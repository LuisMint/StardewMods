using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Netcode;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Storage;
using Pathoschild.Stardew.Common;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>Constructs machine groups.</summary>
internal class MachineGroupFactory
{
    /*********
    ** Fields
    *********/
    /// <summary>The automation factories which construct machines, containers, and connectors.</summary>
    private readonly List<IAutomationFactory> AutomationFactories = [];

    /// <summary>
    /// MOD: added. Cache of reflection access to the base game's private <c>displayItem</c> field
    /// used to read what's shown on a sign, keyed by the sign's actual runtime type (e.g. the
    /// dedicated <c>Sign</c> class, not necessarily the base <c>Object</c> class).
    /// </summary>
    private static readonly Dictionary<Type, System.Reflection.FieldInfo?> SignDisplayItemFieldCache = new();

    /// <summary>MOD: added. Get the reflected <c>displayItem</c> field for a sign's actual runtime type.</summary>
    /// <param name="signType">The sign's actual runtime type (i.e. <c>signObj.GetType()</c>).</param>
    private static System.Reflection.FieldInfo? GetSignDisplayItemField(Type signType)
    {
        if (MachineGroupFactory.SignDisplayItemFieldCache.TryGetValue(signType, out System.Reflection.FieldInfo? cached))
            return cached;

        // MOD: fixed — no DeclaredOnly and no manual hierarchy walk. For instance fields (unlike
        // static ones), Type.GetField without DeclaredOnly already searches the full inheritance
        // chain on its own — this exactly mirrors how the original diagnostic dump found this same
        // field via GetFields() with the same flags.
        System.Reflection.FieldInfo? field = signType.GetField("displayItem", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        MachineGroupFactory.SignDisplayItemFieldCache[signType] = field;
        return field;
    }

    /// <summary>Get the configuration for specific machines by ID, if any.</summary>
    private readonly Func<string, ModConfigMachine?> GetMachineOverride;

    /// <summary>Get the configuration for specific chests by ID, if any.</summary>
    private readonly Func<string, ModConfigStorage?> GetChestOverride;

    /// <summary>Get whether storage containers should be enabled by default if not set via <see cref="GetChestOverride"/>.</summary>
    private readonly Func<bool> GetChestsEnabledByDefault;

    /// <summary>MOD: added. Get the sign item names/IDs that act as a whitelist filter for a touching connector group.</summary>
    private readonly Func<HashSet<string>> GetWhitelistSignNames;

    /// <summary>MOD: added. Get the sign item names/IDs that act as a blacklist filter for a touching connector group.</summary>
    private readonly Func<HashSet<string>> GetBlacklistSignNames;

    /// <summary>MOD: added. Encapsulates the power system, which (if enabled) restricts automation to tiles within range of a power source. See <see cref="PowerSystem"/> for details. Public so callers (e.g. for the overlay) can query powered tiles directly.</summary>
    public PowerSystem PowerSystem { get; }

    /// <summary>Build a storage manager for the given containers.</summary>
    private readonly Func<IContainer[], StorageManager> BuildStorage;

    /// <summary>Encapsulates monitoring and logging.</summary>
    private readonly IMonitor Monitor;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getMachineOverride">Get the configuration for specific machines by ID, if any.</param>
    /// <param name="getChestOverride">Get the configuration for specific chests by ID, if any.</param>
    /// <param name="getChestsEnabledByDefault">Get whether chests should be enabled by default if not set via <see cref="getChestOverride"/>.</param>
    /// <param name="getWhitelistSignNames">MOD: added. Get the sign item names/IDs that act as a whitelist filter for a touching connector group.</param>
    /// <param name="getBlacklistSignNames">MOD: added. Get the sign item names/IDs that act as a blacklist filter for a touching connector group.</param>
    /// <param name="powerSystem">MOD: added. Encapsulates the power system, which (if enabled) restricts automation to tiles within range of a power source.</param>
    /// <param name="buildStorage">Build a storage manager for the given containers.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    public MachineGroupFactory(Func<string, ModConfigMachine?> getMachineOverride, Func<string, ModConfigStorage?> getChestOverride, Func<bool> getChestsEnabledByDefault, Func<HashSet<string>> getWhitelistSignNames, Func<HashSet<string>> getBlacklistSignNames, PowerSystem powerSystem, Func<IContainer[], StorageManager> buildStorage, IMonitor monitor)
    {
        this.GetMachineOverride = getMachineOverride;
        this.GetChestOverride = getChestOverride;
        this.GetChestsEnabledByDefault = getChestsEnabledByDefault;
        this.GetWhitelistSignNames = getWhitelistSignNames; // MOD: added
        this.GetBlacklistSignNames = getBlacklistSignNames; // MOD: added
        this.PowerSystem = powerSystem; // MOD: added
        this.BuildStorage = buildStorage;
        this.Monitor = monitor;
    }

    /// <summary>Add an automation factory.</summary>
    /// <param name="factory">An automation factory which construct machines, containers, and connectors.</param>
    public void Add(IAutomationFactory factory)
    {
        this.AutomationFactories.Add(factory);
    }

    /// <summary>Get the unique key which identifies a location.</summary>
    /// <param name="location">The location instance.</param>
    public string GetLocationKey(GameLocation location)
    {
        return location.uniqueName.Value != null && location.uniqueName.Value != location.Name
            ? $"{location.Name} ({location.uniqueName.Value})"
            : location.Name;
    }

    /// <summary>Sort machines by priority.</summary>
    /// <param name="machines">The machines to sort.</param>
    public IEnumerable<IMachine> SortMachines(IEnumerable<IMachine> machines)
    {
        return
        (
            from machine in machines
            let config = this.GetMachineOverride(machine.MachineTypeID)
            orderby config?.Priority ?? 0 descending
            select machine
        );
    }

    /// <summary>Get all machine groups in a location.</summary>
    /// <param name="location">The location to search.</param>
    /// <param name="monitor">The monitor with which to log errors.</param>
    public IEnumerable<IMachineGroup> GetMachineGroups(GameLocation location, IMonitor monitor)
    {
        LocationFloodFillIndex locationIndex = new(location, monitor);
        HashSet<Vector2>? poweredTiles = this.PowerSystem.GetPoweredTiles(location, locationIndex);
        return this.GetMachineGroups(location, locationIndex, poweredTiles);
    }

    /// <summary>
    /// MOD: added. Same as <see cref="GetMachineGroups(GameLocation,IMonitor)"/>, but for callers
    /// that already have a <see cref="LocationFloodFillIndex"/> and/or the powered-tiles set built
    /// (e.g. <c>MachineManager</c>, which also needs the powered tiles separately for the overlay) —
    /// avoids redundantly re-scanning the whole location for either one.
    ///
    /// MOD: Uses a union-find (disjoint set) approach instead of a plain flood-fill with a "visited"
    /// set, so results don't depend on tile scan order.
    ///
    /// Only connectors ever merge networks together, and only when they're the SAME connector type
    /// (e.g. two Wood Floor tiles) — a Wood Floor tile touching a Stone Floor tile does NOT link
    /// them, so different path materials form separate networks even when physically adjacent.
    ///
    /// Machines and containers (chests) are both excluded from the union step entirely: either one
    /// touching two unrelated connector networks does NOT merge those networks. Instead, it's added
    /// as a member of every network it touches — so a single machine or chest can serve as a shared
    /// point for two independent path-based setups without pooling them into one combined group.
    /// (If two of its groups both try to use it in the same tick, only one actually gets to; the
    /// other just tries again next tick — the same trade-off either way, for a shared chest or a
    /// shared machine.)
    ///
    /// "Touching" includes two cases: being on orthogonally-adjacent tiles, AND sharing the exact
    /// same tile — e.g. a machine or chest placed directly on top of a path tile. Since a path/floor
    /// and a machine/chest are two separate entities that can occupy the identical tile in Stardew
    /// Valley (flooring is a background layer under the object), nodes are deduped by (area,
    /// category) rather than area alone, so a connector and a machine sharing one tile are both kept
    /// instead of one silently overwriting the other.
    ///
    /// MOD: added. If <paramref name="poweredTiles"/> is non-null (power system enabled), only
    /// CONNECTOR tiles need to be within it — machines and containers are collected regardless of
    /// their own tile's power status. A path is what carries power outward from a source, so a
    /// machine or chest can still join an active group through a powered connector touching it even
    /// if its own tile is technically out of range. Something that isn't touching any connector at
    /// all was never going to form an active group anyway (that's true with or without the power
    /// system), so this doesn't change that case.
    /// </summary>
    /// <param name="location">The location to search.</param>
    /// <param name="locationIndex">An already-built indexed view of the location.</param>
    /// <param name="poweredTiles">The already-computed powered tiles (see <see cref="PowerSystem"/>), or <c>null</c> if the power system is disabled.</param>
    public IEnumerable<IMachineGroup> GetMachineGroups(GameLocation location, LocationFloodFillIndex locationIndex, HashSet<Vector2>? poweredTiles)
    {
        static string Categorize(IAutomatable entity) => entity switch
        {
            IMachine => "machine",
            IContainer => "container",
            _ => "connector"
        };

        // step 1: collect every distinct machine/container/connector on the map. Deduped by (tile
        // area, category) instead of just tile area, since a connector (e.g. a path) and a
        // machine/container can legitimately share the exact same tile. For connector nodes, also
        // record an identifying "type key" (e.g. the specific floor type) so different connector
        // materials can be told apart later.
        HashSet<(Rectangle Area, string Category)> seenKeys = new();
        List<IAutomatable> nodes = new();
        List<string?> connectorTypeKeys = new(); // parallel to `nodes`; only meaningful for connector nodes
        List<ConnectorRole> connectorRoles = new(); // MOD: added. parallel to `nodes`; only meaningful for connector nodes

        foreach (Vector2 tile in location.GetTiles())
        {
            foreach (IAutomatable entity in this.GetEntities(location, locationIndex, tile))
            {
                string category = Categorize(entity);

                // MOD: changed — power gating now only applies to CONNECTOR tiles, not machines or
                // containers directly. The path is what carries power outward from a source; a
                // machine or chest can still be part of an active group via a powered connector
                // touching it, even if its own tile is technically outside range. A machine/chest
                // that isn't touching any connector at all (powered or not) still won't form an
                // active group either way — that's already true regardless of the power system,
                // since a fully isolated machine/chest was never "active" to begin with — so this
                // doesn't require any extra work to reach the outcome, just a narrower check.
                if (category == "connector" && poweredTiles != null && !poweredTiles.Contains(tile))
                    continue;

                if (!seenKeys.Add((entity.TileArea, category)))
                    continue;

                nodes.Add(entity);
                connectorTypeKeys.Add(category == "connector" ? this.GetConnectorTypeKey(locationIndex, tile) : null);
                connectorRoles.Add(category == "connector" && entity is Connector connector ? connector.Role : ConnectorRole.Both); // MOD: added
            }
        }

        if (nodes.Count == 0)
            yield break;

        // step 2: map every tile to the node(s) that cover it. A tile can map to more than one node
        // now (e.g. a connector and a machine sharing the same tile).
        Dictionary<Vector2, List<int>> tileToNodeIndices = new();
        for (int i = 0; i < nodes.Count; i++)
        {
            foreach (Vector2 tile in nodes[i].TileArea.GetTiles())
            {
                if (!tileToNodeIndices.TryGetValue(tile, out List<int>? indices))
                    tileToNodeIndices[tile] = indices = new List<int>();
                indices.Add(i);
            }
        }

        bool IsConnector(IAutomatable entity) => entity is not IMachine && entity is not IContainer;

        // MOD: added. Get every other node that "touches" node i — either by being on an
        // orthogonally-adjacent tile, or by sharing the exact same tile (e.g. a machine sitting
        // directly on top of a path).
        IEnumerable<int> GetTouchingNodeIndices(int i)
        {
            foreach (Vector2 tile in nodes[i].TileArea.GetTiles())
            {
                if (tileToNodeIndices.TryGetValue(tile, out List<int>? sameTile))
                {
                    foreach (int j in sameTile)
                    {
                        if (j != i)
                            yield return j;
                    }
                }
            }

            foreach (Vector2 tile in this.GetOrthogonalSurroundingTiles(nodes[i].TileArea))
            {
                if (tileToNodeIndices.TryGetValue(tile, out List<int>? neighbors))
                {
                    foreach (int j in neighbors)
                        yield return j;
                }
            }
        }

        // step 3: union-find setup.
        int[] parent = new int[nodes.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA != rootB)
                parent[rootA] = rootB;
        }

        // step 4: union connectors with each other — ONLY connectors participate here. Machines and
        // containers never merge networks together (see method summary); they're attached to
        // whichever network(s) touch them in steps 5-6 instead. Two connectors only union if they're
        // the same connector type (e.g. two Wood Floor tiles) — different path materials stay
        // separate networks even when the tiles are physically touching.
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!IsConnector(nodes[i]))
                continue;

            foreach (int j in GetTouchingNodeIndices(i))
            {
                if (!IsConnector(nodes[j]))
                    continue;

                if (connectorTypeKeys[i] != connectorTypeKeys[j])
                    continue;

                Union(i, j);
            }
        }

        // step 5: build one MachineGroupBuilder per resulting connector network root.
        Dictionary<int, MachineGroupBuilder> buildersByRoot = new();
        MachineGroupBuilder GetOrCreateBuilder(int root)
        {
            if (!buildersByRoot.TryGetValue(root, out MachineGroupBuilder? builder))
                buildersByRoot[root] = builder = new MachineGroupBuilder(this.GetLocationKey(location), this.SortMachines, this.BuildStorage, this.Monitor);
            return builder;
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            if (IsConnector(nodes[i]))
                GetOrCreateBuilder(Find(i)).Add(nodes[i].TileArea, connectorRoles[i]); // MOD: pass role
        }

        // step 5.5 (MOD: added): detect whitelist/blacklist signs touching each connector network,
        // and resolve one item filter per root. Unlike machines/chests, a sign must be placed
        // DIRECTLY ON TOP of a path tile to count — being merely adjacent isn't enough. An empty
        // sign contributes no marker/filter, but its tile is still tracked as a "candidate" (see
        // MarkSignCandidateTile) so periodic polling knows to watch it in case an item gets placed
        // on it later — otherwise a sign that was empty during the last full scan would be invisible
        // to change-detection entirely until another full scan happens to notice it.
        // Whitelist signs win over blacklist signs if both are accidentally present on one group;
        // multiple signs of the same kind are combined (any item matching ANY of them applies).
        {
            Dictionary<int, List<(Vector2 Tile, bool IsWhitelist, string ItemId)>> signEntriesByRoot = new();

            foreach (Vector2 tile in location.GetTiles())
            {
                // MOD: added — power system gate (also naturally enforced via tileToNodeIndices
                // below, since an unpowered connector tile never makes it into `nodes` in step 1,
                // but skip early here too to avoid a wasted GetSignInfo call).
                if (poweredTiles != null && !poweredTiles.Contains(tile))
                    continue;

                (bool IsWhitelist, string? HeldItemQualifiedId)? signInfo = this.GetSignInfo(locationIndex, tile);
                if (signInfo == null)
                    continue;

                // MOD: restricted to ONLY the sign's own tile — a sign must be placed directly ON
                // TOP of a path tile to count, not just next to it.
                if (!tileToNodeIndices.TryGetValue(tile, out List<int>? indices))
                    continue;

                HashSet<int> touchedRootsForSign = new();
                foreach (int j in indices)
                {
                    if (IsConnector(nodes[j]))
                        touchedRootsForSign.Add(Find(j));
                }

                foreach (int root in touchedRootsForSign)
                {
                    // MOD: added — track this tile as a sign candidate regardless of held item, so
                    // it's watched by periodic polling even while empty.
                    GetOrCreateBuilder(root).MarkSignCandidateTile(tile);

                    // an empty sign contributes no marker/filter beyond the candidate tracking above
                    if (signInfo.Value.HeldItemQualifiedId == null)
                        continue;

                    if (!signEntriesByRoot.TryGetValue(root, out List<(Vector2, bool, string)>? list))
                        signEntriesByRoot[root] = list = new List<(Vector2, bool, string)>();
                    list.Add((tile, signInfo.Value.IsWhitelist, signInfo.Value.HeldItemQualifiedId));
                }
            }

            foreach ((int root, List<(Vector2 Tile, bool IsWhitelist, string ItemId)> entries) in signEntriesByRoot)
            {
                HashSet<string> whitelistItems = entries.Where(e => e.IsWhitelist).Select(e => e.ItemId).ToHashSet();
                HashSet<string> blacklistItems = entries.Where(e => !e.IsWhitelist).Select(e => e.ItemId).ToHashSet();

                bool usingWhitelist = whitelistItems.Count > 0;

                // MOD: changed from Func<ITrackedStack,bool> to Func<string,bool> operating directly
                // on qualified item ID — simpler, and reusable both at the IStorage level
                // (FilteredStorage) and the raw IInventory level (FilteredInventory/ItemFilteredContainer),
                // since some machines bypass IStorage entirely and read a container's Inventory directly.
                Func<string, bool>? filter = null;
                if (usingWhitelist)
                    filter = itemId => whitelistItems.Contains(itemId); // whitelist wins over blacklist
                else if (blacklistItems.Count > 0)
                    filter = itemId => !blacklistItems.Contains(itemId);

                if (filter != null)
                    GetOrCreateBuilder(root).SetItemFilter(filter);

                // MOD: added — only mark tiles whose sign TYPE actually won for this root. A
                // blacklist sign overridden by a whitelist sign elsewhere in the same group is left
                // unmarked entirely, so its tile shows its normal color instead of a stale black
                // highlight that no longer reflects what's actually being enforced.
                foreach ((Vector2 signTile, bool isWhitelistSign, string _) in entries)
                {
                    if (usingWhitelist && !isWhitelistSign)
                        continue; // this blacklist sign lost to a whitelist sign elsewhere in the group — leave unmarked

                    GetOrCreateBuilder(root).MarkSignTile(signTile, isWhitelistSign);
                }
            }
        }

        // step 6: attach each machine or container to every distinct connector network it touches,
        // without merging those networks together. A machine/container touching no connector at all
        // becomes its own solo (non-automated) group, same as before.
        //
        // MOD: note this means a single machine (or chest) CAN now belong to more than one group at
        // once. In practice, if two of its groups both try to use it in the same game tick, only one
        // will actually get to (the other just finds it already busy/empty that tick, and tries
        // again next tick) — same trade-off as a shared chest, just applied to machines too.
        List<MachineGroupBuilder> soloBuilders = new();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (IsConnector(nodes[i]))
                continue;

            bool isEnabledMachine = nodes[i] is IMachine machine1 && this.GetMachineOverride(machine1.MachineTypeID)?.Enabled != false;
            bool isEnabledContainer =
                nodes[i] is IContainer container1
                && (container1.StorageAllowed() || container1.TakingItemsAllowed())
                && (this.GetChestOverride(container1.TypeId)?.Enabled ?? this.GetChestsEnabledByDefault());

            if (!isEnabledMachine && !isEnabledContainer)
                continue;

            HashSet<int> touchedRoots = new();
            Dictionary<int, ConnectorRole> roleByRoot = new(); // MOD: added
            foreach (int j in GetTouchingNodeIndices(i))
            {
                if (IsConnector(nodes[j]))
                {
                    int root = Find(j);
                    touchedRoots.Add(root);
                    roleByRoot.TryAdd(root, connectorRoles[j]); // MOD: added — all connectors merged into one root always share the same role, since role (like type) is a pure function of connector material
                }
            }

            if (touchedRoots.Count == 0)
            {
                MachineGroupBuilder solo = new(this.GetLocationKey(location), this.SortMachines, this.BuildStorage, this.Monitor);
                this.AddToBuilder(solo, nodes[i], ConnectorRole.Both);
                soloBuilders.Add(solo);
            }
            else
            {
                foreach (int root in touchedRoots)
                    this.AddToBuilder(GetOrCreateBuilder(root), nodes[i], roleByRoot.GetValueOrDefault(root, ConnectorRole.Both)); // MOD: added role argument
            }
        }

        foreach (MachineGroupBuilder builder in buildersByRoot.Values)
        {
            if (builder.HasTiles())
                yield return builder.Build();
        }

        foreach (MachineGroupBuilder solo in soloBuilders)
        {
            if (solo.HasTiles())
                yield return solo.Build();
        }
    }

    /// <summary>MOD: added. Add a machine or container to a group builder, applying the same enabled/override checks used elsewhere. If the connector role isn't <see cref="ConnectorRole.Both"/> and the entity is a container, it's wrapped with <see cref="RoleRestrictedContainer"/> so the restriction only applies to this specific group, not the chest's real settings.</summary>
    /// <param name="builder">The builder to add to.</param>
    /// <param name="entity">The machine or container to add.</param>
    /// <param name="role">The connector role this entity was reached through (only meaningful for containers).</param>
    private void AddToBuilder(MachineGroupBuilder builder, IAutomatable entity, ConnectorRole role)
    {
        switch (entity)
        {
            case IMachine machine:
                if (this.GetMachineOverride(machine.MachineTypeID)?.Enabled != false)
                    builder.Add(machine);
                break;

            case IContainer container:
                if (container.StorageAllowed() || container.TakingItemsAllowed())
                {
                    bool enabled = this.GetChestOverride(container.TypeId)?.Enabled ?? this.GetChestsEnabledByDefault();
                    if (enabled)
                    {
                        IContainer toAdd = role == ConnectorRole.Both
                            ? container
                            : new RoleRestrictedContainer(container, role);
                        builder.Add(toAdd);
                    }
                }
                break;
        }
    }


    /// <summary>Get a machine, container, or connector from the given entity, if any.</summary>
    /// <param name="location">The location to check.</param>
    /// <param name="tile">The tile to check.</param>
    /// <param name="entity">The entity to check.</param>
    public IAutomatable? GetEntityFor(GameLocation location, Vector2 tile, object entity)
    {
        return entity switch
        {
            SObject obj => this.GetEntityFor(location, tile, obj),
            TerrainFeature feature => this.GetEntityFor(location, tile, feature),
            Building building => this.GetEntityFor(location, tile, building),
            _ => null
        };
    }

    /// <summary>Get the registered automation factories.</summary>
    public IEnumerable<IAutomationFactory> GetFactories()
    {
        return this.AutomationFactories.Select(p => p);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// MOD: added. Get the tiles directly orthogonally adjacent to a tile area (up/down/left/right
    /// edges only) — unlike the shared <c>Rectangle.GetSurroundingTiles()</c> helper, this
    /// deliberately excludes the four diagonal corner tiles, so a connector (or anything else)
    /// touching only a corner doesn't count as "connected."
    /// </summary>
    /// <param name="area">The tile area to check.</param>
    private IEnumerable<Vector2> GetOrthogonalSurroundingTiles(Rectangle area)
    {
        // top and bottom edges (excludes corners)
        for (int x = area.X; x < area.X + area.Width; x++)
        {
            yield return new Vector2(x, area.Y - 1);
            yield return new Vector2(x, area.Y + area.Height);
        }

        // left and right edges (excludes corners)
        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            yield return new Vector2(area.X - 1, y);
            yield return new Vector2(area.X + area.Width, y);
        }
    }

    /// <summary>
    /// MOD: added. Get an identifying key for the specific type of connector on a tile — e.g.
    /// distinguishing a Wood Floor path from a Stone Floor path — so different connector materials
    /// can be treated as separate networks even when they're physically touching. Returns
    /// <c>null</c> if nothing recognizable is found on the tile.
    /// </summary>
    /// <param name="locationIndex">An indexed view of the location.</param>
    /// <param name="tile">The tile to check.</param>
    private string? GetConnectorTypeKey(LocationFloodFillIndex locationIndex, Vector2 tile)
    {
        object[] targets = locationIndex.GetEntities(tile).ToArray();

        // MOD: fixed — check for Flooring FIRST across all targets on the tile, regardless of
        // enumeration order. A connector's type should always be based on the FLOOR/path type,
        // even when a machine or chest also occupies the same tile. Previously this returned on
        // whichever target it matched first, which could grab the machine's item ID instead of
        // the floor type if the machine happened to be listed before the flooring for that tile —
        // silently making that one tile register as a "different" connector type than its
        // neighbors, and breaking the same-type-matching union check for it.
        foreach (object target in targets)
        {
            if (target is Flooring floor)
            {
                // MOD: fixed — key on the floor's underlying ITEM identity (shared by every
                // Data/FloorsAndPaths entry for the same craftable), not the specific entry ID in
                // `whichFloor.Value`. Guards against any connector whose Data/FloorsAndPaths
                // entries share one ItemId (e.g. an earlier version of the power pipe swapped
                // between a base and "powered" entry this way) — keying on `whichFloor.Value`
                // would treat those as different connector types, so adjacent tiles of one
                // continuous path could silently fail to union just because they happened to
                // currently point at different entries, fragmenting the network into isolated
                // tiles. Falls back to the entry ID itself if the item can't be resolved, matching
                // the old behavior for that edge case.
                string? itemId = floor.GetData()?.ItemId;
                return itemId != null ? $"floor-item:{itemId}" : $"floor:{floor.whichFloor.Value}";
            }
        }

        foreach (object target in targets)
        {
            switch (target)
            {
                case SObject obj:
                    return $"object:{obj.QualifiedItemId}";

                case Building building:
                    return $"building:{building.buildingType.Value}";
            }
        }

        return null;
    }

    /// <summary>
    /// MOD: added. Get sign detection info for a tile, if a configured whitelist/blacklist sign is
    /// there — regardless of whether it currently has an item displayed on it. Returns <c>null</c>
    /// if there's no such sign on the tile at all. <c>HeldItemQualifiedId</c> is <c>null</c> if the
    /// sign is empty (nothing displayed on it); callers should treat that case as "detected but not
    /// currently filtering anything" rather than "not detected."
    /// </summary>
    /// <param name="locationIndex">An indexed view of the location.</param>
    /// <param name="tile">The tile to check.</param>
    private (bool IsWhitelist, string? HeldItemQualifiedId)? GetSignInfo(LocationFloodFillIndex locationIndex, Vector2 tile)
    {
        HashSet<string> whitelistSigns = this.GetWhitelistSignNames();
        HashSet<string> blacklistSigns = this.GetBlacklistSignNames();

        if (whitelistSigns.Count == 0 && blacklistSigns.Count == 0)
            return null; // feature not configured at all — skip the scan entirely

        foreach (object target in locationIndex.GetEntities(tile))
        {
            if (target is not SObject signObj)
                continue;

            bool isWhitelist = whitelistSigns.Contains(signObj.QualifiedItemId) || whitelistSigns.Contains(signObj.Name);
            bool isBlacklist = !isWhitelist && (blacklistSigns.Contains(signObj.QualifiedItemId) || blacklistSigns.Contains(signObj.Name));

            if (!isWhitelist && !isBlacklist)
                continue;

            // The item displayed on a sign is read via the private `displayItem` field (not
            // `heldObject`, which is unrelated for signs). It's a NetRef<Item> whose `Value` property
            // holds the actual displayed item, or null if the sign is empty.
            SObject? heldItem = null;
            System.Reflection.FieldInfo? displayItemField = MachineGroupFactory.GetSignDisplayItemField(signObj.GetType());
            if (displayItemField?.GetValue(signObj) is NetRef<StardewValley.Item> displayItemRef)
                heldItem = displayItemRef.Value as SObject;

            return (isWhitelist, heldItem?.QualifiedItemId);
        }

        return null;
    }

    /// <summary>
    /// MOD: added. Get the qualified item ID currently displayed on a configured whitelist/blacklist
    /// sign at a specific tile, if any — a cheap, direct lookup (no full location scan) meant for
    /// periodic polling to detect when a sign's content changes. Returns <c>null</c> if there's no
    /// matching sign there, or if it's empty. This exists because changing what's displayed on a
    /// sign doesn't trigger any of the normal "something changed in the world" events — the sign
    /// object itself is never added or removed, just one of its internal fields — so without this,
    /// there'd be no way to detect the change without an unrelated nearby world change forcing a
    /// rescan.
    /// </summary>
    /// <param name="location">The location to check.</param>
    /// <param name="tile">The tile to check.</param>
    public string? GetCurrentSignItemId(GameLocation location, Vector2 tile)
    {
        HashSet<string> whitelistSigns = this.GetWhitelistSignNames();
        HashSet<string> blacklistSigns = this.GetBlacklistSignNames();

        if (whitelistSigns.Count == 0 && blacklistSigns.Count == 0)
            return null;

        // MOD: fixed — check both netObjects AND overlayObjects, matching the two sources
        // LocationFloodFillIndex.Scan() checks (confirmed working, since full rescans reliably find
        // signs). Checking only netObjects meant this could silently miss a sign living in
        // overlayObjects instead, always reporting "no sign here" with no error.
        SObject? signObj = null;
        if (location.netObjects.TryGetValue(tile, out SObject? netObj) && netObj != null)
            signObj = netObj;
        else if (location.overlayObjects.TryGetValue(tile, out SObject? overlayObj) && overlayObj != null)
            signObj = overlayObj;

        if (signObj == null)
            return null;

        bool isWhitelist = whitelistSigns.Contains(signObj.QualifiedItemId) || whitelistSigns.Contains(signObj.Name);
        bool isBlacklist = !isWhitelist && (blacklistSigns.Contains(signObj.QualifiedItemId) || blacklistSigns.Contains(signObj.Name));
        if (!isWhitelist && !isBlacklist)
            return null;

        System.Reflection.FieldInfo? displayItemField = MachineGroupFactory.GetSignDisplayItemField(signObj.GetType());
        if (displayItemField?.GetValue(signObj) is NetRef<StardewValley.Item> displayItemRef)
            return (displayItemRef.Value as SObject)?.QualifiedItemId;

        return null;
    }

    /// <summary>Get the machines, containers, or connectors on the given tile, if any.</summary>
    /// <param name="location">The location to search.</param>
    /// <param name="locationIndex">An indexed view of the location.</param>
    /// <param name="tile">The tile to search.</param>
    private IEnumerable<IAutomatable> GetEntities(GameLocation location, LocationFloodFillIndex locationIndex, Vector2 tile)
    {
        // from entities
        foreach (object target in locationIndex.GetEntities(tile))
        {
            switch (target)
            {
                case SObject obj:
                    {
                        IAutomatable? entity = this.GetEntityFor(location, tile, obj);

                        // MOD: added — SignPlaceholder exists purely so ModEntry's placement-change
                        // tracking notices signs; it's not a real machine/container/connector, so
                        // skip it here. Actual sign detection happens separately via GetSignInfo.
                        if (entity != null && entity is not SignPlaceholder)
                            yield return entity;

                        if (obj is IndoorPot pot && pot.bush.Value != null)
                        {
                            entity = this.GetEntityFor(location, tile, pot.bush.Value);
                            if (entity != null)
                                yield return entity;
                        }
                    }
                    break;

                case TerrainFeature feature:
                    {
                        IAutomatable? entity = this.GetEntityFor(location, tile, feature);
                        if (entity != null)
                            yield return entity;
                    }
                    break;

                case Building building:
                    {
                        IAutomatable? entity = this.GetEntityFor(location, tile, building);
                        if (entity != null)
                            yield return entity;
                    }
                    break;
            }
        }

        // from tile position
        foreach (IAutomationFactory factory in this.AutomationFactories)
        {
            if (this.TryGetEntityWithErrorHandling(location, tile, null, factory, p => p.GetForTile(location, tile), out IAutomatable? entity))
                yield return entity;
        }
    }

    /// <summary>Get a machine, container, or connector from the given object, if any.</summary>
    /// <param name="location">The location to search.</param>
    /// <param name="tile">The tile to search.</param>
    /// <param name="obj">The object to check.</param>
    private IAutomatable? GetEntityFor(GameLocation location, Vector2 tile, SObject obj)
    {
        foreach (IAutomationFactory factory in this.AutomationFactories)
        {
            if (this.TryGetEntityWithErrorHandling(location, tile, obj, factory, p => p.GetFor(obj, location, tile), out IAutomatable? entity))
                return entity;
        }

        return null;
    }

    /// <summary>Get a machine, container, or connector from the given terrain feature, if any.</summary>
    /// <param name="location">The location to search.</param>
    /// <param name="tile">The tile to search.</param>
    /// <param name="feature">The terrain feature to check.</param>
    private IAutomatable? GetEntityFor(GameLocation location, Vector2 tile, TerrainFeature feature)
    {
        foreach (IAutomationFactory factory in this.AutomationFactories)
        {
            if (this.TryGetEntityWithErrorHandling(location, tile, factory, factory, p => p.GetFor(feature, location, tile), out IAutomatable? entity))
                return entity;
        }

        return null;
    }

    /// <summary>Get a machine, container, or connector from the given building, if any.</summary>
    /// <param name="location">The location to search.</param>
    /// <param name="tile">The tile to search.</param>
    /// <param name="building">The building to check.</param>
    private IAutomatable? GetEntityFor(GameLocation location, Vector2 tile, Building building)
    {
        foreach (IAutomationFactory factory in this.AutomationFactories)
        {
            if (this.TryGetEntityWithErrorHandling(location, tile, factory, factory, p => p.GetFor(building, location, tile), out IAutomatable? entity))
                return entity;
        }

        return null;
    }

    /// <summary>Try to get a machine, container, or connector from an automation factory with error handling.</summary>
    /// <param name="location">The location being searched.</param>
    /// <param name="tile">The tile position being searched.</param>
    /// <param name="fromEntity">The in-game entity being checked, or <c>null</c> if we're checking the title.</param>
    /// <param name="factory">The automation factory being searched.s</param>
    /// <param name="get">Get the result from the automation factory.</param>
    /// <param name="entity">The result from the automation factory, or <c>null</c> if none was found.</param>
    /// <returns>Returns whether an <paramref name="entity"/> was successfully found.</returns>
    private bool TryGetEntityWithErrorHandling(GameLocation location, Vector2 tile, object? fromEntity, IAutomationFactory factory, Func<IAutomationFactory, IAutomatable?> get, [NotNullWhen(true)] out IAutomatable? entity)
    {
        try
        {
            entity = get(factory);
            return entity != null;
        }
        catch (Exception ex)
        {
            StringBuilder error = new StringBuilder();

            if (factory.GetType() == typeof(AutomationFactory))
                error.Append("Failed");
            else
                error.Append("Custom automation factory [").Append(factory.GetType().FullName).Append("] failed");

            error
                .Append(" getting machine for location [")
                .Append(location?.NameOrUniqueName ?? location?.GetType().FullName ?? "null location")
                .Append("] and tile (")
                .Append(tile.X)
                .Append(", ")
                .Append(tile.Y)
                .Append(")");

            if (fromEntity != null)
            {
                switch (fromEntity)
                {
                    case Building building:
                        error
                            .Append(" and building [")
                            .Append(building.buildingType.Value)
                            .Append(']');
                        break;

                    case SObject obj:
                        error
                            .Append(" and object [")
                            .Append(obj.QualifiedItemId)
                            .Append("] (\"")
                            .Append(obj.DisplayName)
                            .Append("\")");
                        break;

                    case Tree tree:
                        error
                            .Append(" and tree [")
                            .Append(tree.treeType.Value)
                            .Append(']');
                        break;

                    case FruitTree tree:
                        error
                            .Append(" and fruit tree [")
                            .Append(tree.treeId.Value)
                            .Append(']');
                        break;

                    case TerrainFeature feature:
                        error
                            .Append(" and terrain feature type [")
                            .Append(feature.GetType().FullName)
                            .Append(']');
                        break;

                    default:
                        error
                            .Append(" and entity type [")
                            .Append(fromEntity.GetType().FullName)
                            .Append(']');
                        break;
                }
            }

            error
                .AppendLine(". Technical details:")
                .Append(ex);

            this.Monitor.Log(error.ToString(), LogLevel.Error);
        }

        entity = null;
        return false;
    }
}

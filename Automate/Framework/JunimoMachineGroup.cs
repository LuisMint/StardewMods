using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.Utilities;
using StardewModdingAPI;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>An aggregate collection of machine groups linked by Junimo chests.</summary>
internal class JunimoMachineGroup : MachineGroup
{
    /*********
    ** Fields
    *********/
    /// <summary>Sort machines by priority.</summary>
    private readonly Func<IEnumerable<IMachine>, IEnumerable<IMachine>> SortMachines;

    /// <summary>The underlying machine groups.</summary>
    private readonly List<IMachineGroup> MachineGroups = [];

    /// <summary>A map of covered tiles by location key, if loaded.</summary>
    private Dictionary<string, IReadOnlySet<Vector2>>? Tiles;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public override bool HasInternalAutomation => this.Machines.Length > 0;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="sortMachines">Sort machines by priority.</param>
    /// <param name="buildStorage">Build a storage manager for the given containers.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    public JunimoMachineGroup(Func<IEnumerable<IMachine>, IEnumerable<IMachine>> sortMachines, Func<IContainer[], StorageManager> buildStorage, IMonitor monitor)
        : base(
            locationKey: null,
            machines: [],
            containers: [],
            tiles: [],
            buildStorage: buildStorage,
            monitor: monitor
        )
    {
        this.IsJunimoGroup = true;
        this.SortMachines = sortMachines;
    }

    /// <summary>Get the underlying machine groups.</summary>
    public IEnumerable<IMachineGroup> GetAll()
    {
        return this.MachineGroups;
    }

    /// <summary>Add machine groups to the collection.</summary>
    /// <param name="groups">The groups to add.</param>
    /// <remarks>Make sure to call <see cref="Rebuild"/> after making changes.</remarks>
    public void Add(IList<IMachineGroup> groups)
    {
        this.MachineGroups.AddRange(groups);
    }

    /// <summary>Remove all machine groups in the collection.</summary>
    public void Clear()
    {
        this.MachineGroups.Clear();

        this.StorageManager.SetContainers([]);

        this.Containers = [];
        this.Machines = [];
        this.Tiles = null;
    }

    /// <summary>Remove all machine groups within the given locations.</summary>
    /// <param name="locationKeys">The location keys as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>.</param>
    public bool RemoveLocations(ISet<string> locationKeys)
    {
        return this.MachineGroups.RemoveAll(
            group => locationKeys.Contains(group.LocationKey!)
        ) > 0;
    }

    /// <summary>Rebuild the aggregate group for changes to the underlying machine groups.</summary>
    public void Rebuild()
    {
        // MOD: changed from the base GetUniqueContainers to a role-aware dedup — see
        // GetUniqueJunimoContainers' own remarks for why plain identity-based dedup silently broke
        // per-touchpoint connector roles for a shared Junimo inventory.
        this.Containers = this.GetUniqueJunimoContainers();
        this.Machines = this.SortMachines(this.MachineGroups.SelectMany(p => p.Machines)).ToArray();
        this.Tiles = null;

        this.StorageManager.SetContainers(this.Containers);
    }

    /// <inheritdoc />
    public override IReadOnlySet<Vector2> GetTiles(string locationKey)
    {
        this.Tiles ??= this.BuildTileMap();

        return this.Tiles.TryGetValue(locationKey, out IReadOnlySet<Vector2>? tiles)
            ? tiles
            : ImmutableHashSet<Vector2>.Empty;
    }

    /// <summary>Get whether the tile area intersects this machine group.</summary>
    /// <param name="locationKey">The location key as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>.</param>
    /// <param name="tileArea">The tile area to check.</param>
    /// <remarks>This is the Junimo Chest equivalent of <see cref="MachineDataForLocation.IntersectsAutomatedGroup"/>.</remarks>
    public bool IntersectsAutomatedGroup(string locationKey, Rectangle tileArea)
    {
        IReadOnlySet<Vector2> tiles = this.GetTiles(locationKey);
        if (tiles.Count == 0)
            return false;

        foreach (Vector2 tile in tileArea.GetTiles())
        {
            if (tiles.Contains(tile))
                return true;
        }

        return false;
    }

    /// <summary>Get whether a tile area contains or is adjacent to a tracked automateable.</summary>
    /// <param name="locationKey">The location key as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>.</param>
    /// <param name="tileArea">The tile area to check.</param>
    /// <remarks>This is the Junimo Chest equivalent of <see cref="MachineDataForLocation.ContainsOrAdjacent"/>.</remarks>
    public bool ContainsOrAdjacent(string locationKey, Rectangle tileArea)
    {
        IReadOnlySet<Vector2> tiles = this.GetTiles(locationKey);
        if (tiles.Count == 0)
            return false;

        foreach (Vector2 tile in tileArea.GetSurroundingTiles())
        {
            if (tiles.Contains(tile))
                return true;
        }

        return false;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Build a map of covered tiles by location key.</summary>
    private Dictionary<string, IReadOnlySet<Vector2>> BuildTileMap()
    {
        Dictionary<string, IReadOnlySet<Vector2>> tiles = [];

        foreach (IGrouping<string?, IMachineGroup> groupByLocation in this.MachineGroups.GroupBy(p => p.LocationKey))
        {
            string? locationKey = groupByLocation.Key;
            if (locationKey is null)
                continue; // ???

            tiles[locationKey] = new HashSet<Vector2>(groupByLocation.SelectMany(p => p.GetTiles(locationKey)));
        }

        return tiles;
    }

    /// <summary>
    /// MOD: added. Dedupe containers sharing the same underlying inventory (as every Junimo chest on
    /// the farm does, since they all read/write ONE shared inventory) — but unlike the base
    /// <see cref="MachineGroup.GetUniqueContainers"/>, keep a separate entry for each DISTINCT
    /// connector role a touchpoint was reached through, instead of collapsing them all down to
    /// whichever one happened to be enumerated first.
    ///
    /// A shared Junimo inventory can legitimately be reached through several different LOCAL pipes
    /// across different locations/groups (e.g. one pull-only in one location, a different push-only
    /// in another) — plain identity-based dedup silently discarded all but one of those roles,
    /// breaking whichever touchpoints didn't "win." Since every surviving entry still ultimately
    /// reads/writes the exact same real inventory, this doesn't risk double-counting ITEMS — but it's
    /// a deliberate, accepted trade-off that a machine's ingredient-sufficiency check (which sums
    /// quantities across containers without deduping by identity — see <see cref="StackAccumulator"/>)
    /// could double-count that item type if the same Junimo inventory is ALSO reached unrestricted
    /// (Both) at the same time as a restricted touchpoint elsewhere. Deemed narrow enough to accept in
    /// exchange for each local touchpoint's own role actually working as configured.
    ///
    /// Each surviving Junimo-chest entry is also wrapped in <see cref="JunimoTouchpointContainer"/>,
    /// tagged with its origin sub-group's tiles — otherwise, once several touchpoints for the same
    /// shared inventory coexist here, a Powered Chest anywhere in the aggregate could "borrow" a role
    /// that actually belongs to a completely different Powered Chest's own local connection (see that
    /// class's own remarks for a concrete example). Iterates per-group (rather than one flattened
    /// <c>SelectMany</c>) specifically so each container can be tagged with the group it came from.
    /// </summary>
    private IContainer[] GetUniqueJunimoContainers()
    {
        Dictionary<object, HashSet<(bool AllowStorage, bool AllowTaking)>> seenRolesByInventory = new(new ObjectReferenceComparer<object>());
        List<IContainer> result = [];

        foreach (IMachineGroup group in this.MachineGroups)
        {
            if (group.LocationKey is null)
                continue;

            IReadOnlySet<Vector2> groupTiles = group.GetTiles(group.LocationKey);

            foreach (IContainer container in group.Containers)
            {
                bool allowStorage = true;
                bool allowTaking = true;
                if (container is IConnectionRoleRestriction restriction)
                {
                    allowStorage = restriction.AllowStorageThroughThisConnection;
                    allowTaking = restriction.AllowTakingThroughThisConnection;
                }

                if (!seenRolesByInventory.TryGetValue(container.InventoryReferenceId, out HashSet<(bool, bool)>? seenRoles))
                    seenRolesByInventory[container.InventoryReferenceId] = seenRoles = [];

                if (!seenRoles.Add((allowStorage, allowTaking)))
                    continue; // exact duplicate (same inventory, same role) — an equivalent entry already survived

                // MOD: only Junimo chests need origin tagging — a regular (non-shared) container that
                // got swept into this aggregate because it shares a local group with a Junimo chest
                // has a unique InventoryReferenceId of its own, so there's no cross-group ambiguity to
                // resolve for it.
                result.Add(container.IsJunimoChest
                    ? new JunimoTouchpointContainer(container, groupTiles)
                    : container);
            }
        }

        return [.. result];
    }
}

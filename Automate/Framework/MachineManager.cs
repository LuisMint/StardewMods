using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Storage;
using Pathoschild.Stardew.Common;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Extensions;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>Manages machine groups.</summary>
internal class MachineManager
{
    /*********
    ** Fields
    *********/
    /// <summary>Encapsulates monitoring and logging.</summary>
    private readonly IMonitor Monitor;

    /// <summary>The mod configuration.</summary>
    private readonly Func<ModConfig> Config;

    /// <summary>The internal mod data.</summary>
    private readonly DataModel Data;

    /// <summary>The machine data for each location.</summary>
    private readonly Dictionary<string, MachineDataForLocation> MachineData = [];

    /// <summary>The cached machines to process.</summary>
    private IMachineGroup[] ActiveMachineGroups = [];

    /// <summary>The cached disabled machine groups (e.g. machines not connected to a chest).</summary>
    private IMachineGroup[] DisabledMachineGroups = [];

    /// <summary>The locations that should be removed on the next update tick.</summary>
    private readonly HashSet<GameLocation> RemoveQueue = new(new GameLocationNameComparer());

    /// <summary>The locations that should be reloaded on the next update tick.</summary>
    private readonly HashSet<GameLocation> ReloadQueue = new(new GameLocationNameComparer());

    /// <summary>
    /// MOD: added. The total power silo capacity (see <see cref="PowerSiloSystem.GetTotalCapacity"/>)
    /// as of the last <see cref="ReloadMachinesIn"/> call — cheap to compute (no coil scan), so it's
    /// checked every pass purely to detect a change (a Silo built, destroyed, or fed to a new tier).
    /// When it changes, <see cref="PowerSiloSystem.RefreshCoilAllowance"/> (the expensive coil scan) is
    /// triggered, and every location is queued for reload too — not just the one(s) already being
    /// reloaded this pass — since the cap is global and can affect coils in locations that otherwise
    /// have nothing else prompting a rescan.
    /// </summary>
    private int? PreviousTotalCapacity;

    /// <summary>MOD: added. Whether <see cref="PreviousTotalCapacity"/> changed during the last <see cref="ReloadMachinesIn"/> call — checked by <see cref="ReloadQueuedLocations"/> after it clears the reload queue, so every location can be queued for the NEXT pass without the newly-queued entries being immediately wiped by that same clear.</summary>
    private bool PowerSiloCapacityChangedLastPass;

    /// <summary>
    /// MOD: added. The last-known displayed item ID and numeric condition for each tracked
    /// whitelist/blacklist sign, keyed by (location key, tile). Used to detect when a sign's content
    /// (or just its numeric condition) changes so the location can be rescanned automatically,
    /// without needing an unrelated nearby world change to force it.
    /// </summary>
    private readonly Dictionary<(string LocationKey, Vector2 Tile), (string ItemId, int? Number)?> LastKnownSignItems = new();

    /// <summary>MOD: added. How many ticks to wait between each check for sign content changes — doesn't need to be as frequent as automation itself, since it's just a convenience so players don't need to nudge the world to force a rescan.</summary>
    private const int SignCheckIntervalTicks = 30;

    /// <summary>MOD: added. Ticks elapsed since the last sign-change check.</summary>
    private int TicksSinceSignCheck;

    /// <summary>
    /// MOD: added. A cached lookup of the current <see cref="GameLocation"/> instance for each
    /// location key that's been scanned, populated whenever a location is reloaded. Used by
    /// <see cref="CheckForSignChanges"/> so it doesn't need to search every location in the save on
    /// each periodic check — just an O(1) dictionary lookup for locations known to have signs.
    /// </summary>
    private readonly Dictionary<string, GameLocation> LocationsByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MOD: added. The tiles of every machine/container that was part of an active automation group as of the last rebuild, keyed by location key — used to detect newly-joined machines/containers so a "just connected" sparkle effect can be shown exactly once per join, not on every rebuild.</summary>
    private readonly Dictionary<string, HashSet<Vector2>> PreviouslyActiveEntityTilesByLocation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MOD: added. The full tile footprint (entities + connectors) of every active automation group as of the last rebuild, keyed by location key — used to detect a group that stops being valid ENTIRELY (as opposed to just losing one member), so a "group broken" sound can be played exactly once for that event. See its use for how "the same group" is tracked across rebuilds despite <see cref="IMachineGroup"/> instances having no stable identity.</summary>
    private readonly Dictionary<string, List<HashSet<Vector2>>> PreviouslyActiveGroupTileSetsByLocation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. Groups that gained at least one new machine/container member since the last rebuild —
    /// reuses the exact same newly-joined detection the join-sparkle effect uses (see
    /// <see cref="PreviouslyActiveEntityTilesByLocation"/>'s own remarks), queued here for
    /// <see cref="TakeGroupsWithNewMembers"/> to drain. In event-based mode, nothing else notices a
    /// membership change on its own: a container coming into range doesn't fire <c>ChestInventoryChanged</c>
    /// (nothing was stored/removed, it just became reachable), and a machine joining isn't itself
    /// "becoming ready." Without this, a newly-connected chest/machine just sat there doing nothing until
    /// the player happened to open/edit some unrelated chest, or the periodic backstop scan eventually
    /// caught it — reported directly by a user.
    /// </summary>
    private readonly List<IMachineGroup> GroupsWithNewMembers = new();

    /// <summary>
    /// MOD: added. Every locally-active <see cref="IMachineGroup"/> that came out of a location rescan
    /// (see <see cref="ReloadMachinesIn"/>'s "add new groups" step) — unlike <see cref="GroupsWithNewMembers"/>,
    /// this fires for EVERY group the rescan produced, not just ones that gained a genuinely new tile.
    /// Accumulated here and drained by <see cref="TakeRescannedGroups"/>.
    ///
    /// This exists because a group can be rebuilt (a new <see cref="IMachineGroup"/> instance, per
    /// <see cref="IMachineGroup"/>'s own lack of stable identity across rebuilds) for reasons that
    /// <see cref="GroupsWithNewMembers"/> deliberately does NOT count as "joining" — most importantly,
    /// a group that just SHRANK (a member was removed/broken). <see cref="GroupsWithNewMembers"/> only
    /// looks for tiles that are new compared to the previous scan, so a group that lost a member has zero
    /// new tiles and is silently skipped. Without a broader signal, that freshly rebuilt (smaller) group's
    /// queue starts out completely empty and unarmed — none of event-based mode's other triggers (a
    /// machine "becoming ready," a chest's contents changing) fire on their own just because a rescan
    /// happened, so the group would sit doing nothing until some unrelated coincidental event happened to
    /// touch it, or the periodic backstop scan eventually reached it. Confirmed directly via diagnostic
    /// logging: after removing a furnace from a group, the remaining furnaces stopped automating entirely
    /// until an unrelated chest elsewhere in the same location happened to change.
    /// </summary>
    private readonly List<IMachineGroup> RescannedGroups = new();

    /// <summary>
    /// MOD: added. Every <see cref="IMachineGroup"/> instance discarded by the last <see cref="ReloadMachinesIn"/>
    /// call (because its location was rescanned and replaced with brand-new group instances — see the
    /// "remove old groups" step below), accumulated here and drained by <see cref="TakeRemovedGroups"/>.
    /// <see cref="IMachineGroup"/> has no stable identity across a rebuild, so anything outside this class
    /// that keys its own state by a specific <see cref="IMachineGroup"/> reference (namely <c>ModEntry</c>'s
    /// per-group action-pacing queues) needs to know exactly when one of its keys just became orphaned —
    /// otherwise that old group's own pending pacing timer keeps firing forever, completely independently
    /// of (and in parallel with) the new group covering the same physical machines. That was a real,
    /// confirmed bug: diagnostic logging showed the SAME physical machine cluster being driven by several
    /// "zombie" groups at once, each committing on its own schedule, which looked exactly like "the group
    /// takes multiple actions at once" even though each individual group's own pacing was completely
    /// correct in isolation.
    /// </summary>
    private readonly List<IMachineGroup> RemovedGroups = new();

    /// <summary>
    /// MOD: added. Every currently-known machine, keyed by (location key, tile) — lets a Harmony patch
    /// that only has a raw game object (e.g. <see cref="Patches.MachineReadyPatches"/>) look up exactly
    /// which machine/group it belongs to, without scanning every machine in every group. Deliberately
    /// keyed by position rather than by the machine's own underlying entity reference, since every
    /// <see cref="IMachine"/> — including third-party ones — already has to provide <c>Location</c>/
    /// <c>TileArea</c> via <see cref="IAutomatable"/>, so this covers every machine type automatically
    /// with no interface changes needed anywhere.
    /// </summary>
    private readonly Dictionary<(string LocationKey, Vector2 Tile), (IMachineGroup Group, IMachine Machine)> MachineByLocationAndTile = new();

    /// <summary>MOD: added. Every tile key currently indexed in <see cref="MachineByLocationAndTile"/> for a given location key — used to evict exactly that location's entries when its groups are reloaded/removed, the same way <see cref="LastKnownSignItems"/> etc. are evicted.</summary>
    private readonly Dictionary<string, List<Vector2>> MachineTileKeysByLocation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MOD: added. The join sparkle's tint for a tile whose connection is pull-only (<see cref="ConnectorRole.ChestInputOnly"/> — the chest acts as a source, items are taken FROM it).</summary>
    private static readonly Color PullConnectionSparkleColor = Color.Red;

    /// <summary>MOD: added. The join sparkle's tint for a tile whose connection is push-only (<see cref="ConnectorRole.ChestOutputOnly"/> — the chest acts as a destination, items are stored INTO it).</summary>
    private static readonly Color PushConnectionSparkleColor = Color.CornflowerBlue;

    /// <summary>MOD: added. The join sparkle's tint for a tile whose connection allows both taking and storing (<see cref="ConnectorRole.Both"/>).</summary>
    private static readonly Color BothConnectionSparkleColor = Color.MediumSeaGreen;

    /// <summary>MOD: added. The join sparkle's tint for a tile where the connection kind isn't a single clear answer — e.g. a newly-joined container that's now part of two or more groups with different connection kinds, or a solo group with no connector at all.</summary>
    private static readonly Color AmbiguousConnectionSparkleColor = Color.White;

    /// <summary>MOD: added. The per-frame duration for the "just connected" join sparkle, in milliseconds — vanilla's own geode-reveal sparkle uses 100ms; this is ~15% faster.</summary>
    private const float JoinSparkleInterval = 100f * 0.85f;


    /*********
    ** Accessors
    *********/
    /// <summary>Constructs machine groups.</summary>
    public MachineGroupFactory Factory { get; }

    /// <summary>MOD: added. Swaps a connector's displayed appearance between its unpowered and powered variant based on power range.</summary>
    private readonly PoweredFloorSync PoweredFloorSync;

    /// <summary>MOD: added. Animates a connector's displayed appearance while it's powered but not part of a valid automation group.</summary>
    private readonly PoweredFloorAnimator PoweredFloorAnimator;

    /// <summary>MOD: added. Swaps a whitelist/blacklist sign's displayed appearance between a valid and invalid variant based on whether it's actually enforcing its filter.</summary>
    private readonly SignTextureSync SignTextureSync;

    /// <summary>An aggregate collection of machine groups linked by Junimo chests.</summary>
    public JunimoMachineGroup JunimoMachineGroup { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="config">The mod configuration.</param>
    /// <param name="data">The internal mod data.</param>
    /// <param name="defaultFactory">The default automation factory to registry.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="powerSiloTierRoller">MOD: added. Resolves <see cref="ModConfig.PowerSiloTierPools"/> into this save's rolled Power Silo tier requirements — constructed by the caller (see its own remarks for why) rather than here, so it's the same shared instance used elsewhere.</param>
    public MachineManager(Func<ModConfig> config, DataModel data, IAutomationFactory defaultFactory, IMonitor monitor, PowerSiloTierRoller powerSiloTierRoller)
    {
        this.Config = config;
        this.Data = data;
        this.Monitor = monitor;

        // MOD: added — the power system is its own self-contained class (see PowerSystem.cs);
        // construct it here from config and hand the whole thing to the factory.
        PowerSystem powerSystem = new(
            getEnabled: () => this.Config().PowerSystemEnabled,
            getSourceNames: () => this.Config().PowerSourceNames,
            getRangeDistance: () => this.Config().PowerRangeDistance,
            getLocalSourceNames: () => this.Config().LocalPowerSourceNames // MOD: added
        );

        // MOD: added — same self-contained-class pattern as PowerSystem above, but for the
        // "power-required machines" balance mechanic; see PowerRequiredMachineSystem.cs for details.
        PowerRequiredMachineSystem powerRequiredMachineSystem = new(
            getEnabled: () => this.Config().PowerRequiredMachinesEnabled,
            getMachineTypeNames: () => this.Config().PowerRequiredMachineNames,
            getGlobalCalloutsEnabled: () => this.Config().ConnectedMachineLocationPowerCallouts, // MOD: added
            getLocationByKey: this.GetLocationByKey // MOD: added
        );

        // MOD: added — same self-contained-class pattern as PowerSystem above, but for the "power silo
        // capacity" mechanic; see PowerSiloSystem.cs for details.
        PowerSiloSystem powerSiloSystem = new(
            getEnabled: () => this.Config().PowerSiloSystemEnabled,
            getSiloBuildingNames: () => this.Config().PowerSiloBuildingNames,
            getTiers: () => powerSiloTierRoller.GetEffectiveTiers(),
            getBaseCapacity: () => this.Config().PowerSiloBaseCapacity,
            getSourceNames: () => this.Config().PowerSourceNames,
            getSolarPanelNames: () => this.Config().PowerSiloSolarPanelNames, // MOD: added
            // MOD: added — fixed (twice over). First fix: `GetMachineDataFor` only has an entry for a
            // location once it's actually been scanned, so reading `?.PoweredTiles` off it could return
            // null (treated as "power system disabled, everything powered") for a location that just
            // hadn't been scanned yet, not because the power system was actually off. Second, deeper fix:
            // even a location WITH a cached entry only has it rebuilt by MachineManager's own deferred
            // reload queue (see QueueReload/ReloadQueuedLocations), which runs once per tick in
            // OnUpdateTicked — NOT synchronously the moment a Power Coil is placed/removed. Reading that
            // cache right after a coil event (from PowerSiloPatches, via a Harmony postfix or
            // ObjectListChanged, both of which can fire before that tick's reload runs) could return the
            // power layout from BEFORE the coil actually changed, one whole event behind — which is what
            // caused Solar Panels to never register a connectivity change on coil removal/placement.
            // Computing fresh here — the same per-location scan MachineGroupFactory.GetMachineGroups
            // already does for its own poweredTiles — sidesteps the cache (and its timing) entirely; it's
            // only ever called from PowerSiloSystem's own event-driven refreshes, never per-tick, so the
            // extra scan cost is fine.
            getPoweredTilesForLocation: location => powerSystem.GetPoweredTiles(location, new LocationFloodFillIndex(location, this.Monitor))
        );

        // MOD: added — swaps a connector's displayed appearance between its unpowered and "powered"
        // variant based on power range. See PoweredFloorSync.cs for details.
        this.PoweredFloorSync = new PoweredFloorSync(getConnectorTextureIds: () => this.Config().ConnectorPoweredTextureIds);

        // MOD: added — swaps a whitelist/blacklist sign's displayed appearance between a valid and
        // invalid variant. See SignTextureSync.cs for details.
        this.SignTextureSync = new SignTextureSync(getSignTextureIds: () => this.Config().SignTextureIds);

        // MOD: added — animates a connector's displayed appearance while it's powered but not part
        // of a valid automation group. See PoweredFloorAnimator.cs for details.
        this.PoweredFloorAnimator = new PoweredFloorAnimator(
            getConnectorTextureIds: () => this.Config().ConnectorPoweredTextureIds,
            getFps: () => this.Config().PoweredFloorAnimationFps,
            getUnpoweredHoldMultiplier: () => this.Config().PoweredFloorUnpoweredHoldMultiplier
        );

        this.Factory = new(
            getMachineOverride: this.GetMachineOverride,
            getChestOverride: this.GetChestOverride,
            getChestsEnabledByDefault: () => this.Config().ChestsEnabledByDefault,
            getWhitelistSignNames: () => this.Config().WhitelistSignNames, // MOD: added
            getBlacklistSignNames: () => this.Config().BlacklistSignNames, // MOD: added
            getWhitelistCategorySignNames: () => this.Config().WhitelistCategorySignNames, // MOD: added
            getBlacklistCategorySignNames: () => this.Config().BlacklistCategorySignNames, // MOD: added
            getCustomCategories: () => this.Config().CustomCategories, // MOD: added
            powerSystem: powerSystem, // MOD: added
            powerRequiredMachineSystem: powerRequiredMachineSystem, // MOD: added
            powerSiloSystem: powerSiloSystem, // MOD: added
            buildStorage: this.BuildStorage,
            isContainerCategoryEnabled: this.IsContainerCategoryEnabled, // MOD: added
            monitor: monitor
        );
        this.Factory.Add(defaultFactory);

        this.JunimoMachineGroup = new(this.Factory.SortMachines, this.BuildStorage, this.Monitor);
    }

    /// <summary>
    /// MOD: added. Advance the "powered but not part of a valid group" connector animation by one
    /// tick for the given location — normally just the current player's location, since this is a
    /// purely visual effect and there's no reason to animate tiles nobody can see.
    /// </summary>
    /// <param name="location">The location to animate.</param>
    public void TickPoweredFloorAnimation(GameLocation location)
    {
        this.PoweredFloorAnimator.Tick(location, this.GetMachineDataFor(location));
    }

    /****
    ** Machine search
    ****/
    /// <summary>Get the machine groups in every location.</summary>
    public IEnumerable<IMachineGroup> GetActiveMachineGroups()
    {
        if (this.JunimoMachineGroup.HasInternalAutomation)
            yield return this.JunimoMachineGroup;

        foreach (IMachineGroup group in this.ActiveMachineGroups)
            yield return group;
    }

    /// <summary>
    /// MOD: added. Get the same active machine groups <see cref="GetActiveMachineGroups"/> would, but
    /// narrowed to a specific location — used to scope an automation pass triggered by a
    /// location-specific event (e.g. <c>ChestInventoryChanged</c>) to just the affected location, instead
    /// of rescanning every location in the save. The Junimo aggregate group is still included
    /// unconditionally (same as <see cref="GetActiveMachineGroups"/>) since it can span multiple
    /// locations by design — there's no way to narrow it to just one without breaking that.
    /// </summary>
    /// <param name="location">The location whose machine groups to fetch.</param>
    public IEnumerable<IMachineGroup> GetActiveMachineGroupsFor(GameLocation location)
    {
        if (this.JunimoMachineGroup.HasInternalAutomation)
            yield return this.JunimoMachineGroup;

        string locationKey = this.Factory.GetLocationKey(location);
        foreach (IMachineGroup group in this.ActiveMachineGroups)
        {
            if (group.LocationKey == locationKey)
                yield return group;
        }
    }

    /// <summary>
    /// MOD: added. Get the same active machine groups <see cref="GetActiveMachineGroupsFor(GameLocation)"/>
    /// would, but narrowed further to only the group(s) that actually cover a specific tile — used to
    /// scope an automation pass triggered by a SPECIFIC container's inventory change (e.g.
    /// <c>ChestInventoryChanged</c>) to just the group(s) that container actually belongs to, instead of
    /// every group in the location. Without this, a container that's ALWAYS considered "empty" for
    /// scheduling purposes (see <see cref="Machines.Objects.PoweredChestMachine.GetState"/>'s own remarks)
    /// gets re-probed on EVERY chest change anywhere in the location, not just changes to containers it's
    /// actually connected to — real wasted work scheduling/queuing an action for it that's almost always
    /// going to be a no-op.
    /// </summary>
    /// <param name="location">The location whose machine groups to fetch.</param>
    /// <param name="tile">The tile the changed container occupies.</param>
    public IEnumerable<IMachineGroup> GetActiveMachineGroupsFor(GameLocation location, Vector2 tile)
    {
        string locationKey = this.Factory.GetLocationKey(location);
        foreach (IMachineGroup group in this.GetActiveMachineGroupsFor(location))
        {
            if (group.GetTiles(locationKey).Contains(tile))
                yield return group;
        }
    }

    /// <summary>
    /// MOD: added. Get and clear every group that's gained at least one new machine/container member since
    /// the last call — see <see cref="GroupsWithNewMembers"/>'s own remarks for why event-based mode needs
    /// this. Meant to be drained once per tick (see <c>ModEntry.OnUpdateTicked</c>) right after
    /// <see cref="ReloadQueuedLocations"/>, regardless of automation mode, so this can't grow unbounded —
    /// only event-based mode needs to actually act on the result, since interval mode's own periodic full
    /// scan already picks up a newly-joined member on its own.
    /// </summary>
    public IReadOnlyList<IMachineGroup> TakeGroupsWithNewMembers()
    {
        if (this.GroupsWithNewMembers.Count == 0)
            return Array.Empty<IMachineGroup>();

        IMachineGroup[] result = [.. this.GroupsWithNewMembers];
        this.GroupsWithNewMembers.Clear();
        return result;
    }

    /// <summary>
    /// MOD: added. Get and clear every <see cref="IMachineGroup"/> instance discarded since the last call
    /// — see <see cref="RemovedGroups"/>'s own remarks for why this matters. Meant to be drained once per
    /// tick (see <c>ModEntry.OnUpdateTicked</c>) right after <see cref="ReloadQueuedLocations"/>, same as
    /// <see cref="TakeGroupsWithNewMembers"/>, regardless of automation mode or whether action pacing is
    /// even enabled — cheap to drain into an empty result when nothing was removed.
    /// </summary>
    public IReadOnlyList<IMachineGroup> TakeRemovedGroups()
    {
        if (this.RemovedGroups.Count == 0)
            return Array.Empty<IMachineGroup>();

        IMachineGroup[] result = [.. this.RemovedGroups];
        this.RemovedGroups.Clear();
        return result;
    }

    /// <summary>
    /// MOD: added. Get and clear every locally-active <see cref="IMachineGroup"/> produced by a rescan
    /// since the last call — see <see cref="RescannedGroups"/>'s own remarks for why this needs to be
    /// broader than <see cref="TakeGroupsWithNewMembers"/>. Meant to be drained once per tick (see
    /// <c>ModEntry.OnUpdateTicked</c>) right after <see cref="ReloadQueuedLocations"/>, same as its
    /// siblings.
    /// </summary>
    public IReadOnlyList<IMachineGroup> TakeRescannedGroups()
    {
        if (this.RescannedGroups.Count == 0)
            return Array.Empty<IMachineGroup>();

        IMachineGroup[] result = [.. this.RescannedGroups];
        this.RescannedGroups.Clear();
        return result;
    }

    /// <summary>Get the active and disabled machine groups in a specific location for the API.</summary>
    /// <param name="location">The location whose machine groups to fetch.</param>
    public IEnumerable<IMachineGroup> GetForApi(GameLocation location)
    {
        string locationKey = this.Factory.GetLocationKey(location);

        return this
            .ActiveMachineGroups
            .Concat(this.DisabledMachineGroups)
            .Concat(this.JunimoMachineGroup.GetAll())
            .Where(p => p.LocationKey == locationKey);
    }

    /// <summary>Get the registered override settings.</summary>
    public IDictionary<string, ModConfigMachine> GetMachineOverrides()
    {
        ModConfig config = this.Config();

        Dictionary<string, ModConfigMachine> overrides = new(this.Data.DefaultMachineOverrides, StringComparer.OrdinalIgnoreCase);

        foreach ((string id, ModConfigMachine machineConfig) in config.MachineOverrides)
            overrides[id] = machineConfig;

        return overrides;
    }

    /// <summary>Get the settings for a machine.</summary>
    /// <param name="id">The unique machine ID.</param>
    public ModConfigMachine? GetMachineOverride(string id)
    {
        return this.Config().MachineOverrides.TryGetValue(id, out ModConfigMachine? config) || this.Data.DefaultMachineOverrides.TryGetValue(id, out config)
            ? config
            : null;
    }

    /// <summary>Get the settings for a storage container.</summary>
    /// <param name="id">The unique storage ID (usually the qualified item ID).</param>
    public ModConfigStorage? GetChestOverride(string id)
    {
        return this.Config().ChestOverrides.GetValueOrDefault(id);
    }

    /****
    ** Machine state
    ****/
    /// <summary>Get the machine state for a location, if any.</summary>
    /// <param name="location">The location to check.</param>
    public MachineDataForLocation? GetMachineDataFor(GameLocation location)
    {
        string locationKey = this.Factory.GetLocationKey(location);

        return this.MachineData.GetValueOrDefault(locationKey);
    }

    /// <summary>MOD: added. Get the location instance for a location key, if it's currently tracked. Used by <see cref="PowerRequiredMachineSystem.ProcessStarvedMachineCallouts"/> to resolve a friendly display name for its location-specific reminder message.</summary>
    /// <param name="locationKey">The location key, as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>.</param>
    public GameLocation? GetLocationByKey(string locationKey)
    {
        return this.LocationsByKey.GetValueOrDefault(locationKey);
    }

    /// <summary>MOD: added. Get the machine (and its group) at a given location/tile, if any is currently tracked there — see <see cref="MachineByLocationAndTile"/>'s own remarks.</summary>
    /// <param name="locationKey">The location key, as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>.</param>
    /// <param name="tile">The tile to check.</param>
    /// <param name="group">The machine's group, if found.</param>
    /// <param name="machine">The machine, if found.</param>
    public bool TryGetMachineAt(string locationKey, Vector2 tile, out IMachineGroup group, out IMachine machine)
    {
        if (this.MachineByLocationAndTile.TryGetValue((locationKey, tile), out (IMachineGroup Group, IMachine Machine) entry))
        {
            group = entry.Group;
            machine = entry.Machine;
            return true;
        }

        group = null!;
        machine = null!;
        return false;
    }

    /****
    ** State management
    ****/
    /// <summary>Clear all registered machines.</summary>
    public void Clear()
    {
        this.MachineData.Clear();
        this.ActiveMachineGroups = [];
        this.DisabledMachineGroups = [];
        this.JunimoMachineGroup.Clear();
        this.LocationsByKey.Clear(); // MOD: added
    }

    /// <summary>Clear all registered machines and add all locations to the reload queue.</summary>
    public void Reset()
    {
        this.Clear();

        this.JunimoMachineGroup.Rebuild();

        this.ReloadQueue.AddRange(CommonHelper.GetLocations());
    }

    /// <summary>Queue locations to remove and whose machines should be reloaded when <see cref="ReloadQueuedLocations"/> is called.</summary>
    /// <param name="locations">The locations to remove.</param>
    public void QueueRemove(IEnumerable<GameLocation> locations)
    {
        this.RemoveQueue.AddRange(locations);
    }

    /// <summary>Queue a location for which to reload machines when <see cref="ReloadQueuedLocations"/> is called.</summary>
    /// <param name="location">The location to reload.</param>
    public void QueueReload(GameLocation location)
    {
        this.ReloadQueue.Add(location);
    }

    /// <summary>Get whether a reload is already queued for a location.</summary>
    /// <param name="location">The location to reload.</param>
    public bool IsReloadQueued(GameLocation location)
    {
        return this.ReloadQueue.Contains(location);
    }

    /// <summary>Queue locations for which to reload machines when <see cref="ReloadQueuedLocations"/> is called.</summary>
    /// <param name="locations">The locations to reload.</param>
    public void QueueReload(IEnumerable<GameLocation> locations)
    {
        this.ReloadQueue.AddRange(locations);
    }

    /// <summary>Reload any locations queued for reload.</summary>
    /// <returns>Returns whether any locations were reloaded.</returns>
    public bool ReloadQueuedLocations()
    {
        this.CheckForSignChanges(); // MOD: added

        if (this.ReloadQueue.Any() || this.RemoveQueue.Any())
        {
            this.ReloadMachinesIn(this.ReloadQueue, this.RemoveQueue);
            this.ReloadQueue.Clear();
            this.RemoveQueue.Clear();

            // MOD: added — see PowerSiloCapacityChangedLastPass's remarks for why this has to happen
            // here, after the clear above, rather than inside ReloadMachinesIn itself.
            if (this.PowerSiloCapacityChangedLastPass)
            {
                this.PowerSiloCapacityChangedLastPass = false;
                this.QueueReload(CommonHelper.GetLocations());
            }

            return true;
        }

        return false;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// MOD: added. Check whether any tracked whitelist/blacklist sign's displayed item has changed
    /// since the last scan, and queue affected locations for reload if so. Throttled to run only
    /// every <see cref="SignCheckIntervalTicks"/> ticks, since it's just a UX convenience rather than
    /// something that needs to react instantly.
    /// </summary>
    private void CheckForSignChanges()
    {
        this.TicksSinceSignCheck++;
        if (this.TicksSinceSignCheck < MachineManager.SignCheckIntervalTicks)
            return;
        this.TicksSinceSignCheck = 0;

        foreach (MachineDataForLocation data in this.MachineData.Values)
        {
            if (data.SignCandidateTiles.Count == 0)
                continue; // no signs to watch in this location — skip entirely, no lookup needed

            GameLocation? location = null;

            foreach (Vector2 tile in data.SignCandidateTiles)
            {
                if (location == null && !this.LocationsByKey.TryGetValue(data.LocationKey, out location))
                    break; // location no longer exists — nothing to check

                (string ItemId, int? Number)? currentSignState = this.Factory.GetCurrentSignItemId(location, tile);
                (string LocationKey, Vector2 Tile) key = (data.LocationKey, tile);

                if (this.LastKnownSignItems.TryGetValue(key, out (string ItemId, int? Number)? lastSignState))
                {
                    if (currentSignState != lastSignState)
                    {
                        this.LastKnownSignItems[key] = currentSignState;
                        this.QueueReload(location);
                    }
                }
                else
                    this.LastKnownSignItems[key] = currentSignState;
            }
        }
    }

    /// <summary>Build a storage manager for the given containers.</summary>
    /// <param name="containers">The storage containers.</param>
    private StorageManager BuildStorage(IContainer[] containers)
    {
        return new StorageManager(containers, isCategoryEnabled: this.IsContainerCategoryEnabled);
    }

    /// <summary>
    /// MOD: added. Get whether a container's priority-tier category (see <see cref="IHasContainerPriority"/>)
    /// currently allows it to be pushed into/pulled from by the STANDARD machine automation cycle (a
    /// Furnace's output/input, etc. — see <see cref="StorageManager.TryPush"/>/<see cref="StorageManager.GetItems"/>),
    /// and (via <see cref="StorageManager.IsContainerCategoryEnabled"/>) whether a group made up only
    /// of chest-like machines counts as "actually automating anything" at all (see
    /// <see cref="MachineGroup.HasLocalInternalAutomation"/>). A normal chest, a Powered Chest, and a
    /// chest-backed hybrid can each be toggled off independently (see <see cref="ModConfig.ChestsCanAutomate"/>/
    /// <see cref="ModConfig.ChestHybridsCanAutomate"/>/<see cref="ModConfig.PoweredChestsCanAutomate"/>).
    /// This deliberately does NOT affect <see cref="StorageManager.AllContainers"/> — a Powered Chest's
    /// own active pull/push through its piped connectors (see <see cref="Machines.Objects.PoweredChestMachine.SetInput"/>)
    /// reads that directly and isn't restricted by another container's category here; it's gated by its
    /// OWN category instead. So disabling normal chests, say, only stops a Furnace from pushing
    /// into/pulling from one directly — a Powered Chest can still reach into that same chest.
    /// </summary>
    /// <param name="container">The container to check.</param>
    private bool IsContainerCategoryEnabled(IContainer container)
    {
        ModConfig config = this.Config();
        return container.GetContainerPriorityTier() switch
        {
            ContainerPriorityTiers.PoweredChest => config.PoweredChestsCanAutomate,
            ContainerPriorityTiers.ChestHybrid => config.ChestHybridsCanAutomate,
            _ => config.ChestsCanAutomate
        };
    }

    /// <summary>Reload the machines in a given location.</summary>
    /// <param name="locations">The locations whose machines to reload.</param>
    /// <param name="removedLocations">The locations which have been removed, and whose machines should be reloaded if they still exist.</param>
    private void ReloadMachinesIn(ISet<GameLocation> locations, ISet<GameLocation> removedLocations)
    {
        bool junimoGroupChanged = false;
        bool anyChanged = false;

        // remove old groups
        {
            HashSet<string> locationKeys = [.. locations.Concat(removedLocations).Select(this.Factory.GetLocationKey)];
            if (this.Monitor.IsVerbose)
                this.Monitor.Log($"Reloading machines in {locationKeys.Count} locations: {string.Join(", ", locationKeys)}...");

            foreach (string locationKey in locationKeys)
            {
                if (this.MachineData.Remove(locationKey, out MachineDataForLocation? oldData))
                {
                    anyChanged = true;

                    // MOD: added — see RemovedGroups' own remarks for why these need to be tracked.
                    this.RemovedGroups.AddRange(oldData.ActiveMachineGroups);
                    this.RemovedGroups.AddRange(oldData.DisabledMachineGroups);
                }
            }

            // MOD: added — drop stale cached location references for locations being reloaded/removed too.
            foreach (string locationKey in locationKeys)
                this.LocationsByKey.Remove(locationKey);

            // MOD: added — drop stale location+tile machine index entries for locations being
            // reloaded/removed; they'll be reseeded fresh below for anything still active.
            foreach (string locationKey in locationKeys)
            {
                if (this.MachineTileKeysByLocation.TryGetValue(locationKey, out List<Vector2>? tiles))
                {
                    foreach (Vector2 tile in tiles)
                        this.MachineByLocationAndTile.Remove((locationKey, tile));
                    this.MachineTileKeysByLocation.Remove(locationKey);
                }
            }

            // MOD: added — drop stale sign snapshot entries for locations being reloaded/removed;
            // they'll be reseeded fresh below for anything still active.
            foreach ((string LocationKey, Vector2 Tile) key in this.LastKnownSignItems.Keys.Where(k => locationKeys.Contains(k.LocationKey)).ToArray())
                this.LastKnownSignItems.Remove(key);

            // MOD: added — drop stale "previously active" tile snapshots only for locations that are
            // actually gone, NOT ones simply being rescanned — unlike the caches above, this one needs
            // to survive a reload/rescan cycle so the "newly joined" diff below has something to
            // compare against; wiping it on every rescan would make every active tile look "new" and
            // spam the join sparkle constantly.
            foreach (string locationKey in removedLocations.Select(this.Factory.GetLocationKey))
            {
                this.PreviouslyActiveEntityTilesByLocation.Remove(locationKey);
                this.PreviouslyActiveGroupTileSetsByLocation.Remove(locationKey);
            }

            if (this.JunimoMachineGroup.RemoveLocations(locationKeys))
            {
                anyChanged = true;
                junimoGroupChanged = true;
            }
        }

        // MOD: added — the power silo cap is global (not per-location), so a change to it can't be
        // detected inside the per-location loop below the way poweredTiles normally is. This is the
        // CHEAP half (see PreviousTotalCapacity's remarks) — just a building scan, no coils — so it's
        // fine to run every pass purely to detect a change. When it does change, the expensive coil
        // scan (RefreshCoilAllowance) runs once here, and every location (not just the ones already
        // queued this pass) is flagged for reload too, so a Silo tier-up/build/destroy correctly
        // propagates to coils in every other location.
        int totalCapacity = this.Factory.PowerSiloSystem.GetTotalCapacity();
        this.PowerSiloCapacityChangedLastPass = this.PreviousTotalCapacity != totalCapacity;
        this.PreviousTotalCapacity = totalCapacity;
        if (this.PowerSiloCapacityChangedLastPass)
            this.Factory.PowerSiloSystem.RefreshCoilAllowance();

        // add new groups
        foreach (GameLocation location in locations)
        {
            string locationKey = this.Factory.GetLocationKey(location);

            // MOD: changed — build the location index and powered tiles ONCE, and reuse both for
            // group building and the overlay's PoweredTiles data. Previously these were computed
            // twice per rescan (once inside GetMachineGroups, once here) — each involving a full
            // scan of the location — which was pure redundant work.
            LocationFloodFillIndex locationIndex = new(location, this.Monitor);
            HashSet<Vector2>? poweredTiles = this.Factory.PowerSystem.GetPoweredTiles(location, locationIndex);

            // collect new groups
            List<IMachineGroup> active = [];
            List<IMachineGroup> disabled = [];
            List<IMachineGroup> junimo = [];
            foreach (IMachineGroup group in this.Factory.GetMachineGroups(location, locationIndex, poweredTiles))
            {
                if (!group.HasInternalAutomation)
                    disabled.Add(group);

                else if (group.IsJunimoGroup)
                    junimo.Add(group);

                else
                    active.Add(group);
            }

            // MOD: added — (re)build the location+tile machine index for this location (see
            // MachineByLocationAndTile's own remarks) — includes disabled groups too, deliberately:
            // a machine in a currently-disabled group shouldn't be silently dropped from the index,
            // since the group's own Automate() gating (locked containers, pause expiries) already
            // handles whether it's actually safe to process.
            {
                List<Vector2> tileKeys = [];
                foreach (IMachineGroup group in active.Concat(disabled).Concat(junimo))
                {
                    foreach (IMachine machine in group.Machines)
                    {
                        foreach (Vector2 tile in machine.TileArea.GetTiles())
                        {
                            this.MachineByLocationAndTile[(locationKey, tile)] = (group, machine);
                            tileKeys.Add(tile);
                        }
                    }
                }
                this.MachineTileKeysByLocation[locationKey] = tileKeys;
            }

            // MOD: added — show a small sparkle flash (the same star used for the geode-cracking
            // reward reveal) on any machine/container that's newly joined an active automation group
            // since the last rebuild, as a lightweight "just connected" visual confirmation — plus the
            // same sparkle across every connector/pipe tile in that specific group, so the whole
            // network it just joined lights up too. Compares this rebuild's active machine/container
            // tiles against the previous rebuild's, so it fires exactly once per join rather than
            // every rescan. Each tile is tinted by its connection kind (see the *SparkleColor fields
            // above) — a connector tile just uses its own role in this group; a newly-joined
            // machine/container tile uses its group's role IF the group's connectors are all the same
            // role, otherwise (or if the SAME tile ends up assigned conflicting colors — e.g. a
            // container that's part of two or more groups of different kinds) it falls back to the
            // ambiguous (white) color instead of guessing.
            //
            // MOD: added — includes any Junimo sub-group with its own real local automation, same as
            // MachineDataForLocation.GetDisplayActiveGroups does for the overlay — a Junimo chest is
            // otherwise sorted into `junimo`, not `active`, and would silently never trigger this at
            // all (its tiles would never appear in the tracked snapshot, so it could neither be
            // detected joining NOR leaving).
            IEnumerable<IMachineGroup> sparkleEligibleGroups = active.Concat(junimo.Where(g => g.HasLocalInternalAutomation));

            // MOD: added — see RescannedGroups' own remarks. Unconditional (unlike the new-join detection
            // below), so it fires on every rescan of this location, including the very first one.
            this.RescannedGroups.AddRange(sparkleEligibleGroups);

            Dictionary<IMachineGroup, HashSet<Vector2>> entityTilesByGroup = new();
            HashSet<Vector2> activeEntityTiles = new();
            foreach (IMachineGroup group in sparkleEligibleGroups)
            {
                HashSet<Vector2> groupEntityTiles = new();
                foreach (IMachine machine in group.Machines)
                    groupEntityTiles.UnionWith(machine.TileArea.GetTiles());
                foreach (IContainer container in group.Containers)
                    groupEntityTiles.UnionWith(container.TileArea.GetTiles());

                entityTilesByGroup[group] = groupEntityTiles;
                activeEntityTiles.UnionWith(groupEntityTiles);
            }
            if (this.PreviouslyActiveEntityTilesByLocation.TryGetValue(locationKey, out HashSet<Vector2>? previousActiveEntityTiles))
            {
                Dictionary<Vector2, Color> sparkleColorByTile = new();
                HashSet<Vector2> ambiguousSparkleTiles = new();
                bool anyNewJoin = false;

                void AssignSparkleColor(Vector2 tile, Color? color)
                {
                    if (ambiguousSparkleTiles.Contains(tile))
                        return;

                    if (color == null || (sparkleColorByTile.TryGetValue(tile, out Color existing) && existing != color))
                    {
                        ambiguousSparkleTiles.Add(tile);
                        sparkleColorByTile.Remove(tile);
                    }
                    else
                        sparkleColorByTile[tile] = color.Value;
                }

                foreach ((IMachineGroup group, HashSet<Vector2> groupEntityTiles) in entityTilesByGroup)
                {
                    IReadOnlyDictionary<Vector2, ConnectorRole> connectorRoles = group.GetConnectorRoles(locationKey);
                    HashSet<ConnectorRole> distinctGroupRoles = new(connectorRoles.Values);
                    Color? groupEntityColor = distinctGroupRoles.Count == 1 ? MachineManager.GetConnectionSparkleColor(distinctGroupRoles.First()) : null;

                    bool groupHasNewJoin = false;
                    foreach (Vector2 tile in groupEntityTiles)
                    {
                        if (previousActiveEntityTiles.Contains(tile))
                            continue;

                        groupHasNewJoin = true;
                        AssignSparkleColor(tile, groupEntityColor);
                    }

                    if (groupHasNewJoin)
                    {
                        anyNewJoin = true;
                        this.GroupsWithNewMembers.Add(group); // MOD: added — see TakeGroupsWithNewMembers's own remarks
                        foreach ((Vector2 connectorTile, ConnectorRole role) in connectorRoles)
                            AssignSparkleColor(connectorTile, MachineManager.GetConnectionSparkleColor(role));
                    }
                }

                foreach (Vector2 tile in sparkleColorByTile.Keys.Concat(ambiguousSparkleTiles))
                {
                    Color color = ambiguousSparkleTiles.Contains(tile) ? MachineManager.AmbiguousConnectionSparkleColor : sparkleColorByTile[tile];
                    TemporaryAnimatedSprite sparkle = new("TileSheets\\animations", new Rectangle(0, 640, 64, 64), 100f, 8, 0, tile * Game1.tileSize, flicker: false, flipped: false)
                    {
                        color = color,
                        interval = MachineManager.JoinSparkleInterval
                    };
                    Game1.Multiplayer.broadcastSprites(location, sparkle);
                }

                // MOD: added — play a sound once per location per rebuild when at least one group
                // gained a new member (a connection was made / a group formed).
                if (anyNewJoin)
                    location.playSound("dialogueCharacterClose");
            }
            this.PreviouslyActiveEntityTilesByLocation[locationKey] = activeEntityTiles;

            // MOD: added — play a sound when a previously-valid group stops being valid ENTIRELY (not
            // just shrinking — losing one member from an otherwise-still-active group doesn't count).
            // Since IMachineGroup instances are rebuilt fresh every rescan (no stable identity across
            // rebuilds), "the same group" is tracked by tile-set overlap instead: a previously-active
            // group's full footprint (entities + connectors, via GetTiles) is considered "still alive"
            // as long as it overlaps at least one currently-active group's footprint; if it has zero
            // overlap with every currently-active group, that group broke completely.
            List<HashSet<Vector2>> currentActiveGroupTileSets = sparkleEligibleGroups
                .Select(group => new HashSet<Vector2>(group.GetTiles(locationKey)))
                .ToList();
            if (this.PreviouslyActiveGroupTileSetsByLocation.TryGetValue(locationKey, out List<HashSet<Vector2>>? previousActiveGroupTileSets))
            {
                bool anyGroupCompletelyBroken = previousActiveGroupTileSets.Any(previousGroupTiles =>
                    !currentActiveGroupTileSets.Any(currentGroupTiles => currentGroupTiles.Overlaps(previousGroupTiles)));

                if (anyGroupCompletelyBroken)
                    location.playSound("cancel");
            }
            this.PreviouslyActiveGroupTileSetsByLocation[locationKey] = currentActiveGroupTileSets;

            // add groups
            // MOD: passes `junimo` through too — MachineDataForLocation folds it into its
            // display/visualization-only tile lookups (never the real processing list) so a
            // Junimo-touching sign/connector/chest is visualized exactly like any other, instead of
            // needing its own parallel set of checks. See that record's own remarks for why.
            MachineDataForLocation locationData = new(locationKey, active, disabled, poweredTiles, junimo);
            this.MachineData[locationKey] = locationData;
            this.LocationsByKey[locationKey] = location; // MOD: added — keep the cache fresh for CheckForSignChanges

            // MOD: added — swap any managed connector's displayed appearance to match its current
            // power and group state (needs the just-built locationData for its ActiveTiles).
            this.PoweredFloorSync.Sync(location, locationData);

            // MOD: added — swap any managed whitelist/blacklist sign's displayed appearance to match
            // whether it's currently valid (enforcing its filter) or not.
            this.SignTextureSync.Sync(location, locationData);

            // MOD: added — reseed the sign snapshot for this location's current sign candidate tiles
            // (not just ones with an item currently on them), so this fresh rescan isn't immediately
            // (and incorrectly) treated as "a sign changed" on the next periodic check, and so
            // currently-empty signs are seeded too and thus watched going forward.
            foreach (Vector2 signTile in locationData.SignCandidateTiles)
                this.LastKnownSignItems[(locationKey, signTile)] = this.Factory.GetCurrentSignItemId(location, signTile);

            // track change
            if (junimo.Any())
            {
                this.JunimoMachineGroup.Add(junimo);
                junimoGroupChanged = true;
                anyChanged = true;
            }
            else if (active.Any() || disabled.Any())
                anyChanged = true;
        }


        // rebuild caches
        if (anyChanged)
        {
            List<IMachineGroup> active = [];
            List<IMachineGroup> disabled = [];

            foreach (MachineDataForLocation locationData in this.MachineData.Values)
            {
                active.AddRange(locationData.ActiveMachineGroups);
                disabled.AddRange(locationData.DisabledMachineGroups);
            }

            this.ActiveMachineGroups = active.ToArray();
            this.DisabledMachineGroups = disabled.ToArray();
        }

        if (junimoGroupChanged)
            this.JunimoMachineGroup.Rebuild();
    }

    /// <summary>MOD: added. Get the join sparkle's tint for a given connection role — see the *SparkleColor fields for what each one means.</summary>
    /// <param name="role">The connector role to get a color for.</param>
    private static Color GetConnectionSparkleColor(ConnectorRole role)
    {
        return role switch
        {
            ConnectorRole.ChestInputOnly => MachineManager.PullConnectionSparkleColor,
            ConnectorRole.ChestOutputOnly => MachineManager.PushConnectionSparkleColor,
            _ => MachineManager.BothConnectionSparkleColor
        };
    }

}

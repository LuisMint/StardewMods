using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Common;
using StardewModdingAPI;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>Handles logic for building an <see cref="IMachineGroup"/>.</summary>
internal class MachineGroupBuilder
{
    /*********
    ** Fields
    *********/
    /// <summary>The location containing the group, as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>.</summary>
    private readonly string LocationKey;

    /// <summary>Encapsulates monitoring and logging.</summary>
    private readonly IMonitor Monitor;

    /// <summary>The machines in the group.</summary>
    private readonly HashSet<IMachine> Machines = [];

    /// <summary>The containers in the group.</summary>
    private readonly HashSet<IContainer> Containers = [];

    /// <summary>The tiles comprising the group.</summary>
    private readonly HashSet<Vector2> Tiles = [];

    /// <summary>MOD: added. The connector role for each connector tile added to the group.</summary>
    private readonly Dictionary<Vector2, ConnectorRole> ConnectorRoles = [];

    /// <summary>MOD: added. An optional item filter derived from whitelist/blacklist signs touching this group.</summary>
    private SignFilter? ItemFilter;

    /// <summary>MOD: added. Debug markers for tiles where a configured sign was detected, regardless of whether it currently holds an item.</summary>
    private readonly Dictionary<Vector2, bool> SignMarkers = [];

    /// <summary>MOD: added. Every tile where a configured whitelist/blacklist sign object exists, regardless of whether it currently holds an item — broader than <see cref="SignMarkers"/>, used so periodic polling can watch a sign even while it's empty.</summary>
    private readonly HashSet<Vector2> SignCandidateTiles = [];

    /// <summary>MOD: added. The tiles of every machine that's currently "power-starved" (a machine type configured to require power, whose own tile isn't within power range as of this rebuild) — tracked by tile rather than machine reference, since <see cref="Build"/> wraps each machine in a new <see cref="MachineWrapper"/> instance that wouldn't match the original reference.</summary>
    private readonly HashSet<Vector2> PowerStarvedTiles = [];

    /// <summary>Sort machines by priority.</summary>
    private readonly Func<IEnumerable<IMachine>, IEnumerable<IMachine>> SortMachines;

    /// <summary>Build a storage manager for the given containers.</summary>
    private readonly Func<IContainer[], StorageManager> BuildStorage;

    /// <summary>MOD: added. Get the effective <c>ActionDelaySeconds</c> (after any Power Relay bonus), in seconds — see <see cref="ThrottledContainer"/>.</summary>
    private readonly Func<float> GetEffectiveActionDelaySeconds;

    /// <summary>MOD: added. Get the effective <c>ActionsPerDelayWindow</c> (after any Power Relay bonus) — see <see cref="ThrottledContainer"/>.</summary>
    private readonly Func<int> GetEffectiveActionsPerDelayWindow;

    /// <summary>MOD: added. Get the real-time, pause-aware elapsed-milliseconds clock used to size a container's shared pacing window — see <see cref="ThrottledContainer"/>'s own remarks.</summary>
    private readonly Func<double> GetElapsedMs;

    /// <summary>MOD: added. Get whether a container's lid animation/jolt/item sprite/sound should play — see <see cref="ThrottledContainer"/>.</summary>
    private readonly Func<bool> GetVisualEffectsEnabled;

    /// <summary>MOD: added. Proactively wake every active group covering a tile whose container just changed — <c>(location, tile, isJunimoChest)</c> — see <see cref="ThrottledContainer"/>'s own remarks for why this exists alongside SMAPI's own <c>ChestInventoryChanged</c> event.</summary>
    private readonly Action<GameLocation, Vector2, bool> NotifyContainerChanged;

    /*********
    ** Public methods
    *********/
    /// <summary>Create an instance.</summary>
    /// <param name="locationKey">The location containing the group, as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>.</param>
    /// <param name="sortMachines">Sort machines by priority.</param>
    /// <param name="buildStorage">Build a storage manager for the given containers.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="getEffectiveActionDelaySeconds">MOD: added. Get the effective <c>ActionDelaySeconds</c> (after any Power Relay bonus), in seconds.</param>
    /// <param name="getEffectiveActionsPerDelayWindow">MOD: added. Get the effective <c>ActionsPerDelayWindow</c> (after any Power Relay bonus).</param>
    /// <param name="getElapsedMs">MOD: added. Get the real-time, pause-aware elapsed-milliseconds clock — see <see cref="GetElapsedMs"/>.</param>
    /// <param name="getVisualEffectsEnabled">MOD: added. Get whether a container's lid animation/jolt/item sprite/sound should play.</param>
    /// <param name="notifyContainerChanged">MOD: added. Proactively wake every active group covering a tile whose container just changed — see <see cref="NotifyContainerChanged"/>.</param>
    public MachineGroupBuilder(string locationKey, Func<IEnumerable<IMachine>, IEnumerable<IMachine>> sortMachines, Func<IContainer[], StorageManager> buildStorage, IMonitor monitor, Func<float> getEffectiveActionDelaySeconds, Func<int> getEffectiveActionsPerDelayWindow, Func<double> getElapsedMs, Func<bool> getVisualEffectsEnabled, Action<GameLocation, Vector2, bool> notifyContainerChanged)
    {
        this.LocationKey = locationKey;
        this.SortMachines = sortMachines;
        this.BuildStorage = buildStorage;
        this.Monitor = monitor;
        this.GetEffectiveActionDelaySeconds = getEffectiveActionDelaySeconds;
        this.GetEffectiveActionsPerDelayWindow = getEffectiveActionsPerDelayWindow;
        this.GetElapsedMs = getElapsedMs;
        this.GetVisualEffectsEnabled = getVisualEffectsEnabled;
        this.NotifyContainerChanged = notifyContainerChanged;
    }

    /// <summary>Add a machine to the group.</summary>
    /// <param name="machine">The machine to add.</param>
    /// <param name="isPowerStarved">MOD: added. Whether this machine is a "power-required" type whose own tile isn't currently within power range — see <see cref="PowerStarvedTiles"/>.</param>
    public void Add(IMachine machine, bool isPowerStarved = false)
    {
        // MOD: added — if this group has an item filter AND the machine is backed by a real Cask, wrap
        // it so a quality-tag Category Whitelist sign can make it collectible before it's fully aged to
        // iridium. See CaskQualityFilterMachine's own remarks. Every other machine type is left
        // completely untouched, unlike the analogous container wrap below (in the other Add overload),
        // which applies to every container in a filtered group regardless of type.
        if (this.ItemFilter != null && machine is IHasUnderlyingObject { UnderlyingObject: StardewValley.Objects.Cask cask })
            machine = new CaskQualityFilterMachine(machine, cask, this.ItemFilter);

        this.Machines.Add(machine);
        this.Add(machine.TileArea);

        if (isPowerStarved)
            this.PowerStarvedTiles.UnionWith(machine.TileArea.GetTiles());
    }

    /// <summary>Add a container to the group.</summary>
    /// <param name="container">The container to add.</param>
    /// <param name="budget">MOD: added. The shared pacing state for the physical container being added — see <see cref="ThrottledContainerBudget"/> for why this must be resolved by the caller (keyed by the raw container's own identity, shared across every group reaching the same physical container) rather than created fresh here.</param>
    public void Add(IContainer container, ThrottledContainerBudget budget)
    {
        // MOD: added — if this group has an item filter, wrap the container so both Automate's own
        // pull/push flow and its raw Inventory (read directly by some vanilla machines, e.g. via
        // SObject.AttemptAutoLoad) respect it.
        if (this.ItemFilter != null)
            container = new ItemFilteredContainer(container, this.ItemFilter);

        // MOD: added — the outermost wrap: chunked delivery + entry/exit visual effects apply to every
        // container mod-wide, with no per-container-class changes needed. See ThrottledContainer's own
        // remarks.
        container = new ThrottledContainer(container, budget, this.GetEffectiveActionDelaySeconds, this.GetEffectiveActionsPerDelayWindow, this.GetElapsedMs, this.GetVisualEffectsEnabled, this.NotifyContainerChanged);

        this.Containers.Add(container);
        this.Add(container.TileArea);
    }

    /// <summary>Add connector tiles to the group.</summary>
    /// <param name="tileArea">The tile area to add.</param>
    public void Add(Rectangle tileArea)
    {
        this.Add(tileArea, role: null);
    }

    /// <summary>MOD: added. Add connector tiles to the group with an associated connector role.</summary>
    /// <param name="tileArea">The tile area to add.</param>
    /// <param name="role">The connector role for this tile area, or <c>null</c> if not applicable (e.g. when called internally for a machine or container's own tile area, which has no role of its own).</param>
    public void Add(Rectangle tileArea, ConnectorRole? role)
    {
        foreach (Vector2 tile in tileArea.GetTiles())
        {
            this.Tiles.Add(tile);
            if (role.HasValue)
                this.ConnectorRoles[tile] = role.Value;
        }
    }

    /// <summary>MOD: added. Set an item filter derived from whitelist/blacklist signs touching this group. Items not matching the filter won't move through the group's storage in either direction. Must be called BEFORE any containers are added, since it's applied at add-time.</summary>
    /// <param name="filter">The item filter, or <c>null</c> to clear it.</param>
    public void SetItemFilter(SignFilter? filter)
    {
        this.ItemFilter = filter;
    }

    /// <summary>MOD: added. Mark a tile as having a detected whitelist/blacklist sign, for debug overlay purposes — regardless of whether the sign currently holds an item.</summary>
    /// <param name="tile">The tile where the sign was found.</param>
    /// <param name="isWhitelist"><c>true</c> if it's a whitelist sign, <c>false</c> if it's a blacklist sign.</param>
    public void MarkSignTile(Vector2 tile, bool isWhitelist)
    {
        this.SignMarkers[tile] = isWhitelist;
    }

    /// <summary>MOD: added. Mark a tile as having a configured whitelist/blacklist sign object, regardless of whether it currently holds an item — used so periodic polling knows to watch this tile even while the sign is empty.</summary>
    /// <param name="tile">The tile where the sign was found.</param>
    public void MarkSignCandidateTile(Vector2 tile)
    {
        this.SignCandidateTiles.Add(tile);
    }

    /// <summary>Get whether any tiles were added to the builder.</summary>
    public bool HasTiles()
    {
        return this.Tiles.Count > 0;
    }

    /// <summary>Create a group from the saved data.</summary>
    public IMachineGroup Build()
    {
        var machines = this.SortMachines(this.Machines.Select(p => new MachineWrapper(p)));
        return new MachineGroup(this.LocationKey, machines, this.Containers, this.Tiles, this.BuildStorage, this.Monitor, this.ConnectorRoles, this.SignMarkers, this.SignCandidateTiles, this.PowerStarvedTiles); // MOD: added connectorRoles + signMarkers + signCandidateTiles + powerStarvedTiles args
    }
}

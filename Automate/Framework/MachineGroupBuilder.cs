using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Common;
using StardewModdingAPI;

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

    /*********
    ** Public methods
    *********/
    /// <summary>Create an instance.</summary>
    /// <param name="locationKey">The location containing the group, as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>.</param>
    /// <param name="sortMachines">Sort machines by priority.</param>
    /// <param name="buildStorage">Build a storage manager for the given containers.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    public MachineGroupBuilder(string locationKey, Func<IEnumerable<IMachine>, IEnumerable<IMachine>> sortMachines, Func<IContainer[], StorageManager> buildStorage, IMonitor monitor)
    {
        this.LocationKey = locationKey;
        this.SortMachines = sortMachines;
        this.BuildStorage = buildStorage;
        this.Monitor = monitor;
    }

    /// <summary>Add a machine to the group.</summary>
    /// <param name="machine">The machine to add.</param>
    /// <param name="isPowerStarved">MOD: added. Whether this machine is a "power-required" type whose own tile isn't currently within power range — see <see cref="PowerStarvedTiles"/>.</param>
    public void Add(IMachine machine, bool isPowerStarved = false)
    {
        this.Machines.Add(machine);
        this.Add(machine.TileArea);

        if (isPowerStarved)
            this.PowerStarvedTiles.UnionWith(machine.TileArea.GetTiles());
    }

    /// <summary>Add a container to the group.</summary>
    /// <param name="container">The container to add.</param>
    public void Add(IContainer container)
    {
        // MOD: added — if this group has an item filter, wrap the container so both Automate's own
        // pull/push flow and its raw Inventory (read directly by some vanilla machines, e.g. via
        // SObject.AttemptAutoLoad) respect it.
        if (this.ItemFilter != null)
            container = new ItemFilteredContainer(container, this.ItemFilter);

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

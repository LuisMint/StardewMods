using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.Utilities;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>A collection of machines and storage which work as one unit.</summary>
internal class MachineGroup : IMachineGroup
{
    /*********
    ** Fields
    *********/
    /// <summary>The number of milliseconds to pause output for a given item ID when the connected chests can't accept it.</summary>
    private readonly int OutputPauseMilliseconds = 5000;

    /// <summary>The number of milliseconds to pause machines when they crash.</summary>
    private readonly int MachinePauseMilliseconds = 30000;

    /// <summary>Encapsulates monitoring and logging.</summary>
    private readonly IMonitor Monitor;

    /// <summary>Machines which are temporarily paused, with the game time in milliseconds when their pause expires.</summary>
    private readonly Dictionary<IMachine, double> MachinePauseExpiries = new(new ObjectReferenceComparer<IMachine>());

    /// <summary>The output items which are temporarily paused, with the game time in milliseconds when their pause expires.</summary>
    private readonly Dictionary<string, double> OutputPauseExpiries = [];

    /// <summary>The storage manager for the group.</summary>
    protected readonly StorageManager StorageManager;

    /// <summary>The tiles covered by this machine group.</summary>
    private readonly HashSet<Vector2> Tiles;

    /// <summary>MOD: added. The connector role for each connector tile covered by this group.</summary>
    private readonly Dictionary<Vector2, ConnectorRole> ConnectorRoles;

    /// <summary>MOD: added. The debug sign markers for each tile where a configured sign was detected, regardless of whether it currently holds an item.</summary>
    private readonly Dictionary<Vector2, bool> SignMarkers;

    /// <summary>MOD: added. Every tile where a configured whitelist/blacklist sign object exists, regardless of whether it currently holds an item — broader than <see cref="SignMarkers"/>, used so periodic polling can watch a sign even while it's empty.</summary>
    private readonly HashSet<Vector2> SignCandidateTiles;

    /// <summary>MOD: added. The tiles of every machine that's currently "power-starved" (a machine type configured to require power, whose own tile isn't within power range as of this rebuild) — see <see cref="MachineGroupBuilder.PowerStarvedTiles"/> for why this is tracked by tile rather than machine reference.</summary>
    private readonly HashSet<Vector2> PowerStarvedTiles;

    /****
    ** Pooled instances
    ** (These just minimize object allocations, and aren't used to store state between ticks.)
    ****/
    /// <summary>The pre-allocated list used to store machines which have output ready during the current automation tick.</summary>
    private readonly List<IMachine> PooledOutputReady = [];

    /// <summary>The pre-allocated list used to store machines which have input ready during the current automation tick.</summary>
    private readonly List<IMachine> PooledInputReady = [];

    /// <summary>A pre-allocated set used to store machine IDs that should be ignored for input during the current automation tick.</summary>
    private readonly HashSet<string> PooledIgnoreMachinesForInput = [];


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public string? LocationKey { get; }

    /// <inheritdoc />
    public IMachine[] Machines { get; protected set; }

    /// <inheritdoc />
    public IContainer[] Containers { get; protected set; }

    /// <inheritdoc />
    [MemberNotNullWhen(false, nameof(IMachineGroup.LocationKey))]
    public bool IsJunimoGroup { get; protected set; }

    /// <summary>
    /// MOD: changed. A group with only "chest-like" machines (see <see cref="IChestLikeMachine"/> — an
    /// active mover that's also its own chest; currently only the Powered Chest, since chest-backed
    /// hybrids like the Hopper are purely passive storage now, not chest-like machines at all) isn't
    /// necessarily automating anything: each chest-like machine defers to any OTHER chest-like machine
    /// of equal or better priority (see <see cref="Storage.ContainerExtensions.ShouldDeferToAsActiveMover"/>)
    /// to avoid two active movers fighting over the same connection, so e.g. two Powered Chests only
    /// connected to each other — or one chest-like machine alone — can never actually move an item. The
    /// comparison is generic by tier rather than hardcoded to Powered Chest specifically, so it already
    /// extends correctly to any future active-mover machine of a different tier. See
    /// <see cref="HasLocalInternalAutomation"/> for the precise rule.
    /// </summary>
    /// <inheritdoc />
    public virtual bool HasInternalAutomation => this.IsJunimoGroup || this.HasLocalInternalAutomation;

    /// <inheritdoc />
    /// <remarks>
    /// MOD: added — see the interface doc comment for why this needs to exist separately from
    /// <see cref="HasInternalAutomation"/>. Fixed to no longer exclude Junimo chests from counting as
    /// "a real container" the way another chest-like machine's own storage is excluded: a Junimo chest
    /// is mechanically just a chest (see <see cref="IContainer.IsJunimoChest"/>'s own remarks — the
    /// ONLY thing special about it is that it shares its inventory with every other Junimo chest), and
    /// a chest-like machine genuinely DOES actively move items to/from one, exactly like it would a
    /// plain chest. The old <c>!p.IsJunimoChest</c> exclusion here predates that — it was always
    /// vacuous for a genuinely non-Junimo group (by definition, none of its containers ARE Junimo
    /// chests) and unreachable for a Junimo-touching group via <see cref="HasInternalAutomation"/>
    /// (short-circuited by <see cref="IsJunimoGroup"/> before ever getting here), so it only became
    /// "live" — and wrong — once this property started being called directly, on a Junimo-touching
    /// group, by <see cref="JunimoMachineGroup.GetLocallyActiveTiles"/>.
    ///
    /// MOD: the last branch below compares priority tiers generically (see
    /// <see cref="Storage.ContainerExtensions.ShouldDeferToAsActiveMover"/>) rather than hardcoding
    /// "Powered Chest is the only chest-like machine" — currently that's the only chest-like machine
    /// there is (chest-backed hybrids are plain containers now, not active movers — see
    /// <see cref="IChestLikeMachine"/>'s own remarks), but this stays correct without changes if a
    /// future active-mover machine of a different tier is ever added.
    ///
    /// MOD: added — also accounts for <see cref="ModConfig.ChestsCanAutomate"/>/<see cref="ModConfig.ChestHybridsCanAutomate"/>/
    /// <see cref="ModConfig.PoweredChestsCanAutomate"/> (see <see cref="StorageManager.IsContainerCategoryEnabled"/>):
    /// a disabled category can't be reached by the standard machine cycle at all (so it no longer
    /// counts toward "there's a container here" for a regular machine), and a disabled chest-like
    /// machine never initiates its own movement (so it doesn't count as an active mover either) — but
    /// it can still be TARGETED by a different, still-enabled active mover of better priority (e.g. an
    /// enabled Powered Chest can still reach into a disabled Hopper's storage), since that target's own
    /// category only gates whether IT initiates movement, not whether it can be acted upon.
    /// </remarks>
    public bool HasLocalInternalAutomation
    {
        get
        {
            IMachine[] chestLikeMachines = this.Machines.Where(MachineGroup.IsChestLikeMachine).ToArray();

            // a non-chest-like machine (e.g. a Furnace) can automate as long as SOME container in the
            // group is currently reachable by the standard machine cycle — a disabled category isn't.
            if (chestLikeMachines.Length < this.Machines.Length && this.Containers.Any(this.StorageManager.IsContainerCategoryEnabled))
                return true;

            // a disabled chest-like machine never initiates its own movement, so it can't be the one
            // making the group active — but it can still be reached by a different, enabled one below.
            IContainer[] chestLikeOwnContainers = chestLikeMachines
                .Select(MachineGroup.GetChestLikeOwnContainer)
                .Where(container => container != null)
                .Select(container => container!)
                .ToArray();
            IContainer[] enabledActiveMovers = chestLikeOwnContainers.Where(this.StorageManager.IsContainerCategoryEnabled).ToArray();
            if (enabledActiveMovers.Length == 0)
                return false;

            // a real (non-self-owned) container is always reachable by every ENABLED chest-like
            // machine here — none of them exclude a container just for being a plain chest, regardless
            // of that container's own category; they only defer to ANOTHER active mover of
            // equal-or-better priority (handled below).
            HashSet<object> ownChestLikeInventoryIds = new(chestLikeOwnContainers.Select(container => container.InventoryReferenceId));
            if (this.Containers.Any(p => !ownChestLikeInventoryIds.Contains(p.InventoryReferenceId)))
                return true;

            // otherwise, the only containers present are the chest-like machines' own storage —
            // reachable by each other only if an enabled mover has some OTHER chest-like machine (any
            // category, enabled or not — it's just a target here) with a STRICTLY worse priority
            // (higher tier number) than its own, since an active mover defers to anything of
            // equal-or-better priority (see ContainerExtensions.ShouldDeferToAsActiveMover).
            return enabledActiveMovers.Any(mover => chestLikeOwnContainers.Any(target =>
                !target.InventoryReferenceId.Equals(mover.InventoryReferenceId)
                && target.GetContainerPriorityTier() > mover.GetContainerPriorityTier()));
        }
    }

    /// <summary>MOD: added. Unwrap a <see cref="MachineWrapper"/> to the real machine instance it wraps, if needed.</summary>
    /// <param name="machine">The machine to unwrap.</param>
    private static IMachine Unwrap(IMachine machine)
    {
        return machine is MachineWrapper wrapper ? wrapper.Machine : machine;
    }

    /// <summary>Get whether a machine is "chest-like" (see <see cref="IChestLikeMachine"/>), unwrapping a <see cref="MachineWrapper"/> if needed.</summary>
    /// <param name="machine">The machine to check.</param>
    private static bool IsChestLikeMachine(IMachine machine)
    {
        return MachineGroup.Unwrap(machine) is IChestLikeMachine;
    }

    /// <summary>MOD: added. Get a chest-like machine's own self-registered storage (unwrapping a <see cref="MachineWrapper"/> if needed), or <c>null</c> if the machine isn't chest-like or doesn't also implement <see cref="IContainer"/>.</summary>
    /// <param name="machine">The machine to check.</param>
    private static IContainer? GetChestLikeOwnContainer(IMachine machine)
    {
        IMachine actual = MachineGroup.Unwrap(machine);
        return actual is IChestLikeMachine && actual is IContainer container ? container : null;
    }


    /*********
    ** Public methods
    *********/
    /// <summary>Create an instance.</summary>
    /// <param name="locationKey">The main location containing the group (as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>).</param>
    /// <param name="machines">The machines in the group.</param>
    /// <param name="containers">The containers in the group.</param>
    /// <param name="tiles">The tiles comprising the group.</param>
    /// <param name="buildStorage">Build a storage manager for the given containers.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="connectorRoles">MOD: added. The connector role for each connector tile covered by this group, if any.</param>
    /// <param name="signMarkers">MOD: added. Debug markers for tiles where a configured sign was detected, regardless of whether it currently holds an item.</param>
    /// <param name="signCandidateTiles">MOD: added. Every tile where a configured whitelist/blacklist sign object exists, regardless of whether it currently holds an item.</param>
    /// <param name="powerStarvedTiles">MOD: added. The tiles of every machine that's currently power-starved (see <see cref="PowerStarvedTiles"/>).</param>
    public MachineGroup(string? locationKey, IEnumerable<IMachine> machines, IEnumerable<IContainer> containers, IEnumerable<Vector2> tiles, Func<IContainer[], StorageManager> buildStorage, IMonitor monitor, IReadOnlyDictionary<Vector2, ConnectorRole>? connectorRoles = null, IReadOnlyDictionary<Vector2, bool>? signMarkers = null, IReadOnlySet<Vector2>? signCandidateTiles = null, IReadOnlySet<Vector2>? powerStarvedTiles = null)
    {
        this.LocationKey = locationKey;
        this.Machines = machines.ToArray();
        this.Containers = containers.ToArray();
        this.Tiles = [.. tiles];
        this.Monitor = monitor;
        this.ConnectorRoles = connectorRoles != null ? new Dictionary<Vector2, ConnectorRole>(connectorRoles) : []; // MOD: added
        this.SignMarkers = signMarkers != null ? new Dictionary<Vector2, bool>(signMarkers) : []; // MOD: added
        this.SignCandidateTiles = signCandidateTiles != null ? new HashSet<Vector2>(signCandidateTiles) : []; // MOD: added
        this.PowerStarvedTiles = powerStarvedTiles != null ? new HashSet<Vector2>(powerStarvedTiles) : []; // MOD: added

        this.IsJunimoGroup = this.Containers.Any(p => p.IsJunimoChest);

        // MOD: no longer wraps in a separate FilteredStorage — each container is already wrapped in
        // an ItemFilteredContainer (see MachineGroupBuilder.Add) whenever a sign filter applies, which
        // enforces both the type filter and (for numeric sign conditions) per-container quantity
        // clamps directly, so the StorageManager built from them is already correctly filtered.
        this.StorageManager = buildStorage(this.GetUniqueContainers(this.Containers));
    }

    /// <inheritdoc />
    public virtual IReadOnlySet<Vector2> GetTiles(string locationKey)
    {
        return this.LocationKey == locationKey
            ? this.Tiles
            : ImmutableHashSet<Vector2>.Empty;
    }

    /// <inheritdoc />
    /// MOD: added.
    public virtual IReadOnlySet<Vector2> GetPowerStarvedTiles(string locationKey)
    {
        return this.LocationKey == locationKey
            ? this.PowerStarvedTiles
            : ImmutableHashSet<Vector2>.Empty;
    }

    /// <inheritdoc />
    /// MOD: added.
    public virtual IReadOnlyDictionary<Vector2, ConnectorRole> GetConnectorRoles(string locationKey)
    {
        return this.LocationKey == locationKey
            ? this.ConnectorRoles
            : ImmutableDictionary<Vector2, ConnectorRole>.Empty;
    }

    /// <inheritdoc />
    /// MOD: added.
    public virtual IReadOnlyDictionary<Vector2, bool> GetSignMarkers(string locationKey)
    {
        return this.LocationKey == locationKey
            ? this.SignMarkers
            : ImmutableDictionary<Vector2, bool>.Empty;
    }

    /// <inheritdoc />
    /// MOD: added.
    public virtual IReadOnlySet<Vector2> GetSignCandidateTiles(string locationKey)
    {
        return this.LocationKey == locationKey
            ? this.SignCandidateTiles
            : ImmutableHashSet<Vector2>.Empty;
    }

    /// <inheritdoc />
    public void Automate()
    {
        IStorage storage = this.StorageManager;

        // if a chest is locked (e.g. player has it open), pause machines to avoid losing items in update collisions
        if (storage.HasLockedContainers())
            return;

        // clear expired timers
        double curTime = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        if (this.MachinePauseExpiries.Count > 0)
        {
            IMachine[] expired = this.MachinePauseExpiries.Where(p => curTime >= p.Value).Select(p => p.Key).ToArray();
            foreach (IMachine machine in expired)
                this.MachinePauseExpiries.Remove(machine);
        }
        if (this.OutputPauseExpiries.Count > 0)
        {
            string[] expired = this.OutputPauseExpiries.Where(p => curTime >= p.Value).Select(p => p.Key).ToArray();
            foreach (string itemId in expired)
                this.OutputPauseExpiries.Remove(itemId);
        }

        // get machines ready for input/output
        List<IMachine> outputReady = this.PooledOutputReady;
        List<IMachine> inputReady = this.PooledInputReady;
        outputReady.Clear();
        inputReady.Clear();
        foreach (IMachine machine in this.Machines)
        {
            if (this.MachinePauseExpiries.ContainsKey(machine))
                continue;

            switch (machine.GetState())
            {
                case MachineState.Done:
                    outputReady.Add(machine);
                    break;

                case MachineState.Empty:
                    inputReady.Add(machine);
                    break;
            }
        }
        if (!outputReady.Any() && !inputReady.Any())
            return;

        // process output
        foreach (IMachine machine in outputReady)
        {
            ITrackedStack? output = null;
            try
            {
                // get output
                output = machine.GetOutput();
                if (output is null)
                {
                    if (machine.GetState() is MachineState.Empty)
                        inputReady.Add(machine);
                    continue;
                }

                // check if ignored
                string outputKey = $"{output.Type}:{output.Sample.ParentSheetIndex}";
                if (this.OutputPauseExpiries.ContainsKey(outputKey))
                    continue;

                // try to push output
                if (this.TryPushOutput(machine, storage, output))
                {
                    if (machine.GetState() is MachineState.Empty)
                        inputReady.Add(machine);
                    continue;
                }

                // ignore output that can't be stored in chest
                this.OutputPauseExpiries[outputKey] = curTime + this.OutputPauseMilliseconds;
            }
            catch (Exception ex)
            {
                string action;
                if (output == null)
                    action = "retrieving its output";
                else
                {
                    action = $"storing its output item {output.Sample.QualifiedItemId} ('{output.Sample.Name}'";
                    if (output.Sample is SObject outputObj && CommonHelper.IsItemId(outputObj.preservedParentSheetIndex.Value))
                        action += $", preserved item {outputObj.preservedParentSheetIndex.Value}";
                    action += ")";
                }

                this.OnMachineCrashed(machine, action, curTime, ex);
            }
        }

        // process input
        HashSet<string> ignoreMachines = this.PooledIgnoreMachinesForInput;
        ignoreMachines.Clear();
        foreach (IMachine machine in inputReady)
        {
            if (ignoreMachines.Contains(machine.MachineTypeID))
                continue;

            // MOD: added — skip a "power-required" machine (see PowerRequiredMachineSystem) entirely
            // while its own tile is out of power range — the same idea as vanilla's "missing coal"
            // message for a Furnace, just for power instead of a secondary ingredient. The reminder
            // message itself is handled separately (see PowerRequiredMachineSystem.ProcessStarvedMachineCallouts)
            // rather than here — this used to throttle its own "Machine needs power" message per
            // machine INSTANCE, but MachineGroup instances get rebuilt on every rescan (no stable
            // identity across rebuilds), which reset that throttle far more often than intended,
            // showing the message way more frequently than the throttle was meant to allow.
            if (this.PowerStarvedTiles.Count > 0 && machine.TileArea.GetTiles().Any(this.PowerStarvedTiles.Contains))
                continue;

            try
            {
                // MOD: added the IsChestLikeMachine exemption — this "skip every instance of the type"
                // optimization assumes machines sharing a MachineTypeID are interchangeable (if one
                // Furnace can't find coal+ore, no other Furnace will either, since they all draw from
                // the same shared group storage). That assumption breaks for a chest-like machine like
                // PoweredChestMachine: each instance has its OWN destination inventory (and its own
                // distance to a whitelist sign's numeric cap), so one powered chest returning "nothing
                // to do" (e.g. because IT already hit the cap) says nothing about whether a different
                // powered chest in the same group still has room. Without this exemption, whichever
                // powered chest happened to be full (or otherwise idle) first in iteration order would
                // silently block every other powered chest in the group for the rest of the tick.
                if (!machine.SetInput(storage) && !MachineGroup.IsChestLikeMachine(machine))
                    ignoreMachines.Add(machine.MachineTypeID); // if the machine can't process available input, no need to ask every instance of its type
            }
            catch (Exception ex)
            {
                this.OnMachineCrashed(machine, "setting its input", curTime, ex);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// MOD: added/changed. Mirrors <see cref="Automate"/>'s per-machine output body rather than sharing a
    /// single extracted method with it (the group-wide loop has its own <c>outputReady</c>/<c>inputReady</c>
    /// batching context this doesn't need), but reuses the exact same private helpers/state
    /// (<see cref="TryPushOutput"/>, <see cref="OnMachineCrashed"/>, <see cref="MachinePauseExpiries"/>,
    /// <see cref="OutputPauseExpiries"/>) so a machine handled this way is throttled/paused identically to
    /// one found by the full scan.
    ///
    /// Deliberately does NOT chain an immediate re-feed when the machine ends up <see cref="MachineState.Empty"/>
    /// afterward (an earlier version of this method did) — see <see cref="TryFeedMachineInput"/>'s own
    /// remarks for why feeding needs to be its own separately-scheduled event instead.
    /// </remarks>
    /// <param name="machine">The machine to push output from.</param>
    /// <returns>Whether the machine ended up <see cref="MachineState.Empty"/> as a result (i.e. ready for new input).</returns>
    public bool TryPushMachineOutput(IMachine machine)
    {
        IStorage storage = this.StorageManager;
        if (storage.HasLockedContainers() || this.MachinePauseExpiries.ContainsKey(machine))
            return false;

        // still call the real GetState() rather than trusting the raw vanilla flag that triggered this
        // call — some machine types have extra state logic beyond heldObject/readyForHarvest (e.g.
        // DataBasedObjectMachine's incubator special case, which defers Done until the egg hatches)
        if (machine.GetState() != MachineState.Done)
            return false;

        double curTime = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        ITrackedStack? output = null;
        try
        {
            output = machine.GetOutput();
            if (output is null)
                return machine.GetState() is MachineState.Empty;

            string outputKey = $"{output.Type}:{output.Sample.ParentSheetIndex}";
            if (this.OutputPauseExpiries.ContainsKey(outputKey))
                return false;

            if (this.TryPushOutput(machine, storage, output))
                return machine.GetState() is MachineState.Empty;

            this.OutputPauseExpiries[outputKey] = curTime + this.OutputPauseMilliseconds;
            return false;
        }
        catch (Exception ex)
        {
            string action = output == null
                ? "retrieving its output"
                : $"storing its output item {output.Sample.QualifiedItemId} ('{output.Sample.Name}')";
            this.OnMachineCrashed(machine, action, curTime, ex);
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// MOD: added. The input-side counterpart to <see cref="TryPushMachineOutput"/> — split into its own
    /// method (rather than an immediate chained call right after a successful push, as an earlier version
    /// of this class did) specifically so feeding a machine can be scheduled as its OWN independent,
    /// separately-delayed event. Confirmed, via direct user feedback, that chaining the two together
    /// instantly defeated the point of <see cref="Models.ModConfig.EventBasedPushPullDelaySeconds"/>: a
    /// push immediately followed by an undelayed pull looked identical to one instantaneous action instead
    /// of two independently-paced ones.
    ///
    /// MOD: fixed — now returns <see cref="IMachine.SetInput"/>'s own result instead of discarding it. A
    /// caller inferring "did this do anything" by comparing <see cref="IMachine.GetState"/> before and after
    /// would silently misclassify every real action by a <see cref="IChestLikeMachine"/> (i.e. a
    /// <see cref="Machines.Objects.PoweredChestMachine"/>) as a no-op — its <c>GetState</c> is hardcoded to
    /// always report <see cref="MachineState.Empty"/>, so the before/after states are always equal even when
    /// <see cref="IMachine.SetInput"/> genuinely moved items. Returning the real result directly avoids that
    /// trap for any caller that needs to know whether a commit actually happened.
    /// </remarks>
    /// <param name="machine">The machine to feed.</param>
    public bool TryFeedMachineInput(IMachine machine)
    {
        IStorage storage = this.StorageManager;
        if (storage.HasLockedContainers() || this.MachinePauseExpiries.ContainsKey(machine))
            return false;

        if (machine.GetState() != MachineState.Empty)
            return false;

        // MOD: added — same "power-required machine out of power range" gate Automate()'s own input loop
        // applies (see its own remarks); without this check, a power-starved machine could get fed here,
        // bypassing the power requirement entirely.
        if (this.PowerStarvedTiles.Count > 0 && machine.TileArea.GetTiles().Any(this.PowerStarvedTiles.Contains))
            return false;

        double curTime = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        try
        {
            return machine.SetInput(storage);
        }
        catch (Exception ex)
        {
            this.OnMachineCrashed(machine, "setting its input", curTime, ex);
            return false;
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get container instances, ensuring that only one container instance is returned for each shared inventory.</summary>
    /// <param name="containers">The containers to filter.</param>
    protected IContainer[] GetUniqueContainers(IEnumerable<IContainer> containers)
    {
        HashSet<object> seenInventories = new(new ObjectReferenceComparer<object>());

        return containers
            .Where(container => seenInventories.Add(container.InventoryReferenceId))
            .ToArray();
    }

    /// <summary>Add the given item stack to the output pipe if there's space.</summary>
    /// <param name="machine">The machine which produced the output.</param>
    /// <param name="storage">The output storage receiving the output.</param>
    /// <param name="output">The item stack to push.</param>
    /// <returns>Returns whether at least some of the item stack was received.</returns>
    private bool TryPushOutput(IMachine machine, IStorage storage, ITrackedStack output)
    {
        int stackSize = output.Count;

        // valid input
        if (storage.TryPush(output))
            return true;

        // special case: machine produced a zero-size stack
        if (stackSize < 1)
        {
            this.Monitor.Log($"Machine '{machine.MachineTypeID}' at {machine.Location.Name} (tile: {machine.TileArea.X}, {machine.TileArea.Y}) produced an item with ID '{output.Sample.QualifiedItemId}' and {(stackSize == 0 ? "no stack size" : $"stack size {stackSize}")}, so the output will be discarded. This is generally due to another mod breaking machine logic, and isn't related to Automate.", LogLevel.Warn);
            output.Take(1); // trigger on-empty callback
            return true;
        }

        return false;
    }

    /// <summary>Handle a machine which threw an exception during processing.</summary>
    /// <param name="machine">The machine which threw an exception.</param>
    /// <param name="action">A human-readable phrase indicating what the machine was doing when it failed (like "setting its input").</param>
    /// <param name="curTime">The current game time in milliseconds, used to set the machine's pause expiry.</param>
    /// <param name="exception">The exception that was thrown.</param>
    private void OnMachineCrashed(IMachine machine, string action, double curTime, Exception exception)
    {
        this.Monitor.Log(
            $"Failed to automate machine '{machine.MachineTypeID}' at {machine.Location.Name} (tile: {machine.TileArea.X}, {machine.TileArea.Y}). An error occurred while {action}. Machine paused for {this.MachinePauseMilliseconds / 1000}s.\n{exception}",
            LogLevel.Error
        );

        this.MachinePauseExpiries[machine] = curTime + this.MachinePauseMilliseconds;
    }
}

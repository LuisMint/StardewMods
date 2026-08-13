using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using Pathoschild.Stardew.Common;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Objects;

/// <summary>
/// MOD: added. A custom chest that's also its own machine: it behaves like a normal chest for other
/// machines in its group (delegating every <see cref="IContainer"/> member to its own real
/// <see cref="ChestContainer"/>), but additionally pulls from/pushes into NEIGHBORING containers
/// reached through a one-directional connector — something a plain chest can't do on its own, since
/// only an active mover moves items on its own initiative in Automate. See the mod's own remarks on the
/// group's <c>ConnectorRole</c>/<see cref="IConnectionRoleRestriction"/> system for why a
/// one-directional connector (vs. a bidirectional Pull&amp;Push one) is what makes this safe from an
/// infinite loop.
///
/// MOD: changed. An Input Pipe is always this chest's own pull source and an Output Pipe is always its
/// own push destination — see <see cref="SetInput"/>'s own remarks for why that's the OPPOSITE of what
/// those same roles mean to the standard machine cycle (a real machine's own output/input), and why
/// that opposite meaning is exactly what makes a given pipe feel consistent regardless of whether the
/// other end is a real machine or a plain container.
/// </summary>
internal class PoweredChestMachine : BaseMachine, IContainer, IChestLikeMachine, IHasContainerPriority, IHasUnderlyingChest
{
    /*********
    ** Fields
    *********/
    /// <summary>The qualified item ID of this custom chest.</summary>
    public const string QualifiedItemId = "(BC)luisMint.PoweredAutomation_PoweredChest";

    /// <summary>The chest's own real storage container.</summary>
    private readonly IContainer OwnContainer;

    /// <summary>
    /// MOD: added. Get whether this chest is currently allowed to push/pull items through its own
    /// piped connectors at all — see <see cref="Models.ModConfig.PoweredChestsCanAutomate"/>. Doesn't
    /// affect whether this chest's own storage is reachable by the STANDARD machine push/pull cycle (a
    /// Furnace's output/input, etc.), which is gated separately and doesn't go through
    /// <see cref="SetInput"/> at all — see <see cref="MachineManager.IsContainerCategoryEnabled"/>.
    /// </summary>
    private readonly Func<bool> IsEnabled;

    /// <summary>MOD: added, per direct request. Get the effective <c>ActionsPerDelayWindow</c> (after any Power Relay bonus) — see <see cref="SetInput"/>'s own remarks for why this chest's own single paced turn needs it.</summary>
    private readonly Func<int> GetEffectiveActionsPerDelayWindow;

    /// <summary>MOD: added, temporary diagnostic. Encapsulates monitoring and logging (may be <c>null</c>, in which case the diagnostic logging below is skipped) — see the <c>[containersync]</c>-tagged Trace logging in <see cref="SetInput"/>/<see cref="TryMoveOne"/>, added per direct user report of a cross-group pull-conduit delay that survived the first fix attempt.</summary>
    private readonly IMonitor? Monitor;


    /*********
    ** Accessors (IContainer, delegated to the real chest container)
    *********/
    /// <inheritdoc />
    public string TypeId => this.OwnContainer.TypeId;

    /// <inheritdoc />
    public string Name => this.OwnContainer.Name;

    /// <inheritdoc />
    public ModDataDictionary ModData => this.OwnContainer.ModData;

    /// <inheritdoc />
    public bool IsJunimoChest => this.OwnContainer.IsJunimoChest;

    /// <inheritdoc />
    public bool IsLocked => this.OwnContainer.IsLocked;

    /// <inheritdoc />
    public object InventoryReferenceId => this.OwnContainer.InventoryReferenceId;

    /// <inheritdoc />
    public IInventory Inventory => this.OwnContainer.Inventory;

    /// <summary>MOD: added. A Powered Chest is deprioritized relative to a real chest, but still preferred over a chest-backed hybrid — see <see cref="IHasContainerPriority"/>'s own remarks.</summary>
    /// <inheritdoc />
    public int ContainerPriorityTier => ContainerPriorityTiers.PoweredChest;

    /// <inheritdoc />
    public Chest? UnderlyingChest => this.OwnContainer.GetUnderlyingChest();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="chest">The underlying chest.</param>
    /// <param name="location">The location which contains the machine.</param>
    /// <param name="tile">The tile covered by the machine.</param>
    /// <param name="isEnabled">MOD: added. Get whether this chest is currently allowed to push/pull items through its own piped connectors at all, or <c>null</c> to always allow it — see <see cref="IsEnabled"/>.</param>
    /// <param name="getEffectiveActionsPerDelayWindow">MOD: added, per direct request. Get the effective <c>ActionsPerDelayWindow</c> (after any Power Relay bonus), or <c>null</c> to always do exactly one transfer per turn — see <see cref="GetEffectiveActionsPerDelayWindow"/>.</param>
    /// <param name="monitor">MOD: added, temporary diagnostic. Encapsulates monitoring and logging, or <c>null</c> to disable it — see <see cref="Monitor"/>.</param>
    public PoweredChestMachine(Chest chest, GameLocation location, Vector2 tile, Func<bool>? isEnabled = null, Func<int>? getEffectiveActionsPerDelayWindow = null, IMonitor? monitor = null)
        : base(location, BaseMachine.GetTileAreaFor(tile), BaseMachine.GetDefaultMachineId<PoweredChestMachine>())
    {
        this.OwnContainer = new ChestContainer(chest, location, tile, migrateLegacyOptions: false);
        this.IsEnabled = isEnabled ?? (() => true);
        this.GetEffectiveActionsPerDelayWindow = getEffectiveActionsPerDelayWindow ?? (() => 1);
        this.Monitor = monitor;
    }

    /// <summary>
    /// MOD: added. Always reports empty, so <see cref="SetInput"/> runs every automation tick — all
    /// the actual movement (both directions) happens there instead of the usual
    /// <see cref="GetOutput"/>/<c>storage.TryPush</c> cycle, since that cycle has no way to exclude
    /// specific destinations (like itself, or another Powered Chest) from receiving output.
    /// </summary>
    /// <inheritdoc />
    public override MachineState GetState() => MachineState.Empty;

    /// <summary>MOD: added. Always empty — see <see cref="GetState"/>'s remarks for why all movement happens in <see cref="SetInput"/> instead.</summary>
    /// <inheritdoc />
    public override ITrackedStack? GetOutput() => null;

    /// <summary>
    /// Pull from this chest's own connectors marked as its pull source, then push this chest's own
    /// items into its own connectors marked as its push destination — skipping itself and any other
    /// Powered Chest, and skipping any container reached through a bidirectional (or unrestricted)
    /// connector entirely, to avoid an infinite loop between two chests that both actively move items.
    ///
    /// MOD: changed. Reads <see cref="IStorage.AllContainers"/> instead of <see cref="IStorage.InputContainers"/>/
    /// <see cref="IStorage.OutputContainers"/>, using <see cref="ContainerExtensions.IsActiveMoverPullSource"/>/
    /// <see cref="ContainerExtensions.IsActiveMoverPushDestination"/> instead of <see cref="ContainerExtensions.IsPullOnlyConnection"/>/
    /// <see cref="ContainerExtensions.IsPushOnlyConnection"/> — by design, these interpret the SAME
    /// underlying connector role in the OPPOSITE direction from the standard machine cycle: an Input
    /// Pipe is this chest's own pull source (so it reaches out and takes from whatever it's connected
    /// to), not a place to push its own contents into. This is what makes an Input Pipe consistently
    /// mean "flows into this chest" whether the other end is a real machine (which pushes its own
    /// output there via the ordinary <see cref="StorageManager.TryPush"/> cycle, unaffected by any of
    /// this) or a plain chest (which this chest has to actively reach into, since a plain chest never
    /// acts on its own) — before this, the two cases pointed in opposite directions for the exact same
    /// pipe, which was confusing to reason about. <see cref="IStorage.InputContainers"/>/<see cref="IStorage.OutputContainers"/>
    /// can't be reused for this: they already exclude a container reached the "wrong" way for the
    /// standard cycle's own interpretation, so this chest would never even see it as a candidate.
    /// </summary>
    /// <inheritdoc />
    public override bool SetInput(IStorage input)
    {
        if (!this.IsEnabled())
            return false;

        // MOD: fixed — read from and store into whichever instance of THIS chest appears in
        // input.AllContainers (its sign-filtered ItemFilteredContainer wrapper, if this group has a
        // whitelist/blacklist sign), not directly through the raw OwnContainer. Going straight to
        // OwnContainer bypassed the sign's numeric condition entirely, since it's enforced by
        // ItemFilteredContainer.Store/GetEnumerator, not by anything on the underlying ChestContainer
        // itself: a whitelist of 10 would otherwise keep pulling 10 more every tick forever instead of
        // stopping once the chest already has 10, and a blacklist reserve of 10 would push this chest's
        // ENTIRE stock out instead of only the excess above 10. Unlike the InputContainers/OutputContainers
        // split this used to search, AllContainers isn't narrowed by connector role, so this chest's own
        // wrapped self always shows up in it exactly once regardless of which role it's reached through.
        IContainer selfContainer =
            Array.Find(input.AllContainers, c => c.InventoryReferenceId.Equals(this.OwnContainer.InventoryReferenceId))
            ?? this.OwnContainer;

        // MOD: changed, per direct request — do at most ActionsPerDelayWindow individual transfers per
        // call (pulling takes priority over pushing on each one) instead of draining every reachable
        // connection in one shot. This one call is already this chest's own single paced "turn" in its
        // group's ActionDelaySeconds/ActionsPerDelayWindow batch (see ModEntry.RunGroupBatch), so doing
        // UNBOUNDED work inside it meant fanning out to, say, 5 connected chests always moved into all 5
        // at once regardless of the pacing settings — but capping it to exactly one regardless of the
        // configured budget (an earlier version of this fix) undercorrected: raising ActionsPerDelayWindow
        // had no effect on how much a Powered Chest itself could move per turn. Looping up to that same
        // budget here is what makes "5 actions per window" actually mean "up to 5 chunks moved" for a
        // Powered Chest, exactly like it already does for how many separate MACHINES get serviced per
        // window. 0 or less means unlimited, matching RunGroupBatch's own convention — reverting to
        // "drain everything reachable in one call" when the player has explicitly configured no cap at
        // all. No round-robin bookkeeping is needed for fairness across MULTIPLE destinations sharing
        // one budget: a destination that's already at its whitelist/blacklist cap simply moves nothing
        // on that attempt, so the loop falls through to the next candidate in the same call.
        int budget = this.GetEffectiveActionsPerDelayWindow();
        bool unlimitedBudget = budget <= 0;

        if (this.Monitor != null)
        {
            // MOD: added, temporary diagnostic — list every reachable container's tile, not just the count,
            // so a run can be checked against whether the intended pull source is even a MEMBER of this
            // group's own storage at all (a topology/role gap) as opposed to being a member but never
            // getting a scheduling turn (a pacing gap) — the two look identical from the count alone.
            string tileList = string.Join(", ", input.AllContainers.Select(c => $"({c.TileArea.X},{c.TileArea.Y})"));
            this.Monitor.Log($"[containersync] PoweredChestMachine.SetInput at {this.Location.Name} ({this.TileArea.X},{this.TileArea.Y}): {input.AllContainers.Length} reachable container(s) [{tileList}], budget={(unlimitedBudget ? "unlimited" : budget.ToString())}", LogLevel.Trace);
        }

        bool movedAnything = false;
        for (int movedThisCall = 0; unlimitedBudget || movedThisCall < budget; movedThisCall++)
        {
            if (!this.TryMoveOne(input, selfContainer, isPull: true) && !this.TryMoveOne(input, selfContainer, isPull: false))
                break; // nothing left to move this call — no point spinning through the rest of the budget

            movedAnything = true;
        }

        this.Monitor?.Log($"[containersync] PoweredChestMachine.SetInput at {this.Location.Name} ({this.TileArea.X},{this.TileArea.Y}): movedAnything={movedAnything}", LogLevel.Trace); // MOD: added, temporary diagnostic
        return movedAnything;
    }

    /// <summary>Attempt exactly one individual transfer — either pulling from a connected pull-source into this chest, or pushing from this chest into a connected push-destination — stopping as soon as one actually moves something.</summary>
    /// <param name="input">The full set of containers reachable from this chest's own connectors.</param>
    /// <param name="selfContainer">This chest's own (possibly sign-filtered) container instance — see <see cref="SetInput"/>'s own remarks for why it's resolved from <paramref name="input"/> rather than used directly.</param>
    /// <param name="isPull">Whether to attempt a pull (into this chest) rather than a push (out of this chest).</param>
    private bool TryMoveOne(IStorage input, IContainer selfContainer, bool isPull)
    {
        foreach (IContainer container in input.AllContainers)
        {
            bool eligible = isPull
                ? container.TakingItemsAllowed() && container.IsActiveMoverPullSource()
                : container.StorageAllowed() && container.IsActiveMoverPushDestination();

            bool shouldSkip = this.ShouldSkip(container);
            bool isOwnLocalTouchpoint = this.IsOwnLocalTouchpoint(container);
            if (shouldSkip || !eligible || !isOwnLocalTouchpoint)
            {
                // MOD: added, temporary diagnostic — only log a rejected candidate for the pull direction,
                // since that's the direction the reported bug is about (a whitelist-filtered pull conduit
                // not picking up a valid item promptly); logging every push candidate too would double the
                // volume without adding anything relevant to that report.
                if (isPull)
                    this.Monitor?.Log($"[containersync]   TryMoveOne(pull) skip candidate at ({container.TileArea.X},{container.TileArea.Y}): shouldSkip={shouldSkip} eligible={eligible} isOwnLocalTouchpoint={isOwnLocalTouchpoint}", LogLevel.Trace);
                continue;
            }

            IContainer source = isPull ? container : selfContainer;
            IContainer destination = isPull ? selfContainer : container;

            bool sawAnyStack = false;
            foreach (ITrackedStack stack in source.ToArray())
            {
                sawAnyStack = true;
                if (stack.Count <= 0)
                    continue;

                int before = stack.Count;
                destination.Store(stack);
                if (stack.Count < before)
                {
                    if (isPull)
                        this.Monitor?.Log($"[containersync]   TryMoveOne(pull) moved {before - stack.Count}x {stack.Sample.QualifiedItemId} from ({container.TileArea.X},{container.TileArea.Y})", LogLevel.Trace); // MOD: added, temporary diagnostic
                    return true; // one action done this turn — the rest waits for a later turn
                }
            }

            if (isPull)
                this.Monitor?.Log($"[containersync]   TryMoveOne(pull) candidate at ({container.TileArea.X},{container.TileArea.Y}) eligible but moved nothing (sawAnyStack={sawAnyStack})", LogLevel.Trace); // MOD: added, temporary diagnostic
        }

        return false;
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count) => this.OwnContainer.Get(predicate, count);

    /// <inheritdoc />
    public void Store(ITrackedStack stack) => this.OwnContainer.Store(stack);

    /// <inheritdoc />
    public int GetFilled() => this.OwnContainer.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.OwnContainer.GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator() => this.OwnContainer.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PoweredChestMachine other ? this.OwnContainer.Equals(other.OwnContainer) : this.OwnContainer.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.OwnContainer.GetHashCode();


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// Get whether a container should be excluded from this chest's own pull/push cycle — either
    /// because it's this same chest, or (MOD: changed) any active mover this Powered Chest should defer
    /// to as such (see <see cref="ContainerExtensions.ShouldDeferToAsActiveMover"/>) — currently, that's
    /// only another Powered Chest specifically (the same tier as this one), since nothing outranks a
    /// Powered Chest with a strictly better tier today; a plain chest never matches this at all, since
    /// it never has an action of its own to "double up" with in the first place. This used to be a
    /// dedicated <c>container.TypeId == PoweredChestMachine.QualifiedItemId</c> check; the tier-based
    /// comparison subsumes it (every Powered Chest shares the same tier) while also generalizing to any
    /// future higher-or-equal-priority active mover automatically.
    /// </summary>
    /// <param name="container">The container to check.</param>
    private bool ShouldSkip(IContainer container)
    {
        return
            container.InventoryReferenceId.Equals(this.OwnContainer.InventoryReferenceId)
            || container.ShouldDeferToAsActiveMover(this.ContainerPriorityTier);
    }

    /// <summary>
    /// MOD: added. Get whether a candidate container's role restriction actually belongs to THIS
    /// chest's own local connection, as opposed to an unrelated group's connection to the same
    /// shared Junimo inventory. Every Junimo chest on the farm shares one real inventory, and several
    /// differently-restricted local touchpoints to it can coexist in the farm-wide Junimo aggregate
    /// (see <see cref="JunimoTouchpointContainer"/>'s own remarks) — without this check, a Powered
    /// Chest connected to one Junimo chest via an unrestricted Pull&amp;Push pipe could "borrow" a
    /// pull-only or push-only role that actually belongs to a completely different Powered Chest's
    /// own connection to a different Junimo chest sharing the same inventory. Non-Junimo containers
    /// always return true — the ambiguity only exists for the shared Junimo inventory, since a
    /// regular chest's role restriction is never shared across unrelated groups in the first place.
    /// </summary>
    /// <param name="container">The container to check.</param>
    private bool IsOwnLocalTouchpoint(IContainer container)
    {
        return
            container is not JunimoTouchpointContainer touchpoint
            || this.TileArea.GetTiles().Any(touchpoint.OriginGroupTiles.Contains);
    }
}

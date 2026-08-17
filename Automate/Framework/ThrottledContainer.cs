using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps EVERY container in the mod (applied at the single choke point
/// every container passes through, <see cref="MachineGroupBuilder.Add(IContainer)"/>) to spread
/// deliveries into it across time instead of accepting everything a pusher offers in one shot — reusing
/// the SAME <c>ActionDelaySeconds</c>/<c>ActionsPerDelayWindow</c> pacing (Power-Relay-upgrade-aware,
/// via the <see cref="Func{TResult}"/> delegates passed in) that already paces machine automation
/// batches, generalizing the single-container prototype this started as (see
/// <see cref="Storage.ShippingBinContainer"/>'s own now-superseded <c>NextAcceptAllowedMs</c> cooldown).
///
/// Also triggers <see cref="ContainerVisualEffects"/> on both directions — an "entry" effect from
/// <see cref="Store"/>, and an "exit" effect observed generically by wrapping every <see cref="ITrackedStack"/>
/// this container's own enumeration yields (see <see cref="ExitEffectTrackedStack"/>), since
/// <see cref="IContainer.Get"/> is confirmed dead/unused as an actual pull mechanism anywhere in this
/// mod — every real removal, whether a machine consuming ingredients/fuel or another container actively
/// pulling items away, goes through plain enumeration instead. This is what lets the exit effect apply
/// uniformly to every removal without any per-machine wiring: a machine that isn't also an
/// <see cref="IContainer"/> never gets wrapped by this class in the first place, so "never show an
/// effect on a machine" falls out for free rather than needing an explicit guard.
///
/// The exit side is deliberately NOT paced by the window budget below — it only ever OBSERVES a
/// reduction that already happened, never withholds one, since a single
/// action/pull still takes everything it needs out in one shot, same as before this feature existed.
/// Both directions are deduped per (this container, item type, current window) — see
/// <see cref="AnimatedItemTypesThisWindow"/> — so many chunks of the same item type moving through one
/// container within a single window still only animate once, not once per chunk.
/// </summary>
internal class ThrottledContainer : IContainer, IConnectionRoleRestriction, IHasContainerPriority, IHasUnderlyingChest, IHasAttemptAutoLoad
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying container being wrapped.</summary>
    private readonly IContainer Inner;

    /// <summary>Get the effective <c>ActionDelaySeconds</c> (after any Power Relay bonus), in seconds. <c>0</c> or less means unthrottled/instant.</summary>
    private readonly Func<float> GetEffectiveActionDelaySeconds;

    /// <summary>Get the effective <c>ActionsPerDelayWindow</c> (after any Power Relay bonus). <c>0</c> or less means unlimited.</summary>
    private readonly Func<int> GetEffectiveActionsPerDelayWindow;

    /// <summary>Get whether the lid animation/jolt/item sprite/sound should play — never gates the throttling itself, only the visuals.</summary>
    private readonly Func<bool> GetVisualEffectsEnabled;

    /// <summary>
    /// MOD: added. Proactively wake every active group covering a tile whose
    /// container just changed — <c>(location, tile, isJunimoChest)</c>. Called on every successful
    /// entry/exit, unconditionally (never gated by <see cref="GetVisualEffectsEnabled"/> or whether the
    /// location is loaded — this is a correctness fix, not a visual one). Exists alongside SMAPI's own
    /// <c>ChestInventoryChanged</c> event because that path has no redundancy: a
    /// container reachable from MULTIPLE separate machine groups (its own feed is one group, a
    /// whitelist/category-filtered pull-conduit reading from it is a completely different group) could
    /// go unnoticed by that OTHER group until the periodic hourly backstop scan happened to catch it —
    /// the exact same class of gap <c>ModEntry.AutomateMachine</c>'s own cross-group nudge already closes
    /// for a MACHINE split across an input/output group, just never extended to a CONTAINER's own
    /// contents changing. Calling this directly from the storage layer, instead of only reacting to
    /// SMAPI's own event, means every group that should react gets a synchronous, reliable chance to —
    /// regardless of how SMAPI's own chest-diffing watcher happens to coalesce or schedule its event.
    /// </summary>
    private readonly Action<GameLocation, Vector2, bool> NotifyContainerChanged;

    /// <summary>
    /// MOD: added. The shared pacing state for the physical container this instance wraps, referenced (not
    /// owned) so every group's own wrapper for the same physical container stays in sync — see
    /// <see cref="ThrottledContainerBudget"/>'s own remarks for why this can't just be instance fields here.
    /// </summary>
    private readonly ThrottledContainerBudget Budget;

    /// <summary>
    /// MOD: added. The same real-time (not game-simulation-time), pause-aware
    /// elapsed-milliseconds clock <c>ModEntry</c>'s own group-level <c>ActionDelaySeconds</c> timer already
    /// uses (its <c>UnpausedElapsedMs</c> field). This used to read <see cref="Game1.currentGameTime"/>'s raw
    /// simulation-time clock directly, which isn't guaranteed to track real elapsed time closely under heavy
    /// load — confirmed via a <c>[containersync]</c> log from a farm with an 18-Furnace mega-group plus dozens
    /// of Mushroom Boxes, where a per-chest pacing window (same underlying bug class, in
    /// <see cref="Machines.Objects.PoweredChestMachine"/>, before that class's own now-removed budget was
    /// superseded by this shared one) went 14 real seconds between resets instead of the configured 7.
    /// </summary>
    private readonly Func<double> GetElapsedMs;


    /*********
    ** Accessors
    *********/
    /// <summary>
    /// MOD: added. Whether the MOST RECENT <see cref="Store"/> call on this specific wrapper was turned
    /// away purely because this window's shared budget was already spent — as opposed to accepted, or
    /// rejected by the underlying container itself (wrong item type, no space, etc.). Reset to <c>false</c>
    /// at the top of every <see cref="Store"/> call, so it always reflects that exact call by the time it
    /// returns — never a stale result from an earlier delivery. Lets callers several layers up (see
    /// <see cref="StorageManager.WasLastPushRejectedForBudget"/>) distinguish "this container will accept
    /// it again once the window rolls over" from "this container will never take it," which matters
    /// because a genuine rejection is deliberately remembered for a while (<c>MachineGroup.OutputPauseExpiries</c>)
    /// to avoid repeatedly offering doomed output, but a budget rejection is purely a rotation — mistakenly
    /// applying that same memory to it starves every OTHER sibling machine sharing this container's budget
    /// from even being offered a turn for the whole pause duration, confirmed via a diagnostic capture of
    /// several Mushroom Boxes sharing one Powered Chest never landing a single delivery across an 80-second
    /// window.
    /// </summary>
    public bool WasLastStoreRejectedForBudget { get; private set; }

    /// <inheritdoc cref="IAutomatable.Location" />
    public GameLocation Location => this.Inner.Location;

    /// <inheritdoc cref="IAutomatable.TileArea" />
    public Rectangle TileArea => this.Inner.TileArea;

    /// <inheritdoc />
    public string TypeId => this.Inner.TypeId;

    /// <inheritdoc />
    public string Name => this.Inner.Name;

    /// <inheritdoc />
    public ModDataDictionary ModData => this.Inner.ModData;

    /// <inheritdoc />
    public bool IsJunimoChest => this.Inner.IsJunimoChest;

    /// <inheritdoc />
    public bool IsLocked => this.Inner.IsLocked;

    /// <inheritdoc />
    public object InventoryReferenceId => this.Inner.InventoryReferenceId;

    /// <inheritdoc />
    public IInventory Inventory => this.Inner.Inventory;

    /// <summary>MOD: added. Forwards to the wrapped container's own restriction — see <see cref="Storage.ItemFilteredContainer.AllowStorageThroughThisConnection"/>'s own remarks for why this class (now the OUTERMOST wrapper) must keep forwarding it, or role restriction would silently stop applying to every container in the mod.</summary>
    public bool AllowStorageThroughThisConnection => this.Inner is not IConnectionRoleRestriction restriction || restriction.AllowStorageThroughThisConnection;

    /// <inheritdoc cref="AllowStorageThroughThisConnection" />
    public bool AllowTakingThroughThisConnection => this.Inner is not IConnectionRoleRestriction restriction || restriction.AllowTakingThroughThisConnection;

    /// <summary>MOD: added. Forwards to the wrapped container's own priority tier — see <see cref="AllowStorageThroughThisConnection"/>'s own remarks for why forwarding matters now that this is the outermost wrapper.</summary>
    /// <inheritdoc />
    public int ContainerPriorityTier => this.Inner.GetContainerPriorityTier();

    /// <inheritdoc />
    public Chest? UnderlyingChest => this.Inner.GetUnderlyingChest();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The underlying container being wrapped.</param>
    /// <param name="budget">MOD: added. The shared pacing state for the physical container being wrapped — see <see cref="ThrottledContainerBudget"/>.</param>
    /// <param name="getEffectiveActionDelaySeconds">Get the effective <c>ActionDelaySeconds</c> (after any Power Relay bonus), in seconds.</param>
    /// <param name="getEffectiveActionsPerDelayWindow">Get the effective <c>ActionsPerDelayWindow</c> (after any Power Relay bonus).</param>
    /// <param name="getElapsedMs">MOD: added. Get the real-time, pause-aware elapsed-milliseconds clock — see <see cref="GetElapsedMs"/>.</param>
    /// <param name="getVisualEffectsEnabled">Get whether the lid animation/jolt/item sprite/sound should play.</param>
    /// <param name="notifyContainerChanged">MOD: added. Proactively wake every active group covering a tile whose container just changed — see <see cref="NotifyContainerChanged"/>.</param>
    public ThrottledContainer(IContainer inner, ThrottledContainerBudget budget, Func<float> getEffectiveActionDelaySeconds, Func<int> getEffectiveActionsPerDelayWindow, Func<double> getElapsedMs, Func<bool> getVisualEffectsEnabled, Action<GameLocation, Vector2, bool> notifyContainerChanged)
    {
        this.Inner = inner;
        this.Budget = budget;
        this.GetEffectiveActionDelaySeconds = getEffectiveActionDelaySeconds;
        this.GetEffectiveActionsPerDelayWindow = getEffectiveActionsPerDelayWindow;
        this.GetElapsedMs = getElapsedMs;
        this.GetVisualEffectsEnabled = getVisualEffectsEnabled;
        this.NotifyContainerChanged = notifyContainerChanged;
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count) => this.Inner.Get(predicate, count);

    /// <summary>
    /// MOD: added — a vanilla data-driven machine (a Furnace, Keg, etc., via
    /// <see cref="Machines.DataBasedObjectMachine"/>) consuming ingredients never goes through
    /// <see cref="GetEnumerator"/>/<see cref="ExitEffectTrackedStack"/> at all: it reads straight from
    /// <see cref="Inventory"/> and mutates the input item's own <c>Stack</c> directly, bypassing every
    /// hook this class would otherwise use to notice a removal (see <see cref="IHasAttemptAutoLoad"/>'s
    /// own remarks). Since there's no per-item hook to intercept there, this snapshots each item ID's
    /// count before delegating to the wrapped container's own auto-load handling, then diffs afterward
    /// and fires the exit effect for whatever actually dropped — the same <see cref="OnRemoved"/> used
    /// by every other removal path, so it's still throttle-free, deduped, and slotted identically.
    /// </summary>
    /// <inheritdoc />
    public bool AttemptAutoLoad(SObject machine, Farmer who)
    {
        // MOD: added, for performance — the snapshot/diff below
        // exists ONLY to feed the exit effect, so skip building it entirely (not just skip playing the
        // effect afterward) when nothing could come of it: effects disabled, or nobody's in this
        // location to see it (see ContainerVisualEffects's own remarks). This is the one place in this
        // class where that's worth checking up front — everywhere else, the per-call cost of checking
        // is already cheaper than what it'd be skipping.
        bool trackConsumption = this.GetVisualEffectsEnabled() && this.Location.farmers.Any();
        Dictionary<string, int>? before = trackConsumption ? this.SnapshotItemCounts() : null;

        bool loaded = this.Inner is IHasAttemptAutoLoad inner
            ? inner.AttemptAutoLoad(machine, who)
            : machine.AttemptAutoLoad(this.Inner.Inventory, who);

        if (loaded && before != null)
        {
            foreach ((string itemId, int beforeCount) in before)
            {
                int consumed = beforeCount - this.Inner.Inventory.CountId(itemId);
                if (consumed > 0)
                    this.OnRemoved(ItemRegistry.Create(itemId, 1), consumed);
            }
        }

        return loaded;
    }

    /// <summary>
    /// MOD: added. Accepts at most <c>ActionsPerDelayWindow</c> chunks per <c>ActionDelaySeconds</c>
    /// window — rejecting (leaving the stack fully untouched) anything offered beyond that budget, so
    /// the caller naturally retries on a later automation pass. See this class's own remarks for why
    /// this generalizes <see cref="Storage.ShippingBinContainer"/>'s original prototype.
    /// </summary>
    /// <inheritdoc />
    public void Store(ITrackedStack stack)
    {
        // MOD: added. Reset up front so a caller checking this property right after Store() returns
        // always sees THIS call's outcome, never a stale value left over from some earlier delivery
        // attempt against this same container. See the property's own remarks for why callers need this.
        this.WasLastStoreRejectedForBudget = false;

        if (stack.Count <= 0)
            return;

        this.RollWindowIfNeeded();

        float delaySeconds = this.GetEffectiveActionDelaySeconds();
        if (delaySeconds > 0)
        {
            int actionsPerWindow = this.GetEffectiveActionsPerDelayWindow();
            if (actionsPerWindow > 0 && this.Budget.ActionsUsedThisWindow >= actionsPerWindow)
            {
                this.WasLastStoreRejectedForBudget = true;
                return; // budget exhausted this window — leave the stack untouched, caller retries later
            }
        }

        int before = stack.Count;
        this.Inner.Store(stack);
        int moved = before - stack.Count;
        if (moved <= 0)
            return;

        if (delaySeconds > 0)
            this.Budget.ActionsUsedThisWindow++;

        // MOD: added — see NotifyContainerChanged's own remarks for why this fires
        // unconditionally alongside SMAPI's own ChestInventoryChanged event, not instead of it.
        this.NotifyContainerChanged(this.Location, new Vector2(this.TileArea.X, this.TileArea.Y), this.IsJunimoChest);

        // MOD: added — some containers (the shipping bin) always play their own
        // vanilla-driven "item arrived" feedback regardless of automation, so layering the generic
        // effect on top would double up. See IHasOwnEntryEffect's own remarks.
        if (this.GetVisualEffectsEnabled() && !this.Inner.GetHasOwnEntryEffect() && this.Budget.AnimatedItemTypesThisWindow.Add(stack.Sample.QualifiedItemId))
            ContainerVisualEffects.PlayEntryEffect(this, stack.Sample, this.Budget.AnimatedItemTypesThisWindow.Count - 1);
    }

    /// <summary>
    /// MOD: added. Get whether this container's shared per-window budget has any capacity left, rolling
    /// the window over first if it's elapsed — WITHOUT consuming anything. Lets <see cref="Machines.Objects.PoweredChestMachine"/>
    /// check, before committing a PUSH to some OTHER container, whether ITS OWN receiving budget has
    /// already been spent this window — because a physical chest's "one action per window" has to be a
    /// single shared counter covering however it's involved (another machine storing INTO it, or this
    /// chest actively pulling into or pushing out of itself), not two independent counters that each
    /// separately allow one. Confirmed via a diagnostic trace: a Mushroom Box pushing its own output
    /// into a Powered Chest, and that SAME chest pushing a Dried Mushroom out to a completely different
    /// container (the Mini-Shipping Bin), landing in the same nominal window — each was checked against
    /// its own destination's budget in isolation, so both went through even though, from the player's
    /// perspective, this one physical chest visibly did two things in one window. See
    /// <see cref="ConsumeBudget"/> for the other half of tying the outgoing direction to this same counter.
    /// </summary>
    internal bool HasBudgetRemainingThisWindow()
    {
        this.RollWindowIfNeeded();

        float delaySeconds = this.GetEffectiveActionDelaySeconds();
        if (delaySeconds <= 0)
            return true;

        int actionsPerWindow = this.GetEffectiveActionsPerDelayWindow();
        return actionsPerWindow <= 0 || this.Budget.ActionsUsedThisWindow < actionsPerWindow;
    }

    /// <summary>
    /// MOD: added. Manually spend one unit of this container's own shared per-window budget for an
    /// action that doesn't itself call <see cref="Store"/> on this container — specifically,
    /// <see cref="Machines.Objects.PoweredChestMachine"/> pushing OUT of itself into a DIFFERENT
    /// container. That <see cref="Store"/> call lands on the OTHER container's own budget, not this
    /// one's, so without this, nothing would charge this physical chest's own counter for its outgoing
    /// half — see <see cref="HasBudgetRemainingThisWindow"/>'s own remarks for the full picture.
    /// </summary>
    internal void ConsumeBudget()
    {
        if (this.GetEffectiveActionDelaySeconds() > 0)
            this.Budget.ActionsUsedThisWindow++;
    }

    /// <inheritdoc />
    public int GetFilled() => this.Inner.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.Inner.GetCapacity();

    /// <summary>MOD: added. Wraps each yielded stack so an actual reduction (by anyone — a machine consuming it, or another container pulling it away) triggers the exit effect. See this class's own remarks for why this is the only place that can observe every removal generically.</summary>
    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator()
    {
        foreach (ITrackedStack stack in this.Inner)
            yield return new ExitEffectTrackedStack(stack, this);
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ThrottledContainer other ? this.Inner.Equals(other.Inner) : this.Inner.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Inner.GetHashCode();


    /*********
    ** Private methods
    *********/
    /// <summary>Start a fresh pacing window (resetting the budget and the animation dedup set) if the current one has elapsed.</summary>
    private void RollWindowIfNeeded()
    {
        double now = this.GetElapsedMs();
        float delaySeconds = this.GetEffectiveActionDelaySeconds();
        double windowMs = delaySeconds > 0 ? delaySeconds * 1000 : 0;

        if (now < this.Budget.WindowStartMs + windowMs)
            return;

        this.Budget.WindowStartMs = now;
        this.Budget.ActionsUsedThisWindow = 0;
        this.Budget.AnimatedItemTypesThisWindow.Clear();
    }

    /// <summary>Get the current count of every distinct item ID in the wrapped container's own inventory — see <see cref="AttemptAutoLoad"/>'s own remarks for why this is needed.</summary>
    private Dictionary<string, int> SnapshotItemCounts()
    {
        Dictionary<string, int> counts = new();

        foreach (Item? item in this.Inner.Inventory)
        {
            if (item == null || counts.ContainsKey(item.QualifiedItemId))
                continue;

            counts[item.QualifiedItemId] = this.Inner.Inventory.CountId(item.QualifiedItemId);
        }

        return counts;
    }

    /// <summary>Called when an actual reduction is observed on a stack yielded by <see cref="GetEnumerator"/> — rolls the pacing window if needed, then fires the (deduped) exit effect.</summary>
    /// <param name="sample">A sample of the item that was removed.</param>
    /// <param name="amountRemoved">How much was actually removed.</param>
    private void OnRemoved(Item sample, int amountRemoved)
    {
        if (amountRemoved <= 0)
            return;

        // MOD: added — see NotifyContainerChanged's own remarks. Covers BOTH removal
        // paths that reach this method: a container-to-container pull via ExitEffectTrackedStack, and a
        // vanilla data-driven machine's own ingredient consumption via AttemptAutoLoad's snapshot/diff.
        this.NotifyContainerChanged(this.Location, new Vector2(this.TileArea.X, this.TileArea.Y), this.IsJunimoChest);

        this.RollWindowIfNeeded();

        if (this.GetVisualEffectsEnabled() && this.Budget.AnimatedItemTypesThisWindow.Add(sample.QualifiedItemId))
            ContainerVisualEffects.PlayExitEffect(this, sample, this.Budget.AnimatedItemTypesThisWindow.Count - 1);
    }


    /*********
    ** Private types
    *********/
    /// <summary>Wraps a real <see cref="ITrackedStack"/> to notify its owning <see cref="ThrottledContainer"/> whenever it's actually reduced, without altering its behavior at all.</summary>
    private sealed class ExitEffectTrackedStack : ITrackedStack
    {
        /// <summary>The real underlying stack being wrapped.</summary>
        private readonly ITrackedStack Inner;

        /// <summary>The container to notify when this stack is reduced.</summary>
        private readonly ThrottledContainer Owner;

        /// <inheritdoc />
        public Item Sample => this.Inner.Sample;

        /// <inheritdoc />
        public string Type => this.Inner.Type;

        /// <inheritdoc />
        public int Count => this.Inner.Count;

        /// <summary>Construct an instance.</summary>
        /// <param name="inner">The real underlying stack being wrapped.</param>
        /// <param name="owner">The container to notify when this stack is reduced.</param>
        public ExitEffectTrackedStack(ITrackedStack inner, ThrottledContainer owner)
        {
            this.Inner = inner;
            this.Owner = owner;
        }

        /// <inheritdoc />
        public void Reduce(int count)
        {
            int before = this.Inner.Count;
            this.Inner.Reduce(count);
            this.Owner.OnRemoved(this.Inner.Sample, before - this.Inner.Count);
        }

        /// <inheritdoc />
        public Item? Take(int count)
        {
            int before = this.Inner.Count;
            Item? result = this.Inner.Take(count);
            this.Owner.OnRemoved(this.Inner.Sample, before - this.Inner.Count);
            return result;
        }
    }
}

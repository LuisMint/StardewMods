using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added, per direct request. Wraps EVERY container in the mod (applied at the single choke point
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
/// reduction that already happened, never withholds one, since (per direct user request) a single
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
    /// MOD: added, per direct request. Proactively wake every active group covering a tile whose
    /// container just changed — <c>(location, tile, isJunimoChest)</c>. Called on every successful
    /// entry/exit, unconditionally (never gated by <see cref="GetVisualEffectsEnabled"/> or whether the
    /// location is loaded — this is a correctness fix, not a visual one). Exists alongside SMAPI's own
    /// <c>ChestInventoryChanged</c> event because that path has no redundancy: per direct user report, a
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

    /// <summary>MOD: added, temporary diagnostic. Encapsulates monitoring and logging — see the <c>[containersync]</c>-tagged Trace logging in <see cref="Store"/>/<see cref="OnRemoved"/> for why this was added, per direct user report of a cross-group pull-conduit delay that survived the first fix attempt.</summary>
    private readonly IMonitor Monitor;

    /// <summary>The <see cref="Game1.currentGameTime"/> total-milliseconds value the current pacing window started at.</summary>
    private double WindowStartMs;

    /// <summary>How many chunks (successful <see cref="Store"/> calls) have been accepted in the current pacing window.</summary>
    private int ActionsUsedThisWindow;

    /// <summary>
    /// The qualified item IDs that have already animated (entry or exit) in the current window — see
    /// this class's own remarks on deduping. MOD: also doubles as each item type's stacking "slot" —
    /// its <see cref="HashSet{T}.Count"/> right after a NEW type is added is that type's 0-based slot
    /// index for <see cref="ContainerVisualEffects"/>'s per-slot vertical offset, so up to 5 different
    /// item types animating in the same window visibly fan out instead of drawing on top of each other.
    /// </summary>
    private readonly HashSet<string> AnimatedItemTypesThisWindow = [];


    /*********
    ** Accessors
    *********/
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
    /// <param name="getEffectiveActionDelaySeconds">Get the effective <c>ActionDelaySeconds</c> (after any Power Relay bonus), in seconds.</param>
    /// <param name="getEffectiveActionsPerDelayWindow">Get the effective <c>ActionsPerDelayWindow</c> (after any Power Relay bonus).</param>
    /// <param name="getVisualEffectsEnabled">Get whether the lid animation/jolt/item sprite/sound should play.</param>
    /// <param name="notifyContainerChanged">MOD: added, per direct request. Proactively wake every active group covering a tile whose container just changed — see <see cref="NotifyContainerChanged"/>.</param>
    /// <param name="monitor">MOD: added, temporary diagnostic. Encapsulates monitoring and logging — see <see cref="Monitor"/>.</param>
    public ThrottledContainer(IContainer inner, Func<float> getEffectiveActionDelaySeconds, Func<int> getEffectiveActionsPerDelayWindow, Func<bool> getVisualEffectsEnabled, Action<GameLocation, Vector2, bool> notifyContainerChanged, IMonitor monitor)
    {
        this.Inner = inner;
        this.GetEffectiveActionDelaySeconds = getEffectiveActionDelaySeconds;
        this.GetEffectiveActionsPerDelayWindow = getEffectiveActionsPerDelayWindow;
        this.GetVisualEffectsEnabled = getVisualEffectsEnabled;
        this.NotifyContainerChanged = notifyContainerChanged;
        this.Monitor = monitor;
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count) => this.Inner.Get(predicate, count);

    /// <summary>
    /// MOD: added, per direct request — a vanilla data-driven machine (a Furnace, Keg, etc., via
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
        // MOD: added, per direct request ("make sure this is performant") — the snapshot/diff below
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
        if (stack.Count <= 0)
            return;

        this.RollWindowIfNeeded();

        float delaySeconds = this.GetEffectiveActionDelaySeconds();
        if (delaySeconds > 0)
        {
            int actionsPerWindow = this.GetEffectiveActionsPerDelayWindow();
            if (actionsPerWindow > 0 && this.ActionsUsedThisWindow >= actionsPerWindow)
                return; // budget exhausted this window — leave the stack untouched, caller retries later
        }

        int before = stack.Count;
        this.Inner.Store(stack);
        int moved = before - stack.Count;
        if (moved <= 0)
            return;

        if (delaySeconds > 0)
            this.ActionsUsedThisWindow++;

        // MOD: added, per direct request — see NotifyContainerChanged's own remarks for why this fires
        // unconditionally alongside SMAPI's own ChestInventoryChanged event, not instead of it.
        this.Monitor.Log($"[containersync] Store: {this.Location.Name} ({this.TileArea.X},{this.TileArea.Y}) type={this.TypeId} stored {moved}x {stack.Sample.QualifiedItemId} junimo={this.IsJunimoChest} -> notifying", LogLevel.Trace); // MOD: added, temporary diagnostic
        this.NotifyContainerChanged(this.Location, new Vector2(this.TileArea.X, this.TileArea.Y), this.IsJunimoChest);

        // MOD: added, per direct request — some containers (the shipping bin) always play their own
        // vanilla-driven "item arrived" feedback regardless of automation, so layering the generic
        // effect on top would double up. See IHasOwnEntryEffect's own remarks.
        if (this.GetVisualEffectsEnabled() && !this.Inner.GetHasOwnEntryEffect() && this.AnimatedItemTypesThisWindow.Add(stack.Sample.QualifiedItemId))
            ContainerVisualEffects.PlayEntryEffect(this, stack.Sample, this.AnimatedItemTypesThisWindow.Count - 1);
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
        double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        float delaySeconds = this.GetEffectiveActionDelaySeconds();
        double windowMs = delaySeconds > 0 ? delaySeconds * 1000 : 0;

        if (now < this.WindowStartMs + windowMs)
            return;

        this.WindowStartMs = now;
        this.ActionsUsedThisWindow = 0;
        this.AnimatedItemTypesThisWindow.Clear();
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

        // MOD: added, per direct request — see NotifyContainerChanged's own remarks. Covers BOTH removal
        // paths that reach this method: a container-to-container pull via ExitEffectTrackedStack, and a
        // vanilla data-driven machine's own ingredient consumption via AttemptAutoLoad's snapshot/diff.
        this.Monitor.Log($"[containersync] Removed: {this.Location.Name} ({this.TileArea.X},{this.TileArea.Y}) type={this.TypeId} removed {amountRemoved}x {sample.QualifiedItemId} junimo={this.IsJunimoChest} -> notifying", LogLevel.Trace); // MOD: added, temporary diagnostic
        this.NotifyContainerChanged(this.Location, new Vector2(this.TileArea.X, this.TileArea.Y), this.IsJunimoChest);

        this.RollWindowIfNeeded();

        if (this.GetVisualEffectsEnabled() && this.AnimatedItemTypesThisWindow.Add(sample.QualifiedItemId))
            ContainerVisualEffects.PlayExitEffect(this, sample, this.AnimatedItemTypesThisWindow.Count - 1);
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

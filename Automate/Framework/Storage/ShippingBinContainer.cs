using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Inventories;
using StardewValley.Locations;
using StardewValley.Mods;

namespace Pathoschild.Stardew.Automate.Framework.Storage;

/// <summary>
/// MOD: added. Wraps the farm's own shipping bin inventory (see
/// <see cref="Farm.getShippingBin"/>) as a plain <see cref="IContainer"/> — readable, writable, and
/// (critically) countable via <see cref="Inventory"/>'s own <c>CountId</c> — so a whitelist/blacklist
/// sign's numeric cap (enforced entirely by <see cref="ItemFilteredContainer.Store"/> reading
/// <c>Inventory.CountId</c>) has something to apply itself to, and a conduit can pull the bin's own
/// contents back out.
///
/// MOD: changed — this used to be constructed only when the "Better Shipping Bin"
/// companion mod was installed (its own player-facing menu was the only way to browse/withdraw the bin's
/// contents before they're sold overnight otherwise, via a separate active-pull-only machine the rest of
/// the time); that gate is gone now, and this is always what a shipping bin resolves to (see
/// <see cref="AutomationFactory"/>). Vanilla's own once-a-day autosell of whatever's still sitting in this
/// SAME inventory at 6am is completely unaffected either way, since this wraps the game's live
/// shipping-bin list directly rather than a copy — pulling items back out (manually, or via a conduit)
/// before then is exactly what keeps them from being sold.
///
/// Deliberately purely passive (a plain <see cref="IContainer"/>, not also an <see cref="IMachine"/>) —
/// see <see cref="Pathoschild.Stardew.Automate.Framework.Machines.Objects.MiniShippingBinMachine"/>'s own
/// remarks for why that's what lets a whitelist/blacklist sign's cap apply automatically no matter which
/// machine is pushing into it, without any bespoke logic of its own.
/// </summary>
internal class ShippingBinContainer : IContainer, IHasContainerPriority, IHasOwnEntryEffect
{
    /*********
    ** Fields
    *********/
    /// <summary>A dummy mod-data dictionary for this container's <see cref="ModData"/> — the underlying inventory has no true one of its own to persist Automate's per-container preferences onto, so any preferences a player sets via a sign here simply won't survive a save reload. There's exactly one shipping bin inventory per save, so this is a narrow, low-stakes gap rather than a normal chest's usual persisted preferences.</summary>
    private readonly ModDataDictionary DummyModData = new();

    /// <summary>MOD: added. The constructed shipping bin, if this container was built for a farm building rather than the island farm's built-in bin — see <see cref="Store"/>'s own remarks for why this matters for the shipment animation/sound.</summary>
    private readonly ShippingBin? Bin;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public string TypeId { get; } = "luisMint.PoweredAutomation_ShippingBinContainer";

    /// <inheritdoc />
    public string Name => "Shipping Bin";

    /// <inheritdoc />
    public ModDataDictionary ModData => this.DummyModData;

    /// <inheritdoc />
    public bool IsJunimoChest => false;

    /// <inheritdoc />
    public bool IsLocked => false;

    /// <inheritdoc />
    public GameLocation Location { get; }

    /// <inheritdoc />
    public Rectangle TileArea { get; }

    /// <inheritdoc />
    public object InventoryReferenceId => this.Inventory;

    /// <inheritdoc />
    public IInventory Inventory => (this.Location as Farm ?? Game1.getFarm()).getShippingBin(Game1.MasterPlayer);

    /// <summary>MOD: added. Same tier as a chest-backed hybrid (Hopper, Mini-Shipping Bin, etc.) — gated by the same <see cref="Models.ModConfig.ChestHybridsCanAutomate"/> toggle, since this now genuinely is one.</summary>
    /// <inheritdoc />
    public int ContainerPriorityTier => ContainerPriorityTiers.ChestHybrid;

    /// <summary>MOD: added — <see cref="Store"/> always plays vanilla's own shipment animation/sound, so the generic one would otherwise double up on top of it.</summary>
    /// <inheritdoc />
    public bool HasOwnEntryEffect => true;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="location">The location containing the shipping bin.</param>
    /// <param name="tile">The tile area covered by the shipping bin.</param>
    /// <param name="bin">MOD: added. The constructed shipping bin, if this is for a farm building rather than the island farm's built-in bin — see <see cref="Bin"/>.</param>
    public ShippingBinContainer(GameLocation location, Rectangle tile, ShippingBin? bin = null)
    {
        this.Location = location;
        this.TileArea = tile;
        this.Bin = bin;
    }

    /// <summary>
    /// Plays vanilla's own shipment animation/sound after actually storing anything — using the exact
    /// same fallback chain (the constructed <see cref="Bin"/> if there is one, then <see cref="IslandWest"/>,
    /// then plain <see cref="Farm"/>) — so pushing items in through this container still looks and sounds
    /// the same as a normal shipment, instead of silently appearing in the inventory with no feedback.
    ///
    /// MOD: removed this container's own accept-cooldown throttle — every container in the mod
    /// (including this one) is now unconditionally wrapped in <see cref="ThrottledContainer"/> at the
    /// single choke point every container passes through, which paces deliveries generically using the
    /// real <c>ActionDelaySeconds</c>/<c>ActionsPerDelayWindow</c> config (Power-Relay-upgrade-aware)
    /// instead of a hardcoded cooldown — that's what this container's own throttle was an early,
    /// single-container prototype of, so keeping a second one here would just double up on the same job.
    /// </summary>
    /// <inheritdoc />
    public void Store(ITrackedStack stack)
    {
        if (stack.Count <= 0 || !stack.Sample.canBeShipped())
            return;

        int before = stack.Count;
        IInventory inventory = this.Inventory;

        // try to stack into an existing slot
        foreach (Item? slot in inventory)
        {
            if (slot != null && stack.Sample.canStackWith(slot))
            {
                Item sample = stack.Sample.getOne();
                sample.Stack = stack.Count;
                int added = stack.Count - slot.addToStack(sample);
                stack.Reduce(added);
                if (stack.Count <= 0)
                    break;
            }
        }

        // otherwise add a new slot — unlimited capacity, matching the farm's own live shipping-bin inventory
        if (stack.Count > 0)
            inventory.Add(stack.Take(stack.Count));

        // play the shipment animation/sound for whatever was actually stored just now
        //
        // MOD: added — without this guard, the shipment sound plays globally regardless of where the
        // player actually is. Vanilla's own showShipment (Farm.cs/ShippingBin.cs/IslandWest.cs) plays its "Ship" cue via
        // DelayedAction.playSoundAfterDelay WITHOUT passing a location, which falls through to a plain
        // Game1.playSound call — a genuinely global sound with no location/distance filtering at all
        // (confirmed directly in the decompiled source). That's harmless for vanilla's own use, since the
        // player is always standing right at the bin when they toss something in by hand, but Automate
        // calls this once per automated shipment from anywhere in the background — so without this guard,
        // the player hears it constantly regardless of where they actually are. Every other container's
        // own entry sound (see ContainerVisualEffects.PlayEffect) already skips itself the same way
        // unless a farmer is actually standing in this container's own location; this matches that same
        // rule instead of relying on vanilla's own (location-blind) method.
        int moved = before - stack.Count;
        if (moved > 0 && this.Location.farmers.Any())
        {
            Item shipped = stack.Sample.getOne();
            shipped.Stack = moved;

            if (this.Bin != null)
                this.Bin.showShipment(shipped, false);
            else if (this.Location is IslandWest islandFarm)
                islandFarm.showShipment(shipped, false);
            else if (this.Location is Farm farm)
                farm.showShipment(shipped, false);
        }
    }

    /// <inheritdoc />
    public int GetFilled() => this.Inventory.Count(item => item != null);

    /// <inheritdoc />
    public int GetCapacity() => int.MaxValue;

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count)
    {
        ITrackedStack[] stacks = this.GetImpl(predicate, count).ToArray();
        return stacks.Any() ? new TrackedItemCollection(stacks) : null;
    }

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator()
    {
        foreach (Item? item in this.Inventory.ToArray())
        {
            ITrackedStack? stack = this.GetTrackedItem(item);
            if (stack != null)
                yield return stack;
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();


    /*********
    ** Private methods
    *********/
    /// <summary>Find items in the bin matching a predicate.</summary>
    /// <param name="predicate">Matches items that should be returned.</param>
    /// <param name="count">The number of items to find.</param>
    private IEnumerable<ITrackedStack> GetImpl(Func<Item, bool> predicate, int count)
    {
        int countFound = 0;
        foreach (Item? item in this.Inventory)
        {
            if (item != null && predicate(item))
            {
                ITrackedStack? stack = this.GetTrackedItem(item);
                if (stack == null)
                    continue;

                countFound += item.Stack;
                yield return stack;
                if (countFound >= count)
                    yield break;
            }
        }
    }

    /// <summary>Get a tracked item synced with the bin's own inventory.</summary>
    /// <param name="item">The item to track.</param>
    private ITrackedStack? GetTrackedItem(Item? item)
    {
        if (item is not { Stack: > 0 })
            return null;

        return new TrackedItem(item).OnEmpty((_, taken) => this.Inventory.Remove(taken));
    }
}

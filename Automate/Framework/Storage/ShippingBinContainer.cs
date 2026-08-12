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
/// MOD: added, per direct request. Wraps the farm's own shipping bin inventory (see
/// <see cref="Farm.getShippingBin"/>) as a plain <see cref="IContainer"/> — readable, writable, and
/// (critically) countable via <see cref="Inventory"/>'s own <c>CountId</c>, unlike
/// <see cref="Pathoschild.Stardew.Automate.Framework.Machines.Buildings.ShippingBinMachine"/>'s own active-pull design, which reaches straight
/// into another container's items without ever calling <c>Store</c> on anything of its own — so a
/// whitelist/blacklist sign's numeric cap (enforced entirely by <see cref="ItemFilteredContainer.Store"/>
/// reading <c>Inventory.CountId</c>) never had anything to apply itself to, and neither did a conduit
/// trying to pull the bin's own contents back out.
///
/// Only ever constructed when the "Better Shipping Bin" mod is installed (see
/// <see cref="AutomationFactory"/>'s own gating) — without it, the bin has no player-facing way to browse
/// or withdraw its contents before they're sold overnight, so treating it as ordinary bidirectional
/// storage the rest of the day would be misleading. Vanilla's own once-a-day autosell of whatever's still
/// sitting in this SAME inventory at 6am is completely unaffected either way, since this wraps the game's
/// live shipping-bin list directly rather than a copy — pulling items back out (manually, or via a
/// conduit) before then is exactly what keeps them from being sold, same as it would be for a player using
/// Better Shipping Bin's own menu.
///
/// Deliberately purely passive (a plain <see cref="IContainer"/>, not also an <see cref="IMachine"/>) —
/// see <see cref="Pathoschild.Stardew.Automate.Framework.Machines.Objects.MiniShippingBinMachine"/>'s own remarks for why that's what lets a
/// whitelist/blacklist sign's cap apply automatically no matter which machine is pushing into it, without
/// any bespoke logic of its own.
/// </summary>
internal class ShippingBinContainer : IContainer, IHasContainerPriority
{
    /*********
    ** Fields
    *********/
    /// <summary>A dummy mod-data dictionary for this container's <see cref="ModData"/> — the underlying inventory has no true one of its own to persist Automate's per-container preferences onto, so any preferences a player sets via a sign here simply won't survive a save reload. There's exactly one shipping bin inventory per save, so this is a narrow, low-stakes gap rather than a normal chest's usual persisted preferences.</summary>
    private readonly ModDataDictionary DummyModData = new();

    /// <summary>MOD: added. The constructed shipping bin, if this container was built for a farm building rather than the island farm's built-in bin — see <see cref="Store"/>'s own remarks for why this matters for the shipment animation/sound.</summary>
    private readonly ShippingBin? Bin;

    /// <summary>MOD: added. How long to wait between accepting shipments, in milliseconds — see <see cref="Store"/>'s own remarks for why this exists.</summary>
    private const double AcceptIntervalMs = 500;

    /// <summary>MOD: added. The <see cref="Game1.currentGameTime"/> total-milliseconds value before which <see cref="Store"/> won't accept anything new.</summary>
    private double NextAcceptAllowedMs;


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
    /// MOD: changed. Plays the same shipment animation/sound the old active-pull <see cref="Pathoschild.Stardew.Automate.Framework.Machines.Buildings.ShippingBinMachine"/>
    /// always did after actually storing anything — using the exact same fallback chain (the constructed
    /// <see cref="Bin"/> if there is one, then <see cref="IslandWest"/>, then plain <see cref="Farm"/>) —
    /// so pushing items in through this container still looks and sounds the same as it always has,
    /// instead of silently appearing in the inventory with no feedback.
    ///
    /// MOD: added. Also throttled to one accepted stack per <see cref="AcceptIntervalMs"/> — rejecting
    /// (leaving the stack fully untouched, so the caller naturally retries on a later automation tick)
    /// anything that arrives before the cooldown expires. Without this, an active mover like a Powered
    /// Chest drains all of its own distinct stacks into the bin within a single tick, so everything
    /// appears at once; the old active-pull <see cref="Pathoschild.Stardew.Automate.Framework.Machines.Buildings.ShippingBinMachine"/>
    /// never had this problem because it WAS the tick-paced mover. This restores that same natural
    /// trickle using the mod's own existing retry/rescheduling machinery, without this container needing
    /// to know anything about who's pushing into it.
    /// </summary>
    /// <inheritdoc />
    public void Store(ITrackedStack stack)
    {
        if (stack.Count <= 0 || !stack.Sample.canBeShipped())
            return;

        double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        if (now < this.NextAcceptAllowedMs)
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

        // otherwise add a new slot — unlimited capacity, same as the standard machine cycle already assumes for this bin (see ShippingBinMachine.SetInput)
        if (stack.Count > 0)
            inventory.Add(stack.Take(stack.Count));

        // play the shipment animation/sound for whatever was actually stored just now, and start the next accept cooldown
        int moved = before - stack.Count;
        if (moved > 0)
        {
            this.NextAcceptAllowedMs = now + ShippingBinContainer.AcceptIntervalMs;

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

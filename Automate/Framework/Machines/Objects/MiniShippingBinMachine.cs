using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Objects;

/// <summary>A mini-shipping bin, usable as plain storage by other machines in its group.</summary>
/// <remarks>
/// MOD: changed. Purely passive now — no active pull/push logic of its own, so it's a plain
/// <see cref="IContainer"/>, not also an <see cref="IMachine"/>. Automate can pull whatever's currently
/// sitting in the bin back out, the same as any normal chest, right up until it actually ships
/// overnight. Only shippable items can ever get in to begin with (see the <c>canBeShipped</c> filter
/// passed to <see cref="Storage"/>'s constructor), so nothing extra is needed on the output side to keep
/// that restriction — anything already in the bin is shippable by definition, and the filter is
/// enforced by <see cref="ChestHybridStorage.Store"/> directly, so it applies no matter which machine is
/// pushing into it.
///
/// See also <see cref="Storage.ShippingBinContainer"/> (the full-size shipping bin) — that one isn't
/// chest-backed either, but wraps the farm's own live shipping-bin inventory directly instead, achieving
/// the same bidirectional pull-back-out behavior a different way.
/// </remarks>
internal class MiniShippingBinMachine : IContainer, IHasContainerPriority, IHasUnderlyingChest
{
    /*********
    ** Fields
    *********/
    /// <summary>The mini-shipping bin's own storage.</summary>
    private readonly ChestHybridStorage Storage;


    /*********
    ** Accessors (IContainer, delegated to <see cref="Storage"/>)
    *********/
    /// <inheritdoc />
    public GameLocation Location => this.Storage.Location;

    /// <inheritdoc />
    public Rectangle TileArea => this.Storage.TileArea;

    /// <inheritdoc />
    public string TypeId => this.Storage.TypeId;

    /// <inheritdoc />
    public string Name => this.Storage.Name;

    /// <inheritdoc />
    public ModDataDictionary ModData => this.Storage.ModData;

    /// <inheritdoc />
    public bool IsJunimoChest => this.Storage.IsJunimoChest;

    /// <inheritdoc />
    public bool IsLocked => this.Storage.IsLocked;

    /// <inheritdoc />
    public object InventoryReferenceId => this.Storage.InventoryReferenceId;

    /// <inheritdoc />
    public IInventory Inventory => this.Storage.Inventory;

    /// <inheritdoc />
    public int ContainerPriorityTier => this.Storage.ContainerPriorityTier;

    /// <inheritdoc />
    public Chest? UnderlyingChest => this.Storage.UnderlyingChest;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="miniBin">The mini-shipping bin.</param>
    /// <param name="location">The location which contains the machine.</param>
    public MiniShippingBinMachine(Chest miniBin, GameLocation location)
    {
        this.Storage = new ChestHybridStorage(location, () => miniBin.TileLocation, () => miniBin, canAccept: item => item.canBeShipped());
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count) => this.Storage.Get(predicate, count);

    /// <inheritdoc />
    public void Store(ITrackedStack stack) => this.Storage.Store(stack);

    /// <inheritdoc />
    public int GetFilled() => this.Storage.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.Storage.GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator() => this.Storage.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MiniShippingBinMachine other ? this.Storage.Equals(other.Storage) : this.Storage.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Storage.GetHashCode();
}

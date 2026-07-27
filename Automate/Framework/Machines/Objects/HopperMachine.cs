using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Objects;

/// <summary>
/// MOD: added. The vanilla Hopper: usable as plain storage by other machines in its group, e.g. a
/// Furnace piped to a Hopper can pull ore from it (or store its output into it), the same as it would
/// from a plain chest. Its own vanilla behavior (collecting from a nearby Fish Pond, and feeding
/// whatever it holds to a machine placed directly below it) is untouched; this class only governs
/// Automate's own interaction with it.
///
/// MOD: changed. Purely passive now — no active pull/push logic of its own, so it's a plain
/// <see cref="IContainer"/>, not also an <see cref="IMachine"/>. See <see cref="ChestHybridStorage"/>'s
/// own remarks for why this needs its own dedicated entity type rather than falling into the generic
/// tagged <see cref="Storage.ChestContainer"/> case (which is how most vanilla storages are handled).
/// </summary>
internal class HopperMachine : IContainer, IHasContainerPriority
{
    /*********
    ** Fields
    *********/
    /// <summary>The qualified item ID of the vanilla Hopper.</summary>
    public const string QualifiedItemId = "(BC)275";

    /// <summary>The hopper's own storage.</summary>
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


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="hopper">The underlying hopper.</param>
    /// <param name="location">The location which contains the machine.</param>
    public HopperMachine(Chest hopper, GameLocation location)
    {
        this.Storage = new ChestHybridStorage(location, () => hopper.TileLocation, () => hopper);
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
    public override bool Equals(object? obj) => obj is HopperMachine other ? this.Storage.Equals(other.Storage) : this.Storage.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Storage.GetHashCode();
}

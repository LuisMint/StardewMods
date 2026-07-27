using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Objects;

/// <summary>An auto-grabber, usable as plain storage by other machines in its group.</summary>
/// <remarks>
/// Derived from <see cref="SObject.DayUpdate"/> and <see cref="SObject.checkForAction"/> (search for
/// 'case 165'). MOD: changed. Purely passive now — no active pull/push logic of its own, so it's a
/// plain <see cref="IContainer"/>, not also an <see cref="IMachine"/>. A Furnace (or a Powered Chest)
/// piped to an Auto-Grabber can pull collected animal products out of it, or store items into it, the
/// same as it would a plain chest. Vanilla's own Auto-Grabber never accepts anything except its own
/// automatic overnight deposits, but with Automate's pipe/filter system layered on top, letting the
/// player deliberately route items into it too (subject to the same whitelist/blacklist signs as any
/// other container in the group) is a reasonable, deliberate deviation from that original design.
/// </remarks>
internal class AutoGrabberMachine : IContainer, IHasContainerPriority
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying auto-grabber object.</summary>
    private readonly SObject Machine;

    /// <summary>The auto-grabber's own held-chest storage.</summary>
    private readonly ChestHybridStorage Storage;


    /*********
    ** Accessors (IContainer, delegated to <see cref="Storage"/> except where noted)
    *********/
    /// <inheritdoc />
    public GameLocation Location => this.Storage.Location;

    /// <inheritdoc />
    public Rectangle TileArea => this.Storage.TileArea;

    /// <summary>MOD: changed — the Auto-Grabber's OWN qualified item ID, not its held chest's (a generic vanilla chest with no distinct identity of its own), so chest-override config can meaningfully target it.</summary>
    /// <inheritdoc />
    public string TypeId => this.Machine.QualifiedItemId;

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
    /// <param name="machine">The underlying machine.</param>
    /// <param name="location">The in-game location.</param>
    public AutoGrabberMachine(SObject machine, GameLocation location)
    {
        this.Machine = machine;
        this.Storage = new ChestHybridStorage(location, () => this.Machine.TileLocation, () => this.Machine.heldObject.Value as Chest, onStored: this.UpdateGlow);
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
    public override bool Equals(object? obj) => obj is AutoGrabberMachine other ? this.Storage.Equals(other.Storage) : this.Storage.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Storage.GetHashCode();


    /*********
    ** Private methods
    *********/
    /// <summary>MOD: added. Keep the "has contents" glow in sync immediately whenever Automate stores an item into the held chest — restores the same behavior it had back when this moved items through its own active <c>SetInput</c>.</summary>
    private void UpdateGlow()
    {
        if (this.Machine.heldObject.Value is Chest chest)
            this.Machine.showNextIndex.Value = !chest.isEmpty();
    }
}

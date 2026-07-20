using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps a container so its <see cref="IContainer.Inventory"/> is filtered by a
/// whitelist/blacklist sign filter — this is what actually closes the gap for machines that read
/// straight from a container's raw inventory (like <c>SObject.AttemptAutoLoad</c>, used by most
/// vanilla machines) instead of going through Automate's own <see cref="IStorage"/> abstraction.
/// Everything else is delegated straight to the wrapped container unchanged.
/// </summary>
internal class ItemFilteredContainer : IContainer
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying container being wrapped.</summary>
    private readonly IContainer Inner;

    /// <summary>Get whether a given qualified item ID is allowed to move through this container.</summary>
    private readonly Func<string, bool> ItemIdAllowed;


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
    /// MOD: this is the key change — wraps the real inventory in a filtered view.
    public IInventory Inventory { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The underlying container being wrapped.</param>
    /// <param name="itemIdAllowed">Get whether a given qualified item ID is allowed to move through this container.</param>
    public ItemFilteredContainer(IContainer inner, Func<string, bool> itemIdAllowed)
    {
        this.Inner = inner;
        this.ItemIdAllowed = itemIdAllowed;
        this.Inventory = new FilteredInventory(inner.Inventory, itemIdAllowed);
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count)
    {
        return this.Inner.Get(item => this.ItemIdAllowed(item.QualifiedItemId) && predicate(item), count);
    }

    /// <inheritdoc />
    public void Store(ITrackedStack stack) => this.Inner.Store(stack);

    /// <inheritdoc />
    public int GetFilled() => this.Inner.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.Inner.GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator() => this.Inner.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ItemFilteredContainer other ? this.Inner.Equals(other.Inner) : this.Inner.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Inner.GetHashCode();
}

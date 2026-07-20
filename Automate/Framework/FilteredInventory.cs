using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Inventories;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps an <see cref="IInventory"/> to hide items that don't pass an item filter (from
/// whitelist/blacklist signs). This exists because some game machine logic (e.g.
/// <c>SObject.AttemptAutoLoad</c>, used by most vanilla machines) reads directly from a container's
/// raw <see cref="IContainer.Inventory"/> instead of going through Automate's own <see cref="IStorage"/>
/// abstraction — so filtering only at the <see cref="IStorage"/> level (via <see cref="FilteredStorage"/>)
/// isn't enough; this closes that gap by filtering at the source.
///
/// Filtered-out items appear as empty slots (returned as <c>null</c>) rather than being removed from
/// the underlying list, matching how real empty inventory slots normally appear. Read operations
/// (enumeration, Contains, ContainsId, CountId, GetById, HasAny, CountItemStacks) only see allowed
/// items. Mutating operations (Reduce, ReduceId, RemoveButKeepEmptySlot) refuse to act on
/// disallowed items. Bulk/positional operations (Add, Insert, Clear, OverwriteWith, AddRange,
/// RemoveRange, RemoveEmptySlots) pass straight through — filtering only needs to affect what
/// existing content machines can "see" and consume, not general inventory management.
/// </summary>
internal class FilteredInventory : IInventory
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying inventory being wrapped.</summary>
    private readonly IInventory Inner;

    /// <summary>Get whether a given qualified item ID is allowed to be seen/consumed through this inventory.</summary>
    private readonly Func<string, bool> ItemIdAllowed;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The underlying inventory being wrapped.</param>
    /// <param name="itemIdAllowed">Get whether a given qualified item ID is allowed to be seen/consumed through this inventory.</param>
    public FilteredInventory(IInventory inner, Func<string, bool> itemIdAllowed)
    {
        this.Inner = inner;
        this.ItemIdAllowed = itemIdAllowed;
    }

    /*********
    ** IList<Item> / ICollection<Item>
    *********/
    /// <inheritdoc />
    public Item this[int index]
    {
        get
        {
            Item item = this.Inner[index];
            return item != null && this.IsAllowed(item) ? item : null!;
        }
        set => this.Inner[index] = value;
    }

    /// <inheritdoc />
    public int Count => this.Inner.Count;

    /// <inheritdoc />
    public bool IsReadOnly => this.Inner.IsReadOnly;

    /// <inheritdoc />
    public void Add(Item item) => this.Inner.Add(item);

    /// <inheritdoc />
    public void Clear() => this.Inner.Clear();

    /// <inheritdoc />
    public bool Contains(Item item) => item != null && this.IsAllowed(item) && this.Inner.Contains(item);

    /// <inheritdoc />
    public void CopyTo(Item[] array, int arrayIndex)
    {
        Item[] allowed = this.AllowedItemsBySlot().ToArray();
        allowed.CopyTo(array, arrayIndex);
    }

    /// <inheritdoc />
    public IEnumerator<Item> GetEnumerator() => this.AllowedItemsBySlot().GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public int IndexOf(Item item)
    {
        if (item == null || !this.IsAllowed(item))
            return -1;
        return this.Inner.IndexOf(item);
    }

    /// <inheritdoc />
    public void Insert(int index, Item item) => this.Inner.Insert(index, item);

    /// <inheritdoc />
    public bool Remove(Item item) => this.Inner.Remove(item);

    /// <inheritdoc />
    public void RemoveAt(int index) => this.Inner.RemoveAt(index);

    /*********
    ** IInventory
    *********/
    /// <inheritdoc />
    public bool IsLocalPlayerInventory
    {
        get => this.Inner.IsLocalPlayerInventory;
        set => this.Inner.IsLocalPlayerInventory = value;
    }

    /// <inheritdoc />
    public long LastTickSlotChanged => this.Inner.LastTickSlotChanged;

    /// <inheritdoc />
    public bool HasAny() => this.Inner.Any(item => item != null && this.IsAllowed(item));

    /// <inheritdoc />
    public bool HasEmptySlots() => this.Inner.HasEmptySlots();

    /// <inheritdoc />
    public int CountItemStacks() => this.Inner.Count(item => item != null && this.IsAllowed(item));

    /// <inheritdoc />
    public void OverwriteWith(IList<Item> list) => this.Inner.OverwriteWith(list);

    /// <inheritdoc />
    public IList<Item> GetRange(int index, int count)
    {
        return this.Inner.GetRange(index, count)
            .Select(item => item != null && this.IsAllowed(item) ? item : null!)
            .ToList();
    }

    /// <inheritdoc />
    public void AddRange(ICollection<Item> collection) => this.Inner.AddRange(collection);

    /// <inheritdoc />
    public void RemoveRange(int index, int count) => this.Inner.RemoveRange(index, count);

    /// <inheritdoc />
    public void RemoveEmptySlots() => this.Inner.RemoveEmptySlots();

    /// <inheritdoc />
    public bool ContainsId(string itemId)
    {
        return this.ItemIdAllowed(itemId) && this.Inner.ContainsId(itemId);
    }

    /// <inheritdoc />
    public bool ContainsId(string itemId, int minimum)
    {
        if (!this.ItemIdAllowed(itemId))
            return false;
        return this.Inner.ContainsId(itemId, minimum);
    }

    /// <inheritdoc />
    public int CountId(string itemId)
    {
        return this.ItemIdAllowed(itemId) ? this.Inner.CountId(itemId) : 0;
    }

    /// <inheritdoc />
    public IEnumerable<Item> GetById(string itemId)
    {
        return this.ItemIdAllowed(itemId) ? this.Inner.GetById(itemId) : Enumerable.Empty<Item>();
    }

    /// <inheritdoc />
    public int Reduce(Item item, int count, bool reduceRemainderFromInventory = false)
    {
        if (item == null || !this.IsAllowed(item))
            return 0;
        return this.Inner.Reduce(item, count, reduceRemainderFromInventory);
    }

    /// <inheritdoc />
    public int ReduceId(string itemId, int count)
    {
        if (!this.ItemIdAllowed(itemId))
            return 0;
        return this.Inner.ReduceId(itemId, count);
    }

    /// <inheritdoc />
    public bool RemoveButKeepEmptySlot(Item item)
    {
        if (item == null || !this.IsAllowed(item))
            return false;
        return this.Inner.RemoveButKeepEmptySlot(item);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get whether a given item is allowed through this filtered view.</summary>
    /// <param name="item">The item to check.</param>
    private bool IsAllowed(Item item) => this.ItemIdAllowed(item.QualifiedItemId);

    /// <summary>Get every slot's item, with disallowed items replaced by <c>null</c> (matching how empty slots normally appear), preserving slot positions.</summary>
    private IEnumerable<Item> AllowedItemsBySlot()
    {
        for (int i = 0; i < this.Inner.Count; i++)
            yield return this[i];
    }
}

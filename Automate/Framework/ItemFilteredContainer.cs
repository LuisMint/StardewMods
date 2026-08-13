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
/// MOD: added. Wraps a container so items moving through it respect a whitelist/blacklist sign
/// filter — both which item types are allowed at all, and (per <see cref="SignFilter"/>) how much of
/// an item may move based on the container's OWN current count of it. This is also what closes the
/// gap for machines that read straight from a container's raw inventory (like
/// <c>SObject.AttemptAutoLoad</c>, used by most vanilla machines) instead of going through Automate's
/// own <see cref="IStorage"/> abstraction — though that raw-inventory path (<see cref="Inventory"/>,
/// via <see cref="FilteredInventory"/>) only enforces the type filter, not the numeric quantity rules;
/// see <see cref="FilteredInventory"/>'s own remarks for why. Everything else is delegated straight to
/// the wrapped container unchanged.
/// </summary>
internal class ItemFilteredContainer : IContainer, IConnectionRoleRestriction, IHasContainerPriority, IHasUnderlyingChest, IHasAttemptAutoLoad, IHasOwnEntryEffect
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying container being wrapped.</summary>
    private readonly IContainer Inner;

    /// <summary>The resolved whitelist/blacklist condition for this container's machine group.</summary>
    private readonly SignFilter Filter;


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

    /// <summary>
    /// MOD: added. Forwards to the wrapped container's own restriction if it has one (e.g. a
    /// <see cref="RoleRestrictedContainer"/> underneath), defaulting to unrestricted otherwise —
    /// without this, wrapping a role-restricted container in this class would silently drop the
    /// restriction, since <see cref="StorageManager.SetContainers"/> checks for
    /// <see cref="IConnectionRoleRestriction"/> via a type test on the OUTERMOST container only, and
    /// this class used to not implement that interface at all.
    /// </summary>
    public bool AllowStorageThroughThisConnection => this.Inner is not IConnectionRoleRestriction restriction || restriction.AllowStorageThroughThisConnection;

    /// <inheritdoc cref="AllowStorageThroughThisConnection" />
    public bool AllowTakingThroughThisConnection => this.Inner is not IConnectionRoleRestriction restriction || restriction.AllowTakingThroughThisConnection;

    /// <summary>MOD: added. Forwards to the wrapped container's own priority tier (see <see cref="IHasContainerPriority"/>), for the same reason <see cref="AllowStorageThroughThisConnection"/> forwards its own role restriction — see that property's own remarks.</summary>
    /// <inheritdoc />
    public int ContainerPriorityTier => this.Inner.GetContainerPriorityTier();

    /// <summary>MOD: added. Forwards to the wrapped container's own underlying chest (see <see cref="IHasUnderlyingChest"/>), for the same reason <see cref="ContainerPriorityTier"/> forwards its own tier — see that property's own remarks.</summary>
    /// <inheritdoc />
    public Chest? UnderlyingChest => this.Inner.GetUnderlyingChest();

    /// <inheritdoc />
    public bool HasOwnEntryEffect => this.Inner.GetHasOwnEntryEffect();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The underlying container being wrapped.</param>
    /// <param name="filter">The resolved whitelist/blacklist condition for this container's machine group.</param>
    public ItemFilteredContainer(IContainer inner, SignFilter filter)
    {
        this.Inner = inner;
        this.Filter = filter;
        this.Inventory = new FilteredInventory(inner.Inventory, filter.IsItemTypeAllowed);
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count)
    {
        // MOD: reimplemented (rather than delegating `count` straight into Inner.Get) so the numeric
        // sign condition — which depends on THIS container's own current count per item ID — can clamp
        // how much of each matching stack is actually offered, regardless of how many different item
        // types the predicate happens to match.
        List<ITrackedStack> matches = [];
        int found = 0;
        foreach (ITrackedStack stack in this)
        {
            if (!predicate(stack.Sample))
                continue;

            matches.Add(stack);
            found += stack.Count;
            if (found >= count)
                break;
        }

        return matches.Count > 0
            ? new TrackedItemCollection([.. matches])
            : null;
    }

    /// <inheritdoc />
    public void Store(ITrackedStack stack)
    {
        if (stack.Count <= 0)
            return;

        string itemId = stack.Sample.QualifiedItemId;
        int currentCount = this.Inner.Inventory.CountId(itemId);
        int allowed = this.Filter.GetMaxStorable(itemId, currentCount, stack.Count);

        if (allowed <= 0)
            return; // fully blocked — leave the stack untouched so the caller can try elsewhere

        this.Inner.Store(allowed >= stack.Count ? stack : new ClampedTrackedStack(stack, allowed));
    }

    /// <summary>
    /// MOD: added. Run a vanilla <c>Data/Machines</c>-driven machine's own auto-load logic
    /// (<see cref="SObject.AttemptAutoLoad(IInventory, Farmer)"/>) against this container, but with a
    /// numeric sign condition actually enforced on the amount consumed.
    ///
    /// This exists because vanilla's own ingredient consumption (<c>SObject.PlaceInMachine</c> ->
    /// <c>SObject.ConsumeInventoryItem</c>) reduces the input item's <see cref="Item.Stack"/> by
    /// directly mutating that exact object — it never calls back into any count-checking method this
    /// class already clamps (<see cref="Get"/>, <see cref="FilteredInventory.CountId"/>, etc.), so a
    /// numeric cap would otherwise be silently ignored for any machine using this path (which is most
    /// vanilla machines, e.g. the Furnace). To enforce it anyway, each real item's <see cref="Item.Stack"/>
    /// is temporarily reduced to whatever's currently allowed for the duration of this single
    /// (synchronous) call — so vanilla's own "is there enough" checks and consumption see only the
    /// allowed amount — then whatever wasn't actually consumed is restored to the real item afterward
    /// (or, if vanilla's own logic emptied and removed the slot entirely, re-added as a fresh stack).
    /// Since this all happens within one synchronous call with no yielding in between, nothing else
    /// can observe an item's stack while it's temporarily reduced.
    /// </summary>
    /// <param name="machine">The machine attempting to auto-load an ingredient.</param>
    /// <param name="who">The player to attribute the auto-load to.</param>
    public bool AttemptAutoLoad(SObject machine, Farmer who)
    {
        List<(Item Item, int Hidden)> clamped = [];

        foreach (Item? item in this.Inventory.ToArray())
        {
            if (item == null)
                continue;

            string itemId = item.QualifiedItemId;
            int currentCount = this.Inventory.CountId(itemId); // real count for allowed items (FilteredInventory's own type filter already applied)
            int allowed = this.Filter.GetMaxTakeable(itemId, currentCount, currentCount);

            if (allowed < item.Stack)
            {
                clamped.Add((item, item.Stack - allowed));
                item.Stack = allowed;
            }
        }

        try
        {
            return machine.AttemptAutoLoad(this.Inventory, who);
        }
        finally
        {
            foreach ((Item item, int hidden) in clamped)
            {
                if (hidden <= 0)
                    continue;

                if (this.Inventory.Contains(item))
                    item.Stack += hidden; // wasn't fully consumed — just give back the hidden portion
                else
                {
                    // vanilla's own consumption emptied this stack and removed the slot — re-add the
                    // hidden portion as a fresh stack, since the original item reference is now detached
                    Item restored = item.getOne();
                    restored.Stack = hidden;
                    this.Inventory.Add(restored);
                }
            }
        }
    }

    /// <inheritdoc />
    public int GetFilled() => this.Inner.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.Inner.GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator()
    {
        // MOD: yields each real stack clamped to however much of it is still allowed, tracking a
        // running per-item-ID budget across the enumeration (computed once per item ID, since the
        // numeric condition is evaluated against the container's current TOTAL count of that item, not
        // per individual inventory slot — a chest can have the same item split across several slots).
        Dictionary<string, int> remainingTakeableByItem = [];

        foreach (ITrackedStack real in this.Inner)
        {
            string itemId = real.Sample.QualifiedItemId;

            if (!remainingTakeableByItem.TryGetValue(itemId, out int remaining))
            {
                int currentCount = this.Inner.Inventory.CountId(itemId);
                remaining = this.Filter.GetMaxTakeable(itemId, currentCount, currentCount);
            }

            if (remaining <= 0)
            {
                remainingTakeableByItem[itemId] = 0;
                continue;
            }

            int give = Math.Min(real.Count, remaining);
            remainingTakeableByItem[itemId] = remaining - give;

            if (give > 0)
                yield return give == real.Count ? real : new ClampedTrackedStack(real, give);
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ItemFilteredContainer other ? this.Inner.Equals(other.Inner) : this.Inner.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Inner.GetHashCode();
}

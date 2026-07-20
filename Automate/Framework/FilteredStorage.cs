using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewModdingAPI;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps an <see cref="IStorage"/> to restrict which items can move through it, based on
/// a whitelist/blacklist sign filter resolved for the containing machine group. Only items matching
/// the filter can be pulled out (as ingredients/output) or pushed in (as machine output) — everything
/// else is left alone in both directions, as if it doesn't exist to Automate.
/// </summary>
internal class FilteredStorage : IStorage
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying storage being wrapped.</summary>
    private readonly IStorage Inner;

    /// <summary>Get whether a given item stack is allowed to move through this storage.</summary>
    private readonly Func<ITrackedStack, bool> ItemAllowed;

    /// <summary>TEMP DIAGNOSTIC (MOD: added). Encapsulates monitoring and logging, for debug output only. Remove once confirmed working.</summary>
    private readonly IMonitor? Monitor;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public IContainer[] InputContainers => this.Inner.InputContainers;

    /// <inheritdoc />
    public IContainer[] OutputContainers => this.Inner.OutputContainers;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The underlying storage being wrapped.</param>
    /// <param name="itemAllowed">Get whether a given item stack is allowed to move through this storage.</param>
    /// <param name="monitor">TEMP DIAGNOSTIC (MOD: added). Encapsulates monitoring and logging, for debug output only.</param>
    public FilteredStorage(IStorage inner, Func<ITrackedStack, bool> itemAllowed, IMonitor? monitor = null)
    {
        this.Inner = inner;
        this.ItemAllowed = itemAllowed;
        this.Monitor = monitor;
    }

    /// <inheritdoc />
    public bool HasLockedContainers()
    {
        return this.Inner.HasLockedContainers();
    }

    /// <inheritdoc />
    public IEnumerable<ITrackedStack> GetItems()
    {
        // TEMP DIAGNOSTIC (MOD: added) — remove once confirmed working.
        ITrackedStack[] allItems = this.Inner.GetItems().ToArray();
        ITrackedStack[] allowedItems = allItems.Where(this.ItemAllowed).ToArray();
        if (allItems.Length != allowedItems.Length)
        {
            this.Monitor?.Log($"[Automate sign debug] FilteredStorage.GetItems(): {allItems.Length} total items in storage, {allowedItems.Length} passed the filter. Blocked: {string.Join(", ", allItems.Except(allowedItems).Select(s => s.Sample.QualifiedItemId))}", LogLevel.Info);
        }
        else
        {
            this.Monitor?.Log($"[Automate sign debug] FilteredStorage.GetItems(): {allItems.Length} items, all passed filter (or none present). Items: {string.Join(", ", allItems.Select(s => s.Sample.QualifiedItemId))}", LogLevel.Info);
        }

        return allowedItems;
    }

    /// <inheritdoc />
    public bool TryGetIngredient(Func<ITrackedStack, bool> predicate, int count, [NotNullWhen(true)] out IConsumable? consumable)
    {
        // MOD: AND the caller's predicate with our own filter, then delegate — the inner storage's
        // own GetItems() isn't used here, so this stays correctly scoped to the filter regardless.
        return this.Inner.TryGetIngredient(stack => this.ItemAllowed(stack) && predicate(stack), count, out consumable);
    }

    /// <inheritdoc />
    public bool TryGetIngredient(IRecipe[] recipes, [NotNullWhen(true)] out IConsumable? consumable, [NotNullWhen(true)] out IRecipe? recipe)
    {
        // MOD: reimplemented rather than delegated — the inner StorageManager's own implementation of
        // this overload scans its own unfiltered GetItems() internally, which would bypass our filter
        // entirely if we just called through. This mirrors StorageManager's logic exactly, but sources
        // items from OUR filtered GetItems() instead.
        Dictionary<IRecipe, StackAccumulator> accumulator = recipes.ToDictionary(req => req, _ => new StackAccumulator());

        foreach (ITrackedStack input in this.GetItems())
        {
            foreach (var entry in accumulator)
            {
                recipe = entry.Key;
                StackAccumulator stacks = entry.Value;

                if (recipe.AcceptsInput(input))
                {
                    ITrackedStack stack = stacks.Add(input);
                    if (stack.Count >= recipe.InputCount)
                    {
                        consumable = new Consumable(stack, entry.Key.InputCount);
                        return true;
                    }
                }
            }
        }

        consumable = null;
        recipe = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryConsume(Func<ITrackedStack, bool> predicate, int count)
    {
        if (this.TryGetIngredient(predicate, count, out IConsumable? requirement))
        {
            requirement.Reduce();
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool TryPush(ITrackedStack? item)
    {
        if (item is not { Count: > 0 })
            return false;

        // TEMP DIAGNOSTIC (MOD: added) — remove once confirmed working.
        bool allowed = this.ItemAllowed(item);
        this.Monitor?.Log($"[Automate sign debug] FilteredStorage.TryPush(): item={item.Sample.QualifiedItemId}, allowed={allowed}", LogLevel.Info);

        if (!allowed)
            return false;

        return this.Inner.TryPush(item);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Pathoschild.Stardew.Automate;

/// <summary>Manages access to items in the underlying containers.</summary>
public interface IStorage
{
    /*********
    ** Accessors
    *********/
    /// <summary>The storage containers that accept input, in priority order.</summary>
    IContainer[] InputContainers { get; }

    /// <summary>The storage containers that provide items, in priority order.</summary>
    IContainer[] OutputContainers { get; }

    /// <summary>
    /// MOD: added. Every container in the group the player hasn't explicitly disabled outright (see
    /// <c>ContainerExtensions.StorageAllowed</c>/<c>TakingItemsAllowed</c>), WITHOUT the connector-role
    /// narrowing <see cref="InputContainers"/>/<see cref="OutputContainers"/> apply. Meant for an active
    /// mover (e.g. a Powered Chest) that needs to interpret a connector's role from its OWN perspective
    /// — "Input Pipe is my pull source, Output Pipe is my push destination" — rather than the standard
    /// machine cycle's perspective ("a storable-role container is a valid destination for MY output"),
    /// which is the opposite direction for the exact same role. <see cref="InputContainers"/>/
    /// <see cref="OutputContainers"/> pre-exclude a container based on that standard-cycle
    /// interpretation, so an active mover reading them would never even see a container reached the
    /// "wrong" way for its own purposes — this array exists so it can apply its own interpretation
    /// instead, checking each candidate's role restriction and per-container preference directly. See
    /// <c>ContainerExtensions.IsActiveMoverPullSource</c>/<c>IsActiveMoverPushDestination</c>.
    /// </summary>
    IContainer[] AllContainers { get; }

    /// <summary>
    /// MOD: added. The subset of <see cref="OutputContainers"/> the standard machine cycle may actually
    /// draw ingredients from — narrower than <see cref="OutputContainers"/> itself (which only applies
    /// connector-role filtering). A machine implementation that reads ingredient sources directly via
    /// <see cref="IContainer"/> iteration (rather than through <see cref="GetItems"/>/<see cref="TryGetIngredient(Func{ITrackedStack,bool},int,out IConsumable)"/>/
    /// <see cref="TryGetIngredient(IRecipe[],out IConsumable,out IRecipe)"/>) should use this instead of
    /// <see cref="OutputContainers"/>, or it bypasses the same restrictions those methods enforce
    /// internally.
    /// </summary>
    IContainer[] MachineOutputContainers { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Get whether any of the <see cref="InputContainers"/> or <see cref="OutputContainers"/> are locked.</summary>
    bool HasLockedContainers();

    /// <summary>Get all items from the given pipes.</summary>
    IEnumerable<ITrackedStack> GetItems();

    /****
    ** TryGetIngredient
    ****/
    /// <summary>Get an ingredient needed for a recipe.</summary>
    /// <param name="predicate">Returns whether an item should be matched.</param>
    /// <param name="count">The number of items to find.</param>
    /// <param name="consumable">The matching consumables.</param>
    /// <returns>Returns whether the requirement is met.</returns>
    bool TryGetIngredient(Func<ITrackedStack, bool> predicate, int count, [NotNullWhen(true)] out IConsumable? consumable);

    /// <summary>Get an ingredient needed for a recipe.</summary>
    /// <param name="recipes">The items to match.</param>
    /// <param name="consumable">The matching consumables.</param>
    /// <param name="recipe">The matched requisition.</param>
    /// <returns>Returns whether the requirement is met.</returns>
    bool TryGetIngredient(IRecipe[] recipes, [NotNullWhen(true)] out IConsumable? consumable, [NotNullWhen(true)] out IRecipe? recipe);

    /****
    ** TryConsume
    ****/
    /// <summary>Consume an ingredient needed for a recipe.</summary>
    /// <param name="predicate">Returns whether an item should be matched.</param>
    /// <param name="count">The number of items to find.</param>
    /// <returns>Returns whether the item was consumed.</returns>
    bool TryConsume(Func<ITrackedStack, bool> predicate, int count);

    /****
    ** TryPush
    ****/
    /// <summary>Add the given item stack to the output pipe if there's space.</summary>
    /// <param name="item">The item stack to push.</param>
    /// <returns>Returns whether at least some of the item stack was received.</returns>
    bool TryPush(ITrackedStack? item);
}

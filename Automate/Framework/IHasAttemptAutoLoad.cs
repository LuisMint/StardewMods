using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added, per direct request. Implemented by an <see cref="IContainer"/> that needs to intercept a
/// vanilla data-driven machine's own raw-inventory ingredient consumption (<c>SObject.AttemptAutoLoad</c>,
/// used by most vanilla machines — a Furnace, Keg, Cheese Press, etc. — via
/// <see cref="Machines.DataBasedObjectMachine"/>) instead of letting it read straight from
/// <see cref="IContainer.Inventory"/>. Vanilla's own consumption mutates the input item's <c>Stack</c>
/// directly rather than calling back into any <see cref="ITrackedStack"/>/<see cref="IContainer.Get"/>
/// method this mod's own wrappers could otherwise observe, so a container that needs to react to what's
/// actually consumed this way (a numeric whitelist/blacklist cap, or <see cref="ThrottledContainer"/>'s
/// own exit-effect animation) has no other hook to intercept it at — see
/// <see cref="Storage.ItemFilteredContainer.AttemptAutoLoad"/>'s own remarks for the numeric-cap case
/// this was originally built for, and <see cref="ThrottledContainer"/>'s own implementation for the
/// before/after inventory-diffing this needs to use here instead.
/// </summary>
internal interface IHasAttemptAutoLoad
{
    /// <summary>Run a vanilla data-driven machine's own auto-load logic against this container.</summary>
    /// <param name="machine">The machine attempting to auto-load an ingredient.</param>
    /// <param name="who">The player to attribute the auto-load to.</param>
    bool AttemptAutoLoad(SObject machine, Farmer who);
}

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Objects;

/// <summary>
/// MOD: added. A power-required machine that gets assigned a known crafting/cooking recipe by
/// right-clicking it with the recipe's output item in hand (see <see cref="Patches.AutoCrafterPatches"/>),
/// then pulls that recipe's ingredients from a connected Powered Chest — via the standard
/// <see cref="IStorage.GetItems"/>/<see cref="IStorage.TryConsume"/> cycle, which is already restricted to
/// Powered-Chest-tier containers (see <see cref="StorageManager.MachineOutputContainers"/>) — and produces
/// the output into a normal <see cref="SObject.heldObject"/>/<see cref="SObject.readyForHarvest"/> bubble.
/// </summary>
internal class AutoCrafterMachine : GenericObjectMachine<SObject>
{
    /*********
    ** Fields
    *********/
    /// <summary>The qualified item ID of this custom machine.</summary>
    public const string QualifiedItemId = "(BC)luisMint.PoweredAutomation_AutoCrafter";

    /// <summary>The <see cref="StardewValley.CraftingRecipe.name"/> of the currently assigned recipe, if any.</summary>
    internal const string RecipeNameModDataKey = "luisMint.PoweredAutomation/AutoCrafterRecipeName";

    /// <summary>Whether the currently assigned recipe (see <see cref="RecipeNameModDataKey"/>) is a cooking recipe rather than a crafting recipe.</summary>
    internal const string RecipeIsCookingModDataKey = "luisMint.PoweredAutomation/AutoCrafterRecipeIsCooking";

    /// <summary>The qualified item ID of the item to show floating above the machine (like a sign's displayed item) while a recipe is assigned.</summary>
    internal const string DisplayItemModDataKey = "luisMint.PoweredAutomation/AutoCrafterDisplayItem";

    /// <summary>The real-time <see cref="Game1.currentGameTime"/> milliseconds at which the last prime/unprime animation transition started, for <see cref="Patches.AutoCrafterPatches"/>'s draw patch to interpolate from.</summary>
    internal const string AnimStartModDataKey = "luisMint.PoweredAutomation/AutoCrafterAnimStartMs";

    /// <summary>The real-time <see cref="Game1.currentGameTime"/> milliseconds at which the current craft's processing began, for <see cref="Patches.AutoCrafterPatches"/> to anchor its press-cycle animation against — see <see cref="GetProcessingStartMs"/>.</summary>
    internal const string ProcessingStartMsModDataKey = "luisMint.PoweredAutomation/AutoCrafterProcessingStartMs";

    /// <summary>The index of the last press cycle (see <see cref="ProcessingStartMsModDataKey"/>) <see cref="Patches.AutoCrafterPatches"/> has already played the strike particle/sound for — see <see cref="GetLastHandledStrikeCycle"/>.</summary>
    internal const string LastHandledStrikeCycleModDataKey = "luisMint.PoweredAutomation/AutoCrafterLastHandledStrikeCycle";

    /// <summary>MOD: changed, per direct request — every craft now takes a flat 20 in-game minutes, regardless of how many ingredients the recipe needs.</summary>
    internal const int ProcessingMinutes = 20;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="machine">The underlying machine.</param>
    /// <param name="location">The location containing the machine.</param>
    /// <param name="tile">The tile covered by the machine.</param>
    public AutoCrafterMachine(SObject machine, GameLocation location, Vector2 tile)
        : base(machine, location, tile, BaseMachine.GetDefaultMachineId<AutoCrafterMachine>()) { }

    /// <inheritdoc />
    public override bool SetInput(IStorage input)
    {
        if (this.GetGenericState() != MachineState.Empty)
            return false; // already processing, or output sitting uncollected

        if (!AutoCrafterMachine.TryGetAssignedRecipe(this.Machine, out CraftingRecipe? recipe))
            return false; // nothing assigned

        // don't consume anything unless every ingredient is available all at once, same guarantee a
        // vanilla Furnace gets from AttemptAutoLoad for its own multi-ingredient recipe (Coal + Ore)
        if (!AutoCrafterMachine.HasAllIngredients(input, recipe.recipeList))
            return false;

        foreach (KeyValuePair<string, int> entry in recipe.recipeList)
            input.TryConsume(stack => AutoCrafterMachine.MatchesIngredient(stack.Sample, entry.Key), entry.Value);

        this.Machine.heldObject.Value = (SObject)recipe.createItem();
        this.Machine.MinutesUntilReady = AutoCrafterMachine.ProcessingMinutes;
        this.Machine.modData[AutoCrafterMachine.ProcessingStartMsModDataKey] = (Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0).ToString(CultureInfo.InvariantCulture);
        this.Machine.modData.Remove(AutoCrafterMachine.LastHandledStrikeCycleModDataKey); // reset so the first strike of the new craft is detected fresh

        return true;
    }


    /*********
    ** Internal methods
    *********/
    /// <summary>Get the currently assigned recipe, if any.</summary>
    /// <param name="machine">The machine to check.</param>
    /// <param name="recipe">The assigned recipe, if found.</param>
    internal static bool TryGetAssignedRecipe(SObject machine, [NotNullWhen(true)] out CraftingRecipe? recipe)
    {
        if (!machine.modData.TryGetValue(AutoCrafterMachine.RecipeNameModDataKey, out string? name) || string.IsNullOrEmpty(name))
        {
            recipe = null;
            return false;
        }

        bool isCookingRecipe = machine.modData.TryGetValue(AutoCrafterMachine.RecipeIsCookingModDataKey, out string? rawIsCooking) && rawIsCooking == "true";
        recipe = new CraftingRecipe(name, isCookingRecipe);
        return true;
    }

    /// <summary>Get the item to show floating above the machine (like a sign's displayed item), if a recipe is assigned.</summary>
    /// <param name="machine">The machine to check.</param>
    /// <param name="item">The display item, if found.</param>
    internal static bool TryGetDisplayItem(SObject machine, [NotNullWhen(true)] out Item? item)
    {
        if (machine.modData.TryGetValue(AutoCrafterMachine.DisplayItemModDataKey, out string? qualifiedItemId) && !string.IsNullOrEmpty(qualifiedItemId))
        {
            item = ItemRegistry.Create(qualifiedItemId, allowNull: true);
            return item != null;
        }

        item = null;
        return false;
    }

    /// <summary>Assign a recipe to the machine, starting the "priming" (1→4) animation transition only if it wasn't already primed.</summary>
    /// <param name="machine">The machine to update.</param>
    /// <param name="recipe">The recipe to assign.</param>
    /// <param name="displayItemQualifiedId">The qualified item ID of the item the player used to assign it, shown floating above the machine.</param>
    internal static void SetAssignedRecipe(SObject machine, CraftingRecipe recipe, string displayItemQualifiedId)
    {
        // MOD: added, per direct request — swapping the assigned recipe while already primed (i.e. a
        // recipe was already assigned before this call) shouldn't replay the priming transition, since
        // the machine is already sitting at fully-extended (frame 4); only a fresh assignment from
        // completely unassigned needs to animate from pressed (1) up to extended (4).
        bool wasAlreadyPrimed = machine.modData.ContainsKey(AutoCrafterMachine.RecipeNameModDataKey);

        machine.modData[AutoCrafterMachine.RecipeNameModDataKey] = recipe.name;
        machine.modData[AutoCrafterMachine.RecipeIsCookingModDataKey] = recipe.isCookingRecipe ? "true" : "false";
        machine.modData[AutoCrafterMachine.DisplayItemModDataKey] = displayItemQualifiedId;

        if (!wasAlreadyPrimed)
            AutoCrafterMachine.StartAnimTransition(machine);
    }

    /// <summary>Remove the machine's assigned recipe, if any, and start the "unpriming" (4→1) animation transition.</summary>
    /// <param name="machine">The machine to update.</param>
    /// <returns>Returns whether a recipe was assigned (and thus removed).</returns>
    internal static bool TryRemoveAssignedRecipe(SObject machine)
    {
        if (!machine.modData.ContainsKey(AutoCrafterMachine.RecipeNameModDataKey))
            return false;

        machine.modData.Remove(AutoCrafterMachine.RecipeNameModDataKey);
        machine.modData.Remove(AutoCrafterMachine.RecipeIsCookingModDataKey);
        machine.modData.Remove(AutoCrafterMachine.DisplayItemModDataKey);
        AutoCrafterMachine.StartAnimTransition(machine);
        return true;
    }

    /// <summary>Get the real-time <see cref="Game1.currentGameTime"/> milliseconds at which the last prime/unprime animation transition started.</summary>
    /// <param name="machine">The machine to check.</param>
    internal static double GetAnimStartMs(SObject machine)
    {
        return machine.modData.TryGetValue(AutoCrafterMachine.AnimStartModDataKey, out string? raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)
            ? ms
            : 0;
    }

    /// <summary>Get the real-time <see cref="Game1.currentGameTime"/> milliseconds at which the current craft's processing began.</summary>
    /// <param name="machine">The machine to check.</param>
    internal static double GetProcessingStartMs(SObject machine)
    {
        return machine.modData.TryGetValue(AutoCrafterMachine.ProcessingStartMsModDataKey, out string? raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)
            ? ms
            : 0;
    }

    /// <summary>Get the index of the last press cycle the strike particle/sound has already been played for, or -1 if none yet.</summary>
    /// <param name="machine">The machine to check.</param>
    internal static int GetLastHandledStrikeCycle(SObject machine)
    {
        return machine.modData.TryGetValue(AutoCrafterMachine.LastHandledStrikeCycleModDataKey, out string? raw) && int.TryParse(raw, out int cycle)
            ? cycle
            : -1;
    }

    /// <summary>Record that the strike particle/sound has been played for the given press-cycle index.</summary>
    /// <param name="machine">The machine to update.</param>
    /// <param name="cycleIndex">The cycle index just handled — see <see cref="GetLastHandledStrikeCycle"/>.</param>
    internal static void SetLastHandledStrikeCycle(SObject machine, int cycleIndex)
    {
        machine.modData[AutoCrafterMachine.LastHandledStrikeCycleModDataKey] = cycleIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Get the known recipe (if any) that produces the given item, searching only the given player's own learned crafting/cooking recipes.</summary>
    /// <param name="item">The item to match against each candidate recipe's output.</param>
    /// <param name="who">The player whose known recipes to search.</param>
    /// <param name="recipe">The matched recipe, if found.</param>
    internal static bool TryResolveKnownRecipeForItem(Item item, Farmer who, [NotNullWhen(true)] out CraftingRecipe? recipe)
    {
        foreach (string name in who.craftingRecipes.Keys)
        {
            CraftingRecipe candidate = new(name, isCookingRecipe: false);
            if (candidate.itemToProduce.Contains(item.ItemId))
            {
                recipe = candidate;
                return true;
            }
        }

        foreach (string name in who.cookingRecipes.Keys)
        {
            CraftingRecipe candidate = new(name, isCookingRecipe: true);
            if (candidate.itemToProduce.Contains(item.ItemId))
            {
                recipe = candidate;
                return true;
            }
        }

        recipe = null;
        return false;
    }

    /// <summary>Get the recipe (if any) that produces the given item, searching every crafting/cooking recipe in the game regardless of whether any player has learned it — used to tell "this item can't be crafted by anyone" apart from "a recipe exists, but you haven't learned it" (see <see cref="TryResolveKnownRecipeForItem"/>).</summary>
    /// <param name="item">The item to match against each candidate recipe's output.</param>
    /// <param name="recipe">The matched recipe, if found.</param>
    internal static bool TryResolveAnyRecipeForItem(Item item, [NotNullWhen(true)] out CraftingRecipe? recipe)
    {
        foreach (string name in CraftingRecipe.craftingRecipes.Keys)
        {
            CraftingRecipe candidate = new(name, isCookingRecipe: false);
            if (candidate.itemToProduce.Contains(item.ItemId))
            {
                recipe = candidate;
                return true;
            }
        }

        foreach (string name in CraftingRecipe.cookingRecipes.Keys)
        {
            CraftingRecipe candidate = new(name, isCookingRecipe: true);
            if (candidate.itemToProduce.Contains(item.ItemId))
            {
                recipe = candidate;
                return true;
            }
        }

        recipe = null;
        return false;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Record that a prime/unprime animation transition just started, for the draw patch to interpolate from.</summary>
    /// <param name="machine">The machine to update.</param>
    private static void StartAnimTransition(SObject machine)
    {
        double ms = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        machine.modData[AutoCrafterMachine.AnimStartModDataKey] = ms.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Get whether every ingredient in a recipe's ingredient list is currently available, without consuming anything.</summary>
    /// <param name="storage">The storage to check.</param>
    /// <param name="recipeList">The recipe's ingredients, indexed by unqualified item ID or category number.</param>
    private static bool HasAllIngredients(IStorage storage, Dictionary<string, int> recipeList)
    {
        List<ITrackedStack> items = storage.GetItems().ToList();

        bool hasAll = true;
        foreach (KeyValuePair<string, int> entry in recipeList)
        {
            int available = items.Where(stack => AutoCrafterMachine.MatchesIngredient(stack.Sample, entry.Key)).Sum(stack => stack.Count);
            if (available < entry.Value)
                hasAll = false;
        }

        return hasAll;
    }

    /// <summary>Get whether an item matches a <see cref="StardewValley.CraftingRecipe.recipeList"/> ingredient key.</summary>
    /// <param name="item">The item to check.</param>
    /// <param name="ingredientKey">The ingredient key — a category number, an unqualified item ID, OR (MOD: fixed — this case was missing) a fully qualified ID like <c>"(BC)130"</c>, which is how a bigcraftable ingredient is written to disambiguate it from an Object sharing the same numeric ID. A plain <c>item.ItemId == ingredientKey</c> check (the previous implementation) silently never matched any qualified key, so recipes needing a bigcraftable ingredient (e.g. the Powered Chest's own recipe, which needs a vanilla Chest) always looked short on that ingredient even with hundreds sitting in the connected chest. Delegates to vanilla's own <see cref="CraftingRecipe.ItemMatchesForCrafting"/> instead of re-deriving the same qualified/unqualified/category resolution vanilla crafting already gets right.</param>
    private static bool MatchesIngredient(Item item, string ingredientKey)
    {
        return CraftingRecipe.ItemMatchesForCrafting(item, ingredientKey);
    }
}

using HarmonyLib;
using StardewValley.Menus;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Overrides the "Learned the crafting recipe 'X'" confirmation text shown at the bottom
/// of the Cave Carrot Request's completion letter (see PoweredAutomation/Data/SpecialOrdersData.json),
/// which grants all four quality tag recipes via four separate <c>%item craftingrecipe</c> mail
/// commands in one letter. Vanilla's own <see cref="LetterViewerMenu.HandleItemCommand"/> only tracks
/// the LAST recipe granted for that confirmation line, so without this it would read as though only
/// "Iridium Quality Tag" was learned — even though all four are silently added to the player's
/// crafting recipes regardless of what this one leftover display field says. Renaming the recipe
/// itself (its display-name-override field in Data/CraftingRecipes) would fix this same text, but
/// would also rename it everywhere else the recipe is shown (the crafting menu's recipe list, the
/// Dwarf's shop listing) — so this only overrides the field this specific letter's confirmation line
/// happens to read from, after vanilla's own recipe-granting logic has already run.
/// </summary>
internal static class CaveCarrotLetterPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The mail ID of the Cave Carrot Request's completion letter (see Data/mail in PoweredAutomation/Data/SpecialOrdersData.json).</summary>
    private const string TargetMailId = "luisMint.PoweredAutomation_CaveCarrotRequestComplete";


    /*********
    ** Public methods
    *********/
    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(LetterViewerMenu), nameof(LetterViewerMenu.HandleItemCommand)),
            postfix: new HarmonyMethod(typeof(CaveCarrotLetterPatches), nameof(HandleItemCommand_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Replace the last-granted recipe's own display name with a generic label, for this one letter only.</summary>
    /// <param name="__instance">The letter menu whose mail command was just parsed.</param>
    private static void HandleItemCommand_Postfix(LetterViewerMenu __instance)
    {
        if (__instance.mailTitle != CaveCarrotLetterPatches.TargetMailId || string.IsNullOrEmpty(__instance.learnedRecipe))
            return;

        __instance.learnedRecipe = I18n.Message_QualityTagsRecipeName();
    }
}

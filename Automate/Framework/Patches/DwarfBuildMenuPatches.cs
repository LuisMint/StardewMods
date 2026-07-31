using HarmonyLib;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Gives the Dwarf a "build a Power Silo" option, reusing vanilla's own carpenter menu (via
/// <see cref="GameLocation.ShowConstructOptions"/>) rather than a custom one — that method already accepts
/// any "Builder" string (see the Power Silo's own <c>Data/Buildings</c> entry, tagged <c>"Builder": "Dwarf"</c>
/// in <c>AutomatePowerPipes</c>), so the only real gap is a trigger: vanilla only ever calls it with a
/// hardcoded <c>"Robin"</c> (behind her Carpenter shop's dialogue) or <c>"Wizard"</c> (behind a specific
/// tower tile action), neither of which is reachable for a new builder name without a patch.
///
/// Rather than patching <see cref="NPC.checkAction"/> directly (which would mean re-implementing its own
/// gift-giving/queued-dialogue preconditions to avoid breaking them), this patches
/// <see cref="Utility.TryOpenShopMenu(string, string, bool)"/> instead — the exact call vanilla's own
/// <see cref="NPC.checkAction"/> makes for the Dwarf ONLY once every one of its own preconditions (holding
/// no giftable item, <see cref="Farmer.canUnderstandDwarves"/>, standing in a Mine level, no queued
/// dialogue, etc.) already passed. Intercepting there means every one of those checks is inherited for
/// free, with nothing to duplicate or risk getting subtly wrong. Per direct user preference, the build
/// option is gated behind the SAME "can understand the Dwarf" requirement the shop itself already uses,
/// rather than being available any earlier.
///
/// Separately patches <see cref="CarpenterMenu.robinConstructionMessage"/> — vanilla hardcodes THAT
/// confirmation message to Robin's own portrait/dialogue regardless of which "Builder" string the menu
/// was opened with, so without this patch a Dwarf-built structure would still show Robin announcing it.
/// </summary>
internal static class DwarfBuildMenuPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The <c>Data/Buildings</c> "Builder" value the Power Silo (and any future Dwarf-built building) is tagged with — must match the value set in <c>AutomatePowerPipes</c>' <c>BuildingsData.json</c>.</summary>
    private const string BuilderName = "Dwarf";

    /// <summary>
    /// MOD: added. Guards against re-entering <see cref="TryOpenShopMenu_Prefix"/> when the "Shop"
    /// response below calls <see cref="Utility.TryOpenShopMenu(string, string, bool)"/> again to actually
    /// open the Dwarf's shop — without this, that call would just show the SAME question dialogue a
    /// second time instead of ever reaching the real shop.
    /// </summary>
    private static bool IsOpeningRealShop;


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Utility), nameof(Utility.TryOpenShopMenu), [typeof(string), typeof(string), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(DwarfBuildMenuPatches), nameof(TryOpenShopMenu_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(CarpenterMenu), nameof(CarpenterMenu.robinConstructionMessage)),
            prefix: new HarmonyMethod(typeof(DwarfBuildMenuPatches), nameof(RobinConstructionMessage_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Offer a "Shop"/"Construct Dwarf Structures"/"Leave" choice instead of opening the Dwarf's shop directly.</summary>
    /// <param name="shopId">The shop being opened.</param>
    /// <param name="ownerName">The shop's owner.</param>
    /// <param name="__result">The result to return instead of running the original method.</param>
    private static bool TryOpenShopMenu_Prefix(string shopId, string ownerName, ref bool __result)
    {
        if (shopId != "Dwarf" || DwarfBuildMenuPatches.IsOpeningRealShop || Game1.currentLocation is not { } location)
            return true; // not the Dwarf's shop (or we're already reopening it for real) — let vanilla handle it normally

        Response[] responses =
        [
            new Response("Shop", "Shop"),
            new Response("Build", "Construct Dwarf Structures"),
            new Response("Leave", "Leave")
        ];

        location.createQuestionDialogue("What would you like to do?", responses, (who, whichAnswer) =>
        {
            switch (whichAnswer)
            {
                case "Shop":
                    // MOD: guarded re-entry — see IsOpeningRealShop's own remarks.
                    DwarfBuildMenuPatches.IsOpeningRealShop = true;
                    try
                    {
                        Utility.TryOpenShopMenu(shopId, ownerName);
                    }
                    finally
                    {
                        DwarfBuildMenuPatches.IsOpeningRealShop = false;
                    }
                    break;

                case "Build":
                    // MOD: added — per direct user request, only one Dwarf-built structure may be under
                    // construction at a time; refuse (with an in-character message) rather than opening
                    // the menu at all if one's already in progress somewhere.
                    if (DwarfBuildMenuPatches.IsDwarfStructureUnderConstruction())
                        DwarfBuildMenuPatches.ShowDwarfMessage("Sorry, my hands are tied. One task at a time.");
                    else
                        location.ShowConstructOptions(DwarfBuildMenuPatches.BuilderName);
                    break;
            }
        });

        __result = true;
        return false; // skip vanilla's own immediate shop-opening — replaced by the dialogue above
    }

    /// <summary>Show a Dwarf-specific construction-started message instead of vanilla's hardcoded Robin one, for a structure built through the Dwarf's own menu.</summary>
    /// <param name="__instance">The carpenter menu instance.</param>
    private static bool RobinConstructionMessage_Prefix(CarpenterMenu __instance)
    {
        if (__instance.Builder != DwarfBuildMenuPatches.BuilderName)
            return true; // not a Dwarf-built structure — let vanilla show Robin's own message

        // MOD: mirrors vanilla's own cleanup steps (see the original method) before showing any message.
        __instance.exitThisMenu();
        Game1.player.forceCanMove();

        if (!__instance.Blueprint.MagicalConstruction)
            DwarfBuildMenuPatches.ShowDwarfMessage("Others of my kind will begin work while you sleep. Do not bother them.");

        return false;
    }

    /// <summary>Show a message from the Dwarf, using their own portrait/dialogue box.</summary>
    /// <param name="text">The literal message text to show.</param>
    private static void ShowDwarfMessage(string text)
    {
        if (Game1.getCharacterFromName(DwarfBuildMenuPatches.BuilderName) is { } dwarf)
            Game1.DrawDialogue(new Dialogue(dwarf, "luisMint.AutomatePowerPipes_DwarfConstruction", text));
    }

    /// <summary>Get whether a Dwarf-built structure is currently under construction (or upgrading) anywhere in the save.</summary>
    private static bool IsDwarfStructureUnderConstruction()
    {
        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
            {
                if (building.daysOfConstructionLeft.Value <= 0 && building.daysUntilUpgrade.Value <= 0)
                    continue;

                if (building.GetData()?.Builder == DwarfBuildMenuPatches.BuilderName)
                    return true;
            }
        }

        return false;
    }
}

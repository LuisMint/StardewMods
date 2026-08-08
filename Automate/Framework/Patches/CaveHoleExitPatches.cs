using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using xTile.Dimensions;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Lets the player leave the Cave Hole's interior by facing the tile directly above their
/// arrival spot (see <see cref="CaveHoleInteraction"/>) and interacting with it — a PREFIX on
/// <see cref="GameLocation.checkAction"/>, the same vanilla method the actual Mine's own "Leave"/"Do
/// Nothing" ladder-up prompt is built on (see <see cref="M:StardewValley.Locations.MineShaft.checkAction"/>'s
/// own <c>case 115</c>), mirrored here rather than reimplemented on a tile-property basis: the Mine
/// detects its exit ladder by a specific tile INDEX on its own "Buildings" layer, which would mean hand-
/// editing per-tile properties into the Cave Hole's hand-built map (exactly the kind of fragile manual XML
/// surgery that already caused a parsing bug once — see <see cref="CaveHoleInteraction"/>'s own history).
/// Checking the exact (location, tile) pair here instead needs no map changes at all.
/// </summary>
internal static class CaveHoleExitPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The tile the player must be facing (one tile north of the arrival spot) to trigger the exit prompt.</summary>
    private const int ExitTileX = 13;

    /// <inheritdoc cref="ExitTileX"/>
    private const int ExitTileY = 5;


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.checkAction)),
            prefix: new HarmonyMethod(typeof(CaveHoleExitPatches), nameof(CheckAction_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Show the "Leave Cave Hole" prompt when the player interacts with the exit tile.</summary>
    /// <param name="__instance">The location the action was checked in.</param>
    /// <param name="tileLocation">The tile being interacted with.</param>
    /// <param name="who">The player triggering the action.</param>
    /// <param name="__result">Vanilla's own result — overridden when this handles the action itself.</param>
    /// <returns>Returns whether vanilla's own <see cref="GameLocation.checkAction"/> logic should still run.</returns>
    private static bool CheckAction_Prefix(GameLocation __instance, Location tileLocation, Farmer who, ref bool __result)
    {
        if (!__instance.NameOrUniqueName.StartsWith(CaveHoleInteraction.InteriorLocationBaseName) || tileLocation.X != CaveHoleExitPatches.ExitTileX || tileLocation.Y != CaveHoleExitPatches.ExitTileY || !who.IsLocalPlayer)
            return true; // not our tile — let vanilla handle it normally

        Response[] options =
        [
            new Response("Leave", "Leave Cave Hole").SetHotKey(Keys.Y),
            new Response("Do", "Do Nothing").SetHotKey(Keys.Escape)
        ];
        __instance.createQuestionDialogue(" ", options, CaveHoleExitPatches.OnAnsweredExitPrompt);

        __result = true;
        return false; // handled entirely — skip vanilla's own checkAction
    }

    /// <summary>Handle the player's answer to the "Leave Cave Hole" prompt.</summary>
    /// <param name="who">The player who answered.</param>
    /// <param name="answer">The response key they chose.</param>
    private static void OnAnsweredExitPrompt(Farmer who, string answer)
    {
        if (answer != "Leave")
            return;

        // MOD: falls back to the Farm's default spawn area if the return-trip modData is somehow
        // missing (e.g. debug-warped in directly) rather than failing outright.
        string returnLocation = who.modData.TryGetValue(CaveHoleInteraction.ReturnLocationModDataKey, out string? location) ? location : "Farm";
        int returnX = who.modData.TryGetValue(CaveHoleInteraction.ReturnTileXModDataKey, out string? x) && int.TryParse(x, out int parsedX) ? parsedX : 64;
        int returnY = who.modData.TryGetValue(CaveHoleInteraction.ReturnTileYModDataKey, out string? y) && int.TryParse(y, out int parsedY) ? parsedY : 15;

        Game1.currentLocation.playSound("stairsdown");
        Game1.warpFarmer(returnLocation, returnX, returnY, Game1.down);
    }
}

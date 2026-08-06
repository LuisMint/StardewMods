using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Per direct user request, reading Dwarf Research Note #7 ("White/Black List
/// Category Sign") unlocks a nightly trade at the vanilla Statue Of The Dwarf King
/// (<c>(BC)StatueOfTheDwarfKing</c>, not part of this mod): at exactly midnight, clicking the
/// statue while holding a Diamond consumes it and drops a Dwarf Gadget out of the statue, once per
/// statue per day.
///
/// A PREFIX on <see cref="SObject.checkForAction"/>, mirroring <see cref="PowerRelayInteraction"/>'s
/// own "holding the right item swaps the click's meaning" shape: the vanilla Statue's own click
/// handling (choosing a daily mining power) is untouched in every other case — only when the time,
/// held item, note, and once-per-day conditions all line up does this short-circuit the click into
/// the trade instead, returning <c>false</c> to skip vanilla's own handling for that one click.
/// </summary>
internal static class DwarfKingStatueTradePatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The qualified item ID of the vanilla Statue Of The Dwarf King.</summary>
    private const string StatueItemId = "(BC)StatueOfTheDwarfKing";

    /// <summary>The in-game time (<see cref="Game1.timeOfDay"/>) the trade is available at — midnight.</summary>
    private const int TradeTime = 2400;

    /// <summary>The qualified item ID the statue asks for.</summary>
    private const string CostItemId = "(O)72"; // Diamond

    /// <summary>The qualified item ID the statue gives.</summary>
    private const string RewardItemId = "(O)122"; // Dwarf Gadget

    /// <summary>The <see cref="SObject.modData"/> key on the statue tracking the <see cref="StardewValley.Stats.DaysPlayed"/> value it last traded on, so it only triggers once per statue per day.</summary>
    private const string LastTradeDayModDataKey = "luisMint.AutomatePowerPipes/DwarfKingStatueTradeDay";


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.checkForAction)),
            prefix: new HarmonyMethod(typeof(DwarfKingStatueTradePatches), nameof(CheckForAction_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Trade a held Diamond for a Dwarf Gadget at midnight, once per statue per day, after Dwarf Research Note #7 has been read.</summary>
    /// <param name="__instance">The object being interacted with.</param>
    /// <param name="who">The player interacting.</param>
    /// <param name="justCheckingForActivity">Whether this is a non-committal check (e.g. for cursor hover) rather than a real click.</param>
    /// <param name="__result">The result to return if this handles the interaction.</param>
    /// <returns><c>false</c> (skip vanilla's own handling) if this handled the interaction; <c>true</c> to let vanilla run as normal otherwise.</returns>
    private static bool CheckForAction_Prefix(SObject __instance, Farmer who, bool justCheckingForActivity, ref bool __result)
    {
        if (justCheckingForActivity)
            return true;

        if (__instance.QualifiedItemId != DwarfKingStatueTradePatches.StatueItemId)
            return true;

        if (Game1.timeOfDay != DwarfKingStatueTradePatches.TradeTime)
            return true;

        SObject? held = who.ActiveObject;
        if (held is null || held.QualifiedItemId != DwarfKingStatueTradePatches.CostItemId)
            return true;

        int today = (int)Game1.stats.DaysPlayed;
        if (__instance.modData.TryGetValue(DwarfKingStatueTradePatches.LastTradeDayModDataKey, out string? raw) && int.TryParse(raw, out int lastDay) && lastDay == today)
            return true; // already traded with this statue today — fall through to vanilla's normal click handling

        if (!DwarfResearchNotes.HasRead(who, DwarfResearchNotes.CategorySignNoteId))
            return true;

        held.Stack -= 1;
        if (held.Stack <= 0)
            who.removeItemFromInventory(held);

        __instance.modData[DwarfKingStatueTradePatches.LastTradeDayModDataKey] = today.ToString();

        GameLocation location = __instance.Location;
        Vector2 tile = __instance.TileLocation;
        Game1.createItemDebris(
            item: ItemRegistry.Create(DwarfKingStatueTradePatches.RewardItemId),
            pixelOrigin: new Vector2(tile.X * 64f + 32f, tile.Y * 64f + 32f),
            direction: 2,
            location: location
        );
        location.playSound("give_gift");

        __result = true;
        return false;
    }
}

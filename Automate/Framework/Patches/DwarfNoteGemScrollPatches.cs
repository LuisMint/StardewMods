using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Per direct user request, having read Dwarf Research Notes #1, #2, and #3 (Input,
/// Output, and Omni Conduit) unlocks a one-time gem drop on the Mountain map: standing on tile
/// (37,37), facing tile (37,38) (i.e. facing down), while holding any Dwarf Scroll (I-IV) and
/// pressing the action button consumes the scroll and drops a Ruby, a Jade, and an Aquamarine, once
/// ever for the whole save.
///
/// A PREFIX on <see cref="GameLocation.checkAction"/>, the universal "player pressed the action
/// button" entry point — deliberately not tied to whatever's actually on the tile (there's nothing
/// there to interact with), since the real trigger is the player's own position/facing/held item,
/// not the tile's contents.
/// </summary>
internal static class DwarfNoteGemScrollPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The name of the location the hidden spot is on.</summary>
    private const string TreasureLocationName = "Mountain";

    /// <summary>The tile the player must be standing on.</summary>
    private static readonly Point StandingTile = new(37, 37);

    /// <summary>The <see cref="Character.FacingDirection"/> value for "down" — the player must be facing tile (37,38), directly below <see cref="StandingTile"/>.</summary>
    private const int RequiredFacingDirection = 2;

    /// <summary>The qualified item IDs of the four Dwarf Scrolls — any one of them satisfies the requirement.</summary>
    private static readonly string[] DwarfScrollItemIds = ["(O)96", "(O)97", "(O)98", "(O)99"]; // Dwarf Scroll I-IV

    /// <summary>The qualified item IDs dropped, one of each.</summary>
    private static readonly string[] RewardItemIds = ["(O)64", "(O)70", "(O)62"]; // Ruby, Jade, Aquamarine

    /// <summary>The <see cref="Farmer.modData"/> key on <see cref="Game1.MasterPlayer"/> tracking whether the reward has already been claimed. Deliberately global (not per-player), since it's a one-time find for the whole save, not one per player.</summary>
    private const string ClaimedModDataKey = "luisMint.PoweredAutomation/DwarfNoteGemScrollClaimed";


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.checkAction)),
            prefix: new HarmonyMethod(typeof(DwarfNoteGemScrollPatches), nameof(CheckAction_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Drop a Ruby, Jade, and Aquamarine the first time a player stands on the hidden Mountain spot facing the right way while holding a Dwarf Scroll, after Dwarf Research Notes #1-#3 have all been read.</summary>
    /// <param name="__instance">The location being interacted with.</param>
    /// <param name="who">The player interacting.</param>
    /// <param name="__result">The result to return if this handles the interaction.</param>
    /// <returns><c>false</c> (skip vanilla's own handling) if this handled the interaction; <c>true</c> to let vanilla run as normal otherwise.</returns>
    private static bool CheckAction_Prefix(GameLocation __instance, Farmer who, ref bool __result)
    {
        if (__instance.Name != DwarfNoteGemScrollPatches.TreasureLocationName)
            return true;

        if (who.TilePoint.X != DwarfNoteGemScrollPatches.StandingTile.X || who.TilePoint.Y != DwarfNoteGemScrollPatches.StandingTile.Y)
            return true;

        if (who.FacingDirection != DwarfNoteGemScrollPatches.RequiredFacingDirection)
            return true;

        SObject? held = who.ActiveObject;
        if (held is null || !DwarfNoteGemScrollPatches.DwarfScrollItemIds.Contains(held.QualifiedItemId))
            return true;

        if (DwarfNoteGemScrollPatches.HasClaimedGlobally())
            return true;

        if (!DwarfResearchNotes.HasRead(who, DwarfResearchNotes.InputConduitNoteId)
            || !DwarfResearchNotes.HasRead(who, DwarfResearchNotes.OutputConduitNoteId)
            || !DwarfResearchNotes.HasRead(who, DwarfResearchNotes.OmniConduitNoteId))
            return true;

        held.Stack -= 1;
        if (held.Stack <= 0)
            who.removeItemFromInventory(held);

        DwarfNoteGemScrollPatches.MarkClaimedGlobally();

        Point tile = DwarfNoteGemScrollPatches.StandingTile;
        foreach (string itemId in DwarfNoteGemScrollPatches.RewardItemIds)
        {
            Game1.createItemDebris(
                item: ItemRegistry.Create(itemId),
                pixelOrigin: new Vector2(tile.X * 64 + 32, tile.Y * 64 + 32),
                direction: 2,
                location: __instance
            );
        }
        __instance.playSound("give_gift"); // MOD: the usual reward "ding" this mod's other Dwarf Research Note secrets use

        __result = true;
        return false;
    }

    /// <summary>Get whether the hidden spot's reward has already been claimed, for the whole save.</summary>
    private static bool HasClaimedGlobally()
    {
        return Game1.MasterPlayer.modData.ContainsKey(DwarfNoteGemScrollPatches.ClaimedModDataKey);
    }

    /// <summary>Mark the hidden spot's reward as claimed, for the whole save.</summary>
    private static void MarkClaimedGlobally()
    {
        Game1.MasterPlayer.modData[DwarfNoteGemScrollPatches.ClaimedModDataKey] = "true";
    }
}

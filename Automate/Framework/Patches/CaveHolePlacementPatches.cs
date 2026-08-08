using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Blocks placing anything on the Cave Hole interior's arrival/departure tile — per direct
/// user request, since it's the exact spot the player warps in and out on (see
/// <see cref="CaveHoleInteraction"/>/<see cref="CaveHoleExitPatches"/>), and something placed there (a
/// chest, a crafting station, anything) would visually collide with the player every time they arrive.
///
/// A POSTFIX on <see cref="GameLocation.CanItemBePlacedHere"/> — the single choke point vanilla itself
/// uses for "is this tile free to place something on," checked both by the placement-preview highlight
/// and the actual placement action, so overriding its result here covers both without needing to hook
/// the placement action itself separately.
/// </summary>
internal static class CaveHolePlacementPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The tile that must always stay clear — the Cave Hole's arrival/departure spot.</summary>
    private static readonly Vector2 BlockedTile = new(13, 6);


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.CanItemBePlacedHere)),
            postfix: new HarmonyMethod(typeof(CaveHolePlacementPatches), nameof(CanItemBePlacedHere_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Force the result to <c>false</c> for the Cave Hole's own arrival tile.</summary>
    /// <param name="__instance">The location being checked.</param>
    /// <param name="tile">The tile being checked.</param>
    /// <param name="__result">Vanilla's own result, overridden in place.</param>
    private static void CanItemBePlacedHere_Postfix(GameLocation __instance, Vector2 tile, ref bool __result)
    {
        if (__result && tile == CaveHolePlacementPatches.BlockedTile && __instance.NameOrUniqueName.StartsWith(CaveHoleInteraction.InteriorLocationBaseName))
            __result = false;
    }
}

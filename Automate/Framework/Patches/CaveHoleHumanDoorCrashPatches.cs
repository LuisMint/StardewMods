using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Fixes a crash discovered in play: <see cref="Building.doAction"/> has a "walked into the
/// human door" branch (<c>tileLocation == humanDoor</c>) that's meant to warp the player into the
/// building's interior via <c>interior.warps[0]</c>. Elsewhere in the same class (see its own
/// <c>ToggleAnimalDoor</c>-adjacent tile check) vanilla guards this sentinel with <c>humanDoor.X &gt;= 0</c>
/// before trusting it, since <c>{X:-1, Y:-1}</c> means "this building has no door" — but the door-click
/// branch itself is missing that same guard. Every vanilla building either has a real door (and a real
/// interior with real Warp points) or no interior at all, so this gap is never hit in the base game.
///
/// The Cave Hole is the first building in this mod with BOTH an <see cref="StardewValley.GameData.Buildings.BuildingData.IndoorMap"/>
/// AND <see cref="StardewValley.GameData.Buildings.BuildingData.HumanDoor"/> left at the "no door"
/// sentinel (entry/exit is handled entirely by <see cref="CaveHoleInteraction"/> and
/// <see cref="CaveHoleExitPatches"/> instead), which exposes the gap: <c>humanDoor.X + tileX</c> resolves
/// to a real, walkable tile just outside the building's footprint, and the Cave Hole's own interior map
/// has zero vanilla Warp points (by design — see <see cref="CaveHoleExitPatches"/>'s own remarks on why),
/// so <c>interior.warps[0]</c> throws <see cref="System.ArgumentOutOfRangeException"/> if the player ever
/// faces that tile and interacts with it.
///
/// A PREFIX on <see cref="Building.doAction"/>: skips vanilla's door-click branch entirely for Cave Holes,
/// since they were never meant to have vanilla door-entry behavior in the first place.
/// </summary>
internal static class CaveHoleHumanDoorCrashPatches
{
    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Building), nameof(Building.doAction)),
            prefix: new HarmonyMethod(typeof(CaveHoleHumanDoorCrashPatches), nameof(DoAction_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Skip vanilla's door-click handling when it would hit a Cave Hole's unset "no door" sentinel.</summary>
    /// <param name="__instance">The building being interacted with.</param>
    /// <param name="tileLocation">The tile being interacted with.</param>
    /// <param name="__result">The result to return if this handles the interaction.</param>
    /// <returns><c>false</c> (skip vanilla) if this is a Cave Hole's phantom door tile; <c>true</c> otherwise.</returns>
    private static bool DoAction_Prefix(Building __instance, Vector2 tileLocation, ref bool __result)
    {
        if (!CaveHoleInteraction.IsCaveHoleBuildingType(__instance.buildingType.Value) || __instance.humanDoor.X >= 0)
            return true;

        if (tileLocation.X != __instance.humanDoor.X + __instance.tileX.Value || tileLocation.Y != __instance.humanDoor.Y + __instance.tileY.Value)
            return true;

        __result = false;
        return false;
    }
}

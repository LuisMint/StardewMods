using HarmonyLib;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Keeps a Cave Hole interior's <see cref="GameLocation.Name"/> in sync with its own stable
/// <see cref="GameLocation.NameOrUniqueName"/> after an upgrade — vanilla's own
/// <see cref="Building.LoadFromBuildingData"/> swaps <c>mapPath</c> to the new tier's <c>IndoorMap</c> in
/// place (reusing the SAME location instance — see <see cref="CaveHoleInteraction"/>'s own remarks) and,
/// as part of that map swap, reassigns <see cref="GameLocation.Name"/> to match the NEWLY loaded map's own
/// identity (e.g. <c>luisMint.PoweredAutomation_CaveHole2</c> after upgrading to the Big Cave Hole) —
/// while <see cref="GameLocation.uniqueName"/> (which <see cref="GameLocation.NameOrUniqueName"/> actually
/// returns whenever it's set, and which every warp/save/lookup in this mod and vanilla already correctly
/// uses) stays fixed at whatever it was set to at ORIGINAL construction (<c>CaveHole1</c> + a GUID) and is
/// never touched by the upgrade path.
///
/// Confirmed via <c>automate summary</c>'s own location header on a live save: an upgraded Cave Hole
/// printed as <c>luisMint.PoweredAutomation_CaveHole2 (luisMint.PoweredAutomation_CaveHole19da26190-...)</c>
/// — <see cref="GameLocation.Name"/> and the base of <see cref="GameLocation.NameOrUniqueName"/> visibly
/// disagreeing for the exact same location instance. That's an unusual location state no other vanilla
/// building produces (a normal tier upgrade doesn't swap the underlying map file at all), and a very
/// plausible explanation for a live report of Chests Anywhere never finding any chest in this location at
/// all post-upgrade (its own location-category logic reads <see cref="GameLocation.Name"/> directly in
/// places, and a mismatched/duplicate-looking name is exactly the kind of state third-party mods aren't
/// written to expect from an "instanced" location).
///
/// Re-syncing <see cref="GameLocation.Name"/> back to the stable <see cref="CaveHoleInteraction.InteriorLocationBaseName"/>
/// prefix after every <see cref="Building.LoadFromBuildingData"/> call (construction AND upgrade — harmless
/// to reapply even when it's already correct) keeps this location's identity internally consistent instead
/// of silently drifting to whichever tier's map happened to load last.
/// </summary>
internal static class CaveHoleLocationNamePatches
{
    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Building), nameof(Building.LoadFromBuildingData)),
            postfix: new HarmonyMethod(typeof(CaveHoleLocationNamePatches), nameof(LoadFromBuildingData_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Re-sync a Cave Hole interior's <see cref="GameLocation.Name"/> after vanilla's own load/upgrade logic runs.</summary>
    /// <param name="__instance">The building being loaded.</param>
    private static void LoadFromBuildingData_Postfix(Building __instance)
    {
        if (!CaveHoleInteraction.IsCaveHoleBuildingType(__instance.buildingType.Value))
            return;

        if (__instance.indoors.Value is not { } indoors || indoors.Name == CaveHoleInteraction.InteriorLocationBaseName)
            return;

        // MOD: added — Name has no public setter (see this class's own remarks for why writing it is
        // necessary here), so the underlying NetField write below is unavoidable, not an oversight.
#pragma warning disable AvoidNetField
        indoors.name.Value = CaveHoleInteraction.InteriorLocationBaseName;
#pragma warning restore AvoidNetField
    }
}

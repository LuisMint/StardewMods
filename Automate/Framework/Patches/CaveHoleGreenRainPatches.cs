using HarmonyLib;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Swaps any still-uncleared Green Rain Weeds inside a Cave Hole interior to regular weeds
/// (see <see cref="CaveHoleQuarrySystem.SwapRemainingGreenRainWeeds"/>), the exact moment vanilla itself
/// decides "it's the day after Green Rain, clean this up" — a PREFIX on
/// <see cref="GameLocation.performDayAfterGreenRainUpdate"/>, called by <see cref="Game1"/>'s own new-day
/// setup coroutine for every location once <c>yesterdayWasGreenRain</c> is true.
///
/// An earlier version of this fix instead checked a plain "yesterday was Green Rain" flag the next time
/// <see cref="CaveHoleQuarrySystem.Tick"/> ran (from <c>ModEntry.OnDayStarted</c>) — that looked broken
/// (confirmed via diagnostic logging: consistently found 0 Green Rain Weeds left to swap, every time,
/// across every Cave Hole) because vanilla's own <see cref="GameLocation.performDayAfterGreenRainUpdate"/>
/// unconditionally DELETES anything named "GreenRainWeeds" (no replacement) as part of the SAME new-day
/// setup coroutine — which runs well before SMAPI's <c>OnDayStarted</c> event ever fires for mods, so
/// vanilla's own deletion always won the race first. Running our own swap as a prefix on that exact
/// method — before vanilla's own body gets a chance to run — sidesteps the race entirely: our swap
/// renames every Green Rain Weeds object to a regular one first, so vanilla's own subsequent deletion
/// pass (which snapshots <c>objects.Pairs</c> itself) simply finds nothing left to delete for us.
/// </summary>
internal static class CaveHoleGreenRainPatches
{
    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.performDayAfterGreenRainUpdate)),
            prefix: new HarmonyMethod(typeof(CaveHoleGreenRainPatches), nameof(PerformDayAfterGreenRainUpdate_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Swap any still-uncleared Green Rain Weeds to regular weeds before vanilla's own cleanup deletes them outright.</summary>
    /// <param name="__instance">The location vanilla is running its day-after-Green-Rain cleanup on.</param>
    private static void PerformDayAfterGreenRainUpdate_Prefix(GameLocation __instance)
    {
        if (__instance.NameOrUniqueName.StartsWith(CaveHoleInteraction.InteriorLocationBaseName))
            CaveHoleQuarrySystem.SwapRemainingGreenRainWeeds(__instance);
    }
}

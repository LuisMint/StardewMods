using System.Linq;
using HarmonyLib;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Gives each Cave Hole/Big Cave Hole interior a display name unique to that ONE physical
/// building — "Cave Hole" for the first of a given tier, "Cave Hole #1", "Cave Hole #2", etc. for any
/// others (matching the numbering convention already familiar from multiple Sheds/Cabins) — instead of
/// the generic string every instance of a tier shares (<c>Data/Buildings</c>' <c>Name</c> field is the
/// literal same text — "Cave Hole" or "Big Cave Hole" — for every instance).
///
/// <see cref="GameLocation.GetDisplayName"/> normally returns null for an instanced building interior
/// like this one (there's no matching <c>Data/Locations</c> entry for a dynamically-named per-building
/// map), so any code that needs SOME display text falls back to the parent building's own generic
/// <c>Data/Buildings</c> name — confirmed as Chests Anywhere's own fallback
/// (<c>location.ParentBuilding?.GetData()?.Name</c>). With more than one Cave Hole/Big Cave Hole on the
/// farm, that generic name collides, and Chests Anywhere disambiguates collisions by appending "(2)",
/// "(3)", etc. itself — but that suffix's NUMBER depends on how many same-named locations happen to
/// appear together in whichever specific scan produced it, so two different scans that don't cover the
/// exact same set of locations (e.g. one scoped to a single location, one unscoped/world-wide) can assign
/// a DIFFERENT number to the SAME physical Cave Hole in each. Confirmed as the actual root cause of a live
/// report: with two Big Cave Holes on the farm, Chests Anywhere's "Current Location" range mode (which
/// scans exactly one location at a time, so its own category text for whichever Cave Hole you're standing
/// in never gets a numbered suffix — there's nothing else in a single-location scan to collide with)
/// crashed specifically for the SECOND Big Cave Hole, because the currently-open chest's own category —
/// found via a separate, all-locations scan elsewhere in Chests Anywhere's own code — DID carry a "(2)"
/// suffix there, leaving the single-location scan's own category list with zero entries matching a
/// category name it could never itself produce.
///
/// Giving every Cave Hole a genuinely unique display name removes the collision at its source, instead of
/// depending on a third-party mod's own PER-SCAN disambiguation numbering staying consistent across scans
/// it has no way to know are scoped differently. The numbering here is computed fresh from a full,
/// unscoped scan of every Cave Hole of the same tier every time (see <see cref="GetStableIndexAmongSameTier"/>),
/// ordered by a stable, always-available sort key (parent location, then tile position) — NOT by "how many
/// were seen so far in this particular caller's scan," which is exactly what made the built-in vanilla
/// disambiguation inconsistent in the first place. This can't perfectly reflect the order the player
/// actually built them in (nothing persists that), but it's guaranteed to produce the SAME number for the
/// SAME building no matter which range/scope asked for it.
/// </summary>
internal static class CaveHoleUniqueDisplayNamePatches
{
    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.GetDisplayName)),
            postfix: new HarmonyMethod(typeof(CaveHoleUniqueDisplayNamePatches), nameof(GetDisplayName_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Give a Cave Hole interior a unique display name if vanilla didn't already provide one.</summary>
    /// <param name="__instance">The location whose display name was requested.</param>
    /// <param name="__result">Vanilla's own result, overridden in place.</param>
    private static void GetDisplayName_Postfix(GameLocation __instance, ref string? __result)
    {
        if (__result != null || __instance.NameOrUniqueName?.StartsWith(CaveHoleInteraction.InteriorLocationBaseName) != true)
            return; // not a Cave Hole interior, or vanilla already found a real display name — nothing to do

        if (CaveHoleUniqueDisplayNamePatches.FindOwningBuilding(__instance) is not { } owner)
            return; // still under construction, or something went wrong finding its own building

        string baseLabel = owner.GetData()?.Name ?? "Cave Hole";
        int index = CaveHoleUniqueDisplayNamePatches.GetStableIndexAmongSameTier(owner, baseLabel);

        __result = index <= 0 ? baseLabel : $"{baseLabel} #{index}";
    }

    /// <summary>Find the specific <see cref="Building"/> whose own interior is the given location.</summary>
    /// <param name="interior">The interior location to match.</param>
    private static Building? FindOwningBuilding(GameLocation interior)
    {
        return CommonHelper.GetLocations()
            .SelectMany(location => location.buildings)
            .FirstOrDefault(building => ReferenceEquals(building.GetIndoors(), interior));
    }

    /// <summary>
    /// Get this building's stable, scan-scope-independent position (0 = first) among every OTHER Cave Hole
    /// building sharing the same tier's display name, anywhere in the world — see this class's own remarks
    /// for why this is computed fresh from a full scan every time rather than reusing any caller-provided
    /// count.
    /// </summary>
    /// <param name="building">The building to rank.</param>
    /// <param name="baseLabel">The tier's own shared display name (e.g. "Cave Hole" or "Big Cave Hole").</param>
    private static int GetStableIndexAmongSameTier(Building building, string baseLabel)
    {
        var sameTier =
            (
                from location in CommonHelper.GetLocations()
                from candidate in location.buildings
                where CaveHoleInteraction.IsCaveHoleBuildingType(candidate.buildingType.Value)
                where (candidate.GetData()?.Name ?? "Cave Hole") == baseLabel
                orderby location.NameOrUniqueName, candidate.tileX.Value, candidate.tileY.Value
                select candidate
            )
            .ToArray();

        for (int i = 0; i < sameTier.Length; i++)
        {
            if (ReferenceEquals(sameTier[i], building))
                return i;
        }

        return 0; // shouldn't happen (this building didn't find itself in its own scan) — fall back to the bare label rather than an incorrect number
    }
}

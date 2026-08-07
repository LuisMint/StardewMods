using HarmonyLib;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patch that lets the player walk through an Automate-managed whitelist/blacklist
/// (item or category) sign while it's actively "powered" — enforcing its filter, per the same
/// <see cref="SignValidityPatches.SignValidModDataKey"/> modData flag <see cref="SignTextureSync"/>
/// already keeps in sync every time machine groups are rebuilt. While a sign is unpowered — not
/// connected, not part of a valid group, or overridden by another sign of the same kind — it keeps
/// blocking movement like any other placed big-craftable, since this patch leaves vanilla's own
/// <see cref="SObject.isPassable"/> result alone in that case.
/// </summary>
internal static class SignColliderPatches
{
    /*********
    ** Public methods
    *********/
    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.isPassable)),
            postfix: new HarmonyMethod(typeof(SignColliderPatches), nameof(IsPassable_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Let the player walk through a managed sign while it's actively powered.</summary>
    /// <param name="__instance">The object being checked.</param>
    /// <param name="__result">Vanilla's own passability result.</param>
    private static void IsPassable_Postfix(SObject __instance, ref bool __result)
    {
        if (__result)
            return; // already passable

        if (!SignValidityPatches.IsManagedSign(__instance.QualifiedItemId))
            return; // not a sign Automate manages

        if (SignValidityPatches.IsValid(__instance))
            __result = true;
    }
}

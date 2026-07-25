using System;
using System.Collections.Generic;
using HarmonyLib;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patch that lets the player walk through an Automate-managed whitelist/blacklist
/// (item or category) sign while it's actively "powered" — enforcing its filter, per the same
/// <c>AlternativeTextureVariation</c> modData flag <see cref="SignTextureSync"/> already keeps in sync
/// every time machine groups are rebuilt (see that class's remarks for exactly what "powered" means
/// here). While a sign is unpowered — not connected, not part of a valid group, or overridden by
/// another sign of the same kind — it keeps blocking movement like any other placed big-craftable, since
/// this patch leaves vanilla's own <see cref="SObject.isPassable"/> result alone in that case.
/// </summary>
internal static class SignColliderPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the configured mapping of a sign's qualified item ID to its Alternative Textures texture ID, set via <see cref="Initialize"/> — used here only to recognize which placed objects are Automate-managed signs, not for the texture ID itself.</summary>
    private static Func<Dictionary<string, string>>? GetSignTextureIds;


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the config accessor needed to recognize which signs this applies to. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="getSignTextureIds">Get the configured mapping of a sign's qualified item ID to its Alternative Textures texture ID.</param>
    public static void Initialize(Func<Dictionary<string, string>> getSignTextureIds)
    {
        SignColliderPatches.GetSignTextureIds = getSignTextureIds;
    }

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
        if (__result || SignColliderPatches.GetSignTextureIds == null)
            return; // already passable, or not initialized yet

        Dictionary<string, string> signTextureIds = SignColliderPatches.GetSignTextureIds();
        if (!signTextureIds.ContainsKey(__instance.QualifiedItemId))
            return; // not a sign Automate manages

        if (__instance.modData.TryGetValue("AlternativeTextureVariation", out string? variation) && variation == SignTextureSync.ValidVariation.ToString())
            __result = true;
    }
}

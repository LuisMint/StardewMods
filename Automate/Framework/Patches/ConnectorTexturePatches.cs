using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Swaps a connector floor tile's world texture between its normal ("powered"),
/// dedicated "_UnPowered", and two opacity-blended intermediate ("_Dimmer"/"_Dimmest" — the powered
/// texture composited over the unpowered one at 66%/33% opacity, baked as static PNGs) variants,
/// replacing the previous Alternative Textures-driven swap
/// (<see cref="PoweredFloorSync"/>/<see cref="PoweredFloorAnimator"/> now write
/// <see cref="ConnectorVariantModDataKey"/> here instead of AT's own <c>modData</c> keys).
///
/// A POSTFIX on <see cref="Flooring.GetTexture"/> (not a full override of <see cref="Flooring.draw"/>):
/// that single method is the ONE place vanilla resolves which texture a tile's whole draw pass uses —
/// every corner/edge/shadow/random-variant case in <see cref="Flooring.draw"/> just reads whatever
/// <see cref="Flooring.GetTexture"/> returns once at the top and reuses it, so overriding the return
/// value here lets every one of that method's neighbor-connection/shape cases keep working completely
/// unmodified, without needing to reimplement any of that sprawling switch-based logic ourselves.
///
/// MOD: a "connectors sometimes never animate when first placed" bug was chased for a while and briefly
/// "fixed" by replacing this with a full <see cref="Flooring.draw"/> prefix (recomputing neighbor
/// connectivity manually and drawing everything ourselves) — that change had ZERO effect on the actual
/// bug, which was the tell that the real cause was never in THIS mod's rendering at all: a stale
/// Alternative Textures content pack (<c>Mods/[AT]AutomatePowerPipes</c>, left over from before these
/// connectors were migrated off AT) was still installed and still actively patching <see cref="Flooring"/>
/// rendering with its own <c>DefaultVariation: 0</c> config for these exact 3 connector types, racing
/// against this mod's own patch for every draw call. Removing that leftover content pack was the actual
/// fix, so this reverted back to the simpler/cheaper postfix-on-GetTexture approach rather than keeping
/// the pricier full-draw-prefix rewrite.
///
/// The two static states (fully powered, fully unpowered) each draw ONE plain texture, same as
/// <see cref="Patches.PowerCoilPatches"/>/<see cref="Patches.SignValidityPatches"/> already do for
/// their own powered/unpowered swaps. Only the THIRD state — powered but not part of a valid (active)
/// automation group, previously Alternative Textures' 4-frame ping-pong — actually needed a texture
/// drawn over another texture at reduced opacity; since <see cref="Flooring.draw"/>
/// has no per-call alpha/tint hook to inject a live blend into, the two INTERMEDIATE frames of that
/// animation are pre-baked once as their own flat PNGs (see the Pipes/*_Dimmer.png, *_Dimmest.png
/// files) rather than composited live — visually identical to a real-time overlay, since the source
/// art and opacity levels are fixed, but far simpler and safer than trying to inject a partial-alpha
/// draw into vanilla's own rendering.
/// </summary>
internal static class ConnectorTexturePatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The <see cref="Flooring.modData"/> key storing which of the 4 texture variants a managed connector tile currently shows — <c>"0"</c> (unpowered), <c>"1"</c> (powered), <c>"2"</c> (dimmer), or <c>"3"</c> (dimmest). Written by <see cref="PoweredFloorSync"/> (the two static states) and <see cref="PoweredFloorAnimator"/> (the animated "orphaned" state); missing entirely defaults to unpowered.</summary>
    internal const string ConnectorVariantModDataKey = "luisMint.PoweredAutomation/ConnectorVariant";

    /// <summary>The variant index shown for a fully unpowered connector — also the default when <see cref="ConnectorVariantModDataKey"/> is missing.</summary>
    internal const int UnpoweredVariant = 0;

    /// <summary>The variant index shown for a fully powered (and part of a valid group) connector.</summary>
    internal const int PoweredVariant = 1;

    /// <summary>The variant index shown mid-animation, closer to powered — see this class's own remarks for why this is a pre-baked opacity blend rather than a live one.</summary>
    internal const int DimmerVariant = 2;

    /// <summary>The variant index shown mid-animation, closer to unpowered — see this class's own remarks for why this is a pre-baked opacity blend rather than a live one.</summary>
    internal const int DimmestVariant = 3;

    /// <summary>Every managed connector's floor ID (<see cref="Flooring.whichFloor"/>) mapped to its 4 variant asset names, indexed the same way as <see cref="UnpoweredVariant"/>/<see cref="PoweredVariant"/>/<see cref="DimmerVariant"/>/<see cref="DimmestVariant"/>.</summary>
    private static readonly Dictionary<string, string[]> AssetNamesByFloorId = new()
    {
        ["luisMint.PoweredAutomation_PullPushPipe"] =
        [
            "Mods/luisMint.PoweredAutomation/Pipes_UnPowered",
            "Mods/luisMint.PoweredAutomation/Pipes",
            "Mods/luisMint.PoweredAutomation/Pipes_Dimmer",
            "Mods/luisMint.PoweredAutomation/Pipes_Dimmest"
        ],
        ["luisMint.PoweredAutomation_InputPipe"] =
        [
            "Mods/luisMint.PoweredAutomation/PipesInput_UnPowered",
            "Mods/luisMint.PoweredAutomation/PipesInput",
            "Mods/luisMint.PoweredAutomation/PipesInput_Dimmer",
            "Mods/luisMint.PoweredAutomation/PipesInput_Dimmest"
        ],
        ["luisMint.PoweredAutomation_OutputPipe"] =
        [
            "Mods/luisMint.PoweredAutomation/PipesOutput_UnPowered",
            "Mods/luisMint.PoweredAutomation/PipesOutput",
            "Mods/luisMint.PoweredAutomation/PipesOutput_Dimmer",
            "Mods/luisMint.PoweredAutomation/PipesOutput_Dimmest"
        ]
    };


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Flooring), nameof(Flooring.GetTexture)),
            postfix: new HarmonyMethod(typeof(ConnectorTexturePatches), nameof(GetTexture_Postfix))
        );
    }

    /// <summary>Get whether a floor ID is one of the connectors managed by this class (i.e. has dedicated variant textures to swap between).</summary>
    /// <param name="floorId">The floor ID (<see cref="Flooring.whichFloor"/>) to check.</param>
    internal static bool IsManagedConnector(string floorId)
    {
        return ConnectorTexturePatches.AssetNamesByFloorId.ContainsKey(floorId);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Override a managed connector tile's resolved texture to match its currently-stamped variant.</summary>
    /// <param name="__instance">The floor tile whose texture was just resolved.</param>
    /// <param name="__result">Vanilla's own resolved texture; overridden in place for a managed connector.</param>
    private static void GetTexture_Postfix(Flooring __instance, ref Texture2D __result)
    {
        if (!ConnectorTexturePatches.AssetNamesByFloorId.TryGetValue(__instance.whichFloor.Value, out string[]? assetNames))
            return;

        int variant = __instance.modData.TryGetValue(ConnectorTexturePatches.ConnectorVariantModDataKey, out string? raw) && int.TryParse(raw, out int parsed)
            ? parsed
            : ConnectorTexturePatches.UnpoweredVariant;

        if (variant < 0 || variant >= assetNames.Length)
            variant = ConnectorTexturePatches.UnpoweredVariant;

        __result = Game1.content.Load<Texture2D>(assetNames[variant]);
    }
}

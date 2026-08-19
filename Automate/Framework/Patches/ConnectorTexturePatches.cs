using System;
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
/// (<see cref="PoweredFloorSync"/> writes <see cref="ConnectorVariantModDataKey"/> here instead of
/// AT's own <c>modData</c> keys).
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
/// automation group — needs an animated pulse between the two INTERMEDIATE pre-baked frames
/// (see the Pipes/*_Dimmer.png, *_Dimmest.png files) and the two static ones.
///
/// MOD: changed — that pulse used to be driven by a shared per-tick clock (the now-removed
/// <c>PoweredFloorAnimator</c>) which advanced a single animation frame and WROTE it into this same
/// <see cref="ConnectorVariantModDataKey"/> — genuinely shared, networked state. In multiplayer, every
/// client ran its own independent copy of that clock (started whenever THAT client happened to load
/// in), so host and farmhand ended up racing to write different frames to the same synced value, and
/// gating the write to host-only (a first attempt at fixing that) just meant nothing animated at all
/// wherever the host wasn't currently standing, since the host's own tick loop only advanced tiles in
/// its own current location. Neither problem exists once the SHARED state is reduced to just the fact
/// a tile is "orphaned" (see <see cref="OrphanedCategory"/>) — a coarse category that barely ever
/// changes and is safe for the host to compute alongside the other two static states — and the actual
/// per-frame pulse is computed fresh, right here, independently by every client from its own local
/// clock (<see cref="Game1.currentGameTime"/>), the same way every other purely-cosmetic pulse in this
/// codebase already works (e.g. <see cref="Patches.PowerCoilPatches"/>'s own size pulse). Nothing about
/// that pulse needs to be networked or pixel-synced between players for it to read correctly — it's
/// decorative, not state.
/// </summary>
internal static class ConnectorTexturePatches
{
    /*********
    ** Fields
    *********/
    /// <summary>
    /// The <see cref="Flooring.modData"/> key storing a managed connector tile's current SHARED/networked
    /// category — <c>"0"</c> (unpowered), <c>"1"</c> (powered and part of a valid group), or <c>"2"</c>
    /// (<see cref="OrphanedCategory"/> — powered but not part of one, animate it). Written only by the
    /// host (see <see cref="PoweredFloorSync"/>); missing entirely defaults to unpowered. This is
    /// DELIBERATELY not one of the 4 actual texture variants any more — see this class's own remarks for
    /// why baking the live animation frame into this shared value used to cause host/farmhand desync.
    /// </summary>
    internal const string ConnectorVariantModDataKey = "luisMint.PoweredAutomation/ConnectorVariant";

    /// <summary>The variant index shown for a fully unpowered connector — also the default when <see cref="ConnectorVariantModDataKey"/> is missing. Doubles as the STORED category value for this same state.</summary>
    internal const int UnpoweredVariant = 0;

    /// <summary>The variant index shown for a fully powered (and part of a valid group) connector. Doubles as the STORED category value for this same state.</summary>
    internal const int PoweredVariant = 1;

    /// <summary>
    /// MOD: added. The STORED category value meaning "powered but not part of a valid (active)
    /// automation group" — every client that sees this locally animates a pulse between
    /// <see cref="PoweredVariant"/>/<see cref="DimmerVariant"/>/<see cref="DimmestVariant"/>/
    /// <see cref="UnpoweredVariant"/> (see <see cref="GetLocalOrphanedPulseVariant"/>) rather than this
    /// value ever being used directly as a texture-array index.
    /// </summary>
    internal const int OrphanedCategory = 2;

    /// <summary>The variant (texture-array index) shown mid-pulse, closer to powered — see this class's own remarks for why this is a pre-baked opacity blend rather than a live one. Never stored in <see cref="ConnectorVariantModDataKey"/> directly — only ever computed locally by <see cref="GetLocalOrphanedPulseVariant"/>.</summary>
    internal const int DimmerVariant = 2;

    /// <summary>The variant (texture-array index) shown mid-pulse, closer to unpowered — see this class's own remarks for why this is a pre-baked opacity blend rather than a live one. Never stored in <see cref="ConnectorVariantModDataKey"/> directly — only ever computed locally by <see cref="GetLocalOrphanedPulseVariant"/>.</summary>
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


    /// <summary>MOD: added. Get the local pulse's speed, in frames per second — see <see cref="GetLocalOrphanedPulseVariant"/>. Set via <see cref="Initialize"/>.</summary>
    private static Func<double>? GetFps;

    /// <summary>MOD: added. Get how many times longer the local pulse holds on its fully-unpowered frame, relative to the other frames — see <see cref="GetLocalOrphanedPulseVariant"/>. Set via <see cref="Initialize"/>.</summary>
    private static Func<double>? GetUnpoweredHoldMultiplier;

    /// <summary>MOD: added. The pulse sequence for a tile in the <see cref="OrphanedCategory"/> state, from most to least powered and partway back — a ping-pong bounce, not a loop (see <see cref="GetLocalOrphanedPulseVariant"/>'s own remarks).</summary>
    private static readonly int[] OrphanedPulseSequence =
    [
        ConnectorTexturePatches.PoweredVariant,
        ConnectorTexturePatches.DimmerVariant,
        ConnectorTexturePatches.DimmestVariant,
        ConnectorTexturePatches.UnpoweredVariant,
        ConnectorTexturePatches.DimmestVariant,
        ConnectorTexturePatches.DimmerVariant
    ];


    /*********
    ** Public methods
    *********/
    /// <summary>MOD: added. Provide the config accessors used by the local orphaned-tile pulse. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="getFps">Get the pulse's speed, in frames per second.</param>
    /// <param name="getUnpoweredHoldMultiplier">Get how many times longer the pulse holds on its fully-unpowered frame, relative to the other frames.</param>
    public static void Initialize(Func<double> getFps, Func<double> getUnpoweredHoldMultiplier)
    {
        ConnectorTexturePatches.GetFps = getFps;
        ConnectorTexturePatches.GetUnpoweredHoldMultiplier = getUnpoweredHoldMultiplier;
    }

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
    /// <summary>Override a managed connector tile's resolved texture to match its currently-stamped category.</summary>
    /// <param name="__instance">The floor tile whose texture was just resolved.</param>
    /// <param name="__result">Vanilla's own resolved texture; overridden in place for a managed connector.</param>
    private static void GetTexture_Postfix(Flooring __instance, ref Texture2D __result)
    {
        if (!ConnectorTexturePatches.AssetNamesByFloorId.TryGetValue(__instance.whichFloor.Value, out string[]? assetNames))
            return;

        int category = __instance.modData.TryGetValue(ConnectorTexturePatches.ConnectorVariantModDataKey, out string? raw) && int.TryParse(raw, out int parsed)
            ? parsed
            : ConnectorTexturePatches.UnpoweredVariant;

        // MOD: added — the shared/networked value only ever says "orphaned," never which pulse frame to
        // show; that's computed fresh, locally, right here — see this class's own remarks for why.
        int variant = category == ConnectorTexturePatches.OrphanedCategory
            ? ConnectorTexturePatches.GetLocalOrphanedPulseVariant()
            : category;

        if (variant < 0 || variant >= assetNames.Length)
            variant = ConnectorTexturePatches.UnpoweredVariant;

        __result = Game1.content.Load<Texture2D>(assetNames[variant]);
    }

    /// <summary>
    /// MOD: added. Compute which pulse frame an <see cref="OrphanedCategory"/> tile should currently
    /// show, purely as a function of THIS client's own local game clock — no shared/networked state
    /// involved at all, so it's never in danger of racing another client's own pulse the way the old
    /// per-tick, write-the-frame-to-modData approach did. Every client computes this independently and
    /// will show a very slightly different pulse phase from every other (started whenever THAT client's
    /// own session began) — by design, since a decorative pulse doesn't need to be pixel-synced across
    /// players to read correctly, only the underlying "is this tile actually orphaned" fact does.
    /// </summary>
    private static int GetLocalOrphanedPulseVariant()
    {
        double fps = Math.Max(1, ConnectorTexturePatches.GetFps?.Invoke() ?? 6);
        double holdMultiplier = Math.Max(1, ConnectorTexturePatches.GetUnpoweredHoldMultiplier?.Invoke() ?? 2);
        double frameDuration = 1.0 / fps;

        double elapsedSeconds = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;

        double cycleDuration = 0;
        foreach (int step in ConnectorTexturePatches.OrphanedPulseSequence)
            cycleDuration += (step == ConnectorTexturePatches.UnpoweredVariant ? holdMultiplier : 1) * frameDuration;

        double positionInCycle = elapsedSeconds % cycleDuration;

        double accumulated = 0;
        foreach (int step in ConnectorTexturePatches.OrphanedPulseSequence)
        {
            accumulated += (step == ConnectorTexturePatches.UnpoweredVariant ? holdMultiplier : 1) * frameDuration;
            if (positionInCycle < accumulated)
                return step;
        }

        return ConnectorTexturePatches.OrphanedPulseSequence[^1];
    }
}

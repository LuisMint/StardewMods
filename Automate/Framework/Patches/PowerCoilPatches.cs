using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patches for the custom Power Coil craftable's visuals — a size-pulse
/// animation and a light source, neither of which could be reliably achieved through
/// Data/BigCraftables alone (see each patch's own remarks for why).
/// </summary>
internal static class PowerCoilPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The qualified item ID of the object these patches apply to.</summary>
    private const string TargetQualifiedItemId = "(BC)luisMint.AutomatePowerPipes_PowerCoil";

    /// <summary>How far the sprite grows/shrinks at the peak of the pulse, as a fraction of its normal size (e.g. 0.05 = ±5%).</summary>
    private const float PulseAmplitude = 0.05f;

    /// <summary>How fast the pulse cycles, in radians per second.</summary>
    private const float PulseSpeed = 2f;

    /// <summary>
    /// The light's radius. A radius of 10 rendered as a large dark void instead of a bigger light —
    /// the game's light renderer apparently doesn't handle an extreme radius gracefully — so this
    /// started from the same value vanilla itself uses for a lamp-type BigCraftable (3), then reduced
    /// further per testing.
    /// </summary>
    private const float LightRadius = 1f;

    /// <summary>
    /// The light's color. Pure white (255,255,255) rendered as a dark void — the game's lighting
    /// composite apparently doesn't handle a maxed-out color gracefully (possibly overflowing/
    /// inverting at the extreme, matching what happened with the radius) — so this uses a moderate
    /// hue-free gray instead: no color tint (equal R/G/B, unlike vanilla's dim-blue lamp default),
    /// but well clear of the value that broke.
    /// </summary>
    private static readonly Color LightColor = new(50, 200, 0,100);


    /*********
    ** Public methods
    *********/
    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.getScale)),
            postfix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(GetScale_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.initializeLightSource)),
            postfix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(InitializeLightSource_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Override the growth/shrink offset used when drawing the Power Coil, to produce a continuous size pulse.</summary>
    /// <param name="__instance">The object instance being drawn.</param>
    /// <param name="__result">The offset (in pre-4x-zoom pixels) to grow the sprite's drawn size by; mutated in place for the target item.</param>
    private static void GetScale_Postfix(SObject __instance, ref Vector2 __result)
    {
        if (__instance.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
            return;

        double elapsedSeconds = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
        float pulse = (float)Math.Sin(elapsedSeconds * PowerCoilPatches.PulseSpeed) * PowerCoilPatches.PulseAmplitude;

        // MOD: reverse-engineered from how Object.draw() consumes this value for a bigCraftable — it
        // multiplies the result by 4 (the game's zoom factor) and adds it directly to the drawn
        // width, but adds only HALF of the Y component to the drawn height (an old asymmetry in the
        // vanilla "wobble" effect this method was originally built for). Scaling X and Y differently
        // here compensates for that, so the sprite grows/shrinks by the same relative amount in both
        // directions instead of stretching unevenly.
        __result = new Vector2(16f * pulse, 64f * pulse);
    }

    /// <summary>
    /// Force-create a light source for the Power Coil, bypassing vanilla's own <c>IsLamp</c>-driven
    /// logic in <see cref="SObject.initializeLightSource"/>. That data flag alone didn't produce a
    /// visible light in testing — likely because whichever construction path actually places a
    /// crafted item doesn't populate <c>isLamp.Value</c> from Data/BigCraftables the same way the
    /// (Vector2, string, bool) constructor does — so this sets <see cref="SObject.lightSource"/>
    /// directly instead of relying on that flag at all.
    /// </summary>
    /// <param name="__instance">The object being initialized.</param>
    /// <param name="tileLocation">The object's tile position.</param>
    private static void InitializeLightSource_Postfix(SObject __instance, Vector2 tileLocation)
    {
        if (__instance.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
            return;

        __instance.lightSource = new LightSource(
            id: __instance.GenerateLightSourceId(tileLocation),
            textureIndex: 4,
            position: new Vector2(tileLocation.X * 64f + 32f, tileLocation.Y * 64f - 64f),
            radius: PowerCoilPatches.LightRadius,
            color: PowerCoilPatches.LightColor,
            lightContext: LightSource.LightContext.None,
            playerID: 0L,
            onlyLocation: __instance.Location?.NameOrUniqueName
        );
    }
}

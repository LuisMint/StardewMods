using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework;
using StardewValley;
using StardewValley.Audio;
using StardewValley.Extensions;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patches for the custom Power Coil craftable's visuals — a size-pulse
/// animation, a light source, and a taller-than-standard draw size — none of which could be
/// reliably achieved through Data/BigCraftables alone (see each patch's own remarks for why).
/// </summary>
internal static class PowerCoilPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The qualified item ID of the object these patches apply to.</summary>
    internal const string TargetQualifiedItemId = "(BC)luisMint.AutomatePowerPipes_PowerCoil";

    /// <summary>The asset name of the dedicated, purpose-built crafting-menu icon (loaded by the AutomatePowerPipes content pack), as opposed to the taller world sprite used everywhere else. MOD: made internal (not private) so <see cref="PowerCoilMapMarkerPatches"/> can reuse the same small icon for its world-map markers.</summary>
    internal const string CraftIconAssetName = "Mods/luisMint.AutomatePowerPipes/PowerCoilCraftIcon";

    /// <summary>MOD: added. The asset name of the unpowered variant of <see cref="CraftIconAssetName"/>, used by <see cref="PowerCoilMapMarkerPatches"/> to mark an over-capacity coil differently on the world map.</summary>
    internal const string UnpoweredCraftIconAssetName = "Mods/luisMint.AutomatePowerPipes/PowerCoilCraftIcon_UnPowered";

    /// <summary>How far the sprite grows/shrinks at the peak of the pulse, as a fraction of its normal size (e.g. 0.05 = ±5%).</summary>
    private const float PulseAmplitude = 0.05f;

    /// <summary>
    /// How fast the pulse cycles, in radians per second. Sped up from the original 2f, and
    /// deliberately a different value than <see cref="PoweredChestPatches"/>'s own pulse speed (along
    /// with <see cref="PulsePhaseOffset"/>) so the two objects' pulses don't stay in visual sync with
    /// each other.
    /// </summary>
    private const float PulseSpeed = 3f;

    /// <summary>A fixed phase offset added to the pulse, purely so it starts out of sync with <see cref="PoweredChestPatches"/>'s own pulse (which starts at phase 0) — combined with the different <see cref="PulseSpeed"/>, the two never stay aligned.</summary>
    private const float PulsePhaseOffset = MathF.PI;

    /// <summary>
    /// MOD: added. How far the horizontal (width-only) shake pulse grows/shrinks the sprite at its
    /// peak, in the same pre-4x-zoom units as <see cref="GetScale_Postfix"/>'s main pulse (the main
    /// pulse's own width term maxes out at 16 * <see cref="PulseAmplitude"/> = 0.8, so this is kept
    /// noticeably smaller to stay subtle, per request, while still being clearly a separate, faster
    /// wobble).
    /// </summary>
    private const float ShakeAmplitude = 0.35f;

    /// <summary>MOD: added. How fast the horizontal shake pulse cycles, in radians per second — deliberately much faster than <see cref="PulseSpeed"/>, so it reads as a distinct higher-frequency vibration layered on top of the slower breathing pulse (matching the vanilla Lightning Rod's own "just struck" shake, which is a similar fast wobble).</summary>
    private const float ShakeSpeed = 16f;

    /// <summary>A fixed phase offset for the shake, purely so it doesn't happen to start perfectly in sync with the main pulse.</summary>
    private const float ShakePhaseOffset = MathF.PI / 2f;

    /// <summary>MOD: added. The coil sprite's own tint while <see cref="PowerCoilCompass.ShowCompass"/> is on and this coil is powered — matches <see cref="PowerCoilCompass"/>'s own arrow tint.</summary>
    private static readonly Color PoweredCoilTint = Color.Yellow;

    /// <summary>MOD: added. The coil sprite's own tint while <see cref="PowerCoilCompass.ShowCompass"/> is on and this coil is unpowered — matches <see cref="PowerCoilCompass"/>'s own arrow tint.</summary>
    private static readonly Color UnpoweredCoilTint = Color.Red;

    /// <summary>
    /// The light's radius. A radius of 10 rendered as a large dark void instead of a bigger light —
    /// the game's light renderer apparently doesn't handle an extreme radius gracefully — so this
    /// started from the same value vanilla itself uses for a lamp-type BigCraftable (3), then reduced
    /// further per testing.
    ///
    /// MOD: made internal (not private) so <see cref="PoweredChestPatches"/> can reuse the same
    /// hand-tuned color at a different radius, instead of duplicating a value that would drift out of
    /// sync if this one's ever retuned again.
    /// </summary>
    internal const float LightRadius = 1f;

    /// <summary>
    /// The light's color.
    /// </summary>
    internal static readonly Color LightColor = new(10, 10, 10, 255);

    /// <summary>
    /// MOD: added. The asset name of the alternate texture drawn in place of the normal Power Coil
    /// sprite when it's beyond the power silo capacity (see <see cref="PowerSiloSystem"/>) — a
    /// separate sprite rather than a color tint, so it can carry its own art (e.g. no glowing coils)
    /// instead of just a darkened version of the powered one.
    /// </summary>
    private const string UnpoweredAssetName = "Mods/luisMint.AutomatePowerPipes/PowerCoil_UnPowered";


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

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.draw), [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]),
            prefix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(Draw_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.drawInMenu), [typeof(SpriteBatch), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(StackDrawType), typeof(Color), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(DrawInMenu_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.drawWhenHeld)),
            prefix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(DrawWhenHeld_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(CraftingRecipe), nameof(CraftingRecipe.drawMenuView)),
            prefix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(DrawMenuView_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(CraftingPage), "layoutRecipes"),
            postfix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(LayoutRecipes_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.placementAction), [typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer)]),
            prefix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(PlacementAction_Prefix)),
            postfix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(PlacementAction_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.performToolAction)),
            prefix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(PerformToolAction_Prefix)),
            postfix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(PerformToolAction_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SoundsHelper), nameof(SoundsHelper.PlayAll)),
            prefix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(PlaySound_Prefix))
        );

        // MOD: added — keeps an already-placed coil's actual light registration in sync with its live
        // powered/over-capacity state, not just whatever it was at construction time. See
        // UpdateWhenCurrentLocation_Postfix's own remarks for why InitializeLightSource_Postfix alone
        // isn't enough for a coil that flips state well after being placed.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.updateWhenCurrentLocation)),
            postfix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(UpdateWhenCurrentLocation_Postfix))
        );

        // MOD: added — shake + a "can't do that" cue when the player interacts with an unpowered
        // (over-capacity) coil, per direct user request. Not a blocking patch — whatever vanilla's own
        // checkForAction would otherwise do for a plain Type:Crafting BigCraftable still runs normally.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.checkForAction)),
            prefix: new HarmonyMethod(typeof(PowerCoilPatches), nameof(CheckForAction_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// MOD: added. Get whether a Power Coil is currently within the power silo capacity (see
    /// <see cref="PowerSiloSystem"/>), by reading the modData flag <see cref="PowerSiloSystem.RefreshCoilAllowance"/>
    /// stamps on it directly — an O(1) check with no coil scan, since that's only ever recomputed on an
    /// actual trigger (a coil placed/destroyed, or the total capacity changing), not every frame. A
    /// missing flag (the mechanic is disabled, or this coil hasn't been through a refresh yet) is
    /// treated as powered.
    ///
    /// MOD: made internal (not private) so <see cref="PowerCoilAmbientEffect"/> can skip its sparkle
    /// for an unpowered coil too, without duplicating this same modData check.
    /// </summary>
    /// <param name="coil">The Power Coil object instance.</param>
    internal static bool IsPowered(SObject coil)
    {
        return !coil.modData.TryGetValue(PowerSiloSystem.CoilPoweredModDataKey, out string? raw) || raw != "false";
    }

    /// <summary>Override the growth/shrink offset used when drawing the Power Coil, to produce a continuous size pulse.</summary>
    /// <param name="__instance">The object instance being drawn.</param>
    /// <param name="__result">The offset (in pre-4x-zoom pixels) to grow the sprite's drawn size by; mutated in place for the target item.</param>
    private static void GetScale_Postfix(SObject __instance, ref Vector2 __result)
    {
        if (__instance.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
            return;

        // MOD: added — an over-capacity coil (see PowerSiloSystem) sits flat/still instead of
        // pulsing, as part of reading visually as "not actually powered" alongside the alternate
        // sprite in Draw_Prefix and the suppressed light in InitializeLightSource_Postfix.
        if (!PowerCoilPatches.IsPowered(__instance))
        {
            __result = Vector2.Zero;
            return;
        }

        double elapsedSeconds = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
        float pulse = (float)Math.Sin(elapsedSeconds * PowerCoilPatches.PulseSpeed + PowerCoilPatches.PulsePhaseOffset) * PowerCoilPatches.PulseAmplitude;

        // MOD: added — a second, higher-frequency scale pulse layered on top of the main one, but
        // only affecting the WIDTH (horizontal) component, not height — a subtle, fast "vibration"
        // riding on top of the slower breathing pulse, rather than a separate position-based shake.
        float shakePulse = (float)Math.Sin(elapsedSeconds * PowerCoilPatches.ShakeSpeed + PowerCoilPatches.ShakePhaseOffset) * PowerCoilPatches.ShakeAmplitude;

        // MOD: reverse-engineered from how Object.draw() consumes this value for a bigCraftable — it
        // multiplies the result by 4 (the game's zoom factor) and adds it directly to the drawn
        // width, but adds only HALF of the Y component to the drawn height (an old asymmetry in the
        // vanilla "wobble" effect this method was originally built for). Scaling X and Y differently
        // here compensates for that, so the sprite grows/shrinks by the same relative amount in both
        // directions instead of stretching unevenly.
        __result = new Vector2(16f * pulse + shakePulse, 64f * pulse);
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

        // MOD: added — an over-capacity coil (see PowerSiloSystem) isn't actually providing power, so
        // it shouldn't light up either — matches the suppressed pulse (GetScale_Postfix) and alternate
        // sprite (Draw_Prefix) used for the same state.
        if (!PowerCoilPatches.IsPowered(__instance))
        {
            __instance.lightSource = null;
            return;
        }

        // MOD: positioned at the sprite's base (the ground tile it's actually placed on) rather than
        // vanilla's "middle of a standard 2-tile sprite" anchor (tileLocation.Y*64-64) — that anchor
        // stayed fixed even after the Power Coil's draw height grew to 3 tiles (see Draw_Prefix's
        // anchor-preserving math, which keeps the sprite's bottom edge at tileLocation.Y*64+64
        // regardless of texture height), so the light ended up floating near the sprite's middle
        // instead of where the object actually sits.
        __instance.lightSource = new LightSource(
            id: __instance.GenerateLightSourceId(tileLocation),
            textureIndex: 4,
            position: new Vector2(tileLocation.X * 64f + 32f, tileLocation.Y * 64f + 64f),
            radius: PowerCoilPatches.LightRadius,
            color: PowerCoilPatches.LightColor,
            lightContext: LightSource.LightContext.None,
            playerID: 0L,
            onlyLocation: __instance.Location?.NameOrUniqueName
        );
    }

    /// <summary>
    /// MOD: added. Keep an already-placed coil's light registration in sync with its live powered
    /// state every tick, not just whatever it was at construction time. <see cref="InitializeLightSource_Postfix"/>
    /// alone only runs when <c>initializeLightSource</c> is actually called (placement, or a location
    /// reload) — it doesn't re-fire just because <see cref="PowerSiloSystem.RefreshCoilAllowance"/>
    /// later flips this SAME coil's powered flag (e.g. more coils get built elsewhere and push this
    /// one over capacity, or a Silo tier-up frees up room for it again) — so without this, a coil's
    /// light would keep shining (or stay dark) exactly as it was when first placed, regardless of
    /// whatever its sprite/pulse is currently showing. Turns a light back on by re-running
    /// <see cref="InitializeLightSource_Postfix"/> (via calling <c>initializeLightSource</c> again)
    /// rather than duplicating its construction logic here.
    /// </summary>
    /// <param name="__instance">The object being updated.</param>
    private static void UpdateWhenCurrentLocation_Postfix(SObject __instance)
    {
        if (__instance.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
            return;

        GameLocation? location = __instance.Location;
        if (location == null)
            return;

        if (!PowerCoilPatches.IsPowered(__instance))
        {
            if (__instance.lightSource != null)
            {
                location.removeLightSource(__instance.lightSource.Id);
                __instance.lightSource = null;
            }
            return;
        }

        if (__instance.lightSource == null)
            __instance.initializeLightSource(__instance.TileLocation);

        if (__instance.lightSource != null && !location.hasLightSource(__instance.lightSource.Id))
            location.sharedLights.AddLight(__instance.lightSource.Clone());
    }

    /// <summary>
    /// Fully replace the Power Coil's world draw call, so it can be taller than the standard 2-tile
    /// BigCraftable box. Vanilla's own <c>Object.getSourceRectForBigCraftable</c> hardcodes a 32px
    /// (2-tile) source height regardless of the actual texture's size — even for the default,
    /// non-animated sprite index — so simply drawing into a taller destination rectangle (e.g. via
    /// <see cref="GetScale_Postfix"/> alone) would just stretch that same 32px crop rather than
    /// reveal any extra art below it. Reading the texture's own height directly here instead means
    /// the drawn size always matches however tall the source PNG actually is — no code changes
    /// needed if that height changes later.
    /// </summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    /// <param name="alpha">The transparency at which to draw the sprite.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool Draw_Prefix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (__instance.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
            return true;

        // MOD: added — draw the dedicated "unpowered" sprite in place of the normal one for an
        // over-capacity coil (see PowerSiloSystem), rather than tinting the normal sprite — lets that
        // texture carry its own art (e.g. no glow/lit cabling) instead of just a darkened copy.
        bool isPowered = PowerCoilPatches.IsPowered(__instance);
        Texture2D texture = isPowered
            ? ItemRegistry.GetDataOrErrorItem(__instance.QualifiedItemId).GetTexture()
            : Game1.content.Load<Texture2D>(PowerCoilPatches.UnpoweredAssetName);
        Rectangle sourceRect = new(0, 0, texture.Width, texture.Height);

        // MOD: same anchor math vanilla uses for a standard BigCraftable (see Object.draw), extended
        // to account for a taller-than-standard texture. Vanilla's own formula only shifts the TOP
        // up by half the pulse's growth while adding the FULL pulse growth to the height — for a
        // small pulse, that nets out to a fixed bottom edge (top moves up by X, height grows by X,
        // so top+height is unchanged). But that only works because vanilla's baseline height (128)
        // matches its baseline top offset (64, i.e. "one tile up"); once the baseline height itself
        // changes (a taller texture), the top must ALSO shift up by that same extra amount, or the
        // whole box (including its bottom) drifts downward by the difference — which is exactly what
        // happened before this fix (a 48px-tall texture, 64px taller than the vanilla 32px baseline,
        // pushed the bottom down a full tile).
        Vector2 scale = __instance.getScale() * 4f;
        float extraBaseHeight = (texture.Height * 4f) - 128f;

        // MOD: added — same shakeTimer-driven horizontal jitter PowerRequiredMachinePatches/PoweredChestPatches
        // already use elsewhere in this codebase, so setting shakeTimer (see CheckForAction_Prefix) actually
        // reads as a visible "can't do that" wobble instead of doing nothing.
        int shakeJitter = __instance.shakeTimer > 0 ? Game1.random.Next(-1, 2) : 0;

        Vector2 topAnchor = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 + shakeJitter, y * 64 - 64));
        Rectangle destination = new(
            (int)(topAnchor.X - scale.X / 2f),
            (int)(topAnchor.Y - scale.Y / 2f - extraBaseHeight),
            (int)(64f + scale.X),
            (int)(texture.Height * 4f + scale.Y / 2f)
        );

        // MOD: added — a yellow/red tint on the coil itself while the compass is toggled on (see
        // PowerCoilCompass), matching its own arrow tint, per direct user request — makes a coil easy to
        // spot at a glance even without following its arrow.
        Color coilTint = PowerCoilCompass.ShowCompass
            ? (isPowered ? PowerCoilPatches.PoweredCoilTint : PowerCoilPatches.UnpoweredCoilTint)
            : Color.White;

        float layerDepth = Math.Max(0f, (float)((y + 1) * 64 - 24) / 10000f) + x * 1E-05f;
        spriteBatch.Draw(texture, destination, sourceRect, coilTint * alpha, 0f, Vector2.Zero, SpriteEffects.None, layerDepth);

        // MOD: replicate vanilla's lamp glow decal, since we're skipping its own draw method entirely
        // — shifted up by the same extra height so it still sits near the sprite's actual top. Skipped
        // entirely for an over-capacity coil, alongside its actual light source (see
        // InitializeLightSource_Postfix) — it isn't really lit, so it shouldn't glow either.
        if (isPowered && __instance.isLamp.Value && Game1.isDarkOut(__instance.Location))
        {
            spriteBatch.Draw(Game1.mouseCursors, topAnchor + new Vector2(-32f, -32f - extraBaseHeight), new Rectangle(88, 1779, 32, 32), Color.White * 0.75f, 0f, Vector2.Zero, 4f, SpriteEffects.None, Math.Max(0f, (float)((y + 1) * 64 - 20) / 10000f) + x / 1000000f);
        }

        return false;
    }

    /// <summary>
    /// Fully replace the Power Coil's inventory/shop/crafting-menu icon draw, for the same reason as
    /// <see cref="Draw_Prefix"/> — vanilla's hardcoded 32px source height would otherwise crop the
    /// icon to the top two-thirds of the texture, cutting off the bottom.
    /// </summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="location">The top-left pixel position to draw at.</param>
    /// <param name="scaleSize">The size multiplier to draw at.</param>
    /// <param name="transparency">The transparency at which to draw the sprite.</param>
    /// <param name="layerDepth">The layer depth to draw at.</param>
    /// <param name="drawStackNumber">Whether to draw the stack number, if applicable — ignored here, since a Power Coil is never expected to stack.</param>
    /// <param name="color">The color to tint the sprite.</param>
    /// <param name="drawShadow">Whether to also draw a drop shadow — ignored here, since vanilla only draws one for non-BigCraftable items, and a Power Coil is always a BigCraftable.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool DrawInMenu_Prefix(SObject __instance, SpriteBatch spriteBatch, Vector2 location, float scaleSize, float transparency, float layerDepth, StackDrawType drawStackNumber, Color color, bool drawShadow)
    {
        if (__instance.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
            return true;

        ParsedItemData data = ItemRegistry.GetDataOrErrorItem(__instance.QualifiedItemId);
        Texture2D texture = data.GetTexture();
        Rectangle sourceRect = new(0, 0, texture.Width, texture.Height);

        // MOD: mirrors vanilla's own halving of the scale for BigCraftable icons (their source rects
        // are normally twice as tall as a regular item's, so this keeps icon sizes roughly
        // consistent across item types), PLUS an extra factor normalizing for our texture's actual
        // height — without it, the drawn icon would be taller than a normal BigCraftable's in
        // proportion to how much taller our texture is than the vanilla 32px baseline, and overflow
        // the inventory slot. This keeps the drawn footprint the same size as a normal 16x32 icon
        // regardless of how tall the underlying texture actually is.
        float scale = (scaleSize > 0.2f ? scaleSize / 2f : scaleSize) * (32f / texture.Height);

        spriteBatch.Draw(texture, location + new Vector2(32f, 32f), sourceRect, color * transparency, 0f, new Vector2(sourceRect.Width / 2f, sourceRect.Height / 2f), 4f * scale, SpriteEffects.None, layerDepth);

        // MOD: vanilla's own drawInMenu also draws the stack-count/quality badge via this same call —
        // omitted in an earlier version of this patch, which silently dropped the stack number.
        __instance.DrawMenuIcons(spriteBatch, location, scaleSize, transparency, layerDepth, drawStackNumber, color);

        return false;
    }

    /// <summary>Fully replace the Power Coil's "carrying it before placement" draw, for the same reason as <see cref="Draw_Prefix"/>.</summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="objectPosition">The top-left pixel position to draw at.</param>
    /// <param name="f">The farmer holding the item — unused here, since we don't need their standing position for the layer depth (a fixed reasonable value works fine for something this short-lived).</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool DrawWhenHeld_Prefix(SObject __instance, SpriteBatch spriteBatch, Vector2 objectPosition, Farmer f)
    {
        if (__instance.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
            return true;

        ParsedItemData data = ItemRegistry.GetDataOrErrorItem(__instance.QualifiedItemId);
        Texture2D texture = data.GetTexture();
        Rectangle sourceRect = new(0, 0, texture.Width, texture.Height);

        // MOD: shifted up by one tile (64px) from vanilla's plain `objectPosition` anchor — vanilla's
        // anchor assumes a normal 32px-tall sprite drawn from the farmer's hand; with our taller
        // texture drawn from that same point, the extra height hangs entirely below it, making it
        // look like the farmer is gripping the middle of the object instead of near its top.
        Vector2 drawPosition = objectPosition - new Vector2(0f, 64f);

        float layerDepth = Math.Max(0f, (float)(f.StandingPixel.Y + 3) / 10000f);
        spriteBatch.Draw(texture, drawPosition, sourceRect, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, layerDepth);

        return false;
    }

    /// <summary>
    /// Fully replace the Power Coil's crafting-menu recipe icon draw. This is a separate rendering
    /// path from <see cref="DrawInMenu_Prefix"/> entirely — <c>CraftingRecipe.drawMenuView</c> reads
    /// the recipe's output item data directly rather than going through <c>Object.drawInMenu</c> —
    /// so it needed its own patch even though the underlying problem (vanilla's hardcoded 32px source
    /// height) is the same.
    /// </summary>
    /// <param name="__instance">The crafting recipe being drawn.</param>
    /// <param name="b">The sprite batch being drawn to.</param>
    /// <param name="x">The top-left X pixel position to draw at.</param>
    /// <param name="y">The top-left Y pixel position to draw at.</param>
    /// <param name="layerDepth">The layer depth to draw at.</param>
    /// <param name="shadow">Whether to draw a drop shadow — unused here, matching vanilla's own <c>drawMenuView</c>, which also never actually reads this parameter despite accepting it.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this recipe, or <c>true</c> to let it run normally for every other recipe.</returns>
    private static bool DrawMenuView_Prefix(CraftingRecipe __instance, SpriteBatch b, int x, int y, float layerDepth, bool shadow)
    {
        ParsedItemData itemData = __instance.GetItemData(useFirst: true);
        if (itemData?.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
            return true;

        Texture2D texture = itemData.GetTexture();
        Rectangle sourceRect = new(0, 0, texture.Width, texture.Height);

        // MOD: normalizes the scale so the drawn icon occupies the same footprint as a normal 16x32
        // recipe icon (vanilla always draws at a flat 4x scale here), regardless of how much taller
        // our texture actually is — so it doesn't crowd out the recipe icons around it in the grid.
        float scale = 4f * (32f / texture.Height);

        Utility.drawWithShadow(b, texture, new Vector2(x, y), sourceRect, Color.White, 0f, Vector2.Zero, scale, flipped: false, layerDepth);

        return false;
    }

    /// <summary>
    /// Fix up the Power Coil's icon in the crafting menu's recipe grid. This turned out to be the
    /// actual cause of the crafting-menu cropping — <see cref="DrawMenuView_Prefix"/> alone didn't
    /// fix it because the crafting grid doesn't call <c>CraftingRecipe.drawMenuView</c> at all.
    /// Instead, <c>CraftingPage.layoutRecipes</c> builds one <c>ClickableTextureComponent</c> per
    /// recipe ONCE (whenever the page is laid out, not every frame), baking in a texture/source
    /// rect/scale from the recipe's item data at that point — using the same hardcoded 32px source
    /// height as everywhere else. Those components are then drawn generically via
    /// <c>ClickableTextureComponent.draw</c>, which is shared by countless other UI elements, so
    /// patching that directly would be far too broad; overriding the specific component's fields
    /// right after they're built is much more scoped.
    ///
    /// Uses a dedicated, purpose-built 16x32 icon texture rather than reusing (and scaling down) the
    /// taller world sprite — simpler and cleaner than the height-normalizing math the other icon
    /// patches need, since this texture is already the standard BigCraftable icon size.
    /// </summary>
    /// <param name="__instance">The crafting page whose recipes were just laid out.</param>
    private static void LayoutRecipes_Postfix(CraftingPage __instance)
    {
        foreach (Dictionary<ClickableTextureComponent, CraftingRecipe> page in __instance.pagesOfCraftingRecipes)
        {
            foreach ((ClickableTextureComponent component, CraftingRecipe recipe) in page)
            {
                ParsedItemData itemData = recipe.GetItemData(useFirst: true);
                if (itemData?.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
                    continue;

                Texture2D texture = Game1.content.Load<Texture2D>(PowerCoilPatches.CraftIconAssetName);
                component.texture = texture;
                component.sourceRect = new Rectangle(0, 0, texture.Width, texture.Height);
                component.baseScale = 4f; // MOD: standard scale — this texture is already a normal 16x32 icon, no normalizing needed
            }
        }
    }

    /// <summary>
    /// MOD: added. Flag that <see cref="SObject.placementAction"/> is currently placing a Power Coil,
    /// so <see cref="PlaySound_Prefix"/> knows to redirect vanilla's own generic BigCraftable
    /// placement sound ("woodyStep") to the same sound used for placing a normal chest ("axe")
    /// instead — see that method's remarks for why a direct sound swap needs this indirection rather
    /// than just overriding the sound after the fact.
    /// </summary>
    private static bool IsPlacingPowerCoil;

    /// <summary>
    /// MOD: added. Flag that <see cref="SObject.performToolAction"/> is currently breaking a Power
    /// Coil, so <see cref="PlaySound_Prefix"/> knows to redirect vanilla's own generic BigCraftable
    /// "broken by a tool" sound ("hammer") to "axe" instead, for the same reason as
    /// <see cref="IsPlacingPowerCoil"/>.
    /// </summary>
    private static bool IsBreakingPowerCoil;

    /// <summary>
    /// MOD: added. Block placing a Power Coil in a non-permanent location (a Mine/Skull Cavern level, or
    /// the Volcano Dungeon) — per direct user request, since either regenerates/resets its layout,
    /// permanently altering the power grid's connections with no way for the player to ever remove the
    /// coil again. Mirrors <see cref="PoweredChestPatches.PlacementAction_Prefix"/>'s own identical
    /// check/message for the same reason.
    /// </summary>
    /// <param name="__instance">The item being placed.</param>
    /// <param name="location">The location it's being placed in.</param>
    /// <param name="__result">The value the original method would have returned.</param>
    /// <returns>Returns <c>false</c> to skip the original method (blocking placement), or <c>true</c> to let it run normally.</returns>
    private static bool PlacementAction_Prefix(SObject __instance, GameLocation location, ref bool __result)
    {
        bool isPowerCoil = __instance.QualifiedItemId == PowerCoilPatches.TargetQualifiedItemId;
        PowerCoilPatches.IsPlacingPowerCoil = isPowerCoil;

        if (isPowerCoil && (location is MineShaft || location is VolcanoDungeon))
        {
            Game1.showRedMessage(Game1.content.LoadString("Strings\\StringsFromCSFiles:Object.cs.13053"));
            __result = false;
            return false;
        }

        return true;
    }

    /// <summary>Clear the <see cref="IsPlacingPowerCoil"/> flag after vanilla's own placement logic runs.</summary>
    /// <param name="__instance">The item being placed.</param>
    private static void PlacementAction_Postfix(SObject __instance)
    {
        // MOD: changed — the additional placement sound (and now also a HUD message) moved to
        // Patches.PowerSiloPatches.PlacementAction_Postfix, since which sound to play depends on
        // whether the coil ends up powered — a power silo capacity concern, not a visual one — and
        // that class already computes the fresh post-placement state right here anyway.
        PowerCoilPatches.IsPlacingPowerCoil = false;
    }

    /// <summary>Set the <see cref="IsBreakingPowerCoil"/> flag before vanilla's own tool-hit logic runs.</summary>
    /// <param name="__instance">The item being hit by a tool.</param>
    private static void PerformToolAction_Prefix(SObject __instance)
    {
        PowerCoilPatches.IsBreakingPowerCoil = __instance.QualifiedItemId == PowerCoilPatches.TargetQualifiedItemId;
    }

    /// <summary>Clear the <see cref="IsBreakingPowerCoil"/> flag after vanilla's own tool-hit logic runs.</summary>
    private static void PerformToolAction_Postfix()
    {
        PowerCoilPatches.IsBreakingPowerCoil = false;
    }

    /// <summary>
    /// MOD: added. Shake and play a "can't do that" cue when the player interacts with an unpowered
    /// (over-capacity) coil, per direct user request — the same reaction vanilla objects use to
    /// signal a rejected interaction. Reuses the shake magnitude <see cref="PoweredChestPatches"/>
    /// already sets for its own one-time placement shake, and <see cref="MachineManager"/>'s own
    /// "cancel" cue for a broken/invalid automation state. Not a blocking patch — whatever vanilla's
    /// own <c>checkForAction</c> would otherwise do for a plain Type:Crafting BigCraftable (normally
    /// nothing) still runs afterward.
    /// </summary>
    /// <param name="__instance">The object being interacted with.</param>
    /// <param name="justCheckingForActivity">Whether this is just a capability check (e.g. for cursor icon) rather than a real interaction.</param>
    private static void CheckForAction_Prefix(SObject __instance, bool justCheckingForActivity)
    {
        if (justCheckingForActivity || __instance.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId || PowerCoilPatches.IsPowered(__instance))
            return;

        __instance.shakeTimer = 50;
        __instance.Location?.playSound("cancel");
    }

    /// <summary>
    /// MOD: added. Redirect vanilla's own generic BigCraftable placement/breaking sounds to the one
    /// used for a normal chest ("axe"), while a Power Coil is being placed or broken (see
    /// <see cref="IsPlacingPowerCoil"/>/<see cref="IsBreakingPowerCoil"/>). Both "woodyStep" (placing)
    /// and "hammer" (breaking) are hardcoded deep inside their respective vanilla methods' generic
    /// fallback branches (the ones a plain "Type: Crafting" BigCraftable like the Power Coil falls
    /// into, since it isn't one of vanilla's own special-cased item IDs) — there's no clean way to
    /// override just those calls without reimplementing the whole surrounding methods, so this
    /// intercepts the sound itself instead, at the one shared choke point both paths funnel through
    /// (<see cref="GameLocation.playSound"/> and <see cref="SObject.playNearbySoundAll"/> both call
    /// this same method internally), scoped narrowly to the brief windows the two flags above are set
    /// for.
    /// </summary>
    /// <param name="cueName">The cue name to play — reassigning this parameter changes what vanilla's own method actually plays, since Harmony treats a prefix parameter with the same name as the original as a by-reference override.</param>
    private static void PlaySound_Prefix(ref string cueName)
    {
        if (PowerCoilPatches.IsPlacingPowerCoil && cueName == "woodyStep")
            cueName = "axe";
        else if (PowerCoilPatches.IsBreakingPowerCoil && cueName == "hammer")
            cueName = "axe";
    }
}

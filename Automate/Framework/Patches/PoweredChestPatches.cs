using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework.Machines.Objects;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patches for the custom Powered Chest: placing it as a real <see cref="Chest"/>
/// instance instead of a plain <see cref="SObject"/>, a two-axis "breathing" pulse animation, and a
/// light source using the same hand-tuned color as the Power Coil (see <see cref="PowerCoilPatches"/>)
/// at a much smaller radius.
///
/// Unlike whitelist/blacklist signs (which hook into a generic <c>sign_item</c> context tag), vanilla
/// has no data-driven way to make a custom BigCraftable become a Chest — <see cref="SObject.placementAction"/>'s
/// own decompiled source hardcodes specific vanilla BigCraftable IDs (130, 248, etc.) to construct a
/// <c>new Chest(playerChest: true, vector, base.ItemId)</c> — so this replicates that exact pattern
/// for our own item's ID instead. Once placed as a real Chest, save/load, the color-picker UI, and
/// inventory capacity all work automatically as standard vanilla Chest behavior.
///
/// The pulse and light needed their own patches rather than reusing <see cref="PowerCoilPatches"/>'s:
/// <see cref="Chest"/> overrides <see cref="SObject.draw(SpriteBatch, int, int, float)"/> with its own
/// fully custom rendering (it never calls <see cref="SObject.getScale"/> at all, unlike the base
/// <see cref="SObject.draw"/> Power Coil's patch relies on). And even once a light source is properly
/// assigned, <see cref="Chest"/> ALSO overrides <see cref="SObject.updateWhenCurrentLocation"/> without
/// ever calling the base implementation — which is the ONLY place a placed object's light actually
/// gets registered into <c>GameLocation.sharedLights</c> (see <c>Object.updateWhenCurrentLocation</c>'s
/// own decompiled source) — so no chest's light source, however it's set, would ever actually render
/// without a postfix replicating that missing registration step here.
/// </summary>
internal static class PoweredChestPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>How far the sprite grows/shrinks at the peak of the pulse, as a fraction of its normal size — matches <see cref="PowerCoilPatches"/>'s own pulse for visual consistency between the two objects.</summary>
    private const float PulseAmplitude = 0.05f;

    /// <summary>How fast the pulse cycles, in radians per second. Faster than <see cref="PowerCoilPatches"/>'s own pulse, per request.</summary>
    private const float PulseSpeed = 3.5f;

    /// <summary>The light's radius — significantly smaller than <see cref="PowerCoilPatches.LightRadius"/>, per request (confirmed visible during testing at the vanilla lamp baseline of 3, then reduced to this much smaller value, then increased 1.5x per a later request).</summary>
    private const float LightRadius = 0.3f * 1.5f;

    /// <summary>Reflected access to <see cref="Chest"/>'s private <c>currentLidFrame</c> field, needed to replicate its lid-open overlay draw call (there's no public equivalent — <c>getLastLidFrame()</c> returns a different, static value, not the live animated frame).</summary>
    private static readonly FieldInfo CurrentLidFrameField = AccessTools.Field(typeof(Chest), "currentLidFrame");


    /*********
    ** Public methods
    *********/
    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.placementAction), [typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer)]),
            prefix: new HarmonyMethod(typeof(PoweredChestPatches), nameof(PlacementAction_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.initializeLightSource)),
            postfix: new HarmonyMethod(typeof(PoweredChestPatches), nameof(InitializeLightSource_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(Chest), nameof(Chest.draw), [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]),
            prefix: new HarmonyMethod(typeof(PoweredChestPatches), nameof(Draw_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(Chest), nameof(Chest.updateWhenCurrentLocation)),
            postfix: new HarmonyMethod(typeof(PoweredChestPatches), nameof(UpdateWhenCurrentLocation_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Place the Powered Chest as a real <see cref="Chest"/> instead of a plain object, mirroring vanilla's own hardcoded chest-placement branches.</summary>
    /// <param name="__instance">The item being placed.</param>
    /// <param name="location">The location in which to place it.</param>
    /// <param name="x">The X tile position (in pixels) at which to place it.</param>
    /// <param name="y">The Y tile position (in pixels) at which to place it.</param>
    /// <param name="who">The player placing the object, if applicable.</param>
    /// <param name="__result">The value the original method would have returned.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool PlacementAction_Prefix(SObject __instance, GameLocation location, int x, int y, Farmer who, ref bool __result)
    {
        if (__instance.QualifiedItemId != PoweredChestMachine.QualifiedItemId)
            return true;

        Vector2 tile = new(x / 64, y / 64);

        if (location.objects.ContainsKey(tile) || location is MineShaft || location is VolcanoDungeon)
        {
            Game1.showRedMessage(Game1.content.LoadString("Strings\\StringsFromCSFiles:Object.cs.13053"));
            __result = false;
            return false;
        }

        Chest chest = new(playerChest: true, tile, __instance.ItemId)
        {
            name = __instance.name,
            shakeTimer = 50
        };
        location.objects.Add(tile, chest);
        location.playSound("axe");
        location.playSound("grunt");

        // MOD: the constructor overload used above doesn't chain through the one base Object
        // constructor that calls initializeLightSource, so it needs an explicit initial call here —
        // the InitializeLightSource_Postfix below then keeps it re-applied on later calls (e.g. when
        // the location is re-entered after a save reload).
        chest.initializeLightSource(tile);

        __result = true;
        return false;
    }

    /// <summary>Force-create a light source for the Powered Chest, reusing <see cref="PowerCoilPatches"/>'s own hand-tuned color at half its radius.</summary>
    /// <param name="__instance">The object being initialized.</param>
    /// <param name="tileLocation">The object's tile position.</param>
    private static void InitializeLightSource_Postfix(SObject __instance, Vector2 tileLocation)
    {
        if (__instance.QualifiedItemId != PoweredChestMachine.QualifiedItemId)
            return;

        __instance.lightSource = new LightSource(
            id: __instance.GenerateLightSourceId(tileLocation),
            textureIndex: 4,
            position: new Vector2(tileLocation.X * 64f + 32f, tileLocation.Y * 64f + 32f), // MOD: shifted up half a tile (32px) from the sprite's base, per request
            radius: PoweredChestPatches.LightRadius,
            color: PowerCoilPatches.LightColor,
            lightContext: LightSource.LightContext.None,
            playerID: 0L,
            onlyLocation: __instance.Location?.NameOrUniqueName
        );
    }

    /// <summary>
    /// Replace the Powered Chest's world draw call with a version that applies a two-axis "breathing"
    /// pulse: height and width pulse a quarter-cycle out of phase with each other (width leads height
    /// by 90°), so the sprite looks fatter while it's growing taller and skinnier while it's shrinking
    /// back down, instead of just uniformly zooming in and out. Replicates the relevant branch of
    /// <see cref="Chest.draw"/> (the generic <c>playerChest.Value</c> case, since our item never
    /// matches vanilla's own special-cased chest IDs) rather than patching the base
    /// <see cref="SObject.draw"/> — <see cref="Chest"/> overrides <see cref="SObject.draw"/> entirely
    /// and never calls <see cref="SObject.getScale"/>, so patching the base method (as
    /// <see cref="PowerCoilPatches"/> does) wouldn't have any effect here.
    /// </summary>
    /// <param name="__instance">The chest being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    /// <param name="alpha">The transparency at which to draw the sprite.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool Draw_Prefix(Chest __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (__instance.QualifiedItemId != PoweredChestMachine.QualifiedItemId)
            return true;

        ParsedItemData data = ItemRegistry.GetDataOrErrorItem(__instance.QualifiedItemId);
        Texture2D texture = data.GetTexture();
        Rectangle sourceRect = data.GetSourceRect();

        double phase = (Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0) * PoweredChestPatches.PulseSpeed;
        float pulseY = (float)Math.Sin(phase) * PoweredChestPatches.PulseAmplitude;
        float pulseX = (float)Math.Cos(phase) * PoweredChestPatches.PulseAmplitude; // 90 degrees ahead of the vertical pulse
        Vector2 scale = new(4f * (1f + pulseX), 4f * (1f + pulseY));

        int currentLidFrame = PoweredChestPatches.CurrentLidFrameField.GetValue(__instance) is int frame ? frame : 0;
        int shakeOffset = __instance.shakeTimer > 0 ? Game1.random.Next(-1, 2) : 0;

        // MOD: anchored at the sprite's bottom-center (horizontally centered, vertically at the
        // bottom) rather than its top-left or true center — so it grows/shrinks upward from a fixed
        // base, like it's breathing in place on the ground, instead of the base itself drifting up
        // and down or the whole thing growing from one corner.
        Vector2 origin = new(sourceRect.Width / 2f, sourceRect.Height);
        Vector2 topLeft = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f + shakeOffset, (y - 1f) * 64f));
        Vector2 anchorPoint = topLeft + origin * 4f;
        float depth = Math.Max(0f, ((y + 1) * 64f - 24f) / 10000f) + x * 1E-05f;

        spriteBatch.Draw(texture, anchorPoint, sourceRect, __instance.Tint * alpha, 0f, origin, scale, SpriteEffects.None, depth);
        spriteBatch.Draw(texture, anchorPoint, data.GetSourceRect(0, currentLidFrame), __instance.Tint * alpha * alpha, 0f, origin, scale, SpriteEffects.None, depth + 1E-05f);

        return false;
    }

    /// <summary>
    /// Register the Powered Chest's light source with the location, since <see cref="Chest.updateWhenCurrentLocation"/>
    /// completely overrides <see cref="SObject.updateWhenCurrentLocation"/> without ever calling the
    /// base implementation — the ONLY place that normally adds a placed object's light source to
    /// <c>GameLocation.sharedLights</c> (see this class's own remarks for the decompiled evidence).
    /// Without this, no chest's light source could ever actually render, however it's assigned.
    /// </summary>
    /// <param name="__instance">The chest being updated.</param>
    private static void UpdateWhenCurrentLocation_Postfix(Chest __instance)
    {
        if (__instance.QualifiedItemId != PoweredChestMachine.QualifiedItemId)
            return;

        GameLocation? location = __instance.Location;
        LightSource? lightSource = __instance.lightSource;

        if (location != null && lightSource != null && __instance.IsOn && !location.hasLightSource(lightSource.Id))
            location.sharedLights.AddLight(lightSource.Clone());
    }
}

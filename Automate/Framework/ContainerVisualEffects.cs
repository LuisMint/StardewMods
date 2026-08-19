using System;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Plays a lid-open animation, a flying item-icon sprite, and a
/// matching sound whenever an item enters or leaves a container through automation — so
/// a farmer can always see, at the container itself, what's moving in or out of it. Never draws
/// anything on a machine's own tile; the effect always renders at the CONTAINER end of a transfer
/// (a machine consuming from, or depositing
/// into, a container still shows the effect on the container, just never on the machine itself).
///
/// The lid animation (see <see cref="TriggerLidAnimation"/>) is written as a start timestamp in the
/// chest's own <see cref="Chest.modData"/>, purely time-computed and re-asserted fresh every draw call
/// by <see cref="Patches.ChestLidAnimationPatches"/> — see that class's own remarks for why this
/// (rather than a per-tick incremental mutation, as an earlier reverted shipping-bin lid attempt did)
/// avoids fighting vanilla's own per-tick lid-close logic.
///
/// MOD: added — this effect only runs in areas loaded by the players: <see cref="PlayEffect"/>
/// is a no-op for any location no player is currently standing in, since automation itself runs
/// location-agnostically in the background but nobody could see or hear this effect there anyway.
/// </summary>
internal static class ContainerVisualEffects
{
    /*********
    ** Fields
    *********/
    /// <summary>MOD: changed — containers now stay open longer, for clearer visual feedback; how long a chest's lid stays visually open, in milliseconds — see <see cref="TriggerLidAnimation"/> and <see cref="Patches.ChestLidAnimationPatches"/>.</summary>
    public const int LidAnimationDurationMs = 1200;

    /// <summary>MOD: added. The mod data key storing when a chest's lid animation started (see <see cref="TriggerLidAnimation"/>).</summary>
    public const string LidAnimStartTimeModDataKey = "Pathoschild.Automate/ChestLidAnim/StartTime";

    /// <summary>MOD: added. The mod data key storing how long a chest's lid animation lasts (see <see cref="TriggerLidAnimation"/>).</summary>
    public const string LidAnimDurationModDataKey = "Pathoschild.Automate/ChestLidAnim/DurationMs";


    /*********
    ** Public methods
    *********/
    /// <summary>Play the "an item just arrived" effect for a container — a lid pop, an item sprite dropping in, and a sound.</summary>
    /// <param name="container">The container an item was just stored into.</param>
    /// <param name="sample">A sample of the item that arrived.</param>
    /// <param name="slot">MOD: added. This item type's 0-based stacking slot among others animating at the same container in the same window (see <see cref="ThrottledContainer.AnimatedItemTypesThisWindow"/>) — offsets where the sprite is drawn so several different item types don't all draw on top of each other.</param>
    public static void PlayEntryEffect(IContainer container, Item sample, int slot)
    {
        ContainerVisualEffects.PlayEffect(container, sample, isEntry: true, slot);
    }

    /// <summary>Play the "an item just left" effect for a container — a lid pop, an item sprite floating out, and a sound.</summary>
    /// <param name="container">The container an item was just removed from.</param>
    /// <param name="sample">A sample of the item that left.</param>
    /// <param name="slot">MOD: added. This item type's 0-based stacking slot — see <see cref="PlayEntryEffect"/>'s own remarks.</param>
    public static void PlayExitEffect(IContainer container, Item sample, int slot)
    {
        ContainerVisualEffects.PlayEffect(container, sample, isEntry: false, slot);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Play the shared entry/exit effect for a container.</summary>
    /// <param name="container">The container an item just entered or left.</param>
    /// <param name="sample">A sample of the item that moved.</param>
    /// <param name="isEntry">Whether this is the "arriving" effect (vs. the mirrored "leaving" one).</param>
    /// <param name="slot">This item type's 0-based stacking slot — see <see cref="PlayEntryEffect"/>'s own remarks.</param>
    private static void PlayEffect(IContainer container, Item sample, bool isEntry, int slot)
    {
        GameLocation location = container.Location;

        // MOD: added — this effect only runs in areas loaded by the players: a machine group
        // automates regardless of which location any player is actually in, but nobody can see (or
        // hear) this effect in a location nobody's standing in, so skip all of the work below entirely
        // — building/broadcasting a sprite, playing a sound, writing lid modData — for one that isn't
        // currently loaded for anyone. location.farmers lists every local split-screen AND remote
        // multiplayer farmer currently in this specific location, so this covers both cases.
        if (!location.farmers.Any())
            return;

        Vector2 tile = new(container.TileArea.X, container.TileArea.Y);

        // MOD: changed — the jolt (chest.shakeTimer) is removed entirely; the lid
        // animation alone is enough, and vanilla's own draw code applies that jolt as a real ±1px
        // horizontal jitter to the CONTAINER's own sprite every frame it's active — a likely contributor
        // to the "diagonal" look reported for the item's flight path, even though the item sprite's own
        // motion was (and still is) strictly vertical the whole time.
        if (container.GetUnderlyingChest() is { } chest)
            ContainerVisualEffects.TriggerLidAnimation(chest);

        // MOD: added — the shipping bin is a larger container, bigger than 1 tile, and there could be
        // larger containers from other mods too, so this needs to account for that — TileArea.Width is
        // in TILES (e.g. 2 for the shipping bin's real footprint), so its horizontal center is that many
        // tiles wide, not a flat half-tile assumption that would only be correct for a 1x1 container.
        float containerWidthPixels = container.TileArea.Width * 64f;
        ContainerVisualEffects.SpawnItemSprite(location, tile * 64f, containerWidthPixels, sample, isEntry, slot);

        // MOD: changed — no sound at all for an item leaving; for one arriving,
        // reuse "Ship" (the same cue ShippingBinContainer.Store already plays for an automated shipment
        // — vanilla's ShippingBin.showShipment only plays "backpackIN" when playThrowSound is true,
        // which Automate's own shipment calls deliberately pass false for, so "Ship" is the one that
        // actually plays there today) instead of a chest-lid creak.
        if (isEntry)
            location.playSound("Ship", tile);
    }

    /// <summary>Start (or restart) a chest's time-computed lid-open animation — see <see cref="Patches.ChestLidAnimationPatches"/>, which actually reads this and draws it.</summary>
    /// <param name="chest">The chest to animate.</param>
    private static void TriggerLidAnimation(Chest chest)
    {
        chest.modData[ContainerVisualEffects.LidAnimStartTimeModDataKey] = DateTimeOffset.Now.ToString("O");
        chest.modData[ContainerVisualEffects.LidAnimDurationModDataKey] = ContainerVisualEffects.LidAnimationDurationMs.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>How many distinct vertical stacking slots <see cref="SpawnItemSprite"/> supports before it starts reusing the last one — see that method's own remarks.</summary>
    private const int MaxStackingSlots = 5;

    /// <summary>
    /// MOD: rewritten to port Convenient Inventory's own "drop into the chest"
    /// stage instead of vanilla's shipping-bin recipe — that mod's real source
    /// (checked out locally at <c>E:\CodeProjects\convenientInventory</c>,
    /// <c>ConvenientInventory/QuickStack/QuickStackAnimation.cs</c>) is the one that actually looks
    /// right. That method builds a THREE-stage animation (toss from the farmer's hand, a brief hover,
    /// then drop into the chest); only the last "drop" stage applies here, since nothing is being
    /// thrown from a farmer — its motion/acceleration/scaleChange/alphaFade values are ported directly
    /// (see the <c>itemFadeSprite</c> block in that file), starting from its own "chestPosition" anchor
    /// (1.5 tiles directly above the container). Point 2 — where the drop actually ENDS, and the scale
    /// it ends at — is computed from that same recipe's own physics rather than guessed, so entry and
    /// exit are built around the SAME two reference points Convenient Inventory's own animation
    /// actually produces.
    ///
    /// MOD: fixed — an earlier version scaled every one of motion/acceleration/scaleChange/alphaFade by
    /// the same <c>speedFactor</c> to slow things down, which is wrong for acceleration specifically:
    /// position depends on acceleration QUADRATICALLY in time (distance = motion*t + 0.5*accel*t²), so
    /// stretching the duration by 1/speedFactor while only scaling acceleration linearly made its
    /// contribution to the final position 1/speedFactor times too strong — shrinking the actual
    /// start-to-end distance and landing the animation somewhere quite different from Convenient
    /// Inventory's own. Scaling acceleration by speedFactor² instead exactly preserves the original
    /// trajectory (start point, end point, and end scale) while stretching only how long it takes to
    /// get there. Exit uses this same corrected math, mirrored: it starts at point 2, at the SAME scale
    /// entry actually ends at (not entry's own starting scale), and every motion/acceleration/scaleChange
    /// sign is flipped, rising back up to point 1 at the base 4x scale.
    /// </summary>
    /// <param name="location">The location containing the container.</param>
    /// <param name="tilePixels">The top-left pixel position of the container's own tile area.</param>
    /// <param name="containerWidthPixels">The container's full footprint width, in pixels — see this method's own remarks on why a fixed half-tile assumption isn't used for the horizontal center.</param>
    /// <param name="sample">A sample of the item to draw.</param>
    /// <param name="isEntry">Whether this is the "arriving" (point 1 -&gt; point 2) direction, vs. "leaving" (point 2 -&gt; point 1) — the exact mirror.</param>
    /// <param name="slot">This item type's 0-based stacking slot — see <see cref="PlayEntryEffect"/>'s own remarks.</param>
    private static void SpawnItemSprite(GameLocation location, Vector2 tilePixels, float containerWidthPixels, Item sample, bool isEntry, int slot)
    {
        // Convenient Inventory's own unscaled "drop into chest" values (QuickStackAnimation.cs's itemFadeSprite block).
        const float baseScale = 4f;
        const float baseMotionY = 4.5f;
        const float baseAccelerationY = -0.08f; // decelerates the fall — eases to a stop rather than speeding up
        const float baseScaleChange = -0.07f;
        const float baseAlphaFade = 0.04f;

        // MOD: added — slowed down; see this method's own remarks for why
        // acceleration needs the SQUARE of this factor to preserve the original trajectory.
        const float speedFactor = 0.4f;
        const float motionY = baseMotionY * speedFactor;
        const float accelerationY = baseAccelerationY * speedFactor * speedFactor;
        const float scaleChange = baseScaleChange * speedFactor;
        const float alphaFade = baseAlphaFade * speedFactor;

        // MOD: added — point 2 (where the drop physically ends, and what scale it's at by then) is
        // derived from Convenient Inventory's own UNSCALED values, so it lands exactly where its
        // animation actually lands, regardless of speedFactor (see this method's own remarks).
        float baseTicksToFade = 1f / baseAlphaFade;
        float fallDistance = baseMotionY * baseTicksToFade + 0.5f * baseAccelerationY * baseTicksToFade * baseTicksToFade;
        float point2Scale = baseScale + baseScaleChange * baseTicksToFade;

        // MOD: added — a vertical offset, with only up to 5 displayed at a time —
        // each item type gets its own fixed slot (0-4, capped so a burst of many different types can't
        // stack arbitrarily far away) among others animating at the same container in the same window.
        int cappedSlot = Math.Min(slot, ContainerVisualEffects.MaxStackingSlots - 1);
        float slotOffsetY = -cappedSlot * 20f;

        // MOD: fixed — was missing a half tile offset, and needed to account for containers bigger
        // than 1 tile — point1/point2 represent the sprite's actual visual CENTER now (see the
        // corner-anchor compensation below), not a corner reference the draw call happens to offset on
        // its own — so this needs to be the container's real horizontal center (half its full footprint
        // width, not a flat +32px half-tile assumption that only happens to be correct for a 1x1
        // container) to land in the middle of it, regardless of how wide the container actually is —
        // e.g. the shipping bin's real 2-tile-wide footprint, or any larger container a different mod
        // might add. Convenient Inventory's own "chestPosition" uses a bare +0px X (no compensation for
        // the corner-anchored draw call at all, and no concept of a non-1-tile container either), which
        // only reads as roughly centered at ITS OWN starting scale/sprite-size combination — not a value
        // to copy literally now that this method computes a true, scale-independent center target.
        Vector2 point1 = tilePixels + new Vector2(containerWidthPixels / 2f, -1.5f * 64f + slotOffsetY); // 1.5 tiles above the container's own horizontal center
        Vector2 point2 = point1 + new Vector2(0f, fallDistance); // wherever that recipe's own physics actually lands

        ParsedItemData data = ItemRegistry.GetDataOrErrorItem(sample.QualifiedItemId);
        Rectangle sourceRect = data.GetSourceRect();
        Vector2 targetCenter = isEntry ? point1 : point2;
        float startScale = isEntry ? baseScale : point2Scale;
        float scaleChangeUsed = isEntry ? scaleChange : -scaleChange;
        float motionYUsed = isEntry ? motionY : -motionY;
        float accelerationYUsed = isEntry ? accelerationY : -accelerationY;

        // MOD: added — the scale is not center anchored, which was making it look like
        // it's still shifting — TemporaryAnimatedSprite always draws with the sprite's CENTER placed
        // at (trackedPosition + (sourceRect.Width/2, sourceRect.Height/2) * scale) — i.e. it treats
        // trackedPosition as a fixed CORNER reference, not the visual center. Since scale changes over
        // time here, that corner-relative center offset changes too, so a straightforwardly-computed
        // trackedPosition drifts even though it's not moving in either axis directly. Both the ISSUE and
        // the FIX are exactly analogous for X and Y: since scale(t) is linear in time (scale0 +
        // scaleChangeUsed*t, no scaleChangeChange in this recipe), the needed correction is itself
        // linear in time, so it can be folded entirely into trackedPosition's own starting point and
        // motion — no change to acceleration, and no per-frame recomputation needed anywhere else.
        Vector2 halfSprite = new(sourceRect.Width / 2f, sourceRect.Height / 2f);
        Vector2 trackedStartPosition = targetCenter - halfSprite * startScale;
        Vector2 trackedMotion = new(-halfSprite.X * scaleChangeUsed, motionYUsed - halfSprite.Y * scaleChangeUsed);

        // MOD: added — matches vanilla's own Object.draw() depth for this tile (the same formula
        // Convenient Inventory itself reuses, "Refactored from Object.draw()"), so the sprite sorts at
        // the container's own depth instead of defaulting to 0 and drawing behind it.
        float baseLayerDepth = (tilePixels.Y + 64f) / 10000f + tilePixels.X / 3200000f;

        // MOD: added — flavored items (wine, juice, pickles, jelly, etc.) are StardewValley.Objects.ColoredObject,
        // which vanilla never draws as a single plain-tinted sprite (see ColoredObject.draw()/drawInMenu()):
        // unless ColorSameIndexAsParentSheetIndex is set, it draws a white base frame (GetSourceRect(0, ...))
        // plus a SEPARATE colored-mask frame (GetSourceRect(1, ...)) tinted with the item's own color, layered
        // on top. Reusing that same two-layer draw here (rather than just tinting the base icon directly) is
        // what makes the flying sprite actually show the item's specific flavor instead of the generic icon.
        Color baseTint = Color.White;
        bool hasColorOverlay = false;
        Color overlayTint = default;
        Rectangle overlaySourceRect = default;
        if (sample is ColoredObject coloredObject)
        {
            if (coloredObject.ColorSameIndexAsParentSheetIndex)
                baseTint = coloredObject.color.Value;
            else
            {
                hasColorOverlay = true;
                overlayTint = coloredObject.color.Value;
                overlaySourceRect = data.GetSourceRect(1);
            }
        }

        TemporaryAnimatedSprite sprite = ContainerVisualEffects.BuildFlyingItemSprite(data.GetTextureName(), sourceRect, trackedStartPosition, baseTint, alphaFade, startScale, baseLayerDepth - 0.0000015f, trackedMotion, accelerationYUsed, scaleChangeUsed);

        if (hasColorOverlay)
        {
            // MOD: added — drawn at a slightly larger layerDepth than the base sprite, same as vanilla's own
            // "+2E-05f" offset for the colored mask, so it layers on top of (not behind) the white base frame.
            TemporaryAnimatedSprite overlaySprite = ContainerVisualEffects.BuildFlyingItemSprite(data.GetTextureName(), overlaySourceRect, trackedStartPosition, overlayTint, alphaFade, startScale, sprite.layerDepth + 0.0000005f, trackedMotion, accelerationYUsed, scaleChangeUsed);
            Game1.Multiplayer.broadcastSprites(location, sprite, overlaySprite);
        }
        else
            Game1.Multiplayer.broadcastSprites(location, sprite);
    }

    /// <summary>Build one layer of the flying item-icon sprite used by <see cref="SpawnItemSprite"/>, sharing the same trajectory/physics fields — see that method's own remarks for why a flavored item needs two of these layered together.</summary>
    /// <param name="textureName">The sprite sheet texture to draw from.</param>
    /// <param name="sourceRect">The source rect on that texture to draw.</param>
    /// <param name="trackedStartPosition">The sprite's starting tracked position (its corner reference, not its visual center — see <see cref="SpawnItemSprite"/>'s own remarks).</param>
    /// <param name="tint">The color to tint this layer.</param>
    /// <param name="alphaFade">How fast this layer fades out per tick.</param>
    /// <param name="startScale">This layer's starting scale.</param>
    /// <param name="layerDepth">This layer's draw depth.</param>
    /// <param name="motion">This layer's starting motion.</param>
    /// <param name="accelerationY">This layer's vertical acceleration.</param>
    /// <param name="scaleChange">How fast this layer's scale changes per tick.</param>
    private static TemporaryAnimatedSprite BuildFlyingItemSprite(string textureName, Rectangle sourceRect, Vector2 trackedStartPosition, Color tint, float alphaFade, float startScale, float layerDepth, Vector2 motion, float accelerationY, float scaleChange)
    {
        TemporaryAnimatedSprite sprite = TemporaryAnimatedSprite.GetTemporaryAnimatedSprite(textureName, sourceRect, trackedStartPosition, flipped: false, alphaFade: alphaFade, tint);
        sprite.scale = startScale;
        sprite.layerDepth = layerDepth;
        sprite.motion = motion;
        sprite.acceleration = new Vector2(0f, accelerationY);
        sprite.scaleChange = scaleChange;

        // MOD: added — the motion should be straight down/up; an earlier version drifted diagonally.
        // GetTemporaryAnimatedSprite pulls from the game's own pooled sprite instances, which can carry
        // over a nonzero rotation/rotationChange/xPeriodic left behind by some ENTIRELY UNRELATED piece
        // of vanilla code that used the same pooled object earlier — motion.X/acceleration.X above are
        // already hard-zeroed, so this covers the other fields that could still visually read as
        // "diagonal" (a rotating icon drifts sideways as it spins around an off-center origin) without
        // the sprite's own position actually moving off the vertical axis.
        sprite.rotation = 0f;
        sprite.rotationChange = 0f;
        sprite.xPeriodic = false;

        return sprite;
    }
}

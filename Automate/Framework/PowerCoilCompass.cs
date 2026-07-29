using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework.Patches;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Draws an animated compass arrow — the same "continue"/bring-row arrow sprite used in
/// <see cref="PowerSiloMenu"/>'s own bring-request rows — pointing toward every Power Coil in the
/// player's CURRENT location that's currently off-screen, while <see cref="PowerCoilMapMarkerPatches.ShowMarkers"/>
/// is toggled on. Modeled on the "LocationCompass" mod's own on-screen-edge-clamped-arrow approach, but
/// simpler: a single ray-to-rectangle clamp instead of per-quadrant branching, and only ever shown for
/// the player's own current location — a coil on a different map wouldn't have a meaningful screen-space
/// direction to point in anyway (see <see cref="PowerCoilMapMarkerPatches"/>'s own remarks for why this
/// whole feature is built independently of, rather than integrated with, the NPCMapLocations/LocationCompass
/// mods it takes visual inspiration from).
/// </summary>
internal static class PowerCoilCompass
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the item names/IDs that count as a Power Coil for capacity purposes.</summary>
    private static Func<HashSet<string>>? GetSourceNames;

    /// <summary>How close to the screen edge the arrow is allowed to get, in pixels — 2.5x the original 64px margin, per feedback.</summary>
    private const int ScreenMargin = 160;

    /// <summary>MOD: added. Extra clearance above the coil's own rendered sprite top for the hovering arrow, in pixels.</summary>
    private const float OnScreenHoverClearance = -24f;

    /// <summary>MOD: added. The arrow's draw scale — bigger than the default 4x <see cref="Utility.drawWithShadow"/> otherwise falls back to, matching PowerSiloMenu's own bring-row arrow.</summary>
    private const float ArrowScale = 6f;

    /// <summary>MOD: added. The tint for an unpowered coil's arrow, so it stands out from a normal (white) powered one.</summary>
    private static readonly Color UnpoweredTint = Color.Red;

    /// <summary>
    /// MOD: added. The rotation that points the arrow straight down — derived the same way as the
    /// off-screen case's <c>angle + PI/2</c> calibration (see <see cref="Draw"/>'s own remarks): a
    /// "pointing down" direction is <see cref="MathHelper.PiOver2"/> in atan2 terms (screen Y increases
    /// downward), so its rotation is <c>PiOver2 + PiOver2</c> = <see cref="MathHelper.Pi"/>.
    /// </summary>
    private const float PointDownRotation = MathHelper.Pi;

    /// <summary>The vanilla "continue"/bring-row arrow's own source rect, from <c>Game1.mouseCursors</c> — the same one <see cref="PowerSiloMenu"/> uses for its bring-request rows.</summary>
    private static readonly Rectangle ArrowSourceRect = new(412, 495, 5, 4);

    /// <summary>
    /// MOD: added. The sprite's own center, used as the <c>origin</c> for every draw call below instead
    /// of <see cref="Vector2.Zero"/>. This matters a lot more here than it did for PowerSiloMenu's own
    /// static (never-rotated) use of the same sprite: XNA rotates a sprite around its <c>origin</c>, so
    /// an origin at the top-left corner (the default) makes the sprite swing around the given position
    /// in a circle as the rotation changes — which is exactly why the arrow looked "offset" when
    /// pointing down and seemed to jump around unpredictably as the player moved and the angle changed.
    /// Rotating around the sprite's own center instead keeps <c>position</c> meaning "the sprite's
    /// center," regardless of its current rotation.
    /// </summary>
    private static readonly Vector2 ArrowOrigin = new(PowerCoilCompass.ArrowSourceRect.Width / 2f, PowerCoilCompass.ArrowSourceRect.Height / 2f);

    /// <summary>MOD: added. How many draw calls to go between re-scanning the current location for Power Coils — see <see cref="RefreshCoilCacheIfNeeded"/>'s remarks for why this exists.</summary>
    private const int RescanIntervalFrames = 30;

    /// <summary>MOD: added. The location <see cref="CachedCoils"/> was last scanned for.</summary>
    private static GameLocation? CachedLocation;

    /// <summary>MOD: added. Every Power Coil found in <see cref="CachedLocation"/> as of the last scan.</summary>
    private static readonly List<SObject> CachedCoils = [];

    /// <summary>MOD: added. Draw calls remaining before <see cref="CachedCoils"/> is refreshed again, even if the location hasn't changed.</summary>
    private static int TicksUntilRescan;


    /*********
    ** Public methods
    *********/
    /// <summary>Prepare this class before <see cref="Draw"/> is called.</summary>
    /// <param name="getSourceNames">Get the item names/IDs that count as a Power Coil for capacity purposes.</param>
    public static void Initialize(Func<HashSet<string>> getSourceNames)
    {
        PowerCoilCompass.GetSourceNames = getSourceNames;
    }

    /// <summary>Draw a compass arrow toward every off-screen Power Coil in the player's current location.</summary>
    /// <param name="b">The sprite batch being drawn to.</param>
    public static void Draw(SpriteBatch b)
    {
        if (!PowerCoilMapMarkerPatches.ShowMarkers || !Context.IsWorldReady || Game1.activeClickableMenu != null || PowerCoilCompass.GetSourceNames is not { } getSourceNames)
            return;

        HashSet<string> sourceNames = getSourceNames();
        if (sourceNames.Count == 0)
            return;

        PowerCoilCompass.RefreshCoilCacheIfNeeded(sourceNames);
        if (PowerCoilCompass.CachedCoils.Count == 0)
            return;

        Vector2 playerScreenPos = Game1.GlobalToLocal(Game1.viewport, Game1.player.Position);
        Rectangle onScreenBounds = new(0, 0, Game1.viewport.Width, Game1.viewport.Height);

        // MOD: the same little "reach toward" bounce vanilla's own continue-arrow animation uses
        // (Game1.dialogueButtonScale, also used by PowerSiloMenu's bring-row arrows) — applied along
        // the pointing direction for an off-screen coil, or as a gentle float for an on-screen one.
        float bounce = 8f * Game1.dialogueButtonScale / 10f;

        // MOD: added — the Power Coil's own world sprite is 3 tiles tall (not the standard 1-2), so the
        // on-screen hover position needs to clear its actual rendered height, not just sit a fixed
        // distance above the tile itself. Computed from the real texture rather than a hardcoded guess,
        // using the exact same anchor math PowerCoilPatches.Draw_Prefix uses to position that sprite in
        // the first place: its rendered top sits (textureHeight * 4 - 32) pixels above the tile's own
        // center (coilScreenPos below), one tile's worth of "normal" height already accounted for.
        float coilTextureHeight = ItemRegistry.GetDataOrErrorItem(PowerCoilPatches.TargetQualifiedItemId).GetTexture().Height;
        float coilSpriteTopAboveTileCenter = coilTextureHeight * 4f - 32f;
        float hoverHeightAboveTileCenter = coilSpriteTopAboveTileCenter + PowerCoilCompass.OnScreenHoverClearance;

        foreach (SObject obj in PowerCoilCompass.CachedCoils)
        {
            Color tint = PowerCoilPatches.IsPowered(obj) ? Color.White : PowerCoilCompass.UnpoweredTint;

            Vector2 coilWorldCenter = obj.TileLocation * 64f + new Vector2(32f, 32f);
            Vector2 coilScreenPos = Game1.GlobalToLocal(Game1.viewport, coilWorldCenter);

            // MOD: added — the exact spot the on-screen hover marker sits at (see below), computed
            // up front (without the bounce wobble, so it's a stable reference) so the OFF-screen arrow
            // can aim at this same point instead of the coil's tile-center — see its own remarks below
            // for why.
            Vector2 hoverTargetPos = new(coilScreenPos.X, coilScreenPos.Y - hoverHeightAboveTileCenter);

            // MOD: fixed — checks the coil's WHOLE rendered bounding box (accounting for its 3-tile
            // height), not just its tile-center point, so the direction arrow keeps showing as long as
            // ANY part of the tall sprite is still off-screen — only switching to the on-screen hover
            // once the entire thing has scrolled into view.
            Rectangle coilBounds = new(
                (int)(coilScreenPos.X - 32f),
                (int)(coilScreenPos.Y - coilSpriteTopAboveTileCenter),
                64,
                (int)(coilSpriteTopAboveTileCenter + 32f)
            );

            if (onScreenBounds.Contains(coilBounds))
            {
                // MOD: already visible on-screen — instead of just disappearing, hover directly above
                // the coil and point straight down at it, like a quest marker, rather than pointing in
                // the (no longer meaningful once you're already looking at it) player-relative direction.
                Vector2 hoverPosition = new(hoverTargetPos.X, hoverTargetPos.Y - bounce);
                Utility.drawWithShadow(b, Game1.mouseCursors, hoverPosition, PowerCoilCompass.ArrowSourceRect, tint, PowerCoilCompass.PointDownRotation, PowerCoilCompass.ArrowOrigin, PowerCoilCompass.ArrowScale);
                continue;
            }

            // MOD: changed — aims at hoverTargetPos (where the on-screen hover marker will appear)
            // rather than the coil's own tile-center, so the two line up continuously as the player
            // approaches instead of the aim point jumping the moment it crosses into view.
            float angle = (float)Math.Atan2(hoverTargetPos.Y - playerScreenPos.Y, hoverTargetPos.X - playerScreenPos.X);
            float dirX = (float)Math.Cos(angle);
            float dirY = (float)Math.Sin(angle);

            // MOD: fixed — clamps the ray from the PLAYER'S OWN screen position (not an assumed screen
            // center) to the screen edge, since the camera doesn't perfectly center the player near a
            // map's edges/boundaries — assuming "player = screen center" there meant the ray was cast
            // from the wrong origin whenever the camera was off-center, which is exactly what made the
            // arrow seem to point a believable-but-wrong direction on one side of a coil and a correct
            // one on the other (the discrepancy is only really visible near a map boundary). Distance to
            // each edge is signed by which way the ray is actually heading, not just the margin-inset
            // half-width/half-height, since the player's distance to the left edge and right edge (etc.)
            // aren't equal once they're off-center.
            float scaleX = dirX switch
            {
                > 0f => (Game1.viewport.Width - PowerCoilCompass.ScreenMargin - playerScreenPos.X) / dirX,
                < 0f => (PowerCoilCompass.ScreenMargin - playerScreenPos.X) / dirX,
                _ => float.MaxValue
            };
            float scaleY = dirY switch
            {
                > 0f => (Game1.viewport.Height - PowerCoilCompass.ScreenMargin - playerScreenPos.Y) / dirY,
                < 0f => (PowerCoilCompass.ScreenMargin - playerScreenPos.Y) / dirY,
                _ => float.MaxValue
            };
            float clampDistance = Math.Min(scaleX, scaleY);

            Vector2 arrowPosition = playerScreenPos + new Vector2(dirX, dirY) * (clampDistance + bounce);

            // MOD: PowerSiloMenu's own bring-row arrow uses a fixed rotation of PI/2 to point right;
            // since Atan2(0, +1) (i.e. "target directly to the right") is 0, PI/2 is this sprite's own
            // "pointing right" calibration offset, added to whatever direction we actually computed.
            Utility.drawWithShadow(b, Game1.mouseCursors, arrowPosition, PowerCoilCompass.ArrowSourceRect, tint, angle + MathHelper.PiOver2, PowerCoilCompass.ArrowOrigin, PowerCoilCompass.ArrowScale);
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// MOD: added. Refresh <see cref="CachedCoils"/> if the player's changed location, or if it's just
    /// been too long since the last scan — performance guard for locations with a large number of
    /// objects (e.g. 100+ Power Coils, or just a busy farm generally): without this, <see cref="Draw"/>
    /// would re-scan and filter EVERY object in the current location on EVERY single rendered frame,
    /// purely to find the (usually much smaller, and rarely-changing) set of actual Power Coils. A
    /// newly placed/broken coil can take up to <see cref="RescanIntervalFrames"/> frames (well under a
    /// second) to show up/disappear from the compass, which is an imperceptible trade-off for the
    /// avoided per-frame scan cost.
    /// </summary>
    /// <param name="sourceNames">The item names/IDs that count as a Power Coil for capacity purposes.</param>
    private static void RefreshCoilCacheIfNeeded(HashSet<string> sourceNames)
    {
        if (ReferenceEquals(PowerCoilCompass.CachedLocation, Game1.currentLocation) && PowerCoilCompass.TicksUntilRescan > 0)
        {
            PowerCoilCompass.TicksUntilRescan--;
            return;
        }

        PowerCoilCompass.CachedLocation = Game1.currentLocation;
        PowerCoilCompass.CachedCoils.Clear();
        foreach (SObject obj in Game1.currentLocation.Objects.Values)
        {
            if (sourceNames.Contains(obj.QualifiedItemId) || sourceNames.Contains(obj.Name))
                PowerCoilCompass.CachedCoils.Add(obj);
        }

        PowerCoilCompass.TicksUntilRescan = PowerCoilCompass.RescanIntervalFrames;
    }
}

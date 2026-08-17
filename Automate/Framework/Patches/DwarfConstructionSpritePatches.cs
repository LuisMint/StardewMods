using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Reskins the vanilla under-construction/upgrading visual for
/// Dwarf-built structures only, using a custom "Cursors_Dwarf" variant of the same
/// <c>LooseSprites/Cursors</c> sheet vanilla draws that visual from, plus a single ladder tile drawn
/// one tile right and one tile down from the footprint's top-left corner (the dwarves dug an access
/// shaft), but not until the day after construction/upgrading starts —
/// matching the same "vanilla's own visual changes the next day" reasoning as the reskin itself —
/// and gone again once construction/upgrading finishes.
///
/// Modeled directly on the "[CP] Seasonal Construction" content pack, which reskins the
/// same construction visual for everyone by fully overlaying <c>LooseSprites/Cursors</c> per season
/// (its <c>content.json</c> confirmed the target asset and that its overlay PNGs are full copies of
/// the vanilla sheet's own dimensions, mostly transparent except for the edited construction-icon
/// region — <see cref="DwarfAssetName"/>'s PNG follows the same convention).
/// Content Patcher alone can't scope a reskin to only SOME buildings (its conditions are global game
/// state, not per-building-instance), so this achieves the same "full sheet swap" trick via Harmony
/// instead: <see cref="Draw_Prefix"/>/<see cref="Draw_Postfix"/> temporarily swap the shared
/// <see cref="Game1.mouseCursors"/> reference to the Dwarf variant for the duration of a single
/// Dwarf building's own <see cref="Building.draw(SpriteBatch)"/> call, then restore it immediately
/// after — safe because that field is only read synchronously during each building's own draw call,
/// and Building.draw's construction visual is the only thing in that call reading from Cursors. The
/// ladder tile piggybacks on the same postfix, drawn separately (not part of the Cursors sheet at all).
///
/// The "day after" tracking is persisted in the building's own <see cref="Building.modData"/> (see
/// <see cref="FirstObservedDaysModDataKey"/>), not an in-memory table keyed by the <see cref="Building"/>
/// reference — a save reload (including a "reset the day" cheat/mod) deserializes a brand new Building
/// instance for the same in-game structure, which would make an in-memory, reference-keyed table look
/// like it had never seen that building before and silently re-arm the "wait a day" gate, hiding the
/// ladder again even mid-construction. modData survives that round-trip because it's part of the
/// building's own saved state.
/// </summary>
internal static class DwarfConstructionSpritePatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The <c>Data/Buildings</c> "Builder" value a structure must be tagged with for this to apply — matches <see cref="DwarfBuildMenuPatches"/>'s own constant of the same name.</summary>
    private const string BuilderName = "Dwarf";

    /// <summary>The asset name of the Dwarf-reskinned <c>LooseSprites/Cursors</c> variant (loaded by the PoweredAutomation content pack from <c>Cursors_Dwarf.png</c>).</summary>
    private const string DwarfAssetName = "Mods/luisMint.PoweredAutomation/Cursors_Dwarf";

    /// <summary>The asset name of the single-tile ladder decoration (loaded by the PoweredAutomation content pack from <c>DwarfLadder.png</c>, a 16x16 single tile).</summary>
    private const string LadderAssetName = "Mods/luisMint.PoweredAutomation/DwarfLadder";

    /// <summary>The ladder tile's full source rectangle — it's a dedicated single-tile image, not a region within a larger sheet.</summary>
    private static readonly Rectangle LadderSourceRect = new(0, 0, 16, 16);

    /// <summary>The cached Dwarf cursors texture, once successfully loaded (see <see cref="GetDwarfCursorsTexture"/>) — <c>null</c> either before the first attempt or if that attempt failed.</summary>
    private static Texture2D? CachedTexture;

    /// <summary>Whether <see cref="GetDwarfCursorsTexture"/> has already attempted to load <see cref="CachedTexture"/> this session, so a failed load is only attempted once, not every frame.</summary>
    private static bool TriedLoading;

    /// <summary>The cached ladder texture, once successfully loaded (see <see cref="GetLadderTexture"/>) — <c>null</c> either before the first attempt or if that attempt failed.</summary>
    private static Texture2D? CachedLadderTexture;

    /// <summary>Whether <see cref="GetLadderTexture"/> has already attempted to load <see cref="CachedLadderTexture"/> this session, so a failed load is only attempted once, not every frame.</summary>
    private static bool TriedLoadingLadder;

    /// <summary>The <see cref="Building.modData"/> key storing the remaining-days value (<see cref="Building.daysOfConstructionLeft"/> or <see cref="Building.daysUntilUpgrade"/>) first observed for a building, used to hold off drawing the ladder tile until at least one in-game day has ticked since it started. Cleared once construction/upgrading ends so a later reconstruction starts fresh. Persisted (not an in-memory table) — see this class's own remarks for why.</summary>
    private const string FirstObservedDaysModDataKey = "luisMint.PoweredAutomation/DwarfConstructionFirstObservedDays";


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Building), nameof(Building.draw), [typeof(SpriteBatch)]),
            prefix: new HarmonyMethod(typeof(DwarfConstructionSpritePatches), nameof(Draw_Prefix)),
            postfix: new HarmonyMethod(typeof(DwarfConstructionSpritePatches), nameof(Draw_Postfix))
        );
    }

    /// <summary>Get whether <paramref name="building"/> currently shows the ladder tile — i.e. it's a Dwarf structure, still under construction/upgrading, and at least one in-game day has passed since it started. MOD: added — shared with <see cref="DwarfConstructionSiteInteractionPatches"/> so the interaction it grants ("loot from the ladder hole") only ever applies while the ladder itself is actually visible, using the exact same day-tracking state <see cref="Draw_Postfix"/> already maintains rather than a second, potentially-out-of-sync copy.</summary>
    /// <param name="building">The building to check.</param>
    internal static bool HasLadderAppeared(Building building)
    {
        bool active = building.daysOfConstructionLeft.Value > 0 || building.daysUntilUpgrade.Value > 0;
        if (!active || building.GetData()?.Builder != DwarfConstructionSpritePatches.BuilderName)
            return false;

        if (!building.modData.TryGetValue(DwarfConstructionSpritePatches.FirstObservedDaysModDataKey, out string? raw) || !int.TryParse(raw, out int firstObserved))
            return false;

        int remainingDays = building.daysOfConstructionLeft.Value > 0
            ? building.daysOfConstructionLeft.Value
            : building.daysUntilUpgrade.Value;

        return remainingDays < firstObserved;
    }

    /// <summary>Get the tile position the ladder is drawn at (one tile right and one tile down from the footprint's top-left corner) — shared with <see cref="DwarfConstructionSiteInteractionPatches"/> so its loot spawns from the same spot.</summary>
    /// <param name="building">The building to compute the ladder's tile position for.</param>
    internal static Vector2 GetLadderTile(Building building)
    {
        return new Vector2(building.tileX.Value + 1, building.tileY.Value + 1);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Swap <see cref="Game1.mouseCursors"/> to the Dwarf variant just before a Dwarf building draws its under-construction/upgrading visual.</summary>
    /// <param name="__instance">The building about to be drawn.</param>
    /// <param name="__state">Set to the original texture if swapped, so <see cref="Draw_Postfix"/> knows to restore it (and that the ladder tile should also be drawn); left <c>null</c> otherwise.</param>
    private static void Draw_Prefix(Building __instance, out Texture2D? __state)
    {
        __state = null;

        if (__instance.isMoving)
            return;

        bool active = __instance.daysOfConstructionLeft.Value > 0 || __instance.daysUntilUpgrade.Value > 0;
        if (!active)
            return;

        if (__instance.GetData()?.Builder != DwarfConstructionSpritePatches.BuilderName)
            return;

        Texture2D? dwarfTexture = DwarfConstructionSpritePatches.GetDwarfCursorsTexture();
        if (dwarfTexture == null)
            return;

        __state = Game1.mouseCursors;
        Game1.mouseCursors = dwarfTexture;
    }

    /// <summary>Restore <see cref="Game1.mouseCursors"/> and draw the ladder tile immediately after the building finishes drawing.</summary>
    /// <param name="__instance">The building that was just drawn.</param>
    /// <param name="b">The sprite batch being drawn to.</param>
    /// <param name="__state">The original texture to restore, or <c>null</c> if nothing was swapped (in which case there's nothing to draw either — <see cref="Draw_Prefix"/> already ruled this building out).</param>
    private static void Draw_Postfix(Building __instance, SpriteBatch b, Texture2D? __state)
    {
        if (__state == null)
        {
            // MOD: added — not an active Dwarf construction/upgrade (Draw_Prefix already ruled it out);
            // clear any tracked "first observed" state so a later reconstruction starts fresh.
            __instance.modData.Remove(DwarfConstructionSpritePatches.FirstObservedDaysModDataKey);
            return;
        }

        Game1.mouseCursors = __state;

        // MOD: added — hold off drawing the ladder until at least one in-game day has passed since this
        // building started (or started upgrading). See this class's own remarks.
        int remainingDays = __instance.daysOfConstructionLeft.Value > 0
            ? __instance.daysOfConstructionLeft.Value
            : __instance.daysUntilUpgrade.Value;

        if (!__instance.modData.TryGetValue(DwarfConstructionSpritePatches.FirstObservedDaysModDataKey, out string? raw) || !int.TryParse(raw, out int firstObserved))
        {
            __instance.modData[DwarfConstructionSpritePatches.FirstObservedDaysModDataKey] = remainingDays.ToString();
            return;
        }

        if (remainingDays >= firstObserved)
            return;

        // MOD: added — vanilla's own "floor" pieces of the construction visual (as opposed to the
        // scaffolding/posts, which already show every day) only ever appear on daysOfConstructionLeft==1
        // (the very last day) — see this class's own remarks for why. Draw them
        // ourselves every day from here on instead, reusing vanilla's own per-tile corner/edge selection
        // logic (see DrawFloorPieces) but without that restriction. MOD: uses the Dwarf-reskinned texture
        // (not __state, which is the ORIGINAL vanilla texture Draw_Prefix saved off for restoration).
        Texture2D? dwarfTextureForFloor = DwarfConstructionSpritePatches.GetDwarfCursorsTexture();
        if (dwarfTextureForFloor != null)
            DwarfConstructionSpritePatches.DrawFloorPieces(__instance, b, dwarfTextureForFloor);

        Texture2D? ladderTexture = DwarfConstructionSpritePatches.GetLadderTexture();
        if (ladderTexture == null)
            return;

        Vector2 ladderTile = DwarfConstructionSpritePatches.GetLadderTile(__instance);
        Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, new Vector2(ladderTile.X * 64f, ladderTile.Y * 64f));
        float depth = System.Math.Max(0f, ((ladderTile.Y + 1) * 64f - 63f) / 10000f);

        b.Draw(ladderTexture, screenPos, DwarfConstructionSpritePatches.LadderSourceRect, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, depth);
    }

    /// <summary>
    /// Draw the construction visual's "floor" pieces (the flat 16x16 caps vanilla itself only draws on
    /// the very last day of construction) for every tile of the footprint, using the SAME per-tile corner/
    /// edge/middle selection vanilla's own <c>Building.draw</c> uses (mirrored from its decompiled
    /// source), but callable on any day. Uses the resting (non-mid-animation) vertical offset vanilla's
    /// own <c>drawPercentage</c> settles at almost all the time — this isn't trying to sync with vanilla's
    /// own brief "rising into place" completion flash, just draw a steady, correctly-positioned floor.
    /// </summary>
    /// <param name="building">The building being drawn.</param>
    /// <param name="b">The sprite batch being drawn to.</param>
    /// <param name="texture">The (Dwarf-reskinned) Cursors texture to draw the floor pieces from.</param>
    private static void DrawFloorPieces(Building building, SpriteBatch b, Texture2D texture)
    {
        int tileX = building.tileX.Value;
        int tileY = building.tileY.Value;
        int tilesWide = building.tilesWide.Value;
        int tilesHigh = building.tilesHigh.Value;

        for (int x = tileX; x < tileX + tilesWide; x++)
        {
            for (int y = tileY; y < tileY + tilesHigh; y++)
            {
                Rectangle sourceRect;
                float extraYOffset = 0f;

                if (x == tileX + tilesWide / 2 && y == tileY + tilesHigh - 1)
                {
                    sourceRect = new Rectangle(367, 277, 16, 16);
                    extraYOffset = -4f;
                }
                else if (x == tileX && y == tileY)
                    sourceRect = new Rectangle(351, 261, 16, 16);
                else if (x == tileX + tilesWide - 1 && y == tileY)
                    sourceRect = new Rectangle(383, 261, 16, 16);
                else if (x == tileX + tilesWide - 1 && y == tileY + tilesHigh - 1)
                    sourceRect = new Rectangle(383, 277, 16, 16);
                else if (x == tileX && y == tileY + tilesHigh - 1)
                    sourceRect = new Rectangle(351, 277, 16, 16);
                else if (x == tileX + tilesWide - 1)
                    sourceRect = new Rectangle(383, 261, 16, 16);
                else if (y == tileY + tilesHigh - 1)
                    sourceRect = new Rectangle(367, 277, 16, 16);
                else if (x == tileX)
                    sourceRect = new Rectangle(351, 261, 16, 16);
                else if (y == tileY)
                    sourceRect = new Rectangle(367, 261, 16, 16);
                else
                    sourceRect = new Rectangle(367, 261, 16, 16);

                Vector2 screenPos = Game1.GlobalToLocal(Game1.viewport, new Vector2(x, y) * 64f) + new Vector2(0f, 16f + extraYOffset);
                b.Draw(texture, screenPos, sourceRect, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1E-05f);
            }
        }
    }

    /// <summary>Get the Dwarf cursors texture, loading it once (and caching a failure just as permanently, so a missing/broken asset doesn't retry every frame).</summary>
    private static Texture2D? GetDwarfCursorsTexture()
    {
        if (DwarfConstructionSpritePatches.TriedLoading)
            return DwarfConstructionSpritePatches.CachedTexture;

        DwarfConstructionSpritePatches.TriedLoading = true;
        try
        {
            DwarfConstructionSpritePatches.CachedTexture = Game1.content.Load<Texture2D>(DwarfConstructionSpritePatches.DwarfAssetName);
        }
        catch
        {
            // fail quietly rather than crashing or spamming the log every frame if the asset is missing.
        }

        return DwarfConstructionSpritePatches.CachedTexture;
    }

    /// <summary>Get the ladder tile texture, loading it once (and caching a failure just as permanently, so a missing/broken asset doesn't retry every frame).</summary>
    private static Texture2D? GetLadderTexture()
    {
        if (DwarfConstructionSpritePatches.TriedLoadingLadder)
            return DwarfConstructionSpritePatches.CachedLadderTexture;

        DwarfConstructionSpritePatches.TriedLoadingLadder = true;
        try
        {
            DwarfConstructionSpritePatches.CachedLadderTexture = Game1.content.Load<Texture2D>(DwarfConstructionSpritePatches.LadderAssetName);
        }
        catch
        {
            // fail quietly rather than crashing or spamming the log every frame if the asset is missing.
        }

        return DwarfConstructionSpritePatches.CachedLadderTexture;
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework.Machines.Objects;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. While holding a Power Coil or Powered Chest to place it, extends vanilla's own single
/// green/red placement-validity tile (see <see cref="SObject.drawPlacementBounds"/>) with additional
/// green tiles previewing the power range that source would actually cover if placed there — the same
/// shape <see cref="PowerSystem"/> itself computes (a square extending <see cref="GetRangeDistance"/>
/// tiles out from a regular source like the Power Coil, or a fixed plus-shape for a "local" source
/// like the Powered Chest), so the preview can never drift out of sync with the real power system.
/// </summary>
internal static class PowerRangePreviewPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The color/opacity of a range preview tile — dimmer than vanilla's own full-opacity placement-validity tile, so the two read as visually distinct (one exact tile vs. a broader informational preview).</summary>
    private static readonly Color RangeTileColor = Color.White * 0.5f;

    /// <summary>Get how many tiles out from a regular power source, in each cardinal direction, its power extends — see <see cref="Models.ModConfig.PowerRangeDistance"/>.</summary>
    private static Func<int>? GetRangeDistance;


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the config accessor needed to preview a regular source's range. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="getRangeDistance">Get how many tiles out from a regular power source, in each cardinal direction, its power extends.</param>
    public static void Initialize(Func<int> getRangeDistance)
    {
        PowerRangePreviewPatches.GetRangeDistance = getRangeDistance;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.drawPlacementBounds)),
            postfix: new HarmonyMethod(typeof(PowerRangePreviewPatches), nameof(DrawPlacementBounds_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Draw the power range preview tiles on top of vanilla's own placement-validity tile.</summary>
    /// <param name="__instance">The item currently being held for placement.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    private static void DrawPlacementBounds_Postfix(SObject __instance, SpriteBatch spriteBatch)
    {
        IEnumerable<Vector2>? rangeTiles = __instance.QualifiedItemId switch
        {
            PowerCoilPatches.TargetQualifiedItemId => PowerRangePreviewPatches.GetSquareRangeTiles(__instance.TileLocation, Math.Max(0, PowerRangePreviewPatches.GetRangeDistance?.Invoke() ?? 0)),
            PoweredChestMachine.QualifiedItemId => PowerRangePreviewPatches.GetPlusRangeTiles(__instance.TileLocation),
            _ => null
        };
        if (rangeTiles == null)
            return;

        foreach (Vector2 tile in rangeTiles)
        {
            // MOD: skip the center tile — vanilla's own drawPlacementBounds (which this runs right
            // after) already drew a full-opacity green/red tile there.
            if (tile == __instance.TileLocation)
                continue;

            spriteBatch.Draw(
                Game1.mouseCursors,
                new Vector2(tile.X * 64f - Game1.viewport.X, tile.Y * 64f - Game1.viewport.Y),
                new Rectangle(194, 388, 16, 16), // MOD: same source rect vanilla's own valid-placement tile uses
                PowerRangePreviewPatches.RangeTileColor,
                0f,
                Vector2.Zero,
                4f,
                SpriteEffects.None,
                0.01f
            );
        }
    }

    /// <summary>Get every tile in a square extending a given distance out from a center tile in each cardinal direction — mirrors <see cref="PowerSystem"/>'s own regular-source range shape.</summary>
    /// <param name="center">The center tile.</param>
    /// <param name="distance">How many tiles out from <paramref name="center"/>, in each cardinal direction, to cover.</param>
    private static IEnumerable<Vector2> GetSquareRangeTiles(Vector2 center, int distance)
    {
        for (int x = (int)center.X - distance; x <= (int)center.X + distance; x++)
        {
            for (int y = (int)center.Y - distance; y <= (int)center.Y + distance; y++)
                yield return new Vector2(x, y);
        }
    }

    /// <summary>Get a fixed plus-shaped area (a center tile plus its 4 orthogonal neighbors) — mirrors <see cref="PowerSystem"/>'s own "local" source range shape.</summary>
    /// <param name="center">The center tile.</param>
    private static IEnumerable<Vector2> GetPlusRangeTiles(Vector2 center)
    {
        yield return center;
        yield return center + new Vector2(1, 0);
        yield return center + new Vector2(-1, 0);
        yield return center + new Vector2(0, 1);
        yield return center + new Vector2(0, -1);
    }
}

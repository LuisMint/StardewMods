using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Extensions;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. A static lantern-strength light over each Cave Hole interior's own lantern sprite (a 2x2
/// wall decoration spanning tiles (11,3)-(12,4) on the interior map, whose geometric center sits at tile
/// (12,4)) — per direct user request, matching the warm glow vanilla mines use for their own lanterns
/// (see <see cref="StardewValley.Object.initializeLightSource"/>'s own Torch case: <c>textureIndex 4</c>,
/// radius 2.5, color (0, 80, 160) — reused verbatim here rather than any of this mod's OWN light tints,
/// since the goal is to match the mines' own look, not this mod's).
///
/// MOD: changed — since every placed Cave Hole now gets its own instanced interior (see
/// <see cref="CaveHoleInteraction"/>'s own remarks), this needs a light in EACH one rather than a single
/// always-present location, tracked by interior name so a given interior's light is only ever added once.
/// </summary>
internal static class CaveHoleAmbientEffect
{
    /*********
    ** Fields
    *********/
    /// <summary>The light source's own deterministic ID (constant across every interior — each interior is its own location, so IDs never collide between them).</summary>
    private const string LightId = "luisMint.PoweredAutomation_CaveHoleLantern";

    /// <summary>The light's world pixel position — the geometric center of the lantern sprite's 2x2 footprint.</summary>
    private static readonly Vector2 LightPosition = new(12 * Game1.tileSize, 4 * Game1.tileSize);

    /// <summary>The light's radius — matches vanilla's own Torch light.</summary>
    private const float LightRadius = 1.5f;

    /// <summary>The light's tint — matches vanilla's own Torch light.</summary>
    private static readonly Color LightColor = new(80, 80, 80);

    /// <summary>The interior location names that have already had their lantern light added.</summary>
    private static readonly HashSet<string> LitInteriors = new();


    /*********
    ** Public methods
    *********/
    /// <summary>Add every placed Cave Hole's lantern light, for any interior that doesn't already have one.</summary>
    public static void EnsureLights()
    {
        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
            {
                if (!CaveHoleInteraction.IsCaveHoleBuildingType(building.buildingType.Value))
                    continue;

                GameLocation? caveHole = building.GetIndoors();
                if (caveHole is null || !CaveHoleAmbientEffect.LitInteriors.Add(caveHole.NameOrUniqueName))
                    continue; // still under construction, or already lit

                LightSource light = new(
                    id: CaveHoleAmbientEffect.LightId,
                    textureIndex: 4,
                    position: CaveHoleAmbientEffect.LightPosition,
                    radius: CaveHoleAmbientEffect.LightRadius,
                    color: CaveHoleAmbientEffect.LightColor,
                    lightContext: LightSource.LightContext.None,
                    playerID: 0L,
                    onlyLocation: caveHole.NameOrUniqueName
                );

                caveHole.sharedLights.AddLight(light);
            }
        }
    }
}

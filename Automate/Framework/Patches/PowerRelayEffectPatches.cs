using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Extensions;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Two small cosmetic touches for the Power Relay, per direct user request:
/// <list type="bullet">
/// <item>A static lamppost-strength light on every fully-built Relay — reuses the exact same tint/radius
/// convention <see cref="PowerSiloCapPatches"/> already established for its own cap light (<see cref="PowerCoilPatches.LightColor"/>
/// at <see cref="LightRadius"/>), but never moves (unlike the Silo's cap, a Relay has no animated piece
/// to track), so it's only ever created once per Relay and left alone.</item>
/// <item>A one-shot whole-building shake whenever a Relay levels up (see <see cref="TriggerLevelUpShake"/>,
/// called from <see cref="PowerRelayInteraction"/>) — reuses <see cref="PowerSiloCapPatches"/>'s own
/// "flag before Building.draw, react in a Game1.GlobalToLocal postfix, clear after" trick for reaching
/// the WHOLE building (main sprite, any vanilla <c>DrawLayers</c>) without a transpiler, just with a
/// simple linear decay instead of the Silo's rise-animation-tied triangular curve, since a level-up is a
/// single instant, not a multi-second animation.
/// </list>
/// </summary>
internal static class PowerRelayEffectPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the <c>buildingType</c> ID(s) that count as a Power Relay.</summary>
    private static Func<HashSet<string>>? GetRelayBuildingNames;

    /// <summary>The light's radius — matches <see cref="PowerSiloCapPatches.LampLightRadius"/>'s own "lamppost strength" precedent.</summary>
    private const float LightRadius = 2f;

    /// <summary>How many tiles up from the bottom of the Relay's drawn sprite the light is centered.</summary>
    private const float LightHeightAboveGroundInTiles = 3f;

    /// <summary>How long a level-up shake lasts, in seconds.</summary>
    private const float ShakeDurationSeconds = 0.5f;

    /// <summary>How fast the shake wobbles side to side, in radians per second — matches <see cref="PowerSiloCapPatches.ShakeSpeed"/>.</summary>
    private const float ShakeSpeed = 36f;

    /// <summary>How far the shake moves side to side at full strength, in screen pixels — matches <see cref="PowerSiloCapPatches.ShakeAmplitude"/>.</summary>
    private const float ShakeAmplitude = 1.25f;

    /// <summary>Each known Power Relay's own light source (plus the location it's currently registered in), created once and left in place — see this class's own remarks for why a Relay's light never needs repositioning.</summary>
    private static readonly Dictionary<Building, (LightSource Light, GameLocation Location)> RelayLights = new();

    /// <summary>Each known Power Relay's remaining level-up shake time, in seconds — absent or 0 means settled/no shake.</summary>
    private static readonly Dictionary<Building, float> ShakeSecondsRemaining = new();

    /// <summary>Whether the building CURRENTLY being drawn is a Power Relay that's mid-shake — see <see cref="PowerSiloCapPatches.IsShakingCurrentBuilding"/>'s own remarks for why this (plus <see cref="GlobalToLocal_Postfix"/>) is what makes the shake reach the whole building.</summary>
    private static bool IsShakingCurrentBuilding;

    /// <summary>The shake offset to apply while <see cref="IsShakingCurrentBuilding"/> is set.</summary>
    private static Vector2 ActiveShakeOffset;


    /*********
    ** Public methods
    *********/
    /// <summary>Prepare this patch class before <see cref="Apply"/> is called.</summary>
    /// <param name="getRelayBuildingNames">Get the <c>buildingType</c> ID(s) that count as a Power Relay.</param>
    public static void Initialize(Func<HashSet<string>> getRelayBuildingNames)
    {
        PowerRelayEffectPatches.GetRelayBuildingNames = getRelayBuildingNames;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Building), nameof(Building.draw), [typeof(SpriteBatch)]),
            prefix: new HarmonyMethod(typeof(PowerRelayEffectPatches), nameof(Draw_Prefix)),
            postfix: new HarmonyMethod(typeof(PowerRelayEffectPatches), nameof(Draw_Postfix))
        );

        // MOD: added — same shared conversion PowerSiloCapPatches also patches; see IsShakingCurrentBuilding's
        // remarks for why this (rather than Building.draw itself) is what makes the shake reach the whole
        // building. A second independent postfix on the same method from a different flag/offset pair —
        // Harmony chains postfixes fine, and the two classes never interfere since each only acts on its
        // own building type.
        harmony.Patch(
            original: AccessTools.Method(typeof(Game1), nameof(Game1.GlobalToLocal), [typeof(xTile.Dimensions.Rectangle), typeof(Vector2)]),
            postfix: new HarmonyMethod(typeof(PowerRelayEffectPatches), nameof(GlobalToLocal_Postfix))
        );
    }

    /// <summary>Advance every known Power Relay's shake timer, and make sure every fully-built Relay has its light — called once per tick from <c>ModEntry.OnUpdateTicked</c>.</summary>
    public static void Tick()
    {
        if (PowerRelayEffectPatches.GetRelayBuildingNames is not { } getRelayBuildingNames)
            return;

        HashSet<string> relayBuildingNames = getRelayBuildingNames();
        if (relayBuildingNames.Count == 0)
            return;

        float deltaSeconds = (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds;
        HashSet<Building> seenBuildings = new();

        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
            {
                if (!relayBuildingNames.Contains(building.buildingType.Value) || building.daysOfConstructionLeft.Value > 0)
                    continue;

                seenBuildings.Add(building);

                if (PowerRelayEffectPatches.ShakeSecondsRemaining.TryGetValue(building, out float remaining) && remaining > 0f)
                    PowerRelayEffectPatches.ShakeSecondsRemaining[building] = Math.Max(0f, remaining - deltaSeconds);

                if (!PowerRelayEffectPatches.RelayLights.ContainsKey(building))
                    PowerRelayEffectPatches.AddRelayLight(location, building);
            }
        }

        PowerRelayEffectPatches.CleanUpRemovedLights(seenBuildings);
    }

    /// <summary>Clear all cached light/shake state — meant to be called on day start, mirroring <see cref="PowerSiloCapPatches.Reset"/>, so a Relay torn down (or a save reloaded) doesn't leave stale entries behind.</summary>
    public static void Reset()
    {
        PowerRelayEffectPatches.RelayLights.Clear();
        PowerRelayEffectPatches.ShakeSecondsRemaining.Clear();
    }

    /// <summary>Trigger a one-shot whole-building shake on a Relay — meant to be called right as it levels up.</summary>
    /// <param name="relay">The Power Relay that just leveled up.</param>
    public static void TriggerLevelUpShake(Building relay)
    {
        PowerRelayEffectPatches.ShakeSecondsRemaining[relay] = PowerRelayEffectPatches.ShakeDurationSeconds;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Create a Relay's static light source, positioned on its own drawn sprite.</summary>
    /// <param name="location">The location containing the Relay.</param>
    /// <param name="building">The Relay to light.</param>
    private static void AddRelayLight(GameLocation location, Building building)
    {
        float centerX = (building.tileX.Value + building.tilesWide.Value / 2f) * Game1.tileSize;
        float groundY = (building.tileY.Value + building.tilesHigh.Value) * Game1.tileSize;
        float y = groundY - PowerRelayEffectPatches.LightHeightAboveGroundInTiles * Game1.tileSize;
        Vector2 lightPosition = new(centerX, y);

        // MOD: deterministic per-tile ID, matching PowerSiloCapPatches' own lightId convention.
        string lightId = $"PowerRelay_{building.tileX.Value}_{building.tileY.Value}";
        LightSource light = new(
            id: lightId,
            textureIndex: 4,
            position: lightPosition,
            radius: PowerRelayEffectPatches.LightRadius,
            color: PowerCoilPatches.LightColor,
            lightContext: LightSource.LightContext.None,
            playerID: 0L,
            onlyLocation: location.NameOrUniqueName
        );

        location.sharedLights.AddLight(light);
        PowerRelayEffectPatches.RelayLights[building] = (light, location);
    }

    /// <summary>Remove the light for any Relay that's no longer actually placed anywhere.</summary>
    /// <param name="stillPresentBuildings">Every Relay <see cref="Tick"/> actually found this pass.</param>
    private static void CleanUpRemovedLights(HashSet<Building> stillPresentBuildings)
    {
        if (PowerRelayEffectPatches.RelayLights.Count == 0)
            return;

        List<Building>? toRemove = null;
        foreach ((Building building, (LightSource light, GameLocation location)) in PowerRelayEffectPatches.RelayLights)
        {
            if (stillPresentBuildings.Contains(building))
                continue;

            location.removeLightSource(light.Id);
            (toRemove ??= new List<Building>()).Add(building);
        }

        if (toRemove != null)
        {
            foreach (Building building in toRemove)
            {
                PowerRelayEffectPatches.RelayLights.Remove(building);
                PowerRelayEffectPatches.ShakeSecondsRemaining.Remove(building);
            }
        }
    }

    /// <summary>Set up the whole-building shake before vanilla's own <see cref="Building.draw(SpriteBatch)"/> body runs, so its main sprite shakes too.</summary>
    /// <param name="__instance">The building about to be drawn.</param>
    private static void Draw_Prefix(Building __instance)
    {
        PowerRelayEffectPatches.IsShakingCurrentBuilding = false;

        if (PowerRelayEffectPatches.GetRelayBuildingNames is not { } getRelayBuildingNames || !getRelayBuildingNames().Contains(__instance.buildingType.Value))
            return;
        if (__instance.isMoving || __instance.daysOfConstructionLeft.Value > 0)
            return;

        if (!PowerRelayEffectPatches.ShakeSecondsRemaining.TryGetValue(__instance, out float remaining) || remaining <= 0f)
            return;

        // MOD: linear decay (not the Silo's triangular rise-tied curve) — a level-up shake is a single
        // instant, not a multi-second animation with a natural "fastest in the middle" motion to match.
        float strength = remaining / PowerRelayEffectPatches.ShakeDurationSeconds;

        float elapsedSeconds = (float)Game1.currentGameTime.TotalGameTime.TotalSeconds;
        float offsetX = (float)Math.Sin(elapsedSeconds * PowerRelayEffectPatches.ShakeSpeed) * PowerRelayEffectPatches.ShakeAmplitude * strength;
        PowerRelayEffectPatches.ActiveShakeOffset = new Vector2(offsetX, 0f);
        PowerRelayEffectPatches.IsShakingCurrentBuilding = true;
    }

    /// <summary>Nudge every position <see cref="Game1.GlobalToLocal(xTile.Dimensions.Rectangle,Vector2)"/> converts, while <see cref="IsShakingCurrentBuilding"/> is set.</summary>
    /// <param name="__result">The converted screen position, mutated in place.</param>
    private static void GlobalToLocal_Postfix(ref Vector2 __result)
    {
        if (PowerRelayEffectPatches.IsShakingCurrentBuilding)
            __result += PowerRelayEffectPatches.ActiveShakeOffset;
    }

    /// <summary>Clear the shake flag after vanilla's own <see cref="Building.draw(SpriteBatch)"/> body runs — in a <c>finally</c>-equivalent unconditional postfix, so it's always cleared even if something above throws, since this flag is read by a patch on a method used for EVERY building's draw call.</summary>
    private static void Draw_Postfix()
    {
        PowerRelayEffectPatches.IsShakingCurrentBuilding = false;
    }
}

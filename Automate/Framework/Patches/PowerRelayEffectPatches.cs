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
/// MOD: added. Two small cosmetic touches for the Power Relay:
/// <list type="bullet">
/// <item>A static lamppost-strength light on every fully-built Relay — reuses the exact same tint/radius
/// convention <see cref="PowerSiloCapPatches"/> already established for its own cap light (<see cref="PowerCoilPatches.LightColor"/>
/// at <see cref="LightRadius"/>). Repositioned every tick from the building's current tile (see
/// <see cref="RelayLights"/>'s own remarks) so it follows a moved Relay, even though — unlike the Silo's
/// cap — a Relay has no animated piece that otherwise needs a position recomputed every tick.</item>
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

    /// <summary>
    /// MOD: changed. Each known Power Relay's own light source (plus the location it's currently
    /// registered in) — created once per Relay, then repositioned every tick in <see cref="AddRelayLight"/>
    /// exactly like <see cref="PowerSiloCapPatches.UpdateCapLight"/> already does for the Silo's cap
    /// light. Originally created once and left alone (a Relay has no ANIMATED piece to track, unlike the
    /// Silo's cap) — but a Relay can still be RELOCATED via the carpenter menu's "move buildings" flow,
    /// which changes <see cref="Building.tileX"/>/<see cref="Building.tileY"/> on the same instance
    /// without any dedicated event to hook (confirmed via testing: the light stayed behind at
    /// the old position after moving a Relay). Recomputing position every tick from the building's
    /// CURRENT tile — cheap, since it only runs over <see cref="KnownRelays"/>, not a world scan — fixes
    /// that for free, the same way it already worked for the Silo.
    /// </summary>
    private static readonly Dictionary<Building, (LightSource Light, GameLocation Location)> RelayLights = new();

    /// <summary>Each known Power Relay's remaining level-up shake time, in seconds — absent or 0 means settled/no shake.</summary>
    private static readonly Dictionary<Building, float> ShakeSecondsRemaining = new();

    /// <summary>Whether the building CURRENTLY being drawn is a Power Relay that's mid-shake — see <see cref="PowerSiloCapPatches.IsShakingCurrentBuilding"/>'s own remarks for why this (plus <see cref="GlobalToLocal_Postfix"/>) is what makes the shake reach the whole building.</summary>
    private static bool IsShakingCurrentBuilding;

    /// <summary>The shake offset to apply while <see cref="IsShakingCurrentBuilding"/> is set.</summary>
    private static Vector2 ActiveShakeOffset;

    /// <summary>
    /// MOD: changed. Every currently-known Power Relay building (with the location it's in) — populated
    /// entirely by events, mirroring <see cref="PowerSiloCapPatches.KnownSilos"/>'s own identical design:
    /// <see cref="FinishConstruction_Postfix"/> adds one the moment it finishes building,
    /// <see cref="DestroyStructure_Postfix"/> removes one the moment it's torn down, and <see cref="Reset"/>
    /// does exactly ONE full-world scan (day start / save load only) as a correctness backstop. A moved
    /// Relay stays valid in this list without any event at all — see <see cref="RelayLights"/>'s own
    /// remarks for how repositioning is handled.
    /// </summary>
    private static readonly List<(Building Building, GameLocation Location)> KnownRelays = new();


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

        // MOD: added — event-based discovery: adds a Relay to KnownRelays the moment it finishes building
        // (see KnownRelays's own remarks for why this replaces a periodic scan). A second independent
        // postfix on the same method PowerSiloCapPatches also patches — Harmony chains these fine.
        harmony.Patch(
            original: AccessTools.Method(typeof(Building), nameof(Building.FinishConstruction)),
            postfix: new HarmonyMethod(typeof(PowerRelayEffectPatches), nameof(FinishConstruction_Postfix))
        );

        // MOD: added — event-based cleanup: removes a Relay from KnownRelays (and its light) the moment
        // it's torn down.
        harmony.Patch(
            original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.destroyStructure), [typeof(Building)]),
            postfix: new HarmonyMethod(typeof(PowerRelayEffectPatches), nameof(DestroyStructure_Postfix))
        );
    }

    /// <summary>
    /// Advance every known Power Relay's shake timer, and make sure every fully-built Relay has its
    /// light — called once per tick from <c>ModEntry.OnUpdateTicked</c>.
    ///
    /// MOD: added — skips entirely while the window is unfocused, same as
    /// <see cref="PowerCoilAmbientEffect.Tick"/>/<see cref="PowerRelayAmbientEffect.Tick"/> (see their own
    /// remarks for why SMAPI's UpdateTicked firing regardless of focus matters here): without this, a
    /// Relay's shake timer kept counting down by real elapsed time while alt-tabbed away, so a level-up
    /// shake could finish silently off-screen instead of actually being seen when focus returned.
    /// </summary>
    public static void Tick()
    {
        if (!Game1.game1.IsActive)
            return;

        if (PowerRelayEffectPatches.GetRelayBuildingNames is not { } getRelayBuildingNames)
            return;

        HashSet<string> relayBuildingNames = getRelayBuildingNames();
        if (relayBuildingNames.Count == 0 || PowerRelayEffectPatches.KnownRelays.Count == 0)
            return; // MOD: added — no known Relay yet (e.g. a fresh save that's never built one) means nothing below has anything to do; skip the per-tick HashSet allocation entirely rather than iterating zero entries

        float deltaSeconds = (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds;
        HashSet<Building> seenBuildings = new();

        foreach ((Building building, GameLocation location) in PowerRelayEffectPatches.KnownRelays)
        {
            if (building.daysOfConstructionLeft.Value > 0)
                continue;

            seenBuildings.Add(building);

            if (PowerRelayEffectPatches.ShakeSecondsRemaining.TryGetValue(building, out float remaining) && remaining > 0f)
                PowerRelayEffectPatches.ShakeSecondsRemaining[building] = Math.Max(0f, remaining - deltaSeconds);

            // MOD: changed — now called every tick (not just once) so the light follows a moved Relay;
            // see RelayLights's own remarks.
            PowerRelayEffectPatches.AddRelayLight(location, building);
        }

        PowerRelayEffectPatches.CleanUpRemovedLights(seenBuildings);
    }

    /// <summary>
    /// MOD: added. Add a Power Relay to <see cref="KnownRelays"/> the moment it finishes building — the
    /// event-based replacement for a periodic discovery scan (see <see cref="KnownRelays"/>'s own
    /// remarks). Idempotent (checked via <see cref="RelayLights"/>'s own presence, since a building can
    /// have <see cref="Building.FinishConstruction"/> called on it more than once).
    /// </summary>
    /// <param name="__instance">The building that just finished construction.</param>
    private static void FinishConstruction_Postfix(Building __instance)
    {
        if (PowerRelayEffectPatches.GetRelayBuildingNames is not { } getRelayBuildingNames || !getRelayBuildingNames().Contains(__instance.buildingType.Value))
            return;

        if (PowerRelayEffectPatches.RelayLights.ContainsKey(__instance))
            return; // already known — Tick() will just keep it lit as normal

        if (__instance.GetParentLocation() is { } location)
            PowerRelayEffectPatches.KnownRelays.Add((__instance, location));
    }

    /// <summary>Remove a Power Relay from <see cref="KnownRelays"/> (and clean up its light/shake state) the moment it's torn down.</summary>
    /// <param name="building">The building that was removed.</param>
    /// <param name="__result">Whether the building was actually removed.</param>
    private static void DestroyStructure_Postfix(Building building, bool __result)
    {
        if (!__result || PowerRelayEffectPatches.GetRelayBuildingNames is not { } getRelayBuildingNames || !getRelayBuildingNames().Contains(building.buildingType.Value))
            return;

        PowerRelayEffectPatches.KnownRelays.RemoveAll(entry => ReferenceEquals(entry.Building, building));
        PowerRelayEffectPatches.ShakeSecondsRemaining.Remove(building);

        if (PowerRelayEffectPatches.RelayLights.TryGetValue(building, out (LightSource Light, GameLocation Location) existing))
        {
            existing.Location.removeLightSource(existing.Light.Id);
            PowerRelayEffectPatches.RelayLights.Remove(building);
        }
    }

    /// <summary>
    /// MOD: changed. Clear all cached light/shake state, then do exactly ONE full-world scan to rebuild
    /// <see cref="KnownRelays"/> — meant to be called on day start, mirroring <see cref="PowerSiloCapPatches.Reset"/>'s
    /// own identical design (see that method's own remarks for why this one scan, unlike everything
    /// else, stays — a save reload recreates every <see cref="Building"/> instance without re-firing
    /// <see cref="Building.FinishConstruction"/> for ones already standing).
    /// </summary>
    public static void Reset()
    {
        PowerRelayEffectPatches.RelayLights.Clear();
        PowerRelayEffectPatches.ShakeSecondsRemaining.Clear();
        PowerRelayEffectPatches.KnownRelays.Clear();

        if (PowerRelayEffectPatches.GetRelayBuildingNames is not { } getRelayBuildingNames)
            return;

        HashSet<string> relayBuildingNames = getRelayBuildingNames();
        if (relayBuildingNames.Count == 0)
            return;

        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
            {
                if (relayBuildingNames.Contains(building.buildingType.Value))
                    PowerRelayEffectPatches.KnownRelays.Add((building, location));
            }
        }
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
    /// <summary>Create (the first time a Relay is seen) or reposition (every tick after) a Relay's static light source — see <see cref="RelayLights"/>'s own remarks for why this reposition-every-tick approach (matching <see cref="PowerSiloCapPatches.UpdateCapLight"/>) replaced the old create-once approach.</summary>
    /// <param name="location">The location containing the Relay.</param>
    /// <param name="building">The Relay to light.</param>
    private static void AddRelayLight(GameLocation location, Building building)
    {
        float centerX = (building.tileX.Value + building.tilesWide.Value / 2f) * Game1.tileSize;
        float groundY = (building.tileY.Value + building.tilesHigh.Value) * Game1.tileSize;
        float y = groundY - PowerRelayEffectPatches.LightHeightAboveGroundInTiles * Game1.tileSize;
        Vector2 lightPosition = new(centerX, y);

        if (PowerRelayEffectPatches.RelayLights.TryGetValue(building, out (LightSource Light, GameLocation Location) existing))
        {
            existing.Light.position.Value = lightPosition;

            // MOD: added — a Relay can (rarely) change which location it's registered under without
            // ever being torn down (e.g. moved via the carpenter menu's "move buildings" flow); mirrors
            // PowerSiloCapPatches.UpdateCapLight's own identical cross-location handling.
            if (existing.Location != location)
            {
                existing.Location.removeLightSource(existing.Light.Id);
                location.sharedLights.AddLight(existing.Light);
                PowerRelayEffectPatches.RelayLights[building] = (existing.Light, location);
            }

            return;
        }

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

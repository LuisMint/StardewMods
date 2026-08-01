using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Extensions;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Draws the Power Silo's "cap" as a second, independently-floating piece above its
/// (otherwise static) main body — rising higher above the GROUND the more tiers a Silo has reached,
/// then holding in place once it reaches the solar tier (see <see cref="GetTargetRiseTiles"/>'s remarks
/// for why it doesn't keep rising forever). Vanilla's own <c>Data/Buildings</c> <c>DrawLayers</c> field
/// already supports extra layers with their own texture/position, but only ever a FIXED position or a
/// frame-cycling animation — it can't smoothly tween a layer's position over time the way this needs
/// to whenever the tier changes, so this is a Harmony patch on <see cref="Building.draw(SpriteBatch)"/>
/// instead, anchored off the same <c>drawPosition</c> (the building's own footprint-bottom/"ground"
/// point) vanilla's own main-sprite draw call uses (see <see cref="Draw_Postfix"/>'s remarks) so the cap
/// lines up correctly regardless of the Silo's own tile position or zoom.
///
/// The animation itself is a fixed-duration ease-out-back (see <see cref="Tick"/>/<see cref="EvaluateRise"/>)
/// toward whatever the CURRENT tier's target height is — rises, overshoots past the target by a bit,
/// then settles back down onto it, over a fixed span rather than the asymptotic "never quite arrives"
/// feel of a pure exponential smoothing curve. A Silo's animated height snaps straight to its target the
/// first time it's seen (e.g. right after loading a save), so only an ACTUAL tier change while playing
/// produces a visible rise — nothing animates in from zero on world load. If the target changes again
/// mid-rise, the ease restarts from wherever it currently is (not the old start point), so there's never
/// a visual jump.
///
/// While a Silo is mid-rise, the WHOLE building shakes side to side, strongest through the middle of the
/// ease (where it's moving fastest) and quiet at both ends — see <see cref="Draw_Prefix"/>/
/// <see cref="GlobalToLocal_Postfix"/>'s remarks for how that reaches the main body too, not just the
/// cap, without a transpiler.
/// </summary>
internal static class PowerSiloCapPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The asset name of the Silo's cap texture (loaded by the AutomatePowerPipes content pack).</summary>
    private const string CapAssetName = "Mods/luisMint.AutomatePowerPipes/PowerSiloTop";

    /// <summary>How many tiles the cap hangs above the GROUND (the building's own footprint-bottom point) for a freshly-built Silo (tier 0), before any upgrades.</summary>
    private const float BaseGapTiles = 2f;

    /// <summary>How much higher the cap rises per tier reached, in tiles.</summary>
    private const float RisePerTier = 0.5f;

    /// <summary>MOD: changed — how long a rise takes to fully settle, in seconds, replacing the old exponential-smoothing speed constant. A fixed duration is what makes the ease-in-out actually finish, rather than trailing off forever.</summary>
    private const float RiseDurationSeconds = 1.5f;

    /// <summary>How fast the shake wobbles side to side while mid-rise, in radians per second.</summary>
    private const float ShakeSpeed = 36f;

    /// <summary>How far the shake moves side to side at full strength, in screen pixels.</summary>
    private const float ShakeAmplitude = 1.25f;

    /// <summary>MOD: changed — how far through the rise (as a fraction of <see cref="RiseDurationSeconds"/>) the overshoot peaks, before settling back down for the rest of the duration. Nudged later slightly from an earlier 0.35f, per feedback.</summary>
    private const float OvershootPeakFraction = 0.6f;//0.45f;

    /// <summary>MOD: changed — how far PAST the target the rise overshoots at its peak, as a fraction of the total rise distance (e.g. 0.4 = overshoots 40% of the way again past the target before coming back). Toned down slightly from an earlier 0.6f, per feedback.</summary>
    private const float OvershootAmount = 0.1f;

    /// <summary>MOD: added. The cap's light radius — per direct user request, "the strength of a lamppost". Matches vanilla's own generic <c>isLamp</c> light radius (see <see cref="SObject.checkForAction"/>'s decompiled source, the plain <c>lightSource = new LightSource(4, ..., 3f, ...)</c> branch used for lamp-flagged objects), rather than <see cref="PoweredChestPatches.LightRadius"/>'s much smaller radius.</summary>
    private const float LampLightRadius = 2f;

    /// <summary>Get the <c>buildingType</c> ID(s) that count as a Power Silo.</summary>
    private static Func<HashSet<string>>? GetSiloBuildingNames;

    /// <summary>Get the ordered capacity tiers a Power Silo progresses through.</summary>
    private static Func<List<PowerSiloTierConfig>>? GetTiers;

    /// <summary>The power silo capacity system, used to read a Silo's current tier.</summary>
    private static PowerSiloSystem? PowerSiloSystem;

    /// <summary>
    /// MOD: added. Each known Power Silo's rise animation state — where it eased FROM, where it's
    /// easing TO, and how many seconds into that ease it currently is — updated once per tick in
    /// <see cref="Tick"/> and evaluated (via <see cref="EvaluateRise"/>) in <see cref="Draw_Prefix"/>/
    /// <see cref="Draw_Postfix"/>. Kept as a separate tick step (rather than computed fresh inside the
    /// draw patches) so the animation still advances smoothly regardless of how many times a Silo
    /// happens to get drawn in a given tick.
    /// </summary>
    private static readonly Dictionary<Building, (float Start, float Target, float ElapsedSeconds)> RiseState = new();

    /// <summary>
    /// MOD: added. Each known Power Silo's own light source (plus the location it's currently registered
    /// in, since a torn-down <see cref="Building"/> can't reliably be asked for its own parent location
    /// anymore) — created once per Silo the first time <see cref="Tick"/> sees it, then just repositioned
    /// every tick afterward via <see cref="UpdateCapLight"/> to track the cap's own rise animation,
    /// rather than recreating the <see cref="LightSource"/> (and re-registering it) from scratch each time.
    /// </summary>
    private static readonly Dictionary<Building, (LightSource Light, GameLocation Location)> CapLights = new();

    /// <summary>
    /// MOD: added. Whether the building CURRENTLY being drawn (i.e. between <see cref="Draw_Prefix"/>
    /// and <see cref="Draw_Postfix"/> for one single <see cref="Building.draw(SpriteBatch)"/> call) is a
    /// Power Silo that's mid-rise and should be shaking — read by <see cref="GlobalToLocal_Postfix"/>,
    /// which is what actually makes the shake reach the WHOLE building (main sprite, any vanilla
    /// <c>DrawLayers</c>, everything) rather than just the cap this class draws directly. Every one of
    /// vanilla's own draw calls inside <see cref="Building.draw(SpriteBatch)"/> converts its world
    /// position to screen space via <see cref="Game1.GlobalToLocal(xTile.Dimensions.Rectangle,Vector2)"/>
    /// first, so nudging THAT shared conversion while this flag is set shakes everything drawn through
    /// it, without needing a transpiler to reach into the middle of vanilla's own method body. The same
    /// "flag before, react in a shared low-level method, clear after" trick <see cref="PowerCoilPatches.PlaySound_Prefix"/>
    /// already uses for its own sound-cue redirect.
    /// </summary>
    private static bool IsShakingCurrentBuilding;

    /// <summary>MOD: added. The shake offset to apply while <see cref="IsShakingCurrentBuilding"/> is set — computed once in <see cref="Draw_Prefix"/>, then read (and added to every converted position) by <see cref="GlobalToLocal_Postfix"/>.</summary>
    private static Vector2 ActiveShakeOffset;


    /*********
    ** Public methods
    *********/
    /// <summary>Prepare this patch class before <see cref="Apply"/> is called.</summary>
    /// <param name="getSiloBuildingNames">Get the <c>buildingType</c> ID(s) that count as a Power Silo.</param>
    /// <param name="getTiers">Get the ordered capacity tiers a Power Silo progresses through.</param>
    /// <param name="powerSiloSystem">The power silo capacity system, used to read a Silo's current tier.</param>
    public static void Initialize(Func<HashSet<string>> getSiloBuildingNames, Func<List<PowerSiloTierConfig>> getTiers, PowerSiloSystem powerSiloSystem)
    {
        PowerSiloCapPatches.GetSiloBuildingNames = getSiloBuildingNames;
        PowerSiloCapPatches.GetTiers = getTiers;
        PowerSiloCapPatches.PowerSiloSystem = powerSiloSystem;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Building), nameof(Building.draw), [typeof(SpriteBatch)]),
            prefix: new HarmonyMethod(typeof(PowerSiloCapPatches), nameof(Draw_Prefix)),
            postfix: new HarmonyMethod(typeof(PowerSiloCapPatches), nameof(Draw_Postfix))
        );

        // MOD: added — the cap is only ever drawn by the postfix above, which only fires for an actual
        // placed Building.draw call; the carpenter menu's blueprint icon/held-cursor preview instead
        // calls Building.drawInMenu, which only knows about data-driven Data/Buildings DrawLayers (which
        // the Silo doesn't use, precisely because the cap's rise animation can't be expressed that way —
        // see this class's own remarks) — so without this, the menu only ever showed the body. Draws the
        // cap at its resting tier-0 position, since a blueprint preview has no real tier/rise state.
        harmony.Patch(
            original: AccessTools.Method(typeof(Building), nameof(Building.drawInMenu), [typeof(SpriteBatch), typeof(int), typeof(int)]),
            postfix: new HarmonyMethod(typeof(PowerSiloCapPatches), nameof(DrawInMenu_Postfix))
        );

        // MOD: added — the shared conversion every draw call inside Building.draw funnels through; see
        // IsShakingCurrentBuilding's remarks for why patching this (rather than Building.draw itself) is
        // what makes the shake reach the whole building.
        harmony.Patch(
            original: AccessTools.Method(typeof(Game1), nameof(Game1.GlobalToLocal), [typeof(xTile.Dimensions.Rectangle), typeof(Vector2)]),
            postfix: new HarmonyMethod(typeof(PowerSiloCapPatches), nameof(GlobalToLocal_Postfix))
        );
    }

    /// <summary>
    /// MOD: added. Advance every known Power Silo's rise animation clock by one tick's worth of time —
    /// called once per tick from <c>ModEntry.OnUpdateTicked</c>, independent of drawing (a Silo's cap
    /// keeps animating correctly even while off-screen or in an unvisited location, the same way
    /// <see cref="PowerSiloSystem"/>'s own capacity bookkeeping isn't tied to what's currently rendered
    /// either).
    /// </summary>
    public static void Tick()
    {
        if (PowerSiloCapPatches.GetSiloBuildingNames is not { } getSiloBuildingNames || PowerSiloCapPatches.PowerSiloSystem is not { } powerSiloSystem)
            return;

        HashSet<string> siloBuildingNames = getSiloBuildingNames();
        if (siloBuildingNames.Count == 0)
            return;

        float deltaSeconds = (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds;
        HashSet<Building> seenBuildings = new(); // MOD: added — tracks which Silos are still actually placed this tick, so CleanUpRemovedCapLights can tell a torn-down one from one that's just off-screen

        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
            {
                if (!siloBuildingNames.Contains(building.buildingType.Value) || building.daysOfConstructionLeft.Value > 0)
                    continue;

                float target = PowerSiloCapPatches.GetTargetRiseTiles(building, powerSiloSystem);
                (float Start, float Target, float ElapsedSeconds) state;

                if (!PowerSiloCapPatches.RiseState.TryGetValue(building, out state))
                {
                    // MOD: a Silo seen for the first time (e.g. right after loading a save) snaps
                    // straight to its target — Start == Target means EvaluateRise always returns the
                    // target regardless of ElapsedSeconds, so nothing animates in from zero.
                    state = (target, target, 0f);
                    PowerSiloCapPatches.RiseState[building] = state;
                }
                else if (Math.Abs(state.Target - target) > 0.001f)
                {
                    // MOD: the tier changed — restart the ease from wherever it CURRENTLY is (not the
                    // old start point), so a tier change mid-rise never causes a visual jump.
                    float currentValue = PowerSiloCapPatches.EvaluateRise(state);
                    state = (currentValue, target, 0f);
                    PowerSiloCapPatches.RiseState[building] = state;
                    PowerSiloCapPatches.SpawnUpgradeSmoke(location, building); // MOD: added — a one-time puff burst right as the rise toward the new tier kicks off
                }
                else
                {
                    state = (state.Start, state.Target, state.ElapsedSeconds + deltaSeconds);
                    PowerSiloCapPatches.RiseState[building] = state;
                }

                // MOD: added — per direct user request, a lamppost-strength light that moves with the
                // cap's own rise animation (see UpdateCapLight's own remarks).
                seenBuildings.Add(building);
                PowerSiloCapPatches.UpdateCapLight(location, building, PowerSiloCapPatches.EvaluateRise(state));
            }
        }

        PowerSiloCapPatches.CleanUpRemovedCapLights(seenBuildings);
    }

    /// <summary>
    /// MOD: changed. Clear all cached animation state — meant to be called on day start, mirroring
    /// <see cref="MachineManager.Reset"/>, so a Silo torn down (or a save reloaded) doesn't leave stale
    /// entries behind. Deliberately does NOT also call <see cref="GameLocation.removeLightSource"/> for
    /// every tracked <see cref="CapLights"/> entry here — every Silo still actually standing gets
    /// rediscovered on the very next <see cref="Tick"/> anyway, which re-registers its light under the
    /// SAME deterministic ID (see <see cref="UpdateCapLight"/>), so any old registration is simply
    /// overwritten a moment later. <see cref="CleanUpRemovedCapLights"/> is what handles a Silo that's
    /// genuinely gone.
    /// </summary>
    public static void Reset()
    {
        PowerSiloCapPatches.RiseState.Clear();
        PowerSiloCapPatches.CapLights.Clear();
    }

    /// <summary>
    /// MOD: added. Spawn a small burst of smoke puffs around a Silo's base the moment it starts rising
    /// toward a new tier — the same "LooseSprites\Cursors" smoke sprite vanilla's own
    /// <see cref="Utility.addSmokePuff"/> uses, rebuilt here rather than called directly since that
    /// helper adds straight to <see cref="GameLocation.temporarySprites"/> (not networked), so only the
    /// player who happened to deliver the item would see it; <see cref="Multiplayer.broadcastSprites"/>
    /// instead gets it to everyone, the same reasoning <see cref="PowerCoilAmbientEffect"/> already
    /// follows for its own sparkle.
    /// </summary>
    /// <param name="location">The location containing the Silo.</param>
    /// <param name="building">The Silo that just started rising to a new tier.</param>
    private static void SpawnUpgradeSmoke(GameLocation location, Building building)
    {
        const int puffCount = 6;
        float groundY = (building.tileY.Value + building.tilesHigh.Value) * 64f;

        for (int i = 0; i < puffCount; i++)
        {
            float x = (building.tileX.Value + (float)Game1.random.NextDouble() * building.tilesWide.Value) * 64f;
            float y = groundY - (float)Game1.random.NextDouble() * 32f;

            TemporaryAnimatedSprite sprite = TemporaryAnimatedSprite.GetTemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(372, 1956, 10, 10), new Vector2(x, y), flipped: false, alphaFade: 0.008f, Color.Gray);
            sprite.alpha = 0.75f;
            sprite.motion = new Vector2(0f, -0.5f);
            sprite.acceleration = new Vector2(0.002f, 0f);
            sprite.interval = 99999f;
            sprite.layerDepth = 1f;
            sprite.scale = Game1.random.Next(2, 4);
            sprite.scaleChange = 0.02f;
            sprite.rotationChange = (float)Game1.random.Next(-5, 6) * (float)Math.PI / 256f;
            sprite.delayBeforeAnimationStart = i * 80;

            Game1.Multiplayer.broadcastSprites(location, sprite);
        }
    }

    /// <summary>
    /// MOD: added. Create (the first time a Silo is seen) or reposition (every tick after) a light source
    /// that tracks the cap's own current rise animation — per direct user request, "the strength of a
    /// lamppost" that "moves with the power silo top". Reuses <see cref="PowerCoilPatches.LightColor"/>
    /// (the same hand-tuned tint every other power-related light in this mod already uses) at
    /// <see cref="LampLightRadius"/>, rather than <see cref="PoweredChestPatches.LightRadius"/>'s much
    /// smaller one — this is meant to actually light up the area around the Silo, not just glow softly
    /// right at its own tile.
    /// </summary>
    /// <param name="location">The location containing the Silo.</param>
    /// <param name="building">The Silo whose cap light to update.</param>
    /// <param name="riseTiles">The cap's currently-animated rise height (see <see cref="EvaluateRise"/>), matching whatever <see cref="Draw_Postfix"/> is using to actually draw it this same tick.</param>
    private static void UpdateCapLight(GameLocation location, Building building, float riseTiles)
    {
        // MOD: matches Draw_Postfix's own position math exactly, so the light always sits right on the
        // (currently-rendered) cap, never lagging a tick behind or drifting off it during a rise/overshoot.
        Texture2D capTexture = Game1.content.Load<Texture2D>(PowerSiloCapPatches.CapAssetName);
        Vector2 drawPosition = new(building.tileX.Value * 64f, (building.tileY.Value + building.tilesHigh.Value) * 64f);
        Vector2 capPosition = drawPosition + new Vector2(0f, -riseTiles * 64f - capTexture.Height * 4f);
        Vector2 lightPosition = capPosition + new Vector2(capTexture.Width * 4f / 2f, capTexture.Height * 4f / 2f); // MOD: centered on the cap sprite itself, not its top-left draw anchor

        if (PowerSiloCapPatches.CapLights.TryGetValue(building, out (LightSource Light, GameLocation Location) existing))
        {
            existing.Light.position.Value = lightPosition;

            // MOD: added — a Silo can (rarely) change which location it's registered under without ever
            // being torn down (e.g. moved via the carpenter menu's "move buildings" flow); re-register
            // under the new location if so, rather than leaving the light glowing in the old one.
            if (existing.Location != location)
            {
                existing.Location.removeLightSource(existing.Light.Id);
                location.sharedLights.AddLight(existing.Light);
                PowerSiloCapPatches.CapLights[building] = (existing.Light, location);
            }

            return;
        }

        // MOD: added — deterministic per-tile ID (matching the tile-based hashing vanilla's own object
        // light sources already use), unique within a single location's own sharedLights, which is all
        // that's required here.
        string lightId = $"PowerSiloCap_{building.tileX.Value}_{building.tileY.Value}";
        LightSource light = new(
            id: lightId,
            textureIndex: 4,
            position: lightPosition,
            radius: PowerSiloCapPatches.LampLightRadius,
            color: PowerCoilPatches.LightColor,
            lightContext: LightSource.LightContext.None,
            playerID: 0L,
            onlyLocation: location.NameOrUniqueName
        );

        location.sharedLights.AddLight(light);
        PowerSiloCapPatches.CapLights[building] = (light, location);
    }

    /// <summary>
    /// MOD: added. Remove the cap light for any Silo that's no longer actually placed anywhere (torn
    /// down, or its location removed entirely) — <see cref="Reset"/> alone doesn't catch this mid-day,
    /// since it only runs at day start.
    /// </summary>
    /// <param name="stillPresentBuildings">Every Silo <see cref="Tick"/> actually found this pass.</param>
    private static void CleanUpRemovedCapLights(HashSet<Building> stillPresentBuildings)
    {
        if (PowerSiloCapPatches.CapLights.Count == 0)
            return;

        List<Building>? toRemove = null;
        foreach ((Building building, (LightSource light, GameLocation location)) in PowerSiloCapPatches.CapLights)
        {
            if (stillPresentBuildings.Contains(building))
                continue;

            location.removeLightSource(light.Id);
            (toRemove ??= new List<Building>()).Add(building);
        }

        if (toRemove != null)
        {
            foreach (Building building in toRemove)
                PowerSiloCapPatches.CapLights.Remove(building);
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// MOD: added. Get how many tiles above the ground a Silo's cap should currently be aiming for,
    /// based on its tier — <see cref="BaseGapTiles"/> at tier 0, +<see cref="RisePerTier"/> tiles for
    /// EVERY tier reached after that (one rise step per item quest completed), all the way through the
    /// terminal solar tier itself (reaching it, per request, still counts as "one more item quest done"
    /// — it's the tier before it, the one that actually asks for a Solar Panel as an ingredient, that
    /// finishes the last quest). It naturally stops there since there's no tier beyond the last one to
    /// keep rising toward — the solar tier's own unbounded connected-panel bonus afterward doesn't move
    /// the cap any further.
    /// </summary>
    /// <param name="building">The Power Silo building.</param>
    /// <param name="powerSiloSystem">The power silo capacity system, used to read the Silo's current tier.</param>
    private static float GetTargetRiseTiles(Building building, PowerSiloSystem powerSiloSystem)
    {
        List<PowerSiloTierConfig> tiers = PowerSiloCapPatches.GetTiers?.Invoke() ?? [];
        if (tiers.Count == 0)
            return PowerSiloCapPatches.BaseGapTiles;

        int tierIndex = Math.Clamp(powerSiloSystem.GetTier(building), 0, tiers.Count - 1);
        return PowerSiloCapPatches.BaseGapTiles + tierIndex * PowerSiloCapPatches.RisePerTier;
    }

    /// <summary>
    /// MOD: changed. Evaluate a Silo's currently-animated rise height from its animation state, using an
    /// explicit two-phase overshoot curve over <see cref="RiseDurationSeconds"/>: a smoothstep rise from
    /// 0 up to <c>1 + OvershootAmount</c> during the first <see cref="OvershootPeakFraction"/> of the
    /// duration, then a second smoothstep easing back down from that peak to exactly 1 for the rest of
    /// it. Two independently-tunable phases (rather than a single Penner-style cubic) so both WHEN the
    /// peak happens and HOW FAR it overshoots can be adjusted separately — a single formula's overshoot
    /// is stuck wherever its math happens to put it.
    /// </summary>
    /// <param name="state">The rise animation state to evaluate.</param>
    private static float EvaluateRise((float Start, float Target, float ElapsedSeconds) state)
    {
        float t = Math.Clamp(state.ElapsedSeconds / PowerSiloCapPatches.RiseDurationSeconds, 0f, 1f);
        float peak = 1f + PowerSiloCapPatches.OvershootAmount;

        float eased;
        if (t <= PowerSiloCapPatches.OvershootPeakFraction)
        {
            float localT = t / PowerSiloCapPatches.OvershootPeakFraction;
            float smooth = localT * localT * (3f - 2f * localT);
            eased = smooth * peak;
        }
        else
        {
            float localT = (t - PowerSiloCapPatches.OvershootPeakFraction) / (1f - PowerSiloCapPatches.OvershootPeakFraction);
            float smooth = localT * localT * (3f - 2f * localT);
            eased = MathHelper.Lerp(peak, 1f, smooth);
        }

        return MathHelper.Lerp(state.Start, state.Target, eased);
    }

    /// <summary>
    /// MOD: added. Get how strongly the shake effect should currently apply for a Silo (0 = settled/no
    /// shake, 1 = fully mid-motion) — a triangular curve that's zero at the very start and end of the
    /// ease and peaks in the middle, matching where an ease-in-out curve is actually moving fastest.
    /// </summary>
    /// <param name="building">The Power Silo building.</param>
    private static float GetShakeStrength(Building building)
    {
        if (!PowerSiloCapPatches.RiseState.TryGetValue(building, out (float Start, float Target, float ElapsedSeconds) state))
            return 0f;

        if (Math.Abs(state.Target - state.Start) < 0.001f)
            return 0f; // nothing to actually rise toward (e.g. already settled when the tier changed)

        float t = Math.Clamp(state.ElapsedSeconds / PowerSiloCapPatches.RiseDurationSeconds, 0f, 1f);

        if (t <= 0.5f)
            return 1f;

        return 2f * (1f - t);
        //return 1f - Math.Abs(2f * t - 1f);
    }

    /// <summary>
    /// MOD: added. Set up the whole-building shake (see <see cref="IsShakingCurrentBuilding"/>'s
    /// remarks) before vanilla's own <see cref="Building.draw(SpriteBatch)"/> body runs, so its main
    /// sprite (and any vanilla <c>DrawLayers</c>) shake along with the cap this class draws separately.
    /// </summary>
    /// <param name="__instance">The building about to be drawn.</param>
    private static void Draw_Prefix(Building __instance)
    {
        PowerSiloCapPatches.IsShakingCurrentBuilding = false;

        if (PowerSiloCapPatches.GetSiloBuildingNames is not { } getSiloBuildingNames || !getSiloBuildingNames().Contains(__instance.buildingType.Value))
            return;
        if (__instance.isMoving || __instance.daysOfConstructionLeft.Value > 0)
            return;

        float strength = PowerSiloCapPatches.GetShakeStrength(__instance);
        if (strength <= 0f)
            return;

        float elapsedSeconds = (float)Game1.currentGameTime.TotalGameTime.TotalSeconds;
        float offsetX = (float)Math.Sin(elapsedSeconds * PowerSiloCapPatches.ShakeSpeed) * PowerSiloCapPatches.ShakeAmplitude * strength;
        PowerSiloCapPatches.ActiveShakeOffset = new Vector2(offsetX, 0f);
        PowerSiloCapPatches.IsShakingCurrentBuilding = true;
    }

    /// <summary>MOD: added. Nudge every position <see cref="Game1.GlobalToLocal(xTile.Dimensions.Rectangle,Vector2)"/> converts, while <see cref="IsShakingCurrentBuilding"/> is set — see that field's own remarks for why this is where the whole-building shake actually happens.</summary>
    /// <param name="__result">The converted screen position, mutated in place.</param>
    private static void GlobalToLocal_Postfix(ref Vector2 __result)
    {
        if (PowerSiloCapPatches.IsShakingCurrentBuilding)
            __result += PowerSiloCapPatches.ActiveShakeOffset;
    }

    /// <summary>
    /// Draw the Silo's cap above the ground, using its currently-animated rise height, then clear the
    /// shake flag <see cref="Draw_Prefix"/> set (in a <c>finally</c> block, so it's always cleared even
    /// if something above throws or returns early — this flag is read by a patch on a method used for
    /// EVERY building's draw call, so leaving it set past this one Silo's own draw would shake whatever
    /// gets drawn right after it too). Anchored off the same <c>drawPosition</c> vanilla's own
    /// <c>Building.draw</c> uses for its main sprite (the building's own footprint-bottom point, i.e.
    /// the "ground" here) — the cap's bottom edge sits <c>riseTiles</c> tiles above that point.
    ///
    /// MOD: added. Also tinted by <c>___alpha</c> — <see cref="Building"/>'s own protected fade-when-
    /// player-is-behind opacity, accessed here via Harmony's <c>___fieldName</c> injection (no reflection
    /// needed, since this patch method already targets a method on <see cref="Building"/> itself).
    /// Vanilla's own main sprite AND any <c>DrawLayers</c> both multiply by this exact same field (see
    /// <see cref="Building.draw(SpriteBatch)"/>'s own body) — that's why a multi-part vanilla building
    /// like the Mill fades as one coherent object instead of its pieces fading independently, and doing
    /// the same here gets the cap the identical behavior for free.
    /// </summary>
    /// <param name="__instance">The building being drawn.</param>
    /// <param name="b">The sprite batch being drawn to.</param>
    /// <param name="___alpha">The building's own current fade-when-behind opacity.</param>
    private static void Draw_Postfix(Building __instance, SpriteBatch b, float ___alpha)
    {
        try
        {
            if (PowerSiloCapPatches.GetSiloBuildingNames is not { } getSiloBuildingNames || !getSiloBuildingNames().Contains(__instance.buildingType.Value))
                return;

            // MOD: matches Building.draw's own early-returns (isMoving/under construction) — nothing
            // else was drawn for it this frame either, so the cap shouldn't be drawn on its own.
            if (__instance.isMoving || __instance.daysOfConstructionLeft.Value > 0)
                return;

            float riseTiles = PowerSiloCapPatches.RiseState.TryGetValue(__instance, out (float Start, float Target, float ElapsedSeconds) state)
                ? PowerSiloCapPatches.EvaluateRise(state)
                : PowerSiloCapPatches.BaseGapTiles;

            Texture2D capTexture = Game1.content.Load<Texture2D>(PowerSiloCapPatches.CapAssetName);
            Vector2 drawPosition = new(__instance.tileX.Value * 64f, (__instance.tileY.Value + __instance.tilesHigh.Value) * 64f);
            Vector2 capPosition = drawPosition + new Vector2(0f, -riseTiles * 64f - capTexture.Height * 4f);

            float sortY = ((__instance.tileY.Value + __instance.tilesHigh.Value) * 64f) / 10000f + 0.0002f; // a hair after the body's own sortY (and any vanilla DrawLayers), so the cap always draws on top of both

            b.Draw(capTexture, Game1.GlobalToLocal(Game1.viewport, capPosition), new Rectangle(0, 0, capTexture.Width, capTexture.Height), __instance.color * ___alpha, 0f, Vector2.Zero, 4f, SpriteEffects.None, sortY);
        }
        finally
        {
            PowerSiloCapPatches.IsShakingCurrentBuilding = false;
        }
    }

    /// <summary>
    /// MOD: added. Draw the Silo's cap alongside <see cref="Building.drawInMenu(SpriteBatch, int, int)"/>'s
    /// own body — the carpenter menu's blueprint icon/held-cursor preview, in already-converted screen
    /// pixel space (unlike <see cref="Draw_Postfix"/>'s world-space math). Always at the resting tier-0
    /// gap, since there's no real placed building (or tier) behind a blueprint preview to animate toward.
    /// </summary>
    /// <param name="__instance">The building whose preview is being drawn.</param>
    /// <param name="b">The sprite batch being drawn to.</param>
    /// <param name="x">The screen X position the body was drawn at.</param>
    /// <param name="y">The screen Y position the body was drawn at.</param>
    private static void DrawInMenu_Postfix(Building __instance, SpriteBatch b, int x, int y)
    {
        if (PowerSiloCapPatches.GetSiloBuildingNames is not { } getSiloBuildingNames || !getSiloBuildingNames().Contains(__instance.buildingType.Value))
            return;

        Texture2D capTexture = Game1.content.Load<Texture2D>(PowerSiloCapPatches.CapAssetName);

        // MOD: the body's own art bottom edge is its "ground" line, matching Draw_Postfix's own
        // world-space ground anchor — see this method's own remarks.
        float groundY = y + __instance.getSourceRect().Height * 4f;
        float capY = groundY - PowerSiloCapPatches.BaseGapTiles * 64f - capTexture.Height * 4f;

        b.Draw(capTexture, new Vector2(x, capY), new Rectangle(0, 0, capTexture.Width, capTexture.Height), __instance.color, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
    }
}

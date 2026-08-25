using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Extensions;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patches for the custom Cranked Power Coil craftable — a cheap, early-game
/// power source that covers the same square area a regular Power Coil does (see
/// <see cref="PowerSystem"/>), but is always placed unpowered and must be manually cranked to become
/// powered for the rest of the day, resetting back to unpowered every morning (see
/// <see cref="ResetDaily"/>). Unlike the regular Power Coil (<see cref="PowerCoilPatches"/>), this
/// class deliberately never touches <see cref="PowerSiloSystem"/> at all — a Cranked Power Coil is
/// never counted toward the Power Grid's capacity, and never participates in the Solar Panel
/// connectivity mechanic either (see <see cref="PowerSiloPatches"/>'s own Solar Panel handling, which
/// this class is never wired into).
///
/// "Cranking" is the vanilla fishing minigame (<see cref="BobberBar"/>), reused wholesale — see
/// <see cref="TryStartCrankMinigame"/> for how it's opened and decoupled from the player's real
/// fishing progress, and <see cref="CheckMinigameCompletion"/> for how its outcome resolves into a
/// successful (or failed) crank. The actual "what happens on a successful crank" logic lives in
/// <see cref="OnCrankSucceeded"/>, kept deliberately separate from the minigame plumbing so any other
/// future crank trigger could call it directly without touching anything else here.
/// </summary>
internal static class CrankedPowerCoilPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The qualified item ID of the object these patches apply to.</summary>
    internal const string TargetQualifiedItemId = "(BC)luisMint.PoweredAutomation_CrankedPowerCoil";

    /// <summary>The asset name of the static powered sprite (16x32).</summary>
    private const string PoweredAssetName = "Mods/luisMint.PoweredAutomation/CrankedPowerCoil";

    /// <summary>The asset name of the static unpowered sprite (16x32).</summary>
    private const string UnpoweredAssetName = "Mods/luisMint.PoweredAutomation/CrankedPowerCoil_UnPowered";

    /// <summary>The asset name of the cranking animation strip (6 frames, 16x32 each, horizontal) — looped continuously for as long as the crank minigame (see <see cref="TryStartCrankMinigame"/>) is open, rather than played once.</summary>
    private const string CrankingAssetName = "Mods/luisMint.PoweredAutomation/CrankedPowerCoil_Cranking";

    /// <summary>
    /// MOD: added. The asset name of the crank minigame's own themed replacement for vanilla's
    /// fishing-pole/track backdrop sprite (<see cref="BobberBarDraw_Prefix"/>'s second background draw
    /// call) — same 38x150 dimensions as the vanilla sprite it replaces, so it drops in using the exact
    /// same position/origin/scale math with no further changes needed.
    /// </summary>
    private const string CrankingUiAssetName = "Mods/luisMint.PoweredAutomation/CrankingUi";

    /// <summary>
    /// MOD: added. The asset name of the crank minigame's own themed replacement for vanilla's moving
    /// "fish" icon (<see cref="BobberBarDraw_Prefix"/>'s bobber-position indicator draw call) — same
    /// 20x20 dimensions as the vanilla sprite it replaces, so it drops in at the same drawn size with no
    /// further changes needed (drawn off its own actual size rather than a hardcoded 20x20, so it's
    /// never stretched/cropped if that ever changes).
    /// </summary>
    private const string CrankingTargetAssetName = "Mods/luisMint.PoweredAutomation/CrankingTarget";

    /// <summary>The pixel width of a single frame in <see cref="CrankingAssetName"/>.</summary>
    private const int FrameWidth = 16;

    /// <summary>The pixel height of a single frame in <see cref="CrankingAssetName"/>.</summary>
    private const int FrameHeight = 32;

    /// <summary>The number of frames in <see cref="CrankingAssetName"/>.</summary>
    private const int CrankFrameCount = 6;

    /// <summary>How long each frame of the looping crank animation is held, in real milliseconds.</summary>
    private const double CrankFrameDurationMs = 83;

    /// <summary>MOD: added. The fixed catching-bar-height baseline (before doubling — see <see cref="TryStartCrankMinigame"/>) the crank minigame always uses, matching <see cref="BobberBar"/>'s own vanilla formula at Fishing skill level 0 — used instead of the player's real, current Fishing level, so leveling up real fishing never quietly changes the crank's own difficulty.</summary>
    private const int BaseBobberBarHeight = 96;

    /// <summary>
    /// MOD: added. Whether a Cranked Power Coil is currently cranked/powered — deliberately the
    /// INVERTED polarity from <see cref="PowerSiloSystem.CoilPoweredModDataKey"/> (missing = unpowered
    /// here, vs. missing = powered there), since a Cranked Power Coil must always start out unpowered
    /// the instant it's placed, unlike a regular Power Coil which starts out powered by default.
    /// </summary>
    internal const string PoweredModDataKey = "luisMint.PoweredAutomation/CrankedPowerCoilPowered";

    /// <summary>
    /// MOD: added. The <see cref="Farmer.UniqueMultiplayerID"/> of whoever currently has the crank
    /// minigame open for a Cranked Power Coil, if any — a real networked <see cref="SObject.modData"/>
    /// key (unlike <see cref="ActiveMinigame"/>, which is purely local per-client), so every connected
    /// player sees the SAME lock regardless of which client actually opened it. Stamped by
    /// <see cref="TryStartCrankMinigame"/>, checked by <see cref="CheckForAction_Prefix"/> (via
    /// <see cref="IsBeingCrankedByAnother"/>) so the host and a farmhand can each crank a DIFFERENT
    /// coil at the same time, but never the SAME one simultaneously. Cleared by
    /// <see cref="CheckMinigameCompletion"/> once that player's own minigame resolves, or by
    /// <see cref="ReleaseLocksHeldBy"/> if that player disconnects while still holding it (see that
    /// method's own remarks for why that safety net is needed).
    /// </summary>
    private const string CrankingByModDataKey = "luisMint.PoweredAutomation/CrankedPowerCoilCrankingBy";

    /// <summary>How far the sprite grows/shrinks at the peak of the pulse, as a fraction of its normal size — matches <see cref="PowerCoilPatches"/>'s own amplitude.</summary>
    private const float PulseAmplitude = 0.05f;

    /// <summary>How fast the pulse cycles, in radians per second — deliberately a different value than <see cref="PowerCoilPatches"/>'s (3f) and <see cref="PoweredChestPatches"/>'s (3.5f) own pulse speeds, so this coil's pulse doesn't stay in visual sync with either.</summary>
    private const float PulseSpeed = 2f;

    /// <summary>A fixed phase offset for the pulse, purely so it starts out of sync with the other two objects' own pulses.</summary>
    private const float PulsePhaseOffset = MathF.PI / 2f;

    /// <summary>MOD: added. How far the sprite shifts left/right at the peak of the crank shake (see <see cref="GetCrankShakeOffsetX"/>), in already-4x-scaled screen pixels.</summary>
    private const float CrankShakeAmplitude = 0.8f;

    /// <summary>MOD: added. How fast the crank shake cycles, in radians per second, while idle (not currently landing the target) — also what every OTHER connected player always sees, regardless of how the actual cranking player is doing (see <see cref="GetCrankShakeSpeed"/>'s own remarks on why that's not networked).</summary>
    private const float CrankShakeSpeedIdle = 6f;

    /// <summary>MOD: added. How fast the crank shake cycles while the cranking player's own bobber is currently landing the target — visible only to that same player.</summary>
    private const float CrankShakeSpeedSuccess = 14f;

    /// <summary>
    /// MOD: added. The currently-open crank minigame, or <c>null</c> if none is open on THIS client
    /// right now — purely local, per-client state (never networked), matching how the vanilla fishing
    /// minigame it's built on is itself entirely client-local. Set by <see cref="TryStartCrankMinigame"/>,
    /// cleared by <see cref="CheckMinigameCompletion"/> once the underlying <see cref="BobberBar"/> closes.
    /// </summary>
    private static (SObject Coil, Farmer Who, BobberBar Bar)? ActiveMinigame;

    /// <summary>MOD: added. Cached resolution of Catfish's own <c>Data/Fish</c> item ID (see <see cref="ResolveCatfishFishId"/>) — computed once and reused, since that asset practically never changes mid-session.</summary>
    private static string? CachedCatfishFishId;

    /// <summary>MOD: added. Reflected access to <see cref="BobberBar"/>'s own private <c>sparkleText</c> field, needed only by <see cref="BobberBarDraw_Prefix"/>'s own re-implementation of <see cref="BobberBar.draw"/> — see that method's own remarks for why a custom draw is needed at all.</summary>
    private static readonly FieldInfo SparkleTextField = AccessTools.Field(typeof(BobberBar), "sparkleText");

    /// <summary>
    /// MOD: added. Queue specific locations for a machine reload, set via <see cref="Initialize"/> —
    /// called after a successful crank (see <see cref="OnCrankSucceeded"/>), since nothing else would
    /// otherwise notice the newly-powered tiles mid-day (the next rescan would only happen whenever
    /// something unrelated happens to trigger one).
    /// </summary>
    private static Action<IEnumerable<GameLocation>>? RequeueLocations;


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the accessors this class needs. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="requeueLocations">Queue the given locations for a machine reload — see <see cref="RequeueLocations"/>'s own remarks.</param>
    public static void Initialize(Action<IEnumerable<GameLocation>> requeueLocations)
    {
        CrankedPowerCoilPatches.RequeueLocations = requeueLocations;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.getScale)),
            postfix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(GetScale_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.initializeLightSource)),
            postfix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(InitializeLightSource_Postfix))
        );

        // MOD: a second, independent postfix on the same method PowerCoilPatches/PoweredChestPatches
        // already patch — Harmony allows multiple unrelated postfixes on one original method, gated on
        // each class's own QualifiedItemId check, matching PoweredChestPatches' own established
        // precedent for patching the same vanilla methods a second time for a different object type.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.updateWhenCurrentLocation)),
            postfix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(UpdateWhenCurrentLocation_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.draw), [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]),
            prefix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(Draw_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.checkForAction)),
            prefix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(CheckForAction_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.placementAction), [typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer)]),
            prefix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(PlacementAction_Prefix)),
            postfix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(PlacementAction_Postfix))
        );

        // MOD: added — see PullFishFromWater_Prefix/DoneFishing_Prefix's own remarks for why the crank
        // minigame (built on the real BobberBar) needs these to keep it from touching whatever REAL
        // fishing rod the player happens to have equipped while cranking.
        harmony.Patch(
            original: AccessTools.Method(typeof(FishingRod), nameof(FishingRod.pullFishFromWater)),
            prefix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(PullFishFromWater_Prefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(FishingRod), nameof(FishingRod.doneFishing)),
            prefix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(DoneFishing_Prefix))
        );

        // MOD: added — see BobberBarDraw_Prefix's own remarks for why the crank minigame needs its own
        // copy of BobberBar's draw call at all (suppressing vanilla's own "first time fishing" tutorial
        // icon, which otherwise shows up every time for a player who's never caught a real fish).
        harmony.Patch(
            original: AccessTools.Method(typeof(BobberBar), nameof(BobberBar.draw), [typeof(SpriteBatch)]),
            prefix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(BobberBarDraw_Prefix))
        );
    }

    /// <summary>Get whether a Cranked Power Coil is currently cranked/powered — see <see cref="PoweredModDataKey"/>'s own remarks on its inverted polarity.</summary>
    /// <param name="obj">The object to check.</param>
    internal static bool IsPowered(SObject obj)
    {
        return obj.modData.TryGetValue(CrankedPowerCoilPatches.PoweredModDataKey, out string? raw) && raw == "true";
    }

    /// <summary>
    /// MOD: added. The "what happens on a successful crank" boundary — deliberately separate from the
    /// minigame plumbing above, so any other future crank trigger could call this same method directly
    /// on its own success, without touching anything else in this class.
    /// </summary>
    /// <param name="coil">The Cranked Power Coil being cranked.</param>
    /// <param name="who">The player who cranked it.</param>
    internal static void OnCrankSucceeded(SObject coil, Farmer who)
    {
        coil.modData[CrankedPowerCoilPatches.PoweredModDataKey] = "true";

        // MOD: added — mirrors this coil's own powered state onto the real vanilla IsOn field, same as
        // PowerCoilPatches/PowerSiloSystem already do for the regular Power Coil, so
        // UtilityGridReduxSystem's own MustBeOn-gated registration sees the correct state.
        coil.IsOn = true;

        // MOD: added — the same "grunt" cue a regular Power Coil plays when it ends up powered (see
        // PowerSiloPatches.PlacementAction_Postfix), on top of whatever catch feedback the crank
        // minigame itself already gave (a "jingle1" cue, and a "Perfect!" sparkle on a great crank) —
        // this one specifically confirms the COIL itself is now actually powered, not just that the
        // minigame was won.
        coil.Location?.playSound("grunt");

        // MOD: added — a genuinely necessary fix: nothing else would pick up the newly-powered tiles
        // mid-day otherwise (see this class's own remarks). Deliberately does NOT touch Power Silo's
        // solar-panel-connectivity cache — only the regular Power Coil participates in that mechanic.
        if (coil.Location != null)
            CrankedPowerCoilPatches.RequeueLocations?.Invoke([coil.Location]);
    }

    /// <summary>
    /// MOD: added. Poll for the crank minigame closing — called once per tick from
    /// <c>ModEntry.OnUpdateTicked</c>, for every client (not just the host), same as this mod's other
    /// purely-cosmetic per-tick effects (e.g. <see cref="PowerCoilAmbientEffect"/>). A closed
    /// <see cref="BobberBar"/> is detected by reference — <see cref="Game1.activeClickableMenu"/> no
    /// longer being the SAME instance <see cref="TryStartCrankMinigame"/> opened — which reliably
    /// catches every way it can end (a real catch, the fish escaping, or the player pressing Escape
    /// mid-crank, which <see cref="BobberBar.emergencyShutDown"/> itself already routes through the
    /// exact same close path as a normal escape/catch). <see cref="BobberBar.distanceFromCatching"/> is
    /// a public field already left in its final, settled state by the time the menu has actually
    /// closed, so reading it here (rather than trying to intercept BobberBar's own internal close logic)
    /// is reliable.
    /// </summary>
    internal static void CheckMinigameCompletion()
    {
        if (CrankedPowerCoilPatches.ActiveMinigame is not { } minigame || Game1.activeClickableMenu == minigame.Bar)
            return;

        CrankedPowerCoilPatches.ActiveMinigame = null;

        // MOD: added — release this player's own crank lock — see CrankingByModDataKey's own remarks.
        if (minigame.Coil.modData.TryGetValue(CrankedPowerCoilPatches.CrankingByModDataKey, out string? crankingByRaw) && long.TryParse(crankingByRaw, out long crankingById) && crankingById == minigame.Who.UniqueMultiplayerID)
            minigame.Coil.modData.Remove(CrankedPowerCoilPatches.CrankingByModDataKey);

        if (minigame.Bar.distanceFromCatching >= 1f && minigame.Coil.Location != null && minigame.Coil.QualifiedItemId == CrankedPowerCoilPatches.TargetQualifiedItemId)
            CrankedPowerCoilPatches.OnCrankSucceeded(minigame.Coil, minigame.Who);
    }

    /// <summary>
    /// MOD: added. Release any crank lock (see <see cref="CrankingByModDataKey"/>) held by a specific
    /// player, across every given location — a safety net for a player disconnecting while their own
    /// crank minigame is still open. Without this, that coil's lock would never clear on its own (only
    /// <see cref="CheckMinigameCompletion"/> ever clears it, which can only run on that SAME player's
    /// own client, once they're gone), permanently blocking that one coil from ever being cranked by
    /// anyone again. Called from <c>ModEntry</c>'s own <c>PeerDisconnected</c> handler.
    /// </summary>
    /// <param name="locations">The locations to scan.</param>
    /// <param name="uniqueMultiplayerId">The disconnected player's own <see cref="Farmer.UniqueMultiplayerID"/>.</param>
    internal static void ReleaseLocksHeldBy(IEnumerable<GameLocation> locations, long uniqueMultiplayerId)
    {
        foreach (GameLocation location in locations)
        {
            foreach (SObject obj in location.objects.Values)
            {
                if (obj.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId)
                    continue;

                if (obj.modData.TryGetValue(CrankedPowerCoilPatches.CrankingByModDataKey, out string? raw) && long.TryParse(raw, out long crankingById) && crankingById == uniqueMultiplayerId)
                    obj.modData.Remove(CrankedPowerCoilPatches.CrankingByModDataKey);
            }
        }
    }

    /// <summary>
    /// MOD: added. Clear every Cranked Power Coil back to unpowered across the given locations —
    /// called once per new day (see <c>ModEntry.OnDayStarted</c>). No explicit requeue needed here:
    /// <c>MachineManager.Reset()</c> already unconditionally queues every location for reload as part
    /// of the same day-start handling.
    /// </summary>
    /// <param name="locations">The locations to scan.</param>
    internal static void ResetDaily(IEnumerable<GameLocation> locations)
    {
        foreach (GameLocation location in locations)
        {
            foreach (SObject obj in location.objects.Values)
            {
                if (obj.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId)
                    continue;

                obj.modData.Remove(CrankedPowerCoilPatches.PoweredModDataKey);
                obj.IsOn = false; // MOD: added — keeps Utility Grid Redux's own view in sync too

                // MOD: added — an unconditional daily backstop for CrankingByModDataKey, on top of
                // CheckMinigameCompletion/ReleaseLocksHeldBy's own triggers — cheap reassurance against
                // a lock somehow surviving an edge case neither of those catch (e.g. the game itself
                // crashing mid-crank), so a coil can never stay permanently un-crankable.
                obj.modData.Remove(CrankedPowerCoilPatches.CrankingByModDataKey);
            }
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// MOD: added. Open the vanilla fishing minigame (<see cref="BobberBar"/>) as the "crank" — reusing
    /// its own Catfish-difficulty movement/timing wholesale (per request) rather than reimplementing a
    /// bespoke minigame. A no-op if a menu is already open (including an already-open crank minigame) —
    /// the cross-player "is someone ELSE already cranking this exact coil" check happens earlier, in
    /// <see cref="CheckForAction_Prefix"/> (see <see cref="IsBeingCrankedByAnother"/>), so this only
    /// ever needs to guard against THIS client's own local menu state. While this minigame is open,
    /// <see cref="Draw_Prefix"/>/<see cref="GetScale_Postfix"/> play the cranking animation strip on a
    /// continuous loop (see <see cref="IsBeingCranked"/>) instead of the static unpowered
    /// sprite, and <see cref="PullFishFromWater_Prefix"/>/<see cref="DoneFishing_Prefix"/> suppress the
    /// real <see cref="FishingRod"/> reward/consumption logic <see cref="BobberBar"/> would otherwise
    /// try to run against whatever fishing rod <paramref name="who"/> happens to have equipped right
    /// now — entirely unrelated to this interaction.
    /// </summary>
    /// <param name="coil">The Cranked Power Coil being cranked.</param>
    /// <param name="who">The player cranking it.</param>
    private static void TryStartCrankMinigame(SObject coil, Farmer who)
    {
        if (Game1.activeClickableMenu != null)
            return;

        BobberBar bar = new(
            whichFish: CrankedPowerCoilPatches.ResolveCatfishFishId(),
            fishSize: (float)Game1.random.NextDouble(),
            treasure: false,
            bobbers: new List<string>(),
            setFlagOnCatch: "",
            isBossFish: false
        );

        // MOD: added — sets the catching bar's own height from a FIXED baseline (BobberBar's own
        // vanilla formula: 96 + FishingLevel * 8, here always treated as FishingLevel 0) rather than
        // the constructor's own real-FishingLevel-dependent result, then doubles it, per request. Fixing
        // the baseline decouples the crank's own difficulty from the player's real Fishing skill — since
        // bobberBarHeight is the SAME field BobberBar's own unpatched update() reads for both the bar's
        // real hit-box (whether the target is considered "in the bar") and its visual size
        // (BobberBarDraw_Prefix's own three bar draw calls), overwriting it here, on this one local
        // instance only, changes both together with nothing else needed. Re-derives bobberBarPos with
        // the exact same formula the constructor itself uses (568 - bobberBarHeight), so the bar doesn't
        // start clipped off the bottom of the track for a single frame before update()'s own clamping
        // would otherwise self-correct it.
        bar.bobberBarHeight = CrankedPowerCoilPatches.BaseBobberBarHeight * 2;
        bar.bobberBarPos = 568 - bar.bobberBarHeight;

        // MOD: added — forces the same lenient starting "distance from catching" BobberBar's own
        // constructor otherwise only gives a player who has NEVER caught a real fish
        // (Game1.player.fishCaught.Length == 0), so it doesn't quietly change the moment the player
        // catches their first real fish anywhere on the farm.
        bar.distanceFromCatching = 0.1f;

        // MOD: added — the REAL "can't actually be lost" tutorial behavior turned out to live somewhere
        // else entirely: BobberBar's own unpatched update() only ever DECREASES distanceFromCatching
        // (i.e. only ever lets missing the target actually cost progress) once
        // Game1.player.fishCaught.Length != 0 — checked fresh every tick, not just at construction, so
        // the distanceFromCatching override above alone doesn't stop it from becoming losable again
        // after the player's first real catch. That per-tick decrease is also multiplied by this exact
        // field (distanceFromCatchPenaltyModifier — vanilla itself only ever sets it to 0.5 for the
        // "Blessing of Waters" buff), so reusing it here to slow the decay way down is the smallest
        // change that reaches into that same gate without reimplementing update() wholesale.
        bar.distanceFromCatchPenaltyModifier = 0.15f;

        // MOD: added — a real, networked lock (see CrankingByModDataKey's own remarks) so another
        // connected player can't ALSO start cranking this exact coil while this player's own minigame
        // is still open, even though each player's own BobberBar is otherwise entirely client-local.
        coil.modData[CrankedPowerCoilPatches.CrankingByModDataKey] = who.UniqueMultiplayerID.ToString();

        Game1.activeClickableMenu = bar;
        CrankedPowerCoilPatches.ActiveMinigame = (coil, who, bar);
    }

    /// <summary>MOD: added. Get whether a DIFFERENT player currently has the crank minigame open for this exact coil — see <see cref="CrankingByModDataKey"/>'s own remarks.</summary>
    /// <param name="coil">The coil to check.</param>
    /// <param name="who">The player who's about to try cranking it.</param>
    private static bool IsBeingCrankedByAnother(SObject coil, Farmer who)
    {
        return coil.modData.TryGetValue(CrankedPowerCoilPatches.CrankingByModDataKey, out string? raw)
            && long.TryParse(raw, out long crankingById)
            && crankingById != who.UniqueMultiplayerID;
    }

    /// <summary>
    /// MOD: added. Resolve Catfish's own <c>Data/Fish</c> item ID, so the crank minigame can pass a
    /// genuinely valid <c>whichFish</c> into <see cref="BobberBar"/>'s constructor and inherit its real
    /// difficulty/motion data — matching an actual Catfish's own movement, per request. Tries the ID
    /// Catfish has used since the game's original release first (fast path); falls back to a name-based
    /// search of the live <c>Data/Fish</c> asset in case some content pack ever remapped it. Cached
    /// after the first resolution, since that asset practically never changes mid-session.
    /// </summary>
    private static string ResolveCatfishFishId()
    {
        if (CrankedPowerCoilPatches.CachedCatfishFishId != null)
            return CrankedPowerCoilPatches.CachedCatfishFishId;

        const string knownVanillaId = "143"; // Catfish's own stable vanilla item ID
        Dictionary<string, string> fishData = DataLoader.Fish(Game1.content);

        string resolvedId = knownVanillaId;
        if (!fishData.ContainsKey(knownVanillaId))
        {
            foreach ((string id, string raw) in fishData)
            {
                string[] fields = raw.Split('/');
                if (fields.Length > 0 && fields[0].Equals("Catfish", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedId = id;
                    break;
                }
            }
        }

        return CrankedPowerCoilPatches.CachedCatfishFishId = resolvedId;
    }

    /// <summary>
    /// MOD: added. Skip <see cref="FishingRod.pullFishFromWater"/> entirely while the crank minigame
    /// owns the active <see cref="BobberBar"/> — that method is what actually hands the player a real
    /// fish/XP/achievement progress, and <see cref="BobberBar"/>'s own close logic calls it against
    /// whatever <see cref="FishingRod"/> the player happens to have EQUIPPED right now (via
    /// <c>Game1.player.CurrentTool</c>), completely unrelated to this crank interaction — without this,
    /// a player who just happened to be holding a real fishing rod while cranking a coil would be handed
    /// a real Catfish and its rewards for free. Since only one menu can be open at a time, and this
    /// class's own <see cref="BobberBar"/> occupies that slot for the mechanic's entire duration, there's
    /// no legitimate real-fishing call this could ever incorrectly suppress.
    /// </summary>
    /// <returns>Returns <c>false</c> to skip the original method while cranking, or <c>true</c> to let it run normally.</returns>
    private static bool PullFishFromWater_Prefix()
    {
        return CrankedPowerCoilPatches.ActiveMinigame == null;
    }

    /// <summary>Skip <see cref="FishingRod.doneFishing"/> for the same reason as <see cref="PullFishFromWater_Prefix"/> — it consumes the equipped rod's real bait/tackle and resets its casting state, neither of which should ever happen for a rod that was never actually fishing.</summary>
    /// <returns>Returns <c>false</c> to skip the original method while cranking, or <c>true</c> to let it run normally.</returns>
    private static bool DoneFishing_Prefix()
    {
        return CrankedPowerCoilPatches.ActiveMinigame == null;
    }

    /// <summary>
    /// MOD: added. Fully replace <see cref="BobberBar.draw"/> for OUR OWN tracked crank minigame
    /// instance only (every other <see cref="BobberBar"/> — i.e. real fishing — still draws normally),
    /// to suppress vanilla's own "first time fishing" tutorial icon (the mouse-click/controller-button
    /// prompt drawn beside the bar). That icon is gated purely on <c>Game1.player.fishCaught.Length == 0</c>
    /// — the SAME flag <see cref="BobberBar"/>'s own constructor already uses to make the bar more
    /// forgiving for a player who's never caught a real fish (<c>distanceFromCatching</c> starts at 0.1
    /// instead of 0.3) — which is deliberately left untouched here (it's a welcome side effect for the
    /// crank minigame), so there's no flag to flip without ALSO disabling that easier difficulty. The
    /// icon itself has no separate toggle, so the only way to drop just it is a full re-implementation
    /// of this draw call, identical to vanilla's own except for that one block at the very end.
    ///
    /// Reflects into <see cref="BobberBar"/>'s own private <c>sparkleText</c> field (see
    /// <see cref="SparkleTextField"/>) since every other field this needs is already public. Omits the
    /// "SonarBobber"/"ChallengeBait" tackle branches entirely (rather than leaving dead stubs) — this
    /// mod's own crank minigame never gives <see cref="BobberBar"/> any bobbers or challenge bait (see
    /// <see cref="TryStartCrankMinigame"/>), so neither branch could ever apply here anyway.
    /// </summary>
    /// <param name="__instance">The bobber bar being drawn.</param>
    /// <param name="b">The sprite batch being drawn to.</param>
    /// <returns>Returns <c>false</c> to skip the original method for our own tracked crank minigame, or <c>true</c> to let every other <see cref="BobberBar"/> (real fishing) draw normally.</returns>
    private static bool BobberBarDraw_Prefix(BobberBar __instance, SpriteBatch b)
    {
        if (CrankedPowerCoilPatches.ActiveMinigame is not { } minigame || !ReferenceEquals(minigame.Bar, __instance))
            return true;

        Game1.StartWorldDrawInUI(b);

        b.Draw(Game1.mouseCursors, new Vector2(__instance.xPositionOnScreen - (__instance.flipBubble ? 44 : 20) + 104, __instance.yPositionOnScreen - 16 + 314) + __instance.everythingShake, new Rectangle(652, 1685, 52, 157), Color.White * 0.6f * __instance.scale, 0f, new Vector2(26f, 78.5f) * __instance.scale, 4f * __instance.scale, __instance.flipBubble ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0.001f);

        // MOD: added — the crank minigame's own themed track/pole backdrop, drawn in place of vanilla's
        // fishing-pole sprite — see CrankingUiAssetName's own remarks for why this is a drop-in swap.
        Texture2D crankingUiTexture = Game1.content.Load<Texture2D>(CrankedPowerCoilPatches.CrankingUiAssetName);
        b.Draw(crankingUiTexture, new Vector2(__instance.xPositionOnScreen + 70, __instance.yPositionOnScreen + 296) + __instance.everythingShake, new Rectangle(0, 0, crankingUiTexture.Width, crankingUiTexture.Height), Color.White * __instance.scale, 0f, new Vector2(crankingUiTexture.Width / 2f, crankingUiTexture.Height / 2f) * __instance.scale, 4f * __instance.scale, SpriteEffects.None, 0.01f);

        if (__instance.scale == 1f)
        {
            b.Draw(Game1.mouseCursors, new Vector2(__instance.xPositionOnScreen + 64, __instance.yPositionOnScreen + 12 + (int)__instance.bobberBarPos) + __instance.barShake + __instance.everythingShake, new Rectangle(682, 2078, 9, 2), __instance.bobberInBar ? Color.White : (Color.White * 0.25f * ((float)Math.Round(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 100.0), 2) + 2f)), 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.89f);
            b.Draw(Game1.mouseCursors, new Vector2(__instance.xPositionOnScreen + 64, __instance.yPositionOnScreen + 12 + (int)__instance.bobberBarPos + 8) + __instance.barShake + __instance.everythingShake, new Rectangle(682, 2081, 9, 1), __instance.bobberInBar ? Color.White : (Color.White * 0.25f * ((float)Math.Round(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 100.0), 2) + 2f)), 0f, Vector2.Zero, new Vector2(4f, __instance.bobberBarHeight - 16), SpriteEffects.None, 0.89f);
            b.Draw(Game1.mouseCursors, new Vector2(__instance.xPositionOnScreen + 64, __instance.yPositionOnScreen + 12 + (int)__instance.bobberBarPos + __instance.bobberBarHeight - 8) + __instance.barShake + __instance.everythingShake, new Rectangle(682, 2085, 9, 2), __instance.bobberInBar ? Color.White : (Color.White * 0.25f * ((float)Math.Round(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 100.0), 2) + 2f)), 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.89f);
            b.Draw(Game1.staminaRect, new Rectangle(__instance.xPositionOnScreen + 124, __instance.yPositionOnScreen + 4 + (int)(580f * (1f - __instance.distanceFromCatching)), 16, (int)(580f * __instance.distanceFromCatching)), Utility.getRedToGreenLerpColor(__instance.distanceFromCatching));
            b.Draw(Game1.mouseCursors, new Vector2(__instance.xPositionOnScreen + 18, __instance.yPositionOnScreen + 514) + __instance.everythingShake, new Rectangle(257, 1990, 5, 10), Color.White, __instance.reelRotation, new Vector2(2f, 10f), 4f, SpriteEffects.None, 0.9f);

            if (__instance.goldenTreasure)
                b.Draw(Game1.mouseCursors_1_6, new Vector2(__instance.xPositionOnScreen + 64 + 18, (float)(__instance.yPositionOnScreen + 12 + 24) + __instance.treasurePosition) + __instance.treasureShake + __instance.everythingShake, new Rectangle(256, 51, 20, 24), Color.White, 0f, new Vector2(10f, 10f), 2f * __instance.treasureScale, SpriteEffects.None, 0.85f);
            else
                b.Draw(Game1.mouseCursors, new Vector2(__instance.xPositionOnScreen + 64 + 18, (float)(__instance.yPositionOnScreen + 12 + 24) + __instance.treasurePosition) + __instance.treasureShake + __instance.everythingShake, new Rectangle(638, 1865, 20, 24), Color.White, 0f, new Vector2(10f, 10f), 2f * __instance.treasureScale, SpriteEffects.None, 0.85f);

            if (__instance.treasureCatchLevel > 0f && !__instance.treasureCaught)
            {
                b.Draw(Game1.staminaRect, new Rectangle(__instance.xPositionOnScreen + 64, __instance.yPositionOnScreen + 12 + (int)__instance.treasurePosition, 40, 8), Color.DimGray * 0.5f);
                b.Draw(Game1.staminaRect, new Rectangle(__instance.xPositionOnScreen + 64, __instance.yPositionOnScreen + 12 + (int)__instance.treasurePosition, (int)(__instance.treasureCatchLevel * 40f), 8), Color.Orange);
            }

            // MOD: added — the crank minigame's own themed target icon, drawn in place of vanilla's
            // moving fish sprite — see CrankingTargetAssetName's own remarks for why this is a drop-in
            // swap. bossFish is never true for this minigame (see TryStartCrankMinigame), so there's no
            // second variant to offset into like vanilla's own sprite sheet has.
            Texture2D crankingTargetTexture = Game1.content.Load<Texture2D>(CrankedPowerCoilPatches.CrankingTargetAssetName);
            b.Draw(crankingTargetTexture, new Vector2(__instance.xPositionOnScreen + 64 + 18, (float)(__instance.yPositionOnScreen + 12 + 24) + __instance.bobberPosition) + __instance.fishShake + __instance.everythingShake, new Rectangle(0, 0, crankingTargetTexture.Width, crankingTargetTexture.Height), Color.White, 0f, new Vector2(crankingTargetTexture.Width / 2f, crankingTargetTexture.Height / 2f), 2f, SpriteEffects.None, 0.88f);

            (CrankedPowerCoilPatches.SparkleTextField.GetValue(__instance) as SparklingText)?.draw(b, new Vector2(__instance.xPositionOnScreen - 16, __instance.yPositionOnScreen - 64));
        }

        // MOD: deliberately omits vanilla's own "first time fishing" tutorial icon block here — see
        // this method's own remarks for why.

        Game1.EndWorldDrawInUI(b);
        return false;
    }

    /// <summary>
    /// MOD: added. Get whether the given coil currently has ANY player's crank minigame open for it —
    /// used by <see cref="Draw_Prefix"/>/<see cref="GetScale_Postfix"/> to play the looping cranking
    /// animation. Deliberately reads <see cref="CrankingByModDataKey"/> directly (a real, networked
    /// modData flag) rather than this class's own local <see cref="ActiveMinigame"/> field, so every
    /// connected player sees the animation while ANOTHER player is cranking — not just the one actually
    /// running the minigame. Per request: this doesn't need to be frame-synced across clients (matching
    /// <see cref="AutoCrafterPatches"/>' own local-anchor precedent) — each client just loops
    /// <see cref="GetLoopingCrankFrameIndex"/> off its own local clock, so it's simply visible, not
    /// synchronized to the same exact frame everywhere.
    /// </summary>
    /// <param name="obj">The object to check.</param>
    private static bool IsBeingCranked(SObject obj)
    {
        return obj.modData.ContainsKey(CrankedPowerCoilPatches.CrankingByModDataKey);
    }

    /// <summary>MOD: added. Get the current frame index (0-5) into <see cref="CrankingAssetName"/> for a coil currently being cranked — loops continuously off the game's own running clock. No per-machine state is needed: every currently-cranking coil just shares this one clock, so their loops don't need to (and won't) be frame-synced with each other.</summary>
    private static int GetLoopingCrankFrameIndex()
    {
        double elapsedMs = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        return (int)(elapsedMs / CrankedPowerCoilPatches.CrankFrameDurationMs) % CrankedPowerCoilPatches.CrankFrameCount;
    }

    /// <summary>
    /// MOD: added. Get how fast the crank shake (see <see cref="GetCrankShakeOffsetX"/>) should cycle
    /// right now, for a coil currently being cranked. Speeds up while the cranking player's own bobber
    /// is landing the target (<see cref="BobberBar.bobberInBar"/>) — but that field is local-only, never
    /// networked, so only the client actually running the minigame (i.e. this class's own local
    /// <see cref="ActiveMinigame"/>) can ever know it. Every OTHER connected player simply always sees
    /// the idle speed instead, per request, rather than adding a whole new networked signal just for
    /// this cosmetic detail.
    /// </summary>
    private static float GetCrankShakeSpeed()
    {
        return CrankedPowerCoilPatches.ActiveMinigame is { } minigame && minigame.Bar.bobberInBar
            ? CrankedPowerCoilPatches.CrankShakeSpeedSuccess
            : CrankedPowerCoilPatches.CrankShakeSpeedIdle;
    }

    /// <summary>MOD: added. Get the current horizontal shake offset (in already-4x-scaled screen pixels) for a coil currently being cranked — a continuous side-to-side motion, unlike <see cref="Draw_Prefix"/>'s own pre-existing <c>shakeJitter</c> (a brief, random "can't do that" jitter gated on <see cref="SObject.shakeTimer"/>); the two are independent and simply add together on the rare occasion both are active at once.</summary>
    private static float GetCrankShakeOffsetX()
    {
        double elapsedSeconds = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
        return (float)Math.Sin(elapsedSeconds * CrankedPowerCoilPatches.GetCrankShakeSpeed()) * CrankedPowerCoilPatches.CrankShakeAmplitude;
    }

    /// <summary>Override the growth/shrink offset used when drawing the Cranked Power Coil, to produce a continuous size pulse while powered and settled — matches <see cref="PowerCoilPatches.GetScale_Postfix"/>'s own approach.</summary>
    /// <param name="__instance">The object instance being drawn.</param>
    /// <param name="__result">The offset (in pre-4x-zoom pixels) to grow the sprite's drawn size by; mutated in place for the target item.</param>
    private static void GetScale_Postfix(SObject __instance, ref Vector2 __result)
    {
        if (__instance.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId)
            return;

        // MOD: added — no separate pulse while cranking (the loop's own motion already reads as
        // active) or while genuinely unpowered.
        if (CrankedPowerCoilPatches.IsBeingCranked(__instance) || !CrankedPowerCoilPatches.IsPowered(__instance))
        {
            __result = Vector2.Zero;
            return;
        }

        double elapsedSeconds = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
        float pulse = (float)Math.Sin(elapsedSeconds * CrankedPowerCoilPatches.PulseSpeed + CrankedPowerCoilPatches.PulsePhaseOffset) * CrankedPowerCoilPatches.PulseAmplitude;

        __result = new Vector2(16f * pulse, 64f * pulse);
    }

    /// <summary>Force-create a light source for a powered Cranked Power Coil, same technique as <see cref="PowerCoilPatches.InitializeLightSource_Postfix"/> (and for the same reason — see its own remarks) — reuses that class's own hand-tuned radius/color rather than duplicating a value that would drift out of sync if retuned again.</summary>
    /// <param name="__instance">The object being initialized.</param>
    /// <param name="tileLocation">The object's tile position.</param>
    private static void InitializeLightSource_Postfix(SObject __instance, Vector2 tileLocation)
    {
        if (__instance.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId)
            return;

        if (!CrankedPowerCoilPatches.IsPowered(__instance))
        {
            __instance.lightSource = null;
            return;
        }

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

    /// <summary>Keep an already-placed Cranked Power Coil's light registration in sync with its live cranked/powered state every tick — same reasoning as <see cref="PowerCoilPatches.UpdateWhenCurrentLocation_Postfix"/>'s own remarks (needed here since a crank, or the nightly reset, can flip this state well after <see cref="SObject.initializeLightSource"/> was last called).</summary>
    /// <param name="__instance">The object being updated.</param>
    private static void UpdateWhenCurrentLocation_Postfix(SObject __instance)
    {
        if (__instance.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId)
            return;

        GameLocation? location = __instance.Location;
        if (location == null)
            return;

        if (!CrankedPowerCoilPatches.IsPowered(__instance))
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

    /// <summary>Fully replace the Cranked Power Coil's world draw call — swaps between the static unpowered sprite, the looping cranking animation strip (while the crank minigame is open — see <see cref="IsBeingCranked"/>), and the static powered sprite, using the same anchor/destination math <see cref="PowerCoilPatches.Draw_Prefix"/> already computes (kept generalized off the texture's own height rather than hardcoded, even though every texture here happens to be the same standard 16x32 size).</summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    /// <param name="alpha">The transparency at which to draw the sprite.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool Draw_Prefix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (__instance.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId)
            return true;

        Texture2D texture;
        Rectangle sourceRect;
        bool isBeingCranked = CrankedPowerCoilPatches.IsBeingCranked(__instance);

        if (isBeingCranked)
        {
            texture = Game1.content.Load<Texture2D>(CrankedPowerCoilPatches.CrankingAssetName);
            int frameIndex = CrankedPowerCoilPatches.GetLoopingCrankFrameIndex();
            sourceRect = new Rectangle(frameIndex * CrankedPowerCoilPatches.FrameWidth, 0, CrankedPowerCoilPatches.FrameWidth, CrankedPowerCoilPatches.FrameHeight);
        }
        else if (CrankedPowerCoilPatches.IsPowered(__instance))
        {
            texture = Game1.content.Load<Texture2D>(CrankedPowerCoilPatches.PoweredAssetName);
            sourceRect = new Rectangle(0, 0, texture.Width, texture.Height);
        }
        else
        {
            texture = Game1.content.Load<Texture2D>(CrankedPowerCoilPatches.UnpoweredAssetName);
            sourceRect = new Rectangle(0, 0, texture.Width, texture.Height);
        }

        Vector2 scale = __instance.getScale() * 4f;
        float extraBaseHeight = (texture.Height * 4f) - 128f; // see PowerCoilPatches.Draw_Prefix's own remarks on this formula — always 0 here since every texture is the standard 32px-tall size, kept for consistency/future-proofing

        int shakeJitter = __instance.shakeTimer > 0 ? Game1.random.Next(-1, 2) : 0;

        // MOD: added — a continuous side-to-side shake while being cranked, on top of (and independent
        // from) the brief random shakeJitter above — see GetCrankShakeOffsetX's own remarks.
        float crankShakeOffsetX = isBeingCranked ? CrankedPowerCoilPatches.GetCrankShakeOffsetX() : 0f;

        Vector2 topAnchor = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 + shakeJitter + crankShakeOffsetX, y * 64 - 64));
        Rectangle destination = new(
            (int)(topAnchor.X - scale.X / 2f),
            (int)(topAnchor.Y - scale.Y / 2f - extraBaseHeight),
            (int)(64f + scale.X),
            (int)(texture.Height * 4f + scale.Y / 2f)
        );

        float layerDepth = Math.Max(0f, (float)((y + 1) * 64 - 24) / 10000f) + x * 1E-05f;
        spriteBatch.Draw(texture, destination, sourceRect, Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, layerDepth);

        return false;
    }

    /// <summary>Start the crank minigame for an unpowered Cranked Power Coil by right-clicking it; a no-op (falls through to vanilla's normal handling) if it's already cranked/powered.</summary>
    /// <param name="__instance">The object being interacted with.</param>
    /// <param name="who">The player interacting with it.</param>
    /// <param name="justCheckingForActivity">Whether this is just a capability check (e.g. for cursor icon) rather than a real interaction.</param>
    /// <param name="__result">The value the original method would have returned.</param>
    /// <returns>Returns <c>false</c> to skip vanilla's default handling, or <c>true</c> to let it run normally.</returns>
    private static bool CheckForAction_Prefix(SObject __instance, Farmer who, bool justCheckingForActivity, ref bool __result)
    {
        if (__instance.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId || justCheckingForActivity || CrankedPowerCoilPatches.IsPowered(__instance))
            return true;

        // MOD: added — refuse (with the same shake + "can't do that" cue PowerCoilPatches already uses
        // for its own unpowered-coil interaction) if a DIFFERENT connected player already has this exact
        // coil's crank minigame open — see IsBeingCrankedByAnother's own remarks. Only one player can
        // crank a given coil at once; two players can still each crank a DIFFERENT coil simultaneously,
        // since this check is scoped to this one coil's own lock, not a global one.
        if (CrankedPowerCoilPatches.IsBeingCrankedByAnother(__instance, who))
        {
            __instance.shakeTimer = 50;
            __instance.Location?.playSound("cancel");
            __result = true;
            return false;
        }

        CrankedPowerCoilPatches.TryStartCrankMinigame(__instance, who);
        __result = true;
        return false;
    }

    /// <summary>Block placing a Cranked Power Coil in a non-permanent location (a Mine/Skull Cavern level, or the Volcano Dungeon) — same reasoning as <see cref="PowerCoilPatches.PlacementAction_Prefix"/>'s own identical check.</summary>
    /// <param name="__instance">The item being placed.</param>
    /// <param name="location">The location it's being placed in.</param>
    /// <param name="__result">The value the original method would have returned.</param>
    /// <returns>Returns <c>false</c> to skip the original method (blocking placement), or <c>true</c> to let it run normally.</returns>
    private static bool PlacementAction_Prefix(SObject __instance, GameLocation location, ref bool __result)
    {
        if (__instance.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId || (location is not MineShaft && location is not VolcanoDungeon))
            return true;

        Game1.showRedMessage(Game1.content.LoadString("Strings\\StringsFromCSFiles:Object.cs.13053"));
        __result = false;
        return false;
    }

    /// <summary>Clear a newly-placed Cranked Power Coil back to unpowered — vanilla preserves modData (and the real <see cref="SObject.IsOn"/> field) across pickup/replant, so without this, digging up and replanting an already-cranked coil would stay powered, violating "always placed unpowered."</summary>
    /// <param name="__instance">The item being placed — see <see cref="PowerSiloPatches.PlacementAction_Postfix"/>'s own remarks for why this ISN'T actually the object that ends up in the world.</param>
    /// <param name="location">The location it was placed in.</param>
    /// <param name="x">The X pixel coordinate it was placed at.</param>
    /// <param name="y">The Y pixel coordinate it was placed at.</param>
    /// <param name="__result">Whether vanilla's own placement logic succeeded.</param>
    private static void PlacementAction_Postfix(SObject __instance, GameLocation location, int x, int y, bool __result)
    {
        if (!__result || __instance.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId)
            return;

        if (!location.Objects.TryGetValue(new Vector2(x / 64, y / 64), out SObject? placedCoil) || placedCoil.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId)
            return;

        placedCoil.modData.Remove(CrankedPowerCoilPatches.PoweredModDataKey);
        placedCoil.IsOn = false;
    }
}

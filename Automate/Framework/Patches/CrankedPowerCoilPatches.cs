using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.Locations;
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
/// "Cranking" is a fully independent minigame (<see cref="CrankMinigame"/>) — NOT vanilla's own fishing
/// minigame, deliberately, so this can never be affected by another mod's fishing-related Harmony
/// patches (see that class's own remarks for the full story) — see <see cref="TryStartCrankMinigame"/>
/// for how it's opened, and <see cref="CheckMinigameCompletion"/> for how its outcome resolves into a
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
    /// MOD: added. The asset name of the crank minigame's own themed backdrop sprite, drawn by
    /// <see cref="CrankMinigame"/> — internal (not private) so that class can read it directly. Sized
    /// 38x150, matching the original vanilla fishing-pole/track sprite it was designed to drop in for.
    /// </summary>
    internal const string CrankingUiAssetName = "Mods/luisMint.PoweredAutomation/CrankingUi";

    /// <summary>
    /// MOD: added. The asset name of the crank minigame's own themed moving target icon, drawn by
    /// <see cref="CrankMinigame"/> — internal (not private) so that class can read it directly. Sized
    /// 20x20, matching the original vanilla moving-fish sprite it was designed to drop in for (drawn off
    /// its own actual size rather than a hardcoded 20x20, so it's never stretched/cropped if that ever
    /// changes).
    /// </summary>
    internal const string CrankingTargetAssetName = "Mods/luisMint.PoweredAutomation/CrankingTarget";

    /// <summary>The pixel width of a single frame in <see cref="CrankingAssetName"/>.</summary>
    private const int FrameWidth = 16;

    /// <summary>The pixel height of a single frame in <see cref="CrankingAssetName"/>.</summary>
    private const int FrameHeight = 32;

    /// <summary>The number of frames in <see cref="CrankingAssetName"/>.</summary>
    private const int CrankFrameCount = 6;

    /// <summary>How long each frame of the looping crank animation is held, in real milliseconds.</summary>
    private const double CrankFrameDurationMs = 83;

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
    /// right now — purely local, per-client state (never networked), matching how vanilla's own fishing
    /// minigame (which <see cref="CrankMinigame"/> was modeled on) is itself entirely client-local. Set
    /// by <see cref="TryStartCrankMinigame"/>, cleared by <see cref="CheckMinigameCompletion"/> once the
    /// minigame closes.
    /// </summary>
    private static (SObject Coil, Farmer Who, CrankMinigame Minigame)? ActiveMinigame;

    /// <summary>
    /// MOD: added. Queue specific locations for a machine reload, set via <see cref="Initialize"/> —
    /// called after a successful crank (see <see cref="OnCrankSucceeded"/>), since nothing else would
    /// otherwise notice the newly-powered tiles mid-day (the next rescan would only happen whenever
    /// something unrelated happens to trigger one).
    /// </summary>
    private static Action<IEnumerable<GameLocation>>? RequeueLocations;

    /// <summary>
    /// MOD: added. Read <see cref="Models.ModConfig.SkipCrankingMinigame"/> live, set via
    /// <see cref="Initialize"/> — checked by <see cref="CheckForAction_Prefix"/> so an unpowered coil is
    /// powered instantly instead of opening <see cref="CrankMinigame"/> at all, while the config is
    /// enabled.
    /// </summary>
    private static Func<bool>? GetSkipCrankingMinigame;

    /// <summary>
    /// MOD: added. The name of a custom event-script command (see <see cref="CrankPropCommand"/>) that
    /// plays this same looping crank animation on a Cranked Power Coil placed as an event PROP (via the
    /// vanilla <c>addBigProp</c> command), for cutscene use — <c>&lt;x&gt; &lt;y&gt; [loops] [frameDurationMs]</c>,
    /// where <c>x</c>/<c>y</c> matches the tile the prop was added at. Registered once via
    /// <see cref="Event.RegisterCommand"/> in <see cref="Apply"/>, since a Content Patcher event script
    /// has no way to call a mod's own C# code directly — this is the standard extension point vanilla
    /// itself provides for exactly that.
    /// </summary>
    private const string CrankPropCommandName = "luisMint.PoweredAutomation_CrankProp";

    /// <summary>MOD: added. The real-world game-clock time (<see cref="Game1.currentGameTime"/>'s own <see cref="GameTime.TotalGameTime"/>, in milliseconds) a given event's own <see cref="CrankPropCommand"/> call started animating at — see that method's own remarks for why this can't just be a local variable.</summary>
    private static readonly Dictionary<Event, double> CrankPropAnimationStartMs = new();


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the accessors this class needs. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="requeueLocations">Queue the given locations for a machine reload — see <see cref="RequeueLocations"/>'s own remarks.</param>
    /// <param name="getSkipCrankingMinigame">Read <see cref="Models.ModConfig.SkipCrankingMinigame"/> live — see <see cref="GetSkipCrankingMinigame"/>'s own remarks.</param>
    public static void Initialize(Action<IEnumerable<GameLocation>> requeueLocations, Func<bool> getSkipCrankingMinigame)
    {
        CrankedPowerCoilPatches.RequeueLocations = requeueLocations;
        CrankedPowerCoilPatches.GetSkipCrankingMinigame = getSkipCrankingMinigame;
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

        // MOD: added — see DrawAsProp_Prefix's own remarks for why the normal SObject.draw patch above
        // doesn't cover a coil placed as an event cutscene prop (via addBigProp) at all.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.drawAsProp)),
            prefix: new HarmonyMethod(typeof(CrankedPowerCoilPatches), nameof(DrawAsProp_Prefix))
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

        // MOD: added — not a Harmony patch; registers a custom event-script command (see
        // CrankPropCommandName's own remarks) so a cutscene can play this same crank animation on a
        // Cranked Power Coil placed as an event prop.
        Event.RegisterCommand(CrankedPowerCoilPatches.CrankPropCommandName, CrankedPowerCoilPatches.CrankPropCommand);
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
    /// <see cref="CrankMinigame"/> is detected by reference — <see cref="Game1.activeClickableMenu"/> no
    /// longer being the SAME instance <see cref="TryStartCrankMinigame"/> opened — which reliably
    /// catches every way it can end (a win, a loss, or the player pressing Escape mid-crank, which
    /// <see cref="CrankMinigame.emergencyShutDown"/> itself already routes through the exact same close
    /// path as a normal loss). <see cref="CrankMinigame.Progress"/> is left in its final, settled state
    /// by the time the menu has actually closed, so reading it here (rather than trying to intercept the
    /// minigame's own internal close logic) is reliable.
    /// </summary>
    internal static void CheckMinigameCompletion()
    {
        if (CrankedPowerCoilPatches.ActiveMinigame is not { } minigame || Game1.activeClickableMenu == minigame.Minigame)
            return;

        CrankedPowerCoilPatches.ActiveMinigame = null;

        // MOD: added — release this player's own crank lock — see CrankingByModDataKey's own remarks.
        if (minigame.Coil.modData.TryGetValue(CrankedPowerCoilPatches.CrankingByModDataKey, out string? crankingByRaw) && long.TryParse(crankingByRaw, out long crankingById) && crankingById == minigame.Who.UniqueMultiplayerID)
            minigame.Coil.modData.Remove(CrankedPowerCoilPatches.CrankingByModDataKey);

        if (minigame.Minigame.Progress >= 1f && minigame.Coil.Location != null && minigame.Coil.QualifiedItemId == CrankedPowerCoilPatches.TargetQualifiedItemId)
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
    /// MOD: added. Open the crank minigame (<see cref="CrankMinigame"/>) — a fully independent widget
    /// with no relationship to real fishing at all (see its own remarks for why). A no-op if a menu is
    /// already open (including an already-open crank minigame) — the cross-player "is someone ELSE
    /// already cranking this exact coil" check happens earlier, in <see cref="CheckForAction_Prefix"/>
    /// (see <see cref="IsBeingCrankedByAnother"/>), so this only ever needs to guard against THIS
    /// client's own local menu state. While this minigame is open, <see cref="Draw_Prefix"/>/
    /// <see cref="GetScale_Postfix"/> play the cranking animation strip on a continuous loop (see
    /// <see cref="IsBeingCranked"/>) instead of the static unpowered sprite.
    /// </summary>
    /// <param name="coil">The Cranked Power Coil being cranked.</param>
    /// <param name="who">The player cranking it.</param>
    private static void TryStartCrankMinigame(SObject coil, Farmer who)
    {
        if (Game1.activeClickableMenu != null)
            return;

        CrankMinigame minigame = new();

        // MOD: added — a real, networked lock (see CrankingByModDataKey's own remarks) so another
        // connected player can't ALSO start cranking this exact coil while this player's own minigame
        // is still open, even though each player's own CrankMinigame is otherwise entirely client-local.
        coil.modData[CrankedPowerCoilPatches.CrankingByModDataKey] = who.UniqueMultiplayerID.ToString();

        Game1.activeClickableMenu = minigame;
        CrankedPowerCoilPatches.ActiveMinigame = (coil, who, minigame);
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
    /// MOD: added. The <see cref="CrankPropCommandName"/> event command's own handler — plays the
    /// looping crank animation on a Cranked Power Coil placed as an event prop (via vanilla's own
    /// <c>addBigProp</c>) for a fixed number of loops, then flips it to the powered look, before letting
    /// the event script continue to its next command. Syntax: <c>&lt;x&gt; &lt;y&gt; [loops] [frameDurationMs]</c>.
    ///
    /// Reuses <see cref="IsBeingCranked"/>/<see cref="GetLoopingCrankFrameIndex"/> — the exact same
    /// state <see cref="Draw_Prefix"/>/<see cref="GetScale_Postfix"/> already read for the real crank
    /// minigame — by stamping <see cref="CrankingByModDataKey"/> directly onto the prop, rather than
    /// going through <see cref="TryStartCrankMinigame"/> at all (there's no real player interaction or
    /// minigame here, just a scripted cutscene beat). Sets <see cref="PoweredModDataKey"/>/<see cref="SObject.IsOn"/>
    /// directly once done, rather than calling <see cref="OnCrankSucceeded"/> — that method also
    /// requeues the coil's own LOCATION for a machine reload, which doesn't apply here since an event
    /// prop was never actually placed in the world (it's not in <see cref="GameLocation.Objects"/> at
    /// all, just <see cref="Event.props"/>).
    ///
    /// Like vanilla's own <c>PrecisePause</c> event command (<see cref="Event.DefaultCommands"/>) and its
    /// own <c>stopWatch</c> field, this needs
    /// SOME persistent per-event state across repeated calls (this command re-runs every tick for as
    /// long as it remains the event's current command) to know when the animation actually started —
    /// since nothing can be added to the vanilla <see cref="Event"/> class itself, a static dictionary
    /// keyed by the event instance fills that role instead, cleared once the animation finishes.
    ///
    /// Not fully synced to <see cref="Game1.currentGameTime"/>'s only ever-increasing clock actually
    /// mattering here for LOOP COUNTING (not frame selection): <see cref="GetLoopingCrankFrameIndex"/>
    /// itself just keeps looping forever off the raw clock regardless of when this command started (a
    /// continuous loop looks identical no matter its absolute phase), so only the total ELAPSED time
    /// since this command began needs tracking, to know when the requested number of loops has passed.
    /// </summary>
    /// <param name="event">The event running this command.</param>
    /// <param name="args">The command's own arguments — see this method's own remarks for the syntax.</param>
    /// <param name="context">The context for the active event.</param>
    private static void CrankPropCommand(Event @event, string[] args, EventContext context)
    {
        if (!ArgUtility.TryGetInt(args, 1, out int x, out string error)
            || !ArgUtility.TryGetInt(args, 2, out int y, out error)
            || !ArgUtility.TryGetOptionalInt(args, 3, out int loops, out error, 2)
            || !ArgUtility.TryGetOptionalInt(args, 4, out int frameDurationMs, out error, (int)CrankedPowerCoilPatches.CrankFrameDurationMs))
        {
            context.LogErrorAndSkip(error);
            return;
        }

        SObject? prop = null;
        Vector2 tile = new(x, y);
        foreach (SObject candidate in @event.props)
        {
            if (candidate.QualifiedItemId == CrankedPowerCoilPatches.TargetQualifiedItemId && candidate.TileLocation == tile)
            {
                prop = candidate;
                break;
            }
        }

        if (prop == null)
        {
            // MOD: added — no matching prop found (e.g. a typo'd tile, or the event data changed since
            // this command was written) — don't hang the whole cutscene waiting on an animation that can
            // never start.
            CrankedPowerCoilPatches.CrankPropAnimationStartMs.Remove(@event);
            @event.CurrentCommand++;
            return;
        }

        double nowMs = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        if (!CrankedPowerCoilPatches.CrankPropAnimationStartMs.TryGetValue(@event, out double startMs))
        {
            startMs = nowMs;
            CrankedPowerCoilPatches.CrankPropAnimationStartMs[@event] = startMs;
            prop.modData[CrankedPowerCoilPatches.CrankingByModDataKey] = "eventProp";
        }

        double totalDurationMs = loops * CrankedPowerCoilPatches.CrankFrameCount * frameDurationMs;
        if (nowMs - startMs < totalDurationMs)
            return; // still animating — this command re-runs next tick, same as vanilla's own PrecisePause

        prop.modData.Remove(CrankedPowerCoilPatches.CrankingByModDataKey);
        prop.modData[CrankedPowerCoilPatches.PoweredModDataKey] = "true";
        prop.IsOn = true;
        CrankedPowerCoilPatches.CrankPropAnimationStartMs.Remove(@event);
        @event.CurrentCommand++;
    }

    /// <summary>
    /// MOD: added. Get how fast the crank shake (see <see cref="GetCrankShakeOffsetX"/>) should cycle
    /// right now, for a coil currently being cranked. Speeds up while the cranking player's own target
    /// is currently landing in the bar (<see cref="CrankMinigame.IsTargetContained"/>) — but that field
    /// is local-only, never networked, so only the client actually running the minigame (i.e. this
    /// class's own local <see cref="ActiveMinigame"/>) can ever know it. Every OTHER connected player
    /// simply always sees the idle speed instead, per request, rather than adding a whole new networked
    /// signal just for this cosmetic detail.
    /// </summary>
    private static float GetCrankShakeSpeed()
    {
        return CrankedPowerCoilPatches.ActiveMinigame is { } minigame && minigame.Minigame.IsTargetContained
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

    /// <summary>
    /// MOD: added. Fully replace <see cref="SObject.drawAsProp"/> for the Cranked Power Coil — an
    /// entirely separate draw path from the normal in-world <see cref="SObject.draw"/> <see cref="Draw_Prefix"/>
    /// already patches, used specifically when this object is placed as an event cutscene prop (via the
    /// vanilla <c>addBigProp</c> command — see <see cref="CrankPropCommand"/>) rather than actually
    /// placed in the world. Vanilla's own <c>drawAsProp</c> reads the item's raw, DATA-declared texture
    /// directly (<see cref="PoweredAssetName"/>, since that's the base <c>Texture</c> this item's own
    /// Data/BigCraftables entry declares) with no awareness of this class's own unpowered/cranking/
    /// powered swap at all — without this second patch, a coil placed as an event prop always rendered
    /// as if already powered, and never animated while <see cref="CrankPropCommand"/> was cranking it.
    /// Mirrors vanilla's own bigCraftable branch of <c>drawAsProp</c> geometry-for-geometry (its
    /// destination height is already hardcoded to the same 128px baseline our own textures are sized
    /// for, so no extra-height adjustment is needed here unlike <see cref="Draw_Prefix"/>) — omits the
    /// <c>showNextIndex</c>/<c>(BC)17</c> tapper-specific branches, neither of which could ever apply to
    /// this item.
    /// </summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="b">The sprite batch being drawn to.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool DrawAsProp_Prefix(SObject __instance, SpriteBatch b)
    {
        if (__instance.QualifiedItemId != CrankedPowerCoilPatches.TargetQualifiedItemId || __instance.isTemporarilyInvisible)
            return true;

        int x = (int)__instance.TileLocation.X;
        int y = (int)__instance.TileLocation.Y;

        Texture2D texture;
        Rectangle sourceRect;

        if (CrankedPowerCoilPatches.IsBeingCranked(__instance))
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
        Vector2 position = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, y * 64 - 64));
        Rectangle destination = new(
            (int)(position.X - scale.X / 2f),
            (int)(position.Y - scale.Y / 2f),
            (int)(64f + scale.X),
            (int)(128f + scale.Y / 2f)
        );

        b.Draw(texture, destination, sourceRect, Color.White, 0f, Vector2.Zero, SpriteEffects.None, Math.Max(0f, (float)((y + 1) * 64 - 1) / 10000f));

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

        // MOD: added — Config.SkipCrankingMinigame lets a player power a coil instantly instead of
        // opening the minigame at all, reusing OnCrankSucceeded directly (the same end state a real win
        // produces) rather than duplicating its logic. Checked here (not earlier, alongside the
        // IsBeingCrankedByAnother guard above) so a coil someone else is already cranking still refuses
        // normally either way.
        if (CrankedPowerCoilPatches.GetSkipCrankingMinigame?.Invoke() == true)
        {
            CrankedPowerCoilPatches.OnCrankSucceeded(__instance, who);
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

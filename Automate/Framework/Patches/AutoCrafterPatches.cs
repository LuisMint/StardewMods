using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework.Machines.Objects;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.ItemTypeDefinitions;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patches for the custom Auto Crafter machine — assigning/removing its recipe via
/// right-click (<see cref="CheckForAction_Prefix"/>), freezing its processing countdown while starved
/// (<see cref="MinutesElapsed_Prefix"/>, needed because it has no <c>Data/Machines</c> entry for the
/// generic <see cref="PowerRequiredMachinePatches"/> freeze to apply to), and its custom 5-frame press
/// animation plus sign-style assigned-item display (<see cref="Draw_Prefix"/>).
///
/// MOD: fixed — <see cref="GetFrameIndex"/> used to compare <see cref="DateTimeOffset.UtcNow"/> against a
/// <see cref="AutoCrafterMachine.ProcessingStartMsModDataKey"/>/<see cref="AutoCrafterMachine.AnimStartModDataKey"/>
/// timestamp written by whichever client actually started the craft (always the host) or the prime/unprime
/// transition (whichever player physically clicked it) — synced via <see cref="StardewValley.Object.modData"/>,
/// but read back against a DIFFERENT client's own clock on every other connected player. Correct on whoever
/// wrote it, wrong on everyone else by however much that machine's system clock happens to differ — the
/// same mistake found and fixed for <see cref="ContainerVisualEffects.TriggerLidAnimation"/>'s lid timing.
/// Now, exactly like that fix, the synced values are treated as opaque CHANGE MARKERS only — each client
/// independently notices when one changes and anchors ITS OWN local clock to it (see
/// <see cref="LocalAnimStates"/>), so the elapsed-time math is always both computed AND consumed on the
/// same machine. The strike-cycle dedup (<see cref="LocalAnimState.LastHandledStrikeCycle"/>) moved off
/// networked modData into this same local state for the same reason — its only visible effects
/// (<see cref="SpawnStrikePlume"/>'s dust puff and the "crafting" cue via <c>GameLocation.localSound</c>)
/// were ALREADY local-only per client, so sharing the dedup flag across clients via modData never bought
/// anything once each client's own elapsed-time calculation is independently correct; it just meant an
/// unnecessary networked write from a per-frame draw call, and a real chance of clients disagreeing about
/// which cycle they're even on. This requires zero new network messages — it reuses the modData sync that
/// was already happening (see <see cref="ContainerVisualEffects"/>'s own remarks for why a per-transfer
/// SMAPI mod message was tried elsewhere for the analogous problem and reverted for causing severe lag).
/// </summary>
internal static class AutoCrafterPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>
    /// MOD: added. Per-machine, purely LOCAL (never networked) animation anchor state — see this class's
    /// own remarks for why. Keyed via <see cref="ConditionalWeakTable{TKey,TValue}"/> so an entry for a
    /// machine that stops being drawn is reclaimed by the GC on its own, with no explicit cleanup needed.
    /// </summary>
    private static readonly ConditionalWeakTable<SObject, LocalAnimState> LocalAnimStates = new();

    /// <summary>MOD: added. Per-machine mutable state tracked by <see cref="LocalAnimStates"/>.</summary>
    private sealed class LocalAnimState
    {
        /// <summary>The <see cref="AutoCrafterMachine.ProcessingStartMsModDataKey"/> value this client last saw, or <c>null</c> if no craft has been observed yet.</summary>
        public double? LastSeenProcessingStartMarker;

        /// <summary>This client's own <see cref="DateTimeOffset.UtcNow"/> reading (as Unix ms) from the moment it first noticed <see cref="LastSeenProcessingStartMarker"/> change — the press-cycle animation's own local anchor.</summary>
        public double LocalProcessingAnchorMs;

        /// <summary>MOD: moved off networked modData — see this class's own remarks. Which press-cycle index (0-based, since the current craft started) THIS client has already played the strike particle/sound for, or -1 if none yet this craft.</summary>
        public int LastHandledStrikeCycle = -1;

        /// <summary>The <see cref="AutoCrafterMachine.AnimStartModDataKey"/> value this client last saw, or <c>null</c> if no prime/unprime transition has been observed yet.</summary>
        public double? LastSeenAnimStartMarker;

        /// <summary>This client's own <see cref="DateTimeOffset.UtcNow"/> reading (as Unix ms) from the moment it first noticed <see cref="LastSeenAnimStartMarker"/> change — the prime/unprime transition's own local anchor.</summary>
        public double LocalTransitionAnchorMs;

        /// <summary>Whether this client has already determined <see cref="LastSeenAnimStartMarker"/>'s own transition to be over (or — on this client's first-ever sighting of this machine — is being treated as already-settled rather than replayed, matching <see cref="Patches.ChestLidAnimationPatches"/>'s own first-sight handling) — a cheap early-out, same purpose the lid animation's own expired flag serves.</summary>
        public bool IsTransitionSettled;
    }

    /// <summary>The asset name of the powered sprite sheet (5 frames, 16x32 each, horizontal strip).</summary>
    private const string PoweredAssetName = "Mods/luisMint.PoweredAutomation/AutoCrafter";

    /// <summary>The asset name of the static unpowered sprite (16x28).</summary>
    private const string UnpoweredAssetName = "Mods/luisMint.PoweredAutomation/AutoCrafter_UnPowered";

    /// <summary>The pixel width of a single frame in <see cref="PoweredAssetName"/>.</summary>
    private const int FrameWidth = 16;

    /// <summary>The pixel height of a single frame in <see cref="PoweredAssetName"/>.</summary>
    private const int FrameHeight = 32;

    /// <summary>How long the one-shot priming (1→4) / unpriming (4→1) animation takes, in real milliseconds.</summary>
    private const double TransitionDurationMs = 400;

    /// <summary>
    /// MOD: changed — the press-stroke's own visual rhythm is now anchored to the
    /// moment processing began (<see cref="AutoCrafterMachine.GetProcessingStartMs"/>, set once when the
    /// craft starts) and scaled so a whole number of press-cycles exactly fills the craft's own
    /// <see cref="AutoCrafterMachine.ProcessingMinutes"/>-minute real-world duration — see
    /// <see cref="GetPressCycleTiming"/> for the actual math. Unlike the earlier per-10-minute-segment
    /// version (which needed to keep resyncing its anchor against the separately-clocked, discretely-
    /// ticking <c>SObject.MinutesUntilReady</c> and could visibly hiccup wherever the two drifted), this
    /// anchors ONCE at craft start and never resyncs — <c>readyForHarvest</c> is still checked first and
    /// takes priority regardless of any small drift between this cosmetic estimate and the actual
    /// countdown, so there's nothing for the two clocks to visibly disagree about after that.
    /// This is the down-stroke's own BASE duration (before the scaling in <see cref="GetPressCycleTiming"/> is applied).
    /// </summary>
    private const double BasePressDownMs = 200;

    /// <summary>MOD: added — the pressed-hold (frame 1) and extended-hold (frame 4) are now equal length, and shorter than the old extended-only hold. This is the shared BASE duration for both — see <see cref="BasePressDownMs"/>'s own remarks on scaling.</summary>
    private const double BaseHoldMs = 300;

    /// <summary>See <see cref="BasePressDownMs"/> — the return stroke's own BASE duration, frame 1 back up to frame 4.</summary>
    private const double BasePressUpMs = 200;

    /// <summary>The full press-stroke cycle's own BASE duration — <see cref="BasePressDownMs"/> + <see cref="BaseHoldMs"/> (pressed) + <see cref="BasePressUpMs"/> + <see cref="BaseHoldMs"/> (extended) — used only to pick how many whole cycles fit the craft's real-world duration; see <see cref="GetPressCycleTiming"/>.</summary>
    private const double BasePressCycleMs = AutoCrafterPatches.BasePressDownMs + AutoCrafterPatches.BaseHoldMs + AutoCrafterPatches.BasePressUpMs + AutoCrafterPatches.BaseHoldMs;

    /// <summary>MOD: widened further — how much WIDER the sprite gets at the bottom of a press stroke (squash), as a fraction of its normal size. Kept separate from <see cref="SquashAmplitudeY"/>/<see cref="StretchAmplitude"/> so the horizontal squash specifically could be made thicker without also exaggerating the vertical flatten or the spring-back overshoot.</summary>
    private const float SquashAmplitudeX = 0.20f;

    /// <summary>How much SHORTER the sprite gets at the bottom of a press stroke (squash), as a fraction of its normal size.</summary>
    private const float SquashAmplitudeY = 0.045f;

    /// <summary>How much the sprite narrows/elongates right after springing back up (stretch overshoot), as a fraction of its normal size — applied symmetrically to both axes.</summary>
    private const float StretchAmplitude = 0.045f;

    /// <summary>Extra downward pixel offset applied to the sign-style assigned-item display, on top of the vanilla Sign.draw positions it's otherwise copied from — those were tuned for a shorter sign sprite, so the icon sat too high above the taller Auto Crafter sprite without this.</summary>
    private const float DisplayItemExtraYOffset = 32f;

    /// <summary>Get the shared power-required-machines system, set via <see cref="Initialize"/>.</summary>
    private static Func<PowerRequiredMachineSystem>? GetSystem;

    /// <summary>Get the currently-powered tiles for a location (or <c>null</c> if the power system is disabled), set via <see cref="Initialize"/>.</summary>
    private static Func<GameLocation, IReadOnlySet<Vector2>?>? GetPoweredTiles;

    /// <summary>
    /// MOD: added. Nudge Automate's event-based automation to recheck a specific tile's machine group
    /// soon, set via <see cref="Initialize"/>. Called after a recipe is newly assigned (see
    /// <see cref="CheckForAction_Prefix"/>) — without this, a machine that was previously stuck at
    /// "no recipe assigned" (so <see cref="AutoCrafterMachine.SetInput"/> kept returning false) only gets
    /// re-tried by whatever event happens to fire NEXT for its group (another chest change, the periodic
    /// backstop scan, etc.), which could be a long wait even though the connected Powered Chest may
    /// already have every ingredient sitting there ready — this is what caused it to look like it
    /// "doesn't register the items" until something incidental (e.g. opening/closing the connected
    /// chest) finally re-triggered a scan.
    /// </summary>
    private static Action<GameLocation, Vector2>? NotifyMachineMightBeReady;


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the accessors needed to resolve which Auto Crafters are power-starved, and to nudge event-based automation. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="getSystem">Get the shared power-required-machines system.</param>
    /// <param name="getPoweredTiles">Get the currently-powered tiles for a location (or <c>null</c> if the power system is disabled).</param>
    /// <param name="notifyMachineMightBeReady">Nudge Automate's event-based automation to recheck a specific tile's machine group soon — see <see cref="NotifyMachineMightBeReady"/>.</param>
    public static void Initialize(Func<PowerRequiredMachineSystem> getSystem, Func<GameLocation, IReadOnlySet<Vector2>?> getPoweredTiles, Action<GameLocation, Vector2> notifyMachineMightBeReady)
    {
        AutoCrafterPatches.GetSystem = getSystem;
        AutoCrafterPatches.GetPoweredTiles = getPoweredTiles;
        AutoCrafterPatches.NotifyMachineMightBeReady = notifyMachineMightBeReady;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        // MOD: added — freezes the processing countdown while starved. PowerRequiredMachinePatches
        // already does this generically via ShouldTimePassForMachine, but that hook is only consulted
        // by vanilla's own minutesElapsed when GetMachineData() != null (see Object.cs:4806) — the Auto
        // Crafter deliberately has no Data/Machines entry (see the design plan's "Why no Data/Machines
        // entry" section), so it needs its own freeze here instead.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.minutesElapsed), [typeof(int)]),
            prefix: new HarmonyMethod(typeof(AutoCrafterPatches), nameof(MinutesElapsed_Prefix))
        );

        // MOD: added — assign/remove/swap the recipe by right-clicking with (or without) an item in
        // hand. Vanilla's own checkForAction already handles collecting a readyForHarvest output
        // generically (CheckForActionOnMachine, Object.cs:4452-4524) with no dependency on
        // GetMachineData(), so this only intervenes for the not-ready-for-harvest case.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.checkForAction)),
            prefix: new HarmonyMethod(typeof(AutoCrafterPatches), nameof(CheckForAction_Prefix))
        );

        // MOD: added — full custom draw (5-frame press animation, unpowered static sprite, sign-style
        // assigned-item display), replicating the vanilla readyForHarvest bubble (Object.cs:5451-5481)
        // by hand since skipping the original draw call entirely would otherwise lose it too.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.draw), [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]),
            prefix: new HarmonyMethod(typeof(AutoCrafterPatches), nameof(Draw_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get whether an Auto Crafter's own tile is currently out of power range.</summary>
    /// <param name="obj">The object to check.</param>
    private static bool IsStarved(SObject obj)
    {
        GameLocation? location = obj.Location;
        if (location == null)
            return false;

        // MOD: fixed — was BaseMachine.GetDefaultMachineId(obj.Name), which strips non-alphanumeric
        // characters from the vanilla object's OWN Name field. That works for a vanilla-style short name
        // (e.g. "Crystalarium"), but this machine's own Data/BigCraftables entry deliberately sets Name to
        // the qualified "{{ModId}}_AutoCrafter" (every custom object in this mod's content pack does the
        // same, to avoid colliding with another mod's object of the same short name) — stripping that
        // instead produced "luisMintPoweredAutomationAutoCrafter", which could never match "AutoCrafter" in
        // Models.ModConfig.PowerRequiredMachineNames. Every check below silently always saw this machine as
        // powered as a result: the no-power overlay never showed, the processing countdown never actually
        // paused while unpowered, and the recipe-assignment interaction was never blocked either — even
        // with "AutoCrafter" correctly configured. This machine has exactly one well-known type ID
        // (see AutoCrafterMachine's own constructor), so it's used directly here instead of re-deriving one.
        //
        // NOTE: PowerRequiredMachinePatches.GetMachineTypeId fixes this exact same root cause generically
        // (stripping the content pack's own ID prefix) for every OTHER power-required machine, since that
        // class can't hardcode one specific type. If this Auto Crafter's own MachineTypeID resolution ever
        // changes, or another custom machine needs this same treatment, check both places.
        IReadOnlySet<Vector2>? poweredTiles = AutoCrafterPatches.GetPoweredTiles!(location);
        return AutoCrafterPatches.GetSystem!().IsPowerStarved(AutoCrafterPatches.GetCandidateIds(obj), [obj.TileLocation], poweredTiles);
    }

    /// <summary>
    /// MOD: added. Caches every identifier this machine could reasonably be configured under in
    /// <see cref="Models.ModConfig.PowerRequiredMachineNames"/> — "AutoCrafter" plus its own raw
    /// qualified/unqualified item ID, so a player can configure by whichever one's easier to find (see
    /// <see cref="PowerRequiredMachineSystem.RequiresPower(IEnumerable{string})"/>'s own remarks). Every
    /// Auto Crafter instance is the exact same underlying item, so this only ever needs computing once —
    /// <see cref="IsStarved"/> (via <see cref="Draw_Prefix"/>) calls it every frame per visible Auto
    /// Crafter, and without caching that would allocate a fresh array from scratch on every draw call for
    /// a result that never changes.
    /// </summary>
    private static string[]? CandidateIdsCache;

    /// <summary>Get every identifier this machine could reasonably be configured under — see <see cref="CandidateIdsCache"/>'s own remarks.</summary>
    /// <param name="obj">The object to resolve.</param>
    private static string[] GetCandidateIds(SObject obj)
    {
        return AutoCrafterPatches.CandidateIdsCache ??= [BaseMachine.GetDefaultMachineId<AutoCrafterMachine>(), obj.QualifiedItemId, obj.ItemId];
    }

    /// <summary>Freeze the processing countdown while starved, by pretending no time passed for this tick.</summary>
    /// <param name="__instance">The object whose time is elapsing.</param>
    /// <param name="minutes">The number of minutes that passed — zeroed out while starved.</param>
    private static void MinutesElapsed_Prefix(SObject __instance, ref int minutes)
    {
        if (__instance.QualifiedItemId == AutoCrafterMachine.QualifiedItemId && AutoCrafterPatches.IsStarved(__instance))
            minutes = 0;
    }

    /// <summary>Assign, remove, or swap the machine's recipe by right-clicking with (or without) an item in hand.</summary>
    /// <param name="__instance">The object being interacted with.</param>
    /// <param name="who">The player interacting with it.</param>
    /// <param name="justCheckingForActivity">Whether this is just a capability check (e.g. for cursor icon) rather than a real interaction.</param>
    /// <param name="__result">The value the original method would have returned.</param>
    /// <returns>Returns <c>false</c> to skip vanilla's default handling, or <c>true</c> to let it run normally.</returns>
    private static bool CheckForAction_Prefix(SObject __instance, Farmer who, bool justCheckingForActivity, ref bool __result)
    {
        if (__instance.QualifiedItemId != AutoCrafterMachine.QualifiedItemId || justCheckingForActivity)
            return true;

        if (__instance.readyForHarvest.Value)
            return true; // let vanilla collect the finished output normally

        // MOD: added — a starved machine can still have its output collected above
        // (that's deliberately never blocked, same as every other power-required machine — see
        // PowerRequiredMachinePatches's own remarks), but every OTHER interaction (assign/remove/swap)
        // is refused outright while starved, showing only the generic "Machine needs power" reminder
        // (PowerRequiredMachinePatches.CheckForAction_Prefix, a separate non-blocking prefix on this
        // same method that keeps firing regardless of what this prefix returns).
        if (AutoCrafterPatches.IsStarved(__instance))
        {
            __result = true;
            return false;
        }

        // MOD: changed — assign/remove/swap is now allowed even mid-craft. This is
        // safe because a craft that's already started is entirely self-contained in heldObject/
        // MinutesUntilReady once SetInput sets them (see AutoCrafterMachine.SetInput) — changing or
        // clearing the assigned recipe here only affects what happens for the NEXT craft after this one
        // finishes; it doesn't touch the one already in progress. GetFrameIndex mirrors this by giving
        // heldObject-driven state priority over the assigned-recipe modData, so the press/throb
        // animation keeps playing normally even if the assignment was just cleared out from under it.

        Item? held = who.CurrentItem;
        if (held == null)
        {
            // MOD: added — plays the Crystalarium's own "toggle off" cue, via the
            // standard vanilla GameLocation.playSound (multiplayer-safe: broadcasts to other clients via
            // its own netAudio sync when needed, unlike Game1.sounds.PlayLocal which is local-only).
            if (AutoCrafterMachine.TryRemoveAssignedRecipe(__instance) && __instance.Location != null && !Game1.paused) // no-op if nothing assigned; starts the 4→1 unprime animation either way
                __instance.Location.playSound("smallSelect", __instance.TileLocation);
            __result = true;
            return false;
        }

        // MOD: added — re-using the SAME item that's already assigned toggles the
        // assignment off (matching the category signs' own "hold the same item again" gesture, just
        // with a different effect: those signs show info, this machine clears itself), rather than just
        // re-confirming the same recipe over and over.
        if (AutoCrafterMachine.TryGetDisplayItem(__instance, out Item? currentDisplayItem) && currentDisplayItem.QualifiedItemId == held.QualifiedItemId)
        {
            AutoCrafterMachine.TryRemoveAssignedRecipe(__instance);
            if (__instance.Location != null && !Game1.paused)
                __instance.Location.playSound("smallSelect", __instance.TileLocation); // MOD: added
            __result = true;
            return false;
        }

        // MOD: changed — both "no recipe in the game produces this item" and "a
        // recipe exists, but this player hasn't learned it" now show the same message, since from the
        // player's perspective the machine just can't make it either way (Game1.showRedMessage already
        // plays its own "cancel" cue, so no separate sound here).
        if (!AutoCrafterMachine.TryResolveAnyRecipeForItem(held, out CraftingRecipe? _))
        {
            __instance.shakeTimer = 50;
            Game1.showRedMessage(I18n.Message_CraftUnknownRecipe(itemName: held.DisplayName));
        }
        else if (AutoCrafterMachine.TryResolveKnownRecipeForItem(held, who, out CraftingRecipe? recipe))
        {
            AutoCrafterMachine.SetAssignedRecipe(__instance, recipe, held.QualifiedItemId);
            if (__instance.Location != null && !Game1.paused)
                __instance.Location.playSound("select", __instance.TileLocation); // MOD: added — the Crystalarium's own "toggle on" cue

            // MOD: added — nudge event-based automation to recheck this tile's group soon, in case the
            // connected Powered Chest already has every ingredient sitting there ready (see
            // NotifyMachineMightBeReady's own remarks for why this was otherwise missed).
            if (__instance.Location != null)
                AutoCrafterPatches.NotifyMachineMightBeReady?.Invoke(__instance.Location, __instance.TileLocation);
        }
        else
        {
            __instance.shakeTimer = 50;
            Game1.showRedMessage(I18n.Message_CraftUnknownRecipe(itemName: held.DisplayName));
        }

        __result = true;
        return false;
    }

    /// <summary>
    /// MOD: added. Get the press-cycle's actual phase durations, scaled so a whole
    /// number of cycles exactly fills the craft's own real-world <see cref="AutoCrafterMachine.ProcessingMinutes"/>
    /// duration (<see cref="Game1.realMilliSecondsPerGameTenMinutes"/> — the real seconds one 10-minute
    /// segment actually takes at the current game speed, scaled up to the full processing time) with zero
    /// leftover. The cycle count is picked (via rounding) to land each individual cycle as close as
    /// possible to <see cref="BasePressCycleMs"/>, keeping each phase's own proportion of the total.
    /// </summary>
    private static (double pressDownMs, double holdMs, double pressUpMs, double cycleMs) GetPressCycleTiming()
    {
        double totalDurationMs = Math.Max(1, Game1.realMilliSecondsPerGameTenMinutes) * (AutoCrafterMachine.ProcessingMinutes / 10.0);
        int cycleCount = Math.Max(1, (int)Math.Round(totalDurationMs / AutoCrafterPatches.BasePressCycleMs));
        double cycleMs = totalDurationMs / cycleCount;
        double scale = cycleMs / AutoCrafterPatches.BasePressCycleMs;

        return (AutoCrafterPatches.BasePressDownMs * scale, AutoCrafterPatches.BaseHoldMs * scale, AutoCrafterPatches.BasePressUpMs * scale, cycleMs);
    }

    /// <summary>Get the current press-animation frame index (0-4) into <see cref="PoweredAssetName"/>. See <see cref="GetPressCycleTiming"/> for how the cycle is paced.</summary>
    /// <param name="machine">The machine being drawn.</param>
    /// <param name="isProcessing">Whether the machine is actively processing right now (i.e. the squash-and-stretch should apply) — see <see cref="Draw_Prefix"/>.</param>
    /// <param name="strokeProgressMs">How many real milliseconds have elapsed since the current press cycle started — meaningful only when <paramref name="isProcessing"/> is true, used by <see cref="GetSquashStretchScale"/>.</param>
    /// <param name="justStruck">Whether the press just reached fully-pressed (frame 1) on this call — signals <see cref="Draw_Prefix"/> to play the strike particle/sound.</param>
    /// <param name="lastHandledStrikeCycle">MOD: added. THIS client's own <see cref="LocalAnimState.LastHandledStrikeCycle"/> as of this call (meaningful only when <paramref name="justStruck"/> is true) — lets <see cref="Draw_Prefix"/> read it back without a second <see cref="LocalAnimStates"/> lookup, now that it's local state rather than something re-readable from modData.</param>
    private static int GetFrameIndex(SObject machine, out bool isProcessing, out double strokeProgressMs, out bool justStruck, out int lastHandledStrikeCycle)
    {
        // MOD: fixed — see this class's own remarks for why ProcessingStartMs/AnimStartMs are no longer
        // read as literal cross-machine-comparable timestamps; localNowMs is ONLY ever compared against
        // THIS SAME client's own earlier reading (state.LocalProcessingAnchorMs/LocalTransitionAnchorMs),
        // anchored fresh the moment THIS client notices either marker change — never against a value some
        // OTHER machine wrote. Still real wall-clock time (continues through a pause, etc.), just no
        // longer trusted to mean the same instant on a different computer.
        double localNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        isProcessing = false;
        strokeProgressMs = 0;
        justStruck = false;
        lastHandledStrikeCycle = -1;

        LocalAnimState state = AutoCrafterPatches.LocalAnimStates.GetOrCreateValue(machine);

        // MOD: an in-progress craft is entirely self-contained in heldObject/MinutesUntilReady once
        // SetInput starts it — it keeps animating normally (and finishes normally) even if the assigned
        // recipe is removed/swapped mid-craft, since that only changes what happens for the NEXT craft.
        // So heldObject/readyForHarvest take priority over the assigned-recipe modData here.
        if (machine.heldObject.Value != null)
        {
            if (machine.readyForHarvest.Value)
                return 4; // finished, waiting to be collected — hold extended

            isProcessing = true;

            double processingStartMarker = AutoCrafterMachine.GetProcessingStartMs(machine);
            if (state.LastSeenProcessingStartMarker != processingStartMarker)
            {
                // MOD: added — a genuinely new craft, from THIS client's own perspective (whether that's
                // because one truly just started, or because this is the first time this client has drawn
                // this machine while a craft happens to be active) — anchor MY OWN local clock to it right
                // now, and reset the strike-cycle dedup for it (see LocalAnimState.LastHandledStrikeCycle's
                // own remarks for why that's local now too, no longer AutoCrafterMachine.SetInput's own
                // modData.Remove).
                state.LastSeenProcessingStartMarker = processingStartMarker;
                state.LocalProcessingAnchorMs = localNowMs;
                state.LastHandledStrikeCycle = -1;
            }

            double elapsedSinceStart = Math.Max(0, localNowMs - state.LocalProcessingAnchorMs);
            (double pressDownMs, double holdMs, double pressUpMs, double cycleMs) = AutoCrafterPatches.GetPressCycleTiming();
            strokeProgressMs = elapsedSinceStart % cycleMs;

            if (strokeProgressMs < pressDownMs)
                return 4 - (int)Math.Round(strokeProgressMs / pressDownMs * 3); // stamp down: extended (4) -> pressed (1)

            double afterDown = strokeProgressMs - pressDownMs;
            if (afterDown < holdMs)
            {
                // MOD: added — fire the strike particle/sound once per cycle, right
                // as it reaches fully-pressed. Tracked by cycle index (not a "did we juuust cross the
                // threshold" instant check) so it's still reliably caught even if this machine wasn't
                // drawn for a stretch (e.g. off-screen) — it just fires once, for whatever the CURRENT
                // cycle is, rather than bursting for every cycle that passed unseen.
                int cycleIndex = (int)(elapsedSinceStart / cycleMs);
                if (cycleIndex > state.LastHandledStrikeCycle)
                {
                    state.LastHandledStrikeCycle = cycleIndex;
                    justStruck = true;
                }
                lastHandledStrikeCycle = state.LastHandledStrikeCycle;

                return 1; // hold at fully-pressed before springing back up
            }

            double afterHoldPressed = afterDown - holdMs;
            if (afterHoldPressed < pressUpMs)
                return 1 + (int)Math.Round(afterHoldPressed / pressUpMs * 3); // spring back: pressed (1) -> extended (4)

            return 4; // settling, then holding — see GetSquashStretchScale
        }

        bool hasRecipe = AutoCrafterMachine.TryGetAssignedRecipe(machine, out _);
        double animStartMarker = AutoCrafterMachine.GetAnimStartMs(machine);

        if (state.LastSeenAnimStartMarker != animStartMarker)
        {
            if (state.LastSeenAnimStartMarker == null)
            {
                // MOD: fixed — the FIRST time THIS client ever draws THIS machine (e.g. just walked into
                // the location, or it just finished loading), animStartMarker could be an arbitrarily old
                // value from a transition that finished long ago (even in a previous session) — without
                // this branch, it would still visibly replay that transition the instant any client first
                // laid eyes on it, the exact same bug already found and fixed for the item-transfer lid
                // animation (see ChestLidAnimationPatches's own remarks). Recording the current marker as
                // an already-known, already-settled baseline fixes this — only a marker that changes AFTER
                // this client has already seen one counts as a genuinely new trigger.
                state.LastSeenAnimStartMarker = animStartMarker;
                state.IsTransitionSettled = true;
            }
            else
            {
                state.LastSeenAnimStartMarker = animStartMarker;
                state.LocalTransitionAnchorMs = localNowMs;
                state.IsTransitionSettled = false;
            }
        }

        bool transitionInProgress = false;
        double transitionProgress = 0;
        if (!state.IsTransitionSettled)
        {
            double elapsedSinceTransition = localNowMs - state.LocalTransitionAnchorMs;
            transitionInProgress = elapsedSinceTransition < AutoCrafterPatches.TransitionDurationMs;
            transitionProgress = Math.Clamp(elapsedSinceTransition / AutoCrafterPatches.TransitionDurationMs, 0, 1);

            if (!transitionInProgress)
                state.IsTransitionSettled = true; // cheap early-out next time, same purpose as the lid animation's own expired flag
        }

        if (!hasRecipe)
        {
            return transitionInProgress
                ? 4 - (int)Math.Round(transitionProgress * 3) // unpriming: extended (4) -> pressed (1)
                : 0; // idle, unassigned
        }

        return transitionInProgress
            ? 1 + (int)Math.Round(transitionProgress * 3) // priming: pressed (1) -> extended (4)
            : 4; // primed, idle — waiting for ingredients
    }

    /// <summary>
    /// MOD: added. Get the squash-and-stretch scale for the current point in a press
    /// stroke — squashes (widens/flattens) through the down-stroke, overshoots into a stretch
    /// (narrows/elongates) right after springing back up, then eases back to a neutral scale.
    /// </summary>
    /// <param name="strokeProgressMs">How many real milliseconds have elapsed since the current press stroke started — see <see cref="GetFrameIndex"/>.</param>
    private static Vector2 GetSquashStretchScale(double strokeProgressMs)
    {
        const float neutralX = 1f, neutralY = 1f;
        float squashX = 1f + AutoCrafterPatches.SquashAmplitudeX, squashY = 1f - AutoCrafterPatches.SquashAmplitudeY;
        float stretchX = 1f - AutoCrafterPatches.StretchAmplitude, stretchY = 1f + AutoCrafterPatches.StretchAmplitude;

        (double pressDownMs, double holdMs, double pressUpMs, _) = AutoCrafterPatches.GetPressCycleTiming();

        if (strokeProgressMs < pressDownMs)
        {
            double t = strokeProgressMs / pressDownMs; // neutral -> squashed
            return new Vector2(4f * Lerp(neutralX, squashX, t), 4f * Lerp(neutralY, squashY, t));
        }

        double afterDown = strokeProgressMs - pressDownMs;
        if (afterDown < holdMs)
            return new Vector2(4f * squashX, 4f * squashY); // holding fully squashed, matching frame 1's held pose

        double afterHoldPressed = afterDown - holdMs;
        if (afterHoldPressed < pressUpMs)
        {
            double t = afterHoldPressed / pressUpMs; // squashed -> stretched
            return new Vector2(4f * Lerp(squashX, stretchX, t), 4f * Lerp(squashY, stretchY, t));
        }

        // MOD: eases stretched -> neutral across the WHOLE extended-hold phase (no separate settle
        // constant needed) — reaches exactly neutral right as the hold ends, then the next cycle's
        // down-stroke begins from there.
        double settleElapsed = afterHoldPressed - pressUpMs;
        double settleT = Math.Min(1, settleElapsed / holdMs);
        return new Vector2(4f * Lerp(stretchX, neutralX, settleT), 4f * Lerp(stretchY, neutralY, settleT));

        static float Lerp(float from, float to, double t) => from + (to - from) * (float)t;
    }

    /// <summary>
    /// MOD: changed — <see cref="Utility.addDirtPuffs"/> turned out to spawn FOUR
    /// overlapping 64x64 sprites per call (a random pick of two, PLUS one more always added
    /// regardless of the requested count): "TileSheets\animations" row 46 (pixel Y=2944) and row 12
    /// (pixel Y=768), which are the exact same two-layer "construction dust cloud" asset Building.cs
    /// draws together for a building under construction — i.e. the bigger/whiter poof already rejected
    /// earlier — plus row 14 (pixel Y=896) always on top. This now uses ONLY row 14, and fewer of them,
    /// avoiding the construction-cloud rows entirely. Exact look unverified without seeing it in-game —
    /// easy to swap the row number again if this still isn't it.
    /// </summary>
    /// <param name="location">The location to spawn the dust in.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    private static void SpawnStrikePlume(GameLocation location, int x, int y)
    {
        // MOD: fixed — the previous Game1.paused check didn't cover the actual
        // failure case: an UNFOCUSED window isn't necessarily Game1.paused (e.g. "pause when out of
        // focus" off, or a windowed/borderless game that keeps simulating and drawing while the OS
        // considers a different window focused). Game1.currentGameTime.TotalGameTime keeps advancing off
        // real wall-clock time regardless of focus (this is what GetFrameIndex's own cycle timing is
        // anchored to), so the strike cycle keeps silently ticking away in the background — but
        // TemporaryAnimatedSprite.update (which advances/removes each spawned particle) apparently isn't
        // keeping pace while unfocused either. Net effect without this guard: particles kept getting added
        // the whole time the window was unfocused, none of them got a chance to finish/despawn, and they'd
        // all suddenly become visible piled on top of each other the moment focus returned. Game1.game1 is
        // the actual XNA Game instance — its IsActive is the real window-focus flag (distinct from
        // Game1.paused, which only reflects an open pause menu/multiplayer pause vote).
        if (Game1.paused || Game1.game1?.IsActive == false)
            return;

        // MOD: changed — centered further, on the tile's own bottom
        // (matching where the press sprite's own anchor sits — see Draw_Prefix's anchorPoint), with a
        // tight, symmetric jitter instead of the old spread, which was both anchored off the tile's
        // top-left corner (not the sprite at all) and asymmetric (-16 to +31, biased down-right).
        location.temporarySprites.Add(new TemporaryAnimatedSprite(
            rowInAnimationTexture: 14,
            position: new Vector2(x * 64f, y * 64f + 24f) + new Vector2(Game1.random.Next(-6, 7), Game1.random.Next(-6, 7)),
            color: Color.White,
            animationLength: 8,
            flipped: Game1.random.NextBool())
        {
            motion = new Vector2(0f, -1f),
            interval = Game1.random.Next(50, 80)
        });
    }

    /// <summary>Fully replace the Auto Crafter's world draw call: the 5-frame press animation (or the static unpowered sprite), the sign-style assigned-item display, and a hand-replicated copy of vanilla's readyForHarvest output bubble.</summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    /// <param name="alpha">The transparency at which to draw the sprite.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool Draw_Prefix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (__instance.QualifiedItemId != AutoCrafterMachine.QualifiedItemId)
            return true;

        bool isPowered = !AutoCrafterPatches.IsStarved(__instance);
        int shakeJitter = __instance.shakeTimer > 0 ? Game1.random.Next(-1, 2) : 0;
        float drawLayer = Math.Max(0f, (float)((y + 1) * 64 - 24) / 10000f) + x * 1E-05f;

        // base sprite: either the animated powered strip, or the static unpowered texture.
        Texture2D texture;
        Rectangle sourceRect;
        bool isProcessing = false;
        double strokeProgressMs = 0;
        if (isPowered)
        {
            texture = Game1.content.Load<Texture2D>(AutoCrafterPatches.PoweredAssetName);
            int frameIndex = AutoCrafterPatches.GetFrameIndex(__instance, out isProcessing, out strokeProgressMs, out bool justStruck, out int lastHandledStrikeCycle);
            sourceRect = new Rectangle(frameIndex * AutoCrafterPatches.FrameWidth, 0, AutoCrafterPatches.FrameWidth, AutoCrafterPatches.FrameHeight);

            // MOD: changed — a light dust puff every press-cycle right as it reaches
            // fully-pressed, plus the "crafting" cue but ONLY for the craft's first three strikes (cycle
            // index 0-2 — GetFrameIndex already returns the just-handled cycle index via its own
            // lastHandledStrikeCycle out parameter, now that it's purely local per-client state rather
            // than something re-readable from modData — see LocalAnimState's own remarks). Uses
            // GameLocation.localSound rather than the broadcasting playSound used elsewhere: this draw
            // call runs independently on EVERY client that can see the machine (unlike machine logic,
            // which only runs once on the host), so a broadcasting sound here would multiply into an
            // echo — one play per observing client, re-broadcast to everyone else. localSound plays only
            // for the local client that just triggered it — each client now independently (and
            // correctly, regardless of any clock drift from another machine) decides when its OWN
            // justStruck fires, so there's nothing left to keep in sync across clients for this at all.
            if (justStruck && __instance.Location != null)
            {
                AutoCrafterPatches.SpawnStrikePlume(__instance.Location, x, y);

                if (!Game1.paused && lastHandledStrikeCycle < 3)
                    __instance.Location.localSound("crafting", new Vector2(x, y));
            }
        }
        else
        {
            texture = Game1.content.Load<Texture2D>(AutoCrafterPatches.UnpoweredAssetName);
            sourceRect = new Rectangle(0, 0, texture.Width, texture.Height);
        }

        // MOD: added — squash-and-stretch driven by the press stroke itself (see
        // GetSquashStretchScale), only while actively processing (not while idle/primed/unpowered).
        Vector2 scale = isProcessing
            ? AutoCrafterPatches.GetSquashStretchScale(strokeProgressMs)
            : new Vector2(4f, 4f);

        // MOD: anchored at the FIXED world point "tile center X, tile bottom Y" (converted to screen
        // space), rather than the taller-texture-only-safe "topLeft + origin*4" indirection
        // PoweredChestPatches uses (that only works when every texture it draws is the same known
        // height) — our unpowered texture (16x28) is a different height than the powered frames
        // (16x32), and origin = (width/2, height) varies with it, so anchoring off a height-dependent
        // topLeft made the unpowered sprite's bottom land ~16px above the powered frames' baseline
        // instead of on the same one. Anchoring directly at the tile's own bottom edge instead means
        // the sprite's bottom-center (origin) always lands there regardless of the source texture's
        // height, so both textures share the same baseline.
        Vector2 origin = new(sourceRect.Width / 2f, sourceRect.Height);
        Vector2 anchorPoint = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64f + 32f + shakeJitter, y * 64f + 64f));
        spriteBatch.Draw(texture, anchorPoint, sourceRect, Color.White * alpha, 0f, origin, scale, SpriteEffects.None, drawLayer);

        // sign-style assigned-item display — floats above the sprite whenever a recipe is assigned,
        // independent of powered/processing/ready state, replicating Sign.draw's displayItem rendering
        // (Sign.cs:97-124, cases 1/3 — CraftingRecipe.createItem() only ever produces a plain Object or
        // bigCraftable Object, never a Hat/Ring/Furniture, so only those two cases apply here).
        if (AutoCrafterMachine.TryGetDisplayItem(__instance, out Item? displayItem))
        {
            if (displayItem is SObject displayObj && displayObj.bigCraftable.Value)
            {
                displayItem.drawInMenu(spriteBatch, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, y * 64 - 64 + 21 + 4 - 1 + AutoCrafterPatches.DisplayItemExtraYOffset)), 0.75f, 1f, drawLayer + 1E-05f, StackDrawType.Hide, Color.White, drawShadow: false);
            }
            else
            {
                displayItem.drawInMenu(spriteBatch, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 + 1, y * 64 - 64 + 21 + 8 - 2 + AutoCrafterPatches.DisplayItemExtraYOffset)), 0.75f, 0.45f, drawLayer + 1E-05f, StackDrawType.Hide, Color.Black, drawShadow: false);
                displayItem.drawInMenu(spriteBatch, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 + 1, y * 64 - 64 + 21 + 4 - 1 + AutoCrafterPatches.DisplayItemExtraYOffset)), 0.75f, 1f, drawLayer + 2E-05f, StackDrawType.Hide, Color.White, drawShadow: false);
            }
        }

        // readyForHarvest output bubble — hand-replicated copy of Object.cs:5451-5481, since skipping
        // vanilla's own draw entirely (above) would otherwise lose it too, and it's what makes the
        // Furnace-style "output in a bubble" behavior actually show up.
        if (__instance.readyForHarvest.Value)
        {
            float baseSort = (float)((y + 1) * 64) / 10000f + __instance.TileLocation.X / 50000f;
            float yOffset = 4f * (float)Math.Round(Math.Sin((Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0) / 250.0), 2);

            spriteBatch.Draw(Game1.mouseCursors, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 - 8, y * 64 - 96 - 16 + yOffset)), new Rectangle(141, 465, 20, 24), Color.White * 0.75f, 0f, Vector2.Zero, 4f, SpriteEffects.None, baseSort + 1E-06f);

            if (__instance.heldObject.Value != null)
            {
                ParsedItemData heldItemData = ItemRegistry.GetDataOrErrorItem(__instance.heldObject.Value.QualifiedItemId);
                Texture2D heldTexture = heldItemData.GetTexture();

                // MOD: changed — a bigcraftable's own source sprite is 16x32 (twice as
                // tall as a plain Object's 16x16), so drawing it at the same scale/origin vanilla's bubble
                // code uses (Object.cs:5473, which never has to handle a bigcraftable held item since no
                // vanilla machine ever produces one) made it spill way outside the bubble. Scaled down and
                // re-centered on the taller sprite's own midpoint, then nudged up a quarter tile so it
                // settles back inside the bubble instead of hanging below it.
                bool heldIsBigCraftable = __instance.heldObject.Value.bigCraftable.Value;
                float heldScale = heldIsBigCraftable ? 2.5f : 4f;
                Vector2 heldOrigin = heldIsBigCraftable ? new Vector2(8f, 16f) : new Vector2(8f, 8f);
                float heldExtraYOffset = heldIsBigCraftable ? -16f : 0f;
                spriteBatch.Draw(heldTexture, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 + 32, y * 64 - 64 - 8 + yOffset + heldExtraYOffset)), heldItemData.GetSourceRect(), Color.White * 0.75f, 0f, heldOrigin, heldScale, SpriteEffects.None, baseSort + 1E-05f);

                if (__instance.heldObject.Value.Stack > 1)
                    __instance.heldObject.Value.DrawMenuIcons(spriteBatch, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, y * 64 - 64 - 32 + yOffset - 4f)), 1f, 1f, baseSort + 1.2E-05f, StackDrawType.Draw, Color.White);
                else if (__instance.heldObject.Value.Quality > 0)
                    __instance.heldObject.Value.DrawMenuIcons(spriteBatch, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, y * 64 - 64 - 32 + yOffset - 4f)), 1f, 1f, baseSort + 1.2E-05f, StackDrawType.HideButShowQuality, Color.White);
            }
        }

        return false;
    }
}

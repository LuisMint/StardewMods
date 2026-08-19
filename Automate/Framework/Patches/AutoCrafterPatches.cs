using System;
using System.Collections.Generic;
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
/// </summary>
internal static class AutoCrafterPatches
{
    /*********
    ** Fields
    *********/
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

        string machineTypeId = BaseMachine.GetDefaultMachineId(obj.Name);
        IReadOnlySet<Vector2>? poweredTiles = AutoCrafterPatches.GetPoweredTiles!(location);

        return AutoCrafterPatches.GetSystem!().IsPowerStarved(machineTypeId, [obj.TileLocation], poweredTiles);
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
            Game1.showRedMessage($"Don't know how to craft {held.DisplayName}.");
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
            Game1.showRedMessage($"Don't know how to craft {held.DisplayName}.");
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
    private static int GetFrameIndex(SObject machine, out bool isProcessing, out double strokeProgressMs, out bool justStruck)
    {
        // MOD: fixed — this used to read Game1.currentGameTime here, compared against a
        // ProcessingStartMs/AnimStartMs timestamp written (via modData, which syncs to every player) by
        // whichever client actually started the craft or prime/unprime transition. Game1.currentGameTime
        // is each client's own local elapsed-time-since-launch clock with no relationship to any other
        // client's value, so a farmhand reading a timestamp the HOST wrote (automation itself only ever
        // runs on the host) computed a meaningless elapsed time — usually deeply negative, clamped to
        // zero by Math.Max below — freezing the animation on one frame instead of cycling, even though
        // the machine's actual processing (heldObject/readyForHarvest, real synced fields) worked fine.
        // Unix epoch milliseconds are real wall-clock time and stay consistent across networked clients.
        double nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        isProcessing = false;
        strokeProgressMs = 0;
        justStruck = false;

        // MOD: an in-progress craft is entirely self-contained in heldObject/MinutesUntilReady once
        // SetInput starts it — it keeps animating normally (and finishes normally) even if the assigned
        // recipe is removed/swapped mid-craft, since that only changes what happens for the NEXT craft.
        // So heldObject/readyForHarvest take priority over the assigned-recipe modData here.
        if (machine.heldObject.Value != null)
        {
            if (machine.readyForHarvest.Value)
                return 4; // finished, waiting to be collected — hold extended

            isProcessing = true;

            double elapsedSinceStart = Math.Max(0, nowMs - AutoCrafterMachine.GetProcessingStartMs(machine));
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
                if (cycleIndex > AutoCrafterMachine.GetLastHandledStrikeCycle(machine))
                {
                    AutoCrafterMachine.SetLastHandledStrikeCycle(machine, cycleIndex);
                    justStruck = true;
                }

                return 1; // hold at fully-pressed before springing back up
            }

            double afterHoldPressed = afterDown - holdMs;
            if (afterHoldPressed < pressUpMs)
                return 1 + (int)Math.Round(afterHoldPressed / pressUpMs * 3); // spring back: pressed (1) -> extended (4)

            return 4; // settling, then holding — see GetSquashStretchScale
        }

        bool hasRecipe = AutoCrafterMachine.TryGetAssignedRecipe(machine, out _);
        double elapsedSinceTransition = nowMs - AutoCrafterMachine.GetAnimStartMs(machine);
        bool transitionInProgress = elapsedSinceTransition >= 0 && elapsedSinceTransition < AutoCrafterPatches.TransitionDurationMs;
        double transitionProgress = Math.Clamp(elapsedSinceTransition / AutoCrafterPatches.TransitionDurationMs, 0, 1);

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
            int frameIndex = AutoCrafterPatches.GetFrameIndex(__instance, out isProcessing, out strokeProgressMs, out bool justStruck);
            sourceRect = new Rectangle(frameIndex * AutoCrafterPatches.FrameWidth, 0, AutoCrafterPatches.FrameWidth, AutoCrafterPatches.FrameHeight);

            // MOD: changed — a light dust puff every press-cycle right as it reaches
            // fully-pressed, plus the "crafting" cue but ONLY for the craft's first three strikes (cycle
            // index 0-2 — GetFrameIndex already records the just-handled cycle index via
            // AutoCrafterMachine.SetLastHandledStrikeCycle, so it's read back here right after justStruck
            // fires). Uses GameLocation.localSound rather than the broadcasting playSound used elsewhere:
            // this draw call runs independently on EVERY client that can see the machine (unlike machine
            // logic, which only runs once on the host), so a broadcasting sound here would multiply into
            // an echo — one play per observing client, re-broadcast to everyone else. localSound plays
            // only for the local client that just triggered it, and since every client derives justStruck
            // from the same synced modData/game-time, they each fire their own local copy at the same
            // moment without any of them stacking.
            if (justStruck && __instance.Location != null)
            {
                AutoCrafterPatches.SpawnStrikePlume(__instance.Location, x, y);

                if (!Game1.paused && AutoCrafterMachine.GetLastHandledStrikeCycle(__instance) < 3)
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

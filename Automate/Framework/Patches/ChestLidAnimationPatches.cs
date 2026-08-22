using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Makes a chest's lid visually swing open for a bit after
/// <see cref="ContainerVisualEffects"/> triggers an animation on it, using the exact technique the
/// installed "Convenient Inventory" mod already uses for its own quick-stack chest animation, since that
/// approach already had the right feel: a prefix on <see cref="Chest.draw(SpriteBatch, int, int, float)"/>
/// that reflectively overwrites the chest's private <c>currentLidFrame</c> field fresh EVERY draw call,
/// computed purely from a time window anchored in <see cref="LocalAnimStates"/> (see that field's own
/// remarks) — never a live, incrementally-mutated field.
///
/// This is what lets it avoid the jitter/sound-spam an earlier attempt at an analogous shipping-bin lid
/// effect ran into: vanilla's own per-tick logic (<c>Chest.fixLidFrame</c>/<c>UpdateFarmerNearby</c>,
/// which run during <c>updateWhenCurrentLocation</c> — earlier in the same tick than <c>draw</c>) still
/// runs completely unbothered and still force-closes the lid based on real farmer proximity exactly as
/// before; this patch simply overwrites whatever that logic left behind, immediately before each render,
/// for as long as the animation window lasts. It never locks the chest's mutex, so vanilla never
/// auto-pops the real inventory menu, and a real player interacting with the chest is completely
/// unaffected — including deferring entirely (see <see cref="Draw_Prefix"/>'s own mutex check) the
/// instant a player actually opens it mid-animation, so the container never appears to close or attempt
/// to while genuinely being opened, and this never fights a real open with wherever its own closing
/// phase happens to be.
///
/// MOD: fixed — <see cref="ContainerVisualEffects.TriggerLidAnimation"/> writes its start marker as
/// <see cref="DateTimeOffset.Now"/> on whichever machine ran the automation (always the host), synced to
/// every client via <see cref="Chest.modData"/>. The ORIGINAL version of this class read that value back
/// as a literal timestamp and compared it against elapsed-time math on THIS client's own
/// <see cref="DateTimeOffset.Now"/> — correct on the host itself, but wrong on every other connected
/// player by however much that farmhand's own system clock happens to differ from the host's, independent
/// of network latency entirely (confirmed directly via user report: the lid visibly drifted from the
/// flying item sprite specifically as a non-host farmhand, worse than plain latency alone would explain).
/// Now the synced value is treated as an opaque CHANGE MARKER only, never parsed as a timestamp — each
/// client independently notices when it changes and stamps ITS OWN local start time against it (see
/// <see cref="LocalAnimStates"/>), so the elapsed-time math is always both computed AND consumed on the
/// SAME machine. That can't make every player's screen agree with each other (a farmhand's own start
/// still lands whenever ITS OWN copy of the modData sync happens to arrive, same as before), but it makes
/// the lid and the sprite consistent with EACH OTHER on any one given screen, which is what was actually
/// asked for — full cross-machine agreement would need a per-transfer network message, already tried and
/// reverted for being far too expensive at this call frequency (see <see cref="ContainerVisualEffects"/>'s
/// own remarks). The marker is also never cleared once expired anymore (the old modData-clearing purely
/// existed to avoid an old chest re-parsing a dead value forever, which the local expired-cache flag below
/// now handles instead) — so <see cref="Draw_Prefix"/>'s own FIRST look at any given chest treats
/// whatever marker is already sitting there as an already-known baseline, not a fresh trigger, or a chest
/// automated long ago would visibly pop its lid open the instant any client first laid eyes on it (e.g.
/// walking into a location with several already-automated containers) — confirmed directly via user
/// report as exactly that symptom.
/// </summary>
internal static class ChestLidAnimationPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Reflects into <see cref="Chest"/>'s private <c>currentLidFrame</c> field — a separate reflected handle from <see cref="PoweredChestPatches"/>'s own private one, since fields can't be shared across classes.</summary>
    private static readonly FieldInfo CurrentLidFrameField = AccessTools.Field(typeof(Chest), "currentLidFrame");

    /// <summary>
    /// MOD: added. Per-chest, purely LOCAL (never networked) record of the last-seen
    /// <see cref="ContainerVisualEffects.LidAnimStartTimeModDataKey"/> marker and when THIS client first
    /// observed it — see this class's own remarks for why the synced value itself is no longer trusted as
    /// a literal timestamp. Keyed via <see cref="ConditionalWeakTable{TKey,TValue}"/> rather than a plain
    /// dictionary so an entry for a chest that stops being drawn (goes out of scope, location unloaded,
    /// etc.) is reclaimed by the GC on its own, with no explicit cleanup needed.
    /// </summary>
    private static readonly ConditionalWeakTable<Chest, LocalAnimState> LocalAnimStates = new();

    /// <summary>MOD: added. Per-chest mutable state tracked by <see cref="LocalAnimStates"/>.</summary>
    private sealed class LocalAnimState
    {
        /// <summary>The <see cref="ContainerVisualEffects.LidAnimStartTimeModDataKey"/> value this client last saw, or <c>null</c> if none yet.</summary>
        public string? LastSeenStartMarker;

        /// <summary>This client's OWN <see cref="DateTimeOffset.Now"/> reading from the moment it first noticed <see cref="LastSeenStartMarker"/> change.</summary>
        public DateTimeOffset LocalStartTime;

        /// <summary>Whether this client has already determined <see cref="LastSeenStartMarker"/>'s own animation window to be over — a cheap early-out so an old, unchanged, expired marker doesn't redo the elapsed-time math on every future draw call of a chest that's never automated again.</summary>
        public bool IsCurrentMarkerExpired;
    }


    /*********
    ** Public methods
    *********/
    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        // MOD: explicit Priority.First — PoweredChestPatches.Draw_Prefix patches this same method and,
        // specifically for a Powered Chest, reads currentLidFrame and returns false (fully replacing
        // the draw call). This prefix's overwrite must land before that read happens, or the animation
        // would silently never show on a Powered Chest — registration order alone isn't a guaranteed
        // substitute for this.
        harmony.Patch(
            original: AccessTools.Method(typeof(Chest), nameof(Chest.draw), [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]),
            prefix: new HarmonyMethod(typeof(ChestLidAnimationPatches), nameof(Draw_Prefix)) { priority = Priority.First }
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Overwrite the chest's rendered lid frame for the duration of an active animation window, if any — see this class's own remarks.</summary>
    /// <param name="__instance">The chest being drawn.</param>
    private static bool Draw_Prefix(Chest __instance)
    {
        // MOD: added — a container shouldn't appear to close or attempt to while it's in the middle of
        // being opened, so if a real player (local or remote — the mutex lock itself is networked, so
        // this reads the same for everyone) is actually interacting with this chest right now, get out
        // of the way entirely rather than potentially
        // overwriting the frame with wherever THIS animation's own closing phase happens to be. Vanilla's
        // own per-tick fixLidFrame (which already runs every tick regardless, in updateWhenCurrentLocation,
        // before draw) already drives the lid open and holds it there for as long as the lock is held —
        // this only needed to step aside, not duplicate that logic itself.
        if (__instance.GetMutex().IsLocked())
            return true;

        // MOD: fixed — this lookup must happen UNCONDITIONALLY, before the modData check below, so its
        // own presence tracks "has THIS CLIENT ever drawn this exact chest before," independently of
        // whether a marker existed yet at the time. It used to be created lazily only once a marker was
        // already present (see the first-sight check further down), which meant a chest with NO marker
        // yet (never automated) never got a LocalAnimState at all, no matter how many times it had
        // actually been drawn already. The moment such a chest got its first-ever real transfer, its
        // state looked EXACTLY like a chest being seen for the first time ever (both start from "no
        // entry"), so that genuinely new marker was wrongly swallowed as an already-known baseline
        // instead of a fresh trigger. Confirmed directly via user report: a chest that "wasn't automated
        // before" freshly joining a group didn't visibly open on its own first transfer — on either end,
        // source or destination — only from the second transfer onward. isFirstObservation (backed by
        // whether an entry existed at all) is what actually distinguishes the two cases; LastSeenStartMarker
        // being null does not, since it stays null for as long as no marker has ever been recorded, however
        // long this client has already been watching the chest.
        bool isFirstObservation = !ChestLidAnimationPatches.LocalAnimStates.TryGetValue(__instance, out _);
        LocalAnimState state = ChestLidAnimationPatches.LocalAnimStates.GetOrCreateValue(__instance);

        // MOD: changed — this is now an opaque CHANGE MARKER, not a timestamp to parse — see this class's
        // own remarks for why. Any non-empty value works; ContainerVisualEffects.TriggerLidAnimation
        // happens to write DateTimeOffset.Now.ToString("O") for it, but nothing here relies on that being
        // parseable, ordered, or meaningful beyond "did it change since I last looked."
        if (!__instance.modData.TryGetValue(ContainerVisualEffects.LidAnimStartTimeModDataKey, out string? rawStartMarker) || string.IsNullOrEmpty(rawStartMarker))
            return true;

        if (state.LastSeenStartMarker == rawStartMarker)
        {
            if (state.IsCurrentMarkerExpired)
                return true; // already determined THIS trigger is over — cheap early-out until a new one arrives, same purpose the old modData-clearing served
        }
        else if (isFirstObservation)
        {
            // MOD: fixed — the FIRST time THIS client ever draws THIS chest (e.g. just walked into the
            // location, or it just finished loading), rawStartMarker could be an arbitrarily old value.
            // Without this branch, a chest automated hours ago in game time — or even in a PREVIOUS
            // session — would still visibly pop its lid open the instant any client first laid eyes on
            // it, since a fresh LocalAnimState's own LastSeenStartMarker starts null, which
            // unconditionally reads as "changed." Confirmed directly via user report: entering an area
            // with several conduit-group containers made them all visibly open/close at once despite
            // nothing actually moving through them. Recording the CURRENT marker as an already-known
            // baseline (with no animation) fixes this — only a marker that changes AFTER this client has
            // already seen a baseline, OR after it's already been watching this chest with no marker at
            // all, counts as a genuinely new trigger.
            state.LastSeenStartMarker = rawStartMarker;
            state.IsCurrentMarkerExpired = true;
            return true;
        }

        // MOD: only parse the duration once we know we might actually need it — either a genuinely new
        // trigger (below) or a still-active one (state.IsCurrentMarkerExpired was false above).
        if (!__instance.modData.TryGetValue(ContainerVisualEffects.LidAnimDurationModDataKey, out string? rawDurationMs) || !int.TryParse(rawDurationMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out int durationMs))
            return true;

        if (state.LastSeenStartMarker != rawStartMarker)
        {
            // MOD: added — a trigger THIS client HAS a baseline for, and it just changed — a genuinely new
            // automated transfer. Anchor MY OWN local clock to it right now, rather than trusting the
            // marker's own value as when it "really" started — see this class's own remarks.
            state.LastSeenStartMarker = rawStartMarker;
            state.LocalStartTime = DateTimeOffset.Now;
            state.IsCurrentMarkerExpired = false;
        }

        double elapsedMs = (DateTimeOffset.Now - state.LocalStartTime).TotalMilliseconds;
        if (elapsedMs > durationMs)
        {
            state.IsCurrentMarkerExpired = true;
            return true;
        }

        // MOD: kept as a defensive guard — LocalStartTime is always THIS client's own clock now, so this
        // shouldn't normally go negative, but a mid-session system clock adjustment (e.g. an NTP resync)
        // could still move DateTimeOffset.Now backward between the two reads; harmless either way (just
        // skips one frame's redraw rather than computing a garbage lid frame index).
        if (elapsedMs < 0)
            return true;

        int frame = ChestLidAnimationPatches.GetAnimatedLidFrame(__instance, elapsedMs, durationMs);
        ChestLidAnimationPatches.CurrentLidFrameField.SetValue(__instance, frame);

        return true;
    }

    /// <summary>
    /// Compute the lid frame for a moment within an open-hold-close animation window — a direct port of
    /// the proven algorithm the installed "Convenient Inventory" mod uses for its own quick-stack chest
    /// animation (<c>QuickStackChestAnimation.GetCurrentAnimationLidFrame</c>): ramp from
    /// <see cref="Chest.startingLidFrame"/> up to <see cref="Chest.getLastLidFrame"/> one frame every
    /// 83ms, then ramp back down the same way once the window's remaining time is within the same
    /// distance of its end, holding fully open in between.
    /// </summary>
    /// <param name="chest">The chest being animated.</param>
    /// <param name="elapsedMs">How many milliseconds have passed since the animation started.</param>
    /// <param name="durationMs">The total length of the animation window, in milliseconds.</param>
    private static int GetAnimatedLidFrame(Chest chest, double elapsedMs, int durationMs)
    {
        const int frameIntervalMs = 83;

        int startFrame = chest.startingLidFrame.Value;
        int lastFrame = chest.getLastLidFrame();
        int openFrameCount = lastFrame - startFrame;
        if (openFrameCount <= 0)
            return startFrame;

        int openEndMs = openFrameCount * frameIntervalMs;
        int closeStartMs = Math.Max(openEndMs, durationMs - openEndMs);

        // still opening
        if (elapsedMs < openEndMs)
            return startFrame + 1 + (int)(elapsedMs / frameIntervalMs);

        // holding fully open
        if (elapsedMs < closeStartMs)
            return lastFrame;

        // closing
        int closeStepCount = (int)((elapsedMs - closeStartMs) / frameIntervalMs);
        return Math.Max(lastFrame - closeStepCount, startFrame);
    }
}

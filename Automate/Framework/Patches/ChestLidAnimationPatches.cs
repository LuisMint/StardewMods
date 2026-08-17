using System;
using System.Globalization;
using System.Reflection;
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
/// computed purely from a time window stored in the chest's own <see cref="Chest.modData"/> (see
/// <see cref="ContainerVisualEffects.TriggerLidAnimation"/>) — never a live, incrementally-mutated
/// field.
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
/// </summary>
internal static class ChestLidAnimationPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Reflects into <see cref="Chest"/>'s private <c>currentLidFrame</c> field — a separate reflected handle from <see cref="PoweredChestPatches"/>'s own private one, since fields can't be shared across classes.</summary>
    private static readonly FieldInfo CurrentLidFrameField = AccessTools.Field(typeof(Chest), "currentLidFrame");


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

        if (!__instance.modData.TryGetValue(ContainerVisualEffects.LidAnimStartTimeModDataKey, out string? rawStartTime) || !DateTimeOffset.TryParse(rawStartTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset startTime))
            return true;

        if (!__instance.modData.TryGetValue(ContainerVisualEffects.LidAnimDurationModDataKey, out string? rawDurationMs) || !int.TryParse(rawDurationMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out int durationMs))
            return true;

        double elapsedMs = (DateTimeOffset.Now - startTime).TotalMilliseconds;
        if (elapsedMs > durationMs)
        {
            // MOD: added, for performance — every chest that's ever been part of an automated transfer
            // keeps this modData forever otherwise, meaning this
            // method would keep parsing a DateTimeOffset string on every single future draw call of that
            // chest, indefinitely, for a window that's long since passed. Clearing it once means the
            // cheap TryGetValue-fails-immediately path above is what every later draw call actually hits.
            __instance.modData.Remove(ContainerVisualEffects.LidAnimStartTimeModDataKey);
            __instance.modData.Remove(ContainerVisualEffects.LidAnimDurationModDataKey);
            return true;
        }

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

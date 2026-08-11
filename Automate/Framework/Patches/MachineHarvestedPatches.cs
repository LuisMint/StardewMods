using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Notices when a machine's finished output was just collected BY HAND (via
/// <see cref="SObject.checkForAction"/> — i.e. the player right-clicked it, not Automate's own output-push
/// cycle), and nudges event-based automation to recheck that machine's group right away. Mirrors
/// <see cref="MachineReadyPatches"/>'s own prefix/postfix "genuine transition" approach, just for the
/// opposite edge: <c>readyForHarvest</c> going true→false instead of false→true.
///
/// Without this, a machine emptied this way — most commonly because its usual output chest was full when
/// it finished, so the player grabbed the item by hand instead of waiting for Automate to place it — could
/// sit fully able to start its next cycle but simply never get looked at again until something UNRELATED
/// happened to trigger a rescan (a chest inventory change elsewhere in the group, a menu closing, or the
/// periodic backstop scan). That gap isn't specific to any one machine type — it applies to every machine
/// Automate supports, since none of them notify automation directly when the player bypasses the normal
/// output-push path.
/// </summary>
internal static class MachineHarvestedPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Get whether event-based automation is currently enabled — this patch is a no-op when it's off, so interval mode's own full scan handles everything without double-processing.</summary>
    private static Func<bool>? GetUseEventBasedAutomation;

    /// <summary>Nudge Automate's event-based automation to recheck a specific tile's machine group soon, set via <see cref="Initialize"/>.</summary>
    private static Action<GameLocation, Vector2>? NotifyMachineMightBeReady;


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the accessors needed to nudge event-based automation. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="getUseEventBasedAutomation">Get whether event-based automation is currently enabled.</param>
    /// <param name="notifyMachineMightBeReady">Nudge Automate's event-based automation to recheck a specific tile's machine group soon.</param>
    public static void Initialize(Func<bool> getUseEventBasedAutomation, Action<GameLocation, Vector2> notifyMachineMightBeReady)
    {
        MachineHarvestedPatches.GetUseEventBasedAutomation = getUseEventBasedAutomation;
        MachineHarvestedPatches.NotifyMachineMightBeReady = notifyMachineMightBeReady;
    }

    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.checkForAction)),
            prefix: new HarmonyMethod(typeof(MachineHarvestedPatches), nameof(CheckForAction_Prefix)),
            postfix: new HarmonyMethod(typeof(MachineHarvestedPatches), nameof(CheckForAction_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Capture whether the object was ready for harvest BEFORE the original (and every other prefix on this method) runs, so the postfix can tell a genuine true→false transition apart from "wasn't ready before either" — see this class's own remarks for why that distinction matters.</summary>
    /// <param name="__instance">The object being interacted with.</param>
    /// <param name="__state">Receives the object's <c>readyForHarvest</c> value before any prefix runs.</param>
    private static void CheckForAction_Prefix(SObject __instance, out bool __state)
    {
        __state = __instance.readyForHarvest.Value;
    }

    /// <summary>Notify event-based automation if this call just collected a machine's output by hand.</summary>
    /// <param name="__instance">The object that was interacted with.</param>
    /// <param name="__state">Whether the object was ready for harvest before this call (see <see cref="CheckForAction_Prefix"/>) — used to require a genuine true→false transition.</param>
    private static void CheckForAction_Postfix(SObject __instance, bool __state)
    {
        if (MachineHarvestedPatches.GetUseEventBasedAutomation is not { } getUseEventBasedAutomation || !getUseEventBasedAutomation())
            return;

        // only proceed on a genuine true→false transition — anything else (was never ready, or is still
        // ready because this call didn't actually collect it, e.g. a locked/failed interaction) isn't a
        // "just emptied by hand" event
        if (!__state || __instance.readyForHarvest.Value || __instance.Location is null)
            return;

        MachineHarvestedPatches.NotifyMachineMightBeReady?.Invoke(__instance.Location, __instance.TileLocation);
    }
}

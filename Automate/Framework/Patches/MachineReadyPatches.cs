using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Notices directly when a specific machine becomes ready, instead of waiting for the
/// periodic full-group scan (see <see cref="ModEntry.OnTimeChanged"/>/<see cref="ModEntry.OnChestInventoryChanged"/>)
/// to notice it. A machine's <c>readyForHarvest</c> flag has no single vanilla chokepoint — this
/// deliberately only patches <see cref="SObject.minutesElapsed"/>, the one that completes the ordinary
/// multi-minute processing countdown (by far the common case for real gameplay). The other 3 vanilla
/// sites that can set <c>readyForHarvest</c> (an instant-complete 0-minute recipe, and CrabPot/IndoorPot's
/// own overnight completion) are deliberately left uncovered here — they're still collected correctly via
/// the existing full-scan backstop, just not accelerated by this class.
///
/// <see cref="SObject.minutesElapsed"/> runs for every placed object/furniture in every location on
/// every in-game 10-minute tick, so the postfix below is written to bail out as cheaply as possible for
/// the overwhelming majority of calls where nothing relevant happened. Two design points worth calling out:
/// <list type="bullet">
/// <item>The <c>__state</c> prefix/postfix pair requires a genuine false→true transition on <c>readyForHarvest</c>,
/// not just "is it ready right now." Vanilla re-executes <c>readyForHarvest.Value = true</c> (a harmless
/// no-op from its own perspective) on every subsequent tick a machine stays Done-but-uncollected (e.g. a
/// full output chest, or a container that was locked at the moment it finished) — without the edge check,
/// that same already-known machine would get re-flagged every tick it stayed uncollected instead of once.
/// One consequence: a machine that's never actually collected only gets flagged ONCE via this fast path;
/// anything that stays stuck relies on <c>ChestInventoryChanged</c>/<c>MenuChanged</c> or the periodic
/// backstop scan to eventually retry it, same as the cases this hook can't see at all.</item>
/// <item>The postfix only ever records the flagged <c>(group, machine)</c> pair — it never calls into the
/// group's own automation logic directly. Beyond keeping per-flag overhead minimal (letting
/// <see cref="ModEntry"/> pace the actual work via <see cref="Models.ModConfig.EventBasedPushPullDelaySeconds"/>),
/// this also avoids reentrancy: a machine's output collection can trigger a nested call back into
/// <see cref="SObject.minutesElapsed"/> (confirmed via a real stack-overflow crash during development), and
/// a plain, side-effect-free list add can't recurse the way calling back into automation logic could.</item>
/// </list>
/// </summary>
internal static class MachineReadyPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Get whether event-based automation is currently enabled — this patch is a no-op when it's off, so interval mode's own full scan handles everything without double-processing.</summary>
    private static Func<bool>? GetUseEventBasedAutomation;

    /// <summary>Get the location key for a location, formatted the same way <see cref="MachineManager"/>'s own machine index uses.</summary>
    private static Func<GameLocation, string>? GetLocationKey;

    /// <summary>The machine manager, used to look up which machine/group (if any) is at a given location/tile.</summary>
    private static MachineManager? MachineManager;

    /// <summary>
    /// MOD: added. The specific machines that became ready since the last <see cref="TakePendingReadyMachines"/>
    /// call, each as its own separate entry (deliberately NOT deduplicated/grouped by owner — see this
    /// class's own remarks for why), accumulated here and drained by <see cref="ModEntry.OnTimeChanged"/>
    /// once the whole tick's <see cref="SObject.minutesElapsed"/> sweep has finished.
    /// </summary>
    private static readonly List<(IMachineGroup Group, IMachine Machine)> PendingReadyMachines = new();


    /*********
    ** Public methods
    *********/
    /// <summary>Prepare this patch class before <see cref="Apply"/> is called.</summary>
    /// <param name="getUseEventBasedAutomation">Get whether event-based automation is currently enabled.</param>
    /// <param name="getLocationKey">Get the location key for a location.</param>
    /// <param name="machineManager">The machine manager, used to look up which machine/group (if any) is at a given location/tile.</param>
    public static void Initialize(Func<bool> getUseEventBasedAutomation, Func<GameLocation, string> getLocationKey, MachineManager machineManager)
    {
        MachineReadyPatches.GetUseEventBasedAutomation = getUseEventBasedAutomation;
        MachineReadyPatches.GetLocationKey = getLocationKey;
        MachineReadyPatches.MachineManager = machineManager;
    }

    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.minutesElapsed)),
            prefix: new HarmonyMethod(typeof(MachineReadyPatches), nameof(MinutesElapsed_Prefix)),
            postfix: new HarmonyMethod(typeof(MachineReadyPatches), nameof(MinutesElapsed_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Capture whether the object was already ready BEFORE the original method runs, so the postfix can tell a genuine false→true transition apart from "was already ready and still is" — see this class's own remarks for why that distinction matters.</summary>
    /// <param name="__instance">The object whose time is about to elapse.</param>
    /// <param name="__state">Receives the object's <c>readyForHarvest</c> value before the original method runs.</param>
    private static void MinutesElapsed_Prefix(SObject __instance, out bool __state)
    {
        __state = __instance.readyForHarvest.Value;
    }

    /// <summary>Flag that a machine just became ready via its own <see cref="SObject.minutesElapsed"/> call, for <see cref="ModEntry.OnTimeChanged"/> to process shortly after (see <see cref="TakePendingReadyMachines"/>) instead of touching it right away.</summary>
    /// <param name="__instance">The object whose time just elapsed.</param>
    /// <param name="__state">Whether the object was already ready before this call (see <see cref="MinutesElapsed_Prefix"/>) — used to require a genuine false→true transition.</param>
    private static void MinutesElapsed_Postfix(SObject __instance, bool __state)
    {
        // no-op in interval mode — its own full scan handles everything, and firing here too would
        // just be redundant (harmless, since flagging a machine twice is idempotent, but wasted work)
        if (MachineReadyPatches.GetUseEventBasedAutomation is not { } getUseEventBasedAutomation || !getUseEventBasedAutomation())
            return;

        // only proceed on a genuine false→true transition — see this class's own remarks for why a plain
        // level check on readyForHarvest re-flags the same already-known machine every tick it stays
        // uncollected, instead of just once when it first became ready
        if (__state || !__instance.readyForHarvest.Value || __instance.Location is null)
            return;

        if (MachineReadyPatches.GetLocationKey is not { } getLocationKey || MachineReadyPatches.MachineManager is not { } machineManager)
            return;

        string locationKey = getLocationKey(__instance.Location);
        if (!machineManager.TryGetMachineAt(locationKey, __instance.TileLocation, out IMachineGroup group, out IMachine machine))
            return;

        MachineReadyPatches.PendingReadyMachines.Add((group, machine));
        AutomationPerfTracker.RecordFlaggedMachine();
    }


    /*********
    ** Public methods (continued)
    *********/
    /// <summary>Get and clear every machine flagged since the last call, each as its own separate entry. Meant to be called once per <see cref="ModEntry.OnTimeChanged"/> firing, after the whole tick's <see cref="SObject.minutesElapsed"/> sweep has already finished — see this class's own remarks for why each entry is kept separate instead of grouped/deduplicated by owner.</summary>
    public static IReadOnlyList<(IMachineGroup Group, IMachine Machine)> TakePendingReadyMachines()
    {
        if (MachineReadyPatches.PendingReadyMachines.Count == 0)
            return Array.Empty<(IMachineGroup, IMachine)>();

        (IMachineGroup, IMachine)[] result = [.. MachineReadyPatches.PendingReadyMachines];
        MachineReadyPatches.PendingReadyMachines.Clear();
        return result;
    }
}

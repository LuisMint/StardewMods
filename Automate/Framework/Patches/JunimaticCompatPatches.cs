using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Bridges Automate's own "power-required machines" balance mechanic (see
/// <see cref="PowerRequiredMachineSystem"/>/<see cref="PowerRequiredMachinePatches"/>) into the separately
/// installed "Junimatic" mod, which otherwise has no awareness of it at all.
///
/// Junimatic decides which machines are valid delivery targets for its own Junimos entirely through its
/// own <c>NermNermNerm.Junimatic.ObjectMachine.State</c> property (read via its own <c>GameMachine.IsIdle</c>
/// helper), which only reads VANILLA readiness (an empty <see cref="SObject.heldObject"/> and a zero
/// <see cref="SObject.MinutesUntilReady"/>) — it has no concept of Automate's own power gate at all.
/// Without this patch, a starved power-required machine still reports itself "Idle" to Junimatic, so a
/// Junimo gets dispatched carrying an item all the way over, only for the actual delivery to be refused at
/// the very last step by <see cref="PowerRequiredMachinePatches.PerformObjectDropInAction_Prefix"/> —
/// vanilla's own <c>Object.AttemptAutoLoad(IInventory, Farmer)</c> calls
/// <see cref="SObject.performObjectDropInAction"/> directly, with no probe step first — which Junimatic has
/// no recovery path for beyond dropping every item the Junimo was carrying on the ground as debris and
/// logging a "probably a bug in the mod" warning (confirmed by decompiling Junimatic.dll — it assumes any
/// such refusal can only be caused by a genuinely broken recipe list, e.g. the same item used as both input
/// and fuel).
///
/// This patches Junimatic's own <c>ObjectMachine.State</c> getter (a postfix) so a starved power-required
/// machine reports <c>MachineState.Working</c> (busy) instead of <c>Idle</c> — the exact same signal
/// Junimatic already uses for a machine mid-processing — so Junimatic's own periodic work scan (its own
/// <c>readOnlyList.Where(m =&gt; m.IsIdle &amp;&amp; m.IsCompatibleWithJunimo(...))</c>, confirmed to be
/// the only place `IsIdle` gates a FILL/delivery decision — every other `IsIdle` check in Junimatic's own
/// code is the exact same "else if" branch alongside an `IsAwaitingPickup` check first, i.e. harvesting a
/// starved machine's already-finished output is never affected by this patch, matching this mod's existing
/// "starved blocks new input, not taking existing output" policy) simply never selects it as a delivery
/// target in the first place. No Junimo is ever dispatched, so nothing is ever carried there to be refused
/// (or dropped) at all. The moment power is restored, <c>State</c> reports <c>Idle</c> again on the very
/// next scan, same as waiting out a real busy machine.
///
/// Deliberately narrower than <see cref="StardioConveyorBeltPatches"/>'s own reflection bridge: this only
/// ever touches ONE Junimatic getter, resolved entirely at runtime and only if Junimatic is actually
/// installed — Junimatic's assembly is never referenced at compile time, and this whole class is a no-op if
/// Junimatic isn't loaded, or if its internal shape (an unofficial, non-API surface) ever changes in a
/// future update; see <see cref="TryApply"/>'s own remarks on why it must run from <c>GameLaunched</c>, not
/// <c>Entry</c> — the same assembly-load-ordering issue <see cref="StardioConveyorBeltPatches"/> already
/// documents for the identical reason.
/// </summary>
internal static class JunimaticCompatPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Junimatic's own manifest unique ID.</summary>
    private const string JunimaticModId = "NermNermNerm.Junimatic";

    /// <summary>
    /// MOD: added. <c>ObjectMachine</c>'s own public <c>Machine</c> property getter — returns the
    /// underlying <see cref="SObject"/> the Junimatic wrapper is tracking, conveniently already typed as a
    /// real vanilla type rather than something else internal to Junimatic. Resolved once in
    /// <see cref="TryApply"/> so <see cref="GetState_Postfix"/> can read it per call without needing a
    /// compile-time reference to Junimatic's own <c>ObjectMachine</c> type.
    /// </summary>
    private static MethodInfo? MachineGetter;


    /*********
    ** Public methods
    *********/
    /// <summary>
    /// Apply this patch, if Junimatic is installed and its own internal shape can be resolved by
    /// reflection. Safe to call unconditionally — a complete no-op if Junimatic isn't loaded at all. Must
    /// be called from <c>GameLaunched</c>, never from <c>Entry</c> — Automate's own <c>Entry</c> can run
    /// before Junimatic's assembly is even loaded, the same ordering issue
    /// <see cref="StardioConveyorBeltPatches.TryApply"/> already documents for the identical reason.
    /// </summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    /// <param name="modRegistry">Used to check whether Junimatic is installed.</param>
    /// <param name="monitor">Used to log a warning if Junimatic is installed but its internal shape couldn't be resolved by reflection.</param>
    public static void TryApply(Harmony harmony, IModRegistry modRegistry, IMonitor monitor)
    {
        if (!modRegistry.IsLoaded(JunimaticCompatPatches.JunimaticModId))
            return;

        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(asm => asm.GetName().Name == "Junimatic");
        if (assembly == null)
        {
            monitor.Log("Junimatic is installed, but its assembly couldn't be found — its Junimos won't respect Automate's power grid, and may drop items on the ground trying to fill a starved machine.", LogLevel.Warn);
            return;
        }

        Type? objectMachineType = assembly.GetType("NermNermNerm.Junimatic.ObjectMachine");
        if (objectMachineType == null)
        {
            monitor.Log("Junimatic is installed, but NermNermNerm.Junimatic.ObjectMachine wasn't found by reflection — its Junimos won't respect Automate's power grid, and may drop items on the ground trying to fill a starved machine.", LogLevel.Warn);
            return;
        }

        JunimaticCompatPatches.MachineGetter = AccessTools.PropertyGetter(objectMachineType, "Machine");
        if (JunimaticCompatPatches.MachineGetter == null)
        {
            monitor.Log("Junimatic is installed, but ObjectMachine.Machine wasn't found by reflection — its Junimos won't respect Automate's power grid, and may drop items on the ground trying to fill a starved machine.", LogLevel.Warn);
            return;
        }

        MethodInfo? stateGetter = AccessTools.PropertyGetter(objectMachineType, "State");
        if (stateGetter == null)
        {
            monitor.Log("Junimatic is installed, but ObjectMachine.State wasn't found by reflection — its Junimos won't respect Automate's power grid, and may drop items on the ground trying to fill a starved machine.", LogLevel.Warn);
            return;
        }

        harmony.Patch(stateGetter, postfix: new HarmonyMethod(typeof(JunimaticCompatPatches), nameof(JunimaticCompatPatches.GetState_Postfix)));
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Report a starved power-required machine as busy ("Working") instead of "Idle" to Junimatic, so it's never picked as a fill/delivery target while unpowered — see this class's own remarks for why.</summary>
    /// <param name="__instance">The Junimatic <c>ObjectMachine</c> wrapper being checked — typed <see cref="object"/> since that type isn't referenced at compile time.</param>
    /// <param name="__result">Junimatic's own <c>MachineState</c> result, boxed — typed <see cref="object"/> for the same reason; Harmony boxes/unboxes it against the real enum type automatically on the way in and out.</param>
    private static void GetState_Postfix(object __instance, ref object __result)
    {
        // MOD: added — MachineState.Idle is ordinal 0 in Junimatic's own enum (confirmed by decompiling
        // Junimatic.dll — Idle, Working, AwaitingPickup, in that declared order). Only override an Idle
        // result: a machine already reporting Working/AwaitingPickup needs no help from this patch, and
        // re-checking IsPowerStarved for those would be pure waste on a hot-ish path (this getter is read
        // repeatedly during Junimatic's own periodic work scans).
        if (Convert.ToInt32(__result) != 0)
            return;

        if (JunimaticCompatPatches.MachineGetter?.Invoke(__instance, null) is not SObject machine)
            return;

        if (!PowerRequiredMachinePatches.IsPowerStarved(machine))
            return;

        // MOD: added — MachineState.Working is ordinal 1. Re-boxed against __result's own actual runtime
        // type (rather than a compile-time-known MachineState this project doesn't reference) so Harmony's
        // own unbox-back-to-the-real-type step on the way out succeeds instead of throwing.
        __result = Enum.ToObject(__result.GetType(), 1);
    }
}

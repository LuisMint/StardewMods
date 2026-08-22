using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Two independent integration points with the separately-installed "Utility Grid Redux"
/// mod's own power/water grid — a completely different economy from Automate's own Power Coil system:
/// <list type="bullet">
/// <item>READ side (<see cref="IsStarved"/> plus <see cref="IsTrackedAsConsumer"/>): layers an
/// independent "is this machine powered" gate on top of Automate's own power-required-machines mechanic
/// (see <see cref="PowerRequiredMachineSystem"/>). Any machine Utility Grid Redux itself considers a
/// power/water consumer is gated SOLELY on ITS OWN live answer, regardless of whether that same machine
/// is also listed in Automate's own <see cref="Models.ModConfig.PowerRequiredMachineNames"/> or within
/// range of an Automate Power Coil — a machine Utility Grid Redux doesn't track at all still falls back
/// to Automate's own Power-Coil-range check exactly as before. See the two call sites
/// (<see cref="Patches.PowerRequiredMachinePatches"/>'s own <c>IsPowerStarved(SObject)</c>, and
/// <see cref="MachineGroupFactory.AddToBuilder"/>) — this class's own surface, and
/// <see cref="PowerRequiredMachineSystem"/>'s, are both untouched by the other.</item>
/// <item>WRITE side (<see cref="TryRegisterGenerator"/>): registers Automate's own Power Coil and
/// Powered Chest as Utility Grid Redux power PRODUCERS, so building either also helps power that grid —
/// the reverse direction, and a genuinely more invasive one (see that method's own remarks).</item>
/// <item>NOTIFICATION side (<see cref="TryHookPowerChangeNotifications"/>): tells Automate's own
/// <c>MachineManager.QueueReload</c> to recheck a location promptly whenever Utility Grid Redux itself
/// notices that location's power state may have changed AND that change actually flipped a
/// tracked object's starved/powered state (see <see cref="MarkDirty_Postfix"/>'s own remarks — not
/// every notification, since most don't correspond to a real change Automate cares about), so a machine
/// gaining Utility Grid Redux power gets recognized by its automation group about as quickly as gaining
/// Power-Coil-range power already was — see that method's own remarks for why this is needed on top of
/// the READ side alone.</item>
/// </list>
///
/// Utility Grid Redux's own public API (<c>IUtilityGridReduxApi</c>, via <c>ModEntry.GetApi()</c>) has
/// no per-object "is this powered" query, and no way to register an external power producer, at all —
/// only aggregate/read-only grid-overlay accessors. Both real mechanisms live entirely in
/// <c>ThaleTheGreat.UtilityGridRedux.ModEntry</c>'s own PRIVATE members — not a stable, versioned
/// contract. Reaching them means reflecting into Utility Grid Redux's own internals, the same
/// unsupported technique its own addon mod ("Industrialization for Utility Grid Redux") already uses to
/// register its own machine rules with it (confirmed working by decompiling both assemblies).
///
/// Resolved exactly ONCE, from <c>ModEntry.OnGameLaunched</c> — never from <c>Entry</c>. See
/// <see cref="Patches.StardioConveyorBeltPatches"/>'s own remarks for why: SMAPI loads each mod's
/// assembly and runs its own <c>Entry</c> one mod at a time, not "load every assembly, then run every
/// Entry()" — reflecting into another mod's assembly from Automate's own <c>Entry</c> can silently find
/// nothing if that mod hasn't taken its turn yet, exactly the bug already fixed for Stardio.
///
/// Two independent layers of defense, since a future Utility Grid Redux update changing its own private
/// internals must never crash Automate:
/// <list type="number">
/// <item>Setup-time (<see cref="TryInitialize"/> and the two methods it calls): if an assembly, type, or
/// member can't be found — or a method's return type isn't the expected one — exactly one clear warning
/// is logged and just THAT piece stays permanently disabled for the rest of the session (the read side
/// and write side fail independently of each other). Every other Automate mechanic, INCLUDING its own
/// Power-Coil-based power-required-machines mechanic, is completely unaffected either way.</item>
/// <item>Call-time (<see cref="IsStarved"/>): the actual <see cref="MethodInfo.Invoke(object?,object?[]?)"/>
/// call is wrapped in its own try/catch, in case Utility Grid Redux's own method throws at runtime for
/// some edge case (e.g. a location it hasn't initialized internal state for yet). Any such failure is
/// treated as "not gated by it" / "don't block" — never as a crash — with a RATE-LIMITED
/// (<see cref="IMonitor.LogOnce"/>) warning rather than one spammed every frame. The write side has no
/// equivalent per-call risk — it only ever runs once, at startup, wrapped in its own try/catch too.</item>
/// </list>
/// </summary>
internal static class UtilityGridReduxSystem
{
    /*********
    ** Fields
    *********/
    /// <summary>Utility Grid Redux's own manifest unique ID.</summary>
    private const string UtilityGridReduxModId = "ThaleTheGreat.UtilityGridRedux";

    /// <summary>The resolved <c>ThaleTheGreat.UtilityGridRedux.ModEntry.ShouldBlockUnpoweredObject(Object, GameLocation)</c> method, or <c>null</c> if it couldn't be resolved (Utility Grid Redux isn't installed, or its internals no longer match — see <see cref="TryInitialize"/>).</summary>
    private static MethodInfo? ShouldBlockUnpoweredObjectMethod;

    /// <summary>The resolved <c>ThaleTheGreat.UtilityGridRedux.ModEntry.GetRuleKey(Object)</c> method, used only by <see cref="IsTrackedAsConsumer"/> — see that method's own remarks.</summary>
    private static MethodInfo? GetRuleKeyMethod;

    /// <summary>The resolved <c>ThaleTheGreat.UtilityGridRedux.ModEntry.ObjectNeedsPower(UtilityObjectRule)</c> method, used only by <see cref="IsTrackedAsConsumer"/>.</summary>
    private static MethodInfo? ObjectNeedsPowerMethod;

    /// <summary>A cached reference to Utility Grid Redux's own private static <c>ObjectRules</c> dictionary, used only by <see cref="IsTrackedAsConsumer"/> — safe to cache the reference itself (rather than re-resolving the field every call) since entries are added to this SAME dictionary instance over time, never replaced with a new one.</summary>
    private static IDictionary? CachedObjectRules;

    /// <summary>Used to log a rate-limited warning if a resolved call fails at runtime — set via <see cref="TryInitialize"/>.</summary>
    private static IMonitor? Monitor;

    /// <summary>Queue a location for Automate to reload its own machine groups in soon — set via <see cref="TryInitialize"/>, invoked by <see cref="MarkDirty_Postfix"/>.</summary>
    private static Action<GameLocation>? QueueReload;

    /// <summary>
    /// MOD: added. Per-object, purely LOCAL cache of the last-observed <see cref="IsStarved"/> result for
    /// a Utility-Grid-Redux-tracked object — lets <see cref="MarkDirty_Postfix"/> detect a REAL
    /// starved/powered TRANSITION instead of queuing a reload for the whole location on every single
    /// notification regardless of whether anything Automate actually cares about changed. Matches this
    /// fork's own existing <see cref="PowerSiloSystem.RefreshCoilAllowance"/> precedent, which likewise
    /// only reports locations where a Power Coil's OWN powered state actually flipped, not every location
    /// that happens to contain one. Keyed via <see cref="ConditionalWeakTable{TKey,TValue}"/> (the same
    /// pattern <see cref="Patches.ChestLidAnimationPatches"/>/<see cref="Patches.AutoCrafterPatches"/> use
    /// for their own per-instance local state) so an entry for an object that stops existing is reclaimed
    /// by the GC on its own.
    /// </summary>
    private static readonly ConditionalWeakTable<SObject, LastKnownState> LastKnownStarvedStates = new();

    /// <summary>MOD: added. Per-object mutable state tracked by <see cref="LastKnownStarvedStates"/>.</summary>
    private sealed class LastKnownState
    {
        /// <summary>The <see cref="IsStarved"/> result this object had the last time <see cref="MarkDirty_Postfix"/> checked it.</summary>
        public bool IsStarved;
    }


    /*********
    ** Accessors
    *********/
    /// <summary>Whether Utility Grid Redux was detected and the READ-side check was successfully resolved. When <c>false</c>, <see cref="IsStarved"/> always returns <c>false</c> — i.e. this mechanic behaves as if it doesn't exist. Independent of whether the WRITE-side Power Coil registration succeeded.</summary>
    public static bool IsEnabled => UtilityGridReduxSystem.ShouldBlockUnpoweredObjectMethod != null;


    /*********
    ** Public methods
    *********/
    /// <summary>
    /// Resolve Utility Grid Redux's own internals by reflection, if it's installed, and attempt both the
    /// read-side power check and the write-side Power Coil generator registration — each independently,
    /// so one failing doesn't affect the other. Safe to call unconditionally — a complete, silent no-op
    /// if Utility Grid Redux isn't loaded at all (the normal case for most Automate users). Must be
    /// called from <c>GameLaunched</c>, never from <c>Entry</c>.
    /// </summary>
    /// <param name="modRegistry">Used to check whether Utility Grid Redux is installed.</param>
    /// <param name="monitor">Used to log a warning if Utility Grid Redux is installed but couldn't be fully resolved, and later to log a rate-limited warning for a runtime call failure.</param>
    /// <param name="powerCoilQualifiedItemId">Automate's own Power Coil's qualified item ID — see <see cref="TryRegisterGenerator"/>. Passed in (rather than referenced directly) so this Framework-level class doesn't need to depend on <see cref="Patches.PowerCoilPatches"/>, matching this codebase's established Patches-depend-on-Framework direction.</param>
    /// <param name="powerCoilGeneratedPower">How much power a powered Power Coil should produce for Utility Grid Redux's own grid — see <see cref="Models.ModConfig.PowerCoilUtilityGridReduxPower"/>.</param>
    /// <param name="poweredChestQualifiedItemId">Automate's own Powered Chest's qualified item ID — same reasoning as <paramref name="powerCoilQualifiedItemId"/>.</param>
    /// <param name="poweredChestGeneratedPower">How much power a Powered Chest should produce for Utility Grid Redux's own grid, unconditionally — see <see cref="Models.ModConfig.PoweredChestUtilityGridReduxPower"/>.</param>
    /// <param name="harmony">Used to patch Utility Grid Redux's own "power state changed" notifications — see <see cref="TryHookPowerChangeNotifications"/>.</param>
    /// <param name="queueReload">Queue a location for Automate to reload its own machine groups in soon (i.e. <c>MachineManager.QueueReload</c>) — called whenever Utility Grid Redux itself notices a location's power state may have changed, so a machine that just gained Utility Grid Redux power is recognized by its automation group promptly, instead of waiting for the next periodic rescan.</param>
    /// <returns>Returns whether the read-side power check was successfully enabled (independent of whether either write-side registration, or the power-change hook, also succeeded).</returns>
    public static bool TryInitialize(IModRegistry modRegistry, IMonitor monitor, string powerCoilQualifiedItemId, int powerCoilGeneratedPower, string poweredChestQualifiedItemId, int poweredChestGeneratedPower, Harmony harmony, Action<GameLocation> queueReload)
    {
        UtilityGridReduxSystem.Monitor = monitor;
        UtilityGridReduxSystem.QueueReload = queueReload;

        if (!modRegistry.IsLoaded(UtilityGridReduxSystem.UtilityGridReduxModId))
            return false; // not installed — the normal case, no warning needed (mirrors StardioConveyorBeltPatches's own early-out)

        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(asm => asm.GetName().Name == "UtilityGridRedux");
        if (assembly == null)
        {
            monitor.Log("Utility Grid Redux is installed, but its assembly couldn't be found — Automate's own Utility Grid Redux integration is fully disabled.", LogLevel.Warn);
            return false;
        }

        Type? modEntryType = assembly.GetType("ThaleTheGreat.UtilityGridRedux.ModEntry");
        if (modEntryType == null)
        {
            monitor.Log("Utility Grid Redux is installed, but ThaleTheGreat.UtilityGridRedux.ModEntry wasn't found by reflection — Automate's own Utility Grid Redux integration is fully disabled.", LogLevel.Warn);
            return false;
        }

        bool readSideEnabled = UtilityGridReduxSystem.TryResolvePowerCheck(modEntryType, monitor);
        UtilityGridReduxSystem.TryResolveConsumerCheck(modEntryType, monitor);
        UtilityGridReduxSystem.TryHookPowerChangeNotifications(harmony, modEntryType, monitor);

        // MOD: added — the Power Coil only counts while IT ITSELF is powered (MustBeOn, mirrored onto
        // its real IsOn field by PowerSiloSystem/PowerSiloPatches). The Powered Chest has no equivalent
        // "am I currently powered" concept anywhere in Automate — it's always a local power source the
        // instant it's placed (see Models.ModConfig.LocalPowerSourceNames's own remarks) — so it's
        // registered unconditionally instead.
        UtilityGridReduxSystem.TryRegisterGenerator(assembly, modEntryType, powerCoilQualifiedItemId, powerCoilGeneratedPower, mustBeOn: true, label: "Power Coil", monitor);
        UtilityGridReduxSystem.TryRegisterGenerator(assembly, modEntryType, poweredChestQualifiedItemId, poweredChestGeneratedPower, mustBeOn: false, label: "Powered Chest", monitor);

        return readSideEnabled;
    }

    /// <summary>Get whether Utility Grid Redux itself considers the given object power/water-starved right now. Always returns <c>false</c> if Utility Grid Redux isn't installed, couldn't be fully resolved, or its own check throws.</summary>
    /// <param name="obj">The object to check.</param>
    /// <param name="location">The object's current location.</param>
    public static bool IsStarved(SObject obj, GameLocation location)
    {
        MethodInfo? method = UtilityGridReduxSystem.ShouldBlockUnpoweredObjectMethod;
        if (method == null)
            return false;

        try
        {
            return (bool)method.Invoke(null, [obj, location])!;
        }
        catch (Exception ex)
        {
            UtilityGridReduxSystem.Monitor?.LogOnce($"Utility Grid Redux's own power check threw an unexpected error ({ex.GetType().Name}: {ex.Message}) — treating affected machines as not blocked by it until the game restarts, to avoid crashing Automate.", LogLevel.Warn);
            return false;
        }
    }

    /// <summary>
    /// MOD: added. Get whether Utility Grid Redux itself considers the given object a power/water
    /// CONSUMER at all — i.e. whether it has a registered rule for this object AND that rule needs
    /// power or water (as opposed to no rule existing, or the object being a pure PRODUCER like
    /// Automate's own registered Power Coil/Powered Chest rules). Used by <see cref="Patches.PowerRequiredMachinePatches"/>
    /// and <see cref="MachineGroupFactory"/> to decide whether Utility Grid Redux's own answer
    /// (<see cref="IsStarved"/>) should be AUTHORITATIVE for this specific object — i.e. sufficient on
    /// its own, without ALSO needing Automate's own Power-Coil-range check to agree.
    ///
    /// MOD: fixed — the two call sites originally combined the two gates with a plain OR-of-starved
    /// (<c>starved = AutomateStarved() || UgrStarved()</c>), which sounds independent but is actually
    /// the same as requiring BOTH to say "powered" for a machine to actually work — so a machine Utility
    /// Grid Redux itself was successfully powering still showed as starved if it also happened to be
    /// listed in Automate's own <see cref="Models.ModConfig.PowerRequiredMachineNames"/> and outside
    /// Power Coil range. Confirmed directly via user report. Now, for a machine this method says IS
    /// Utility-Grid-Redux-tracked, ITS answer alone decides the outcome — connecting it to Utility Grid
    /// Redux's own grid is sufficient on its own, the same way connecting it to an Automate Power Coil
    /// already was for a machine Utility Grid Redux doesn't track at all.
    ///
    /// Reflects into 2 more members than <see cref="IsStarved"/> alone needs (<c>GetRuleKey</c>,
    /// <c>ObjectNeedsPower</c>, plus the already-cached <c>ObjectRules</c> dictionary) — unavoidable,
    /// since Utility Grid Redux's own <c>ShouldBlockUnpoweredObject</c> returns a single bool that can't
    /// distinguish "not tracked at all" from "tracked and currently satisfied," and this method needs
    /// that distinction specifically. Fails safe exactly like everything else in this class: if any of
    /// these can't be resolved, this always returns <c>false</c> (never treat anything as
    /// Utility-Grid-Redux-tracked), which falls back to Automate's own pre-existing behavior for every
    /// machine — never a crash, never a wrong "always powered" outcome.
    /// </summary>
    /// <param name="obj">The object to check.</param>
    public static bool IsTrackedAsConsumer(SObject obj)
    {
        if (UtilityGridReduxSystem.GetRuleKeyMethod is not { } getRuleKeyMethod || UtilityGridReduxSystem.ObjectNeedsPowerMethod is not { } objectNeedsPowerMethod || UtilityGridReduxSystem.CachedObjectRules is not { } objectRules)
            return false;

        try
        {
            if (getRuleKeyMethod.Invoke(null, [obj]) is not string ruleKey || !objectRules.Contains(ruleKey))
                return false;

            object? rule = objectRules[ruleKey];
            return rule != null && (bool)objectNeedsPowerMethod.Invoke(null, [rule])!;
        }
        catch (Exception ex)
        {
            UtilityGridReduxSystem.Monitor?.LogOnce($"Utility Grid Redux's own rule lookup threw an unexpected error ({ex.GetType().Name}: {ex.Message}) — treating affected machines as not tracked by it until the game restarts, to avoid crashing Automate.", LogLevel.Warn);
            return false;
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// Resolve the READ side — <c>ShouldBlockUnpoweredObject</c> — the single, side-effect-free
    /// predicate Utility Grid Redux's own Harmony self-patch (on <c>Object.MinutesElapsed</c>/
    /// <c>DayUpdate</c>) already calls to pause an unpowered machine's countdown. Deliberately calls this
    /// ONE method directly rather than reimplementing its internal chain (resolve a rule key, look it
    /// up, check whether the rule needs power/water, refresh the grid cache, check the live per-tile
    /// answer): confirmed by reading its full body, it ALSO respects Utility Grid Redux's own "Enable
    /// Mod"/"Enable Power Rules" toggles (live-editable via Generic Mod Config Menu) and its own
    /// multiplayer host guard (never blocks on a farmhand's own client, since its grid simulation is
    /// host-authoritative) — a manual reimplementation would silently ignore both, making Automate's
    /// answer drift from Utility Grid Redux's own even though the entire point of reflecting into it is
    /// to never drift. It also means exactly ONE reflected member instead of several, which matters
    /// directly for "don't crash if it changes" — fewer bound members means fewer places a future
    /// Utility Grid Redux update can silently break this.
    /// </summary>
    /// <param name="modEntryType">Utility Grid Redux's own resolved <c>ModEntry</c> type.</param>
    /// <param name="monitor">Used to log a warning if the method can't be resolved.</param>
    private static bool TryResolvePowerCheck(Type modEntryType, IMonitor monitor)
    {
        MethodInfo? method = AccessTools.Method(modEntryType, "ShouldBlockUnpoweredObject", [typeof(SObject), typeof(GameLocation)]);
        if (method == null || method.ReturnType != typeof(bool))
        {
            monitor.Log("Utility Grid Redux is installed, but ModEntry.ShouldBlockUnpoweredObject(Object, GameLocation) wasn't found by reflection — machines it powers won't be gated on its own power state by Automate.", LogLevel.Warn);
            return false;
        }

        UtilityGridReduxSystem.ShouldBlockUnpoweredObjectMethod = method;
        monitor.VerboseLog("Detected Utility Grid Redux — machines it powers will also be gated on its own power state.");
        return true;
    }

    /// <summary>Resolve the members <see cref="IsTrackedAsConsumer"/> needs — see that method's own remarks for why it needs more than <see cref="TryResolvePowerCheck"/> alone.</summary>
    /// <param name="modEntryType">Utility Grid Redux's own resolved <c>ModEntry</c> type.</param>
    /// <param name="monitor">Used to log a warning if a member can't be resolved.</param>
    private static void TryResolveConsumerCheck(Type modEntryType, IMonitor monitor)
    {
        MethodInfo? getRuleKeyMethod = AccessTools.Method(modEntryType, "GetRuleKey", [typeof(SObject)]);
        MethodInfo? objectNeedsPowerMethod = AccessTools.Method(modEntryType, "ObjectNeedsPower");
        FieldInfo? objectRulesField = AccessTools.Field(modEntryType, "ObjectRules");

        if (getRuleKeyMethod == null || getRuleKeyMethod.ReturnType != typeof(string)
            || objectNeedsPowerMethod == null || objectNeedsPowerMethod.ReturnType != typeof(bool)
            || objectRulesField?.GetValue(null) is not IDictionary objectRules)
        {
            monitor.Log("Utility Grid Redux is installed, but its internal rule-lookup members weren't found by reflection — a machine it powers won't be recognized as powered unless it's ALSO within Automate's own Power Coil range.", LogLevel.Warn);
            return;
        }

        UtilityGridReduxSystem.GetRuleKeyMethod = getRuleKeyMethod;
        UtilityGridReduxSystem.ObjectNeedsPowerMethod = objectNeedsPowerMethod;
        UtilityGridReduxSystem.CachedObjectRules = objectRules;
    }

    /// <summary>
    /// MOD: added. Patches Utility Grid Redux's own internal <c>MarkGroupsDirty(string, GridKind)</c> and
    /// <c>MarkPowerCacheDirty(string, GridKind)</c> — confirmed by reading the decompiled source, these
    /// are the two places Utility Grid Redux itself invalidates a location's cached power state (called
    /// on a pipe being placed/destroyed, a relevant object being added/removed, a storage tank charging
    /// or discharging, and a config reload — never on a per-tick basis) — with a shared postfix that
    /// tells Automate's own <c>MachineManager.QueueReload</c> to recheck that same location soon.
    ///
    /// MOD: fixed — without this, a machine gaining Utility Grid Redux power was correctly recognized by
    /// <see cref="IsStarved"/> the INSTANT anything asked (the live-interaction icon/blocking patches in
    /// <see cref="Patches.PowerRequiredMachinePatches"/> already re-check on every draw/interaction) —
    /// but the machine's own automation GROUP membership (computed once per rebuild by
    /// <see cref="MachineGroupFactory.AddToBuilder"/>, not re-checked continuously) stayed stale until
    /// Automate's own next periodic rescan, since nothing told it a Utility-Grid-Redux-sourced power
    /// change had happened — unlike placing/removing an Automate Power Coil, which already triggers an
    /// immediate rebuild via this fork's own existing placement/removal patches. Confirmed directly via
    /// user report: a chest feeding a newly-Utility-Grid-Redux-powered machine just sat there, items not
    /// moving, until the next backstop scan caught up. <c>QueueReload</c> is processed once per game tick
    /// (not hourly), so this closes that gap to effectively the same responsiveness the Power Coil path
    /// already has.
    ///
    /// Resolved by NAME only (no parameter-type array), since <c>GridKind</c> is itself an internal type
    /// this project has no compile-time reference to and doesn't need one for — the postfix below only
    /// reads the first (<c>string</c>) parameter by name, Harmony doesn't require declaring every
    /// original parameter. Fails safe like everything else here: if either method can't be resolved, this
    /// piece alone stays disabled (an Automate Power-Coil-based rebuild still fires normally regardless;
    /// a Utility-Grid-Redux-sourced power change just falls back to the next periodic rescan instead of
    /// being instant) — nothing else in this class is affected.
    /// </summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    /// <param name="modEntryType">Utility Grid Redux's own resolved <c>ModEntry</c> type.</param>
    /// <param name="monitor">Used to log a warning if a method can't be resolved.</param>
    private static void TryHookPowerChangeNotifications(Harmony harmony, Type modEntryType, IMonitor monitor)
    {
        MethodInfo? markGroupsDirtyMethod = AccessTools.Method(modEntryType, "MarkGroupsDirty");
        MethodInfo? markPowerCacheDirtyMethod = AccessTools.Method(modEntryType, "MarkPowerCacheDirty");

        if (markGroupsDirtyMethod == null || markPowerCacheDirtyMethod == null)
        {
            monitor.Log("Utility Grid Redux is installed, but its internal power-change notifications weren't found by reflection — a machine gaining Utility Grid Redux power won't be recognized by its automation group until the next periodic rescan, instead of instantly.", LogLevel.Warn);
            return;
        }

        HarmonyMethod postfix = new(typeof(UtilityGridReduxSystem), nameof(UtilityGridReduxSystem.MarkDirty_Postfix));
        harmony.Patch(markGroupsDirtyMethod, postfix: postfix);
        harmony.Patch(markPowerCacheDirtyMethod, postfix: postfix);
    }

    /// <summary>
    /// Shared postfix for <see cref="TryHookPowerChangeNotifications"/>. Rather than queuing the affected
    /// location for reload unconditionally on every notification (Utility Grid Redux marks a location
    /// dirty for plenty of reasons Automate doesn't care about at all — a Solar Generator's own charge
    /// level ticking up, an unrelated pipe edit on the far side of the map), this re-checks every
    /// currently-placed object in the location that <see cref="IsTrackedAsConsumer"/> says Utility Grid
    /// Redux tracks, and only queues a reload if AT LEAST ONE of them actually flipped between starved
    /// and powered since the last time this ran — matching how <see cref="PowerSiloSystem.RefreshCoilAllowance"/>
    /// already only reports genuinely-changed locations for the Power-Coil side of this same mechanic.
    /// A location with no Utility-Grid-Redux-tracked objects at all (the common case for most locations,
    /// most of the time) costs one cheap iteration over its placed objects and nothing more.
    /// </summary>
    /// <param name="locationName">The location whose power state Utility Grid Redux just marked as needing recalculation — the first parameter of both patched methods, matched by name (their second parameter, an internal <c>GridKind</c> enum, is deliberately not declared here since it isn't needed).</param>
    private static void MarkDirty_Postfix(string locationName)
    {
        try
        {
            GameLocation? location = Game1.getLocationFromName(locationName) ?? Game1.getLocationFromName(locationName, true);
            if (location == null)
                return;

            bool anyChanged = false;
            foreach (SObject obj in location.objects.Values)
            {
                if (!UtilityGridReduxSystem.IsTrackedAsConsumer(obj))
                    continue;

                bool isStarved = UtilityGridReduxSystem.IsStarved(obj, location);

                if (!UtilityGridReduxSystem.LastKnownStarvedStates.TryGetValue(obj, out LastKnownState? state))
                {
                    // MOD: added — first sight of this specific object is treated as "changed" defensively,
                    // so a genuine transition landing on the very first check isn't missed. In practice a
                    // brand new object is normally already caught by Automate's own separate "world
                    // changed" triggers anyway, so this rarely does any extra work on its own.
                    UtilityGridReduxSystem.LastKnownStarvedStates.Add(obj, new LastKnownState { IsStarved = isStarved });
                    anyChanged = true;
                }
                else if (state.IsStarved != isStarved)
                {
                    state.IsStarved = isStarved;
                    anyChanged = true;
                }
            }

            if (anyChanged)
                UtilityGridReduxSystem.QueueReload?.Invoke(location);
        }
        catch (Exception ex)
        {
            UtilityGridReduxSystem.Monitor?.LogOnce($"Failed to check for Utility Grid Redux power changes ({ex.GetType().Name}: {ex.Message}) — affected machines may not update until the next periodic rescan.", LogLevel.Warn);
        }
    }

    /// <summary>
    /// MOD: added. One-time registration of one of Automate's own objects (the Power Coil, or the
    /// Powered Chest) as a Utility Grid Redux power PRODUCER — the reverse direction from
    /// <see cref="IsStarved"/>, which only ever READS Utility Grid Redux's own state. Reaches directly
    /// into Utility Grid Redux's own private static <c>ObjectRules</c> dictionary (a
    /// <c>Dictionary&lt;string, UtilityObjectRule&gt;</c>, manipulated here through the non-generic
    /// <see cref="System.Collections.IDictionary"/> so this project never needs a compile-time reference
    /// to the internal <c>UtilityObjectRule</c> type) and inserts one new entry — the EXACT same
    /// technique its own addon mod ("Industrialization for Utility Grid Redux") already uses to register
    /// its own machines with it (confirmed working by decompiling both assemblies).
    ///
    /// This is a genuine WRITE into another mod's private runtime state — more invasive than the
    /// read-only check above — but it only ever runs ONCE, at startup, per object type; Utility Grid
    /// Redux's own existing, UNMODIFIED scanning logic (<c>AddObjectsToGrid</c>) does everything else
    /// automatically from then on, for every matching object placed on one of ITS OWN pipe tiles
    /// (confirmed: Utility Grid Redux only ever recognizes an object sitting directly on a pipe tile
    /// belonging to one of its own pipe groups — true for every object type it tracks, not a special new
    /// constraint here), for the rest of the game session.
    ///
    /// When <paramref name="mustBeOn"/> is <c>true</c>, the registered rule's <c>MustBeOn</c> is set —
    /// confirmed by reading Utility Grid Redux's own <c>ConditionsAllowObject</c> directly:
    /// <c>if ((!rule.MustBeOn || worldObject.IsOn) &amp;&amp; ...)</c>, i.e. a real, ordinary
    /// <see cref="SObject.IsOn"/> check, the same condition its own generators (e.g. a Solar Generator)
    /// already use. <see cref="Patches.PowerCoilPatches"/>/<see cref="PowerSiloSystem"/> mirror a Power
    /// Coil's own Automate-computed powered state onto that exact same real field for exactly this
    /// reason — a completely ordinary vanilla field Utility Grid Redux was already going to check
    /// regardless of whether Automate is installed, so there's no new coupling between the two mods
    /// beyond that one shared field. A Powered Chest has no equivalent state to mirror at all (see
    /// <see cref="Models.ModConfig.PoweredChestUtilityGridReduxPower"/>'s own remarks), so it's
    /// registered with <paramref name="mustBeOn"/> <c>false</c> — an unconditional producer.
    /// </summary>
    /// <param name="assembly">Utility Grid Redux's own already-loaded assembly.</param>
    /// <param name="modEntryType">Utility Grid Redux's own resolved <c>ModEntry</c> type.</param>
    /// <param name="qualifiedItemId">The object's own qualified item ID — the key this rule is registered under, matching exactly what Utility Grid Redux's own <c>GetRuleKey</c> resolves for a real placed instance (confirmed: it tries an unqualified suffix, then the full qualified ID, then the raw name, each gated on already existing in the dictionary — registering under the full qualified ID up front means that's the checkpoint that matches).</param>
    /// <param name="generatedPower">How much power this object should produce.</param>
    /// <param name="mustBeOn">Whether the rule should only count while the object's own real <see cref="SObject.IsOn"/> field is <c>true</c>.</param>
    /// <param name="label">A human-readable name for this object, used only in log messages.</param>
    /// <param name="monitor">Used to log a warning if registration fails.</param>
    private static void TryRegisterGenerator(Assembly assembly, Type modEntryType, string qualifiedItemId, int generatedPower, bool mustBeOn, string label, IMonitor monitor)
    {
        Type? ruleType = assembly.GetType("ThaleTheGreat.UtilityGridRedux.UtilityObjectRule");
        FieldInfo? objectRulesField = AccessTools.Field(modEntryType, "ObjectRules");

        if (ruleType == null || objectRulesField?.GetValue(null) is not IDictionary objectRules)
        {
            monitor.Log($"Utility Grid Redux is installed, but its internal object-rule registry wasn't found by reflection — the {label} won't produce power for its grid.", LogLevel.Warn);
            return;
        }

        try
        {
            object rule = Activator.CreateInstance(ruleType, nonPublic: true)!;
            ruleType.GetProperty("Power")?.SetValue(rule, (float)generatedPower);
            if (mustBeOn)
                ruleType.GetProperty("MustBeOn")?.SetValue(rule, true);

            objectRules[qualifiedItemId] = rule;
            monitor.VerboseLog($"Registered Automate's {label} with Utility Grid Redux as a {generatedPower}-power generator{(mustBeOn ? " (only while powered)" : "")}.");
        }
        catch (Exception ex)
        {
            monitor.Log($"Failed to register Automate's {label} with Utility Grid Redux ({ex.GetType().Name}: {ex.Message}) — it won't produce power for its grid.", LogLevel.Warn);
        }
    }
}

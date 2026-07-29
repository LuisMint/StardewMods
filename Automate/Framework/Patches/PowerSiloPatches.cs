using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patches (plus one plain event-driven entry point, <see cref="OnObjectListChanged"/>)
/// for the Power Silo capacity mechanic's coil- and Solar-Panel-side triggers:
/// <list type="bullet">
/// <item>a Power Coil being placed (stamps its placement-order timestamp, then refreshes the whole
/// save's coil allowance) or destroyed (just refreshes the allowance, since one fewer coil can let the
/// next-oldest excess one regain power) — handled here via Harmony postfixes, since both need data
/// (<c>__result</c>, the coil's own post-refresh powered/not state) that isn't available any other way;</item>
/// <item>a Power Coil, Solar Panel, OR local power source (e.g. a Powered Chest — see
/// <see cref="IsLocalPowerSource"/>'s remarks) being placed or destroyed, changing the solar tier's
/// connected-panel bonus (see <see cref="PowerSiloSystem.RefreshConnectedSolarPanelCount"/>) and
/// therefore the whole save's total capacity — handled by <see cref="OnObjectListChanged"/> instead of
/// a Harmony postfix on purpose (see its own remarks for why), re-stamping coil allowance too and
/// showing a delta-specific popup only when that total actually changed.</item>
/// </list>
/// Kept separate from <see cref="PowerCoilPatches"/> (which is purely about the Power Coil's own
/// visuals, per its own remarks) since this is really about the Power Silo mechanic, not the coil
/// itself — the coil/panel are just where these triggers naturally occur. See
/// <see cref="PowerSiloSystem"/>'s own remarks for the full list of triggers, including the Silo-side
/// ones (built/destroyed/fed a new tier) handled in <see cref="MachineManager"/>.
/// </summary>
internal static class PowerSiloPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the item names/IDs that count as a Power Coil for capacity purposes.</summary>
    private static Func<HashSet<string>>? GetSourceNames;

    /// <summary>MOD: added. Get the item names/IDs that count as a Solar Panel for the solar tier's connected-panel bonus.</summary>
    private static Func<HashSet<string>>? GetSolarPanelNames;

    /// <summary>MOD: added. Get the item names/IDs that count as a "local" power source (e.g. the Powered Chest) — see <see cref="IsLocalPowerSource"/>'s remarks for why <see cref="OnObjectListChanged"/> needs to know about these too.</summary>
    private static Func<HashSet<string>>? GetLocalSourceNames;

    /// <summary>The power silo capacity system, used to refresh every coil's allowance after a placement/destruction.</summary>
    private static PowerSiloSystem? PowerSiloSystem;


    /*********
    ** Public methods
    *********/
    /// <summary>Prepare this patch class before <see cref="Apply"/> is called.</summary>
    /// <param name="getSourceNames">Get the item names/IDs that count as a Power Coil for capacity purposes.</param>
    /// <param name="getSolarPanelNames">MOD: added. Get the item names/IDs that count as a Solar Panel for the solar tier's connected-panel bonus.</param>
    /// <param name="getLocalSourceNames">MOD: added. Get the item names/IDs that count as a "local" power source (e.g. the Powered Chest).</param>
    /// <param name="powerSiloSystem">The power silo capacity system, used to refresh every coil's allowance after a placement/destruction.</param>
    public static void Initialize(Func<HashSet<string>> getSourceNames, Func<HashSet<string>> getSolarPanelNames, Func<HashSet<string>> getLocalSourceNames, PowerSiloSystem powerSiloSystem)
    {
        PowerSiloPatches.GetSourceNames = getSourceNames;
        PowerSiloPatches.GetSolarPanelNames = getSolarPanelNames;
        PowerSiloPatches.GetLocalSourceNames = getLocalSourceNames;
        PowerSiloPatches.PowerSiloSystem = powerSiloSystem;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        // MOD: a second, independent patch on the same methods PowerCoilPatches already patches —
        // Harmony allows multiple unrelated postfixes on one original method, so this doesn't conflict
        // with that class's own placement/breaking-sound postfixes.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.placementAction), [typeof(GameLocation), typeof(int), typeof(int), typeof(Farmer)]),
            postfix: new HarmonyMethod(typeof(PowerSiloPatches), nameof(PlacementAction_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.performToolAction)),
            postfix: new HarmonyMethod(typeof(PowerSiloPatches), nameof(PerformToolAction_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.checkForAction)),
            postfix: new HarmonyMethod(typeof(PowerSiloPatches), nameof(CheckForAction_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get whether an object counts as a Power Coil for capacity purposes.</summary>
    /// <param name="obj">The object to check.</param>
    private static bool IsPowerCoil(SObject obj)
    {
        HashSet<string>? sourceNames = PowerSiloPatches.GetSourceNames?.Invoke();
        return sourceNames is not null && (sourceNames.Contains(obj.QualifiedItemId) || sourceNames.Contains(obj.Name));
    }

    /// <summary>MOD: added. Get whether an object counts as a Solar Panel for the solar tier's connected-panel bonus.</summary>
    /// <param name="obj">The object to check.</param>
    private static bool IsSolarPanel(SObject obj)
    {
        HashSet<string>? solarPanelNames = PowerSiloPatches.GetSolarPanelNames?.Invoke();
        return solarPanelNames is not null && (solarPanelNames.Contains(obj.QualifiedItemId) || solarPanelNames.Contains(obj.Name));
    }

    /// <summary>
    /// MOD: added. Get whether an object counts as a "local" power source (e.g. the Powered Chest) —
    /// these ALSO affect which tiles <see cref="PowerSystem.GetPoweredTiles"/> treats as powered (their
    /// own fixed plus-shaped range, same as a Power Coil's own range), so placing/removing one can just
    /// as easily bring a Solar Panel into or out of range as a Power Coil can. This was the actual gap
    /// behind a real report: removing a Powered Chest with Solar Panels connected didn't update the
    /// connected-panel count at all until some UNRELATED Power Coil event happened to trigger a refresh
    /// — <see cref="OnObjectListChanged"/> only ever checked <see cref="IsPowerCoil"/>/<see cref="IsSolarPanel"/>,
    /// so a Powered Chest's own placement/removal was never treated as a relevant change in the first
    /// place.
    /// </summary>
    /// <param name="obj">The object to check.</param>
    private static bool IsLocalPowerSource(SObject obj)
    {
        HashSet<string>? localSourceNames = PowerSiloPatches.GetLocalSourceNames?.Invoke();
        return localSourceNames is not null && (localSourceNames.Contains(obj.QualifiedItemId) || localSourceNames.Contains(obj.Name));
    }

    /// <summary>Stamp the placement time on a newly-placed Power Coil, refresh the whole save's coil allowance, then play a sound and show a capacity popup reflecting whether it actually ended up powered.</summary>
    /// <param name="__instance">The item being placed — see this method's own remarks for why this ISN'T actually the object that ends up in the world.</param>
    /// <param name="location">The location it was placed in.</param>
    /// <param name="x">The X pixel coordinate it was placed at.</param>
    /// <param name="y">The Y pixel coordinate it was placed at.</param>
    /// <param name="__result">Whether vanilla's own placement logic succeeded.</param>
    private static void PlacementAction_Postfix(SObject __instance, GameLocation location, int x, int y, bool __result)
    {
        if (!__result || PowerSiloPatches.PowerSiloSystem is not { } powerSiloSystem || !PowerSiloPatches.IsPowerCoil(__instance))
            return;

        // MOD: fixed — vanilla's own generic BigCraftable placement logic (see the tail end of
        // SObject.placementAction) doesn't add __instance itself to the location; it clones a NEW
        // object via getOne() and adds THAT clone to location.Objects instead, then discards __instance
        // (the originally-held item). Stamping/reading modData on __instance was therefore always
        // operating on an object that never actually exists in the world — RefreshCoilAllowance() scans
        // location.Objects.Values, so it never touched __instance's modData at all, meaning
        // PowerCoilPatches.IsPowered(__instance) below always hit its "missing = powered" fallback and
        // played "grunt" unconditionally regardless of actual capacity. It also meant the REAL placed
        // coil never got a placement-order stamp at all, defaulting it to "oldest" (long.MinValue) on
        // every single placement — silently breaking the oldest-coil-keeps-power ordering guarantee.
        // Looking the real object up by the tile it was placed on fixes both: that's the same instance
        // every other Silo/coil check (draw patches, RefreshCoilAllowance's own scan) already reads.
        if (!location.Objects.TryGetValue(new Vector2(x / 64, y / 64), out SObject? placedCoil) || !PowerSiloPatches.IsPowerCoil(placedCoil))
            return;

        placedCoil.modData[PowerSiloSystem.PlacementOrderModDataKey] = DateTime.UtcNow.Ticks.ToString();
        powerSiloSystem.RefreshCoilAllowance();

        // MOD: added — the refresh above just stamped this exact coil, so its own modData now
        // reflects whether it actually made it within capacity; play "grunt" only if it did, and stay
        // silent otherwise (no "cancel" or any other sound) rather than announce the miss audibly —
        // the capacity popup below already covers that. Both skipped if the mechanic itself is disabled
        // (nothing meaningful to report), even though the stamp/refresh above still runs so state stays
        // consistent for whenever it's re-enabled.
        if (powerSiloSystem.IsEnabled)
        {
            if (PowerCoilPatches.IsPowered(placedCoil))
                location.playSound("grunt");
            PowerSiloPatches.ShowCapacityPopup(powerSiloSystem);
        }

        // NOTE: this coil could ALSO have brought Solar Panels into (or, on removal via
        // PerformToolAction_Postfix, out of) its own powered range — but that's deliberately NOT
        // handled here; see OnObjectListChanged's remarks for why a Harmony postfix is the wrong place
        // for that specific check.
    }

    /// <summary>Refresh the whole save's coil allowance after a Power Coil is broken/removed by a tool, since one fewer coil can let the next-oldest excess one regain power.</summary>
    /// <param name="__instance">The item being hit by a tool.</param>
    private static void PerformToolAction_Postfix(SObject __instance)
    {
        if (PowerSiloPatches.PowerSiloSystem is not { } powerSiloSystem || !PowerSiloPatches.IsPowerCoil(__instance))
            return;

        powerSiloSystem.RefreshCoilAllowance();
    }

    /// <summary>MOD: added. Play a sound and show a capacity popup when a player interacts with a Power Coil directly.</summary>
    /// <param name="__instance">The object being interacted with.</param>
    /// <param name="justCheckingForActivity">Whether this is just a cursor-hover check rather than an actual click — no sound/popup should show for those.</param>
    private static void CheckForAction_Postfix(SObject __instance, bool justCheckingForActivity)
    {
        if (justCheckingForActivity || !PowerSiloPatches.IsPowerCoil(__instance) || PowerSiloPatches.PowerSiloSystem is not { IsEnabled: true } powerSiloSystem)
            return;

        // MOD: added — unlike PlacementAction_Postfix (deliberately silent on a miss, per request),
        // direct interaction keeps the grunt/cancel distinction, since the player is explicitly asking
        // about this exact coil's state rather than just placing it.
        __instance.Location?.playSound(PowerCoilPatches.IsPowered(__instance) ? "grunt" : "cancel");
        PowerSiloPatches.ShowCapacityPopup(powerSiloSystem);
    }

    /// <summary>
    /// MOD: added. Refresh the connected-Solar-Panel count (see <see cref="PowerSiloSystem.RefreshConnectedSolarPanelCount"/>)
    /// and, if that changes the total capacity a Silo at the solar tier is granting, re-stamp coil
    /// allowance to match and show a delta-specific popup (e.g. "Expanded Power Grid +1 : 3/9") —
    /// called from <see cref="ModEntry"/>'s <c>OnObjectListChanged</c> handler whenever a Power Coil OR
    /// Solar Panel was added to or removed from a location, rather than from a Harmony postfix on
    /// placement/tool-action directly. That's deliberate: <see cref="SObject.performToolAction(Tool)"/>'s
    /// own postfix fires BEFORE the game actually removes the broken object from
    /// <see cref="GameLocation.Objects"/> (the caller removes it afterward, once it sees a
    /// <c>true</c> result) — so a refresh triggered from that postfix would always see the about-to-be-
    /// removed coil/panel as still present, meaning removing one that was actually mattering never
    /// registered any change at all (and the callout for it would only ever show up ONE step late, on
    /// the NEXT coil/panel event, in whichever direction was actually stale). SMAPI's
    /// <c>ObjectListChanged</c> event instead fires with the location's object list already diffed
    /// against last tick — by the time it fires, an added object is truly there and a removed one is
    /// truly gone, so this always sees accurate, current state.
    /// </summary>
    /// <param name="powerSiloSystem">The power silo capacity system.</param>
    /// <param name="location">The location a Power Coil or Solar Panel was added to or removed from.</param>
    private static void RefreshSolarConnectionAndNotify(PowerSiloSystem powerSiloSystem, GameLocation location)
    {
        int before = powerSiloSystem.GetTotalCapacity();
        powerSiloSystem.RefreshConnectedSolarPanelCount();
        int after = powerSiloSystem.GetTotalCapacity();

        if (before == after)
            return;

        powerSiloSystem.RefreshCoilAllowance(); // MOD: added — the capacity changed, so which coils currently fit within it needs re-evaluating too
        location.playSound("give_gift");
        PowerSiloPatches.ShowCapacityPopup(powerSiloSystem, before, after);
    }

    /// <summary>
    /// MOD: added. Entry point called from <see cref="ModEntry"/>'s <c>OnObjectListChanged</c> handler —
    /// see <see cref="RefreshSolarConnectionAndNotify"/>'s remarks for why this (rather than a Harmony
    /// postfix) is where the solar-connectivity trigger lives. A no-op unless at least one of the
    /// added/removed objects is a Power Coil, Solar Panel, or local power source (e.g. a Powered Chest —
    /// see <see cref="IsLocalPowerSource"/>'s remarks), so unrelated object changes elsewhere in the
    /// location (crops harvested, items dropped, etc.) don't trigger a needless rescan.
    /// </summary>
    /// <param name="location">The location whose object list changed.</param>
    /// <param name="added">The objects added to the location.</param>
    /// <param name="removed">The objects removed from the location.</param>
    public static void OnObjectListChanged(GameLocation location, IEnumerable<SObject> added, IEnumerable<SObject> removed)
    {
        if (PowerSiloPatches.PowerSiloSystem is not { } powerSiloSystem)
            return;

        bool isRelevantChange = false;
        foreach (SObject obj in added)
        {
            if (PowerSiloPatches.IsPowerCoil(obj) || PowerSiloPatches.IsSolarPanel(obj) || PowerSiloPatches.IsLocalPowerSource(obj))
            {
                isRelevantChange = true;
                break;
            }
        }
        if (!isRelevantChange)
        {
            foreach (SObject obj in removed)
            {
                if (PowerSiloPatches.IsPowerCoil(obj) || PowerSiloPatches.IsSolarPanel(obj) || PowerSiloPatches.IsLocalPowerSource(obj))
                {
                    isRelevantChange = true;
                    break;
                }
            }
        }

        if (isRelevantChange)
            PowerSiloPatches.RefreshSolarConnectionAndNotify(powerSiloSystem, location);
    }

    /// <summary>
    /// MOD: added/changed. Show a capacity popup — a delta-specific "Expanded Power Grid +N/-N : X/Y"
    /// (e.g. "Expanded Power Grid +1 : 3/9") when <paramref name="capacityBefore"/> and
    /// <paramref name="capacityAfter"/> are given and differ, since what's actually notable then is HOW
    /// MUCH the grid just grew/shrank by (which can be more than 1 if several Solar Panels crossed the
    /// per-3 threshold at once) — or the flat "Power Grid: X/Y" otherwise (no before/after given at
    /// all, as from the plain coil-interaction trigger, or given but unchanged) — matching
    /// <see cref="PowerSiloMenu"/>'s own wording for the same number.
    ///
    /// MOD: made internal (not private) so <see cref="PowerSiloInteraction"/> can reuse the exact same
    /// delta-popup wording when a Silo reaches the solar tier — see its own remarks for why that moment
    /// can change total capacity just as much as any coil/panel/chest placement can.
    /// </summary>
    /// <param name="powerSiloSystem">The power silo capacity system.</param>
    /// <param name="capacityBefore">The total capacity just before this event, if this event could have changed it.</param>
    /// <param name="capacityAfter">The total capacity just after this event, if this event could have changed it.</param>
    internal static void ShowCapacityPopup(PowerSiloSystem powerSiloSystem, int? capacityBefore = null, int? capacityAfter = null)
    {
        (int totalCoils, int capacity) = powerSiloSystem.GetUsage();

        if (capacityBefore is { } before && capacityAfter is { } after && before != after)
        {
            int delta = after - before;
            string sign = delta > 0 ? "+" : "";
            Game1.addHUDMessage(new HUDMessage($"Expanded Power Grid {sign}{delta} : {totalCoils}/{capacity}", HUDMessage.newQuest_type));
            return;
        }

        Game1.addHUDMessage(new HUDMessage($"Power Grid: {totalCoils}/{capacity}", HUDMessage.newQuest_type));
    }
}

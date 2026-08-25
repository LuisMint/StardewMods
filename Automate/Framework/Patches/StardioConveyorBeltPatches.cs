using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Gates the "Stardio" mod's factory pieces on Automate's own power grid — even though none of
/// them are an <see cref="IMachine"/> Automate automates at all (Stardio moves items through them with its
/// own entirely separate logic). Covers two different shapes of "starved":
/// <list type="bullet">
/// <item>The 4 conveyor belt types and the Splitter each have their own per-tick update
/// (<c>beltUpdate</c>/<c>splitterUpdate</c>) — a starved one simply doesn't advance or push/pull items
/// this tick (<see cref="BeltUpdate_Prefix"/>/<see cref="SplitterUpdate_Prefix"/>), and a belt additionally
/// doesn't animate (<see cref="GetBeltAnim_Prefix"/>) or push/speed-boost the player standing on it
/// (<see cref="FarmerMovePosition_Postfix"/>).</item>
/// <item>The Filter, Inverted Filter, Bridge, and Warp Nexus have NO per-tick update of their own at all —
/// their whole function happens synchronously inside whichever belt/splitter's own <c>PushItem</c> call
/// routes an item through (or, for the Warp Nexus, pulls one back out of) them. Gating these means
/// refusing that routing/pulling outright (<see cref="RoutingTarget_Prefix"/>) rather than pausing a
/// countdown — the net effect is the same either way: an item just doesn't move through a starved one.</item>
/// </list>
/// Every one of the above also shows the same pulsing "no power" icon every other power-required machine
/// does (<see cref="Draw_Postfix"/>, reusing <see cref="PowerRequiredMachinePatches.DrawNoPowerIcon"/>
/// directly rather than duplicating that visual).
///
/// Each machine type gets its OWN <see cref="Models.ModConfig.PowerRequiredMachineNames"/> entry —
/// <c>"ConveyorBelt"</c>, <c>"FastConveyorBelt"</c>, <c>"TurboConveyorBelt"</c>,
/// <c>"TurboPushingConveyorBelt"</c>, <c>"Filter"</c>, <c>"InvertedFilter"</c>, <c>"Bridge"</c>,
/// <c>"Splitter"</c>, <c>"WarpNexus"</c> — derived from that type's own in-game DISPLAY name via
/// <see cref="BaseMachine.GetDefaultMachineId(string)"/>, exactly like every other entry in that list
/// resolves from a real machine's own name. An earlier version of this class used one hardcoded shared ID
/// for all four belt types instead, which — confirmed directly via user report — didn't match the
/// type-specific names a player would naturally type in, following the same pattern every other entry
/// already uses. (The Input Hub/Output Hub are a different animal entirely — plain BigCraftables, not part
/// of this custom class hierarchy at all, so they're already covered by the fully generic
/// <see cref="PowerRequiredMachinePatches"/> with no code in this class needed.)
///
/// Unlike every other patch class in this codebase, the patched methods here don't exist in any assembly
/// this project references at compile time — Stardio's own types are resolved entirely by reflection
/// against Stardio's own already-loaded assembly, and patched only if Stardio is actually installed. If
/// Stardio ever changes a method's shape, this fails safe: a clear startup warning is logged and gating for
/// that specific piece simply doesn't apply, rather than crashing Automate or mis-patching something else.
///
/// MOD: fixed — <see cref="TryApply"/> used to be called directly from <c>ModEntry.Entry</c>, alongside
/// every other patch class in this codebase. Every one of THOSE only ever patches vanilla game types, which
/// are always available the instant Automate's own assembly is loaded — but THIS class needs to reflect
/// into a DIFFERENT mod's already-loaded assembly, and SMAPI loads each mod's assembly and calls its own
/// <c>Entry</c> ONE MOD AT A TIME (in whatever order it resolves them), not "load every assembly first,
/// then call every Entry()". Automate happened to load and run its own <c>Entry</c> before Stardio's
/// assembly was loaded at all — confirmed via the SMAPI log, which showed Automate discovered several lines
/// before Stardio during the "Loading mods..." phase — so this silently never found Stardio's assembly and
/// never applied anything, with no warning logged either (the "not installed" early-out has no log message
/// of its own, since that's the normal case for the other 99% of users). Moved to run from
/// <c>ModEntry.OnGameLaunched</c> instead — SMAPI's own documented guarantee point where every mod's
/// <c>Entry</c> has already run, the standard place any mod checks another mod's presence.
/// </summary>
internal static class StardioConveyorBeltPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Stardio's own manifest unique ID.</summary>
    private const string StardioModId = "Jok.Stardio";

    /// <summary>
    /// MOD: added. The Input Hub's own qualified item ID — a plain BigCraftable (not part of the
    /// IBeltPushing hierarchy at all), gated by <see cref="PowerRequiredMachinePatches"/>'s fully generic
    /// system rather than anything else in this class. Used only to find it inside a building's interior
    /// from <see cref="TryPushToBuilding_Prefix"/> — see that method's own remarks for why this needs a
    /// live check here instead of just leaning on that generic system's own gating alone.
    /// </summary>
    private const string InputChestQualifiedItemId = "(BC)Jok.Stardio.InputChest";

    /// <summary>MOD: added. The Output Hub's own qualified item ID — the pull-side counterpart to <see cref="InputChestQualifiedItemId"/>, used by <see cref="TryPullFromBuilding_Prefix"/>.</summary>
    private const string OutputChestQualifiedItemId = "(BC)Jok.Stardio.OutputChest";

    /// <summary>
    /// MOD: added. Every belt-family class (all under the <c>Jok.Stardio</c> namespace) that needs
    /// patching, paired with its own in-game display name — run through
    /// <see cref="BaseMachine.GetDefaultMachineId(string)"/> to get that type's own
    /// <see cref="Models.ModConfig.PowerRequiredMachineNames"/> entry, the exact same derivation every
    /// other entry in that config field already goes through for a real machine's own name.
    /// </summary>
    private static readonly (string TypeName, string DisplayName)[] BeltTypes =
    [
        ("BeltItem", "Conveyor Belt"),
        ("BeltItem2", "Fast Conveyor Belt"),
        ("BeltItem3", "Turbo Conveyor Belt"),
        ("BeltItem4", "Turbo Pushing Conveyor Belt")
    ];

    /// <summary>
    /// MOD: added. The rest of Stardio's factory pieces (everything under <c>Jok.Stardio</c> besides the 4
    /// belt types above), paired with its own in-game display name the same way <see cref="BeltTypes"/> is.
    /// Unlike the belts, each of these types defines its OWN distinct <c>draw</c> override rather than
    /// sharing one common base — see <see cref="TryApply"/>'s own remarks on why <c>FilterItemInv</c> is
    /// deliberately excluded from that per-type draw patch despite being listed here (it still needs its
    /// own entry in <see cref="MachineTypeIdsByType"/>, just not its own <c>draw</c> patch).
    /// </summary>
    private static readonly (string TypeName, string DisplayName)[] OtherManagedTypes =
    [
        ("FilterItem", "Filter"),
        ("FilterItemInv", "Inverted Filter"),
        ("BridgeItem", "Bridge"),
        ("SplitterItem", "Splitter"),
        ("WarpItem", "Warp Nexus")
    ];

    /// <summary>Get the shared power-required-machines system, set via <see cref="Initialize"/>.</summary>
    private static Func<PowerRequiredMachineSystem>? GetSystem;

    /// <summary>Get the currently-powered tiles for a location (or <c>null</c> if the power system is disabled), set via <see cref="Initialize"/>.</summary>
    private static Func<GameLocation, IReadOnlySet<Vector2>?>? GetPoweredTiles;

    /// <summary>
    /// MOD: added. IBeltPushing's own private <c>TryPushToChest(Object outputTarget)</c>, resolved once in
    /// <see cref="TryApply"/> so <see cref="TryPushToBuilding_Prefix"/> can invoke it itself per Input Hub
    /// candidate — see that method's own remarks for why it needs to replicate Stardio's per-hub loop
    /// rather than just gating the whole <c>TryPushToBuilding</c> call.
    ///
    /// MOD: reverted 2026-08-22 — briefly replaced with an <see cref="AccessTools.MethodDelegate{DelegateType}"/>-bound
    /// fast delegate as a performance optimization, since <see cref="MethodInfo.Invoke(object, object[])"/>
    /// is slower. Confirmed via direct user report that the delegate-bound version stopped gating
    /// correctly (Input/Output Hub worked even while unpowered) — likely an open-instance-delegate binding
    /// mismatch against a private method on a runtime-only-known type from another mod's assembly, though
    /// this wasn't root-caused before reverting. Plain <see cref="MethodInfo.Invoke"/> is correct and was
    /// already confirmed working; this only runs for a belt actually adjacent to a building on top of
    /// Stardio's own already-throttled tick cadence, so the performance difference isn't worth the risk.
    /// </summary>
    private static MethodInfo? TryPushToChestMethod;

    /// <summary>MOD: added. BeltItem's own private <c>TryPullFromChest(Object inputObj)</c>, the pull-side counterpart to <see cref="TryPushToChestMethod"/>, used by <see cref="TryPullFromBuilding_Prefix"/>.</summary>
    private static MethodInfo? TryPullFromChestMethod;

    /// <summary>
    /// MOD: added. Each belt type's own CLR <see cref="Type"/> (reflected from Stardio's assembly, so this
    /// project never needs a compile-time reference to it) mapped to its own derived machine type ID — see
    /// <see cref="BeltTypes"/>'s own remarks. Populated once in <see cref="TryApply"/>. Every patched method
    /// below is shared across all 4 belt types (Harmony patches each type's own override separately, but
    /// they all point at the SAME static prefix/postfix method here), so this is how a single shared method
    /// tells which specific belt type — and therefore which config entry — a given instance is.
    /// </summary>
    private static readonly Dictionary<Type, string> MachineTypeIdsByType = new();


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the accessors needed to resolve which belts are power-starved. Must be called before <see cref="TryApply"/>.</summary>
    /// <param name="getSystem">Get the shared power-required-machines system.</param>
    /// <param name="getPoweredTiles">Get the currently-powered tiles for a location (or <c>null</c> if the power system is disabled).</param>
    public static void Initialize(Func<PowerRequiredMachineSystem> getSystem, Func<GameLocation, IReadOnlySet<Vector2>?> getPoweredTiles)
    {
        StardioConveyorBeltPatches.GetSystem = getSystem;
        StardioConveyorBeltPatches.GetPoweredTiles = getPoweredTiles;
    }

    /// <summary>
    /// Apply these patches, if Stardio is installed and its own methods can be resolved by reflection. Safe
    /// to call unconditionally — a complete no-op (no reflection, no Harmony patch) if Stardio isn't loaded
    /// at all. Must be called from <c>GameLaunched</c>, never from <c>Entry</c> — see this class's own
    /// remarks for why.
    /// </summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    /// <param name="modRegistry">Used to check whether Stardio is installed.</param>
    /// <param name="monitor">Used to log a warning if Stardio is installed but a piece of it couldn't be resolved by reflection.</param>
    /// <returns>Returns whether at least one belt type was successfully patched.</returns>
    public static bool TryApply(Harmony harmony, IModRegistry modRegistry, IMonitor monitor)
    {
        if (!modRegistry.IsLoaded(StardioConveyorBeltPatches.StardioModId))
            return false;

        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(asm => asm.GetName().Name == "Stardio");
        if (assembly == null)
        {
            monitor.Log("Stardio is installed, but its assembly couldn't be found — its conveyor belts won't respect Automate's power grid.", LogLevel.Warn);
            return false;
        }

        bool appliedAny = false;
        Type? baseBeltType = null;
        foreach ((string typeName, string displayName) in StardioConveyorBeltPatches.BeltTypes)
        {
            Type? beltType = assembly.GetType($"Jok.Stardio.{typeName}");
            if (beltType == null)
            {
                monitor.Log($"Stardio is installed, but Jok.Stardio.{typeName} wasn't found by reflection — that belt type won't respect Automate's power grid.", LogLevel.Warn);
                continue;
            }

            if (typeName == "BeltItem")
                baseBeltType = beltType; // MOD: added — draw() is only ever defined on this base type, see below

            StardioConveyorBeltPatches.MachineTypeIdsByType[beltType] = BaseMachine.GetDefaultMachineId(displayName);

            MethodInfo? updateMethod = AccessTools.Method(beltType, "beltUpdate", [typeof(bool)]);
            if (updateMethod != null)
            {
                harmony.Patch(updateMethod, prefix: new HarmonyMethod(typeof(StardioConveyorBeltPatches), nameof(StardioConveyorBeltPatches.BeltUpdate_Prefix)));
                appliedAny = true;
            }
            else
                monitor.Log($"Stardio is installed, but Jok.Stardio.{typeName}.beltUpdate wasn't found by reflection — that belt type won't respect Automate's power grid.", LogLevel.Warn);

            MethodInfo? animMethod = AccessTools.Method(beltType, "GetBeltAnim", []);
            if (animMethod != null)
                harmony.Patch(animMethod, prefix: new HarmonyMethod(typeof(StardioConveyorBeltPatches), nameof(StardioConveyorBeltPatches.GetBeltAnim_Prefix)));
            else
                monitor.Log($"Stardio is installed, but Jok.Stardio.{typeName}.GetBeltAnim wasn't found by reflection — that belt type's animation won't freeze while unpowered.", LogLevel.Warn);
        }

        // MOD: added — draw() is declared only on the base BeltItem class (confirmed by decompiling
        // Stardio.dll — none of the 3 subclasses override it, they inherit it unchanged, presumably since
        // it already reads each instance's own texture generically via ItemRegistry rather than needing a
        // per-type override), so ONE patch here covers every belt type's own no-power icon via normal
        // virtual dispatch — unlike beltUpdate/GetBeltAnim above, which genuinely need 4 separate patches
        // since each subclass overrides those with its own distinct method body.
        if (baseBeltType != null)
        {
            MethodInfo? drawMethod = AccessTools.Method(baseBeltType, "draw", [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]);
            if (drawMethod != null)
                harmony.Patch(drawMethod, postfix: new HarmonyMethod(typeof(StardioConveyorBeltPatches), nameof(StardioConveyorBeltPatches.Draw_Postfix)));
            else
                monitor.Log("Stardio is installed, but BeltItem.draw wasn't found by reflection — the no-power icon won't show on starved belts.", LogLevel.Warn);
        }

        // MOD: added — Stardio's own Farmer.MovePosition postfix (HarmonyPatches.Farmer_MovePosition_postfix
        // in its own code) unconditionally sets yVelocity/xVelocity/temporarySpeedBuff whenever the farmer
        // is standing on ANY belt tile, powered or not. This patch runs at Priority.Last specifically so it
        // executes AFTER that one regardless of patch registration order (Harmony runs a lower-priority
        // postfix later, as the outermost wrapper around every higher-priority one) — so a starved belt's
        // push/speed-boost effect can be reset back to neutral right after Stardio applies it, rather than
        // trying to prevent Stardio's own patch from running at all (not possible from a different mod's
        // patch on the same method).
        MethodInfo? movePositionMethod = AccessTools.Method(typeof(Farmer), "MovePosition");
        if (movePositionMethod != null)
        {
            harmony.Patch(
                movePositionMethod,
                postfix: new HarmonyMethod(typeof(StardioConveyorBeltPatches), nameof(StardioConveyorBeltPatches.FarmerMovePosition_Postfix)) { priority = Priority.Last }
            );
        }

        // MOD: added — the rest of Stardio's factory pieces (see OtherManagedTypes' own remarks). Each
        // gets its own MachineTypeIdsByType entry (needed by every gate below, and by RoutingTarget_Prefix
        // further down) and its own no-power icon patch — except FilterItemInv, which doesn't declare its
        // own draw() at all (confirmed by decompiling Stardio.dll — it inherits FilterItem's unchanged), so
        // patching "draw" reflected against FilterItemInv would just resolve back to the SAME MethodInfo
        // already patched for FilterItem, applying the same postfix to it a second time.
        Type? splitterType = null;
        foreach ((string typeName, string displayName) in StardioConveyorBeltPatches.OtherManagedTypes)
        {
            Type? machineType = assembly.GetType($"Jok.Stardio.{typeName}");
            if (machineType == null)
            {
                monitor.Log($"Stardio is installed, but Jok.Stardio.{typeName} wasn't found by reflection — it won't respect Automate's power grid.", LogLevel.Warn);
                continue;
            }

            if (typeName == "SplitterItem")
                splitterType = machineType; // MOD: added — needed below to patch its own splitterUpdate, the one type in this group with a per-tick update of its own

            StardioConveyorBeltPatches.MachineTypeIdsByType[machineType] = BaseMachine.GetDefaultMachineId(displayName);

            if (typeName == "FilterItemInv")
                continue; // MOD: added — see this loop's own remarks above; still gets its MachineTypeIdsByType entry, just no separate draw patch

            MethodInfo? drawMethod = AccessTools.Method(machineType, "draw", [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]);
            if (drawMethod != null)
                harmony.Patch(drawMethod, postfix: new HarmonyMethod(typeof(StardioConveyorBeltPatches), nameof(StardioConveyorBeltPatches.Draw_Postfix)));
            else
                monitor.Log($"Stardio is installed, but Jok.Stardio.{typeName}.draw wasn't found by reflection — the no-power icon won't show on a starved one.", LogLevel.Warn);
        }

        // MOD: added — the Splitter's own per-tick advance, the same shape as a belt's own beltUpdate.
        if (splitterType != null && StardioConveyorBeltPatches.PatchIfFound(harmony, splitterType, "splitterUpdate", [typeof(bool)], nameof(StardioConveyorBeltPatches.SplitterUpdate_Prefix), monitor))
            appliedAny = true;

        // MOD: added — gates routing an item THROUGH a starved Filter/Inverted Filter/Bridge, or STORING
        // one into a starved Warp Nexus. All four are private instance methods declared on the shared
        // IBeltPushing base class (see this class' own remarks for why they have no per-tick update of
        // their own to hook instead) — called as e.g. `this.TryPushToFilter(...)` from inside whichever
        // belt/splitter's own PushItem is doing the pushing, so `this` there is the PUSHER, not the
        // Filter/Bridge/Warp Nexus being routed through. The object actually being routed through is the
        // method's own first parameter instead — RoutingTarget_Prefix reads it positionally via Harmony's
        // `__0` binding, since Harmony requires a by-name prefix parameter to exist on every method it
        // patches, and TryPushToBridge's own `dir` parameter isn't declared `ref` while the filter
        // variants' are, which would otherwise need two near-duplicate prefixes just for that mismatch.
        Type? beltPushingType = assembly.GetType("Jok.Stardio.IBeltPushing");
        if (beltPushingType != null)
        {
            Type? directionType = beltPushingType.GetNestedType("Direction");
            if (directionType != null)
            {
                if (StardioConveyorBeltPatches.PatchIfFound(harmony, beltPushingType, "TryPushToFilter", [typeof(SObject), typeof(SObject).MakeByRefType(), directionType.MakeByRefType()], nameof(StardioConveyorBeltPatches.RoutingTarget_Prefix), monitor))
                    appliedAny = true;
                if (StardioConveyorBeltPatches.PatchIfFound(harmony, beltPushingType, "TryPushToFilterInverted", [typeof(SObject), typeof(SObject).MakeByRefType(), directionType.MakeByRefType()], nameof(StardioConveyorBeltPatches.RoutingTarget_Prefix), monitor))
                    appliedAny = true;
                if (StardioConveyorBeltPatches.PatchIfFound(harmony, beltPushingType, "TryPushToBridge", [typeof(SObject), typeof(SObject).MakeByRefType(), directionType], nameof(StardioConveyorBeltPatches.RoutingTarget_Prefix), monitor))
                    appliedAny = true;
            }
            else
                monitor.Log("Stardio is installed, but Jok.Stardio.IBeltPushing.Direction wasn't found by reflection — Filters/Inverted Filters/Bridges won't respect Automate's power grid.", LogLevel.Warn);

            if (StardioConveyorBeltPatches.PatchIfFound(harmony, beltPushingType, "TryPushToWarp", [typeof(SObject)], nameof(StardioConveyorBeltPatches.RoutingTarget_Prefix), monitor))
                appliedAny = true;

            // MOD: added — resolved here (not patched) so TryPushToBuilding_Prefix can invoke it itself
            // per Input Hub candidate, replicating Stardio's own per-hub fallback loop instead of gating
            // the whole TryPushToBuilding call — see that prefix's own remarks.
            StardioConveyorBeltPatches.TryPushToChestMethod = AccessTools.Method(beltPushingType, "TryPushToChest", [typeof(SObject)]);
            if (StardioConveyorBeltPatches.TryPushToChestMethod == null)
                monitor.Log("Stardio is installed, but Jok.Stardio.IBeltPushing.TryPushToChest wasn't found by reflection — a starved Input Hub sharing a building with a powered one may block the powered one too.", LogLevel.Warn);
        }
        else
            monitor.Log("Stardio is installed, but Jok.Stardio.IBeltPushing wasn't found by reflection — Filters/Inverted Filters/Bridges/Warp Nexuses won't respect Automate's power grid.", LogLevel.Warn);

        // MOD: added — the mirror image of TryPushToWarp above: gates PULLING an item back out of a
        // starved Warp Nexus. Declared on BeltItem itself rather than IBeltPushing, since only a belt (not
        // a Splitter/Filter/Bridge) ever reads out of one — reuses baseBeltType, already resolved above.
        if (baseBeltType != null)
        {
            if (StardioConveyorBeltPatches.PatchIfFound(harmony, baseBeltType, "TryPullFromWarp", [typeof(SObject)], nameof(StardioConveyorBeltPatches.RoutingTarget_Prefix), monitor))
                appliedAny = true;

            // MOD: added — resolved here (not patched) so TryPullFromBuilding_Prefix can invoke it itself
            // per Output Hub candidate — the pull-side counterpart to TryPushToChestDelegate above.
            StardioConveyorBeltPatches.TryPullFromChestMethod = AccessTools.Method(baseBeltType, "TryPullFromChest", [typeof(SObject)]);
            if (StardioConveyorBeltPatches.TryPullFromChestMethod == null)
                monitor.Log("Stardio is installed, but Jok.Stardio.BeltItem.TryPullFromChest wasn't found by reflection — a starved Output Hub sharing a building with a powered one may block the powered one too.", LogLevel.Warn);
        }

        // MOD: added — gates pushing an item INTO the Input Hub, and pulling one back OUT of the Output
        // Hub. Both take a Vector2 target tile (not the Hub object itself — Stardio finds it internally by
        // searching the building's interior, and MAY find several — e.g. two Input Hubs in the same shed),
        // so these get their own dedicated prefixes rather than sharing RoutingTarget_Prefix. See
        // TryPushToBuilding_Prefix's own remarks for why this can't just lean on PowerRequiredMachinePatches'
        // own generic Auto-Grabber-style chest-tag gating alone.
        if (beltPushingType != null && StardioConveyorBeltPatches.PatchIfFound(harmony, beltPushingType, "TryPushToBuilding", [typeof(Vector2)], nameof(StardioConveyorBeltPatches.TryPushToBuilding_Prefix), monitor))
            appliedAny = true;
        if (baseBeltType != null && StardioConveyorBeltPatches.PatchIfFound(harmony, baseBeltType, "TryPullFromBuilding", [typeof(Vector2)], nameof(StardioConveyorBeltPatches.TryPullFromBuilding_Prefix), monitor))
            appliedAny = true;

        return appliedAny;
    }

    /// <summary>Patch a reflected private method with a prefix by name, logging a warning and returning <c>false</c> instead of throwing if it can't be found.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    /// <param name="declaringType">The type declaring the method to patch.</param>
    /// <param name="methodName">The method's own name.</param>
    /// <param name="parameterTypes">The method's own parameter types, needed to disambiguate by reflection.</param>
    /// <param name="prefixName">The name of the static prefix method (on this class) to apply.</param>
    /// <param name="monitor">Used to log a warning if the method couldn't be resolved.</param>
    private static bool PatchIfFound(Harmony harmony, Type declaringType, string methodName, Type[] parameterTypes, string prefixName, IMonitor monitor)
    {
        MethodInfo? method = AccessTools.Method(declaringType, methodName, parameterTypes);
        if (method == null)
        {
            monitor.Log($"Stardio is installed, but {declaringType.Name}.{methodName} wasn't found by reflection — it won't respect Automate's power grid.", LogLevel.Warn);
            return false;
        }

        harmony.Patch(method, prefix: new HarmonyMethod(typeof(StardioConveyorBeltPatches), prefixName));
        return true;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get whether an object is a Stardio belt type this class knows about, and it's currently starved of power.</summary>
    /// <param name="obj">The object to check.</param>
    private static bool IsStarved(SObject obj)
    {
        if (!StardioConveyorBeltPatches.MachineTypeIdsByType.TryGetValue(obj.GetType(), out string? machineTypeId))
            return false;

        GameLocation? location = obj.Location;
        if (location == null)
            return false;

        IReadOnlySet<Vector2>? poweredTiles = StardioConveyorBeltPatches.GetPoweredTiles!(location);
        return StardioConveyorBeltPatches.GetSystem!().IsPowerStarved(StardioConveyorBeltPatches.GetCandidateIds(obj, machineTypeId), [obj.TileLocation], poweredTiles);
    }

    /// <summary>
    /// MOD: added. Caches every identifier a given belt-family instance could reasonably be configured
    /// under in <see cref="Models.ModConfig.PowerRequiredMachineNames"/> — its resolved type ID (already
    /// known per-CLR-type via <see cref="MachineTypeIdsByType"/>) plus its own raw qualified/unqualified
    /// item ID, so a player can configure by whichever one's easier to find (see
    /// <see cref="PowerRequiredMachineSystem.RequiresPower(IEnumerable{string})"/>'s own remarks). Keyed
    /// by CLR <see cref="Type"/>, same as <see cref="MachineTypeIdsByType"/> — every instance of a given
    /// Stardio belt-family type is always the exact same underlying item (confirmed by decompiling
    /// Stardio.dll: each type maps 1:1 to one <c>Jok.Stardio/FactoryItems</c> entry), so this is safe to
    /// cache per-type rather than per-instance. <see cref="IsStarved"/> (via <see cref="Draw_Postfix"/>)
    /// calls this once per visible starved-eligible belt/filter/etc. EVERY FRAME — without this cache,
    /// that allocated a fresh 3-element array from scratch every single draw call for a result that's
    /// always identical for a given type.
    /// </summary>
    private static readonly Dictionary<Type, string[]> CandidateIdsByType = new();

    /// <summary>Get every identifier a given belt-family instance could reasonably be configured under — see <see cref="CandidateIdsByType"/>'s own remarks.</summary>
    /// <param name="obj">The object to resolve.</param>
    /// <param name="machineTypeId">The object's own already-resolved type ID (from <see cref="MachineTypeIdsByType"/>), to avoid a second dictionary lookup.</param>
    private static string[] GetCandidateIds(SObject obj, string machineTypeId)
    {
        Type type = obj.GetType();
        if (StardioConveyorBeltPatches.CandidateIdsByType.TryGetValue(type, out string[]? cached))
            return cached;

        string[] result = [machineTypeId, obj.QualifiedItemId, obj.ItemId];
        StardioConveyorBeltPatches.CandidateIdsByType[type] = result;
        return result;
    }

    /// <summary>Skip a starved belt's own movement for this tick — it simply doesn't advance, and resumes cleanly once repowered.</summary>
    /// <param name="__instance">The belt being updated, typed as its common vanilla base since this project has no compile-time reference to Stardio's own types.</param>
    private static bool BeltUpdate_Prefix(SObject __instance)
    {
        return !StardioConveyorBeltPatches.IsStarved(__instance);
    }

    /// <summary>Skip a starved Splitter's own movement for this tick — the same shape as <see cref="BeltUpdate_Prefix"/>, just for splitterUpdate instead of beltUpdate.</summary>
    /// <param name="__instance">The Splitter being updated.</param>
    private static bool SplitterUpdate_Prefix(SObject __instance)
    {
        return !StardioConveyorBeltPatches.IsStarved(__instance);
    }

    /// <summary>
    /// Refuse to route an item through a starved Filter/Inverted Filter/Bridge, or store one into (or pull
    /// one back out of) a starved Warp Nexus — see <see cref="TryApply"/>'s own remarks on why this is one
    /// shared prefix bound positionally via <c>__0</c> rather than several near-identical ones.
    /// </summary>
    /// <param name="__0">The Filter/Inverted Filter/Bridge/Warp Nexus instance being routed through, pushed into, or pulled from — always the patched method's own first parameter, regardless of its actual declared name.</param>
    /// <param name="__result">The value the original method would have returned.</param>
    /// <returns>Returns <c>false</c> to skip the original method (refusing the route, exactly as if the target didn't match/accept), or <c>true</c> to let it run normally.</returns>
    private static bool RoutingTarget_Prefix(SObject __0, ref bool __result)
    {
        if (!StardioConveyorBeltPatches.IsStarved(__0))
            return true;

        __result = false;
        return false;
    }

    /// <summary>
    /// Refuse to push an item into a starved Input Hub — but if the same building has more than one Input
    /// Hub, a starved one only ever blocks ITSELF, not the whole building. The Input Hub is a plain
    /// BigCraftable already gated by <see cref="PowerRequiredMachinePatches"/>'s generic system (its held
    /// Chest gets tagged starved the same way an Auto-Grabber's does), but that tag is only refreshed
    /// roughly once per night (see <see cref="PowerRequiredMachinePatches.IsPowerStarved(SObject)"/>'s own
    /// remarks) — fine for an Auto-Grabber's inherently-overnight production, but far too stale for a belt
    /// pushing into a Hub continuously all day, so this checks live instead.
    ///
    /// An earlier version of this gate just found the FIRST Input Hub in the building and blocked the
    /// entire push if that one happened to be starved — confirmed via user report to be wrong the moment a
    /// building has multiple Input Hubs: a starved one sitting first in iteration order would block a
    /// perfectly powered one sitting right next to it. Stardio's own TryPushToBuilding already tries every
    /// Input Hub in the building in turn, moving on if one's chest refuses the item (full, locked, etc.) —
    /// this replicates that exact loop, just also skipping a starved one the same way a full one gets
    /// skipped, so the powered/room-for-it Hub still gets used normally.
    /// </summary>
    /// <param name="__instance">The pushing belt/splitter.</param>
    /// <param name="targetTile">The building tile being pushed toward.</param>
    /// <param name="__result">The value the original method would have returned.</param>
    /// <returns>Returns <c>false</c> to skip the original method — this prefix always does its own equivalent work instead — or <c>true</c> to let the original run un-gated if there's no building here, or <see cref="TryPushToChestMethod"/> couldn't be resolved.</returns>
    private static bool TryPushToBuilding_Prefix(SObject __instance, Vector2 targetTile, ref bool __result)
    {
        GameLocation? indoors = __instance.Location?.getBuildingAt(targetTile)?.GetIndoors();
        if (indoors == null || StardioConveyorBeltPatches.TryPushToChestMethod == null)
            return true;

        foreach (SObject obj in indoors.objects.Values)
        {
            if (obj.QualifiedItemId != StardioConveyorBeltPatches.InputChestQualifiedItemId || PowerRequiredMachinePatches.IsPowerStarved(obj))
                continue;

            if (obj.heldObject.Value is Chest chest && (bool)StardioConveyorBeltPatches.TryPushToChestMethod.Invoke(__instance, [chest])!)
            {
                __result = true;
                return false;
            }
        }

        __result = true; // MOD: matches Stardio's own TryPushToBuilding, which returns true unconditionally once a building+indoors was found — even if every Input Hub was starved, full, or absent
        return false;
    }

    /// <summary>The pull-side counterpart to <see cref="TryPushToBuilding_Prefix"/> — refuses to pull an item back out of a starved Output Hub, while still pulling normally from a different, powered Output Hub sharing the same building. See that method's own remarks for why this replicates Stardio's own per-hub loop rather than gating the whole method.</summary>
    /// <param name="__instance">The pulling belt.</param>
    /// <param name="targetTile">The building tile being pulled from.</param>
    /// <returns>Returns <c>false</c> to skip the original method — this prefix always does its own equivalent work instead — or <c>true</c> to let the original run un-gated if there's no building here, or <see cref="TryPullFromChestMethod"/> couldn't be resolved.</returns>
    private static bool TryPullFromBuilding_Prefix(SObject __instance, Vector2 targetTile)
    {
        GameLocation? indoors = __instance.Location?.getBuildingAt(targetTile)?.GetIndoors();
        if (indoors == null || StardioConveyorBeltPatches.TryPullFromChestMethod == null)
            return true;

        foreach (SObject obj in indoors.objects.Values)
        {
            if (obj.QualifiedItemId != StardioConveyorBeltPatches.OutputChestQualifiedItemId || PowerRequiredMachinePatches.IsPowerStarved(obj))
                continue;

            if (obj.heldObject.Value is Chest chest && (bool)StardioConveyorBeltPatches.TryPullFromChestMethod.Invoke(__instance, [chest])!)
                break;
        }

        return false;
    }

    /// <summary>Freeze a starved belt's own animation frame at 0 instead of the shared, ever-advancing counter every belt of that type normally reads.</summary>
    /// <param name="__instance">The belt being drawn.</param>
    /// <param name="__result">The animation frame that would otherwise be used.</param>
    private static bool GetBeltAnim_Prefix(SObject __instance, ref int __result)
    {
        if (!StardioConveyorBeltPatches.IsStarved(__instance))
            return true;

        __result = 0;
        return false;
    }

    /// <summary>Draw the same pulsing "no power" icon every other power-required machine shows, over a starved belt.</summary>
    /// <param name="__instance">The belt being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    /// <param name="alpha">The alpha the belt itself was drawn at.</param>
    private static void Draw_Postfix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (__instance.isTemporarilyInvisible || !StardioConveyorBeltPatches.IsStarved(__instance))
            return;

        PowerRequiredMachinePatches.DrawNoPowerIcon(spriteBatch, x, y, alpha);
    }

    /// <summary>Undo Stardio's own push/speed-boost effect for a farmer currently standing on a starved belt.</summary>
    /// <param name="__instance">The farmer being moved.</param>
    /// <param name="currentLocation">The farmer's current location.</param>
    private static void FarmerMovePosition_Postfix(Farmer __instance, GameLocation currentLocation)
    {
        if (currentLocation == null || !currentLocation.objects.TryGetValue(__instance.Tile, out SObject belt) || !StardioConveyorBeltPatches.IsStarved(belt))
            return;

        __instance.yVelocity = 0f;
        __instance.xVelocity = 0f;
        __instance.temporarySpeedBuff = 0f;
    }
}

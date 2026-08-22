using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Gates the "Stardio" mod's conveyor belts on Automate's own power grid — even though a belt
/// isn't an <see cref="IMachine"/> Automate automates at all (Stardio moves items along them, and pushes
/// the player standing on one, with its own entirely separate logic). A starved belt: doesn't advance or
/// push/pull items (<see cref="BeltUpdate_Prefix"/>), doesn't animate (<see cref="GetBeltAnim_Prefix"/>),
/// doesn't push or speed-boost the player standing on it (<see cref="FarmerMovePosition_Postfix"/>), and
/// shows the same pulsing "no power" icon every other power-required machine does
/// (<see cref="Draw_Postfix"/>, reusing <see cref="PowerRequiredMachinePatches.DrawNoPowerIcon"/> directly
/// rather than duplicating that visual).
///
/// Each of the four belt types (plain/Fast/Turbo/Turbo-Pushing) gets its OWN
/// <see cref="Models.ModConfig.PowerRequiredMachineNames"/> entry — <c>"ConveyorBelt"</c>,
/// <c>"FastConveyorBelt"</c>, <c>"TurboConveyorBelt"</c>, <c>"TurboPushingConveyorBelt"</c> — derived from
/// each type's own in-game DISPLAY name via <see cref="BaseMachine.GetDefaultMachineId(string)"/>, exactly
/// like every other entry in that list resolves from a real machine's own name. An earlier version of this
/// class used one hardcoded shared ID for all four belt types instead, which — confirmed directly via user
/// report — didn't match the type-specific names a player would naturally type in, following the same
/// pattern every other entry already uses.
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

    /// <summary>Get the shared power-required-machines system, set via <see cref="Initialize"/>.</summary>
    private static Func<PowerRequiredMachineSystem>? GetSystem;

    /// <summary>Get the currently-powered tiles for a location (or <c>null</c> if the power system is disabled), set via <see cref="Initialize"/>.</summary>
    private static Func<GameLocation, IReadOnlySet<Vector2>?>? GetPoweredTiles;

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

        return appliedAny;
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
        return StardioConveyorBeltPatches.GetSystem!().IsPowerStarved(machineTypeId, [obj.TileLocation], poweredTiles);
    }

    /// <summary>Skip a starved belt's own movement for this tick — it simply doesn't advance, and resumes cleanly once repowered.</summary>
    /// <param name="__instance">The belt being updated, typed as its common vanilla base since this project has no compile-time reference to Stardio's own types.</param>
    private static bool BeltUpdate_Prefix(SObject __instance)
    {
        return !StardioConveyorBeltPatches.IsStarved(__instance);
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

using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patches enforcing the "power-required machines" balance mechanic (see
/// <see cref="PowerRequiredMachineSystem"/>) OUTSIDE Automate's own automation loop — i.e. for a
/// player manually interacting with one of these machines, or for a machine's own passive vanilla
/// behavior (e.g. an Auto-Grabber silently collecting animal produce overnight) that has nothing to
/// do with Automate's own automation loop at all. Without these, a player (or the game itself) could
/// just bypass the whole mechanic by feeding/collecting/interacting with the machine by hand instead
/// of through Automate. This is deliberately kept in its own file with no logic of its own beyond
/// wiring vanilla hook points to <see cref="PowerRequiredMachineSystem"/> — the actual "is this
/// starved" decision lives entirely in that one shared class (see its own remarks), so this file can
/// be dropped or rewritten independently of the build-time gate in <see cref="MachineGroupFactory"/>/
/// <see cref="MachineGroupBuilder"/>/<see cref="MachineGroup"/> without the two drifting apart.
///
/// Deliberately narrow in WHAT it blocks — only the specific action that would begin/advance a
/// starved machine's own process:
/// <list type="bullet">
/// <item>Loading a new input item is refused (<see cref="PerformObjectDropInAction_Prefix"/>), showing
/// a "Machine needs power" message.</item>
/// <item>An already-in-progress machine's countdown simply doesn't advance while unpowered
/// (<see cref="ShouldTimePassForMachine_Postfix"/>) — it picks back up right where it left off once
/// power is restored, rather than losing progress.</item>
/// <item>For a machine whose "process" is really passive collection into its own internal storage
/// rather than a countdown (e.g. Auto-Grabber collecting animal produce overnight — see
/// <see cref="ShouldTimePassForMachine_Postfix"/>'s chest-tagging and <see cref="ChestAddItem_Prefix"/>),
/// new items are refused from entering that internal storage while starved — but the storage can
/// still be freely OPENED and emptied (by hand or via a connected pipe), since taking things out was
/// never the gated action to begin with.</item>
/// </list>
/// Deliberately NOT blocked: opening a machine's own inventory/menu (e.g. an Auto-Grabber's or
/// Auto-Petter's), or taking/pulling existing output out of one, whether by hand or through Automate
/// itself — a starved machine still behaves like an inert chest for anything already sitting inside
/// it, only its own "production" is gated. That said, every one of THOSE otherwise-allowed player
/// interactions still shows the "Machine needs power" reminder (<see cref="CheckForAction_Prefix"/>)
/// — it's just a courtesy nudge alongside letting the interaction go through, not a refusal.
/// </summary>
internal static class PowerRequiredMachinePatches
{
    /*********
    ** Fields
    *********/
    /// <summary>MOD: added. The mod data key used to tag a machine's own internal storage chest (e.g. an Auto-Grabber's held chest) as currently power-starved, so <see cref="ChestAddItem_Prefix"/> can cheaply refuse new items without needing to resolve the chest's owning machine on every call. Kept in sync by <see cref="ShouldTimePassForMachine_Postfix"/>, which already runs for every placed object roughly every in-game 10 minutes. Internal (not private) so a machine wrapper with its own alternate write path that bypasses <see cref="Chest.addItem"/> entirely (e.g. <see cref="Machines.Objects.AutoGrabberMachine.SetInput"/>, which uses Automate's own <see cref="IContainer.Store"/> instead) can check the SAME tag directly, so a starved machine can't be fed around the block just by going through Automate's pipe network instead of a player's hand.</summary>
    internal const string PowerStarvedChestModDataKey = "luisMint.PoweredAutomation/PowerStarvedChest";

    /// <summary>MOD: added. The qualified item ID of the vanilla Auto-Petter — its animal-petting behavior and rotating-part visual are both hardcoded special cases in vanilla (search either method for this same literal), not data-driven, so they need their own dedicated hooks rather than going through the generic machinery above.</summary>
    private const string AutoPetterQualifiedItemId = "(BC)272";

    /// <summary>MOD: added. The resolved machine type ID (<see cref="BaseMachine.GetDefaultMachineId(string)"/> applied to the Auto-Grabber's internal name "Auto-Grabber") — used to recognize which machine tagged a starved chest (see <see cref="PowerStarvedChestModDataKey"/>), so <see cref="ChestAddItem_Prefix"/> knows to show the Auto-Grabber-specific wake-up message rather than a generic one. Deliberately the resolved TYPE ID here, not the qualified item ID — the tag itself stores a type ID (see <see cref="SyncHeldChestTag"/>) so it can be checked directly against <see cref="PowerRequiredMachineSystem.RequiresPower"/> without re-deriving it from the wrong kind of string.</summary>
    private const string AutoGrabberMachineTypeId = "AutoGrabber";

    /// <summary>MOD: added. Set while inside a power-starved Auto-Petter's own <see cref="SObject.DayUpdate"/> call, so <see cref="FarmAnimalPet_Prefix"/> knows to skip just the auto-pet calls that specific Auto-Petter is about to make — see <see cref="DayUpdate_Prefix"/>/<see cref="DayUpdate_Postfix"/>.</summary>
    private static bool IsInsideStarvedAutoPetterDayUpdate;

    /// <summary>MOD: added. Whether a wake-up message about a starved Auto-Grabber failing to gather produce has already been queued for the night currently being processed — reset once per night via <see cref="ResetNightlyFailureMessages"/>, so multiple animals/multiple starved Auto-Grabbers in the same night only ever queue the message once.</summary>
    private static bool HasQueuedAutoGrabberFailureMessage;

    /// <summary>MOD: added. Same as <see cref="HasQueuedAutoGrabberFailureMessage"/>, but for a starved Auto-Petter failing to pet animals.</summary>
    private static bool HasQueuedAutoPetterFailureMessage;

    /// <summary>
    /// MOD: added. The asset name of the "no power" overlay icon (loaded by the PoweredAutomation
    /// content pack), drawn pulsing on top of any power-required machine while it's starved — see
    /// <see cref="Draw_Postfix"/>. This is deliberately a generic overlay rather than per-machine
    /// alternate textures: it works uniformly for every current and future power-required machine
    /// (including ones added by other mods hooking into Automate's own automation factory API) with no
    /// per-machine art needed.
    /// </summary>
    private const string NoPowerIconAssetName = "Mods/luisMint.PoweredAutomation/NoPowerIcon";

    /// <summary>The overlay icon's alpha at the low point of its pulse.</summary>
    private const float NoPowerIconMinAlpha = 0.35f;

    /// <summary>The overlay icon's alpha at the high point of its pulse.</summary>
    private const float NoPowerIconMaxAlpha = 0.9f;

    /// <summary>How fast the overlay icon's alpha pulses, in radians per second.</summary>
    private const float NoPowerIconPulseSpeed = 4f;

    /// <summary>MOD: added. How far the overlay icon's drawn scale grows/shrinks at the peak of its pulse, in the same units as the base 4x draw scale (e.g. 0.4 = ±0.4 around 4x, so it ranges from 3.6x to 4.4x).</summary>
    private const float NoPowerIconScaleAmplitude = 0.4f;

    /// <summary>MOD: added. How fast the overlay icon's scale pulses, in radians per second — kept an exact whole multiple of <see cref="NoPowerIconPulseSpeed"/> (the alpha pulse) on purpose, so the two stay in lockstep (always realigning at the start of each alpha cycle) instead of gradually drifting in and out of phase with each other, while still reading as a distinct, quicker beat layered on top of the slower opacity fade.</summary>
    private const float NoPowerIconScalePulseSpeed = PowerRequiredMachinePatches.NoPowerIconPulseSpeed * 2f;

    /// <summary>Get the shared power-required-machines system, set via <see cref="Initialize"/>.</summary>
    private static Func<PowerRequiredMachineSystem>? GetSystem;

    /// <summary>Get the currently-powered tiles for a location (or <c>null</c> if the power system is disabled), set via <see cref="Initialize"/>.</summary>
    private static Func<GameLocation, IReadOnlySet<Vector2>?>? GetPoweredTiles;


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the accessors needed to resolve which machines are power-starved. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="getSystem">Get the shared power-required-machines system.</param>
    /// <param name="getPoweredTiles">Get the currently-powered tiles for a location (or <c>null</c> if the power system is disabled).</param>
    public static void Initialize(Func<PowerRequiredMachineSystem> getSystem, Func<GameLocation, IReadOnlySet<Vector2>?> getPoweredTiles)
    {
        PowerRequiredMachinePatches.GetSystem = getSystem;
        PowerRequiredMachinePatches.GetPoweredTiles = getPoweredTiles;
    }

    /// <summary>MOD: added. Reset the per-night "already queued" flags for the Auto-Grabber/Auto-Petter wake-up failure messages. Must be called once at the start of each night's processing (before the overnight machine/animal updates run), so a failure on a later night can queue the message again.</summary>
    public static void ResetNightlyFailureMessages()
    {
        PowerRequiredMachinePatches.HasQueuedAutoGrabberFailureMessage = false;
        PowerRequiredMachinePatches.HasQueuedAutoPetterFailureMessage = false;
    }

    /// <summary>
    /// MOD: added. Sync every placed object's held-chest starved tag (see <see cref="ChestAddItem_Prefix"/>)
    /// across the given locations. Must be called once right before the overnight update runs (i.e.
    /// from <c>DayEnding</c>), NOT relied on via <see cref="ShouldTimePassForMachine_Postfix"/> alone —
    /// that hook only actually runs for objects with a Data/Machines entry (its own early-out for
    /// <c>machineData == null</c> means it's silently skipped for a machine like the Auto-Grabber,
    /// which has no such entry at all), and even where it DOES apply, its ~10-minute cadence could
    /// otherwise leave a stale tag right at the one moment (the overnight update) it actually matters.
    /// <see cref="StardewValley.Object.DayUpdate"/> has no such gate and IS called for every placed
    /// object, but only immediately before that SAME object's own overnight logic runs — for an
    /// Auto-Grabber, that's not necessarily before the farm animals whose produce it's trying to
    /// collect have already run theirs, so relying on that alone would still race depending on
    /// processing order. Scanning every location up front here, strictly before ANY overnight
    /// processing begins, avoids that risk entirely.
    /// </summary>
    /// <param name="locations">The locations to scan.</param>
    public static void SyncHeldChestTagsBeforeOvernightUpdate(IEnumerable<GameLocation> locations)
    {
        foreach (GameLocation location in locations)
        {
            foreach (SObject obj in location.objects.Values)
                PowerRequiredMachinePatches.SyncHeldChestTag(obj, PowerRequiredMachinePatches.IsPowerStarved(obj));
        }
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.ShouldTimePassForMachine)),
            postfix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(ShouldTimePassForMachine_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.performObjectDropInAction)),
            prefix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(PerformObjectDropInAction_Prefix))
        );

        // MOD: added — freezes a starved machine's idle "working" wobble/pulse animation (the same
        // throb Crystalarium, Heavy Furnace, etc. play while processing) so it visually reads as
        // stopped, matching the paused countdown from ShouldTimePassForMachine above. Object.getScale()
        // — which actually computes the wobble each frame — reads ShouldWobble() itself, so patching
        // this one virtual method freezes the animation for every data-driven machine generically,
        // with no per-machine-type drawing code needed.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.ShouldWobble)),
            postfix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(ShouldWobble_Postfix))
        );

        // MOD: added — refuses new items into a starved machine's own internal storage chest (e.g. an
        // Auto-Grabber silently collecting animal produce overnight has nothing to do with
        // performObjectDropInAction/checkForAction at all — see FarmAnimal's own overnight update,
        // which just calls Chest.addItem directly on whatever Auto-Grabber it finds). Gated by a
        // mod-data tag kept in sync by ShouldTimePassForMachine_Postfix below, so this stays a cheap
        // per-call dictionary lookup rather than resolving the chest's owning machine every time —
        // Chest.addItem is called very frequently for entirely unrelated chests.
        harmony.Patch(
            original: AccessTools.Method(typeof(Chest), nameof(Chest.addItem)),
            prefix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(ChestAddItem_Prefix))
        );

        // MOD: added — NOT a blocking patch (no __result/return false) — every other player
        // interaction with a starved machine (opening its inventory, collecting ready output, etc.)
        // still goes through completely normally, this just also shows the reminder message alongside
        // it, so the player always gets a nudge on any interaction even where nothing is refused.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.checkForAction)),
            prefix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(CheckForAction_Prefix))
        );

        // MOD: added — the Auto-Petter's actual "machine aspect" (auto-petting every animal in its
        // building once a day) is a hardcoded switch case inside SObject.DayUpdate, not something
        // driven by performObjectDropInAction/checkForAction/ShouldTimePassForMachine at all — so it
        // needs its own hook. DayUpdate() doesn't distinguish which specific object triggered a given
        // FarmAnimal.pet(..., is_auto_pet: true) call, so this uses the same flag+intercept technique
        // PowerCoilPatches already uses elsewhere in this codebase: wrap the whole DayUpdate() call to
        // flag whether we're currently inside a starved Auto-Petter's own call, then have the
        // FarmAnimal.pet patch below check that flag. Manual player petting (is_auto_pet: false,
        // the only other caller of FarmAnimal.pet) is completely untouched either way.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.DayUpdate)),
            prefix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(DayUpdate_Prefix)),
            postfix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(DayUpdate_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.pet)),
            prefix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(FarmAnimalPet_Prefix))
        );

        // MOD: added — the Auto-Petter's rotating decorative part is a hardcoded special case in
        // SObject.draw (search for the same qualified ID) that spins continuously off live game time,
        // completely independent of ShouldWobble/getScale — so freezing it needs its own full redraw
        // of just this one case, the same technique PowerCoilPatches uses for its own custom draw.
        // Every other item (and even a POWERED Auto-Petter) is untouched and falls through to vanilla.
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.draw), [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]),
            prefix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(Draw_Prefix)),
            postfix: new HarmonyMethod(typeof(PowerRequiredMachinePatches), nameof(Draw_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Pause a power-required machine's processing countdown while its tile is out of power range, and keep its internal storage chest's starved tag (see <see cref="PowerStarvedChestModDataKey"/>) in sync.</summary>
    /// <param name="__instance">The machine being checked.</param>
    /// <param name="__result">Whether vanilla's own logic would otherwise let time pass for this machine.</param>
    private static void ShouldTimePassForMachine_Postfix(SObject __instance, ref bool __result)
    {
        bool isStarved = PowerRequiredMachinePatches.IsPowerStarved(__instance);

        if (__result && isStarved)
            __result = false;

        // MOD: added — sync the starved tag on the machine's own internal storage chest, if any (e.g.
        // an Auto-Grabber's held chest), regardless of __result — that only reflects the processing
        // countdown, but the chest-input gate below applies just as much to a machine with no
        // countdown of its own at all. This is a secondary/best-effort sync for objects this hook
        // actually runs for (see SyncHeldChestTagsBeforeOvernightUpdate's remarks for why it's NOT the
        // only sync point).
        PowerRequiredMachinePatches.SyncHeldChestTag(__instance, isStarved);
    }

    /// <summary>Tag (or untag) an object's held storage chest, if any, as currently power-starved — see <see cref="PowerStarvedChestModDataKey"/> and <see cref="ChestAddItem_Prefix"/>.</summary>
    /// <param name="obj">The object whose held chest, if any, should be synced.</param>
    /// <param name="isStarved">Whether <paramref name="obj"/> is currently power-starved.</param>
    private static void SyncHeldChestTag(SObject obj, bool isStarved)
    {
        if (obj.heldObject.Value is not Chest heldChest)
            return;

        // MOD: tags with the machine's own resolved machine TYPE ID (not the qualified item ID, and
        // not just a bare "true") so ChestAddItem_Prefix can both re-check RequiresPower directly
        // against it (RequiresPower expects a type ID, e.g. "AutoGrabber" — feeding it a qualified
        // item ID like "(BC)165" instead would never match anything in the configured machine list)
        // and tell WHICH machine a starved chest belongs to, to show the right wake-up failure message.
        if (isStarved)
            heldChest.modData[PowerRequiredMachinePatches.PowerStarvedChestModDataKey] = BaseMachine.GetDefaultMachineId(obj.Name);
        else
            heldChest.modData.Remove(PowerRequiredMachinePatches.PowerStarvedChestModDataKey);
    }

    /// <summary>Freeze a starved machine's idle "working" wobble/pulse animation.</summary>
    /// <param name="__instance">The machine being checked.</param>
    /// <param name="__result">Whether vanilla's own logic would otherwise wobble this machine.</param>
    private static void ShouldWobble_Postfix(SObject __instance, ref bool __result)
    {
        if (__result && PowerRequiredMachinePatches.IsPowerStarved(__instance))
            __result = false;
    }

    /// <summary>Refuse to load an item into a power-required machine while its tile is out of power range.</summary>
    /// <param name="__instance">The machine being loaded.</param>
    /// <param name="probe">Whether this is just a hover/capability check rather than a real interaction.</param>
    /// <param name="__result">The value the original method would have returned.</param>
    /// <returns>Returns <c>false</c> to skip the original method (refusing the item), or <c>true</c> to let it run normally.</returns>
    private static bool PerformObjectDropInAction_Prefix(SObject __instance, bool probe, ref bool __result)
    {
        if (!PowerRequiredMachinePatches.IsPowerStarved(__instance))
            return true;

        if (!probe)
            PowerRequiredMachineSystem.ShowNeedsPowerMessage();

        __result = false;
        return false;
    }

    /// <summary>Refuse to add a new item to a machine's internal storage chest while it's tagged power-starved (see <see cref="PowerStarvedChestModDataKey"/>) — but leave every other chest (including this same chest once power is restored, or while a player is just taking things OUT of it) completely unaffected.</summary>
    /// <param name="__instance">The chest an item is being added to.</param>
    /// <param name="item">The item being added.</param>
    /// <param name="__result">The value the original method would have returned (the leftover item that didn't fit, or <c>null</c> if it was fully added).</param>
    /// <returns>Returns <c>false</c> to skip the original method (refusing the item, as if the chest were full), or <c>true</c> to let it run normally.</returns>
    private static bool ChestAddItem_Prefix(Chest __instance, Item item, ref Item? __result)
    {
        if (!__instance.modData.TryGetValue(PowerRequiredMachinePatches.PowerStarvedChestModDataKey, out string? machineTypeId))
            return true;

        // MOD: added — re-verify the mechanic (and this specific machine type) is still configured to
        // require power right now, so toggling PowerRequiredMachinesEnabled off (or removing this
        // machine type from the configured list) takes effect immediately, rather than waiting for the
        // next ~10-minute sync tick in ShouldTimePassForMachine_Postfix to clear a stale tag.
        if (PowerRequiredMachinePatches.GetSystem == null || !PowerRequiredMachinePatches.GetSystem().RequiresPower(machineTypeId))
        {
            __instance.modData.Remove(PowerRequiredMachinePatches.PowerStarvedChestModDataKey);
            return true;
        }

        if (machineTypeId == PowerRequiredMachinePatches.AutoGrabberMachineTypeId)
            PowerRequiredMachinePatches.QueueFailureMessage(ref PowerRequiredMachinePatches.HasQueuedAutoGrabberFailureMessage, "Auto-Grabber failed to gather animal products due to lack of power");

        __result = item;
        return false;
    }

    /// <summary>Show the "needs power" reminder alongside any OTHER player interaction with a starved machine (opening its inventory, collecting output, etc.), without blocking the interaction itself.</summary>
    /// <param name="__instance">The machine being interacted with.</param>
    /// <param name="justCheckingForActivity">Whether this is just a capability check (e.g. for cursor icon) rather than a real interaction.</param>
    private static void CheckForAction_Prefix(SObject __instance, bool justCheckingForActivity)
    {
        if (!justCheckingForActivity && PowerRequiredMachinePatches.IsPowerStarved(__instance))
            PowerRequiredMachineSystem.ShowNeedsPowerMessage();
    }

    /// <summary>Flag whether the Auto-Petter about to run its overnight update is currently power-starved, for <see cref="FarmAnimalPet_Prefix"/> to check.</summary>
    /// <param name="__instance">The object whose day update is about to run.</param>
    private static void DayUpdate_Prefix(SObject __instance)
    {
        bool isStarved = PowerRequiredMachinePatches.IsPowerStarved(__instance);

        // MOD: added — extra safety-net sync (SyncHeldChestTagsBeforeOvernightUpdate is the primary
        // one, run earlier from DayEnding for every object up front) in case this object's chest
        // wasn't tagged yet for any reason (e.g. it was created after that pre-pass somehow).
        PowerRequiredMachinePatches.SyncHeldChestTag(__instance, isStarved);

        PowerRequiredMachinePatches.IsInsideStarvedAutoPetterDayUpdate =
            __instance.QualifiedItemId == PowerRequiredMachinePatches.AutoPetterQualifiedItemId && isStarved;
    }

    /// <summary>Clear the flag set by <see cref="DayUpdate_Prefix"/> once the day update call returns.</summary>
    private static void DayUpdate_Postfix()
    {
        PowerRequiredMachinePatches.IsInsideStarvedAutoPetterDayUpdate = false;
    }

    /// <summary>Skip an automatic (not manual) pet while inside a starved Auto-Petter's own day update.</summary>
    /// <param name="is_auto_pet">Whether this is an automatic pet (from an Auto-Petter) rather than the player directly interacting with the animal.</param>
    /// <returns>Returns <c>false</c> to skip the pet, or <c>true</c> to let it happen normally — always <c>true</c> for a manual player pet, regardless of any Auto-Petter's power state.</returns>
    private static bool FarmAnimalPet_Prefix(bool is_auto_pet)
    {
        if (!is_auto_pet || !PowerRequiredMachinePatches.IsInsideStarvedAutoPetterDayUpdate)
            return true;

        PowerRequiredMachinePatches.QueueFailureMessage(ref PowerRequiredMachinePatches.HasQueuedAutoPetterFailureMessage, "Auto-Petter failed to pet animals due to lack of power");
        return false;
    }

    /// <summary>Queue a wake-up message (the same style as vanilla's "weeds and debris" or "tool ready" notices) to show once the player wakes up, at most once per night.</summary>
    /// <param name="alreadyQueuedFlag">A per-night flag tracking whether this specific message has already been queued — set to <c>true</c> as a side effect.</param>
    /// <param name="message">The message to show.</param>
    private static void QueueFailureMessage(ref bool alreadyQueuedFlag, string message)
    {
        if (alreadyQueuedFlag)
            return;

        alreadyQueuedFlag = true;
        Game1.morningQueue.Enqueue(() => Game1.showGlobalMessage(message));
    }

    /// <summary>Freeze the Auto-Petter's rotating decorative part while it's power-starved, instead of vanilla's normal continuous spin.</summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    /// <param name="alpha">The transparency at which to draw the sprite.</param>
    /// <returns>Returns <c>false</c> to skip the original method for a starved Auto-Petter, or <c>true</c> to let it run normally for everything else (including a POWERED Auto-Petter, which keeps spinning as usual).</returns>
    private static bool Draw_Prefix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (__instance.isTemporarilyInvisible || __instance.QualifiedItemId != PowerRequiredMachinePatches.AutoPetterQualifiedItemId || !PowerRequiredMachinePatches.IsPowerStarved(__instance))
            return true;

        // MOD: replicated from vanilla's own (BC)272 special case in Object.draw — see that method's
        // remarks. ShouldWobble() is already always false for this item (it never has a
        // minutesUntilReady countdown), so getScale() is always Vector2.Zero for it and the
        // scale-dependent terms vanilla's own math would otherwise include all safely drop out here.
        ParsedItemData itemData = ItemRegistry.GetDataOrErrorItem(__instance.QualifiedItemId);
        Texture2D texture = itemData.GetTexture();

        Vector2 position = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, y * 64 - 64));
        int jitter = __instance.shakeTimer > 0 ? Game1.random.Next(-1, 2) : 0;
        Rectangle destination = new((int)position.X + jitter, (int)position.Y + jitter, 64, 128);
        float drawLayer = Math.Max(0f, (float)((y + 1) * 64 - 24) / 10000f) + x * 1E-05f;

        spriteBatch.Draw(texture, destination, itemData.GetSourceRect(1, __instance.ParentSheetIndex), Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, drawLayer);

        // MOD: the only real change from vanilla — a fixed 0f rotation instead of the live
        // `(float)Game1.currentGameTime.TotalGameTime.TotalSeconds * -1.5f` vanilla uses, freezing the
        // rotating part in place instead of spinning it.
        spriteBatch.Draw(texture, position + new Vector2(8.5f, 12f) * 4f, itemData.GetSourceRect(2, __instance.ParentSheetIndex), Color.White * alpha, 0f, new Vector2(7.5f, 15.5f), 4f, SpriteEffects.None, drawLayer + 1E-05f);

        return false;
    }

    /// <summary>
    /// MOD: added. Draw a pulsing "no power" icon over any power-required machine while it's starved,
    /// on top of whatever it just drew (vanilla's own sprite, or a starved Auto-Petter's own frozen
    /// redraw from <see cref="Draw_Prefix"/> above — this runs as a postfix either way, so it composes
    /// with both). Deliberately generic — a single shared icon works for every current AND future
    /// power-required machine (including ones from other mods) instead of needing dedicated
    /// alternate/powered-unpowered textures per machine.
    /// </summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    /// <param name="alpha">The transparency at which the machine itself was just drawn.</param>
    private static void Draw_Postfix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (__instance.isTemporarilyInvisible || !PowerRequiredMachinePatches.IsPowerStarved(__instance))
            return;

        Texture2D texture = Game1.content.Load<Texture2D>(PowerRequiredMachinePatches.NoPowerIconAssetName);

        // MOD: anchored at the CENTER of the standard 2-tile (64x128 at 4x) bounding box, with a
        // matching center origin below, so the scale pulse grows/shrinks around a fixed point instead
        // of visibly drifting from a corner as it scales.
        Vector2 topLeft = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, y * 64 - 64));
        Vector2 centerPosition = topLeft + new Vector2(32f, 64f);
        Vector2 origin = new(texture.Width / 2f, texture.Height / 2f);

        double elapsedSeconds = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
        float alphaPulseFraction = (float)(Math.Sin(elapsedSeconds * PowerRequiredMachinePatches.NoPowerIconPulseSpeed) * 0.5 + 0.5);
        float pulseAlpha = PowerRequiredMachinePatches.NoPowerIconMinAlpha + alphaPulseFraction * (PowerRequiredMachinePatches.NoPowerIconMaxAlpha - PowerRequiredMachinePatches.NoPowerIconMinAlpha);

        // MOD: a separate, faster sine wave from the alpha pulse above — a different speed (and phase,
        // since they're not offset here but run at different speeds so they drift apart from each
        // other naturally) reads as two distinct layered pulses instead of one throb scaled two ways.
        float scalePulseFraction = (float)(Math.Sin(elapsedSeconds * PowerRequiredMachinePatches.NoPowerIconScalePulseSpeed) * 0.5 + 0.5);
        float scale = 4f + (scalePulseFraction * 2f - 1f) * PowerRequiredMachinePatches.NoPowerIconScaleAmplitude;

        // MOD: a small positive offset on top of the machine's own draw layer (matching its standard
        // 2-tile anchor, regardless of the machine's actual sprite height) so the icon consistently
        // draws IN FRONT of the machine it's overlaying.
        float layerDepth = Math.Max(0f, (float)((y + 1) * 64 - 24) / 10000f) + x * 1E-05f + 0.0002f;

        spriteBatch.Draw(texture, centerPosition, new Rectangle(0, 0, texture.Width, texture.Height), Color.White * pulseAlpha * alpha, 0f, origin, scale, SpriteEffects.None, layerDepth);
    }

    /// <summary>Get whether an object is a configured power-required machine type whose own tile is currently out of power range.</summary>
    /// <param name="obj">The object to check.</param>
    private static bool IsPowerStarved(SObject obj)
    {
        GameLocation? location = obj.Location;
        if (location == null)
            return false;

        string machineTypeId = BaseMachine.GetDefaultMachineId(obj.Name);
        IReadOnlySet<Vector2>? poweredTiles = PowerRequiredMachinePatches.GetPoweredTiles!(location);

        return PowerRequiredMachinePatches.GetSystem!().IsPowerStarved(machineTypeId, [obj.TileLocation], poweredTiles);
    }
}

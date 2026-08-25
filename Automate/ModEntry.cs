using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework;
using Pathoschild.Stardew.Automate.Framework.Commands;
using Pathoschild.Stardew.Automate.Framework.Machines.Objects;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Patches;
using Pathoschild.Stardew.Automate.Framework.Storage;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.Integrations.GenericModConfigMenu;
using Pathoschild.Stardew.Common.Messages;
using Pathoschild.Stardew.Common.Utilities;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.GameData.BigCraftables;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate;

/// <summary>The mod entry point.</summary>
internal class ModEntry : Mod
{
    /*********
    ** Fields
    *********/
    /// <summary>The internal mod data.</summary>
    private DataModel Data = null!; // set in Entry

    /// <summary>The mod configuration.</summary>
    private ModConfig Config = null!; // set in Entry

    /// <summary>The configured key bindings.</summary>
    private ModConfigKeys Keys => this.Config.Controls;

    /// <summary>Manages machine groups.</summary>
    private MachineManager MachineManager = null!; // set in Entry

    /// <summary>Handles console commands from players.</summary>
    private CommandHandler CommandHandler = null!; // set in Entry

    /// <summary>MOD: added. Plays a passive dust-puff ambient effect on placed Power Coils.</summary>
    private readonly PowerCoilAmbientEffect PowerCoilAmbientEffect = new();

    /// <summary>MOD: added. The Power Relay efficiency-bonus mechanic, used both to read the save-wide bonus (see <see cref="GetEffectiveActionDelaySeconds"/>/<see cref="GetEffectiveActionsPerDelayWindow"/>) and by <see cref="PowerRelayInteraction"/>/<see cref="PowerRelayMenu"/> to read/write a Relay's own slot state.</summary>
    private PowerRelaySystem PowerRelaySystem = null!; // set in Entry

    /// <summary>MOD: added. Resolves <see cref="ModConfig.PowerSiloTierPools"/> into this save's rolled Power Silo tier requirements — shared by <see cref="MachineManager"/> (which builds <see cref="PowerSiloSystem"/> from it), <see cref="PowerSiloInteraction"/>, and <see cref="Patches.PowerSiloCapPatches"/>, so all three always agree on the same rolled result.</summary>
    private PowerSiloTierRoller PowerSiloTierRoller = null!; // set in Entry

    /// <summary>MOD: added. Plays a passive shimmering-glint ambient effect on placed Power Relays.</summary>
    private PowerRelayAmbientEffect PowerRelayAmbientEffect = null!; // set in Entry

    /// <summary>Whether to automate machines for the current save.</summary>
    private bool EnableAutomation => this.Config.Enabled && Context.IsMainPlayer;

    /// <summary>
    /// Whether to track machine changes for the current save.
    ///
    /// MOD: fixed — this used to also require <c>Context.IsMainPlayer || this.CurrentOverlay.Value is not null</c>,
    /// so a farmhand only tracked world changes (placements/removals — see every <c>On*ListChanged</c>
    /// handler below) while their OWN debug overlay happened to be open. Automation itself stays
    /// host-only (see <see cref="EnableAutomation"/>) for good reason — only one client should ever
    /// actually move items — but this went further and froze a farmhand's own LOCAL view of groups and
    /// connections (used for the connector-tile power animation, the overlay, and anything else that
    /// reads cached machine-group data) any time their overlay was closed, so it silently went stale and
    /// only ever caught up on the next full rebuild the overlay itself triggers on open. The underlying
    /// reload machinery is already diff-based and scoped to just the location that actually changed (see
    /// <see cref="ReloadIfNeeded"/>), not a full rescan, so there's no real perf reason to gate it behind
    /// the overlay specifically — every client (host or farmhand) now tracks changes continuously, so a
    /// farmhand's own local picture of the world stays as fresh as the host's without needing to manually
    /// toggle anything.
    /// </summary>
    private bool EnableAutomationChangeTracking =>
        this.Config.Enabled
        && !this.IsSecondaryScreen; // in split-screen mode, the change will be tracked by the main player

    /// <summary>Whether this is a secondary screen in split-screen mode.</summary>
    private bool IsSecondaryScreen => Context.IsSplitScreen && !Context.IsMainPlayer;

    /// <summary>The number of ticks until the next automation cycle. Only used when <see cref="ModConfig.UseEventBasedAutomation"/> is disabled.</summary>
    private int AutomateCountdown;

    /// <summary>
    /// MOD: added. Whether to run one automation pass on the next <see cref="OnUpdateTicked"/> call,
    /// regardless of trigger mode — set right after something rebuilds machine groups outside the
    /// normal event flow (a new day starting, a config change) so event-based mode doesn't have to
    /// wait for the next natural <see cref="OnTimeChanged"/>/<see cref="OnChestInventoryChanged"/> to
    /// reflect it, the same way interval mode already gets an instant pass via <see cref="AutomateCountdown"/>
    /// being reset to 0.
    /// </summary>
    private bool RunAutomationPassOnNextTick;

    /// <summary>
    /// MOD: added. Set on <see cref="OnSaveLoaded"/>, cleared at the end of <see cref="OnDayStarted"/> —
    /// while true, <see cref="OnLocationListChanged"/> skips queuing anything, since every location
    /// populating as a save loads is guaranteed to be re-scanned anyway moments later by
    /// <see cref="OnDayStarted"/>'s own unconditional <see cref="MachineManager.Reset"/> (which discards
    /// every group and queues literally every current location for a full rebuild, regardless of what
    /// specifically changed). Without this, a fresh save load did the ENTIRE world's machine-group scan
    /// TWICE back to back — once draining the queue <see cref="OnLocationListChanged"/> built while the
    /// world's locations were still populating, then again from <see cref="OnDayStarted"/>'s reset —
    /// confirmed directly via a diagnostic log showing every single Powered Chest enqueued twice, ~75-90ms
    /// apart, each time under a totally different (freshly rebuilt) <see cref="IMachineGroup"/> instance.
    /// That doubled real scan cost (location flood-fill, connector traversal, Power Silo capacity/coil
    /// calculations) is a very plausible cause of a real frame hitch right at load, independent of
    /// anything about automation pacing. Scoped to the SaveLoaded→DayStarted window specifically (not a
    /// one-time session flag) so it correctly re-arms on every subsequent load too (e.g. returning to the
    /// title screen and loading a different save).
    /// </summary>
    private bool SuppressLocationListChangedUntilDayStarted;

    /// <summary>
    /// MOD: added. Locations whose <see cref="IWorldEvents.ChestInventoryChanged"/> pass may have found
    /// its containers locked (the player has a chest menu open, so <see cref="MachineGroup.Automate"/>
    /// bails out immediately) — retried once any menu closes (see <see cref="OnMenuChanged"/>), since
    /// that's exactly when such a lock would be released. Without this, a manual edit made through an
    /// open chest menu would sit unprocessed until the next incidental <see cref="OnTimeChanged"/> tick,
    /// since the chest's contents don't change again just from closing the menu (nothing re-fires
    /// <see cref="IWorldEvents.ChestInventoryChanged"/> at that point).
    /// </summary>
    private readonly HashSet<GameLocation> LocationsPendingLockedRetry = new();

    /// <summary>
    /// MOD: added. How many <see cref="OnTimeChanged"/> firings have happened since the last full,
    /// unscoped backstop scan of every active group. <see cref="Patches.MachineReadyPatches"/> already
    /// covers the common case (an ordinary machine finishing its processing countdown) precisely and
    /// immediately, so re-scanning literally everything on every single 10-minute tick as well is mostly
    /// redundant now — this backstop only exists for the handful of cases that hook can't see at all
    /// (non-<c>Object</c>-backed machines; instant-complete 0-minute recipes), so it only needs to run
    /// occasionally, not every tick.
    /// </summary>
    private int TicksSinceFullBackstopScan;

    /// <summary>MOD: added. How many <see cref="OnTimeChanged"/> firings to let pass between full backstop scans (see <see cref="TicksSinceFullBackstopScan"/>) — once per in-game hour.</summary>
    private const int FullBackstopScanIntervalTicks = 6;

    /// <summary>
    /// MOD: added. The most <see cref="PendingDelayedPasses"/> entries <see cref="RunDuePendingPasses"/>
    /// will execute in one <see cref="OnUpdateTicked"/> call. Without this, every group whose delay
    /// happened to land at the same moment (the normal case now that entries aren't jittered — see
    /// EnqueueForAutomation's own remarks) ran synchronously in the SAME frame: dozens of real container
    /// scans/transfers/event notifications back to back, a plausible source of a real frame hitch right
    /// when a lot of Powered Chests all become due together (e.g. day start). Anything beyond this cap
    /// simply carries over to the next tick(s) instead — each entry's own delay already elapsed by the
    /// time it's picked here, so being executed a few ~16ms ticks later is an imperceptible difference
    /// against a multi-second <see cref="ModConfig.ActionDelaySeconds"/>, in exchange for spreading a
    /// large burst's real cost across several frames instead of spiking one.
    /// </summary>
    private const int MaxDuePassesPerTick = 8;

    /// <summary>
    /// MOD: added. Group batches queued to run after <see cref="ModConfig.ActionDelaySeconds"/> has passed,
    /// instead of immediately — see <see cref="TryScheduleGroupBatch"/>/<see cref="RunGroupBatch"/>.
    ///
    /// MOD: added — <c>Group</c> records which <see cref="IMachineGroup"/> (if any) the pass is scoped to,
    /// purely so <see cref="PurgeRemovedGroupsPacingState"/> can cancel a still-pending pass for a group
    /// that's just been discarded by a rebuild, instead of letting it fire later against a group whose
    /// <see cref="GroupActionQueues"/>/<see cref="QueuedMachines"/> entries have already been cleaned up.
    /// Without this, such a stale pass would silently RE-CREATE those entries from scratch when it finally
    /// ran — effectively resurrecting a group that was supposed to be dead, back into a live, independently
    /// ticking duplicate of whatever new group replaced it. Confirmed directly via diagnostic logging: a
    /// group would commit twice within a few hundred milliseconds at session start, well short of the
    /// configured delay, because a leftover pass from the group's very first (pre-warp) scan fired after
    /// that scan had already been superseded and purged.
    /// </summary>
    private readonly List<(double CreatedAtMs, double ScheduledTimeMs, Action Action, IMachineGroup? Group)> PendingDelayedPasses = new();

    /// <summary>
    /// MOD: added. Each group's own FIFO queue of machines waiting for their push/pull — used only while
    /// <see cref="ModConfig.ActionDelaySeconds"/> is greater than zero. A machine's position in this queue
    /// is fixed the moment it's added (see <see cref="QueuedMachines"/>'s own remarks for why that fixed
    /// ordering matters) and can't be jumped by later rediscovery, regardless of how often something
    /// rediscovers it as "still ready" before its turn comes up.
    ///
    /// Deliberately keyed by <see cref="IMachineGroup"/> reference, with NO attempt to carry a queue forward
    /// across a rebuild that recreates the group instance — that's an acceptable
    /// trade: a rebuild just resets the affected group's pacing to fresh (its machines get rediscovered and
    /// re-queued from scratch by the normal triggers), rather than trying to bridge old-to-new group
    /// instances, which is what caused most of the fragility in earlier attempts at this feature.
    /// </summary>
    /// MOD: changed from <see cref="Queue{T}"/> to <see cref="LinkedList{T}"/> — a chest-like machine (e.g.
    /// <see cref="Machines.Objects.PoweredChestMachine"/>) is inserted at the FRONT instead of the back (see
    /// <see cref="EnqueueForAutomation"/>), so it always gets first crack at a firing's budget instead of
    /// waiting behind whatever ordinary machines happened to already be queued — otherwise, in a busy group
    /// where something else reliably wins the single per-firing budget slot every cycle, a chest shared by
    /// several groups could go many cycles in THIS particular group without ever actually being dequeued and
    /// attempted at all, even though its own cross-group nudge only fires once it's actually tried and failed.
    ///
    /// MOD: added — each entry now also carries its own <c>EligibleAtMs</c> timestamp (set once, at the
    /// moment it's enqueued — see <see cref="EnqueueForAutomation"/>), and <see cref="RunGroupBatch"/> only
    /// ever commits an entry once <see cref="UnpausedElapsedMs"/> has actually reached it. Before this, a
    /// group's timer was armed ONCE per priming cycle (see <see cref="ArmedGroupBatches"/>) and, whenever it
    /// fired, drained WHATEVER was in the queue at that moment — including anything enqueued moments
    /// earlier, mid-countdown, which then only ever waited however much of that ALREADY-ticking timer
    /// happened to be left, not its own full <see cref="ModConfig.ActionDelaySeconds"/>. That's what let a
    /// Powered Chest pushing into a container, and a second Powered Chest immediately pulling that exact
    /// item back out, both land in the SAME already-armed shot — visually an instant two-hop chain, even
    /// though each individual chest's own automation is still correctly paced on its own.
    ///
    /// MOD: added — each entry now also carries whether it was queued by a <c>Confirmed</c> signal (a
    /// specific container actually changing — see <see cref="ScheduleInputFeedsFor"/>) versus a blind one
    /// (a group merely being built/rebuilt, or — in interval mode — the periodic bulk sweep, neither of
    /// which says anything about whether real work exists). This only matters for a
    /// <see cref="IChestLikeMachine"/> (e.g. <see cref="Machines.Objects.PoweredChestMachine"/>), whose
    /// <see cref="IMachine.GetState"/> is hardcoded to always report Empty and so carries no information of
    /// its own either way — see <see cref="EnqueueForAutomation"/>'s own remarks for how it's used.
    private readonly Dictionary<IMachineGroup, LinkedList<(IMachine Machine, double EligibleAtMs, bool Confirmed)>> GroupActionQueues = new(new ObjectReferenceComparer<IMachineGroup>());

    /// <summary>
    /// MOD: added. Every machine currently sitting in some group's <see cref="GroupActionQueues"/>, waiting
    /// for its turn. Without this, a machine still waiting in queue would get enqueued AGAIN every time
    /// something rediscovers it as "still ready" — interval mode's bulk scan rediscovers every still-ready
    /// machine on EVERY <see cref="ModConfig.AutomationInterval"/> tick (often much shorter than
    /// <see cref="ModConfig.ActionDelaySeconds"/>), and <see cref="ScheduleInputFeedsFor"/> rediscovers every
    /// still-empty machine on EVERY chest change in the group.
    ///
    /// This matters even more for a machine whose <see cref="IMachine.GetState"/> never actually leaves
    /// Done/Empty between being queued and being serviced — e.g. <see cref="Machines.Objects.PoweredChestMachine"/>,
    /// which always reports <see cref="MachineState.Empty"/>. Without this dedup, such a machine would get
    /// rediscovered (and would try to jump the queue) far more often than a normal machine that's only
    /// occasionally ready, structurally starving everything queued behind it — observed as
    /// an unexpectedly long stall in event-based mode and multiple machines committing at once in interval mode.
    /// </summary>
    private readonly HashSet<IMachine> QueuedMachines = new(new ObjectReferenceComparer<IMachine>());

    /// <summary>MOD: added. Every group that currently has a batch scheduled (see <see cref="TryScheduleGroupBatch"/>) — used only while <see cref="ModConfig.ActionDelaySeconds"/> is greater than zero. A group already in here is left alone by any new trigger that finds more of its work; the already-scheduled batch will drain its <see cref="GroupActionQueues"/> entry fresh when it fires (see <see cref="RunGroupBatch"/>).</summary>
    private readonly HashSet<IMachineGroup> ArmedGroupBatches = new(new ObjectReferenceComparer<IMachineGroup>());

    /// <summary>
    /// MOD: added. How many times in a row a given <see cref="IChestLikeMachine"/> was actually attempted
    /// (see <see cref="AutomateMachine"/>) and moved nothing, plus when that last attempt happened — used
    /// by <see cref="ScheduleGroupCheck"/> to stop re-enqueueing one on EVERY confirmed signal for its group
    /// once it's proven itself unproductive several times in a row (see <see cref="ChestLikeFruitlessStreakThreshold"/>/
    /// <see cref="ChestLikeBackoffWindowsCooldown"/>). A <see cref="IChestLikeMachine"/>'s own
    /// <see cref="IMachine.GetState"/> is hardcoded to always report Empty (see <see cref="QueuedMachines"/>'s
    /// own remarks), so — unlike a normal machine — nothing about its state alone ever says "skip me, I have
    /// nothing to do"; without this, a Powered Chest whose piped connections genuinely never have anything
    /// for it still gets a real <see cref="Framework.Machines.Objects.PoweredChestMachine.TryMoveOne"/> scan
    /// (a real per-container, per-item-stack loop, not a free check) on every single confirmed signal for its
    /// group, forever, crowding out the group's limited <see cref="ModConfig.ActionsPerDelayWindow"/> budget
    /// from machines that actually have something to do.
    ///
    /// Keyed via <see cref="ConditionalWeakTable{TKey,TValue}"/> rather than a plain <see cref="Dictionary{TKey,TValue}"/>
    /// specifically so it never needs wiring into <see cref="PurgeRemovedGroupsPacingState"/>/<see cref="ResetDelayQueueState"/>
    /// — an <see cref="IMachine"/> wrapper discarded by a rebuild simply becomes unreachable and its entry is
    /// reclaimed by the GC on its own, the same "fresh start after a rebuild" behavior every other per-machine
    /// tracker here already gets via explicit purging, for free.
    /// </summary>
    private readonly ConditionalWeakTable<IMachine, ChestLikeAttemptState> ChestLikeAttemptStreaks = new();

    /// <summary>MOD: added. How many consecutive fruitless attempts (see <see cref="ChestLikeAttemptStreaks"/>) before a <see cref="IChestLikeMachine"/> starts being backed off from confirmed-signal re-checks.</summary>
    private const int ChestLikeFruitlessStreakThreshold = 3;

    /// <summary>MOD: added. Once backed off (see <see cref="ChestLikeFruitlessStreakThreshold"/>), how many real-time seconds (scaled by <see cref="GetEffectiveActionDelaySeconds"/> — see <see cref="ScheduleGroupCheck"/>) a <see cref="IChestLikeMachine"/> is skipped for on a confirmed signal before it's given another chance.</summary>
    private const double ChestLikeBackoffWindowsCooldown = 2;

    /// <summary>MOD: added. Per-<see cref="IChestLikeMachine"/> mutable state tracked by <see cref="ChestLikeAttemptStreaks"/>.</summary>
    private sealed class ChestLikeAttemptState
    {
        /// <summary>How many attempts in a row (see <see cref="AutomateMachine"/>) moved nothing.</summary>
        public int ConsecutiveFruitless;

        /// <summary>The <see cref="UnpausedElapsedMs"/> value as of the last attempt, fruitless or not.</summary>
        public double LastAttemptAtMs;
    }

    /// <summary>
    /// MOD: added. Container changes reported by <see cref="ThrottledContainer"/> via
    /// its <c>notifyContainerChanged</c> delegate, queued here instead of being acted on immediately — see
    /// <see cref="ProcessPendingContainerChangeNotifications"/> for why this indirection is required.
    ///
    /// A first attempt at this wired the delegate straight to <see cref="ScheduleInputFeedsFor"/>, called
    /// synchronously from inside <c>ThrottledContainer.Store</c>/its removal hook. That's the EXACT same
    /// hang <see cref="AutomateMachine"/>'s own remarks already document for a near-identical mistake: a
    /// container write happening deep inside <see cref="RunGroupBatch"/>'s own synchronous while loop (via
    /// <see cref="AutomateMachine"/> → <c>group.TryPushMachineOutput</c>/<c>TryFeedMachineInput</c> →
    /// <c>StorageManager</c> → the container's own <c>Store</c>/removal call) would call
    /// <see cref="ScheduleGroupCheck"/> on that SAME group being drained, re-enqueueing a Done/Empty
    /// machine right back onto the queue the outer loop was in the middle of emptying — the loop's
    /// <c>Count</c> would never reach 0. The zero-delay path is just as vulnerable in a different way: a
    /// container write during <c>group.Automate()</c> (run directly when <see cref="ModConfig.ActionDelaySeconds"/>
    /// is 0) would call back into <see cref="TryScheduleGroupBatch"/> for that same group, which in THAT
    /// mode calls <c>group.Automate()</c> again immediately — recursively, while already inside it.
    /// Queuing here instead, and draining strictly AFTER this tick's automation work has fully finished
    /// (see <see cref="ProcessPendingContainerChangeNotifications"/>), avoids re-touching any in-progress
    /// queue or call stack entirely.
    /// </summary>
    private List<(GameLocation Location, Vector2 Tile, bool IsJunimoChest)> PendingContainerChangeNotifications = new();

    /// <summary>
    /// MOD: added. Real time actually spent with <see cref="Game1.shouldTimePass"/> true, in milliseconds
    /// — the clock every delay/queue timing calculation uses instead of reading
    /// <see cref="Game1.currentGameTime"/> directly, so having ANY menu open (inventory, a chest, etc.),
    /// not just the Escape/options pause screen, correctly stops these purely cosmetic delays from
    /// silently counting down — and the queue from silently draining — in the background while the player
    /// can't see any of it happening. Time simply doesn't pass for these delays while paused, and resumes
    /// exactly where it left off once unpaused, rather than causing a catch-up burst. This only affects
    /// the delay/queue timing itself — it does NOT pause automation entirely, matching this mod's existing
    /// design of still automating while the game is paused when no cosmetic delay is configured.
    /// </summary>
    private double UnpausedElapsedMs;

    /// <summary>MOD: added. The SMAPI multiplayer message type used to broadcast a save-wide HUD toast to every connected player — see <see cref="BroadcastHudMessage"/>/<see cref="OnModMessageReceived"/>.</summary>
    private const string BroadcastHudMessageType = "luisMint.PoweredAutomation_BroadcastHudMessage";

    /// <summary>MOD: added. The SMAPI multiplayer message type used to tell every other connected player to reload their own machine data for specific locations — see <see cref="BroadcastReloadLocations"/>/<see cref="OnModMessageReceived"/>.</summary>
    private const string BroadcastReloadLocationsMessageType = "luisMint.PoweredAutomation_BroadcastReloadLocations";

    /// <summary>MOD: added. The SMAPI multiplayer message type used to tell every other connected player about an automated shipment changing the shipping bin's "last shipped" display — see <see cref="BroadcastLastItemShipped"/>/<see cref="OnModMessageReceived"/>.</summary>
    private const string BroadcastLastItemShippedMessageType = "luisMint.PoweredAutomation_BroadcastLastItemShipped";

    /// <summary>MOD: added. The SMAPI multiplayer message type a farmhand sends to the host to say one of their own menus just closed — see <see cref="OnMenuChanged"/>/<see cref="OnModMessageReceived"/> for why the host can't just detect this itself.</summary>
    private const string NotifyHostMenuClosedMessageType = "luisMint.PoweredAutomation_NotifyHostMenuClosed";

    /// <summary>MOD: added. How long the most recent delayed group batch actually waited (in milliseconds) before firing, for the perf overlay to show — lets <see cref="ModConfig.ActionDelaySeconds"/> be verified against real measured timing instead of going on feel alone.</summary>
    private double? LastActualDelayMs;

    /// <summary>The number of ticks until the config UI is registered with Generic Mod Config Menu.</summary>
    /// <remarks>This must happen later than <see cref="IGameLoopEvents.GameLaunched"/>, since Content Patcher packs haven't added their edits to <c>Data/Machines</c> yet at that point.</remarks>
    private int RegisterConfigCountdown = 10;

    /// <summary>The current overlay being displayed, if any.</summary>
    private readonly PerScreen<OverlayMenu?> CurrentOverlay = new();

    /// <summary>MOD: added. Whether the automation performance overlay (see <see cref="AutomationPerfTracker"/>) is currently shown.</summary>
    private bool ShowPerfOverlay;


    /*********
    ** Public methods
    *********/
    /// <inheritdoc />
    public override void Entry(IModHelper helper)
    {
        I18n.Init(helper.Translation);
        CommonHelper.RemoveObsoleteFiles(this, "Automate.pdb"); // removed in 1.28.4

        // MOD: added — Content Patcher's Data/AudioChanges resolves a cue's FilePaths through
        // {{InternalAssetKey}} to a path under Content/SMAPI/<mod id>/..., but the game's own audio code
        // (AudioCueModificationManager.ApplyCueModification) opens that path with a raw File.Open call
        // instead of going through SMAPI's content pipeline — so unlike textures/data (which SMAPI can
        // serve virtually), nothing ever actually copies the real file to that location, and the game
        // crashes trying to open a directory that was never created. Mirroring the file there ourselves
        // once at launch works around it without needing any changes to the content pack's own JSON.
        this.MirrorPowerSiloAudioFiles();

        // read data file
        const string dataPath = "assets/data.json";
        try
        {
            DataModel? data = this.Helper.Data.ReadJsonFile<DataModel>(dataPath);
            if (data == null)
            {
                data = new(null);
                this.Monitor.Log($"The {dataPath} file seems to be missing or invalid. Floor connectors will be disabled.", LogLevel.Error);
            }
            this.Data = data;
        }
        catch (Exception ex)
        {
            this.Data = new(null);
            this.Monitor.Log($"The {dataPath} file seems to be invalid. Floor connectors will be disabled.\n{ex}", LogLevel.Error);
        }

        // read config
        this.Config = this.Helper.ReadConfig<ModConfig>();

        // MOD: added — constructed before MachineManager (which needs it to build PowerSiloSystem) and
        // kept as its own field so PowerSiloInteraction/PowerSiloCapPatches below share this EXACT same
        // instance. MOD: changed — each roller call now takes the specific Silo building it's rolling
        // for (see PowerSiloTierRoller's own remarks on why the roll moved from once-per-save to
        // once-per-Silo), so sharing this one instance just means they all reuse the same in-memory
        // per-building cache, not that they'd ever get the same roll for two DIFFERENT Silos.
        this.PowerSiloTierRoller = new PowerSiloTierRoller(
            getBaseTiers: () => this.Config.PowerSiloTiers,
            getTierPools: () => this.Config.PowerSiloTierPools
        );

        // MOD: added — must run before any machine is constructed (i.e. before MachineManager below),
        // since every BaseMachine.GetDefaultMachineId(string) call — including the ones baked into a
        // machine's own MachineTypeID at construction time — shares this same prefix list. See that
        // method's own remarks for why.
        BaseMachine.SetKnownModIdPrefixes(this.Helper.ModRegistry.GetAll().Select(mod => mod.Manifest.UniqueID));

        // init
        this.MachineManager = new MachineManager(
            config: () => this.Config,
            data: this.Data,
            defaultFactory: new AutomationFactory(
                config: () => this.Config,
                monitor: this.Monitor,
                reflection: this.Helper.Reflection,
                // MOD: added — see PoweredChestMachine's own remarks for why its
                // own single paced turn needs this to perform more than one transfer per call.
                getEffectiveActionsPerDelayWindow: () => this.GetEffectiveActionsPerDelayWindow()
            ),
            monitor: this.Monitor,
            powerSiloTierRoller: this.PowerSiloTierRoller,
            // MOD: added — see ThrottledContainer's own remarks for why chunked
            // container delivery reuses this exact same Relay-upgrade-aware pacing.
            getEffectiveActionDelaySeconds: () => this.GetEffectiveActionDelaySeconds(),
            getEffectiveActionsPerDelayWindow: () => this.GetEffectiveActionsPerDelayWindow(),
            // MOD: added — see ThrottledContainer's own remarks for why its shared
            // per-container-per-window budget needs THIS exact real-time clock (not Game1.currentGameTime)
            // to actually enforce N-per-window against real elapsed seconds, matching the group-level
            // ActionDelaySeconds timer's own already-proven clock.
            getElapsedMs: () => this.UnpausedElapsedMs,
            getVisualEffectsEnabled: () => this.Config.AnimatedItemTransfers,
            // MOD: added — a Powered Chest (or any container) whose contents change
            // through automation now proactively wakes every active group covering its own tile directly,
            // instead of relying solely on SMAPI's own ChestInventoryChanged event round-trip. A container
            // reachable from MULTIPLE separate machine groups (e.g. one group's
            // conduit feeds it, a completely different group's pull-conduit — possibly whitelist/category
            // filtered — reads from it) could go unnoticed by that OTHER group until the periodic hourly
            // backstop scan happened to catch it. This mirrors AutomateMachine's own existing cross-group
            // nudge for a MACHINE split across an input/output group (see its own remarks) — that fix
            // never covered a CONTAINER's contents changing, only a machine's own push/pull outcome, so
            // this closes the equivalent gap on the storage side. Deliberately just queues here rather than
            // calling ScheduleInputFeedsFor directly — see PendingContainerChangeNotifications's own
            // remarks for the hang that caused.
            notifyContainerChanged: (location, tile, isJunimoChest) => this.PendingContainerChangeNotifications.Add((location, tile, isJunimoChest))
        );

        this.CommandHandler = new CommandHandler(this.Monitor, () => this.Config, this.MachineManager, this.PowerSiloTierRoller, this.TogglePerfOverlay);

        // MOD: added — the Power Relay's global efficiency-bonus mechanic (see PowerRelaySystem's own
        // remarks). Deliberately NOT owned by MachineManager.Factory like PowerSiloSystem is — unlike a
        // Power Silo (which gates Power Coil range, feeding directly into machine group formation), a
        // Power Relay has no relationship to grouping at all; it only ever needs to be read by this
        // class's own pacing code and written by PowerRelayInteraction/PowerRelayMenu.
        this.PowerRelaySystem = new PowerRelaySystem(
            getEnabled: () => this.Config.PowerRelaySystemEnabled,
            getRelayBuildingNames: () => this.Config.PowerRelayBuildingNames,
            getActionDelayReductionPerShard: () => this.Config.PowerRelayActionDelayReductionPerShardSeconds,
            getActionsPerDelayWindowBonusPerBar: () => this.Config.PowerRelayActionsPerDelayWindowBonusPerBar,
            // MOD: fixed — used to read Config.ActionDelaySeconds unconditionally, which only equals the
            // true "not overwriting" default (ModConfig.DefaultActionDelaySeconds) right after a config
            // file load; GMCM edits it live without resetting it back, so disabling "Overwrite automation
            // delay" live left this (and PowerRelayMenu's own display, which reads through the same
            // method) still computing off whatever custom number was last dialed in, until the slider was
            // touched again.
            getBaseActionDelaySeconds: () => this.Config.OverwriteAutomationDelay ? this.Config.ActionDelaySeconds : ModConfig.DefaultActionDelaySeconds,
            getMinimumActionDelaySeconds: () => this.Config.PowerRelayMinimumActionDelaySeconds
        );

        // MOD: added — the Power Relay's passive shimmering-glint ambient effect, purely cosmetic.
        this.PowerRelayAmbientEffect = new PowerRelayAmbientEffect(
            getRelayBuildingNames: () => this.Config.PowerRelayBuildingNames
        );

        // apply Harmony patches
        Harmony harmony = new(this.ModManifest.UniqueID);
        PowerCoilPatches.Apply(harmony);

        // MOD: added — the cheap, early-game, manually-cranked alternative to the Power Coil. Doesn't
        // need a Power Silo system reference (unlike PowerSiloPatches.Initialize below) since this
        // object deliberately never touches Power Grid capacity or solar-connectivity at all.
        CrankedPowerCoilPatches.Initialize(
            requeueLocations: this.BroadcastReloadLocations
        );
        CrankedPowerCoilPatches.Apply(harmony);

        SignFilterPatches.Initialize(
            getWhitelistSignNames: () => this.Config.WhitelistSignNames,
            getBlacklistSignNames: () => this.Config.BlacklistSignNames
        );
        SignFilterPatches.Apply(harmony);

        CategorySignPatches.Initialize(
            getWhitelistCategorySignNames: () => this.Config.WhitelistCategorySignNames,
            getBlacklistCategorySignNames: () => this.Config.BlacklistCategorySignNames,
            getCustomCategories: () => this.Config.CustomCategories
        );
        CategorySignPatches.Apply(harmony);

        SignColliderPatches.Apply(harmony);

        // MOD: added — swaps a managed sign's world sprite to its dedicated "_UnPowered" texture while
        // invalid, replacing the previous Alternative Textures-driven swap (see
        // SignValidityPatches' own remarks).
        SignValidityPatches.Apply(harmony);

        // MOD: added — swaps a managed connector's world sprite between its powered/unpowered/dimmer/
        // dimmest variants, replacing the previous Alternative Textures-driven
        // swap (see ConnectorTexturePatches' own remarks). Initialize wires up the same two config
        // values that used to drive the now-removed PoweredFloorAnimator's per-tick clock — the
        // "orphaned" category's pulse is computed locally by this class now, at render time.
        ConnectorTexturePatches.Initialize(
            getFps: () => this.Config.PoweredFloorAnimationFps,
            getUnpoweredHoldMultiplier: () => this.Config.PoweredFloorUnpoweredHoldMultiplier
        );
        ConnectorTexturePatches.Apply(harmony);

        PoweredChestPatches.Apply(harmony);

        // MOD: added — makes a chest's lid visually swing open for a bit after
        // ContainerVisualEffects triggers an animation on it (chunked container delivery's entry/exit
        // feedback). Registered after PoweredChestPatches so both are applied, but see
        // ChestLidAnimationPatches.Apply's own remarks for why explicit Harmony priority — not
        // registration order — is what actually guarantees the two run in the right order for a
        // Powered Chest specifically.
        ChestLidAnimationPatches.Apply(harmony);

        // MOD: added — reacts directly to a specific machine becoming ready via Object.minutesElapsed,
        // instead of waiting for the periodic full-group scan to notice (see its own remarks).
        MachineReadyPatches.Initialize(
            getUseEventBasedAutomation: () => this.Config.UseEventBasedAutomation,
            getLocationKey: this.MachineManager.Factory.GetLocationKey,
            machineManager: this.MachineManager
        );
        MachineReadyPatches.Apply(harmony);

        PowerRequiredMachinePatches.Initialize(
            getSystem: () => this.MachineManager.Factory.PowerRequiredMachineSystem,
            getPoweredTiles: location => this.MachineManager.GetMachineDataFor(location)?.PoweredTiles
        );
        PowerRequiredMachinePatches.Apply(harmony);

        AutoCrafterPatches.Initialize(
            getSystem: () => this.MachineManager.Factory.PowerRequiredMachineSystem,
            getPoweredTiles: location => this.MachineManager.GetMachineDataFor(location)?.PoweredTiles,
            notifyMachineMightBeReady: (location, tile) =>
            {
                if (Context.IsWorldReady && this.EnableAutomation && this.Config.UseEventBasedAutomation)
                    this.ScheduleInputFeedsFor(location, tile);
            }
        );
        AutoCrafterPatches.Apply(harmony);

        // MOD: added — see MachineHarvestedPatches's own remarks. Fixes a gap affecting every machine
        // (not just the Auto Crafter): a machine emptied by hand — usually because its output chest was
        // full when it finished, so the player grabbed the item directly — never notified automation that
        // it could now accept new input, leaving it stuck until an unrelated event or the periodic
        // backstop scan eventually rechecked it.
        MachineHarvestedPatches.Initialize(
            getUseEventBasedAutomation: () => this.Config.UseEventBasedAutomation,
            notifyMachineMightBeReady: (location, tile) =>
            {
                if (Context.IsWorldReady && this.EnableAutomation && this.Config.UseEventBasedAutomation)
                    this.ScheduleInputFeedsFor(location, tile);
            }
        );
        MachineHarvestedPatches.Apply(harmony);

        PowerRangePreviewPatches.Initialize(
            getRangeDistance: () => this.Config.PowerRangeDistance
        );
        PowerRangePreviewPatches.Apply(harmony);

        PowerSiloPatches.Initialize(
            getSourceNames: () => this.Config.PowerSourceNames,
            getSolarPanelNames: () => this.Config.PowerSiloSolarPanelNames, // MOD: added
            getLocalSourceNames: () => this.Config.LocalPowerSourceNames, // MOD: added — so a Powered Chest's own placement/removal is recognized as solar-connectivity-relevant too
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem,
            requeueLocations: this.BroadcastReloadLocations, // MOD: changed — also tells every other connected player to reload these locations, not just the host's own MachineManager; see BroadcastReloadLocations's own remarks for why
            broadcastHudMessage: this.BroadcastHudMessage // MOD: added — Power Grid capacity is save-wide, so every player should see it change
        );
        PowerSiloPatches.Apply(harmony);

        // MOD: added — lets every connected player see the shipping bin's "last shipped" display update
        // for an automated (conduit) delivery, not just whoever's client happened to run the automation
        // that stored it — see ShippingBinContainer's own remarks for why that display doesn't sync on
        // its own otherwise.
        ShippingBinContainer.Initialize(this.BroadcastLastItemShipped);

        // MOD: added — the "Mark/Hide Power Coils" world-map overlay, toggled from PowerSiloMenu.
        PowerCoilMapMarkerPatches.Initialize(
            getSourceNames: () => this.Config.PowerSourceNames
        );
        PowerCoilMapMarkerPatches.Apply(harmony);

        // MOD: added — the same toggle's on-screen compass arrows, pointing toward off-screen Power
        // Coils in the player's current location (not a Harmony patch — drawn from RenderedHud below).
        PowerCoilCompass.Initialize(
            getSourceNames: () => this.Config.PowerSourceNames
        );

        // MOD: added — the Power Silo's animated "cap" that rises with each tier reached (see
        // PowerSiloCapPatches's own remarks). Tick() advances the animation and is called every game
        // tick below; Reset() clears it on day start.
        PowerSiloCapPatches.Initialize(
            getSiloBuildingNames: () => this.Config.PowerSiloBuildingNames,
            getTiers: silo => this.PowerSiloTierRoller.GetEffectiveTiers(silo),
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem
        );
        PowerSiloCapPatches.Apply(harmony);

        // MOD: added — registers the Power Silo's feed/status interaction via GameLocation.RegisterTileAction,
        // not a Harmony patch (see PowerSiloInteraction's own remarks for why).
        new PowerSiloInteraction(
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem,
            getTiers: silo => this.PowerSiloTierRoller.GetEffectiveTiers(silo),
            broadcastHudMessage: this.BroadcastHudMessage, // MOD: added — Power Silo capacity is save-wide, so every player should see a tier-up
            queueReload: this.BroadcastReloadLocations // MOD: fixed — was this.MachineManager.QueueReload, which only ever reloaded the HOST's own local machine data. A farmhand's own MachineManager needed the exact same fix BroadcastReloadLocations already exists for (see its own remarks) — otherwise a farmhand's own no-power icons/conduit textures stayed stale after a tier-up even once the host's own view correctly refreshed. See PowerSiloInteraction's own remarks for why a tier-up needs to requeue affected locations at all, not just refresh each coil's own modData.
        ).Register();

        // MOD: added — registers the Power Relay's click interaction the same way (see
        // PowerRelayInteraction's own remarks).
        new PowerRelayInteraction(
            this.PowerRelaySystem,
            getShardItemId: () => this.Config.PowerRelayShardItemId,
            getFirstShardItemId: () => this.Config.PowerRelayFirstShardItemId,
            getBarItemId: () => this.Config.PowerRelayBarItemId,
            getFirstBarItemId: () => this.Config.PowerRelayFirstBarItemId,
            // MOD: fixed — same reasoning as getBaseActionDelaySeconds above, for the actions side.
            getBaseActionsPerDelayWindow: () => this.Config.OverwriteAutomationActions ? this.Config.ActionsPerDelayWindow : ModConfig.DefaultActionsPerDelayWindow,
            broadcastHudMessage: this.BroadcastHudMessage // MOD: added — the Relay's pacing bonus is save-wide, so every player should see it improve
        ).Register();

        // MOD: added — a static lamppost light on every Power Relay, plus a one-shot whole-building
        // shake whenever one levels up (see PowerRelayEffectPatches's own remarks). Tick() is called
        // every game tick below; Reset() clears it on day start.
        PowerRelayEffectPatches.Initialize(
            getRelayBuildingNames: () => this.Config.PowerRelayBuildingNames
        );
        PowerRelayEffectPatches.Apply(harmony);

        // MOD: added — registers the Cave Hole's ladder-down interaction the same way (see
        // CaveHoleInteraction's own remarks).
        new CaveHoleInteraction().Register();

        // MOD: added — swaps any still-uncleared Green Rain Weeds to regular weeds at the exact moment
        // vanilla itself would otherwise just delete them outright (see CaveHoleGreenRainPatches's own
        // remarks for why this needs to be a patch, not a day-start check).
        CaveHoleGreenRainPatches.Apply(harmony);

        // MOD: added — lets the player leave the Cave Hole's interior again (see CaveHoleExitPatches's
        // own remarks for why this is a Harmony patch rather than a map tile property).
        CaveHoleExitPatches.Apply(harmony);

        // MOD: added — keeps the Cave Hole's own arrival/departure tile clear of anything the player
        // might place there (see CaveHolePlacementPatches's own remarks).
        CaveHolePlacementPatches.Apply(harmony);

        // MOD: added — fixes a real crash (ArgumentOutOfRangeException in Building.doAction) caused by
        // the Cave Hole's "no door" HumanDoor sentinel combined with having a real IndoorMap (see
        // CaveHoleHumanDoorCrashPatches's own remarks).
        CaveHoleHumanDoorCrashPatches.Apply(harmony);

        // MOD: added — keeps an upgraded Cave Hole's own Name in sync with its stable NameOrUniqueName,
        // a real inconsistency vanilla's own upgrade path produces (see CaveHoleLocationNamePatches's own
        // remarks) — confirmed NOT sufficient on its own to fix every third-party mod compatibility issue
        // with this location; see CaveHoleUniqueDisplayNamePatches below for the actual root cause found
        // for one such report (Chests Anywhere).
        CaveHoleLocationNamePatches.Apply(harmony);

        // MOD: added — gives each Cave Hole/Big Cave Hole a display name unique to its own physical
        // building instead of the generic, shared-across-every-instance string every tier's own
        // Data/Buildings entry uses — fixes a confirmed Chests Anywhere crash specific to having more
        // than one Cave Hole on the farm (see CaveHoleUniqueDisplayNamePatches's own remarks).
        CaveHoleUniqueDisplayNamePatches.Apply(harmony);

        // MOD: added — restricts the loot a Barrel/Crate gives inside a Cave Hole interior to 1-3 Cave
        // Carrots or nothing (see CaveHoleCrateLootPatches's own remarks).
        CaveHoleCrateLootPatches.Apply(harmony);

        // MOD: added — gives the Dwarf a "build a Power Silo" option alongside their normal shop, reusing
        // vanilla's own carpenter menu (see DwarfBuildMenuPatches's own remarks).
        DwarfBuildMenuPatches.Apply(harmony);

        // MOD: added — reskins the under-construction/upgrading visual for Dwarf-built structures only
        // (see DwarfConstructionSpritePatches's own remarks).
        DwarfConstructionSpritePatches.Apply(harmony);

        // MOD: added — clicking an active Dwarf construction site spits a random item out of the ladder
        // hole, once per building per day (see
        // DwarfConstructionSiteInteractionPatches's own remarks).
        DwarfConstructionSiteInteractionPatches.Apply(harmony);

        // MOD: added — reading Dwarf Research Note #4 unlocks a one-time hidden Power Coil at a specific
        // Mine level 120 tile (see DwarfNoteTreasureTilePatches's own remarks).
        DwarfNoteTreasureTilePatches.Apply(harmony);

        // MOD: added — reading Dwarf Research Note #7 unlocks a nightly Diamond-for-Dwarf-Gadget trade
        // at the vanilla Statue Of The Dwarf King (see
        // DwarfKingStatueTradePatches's own remarks).
        DwarfKingStatueTradePatches.Apply(harmony);

        // MOD: added — reading Dwarf Research Notes #1-#3 unlocks a one-time gem drop at a specific
        // Mountain spot, consuming a held Dwarf Scroll (see
        // DwarfNoteGemScrollPatches's own remarks).
        DwarfNoteGemScrollPatches.Apply(harmony);

        // MOD: added — gives the Dwarf's shop 3 Cave Carrots that restock weekly, plus 1 Power Coil and
        // 1 Powered Chest that each restock once a season, and 1 already-donated geode mineral that
        // restocks weekly (see DwarfWeeklyShopPatches's own remarks).
        DwarfWeeklyShopPatches.Initialize(this.Monitor);
        DwarfWeeklyShopPatches.Apply(harmony);

        // hook events
        helper.Events.Content.AssetRequested += this.OnAssetRequested;
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched; // MOD: added — see that handler's own remarks for why this needs to wait until every mod's own Entry() has run
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayEnding += this.OnDayEnding;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.TimeChanged += this.OnTimeChanged; // MOD: added — event-based automation trigger
        helper.Events.Input.ButtonsChanged += this.OnButtonsChanged;
        helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
        helper.Events.Multiplayer.PeerDisconnected += this.OnPeerDisconnected; // MOD: added — releases a Cranked Power Coil's crank lock if the player holding it disconnects mid-crank, see CrankedPowerCoilPatches.ReleaseLocksHeldBy's own remarks
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.World.BuildingListChanged += this.OnBuildingListChanged;
        helper.Events.World.LocationListChanged += this.OnLocationListChanged;
        helper.Events.World.ObjectListChanged += this.OnObjectListChanged;
        helper.Events.World.ChestInventoryChanged += this.OnChestInventoryChanged; // MOD: added — event-based automation trigger
        helper.Events.World.TerrainFeatureListChanged += this.OnTerrainFeatureListChanged;
        helper.Events.World.LargeTerrainFeatureListChanged += this.OnLargeTerrainFeatureListChanged;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Display.RenderedHud += this.OnRenderedHud; // MOD: added — draws the automation performance overlay, see AutomationPerfTracker
        helper.Events.Display.MenuChanged += this.OnMenuChanged; // MOD: added — retries locations left pending by OnChestInventoryChanged once a menu (e.g. a chest) closes

        // hook commands
        this.CommandHandler.RegisterWith(helper.ConsoleCommands);

        // log info
        this.Monitor.VerboseLog(this.Config.UseEventBasedAutomation
            ? "Initialized with event-based automation."
            : $"Initialized with automation every {this.Config.AutomationInterval} ticks.");
        if (this.Config.WarnForMissingBridgeMod)
            this.ReportMissingBridgeMods(this.Data.SuggestedIntegrations);
    }

    /// <summary>
    /// MOD: added. Applies patches that need to reflect into ANOTHER mod's own already-loaded assembly
    /// (currently just <see cref="StardioConveyorBeltPatches"/>) — this can't happen in <see cref="Entry"/>
    /// like every other patch in this class, because SMAPI loads each mod's assembly and calls its own
    /// <see cref="Entry"/> ONE MOD AT A TIME, in whatever order it resolves them (dependency order, with no
    /// guaranteed tie-break for two mods with no dependency relationship to each other) — NOT "load every
    /// mod's assembly first, then call every Entry()". Automate happened to load and run its own Entry()
    /// before Stardio's assembly was loaded at all (confirmed via the SMAPI log — Automate is discovered
    /// several lines before Stardio during the "Loading mods..." phase), so
    /// <c>AppDomain.CurrentDomain.GetAssemblies()</c> genuinely didn't contain Stardio yet at that point,
    /// and <see cref="IModRegistry.IsLoaded"/> returned false — not because Stardio wasn't installed, but
    /// because it simply hadn't taken its own turn yet. Confirmed directly via user report: belts kept
    /// working completely unpowered, and the SMAPI log had NONE of <see cref="StardioConveyorBeltPatches"/>'s
    /// own warnings either (which only fire once the "Stardio not installed" early-out is already known to
    /// be false), meaning the patch attempt never got far enough to log anything at all.
    /// <see cref="IGameLoopEvents.GameLaunched"/> is SMAPI's own documented point at which every mod's
    /// <see cref="Entry"/> is guaranteed to have already run — the standard place any SMAPI mod checks
    /// another mod's <see cref="IModRegistry.IsLoaded"/>/fetches its API, for exactly this reason. A fresh
    /// <see cref="Harmony"/> instance (same ID as <see cref="Entry"/>'s own) is used here rather than
    /// threading that one through as a field, since patching via a second instance under the same ID is a
    /// normal, supported pattern and keeps this deferred-patching concern fully self-contained.
    /// </summary>
    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        Harmony harmony = new(this.ModManifest.UniqueID);

        StardioConveyorBeltPatches.Initialize(
            getSystem: () => this.MachineManager.Factory.PowerRequiredMachineSystem,
            getPoweredTiles: location => this.MachineManager.GetMachineDataFor(location)?.PoweredTiles
        );
        StardioConveyorBeltPatches.TryApply(harmony, this.Helper.ModRegistry, this.Monitor);

        // MOD: added — same reflect-into-another-mod's-already-loaded-assembly timing requirement as
        // StardioConveyorBeltPatches above; see UtilityGridReduxSystem's own remarks.
        UtilityGridReduxSystem.TryInitialize(
            this.Helper.ModRegistry,
            this.Monitor,
            powerCoilQualifiedItemId: PowerCoilPatches.TargetQualifiedItemId,
            powerCoilGeneratedPower: this.Config.PowerCoilUtilityGridReduxPower,
            poweredChestQualifiedItemId: PoweredChestMachine.QualifiedItemId,
            poweredChestGeneratedPower: this.Config.PoweredChestUtilityGridReduxPower,
            crankedPowerCoilQualifiedItemId: CrankedPowerCoilPatches.TargetQualifiedItemId,
            crankedPowerCoilGeneratedPower: this.Config.CrankedPowerCoilUtilityGridReduxPower,
            harmony: harmony,
            queueReload: location => this.MachineManager.QueueReload(location)
        );
    }

    /// <summary>
    /// MOD: added. Copy every audio file from the PoweredAutomation content pack's <c>Pipes</c> folder
    /// into the exact <c>Content/SMAPI/&lt;mod id&gt;/Pipes</c> location that <c>{{InternalAssetKey}}</c>
    /// resolves a <c>Data/AudioChanges</c> cue's <c>FilePaths</c> to — see this method's call site for why
    /// that's otherwise never created on its own. A no-op if the content pack isn't installed.
    /// </summary>
    private void MirrorPowerSiloAudioFiles()
    {
        try
        {
            // MOD: IModInfo doesn't expose a content pack's install folder (only IContentPack does, which
            // is only available to a mod that owns the content pack) — so this assumes the standard
            // "Mods/PoweredAutomation" folder name instead, same as this codebase already hardcodes that
            // content pack's mod ID elsewhere (e.g. PowerSiloMenu's asset name constants).
            IModInfo? contentPack = this.Helper.ModRegistry.Get("luisMint.PoweredAutomation");
            if (contentPack is null)
                return;

            string sourceDir = Path.Combine(Constants.GamePath, "Mods", "PoweredAutomation", "Pipes");
            if (!Directory.Exists(sourceDir))
                return;

            string targetDir = Path.Combine(Constants.GamePath, "Content", "SMAPI", contentPack.Manifest.UniqueID.ToLowerInvariant(), "Pipes");
            Directory.CreateDirectory(targetDir);

            foreach (string sourceFile in Directory.EnumerateFiles(sourceDir, "*.*"))
            {
                string extension = Path.GetExtension(sourceFile);
                if (extension is not (".wav" or ".ogg"))
                    continue;

                string targetFile = Path.Combine(targetDir, Path.GetFileName(sourceFile));
                if (!File.Exists(targetFile) || File.GetLastWriteTimeUtc(sourceFile) > File.GetLastWriteTimeUtc(targetFile))
                    File.Copy(sourceFile, targetFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Failed mirroring Power Silo audio files; custom sounds may not play.\n{ex}", LogLevel.Warn);
        }
    }

    /// <inheritdoc />
    public override object GetApi()
    {
        return new AutomateAPI(this.Monitor, this.MachineManager);
    }


    /*********
    ** Private methods
    *********/
    /****
    ** Event handlers
    ****/
    /// <inheritdoc cref="IContentEvents.AssetRequested" />
    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        // add automate storage tags for vanilla storages to enable per-storage settings feature
        if (e.NameWithoutLocale.IsEquivalentTo("Data/BigCraftables"))
        {
            e.Edit(asset =>
            {
                IDictionary<string, BigCraftableData> assetData = asset.AsDictionary<string, BigCraftableData>().Data;

                foreach (string itemId in AutomateConstants.GetDefaultChestItemIds())
                {
                    if (assetData.TryGetValue(itemId, out BigCraftableData? entry))
                    {
                        entry.ContextTags ??= [];
                        entry.ContextTags.Add(AutomateConstants.StorageTag);
                    }
                }

                foreach (string itemId in AutomateConstants.GetTakeOnlyChestItemIds())
                {
                    if (assetData.TryGetValue(itemId, out BigCraftableData? entry))
                    {
                        entry.ContextTags ??= [];
                        entry.ContextTags.Add(AutomateConstants.StorageTag);
                        entry.ContextTags.Add(AutomateConstants.StorageTakeOnlyTag);
                    }
                }
            });
        }
    }

    /// <inheritdoc cref="IGameLoopEvents.SaveLoaded" />
    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        // MOD: added — returning to title and loading a different save (or the same one again) keeps the
        // SAME ModEntry instance alive (SMAPI doesn't recreate mods per save), so any of this mod's own
        // delay/queue bookkeeping left over from a PREVIOUS save would reference now-defunct machine/group
        // objects. OnDayStarted already does this for the same reason (a day transition also fully
        // rebuilds every group) — this covers the broader "entirely different save" case.
        this.ResetDelayQueueState();

        // MOD: added — see SuppressLocationListChangedUntilDayStarted's own remarks for why this avoids
        // scanning the whole world's machine groups twice on every load.
        this.SuppressLocationListChangedUntilDayStarted = true;

        // MOD: added — same reasoning: a cached rolled tier list from a PREVIOUS save must not leak into
        // this one. Reset() just clears the cache; the next GetEffectiveTiers() call re-reads (or rolls
        // fresh for) THIS save's own persisted result.
        this.PowerSiloTierRoller.Reset();

        // disable if secondary player
        if (!this.EnableAutomation)
        {
            if (Context.IsMultiplayer)
            {
                if (this.HostHasAutomate(out ISemanticVersion? installedVersion))
                    this.Monitor.Log($"Automate {installedVersion} is installed by the main player, so machines will be automated by their instance.");
                else
                    this.Monitor.Log("Automate isn't installed by the main player, so machines won't be automated.", LogLevel.Warn);
            }
            else
                this.Monitor.Log("You disabled Automate in the mod settings, so it won't do anything.", LogLevel.Info);
        }
    }

    /// <inheritdoc cref="IGameLoopEvents.DayEnding" />
    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        // MOD: added — reset the power-required-machines wake-up failure message flags before the
        // overnight machine/animal updates run, so a fresh failure tonight can queue the message again
        // even if it already fired on a previous night.
        PowerRequiredMachinePatches.ResetNightlyFailureMessages();

        // MOD: added — sync every power-required machine's held-chest starved tag across all locations
        // right before the overnight update runs. This is the fix for the Auto-Grabber specifically:
        // it has no Data/Machines entry, so ShouldTimePassForMachine (the periodic sync during normal
        // play) never actually runs for it at all, and DayUpdate's own per-object sync could still race
        // against FarmAnimal's overnight produce collection depending on processing order — this pass
        // guarantees every tag is correct before anything overnight touches it.
        PowerRequiredMachinePatches.SyncHeldChestTagsBeforeOvernightUpdate(CommonHelper.GetLocations());
    }

    /// <inheritdoc cref="IGameLoopEvents.DayStarted" />
    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        // reset machine state
        if (!this.IsSecondaryScreen) // in split-screen mode, machine state is managed by the main screen
        {
            this.MachineManager.Reset();
            this.AutomateCountdown = 0;
            this.RunAutomationPassOnNextTick = true; // MOD: added — event-based mode's equivalent instant pass, since it doesn't use AutomateCountdown
            this.TicksSinceFullBackstopScan = 0; // MOD: added — avoid an almost-immediate redundant backstop scan right after the pass above already covered everything
            this.ResetDelayQueueState(); // MOD: added — MachineManager.Reset() just discarded every group/machine instance, so any of this mod's own delay/queue bookkeeping for them is now stale

            // MOD: added — resets every Cranked Power Coil back to unpowered for the new day, run
            // BEFORE the solar/coil refreshes just below so neither one could ever observe a coil still
            // in yesterday's cranked state. In practice this ordering doesn't currently change either
            // refresh's result — RefreshConnectedSolarPanelCount's own poweredTiles lookup deliberately
            // excludes Cranked Power Coils entirely (see MachineManager's own PowerSiloSystem
            // construction), and RefreshCoilAllowance only ever looks at regular Power Coils — but
            // settling this coil's own state first is the more defensively correct order regardless. No
            // explicit requeue needed here — MachineManager.Reset() above already unconditionally
            // queues every location for reload as part of this same day-start handling.
            CrankedPowerCoilPatches.ResetDaily(CommonHelper.GetLocations());

            // MOD: added — an unconditional refresh every new day, on top of the usual triggers (a
            // coil placed/destroyed, a Solar Panel placed/destroyed, or a Silo built/destroyed/fed a new
            // tier — see PowerSiloSystem's own remarks). Not strictly required for correctness, but a
            // cheap, reassuring backstop in case any of those triggers is ever missed for some reason —
            // e.g. a Power Coil or Powered Chest placed/removed near an EXISTING Solar Panel changes
            // whether that panel counts as "connected" without the panel itself being touched, which
            // isn't covered by PowerSiloPatches' own Solar Panel placement/destruction hooks. Solar
            // count refreshes first since coil allowance depends on total capacity, which now depends
            // on it too.
            this.MachineManager.Factory.PowerSiloSystem.RefreshConnectedSolarPanelCount();

            // MOD: this call's own return value (which locations had a coil actually flip — see
            // PowerSiloSystem.RefreshCoilAllowance's own remarks) is deliberately discarded here, unlike
            // PowerSiloPatches'/PowerSiloInteraction's own calls to the same method, which requeue exactly
            // those locations. Not an oversight: this whole block sits just after this.MachineManager.Reset()
            // a few lines up, which unconditionally rebuilds EVERY location's machine groups/poweredTiles
            // regardless of what changed — so whatever this call reports is already moot by the time it
            // returns. Requeuing it too would just be a same-day repeat of a scan that already just ran.
            this.MachineManager.Factory.PowerSiloSystem.RefreshCoilAllowance();

            // MOD: added — clears the Power Silo cap's cached animation state, mirroring MachineManager.Reset() above.
            PowerSiloCapPatches.Reset();

            // MOD: added — clears the Power Relay's cached light/shake state, mirroring PowerSiloCapPatches.Reset() above.
            PowerRelayEffectPatches.Reset();

            // MOD: added — clears the power-required-machines callout tracking, so a machine that was
            // ALREADY starved before today doesn't look "newly starved" and fire the reminder the
            // moment the save loads — see PowerRequiredMachineSystem.Reset's own remarks.
            this.MachineManager.Factory.PowerRequiredMachineSystem.Reset();
        }

        // MOD: added — see SuppressLocationListChangedUntilDayStarted's own remarks. The comprehensive
        // reset above (when not a secondary screen) already covers every location that populated since
        // OnSaveLoaded; from here on, a genuinely new location change should queue normally again.
        this.SuppressLocationListChangedUntilDayStarted = false;

        // MOD: fixed — gated behind Context.IsMainPlayer. This spawns every placed Cave Hole's own
        // quarry-style stone/ore nodes: a full dense fill the first morning after construction, then a
        // smaller daily top-up after that (see CaveHoleQuarrySystem's own remarks). IGameLoopEvents.DayStarted
        // fires independently on EVERY connected client, not just the host, so running this unconditionally
        // meant each farmhand's own client redundantly placed a full extra round of nodes into the SAME
        // network-synced location on top of whatever the host (and every other farmhand) just placed —
        // stones overlapping each other, barrels breaking off their own footprint, one full extra fill
        // per connected player. !IsSecondaryScreen alone doesn't cover this: that only distinguishes
        // multiple screens sharing ONE local game process (split-screen), not separate networked clients.
        if (Context.IsMainPlayer)
            CaveHoleQuarrySystem.Tick();

        // MOD: added — adds the lantern light to any Cave Hole interior that doesn't have one yet
        // (a newly-finished Cave Hole's interior only exists once construction completes, so this can't
        // run any earlier than the same point the quarry spawner needs to check anyway).
        CaveHoleAmbientEffect.EnsureLights();

        // reset overlay
        this.DisableOverlay();
    }

    /// <inheritdoc cref="IPlayerEvents.Warped" />
    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (e.IsLocalPlayer)
            this.ResetOverlayIfShown();
    }

    /// <inheritdoc cref="IWorldEvents.LocationListChanged" />
    private void OnLocationListChanged(object? sender, LocationListChangedEventArgs e)
    {
        if (!this.EnableAutomationChangeTracking)
            return;

        // MOD: added — see SuppressLocationListChangedUntilDayStarted's own remarks. Every location
        // populating right now is about to be fully rebuilt anyway by OnDayStarted's own unconditional
        // reset, so queuing it here too would just scan the whole world twice back to back.
        if (this.SuppressLocationListChangedUntilDayStarted)
            return;

        this.Monitor.VerboseLog("Location list changed, reloading machines in affected locations.");

        try
        {
            if (e.Removed.Any())
                this.MachineManager.QueueRemove(e.Removed);

            this.MachineManager.QueueReload(e.Added);
        }
        catch (Exception ex)
        {
            this.HandleError(ex, "updating locations", I18n.Message_GenericError_Verb_UpdatingLocations());
        }
    }

    /// <inheritdoc cref="IWorldEvents.BuildingListChanged" />
    private void OnBuildingListChanged(object? sender, BuildingListChangedEventArgs e)
    {
        if (!this.EnableAutomationChangeTracking || this.MachineManager.IsReloadQueued(e.Location))
            return;

        this.Monitor.VerboseLog(
            this.ReloadIfNeeded(e.Location, this.GetDiffList(e.Added, e.Removed, BaseMachine.GetTileAreaFor))
                ? $"Building list changed in {e.Location.Name}, reloading its machines."
                : $"Building list changed in {e.Location.Name}, but no reload is needed."
        );
    }

    /// <inheritdoc cref="IWorldEvents.ObjectListChanged" />
    private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
    {
        // MOD: added — Power Silo's solar-connectivity trigger deliberately runs here (see
        // PowerSiloPatches.OnObjectListChanged's own remarks for why a Harmony postfix on
        // placement/tool-action can't reliably detect a REMOVED coil/panel), independent of the
        // automation-reload-tracking gate below — that gate is about Automate's own machine groups, a
        // separate concern from Power Silo capacity, which shouldn't stop working just because
        // automation itself is disabled. Still limited to the main player, since it mutates shared,
        // save-wide power-grid state.
        if (Context.IsMainPlayer)
        {
            try
            {
                PowerSiloPatches.OnObjectListChanged(e.Location, e.Added.Select(pair => pair.Value), e.Removed.Select(pair => pair.Value));
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "updating Power Silo solar panel connectivity", I18n.Message_GenericError_Verb_UpdatingSolarConnectivity());
            }
        }

        if (!this.EnableAutomationChangeTracking || this.MachineManager.IsReloadQueued(e.Location))
            return;

        this.Monitor.VerboseLog(
            this.ReloadIfNeeded(e.Location, this.GetDiffList(e.Added, e.Removed))
                ? $"Object list changed in {e.Location.Name}, reloading its machines."
                : $"Object list changed in {e.Location.Name}, but no reload is needed."
        );
    }

    /// <inheritdoc cref="IWorldEvents.TerrainFeatureListChanged" />
    private void OnTerrainFeatureListChanged(object? sender, TerrainFeatureListChangedEventArgs e)
    {
        if (!this.EnableAutomationChangeTracking || this.MachineManager.IsReloadQueued(e.Location))
            return;

        this.Monitor.VerboseLog(
            this.ReloadIfNeeded(e.Location, this.GetDiffList(e.Added, e.Removed))
                ? $"Terrain feature list changed in {e.Location.Name}, reloading its machines."
                : $"Terrain feature list changed in {e.Location.Name}, but no reload is needed."
        );
    }

    /// <inheritdoc cref="IWorldEvents.LargeTerrainFeatureListChanged" />
    private void OnLargeTerrainFeatureListChanged(object? sender, LargeTerrainFeatureListChangedEventArgs e)
    {
        if (!this.EnableAutomationChangeTracking || this.MachineManager.IsReloadQueued(e.Location))
            return;

        this.Monitor.VerboseLog(
            this.ReloadIfNeeded(e.Location, this.GetDiffList(e.Added, e.Removed, BaseMachine.GetTileAreaFor))
                ? $"Large terrain feature list changed in {e.Location.Name}, reloading its machines."
                : $"Large terrain feature list changed in {e.Location.Name}, but no reload is needed."
        );
    }

    /// <inheritdoc cref="IGameLoopEvents.UpdateTicked" />
    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // add Generic Mod Config Menu integration
        if (this.RegisterConfigCountdown > 0 && --this.RegisterConfigCountdown == 0)
        {
            // MOD: changed — uses AddGenericModConfigMenuWithDisplayName instead of the shared
            // Common.AddGenericModConfigMenu helper, so the GMCM page shows "Powered Automation"
            // instead of "Automate" (see DisplayNameManifest's own remarks for why).
            this.AddGenericModConfigMenuWithDisplayName(
                new GenericModConfigMenuIntegrationForAutomate(this.Data),
                get: () => this.Config,
                set: config => this.Config = config,
                onSaved: this.ReloadConfig
            );
        }

        // run automation
        if (Context.IsWorldReady && this.EnableAutomation)
        {
            try
            {
                // MOD: fixed — see UnpausedElapsedMs's own remarks for why this clock (not raw wall-clock
                // time) drives every delay/queue timing calculation. Originally checked Game1.paused, but
                // that's ONLY true for the Escape/options pause menu — opening your inventory, a chest, or
                // any other menu doesn't set it at all, so the clock kept advancing regardless.
                // Game1.shouldTimePass() is vanilla's own purpose-built check for "is time actually passing
                // right now," correctly accounting for any open menu, events, festivals, and (in
                // multiplayer) the shared world pause state instead of just one flag.
                if (Game1.shouldTimePass())
                    this.UnpausedElapsedMs += Game1.currentGameTime.ElapsedGameTime.TotalMilliseconds;

                // reload machines if needed
                if (this.EnableAutomationChangeTracking)
                {
                    // MOD: fixed — a rescan (e.g. placing/removing a building or machine) used to only
                    // set RunAutomationPassOnNextTick on day-start or a config reload, never here, so a
                    // machine that joined a group already power-starved had no trigger to show the
                    // reminder until the next periodic OnTimeChanged callout (up to 10 in-game minutes
                    // later). Checking right here means it fires the same tick the rescan happens —
                    // genuinely event-based, not a slow periodic catch-up.
                    if (this.MachineManager.ReloadQueuedLocations())
                    {
                        this.ResetOverlayIfShown();
                        this.MachineManager.Factory.PowerRequiredMachineSystem.ProcessStarvedMachineCallouts(this.MachineManager.GetActiveMachineGroups());
                    }

                    // MOD: added — always drain (even outside event-based mode and even when action
                    // pacing is disabled, so this can't grow unbounded) — see
                    // PurgeRemovedGroupsPacingState's own remarks for why any group discarded by the
                    // rescan above needs to be untangled from this mod's own pacing bookkeeping right
                    // away, not left to keep ticking on its own stale schedule.
                    this.PurgeRemovedGroupsPacingState(this.MachineManager.TakeRemovedGroups());

                    // MOD: added — always drain (even outside event-based mode, so this can't grow
                    // unbounded), but only act on it in event-based mode: interval mode's own periodic
                    // full scan already picks up a newly-joined member on its own, but event-based mode
                    // has no other trigger for this at all — a container joining a group doesn't fire
                    // ChestInventoryChanged (nothing was stored/removed, it just came into range), and a
                    // machine joining isn't itself "becoming ready." Without this, a newly-connected
                    // chest/machine just sat there until the player happened to open/edit a chest (or the
                    // ~7s backstop scan) triggered an unrelated rescan.
                    // MOD: added — isConfirmedSignal: false. A group merely gaining a member doesn't confirm
                    // any of its machines actually have real work available yet — see EnqueueForAutomation's
                    // own remarks.
                    IReadOnlyList<IMachineGroup> newlyJoinedGroups = this.MachineManager.TakeGroupsWithNewMembers();
                    if (this.Config.UseEventBasedAutomation)
                    {
                        foreach (IMachineGroup group in newlyJoinedGroups)
                            this.ScheduleGroupCheck(group, isConfirmedSignal: false);
                    }

                    // MOD: added — always drain (even outside event-based mode, so this can't grow
                    // unbounded), but only act on it in event-based mode, same as newlyJoinedGroups above.
                    // Broader than newlyJoinedGroups: fires for EVERY group a rescan produced, not just
                    // ones that gained a genuinely new tile — see MachineManager.RescannedGroups' own
                    // remarks for why a group that only SHRANK (a machine was broken/removed) needs this
                    // too, since it's rebuilt into a brand-new IMachineGroup instance with an empty,
                    // unarmed queue, and nothing else naturally re-triggers it. Confirmed directly via
                    // diagnostic logging: without this, breaking one furnace out of a group made the whole
                    // remaining group stop automating until an unrelated event elsewhere happened to touch
                    // it.
                    foreach (IMachineGroup group in this.MachineManager.TakeRescannedGroups())
                    {
                        // MOD: added — isConfirmedSignal: false, same reasoning as newlyJoinedGroups above.
                        if (this.Config.UseEventBasedAutomation)
                            this.ScheduleGroupCheck(group, isConfirmedSignal: false);
                    }
                }

                // MOD: added — drain any machine MachineReadyPatches has flagged ready SINCE THE LAST
                // TICK, immediately, rather than only within OnTimeChanged (which fires on the natural
                // in-game 10-minute clock tick — up to ~7 real seconds away at default game speed). This
                // closes a gap for anything that completes a machine's processing OUTSIDE that normal
                // tick cadence — e.g. Fairy Dust, which sets MinutesUntilReady = 10 and then calls
                // minutesElapsed(10) itself via its own DelayedAction roughly 50ms later, completing the
                // machine almost instantly regardless of how much time was actually left on it.
                // MachineReadyPatches already caught that false→true readyForHarvest transition correctly
                // and promptly (its own hook is a Harmony patch on minutesElapsed itself, so it fires
                // exactly when THAT specific call happens) — the flagged machine just used to sit unread
                // until whatever in-game tick happened to fire next, instead of being picked up right
                // away. TakePendingReadyMachines() is a cheap no-op (a single list-count check) on the
                // overwhelming majority of ticks where nothing's actually pending, so checking every tick
                // instead of only on the periodic one costs essentially nothing.
                if (this.Config.UseEventBasedAutomation)
                    this.ProcessPendingReadyMachines();

                // MOD: added — a one-shot instant pass for event-based mode, queued by something that
                // rebuilt machine groups outside the normal TimeChanged/ChestInventoryChanged flow (see
                // RunAutomationPassOnNextTick's own remarks). Runs after the reload above so freshly
                // rebuilt groups are already in place.
                if (this.RunAutomationPassOnNextTick)
                {
                    this.RunAutomationPassOnNextTick = false;
                    this.TryRunAutomationPass();
                }

                // process machines (interval mode only — event-based mode is instead triggered by
                // OnTimeChanged/OnChestInventoryChanged, see TryRunAutomationPass's own remarks for why
                // that's strictly at least as responsive as this countdown ever was)
                if (!this.Config.UseEventBasedAutomation && --this.AutomateCountdown <= 0)
                {
                    this.AutomateCountdown = this.Config.AutomationInterval;
                    this.TryRunAutomationPass();
                }

                // MOD: added — flush any group batches whose ModConfig.ActionDelaySeconds has elapsed.
                this.RunDuePendingPasses();

                // MOD: added — deferred until strictly after every automation pass
                // above has fully finished this tick — see ProcessPendingContainerChangeNotifications's
                // own remarks for why this can't run any earlier (or from inside one of those passes).
                this.ProcessPendingContainerChangeNotifications();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "processing machines", I18n.Message_GenericError_Verb_ProcessingMachines());
            }
        }

        // MOD: added — keeps a FARMHAND's own local MachineManager cache fresh purely for visual
        // purposes (the no-power icon, connector/cable animation state, the debug overlay), even
        // though actual automation only ever runs on the host (see EnableAutomation). The host's own
        // block above already calls ReloadQueuedLocations() as part of running automation — this is
        // ONLY for a farmhand, whose queue (already correctly populated for them by
        // EnableAutomationChangeTracking via the On*ListChanged handlers) previously had nothing that
        // ever actually PROCESSED it, since that call used to live exclusively inside the
        // EnableAutomation-gated block above. That's exactly why a farmhand's own no-power icons/cable
        // animation stayed stuck at whatever they were on join (or whenever the debug overlay was last
        // opened, which is the only other place that ever populated this) instead of updating live —
        // TickPoweredFloorAnimation just below already runs unconditionally every tick, but its own
        // comment already notes it's a no-op without cached machine data to animate.
        if (Context.IsWorldReady && this.EnableAutomationChangeTracking && !Context.IsMainPlayer)
        {
            try
            {
                if (this.MachineManager.ReloadQueuedLocations())
                    this.ResetOverlayIfShown();

                // MOD: added — ReloadQueuedLocations() above records every group it touched into these
                // same tracking lists the host's own block drains every tick to decide what to schedule
                // for automation — a farmhand never acts on them (it has nothing to schedule, since
                // automation itself stays host-only), but still has to drain them here, or they'd just
                // grow unbounded on a farmhand's client forever, same as the host's own copies would
                // without their own drain.
                this.MachineManager.TakeGroupsWithNewMembers();
                this.MachineManager.TakeRescannedGroups();
                this.MachineManager.TakeRemovedGroups();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "refreshing machine visuals", I18n.Message_GenericError_Verb_RefreshingMachineVisuals());
            }
        }

        // MOD: removed — connectors used to need a per-tick "advance the animation and write the
        // frame" pass here (host-only, covering every online player's own current location). That's
        // gone now: the host only ever needs to sync each connector's coarse CATEGORY (unpowered /
        // powered / orphaned) as part of the normal machine-data reload above, and the actual pulse for
        // an orphaned connector is computed fresh, locally, by ConnectorTexturePatches right at draw
        // time — no per-tick work and no shared animation clock needed at all. See that class's own
        // remarks for why a decorative pulse never needed to be networked or host/farmhand-synced in
        // the first place.

        // MOD: added — passive dust-puff ambient effect on placed Power Coils, purely cosmetic.
        if (Context.IsWorldReady)
        {
            try
            {
                this.PowerCoilAmbientEffect.Tick();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "animating Power Coil ambient effect", I18n.Message_GenericError_Verb_AnimatingPowerCoilAmbientEffect());
            }
        }

        // MOD: added — passive shimmering-glint ambient effect on placed Power Relays, purely cosmetic.
        if (Context.IsWorldReady)
        {
            try
            {
                this.PowerRelayAmbientEffect.Tick();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "animating Power Relay ambient effect", I18n.Message_GenericError_Verb_AnimatingPowerRelayAmbientEffect());
            }
        }

        // MOD: added — advances the Power Silo's animated cap height, purely cosmetic.
        if (Context.IsWorldReady)
        {
            try
            {
                PowerSiloCapPatches.Tick();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "animating Power Silo cap", I18n.Message_GenericError_Verb_AnimatingPowerSiloCap());
            }
        }

        // MOD: added — advances the Power Relay's light/shake state, purely cosmetic.
        if (Context.IsWorldReady)
        {
            try
            {
                PowerRelayEffectPatches.Tick();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "animating Power Relay light/shake effects", I18n.Message_GenericError_Verb_AnimatingPowerRelayLightShakeEffects());
            }
        }

        // MOD: added — polls for the Cranked Power Coil's crank minigame closing. Runs for every
        // client (not just the host, unlike the automation pass above) since the minigame itself is
        // entirely local to whichever player opened it — see CrankedPowerCoilPatches.CheckMinigameCompletion's
        // own remarks.
        if (Context.IsWorldReady)
        {
            try
            {
                CrankedPowerCoilPatches.CheckMinigameCompletion();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "checking the Cranked Power Coil minigame", I18n.Message_GenericError_Verb_CheckingCrankedPowerCoilMinigame());
            }
        }
    }

    /// <inheritdoc cref="IGameLoopEvents.TimeChanged" />
    /// <remarks>
    /// MOD: added. This event fires right after vanilla's own per-location "ten minute update" pass has
    /// completely finished for this tick — which means every <see cref="Patches.MachineReadyPatches"/>
    /// detection for this tick has already been recorded by the time this handler runs, so there's no
    /// "stragglers" problem to wait out; the whole tick's detections are already final. Instead of blindly
    /// re-scanning every active group here (confirmed, via a real performance comparison test, to cost
    /// MORE total time than the old fixed interval, since it double-processed the exact same machines
    /// <see cref="Patches.MachineReadyPatches"/> just found), this pushes output for only the specific
    /// machines that were actually flagged this tick, each scheduled independently (see
    /// <see cref="RunOrScheduleDelayedPass"/>) rather than bundled into one shared pass — and, per
    /// <see cref="IMachineGroup.TryFeedMachineInput"/>'s own remarks, does NOT chain an immediate re-feed;
    /// a machine that empties gets its OWN separately-delayed feed instead. A much less frequent full,
    /// unscoped scan still runs periodically (see <see cref="TicksSinceFullBackstopScan"/>) as a backstop
    /// for the handful of completion paths this hook can't see at all.
    /// </remarks>
    private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        if (!Context.IsWorldReady || !this.EnableAutomation || !this.Config.UseEventBasedAutomation)
            return;

        try
        {
            // MOD: removed — this used to schedule each ready machine's OUTPUT push behind
            // ModConfig.EventBasedPushPullDelaySeconds, a purely cosmetic reveal delay. That's gone now
            // that ActionDelaySeconds/ActionsPerDelayWindow (paced via
            // TryScheduleGroupBatch below) is a real progression-driven pacing system in its own right —
            // stacking the old cosmetic delay on top of that just made the very start of a save feel
            // doubly slow for no benefit. Each flagged machine is enqueued immediately instead; its own
            // group batch is still paced independently by ActionDelaySeconds as before.
            this.ProcessPendingReadyMachines();

            if (++this.TicksSinceFullBackstopScan >= ModEntry.FullBackstopScanIntervalTicks)
            {
                this.TicksSinceFullBackstopScan = 0;
                this.TryRunAutomationPass();
            }

            // MOD: added — power-starved callouts (see ProcessStarvedMachineCallouts's own remarks) need
            // their own steady, every-tick cadence with a COMPLETE view of every active group each time,
            // independent of whichever partial subset (if any) the automation passes above just touched.
            // This is cheap regardless of active group count (just reads a small cached set per group),
            // so it doesn't need to share the backstop scan's much lower frequency.
            this.MachineManager.Factory.PowerRequiredMachineSystem.ProcessStarvedMachineCallouts(this.MachineManager.GetActiveMachineGroups());
        }
        catch (Exception ex)
        {
            this.HandleError(ex, "processing machines", I18n.Message_GenericError_Verb_ProcessingMachines());
        }
    }

    /// <summary>
    /// MOD: added. Enqueue every machine <see cref="MachineReadyPatches"/> has flagged as newly ready
    /// since the last call — shared by <see cref="OnTimeChanged"/> (its own natural periodic drain) and
    /// <see cref="OnUpdateTicked"/> (an immediate, every-tick drain, so nothing has to wait for the next
    /// natural in-game 10-minute tick — see that call site's own remarks for why, e.g. Fairy Dust).
    /// </summary>
    private void ProcessPendingReadyMachines()
    {
        foreach ((IMachineGroup group, IMachine machine) in MachineReadyPatches.TakePendingReadyMachines())
        {
            this.EnqueueForAutomation(group, machine);
            this.TryScheduleGroupBatch(group);
        }
    }

    /// <inheritdoc cref="IWorldEvents.ChestInventoryChanged" />
    /// <remarks>
    /// MOD: added. Event-based automation's input-side trigger — covers every way a chest's contents
    /// can change (a player restocking it, or one chest/machine pushing into another, including
    /// Automate's own pushes), so a machine waiting on ingredients is noticed the moment they arrive
    /// instead of waiting for the next poll.
    ///
    /// MOD: fixed — this used to schedule ONE shared delayed pass that fed every currently-empty machine
    /// in the location's active groups all at once (via <see cref="MachineGroup.Automate"/>). That
    /// meant several machines all being fed by the same chest restock would visually
    /// resolve in one synchronized burst instead of each pull being independently paced. Now schedules a
    /// separate delayed <see cref="IMachineGroup.TryFeedMachineInput"/> call per currently-empty machine
    /// (see <see cref="ScheduleInputFeedsFor"/>), each with its own independent delay.
    ///
    /// MOD: fixed — narrowed further to just the group(s) that actually cover <see cref="ChestInventoryChangedEventArgs.Chest"/>'s
    /// own tile, instead of every active group in the location. This used to scan every group in the
    /// location regardless of relevance — harmless for a normal machine (its own <c>GetState() != Empty</c>
    /// check already filters out anything not actually waiting on input), but a <see cref="Machines.Objects.PoweredChestMachine"/>
    /// always reports itself as ready to act (see its own <c>GetState</c> remarks), so it got probed on
    /// EVERY chest change anywhere in the location, not just changes to containers it's actually connected
    /// to — real wasted work scheduling/queuing an action for it that almost always turned out to be a
    /// no-op, not just a cosmetic concern.
    /// </remarks>
    private void OnChestInventoryChanged(object? sender, ChestInventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady || !this.EnableAutomation || !this.Config.UseEventBasedAutomation)
            return;

        try
        {
            // MOD: added — a Junimo Chest's inventory is shared across every instance in the save, so a
            // change to ANY of them (even a standalone one with no adjacent machines, not part of any
            // tracked group at all) needs to unconditionally wake the aggregate Junimo group — see
            // ScheduleInputFeedsFor's own remarks for why the tile-narrowed lookup alone can miss it.
            bool isJunimoChest = e.Chest.SpecialChestType == Chest.SpecialChestTypes.JunimoChest;
            this.ScheduleInputFeedsFor(e.Location, e.Chest.TileLocation, isJunimoChest);

            // MOD: added — this event fires the moment the chest's contents change, which for a
            // player editing it through an open menu is BEFORE the menu closes, while its mutex is
            // still locked (MachineGroup.Automate bails out immediately for a locked container). The
            // contents won't change again just from closing the menu, so nothing else will naturally
            // retrigger this location — remember it and retry once any menu closes instead (see
            // OnMenuChanged). Harmless to queue unconditionally even when nothing was actually locked;
            // the retry is cheap and just finds nothing left to do.
            this.LocationsPendingLockedRetry.Add(e.Location);
        }
        catch (Exception ex)
        {
            this.HandleError(ex, "processing machines", I18n.Message_GenericError_Verb_ProcessingMachines());
        }
    }

    /// <inheritdoc cref="IDisplayEvents.MenuChanged" />
    /// <remarks>
    /// MOD: added. Retries every location <see cref="OnChestInventoryChanged"/> left pending (its
    /// containers may have still been locked at the time) whenever a menu closes — a chest's mutex is
    /// only ever released at that exact moment, so this is the correct signal to react to rather than
    /// polling lock state every tick. Reacts to ANY menu closing (not just a chest's) rather than trying
    /// to identify chest menus specifically, since a mod like Chests Anywhere can edit a chest that
    /// isn't even in the player's current location — the real location was already captured correctly
    /// back when <see cref="OnChestInventoryChanged"/> queued it, so this doesn't need to re-derive it
    /// from the closing menu at all.
    ///
    /// MOD: fixed — <see cref="IDisplayEvents.MenuChanged"/> is inherently LOCAL to whichever client's own
    /// screen the menu closed on; it never fires on any OTHER client, including the host. Since
    /// <see cref="LocationsPendingLockedRetry"/> is only ever populated by the HOST's own
    /// <see cref="OnChestInventoryChanged"/> (gated behind <see cref="EnableAutomation"/>, i.e.
    /// <see cref="Context.IsMainPlayer"/>), a FARMHAND closing the exact chest menu that caused a location
    /// to be queued there produced a <see cref="IDisplayEvents.MenuChanged"/> the host could never see —
    /// so if that container was STILL locked the one time the host's own (delay-paced) automation attempt
    /// happened to run, nothing would EVER retry it again, until some unrelated later event happened to
    /// touch that location. Confirmed directly via user report: a farmhand manually adding items to a
    /// Powered Chest sometimes wasn't picked up after closing it. Now every non-host client that closes a
    /// menu tells the host directly (a cheap, occasional per-menu-close message — nothing like the
    /// per-transfer frequency that made a similar message-based approach too expensive elsewhere, see
    /// <see cref="ContainerVisualEffects"/>'s own remarks), and the host retries from either trigger.
    /// </remarks>
    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (e.NewMenu != null || !Context.IsWorldReady || !this.Config.UseEventBasedAutomation)
            return;

        if (!Context.IsMainPlayer)
        {
            // MOD: added — see this method's own remarks. Sent unconditionally on any menu closing,
            // even if nothing is actually pending on the host right now — cheap either way, and this
            // client has no way to know the host's own LocationsPendingLockedRetry state.
            if (Context.IsMultiplayer)
            {
                this.Helper.Multiplayer.SendMessage(
                    message: true,
                    messageType: ModEntry.NotifyHostMenuClosedMessageType,
                    modIDs: [this.ModManifest.UniqueID],
                    playerIDs: [Game1.MasterPlayer.UniqueMultiplayerID]
                );
            }
            return;
        }

        if (!this.EnableAutomation)
            return;

        this.RetryLocationsPendingLockedRetry();
    }

    /// <summary>
    /// MOD: added. Retry every location <see cref="LocationsPendingLockedRetry"/> is holding — shared by
    /// <see cref="OnMenuChanged"/> (the host's own menu closing) and <see cref="OnModMessageReceived"/>
    /// (a farmhand's own menu closing, reported via <see cref="NotifyHostMenuClosedMessageType"/>) so the
    /// two triggers can't drift apart. Host-only to call — every caller already checks
    /// <see cref="EnableAutomation"/> first.
    /// </summary>
    private void RetryLocationsPendingLockedRetry()
    {
        if (this.LocationsPendingLockedRetry.Count == 0)
            return;

        try
        {
            GameLocation[] locations = this.LocationsPendingLockedRetry.ToArray();
            this.LocationsPendingLockedRetry.Clear();

            foreach (GameLocation location in locations)
                this.ScheduleInputFeedsFor(location);
        }
        catch (Exception ex)
        {
            this.HandleError(ex, "processing machines", I18n.Message_GenericError_Verb_ProcessingMachines());
        }
    }

    /// <summary>
    /// MOD: changed. Run one automation pass across every currently-active machine group — shared by
    /// interval mode's regular polling, event-based mode's rare periodic backstop scan, and the one-shot
    /// pass after a day starts or the config changes, so the actual processing logic isn't duplicated
    /// between them.
    ///
    /// When <see cref="ModConfig.ActionDelaySeconds"/> is 0, this is the fast path — one instant
    /// <see cref="IMachineGroup.Automate"/> call per group. When it's greater than zero, each group's own
    /// Done/Empty machines are queued via <see cref="EnqueueForAutomation"/> instead, exactly like the
    /// fine-grained event-based hooks do, so it doesn't matter which trigger found a machine.
    /// </summary>
    /// <summary>
    /// MOD: added. Get <see cref="ModConfig.ActionDelaySeconds"/> after applying the Power Relay's
    /// global efficiency bonus, floored at <see cref="ModConfig.PowerRelayMinimumActionDelaySeconds"/>
    /// (see <see cref="PowerRelaySystem.GetEffectiveActionDelaySeconds"/>, the actual source of truth —
    /// this is a thin wrapper so every pacing call site below reads through the same method name it
    /// already used before the Relay mechanic existed). If that floor is ever configured to 0 or below,
    /// enough Relay levels can still reach the instant/unbatched fast path every call site below already
    /// has for a literal 0 config value.
    /// </summary>
    private float GetEffectiveActionDelaySeconds()
    {
        // MOD: added — OverwriteAutomationDelay is meant to pin ActionDelaySeconds
        // to exactly the configured value, full stop; the Power Relay's in-game efficiency bonus (like
        // any other "upgrade" a player might have) shouldn't still be adjusting it on top of that once
        // the player has explicitly opted into overwriting it themselves.
        if (this.Config.OverwriteAutomationDelay)
            return this.Config.ActionDelaySeconds;

        return this.PowerRelaySystem.GetEffectiveActionDelaySeconds();
    }

    /// <summary>
    /// MOD: added. Get <see cref="ModConfig.ActionsPerDelayWindow"/> after applying the Power Relay's
    /// global efficiency bonus (see <see cref="PowerRelaySystem.GetActionsPerDelayWindowBonus"/>). A
    /// configured value of 0 or less already means "unlimited" and stays that way regardless of the
    /// bonus — there's no more "unlimited" to add to.
    /// </summary>
    private int GetEffectiveActionsPerDelayWindow()
    {
        // MOD: fixed — this checked OverwriteAutomationDelay (the DELAY flag) instead of
        // OverwriteAutomationActions, a copy-paste mistake — meaning enabling/disabling "Overwrite
        // automation actions" never actually did anything as long as the DELAY overwrite happened to be
        // on (this branch would still fire) or off (it would still skip to the branch below regardless of
        // what the ACTIONS checkbox said). Same reasoning as GetEffectiveActionDelaySeconds above: an
        // overwrite should mean exactly that, with no Power Relay bonus layered on top.
        if (this.Config.OverwriteAutomationActions)
            return this.Config.ActionsPerDelayWindow;

        // MOD: fixed — used to read Config.ActionsPerDelayWindow directly here too, which has the exact
        // same staleness problem as ActionDelaySeconds (see getBaseActionDelaySeconds's own remarks) —
        // only reliably equal to the true default right after a config file load, not after a live GMCM
        // edit. Reading the constant directly means this is correct the instant the checkbox is
        // unchecked, with no dependency on the raw config value having been reset first.
        return ModConfig.DefaultActionsPerDelayWindow <= 0
            ? ModConfig.DefaultActionsPerDelayWindow
            : ModConfig.DefaultActionsPerDelayWindow + this.PowerRelaySystem.GetActionsPerDelayWindowBonus();
    }

    private void TryRunAutomationPass()
    {
        IMachineGroup[] activeGroups = this.MachineManager.GetActiveMachineGroups().ToArray();

        if (this.GetEffectiveActionDelaySeconds() <= 0)
        {
            foreach (IMachineGroup group in activeGroups)
            {
                // MOD: added — only pay for a Stopwatch (a real-time-clock query, plus an allocation) when the
                // perf overlay is actually recording; this path runs for every active group every pass, so
                // skipping it the rest of the time avoids pure overhead with nothing reading the result.
                if (AutomationPerfTracker.IsRecording)
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    group.Automate();
                    AutomationPerfTracker.RecordFullScan(stopwatch.Elapsed.TotalMilliseconds);
                }
                else
                    group.Automate();
            }
        }
        else
        {
            foreach (IMachineGroup group in activeGroups)
            {
                foreach (IMachine machine in group.Machines)
                {
                    if (machine.GetState() is not (MachineState.Done or MachineState.Empty))
                        continue;

                    // MOD: added — in event-based mode, don't let this blind periodic/backstop/day-start
                    // sweep enqueue a IChestLikeMachine (e.g. PoweredChestMachine) at all. Its GetState() is
                    // hardcoded to always report Empty, so unlike a real machine, this check carries no
                    // information about whether it actually has anything new to move. Event-based mode
                    // already has a precise, comprehensive discovery path for it instead:
                    // OnChestInventoryChanged/ScheduleInputFeedsFor when a neighboring container actually
                    // changes, and TakeGroupsWithNewMembers/TakeRescannedGroups when its own group is
                    // (re)built (including right after a save loads or a new day starts) — each anchors its
                    // own delay to a real reason, not a periodic sweep. Interval mode has no such alternative
                    // (it never calls those event hooks), so it still needs this blind sweep below to
                    // discover chest-like machines at all — passed through as an unconfirmed signal (see
                    // EnqueueForAutomation's own remarks) so a later confirmed one can still promote it.
                    if (this.Config.UseEventBasedAutomation && MachineGroup.IsChestLikeMachine(machine))
                        continue;

                    this.EnqueueForAutomation(group, machine, isConfirmedSignal: false);
                }

                this.TryScheduleGroupBatch(group);
            }
        }

        // MOD: added — power-required-machines wake-up callouts need cross-rebuild tracking by location
        // (see PowerRequiredMachineSystem.ProcessStarvedMachineCallouts's own remarks). Deliberately NOT
        // delayed — it's a reminder message, not a push/pull action, and reads a small cached set per group.
        this.MachineManager.Factory.PowerRequiredMachineSystem.ProcessStarvedMachineCallouts(activeGroups);
    }

    /// <summary>
    /// MOD: added. Automate a single machine that either <see cref="Patches.MachineReadyPatches"/> flagged
    /// as ready, <see cref="OnChestInventoryChanged"/>/<see cref="OnMenuChanged"/> found newly feedable, or
    /// <see cref="TryRunAutomationPass"/>'s bulk scan found — pushing its output (via
    /// <see cref="IMachineGroup.TryPushMachineOutput"/>) if it's Done, AND feeding it fresh input (via
    /// <see cref="IMachineGroup.TryFeedMachineInput"/>) if it's Empty, checked fresh so BOTH happen in the
    /// same call when a push immediately empties the machine.
    /// </summary>
    /// <param name="group">The machine's owning group.</param>
    /// <param name="machine">The machine to automate.</param>
    /// <returns>Whether the machine actually did anything.</returns>
    private bool AutomateMachine(IMachineGroup group, IMachine machine)
    {
        MachineState stateBefore = machine.GetState();
        if (stateBefore is not (MachineState.Done or MachineState.Empty))
            return false;

        // MOD: added — only pay for a Stopwatch (a real-time-clock query, plus an allocation) when the perf
        // overlay is actually recording; this runs for every single machine automation attempt, potentially
        // thousands of times per game session on a large farm, so skipping it otherwise avoids pure
        // per-call overhead with nothing ever reading the result.
        Stopwatch? stopwatch = AutomationPerfTracker.IsRecording ? Stopwatch.StartNew() : null;

        // MOD: track whether a real commit happened via TryPushMachineOutput/TryFeedMachineInput's OWN
        // return values, NOT by comparing machine.GetState() before and after — that comparison is always a
        // false negative for a IChestLikeMachine like PoweredChestMachine, whose GetState() is hardcoded to
        // always report MachineState.Empty regardless of what SetInput just did.
        bool didSomething = false;

        if (stateBefore is MachineState.Done)
        {
            bool pushed = group.TryPushMachineOutput(machine);
            didSomething |= pushed;
        }

        if (machine.GetState() is MachineState.Empty)
        {
            bool fed = group.TryFeedMachineInput(machine);
            didSomething |= fed;

            // MOD: fixed — a machine fed through one Input Conduit network but pushing output through a
            // SEPARATE Output Conduit network belongs to TWO different machine groups at once (different
            // connector "materials" never merge into one shared group — see MachineGroupFactory's own
            // remarks on step 4), each seeing only its own half of the machine's storage. Failing to feed
            // it through THIS group doesn't mean nothing can — the chest with its actual ingredients might
            // only be reachable through the OTHER group sharing this same tile. Previously nothing
            // rescheduled that other group until the periodic full backstop scan happened to notice (the
            // ONLY path that already worked correctly here, since it iterates every active group
            // unconditionally rather than being scoped to just one) — the item would be taken fine,
            // but a new one wouldn't get inserted until the hourly check. Omni Conduit was never affected,
            // since input and output flow through the same single connector network/group there.
            //
            // MOD: fixed — a first attempt at this called ScheduleInputFeedsFor (which includes THIS same
            // group, since it covers the machine's own tile too) and caused a real hang: this method is
            // itself called from RunGroupBatch's own synchronous while loop draining THIS group's queue,
            // and ScheduleInputFeedsFor's ScheduleGroupCheck re-enqueues every Done/Empty machine in the
            // group it's given — including the machine that was JUST dequeued a moment ago (now removed
            // from QueuedMachines, so the dedup no longer blocks re-adding it). Every failed feed
            // attempt re-added itself right back onto the queue being drained, so the while loop's
            // queue.Count never reached 0 and the game hung completely (confirmed via a SMAPI log showing
            // the same 3 groups cycling "already armed — skipped re-arm" thousands of times with real game
            // time never advancing). Only nudging OTHER groups — never the one currently being drained —
            // and only THIS specific machine (not a broad rescan of every machine in that other group)
            // avoids re-touching the in-progress queue entirely, so there's nothing left to loop on.
            if (!fed)
            {
                foreach (IMachineGroup otherGroup in this.MachineManager.GetActiveMachineGroupsFor(machine.Location, new Vector2(machine.TileArea.X, machine.TileArea.Y)))
                {
                    if (ReferenceEquals(otherGroup, group))
                        continue;

                    // MOD: fixed — resolve the OTHER group's own wrapper instance for this same underlying
                    // machine, instead of reusing THIS group's wrapper reference. A shared machine gets a
                    // separate MachineWrapper instance per group (see MachineGroupFactory step 6's own
                    // remarks), but QueuedMachines dedups globally by wrapper reference — reusing this
                    // group's wrapper silently parked it in the OTHER group's queue instead. From then on,
                    // the machine's real owning group saw "already queued" on every future check (it really
                    // was queued — just under a foreign group) and could never re-enqueue its OWN wrapper,
                    // so it stopped getting turns entirely unless/until the foreign group happened to drain
                    // it — confirmed via a [containersync] log showing a Powered Chest's omni/output group
                    // permanently stuck on "nothing queued, nothing to prime" once a busier sibling group's
                    // nudge claimed its wrapper first.
                    IMachine? otherGroupsMachine = Array.Find(otherGroup.Machines, m =>
                        m.TileArea.X == machine.TileArea.X && m.TileArea.Y == machine.TileArea.Y && m.MachineTypeID == machine.MachineTypeID);
                    if (otherGroupsMachine == null)
                        continue;

                    this.EnqueueForAutomation(otherGroup, otherGroupsMachine);
                    this.TryScheduleGroupBatch(otherGroup);
                }
            }
        }

        if (stopwatch != null)
            AutomationPerfTracker.RecordFlaggedBatch(stopwatch.Elapsed.TotalMilliseconds);

        // MOD: added — see ChestLikeAttemptStreaks's own remarks. Only tracked for a IChestLikeMachine:
        // an ordinary machine's own GetState() already prevents a genuinely idle one from being re-attempted
        // at all, so a streak would never accumulate for one in the first place.
        if (MachineGroup.IsChestLikeMachine(machine))
        {
            ChestLikeAttemptState state = this.ChestLikeAttemptStreaks.GetOrCreateValue(machine);
            state.ConsecutiveFruitless = didSomething ? 0 : state.ConsecutiveFruitless + 1;
            state.LastAttemptAtMs = this.UnpausedElapsedMs;
        }

        return didSomething;
    }

    /// <summary>
    /// MOD: added. Add a machine to its group's FIFO action queue (see <see cref="GroupActionQueues"/>) if
    /// it isn't already there (see <see cref="QueuedMachines"/>'s own remarks for why that dedup matters). A
    /// no-op while <see cref="ModConfig.ActionDelaySeconds"/> is 0, since nothing consults the queue in that
    /// mode — <see cref="TryScheduleGroupBatch"/> just runs <see cref="IMachineGroup.Automate"/> directly
    /// instead.
    /// </summary>
    /// <param name="group">The machine's owning group.</param>
    /// <param name="machine">The machine believed ready for a push/pull.</param>
    /// <param name="isConfirmedSignal">
    /// MOD: added. Whether this call is backed by a specific container actually changing (see
    /// <see cref="ScheduleInputFeedsFor"/>) as opposed to a blind poll that says nothing about whether real
    /// work exists (a group merely being built/rebuilt, or the periodic/interval bulk sweep). Only matters
    /// for a <see cref="IChestLikeMachine"/> that's already queued: a blind entry gets refreshed to a fresh
    /// full delay the first time a confirmed signal arrives for it (see below), but a confirmed entry is
    /// left alone by anything arriving after it — it's already honestly counting down toward real work, so
    /// resetting it again would just delay something that's already correctly in progress. Irrelevant for
    /// an ordinary machine, whose <see cref="IMachine.GetState"/> already IS a real signal by itself.
    /// </param>
    private void EnqueueForAutomation(IMachineGroup group, IMachine machine, bool isConfirmedSignal = true)
    {
        float effectiveActionDelaySeconds = this.GetEffectiveActionDelaySeconds();
        if (effectiveActionDelaySeconds <= 0)
            return;

        // MOD: added — this machine's own entry isn't eligible to be committed until its own full delay
        // has elapsed from right now, regardless of whether the group's batch timer is already armed and
        // about to fire sooner than that — see GroupActionQueues's own remarks for why.
        //
        // MOD: added, then removed for good — this used to add a small extra jitter (0-15% of the base
        // delay) on top of the delay below, to reduce (not eliminate — it's a random walk, so it can only
        // ever make the coincidence rarer, never impossible) how often two independently-paced groups that
        // cross-trigger each other landed suspiciously close together. No longer needed: the REAL fix for
        // that is PoweredChestMachine.TryMoveOne now charging a PULL against its source container's own
        // ThrottledContainer budget (the same one a PUSH already respects), which deterministically
        // prevents any one container being touched twice within the same delay window, regardless of how
        // the two machines' independent timers happen to line up. Every entry's delay is exactly
        // effectiveActionDelaySeconds now — nothing added, nothing random.
        double eligibleAtMs = this.UnpausedElapsedMs + effectiveActionDelaySeconds * 1000;

        if (!this.QueuedMachines.Add(machine))
        {
            // MOD: added — a blind entry (queued without a confirmed signal — see this parameter's own
            // remarks) is just a placeholder that might not represent any real work at all. If THIS attempt
            // is the first confirmed signal to arrive for it, promote it: refresh to a fresh full delay from
            // right now (so the real opportunity gets its own honest wait, same as every other machine gets)
            // and mark it confirmed. An already-confirmed entry is left untouched no matter what arrives
            // after it — it's already honestly counting down toward real work, so this is deliberately NOT
            // "refresh on every new signal": that would let a busy neighborhood reset it forever and starve
            // it, and would also delay work that's already correctly in progress for no reason. A second
            // blind signal against a still-blind entry is likewise a no-op — nothing new was actually
            // confirmed, so there's nothing to promote it with yet.
            if (isConfirmedSignal && MachineGroup.IsChestLikeMachine(machine) && this.GroupActionQueues.TryGetValue(group, out LinkedList<(IMachine Machine, double EligibleAtMs, bool Confirmed)>? existingQueue))
            {
                for (LinkedListNode<(IMachine Machine, double EligibleAtMs, bool Confirmed)>? node = existingQueue.First; node != null; node = node.Next)
                {
                    if (ReferenceEquals(node.Value.Machine, machine))
                    {
                        if (!node.Value.Confirmed)
                            node.Value = (machine, eligibleAtMs, true);
                        break;
                    }
                }
            }

            return; // already queued — see QueuedMachines's own remarks
        }

        if (!this.GroupActionQueues.TryGetValue(group, out LinkedList<(IMachine Machine, double EligibleAtMs, bool Confirmed)>? queue))
            this.GroupActionQueues[group] = queue = new LinkedList<(IMachine, double, bool)>();

        // MOD: added — a chest-like machine jumps to the front instead of the back, so it's never crowded
        // out of its own turn by ordinary machines already ahead of it in FIFO order — see GroupActionQueues's
        // own remarks for why.
        if (MachineGroup.IsChestLikeMachine(machine))
            queue.AddFirst((machine, eligibleAtMs, isConfirmedSignal));
        else
            queue.AddLast((machine, eligibleAtMs, isConfirmedSignal));
    }

    /// <summary>
    /// MOD: added. Arm a group's batch timer if it has queued work AND isn't already priming for one — the
    /// SINGLE mechanism every trigger (<see cref="Patches.MachineReadyPatches"/>, <see cref="OnChestInventoryChanged"/>,
    /// <see cref="TryRunAutomationPass"/>'s bulk scan, a newly-joined group) shares, so it doesn't matter
    /// which one found a group's work. Safe to call unconditionally after enqueuing (or with nothing newly
    /// enqueued at all) — a group with an empty <see cref="GroupActionQueues"/> entry, or one already
    /// priming, is simply left alone.
    ///
    /// A group with nothing queued does nothing at all (no repeating heartbeat); the INSTANT it gets queued
    /// work, it starts priming (waiting <see cref="ModConfig.ActionDelaySeconds"/>, even for the very first
    /// batch); once primed, <see cref="RunGroupBatch"/> fires one "shot" (up to
    /// <see cref="ModConfig.ActionsPerDelayWindow"/> actions, taken from the FRONT of the queue in the order
    /// they were added) and immediately starts priming again if more queued work remains, or goes back to
    /// doing nothing if the queue is now empty.
    ///
    /// <see cref="ModConfig.ActionDelaySeconds"/> <c>&lt;= 0</c> bypasses all of this and runs the group's
    /// normal <see cref="IMachineGroup.Automate"/> pass directly instead, matching the original
    /// instant/unbatched behavior.
    /// </summary>
    /// <param name="group">The group to schedule a batch for.</param>
    private void TryScheduleGroupBatch(IMachineGroup group)
    {
        float effectiveActionDelaySeconds = this.GetEffectiveActionDelaySeconds();
        if (effectiveActionDelaySeconds <= 0)
        {
            group.Automate();
            return;
        }

        if (!this.GroupActionQueues.TryGetValue(group, out LinkedList<(IMachine Machine, double EligibleAtMs, bool Confirmed)>? queue) || queue.Count == 0)
            return; // nothing queued for this group — nothing to prime for

        if (!this.ArmedGroupBatches.Add(group))
            return; // already priming — it'll drain the queue (including anything just added to it) when it fires

        // MOD: added — arm for however long until the SOONEST entry in the queue actually becomes
        // eligible, not blindly for effectiveActionDelaySeconds. Every entry's own EligibleAtMs already
        // includes 0-15% of extra jitter on top of the base delay (see EnqueueForAutomation's own remarks
        // for why), which is always >= effectiveActionDelaySeconds — so arming for the flat config value
        // meant this timer almost always fired a hair BEFORE the entry that triggered it had actually
        // become eligible, found nothing to commit, and then re-armed for another FULL effectiveActionDelaySeconds
        // from that point (see the "genuinely isn't eligible yet" re-priming below) — nearly doubling the
        // real wait for what should have been one single delay. Confirmed directly via user report: actions
        // were consistently taking close to double the configured delay. Arming for the queue's own actual
        // soonest deadline instead means the timer fires right when there's genuinely something to do.
        double minEligibleAtMs = double.MaxValue;
        foreach ((IMachine _, double entryEligibleAtMs, bool _) in queue)
        {
            if (entryEligibleAtMs < minEligibleAtMs)
                minEligibleAtMs = entryEligibleAtMs;
        }
        double curTimeMs = this.UnpausedElapsedMs;

        // MOD: added — if the soonest entry is ALREADY eligible (not in the future), this call is
        // re-priming right after RunGroupBatch's own drain loop hit its ActionsPerDelayWindow budget cap
        // with more already-eligible work left over — a real, live crash was traced to exactly this case:
        // an "eligible in the past" entry made the line above compute an arm delay of (near) zero, which
        // hit RunOrScheduleDelayedPass's own "0 or less runs immediately" fast path and called RunGroupBatch
        // again SYNCHRONOUSLY, which hit the SAME budget cap again with the SAME leftover entries and
        // re-armed for (near) zero again — an infinite synchronous recursion with no time ever elapsing
        // between iterations, blowing the call stack (a StackOverflowException, which .NET can't catch or
        // log, so the process just died instantly with nothing useful in the log). This case isn't a timing
        // mismatch to correct for — genuinely waiting a full fresh window before the budget replenishes is
        // exactly what ActionsPerDelayWindow is supposed to mean, so it gets the ordinary flat delay instead
        // of the precise-timing calculation above (which only applies when nothing is eligible yet).
        float armDelaySeconds = minEligibleAtMs > curTimeMs
            ? (float)((minEligibleAtMs - curTimeMs) / 1000.0)
            : effectiveActionDelaySeconds;

        this.RunOrScheduleDelayedPass(() => this.RunGroupBatch(group), armDelaySeconds, group);
    }

    /// <summary>
    /// MOD: added. Fire one group's batch (its "shot") — drains up to <see cref="ModConfig.ActionsPerDelayWindow"/>
    /// genuine actions from the FRONT of its <see cref="GroupActionQueues"/> entry (0 or less means
    /// unlimited — drain the whole queue in one shot), then immediately starts priming again (see
    /// <see cref="TryScheduleGroupBatch"/>) if the queue still isn't empty afterward.
    ///
    /// Each dequeued machine's state is re-validated fresh by <see cref="AutomateMachine"/> right before
    /// acting on it — a machine that's no longer Done/Empty by the time its turn comes up (e.g. it was
    /// serviced some other way in the meantime) is a safe, free no-op, not counted against the batch budget.
    /// </summary>
    /// <param name="group">The group whose batch just came due.</param>
    private void RunGroupBatch(IMachineGroup group)
    {
        if (!this.ArmedGroupBatches.Remove(group))
            return; // stale fire for a group whose pacing state was since purged/reset — nothing to do

        if (!this.GroupActionQueues.TryGetValue(group, out LinkedList<(IMachine Machine, double EligibleAtMs, bool Confirmed)>? queue))
            return;

        int committed = 0;
        int effectiveActionsPerDelayWindow = this.GetEffectiveActionsPerDelayWindow();
        bool unlimited = effectiveActionsPerDelayWindow <= 0;
        double curTimeMs = this.UnpausedElapsedMs;

        // MOD: added. A machine dequeued here and dropped for good is only rediscovered if some OTHER
        // event happens to touch this group again — its own state didn't change (still Done, holding the
        // same output), so nothing guarantees that happens soon. Collected here (not requeued mid-loop, to
        // avoid immediately re-trying the SAME still-exhausted budget in this SAME pass) and pushed back
        // onto the FRONT of this group's own queue once the drain is done, so it gets first crack at this
        // group's next scheduled batch instead of competing from scratch with whatever else got queued in
        // the meantime. See MachineGroup.WasLastPushRejectedForBudget's own remarks for why only a
        // purely-budget rejection earns this treatment, not a genuine one.
        List<(IMachine Machine, double EligibleAtMs, bool Confirmed)>? budgetRejectedMachines = null;

        while (unlimited || committed < effectiveActionsPerDelayWindow)
        {
            // MOD: added — find the first entry (front-to-back, respecting the existing chest-jumps-to-
            // front priority) whose OWN full delay has actually elapsed, rather than just always taking
            // the front of the queue. The group's shared timer firing only means IT'S time to check again
            // — it doesn't mean every entry currently sitting in the queue has actually waited out its own
            // full ActionDelaySeconds yet (one could have been enqueued moments ago, mid-countdown, or
            // jumped ahead of an older ordinary machine by the chest-priority rule above). See
            // GroupActionQueues's own remarks for the exact symptom this fixes.
            LinkedListNode<(IMachine Machine, double EligibleAtMs, bool Confirmed)>? node = queue.First;
            while (node != null && node.Value.EligibleAtMs > curTimeMs)
                node = node.Next;

            if (node == null)
                break; // nothing left in the queue is eligible yet this shot

            (IMachine machine, double eligibleAtMs, bool confirmed) = node.Value;
            queue.Remove(node);
            this.QueuedMachines.Remove(machine);

            bool wasDoneBefore = machine.GetState() is MachineState.Done;

            if (this.AutomateMachine(group, machine))
            {
                committed++;
            }
            else if (wasDoneBefore && group is MachineGroup { WasLastPushRejectedForBudget: true })
            {
                (budgetRejectedMachines ??= []).Add((machine, eligibleAtMs, confirmed));
            }
        }

        if (budgetRejectedMachines != null)
        {
            for (int i = budgetRejectedMachines.Count - 1; i >= 0; i--)
            {
                (IMachine machine, double eligibleAtMs, bool confirmed) = budgetRejectedMachines[i];
                queue.AddFirst((machine, eligibleAtMs, confirmed)); // MOD: changed — already-eligible, so it keeps its ORIGINAL timestamp instead of being reset to a fresh delay
                this.QueuedMachines.Add(machine);
            }
        }

        // MOD: added — if anything's left, re-prime; TryScheduleGroupBatch itself now arms for exactly
        // however long until the soonest remaining entry becomes eligible (see its own remarks), so this
        // never waits longer than necessary and — since that arm time is always derived from the entries'
        // own EligibleAtMs, never a flat guess — can't reintroduce the "acted before its own delay elapsed"
        // bug this whole change exists to close either.
        if (queue.Count > 0)
            this.TryScheduleGroupBatch(group);
    }

    /// <summary>
    /// Schedule an independent, separately-delayed <see cref="EnqueueForAutomation"/> for every machine
    /// that's CURRENTLY Done or Empty in a location's active groups — used by <see cref="OnChestInventoryChanged"/>/
    /// <see cref="OnMenuChanged"/> instead of one shared pass covering the whole location, so several
    /// machines fed by the same chest restock each get their own independent reveal pacing rather than all
    /// resolving in one synchronized burst. The candidate list is captured now (at the moment the chest
    /// changed), but each individual feed re-validates the machine's state itself right before acting, so a
    /// machine that's no longer Done/Empty by the time its own delay elapses is safely skipped instead of
    /// double-fed.
    ///
    /// MOD: fixed — checks BOTH Done and Empty (previously Empty only), delegating the actual per-machine
    /// scheduling to <see cref="ScheduleGroupCheck"/> so the two never drift apart again. A chest's
    /// contents changing isn't just "input became available" (relevant to an Empty machine) — it's also
    /// "output space may have freed up" (relevant to a Done machine blocked on a full destination).
    ///
    /// MOD: fixed — <paramref name="isJunimoChest"/> unconditionally also wakes the aggregate
    /// <see cref="MachineManager.JunimoMachineGroup"/>, bypassing the tile-narrowed lookup for it
    /// specifically. A Junimo Chest's inventory is shared across every instance in the save, so a change
    /// to ANY of them can unblock a machine elsewhere using a completely different instance — but the
    /// tile-narrowed lookup only matches a group whose OWN tracked footprint covers the changed chest's
    /// tile, which misses this entirely for a Junimo Chest that isn't itself adjacent to any machine (not
    /// part of any tracked group at all, so no footprint contains its tile) even though its contents are
    /// still part of the same shared inventory every Junimo-touching machine reads from.
    /// </summary>
    /// <param name="location">The location whose active groups to scan for Done/Empty machines.</param>
    /// <param name="originTile">MOD: added. The specific tile whose container just changed, if known — narrows the scan to just the group(s) covering that tile instead of every active group in the location (see <see cref="OnChestInventoryChanged"/>'s own remarks for why that narrowing matters). Left <c>null</c> for <see cref="OnMenuChanged"/>'s locked-container retry, which no longer has a specific tile to narrow to by the time it fires — that path keeps scanning the whole location as a broader backstop.</param>
    /// <param name="isJunimoChest">MOD: added. Whether the container that changed is a Junimo Chest — see this method's own remarks for why that unconditionally wakes the aggregate Junimo group regardless of <paramref name="originTile"/>.</param>
    private void ScheduleInputFeedsFor(GameLocation location, Vector2? originTile = null, bool isJunimoChest = false)
    {
        IEnumerable<IMachineGroup> groups = originTile.HasValue
            ? this.MachineManager.GetActiveMachineGroupsFor(location, originTile.Value)
            : this.MachineManager.GetActiveMachineGroupsFor(location);

        foreach (IMachineGroup group in groups)
            this.ScheduleGroupCheck(group);

        // MOD: added — see this method's own remarks for why a Junimo Chest change needs this even when
        // the tile-narrowed lookup above didn't already include the aggregate group (e.g. the changed
        // chest isn't itself adjacent to any machine). Only needed when originTile narrowed the lookup —
        // the unnarrowed overload already includes the Junimo group unconditionally. Safe to schedule
        // again even if it WAS already included above; EnqueueForAutomation's own dedup absorbs it.
        if (isJunimoChest && originTile.HasValue && this.MachineManager.JunimoMachineGroup.HasInternalAutomation)
            this.ScheduleGroupCheck(this.MachineManager.JunimoMachineGroup);
    }

    /// <summary>
    /// MOD: added. Give one specific group's currently Done/Empty machines a one-time proactive automation
    /// check — used right after that group gains a new member (see
    /// <see cref="MachineManager.TakeGroupsWithNewMembers"/>), since neither a newly-joined container nor a
    /// newly-joined machine fires any of event-based mode's other triggers on its own: a container coming
    /// into range doesn't fire <see cref="OnChestInventoryChanged"/> (nothing was stored/removed, it just
    /// became reachable), and a machine joining isn't itself "becoming ready" for
    /// <see cref="Patches.MachineReadyPatches"/> to notice. Checks BOTH states, since a machine could
    /// already have been sitting Done with nowhere to push before the new member arrived — also shared by
    /// <see cref="ScheduleInputFeedsFor"/> for the same reason. Each machine is enqueued immediately (see
    /// <see cref="OnTimeChanged"/>'s own remarks for why the old cosmetic per-machine delay is gone); its
    /// own group batch is still paced independently by <see cref="ModConfig.ActionDelaySeconds"/> via
    /// <see cref="TryScheduleGroupBatch"/>, rather than automating the whole group synchronously in one call.
    /// </summary>
    /// <param name="group">The group to check.</param>
    /// <param name="isConfirmedSignal">
    /// MOD: added. Whether this check was triggered by a specific container actually changing (the
    /// <see cref="ScheduleInputFeedsFor"/> callers — <c>true</c>, the default) as opposed to the group
    /// merely being built/rebuilt (the <see cref="MachineManager.TakeGroupsWithNewMembers"/>/
    /// <see cref="MachineManager.TakeRescannedGroups"/> callers — <c>false</c>, passed explicitly), which
    /// says nothing about whether any of its members actually have real work available yet. See
    /// <see cref="EnqueueForAutomation"/>'s own remarks for how this is used.
    /// </param>
    private void ScheduleGroupCheck(IMachineGroup group, bool isConfirmedSignal = true)
    {
        // MOD: added — lazily computed (and cached) the FIRST time the backoff check below actually needs
        // it, not unconditionally at the top of the loop. GetEffectiveActionDelaySeconds ultimately sums
        // Power Relay shard levels across every relay in the save (PowerRelaySystem.SumAcrossRelays) —
        // real work, not a cheap read — so this avoids paying it at all for the common case (no chest-like
        // machine in this group is even eligible for backoff), while still reusing the same value instead
        // of re-deriving it for every additional candidate later in the same loop.
        float? effectiveActionDelaySeconds = null;

        foreach (IMachine machine in group.Machines)
        {
            MachineState state = machine.GetState();
            if (state is not (MachineState.Done or MachineState.Empty))
                continue;

            // MOD: added — see ChestLikeAttemptStreaks's own remarks. A IChestLikeMachine that's struck out
            // several times in a row doesn't get an automatic re-check on EVERY confirmed signal for its
            // whole group anymore; it waits out a short cooldown instead, so it stops crowding the group's
            // limited ActionsPerDelayWindow budget with attempts that keep finding nothing. A blind signal
            // (isConfirmedSignal: false — the group itself was just built/rebuilt) always bypasses this, so
            // a genuinely new connection is still checked immediately regardless of any prior streak.
            if (isConfirmedSignal
                && MachineGroup.IsChestLikeMachine(machine)
                && this.ChestLikeAttemptStreaks.TryGetValue(machine, out ChestLikeAttemptState? attemptState)
                && attemptState.ConsecutiveFruitless >= ModEntry.ChestLikeFruitlessStreakThreshold
                && this.UnpausedElapsedMs - attemptState.LastAttemptAtMs < ModEntry.ChestLikeBackoffWindowsCooldown * (effectiveActionDelaySeconds ??= this.GetEffectiveActionDelaySeconds()) * 1000)
            {
                continue; // still backed off — skip this one, but keep checking the rest of the group
            }

            this.EnqueueForAutomation(group, machine, isConfirmedSignal);
            this.TryScheduleGroupBatch(group);
        }
    }

    /// <summary>
    /// Run an automation pass now, or after <paramref name="delaySeconds"/> real-time seconds if that's
    /// greater than zero. <paramref name="action"/> is evaluated lazily at whichever point it actually runs
    /// — for a delayed pass, that means anything it reads (e.g. a fresh <see cref="MachineManager.GetActiveMachineGroupsFor"/>
    /// lookup) reflects state as of the delay elapsing, not the moment it was originally scheduled.
    /// </summary>
    /// <param name="action">The automation pass to run.</param>
    /// <param name="delaySeconds">How many real-time seconds to wait before running <paramref name="action"/> — 0 or less runs it immediately instead of queuing it.</param>
    /// <param name="group">MOD: added. The group <paramref name="action"/> is scoped to, if any — see <see cref="PendingDelayedPasses"/>'s own remarks for why this needs to be tracked.</param>
    private void RunOrScheduleDelayedPass(Action action, float delaySeconds, IMachineGroup? group = null)
    {
        if (delaySeconds <= 0)
        {
            action();
            return;
        }

        double curTimeMs = this.UnpausedElapsedMs;
        double scheduledTimeMs = curTimeMs + delaySeconds * 1000;
        this.PendingDelayedPasses.Add((curTimeMs, scheduledTimeMs, action, group));
    }

    /// <summary>
    /// MOD: added. Drain <see cref="PendingContainerChangeNotifications"/> and call
    /// <see cref="ScheduleInputFeedsFor"/> for each — meant to be called once per <see cref="OnUpdateTicked"/>,
    /// strictly AFTER every automation pass that tick (<see cref="TryRunAutomationPass"/>,
    /// <see cref="RunDuePendingPasses"/>) has fully finished, never from in between or nested inside one.
    /// See <see cref="PendingContainerChangeNotifications"/>'s own remarks for the hang/recursion this
    /// avoids by deferring instead of notifying synchronously from inside the container write itself.
    ///
    /// Swaps out the list before iterating (rather than clearing it after) so that if
    /// <see cref="ScheduleInputFeedsFor"/> itself triggers more container writes this same call — e.g. the
    /// zero-delay path running <c>group.Automate()</c> synchronously — those newly-queued notifications land
    /// in a fresh list for NEXT tick's drain, instead of either being lost (if cleared after) or throwing
    /// (mutating the list this method is still enumerating).
    /// </summary>
    private void ProcessPendingContainerChangeNotifications()
    {
        if (this.PendingContainerChangeNotifications.Count == 0)
            return;

        List<(GameLocation Location, Vector2 Tile, bool IsJunimoChest)> pending = this.PendingContainerChangeNotifications;
        this.PendingContainerChangeNotifications = new();

        foreach (var (location, tile, isJunimoChest) in pending)
            this.ScheduleInputFeedsFor(location, tile, isJunimoChest);
    }

    /// <summary>MOD: added. Run any <see cref="PendingDelayedPasses"/> whose delay has elapsed — meant to be called once per <see cref="OnUpdateTicked"/>.</summary>
    private void RunDuePendingPasses()
    {
        if (this.PendingDelayedPasses.Count == 0)
            return;

        double curTimeMs = this.UnpausedElapsedMs;
        int executedThisTick = 0;
        for (int i = this.PendingDelayedPasses.Count - 1; i >= 0; i--)
        {
            (double createdAtMs, double scheduledTimeMs, Action action, IMachineGroup? _) = this.PendingDelayedPasses[i];
            if (curTimeMs < scheduledTimeMs)
                continue;

            // MOD: added — see MaxDuePassesPerTick's own remarks. Only counts entries that were actually
            // due (not every entry scanned), and stops the WHOLE scan for this tick rather than skipping
            // just this one — any other still-due entries beyond this point are left untouched, to be
            // picked up (still correctly recognized as due, now even more overdue) on a later tick.
            if (executedThisTick >= ModEntry.MaxDuePassesPerTick)
                break;

            this.PendingDelayedPasses.RemoveAt(i);
            executedThisTick++;

            // MOD: added — see LastActualDelayMs's own remarks; lets the perf overlay show the real
            // measured delay instead of going on feel alone.
            this.LastActualDelayMs = curTimeMs - createdAtMs;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "processing a delayed automation pass", I18n.Message_GenericError_Verb_ProcessingDelayedAutomationPass());
            }
        }
    }

    /// <inheritdoc cref="IDisplayEvents.RenderedWorld" />
    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        // MOD: added — draws the "Mark Power Coils" toggle's compass arrows; a no-op unless the toggle
        // is actually on (see PowerCoilCompass's own remarks), kept in its own try/catch since it's
        // purely cosmetic. MOD: fixed — this must be RenderedWorld, not RenderedHud: the arrow's
        // position is computed from Game1.viewport (world/zoom-relative coordinates, matching
        // OverlayMenu's own tile-to-screen math), but RenderedHud's sprite batch is in a DIFFERENT,
        // UI-scale-relative coordinate space — the two only happened to look close to right at 100%
        // pixel zoom, and drifted apart at any other zoom level.
        try
        {
            PowerCoilCompass.Draw(e.SpriteBatch);
        }
        catch (Exception ex)
        {
            this.HandleError(ex, "drawing Power Coil compass arrows", I18n.Message_GenericError_Verb_DrawingPowerCoilCompassArrows());
        }
    }

    /// <summary>
    /// MOD: added. Toggle the automation performance overlay (see <see cref="AutomationPerfTracker"/>) —
    /// moved from a rebindable keybind to a console-only command (<c>automate perf</c>, see
    /// <see cref="Commands.TogglePerfOverlayCommand"/>) — a keybind lives in
    /// <c>config.json</c>/GMCM's rebind list where a regular player could stumble onto it by accident,
    /// while a console command only ever fires if someone deliberately opens the SMAPI console and types
    /// it. Starting/stopping the recording alongside the display means every time you turn it on, you get
    /// a clean window to compare against a previous run, rather than a lifetime total.
    /// </summary>
    private void TogglePerfOverlay()
    {
        if (this.ShowPerfOverlay)
        {
            this.ShowPerfOverlay = false;
            AutomationPerfTracker.StopRecording();

            // MOD: added — dump the same summary shown on the overlay to the log, so it's easy to
            // copy/paste or compare against a previous run without having to screenshot the HUD.
            this.Monitor.Log(string.Join(Environment.NewLine, this.BuildPerfSummaryLines()), LogLevel.Info);
        }
        else
        {
            this.ShowPerfOverlay = true;
            this.LastActualDelayMs = null; // MOD: added — clean slate for the new recording window, same as AutomationPerfTracker's own counts
            AutomationPerfTracker.StartRecording();
            this.Monitor.Log("Automation performance overlay enabled.", LogLevel.Info);
        }
    }

    /// <summary>
    /// MOD: added. Build the automation performance summary lines, shared by the overlay (see
    /// <see cref="OnRenderedHud"/>) and the log dump written when recording stops (see <see cref="TogglePerfOverlay"/>)
    /// so the two can't drift out of sync.
    /// </summary>
    private string[] BuildPerfSummaryLines()
    {
        List<string> lines =
        [
            $"Automate performance ({(this.Config.UseEventBasedAutomation ? "event-based" : "interval")})",
            $"Recording: {AutomationPerfTracker.Elapsed:mm\\:ss}",
            $"Full scans: {AutomationPerfTracker.FullScans} (avg {AutomationPerfTracker.FullScanAverageMs:0.###}ms, total {AutomationPerfTracker.FullScanTotalMilliseconds:0.##}ms)",
            $"Flagged-machine batches: {AutomationPerfTracker.FlaggedBatches} (avg {AutomationPerfTracker.FlaggedBatchAverageMs:0.###}ms, total {AutomationPerfTracker.FlaggedBatchTotalMilliseconds:0.##}ms)",
            $"Machines flagged by minutesElapsed: {AutomationPerfTracker.FlaggedMachines} (batched into the flagged-machine batches above)",
            $"Total: {AutomationPerfTracker.TotalPasses} passes, {AutomationPerfTracker.TotalMilliseconds:0.##}ms ({AutomationPerfTracker.PercentOfElapsedTime:0.###}% of elapsed time)"
        ];

        // MOD: added — shows the REAL measured delay of the most recent fired pass next to the
        // configured target, so it can be verified against actual timing instead of going on feel alone.
        if (this.Config.ActionDelaySeconds > 0)
        {
            string actual = this.LastActualDelayMs is { } lastActualDelayMs ? $"{lastActualDelayMs:0}ms" : "none fired yet";
            lines.Add($"Last group batch delay: {actual} (configured {this.Config.ActionDelaySeconds * 1000:0}ms)");
        }

        return [.. lines];
    }

    /// <inheritdoc cref="IDisplayEvents.RenderedHud" />
    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        // MOD: added — draws the automation performance overlay (see AutomationPerfTracker's own
        // remarks) while toggled on; a temporary diagnostic aid, not meant to ship long-term.
        if (this.ShowPerfOverlay)
        {
            try
            {
                SpriteBatch b = e.SpriteBatch;
                SpriteFont font = Game1.smallFont;

                string[] lines = this.BuildPerfSummaryLines();

                Vector2 position = new(16, 16);
                float lineHeight = font.MeasureString("A").Y + 2;
                float maxWidth = 0;
                foreach (string line in lines)
                    maxWidth = Math.Max(maxWidth, font.MeasureString(line).X);

                Rectangle background = new((int)position.X - 8, (int)position.Y - 8, (int)maxWidth + 16, (int)(lineHeight * lines.Length) + 16);
                b.Draw(Game1.staminaRect, background, Color.Black * 0.75f);

                for (int i = 0; i < lines.Length; i++)
                    b.DrawString(font, lines[i], position + new Vector2(0, lineHeight * i), Color.White);
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "drawing automation performance overlay", I18n.Message_GenericError_Verb_DrawingAutomationPerformanceOverlay());
            }
        }

        // MOD: fixed — this used to sit after an early `if (!this.ShowPerfOverlay) return;` above, which
        // meant it could only ever draw while the PERF overlay ("P") was also on, instead of showing
        // whenever the "U" automate overlay was open like it's actually meant to — the two toggles are
        // independent, so this is now its own separate condition rather than sharing the perf overlay's
        // early return. A small bottom-left info panel (Power Grid capacity, automation delay, actions
        // per automation), styled the same way as the perf overlay above (translucent black box, white
        // text lines), just anchored to the opposite corner so the two never overlap if both are on at once.
        if (this.CurrentOverlay.Value != null)
        {
            try
            {
                SpriteBatch b = e.SpriteBatch;
                SpriteFont font = Game1.smallFont;

                (int totalCoils, int capacity) = this.MachineManager.Factory.PowerSiloSystem.GetUsage();
                string capacityText = capacity == int.MaxValue
                    ? $"Power Grid: {totalCoils}/Unlimited"
                    : $"Power Grid: {totalCoils}/{capacity}";

                float effectiveDelay = this.GetEffectiveActionDelaySeconds();
                string delayText = effectiveDelay <= 0
                    ? "Automation Delay: Instant"
                    : $"Automation Delay: {effectiveDelay:0.##}s";

                int actionsPerWindow = this.GetEffectiveActionsPerDelayWindow();
                string actionsText = actionsPerWindow <= 0
                    ? "Actions per Automation: Unlimited"
                    : $"Actions per Automation: {actionsPerWindow}";

                string[] lines = [capacityText, delayText, actionsText];

                float lineHeight = font.MeasureString("A").Y + 2;
                float maxWidth = 0;
                foreach (string line in lines)
                    maxWidth = Math.Max(maxWidth, font.MeasureString(line).X);

                Vector2 position = new(16, Game1.uiViewport.Height - 16 - lineHeight * lines.Length);

                Rectangle background = new((int)position.X - 8, (int)position.Y - 8, (int)maxWidth + 16, (int)(lineHeight * lines.Length) + 16);
                b.Draw(Game1.staminaRect, background, Color.Black * 0.75f);

                for (int i = 0; i < lines.Length; i++)
                    b.DrawString(font, lines[i], position + new Vector2(0, lineHeight * i), Color.White);
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "drawing automation overlay info panel", I18n.Message_GenericError_Verb_DrawingAutomationOverlayInfoPanel());
            }
        }
    }

    /// <inheritdoc cref="IInputEvents.ButtonsChanged" />
    private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        if (!this.Config.Enabled) // don't check EnableAutomation, since overlay is still available for farmhands
            return;

        try
        {
            // toggle overlay
            if (Context.IsPlayerFree && this.Keys.ToggleOverlay.JustPressed())
            {
                if (this.CurrentOverlay.Value != null)
                    this.DisableOverlay();
                else
                    this.EnableOverlay();
            }

        }
        catch (Exception ex)
        {
            this.HandleError(ex, "handling key input", I18n.Message_GenericError_Verb_HandlingKeyInput());
        }
    }

    /// <summary>
    /// MOD: added. Release any Cranked Power Coil crank lock the disconnecting player was still
    /// holding — see <see cref="CrankedPowerCoilPatches.ReleaseLocksHeldBy"/>'s own remarks for why this
    /// safety net is needed at all. Deliberately unconditional (no <see cref="Context.IsMainPlayer"/>
    /// gate) — every remaining client independently clearing the same already-networked modData key is
    /// harmless (a no-op past the first), matching this codebase's own "safe for whichever client" modData
    /// precedent elsewhere, and means the cleanup doesn't depend on the host specifically still being
    /// connected to notice it.
    /// </summary>
    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        try
        {
            CrankedPowerCoilPatches.ReleaseLocksHeldBy(CommonHelper.GetLocations(), e.Peer.PlayerID);
        }
        catch (Exception ex)
        {
            this.HandleError(ex, "releasing a disconnected player's Cranked Power Coil crank lock", I18n.Message_GenericError_Verb_ReleasingCrankedPowerCoilLock());
        }
    }

    /// <inheritdoc cref="IMultiplayerEvents.ModMessageReceived" />
    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        // update automation if chest options changed
        if (Context.IsMainPlayer && e is { FromModID: "Pathoschild.ChestsAnywhere", Type: nameof(AutomateUpdateChestMessage) })
        {
            var message = e.ReadAs<AutomateUpdateChestMessage>();
            var location = message.LocationName != null
                ? Game1.getLocationFromName(message.LocationName)
                : null;
            var player = Game1.GetPlayer(e.FromPlayerID);

            string label;
            if (player is null)
                label = $"unknown player {e.FromPlayerID}/{e.FromModID}";
            else if (player != Game1.MasterPlayer)
                label = $"{player.Name}/{e.FromModID}";
            else
                label = e.FromModID;

            if (location != null)
            {
                this.Monitor.Log($"Received chest update from {label} for chest at {message.LocationName} ({message.Tile}), updating machines.");
                this.MachineManager.QueueReload(location);
            }
            else
                this.Monitor.Log($"Received chest update from {label} for chest at {message.LocationName} ({message.Tile}), but no such location was found.");
        }

        // MOD: added — the receiving half of BroadcastHudMessage: another player already showed this
        // toast locally on their own screen before sending it, so this just shows the SAME text here too.
        // FromModID can't be matched via an `is` pattern like the branch above (that used a literal string) —
        // this.ModManifest.UniqueID is a runtime value, not a compile-time constant.
        else if (e.FromModID == this.ModManifest.UniqueID && e.Type == ModEntry.BroadcastHudMessageType)
        {
            string text = e.ReadAs<string>();
            Game1.addHUDMessage(new HUDMessage(text, HUDMessage.newQuest_type));
        }

        // MOD: added — the receiving half of BroadcastReloadLocations: the host already reloaded these
        // locations' machine data on its own client before sending this, so this just does the same here.
        else if (e.FromModID == this.ModManifest.UniqueID && e.Type == ModEntry.BroadcastReloadLocationsMessageType)
        {
            string[] locationNames = e.ReadAs<string[]>();
            foreach (string locationName in locationNames)
            {
                if (Game1.getLocationFromName(locationName) is { } location)
                    this.MachineManager.QueueReload(location);
            }
        }

        // MOD: added — the receiving half of BroadcastLastItemShipped: find the matching REAL slot in
        // this client's own synced view of the shipping bin's inventory (not a detached copy — see
        // BroadcastLastItemShipped's own remarks for why that matters for vanilla's click-to-collect),
        // and point this client's own Farm.lastItemShipped at it, same as the host already did locally.
        else if (e.FromModID == this.ModManifest.UniqueID && e.Type == ModEntry.BroadcastLastItemShippedMessageType)
        {
            LastItemShippedMessage message = e.ReadAs<LastItemShippedMessage>();
            Farm farm = Game1.getFarm();
            Item? match = farm.getShippingBin(Game1.MasterPlayer)
                .LastOrDefault(item => item != null && item.QualifiedItemId == message.QualifiedItemId && item.Quality == message.Quality);

            if (match != null)
                farm.lastItemShipped = match;
        }

        // MOD: added — the receiving half of NotifyHostMenuClosedMessageType: a farmhand's own menu just
        // closed on THEIR client, which the host has no other way to observe — see OnMenuChanged's own
        // remarks for why this is needed at all.
        else if (Context.IsMainPlayer && e.FromModID == this.ModManifest.UniqueID && e.Type == ModEntry.NotifyHostMenuClosedMessageType && this.EnableAutomation)
        {
            this.RetryLocationsPendingLockedRetry();
        }
    }

    /****
    ** Methods
    ****/
    /// <summary>Update when the configuration changes.</summary>
    public void ReloadConfig()
    {
        this.AutomateCountdown = Math.Min(this.AutomateCountdown, this.Config.AutomationInterval);
        this.RunAutomationPassOnNextTick = true; // MOD: added — event-based mode's equivalent instant pass (e.g. right after toggling UseEventBasedAutomation itself)

        // MOD: added — every branch below calls MachineManager.Clear()/Reset(), discarding every
        // group/machine instance currently in use — see ResetDelayQueueState's own remarks for why this
        // mod's own delay/queue bookkeeping needs clearing right alongside that. This is very likely the
        // MOST frequently hit of the three places this reset is needed, since it fires on every single
        // Generic Mod Config Menu save — including every time ActionDelaySeconds itself gets tuned while
        // testing.
        this.ResetDelayQueueState();

        if (!this.Config.Enabled)
        {
            if (this.MachineManager.GetActiveMachineGroups().Any())
                this.Monitor.Log("Disabled per config change. Machines are no longer automated.", LogLevel.Warn);

            this.MachineManager.Clear();
            this.DisableOverlay();
        }
        else
        {
            this.MachineManager.Reset();
            this.ResetOverlayIfShown();
        }
    }

    /// <summary>
    /// MOD: added. Clear all of this mod's own <see cref="PendingDelayedPasses"/>/<see cref="GroupActionQueues"/>/
    /// <see cref="QueuedMachines"/>/<see cref="ArmedGroupBatches"/> — meant to be called anywhere
    /// <see cref="MachineManager.Reset"/>/<see cref="MachineManager.Clear"/> also runs (<see cref="OnDayStarted"/>,
    /// <see cref="OnSaveLoaded"/>, <see cref="ReloadConfig"/>), since those discard every
    /// <see cref="IMachineGroup"/>/<see cref="IMachine"/> instance currently in use. Without this, a
    /// leftover scheduled pass would eventually fire against outdated group/machine state instead of the
    /// fresh instances built afterward, and this bookkeeping would keep holding references to now-discarded
    /// instances indefinitely.
    /// </summary>
    private void ResetDelayQueueState()
    {
        this.PendingDelayedPasses.Clear();
        this.GroupActionQueues.Clear();
        this.QueuedMachines.Clear();
        this.ArmedGroupBatches.Clear();
    }

    /// <summary>
    /// MOD: added. Clear <see cref="GroupActionQueues"/>/<see cref="QueuedMachines"/>/<see cref="ArmedGroupBatches"/>
    /// entries tied to specific <see cref="IMachineGroup"/> instances that <see cref="MachineManager"/> just
    /// discarded via a targeted, location-scoped rescan (see <see cref="MachineManager.TakeRemovedGroups"/>)
    /// — the narrower sibling of <see cref="ResetDelayQueueState"/>, which only handles the OTHER case
    /// (a full <see cref="MachineManager.Reset"/> that discards every group at once).
    ///
    /// Without this, an old group's own pending pacing timer (armed via <see cref="RunOrScheduleDelayedPass"/>
    /// before the rescan happened) keeps firing on schedule even after <see cref="MachineManager"/> has moved
    /// on to a brand-new <see cref="IMachineGroup"/> instance for the same physical machines — since
    /// <see cref="IMachineGroup"/> has no stable identity across a rebuild, that closure still captures the
    /// OLD group reference directly, and nothing else ever tells this bookkeeping that key is now stale. The
    /// result, confirmed via diagnostic logging: the same physical machine cluster gets driven independently
    /// by several "zombie" groups at once, each pacing itself correctly in isolation but committing on its
    /// own schedule in parallel with the others — which looks exactly like a single group suddenly
    /// processing multiple actions per window.
    ///
    /// Removing a group's queue entry also un-marks whichever of its machines were still sitting in
    /// <see cref="QueuedMachines"/> — deliberately, so if the same physical machine gets rediscovered under
    /// the new group (which it will, via the normal triggers), it isn't wrongly treated as "already queued"
    /// against a queue that no longer exists. Safe to do only because <see cref="PendingDelayedPasses"/>
    /// entries scoped to this group are ALSO cancelled below — otherwise a still-pending pass (e.g. one of
    /// <see cref="ScheduleGroupCheck"/>'s per-machine closures, queued moments before this same group got
    /// superseded) would later run against the now-unmarked machines and see them as newly discovered,
    /// silently recreating this group's queue/armed-timer entries from scratch and resurrecting it as an
    /// independent duplicate of whatever new group replaced it — confirmed directly via diagnostic logging
    /// as the cause of a group committing twice within a few hundred milliseconds at session start.
    /// </summary>
    /// <param name="removedGroups">The groups that were just discarded.</param>
    private void PurgeRemovedGroupsPacingState(IReadOnlyList<IMachineGroup> removedGroups)
    {
        if (removedGroups.Count == 0)
            return;

        HashSet<IMachineGroup> removedSet = new(removedGroups, new ObjectReferenceComparer<IMachineGroup>());

        foreach (IMachineGroup group in removedGroups)
        {
            if (this.GroupActionQueues.Remove(group, out LinkedList<(IMachine Machine, double EligibleAtMs, bool Confirmed)>? queue))
            {
                foreach ((IMachine machine, double _, bool _) in queue)
                    this.QueuedMachines.Remove(machine);
            }

            this.ArmedGroupBatches.Remove(group);
        }

        for (int i = this.PendingDelayedPasses.Count - 1; i >= 0; i--)
        {
            IMachineGroup? passGroup = this.PendingDelayedPasses[i].Group;
            if (passGroup != null && removedSet.Contains(passGroup))
                this.PendingDelayedPasses.RemoveAt(i);
        }
    }

    /// <summary>
    /// MOD: added. Same as the shared <c>Common.AddGenericModConfigMenu</c> helper, except it registers
    /// under a <see cref="DisplayNameManifest"/> instead of this mod's own real manifest, so the GMCM
    /// page shows "Powered Automation" instead of "Automate" — see that class's own remarks for why.
    /// Deliberately not a change to the shared helper itself, since that's used generically and this
    /// display override is specific to this one mod.
    /// </summary>
    /// <typeparam name="TConfig">The config model type.</typeparam>
    /// <param name="configMenu">The config UI to register.</param>
    /// <param name="get">Get the current config model.</param>
    /// <param name="set">Overwrite the current config model.</param>
    /// <param name="onSaved">Apply the config changes after they've been saved.</param>
    private void AddGenericModConfigMenuWithDisplayName<TConfig>(IGenericModConfigMenuIntegrationFor<TConfig> configMenu, Func<TConfig> get, Action<TConfig> set, Action? onSaved = null)
        where TConfig : class, new()
    {
        void Reset()
        {
            set(new TConfig());
            this.Helper.WriteConfig(get());
        }

        void SaveAndApply()
        {
            this.Helper.WriteConfig(get());
            onSaved?.Invoke();
        }

        IManifest displayManifest = new DisplayNameManifest(this.ModManifest, I18n.Config_DisplayName());
        GenericModConfigMenuIntegration<TConfig> api = new(this.Helper.ModRegistry, this.Monitor, displayManifest, get, Reset, SaveAndApply);
        if (api.IsLoaded)
        {
            try
            {
                configMenu.Register(api, this.Monitor);
            }
            catch (Exception ex)
            {
                this.Monitor.LogOnce($"Failed registering config menu with Generic Mod Config Menu.\n\nTechnical info:\n{ex}", LogLevel.Error);
            }
        }
    }

    /// <summary>Log warnings if custom-machine frameworks are installed without their automation component.</summary>
    /// <param name="integrations">Mods which add custom machine recipes and require a separate automation component.</param>
    private void ReportMissingBridgeMods(DataModelIntegration[] integrations)
    {
        var registry = this.Helper.ModRegistry;
        foreach (DataModelIntegration integration in integrations)
        {
            if (registry.IsLoaded(integration.Id) && !registry.IsLoaded(integration.SuggestedId))
                this.Monitor.Log($"Machine recipes added by {integration.Name} aren't currently automated. Install {integration.SuggestedName} too to enable them: {integration.SuggestedUrl}.", LogLevel.Warn);
        }
    }

    /// <summary>Get whether the host player has Automate installed.</summary>
    /// <param name="version">The installed version, if any.</param>
    private bool HostHasAutomate([NotNullWhen(true)] out ISemanticVersion? version)
    {
        if (Context.IsMainPlayer || Context.IsSplitScreen)
        {
            version = this.ModManifest.Version;
            return true;
        }

        IMultiplayerPeer? host = this.Helper.Multiplayer.GetConnectedPlayer(Game1.MasterPlayer.UniqueMultiplayerID);
        IMultiplayerPeerMod? mod = host?.Mods.SingleOrDefault(p => string.Equals(p.ID, this.ModManifest.UniqueID, StringComparison.OrdinalIgnoreCase));

        version = mod?.Version;
        return mod != null;
    }

    /// <summary>Log an error and warn the user.</summary>
    /// <param name="ex">The exception to handle.</param>
    /// <param name="verb">The verb describing where the error occurred (e.g. "looking that up"), always in English — used only for the SMAPI log, which should stay in English for troubleshooting/bug reports regardless of the player's language.</param>
    /// <param name="translatedVerb">MOD: added. The same verb, translated for the on-screen message shown to the player — kept as a separate argument from <paramref name="verb"/> specifically so the log and the on-screen text can use different languages.</param>
    private void HandleError(Exception ex, string verb, string translatedVerb)
    {
        this.Monitor.Log($"Something went wrong {verb}:\n{ex}", LogLevel.Error);
        CommonHelper.ShowErrorMessage(I18n.Message_GenericError(verb: translatedVerb));
    }

    /// <summary>
    /// MOD: added. Show a HUD toast locally AND broadcast it to every other connected player — for
    /// events that represent a genuinely save-wide change (Power Silo capacity, Power Grid solar
    /// connectivity, the Automation Relay's save-wide pacing bonus) rather than something purely local
    /// to whichever player happened to trigger it. These affect every player's own automation equally
    /// (the pacing bonus and coil capacity aren't per-player), so every player should see the
    /// notification, not just whoever was standing at the building — matches
    /// <see cref="OnModMessageReceived"/>'s own handling of this same message type on the receiving end.
    /// </summary>
    /// <param name="text">The message text.</param>
    private void BroadcastHudMessage(string text)
    {
        Game1.addHUDMessage(new HUDMessage(text, HUDMessage.newQuest_type));

        if (Context.IsMultiplayer)
        {
            this.Helper.Multiplayer.SendMessage(
                message: text,
                messageType: ModEntry.BroadcastHudMessageType,
                modIDs: [this.ModManifest.UniqueID]
            );
        }
    }

    /// <summary>
    /// MOD: added. Reload the given locations' machine data locally, then tell every other connected
    /// player to do the same for their own <see cref="MachineManager"/> — passed to
    /// <see cref="PowerSiloPatches.Initialize"/> as its <c>requeueLocations</c> callback, which is only
    /// ever invoked by the host now (see <see cref="PowerSiloSystem.RefreshCoilAllowance"/>'s own
    /// remarks). A coil's powered/rank state flipping there is a plain <see cref="ModData"/> mutation on
    /// an object that was already there — no add/remove — so it never raises SMAPI's own
    /// <see cref="IWorldEvents.ObjectListChanged"/> on anyone else's client, meaning a farmhand's own
    /// overlay/no-power icons had no trigger at all to notice a change like that without this broadcast
    /// (short of the player manually toggling the overlay off and back on to force a full rebuild).
    /// </summary>
    /// <param name="locations">The locations to reload.</param>
    private void BroadcastReloadLocations(IEnumerable<GameLocation> locations)
    {
        GameLocation[] locationsArray = locations as GameLocation[] ?? locations.ToArray();
        this.MachineManager.QueueReload(locationsArray);

        if (!Context.IsMultiplayer || locationsArray.Length == 0)
            return;

        this.Helper.Multiplayer.SendMessage(
            message: locationsArray.Select(location => location.NameOrUniqueName).ToArray(),
            messageType: ModEntry.BroadcastReloadLocationsMessageType,
            modIDs: [this.ModManifest.UniqueID]
        );
    }

    /// <summary>
    /// MOD: added. Tell every other connected player that an automated shipment (via conduit, not a
    /// manual player toss) just changed the shipping bin's own "last shipped" display — see
    /// <see cref="Framework.Storage.ShippingBinContainer"/>'s own remarks for why <c>Farm.lastItemShipped</c>
    /// (a plain, non-networked vanilla field) doesn't sync on its own, and automation only ever runs on
    /// the host (see <see cref="EnableAutomation"/>) — so without this, a farmhand's own client would
    /// never see the display update for anything a conduit delivered.
    ///
    /// Sends just the shipped item's identity (qualified ID + quality), not the object itself — each
    /// receiving client resolves that back to the real, matching slot in its own synced view of the
    /// bin's inventory (see <see cref="OnModMessageReceived"/>), so vanilla's own click-to-collect
    /// (which removes <c>lastItemShipped</c> by reference) still works correctly on every client.
    /// </summary>
    /// <param name="shipped">The real item instance that was just shipped (a live reference into the bin's own inventory).</param>
    private void BroadcastLastItemShipped(Item shipped)
    {
        if (!Context.IsMultiplayer)
            return;

        this.Helper.Multiplayer.SendMessage(
            message: new LastItemShippedMessage { QualifiedItemId = shipped.QualifiedItemId, Quality = shipped.Quality },
            messageType: ModEntry.BroadcastLastItemShippedMessageType,
            modIDs: [this.ModManifest.UniqueID]
        );
    }

    /// <summary>Disable the overlay, if shown.</summary>
    private void DisableOverlay()
    {
        this.CurrentOverlay.Value?.Dispose();
        this.CurrentOverlay.Value = null;
    }

    /// <summary>Enable the overlay.</summary>
    private void EnableOverlay()
    {
        if (!Context.IsMainPlayer)
        {
            this.MachineManager.Reset();
            this.ResetDelayQueueState(); // MOD: added — MachineManager.Reset() just discarded every group/machine instance, so any of this mod's own delay/queue bookkeeping for them is now stale (same as the other MachineManager.Reset() call sites)
            this.MachineManager.ReloadQueuedLocations();
        }
        else
        {
            // MOD: added — force a fresh rescan of the current location whenever the overlay is
            // opened, so it always reflects the actual current state instead of whatever the
            // automatic change-detection heuristics last cached (which can occasionally miss an
            // edge case and go stale).
            this.MachineManager.QueueReload(Game1.currentLocation);
            this.MachineManager.ReloadQueuedLocations();
        }

        this.CurrentOverlay.Value ??= new OverlayMenu(
            events: this.Helper.Events,
            inputHelper: this.Helper.Input,
            reflection: this.Helper.Reflection,
            locationKey: this.MachineManager.Factory.GetLocationKey(Game1.currentLocation),
            machineData: this.MachineManager.GetMachineDataFor(Game1.currentLocation),
            powerSilo: this.MachineManager.Factory.PowerSiloSystem,
            powerCoilSourceNames: this.Config.PowerSourceNames
        );
    }

    /// <summary>Reset the overlay if it's being shown.</summary>
    private void ResetOverlayIfShown()
    {
        if (this.CurrentOverlay.Value != null)
        {
            this.DisableOverlay();
            this.EnableOverlay();
        }
    }

    /// <summary>
    /// MOD: added. Get whether a tile is powered, or orthogonally touching a powered tile. Used by
    /// <see cref="ReloadIfNeeded{TEntity}"/> to decide whether a placement/removal change is worth
    /// reacting to — matches the same "touching" rule (adjacent or same-tile) used by the actual
    /// grouping logic, so a machine/chest that can legitimately join a group via a powered connector
    /// touching it doesn't get incorrectly ignored just because its own exact tile is out of range.
    /// </summary>
    /// <param name="data">The location's tracked machine data.</param>
    /// <param name="tile">The tile to check.</param>
    private bool IsNearPower(MachineDataForLocation data, Vector2 tile)
    {
        if (data.PoweredTiles == null)
            return true; // power system disabled — everything is unrestricted

        if (data.PoweredTiles.Contains(tile))
            return true;

        return data.PoweredTiles.Contains(new Vector2(tile.X, tile.Y - 1))
            || data.PoweredTiles.Contains(new Vector2(tile.X, tile.Y + 1))
            || data.PoweredTiles.Contains(new Vector2(tile.X - 1, tile.Y))
            || data.PoweredTiles.Contains(new Vector2(tile.X + 1, tile.Y));
    }

    /// <summary>Rescan machines in a location if added/removed entities may change active automation.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="location">The location whose entities changed.</param>
    /// <param name="entities">The entities that were added or removed.</param>
    private bool ReloadIfNeeded<TEntity>(GameLocation location, IEnumerable<DiffEntry<TEntity>> entities)
        where TEntity : notnull
    {
        string locationKey = this.MachineManager.Factory.GetLocationKey(location);
        MachineDataForLocation? data = this.MachineManager.GetMachineDataFor(location);
        JunimoMachineGroup junimoData = this.MachineManager.JunimoMachineGroup;

        bool shouldReload = false;
        foreach ((Rectangle tileArea, TEntity entity, bool isAdded) in entities)
        {
            // MOD: added — placing or removing a configured power source (ranged or local) always
            // forces a rescan, regardless of the normal "is this near something already tracked"
            // heuristic below. That heuristic can't work for the power system: when a tile is out of
            // power range, whatever's there is never tracked at all (not even as "disabled" or
            // "outdated") — so there's nothing for a newly-placed or moved power source to appear
            // "adjacent to," and the heuristic would otherwise never realize the powered area changed.
            if (this.Config.PowerSystemEnabled
                && entity is StardewValley.Object powerSourceCandidate
                && (this.Config.PowerSourceNames.Contains(powerSourceCandidate.QualifiedItemId) || this.Config.PowerSourceNames.Contains(powerSourceCandidate.Name)
                    || this.Config.LocalPowerSourceNames.Contains(powerSourceCandidate.QualifiedItemId) || this.Config.LocalPowerSourceNames.Contains(powerSourceCandidate.Name)
                    || this.Config.CrankedPowerSourceNames.Contains(powerSourceCandidate.QualifiedItemId) || this.Config.CrankedPowerSourceNames.Contains(powerSourceCandidate.Name))) // MOD: added
            {
                shouldReload = true;
                break;
            }

            // MOD: added — placing a managed connector always forces a rescan too, regardless of
            // power range, so its displayed texture gets assigned promptly instead of sitting on the
            // vanilla look until some unrelated nearby change happens to trigger a rescan.
            if (isAdded && entity is Flooring placedFloor && ConnectorTexturePatches.IsManagedConnector(placedFloor.whichFloor.Value))
            {
                shouldReload = true;
                break;
            }

            // MOD: added — placing a managed sign always forces a rescan too, for the same reason as
            // the connector case above: signs aren't machines, containers, or connectors, so they're
            // never recognized as an automatable entity by the "ignore unknown entity" check just
            // below, and would otherwise keep showing whatever validity texture it last had (or the
            // content pack's default) until some unrelated nearby change happened to trigger a rescan.
            if (isAdded && entity is StardewValley.Object placedSign && SignValidityPatches.IsManagedSign(placedSign.QualifiedItemId))
            {
                shouldReload = true;
                break;
            }

            // ignore unknown entity
            IAutomatable? automateable = this.MachineManager.Factory.GetEntityFor(location, new Vector2(tileArea.X, tileArea.Y), entity);
            if (automateable is null)
                continue;

            // MOD: added — if this tile (and nothing orthogonally touching it) is powered, ignore
            // the change entirely (no reload, no outdated-tracking). A tile that's truly isolated
            // from power is treated as if nothing is there at all, so a change there shouldn't
            // affect anything. The check also considers the 4 orthogonally-adjacent tiles, not just
            // the exact tile — a machine/chest can join a group through a powered connector
            // touching it even while its own tile is technically just outside the configured range
            // (the same "touching" rule used everywhere else in the grouping logic), so only
            // skipping based on the exact tile would incorrectly suppress a rescan for that case.
            // (Power source placement/removal is handled separately above, since that's what
            // actually changes which tiles are powered in the first place.)
            if (data != null && !this.IsNearPower(data, new Vector2(tileArea.X, tileArea.Y)))
                continue;

            // reload if added to an unknown location
            if (data is null)
            {
                if (isAdded)
                {
                    shouldReload = true;
                    break;
                }

                continue;
            }

            // reload if potentially connected to a chest
            if (isAdded)
            {
                shouldReload =
                    junimoData.ContainsOrAdjacent(locationKey, tileArea)
                    || (automateable is IContainer ? data.ContainsOrAdjacent(tileArea) : data.IsConnectedToChest(tileArea));

                if (shouldReload)
                    break;
            }

            // reload if removed from a valid machine group
            if (data.IntersectsAutomatedGroup(tileArea) || junimoData.IntersectsAutomatedGroup(locationKey, tileArea))
            {
                shouldReload = true;
                break;
            }

            // else track entity change
            data.MarkOutdated(tileArea, automateable);
        }

        if (shouldReload)
            this.MachineManager.QueueReload(location);

        return shouldReload;
    }

    /// <summary>Get a standardized list of changed entities.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="added">The added entities.</param>
    /// <param name="removed">The removed entities.</param>
    private IEnumerable<DiffEntry<TEntity>> GetDiffList<TEntity>(IEnumerable<KeyValuePair<Vector2, TEntity>> added, IEnumerable<KeyValuePair<Vector2, TEntity>> removed)
        where TEntity : notnull
    {
        return
            added.Select(cur => new DiffEntry<TEntity>(new Rectangle((int)cur.Key.X, (int)cur.Key.Y, 1, 1), cur.Value, true))
            .Concat(removed.Select(cur => new DiffEntry<TEntity>(new Rectangle((int)cur.Key.X, (int)cur.Key.Y, 1, 1), cur.Value, false)));
    }

    /// <summary>Get a standardized list of changed entities.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="added">The added entities.</param>
    /// <param name="removed">The removed entities.</param>
    /// <param name="getTileArea">Get the tile area for an entity.</param>
    private IEnumerable<DiffEntry<TEntity>> GetDiffList<TEntity>(IEnumerable<TEntity> added, IEnumerable<TEntity> removed, Func<TEntity, Rectangle> getTileArea)
        where TEntity : notnull
    {
        return
            added.Select(cur => new DiffEntry<TEntity>(getTileArea(cur), cur, true))
            .Concat(removed.Select(cur => new DiffEntry<TEntity>(getTileArea(cur), cur, false)));
    }

    /// <summary>A standardized entity change.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="TileArea">The tile area covered by the entity.</param>
    /// <param name="Entity">The entity value.</param>
    /// <param name="Added">Whether the entity was added (else removed).</param>
    private readonly record struct DiffEntry<TEntity>(Rectangle TileArea, TEntity Entity, bool Added)
        where TEntity : notnull;
}

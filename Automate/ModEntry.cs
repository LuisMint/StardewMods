using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework;
using Pathoschild.Stardew.Automate.Framework.Commands;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Patches;
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

    /// <summary>Whether to track machine changes for the current save.</summary>
    private bool EnableAutomationChangeTracking =>
        this.Config.Enabled
        && !this.IsSecondaryScreen // in split-screen mode, the change will be tracked by the main player
        && (Context.IsMainPlayer || this.CurrentOverlay.Value is not null);

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
    /// across a rebuild that recreates the group instance — per direct user feedback, that's an acceptable
    /// trade: a rebuild just resets the affected group's pacing to fresh (its machines get rediscovered and
    /// re-queued from scratch by the normal triggers), rather than trying to bridge old-to-new group
    /// instances, which is what caused most of the fragility in earlier attempts at this feature.
    /// </summary>
    private readonly Dictionary<IMachineGroup, Queue<IMachine>> GroupActionQueues = new(new ObjectReferenceComparer<IMachineGroup>());

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
    /// occasionally ready, structurally starving everything queued behind it — reported directly by a user as
    /// an unexpectedly long stall in event-based mode and multiple machines committing at once in interval mode.
    /// </summary>
    private readonly HashSet<IMachine> QueuedMachines = new(new ObjectReferenceComparer<IMachine>());

    /// <summary>MOD: added. Every group that currently has a batch scheduled (see <see cref="TryScheduleGroupBatch"/>) — used only while <see cref="ModConfig.ActionDelaySeconds"/> is greater than zero. A group already in here is left alone by any new trigger that finds more of its work; the already-scheduled batch will drain its <see cref="GroupActionQueues"/> entry fresh when it fires (see <see cref="RunGroupBatch"/>).</summary>
    private readonly HashSet<IMachineGroup> ArmedGroupBatches = new(new ObjectReferenceComparer<IMachineGroup>());

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
        // instance, rather than each rolling independently.
        this.PowerSiloTierRoller = new PowerSiloTierRoller(
            getBaseTiers: () => this.Config.PowerSiloTiers,
            getTierPools: () => this.Config.PowerSiloTierPools
        );

        // init
        this.MachineManager = new MachineManager(
            config: () => this.Config,
            data: this.Data,
            defaultFactory: new AutomationFactory(
                config: () => this.Config,
                monitor: this.Monitor,
                reflection: this.Helper.Reflection
            ),
            monitor: this.Monitor,
            powerSiloTierRoller: this.PowerSiloTierRoller
        );

        this.CommandHandler = new CommandHandler(this.Monitor, () => this.Config, this.MachineManager);

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
            getBaseActionDelaySeconds: () => this.Config.ActionDelaySeconds,
            getMinimumActionDelaySeconds: () => this.Config.PowerRelayMinimumActionDelaySeconds
        );

        // MOD: added — the Power Relay's passive shimmering-glint ambient effect, purely cosmetic.
        this.PowerRelayAmbientEffect = new PowerRelayAmbientEffect(
            getRelayBuildingNames: () => this.Config.PowerRelayBuildingNames
        );

        // apply Harmony patches
        Harmony harmony = new(this.ModManifest.UniqueID);
        PowerCoilPatches.Apply(harmony);

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
        // invalid, per direct user request replacing the previous Alternative Textures-driven swap (see
        // SignValidityPatches' own remarks).
        SignValidityPatches.Apply(harmony);

        // MOD: added — swaps a managed connector's world sprite between its powered/unpowered/dimmer/
        // dimmest variants, per direct user request replacing the previous Alternative Textures-driven
        // swap (see ConnectorTexturePatches' own remarks).
        ConnectorTexturePatches.Apply(harmony);

        PoweredChestPatches.Apply(harmony);

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

        PowerRangePreviewPatches.Initialize(
            getRangeDistance: () => this.Config.PowerRangeDistance
        );
        PowerRangePreviewPatches.Apply(harmony);

        PowerSiloPatches.Initialize(
            getSourceNames: () => this.Config.PowerSourceNames,
            getSolarPanelNames: () => this.Config.PowerSiloSolarPanelNames, // MOD: added
            getLocalSourceNames: () => this.Config.LocalPowerSourceNames, // MOD: added — so a Powered Chest's own placement/removal is recognized as solar-connectivity-relevant too
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem,
            requeueLocations: locations => this.MachineManager.QueueReload(locations) // MOD: added — see PowerSiloPatches.RequeueLocations's own remarks for why a coil-allowance refresh may need to reach locations other than the one that triggered it
        );
        PowerSiloPatches.Apply(harmony);

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
            getTiers: () => this.PowerSiloTierRoller.GetEffectiveTiers(),
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem
        );
        PowerSiloCapPatches.Apply(harmony);

        // MOD: added — registers the Power Silo's feed/status interaction via GameLocation.RegisterTileAction,
        // not a Harmony patch (see PowerSiloInteraction's own remarks for why).
        new PowerSiloInteraction(
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem,
            getTiers: () => this.PowerSiloTierRoller.GetEffectiveTiers()
        ).Register();

        // MOD: added — registers the Power Relay's click interaction the same way (see
        // PowerRelayInteraction's own remarks).
        new PowerRelayInteraction(
            this.PowerRelaySystem,
            getShardItemId: () => this.Config.PowerRelayShardItemId,
            getFirstShardItemId: () => this.Config.PowerRelayFirstShardItemId,
            getBarItemId: () => this.Config.PowerRelayBarItemId,
            getFirstBarItemId: () => this.Config.PowerRelayFirstBarItemId,
            getBaseActionsPerDelayWindow: () => this.Config.ActionsPerDelayWindow
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

        // MOD: added — restricts the loot a Barrel/Crate gives inside a Cave Hole interior to 1-3 Cave
        // Carrots or nothing, per direct user request (see CaveHoleCrateLootPatches's own remarks).
        CaveHoleCrateLootPatches.Apply(harmony);

        // MOD: added — gives the Dwarf a "build a Power Silo" option alongside their normal shop, reusing
        // vanilla's own carpenter menu (see DwarfBuildMenuPatches's own remarks).
        DwarfBuildMenuPatches.Apply(harmony);

        // MOD: added — reskins the under-construction/upgrading visual for Dwarf-built structures only,
        // per direct user request (see DwarfConstructionSpritePatches's own remarks).
        DwarfConstructionSpritePatches.Apply(harmony);

        // MOD: added — clicking an active Dwarf construction site spits a random item out of the ladder
        // hole, once per building per day, per direct user request (see
        // DwarfConstructionSiteInteractionPatches's own remarks).
        DwarfConstructionSiteInteractionPatches.Apply(harmony);

        // MOD: added — reading Dwarf Research Note #4 unlocks a one-time hidden Power Coil at a specific
        // Mine level 120 tile, per direct user request (see DwarfNoteTreasureTilePatches's own remarks).
        DwarfNoteTreasureTilePatches.Apply(harmony);

        // MOD: added — reading Dwarf Research Note #7 unlocks a nightly Diamond-for-Dwarf-Gadget trade
        // at the vanilla Statue Of The Dwarf King, per direct user request (see
        // DwarfKingStatueTradePatches's own remarks).
        DwarfKingStatueTradePatches.Apply(harmony);

        // MOD: added — reading Dwarf Research Notes #1-#3 unlocks a one-time gem drop at a specific
        // Mountain spot, consuming a held Dwarf Scroll, per direct user request (see
        // DwarfNoteGemScrollPatches's own remarks).
        DwarfNoteGemScrollPatches.Apply(harmony);

        // MOD: added — gives the Dwarf's shop 3 Cave Carrots that restock weekly, plus 1 Power Coil and
        // 1 Powered Chest that each restock once a season, per direct user request (see
        // DwarfWeeklyShopPatches's own remarks).
        DwarfWeeklyShopPatches.Apply(harmony);

        // hook events
        helper.Events.Content.AssetRequested += this.OnAssetRequested;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayEnding += this.OnDayEnding;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.TimeChanged += this.OnTimeChanged; // MOD: added — event-based automation trigger
        helper.Events.Input.ButtonsChanged += this.OnButtonsChanged;
        helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
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
    /// MOD: added. Copy every audio file from the AutomatePowerPipes content pack's <c>Pipes</c> folder
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
            // "Mods/AutomatePowerPipes" folder name instead, same as this codebase already hardcodes that
            // content pack's mod ID elsewhere (e.g. PowerSiloMenu's asset name constants).
            IModInfo? contentPack = this.Helper.ModRegistry.Get("luisMint.AutomatePowerPipes");
            if (contentPack is null)
                return;

            string sourceDir = Path.Combine(Constants.GamePath, "Mods", "AutomatePowerPipes", "Pipes");
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
            this.MachineManager.Factory.PowerSiloSystem.RefreshCoilAllowance();

            // MOD: added — clears the Power Silo cap's cached animation state, mirroring MachineManager.Reset() above.
            PowerSiloCapPatches.Reset();

            // MOD: added — clears the Power Relay's cached light/shake state, mirroring PowerSiloCapPatches.Reset() above.
            PowerRelayEffectPatches.Reset();
        }

        // MOD: added — spawns every placed Cave Hole's own quarry-style stone/ore nodes: a full dense
        // fill the first morning after construction, then a smaller daily top-up after that (see
        // CaveHoleQuarrySystem's own remarks). Runs unconditionally (not gated behind !IsSecondaryScreen
        // above) since it's just placing objects in a shared location, not machine/automation state.
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

        this.Monitor.VerboseLog("Location list changed, reloading machines in affected locations.");

        try
        {
            if (e.Removed.Any())
                this.MachineManager.QueueRemove(e.Removed);

            this.MachineManager.QueueReload(e.Added);
        }
        catch (Exception ex)
        {
            this.HandleError(ex, "updating locations");
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
                this.HandleError(ex, "updating Power Silo solar panel connectivity");
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
            this.AddGenericModConfigMenu(
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
                    if (this.MachineManager.ReloadQueuedLocations())
                        this.ResetOverlayIfShown();

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
                    // ~7s backstop scan) triggered an unrelated rescan. Reported directly by a user.
                    IReadOnlyList<IMachineGroup> newlyJoinedGroups = this.MachineManager.TakeGroupsWithNewMembers();
                    if (this.Config.UseEventBasedAutomation)
                    {
                        foreach (IMachineGroup group in newlyJoinedGroups)
                            this.ScheduleGroupCheck(group);
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
                        if (this.Config.UseEventBasedAutomation)
                            this.ScheduleGroupCheck(group);
                    }
                }

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
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "processing machines");
            }
        }

        // MOD: added — animate any "powered but not part of a valid group" connectors in the
        // player's current location. Purely visual, so it's kept in its own try/catch and doesn't
        // depend on EnableAutomation — it's a no-op anyway once there's no cached machine data.
        if (Context.IsWorldReady && this.Config.PowerSystemEnabled)
        {
            try
            {
                this.MachineManager.TickPoweredFloorAnimation(Game1.currentLocation);
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "animating powered connectors");
            }
        }

        // MOD: added — passive dust-puff ambient effect on placed Power Coils, purely cosmetic.
        if (Context.IsWorldReady)
        {
            try
            {
                this.PowerCoilAmbientEffect.Tick();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "animating Power Coil ambient effect");
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
                this.HandleError(ex, "animating Power Relay ambient effect");
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
                this.HandleError(ex, "animating Power Silo cap");
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
                this.HandleError(ex, "animating Power Relay light/shake effects");
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
            // ModConfig.EventBasedPushPullDelaySeconds, a purely cosmetic reveal delay. Per direct user
            // request, that's gone now that ActionDelaySeconds/ActionsPerDelayWindow (paced via
            // TryScheduleGroupBatch below) is a real progression-driven pacing system in its own right —
            // stacking the old cosmetic delay on top of that just made the very start of a save feel
            // doubly slow for no benefit. Each flagged machine is enqueued immediately instead; its own
            // group batch is still paced independently by ActionDelaySeconds as before.
            foreach ((IMachineGroup group, IMachine machine) in MachineReadyPatches.TakePendingReadyMachines())
            {
                this.EnqueueForAutomation(group, machine);
                this.TryScheduleGroupBatch(group);
            }

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
            this.HandleError(ex, "processing machines");
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
    /// in the location's active groups all at once (via <see cref="MachineGroup.Automate"/>). Per direct
    /// user feedback, that meant several machines all being fed by the same chest restock would visually
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
            this.HandleError(ex, "processing machines");
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
    /// </remarks>
    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (e.NewMenu != null || !Context.IsWorldReady || !this.EnableAutomation || !this.Config.UseEventBasedAutomation || this.LocationsPendingLockedRetry.Count == 0)
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
            this.HandleError(ex, "processing machines");
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
        return this.Config.ActionsPerDelayWindow <= 0
            ? this.Config.ActionsPerDelayWindow
            : this.Config.ActionsPerDelayWindow + this.PowerRelaySystem.GetActionsPerDelayWindowBonus();
    }

    private void TryRunAutomationPass()
    {
        IMachineGroup[] activeGroups = this.MachineManager.GetActiveMachineGroups().ToArray();

        if (this.GetEffectiveActionDelaySeconds() <= 0)
        {
            foreach (IMachineGroup group in activeGroups)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                group.Automate();
                AutomationPerfTracker.RecordFullScan(stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        else
        {
            foreach (IMachineGroup group in activeGroups)
            {
                foreach (IMachine machine in group.Machines)
                {
                    if (machine.GetState() is MachineState.Done or MachineState.Empty)
                        this.EnqueueForAutomation(group, machine);
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

        Stopwatch stopwatch = Stopwatch.StartNew();

        // MOD: track whether a real commit happened via TryPushMachineOutput/TryFeedMachineInput's OWN
        // return values, NOT by comparing machine.GetState() before and after — that comparison is always a
        // false negative for a IChestLikeMachine like PoweredChestMachine, whose GetState() is hardcoded to
        // always report MachineState.Empty regardless of what SetInput just did.
        bool didSomething = false;

        if (stateBefore is MachineState.Done)
            didSomething |= group.TryPushMachineOutput(machine);

        if (machine.GetState() is MachineState.Empty)
            didSomething |= group.TryFeedMachineInput(machine);

        AutomationPerfTracker.RecordFlaggedBatch(stopwatch.Elapsed.TotalMilliseconds);

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
    private void EnqueueForAutomation(IMachineGroup group, IMachine machine)
    {
        if (this.GetEffectiveActionDelaySeconds() <= 0)
            return;

        if (!this.QueuedMachines.Add(machine))
            return; // already queued — see QueuedMachines's own remarks

        if (!this.GroupActionQueues.TryGetValue(group, out Queue<IMachine>? queue))
            this.GroupActionQueues[group] = queue = new Queue<IMachine>();

        queue.Enqueue(machine);
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

        if (!this.GroupActionQueues.TryGetValue(group, out Queue<IMachine>? queue) || queue.Count == 0)
            return; // nothing queued for this group — nothing to prime for

        if (!this.ArmedGroupBatches.Add(group))
            return; // already priming — it'll drain the queue (including anything just added to it) when it fires

        this.RunOrScheduleDelayedPass(() => this.RunGroupBatch(group), effectiveActionDelaySeconds, group);
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

        if (!this.GroupActionQueues.TryGetValue(group, out Queue<IMachine>? queue))
            return;

        int committed = 0;
        int effectiveActionsPerDelayWindow = this.GetEffectiveActionsPerDelayWindow();
        bool unlimited = effectiveActionsPerDelayWindow <= 0;

        while (queue.Count > 0 && (unlimited || committed < effectiveActionsPerDelayWindow))
        {
            IMachine machine = queue.Dequeue();
            this.QueuedMachines.Remove(machine);

            if (this.AutomateMachine(group, machine))
                committed++;
        }

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
    private void ScheduleGroupCheck(IMachineGroup group)
    {
        foreach (IMachine machine in group.Machines)
        {
            if (machine.GetState() is not (MachineState.Done or MachineState.Empty))
                continue;

            this.EnqueueForAutomation(group, machine);
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

    /// <summary>MOD: added. Run any <see cref="PendingDelayedPasses"/> whose delay has elapsed — meant to be called once per <see cref="OnUpdateTicked"/>.</summary>
    private void RunDuePendingPasses()
    {
        if (this.PendingDelayedPasses.Count == 0)
            return;

        double curTimeMs = this.UnpausedElapsedMs;
        for (int i = this.PendingDelayedPasses.Count - 1; i >= 0; i--)
        {
            (double createdAtMs, double scheduledTimeMs, Action action, IMachineGroup? _) = this.PendingDelayedPasses[i];
            if (curTimeMs < scheduledTimeMs)
                continue;

            this.PendingDelayedPasses.RemoveAt(i);

            // MOD: added — see LastActualDelayMs's own remarks; lets the perf overlay show the real
            // measured delay instead of going on feel alone.
            this.LastActualDelayMs = curTimeMs - createdAtMs;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, "processing a delayed automation pass");
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
            this.HandleError(ex, "drawing Power Coil compass arrows");
        }
    }

    /// <summary>
    /// MOD: added. Build the automation performance summary lines, shared by the overlay (see
    /// <see cref="OnRenderedHud"/>) and the log dump written when recording stops (see <see cref="OnButtonsChanged"/>)
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
                this.HandleError(ex, "drawing automation performance overlay");
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
                this.HandleError(ex, "drawing automation overlay info panel");
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

            // MOD: added — toggle the automation performance overlay (see AutomationPerfTracker's own
            // remarks). Starting/stopping the recording alongside the display means every time you turn
            // it on, you get a clean window to compare against a previous run, rather than a lifetime total.
            if (Context.IsPlayerFree && this.Keys.TogglePerformanceOverlay.JustPressed())
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
                }
            }
        }
        catch (Exception ex)
        {
            this.HandleError(ex, "handling key input");
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
            if (this.GroupActionQueues.Remove(group, out Queue<IMachine>? queue))
            {
                foreach (IMachine machine in queue)
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
    /// <param name="verb">The verb describing where the error occurred (e.g. "looking that up").</param>
    private void HandleError(Exception ex, string verb)
    {
        this.Monitor.Log($"Something went wrong {verb}:\n{ex}", LogLevel.Error);
        CommonHelper.ShowErrorMessage($"Huh. Something went wrong {verb}. The error log has the technical details.");
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
                    || this.Config.LocalPowerSourceNames.Contains(powerSourceCandidate.QualifiedItemId) || this.Config.LocalPowerSourceNames.Contains(powerSourceCandidate.Name)))
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

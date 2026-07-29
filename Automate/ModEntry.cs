using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework;
using Pathoschild.Stardew.Automate.Framework.Commands;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Patches;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.Integrations.GenericModConfigMenu;
using Pathoschild.Stardew.Common.Messages;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.GameData.BigCraftables;
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

    /// <summary>Whether to automate machines for the current save.</summary>
    private bool EnableAutomation => this.Config.Enabled && Context.IsMainPlayer;

    /// <summary>Whether to track machine changes for the current save.</summary>
    private bool EnableAutomationChangeTracking =>
        this.Config.Enabled
        && !this.IsSecondaryScreen // in split-screen mode, the change will be tracked by the main player
        && (Context.IsMainPlayer || this.CurrentOverlay.Value is not null);

    /// <summary>Whether this is a secondary screen in split-screen mode.</summary>
    private bool IsSecondaryScreen => Context.IsSplitScreen && !Context.IsMainPlayer;

    /// <summary>The number of ticks until the next automation cycle.</summary>
    private int AutomateCountdown;

    /// <summary>The number of ticks until the config UI is registered with Generic Mod Config Menu.</summary>
    /// <remarks>This must happen later than <see cref="IGameLoopEvents.GameLaunched"/>, since Content Patcher packs haven't added their edits to <c>Data/Machines</c> yet at that point.</remarks>
    private int RegisterConfigCountdown = 10;

    /// <summary>The current overlay being displayed, if any.</summary>
    private readonly PerScreen<OverlayMenu?> CurrentOverlay = new();


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

        // init
        this.MachineManager = new MachineManager(
            config: () => this.Config,
            data: this.Data,
            defaultFactory: new AutomationFactory(
                config: () => this.Config,
                monitor: this.Monitor,
                reflection: this.Helper.Reflection
            ),
            monitor: this.Monitor
        );

        this.CommandHandler = new CommandHandler(this.Monitor, () => this.Config, this.MachineManager);

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

        SignColliderPatches.Initialize(
            getSignTextureIds: () => this.Config.SignTextureIds
        );
        SignColliderPatches.Apply(harmony);

        PoweredChestPatches.Apply(harmony);

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
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem
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
            getTiers: () => this.Config.PowerSiloTiers,
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem
        );
        PowerSiloCapPatches.Apply(harmony);

        // MOD: added — registers the Power Silo's feed/status interaction via GameLocation.RegisterTileAction,
        // not a Harmony patch (see PowerSiloInteraction's own remarks for why).
        new PowerSiloInteraction(
            powerSiloSystem: this.MachineManager.Factory.PowerSiloSystem,
            getTiers: () => this.Config.PowerSiloTiers
        ).Register();

        // hook events
        helper.Events.Content.AssetRequested += this.OnAssetRequested;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayEnding += this.OnDayEnding;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Input.ButtonsChanged += this.OnButtonsChanged;
        helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.World.BuildingListChanged += this.OnBuildingListChanged;
        helper.Events.World.LocationListChanged += this.OnLocationListChanged;
        helper.Events.World.ObjectListChanged += this.OnObjectListChanged;
        helper.Events.World.TerrainFeatureListChanged += this.OnTerrainFeatureListChanged;
        helper.Events.World.LargeTerrainFeatureListChanged += this.OnLargeTerrainFeatureListChanged;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;

        // hook commands
        this.CommandHandler.RegisterWith(helper.ConsoleCommands);

        // log info
        this.Monitor.VerboseLog($"Initialized with automation every {this.Config.AutomationInterval} ticks.");
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
        }

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
                // reload machines if needed
                if (this.EnableAutomationChangeTracking)
                {
                    if (this.MachineManager.ReloadQueuedLocations())
                        this.ResetOverlayIfShown();
                }

                // process machines
                if (--this.AutomateCountdown <= 0)
                {
                    this.AutomateCountdown = this.Config.AutomationInterval;

                    IMachineGroup[] activeGroups = this.MachineManager.GetActiveMachineGroups().ToArray();
                    foreach (IMachineGroup group in activeGroups)
                        group.Automate();

                    // MOD: added — separate from each group's own Automate() call above, since the
                    // power-required-machines wake-up callouts need cross-rebuild tracking by location
                    // (see PowerRequiredMachineSystem.ProcessStarvedMachineCallouts's own remarks).
                    this.MachineManager.Factory.PowerRequiredMachineSystem.ProcessStarvedMachineCallouts(activeGroups);
                }
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
            machineData: this.MachineManager.GetMachineDataFor(Game1.currentLocation)
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

            // MOD: added — placing a connector that has a registered Alternative Textures powered
            // look always forces a rescan too, regardless of power range, so its displayed texture
            // gets assigned promptly instead of sitting on the vanilla look until some unrelated
            // nearby change happens to trigger a rescan.
            if (isAdded && entity is Flooring placedFloor && this.Config.ConnectorPoweredTextureIds.ContainsKey(placedFloor.whichFloor.Value))
            {
                shouldReload = true;
                break;
            }

            // MOD: added — placing a sign that has a registered Alternative Textures valid/invalid
            // look always forces a rescan too, for the same reason as the connector case above: signs
            // aren't machines, containers, or connectors, so they're never recognized as an
            // automatable entity by the "ignore unknown entity" check just below, and would otherwise
            // keep whatever texture it last had (or the content pack's default) until some unrelated
            // nearby change happened to trigger a rescan.
            if (isAdded && entity is StardewValley.Object placedSign && this.Config.SignTextureIds.ContainsKey(placedSign.QualifiedItemId))
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

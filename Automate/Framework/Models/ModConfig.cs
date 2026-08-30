using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Pathoschild.Stardew.Common;
using StardewValley.Extensions;

namespace Pathoschild.Stardew.Automate.Framework.Models;

/// <summary>The raw mod configuration.</summary>
internal class ModConfig
{
    /*********
    ** Fields
    *********/
    /// <summary>
    /// MOD: added. The fixed value <see cref="ActionDelaySeconds"/> is reset to on load unless
    /// <see cref="OverwriteAutomationDelay"/> is <c>true</c>. Also read directly by <see cref="ModEntry"/>'s
    /// Power Relay wiring as the "not overwriting" base, so the Relay's own bonus is never computed on top
    /// of a stale leftover overwritten value once the player disables the checkbox live — internal (not
    /// private) so <see cref="ModEntry"/> can reference it there.
    /// </summary>
    internal const float DefaultActionDelaySeconds = 7f;

    /// <summary>
    /// MOD: added. The fixed value <see cref="ActionsPerDelayWindow"/> is reset to on load unless
    /// <see cref="OverwriteAutomationActions"/> is <c>true</c> — see <see cref="DefaultActionDelaySeconds"/>'s
    /// own remarks for why this is internal, not private.
    /// </summary>
    internal const int DefaultActionsPerDelayWindow = 1;


    /*********
    ** Accessors
    *********/
    /// <summary>Whether Automate is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// MOD: added. Whether to run automation in response to SMAPI's <c>TimeChanged</c>/
    /// <c>ChestInventoryChanged</c> events instead of polling every <see cref="AutomationInterval"/>
    /// ticks — a machine's ready/not-ready state can only change on those exact events anyway (see
    /// <c>ModEntry.TryRunAutomationPass</c>'s remarks), so this eliminates the wasted rescans between
    /// them, including while the game is paused. Disable to fall back to the original fixed-interval
    /// polling if your setup doesn't get along with it.
    /// </summary>
    public bool UseEventBasedAutomation { get; set; } = true;

    /// <summary>The number of ticks between each automation process (60 = once per second). Only used when <see cref="UseEventBasedAutomation"/> is disabled.</summary>
    public int AutomationInterval { get; set; } = 60;

    /// <summary>The key bindings.</summary>
    public ModConfigKeys Controls { get; set; } = new();

    /// <summary>The in-game objects through which machines can connect. This can be the internal name or qualified item ID.</summary>
    [JsonProperty("ConnectorNames")]
    public HashSet<string> Connectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. The in-game objects (usually paths/flooring) through which a touching chest acts
    /// as an INPUT/SOURCE only — machines can take items from the chest through this connector, but
    /// items are never stored INTO the chest through it. This can be the internal name or qualified
    /// item ID, same format as <see cref="Connectors"/>.
    /// </summary>
    [JsonProperty("ChestInputConnectorNames")]
    public HashSet<string> ChestInputConnectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. The in-game objects (usually paths/flooring) through which a touching chest acts
    /// as an OUTPUT/DESTINATION only — machines can store items into the chest through this
    /// connector, but items are never taken FROM the chest through it. This can be the internal name
    /// or qualified item ID, same format as <see cref="Connectors"/>.
    /// </summary>
    [JsonProperty("ChestOutputConnectorNames")]
    public HashSet<string> ChestOutputConnectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. The sign item(s) that act as a WHITELIST filter when placed touching a connector
    /// group with an item displayed on them — only the displayed item can move through that group's
    /// storage (input, output, or both). This can be the internal name or qualified item ID, same
    /// format as <see cref="Connectors"/>. Whitelist signs take priority over blacklist signs if both
    /// are accidentally present on the same group.
    /// </summary>
    [JsonProperty("WhitelistSignNames")]
    public HashSet<string> WhitelistSignNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. The sign item(s) that act as a BLACKLIST filter when placed touching a connector
    /// group with an item displayed on them — the displayed item is blocked from moving through that
    /// group's storage (input, output, or both). This can be the internal name or qualified item ID,
    /// same format as <see cref="Connectors"/>. An empty sign (nothing displayed on it) is ignored.
    /// </summary>
    [JsonProperty("BlacklistSignNames")]
    public HashSet<string> BlacklistSignNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. The sign item(s) that act as a WHITELIST filter based on the CATEGORY (e.g.
    /// "Cooking", "Minerals", or a configured <see cref="CustomCategories"/> entry) of whatever item is
    /// displayed on them, rather than the specific item — only items sharing that category can move
    /// through a touching connector group's storage. Unlike <see cref="WhitelistSignNames"/>, a
    /// category sign has no numeric condition. Multiple whitelist category signs in the same group
    /// combine (e.g. two signs for two different categories both apply), same as the item-based signs
    /// already do for different items. This can be the internal name or qualified item ID, same format
    /// as <see cref="Connectors"/>. Category whitelist signs take priority over category blacklist
    /// signs if both are present on the same group, same as the item-based signs.
    /// </summary>
    [JsonProperty("WhitelistCategorySignNames")]
    public HashSet<string> WhitelistCategorySignNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. The sign item(s) that act as a BLACKLIST filter based on the CATEGORY of whatever
    /// item is displayed on them, rather than the specific item — items sharing that category are
    /// blocked from moving through a touching connector group's storage. See
    /// <see cref="WhitelistCategorySignNames"/>'s remarks for how this differs from the item-based
    /// blacklist signs.
    /// </summary>
    [JsonProperty("BlacklistCategorySignNames")]
    public HashSet<string> BlacklistCategorySignNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. Custom category groupings for the category whitelist/blacklist signs, keyed by a
    /// category name of your choosing (e.g. "Lootboxes") to the item names/qualified IDs (same format
    /// as <see cref="Connectors"/>) that belong to it. A custom category takes priority over an item's
    /// own vanilla category when a category sign reads it or when filtering decides whether an item
    /// matches — e.g. a "Lootboxes" category could group every geode, the Golden Coconut, and both
    /// Mystery Boxes together even though none of them share a vanilla category (geodes are normally
    /// lumped in with gems/minerals, the coconut and mystery boxes have their own separate categories).
    /// See <see cref="SignFilter.GetEffectiveCategory"/> for the exact resolution rule.
    /// </summary>
    [JsonProperty("CustomCategories")]
    public Dictionary<string, HashSet<string>> CustomCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. Whether the "power system" is enabled. When true, automation only works within
    /// range of a configured power source (see <see cref="PowerSourceNames"/>) — anything outside
    /// range is treated as if it doesn't exist to Automate at all (not just disabled; fully ignored,
    /// the same as if it were never placed). Defaults to <c>true</c> — this is a deliberate gameplay
    /// gate, not an opt-in convenience feature, so automation is expected to require power sources
    /// out of the box.
    /// </summary>
    public bool PowerSystemEnabled { get; set; } = true;

    /// <summary>
    /// MOD: added. The in-game objects that act as a power source for the power system (see
    /// <see cref="PowerSystemEnabled"/>) — the Power Coil, by default. This can be the internal name
    /// or qualified item ID, same format as <see cref="Connectors"/>.
    /// </summary>
    [JsonProperty("PowerSourceNames")]
    public HashSet<string> PowerSourceNames { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "luisMint.PoweredAutomation_PowerCoil" };

    /// <summary>
    /// MOD: added. How many tiles out from a power source, in each of the 4 cardinal directions, its
    /// power extends — e.g. 2 means a square reaching 2 tiles in every direction from the source,
    /// covering 5x5 tiles total (2 + 1 center + 2). Defined as a distance rather than a total width
    /// so the covered area is always exactly centered on the source, with no rounding ambiguity.
    /// </summary>
    public int PowerRangeDistance { get; set; } = 2;

    /// <summary>
    /// MOD: added. How much power a Power Coil produces for the separately-installed "Utility Grid
    /// Redux" mod's own independent power grid, while that specific coil is itself powered by
    /// Automate's own grid — see <see cref="UtilityGridReduxSystem"/>'s own remarks for the full
    /// mechanism. Has no effect at all if Utility Grid Redux isn't installed.
    /// </summary>
    public int PowerCoilUtilityGridReduxPower { get; set; } = 10;

    /// <summary>
    /// MOD: added. How much power a Powered Chest produces for the separately-installed "Utility Grid
    /// Redux" mod's own independent power grid — unlike <see cref="PowerCoilUtilityGridReduxPower"/>,
    /// this is unconditional (a Powered Chest has no "is this specific instance currently powered"
    /// concept in Automate at all — it's always a local power source the moment it's placed, the same
    /// way it's always a <see cref="LocalPowerSourceNames"/> entry regardless of anything else). See
    /// <see cref="UtilityGridReduxSystem"/>'s own remarks for the full mechanism. Has no effect at all
    /// if Utility Grid Redux isn't installed.
    /// </summary>
    public int PoweredChestUtilityGridReduxPower { get; set; } = 2;

    /// <summary>
    /// MOD: added. How much power a Cranked Power Coil produces for the separately-installed "Utility
    /// Grid Redux" mod's own independent power grid, while that specific coil is currently cranked —
    /// same <c>MustBeOn</c>-mirrored mechanism as <see cref="PowerCoilUtilityGridReduxPower"/>, not the
    /// unconditional one <see cref="PoweredChestUtilityGridReduxPower"/> uses. See
    /// <see cref="UtilityGridReduxSystem"/>'s own remarks for the full mechanism. Has no effect at all
    /// if Utility Grid Redux isn't installed.
    /// </summary>
    public int CrankedPowerCoilUtilityGridReduxPower { get; set; } = 2;

    /// <summary>
    /// MOD: added. The in-game objects that act as a "local" power source for the power system (see
    /// <see cref="PowerSystemEnabled"/>) — e.g. the Powered Chest. Unlike <see cref="PowerSourceNames"/>,
    /// a local power source always powers only its own tile plus the 4 orthogonal neighbors,
    /// regardless of <see cref="PowerRangeDistance"/>. This can be the internal name or qualified
    /// item ID, same format as <see cref="Connectors"/>.
    /// </summary>
    [JsonProperty("LocalPowerSourceNames")]
    public HashSet<string> LocalPowerSourceNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. The in-game objects that act as a "cranked" power source — e.g. the Cranked Power
    /// Coil. Unlike <see cref="LocalPowerSourceNames"/>, a cranked source covers the SAME square area a
    /// regular <see cref="PowerSourceNames"/> entry does (sized by <see cref="PowerRangeDistance"/>) —
    /// but unlike <see cref="PowerSourceNames"/>, it's never counted toward the Power Grid's capacity
    /// (see <see cref="PowerSiloSystem"/>), since this set is never passed into that system at all. A
    /// cranked source is also only ever "on" while the underlying object reports itself powered (see
    /// <see cref="Patches.CrankedPowerCoilPatches.IsPowered"/>) — e.g. the Cranked Power Coil must be
    /// manually cranked each day, unlike a regular Power Coil which is powered automatically whenever
    /// it's within capacity. This can be the internal name or qualified item ID, same format as
    /// <see cref="Connectors"/>.
    /// </summary>
    [JsonProperty("CrankedPowerSourceNames")]
    public HashSet<string> CrankedPowerSourceNames { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "luisMint.PoweredAutomation_CrankedPowerCoil" };

    /// <summary>
    /// MOD: added. Whether the "power-required machines" balance mechanic is enabled — when true, the
    /// machine types listed in <see cref="PowerRequiredMachineNames"/> are only automated while their
    /// own tile is within power range (see <see cref="PowerSourceNames"/>/<see cref="LocalPowerSourceNames"/>),
    /// not just connected to a powered connector like every other machine. A separate toggle from
    /// <see cref="PowerSystemEnabled"/> since this is a deliberate, opinionated gameplay-balance choice
    /// (gating specific strong "passive value" machines) rather than the base power system itself.
    /// </summary>
    public bool PowerRequiredMachinesEnabled { get; set; } = true;

    /// <summary>
    /// MOD: added. The machine types that require their OWN tile to be within power range in order to
    /// be automated OR interacted with at all (see <see cref="PowerRequiredMachinesEnabled"/>) — a
    /// deliberate balance gate for machines that generate a lot of value passively, with little ongoing
    /// resource cost, so automating them removes real gameplay tension rather than just tedium (unlike
    /// e.g. the Mayonnaise Machine or Cheese Press, which keep consuming an ongoing input tied to
    /// animals). Blocks BOTH Automate's own automation and manual player interaction while starved (see
    /// <see cref="Patches.PowerRequiredMachinePatches"/>) — an unpowered machine on this list can't be
    /// fed/collected by hand either, only viewed/emptied.
    ///
    /// Each entry can be EITHER the machine's own resolved internal type ID (letters/digits only, e.g.
    /// "Auto-Grabber" -> "AutoGrabber", the same identifier used by <see cref="MachineOverrides"/> — for
    /// a THIRD-PARTY mod's own machine, that ID has any leading "{ModUniqueID}_" prefix stripped first,
    /// see <see cref="BaseMachine.GetDefaultMachineId(string)"/>'s own remarks) OR the machine's raw
    /// qualified/unqualified item ID (e.g. "(BC)Cornucopia_Extruder" or "Cornucopia_Extruder") — both are
    /// checked, so whichever one's easier to find works. The resolved type ID isn't always intuitive for
    /// a third-party mod whose own internal naming doesn't cleanly reduce to its display name (confirmed
    /// via user report — Cornucopia's own objects use a shorter prefix than their manifest UniqueID, so
    /// stripping doesn't produce "Extruder" the way a player would expect); the raw item ID is a reliable
    /// fallback in that case, since it can be copied directly from any item-ID-showing tool (Lookup
    /// Anything, Chests Anywhere, a debug spawner, etc.). Run the <c>automate summary</c> console command
    /// near the machine to see its resolved type ID rather than guessing either way.
    /// </summary>
    [JsonProperty("PowerRequiredMachineNames")]
    public HashSet<string> PowerRequiredMachineNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Crystalarium",
        "PrismaticCrystalarium",
        "RadioactiveCrystalarium",
        "AutoGrabber",
        "AutoPetter",
        "HeavyFurnace",
        "PrismaticHeavyFurnace",
        "RadioactiveHeavyFurnace",
        "FishSmoker",
        "GoldFishSmoker",
        "DiamondFishSmoker",
        "IridiumFishSmoker",
        "RadioactiveFishSmoker",
        "DeluxeWormBin",
        "PrismaticDeluxeWormBin",
        "DiamondDeluxeWormBin",
        "IridiumDeluxeWormBin",
        "RadioactiveDeluxeWormBin",
        "RecyclingMachine",
        "GoldRecyclingMachine",
        "DiamondRecyclingMachine",
        "IridiumRecyclingMachine",
        "PrismaticRecyclingMachine",
        "RadioactiveRecyclingMachine",
        "SeedMaker",
        "GoldSeedMaker",
        "IronSeedMaker",
        "IridiumSeedMaker",
        "PrismaticSeedMaker",
        "RadioactiveSeedMaker",
        "GeodeCrusher",
        "IridiumGeodeCrusher",
        "PrismaticGeodeCrusher",
        "RadioactiveGeodeCrusher",
        "OstrichIncubator",
        "PrismaticOstrichIncubator",
        "RadioactiveOstrichIncubator",
        "WoodChipper",
        "ElectricFurnace",
        "AlternatorEFurnace",
        "ChromiumFurnace",
        "ChroHFurnace",
        "Pulverizer",
        "RockCrusher",
        "IndustrialDistillery",
        "PerservativePress",
        "SuperGardenCloche",
        "AutoMiner",
        "MineralWasher",
        "GemPolisher",
        "FishingWell",
        "BatteryCharger",
        "ChemicalProcessingMachine",
        "GasSmoker",
        "BigCheesePress",
        "BigMayoMachine",
        "AutoCrafter",

        // MOD: added — the "Stardio" mod's 4 conveyor belt types, gated via StardioConveyorBeltPatches
        // even though a belt isn't a real IMachine. Each type gets its own entry (derived from that
        // type's own in-game display name, the same way every entry above resolves from a real
        // machine's own name) rather than one shared ID, so an individual belt tier can be exempted on
        // its own if wanted. Same on/off mechanism as every entry above: remove any of these to stop
        // gating that belt type, with zero other changes needed. Also a complete no-op if Stardio isn't
        // installed at all — see that class's own remarks.
        "ConveyorBelt",
        "FastConveyorBelt",
        "TurboConveyorBelt",
        "TurboPushingConveyorBelt",

        // MOD: added — the rest of Stardio's factory pieces, gated the same way as the belts above (also
        // via StardioConveyorBeltPatches). Filter/InvertedFilter/Bridge/WarpNexus have no per-tick update
        // of their own — they're pure routing logic inside whichever belt/splitter pushes an item into
        // them — so gating them means refusing to route through/pull from a starved one, rather than
        // pausing a countdown. InputHub/OutputHub are plain BigCraftables (not part of this custom class
        // hierarchy at all) already covered by the fully generic PowerRequiredMachinePatches above; they're
        // listed here purely for discoverability alongside the rest of Stardio's items.
        "Filter",
        "InvertedFilter",
        "Bridge",
        "Splitter",
        "WarpNexus",
        "InputHub",
        "OutputHub",

        // MOD: added — Cornucopia Artisan Machines' own machine types, gated the same way as every entry
        // above (a plain BigCraftable, so already covered by the fully generic PowerRequiredMachinePatches
        // — listed here purely for discoverability). No-op if that mod isn't installed.
        "CornucopiaJuicer",
        "(BC)Cornucopia_DeluxeSmoker",
        "CornucopiaExtruder",
        "CornucopiaCompactMill"
    };

    /// <summary>
    /// MOD: added. Whether to show a periodic "Connected machine needs power in {location}" reminder
    /// (see <see cref="PowerRequiredMachineSystem.ProcessStarvedMachineCallouts"/>) every
    /// <see cref="ConnectedMachineLocationPowerCalloutIntervalSeconds"/> real-world seconds for as long
    /// as a power-required machine remains starved, even while the player isn't directly interacting
    /// with it. Independent of this setting, a single one-off "Machine needs power" message always
    /// shows the moment a machine is first noticed to be starved in a valid group — this only controls
    /// the ONGOING repeated nag on top of that.
    /// </summary>
    public bool ConnectedMachineLocationPowerCallouts { get; set; } = true;

    /// <summary>MOD: added. How often to repeat the "Connected machine needs power in {location}" reminder (see <see cref="ConnectedMachineLocationPowerCallouts"/>), in real-world seconds, for as long as a power-required machine remains starved.</summary>
    public int ConnectedMachineLocationPowerCalloutIntervalSeconds { get; set; } = 12;

    /// <summary>
    /// MOD: added. Whether the "power silo capacity" mechanic is enabled — when true, the total
    /// number of Power Coils that can be active across the whole save is capped by how many Power
    /// Silos (see <see cref="PowerSiloBuildingNames"/>) exist and what tier each has reached (see
    /// <see cref="PowerSiloTiers"/>); any coils beyond that cap simply don't provide power, oldest
    /// placed first. A separate toggle from <see cref="PowerSystemEnabled"/> since this is a
    /// deliberate, opinionated gameplay-balance choice (limiting how much power can exist at once)
    /// rather than the base power system itself. See <see cref="PowerSiloSystem"/>.
    /// </summary>
    public bool PowerSiloSystemEnabled { get; set; } = true;

    /// <summary>
    /// MOD: added. The <c>buildingType</c> ID(s) that count as a Power Silo for the power-silo-capacity
    /// mechanic (see <see cref="PowerSiloSystemEnabled"/>) — the Power Silo, by default.
    /// </summary>
    [JsonProperty("PowerSiloBuildingNames")]
    public HashSet<string> PowerSiloBuildingNames { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "luisMint.PoweredAutomation_PowerSilo" };

    /// <summary>
    /// MOD: added. The Power Coil capacity available across the whole save with no Power Silo built at
    /// all — a small free allowance so early automation isn't hard-gated behind constructing one. Every
    /// Power Silo adds on top of this (see <see cref="PowerSiloTiers"/>).
    /// </summary>
    public int PowerSiloBaseCapacity { get; set; } = 4;

    /// <summary>
    /// MOD: added. Whether <see cref="PowerGridCapacityOverride"/> replaces the normal Power Grid
    /// capacity calculation (<see cref="PowerSiloBaseCapacity"/> plus every Power Silo's own
    /// contribution — see <see cref="PowerSiloSystem.GetTotalCapacity"/>) with a fixed value, e.g. for
    /// testing a specific capacity without re-tiering every Silo.
    /// </summary>
    public bool OverwritePowerGridCapacity { get; set; }

    /// <summary>
    /// MOD: added. The fixed total Power Coil capacity to use when <see cref="OverwritePowerGridCapacity"/>
    /// is enabled — see that field's own remarks. A value of
    /// <see cref="PowerGridCapacityOverrideInfiniteValue"/> means infinite capacity instead of a literal
    /// number — see <see cref="PowerSiloSystem.GetTotalCapacity"/>, the only place this is actually
    /// interpreted specially. This ONLY applies to this override value itself; the normal calculation
    /// (<see cref="PowerSiloBaseCapacity"/> plus every Silo's own tier) is never treated as infinite even
    /// if it happens to sum to the same number.
    /// </summary>
    public int PowerGridCapacityOverride { get; set; }

    /// <summary>
    /// MOD: added. The <see cref="PowerGridCapacityOverride"/> value that means infinite capacity instead
    /// of a literal number — one past the real 0-100 range, so the GMCM slider's own max (see
    /// <see cref="GenericModConfigMenuIntegrationForAutomate"/>) doubles as this sentinel with no separate
    /// toggle needed.
    /// </summary>
    public const int PowerGridCapacityOverrideInfiniteValue = 101;

    /// <summary>
    /// MOD: added. The in-game objects that count as a Solar Panel for the power silo's solar tier
    /// bonus (see <see cref="PowerSiloTierConfig.GrantsSolarBonus"/>) — the vanilla Solar Panel, by
    /// default. This can be the internal name or qualified item ID, same format as
    /// <see cref="Connectors"/>.
    /// </summary>
    [JsonProperty("PowerSiloSolarPanelNames")]
    public HashSet<string> PowerSiloSolarPanelNames { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "(BC)231" };

    /// <summary>
    /// MOD: added. The ordered capacity tiers a Power Silo progresses through (see
    /// <see cref="PowerSiloSystemEnabled"/>) — index 0 is a freshly-built Silo's starting tier. Each
    /// entry's <see cref="PowerSiloTierConfig.RequiredItems"/> is what the Silo asks a player to
    /// deliver to advance to the NEXT entry in the list — one or more item types, each tracked and
    /// delivered independently (see <see cref="PowerSiloInteraction"/>), all of which must be fully
    /// delivered before the tier advances. <see cref="PowerSiloTierConfig.CapacityGranted"/> is the
    /// total capacity THAT SILO ALONE contributes once it has reached that entry (added on top of
    /// <see cref="PowerSiloBaseCapacity"/> and every other Silo's own contribution — not additive
    /// per-tier; each tier's value is the Silo's whole contribution at that tier). The second-to-last
    /// entry asks for a Solar Panel to unlock the true last tier, which has no
    /// <see cref="PowerSiloTierConfig.RequiredItems"/> (nothing more to deliver, ever) but instead
    /// keeps growing via <see cref="PowerSiloTierConfig.GrantsSolarBonus"/> as more Solar Panels get
    /// connected — see <see cref="PowerSiloSystem.GetConnectedSolarPanelCount"/>.
    /// </summary>
    public List<PowerSiloTierConfig> PowerSiloTiers { get; set; } =
    [
        new() { CapacityGranted = 2, RequiredItems = [new() { ItemId = "(O)334", Count = 10 }, new() { ItemId = "(O)78", Count = 5 }] }, // 10 Copper Bar AND 5 Cave Carrot unlocks the next
        new() { CapacityGranted = 4, RequiredItems = [new() { ItemId = "(O)335", Count = 10 }, new() { ItemId = "(O)78", Count = 5 }] }, // 10 Iron Bar AND 5 Cave Carrot unlocks the next
        new() { CapacityGranted = 6, RequiredItems = [new() { ItemId = "(O)334", Count = 10 }, new() { ItemId = "(O)787", Count = 1 }, new() { ItemId = "(O)78", Count = 5 }] }, // 10 Copper Bar, 1 Battery Pack, AND 5 Cave Carrot unlocks the next
        new() { CapacityGranted = 8, RequiredItems = [new() { ItemId = "(O)909", Count = 5 }, new() { ItemId = "(O)78", Count = 5 }] }, // 5 Radioactive Bar AND 5 Cave Carrot unlocks the next
        new() { CapacityGranted = 10, RequiredItems = [new() { ItemId = "(BC)231", Count = 1 }, new() { ItemId = "(O)78", Count = 5 }, new() { ItemId = "(O)909", Count = 3 }] }, // 1 Solar Panel, 5 Cave Carrot, AND 3 Radioactive Bar unlocks the solar tier
        new() { CapacityGranted = 10, RequiredItems = null, GrantsSolarBonus = true } // the solar tier — nothing more to deliver, but keeps growing as more Solar Panels get connected (every 3 add 1 more)
    ];

    /// <summary>
    /// MOD: added. Every geode mineral — shared by the "minerals" slot in each
    /// of <see cref="PowerSiloTierPools"/>'s 5 tiers, which always asks for exactly 1 of ONE randomly
    /// picked mineral from this list (see <see cref="PowerSiloItemOption.ItemIds"/>'s own remarks for
    /// how a nested item-ID list resolves to one random pick). Defined once here instead of duplicating
    /// all 39 IDs across all 5 tiers, so the list can't drift out of sync between tiers.
    /// MOD: changed from private to internal — also reused by <see cref="Patches.DwarfWeeklyShopPatches"/>
    /// for its own "1 already-donated geode mineral" weekly Dwarf shop entry, so
    /// that list stays the single shared source of truth rather than a second copy drifting out of sync.
    /// </summary>
    internal static readonly List<string> MineralItemIds =
    [
        "(O)562", // Tigerseye
        "(O)564", // Opal
        "(O)565", // Fire Opal
        "(O)538", // Alamite
        "(O)539", // Bixite
        "(O)540", // Baryte
        "(O)541", // Aerinite
        "(O)542", // Calcite
        "(O)543", // Dolomite
        "(O)544", // Esperite
        "(O)545", // Fluorapatite
        "(O)546", // Geminite
        "(O)547", // Helvite
        "(O)548", // Jamborite
        "(O)549", // Jagoite
        "(O)550", // Kyanite
        "(O)551", // Lunarite
        "(O)552", // Malachite
        "(O)553", // Neptunite
        "(O)554", // Lemon Stone
        "(O)555", // Nekoite
        "(O)556", // Orpiment
        "(O)557", // Petrified Slime
        "(O)558", // Thunder Egg
        "(O)559", // Pyrite
        "(O)561", // Ghost Crystal
        "(O)563", // Jasper
        "(O)566", // Celestine
        "(O)567", // Marble
        "(O)568", // Sandstone
        "(O)569", // Granite
        "(O)570", // Basalt
        "(O)571", // Limestone
        "(O)572", // Soapstone
        "(O)573", // Hematite
        "(O)574", // Mudstone
        "(O)575", // Obsidian
        "(O)576", // Slate
        "(O)577", // Fairy Stone
        "(O)578"  // Star Shards
    ];

    /// <summary>
    /// MOD: added. Randomized alternatives to <see cref="PowerSiloTiers"/>'s
    /// fixed <see cref="PowerSiloTierConfig.RequiredItems"/> — index-aligned with <see cref="PowerSiloTiers"/>,
    /// each entry's <see cref="PowerSiloTierPool.Slots"/> is rolled ONCE per save (one random option per
    /// slot, with a random count within that option's own range — see <see cref="PowerSiloTierRoller"/>)
    /// into that tier's actual required items, so upgrade costs feel varied between saves without being
    /// fully random every time. A gem-family option (<see cref="PowerSiloItemOption.ItemIds"/>) is itself
    /// a nested random pick — e.g. tier 2 asks for ONE random non-Diamond gem, tier 4 for 3-5 of ONE
    /// random gem including Diamond. This list only has 5 entries (tiers 0-4) — a tier index BEYOND the
    /// end of this list (like tier 5, the solar tier, which has no <see cref="PowerSiloTierConfig.RequiredItems"/>
    /// to roll in the first place) simply keeps using its fixed <see cref="PowerSiloTierConfig.RequiredItems"/> unchanged.
    /// </summary>
    public List<PowerSiloTierPool> PowerSiloTierPools { get; set; } =
    [
        // tier 1 (unlocks tier 2)
        new()
        {
            Slots =
            [
                new() // basic ore/mineral
                {
                    Options =
                    [
                        new() { ItemId = "(O)334", MinCount = 3, MaxCount = 5 },   // Copper Bar
                        new() { ItemId = "(O)378", MinCount = 20, MaxCount = 25 }, // Copper Ore
                        new() { ItemId = "(O)382", MinCount = 10, MaxCount = 15 },   // Coal
                        new() { ItemId = "(O)330", MinCount = 10, MaxCount = 20 },   // Clay (substituted for "Mud", which isn't a real item)
                        new() { ItemId = "(O)390", MinCount = 100, MaxCount = 150 }, // Stone
                        new() { ItemId = "(O)86", MinCount = 5, MaxCount = 10 }     // Earth Crystal
                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 5, MaxCount = 5 }  // Cave Carrot
                    ]
                },
                new() // minerals — MOD: added, always exactly 1 of ONE random geode mineral
                {
                    Options = [new() { ItemIds = ModConfig.MineralItemIds, MinCount = 1, MaxCount = 1 }]
                }
            ]
        },

        // tier 2 (unlocks tier 3)
        new()
        {
            Slots =
            [
                new() // ore/bar/gem
                {
                    Options =
                    [
                        new() { ItemId = "(O)384", MinCount = 10, MaxCount = 15 },  // Gold Ore
                        new() { ItemId = "(O)335", MinCount = 5, MaxCount = 10 },    // Iron Bar
                        new() { ItemId = "(O)390", MinCount = 150, MaxCount = 200 },  // Stone
                        new() { ItemIds = ["(O)60", "(O)62", "(O)64", "(O)66", "(O)68", "(O)70"], MinCount = 3, MaxCount = 5 } // gems except Diamond/Prismatic Shard

                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 5, MaxCount = 10 }  // Cave Carrot
                    ]
                },
                new() // minerals — MOD: added, always exactly 1 of ONE random geode mineral
                {
                    Options = [new() { ItemIds = ModConfig.MineralItemIds, MinCount = 1, MaxCount = 1 }]
                }
            ]
        },

        // tier 3 (unlocks tier 4)
        new()
        {
            Slots =
            [
                new() // battery/quartz
                {
                    Options =
                    [
                        new() { ItemId = "(O)787", MinCount = 5, MaxCount = 10 },   // Battery Pack
                        new() { ItemId = "(O)338", MinCount = 10, MaxCount = 20}   // Refined Quartz
                    ]
                },
                new() // ore/bar
                {
                    Options =
                    [
                        new() { ItemId = "(O)336", MinCount = 5, MaxCount = 5  },   // Gold Bar
                        new() { ItemId = "(O)335", MinCount = 5, MaxCount = 10 },   // Iron Bar
                        new() { ItemId = "(O)380", MinCount = 25, MaxCount = 40 }, // Iron Ore
                        new() { ItemId = "(O)384", MinCount = 40, MaxCount = 50 },   // Gold Ore
                        new() { ItemId = "(O)386", MinCount = 10, MaxCount = 15 }   // Iridium Ore 
                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 10, MaxCount = 10 },                                              // Cave Carrot
                        new() { ItemId = "(O)186", MinCount = 2, MaxCount = 5 },                                             // Large Milk
                        new() { ItemId = "(O)749", MinCount = 10, MaxCount = 20 }                                              // Omni Geode
                        
                    ]
                },
                new() // minerals — MOD: added, always exactly 1 of ONE random geode mineral
                {
                    Options = [new() { ItemIds = ModConfig.MineralItemIds, MinCount = 1, MaxCount = 1 }]
                }
            ]
        },

        // tier 4 (unlocks tier 5)
        new()
        {
            Slots =
            [
                new() // battery/quartz
                {
                    Options =
                    [
                        new() { ItemId = "(O)787", MinCount = 5, MaxCount = 10 },   // Battery Pack
                        new() { ItemId = "(O)338", MinCount = 15, MaxCount = 20 }   // Refined Quartz
                    ]
                },
                new() // bar/coal/gem
                {
                    Options =
                    [
                        new() { ItemId = "(O)336", MinCount = 8, MaxCount = 10 },   // Gold Bar
                        new() { ItemId = "(O)382", MinCount = 20, MaxCount = 30 }, // Coal
                        new() { ItemIds = ["(O)60", "(O)62", "(O)64", "(O)66", "(O)68", "(O)70", "(O)72"], MinCount = 5, MaxCount = 8 }, // any gem including Diamond, except Prismatic Shard
                        new() { ItemId = "(O)386", MinCount = 10, MaxCount = 15 },   // Iridium Ore
                        new() { ItemId = "(O)337", MinCount = 1, MaxCount = 2 }  // Iridium Bar 

                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 10, MaxCount = 15 },                                              // Cave Carrot
                        new() { ItemId = "(O)186", MinCount = 5, MaxCount = 10 },                                             // Large Milk
                        new() { ItemId = "(O)749", MinCount = 10, MaxCount = 15 },                                             // Omni Geode
                        new() { ItemId = "(O)CaveJelly", MinCount = 1, MaxCount = 3 },                                       // Cave Jelly
                        new() { ItemId = "(O)158", MinCount = 1, MaxCount = 2 }                                              // Stonefish
                    ]
                },
                new() // minerals — MOD: added, always exactly 1 of ONE random geode mineral
                {
                    Options = [new() { ItemIds = ModConfig.MineralItemIds, MinCount = 1, MaxCount = 1 }]
                }
            ]
        },

        // tier 5 (unlocks the solar tier)
        new()
        {
            Slots =
            [
                new() // solar panel
                {
                    Options = [new() { ItemId = "(BC)231", MinCount = 1, MaxCount = 1 }] // Solar Panel
                },
                new() // ore/essence
                {
                    Options =
                    [
                        new() { ItemId = "(O)380", MinCount = 30, MaxCount = 45 }, // Iron Ore
                        new() { ItemId = "(O)384", MinCount = 20, MaxCount = 40 }, // Gold Ore
                        new() { ItemId = "(O)768", MinCount = 20, MaxCount = 30 }, // Solar Essence
                        new() { ItemId = "(O)386", MinCount = 15, MaxCount = 20 }, // Iridium Ore
                        new() { ItemId = "(O)337", MinCount = 3, MaxCount = 5 }  // Iridium Bar
                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 10, MaxCount = 15 },                                              // Cave Carrot
                        new() { ItemId = "(O)CaveJelly", MinCount = 3, MaxCount = 4 },                                       // Cave Jelly
                        new() { ItemId = "(O)749", MinCount = 15, MaxCount = 20 }                                              // Omni Geode
                    ]
                },
                new() // minerals — MOD: added, always exactly 1 of ONE random geode mineral
                {
                    Options = [new() { ItemIds = ModConfig.MineralItemIds, MinCount = 1, MaxCount = 1 }]
                }
            ]
        }
    ];

    /// <summary>
    /// MOD: added. Whether the Power Relay mechanic is enabled — when true, each Prismatic Shard
    /// delivered to a Power Relay building (up to <see cref="PowerRelaySystem.MaxShards"/> per Relay)
    /// subtracts <see cref="PowerRelayActionDelayReductionPerShardSeconds"/> from
    /// <see cref="ActionDelaySeconds"/>, and each Radioactive Bar delivered (up to
    /// <see cref="PowerRelaySystem.MaxBars"/> per Relay) adds <see cref="PowerRelayActionsPerDelayWindowBonusPerBar"/>
    /// to <see cref="ActionsPerDelayWindow"/>, summed across every Relay in the save (global, like
    /// <see cref="PowerSiloSystemEnabled"/>'s capacity mechanic). Delivery is one-way, like a Power Silo
    /// tier — see <see cref="PowerRelaySystem"/>.
    /// </summary>
    public bool PowerRelaySystemEnabled { get; set; } = true;

    /// <summary>MOD: added. The <c>buildingType</c> ID(s) that count as a Power Relay for the efficiency-bonus mechanic (see <see cref="PowerRelaySystemEnabled"/>) — the Power Relay, by default.</summary>
    [JsonProperty("PowerRelayBuildingNames")]
    public HashSet<string> PowerRelayBuildingNames { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "luisMint.PoweredAutomation_PowerRelay" };

    /// <summary>MOD: added. The qualified/unqualified item ID delivered to a Power Relay for the delay-reduction track — Prismatic Shard, by default. Only accepted from level 1 onward; the very first delivery (level 0→1) instead asks for <see cref="PowerRelayFirstShardItemId"/>.</summary>
    public string PowerRelayShardItemId { get; set; } = "(O)74";

    /// <summary>MOD: changed — reverted to Prismatic Shard (matching <see cref="PowerRelayShardItemId"/>), so the delay-reduction track is a plain "1 shard, 2 shards, 3 shards, 4 shards" progression across all 4 levels rather than a special first item.</summary>
    public string PowerRelayFirstShardItemId { get; set; } = "(O)74";

    /// <summary>MOD: added. The qualified/unqualified item ID delivered to a Power Relay for the actions-per-window bonus track — Radioactive Bar, by default. MOD: fixed — (O)909 is actually Radioactive Ore (the raw/unsmelted item); Radioactive Bar (the smelted one) is (O)910, confirmed via the Stardew Valley Wiki after this defaulted to the wrong item. Only accepted from level 1 onward; the very first delivery (level 0→1) instead asks for <see cref="PowerRelayFirstBarItemId"/>.</summary>
    public string PowerRelayBarItemId { get; set; } = "(O)910";

    /// <summary>MOD: changed — reverted to Radioactive Bar (matching <see cref="PowerRelayBarItemId"/>), so the actions-per-window track is a plain "1 bar, 2 bars, 3 bars, 4 bars" progression across all 4 levels rather than a special first item.</summary>
    public string PowerRelayFirstBarItemId { get; set; } = "(O)910";

    /// <summary>MOD: added. How much a single delivered shard subtracts from <see cref="ActionDelaySeconds"/>, in seconds, summed across every shard delivered to every Relay in the save (each Relay accepts up to <see cref="PowerRelaySystem.MaxShards"/>). The effective delay is floored at <see cref="PowerRelayMinimumActionDelaySeconds"/> regardless of how many are delivered.</summary>
    public float PowerRelayActionDelayReductionPerShardSeconds { get; set; } = 0.4f;

    /// <summary>
    /// MOD: added. The lowest <see cref="ActionDelaySeconds"/> can ever be pushed down to by Power Relay
    /// shard deliveries, globally across every Relay in the save — a hard floor
    /// distinct from <see cref="ActionDelaySeconds"/> itself possibly already being lower (in which case
    /// this has no effect either way). Once the effective delay has been pushed down to this floor, EVERY
    /// Relay's shard track stops accepting further deliveries entirely (see <see cref="PowerRelaySystem.IsGlobalSpeedCapped"/>)
    /// — deliberately no equivalent cap on the actions-per-window side, which the player can keep
    /// upgrading without limit.
    /// </summary>
    public float PowerRelayMinimumActionDelaySeconds { get; set; } = 0.6f;

    /// <summary>MOD: added. How much a single delivered bar adds to <see cref="ActionsPerDelayWindow"/>, summed across every bar delivered to every Relay in the save (each Relay accepts up to <see cref="PowerRelaySystem.MaxBars"/>).</summary>
    public int PowerRelayActionsPerDelayWindowBonusPerBar { get; set; } = 2;

    /// <summary>
    /// MOD: added. The animation speed, in frames per second, for a connector that's powered but not
    /// part of a valid (active) automation group — a "flickering" cue that it's connected to power
    /// but isn't actually automating anything (e.g. missing a machine or chest). See
    /// <see cref="Patches.ConnectorTexturePatches"/>.
    /// </summary>
    public double PoweredFloorAnimationFps { get; set; } = 6;

    /// <summary>
    /// MOD: added. How many times longer to hold the fully-unpowered frame, relative to the other
    /// frames, in the "powered but not part of a valid group" flicker animation. See
    /// <see cref="Patches.ConnectorTexturePatches"/>.
    /// </summary>
    public double PoweredFloorUnpoweredHoldMultiplier { get; set; } = 2;

    /// <summary>Whether to collect moss on trees.</summary>
    public bool CollectTreeMoss { get; set; } = true;

    /// <summary>Whether to log a warning if the player installs a custom-machine mod that requires a separate compatibility patch which isn't installed.</summary>
    public bool WarnForMissingBridgeMod { get; set; } = true;

    /// <summary>Whether chests should be automated (true) or ignored (false) by default, unless overridden by <see cref="ChestOverrides"/>.</summary>
    public bool ChestsEnabledByDefault { get; set; } = true;

    /// <summary>
    /// MOD: added. Whether a normal chest (anything that isn't a Powered Chest or a chest-backed
    /// hybrid — see <see cref="ChestHybridsCanAutomate"/>) can be automatically pushed into or pulled
    /// from by the STANDARD machine automation cycle — e.g. a Furnace storing its finished bars into
    /// one, or pulling ore out of one. Disabling this only affects that standard cycle: a Powered Chest
    /// (if <see cref="PoweredChestsCanAutomate"/> is still true) can still directly place items into,
    /// or take items from, a normal chest through its own piped connectors regardless of this setting,
    /// since that's a separate mechanism that doesn't go through the standard cycle at all. A group
    /// made up ONLY of machines and a disabled category (nothing else) no longer counts as "actually
    /// automating anything" either (see <see cref="MachineGroup.HasLocalInternalAutomation"/>) — e.g. a
    /// Furnace piped to nothing but a disabled normal chest just sits there, same as if it weren't
    /// piped to anything at all. Mainly meant for testing the relative priority between normal chests,
    /// Powered Chests, and hybrids (see <see cref="IHasContainerPriority"/>) in isolation.
    /// </summary>
    public bool ChestsCanAutomate { get; set; } = true;

    /// <summary>
    /// MOD: added. Whether a chest-backed machine/chest hybrid (Hopper, Auto-Grabber, Mini-Shipping
    /// Bin, Junimo Hut — see <see cref="ChestHybridStorage"/>) can be pushed into or pulled from by the
    /// STANDARD machine automation cycle (e.g. a Furnace storing its output into one). A hybrid has no
    /// movement logic of its own at all (it's a plain <see cref="IContainer"/>, not an
    /// <see cref="IMachine"/>) — this setting only controls whether OTHER machines can use it as
    /// storage, the same as <see cref="ChestsCanAutomate"/> does for a normal chest. A Powered Chest (if
    /// <see cref="PoweredChestsCanAutomate"/> is still true) can still directly reach into it through
    /// its own piped connectors regardless of this setting, since that's a separate mechanism that
    /// doesn't go through the standard cycle at all (see <see cref="ChestsCanAutomate"/>'s remarks for
    /// why, including the effect on whether a group counts as "actually automating anything"). The
    /// hybrid's own vanilla behavior (e.g. the Hopper feeding a machine directly below it) is untouched
    /// either way.
    /// </summary>
    public bool ChestHybridsCanAutomate { get; set; } = true;

    /// <summary>
    /// MOD: added. Whether a Powered Chest can push/pull items through its own piped connectors, AND
    /// be used as plain storage by other machines in its group — see <see cref="ChestHybridsCanAutomate"/>'s
    /// remarks for the equivalent hybrid setting (including the effect on whether a group counts as
    /// "actually automating anything"). If every one of these three settings were false, no group could
    /// ever count as active at all; if all three are true (the default), this behaves exactly like
    /// before these settings existed.
    /// </summary>
    public bool PoweredChestsCanAutomate { get; set; } = true;

    /// <summary>Whether each chest type should be used as storage, for types which override <see cref="ChestsEnabledByDefault"/>.</summary>
    public Dictionary<string, ModConfigStorage> ChestOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The configuration for specific machines by ID.</summary>
    public Dictionary<string, ModConfigMachine> MachineOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The minimum machine processing time in minutes for which to apply fairy dust.</summary>
    public int MinMinutesForFairyDust { get; set; } = 20;

    /// <summary>
    /// MOD: added. What percentage (0-100) of the skill experience a machine/action
    /// would normally grant on harvest is actually granted when Automate collects it automatically,
    /// instead of the player collecting it by hand — see the three grant sites this scales:
    /// <see cref="Machines.DataBasedObjectMachine"/> (vanilla <c>Data/Machines</c> ExperienceGainOnHarvest,
    /// e.g. Bee Houses/Recycling Machines), <see cref="Machines.Objects.CrabPotMachine"/> (fishing XP), and
    /// <see cref="Machines.Buildings.FishPondMachine"/> (fishing XP). Defaults to 0 — automation grants no
    /// experience at all unless raised.
    /// </summary>
    public int AutomationExperiencePercent { get; set; } = 0;

    /// <summary>
    /// MOD: added. Whether <see cref="ActionDelaySeconds"/> in this file takes effect.
    /// In-game progression (delivering Prismatic Shards to a Power Relay — see
    /// <see cref="PowerRelaySystemEnabled"/>) is meant to be the main way automation pacing improves, not
    /// a config edit — so while this is <c>false</c> (the default), <see cref="ActionDelaySeconds"/> is
    /// reset to <see cref="DefaultActionDelaySeconds"/> every time the config loads, regardless of what's
    /// written here. Set to <c>true</c> to let your own value below actually take effect instead.
    /// </summary>
    public bool OverwriteAutomationDelay { get; set; } = false;

    /// <summary>
    /// MOD: added. Whether <see cref="ActionsPerDelayWindow"/> in this file takes effect.
    /// In-game progression (delivering Radioactive Bars to a Power Relay — see
    /// <see cref="PowerRelaySystemEnabled"/>) is meant to be the main way automation pacing improves, not
    /// a config edit — so while this is <c>false</c> (the default), <see cref="ActionsPerDelayWindow"/> is
    /// reset to <see cref="DefaultActionsPerDelayWindow"/> every time the config loads, regardless of
    /// what's written here. Set to <c>true</c> to let your own value below actually take effect instead.
    /// </summary>
    public bool OverwriteAutomationActions { get; set; } = false;

    /// <summary>
    /// MOD: added. How many real-time seconds a group waits before each batch of push/pull actions (see
    /// <see cref="ActionsPerDelayWindow"/>), including the very first batch for a freshly-active group —
    /// applies in BOTH interval mode and event-based mode. 0 disables this entirely (the original instant,
    /// whole-group-at-once behavior). Only takes effect if
    /// <see cref="OverwriteAutomationDelay"/> is <c>true</c> — otherwise always resets to
    /// <see cref="DefaultActionDelaySeconds"/> on load.
    ///
    /// Paced per group via <c>ModEntry.GroupActionQueues</c> — a FIFO queue of that group's own machines,
    /// drained a batch at a time. Deliberately does NOT try to carry a group's pacing forward across a
    /// rebuild that recreates its wrapper instance; a rebuild just resets the
    /// affected group's pacing to fresh (its machines get rediscovered and re-queued from scratch by the
    /// normal triggers) rather than trying to bridge old-to-new group instances, which is what caused most
    /// of the fragility in earlier attempts at this feature. A separate group's own queue and pacing always
    /// runs fully independently.
    /// </summary>
    public float ActionDelaySeconds { get; set; } = ModConfig.DefaultActionDelaySeconds;

    /// <summary>
    /// MOD: added. How many machines a group may drain from the FRONT of its action queue (see
    /// <see cref="ActionDelaySeconds"/>) in one batch, before the rest have to wait for the next one.
    /// <c>0</c> (or less) means unlimited — drain the group's entire queue in one batch. Has no effect
    /// when <see cref="ActionDelaySeconds"/> is 0. Only takes effect if
    /// <see cref="OverwriteAutomationActions"/> is <c>true</c> — otherwise always resets to
    /// <see cref="DefaultActionsPerDelayWindow"/> on load.
    /// </summary>
    public int ActionsPerDelayWindow { get; set; } = ModConfig.DefaultActionsPerDelayWindow;

    /// <summary>
    /// MOD: added. Whether a container shows a lid animation, a jolt, a flying item
    /// sprite, and a sound whenever an item enters or leaves it through automation (see
    /// <see cref="ThrottledContainer"/>/<see cref="ContainerVisualEffects"/>). This ONLY gates those
    /// visuals/audio — the underlying chunked delivery pacing itself (<see cref="ActionDelaySeconds"/>/
    /// <see cref="ActionsPerDelayWindow"/>, including any Power Relay bonus) always applies regardless
    /// of this setting. Also only ever plays for a location a player is actually standing in (see
    /// <see cref="ContainerVisualEffects"/>'s own remarks) — background automation elsewhere never pays
    /// for or shows these regardless of this setting either.
    /// </summary>
    public bool AnimatedItemTransfers { get; set; } = true;


    /*********
    ** Public methods
    *********/
    /// <summary>Normalize the model after it's deserialized.</summary>
    /// <param name="context">The deserialization context.</param>
    [OnDeserialized]
    [SuppressMessage("ReSharper", "ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract", Justification = SuppressReasons.MethodValidatesNullability)]
    [SuppressMessage("ReSharper", "NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract", Justification = SuppressReasons.MethodValidatesNullability)]
    [SuppressMessage("ReSharper", "UnusedMember.Global", Justification = SuppressReasons.UsedViaOnDeserialized)]
    public void OnDeserialized(StreamingContext context)
    {
        this.Controls ??= new ModConfigKeys();

        // MOD: added — ActionDelaySeconds/ActionsPerDelayWindow are each locked
        // to their own fixed default unless the player opts in via the matching Overwrite flag, so
        // in-game progression (the Power Relay) stays the main way automation pacing improves.
        if (!this.OverwriteAutomationDelay)
            this.ActionDelaySeconds = ModConfig.DefaultActionDelaySeconds;
        if (!this.OverwriteAutomationActions)
            this.ActionsPerDelayWindow = ModConfig.DefaultActionsPerDelayWindow;

        // MOD: added — guard against a nonsensical (hand-edited) negative delay/batch size; 0 is valid for both (0 batch size means unlimited).
        if (this.ActionDelaySeconds < 0)
            this.ActionDelaySeconds = 0;
        if (this.ActionsPerDelayWindow < 0)
            this.ActionsPerDelayWindow = 0;

        this.Connectors = this.Connectors.ToNonNullCaseInsensitive();
        this.Connectors.RemoveWhere(string.IsNullOrWhiteSpace);

        // MOD: added — normalize the two new connector role sets the same way as Connectors.
        this.ChestInputConnectors = this.ChestInputConnectors.ToNonNullCaseInsensitive();
        this.ChestInputConnectors.RemoveWhere(string.IsNullOrWhiteSpace);

        this.ChestOutputConnectors = this.ChestOutputConnectors.ToNonNullCaseInsensitive();
        this.ChestOutputConnectors.RemoveWhere(string.IsNullOrWhiteSpace);

        // MOD: added — normalize the two new sign filter sets the same way.
        this.WhitelistSignNames = this.WhitelistSignNames.ToNonNullCaseInsensitive();
        this.WhitelistSignNames.RemoveWhere(string.IsNullOrWhiteSpace);

        this.BlacklistSignNames = this.BlacklistSignNames.ToNonNullCaseInsensitive();
        this.BlacklistSignNames.RemoveWhere(string.IsNullOrWhiteSpace);

        this.WhitelistCategorySignNames = this.WhitelistCategorySignNames.ToNonNullCaseInsensitive();
        this.WhitelistCategorySignNames.RemoveWhere(string.IsNullOrWhiteSpace);

        this.BlacklistCategorySignNames = this.BlacklistCategorySignNames.ToNonNullCaseInsensitive();
        this.BlacklistCategorySignNames.RemoveWhere(string.IsNullOrWhiteSpace);

        // MOD: added — normalize both the category-name keys AND each category's own member set.
        this.CustomCategories = this.CustomCategories.ToNonNullCaseInsensitive();
        foreach (string categoryName in this.CustomCategories.Keys.ToArray())
        {
            HashSet<string> members = this.CustomCategories[categoryName].ToNonNullCaseInsensitive();
            members.RemoveWhere(string.IsNullOrWhiteSpace);
            this.CustomCategories[categoryName] = members;
        }

        // MOD: added — normalize the power source set the same way, and guard against a nonsensical range.
        this.PowerSourceNames = this.PowerSourceNames.ToNonNullCaseInsensitive();
        this.PowerSourceNames.RemoveWhere(string.IsNullOrWhiteSpace);
        if (this.PowerRangeDistance < 0)
            this.PowerRangeDistance = 0;
        if (this.PowerCoilUtilityGridReduxPower < 0)
            this.PowerCoilUtilityGridReduxPower = 0;
        if (this.PoweredChestUtilityGridReduxPower < 0)
            this.PoweredChestUtilityGridReduxPower = 0;
        if (this.CrankedPowerCoilUtilityGridReduxPower < 0)
            this.CrankedPowerCoilUtilityGridReduxPower = 0;

        // MOD: added — normalize the local power source set the same way.
        this.LocalPowerSourceNames = this.LocalPowerSourceNames.ToNonNullCaseInsensitive();
        this.LocalPowerSourceNames.RemoveWhere(string.IsNullOrWhiteSpace);

        // MOD: added — normalize the cranked power source set the same way.
        this.CrankedPowerSourceNames = this.CrankedPowerSourceNames.ToNonNullCaseInsensitive();
        this.CrankedPowerSourceNames.RemoveWhere(string.IsNullOrWhiteSpace);

        // MOD: added — normalize the power-required machine set the same way.
        this.PowerRequiredMachineNames = this.PowerRequiredMachineNames.ToNonNullCaseInsensitive();
        this.PowerRequiredMachineNames.RemoveWhere(string.IsNullOrWhiteSpace);

        // MOD: added — normalize the power silo building set the same way, and drop any tier entry
        // that's missing entirely (a malformed/empty list element from hand-edited JSON).
        this.PowerSiloBuildingNames = this.PowerSiloBuildingNames.ToNonNullCaseInsensitive();
        this.PowerSiloBuildingNames.RemoveWhere(string.IsNullOrWhiteSpace);
        if (this.PowerSiloBaseCapacity < 0)
            this.PowerSiloBaseCapacity = 0;
        this.PowerGridCapacityOverride = Math.Clamp(this.PowerGridCapacityOverride, 0, ModConfig.PowerGridCapacityOverrideInfiniteValue);
        this.PowerSiloSolarPanelNames = this.PowerSiloSolarPanelNames.ToNonNullCaseInsensitive();
        this.PowerSiloSolarPanelNames.RemoveWhere(string.IsNullOrWhiteSpace);
        this.PowerSiloTiers ??= [];
        this.PowerSiloTiers.RemoveAll(tier => tier is null);
        foreach (PowerSiloTierConfig tier in this.PowerSiloTiers)
            tier.RequiredItems?.RemoveAll(item => item is null);

        // MOD: added — normalize PowerSiloTierPools the same way, and drop any slot/option that's
        // missing entirely (a malformed/empty entry from hand-edited JSON) rather than letting the
        // roller trip over a null later.
        this.PowerSiloTierPools ??= [];
        this.PowerSiloTierPools.RemoveAll(pool => pool is null);
        foreach (PowerSiloTierPool pool in this.PowerSiloTierPools)
        {
            pool.Slots?.RemoveAll(slot => slot is null);
            foreach (PowerSiloSlotPool slot in pool.Slots ?? [])
                slot.Options?.RemoveAll(option => option is null);
        }

        // MOD: added — normalize the power relay building set the same way, and guard against
        // nonsensical (hand-edited) bonus values.
        this.PowerRelayBuildingNames = this.PowerRelayBuildingNames.ToNonNullCaseInsensitive();
        this.PowerRelayBuildingNames.RemoveWhere(string.IsNullOrWhiteSpace);
        if (this.PowerRelayActionsPerDelayWindowBonusPerBar < 0)
            this.PowerRelayActionsPerDelayWindowBonusPerBar = 0;
        if (this.PowerRelayActionDelayReductionPerShardSeconds < 0)
            this.PowerRelayActionDelayReductionPerShardSeconds = 0;
        if (this.PowerRelayMinimumActionDelaySeconds < 0)
            this.PowerRelayMinimumActionDelaySeconds = 0;

        // MOD: added — guard against a nonsensical animation speed/hold multiplier.
        if (this.PoweredFloorAnimationFps <= 0)
            this.PoweredFloorAnimationFps = 6;
        if (this.PoweredFloorUnpoweredHoldMultiplier < 1)
            this.PoweredFloorUnpoweredHoldMultiplier = 1;

        this.ChestOverrides = this.ChestOverrides.ToNonNullCaseInsensitive();
        this.ChestOverrides.RemoveWhere(pair => pair.Value is null);

        this.MachineOverrides = this.MachineOverrides.ToNonNullCaseInsensitive();
        this.MachineOverrides.RemoveWhere(pair => pair.Value is null);
    }
}

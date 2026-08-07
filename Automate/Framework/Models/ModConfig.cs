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
    /// <summary>MOD: added. The fixed value <see cref="ActionDelaySeconds"/> is reset to on load unless <see cref="OverwriteAutomationSettings"/> is <c>true</c>.</summary>
    private const float DefaultActionDelaySeconds = 7f;

    /// <summary>MOD: added. The fixed value <see cref="ActionsPerDelayWindow"/> is reset to on load unless <see cref="OverwriteAutomationSettings"/> is <c>true</c>.</summary>
    private const int DefaultActionsPerDelayWindow = 1;


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
    public HashSet<string> PowerSourceNames { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "luisMint.AutomatePowerPipes_PowerCoil" };

    /// <summary>
    /// MOD: added. How many tiles out from a power source, in each of the 4 cardinal directions, its
    /// power extends — e.g. 2 means a square reaching 2 tiles in every direction from the source,
    /// covering 5x5 tiles total (2 + 1 center + 2). Defined as a distance rather than a total width
    /// so the covered area is always exactly centered on the source, with no rounding ambiguity.
    /// </summary>
    public int PowerRangeDistance { get; set; } = 2;

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
    /// be automated at all (see <see cref="PowerRequiredMachinesEnabled"/>) — a deliberate balance gate
    /// for machines that generate a lot of value passively, with little ongoing resource cost, so
    /// automating them removes real gameplay tension rather than just tedium (unlike e.g. the Mayonnaise
    /// Machine or Cheese Press, which keep consuming an ongoing input tied to animals). Only affects
    /// Automate itself — an unpowered machine still works fine if fed/collected by hand. Matched against
    /// the machine's own internal type ID (letters/digits only, e.g. "Auto-Grabber" -> "AutoGrabber"),
    /// the same identifier used by <see cref="MachineOverrides"/>.
    /// </summary>
    [JsonProperty("PowerRequiredMachineNames")]
    public HashSet<string> PowerRequiredMachineNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Crystalarium",
        "AutoGrabber",
        "AutoPetter",
        "HeavyFurnace"
    };

    /// <summary>
    /// MOD: added. Whether to show a periodic "Connected machine needs power in {location}" reminder
    /// (see <see cref="PowerRequiredMachineSystem.ProcessStarvedMachineCallouts"/>) every 12
    /// real-world seconds for as long as a power-required machine remains starved, even while the
    /// player isn't directly interacting with it. Independent of this setting, a single one-off
    /// "Machine needs power" message always shows the moment a machine is first noticed to be starved
    /// in a valid group — this only controls the ONGOING repeated nag on top of that.
    /// </summary>
    public bool ConnectedMachineLocationPowerCallouts { get; set; } = true;

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
    public HashSet<string> PowerSiloBuildingNames { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "luisMint.AutomatePowerPipes_PowerSilo" };

    /// <summary>
    /// MOD: added. The Power Coil capacity available across the whole save with no Power Silo built at
    /// all — a small free allowance so early automation isn't hard-gated behind constructing one. Every
    /// Power Silo adds on top of this (see <see cref="PowerSiloTiers"/>).
    /// </summary>
    public int PowerSiloBaseCapacity { get; set; } = 3;

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
    /// MOD: added. Per direct user request, randomized alternatives to <see cref="PowerSiloTiers"/>'s
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
                        new() { ItemId = "(O)378", MinCount = 20, MaxCount = 30 }, // Copper Ore
                        new() { ItemId = "(O)382", MinCount = 5, MaxCount = 5 },   // Coal
                        new() { ItemId = "(O)330", MinCount = 5, MaxCount = 10 },   // Clay (substituted for "Mud", which isn't a real item)
                        new() { ItemId = "(O)390", MinCount = 30, MaxCount = 50 }, // Stone
                        new() { ItemId = "(O)86", MinCount = 1, MaxCount = 3 }     // Earth Crystal
                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 1, MaxCount = 3 }  // Cave Carrot
                    ]
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
                        new() { ItemId = "(O)334", MinCount = 5, MaxCount = 10 },   // Copper Bar
                        new() { ItemId = "(O)335", MinCount = 4, MaxCount = 8 },    // Iron Bar
                        new() { ItemId = "(O)390", MinCount = 65, MaxCount = 80 },  // Stone
                        new() { ItemIds = ["(O)60", "(O)62", "(O)64", "(O)66", "(O)68", "(O)70"], MinCount = 1, MaxCount = 1 }, // any gem except Diamond/Prismatic Shard
                        new() { ItemId = "(O)86", MinCount = 3, MaxCount = 5 }      // Earth Crystal
                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 3, MaxCount = 5 }  // Cave Carrot
                    ]
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
                        new() { ItemId = "(O)787", MinCount = 2, MaxCount = 3 },   // Battery Pack
                        new() { ItemId = "(O)338", MinCount = 10, MaxCount = 20 }   // Refined Quartz
                    ]
                },
                new() // ore/bar
                {
                    Options =
                    [
                        new() { ItemId = "(O)334", MinCount = 10, MaxCount = 15 },   // Copper Bar
                        new() { ItemId = "(O)335", MinCount = 7, MaxCount = 10 },   // Iron Bar
                        new() { ItemId = "(O)380", MinCount = 50, MaxCount = 60 }, // Iron Ore
                        new() { ItemId = "(O)384", MinCount = 20, MaxCount = 40 }   // Gold Ore
                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 5, MaxCount = 8 },                                              // Cave Carrot
                        new() { ItemId = "(O)186", MinCount = 2, MaxCount = 2 },                                             // Large Milk
                        new() { ItemId = "(O)749", MinCount = 2, MaxCount = 5 }                                              // Omni Geode
                    ]
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
                        new() { ItemId = "(O)787", MinCount = 2, MaxCount = 8 },   // Battery Pack
                        new() { ItemId = "(O)338", MinCount = 20, MaxCount = 30 }   // Refined Quartz
                    ]
                },
                new() // bar/coal/gem
                {
                    Options =
                    [
                        new() { ItemId = "(O)336", MinCount = 9, MaxCount = 14 },   // Gold Bar
                        new() { ItemId = "(O)382", MinCount = 35, MaxCount = 58 }, // Coal
                        new() { ItemIds = ["(O)60", "(O)62", "(O)64", "(O)66", "(O)68", "(O)70", "(O)72"], MinCount = 3, MaxCount = 5 }, // any gem including Diamond, except Prismatic Shard
                        new() { ItemId = "(O)386", MinCount = 5, MaxCount = 10 }   // Iridium Ore 
                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 5, MaxCount = 10 },                                              // Cave Carrot
                        new() { ItemId = "(O)186", MinCount = 2, MaxCount = 5 },                                             // Large Milk
                        new() { ItemId = "(O)749", MinCount = 5, MaxCount = 10 },                                             // Omni Geode
                        new() { ItemId = "(O)158", MinCount = 1, MaxCount = 1 }                                              // Stonefish
                    ]
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
                        new() { ItemId = "(O)909", MinCount = 1, MaxCount = 1 },   // Radioactive Ore
                        new() { ItemId = "(O)768", MinCount = 15, MaxCount = 20 }, // Solar Essence
                        new() { ItemId = "(O)386", MinCount = 10, MaxCount = 20 }  // Iridium Ore — MOD: deliberately NOT scaled, per direct user request
                    ]
                },
                new() // cave carrot family
                {
                    Options =
                    [
                        new() { ItemId = "(O)78", MinCount = 15, MaxCount = 30 },                                              // Cave Carrot
                        new() { ItemId = "(O)CaveJelly", MinCount = 1, MaxCount = 2 },                                       // Cave Jelly (substituted for "Dehydrated Cave Carrot", which isn't a real item) — MOD: deliberately NOT scaled, per direct user request. Unverified ID, couldn't confirm against the wiki; fix this if it turns out wrong
                        new() { ItemId = "(O)749", MinCount = 15, MaxCount = 30 }                                              // Omni Geode
                    ]
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
    public HashSet<string> PowerRelayBuildingNames { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "luisMint.AutomatePowerPipes_PowerRelay" };

    /// <summary>MOD: added. The qualified/unqualified item ID delivered to a Power Relay for the delay-reduction track — Prismatic Shard, by default. Only accepted from level 1 onward; the very first delivery (level 0→1) instead asks for <see cref="PowerRelayFirstShardItemId"/>.</summary>
    public string PowerRelayShardItemId { get; set; } = "(O)74";

    /// <summary>MOD: added. Per direct user request, the qualified/unqualified item ID accepted for the delay-reduction track's very FIRST delivery only (level 0→1) — Diamond, by default. Every delivery from level 1 onward reverts to <see cref="PowerRelayShardItemId"/>.</summary>
    public string PowerRelayFirstShardItemId { get; set; } = "(O)72";

    /// <summary>MOD: added. The qualified/unqualified item ID delivered to a Power Relay for the actions-per-window bonus track — Radioactive Bar, by default. MOD: fixed — (O)909 is actually Radioactive Ore (the raw/unsmelted item); Radioactive Bar (the smelted one) is (O)910, confirmed via the Stardew Valley Wiki after this defaulted to the wrong item. Only accepted from level 1 onward; the very first delivery (level 0→1) instead asks for <see cref="PowerRelayFirstBarItemId"/>.</summary>
    public string PowerRelayBarItemId { get; set; } = "(O)910";

    /// <summary>MOD: added. Per direct user request, the qualified/unqualified item ID accepted for the actions-per-window track's very FIRST delivery only (level 0→1) — Radioactive Ore, by default. Every delivery from level 1 onward reverts to <see cref="PowerRelayBarItemId"/>.</summary>
    public string PowerRelayFirstBarItemId { get; set; } = "(O)909";

    /// <summary>MOD: added. How much a single delivered shard subtracts from <see cref="ActionDelaySeconds"/>, in seconds, summed across every shard delivered to every Relay in the save (each Relay accepts up to <see cref="PowerRelaySystem.MaxShards"/>). The effective delay is floored at <see cref="PowerRelayMinimumActionDelaySeconds"/> regardless of how many are delivered.</summary>
    public float PowerRelayActionDelayReductionPerShardSeconds { get; set; } = 0.4f;

    /// <summary>
    /// MOD: added. The lowest <see cref="ActionDelaySeconds"/> can ever be pushed down to by Power Relay
    /// shard deliveries, globally across every Relay in the save — per direct user request, a hard floor
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
    /// <see cref="PoweredFloorAnimator"/>.
    /// </summary>
    public double PoweredFloorAnimationFps { get; set; } = 6;

    /// <summary>
    /// MOD: added. How many times longer to hold the fully-unpowered frame, relative to the other
    /// frames, in the "powered but not part of a valid group" flicker animation. See
    /// <see cref="PoweredFloorAnimator"/>.
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
    /// MOD: added. Whether <see cref="ActionDelaySeconds"/> in this file takes effect. Per direct user
    /// request, in-game progression (delivering Prismatic Shards to a Power Relay — see
    /// <see cref="PowerRelaySystemEnabled"/>) is meant to be the main way automation pacing improves, not
    /// a config edit — so while this is <c>false</c> (the default), <see cref="ActionDelaySeconds"/> is
    /// reset to <see cref="DefaultActionDelaySeconds"/> every time the config loads, regardless of what's
    /// written here. Set to <c>true</c> to let your own value below actually take effect instead.
    /// </summary>
    public bool OverwriteAutomationDelay { get; set; } = false;

    /// <summary>
    /// MOD: added. Whether <see cref="ActionsPerDelayWindow"/> in this file takes effect. Per direct user
    /// request, in-game progression (delivering Radioactive Bars to a Power Relay — see
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
    /// rebuild that recreates its wrapper instance; per direct user feedback, a rebuild just resets the
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

        // MOD: added — per direct user request, ActionDelaySeconds/ActionsPerDelayWindow are each locked
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

        // MOD: added — normalize the local power source set the same way.
        this.LocalPowerSourceNames = this.LocalPowerSourceNames.ToNonNullCaseInsensitive();
        this.LocalPowerSourceNames.RemoveWhere(string.IsNullOrWhiteSpace);

        // MOD: added — normalize the power-required machine set the same way.
        this.PowerRequiredMachineNames = this.PowerRequiredMachineNames.ToNonNullCaseInsensitive();
        this.PowerRequiredMachineNames.RemoveWhere(string.IsNullOrWhiteSpace);

        // MOD: added — normalize the power silo building set the same way, and drop any tier entry
        // that's missing entirely (a malformed/empty list element from hand-edited JSON).
        this.PowerSiloBuildingNames = this.PowerSiloBuildingNames.ToNonNullCaseInsensitive();
        this.PowerSiloBuildingNames.RemoveWhere(string.IsNullOrWhiteSpace);
        if (this.PowerSiloBaseCapacity < 0)
            this.PowerSiloBaseCapacity = 0;
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

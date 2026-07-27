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
    ** Accessors
    *********/
    /// <summary>Whether Automate is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The number of ticks between each automation process (60 = once per second).</summary>
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
    /// MOD: added. Maps a connector's <c>Data/FloorsAndPaths</c> ID to the Alternative Textures
    /// texture ID (in the form <c>{Owner}.{ModelName}</c>, e.g.
    /// <c>luisMint.ATAutomatePowerPipes.Flooring_luisMint.AutomatePowerPipes_PullPushPipe</c>)
    /// providing its four appearance variations: 0 = unpowered, 1 = powered, 2 = powered (dimmer),
    /// 3 = powered (dimmest). Empty by default; populated for a custom connector that has a matching
    /// Alternative Textures content pack installed. Requires the Alternative Textures mod — see
    /// <see cref="PoweredFloorAnimator"/>.
    /// </summary>
    public Dictionary<string, string> ConnectorPoweredTextureIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. Maps a whitelist/blacklist sign's qualified item ID to the Alternative Textures
    /// texture ID (in the form <c>{Owner}.{ModelName}</c>, e.g.
    /// <c>luisMint.ATAutomatePowerPipes.Craftable_luisMint.AutomatePowerPipes_WhitelistSign</c>)
    /// providing its two appearance variations: 0 = invalid (not currently enforcing its filter), 1 =
    /// valid. Empty by default; populated for a custom sign that has a matching Alternative Textures
    /// content pack installed. Requires the Alternative Textures mod — see <see cref="SignTextureSync"/>.
    /// </summary>
    public Dictionary<string, string> SignTextureIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

        // MOD: added — guard against a nonsensical animation speed/hold multiplier.
        if (this.PoweredFloorAnimationFps <= 0)
            this.PoweredFloorAnimationFps = 6;
        if (this.PoweredFloorUnpoweredHoldMultiplier < 1)
            this.PoweredFloorUnpoweredHoldMultiplier = 1;

        // MOD: added.
        this.ConnectorPoweredTextureIds = this.ConnectorPoweredTextureIds.ToNonNullCaseInsensitive();
        this.SignTextureIds = this.SignTextureIds.ToNonNullCaseInsensitive();

        this.ChestOverrides = this.ChestOverrides.ToNonNullCaseInsensitive();
        this.ChestOverrides.RemoveWhere(pair => pair.Value is null);

        this.MachineOverrides = this.MachineOverrides.ToNonNullCaseInsensitive();
        this.MachineOverrides.RemoveWhere(pair => pair.Value is null);
    }
}

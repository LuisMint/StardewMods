using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>How Junimo huts should automate gems.</summary>
    /// <remarks>The <see cref="JunimoHutBehavior.AutoDetect"/> option is equivalent to <see cref="JunimoHutBehavior.Ignore"/>.</remarks>
    public JunimoHutBehavior JunimoHutBehaviorForGems { get; set; } = JunimoHutBehavior.AutoDetect;

    /// <summary>How Junimo huts should automate fertilizer items.</summary>
    /// <remarks>The <see cref="JunimoHutBehavior.AutoDetect"/> option is equivalent to <see cref="JunimoHutBehavior.Ignore"/> (if Better Junimos is installed), else <see cref="JunimoHutBehavior.MoveIntoChests"/>.</remarks>
    public JunimoHutBehavior JunimoHutBehaviorForFertilizer { get; set; } = JunimoHutBehavior.AutoDetect;

    /// <summary>How Junimo huts should automate seed items.</summary>
    /// <remarks>The <see cref="JunimoHutBehavior.AutoDetect"/> option is equivalent to <see cref="JunimoHutBehavior.Ignore"/> (if Better Junimos is installed), else <see cref="JunimoHutBehavior.MoveIntoChests"/>.</remarks>
    public JunimoHutBehavior JunimoHutBehaviorForSeeds { get; set; } = JunimoHutBehavior.AutoDetect;

    /// <summary>How Junimo huts should automate specific items.</summary>
    /// <remarks>Each key is a qualified item ID. If an item matches an entry with any value except <see cref="JunimoHutBehavior.AutoDetect"/>, this overrides the by-category fields like <see cref="JunimoHutBehaviorForSeeds"/>.</remarks>
    public Dictionary<string, JunimoHutBehavior> JunimoHutBehaviors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether to collect moss on trees.</summary>
    public bool CollectTreeMoss { get; set; } = true;

    /// <summary>Whether to log a warning if the player installs a custom-machine mod that requires a separate compatibility patch which isn't installed.</summary>
    public bool WarnForMissingBridgeMod { get; set; } = true;

    /// <summary>Whether chests should be automated (true) or ignored (false) by default, unless overridden by <see cref="ChestOverrides"/>.</summary>
    public bool ChestsEnabledByDefault { get; set; } = true;

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

        this.JunimoHutBehaviors = this.JunimoHutBehaviors.ToNonNullCaseInsensitive();

        this.ChestOverrides = this.ChestOverrides.ToNonNullCaseInsensitive();
        this.ChestOverrides.RemoveWhere(pair => pair.Value is null);

        this.MachineOverrides = this.MachineOverrides.ToNonNullCaseInsensitive();
        this.MachineOverrides.RemoveWhere(pair => pair.Value is null);
    }
}

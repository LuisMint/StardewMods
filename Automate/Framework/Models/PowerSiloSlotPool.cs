using System.Collections.Generic;

namespace Pathoschild.Stardew.Automate.Framework.Models;

/// <summary>
/// MOD: added. One "item slot" within a <see cref="PowerSiloTierPool"/> — a Power Silo tier asking for
/// N slots means N independently-tracked required items (same shape as a fixed
/// <see cref="PowerSiloTierConfig.RequiredItems"/> list), except each slot's actual item/count is
/// rolled once per save from <see cref="Options"/> (see <see cref="PowerSiloTierRoller"/>) instead of
/// being fixed in the config.
/// </summary>
internal class PowerSiloSlotPool
{
    /// <summary>The possible options for this slot — exactly one is picked at random per save.</summary>
    public List<PowerSiloItemOption> Options { get; set; } = [];
}

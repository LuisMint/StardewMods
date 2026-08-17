using System.Collections.Generic;

namespace Pathoschild.Stardew.Automate.Framework.Models;

/// <summary>
/// MOD: added. A randomized alternative to a <see cref="PowerSiloTierConfig"/>'s
/// fixed <see cref="PowerSiloTierConfig.RequiredItems"/> — one entry in <see cref="ModConfig.PowerSiloTierPools"/>,
/// index-aligned with <see cref="ModConfig.PowerSiloTiers"/> (index <c>i</c>'s pool, if present, replaces
/// that same index's <see cref="PowerSiloTierConfig.RequiredItems"/> with a rolled result — see
/// <see cref="PowerSiloTierRoller"/>). A tier with no pool entry (or a <c>null</c>/missing one) simply
/// keeps using its original fixed <see cref="PowerSiloTierConfig.RequiredItems"/> unchanged.
/// </summary>
internal class PowerSiloTierPool
{
    /// <summary>This tier's item slots — each rolled independently once per save into one concrete required item.</summary>
    public List<PowerSiloSlotPool> Slots { get; set; } = [];
}

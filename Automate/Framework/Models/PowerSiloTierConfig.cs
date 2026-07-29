using System.Collections.Generic;

namespace Pathoschild.Stardew.Automate.Framework.Models;

/// <summary>
/// MOD: added. One capacity tier for a Power Silo (see <see cref="ModConfig.PowerSiloTiers"/>) — the
/// item(s) a Silo currently at this tier asks for to reach the NEXT tier, and how much total Power
/// Coil capacity this tier itself contributes once reached. The first tier in the list is the Silo's
/// starting state (a freshly-built Silo is seeded at index 0 via its <c>Data/Buildings</c>
/// <c>ModData</c>), so its own <see cref="RequiredItems"/> are what unlock tier 1, not a requirement to
/// "reach" tier 0 itself.
/// </summary>
internal class PowerSiloTierConfig
{
    /// <summary>The item(s) the Silo asks for to advance past this tier, delivered one at a time and tracked independently (see <see cref="PowerSiloInteraction"/>) — or <c>null</c>/empty if this is the last tier (no further upgrade available).</summary>
    public List<PowerSiloRequiredItem>? RequiredItems { get; set; }

    /// <summary>The total Power Coil capacity this Silo contributes once it has reached this tier.</summary>
    public int CapacityGranted { get; set; }

    /// <summary>
    /// MOD: added. Whether a Silo at this tier also contributes the connected-solar-panel bonus (see
    /// <see cref="PowerSiloSystem.GetConnectedSolarPanelCount"/>) on top of <see cref="CapacityGranted"/>
    /// — every 3 connected Solar Panels add 1 more. Multiple Silos reaching a tier with this set each
    /// add their own copy of the same bonus, since it's evaluated per-Silo like everything else here.
    /// </summary>
    public bool GrantsSolarBonus { get; set; }
}

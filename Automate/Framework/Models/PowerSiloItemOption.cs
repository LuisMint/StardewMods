using System.Collections.Generic;

namespace Pathoschild.Stardew.Automate.Framework.Models;

/// <summary>
/// MOD: added. One possible option within a <see cref="PowerSiloSlotPool"/> — a Power Silo tier's
/// actual <see cref="PowerSiloRequiredItem"/> for that slot is rolled once per save (see
/// <see cref="PowerSiloTierRoller"/>) by picking one option from the slot's pool at random, then a
/// random count within that option's own range.
/// </summary>
internal class PowerSiloItemOption
{
    /// <summary>The qualified or unqualified item ID to deliver — mutually exclusive with <see cref="ItemIds"/>, set this for a single fixed item.</summary>
    public string? ItemId { get; set; }

    /// <summary>
    /// MOD: added. A sub-pool of qualified/unqualified item IDs to pick ONE from at random, as a nested
    /// roll within this option — e.g. "any gem" resolving to a random pick among Emerald/Aquamarine/
    /// Ruby/Amethyst/Topaz/Jade. Mutually exclusive with <see cref="ItemId"/>.
    /// </summary>
    public List<string>? ItemIds { get; set; }

    /// <summary>The minimum quantity to roll for this option (inclusive).</summary>
    public int MinCount { get; set; }

    /// <summary>The maximum quantity to roll for this option (inclusive) — same as <see cref="MinCount"/> for a fixed (non-ranged) quantity.</summary>
    public int MaxCount { get; set; }

    /// <summary>MOD: added. For a flavored item (see <see cref="PowerSiloRequiredItem.RequiredPreservedFlavorItemId"/>), the unqualified item ID the delivered item's flavor must match — e.g. <c>"78"</c> (Cave Carrot) for Pickled Cave Carrot. <c>null</c> for an item with no flavor concept.</summary>
    public string? RequiredPreservedFlavorItemId { get; set; }
}

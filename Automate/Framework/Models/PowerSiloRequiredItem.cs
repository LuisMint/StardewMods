namespace Pathoschild.Stardew.Automate.Framework.Models;

/// <summary>
/// MOD: added. One of possibly several items a Power Silo tier asks for (see
/// <see cref="PowerSiloTierConfig.RequiredItems"/>) — delivered one item type at a time (see
/// <see cref="PowerSiloInteraction"/>), with progress toward each tracked independently, so a tier
/// asking for multiple item types doesn't require holding more than one at once to make progress.
/// </summary>
internal class PowerSiloRequiredItem
{
    /// <summary>The qualified or unqualified item ID to deliver.</summary>
    public string ItemId { get; set; } = "";

    /// <summary>How many of <see cref="ItemId"/> must be delivered in total.</summary>
    public int Count { get; set; }
}

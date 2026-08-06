using StardewValley;
using SObject = StardewValley.Object;

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

    /// <summary>
    /// MOD: added. For a flavored item sharing its base <see cref="ItemId"/> with every other flavor of
    /// the same product (e.g. Pickles — Pickled Cave Carrot and Pickled Blueberry are both
    /// <c>(O)342</c>, distinguished only by <see cref="SObject.preservedParentSheetIndex"/>), the
    /// UNQUALIFIED item ID the delivered item's own <see cref="SObject.preservedParentSheetIndex"/> must
    /// match — e.g. <c>"78"</c> (Cave Carrot) so only Pickled Cave Carrot satisfies this requirement,
    /// not a pickled anything-else sharing the same base ID. <c>null</c> for an item with no flavor
    /// concept at all (the overwhelming majority), which skips this check entirely.
    /// </summary>
    public string? RequiredPreservedFlavorItemId { get; set; }

    /// <summary>
    /// MOD: added. Get whether a held item satisfies this requirement — the base ID match every
    /// requirement needs, plus (only when <see cref="RequiredPreservedFlavorItemId"/> is set) an exact
    /// flavor match, so a generic Pickles/Juice of the WRONG flavor doesn't silently count.
    /// </summary>
    /// <param name="held">The item to check.</param>
    public bool Matches(SObject held)
    {
        bool idMatches = held.QualifiedItemId == this.ItemId || held.ItemId == this.ItemId;
        if (!idMatches)
            return false;

        return this.RequiredPreservedFlavorItemId is null
            || held.preservedParentSheetIndex.Value == this.RequiredPreservedFlavorItemId;
    }

    /// <summary>
    /// MOD: added. Get this requirement's display name — for a flavored requirement (see
    /// <see cref="RequiredPreservedFlavorItemId"/>), this is the ACTUAL flavored name (e.g. "Pickled Cave
    /// Carrot"), not the generic base item's own name (e.g. "Pickles"), since <see cref="ItemId"/> alone
    /// can't distinguish them.
    /// </summary>
    public string GetDisplayName()
    {
        if (this.RequiredPreservedFlavorItemId is null)
            return ItemRegistry.GetDataOrErrorItem(this.ItemId).DisplayName;

        SObject flavored = ItemRegistry.Create<SObject>(this.ItemId);
        flavored.preservedParentSheetIndex.Value = this.RequiredPreservedFlavorItemId;
        return flavored.DisplayName;
    }
}

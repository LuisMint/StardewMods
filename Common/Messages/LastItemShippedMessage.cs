namespace Pathoschild.Stardew.Common.Messages;

/// <summary>MOD: added. A network message announcing that an automated shipment (via conduit) just changed the shipping bin's own "last shipped" display — see <c>ModEntry.BroadcastLastItemShipped</c>'s own remarks for why this needs to reach every connected player. Carries just the shipped item's identity, not the item itself; each receiving client resolves that back to the real, matching slot in its own synced view of the shipping bin's inventory.</summary>
internal class LastItemShippedMessage
{
    /// <summary>The shipped item's qualified item ID.</summary>
    public string? QualifiedItemId { get; set; }

    /// <summary>The shipped item's quality.</summary>
    public int Quality { get; set; }
}

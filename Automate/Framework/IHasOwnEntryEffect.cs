namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added, per direct request. Implemented by an <see cref="IContainer"/> that already plays its
/// own "item arrived" feedback independently of <see cref="ContainerVisualEffects"/> — e.g. the shipping
/// bin, which always plays vanilla's own parcel-icon animation and sound via <c>ShippingBin.showShipment</c>
/// regardless of automation. Without this, <see cref="ThrottledContainer"/> would layer its own generic
/// entry effect on top of that every time, looking doubled-up. Only suppresses the ENTRY side — there's
/// no vanilla equivalent for an item LEAVING a container, so the exit effect is unaffected by this.
/// Mirrors <see cref="IHasContainerPriority"/>'s own convention exactly, including its
/// extension-method-with-fallback style (see <see cref="Storage.ContainerExtensions.HasOwnEntryEffect"/>).
/// </summary>
internal interface IHasOwnEntryEffect
{
    /// <summary>Whether this container already shows its own feedback when an item arrives, so the generic entry effect should be skipped.</summary>
    bool HasOwnEntryEffect { get; }
}

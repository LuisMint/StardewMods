using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Implemented by an <see cref="IContainer"/> that's backed by a real
/// <see cref="Chest"/> instance — lets <see cref="ContainerVisualEffects"/> reach the underlying chest
/// (for the lid-open animation and shake jolt) without every container type needing to know about
/// either of those concerns itself. Mirrors <see cref="IHasContainerPriority"/>'s own convention exactly,
/// including its extension-method-with-fallback style (see <see cref="Storage.ContainerExtensions.GetUnderlyingChest"/>).
/// </summary>
internal interface IHasUnderlyingChest
{
    /// <summary>The underlying chest, or <c>null</c> if this container isn't backed by a real chest (e.g. the shipping bin, which wraps a raw inventory instead).</summary>
    Chest? UnderlyingChest { get; }
}

using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Implemented by an <see cref="IMachine"/> that's backed by a real <see cref="SObject"/>
/// instance — lets a caller (e.g. <see cref="MachineGroupFactory"/>, to reach the real object for a
/// reflective Utility Grid Redux power check — see <see cref="UtilityGridReduxSystem"/>) get at the
/// underlying object without the public <see cref="IMachine"/>/<see cref="IAutomatable"/> interfaces
/// needing to expose it generally. Mirrors <see cref="IHasUnderlyingChest"/>'s own convention exactly.
/// Only meaningful for an object-backed machine (<see cref="GenericObjectMachine{TMachine}"/>) — a
/// building- or terrain-feature-backed machine simply doesn't implement this at all, since Utility Grid
/// Redux's own gate only ever operates on a real <see cref="SObject"/>.
/// </summary>
internal interface IHasUnderlyingObject
{
    /// <summary>The underlying object, or <c>null</c> if not applicable.</summary>
    SObject? UnderlyingObject { get; }
}

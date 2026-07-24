namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Marker for a machine that's also its own chest (e.g. <see cref="Machines.Objects.PoweredChestMachine"/>)
/// and therefore doesn't count toward <see cref="MachineGroup.HasInternalAutomation"/> on its own —
/// two such machines (or one alone) can never actually move anything between each other, since a
/// chest-like machine explicitly ignores other chest-like machines to avoid an infinite loop (see
/// <c>PoweredChestMachine.ShouldSkip</c>). A group is only really "active" once it also has at least
/// one machine or container that ISN'T chest-like.
/// </summary>
internal interface IChestLikeMachine
{
}

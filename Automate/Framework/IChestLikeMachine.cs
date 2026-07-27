namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Marker for a machine that's also its own chest AND actively pulls/pushes items through
/// its own piped connectors — currently only <see cref="Machines.Objects.PoweredChestMachine"/>. A
/// chest-backed hybrid (Hopper, Auto-Grabber, Mini-Shipping Bin, Junimo Hut — see
/// <see cref="ChestHybridStorage"/>) does NOT implement this: it's purely passive storage now, with no
/// active movement of its own, so it's just a plain <see cref="IContainer"/> instead. Used by
/// <see cref="MachineGroup.HasLocalInternalAutomation"/> to work out whether a group with only
/// chest-like machines in it is actually able to move anything — a lone chest-like machine (or two
/// Powered Chests only connected to each other, the only kind that mutually excludes its own kind, see
/// <c>PoweredChestMachine.ShouldSkip</c>) never actually moves anything. See that property's own
/// remarks for the precise rule.
/// </summary>
internal interface IChestLikeMachine
{
}

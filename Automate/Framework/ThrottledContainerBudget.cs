using System.Collections.Generic;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. The shared pacing state for one physical container, referenced by every <see cref="ThrottledContainer"/>
/// instance that wraps it. A single physical container (most commonly a Powered Chest reached through an
/// Omni-role connector — see <see cref="MachineGroupFactory"/> step 6's own remarks) can belong to more than one
/// <see cref="IMachineGroup"/> at once, and each group builds its own <see cref="MachineGroupBuilder"/> with its
/// own <see cref="ThrottledContainer"/> wrapper around that same physical container. Without sharing this state
/// by reference, each group's wrapper tracked its own independent window, so a container reachable from N groups
/// effectively got N times the configured budget — confirmed via a diagnostic trace of a Powered Chest shared
/// across 3 sibling groups (via 2 different Mushroom Boxes, one per group) accepting 2 deliveries as its "first
/// action" instead of 1. <see cref="MachineGroupFactory"/> creates exactly one instance per underlying container
/// per rebuild pass (keyed by the raw container's own object identity, before any per-group wrapping) and hands
/// it to every <see cref="MachineGroupBuilder.Add(IContainer, ThrottledContainerBudget)"/> call that reaches the
/// same physical container, so it naturally dies with the rest of that rebuild's state — the same "no
/// cross-rebuild continuity" tradeoff already accepted for the rest of this mod's pacing system.
/// </summary>
internal sealed class ThrottledContainerBudget
{
    /// <summary>The elapsed-milliseconds value (see <see cref="MachineGroupFactory"/>'s <c>getElapsedMs</c>) the current pacing window started at.</summary>
    public double WindowStartMs;

    /// <summary>How many chunks (successful <see cref="ThrottledContainer.Store"/> calls) have been accepted in the current pacing window, across every group sharing this container.</summary>
    public int ActionsUsedThisWindow;

    /// <summary>
    /// The qualified item IDs that have already animated (entry or exit) in the current window — see
    /// <see cref="ThrottledContainer"/>'s own remarks on deduping. Also doubles as each item type's stacking
    /// "slot" — its <see cref="HashSet{T}.Count"/> right after a NEW type is added is that type's 0-based slot
    /// index for <see cref="ContainerVisualEffects"/>'s per-slot vertical offset.
    /// </summary>
    public readonly HashSet<string> AnimatedItemTypesThisWindow = [];
}

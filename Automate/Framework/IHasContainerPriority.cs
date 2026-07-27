namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Implemented by an <see cref="IContainer"/> that should have a default priority other
/// than <see cref="ContainerPriorityTiers.Normal"/> when multiple candidates are available for the same
/// push or pull, WITHOUT the player having explicitly set a preference via the container's own mod data
/// (see <see cref="Storage.ContainerExtensions.StoragePreferred"/>/<see cref="Storage.ContainerExtensions.TakingItemsPreferred"/>
/// — an explicit player preference always takes precedence over this default, see
/// <see cref="StorageManager.SetContainers"/>).
///
/// Exists because a chest-like machine's own storage (<see cref="Machines.Objects.PoweredChestMachine"/>,
/// or a <see cref="ChestHybridStorage"/>-backed hybrid) is registered as a plain <see cref="IContainer"/>
/// alongside real chests, so OTHER machines can use it like one — but it shouldn't silently compete on
/// equal footing for every item by default.
/// </summary>
internal interface IHasContainerPriority
{
    /// <summary>This container's priority tier — see <see cref="ContainerPriorityTiers"/>. Lower values are tried first.</summary>
    int ContainerPriorityTier { get; }
}

/// <summary>
/// MOD: added. The default priority tiers for <see cref="IHasContainerPriority"/> — lower values are
/// tried first for both storing and taking items. Also doubles as the "who defers to whom" ranking
/// between active movers (see <see cref="Storage.ContainerExtensions.ShouldDeferToAsActiveMover"/>): an
/// active mover (currently only <see cref="Machines.Objects.PoweredChestMachine"/> — chest-backed
/// hybrids no longer have any active pull/push logic of their own, they're purely passive storage now)
/// defers to any OTHER active mover of equal or better (lower-numbered) tier, so two active movers
/// reaching each other through the same connector don't both act on the same pair, and two of the SAME
/// tier don't fight each other at all (e.g. two Powered Chests only connected to each other).
/// </summary>
internal static class ContainerPriorityTiers
{
    /// <summary>A Powered Chest (<see cref="Machines.Objects.PoweredChestMachine"/>) — the fork's main active mover, so it gets first refusal for storage by default, ahead of even a real chest.</summary>
    public const int PoweredChest = 0;

    /// <summary>A plain chest (or anything else that doesn't implement <see cref="IHasContainerPriority"/>) — general-purpose storage, tried after a Powered Chest but before a narrower-purpose hybrid.</summary>
    public const int Normal = 1;

    /// <summary>A chest-backed machine/chest hybrid (<see cref="ChestHybridStorage"/>) — exists for a narrower purpose than general storage (e.g. a Hopper primarily feeds the machine below it), so it's tried last by default. Purely passive now — it never initiates its own movement, so it's never itself the one "deferring" as an active mover; it's just a lower-priority target for the Powered Chest (or the standard machine cycle) to reach.</summary>
    public const int ChestHybrid = 2;
}

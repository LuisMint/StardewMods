using StardewValley.Mods;

namespace Pathoschild.Stardew.Automate.Framework.Storage;

/// <summary>Provides extensions for <see cref="IContainer"/> instances.</summary>
internal static class ContainerExtensions
{
    /*********
    ** Public methods
    *********/
    /// <summary>Get whether items can be stored in this container.</summary>
    /// <param name="container">The container instance.</param>
    public static bool StorageAllowed(this IContainer container)
    {
        return container.GetStoragePreference().IsAllowed();
    }

    /// <summary>Get whether this container should be preferred when choosing where to store items.</summary>
    /// <param name="container">The container instance.</param>
    public static bool StoragePreferred(this IContainer container)
    {
        return container.GetStoragePreference().IsPreferred();
    }

    /// <summary>Get the preference for storing items in this container.</summary>
    /// <param name="container">The container instance.</param>
    public static AutomateContainerPreference GetStoragePreference(this IContainer container)
    {
        return container.ModData.ReadPreferenceField(AutomateContainerHelper.StoreItemsKey);
    }

    /// <summary>Get whether items can be retrieved from this container.</summary>
    /// <param name="container">The container instance.</param>
    public static bool TakingItemsAllowed(this IContainer container)
    {
        return container.GetTakingItemsPreference().IsAllowed();
    }

    /// <summary>Get whether this container should be preferred when choosing where to retrieve items.</summary>
    /// <param name="container">The container instance.</param>
    public static bool TakingItemsPreferred(this IContainer container)
    {
        return container.GetTakingItemsPreference().IsPreferred();
    }

    /// <summary>Get the preference for taking items from this container.</summary>
    /// <param name="container">The container instance.</param>
    public static AutomateContainerPreference GetTakingItemsPreference(this IContainer container)
    {
        return container.ModData.ReadPreferenceField(AutomateContainerHelper.TakeItemsKey);
    }

    /// <summary>
    /// MOD: added. Get whether a container was reached through a one-directional "pull" connector
    /// (items may be taken from it, but never stored into it) — as opposed to a bidirectional or
    /// unrestricted connection. Used by <see cref="Machines.Objects.PoweredChestMachine"/> (the fork's
    /// active mover) to avoid reading from a bidirectional connection — doing so would let it
    /// immediately hand its own output straight back to the same source it just pulled from, looping
    /// forever, since (unlike a normal recipe machine) it doesn't transform the item into something
    /// different along the way.
    /// </summary>
    /// <param name="container">The container instance.</param>
    public static bool IsPullOnlyConnection(this IContainer container)
    {
        return container is IConnectionRoleRestriction restriction && restriction.AllowTakingThroughThisConnection && !restriction.AllowStorageThroughThisConnection;
    }

    /// <summary>MOD: added. Get whether a container was reached through a one-directional "push" connector (items may be stored into it, but never taken from it) — the opposite of <see cref="IsPullOnlyConnection"/>, see its own remarks.</summary>
    /// <param name="container">The container instance.</param>
    public static bool IsPushOnlyConnection(this IContainer container)
    {
        return container is IConnectionRoleRestriction restriction && restriction.AllowStorageThroughThisConnection && !restriction.AllowTakingThroughThisConnection;
    }

    /// <summary>
    /// MOD: added. Get whether an active mover (e.g. a Powered Chest) should treat this container as
    /// its OWN pull source — i.e. it's reached via a connector role marked "storable" (an Input Pipe).
    /// The underlying condition is identical to <see cref="IsPushOnlyConnection"/>, but the MEANING is
    /// the opposite: the standard machine cycle treats a storable-role container as a valid PUSH
    /// destination for a machine's own output (see <see cref="StorageManager.TryPush"/>), while an
    /// active mover treats that exact same role as ITS OWN input side instead — it reaches out and
    /// pulls from it. This is what makes "Input Pipe" consistently mean "flows into the automator"
    /// whether the other end is a real machine (which pushes its own output there on its own) or a
    /// plain chest (which the active mover has to reach into itself, since a plain chest never acts on
    /// its own) — see <see cref="IStorage.AllContainers"/>'s own remarks for why this needs a separate
    /// container set from <see cref="IStorage.OutputContainers"/> to actually see these candidates at
    /// all (that array excludes them, since the standard cycle can never take from a storable-only
    /// role).
    /// </summary>
    /// <param name="container">The container instance.</param>
    public static bool IsActiveMoverPullSource(this IContainer container)
    {
        return container.IsPushOnlyConnection();
    }

    /// <summary>
    /// MOD: added. Get whether an active mover (e.g. a Powered Chest) should treat this container as
    /// its OWN push destination — i.e. it's reached via a connector role marked "takeable" (an Output
    /// Pipe). The underlying condition is identical to <see cref="IsPullOnlyConnection"/>, but the
    /// MEANING is the opposite — see <see cref="IsActiveMoverPullSource"/>'s own remarks for the full
    /// explanation (same idea, opposite direction): the standard machine cycle treats a takeable-role
    /// container as a valid PULL source for a machine's own input, while an active mover treats that
    /// same role as ITS OWN output side, pushing into it instead.
    /// </summary>
    /// <param name="container">The container instance.</param>
    public static bool IsActiveMoverPushDestination(this IContainer container)
    {
        return container.IsPullOnlyConnection();
    }

    /// <summary>MOD: added. Get this container's default priority tier (see <see cref="IHasContainerPriority"/>) for choosing between multiple candidates when pushing or pulling items — <see cref="ContainerPriorityTiers.Normal"/> if it doesn't implement <see cref="IHasContainerPriority"/>.</summary>
    /// <param name="container">The container instance.</param>
    public static int GetContainerPriorityTier(this IContainer container)
    {
        return container is IHasContainerPriority p ? p.ContainerPriorityTier : ContainerPriorityTiers.Normal;
    }

    /// <summary>
    /// MOD: added. Get whether a candidate container is ANOTHER active mover (i.e. it implements
    /// <see cref="IHasContainerPriority"/> itself, not just via the fallback default) with a priority
    /// EQUAL TO OR BETTER THAN (lower or equal tier number) <paramref name="ownTier"/>. Used by an
    /// active mover's own pull/push scan so two active movers reaching each other through the same
    /// connector don't both act on the same pair — currently only relevant to
    /// <see cref="Machines.Objects.PoweredChestMachine"/>, the fork's only active mover (chest-backed
    /// hybrids are purely passive storage now, with no scan of their own to defer from), but this stays
    /// correct without changes if a future active-mover machine is ever added:
    /// <list type="bullet">
    /// <item>A strictly BETTER-priority candidate (lower tier number) is deferred to — it's already
    /// handling that connection, so this one doesn't also act.</item>
    /// <item>An EQUAL-priority candidate (e.g. another Powered Chest) is deferred to as well,
    /// symmetrically on both sides — so two active movers of the exact same tier never fight over the
    /// same connection at all.</item>
    /// </list>
    /// A WORSE-priority candidate (higher tier number, e.g. a chest-backed hybrid) is NOT deferred to —
    /// the caller keeps acting on it normally, since it's the higher-priority side here. A plain chest
    /// never matches this at all (it doesn't implement <see cref="IHasContainerPriority"/>), and a
    /// hybrid — despite implementing it, for storage-preference ordering purposes — never has an active
    /// scan of its own to call this in the first place, so in practice neither ever has an action to
    /// "double up" with; they just stay reachable by whichever active mover touches them.
    /// </summary>
    /// <param name="container">The candidate container to check.</param>
    /// <param name="ownTier">The active mover's own priority tier (see <see cref="IHasContainerPriority.ContainerPriorityTier"/>).</param>
    public static bool ShouldDeferToAsActiveMover(this IContainer container, int ownTier)
    {
        return container is IHasContainerPriority p && p.ContainerPriorityTier <= ownTier;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Read a container preference from a mod data dictionary.</summary>
    /// <param name="data">The mod data dictionary to read.</param>
    /// <param name="key">The dictionary key to read.</param>
    private static AutomateContainerPreference ReadPreferenceField(this ModDataDictionary data, string key)
    {
        data.TryGetValue(key, out string rawValue);
        return rawValue switch
        {
            nameof(AutomateContainerPreference.Allow) => AutomateContainerPreference.Allow,
            nameof(AutomateContainerPreference.Prefer) => AutomateContainerPreference.Prefer,
            nameof(AutomateContainerPreference.Disable) => AutomateContainerPreference.Disable,
            _ => AutomateContainerPreference.Allow
        };
    }
}

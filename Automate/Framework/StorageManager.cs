using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Pathoschild.Stardew.Automate.Framework.Storage;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>Manages access to items in the underlying containers.</summary>
internal class StorageManager : IStorage
{
    /*********
    ** Fields
    *********/
    /// <summary>The index of the first container in <see cref="MachineInputContainers"/> which doesn't match <see cref="ContainerExtensions.StoragePreferred"/>.</summary>
    private int FirstNonPreferredInput;

    /// <summary>
    /// MOD: added. Get whether a container's priority-tier category currently allows it to be reached
    /// by the STANDARD machine push/pull cycle (a Furnace's output/input, etc. — see
    /// <see cref="TryPush"/>/<see cref="GetItems"/>) — see <see cref="Models.ModConfig.ChestsCanAutomate"/>/
    /// <see cref="Models.ModConfig.ChestHybridsCanAutomate"/>/<see cref="Models.ModConfig.PoweredChestsCanAutomate"/>.
    /// Deliberately does NOT affect <see cref="AllContainers"/> — a Powered Chest's own active pull/push
    /// through its piped connectors reads that directly (see <see cref="Machines.Objects.PoweredChestMachine.SetInput"/>)
    /// and isn't restricted by another container's category here, only by its own (checked separately,
    /// where that movement happens). So disabling normal chests, say, only stops a Furnace from pushing
    /// into/pulling from one directly — a Powered Chest can still reach into that same chest. Also
    /// exposed publicly via <see cref="IsContainerCategoryEnabled"/> so <see cref="MachineGroup.HasLocalInternalAutomation"/>
    /// can determine whether a chest-like machine's own category currently permits it to automate at
    /// all, using the exact same categorization already applied to <see cref="MachineInputContainers"/>/
    /// <see cref="MachineOutputContainers"/>.
    /// </summary>
    private readonly Func<IContainer, bool> IsCategoryEnabled;

    /// <summary>MOD: added. The subset of <see cref="InputContainers"/> usable by the standard machine push cycle (<see cref="TryPush"/>) — see <see cref="IsCategoryEnabled"/>. Further restricted to Powered Chests only — see the filter in <see cref="SetContainers"/>.</summary>
    private IContainer[] MachineInputContainers;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public IContainer[] InputContainers { get; private set; }

    /// <inheritdoc />
    public IContainer[] OutputContainers { get; private set; }

    /// <inheritdoc />
    public IContainer[] AllContainers { get; private set; }

    /// <summary>
    /// MOD: added. The subset of <see cref="OutputContainers"/> usable by the standard machine pull
    /// cycle — see <see cref="IsCategoryEnabled"/>. Further restricted to Powered Chests only, see the
    /// filter in <see cref="SetContainers"/>. Exposed publicly (not just used internally by
    /// <see cref="GetItems"/>/<see cref="TryGetIngredient(Func{ITrackedStack,bool},int,out IConsumable)"/>)
    /// because <see cref="Machines.DataBasedObjectMachine"/> (the vanilla data-driven machine cycle —
    /// covers most machines, including the Furnace) reads its ingredient sources directly through
    /// <see cref="IContainer"/> iteration instead of going through any of this class's own methods, so
    /// it needs the SAME filtered set those methods use internally or it would silently see (and pull
    /// from) an ineligible container the standard cycle should never reach.
    /// </summary>
    public IContainer[] MachineOutputContainers { get; private set; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="containers">The storage containers.</param>
    /// <param name="isCategoryEnabled">MOD: added. Get whether a container's priority-tier category currently allows it to automate at all, or <c>null</c> to always allow it — see <see cref="IsCategoryEnabled"/>.</param>
    public StorageManager(IEnumerable<IContainer> containers, Func<IContainer, bool>? isCategoryEnabled = null)
    {
        this.IsCategoryEnabled = isCategoryEnabled ?? (_ => true);
        this.SetContainers(containers);
    }

    /// <summary>MOD: added. Get whether a container's priority-tier category currently allows it to automate at all — see <see cref="IsCategoryEnabled"/>'s own remarks.</summary>
    /// <param name="container">The container to check.</param>
    public bool IsContainerCategoryEnabled(IContainer container) => this.IsCategoryEnabled(container);

    /// <summary>Set the containers to use.</summary>
    /// <param name="containers">The storage containers.</param>
    [MemberNotNull(nameof(StorageManager.InputContainers), nameof(StorageManager.OutputContainers), nameof(StorageManager.AllContainers), nameof(StorageManager.MachineInputContainers), nameof(StorageManager.MachineOutputContainers))]
    public void SetContainers(IEnumerable<IContainer> containers)
    {
        ICollection<IContainer> containerCollection = containers as ICollection<IContainer> ?? containers.ToArray();

        // MOD: added — see AllContainers's own remarks for why an active mover needs this instead of
        // InputContainers/OutputContainers.
        this.AllContainers = containerCollection
            .Where(p => p.StorageAllowed() || p.TakingItemsAllowed())
            .ToArray();

        // MOD: added local helpers — a container's own settings (StorageAllowed/TakingItemsAllowed)
        // are always checked first; a per-connection role restriction (if the container was wrapped
        // via RoleRestrictedContainer) can only ADD a further restriction on top, never override a
        // restriction the player explicitly set on the chest itself.
        static bool CanStoreThroughThisConnection(IContainer container) =>
            container is not IConnectionRoleRestriction restriction || restriction.AllowStorageThroughThisConnection;

        static bool CanTakeThroughThisConnection(IContainer container) =>
            container is not IConnectionRoleRestriction restriction || restriction.AllowTakingThroughThisConnection;

        // MOD: added a container-priority-tier tiebreaker (see IHasContainerPriority) between the
        // player's own explicit preference and the Junimo tiebreaker — a real chest is tried before a
        // Powered Chest, which is tried before a chest-backed hybrid (Hopper, Auto-Grabber, etc.),
        // UNLESS the player explicitly marked a container "Prefer" via its own mod data, which always
        // wins regardless of tier. Without this, a hybrid registered as a plain container (so OTHER
        // machines can use it like a chest) would otherwise compete on equal footing with a real chest
        // for every item, purely based on tile-scan order — e.g. a Mini-Shipping Bin "stealing"
        // shippable output that was meant to land in a normal chest, or an Auto-Grabber (which accepts
        // anything) hoovering up everything before a chest ever gets a turn. A hybrid still receives
        // the overflow once earlier tiers are full or reject an item.
        this.InputContainers = containerCollection
            .Where(p => p.StorageAllowed() && CanStoreThroughThisConnection(p))
            .OrderByDescending(p => p.StoragePreferred())
            .ThenBy(p => p.GetContainerPriorityTier())
            .ThenBy(p => p.IsJunimoChest) // push items into Junimo chests last
            .ToArray();

        this.OutputContainers = containerCollection
            .Where(p => p.TakingItemsAllowed() && CanTakeThroughThisConnection(p))
            .OrderByDescending(p => p.TakingItemsPreferred())
            .ThenBy(p => p.GetContainerPriorityTier())
            .ThenByDescending(p => p.IsJunimoChest) // take items from Junimo chests first
            .ToArray();

        // MOD: added — narrower subsets used ONLY by the standard machine push/pull cycle below
        // (TryPush/GetItems) — see IsCategoryEnabled's own remarks for why this is separate from
        // InputContainers/OutputContainers themselves.
        //
        // MOD: added a second filter — the standard cycle may only ever target a Powered Chest, never
        // a plain container or chest-backed hybrid, regardless of which conduit (Input, Output, or
        // Omni) connects them. A container and a real machine only ever end up sharing a group by both
        // touching the same connector network (see MachineGroupFactory's own remarks on how groups are
        // built) — nothing about that shared membership implies they're allowed to interact directly,
        // and they never should: a plain container may only ever be reached by
        // a Powered Chest's own active pull/push (which reads AllContainers directly, unaffected by
        // this — see AllContainers's own remarks). Uses GetContainerPriorityTier() rather than a type
        // check so it still works correctly through a RoleRestrictedContainer wrapper, which forwards
        // the tier from its real underlying container.
        static bool IsPoweredChestTier(IContainer container) => container.GetContainerPriorityTier() == ContainerPriorityTiers.PoweredChest;

        this.MachineInputContainers = this.InputContainers.Where(this.IsCategoryEnabled).Where(IsPoweredChestTier).ToArray();
        this.MachineOutputContainers = this.OutputContainers.Where(this.IsCategoryEnabled).Where(IsPoweredChestTier).ToArray();

        this.FirstNonPreferredInput = this.MachineInputContainers.Length;
        for (int i = 0; i < this.MachineInputContainers.Length; i++)
        {
            if (!this.MachineInputContainers[i].StoragePreferred())
            {
                this.FirstNonPreferredInput = i;
                break;
            }
        }
    }


    /****
    ** GetItems
    ****/
    /// <inheritdoc />
    public bool HasLockedContainers()
    {
        // MOD: checks the full (unfiltered) container sets, not just the machine-usable subsets — a
        // locked chest (e.g. the player has it open) should still pause automation touching it to
        // avoid update collisions, regardless of whether the standard machine cycle can currently
        // reach it; a Powered Chest reading it directly is affected by the same risk either way.
        foreach (IContainer container in this.InputContainers)
        {
            if (container.IsLocked)
                return true;
        }

        foreach (IContainer container in this.OutputContainers)
        {
            if (container.IsLocked)
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public IEnumerable<ITrackedStack> GetItems()
    {
        foreach (IContainer container in this.MachineOutputContainers)
        {
            foreach (ITrackedStack stack in container)
            {
                if (stack.Count > 0)
                    yield return stack;
            }
        }
    }

    /****
    ** TryGetIngredient
    ****/
    /// <inheritdoc />
    public bool TryGetIngredient(Func<ITrackedStack, bool> predicate, int count, [NotNullWhen(true)] out IConsumable? consumable)
    {
        StackAccumulator stacks = new StackAccumulator();
        foreach (ITrackedStack input in this.GetItems().Where(predicate))
        {
            TrackedItemCollection stack = stacks.Add(input);
            if (stack.Count >= count)
            {
                consumable = new Consumable(stack, count);
                return consumable.IsMet;
            }
        }

        consumable = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetIngredient(IRecipe[] recipes, [NotNullWhen(true)] out IConsumable? consumable, [NotNullWhen(true)] out IRecipe? recipe)
    {
        Dictionary<IRecipe, StackAccumulator> accumulator = recipes.ToDictionary(req => req, _ => new StackAccumulator());

        foreach (ITrackedStack input in this.GetItems())
        {
            foreach (var entry in accumulator)
            {
                recipe = entry.Key;
                StackAccumulator stacks = entry.Value;

                if (recipe.AcceptsInput(input))
                {
                    ITrackedStack stack = stacks.Add(input);
                    if (stack.Count >= recipe.InputCount)
                    {
                        consumable = new Consumable(stack, entry.Key.InputCount);
                        return true;
                    }
                }
            }
        }

        consumable = null;
        recipe = null;
        return false;
    }

    /****
    ** TryConsume
    ****/
    /// <inheritdoc />
    public bool TryConsume(Func<ITrackedStack, bool> predicate, int count)
    {
        if (this.TryGetIngredient(predicate, count, out IConsumable? requirement))
        {
            requirement.Reduce();
            return true;
        }
        return false;
    }

    /// <summary>
    /// MOD: added. Whether the MOST RECENT <see cref="TryPush"/> call failed for a reason that's purely
    /// rotational — every candidate container it tried rejected the item specifically because ITS OWN
    /// shared budget window was already spent this round (see <see cref="ThrottledContainer.WasLastStoreRejectedForBudget"/>),
    /// not because the item genuinely doesn't fit anywhere. <c>false</c> whenever the push succeeded, or
    /// when at least one tried container rejected it for any OTHER reason (wrong type, no space) — in
    /// that mixed case a caller can't tell which rejection actually mattered, so this deliberately stays
    /// conservative and reports "not purely budget" rather than risk masking a real incompatibility. See
    /// <see cref="MachineGroup.WasLastPushRejectedForBudget"/> for why this distinction exists at all.
    /// </summary>
    internal bool WasLastPushRejectedForBudget { get; private set; }

    /****
    ** TryPush
    ****/
    /// <inheritdoc />
    public bool TryPush(ITrackedStack? item)
    {
        if (item is not { Count: > 0 })
        {
            this.WasLastPushRejectedForBudget = false;
            return false;
        }

        bool anyBudgetRejection = false;
        bool anyNonBudgetAttempt = false;

        // try chests marked "put items in this chest first"
        int fallbackStartAt = this.FirstNonPreferredInput;
        bool pushedToPreferred = false;
        if (fallbackStartAt > 0 && this.TryPushImpl(item, startAt: 0, endBefore: fallbackStartAt, anyBudgetRejection: ref anyBudgetRejection, anyNonBudgetAttempt: ref anyNonBudgetAttempt))
        {
            pushedToPreferred = true;
            if (item.Count < 1)
            {
                this.WasLastPushRejectedForBudget = false;
                return true;
            }
        }

        // try remaining chests
        bool pushedRemaining = this.TryPushImpl(item, startAt: fallbackStartAt, endBefore: null, anyBudgetRejection: ref anyBudgetRejection, anyNonBudgetAttempt: ref anyNonBudgetAttempt);
        bool result = pushedRemaining || pushedToPreferred;

        this.WasLastPushRejectedForBudget = !result && anyBudgetRejection && !anyNonBudgetAttempt;

        return result;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Add item to a container within the given index range without checking the storage-preferred flag.</summary>
    /// <param name="item">The item stack to push.</param>
    /// <param name="startAt">The index in <see cref="MachineInputContainers"/> at which to start pushing (inclusive).</param>
    /// <param name="endBefore">The index in <see cref="MachineInputContainers"/> at which to stop pushing (exclusive), or <c>null</c> to continue to the end of the array.</param>
    /// <param name="anyBudgetRejection">MOD: added. Set to <c>true</c> if any tried container rejected the item specifically due to its own shared budget being spent this window.</param>
    /// <param name="anyNonBudgetAttempt">MOD: added. Set to <c>true</c> if any tried container was actually offered the item (via <see cref="IContainer.Store"/>) and didn't fully accept it for a reason OTHER than budget.</param>
    /// <returns>Returns whether at least some of the item stack was received.</returns>
    private bool TryPushImpl(ITrackedStack item, int startAt, int? endBefore, ref bool anyBudgetRejection, ref bool anyNonBudgetAttempt)
    {
        int originalCount = item.Count;
        IContainer[] containers = this.MachineInputContainers;

        endBefore ??= containers.Length;
        if (startAt >= endBefore)
            return false;

        // add to chests which contain the item
        string qualifiedItemId = item.Sample.QualifiedItemId;
        int fallbackStartAt = startAt;
        for (int i = startAt; i < endBefore; i++)
        {
            IContainer container = containers[i];
            if (!container.Inventory.ContainsId(qualifiedItemId))
                continue;

            int beforeCount = item.Count;
            container.Store(item);
            if (item.Count < 1)
                return true;

            // MOD: added — see this method's own <paramref name="anyBudgetRejection"/>/<paramref name="anyNonBudgetAttempt"/> remarks.
            if (item.Count == beforeCount && container is ThrottledContainer { WasLastStoreRejectedForBudget: true })
                anyBudgetRejection = true;
            else
                anyNonBudgetAttempt = true;

            if (i == fallbackStartAt)
                fallbackStartAt++; // we can skip this one too since we just checked it
        }

        // else any chests with enough space
        for (int i = fallbackStartAt; i < endBefore; i++)
        {
            IContainer container = containers[i];

            int beforeCount = item.Count;
            container.Store(item);
            if (item.Count < 1)
                return true;

            // MOD: added — see this method's own <paramref name="anyBudgetRejection"/>/<paramref name="anyNonBudgetAttempt"/> remarks.
            if (item.Count == beforeCount && container is ThrottledContainer { WasLastStoreRejectedForBudget: true })
                anyBudgetRejection = true;
            else
                anyNonBudgetAttempt = true;
        }

        return item.Count < originalCount;
    }
}

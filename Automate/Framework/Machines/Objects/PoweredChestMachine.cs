using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Objects;

/// <summary>
/// MOD: added. A custom chest that's also its own machine: it behaves like a normal chest for other
/// machines in its group (delegating every <see cref="IContainer"/> member to its own real
/// <see cref="ChestContainer"/>), but additionally pulls from/pushes into NEIGHBORING plain chests
/// reached through a one-directional connector — something no plain chest can do on its own, since
/// only machines actively move items in Automate. See the mod's own remarks on the group's
/// <c>ConnectorRole</c>/<see cref="IConnectionRoleRestriction"/> system for why a one-directional
/// connector (vs. a bidirectional Pull&amp;Push one) is what makes this safe from an infinite loop.
/// </summary>
internal class PoweredChestMachine : BaseMachine, IContainer, IChestLikeMachine
{
    /*********
    ** Fields
    *********/
    /// <summary>The qualified item ID of this custom chest.</summary>
    public const string QualifiedItemId = "(BC)luisMint.AutomatePowerPipes_PoweredChest";

    /// <summary>The chest's own real storage container.</summary>
    private readonly IContainer OwnContainer;


    /*********
    ** Accessors (IContainer, delegated to the real chest container)
    *********/
    /// <inheritdoc />
    public string TypeId => this.OwnContainer.TypeId;

    /// <inheritdoc />
    public string Name => this.OwnContainer.Name;

    /// <inheritdoc />
    public ModDataDictionary ModData => this.OwnContainer.ModData;

    /// <inheritdoc />
    public bool IsJunimoChest => this.OwnContainer.IsJunimoChest;

    /// <inheritdoc />
    public bool IsLocked => this.OwnContainer.IsLocked;

    /// <inheritdoc />
    public object InventoryReferenceId => this.OwnContainer.InventoryReferenceId;

    /// <inheritdoc />
    public IInventory Inventory => this.OwnContainer.Inventory;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="chest">The underlying chest.</param>
    /// <param name="location">The location which contains the machine.</param>
    /// <param name="tile">The tile covered by the machine.</param>
    public PoweredChestMachine(Chest chest, GameLocation location, Vector2 tile)
        : base(location, BaseMachine.GetTileAreaFor(tile), BaseMachine.GetDefaultMachineId<PoweredChestMachine>())
    {
        this.OwnContainer = new ChestContainer(chest, location, tile, migrateLegacyOptions: false);
    }

    /// <summary>
    /// MOD: added. Always reports empty, so <see cref="SetInput"/> runs every automation tick — all
    /// the actual movement (both directions) happens there instead of the usual
    /// <see cref="GetOutput"/>/<c>storage.TryPush</c> cycle, since that cycle has no way to exclude
    /// specific destinations (like itself, or another Powered Chest) from receiving output.
    /// </summary>
    /// <inheritdoc />
    public override MachineState GetState() => MachineState.Empty;

    /// <summary>MOD: added. Always empty — see <see cref="GetState"/>'s remarks for why all movement happens in <see cref="SetInput"/> instead.</summary>
    /// <inheritdoc />
    public override ITrackedStack? GetOutput() => null;

    /// <summary>
    /// MOD: added. Pull from one-directional "pull"-role containers into this chest, then push this
    /// chest's own items into one-directional "push"-role containers — skipping itself and any other
    /// Powered Chest, and skipping any container reached through a bidirectional (or unrestricted)
    /// connector entirely, to avoid an infinite loop between two chests that both actively move items.
    /// </summary>
    /// <inheritdoc />
    public override bool SetInput(IStorage input)
    {
        bool moved = false;

        // MOD: fixed — read from and store into whichever instance of THIS chest appears in
        // input.InputContainers or input.OutputContainers (its sign-filtered ItemFilteredContainer
        // wrapper, if this group has a whitelist/blacklist sign), not directly through the raw
        // OwnContainer. Going straight to OwnContainer bypassed the sign's numeric condition entirely,
        // since it's enforced by ItemFilteredContainer.Store/GetEnumerator, not by anything on the
        // underlying ChestContainer itself: a whitelist of 10 would otherwise keep pulling 10 more
        // every tick forever instead of stopping once the chest already has 10, and a blacklist
        // reserve of 10 would push this chest's ENTIRE stock out instead of only the excess above 10.
        //
        // Both arrays need checking (not just InputContainers) because the connector role restriction
        // wrapping this chest is a property of the whole connected network, applied uniformly to EVERY
        // container touching it — including this one. A one-directional connector's role marks every
        // container it touches as (from Automate's array-membership perspective) restricted in ONE
        // direction through that connection, which correctly restricts the neighboring chest on the
        // other end, but ALSO applies that same restriction to this chest's own entry — so depending on
        // the connector's role, this chest's wrapped self shows up in only ONE of the two arrays, never
        // both. That role restriction doesn't actually block Store()/GetEnumerator() themselves
        // (RoleRestrictedContainer delegates unconditionally); it only controls which array an entry
        // lands in, so looking in both finds the correctly sign-filtered instance either way.
        IContainer selfContainer =
            Array.Find(input.InputContainers, c => c.InventoryReferenceId.Equals(this.OwnContainer.InventoryReferenceId))
            ?? Array.Find(input.OutputContainers, c => c.InventoryReferenceId.Equals(this.OwnContainer.InventoryReferenceId))
            ?? this.OwnContainer;

        foreach (IContainer container in input.OutputContainers)
        {
            if (this.ShouldSkip(container) || !PoweredChestMachine.IsPullOnly(container) || !this.IsOwnLocalTouchpoint(container))
                continue;

            foreach (ITrackedStack stack in container.ToArray())
            {
                if (stack.Count <= 0)
                    continue;

                int before = stack.Count;
                selfContainer.Store(stack);
                if (stack.Count < before)
                    moved = true;
            }
        }

        foreach (IContainer container in input.InputContainers)
        {
            if (this.ShouldSkip(container) || !PoweredChestMachine.IsPushOnly(container) || !this.IsOwnLocalTouchpoint(container))
                continue;

            foreach (ITrackedStack stack in selfContainer.ToArray())
            {
                if (stack.Count <= 0)
                    continue;

                int before = stack.Count;
                container.Store(stack);
                if (stack.Count < before)
                    moved = true;
            }
        }

        return moved;
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count) => this.OwnContainer.Get(predicate, count);

    /// <inheritdoc />
    public void Store(ITrackedStack stack) => this.OwnContainer.Store(stack);

    /// <inheritdoc />
    public int GetFilled() => this.OwnContainer.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.OwnContainer.GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator() => this.OwnContainer.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PoweredChestMachine other ? this.OwnContainer.Equals(other.OwnContainer) : this.OwnContainer.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.OwnContainer.GetHashCode();


    /*********
    ** Private methods
    *********/
    /// <summary>Get whether a container should be excluded from this chest's own pull/push cycle — either because it's this same chest, or another Powered Chest (to avoid an infinite loop between two actively-moving chests).</summary>
    /// <param name="container">The container to check.</param>
    private bool ShouldSkip(IContainer container)
    {
        return
            container.InventoryReferenceId.Equals(this.OwnContainer.InventoryReferenceId)
            || container.TypeId == PoweredChestMachine.QualifiedItemId;
    }

    /// <summary>Get whether a container was reached through a one-directional "pull" connector (items may be taken from it, but never stored into it) — as opposed to a bidirectional or unrestricted connection, which this chest should never actively move items through.</summary>
    /// <param name="container">The container to check.</param>
    private static bool IsPullOnly(IContainer container) =>
        container is IConnectionRoleRestriction restriction && restriction.AllowTakingThroughThisConnection && !restriction.AllowStorageThroughThisConnection;

    /// <summary>Get whether a container was reached through a one-directional "push" connector (items may be stored into it, but never taken from it) — as opposed to a bidirectional or unrestricted connection, which this chest should never actively move items through.</summary>
    /// <param name="container">The container to check.</param>
    private static bool IsPushOnly(IContainer container) =>
        container is IConnectionRoleRestriction restriction && restriction.AllowStorageThroughThisConnection && !restriction.AllowTakingThroughThisConnection;

    /// <summary>
    /// MOD: added. Get whether a candidate container's role restriction actually belongs to THIS
    /// chest's own local connection, as opposed to an unrelated group's connection to the same
    /// shared Junimo inventory. Every Junimo chest on the farm shares one real inventory, and several
    /// differently-restricted local touchpoints to it can coexist in the farm-wide Junimo aggregate
    /// (see <see cref="JunimoTouchpointContainer"/>'s own remarks) — without this check, a Powered
    /// Chest connected to one Junimo chest via an unrestricted Pull&amp;Push pipe could "borrow" a
    /// pull-only or push-only role that actually belongs to a completely different Powered Chest's
    /// own connection to a different Junimo chest sharing the same inventory. Non-Junimo containers
    /// always return true — the ambiguity only exists for the shared Junimo inventory, since a
    /// regular chest's role restriction is never shared across unrelated groups in the first place.
    /// </summary>
    /// <param name="container">The container to check.</param>
    private bool IsOwnLocalTouchpoint(IContainer container)
    {
        return
            container is not JunimoTouchpointContainer touchpoint
            || this.TileArea.GetTiles().Any(touchpoint.OriginGroupTiles.Contains);
    }
}

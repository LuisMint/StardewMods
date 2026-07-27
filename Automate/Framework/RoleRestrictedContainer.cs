using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Marker interface for containers that have been restricted to a specific role for ONE
/// particular connection/machine group — layered ON TOP of the container's own real, persistent
/// settings. This can only ADD a restriction; it never removes one the player explicitly set on the
/// chest itself (see how it's consumed in <see cref="StorageManager.SetContainers"/>).
/// </summary>
internal interface IConnectionRoleRestriction
{
    /// <summary>Whether items may be stored into the container through this specific connection.</summary>
    bool AllowStorageThroughThisConnection { get; }

    /// <summary>Whether items may be taken from the container through this specific connection.</summary>
    bool AllowTakingThroughThisConnection { get; }
}

/// <summary>
/// MOD: added. Wraps a container to restrict it to a specific role (input-only or output-only) for
/// ONE particular machine group, without altering the container's own real, persistent settings. The
/// same physical chest can be wrapped differently for different groups it belongs to — e.g. acting
/// as input-only through one path network and output-only through a separate one.
/// </summary>
internal class RoleRestrictedContainer : IContainer, IConnectionRoleRestriction, IHasContainerPriority
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying container being wrapped.</summary>
    private readonly IContainer Inner;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public bool AllowStorageThroughThisConnection { get; }

    /// <inheritdoc />
    public bool AllowTakingThroughThisConnection { get; }

    /// <summary>
    /// MOD: added. Forwards to the wrapped container's own priority tier (see <see cref="IHasContainerPriority"/>),
    /// defaulting to <see cref="ContainerPriorityTiers.Normal"/> if it doesn't have one — without this,
    /// wrapping a prioritized container (e.g. a chest-backed hybrid) in this class would silently lose
    /// its tier, since <see cref="StorageManager.SetContainers"/> checks for <see cref="IHasContainerPriority"/>
    /// via a type test on the OUTERMOST container only.
    /// </summary>
    /// <inheritdoc />
    public int ContainerPriorityTier => this.Inner.GetContainerPriorityTier();

    /// <inheritdoc cref="IAutomatable.Location" />
    public GameLocation Location => this.Inner.Location;

    /// <inheritdoc cref="IAutomatable.TileArea" />
    public Rectangle TileArea => this.Inner.TileArea;

    /// <inheritdoc />
    public string TypeId => this.Inner.TypeId;

    /// <inheritdoc />
    public string Name => this.Inner.Name;

    /// <inheritdoc />
    public ModDataDictionary ModData => this.Inner.ModData;

    /// <inheritdoc />
    public bool IsJunimoChest => this.Inner.IsJunimoChest;

    /// <inheritdoc />
    public bool IsLocked => this.Inner.IsLocked;

    /// <inheritdoc />
    public object InventoryReferenceId => this.Inner.InventoryReferenceId;

    /// <inheritdoc />
    public IInventory Inventory => this.Inner.Inventory;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The underlying container being wrapped.</param>
    /// <param name="role">The role to restrict this specific connection to.</param>
    public RoleRestrictedContainer(IContainer inner, ConnectorRole role)
    {
        this.Inner = inner;
        this.AllowStorageThroughThisConnection = role != ConnectorRole.ChestInputOnly;  // input-only chests never receive items
        this.AllowTakingThroughThisConnection = role != ConnectorRole.ChestOutputOnly;  // output-only chests are never drained
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count) => this.Inner.Get(predicate, count);

    /// <inheritdoc />
    public void Store(ITrackedStack stack) => this.Inner.Store(stack);

    /// <inheritdoc />
    public int GetFilled() => this.Inner.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.Inner.GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator() => this.Inner.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RoleRestrictedContainer other ? this.Inner.Equals(other.Inner) : this.Inner.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Inner.GetHashCode();
}

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps a Junimo chest touchpoint container (already possibly wrapped in
/// <see cref="RoleRestrictedContainer"/>/<see cref="ItemFilteredContainer"/> by its own local
/// group) to additionally carry which local sub-group's tiles it came from, before being merged
/// into the farm-wide <see cref="JunimoMachineGroup"/> aggregate.
///
/// Every Junimo chest on the farm shares ONE real inventory, but each physical chest may be reached
/// through a DIFFERENT local connector role (see <see cref="JunimoMachineGroup"/>'s own remarks on
/// its container dedup for why plain identity-based dedup broke that). Once several
/// differently-restricted touchpoints for the SAME shared inventory all coexist in the aggregate,
/// though, a Powered Chest anywhere in that aggregate could otherwise "borrow" a role that doesn't
/// actually belong to ITS OWN local connection — e.g. a Powered Chest connected to Junimo Chest A
/// via an unrestricted Pull&amp;Push pipe (which should never actively pull/push) could mistakenly
/// start actively pulling anyway, just because a completely unrelated Powered Chest connected to
/// Junimo Chest B (same shared inventory) via a pull-only pipe also has an entry in the same
/// aggregate.
///
/// <see cref="OriginGroupTiles"/> lets <see cref="Machines.Objects.PoweredChestMachine"/> check "is
/// MY OWN tile actually part of the local group that produced THIS SPECIFIC entry" before treating
/// it as an active pull/push candidate, so each local touchpoint's role only ever applies to the
/// Powered Chest that's actually attached to it — while every entry still reads/writes the exact
/// same real inventory, so normal machines' storage/ingredient lookups (which don't care about
/// origin) are unaffected.
/// </summary>
internal class JunimoTouchpointContainer : IContainer, IConnectionRoleRestriction, IHasUnderlyingChest, IHasAttemptAutoLoad, IHasOwnEntryEffect
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying container being wrapped.</summary>
    private readonly IContainer Inner;


    /*********
    ** Accessors
    *********/
    /// <summary>The tiles covered by the local sub-group (before farm-wide Junimo merging) that this entry came from.</summary>
    public IReadOnlySet<Vector2> OriginGroupTiles { get; }

    /// <inheritdoc />
    public bool AllowStorageThroughThisConnection => this.Inner is not IConnectionRoleRestriction restriction || restriction.AllowStorageThroughThisConnection;

    /// <inheritdoc />
    public bool AllowTakingThroughThisConnection => this.Inner is not IConnectionRoleRestriction restriction || restriction.AllowTakingThroughThisConnection;

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

    /// <inheritdoc />
    public Chest? UnderlyingChest => this.Inner.GetUnderlyingChest();

    /// <inheritdoc />
    public bool HasOwnEntryEffect => this.Inner.GetHasOwnEntryEffect();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The underlying container being wrapped.</param>
    /// <param name="originGroupTiles">The tiles covered by the local sub-group (before farm-wide Junimo merging) that this entry came from.</param>
    public JunimoTouchpointContainer(IContainer inner, IReadOnlySet<Vector2> originGroupTiles)
    {
        this.Inner = inner;
        this.OriginGroupTiles = originGroupTiles;
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count) => this.Inner.Get(predicate, count);

    /// <inheritdoc />
    public void Store(ITrackedStack stack) => this.Inner.Store(stack);

    /// <inheritdoc />
    public bool AttemptAutoLoad(SObject machine, Farmer who) => this.Inner is IHasAttemptAutoLoad inner ? inner.AttemptAutoLoad(machine, who) : machine.AttemptAutoLoad(this.Inner.Inventory, who);

    /// <inheritdoc />
    public int GetFilled() => this.Inner.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.Inner.GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator() => this.Inner.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JunimoTouchpointContainer other ? this.Inner.Equals(other.Inner) : this.Inner.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Inner.GetHashCode();
}

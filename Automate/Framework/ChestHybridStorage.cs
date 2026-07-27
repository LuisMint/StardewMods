using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Shared engine for a "chest-backed machine/chest hybrid" — a machine whose entire storage
/// role is just being usable as plain <see cref="IContainer"/> storage by other machines in its group
/// (e.g. Auto-Grabber, Hopper, Mini-Shipping Bin, Junimo Hut — but NOT Powered Chest, which is the
/// fork's one deliberate ACTIVE mover and keeps its own separate implementation for that reason, plus
/// its own extra concerns like Junimo-shared-inventory awareness).
///
/// MOD: changed. By request, a hybrid no longer has any active pull/push logic of its own — it never
/// initiates movement through its own piped connectors; it's purely a passive container now, same as a
/// normal chest, just with its own identity/filter (e.g. Mini-Shipping Bin's shippable-only
/// restriction). Only the Powered Chest (and any future dedicated active-mover machine built the same
/// way, e.g. a possible "Powered Shipping Bin") actively reaches into it. This is why a hybrid using
/// this is a plain <see cref="IContainer"/>, not also an <see cref="IMachine"/> — there's no per-tick
/// action for it to take anymore.
///
/// Used by composition rather than inheritance, since C# doesn't support mixins and different hybrids
/// may still want their own thin wrapper type (e.g. for an identity override — see
/// <see cref="Machines.Objects.AutoGrabberMachine"/>'s own <c>TypeId</c>). This holds the parts that
/// would otherwise be duplicated identically across every one of them: a lazily-built container, and
/// (since this itself implements <see cref="IContainer"/>) the delegation members a hybrid needs to
/// forward.
///
/// <para>To add a new chest-backed hybrid: give it a private <see cref="ChestHybridStorage"/> field
/// constructed with a way to find its own chest, and delegate every <see cref="IContainer"/> member to
/// it (see any of the classes mentioned above for the exact boilerplate). That's the whole
/// integration — no <see cref="IMachine"/> involvement needed at all.</para>
/// </summary>
internal class ChestHybridStorage : IContainer, IHasContainerPriority
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the tile position to report for the wrapped container.</summary>
    private readonly Func<Vector2> GetTile;

    /// <summary>Get the underlying chest to wrap, if currently available.</summary>
    private readonly Func<Chest?> GetChest;

    /// <summary>Get whether an item may be pulled/stored into the chest — see remarks on <see cref="Store"/>.</summary>
    private readonly Func<Item, bool> CanAccept;

    /// <summary>Get whether an item currently in the chest may be offered to another container — see remarks on <see cref="Store"/>.</summary>
    private readonly Func<Item, bool> CanProvide;

    /// <summary>MOD: added. Called after an item is actually stored via <see cref="Store"/> — e.g. so <see cref="Machines.Objects.AutoGrabberMachine"/> can keep its "has contents" glow in sync immediately, the same as it did back when it moved items through its own active <c>SetInput</c>.</summary>
    private readonly Action OnStored;

    /// <summary>The wrapped container, if built yet.</summary>
    private IContainer? Container;


    /*********
    ** Accessors (IContainer, delegated to the built container — see <see cref="GetContainer"/>)
    *********/
    /// <inheritdoc />
    public GameLocation Location { get; }

    /// <inheritdoc />
    public Rectangle TileArea => new((int)this.GetTile().X, (int)this.GetTile().Y, 1, 1);

    /// <inheritdoc />
    public string TypeId => this.GetContainer().TypeId;

    /// <inheritdoc />
    public string Name => this.GetContainer().Name;

    /// <inheritdoc />
    public ModDataDictionary ModData => this.GetContainer().ModData;

    /// <inheritdoc />
    public bool IsJunimoChest => this.GetContainer().IsJunimoChest;

    /// <inheritdoc />
    public bool IsLocked => this.GetContainer().IsLocked;

    /// <inheritdoc />
    public object InventoryReferenceId => this.GetContainer().InventoryReferenceId;

    /// <inheritdoc />
    public IInventory Inventory => this.GetContainer().Inventory;

    /// <summary>MOD: added. This hybrid's priority tier — see <see cref="IHasContainerPriority"/>'s own remarks. Used only for storage-preference ordering now (which container a machine tries first) — there's no more "who acts" contest to resolve, since a hybrid never acts on its own.</summary>
    /// <inheritdoc />
    public int ContainerPriorityTier => ContainerPriorityTiers.ChestHybrid;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="location">The location containing the underlying chest.</param>
    /// <param name="getTile">Get the tile position to report for the wrapped container.</param>
    /// <param name="getChest">Get the underlying chest to wrap, if currently available.</param>
    /// <param name="canAccept">Get whether an item may be stored into the chest, or <c>null</c> to accept anything.</param>
    /// <param name="canProvide">Get whether an item currently in the chest may be offered to another container, or <c>null</c> to offer anything.</param>
    /// <param name="onStored">Called after an item is actually stored via <see cref="Store"/>, or <c>null</c> to not hook it — see <see cref="OnStored"/>.</param>
    public ChestHybridStorage(GameLocation location, Func<Vector2> getTile, Func<Chest?> getChest, Func<Item, bool>? canAccept = null, Func<Item, bool>? canProvide = null, Action? onStored = null)
    {
        this.Location = location;
        this.GetTile = getTile;
        this.GetChest = getChest;
        this.CanAccept = canAccept ?? (_ => true);
        this.CanProvide = canProvide ?? (_ => true);
        this.OnStored = onStored ?? (() => { });
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count)
    {
        return this.GetContainer().Get(item => predicate(item) && this.CanProvide(item), count);
    }

    /// <inheritdoc />
    public void Store(ITrackedStack stack)
    {
        if (!this.CanAccept(stack.Sample))
            return;

        int before = stack.Count;
        this.GetContainer().Store(stack);
        if (stack.Count < before)
            this.OnStored();
    }

    /// <inheritdoc />
    public int GetFilled() => this.GetContainer().GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.GetContainer().GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator()
    {
        foreach (ITrackedStack stack in this.GetContainer())
        {
            if (this.CanProvide(stack.Sample))
                yield return stack;
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is ChestHybridStorage other
            ? this.GetContainer().Equals(other.GetContainer())
            : this.GetContainer().Equals(obj);
    }

    /// <inheritdoc />
    public override int GetHashCode() => this.GetContainer().GetHashCode();


    /*********
    ** Private methods
    *********/
    /// <summary>Get the wrapped container, building it (from the underlying chest) the first time it's needed.</summary>
    private IContainer GetContainer()
    {
        if (this.Container == null)
        {
            Chest chest = this.GetChest() ?? throw new InvalidOperationException("Tried to access a chest-hybrid's storage, but its underlying chest is currently unavailable.");
            this.Container = new ChestContainer(chest, this.Location, this.GetTile(), migrateLegacyOptions: false);
        }

        return this.Container;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Inventories;
using StardewValley.Mods;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Buildings;

/// <summary>A Junimo hut's output chest, usable as plain storage by other machines in its group.</summary>
/// <remarks>
/// MOD: changed. Now chest-backed like the other hybrids in this fork (Auto-Grabber, Hopper,
/// Mini-Shipping Bin) instead of using its own bespoke per-item-type config (previously
/// <c>ModConfig.JunimoHutBehaviorForGems</c>/<c>ForFertilizer</c>/<c>ForSeeds</c>/<c>JunimoHutBehaviors</c>,
/// all removed) — what moves in and out is now controlled entirely by how the hut is physically piped
/// and whitelist/blacklist signs, the same as every other hybrid, rather than a separate config system.
///
/// MOD: changed. Purely passive now — no active pull/push logic of its own, so it's a plain
/// <see cref="IContainer"/>, not also an <see cref="IMachine"/>. See <see cref="AutomationFactory.GetFor(Building, GameLocation, in Vector2)"/>
/// for why a hut under construction isn't registered as a container at all (its output chest may not be
/// meaningfully usable yet).
///
/// No exception for Raisins (unlike an earlier version of this class) — by request, that's now just
/// another item the player can choose to keep or move via connectors/signs like anything else, rather
/// than a hardcoded carve-out.
/// </remarks>
internal class JunimoHutMachine : IContainer, IHasContainerPriority
{
    /*********
    ** Fields
    *********/
    /// <summary>MOD: added. This machine's own type ID (see <see cref="TypeId"/>) — computed once, since it never depends on instance state. Uses the non-generic overload since <see cref="BaseMachine.GetDefaultMachineId{TMachine}"/> requires <see cref="IMachine"/>, which this class no longer implements.</summary>
    private static readonly string TypeIdValue = BaseMachine.GetDefaultMachineId(typeof(JunimoHutMachine));

    /// <summary>The Junimo hut's output chest, wrapped as a container for Automate's own use.</summary>
    private readonly ChestHybridStorage Storage;


    /*********
    ** Accessors (IContainer, delegated to <see cref="Storage"/> except where noted)
    *********/
    /// <inheritdoc />
    public GameLocation Location => this.Storage.Location;

    /// <summary>MOD: changed — the hut's own full building footprint, not <see cref="Storage"/>'s single-tile rectangle (which only covers the exact tile its internal <see cref="ChestContainer"/> reports for diagnostics, sized for a single-tile hybrid like a Hopper — a building spans multiple tiles, and every one of them needs to be part of this container's area for a connector touching any of them to actually reach it).</summary>
    /// <inheritdoc />
    public Rectangle TileArea { get; }

    /// <summary>MOD: changed — this machine's own type ID, not the output chest's (a generic building chest with no distinct identity of its own), so chest-override config can meaningfully target it.</summary>
    /// <inheritdoc />
    public string TypeId => JunimoHutMachine.TypeIdValue;

    /// <inheritdoc />
    public string Name => this.Storage.Name;

    /// <inheritdoc />
    public ModDataDictionary ModData => this.Storage.ModData;

    /// <inheritdoc />
    public bool IsJunimoChest => this.Storage.IsJunimoChest;

    /// <inheritdoc />
    public bool IsLocked => this.Storage.IsLocked;

    /// <inheritdoc />
    public object InventoryReferenceId => this.Storage.InventoryReferenceId;

    /// <inheritdoc />
    public IInventory Inventory => this.Storage.Inventory;

    /// <inheritdoc />
    public int ContainerPriorityTier => this.Storage.ContainerPriorityTier;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="hut">The underlying Junimo hut.</param>
    /// <param name="location">The location which contains the machine.</param>
    public JunimoHutMachine(JunimoHut hut, GameLocation location)
    {
        this.TileArea = BaseMachine.GetTileAreaFor(hut);
        this.Storage = new ChestHybridStorage(location, () => new Vector2(hut.tileX.Value, hut.tileY.Value), hut.GetOutputChest);
    }

    /// <inheritdoc />
    public ITrackedStack? Get(Func<Item, bool> predicate, int count) => this.Storage.Get(predicate, count);

    /// <inheritdoc />
    public void Store(ITrackedStack stack) => this.Storage.Store(stack);

    /// <inheritdoc />
    public int GetFilled() => this.Storage.GetFilled();

    /// <inheritdoc />
    public int GetCapacity() => this.Storage.GetCapacity();

    /// <inheritdoc />
    public IEnumerator<ITrackedStack> GetEnumerator() => this.Storage.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JunimoHutMachine other ? this.Storage.Equals(other.Storage) : this.Storage.Equals(obj);

    /// <inheritdoc />
    public override int GetHashCode() => this.Storage.GetHashCode();
}

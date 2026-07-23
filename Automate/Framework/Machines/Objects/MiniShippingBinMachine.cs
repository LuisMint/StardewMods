using System.Linq;
using Pathoschild.Stardew.Automate.Framework.Machines.Buildings;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewValley;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Objects;

/// <summary>A mini-shipping bin that accepts input.</summary>
/// <remarks>See also <see cref="ShippingBinMachine"/>.</remarks>
internal class MiniShippingBinMachine : BaseMachine, IHasFilterableStorage
{
    /*********
    ** Fields
    *********/
    /// <summary>The mini-shipping bin.</summary>
    private IContainer MiniBin;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="miniBin">The mini-shipping bin.</param>
    /// <param name="location">The location which contains the machine.</param>
    public MiniShippingBinMachine(Chest miniBin, GameLocation location)
        : base(location, BaseMachine.GetTileAreaFor(miniBin.TileLocation))
    {
        this.MiniBin = new ChestContainer(miniBin, location, miniBin.TileLocation, migrateLegacyOptions: false);
    }

    /// <summary>
    /// MOD: added. The mini-shipping bin's own inventory is never added to the group as a regular
    /// container (it's only ever reached through this machine wrapper), so it needs to be wrapped here
    /// directly to pick up the group's whitelist/blacklist sign filter — otherwise a numeric condition
    /// (e.g. "only ship up to 5 iron ore into this bin") would silently never be enforced.
    /// </summary>
    /// <inheritdoc />
    public void ApplySignFilter(SignFilter filter)
    {
        this.MiniBin = new ItemFilteredContainer(this.MiniBin, filter);
    }

    /// <inheritdoc />
    public override MachineState GetState()
    {
        return MachineState.Empty; // always accepts items
    }

    /// <inheritdoc />
    public override ITrackedStack? GetOutput()
    {
        return null; // no output
    }

    /// <inheritdoc />
    public override bool SetInput(IStorage input)
    {
        foreach (ITrackedStack tracker in input.GetItems().Where(p => p.Sample.canBeShipped()))
        {
            int prevStack = tracker.Count;
            this.MiniBin.Store(tracker);
            if (prevStack > tracker.Count)
                return true;
        }

        return false;
    }
}

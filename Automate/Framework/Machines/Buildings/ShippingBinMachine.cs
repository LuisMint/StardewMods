using System.Linq;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Machines.Objects;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace Pathoschild.Stardew.Automate.Framework.Machines.Buildings;

/// <summary>A shipping bin that accepts input.</summary>
/// <remarks>
/// See also <see cref="MiniShippingBinMachine"/> — that one is bidirectional (can also provide output),
/// but this one deliberately isn't, and it's a structural difference rather than a design choice: this
/// class has no internal chest/inventory of its own to wrap and read back from. <see cref="SetInput"/>
/// drops an accepted item straight into the farm's own LIVE overnight shipping queue
/// (<see cref="Farm.getShippingBin"/>) — the same list vanilla itself uses — rather than into any
/// storage this class owns. Reading items back out of that live queue would be a fundamentally
/// different (and riskier) operation than pulling from a normal chest, since vanilla processes and
/// clears that exact list at day's end and other systems interact with it too, so this stays
/// intentionally input-only.
/// </remarks>
internal class ShippingBinMachine : BaseMachine
{
    /*********
    ** Fields
    *********/
    /// <summary>The constructed shipping bin, if applicable.</summary>
    private readonly ShippingBin? Bin;


    /*********
    ** Accessors
    *********/
    /// <summary>Get the unique ID for the shipping bin machine.</summary>
    internal static string ShippingBinId { get; } = BaseMachine.GetDefaultMachineId(typeof(ShippingBinMachine));


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="location">The machine's in-game location.</param>
    /// <param name="tileArea">The tile area covered by the machine.</param>
    public ShippingBinMachine(GameLocation location, Rectangle tileArea)
        : base(location, tileArea, ShippingBinMachine.ShippingBinId)
    {
        this.Bin = null;
    }

    /// <summary>Construct an instance.</summary>
    /// <param name="bin">The constructed shipping bin.</param>
    /// <param name="location">The location which contains the machine.</param>
    public ShippingBinMachine(ShippingBin bin, GameLocation location)
        : base(location, BaseMachine.GetTileAreaFor(bin), ShippingBinMachine.ShippingBinId)
    {
        this.Bin = bin;
    }

    /// <inheritdoc />
    public override MachineState GetState()
    {
        if (this.Bin?.isUnderConstruction() == true)
            return MachineState.Disabled;

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
        // get next item
        ITrackedStack? tracker = input.GetItems().FirstOrDefault(p => p.Sample.canBeShipped());
        if (tracker == null)
            return false;

        // ship item
        Item item = tracker.Take(tracker.Count)!;
        var binList = (this.Location as Farm ?? Game1.getFarm()).getShippingBin(Game1.MasterPlayer);
        Utility.addItemToThisInventoryList(item, binList, listMaxSpace: int.MaxValue);

        // play animation/sound
        if (this.Bin != null)
            this.Bin.showShipment(item, false);
        else if (this.Location is IslandWest islandFarm)
            islandFarm.showShipment(item, false);
        else if (this.Location is Farm farm)
            farm.showShipment(item, false);

        return true;
    }
}

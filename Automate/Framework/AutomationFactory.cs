using System;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Machines;
using Pathoschild.Stardew.Automate.Framework.Machines.Buildings;
using Pathoschild.Stardew.Automate.Framework.Machines.Objects;
using Pathoschild.Stardew.Automate.Framework.Machines.TerrainFeatures;
using Pathoschild.Stardew.Automate.Framework.Machines.Tiles;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Storage;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>Constructs machines, containers, or connectors which can be added to a machine group.</summary>
internal class AutomationFactory : IAutomationFactory
{
    /*********
    ** Fields
    *********/
    /// <summary>The mod configuration.</summary>
    private readonly Func<ModConfig> Config;

    /// <summary>Encapsulates monitoring and logging.</summary>
    private readonly IMonitor Monitor;

    /// <summary>Simplifies access to private code.</summary>
    private readonly IReflectionHelper Reflection;

    /// <summary>Whether the Better Junimos mod is installed.</summary>
    private readonly bool IsBetterJunimosLoaded;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="config">The mod configuration.</param>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="reflection">Simplifies access to private code.</param>
    /// <param name="isBetterJunimosLoaded">Whether the Better Junimos mod is installed.</param>
    public AutomationFactory(Func<ModConfig> config, IMonitor monitor, IReflectionHelper reflection, bool isBetterJunimosLoaded)
    {
        this.Config = config;
        this.Monitor = monitor;
        this.Reflection = reflection;
        this.IsBetterJunimosLoaded = isBetterJunimosLoaded;
    }

    /// <summary>Get a machine, container, or connector instance for a given object.</summary>
    /// <param name="obj">The in-game object.</param>
    /// <param name="location">The location to check.</param>
    /// <param name="tile">The tile position to check.</param>
    /// <returns>Returns an instance or <c>null</c>.</returns>
    public IAutomatable? GetFor(SObject obj, GameLocation location, in Vector2 tile)
    {
        // chest
        if (obj is Chest chest && chest.playerChest.Value)
        {
            // MOD: added — the custom Powered Chest acts as both a machine and a container; see
            // PoweredChestMachine's own remarks for why it needs its own dedicated entity type rather
            // than falling into the generic ChestContainer case below.
            if (chest.QualifiedItemId == PoweredChestMachine.QualifiedItemId)
                return new PoweredChestMachine(chest, location, tile);

            switch (chest.SpecialChestType)
            {
                case Chest.SpecialChestTypes.AutoLoader when chest.modData.ContainsKey("spacechase0.SuperHopper"): // super hopper is used to transfer items between two chests without connecting them to the same group
                case Chest.SpecialChestTypes.Enricher: // not a chest
                    break;

                case Chest.SpecialChestTypes.MiniShippingBin:
                    return new MiniShippingBinMachine(chest, location);

                default:
                    if (chest.HasContextTag(AutomateConstants.StorageTag))
                        return new ChestContainer(chest, location, tile, isTakeOnly: chest.HasContextTag(AutomateConstants.StorageTakeOnlyTag));
                    break;
            }
        }

        // machine by item ID
        switch (obj.QualifiedItemId)
        {
            case "(BC)165":
                return new AutoGrabberMachine(obj, location, tile);

            case "(BC)99":
                return new FeedHopperMachine(location, tile);

            case "(O)710":
                if (obj is CrabPot crabPot)
                    return new CrabPotMachine(crabPot, location, tile, this.Monitor);
                break;
        }

        // machine in Data/Machines
        if (obj.GetMachineData() != null)
            return new DataBasedObjectMachine(obj, location, tile, () => this.Config().MinMinutesForFairyDust);

        // connector
        // MOD: changed from a bool IsConnector(...) check to a role-aware GetConnectorRole(...) lookup.
        ConnectorRole? role = this.GetConnectorRole(obj);
        if (role != null)
            return new Connector(location, tile, role.Value);

        // MOD: added — a configured whitelist/blacklist sign. Returning a SignPlaceholder here makes
        // Automate's existing "was something automatable placed/removed nearby?" change-tracking
        // notice this sign, so placing/removing it triggers a rescan the same way placing a machine
        // or path does. The actual sign content (whitelist/blacklist item) is still read separately
        // by MachineGroupFactory during a scan — this placeholder is never itself treated as a
        // machine, container, or connector.
        ModConfig config = this.Config();
        if (config.WhitelistSignNames.Contains(obj.QualifiedItemId) || config.WhitelistSignNames.Contains(obj.Name)
            || config.BlacklistSignNames.Contains(obj.QualifiedItemId) || config.BlacklistSignNames.Contains(obj.Name))
        {
            return new SignPlaceholder(location, tile);
        }

        return null;
    }

    /// <summary>Get a machine, container, or connector instance for a given terrain feature.</summary>
    /// <param name="feature">The terrain feature.</param>
    /// <param name="location">The location to check.</param>
    /// <param name="tile">The tile position to check.</param>
    /// <returns>Returns an instance or <c>null</c>.</returns>
    public IAutomatable? GetFor(TerrainFeature feature, GameLocation location, in Vector2 tile)
    {
        // machine
        switch (feature)
        {
            case Bush bush when BushMachine.CanAutomate(bush):
                return new BushMachine(bush, location);

            case FruitTree fruitTree:
                return new FruitTreeMachine(fruitTree, location, tile);

            case Tree tree when TreeMachine.CanAutomate(tree) && tree.growthStage.Value >= Tree.treeStage: // avoid accidental machine links due to seeds spreading automatically
                return new TreeMachine(tree, location, tile, this.Config().CollectTreeMoss, this.Reflection);
        }

        // connector
        // MOD: changed from a bool IsConnector(...) check to a role-aware GetConnectorRole(...) lookup.
        ConnectorRole? role = this.GetConnectorRole(feature);
        if (role != null)
            return new Connector(location, tile, role.Value);

        return null;
    }

    /// <summary>Get a machine, container, or connector instance for a given building.</summary>
    /// <param name="building">The building.</param>
    /// <param name="location">The location to check.</param>
    /// <param name="tile">The tile position to check.</param>
    /// <returns>Returns an instance or <c>null</c>.</returns>
    public IAutomatable? GetFor(Building building, GameLocation location, in Vector2 tile)
    {
        // building by type
        switch (building)
        {
            case FishPond pond:
                return new FishPondMachine(pond, location);

            case JunimoHut hut:
                {
                    ModConfig config = this.Config();

                    JunimoHutBehavior gemBehavior = config.JunimoHutBehaviorForGems;
                    if (gemBehavior is JunimoHutBehavior.AutoDetect)
                        gemBehavior = JunimoHutBehavior.Ignore;

                    JunimoHutBehavior fertilizerBehavior = config.JunimoHutBehaviorForFertilizer;
                    if (fertilizerBehavior is JunimoHutBehavior.AutoDetect)
                        fertilizerBehavior = this.IsBetterJunimosLoaded ? JunimoHutBehavior.Ignore : JunimoHutBehavior.MoveIntoChests;

                    JunimoHutBehavior seedBehavior = config.JunimoHutBehaviorForSeeds;
                    if (seedBehavior is JunimoHutBehavior.AutoDetect)
                        seedBehavior = this.IsBetterJunimosLoaded ? JunimoHutBehavior.Ignore : JunimoHutBehavior.MoveIntoChests;

                    return new JunimoHutMachine(hut, location, gemBehavior, fertilizerBehavior, seedBehavior, config.JunimoHutBehaviors);
                }

            case ShippingBin bin:
                return new ShippingBinMachine(bin, location);

            default:
                if (DataBasedBuildingMachine.CanAutomate(building))
                    return new DataBasedBuildingMachine(building, location);
                break;
        }

        // building by buildingType
        if (building.buildingType.Value == "Silo")
            return new FeedHopperMachine(building, location);

        return null;
    }

    /// <summary>Get a machine, container, or connector instance for a given tile position.</summary>
    /// <param name="location">The location to check.</param>
    /// <param name="tile">The tile position to check.</param>
    /// <returns>Returns an instance or <c>null</c>.</returns>
    /// <remarks>Shipping bin logic from <see cref="Farm.leftClick"/>, garbage can logic from <see cref="Town.checkAction"/>.</remarks>
    public IAutomatable? GetForTile(GameLocation location, in Vector2 tile)
    {
        // shipping bin on island farm
        if (location is IslandWest farm && farm.farmhouseRestored.Value && (int)tile.X == farm.shippingBinPosition.X && (int)tile.Y == farm.shippingBinPosition.Y)
            return new ShippingBinMachine(Game1.getFarm(), new Rectangle(farm.shippingBinPosition.X, farm.shippingBinPosition.Y, 2, 1));

        // garbage can
        string action = location.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Action", "Buildings");
        if (!string.IsNullOrWhiteSpace(action))
        {
            string[] fields = ArgUtility.SplitBySpace(action);
            if (string.Equals(fields[0], "Garbage", StringComparison.OrdinalIgnoreCase) && ArgUtility.HasIndex(fields, 1))
                return new TrashCanMachine(location, tile, fields[1]);
        }

        // fridge
        switch (location)
        {
            case FarmHouse house when (house.fridgePosition != Point.Zero && house.fridgePosition.X == (int)tile.X && house.fridgePosition.Y == (int)tile.Y):
                return new ChestContainer(house.fridge.Value, location, tile, migrateLegacyOptions: false);

            case IslandFarmHouse house when (house.fridgePosition != Point.Zero && house.fridgePosition.X == (int)tile.X && house.fridgePosition.Y == (int)tile.Y):
                return new ChestContainer(house.fridge.Value, location, tile, migrateLegacyOptions: false);
        }

        return null;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// MOD: added, replaces the old bool <c>IsConnector(object)</c> method. Get the connector role
    /// for a given in-game entity, or <c>null</c> if it's not a connector at all. Checks
    /// <see cref="ModConfig.ChestInputConnectors"/> and <see cref="ModConfig.ChestOutputConnectors"/>
    /// first (more specific), falling back to the original <see cref="ModConfig.Connectors"/> list
    /// (role <see cref="ConnectorRole.Both"/>) for backward compatibility with existing configs.
    /// </summary>
    /// <param name="entity">The in-game entity.</param>
    private ConnectorRole? GetConnectorRole(object entity)
    {
        ModConfig config = this.Config();

        string? qualifiedItemId;
        string? internalName;

        switch (entity)
        {
            case Item item:
                qualifiedItemId = item.QualifiedItemId;
                internalName = item.Name;
                break;

            case Flooring floor:
                {
                    string? itemId = floor.GetData()?.ItemId;
                    ParsedItemData? itemData = ItemRegistry.GetData(itemId);
                    if (itemData == null)
                        return null;

                    qualifiedItemId = itemData.QualifiedItemId;
                    internalName = itemData.InternalName;
                }
                break;

            default:
                return null;
        }

        bool Matches(System.Collections.Generic.HashSet<string> set) =>
            (qualifiedItemId != null && set.Contains(qualifiedItemId))
            || (internalName != null && set.Contains(internalName));

        if (Matches(config.ChestInputConnectors))
            return ConnectorRole.ChestInputOnly;

        if (Matches(config.ChestOutputConnectors))
            return ConnectorRole.ChestOutputOnly;

        if (Matches(config.Connectors))
            return ConnectorRole.Both;

        return null;
    }
}

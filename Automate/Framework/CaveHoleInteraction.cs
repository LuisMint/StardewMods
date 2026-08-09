using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Handles the Cave Hole building's descent interaction, registered via
/// <see cref="GameLocation.RegisterTileAction"/> against its <c>Data/Buildings</c> <c>ActionTiles</c>
/// entry — only the building's center tile (the ladder) has an action; the other 8 tiles in its 3x3
/// footprint are plain walkable floor with no action of their own (see the building's own
/// <c>CollisionMap</c>/<c>ActionTiles</c> remarks in <c>BuildingsData.json</c>). Interacting with the
/// ladder plays the same "stairsdown" cue vanilla uses for the mine entrance, then warps the player into
/// the CLICKED building's own interior — after first stamping the player's own <c>modData</c> with where
/// they warped in FROM, so <see cref="Patches.CaveHoleExitPatches"/> can send them back to the right spot
/// when they leave again.
///
/// MOD: changed — per direct user request (building a second Cave Hole revealed both shared one
/// interior), this now uses vanilla's own per-building interior system instead of a single
/// always-present location: <c>BuildingsData.json</c>'s <c>IndoorMap</c> field is set to our map, so
/// every placed Cave Hole gets its OWN uniquely-named interior automatically (the same mechanism a Shed
/// or Barn uses — see <see cref="Building.GetIndoors"/>/<see cref="Building.createIndoors"/>, which
/// suffixes the map name with a fresh GUID per building instance). <see cref="Patches.CaveHoleExitPatches"/>
/// and <see cref="Patches.CaveHolePlacementPatches"/> both match against <see cref="InteriorLocationBaseName"/>
/// as a PREFIX for this reason, since every Cave Hole's interior name starts with it but none match it
/// exactly.
/// </summary>
internal class CaveHoleInteraction
{
    /*********
    ** Fields
    *********/
    /// <summary>The key this interaction is registered under — must match the <c>Action</c> value on the Cave Hole's <c>Data/Buildings</c> <c>ActionTiles</c> entry.</summary>
    public const string ActionKey = "luisMint.PoweredAutomation_CaveHoleInteract";

    /// <summary>The base name every Cave Hole interior's unique location name starts with — matches <c>BuildingsData.json</c>'s <c>IndoorMap</c> value, since vanilla names each instance <c>{IndoorMap}{a fresh GUID}</c>.</summary>
    internal const string InteriorLocationBaseName = "luisMint.PoweredAutomation_CaveHole1";

    /// <summary>The Cave Hole's own <c>Data/Buildings</c> key (i.e. <see cref="Building.buildingType"/>'s value for one) — used by <see cref="CaveHoleQuarrySystem"/> to find every placed building.</summary>
    internal const string BuildingType = "luisMint.PoweredAutomation_CaveHole";

    /// <summary>
    /// MOD: added. The Big Cave Hole's own <c>Data/Buildings</c> key — a <c>BuildingToUpgrade</c> upgrade
    /// of the regular Cave Hole (see its own <c>BuildingsData.json</c> entry), so it's a DIFFERENT
    /// <see cref="Building.buildingType"/> value even though vanilla's own upgrade flow reuses the exact
    /// same <see cref="Building"/>/interior instance in place (only <see cref="Building.buildingType"/>
    /// and the interior's own map get swapped — see <see cref="M:StardewValley.Buildings.Building.FinishConstruction"/>
    /// and <see cref="M:StardewValley.Buildings.Building.LoadFromBuildingData"/>). <see cref="InteriorLocationBaseName"/>
    /// still matches an upgraded interior fine (its unique name was set once at original construction and
    /// is never changed by an upgrade), but anything keyed on <see cref="BuildingType"/> alone would stop
    /// matching a building the moment it upgrades — see <see cref="IsCaveHoleBuildingType"/>.
    /// </summary>
    internal const string BigBuildingType = "luisMint.PoweredAutomation_CaveHole2";

    /// <summary>The tile the player arrives at when entering a Cave Hole.</summary>
    private const int EntryTileX = 13;

    /// <inheritdoc cref="EntryTileX"/>
    private const int EntryTileY = 6;

    /// <summary>The <see cref="Farmer.modData"/> key storing which location the player should return to when they leave a Cave Hole (see <see cref="Patches.CaveHoleExitPatches"/>).</summary>
    internal const string ReturnLocationModDataKey = "luisMint.PoweredAutomation/CaveHoleReturnLocation";

    /// <summary>The <see cref="Farmer.modData"/> key storing the X tile the player should return to when they leave a Cave Hole.</summary>
    internal const string ReturnTileXModDataKey = "luisMint.PoweredAutomation/CaveHoleReturnTileX";

    /// <summary>The <see cref="Farmer.modData"/> key storing the Y tile the player should return to when they leave a Cave Hole.</summary>
    internal const string ReturnTileYModDataKey = "luisMint.PoweredAutomation/CaveHoleReturnTileY";


    /*********
    ** Public methods
    *********/
    /// <summary>Register this interaction with the game, so clicking a Cave Hole's ladder tile invokes <see cref="Handle"/>.</summary>
    public void Register()
    {
        GameLocation.RegisterTileAction(CaveHoleInteraction.ActionKey, this.Handle);
    }

    /// <summary>Get whether a <see cref="Building.buildingType"/> value is a Cave Hole or Big Cave Hole.</summary>
    /// <param name="buildingType">The building's own <see cref="Building.buildingType"/> value.</param>
    internal static bool IsCaveHoleBuildingType(string buildingType)
    {
        return buildingType == CaveHoleInteraction.BuildingType || buildingType == CaveHoleInteraction.BigBuildingType;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Handle a click on a Cave Hole's ladder tile.</summary>
    /// <param name="location">The location containing the Cave Hole.</param>
    /// <param name="args">The action's arguments — unused, since this action takes none.</param>
    /// <param name="who">The player who triggered the action.</param>
    /// <param name="tile">The tile that was clicked.</param>
    /// <returns>Returns whether the action was handled.</returns>
    private bool Handle(GameLocation location, string[] args, Farmer who, Point tile)
    {
        Building? caveHole = location.getBuildingAt(new Vector2(tile.X, tile.Y));
        if (caveHole is null)
            return false;

        GameLocation? indoors = caveHole.GetIndoors();
        if (indoors is null)
            return false; // still under construction, or something went wrong creating its interior

        // MOD: added — remember where to send the player back to on the way out, since who.Tile is
        // wherever they were standing to interact with the ladder (always a walkable tile just outside
        // it, so it's always safe to warp back to).
        who.modData[CaveHoleInteraction.ReturnLocationModDataKey] = location.NameOrUniqueName;
        who.modData[CaveHoleInteraction.ReturnTileXModDataKey] = ((int)who.Tile.X).ToString();
        who.modData[CaveHoleInteraction.ReturnTileYModDataKey] = ((int)who.Tile.Y).ToString();

        location.playSound("stairsdown");
        Game1.warpFarmer(indoors.NameOrUniqueName, CaveHoleInteraction.EntryTileX, CaveHoleInteraction.EntryTileY, Game1.down);
        return true;
    }
}

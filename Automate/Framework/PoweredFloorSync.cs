using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: changed. Swaps a connector's displayed appearance between an "unpowered" and "powered" look
/// while it's within power system range — per direct user request, no longer via the Alternative
/// Textures mod (see <see cref="Patches.ConnectorTexturePatches"/>'s own remarks for why); this class
/// now just stamps each managed connector's own <see cref="Patches.ConnectorTexturePatches.ConnectorVariantModDataKey"/>
/// with its current variant, which that class's <see cref="Flooring.GetTexture"/> patch reads directly
/// at render time.
///
/// This only handles the two STATIC states — fully unpowered, and powered while part of a valid
/// (active) automation group. A connector that's powered but NOT part of a valid group is left
/// alone here and its tile returned in the result set instead — <see cref="PoweredFloorAnimator"/>
/// takes over that tile's appearance, since that state needs a per-tick animation rather than a
/// one-off assignment on rescan.
///
/// MOD: added — this scan (of every terrain feature in the location) now runs ONLY here, once per
/// rebuild, rather than also being repeated by <see cref="PoweredFloorAnimator"/> up to 6 times a
/// second — the returned "orphaned" tile set is cached by <see cref="MachineManager"/> and handed to
/// the animator directly each frame, so it never needs to rescan a location's (potentially large,
/// e.g. hundreds of crops) terrain feature collection itself.
/// </summary>
internal class PoweredFloorSync
{
    /*********
    ** Public methods
    *********/
    /// <summary>Sync every managed connector's displayed appearance in a location to match its current power and group state.</summary>
    /// <param name="location">The location to sync.</param>
    /// <param name="data">The location's freshly-rebuilt machine data. Its <see cref="MachineDataForLocation.ActiveTiles"/> already folds in any Junimo-touching connector with its own local automation — see that record's own remarks.</param>
    /// <returns>The tiles left "powered but not part of an active group" — i.e. the ones <see cref="PoweredFloorAnimator"/> should animate.</returns>
    public HashSet<Vector2> Sync(GameLocation location, MachineDataForLocation data)
    {
        HashSet<Vector2> orphanedTiles = [];

        foreach ((Vector2 tile, TerrainFeature feature) in location.terrainFeatures.Pairs)
        {
            if (feature is not Flooring floor || !Patches.ConnectorTexturePatches.IsManagedConnector(floor.whichFloor.Value))
                continue;

            bool isPowered = data.PoweredTiles == null || data.PoweredTiles.Contains(tile);
            if (!isPowered)
                floor.modData[Patches.ConnectorTexturePatches.ConnectorVariantModDataKey] = Patches.ConnectorTexturePatches.UnpoweredVariant.ToString();
            else if (data.ActiveTiles.ContainsKey(tile))
                floor.modData[Patches.ConnectorTexturePatches.ConnectorVariantModDataKey] = Patches.ConnectorTexturePatches.PoweredVariant.ToString();
            else
                orphanedTiles.Add(tile); // powered but not part of an active (valid) automation group — PoweredFloorAnimator drives this tile's appearance instead
        }

        return orphanedTiles;
    }
}

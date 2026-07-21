using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Swaps a connector's displayed appearance between an "unpowered" and "powered" look
/// while it's within power system range, via the Alternative Textures mod's <c>modData</c>-driven
/// rendering. Alternative Textures patches <c>Flooring.draw</c> to check a few <c>modData</c> keys
/// fresh on every draw call, so changing them here takes effect immediately on the next frame.
///
/// This is deliberately NOT done by swapping between two <c>Data/FloorsAndPaths</c> entries sharing
/// one ItemId (the more "native" approach) — the game's own placement code has to pick one of those
/// two entries somewhat arbitrarily when a tile is first placed, and that choice can't be relied on
/// to stay consistent, which both breaks the visual (stuck on the wrong variant) and, worse, can
/// fragment a single connected path into separate networks if the type-key used for grouping is
/// sensitive to which entry a given tile currently has. Routing the visual through Alternative
/// Textures instead means the tile only ever has ONE real <see cref="Flooring.whichFloor"/> identity
/// (stable, unambiguous), and only its DISPLAYED texture changes.
///
/// This only handles the two STATIC states — fully unpowered, and powered while part of a valid
/// (active) automation group. A connector that's powered but NOT part of a valid group is left
/// alone here; <see cref="PoweredFloorAnimator"/> takes over that tile's appearance instead, since
/// that state needs a per-tick animation rather than a one-off assignment on rescan.
/// </summary>
internal class PoweredFloorSync
{
    /*********
    ** Fields
    *********/
    /// <summary>The variation index which shows the unpowered appearance.</summary>
    internal const int UnpoweredVariation = 0;

    /// <summary>The variation index which shows the fully-powered appearance.</summary>
    internal const int PoweredVariation = 1;

    /// <summary>Get the configured mapping of a connector's floor ID to the Alternative Textures texture ID (in the form <c>{Owner}.{ModelName}</c>) providing its appearance variations.</summary>
    private readonly System.Func<Dictionary<string, string>> GetConnectorTextureIds;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getConnectorTextureIds">Get the configured mapping of a connector's floor ID to the Alternative Textures texture ID providing its appearance variations.</param>
    public PoweredFloorSync(System.Func<Dictionary<string, string>> getConnectorTextureIds)
    {
        this.GetConnectorTextureIds = getConnectorTextureIds;
    }

    /// <summary>Sync every managed connector's displayed appearance in a location to match its current power and group state.</summary>
    /// <param name="location">The location to sync.</param>
    /// <param name="data">The location's freshly-rebuilt machine data.</param>
    public void Sync(GameLocation location, MachineDataForLocation data)
    {
        Dictionary<string, string> connectorTextureIds = this.GetConnectorTextureIds();
        if (connectorTextureIds.Count == 0)
            return; // nothing configured — skip entirely, no cost

        foreach ((Vector2 tile, TerrainFeature feature) in location.terrainFeatures.Pairs)
        {
            if (feature is not Flooring floor)
                continue;

            if (!connectorTextureIds.TryGetValue(floor.whichFloor.Value, out string? textureId))
                continue;

            bool isPowered = data.PoweredTiles == null || data.PoweredTiles.Contains(tile);
            if (!isPowered)
            {
                // MOD: Alternative Textures' Flooring draw patch only reads "AlternativeTextureName"
                // and "AlternativeTextureVariation" at render time (confirmed by decompiling
                // AlternativeTextures.Framework.Patches.StandardObjects.FlooringPatch.DrawPrefix) —
                // "AlternativeTextureOwner" isn't consulted there, so it's intentionally not set here.
                floor.modData["AlternativeTextureName"] = textureId;
                floor.modData["AlternativeTextureVariation"] = PoweredFloorSync.UnpoweredVariation.ToString();
            }
            else if (data.ActiveTiles.ContainsKey(tile))
            {
                floor.modData["AlternativeTextureName"] = textureId;
                floor.modData["AlternativeTextureVariation"] = PoweredFloorSync.PoweredVariation.ToString();
            }
            // else: powered but not part of an active (valid) automation group — leave modData
            // alone; PoweredFloorAnimator drives this tile's appearance instead.
        }
    }
}

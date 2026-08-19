using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: changed. Swaps a connector's displayed appearance between an "unpowered," "powered," and
/// "powered but orphaned" look while it's within power system range — no longer via the Alternative
/// Textures mod (see <see cref="Patches.ConnectorTexturePatches"/>'s own remarks for why); this class
/// now just stamps each managed connector's own <see cref="Patches.ConnectorTexturePatches.ConnectorVariantModDataKey"/>
/// with its current category, which that class's <see cref="Flooring.GetTexture"/> patch reads directly
/// at render time (animating the "orphaned" category's own pulse locally, purely client-side — see that
/// class's own remarks for why the actual pulse FRAME is never part of this shared category value).
///
/// MOD: added — only the host actually writes this shared/networked category now. This method still
/// runs on every client, including a farmhand, since <see cref="MachineManager"/> still needs a fresh
/// rescan locally for other purposes (the no-power icon, etc.) — but before this, a farmhand's own
/// independent rebuild wrote to the exact same synced <c>Flooring.modData</c> key as the host's,
/// racing it with whatever THIS client's own (unsynchronized) rebuild timing happened to compute.
/// </summary>
internal class PoweredFloorSync
{
    /*********
    ** Public methods
    *********/
    /// <summary>Sync every managed connector's displayed category in a location to match its current power and group state.</summary>
    /// <param name="location">The location to sync.</param>
    /// <param name="data">The location's freshly-rebuilt machine data. Its <see cref="MachineDataForLocation.ActiveTiles"/> already folds in any Junimo-touching connector with its own local automation — see that record's own remarks.</param>
    public void Sync(GameLocation location, MachineDataForLocation data)
    {
        if (!Context.IsMainPlayer)
            return; // MOD: added — see this class's own remarks for why only the host writes this shared category

        foreach ((Vector2 tile, TerrainFeature feature) in location.terrainFeatures.Pairs)
        {
            if (feature is not Flooring floor || !Patches.ConnectorTexturePatches.IsManagedConnector(floor.whichFloor.Value))
                continue;

            bool isPowered = data.PoweredTiles == null || data.PoweredTiles.Contains(tile);
            int category = !isPowered
                ? Patches.ConnectorTexturePatches.UnpoweredVariant
                : data.ActiveTiles.ContainsKey(tile)
                    ? Patches.ConnectorTexturePatches.PoweredVariant
                    : Patches.ConnectorTexturePatches.OrphanedCategory; // powered but not part of an active (valid) automation group

            floor.modData[Patches.ConnectorTexturePatches.ConnectorVariantModDataKey] = category.ToString();
        }
    }
}

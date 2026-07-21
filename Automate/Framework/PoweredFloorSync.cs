using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Swaps a connector's underlying floor appearance to a "powered" variant while it's
/// within power system range, and back to its base appearance when it isn't — rather than drawing a
/// separate overlay sprite. Reusing the game's own <see cref="Flooring.whichFloor"/> field means
/// rendering is handled entirely by the game's normal draw sequence: correct depth (never draws over
/// the player or anything standing on the tile), correct connectivity with neighbors (no need to
/// guess or replicate the game's own autotile logic), at the cost of being affected by lighting like
/// any other floor tile (won't glow at night) — a deliberate trade-off over a custom-drawn overlay.
///
/// This only ever affects a single physical tile's appearance — it never creates, removes, or
/// duplicates any item. The "powered" and "base" appearances are just two different
/// <c>Data/FloorsAndPaths</c> entries that a content pack defines for the same craftable item; this
/// class just switches which one a given tile currently points to.
/// </summary>
internal class PoweredFloorSync
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the configured mapping of base connector floor ID to its powered variant ID.</summary>
    private readonly Func<Dictionary<string, string>> GetConnectorPoweredVariants;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getConnectorPoweredVariants">Get the configured mapping of base connector floor ID to its powered variant ID.</param>
    public PoweredFloorSync(Func<Dictionary<string, string>> getConnectorPoweredVariants)
    {
        this.GetConnectorPoweredVariants = getConnectorPoweredVariants;
    }

    /// <summary>Sync every managed connector's floor appearance in a location to match its current power state.</summary>
    /// <param name="location">The location to sync.</param>
    /// <param name="poweredTiles">The set of currently-powered tiles for this location, or <c>null</c> if the power system is disabled.</param>
    public void Sync(GameLocation location, HashSet<Vector2>? poweredTiles)
    {
        Dictionary<string, string> baseToPowered = this.GetConnectorPoweredVariants();
        if (baseToPowered.Count == 0)
            return; // nothing configured — skip entirely, no cost

        // build a reverse lookup so we can switch back to the base appearance too — cheap, since
        // this dictionary is expected to stay tiny (one or two entries per custom connector type)
        Dictionary<string, string> poweredToBase = baseToPowered
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);

        foreach ((Vector2 tile, TerrainFeature feature) in location.terrainFeatures.Pairs)
        {
            if (feature is not Flooring floor)
                continue;

            string currentId = floor.whichFloor.Value;
            bool isPowered = poweredTiles != null && poweredTiles.Contains(tile);

            if (isPowered && baseToPowered.TryGetValue(currentId, out string? poweredId))
                floor.whichFloor.Value = poweredId;
            else if (!isPowered && poweredToBase.TryGetValue(currentId, out string? baseId))
                floor.whichFloor.Value = baseId;
        }
    }
}

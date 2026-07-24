using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Common;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Encapsulates Automate's optional "power system" — a gate that restricts automation
/// to tiles within range of a configured power source. For now the power source is a placeholder
/// (the vanilla Lightning Rod, via config), intended to be swapped for a dedicated custom "tesla
/// coil" object later; keeping this logic in its own self-contained class means that swap (or any
/// future extension — tiers, multiple source types, etc.) only touches this one file.
///
/// Disabled by default (see <see cref="IsEnabled"/>) — when off, every tile is unrestricted, so
/// existing setups aren't affected unless a player deliberately opts in via config.
/// </summary>
internal class PowerSystem
{
    /*********
    ** Fields
    *********/
    /// <summary>Get whether the power system is currently enabled.</summary>
    private readonly Func<bool> GetEnabledFromConfig;

    /// <summary>Get the item names/IDs that currently act as a power source.</summary>
    private readonly Func<HashSet<string>> GetSourceNames;

    /// <summary>MOD: changed from a total width to a distance-from-center, to guarantee exact centering with no rounding ambiguity. Get how many tiles out from a power source, in each cardinal direction, its power extends.</summary>
    private readonly Func<int> GetRangeDistance;

    /// <summary>MOD: added. Get the item names/IDs that currently act as a "local" power source — e.g. the Powered Chest — which powers only its own tile plus the 4 orthogonal neighbors, regardless of <see cref="GetRangeDistance"/>.</summary>
    private readonly Func<HashSet<string>> GetLocalSourceNames;


    /*********
    ** Accessors
    *********/
    /// <summary>Whether the power system is currently enabled. When <c>false</c>, automation is unrestricted everywhere (as if the power system didn't exist).</summary>
    public bool IsEnabled => this.GetEnabledFromConfig();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getEnabled">Get whether the power system is currently enabled.</param>
    /// <param name="getSourceNames">Get the item names/IDs that currently act as a power source.</param>
    /// <param name="getRangeDistance">MOD: changed. Get how many tiles out from a power source, in each cardinal direction, its power extends.</param>
    /// <param name="getLocalSourceNames">MOD: added. Get the item names/IDs that currently act as a "local" power source, which always powers only its own tile plus the 4 orthogonal neighbors regardless of <paramref name="getRangeDistance"/>.</param>
    public PowerSystem(Func<bool> getEnabled, Func<HashSet<string>> getSourceNames, Func<int> getRangeDistance, Func<HashSet<string>> getLocalSourceNames)
    {
        this.GetEnabledFromConfig = getEnabled;
        this.GetSourceNames = getSourceNames;
        this.GetRangeDistance = getRangeDistance;
        this.GetLocalSourceNames = getLocalSourceNames;
    }

    /// <summary>
    /// Get the set of tiles powered by a power source in the given location, or <c>null</c> if the
    /// power system is disabled — meaning every tile should be treated as unrestricted. Each power
    /// source covers a square area centered on it, extending the configured distance in each
    /// cardinal direction; multiple sources' areas simply combine (no stacking/overlap logic). A
    /// "local" power source (see <see cref="GetLocalSourceNames"/>) instead always covers a fixed
    /// plus-shape (itself plus its 4 orthogonal neighbors), regardless of the configured range.
    /// </summary>
    /// <param name="location">The location to scan for power sources.</param>
    /// <param name="locationIndex">An indexed view of the location.</param>
    public HashSet<Vector2>? GetPoweredTiles(GameLocation location, LocationFloodFillIndex locationIndex)
    {
        if (!this.IsEnabled)
            return null;

        HashSet<string> sourceNames = this.GetSourceNames();
        HashSet<string> localSourceNames = this.GetLocalSourceNames();
        if (sourceNames.Count == 0 && localSourceNames.Count == 0)
            return []; // power system is on, but nothing is configured as a source — nothing is powered

        int rangeDistance = Math.Max(0, this.GetRangeDistance());

        HashSet<Vector2> powered = new();
        foreach (Vector2 tile in location.GetTiles())
        {
            foreach (object target in locationIndex.GetEntities(tile))
            {
                if (target is not SObject sourceObj)
                    continue;

                if (sourceNames.Contains(sourceObj.QualifiedItemId) || sourceNames.Contains(sourceObj.Name))
                    this.AddPoweredArea(powered, tile, rangeDistance);

                if (localSourceNames.Contains(sourceObj.QualifiedItemId) || localSourceNames.Contains(sourceObj.Name))
                    this.AddLocalPoweredArea(powered, tile);
            }
        }

        return powered;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Add every tile in a power source's coverage area to the given set.</summary>
    /// <param name="powered">The set to add tiles to.</param>
    /// <param name="sourceTile">The power source's tile position.</param>
    /// <param name="rangeDistance">How many tiles out from <paramref name="sourceTile"/>, in each cardinal direction, to cover.</param>
    private void AddPoweredArea(HashSet<Vector2> powered, Vector2 sourceTile, int rangeDistance)
    {
        // MOD: changed — built directly from ±distance around the source, instead of a
        // width-based start offset (which used integer division and could end up slightly
        // off-center depending on rounding). This is always exactly centered by construction.
        for (int x = (int)sourceTile.X - rangeDistance; x <= (int)sourceTile.X + rangeDistance; x++)
        {
            for (int y = (int)sourceTile.Y - rangeDistance; y <= (int)sourceTile.Y + rangeDistance; y++)
                powered.Add(new Vector2(x, y));
        }
    }

    /// <summary>MOD: added. Add a "local" power source's fixed plus-shaped coverage area (itself plus its 4 orthogonal neighbors) to the given set.</summary>
    /// <param name="powered">The set to add tiles to.</param>
    /// <param name="sourceTile">The power source's tile position.</param>
    private void AddLocalPoweredArea(HashSet<Vector2> powered, Vector2 sourceTile)
    {
        powered.Add(sourceTile);
        powered.Add(sourceTile + new Vector2(1, 0));
        powered.Add(sourceTile + new Vector2(-1, 0));
        powered.Add(sourceTile + new Vector2(0, 1));
        powered.Add(sourceTile + new Vector2(0, -1));
    }
}

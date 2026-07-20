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

    /// <summary>Get the width/height in tiles of the square area powered by each source.</summary>
    private readonly Func<int> GetRangeSize;


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
    /// <param name="getRangeSize">Get the width/height in tiles of the square area powered by each source.</param>
    public PowerSystem(Func<bool> getEnabled, Func<HashSet<string>> getSourceNames, Func<int> getRangeSize)
    {
        this.GetEnabledFromConfig = getEnabled;
        this.GetSourceNames = getSourceNames;
        this.GetRangeSize = getRangeSize;
    }

    /// <summary>
    /// Get the set of tiles powered by a power source in the given location, or <c>null</c> if the
    /// power system is disabled — meaning every tile should be treated as unrestricted. Each power
    /// source covers a square area of the configured size, centered on it; multiple sources' areas
    /// simply combine (no stacking/overlap logic).
    /// </summary>
    /// <param name="location">The location to scan for power sources.</param>
    /// <param name="locationIndex">An indexed view of the location.</param>
    public HashSet<Vector2>? GetPoweredTiles(GameLocation location, LocationFloodFillIndex locationIndex)
    {
        if (!this.IsEnabled)
            return null;

        HashSet<string> sourceNames = this.GetSourceNames();
        if (sourceNames.Count == 0)
            return []; // power system is on, but nothing is configured as a source — nothing is powered

        int rangeSize = Math.Max(1, this.GetRangeSize());

        HashSet<Vector2> powered = new();
        foreach (Vector2 tile in location.GetTiles())
        {
            foreach (object target in locationIndex.GetEntities(tile))
            {
                if (target is not SObject sourceObj)
                    continue;

                bool isPowerSource = sourceNames.Contains(sourceObj.QualifiedItemId) || sourceNames.Contains(sourceObj.Name);
                if (isPowerSource)
                    this.AddPoweredArea(powered, tile, rangeSize);
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
    /// <param name="rangeSize">The width/height in tiles of the square area to cover.</param>
    private void AddPoweredArea(HashSet<Vector2> powered, Vector2 sourceTile, int rangeSize)
    {
        int startX = (int)sourceTile.X - rangeSize / 2;
        int startY = (int)sourceTile.Y - rangeSize / 2;

        for (int x = startX; x < startX + rangeSize; x++)
        {
            for (int y = startY; y < startY + rangeSize; y++)
                powered.Add(new Vector2(x, y));
        }
    }
}

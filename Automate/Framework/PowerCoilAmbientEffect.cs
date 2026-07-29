using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Patches;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Loops the same single sparkle flash used for the geode-cracking reward reveal on
/// every placed Power Coil, as a passive "it's running" ambient effect — purely cosmetic, unrelated
/// to whether the coil is actually part of a valid power/automation setup. Only scans the player's
/// own current location each tick, since there's no need to track ambient effects for locations no
/// one's looking at; each client naturally only needs the effect wherever its own player currently
/// is. Re-triggers a fresh flash for each coil right as its previous one finishes (at normal speed,
/// not sped up), so it reads as a continuous pulsing loop — one sprite at a time, not a shower of
/// particles.
/// </summary>
internal class PowerCoilAmbientEffect
{
    /*********
    ** Fields
    *********/
    /// <summary>The per-frame duration of the sparkle, in milliseconds — double the vanilla default (100ms), i.e. half speed.</summary>
    private const float SparkleInterval = 200f;

    /// <summary>The number of frames in the sparkle animation.</summary>
    private const int SparkleAnimationLength = 8;

    /// <summary>The sparkle's opacity (1 = fully opaque, matching vanilla; lower makes it fainter).</summary>
    private const float SparkleAlpha = 0.5f;

    /// <summary>The sparkle's tint.</summary>
    private static readonly Color SparkleColor = new(255, 240, 120);

    /// <summary>How far behind the coil's own sprite the sparkle draws — subtracted from the tile's natural Y-sorted layer depth, the same trick vanilla uses to keep a shadow sprite drawn behind its owner (see e.g. <c>Object.drawInMenu</c>'s shadow draw).</summary>
    private const float BehindCoilLayerDepthOffset = 0.0001f;

    /// <summary>How many ticks one full flash takes (at 60 ticks/second) — the next flash is queued to start right as this one finishes, so consecutive flashes read as one continuous loop.</summary>
    private const int TicksPerFlash = (int)(PowerCoilAmbientEffect.SparkleInterval * PowerCoilAmbientEffect.SparkleAnimationLength * 60 / 1000);

    /// <summary>Ticks remaining until the next flash, keyed by tile.</summary>
    private readonly Dictionary<Vector2, int> TicksUntilNextFlash = new();

    /// <summary>The location the tiles above were last scanned in, so state resets cleanly on warp instead of carrying over stale tiles from a different location.</summary>
    private GameLocation? TrackedLocation;


    /*********
    ** Public methods
    *********/
    /// <summary>Advance the ambient effect by one game tick, for the player's current location.</summary>
    public void Tick()
    {
        GameLocation? location = Game1.currentLocation;
        if (location == null)
            return;

        if (!ReferenceEquals(location, this.TrackedLocation))
        {
            this.TrackedLocation = location;
            this.TicksUntilNextFlash.Clear();
        }

        HashSet<Vector2> seenTiles = new();
        foreach ((Vector2 tile, SObject obj) in location.objects.Pairs)
        {
            if (obj.QualifiedItemId != PowerCoilPatches.TargetQualifiedItemId)
                continue;

            // MOD: added — an over-capacity coil (see PowerSiloSystem) isn't actually doing anything,
            // so it shouldn't get the "it's running" sparkle either; simply not tracking its tile here
            // means any flash already in flight for it just stops repeating (and its tracking entry
            // gets cleaned up below, the same as if the coil had been removed).
            if (!PowerCoilPatches.IsPowered(obj))
                continue;

            seenTiles.Add(tile);

            if (!this.TicksUntilNextFlash.TryGetValue(tile, out int ticksLeft))
            {
                // stagger a newly-seen coil's first flash so multiple coils don't pulse in lockstep
                this.TicksUntilNextFlash[tile] = Game1.random.Next(PowerCoilAmbientEffect.TicksPerFlash);
                continue;
            }

            ticksLeft--;
            if (ticksLeft <= 0)
            {
                Game1.Multiplayer.broadcastSprites(location, PowerCoilAmbientEffect.CreateSparkle(tile));
                ticksLeft = PowerCoilAmbientEffect.TicksPerFlash;
            }
            this.TicksUntilNextFlash[tile] = ticksLeft;
        }

        // drop tracking for any tile that no longer has a coil there
        if (seenTiles.Count != this.TicksUntilNextFlash.Count)
        {
            foreach (Vector2 tile in new List<Vector2>(this.TicksUntilNextFlash.Keys))
            {
                if (!seenTiles.Contains(tile))
                    this.TicksUntilNextFlash.Remove(tile);
            }
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Create one sparkle sprite at the given tile, with this effect's standard tint/speed/opacity/depth.</summary>
    /// <param name="tile">The tile to center the sparkle on.</param>
    private static TemporaryAnimatedSprite CreateSparkle(Vector2 tile)
    {
        Vector2 position = tile * Game1.tileSize;
        return new TemporaryAnimatedSprite("TileSheets\\animations", new Rectangle(0, 640, 64, 64), PowerCoilAmbientEffect.SparkleInterval, PowerCoilAmbientEffect.SparkleAnimationLength, 0, position, flicker: false, flipped: false)
        {
            alpha = PowerCoilAmbientEffect.SparkleAlpha,
            color = PowerCoilAmbientEffect.SparkleColor,
            layerDepth = (position.Y + 32f) / 10000f - PowerCoilAmbientEffect.BehindCoilLayerDepthOffset
        };
    }
}

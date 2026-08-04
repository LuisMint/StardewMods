using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Loops the same shimmering glint vanilla uses to mark an ore-panning point in a river, as
/// a passive "it's running" ambient effect on every placed Power Relay building — purely cosmetic,
/// unrelated to how many levels it's actually reached. Centered horizontally on the Relay's footprint and
/// 3 tiles up from the bottom of its drawn sprite, per direct user request. Mirrors
/// <see cref="PowerCoilAmbientEffect"/>'s own architecture (re-trigger a fresh flash right as the previous
/// one finishes, scanning only the player's current location each tick), but scans buildings instead of
/// objects. Just the main glint — per direct user request, this deliberately skips vanilla's own
/// occasional secondary sparkle burst around the main glint (see <see cref="GameLocation.updateOrePanAnimation"/>'s
/// own per-tick 5% chance), which read as too much extra layering.
///
/// MOD: the particle's own layerDepth is anchored to the building's GROUND row, not its own (higher up)
/// draw position — Y-sorted drawing in this game means a sprite's layerDepth should reflect where it
/// physically stands, not where it visually appears; using the particle's own elevated Y put it behind
/// the Relay's sprite entirely (confirmed by direct user report) since a smaller Y sorts earlier/further
/// back. Anchoring to the ground row (plus a tiny forward nudge) puts it reliably in front instead.
/// </summary>
internal class PowerRelayAmbientEffect
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the <c>buildingType</c> ID(s) that count as a Power Relay.</summary>
    private readonly Func<HashSet<string>> GetRelayBuildingNames;

    /// <summary>How many tiles up from the bottom of the Relay's drawn sprite the effect is centered, per direct user request.</summary>
    private const float HeightAboveGroundInTiles = 3f;

    /// <summary>How far in front of the Relay's own ground-row layer depth the effect draws, so it isn't sorted behind the building's sprite despite appearing higher up on it.</summary>
    private const float InFrontOfBuildingLayerDepthOffset = 0.0001f;

    /// <summary>The per-frame duration of the glint, in milliseconds — matches vanilla's own ore-panning point animation.</summary>
    private const float GlintInterval = 100f;

    /// <summary>The number of frames in the glint animation — matches vanilla's own ore-panning point animation.</summary>
    private const int GlintAnimationLength = 6;

    /// <summary>The glint's draw scale — matches vanilla's own ore-panning point animation.</summary>
    private const float GlintScale = 3f;

    /// <summary>The source rect for the glint sprite on <c>LooseSprites\Cursors</c> — matches vanilla's own ore-panning point animation.</summary>
    private static readonly Rectangle GlintSourceRect = new(432, 1435, 16, 16);

    /// <summary>How many ticks one full flash takes (at 60 ticks/second) — the next flash is queued to start right as this one finishes, so consecutive flashes read as one continuous loop.</summary>
    private const int TicksPerFlash = (int)(PowerRelayAmbientEffect.GlintInterval * PowerRelayAmbientEffect.GlintAnimationLength * 60 / 1000);

    /// <summary>Ticks remaining until the next flash, keyed by effect tile.</summary>
    private readonly Dictionary<Vector2, int> TicksUntilNextFlash = new();

    /// <summary>The location the tiles above were last scanned in, so state resets cleanly on warp instead of carrying over stale tiles from a different location.</summary>
    private GameLocation? TrackedLocation;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getRelayBuildingNames">Get the <c>buildingType</c> ID(s) that count as a Power Relay.</param>
    public PowerRelayAmbientEffect(Func<HashSet<string>> getRelayBuildingNames)
    {
        this.GetRelayBuildingNames = getRelayBuildingNames;
    }

    /// <summary>Advance the ambient effect by one game tick, for the player's current location.</summary>
    public void Tick()
    {
        // MOD: added — SMAPI's UpdateTicked keeps firing even while the game window is unfocused, but
        // location.temporarySprites' own per-frame cleanup effectively stalls then (confirmed by direct
        // user report, matching vanilla's own ore-panning sparkles doing the same thing) — without this
        // guard, every flash/sparkle queued while unfocused just piles up unseen and then dumps out all
        // at once the moment focus returns. Freezing the whole tick while unfocused means state just
        // resumes exactly where it left off instead, with nothing to catch up on.
        if (!Game1.game1.IsActive)
            return;

        GameLocation? location = Game1.currentLocation;
        if (location == null)
            return;

        if (!ReferenceEquals(location, this.TrackedLocation))
        {
            this.TrackedLocation = location;
            this.TicksUntilNextFlash.Clear();
        }

        HashSet<string> relayBuildingNames = this.GetRelayBuildingNames();
        if (relayBuildingNames.Count == 0)
            return;

        HashSet<Vector2> seenTiles = new();
        foreach (Building building in location.buildings)
        {
            if (!relayBuildingNames.Contains(building.buildingType.Value))
                continue;

            // a Relay still under construction doesn't get the ambient effect yet, matching how it
            // doesn't contribute its bonus yet either (see PowerRelaySystem's own remarks).
            if (building.daysOfConstructionLeft.Value > 0)
                continue;

            Vector2 position = PowerRelayAmbientEffect.GetEffectPosition(building);
            float groundLayerDepth = PowerRelayAmbientEffect.GetGroundLayerDepth(building);
            seenTiles.Add(position);

            if (!this.TicksUntilNextFlash.TryGetValue(position, out int ticksLeft))
            {
                // stagger a newly-seen Relay's first flash so multiple Relays don't pulse in lockstep
                this.TicksUntilNextFlash[position] = Game1.random.Next(PowerRelayAmbientEffect.TicksPerFlash);
                continue;
            }

            ticksLeft--;
            if (ticksLeft <= 0)
            {
                Game1.Multiplayer.broadcastSprites(location, PowerRelayAmbientEffect.CreateGlint(position, groundLayerDepth));
                ticksLeft = PowerRelayAmbientEffect.TicksPerFlash;
            }
            this.TicksUntilNextFlash[position] = ticksLeft;
        }

        // drop tracking for any Relay that's no longer there
        if (seenTiles.Count != this.TicksUntilNextFlash.Count)
        {
            foreach (Vector2 trackedPosition in new List<Vector2>(this.TicksUntilNextFlash.Keys))
            {
                if (!seenTiles.Contains(trackedPosition))
                    this.TicksUntilNextFlash.Remove(trackedPosition);
            }
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get the pixel-space position where the effect is centered — horizontally centered on the Relay's footprint, <see cref="HeightAboveGroundInTiles"/> tiles up from the bottom of its drawn sprite.</summary>
    /// <param name="building">The Power Relay building.</param>
    private static Vector2 GetEffectPosition(Building building)
    {
        float centerX = (building.tileX.Value + building.tilesWide.Value / 2.55f) * Game1.tileSize;
        float groundY = (building.tileY.Value + building.tilesHigh.Value) * Game1.tileSize;
        float y = groundY - PowerRelayAmbientEffect.HeightAboveGroundInTiles * Game1.tileSize * 1.02f;

        return new Vector2(centerX, y);
    }

    /// <summary>Get the layer depth to draw the effect at — anchored to the Relay's own ground row (not the effect's own, higher-up position) plus a small forward nudge, so it draws in front of the Relay's sprite instead of sorting behind it.</summary>
    /// <param name="building">The Power Relay building.</param>
    private static float GetGroundLayerDepth(Building building)
    {
        float groundY = (building.tileY.Value + building.tilesHigh.Value) * Game1.tileSize;
        return groundY / 10000f + PowerRelayAmbientEffect.InFrontOfBuildingLayerDepthOffset;
    }

    /// <summary>Create the main looping glint sprite at the given effect position.</summary>
    /// <param name="position">The pixel-space position to center the glint on.</param>
    /// <param name="layerDepth">The layer depth to draw at (see <see cref="GetGroundLayerDepth"/>).</param>
    private static TemporaryAnimatedSprite CreateGlint(Vector2 position, float layerDepth)
    {
        return new TemporaryAnimatedSprite("LooseSprites\\Cursors", PowerRelayAmbientEffect.GlintSourceRect, position, flipped: false, 0f, Color.White)
        {
            interval = PowerRelayAmbientEffect.GlintInterval,
            scale = PowerRelayAmbientEffect.GlintScale,
            animationLength = PowerRelayAmbientEffect.GlintAnimationLength,
            layerDepth = layerDepth
        };
    }

}

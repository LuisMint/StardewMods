using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: changed. Animates a connector's displayed appearance while it's powered but NOT part of a
/// valid (active) automation group — a "flickering" ping-pong between the powered look and the
/// unpowered look, as a visual cue that the tile is connected to power but isn't actually
/// automating anything (e.g. it has no machine or chest attached). Connectors that are fully
/// unpowered, or powered and part of a valid group, are left as a static appearance by
/// <see cref="PoweredFloorSync"/> instead — this class only touches tiles in the "powered but
/// orphaned" state, so it never fights with that class over the same tile.
///
/// MOD: per direct user request, no longer drives Alternative Textures' own <c>modData</c> keys — see
/// <see cref="Patches.ConnectorTexturePatches"/>'s own remarks for why. The timing/ping-pong logic
/// below is otherwise completely unchanged from the Alternative Textures version, so the animation's
/// actual rhythm (frame timing, hold-on-unpowered pacing) stays identical; only WHAT gets written each
/// frame change is different (this class's own <see cref="Patches.ConnectorTexturePatches.ConnectorVariantModDataKey"/>
/// instead of AT's keys).
///
/// MOD: changed — no longer scans a location's full terrain feature collection itself; <see cref="MachineManager"/>
/// hands over the already-computed "orphaned" connector tile set (built once per rebuild by
/// <see cref="PoweredFloorSync.Sync"/>, which needs to scan every terrain feature anyway to resolve the
/// two static states) instead, so this only ever touches the small set of tiles that actually need to
/// animate — not every crop/path/other terrain feature in the location — even though it still runs up
/// to 6 times a second while the player has the power system enabled.
/// </summary>
internal class PoweredFloorAnimator
{
    /*********
    ** Fields
    *********/
    /// <summary>The appearance variations shown while "powered but not part of a valid group", from most to least powered.</summary>
    private static readonly int[] PoweredToUnpoweredVariations =
    [
        Patches.ConnectorTexturePatches.PoweredVariant,
        Patches.ConnectorTexturePatches.DimmerVariant,
        Patches.ConnectorTexturePatches.DimmestVariant,
        Patches.ConnectorTexturePatches.UnpoweredVariant
    ];

    /// <summary>Get the animation speed, in frames per second.</summary>
    private readonly Func<double> GetFps;

    /// <summary>Get how many times longer to hold the fully-unpowered frame, relative to the other frames.</summary>
    private readonly Func<double> GetUnpoweredHoldMultiplier;

    /// <summary>Ticks elapsed since the last frame change.</summary>
    private int TicksSinceLastFrame;

    /// <summary>The index into <see cref="PoweredToUnpoweredVariations"/> currently being shown, and whether the sequence is currently playing forward (powered → unpowered) or backward.</summary>
    private (int Index, bool Forward) CurrentFrame = (0, true);

    /// <summary>Whether the first frame has been applied yet, so the very first <see cref="Tick"/> call always applies immediately instead of waiting out a full hold period first.</summary>
    private bool HasAppliedFirstFrame;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getFps">Get the animation speed, in frames per second.</param>
    /// <param name="getUnpoweredHoldMultiplier">Get how many times longer to hold the fully-unpowered frame, relative to the other frames.</param>
    public PoweredFloorAnimator(Func<double> getFps, Func<double> getUnpoweredHoldMultiplier)
    {
        this.GetFps = getFps;
        this.GetUnpoweredHoldMultiplier = getUnpoweredHoldMultiplier;
    }

    /// <summary>Advance the animation by one tick, and update any "powered but not part of a valid group" connectors in the given location if the frame changed.</summary>
    /// <param name="location">The location to animate — normally just the current player's location, since this is a purely visual effect.</param>
    /// <param name="orphanedTiles">The location's tiles left "powered but not part of an active group" as of the last rebuild (see <see cref="PoweredFloorSync.Sync"/>), or <c>null</c> if it hasn't been scanned yet.</param>
    public void Tick(GameLocation location, IReadOnlySet<Vector2>? orphanedTiles)
    {
        if (orphanedTiles is null)
            return;

        if (!this.AdvanceFrame())
            return; // frame hasn't changed since the last tick — nothing to update

        if (orphanedTiles.Count == 0)
            return; // nothing in this location needs animating right now

        int variant = PoweredFloorAnimator.PoweredToUnpoweredVariations[this.CurrentFrame.Index];

        foreach (Vector2 tile in orphanedTiles)
        {
            if (location.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature) && feature is Flooring floor)
                floor.modData[Patches.ConnectorTexturePatches.ConnectorVariantModDataKey] = variant.ToString();
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Advance the animation clock by one tick, updating <see cref="CurrentFrame"/> if enough time has passed to move to the next (or previous) frame in the ping-pong sequence.</summary>
    /// <returns>Returns whether the current frame changed.</returns>
    private bool AdvanceFrame()
    {
        int lastIndex = PoweredFloorAnimator.PoweredToUnpoweredVariations.Length - 1;
        bool isUnpoweredFrame = this.CurrentFrame.Index == lastIndex;

        double fps = Math.Max(1, this.GetFps());
        double holdMultiplier = isUnpoweredFrame ? Math.Max(1, this.GetUnpoweredHoldMultiplier()) : 1;
        int holdTicks = Math.Max(1, (int)Math.Round(60 / fps * holdMultiplier));

        if (++this.TicksSinceLastFrame < holdTicks)
            return !this.HasAppliedFirstFrame; // no change yet, unless nothing's ever been applied

        this.TicksSinceLastFrame = 0;

        // ping-pong: bounce between the first and last frame instead of looping back to the start
        if (this.CurrentFrame.Forward)
        {
            if (this.CurrentFrame.Index >= lastIndex)
                this.CurrentFrame = (lastIndex - 1, false);
            else
                this.CurrentFrame = (this.CurrentFrame.Index + 1, true);
        }
        else
        {
            if (this.CurrentFrame.Index <= 0)
                this.CurrentFrame = (1, true);
            else
                this.CurrentFrame = (this.CurrentFrame.Index - 1, false);
        }

        this.HasAppliedFirstFrame = true;
        return true;
    }
}

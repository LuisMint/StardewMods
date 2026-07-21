using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Animates a connector's displayed appearance while it's powered but NOT part of a
/// valid (active) automation group — a "flickering" ping-pong between the powered look and the
/// unpowered look, as a visual cue that the tile is connected to power but isn't actually
/// automating anything (e.g. it has no machine or chest attached). Connectors that are fully
/// unpowered, or powered and part of a valid group, are left as a static appearance by
/// <see cref="PoweredFloorSync"/> instead — this class only touches tiles in the "powered but
/// orphaned" state, so it never fights with that class over the same tile.
///
/// Only the current location is animated (matching what's actually visible to the player), and the
/// underlying tiles are only re-scanned when the animation frame actually changes — for the default
/// 6fps/10-tick-per-frame timing, that's roughly 6 times a second rather than all 60 ticks — so this
/// stays cheap even on maps with hundreds of unrelated floor tiles.
/// </summary>
internal class PoweredFloorAnimator
{
    /*********
    ** Fields
    *********/
    /// <summary>The appearance variations shown while "powered but not part of a valid group", from most to least powered.</summary>
    private static readonly int[] PoweredToUnpoweredVariations = [PoweredFloorSync.PoweredVariation, 2, 3, PoweredFloorSync.UnpoweredVariation];

    /// <summary>Get the configured mapping of a connector's floor ID to the Alternative Textures texture ID (in the form <c>{Owner}.{ModelName}</c>) providing its appearance variations.</summary>
    private readonly Func<Dictionary<string, string>> GetConnectorTextureIds;

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
    /// <param name="getConnectorTextureIds">Get the configured mapping of a connector's floor ID to the Alternative Textures texture ID providing its appearance variations.</param>
    /// <param name="getFps">Get the animation speed, in frames per second.</param>
    /// <param name="getUnpoweredHoldMultiplier">Get how many times longer to hold the fully-unpowered frame, relative to the other frames.</param>
    public PoweredFloorAnimator(Func<Dictionary<string, string>> getConnectorTextureIds, Func<double> getFps, Func<double> getUnpoweredHoldMultiplier)
    {
        this.GetConnectorTextureIds = getConnectorTextureIds;
        this.GetFps = getFps;
        this.GetUnpoweredHoldMultiplier = getUnpoweredHoldMultiplier;
    }

    /// <summary>Advance the animation by one tick, and update any "powered but not part of a valid group" connectors in the given location if the frame changed.</summary>
    /// <param name="location">The location to animate — normally just the current player's location, since this is a purely visual effect.</param>
    /// <param name="data">The location's tracked machine data, or <c>null</c> if it hasn't been scanned yet.</param>
    public void Tick(GameLocation location, MachineDataForLocation? data)
    {
        Dictionary<string, string> connectorTextureIds = this.GetConnectorTextureIds();
        if (connectorTextureIds.Count == 0 || data is null)
            return;

        if (!this.AdvanceFrame())
            return; // frame hasn't changed since the last tick — nothing to update

        int variation = PoweredFloorAnimator.PoweredToUnpoweredVariations[this.CurrentFrame.Index];

        foreach ((Vector2 tile, TerrainFeature feature) in location.terrainFeatures.Pairs)
        {
            if (feature is not Flooring floor)
                continue;

            if (!connectorTextureIds.TryGetValue(floor.whichFloor.Value, out string? textureId))
                continue;

            bool isPowered = data.PoweredTiles == null || data.PoweredTiles.Contains(tile);
            if (!isPowered || data.ActiveTiles.ContainsKey(tile))
                continue; // static state — PoweredFloorSync owns this tile instead

            floor.modData["AlternativeTextureName"] = textureId;
            floor.modData["AlternativeTextureVariation"] = variation.ToString();
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

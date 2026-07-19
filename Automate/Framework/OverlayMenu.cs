using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>The overlay which highlights automatable machines.</summary>
internal class OverlayMenu : BaseOverlay
{
    /*********
    ** Fields
    *********/
    /// <summary>The padding to apply to tile backgrounds to make the grid visible.</summary>
    private readonly int TileGap = 1;

    /// <summary>The unique key for the current location.</summary>
    private readonly string LocationKey;

    /// <summary>The machine data for the current location.</summary>
    private readonly MachineDataForLocation? MachineData;

    /// <summary>The machine group for machines connected to Junimo chests.</summary>
    private readonly JunimoMachineGroup JunimoGroup;

    /// <summary>MOD: added. The color used to highlight a tile that belongs to more than one active machine group at once (e.g. a chest or machine shared between two separate path networks).</summary>
    private static readonly Color MultiGroupColor = Color.Blue;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="events">The SMAPI events available for mods.</param>
    /// <param name="inputHelper">An API for checking and changing input state.</param>
    /// <param name="reflection">Simplifies access to private code.</param>
    /// <param name="locationKey">The unique key for the current location.</param>
    /// <param name="machineData">The machine groups to display.</param>
    /// <param name="junimoGroup">The machine group for machines connected to Junimo chests.</param>
    public OverlayMenu(IModEvents events, IInputHelper inputHelper, IReflectionHelper reflection, string locationKey, MachineDataForLocation? machineData, JunimoMachineGroup junimoGroup)
        : base(events, inputHelper, reflection)
    {
        this.LocationKey = locationKey;
        this.MachineData = machineData;
        this.JunimoGroup = junimoGroup;
    }


    /*********
    ** Protected
    *********/
    /// <inheritdoc />
    [SuppressMessage("ReSharper", "PossibleLossOfFraction", Justification = "Deliberate discarded for conversion to tile coordinates.")]
    protected override void DrawWorld(SpriteBatch spriteBatch)
    {
        if (!Context.IsPlayerFree)
            return;

        // draw each tile
        IReadOnlySet<Vector2> junimoChestTiles = this.JunimoGroup.GetTiles(this.LocationKey);
        foreach (Vector2 tile in TileHelper.GetVisibleTiles(expand: 1))
        {
            // get tile's screen coordinates
            float screenX = tile.X * Game1.tileSize - Game1.viewport.X;
            float screenY = tile.Y * Game1.tileSize - Game1.viewport.Y;
            int tileSize = Game1.tileSize;

            // get machine group
            IMachineGroup? group = null;
            Color? color = null;
            bool isMultiGroup = false; // MOD: added
            if (junimoChestTiles.Contains(tile))
            {
                color = this.JunimoGroup.HasInternalAutomation
                    ? Color.Green * 0.2f
                    : Color.Red * 0.2f;
                group = this.JunimoGroup;
            }
            else if (this.MachineData is not null)
            {
                if (this.MachineData.ActiveTiles.TryGetValue(tile, out group))
                {
                    color = Color.Green * 0.2f;

                    // MOD: added — check whether this tile is a member of more than one active
                    // machine group (e.g. a chest or machine shared between two separate path
                    // networks). If so, flag it for the solid full-tile highlight drawn below,
                    // instead of the normal grid-gapped green background.
                    if (this.MachineData.ActiveGroupsByTile.TryGetValue(tile, out IMachineGroup[]? allGroups) && allGroups.Length > 1)
                        isMultiGroup = true;
                }
                else if (this.MachineData.DisabledTiles.TryGetValue(tile, out group) || this.MachineData.OutdatedTiles.ContainsKey(tile))
                    color = Color.Red * 0.2f;
            }
            color ??= Color.Black * 0.5f;

            // draw background
            spriteBatch.DrawLine(screenX + this.TileGap, screenY + this.TileGap, new Vector2(tileSize - this.TileGap * 2, tileSize - this.TileGap * 2), color);

            // MOD: added — draw a solid, full (no grid gap) blue square directly over a tile that
            // belongs to more than one active group, so it's unmistakable regardless of whatever
            // object is drawn on top of it.
            if (isMultiGroup)
                spriteBatch.DrawLine(screenX, screenY, new Vector2(tileSize, tileSize), OverlayMenu.MultiGroupColor * 0.45f);

            // draw group edge borders
            if (group != null)
            {
                Color borderColor = isMultiGroup
                    ? OverlayMenu.MultiGroupColor
                    : (group.HasInternalAutomation ? Color.Green : Color.Red);

                this.DrawEdgeBorders(spriteBatch, group, tile, borderColor);
            }
        }

        // draw cursor
        this.DrawCursor();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Draw borders for each unconnected edge of a tile.</summary>
    /// <param name="spriteBatch">The sprite batch being drawn.</param>
    /// <param name="group">The machine group.</param>
    /// <param name="tile">The group tile.</param>
    /// <param name="color">The border color.</param>
    private void DrawEdgeBorders(SpriteBatch spriteBatch, IMachineGroup group, Vector2 tile, Color color)
    {
        int borderSize = 3;
        float screenX = tile.X * Game1.tileSize - Game1.viewport.X;
        float screenY = tile.Y * Game1.tileSize - Game1.viewport.Y;
        float tileSize = Game1.tileSize;

        IReadOnlySet<Vector2> tiles = group.GetTiles(this.LocationKey);

        // top
        if (!tiles.Contains(new Vector2(tile.X, tile.Y - 1)))
            spriteBatch.DrawLine(screenX, screenY, new Vector2(tileSize, borderSize), color); // top

        // bottom
        if (!tiles.Contains(new Vector2(tile.X, tile.Y + 1)))
            spriteBatch.DrawLine(screenX, screenY + tileSize, new Vector2(tileSize, borderSize), color); // bottom

        // left
        if (!tiles.Contains(new Vector2(tile.X - 1, tile.Y)))
            spriteBatch.DrawLine(screenX, screenY, new Vector2(borderSize, tileSize), color); // left

        // right
        if (!tiles.Contains(new Vector2(tile.X + 1, tile.Y)))
            spriteBatch.DrawLine(screenX + tileSize, screenY, new Vector2(borderSize, tileSize), color); // right
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework.Patches;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>The overlay which highlights automatable machines.</summary>
internal class OverlayMenu : BaseOverlay
{
    /*********
    ** Fields
    *********/
    /// <summary>MOD: changed from TileGap (which shrank tiles to leave a gap) to TileOverlap (which grows tiles slightly beyond their bounds). Adjacent tiles' fills now overlap a little at the shared edge; the overlap darkens due to stacked opacity, producing a subtle grid-line effect instead of a plain empty gap.</summary>
    private const int TileOverlap = 1;

    /// <summary>The unique key for the current location.</summary>
    private readonly string LocationKey;

    /// <summary>The machine data for the current location.</summary>
    private readonly MachineDataForLocation? MachineData;

    /// <summary>MOD: added. The color used to highlight a tile (machine or chest) that belongs to more than one active machine group at once (e.g. a chest or machine shared between two separate path networks).</summary>
    private static readonly Color MultiGroupColor = Color.Purple;

    /// <summary>MOD: added. The color used for connector tiles whose network is input-only (chests can only be pulled from, never stored into) — a warm orange leaning toward red.</summary>
    private static readonly Color InputOnlyConnectorColor = new(235, 80, 20);

    /// <summary>MOD: added. The color used for connector tiles whose network is output-only (chests can only be stored into, never pulled from).</summary>
    private static readonly Color OutputOnlyConnectorColor = Color.Blue;

    /// <summary>MOD: added. The color used for disabled tiles — a darker, less vibrant red than the plain <see cref="Color.Red"/> used elsewhere.</summary>
    private static readonly Color DisabledColor = new(120, 30, 30);

    /// <summary>MOD: added. The color used for tiles outside every power source's range when the power system is enabled — deliberately a different, darker/more saturated red than <see cref="DisabledColor"/> so the two are visually distinct.</summary>
    private static readonly Color UnpoweredColor = new(45, 0, 0);

    /// <summary>MOD: added. The fill opacity for unpowered tiles — deliberately higher than the normal black background opacity, so out-of-range areas read as more solid/darker rather than just tinted.</summary>
    private const float UnpoweredFillOpacity = 0.75f;

    /// <summary>MOD: added. The background fill opacity for normal (single-role) tile highlights.</summary>
    private const float NormalFillOpacity = 0.3f;

    /// <summary>MOD: added. The background fill opacity for connector-role tile highlights.</summary>
    private const float ConnectorRoleFillOpacity = 0.35f;

    /// <summary>MOD: added. The fill opacity for the solid full-tile multi-group highlight.</summary>
    private const float MultiGroupFillOpacity = 0.55f;

    /// <summary>MOD: added. The thickness in pixels of the group edge border lines.</summary>
    private const int BorderSize = 5;

    /// <summary>MOD: added. The fill opacity for the white/black sign-detection debug marker.</summary>
    private const float SignMarkerFillOpacity = 0.65f;

    /// <summary>MOD: added. The size of the white/black sign-detection debug marker, as a fraction of the full tile size — drawn smaller (and centered) than the tile so the connector-role color underneath stays visible around its edges too, not just through its own translucency.</summary>
    private const float SignMarkerSizeScale = 0.65f;

    /// <summary>MOD: added. The size of the power-coil grid-usage marker, as a fraction of the full tile size.</summary>
    private const float PowerCoilMarkerSizeScale = 0.4f; //1f / 3f;

    /// <summary>MOD: added. The fill opacity for the power-coil grid-usage marker — deliberately more solid than the tile's own background fill, so it reads as a clear marker rather than another translucent tint layered on top.</summary>
    private const float PowerCoilMarkerFillOpacity = 0.85f;

    /// <summary>MOD: added. The draw scale for the power-coil grid-usage marker's "placed/capacity" text — <see cref="Game1.tinyFont"/> at its natural size is still too large to comfortably fit inside a marker only a third of a tile wide.</summary>
    private const float PowerCoilMarkerTextScale = 0.6f;

    /// <summary>MOD: added. The Power Silo capacity system — used to check whether the mechanic is enabled (see <see cref="PowerSiloSystem.IsEnabled"/>) and to look up each individual Power Coil's own rank/the grid's total capacity for the marker's text. Reading a coil's rank/capacity is cheap (a modData lookup and a buildings-only scan respectively — see <see cref="PowerSiloSystem.GetCoilRank"/>/<see cref="PowerSiloSystem.GetTotalCapacity"/>), so unlike the marker's color (below), this is looked up fresh every draw call rather than cached at construction.</summary>
    private readonly PowerSiloSystem PowerSilo;

    /// <summary>MOD: added. The item names/IDs that count as a Power Coil for the grid-usage marker.</summary>
    private readonly HashSet<string> PowerCoilSourceNames;

    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="events">The SMAPI events available for mods.</param>
    /// <param name="inputHelper">An API for checking and changing input state.</param>
    /// <param name="reflection">Simplifies access to private code.</param>
    /// <param name="locationKey">The unique key for the current location.</param>
    /// <param name="machineData">The machine groups to display — its lookups already fold in any Junimo-touching local group with its own real automation (see <see cref="MachineDataForLocation"/>'s own remarks), so a Junimo chest is drawn through the exact same logic as any other chest below, with no special-casing needed.</param>
    /// <param name="powerSilo">MOD: added. The Power Silo capacity system.</param>
    /// <param name="powerCoilSourceNames">MOD: added. The item names/IDs that count as a Power Coil for the grid-usage marker.</param>
    public OverlayMenu(IModEvents events, IInputHelper inputHelper, IReflectionHelper reflection, string locationKey, MachineDataForLocation? machineData, PowerSiloSystem powerSilo, HashSet<string> powerCoilSourceNames)
        : base(events, inputHelper, reflection)
    {
        this.LocationKey = locationKey;
        this.MachineData = machineData;
        this.PowerSilo = powerSilo;
        this.PowerCoilSourceNames = powerCoilSourceNames;
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

        // MOD: added — collect border info per tile during the background pass, so ALL borders can
        // be drawn in a separate pass afterward. Previously each tile's border was drawn immediately
        // after its own background, in the same scan; a neighboring tile's plain background (drawn
        // later in the same left-to-right, top-to-bottom scan) could then visually paint over part
        // of an already-drawn border at their shared edge. Drawing every background first, then
        // every border on top, guarantees borders are never covered.
        List<(Vector2 Tile, IMachineGroup Group, Color BorderColor)> borderQueue = new();

        // MOD: added — same reasoning as borderQueue above: the power-coil marker's text needs to render
        // on top of the group borders too (borders are drawn in their own pass, after every tile's
        // background/marker fill — see pass 2 below), so it's queued here and drawn in a final pass
        // after borders, instead of immediately alongside the marker's own fill square.
        List<(Vector2 Position, string Text, SpriteFont Font, float Scale)> textQueue = new();

        // pass 1: backgrounds
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
            Color? connectorRoleColor = null; // MOD: added
            bool? isWhitelistSignMarker = null; // MOD: added
            if (this.MachineData is not null)
            {
                if (this.MachineData.ActiveTiles.TryGetValue(tile, out group))
                {
                    color = Color.Green * OverlayMenu.NormalFillOpacity;

                    // MOD: added — check whether this tile is a member of more than one active
                    // machine group (e.g. a chest or machine shared between two separate path
                    // networks). If so, flag it for the solid full-tile purple highlight drawn below.
                    if (this.MachineData.ActiveGroupsByTile.TryGetValue(tile, out IMachineGroup[]? allGroups) && allGroups.Length > 1)
                        isMultiGroup = true;

                    // MOD: added — check whether this tile is a connector with a specific role
                    // (input-only or output-only). "Both" role connectors just stay the default green.
                    if (this.MachineData.ConnectorRolesByTile.TryGetValue(tile, out ConnectorRole role))
                    {
                        connectorRoleColor = role switch
                        {
                            ConnectorRole.ChestInputOnly => OverlayMenu.InputOnlyConnectorColor,
                            ConnectorRole.ChestOutputOnly => OverlayMenu.OutputOnlyConnectorColor,
                            _ => null // Both — stays default green
                        };

                        if (connectorRoleColor.HasValue)
                            color = connectorRoleColor.Value * OverlayMenu.ConnectorRoleFillOpacity;
                    }

                    // MOD: added — debug marker for a tile where a configured whitelist/blacklist sign
                    // was detected, regardless of connector role or whether the sign currently holds an
                    // item. White = whitelist sign detected, black = blacklist sign detected. This
                    // exists purely so it's visually obvious whether sign detection is matching at all.
                    // MOD: changed — drawn as a separate translucent layer ON TOP of the tile's own
                    // fill (see the draw call below) rather than replacing `color` outright, so the
                    // connector-role color underneath (green/orange/blue) stays visible instead of
                    // being fully hidden behind a solid black/white square.
                    if (this.MachineData.SignMarkersByTile.TryGetValue(tile, out bool isWhitelistSign))
                        isWhitelistSignMarker = isWhitelistSign;
                }
                else if (this.MachineData.DisabledTiles.TryGetValue(tile, out group) || this.MachineData.OutdatedTiles.ContainsKey(tile))
                {
                    // MOD: changed — only use the normal disabled-red FILL if this tile is actually
                    // powered (or the power system is off). If it's out of range, leave `color` as
                    // null so the power-aware fallback below applies (dark red) instead — but
                    // `group` is still set above, so the usual disabled-style outline border still
                    // gets drawn on top of it. This way a viable-but-unpowered machine/chest still
                    // looks recognizably "there" (an outline on a dark-red tile) instead of either
                    // vanishing into a featureless background or looking identical to a normal
                    // powered-but-disconnected machine.
                    bool isPowered = this.MachineData.PoweredTiles == null || this.MachineData.PoweredTiles.Contains(tile);
                    if (isPowered)
                        color = OverlayMenu.DisabledColor * OverlayMenu.NormalFillOpacity;
                }
            }
            // MOD: added — for tiles with no group/connector/sign info above, fall back to a
            // power-aware default instead of always plain black: if the power system is enabled,
            // show dark red for tiles outside every power source's range, and black for tiles that
            // are powered but have nothing else going on. If the power system is disabled, keep the
            // original plain black default (unrestricted, nothing to visualize).
            if (color == null)
            {
                if (this.MachineData?.PoweredTiles == null)
                    color = Color.Black * 0.5f;
                else if (this.MachineData.PoweredTiles.Contains(tile))
                    color = Color.Black * 0.5f;
                else
                    color = OverlayMenu.UnpoweredColor * OverlayMenu.UnpoweredFillOpacity;
            }

            // draw background
            // MOD: drawn slightly LARGER than the tile itself (instead of shrunk with a gap), so
            // adjacent tiles' fills overlap a little at the edges — see TileOverlap field comment.
            spriteBatch.DrawLine(
                screenX - OverlayMenu.TileOverlap,
                screenY - OverlayMenu.TileOverlap,
                new Vector2(tileSize + OverlayMenu.TileOverlap * 2, tileSize + OverlayMenu.TileOverlap * 2),
                color
            );

            // draw the solid full-tile multi-group highlight now (still part of the background pass)
            if (isMultiGroup)
                spriteBatch.DrawLine(screenX, screenY, new Vector2(tileSize, tileSize), OverlayMenu.MultiGroupColor * OverlayMenu.MultiGroupFillOpacity);

            // MOD: added — draw the sign-detection marker as a translucent layer on top of the tile's
            // own fill (instead of replacing it) so the connector-role color underneath stays visible,
            // and shrunk to a centered square smaller than the full tile so that color shows around its
            // edges too, not just through its own translucency.
            if (isWhitelistSignMarker.HasValue)
            {
                float signMarkerSize = tileSize * OverlayMenu.SignMarkerSizeScale;
                float signMarkerOffset = (tileSize - signMarkerSize) / 2f;
                spriteBatch.DrawLine(
                    screenX + signMarkerOffset,
                    screenY + signMarkerOffset,
                    new Vector2(signMarkerSize, signMarkerSize),
                    (isWhitelistSignMarker.Value ? Color.White : Color.Black) * OverlayMenu.SignMarkerFillOpacity
                );
            }

            // MOD: added — draw a small red/green marker over any tile with a power coil, showing
            // whether it's currently within the power grid's capacity (see
            // PowerSiloSystem.RefreshCoilAllowance), plus that SPECIFIC coil's own "rank/capacity" as
            // white text floating just above the tile (e.g. "9/8" vs "3/8" for two different coils under
            // the same capacity — the rank tells you which one would turn on next if capacity increased
            // by one, which a single grid-wide total can't). Drawn as its own layer on top of everything
            // above (same approach as the sign-detection marker) so it stays visible regardless of the
            // tile's own fill/role color.
            if (this.PowerSilo.IsEnabled
                && this.PowerCoilSourceNames.Count > 0
                && Game1.currentLocation.Objects.TryGetValue(tile, out SObject? coil)
                && (this.PowerCoilSourceNames.Contains(coil.QualifiedItemId) || this.PowerCoilSourceNames.Contains(coil.Name)))
            {
                float markerSize = tileSize * OverlayMenu.PowerCoilMarkerSizeScale;
                float markerOffset = (tileSize - markerSize) / 2f;
                Color markerColor = PowerCoilPatches.IsPowered(coil) ? Color.Green : Color.Red;

                spriteBatch.DrawLine(
                    screenX + markerOffset,
                    screenY + markerOffset,
                    new Vector2(markerSize, markerSize),
                    markerColor * OverlayMenu.PowerCoilMarkerFillOpacity
                );

                int? rank = this.PowerSilo.GetCoilRank(coil);
                int capacity = this.PowerSilo.GetTotalCapacity();
                string usageText = rank.HasValue ? $"{rank}/{capacity}" : $"?/{capacity}";

                // MOD: fixed — measure with the SAME font actually drawn below. Measuring with one font
                // but drawing with another (e.g. after swapping fonts) gives a bounding box that doesn't
                // match the real rendered size, so the centering offset undershoots/overshoots and the
                // text appears to start at the center and run off to one side instead of being centered.
                SpriteFont font = Game1.dialogueFont;
                Vector2 textSize = font.MeasureString(usageText) * OverlayMenu.PowerCoilMarkerTextScale;

                // MOD: queued instead of drawn immediately — see comment above textQueue. Centered in the
                // middle of the tile (not the small marker square, which is too cramped to hold it).
                textQueue.Add((
                    new Vector2(screenX + tileSize / 2f - textSize.X / 2f, screenY + tileSize / 2f - textSize.Y / 2f),
                    usageText,
                    font,
                    OverlayMenu.PowerCoilMarkerTextScale
                ));
            }

            // MOD: queue the border instead of drawing it immediately — see comment above borderQueue
            if (group != null)
            {
                Color borderColor;
                if (isMultiGroup)
                    borderColor = OverlayMenu.MultiGroupColor;
                else if (connectorRoleColor.HasValue)
                    borderColor = connectorRoleColor.Value;
                else
                    // MOD: uses HasLocalInternalAutomation rather than HasInternalAutomation — for a
                    // Junimo-touching group, HasInternalAutomation is unconditionally true (it's the
                    // farm-wide "should this be processed at all" signal), which drew a green border
                    // around a Junimo chest with no local automation even though its FILL (driven by
                    // MachineDataForLocation's own HasLocalInternalAutomation-based bucketing, a few
                    // lines up) already correctly showed it as disabled. See MachineGroup's own
                    // remarks for why HasLocalInternalAutomation is identical to HasInternalAutomation
                    // for a non-Junimo group, so this doesn't change anything for the common case.
                    borderColor = group.HasLocalInternalAutomation ? Color.Green : OverlayMenu.DisabledColor;

                borderQueue.Add((tile, group, borderColor));
            }
        }

        // pass 2: borders — drawn after every background (including all neighbors), so they always render on top
        foreach ((Vector2 tile, IMachineGroup group, Color borderColor) in borderQueue)
            this.DrawEdgeBorders(spriteBatch, group, tile, borderColor);

        // pass 3: power-coil marker text — drawn after borders, so it always renders on top of them instead of underneath
        foreach ((Vector2 position, string text, SpriteFont font, float scale) in textQueue)
            spriteBatch.DrawString(font, text, position, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);

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
        int borderSize = OverlayMenu.BorderSize;
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

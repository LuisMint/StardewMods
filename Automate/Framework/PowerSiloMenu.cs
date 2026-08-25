using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Automate.Framework.Patches;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. The Power Silo's status menu, opened by <see cref="PowerSiloInteraction"/> when the
/// player clicks it without delivering its currently-requested item — modeled on vanilla's own
/// <see cref="PondQueryMenu"/> (title tag overlapping the box's top border, one continuous dialogue box
/// with an internal partition line separating the main content from the bring-request strip, and a
/// stack of buttons along the right edge), but scoped to what a Power Silo actually needs to show:
/// capacity usage and its next upgrade requirement. The two side buttons largely mirror
/// <see cref="PondQueryMenu.changeNettingButton"/>/<see cref="PondQueryMenu.emptyButton"/>'s
/// positions/icons; <see cref="MarkAllCoilsButton"/> toggles the "Power Coil markers" compass/arrow
/// overlay (see <see cref="PowerCoilCompass"/>) and <see cref="ChangeAppearanceButton"/> — despite its
/// field name, a holdover from when it was a placeholder — now toggles showing every Power Coil on the
/// world map instead (see <see cref="Patches.PowerCoilMapMarkerPatches"/>); these two used to share a
/// single toggle/button and are now fully independent, for clearer per-feature control.
///
/// The icon grid represents THIS Silo's own progress (one slot per point of the max any single Silo
/// can contribute, filled up to its current tier) rather than the save-wide total, so it stays a
/// small, fixed-size grid regardless of how many other Silos exist.
///
/// MOD: laid out as two vertical zones within ONE dialogue box (matching PondQueryMenu's own structure
/// — see its <c>UpdateState</c>/<c>draw</c> — rather than two separate boxes): the main zone (capacity
/// lines + grid + flavor text) and, only when there's a next tier, the bring-request zone below a
/// horizontal partition. Each zone's height is its own content plus a clearance on both sides — bigger
/// wherever that side borders the box's own frame (<see cref="BodyTopMargin"/>/<see cref="QuestBottomMargin"/>)
/// than where it only borders the partition line (<see cref="BodyBottomMargin"/>/<see cref="QuestTopMargin"/>), since the border
/// art itself needs clearing but the partition doesn't. <see cref="ComputeHeight"/> mirrors this exact
/// math so the menu is sized and centered on screen before anything is drawn.
/// </summary>
internal class PowerSiloMenu : IClickableMenu
{
    /*********
    ** Fields
    *********/
    /// <summary>The menu's fixed width, matching <see cref="PondQueryMenu.width"/>.</summary>
    private const int MenuWidth = 400;//384;

    /// <summary>The vertical space from the menu's own top to where the dialogue box starts — the title tag is drawn overlapping the box's top border (see <see cref="draw"/>), the same way <see cref="PondQueryMenu"/> positions its own name tag.</summary>
    private const int TitleZoneHeight = 128;

    /// <summary>
    /// Four independent margins to hand-tune the layout — body (main zone, above the partition line)
    /// top/bottom, then quest (bring zone, below the line) top/bottom:
    /// <code>
    /// [title tag]
    /// ─ BodyTopMargin ─
    /// [silo capacity / power grid / battery grid / flavor text]
    /// ─ BodyBottomMargin ─
    /// ───── partition line ─────
    /// ─ QuestTopMargin ─
    /// [bring row]
    /// ─ QuestBottomMargin ─
    /// </code>
    /// <see cref="BodyTopMargin"/> and <see cref="QuestBottomMargin"/> border the box's OWN top/bottom
    /// edge, where <see cref="Game1.drawDialogueBox(int,int,int,int,bool,bool,string,bool,bool,int,int,int)"/>'s
    /// fixed 64x64 corner/edge tiles live — going below 64 there lets content/corners overlap the border
    /// art (and the box's corners visibly collapse if it's shorter than 128 total). <see cref="BodyTopMargin"/>
    /// also has to clear the title tag itself, which overlaps that same edge, hence its bigger default.
    /// <see cref="BodyBottomMargin"/> and <see cref="QuestTopMargin"/> only border the partition line
    /// (no border art there), so they're free to go much smaller. If there's no next tier (no quest
    /// zone at all), <see cref="BodyBottomMargin"/> becomes the main zone's ONLY bottom clearance and
    /// borders the box's bottom edge directly — keep it at least 64 too if that state matters to you.
    /// </summary>
    private const int BodyTopMargin = 120;

    /// <summary>See <see cref="BodyTopMargin"/>'s remarks.</summary>
    private const int BodyBottomMargin = 0;// //24;

    /// <summary>See <see cref="BodyTopMargin"/>'s remarks.</summary>
    private const int QuestTopMargin = 64;

    /// <summary>See <see cref="BodyTopMargin"/>'s remarks.</summary>
    private const int QuestBottomMargin = 64;

    /// <summary>The vertical advance per single-line text element.</summary>
    private const int LineHeight = 32;

    /// <summary>The small gap between distinct elements within the main zone (e.g. between the text lines and the icon grid).</summary>
    private const int ElementGap = 10;

    /// <summary>The height of one bring-request row's content — dictated by the item icon (a 16x16 sprite at 4x zoom).</summary>
    private const int BringRowHeight = 64;

    /// <summary>The vertical gap between consecutive bring-request rows, when a tier has more than one item still outstanding.</summary>
    private const int BringRowGap = 12;

    /// <summary>How many capacity icons to draw per row — a tidy 4-column grid, since the icon count is this Silo's own max (a small, fixed number from the tier config) rather than the potentially-large save-wide total.</summary>
    private const int IconsPerRow = 5;

    /// <summary>The vertical (and horizontal) spacing between icon slots, before the 4x zoom multiplier — matches <see cref="PondQueryMenu"/>'s own <c>slot_spacing</c>.</summary>
    private const float IconSlotSpacing = 13f;

    /// <summary>The qualified item ID drawn in the capacity grid — a Battery Pack, standing in for "stored power" rather than the Power Coil itself.</summary>
    private const string CapacityIconQualifiedItemId = "(O)787";

    /// <summary>MOD: added. The qualified item ID drawn in the solar-tier icon cluster (see <see cref="draw"/>'s <c>isSolarTier</c> branch) once a Silo reaches the terminal solar tier — a Solar Panel, standing in for the connected-panel bonus rather than the flat battery grid.</summary>
    private const string SolarPanelQualifiedItemId = "(BC)231";

    /// <summary>MOD: added. How many Solar Panel icons to draw in the solar-tier cluster — matches <see cref="PowerSiloSystem.SolarPanelsPerCapacityPoint"/> (the icons represent one full set), close together, per the design (the last one carries the "x{count}" badge).</summary>
    private const int SolarClusterIconCount = PowerSiloSystem.SolarPanelsPerCapacityPoint;

    /// <summary>MOD: added. The draw scale for each icon in the solar-tier cluster.</summary>
    private const float SolarClusterIconScale = 2.25f;

    /// <summary>MOD: added. The gap between consecutive icons in the solar-tier cluster — small, so they read as "close to each other" per the design.</summary>
    private const float SolarClusterIconGap = 4f;

    /// <summary>MOD: added. Nudges the solar-tier icon cluster up slightly within its row, since the smaller <see cref="SolarClusterIconScale"/> otherwise sits a bit low compared to the old full-size battery grid it replaces.</summary>
    private const float SolarClusterIconYOffset = -8f;

    /// <summary>MOD: added. How fast the "next tier" slots pulse between a full black tint and no tint at all, in radians per second.</summary>
    private const float NextTierPulseSpeed = 3f;

    /// <summary>MOD: added. The asset name of <see cref="MarkAllCoilsButton"/>'s own icon (loaded by the PoweredAutomation content pack), replacing the placeholder vanilla mouseCursors icon it used before this button became a real toggle.</summary>
    private const string MarkPowerCoilsIconAssetName = "Mods/luisMint.PoweredAutomation/MarkPowerCoilsIcon";

    /// <summary>The Power Silo this menu displays.</summary>
    private readonly Building Silo;

    /// <summary>The power silo capacity system, used to read usage/tier state.</summary>
    private readonly PowerSiloSystem PowerSiloSystem;

    /// <summary>The ordered capacity tiers a Power Silo progresses through.</summary>
    private readonly List<PowerSiloTierConfig> Tiers;

    /// <summary>A throwaway Battery Pack instance used purely to draw its icon in the capacity grid — never placed, never touched by any world-state code.</summary>
    private readonly SObject CapacityIcon;

    /// <summary>The button that closes this menu.</summary>
    private readonly ClickableTextureComponent OkButton;

    /// <summary>MOD: added. "Show/Hide Power Coils on map" — toggles <see cref="PowerCoilMapMarkerPatches.ShowMapMarkers"/>, marking every Power Coil's location on the world map. Field name is a holdover from when this was a placeholder "change appearance" button.</summary>
    private readonly ClickableTextureComponent ChangeAppearanceButton;

    /// <summary>MOD: added. "Show/Hide Power Coil markers" — toggles <see cref="PowerCoilCompass.ShowCompass"/>, showing compass arrows toward every Power Coil in the player's current location (plus a yellow/red tint on the coils themselves), independently of <see cref="ChangeAppearanceButton"/>'s own world-map toggle.</summary>
    private readonly ClickableTextureComponent MarkAllCoilsButton;

    /// <summary>The text currently hovered, shown as a tooltip.</summary>
    private string HoverText = "";

    /// <summary>MOD: added. The "Bring" row requirement currently under the mouse, if any — shown as a full vanilla item tooltip (name, description, etc.) in <see cref="draw"/>, since the icon alone doesn't say which item it is (especially for a randomly-rolled mineral) and this menu's custom-drawn icons aren't real inventory slots Lookup Anything or similar mods can inspect.</summary>
    private PowerSiloRequiredItem? HoveredBringRowRequirement;

    /// <summary>MOD: added. Elapsed seconds since this menu opened — drives the capacity grid's subtle bob and pulse animations, the same way <see cref="PondQueryMenu"/>'s own <c>_age</c> field drives its fish icons' bob.</summary>
    private float AnimationTimer;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="silo">The Power Silo this menu displays.</param>
    /// <param name="powerSiloSystem">The power silo capacity system, used to read usage/tier state.</param>
    /// <param name="tiers">The ordered capacity tiers a Power Silo progresses through.</param>
    public PowerSiloMenu(Building silo, PowerSiloSystem powerSiloSystem, List<PowerSiloTierConfig> tiers)
        : base(
            Game1.uiViewport.Width / 2 - PowerSiloMenu.MenuWidth / 2,
            Game1.uiViewport.Height / 2 - PowerSiloMenu.ComputeHeight(silo, powerSiloSystem, tiers) / 2,
            PowerSiloMenu.MenuWidth,
            PowerSiloMenu.ComputeHeight(silo, powerSiloSystem, tiers)
        )
    {
        this.Silo = silo;
        this.PowerSiloSystem = powerSiloSystem;
        this.Tiers = tiers;
        this.CapacityIcon = ItemRegistry.Create<SObject>(PowerSiloMenu.CapacityIconQualifiedItemId);

        Game1.player.Halt();

        // MOD: changed — the OK button aligns with the box's own partition line (the horizontal divider
        // between the main body and the quest/bring body), matching how a vanilla menu like
        // PondQueryMenu aligns its own close button to that same line, instead of being computed from
        // the bottom of the whole (dynamically-sized) menu — that bottom anchor made the whole stack
        // drift and tend to sit low overall. The other two buttons keep their ORIGINAL spacing relative
        // to the OK button and to each other (128px above OK, then a further 64px above that) — only
        // the group's overall anchor point changed, not their relative layout.
        int boxTop = this.yPositionOnScreen + PowerSiloMenu.TitleZoneHeight;
        int partitionY = boxTop + PowerSiloMenu.GetMainZoneHeight(silo, powerSiloSystem, tiers);
        int okButtonY = partitionY; // top-aligned to the line
         
        this.OkButton = new ClickableTextureComponent(
            new Rectangle(this.xPositionOnScreen + this.width + 4, okButtonY, 64, 64),
            Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46), 1f
        );

        // MOD: kept its original vanilla icon (a map-ish icon from the standard mouseCursors sheet) —
        // only MarkAllCoilsButton uses the dedicated coil icon.
        this.ChangeAppearanceButton = new ClickableTextureComponent(
            new Rectangle(this.xPositionOnScreen + this.width + 4, okButtonY - 128, 64, 64),
            Game1.mouseCursors, new Rectangle(48, 384, 16, 16), 4f
        );

        Texture2D markPowerCoilsIcon = Game1.content.Load<Texture2D>(PowerSiloMenu.MarkPowerCoilsIconAssetName);
        this.MarkAllCoilsButton = new ClickableTextureComponent(
            new Rectangle(this.xPositionOnScreen + this.width + 4, okButtonY - 192, 64, 64),
            markPowerCoilsIcon, new Rectangle(0, 0, markPowerCoilsIcon.Width, markPowerCoilsIcon.Height), 4f
        );
    }

    /// <inheritdoc />
    public override void update(GameTime time)
    {
        base.update(time);

        // MOD: added — advances the capacity grid's bob/pulse animations; see AnimationTimer's remarks.
        this.AnimationTimer += (float)time.ElapsedGameTime.TotalSeconds;
    }

    /// <inheritdoc />
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.OkButton.containsPoint(x, y))
        {
            Game1.playSound("smallSelect");
            Game1.exitActiveMenu();
        }
        else if (this.MarkAllCoilsButton.containsPoint(x, y))
        {
            // MOD: added — toggles the compass/arrow overlay (see PowerCoilCompass); a distinct sound
            // for each direction so turning it on/off has its own audible cue.
            bool nowShowing = PowerCoilCompass.ToggleCompass();
            Game1.playSound(nowShowing ? "smallSelect" : "bigDeSelect");
        }
        else if (this.ChangeAppearanceButton.containsPoint(x, y))
        {
            // MOD: added — toggles the world-map overlay (see PowerCoilMapMarkerPatches); a distinct
            // sound for each direction so turning it on/off has its own audible cue.
            bool nowShowing = PowerCoilMapMarkerPatches.ToggleMapMarkers();
            Game1.playSound(nowShowing ? "smallSelect" : "bigDeSelect");
        }
    }

    /// <inheritdoc />
    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        Game1.exitActiveMenu();
        Game1.playSound("smallSelect");
    }

    /// <inheritdoc />
    public override void performHoverAction(int x, int y)
    {
        this.HoverText = "";

        foreach (ClickableTextureComponent button in new[] { this.OkButton, this.ChangeAppearanceButton, this.MarkAllCoilsButton })
        {
            if (button.containsPoint(x, y))
                button.scale = Math.Min(button.baseScale + 0.1f, button.scale + 0.05f);
            else
                button.scale = Math.Max(button.baseScale, button.scale - 0.05f);
        }

        if (this.MarkAllCoilsButton.containsPoint(x, y))
            this.HoverText = PowerCoilCompass.ShowCompass ? I18n.Menu_PowerSilo_HideCoilMarkers() : I18n.Menu_PowerSilo_ShowCoilMarkers();
        else if (this.ChangeAppearanceButton.containsPoint(x, y))
            this.HoverText = PowerCoilMapMarkerPatches.ShowMapMarkers ? I18n.Menu_PowerSilo_HideCoilsOnMap() : I18n.Menu_PowerSilo_ShowCoilsOnMap();
    }

    /// <inheritdoc />
    public override void draw(SpriteBatch b)
    {
        if (!Game1.options.showClearBackgrounds)
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.75f);

        (int totalCoils, int totalCapacity, int thisSiloCapacity, int nextTierCapacity, int maxPerSilo, PowerSiloTierConfig? tier, bool isSolarTier) = this.GetState();
        List<(PowerSiloRequiredItem Requirement, int Remaining)> remainingItems = PowerSiloMenu.GetRemainingItems(this.Silo, this.PowerSiloSystem, tier);
        bool hasOutstandingItems = remainingItems.Count > 0;
        bool isUnlimited = totalCapacity == int.MaxValue;
        int gridHeight = isSolarTier
            ? (int)(PowerSiloMenu.IconSlotSpacing * 4f) // MOD: the solar cluster is always a single row of icons, regardless of maxPerSilo
            : PowerSiloMenu.GetRowCount(maxPerSilo) * (int)(PowerSiloMenu.IconSlotSpacing * 4f);
        string wrappedFlavorText = PowerSiloMenu.GetWrappedFlavorText(isSolarTier, this.width);
        int flavorTextHeight = (int)Game1.smallFont.MeasureString(wrappedFlavorText).Y;
        int mainContentHeight = PowerSiloMenu.GetMainContentHeight(gridHeight, flavorTextHeight);
        int mainZoneHeight = mainContentHeight + PowerSiloMenu.BodyTopMargin + PowerSiloMenu.BodyBottomMargin;

        // bottom zone — always present now, showing either the bring-request rows (while there's
        // something outstanding) or a status line in their place (e.g. "Fully upgraded!", or, once the
        // terminal solar tier is reached, "Connect Solar Panels to expand Power Grid") rather than
        // disappearing entirely, so the box never loses its second half.
        string wrappedStatusText = PowerSiloMenu.GetWrappedStatusText(hasOutstandingItems, isSolarTier, this.width);
        int statusTextHeight = (int)Game1.smallFont.MeasureString(wrappedStatusText).Y;
        int bottomZoneHeight = PowerSiloMenu.GetBottomZoneHeight(remainingItems.Count, statusTextHeight);

        // one continuous box spanning both zones — matches PondQueryMenu's own single-box-with-an-
        // internal-partition structure, rather than two visually separate boxes.
        int boxTop = this.yPositionOnScreen + PowerSiloMenu.TitleZoneHeight;
        int boxHeight = mainZoneHeight + bottomZoneHeight;
        Game1.drawDialogueBox(this.xPositionOnScreen, boxTop, this.width, boxHeight, speaker: false, drawOnlyBox: true);

        // title tag — overlaps the box's own top border (drawn last, on top), the same way
        // PondQueryMenu positions its own name tag relative to its box rather than the menu's origin.
        string nameText = I18n.Menu_PowerSilo_Title();
        Vector2 nameSize = Game1.smallFont.MeasureString(nameText);
        Game1.DrawBox((int)(this.xPositionOnScreen + this.width / 2 - (nameSize.X + 64f) * 0.5f), boxTop - 4, (int)(nameSize.X + 64f), 64);
        Utility.drawTextWithShadow(b, nameText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - nameSize.X * 0.5f, boxTop - 4 + 32 - nameSize.Y * 0.5f), Color.Black);

        // main zone — within [boxTop, boxTop + mainZoneHeight]. See BodyTopMargin's remarks for why the
        // top/bottom margins default to different sizes.
        float cursorY = boxTop + PowerSiloMenu.BodyTopMargin;

        string siloCapacityText = I18n.Menu_PowerSilo_SiloCapacity(capacity: thisSiloCapacity);
        Vector2 siloCapacityTextSize = Game1.smallFont.MeasureString(siloCapacityText);
        Utility.drawTextWithShadow(b, siloCapacityText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - siloCapacityTextSize.X * 0.5f, cursorY), Game1.textColor);
        cursorY += PowerSiloMenu.LineHeight;

        string totalCapacityText = I18n.Menu_PowerSilo_GridStatus(totalCoils: totalCoils, capacity: isUnlimited ? I18n.Menu_Unlimited() : totalCapacity.ToString());
        Vector2 totalCapacityTextSize = Game1.smallFont.MeasureString(totalCapacityText);
        Utility.drawTextWithShadow(b, totalCapacityText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - totalCapacityTextSize.X * 0.5f, cursorY), Game1.textColor);
        cursorY += PowerSiloMenu.LineHeight + PowerSiloMenu.ElementGap;

        // MOD: once a Silo reaches the terminal solar tier, the flat battery grid no longer means
        // anything (capacity there comes from connected Solar Panels, not delivered items) — replace it
        // with a compact Solar Panel cluster instead (SolarClusterIconCount icons, one per panel in a
        // full set), the last icon carrying an "x{count}" badge for how many panels are actually
        // connected right now (see PowerSiloSystem.GetConnectedSolarPanelCount).
        if (isSolarTier)
        {
            ParsedItemData solarItemData = ItemRegistry.GetDataOrErrorItem(PowerSiloMenu.SolarPanelQualifiedItemId);
            Texture2D solarTexture = solarItemData.GetTexture();
            Rectangle solarSourceRect = solarItemData.GetSourceRect();
            int connectedCount = this.PowerSiloSystem.GetConnectedSolarPanelCount();

            // MOD: a modulo "fill" representation, not a flat x/N progress bar — the icons represent the
            // CURRENT partial set filling up (so it always shows a fresh 0-N count that resets each time
            // a set completes), while the "x{amount}" badge is how many complete sets have been made so
            // far (i.e. how many extra coils that's actually granted). E.g. with a set size of 5, 6
            // connected panels = one complete set already banked (x1) plus 1 of the next set filled.
            int completedSets = connectedCount / PowerSiloSystem.SolarPanelsPerCapacityPoint;
            int filledIcons = connectedCount == 0 ? 0 : ((connectedCount - 1) % PowerSiloSystem.SolarPanelsPerCapacityPoint) + 1;

            float iconPixelSize = 16f * PowerSiloMenu.SolarClusterIconScale;
            float clusterWidth = PowerSiloMenu.SolarClusterIconCount * iconPixelSize + (PowerSiloMenu.SolarClusterIconCount - 1) * PowerSiloMenu.SolarClusterIconGap;
            float clusterStartX = this.xPositionOnScreen + this.width / 2f - clusterWidth / 2f;
            float clusterY = cursorY + PowerSiloMenu.SolarClusterIconYOffset;

            for (int i = 0; i < PowerSiloMenu.SolarClusterIconCount; i++)
            {
                Vector2 iconPosition = new(clusterStartX + i * (iconPixelSize + PowerSiloMenu.SolarClusterIconGap), clusterY);
                bool isFilledIcon = i < filledIcons;
                b.Draw(solarTexture, iconPosition, solarSourceRect, Color.White * (isFilledIcon ? 1f : 0.5f), 0f, Vector2.Zero, PowerSiloMenu.SolarClusterIconScale, SpriteEffects.None, 1f);

                if (i == PowerSiloMenu.SolarClusterIconCount - 1)
                {
                    string countText = I18n.Menu_PowerSilo_CompletedSetsCount(count: completedSets);
                    Vector2 badgePosition = new(iconPosition.X + iconPixelSize + PowerSiloMenu.SolarClusterIconGap, iconPosition.Y + iconPixelSize * 0.5f - Game1.smallFont.MeasureString(countText).Y * 0.5f);
                    Utility.drawTextWithShadow(b, countText, Game1.smallFont, badgePosition, Game1.textColor);
                }
            }
        }
        else
        {
            // capacity icon grid — one slot per point of this Silo's OWN max capacity (not the save-wide
            // total), in three visual states: bright/full for however much it's currently granting;
            // dim but untinted white for exactly how many MORE the next upgrade adds (so the player can
            // see at a glance how big the next jump is, not just that there IS one); and dim with a full
            // black tint for everything beyond that, still further out.
            int slotsInRow = Math.Min(maxPerSilo, PowerSiloMenu.IconsPerRow);
            int col = 0, row = 0;
            for (int i = 0; i < maxPerSilo; i++)
            {
                Vector2 iconPosition = new(
                    this.xPositionOnScreen + this.width / 2f - PowerSiloMenu.IconSlotSpacing * slotsInRow * 4f * 0.5f + PowerSiloMenu.IconSlotSpacing * 4f * col - 12f,
                    cursorY + row * PowerSiloMenu.IconSlotSpacing * 4f
                );

                Color tint;
                float alpha;
                if (i < thisSiloCapacity)
                {
                    tint = Color.White;
                    alpha = 1f;
                }
                else if (i < nextTierCapacity)
                {
                    // MOD: pulses between a full black tint and no tint at all (rather than a static
                    // dim-white), so the "this many more next upgrade" slots visibly stand out as a
                    // preview rather than reading as just another dim state.
                    float pulse = ((float)Math.Sin(this.AnimationTimer * PowerSiloMenu.NextTierPulseSpeed) + 1f) / 2f;
                    tint = Color.Lerp(Color.Black, Color.White, pulse);
                    alpha = 0.5f;
                }
                else
                {
                    tint = Color.Black;
                    alpha = 0.5f;
                }
                this.CapacityIcon.drawInMenu(b, iconPosition, 0.75f, alpha, 0f, StackDrawType.Hide, tint, drawShadow: false);

                col++;
                if (col == PowerSiloMenu.IconsPerRow)
                {
                    col = 0;
                    row++;
                }
            }
        }
        cursorY += gridHeight + PowerSiloMenu.ElementGap;

        // MOD: added back — flavor text below the icon grid/solar cluster, swapping to a solar-specific
        // line once the terminal solar tier is reached (see GetWrappedFlavorText).
        Vector2 flavorTextSize = Game1.smallFont.MeasureString(wrappedFlavorText);
        Utility.drawTextWithShadow(b, wrappedFlavorText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - flavorTextSize.X * 0.5f, cursorY), Game1.textColor);
        // (main zone ends here — the flavor text is the last element, BodyBottomMargin above the
        // partition line below.)

        // bottom zone — below a horizontal partition. Either one row per still-outstanding required
        // item (see this class's own remarks — the zone, and the box as a whole, expands or shrinks to
        // fit however many rows that currently is), or, if nothing's currently outstanding, a single
        // centered status line in that same space instead of the zone disappearing entirely.
        int partitionY = boxTop + mainZoneHeight;
        this.drawHorizontalPartition(b, partitionY);

        // MOD: added — recomputed fresh every frame (not just on mouse-move) since the bring rows'
        // positions can shift between frames (e.g. a delivery completing removes a row) — see the loop
        // below, where it's set the moment a hovered icon is found.
        this.HoveredBringRowRequirement = null;

        if (hasOutstandingItems)
        {
            int leftX = this.xPositionOnScreen + 88;
            string bringText = I18n.Menu_BringLabel();
            Vector2 bringTextSize = Game1.smallFont.MeasureString(bringText);
            float iconX = leftX + bringTextSize.X + 12f;

            for (int i = 0; i < remainingItems.Count; i++)
            {
                (PowerSiloRequiredItem requirement, int remaining) = remainingItems[i];

                float rowTop = partitionY + PowerSiloMenu.QuestTopMargin + i * (PowerSiloMenu.BringRowHeight + PowerSiloMenu.BringRowGap);
                float rowCenterY = rowTop + PowerSiloMenu.BringRowHeight / 2f;

                // MOD: added — the same animated "continue" arrow vanilla dialogue boxes use, borrowed
                // the same way PondQueryMenu points it at its own "bring" icon.
                Utility.drawWithShadow(b, Game1.mouseCursors, new Vector2(leftX - 28 + 8f * Game1.dialogueButtonScale / 10f, rowCenterY - 8f), new Rectangle(412, 495, 5, 4), Color.White, (float)Math.PI / 2f, Vector2.Zero);

                Utility.drawTextWithShadow(b, bringText, Game1.smallFont, new Vector2(leftX, rowCenterY - bringTextSize.Y / 2f), Game1.textColor);

                // MOD: icon + count only — no item name text after it, to keep the row compact. MOD: added — hovering
                // the icon shows the full vanilla item tooltip instead (see HoveredBringRowRequirement's
                // own remarks), so the name is still discoverable without permanently taking up row space.
                ParsedItemData itemData = ItemRegistry.GetDataOrErrorItem(requirement.ItemId);
                Texture2D texture = itemData.GetTexture();
                Rectangle sourceRect = itemData.GetSourceRect();

                // MOD: fixed — scale to a fixed drawn HEIGHT (matching BringRowHeight) rather than a
                // flat 4x multiplier. A normal 16x16 item source rect still comes out to exactly 4x (the
                // original scale), but a Big Craftable like the Solar Panel has a taller (16x32) source
                // rect that 4x blew up to twice the row's height — this keeps every bring-row icon the
                // same visual size regardless of the underlying sprite's own dimensions.
                float iconScale = PowerSiloMenu.BringRowHeight / (float)sourceRect.Height;
                Vector2 iconPos = new(iconX, rowCenterY - PowerSiloMenu.BringRowHeight / 2f);
                b.Draw(texture, iconPos, sourceRect, Color.White, 0f, Vector2.Zero, iconScale, SpriteEffects.None, 1f);
                if (remaining > 1)
                    Utility.drawTinyDigits(remaining, b, iconPos + new Vector2(sourceRect.Width * iconScale * 0.75f, PowerSiloMenu.BringRowHeight * 0.6875f), 3f, 1f, Color.White);

                // MOD: added — hit-test the drawn icon against the current mouse position for the tooltip.
                Rectangle iconBounds = new((int)iconPos.X, (int)iconPos.Y, (int)(sourceRect.Width * iconScale), (int)(sourceRect.Height * iconScale));
                if (iconBounds.Contains(Game1.getMouseX(), Game1.getMouseY()))
                    this.HoveredBringRowRequirement = requirement;
            }
        }
        else
        {
            float statusRowCenterY = partitionY + PowerSiloMenu.QuestTopMargin + statusTextHeight / 2f;
            Vector2 statusTextSize = Game1.smallFont.MeasureString(wrappedStatusText);
            Utility.drawTextWithShadow(b, wrappedStatusText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - statusTextSize.X * 0.5f, statusRowCenterY - statusTextSize.Y / 2f), Game1.textColor);
        }

        this.OkButton.draw(b);
        this.ChangeAppearanceButton.draw(b);
        this.MarkAllCoilsButton.draw(b);

        if (!string.IsNullOrEmpty(this.HoverText))
            IClickableMenu.drawHoverText(b, this.HoverText, Game1.smallFont);

        // MOD: added — the full vanilla item tooltip (name, description, etc.) for whichever "Bring" row
        // icon is currently under the mouse, if any — see HoveredBringRowRequirement's own remarks.
        // Drawn last (on top of everything else, including the buttons' own HoverText) so it's never
        // obscured, matching where vanilla itself draws an item tooltip relative to the rest of a menu.
        if (this.HoveredBringRowRequirement is { } hovered)
        {
            Item hoveredItem = ItemRegistry.Create(hovered.ItemId);
            IClickableMenu.drawToolTip(b, hoveredItem.getDescription(), hoveredItem.DisplayName, hoveredItem);
        }

        this.drawMouse(b);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// Get the Silo's current tier config, this Silo's own capacity contribution (and the max any one
    /// Silo can contribute), the global capacity usage, and whether this Silo has reached the terminal
    /// solar tier — all in one place since every draw/click handler needs the same values.
    /// MOD: <c>ThisSiloCapacity</c> now folds in the connected-Solar-Panel bonus (see
    /// <see cref="PowerSiloSystem.GetConnectedSolarPanelCount"/>) on top of the tier's flat
    /// <see cref="PowerSiloTierConfig.CapacityGranted"/> once <see cref="PowerSiloTierConfig.GrantsSolarBonus"/>
    /// is reached, so the "Silo Capacity" text reflects what this Silo is actually contributing right now.
    /// </summary>
    private (int TotalCoils, int TotalCapacity, int ThisSiloCapacity, int NextTierCapacity, int MaxPerSilo, PowerSiloTierConfig? Tier, bool IsSolarTier) GetState()
    {
        if (this.Tiers.Count == 0)
            return (0, 0, 0, 0, 0, null, false);

        int tierIndex = Math.Clamp(this.PowerSiloSystem.GetTier(this.Silo), 0, this.Tiers.Count - 1);
        (int totalCoils, int totalCapacity) = this.PowerSiloSystem.GetUsage();
        int maxPerSilo = this.Tiers[^1].CapacityGranted;
        PowerSiloTierConfig tier = this.Tiers[tierIndex];
        bool isSolarTier = tier.GrantsSolarBonus;
        int thisSiloCapacity = tier.CapacityGranted + (isSolarTier ? this.PowerSiloSystem.GetConnectedSolarPanelCount() / PowerSiloSystem.SolarPanelsPerCapacityPoint : 0);

        // MOD: added — the next tier's flat CapacityGranted (or the same as thisSiloCapacity if this is
        // already the last tier, so nothing shows as "about to be gained") — lets the capacity grid show
        // a distinct dim-but-not-blacked-out band for exactly how many slots the NEXT upgrade adds, on
        // top of the plain filled/empty distinction it already had.
        int nextTierCapacity = tierIndex + 1 < this.Tiers.Count
            ? this.Tiers[tierIndex + 1].CapacityGranted
            : thisSiloCapacity;

        return (totalCoils, totalCapacity, thisSiloCapacity, nextTierCapacity, maxPerSilo, tier, isSolarTier);
    }

    /// <summary>Get how many rows the capacity icon grid needs.</summary>
    /// <param name="slotCount">The number of icon slots to draw.</param>
    private static int GetRowCount(int slotCount)
    {
        return Math.Max(1, (int)Math.Ceiling(slotCount / (float)PowerSiloMenu.IconsPerRow));
    }

    /// <summary>
    /// Get the current tier's required items that still need more delivered (i.e. haven't been fully
    /// delivered yet), each paired with how much of it is still outstanding — one bring-request row is
    /// drawn per entry (see <see cref="draw"/>), so this is also what determines how many rows there
    /// are for sizing purposes (see <see cref="GetBottomZoneHeight"/>). A tier with no
    /// <see cref="PowerSiloTierConfig.RequiredItems"/> at all (fully upgraded) simply returns empty.
    /// </summary>
    /// <param name="silo">The Power Silo building.</param>
    /// <param name="powerSiloSystem">The power silo capacity system, used to read delivery progress.</param>
    /// <param name="tier">The Silo's current tier config, or <c>null</c> if there are no tiers configured at all.</param>
    private static List<(PowerSiloRequiredItem Requirement, int Remaining)> GetRemainingItems(Building silo, PowerSiloSystem powerSiloSystem, PowerSiloTierConfig? tier)
    {
        List<(PowerSiloRequiredItem, int)> remaining = [];
        if (tier?.RequiredItems is not { Count: > 0 } requiredItems)
            return remaining;

        for (int i = 0; i < requiredItems.Count; i++)
        {
            int delivered = powerSiloSystem.GetDeliveredCount(silo, i);
            int stillNeeded = requiredItems[i].Count - delivered;
            if (stillNeeded > 0)
                remaining.Add((requiredItems[i], stillNeeded));
        }

        return remaining;
    }

    /// <summary>Get the bottom zone's total height — either one row per still-outstanding item (with a gap between consecutive rows), or a single status text line when nothing's currently outstanding — plus the usual top/bottom margins either way.</summary>
    /// <param name="remainingItemCount">How many bring-request rows the zone needs, or 0 to size it for the status text instead.</param>
    /// <param name="statusTextHeight">The status text's own measured height (after wrapping), used when <paramref name="remainingItemCount"/> is 0.</param>
    private static int GetBottomZoneHeight(int remainingItemCount, int statusTextHeight)
    {
        int contentHeight = remainingItemCount > 0
            ? remainingItemCount * PowerSiloMenu.BringRowHeight + Math.Max(0, remainingItemCount - 1) * PowerSiloMenu.BringRowGap
            : statusTextHeight;
        return PowerSiloMenu.QuestTopMargin + contentHeight + PowerSiloMenu.QuestBottomMargin;
    }

    /// <summary>Get the main zone's natural content height (before padding) — the two capacity text lines, the icon grid, and the flavor text below it, separated the same way in both <see cref="draw"/> and <see cref="ComputeHeight"/>.</summary>
    /// <param name="gridHeight">The icon grid's own height.</param>
    /// <param name="flavorTextHeight">MOD: added. The flavor text's own measured height (after wrapping).</param>
    private static int GetMainContentHeight(int gridHeight, int flavorTextHeight)
    {
        return PowerSiloMenu.LineHeight * 2 + PowerSiloMenu.ElementGap + gridHeight + PowerSiloMenu.ElementGap + flavorTextHeight;
    }

    /// <summary>
    /// MOD: added. Get the main zone's total height (title-tag-relative) — i.e. how far below the box's
    /// own top the horizontal partition line falls. Shared by <see cref="ComputeHeight"/> (to size the
    /// menu) and the constructor (to align the button stack's <see cref="OkButton"/> with that same
    /// partition line, matching where a vanilla menu like <see cref="PondQueryMenu"/> aligns its own
    /// close button), so all three can never drift apart from each other.
    /// </summary>
    /// <param name="silo">The Power Silo this menu displays.</param>
    /// <param name="powerSiloSystem">The power silo capacity system, used to read usage/tier state.</param>
    /// <param name="tiers">The ordered capacity tiers a Power Silo progresses through.</param>
    private static int GetMainZoneHeight(Building silo, PowerSiloSystem powerSiloSystem, List<PowerSiloTierConfig> tiers)
    {
        if (tiers.Count == 0)
        {
            int emptyFlavorTextHeight = (int)Game1.smallFont.MeasureString(PowerSiloMenu.GetWrappedFlavorText(false, PowerSiloMenu.MenuWidth)).Y;
            return PowerSiloMenu.GetMainContentHeight(0, emptyFlavorTextHeight) + PowerSiloMenu.BodyTopMargin + PowerSiloMenu.BodyBottomMargin;
        }

        int tierIndex = Math.Clamp(powerSiloSystem.GetTier(silo), 0, tiers.Count - 1);
        PowerSiloTierConfig tier = tiers[tierIndex];
        bool isSolarTier = tier.GrantsSolarBonus;
        int maxPerSilo = tiers[^1].CapacityGranted;

        int gridHeight = isSolarTier
            ? (int)(PowerSiloMenu.IconSlotSpacing * 4f)
            : PowerSiloMenu.GetRowCount(maxPerSilo) * (int)(PowerSiloMenu.IconSlotSpacing * 4f);
        int flavorTextHeight = (int)Game1.smallFont.MeasureString(PowerSiloMenu.GetWrappedFlavorText(isSolarTier, PowerSiloMenu.MenuWidth)).Y;
        return PowerSiloMenu.GetMainContentHeight(gridHeight, flavorTextHeight) + PowerSiloMenu.BodyTopMargin + PowerSiloMenu.BodyBottomMargin;
    }

    /// <summary>MOD: added. Get the main zone's flavor text (see <see cref="DefaultFlavorText"/>/<see cref="SolarFlavorText"/>), word-wrapped to fit the menu's width — shared by <see cref="draw"/> and <see cref="ComputeHeight"/> so they always agree on how many lines it takes.</summary>
    /// <param name="isSolarTier">Whether the Silo has reached the terminal solar tier.</param>
    /// <param name="menuWidth">The menu's width, used to compute the wrap width.</param>
    private static string GetWrappedFlavorText(bool isSolarTier, int menuWidth)
    {
        string flavorText = isSolarTier ? I18n.Menu_PowerSilo_FlavorTextSolar() : I18n.Menu_PowerSilo_FlavorTextDefault();
        return Game1.parseText(flavorText, Game1.smallFont, menuWidth - IClickableMenu.spaceToClearSideBorder * 2);
    }

    /// <summary>Get the bottom zone's status text (shown in place of the bring-request rows when nothing's currently outstanding), word-wrapped to fit the menu's width — shared by <see cref="draw"/> and <see cref="ComputeHeight"/> so they always agree on how many lines it takes.</summary>
    /// <param name="hasOutstandingItems">Whether the Silo currently has any outstanding item to deliver (if so, the bring rows are shown instead and this text isn't drawn at all, but it's still measured for <see cref="ComputeHeight"/> to size against defensively).</param>
    /// <param name="isSolarTier">MOD: added. Whether the Silo has reached the terminal solar tier — its status text points the player at Solar Panels instead of claiming there's nothing left to do.</param>
    /// <param name="menuWidth">The menu's width, used to compute the wrap width.</param>
    private static string GetWrappedStatusText(bool hasOutstandingItems, bool isSolarTier, int menuWidth)
    {
        string statusText = hasOutstandingItems
            ? ""
            : isSolarTier
                ? I18n.Menu_PowerSilo_ConnectSolarPanels()
                : I18n.Menu_PowerSilo_FullyUpgraded();
        return Game1.parseText(statusText, Game1.smallFont, menuWidth - IClickableMenu.spaceToClearSideBorder * 2);
    }

    /// <summary>Compute this menu's total height up front, so it can be centered and sized correctly before anything is drawn — mirrors the exact same per-zone math <see cref="draw"/> uses, so the two never drift apart.</summary>
    /// <param name="silo">The Power Silo this menu displays.</param>
    /// <param name="powerSiloSystem">The power silo capacity system, used to read usage/tier state.</param>
    /// <param name="tiers">The ordered capacity tiers a Power Silo progresses through.</param>
    private static int ComputeHeight(Building silo, PowerSiloSystem powerSiloSystem, List<PowerSiloTierConfig> tiers)
    {
        int mainZoneHeight = PowerSiloMenu.GetMainZoneHeight(silo, powerSiloSystem, tiers);

        if (tiers.Count == 0)
        {
            int emptyStatusTextHeight = (int)Game1.smallFont.MeasureString(PowerSiloMenu.GetWrappedStatusText(false, false, PowerSiloMenu.MenuWidth)).Y;
            int emptyBottomZoneHeight = PowerSiloMenu.GetBottomZoneHeight(0, emptyStatusTextHeight);
            return PowerSiloMenu.TitleZoneHeight + mainZoneHeight + emptyBottomZoneHeight;
        }

        int tierIndex = Math.Clamp(powerSiloSystem.GetTier(silo), 0, tiers.Count - 1);
        PowerSiloTierConfig tier = tiers[tierIndex];
        bool isSolarTier = tier.GrantsSolarBonus;
        List<(PowerSiloRequiredItem Requirement, int Remaining)> remainingItems = PowerSiloMenu.GetRemainingItems(silo, powerSiloSystem, tier);
        bool hasOutstandingItems = remainingItems.Count > 0;

        int statusTextHeight = (int)Game1.smallFont.MeasureString(PowerSiloMenu.GetWrappedStatusText(hasOutstandingItems, isSolarTier, PowerSiloMenu.MenuWidth)).Y;
        int bottomZoneHeight = PowerSiloMenu.GetBottomZoneHeight(remainingItems.Count, statusTextHeight);

        return PowerSiloMenu.TitleZoneHeight + mainZoneHeight + bottomZoneHeight;
    }
}

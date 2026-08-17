using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. The Power Relay's status menu, opened by <see cref="PowerRelayInteraction"/> when the
/// player clicks it without delivering a matching item — modeled directly on
/// <see cref="PowerSiloMenu"/>'s own layout (title tag overlapping the box's top border, one continuous
/// dialogue box with an internal partition line separating the main content from a bottom status strip),
/// scoped to what a Power Relay actually needs to show: the save-wide pacing bonus, this Relay's own
/// contribution, and how many of each item it's had delivered.
///
/// This is a pure status display — no click targets besides the OK button. Two earlier versions of this
/// menu let the player insert/remove items directly here (first via a custom held-slot system, then via
/// a full player-inventory drag-and-drop panel); delivery now only happens by
/// clicking the Relay building itself while holding the item (see <see cref="PowerRelayInteraction"/>),
/// exactly like a Power Silo tier.
///
/// The two icon rows always show exactly <see cref="PowerRelaySystem.MaxShards"/>/<see cref="PowerRelaySystem.MaxBars"/>
/// slots each (fixed), but the bottom zone's height still varies per-instance — 0, 1, or 2 lines
/// depending on which track(s) this specific Relay has maxed — so <see cref="ComputeHeight"/> still
/// takes the Relay/system the same way <see cref="PowerSiloMenu.ComputeHeight"/> does.
/// </summary>
internal class PowerRelayMenu : IClickableMenu
{
    /*********
    ** Fields
    *********/
    /// <summary>The menu's fixed width, matching <see cref="PowerSiloMenu.MenuWidth"/>.</summary>
    private const int MenuWidth = 600;

    /// <summary>The vertical space from the menu's own top to where the dialogue box starts — see <see cref="PowerSiloMenu.TitleZoneHeight"/>'s remarks.</summary>
    private const int TitleZoneHeight = 128;

    /// <summary>See <see cref="PowerSiloMenu.BodyTopMargin"/>'s remarks.</summary>
    private const int BodyTopMargin = 120;

    /// <summary>See <see cref="PowerSiloMenu.BodyBottomMargin"/>'s remarks.</summary>
    private const int BodyBottomMargin = 0;

    /// <summary>The bottom zone's clearance above its content (or, when both tracks are maxed, its only content), below the partition line.</summary>
    private const int BottomTopMargin = 64;

    /// <summary>The bottom zone's clearance below its content, against the box's own bottom border.</summary>
    private const int BottomBottomMargin = 64;

    /// <summary>The vertical advance per single-line text element.</summary>
    private const int LineHeight = 32;

    /// <summary>The small gap between distinct elements within the main zone.</summary>
    private const int ElementGap = 10;

    /// <summary>The vertical (and horizontal) spacing between icon slots, before the 4x zoom multiplier — matches <see cref="PowerSiloMenu.IconSlotSpacing"/>.</summary>
    private const float IconSlotSpacing = 13f;

    /// <summary>The gap between the shard row and the bar row.</summary>
    private const float IconRowGap = 6f;

    /// <summary>The height of one bottom-zone row (a "Bring:" prompt or a "reached max" status line) — matches <see cref="PowerSiloMenu.BringRowHeight"/>, dictated by the item icon (a 16x16 sprite at 4x zoom).</summary>
    private const int BringRowHeight = 64;

    /// <summary>The flavor text shown below the icon rows for as long as at least one track isn't maxed.</summary>
    private const string DefaultFlavorText = "Convinent slots are lined along the relay.\nIt seems to need precious materials to\nimprove automation efficency.";

    /// <summary>The flavor text shown once both tracks are maxed.</summary>
    private const string FullyPoweredFlavorText = "The stones glow and shake with energy,\nthe relay hums at peak efficiency.";

    /// <summary>The Power Relay this menu displays.</summary>
    private readonly Building Relay;

    /// <summary>The power relay system, used to read delivered counts and the save-wide bonus.</summary>
    private readonly PowerRelaySystem PowerRelaySystem;

    /// <summary>Get the qualified/unqualified item ID delivered for the delay-reduction track, from level 1 onward.</summary>
    private readonly Func<string> GetShardItemId;

    /// <summary>MOD: added. Get the qualified/unqualified item ID delivered for the delay-reduction track's very first delivery only (level 0→1).</summary>
    private readonly Func<string> GetFirstShardItemId;

    /// <summary>Get the qualified/unqualified item ID delivered for the actions-per-window bonus track, from level 1 onward.</summary>
    private readonly Func<string> GetBarItemId;

    /// <summary>MOD: added. Get the qualified/unqualified item ID delivered for the actions-per-window bonus track's very first delivery only (level 0→1).</summary>
    private readonly Func<string> GetFirstBarItemId;

    /// <summary>Get the current base <see cref="ModConfig.ActionsPerDelayWindow"/>, before the Power Relay bonus is applied.</summary>
    private readonly Func<int> GetBaseActionsPerDelayWindow;

    /// <summary>A throwaway instance of the shard item, used purely to draw its icon (slots 1+ of the icon row).</summary>
    private readonly SObject ShardIcon;

    /// <summary>MOD: added. A throwaway instance of the delay-reduction track's first-delivery item, used purely to draw its icon (slot 0 of the icon row).</summary>
    private readonly SObject FirstShardIcon;

    /// <summary>A throwaway instance of the bar item, used purely to draw its icon (slots 1+ of the icon row).</summary>
    private readonly SObject BarIcon;

    /// <summary>MOD: added. A throwaway instance of the actions-per-window track's first-delivery item, used purely to draw its icon (slot 0 of the icon row).</summary>
    private readonly SObject FirstBarIcon;

    /// <summary>The button that closes this menu.</summary>
    private readonly ClickableTextureComponent OkButton;

    /// <summary>The text currently hovered, shown as a tooltip.</summary>
    private string HoverText = "";

    /// <summary>Elapsed seconds since this menu opened — drives the next-icon pulse, matching <see cref="PowerSiloMenu.AnimationTimer"/>.</summary>
    private float AnimationTimer;

    /// <summary>How fast the "next delivery" icon pulses between a full black tint and no tint at all, in radians per second — matches <see cref="PowerSiloMenu.NextTierPulseSpeed"/>.</summary>
    private const float NextTierPulseSpeed = 3f;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="relay">The Power Relay this menu displays.</param>
    /// <param name="powerRelaySystem">The power relay system, used to read delivered counts and the save-wide bonus.</param>
    /// <param name="getShardItemId">Get the qualified/unqualified item ID delivered for the delay-reduction track, from level 1 onward.</param>
    /// <param name="getFirstShardItemId">MOD: added. Get the qualified/unqualified item ID delivered for the delay-reduction track's very first delivery only (level 0→1).</param>
    /// <param name="getBarItemId">Get the qualified/unqualified item ID delivered for the actions-per-window bonus track, from level 1 onward.</param>
    /// <param name="getFirstBarItemId">MOD: added. Get the qualified/unqualified item ID delivered for the actions-per-window bonus track's very first delivery only (level 0→1).</param>
    /// <param name="getBaseActionsPerDelayWindow">Get the current base <see cref="ModConfig.ActionsPerDelayWindow"/>, before the Power Relay bonus is applied.</param>
    public PowerRelayMenu(Building relay, PowerRelaySystem powerRelaySystem, Func<string> getShardItemId, Func<string> getFirstShardItemId, Func<string> getBarItemId, Func<string> getFirstBarItemId, Func<int> getBaseActionsPerDelayWindow)
        : base(
            Game1.uiViewport.Width / 2 - PowerRelayMenu.MenuWidth / 2,
            Game1.uiViewport.Height / 2 - PowerRelayMenu.ComputeHeight() / 2,
            PowerRelayMenu.MenuWidth,
            PowerRelayMenu.ComputeHeight()
        )
    {
        this.Relay = relay;
        this.PowerRelaySystem = powerRelaySystem;
        this.GetShardItemId = getShardItemId;
        this.GetFirstShardItemId = getFirstShardItemId;
        this.GetBarItemId = getBarItemId;
        this.GetFirstBarItemId = getFirstBarItemId;
        this.GetBaseActionsPerDelayWindow = getBaseActionsPerDelayWindow;
        this.ShardIcon = ItemRegistry.Create<SObject>(getShardItemId());
        this.FirstShardIcon = ItemRegistry.Create<SObject>(getFirstShardItemId());
        this.BarIcon = ItemRegistry.Create<SObject>(getBarItemId());
        this.FirstBarIcon = ItemRegistry.Create<SObject>(getFirstBarItemId());

        Game1.player.Halt();

        int boxTop = this.yPositionOnScreen + PowerRelayMenu.TitleZoneHeight;
        int partitionY = boxTop + PowerRelayMenu.GetMainZoneHeight();

        this.OkButton = new ClickableTextureComponent(
            new Rectangle(this.xPositionOnScreen + this.width + 4, partitionY, 64, 64),
            Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46), 1f
        );
    }

    /// <inheritdoc />
    public override void update(GameTime time)
    {
        base.update(time);

        // MOD: added — advances the next-icon pulse animation; see AnimationTimer's remarks.
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

        if (this.OkButton.containsPoint(x, y))
            this.OkButton.scale = Math.Min(this.OkButton.baseScale + 0.1f, this.OkButton.scale + 0.05f);
        else
            this.OkButton.scale = Math.Max(this.OkButton.baseScale, this.OkButton.scale - 0.05f);
    }

    /// <inheritdoc />
    public override void draw(SpriteBatch b)
    {
        if (!Game1.options.showClearBackgrounds)
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.75f);

        int shardLevel = this.PowerRelaySystem.GetShardLevel(this.Relay);
        int barLevel = this.PowerRelaySystem.GetBarLevel(this.Relay);
        bool shardsMaxed = shardLevel >= PowerRelaySystem.MaxShards;
        bool barsMaxed = barLevel >= PowerRelaySystem.MaxBars;
        bool bothMaxed = shardsMaxed && barsMaxed;

        int mainZoneHeight = PowerRelayMenu.GetMainZoneHeight();
        int bottomZoneHeight = PowerRelayMenu.GetBottomZoneHeight();

        int boxTop = this.yPositionOnScreen + PowerRelayMenu.TitleZoneHeight;
        int boxHeight = mainZoneHeight + bottomZoneHeight;
        Game1.drawDialogueBox(this.xPositionOnScreen, boxTop, this.width, boxHeight, speaker: false, drawOnlyBox: true);

        // title tag — overlaps the box's own top border, same as PowerSiloMenu.
        string nameText = "Automation Relay";
        Vector2 nameSize = Game1.smallFont.MeasureString(nameText);
        Game1.DrawBox((int)(this.xPositionOnScreen + this.width / 2 - (nameSize.X + 64f) * 0.5f), boxTop - 4, (int)(nameSize.X + 64f), 64);
        Utility.drawTextWithShadow(b, nameText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - nameSize.X * 0.5f, boxTop - 4 + 32 - nameSize.Y * 0.5f), Color.Black);

        // main zone
        float cursorY = boxTop + PowerRelayMenu.BodyTopMargin;

        // MOD: delay floor now comes from PowerRelaySystem.GetEffectiveActionDelaySeconds() (the single
        // source of truth, shared with ModEntry's own pacing code) rather than a duplicated Math.Max(0f, ...)
        // here — see PowerRelaySystem's own remarks for why 0 is no longer the right floor by default.
        // The actions side has no equivalent cap (a base of "unlimited" stays unlimited) — still computed
        // here rather than exposed from ModEntry, since that one's a private instance helper with no
        // other outside caller.
        float effectiveDelay = this.PowerRelaySystem.GetEffectiveActionDelaySeconds();
        int baseActionsPerDelayWindow = this.GetBaseActionsPerDelayWindow();
        int effectiveActionsPerDelayWindow = baseActionsPerDelayWindow <= 0
            ? baseActionsPerDelayWindow
            : baseActionsPerDelayWindow + this.PowerRelaySystem.GetActionsPerDelayWindowBonus();

        float thisRelayDelayReduction = PowerRelayMenu.GetThisRelayDelayReduction(this.Relay, this.PowerRelaySystem);
        int thisRelayActionsBonus = PowerRelayMenu.GetThisRelayActionsBonus(this.Relay, this.PowerRelaySystem);
        string thisRelayText = $"Automation Relay: (-{thisRelayDelayReduction:0.0}s delay/+{thisRelayActionsBonus} actions)";
        Vector2 thisRelayTextSize = Game1.smallFont.MeasureString(thisRelayText);
        Utility.drawTextWithShadow(b, thisRelayText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - thisRelayTextSize.X * 0.5f, cursorY), Game1.textColor);
        cursorY += PowerRelayMenu.LineHeight + PowerRelayMenu.ElementGap;

        string gridDelayText = $"Power Grid automation delay: {effectiveDelay:0.0}s";
        Vector2 gridDelayTextSize = Game1.smallFont.MeasureString(gridDelayText);
        Utility.drawTextWithShadow(b, gridDelayText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - gridDelayTextSize.X * 0.5f, cursorY), Game1.textColor);
        cursorY += PowerRelayMenu.LineHeight;

        string gridActionsText = $"Power Grid automation actions: {(effectiveActionsPerDelayWindow <= 0 ? "Unlimited" : effectiveActionsPerDelayWindow.ToString())}";
        Vector2 gridActionsTextSize = Game1.smallFont.MeasureString(gridActionsText);
        Utility.drawTextWithShadow(b, gridActionsText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - gridActionsTextSize.X * 0.5f, cursorY), Game1.textColor);
        cursorY += PowerRelayMenu.LineHeight;


        // icon rows — always IconsPerRow slots each; filled (reached level) draws at full opacity, the
        // next level pulses, and everything beyond that is dimmed with a full black tint.
        this.DrawIconRow(b, this.ShardIcon, this.FirstShardIcon, shardLevel, PowerRelaySystem.MaxShards, cursorY);
        cursorY += PowerRelayMenu.IconSlotSpacing * 4f + PowerRelayMenu.IconRowGap;
        this.DrawIconRow(b, this.BarIcon, this.FirstBarIcon, barLevel, PowerRelaySystem.MaxBars, cursorY);
        cursorY += PowerRelayMenu.IconSlotSpacing * 4f + PowerRelayMenu.ElementGap;

        string flavorText = bothMaxed ? PowerRelayMenu.FullyPoweredFlavorText : PowerRelayMenu.DefaultFlavorText;
        string wrappedFlavorText = Game1.parseText(flavorText, Game1.smallFont, this.width - IClickableMenu.spaceToClearSideBorder * 2);
        Vector2 flavorTextSize = Game1.smallFont.MeasureString(wrappedFlavorText);
        Utility.drawTextWithShadow(b, wrappedFlavorText, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - flavorTextSize.X * 0.5f, cursorY), Game1.textColor);
        // (main zone ends here — BodyBottomMargin above the partition line below.)

        // bottom zone — below a horizontal partition. Always 2 status lines — either a "Bring:" prompt
        // or a "reached max" message per track (an earlier version blanked
        // this zone out entirely once both tracks were maxed; the reached-max messages now stay
        // permanently instead of disappearing).
        int partitionY = boxTop + mainZoneHeight;
        this.drawHorizontalPartition(b, partitionY);

        int leftX = this.xPositionOnScreen + 88;
        string bringText = "Bring:";
        Vector2 bringTextSize = Game1.smallFont.MeasureString(bringText);
        float iconX = leftX + bringTextSize.X + 12f;

        float rowTop = partitionY + PowerRelayMenu.BottomTopMargin;
        float rowCenterY = rowTop + PowerRelayMenu.BringRowHeight / 2f;

        // MOD: added — once the delay's been pushed down to its global floor,
        // EVERY Relay's shard row shows this instead of either its own bring-prompt or its own
        // per-Relay-maxed message, since neither would be true/useful anymore at that point.
        if (this.PowerRelaySystem.IsGlobalSpeedCapped())
            this.DrawCenteredStatusLine(b, "[Reached global max Power Grid speed]", rowCenterY);
        else if (!shardsMaxed)
        {
            int shardsNeeded = this.PowerRelaySystem.GetShardsNeededForNextLevel(this.Relay);
            string currentShardItemId = shardLevel == 0 ? this.GetFirstShardItemId() : this.GetShardItemId();
            this.DrawBringRow(b, currentShardItemId, shardsNeeded, "(-0.4s automation delay)", leftX, iconX, bringText, bringTextSize, rowCenterY);
        }
        else
            this.DrawCenteredStatusLine(b, "[Reached max speed on Automation Relay]", rowCenterY);

        rowTop += PowerRelayMenu.BringRowHeight;
        rowCenterY = rowTop + PowerRelayMenu.BringRowHeight / 2f;

        if (!barsMaxed)
        {
            int barsNeeded = this.PowerRelaySystem.GetBarsNeededForNextLevel(this.Relay);
            string currentBarItemId = barLevel == 0 ? this.GetFirstBarItemId() : this.GetBarItemId();
            this.DrawBringRow(b, currentBarItemId, barsNeeded, "(+2 automation actions)", leftX, iconX, bringText, bringTextSize, rowCenterY);
        }
        else
            this.DrawCenteredStatusLine(b, "[Reached max actions on Automation Relay]", rowCenterY);

        this.OkButton.draw(b);

        if (!string.IsNullOrEmpty(this.HoverText))
            IClickableMenu.drawHoverText(b, this.HoverText, Game1.smallFont);

        this.drawMouse(b);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Draw a "Bring: [icon]xN {trailing text}" row — the same animated arrow + icon layout <see cref="PowerSiloMenu"/>'s own bring rows use (including the tiny-digit count badge when more than 1 is still needed), plus the original descriptive sentence after the icon.</summary>
    /// <param name="b">The sprite batch to draw to.</param>
    /// <param name="itemId">The item ID to draw the icon for.</param>
    /// <param name="neededCount">How many more of the item are needed to reach the next level.</param>
    /// <param name="trailingText">The text drawn after the icon (e.g. "to increase automation speed.").</param>
    /// <param name="leftX">The row's left edge (where the arrow is drawn).</param>
    /// <param name="iconX">Where the item icon starts, after the "Bring:" text.</param>
    /// <param name="bringText">The "Bring:" label text.</param>
    /// <param name="bringTextSize">The "Bring:" label's measured size.</param>
    /// <param name="rowCenterY">The row's vertical center.</param>
    private void DrawBringRow(SpriteBatch b, string itemId, int neededCount, string trailingText, int leftX, float iconX, string bringText, Vector2 bringTextSize, float rowCenterY)
    {
        // the same animated "continue" arrow vanilla dialogue boxes use, matching PowerSiloMenu's own bring rows.
        Utility.drawWithShadow(b, Game1.mouseCursors, new Vector2(leftX - 28 + 8f * Game1.dialogueButtonScale / 10f, rowCenterY - 8f), new Rectangle(412, 495, 5, 4), Color.White, (float)Math.PI / 2f, Vector2.Zero);

        Utility.drawTextWithShadow(b, bringText, Game1.smallFont, new Vector2(leftX, rowCenterY - bringTextSize.Y / 2f), Game1.textColor);

        ParsedItemData itemData = ItemRegistry.GetDataOrErrorItem(itemId);
        Texture2D texture = itemData.GetTexture();
        Rectangle sourceRect = itemData.GetSourceRect();
        float iconScale = PowerRelayMenu.BringRowHeight / (float)sourceRect.Height;
        Vector2 iconPos = new(iconX, rowCenterY - PowerRelayMenu.BringRowHeight / 2f);
        b.Draw(texture, iconPos, sourceRect, Color.White, 0f, Vector2.Zero, iconScale, SpriteEffects.None, 1f);

        // MOD: added — the same tiny-digit "how many more" badge PowerSiloMenu's own bring rows draw
        // when more than 1 is still needed, now that each level can cost more than a single item.
        if (neededCount > 1)
            Utility.drawTinyDigits(neededCount, b, iconPos + new Vector2(sourceRect.Width * iconScale * 0.75f, PowerRelayMenu.BringRowHeight * 0.6875f), 3f, 1f, Color.White);

        float iconDrawWidth = sourceRect.Width * iconScale;
        Vector2 trailingTextSize = Game1.smallFont.MeasureString(trailingText);
        Utility.drawTextWithShadow(b, trailingText, Game1.smallFont, new Vector2(iconX + iconDrawWidth + 8f, rowCenterY - trailingTextSize.Y / 2f), Game1.textColor);
    }

    /// <summary>Draw a single centered status line within a bottom-zone row (e.g. a "reached max" message).</summary>
    /// <param name="b">The sprite batch to draw to.</param>
    /// <param name="text">The text to draw.</param>
    /// <param name="rowCenterY">The row's vertical center.</param>
    private void DrawCenteredStatusLine(SpriteBatch b, string text, float rowCenterY)
    {
        Vector2 size = Game1.smallFont.MeasureString(text);
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(this.xPositionOnScreen + this.width / 2 - size.X * 0.5f, rowCenterY - size.Y / 2f), Game1.textColor);
    }

    /// <summary>Draw one row of item icons, filled up to <paramref name="deliveredCount"/> at full opacity and the rest dimmed.</summary>
    /// <param name="b">The sprite batch to draw to.</param>
    /// <param name="icon">A throwaway instance of the track's normal item (slots 1+), used purely to draw its icon.</param>
    /// <param name="firstIcon">MOD: added. A throwaway instance of the track's special first-delivery item (slot 0 only), used purely to draw its icon.</param>
    /// <param name="deliveredCount">How many of this row's slots are filled.</param>
    /// <param name="totalCount">How many slots this row has in total.</param>
    /// <param name="rowY">The row's top Y position.</param>
    private void DrawIconRow(SpriteBatch b, SObject icon, SObject firstIcon, int deliveredCount, int totalCount, float rowY)
    {
        for (int i = 0; i < totalCount; i++)
        {
            Vector2 iconPosition = new(
                this.xPositionOnScreen + this.width / 2f - PowerRelayMenu.IconSlotSpacing * totalCount * 4f * 0.5f + PowerRelayMenu.IconSlotSpacing * 4f * i - 12f,
                rowY
            );

            // MOD: added — slot 0 always represents the track's special first-delivery item (see
            // ModConfig.PowerRelayFirstShardItemId/PowerRelayFirstBarItemId's own remarks), regardless of
            // how many raw items have actually been delivered so far.
            SObject slotIcon = i == 0 ? firstIcon : icon;

            // MOD: three-tier visual matching PowerSiloMenu's own capacity grid —
            // delivered slots are full opacity/untinted; the NEXT slot to be filled pulses
            // between a full black tint and no tint at all (a preview of what's coming next); everything
            // beyond that sits dimmed with a full black tint.
            Color tint;
            float alpha;
            if (i < deliveredCount)
            {
                tint = Color.White;
                alpha = 1f;
            }
            else if (i == deliveredCount)
            {
                float pulse = ((float)Math.Sin(this.AnimationTimer * PowerRelayMenu.NextTierPulseSpeed) + 1f) / 2f;
                tint = Color.Lerp(Color.Black, Color.White, pulse);
                alpha = 0.5f;
            }
            else
            {
                tint = Color.Black;
                alpha = 0.5f;
            }

            slotIcon.drawInMenu(b, iconPosition, 0.75f, alpha, 0f, StackDrawType.Hide, tint, drawShadow: false);

            // MOD: a per-slot cost label (this slot's own level cost — see PowerRelaySystem.GetLevelCost).
            // MOD: fixed — checks the actual cost rather than just "i > 0": with
            // the 1/1/2/3 cost curve, slot 1 (level 2) now costs 1 too, same as slot 0, so it needs the
            // same "no badge for a cost of 1" treatment rather than showing a redundant "1". Only drawn
            // once a slot has actually come up (filled, or the current pulsing "next" slot) — a slot
            // still fully black-tinted (i.e. not reached yet) shows no number at all.
            int slotCost = PowerRelaySystem.GetLevelCost(i + 1);
            if (slotCost > 1 && i <= deliveredCount)
                Utility.drawTinyDigits(slotCost, b, iconPosition + new Vector2(40f, 38f), 3f, 1f, Color.White);
        }
    }

    /// <summary>Get this Relay's own delay-reduction contribution (not the save-wide total).</summary>
    /// <param name="relay">The Power Relay building.</param>
    /// <param name="powerRelaySystem">The power relay system.</param>
    private static float GetThisRelayDelayReduction(Building relay, PowerRelaySystem powerRelaySystem)
    {
        // this Relay's own level as a fraction of the save-wide total levels, applied to the save-wide
        // reduction — avoids needing a second per-shard-value accessor on PowerRelaySystem purely for a
        // single-Relay display figure.
        int totalShardLevels = powerRelaySystem.GetTotalShardLevels();
        if (totalShardLevels <= 0)
            return 0f;

        int thisRelayShardLevel = powerRelaySystem.GetShardLevel(relay);
        return powerRelaySystem.GetActionDelayReduction() * thisRelayShardLevel / totalShardLevels;
    }

    /// <summary>Get this Relay's own actions-per-window bonus contribution (not the save-wide total).</summary>
    /// <param name="relay">The Power Relay building.</param>
    /// <param name="powerRelaySystem">The power relay system.</param>
    private static int GetThisRelayActionsBonus(Building relay, PowerRelaySystem powerRelaySystem)
    {
        int totalBarLevels = powerRelaySystem.GetTotalBarLevels();
        if (totalBarLevels <= 0)
            return 0;

        int thisRelayBarLevel = powerRelaySystem.GetBarLevel(relay);
        return powerRelaySystem.GetActionsPerDelayWindowBonus() * thisRelayBarLevel / totalBarLevels;
    }

    /// <summary>Get the main zone's total height (title-tag-relative) — fixed, since the icon rows are always the same shape.</summary>
    private static int GetMainZoneHeight()
    {
        int gridHeight = (int)(PowerRelayMenu.IconSlotSpacing * 4f * 2 + PowerRelayMenu.IconRowGap);
        int flavorTextHeight = (int)Game1.smallFont.MeasureString(Game1.parseText(PowerRelayMenu.DefaultFlavorText, Game1.smallFont, PowerRelayMenu.MenuWidth - IClickableMenu.spaceToClearSideBorder * 2)).Y;
        int contentHeight = PowerRelayMenu.LineHeight * 3 + PowerRelayMenu.ElementGap + gridHeight + PowerRelayMenu.ElementGap + flavorTextHeight;
        return contentHeight + PowerRelayMenu.BodyTopMargin + PowerRelayMenu.BodyBottomMargin;
    }

    /// <summary>Get the bottom zone's total height — 0, 1, or 2 status lines depending on which track(s) are maxed, plus the usual top/bottom margins either way (even when both are maxed and the zone is blank).</summary>
    /// <param name="shardsMaxed">Whether the delay-reduction track is maxed.</param>
    /// <param name="barsMaxed">Whether the actions-per-window track is maxed.</param>
    private static int GetBottomZoneHeight()
    {
        int contentHeight = 2 * PowerRelayMenu.BringRowHeight;
        return PowerRelayMenu.BottomTopMargin + contentHeight + PowerRelayMenu.BottomBottomMargin;
    }

    /// <summary>Compute this menu's total height up front, so it can be centered and sized correctly before anything is drawn — mirrors the exact same per-zone math <see cref="draw"/> uses, so the two never drift apart. Fixed, since neither zone's shape depends on the Relay's own state anymore (the bottom zone always shows 2 rows now, maxed or not).</summary>
    private static int ComputeHeight()
    {
        return PowerRelayMenu.TitleZoneHeight + PowerRelayMenu.GetMainZoneHeight() + PowerRelayMenu.GetBottomZoneHeight();
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patch giving a Category Whitelist/Blacklist sign (see
/// <see cref="ModConfig.WhitelistCategorySignNames"/>/<see cref="ModConfig.BlacklistCategorySignNames"/>)
/// a quick visual confirmation of what category it's currently filtering by. Clicking the SAME item
/// onto the sign again — the same "re-place to confirm" gesture <see cref="SignFilterPatches"/> uses
/// to bump the numeric counter on item-based signs — shows a short HUD message with the item's
/// effective category (see <see cref="SignFilter.GetEffectiveCategory"/> — a configured custom
/// category wins over the item's vanilla one), or <c>"..."</c> with an error icon if the item can't be
/// resolved to any category at all. Vanilla's above-head speech bubble (<c>NPC.showTextAboveHead</c>) is
/// declared exclusively on <see cref="StardewValley.NPC"/> — its backing state doesn't exist on
/// <see cref="Farmer"/>/<see cref="Character"/> at all — so a HUD message is used instead of a literal
/// floating bubble over the player.
/// </summary>
internal static class CategorySignPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the configured category whitelist sign names/IDs, set via <see cref="Initialize"/>.</summary>
    private static Func<HashSet<string>>? GetWhitelistCategorySignNames;

    /// <summary>Get the configured category blacklist sign names/IDs, set via <see cref="Initialize"/>.</summary>
    private static Func<HashSet<string>>? GetBlacklistCategorySignNames;

    /// <summary>Get the configured custom categories, set via <see cref="Initialize"/>.</summary>
    private static Func<Dictionary<string, HashSet<string>>>? GetCustomCategories;

    /// <summary>MOD: added. The solid backdrop color drawn behind the icon in the HUD message when triggered from a whitelist category sign, matching that sign's near-white overlay tint.</summary>
    private static readonly Color WhitelistBackdropColor = new(235, 235, 235);

    /// <summary>MOD: added. The solid backdrop color drawn behind the icon in the HUD message when triggered from a blacklist category sign, matching that sign's dark overlay tint.</summary>
    private static readonly Color BlacklistBackdropColor = new(85, 85, 85);


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the config accessors needed to recognize which signs this applies to. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="getWhitelistCategorySignNames">Get the configured category whitelist sign names/IDs.</param>
    /// <param name="getBlacklistCategorySignNames">Get the configured category blacklist sign names/IDs.</param>
    /// <param name="getCustomCategories">Get the configured custom categories.</param>
    public static void Initialize(Func<HashSet<string>> getWhitelistCategorySignNames, Func<HashSet<string>> getBlacklistCategorySignNames, Func<Dictionary<string, HashSet<string>>> getCustomCategories)
    {
        CategorySignPatches.GetWhitelistCategorySignNames = getWhitelistCategorySignNames;
        CategorySignPatches.GetBlacklistCategorySignNames = getBlacklistCategorySignNames;
        CategorySignPatches.GetCustomCategories = getCustomCategories;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Sign), nameof(Sign.checkForAction), [typeof(Farmer), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(CategorySignPatches), nameof(CheckForAction_Prefix)),
            postfix: new HarmonyMethod(typeof(CategorySignPatches), nameof(CheckForAction_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Capture the sign's displayed item before vanilla's own logic replaces it, so the postfix can tell whether the same item was placed again.</summary>
    /// <param name="__instance">The sign being interacted with.</param>
    /// <param name="justCheckingForActivity">Whether this is just a passive check rather than a real interaction — ignored here since nothing should change either way.</param>
    /// <param name="__state">The sign's displayed item's qualified ID before vanilla's own logic runs, or <c>null</c> if this isn't a category sign (or the check doesn't apply).</param>
    private static void CheckForAction_Prefix(Sign __instance, bool justCheckingForActivity, out string? __state)
    {
        __state = null;

        if (justCheckingForActivity || CategorySignPatches.GetSignKind(__instance) == SignKind.None)
            return;

        __state = __instance.displayItem.Value?.QualifiedItemId;
    }

    /// <summary>Show a HUD message with the item's effective category if the same item was placed on the sign again.</summary>
    /// <param name="__instance">The sign being interacted with.</param>
    /// <param name="__result">Whether vanilla's own logic accepted the interaction (i.e. an item was actually placed).</param>
    /// <param name="__state">The sign's displayed item's qualified ID before vanilla's own logic ran, captured by <see cref="CheckForAction_Prefix"/>.</param>
    private static void CheckForAction_Postfix(Sign __instance, bool __result, string? __state)
    {
        if (!__result || __state == null)
            return;

        Item? newItem = __instance.displayItem.Value;
        if (newItem == null || newItem.QualifiedItemId != __state)
            return; // a different item was placed, or the sign was emptied — not a "re-place the same item" confirmation gesture

        SignKind kind = CategorySignPatches.GetSignKind(__instance);
        if (kind == SignKind.None)
            return;

        Dictionary<string, HashSet<string>> customCategories = CategorySignPatches.GetCustomCategories!();
        string? categoryText = SignFilter.GetCategoryDisplayText(newItem.QualifiedItemId, customCategories);
        Color backdropColor = kind == SignKind.Blacklist ? CategorySignPatches.BlacklistBackdropColor : CategorySignPatches.WhitelistBackdropColor;

        ColoredHudMessage message = new(categoryText ?? "...", backdropColor);
        if (categoryText != null)
            message.messageSubject = newItem;
        else
            message.whatType = HUDMessage.error_type; // unresolvable item — show the error icon instead of the (nonexistent) item icon

        Game1.addHUDMessage(message);
    }

    /// <summary>MOD: added. Which kind of category sign a <see cref="Sign"/> is configured as.</summary>
    private enum SignKind
    {
        /// <summary>Not a category sign.</summary>
        None,

        /// <summary>A whitelist category sign.</summary>
        Whitelist,

        /// <summary>A blacklist category sign.</summary>
        Blacklist
    }

    /// <summary>Get which kind of category whitelist/blacklist filter sign a sign is configured as, if any.</summary>
    /// <param name="sign">The sign to check.</param>
    private static SignKind GetSignKind(Sign sign)
    {
        if (CategorySignPatches.GetWhitelistCategorySignNames == null || CategorySignPatches.GetBlacklistCategorySignNames == null)
            return SignKind.None;

        HashSet<string> whitelist = CategorySignPatches.GetWhitelistCategorySignNames();
        if (whitelist.Contains(sign.QualifiedItemId) || whitelist.Contains(sign.Name))
            return SignKind.Whitelist;

        HashSet<string> blacklist = CategorySignPatches.GetBlacklistCategorySignNames();
        if (blacklist.Contains(sign.QualifiedItemId) || blacklist.Contains(sign.Name))
            return SignKind.Blacklist;

        return SignKind.None;
    }

    /// <summary>
    /// MOD: added. A <see cref="HUDMessage"/> that draws a solid-color backdrop directly behind the icon
    /// instead of leaving it bare, so the popup can reflect which kind of category sign triggered it
    /// (near-white for whitelist, dark gray for blacklist — matching those signs' own overlay tint). Uses
    /// <see cref="Game1.staminaRect"/> (a plain solid-white 1x1 texture already used for the game's own
    /// solid-color UI fills, e.g. the stamina/health bars) rather than tinting the ornate item-box border
    /// graphic itself — that border's art already has its own baked-in shading, so a tint on it barely
    /// reads as a color change; a flat rect behind the icon shows the color clearly regardless. The
    /// message text itself stays the normal <see cref="Game1.textColor"/>. <see cref="draw"/> otherwise
    /// mirrors vanilla's <c>HUDMessage.draw</c> exactly (same layout math, same icon/border logic), since
    /// the base method has no hook to add this backdrop without a full override.
    /// </summary>
    private sealed class ColoredHudMessage : HUDMessage
    {
        /// <summary>The solid color to draw directly behind the icon.</summary>
        private readonly Color BackdropColor;

        /// <summary>Construct an instance.</summary>
        /// <param name="message">The message text to show.</param>
        /// <param name="backdropColor">The solid color to draw directly behind the icon.</param>
        public ColoredHudMessage(string message, Color backdropColor)
            : base(message)
        {
            this.BackdropColor = backdropColor;
        }

        /// <inheritdoc />
        public override void draw(SpriteBatch b, int i, ref int heightUsed)
        {
            Rectangle tsarea = Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea();
            int height = 112;
            Vector2 itemBoxPosition = new(tsarea.Left + 16, tsarea.Bottom - height - heightUsed - 64);
            heightUsed += height;
            if (Game1.isOutdoorMapSmallerThanViewport())
                itemBoxPosition.X = Math.Max(tsarea.Left + 16, -Game1.uiViewport.X + 16);
            if (Game1.uiViewport.Width < 1400)
                itemBoxPosition.Y -= 48f;

            b.Draw(Game1.mouseCursors, itemBoxPosition, this.messageSubject is SObject obj && obj.sellToStorePrice(-1L) > 500 ? new Rectangle(163, 399, 26, 24) : new Rectangle(293, 360, 26, 24), Color.White * this.transparency, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            float messageWidth = Game1.smallFont.MeasureString(this.message ?? "").X;
            b.Draw(Game1.mouseCursors, new Vector2(itemBoxPosition.X + 104f, itemBoxPosition.Y), new Rectangle(319, 360, 1, 24), Color.White * this.transparency, 0f, Vector2.Zero, new Vector2(messageWidth, 4f), SpriteEffects.None, 1f);
            b.Draw(Game1.mouseCursors, new Vector2(itemBoxPosition.X + 104f + messageWidth, itemBoxPosition.Y), new Rectangle(323, 360, 6, 24), Color.White * this.transparency, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            itemBoxPosition.X += 16f;
            itemBoxPosition.Y += 16f;

            b.Draw(Game1.staminaRect, new Rectangle((int)itemBoxPosition.X, (int)itemBoxPosition.Y, 64, 64), this.BackdropColor * this.transparency);

            if (this.messageSubject == null)
            {
                Rectangle? iconSourceRect = this.whatType switch
                {
                    1 => new Rectangle(294, 392, 16, 16),
                    2 => new Rectangle(403, 496, 5, 14),
                    3 => new Rectangle(268, 470, 16, 16),
                    4 => new Rectangle(0, 411, 16, 16),
                    5 => new Rectangle(16, 411, 16, 16),
                    6 => new Rectangle(96, 32, 16, 16),
                    _ => null
                };
                if (iconSourceRect != null)
                {
                    Texture2D texture = this.whatType == 6 ? Game1.mouseCursors2 : Game1.mouseCursors;
                    Vector2 origin = this.whatType == 2 ? new Vector2(3f, 7f) : new Vector2(8f, 8f);
                    b.Draw(texture, itemBoxPosition + new Vector2(8f, 8f) * 4f, iconSourceRect, Color.White * this.transparency, 0f, origin, 4f + Math.Max(0f, (this.timeLeft - 3000f) / 900f), SpriteEffects.None, 1f);
                }
            }
            else
            {
                this.messageSubject.drawInMenu(b, itemBoxPosition, 1f + Math.Max(0f, (this.timeLeft - 3000f) / 900f), this.transparency, 1f, StackDrawType.Hide);
            }

            itemBoxPosition.X += 51f;
            itemBoxPosition.Y += 51f;
            if (this.number > 1)
                Utility.drawTinyDigits(this.number, b, itemBoxPosition, 3f, 1f, Color.White * this.transparency);
            itemBoxPosition.X += 32f;
            itemBoxPosition.Y -= 33f;
            Utility.drawTextWithShadow(b, this.message ?? "", Game1.smallFont, itemBoxPosition, Game1.textColor * this.transparency, 1f, 1f, -1, -1, this.transparency);
        }
    }
}

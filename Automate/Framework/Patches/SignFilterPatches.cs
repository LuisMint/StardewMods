using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Harmony patches giving whitelist/blacklist signs (see <see cref="ModConfig.WhitelistSignNames"/>/<see cref="ModConfig.BlacklistSignNames"/>)
/// a numeric condition: clicking the SAME item onto a sign repeatedly (instead of a different one)
/// bumps a counter shown as a stack-style number in the sign's bottom-right corner, up to 999. This
/// repurposes the displayed item's own <see cref="Item.Stack"/> as the counter rather than adding new
/// mod data — a fresh placement is always <c>Stack == 1</c> (vanilla's own <c>getOne()</c> default),
/// which is treated as "no numeric condition" (the plain type-only filter behavior from before this
/// feature existed); <c>Stack >= 2</c> means a numeric condition of <c>Stack - 1</c>. See
/// <see cref="SignFilter"/> for how the resulting condition is actually enforced. Vanilla's own Wood
/// Sign/Stone Sign (and any other sign not configured as a whitelist/blacklist sign) are left
/// untouched, since neither patch does anything unless <see cref="IsFilterSign"/> matches.
/// </summary>
internal static class SignFilterPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The highest <see cref="Item.Stack"/> value the counter may reach (displayed as 999).</summary>
    private const int MaxStack = 1000;

    /// <summary>Get the configured whitelist sign names/IDs, set via <see cref="Initialize"/>.</summary>
    private static Func<HashSet<string>>? GetWhitelistSignNames;

    /// <summary>Get the configured blacklist sign names/IDs, set via <see cref="Initialize"/>.</summary>
    private static Func<HashSet<string>>? GetBlacklistSignNames;


    /*********
    ** Public methods
    *********/
    /// <summary>Provide the config accessors needed to recognize which signs this applies to. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="getWhitelistSignNames">Get the configured whitelist sign names/IDs.</param>
    /// <param name="getBlacklistSignNames">Get the configured blacklist sign names/IDs.</param>
    public static void Initialize(Func<HashSet<string>> getWhitelistSignNames, Func<HashSet<string>> getBlacklistSignNames)
    {
        SignFilterPatches.GetWhitelistSignNames = getWhitelistSignNames;
        SignFilterPatches.GetBlacklistSignNames = getBlacklistSignNames;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Sign), nameof(Sign.checkForAction), [typeof(Farmer), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(SignFilterPatches), nameof(CheckForAction_Prefix)),
            postfix: new HarmonyMethod(typeof(SignFilterPatches), nameof(CheckForAction_Postfix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(Sign), nameof(Sign.draw), [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]),
            postfix: new HarmonyMethod(typeof(SignFilterPatches), nameof(Draw_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Capture the sign's displayed item before vanilla's own logic replaces it, so the postfix can tell whether the same item was placed again.</summary>
    /// <param name="__instance">The sign being interacted with.</param>
    /// <param name="justCheckingForActivity">Whether this is just a passive check rather than a real interaction — ignored here since nothing should change either way.</param>
    /// <param name="__state">The sign's displayed item's qualified ID and stack size before vanilla's own logic runs, or <c>null</c> if this isn't a filter sign (or the check doesn't apply).</param>
    private static void CheckForAction_Prefix(Sign __instance, bool justCheckingForActivity, out (string? ItemId, int Stack)? __state)
    {
        __state = null;

        if (justCheckingForActivity || !SignFilterPatches.IsFilterSign(__instance))
            return;

        Item? current = __instance.displayItem.Value;
        __state = (current?.QualifiedItemId, current?.Stack ?? 0);
    }

    /// <summary>Bump the numeric counter if the same item was placed on the sign again.</summary>
    /// <param name="__instance">The sign being interacted with.</param>
    /// <param name="__result">Whether vanilla's own logic accepted the interaction (i.e. an item was actually placed).</param>
    /// <param name="__state">The sign's displayed item before vanilla's own logic ran, captured by <see cref="CheckForAction_Prefix"/>.</param>
    private static void CheckForAction_Postfix(Sign __instance, bool __result, (string? ItemId, int Stack)? __state)
    {
        if (!__result || __state == null)
            return;

        Item? newItem = __instance.displayItem.Value;
        if (newItem == null)
            return;

        // MOD: same item placed again -> bump the counter. A different item was placed -> leave
        // vanilla's fresh Stack = 1 as-is (that's the "no numeric condition" state).
        if (newItem.QualifiedItemId == __state.Value.ItemId)
            newItem.Stack = Math.Min(SignFilterPatches.MaxStack, __state.Value.Stack + 1);
    }

    /// <summary>Draw the sign's numeric condition (if any) as a stack-style number in its bottom-right corner, after vanilla draws the plain icon.</summary>
    /// <param name="__instance">The sign being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    private static void Draw_Postfix(Sign __instance, SpriteBatch spriteBatch, int x, int y)
    {
        Item? displayItem = __instance.displayItem.Value;
        if (displayItem is not { Stack: >= 2 } || !SignFilterPatches.IsFilterSign(__instance))
            return;

        int number = displayItem.Stack - 1;

        // MOD: mirrors the X offset Sign.draw itself uses for the foreground icon copy per
        // displayType (reusing the same field vanilla's own checkForAction already set, rather than
        // re-deriving "what kind of item is this" ourselves), and the same digit-position formula
        // vanilla's own Item.DrawMenuIcons uses relative to a standard 64x64 icon box, scaled for the
        // 0.75f size Sign.draw itself draws its icon at.
        int xOffset = __instance.displayType.Value switch
        {
            2 or 1 => 1,
            4 => -1,
            _ => 0
        };
        const float iconScale = 0.75f;
        float digitScale = 3f * iconScale;

        Vector2 basePosition = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 + xOffset, y * 64 - 64 + 24));
        float digitWidth = Utility.getWidthOfTinyDigitString(number, digitScale);
        Vector2 digitPosition = basePosition + new Vector2(64f - digitWidth + digitScale, 64f - 18f * iconScale + 1f);

        float layerDepth = Math.Max(0f, (float)((y + 1) * 64 - 24) / 10000f) + x * 1E-05f + 3E-05f;
        Utility.drawTinyDigits(number, spriteBatch, digitPosition, digitScale, layerDepth, Color.White);
    }

    /// <summary>Get whether a sign is configured as a whitelist/blacklist filter sign (as opposed to a plain decorative sign), matching the same name/ID check <see cref="MachineGroupFactory"/> uses to detect them.</summary>
    /// <param name="sign">The sign to check.</param>
    private static bool IsFilterSign(Sign sign)
    {
        if (SignFilterPatches.GetWhitelistSignNames == null || SignFilterPatches.GetBlacklistSignNames == null)
            return false;

        HashSet<string> whitelist = SignFilterPatches.GetWhitelistSignNames();
        HashSet<string> blacklist = SignFilterPatches.GetBlacklistSignNames();

        return
            whitelist.Contains(sign.QualifiedItemId) || whitelist.Contains(sign.Name)
            || blacklist.Contains(sign.QualifiedItemId) || blacklist.Contains(sign.Name);
    }
}

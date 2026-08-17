using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Swaps a whitelist/blacklist (item or category) sign's world sprite to its dedicated
/// "_UnPowered" texture while it isn't currently enforcing its filter — replaces the previous
/// Alternative Textures-driven swap (<see cref="SignTextureSync"/> used to write AT's own
/// <c>modData</c> keys; it now writes <see cref="SignValidModDataKey"/> here instead), following the
/// same shape as <see cref="PowerCoilPatches"/>'s own powered/unpowered texture swap
/// (<see cref="PowerCoilPatches.IsPowered"/>/<see cref="PowerCoilPatches.Draw_Prefix"/>), just without
/// any pulse/shake/light-source complexity, since a sign is a plain static, standard-sized (16x32,
/// i.e. vanilla's own default BigCraftable source height) sprite either way — no lighting involvement
/// at all.
/// </summary>
internal static class SignValidityPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The <see cref="SObject.modData"/> key storing whether a managed sign is currently valid (enforcing its filter) — written by <see cref="SignTextureSync"/>, read here at draw time.</summary>
    internal const string SignValidModDataKey = "luisMint.PoweredAutomation/SignValid";

    /// <summary>Every managed sign's qualified item ID mapped to the asset name of its dedicated "invalid" (not currently enforcing) texture.</summary>
    private static readonly Dictionary<string, string> UnpoweredAssetNamesByQualifiedItemId = new()
    {
        ["(BC)luisMint.PoweredAutomation_WhitelistSign"] = "Mods/luisMint.PoweredAutomation/WhitelistSign_UnPowered",
        ["(BC)luisMint.PoweredAutomation_BlacklistSign"] = "Mods/luisMint.PoweredAutomation/BlacklistSign_UnPowered",
        ["(BC)luisMint.PoweredAutomation_WhitelistCategorySign"] = "Mods/luisMint.PoweredAutomation/WhitelistCategorySign_UnPowered",
        ["(BC)luisMint.PoweredAutomation_BlacklistCategorySign"] = "Mods/luisMint.PoweredAutomation/BlacklistCategorySign_UnPowered"
    };


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(SObject), nameof(SObject.draw), [typeof(SpriteBatch), typeof(int), typeof(int), typeof(float)]),
            prefix: new HarmonyMethod(typeof(SignValidityPatches), nameof(Draw_Prefix))
        );
    }

    /// <summary>Get whether a qualified item ID is one of the signs managed by this class (i.e. has a dedicated "_UnPowered" texture to swap to).</summary>
    /// <param name="qualifiedItemId">The qualified item ID to check.</param>
    internal static bool IsManagedSign(string qualifiedItemId)
    {
        return SignValidityPatches.UnpoweredAssetNamesByQualifiedItemId.ContainsKey(qualifiedItemId);
    }

    /// <summary>
    /// Get whether a managed sign is currently valid (enforcing its filter). MOD: unlike
    /// <see cref="PowerCoilPatches.IsPowered"/>, a MISSING flag defaults to <c>false</c> (invalid) here,
    /// not <c>true</c> — the Power Coil's "missing = powered" default exists specifically so a coil
    /// placed before that mechanic existed doesn't look broken; signs have always had this validity
    /// tracking, so there's no equivalent pre-existing-save case to protect, and the safer default for a
    /// sign that just hasn't been synced yet (e.g. the very first frame after placement, before the next
    /// machine-group rebuild) is to render/collide as "not yet confirmed valid" rather than assume the best.
    /// </summary>
    /// <param name="sign">The sign object instance.</param>
    internal static bool IsValid(SObject sign)
    {
        return sign.modData.TryGetValue(SignValidityPatches.SignValidModDataKey, out string? raw) && raw == "true";
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Draw a managed sign's dedicated "invalid" texture in place of its normal one while it isn't currently enforcing its filter.</summary>
    /// <param name="__instance">The object being drawn.</param>
    /// <param name="spriteBatch">The sprite batch being drawn to.</param>
    /// <param name="x">The tile X position being drawn.</param>
    /// <param name="y">The tile Y position being drawn.</param>
    /// <param name="alpha">The transparency at which to draw the sprite.</param>
    /// <returns>Returns <c>false</c> to skip the original method for this item, or <c>true</c> to let it run normally for every other item.</returns>
    private static bool Draw_Prefix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
    {
        if (!SignValidityPatches.UnpoweredAssetNamesByQualifiedItemId.TryGetValue(__instance.QualifiedItemId, out string? unpoweredAssetName))
            return true;

        if (SignValidityPatches.IsValid(__instance))
            return true; // normal texture — let vanilla's own draw handle it as usual

        // MOD: same standard-BigCraftable anchor math vanilla's own Object.draw uses (see
        // PowerCoilPatches.Draw_Prefix's own remarks for the derivation) — reduces to exactly vanilla's
        // normal positioning here since a sign's texture is the standard 16x32 (2-tile) size, unlike
        // the Power Coil's taller one.
        Texture2D texture = Game1.content.Load<Texture2D>(unpoweredAssetName);
        Rectangle sourceRect = new(0, 0, texture.Width, texture.Height);

        Vector2 scale = __instance.getScale() * 4f;
        float extraBaseHeight = (texture.Height * 4f) - 128f;
        int shakeJitter = __instance.shakeTimer > 0 ? Game1.random.Next(-1, 2) : 0;

        Vector2 topAnchor = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 + shakeJitter, y * 64 - 64));
        Rectangle destination = new(
            (int)(topAnchor.X - scale.X / 2f),
            (int)(topAnchor.Y - scale.Y / 2f - extraBaseHeight),
            (int)(64f + scale.X),
            (int)(texture.Height * 4f + scale.Y / 2f)
        );

        float layerDepth = System.Math.Max(0f, (float)((y + 1) * 64 - 24) / 10000f) + x * 1E-05f;
        spriteBatch.Draw(texture, destination, sourceRect, Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, layerDepth);

        return false;
    }
}

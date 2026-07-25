using System;
using System.Collections.Generic;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. A single item's resolved whitelist/blacklist condition within a machine group, after
/// combining every sign touching that group. <see cref="WhitelistNumber"/>/<see cref="BlacklistNumber"/>
/// are <c>null</c> when that sign has no numeric condition set (i.e. a plain type-only filter).
/// </summary>
/// <param name="HasWhitelist">Whether a whitelist sign for this item exists in the group.</param>
/// <param name="WhitelistNumber">The whitelist sign's numeric condition, if any.</param>
/// <param name="HasNumericBlacklist">Whether a <em>numeric</em> blacklist sign for this item exists in the group. A non-numeric blacklist is tracked separately (see <see cref="SignFilter"/>), since it's overridden entirely by a group-wide whitelist instead of combining with it.</param>
/// <param name="BlacklistNumber">The numeric blacklist sign's condition, if any.</param>
internal readonly record struct SignItemCondition(bool HasWhitelist, int? WhitelistNumber, bool HasNumericBlacklist, int? BlacklistNumber);

/// <summary>
/// MOD: added. Resolves whitelist/blacklist sign conditions for a single machine group into per-item
/// take/store rules. Replaces the previous plain <c>Func&lt;string,bool&gt;</c> item filter now that
/// signs can carry a numeric condition (see each sign's own click-to-increment counter, applied via
/// <see cref="Patches.SignFilterPatches"/>) instead of being a pure type filter.
///
/// MOD: added. Alongside the per-ITEM conditions above, a group can also have any number of
/// whitelisted categories and blacklisted categories (see <see cref="WhitelistedCategories"/>/
/// <see cref="BlacklistedCategories"/>), set by Category Whitelist/Blacklist signs — unlike the
/// item-based signs, a category sign has no numeric condition. Multiple category signs of the SAME
/// kind combine (e.g. two whitelist category signs showing different categories both apply, exactly
/// like two item-based whitelist signs for different items already do) rather than only the first one
/// counting. A category condition is just another way an item can be allowed/blocked — see
/// <see cref="IsItemTypeAllowed"/> for how it combines with the per-item conditions.
///
/// MOD: added. A "category" isn't necessarily one of the game's own built-in numeric categories — see
/// <see cref="GetEffectiveCategory"/> for how a configured custom category (e.g. grouping every kind
/// of geode, the Golden Coconut, and both Mystery Boxes into a single "Lootboxes" category) takes
/// priority over an item's vanilla category when resolving what a category sign represents, or what
/// category a given item belongs to for filtering purposes.
///
/// Resolution per item, in priority order — item-level rules always win over category-level ones,
/// since they're strictly more specific (e.g. a "Lootboxes" category whitelist plus a plain blacklist
/// sign for one specific lootbox item excludes just that one item, not the whole category — see the
/// <c>NonNumericBlacklistItems</c> check below, which runs before any category match is even
/// considered):
/// <list type="number">
/// <item>Has a whitelist entry -> allowed. Take is capped to at most the whitelist number per
/// transfer (independent of the source's own count — see <see cref="GetMaxTakeable"/>'s remarks for
/// why), further reduced by any numeric blacklist reserve for the same item. Store is capped so the
/// destination's count never exceeds the whitelist number.</item>
/// <item>Else has a non-numeric (item-level) blacklist entry -> blocked entirely, regardless of any
/// category whitelist that would otherwise allow it — an explicit per-item blacklist is more specific
/// than any category-level rule.</item>
/// <item>Else the item's effective category matches one of the group's whitelisted categories ->
/// allowed, uncapped (no numeric condition support for category signs).</item>
/// <item>Else has a NUMERIC blacklist entry -> allowed. Take is reduced to the excess above the
/// reserve (a "keep at least this many, drain the rest" rule on the container it protects). Store is
/// unrestricted — the reserve only limits what may be taken FROM this container, not what a different
/// container may receive of the same item.</item>
/// <item>Else if the group has a whitelist anywhere (item-level OR category-level) -> blocked
/// (unlisted item under an active whitelist).</item>
/// <item>Else if the item's effective category matches one of the group's blacklisted categories ->
/// blocked entirely.</item>
/// <item>Else -> fully allowed, unrestricted.</item>
/// </list>
/// </summary>
internal class SignFilter
{
    /*********
    ** Fields
    *********/
    /// <summary>The resolved condition for each item with a whitelist and/or numeric blacklist entry in this group.</summary>
    private readonly Dictionary<string, SignItemCondition> ConditionsByItemId;

    /// <summary>Items with a non-numeric blacklist entry in this group. Always excludes that specific item, EXCEPT when the same item also has an item-level whitelist entry in <see cref="ConditionsByItemId"/> — an active whitelist elsewhere in the group (item-level for a different item, or category-level) does not override this.</summary>
    private readonly HashSet<string> NonNumericBlacklistItems;

    /// <summary>Whether the group has an item-level whitelist sign for any item at all.</summary>
    private readonly bool GroupHasWhitelist;

    /// <summary>MOD: added. The group's active whitelisted categories, if any Category Whitelist signs are present — each entry is either a custom category name (<see cref="string"/>) or a vanilla category code (<see cref="int"/>), as resolved by <see cref="GetEffectiveCategory"/>. Every category in this set is allowed, same as every item in <see cref="ConditionsByItemId"/> with a whitelist entry.</summary>
    private readonly HashSet<object> WhitelistedCategories;

    /// <summary>MOD: added. The group's active blacklisted categories, if any Category Blacklist signs are present (see <see cref="WhitelistedCategories"/> for the entry format). Moot whenever <see cref="WhitelistedCategories"/> is non-empty or <see cref="GroupHasWhitelist"/> is set, since anything not matching an active whitelist is already blocked regardless.</summary>
    private readonly HashSet<object> BlacklistedCategories;

    /// <summary>MOD: added. Get the configured custom categories, each mapping a category name to the item names/qualified IDs that belong to it — see <see cref="GetEffectiveCategory"/>.</summary>
    private readonly IReadOnlyDictionary<string, HashSet<string>> CustomCategories;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="conditionsByItemId">The resolved condition for each item with a whitelist and/or numeric blacklist entry in this group.</param>
    /// <param name="nonNumericBlacklistItems">Items with a non-numeric blacklist entry in this group.</param>
    /// <param name="groupHasWhitelist">Whether the group has an item-level whitelist sign for any item at all.</param>
    /// <param name="customCategories">MOD: added. The configured custom categories, each mapping a category name to the item names/qualified IDs that belong to it.</param>
    /// <param name="whitelistedCategories">MOD: added. The group's active whitelisted categories, if any (see <see cref="WhitelistedCategories"/> for the entry format).</param>
    /// <param name="blacklistedCategories">MOD: added. The group's active blacklisted categories, if any (see <see cref="WhitelistedCategories"/> for the entry format).</param>
    public SignFilter(Dictionary<string, SignItemCondition> conditionsByItemId, HashSet<string> nonNumericBlacklistItems, bool groupHasWhitelist, IReadOnlyDictionary<string, HashSet<string>>? customCategories = null, HashSet<object>? whitelistedCategories = null, HashSet<object>? blacklistedCategories = null)
    {
        this.ConditionsByItemId = conditionsByItemId;
        this.NonNumericBlacklistItems = nonNumericBlacklistItems;
        this.GroupHasWhitelist = groupHasWhitelist;
        this.CustomCategories = customCategories ?? new Dictionary<string, HashSet<string>>();
        this.WhitelistedCategories = whitelistedCategories ?? [];
        this.BlacklistedCategories = blacklistedCategories ?? [];
    }

    /// <summary>
    /// MOD: added. Get an item's "effective" category for filtering purposes — a configured custom
    /// category (see <see cref="ModConfig.CustomCategories"/>) if the item belongs to one, since a
    /// custom category is a deliberate, more specific regrouping that should win over the game's own
    /// default (e.g. every geode, the Golden Coconut, and both Mystery Boxes could be grouped into a
    /// single custom "Lootboxes" category even though they don't share a vanilla one) — otherwise the
    /// item's own vanilla category code, AS-IS (including 0 — many big-craftables and tools have no
    /// vanilla category assigned, but 0 is still a specific, consistent code shared by every item in
    /// that boat, so it's just as valid a grouping as any other). Returns <c>null</c> only when the item
    /// itself can't be resolved at all (an unknown/invalid item ID). The returned value is either a
    /// <see cref="string"/> (custom category name) or a boxed <see cref="int"/> (vanilla category code,
    /// possibly 0); the two spaces never collide since a vanilla category code is never a positive
    /// number matching a plausible custom-category hash, and callers distinguish them by type anyway.
    /// </summary>
    /// <param name="itemId">The qualified item ID to look up.</param>
    /// <param name="customCategories">The configured custom categories, each mapping a category name to the item names/qualified IDs that belong to it.</param>
    public static object? GetEffectiveCategory(string itemId, IReadOnlyDictionary<string, HashSet<string>> customCategories)
    {
        ParsedItemData? data = ItemRegistry.GetData(itemId);

        foreach ((string categoryName, HashSet<string> members) in customCategories)
        {
            if (members.Contains(itemId) || (data != null && members.Contains(data.InternalName)))
                return categoryName;
        }

        return data != null ? data.Category : null;
    }

    /// <summary>MOD: added. Display name overrides for vanilla category codes whose own name isn't a stable/generic label to show on a category sign. Keyed by the category code so it applies uniformly to any item with that code — vanilla or modded — without needing a custom category list.</summary>
    /// <remarks>
    /// Currently just <see cref="SObject.weaponCategory"/>: every sword, dagger, club, and slingshot
    /// (vanilla or added by another mod's <c>Data/Weapons</c> entries) resolves to this same category
    /// code via <see cref="ItemRegistry"/>'s weapon data definition (scythes excluded — they resolve to
    /// the tool category instead, since they're tools, not weapons). Vanilla's own
    /// <see cref="SObject.GetCategoryDisplayName"/> has no text for this code at all (it returns an
    /// empty string), and a live item's own <c>getCategoryName()</c> isn't a stable substitute either —
    /// <c>MeleeWeapon</c> overrides it to something per-item like "Level 5 Sword", and <c>Slingshot</c>
    /// doesn't override it at all, so it falls back to <c>Tool</c>'s always-on category override and
    /// misreports itself as "Tool" — so a plain shared label is filled in here instead.
    /// </remarks>
    private static readonly Dictionary<int, string> VanillaCategoryDisplayNameOverrides = new()
    {
        [SObject.weaponCategory] = "Weapon"
    };

    /// <summary>MOD: added. Get the plain-text name for a vanilla category code, preferring <see cref="VanillaCategoryDisplayNameOverrides"/> over the game's own <see cref="SObject.GetCategoryDisplayName"/> — or an empty string if neither has one (e.g. category 0, or the big-craftable category).</summary>
    /// <param name="category">The vanilla category code, as resolved by <see cref="GetEffectiveCategory"/>.</param>
    public static string GetVanillaCategoryDisplayName(int category)
    {
        return SignFilter.VanillaCategoryDisplayNameOverrides.TryGetValue(category, out string? name)
            ? name
            : SObject.GetCategoryDisplayName(category);
    }

    /// <summary>
    /// MOD: added. Get the full display text for an item's effective category, for a category sign's
    /// confirmation message. A custom category (see <see cref="ModConfig.CustomCategories"/>) is shown
    /// as just its plain name (e.g. <c>"Lootboxes"</c>) with no number, since it's a deliberate label of
    /// its own rather than a real vanilla category. Otherwise, shows the vanilla category's plain-text
    /// name (if it has one) followed by its underlying category number in parentheses, e.g.
    /// <c>"Weapon(-98)"</c>, or just <c>"(-9)"</c> when the category has no plain-text name at all (the
    /// number is always shown here, since it's always a real, meaningful value even when nothing has
    /// given it a name — see <see cref="GetEffectiveCategory"/>'s remarks on category 0). Returns
    /// <c>null</c> if the item can't be resolved at all (an unknown/invalid item ID).
    /// </summary>
    /// <param name="itemId">The qualified item ID to look up.</param>
    /// <param name="customCategories">The configured custom categories, each mapping a category name to the item names/qualified IDs that belong to it.</param>
    public static string? GetCategoryDisplayText(string itemId, IReadOnlyDictionary<string, HashSet<string>> customCategories)
    {
        ParsedItemData? data = ItemRegistry.GetData(itemId);
        if (data == null)
            return null;

        return SignFilter.GetEffectiveCategory(itemId, customCategories) switch
        {
            string customCategoryName => customCategoryName,
            int vanillaCategory => $"{SignFilter.GetVanillaCategoryDisplayName(vanillaCategory)}({vanillaCategory})",
            _ => $"({data.Category})"
        };
    }

    /// <summary>Get whether an item type may move through this group's storage at all, ignoring quantity.</summary>
    /// <param name="itemId">The qualified item ID to check.</param>
    public bool IsItemTypeAllowed(string itemId)
    {
        if (this.ConditionsByItemId.ContainsKey(itemId))
            return true; // has a whitelist and/or numeric blacklist entry

        // MOD: an explicit item-level (non-numeric) blacklist entry always excludes this one item,
        // even if its category would otherwise be allowed by an active category whitelist — item-level
        // rules are strictly more specific than category-level ones, so this has to be checked before
        // any category match below, not after.
        if (this.NonNumericBlacklistItems.Contains(itemId))
            return false;

        object? effectiveCategory = null;
        if (this.WhitelistedCategories.Count > 0 || this.BlacklistedCategories.Count > 0)
            effectiveCategory = SignFilter.GetEffectiveCategory(itemId, this.CustomCategories);

        if (effectiveCategory != null && this.WhitelistedCategories.Contains(effectiveCategory))
            return true; // item's effective category matches one of the group's whitelisted categories

        if (this.GroupHasWhitelist || this.WhitelistedCategories.Count > 0)
            return false; // unlisted item under an active whitelist (item-level or category-level)

        if (effectiveCategory != null && this.BlacklistedCategories.Contains(effectiveCategory))
            return false; // item's effective category matches one of the group's blacklisted categories

        return true;
    }

    /// <summary>
    /// Get the maximum amount of an item that may be taken out of a container right now.
    /// </summary>
    /// <param name="itemId">The qualified item ID being taken.</param>
    /// <param name="currentCount">The container being taken FROM (the source)'s current total count of this item.</param>
    /// <param name="requested">The amount the caller wants to take.</param>
    /// <remarks>
    /// MOD: a numeric whitelist here is a flat cap on how much may move in a single transfer (e.g.
    /// whitelist 4 iron ore means at most 4 iron ore may ever be taken at once, even if the source
    /// chest has 999 and a machine recipe wants 5 — it just won't have enough to fire) — it does NOT
    /// depend on the source's own count, since the destination (a machine consuming ingredients has
    /// no stored "count" to check against a chest) is what the condition is really about. A numeric
    /// blacklist, in contrast, IS about the source: it reserves that many units in the source
    /// container that can never be taken, so it naturally depends on the source's current count. A
    /// category condition (see <see cref="IsItemTypeAllowed"/>) has neither — it's a plain type gate,
    /// so it falls through to the unrestricted/blocked fallback at the bottom.
    /// </remarks>
    public int GetMaxTakeable(string itemId, int currentCount, int requested)
    {
        if (requested <= 0)
            return 0;

        if (this.ConditionsByItemId.TryGetValue(itemId, out SignItemCondition condition))
        {
            int reserve = condition.HasNumericBlacklist ? condition.BlacklistNumber ?? 0 : 0;
            int result = Math.Min(requested, Math.Max(0, currentCount - reserve));

            if (condition.HasWhitelist && condition.WhitelistNumber.HasValue)
                result = Math.Min(result, condition.WhitelistNumber.Value);

            return result;
        }

        return this.IsItemTypeAllowed(itemId) ? requested : 0;
    }

    /// <summary>Get the maximum amount of an item that may be stored into a container right now.</summary>
    /// <param name="itemId">The qualified item ID being stored.</param>
    /// <param name="currentCount">The container's current total count of this item.</param>
    /// <param name="requested">The amount the caller wants to store.</param>
    public int GetMaxStorable(string itemId, int currentCount, int requested)
    {
        if (requested <= 0)
            return 0;

        if (this.ConditionsByItemId.TryGetValue(itemId, out SignItemCondition condition) && condition.HasWhitelist)
        {
            int cap = condition.WhitelistNumber ?? int.MaxValue;
            return Math.Min(requested, Math.Max(0, cap - currentCount));
        }

        // MOD: a numeric-blacklist-only item condition, and a category condition, don't restrict
        // storage at all — see IsItemTypeAllowed's remarks for the type gate, and GetMaxTakeable's
        // remarks for why a numeric blacklist's reserve only limits taking, not storing.
        return this.IsItemTypeAllowed(itemId) ? requested : 0;
    }
}

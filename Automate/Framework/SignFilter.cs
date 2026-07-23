using System.Collections.Generic;

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
/// Resolution per item, in priority order:
/// <list type="number">
/// <item>Has a whitelist entry -> allowed. Take is capped to at most the whitelist number per
/// transfer (independent of the source's own count — see <see cref="GetMaxTakeable"/>'s remarks for
/// why), further reduced by any numeric blacklist reserve for the same item. Store is capped so the
/// destination's count never exceeds the whitelist number.</item>
/// <item>Else has a NUMERIC blacklist entry -> allowed. Take is reduced to the excess above the
/// reserve. Store is gated off until the container's count already exceeds the reserve.</item>
/// <item>Else if the group has a whitelist anywhere -> blocked (unlisted item under an active
/// whitelist).</item>
/// <item>Else if the item has a non-numeric blacklist entry -> blocked entirely.</item>
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

    /// <summary>Items with a non-numeric blacklist entry in this group (only relevant when <see cref="GroupHasWhitelist"/> is false, since a whitelist anywhere overrides non-numeric blacklists entirely).</summary>
    private readonly HashSet<string> NonNumericBlacklistItems;

    /// <summary>Whether the group has a whitelist sign for any item at all.</summary>
    private readonly bool GroupHasWhitelist;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="conditionsByItemId">The resolved condition for each item with a whitelist and/or numeric blacklist entry in this group.</param>
    /// <param name="nonNumericBlacklistItems">Items with a non-numeric blacklist entry in this group.</param>
    /// <param name="groupHasWhitelist">Whether the group has a whitelist sign for any item at all.</param>
    public SignFilter(Dictionary<string, SignItemCondition> conditionsByItemId, HashSet<string> nonNumericBlacklistItems, bool groupHasWhitelist)
    {
        this.ConditionsByItemId = conditionsByItemId;
        this.NonNumericBlacklistItems = nonNumericBlacklistItems;
        this.GroupHasWhitelist = groupHasWhitelist;
    }

    /// <summary>Get whether an item type may move through this group's storage at all, ignoring quantity.</summary>
    /// <param name="itemId">The qualified item ID to check.</param>
    public bool IsItemTypeAllowed(string itemId)
    {
        if (this.ConditionsByItemId.ContainsKey(itemId))
            return true; // has a whitelist and/or numeric blacklist entry

        if (this.GroupHasWhitelist)
            return false; // unlisted item under an active whitelist

        return !this.NonNumericBlacklistItems.Contains(itemId);
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
    /// container that can never be taken, so it naturally depends on the source's current count.
    /// </remarks>
    public int GetMaxTakeable(string itemId, int currentCount, int requested)
    {
        if (requested <= 0)
            return 0;

        if (!this.ConditionsByItemId.TryGetValue(itemId, out SignItemCondition condition))
            return this.IsItemTypeAllowed(itemId) ? requested : 0;

        int reserve = condition.HasNumericBlacklist ? condition.BlacklistNumber ?? 0 : 0;
        int result = System.Math.Min(requested, System.Math.Max(0, currentCount - reserve));

        if (condition.HasWhitelist && condition.WhitelistNumber.HasValue)
            result = System.Math.Min(result, condition.WhitelistNumber.Value);

        return result;
    }

    /// <summary>Get the maximum amount of an item that may be stored into a container right now.</summary>
    /// <param name="itemId">The qualified item ID being stored.</param>
    /// <param name="currentCount">The container's current total count of this item.</param>
    /// <param name="requested">The amount the caller wants to store.</param>
    public int GetMaxStorable(string itemId, int currentCount, int requested)
    {
        if (requested <= 0)
            return 0;

        if (!this.ConditionsByItemId.TryGetValue(itemId, out SignItemCondition condition))
            return this.IsItemTypeAllowed(itemId) ? requested : 0;

        if (condition.HasWhitelist)
        {
            int cap = condition.WhitelistNumber ?? int.MaxValue;
            return System.Math.Min(requested, System.Math.Max(0, cap - currentCount));
        }

        // numeric blacklist only: gated until the container already holds more than the reserve
        if (currentCount <= (condition.BlacklistNumber ?? 0))
            return 0;

        return requested;
    }
}

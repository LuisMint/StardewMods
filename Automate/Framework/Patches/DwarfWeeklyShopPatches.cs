using HarmonyLib;
using Pathoschild.Stardew.Automate.Framework.Machines.Objects;
using StardewValley;
using StardewValley.Delegates;
using StardewValley.Menus;
using StardewValley.Mods;
using StardewValley.Triggers;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Per direct user request, gives the Dwarf's shop three extra, period-limited items sold
/// alongside its normal recipes: 3 Cave Carrots (50g each) that restock weekly, and 1 Power Coil
/// ($30,000) plus 1 Powered Chest ($15,000) that each restock once a season.
///
/// Vanilla's own <see cref="StardewValley.GameData.Shops.ShopItemData.AvailableStock"/> only ever
/// resets DAILY (see its own doc comment) — there's no built-in weekly/seasonal equivalent — so the
/// three new entries in <c>ShopsData.json</c> are given <c>"AvailableStock": -1</c> (unlimited,
/// entirely un-tracked by vanilla) and this class is the sole authority on their remaining stock
/// instead, via its own week/season-keyed counters in <see cref="Game1.MasterPlayer"/>'s
/// <c>modData</c> (the save's own host-farmer data, shared by every player in multiplayer — matching
/// the "Global" stock semantics the other shop entries already use).
///
/// A PREFIX on <see cref="ShopMenu.AddForSale"/> — not a postfix on the constructor mutating
/// <see cref="ShopMenu.itemPriceAndStock"/>/<see cref="ShopMenu.forSale"/> afterward — is the hook:
/// all three <see cref="ShopMenu"/> constructors populate the shop by calling <c>AddForSale</c> once
/// per entry, and by the time <see cref="ShopMenu.Initialize"/> lays out the actual UI buttons
/// afterward, <see cref="ShopMenu.forSale"/> already needs to reflect the final list — removing an
/// entry from the dictionary after that point would leave a stale, still-clickable button pointing at
/// nothing. Intercepting here means a sold-out item is simply never added at all, and a still-available
/// one gets its stock overridden to this period's actual remaining count before it's ever added.
///
/// Purchases are detected via each entry's own <c>ActionsOnPurchase</c> (see
/// <c>ShopMenu.TryToPurchaseItem</c>'s own decompiled source, which runs
/// <see cref="TriggerActionManager.TryRunAction"/> once per successful purchase) rather than a Harmony
/// patch on the purchase flow itself — vanilla already calls out to
/// <see cref="TriggerActionManager"/> for exactly this, and repeat-buying (holding the mouse down) goes
/// through that same one-purchase-at-a-time call each time, not a single bulk call — so a simple "+1
/// per firing" counter is accurate with no extra bookkeeping.
/// </summary>
internal static class DwarfWeeklyShopPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The shop ID these limits apply to.</summary>
    private const string ShopId = "Dwarf";

    /// <summary>The qualified item ID of the Cave Carrot.</summary>
    private const string CaveCarrotQualifiedItemId = "(O)78";

    /// <summary>How many Cave Carrots the Dwarf sells per week.</summary>
    private const int CaveCarrotWeeklyLimit = 3;

    /// <summary>How many Power Coils the Dwarf sells per season.</summary>
    private const int PowerCoilSeasonalLimit = 1;

    /// <summary>How many Powered Chests the Dwarf sells per season.</summary>
    private const int PoweredChestSeasonalLimit = 1;

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking which week's Cave Carrot stock was last rolled.</summary>
    private const string CaveCarrotPeriodModDataKey = "luisMint.AutomatePowerPipes/DwarfCaveCarrotWeek";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking how many Cave Carrots have been bought this week.</summary>
    private const string CaveCarrotBoughtModDataKey = "luisMint.AutomatePowerPipes/DwarfCaveCarrotBoughtThisWeek";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking which season's Power Coil stock was last rolled.</summary>
    private const string PowerCoilPeriodModDataKey = "luisMint.AutomatePowerPipes/DwarfPowerCoilSeason";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking how many Power Coils have been bought this season.</summary>
    private const string PowerCoilBoughtModDataKey = "luisMint.AutomatePowerPipes/DwarfPowerCoilBoughtThisSeason";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking which season's Powered Chest stock was last rolled.</summary>
    private const string PoweredChestPeriodModDataKey = "luisMint.AutomatePowerPipes/DwarfPoweredChestSeason";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking how many Powered Chests have been bought this season.</summary>
    private const string PoweredChestBoughtModDataKey = "luisMint.AutomatePowerPipes/DwarfPoweredChestBoughtThisSeason";

    /// <summary>The <see cref="TriggerActionManager"/> action name referenced by the Cave Carrot entry's <c>ActionsOnPurchase</c> in <c>ShopsData.json</c>.</summary>
    private const string CaveCarrotPurchasedAction = "luisMint.AutomatePowerPipes_DwarfCaveCarrotPurchased";

    /// <summary>The <see cref="TriggerActionManager"/> action name referenced by the Power Coil entry's <c>ActionsOnPurchase</c> in <c>ShopsData.json</c>.</summary>
    private const string PowerCoilPurchasedAction = "luisMint.AutomatePowerPipes_DwarfPowerCoilPurchased";

    /// <summary>The <see cref="TriggerActionManager"/> action name referenced by the Powered Chest entry's <c>ActionsOnPurchase</c> in <c>ShopsData.json</c>.</summary>
    private const string PoweredChestPurchasedAction = "luisMint.AutomatePowerPipes_DwarfPoweredChestPurchased";


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(ShopMenu), nameof(ShopMenu.AddForSale)),
            prefix: new HarmonyMethod(typeof(DwarfWeeklyShopPatches), nameof(AddForSale_Prefix))
        );

        TriggerActionManager.RegisterAction(DwarfWeeklyShopPatches.CaveCarrotPurchasedAction, (string[] _, TriggerActionContext _, out string error) =>
        {
            error = null!;
            DwarfWeeklyShopPatches.RecordPurchase(DwarfWeeklyShopPatches.CaveCarrotPeriodModDataKey, DwarfWeeklyShopPatches.CaveCarrotBoughtModDataKey, DwarfWeeklyShopPatches.GetCurrentWeekKey());
            return true;
        });

        TriggerActionManager.RegisterAction(DwarfWeeklyShopPatches.PowerCoilPurchasedAction, (string[] _, TriggerActionContext _, out string error) =>
        {
            error = null!;
            DwarfWeeklyShopPatches.RecordPurchase(DwarfWeeklyShopPatches.PowerCoilPeriodModDataKey, DwarfWeeklyShopPatches.PowerCoilBoughtModDataKey, DwarfWeeklyShopPatches.GetCurrentSeasonKey());
            return true;
        });

        TriggerActionManager.RegisterAction(DwarfWeeklyShopPatches.PoweredChestPurchasedAction, (string[] _, TriggerActionContext _, out string error) =>
        {
            error = null!;
            DwarfWeeklyShopPatches.RecordPurchase(DwarfWeeklyShopPatches.PoweredChestPeriodModDataKey, DwarfWeeklyShopPatches.PoweredChestBoughtModDataKey, DwarfWeeklyShopPatches.GetCurrentSeasonKey());
            return true;
        });
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Skip adding a period-limited Dwarf shop item once it's sold out for the current week/season, or override its stock to the actual remaining count otherwise.</summary>
    /// <param name="__instance">The shop menu being built.</param>
    /// <param name="item">The item being added for sale.</param>
    /// <param name="stock">The stock information being added — reassigning this changes what vanilla's own method actually adds, since Harmony treats a prefix parameter with the same name as the original as a by-reference override.</param>
    /// <returns><c>false</c> to skip adding this item (it's sold out for the period), or <c>true</c> to let vanilla add it normally.</returns>
    private static bool AddForSale_Prefix(ShopMenu __instance, ISalable item, ref ItemStockInformation? stock)
    {
        if (__instance.ShopId != DwarfWeeklyShopPatches.ShopId || item.IsRecipe)
            return true; // not one of our tracked entries — recipes keep their own separate (daily/unlimited) stock entirely

        int remaining;
        if (item.QualifiedItemId == DwarfWeeklyShopPatches.CaveCarrotQualifiedItemId)
            remaining = DwarfWeeklyShopPatches.GetRemaining(DwarfWeeklyShopPatches.CaveCarrotPeriodModDataKey, DwarfWeeklyShopPatches.CaveCarrotBoughtModDataKey, DwarfWeeklyShopPatches.GetCurrentWeekKey(), DwarfWeeklyShopPatches.CaveCarrotWeeklyLimit);
        else if (item.QualifiedItemId == PowerCoilPatches.TargetQualifiedItemId)
            remaining = DwarfWeeklyShopPatches.GetRemaining(DwarfWeeklyShopPatches.PowerCoilPeriodModDataKey, DwarfWeeklyShopPatches.PowerCoilBoughtModDataKey, DwarfWeeklyShopPatches.GetCurrentSeasonKey(), DwarfWeeklyShopPatches.PowerCoilSeasonalLimit);
        else if (item.QualifiedItemId == PoweredChestMachine.QualifiedItemId)
            remaining = DwarfWeeklyShopPatches.GetRemaining(DwarfWeeklyShopPatches.PoweredChestPeriodModDataKey, DwarfWeeklyShopPatches.PoweredChestBoughtModDataKey, DwarfWeeklyShopPatches.GetCurrentSeasonKey(), DwarfWeeklyShopPatches.PoweredChestSeasonalLimit);
        else
            return true; // not one of our tracked entries

        if (remaining <= 0)
            return false; // sold out for this week/season — don't add it at all

        ItemStockInformation info = stock ?? new ItemStockInformation(item.salePrice(), item.Stack);
        info.Stock = remaining;
        stock = info;
        return true;
    }

    /// <summary>Get how many of a period-limited item are still available this week/season, without recording a purchase.</summary>
    /// <param name="periodModDataKey">The <c>Game1.MasterPlayer.modData</c> key tracking which period was last rolled.</param>
    /// <param name="boughtModDataKey">The <c>Game1.MasterPlayer.modData</c> key tracking how many have been bought this period.</param>
    /// <param name="currentPeriod">This week/season's own identifier (see <see cref="GetCurrentWeekKey"/>/<see cref="GetCurrentSeasonKey"/>).</param>
    /// <param name="limit">The maximum available per period.</param>
    private static int GetRemaining(string periodModDataKey, string boughtModDataKey, string currentPeriod, int limit)
    {
        ModDataDictionary modData = Game1.MasterPlayer.modData;

        if (!modData.TryGetValue(periodModDataKey, out string? storedPeriod) || storedPeriod != currentPeriod)
            return limit; // a new week/season with nothing bought yet — don't write anything just from checking

        int bought = modData.TryGetValue(boughtModDataKey, out string? raw) && int.TryParse(raw, out int parsed) ? parsed : 0;
        return System.Math.Max(0, limit - bought);
    }

    /// <summary>Record that one unit of a period-limited item was just bought, rolling its counter over to a fresh week/season first if needed.</summary>
    /// <param name="periodModDataKey">The <c>Game1.MasterPlayer.modData</c> key tracking which period was last rolled.</param>
    /// <param name="boughtModDataKey">The <c>Game1.MasterPlayer.modData</c> key tracking how many have been bought this period.</param>
    /// <param name="currentPeriod">This week/season's own identifier (see <see cref="GetCurrentWeekKey"/>/<see cref="GetCurrentSeasonKey"/>).</param>
    private static void RecordPurchase(string periodModDataKey, string boughtModDataKey, string currentPeriod)
    {
        ModDataDictionary modData = Game1.MasterPlayer.modData;

        bool samePeriod = modData.TryGetValue(periodModDataKey, out string? storedPeriod) && storedPeriod == currentPeriod;
        int bought = samePeriod && modData.TryGetValue(boughtModDataKey, out string? raw) && int.TryParse(raw, out int parsed) ? parsed : 0;

        modData[periodModDataKey] = currentPeriod;
        modData[boughtModDataKey] = (bought + 1).ToString();
    }

    /// <summary>Get an identifier for the current in-game week, changing every 7 days.</summary>
    private static string GetCurrentWeekKey()
    {
        int weekIndex = ((int)Game1.stats.DaysPlayed - 1) / 7;
        return weekIndex.ToString();
    }

    /// <summary>Get an identifier for the current in-game season, changing every season.</summary>
    private static string GetCurrentSeasonKey()
    {
        return $"{Game1.year}_{Game1.season}";
    }
}

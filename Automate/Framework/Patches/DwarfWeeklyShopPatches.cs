using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Pathoschild.Stardew.Automate.Framework.Machines.Objects;
using Pathoschild.Stardew.Automate.Framework.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Delegates;
using StardewValley.GameData.Shops;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Mods;
using StardewValley.Triggers;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Gives the Dwarf's shop three extra, period-limited items sold
/// alongside its normal recipes: 3 Cave Carrots (50g each) that restock weekly, and 1 Power Coil
/// ($30,000) plus 1 Powered Chest ($15,000) that each restock once a season. Also enforces a genuine
/// LIFETIME cap of 7 on the Dwarf Research Note (see <see cref="DwarfResearchNoteQualifiedItemId"/>'s
/// own remarks) — the one entry here that should never restock at all, as opposed to the three above
/// which restock on a period.
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
    /// <summary>MOD: added. Encapsulates monitoring and logging — only used to warn (instead of crashing the whole mod) if <see cref="ShopMenu"/>'s <c>(string, ShopData, ShopOwnerData, ...)</c> constructor can't be resolved by reflection for the weekly mineral entry — see <see cref="Apply"/>.</summary>
    private static IMonitor? Monitor;

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

    /// <summary>
    /// MOD: added. The qualified item ID of the Dwarf Research Note (see
    /// <c>DwarfNotesData.json</c>'s <c>Data/Objects</c> entry). Originally relied on vanilla's own
    /// <c>ShopItemData.AvailableStock</c>/<c>AvailableStockLimit: "Player"</c> (7) to cap it — but per
    /// this class's own remarks, vanilla's stock tracking only ever resets DAILY, so that actually meant
    /// "7 per day, forever," not "7 total, ever," letting the shop keep restocking it day after day —
    /// it kept selling Dwarf Research Notes even after 7 had already been bought out, when it should
    /// only ever sell 7 total and then never restock. Fixed the same way as the three
    /// period-limited entries above — <c>AvailableStock: -1</c> in JSON (vanilla stops tracking it
    /// entirely) plus this class enforcing the real limit — except this one uses NO period at all: see
    /// <see cref="DwarfResearchNoteBoughtModDataKey"/>'s own remarks for why a lifetime cap needs no
    /// period/rollover logic whatsoever, unlike every other entry here.
    /// </summary>
    private const string DwarfResearchNoteQualifiedItemId = "(O)luisMint.PoweredAutomation_DwarfResearchNote";

    /// <summary>MOD: added. The lifetime (never resets) cap on the Dwarf Research Note — see <see cref="DwarfResearchNoteQualifiedItemId"/>'s own remarks.</summary>
    private const int DwarfResearchNoteLifetimeLimit = 7;

    /// <summary>MOD: added. How many geode minerals the Dwarf sells per Monday/Tuesday slot per week.</summary>
    private const int MineralWeeklyLimit = 1;

    /// <summary>MOD: changed — the mineral's price is now its own <see cref="ISalable.salePrice"/> times a randomly-rolled multiplier in this range (inclusive), rolled once alongside the mineral itself — see <see cref="GetOrRollWeeklyMineral"/>. This is the low end of that range.</summary>
    private const int MineralMinPriceMultiplier = 1;

    /// <summary>MOD: added. The high end of the price multiplier range — see <see cref="MineralMinPriceMultiplier"/>'s own remarks.</summary>
    private const int MineralMaxPriceMultiplier = 5;

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking which week's Cave Carrot stock was last rolled.</summary>
    private const string CaveCarrotPeriodModDataKey = "luisMint.PoweredAutomation/DwarfCaveCarrotWeek";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking how many Cave Carrots have been bought this week.</summary>
    private const string CaveCarrotBoughtModDataKey = "luisMint.PoweredAutomation/DwarfCaveCarrotBoughtThisWeek";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking which season's Power Coil stock was last rolled.</summary>
    private const string PowerCoilPeriodModDataKey = "luisMint.PoweredAutomation/DwarfPowerCoilSeason";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking how many Power Coils have been bought this season.</summary>
    private const string PowerCoilBoughtModDataKey = "luisMint.PoweredAutomation/DwarfPowerCoilBoughtThisSeason";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking which season's Powered Chest stock was last rolled.</summary>
    private const string PoweredChestPeriodModDataKey = "luisMint.PoweredAutomation/DwarfPoweredChestSeason";

    /// <summary>The <c>Game1.MasterPlayer.modData</c> key tracking how many Powered Chests have been bought this season.</summary>
    private const string PoweredChestBoughtModDataKey = "luisMint.PoweredAutomation/DwarfPoweredChestBoughtThisSeason";

    /// <summary>
    /// MOD: added. The <c>Game1.MasterPlayer.modData</c> key tracking how many
    /// Dwarf Research Notes have EVER been bought, with no period/rollover concept at all — unlike every
    /// other bought-counter in this class (which is paired with its own "which week/season was this last
    /// rolled for" key so the counter can be reset when a new period starts), this one is simply
    /// incremented forever and never reset, which is exactly what makes it a genuine lifetime cap instead
    /// of a recurring one. See <see cref="GetLifetimeRemaining"/>/<see cref="RecordLifetimePurchase"/>.
    /// </summary>
    private const string DwarfResearchNoteBoughtModDataKey = "luisMint.PoweredAutomation/DwarfResearchNoteBoughtTotal";

    /// <summary>MOD: added. The <c>Game1.MasterPlayer.modData</c> key tracking which week's Monday geode mineral was last rolled — see <see cref="GetOrRollWeeklyMineral"/>.</summary>
    private const string MineralMondayPeriodModDataKey = "luisMint.PoweredAutomation/DwarfMineralWeek";

    /// <summary>MOD: added. The <c>Game1.MasterPlayer.modData</c> key storing which specific mineral (unqualified item ID, or empty if none were eligible) was rolled for Monday this week — see <see cref="GetOrRollWeeklyMineral"/>.</summary>
    private const string MineralMondayRolledItemModDataKey = "luisMint.PoweredAutomation/DwarfMineralRolledItem";

    /// <summary>MOD: added. The <c>Game1.MasterPlayer.modData</c> key tracking how many Monday geode minerals have been bought this week.</summary>
    private const string MineralMondayBoughtModDataKey = "luisMint.PoweredAutomation/DwarfMineralBoughtThisWeek";

    /// <summary>MOD: added. The <c>Game1.MasterPlayer.modData</c> key storing Monday's rolled price multiplier (see <see cref="MineralMinPriceMultiplier"/>) for this week.</summary>
    private const string MineralMondayPriceMultiplierModDataKey = "luisMint.PoweredAutomation/DwarfMineralPriceMultiplier";

    /// <summary>MOD: added. The <c>Game1.MasterPlayer.modData</c> key tracking which week's Tuesday geode mineral was last rolled — an entirely separate roll from Monday's, so the two days can (and usually will, but aren't guaranteed to) offer different minerals.</summary>
    private const string MineralTuesdayPeriodModDataKey = "luisMint.PoweredAutomation/DwarfMineralTuesdayWeek";

    /// <summary>MOD: added. The <c>Game1.MasterPlayer.modData</c> key storing which specific mineral (unqualified item ID, or empty if none were eligible) was rolled for Tuesday this week — see <see cref="GetOrRollWeeklyMineral"/>.</summary>
    private const string MineralTuesdayRolledItemModDataKey = "luisMint.PoweredAutomation/DwarfMineralTuesdayRolledItem";

    /// <summary>MOD: added. The <c>Game1.MasterPlayer.modData</c> key tracking how many Tuesday geode minerals have been bought this week.</summary>
    private const string MineralTuesdayBoughtModDataKey = "luisMint.PoweredAutomation/DwarfMineralTuesdayBoughtThisWeek";

    /// <summary>MOD: added. The <c>Game1.MasterPlayer.modData</c> key storing Tuesday's rolled price multiplier (see <see cref="MineralMinPriceMultiplier"/>) for this week — rolled entirely independently of Monday's.</summary>
    private const string MineralTuesdayPriceMultiplierModDataKey = "luisMint.PoweredAutomation/DwarfMineralTuesdayPriceMultiplier";

    /// <summary>The <see cref="TriggerActionManager"/> action name referenced by the Cave Carrot entry's <c>ActionsOnPurchase</c> in <c>ShopsData.json</c>.</summary>
    private const string CaveCarrotPurchasedAction = "luisMint.PoweredAutomation_DwarfCaveCarrotPurchased";

    /// <summary>The <see cref="TriggerActionManager"/> action name referenced by the Power Coil entry's <c>ActionsOnPurchase</c> in <c>ShopsData.json</c>.</summary>
    private const string PowerCoilPurchasedAction = "luisMint.PoweredAutomation_DwarfPowerCoilPurchased";

    /// <summary>The <see cref="TriggerActionManager"/> action name referenced by the Powered Chest entry's <c>ActionsOnPurchase</c> in <c>ShopsData.json</c>.</summary>
    private const string PoweredChestPurchasedAction = "luisMint.PoweredAutomation_DwarfPoweredChestPurchased";

    /// <summary>MOD: added. The <see cref="TriggerActionManager"/> action name referenced by the Dwarf Research Note entry's <c>ActionsOnPurchase</c> in <c>DwarfNotesData.json</c>.</summary>
    private const string DwarfResearchNotePurchasedAction = "luisMint.PoweredAutomation_DwarfResearchNotePurchased";

    /// <summary>MOD: added. The <see cref="TriggerActionManager"/> action name for the weekly geode mineral entry's purchase — set directly on the <see cref="ItemStockInformation"/> we construct ourselves (see <see cref="ShopMenuCtor_Postfix"/>), since this entry has no <c>ShopsData.json</c> counterpart to declare it on.</summary>
    private const string MineralPurchasedAction = "luisMint.PoweredAutomation_DwarfMineralPurchased";


    /*********
    ** Public methods
    *********/
    /// <summary>MOD: added. Provide the monitor used to warn (instead of crashing the whole mod) if the weekly mineral entry's constructor patch can't be resolved. Must be called before <see cref="Apply"/>.</summary>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    public static void Initialize(IMonitor monitor)
    {
        DwarfWeeklyShopPatches.Monitor = monitor;
    }

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

        // MOD: added — no period key involved at all, since this is a genuine
        // lifetime cap; see DwarfResearchNoteBoughtModDataKey's own remarks.
        TriggerActionManager.RegisterAction(DwarfWeeklyShopPatches.DwarfResearchNotePurchasedAction, (string[] _, TriggerActionContext _, out string error) =>
        {
            error = null!;
            DwarfWeeklyShopPatches.RecordLifetimePurchase(DwarfWeeklyShopPatches.DwarfResearchNoteBoughtModDataKey);
            return true;
        });

        // MOD: added — see ShopMenuCtor_Postfix's own remarks for why this needs a
        // constructor postfix instead of just extending AddForSale_Prefix like the three entries above:
        // this entry has no fixed item identity to react to in ShopsData.json, since WHICH mineral is
        // for sale changes week to week. MOD: changed — the mineral entry is now
        // only ever listed on its own specific day (Monday or Tuesday — see ShopMenuCtor_Postfix), so a
        // purchase can only ever happen on one of those two days; checking the day again here (rather
        // than baking "which day" into the action name itself) is enough to route the purchase to the
        // right day's own bought-counter, since the shop menu can't stay open across a day change.
        TriggerActionManager.RegisterAction(DwarfWeeklyShopPatches.MineralPurchasedAction, (string[] _, TriggerActionContext _, out string error) =>
        {
            error = null!;

            if (DwarfWeeklyShopPatches.IsMonday())
                DwarfWeeklyShopPatches.RecordPurchase(DwarfWeeklyShopPatches.MineralMondayPeriodModDataKey, DwarfWeeklyShopPatches.MineralMondayBoughtModDataKey, DwarfWeeklyShopPatches.GetCurrentWeekKey());
            else if (DwarfWeeklyShopPatches.IsTuesday())
                DwarfWeeklyShopPatches.RecordPurchase(DwarfWeeklyShopPatches.MineralTuesdayPeriodModDataKey, DwarfWeeklyShopPatches.MineralTuesdayBoughtModDataKey, DwarfWeeklyShopPatches.GetCurrentWeekKey());

            return true;
        });

        // MOD: fixed — the exact full parameter list (including trailing optional params) didn't match
        // this game build's actual ShopMenu constructor, so AccessTools.Constructor returned null and
        // Harmony's own Patch() call threw trying to patch a null MethodBase, crashing the ENTIRE
        // Automate mod on startup (not just this one feature) since nothing after this Apply() call in
        // ModEntry.Entry() ever got to run either. Resolved by matching only the first 3 parameters
        // (string, ShopData, ShopOwnerData) by type instead of the full signature — the actual identity
        // of an NPC shop menu, which is all this postfix needs — so a harmless difference in a LATER
        // optional parameter's exact type can't break this again. If the shape changes enough that even
        // this can't find a match, this now logs a warning and skips just this one feature instead of
        // taking the whole mod down with it.
        System.Reflection.ConstructorInfo? shopMenuCtor = AccessTools.GetDeclaredConstructors(typeof(ShopMenu))
            .FirstOrDefault(ctor =>
            {
                System.Reflection.ParameterInfo[] parameters = ctor.GetParameters();
                return parameters.Length >= 3
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(ShopData)
                    && parameters[2].ParameterType == typeof(ShopOwnerData);
            });

        if (shopMenuCtor != null)
        {
            harmony.Patch(
                original: shopMenuCtor,
                postfix: new HarmonyMethod(typeof(DwarfWeeklyShopPatches), nameof(ShopMenuCtor_Postfix))
            );
        }
        else
        {
            DwarfWeeklyShopPatches.Monitor?.Log("Couldn't find ShopMenu's (string, ShopData, ShopOwnerData, ...) constructor to patch — the Dwarf's weekly geode mineral entry won't be added, but the rest of the mod is unaffected.", LogLevel.Warn);
        }
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
        else if (item.QualifiedItemId == DwarfWeeklyShopPatches.DwarfResearchNoteQualifiedItemId)
            remaining = DwarfWeeklyShopPatches.GetLifetimeRemaining(DwarfWeeklyShopPatches.DwarfResearchNoteBoughtModDataKey, DwarfWeeklyShopPatches.DwarfResearchNoteLifetimeLimit);
        else
            return true; // not one of our tracked entries

        if (remaining <= 0)
            return false; // sold out for this week/season — don't add it at all

        ItemStockInformation info = stock ?? new ItemStockInformation(item.salePrice(), item.Stack);
        info.Stock = remaining;
        stock = info;
        return true;
    }

    /// <summary>
    /// MOD: added. Add this week's Monday (or Tuesday) geode mineral entry — 1 of
    /// ONE random mineral the player has already donated to the museum, each day rolled entirely
    /// independently of the other — to the Dwarf's shop, but ONLY on that specific day of the week; every
    /// other day (including the rest of the week the mineral was rolled for), neither entry is added at
    /// all. A POSTFIX on the constructor — not an extra case in <see cref="AddForSale_Prefix"/> like the
    /// other three period-limited entries — because those three all have a FIXED, known item identity
    /// declared in <c>ShopsData.json</c> for that prefix to react to when vanilla's own shop-building loop
    /// adds them; this entry's item identity itself changes week to week (and some weeks may have none at
    /// all, if nothing's been donated yet), so there's nothing fixed to declare there — this postfix adds
    /// it directly instead, using the same <see cref="GetRemaining"/>/<see cref="RecordPurchase"/>
    /// period-tracking helpers the other three already share.
    /// </summary>
    /// <param name="__instance">The shop menu that was just built.</param>
    private static void ShopMenuCtor_Postfix(ShopMenu __instance)
    {
        if (__instance.ShopId != DwarfWeeklyShopPatches.ShopId)
            return;

        if (DwarfWeeklyShopPatches.IsMonday())
            DwarfWeeklyShopPatches.TryAddWeeklyMineral(__instance, DwarfWeeklyShopPatches.MineralMondayPeriodModDataKey, DwarfWeeklyShopPatches.MineralMondayRolledItemModDataKey, DwarfWeeklyShopPatches.MineralMondayPriceMultiplierModDataKey, DwarfWeeklyShopPatches.MineralMondayBoughtModDataKey);
        else if (DwarfWeeklyShopPatches.IsTuesday())
            DwarfWeeklyShopPatches.TryAddWeeklyMineral(__instance, DwarfWeeklyShopPatches.MineralTuesdayPeriodModDataKey, DwarfWeeklyShopPatches.MineralTuesdayRolledItemModDataKey, DwarfWeeklyShopPatches.MineralTuesdayPriceMultiplierModDataKey, DwarfWeeklyShopPatches.MineralTuesdayBoughtModDataKey);
        // any other day of the week — no mineral entry at all, and nothing rolled/touched
    }

    /// <summary>MOD: added. Add one day-slot's geode mineral entry to the shop — priced at the mineral's own <see cref="ISalable.salePrice"/> times this week's rolled multiplier (see <see cref="GetOrRollWeeklyMineral"/>) — if one is currently available for that slot.</summary>
    /// <param name="shopMenu">The shop menu to add the entry to.</param>
    /// <param name="periodModDataKey">This slot's period tracking key — see <see cref="GetOrRollWeeklyMineral"/>.</param>
    /// <param name="rolledModDataKey">This slot's rolled-item tracking key — see <see cref="GetOrRollWeeklyMineral"/>.</param>
    /// <param name="priceMultiplierModDataKey">This slot's rolled price multiplier tracking key — see <see cref="GetOrRollWeeklyMineral"/>.</param>
    /// <param name="boughtModDataKey">This slot's purchased-count tracking key — see <see cref="GetOrRollWeeklyMineral"/>.</param>
    private static void TryAddWeeklyMineral(ShopMenu shopMenu, string periodModDataKey, string rolledModDataKey, string priceMultiplierModDataKey, string boughtModDataKey)
    {
        (string? mineralId, int priceMultiplier) = DwarfWeeklyShopPatches.GetOrRollWeeklyMineral(periodModDataKey, rolledModDataKey, priceMultiplierModDataKey, boughtModDataKey);
        if (mineralId is null)
            return; // nothing donated to the museum yet — no mineral to sell this week

        int remaining = DwarfWeeklyShopPatches.GetRemaining(periodModDataKey, boughtModDataKey, DwarfWeeklyShopPatches.GetCurrentWeekKey(), DwarfWeeklyShopPatches.MineralWeeklyLimit);
        if (remaining <= 0)
            return; // already bought this week's mineral for this slot

        ISalable mineral = ItemRegistry.Create("(O)" + mineralId);
        shopMenu.AddForSale(mineral, new ItemStockInformation(mineral.salePrice() * priceMultiplier, remaining, actionsOnPurchase: [DwarfWeeklyShopPatches.MineralPurchasedAction]));
    }

    /// <summary>
    /// MOD: added. Get this week's already-rolled geode mineral (unqualified item
    /// ID) and price multiplier for one day-slot (Monday or Tuesday — distinguished entirely by which
    /// <c>modData</c> keys are passed in), rolling a fresh pair — restricted to whichever of
    /// <see cref="ModConfig.MineralItemIds"/> the player has already donated to the museum (see
    /// <see cref="LibraryMuseum.HasDonatedArtifact"/>), and a multiplier randomly chosen from
    /// <see cref="MineralMinPriceMultiplier"/> to <see cref="MineralMaxPriceMultiplier"/> inclusive — the
    /// first time this slot is checked in a new week. Returns a <c>null</c> mineral ID (multiplier is
    /// meaningless in that case) if nothing's eligible for this week's roll (nothing donated to the
    /// museum yet), including a week that was already rolled with nothing eligible at the time — it does
    /// NOT retroactively re-roll mid-week just because the player donates something new; the next roll
    /// happens at the next weekly boundary, same as every other period-limited entry in this class.
    /// </summary>
    /// <param name="periodModDataKey">This slot's period tracking key.</param>
    /// <param name="rolledModDataKey">This slot's rolled-item tracking key.</param>
    /// <param name="priceMultiplierModDataKey">This slot's rolled price multiplier tracking key.</param>
    /// <param name="boughtModDataKey">This slot's purchased-count tracking key, reset to 0 whenever a fresh roll happens.</param>
    private static (string? mineralId, int priceMultiplier) GetOrRollWeeklyMineral(string periodModDataKey, string rolledModDataKey, string priceMultiplierModDataKey, string boughtModDataKey)
    {
        ModDataDictionary modData = Game1.MasterPlayer.modData;
        string currentWeek = DwarfWeeklyShopPatches.GetCurrentWeekKey();

        if (modData.TryGetValue(periodModDataKey, out string? storedWeek) && storedWeek == currentWeek)
        {
            string? rolledMineral = modData.TryGetValue(rolledModDataKey, out string? rolled) && !string.IsNullOrEmpty(rolled)
                ? rolled
                : null;
            int storedMultiplier = modData.TryGetValue(priceMultiplierModDataKey, out string? rawMultiplier) && int.TryParse(rawMultiplier, out int parsedMultiplier)
                ? parsedMultiplier
                : DwarfWeeklyShopPatches.MineralMinPriceMultiplier;

            return (rolledMineral, storedMultiplier);
        }

        // a new week — roll a fresh pick among currently-donated minerals plus its own price multiplier,
        // and reset the purchase counter for the new week right alongside them (GetRemaining/RecordPurchase
        // would otherwise treat a stale bought-count as still applying, since by the time either is
        // called the period key written just below already matches the current week)
        List<string> eligible = ModConfig.MineralItemIds.Where(LibraryMuseum.HasDonatedArtifact).ToList();
        string? picked = eligible.Count > 0 ? eligible[Game1.random.Next(eligible.Count)] : null;
        int multiplier = Game1.random.Next(DwarfWeeklyShopPatches.MineralMinPriceMultiplier, DwarfWeeklyShopPatches.MineralMaxPriceMultiplier + 1);

        modData[periodModDataKey] = currentWeek;
        modData[rolledModDataKey] = picked ?? "";
        modData[priceMultiplierModDataKey] = multiplier.ToString();
        modData[boughtModDataKey] = "0";

        return (picked, multiplier);
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

    /// <summary>MOD: added. Get how many of a lifetime-capped item are still available EVER, without recording a purchase — no period/rollover concept at all, unlike <see cref="GetRemaining"/>.</summary>
    /// <param name="boughtModDataKey">The <c>Game1.MasterPlayer.modData</c> key tracking how many have ever been bought.</param>
    /// <param name="limit">The maximum available, ever.</param>
    private static int GetLifetimeRemaining(string boughtModDataKey, int limit)
    {
        ModDataDictionary modData = Game1.MasterPlayer.modData;
        int bought = modData.TryGetValue(boughtModDataKey, out string? raw) && int.TryParse(raw, out int parsed) ? parsed : 0;
        return System.Math.Max(0, limit - bought);
    }

    /// <summary>MOD: added. Record that one unit of a lifetime-capped item was just bought — never rolls over, unlike <see cref="RecordPurchase"/>.</summary>
    /// <param name="boughtModDataKey">The <c>Game1.MasterPlayer.modData</c> key tracking how many have ever been bought.</param>
    private static void RecordLifetimePurchase(string boughtModDataKey)
    {
        ModDataDictionary modData = Game1.MasterPlayer.modData;
        int bought = modData.TryGetValue(boughtModDataKey, out string? raw) && int.TryParse(raw, out int parsed) ? parsed : 0;
        modData[boughtModDataKey] = (bought + 1).ToString();
    }

    /// <summary>Get an identifier for the current in-game week, changing every 7 days.</summary>
    private static string GetCurrentWeekKey()
    {
        int weekIndex = ((int)Game1.stats.DaysPlayed - 1) / 7;
        return weekIndex.ToString();
    }

    /// <summary>MOD: added. Get whether today is in-game Monday — <see cref="Game1.dayOfMonth"/> 1 is always a Monday, and every 7 days after that repeats the cycle.</summary>
    private static bool IsMonday()
    {
        return Game1.dayOfMonth % 7 == 1;
    }

    /// <summary>MOD: added. Get whether today is in-game Tuesday — see <see cref="IsMonday"/>'s own remarks for the day-of-week math.</summary>
    private static bool IsTuesday()
    {
        return Game1.dayOfMonth % 7 == 2;
    }

    /// <summary>Get an identifier for the current in-game season, changing every season.</summary>
    private static string GetCurrentSeasonKey()
    {
        return $"{Game1.year}_{Game1.season}";
    }
}

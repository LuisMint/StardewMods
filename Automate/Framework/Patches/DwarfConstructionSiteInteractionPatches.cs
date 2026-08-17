using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Clicking a Dwarf construction site while its ladder tile is showing (see
/// <see cref="DwarfConstructionSpritePatches.HasLadderAppeared"/>) spits out a random item from the
/// ladder hole, once per building per day — the same cadence as the vanilla trash cans.
///
/// Deliberately scoped to <see cref="Building.daysOfConstructionLeft"/> only, not
/// <see cref="Building.daysUntilUpgrade"/> — vanilla's own <see cref="Building.doAction(Vector2, Farmer)"/>
/// only intercepts the click during real construction (an upgrading building is otherwise fully
/// enterable/interactive, e.g. walking in the door); reusing that same distinction here means this
/// patch never blocks normal interaction with a Dwarf building that's mid-upgrade, only a genuinely
/// new one still being built.
///
/// A PREFIX (not postfix) on <see cref="Building.doAction(Vector2, Farmer)"/>: when the loot check
/// fires, it returns <c>false</c> to skip vanilla's own handling entirely (which would otherwise just
/// show the generic "this building is under construction" message) rather than running both.
///
/// MOD: added. Once a player has read Dwarf Research Note #7, their very
/// next construction-site loot pull (whichever building they hit next) is guaranteed to be a Diamond
/// instead of a normal <see cref="LootTable"/> roll — a one-time, per-player nudge toward having a
/// Diamond in hand for the Note #7-gated Statue Of The Dwarf King trade (see
/// <see cref="DwarfKingStatueTradePatches"/>). Tracked on the player's own <see cref="Farmer.modData"/>,
/// not the building's, since it's a once-per-player bonus rather than a once-per-building one.
/// </summary>
internal static class DwarfConstructionSiteInteractionPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The <see cref="Building.modData"/> key storing the <see cref="StardewValley.Stats.DaysPlayed"/> value the loot was last given on, so it only triggers once per in-game day per building.</summary>
    private const string LastLootDayModDataKey = "luisMint.PoweredAutomation/DwarfConstructionLootDay";

    /// <summary>The <see cref="Farmer.modData"/> key tracking whether a player has already used their one-time Dwarf Research Note #7 guaranteed-Diamond loot pull.</summary>
    private const string GuaranteedDiamondUsedModDataKey = "luisMint.PoweredAutomation/DwarfNote7DiamondGuaranteeUsed";

    /// <summary>The qualified item ID a Dwarf Research Note #7 guaranteed loot pull gives.</summary>
    private const string GuaranteedDiamondItemId = "(O)72"; // Diamond

    /// <summary>The vanilla gem item IDs an "any gem" loot roll picks randomly from (Emerald, Aquamarine, Ruby, Amethyst, Topaz).</summary>
    private static readonly string[] GemItemIds = ["(O)60", "(O)62", "(O)64", "(O)66", "(O)68"];

    /// <summary>
    /// The loot table: each entry's chosen item ID (or <c>null</c> to roll a random <see cref="GemItemIds"/> entry instead), its relative weight out of the table's total, and the inclusive quantity range to spawn. Cave carrot is most common, Dwarvish Helm rarest, and coal/copper ore/iron ore spawn 1-5 at a time rather than a flat 1.
    /// </summary>
    private static readonly (string? ItemId, int Weight, int MinQuantity, int MaxQuantity)[] LootTable =
    [
        ("(O)78", 30, 1, 1),  // Cave Carrot
        ("(O)378", 17, 1, 5), // Copper Ore
        ("(O)380", 14, 1, 5), // Iron Ore
        ("(O)382", 14, 1, 5), // Coal
        ("(O)535", 10, 1, 1), // Geode
        (null, 7, 1, 1),      // any gem
        ("(O)749", 6, 1, 1),  // Omni Geode
        ("(O)121", 2, 1, 1),  // Dwarvish Helm
    ];


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Building), nameof(Building.doAction)),
            prefix: new HarmonyMethod(typeof(DwarfConstructionSiteInteractionPatches), nameof(DoAction_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Spit a random item out of the ladder hole when the player clicks an active Dwarf construction site, once per building per day.</summary>
    /// <param name="__instance">The building being interacted with.</param>
    /// <param name="tileLocation">The tile the player clicked.</param>
    /// <param name="who">The player interacting.</param>
    /// <param name="__result">The result to return if this handles the interaction.</param>
    /// <returns><c>false</c> (skip the original/vanilla handling) if this handled the interaction; <c>true</c> to let vanilla run as normal otherwise.</returns>
    private static bool DoAction_Prefix(Building __instance, Vector2 tileLocation, Farmer who, ref bool __result)
    {
        if (!who.IsLocalPlayer || who.isRidingHorse())
            return true;

        // MOD: added — only real construction, not upgrading; see this class's own remarks for why.
        if (__instance.daysOfConstructionLeft.Value <= 0)
            return true;

        if (!__instance.occupiesTile(tileLocation))
            return true;

        if (!DwarfConstructionSpritePatches.HasLadderAppeared(__instance))
            return true; // ladder isn't showing yet — let vanilla's own "under construction" dialogue show instead

        int today = (int)Game1.stats.DaysPlayed;
        if (__instance.modData.TryGetValue(DwarfConstructionSiteInteractionPatches.LastLootDayModDataKey, out string? raw) && int.TryParse(raw, out int lastDay) && lastDay == today)
        {
            Game1.drawObjectDialogue(Game1.content.LoadString("Strings\\Buildings:UnderConstruction"));
            __result = true;
            return false;
        }

        __instance.modData[DwarfConstructionSiteInteractionPatches.LastLootDayModDataKey] = today.ToString();

        GameLocation? location = __instance.GetParentLocation();
        if (location != null)
        {
            (string itemId, int quantity) = DwarfConstructionSiteInteractionPatches.RollLootOrGuaranteedDiamond(who);
            Vector2 ladderTile = DwarfConstructionSpritePatches.GetLadderTile(__instance);
            Game1.createMultipleObjectDebris(itemId, (int)ladderTile.X, (int)ladderTile.Y, quantity, who.UniqueMultiplayerID, location);
            location.playSound("give_gift");
        }

        __result = true;
        return false;
    }

    /// <summary>Get this loot pull's item ID and quantity: a guaranteed Diamond if <paramref name="who"/> has read Dwarf Research Note #7 and hasn't already used that one-time bonus, otherwise a normal <see cref="RollLoot"/> roll.</summary>
    /// <param name="who">The player receiving the loot.</param>
    private static (string ItemId, int Quantity) RollLootOrGuaranteedDiamond(Farmer who)
    {
        if (!who.modData.ContainsKey(DwarfConstructionSiteInteractionPatches.GuaranteedDiamondUsedModDataKey)
            && DwarfResearchNotes.HasRead(who, DwarfResearchNotes.CategorySignNoteId))
        {
            who.modData[DwarfConstructionSiteInteractionPatches.GuaranteedDiamondUsedModDataKey] = "true";
            return (DwarfConstructionSiteInteractionPatches.GuaranteedDiamondItemId, 1);
        }

        return DwarfConstructionSiteInteractionPatches.RollLoot();
    }

    /// <summary>Roll a random item ID and quantity from <see cref="LootTable"/>, weighted per entry.</summary>
    private static (string ItemId, int Quantity) RollLoot()
    {
        int totalWeight = DwarfConstructionSiteInteractionPatches.LootTable.Sum(entry => entry.Weight);
        int roll = Game1.random.Next(totalWeight);

        int cumulative = 0;
        foreach ((string? itemId, int weight, int minQuantity, int maxQuantity) in DwarfConstructionSiteInteractionPatches.LootTable)
        {
            cumulative += weight;
            if (roll < cumulative)
            {
                string resolvedItemId = itemId ?? DwarfConstructionSiteInteractionPatches.GemItemIds[Game1.random.Next(DwarfConstructionSiteInteractionPatches.GemItemIds.Length)];
                int quantity = Game1.random.Next(minQuantity, maxQuantity + 1);
                return (resolvedItemId, quantity);
            }
        }

        return ("(O)78", 1); // unreachable in practice — roll is always < totalWeight
    }
}

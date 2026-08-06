using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Pathoschild.Stardew.Automate.Framework.Models;
using StardewModdingAPI;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Per direct user request, resolves <see cref="ModConfig.PowerSiloTierPools"/> into a
/// concrete effective tier list — every tier with a pool gets its <see cref="PowerSiloTierConfig.RequiredItems"/>
/// replaced by a ONE-TIME roll (one random option per <see cref="PowerSiloSlotPool"/>, with a random
/// count within that option's own range), so requirements feel varied between saves without changing
/// every time the config is read. The roll happens once per save and is persisted (see
/// <see cref="RolledRequirementsModDataKey"/>), so reloading the same save always sees the same
/// requirements it already showed the player, rather than re-rolling out from under them.
///
/// Deliberately global (stored on <see cref="Game1.MasterPlayer"/>, not per-player) since every Power
/// Silo in the save shares the exact same tier progression regardless of which player built or is
/// feeding it — a per-player roll would mean two players' Silos disagreeing about what tier 2 costs.
///
/// Everything downstream (<see cref="PowerSiloSystem"/>, <see cref="PowerSiloInteraction"/>,
/// <see cref="PowerSiloMenu"/>, <see cref="Patches.PowerSiloCapPatches"/>) reads the result of
/// <see cref="GetEffectiveTiers"/> exactly like it always read <c>ModConfig.PowerSiloTiers</c> directly
/// — none of them know or care whether a given tier's requirements came from the fixed config or a roll.
/// </summary>
internal class PowerSiloTierRoller
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the base capacity tiers (for <see cref="PowerSiloTierConfig.CapacityGranted"/>/<see cref="PowerSiloTierConfig.GrantsSolarBonus"/>, and as the fallback <see cref="PowerSiloTierConfig.RequiredItems"/> for any tier with no pool).</summary>
    private readonly Func<List<PowerSiloTierConfig>> GetBaseTiers;

    /// <summary>Get the randomized pools, index-aligned with <see cref="GetBaseTiers"/> — a <c>null</c> entry (or a list shorter than the base tiers) leaves the corresponding tier(s) using their fixed <see cref="PowerSiloTierConfig.RequiredItems"/> unchanged.</summary>
    private readonly Func<List<PowerSiloTierPool>?> GetTierPools;

    /// <summary>The <see cref="Farmer.modData"/> key on <see cref="Game1.MasterPlayer"/> storing this save's already-rolled requirements, keyed by tier index — see this class's own remarks for why it's global rather than per-player.</summary>
    private const string RolledRequirementsModDataKey = "luisMint.AutomatePowerPipes/PowerSiloRolledRequirements";

    /// <summary>The effective tier list computed for the current save, cached until <see cref="Reset"/> is called.</summary>
    private List<PowerSiloTierConfig>? CachedEffectiveTiers;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getBaseTiers">Get the base capacity tiers.</param>
    /// <param name="getTierPools">Get the randomized pools, index-aligned with <paramref name="getBaseTiers"/>.</param>
    public PowerSiloTierRoller(Func<List<PowerSiloTierConfig>> getBaseTiers, Func<List<PowerSiloTierPool>?> getTierPools)
    {
        this.GetBaseTiers = getBaseTiers;
        this.GetTierPools = getTierPools;
    }

    /// <summary>Get the effective tier list — the base tiers with any pooled tier's <see cref="PowerSiloTierConfig.RequiredItems"/> swapped out for this save's already-rolled (or freshly rolled, if this is the first call this save) result. Cheap after the first call per save.</summary>
    public List<PowerSiloTierConfig> GetEffectiveTiers()
    {
        if (this.CachedEffectiveTiers is not null)
            return this.CachedEffectiveTiers;

        List<PowerSiloTierConfig> baseTiers = this.GetBaseTiers();
        List<PowerSiloTierPool>? pools = this.GetTierPools();
        if (pools is not { Count: > 0 } || !Context.IsWorldReady)
        {
            // MOD: added — no pools configured, or called before a save is loaded (e.g. during early mod
            // init): fall back to the base tiers unmodified, and don't cache — a later call once the
            // world IS ready should still roll normally instead of being stuck on this fallback forever.
            return baseTiers;
        }

        Dictionary<int, List<PowerSiloRequiredItem>> rolled = this.LoadOrRoll(baseTiers, pools);

        List<PowerSiloTierConfig> effective = new(baseTiers.Count);
        for (int i = 0; i < baseTiers.Count; i++)
        {
            if (rolled.TryGetValue(i, out List<PowerSiloRequiredItem>? rolledItems))
            {
                effective.Add(new PowerSiloTierConfig
                {
                    RequiredItems = rolledItems,
                    CapacityGranted = baseTiers[i].CapacityGranted,
                    GrantsSolarBonus = baseTiers[i].GrantsSolarBonus
                });
            }
            else
                effective.Add(baseTiers[i]);
        }

        this.CachedEffectiveTiers = effective;
        return effective;
    }

    /// <summary>Clear the cached effective tier list — call this on save load, so a different save (or the same save reloaded) re-reads its own persisted roll instead of reusing whatever was cached in memory from before.</summary>
    public void Reset()
    {
        this.CachedEffectiveTiers = null;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get this save's already-rolled requirements if present, otherwise roll fresh ones and persist them immediately.</summary>
    /// <param name="baseTiers">The base capacity tiers, for bounds-checking the pools against.</param>
    /// <param name="pools">The randomized pools to roll from.</param>
    private Dictionary<int, List<PowerSiloRequiredItem>> LoadOrRoll(List<PowerSiloTierConfig> baseTiers, List<PowerSiloTierPool> pools)
    {
        if (Game1.MasterPlayer.modData.TryGetValue(PowerSiloTierRoller.RolledRequirementsModDataKey, out string? raw))
        {
            try
            {
                Dictionary<int, List<PowerSiloRequiredItem>>? saved = JsonConvert.DeserializeObject<Dictionary<int, List<PowerSiloRequiredItem>>>(raw);
                if (saved is not null)
                    return saved;
            }
            catch (JsonException)
            {
                // MOD: added — a corrupted/hand-edited value falls through to rolling fresh below, rather than crashing every time this Silo is touched.
            }
        }

        Dictionary<int, List<PowerSiloRequiredItem>> rolled = new();
        for (int i = 0; i < baseTiers.Count && i < pools.Count; i++)
        {
            PowerSiloTierPool? pool = pools[i];
            if (pool is not { Slots.Count: > 0 })
                continue;

            rolled[i] = pool.Slots.Select(PowerSiloTierRoller.RollSlot).ToList();
        }

        Game1.MasterPlayer.modData[PowerSiloTierRoller.RolledRequirementsModDataKey] = JsonConvert.SerializeObject(rolled);
        return rolled;
    }

    /// <summary>Roll one slot: pick a random option, then (if that option is itself a sub-pool via <see cref="PowerSiloItemOption.ItemIds"/>) a random concrete item within it, then a random count within the chosen option's range.</summary>
    /// <param name="slot">The slot to roll.</param>
    private static PowerSiloRequiredItem RollSlot(PowerSiloSlotPool slot)
    {
        PowerSiloItemOption option = slot.Options[Game1.random.Next(slot.Options.Count)];

        string itemId = option.ItemIds is { Count: > 0 } itemIds
            ? itemIds[Game1.random.Next(itemIds.Count)]
            : option.ItemId ?? "";

        int count = option.MinCount >= option.MaxCount
            ? option.MinCount
            : Game1.random.Next(option.MinCount, option.MaxCount + 1);

        return new PowerSiloRequiredItem
        {
            ItemId = itemId,
            Count = count,
            RequiredPreservedFlavorItemId = option.RequiredPreservedFlavorItemId
        };
    }
}

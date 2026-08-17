using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Common;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Resolves <see cref="ModConfig.PowerSiloTierPools"/> into a
/// concrete effective tier list — every tier with a pool gets its <see cref="PowerSiloTierConfig.RequiredItems"/>
/// replaced by a ONE-TIME roll (one random option per <see cref="PowerSiloSlotPool"/>, with a random
/// count within that option's own range), so requirements feel varied between saves without changing
/// every time the config is read. The roll happens once per Power Silo BUILDING and is persisted on
/// that specific building (see <see cref="RolledRequirementsModDataKey"/>), so reloading the same save
/// always sees the same requirements it already showed the player for each Silo, rather than
/// re-rolling out from under them.
///
/// MOD: changed — rolled per Power Silo BUILDING now, not once globally for the
/// whole save. The previous design stored a single save-wide roll on <see cref="Game1.MasterPlayer"/>
/// so every Silo agreed on tier costs regardless of which player fed it — but that also meant every
/// Silo in the save was IDENTICAL to every other one, which defeats the point of rolling requirements
/// at all if a player builds more than one. Storing the roll on the Silo <see cref="Building"/>'s own
/// <see cref="Building.modData"/> instead (the same place <see cref="PowerSiloSystem"/> already stores
/// that Silo's own tier/delivery progress) keeps each Silo's requirements independent of every OTHER
/// Silo, while still being the same regardless of which player is looking at or feeding that ONE Silo —
/// multiplayer players still can't disagree about what a given Silo wants, they just no longer see the
/// same list on a DIFFERENT Silo.
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

    /// <summary>MOD: changed — now a <see cref="Building.modData"/> key on the specific Power Silo, not <see cref="Farmer.modData"/> on <see cref="Game1.MasterPlayer"/> — storing this Silo's already-rolled requirements, keyed by tier index. See this class's own remarks for why it moved from a save-wide key to a per-building one.</summary>
    private const string RolledRequirementsModDataKey = "luisMint.PoweredAutomation/PowerSiloRolledRequirements";

    /// <summary>MOD: changed — now keyed per Silo <see cref="Building"/> instead of a single save-wide value, so each Silo's own effective tier list is cached independently. Cleared entirely by <see cref="Reset"/>.</summary>
    private readonly Dictionary<Building, List<PowerSiloTierConfig>> CachedEffectiveTiersBySilo = new();


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

    /// <summary>Get a Power Silo's effective tier list — the base tiers with any pooled tier's <see cref="PowerSiloTierConfig.RequiredItems"/> swapped out for THIS Silo's already-rolled (or freshly rolled, if this is the first call for it) result. Cheap after the first call per Silo.</summary>
    /// <param name="silo">The Power Silo building to get (or roll) requirements for.</param>
    public List<PowerSiloTierConfig> GetEffectiveTiers(Building silo)
    {
        if (this.CachedEffectiveTiersBySilo.TryGetValue(silo, out List<PowerSiloTierConfig>? cached))
            return cached;

        List<PowerSiloTierConfig> baseTiers = this.GetBaseTiers();
        List<PowerSiloTierPool>? pools = this.GetTierPools();
        if (pools is not { Count: > 0 } || !Context.IsWorldReady)
        {
            // MOD: added — no pools configured, or called before a save is loaded (e.g. during early mod
            // init): fall back to the base tiers unmodified, and don't cache — a later call once the
            // world IS ready should still roll normally instead of being stuck on this fallback forever.
            return baseTiers;
        }

        Dictionary<int, List<PowerSiloRequiredItem>> rolled = this.LoadOrRoll(silo, baseTiers, pools);

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

        this.CachedEffectiveTiersBySilo[silo] = effective;
        return effective;
    }

    /// <summary>Clear every cached effective tier list — call this on save load, so a different save (or the same save reloaded) re-reads each Silo's own persisted roll instead of reusing whatever was cached in memory from before.</summary>
    public void Reset()
    {
        this.CachedEffectiveTiersBySilo.Clear();
    }

    /// <summary>
    /// MOD: added. Discard EVERY Power Silo's persisted roll (see <see cref="RolledRequirementsModDataKey"/>)
    /// across every location in the save, AND the in-memory cache, so the next <see cref="GetEffectiveTiers"/>
    /// call for each Silo rolls fresh from the CURRENT <see cref="ModConfig.PowerSiloTierPools"/> instead
    /// of replaying whatever was rolled before — a dev/testing convenience for iterating on tier pool
    /// balance without needing to start a new save each time. Exposed via the
    /// <c>automate reset_silo_tiers</c> console command. MOD: changed — now clears every Silo BUILDING's
    /// own roll (the roll moved from a single save-wide key to a per-building one — see this class's own
    /// remarks), not just one global value; removing the key from a building that never had it (i.e. not
    /// actually a Power Silo) is a harmless no-op, so this doesn't need to know which buildingType names
    /// actually count as a Power Silo.
    /// </summary>
    public void ResetSavedRoll()
    {
        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
                building.modData.Remove(PowerSiloTierRoller.RolledRequirementsModDataKey);
        }

        this.CachedEffectiveTiersBySilo.Clear();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get a Power Silo's already-rolled requirements if present, otherwise roll fresh ones and persist them immediately.</summary>
    /// <param name="silo">The Power Silo building to get (or roll) requirements for.</param>
    /// <param name="baseTiers">The base capacity tiers, for bounds-checking the pools against.</param>
    /// <param name="pools">The randomized pools to roll from.</param>
    private Dictionary<int, List<PowerSiloRequiredItem>> LoadOrRoll(Building silo, List<PowerSiloTierConfig> baseTiers, List<PowerSiloTierPool> pools)
    {
        if (silo.modData.TryGetValue(PowerSiloTierRoller.RolledRequirementsModDataKey, out string? raw))
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

        silo.modData[PowerSiloTierRoller.RolledRequirementsModDataKey] = JsonConvert.SerializeObject(rolled);
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

using System;
using System.Collections.Generic;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Encapsulates the Power Relay's "efficiency boost" mechanic — a global (save-wide, not
/// per-location or per-group) bonus to <see cref="ModConfig.ActionsPerDelayWindow"/>/<see cref="ModConfig.ActionDelaySeconds"/>
/// based on two independent items delivered to every placed Power Relay, each tracked and capped
/// separately (up to <see cref="MaxShards"/> levels from Prismatic Shard for the delay reduction,
/// <see cref="MaxBars"/> levels from Radioactive Bar for the actions-per-window bonus) — mirroring
/// <see cref="PowerSiloInteraction"/>'s own "click the building while holding the item" delivery, not a
/// held/reversible item slot (an earlier version of this class supported pulling delivered items back
/// out; per direct user request, delivery is now one-way, same as a Power Silo tier).
///
/// MOD: each level costs progressively more raw items than the last — level 1 costs 1 item, level 2
/// costs 2 MORE (3 total), level 3 costs 3 more (6 total), and so on, so <see cref="ShardsDeliveredModDataKey"/>/
/// <see cref="BarsDeliveredModDataKey"/> actually store the RAW cumulative item count (0-<see cref="MaxCumulativeShards"/>/<see cref="MaxCumulativeBars"/>),
/// not the level directly — <see cref="GetShardLevel"/>/<see cref="GetBarLevel"/> derive the level (0-<see cref="MaxShards"/>/<see cref="MaxBars"/>,
/// one per icon) from that cumulative count, per direct user request.
///
/// Cheap enough (a buildings-only scan, mirroring <see cref="PowerSiloSystem.GetTotalCapacity"/>) to
/// just recompute on demand every time <see cref="ModEntry"/>'s pacing code asks — no caching, no
/// refresh triggers, one less thing that can drift stale.
/// </summary>
internal class PowerRelaySystem
{
    /*********
    ** Fields
    *********/
    /// <summary>Get whether the Power Relay mechanic is currently enabled.</summary>
    private readonly Func<bool> GetEnabledFromConfig;

    /// <summary>Get the <c>buildingType</c> ID(s) that count as a Power Relay.</summary>
    private readonly Func<HashSet<string>> GetRelayBuildingNames;

    /// <summary>Get how much a single shard level subtracts from <see cref="ModConfig.ActionDelaySeconds"/>, in seconds.</summary>
    private readonly Func<float> GetActionDelayReductionPerShard;

    /// <summary>Get how much a single bar level adds to <see cref="ModConfig.ActionsPerDelayWindow"/>.</summary>
    private readonly Func<int> GetActionsPerDelayWindowBonusPerBar;

    /// <summary>Get the current base <see cref="ModConfig.ActionDelaySeconds"/>, before the Power Relay bonus is applied.</summary>
    private readonly Func<float> GetBaseActionDelaySeconds;

    /// <summary>Get the lowest <see cref="ModConfig.ActionDelaySeconds"/> can ever be pushed down to by Power Relay shard deliveries — see <see cref="ModConfig.PowerRelayMinimumActionDelaySeconds"/>'s own remarks.</summary>
    private readonly Func<float> GetMinimumActionDelaySeconds;

    /// <summary>The maximum number of shard levels (icons) a single Relay can reach.</summary>
    public const int MaxShards = 4;

    /// <summary>The maximum number of bar levels (icons) a single Relay can reach.</summary>
    public const int MaxBars = 4;

    /// <summary>The total raw Prismatic Shards needed to reach <see cref="MaxShards"/> (1+2+3+4).</summary>
    public static readonly int MaxCumulativeShards = PowerRelaySystem.GetCumulativeRequiredForLevel(PowerRelaySystem.MaxShards);

    /// <summary>The total raw Radioactive Bars needed to reach <see cref="MaxBars"/> (1+2+3+4).</summary>
    public static readonly int MaxCumulativeBars = PowerRelaySystem.GetCumulativeRequiredForLevel(PowerRelaySystem.MaxBars);

    /// <summary>The <see cref="Building.modData"/> key storing how many raw shards a Relay has had delivered in total.</summary>
    private const string ShardsDeliveredModDataKey = "luisMint.AutomatePowerPipes/RelayShardsDelivered";

    /// <summary>The <see cref="Building.modData"/> key storing how many raw bars a Relay has had delivered in total.</summary>
    private const string BarsDeliveredModDataKey = "luisMint.AutomatePowerPipes/RelayBarsDelivered";


    /*********
    ** Accessors
    *********/
    /// <summary>Whether the Power Relay mechanic is currently enabled. When <c>false</c>, every Relay's delivered items are ignored entirely (no bonus, though the building itself still exists and can still be interacted with).</summary>
    public bool IsEnabled => this.GetEnabledFromConfig();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getEnabled">Get whether the Power Relay mechanic is currently enabled.</param>
    /// <param name="getRelayBuildingNames">Get the <c>buildingType</c> ID(s) that count as a Power Relay.</param>
    /// <param name="getActionDelayReductionPerShard">Get how much a single shard level subtracts from <see cref="ModConfig.ActionDelaySeconds"/>, in seconds.</param>
    /// <param name="getActionsPerDelayWindowBonusPerBar">Get how much a single bar level adds to <see cref="ModConfig.ActionsPerDelayWindow"/>.</param>
    /// <param name="getBaseActionDelaySeconds">Get the current base <see cref="ModConfig.ActionDelaySeconds"/>, before the Power Relay bonus is applied.</param>
    /// <param name="getMinimumActionDelaySeconds">Get the lowest <see cref="ModConfig.ActionDelaySeconds"/> can ever be pushed down to by Power Relay shard deliveries.</param>
    public PowerRelaySystem(Func<bool> getEnabled, Func<HashSet<string>> getRelayBuildingNames, Func<float> getActionDelayReductionPerShard, Func<int> getActionsPerDelayWindowBonusPerBar, Func<float> getBaseActionDelaySeconds, Func<float> getMinimumActionDelaySeconds)
    {
        this.GetEnabledFromConfig = getEnabled;
        this.GetRelayBuildingNames = getRelayBuildingNames;
        this.GetActionDelayReductionPerShard = getActionDelayReductionPerShard;
        this.GetActionsPerDelayWindowBonusPerBar = getActionsPerDelayWindowBonusPerBar;
        this.GetBaseActionDelaySeconds = getBaseActionDelaySeconds;
        this.GetMinimumActionDelaySeconds = getMinimumActionDelaySeconds;
    }

    /// <summary>Get how many raw shards a Relay has had delivered in total (0-<see cref="MaxCumulativeShards"/>).</summary>
    /// <param name="relay">The Power Relay building.</param>
    public int GetShardsDelivered(Building relay)
    {
        return relay.modData.TryGetValue(PowerRelaySystem.ShardsDeliveredModDataKey, out string? raw) && int.TryParse(raw, out int count)
            ? Math.Clamp(count, 0, PowerRelaySystem.MaxCumulativeShards)
            : 0;
    }

    /// <summary>Set how many raw shards a Relay has had delivered in total.</summary>
    /// <param name="relay">The Power Relay building.</param>
    /// <param name="count">The new delivered count, clamped to <see cref="MaxCumulativeShards"/>.</param>
    public void SetShardsDelivered(Building relay, int count)
    {
        relay.modData[PowerRelaySystem.ShardsDeliveredModDataKey] = Math.Clamp(count, 0, PowerRelaySystem.MaxCumulativeShards).ToString();
    }

    /// <summary>Get how many raw bars a Relay has had delivered in total (0-<see cref="MaxCumulativeBars"/>).</summary>
    /// <param name="relay">The Power Relay building.</param>
    public int GetBarsDelivered(Building relay)
    {
        return relay.modData.TryGetValue(PowerRelaySystem.BarsDeliveredModDataKey, out string? raw) && int.TryParse(raw, out int count)
            ? Math.Clamp(count, 0, PowerRelaySystem.MaxCumulativeBars)
            : 0;
    }

    /// <summary>Set how many raw bars a Relay has had delivered in total.</summary>
    /// <param name="relay">The Power Relay building.</param>
    /// <param name="count">The new delivered count, clamped to <see cref="MaxCumulativeBars"/>.</param>
    public void SetBarsDelivered(Building relay, int count)
    {
        relay.modData[PowerRelaySystem.BarsDeliveredModDataKey] = Math.Clamp(count, 0, PowerRelaySystem.MaxCumulativeBars).ToString();
    }

    /// <summary>Get how many shard levels (icons, 0-<see cref="MaxShards"/>) a Relay has reached, derived from its raw cumulative delivered count.</summary>
    /// <param name="relay">The Power Relay building.</param>
    public int GetShardLevel(Building relay)
    {
        return PowerRelaySystem.GetLevelForCumulative(this.GetShardsDelivered(relay), PowerRelaySystem.MaxShards);
    }

    /// <summary>Get how many bar levels (icons, 0-<see cref="MaxBars"/>) a Relay has reached, derived from its raw cumulative delivered count.</summary>
    /// <param name="relay">The Power Relay building.</param>
    public int GetBarLevel(Building relay)
    {
        return PowerRelaySystem.GetLevelForCumulative(this.GetBarsDelivered(relay), PowerRelaySystem.MaxBars);
    }

    /// <summary>Get how many more raw shards are needed to reach this Relay's next shard level — 0 if <see cref="MaxShards"/> is already reached.</summary>
    /// <param name="relay">The Power Relay building.</param>
    public int GetShardsNeededForNextLevel(Building relay)
    {
        int level = this.GetShardLevel(relay);
        return level >= PowerRelaySystem.MaxShards
            ? 0
            : PowerRelaySystem.GetCumulativeRequiredForLevel(level + 1) - this.GetShardsDelivered(relay);
    }

    /// <summary>Get how many more raw bars are needed to reach this Relay's next bar level — 0 if <see cref="MaxBars"/> is already reached.</summary>
    /// <param name="relay">The Power Relay building.</param>
    public int GetBarsNeededForNextLevel(Building relay)
    {
        int level = this.GetBarLevel(relay);
        return level >= PowerRelaySystem.MaxBars
            ? 0
            : PowerRelaySystem.GetCumulativeRequiredForLevel(level + 1) - this.GetBarsDelivered(relay);
    }

    /// <summary>
    /// Get the total shard levels reached across every Power Relay in the save — cheap (buildings only,
    /// no object scan), so it's safe to call on demand every time the pacing code needs it rather than
    /// caching it. Returns 0 if the mechanic is disabled.
    /// </summary>
    public int GetTotalShardLevels()
    {
        return this.SumAcrossRelays(this.GetShardLevel);
    }

    /// <summary>Get the total bar levels reached across every Power Relay in the save — see <see cref="GetTotalShardLevels"/>'s remarks.</summary>
    public int GetTotalBarLevels()
    {
        return this.SumAcrossRelays(this.GetBarLevel);
    }

    /// <summary>Get the total bonus to <see cref="ModConfig.ActionsPerDelayWindow"/> from every bar level reached across every Power Relay.</summary>
    public int GetActionsPerDelayWindowBonus()
    {
        return this.GetTotalBarLevels() * this.GetActionsPerDelayWindowBonusPerBar();
    }

    /// <summary>Get the total reduction to <see cref="ModConfig.ActionDelaySeconds"/> from every shard level reached across every Power Relay, in seconds.</summary>
    public float GetActionDelayReduction()
    {
        return this.GetTotalShardLevels() * this.GetActionDelayReductionPerShard();
    }

    /// <summary>
    /// Get the current base <see cref="ModConfig.ActionDelaySeconds"/> after applying every shard level's
    /// reduction, floored at <see cref="ModConfig.PowerRelayMinimumActionDelaySeconds"/> — the single
    /// source of truth for this floor, shared by <see cref="ModEntry"/>'s own pacing code and
    /// <see cref="PowerRelayMenu"/>'s display so neither can drift from the other.
    /// </summary>
    public float GetEffectiveActionDelaySeconds()
    {
        float effectiveDelay = this.GetBaseActionDelaySeconds() - this.GetActionDelayReduction();
        return Math.Max(effectiveDelay, this.GetMinimumActionDelaySeconds());
    }

    /// <summary>
    /// MOD: added. Get whether the delay has already been pushed down to (or past) <see cref="ModConfig.PowerRelayMinimumActionDelaySeconds"/>
    /// — once true, no Power Relay's shard track accepts further deliveries at all (see
    /// <see cref="PowerRelayInteraction"/>), and every Relay's shard row shows a "reached the global cap"
    /// message instead of its own bring-prompt/per-Relay-maxed message (see <see cref="PowerRelayMenu"/>),
    /// per direct user request. Deliberately no equivalent for the actions-per-window side.
    /// </summary>
    public bool IsGlobalSpeedCapped()
    {
        return this.GetEffectiveActionDelaySeconds() <= this.GetMinimumActionDelaySeconds();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get how many raw items are needed in total to reach a given level (1-indexed) from scratch — the Nth triangular number (1, 3, 6, 10, 15, ...), since level N alone costs N more than the level before it.</summary>
    /// <param name="level">The level to reach (1-indexed).</param>
    private static int GetCumulativeRequiredForLevel(int level)
    {
        return level * (level + 1) / 2;
    }

    /// <summary>Get the level (0-<paramref name="maxLevel"/>) reached for a given raw cumulative delivered count.</summary>
    /// <param name="cumulative">The raw cumulative delivered count.</param>
    /// <param name="maxLevel">The highest level obtainable.</param>
    private static int GetLevelForCumulative(int cumulative, int maxLevel)
    {
        int level = 0;
        while (level < maxLevel && cumulative >= PowerRelaySystem.GetCumulativeRequiredForLevel(level + 1))
            level++;
        return level;
    }

    /// <summary>Sum a per-Relay value across every Power Relay in the save, skipping ones still under construction. Returns 0 if the mechanic is disabled.</summary>
    /// <param name="getValue">Get the value to sum for a single Relay building.</param>
    private int SumAcrossRelays(Func<Building, int> getValue)
    {
        if (!this.IsEnabled)
            return 0;

        HashSet<string> relayBuildingNames = this.GetRelayBuildingNames();
        if (relayBuildingNames.Count == 0)
            return 0;

        int total = 0;
        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
            {
                if (!relayBuildingNames.Contains(building.buildingType.Value))
                    continue;

                // a Relay still under construction doesn't contribute yet, matching how a Power Silo
                // under construction doesn't contribute capacity yet (see PowerSiloSystem.GetTotalCapacity).
                if (building.daysOfConstructionLeft.Value > 0)
                    continue;

                total += getValue(building);
            }
        }

        return total;
    }
}

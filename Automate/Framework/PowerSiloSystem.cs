using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Buildings;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Encapsulates Automate's optional "power silo capacity" mechanic — a global cap (across
/// every location in the save, not just one) on how many Power Coils can be active at once, set by how
/// many Power Silo buildings exist and what tier each has reached. Coils beyond the cap simply don't
/// provide power, oldest placed first — mirroring <see cref="PowerSystem"/>'s own "disabled =
/// unrestricted" philosophy when <see cref="ModConfig.PowerSiloSystemEnabled"/> is off.
///
/// Unlike <see cref="PowerSystem"/>/<see cref="PowerRequiredMachineSystem"/> (which resolve their gate
/// live, every time they're asked), this one is event-driven: <see cref="RefreshCoilAllowance"/> does
/// the expensive full-save coil scan and stamps each coil's own <see cref="SObject.modData"/> with
/// whether it's currently powered, and is only called when something that could actually change the
/// answer happens — a coil placed or destroyed (see <see cref="Patches.PowerSiloPatches"/>), the
/// total capacity itself changing (a Silo built, destroyed, or fed to a new tier — see
/// <see cref="MachineManager"/>'s cheap <see cref="GetTotalCapacity"/> comparison), or an unconditional
/// refresh every new day as a backstop (see <c>ModEntry.OnDayStarted</c>). Everything else
/// (<see cref="PowerSystem.GetPoweredTiles"/>, the Power Coil's own draw patches) just reads that
/// stamped modData directly — an O(1) check with no coil scan involved.
/// </summary>
internal class PowerSiloSystem
{
    /*********
    ** Fields
    *********/
    /// <summary>Get whether the power silo capacity mechanic is currently enabled.</summary>
    private readonly Func<bool> GetEnabledFromConfig;

    /// <summary>Get the <c>buildingType</c> ID(s) that count as a Power Silo.</summary>
    private readonly Func<HashSet<string>> GetSiloBuildingNames;

    /// <summary>Get the ordered capacity tiers a Power Silo progresses through.</summary>
    private readonly Func<List<PowerSiloTierConfig>> GetTiers;

    /// <summary>Get the Power Coil capacity available with no Power Silo built at all.</summary>
    private readonly Func<int> GetBaseCapacity;

    /// <summary>Get the item names/IDs that count as a Power Coil for capacity purposes — the same set <see cref="PowerSystem"/> itself uses as its power sources, so a coil isn't tracked as two independent lists that could drift apart.</summary>
    private readonly Func<HashSet<string>> GetSourceNames;

    /// <summary>MOD: added. Get the item names/IDs that count as a Solar Panel for the solar tier's connected-panel bonus.</summary>
    private readonly Func<HashSet<string>> GetSolarPanelNames;

    /// <summary>MOD: added. Get a location's currently-powered tiles (see <see cref="PowerSystem.GetPoweredTiles"/>), or <c>null</c> if the power system itself is disabled (every tile counts as powered) — used to decide whether a Solar Panel counts as "connected".</summary>
    private readonly Func<GameLocation, IReadOnlySet<Vector2>?> GetPoweredTilesForLocation;

    /// <summary>
    /// MOD: added. How many Solar Panels are currently connected (see <see cref="GetConnectedSolarPanelCount"/>),
    /// as of the last <see cref="RefreshConnectedSolarPanelCount"/> call — cached rather than
    /// recomputed on every read, since counting requires a full-save object scan just like
    /// <see cref="RefreshCoilAllowance"/> does for coils.
    /// </summary>
    private int CachedConnectedSolarPanelCount;

    /// <summary>The <see cref="Building.modData"/> key storing which tier a Power Silo has reached (an index into <see cref="GetTiers"/>).</summary>
    private const string CapacityTierModDataKey = "luisMint.AutomatePowerPipes/CapacityTier";

    /// <summary>
    /// The <see cref="Building.modData"/> key storing how much of each of the CURRENT tier's
    /// <see cref="Models.PowerSiloTierConfig.RequiredItems"/> has been delivered so far, as a
    /// comma-separated list of counts index-aligned with that list (e.g. <c>"3,0"</c> = 3 delivered of
    /// the first required item, 0 of the second). Reset whenever the tier advances (see
    /// <see cref="ResetDeliveryProgress"/>), since the next tier's requirements start fresh.
    /// </summary>
    private const string TierProgressModDataKey = "luisMint.AutomatePowerPipes/TierProgress";

    /// <summary>The <see cref="SObject.modData"/> key storing when a Power Coil was placed (a <see cref="DateTime.Ticks"/> value), used to break ties for <see cref="RefreshCoilAllowance"/>'s oldest-first rule.</summary>
    internal const string PlacementOrderModDataKey = "luisMint.AutomatePowerPipes/PlacementOrder";

    /// <summary>
    /// The <see cref="SObject.modData"/> key storing whether a Power Coil is currently within capacity
    /// (<c>"true"</c>/<c>"false"</c>) — the single source of truth both <see cref="PowerSystem.GetPoweredTiles"/>
    /// (whether it actually provides power) and <see cref="Patches.PowerCoilPatches"/> (how it's drawn)
    /// read directly, rather than each re-deriving it. Missing entirely (e.g. a coil placed before this
    /// mechanic existed, or before the first <see cref="RefreshCoilAllowance"/> call) is treated as
    /// powered, so nothing looks wrong before its first refresh.
    /// </summary>
    internal const string CoilPoweredModDataKey = "luisMint.AutomatePowerPipes/CoilPowered";


    /*********
    ** Accessors
    *********/
    /// <summary>Whether the power silo capacity mechanic is currently enabled. When <c>false</c>, every Power Coil is allowed (as if the mechanic didn't exist).</summary>
    public bool IsEnabled => this.GetEnabledFromConfig();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getEnabled">Get whether the power silo capacity mechanic is currently enabled.</param>
    /// <param name="getSiloBuildingNames">Get the <c>buildingType</c> ID(s) that count as a Power Silo.</param>
    /// <param name="getTiers">Get the ordered capacity tiers a Power Silo progresses through.</param>
    /// <param name="getBaseCapacity">Get the Power Coil capacity available with no Power Silo built at all.</param>
    /// <param name="getSourceNames">Get the item names/IDs that count as a Power Coil for capacity purposes.</param>
    /// <param name="getSolarPanelNames">MOD: added. Get the item names/IDs that count as a Solar Panel for the solar tier's connected-panel bonus.</param>
    /// <param name="getPoweredTilesForLocation">MOD: added. Get a location's currently-powered tiles, or <c>null</c> if the power system itself is disabled.</param>
    public PowerSiloSystem(Func<bool> getEnabled, Func<HashSet<string>> getSiloBuildingNames, Func<List<PowerSiloTierConfig>> getTiers, Func<int> getBaseCapacity, Func<HashSet<string>> getSourceNames, Func<HashSet<string>> getSolarPanelNames, Func<GameLocation, IReadOnlySet<Vector2>?> getPoweredTilesForLocation)
    {
        this.GetEnabledFromConfig = getEnabled;
        this.GetSiloBuildingNames = getSiloBuildingNames;
        this.GetTiers = getTiers;
        this.GetBaseCapacity = getBaseCapacity;
        this.GetSourceNames = getSourceNames;
        this.GetSolarPanelNames = getSolarPanelNames;
        this.GetPoweredTilesForLocation = getPoweredTilesForLocation;
    }

    /// <summary>Get the tier a Power Silo has reached, as an index into <see cref="GetTiers"/>.</summary>
    /// <param name="silo">The Power Silo building.</param>
    public int GetTier(Building silo)
    {
        return silo.modData.TryGetValue(PowerSiloSystem.CapacityTierModDataKey, out string? raw) && int.TryParse(raw, out int tier)
            ? tier
            : 0;
    }

    /// <summary>Set the tier a Power Silo has reached.</summary>
    /// <param name="silo">The Power Silo building.</param>
    /// <param name="tier">The new tier, as an index into <see cref="GetTiers"/>.</param>
    public void SetTier(Building silo, int tier)
    {
        silo.modData[PowerSiloSystem.CapacityTierModDataKey] = tier.ToString();
    }

    /// <summary>Get how many of one of the current tier's required items has been delivered so far.</summary>
    /// <param name="silo">The Power Silo building.</param>
    /// <param name="itemIndex">The item's index into the current tier's <see cref="Models.PowerSiloTierConfig.RequiredItems"/>.</param>
    public int GetDeliveredCount(Building silo, int itemIndex)
    {
        if (!silo.modData.TryGetValue(PowerSiloSystem.TierProgressModDataKey, out string? raw))
            return 0;

        string[] parts = raw.Split(',');
        return itemIndex < parts.Length && int.TryParse(parts[itemIndex], out int delivered)
            ? delivered
            : 0;
    }

    /// <summary>Set how many of one of the current tier's required items has been delivered so far.</summary>
    /// <param name="silo">The Power Silo building.</param>
    /// <param name="itemIndex">The item's index into the current tier's <see cref="Models.PowerSiloTierConfig.RequiredItems"/>.</param>
    /// <param name="delivered">The new delivered count.</param>
    /// <param name="itemCount">How many required items the current tier has in total, so every index has a slot even if not yet touched.</param>
    public void SetDeliveredCount(Building silo, int itemIndex, int delivered, int itemCount)
    {
        int[] counts = new int[itemCount];
        if (silo.modData.TryGetValue(PowerSiloSystem.TierProgressModDataKey, out string? raw))
        {
            string[] parts = raw.Split(',');
            for (int i = 0; i < counts.Length && i < parts.Length; i++)
                int.TryParse(parts[i], out counts[i]);
        }

        counts[itemIndex] = delivered;
        silo.modData[PowerSiloSystem.TierProgressModDataKey] = string.Join(',', counts);
    }

    /// <summary>Clear all delivery progress for a Silo — meant to be called right after <see cref="SetTier"/> advances it, since the new tier's requirements start fresh.</summary>
    /// <param name="silo">The Power Silo building.</param>
    public void ResetDeliveryProgress(Building silo)
    {
        silo.modData.Remove(PowerSiloSystem.TierProgressModDataKey);
    }

    /// <summary>
    /// Get the total Power Coil capacity available across the save — <see cref="GetBaseCapacity"/>
    /// (available even with no Power Silo at all) plus every Power Silo's own contribution at its
    /// current tier. Cheap — only scans buildings, never coils — so it's safe to call every tick purely
    /// to detect a change (see <see cref="MachineManager"/>), reserving the expensive
    /// <see cref="RefreshCoilAllowance"/> scan for when that number (or a coil placement/destruction)
    /// actually changes something.
    /// </summary>
    public int GetTotalCapacity()
    {
        if (!this.IsEnabled)
            return int.MaxValue;

        int total = Math.Max(0, this.GetBaseCapacity());

        HashSet<string> siloBuildingNames = this.GetSiloBuildingNames();
        List<PowerSiloTierConfig> tiers = this.GetTiers();
        if (siloBuildingNames.Count == 0 || tiers.Count == 0)
            return total;

        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
            {
                if (!siloBuildingNames.Contains(building.buildingType.Value))
                    continue;

                // MOD: added — a Silo still under construction (daysOfConstructionLeft > 0) doesn't
                // contribute yet, matching how it also can't be interacted with yet (Building.doAction
                // shows "under construction" instead of dispatching to PowerSiloInteraction for the
                // same reason). Without this, a newly-queued Silo would grant capacity a full day
                // before it's actually standing.
                if (building.daysOfConstructionLeft.Value > 0)
                    continue;

                int tier = Math.Clamp(this.GetTier(building), 0, tiers.Count - 1);
                PowerSiloTierConfig tierConfig = tiers[tier];
                int granted = tierConfig.CapacityGranted;

                // MOD: added — a Silo at the solar tier also contributes the connected-Solar-Panel
                // bonus on top of its own flat CapacityGranted (every 3 connected panels add 1 more).
                // Reads the CACHED count (see RefreshConnectedSolarPanelCount's remarks) rather than
                // rescanning here, since this method is deliberately cheap and called every tick.
                if (tierConfig.GrantsSolarBonus)
                    granted += this.CachedConnectedSolarPanelCount / 3;

                total += granted;
            }
        }

        return total;
    }

    /// <summary>
    /// The expensive part of the mechanic: scan every Power Coil across every location, and stamp each
    /// one's <see cref="CoilPoweredModDataKey"/> with whether it's within the total capacity (see
    /// <see cref="GetTotalCapacity"/>), oldest placed first. A coil with no recorded placement time
    /// (e.g. one placed before this mechanic existed) is treated as the oldest of all, so existing
    /// saves aren't retroactively broken. Only meant to be called on an actual trigger — see this
    /// class's own remarks for the full list.
    /// </summary>
    public void RefreshCoilAllowance()
    {
        HashSet<string> sourceNames = this.GetSourceNames();
        bool enabled = this.IsEnabled;
        int capacity = enabled ? this.GetTotalCapacity() : int.MaxValue;

        List<(SObject Coil, long PlacementOrder)> coils = [];
        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (SObject obj in location.Objects.Values)
            {
                if (!sourceNames.Contains(obj.QualifiedItemId) && !sourceNames.Contains(obj.Name))
                    continue;

                long placementOrder = obj.modData.TryGetValue(PowerSiloSystem.PlacementOrderModDataKey, out string? raw) && long.TryParse(raw, out long parsed)
                    ? parsed
                    : long.MinValue;

                coils.Add((obj, placementOrder));
            }
        }

        coils.Sort((a, b) => a.PlacementOrder.CompareTo(b.PlacementOrder));
        for (int i = 0; i < coils.Count; i++)
        {
            // MOD: fixed — every reader compares against the literal lowercase "false", but
            // bool.ToString() produces "True"/"False" (PascalCase); the mismatch meant a coil stamped
            // "False" never actually compared equal to "false", so every coil silently read back as
            // powered regardless of capacity. Writing the literal lowercase string directly avoids the
            // whole class of case-sensitivity bugs instead of just patching this one spot.
            coils[i].Coil.modData[PowerSiloSystem.CoilPoweredModDataKey] = i < capacity ? "true" : "false";
        }
    }

    /// <summary>
    /// MOD: added. The expensive part of the solar tier's bonus: scan every Solar Panel across every
    /// location, and count how many sit on a tile that's currently powered (through a Power Coil's
    /// range or a Powered Chest's local range — anything already in <see cref="PowerSystem.GetPoweredTiles"/>'s
    /// output counts as "connected"), caching the result for <see cref="GetTotalCapacity"/> to read.
    /// Only meant to be called on an actual trigger (a Solar Panel, Power Coil, or Powered Chest placed
    /// or removed — see <see cref="Patches.PowerSiloPatches"/> — or an unconditional refresh every new
    /// day as a backstop), not every tick, for the same reason <see cref="RefreshCoilAllowance"/> isn't.
    /// </summary>
    public void RefreshConnectedSolarPanelCount()
    {
        HashSet<string> solarPanelNames = this.GetSolarPanelNames();
        if (solarPanelNames.Count == 0)
        {
            this.CachedConnectedSolarPanelCount = 0;
            return;
        }

        int count = 0;
        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            // MOD: added — a Solar Panel only makes sense generating power outdoors (under the sky),
            // regardless of time of day/season/weather; skip interiors (farmhouse, sheds, mines, etc.)
            // entirely rather than letting one sit in a building and still count.
            if (!location.IsOutdoors)
                continue;

            IReadOnlySet<Vector2>? poweredTiles = this.GetPoweredTilesForLocation(location);

            foreach (SObject obj in location.Objects.Values)
            {
                if (!solarPanelNames.Contains(obj.QualifiedItemId) && !solarPanelNames.Contains(obj.Name))
                    continue;

                if (poweredTiles is null || poweredTiles.Contains(obj.TileLocation))
                    count++;
            }
        }

        this.CachedConnectedSolarPanelCount = count;
    }

    /// <summary>Get how many Solar Panels are currently connected — see <see cref="RefreshConnectedSolarPanelCount"/>'s remarks for how "connected" is determined and when this number actually updates.</summary>
    public int GetConnectedSolarPanelCount()
    {
        return this.CachedConnectedSolarPanelCount;
    }

    /// <summary>
    /// Get how many Power Coils are currently placed across the save, out of the total capacity
    /// granted by every Power Silo — e.g. for a "Power Grid Capacity: 12/10" popup. Deliberately
    /// counts EVERY matching coil, not just the powered ones — capping the shown count at the
    /// capacity would silently hide the fact that some are actually sitting unpowered (over the
    /// limit), which defeats the point of showing a number at all; letting the first value exceed
    /// the second IS the signal that some placed coils aren't currently getting power. Reflects
    /// whatever <see cref="RefreshCoilAllowance"/> last stamped, not a fresh recompute.
    /// </summary>
    public (int TotalCoils, int Capacity) GetUsage()
    {
        HashSet<string> sourceNames = this.GetSourceNames();
        int capacity = this.GetTotalCapacity();

        int totalCoils = 0;
        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (SObject obj in location.Objects.Values)
            {
                if (sourceNames.Contains(obj.QualifiedItemId) || sourceNames.Contains(obj.Name))
                    totalCoils++;
            }
        }

        return (totalCoils, capacity);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Encapsulates Automate's optional "power-required machines" balance mechanic — some
/// otherwise-overpowered machines (e.g. Crystalarium, Auto-Grabber, Auto-Petter, Heavy Furnace, see
/// <see cref="Models.ModConfig.PowerRequiredMachineNames"/>) only run, accept input, or can be
/// interacted with at all while their own tile is within range of a power source (see
/// <see cref="PowerSystem"/>). Keeping this logic in its own self-contained class (mirroring
/// <see cref="PowerSystem"/>) means the whole mechanic can be extended, reworked, or removed by
/// touching only this file plus its two call sites: the build-time automation gate
/// (<see cref="MachineGroupFactory"/>/<see cref="MachineGroupBuilder"/>/<see cref="MachineGroup"/>)
/// and the live per-interaction gate (<see cref="Patches.PowerRequiredMachinePatches"/>) — both read
/// through the single <see cref="IsPowerStarved"/> check here rather than each resolving "is this
/// machine starved" their own way.
///
/// A machine's "type" here is always its resolved <c>MachineTypeID</c> string (see
/// <see cref="IMachine.MachineTypeID"/> / <see cref="BaseMachine.GetDefaultMachineId(string)"/>) —
/// the same identifier already used throughout Automate for per-machine-type config (e.g.
/// <see cref="Models.ModConfig.MachineOverrides"/>). Any machine — vanilla or added by another mod
/// via Automate's own automation factory API — can opt into this mechanic just by having its type ID
/// added to <see cref="Models.ModConfig.PowerRequiredMachineNames"/>. The one caveat is the LIVE,
/// manual-interaction half of the gate (<see cref="Patches.PowerRequiredMachinePatches"/>): it works
/// by patching the vanilla <see cref="StardewValley.Object"/> interaction methods directly, so it can
/// only intercept a mod machine that's actually a <see cref="StardewValley.Object"/> using those same
/// methods (true for the vast majority of Data/Machines-driven mod machines, same as vanilla's own
/// Crystalarium/Heavy Furnace/etc.) — a mod machine backed by something else entirely (a custom
/// non-Object type, or one that overrides those methods without calling the vanilla base) wouldn't be
/// caught by that half, though it would still be correctly gated out of Automate's own automation loop
/// either way, since that half only depends on Automate's own <see cref="IMachine"/> abstraction.
/// </summary>
internal class PowerRequiredMachineSystem
{
    /*********
    ** Fields
    *********/
    /// <summary>Get whether the power-required-machines mechanic is currently enabled.</summary>
    private readonly Func<bool> GetEnabledFromConfig;

    /// <summary>Get the machine type IDs that require their own tile to be within power range.</summary>
    private readonly Func<HashSet<string>> GetMachineTypeNames;

    /// <summary>Get whether the periodic "Connected machine needs power in {location}" callout is enabled — see <see cref="Models.ModConfig.ConnectedMachineLocationPowerCallouts"/>.</summary>
    private readonly Func<bool> GetGlobalCalloutsEnabledFromConfig;

    /// <summary>MOD: added. Get how often to repeat the "Connected machine needs power in {location}" callout for a location that still has a starved machine, in real-world seconds, while <see cref="GetGlobalCalloutsEnabledFromConfig"/> is enabled — see <see cref="Models.ModConfig.ConnectedMachineLocationPowerCalloutIntervalSeconds"/>.</summary>
    private readonly Func<int> GetCalloutIntervalSeconds;

    /// <summary>Get the location instance for a location key, if it's currently tracked — used to resolve a friendly display name for the periodic callout.</summary>
    private readonly Func<string, GameLocation?> GetLocationByKey;

    /// <summary>MOD: added. The power-starved tiles known as of the last <see cref="ProcessStarvedMachineCallouts"/> call, by location key — compared against each call's freshly-computed set to detect when a location NEWLY has a starved tile it didn't already have, for the one-off notice.</summary>
    private readonly Dictionary<string, HashSet<Vector2>> PreviouslyStarvedTilesByLocation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MOD: added. The game time (in milliseconds) when each location is next allowed to show its repeating "Connected machine needs power in {location}" callout.</summary>
    private readonly Dictionary<string, double> NextCalloutTimeByLocation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MOD: added. Whether the NEXT <see cref="ProcessStarvedMachineCallouts"/> call should silently
    /// re-establish its baseline instead of firing the one-off "newly starved" notice — set by
    /// <see cref="Reset"/> (day start / save load). Without this, the first check after a reset has no
    /// baseline at all, so every machine that was ALREADY starved before the reset looks "newly
    /// starved" and fires the reminder immediately on wake-up, even though nothing actually just
    /// became starved.
    /// </summary>
    private bool suppressNextNewlyStarvedNotice = true;


    /*********
    ** Accessors
    *********/
    /// <summary>Whether the power-required-machines mechanic is currently enabled. When <c>false</c>, every machine is unrestricted (as if the mechanic didn't exist).</summary>
    public bool IsEnabled => this.GetEnabledFromConfig();


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getEnabled">Get whether the power-required-machines mechanic is currently enabled.</param>
    /// <param name="getMachineTypeNames">Get the machine type IDs that require their own tile to be within power range.</param>
    /// <param name="getGlobalCalloutsEnabled">Get whether the periodic "Connected machine needs power in {location}" callout is enabled.</param>
    /// <param name="getCalloutIntervalSeconds">MOD: added. Get how often to repeat the periodic callout, in real-world seconds.</param>
    /// <param name="getLocationByKey">Get the location instance for a location key, if it's currently tracked.</param>
    public PowerRequiredMachineSystem(Func<bool> getEnabled, Func<HashSet<string>> getMachineTypeNames, Func<bool> getGlobalCalloutsEnabled, Func<int> getCalloutIntervalSeconds, Func<string, GameLocation?> getLocationByKey)
    {
        this.GetEnabledFromConfig = getEnabled;
        this.GetMachineTypeNames = getMachineTypeNames;
        this.GetGlobalCalloutsEnabledFromConfig = getGlobalCalloutsEnabled;
        this.GetCalloutIntervalSeconds = getCalloutIntervalSeconds;
        this.GetLocationByKey = getLocationByKey;
    }

    /// <summary>Get whether a machine type is configured to require power at all, regardless of whether any particular instance is currently starved.</summary>
    /// <param name="machineTypeId">The machine's resolved type ID.</param>
    public bool RequiresPower(string machineTypeId)
    {
        return this.IsEnabled && this.GetMachineTypeNames().Contains(machineTypeId);
    }

    /// <summary>
    /// MOD: added. Get whether a machine is configured to require power, checking every candidate
    /// identifier for it rather than just one — <see cref="Models.ModConfig.PowerRequiredMachineNames"/>
    /// accepts EITHER a machine's resolved friendly type ID (e.g. "Extruder") OR its raw qualified/
    /// unqualified item ID (e.g. "(BC)Cornucopia_Extruder" / "Cornucopia_Extruder") — the friendly ID is
    /// what <c>automate summary</c> shows and is what most players naturally think to type, but a
    /// third-party mod's own internal naming doesn't always strip down to something intuitive (confirmed
    /// via user report — Cornucopia's own "Cornucopia_Extruder" doesn't reduce to "Extruder" since its
    /// object Name uses a different, shorter prefix than its manifest UniqueID). Accepting the raw item ID
    /// too means a player can always just copy it from any item-ID-showing tool (Lookup Anything, Chests
    /// Anywhere, a debug spawner, etc.) as a reliable fallback, without needing to understand Automate's
    /// own ID-resolution rules at all.
    /// </summary>
    /// <param name="candidateIds">Every identifier this specific machine could reasonably be configured under.</param>
    public bool RequiresPower(IEnumerable<string> candidateIds)
    {
        if (!this.IsEnabled)
            return false;

        HashSet<string> configured = this.GetMachineTypeNames();
        return candidateIds.Any(configured.Contains);
    }

    /// <summary>Get whether a machine is currently power-starved — i.e. its type requires power, but none of its tiles are within range of a source.</summary>
    /// <param name="machineTypeId">The machine's resolved type ID.</param>
    /// <param name="tiles">The tiles the machine occupies.</param>
    /// <param name="poweredTiles">The location's currently-powered tiles (see <see cref="PowerSystem.GetPoweredTiles"/>), or <c>null</c> if the power system itself is disabled (everything unrestricted).</param>
    public bool IsPowerStarved(string machineTypeId, IEnumerable<Vector2> tiles, IReadOnlySet<Vector2>? poweredTiles)
    {
        return this.RequiresPower(machineTypeId) && poweredTiles != null && !tiles.Any(poweredTiles.Contains);
    }

    /// <summary>MOD: added. The <see cref="IEnumerable{String}"/> counterpart to <see cref="IsPowerStarved(string,System.Collections.Generic.IEnumerable{Microsoft.Xna.Framework.Vector2},System.Collections.Generic.IReadOnlySet{Microsoft.Xna.Framework.Vector2}?)"/> — see <see cref="RequiresPower(IEnumerable{string})"/>'s own remarks for why a machine may need to be checked under more than one candidate identifier.</summary>
    /// <param name="candidateIds">Every identifier this specific machine could reasonably be configured under.</param>
    /// <param name="tiles">The tiles the machine occupies.</param>
    /// <param name="poweredTiles">The location's currently-powered tiles (see <see cref="PowerSystem.GetPoweredTiles"/>), or <c>null</c> if the power system itself is disabled (everything unrestricted).</param>
    public bool IsPowerStarved(IEnumerable<string> candidateIds, IEnumerable<Vector2> tiles, IReadOnlySet<Vector2>? poweredTiles)
    {
        return this.RequiresPower(candidateIds) && poweredTiles != null && !tiles.Any(poweredTiles.Contains);
    }

    /// <summary>Show the standard "needs power" reminder message to the player, matching vanilla's own style for a machine missing a required ingredient (e.g. a Furnace with no coal).</summary>
    public static void ShowNeedsPowerMessage()
    {
        Game1.showRedMessage(I18n.Message_MachineNeedsPower());
    }

    /// <summary>
    /// MOD: added. Clear all tracked callout state — call on day start (or save load), mirroring
    /// <c>PowerSiloCapPatches.Reset</c>/<c>PowerRelayEffectPatches.Reset</c>'s own day-start cache
    /// clears elsewhere in this codebase. Also marks the next <see cref="ProcessStarvedMachineCallouts"/>
    /// call to silently re-establish its baseline instead of treating every already-starved machine as
    /// newly discovered — see <see cref="suppressNextNewlyStarvedNotice"/>'s own remarks for why that
    /// matters.
    /// </summary>
    public void Reset()
    {
        this.PreviouslyStarvedTilesByLocation.Clear();
        this.NextCalloutTimeByLocation.Clear();
        this.suppressNextNewlyStarvedNotice = true;
    }

    /// <summary>
    /// MOD: added. Check every active machine group for power-starved machines and show the
    /// appropriate reminder — meant to be called once per automation tick (the same cadence
    /// <see cref="MachineGroup.Automate"/> itself runs at), NOT tied to a rescan, since a starved
    /// machine's condition doesn't otherwise change tick-to-tick and the repeating callout below needs
    /// a steady real-time cadence independent of whether anything in the world actually changed.
    ///
    /// Two distinct behaviors, gathered per location (since the repeating callout names the location):
    /// <list type="bullet">
    /// <item>The moment a location has a starved tile it didn't have on the PREVIOUS call (i.e. a
    /// machine just became starved, or a whole new starved group just connected), the plain
    /// <see cref="ShowNeedsPowerMessage"/> fires once — unconditional, not gated by
    /// <see cref="GetGlobalCalloutsEnabledFromConfig"/>, since this is a one-off notice rather than an
    /// ongoing nag.</item>
    /// <item>While a location continues to have at least one starved tile AND
    /// <see cref="GetGlobalCalloutsEnabledFromConfig"/> is enabled, a location-specific
    /// "Connected machine needs power in {location}" message repeats every
    /// <see cref="GetCalloutIntervalSeconds"/> seconds. See
    /// <see cref="Models.ModConfig.ConnectedMachineLocationPowerCallouts"/>/<see cref="Models.ModConfig.ConnectedMachineLocationPowerCalloutIntervalSeconds"/>.</item>
    /// </list>
    ///
    /// Deliberately keyed by LOCATION (not by machine instance) — <see cref="MachineGroup"/> instances
    /// are rebuilt fresh on every rescan with no stable identity across rebuilds, which is exactly what
    /// caused the OLD per-machine-instance throttle (removed from <see cref="MachineGroup.Automate"/>)
    /// to reset far more often than intended and spam the message every few seconds instead of the
    /// throttle it was supposed to enforce. Tracking by location key here survives rescans just fine.
    /// </summary>
    /// <param name="activeGroups">The currently active machine groups across every location.</param>
    public void ProcessStarvedMachineCallouts(IEnumerable<IMachineGroup> activeGroups)
    {
        Dictionary<string, HashSet<Vector2>> starvedTilesByLocation = new(StringComparer.OrdinalIgnoreCase);
        foreach (IMachineGroup group in activeGroups)
        {
            // MOD: a Junimo-chest aggregate group's own LocationKey is always null (it spans multiple
            // locations); its underlying per-location sub-groups are handled separately when THEY
            // appear in activeGroups instead, so this simply has nothing to report for itself.
            if (group.LocationKey == null)
                continue;

            IReadOnlySet<Vector2> groupStarvedTiles = group.GetPowerStarvedTiles(group.LocationKey);
            if (groupStarvedTiles.Count == 0)
                continue;

            if (!starvedTilesByLocation.TryGetValue(group.LocationKey, out HashSet<Vector2>? locationStarvedTiles))
                starvedTilesByLocation[group.LocationKey] = locationStarvedTiles = new HashSet<Vector2>();
            locationStarvedTiles.UnionWith(groupStarvedTiles);
        }

        // MOD: forget any location that no longer has a starved tile at all, so a later fresh
        // occurrence there is correctly treated as "newly noticed" again instead of staying silently
        // suppressed by a long-stale snapshot.
        foreach (string clearedLocationKey in this.PreviouslyStarvedTilesByLocation.Keys.Where(key => !starvedTilesByLocation.ContainsKey(key)).ToArray())
        {
            this.PreviouslyStarvedTilesByLocation.Remove(clearedLocationKey);
            this.NextCalloutTimeByLocation.Remove(clearedLocationKey);
        }

        if (starvedTilesByLocation.Count == 0)
            return;

        double curTime = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
        bool globalCalloutsEnabled = this.GetGlobalCalloutsEnabledFromConfig();
        double calloutIntervalMilliseconds = Math.Max(1, this.GetCalloutIntervalSeconds()) * 1000;

        // MOD: added — consume the suppress flag ONCE per call to this method (not once per location),
        // so a reset that affects multiple locations at once doesn't fire the one-off notice for any of
        // them, but a location that becomes newly starved on a LATER call still gets its notice normally.
        bool suppressThisPass = this.suppressNextNewlyStarvedNotice;
        this.suppressNextNewlyStarvedNotice = false;

        foreach ((string locationKey, HashSet<Vector2> currentStarvedTiles) in starvedTilesByLocation)
        {
            bool hasNewlyStarvedTile =
                !this.PreviouslyStarvedTilesByLocation.TryGetValue(locationKey, out HashSet<Vector2>? previousStarvedTiles)
                || currentStarvedTiles.Any(tile => !previousStarvedTiles.Contains(tile));

            if (hasNewlyStarvedTile)
            {
                if (!suppressThisPass)
                    PowerRequiredMachineSystem.ShowNeedsPowerMessage();
                this.NextCalloutTimeByLocation[locationKey] = curTime + calloutIntervalMilliseconds;
            }
            else if (globalCalloutsEnabled && (!this.NextCalloutTimeByLocation.TryGetValue(locationKey, out double nextCalloutTime) || curTime >= nextCalloutTime))
            {
                string locationName = this.GetLocationByKey(locationKey)?.DisplayName ?? locationKey;

                // MOD: added — count the starved tiles for a "N connected machine(s)" count. Tracked by
                // tile rather than machine instance (see PreviouslyStarvedTilesByLocation's own remarks
                // for why), but every power-required machine type in practice occupies exactly one
                // tile, so tile count and machine count are the same number here.
                int count = currentStarvedTiles.Count;
                string message = count == 1
                    ? I18n.Message_MachinesNeedPowerInLocation_Singular(count: count, locationName: locationName)
                    : I18n.Message_MachinesNeedPowerInLocation_Plural(count: count, locationName: locationName);
                Game1.showRedMessage(message);

                this.NextCalloutTimeByLocation[locationKey] = curTime + calloutIntervalMilliseconds;
            }

            this.PreviouslyStarvedTilesByLocation[locationKey] = currentStarvedTiles;
        }
    }
}

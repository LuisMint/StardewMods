using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Wraps a Cask's machine wrapper so it reports itself ready for collection as soon as its
/// held item reaches a quality currently permitted by a quality-tag Category Sign touching its group —
/// whitelist OR blacklist (see <see cref="SignFilter.IsQualityPermittedForEarlyCollection"/>/
/// <see cref="QualityCategory"/>) — instead of only ever becoming ready once vanilla's own aging
/// finishes at iridium.
///
/// Vanilla's own <see cref="Cask"/> (see the decompiled game source) only sets its own
/// <see cref="SObject.readyForHarvest"/> flag once its held item's quality reaches iridium
/// (<c>Cask.checkForMaturity</c>, called from <c>Cask.DayUpdate</c>) — there's no vanilla concept of
/// "ready" at an earlier tier at all. This class does NOT touch that flag at all — the Cask's own
/// visual "ready" indicator and player-facing behavior stay entirely vanilla, only ever flipping true at
/// real (iridium) maturity, exactly as before this feature existed. Instead, <see cref="GetState"/>
/// independently reports <see cref="MachineState.Done"/> for AUTOMATE'S OWN purposes whenever the
/// quality matches — <see cref="GetOutput"/> doesn't need any special handling for this at all, since
/// the wrapped machine's own implementation (see <c>GenericObjectMachine.GetOutput</c>/
/// <c>DataBasedObjectMachine.GetOutput</c>) already just reads whatever's in <c>heldObject</c> directly,
/// with no dependency on <c>readyForHarvest</c> — that flag is only ever consulted by <c>GetState</c> to
/// decide whether Automate's own orchestration tries collecting in the first place.
///
/// Whether it's currently allowed to be TAKEN once collected is still enforced entirely separately, by
/// the group's existing <see cref="ItemFilteredContainer"/>-wrapped destination containers — this class
/// only ever controls when the Cask starts OFFERING its held item, not whether that item is accepted
/// once offered. If the wine ages PAST the whitelisted quality before Automate gets around to collecting
/// it (e.g. a "Gold Quality Tag" whitelist, but the wine reaches iridium before collection), it simply
/// stops matching this class's own quality check — but by then it's independently reached vanilla's own
/// real maturity anyway, so <c>GetState</c> still reports it as done through the normal (now-true)
/// <c>readyForHarvest</c> path regardless; the EXACT-match filter on the container side (not this class)
/// is what would then refuse to actually move it anywhere, exactly matching the "not when it's iridium"
/// behavior an exact-match quality whitelist implies everywhere else in this mod.
///
/// Only ever wraps a machine confirmed to be backed by a real Cask (see <see cref="MachineGroupBuilder.Add"/>)
/// — every other machine type is left completely untouched.
/// </summary>
internal class CaskQualityFilterMachine : IMachine
{
    /*********
    ** Fields
    *********/
    /// <summary>The wrapped machine.</summary>
    private readonly IMachine Inner;

    /// <summary>The underlying Cask object.</summary>
    private readonly Cask Cask;

    /// <summary>The group's resolved whitelist/blacklist sign condition.</summary>
    private readonly SignFilter Filter;


    /*********
    ** Accessors
    *********/
    /// <inheritdoc />
    public string MachineTypeID => this.Inner.MachineTypeID;

    /// <inheritdoc />
    public GameLocation Location => this.Inner.Location;

    /// <inheritdoc />
    public Rectangle TileArea => this.Inner.TileArea;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="inner">The wrapped machine.</param>
    /// <param name="cask">The underlying Cask object.</param>
    /// <param name="filter">The group's resolved whitelist/blacklist sign condition.</param>
    public CaskQualityFilterMachine(IMachine inner, Cask cask, SignFilter filter)
    {
        this.Inner = inner;
        this.Cask = cask;
        this.Filter = filter;
    }

    /// <inheritdoc />
    public MachineState GetState()
    {
        // MOD: added — report done for Automate's own purposes as soon as the held item's quality is
        // currently permitted by a quality-tag condition (whitelist OR blacklist), WITHOUT touching
        // Cask.readyForHarvest at all — see this class's own remarks for why that flag is left entirely
        // alone (only vanilla's own iridium-maturity check ever sets it).
        SObject? held = this.Cask.heldObject.Value;
        if (held != null && this.Filter.IsQualityPermittedForEarlyCollection(held.Quality))
            return MachineState.Done;

        return this.Inner.GetState();
    }

    /// <inheritdoc />
    public ITrackedStack? GetOutput()
    {
        // MOD: added — no quality check needed here; the wrapped machine's own GetOutput() already just
        // reads whatever's in heldObject directly, regardless of readyForHarvest — see this class's own
        // remarks.
        return this.Inner.GetOutput();
    }

    /// <inheritdoc />
    public bool SetInput(IStorage input)
    {
        return this.Inner.SetInput(input);
    }
}

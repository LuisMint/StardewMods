using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Swaps a whitelist/blacklist sign's displayed appearance between a "valid" and
/// "invalid" look, via the Alternative Textures mod's <c>modData</c>-driven rendering — same
/// mechanism <see cref="PoweredFloorSync"/> already uses for connectors (see that class's own remarks
/// for why this approach was chosen over swapping between two separate Data entries).
///
/// A sign counts as "invalid" for any reason it wouldn't actually be enforcing its filter: not placed
/// on a connector at all, connected to a group that isn't a valid (active) automation group, or
/// overridden by another sign of the same kind winning the same group's resolution (e.g. a duplicate
/// whitelist sign for the same item, or a non-numeric blacklist overridden by a whitelist elsewhere in
/// the group). All of that is already resolved by <see cref="MachineGroupFactory"/>'s own sign
/// detection into <see cref="MachineDataForLocation.SignMarkersByTile"/> — which only ever contains
/// tiles from ACTIVE machine groups (see <see cref="MachineDataForLocation"/>'s own lazy accessors,
/// all built from <c>ActiveMachineGroups</c> alone) — so a sign's presence in that lookup already
/// means exactly "valid," with no extra validity logic needed here.
/// </summary>
internal class SignTextureSync
{
    /*********
    ** Fields
    *********/
    /// <summary>The variation index which shows the invalid (not currently enforcing) appearance.</summary>
    internal const int InvalidVariation = 0;

    /// <summary>The variation index which shows the normal, valid appearance.</summary>
    internal const int ValidVariation = 1;

    /// <summary>Get the configured mapping of a sign's qualified item ID to the Alternative Textures texture ID (in the form <c>{Owner}.{ModelName}</c>) providing its appearance variations.</summary>
    private readonly Func<Dictionary<string, string>> GetSignTextureIds;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="getSignTextureIds">Get the configured mapping of a sign's qualified item ID to the Alternative Textures texture ID providing its appearance variations.</param>
    public SignTextureSync(Func<Dictionary<string, string>> getSignTextureIds)
    {
        this.GetSignTextureIds = getSignTextureIds;
    }

    /// <summary>Sync every managed sign's displayed appearance in a location to match its current validity.</summary>
    /// <param name="location">The location to sync.</param>
    /// <param name="data">The location's freshly-rebuilt machine data.</param>
    public void Sync(GameLocation location, MachineDataForLocation data)
    {
        Dictionary<string, string> signTextureIds = this.GetSignTextureIds();
        if (signTextureIds.Count == 0)
            return; // nothing configured — skip entirely, no cost

        this.SyncFrom(location.netObjects.Pairs, data, signTextureIds);
        this.SyncFrom(location.overlayObjects, data, signTextureIds);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Sync every managed sign found in a set of placed objects.</summary>
    /// <param name="pairs">The tile/object pairs to scan.</param>
    /// <param name="data">The location's freshly-rebuilt machine data.</param>
    /// <param name="signTextureIds">The configured mapping of a sign's qualified item ID to its Alternative Textures texture ID.</param>
    private void SyncFrom(IEnumerable<KeyValuePair<Vector2, SObject>> pairs, MachineDataForLocation data, Dictionary<string, string> signTextureIds)
    {
        foreach ((Vector2 tile, SObject signObj) in pairs)
        {
            if (signObj == null || !signTextureIds.TryGetValue(signObj.QualifiedItemId, out string? textureId))
                continue;

            bool isValid = data.SignMarkersByTile.ContainsKey(tile);

            signObj.modData["AlternativeTextureName"] = textureId;
            signObj.modData["AlternativeTextureVariation"] = (isValid ? SignTextureSync.ValidVariation : SignTextureSync.InvalidVariation).ToString();
        }
    }
}

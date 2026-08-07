using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: changed. Swaps a whitelist/blacklist sign's displayed appearance between a "valid" and
/// "invalid" look — per direct user request, no longer via the Alternative Textures mod (see
/// <see cref="Patches.SignValidityPatches"/>'s own remarks for why); this class now just stamps each
/// managed sign's own <see cref="Patches.SignValidityPatches.SignValidModDataKey"/> with its current
/// validity, which that class's draw patch reads directly at render time.
///
/// A sign counts as "invalid" for any reason it wouldn't actually be enforcing its filter: not placed
/// on a connector at all, connected to a group that isn't a valid (active) automation group, or
/// overridden by another sign of the same kind winning the same group's resolution (e.g. a duplicate
/// whitelist sign for the same item, or a non-numeric blacklist overridden by a whitelist elsewhere in
/// the group). All of that is already resolved by <see cref="MachineGroupFactory"/>'s own sign
/// detection into <see cref="MachineDataForLocation.SignMarkersByTile"/> — which folds in a
/// Junimo-touching sign's group too, as long as that specific local group has its own real automation
/// (see <see cref="MachineDataForLocation"/>'s own remarks) — so a sign's presence in that lookup
/// already means exactly "valid," with no extra validity logic needed here.
/// </summary>
internal class SignTextureSync
{
    /*********
    ** Public methods
    *********/
    /// <summary>Sync every managed sign's displayed appearance in a location to match its current validity.</summary>
    /// <param name="location">The location to sync.</param>
    /// <param name="data">The location's freshly-rebuilt machine data.</param>
    public void Sync(GameLocation location, MachineDataForLocation data)
    {
        this.SyncFrom(location.netObjects.Pairs, data);
        this.SyncFrom(location.overlayObjects, data);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Sync every managed sign found in a set of placed objects.</summary>
    /// <param name="pairs">The tile/object pairs to scan.</param>
    /// <param name="data">The location's freshly-rebuilt machine data.</param>
    private void SyncFrom(IEnumerable<KeyValuePair<Vector2, SObject>> pairs, MachineDataForLocation data)
    {
        foreach ((Vector2 tile, SObject signObj) in pairs)
        {
            if (signObj == null || !Patches.SignValidityPatches.IsManagedSign(signObj.QualifiedItemId))
                continue;

            bool isValid = data.SignMarkersByTile.ContainsKey(tile);
            signObj.modData[Patches.SignValidityPatches.SignValidModDataKey] = isValid ? "true" : "false";
        }
    }
}

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Xna.Framework;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>A collection of machines and storage which work as one unit.</summary>
internal interface IMachineGroup
{
    /*********
    ** Accessors
    *********/
    /// <summary>The main location containing the group (as formatted by <see cref="MachineGroupFactory.GetLocationKey"/>), unless this is an aggregate machine group.</summary>
    string? LocationKey { get; }

    /// <summary>The machines in the group.</summary>
    IMachine[] Machines { get; }

    /// <summary>The containers in the group.</summary>
    IContainer[] Containers { get; }

    /// <summary>Whether the machine group is linked to a Junimo chest.</summary>
    [MemberNotNullWhen(false, nameof(IMachineGroup.LocationKey))]
    bool IsJunimoGroup { get; }

    /// <summary>Whether the group has the minimum requirements to enable internal automation (i.e., at least one chest and one machine).</summary>
    bool HasInternalAutomation { get; }


    /*********
    ** Methods
    *********/
    /// <summary>Automate the machines inside the group.</summary>
    void Automate();

    /// <summary>Get the tiles covered by this machine group.</summary>
    /// <param name="locationKey">The location key for which to get tiles.</param>
    IReadOnlySet<Vector2> GetTiles(string locationKey);

    /// <summary>MOD: added. Get the connector role (Both/ChestInputOnly/ChestOutputOnly) for each connector tile covered by this group, keyed by tile position. Only connector tiles appear here — machine and chest tiles don't have a role of their own.</summary>
    /// <param name="locationKey">The location key for which to get tiles.</param>
    IReadOnlyDictionary<Vector2, ConnectorRole> GetConnectorRoles(string locationKey);

    /// <summary>MOD: added. Get a debug marker for each tile with a configured whitelist/blacklist sign directly on top of a connector tile in this group — <c>true</c> for whitelist, <c>false</c> for blacklist. This is set regardless of whether the sign currently has an item on it (unlike the actual item filter), purely so the overlay can show that sign detection itself is working.</summary>
    /// <param name="locationKey">The location key for which to get tiles.</param>
    IReadOnlyDictionary<Vector2, bool> GetSignMarkers(string locationKey);

    /// <summary>MOD: added. Get every tile where a configured whitelist/blacklist sign object exists, regardless of whether it currently holds an item. Broader than <see cref="GetSignMarkers"/> — meant for periodic polling to detect when a previously-empty sign gets an item placed on it.</summary>
    /// <param name="locationKey">The location key for which to get tiles.</param>
    IReadOnlySet<Vector2> GetSignCandidateTiles(string locationKey);
}

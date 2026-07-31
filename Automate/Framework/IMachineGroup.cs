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

    /// <summary>
    /// MOD: added. The same check as <see cref="HasInternalAutomation"/>, but WITHOUT the automatic
    /// "always true" shortcut a Junimo-touching group gets there (since a lone Junimo chest with no
    /// machine nearby can still be functionally activated by another Junimo chest elsewhere on the
    /// farm sharing the same inventory). This reports whether THIS SPECIFIC group has a real machine
    /// and container of its own, regardless of any farm-wide Junimo sharing — meant for the overlay,
    /// to distinguish "this specific touchpoint is doing something" from "this touchpoint is just
    /// along for the shared-inventory ride." For a non-Junimo group this is identical to
    /// <see cref="HasInternalAutomation"/>.
    /// </summary>
    bool HasLocalInternalAutomation { get; }


    /*********
    ** Methods
    *********/
    /// <summary>Automate the machines inside the group.</summary>
    void Automate();

    /// <summary>
    /// MOD: added. Try to push a single machine's output into this group's storage, without touching any
    /// other machine in the group — see <see cref="Patches.MachineReadyPatches"/>'s own remarks for why
    /// each machine is handled independently rather than as part of a shared batch.
    /// </summary>
    /// <param name="machine">The machine to push output from — must already belong to this group.</param>
    /// <returns>Whether the machine ended up empty (ready for new input) as a result.</returns>
    bool TryPushMachineOutput(IMachine machine);

    /// <summary>
    /// MOD: added. Try to feed a single machine fresh input from this group's storage, without touching
    /// any other machine in the group — kept separate from <see cref="TryPushMachineOutput"/> so pushing
    /// and feeding can each be scheduled with their own independent delay (see <see cref="ModEntry.RunOrScheduleDelayedPass"/>)
    /// instead of one instantly chaining into the other.
    /// </summary>
    /// <param name="machine">The machine to feed — must already belong to this group.</param>
    /// <returns>Whether the machine's input was actually changed as a result. MOD: this is the caller's
    /// only reliable way to know whether feeding actually did anything — for a normal machine, comparing
    /// <see cref="IMachine.GetState"/> before/after would give the same answer, but that comparison is
    /// always a false negative for a <see cref="IChestLikeMachine"/> like <see cref="Machines.Objects.PoweredChestMachine"/>,
    /// whose <c>GetState</c> is hardcoded to always report <see cref="MachineState.Empty"/> regardless of
    /// what its own <see cref="IMachine.SetInput"/> call just did.</returns>
    bool TryFeedMachineInput(IMachine machine);

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

    /// <summary>MOD: added. Get the tiles of every machine in this group that's currently "power-starved" (see <see cref="PowerRequiredMachineSystem"/>) — used to drive the wake-up/reminder callouts in <see cref="PowerRequiredMachineSystem.ProcessStarvedMachineCallouts"/>.</summary>
    /// <param name="locationKey">The location key for which to get tiles.</param>
    IReadOnlySet<Vector2> GetPowerStarvedTiles(string locationKey);
}

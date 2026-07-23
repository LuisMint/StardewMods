namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Implemented by machines that store items into their OWN private <see cref="IContainer"/>
/// (e.g. <see cref="Machines.Objects.MiniShippingBinMachine"/>'s own 3x3 inventory) rather than going
/// through the group's shared storage — such a container is never added to
/// <see cref="MachineGroupBuilder"/> as a regular container (see <see cref="MachineGroupBuilder.Add(IContainer)"/>),
/// so it would never otherwise pick up the group's whitelist/blacklist sign filter.
/// </summary>
internal interface IHasFilterableStorage
{
    /// <summary>Apply the group's resolved whitelist/blacklist sign filter to this machine's own private storage.</summary>
    /// <param name="filter">The resolved filter for the machine's group.</param>
    void ApplySignFilter(SignFilter filter);
}

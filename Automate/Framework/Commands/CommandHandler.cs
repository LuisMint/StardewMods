using System;
using Pathoschild.Stardew.Automate.Framework.Models;
using Pathoschild.Stardew.Common.Commands;
using StardewModdingAPI;

namespace Pathoschild.Stardew.Automate.Framework.Commands;

/// <summary>Handles console commands from players.</summary>
internal class CommandHandler : GenericCommandHandler
{
    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="monitor">Writes messages to the console.</param>
    /// <param name="config">The mod configuration.</param>
    /// <param name="machineManager">Manages machine groups.</param>
    /// <param name="powerSiloTierRoller">MOD: added. Resolves <see cref="Models.ModConfig.PowerSiloTierPools"/> into this save's rolled Power Silo tier requirements.</param>
    public CommandHandler(IMonitor monitor, Func<ModConfig> config, MachineManager machineManager, PowerSiloTierRoller powerSiloTierRoller)
        : base("automate", "Automate", CommandHandler.BuildCommands(monitor, config, machineManager, powerSiloTierRoller), monitor) { }


    /*********
    ** Private methods
    *********/
    /// <summary>Build the available commands.</summary>
    /// <param name="monitor">Writes messages to the console.</param>
    /// <param name="config">The mod configuration.</param>
    /// <param name="machineManager">Manages machine groups.</param>
    /// <param name="powerSiloTierRoller">MOD: added. Resolves <see cref="Models.ModConfig.PowerSiloTierPools"/> into this save's rolled Power Silo tier requirements.</param>
    private static ICommand[] BuildCommands(IMonitor monitor, Func<ModConfig> config, MachineManager machineManager, PowerSiloTierRoller powerSiloTierRoller)
    {
        return [
            new ResetCommand(monitor, machineManager),
                new SummaryCommand(monitor, config, machineManager),
                new ResetPowerSiloRollCommand(monitor, powerSiloTierRoller) // MOD: added
        ];
    }
}

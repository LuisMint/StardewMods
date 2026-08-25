using Pathoschild.Stardew.Common.Commands;
using StardewModdingAPI;

namespace Pathoschild.Stardew.Automate.Framework.Commands;

/// <summary>
/// MOD: added. A console command which discards this save's persisted Power Silo tier roll (see
/// <see cref="PowerSiloTierRoller.ResetSavedRoll"/>), so the next read rolls fresh from the current
/// <c>PowerSiloTierPools</c> config instead of replaying whatever was rolled before — meant for
/// iterating on tier pool balance during testing without needing to start a new save each time.
/// </summary>
internal class ResetPowerSiloRollCommand : BaseCommand
{
    /*********
    ** Fields
    *********/
    /// <summary>Resolves <see cref="Models.ModConfig.PowerSiloTierPools"/> into this save's rolled Power Silo tier requirements.</summary>
    private readonly PowerSiloTierRoller PowerSiloTierRoller;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="powerSiloTierRoller">Resolves <see cref="Models.ModConfig.PowerSiloTierPools"/> into this save's rolled Power Silo tier requirements.</param>
    public ResetPowerSiloRollCommand(IMonitor monitor, PowerSiloTierRoller powerSiloTierRoller)
        : base(monitor, "reset_silo_tiers")
    {
        this.PowerSiloTierRoller = powerSiloTierRoller;
    }

    /// <inheritdoc />
    public override string GetDescription()
    {
        return I18n.Command_ResetSiloTiers_Description();
    }

    /// <inheritdoc />
    public override void Handle(string[] args)
    {
        if (!Context.IsWorldReady)
        {
            this.Monitor.Log("You must load a save to use this command.", LogLevel.Error);
            return;
        }

        if (!Context.IsMainPlayer)
        {
            this.Monitor.Log("In multiplayer, the Power Silo tier roll is tracked by the host player's save. Since you're not the host, this command has no effect for you.", LogLevel.Error);
            return;
        }

        this.PowerSiloTierRoller.ResetSavedRoll();
        this.Monitor.Log("Discarded this save's rolled Power Silo tier requirements — they'll be rolled fresh from the current config next time they're read.", LogLevel.Info);
    }
}

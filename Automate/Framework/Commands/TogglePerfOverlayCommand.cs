using System;
using Pathoschild.Stardew.Common.Commands;
using StardewModdingAPI;

namespace Pathoschild.Stardew.Automate.Framework.Commands;

/// <summary>
/// MOD: added. A console command which toggles the automation performance overlay (see
/// <see cref="AutomationPerfTracker"/>) — moved here from a rebindable keybind:
/// a keybind lives in <c>config.json</c>/GMCM's rebind list where a regular player could stumble onto it
/// by accident, while this only ever fires if someone deliberately opens the SMAPI console and types it.
/// </summary>
internal class TogglePerfOverlayCommand : BaseCommand
{
    /*********
    ** Fields
    *********/
    /// <summary>Toggle the automation performance overlay.</summary>
    private readonly Action ToggleOverlay;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="monitor">Encapsulates monitoring and logging.</param>
    /// <param name="toggleOverlay">Toggle the automation performance overlay.</param>
    public TogglePerfOverlayCommand(IMonitor monitor, Action toggleOverlay)
        : base(monitor, "perf")
    {
        this.ToggleOverlay = toggleOverlay;
    }

    /// <inheritdoc />
    public override string GetDescription()
    {
        return
            """
            automate perf
               Usage: automate perf
               Toggles the automation performance overlay on or off. While on, it shows on the HUD and
               dumps a summary to the log when turned back off.
            """;
    }

    /// <inheritdoc />
    public override void Handle(string[] args)
    {
        this.ToggleOverlay();
    }
}

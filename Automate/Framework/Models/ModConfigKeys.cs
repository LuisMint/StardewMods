using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using Pathoschild.Stardew.Common;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace Pathoschild.Stardew.Automate.Framework.Models;

/// <summary>A set of parsed key bindings.</summary>
internal class ModConfigKeys
{
    /*********
    ** Accessors
    *********/
    /// <summary>The keys which toggle the automation overlay.</summary>
    public KeybindList ToggleOverlay { get; set; } = new(SButton.U);

    /// <summary>MOD: added. The keys which toggle the automation performance overlay (see <see cref="AutomationPerfTracker"/>).</summary>
    public KeybindList TogglePerformanceOverlay { get; set; } = new(SButton.P);


    /*********
    ** Public methods
    *********/
    /// <summary>Normalize the model after it's deserialized.</summary>
    /// <param name="context">The deserialization context.</param>
    [OnDeserialized]
    [SuppressMessage("ReSharper", "NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract", Justification = SuppressReasons.MethodValidatesNullability)]
    [SuppressMessage("ReSharper", "UnusedMember.Global", Justification = SuppressReasons.UsedViaOnDeserialized)]
    public void OnDeserialized(StreamingContext context)
    {
        this.ToggleOverlay ??= new KeybindList();
        this.TogglePerformanceOverlay ??= new KeybindList();
    }
}

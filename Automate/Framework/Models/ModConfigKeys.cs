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

    /// <summary>MOD: added. The keys which toggle the automation performance overlay (see <see cref="AutomationPerfTracker"/>). MOD: changed default from a bare P to a Ctrl+Shift+P combo, per direct user request, so a regular player isn't likely to stumble onto it by accident.</summary>
    public KeybindList TogglePerformanceOverlay { get; set; } = KeybindList.Parse("LeftControl+LeftShift+P");


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

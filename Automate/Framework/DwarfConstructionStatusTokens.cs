using System;
using System.Collections.Generic;
using Pathoschild.Stardew.Automate.Framework.Patches;
using StardewModdingAPI;
using StardewValley.Buildings;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Registers two Content Patcher custom tokens exposing the current Dwarf-built structure's
/// construction status — consumed by PoweredAutomation's own content pack (see
/// <c>PoweredAutomation/Data/UiInfoSuite2AltIcons.json</c>) to show a "days remaining" icon via UI Info
/// Suite 2 Alternative's own documented custom-icon feature
/// (https://github.com/dazuki/UIInfoSuite2Alt/blob/main/docs/custom-icons.md) — a real, supported
/// integration point, unlike the original UI Info Suite 2, which was confirmed (by decompiling it — no
/// <c>GetApi()</c> override anywhere in that mod) to have no API for other mods at all.
///
/// Content Patcher itself is the actual integration surface here, not UI Info Suite 2 Alternative
/// directly — PoweredAutomation just edits UIInfoSuite2Alt's own <c>Mods/DazUki.UIInfoSuite2Alt/CustomIcons</c>
/// asset via a normal Content Patcher patch, and Content Patcher's own custom-token system (see
/// <see cref="IContentPatcherApi.RegisterToken"/>) is what lets that patch's <c>HoverText</c> field show
/// a LIVE, updating value instead of static text — the same public, documented mechanism several other
/// installed mods already use for their own custom tokens (Cauldron, Unlockable Bundles, Secret Note
/// Framework, etc.), re-evaluated at the start of each day by default (matching how often the
/// days-remaining count itself actually changes, so no extra <c>UpdateRate</c> is needed).
/// </summary>
internal static class DwarfConstructionStatusTokens
{
    /*********
    ** Public methods
    *********/
    /// <summary>Register this class's Content Patcher tokens. A no-op if Content Patcher's own API isn't available for some reason (shouldn't be possible in practice, since it's a hard dependency of this whole mod).</summary>
    /// <param name="modRegistry">The mod registry to fetch Content Patcher's own API through.</param>
    /// <param name="manifest">This mod's own manifest, used to scope the registered tokens under its own mod ID (so the full token names other content packs reference are <c>{{luisMint.Automate/HasActiveDwarfConstruction}}</c> and <c>{{luisMint.Automate/DwarfConstructionHoverText}}</c>).</param>
    public static void Apply(IModRegistry modRegistry, IManifest manifest)
    {
        if (modRegistry.GetApi<IContentPatcherApi>("Pathoschild.ContentPatcher") is not { } contentPatcherApi)
            return;

        // MOD: added — a plain boolean token, used purely to gate the CustomIcons entry's own "When"
        // condition (see UiInfoSuite2AltIcons.json) so the icon only shows up while a Dwarf structure is
        // actually under construction. Kept separate from the hover-text token below for clarity — one
        // token answers "should this even be visible", the other answers "what should it say".
        contentPatcherApi.RegisterToken(manifest, "HasActiveDwarfConstruction", static () =>
            new[] { DwarfBuildMenuPatches.FindDwarfStructureUnderConstruction() != null ? "true" : "false" });

        contentPatcherApi.RegisterToken(manifest, "DwarfConstructionHoverText", static () =>
        {
            if (DwarfConstructionStatusTokens.GetStatus() is not { } status)
                return null; // no active construction — the icon's own "When" condition (see above) already hides it in this case, so an empty/unready token here is harmless

            return new[] { I18n.Message_DwarfConstructionHoverText(structureName: status.StructureName, days: status.DaysRemaining) };
        });
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Get the current Dwarf-built structure's own display name and days remaining, or <c>null</c> if none is currently under construction.</summary>
    private static (string StructureName, int DaysRemaining)? GetStatus()
    {
        if (DwarfBuildMenuPatches.FindDwarfStructureUnderConstruction() is not { } building)
            return null;

        // MOD: mirrors DwarfConstructionSpritePatches' own "which field is actually counting down"
        // resolution exactly — a building always has EXACTLY ONE of these two fields active at a time
        // (construction vs. a later upgrade), never both.
        int daysRemaining = building.daysOfConstructionLeft.Value > 0
            ? building.daysOfConstructionLeft.Value
            : building.daysUntilUpgrade.Value;

        string structureName = building.GetData()?.Name ?? building.buildingType.Value;

        return (structureName, daysRemaining);
    }
}

/// <summary>
/// MOD: added. A minimal local copy of Content Patcher's own public API surface — just the one member
/// this mod actually calls (<see cref="RegisterToken"/>). SMAPI's <see cref="IModRegistry.GetApi{TInterface}"/>
/// only needs the requested interface to be structurally compatible with the real mod's API object (it
/// generates a proxy at runtime), so a local copy doesn't need to declare every member the real
/// <c>IContentPatcherAPI</c> interface has — only the ones actually used here.
///
/// MOD: fixed — must be <c>public</c>, not <c>internal</c> (unlike the rest of this codebase's own
/// convention): SMAPI's proxy generation for <see cref="IModRegistry.GetApi{TInterface}"/> refuses a
/// non-public interface outright ("Tried to map a mod-provided API to non-public interface... must be a
/// public interface"), confirmed by testing in-game.
/// </summary>
public interface IContentPatcherApi
{
    /// <summary>Register a custom token with no input arguments.</summary>
    /// <param name="mod">The manifest of the mod defining the token.</param>
    /// <param name="name">The token name. This must be alphanumeric/underscore characters only. This shouldn't be prefixed with the mod ID.</param>
    /// <param name="getValue">A function which returns the token value, or <c>null</c> if the token isn't currently available (e.g. because it's context-sensitive and the current context isn't valid).</param>
    void RegisterToken(IManifest mod, string name, Func<IEnumerable<string>?> getValue);
}

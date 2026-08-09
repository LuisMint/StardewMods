using System.Linq;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Shared Secret Note Framework note IDs and read-check helper for the Dwarf Research
/// Note-gated secrets, used by <see cref="Patches.DwarfNoteTreasureTilePatches"/>,
/// <see cref="Patches.DwarfKingStatueTradePatches"/>, and
/// <see cref="Patches.DwarfConstructionSiteInteractionPatches"/>'s note #7 diamond guarantee.
/// </summary>
internal static class DwarfResearchNotes
{
    /*********
    ** Fields
    *********/
    /// <summary>Dwarf Research Note #1 ("Input Conduit")'s ID, matching its key in the <c>Mods/ichortower.SecretNoteFramework/Notes</c> asset.</summary>
    public const string InputConduitNoteId = "luisMint.PoweredAutomation_DwarfNote_InputConduit";

    /// <summary>Dwarf Research Note #2 ("Output Conduit")'s ID, matching its key in the <c>Mods/ichortower.SecretNoteFramework/Notes</c> asset.</summary>
    public const string OutputConduitNoteId = "luisMint.PoweredAutomation_DwarfNote_OutputConduit";

    /// <summary>Dwarf Research Note #3 ("Omni Conduit")'s ID, matching its key in the <c>Mods/ichortower.SecretNoteFramework/Notes</c> asset.</summary>
    public const string OmniConduitNoteId = "luisMint.PoweredAutomation_DwarfNote_OmniConduit";

    /// <summary>Dwarf Research Note #4 ("Powered Chest - Power Coil")'s ID, matching its key in the <c>Mods/ichortower.SecretNoteFramework/Notes</c> asset.</summary>
    public const string PoweredSystemsNoteId = "luisMint.PoweredAutomation_DwarfNote_PoweredSystems";

    /// <summary>Dwarf Research Note #7 ("White/Black List Category Sign")'s ID, matching its key in the <c>Mods/ichortower.SecretNoteFramework/Notes</c> asset.</summary>
    public const string CategorySignNoteId = "luisMint.PoweredAutomation_DwarfNote_CategorySign";

    /// <summary>The <see cref="Farmer.modData"/> key Secret Note Framework itself uses to track which notes a player has read, read directly since its own public API (<c>ichortower.SNF.API</c>) doesn't expose a read-check method.</summary>
    private const string SecretNoteFrameworkSeenNotesModDataKey = "ichortower.SecretNoteFramework/NotesSeen";


    /*********
    ** Public methods
    *********/
    /// <summary>Get whether the given player has read a Secret Note Framework note.</summary>
    /// <param name="who">The player to check.</param>
    /// <param name="noteId">The full note ID (matching its key in the <c>Mods/ichortower.SecretNoteFramework/Notes</c> asset).</param>
    public static bool HasRead(Farmer who, string noteId)
    {
        if (!who.modData.TryGetValue(DwarfResearchNotes.SecretNoteFrameworkSeenNotesModDataKey, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        // matches SecretNoteFramework's own storage format exactly: a JSON-like array of quoted IDs, e.g. ["id1","id2"]
        return raw
            .Trim('[', ']')
            .Split(',')
            .Select(s => s.Trim().Trim('"'))
            .Contains(noteId);
    }
}

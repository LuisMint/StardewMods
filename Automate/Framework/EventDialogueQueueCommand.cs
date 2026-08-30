using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Registers a custom event-script command that pushes a single line onto an NPC's dialogue
/// queue without displaying it immediately, so it's the next thing they say once the player talks to
/// them normally — the exact same underlying mechanism vanilla's own <c>end dialogue &lt;NPC&gt;
/// "&lt;text&gt;"</c> end-behavior uses (see <c>Event.endBehaviors</c>'s own <c>"dialogue"</c> case in
/// the decompiled game source), just exposed as an ordinary mid-script command instead of only once per
/// event via <c>end</c>. Vanilla's own <c>end dialogue</c> only supports a single NPC per event; this
/// lets one event queue a different one-off "after the cutscene" line for as many NPCs as it wants
/// (including NPCs who never appeared in the event at all — see <see cref="QueueDialogue"/>'s own
/// remarks), just by calling this command once per NPC before the event's own <c>end</c>.
/// </summary>
internal static class EventDialogueQueueCommand
{
    /*********
    ** Fields
    *********/
    /// <summary>The event command name, matching the naming convention <see cref="Patches.CrankedPowerCoilPatches"/>'s own custom command uses.</summary>
    private const string CommandName = "luisMint.PoweredAutomation_QueueDialogue";


    /*********
    ** Public methods
    *********/
    /// <summary>Register the command. Not a Harmony patch — just registers with vanilla's own event-command extension point, same as <see cref="Patches.CrankedPowerCoilPatches"/>'s own custom command.</summary>
    public static void Apply()
    {
        Event.RegisterCommand(EventDialogueQueueCommand.CommandName, EventDialogueQueueCommand.QueueDialogue);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// The <see cref="CommandName"/> event command's own handler. Syntax: <c>&lt;NPC&gt;
    /// "&lt;text&gt;"</c>.
    ///
    /// The target NPC doesn't need to be one of the event's own actors — this just looks them up by name
    /// via <see cref="Game1.getCharacterFromName(string, bool, bool)"/> the same way vanilla's <c>speak</c>
    /// command does, so it works equally well for an NPC who's off in their usual daily spot somewhere
    /// else on the map while this event plays out.
    /// </summary>
    /// <param name="event">The event running this command.</param>
    /// <param name="args">The command's own arguments — see this method's own remarks for the syntax.</param>
    /// <param name="context">The context for the active event.</param>
    private static void QueueDialogue(Event @event, string[] args, EventContext context)
    {
        if (!ArgUtility.TryGet(args, 1, out string npcName, out string error, allowBlank: false) || !ArgUtility.TryGet(args, 2, out string dialogueText, out error))
        {
            context.LogErrorAndSkip(error);
            return;
        }

        NPC npc = Game1.getCharacterFromName(npcName);
        if (npc != null)
        {
            // MOD: mirrors Event.endBehaviors' own "dialogue" case exactly (decompiled game source), so
            // this behaves identically to vanilla's single-NPC end-of-event line — just callable more
            // than once per event, and not tied to the event actually ending right after.
            npc.shouldSayMarriageDialogue.Value = false;
            npc.currentMarriageDialogue.Clear();
            npc.CurrentDialogue.Clear();
            npc.CurrentDialogue.Push(new Dialogue(npc, null, dialogueText));
        }

        @event.CurrentCommand++;
    }
}

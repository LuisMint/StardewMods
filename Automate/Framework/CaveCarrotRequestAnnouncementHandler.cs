using StardewModdingAPI;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Queues a one-time reaction line from Mayor Lewis, the next time the player talks to him
/// after accepting the Cave Carrot Request special order (see
/// PoweredAutomation/Data/SpecialOrdersData.json) — checked once per day rather than the instant the
/// order's accepted, since there's no clean "order was just accepted" hook to react to immediately.
/// Uses the same "push onto CurrentDialogue" mechanism as <see cref="EventDialogueQueueCommand"/>, just
/// triggered from a day-started check instead of from within an event script.
/// </summary>
internal static class CaveCarrotRequestAnnouncementHandler
{
    /*********
    ** Fields
    *********/
    /// <summary>The special order key to watch for.</summary>
    private const string OrderKey = "luisMint.PoweredAutomation_CaveCarrotRequest";

    /// <summary>The mail flag marking that this reaction has already been queued, so it only ever fires once.</summary>
    private const string AnnouncedMailFlag = "luisMint.PoweredAutomation_CaveCarrotRequestAnnounced";

    /// <summary>The NPC who reacts to the order.</summary>
    private const string AnnouncingNpcName = "Lewis";


    /*********
    ** Public methods
    *********/
    /// <summary>Hook this into the game. Not a Harmony patch — just an ordinary day-started check.</summary>
    /// <param name="helper">The mod helper to hook events through.</param>
    public static void Apply(IModHelper helper)
    {
        helper.Events.GameLoop.DayStarted += CaveCarrotRequestAnnouncementHandler.OnDayStarted;
    }


    /*********
    ** Private methods
    *********/
    /// <inheritdoc cref="StardewModdingAPI.Events.IGameLoopEvents.DayStarted" />
    private static void OnDayStarted(object? sender, StardewModdingAPI.Events.DayStartedEventArgs e)
    {
        if (Game1.player.mailReceived.Contains(CaveCarrotRequestAnnouncementHandler.AnnouncedMailFlag))
            return;

        if (!Game1.player.team.SpecialOrderActive(CaveCarrotRequestAnnouncementHandler.OrderKey))
            return;

        Game1.player.mailReceived.Add(CaveCarrotRequestAnnouncementHandler.AnnouncedMailFlag);

        NPC? npc = Game1.getCharacterFromName(CaveCarrotRequestAnnouncementHandler.AnnouncingNpcName);
        if (npc == null)
            return;

        // MOD: mirrors EventDialogueQueueCommand.QueueDialogue exactly (see its own remarks) — the same
        // underlying mechanism vanilla's own "end dialogue <NPC> "<text>"" event end-behavior uses.
        npc.shouldSayMarriageDialogue.Value = false;
        npc.currentMarriageDialogue.Clear();
        npc.CurrentDialogue.Clear();
        npc.CurrentDialogue.Push(new Dialogue(npc, null, I18n.Dialogue_LewisCaveCarrotRequestReaction()));
    }
}

using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. Registers a custom event-script command that draws a purely decorative, non-interactive
/// prop using a mod's own custom texture — for cutscene set-dressing that shouldn't be a real item at
/// all.
///
/// Vanilla's own <c>addBigProp</c> creates a genuine <c>(BC)</c>-qualified <see cref="Object"/> (see
/// <c>Event.DefaultCommands.AddBigProp</c> in the decompiled game source), which means it has to be a
/// real registered <c>Data/BigCraftables</c> entry — and that in turn means it shows up in any mod that
/// enumerates all known items (item-spawner/cheat menus, lookup tools), and if the player ever gets a
/// copy of it another way, it can be placed in the world like a normal machine. Vanilla's own
/// <c>addProp</c>/<c>addFloorProp</c> avoids that (it draws from <see cref="Event.festivalProps"/>,
/// which needs no backing item at all) but is hardcoded to only ever read from the vanilla
/// <c>Maps\Festivals</c> spritesheet (<c>Event.festivalTextureName</c>) — there's no event-script way to
/// point it at a mod's own texture.
///
/// This command bridges the two: it builds a <see cref="Prop"/> as <c>addProp</c> does — for depth
/// sorting, collision, and automatic cleanup once the event ends (a <see cref="Event"/> instance's own
/// <see cref="Event.festivalProps"/> list just goes away with it, exactly like <c>addProp</c>'s own
/// props) — but backed by any texture asset name given in the script, not the fixed festival one. The
/// prop drawn this way was never a real <see cref="Item"/>, so it can't appear in an item spawner, can't
/// be picked up, and can't be placed in the world.
/// </summary>
internal static class EventCustomPropCommand
{
    /*********
    ** Fields
    *********/
    /// <summary>The event command name, matching the naming convention <see cref="Patches.CrankedPowerCoilPatches"/>'s own custom command uses.</summary>
    private const string CommandName = "luisMint.PoweredAutomation_AddCustomProp";


    /*********
    ** Public methods
    *********/
    /// <summary>Register the command. Not a Harmony patch — just registers with vanilla's own event-command extension point, same as <see cref="Patches.CrankedPowerCoilPatches"/>'s own custom command.</summary>
    public static void Apply()
    {
        Event.RegisterCommand(EventCustomPropCommand.CommandName, EventCustomPropCommand.AddCustomProp);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>
    /// The <see cref="CommandName"/> event command's own handler. Syntax: <c>&lt;textureAssetName&gt;
    /// &lt;x&gt; &lt;y&gt; [tilesWide] [tilesHighDraw] [tilesHighSolid]</c>. The trailing three are
    /// optional and default to <c>1 2 1</c> — a single-column sprite two tiles tall, whose bottom tile is
    /// the solid/footprint tile (matching a normal 16x32 single-sprite texture, like this mod's own
    /// broken-crank textures). See <see cref="Prop"/>'s own constructor remarks (decompiled game source)
    /// for exactly what each of those three numbers controls.
    /// </summary>
    /// <param name="event">The event running this command.</param>
    /// <param name="args">The command's own arguments — see this method's own remarks for the syntax.</param>
    /// <param name="context">The context for the active event.</param>
    private static void AddCustomProp(Event @event, string[] args, EventContext context)
    {
        if (!ArgUtility.TryGet(args, 1, out string textureAssetName, out string error, allowBlank: false)
            || !ArgUtility.TryGetInt(args, 2, out int x, out error)
            || !ArgUtility.TryGetInt(args, 3, out int y, out error)
            || !ArgUtility.TryGetOptionalInt(args, 4, out int tilesWide, out error, 1)
            || !ArgUtility.TryGetOptionalInt(args, 5, out int tilesHighDraw, out error, 2)
            || !ArgUtility.TryGetOptionalInt(args, 6, out int tilesHighSolid, out error, 1))
        {
            context.LogErrorAndSkip(error);
            return;
        }

        Texture2D texture = Game1.content.Load<Texture2D>(textureAssetName);
        @event.festivalProps.Add(new Prop(texture, 0, tilesWide, tilesHighSolid, tilesHighDraw, x, y));

        @event.CurrentCommand++;
    }
}

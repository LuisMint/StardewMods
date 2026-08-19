using Microsoft.Xna.Framework;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added. A minimal marker so a configured whitelist/blacklist sign is recognized by Automate's
/// existing "was something automatable placed/removed nearby?" change-tracking (in ModEntry's
/// OnObjectListChanged → ReloadIfNeeded), without being treated as a machine, container, or
/// connector anywhere else. The actual functional sign detection (reading whitelist/blacklist
/// config, reading the displayed item) continues to happen separately in
/// <see cref="MachineGroupFactory.GetSignInfo"/> — this type exists purely so PLACING or REMOVING a
/// sign is noticed by the same mechanism that already notices placing/removing a machine or path,
/// instead of requiring a periodic poll or an unrelated nearby change to trigger the first scan.
/// </summary>
internal class SignPlaceholder : IAutomatable
{
    /*********
    ** Accessors
    *********/
    /// <summary>The location which contains the sign.</summary>
    public GameLocation Location { get; }

    /// <summary>The tile area covered by the sign.</summary>
    public Rectangle TileArea { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="location">The location which contains the sign.</param>
    /// <param name="tile">The tile covered by the sign.</param>
    public SignPlaceholder(GameLocation location, Vector2 tile){
        this.Location = location;
        this.TileArea = new Rectangle((int)tile.X, (int)tile.Y, 1, 1);
    }
}

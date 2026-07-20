using Microsoft.Xna.Framework;
using StardewValley;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>An entity which connects machines and chests in a machine group, but otherwise has no logic of its own.</summary>
internal class Connector : IAutomatable
{
    /*********
    ** Accessors
    *********/
    /// <summary>The location which contains the machine.</summary>
    public GameLocation Location { get; }

    /// <summary>The tile area covered by the machine.</summary>
    public Rectangle TileArea { get; }

    /// <summary>MOD: added. The role this connector plays for containers it touches (e.g. whether a touching chest is restricted to input-only or output-only through this specific connector).</summary>
    public ConnectorRole Role { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="location">The location which contains the machine.</param>
    /// <param name="tileArea">The tile area covered by the machine.</param>
    /// <param name="role">MOD: added. The role this connector plays for containers it touches.</param>
    public Connector(GameLocation location, Rectangle tileArea, ConnectorRole role = ConnectorRole.Both)
    {
        this.Location = location;
        this.TileArea = tileArea;
        this.Role = role;
    }

    /// <summary>Construct an instance.</summary>
    /// <param name="location">The location which contains the machine.</param>
    /// <param name="tile">The tile covered by the machine.</param>
    /// <param name="role">MOD: added. The role this connector plays for containers it touches.</param>
    public Connector(GameLocation location, Vector2 tile, ConnectorRole role = ConnectorRole.Both)
        : this(location, new Rectangle((int)tile.X, (int)tile.Y, 1, 1), role) { }
}

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>MOD: added. The role a connector plays for the containers it touches.</summary>
internal enum ConnectorRole
{
    /// <summary>The connector allows both taking items from and storing items into touching containers — the original, default behavior.</summary>
    Both,

    /// <summary>The connector only allows TAKING items from touching containers. Items are never stored into a chest through this connector — the chest acts purely as an input/source.</summary>
    ChestInputOnly,

    /// <summary>The connector only allows STORING items into touching containers. Items are never taken from a chest through this connector — the chest acts purely as an output/destination.</summary>
    ChestOutputOnly
}

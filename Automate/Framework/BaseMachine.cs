using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>The base implementation for a machine.</summary>
internal abstract class BaseMachine : IMachine
{
    /*********
    ** Accessors
    *********/
    /// <summary>A unique ID for the machine type.</summary>
    /// <remarks>This value should be identical for two machines if they have the exact same behavior and input logic. For example, if one machine in a group can't process input due to missing items, Automate will skip any other empty machines of that type in the same group since it assumes they need the same inputs.</remarks>
    public string MachineTypeID { get; protected set; }

    /// <summary>The location which contains the machine.</summary>
    public GameLocation Location { get; }

    /// <summary>The tile area covered by the machine.</summary>
    public Rectangle TileArea { get; }


    /*********
    ** Public methods
    *********/
    /// <summary>Get the machine's processing state.</summary>
    public abstract MachineState GetState();

    /// <summary>Get the output item.</summary>
    public abstract ITrackedStack? GetOutput();

    /// <summary>Provide input to the machine.</summary>
    /// <param name="input">The available items.</param>
    /// <returns>Returns whether the machine started processing an item.</returns>
    public abstract bool SetInput(IStorage input);

    /// <summary>Get the default ID for an Automate machine type.</summary>
    /// <param name="machineType">The machine type.</param>
    public static string GetDefaultMachineId(Type machineType)
    {
        string id = machineType.Name;
        if (id.EndsWith("Machine"))
            id = id.Substring(0, id.Length - "Machine".Length);

        return id;
    }

    /// <summary>
    /// MOD: added. Every currently-installed mod's own UniqueID (content packs included), longest-first
    /// so a more specific nested ID matches before a shorter one that's also a prefix of it — set once via
    /// <see cref="SetKnownModIdPrefixes"/> during startup, before any machine is constructed.
    /// </summary>
    private static IReadOnlyList<string> KnownModIdPrefixes = [];

    /// <summary>MOD: added. Caches <see cref="StripKnownModIdPrefix"/>'s own result per raw name — cleared whenever <see cref="SetKnownModIdPrefixes"/> is called.</summary>
    private static readonly Dictionary<string, string> StrippedNameCache = new();

    /// <summary>
    /// MOD: added. Provide every currently-installed mod's own UniqueID, used to generically strip a
    /// leading <c>"{UniqueID}_"</c> prefix from a machine's raw internal name before
    /// <see cref="GetDefaultMachineId(string)"/> deletes its non-alphanumeric characters — so an object
    /// namespaced the conventional way (e.g. <c>"{ModId}_ElectricFurnace"</c>) resolves to the short type
    /// ID (<c>"ElectricFurnace"</c>) a config author would intuitively expect and type into
    /// <see cref="Models.ModConfig.PowerRequiredMachineNames"/>/<see cref="Models.ModConfig.MachineOverrides"/>,
    /// regardless of which mod added the object — not just this mod's own content pack. Must be called once
    /// during startup, before any machine is constructed (every <see cref="GetDefaultMachineId(string)"/>
    /// caller shares this same stripping, so calling it late would leave already-constructed machines with
    /// a stale, unstripped <see cref="IMachine.MachineTypeID"/>).
    /// </summary>
    /// <param name="modIds">Every currently-installed mod's own UniqueID.</param>
    public static void SetKnownModIdPrefixes(IEnumerable<string> modIds)
    {
        BaseMachine.KnownModIdPrefixes = modIds.Where(id => !string.IsNullOrWhiteSpace(id)).OrderByDescending(id => id.Length).ToArray();
        BaseMachine.StrippedNameCache.Clear();
    }

    /// <summary>Get th default ID for a machine instance's internal name.</summary>
    /// <param name="name">The machine's internal item.</param>
    public static string GetDefaultMachineId(string name)
    {
        name = BaseMachine.StripKnownModIdPrefix(name);
        return new string(name.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>MOD: added. Strip a leading <c>"{UniqueID}_"</c> prefix matching a known installed mod's own ID from a machine's raw internal name, if any — see <see cref="SetKnownModIdPrefixes"/>'s own remarks. A safe no-op for a vanilla machine, or one whose name doesn't start with any known mod's own ID.</summary>
    /// <param name="name">The machine's raw internal name.</param>
    private static string StripKnownModIdPrefix(string name)
    {
        if (BaseMachine.StrippedNameCache.TryGetValue(name, out string? cached))
            return cached;

        string result = name;
        foreach (string modId in BaseMachine.KnownModIdPrefixes)
        {
            string prefix = modId + "_";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = name.Substring(prefix.Length);
                break;
            }
        }

        BaseMachine.StrippedNameCache[name] = result;
        return result;
    }

    /// <summary>Get the default ID for an Automate machine type.</summary>
    /// <typeparam name="TMachine">The machine type.</typeparam>
    public static string GetDefaultMachineId<TMachine>()
        where TMachine : IMachine
    {
        return BaseMachine.GetDefaultMachineId(typeof(TMachine));
    }

    /// <summary>Get the tile area for a building.</summary>
    /// <param name="building">The building.</param>
    public static Rectangle GetTileAreaFor(Building building)
    {
        return new Rectangle(building.tileX.Value, building.tileY.Value, building.tilesWide.Value, building.tilesHigh.Value);
    }

    /// <summary>Get the tile area covered by a bush.</summary>
    /// <param name="bush">The bush whose area to get.</param>
    public static Rectangle GetTileAreaFor(LargeTerrainFeature bush)
    {
        Rectangle box = bush.getBoundingBox();
        return new Rectangle(
            x: box.X / Game1.tileSize,
            y: box.Y / Game1.tileSize,
            width: box.Width / Game1.tileSize,
            height: box.Height / Game1.tileSize
        );
    }


    /*********
    ** Protected methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="location">The machine's in-game location.</param>
    /// <param name="tileArea">The tile area covered by the machine.</param>
    /// <param name="machineTypeId">A unique ID for the machine type, or <c>null</c> to generate it from the type name.</param>
    protected BaseMachine(GameLocation location, in Rectangle tileArea, string? machineTypeId = null)
    {
        this.MachineTypeID = machineTypeId ?? this.GetDefaultMachineId();
        this.Location = location;
        this.TileArea = tileArea;
    }

    /// <summary>Get the tile area for a placed object.</summary>
    /// <param name="tile">The tile position.</param>
    protected static Rectangle GetTileAreaFor(in Vector2 tile)
    {
        return new Rectangle((int)tile.X, (int)tile.Y, 1, 1);
    }

    /// <summary>Get a tracked stack for an item, if it's not null.</summary>
    /// <param name="item">The item to track.</param>
    /// <param name="onReduced">The callback invoked when the stack size is reduced (including reduced to zero).</param>
    /// <param name="onEmpty">The callback invoked when the stack is empty.</param>
    protected ITrackedStack? GetTracked(Item? item, Action<TrackedItem, Item>? onReduced = null, Action<TrackedItem, Item>? onEmpty = null)
    {
        return item != null
            ? new TrackedItem(item).OnReduced(onReduced).OnEmpty(onEmpty)
            : null;
    }

    /// <summary>Get the default ID for the machine type.</summary>
    private string GetDefaultMachineId()
    {
        return BaseMachine.GetDefaultMachineId(this.GetType());
    }
}

/// <summary>The base implementation for a machine.</summary>
internal abstract class BaseMachine<TMachine> : BaseMachine
{
    /*********
    ** Fields
    *********/
    /// <summary>The underlying entity automated by this machine. This is only stored for the machine instance, and can be null if not applicable.</summary>
    protected TMachine Machine { get; }


    /*********
    ** Protected methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="machine">The underlying entity automated by this machine. This is only stored for the machine instance, and can be null if not applicable.</param>
    /// <param name="location">The machine's in-game location.</param>
    /// <param name="tileArea">The tile area covered by the machine.</param>
    /// <param name="machineTypeId">A unique ID for the machine type, or <c>null</c> to generate it from the type name.</param>
    protected BaseMachine(TMachine machine, GameLocation location, in Rectangle tileArea, string? machineTypeId = null)
        : base(location, tileArea, machineTypeId)
    {
        this.Machine = machine;
    }
}

using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Tools;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Per direct user request, reading Dwarf Research Note #4 ("Powered Chest - Power
/// Coil") unlocks a one-time hidden Power Coil at a specific Mine level 120 tile: hitting tile
/// (3,8) with a Hoe, Pickaxe, or Axe drops a Power Coil there, once ever for the whole save (not
/// per player) — whoever finds it first after reading the note claims it, and it's gone for
/// everyone else after that.
///
/// A POSTFIX on the shared <see cref="Tool.DoFunction"/> base method, not
/// <see cref="GameLocation.performToolAction"/>: <see cref="Hoe.DoFunction"/> never calls
/// performToolAction for a plain non-tillable tile (it only checks terrain features/objects/the
/// Diggable tile property directly), so patching performToolAction alone would silently miss hoe
/// swings entirely. Tool.DoFunction is the one method all three tools funnel through, so a single
/// postfix here covers all three uniformly. It's a postfix rather than a prefix because letting the
/// original run first is harmless — none of the three tools do anything to a bare, non-tillable
/// mine floor tile on their own — this only ever adds behavior on top, never needs to suppress
/// vanilla's own handling.
/// </summary>
internal static class DwarfNoteTreasureTilePatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The mine level the hidden tile is on.</summary>
    private const int TreasureMineLevel = 120;

    /// <summary>The tile the hidden Power Coil is buried under.</summary>
    private static readonly Point TreasureTile = new(3, 8);

    /// <summary>The qualified item ID the tile drops.</summary>
    private const string RewardItemId = "(BC)luisMint.AutomatePowerPipes_PowerCoil";

    /// <summary>The <see cref="Farmer.modData"/> key on <see cref="Game1.MasterPlayer"/> tracking whether the reward has already been claimed. Deliberately global (not per-player), since it's a one-time find for the whole save, not one per player.</summary>
    private const string ClaimedModDataKey = "luisMint.AutomatePowerPipes/DwarfNoteTreasureTileClaimed";


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(Tool), nameof(Tool.DoFunction)),
            postfix: new HarmonyMethod(typeof(DwarfNoteTreasureTilePatches), nameof(DoFunction_Postfix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Drop a Power Coil the first time the hidden tile is struck with a Hoe, Pickaxe, or Axe, after Dwarf Research Note #4 has been read.</summary>
    /// <param name="__instance">The tool being used.</param>
    /// <param name="location">The location the tool was used in.</param>
    /// <param name="x">The target pixel X position.</param>
    /// <param name="y">The target pixel Y position.</param>
    /// <param name="who">The player using the tool.</param>
    private static void DoFunction_Postfix(Tool __instance, GameLocation location, int x, int y, Farmer who)
    {
        if (__instance is not (Hoe or Pickaxe or Axe))
            return;

        if (location is not MineShaft { mineLevel: DwarfNoteTreasureTilePatches.TreasureMineLevel })
            return;

        int tileX = x / 64;
        int tileY = y / 64;
        if (tileX != DwarfNoteTreasureTilePatches.TreasureTile.X || tileY != DwarfNoteTreasureTilePatches.TreasureTile.Y)
            return;

        if (DwarfNoteTreasureTilePatches.HasClaimedGlobally())
            return;

        if (!DwarfResearchNotes.HasRead(who, DwarfResearchNotes.PoweredSystemsNoteId))
            return;

        DwarfNoteTreasureTilePatches.MarkClaimedGlobally();

        Game1.createItemDebris(
            item: ItemRegistry.Create(DwarfNoteTreasureTilePatches.RewardItemId),
            pixelOrigin: new Vector2(tileX * 64 + 32, tileY * 64 + 32),
            direction: 2,
            location: location
        );
        location.playSound("give_gift"); // MOD: matches the sound DwarfConstructionSiteInteractionPatches already uses for a surprise item drop
    }

    /// <summary>Get whether the hidden tile's reward has already been claimed, for the whole save.</summary>
    private static bool HasClaimedGlobally()
    {
        return Game1.MasterPlayer.modData.ContainsKey(DwarfNoteTreasureTilePatches.ClaimedModDataKey);
    }

    /// <summary>Mark the hidden tile's reward as claimed, for the whole save.</summary>
    private static void MarkClaimedGlobally()
    {
        Game1.MasterPlayer.modData[DwarfNoteTreasureTilePatches.ClaimedModDataKey] = "true";
    }
}

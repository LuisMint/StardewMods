using HarmonyLib;
using StardewValley;
using StardewValley.Objects;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Replaces the loot a Barrel/Crate gives when broken inside a Cave Hole (or Big Cave Hole)
/// interior — these are purely decorative first-day flavor (see
/// <see cref="CaveHoleQuarrySystem.SpawnCrateOrBarrel"/>), so they should only ever give 1-3 Cave Carrots
/// or nothing, not vanilla's own <see cref="BreakableContainer.releaseContents"/> mine loot table (ore,
/// coal, gems, mystery boxes, etc.).
///
/// <see cref="BreakableContainer.releaseContents"/> isn't virtual, and its own fields (health, hit
/// sounds, debris) are all private, so overriding it via a subclass isn't an option — everything else
/// about the vanilla Barrel/Crate (hit-shake animation, break sound, debris chips) is reused as-is by
/// placing a plain vanilla <see cref="BreakableContainer"/> directly; only the loot itself is swapped
/// out here, via a PREFIX that returns <c>false</c> to skip vanilla's own body entirely.
///
/// Vanilla's own SAVE/LOAD of a <see cref="BreakableContainer"/> doesn't correctly restore its private
/// health/hitSound/breakSound fields (confirmed directly via diagnostic logging — likely because vanilla
/// itself only ever places these in Mine levels, which regenerate fresh every visit rather than actually
/// persisting through a save/reload). <see cref="CaveHoleQuarrySystem.RepairCorruptedCratesAndBarrels"/>
/// is the fix for that (a different concern from the loot swap here, so it lives with the rest of that
/// class's day-tick logic instead of this one).
/// </summary>
internal static class CaveHoleCrateLootPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>The chance of getting anything at all when a Cave Hole Barrel/Crate breaks.</summary>
    private const double DropChance = 0.5;

    /// <summary>The item given when a Cave Hole Barrel/Crate's <see cref="DropChance"/> roll succeeds.</summary>
    private const string CaveCarrotItemId = "(O)78";


    /*********
    ** Public methods
    *********/
    /// <summary>Apply this patch to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(BreakableContainer), nameof(BreakableContainer.releaseContents)),
            prefix: new HarmonyMethod(typeof(CaveHoleCrateLootPatches), nameof(ReleaseContents_Prefix))
        );
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Give restricted loot instead of vanilla's own mine loot table, for a Barrel/Crate inside a Cave Hole interior.</summary>
    /// <param name="__instance">The Barrel/Crate being broken.</param>
    private static bool ReleaseContents_Prefix(BreakableContainer __instance)
    {
        GameLocation? location = __instance.Location;
        if (location is null || !location.NameOrUniqueName.StartsWith(CaveHoleInteraction.InteriorLocationBaseName))
            return true; // not a Cave Hole — let vanilla's own loot table run

        if (Game1.random.NextDouble() < CaveHoleCrateLootPatches.DropChance)
        {
            int quantity = Game1.random.Next(1, 4); // 1-3
            Game1.createMultipleObjectDebris(CaveHoleCrateLootPatches.CaveCarrotItemId, (int)__instance.TileLocation.X, (int)__instance.TileLocation.Y, quantity, location);
        }

        return false; // handled entirely — skip vanilla's own loot table
    }
}

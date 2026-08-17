using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Netcode;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Extensions;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework;

/// <summary>
/// MOD: added/changed. Spawns quarry-style stone/ore nodes across the Cave Hole (and Big Cave Hole)
/// interior's open floor — this hand-picked, hand-weighted item set is used for
/// both tiers rather than reusing the vanilla Quarry farm's own full item pool verbatim (this class's
/// earlier version). Every item ID below was looked up directly in the decompiled game code (or via
/// an in-game item lookup, for the Barrel/Crate IDs) rather than guessed — see each field's own
/// remarks for exactly where each one comes from.
///
/// A room's first morning fills densely — the room is meant to start out "nearly full" — by
/// deterministically visiting every open tile once (see <see cref="FillDensely"/>), which also rolls a
/// small chance of a purely-decorative Barrel/Crate instead of a normal node. Every morning after that
/// runs a single normal <see cref="SpawnPass"/>, the same geometrically-decaying random sampling the real
/// Quarry farm's own day-to-day respawns use (never placing Barrels/Crates), only ever placing on tiles
/// that are currently free.
///
/// Runs separately for EVERY placed Cave Hole/Big Cave Hole's own interior (see
/// <see cref="CaveHoleInteraction"/>'s own remarks for why each building gets its own instanced interior),
/// tracking which <see cref="Building.buildingType"/> each one was last densely filled for via
/// <see cref="Building.modData"/> — see <see cref="FilledForBuildingTypeModDataKey"/>'s own remarks for why
/// that (rather than a plain done/not-done flag) is what makes a Cave Hole → Big Cave Hole upgrade refill
/// its newly-opened floor space without disturbing anything already there.
/// </summary>
internal static class CaveHoleQuarrySystem
{
    /*********
    ** Fields
    *********/
    /// <summary>
    /// MOD: changed. The <see cref="Building.modData"/> key storing which <see cref="Building.buildingType"/>
    /// value the Cave Hole's interior was last densely filled for. A plain "already initialized" boolean
    /// (this field's original form) can only ever mean "yes"/"no" — but a Big Cave Hole upgrade needs a
    /// THIRD state: "already filled, but for the smaller Cave Hole's floor plan, not this bigger one yet".
    /// Comparing against the building's CURRENT <see cref="Building.buildingType"/> value gets all three
    /// for free: unset (or stale, e.g. still says the small Cave Hole's type after an upgrade) triggers
    /// <see cref="FillDensely"/> again — which is safe to re-run post-upgrade specifically because it
    /// already skips any tile that's occupied (see its own remarks), so a re-run only ever touches the
    /// newly-opened floor space, never anything the player placed or a node already sitting there.
    /// </summary>
    private const string FilledForBuildingTypeModDataKey = "luisMint.PoweredAutomation/CaveHoleQuarryFilledForType";

    /// <summary>The chance each individual open tile gets filled during the initial dense fill — the room is meant to start out "nearly full" rather than just denser-than-usual. Deliberately not 100%, so it doesn't look like an unnaturally perfect grid.</summary>
    private const double InitialFillDensity = 0.9;

    /// <summary>The chance a tile chosen during <see cref="FillDensely"/> gets a decorative crate/barrel instead of a normal quarry node — roughly 10% of the room's first-day contents. Deliberately checked only in <see cref="FillDensely"/>, never <see cref="SpawnPass"/>: these are meant as first-look flavor, not something that keeps respawning.</summary>
    private const double CrateOrBarrelChance = 0.1;

    /// <summary>The tile area to spawn nodes in for a regular Cave Hole — a bounding rectangle around the interior's open floor; <see cref="GameLocation.CanItemBePlacedHere"/> naturally excludes the walls/arrival tile within it.</summary>
    private static readonly Rectangle SpawnArea = new(8, 4, 11, 11);

    /// <summary>The tile area to spawn nodes in for a Big Cave Hole — the same idea as <see cref="SpawnArea"/>, sized for <c>CaveHole2.tmx</c>'s larger floor plan instead.</summary>
    private static readonly Rectangle BigSpawnArea = new(6, 6, 14, 10);

    /// <summary>The vanilla Barrel's own <see cref="StardewValley.Objects.BreakableContainer"/> item ID — found via an in-game item lookup.</summary>
    private const string BarrelItemId = "118";

    /// <summary>The vanilla Crate's own <see cref="StardewValley.Objects.BreakableContainer"/> item ID — found via an in-game item lookup.</summary>
    private const string CrateItemId = "119";

    /// <summary>MOD: added. Reflected access to <see cref="BreakableContainer"/>'s private <c>health</c> field — needed by <see cref="RepairCorruptedCratesAndBarrels"/> to detect a corrupted-by-reload Barrel/Crate (see that method's own remarks).</summary>
    private static readonly FieldInfo HealthField = AccessTools.Field(typeof(BreakableContainer), "health");

    /// <summary>The common plain stone node IDs — matches the "whichStone" values used for ordinary breakable rocks throughout <see cref="StardewValley.Locations.MineShaft.getRandomStoneForThisLevel"/>.</summary>
    private static readonly string[] CommonStoneIds = ["38", "32", "42", "40"];

    /// <summary>The rarer plain stone node IDs — the pair <see cref="StardewValley.Locations.MineShaft.getAppropriateStone"/> uses for the shallowest/quarry-like areas.</summary>
    private static readonly string[] RareStoneIds = ["668", "670"];

    /// <summary>The 2x2 resource clump IDs used in the early mines (see <see cref="StardewValley.Locations.MineShaft"/>'s own <c>whichClump = mineRandom.Choose(752, 754)</c> for its shallowest area) — health is left to <see cref="ResourceClump"/>'s own per-ID default rather than set explicitly.</summary>
    private static readonly int[] LargeStoneClumpIds = [752, 754];

    /// <summary>The Copper Ore node ID — <see cref="StardewValley.Locations.MineShaft.getAppropriateOre"/>'s own default/shallow-area ore.</summary>
    private const string CopperOreNodeId = "751";

    /// <summary>The rare gem/geode node IDs — Geode plus the 4 gem node IDs (as opposed to the gem ITEM ids) read out of <see cref="StardewValley.Locations.MineShaft.getRandomGemRichStoneForThisLevel"/>'s own switch (Ruby 4, Jade 6, Amethyst 8, Aquamarine 14).</summary>
    private static readonly string[] RareOreNodeIds = ["75", "4", "8", "6", "14"];

    /// <summary>
    /// MOD: changed. The Clay Stone node ID — a real vanilla mining node (found via its own reward switch
    /// in the decompiled source, alongside the Mussel Stone node IDs 816/817), which breaks like any other
    /// stone/ore node and drops several Clay per hit, rather than the earlier version of this field, which
    /// wrongly placed the raw Clay item itself as a stand-in "node" (no such object existing had been the
    /// assumption at the time). Spawns within the ore branch of <see cref="SpawnSomething"/>, taking up
    /// 5% of that branch (taken from <see cref="CopperOreNodeId"/>'s own share, not the
    /// rare gem chance).
    /// </summary>
    private const string ClayStoneNodeId = "818";

    /// <summary>MOD: added. The chance, within the ore branch of <see cref="SpawnSomething"/>, that a Clay node spawns instead of Copper Ore — taken from Copper Ore's own share.</summary>
    private const double ClayChance = 0.05;

    /// <summary>The Earth Crystal forageable ID — read out of <see cref="StardewValley.Locations.MineShaft.getRandomItemForThisLevel"/>'s own shallow-area defaults.</summary>
    private const string EarthCrystalItemId = "86";

    /// <summary>The Quartz forageable ID — read out of <see cref="StardewValley.Locations.MineShaft.getRandomItemForThisLevel"/>'s own shallow-area defaults.</summary>
    private const string QuartzItemId = "80";

    /// <summary>
    /// MOD: changed. The chance a "common" forageable roll (see <see cref="SpawnSomething"/>) picks
    /// <see cref="EarthCrystalItemId"/> rather than <see cref="QuartzItemId"/> —
    /// Earth Crystal is now half as common as before (was an even 50/50 via <c>Game1.random.Choose</c>), with
    /// Quartz taking up the freed-up share instead of the two staying evenly split.
    /// </summary>
    private const double EarthCrystalChance = 0.25;

    /// <summary>The rarer sparse forageable ID — Red Mushroom (420), also read out of <see cref="StardewValley.Locations.MineShaft.getRandomItemForThisLevel"/>.</summary>
    private const string RareForageId = "420";

    /// <summary>
    /// MOD: added. Every Green Rain Weeds variant ID — <c>"(O)GreenRainWeeds0"</c> through
    /// <c>"(O)GreenRainWeeds7"</c>, matching vanilla's own convention exactly (found in
    /// <see cref="StardewValley.GameLocation"/>'s own decompiled weed-spawning logic: <c>"(O)GreenRainWeeds" + Game1.random.Next(8)</c>).
    /// Used by <see cref="FillWithGreenRainWeeds"/>.
    /// </summary>
    private static readonly string[] GreenRainWeedsIds = ["(O)GreenRainWeeds0", "(O)GreenRainWeeds1", "(O)GreenRainWeeds2", "(O)GreenRainWeeds3", "(O)GreenRainWeeds4", "(O)GreenRainWeeds5", "(O)GreenRainWeeds6", "(O)GreenRainWeeds7"];

    /// <summary>MOD: added. The regular weed IDs <see cref="SwapRemainingGreenRainWeeds"/> replaces any leftover Green Rain Weeds with — these specific IDs were confirmed valid via diagnostic logging: DisplayName "Weeds" for all three.</summary>
    private static readonly string[] RegularWeedIds = ["(O)313", "(O)314", "(O)315"];


    /*********
    ** Public methods
    *********/
    /// <summary>Spawn this morning's nodes in every placed Cave Hole's own interior — meant to be called once per day.</summary>
    public static void Tick()
    {
        foreach (Building building in CaveHoleQuarrySystem.FindCaveHoleBuildings())
        {
            if (building.daysOfConstructionLeft.Value > 0 || building.daysUntilUpgrade.Value > 0)
                continue; // still under construction, or an upgrade hasn't finished yet — nothing to spawn yet

            GameLocation? caveHole = building.GetIndoors();
            if (caveHole is null)
                continue;

            // MOD: added — vanilla's own BreakableContainer (see SpawnCrateOrBarrel) apparently isn't
            // built to survive a save/reload at all: it's normally only ever used in Mine levels, which
            // regenerate fresh every visit rather than actually being saved and loaded mid-existence. Its
            // health/hitSound/breakSound fields silently come back at their .NET default (0/null) after
            // a reload — confirmed directly via diagnostic logging: a freshly-placed Barrel/Crate hit
            // normally (health 3→2→1→0 with the right sounds each time), but the SAME one, after a
            // sleep+reload, read health=0 and both sounds=null on the very first hit, breaking silently
            // and instantly instead. Since we can't fix vanilla's own (de)serialization, this repairs the
            // symptom once per day: any Cave Hole Barrel/Crate reading health <= 0 gets replaced with a
            // freshly-constructed one at the same tile (which is otherwise indistinguishable from the
            // corrupted one — same appearance, same restricted loot behavior).
            CaveHoleQuarrySystem.RepairCorruptedCratesAndBarrels(caveHole);

            Rectangle spawnArea = CaveHoleQuarrySystem.GetSpawnArea(building.buildingType.Value);

            // MOD: added — a Green Rain day densely fills the room with Green
            // Rain Weeds INSTEAD of the normal fill/top-up below (there's little open floor left for
            // normal nodes to spawn into afterward anyway). Swapping any uncleared ones to regular weeds
            // the day after is NOT handled here — see
            // Patches.CaveHoleGreenRainPatches's own remarks for why that needs to happen at a completely
            // different, much more precise moment instead of a plain day-after Tick() check.
            if (Game1.isGreenRain)
            {
                CaveHoleQuarrySystem.FillWithGreenRainWeeds(caveHole, spawnArea);
                continue;
            }

            // MOD: changed — comparing against the CURRENT building type (rather than a plain done/not-done
            // flag) means this also fires once right after a Cave Hole upgrades into a Big Cave Hole, since
            // the stored value still says the old (smaller) type. See FilledForBuildingTypeModDataKey's own
            // remarks for why that's exactly the refill-without-clearing behavior the upgrade needs.
            if (!building.modData.TryGetValue(CaveHoleQuarrySystem.FilledForBuildingTypeModDataKey, out string? filledFor) || filledFor != building.buildingType.Value)
            {
                CaveHoleQuarrySystem.FillDensely(caveHole, spawnArea);
                building.modData[CaveHoleQuarrySystem.FilledForBuildingTypeModDataKey] = building.buildingType.Value;
            }
            else
            {
                // MOD: changed — run three independent spawn passes instead of
                // one, so more respawns each morning. Each pass still only ever places into tiles that are
                // free at the time it runs, so each later pass naturally spawns less once the room's
                // fuller, rather than double/triple-placing on top of an earlier pass's own results.
                CaveHoleQuarrySystem.SpawnPass(caveHole, spawnArea);
                CaveHoleQuarrySystem.SpawnPass(caveHole, spawnArea);
                CaveHoleQuarrySystem.SpawnPass(caveHole, spawnArea);
            }
        }
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Find every placed Cave Hole or Big Cave Hole building.</summary>
    private static IEnumerable<Building> FindCaveHoleBuildings()
    {
        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (Building building in location.buildings)
            {
                if (CaveHoleInteraction.IsCaveHoleBuildingType(building.buildingType.Value))
                    yield return building;
            }
        }
    }

    /// <summary>Get the tile area to spawn nodes in for a given Cave Hole building type.</summary>
    /// <param name="buildingType">The building's own <see cref="Building.buildingType"/> value.</param>
    private static Rectangle GetSpawnArea(string buildingType)
    {
        return buildingType == CaveHoleInteraction.BigBuildingType
            ? CaveHoleQuarrySystem.BigSpawnArea
            : CaveHoleQuarrySystem.SpawnArea;
    }

    /// <summary>
    /// Fill the room densely for its first morning — unlike <see cref="SpawnPass"/>'s geometrically-decaying
    /// random sampling (which reliably leaves plenty of open floor even after many repeats, since later
    /// attempts increasingly re-roll tiles that are already occupied), this deterministically visits every
    /// tile in <paramref name="spawnArea"/> once, so coverage is actually "nearly full" rather than just
    /// denser. Also the ONLY place decorative crates/barrels ever spawn (see <see cref="CrateOrBarrelChance"/>).
    ///
    /// MOD: changed. Re-run a second time (with a bigger <paramref name="spawnArea"/>) right after a Cave
    /// Hole upgrades into a Big Cave Hole — this must NOT clear or disturb
    /// anything already there (player-placed items included), only fill newly-opened floor space. Since
    /// every tile check below already requires the tile to be unoccupied, simply re-running this over the
    /// bigger area does exactly that for free: already-occupied tiles (old nodes, crates, anything the
    /// player placed) are silently skipped, so only genuinely empty tiles ever get anything new.
    /// </summary>
    /// <param name="caveHole">The Cave Hole interior to spawn in.</param>
    /// <param name="spawnArea">The tile area to spawn in.</param>
    private static void FillDensely(GameLocation caveHole, Rectangle spawnArea)
    {
        for (int y = spawnArea.Y; y < spawnArea.Y + spawnArea.Height; y++)
        {
            for (int x = spawnArea.X; x < spawnArea.X + spawnArea.Width; x++)
            {
                Vector2 tile = new(x, y);
                if (caveHole.CanItemBePlacedHere(tile) && !caveHole.terrainFeatures.ContainsKey(tile) && !caveHole.objects.ContainsKey(tile) && Game1.random.NextDouble() < CaveHoleQuarrySystem.InitialFillDensity)
                {
                    if (Game1.random.NextDouble() < CaveHoleQuarrySystem.CrateOrBarrelChance)
                        CaveHoleQuarrySystem.SpawnCrateOrBarrel(caveHole, tile);
                    else
                        CaveHoleQuarrySystem.SpawnSomething(caveHole, tile);
                }
            }
        }
    }

    /// <summary>
    /// MOD: added. Replace any Cave Hole Barrel/Crate whose <c>health</c> reads at or below 0 — see this
    /// method's own call site in <see cref="Tick"/> for why that happens (vanilla's own save/load doesn't
    /// correctly restore a <see cref="BreakableContainer"/>'s private fields) and why replacing it with a
    /// fresh one is the practical fix. A legitimately-near-death Barrel/Crate the player left partially
    /// hit before saving would also get "healed" back to full by this — a minor, acceptable tradeoff,
    /// since the alternative (permanently stuck instant-breaking) is far worse and there's no way to tell
    /// the two cases apart once the corruption has already happened.
    /// </summary>
    /// <param name="caveHole">The Cave Hole interior to check.</param>
    private static void RepairCorruptedCratesAndBarrels(GameLocation caveHole)
    {
        List<(Vector2 Tile, string ItemId)>? toRepair = null;

        foreach ((Vector2 tile, SObject obj) in caveHole.objects.Pairs)
        {
            if (obj is not BreakableContainer container || (container.ItemId != CaveHoleQuarrySystem.BarrelItemId && container.ItemId != CaveHoleQuarrySystem.CrateItemId))
                continue;

            if (CaveHoleQuarrySystem.HealthField.GetValue(container) is NetInt { Value: <= 0 })
                (toRepair ??= new List<(Vector2, string)>()).Add((tile, container.ItemId));
        }

        if (toRepair != null)
        {
            foreach ((Vector2 tile, string itemId) in toRepair)
                caveHole.objects[tile] = new BreakableContainer(tile, itemId);
        }
    }

    /// <summary>
    /// MOD: added. Densely fill the room with Green Rain Weeds on a Green Rain day —
    /// roughly 90% of open tiles (reusing <see cref="InitialFillDensity"/>'s own "nearly full,
    /// but not an unnaturally perfect grid" density), the same deterministic every-open-tile sweep
    /// <see cref="FillDensely"/> uses, just with Green Rain Weeds instead of a normal node roll.
    /// </summary>
    /// <param name="caveHole">The Cave Hole interior to fill.</param>
    /// <param name="spawnArea">The tile area to fill in.</param>
    private static void FillWithGreenRainWeeds(GameLocation caveHole, Rectangle spawnArea)
    {
        for (int y = spawnArea.Y; y < spawnArea.Y + spawnArea.Height; y++)
        {
            for (int x = spawnArea.X; x < spawnArea.X + spawnArea.Width; x++)
            {
                Vector2 tile = new(x, y);
                if (caveHole.CanItemBePlacedHere(tile) && !caveHole.terrainFeatures.ContainsKey(tile) && !caveHole.objects.ContainsKey(tile) && Game1.random.NextDouble() < CaveHoleQuarrySystem.InitialFillDensity)
                    caveHole.objects.Add(tile, ItemRegistry.Create<SObject>(Game1.random.Choose(CaveHoleQuarrySystem.GreenRainWeedsIds)));
            }
        }
    }

    /// <summary>
    /// MOD: changed. Replace HALF of any still-uncleared Green Rain Weeds with regular weeds — the other
    /// half is just removed outright. Made internal (not private) so
    /// <see cref="Patches.CaveHoleGreenRainPatches"/> can call it
    /// at the one precise moment this actually needs to happen — see that class's own remarks for why a
    /// plain "check this the next time Tick() runs" approach doesn't work: vanilla's own
    /// <see cref="GameLocation.performDayAfterGreenRainUpdate"/> unconditionally deletes anything named
    /// "GreenRainWeeds" during its OWN new-day setup, which runs well before <c>ModEntry.OnDayStarted</c>
    /// (and therefore <see cref="Tick"/>) ever gets a chance to react — confirmed directly via diagnostic
    /// logging (consistently found 0 Green Rain Weeds left to swap, every time, across every Cave Hole).
    /// </summary>
    /// <param name="caveHole">The Cave Hole interior to check.</param>
    internal static void SwapRemainingGreenRainWeeds(GameLocation caveHole)
    {
        List<Vector2>? toSwap = null;

        foreach ((Vector2 tile, SObject obj) in caveHole.objects.Pairs)
        {
            if (obj.Name.Contains("GreenRainWeeds"))
                (toSwap ??= new List<Vector2>()).Add(tile);
        }

        if (toSwap != null)
        {
            foreach (Vector2 tile in toSwap)
            {
                // MOD: changed — only half of what's left becomes a regular
                // weed; the other half is just gone, rather than every leftover Green Rain Weed surviving
                // as a regular one.
                if (Game1.random.NextBool())
                    caveHole.objects[tile] = ItemRegistry.Create<SObject>(Game1.random.Choose(CaveHoleQuarrySystem.RegularWeedIds));
                else
                    caveHole.objects.Remove(tile);
            }
        }
    }

    /// <summary>Run one Quarry-farm-style spawn pass — a geometrically-decaying series of single-tile spawn attempts (see <see cref="M:StardewValley.Farm.doDailyMountainFarmUpdate"/>).</summary>
    /// <param name="caveHole">The Cave Hole interior to spawn in.</param>
    /// <param name="spawnArea">The tile area to spawn in.</param>
    private static void SpawnPass(GameLocation caveHole, Rectangle spawnArea)
    {
        double chance = 1.0;
        while (Game1.random.NextDouble() < chance)
        {
            Vector2 tile = new(
                Game1.random.Next(spawnArea.X, spawnArea.X + spawnArea.Width),
                Game1.random.Next(spawnArea.Y, spawnArea.Y + spawnArea.Height)
            );

            if (caveHole.CanItemBePlacedHere(tile) && !caveHole.terrainFeatures.ContainsKey(tile) && !caveHole.objects.ContainsKey(tile))
                CaveHoleQuarrySystem.SpawnSomething(caveHole, tile);

            chance *= 0.75;
        }
    }

    /// <summary>Place a decorative Barrel or Crate — purely visual flavor for the room's first morning; its loot is restricted separately (see <see cref="Patches.CaveHoleCrateLootPatches"/>).</summary>
    /// <param name="caveHole">The Cave Hole interior to spawn in.</param>
    /// <param name="tile">The tile to spawn on.</param>
    private static void SpawnCrateOrBarrel(GameLocation caveHole, Vector2 tile)
    {
        string itemId = Game1.random.NextBool() ? CaveHoleQuarrySystem.BarrelItemId : CaveHoleQuarrySystem.CrateItemId;
        caveHole.objects.Add(tile, new BreakableContainer(tile, itemId));
    }

    /// <summary>Roll and place one item — a plain stone, a large stone clump, an ore node, or a sparse forageable.</summary>
    /// <param name="caveHole">The Cave Hole interior to spawn in.</param>
    /// <param name="tile">The tile to spawn on.</param>
    private static void SpawnSomething(GameLocation caveHole, Vector2 tile)
    {
        // top-level category roll (out of 100)
        int roll = Game1.random.Next(100);

        // MOD: changed — plain stone trimmed from 50 to 45 and the sparse
        // forageable category (below) trimmed from 15 to 10 wide, with all 10 freed points folded into
        // ore node (20 -> 30), to push more copper specifically (see the ore branch's own remarks) while
        // also making gems/quartz/earth crystal noticeably rarer.
        if (roll < 45) // plain stone
        {
            string stoneId = Game1.random.NextDouble() < 0.1
                ? Game1.random.Choose(CaveHoleQuarrySystem.RareStoneIds)
                : Game1.random.Choose(CaveHoleQuarrySystem.CommonStoneIds);
            caveHole.objects.Add(tile, new SObject(stoneId, 1) { MinutesUntilReady = 2 });
        }
        else if (roll < 60) // large stone / resource clump (45-59, i.e. 15 wide) — needs its own 2x2 footprint clear
        {
            CaveHoleQuarrySystem.TrySpawnLargeStoneClump(caveHole, tile);
        }
        // MOD: changed — top-level share grown from 20 to 30 (freed width taken from plain stone and the
        // sparse forageable category, see this method's own remarks) for more
        // copper overall.
        else if (roll < 90) // ore node (60-89, i.e. 30 wide)
        {
            // MOD: changed — rare gem-node sub-chance trimmed from 0.1 to 0.05,
            // giving Copper Ore a bigger share of this now-wider ore band on top of the band itself
            // growing (see this method's own remarks).
            double oreRoll = Game1.random.NextDouble();
            string oreId;
            if (oreRoll < 0.05)
                oreId = Game1.random.Choose(CaveHoleQuarrySystem.RareOreNodeIds);
            // MOD: added — 5% Clay, taken from Copper Ore's own share (was a
            // flat 90% Copper Ore/10% rare gem split; now roughly 90% Copper Ore/5% rare gem/5% Clay).
            else if (oreRoll < 0.05 + CaveHoleQuarrySystem.ClayChance)
                oreId = CaveHoleQuarrySystem.ClayStoneNodeId;
            else
                oreId = CaveHoleQuarrySystem.CopperOreNodeId;
            caveHole.objects.Add(tile, new SObject(oreId, 1) { MinutesUntilReady = 3 });
        }
        // MOD: changed — sparse forageables (this whole category, not just Red
        // Mushroom within it) only actually spawn some of the time this branch is reached, rather than
        // shrinking its top-level share (which would've meant re-deriving the other categories' shares to
        // still sum to 100) — except this round, the top-level share ITSELF also shrank (15 -> 10, see
        // this method's own remarks), on top of trimming this activation chance again (was 0.4), to make
        // gems/quartz/earth crystal noticeably rarer.
        else if (Game1.random.NextDouble() < 0.3) // sparse forageable (90-99, i.e. 10 wide)
        {
            string forageId;
            if (Game1.random.NextDouble() < 0.2)
                forageId = CaveHoleQuarrySystem.RareForageId;
            else
                forageId = Game1.random.NextDouble() < CaveHoleQuarrySystem.EarthCrystalChance
                    ? CaveHoleQuarrySystem.EarthCrystalItemId
                    : CaveHoleQuarrySystem.QuartzItemId;
            SObject forageItem = ItemRegistry.Create<SObject>("(O)" + forageId);

            // MOD: added — without this, these show up as plain placed objects the player can't actually
            // interact with at all; vanilla's own "walk up and grab it" pickup logic (see GameLocation's
            // own object-interaction handling) is gated specifically on this flag, same as every other
            // forage item spawned directly on the ground.
            forageItem.IsSpawnedObject = true;

            caveHole.objects.Add(tile, forageItem);
        }
    }

    /// <summary>Try to place a 2x2 large stone/resource clump, if its whole footprint is actually free.</summary>
    /// <param name="caveHole">The Cave Hole interior to spawn in.</param>
    /// <param name="topLeft">The top-left tile of the clump's 2x2 footprint.</param>
    private static void TrySpawnLargeStoneClump(GameLocation caveHole, Vector2 topLeft)
    {
        Vector2 topRight = topLeft + new Vector2(1, 0);
        Vector2 bottomLeft = topLeft + new Vector2(0, 1);
        Vector2 bottomRight = topLeft + new Vector2(1, 1);

        if (!caveHole.CanItemBePlacedHere(topRight) || !caveHole.CanItemBePlacedHere(bottomLeft) || !caveHole.CanItemBePlacedHere(bottomRight))
            return; // the rest of the 2x2 footprint isn't clear — skip this attempt rather than spawn a smaller/overlapping clump

        int clumpId = Game1.random.Choose(CaveHoleQuarrySystem.LargeStoneClumpIds);
        caveHole.resourceClumps.Add(new ResourceClump(clumpId, 2, 2, topLeft));
    }
}

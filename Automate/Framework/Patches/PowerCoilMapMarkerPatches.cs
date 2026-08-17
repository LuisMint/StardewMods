using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Common;
using StardewValley;
using StardewValley.Menus;
using StardewValley.WorldMaps;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.Automate.Framework.Patches;

/// <summary>
/// MOD: added. Marks every Power Coil's location on the world map (the big map opened via the pause
/// menu's map tab, or the M key) when the player toggles it on from <see cref="PowerSiloMenu"/>'s
/// "Show/Hide Power Coils on map" button — independent of <see cref="PowerCoilCompass.ShowCompass"/>
/// (the two used to share a single toggle, but each now has its own
/// button/state, for finer-grained control) — the same idea as the NPCMapLocations mod's own NPC markers, but
/// fully independent of it (no reference, dependency, or compatibility risk either way): rather than
/// subclassing/replacing <see cref="MapPage"/> the way that mod does (which would fight over
/// ownership of <c>GameMenu</c>'s page instance if both mods tried it at once), this is a Harmony
/// postfix on <see cref="MapPage.draw"/> itself. Since NPCMapLocations' own map page (if installed)
/// doesn't override <c>draw</c> at all (only <c>drawMiniPortraits</c>/tooltip methods), this postfix
/// fires and draws on top regardless of whether the active page is vanilla's own <see cref="MapPage"/>
/// or NPCMapLocations' subclass of it — giving the same "works alongside it" result without touching
/// its code or depending on an API it doesn't expose.
///
/// Coordinate mapping uses the vanilla 1.6 <see cref="WorldMapManager.GetPositionData"/> /
/// <see cref="MapAreaPosition.GetMapPixelPosition"/> pipeline directly — the same one both vanilla's
/// own map markers and NPCMapLocations build on — rather than reinventing per-location pixel-rect data.
/// </summary>
internal static class PowerCoilMapMarkerPatches
{
    /*********
    ** Fields
    *********/
    /// <summary>Get the item names/IDs that count as a Power Coil for capacity purposes.</summary>
    private static Func<HashSet<string>>? GetSourceNames;

    /// <summary>The draw scale for each marker icon — the craft icon's native size (16x32) is a bit large for a map marker, so this shrinks it down.</summary>
    private const float MarkerScale = 0.5f;

    /// <summary>Whether markers are currently shown — toggled by <see cref="PowerSiloMenu"/>'s "Show/Hide Power Coils on map" button.</summary>
    public static bool ShowMapMarkers { get; private set; }

    /// <summary>MOD: added. How many draw calls to go between re-scanning every location for Power Coils — see <see cref="RefreshMarkerCacheIfNeeded"/>'s remarks for why this exists.</summary>
    private const int RescanIntervalFrames = 60;

    /// <summary>MOD: added. The <see cref="MapPage"/> instance <see cref="CachedMarkers"/> was last scanned for — a fresh instance (i.e. the map was just (re)opened) forces an immediate re-scan regardless of <see cref="TicksUntilRescan"/>.</summary>
    private static MapPage? LastMapPageInstance;

    /// <summary>MOD: added. Every Power Coil found across every location as of the last scan, paired with its already-resolved map pixel position (which doesn't depend on anything that changes frame-to-frame, unlike the compass arrows in <see cref="PowerCoilCompass"/>).</summary>
    private static readonly List<(SObject Coil, Vector2 PixelPosition)> CachedMarkers = [];

    /// <summary>MOD: added. Draw calls remaining before <see cref="CachedMarkers"/> is refreshed again, even if the map page instance hasn't changed.</summary>
    private static int TicksUntilRescan;


    /*********
    ** Public methods
    *********/
    /// <summary>Prepare this patch class before <see cref="Apply"/> is called.</summary>
    /// <param name="getSourceNames">Get the item names/IDs that count as a Power Coil for capacity purposes.</param>
    public static void Initialize(Func<HashSet<string>> getSourceNames)
    {
        PowerCoilMapMarkerPatches.GetSourceNames = getSourceNames;
    }

    /// <summary>Apply these patches to the game.</summary>
    /// <param name="harmony">The Harmony instance to patch with.</param>
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
            original: AccessTools.Method(typeof(MapPage), nameof(MapPage.draw), [typeof(SpriteBatch)]),
            postfix: new HarmonyMethod(typeof(PowerCoilMapMarkerPatches), nameof(Draw_Postfix))
        );
    }

    /// <summary>Toggle whether Power Coil markers are currently shown on the world map.</summary>
    /// <returns>Returns the new state (<c>true</c> if markers are now shown).</returns>
    public static bool ToggleMapMarkers()
    {
        return PowerCoilMapMarkerPatches.ShowMapMarkers = !PowerCoilMapMarkerPatches.ShowMapMarkers;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Draw a small icon over every Power Coil's location on the world map, once <see cref="ShowMapMarkers"/> is toggled on — the ordinary craft icon for a powered coil, or its dedicated unpowered variant for one that's currently over the Power Silo capacity, so the player can spot coils they left unpowered.</summary>
    /// <param name="__instance">The map page being drawn.</param>
    /// <param name="b">The sprite batch being drawn to.</param>
    private static void Draw_Postfix(MapPage __instance, SpriteBatch b)
    {
        if (!PowerCoilMapMarkerPatches.ShowMapMarkers || PowerCoilMapMarkerPatches.GetSourceNames is not { } getSourceNames)
            return;

        HashSet<string> sourceNames = getSourceNames();
        if (sourceNames.Count == 0)
            return;

        PowerCoilMapMarkerPatches.RefreshMarkerCacheIfNeeded(__instance, sourceNames);
        if (PowerCoilMapMarkerPatches.CachedMarkers.Count == 0)
            return;

        Texture2D poweredTexture = Game1.content.Load<Texture2D>(PowerCoilPatches.CraftIconAssetName);
        Texture2D unpoweredTexture = Game1.content.Load<Texture2D>(PowerCoilPatches.UnpoweredCraftIconAssetName);

        foreach ((SObject coil, Vector2 pixelPosition) in PowerCoilMapMarkerPatches.CachedMarkers)
        {
            bool isPowered = PowerCoilPatches.IsPowered(coil);
            Texture2D texture = isPowered ? poweredTexture : unpoweredTexture;

            Vector2 drawPosition = new(
                __instance.mapBounds.X + pixelPosition.X - texture.Width * PowerCoilMapMarkerPatches.MarkerScale / 2f,
                __instance.mapBounds.Y + pixelPosition.Y - texture.Height * PowerCoilMapMarkerPatches.MarkerScale / 2f
            );

            b.Draw(texture, drawPosition, new Rectangle(0, 0, texture.Width, texture.Height), Color.White, 0f, Vector2.Zero, PowerCoilMapMarkerPatches.MarkerScale, SpriteEffects.None, 1f);
        }
    }

    /// <summary>
    /// MOD: added. Refresh <see cref="CachedMarkers"/> if the map was just (re)opened, or if it's just
    /// been too long since the last scan — performance guard for saves with a large number of Power
    /// Coils (e.g. 100+): without this, <see cref="Draw_Postfix"/> would re-scan every object in every
    /// location, and re-resolve each one's <see cref="WorldMapManager.GetPositionData"/> lookup, on
    /// EVERY single frame the map stays open. A coil's map position never changes on its own (only if
    /// the coil itself is moved, which requires breaking and replacing it), so caching the resolved
    /// pixel positions — not just which objects are coils — avoids repeating that lookup for as long as
    /// the same map page instance stays open.
    /// </summary>
    /// <param name="mapPage">The map page currently being drawn.</param>
    /// <param name="sourceNames">The item names/IDs that count as a Power Coil for capacity purposes.</param>
    private static void RefreshMarkerCacheIfNeeded(MapPage mapPage, HashSet<string> sourceNames)
    {
        if (ReferenceEquals(PowerCoilMapMarkerPatches.LastMapPageInstance, mapPage) && PowerCoilMapMarkerPatches.TicksUntilRescan > 0)
        {
            PowerCoilMapMarkerPatches.TicksUntilRescan--;
            return;
        }

        PowerCoilMapMarkerPatches.LastMapPageInstance = mapPage;
        PowerCoilMapMarkerPatches.CachedMarkers.Clear();

        foreach (GameLocation location in CommonHelper.GetLocations())
        {
            foreach (SObject obj in location.Objects.Values)
            {
                if (!sourceNames.Contains(obj.QualifiedItemId) && !sourceNames.Contains(obj.Name))
                    continue;

                // MOD: a location without world map data (e.g. most indoor locations) simply isn't
                // shown on the map at all — silently skip that coil rather than guessing a fallback
                // position for it.
                MapAreaPositionWithContext? positionData = WorldMapManager.GetPositionData(location, obj.TileLocation.ToPoint());
                if (positionData is not { } position)
                    continue;

                PowerCoilMapMarkerPatches.CachedMarkers.Add((obj, position.GetMapPixelPosition()));
            }
        }

        PowerCoilMapMarkerPatches.TicksUntilRescan = PowerCoilMapMarkerPatches.RescanIntervalFrames;
    }
}

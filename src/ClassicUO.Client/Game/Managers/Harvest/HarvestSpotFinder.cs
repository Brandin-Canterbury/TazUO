#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>A tile the agent can target with its tool.</summary>
/// <param name="X">Tile X.</param>
/// <param name="Y">Tile Y.</param>
/// <param name="Z">Z to send with the target.</param>
/// <param name="Graphic">Static graphic to target, or 0 for the land tile.</param>
/// <param name="Kind">Bank the tile draws from.</param>
internal readonly record struct HarvestSpot(ushort X, ushort Y, sbyte Z, ushort Graphic, HarvestResourceKind Kind);

/// <summary>Picks the next harvestable tile around the player. Main thread only.</summary>
internal static class HarvestSpotFinder
{
    #region Public methods

    /// <summary>
    /// The closest harvestable tile within reach whose bank is not known to be empty and which has not
    /// been ruled out this stand.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="mode">Mining or lumberjacking.</param>
    /// <param name="skippedTiles">Tiles the server refused (out of sight, not harvestable), packed by <see cref="TileKey"/>.</param>
    /// <param name="spot">The chosen tile when the method returns true.</param>
    /// <returns>False when every reachable spot is empty or ruled out.</returns>
    public static bool TryFindNext(World world, HarvestMode mode, HashSet<int> skippedTiles, out HarvestSpot spot)
    {
        spot = default;
        PlayerMobile? player = world.Player;

        if (player == null || world.Map == null)
            return false;

        int mapIndex = world.Map.Index;

        // Ring by ring outwards, so the player's own tile and neighbours are tried first.
        for (int ring = 0; ring <= HarvestData.HARVEST_RANGE; ring++)
        {
            for (int dy = -ring; dy <= ring; dy++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        continue;

                    int x = player.X + dx;
                    int y = player.Y + dy;

                    if (x < 0 || y < 0 || skippedTiles.Contains(TileKey(x, y)))
                        continue;

                    if (!TryGetHarvestable(world, mode, x, y, out spot))
                        continue;

                    if (!HarvestDepletionTracker.Instance.IsDepleted(spot.Kind, mapIndex, x, y))
                        return true;
                }
            }
        }

        spot = default;
        return false;
    }

    /// <summary>Packs a tile position into a set key.</summary>
    /// <param name="x">Tile X.</param>
    /// <param name="y">Tile Y.</param>
    /// <returns>The key.</returns>
    public static int TileKey(int x, int y) => (x << 16) | (y & 0xFFFF);

    #endregion

    #region Private methods

    /// <summary>Finds the tile's harvestable object: a tree, a cave rock static, or mountain land.</summary>
    private static bool TryGetHarvestable(World world, HarvestMode mode, int x, int y, out HarvestSpot spot)
    {
        spot = default;
        bool foundLand = false;

        for (GameObject? obj = world.Map.GetTile(x, y, false); obj != null; obj = obj.TNext)
        {
            if (obj is Static staticObject)
            {
                ushort graphic = staticObject.OriginalGraphic;
                bool match = mode == HarvestMode.Lumberjacking
                    ? HarvestData.IsTree(graphic)
                    : HarvestData.IsMineableStatic(graphic);

                if (match)
                {
                    HarvestResourceKind kind = mode == HarvestMode.Lumberjacking ? HarvestResourceKind.Wood : HarvestResourceKind.Ore;
                    spot = new HarvestSpot((ushort)x, (ushort)y, staticObject.Z, graphic, kind);
                    return true;
                }
            }
            else if (obj is Land land && mode == HarvestMode.Mining && HarvestData.IsMineableLand(land.OriginalGraphic))
            {
                spot = new HarvestSpot((ushort)x, (ushort)y, land.Z, 0, HarvestResourceKind.Ore);
                foundLand = true;
            }
        }

        return foundLand;
    }

    #endregion
}

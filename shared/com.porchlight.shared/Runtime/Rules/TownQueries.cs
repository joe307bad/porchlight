using System;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>Lookups used by builder tools (hover, remove, snap). These never change the town.</summary>
    public static class TownQueries
    {
        public static BuildingDto FindBuildingAt(TownDto town, Vec2 point)
        {
            // Search back to front so the most recently placed building wins.
            for (var i = town.Buildings.Count - 1; i >= 0; i--)
            {
                var building = town.Buildings[i];
                if (TownRules.TryFootprint(building, out var footprint) && footprint.Contains(point)) return building;
            }

            return null;
        }

        /// <summary>The tile under a point, if it's a street, path or plaza.</summary>
        public static bool TryFindPavedAt(TownDto town, Vec2 point, out int i, out int j, out TileSurface surface)
        {
            surface = TileSurface.Grass;
            if (!TownTiles.TryCell(town, point, out i, out j)) return false;
            surface = TownTiles.Surface(town, i, j);
            return TownTiles.IsPaved(surface);
        }

        /// <summary>
        /// When the cursor is near a street, path or plaza, places the building beside it, square to the
        /// tiles, with its front facing the paving and flush against the tile's edge. The building lands on
        /// the side of the paving the cursor is on.
        /// </summary>
        public static bool TrySnapToRoad(TownDto town, BuildingType type, Vec2 cursor, out Vec2 position, out float rotationDeg)
        {
            position = cursor;
            rotationDeg = 0f;
            if (!TownTiles.TryCell(town, cursor, out var ci, out var cj) || TownTiles.IsPaved(TownTiles.Surface(town, ci, cj))) return false;

            var size = TownTiles.Spacing(town);
            var reach = (int)MathF.Ceiling((type.Depth + 2f) / size);
            var best = float.MaxValue;
            for (var side = 0; side < 4; side++)
            {
                var (di, dj) = TownTiles.Neighbours[side];
                for (var step = 1; step <= reach; step++)
                {
                    int i = ci + di * step, j = cj + dj * step;
                    if (!TownTiles.Inside(town, i, j)) break;
                    if (!TownTiles.IsPaved(TownTiles.Surface(town, i, j))) continue;

                    // The paving's near edge, and how far the cursor is from it.
                    var tile = TownTiles.Center(town, i, j);
                    var facing = new Vec2(di, dj);
                    var edge = tile - facing * (size * 0.5f);
                    var gap = Vec2.Dot(edge - cursor, facing);
                    if (gap < best)
                    {
                        best = gap;
                        var across = new Vec2(MathF.Abs(dj), MathF.Abs(di));

                        // Back from the edge by half the building, and along it in half-tile steps.
                        var lateral = MathF.Round(Vec2.Dot(cursor, across) / (size * 0.5f)) * (size * 0.5f);
                        var depth = Vec2.Dot(edge, facing) - type.Depth * 0.5f - 0.05f;
                        position = facing * depth + across * lateral;
                        rotationDeg = Vec2.HeadingOf(facing);
                    }

                    break;
                }
            }

            return best < float.MaxValue;
        }
    }
}

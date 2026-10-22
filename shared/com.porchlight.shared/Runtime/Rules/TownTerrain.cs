using System;
using System.Collections.Generic;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// The height of the ground, wherever anything needs it (walking, cameras, plants, buildings). The
    /// ground is tiles (<see cref="TownTiles"/>); this answers from them. Towns saved before tiles had a
    /// heightmap instead, which is only read here to convert them (<see cref="TownTiles.Upgrade"/>).
    /// </summary>
    public static class TownTerrain
    {
        public const float MinHeight = TownTiles.MinLevel;
        public const float MaxHeight = TownTiles.MaxLevel;

        /// <summary>Rise over run that still counts as gentle ground underfoot: about 17°.</summary>
        public const float MaxSlope = 0.3f;

        /// <summary>Height of the ground at a point: the tiles' (0 in the countryside), or an old town's heightmap until it's converted.</summary>
        public static float HeightAt(TownDto town, Vec2 point)
        {
            if (town == null) return 0f;
            return TownTiles.Has(town) ? TownTiles.HeightAt(town, point) : LegacyHeightAt(town, point);
        }

        /// <summary>Whether the ground has any shape to it (tiles always do).</summary>
        public static bool IsShaped(TownDto town) => TownTiles.Has(town) || TryLegacyGrid(town, out _, out _);

        /// <summary>The height a building stands at: its tiles' level.</summary>
        public static float BaseHeight(TownDto town, in OrientedRect footprint) =>
            TownTiles.Has(town) ? TownTiles.BaseLevel(town, footprint) : LegacyBaseHeight(town, footprint);

        public static float BaseHeight(TownDto town, BuildingDto building) =>
            TownRules.TryFootprint(building, out var footprint) ? BaseHeight(town, footprint) : HeightAt(town, new Vec2(building.X, building.Z));

        /// <summary>
        /// An old town's heightmap at a point: corner heights in centimetres on a lattice, each cell two
        /// triangles split from its -X -Z corner to its +X +Z corner. 0 where there's none.
        /// </summary>
        public static float LegacyHeightAt(TownDto town, Vec2 point)
        {
            if (!TryLegacyGrid(town, out var cells, out var heights)) return 0f;
            var spacing = town.Size / cells;
            var half = town.Size * 0.5f;
            var fx = (point.X + half) / spacing;
            var fz = (point.Z + half) / spacing;
            if (!(fx >= 0f && fz >= 0f && fx <= cells && fz <= cells)) return 0f;

            var i = Math.Min((int)fx, cells - 1);
            var j = Math.Min((int)fz, cells - 1);
            var u = fx - i;
            var v = fz - j;
            var n = cells + 1;
            var h00 = heights[j * n + i] * 0.01f;
            var h10 = heights[j * n + i + 1] * 0.01f;
            var h01 = heights[(j + 1) * n + i] * 0.01f;
            var h11 = heights[(j + 1) * n + i + 1] * 0.01f;
            return u >= v
                ? h00 + u * (h10 - h00) + v * (h11 - h10)
                : h00 + v * (h01 - h00) + u * (h11 - h01);
        }

        /// <summary>The lowest of an old town's heightmap under a footprint (where its building stood).</summary>
        public static float LegacyBaseHeight(TownDto town, in OrientedRect footprint)
        {
            var lowest = float.MaxValue;
            var ax = footprint.AxisX * footprint.HalfWidth;
            var az = footprint.AxisZ * footprint.HalfDepth;
            for (var u = -1f; u <= 1.001f; u += 0.25f)
            {
                for (var v = -1f; v <= 1.001f; v += 0.25f) lowest = Math.Min(lowest, LegacyHeightAt(town, footprint.Center + ax * u + az * v));
            }

            return lowest;
        }

        private static bool TryLegacyGrid(TownDto town, out int cells, out List<int> heights)
        {
            var terrain = town?.Terrain;
            cells = terrain?.Cells ?? 0;
            heights = terrain?.Heights;
            return terrain != null && cells > 0 && heights != null && heights.Count == (cells + 1) * (cells + 1);
        }
    }
}

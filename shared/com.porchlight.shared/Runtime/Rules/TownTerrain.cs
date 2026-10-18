using System;
using System.Collections.Generic;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// The shape of the ground. Heights sit on a lattice that lines up with the building grid, and each
    /// cell between lattice points is two flat triangles, split from its -X -Z corner to its +X +Z corner.
    /// The renderer draws exactly those triangles, so <see cref="HeightAt"/> is the height you see.
    ///
    /// Shaping keeps three things true, so there's always somewhere sensible to build and walk: no slope
    /// between neighbouring points is steeper than <see cref="MaxSlope"/>, the edge of the town stays at 0
    /// to meet the countryside, and heights stay between <see cref="MinHeight"/> and <see cref="MaxHeight"/>.
    /// </summary>
    public static class TownTerrain
    {
        /// <summary>Rise over run. 0.3 is about 17°: easy to walk up, and gentle enough to look like hills.</summary>
        public const float MaxSlope = 0.3f;

        public const float MinHeight = -8f;
        public const float MaxHeight = 30f;

        /// <summary>How far one click of the terrain tool raises or lowers the ground, in metres.</summary>
        public const float Step = 1f;

        /// <summary>The largest level pad the terrain tool makes, as half its side length.</summary>
        public const float MaxPadHalfSize = 32f;

        /// <summary>
        /// How uneven the ground under a building may be. Buildings stand on the lowest point under them,
        /// so this much of the plinth can disappear into the ground on the high side.
        /// </summary>
        public const float LevelTolerance = 0.3f;

        /// <summary>Neighbouring heights may differ by this many centimetres more than the slope allows, for rounding.</summary>
        private const int RoundingSlack = 2;

        /// <summary>Distance between the points sampled along a footprint's edges when finding its lowest and highest ground.</summary>
        private const float EdgeSampleStep = 0.5f;

        public static int CellsFor(float townSize) => Math.Max(1, (int)MathF.Round(townSize / TownGrid.Spacing));

        /// <summary>Distance between lattice points: the grid spacing, stretched slightly if the town size isn't a multiple of it.</summary>
        public static float Spacing(float townSize) => townSize / CellsFor(townSize);

        /// <summary>Height of the ground at a point. Flat towns, and everywhere outside the town, are at 0.</summary>
        public static float HeightAt(TownDto town, Vec2 point)
        {
            if (!TryGrid(town, out var cells, out var heights)) return 0f;

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

        /// <summary>Height of lattice point (i, j), counting from the -X -Z corner.</summary>
        public static float HeightAt(TownDto town, int i, int j)
        {
            if (!TryGrid(town, out var cells, out var heights) || i < 0 || j < 0 || i > cells || j > cells) return 0f;
            return heights[j * (cells + 1) + i] * 0.01f;
        }

        /// <summary>True when the town has a usable heightmap. Flat towns (and malformed ones) don't.</summary>
        public static bool IsShaped(TownDto town) => TryGrid(town, out _, out _);

        /// <summary>The lowest and highest ground under a rectangle.</summary>
        public static void Range(TownDto town, in OrientedRect rect, out float min, out float max)
        {
            if (!TryGrid(town, out var cells, out _))
            {
                min = max = 0f;
                return;
            }

            var lo = float.MaxValue;
            var hi = float.MinValue;

            // The ground is flat between lattice points and the lines joining them, so the extremes
            // are at lattice points inside the rectangle or somewhere on its edges.
            var ax = rect.AxisX * rect.HalfWidth;
            var az = rect.AxisZ * rect.HalfDepth;
            var corners = new[] { rect.Center - ax - az, rect.Center + ax - az, rect.Center + ax + az, rect.Center - ax + az };
            for (var k = 0; k < 4; k++)
            {
                var a = corners[k];
                var b = corners[(k + 1) % 4];
                var steps = Math.Max(1, (int)MathF.Ceiling(Vec2.Distance(a, b) / EdgeSampleStep));
                for (var s = 0; s < steps; s++)
                {
                    var h = HeightAt(town, a + (b - a) * ((float)s / steps));
                    lo = Math.Min(lo, h);
                    hi = Math.Max(hi, h);
                }
            }

            var spacing = town.Size / cells;
            var half = town.Size * 0.5f;
            var reachX = rect.ExtentAlong(new Vec2(1f, 0f));
            var reachZ = rect.ExtentAlong(new Vec2(0f, 1f));
            var i0 = Math.Max(0, (int)MathF.Ceiling((rect.Center.X - reachX + half) / spacing));
            var i1 = Math.Min(cells, (int)MathF.Floor((rect.Center.X + reachX + half) / spacing));
            var j0 = Math.Max(0, (int)MathF.Ceiling((rect.Center.Z - reachZ + half) / spacing));
            var j1 = Math.Min(cells, (int)MathF.Floor((rect.Center.Z + reachZ + half) / spacing));
            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    if (!rect.Contains(new Vec2(-half + i * spacing, -half + j * spacing))) continue;
                    var h = HeightAt(town, i, j);
                    lo = Math.Min(lo, h);
                    hi = Math.Max(hi, h);
                }
            }

            min = lo;
            max = hi;
        }

        /// <summary>Whether the ground under a rectangle is level enough to build on.</summary>
        public static bool IsLevel(TownDto town, in OrientedRect rect, float tolerance = LevelTolerance)
        {
            Range(town, rect, out var min, out var max);
            return max - min <= tolerance;
        }

        /// <summary>The height a building stands at: the lowest ground under its footprint.</summary>
        public static float BaseHeight(TownDto town, in OrientedRect footprint)
        {
            Range(town, footprint, out var min, out _);
            return min;
        }

        public static float BaseHeight(TownDto town, BuildingDto building) =>
            TownRules.TryFootprint(building, out var footprint) ? BaseHeight(town, footprint) : HeightAt(town, new Vec2(building.X, building.Z));

        /// <summary>
        /// The terrain tool. Levels a square pad around <paramref name="center"/> to the height there plus
        /// <paramref name="delta"/>, and slopes the ground around it at <see cref="MaxSlope"/> to meet the
        /// existing ground, so the pad is ready to build on. Returns the new heights; the town is unchanged.
        /// </summary>
        public static List<int> Shape(TownDto town, Vec2 center, float padHalfSize, float delta)
        {
            var cells = CellsFor(town.Size);
            var spacing = town.Size / cells;
            var half = town.Size * 0.5f;
            var n = cells + 1;
            var hasOld = TryGrid(town, out _, out var old);
            var target = Math.Clamp(HeightAt(town, center) + delta, MinHeight, MaxHeight);

            var heights = new List<int>(n * n);
            for (var j = 0; j <= cells; j++)
            {
                for (var i = 0; i <= cells; i++)
                {
                    var h = hasOld ? old[j * n + i] * 0.01f : 0f;

                    // Within reach of the pad, the ground can't be further from the target than the
                    // slope allows. Clamping both ways levels the pad and ramps the ground around it.
                    var dx = Math.Max(MathF.Abs(-half + i * spacing - center.X) - padHalfSize, 0f);
                    var dz = Math.Max(MathF.Abs(-half + j * spacing - center.Z) - padHalfSize, 0f);
                    var reach = MaxSlope * MathF.Sqrt(dx * dx + dz * dz);
                    h = Math.Clamp(h, target - reach, target + reach);

                    // The same rule pins the edge of town to 0.
                    var edge = MaxSlope * spacing * Math.Min(Math.Min(i, cells - i), Math.Min(j, cells - j));
                    h = Math.Clamp(h, -edge, edge);

                    heights.Add((int)MathF.Round(Math.Clamp(h, MinHeight, MaxHeight) * 100f));
                }
            }

            return heights;
        }

        /// <summary>The server's check of a saved heightmap. Returns false if it's unusable.</summary>
        internal static bool Validate(TownDto town, ValidationResult result)
        {
            var terrain = town.Terrain;
            if (terrain == null) return true;

            var cells = CellsFor(town.Size);
            var n = cells + 1;
            if (terrain.Cells != cells || terrain.Heights == null || terrain.Heights.Count != n * n)
            {
                result.Add($"Terrain must be {cells} cells a side with {n * n} heights");
                return false;
            }

            var heights = terrain.Heights;
            var maxStep = (int)MathF.Ceiling(MaxSlope * (town.Size / cells) * 100f) + RoundingSlack;
            for (var j = 0; j <= cells; j++)
            {
                for (var i = 0; i <= cells; i++)
                {
                    var h = heights[j * n + i];
                    if (h < MinHeight * 100f || h > MaxHeight * 100f)
                    {
                        result.Add($"Terrain heights must be between {MinHeight} and {MaxHeight} m");
                        return false;
                    }

                    if ((i == 0 || j == 0 || i == cells || j == cells) && h != 0)
                    {
                        result.Add("Terrain must be at 0 along the edge of the town");
                        return false;
                    }

                    if ((i > 0 && Math.Abs(h - heights[j * n + i - 1]) > maxStep) || (j > 0 && Math.Abs(h - heights[(j - 1) * n + i]) > maxStep))
                    {
                        result.Add("Terrain is too steep");
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryGrid(TownDto town, out int cells, out List<int> heights)
        {
            var terrain = town.Terrain;
            cells = terrain?.Cells ?? 0;
            heights = terrain?.Heights;
            return terrain != null && cells == CellsFor(town.Size) && heights != null && heights.Count == (cells + 1) * (cells + 1);
        }
    }
}

using System;
using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    public enum FoliageKind
    {
        Tree,
        Bush,
        TallFlower,
        MediumFlower,
        SmallFlower,
        Grass,
    }

    /// <summary>One plant: what it is, which of that kind's looks, where, turned and sized how.</summary>
    public readonly struct FoliagePlant
    {
        public FoliagePlant(FoliageKind kind, int variant, Vec2 position, float rotationDeg, float scale)
        {
            Kind = kind;
            Variant = variant;
            Position = position;
            RotationDeg = rotationDeg;
            Scale = scale;
        }

        public FoliageKind Kind { get; }
        public int Variant { get; }
        public Vec2 Position { get; }
        public float RotationDeg { get; }
        public float Scale { get; }
    }

    /// <summary>
    /// Grass, flowers and trees, grown in the areas painted in the builder (<see cref="TownDto.FoliageAreas"/>)
    /// and on garden tiles.
    /// No plant is stored: the areas, the town's layout and its <see cref="TownDto.Foliage"/> amounts
    /// decide everything, and the same town always grows the same plants. One spot is tried every
    /// <see cref="Spacing"/> metres (nudged randomly) inside the areas, and what grows there depends on
    /// where it is:
    /// <list type="bullet">
    /// <item>Garden tiles: flower beds and the odd bush, whether or not an area's painted there.</item>
    /// <item>Along the top and foot of retaining walls: shrubs and long grass soften the stone.</item>
    /// <item>Right around houses, where there's room: flower beds and the odd bush. A little further out, trees and bushes.</item>
    /// <item>Along streets, paths and plazas: a verge of grass and small flowers.</item>
    /// <item>Open ground: sparse trees, bushes, grass and wildflowers.</item>
    /// </list>
    /// Nothing grows on paving, under buildings, or outside the town. Trees and bushes keep clear of
    /// walls and paving so they don't block them.
    /// </summary>
    public static class Foliage
    {
        /// <summary>Metres between the spots where a plant might grow.</summary>
        public const float Spacing = 2f;

        /// <summary>How many looks each kind has, in <see cref="FoliageKind"/> order.</summary>
        public static readonly int[] Variants = { 10, 10, 10, 10, 10, 4 };

        /// <summary>The most plants a town grows, however big and leafy, so walking stays smooth.</summary>
        public const int MaxPlants = 12000;

        private const float FlowerBed = 3.5f;
        private const float Garden = 10f;
        private const float Verge = 2.5f;

        public static List<FoliagePlant> Plan(TownDto town)
        {
            var plants = new List<FoliagePlant>();
            var amounts = town.Foliage ?? new FoliageDto();
            if (amounts.Overall <= 0f || !TownTiles.Has(town)) return plants;
            var areas = town.FoliageAreas ?? new List<FoliageAreaDto>();

            // Only the spots within reach of a painted area or a garden are worth trying.
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            void Include(float x0, float z0, float x1, float z1)
            {
                minX = Math.Min(minX, x0);
                minZ = Math.Min(minZ, z0);
                maxX = Math.Max(maxX, x1);
                maxZ = Math.Max(maxZ, z1);
            }

            foreach (var area in areas)
            {
                if (area != null && !area.Erase) Include(area.X0, area.Z0, area.X1, area.Z1);
            }

            var n = TownTiles.Cells(town);
            var tile = TownTiles.Spacing(town);
            for (var tj = 0; tj < n; tj++)
            {
                for (var ti = 0; ti < n; ti++)
                {
                    if (TownTiles.Surface(town, ti, tj) != TileSurface.Garden) continue;
                    var c = TownTiles.Center(town, ti, tj);
                    Include(c.X - tile * 0.5f, c.Z - tile * 0.5f, c.X + tile * 0.5f, c.Z + tile * 0.5f);
                }
            }

            if (minX > maxX) return plants;

            var footprints = new List<OrientedRect>();
            foreach (var building in town.Buildings)
            {
                if (TownRules.TryFootprint(building, out var footprint)) footprints.Add(footprint);
            }

            var seed = Hash(town.Id ?? "");
            var half = town.Size * 0.5f;
            var count = (int)(town.Size / Spacing);
            var i0 = Math.Max(0, (int)MathF.Floor((minX + half) / Spacing));
            var i1 = Math.Min(count - 1, (int)MathF.Floor((maxX + half) / Spacing));
            var j0 = Math.Max(0, (int)MathF.Floor((minZ + half) / Spacing));
            var j1 = Math.Min(count - 1, (int)MathF.Floor((maxZ + half) / Spacing));
            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    var h = Hash(seed, i, j);
                    var p = new Vec2(-half + (i + Unit(h, 0)) * Spacing, -half + (j + Unit(h, 1)) * Spacing);
                    if (!TownTiles.TryCell(town, p, out var ti, out var tj)) continue;
                    var garden = TownTiles.Surface(town, ti, tj) == TileSurface.Garden;
                    if (!garden && !Grows(areas, p)) continue;
                    if (!Choose(town, footprints, amounts, p, ti, tj, Unit(h, 2), out var kind)) continue;

                    var variant = (int)(Unit(h, 3) * Variants[(int)kind]) % Variants[(int)kind];
                    plants.Add(new FoliagePlant(kind, variant, p, Unit(h, 4) * 360f, 0.8f + Unit(h, 5) * 0.45f));
                    if (plants.Count >= MaxPlants) return plants;
                }
            }

            return plants;
        }

        /// <summary>What grows at a spot on tile (ti, tj), if anything, given a random number <paramref name="roll"/> in [0, 1).</summary>
        private static bool Choose(TownDto town, List<OrientedRect> footprints, FoliageDto amounts, Vec2 p, int ti, int tj, float roll, out FoliageKind kind)
        {
            kind = FoliageKind.Grass;
            var surface = TownTiles.Surface(town, ti, tj);
            if (TownTiles.IsPaved(surface)) return false;

            var toHouse = float.MaxValue;
            foreach (var footprint in footprints) toHouse = Math.Min(toHouse, Distance(footprint, p));
            if (toHouse < 0.5f) return false;

            // How close the paving and the retaining walls are: the edges this tile shares with a paved
            // tile, or with a tile at another level.
            var size = TownTiles.Spacing(town);
            var center = TownTiles.Center(town, ti, tj);
            var level = TownTiles.Level(town, ti, tj);
            float toPaving = float.MaxValue, toWall = float.MaxValue;
            for (var side = 0; side < 4; side++)
            {
                var (di, dj) = TownTiles.Neighbours[side];
                var edge = size * 0.5f - Math.Abs(Vec2.Dot(p - center, new Vec2(di, dj)) );
                if (Vec2.Dot(p - center, new Vec2(di, dj)) < 0f) edge = size;
                if (TownTiles.IsPaved(TownTiles.Surface(town, ti + di, tj + dj))) toPaving = Math.Min(toPaving, edge);
                if (TownTiles.Level(town, ti + di, tj + dj) != level) toWall = Math.Min(toWall, edge);
            }

            if (toPaving < 0.3f) return false;

            // Chances per spot, before the amounts.
            float tree, bush, tall, medium, small, grass;
            if (surface == TileSurface.Garden)
            {
                (tree, bush, tall, medium, small, grass) = (0f, 0.06f, 0.22f, 0.26f, 0.24f, 0.04f);
            }
            else if (toWall < 1.2f)
            {
                // Along the top and foot of retaining walls: shrubs and long grass soften the stone.
                (tree, bush, tall, medium, small, grass) = (0f, 0.22f, 0.04f, 0.06f, 0.06f, 0.4f);
            }
            else if (toHouse < FlowerBed && toPaving > 1f)
            {
                (tree, bush, tall, medium, small, grass) = (0f, 0.07f, 0.12f, 0.16f, 0.14f, 0.06f);
            }
            else if (toPaving < Verge)
            {
                (tree, bush, tall, medium, small, grass) = (0f, 0f, 0.01f, 0.04f, 0.12f, 0.35f);
            }
            else if (toHouse < Garden)
            {
                (tree, bush, tall, medium, small, grass) = (0.05f, 0.06f, 0.02f, 0.03f, 0.04f, 0.12f);
            }
            else
            {
                (tree, bush, tall, medium, small, grass) = (0.025f, 0.025f, 0.008f, 0.015f, 0.03f, 0.14f);
            }

            // Trees and bushes need room to stand clear of walls, paving and houses.
            if (toHouse < 4f || toPaving < 3f || toWall < 2f) tree = 0f;
            if (toHouse < 1.5f || toPaving < 1.5f) bush = 0f;

            var overall = amounts.Overall;
            var chances = new[]
            {
                tree * amounts.Trees * overall, bush * amounts.Trees * overall,
                tall * amounts.Flowers * overall, medium * amounts.Flowers * overall, small * amounts.Flowers * overall,
                grass * amounts.Grass * overall,
            };

            // Heavier settings can't fill more than nearly every spot.
            var total = 0f;
            foreach (var chance in chances) total += chance;
            var scale = total > 0.95f ? 0.95f / total : 1f;
            var running = 0f;
            for (var k = 0; k < chances.Length; k++)
            {
                running += chances[k] * scale;
                if (roll >= running) continue;
                kind = (FoliageKind)k;
                return true;
            }

            return false;
        }

        /// <summary>Whether the last area covering a point was painted (not erased).</summary>
        public static bool Grows(List<FoliageAreaDto> areas, Vec2 p)
        {
            for (var k = areas.Count - 1; k >= 0; k--)
            {
                var area = areas[k];
                if (area != null && area.Contains(p.X, p.Z)) return !area.Erase;
            }

            return false;
        }

        /// <summary>How far a point is from a building's walls; 0 inside.</summary>
        private static float Distance(in OrientedRect rect, Vec2 p)
        {
            var local = p - rect.Center;
            var x = Math.Max(MathF.Abs(Vec2.Dot(local, rect.AxisX)) - rect.HalfWidth, 0f);
            var z = Math.Max(MathF.Abs(Vec2.Dot(local, rect.AxisZ)) - rect.HalfDepth, 0f);
            return MathF.Sqrt(x * x + z * z);
        }

        private static uint Hash(string text)
        {
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }

        private static uint Hash(uint seed, int i, int j)
        {
            var h = seed ^ (uint)i * 73856093u ^ (uint)j * 19349663u;
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h;
        }

        /// <summary>The <paramref name="index"/>th random number in [0, 1) from a spot's hash.</summary>
        private static float Unit(uint hash, int index)
        {
            var h = Hash(hash, index, 7919);
            return (h >> 8) / 16777216f;
        }
    }
}

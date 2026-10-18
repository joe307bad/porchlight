using System;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// The ground's shape (<see cref="TownTerrain"/>) as meshes, plus finding the ground under a ray.
    /// The grass, the grid, roads and the builder's ghosts all lie on the ground through here, so
    /// they follow the same triangles.
    /// </summary>
    public static class TerrainMeshes
    {
        /// <summary>Roads and outlines are cut into pieces about this long so they bend with the ground.</summary>
        private const float DrapeStep = 2f;

        /// <summary>How far a ray steps between ground checks before narrowing in on the hit.</summary>
        private const float RayStep = 0.5f;

        /// <summary>
        /// The town's ground as faceted triangles, two per cell, lifted by <paramref name="lift"/>.
        /// <paramref name="colorOf"/> colours each cell by the position of its -X -Z corner.
        /// </summary>
        public static void Surface(MeshBuilder mb, TownDto town, float lift, Func<float, float, Color> colorOf)
        {
            var cells = TownTerrain.CellsFor(town.Size);
            var spacing = town.Size / cells;
            var half = town.Size * 0.5f;
            for (var j = 0; j < cells; j++)
            {
                for (var i = 0; i < cells; i++)
                {
                    float x0 = -half + i * spacing, z0 = -half + j * spacing, x1 = x0 + spacing, z1 = z0 + spacing;
                    var h00 = TownTerrain.HeightAt(town, i, j) + lift;
                    var h10 = TownTerrain.HeightAt(town, i + 1, j) + lift;
                    var h01 = TownTerrain.HeightAt(town, i, j + 1) + lift;
                    var h11 = TownTerrain.HeightAt(town, i + 1, j + 1) + lift;
                    var a = new Vector3(x0, h00, z0);
                    var b = new Vector3(x1, h10, z0);
                    var c = new Vector3(x1, h11, z1);
                    var d = new Vector3(x0, h01, z1);
                    var color = colorOf(x0, z0);

                    // Same split as TownTerrain.HeightAt: corner a to corner c. A flat cell is one quad.
                    if (Mathf.Abs(h00 + h11 - h10 - h01) < 0.001f)
                    {
                        mb.Quad(a, b, c, d, color, Vector3.up);
                    }
                    else
                    {
                        mb.Triangle(a, b, c, color, Vector3.up);
                        mb.Triangle(a, c, d, color, Vector3.up);
                    }
                }
            }
        }

        /// <summary>A point on the ground, lifted by <paramref name="lift"/>.</summary>
        public static Vector3 OnGround(TownDto town, Vec2 point, float lift = 0f) =>
            new Vector3(point.X, TownTerrain.HeightAt(town, point) + lift, point.Z);

        /// <summary>A flat strip from <paramref name="a"/> to <paramref name="b"/> lying on the ground, in pieces that follow its shape.</summary>
        public static void Strip(MeshBuilder mb, TownDto town, Vec2 a, Vec2 b, float width, Color color, float lift)
        {
            var length = Vec2.Distance(a, b);
            if (length < 1e-3f) return;

            var direction = (b - a) * (1f / length);
            var side = new Vec2(direction.Z, -direction.X) * (width * 0.5f);
            var shaped = TownTerrain.IsShaped(town);
            var along = shaped ? Mathf.Max(1, Mathf.CeilToInt(length / DrapeStep)) : 1;
            var across = shaped ? Mathf.Max(1, Mathf.CeilToInt(width / DrapeStep)) : 1;

            Vector3 At(int s, int k) => OnGround(town, a + (b - a) * ((float)s / along) + side * (2f * k / across - 1f), lift);

            for (var s = 0; s < along; s++)
            {
                for (var k = 0; k < across; k++)
                {
                    mb.Quad(At(s, k), At(s + 1, k), At(s + 1, k + 1), At(s, k + 1), color, Vector3.up);
                }
            }
        }

        /// <summary>A flat disc lying on the ground, to fill road joints.</summary>
        public static void Disc(MeshBuilder mb, TownDto town, Vec2 center, float radius, Color color, float lift)
        {
            const int sides = 10;
            var middle = OnGround(town, center, lift);
            for (var i = 0; i < sides; i++)
            {
                var a0 = i * Mathf.PI * 2f / sides;
                var a1 = (i + 1) * Mathf.PI * 2f / sides;
                mb.Triangle(middle,
                    OnGround(town, center + new Vec2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius, lift),
                    OnGround(town, center + new Vec2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius, lift),
                    color, Vector3.up);
            }
        }

        /// <summary>The outline of an axis-aligned square on the ground, <paramref name="halfSize"/> from its middle to each side.</summary>
        public static void SquareOutline(MeshBuilder mb, TownDto town, Vec2 center, float halfSize, float lineWidth, Color color, float lift)
        {
            var reach = halfSize + lineWidth * 0.5f;
            var corners = new[]
            {
                center + new Vec2(-reach, -halfSize), center + new Vec2(reach, -halfSize),
                center + new Vec2(halfSize, -reach), center + new Vec2(halfSize, reach),
                center + new Vec2(reach, halfSize), center + new Vec2(-reach, halfSize),
                center + new Vec2(-halfSize, reach), center + new Vec2(-halfSize, -reach),
            };

            for (var i = 0; i < corners.Length; i += 2)
            {
                Strip(mb, town, corners[i], corners[i + 1], lineWidth, color, lift);
            }
        }

        /// <summary>Which way the ground faces at a point.</summary>
        public static Vector3 Normal(TownDto town, Vec2 point)
        {
            const float d = 0.5f;
            var dx = TownTerrain.HeightAt(town, point + new Vec2(d, 0f)) - TownTerrain.HeightAt(town, point - new Vec2(d, 0f));
            var dz = TownTerrain.HeightAt(town, point + new Vec2(0f, d)) - TownTerrain.HeightAt(town, point - new Vec2(0f, d));
            return new Vector3(-dx, 2f * d, -dz).normalized;
        }

        /// <summary>
        /// Where a ray first meets the ground: the terrain inside the town, or y = 0 outside it.
        /// Steps along the ray through the band of heights the ground can have, then narrows in.
        /// </summary>
        public static bool Raycast(TownDto town, Ray ray, out Vector3 hit)
        {
            hit = default;
            var direction = ray.direction.normalized;
            if (town == null || !TownTerrain.IsShaped(town))
            {
                if (direction.y > -1e-4f) return false;
                hit = ray.origin + direction * (-ray.origin.y / direction.y);
                return true;
            }

            float Above(float t)
            {
                var p = ray.origin + direction * t;
                return p.y - TownTerrain.HeightAt(town, new Vec2(p.x, p.z));
            }

            // Only the stretch of the ray between the highest and lowest the ground can be matters.
            var start = 0f;
            var end = 2000f;
            if (direction.y < -1e-4f)
            {
                start = Mathf.Max(0f, (ray.origin.y - TownTerrain.MaxHeight) / -direction.y);
                end = Mathf.Min(end, (ray.origin.y - TownTerrain.MinHeight) / -direction.y);
            }
            else if (ray.origin.y > TownTerrain.MaxHeight)
            {
                return false;
            }

            if (Above(start) <= 0f) return false;

            var previous = start;
            for (var t = start + RayStep; t <= end + RayStep; t += RayStep)
            {
                if (Above(t) > 0f)
                {
                    previous = t;
                    continue;
                }

                var lo = previous;
                var hi = t;
                for (var k = 0; k < 12; k++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (Above(mid) > 0f) lo = mid;
                    else hi = mid;
                }

                hit = ray.origin + direction * hi;
                return true;
            }

            return false;
        }
    }
}

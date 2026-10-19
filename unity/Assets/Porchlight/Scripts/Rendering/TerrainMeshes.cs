using System;
using System.Collections.Generic;
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
        /// <summary>How far a ray steps between ground checks before narrowing in on the hit.</summary>
        private const float RayStep = 0.5f;

        /// <summary>
        /// The town's ground as faceted triangles, two per cell, lifted by <paramref name="lift"/>.
        /// <paramref name="colorOf"/> colours each cell by the position of its -X -Z corner. Triangles
        /// steeper than the terrain tool makes (the banks beside levelled buildings) get <paramref name="bank"/>, if given.
        /// </summary>
        public static void Surface(MeshBuilder mb, TownDto town, float lift, Func<float, float, Color> colorOf, Color? bank = null)
        {
            // A face's normal points less upwards the steeper it is; this is MaxSlope with a little slack.
            var steepY = 1f / Mathf.Sqrt(1f + TownTerrain.MaxSlope * TownTerrain.MaxSlope * 1.5f);
            Color Shade(Vector3 p, Vector3 q, Vector3 r, Color color)
            {
                if (!bank.HasValue) return color;
                var upness = Mathf.Abs(Vector3.Cross(q - p, r - p).normalized.y);
                return upness < steepY ? bank.Value : color;
            }

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
                        mb.Quad(a, b, c, d, Shade(a, b, c, color), Vector3.up);
                    }
                    else
                    {
                        mb.Triangle(a, b, c, Shade(a, b, c, color), Vector3.up);
                        mb.Triangle(a, c, d, Shade(a, c, d, color), Vector3.up);
                    }
                }
            }
        }

        /// <summary>A point on the ground, lifted by <paramref name="lift"/>.</summary>
        public static Vector3 OnGround(TownDto town, Vec2 point, float lift = 0f) =>
            new Vector3(point.X, TownTerrain.HeightAt(town, point) + lift, point.Z);

        /// <summary>
        /// A flat strip from <paramref name="a"/> to <paramref name="b"/> lying on the ground, with
        /// <paramref name="cutouts"/> (building footprints) cut out of it.
        /// </summary>
        public static void Strip(MeshBuilder mb, TownDto town, Vec2 a, Vec2 b, float width, Color color, float lift, List<OrientedRect> cutouts = null)
        {
            var length = Vec2.Distance(a, b);
            if (length < 1e-3f) return;

            var direction = (b - a) * (1f / length);
            var side = new Vec2(direction.Z, -direction.X) * (width * 0.5f);
            DrapeAround(mb, town, new List<Vec2> { a - side, b - side, b + side, a + side }, cutouts, color, lift);
        }

        /// <summary>A flat disc lying on the ground, to fill road joints, with <paramref name="cutouts"/> cut out of it.</summary>
        public static void Disc(MeshBuilder mb, TownDto town, Vec2 center, float radius, Color color, float lift, List<OrientedRect> cutouts = null)
        {
            const int sides = 10;
            var outline = new List<Vec2>(sides);
            for (var i = 0; i < sides; i++)
            {
                var angle = i * Mathf.PI * 2f / sides;
                outline.Add(center + new Vec2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
            }

            DrapeAround(mb, town, outline, cutouts, color, lift);
        }

        /// <summary>Drapes a convex polygon with rectangles cut out of it, so roads meet buildings at their walls.</summary>
        private static void DrapeAround(MeshBuilder mb, TownDto town, List<Vec2> polygon, List<OrientedRect> cutouts, Color color, float lift)
        {
            var pieces = new List<List<Vec2>> { polygon };
            if (cutouts != null)
            {
                foreach (var rect in cutouts) pieces = Subtract(pieces, rect);
            }

            foreach (var piece in pieces) Drape(mb, town, piece, color, lift);
        }

        /// <summary>
        /// Convex pieces minus a rectangle, still as convex pieces: for each side of the rectangle in turn,
        /// the part beyond that side but within the sides before it.
        /// </summary>
        private static List<List<Vec2>> Subtract(List<List<Vec2>> pieces, in OrientedRect rect)
        {
            var ax = rect.AxisX * rect.HalfWidth;
            var az = rect.AxisZ * rect.HalfDepth;
            var corners = new[] { rect.Center - ax - az, rect.Center + ax - az, rect.Center + ax + az, rect.Center - ax + az };

            // Wind the corners anticlockwise seen from above, so "inside" is on the left of each side.
            var turn = (corners[1].X - corners[0].X) * (corners[2].Z - corners[0].Z) - (corners[1].Z - corners[0].Z) * (corners[2].X - corners[0].X);
            if (turn < 0f) Array.Reverse(corners);

            var result = new List<List<Vec2>>();
            foreach (var piece in pieces)
            {
                if (!Touches(piece, rect))
                {
                    result.Add(piece);
                    continue;
                }

                var inside = piece;
                for (var k = 0; k < 4 && inside.Count >= 3; k++)
                {
                    Vec2 a = corners[k], b = corners[(k + 1) % 4];
                    var outside = ClipEdge(inside, b, a);
                    if (outside.Count >= 3) result.Add(outside);
                    inside = ClipEdge(inside, a, b);
                }
            }

            return result;
        }

        private static bool Touches(List<Vec2> polygon, in OrientedRect rect)
        {
            var reach = rect.BoundingRadius;
            foreach (var p in polygon)
            {
                if (Vec2.Distance(p, rect.Center) <= reach) return true;
            }

            // A polygon can cross a rectangle without a corner near it, so also check the edges.
            for (var k = 0; k < polygon.Count; k++)
            {
                if (Segments.Distance(rect.Center, polygon[k], polygon[(k + 1) % polygon.Count]) <= reach) return true;
            }

            return false;
        }

        /// <summary>
        /// Lays a convex polygon on the ground. It's cut along the edges of the ground's triangles, so
        /// every piece lies flat on one triangle and the ground never pokes through. Beyond the edge of
        /// the town the same lattice carries on at height 0.
        /// </summary>
        public static void Drape(MeshBuilder mb, TownDto town, List<Vec2> polygon, Color color, float lift)
        {
            if (polygon.Count < 3) return;
            if (!TownTerrain.IsShaped(town))
            {
                Fan(mb, town, polygon, color, lift);
                return;
            }

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in polygon)
            {
                minX = Mathf.Min(minX, p.X);
                maxX = Mathf.Max(maxX, p.X);
                minZ = Mathf.Min(minZ, p.Z);
                maxZ = Mathf.Max(maxZ, p.Z);
            }

            var spacing = town.Size / TownTerrain.CellsFor(town.Size);
            var half = town.Size * 0.5f;
            var i0 = Mathf.FloorToInt((minX + half) / spacing);
            var i1 = Mathf.FloorToInt((maxX + half) / spacing);
            var j0 = Mathf.FloorToInt((minZ + half) / spacing);
            var j1 = Mathf.FloorToInt((maxZ + half) / spacing);
            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    var c00 = new Vec2(-half + i * spacing, -half + j * spacing);
                    var c10 = c00 + new Vec2(spacing, 0f);
                    var c11 = c00 + new Vec2(spacing, spacing);
                    var c01 = c00 + new Vec2(0f, spacing);

                    // The same two triangles as Surface, both wound anticlockwise seen from above.
                    var lower = Clip(polygon, c00, c10, c11);
                    if (lower != null) Fan(mb, town, lower, color, lift);
                    var upper = Clip(polygon, c00, c11, c01);
                    if (upper != null) Fan(mb, town, upper, color, lift);
                }
            }
        }

        /// <summary>The part of a convex polygon inside the triangle abc (anticlockwise from above), or null.</summary>
        private static List<Vec2> Clip(List<Vec2> polygon, Vec2 a, Vec2 b, Vec2 c)
        {
            var result = polygon;
            result = ClipEdge(result, a, b);
            if (result.Count >= 3) result = ClipEdge(result, b, c);
            if (result.Count >= 3) result = ClipEdge(result, c, a);
            return result.Count >= 3 ? result : null;
        }

        /// <summary>Keeps the part of the polygon on the left of the line from a to b, seen from above.</summary>
        private static List<Vec2> ClipEdge(List<Vec2> polygon, Vec2 a, Vec2 b)
        {
            var edge = b - a;
            float Side(Vec2 p) => edge.Z * (p.X - a.X) - edge.X * (p.Z - a.Z);

            var kept = new List<Vec2>(polygon.Count + 1);
            for (var k = 0; k < polygon.Count; k++)
            {
                var p = polygon[k];
                var q = polygon[(k + 1) % polygon.Count];
                var sp = Side(p);
                var sq = Side(q);
                if (sp <= 0f) kept.Add(p);
                if ((sp < 0f && sq > 0f) || (sp > 0f && sq < 0f)) kept.Add(p + (q - p) * (sp / (sp - sq)));
            }

            return kept;
        }

        private static void Fan(MeshBuilder mb, TownDto town, List<Vec2> polygon, Color color, float lift)
        {
            var first = OnGround(town, polygon[0], lift);
            for (var k = 1; k + 1 < polygon.Count; k++)
            {
                mb.Triangle(first, OnGround(town, polygon[k], lift), OnGround(town, polygon[k + 1], lift), color, Vector3.up);
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

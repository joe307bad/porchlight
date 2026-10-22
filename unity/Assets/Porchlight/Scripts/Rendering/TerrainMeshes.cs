using System.Collections.Generic;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Laying things on the ground (<see cref="TownTerrain"/>: the tiles), and finding the ground under
    /// a ray. The builder's ghosts and outlines lie on the tiles through here.
    /// </summary>
    public static class TerrainMeshes
    {
        /// <summary>How far a ray steps between ground checks before narrowing in on the hit.</summary>
        private const float RayStep = 0.5f;

        /// <summary>A point on the ground, lifted by <paramref name="lift"/>.</summary>
        public static Vector3 OnGround(TownDto town, Vec2 point, float lift = 0f) =>
            new Vector3(point.X, TownTerrain.HeightAt(town, point) + lift, point.Z);

        /// <summary>A flat strip from <paramref name="a"/> to <paramref name="b"/> lying on the ground.</summary>
        public static void Strip(MeshBuilder mb, TownDto town, Vec2 a, Vec2 b, float width, Color color, float lift)
        {
            var length = Vec2.Distance(a, b);
            if (length < 1e-3f) return;

            var direction = (b - a) * (1f / length);
            var side = new Vec2(direction.Z, -direction.X) * (width * 0.5f);
            Drape(mb, town, new List<Vec2> { a - side, b - side, b + side, a + side }, color, lift);
        }

        /// <summary>A flat disc lying on the ground.</summary>
        public static void Disc(MeshBuilder mb, TownDto town, Vec2 center, float radius, Color color, float lift)
        {
            const int sides = 10;
            var outline = new List<Vec2>(sides);
            for (var i = 0; i < sides; i++)
            {
                var angle = i * Mathf.PI * 2f / sides;
                outline.Add(center + new Vec2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
            }

            Drape(mb, town, outline, color, lift);
        }

        /// <summary>
        /// Lays a convex polygon on the ground. It's cut along the tiles' edges, so each piece lies on one
        /// tile and steps down its walls with it, and finer on tiles that climb or slope, so pieces follow ramps and hills
        /// and keep to one side of a flight of steps' edges. Beyond the edge of town it lies at 0.
        /// </summary>
        public static void Drape(MeshBuilder mb, TownDto town, List<Vec2> polygon, Color color, float lift)
        {
            if (polygon.Count < 3) return;
            if (!TownTiles.Has(town))
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

            var spacing = TownTiles.Spacing(town);
            var half = town.Size * 0.5f;
            var i0 = Mathf.FloorToInt((minX + half) / spacing);
            var i1 = Mathf.FloorToInt((maxX + half) / spacing);
            var j0 = Mathf.FloorToInt((minZ + half) / spacing);
            var j1 = Mathf.FloorToInt((maxZ + half) / spacing);
            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    // Outside the town, and on flat tiles, one piece a tile; on climbing tiles and hillsides, 0.4 m pieces.
                    var parts = TownTiles.Inside(town, i, j) && (TownTiles.TryClimb(town, i, j, out _) || TownTiles.IsHill(town, i, j)) ? Mathf.Max(1, Mathf.RoundToInt(spacing / 0.4f)) : 1;
                    var step = spacing / parts;
                    for (var b = 0; b < parts; b++)
                    {
                        for (var a = 0; a < parts; a++)
                        {
                            var c00 = new Vec2(-half + i * spacing + a * step, -half + j * spacing + b * step);
                            var piece = Clip(polygon, c00, c00 + new Vec2(step, 0f), c00 + new Vec2(step, step), c00 + new Vec2(0f, step));
                            if (piece != null) Fan(mb, town, piece, color, lift, c00 + new Vec2(step * 0.5f, step * 0.5f));
                        }
                    }
                }
            }
        }

        /// <summary>The part of a convex polygon inside the square abcd (anticlockwise from above), or null.</summary>
        private static List<Vec2> Clip(List<Vec2> polygon, Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            var result = ClipEdge(polygon, a, b);
            if (result.Count >= 3) result = ClipEdge(result, b, c);
            if (result.Count >= 3) result = ClipEdge(result, c, d);
            if (result.Count >= 3) result = ClipEdge(result, d, a);
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

        /// <summary>
        /// A convex piece as a fan of triangles on the ground. Heights are read a hair towards
        /// <paramref name="inside"/> (the middle of the cell it was cut to), so corners on a wall take
        /// this piece's tile's height, not the one below or above.
        /// </summary>
        private static void Fan(MeshBuilder mb, TownDto town, List<Vec2> polygon, Color color, float lift, Vec2? inside = null)
        {
            Vector3 At(Vec2 p)
            {
                var probe = inside.HasValue ? p + (inside.Value - p) * 0.002f : p;
                return new Vector3(p.X, TownTerrain.HeightAt(town, probe) + lift, p.Z);
            }

            var first = At(polygon[0]);
            for (var k = 1; k + 1 < polygon.Count; k++)
            {
                mb.Triangle(first, At(polygon[k]), At(polygon[k + 1]), color, Vector3.up);
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

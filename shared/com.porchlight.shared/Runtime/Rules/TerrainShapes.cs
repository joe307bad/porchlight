using System;
using System.Collections.Generic;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// The terrain builder's shapes and the overlay that drapes the ground over them.
    ///
    /// Each kind is a unit shape that fits in the box from -0.5 to 0.5 on every axis, which a shape's
    /// scale stretches and its rotation turns: a cube that fills the box, a ball touching its sides,
    /// and a triangular prism lying like a tent: two isosceles triangle ends (a right angle at the
    /// ridge at its default scale) and three rectangles, the floor and two slopes meeting in a ridge along Z. The renderer draws
    /// exactly these.
    ///
    /// <see cref="Overlay"/> looks down on the shapes with their overlay switched on and raises the tiles
    /// under each to its top, so the ground takes their form in terraces, keeping clear of buildings.
    /// </summary>
    public static class TerrainShapes
    {
        public const string Prism = "prism";
        public const string Sphere = "sphere";
        public const string Cube = "cube";

        /// <summary>What the triangle was before it became a prism. Old saves still load, as prisms.</summary>
        private const string LegacyPyramid = "pyramid";

        /// <summary>In the order the builder offers them.</summary>
        public static readonly string[] Kinds = { Prism, Sphere, Cube };

        public const float MinScale = 0.5f;
        public const float MaxScale = 200f;

        /// <summary>How low and high a shape's middle may go. Shapes can sit well below or above the ground's own limits.</summary>
        public const float MinY = TownTerrain.MinHeight - 100f;
        public const float MaxY = TownTerrain.MaxHeight + 200f;

        /// <summary>How far outside the town a shape's middle may go.</summary>
        public const float OutsideReach = 50f;


        // Unit prism: an isosceles triangle across X and Y (base along the bottom, apex at the top
        // middle), run from one end of the box to the other along Z. With scale (2h, h, any) the ends
        // are right-angled at the ridge, with 45° slopes.
        private static readonly Vec3[] TriangleCorners =
        {
            new Vec3(-0.5f, -0.5f, 0f),
            new Vec3(0.5f, -0.5f, 0f),
            new Vec3(0f, 0.5f, 0f),
        };

        private static readonly (Vec3 Normal, float Offset)[] CubePlanes =
        {
            (new Vec3(1f, 0f, 0f), 0.5f), (new Vec3(-1f, 0f, 0f), 0.5f),
            (new Vec3(0f, 1f, 0f), 0.5f), (new Vec3(0f, -1f, 0f), 0.5f),
            (new Vec3(0f, 0f, 1f), 0.5f), (new Vec3(0f, 0f, -1f), 0.5f),
        };

        private static readonly (Vec3 Normal, float Offset)[] PrismPlanes =
        {
            (new Vec3(0f, -1f, 0f), 0.5f),
            (new Vec3(0f, 0f, 1f), 0.5f), (new Vec3(0f, 0f, -1f), 0.5f),
            SlopeOf(TriangleCorners[0], TriangleCorners[2], -1f),
            SlopeOf(TriangleCorners[1], TriangleCorners[2], 1f),
        };

        /// <summary>The prism's triangle in unit space (z = 0): bottom left, bottom right, apex. The ends are at z = ±0.5.</summary>
        public static IReadOnlyList<Vec3> UnitTriangle => TriangleCorners;

        public static bool IsKind(string kind) => Canonical(kind) != null;

        /// <summary>The kind as it's stored now (old pyramids are prisms), or null if it isn't one.</summary>
        public static string Canonical(string kind)
        {
            switch (kind)
            {
                case Prism:
                case LegacyPyramid: return Prism;
                case Sphere: return Sphere;
                case Cube: return Cube;
                default: return null;
            }
        }

        public static string DisplayName(string kind)
        {
            switch (Canonical(kind))
            {
                case Prism: return "Triangle";
                case Sphere: return "Sphere";
                case Cube: return "Cube";
                default: return "Shape";
            }
        }

        /// <summary>A sensible size for a new shape: a tent-like ridge, a ball and a block about a building across.</summary>
        public static Vec3 DefaultScale(string kind)
        {
            switch (kind)
            {
                case Prism: return new Vec3(16f, 8f, 20f);
                case Sphere: return new Vec3(14f, 14f, 14f);
                default: return new Vec3(10f, 10f, 10f);
            }
        }

        /// <summary>"Cube 3": the kind and one more than the highest number already used for it.</summary>
        public static string NextName(TownDto town, string kind)
        {
            var prefix = DisplayName(kind) + " ";
            var highest = 0;
            foreach (var shape in town.TerrainShapes)
            {
                if (shape?.Name == null || !shape.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (int.TryParse(shape.Name.Substring(prefix.Length), out var number)) highest = Math.Max(highest, number);
            }

            return prefix + (highest + 1);
        }

        public static Vec3 Center(TerrainShapeDto shape) => new Vec3(shape.X, shape.Y, shape.Z);
        public static Vec3 Scale(TerrainShapeDto shape) => new Vec3(shape.ScaleX, shape.ScaleY, shape.ScaleZ);

        /// <summary>The distance from a shape's middle to the furthest corner of its box.</summary>
        public static float BoundingRadius(TerrainShapeDto shape) => Scale(shape).Length * 0.5f;

        /// <summary>Turns a vector the way Unity's Quaternion.Euler(x, y, z) does: about Z, then X, then Y.</summary>
        public static Vec3 Rotate(Vec3 v, float xDeg, float yDeg, float zDeg) => RotateY(RotateX(RotateZ(v, zDeg), xDeg), yDeg);

        /// <summary>Undoes <see cref="Rotate"/>.</summary>
        public static Vec3 Unrotate(Vec3 v, float xDeg, float yDeg, float zDeg) => RotateZ(RotateX(RotateY(v, -yDeg), -xDeg), -zDeg);

        /// <summary>A point in the shape's unit space moved out to where it is in the world.</summary>
        public static Vec3 ToWorld(TerrainShapeDto shape, Vec3 local) =>
            Center(shape) + Rotate(Vec3.Scale(local, Scale(shape)), shape.RotationX, shape.RotationY, shape.RotationZ);

        /// <summary>
        /// Where a ray first enters a shape, as a distance along <paramref name="direction"/> (in lengths
        /// of it). 0 if the ray starts inside. False if it misses or the shape is behind it.
        /// </summary>
        public static bool Raycast(TerrainShapeDto shape, Vec3 origin, Vec3 direction, out float distance)
        {
            distance = 0f;
            var inverse = new Vec3(1f / shape.ScaleX, 1f / shape.ScaleY, 1f / shape.ScaleZ);
            var o = Vec3.Scale(Unrotate(origin - Center(shape), shape.RotationX, shape.RotationY, shape.RotationZ), inverse);
            var d = Vec3.Scale(Unrotate(direction, shape.RotationX, shape.RotationY, shape.RotationZ), inverse);

            float enter, exit;
            switch (Canonical(shape.Kind))
            {
                case Sphere:
                {
                    var a = Vec3.Dot(d, d);
                    var b = 2f * Vec3.Dot(o, d);
                    var c = Vec3.Dot(o, o) - 0.25f;
                    var discriminant = b * b - 4f * a * c;
                    if (a < 1e-12f || discriminant < 0f) return false;
                    var root = MathF.Sqrt(discriminant);
                    enter = (-b - root) / (2f * a);
                    exit = (-b + root) / (2f * a);
                    break;
                }

                case Prism:
                    if (!Clip(PrismPlanes, o, d, out enter, out exit)) return false;
                    break;

                case Cube:
                    if (!Clip(CubePlanes, o, d, out enter, out exit)) return false;
                    break;

                default:
                    return false;
            }

            if (exit < 0f) return false;
            distance = Math.Max(0f, enter);
            return true;
        }

        /// <summary>The height of a shape's top surface over a point on the ground, if it's over it at all.</summary>
        public static bool TopAt(TerrainShapeDto shape, Vec2 point, out float height)
        {
            // Start just above the shape: a ray from far up loses precision where it meets a curve.
            height = 0f;
            var start = shape.Y + BoundingRadius(shape) + 1f;
            if (!Raycast(shape, new Vec3(point.X, start, point.Z), Vec3.Down, out var distance)) return false;
            height = start - distance;
            return true;
        }

        /// <summary>
        /// The tiles the overlay raises: each tile under a shape with <see cref="TerrainShapeDto.Overlay"/>
        /// on goes up to the shape's top over it (to the nearest metre), so the ground takes the shape's
        /// form in terraces. Tiles under buildings stay put. The town is unchanged.
        /// </summary>
        public static List<TileChange> Overlay(TownDto town)
        {
            var changes = new List<TileChange>();
            if (!TownTiles.Has(town)) return changes;
            var shapes = (town.TerrainShapes ?? new List<TerrainShapeDto>()).FindAll(s => s != null && s.Overlay && IsKind(s.Kind));
            if (shapes.Count == 0) return changes;

            var held = TownTiles.HeldByBuildings(town, null);
            var n = TownTiles.Cells(town);
            var quarter = TownTiles.Spacing(town) * 0.25f;
            var probes = new[] { Vec2.Zero, new Vec2(-quarter, -quarter), new Vec2(quarter, -quarter), new Vec2(-quarter, quarter), new Vec2(quarter, quarter) };
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    var k = TownTiles.Index(town, i, j);
                    if (held.Contains(k)) continue;
                    var center = TownTiles.Center(town, i, j);

                    // The highest top over the tile's middle, or failing that over the rest of it.
                    var top = float.MinValue;
                    foreach (var shape in shapes)
                    {
                        if (Vec2.Distance(center, new Vec2(shape.X, shape.Z)) > BoundingRadius(shape) + TownTiles.Size) continue;
                        foreach (var probe in probes)
                        {
                            if (TopAt(shape, center + probe, out var height)) top = Math.Max(top, height);
                        }
                    }

                    if (top == float.MinValue) continue;
                    var level = Math.Clamp((int)MathF.Round(top), TownTiles.MinLevel, TownTiles.MaxLevel);
                    if (level > town.Tiles.Levels[k]) changes.Add(new TileChange(k, level, (TileSurface)town.Tiles.Surfaces[k]));
                }
            }

            return changes;
        }

        /// <summary>
        /// Rounds a shape to what gets stored: positions to the town's quantum, angles to a tenth of a
        /// degree, scales into range. Returns a new copy.
        /// </summary>
        public static TerrainShapeDto Normalize(TerrainShapeDto shape)
        {
            var copy = shape.Clone();
            copy.Kind = Canonical(shape.Kind) ?? shape.Kind;
            copy.Name = (shape.Name ?? "").Trim();
            if (copy.Name.Length > TownLimits.MaxShapeNameLength) copy.Name = copy.Name.Substring(0, TownLimits.MaxShapeNameLength);
            copy.X = Quantize(shape.X);
            copy.Y = Quantize(shape.Y);
            copy.Z = Quantize(shape.Z);
            copy.RotationX = Editing.TownMath.NormalizeAngle(shape.RotationX);
            copy.RotationY = Editing.TownMath.NormalizeAngle(shape.RotationY);
            copy.RotationZ = Editing.TownMath.NormalizeAngle(shape.RotationZ);
            copy.ScaleX = Quantize(Math.Clamp(shape.ScaleX, MinScale, MaxScale));
            copy.ScaleY = Quantize(Math.Clamp(shape.ScaleY, MinScale, MaxScale));
            copy.ScaleZ = Quantize(Math.Clamp(shape.ScaleZ, MinScale, MaxScale));
            return copy;
        }

        /// <summary>Whether a shape can be stored: a known kind, finite numbers, in range, and near the town.</summary>
        public static EditResult Check(TownDto town, TerrainShapeDto shape)
        {
            if (shape == null || string.IsNullOrEmpty(shape.Id)) return EditResult.Fail("Missing shape id");
            if (!IsKind(shape.Kind)) return EditResult.Fail($"Unknown shape '{shape.Kind}'");
            if (shape.Name != null && shape.Name.Length > TownLimits.MaxShapeNameLength) return EditResult.Fail("Shape name is too long");
            foreach (var value in new[] { shape.X, shape.Y, shape.Z, shape.RotationX, shape.RotationY, shape.RotationZ, shape.ScaleX, shape.ScaleY, shape.ScaleZ })
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) return EditResult.Fail("Invalid shape");
            }

            var reach = town.Size * 0.5f + OutsideReach;
            if (MathF.Abs(shape.X) > reach || MathF.Abs(shape.Z) > reach) return EditResult.Fail("Too far outside the town");
            if (shape.Y < MinY || shape.Y > MaxY) return EditResult.Fail(shape.Y < MinY ? "Too far underground" : "Too high up");

            const float slack = 0.01f;
            if (Math.Min(shape.ScaleX, Math.Min(shape.ScaleY, shape.ScaleZ)) < MinScale - slack) return EditResult.Fail("Too small");
            if (Math.Max(shape.ScaleX, Math.Max(shape.ScaleY, shape.ScaleZ)) > MaxScale + slack) return EditResult.Fail("Too big");
            return EditResult.Success;
        }

        /// <summary>The server's check of the saved shapes.</summary>
        internal static void Validate(TownDto town, HashSet<string> ids, ValidationResult result)
        {
            if (town.TerrainShapes == null) return;
            if (town.TerrainShapes.Count > TownLimits.MaxTerrainShapes)
            {
                result.Add("Too many terrain shapes");
                return;
            }

            foreach (var shape in town.TerrainShapes)
            {
                if (shape != null && !string.IsNullOrEmpty(shape.Id) && !ids.Add(shape.Id))
                {
                    result.Add($"Terrain shape id '{shape.Id}' is duplicated");
                    continue;
                }

                var check = Check(town, shape);
                if (!check.Ok) result.Add($"Terrain shape {shape?.Id}: {check.Error}");
            }
        }

        private static float Quantize(float value) => Editing.TownMath.Quantize(value);

        /// <summary>Cuts a ray down to the part inside a convex shape given by its face planes (n·p ≤ offset).</summary>
        private static bool Clip((Vec3 Normal, float Offset)[] planes, Vec3 o, Vec3 d, out float enter, out float exit)
        {
            enter = float.NegativeInfinity;
            exit = float.PositiveInfinity;
            foreach (var (normal, offset) in planes)
            {
                var along = Vec3.Dot(normal, d);
                var gap = offset - Vec3.Dot(normal, o);
                if (MathF.Abs(along) < 1e-9f)
                {
                    if (gap < 0f) return false;
                    continue;
                }

                var t = gap / along;
                if (along < 0f) enter = Math.Max(enter, t);
                else exit = Math.Min(exit, t);
                if (enter > exit) return false;
            }

            return true;
        }

        /// <summary>The plane of a sloping side through two triangle corners, facing <paramref name="side"/> (-1 left, 1 right) and up.</summary>
        private static (Vec3, float) SlopeOf(Vec3 a, Vec3 b, float side)
        {
            var along = b - a;
            var normal = new Vec3(along.Y, -along.X, 0f);
            if (normal.X * side < 0f) normal = -normal;
            normal = normal * (1f / normal.Length);
            return (normal, Vec3.Dot(normal, a));
        }

        private static Vec3 RotateX(Vec3 v, float degrees)
        {
            var (s, c) = SinCos(degrees);
            return new Vec3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
        }

        private static Vec3 RotateY(Vec3 v, float degrees)
        {
            var (s, c) = SinCos(degrees);
            return new Vec3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
        }

        private static Vec3 RotateZ(Vec3 v, float degrees)
        {
            var (s, c) = SinCos(degrees);
            return new Vec3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }

        private static (float Sin, float Cos) SinCos(float degrees)
        {
            var radians = degrees * Vec2.Deg2Rad;
            return (MathF.Sin(radians), MathF.Cos(radians));
        }
    }
}

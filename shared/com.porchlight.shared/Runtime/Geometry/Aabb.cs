using System;

namespace Porchlight.Shared.Geometry
{
    /// <summary>
    /// An axis-aligned rectangle on the ground plane. Everything inside a cottage lines up with the
    /// cottage's own axes, so its rules work in these instead of <see cref="OrientedRect"/>s.
    /// </summary>
    public readonly struct Aabb
    {
        public readonly float MinX;
        public readonly float MinZ;
        public readonly float MaxX;
        public readonly float MaxZ;

        public Aabb(float minX, float minZ, float maxX, float maxZ)
        {
            MinX = Math.Min(minX, maxX);
            MinZ = Math.Min(minZ, maxZ);
            MaxX = Math.Max(minX, maxX);
            MaxZ = Math.Max(minZ, maxZ);
        }

        public static Aabb Around(Vec2 center, float halfX, float halfZ) =>
            new Aabb(center.X - halfX, center.Z - halfZ, center.X + halfX, center.Z + halfZ);

        public Vec2 Center => new Vec2((MinX + MaxX) * 0.5f, (MinZ + MaxZ) * 0.5f);
        public float HalfX => (MaxX - MinX) * 0.5f;
        public float HalfZ => (MaxZ - MinZ) * 0.5f;

        /// <summary>Rectangles that only touch, or overlap by less than <paramref name="tolerance"/>, don't count.</summary>
        public bool Overlaps(in Aabb other, float tolerance = 0.01f) =>
            MinX < other.MaxX - tolerance && other.MinX < MaxX - tolerance
            && MinZ < other.MaxZ - tolerance && other.MinZ < MaxZ - tolerance;

        public bool Contains(Vec2 point, float tolerance = 0f) =>
            point.X >= MinX - tolerance && point.X <= MaxX + tolerance && point.Z >= MinZ - tolerance && point.Z <= MaxZ + tolerance;

        public bool Contains(in Aabb other, float tolerance = 0.01f) =>
            other.MinX >= MinX - tolerance && other.MaxX <= MaxX + tolerance && other.MinZ >= MinZ - tolerance && other.MaxZ <= MaxZ + tolerance;

        public Aabb Expanded(float by) => new Aabb(MinX - by, MinZ - by, MaxX + by, MaxZ + by);

        public Aabb Union(in Aabb other) =>
            new Aabb(Math.Min(MinX, other.MinX), Math.Min(MinZ, other.MinZ), Math.Max(MaxX, other.MaxX), Math.Max(MaxZ, other.MaxZ));

        public override string ToString() => $"[{MinX:0.##}, {MinZ:0.##} .. {MaxX:0.##}, {MaxZ:0.##}]";
    }

    /// <summary>Moving between a building's own frame and the town's (same convention as <see cref="OrientedRect"/>).</summary>
    public static class Frames
    {
        public static Vec2 ToWorld(Vec2 origin, float rotationDeg, Vec2 local)
        {
            var r = rotationDeg * Vec2.Deg2Rad;
            float cos = MathF.Cos(r), sin = MathF.Sin(r);
            return new Vec2(origin.X + local.X * cos + local.Z * sin, origin.Z - local.X * sin + local.Z * cos);
        }

        public static Vec2 ToLocal(Vec2 origin, float rotationDeg, Vec2 world)
        {
            var r = rotationDeg * Vec2.Deg2Rad;
            float cos = MathF.Cos(r), sin = MathF.Sin(r);
            var d = world - origin;
            return new Vec2(d.X * cos - d.Z * sin, d.X * sin + d.Z * cos);
        }

        /// <summary>A local rectangle as a rotated one in the town.</summary>
        public static OrientedRect ToWorld(Vec2 origin, float rotationDeg, in Aabb local) =>
            new OrientedRect(ToWorld(origin, rotationDeg, local.Center), local.HalfX, local.HalfZ, rotationDeg);
    }
}

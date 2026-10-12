using System;

namespace Porchlight.Shared.Geometry
{
    /// <summary>
    /// A rotated rectangle on the ground plane. Used for building footprints and road segments.
    /// Width runs along the local X axis and depth along the local Z axis (the building's "front").
    /// </summary>
    public readonly struct OrientedRect
    {
        public readonly Vec2 Center;
        public readonly float HalfWidth;
        public readonly float HalfDepth;
        public readonly float RotationDeg;

        /// <summary>World direction of the local +X axis.</summary>
        public readonly Vec2 AxisX;

        /// <summary>World direction of the local +Z axis (the way the rectangle faces).</summary>
        public readonly Vec2 AxisZ;

        public OrientedRect(Vec2 center, float halfWidth, float halfDepth, float rotationDeg)
        {
            Center = center;
            HalfWidth = halfWidth;
            HalfDepth = halfDepth;
            RotationDeg = rotationDeg;

            // Same convention as Unity's Quaternion.Euler(0, rotationDeg, 0).
            var r = rotationDeg * Vec2.Deg2Rad;
            var cos = MathF.Cos(r);
            var sin = MathF.Sin(r);
            AxisX = new Vec2(cos, -sin);
            AxisZ = new Vec2(sin, cos);
        }

        public float BoundingRadius => MathF.Sqrt(HalfWidth * HalfWidth + HalfDepth * HalfDepth);

        /// <summary>A rectangle covering a road segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
        public static OrientedRect FromSegment(Vec2 a, Vec2 b, float width)
        {
            var d = b - a;
            return new OrientedRect((a + b) * 0.5f, width * 0.5f, d.Length * 0.5f, Vec2.HeadingOf(d));
        }

        public bool Contains(Vec2 point)
        {
            var d = point - Center;
            return MathF.Abs(Vec2.Dot(d, AxisX)) <= HalfWidth
                && MathF.Abs(Vec2.Dot(d, AxisZ)) <= HalfDepth;
        }

        /// <summary>Half the length of this rectangle's shadow on <paramref name="axis"/> (a unit vector).</summary>
        public float ExtentAlong(Vec2 axis) =>
            HalfWidth * MathF.Abs(Vec2.Dot(AxisX, axis)) + HalfDepth * MathF.Abs(Vec2.Dot(AxisZ, axis));

        /// <summary>
        /// Separating-axis overlap test. Rectangles that only touch, or overlap by less than
        /// <paramref name="tolerance"/> metres, don't count, so buildings can sit flush against roads.
        /// </summary>
        public bool Overlaps(in OrientedRect other, float tolerance = 0.01f)
        {
            var reach = BoundingRadius + other.BoundingRadius;
            if ((other.Center - Center).LengthSquared > reach * reach)
            {
                return false;
            }

            return !SeparatedOn(AxisX, other, tolerance)
                && !SeparatedOn(AxisZ, other, tolerance)
                && !SeparatedOn(other.AxisX, other, tolerance)
                && !SeparatedOn(other.AxisZ, other, tolerance);
        }

        /// <summary>True when the whole rectangle lies inside the square from -halfSize to +halfSize.</summary>
        public bool FitsInSquare(float halfSize, float tolerance = 0.001f)
        {
            return MathF.Abs(Center.X) + ExtentAlong(new Vec2(1f, 0f)) <= halfSize + tolerance
                && MathF.Abs(Center.Z) + ExtentAlong(new Vec2(0f, 1f)) <= halfSize + tolerance;
        }

        private bool SeparatedOn(Vec2 axis, in OrientedRect other, float tolerance)
        {
            var distance = MathF.Abs(Vec2.Dot(other.Center - Center, axis));
            return distance >= ExtentAlong(axis) + other.ExtentAlong(axis) - tolerance;
        }
    }
}

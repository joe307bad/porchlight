using System;

namespace Porchlight.Shared.Geometry
{
    /// <summary>
    /// A point or direction on the ground plane. X is east and Z is north, matching Unity's XZ plane,
    /// so a Vec2 maps directly onto a Unity world position (x, 0, z).
    /// </summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public const float Deg2Rad = MathF.PI / 180f;
        public const float Rad2Deg = 180f / MathF.PI;

        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        public readonly float X;
        public readonly float Z;

        public Vec2(float x, float z)
        {
            X = x;
            Z = z;
        }

        public float LengthSquared => X * X + Z * Z;
        public float Length => MathF.Sqrt(LengthSquared);

        public Vec2 Normalized
        {
            get
            {
                var length = Length;
                return length > 1e-6f ? new Vec2(X / length, Z / length) : Zero;
            }
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Z + b.Z);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Z - b.Z);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Z);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Z * s);

        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Z * b.Z;
        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;

        /// <summary>
        /// Unit direction for a heading in degrees, using Unity's rotation about +Y:
        /// 0 faces +Z (north) and 90 faces +X (east).
        /// </summary>
        public static Vec2 FromHeading(float rotationDeg)
        {
            var r = rotationDeg * Deg2Rad;
            return new Vec2(MathF.Sin(r), MathF.Cos(r));
        }

        /// <summary>The heading in degrees that <see cref="FromHeading"/> would turn into this direction.</summary>
        public static float HeadingOf(Vec2 direction) => MathF.Atan2(direction.X, direction.Z) * Rad2Deg;

        public bool Equals(Vec2 other) => X.Equals(other.X) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is Vec2 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Z);
        public override string ToString() => $"({X:0.##}, {Z:0.##})";
    }
}

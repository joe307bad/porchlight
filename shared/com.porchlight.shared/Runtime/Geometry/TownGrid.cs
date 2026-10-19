using System;

namespace Porchlight.Shared.Geometry
{
    /// <summary>
    /// The building grid drawn across the town floor. Builder snapping and the hover cross both use
    /// it, so what the player sees is what they get.
    /// </summary>
    public static class TownGrid
    {
        /// <summary>Distance between grid lines, in metres.</summary>
        public const float Spacing = 4f;

        /// <summary>Every this many lines, a stronger "major" line.</summary>
        public const int MajorEvery = 4;

        /// <summary>Roads snap to half-grid steps, so they can run down the middle of a grid square.</summary>
        public const float HalfStep = Spacing * 0.5f;

        /// <summary>The grid intersection nearest to <paramref name="point"/>.</summary>
        public static Vec2 Snap(Vec2 point) => Snap(point, Spacing);

        /// <summary>The nearest point on a grid <paramref name="step"/> metres apart, lined up with the main grid.</summary>
        public static Vec2 Snap(Vec2 point, float step) =>
            new Vec2(MathF.Round(point.X / step) * step, MathF.Round(point.Z / step) * step);
    }
}

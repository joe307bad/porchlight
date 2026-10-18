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

        /// <summary>The grid intersection nearest to <paramref name="point"/>.</summary>
        public static Vec2 Snap(Vec2 point) =>
            new Vec2(MathF.Round(point.X / Spacing) * Spacing, MathF.Round(point.Z / Spacing) * Spacing);
    }
}

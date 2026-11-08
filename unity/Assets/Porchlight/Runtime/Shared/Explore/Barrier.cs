// Fences you can't walk through (a race course's ribbon): thin walls, as line
// segments, kept in a grid of tiles so a step only looks at those near it. A
// step is refused if it crosses one, or comes closer to one than you are
// wide (moving away from one, or along it, is always fine, so you never stick).
// Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public sealed class Barrier
    {
        readonly List<(Vec2 a, Vec2 b)> segments = new List<(Vec2, Vec2)>();
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();

        public int Count => segments.Count;

        static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        /// <summary>A fence along a line of points.</summary>
        public void Add(IReadOnlyList<Vec2> line)
        {
            for (int i = 1; i < line.Count; i++) Add(line[i - 1], line[i]);
        }

        public void Add(Vec2 a, Vec2 b)
        {
            if (Geo.Dist(a, b) < 1e-6) return;
            int id = segments.Count;
            segments.Add((a, b));
            // Every tile the segment's box touches, and one more round (a step reaches a little way).
            int x0 = (int)Math.Floor(Math.Min(a.x, b.x)) - 1, x1 = (int)Math.Floor(Math.Max(a.x, b.x)) + 1;
            int y0 = (int)Math.Floor(Math.Min(a.y, b.y)) - 1, y1 = (int)Math.Floor(Math.Max(a.y, b.y)) + 1;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (!cells.TryGetValue(Key(x, y), out var list)) cells[Key(x, y)] = list = new List<int>();
                    list.Add(id);
                }
        }

        /// <summary>Would a step from (x0, y0) to (x1, y1), by someone `radius` across, go through or into a fence?</summary>
        public bool Blocks(double x0, double y0, double x1, double y1, double radius)
        {
            if (!cells.TryGetValue(Key((int)Math.Floor(x1), (int)Math.Floor(y1)), out var near)) return false;
            var from = new Vec2(x0, y0);
            var to = new Vec2(x1, y1);
            foreach (int id in near)
            {
                var (a, b) = segments[id];
                if (Geo.SegmentIntersection(from, to, a, b) != null) return true;
                double after = Geo.DistToSegment(to, a, b);
                if (after < radius && after < Geo.DistToSegment(from, a, b) - 1e-9) return true;
            }
            return false;
        }
    }
}

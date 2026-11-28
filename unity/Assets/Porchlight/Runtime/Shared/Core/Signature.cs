// Signatures: a number naming a piece of the town by its shape alone, the same whatever order it was found in,
// so a piece that comes out the same shape draws the same random numbers (Rng.Local). This is what keeps a change
// in one corner of a town from reshuffling the rest (docs/street-editor.md §4.1).

using System;
using System.Collections.Generic;

namespace Porchlight.Core
{
    public static class Signature
    {
        const uint FNV = 2166136261u;
        const uint PRIME = 16777619u;

        public static uint Mix(uint h, int v)
        {
            unchecked
            {
                for (int k = 0; k < 4; k++)
                {
                    h ^= (uint)(v >> (8 * k)) & 0xff;
                    h *= PRIME;
                }
                return h;
            }
        }

        /// <summary>A set of tile indices, in any order.</summary>
        public static uint OfTiles(IReadOnlyList<int> tiles)
        {
            var sorted = new int[tiles.Count];
            for (int i = 0; i < sorted.Length; i++) sorted[i] = tiles[i];
            Array.Sort(sorted);
            uint h = Mix(FNV, sorted.Length);
            foreach (int t in sorted) h = Mix(h, t);
            return h;
        }

        /// <summary>A polygon, rounded to half tiles, from whichever corner it starts and whichever way round it goes.</summary>
        public static uint OfPolygon(IReadOnlyList<Vec2> pts)
        {
            int n = pts.Count;
            if (n == 0) return FNV;
            var q = new (int x, int y)[n];
            for (int i = 0; i < n; i++) q[i] = ((int)Math.Round(pts[i].x * 2), (int)Math.Round(pts[i].y * 2));
            int start = 0;
            for (int i = 1; i < n; i++) if (q[i].y < q[start].y || (q[i].y == q[start].y && q[i].x < q[start].x)) start = i;
            // Read the same way round whatever the input: towards whichever neighbour comes first (lower y, then x).
            var a = q[(start + 1) % n];
            var b = q[(start - 1 + n) % n];
            int dir = (a.y < b.y || (a.y == b.y && a.x <= b.x)) ? 1 : -1;
            uint h = Mix(FNV, n);
            for (int k = 0; k < n; k++)
            {
                var p = q[((start + dir * k) % n + n) % n];
                h = Mix(Mix(h, p.x), p.y);
            }
            return h;
        }

        /// <summary>A point, rounded to whole tiles.</summary>
        public static uint OfPoint(Vec2 p) => Mix(Mix(FNV, (int)Math.Floor(p.x)), (int)Math.Floor(p.y));

        /// <summary>A polyline's two ends, rounded to whole tiles, either way along.</summary>
        public static uint OfEnds(Vec2 a, Vec2 b)
        {
            uint x = OfPoint(a), y = OfPoint(b);
            return x < y ? Mix(Mix(FNV, (int)x), (int)y) : Mix(Mix(FNV, (int)y), (int)x);
        }
    }
}

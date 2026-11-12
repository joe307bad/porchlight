// Where a way shown turns: the corners an arrow is put up at, pointing the way to
// go, so a turn can be seen coming at speed. The heading over a little way before
// each spot against the heading over a little way after it: a corner turns it, a
// kink (the way doubling back on itself a step, where two ways are joined) doesn't,
// and nor does a jog (a step aside and on the same way, on a straightaway).
// Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public static class RouteTurns
    {
        public struct Turn
        {
            /// <summary>The corner.</summary>
            public Vec2 at;
            /// <summary>The way out of it (a unit step).</summary>
            public Vec2 dir;
            /// <summary>How far along the way it is (tiles).</summary>
            public double along;
            /// <summary>How sharp (radians; positive turns towards +y from +x, i.e. clockwise on the map: right).</summary>
            public double angle;
        }

        /// <summary>Samples this far apart along the way (tiles).</summary>
        const double STEP = 0.5;
        /// <summary>The heading before and after a spot is over this far (tiles), as many samples.</summary>
        const int SPAN = 3;
        /// <summary>
        /// A corner and the next one turning back the other way, so the way goes on as before but stepped aside less
        /// than this (tiles), are a jog, not turns: a straightaway stepping over from one street's middle to the
        /// next. A longer step (a staggered crossing: a turn, a block, and a turn back) is two turns.
        /// </summary>
        const double JOG = 4;
        /// <summary>
        /// A corner nearer than this along the way (tiles) isn't shown: from there (partway along a jog, its first
        /// corner behind you) there's no telling it from a turn. One further off was shown coming up to it.
        /// </summary>
        const double NEAREST = JOG + 1;
        /// <summary>Two turns closer than this (tiles) are one.</summary>
        const double APART = 4;

        /// <summary>The way's turns of `min` radians or more, from `skip` to `reach` tiles along it, the nearest first, `max` at most.</summary>
        public static List<Turn> Find(IReadOnlyList<Vec2> way, double min, double skip, double reach, int max)
        {
            var o = new List<Turn>();
            var pts = Even(Unkinked(way), reach + JOG + 4 * SPAN * STEP);
            int n = pts.Count;
            if (n < 4 * SPAN + 1) return o;
            Vec2 Dir(int a, int b)
            {
                double dx = pts[b].x - pts[a].x, dy = pts[b].y - pts[a].y, d = Math.Sqrt(dx * dx + dy * dy);
                return d < 1e-9 ? new Vec2(0, 0) : new Vec2(dx / d, dy / d);
            }
            double Between(Vec2 before, Vec2 after)
            {
                if (before.x == 0 && before.y == 0 || after.x == 0 && after.y == 0) return 0;
                return Math.Atan2(before.x * after.y - before.y * after.x, before.x * after.x + before.y * after.y);
            }
            // Near the way's start, over what there is of it before (so a corner just ahead is found too).
            double Angle(int i) => Between(Dir(Math.Max(0, i - 2 * SPAN), Math.Max(1, i - SPAN)), Dir(i + SPAN, i + 2 * SPAN));
            // The sharpest spots of a run of turning ones (a corner turns them all alike for a few
            // samples either side of it): the corner is the middle of them.
            var corners = new List<(Turn t, int i)>();
            int best = -1, bestEnd = -1;
            double bestA = 0;
            void Close()
            {
                if (best < 0) return;
                int c = (best + bestEnd) / 2;
                corners.Add((new Turn { at = pts[c], dir = Dir(c, Math.Min(n - 1, c + 2 * SPAN)), along = c * STEP, angle = bestA }, c));
                best = bestEnd = -1;
                bestA = 0;
            }
            for (int i = 1; i + 2 * SPAN < n; i++)
            {
                double a = Angle(i);
                if (Math.Abs(a) >= min)
                {
                    if (best < 0 || Math.Abs(a) > Math.Abs(bestA) + 1e-3)
                    {
                        best = bestEnd = i;
                        bestA = a;
                    }
                    else if (Math.Abs(Math.Abs(a) - Math.Abs(bestA)) <= 1e-3) bestEnd = i;
                }
                else Close();
            }
            Close();
            // A jog's two corners go; the rest are turns. How far aside it steps: from the way before the
            // first corner to the way after the second, across the heading before.
            var jog = new bool[corners.Count];
            for (int k = 0; k + 1 < corners.Count; k++)
            {
                int i0 = corners[k].i, i1 = corners[k + 1].i;
                if (Math.Abs(corners[k].t.angle + corners[k + 1].t.angle) >= min) continue;
                int b0 = Math.Max(0, i0 - 2 * SPAN), b1 = Math.Max(1, i0 - SPAN), a0 = Math.Min(n - 1, i1 + 2 * SPAN);
                var h = Dir(b0, b1);
                double aside = Math.Abs(h.x * (pts[a0].y - pts[b1].y) - h.y * (pts[a0].x - pts[b1].x));
                if (aside < JOG) jog[k] = jog[k + 1] = true;
            }
            for (int k = 0; k < corners.Count && o.Count < max; k++)
            {
                var t = corners[k].t;
                if (jog[k] || t.along < Math.Max(skip, NEAREST) || t.along > reach) continue;
                if (o.Count > 0 && t.along - o[o.Count - 1].along < APART) continue;
                o.Add(t);
            }
            return o;
        }

        /// <summary>A short spur the way runs out along and straight back (where two ways were joined) taken out.</summary>
        const double SPUR = 3;

        static List<Vec2> Unkinked(IReadOnlyList<Vec2> way)
        {
            var o = new List<Vec2>();
            foreach (var p in way)
            {
                if (o.Count > 0 && Geo.Dist(o[o.Count - 1], p) < 1e-6) continue;
                o.Add(p);
                // a, b, c doubling back at b (sharper than 150°) with a leg under SPUR: b goes.
                while (o.Count >= 3)
                {
                    var a = o[o.Count - 3];
                    var b = o[o.Count - 2];
                    var c = o[o.Count - 1];
                    double ab = Geo.Dist(a, b), bc = Geo.Dist(b, c);
                    if (ab < 1e-9 || bc < 1e-9) break;
                    double cos = ((b.x - a.x) * (c.x - b.x) + (b.y - a.y) * (c.y - b.y)) / (ab * bc);
                    if (cos > -0.866 || Math.Min(ab, bc) >= SPUR) break;
                    o.RemoveAt(o.Count - 2);
                }
            }
            return o;
        }

        /// <summary>The way as points STEP apart along it, as far as `upTo` tiles.</summary>
        static List<Vec2> Even(IReadOnlyList<Vec2> way, double upTo)
        {
            var o = new List<Vec2>();
            if (way.Count == 0) return o;
            o.Add(way[0]);
            double carry = 0, along = 0;
            for (int i = 0; i + 1 < way.Count && along < upTo; i++)
            {
                var a = way[i];
                var b = way[i + 1];
                double len = Geo.Dist(a, b);
                if (len < 1e-9) continue;
                double t = STEP - carry;
                while (t <= len + 1e-9 && along < upTo)
                {
                    o.Add(Geo.Lerp(a, b, t / len));
                    along += STEP;
                    t += STEP;
                }
                carry = len - (t - STEP);
            }
            return o;
        }
    }
}

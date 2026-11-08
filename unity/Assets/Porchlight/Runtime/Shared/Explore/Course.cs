// A race course over a town's streets (Marathon Runner): the longest way from
// one corner of the map to the opposite one. Of the two diagonals, the one
// whose shortest street route is longer; then lengthened, a stretch at a time,
// by going round the block wherever a longer way joins the same two junctions
// without passing a junction the course already uses, so it never crosses or
// doubles back on itself. And the runner's progress along it: only the course
// counts, in order, so cutting through to a later stretch gains nothing. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class Course
    {
        /// <summary>The middle of the road, from the start line to the finish line.</summary>
        public readonly List<Vec2> points = new List<Vec2>();
        /// <summary>At each point, how far from the middle the asphalt goes (tiles).</summary>
        public readonly List<double> half = new List<double>();
        /// <summary>At each point, how far along it is (tiles).</summary>
        public readonly List<double> at = new List<double>();
        /// <summary>The roads it runs along, in order (indices into the town's roads).</summary>
        public readonly List<int> roads = new List<int>();

        public double Length => at.Count > 0 ? at[at.Count - 1] : 0;
        public Vec2 Start => points[0];
        public Vec2 Finish => points[points.Count - 1];

        /// <summary>How much longer than its shortest way a course may grow, at most.</summary>
        const double MAX_STRETCH = 4;
        /// <summary>And how long, at most (tiles).</summary>
        const double MAX_LENGTH = 3000;
        /// <summary>The most streets a way round the block may take in place of one.</summary>
        const int DETOUR_STEPS = 4;

        sealed class Edge
        {
            public int road, a, b;
            public double length;
        }

        /// <summary>The town's longest corner-to-corner course; null if its streets don't join two opposite corners.</summary>
        public static Course Longest(IReadOnlyList<RoadStroke> strokes, double width, double height)
        {
            var edges = new List<Edge>();
            var byNode = new Dictionary<int, List<Edge>>();
            var nodeAt = new Dictionary<int, Vec2>();
            for (int i = 0; i < strokes.Count; i++)
            {
                var r = strokes[i];
                if (r.kind == RoadKind.Path || r.points == null || r.points.Count < 2 || r.from == r.to) continue;
                double length = Geo.PolylineLength(r.points);
                if (length < 1e-6) continue;
                var e = new Edge { road = i, a = r.from, b = r.to, length = length };
                edges.Add(e);
                foreach (int n in new[] { r.from, r.to })
                {
                    if (!byNode.TryGetValue(n, out var list)) byNode[n] = list = new List<Edge>();
                    list.Add(e);
                }
                nodeAt[r.from] = r.points[0];
                nodeAt[r.to] = r.points[r.points.Count - 1];
            }
            if (edges.Count == 0) return null;

            int Nearest(Vec2 corner)
            {
                int best = -1;
                double bestD = double.PositiveInfinity;
                foreach (var kv in nodeAt)
                {
                    double d = Geo.Dist(kv.Value, corner);
                    if (d < bestD || (d == bestD && kv.Key < best))
                    {
                        bestD = d;
                        best = kv.Key;
                    }
                }
                return best;
            }
            var corners = new[] { new Vec2(0, 0), new Vec2(width, 0), new Vec2(width, height), new Vec2(0, height) };
            var near = new int[4];
            for (int k = 0; k < 4; k++) near[k] = Nearest(corners[k]);

            // The two diagonals, and the one whose shortest way round the streets is longer.
            List<Edge> chosen = null;
            int from = -1;
            double chosenLength = -1;
            foreach (var (s, t) in new[] { (0, 2), (1, 3) })
            {
                if (near[s] == near[t]) continue;
                var way = Shortest(byNode, near[s], near[t], out double length);
                if (way == null || length <= chosenLength) continue;
                chosen = way;
                from = near[s];
                chosenLength = length;
            }
            if (chosen == null) return null;
            var nodes = NodesOf(chosen, from);
            Lengthen(byNode, chosen, nodes, Math.Min(MAX_LENGTH, chosenLength * MAX_STRETCH));
            return Build(strokes, chosen, nodes);
        }

        static List<Edge> Shortest(Dictionary<int, List<Edge>> byNode, int from, int to, out double length)
        {
            var cost = new Dictionary<int, double> { [from] = 0 };
            var via = new Dictionary<int, Edge>();
            var ids = new List<int>();
            var idOf = new Dictionary<int, int>();
            int Id(int n)
            {
                if (!idOf.TryGetValue(n, out int k))
                {
                    ids.Add(n);
                    idOf[n] = k = ids.Count - 1;
                }
                return k;
            }
            var heap = new MinHeap();
            heap.Push(Id(from), 0);
            var done = new HashSet<int>();
            while (heap.Size > 0)
            {
                int n = ids[heap.Pop()];
                if (!done.Add(n)) continue;
                if (n == to) break;
                if (!byNode.TryGetValue(n, out var ways)) continue;
                foreach (var e in ways)
                {
                    int o = e.a == n ? e.b : e.a;
                    double c = cost[n] + e.length;
                    if (cost.TryGetValue(o, out var was) && was <= c) continue;
                    cost[o] = c;
                    via[o] = e;
                    heap.Push(Id(o), c);
                }
            }
            length = 0;
            if (!cost.ContainsKey(to)) return null;
            length = cost[to];
            var path = new List<Edge>();
            for (int n = to; n != from;)
            {
                var e = via[n];
                path.Add(e);
                n = e.a == n ? e.b : e.a;
            }
            path.Reverse();
            return path;
        }

        static List<int> NodesOf(List<Edge> path, int from)
        {
            var nodes = new List<int> { from };
            foreach (var e in path) nodes.Add(e.a == nodes[nodes.Count - 1] ? e.b : e.a);
            return nodes;
        }

        /// <summary>
        /// Swap stretches for longer ways round between the same junctions, through junctions the
        /// course doesn't use yet, the biggest gain first in each pass, until none is left or it's long enough.
        /// </summary>
        static void Lengthen(Dictionary<int, List<Edge>> byNode, List<Edge> path, List<int> nodes, double limit)
        {
            var used = new HashSet<int>(nodes);
            double total = 0;
            foreach (var e in path) total += e.length;
            for (int pass = 0; pass < 400 && total < limit; pass++)
            {
                bool grew = false;
                for (int i = 0; i < path.Count && total < limit; i++)
                {
                    int u = nodes[i], v = nodes[i + 1];
                    var best = Detour(byNode, u, v, path[i], used, out double length);
                    if (best == null || length <= path[i].length + 0.5 || total - path[i].length + length > limit) continue;
                    total += length - path[i].length;
                    var inner = NodesOf(best, u);
                    path.RemoveAt(i);
                    path.InsertRange(i, best);
                    nodes.InsertRange(i + 1, inner.GetRange(1, inner.Count - 2));
                    for (int k = 1; k < inner.Count - 1; k++) used.Add(inner[k]);
                    i += best.Count - 1;
                    grew = true;
                }
                if (!grew) break;
            }
        }

        /// <summary>The longest way from u to v in a few streets, other than `instead`, through no junction in `used`.</summary>
        static List<Edge> Detour(Dictionary<int, List<Edge>> byNode, int u, int v, Edge instead, HashSet<int> used, out double length)
        {
            List<Edge> best = null;
            double bestLength = 0;
            var stack = new List<Edge>();
            var seen = new HashSet<int> { u };
            void Walk(int n, double sofar)
            {
                if (stack.Count >= DETOUR_STEPS || !byNode.TryGetValue(n, out var ways)) return;
                foreach (var e in ways)
                {
                    if (e == instead) continue;
                    int o = e.a == n ? e.b : e.a;
                    if (o == v)
                    {
                        if (stack.Count == 0 || sofar + e.length <= bestLength) continue;
                        stack.Add(e);
                        best = new List<Edge>(stack);
                        bestLength = sofar + e.length;
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }
                    if (used.Contains(o) || !seen.Add(o)) continue;
                    stack.Add(e);
                    Walk(o, sofar + e.length);
                    stack.RemoveAt(stack.Count - 1);
                    seen.Remove(o);
                }
            }
            Walk(u, 0);
            length = bestLength;
            return best;
        }

        static Course Build(IReadOnlyList<RoadStroke> strokes, List<Edge> path, List<int> nodes)
        {
            var c = new Course();
            for (int i = 0; i < path.Count; i++)
            {
                var r = strokes[path[i].road];
                bool forward = r.from == nodes[i];
                double hw = r.width / 2;
                c.roads.Add(path[i].road);
                for (int k = 0; k < r.points.Count; k++)
                {
                    var p = r.points[forward ? k : r.points.Count - 1 - k];
                    if (c.points.Count > 0 && Geo.Dist(c.points[c.points.Count - 1], p) < 1e-6)
                    {
                        c.half[c.half.Count - 1] = Math.Max(c.half[c.half.Count - 1], hw);
                        continue;
                    }
                    c.points.Add(p);
                    c.half.Add(hw);
                }
            }
            c.at.Add(0);
            for (int k = 1; k < c.points.Count; k++) c.at.Add(c.at[k - 1] + Geo.Dist(c.points[k - 1], c.points[k]));
            return c;
        }

        /// <summary>The point `s` tiles along, and which way the course runs there (a unit vector).</summary>
        public (Vec2 point, Vec2 along, double half) At(double s)
        {
            s = Math.Max(0, Math.Min(Length, s));
            int k = 1;
            while (k < points.Count - 1 && at[k] < s) k++;
            var a = points[k - 1];
            var b = points[k];
            double seg = at[k] - at[k - 1];
            double f = seg > 1e-9 ? (s - at[k - 1]) / seg : 0;
            var d = seg > 1e-9 ? new Vec2((b.x - a.x) / seg, (b.y - a.y) / seg) : new Vec2(1, 0);
            return (new Vec2(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f), d, half[k - 1] + (half[k] - half[k - 1]) * f);
        }

        /// <summary>
        /// The nearest point of the course to `p` among the stretch from `lo` to `hi` tiles along:
        /// how far along it is, how far off `p` is, and the road's half-width there.
        /// </summary>
        public (double s, double d, double half) Nearest(Vec2 p, double lo, double hi)
        {
            double bestS = 0, bestD = double.PositiveInfinity, bestHalf = 0;
            for (int k = 1; k < points.Count; k++)
            {
                if (at[k] < lo || at[k - 1] > hi) continue;
                var a = points[k - 1];
                var b = points[k];
                double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
                double t = l2 > 1e-12 ? Math.Max(0, Math.Min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2)) : 0;
                double d = Numbers.Hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t));
                if (d >= bestD) continue;
                bestD = d;
                bestS = at[k - 1] + (at[k] - at[k - 1]) * t;
                bestHalf = half[k - 1] + (half[k] - half[k - 1]) * t;
            }
            return (bestS, bestD, bestHalf);
        }
    }

    /// <summary>
    /// A runner's way along a course: how far they've got, gained only by being on the course
    /// near where they'd got to (a little behind, or a little ahead), so the stretches must be
    /// run in order and a short cut to a later one counts for nothing.
    /// </summary>
    public sealed class CourseRun
    {
        /// <summary>How far ahead of where you'd got the course looks for you (tiles).</summary>
        public const double LOOK_AHEAD = 4;
        public const double LOOK_BACK = 8;
        /// <summary>How far past the road's edge still counts as on the course (the pavement, behind the ribbon).</summary>
        public const double EDGE = 1.2;

        public readonly Course course;
        /// <summary>How far along you've got (tiles).</summary>
        public double progress;
        /// <summary>Over the finish line.</summary>
        public bool Finished => progress >= course.Length - 0.25;

        public CourseRun(Course course) => this.course = course;

        /// <summary>Is `p` on the course near where you'd got to?</summary>
        public bool OnCourse(Vec2 p)
        {
            var n = course.Nearest(p, progress - LOOK_BACK, progress + LOOK_AHEAD * 3);
            return n.d <= n.half + EDGE;
        }

        /// <summary>You're at `p` (counted only when `counts`: on foot, on the ground): on as far as you've got along the course.</summary>
        public void Step(Vec2 p, bool counts)
        {
            if (!counts || Finished) return;
            var n = course.Nearest(p, progress - LOOK_BACK, progress + LOOK_AHEAD);
            if (n.d <= n.half + EDGE && n.s > progress) progress = Math.Min(course.Length, n.s);
        }
    }
}

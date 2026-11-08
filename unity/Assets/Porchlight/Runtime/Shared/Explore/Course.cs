// A race course over a town's streets (Marathon Runner): a long way from one
// corner of the map to the opposite one, made of long straight runs along the
// widest streets. Streets are worth their length (narrow ones much less than
// main roads), and every turn costs as much as a good stretch of running, so
// the course is the corner-to-corner way with the most straight, wide running
// for its turns: first the best way across, then lengthened wherever a way
// round, through junctions the course doesn't use yet, adds more running than
// its extra turns cost. It never passes a junction twice, so it never crosses
// itself. Its sides (Side) are where the ribbon goes, the corners met cleanly;
// and the runner's progress along it (CourseRun): only the course counts, in
// order, so cutting through to a later stretch gains nothing. Pure.

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
        /// <summary>How many times it turns a corner.</summary>
        public int turns;
        /// <summary>How much of it is along main roads (tiles).</summary>
        public double mainLength;

        public double Length => at.Count > 0 ? at[at.Count - 1] : 0;
        public Vec2 Start => points[0];
        public Vec2 Finish => points[points.Count - 1];

        /// <summary>A turn is a change of way sharper than this (radians) where two streets meet.</summary>
        const double TURN_ANGLE = 0.6;
        /// <summary>What a turn costs, in tiles of running.</summary>
        const double TURN_COST = 30;
        /// <summary>What a tile of narrow street is worth against one of main road, and what it costs to cross by.</summary>
        const double STREET_VALUE = 0.45;
        const double STREET_COST = 2.2;
        /// <summary>A way round has to gain at least this much (tiles, after its turns).</summary>
        const double MIN_GAIN = 6;
        /// <summary>How much longer than the best way across a course may grow, at most, and how long at most (tiles).</summary>
        const double MAX_STRETCH = 3;
        const double MAX_LENGTH = 2500;
        /// <summary>The most streets a way round may take in place of one, and the most turns in it.</summary>
        const int DETOUR_STEPS = 7;
        const int DETOUR_TURNS = 4;

        sealed class Edge
        {
            public int id, road, a, b;
            public double length;
            public bool main;
            /// <summary>Which way it leaves each end (unit vectors).</summary>
            public Vec2 outA, outB;
            public double Value => main ? length : length * STREET_VALUE;
            public double Cost => main ? length : length * STREET_COST;
            public int Other(int n) => n == a ? b : a;
            public Vec2 Leaving(int n) => n == a ? outA : outB;
        }

        sealed class Net
        {
            public readonly List<Edge> edges = new List<Edge>();
            public readonly Dictionary<int, List<Edge>> byNode = new Dictionary<int, List<Edge>>();
            public readonly Dictionary<int, Vec2> nodeAt = new Dictionary<int, Vec2>();

            /// <summary>Does going from `from` on to `to`, where they meet at `node`, turn a corner?</summary>
            public static bool Turns(Edge from, int node, Edge to)
            {
                if (from == null || to == null) return false;
                var arriving = from.Leaving(node);
                var leaving = to.Leaving(node);
                // Arriving runs the opposite way to how it leaves the node.
                double dot = -(arriving.x * leaving.x + arriving.y * leaving.y);
                return dot < Math.Cos(TURN_ANGLE);
            }
        }

        /// <summary>Which way a road leaves its first point (or its last, `atEnd`), from about a tile along.</summary>
        static Vec2 Leaving(List<Vec2> pts, bool atEnd)
        {
            var from = atEnd ? pts[pts.Count - 1] : pts[0];
            Vec2 to = from;
            double sofar = 0;
            for (int k = 1; k < pts.Count; k++)
            {
                var p = atEnd ? pts[pts.Count - 1 - k] : pts[k];
                var q = atEnd ? pts[pts.Count - k] : pts[k - 1];
                sofar += Geo.Dist(p, q);
                to = p;
                if (sofar >= 1.2) break;
            }
            double l = Geo.Dist(from, to);
            return l < 1e-9 ? new Vec2(1, 0) : new Vec2((to.x - from.x) / l, (to.y - from.y) / l);
        }

        /// <summary>The town's marathon course: null if its streets don't join two opposite corners.</summary>
        public static Course Longest(IReadOnlyList<RoadStroke> strokes, double width, double height)
        {
            var net = new Net();
            for (int i = 0; i < strokes.Count; i++)
            {
                var r = strokes[i];
                if (r.kind == RoadKind.Path || r.points == null || r.points.Count < 2 || r.from == r.to) continue;
                double length = Geo.PolylineLength(r.points);
                if (length < 1e-6) continue;
                var e = new Edge { id = net.edges.Count, road = i, a = r.from, b = r.to, length = length, main = r.kind == RoadKind.Main, outA = Leaving(r.points, false), outB = Leaving(r.points, true) };
                net.edges.Add(e);
                foreach (int n in new[] { r.from, r.to })
                {
                    if (!net.byNode.TryGetValue(n, out var list)) net.byNode[n] = list = new List<Edge>();
                    list.Add(e);
                }
                net.nodeAt[r.from] = r.points[0];
                net.nodeAt[r.to] = r.points[r.points.Count - 1];
            }
            if (net.edges.Count == 0) return null;

            // Near each corner, a junction to start or finish at: through-going (not a dead end), on a main road if one's close.
            int Near(Vec2 corner)
            {
                int best = -1;
                double bestScore = double.PositiveInfinity;
                foreach (var kv in net.nodeAt)
                {
                    var ways = net.byNode[kv.Key];
                    double score = Geo.Dist(kv.Value, corner) + (ways.Count < 2 ? 8 : 0) + (ways.Exists(e => e.main) ? 0 : 12);
                    if (score < bestScore || (score == bestScore && kv.Key < best))
                    {
                        bestScore = score;
                        best = kv.Key;
                    }
                }
                return best;
            }
            var corners = new[] { new Vec2(0, 0), new Vec2(width, 0), new Vec2(width, height), new Vec2(0, height) };
            var near = new int[4];
            for (int k = 0; k < 4; k++) near[k] = Near(corners[k]);

            // Both diagonals, each laid out and lengthened: the one with the more running for its turns.
            Course best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var (sIdx, tIdx) in new[] { (0, 2), (1, 3) })
            {
                if (near[sIdx] == near[tIdx]) continue;
                var path = Across(net, near[sIdx], near[tIdx]);
                if (path == null) continue;
                var nodes = NodesOf(path, near[sIdx]);
                double across = 0;
                foreach (var e in path) across += e.length;
                Lengthen(net, path, nodes, Math.Min(MAX_LENGTH, Math.Max(across * MAX_STRETCH, across)));
                double score = Score(path, nodes);
                if (score <= bestScore) continue;
                bestScore = score;
                best = Build(strokes, path, nodes);
            }
            return best;
        }

        /// <summary>A way's worth: its running (main roads full, narrow streets less), less its turns.</summary>
        static double Score(List<Edge> path, List<int> nodes)
        {
            double value = 0;
            for (int i = 0; i < path.Count; i++)
            {
                value += path[i].Value;
                if (i > 0 && Net.Turns(path[i - 1], nodes[i], path[i])) value -= TURN_COST;
            }
            return value;
        }

        /// <summary>The best way across: cheapest by length (narrow streets dearer) plus its turns.</summary>
        static List<Edge> Across(Net net, int from, int to)
        {
            // States: arrived at a node along an edge (edge id * 2, + 1 when arriving at its b end).
            var cost = new Dictionary<int, double>();
            var via = new Dictionary<int, int>();
            var heap = new MinHeap();
            int StateOf(Edge e, int node) => e.id * 2 + (node == e.b ? 1 : 0);
            Edge EdgeOf(int state) => net.edges[state / 2];
            int NodeOf(int state) => state % 2 == 1 ? EdgeOf(state).b : EdgeOf(state).a;
            void Reach(int state, double c, int before)
            {
                if (cost.TryGetValue(state, out var was) && was <= c) return;
                cost[state] = c;
                via[state] = before;
                heap.Push(state, c);
            }
            foreach (var e in net.byNode[from]) Reach(StateOf(e, e.Other(from)), e.Cost, -1);
            var done = new HashSet<int>();
            int end = -1;
            while (heap.Size > 0)
            {
                int state = heap.Pop();
                if (!done.Add(state)) continue;
                int node = NodeOf(state);
                if (node == to)
                {
                    end = state;
                    break;
                }
                var came = EdgeOf(state);
                foreach (var e in net.byNode[node])
                {
                    if (e == came) continue;
                    Reach(StateOf(e, e.Other(node)), cost[state] + e.Cost + (Net.Turns(came, node, e) ? TURN_COST : 0), state);
                }
            }
            if (end < 0) return null;
            var path = new List<Edge>();
            for (int state = end; state >= 0; state = via[state]) path.Add(EdgeOf(state));
            path.Reverse();
            // A way that passes a junction twice (a loop to save a turn) isn't a course.
            var seen = new HashSet<int> { from };
            foreach (int n in NodesOf(path, from).GetRange(1, path.Count)) if (!seen.Add(n)) return null;
            return path;
        }

        static List<int> NodesOf(List<Edge> path, int from)
        {
            var nodes = new List<int> { from };
            foreach (var e in path) nodes.Add(e.Other(nodes[nodes.Count - 1]));
            return nodes;
        }

        /// <summary>
        /// Swap stretches for longer ways round between the same junctions, through junctions the course
        /// doesn't use yet, wherever the extra running is worth more than the extra turns, until none is or it's long enough.
        /// </summary>
        static void Lengthen(Net net, List<Edge> path, List<int> nodes, double limit)
        {
            var used = new HashSet<int>(nodes);
            double total = 0;
            foreach (var e in path) total += e.length;
            for (int pass = 0; pass < 200 && total < limit; pass++)
            {
                // The best swap anywhere this pass.
                int bestAt = -1;
                List<Edge> bestWay = null;
                double bestGain = MIN_GAIN, bestLength = 0;
                for (int i = 0; i < path.Count; i++)
                {
                    var before = i > 0 ? path[i - 1] : null;
                    var after = i + 1 < path.Count ? path[i + 1] : null;
                    var way = Detour(net, nodes[i], nodes[i + 1], path[i], before, after, used, out double gain, out double length);
                    if (way == null || gain <= bestGain || total - path[i].length + length > limit) continue;
                    bestGain = gain;
                    bestAt = i;
                    bestWay = way;
                    bestLength = length;
                }
                if (bestWay == null) break;
                total += bestLength - path[bestAt].length;
                var inner = NodesOf(bestWay, nodes[bestAt]);
                path.RemoveAt(bestAt);
                path.InsertRange(bestAt, bestWay);
                nodes.InsertRange(bestAt + 1, inner.GetRange(1, inner.Count - 2));
                for (int k = 1; k < inner.Count - 1; k++) used.Add(inner[k]);
            }
        }

        /// <summary>
        /// The way from u to v in a few streets, other than `instead`, through no junction in `used`, that gains
        /// the most over it (its running less its turns, counting the turns onto it from `before` and off it to `after`).
        /// </summary>
        static List<Edge> Detour(Net net, int u, int v, Edge instead, Edge before, Edge after, HashSet<int> used, out double gain, out double length)
        {
            double was = instead.Value - TURN_COST * ((Net.Turns(before, u, instead) ? 1 : 0) + (Net.Turns(instead, v, after) ? 1 : 0));
            List<Edge> best = null;
            double bestGain = 0, bestLength = 0;
            var stack = new List<Edge>();
            var seen = new HashSet<int> { u };
            void Walk(int n, Edge came, double value, double sofar, int turned)
            {
                if (stack.Count >= DETOUR_STEPS || !net.byNode.TryGetValue(n, out var ways)) return;
                foreach (var e in ways)
                {
                    if (e == instead || e == came) continue;
                    int t = turned + (Net.Turns(came, n, e) ? 1 : 0);
                    if (t > DETOUR_TURNS) continue;
                    int o = e.Other(n);
                    double v1 = value + e.Value;
                    if (o == v)
                    {
                        if (stack.Count == 0) continue;
                        double g = v1 - TURN_COST * (t + (Net.Turns(e, v, after) ? 1 : 0)) - was;
                        if (g <= bestGain) continue;
                        stack.Add(e);
                        best = new List<Edge>(stack);
                        bestGain = g;
                        bestLength = sofar + e.length;
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }
                    if (used.Contains(o) || !seen.Add(o)) continue;
                    stack.Add(e);
                    Walk(o, e, v1, sofar + e.length, t);
                    stack.RemoveAt(stack.Count - 1);
                    seen.Remove(o);
                }
            }
            Walk(u, before, 0, 0, 0);
            gain = bestGain;
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
                if (path[i].main) c.mainLength += path[i].length;
                if (i > 0 && Net.Turns(path[i - 1], nodes[i], path[i])) c.turns++;
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

        /// <summary>
        /// One side of the course (+1 left of the way it runs, -1 right), `extra` past the asphalt: each
        /// stretch's edge offset by its own road's width, neighbouring stretches meeting where their edges
        /// cross (a clean corner, outside and in), and any loop the inside of a tight bend would make cut out.
        /// Each point comes with how far along the course it is.
        /// </summary>
        public List<(Vec2 p, double s)> Side(int side, double extra)
        {
            // The middle, without the little wiggles that would make the edge saw-toothed.
            var keep = Simplify(0.12);
            int n = keep.Count;
            var o = new List<(Vec2 p, double s)>();
            if (n < 2) return o;
            var normal = new Vec2[n - 1];
            var off = new double[n - 1];
            for (int i = 0; i + 1 < n; i++)
            {
                var a = points[keep[i]];
                var b = points[keep[i + 1]];
                double l = Math.Max(1e-9, Geo.Dist(a, b));
                normal[i] = new Vec2(-(b.y - a.y) / l * side, (b.x - a.x) / l * side);
                // A stretch between two kept points is all one road but for its ends (where a width change is kept), so its middle says its width.
                off[i] = half[(keep[i] + keep[i + 1]) / 2] + extra;
            }
            Vec2 Shift(int seg, int point) => new Vec2(points[keep[point]].x + normal[seg].x * off[seg], points[keep[point]].y + normal[seg].y * off[seg]);
            o.Add((Shift(0, 0), at[keep[0]]));
            for (int k = 1; k + 1 < n; k++)
            {
                // Where the edge of the stretch before meets the edge of the stretch after.
                var a0 = Shift(k - 1, k - 1);
                var a1 = Shift(k - 1, k);
                var b0 = Shift(k, k);
                var b1 = Shift(k, k + 1);
                var r = Geo.Sub(a1, a0);
                var q = Geo.Sub(b1, b0);
                double denom = Geo.Cross(r, q);
                double s = at[keep[k]];
                if (Math.Abs(denom) < 1e-6 * Math.Max(1e-9, Geo.Dist(a0, a1) * Geo.Dist(b0, b1)))
                {
                    // Straight on: where the road's width changes, a step out (or in) to the new edge.
                    o.Add((a1, s));
                    if (Geo.Dist(a1, b0) > 1e-6) o.Add((b0, s));
                    continue;
                }
                double t = Geo.Cross(Geo.Sub(b0, a0), q) / denom;
                var x = Geo.Add(a0, Geo.Scale(r, t));
                // Too far out (a very sharp outside corner): cut the corner off instead.
                if (Geo.Dist(x, points[keep[k]]) > 3 * Math.Max(off[k - 1], off[k]))
                {
                    o.Add((a1, s));
                    o.Add((b0, s));
                }
                else o.Add((x, s));
            }
            o.Add((Shift(n - 2, n - 1), at[keep[n - 1]]));
            return Unloop(o);
        }

        /// <summary>Which points to keep for the course's edges: Ramer–Douglas–Peucker, within `epsilon` of the middle.</summary>
        List<int> Simplify(double epsilon)
        {
            var keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            // Where one road gives way to a wider or narrower one, the edge steps: keep the point.
            for (int k = 1; k + 1 < points.Count; k++) if (Math.Abs(half[k] - half[k - 1]) > 1e-6 || Math.Abs(half[k] - half[k + 1]) > 1e-6) keep[k] = true;
            var spans = new Stack<(int, int)>();
            spans.Push((0, points.Count - 1));
            while (spans.Count > 0)
            {
                var (i, j) = spans.Pop();
                double worst = 0;
                int at = -1;
                for (int k = i + 1; k < j; k++)
                {
                    double d = Geo.DistToSegment(points[k], points[i], points[j]);
                    if (d > worst)
                    {
                        worst = d;
                        at = k;
                    }
                }
                if (at < 0 || worst <= epsilon) continue;
                keep[at] = true;
                spans.Push((i, at));
                spans.Push((at, j));
            }
            var o = new List<int>();
            for (int k = 0; k < keep.Length; k++) if (keep[k]) o.Add(k);
            return o;
        }

        /// <summary>A line with any small loops in it cut out: where it crosses itself a little further on, straight to the crossing.</summary>
        static List<(Vec2 p, double s)> Unloop(List<(Vec2 p, double s)> line)
        {
            const int REACH = 16;
            var o = new List<(Vec2 p, double s)>(line);
            for (int i = 0; i + 1 < o.Count; i++)
            {
                for (int j = Math.Min(o.Count - 2, i + REACH); j >= i + 2; j--)
                {
                    var hit = Geo.SegmentIntersection(o[i].p, o[i + 1].p, o[j].p, o[j + 1].p);
                    if (hit == null) continue;
                    o.RemoveRange(i + 1, j - i);
                    o.Insert(i + 1, (hit.Value.point, (o[i].s + o[i + 1].s) / 2));
                    break;
                }
            }
            return o;
        }

        /// <summary>The fence all round the course: both sides, closed off behind the start line and past the finish.</summary>
        public Barrier Fence(double extra)
        {
            var left = Side(1, extra);
            var right = Side(-1, extra);
            var b = new Barrier();
            if (left.Count < 2 || right.Count < 2) return b;
            b.Add(left.ConvertAll(x => x.p));
            b.Add(right.ConvertAll(x => x.p));
            b.Add(left[0].p, right[0].p);
            b.Add(left[left.Count - 1].p, right[right.Count - 1].p);
            return b;
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

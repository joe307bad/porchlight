// Walking the sidewalk network: follow a sidewalk, take a turning at its end,
// turn around, or cross the road to the sidewalk opposite. Used by the
// townsfolk and by the player's auto-walk. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public struct Spot
    {
        public int path;
        public double s;
        public int dir;
    }

    /// <summary>One step of a planned route: walk `path` in `dir`, reached by crossing the road if `cross`.</summary>
    public struct Leg
    {
        public int path;
        public int dir;
        public bool cross;
    }

    public sealed class Itinerary
    {
        public Spot start;
        public List<Leg> legs;
        public int goal;
        /// <summary>Where on the goal sidewalk the walk ends (NaN: halfway along).</summary>
        public double goalS = double.NaN;
        /// <summary>Who or what is there, for the last direction (null: "your destination").</summary>
        public string destination;
        public bool joining;
    }

    public sealed class SidewalkWalk
    {
        /// <summary>Crossing the road costs about this many tiles of walking.</summary>
        const double CROSSING_COST = 8;
        /// <summary>
        /// Turning costs up to this many tiles (a U-turn; a right angle half that),
        /// so of routes about as long, the straightest wins: on across the side
        /// streets, rather than zigzagging block by block.
        /// </summary>
        const double TURN_COST = 12;
        /// <summary>Crossing the mouth of a side street at a junction costs this many tiles, besides the walk over.</summary>
        const double JUNCTION_CROSSING_COST = 2;

        sealed class Crossing
        {
            public Vec2 from;
            public Vec2 to;
            public double t;
            public Spot then;
        }

        readonly SidewalkNetwork net;
        readonly Rng rng;
        int path;
        double s;
        int dir;
        Crossing crossing;
        List<Leg> route = new List<Leg>();
        int? goal;
        double goalS = double.NaN;
        bool roaming;
        bool joining;
        bool needsPlan;

        public SidewalkWalk(SidewalkNetwork net, Rng rng, Spot start)
        {
            this.net = net;
            this.rng = rng;
            path = start.path;
            s = start.s;
            dir = start.dir;
        }

        public static SidewalkWalk Random(SidewalkNetwork net, Rng rng)
        {
            int path = rng.Int(0, net.paths.Count - 1);
            double s = rng.Range(0, net.paths[path].Length);
            int dir = rng.Chance(0.5) ? 1 : -1;
            return new SidewalkWalk(net, rng, new Spot { path = path, s = s, dir = dir });
        }

        /// <summary>Start from an arbitrary point: walk straight to the nearest sidewalk, then follow it.</summary>
        public static SidewalkWalk Join(SidewalkNetwork net, Rng rng, Vec2 from, int dir = 0)
        {
            int bestPath = 0;
            double bestS = 0, bestD = double.PositiveInfinity;
            for (int i = 0; i < net.paths.Count; i++)
            {
                var p = net.paths[i];
                double s = Geo.ArcPosition(from, p.points);
                var q = PointAlong(p, s);
                double d = Numbers.Hypot(q.x - from.x, q.y - from.y);
                if (d < bestD)
                {
                    bestPath = i;
                    bestS = s;
                    bestD = d;
                }
            }
            var walk = new SidewalkWalk(net, rng, new Spot { path = bestPath, s = bestS, dir = dir != 0 ? dir : rng.Chance(0.5) ? 1 : -1 });
            walk.crossing = new Crossing { from = from, to = PointAlong(net.paths[bestPath], bestS), t = 0, then = new Spot { path = bestPath, s = bestS, dir = walk.dir } };
            walk.joining = true;
            return walk;
        }

        /// <summary>
        /// A route along the sidewalks from `from` to the spot nearest `to`
        /// (whichever way along the first sidewalk is shorter). Only for its
        /// RoutePoints and Itinerary: nobody walks it.
        /// </summary>
        public static SidewalkWalk Toward(SidewalkNetwork net, Rng rng, Vec2 from, Vec2 to)
        {
            int goal = 0;
            double goalS = 0, bestD = double.PositiveInfinity;
            for (int i = 0; i < net.paths.Count; i++)
            {
                var p = net.paths[i];
                double s = Geo.ArcPosition(to, p.points);
                var q = PointAlong(p, s);
                double d = Numbers.Hypot(q.x - to.x, q.y - to.y);
                if (d < bestD)
                {
                    goal = i;
                    goalS = s;
                    bestD = d;
                }
            }
            SidewalkWalk best = null;
            double bestLength = double.PositiveInfinity;
            foreach (int dir in new[] { 1, -1 })
            {
                var walk = Join(net, rng, from, dir);
                walk.goal = goal;
                walk.goalS = goalS;
                walk.Plan();
                if (walk.goal == null) continue;
                double length = Geo.PolylineLength(walk.RoutePoints());
                if (length < bestLength)
                {
                    best = walk;
                    bestLength = length;
                }
            }
            return best;
        }

        double GoalS(SidewalkPath p) => double.IsNaN(goalS) ? p.Length / 2 : goalS;

        /// <summary>Keep heading somewhere far away, and on arrival pick another.</summary>
        public void Roam()
        {
            roaming = true;
            PickFarGoal();
        }

        public Vec2? GoalPoint()
        {
            if (goal == null) return null;
            var p = net.paths[goal.Value];
            return PointAlong(p, GoalS(p));
        }

        /// <summary>The planned route as a polyline from where we are to the goal.</summary>
        public List<Vec2> RoutePoints()
        {
            var pts = new List<Vec2>();
            if (goal == null) return pts;
            pts.Add(Position());
            int path = this.path;
            double s = this.s;
            int dir = this.dir;
            if (crossing != null)
            {
                pts.Add(crossing.to);
                path = crossing.then.path;
                s = crossing.then.s;
                dir = crossing.then.dir;
            }
            foreach (var leg in route)
            {
                if (leg.cross)
                {
                    var other = net.paths[leg.path];
                    s = Geo.ArcPosition(pts[pts.Count - 1], other.points);
                    pts.Add(PointAlong(other, s));
                }
                else if (leg.path == path)
                {
                    // Turning back at a dead end.
                    var p = net.paths[path];
                    pts.AddRange(Slice(p, s, dir > 0 ? p.Length : 0));
                    s = dir > 0 ? p.Length : 0;
                }
                else
                {
                    var p = net.paths[path];
                    pts.AddRange(Slice(p, s, dir > 0 ? p.Length : 0));
                    var next = net.paths[leg.path];
                    s = leg.dir > 0 ? 0 : next.Length;
                    pts.Add(PointAlong(next, s));
                }
                path = leg.path;
                dir = leg.dir;
            }
            if (path == goal)
            {
                var p = net.paths[path];
                pts.AddRange(Slice(p, s, GoalS(p)));
            }
            return pts;
        }

        public Itinerary Itinerary()
        {
            if (goal == null) return null;
            var start = crossing != null ? crossing.then : new Spot { path = path, s = s, dir = dir };
            return new Itinerary { start = start, legs = new List<Leg>(route), goal = goal.Value, goalS = goalS, joining = joining };
        }

        void PickFarGoal()
        {
            var here = Position();
            int best = -1;
            double bestD = -1;
            for (int k = 0; k < 12; k++)
            {
                int i = rng.Int(0, net.paths.Count - 1);
                var p = net.paths[i];
                var m = PointAlong(p, p.Length / 2);
                double d = Numbers.Hypot(m.x - here.x, m.y - here.y);
                if (d > bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            goal = best;
            goalS = double.NaN;
            Plan();
        }

        /// <summary>Plan the way to the goal; if there's none going this way, turn round and go the other.</summary>
        void Plan()
        {
            var want = goal;
            PlanFrom();
            if (goal != null || want == null) return;
            goal = want;
            if (crossing != null) crossing.then.dir = -crossing.then.dir;
            else dir = -dir;
            PlanFrom();
            if (goal != null) return;
            if (crossing != null) crossing.then.dir = -crossing.then.dir;
            else dir = -dir;
        }

        /// <summary>Dijkstra over (sidewalk, direction) states from where we are to the goal sidewalk.</summary>
        void PlanFrom()
        {
            route = new List<Leg>();
            if (goal == null) return;
            int startPath = crossing != null ? crossing.then.path : path;
            int startDir = crossing != null ? crossing.then.dir : dir;
            int n = net.paths.Count * 2;
            int Key(int p, int d) => p * 2 + (d > 0 ? 1 : 0);
            var cost = new double[n];
            for (int i = 0; i < n; i++) cost[i] = double.PositiveInfinity;
            var prev = new int[n];
            for (int i = 0; i < n; i++) prev[i] = -1;
            var viaCross = new byte[n];
            var heap = new MinHeap();
            int s0 = Key(startPath, startDir);
            cost[s0] = 0;
            heap.Push(s0, 0);
            int found = -1;
            while (heap.Size > 0)
            {
                int state = heap.Pop();
                int p = state >> 1;
                int d = (state & 1) != 0 ? 1 : -1;
                if (p == goal)
                {
                    found = state;
                    break;
                }
                void Relax(int next, double c, bool cross)
                {
                    if (cost[state] + c < cost[next])
                    {
                        cost[next] = cost[state] + c;
                        prev[next] = state;
                        viaCross[next] = cross ? (byte)1 : (byte)0;
                        heap.Push(next, cost[next]);
                    }
                }
                int end = d > 0 ? 1 : 0;
                var arrive = EndHeading(p, end, true);
                foreach (int link in net.links[p * 2 + end])
                {
                    int q = link >> 1;
                    var leave = EndHeading(q, link & 1, false);
                    double turn = (1 - (arrive.x * leave.x + arrive.y * leave.y)) / 2;
                    var from = EndPoint(p, end);
                    var to = EndPoint(q, link & 1);
                    double over = Numbers.Hypot(to.x - from.x, to.y - from.y) + (net.crossings.Contains(SidewalkNetwork.LinkKey(p * 2 + end, link)) ? JUNCTION_CROSSING_COST : 0);
                    Relax(Key(q, (link & 1) == 0 ? 1 : -1), net.paths[p].Length + turn * TURN_COST + over, false);
                }
                // A dead end: turn back along it.
                if (net.links[p * 2 + end].Count == 0) Relax(Key(p, -d), net.paths[p].Length + TURN_COST, false);
                int sibling = SiblingOf(p);
                if (sibling >= 0) Relax(Key(sibling, d), CROSSING_COST, true);
            }
            if (found < 0)
            {
                goal = null;
                return;
            }
            var legs = new List<Leg>();
            for (int st = found; st != s0 && st >= 0; st = prev[st])
                legs.Add(new Leg { path = st >> 1, dir = (st & 1) != 0 ? 1 : -1, cross = viaCross[st] == 1 });
            legs.Reverse();
            route = legs;
        }

        Vec2 EndPoint(int path, int end)
        {
            var pts = net.paths[path].points;
            return end == 0 ? pts[0] : pts[pts.Count - 1];
        }

        /// <summary>Which way you're going at one end of a sidewalk (end 0: its start): arriving there, or leaving from it.</summary>
        Vec2 EndHeading(int path, int end, bool arriving)
        {
            var pts = net.paths[path].points;
            Vec2 a, b;
            if (end == 0)
            {
                a = pts[Math.Min(pts.Count - 1, 1)];
                b = pts[0];
            }
            else
            {
                a = pts[Math.Max(0, pts.Count - 2)];
                b = pts[pts.Count - 1];
            }
            double l = Numbers.Hypot(b.x - a.x, b.y - a.y);
            if (l < 1e-9) return new Vec2(0, 0);
            double sign = arriving ? 1 : -1;
            return new Vec2((b.x - a.x) / l * sign, (b.y - a.y) / l * sign);
        }

        int SiblingOf(int p)
        {
            var me = net.paths[p];
            int o = -1;
            for (int i = 0; i < net.paths.Count; i++)
            {
                var q = net.paths[i];
                if (i != p && q.road == me.road && q.side != me.side) o = i;
            }
            return o;
        }

        public bool IsCrossing => crossing != null;

        public Vec2 Position()
        {
            if (crossing != null)
            {
                var c = crossing;
                return new Vec2(c.from.x + (c.to.x - c.from.x) * c.t, c.from.y + (c.to.y - c.from.y) * c.t);
            }
            return PointAlong(net.paths[path], s);
        }

        /// <summary>Unit vector of the way we're walking along the sidewalk.</summary>
        public Vec2 Heading()
        {
            var p = net.paths[path];
            var a = PointAlong(p, s);
            var b = PointAlong(p, Math.Max(0, Math.Min(p.Length, s + 0.3 * dir)));
            double l = Numbers.Hypot(b.x - a.x, b.y - a.y);
            if (l < 1e-6) return new Vec2(0, 0);
            return new Vec2((b.x - a.x) / l, (b.y - a.y) / l);
        }

        /// <summary>Unit vector from this sidewalk towards its road.</summary>
        public Vec2 TowardRoad()
        {
            var p = net.paths[path];
            var a = PointAlong(p, Math.Max(0, s - 0.15));
            var b = PointAlong(p, Math.Min(p.Length, s + 0.15));
            double tx = b.x - a.x, ty = b.y - a.y;
            double l = Numbers.Hypot(tx, ty);
            if (l == 0 || double.IsNaN(l)) l = 1;
            return new Vec2((-ty / l) * p.side, (tx / l) * p.side);
        }

        /// <summary>Walk `distance` tiles further and return the new position.</summary>
        public Vec2 Advance(double distance)
        {
            double left = distance;
            for (int guard = 0; left > 1e-9 && guard < 8; guard++)
            {
                if (crossing != null)
                {
                    var c = crossing;
                    double len = Math.Max(0.05, Numbers.Hypot(c.to.x - c.from.x, c.to.y - c.from.y));
                    double need = (1 - c.t) * len;
                    if (left < need)
                    {
                        c.t += left / len;
                        left = 0;
                    }
                    else
                    {
                        left -= need;
                        path = c.then.path;
                        s = c.then.s;
                        dir = c.then.dir;
                        crossing = null;
                        joining = false;
                    }
                    continue;
                }
                if (route.Count > 0 && route[0].cross)
                {
                    var leg = route[0];
                    route.RemoveAt(0);
                    dir = leg.dir;
                    if (CrossStreet(false)) continue;
                }
                if (roaming && (goal == null || (path == goal && route.Count == 0))) PickFarGoal();
                var p = net.paths[path];
                double total = p.Length;
                double next = s + left * dir;
                if (next >= 0 && next <= total)
                {
                    s = next;
                    left = 0;
                }
                else
                {
                    int end = next < 0 ? 0 : 1;
                    left -= end == 0 ? s : total - s;
                    s = end == 0 ? 0 : total;
                    AtEnd(end);
                }
            }
            return Position();
        }

        public void TurnAround()
        {
            if (crossing != null)
            {
                var c = crossing;
                var here = Position();
                crossing = new Crossing { from = here, to = c.from, t = 0, then = new Spot { path = path, s = s, dir = -dir } };
            }
            else dir = -dir;
            Plan();
        }

        /// <summary>Cross the road to the sidewalk directly opposite. False if there's no sidewalk across.</summary>
        public bool CrossStreet(bool replan = true)
        {
            if (crossing != null) return false;
            var here = Position();
            var p = net.paths[path];
            int target = -1;
            for (int i = 0; i < net.paths.Count; i++)
            {
                var q = net.paths[i];
                if (i != path && q.road == p.road && q.side != p.side) target = i;
            }
            if (target < 0) return false;
            var other = net.paths[target];
            double s2 = Geo.ArcPosition(here, other.points);
            crossing = new Crossing { from = here, to = PointAlong(other, s2), t = 0, then = new Spot { path = target, s = s2, dir = dir } };
            if (replan) Plan();
            return true;
        }

        void AtEnd(int end)
        {
            if (needsPlan)
            {
                needsPlan = false;
                Plan();
            }
            // The plan says turn back here (a dead end).
            if (route.Count > 0 && !route[0].cross && route[0].path == path)
            {
                route.RemoveAt(0);
                dir = end == 0 ? 1 : -1;
                return;
            }
            var options = net.links[path * 2 + end];
            int? routed = null;
            if (route.Count > 0 && !route[0].cross)
            {
                int want = route[0].path;
                foreach (int o in options)
                {
                    if (o >> 1 == want)
                    {
                        routed = o;
                        break;
                    }
                }
            }
            if (routed != null) route.RemoveAt(0);
            else if (goal != null) route = new List<Leg>();
            if (routed == null && goal != null) needsPlan = true;
            if (options.Count == 0 || (routed == null && goal == null && rng.Chance(0.08)))
            {
                dir = end == 0 ? 1 : -1;
                return;
            }
            int target = routed ?? rng.Pick(options);
            int np = target >> 1;
            bool fromStart = (target & 1) == 0;
            var pp = net.paths[np];
            double ns = fromStart ? 0 : pp.Length;
            crossing = new Crossing { from = Position(), to = PointAlong(pp, ns), t = 0, then = new Spot { path = np, s = ns, dir = fromStart ? 1 : -1 } };
        }

        /// <summary>Points along a sidewalk from arc position s0 to s1 (either direction), ends included.</summary>
        static List<Vec2> Slice(SidewalkPath p, double s0, double s1)
        {
            var o = new List<Vec2> { PointAlong(p, s0) };
            double lo = Math.Min(s0, s1), hi = Math.Max(s0, s1);
            var inner = new List<Vec2>();
            for (int k = 0; k < p.points.Count; k++) if (p.at[k] > lo && p.at[k] < hi) inner.Add(p.points[k]);
            if (!(s1 >= s0)) inner.Reverse();
            o.AddRange(inner);
            o.Add(PointAlong(p, s1));
            return o;
        }

        public static Vec2 PointAlong(SidewalkPath path, double s)
        {
            int k = 1;
            while (k < path.at.Count - 1 && path.at[k] < s) k++;
            var a = path.points[k - 1];
            var b = path.points[k];
            double f = (s - path.at[k - 1]) / Math.Max(1e-9, path.at[k] - path.at[k - 1]);
            return new Vec2(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f);
        }
    }
}

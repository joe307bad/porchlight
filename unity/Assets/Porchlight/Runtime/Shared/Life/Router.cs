// Ways over the town's lanes for drivers: the quickest by travel time as the
// roads are (each lane's length at its limit plus the hold-up measured on it
// lately, each turn's curve at its speed, a signal's average wait, and a heavy
// penalty for pulling out from a stop sign onto a bigger road), and the hunt for
// a parking spot when the street is full. Trips to the same place share one tree
// of ways there, worked out the first time it's asked for after the hold-ups
// were last measured (SetDelays). Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public sealed class Router
    {
        /// <summary>What pulling out from a stop sign onto a bigger road costs a route (seconds).</summary>
        public const double UNPROTECTED_S = 30;
        /// <summary>
        /// What a signal costs a route (seconds): the wait, on average, of a car turning up at any moment (red half
        /// the time, for half a stage on average). Counting it, routes don't thread every signal in town when there's
        /// a way round, and the town's traffic spreads over its streets.
        /// </summary>
        public const double SIGNAL_S = RoadNet.STAGE_MS / 1000.0 / 4;
        /// <summary>A stop sign's stop (seconds), as routes count it.</summary>
        const double STOP_S = 2;
        /// <summary>The most trees of ways kept at once.</summary>
        const int MOST_TREES = 16;

        readonly RoadNet net;
        /// <summary>The movements into each lane.</summary>
        readonly List<int>[] ins;
        readonly Dictionary<int, Tree> trees = new Dictionary<int, Tree>();
        readonly LinkedList<int> recent = new LinkedList<int>();

        sealed class Tree
        {
            /// <summary>Seconds from the start of each lane's queue to the start of the target's.</summary>
            public double[] cost;
            /// <summary>The movement to take at the end of each lane, -1 at the target or if there's no way.</summary>
            public int[] next;
            public LinkedListNode<int> node;
        }

        public Router(RoadNet net)
        {
            this.net = net;
            ins = new List<int>[net.lanes.Count];
            for (int i = 0; i < ins.Length; i++) ins[i] = new List<int>();
            foreach (var m in net.movements) ins[m.to].Add(m.id);
        }

        /// <summary>Each lane's hold-up (seconds over its time at the limit), as last measured.</summary>
        double[] delay;

        public double LaneTime(Lane l) => l.Span / l.speed + (delay != null ? delay[l.id] : 0);

        /// <summary>The lanes' hold-ups as measured now: every tree of ways is worked out again from them.</summary>
        public void SetDelays(double[] seconds)
        {
            delay = (double[])seconds.Clone();
            trees.Clear();
            recent.Clear();
        }

        /// <summary>What's left of `route` from `d` along its segment `step`, to `goal` along its last (seconds, as Route counts it).</summary>
        public double Left(List<int> route, int step, double d, double goal)
        {
            int n = net.lanes.Count;
            double t = 0;
            for (int k = step; k < route.Count; k++)
            {
                int seg = route[k];
                if (seg >= n)
                {
                    t += TurnTime(net.movements[seg - n]);
                    continue;
                }
                var l = net.lanes[seg];
                double from = k == step ? d : 0, to = k == route.Count - 1 ? goal : l.Span;
                t += Math.Max(0, to - from) / l.speed;
                if (k < route.Count - 1 && delay != null) t += delay[seg] * Math.Max(0, Math.Min(1, (l.Span - from) / Math.Max(1e-6, l.Span)));
            }
            return t;
        }

        public double TurnTime(Movement m) => m.length / m.speed + (m.unprotected ? UNPROTECTED_S : 0) + (m.stops ? STOP_S : 0) + (m.signal ? SIGNAL_S : 0);

        Tree TreeTo(int target)
        {
            if (trees.TryGetValue(target, out var t))
            {
                recent.Remove(t.node);
                recent.AddFirst(t.node);
                return t;
            }
            int n = net.lanes.Count;
            t = new Tree { cost = new double[n], next = new int[n] };
            for (int i = 0; i < n; i++)
            {
                t.cost[i] = double.PositiveInfinity;
                t.next[i] = -1;
            }
            var heap = new MinHeap();
            t.cost[target] = 0;
            heap.Push(target, 0);
            var done = new bool[n];
            while (heap.Size > 0)
            {
                int l = heap.Pop();
                if (done[l]) continue;
                done[l] = true;
                foreach (int mid in ins[l])
                {
                    var m = net.movements[mid];
                    double c = t.cost[l] + TurnTime(m) + LaneTime(net.lanes[m.from]);
                    if (c < t.cost[m.from] || (c == t.cost[m.from] && mid < t.next[m.from]))
                    {
                        t.cost[m.from] = c;
                        t.next[m.from] = mid;
                        heap.Push(m.from, c);
                    }
                }
            }
            t.node = recent.AddFirst(target);
            trees[target] = t;
            if (trees.Count > MOST_TREES)
            {
                int old = recent.Last.Value;
                recent.RemoveLast();
                trees.Remove(old);
            }
            return t;
        }

        /// <summary>
        /// The quickest way from `d` along lane `from`'s queue to any of `targets` (a lane, and how far
        /// along its queue), as the lanes and movements in turn (movements as lanes.Count + id), and which
        /// target; null if there's none.
        /// </summary>
        public List<int> Route(int from, double d, IReadOnlyList<(int lane, double d)> targets, out int chosen, out double seconds)
        {
            chosen = -1;
            seconds = double.PositiveInfinity;
            List<int> best = null;
            var fl = net.lanes[from];
            for (int k = 0; k < targets.Count; k++)
            {
                var (lane, td) = targets[k];
                var tl = net.lanes[lane];
                if (lane == from && td >= d - 1e-6)
                {
                    double c0 = (td - d) / fl.speed;
                    if (c0 < seconds)
                    {
                        seconds = c0;
                        chosen = k;
                        best = new List<int> { from };
                    }
                    continue;
                }
                var tree = TreeTo(lane);
                // Off the end of this lane by its best movement.
                int bestM = -1;
                double bestC = double.PositiveInfinity;
                foreach (int mid in fl.outs)
                {
                    var m = net.movements[mid];
                    double c = TurnTime(m) + tree.cost[m.to];
                    if (c < bestC)
                    {
                        bestC = c;
                        bestM = mid;
                    }
                }
                if (bestM < 0) continue;
                double total = Math.Max(0, fl.Span - d) / fl.speed + (delay != null ? delay[from] * Math.Max(0, Math.Min(1, (fl.Span - d) / Math.Max(1e-6, fl.Span))) : 0) + bestC + td / tl.speed;
                if (total >= seconds) continue;
                var segs = new List<int> { from, net.lanes.Count + bestM };
                int at = net.movements[bestM].to;
                int guard = net.lanes.Count + 1;
                while (at != lane && guard-- > 0)
                {
                    segs.Add(at);
                    int mid = tree.next[at];
                    if (mid < 0) break;
                    segs.Add(net.lanes.Count + mid);
                    at = net.movements[mid].to;
                }
                if (at != lane) continue;
                segs.Add(lane);
                seconds = total;
                chosen = k;
                best = segs;
            }
            return best;
        }

        /// <summary>
        /// Hunting for a parking spot from the end of lane `from`: out over the lanes nearest first, each
        /// turn's cost scaled by a factor in [0.1, 0.9) particular to this car and that lane (so cars
        /// hunting at once spread out), to the first lane with a spot `free` says is free. The way there
        /// (from `from`, as Route's), and the spot; null if there's none in reach.
        /// </summary>
        public List<int> Hunt(int from, int car, Func<int, bool> free, out int spot, int reach = 600)
        {
            spot = -1;
            int n = net.lanes.Count;
            var cost = new Dictionary<int, double> { [from] = 0 };
            var via = new Dictionary<int, int>();
            var heap = new MinHeap();
            heap.Push(from, 0);
            var done = new HashSet<int>();
            while (heap.Size > 0 && done.Count < reach)
            {
                int l = heap.Pop();
                if (!done.Add(l)) continue;
                if (l != from)
                {
                    foreach (int s in net.lanes[l].spots)
                    {
                        if (!free(s)) continue;
                        spot = s;
                        var segs = new List<int>();
                        int at = l;
                        while (at != from)
                        {
                            segs.Add(at);
                            int mid = via[at];
                            segs.Add(n + mid);
                            at = net.movements[mid].from;
                        }
                        segs.Add(from);
                        segs.Reverse();
                        return segs;
                    }
                }
                foreach (int mid in net.lanes[l].outs)
                {
                    var m = net.movements[mid];
                    double jitter = 0.1 + 0.8 * Layout01(car, m.to);
                    double c = cost[l] + TurnTime(m) * jitter + LaneTime(net.lanes[m.to]);
                    if (!cost.TryGetValue(m.to, out double was) || c < was)
                    {
                        cost[m.to] = c;
                        via[m.to] = mid;
                        heap.Push(m.to, c);
                    }
                }
            }
            return null;
        }

        /// <summary>A number in [0, 1) particular to (a, b).</summary>
        static double Layout01(int a, int b) => Math.Min(0.999999, Porchlight.Explore.Layout.Hash(a * 7919 + 17, b + 3));
    }
}

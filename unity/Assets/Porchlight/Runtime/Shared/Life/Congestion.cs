// Routes that know the traffic. A town's drivers don't all take the way that's
// quickest on empty roads: they know which streets are slow at this hour, and a
// driver stuck in a queue thinks again. So here:
//
// - Each lane keeps how long cars have lately taken over it, beyond its time
//   at the limit (its hold-up), measured as each leaves it. A queue nobody's
//   got out of yet counts too: its front car's been there this long already.
// - Every ROUTE_EVERY_MS the router's lanes take those hold-ups (half the old
//   figure, half the new, so routes don't swing), and each tree of ways is
//   worked out again from them: trips planned then go round what's slow.
// - A car held up on a lane for REROUTE_AFTER_MS looks again (at most every
//   REROUTE_EVERY_MS) and takes a way at least REROUTE_GAIN quicker if
//   there is one.
//
// - Each lane's flow over the last quarter hour, against its capacity, gives
//   the BPR curve's forecast of its delay, t₀ · 0.15 (v/c)⁴ (Bureau of Public
//   Roads 1964): a lane filling up costs routes more before its queue shows.
//   Routes take the larger of the forecast and the measured hold-up.
// - Drivers keep habits: a driver who's made a trip before knows the lanes
//   they took, which cost them a fifth less (Router.FAMILIAR), so they keep
//   to their way unless another's clearly quicker (Mahmassani & Chang 1987's
//   boundedly rational drivers, with an indifference band); and each day one
//   in ten of them (seeded by the driver, the trip and the day) works it out
//   afresh from the traffic as it is (MATSim's day-to-day replanning). Day on
//   day the town's routes settle towards Wardrop's equilibrium, where nobody's
//   way is clearly slower than another they could take, without every driver
//   jumping to the same street at once.
//
// It spreads the town's traffic over its streets instead of every car on the
// same main roads. (Routing by measured, not free-flow, times and rerouting
// drivers held up are ideas A/B Street describes.)

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public sealed partial class TownLife
    {
        /// <summary>How often the router takes the lanes' measured hold-ups.</summary>
        public const long ROUTE_EVERY_MS = 5 * TownClock.MINUTE;
        /// <summary>Held up on a lane this long, a driver looks for another way.</summary>
        public const long REROUTE_AFTER_MS = TownClock.MINUTE;
        /// <summary>No more often than this, a driver changes their way.</summary>
        const long REROUTE_EVERY_MS = 3 * TownClock.MINUTE;
        /// <summary>A new way's taken if it's at least this much quicker (a fifth).</summary>
        const double REROUTE_GAIN = 0.8;

        double[] holdSum, holdNow;
        int[] holdN;

        /// <summary>The BPR curve (Bureau of Public Roads 1964): delay t₀ · ALPHA · (v/c)^BETA.</summary>
        public const double BPR_ALPHA = 0.15, BPR_BETA = 4;
        /// <summary>The flow BPR reads: the last quarter hour, in ROUTE_EVERY_MS buckets.</summary>
        const int FLOW_BUCKETS = (int)(15 * TownClock.MINUTE / ROUTE_EVERY_MS);
        /// <summary>The most BPR forecasts for a lane (s): past capacity the curve runs away.</summary>
        const double BPR_MOST_S = 600;
        /// <summary>
        /// HCM's base follow-up headways at a two-way stop (s, HCM 2010 Exhibit 19-10): how close behind each other
        /// cars go from the minor road into a gap, turning right, straight across, or left.
        /// </summary>
        const double FOLLOW_RIGHT = 3.3, FOLLOW_THROUGH = 4.0, FOLLOW_LEFT = 3.5;
        /// <summary>The gap between cars leaving an all-way stop (s): HCM's departure headways run about 4 to 7.</summary>
        const double ALL_WAY_HEADWAY = 5;
        /// <summary>A driver who's made a trip before works its way out again one day in this many.</summary>
        public const int REPLAN_ONE_IN = 10;

        /// <summary>Cars onto each lane: this bucket, and the quarter hour's (ring of FLOW_BUCKETS).</summary>
        int[][] flowBuckets;
        int flowBucket;
        /// <summary>Each lane's delay as BPR forecasts it from the quarter hour's flow (s).</summary>
        double[] bpr;

        /// <summary>A car's come onto lane `l`: counted for BPR.</summary>
        void FlowOnto(int l)
        {
            if (flowBuckets != null) flowBuckets[flowBucket][l]++;
        }

        /// <summary>Lane `l`'s flow over the last quarter hour (cars an hour).</summary>
        public double LaneFlow(int l)
        {
            if (flowBuckets == null) return 0;
            int n = 0;
            foreach (var b in flowBuckets) n += b[l];
            return n * (TownClock.HOUR / (double)(FLOW_BUCKETS * ROUTE_EVERY_MS));
        }

        /// <summary>
        /// How many cars an hour lane `l` can let out at its end: the saturation flow on through, or onto a free
        /// junction; at a signal, its share of the green (SATURATION · effective green / cycle); at an all-way stop,
        /// one car every ALL_WAY_HEADWAY; out of a two-way stop, the HCM's potential capacity for its turns into the
        /// traffic they give way to, c = v·e^(−v·t_c/3600) / (1 − e^(−v·t_f/3600)), averaged over them.
        /// </summary>
        public double Capacity(int l)
        {
            var lane = net.lanes[l];
            if (lane.outs.Count == 0) return SATURATION;
            var first = net.movements[lane.outs[0]];
            var j = net.junctions[first.node];
            switch (j.control)
            {
                case Control.Signal:
                    return SATURATION * Math.Max(0, j.green[first.stage] - LOST_MS) / Math.Max(1, RoadNet.Cycle(j));
                case Control.AllWayStop:
                    return TownClock.HOUR / 1000.0 / ALL_WAY_HEADWAY;
                case Control.Stop when first.stops:
                {
                    double sum = 0;
                    foreach (int mid in lane.outs)
                    {
                        var m = net.movements[mid];
                        double v = 0;
                        var seen = new HashSet<int>();
                        foreach (int other in j.movements)
                        {
                            var b = net.movements[other];
                            if (GivesWay(m, b, j) && seen.Add(b.from)) v += LaneFlow(b.from);
                        }
                        double tc = m.turn == Turn.Right ? GAP_MINOR_RIGHT : m.turn == Turn.Straight ? GAP_MINOR_THROUGH : GAP_MINOR_LEFT;
                        double tf = m.turn == Turn.Right ? FOLLOW_RIGHT : m.turn == Turn.Straight ? FOLLOW_THROUGH : FOLLOW_LEFT;
                        sum += v < 1 ? 3600 / tf : v * Math.Exp(-v * tc / 3600) / (1 - Math.Exp(-v * tf / 3600));
                    }
                    return sum / lane.outs.Count;
                }
                default:
                    return SATURATION;
            }
        }

        /// <summary>Lane `l`'s delay as the BPR curve has it from the quarter hour's flow (s).</summary>
        public double BprDelay(int l)
        {
            var lane = net.lanes[l];
            double ratio = LaneFlow(l) / Math.Max(1, Capacity(l));
            return Math.Min(BPR_MOST_S, lane.Span / lane.speed * BPR_ALPHA * Math.Pow(ratio, BPR_BETA));
        }

        /// <summary>Car `c`'s leaving the lane it's on: how long it took, over its time at the limit.</summary>
        void Measured(Car c)
        {
            var l = net.lanes[c.seg];
            double took = (Now - c.laneSince) / 1000.0 - Math.Max(0, l.Span - c.laneFrom) / l.speed;
            // The hour's log counts the whole hold-up, a light's wait and all: it's what a driver sat through.
            Logged(c.seg, Math.Max(0, took));
            if (holdSum == null) return;
            // A signal's wait is counted by routes already (Router.SIGNAL_S).
            if (l.outs.Count > 0 && net.movements[l.outs[0]].signal) took -= Router.SIGNAL_S;
            holdSum[c.seg] += Math.Max(0, took);
            holdN[c.seg]++;
        }

        // --- The last day's hold-ups, an hour at a time (Manage's congestion slider) -------------

        /// <summary>How many hours back the log of hold-ups goes (this one, and the 23 before it).</summary>
        public const int HOURS_KEPT = 24;
        /// <summary>Which hour (since the epoch) each of the log's slots holds (-1: none yet).</summary>
        readonly long[] loggedHour = NewHours();
        /// <summary>Each slot's cars that left each lane, and their hold-ups (seconds, summed).</summary>
        int[][] loggedCars;
        double[][] loggedHold;

        static long[] NewHours()
        {
            var h = new long[HOURS_KEPT];
            for (int i = 0; i < h.Length; i++) h[i] = -1;
            return h;
        }

        /// <summary>A car's left `lane` held up `hold` seconds: into this hour's slot.</summary>
        void Logged(int lane, double hold)
        {
            if (loggedCars == null)
            {
                loggedCars = new int[HOURS_KEPT][];
                loggedHold = new double[HOURS_KEPT][];
                for (int i = 0; i < HOURS_KEPT; i++)
                {
                    loggedCars[i] = new int[Lanes];
                    loggedHold[i] = new double[Lanes];
                }
            }
            long hour = Now / TownClock.HOUR;
            int k = (int)(((hour % HOURS_KEPT) + HOURS_KEPT) % HOURS_KEPT);
            if (loggedHour[k] != hour)
            {
                loggedHour[k] = hour;
                Array.Clear(loggedCars[k], 0, Lanes);
                Array.Clear(loggedHold[k], 0, Lanes);
            }
            loggedCars[k][lane]++;
            loggedHold[k][lane] += hold;
        }

        /// <summary>The log's slot for the hour `ago` hours before this one, or -1 if nothing was logged then.</summary>
        int LoggedSlot(int ago)
        {
            if (loggedCars == null || ago < 0 || ago >= HOURS_KEPT) return -1;
            long hour = Now / TownClock.HOUR - ago;
            int k = (int)(((hour % HOURS_KEPT) + HOURS_KEPT) % HOURS_KEPT);
            return loggedHour[k] == hour ? k : -1;
        }

        /// <summary>One hour of the log: when it started, how many cars left a lane in it, and their hold-ups (seconds).</summary>
        public struct HourHold
        {
            public long from;
            public int cars;
            public double hold;
        }

        /// <summary>The last HOURS_KEPT hours, oldest first (this one, so far, last).</summary>
        public HourHold[] HoldsByHour()
        {
            var o = new HourHold[HOURS_KEPT];
            long now = Now / TownClock.HOUR;
            for (int i = 0; i < HOURS_KEPT; i++)
            {
                int ago = HOURS_KEPT - 1 - i;
                o[i].from = (now - ago) * TownClock.HOUR;
                int k = LoggedSlot(ago);
                if (k < 0) continue;
                for (int l = 0; l < Lanes; l++)
                {
                    o[i].cars += loggedCars[k][l];
                    o[i].hold += loggedHold[k][l];
                }
            }
            return o;
        }

        /// <summary>A road's hold-ups in an hour: both its lanes' cars, and their hold-ups (seconds, summed).</summary>
        public struct RoadHold
        {
            public int road, cars;
            public double hold;
            public double Mean => cars == 0 ? 0 : hold / cars;
        }

        /// <summary>The `top` roads that held cars up longest in all in the hour `ago` hours before this one, worst first.</summary>
        public List<RoadHold> MostHeldUp(int ago, int top)
        {
            var o = new List<RoadHold>();
            int k = LoggedSlot(ago);
            if (k < 0) return o;
            var byRoad = new Dictionary<int, RoadHold>();
            for (int l = 0; l < Lanes; l++)
            {
                if (loggedCars[k][l] == 0) continue;
                int road = net.lanes[l].road;
                byRoad.TryGetValue(road, out var r);
                r.road = road;
                r.cars += loggedCars[k][l];
                r.hold += loggedHold[k][l];
                byRoad[road] = r;
            }
            foreach (var r in byRoad.Values) if (r.hold > 0) o.Add(r);
            o.Sort((a, b) => a.hold != b.hold ? b.hold.CompareTo(a.hold) : a.road.CompareTo(b.road));
            if (o.Count > top) o.RemoveRange(top, o.Count - top);
            return o;
        }

        /// <summary>Each minute: every few minutes the router's told the hold-ups; drivers held up think again.</summary>
        void Congestion()
        {
            if (!options.liveRoutes) return;
            if (holdSum == null)
            {
                holdSum = new double[Lanes];
                holdN = new int[Lanes];
                holdNow = new double[Lanes];
                bpr = new double[Lanes];
                flowBuckets = new int[FLOW_BUCKETS][];
                for (int b = 0; b < FLOW_BUCKETS; b++) flowBuckets[b] = new int[Lanes];
            }
            if (TownClock.TimeOfDay(Now) % ROUTE_EVERY_MS < TownClock.MINUTE)
            {
                for (int l = 0; l < Lanes; l++)
                {
                    double now = holdN[l] > 0 ? holdSum[l] / holdN[l] : 0;
                    // A queue no one's got out of yet: its front car's been there this long.
                    var q = queues[l];
                    if (q.cars.Count > 0)
                    {
                        var f = cars[q.cars[0]];
                        var lane = net.lanes[l];
                        now = Math.Max(now, (Now - f.laneSince) / 1000.0 - Math.Max(0, lane.Span - f.laneFrom) / lane.speed - Router.SIGNAL_S);
                    }
                    holdNow[l] = Math.Max(0, 0.5 * holdNow[l] + 0.5 * now);
                    holdSum[l] = 0;
                    holdN[l] = 0;
                }
                // What routes cost: the hold-up measured, or BPR's forecast from the flow if that's more.
                for (int l = 0; l < Lanes; l++) bpr[l] = Math.Max(holdNow[l], BprDelay(l));
                router.SetDelays(bpr);
                flowBucket = (flowBucket + 1) % FLOW_BUCKETS;
                Array.Clear(flowBuckets[flowBucket], 0, Lanes);
            }
            foreach (var c in cars)
            {
                if (c.seg < 0 || c.seg >= Lanes || c.bus >= 0 || c.route == null || c.step >= c.route.Count - 1) continue;
                if (c.state != CarState.Queued && c.state != CarState.Waiting) continue;
                if (Now - c.laneSince < REROUTE_AFTER_MS || (c.rerouted >= 0 && Now - c.rerouted < REROUTE_EVERY_MS)) continue;
                Reroute(c);
            }
        }

        /// <summary>
        /// Driver `p`'s way from `from` (lane, `d` along) to `targets` for a trip from place `origin` to `dest`. If
        /// they've made it before, the lanes they took then are familiar (Router.FAMILIAR): they keep to that way
        /// unless another's clearly quicker. Each day one in REPLAN_ONE_IN of them (seeded by driver, trip and day)
        /// works it out afresh from the traffic as it is. Either way, the way taken is the habit from now on.
        /// </summary>
        List<int> HabitualRoute(Person p, Place origin, Place dest, int from, double d, IReadOnlyList<(int lane, double d)> targets, out int chosen, out double seconds)
        {
            if (p == null || !options.habits) return router.Route(from, d, targets, out chosen, out seconds);
            long key = HabitKey(origin, dest);
            HashSet<int> known = null;
            if (p.habits != null && p.habits.TryGetValue(key, out var h) && !Replans(p, key))
            {
                known = h;
                stats.habits++;
            }
            var way = router.Route(from, d, targets, known, out chosen, out seconds);
            if (way != null)
            {
                var lanes = new HashSet<int>();
                foreach (int seg in way) if (seg < Lanes) lanes.Add(seg);
                if (known != null && known.IsSupersetOf(lanes)) stats.keptWay++;
                (p.habits = p.habits ?? new Dictionary<long, HashSet<int>>())[key] = lanes;
            }
            return way;
        }

        static long HabitKey(Place origin, Place dest) => ((long)PlaceKey(origin) << 32) | (uint)PlaceKey(dest);

        static int PlaceKey(Place p) => p.border ? -1 - p.id : p.id;

        /// <summary>Is today the day driver `p` works their way for trip `key` out afresh?</summary>
        bool Replans(Person p, long key)
        {
            ulong h = 1469598103934665603UL;
            foreach (long v in new[] { seedHash, p.id, p.day, key })
            {
                h ^= (ulong)v;
                h *= 1099511628211UL;
                h ^= h >> 29;
            }
            return h % REPLAN_ONE_IN == 0;
        }

        /// <summary>The places car `c` can stop at the end of its way (as Plan has them).</summary>
        List<(int, double)> Targets(Car c)
        {
            var targets = new List<(int, double)>();
            // A service vehicle: the stop, call or gate it's going to, and no other (not the far side of the road).
            if (c.service >= 0 && c.route != null && c.route.Count > 0)
            {
                targets.Add((c.route[c.route.Count - 1], c.goal));
                return targets;
            }
            if (c.target >= 0)
            {
                var s = net.spots[c.target];
                targets.Add((s.lane, s.s - net.lanes[s.lane].start));
            }
            else if (c.dest.border) targets.Add((net.borders[c.dest.id].exit, net.lanes[net.borders[c.dest.id].exit].Span));
            else if (net.access.TryGetValue(c.dest.id, out var a))
                foreach (int l in net.LanesFor(a)) targets.Add((l, Math.Max(0, Math.Min(net.lanes[l].Span, net.DoneFor(net.lanes[l], a) - net.lanes[l].start))));
            return targets;
        }

        /// <summary>Car `c`'s held up: a quicker way from here, if there's one.</summary>
        void Reroute(Car c)
        {
            c.rerouted = Now;
            double d = Math.Max(0, Front(c));
            var targets = Targets(c);
            if (targets.Count == 0) return;
            var route = router.Route(c.seg, d, targets, out int k, out double seconds);
            if (route == null || route.Count < 2 || route[0] != c.seg) return;
            double left = router.Left(c.route, c.step, d, c.goal);
            if (seconds > left * REROUTE_GAIN) return;
            if (route.Count == c.route.Count - c.step && c.route.GetRange(c.step, route.Count).SequenceEqualTo(route)) return;
            c.route = route;
            c.step = 0;
            c.goal = targets[k].Item2;
            c.routeS = seconds;
            c.routed = Now;
            c.reroutes++;
            stats.rerouted++;
            blockedBy.Remove(c.id);
            // At the front: it asks for its new turn now.
            if (c.state == CarState.Waiting) Schedule(K_CAR, c.id, Now);
        }
    }

    static class ListEq
    {
        public static bool SequenceEqualTo(this List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}

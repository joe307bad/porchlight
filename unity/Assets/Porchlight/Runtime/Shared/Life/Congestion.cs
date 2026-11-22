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
                router.SetDelays(holdNow);
            }
            foreach (var c in cars)
            {
                if (c.seg < 0 || c.seg >= Lanes || c.bus >= 0 || c.route == null || c.step >= c.route.Count - 1) continue;
                if (c.state != CarState.Queued && c.state != CarState.Waiting) continue;
                if (Now - c.laneSince < REROUTE_AFTER_MS || (c.rerouted >= 0 && Now - c.rerouted < REROUTE_EVERY_MS)) continue;
                Reroute(c);
            }
        }

        /// <summary>The places car `c` can stop at the end of its way (as Plan has them).</summary>
        List<(int, double)> Targets(Car c)
        {
            var targets = new List<(int, double)>();
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

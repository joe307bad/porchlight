// The town's bus: one loop, from the middle of town out to each gate's first
// junction and back, a stop at each. A bus leaves the first stop every
// HEADWAY (more buses on a long loop), stops a few seconds at each stop (standing in
// its lane, so the cars behind wait) and, between rounds, lays over out of the way.
// Riders walk to the stop nearest them, wait, get on any bus, and get off at the
// stop nearest where they're going: they take the bus when that beats walking.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public sealed class BusStop
    {
        public int id;
        public int lane;
        /// <summary>How far along the lane's queue the bus stops.</summary>
        public double d;
        /// <summary>Where riders wait.</summary>
        public Vec2 at;
        public int node;
    }

    public sealed partial class TownLife
    {
        /// <summary>A bus leaves the first stop this often.</summary>
        public const long HEADWAY = 20 * TownClock.MINUTE;
        /// <summary>At each stop.</summary>
        public const long DWELL = 10_000;
        public const double BUS_LENGTH = 3.1;
        /// <summary>About this far between stops (tiles: 180 m).</summary>
        const double STOP_EVERY = 45;
        /// <summary>No bus in this long: walk instead.</summary>
        const long GIVE_UP_WAITING = 45 * TownClock.MINUTE;

        public readonly List<BusStop> busStops = new List<BusStop>();
        /// <summary>The way from each stop to the next (segments, as a car's route).</summary>
        readonly List<List<int>> busLegs = new List<List<int>>();
        readonly List<double> busLegSeconds = new List<double>();
        public readonly List<Car> buses = new List<Car>();
        /// <summary>Why the town has no bus, if it hasn't.</summary>
        public string busWhy;
        /// <summary>The bus loop's lanes and turns, for Manage.</summary>
        public List<int> busRoute;
        readonly Dictionary<int, List<int>> riders = new Dictionary<int, List<int>>();
        readonly List<List<int>> waitingAtStop = new List<List<int>>();
        /// <summary>Who's on which bus (person → bus car).</summary>
        readonly Dictionary<int, int> riding = new Dictionary<int, int>();

        void MakeBusRoute()
        {
            busWhy = "no junctions or gates";
            if (!options.buses || net.junctions.Count == 0 || net.borders.Count == 0) return;
            // The middle: the junction nearest the town's centre, of three or more roads.
            var centre = model?.center ?? new Vec2(0, 0);
            int middle = -1;
            double best = double.PositiveInfinity;
            var nodes = new List<int>(net.junctions.Keys);
            nodes.Sort();
            foreach (int n in nodes)
            {
                var j = net.junctions[n];
                if (j.legs.Count < 3) continue;
                double d = Geo.Dist(j.at, centre);
                if (d < best)
                {
                    best = d;
                    middle = n;
                }
            }
            busWhy = "no middle";
            if (middle < 0) return;
            var stops = new List<int> { middle };
            // Each gate's first junction in.
            var byAngle = new List<(double angle, int node)>();
            foreach (var b in net.borders)
            {
                int lane = b.entry, guard = 64;
                int node = -1;
                while (guard-- > 0)
                {
                    var l = net.lanes[lane];
                    if (net.junctions.TryGetValue(l.toNode, out var j) && j.legs.Count >= 3)
                    {
                        node = l.toNode;
                        break;
                    }
                    int next = -1;
                    foreach (int m in l.outs) if (!net.movements[m].uturn) { next = net.movements[m].to; break; }
                    if (next < 0) break;
                    lane = next;
                }
                if (node < 0 || stops.Contains(node) || byAngle.Exists(x => x.node == node)) continue;
                var at = net.nodeAt[node];
                byAngle.Add((DetMath.Atan2(at.y - centre.y, at.x - centre.x), node));
            }
            byAngle.Sort((a, b) => a.angle != b.angle ? a.angle.CompareTo(b.angle) : a.node.CompareTo(b.node));
            foreach (var x in byAngle) stops.Add(x.node);
            if (stops.Count < 2) return;
            // A lane coming into each (the longest, main roads first) to route the loop through.
            var through = new List<(int lane, double d)>();
            foreach (int node in stops)
            {
                Lane pick = null;
                foreach (var l in net.lanes)
                {
                    if (l.toNode != node || l.exit || l.entry || l.Span < BUS_LENGTH + 1) continue;
                    if (pick == null || l.rank > pick.rank || (l.rank == pick.rank && l.Span > pick.Span)) pick = l;
                }
                if (pick != null) through.Add((pick.id, pick.Span / 2 + BUS_LENGTH / 2));
            }
            busWhy = $"{stops.Count} waypoints, {through.Count} with a lane in";
            if (through.Count < 2) return;
            // The loop through them all and back to the first.
            var loopSegs = new List<int>();
            for (int k = 0; k < through.Count; k++)
            {
                var a = through[k];
                var b = through[(k + 1) % through.Count];
                var way = router.Route(a.lane, a.d, new List<(int, double)> { b }, out _, out _);
                if (way == null)
                {
                    busWhy = $"no way from waypoint {k} to the next";
                    return;
                }
                if (loopSegs.Count > 0) way.RemoveAt(0);
                loopSegs.AddRange(way);
            }
            // The last segment is the first lane again: the loop proper runs to just before it.
            loopSegs.RemoveAt(loopSegs.Count - 1);
            // Stops: the first lane's, then on each lane long enough once STOP_EVERY or more has gone by.
            var stopAt = new List<(int index, double d)> { (0, through[0].d) };
            double since = 0;
            for (int i = 1; i < loopSegs.Count; i++)
            {
                int seg = loopSegs[i];
                since += SegLength(seg);
                if (seg >= Lanes || since < STOP_EVERY) continue;
                var l = net.lanes[seg];
                if (l.Span < BUS_LENGTH + 2 || l.exit || l.entry) continue;
                stopAt.Add((i, l.Span / 2 + BUS_LENGTH / 2));
                since = 0;
            }
            busWhy = "fewer than two stops";
            if (stopAt.Count < 2) return;
            busWhy = null;
            foreach (var (index, d) in stopAt)
            {
                var l = net.lanes[loopSegs[index]];
                busStops.Add(new BusStop { id = busStops.Count, lane = l.id, d = d, at = net.LanePoint(l.road, l.dir, l.start + d, 1.2).at, node = l.toNode });
            }
            busRoute = loopSegs;
            double loop = 0;
            for (int k = 0; k < stopAt.Count; k++)
            {
                int from = stopAt[k].index, to = k + 1 < stopAt.Count ? stopAt[k + 1].index : loopSegs.Count;
                var leg = new List<int>();
                for (int i = from; i <= to; i++) leg.Add(i < loopSegs.Count ? loopSegs[i] : loopSegs[0]);
                double secs = 0;
                for (int i = 0; i < leg.Count; i++)
                {
                    double len = SegLength(leg[i]);
                    if (i == 0) len -= stopAt[k].d;
                    if (i == leg.Count - 1) len -= SegLength(leg[i]) - (k + 1 < stopAt.Count ? stopAt[k + 1].d : stopAt[0].d);
                    secs += Math.Max(0, len) / SegSpeed(leg[i]);
                }
                busLegs.Add(leg);
                busLegSeconds.Add(secs);
                loop += secs + DWELL / 1000.0;
            }
            int count = Math.Max(1, (int)Math.Ceiling(loop * 1000 / HEADWAY));
            for (int k = 0; k < count; k++)
            {
                var c = new Car { id = cars.Count, owner = -1, bus = k, length = BUS_LENGTH };
                cars.Add(c);
                buses.Add(c);
            }
            for (int k = 0; k < busStops.Count; k++) waitingAtStop.Add(new List<int>());
        }

        /// <summary>When bus `k` next leaves the first stop, at or after `t`.</summary>
        long NextDeparture(int k, long t)
        {
            long cycle = HEADWAY * buses.Count;
            long phase = k * HEADWAY;
            long n = (long)Math.Ceiling((t - phase) / (double)cycle);
            return phase + n * cycle;
        }

        void StartBuses(long from)
        {
            for (int k = 0; k < buses.Count; k++) Schedule(K_BUS, k, Math.Max(from, NextDeparture(k, from) - DWELL));
        }

        /// <summary>A bus due out of its layover: onto the road at the first stop.</summary>
        void OnBusDue(int k)
        {
            var c = buses[k];
            if (c.seg >= 0 || takenSlots.Contains(c.slot)) return;
            var stop = busStops[0];
            if (!PutOnRoad(c, stop.lane, stop.d))
            {
                Schedule(K_BUS, k, Now + 5000);
                return;
            }
            c.route = new List<int> { stop.lane };
            c.step = 0;
            c.goal = stop.d;
            c.busStop = 0;
            AtStopNow(c);
        }

        /// <summary>Reached the stop it was going to.</summary>
        void AtStop(Car c)
        {
            c.busStop = (c.busStop + 1) % busStops.Count;
            AtStopNow(c);
        }

        void AtStopNow(Car c)
        {
            c.state = CarState.AtStop;
            int stop = c.busStop;
            if (!riders.TryGetValue(c.id, out var on)) riders[c.id] = on = new List<int>();
            // Off, then on.
            foreach (int id in on.ToArray())
            {
                var p = people[id];
                var leg = p.trip?.legs[p.trip.leg];
                if (leg == null || leg.alight != stop) continue;
                on.Remove(id);
                riding.Remove(id);
                NextLeg(p);
            }
            var waiting = waitingAtStop[stop];
            foreach (int id in waiting.ToArray())
            {
                var p = people[id];
                waiting.Remove(id);
                Cancel(K_WALK, id);
                on.Add(id);
                riding[id] = c.id;
                FreePersonSlot(p);
                stats.boarded++;
            }
            long go = Now + DWELL;
            Schedule(K_CAR, c.id, go);
        }

        void LeaveStop(Car c)
        {
            int stop = c.busStop;
            // Back at the first stop with time to spare: lay over off the road (riders still aboard stay on).
            if (stop == 0 && NextDeparture(c.bus, Now) - Now > 2 * DWELL && (!riders.TryGetValue(c.id, out var on) || on.Count == 0))
            {
                RemoveFromRoad(c);
                Schedule(K_BUS, c.bus, NextDeparture(c.bus, Now) - DWELL);
                return;
            }
            c.route = new List<int>(busLegs[stop]);
            c.step = 0;
            c.goal = busStops[(stop + 1) % busStops.Count].d;
            c.state = CarState.Crossing;
            SetInterval(c, Front(c), c.route.Count == 1 ? c.goal : SegLength(c.seg));
        }

        /// <summary>The bus is taken only if it's at least this much quicker than walking (a fifth).</summary>
        public const double BUS_EDGE = 0.8;

        /// <summary>Is the bus quicker than walking from `from` to `to` (stop to stop the nearest each end)?</summary>
        bool BusBetter(Person p, Vec2 from, Vec2 to, out int board, out int alight, ref ModeWhy why)
        {
            board = alight = -1;
            if (busStops.Count < 2 || buses.Count == 0) return false;
            board = NearestStop(from);
            alight = NearestStop(to);
            if (board == alight) return false;
            double walk = Geo.Dist(from, to) / p.pace;
            double ride = 0;
            for (int k = board; k != alight; k = (k + 1) % busStops.Count) ride += busLegSeconds[k] + DWELL / 1000.0;
            // The wait by the timetable: the first bus at the stop after they've walked there.
            double toStop = Geo.Dist(from, busStops[board].at) / p.pace;
            long there = Now + (long)(toStop * 1000);
            double wait = (NextAt(board, there) - there) / 1000.0;
            double fromStop = Geo.Dist(busStops[alight].at, to) / p.pace;
            double bus = toStop + wait + ride + fromStop;
            why.busWeighed = true;
            why.walkS = walk;
            why.toStopS = toStop;
            why.waitS = wait;
            why.rideS = ride;
            why.fromStopS = fromStop;
            why.board = board;
            why.alight = alight;
            return bus < walk * BUS_EDGE;
        }

        /// <summary>When the next bus is due at stop `stop` at or after `t`, by the timetable.</summary>
        long NextAt(int stop, long t)
        {
            double offset = 0;
            for (int k = 0; k < stop; k++) offset += busLegSeconds[k] + DWELL / 1000.0;
            long off = (long)(offset * 1000) + DWELL;
            long best = long.MaxValue;
            for (int k = 0; k < buses.Count; k++) best = Math.Min(best, NextDeparture(k, t - off) + off);
            return best;
        }

        int NearestStop(Vec2 p)
        {
            int best = 0;
            for (int k = 1; k < busStops.Count; k++) if (Geo.Dist(busStops[k].at, p) < Geo.Dist(busStops[best].at, p)) best = k;
            return best;
        }

        void WaitForBus(Person p, Leg leg)
        {
            p.standing = busStops[leg.board].at;
            TakePersonSlot(p);
            waitingAtStop[leg.board].Add(p.id);
            // Nothing comes (the player's taken the bus): give up.
            Schedule(K_WALK, p.id, Now + GIVE_UP_WAITING);
        }

        /// <summary>Off any bus and out of any queue for one (a trip given up).</summary>
        void LeaveBus(Person p)
        {
            foreach (var w in waitingAtStop) w.Remove(p.id);
            if (riding.TryGetValue(p.id, out int bus))
            {
                riding.Remove(p.id);
                if (riders.TryGetValue(bus, out var on)) on.Remove(p.id);
            }
        }

        /// <summary>The player's taken a bus: its riders are put where they were going, and it's off the road until let go.</summary>
        void TakeBus(Car c)
        {
            if (riders.TryGetValue(c.id, out var on))
                foreach (int id in on.ToArray())
                    Fail(people[id], "their bus was taken");
            int slot = c.slot;
            RemoveFromRoad(c);
            c.slot = slot;
            carSlots[slot] = new Pose { who = c.id };
            Cancel(K_BUS, c.bus);
        }

        void RestoreBus(int slot)
        {
            int k = slot - busSlotsFrom;
            if (k >= 0 && k < buses.Count) Schedule(K_BUS, k, Now);
        }

        /// <summary>A person's still waiting for a bus that hasn't come.</summary>
        bool WaitingForBus(Person p)
        {
            foreach (var w in waitingAtStop) if (w.Contains(p.id)) return true;
            return false;
        }
    }
}

// The town's service vehicles (docs/town-life.md §9): bin lorries and emergency
// vehicles, each with a slot of its own (drawn as its own model, as the buses are).
//
// Bin lorries. Each neighbourhood (Perry's units, from the town plan) is a
// collection zone with its own weekday, Monday to Friday in turn. On its day, at
// 7 am, a lorry comes in by the gate nearest the zone's first homes and works
// through them in order, nearest next, stopping in the lane at each stretch of
// houses while the crew empties the bins (holding up whoever's behind), then
// leaves by the nearest gate. The depot is beyond the town.
//
// Emergency vehicles. Ambulances and a fire engine answer calls that come at
// random (a Poisson process at rough national rates a person a year), from
// beyond the town (there's no hospital or fire station in it yet), with sirens:
// they go on red and through stop signs without waiting for a gap, drivers ahead
// of them in the lane slow and pull over to let them by, and drivers about to
// enter a junction they're coming to wait for them. At the call they stay a
// while, then go back out of town without sirens.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;

namespace Porchlight.Life
{
    public enum ServiceKind { Bins, Ambulance, FireEngine }

    /// <summary>A service vehicle: its kind, its car, and its round or its calls.</summary>
    public sealed class Service
    {
        public int id;
        public ServiceKind kind;
        public Car car;
        /// <summary>A bin lorry's zone (an index into the town's neighbourhoods, or 0 if it has none) and its weekday (0 Sunday).</summary>
        public int zone, weekday;
        /// <summary>A bin lorry's stops in order: where it stands in the lane, and how many houses it empties there.</summary>
        public List<(int building, int lane, double d, int houses)> stops = new List<(int, int, double, int)>();
        /// <summary>The stop it's making or driving to.</summary>
        public int next;
        /// <summary>How many calls it's answered (an emergency vehicle), or rounds it's done (a bin lorry).</summary>
        public int done;
        /// <summary>An emergency vehicle at a call: where (a building).</summary>
        public int call = -1;
        /// <summary>The cars it's had pull over for it.</summary>
        public readonly List<int> pulled = new List<int>();
    }

    public sealed partial class TownLife
    {
        /// <summary>A bin lorry's round starts at this time of day.</summary>
        public const long BINS_FROM = 7 * TownClock.HOUR;
        /// <summary>Houses this close (tiles) to the last stop's are emptied from it: a stretch of kerb, not every door.</summary>
        const double BIN_STRETCH = 6;
        /// <summary>A stop's time (ms): the crew wheels the bins to the back and tips them, about 20 s a stop and 8 s a house more (an estimate).</summary>
        const long BIN_STOP_MS = 20_000, BIN_HOUSE_MS = 8_000, BIN_MOST_MS = 60_000;
        /// <summary>
        /// Calls a person a year, rough estimates of the national order of magnitude (not taken from a dataset line
        /// by line): ambulances answer something like 30 million calls a year in the US (NEMSIS's yearly activations),
        /// about one for every eleven people; fire departments some 36 million (NFPA's Fire Department Calls), most of
        /// them medical, and a fire engine goes to a smaller share of those.
        /// </summary>
        const double AMBULANCE_CALLS = 0.088, FIRE_CALLS = 0.04;
        /// <summary>A town has an ambulance for every this many people (at least one), and one fire engine.</summary>
        const int PEOPLE_AN_AMBULANCE = 12_000;
        /// <summary>At a call: about a quarter hour for an ambulance, longer for a fire engine (estimates).</summary>
        const long AMBULANCE_SCENE_MS = 15 * TownClock.MINUTE, FIRE_SCENE_MS = 25 * TownClock.MINUTE;
        /// <summary>Drivers ahead in the lane this near (tiles) pull over for a siren.</summary>
        public const double SIREN_REACH = 20;
        /// <summary>A driver pulled over creeps along at this (tiles a second) and stands this far (tiles) towards the kerb.</summary>
        const double PULL_SPEED = 0.4, PULL_ASIDE = 0.35;
        /// <summary>How long (ms) a driver takes to steer over to the kerb, or back out.</summary>
        const long PULL_MS = 2000;
        /// <summary>A driver about to enter a junction waits for a siren this many seconds off it.</summary>
        const double SIREN_SECONDS = 6;
        /// <summary>How often a siren on the road looks at the lane ahead (ms).</summary>
        const long SIREN_TICK_MS = 250;

        /// <summary>The town's service vehicles: bin lorries first, then emergency vehicles.</summary>
        public readonly List<Service> services = new List<Service>();
        /// <summary>The emergency vehicles on the road with sirens going.</summary>
        readonly List<Service> sirens = new List<Service>();

        /// <summary>The model each kind's drawn as.</summary>
        public static string ServiceModel(ServiceKind k) => k == ServiceKind.Bins ? "cars/garbage-truck" : k == ServiceKind.Ambulance ? "cars/ambulance" : "cars/firetruck";

        void MakeServices()
        {
            if (net.borders.Count == 0 || net.access.Count == 0) return;
            // Bin rounds: the homes, by their nearest neighbourhood.
            var hoods = model?.neighbourhoods ?? new List<Porchlight.Model.Neighbourhood>();
            var zones = new SortedDictionary<int, List<int>>();
            foreach (int b in homesList)
            {
                var door = net.access[b].door;
                int best = 0;
                double bestD = double.PositiveInfinity;
                for (int z = 0; z < hoods.Count; z++)
                {
                    double d = Geo.Dist(door, hoods[z].middle);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = z;
                    }
                }
                if (!zones.TryGetValue(best, out var list)) zones[best] = list = new List<int>();
                list.Add(b);
            }
            int n = 0;
            foreach (var kv in zones)
            {
                var s = new Service { id = services.Count, kind = ServiceKind.Bins, zone = kv.Key, weekday = 1 + n++ % 5 };
                s.stops = BinStops(kv.Value);
                if (s.stops.Count == 0) continue;
                AddService(s, 3.4);
            }
            int ambulances = Math.Max(1, (int)Math.Round(people.Count / (double)PEOPLE_AN_AMBULANCE));
            for (int k = 0; k < ambulances; k++) AddService(new Service { id = services.Count, kind = ServiceKind.Ambulance }, 2.0);
            AddService(new Service { id = services.Count, kind = ServiceKind.FireEngine }, 3.6);
        }

        void AddService(Service s, double length)
        {
            s.car = new Car { id = cars.Count, service = s.id, length = length, state = CarState.Parked, offMap = true };
            cars.Add(s.car);
            services.Add(s);
        }

        /// <summary>A zone's bin stops: its homes nearest next from the one nearest a gate, a stretch of kerb a stop.</summary>
        List<(int, int, double, int)> BinStops(List<int> homes)
        {
            var o = new List<(int, int, double, int)>();
            var left = new List<int>(homes);
            left.Sort();
            if (left.Count == 0) return o;
            // From the home nearest any gate.
            int start = 0;
            double startD = double.PositiveInfinity;
            for (int i = 0; i < left.Count; i++)
                foreach (var g in net.borders)
                {
                    double d = Geo.Dist(net.access[left[i]].door, g.at);
                    if (d < startD)
                    {
                        startD = d;
                        start = i;
                    }
                }
            var at = net.access[left[start]].door;
            int cur = left[start];
            left.RemoveAt(start);
            var order = new List<int> { cur };
            while (left.Count > 0)
            {
                int best = 0;
                double bestD = double.PositiveInfinity;
                for (int i = 0; i < left.Count; i++)
                {
                    double d = Geo.Dist(at, net.access[left[i]].door);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }
                cur = left[best];
                at = net.access[cur].door;
                left.RemoveAt(best);
                order.Add(cur);
            }
            // A stop for each stretch: the houses near the last stop's, on the same lane, are emptied from it.
            Vec2 stopAt = default;
            foreach (int b in order)
            {
                var a = net.access[b];
                // Its own side's lane, unless that one only leads out of town (by a gate): then across the road.
                int lane = -1;
                foreach (int l in net.LanesFor(a))
                    if (!net.lanes[l].exit && !net.lanes[l].entry && net.lanes[l].outs.Count > 0)
                    {
                        lane = l;
                        break;
                    }
                if (lane < 0) continue;
                double d = Math.Max(0, Math.Min(net.lanes[lane].Span, net.DoneFor(net.lanes[lane], a) - net.lanes[lane].start));
                if (o.Count > 0 && o[o.Count - 1].Item2 == lane && Geo.Dist(stopAt, a.door) <= BIN_STRETCH)
                {
                    var last = o[o.Count - 1];
                    o[o.Count - 1] = (last.Item1, last.Item2, last.Item3, last.Item4 + 1);
                    continue;
                }
                o.Add((b, lane, d, 1));
                stopAt = a.door;
            }
            return o;
        }

        /// <summary>The day of the week at `t` (0 Sunday): 1 January 1970 was a Thursday.</summary>
        static int Weekday(long t) => (int)(((TownClock.DayOf(t) + 4) % 7 + 7) % 7);

        void StartServices(long from)
        {
            foreach (var s in services) Schedule(K_SERVICE, s.id, NextDue(s, from));
        }

        /// <summary>When service `s` next goes out, at or after `t`: a bin lorry's next round, an emergency vehicle's next call.</summary>
        long NextDue(Service s, long t)
        {
            if (s.kind == ServiceKind.Bins)
            {
                for (int k = 0; k <= 7; k++)
                {
                    long day = TownClock.DayOf(t) + k;
                    long start = TownClock.At(day, BINS_FROM + s.zone % 5 * 3 * TownClock.MINUTE);
                    if (Weekday(start) != s.weekday) continue;
                    // Today's round, if it's not over: from now (a town started mid-round sends it out now).
                    if (start >= t) return start;
                    if (k == 0 && s.done == 0 && t < start + 4 * TownClock.HOUR) return t;
                }
                return t + 7 * TownClock.DAY;
            }
            // A call: each minute a chance, the vehicle's share of the town's calls a year, seeded by vehicle and minute.
            int same = 0;
            foreach (var o in services) if (o.kind == s.kind) same++;
            double perYear = options.callRate * people.Count * (s.kind == ServiceKind.Ambulance ? AMBULANCE_CALLS : FIRE_CALLS) / Math.Max(1, same);
            double p = perYear / (365.0 * 24 * 60);
            long minute = (long)Math.Floor(t / (double)TownClock.MINUTE) + 1;
            for (long m = minute; m < minute + 60L * 24 * 60; m++)
                if (Chance(s.id, m) < p) return m * TownClock.MINUTE;
            return t + 60 * TownClock.DAY;
        }

        /// <summary>A number in [0, 1) for service `id` at minute `m`, the same on every machine.</summary>
        double Chance(int id, long m)
        {
            ulong h = (ulong)seedHash ^ ((ulong)id * 0x9E3779B97F4A7C15UL) ^ ((ulong)m * 0xBF58476D1CE4E5B9UL);
            h ^= h >> 31;
            h *= 0x94D049BB133111EBUL;
            h ^= h >> 29;
            return (h >> 11) / (double)(1UL << 53);
        }

        /// <summary>Service `k`'s time to go out: a bin round, or a call.</summary>
        void OnServiceDue(int k)
        {
            var s = services[k];
            var c = s.car;
            if (takenSlots.Contains(c.slot)) return;
            if (c.seg >= 0)
            {
                // Still out (a call while at another): the next.
                if (s.kind != ServiceKind.Bins) Schedule(K_SERVICE, k, NextDue(s, Now));
                return;
            }
            Vec2 to;
            if (s.kind == ServiceKind.Bins)
            {
                s.next = 0;
                to = net.access[s.stops[0].building].door;
            }
            else
            {
                s.call = calls.Count > 0 ? calls[(int)Math.Min(calls.Count - 1, Chance(s.id, Now / 1000 + 7) * calls.Count)] : -1;
                if (s.call < 0 || !net.access.ContainsKey(s.call))
                {
                    Schedule(K_SERVICE, k, NextDue(s, Now));
                    return;
                }
                to = net.access[s.call].door;
            }
            var gate = NearestGate(to);
            if (!PutOnRoad(c, net.borders[gate].entry, 0))
            {
                Schedule(K_SERVICE, k, Now + 5000);
                return;
            }
            c.state = CarState.Crossing;
            c.siren = s.kind != ServiceKind.Bins;
            if (c.siren && !sirens.Contains(s))
            {
                sirens.Add(s);
                Schedule(K_SIREN, s.id, Now + SIREN_TICK_MS);
            }
            if (!ServiceOnward(s)) ServiceHome(s);
        }

        int NearestGate(Vec2 p)
        {
            int best = 0;
            double bestD = double.PositiveInfinity;
            for (int g = 0; g < net.borders.Count; g++)
            {
                double d = Geo.Dist(p, net.borders[g].at);
                if (d < bestD)
                {
                    bestD = d;
                    best = g;
                }
            }
            return best;
        }

        /// <summary>Service `s` on to where it's going next: its next bin stop, its call, or out by the nearest gate. False: no way there.</summary>
        bool ServiceOnward(Service s)
        {
            var c = s.car;
            if (c.seg < 0 || c.seg >= Lanes) return false;
            var targets = new List<(int, double)>();
            double d = Math.Max(0, Front(c));
            if (s.kind == ServiceKind.Bins && s.next < s.stops.Count)
            {
                // The next stop it can get to from here (one it can't, behind it on a one-way bit, is left).
                for (; s.next < s.stops.Count; s.next++)
                {
                    var st = s.stops[s.next];
                    if (st.lane == c.seg && Math.Abs(st.d - d) <= 0.5)
                    {
                        // Already there (the next stretch on from the last): stops now.
                        c.dest = Place.Building(st.building);
                        ServiceReached(c);
                        return true;
                    }
                    var way = router.Route(c.seg, d, new List<(int, double)> { (st.lane, st.d) }, out _, out double secs);
                    if (way != null && way.Count > 0 && way[0] == c.seg)
                    {
                        c.dest = Place.Building(st.building);
                        return Drive(c, way, st.d, secs);
                    }
                    stats.binSkipped++;
                }
                return ServiceOut(c);
            }
            else if (s.kind != ServiceKind.Bins && s.call >= 0 && c.siren)
            {
                var to = net.access[s.call];
                c.dest = Place.Building(s.call);
                foreach (int l in net.LanesFor(to))
                {
                    if (net.lanes[l].exit || net.lanes[l].entry) continue;
                    double g = Math.Max(0, Math.Min(net.lanes[l].Span, net.DoneFor(net.lanes[l], to) - net.lanes[l].start));
                    if (l == c.seg && g <= d + 0.5) continue;
                    targets.Add((l, g));
                }
            }
            else return ServiceOut(c);
            if (targets.Count == 0) return false;
            var route = router.Route(c.seg, d, targets, out int t, out double seconds);
            if (route == null || route.Count == 0 || route[0] != c.seg) return false;
            return Drive(c, route, targets[t].Item2, seconds);
        }

        /// <summary>Out of town by the nearest gate it can get to (on an exit lane already: on out by it).</summary>
        bool ServiceOut(Car c)
        {
            double d = Math.Max(0, Front(c));
            var targets = new List<(int, double)>();
            var gates = new List<int>();
            for (int g = 0; g < net.borders.Count; g++)
            {
                targets.Add((net.borders[g].exit, net.lanes[net.borders[g].exit].Span));
                gates.Add(g);
            }
            var route = router.Route(c.seg, d, targets, out int t, out double seconds);
            if (route == null || route.Count == 0 || route[0] != c.seg) return false;
            c.dest = Place.Gate(gates[t]);
            return Drive(c, route, targets[t].Item2, seconds);
        }

        bool Drive(Car c, List<int> route, double goal, double seconds)
        {
            c.route = route;
            c.routeS = seconds;
            c.routed = Now;
            c.step = 0;
            c.goal = goal;
            c.target = -1;
            c.state = CarState.Crossing;
            SetInterval(c, Front(c), route.Count == 1 ? c.goal : SegLength(c.seg));
            return true;
        }

        /// <summary>Service car `c` at the end of its way: a bin stop or its call (it stands a while), or a gate (gone).</summary>
        void ServiceReached(Car c)
        {
            var s = services[c.service];
            if (c.dest.border)
            {
                ServiceHome(s);
                return;
            }
            c.state = CarState.AtStop;
            long stand;
            if (s.kind == ServiceKind.Bins)
            {
                int houses = s.next < s.stops.Count ? s.stops[s.next].houses : 1;
                stand = Math.Min(BIN_MOST_MS, BIN_STOP_MS + (houses - 1) * BIN_HOUSE_MS);
                stats.binStops++;
            }
            else
            {
                stand = s.kind == ServiceKind.Ambulance ? AMBULANCE_SCENE_MS : FIRE_SCENE_MS;
                StopSiren(s);
                stats.callsAnswered++;
            }
            Schedule(K_CAR, c.id, Now + stand);
        }

        /// <summary>Service car `c`'s done at its stop: on to the next, or home.</summary>
        void ServiceLeaves(Car c)
        {
            var s = services[c.service];
            if (s.kind == ServiceKind.Bins) s.next++;
            else s.call = -1;
            c.state = CarState.Crossing;
            if (!ServiceOnward(s)) ServiceHome(s);
        }

        /// <summary>Service `s` off the road (out of town, or nowhere to go): its next round or call scheduled.</summary>
        void ServiceHome(Service s)
        {
            var c = s.car;
            StopSiren(s);
            if (c.seg >= 0) RemoveFromRoad(c);
            c.offMap = true;
            c.route = null;
            c.state = CarState.Parked;
            s.done++;
            Schedule(K_SERVICE, s.id, NextDue(s, Now + TownClock.MINUTE));
        }

        /// <summary>Its siren off: the drivers pulled over for it go on.</summary>
        void StopSiren(Service s)
        {
            s.car.siren = false;
            sirens.Remove(s);
            Cancel(K_SIREN, s.id);
            foreach (int id in s.pulled) Release(cars[id]);
            s.pulled.Clear();
        }

        void Release(Car c)
        {
            if (!c.pulledOver) return;
            c.pulledOver = false;
            c.pullAt = Now - (PULL_MS - Math.Min(PULL_MS, Now - c.pullAt));
            version++;
            Refresh(c);
        }

        /// <summary>
        /// A siren's look down the lane ahead: drivers within SIREN_REACH slow and pull over (not one already at the
        /// line: it goes, clearing the way); once it's past one, it's ahead of it in the queue. Drivers it's left
        /// behind, or on another lane now, go on.
        /// </summary>
        void SirenTick(int k)
        {
            var s = services[k];
            var c = s.car;
            if (!c.siren || c.seg < 0) return;
            for (int i = s.pulled.Count - 1; i >= 0; i--)
            {
                var o = cars[s.pulled[i]];
                if (o.seg != c.seg || queues[c.seg].cars.IndexOf(o.id) > queues[c.seg].cars.IndexOf(c.id))
                {
                    Release(o);
                    s.pulled.RemoveAt(i);
                }
            }
            if (c.seg < Lanes)
            {
                var q = queues[c.seg];
                int me = q.cars.IndexOf(c.id);
                double front = Front(c);
                for (int i = me - 1; i >= 0; i--)
                {
                    var o = cars[q.cars[i]];
                    if (o.state == CarState.Waiting || o.state == CarState.AtStop || o.service >= 0) break;
                    if (Front(o) - front > SIREN_REACH) break;
                    if (!o.pulledOver)
                    {
                        o.pulledOver = true;
                        o.pullAt = Now - (PULL_MS - Math.Min(PULL_MS, Now - o.pullAt));
                        s.pulled.Add(o.id);
                        stats.pulledOver++;
                        version++;
                        Refresh(o);
                    }
                }
                // Past the one ahead (its back clear of that one's front): ahead of it in the queue.
                while (me > 0)
                {
                    var o = cars[q.cars[me - 1]];
                    if (!o.pulledOver || Front(c) - c.length - FOLLOWING < Front(o)) break;
                    q.cars[me - 1] = c.id;
                    q.cars[me] = o.id;
                    me--;
                    version++;
                    Refresh(o);
                }
            }
            Schedule(K_SIREN, k, Now + SIREN_TICK_MS);
        }

        /// <summary>Is car `c` in the way of a siren behind it on its lane (it goes, clearing the way)?</summary>
        bool ClearingFor(Car c)
        {
            if (sirens.Count == 0 || c.seg < 0 || c.seg >= Lanes) return false;
            var q = queues[c.seg];
            int me = q.cars.IndexOf(c.id);
            foreach (var s in sirens)
            {
                if (s.car.seg != c.seg) continue;
                int it = q.cars.IndexOf(s.car.id);
                if (it > me && Front(s.car) > Front(c) - SIREN_REACH) return true;
            }
            return false;
        }

        /// <summary>A siren coming to junction `j` (on a lane into it, within SIREN_SECONDS, or on a turn through it) that car `c` should wait for: its id, or -1.</summary>
        int SirenComing(Junction j, Car c)
        {
            foreach (var s in sirens)
            {
                var e = s.car;
                if (e.id == c.id || e.seg < 0) continue;
                if (e.seg >= Lanes)
                {
                    if (net.movements[e.seg - Lanes].node == j.node) return e.id;
                    continue;
                }
                var lane = net.lanes[e.seg];
                if (lane.outs.Count == 0 || net.movements[lane.outs[0]].node != j.node || e.seg == c.seg) continue;
                if ((lane.Span - Front(e)) / Math.Max(0.5, lane.speed) <= SIREN_SECONDS) return e.id;
            }
            return -1;
        }

        /// <summary>How far over towards the kerb car `c` is drawn (tiles): steered over and back smoothly, not stepped.</summary>
        double Aside(Car c)
        {
            double k = Math.Max(0, Math.Min(1, (Now - c.pullAt) / (double)PULL_MS));
            k = k * k * (3 - 2 * k);
            return PULL_ASIDE * (c.pulledOver ? k : 1 - k);
        }

        /// <summary>A service vehicle taken by the player: off the road, its slot kept for when they let it go.</summary>
        void TakeService(Car c)
        {
            var s = services[c.service];
            StopSiren(s);
            int slot = c.slot;
            RemoveFromRoad(c);
            c.slot = slot;
            c.offMap = true;
            carSlots[slot] = new Pose { who = c.id };
            Cancel(K_SERVICE, s.id);
        }

        void RestoreService(int slot)
        {
            int k = slot - serviceSlotsFrom;
            if (k >= 0 && k < services.Count) Schedule(K_SERVICE, k, NextDue(services[k], Now + TownClock.MINUTE));
        }
    }
}

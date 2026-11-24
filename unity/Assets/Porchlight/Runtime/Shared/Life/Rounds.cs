// Vans on their rounds: deliveries, tradespeople, the post, taxis. A town's streets
// aren't only its own people's comings and goings: day and night there are vans
// calling at its homes, shops and offices, one after another, which is most of what
// keeps a side street from being empty. How many are out follows real commercial
// traffic hour by hour (WORKING: Batterman, Cook & Justin 2015, Table 7, Detroit's
// commercial weekday profile): a quarter of them at 3 am, nearly all through the
// morning, fewer by evening; about half as many on a Saturday, a third on a Sunday. Each van has its place in line (Layout.Hash of its
// number): it's out while the share working is above it, so as the share rises
// vans come out in turn and as it falls the last in go off first, none flickering
// on and off. Vans sit out the rushes (RUSHES), which have the commuters. A van starts at a building, drives to the next call (the nearest of a
// few picked at random, so it works one part of town at a time) and on to the next.
// Vans don't park: they pull in, drop off and go (so they don't take the kerb spots
// residents need). There are as many as the town has blocks to keep busy
// (VANS_A_BLOCK), so a bigger town has more.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;

namespace Porchlight.Life
{
    public sealed partial class TownLife
    {
        /// <summary>Vans for each block of street (between two junctions): how busy the streets are between the rushes.</summary>
        public const double VANS_A_BLOCK = 0.6;
        /// <summary>
        /// Commercial traffic through a weekday, hour by hour (the share of the day's in each hour from midnight):
        /// Batterman, Cook &amp; Justin, "Temporal variation of traffic on highways and the development of accurate
        /// temporal allocation factors for air pollution analyses", Atmospheric Environment 107 (2015), Table 7,
        /// Detroit metro, commercial, weekday. Over its highest, it's the share of the vans out.
        /// </summary>
        static readonly double[] WORKING = { 0.0137, 0.0129, 0.0136, 0.0166, 0.0229, 0.0342, 0.0524, 0.0637, 0.0684, 0.0711, 0.0700, 0.0679, 0.0669, 0.0664, 0.0643, 0.0589, 0.0506, 0.0414, 0.0336, 0.0283, 0.0250, 0.0222, 0.0190, 0.0159 };
        const double WORKING_MOST = 0.0711;
        /// <summary>
        /// The rushes (from, to: hours into the day), which vans sit out: after the call they're at, off the road till
        /// it's over (each a few minutes after the next, not all at once). The rushes have the commuters, and a van
        /// then only holds them up: vans out through them doubled Ember Crossing's rush-hour drives.
        /// </summary>
        static readonly (double from, double to)[] RUSHES = { (7.75, 9.25), (16.25, 18.25) };
        /// <summary>
        /// Commercial traffic at the weekend and on holidays, of a weekday's: about half on a Saturday and a third on a
        /// Sunday (FHWA's Traffic Monitoring Guide day-of-week factors for trucks; docs/town-calendar.md).
        /// </summary>
        const double SATURDAY_VANS = 0.5, SUNDAY_VANS = 0.33, HOLIDAY_VANS = 0.4;
        /// <summary>How far on a van looks for when it's next out (it's out every day: a day is far enough).</summary>
        const long LOOK_STEP = 5 * TownClock.MINUTE;
        /// <summary>A van's next call is the nearest of this many picked at random.</summary>
        const int VAN_CHOICES = 3;
        /// <summary>A call this close (tiles) to where it is isn't worth driving to: another.</summary>
        const double VAN_HOP = 12;

        /// <summary>The vans (cars with no owner), in order.</summary>
        public readonly List<Car> vans = new List<Car>();
        /// <summary>The buildings vans call at: any (parcels go to homes as much as shops), so they're on every street.</summary>
        readonly List<int> calls = new List<int>();
        /// <summary>How many calls each van's had picked for it (its picks come from its id and this).</summary>
        int[] vanPicks = new int[0];

        void MakeVans()
        {
            foreach (var b in zoning?.buildings ?? new List<ZonedBuilding>())
                if (net.access.ContainsKey(b.buildingId)) calls.Add(b.buildingId);
            if (calls.Count < 2) return;
            int n = (int)Math.Round(VANS_A_BLOCK * Blocks());
            for (int k = 0; k < n; k++)
            {
                var c = new Car { id = cars.Count, van = k, length = 1.4 + 0.5 * Layout.Hash(k + 211, 17), state = CarState.Parked, offMap = true };
                cars.Add(c);
                vans.Add(c);
            }
            vanPicks = new int[n];
        }

        /// <summary>The share of the vans out at `t` (0-1): the commercial profile, between its hours smoothly.</summary>
        public static double Working(long t)
        {
            double h = TownClock.Hours(t);
            int i = (int)Math.Floor(h) % 24;
            double f = h - Math.Floor(h);
            return (WORKING[i] * (1 - f) + WORKING[(i + 1) % 24] * f) / WORKING_MOST;
        }

        /// <summary>Van `k`'s place in line: it's out while the share working is above this.</summary>
        static double VanPlace(int k) => Layout.Hash(k * 31 + 7, 1931);

        /// <summary>Whether van `k` is out at `t`: its turn in the hour's share, and not a rush.</summary>
        static bool OnDuty(int k, long t)
        {
            long day = TownClock.DayOf(t);
            int weekday = TownCalendar.Weekday(day);
            bool holiday = TownCalendar.Holiday(day) != null;
            double share = weekday == 6 ? SATURDAY_VANS : weekday == 0 ? SUNDAY_VANS : holiday ? HOLIDAY_VANS : 1;
            if (VanPlace(k) >= Working(t) * share) return false;
            // No rushes to sit out at the weekend or on a holiday.
            if (share < 1) return true;
            double h = TownClock.Hours(t), late = Layout.Hash(k * 31 + 13, 1931) / 6;
            foreach (var (from, to) in RUSHES)
                if (h >= from && h < to + late) return false;
            return true;
        }

        /// <summary>When van `k` next goes out, at or after `t`: now, if it's on duty.</summary>
        long NextShift(int k, long t)
        {
            if (OnDuty(k, t)) return t;
            for (long u = t + LOOK_STEP; u <= t + TownClock.DAY; u += LOOK_STEP)
                if (OnDuty(k, u)) return u;
            return t + TownClock.DAY;
        }

        /// <summary>When van `k`, out at `t`, next goes off (the share working falls below its place).</summary>
        long OffDuty(int k, long t)
        {
            for (long u = t; u <= t + TownClock.DAY; u += LOOK_STEP)
                if (!OnDuty(k, u)) return u;
            return t + TownClock.DAY;
        }

        void StartVans(long from)
        {
            // Those already out at the start come on a few seconds apart, not all at once.
            for (int k = 0; k < vans.Count; k++)
            {
                long at = NextShift(k, from);
                Schedule(K_VAN, k, at == from ? from + k * 997 % 60_000 : at);
            }
        }

        /// <summary>Van `k`'s shift starts: out from one of its calls.</summary>
        void OnVanDue(int k)
        {
            var c = vans[k];
            if (c.seg >= 0) return;
            if (NextShift(k, Now) > Now)
            {
                Schedule(K_VAN, k, NextShift(k, Now));
                return;
            }
            int b = calls[Pick(k, calls.Count)];
            var a = net.access[b];
            int lane = net.SideLane(a.road, a.s, a.door);
            if (lane < 0 || !PutOnRoad(c, lane, Math.Max(0, Math.Min(net.lanes[lane].Span, net.DoneFor(net.lanes[lane], a) - net.lanes[lane].start))))
            {
                // No gap there: somewhere else shortly.
                Schedule(K_VAN, k, Now + 5000);
                return;
            }
            c.dest = Place.Building(b);
            if (!VanOnward(c)) RemoveVan(c);
        }

        /// <summary>At a call (or just out): off to the next, or, its shift over, off the road. False: nowhere to go.</summary>
        bool VanOnward(Car c)
        {
            int k = c.van;
            if (!OnDuty(k, Now))
            {
                RemoveVan(c);
                return true;
            }
            if (c.seg < 0 || c.seg >= Lanes) return false;
            int lane = c.seg;
            double d = Math.Max(0, Front(c));
            var here = CarPoint(c).at;
            for (int tries = 0; tries < 4; tries++)
            {
                // The nearest of a few, not too near.
                int best = -1;
                double bestD = double.PositiveInfinity;
                for (int i = 0; i < VAN_CHOICES; i++)
                {
                    int b = calls[Pick(k, calls.Count)];
                    double dist = Geo.Dist(here, net.access[b].door);
                    if (b == c.dest.id || dist < VAN_HOP) continue;
                    if (dist < bestD)
                    {
                        bestD = dist;
                        best = b;
                    }
                }
                if (best < 0) continue;
                var to = net.access[best];
                var targets = new List<(int, double)>();
                foreach (int l in net.LanesFor(to))
                {
                    double g = Math.Max(0, Math.Min(net.lanes[l].Span, net.DoneFor(net.lanes[l], to) - net.lanes[l].start));
                    if (l == lane && g <= d + 0.5) continue;
                    targets.Add((l, g));
                }
                if (targets.Count == 0) continue;
                var route = router.Route(lane, d, targets, out int t, out double seconds);
                if (route == null || route.Count == 0 || route[0] != lane) continue;
                c.route = route;
                c.routeS = seconds;
                c.routed = Now;
                c.step = 0;
                c.goal = targets[t].Item2;
                c.dest = Place.Building(best);
                c.callFrom = Now;
                c.target = -1;
                c.state = CarState.Crossing;
                SetInterval(c, Front(c), route.Count == 1 ? c.goal : SegLength(c.seg));
                return true;
            }
            return false;
        }

        /// <summary>Off the road until its next shift (or a minute, if it had nowhere to go).</summary>
        void RemoveVan(Car c)
        {
            if (c.seg >= 0) RemoveFromRoad(c);
            c.offMap = true;
            c.route = null;
            long next = NextShift(c.van, Now);
            Schedule(K_VAN, c.van, next > Now ? next : Now + TownClock.MINUTE);
        }

        /// <summary>Van `k`'s next pick of `n` (deterministic: from its id and how many it's had).</summary>
        int Pick(int k, int n) => Math.Min(n - 1, (int)(n * Layout.Hash(k * 7919 + 3, vanPicks[k]++)));

        /// <summary>How many blocks (a street between two junctions, either way) the town has.</summary>
        int Blocks()
        {
            var all = new HashSet<long>();
            foreach (var l in net.lanes) all.Add(BlockKey(l));
            return all.Count;
        }
    }
}

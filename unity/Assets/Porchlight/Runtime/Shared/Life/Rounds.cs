// Vans on their rounds: deliveries, tradespeople, the post. A town's streets
// aren't only its own people's comings and goings: all day there are vans
// calling at its homes, shops and offices, one after another, which is most of what
// keeps a side street from being empty between the rushes. Each van works a
// shift (on 06:30-07:30, off 20:00-22:00) and sits out the rushes (RUSHES):
// the rushes have the commuters, and a van then only holds them up. It starts at a
// building, drives to the next call (the nearest of a few picked at random,
// so it works one part of town at a time) and on to the next, and goes off at
// the end of its shift. Vans don't park: they pull in, drop off and go (so
// they don't take the kerb spots residents need). There are as many as the
// town has blocks to keep busy (VANS_A_BLOCK), so a bigger town has more.

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
        /// <summary>The rushes (from, to: hours into the day), which vans sit out: after the call they're at, off the road till it's over.</summary>
        static readonly (double from, double to)[] RUSHES = { (7.75, 9.25), (16.25, 18.25) };
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

        /// <summary>Van `k`'s shift on `day`: when it starts and ends.</summary>
        (long on, long off) Shift(int k, long day)
        {
            long start = DayStart(day);
            long H = TownClock.HOUR;
            double a = Layout.Hash(k * 31 + 7, (int)day), b = Layout.Hash(k * 31 + 11, (int)day);
            return (start + (long)((6.5 + a) * H), start + (long)((20 + 2 * b) * H));
        }

        /// <summary>When van `k` next goes out, at or after `t`: now, if it's in its shift and not a rush.</summary>
        long NextShift(int k, long t)
        {
            long day = TownClock.DayOf(t);
            var (on, off) = Shift(k, day);
            if (t < on) return on;
            if (t >= off) return Shift(k, day + 1).on;
            // A rush: out again once it's over (a few minutes apart, not all at once).
            long tod = TownClock.TimeOfDay(t);
            foreach (var (from, to) in RUSHES)
                if (tod >= from * TownClock.HOUR && tod < to * TownClock.HOUR)
                    return Math.Min(off, t - tod + (long)(to * TownClock.HOUR) + (long)(10 * TownClock.MINUTE * Layout.Hash(k * 31 + 13, (int)day)));
            return t;
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
            if (Now >= Shift(k, TownClock.DayOf(Now)).off || NextShift(k, Now) > Now)
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

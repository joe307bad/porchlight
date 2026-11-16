// Walkers and cars at the crossings, after SUMO's crossing rules, worked out
// in game seconds (walkers here go three to five times real walking pace, so
// real walking timings don't carry over; Units).
//
// Each crossing of a road (a junction mouth, from the sidewalk network, or a
// stretch of road someone's crossing mid-block) is something a walker
// reserves. At the kerb a walker looks (0.4 to 1.2 s), then waits until every
// car coming will take longer to reach the crossing than they need to get
// across, and a margin (1.5 s, falling to 0.5 s over 15 s of waiting); at a
// signal, only while the cars on that road have red. Then they commit: the
// crossing is closed to cars (Traffic.closedCrossings) until they're over.
//
// Drivers decide once per walker waiting at a crossing whether to stop for
// them (85% where the crossing is marked, at minor and controlled junction
// legs; 40% on a main road's priority leg or mid-block), unless they're too
// close to stop; a car already within its braking distance counts as not
// stopping. So nobody flip-flops. Three guards against stalemates: walkers
// never make a car turn round; a car held 6 s by walkers gets a turn (the
// crossing takes no new walkers until it's by); and a crossing that has had
// walkers on it 8 s straight takes no more until it's clear.
// Pure, run by the host.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class Crosswalk
    {
        public int id;
        public Vec2 a, b;
        /// <summary>The road it crosses (index into IsoLayout.roads), and the junction it's at (-1: mid-block).</summary>
        public int road, node = -1;
        /// <summary>The junction leg it crosses, or -1.</summary>
        public int leg = -1;
        /// <summary>Marked (drivers mostly stop): at a minor or controlled leg, not across a priority road or mid-block.</summary>
        public bool marked;
        /// <summary>Made for one walker crossing mid-block: gone when they're over.</summary>
        public bool transient;
        // State.
        public readonly List<Pedestrian> committed = new List<Pedestrian>();
        public readonly List<Pedestrian> waiting = new List<Pedestrian>();
        /// <summary>Seconds it's had walkers on it without a break.</summary>
        public double busy, shutFor;
        /// <summary>Taking no new walkers (a car's turn, or it's been busy too long).</summary>
        public bool shut;
    }

    public sealed class Crosswalks
    {
        /// <summary>Look before crossing: this long, give or take (s, drawn per walker per crossing).</summary>
        const double LOOK_MIN = 0.4, LOOK_MAX = 1.2;
        /// <summary>The margin over the time to cross a walker wants (s): from START down to END over SHRINK seconds of waiting.</summary>
        const double MARGIN_START = 1.5, MARGIN_END = 0.5, MARGIN_SHRINK = 15;
        /// <summary>Chance a driver stops for a walker waiting at a marked crossing, and at an unmarked one (tuning guesses, after FHWA's direction).</summary>
        const double YIELD_MARKED = 0.85, YIELD_UNMARKED = 0.4;
        /// <summary>A car held this long by walkers gets its turn; a crossing busy this long takes no more till it clears (s).</summary>
        const double CAR_TURN = 6, BUSY = 8;
        /// <summary>The longest a crossing stays shut to new walkers once clear (s).</summary>
        const double SHUT = 4;
        /// <summary>A walker waiting this long at a kerb gives up and goes another way (s).</summary>
        const double GIVE_UP = 20;
        /// <summary>How far off a car counts as coming (tiles).</summary>
        const double LOOK_OUT = 12;
        /// <summary>Mid-block crossings only this far (tiles) from any junction.</summary>
        const double MIDBLOCK_CLEAR = 3;
        /// <summary>A car SUMO's jmCrossingGap (10 m) from the crossing, coming on, blocks a walker starting.</summary>
        static readonly double CROSSING_GAP = Units.Tiles(10);

        readonly IsoLayout layout;
        readonly Traffic traffic;
        readonly Rng rng;
        public readonly List<Crosswalk> all = new List<Crosswalk>();
        readonly int fixedCount;
        double now;

        sealed class WalkerState
        {
            public Crosswalk at;
            public double since, look;
            public string why;
        }

        readonly Dictionary<Pedestrian, WalkerState> states = new Dictionary<Pedestrian, WalkerState>();

        /// <summary>The ends of the road crossings at the junctions (from the sidewalk network).</summary>
        public Crosswalks(IsoLayout layout, Traffic traffic, Rng rng)
        {
            this.layout = layout;
            this.traffic = traffic;
            this.rng = rng;
            var net = layout.sidewalks;
            if (net == null) return;
            var ends = new List<Vec2>();
            foreach (var p in net.paths)
            {
                ends.Add(p.points[0]);
                ends.Add(p.points[p.points.Count - 1]);
            }
            for (int from = 0; from < net.links.Count; from++)
                foreach (int to in net.links[from])
                {
                    if (to < from || !net.crossings.Contains(SidewalkNetwork.LinkKey(from, to))) continue;
                    var c = Make(ends[from], ends[to], false);
                    if (c != null) all.Add(c);
                }
            fixedCount = all.Count;
        }

        /// <summary>A crossing from a to b, if it crosses a road cars drive: which road, where, marked or not.</summary>
        Crosswalk Make(Vec2 a, Vec2 b, bool transient)
        {
            int road = -1;
            double bestT = double.PositiveInfinity;
            Vec2 hitAt = default;
            for (int r = 0; r < layout.roads.Count; r++)
            {
                if (layout.roads[r].kind == RoadKind.Path || !traffic.Driven(r)) continue;
                var pts = layout.roads[r].points;
                for (int k = 0; k + 1 < pts.Count; k++)
                {
                    var hit = Geo.SegmentIntersection(a, b, pts[k], pts[k + 1]);
                    if (hit != null && Math.Abs(hit.Value.t - 0.5) < bestT)
                    {
                        bestT = Math.Abs(hit.Value.t - 0.5);
                        road = r;
                        hitAt = hit.Value.point;
                    }
                    else if (hit == null && bestT > 1 && Geo.SegmentDistance(a, b, pts[k], pts[k + 1]) < layout.roads[r].width / 2)
                    {
                        // Across the carriageway though short of the middle (the end of a road, say).
                        bestT = 1;
                        road = r;
                        hitAt = Geo.Lerp(a, b, 0.5);
                    }
                }
            }
            if (road < 0) return null;
            var c = new Crosswalk { id = all.Count, a = a, b = b, road = road, transient = transient };
            var rr = layout.roads[road];
            // At a junction if it's near one of the road's ends.
            foreach (int n in new[] { rr.from, rr.to })
            {
                if (n >= traffic.junctions.Length || traffic.junctions[n] == null) continue;
                var j = traffic.junctions[n];
                if (Geo.Dist(j.at, hitAt) > 3) continue;
                c.node = n;
                c.leg = j.LegOf(road, rr.from == n);
                var leg = c.leg >= 0 ? j.legs[c.leg] : null;
                c.marked = j.control != JunctionControl.Priority || (leg != null && leg.minor);
            }
            return c;
        }

        Crosswalk Find(Vec2 a, Vec2 b)
        {
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if ((Geo.Dist(c.a, a) < 0.05 && Geo.Dist(c.b, b) < 0.05) || (Geo.Dist(c.a, b) < 0.05 && Geo.Dist(c.b, a) < 0.05)) return c;
            }
            return null;
        }

        /// <summary>
        /// At the kerb, about to step out from `from` to `to`: go, wait, or (no crossing here,
        /// or too near a junction to cross mid-block) never.
        /// </summary>
        public CrossingAnswer Ask(Pedestrian p, Vec2 from, Vec2 to)
        {
            states.TryGetValue(p, out var st);
            var c = st != null && st.at != null && SameLine(st.at, from, to) ? st.at : Find(from, to);
            if (c == null)
            {
                // Not one of the junctions' crossings: mid-block, if it crosses a road at all.
                c = Make(from, to, true);
                if (c == null) return CrossingAnswer.Go;
                if (NearJunction(Geo.Lerp(from, to, 0.5))) return CrossingAnswer.Never;
                c.id = all.Count;
                c.node = -1;
                c.marked = false;
                all.Add(c);
            }
            if (c.node >= 0 && traffic.junctions[c.node] != null && traffic.junctions[c.node].noCrossing) return CrossingAnswer.Never;
            if (st == null || st.at != c)
            {
                Forget(p);
                st = new WalkerState { at = c, since = now, look = rng.Range(LOOK_MIN, LOOK_MAX) };
                states[p] = st;
                c.waiting.Add(p);
            }
            double waited = now - st.since;
            if (waited < st.look) return CrossingAnswer.Wait;
            if (waited > GIVE_UP)
            {
                // Had enough: some other way.
                Forget(p);
                return CrossingAnswer.Never;
            }
            if (!Clear(c, p, waited, out st.why)) return CrossingAnswer.Wait;
            c.waiting.Remove(p);
            c.committed.Add(p);
            st.since = now;
            return CrossingAnswer.Go;
        }

        static bool SameEnds((Vec2, Vec2) x, Vec2 a, Vec2 b) =>
            (Geo.Dist(x.Item1, a) < 0.05 && Geo.Dist(x.Item2, b) < 0.05) || (Geo.Dist(x.Item1, b) < 0.05 && Geo.Dist(x.Item2, a) < 0.05);

        static bool SameLine(Crosswalk c, Vec2 a, Vec2 b) =>
            (Geo.Dist(c.a, a) < 0.05 && Geo.Dist(c.b, b) < 0.05) || (Geo.Dist(c.a, b) < 0.05 && Geo.Dist(c.b, a) < 0.05) || (c.transient && Geo.Dist(c.a, a) < 0.05);

        bool NearJunction(Vec2 at)
        {
            foreach (var j in traffic.junctions)
                if (j != null && Geo.Dist(j.at, at) < MIDBLOCK_CLEAR) return true;
            return false;
        }

        /// <summary>Safe for walker `p` to start across `c` now, having waited `waited` seconds.</summary>
        bool Clear(Crosswalk c, Pedestrian p, double waited, out string why)
        {
            why = null;
            if (c.shut && c.committed.Count == 0 && !AnyCarWaiting(c)) c.shut = false;
            if (c.shut) { why = "shut"; return false; }
            // At a signal: only while the cars on this road have red.
            if (c.node >= 0 && c.leg >= 0)
            {
                var j = traffic.junctions[c.node];
                if (j != null && j.IsSignal && !j.Walk(c.leg, traffic.Now, traffic.night)) { why = "signal"; return false; }
            }
            double across = Geo.Dist(c.a, c.b) / Math.Max(0.3, p.Pace) + 0.3;
            double margin = MARGIN_START + (MARGIN_END - MARGIN_START) * Math.Min(1, waited / MARGIN_SHRINK);
            foreach (var car in traffic.cars)
            {
                if (car.taken || car.idle || car.loop >= 0) continue;
                if (Math.Abs(car.x - c.a.x) > LOOK_OUT + 2 || Math.Abs(car.y - c.a.y) > LOOK_OUT + 2) continue;
                // On the crossing already (its nose or tail over it): wait for it to go.
                if (Geo.DistToSegment(new Vec2(car.x, car.y), c.a, c.b) < Traffic.CAR_LENGTH * 0.5 + 0.35) { why = "a car on it"; return false; }
                double d = traffic.PathTo(car, c.a, c.b, LOOK_OUT);
                if (double.IsPositiveInfinity(d)) continue;
                // Stopped for this crossing (or held at a junction short of it): not coming.
                if (car.speed < 0.15 && car.granted < 0 && d > Traffic.CAR_LENGTH + 0.5) continue;
                bool stopping = car.yields && car.yieldFor == c.id && d > Traffic.StoppingDistance(car.speed) + 0.3;
                if (stopping) continue;
                double tta = d / Math.Max(car.speed, 0.3);
                if (tta < across + margin || d < CROSSING_GAP * 0.5) { why = $"car {traffic.cars.IndexOf(car)} d {d:0.0} v {car.speed:0.00} yields {car.yields} for {car.yieldFor}/{c.id} why '{car.why}' at ({car.x:0.0},{car.y:0.0})"; return false; }
            }
            return true;
        }

        bool AnyCarWaiting(Crosswalk c)
        {
            foreach (var car in traffic.cars)
                if (!car.taken && !car.idle && car.walkerWait > 0.5 && traffic.PathTo(car, c.a, c.b, 3) < 3) return true;
            return false;
        }

        void Forget(Pedestrian p)
        {
            if (!states.TryGetValue(p, out var st)) return;
            st.at?.waiting.Remove(p);
            st.at?.committed.Remove(p);
            states.Remove(p);
        }

        /// <summary>
        /// Before the walkers move: the clock, who's over, which crossings take no more walkers,
        /// and each driver's decision about stopping for those waiting.
        /// </summary>
        public void Begin(double dt, IReadOnlyList<Pedestrian> people)
        {
            now += dt;
            // Over (or off somewhere else): no longer on the crossing.
            foreach (var c in all)
            {
                c.committed.RemoveAll(p => !p.OnCrossing || !OnThis(p, c));
                c.waiting.RemoveAll(p => p.OnCrossing || p.CrossingLine == null || !OnThis(p, c));
            }
            var gone = new List<Pedestrian>();
            foreach (var kv in states) if (kv.Key.CrossingLine == null) gone.Add(kv.Key);
            gone.Sort((x, y) => IndexIn(people, x).CompareTo(IndexIn(people, y)));
            foreach (var p in gone) Forget(p);
            for (int i = all.Count - 1; i >= fixedCount; i--)
            {
                var c = all[i];
                if (c.committed.Count == 0 && c.waiting.Count == 0)
                {
                    all.RemoveAt(i);
                    ClearYields(c);
                }
            }
            for (int i = 0; i < all.Count; i++) all[i].id = i;
            foreach (var c in all)
            {
                c.busy = c.committed.Count > 0 ? c.busy + dt : 0;
                if (c.busy > BUSY) c.shut = true;
                if (c.committed.Count == 0 && c.waiting.Count == 0) ClearYields(c);
            }
            // A car held by walkers long enough: its turn (and it stops stopping for them).
            foreach (var car in traffic.cars)
            {
                if (car.taken || car.idle || car.walkerWait < CAR_TURN) continue;
                foreach (var c in all)
                    if (traffic.PathTo(car, c.a, c.b, 3) < 3)
                    {
                        c.shut = true;
                        ClearYields(c);
                    }
            }
            // Shut no longer than a car's turn takes, once it's clear of walkers.
            foreach (var c in all)
            {
                c.shutFor = c.shut ? c.shutFor + dt : 0;
                if (c.shut && c.committed.Count == 0 && c.shutFor > SHUT) c.shut = false;
            }
            // Drivers decide about the walkers waiting, once each.
            foreach (var c in all)
            {
                if (c.waiting.Count == 0 || c.shut) continue;
                foreach (var car in traffic.cars)
                {
                    if (car.taken || car.idle || car.loop >= 0 || car.yieldFor == c.id) continue;
                    if (Math.Abs(car.x - c.a.x) > LOOK_OUT || Math.Abs(car.y - c.a.y) > LOOK_OUT) continue;
                    double d = traffic.PathTo(car, c.a, c.b, LOOK_OUT);
                    if (double.IsPositiveInfinity(d)) continue;
                    car.yieldFor = c.id;
                    car.yieldA = c.a;
                    car.yieldB = c.b;
                    // Too close to stop comfortably: it doesn't.
                    car.yields = d > Traffic.StoppingDistance(car.speed) + 0.3 && rng.Chance(c.marked ? YIELD_MARKED : YIELD_UNMARKED);
                }
            }
        }

        static int IndexIn(IReadOnlyList<Pedestrian> people, Pedestrian p)
        {
            for (int i = 0; i < people.Count; i++) if (people[i] == p) return i;
            return int.MaxValue;
        }

        static bool OnThis(Pedestrian p, Crosswalk c)
        {
            var line = p.CrossingLine;
            return line != null && SameLine(c, line.Value.from, line.Value.to);
        }

        void ClearYields(Crosswalk c)
        {
            foreach (var car in traffic.cars)
                if (car.yieldFor == c.id)
                {
                    car.yieldFor = -1;
                    car.yields = false;
                }
        }

        /// <summary>After the walkers move: the crossings closed to cars, and where the walkers out on the road are.</summary>
        public void End(IReadOnlyList<Pedestrian> people, List<(Vec2, Vec2)> closed, List<Vec2> onRoad)
        {
            closed.Clear();
            onRoad.Clear();
            foreach (var c in all)
                if (c.committed.Count > 0) closed.Add((c.a, c.b));
            foreach (var p in people)
            {
                if (!p.OnCrossing) continue;
                onRoad.Add(new Vec2(p.x, p.y));
                // Out on a crossing it didn't ask for (back to the kerb it left, say): closed all the same.
                var line = p.CrossingLine.Value;
                if (!closed.Exists(x => SameEnds(x, line.from, line.to))) closed.Add((line.from, line.to));
            }
        }

        /// <summary>How many walkers are waiting at the kerb now, and the longest any has waited (s).</summary>
        public (int count, double longest) Waiting() => Waiting(out _);

        /// <summary>The same, and what's holding up the walker who's waited longest.</summary>
        public (int count, double longest) Waiting(out string why)
        {
            int n = 0;
            double longest = 0;
            why = null;
            foreach (var kv in states)
            {
                if (kv.Value.at == null || !kv.Value.at.waiting.Contains(kv.Key)) continue;
                n++;
                if (now - kv.Value.since >= longest)
                {
                    longest = now - kv.Value.since;
                    var c = kv.Value.at;
                    why = $"{kv.Value.why} (crossing {c.id}: road {c.road}, node {c.node}, leg {c.leg}, marked {c.marked}, transient {c.transient}, {c.committed.Count} on it, shut {c.shut}, busy {c.busy:0.0})";
                }
            }
            return (n, longest);
        }
    }

    /// <summary>
    /// The town's street life a step at a time, in a fixed order: the walkers at the kerb
    /// decide and the walkers move (Crosswalks), the crossings close to cars, then the cars.
    /// Signals need no step of their own: their state is a function of the clock.
    /// </summary>
    public sealed class Streetlife
    {
        public readonly Traffic traffic;
        public readonly Crosswalks crosswalks;
        readonly List<Vec2> onRoad = new List<Vec2>();
        readonly Func<Pedestrian, Vec2, Vec2, CrossingAnswer> ask;

        public Streetlife(IsoLayout layout, Traffic traffic, Rng rng)
        {
            this.traffic = traffic;
            crosswalks = new Crosswalks(layout, traffic, rng);
            ask = crosswalks.Ask;
        }

        /// <summary>
        /// One step: `people` stroll (those not following someone), then the traffic drives, stopping
        /// for `obstacles` (players, parked cars) and the walkers out on the road.
        /// </summary>
        public void Step(double dt, IReadOnlyList<Pedestrian> people, IReadOnlyList<Vec2> obstacles)
        {
            crosswalks.Begin(dt, people);
            foreach (var p in people)
            {
                if (p.Guard != ask) p.Guard = ask;
                if (!p.following) p.Update(dt);
            }
            crosswalks.End(people, traffic.closedCrossings, onRoad);
            traffic.Update(dt, obstacles, onRoad);
        }
    }
}

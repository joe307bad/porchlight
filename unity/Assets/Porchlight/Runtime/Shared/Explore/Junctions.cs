// Junctions as the traffic sees them: for every node where three roads or
// more meet, its legs (one per road end, sorted round it), its movements (one
// per way in and way out), which movements cross or merge (foes) and, of each
// pair of foes, which gives way: after SUMO's junction model.
//
//   - Legs are sorted by angle; each has a lane in and a lane out, either side
//     of it (traffic keeps to the right), as "ports" round the junction.
//   - Two movements are foes if they leave by the same road, or their chords
//     from port to port cross. Two from the same leg are never foes: they queue.
//   - Of two foes, the one from the lower-ranked road gives way; then a turn
//     gives way to straight on, a far turn (across the oncoming lane) to a near
//     one; then the car with the other on its right (the near side) gives way;
//     and if nothing else decides, the higher-numbered movement. Exactly one of
//     every pair of foes gives way (Check).
//   - A signal runs a fixed-time plan worked out from the legs (SUMO's netconvert
//     recipe): the highest-ranked leg and the one most opposite it go together,
//     their movements green, a far turn that crosses the oncoming lane only a
//     "permitted" green (go, but give way); then the next pair, and so on.
//     Short cycles, 3 s yellows and 1 s all-red; its phase is a pure function of
//     the clock, so every client can light its lamps with nothing sent. Signals
//     along an arterial are offset into a green wave; at night (if the town
//     wants) they flash, yellow on the major road and red on the minor.
// Pure, with no engine.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public enum Turn { Straight, Near, Far }

    /// <summary>A signal's state for a movement (or its lamp, for a leg).</summary>
    public enum Lamp { Off, Green, Permitted, Yellow, Red, FlashYellow, FlashRed }

    public sealed class JunctionLeg
    {
        /// <summary>The road (index into IsoLayout.roads).</summary>
        public int road;
        /// <summary>This end of it is the road's start (its `from` node); else its end.</summary>
        public bool atStart;
        /// <summary>The way it leaves the junction (unit), and that as an angle.</summary>
        public Vec2 dir;
        public double angle;
        /// <summary>Right of way: 1 has it, 0 gives way (Priority and Stop); its road's rank, otherwise.</summary>
        public int rank;
        public bool minor;
        /// <summary>The road's own rank (RoadSense).</summary>
        public int roadRank;
        /// <summary>A car leaving by this leg drives the road this way (1: from→to).</summary>
        public int OutDir => atStart ? 1 : -1;
        /// <summary>A car coming in by this leg drives the road this way.</summary>
        public int InDir => atStart ? -1 : 1;
    }

    public sealed class Movement
    {
        public int from, to;
        public Turn turn;
    }

    /// <summary>A signal's stage: the movements it gives a green (some only a permitted one), and how long.</summary>
    public sealed class SignalStage
    {
        public ulong green, permitted;
        public int greenMs, yellowMs, redMs;
        public int Ms => greenMs + yellowMs + redMs;
    }

    public sealed class Junction
    {
        public const int MAX_LEGS = 8;
        public const int YELLOW_MS = 3000, ALL_RED_MS = 1000, MIN_GREEN_MS = 5000;
        public const int MAJOR_GREEN_MS = 13000, MINOR_GREEN_MS = 9000;
        /// <summary>How fast a flashing lamp blinks (ms on, ms off).</summary>
        public const int FLASH_MS = 500;

        public int node;
        public Vec2 at;
        public JunctionControl control;
        public List<JunctionLeg> legs = new List<JunctionLeg>();
        public List<Movement> moves = new List<Movement>();
        /// <summary>moves[m] crosses or merges with moves[n] where bit n of foes[m] is set.</summary>
        public ulong[] foes;
        /// <summary>moves[m] gives way to moves[n] where bit n of yields[m] is set (off a signal; on one, permitted greens give way to their foes).</summary>
        public ulong[] yields;
        int[] moveIndex;
        // The signal's plan.
        public List<SignalStage> stages = new List<SignalStage>();
        public int cycleMs;
        public int offsetMs;
        public bool nightFlash = true;
        /// <summary>Seconds walkers get to start across before the turning cars' green.</summary>
        public double leading = 1.5;
        public bool noCrossing;

        public bool IsSignal => control == JunctionControl.Signal && stages.Count > 0;

        /// <summary>The leg that is this end of this road, or -1.</summary>
        public int LegOf(int road, bool atStart)
        {
            for (int i = 0; i < legs.Count; i++) if (legs[i].road == road && legs[i].atStart == atStart) return i;
            return -1;
        }

        /// <summary>The leg a car on `road` driving way `dir` comes in by.</summary>
        public int LegIn(int road, int dir) => LegOf(road, dir == -1);

        /// <summary>The leg a car leaves by onto `road` driving way `dir`.</summary>
        public int LegOut(int road, int dir) => LegOf(road, dir == 1);

        public int Move(int from, int to) => from < 0 || to < 0 ? -1 : moveIndex[from * legs.Count + to];

        public bool Foes(int m, int n) => m >= 0 && n >= 0 && (foes[m] & (1UL << n)) != 0;

        public bool Yields(int m, int n) => m >= 0 && n >= 0 && (yields[m] & (1UL << n)) != 0;

        // ------------------------------------------------------------- signals

        /// <summary>Where in its cycle the signal is at `ms`: the stage, and ms into it.</summary>
        public (int stage, int into) Phase(long ms)
        {
            if (stages.Count == 0 || cycleMs <= 0) return (-1, 0);
            long t = ((ms + offsetMs) % cycleMs + cycleMs) % cycleMs;
            for (int k = 0; k < stages.Count; k++)
            {
                if (t < stages[k].Ms) return (k, (int)t);
                t -= stages[k].Ms;
            }
            return (stages.Count - 1, stages[stages.Count - 1].Ms - 1);
        }

        /// <summary>A movement's light at `ms` (Off where there's no signal); `night`: flashing, if the town lets its signals flash.</summary>
        public Lamp Light(int m, long ms, bool night)
        {
            if (!IsSignal || m < 0) return Lamp.Off;
            if (night && nightFlash) return legs[moves[m].from].minor ? Lamp.FlashRed : Lamp.FlashYellow;
            var (k, into) = Phase(ms);
            var s = stages[k];
            ulong bit = 1UL << m;
            if ((s.green & bit) == 0) return Lamp.Red;
            if (into >= s.greenMs + s.yellowMs) return Lamp.Red;
            if (into >= s.greenMs) return Lamp.Yellow;
            // Walkers get a head start across the road a turning car would turn into.
            if (moves[m].turn != Turn.Straight && into < leading * 1000 && WalkStarts(moves[m].to, k)) return Lamp.Red;
            return (s.permitted & bit) != 0 ? Lamp.Permitted : Lamp.Green;
        }

        /// <summary>A leg's lamp: the most restrictive of its movements' lights.</summary>
        public Lamp HeadLight(int leg, long ms, bool night)
        {
            if (!IsSignal) return Lamp.Off;
            if (night && nightFlash) return legs[leg].minor ? Lamp.FlashRed : Lamp.FlashYellow;
            var worst = Lamp.Off;
            for (int m = 0; m < moves.Count; m++)
            {
                if (moves[m].from != leg) continue;
                var l = Light(m, ms, night);
                if (worst == Lamp.Off || Severity(l) > Severity(worst)) worst = l;
            }
            return worst;
        }

        /// <summary>Whether a flashing lamp is lit at `ms`.</summary>
        public static bool FlashOn(long ms) => ((ms / FLASH_MS) & 1) == 0;

        static int Severity(Lamp l) => l switch
        {
            Lamp.Green => 0,
            Lamp.Permitted => 1,
            Lamp.FlashYellow => 2,
            Lamp.Yellow => 3,
            Lamp.FlashRed => 4,
            Lamp.Red => 5,
            _ => -1,
        };

        /// <summary>Walkers may start across leg `leg` at `ms`: no car comes in by it (its movements red), at a signal.</summary>
        public bool Walk(int leg, long ms, bool night)
        {
            if (!IsSignal || (night && nightFlash)) return true;
            var (k, into) = Phase(ms);
            var s = stages[k];
            for (int m = 0; m < moves.Count; m++)
                if (moves[m].from == leg && (s.green & (1UL << m)) != 0) return false;
            // Not in the stage's last seconds: there'd be no time to get across.
            return into < s.greenMs;
        }

        /// <summary>Walkers start across `leg` as stage `k` begins (none of its own movements green in it).</summary>
        bool WalkStarts(int leg, int k)
        {
            if (noCrossing) return false;
            var s = stages[k];
            for (int m = 0; m < moves.Count; m++)
                if (moves[m].from == leg && (s.green & (1UL << m)) != 0) return false;
            return true;
        }

        /// <summary>Every pair of foes has exactly one that gives way, and no stage gives two foes a protected green: the faults, if any.</summary>
        public List<string> Check()
        {
            var errors = new List<string>();
            for (int m = 0; m < moves.Count; m++)
                for (int n = m + 1; n < moves.Count; n++)
                {
                    if (Foes(m, n) != Foes(n, m)) errors.Add($"junction {node}: foes aren't mutual ({m}, {n})");
                    if (Foes(m, n) && Yields(m, n) == Yields(n, m)) errors.Add($"junction {node}: movements {m} and {n} {(Yields(m, n) ? "both give way" : "neither gives way")}");
                    if (!Foes(m, n) && (Yields(m, n) || Yields(n, m))) errors.Add($"junction {node}: {m} gives way to {n}, not a foe");
                }
            foreach (var s in stages)
            {
                ulong prot = s.green & ~s.permitted;
                for (int m = 0; m < moves.Count; m++)
                    if ((prot & (1UL << m)) != 0 && (foes[m] & prot) != 0) errors.Add($"junction {node}: a stage gives foes {m} a protected green together");
            }
            return errors;
        }

        // ------------------------------------------------------------- building

        internal void Build()
        {
            int L = legs.Count;
            legs.Sort((a, b) => a.angle != b.angle ? a.angle.CompareTo(b.angle) : a.road != b.road ? a.road.CompareTo(b.road) : a.atStart.CompareTo(b.atStart));
            moveIndex = new int[L * L];
            for (int i = 0; i < moveIndex.Length; i++) moveIndex[i] = -1;
            moves.Clear();
            for (int a = 0; a < L; a++)
                for (int b = 0; b < L; b++)
                {
                    if (a == b) continue;
                    var hin = Geo.Scale(legs[a].dir, -1);
                    var hout = legs[b].dir;
                    double angle = DetMath.Acos(Math.Max(-1, Math.Min(1, Geo.Dot(hin, hout))));
                    var turn = angle < 44 * Math.PI / 180 ? Turn.Straight : Geo.Cross(hin, hout) > 0 ? Turn.Near : Turn.Far;
                    moveIndex[a * L + b] = moves.Count;
                    moves.Add(new Movement { from = a, to = b, turn = turn });
                }
            int M = moves.Count;
            foes = new ulong[M];
            yields = new ulong[M];
            // Ports: each leg's lane in and lane out, round the junction.
            var portIn = new double[L];
            var portOut = new double[L];
            for (int i = 0; i < L; i++)
            {
                var u = legs[i].dir;
                var side = new Vec2(u.y, -u.x); // the lane in's side
                portIn[i] = DetMath.Atan2(u.y + side.y * 0.3, u.x + side.x * 0.3);
                portOut[i] = DetMath.Atan2(u.y - side.y * 0.3, u.x - side.x * 0.3);
            }
            for (int m = 0; m < M; m++)
                for (int n = 0; n < M; n++)
                {
                    if (m == n) continue;
                    var p = moves[m];
                    var q = moves[n];
                    bool foe = p.from != q.from && (p.to == q.to || Interleave(portIn[p.from], portOut[p.to], portIn[q.from], portOut[q.to]));
                    if (foe) foes[m] |= 1UL << n;
                }
            for (int m = 0; m < M; m++)
                for (int n = m + 1; n < M; n++)
                {
                    if ((foes[m] & (1UL << n)) == 0) continue;
                    if (GivesWay(m, n)) yields[m] |= 1UL << n;
                    else yields[n] |= 1UL << m;
                }
        }

        /// <summary>Whether chord a–b and chord c–d cross, the four ends being angles round a circle.</summary>
        static bool Interleave(double a, double b, double c, double d)
        {
            bool Between(double x, double lo, double hi)
            {
                // Strictly between lo and hi going anticlockwise.
                double span = Norm(hi - lo), off = Norm(x - lo);
                return off > 1e-9 && off < span - 1e-9;
            }
            if (Same(a, c) || Same(a, d) || Same(b, c) || Same(b, d)) return false;
            return Between(c, a, b) != Between(d, a, b);
        }

        static bool Same(double x, double y) => Math.Abs(Norm(x - y)) < 1e-9 || Math.Abs(Norm(x - y) - 2 * Math.PI) < 1e-9;

        static double Norm(double x)
        {
            double t = x % (2 * Math.PI);
            return t < 0 ? t + 2 * Math.PI : t;
        }

        /// <summary>Of foes m and n (m &lt; n), whether m is the one that gives way.</summary>
        bool GivesWay(int m, int n)
        {
            var p = moves[m];
            var q = moves[n];
            int rp = legs[p.from].rank, rq = legs[q.from].rank;
            if (rp != rq) return rp < rq;
            if ((p.turn == Turn.Straight) != (q.turn == Turn.Straight)) return p.turn != Turn.Straight;
            if (p.turn != q.turn) return p.turn == Turn.Far;
            // The car with the other coming from its near side (its right) gives way.
            bool qOnPsRight = OnNearSide(p.from, q.from), pOnQsRight = OnNearSide(q.from, p.from);
            if (qOnPsRight != pOnQsRight) return qOnPsRight;
            return true; // m < n: the lower-numbered goes first
        }

        /// <summary>Whether leg `other` is on the near side (the right, keeping right) of a car coming in by leg `me`.</summary>
        bool OnNearSide(int me, int other)
        {
            var h = Geo.Scale(legs[me].dir, -1);
            var near = new Vec2(-h.y, h.x);
            return Geo.Dot(legs[other].dir, near) > 1e-6;
        }

        /// <summary>Lay out the stages of a fixed-time plan.</summary>
        internal void PlanSignal()
        {
            stages.Clear();
            int L = legs.Count;
            var left = new List<int>();
            for (int i = 0; i < L; i++) left.Add(i);
            while (left.Count > 0)
            {
                // The highest-ranked leg left, and the one most opposite it.
                int a = left[0];
                foreach (int i in left) if (legs[i].roadRank > legs[a].roadRank) a = i;
                int b = -1;
                double most = -0.5;
                foreach (int i in left)
                {
                    if (i == a) continue;
                    double d = -Geo.Dot(legs[i].dir, legs[a].dir);
                    if (d > most + 1e-9)
                    {
                        most = d;
                        b = i;
                    }
                }
                var stage = new SignalStage { yellowMs = YELLOW_MS, redMs = ALL_RED_MS };
                var entry = new List<int> { a };
                if (b >= 0) entry.Add(b);
                // Straight on and near turns first, protected; then far turns, protected if they cross nothing protected.
                ulong prot = 0, perm = 0;
                foreach (var pass in new[] { 0, 1 })
                    for (int m = 0; m < moves.Count; m++)
                    {
                        var mv = moves[m];
                        if (!entry.Contains(mv.from) || (pass == 0) == (mv.turn == Turn.Far)) continue;
                        if ((foes[m] & prot) == 0) prot |= 1UL << m;
                        else perm |= 1UL << m;
                    }
                stage.green = prot | perm;
                stage.permitted = perm;
                bool major = legs[a].roadRank >= 3;
                stage.greenMs = major ? MAJOR_GREEN_MS : MINOR_GREEN_MS;
                stages.Add(stage);
                left.Remove(a);
                if (b >= 0) left.Remove(b);
            }
            cycleMs = 0;
            foreach (var s in stages) cycleMs += s.Ms;
        }
    }

    public static class Junctions
    {
        /// <summary>Every junction (three driveable legs or more) of the layout, by node id (null elsewhere).</summary>
        public static Junction[] Build(IsoLayout layout, ResolvedTraffic settings = null)
        {
            settings = settings ?? ResolvedTraffic.Defaults(layout);
            int N = layout.nodes.Count;
            var o = new Junction[N];
            var ends = new List<(int road, bool atStart)>[N];
            for (int n = 0; n < N; n++) ends[n] = new List<(int, bool)>();
            for (int r = 0; r < layout.roads.Count; r++)
            {
                var rr = layout.roads[r];
                if (rr.kind == RoadKind.Path) continue;
                ends[rr.from].Add((r, true));
                ends[rr.to].Add((r, false));
            }
            for (int n = 0; n < N; n++)
            {
                if (ends[n].Count < 3 || ends[n].Count > Junction.MAX_LEGS) continue;
                var node = layout.nodes[n];
                var j = new Junction { node = n, at = node.at, control = settings.control[n], nightFlash = settings.nightFlash, leading = settings.leading[n], noCrossing = settings.noCrossing[n] };
                bool ranked = j.control == JunctionControl.Priority || j.control == JunctionControl.Stop;
                foreach (var (road, atStart) in ends[n])
                {
                    var rr = layout.roads[road];
                    var pts = atStart ? rr.points : Reversed(rr.points);
                    var dir = Gen.RoadSenseStage.Heading(node.at, pts);
                    bool minor = node.minor != null && node.minor.Contains(road);
                    j.legs.Add(new JunctionLeg
                    {
                        road = road,
                        atStart = atStart,
                        dir = dir,
                        angle = DetMath.Atan2(dir.y, dir.x),
                        minor = minor,
                        roadRank = rr.priority,
                        rank = ranked ? (minor ? 0 : 1) : j.control == JunctionControl.AllWayStop || j.control == JunctionControl.RightBeforeLeft ? 0 : rr.priority,
                    });
                }
                j.Build();
                if (j.control == JunctionControl.Signal) j.PlanSignal();
                o[n] = j;
            }
            GreenWave(layout, o);
            return o;
        }

        /// <summary>
        /// Signals along the arterials share one cycle, each offset by how long a car at the road's
        /// speed takes to reach it from the first, so a car let go by one finds the next one green.
        /// </summary>
        static void GreenWave(IsoLayout layout, Junction[] junctions)
        {
            var signals = new List<Junction>();
            foreach (var j in junctions) if (j != null && j.IsSignal) signals.Add(j);
            if (signals.Count == 0) return;
            int cycle = 0;
            foreach (var j in signals) cycle = Math.Max(cycle, j.cycleMs);
            foreach (var j in signals)
            {
                // Stretch the major stage to the shared cycle.
                j.stages[0].greenMs += cycle - j.cycleMs;
                j.cycleMs = cycle;
            }
            // Distance (in seconds of driving) along arterials from the first signal; unconnected ones offset by a hash of where they are.
            int N = layout.nodes.Count;
            var secs = new double[N];
            for (int i = 0; i < N; i++) secs[i] = double.PositiveInfinity;
            var byNode = new List<int>[N];
            for (int i = 0; i < N; i++) byNode[i] = new List<int>();
            for (int r = 0; r < layout.roads.Count; r++)
            {
                var rr = layout.roads[r];
                if (rr.cls != RoadClass.Arterial) continue;
                byNode[rr.from].Add(r);
                byNode[rr.to].Add(r);
            }
            var done = new bool[N];
            foreach (var start in signals)
            {
                if (done[start.node]) continue;
                // Each signal not yet reached starts a wave of its own.
                secs[start.node] = 0;
                var frontier = new List<int> { start.node };
                while (frontier.Count > 0)
                {
                    int best = 0;
                    for (int i = 1; i < frontier.Count; i++)
                        if (secs[frontier[i]] < secs[frontier[best]] || (secs[frontier[i]] == secs[frontier[best]] && frontier[i] < frontier[best])) best = i;
                    int n = frontier[best];
                    frontier.RemoveAt(best);
                    if (done[n]) continue;
                    done[n] = true;
                    foreach (int r in byNode[n])
                    {
                        var rr = layout.roads[r];
                        int m = rr.from == n ? rr.to : rr.from;
                        double t = secs[n] + Geo.PolylineLength(rr.points) / Math.Max(0.5, rr.speed);
                        if (t < secs[m])
                        {
                            secs[m] = t;
                            frontier.Add(m);
                        }
                    }
                }
            }
            foreach (var j in signals)
            {
                long off = double.IsInfinity(secs[j.node]) ? (long)(Layout.Hash(j.node, 7) * cycle) : -(long)Math.Round(secs[j.node] * 1000);
                j.offsetMs = (int)(((off % cycle) + cycle) % cycle);
            }
        }

        static List<Vec2> Reversed(List<Vec2> pts)
        {
            var r = new List<Vec2>(pts);
            r.Reverse();
            return r;
        }
    }
}

// Signal timing by Webster's method (Webster 1958, "Traffic signal settings", Road
// Research Technical Paper 39; docs/town-life.md §2). Each quarter hour (the HCM's
// analysis period) every signal's cycle and split are worked out again from the cars
// that went through it in the quarter just gone:
//
//   y  = the busiest lane's flow on each stage over the saturation flow (1,900 an hour)
//   C₀ = (1.5 L + 5) / (1 − Σy)     the cycle that delays drivers least (L: lost time)
//   gᵢ = (C₀ − L) · yᵢ / Σy          each stage's share of the green
//
// held to a sensible range (a short cycle still lets walkers across; a long one doesn't
// leave a side street waiting minutes), and never shorter than the junction's slowest
// turn or widest crossing needs (NeedGreen). The stage showing keeps its end where it was (or
// sooner), so nobody's light jumps; the next one runs to the new timing.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public sealed partial class TownLife
    {
        /// <summary>How often the signals are retimed: a quarter hour, the HCM's analysis period.</summary>
        public const long RETIME_MS = 15 * TownClock.MINUTE;
        /// <summary>Saturation flow: cars an hour of green a lane can pass (HCM base, ~1.9 s between cars at the line) [T8].</summary>
        public const double SATURATION = 1900;
        /// <summary>Time lost each stage (ms): starting up, and the end of the amber nobody uses (Webster's ~4 s a phase).</summary>
        public const long LOST_MS = 4000;
        /// <summary>The cycle's bounds (ms): Webster's formula wants a shorter one when it's quiet, but walkers need to get across.</summary>
        public const long MIN_CYCLE_MS = 40_000, MAX_CYCLE_MS = 120_000;
        /// <summary>A stage's green, amber included, at least and at most (ms).</summary>
        public const long MIN_STAGE_MS = 12_000, MAX_STAGE_MS = 60_000;

        /// <summary>The slowest walker's pace (tiles a second: People's slowest, 2 mph), as a light times its crossings.</summary>
        const double SLOWEST_PACE = 0.22;
        /// <summary>The longest vehicle a light times its turns for (tiles: a bus).</summary>
        const double LONGEST_VEHICLE = BUS_LENGTH;
        /// <summary>Over the slowest turn or crossing, a stage's green has this much more (ms): a car or walker arriving just after it starts still goes.</summary>
        const long SPARE_MS = 3000;
        /// <summary>The most a stage's green can be (ms): what the wire's u16 of ms to go holds.</summary>
        const long WIRE_MOST_MS = 65_000;

        /// <summary>Each signal's least green a stage, by node (NeedGreen), worked out once.</summary>
        readonly Dictionary<int, long[]> needGreen = new Dictionary<int, long[]>();

        /// <summary>
        /// The least green stage `stage` of junction `j` can have (ms): MIN_STAGE_MS, or long enough for its slowest
        /// turn (a bus round it, as Judge times it: never started unless it can be finished on the green) and for
        /// the slowest walker over the widest road it lets them cross (MayCross: likewise), with SPARE_MS over.
        /// Shorter, a U-turn or a wide crossing would never be let go.
        /// </summary>
        public long NeedGreen(Junction j, int stage)
        {
            if (!needGreen.TryGetValue(j.node, out var need))
            {
                need = new long[] { MIN_STAGE_MS, MIN_STAGE_MS };
                foreach (int id in j.movements)
                {
                    var m = net.movements[id];
                    long through = (long)Math.Ceiling((m.length + LONGEST_VEHICLE + FOLLOWING) / m.speed * 1000) + SPARE_MS;
                    need[m.stage] = Math.Max(need[m.stage], through);
                }
                for (int leg = 0; leg < j.legs.Count; leg++)
                {
                    // Walkers cross a road while its traffic's held: on the other stage.
                    if (!walkways.longestCrossing.TryGetValue((j.node, j.legs[leg]), out double over)) continue;
                    int on = 1 - j.legStage[leg];
                    need[on] = Math.Max(need[on], (long)Math.Ceiling(over / SLOWEST_PACE * 1000) + SPARE_MS);
                }
                for (int s = 0; s < 2; s++) need[s] = Math.Min(WIRE_MOST_MS, (need[s] + 999) / 1000 * 1000);
                needGreen[j.node] = need;
            }
            return need[stage];
        }

        /// <summary>Cars let round from each lane into a signal since the last retiming, and when that was.</summary>
        readonly Dictionary<int, int> signalCounts = new Dictionary<int, int>();
        long countedSince = -1;

        /// <summary>A car let round turn `m`: counted for its signal's next timing.</summary>
        void Counted(Movement m)
        {
            if (!m.signal) return;
            signalCounts.TryGetValue(m.from, out int n);
            signalCounts[m.from] = n + 1;
        }

        /// <summary>The next quarter hour on the clock after `moment`.</summary>
        static long NextQuarter(long moment) => ((long)Math.Floor(moment / (double)RETIME_MS) + 1) * RETIME_MS;

        /// <summary>Every signal timed again from the quarter hour just gone (Webster), and the next retiming due.</summary>
        void Retime()
        {
            long window = countedSince >= 0 ? Now - countedSince : 0;
            if (window >= TownClock.MINUTE)
            {
                foreach (var j in net.junctions.Values)
                {
                    if (j.control != Control.Signal) continue;
                    var (g0, g1) = Webster(j, window);
                    if (g0 == j.green[0] && g1 == j.green[1]) continue;
                    var (stage, ends) = RoadNet.Stage(j, Now);
                    j.green[0] = g0;
                    j.green[1] = g1;
                    j.offset = RoadNet.OffsetFor(j, stage, ends - Now, Now);
                    stats.retimed++;
                    Wake(j.node);
                }
            }
            signalCounts.Clear();
            countedSince = Now;
            Schedule(K_RETIME, 0, NextQuarter(Now));
        }

        /// <summary>
        /// Junction `j`'s greens (ms, amber included) by Webster's method from the cars counted over the last
        /// `window` ms: each stage's critical flow ratio from its busiest lane, the optimum cycle from them, and the
        /// green shared in their proportion, each stage given back its lost time; held within the bounds, in whole seconds.
        /// </summary>
        public (long g0, long g1) Webster(Junction j, long window)
        {
            var y = new double[2];
            foreach (int id in j.movements)
            {
                var m = net.movements[id];
                signalCounts.TryGetValue(m.from, out int n);
                double flow = n * (TownClock.HOUR / (double)Math.Max(1, window));
                y[m.stage] = Math.Max(y[m.stage], flow / SATURATION);
            }
            var (g0, g1) = WebsterGreens(y[0], y[1]);
            return (Math.Max(g0, NeedGreen(j, 0)), Math.Max(g1, NeedGreen(j, 1)));
        }

        /// <summary>Webster's two greens (ms, amber included) for critical flow ratios `y0` and `y1`.</summary>
        public static (long g0, long g1) WebsterGreens(double y0, double y1)
        {
            double sum = y0 + y1, lost = 2 * LOST_MS / 1000.0;
            double cycle = sum >= 0.95 ? MAX_CYCLE_MS / 1000.0 : (1.5 * lost + 5) / (1 - sum);
            cycle = Math.Max(MIN_CYCLE_MS / 1000.0, Math.Min(MAX_CYCLE_MS / 1000.0, cycle));
            double share0 = sum > 1e-9 ? y0 / sum : 0.5;
            double effective = cycle - lost;
            long g0 = Seconds(effective * share0 + LOST_MS / 1000.0), g1 = Seconds(effective * (1 - share0) + LOST_MS / 1000.0);
            return (g0, g1);
        }

        static long Seconds(double s) => Math.Max(MIN_STAGE_MS, Math.Min(MAX_STAGE_MS, (long)Math.Round(s) * 1000));
    }
}

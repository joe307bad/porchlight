// Giving up in the traffic. The junctions' rules keep the box clear and
// undo a knot of cars waiting on each other at a junction (Cars.cs), but a
// town's whole grid can still lock: a ring of full lanes round a block, each
// car waiting for room the next one holds, none of them at a junction to
// ask. Real drivers don't sit there all night: after long enough they pull
// over where they can and walk. So here: once a minute every car on the road
// is looked at, and one that hasn't moved in STUCK_MS gives up its drive. It
// pulls over by the nearest building and its driver walks the rest of the way
// (or, bound for a gate, their trip's given up); a van goes off its rounds.
// That frees a car's length in the ring, and the ring moves again. (The idea
// of cancelling drivers held up past a threshold is A/B Street's.) The car is
// fetched home by morning.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public sealed partial class TownLife
    {
        /// <summary>A car that hasn't moved in this long gives up its drive.</summary>
        public const long STUCK_MS = 10 * TownClock.MINUTE;
        /// <summary>Less than this (tiles) in a minute isn't moving.</summary>
        const double MOVED = 0.3;

        /// <summary>Every minute: who's moved, and who hasn't in a long while.</summary>
        void Sweep()
        {
            List<Car> stuck = null;
            foreach (var c in cars)
            {
                if (c.seg < 0 || c.bus >= 0 || (c.service >= 0 && c.state == CarState.AtStop))
                {
                    c.seenSeg = -1;
                    continue;
                }
                double f = Front(c);
                if (c.seg != c.seenSeg || Math.Abs(f - c.seenFront) > MOVED || c.state == CarState.Parking || c.state == CarState.Unparking)
                {
                    c.seenSeg = c.seg;
                    c.seenFront = f;
                    c.movedAt = Now;
                }
                else if (Now - c.movedAt >= STUCK_MS) (stuck = stuck ?? new List<Car>()).Add(c);
            }
            if (stuck != null) foreach (var c in stuck) GiveUp(c);
            Congestion();
            Schedule(K_SWEEP, 0, Now + TownClock.MINUTE);
        }

        /// <summary>Car `c` gives up its drive: pulled over by the nearest building, its driver on foot (or a van off its rounds).</summary>
        void GiveUp(Car c)
        {
            stats.gaveUp++;
            c.seenSeg = -1;
            if (c.van >= 0)
            {
                RemoveVan(c);
                return;
            }
            if (c.service >= 0)
            {
                ServiceHome(services[c.service]);
                return;
            }
            var p = c.owner >= 0 ? people[c.owner] : null;
            var at = CarPoint(c).at;
            if (p == null || p.trip == null)
            {
                RemoveFromRoad(c);
                return;
            }
            if (c.dest.border)
            {
                Fail(p, "gave up in the traffic");
                return;
            }
            // The nearest building: it's left by there, and they walk on from it.
            int near = -1;
            double best = double.PositiveInfinity;
            foreach (var kv in net.access)
            {
                double d = Geo.Dist(at, kv.Value.door);
                if (d < best || (d == best && kv.Key < near))
                {
                    best = d;
                    near = kv.Key;
                }
            }
            if (c.target >= 0 && spotHolder[c.target] == c.id) spotHolder[c.target] = -1;
            RemoveFromRoad(c);
            c.target = -1;
            c.spot = -1;
            c.offMap = false;
            c.parkedBy = near >= 0 ? near : c.dest.id;
            DriveDone(p);
        }
    }
}

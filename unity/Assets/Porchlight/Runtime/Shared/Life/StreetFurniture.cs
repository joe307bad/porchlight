// Where the junctions' traffic signals and stop signs stand: one at the stop
// line of each lane into a junction that has them, on the kerb to the driver's
// right, facing back down the lane at the cars coming. A signal's arm reaches
// from there out over the middle of the lane. Manage draws them from this
// (Analytics.Signals, GET /mp/sim/signals) and the game puts its models here
// (Rendering/Signage.cs), so the two always agree. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public struct SignalPost
    {
        /// <summary>The junction's node, and the stage this lane goes on (0 or 1).</summary>
        public int node, stage;
        /// <summary>The post's foot, on the kerb at the stop line.</summary>
        public Vec2 at;
        /// <summary>The way the lane's cars are going (unit): the lights face the other way, at them.</summary>
        public Vec2 along;
        /// <summary>From the post towards the middle of the lane (unit), and how far that is (tiles).</summary>
        public Vec2 inward;
        public double reach;
    }

    public struct StopPost
    {
        public Vec2 at;
        /// <summary>The way the lane's cars are going (unit): the sign faces the other way, at them.</summary>
        public Vec2 along;
        /// <summary>From the post towards the middle of the lane (unit).</summary>
        public Vec2 inward;
        /// <summary>An all-way stop (every lane in stops).</summary>
        public bool allWay;
    }

    public static class StreetFurniture
    {
        /// <summary>How far out from a lane's middle the kerb post stands: a quarter of the road and a little more.</summary>
        public static double KerbOut(RoadNet net, Lane l) => net.roads[l.road].width / 4 + 0.35;

        /// <summary>A signal post at the stop line of each lane into each signal, junctions in node order.</summary>
        public static List<SignalPost> Signals(RoadNet net)
        {
            var o = new List<SignalPost>();
            foreach (int node in Nodes(net))
            {
                var j = net.junctions[node];
                if (j.control != Control.Signal) continue;
                var seen = new HashSet<int>();
                foreach (int id in j.movements)
                {
                    var m = net.movements[id];
                    if (!seen.Add(m.from)) continue;
                    var l = net.lanes[m.from];
                    var (kerb, along) = net.LanePoint(l.road, l.dir, l.end, KerbOut(net, l));
                    o.Add(new SignalPost { node = node, stage = m.stage, at = kerb, along = along, inward = Inward(net, l, kerb), reach = KerbOut(net, l) });
                }
            }
            return o;
        }

        /// <summary>A stop sign at the stop line of each lane that stops, junctions in node order.</summary>
        public static List<StopPost> Stops(RoadNet net)
        {
            var o = new List<StopPost>();
            foreach (int node in Nodes(net))
            {
                var j = net.junctions[node];
                if (j.control != Control.Stop && j.control != Control.AllWayStop) continue;
                var seen = new HashSet<int>();
                foreach (int id in j.movements)
                {
                    var m = net.movements[id];
                    if (!m.stops || !seen.Add(m.from)) continue;
                    var l = net.lanes[m.from];
                    var (kerb, along) = net.LanePoint(l.road, l.dir, l.end, KerbOut(net, l));
                    o.Add(new StopPost { at = kerb, along = along, inward = Inward(net, l, kerb), allWay = j.control == Control.AllWayStop });
                }
            }
            return o;
        }

        /// <summary>
        /// What a signal shows the lane on `stage` at `moment`: 0 red, 1 amber, 2 green. Amber is the last
        /// AMBER_MS of its green (the stage's end may come early, for a car waiting the other way: that's
        /// the junction's offset moving, which Stage already has).
        /// </summary>
        public static int Showing(Junction j, int stage, long moment)
        {
            var (green, ends) = RoadNet.Stage(j, moment);
            if (green != stage) return 0;
            return ends - moment <= AMBER_MS ? 1 : 2;
        }

        /// <summary>How long a light shows amber before red (ITE: about a second for each 10 mph; 3 s at a town's speeds).</summary>
        public const long AMBER_MS = 3000;

        static Vec2 Inward(RoadNet net, Lane l, Vec2 kerb)
        {
            var (middle, _) = net.LanePoint(l.road, l.dir, l.end, 0);
            double dx = middle.x - kerb.x, dy = middle.y - kerb.y, d = Math.Sqrt(dx * dx + dy * dy);
            return d > 1e-9 ? new Vec2(dx / d, dy / d) : new Vec2(0, 0);
        }

        static List<int> Nodes(RoadNet net)
        {
            var nodes = new List<int>(net.junctions.Keys);
            nodes.Sort();
            return nodes;
        }
    }
}

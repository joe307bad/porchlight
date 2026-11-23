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
        /// <summary>Its junction's place among the town's signals (SignalJunctions, and the lights on the wire).</summary>
        public int light;
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
            int light = -1;
            foreach (int node in Nodes(net))
            {
                var j = net.junctions[node];
                if (j.control != Control.Signal) continue;
                light++;
                var seen = new HashSet<int>();
                foreach (int id in j.movements)
                {
                    var m = net.movements[id];
                    if (!seen.Add(m.from)) continue;
                    var l = net.lanes[m.from];
                    var (kerb, along) = net.LanePoint(l.road, l.dir, l.end, KerbOut(net, l));
                    o.Add(new SignalPost { node = node, light = light, stage = m.stage, at = kerb, along = along, inward = Inward(net, l, kerb), reach = KerbOut(net, l) });
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
            return Showing(green, ends, stage, moment);
        }

        /// <summary>
        /// What a light on `stage` shows at `now`, from a word that its junction had `green` on until `ends`
        /// (both on any one clock). Every stage runs a whole STAGE_MS from its start, one changed early for a
        /// waiting car too, so `green` began at ends − STAGE_MS: before that (lamps are drawn a moment behind
        /// the word) the other stage was on, and after `ends` they take turns until the next word.
        /// </summary>
        public static int Showing(int green, double ends, int stage, double now)
        {
            long turns = (long)Math.Floor((now - (ends - RoadNet.STAGE_MS)) / RoadNet.STAGE_MS);
            if (turns != 0)
            {
                if ((turns & 1) != 0) green ^= 1;
                ends += turns * RoadNet.STAGE_MS;
            }
            if (green != stage) return 0;
            return ends - now <= AMBER_MS ? 1 : 2;
        }

        /// <summary>The town's signals, in node order: the order of their lights on the wire.</summary>
        public static List<Junction> SignalJunctions(RoadNet net)
        {
            var o = new List<Junction>();
            foreach (int node in Nodes(net)) if (net.junctions[node].control == Control.Signal) o.Add(net.junctions[node]);
            return o;
        }

        /// <summary>
        /// The town's lights at `moment`, for the players following it (the end of each delta, Delta.cs): u8 n, then
        /// for each signal (SignalJunctions) u8 the stage that's green and u16 the ms until it ends, big-endian.
        /// The server's lights change early for a waiting car, so the players' can't be worked out from the clock alone.
        /// </summary>
        public static byte[] EncodeLights(RoadNet net, long moment)
        {
            var js = SignalJunctions(net);
            int n = Math.Min(js.Count, 255);
            var o = new byte[1 + 3 * n];
            o[0] = (byte)n;
            for (int k = 0; k < n; k++)
            {
                var (green, ends) = RoadNet.Stage(js[k], moment);
                long left = Math.Max(0, Math.Min(0xFFFF, ends - moment));
                o[1 + 3 * k] = (byte)green;
                o[2 + 3 * k] = (byte)(left >> 8);
                o[3 + 3 * k] = (byte)left;
            }
            return o;
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

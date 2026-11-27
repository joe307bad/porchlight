// Where the junctions' traffic signals and stop signs stand: one for each lane
// into a junction that has them, on the corner of the sidewalk to the driver's
// right (where it meets the cross street's sidewalk, as Layout trims them), just
// in from the kerb, facing back down the lane at the cars coming. A signal's arm reaches
// from there out over the middle of the lane. Manage draws them from this
// (Analytics.Signals, GET /mp/sim/signals) and the game puts its models here
// (Rendering/Signage.cs), so the two always agree. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;

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
        public static double KerbOut(RoadNet net, Lane l) => net.roads[l.road].width / 2 - net.KerbOffset(l.road) + 0.35;

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
                    var (at, along, inward, reach) = Corner(net, j, l);
                    o.Add(new SignalPost { node = node, light = light, stage = m.stage, at = at, along = along, inward = inward, reach = reach });
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
                    var (at, along, inward, _) = Corner(net, j, l);
                    o.Add(new StopPost { at = at, along = along, inward = inward, allWay = j.control == Control.AllWayStop });
                }
            }
            return o;
        }

        /// <summary>
        /// What a signal shows the lane on `stage` at `moment`: 0 red, 1 amber, 2 green. Amber is the last
        /// AMBER_MS of its green (the stage's end may come early, for a car waiting the other way, or move when the
        /// signal's retimed: that's the junction's offset and greens, which Stage already has).
        /// </summary>
        public static int Showing(Junction j, int stage, long moment)
        {
            var (green, ends) = RoadNet.Stage(j, moment);
            return Showing(green, ends, stage, moment, j.green[0], j.green[1]);
        }

        /// <summary>
        /// What a light on `stage` shows at `now`, from a word that its junction had `green` on until `ends`
        /// (both on any one clock), its stages `g0` and `g1` ms of green each: before the word's stage began
        /// (lamps are drawn a moment behind the word) and after `ends`, the stages take turns on that timing
        /// until the next word.
        /// </summary>
        public static int Showing(int green, double ends, int stage, double now, long g0 = RoadNet.STAGE_MS, long g1 = RoadNet.STAGE_MS)
        {
            double period = Math.Max(1, g0 + g1);
            // Where `now` falls in the cycle, the word's stage ending at `ends` (stage 0 ends g0 in, stage 1 at the cycle's end).
            double anchor = green == 0 ? g0 : period;
            double into = ((anchor - (ends - now)) % period + period) % period;
            int showing = into < g0 ? 0 : 1;
            double left = (showing == 0 ? g0 : period) - into;
            if (showing != stage) return 0;
            return left <= AMBER_MS ? 1 : 2;
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
        /// for each signal (SignalJunctions) u8 the stage that's green and u16 the ms until it ends, big-endian; then
        /// their timing, u8 n again and for each u8 stage 0's green and u8 stage 1's (whole seconds, amber included).
        /// The server's lights change early for a waiting car and are retimed by the traffic (Signals.cs), so the
        /// players' can't be worked out from the clock alone. A player too old for the timing reads the lights and
        /// stops there.
        /// </summary>
        public static byte[] EncodeLights(RoadNet net, long moment)
        {
            var js = SignalJunctions(net);
            int n = Math.Min(js.Count, 255);
            var o = new byte[2 + 5 * n];
            o[0] = (byte)n;
            o[1 + 3 * n] = (byte)n;
            for (int k = 0; k < n; k++)
            {
                var (green, ends) = RoadNet.Stage(js[k], moment);
                long left = Math.Max(0, Math.Min(0xFFFF, ends - moment));
                o[1 + 3 * k] = (byte)green;
                o[2 + 3 * k] = (byte)(left >> 8);
                o[3 + 3 * k] = (byte)left;
                o[2 + 3 * n + 2 * k] = (byte)Math.Min(255, js[k].green[0] / 1000);
                o[3 + 3 * n + 2 * k] = (byte)Math.Min(255, js[k].green[1] / 1000);
            }
            return o;
        }

        /// <summary>How long a light shows amber before red (ITE: about a second for each 10 mph; 3 s at a town's speeds).</summary>
        public const long AMBER_MS = 3000;

        /// <summary>How far in from the sidewalk's middle, towards the kerb, a post stands (tiles).</summary>
        const double IN_FROM_MIDDLE = Layout.SIDEWALK * 0.25;
        /// <summary>How far back from a junction a sidewalk can end at its corner (tiles: Layout's CORNER_TRIM).</summary>
        const double CORNER_REACH = 4;
        const double STEP = 0.1;

        /// <summary>
        /// Where lane `l`'s post stands at junction `j`: on the corner of the sidewalk on its right, where that
        /// sidewalk meets a cross street's (the first such meeting coming up to the junction, as Layout ends the
        /// sidewalk there), just in from the kerb. With no cross street's sidewalk to meet (the road runs straight
        /// on), on the kerb at the stop line. The way the lane goes, from the post to the lane's middle, and how far.
        /// </summary>
        public static (Vec2 at, Vec2 along, Vec2 inward, double reach) Corner(RoadNet net, Junction j, Lane l)
        {
            double w = net.roads[l.road].width;
            double side = w / 2 + Layout.PARKING + Layout.SIDEWALK / 2;
            // The corner on its right; at a T with the side street on its left there's none (the sidewalk runs on
            // past), so level with the corner across the road, where the side street meets the far sidewalk.
            double best = MeetsAt(net, j, l, side);
            if (double.IsInfinity(best)) best = MeetsAt(net, j, l, -side);
            if (double.IsInfinity(best))
            {
                var (kerb, way) = net.LanePoint(l.road, l.dir, l.end, KerbOut(net, l));
                return (kerb, way, Towards(kerb, net.LanePoint(l.road, l.dir, l.end, 0).at), KerbOut(net, l));
            }
            // Square across the lane from there: on its sidewalk's line, a little in towards the kerb.
            var (middle, along) = net.LanePoint(l.road, l.dir, best, 0);
            var at = net.LanePoint(l.road, l.dir, best, side - net.KerbOffset(l.road) - IN_FROM_MIDDLE).at;
            return (at, along, Towards(at, middle), Geo.Dist(at, middle));
        }

        /// <summary>
        /// How far along lane `l` (from its start) the sidewalk `offset` tiles from its road's middle (+: on its right)
        /// first meets a cross street's sidewalk coming up to junction `j`, or infinity. The road carrying straight on
        /// isn't a cross street.
        /// </summary>
        static double MeetsAt(RoadNet net, Junction j, Lane l, double offset)
        {
            double w = net.roads[l.road].width, len = net.lengths[l.road];
            var mine = new List<(Vec2 p, double done)>();
            for (double done = Math.Max(0, len - CORNER_REACH - 2); done <= len + 1e-9; done += STEP)
                mine.Add((net.LanePoint(l.road, l.dir, done, offset - net.KerbOffset(l.road)).at, done));
            var back = net.LanePoint(l.road, l.dir, Math.Max(0, len - 2), 0).at;
            double best = double.PositiveInfinity;
            foreach (int road in j.legs)
            {
                if (road == l.road) continue;
                double wo = net.roads[road].width, lo = net.lengths[road];
                double so = wo / 2 + Layout.PARKING + Layout.SIDEWALK / 2;
                var pts = net.roads[road].points;
                bool atStart = Geo.Dist(pts[0], j.at) <= Geo.Dist(pts[pts.Count - 1], j.at);
                // Straight on (within STRAIGHT_ON of the way this lane comes in): not a cross street.
                var away = net.LanePoint(road, 1, atStart ? Math.Min(lo, 2) : Math.Max(0, lo - 2), -net.KerbOffset(road)).at;
                double ix = j.at.x - back.x, iy = j.at.y - back.y, ox = away.x - j.at.x, oy = away.y - j.at.y;
                double cos = (ix * ox + iy * oy) / Math.Max(1e-9, Math.Sqrt((ix * ix + iy * iy) * (ox * ox + oy * oy)));
                if (cos > STRAIGHT_ON) continue;
                foreach (int dir in new[] { 1, -1 })
                {
                    var other = new List<Vec2>();
                    for (double s = 0; s <= CORNER_REACH + 2 + 1e-9; s += STEP)
                    {
                        double along = atStart ? s : lo - s;
                        other.Add(net.LanePoint(road, dir, dir == 1 ? along : lo - along, so - net.KerbOffset(road)).at);
                    }
                    for (int a = 0; a + 1 < mine.Count; a++)
                        for (int b = 0; b + 1 < other.Count; b++)
                        {
                            var h = Geo.SegmentIntersection(mine[a].p, mine[a + 1].p, other[b], other[b + 1]);
                            if (h == null) continue;
                            best = Math.Min(best, mine[a].done + (mine[a + 1].done - mine[a].done) * h.Value.t);
                        }
                }
            }
            return best;
        }

        /// <summary>A road leaving a junction within about 30° of the way a lane comes in carries straight on.</summary>
        const double STRAIGHT_ON = 0.87;

        static Vec2 Towards(Vec2 from, Vec2 to)
        {
            double dx = to.x - from.x, dy = to.y - from.y, d = Math.Sqrt(dx * dx + dy * dy);
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

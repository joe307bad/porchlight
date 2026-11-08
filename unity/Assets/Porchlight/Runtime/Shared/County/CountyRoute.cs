// The way across the county to a town: which highways, and where to head in the
// space you're in to be on it. Towns, the junction and empty plots are the stops;
// highways join them. In a stop that isn't the one you want, you head for the gate
// whose highway leads on; on a stretch of highway, for the end of the stretch that
// way. Once you're in the town itself, the town's own streets take you the rest.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.County
{
    public static class CountyRoute
    {
        /// <summary>Where to head in the space you're in: a spot on its map (world tiles), and what's there.</summary>
        public struct Stop
        {
            public Vec2 at;
            public string name;
            /// <summary>The rest of the way to the town, along the roads (tiles, roughly).</summary>
            public double remaining;
        }

        /// <summary>How far past the end of a stretch to aim (tiles), so the next one has taken you over before you're there.</summary>
        const double PAST = CountyMap.HANDOVER + 4;
        /// <summary>How far in through a gate to aim from the highway (tiles): past where the space takes you over, short of where the highway's ground ends.</summary>
        const double INTO = CountyMap.ENTER + 0.5;

        /// <summary>From every stop, how far it is along the highways to `to` (Infinity: no way there).</summary>
        public static double[] DistancesTo(CountyMap map, Space to)
        {
            var dist = new double[map.spaces.Count];
            for (int i = 0; i < dist.Length; i++) dist[i] = double.PositiveInfinity;
            var done = new bool[dist.Length];
            dist[to.id] = 0;
            while (true)
            {
                int u = -1;
                for (int i = 0; i < dist.Length; i++)
                    if (!done[i] && !double.IsInfinity(dist[i]) && (u < 0 || dist[i] < dist[u])) u = i;
                if (u < 0) break;
                done[u] = true;
                foreach (var hw in map.highways)
                {
                    int v = hw.a.space == u ? hw.b.space : hw.b.space == u ? hw.a.space : -1;
                    if (v < 0 || done[v]) continue;
                    double d = dist[u] + hw.length;
                    if (d < dist[v]) dist[v] = d;
                }
            }
            return dist;
        }

        /// <summary>
        /// The way on past where to head in `here` (Next), along the highways towards the town `to`
        /// (world tiles): so the way shown carries on over the spaces ahead. Whole highways, till it's
        /// `max` tiles or more, so it always ends at a gate (a spot that stays put as you drive on).
        /// </summary>
        public static List<Vec2> Ahead(CountyMap map, Space here, Vec2 from, Space to, double max)
        {
            var o = new List<Vec2>();
            if (here == null || to == null || here.id == to.id) return o;
            var dist = DistancesTo(map, to);
            int at;
            if (here.kind == SpaceKind.Highway)
            {
                var hw = map.highways[here.highway];
                var (s, _) = CountyMap.Nearest(hw, from);
                double viaA = s + dist[hw.a.space], viaB = hw.length - s + dist[hw.b.space];
                if (double.IsInfinity(viaA) && double.IsInfinity(viaB)) return o;
                bool forward = Math.Abs(viaB - viaA) < 1e-6 ? s < hw.length / 2 : viaB < viaA;
                // From the end of this stretch (Next's aim) on to the end of the highway.
                double aim = forward ? Math.Min(hw.length, here.s1 + PAST) : Math.Max(0, here.s0 - PAST);
                var rest = forward ? CountyMap.Stretch(hw, aim, hw.length) : CountyMap.Stretch(hw, 0, aim);
                if (!forward) rest.Reverse();
                Append(o, rest);
                at = forward ? hw.b.space : hw.a.space;
            }
            else at = here.id;
            // Stop to stop, each time along the highway that gets there soonest.
            for (int guard = 0; at != to.id && guard < map.spaces.Count && Length(o) < max; guard++)
            {
                Highway best = null;
                bool fromA = true;
                double bestD = double.PositiveInfinity;
                foreach (var hw in map.highways)
                {
                    int other = hw.a.space == at ? hw.b.space : hw.b.space == at ? hw.a.space : -1;
                    if (other < 0) continue;
                    double d = hw.length + dist[other];
                    if (d < bestD)
                    {
                        bestD = d;
                        best = hw;
                        fromA = hw.a.space == at;
                    }
                }
                if (best == null) break;
                var pts = new List<Vec2>(best.points);
                if (!fromA) pts.Reverse();
                Append(o, pts);
                at = fromA ? best.b.space : best.a.space;
            }
            return o;
        }

        static void Append(List<Vec2> o, List<Vec2> pts)
        {
            foreach (var p in pts)
                if (o.Count == 0 || Geo.Dist(o[o.Count - 1], p) > 1e-6) o.Add(p);
        }

        static double Length(List<Vec2> pts)
        {
            double d = 0;
            for (int i = 1; i < pts.Count; i++) d += Geo.Dist(pts[i - 1], pts[i]);
            return d;
        }

        /// <summary>
        /// Where to head in `here` (at `from`, world tiles) to get to the town `to`; null once you're in it
        /// (its streets take over), or if there's no way there from here.
        /// </summary>
        public static Stop? Next(CountyMap map, Space here, Vec2 from, Space to)
        {
            if (here == null || to == null || here.id == to.id) return null;
            var dist = DistancesTo(map, to);
            if (here.kind == SpaceKind.Highway)
            {
                var hw = map.highways[here.highway];
                var (s, _) = CountyMap.Nearest(hw, from);
                double viaA = s + dist[hw.a.space], viaB = hw.length - s + dist[hw.b.space];
                if (double.IsInfinity(viaA) && double.IsInfinity(viaB)) return null;
                // Level only at an end, where the way on runs back along this highway: on to the far end, then.
                bool forward = Math.Abs(viaB - viaA) < 1e-6 ? s < hw.length / 2 : viaB < viaA;
                double aim = forward ? here.s1 + PAST : here.s0 - PAST;
                var port = forward ? hw.b : hw.a;
                var next = map.spaces[port.space];
                Vec2 at;
                if (aim > 0 && aim < hw.length) at = CountyMap.PointAt(hw, aim);
                else
                {
                    // The last stretch: a little way in through the gate, where the town (or junction, or plot) takes you.
                    var edge = CountyMap.PortPoint(next, port.side);
                    var o = Compass.Outward(port.side);
                    at = new Vec2(edge.x - o.x * INTO, edge.y - o.y * INTO);
                }
                return new Stop
                {
                    at = at,
                    name = next.id == to.id ? to.name : $"{next.name}, on the way to {to.name}",
                    remaining = forward ? viaB : viaA,
                };
            }
            // A town, the junction or a plot: out by the gate whose highway gets there soonest.
            Side? side = null;
            double bestD = double.PositiveInfinity;
            foreach (var hw in map.highways)
            {
                Port mine, theirs;
                if (hw.a.space == here.id) (mine, theirs) = (hw.a, hw.b);
                else if (hw.b.space == here.id) (mine, theirs) = (hw.b, hw.a);
                else continue;
                double d = hw.length + dist[theirs.space];
                if (d < bestD)
                {
                    bestD = d;
                    side = mine.side;
                }
            }
            if (side == null) return null;
            var gate = CountyMap.PortPoint(here, side.Value);
            return new Stop
            {
                at = gate,
                name = $"the {Compass.Name(side.Value)} gate, for {to.name}",
                remaining = bestD + Geo.Dist(from, gate),
            };
        }
    }
}

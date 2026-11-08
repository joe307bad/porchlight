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

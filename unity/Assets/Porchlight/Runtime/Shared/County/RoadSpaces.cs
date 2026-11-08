// The county's road spaces as towns of their own (TownModel), so everything
// that drives, walks and draws a town takes them as they are: a stretch of
// highway is a long thin town with one main road and countryside either side,
// the junction a crossroads, and one of your empty slots a plot with the road
// running through it.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.County
{
    public static class RoadSpaces
    {
        /// <summary>How far past its cut a highway's road carries on into the next stretch's ground (tiles).</summary>
        const double OVERLAP = 10;

        /// <summary>A road space's town: grass, its roads, and which tiles the next stretch draws.</summary>
        public static TownModel Build(CountyMap map, Space s)
        {
            if (s.kind == SpaceKind.Town) throw new ArgumentException("a town grows from its key");
            var p = new GenParams { width = s.width, height = s.height, waterFraction = 0, riverCount = 0, townRadius = 0.15 };
            string seed = $"county:{s.kind}:{s.name}:{Numbers.ToFixed(s.s0, 0)}";
            var m = TownModel.CreateEmpty(seed, p);
            m.width = s.width;
            m.height = s.height;
            m.heightmap = new float[s.width * s.height];
            for (int i = 0; i < m.heightmap.Length; i++) m.heightmap[i] = 0.5f;
            m.waterLevel = -1;
            m.townRadius = 0;
            m.roadName = s.name;
            var g = new GraphBuilder();
            int Node(Vec2 at)
            {
                foreach (var n in g.nodes) if (Geo.Dist(n.at, at) < 1e-6) return n.id;
                g.nodes.Add(new RoadNode { id = g.nodes.Count, at = at });
                return g.nodes.Count - 1;
            }
            void Road(List<Vec2> pts)
            {
                int a = Node(pts[0]), b = Node(pts[pts.Count - 1]);
                g.edges.Add(new RoadEdge { id = g.edges.Count, from = a, to = b, kind = RoadKind.Main, points = pts });
            }
            if (s.kind == SpaceKind.Highway)
            {
                var hw = map.highways[s.highway];
                double lo = Math.Max(0, s.s0 - OVERLAP), hi = Math.Min(hw.length, s.s1 + OVERLAP);
                var pts = new List<Vec2>();
                foreach (var q in CountyMap.Stretch(hw, lo, hi))
                {
                    var l = s.ToLocal(q);
                    // Inside the space (the ends at a town's edge sit on its border).
                    pts.Add(new Vec2(Math.Max(0, Math.Min(s.width, l.x)), Math.Max(0, Math.Min(s.height, l.y))));
                }
                pts = Geo.SimplifyRDP(pts, 0.05, (a, b) => true);
                Road(pts);
                m.gates.Add(pts[0]);
                m.gates.Add(pts[pts.Count - 1]);
                m.center = s.ToLocal(CountyMap.PointAt(hw, (s.s0 + s.s1) / 2));
                // The tiles nearer the next stretch's part of the road are its to draw.
                m.foreign = new byte[s.width * s.height];
                for (int y = 0; y < s.height; y++)
                    for (int x = 0; x < s.width; x++)
                    {
                        var (at, _) = CountyMap.Nearest(hw, s.ToWorld(new Vec2(x + 0.5, y + 0.5)));
                        if (at < s.s0 || at > s.s1) m.foreign[y * s.width + x] = 1;
                    }
            }
            else
            {
                // A crossroads (or a bend, or a T) through the middle.
                var mid = new Vec2(s.width / 2 + 0.5, s.height / 2 + 0.5);
                foreach (var side in s.sides)
                {
                    var e = Compass.GateEdge(side, s.width, s.height);
                    Road(new List<Vec2> { e, mid });
                    m.gates.Add(e);
                }
                m.center = mid;
            }
            m.roads = RoadGraphOps.Compact(g);
            m.spawn = m.center;
            m = new RasterizeStage().Run(m, Rng.Stage(seed, "rasterize"));
            return m;
        }
    }
}

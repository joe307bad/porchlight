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
        /// <summary>A stretch draws (and plants) its ground this far either side of its road (tiles); the countryside the rest.</summary>
        const double DRAWN = CountyTerrain.FLAT - 2;

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
                var line = CountyMap.Stretch(hw, lo, hi);
                // Into the spaces at the ends a little way (they overlap there): the road carries straight on in.
                if (lo <= 0)
                {
                    var o = Compass.Outward(hw.a.side);
                    line.Insert(0, new Vec2(line[0].x - o.x * CountyMap.INSET, line[0].y - o.y * CountyMap.INSET));
                }
                if (hi >= hw.length)
                {
                    var o = Compass.Outward(hw.b.side);
                    var e = line[line.Count - 1];
                    line.Add(new Vec2(e.x - o.x * CountyMap.INSET, e.y - o.y * CountyMap.INSET));
                }
                var pts = new List<Vec2>();
                foreach (var q in line)
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
                // Each tile is drawn by the stretch whose road it's nearest (the next one along, or another
                // highway's where two pass close by a small town), and a town, junction or plot draws its own.
                var near = new List<(Space seg, List<Vec2> road)>();
                foreach (var other in map.spaces)
                {
                    if (other.kind != SpaceKind.Highway) continue;
                    bool overlap = other.x0 < s.x0 + s.width && s.x0 < other.x0 + other.width && other.y0 < s.y0 + s.height && s.y0 < other.y0 + other.height;
                    if (overlap) near.Add((other, Geo.SimplifyRDP(CountyMap.Stretch(map.highways[other.highway], other.s0, other.s1), 0.05, (a, b) => true)));
                }
                var ends = new List<Space>();
                foreach (var other in map.spaces)
                {
                    if (other.kind == SpaceKind.Highway) continue;
                    bool overlap = other.x0 < s.x0 + s.width && s.x0 < other.x0 + other.width && other.y0 < s.y0 + s.height && s.y0 < other.y0 + other.height;
                    if (overlap) ends.Add(other);
                }
                var verge = Geo.SimplifyRDP(line, 0.05, (a, b) => true);
                m.foreign = new byte[s.width * s.height];
                for (int y = 0; y < s.height; y++)
                    for (int x = 0; x < s.width; x++)
                    {
                        var w = s.ToWorld(new Vec2(x + 0.5, y + 0.5));
                        // Off the verge, the countryside's: its hills rise there (CountyTerrain).
                        bool theirs = Geo.DistToPolyline(w, verge) > DRAWN;
                        foreach (var e in ends) if (e.Contains(w.x, w.y)) theirs = true;
                        if (!theirs && near.Count > 1)
                        {
                            Space best = s;
                            double bestD = double.PositiveInfinity;
                            foreach (var (seg, road) in near)
                            {
                                double d = Geo.DistToPolyline(w, road);
                                if (d < bestD - 1e-9)
                                {
                                    bestD = d;
                                    best = seg;
                                }
                            }
                            theirs = best != s;
                        }
                        if (theirs) m.foreign[y * s.width + x] = 1;
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
            if (s.kind == SpaceKind.Highway)
            {
                // Paved where it overlaps the next stretch (so the car's on road on both), but each draws,
                // names and lights only its own stretch: the two meet at the cut.
                var hw = map.highways[s.highway];
                var own = new List<Vec2>();
                foreach (var q in CountyMap.Stretch(hw, s.s0, s.s1)) own.Add(s.ToLocal(q));
                own = Geo.SimplifyRDP(own, 0.05, (a, b) => true);
                var trimmed = new GraphBuilder();
                trimmed.nodes.Add(new RoadNode { id = 0, at = own[0] });
                trimmed.nodes.Add(new RoadNode { id = 1, at = own[own.Count - 1] });
                trimmed.edges.Add(new RoadEdge { id = 0, from = 0, to = 1, kind = RoadKind.Main, points = own });
                m.roads = RoadGraphOps.Compact(trimmed);
            }
            return m;
        }
    }
}

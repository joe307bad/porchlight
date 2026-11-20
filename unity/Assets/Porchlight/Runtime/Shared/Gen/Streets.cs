// Stage 5: streets. The land between the main roads is platted into blocks by
// splitting it, region by region, with streets laid right across (StreetPlan.cs:
// hierarchical domain splitting, with every street kept a block's depth from any
// road alongside it). Then each street that ends where there was no road to meet
// (water, steep ground, the town's edge) either stays a cul-de-sac, ending in a
// round bulb to turn in, or is joined to a road within reach (straight on, or
// round the corner) by a connector that keeps the same distance from the roads
// alongside it. Land the roads never reach gets a footpath back to the network,
// and a dead end too short to serve anything, or with no room for its bulb, is cut back.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed partial class StreetsStage : IStepwiseStage
    {
        static readonly double MIN_ANGLE = (35 * Math.PI) / 180;
        /// <summary>cos 35°: a street meets a road at no shallower angle.</summary>
        const double COS_MIN_ANGLE = 0.8191520442889918;
        /// <summary>A street meeting a road this near a junction already there meets it at that junction (a crossroads).</summary>
        const double SNAP = 0.75;
        static readonly double GRID_TOLERANCE = (8 * Math.PI) / 180;
        const double MIN_SEGMENT = 1.5;
        const double JUNCTION_CLEARANCE = 3;
        const double HEADING_REACH = 2;
        const int PATH_COMPONENT_MIN = 150;
        const double DUPLICATE_DISTANCE = 1.5;
        const int MAX_DUPLICATE_PASSES = 200;
        /// <summary>A cul-de-sac runs at least this far from its junction to the middle of its bulb (tiles).</summary>
        const double CUL_DE_SAC_LENGTH = 4;
        const int MAX_PRUNE_PASSES = 50;
        /// <summary>No street runs alongside another road nearer than this, centre to centre (tiles): the Check's limit, with no land at all between them.</summary>
        public const double MIN_APART = 4;
        /// <summary>Dead ends looked at between one share of the work and the next (Steps).</summary>
        const int DEAD_ENDS_A_STEP = 40;

        struct Hit
        {
            public int edge;
            public Vec2 point;
            public double t;
        }

        public string Name => "streets";

        public TownModel Run(TownModel model, Rng rng) => Stepwise.Drain<TownModel>(done => Steps(model, rng, done));

        /// <summary>The streets, a share at a time (Run, all at once).</summary>
        public IEnumerable<string> Steps(TownModel model, Rng rng, Action<TownModel> done)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var C = model.center;
            double R = model.townRadius;
            var terrainInfo = Raster.RoadTerrain(model);
            var water = terrainInfo.water;
            var slope = terrainInfo.slope;
            var cost = terrainInfo.cost;
            var airport = Airfield.Mask(model.airport, w, h);
            var zoo = Menagerie.Mask(model.zoo, w, h, Menagerie.ROAD_CLEAR);
            var g = RoadGraphOps.BuilderFrom(model.roads);
            double Falloff(Vec2 pt) => Math.Max(0, 1 - Geo.Dist(pt, C) / R);
            var keep = new HashSet<int>();
            foreach (var n in g.nodes)
            {
                bool near = Geo.Dist(C, n.at) < 0.01;
                foreach (var q in model.gates) if (Geo.Dist(q, n.at) < 0.01) near = true;
                if (near) keep.Add(n.id);
            }

            bool TileOk(int x, int y)
            {
                if (x < 1 || y < 1 || x >= w - 1 || y >= h - 1) return false;
                int i = Grid.Index(x, y, w);
                return water[i] == 0 && airport[i] == 0 && zoo[i] == 0 && slope[i] <= p.maxRoadSlope;
            }
            bool LandOk(Vec2 a, Vec2 b)
            {
                foreach (var (x, y) in Grid.CellsOnSegment(a.x, a.y, b.x, b.y)) if (!TileOk(x, y)) return false;
                return true;
            }

            List<Vec2> IncidentDirs(int node)
            {
                var at = g.nodes[node].at;
                var o = new List<Vec2>();
                foreach (var e in RoadGraphOps.LiveEdges(g))
                {
                    if (e.from == node) o.Add(Heading(at, e.points));
                    if (e.to == node)
                    {
                        var rev = new List<Vec2>(e.points);
                        rev.Reverse();
                        o.Add(Heading(at, rev));
                    }
                }
                return o;
            }
            bool AngleClash(int node, Vec2 dir)
            {
                foreach (var d in IncidentDirs(node)) if (DetMath.Acos(Clamp(Geo.Dot(d, dir), -1, 1)) < MIN_ANGLE) return true;
                return false;
            }
            bool Connected(int a, int b)
            {
                foreach (var e in RoadGraphOps.LiveEdges(g)) if ((e.from == a && e.to == b) || (e.from == b && e.to == a)) return true;
                return false;
            }

            Hit? FirstHit(Vec2 a, Vec2 b, int[] skipNodes, int skipEdge = -1)
            {
                Hit? best = null;
                foreach (var e in RoadGraphOps.LiveEdges(g))
                {
                    if (e.id == skipEdge || Array.IndexOf(skipNodes, e.from) >= 0 || Array.IndexOf(skipNodes, e.to) >= 0) continue;
                    for (int s = 0; s + 1 < e.points.Count; s++)
                    {
                        var x = Geo.SegmentIntersection(a, b, e.points[s], e.points[s + 1]);
                        if (x != null && x.Value.t > 1e-6 && (best == null || x.Value.t < best.Value.t))
                            best = new Hit { edge = e.id, point = x.Value.point, t = x.Value.t };
                    }
                }
                return best;
            }
            bool Grazes(Vec2 a, Vec2 b, int[] ends)
            {
                var seg = new List<Vec2> { a, b };
                foreach (var e in RoadGraphOps.LiveEdges(g))
                    if (Array.IndexOf(ends, e.from) < 0 && Array.IndexOf(ends, e.to) < 0 && Geo.PolylineDistance(seg, e.points) < DUPLICATE_DISTANCE) return true;
                return false;
            }
            bool Joinable(int from, int to)
            {
                if (from == to || Connected(from, to)) return false;
                var a = g.nodes[from].at;
                var b = g.nodes[to].at;
                if (Geo.Dist(a, b) < MIN_SEGMENT || !LandOk(a, b) || FirstHit(a, b, new[] { from, to }) != null || Grazes(a, b, new[] { from, to })) return false;
                var d = Unit(Geo.Sub(b, a));
                return OnGrid(d) && !AngleClash(from, d) && !AngleClash(to, Geo.Scale(d, -1));
            }

            // Room for a cul-de-sac's bulb at `at` (the end of `edge`, or of a street still to be laid: -1): on gentle
            // dry land, its asphalt and parking lane clear of every other road's.
            double bulbRadius = Raster.BulbRadius(p);
            double bulbTiles = Raster.BulbHalfWidth(p);
            bool BulbFits(Vec2 at, int edge)
            {
                for (int y = (int)Math.Floor(at.y - bulbTiles); y <= (int)Math.Ceiling(at.y + bulbTiles); y++)
                    for (int x = (int)Math.Floor(at.x - bulbTiles); x <= (int)Math.Ceiling(at.x + bulbTiles); x++)
                        if (Numbers.Hypot(x + 0.5 - at.x, y + 0.5 - at.y) < bulbTiles && !TileOk(x, y)) return false;
                foreach (var e in RoadGraphOps.LiveEdges(g))
                {
                    if (e.id == edge) continue;
                    double room = bulbRadius + (e.kind == RoadKind.Path ? Raster.RoadDrawWidth(e.kind, p) / 2 : Raster.RoadDrawWidth(e.kind, p) / 2 + Raster.PARKING_WIDTH);
                    if (!(Geo.DistToPolyline(at, e.points) >= room)) return false;
                }
                return true;
            }
            // The gridded core joins every dead end it can; the post-war edge keeps more of them as cul-de-sacs.
            bool CulDeSac(int node, RoadEdge e)
            {
                if (!(Geo.PolylineLength(e.points) >= CUL_DE_SAC_LENGTH && BulbFits(g.nodes[node].at, e.id))) return false;
                var era = TownPlan.EraAt(model, g.nodes[node].at);
                double keepShare = era == Era.Core ? 0 : era == Era.PostWar ? Math.Min(1, p.culDeSacs * 1.5) : p.culDeSacs;
                return rng.Chance(keepShare);
            }

            // The land to plat: what blocks are made of (Parcels.Ground), and where a street may be laid.
            var rock = Raster.RockMask(slope, p);
            var parkZoo = Menagerie.Mask(model.zoo, w, h, 1);
            var land = new byte[w * h];
            var roadOk = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    bool inside = Numbers.Hypot(x + 0.5 - C.x, y + 0.5 - C.y) <= R;
                    land[i] = inside && water[i] == 0 && rock[i] == 0 && airport[i] == 0 && parkZoo[i] == 0 ? (byte)1 : (byte)0;
                    roadOk[i] = TileOk(x, y) ? (byte)1 : (byte)0;
                }
            }
            var plan = new StreetPlan(model, g, rng, land, roadOk, Falloff, BulbFits, FirstHit);
            int floor = plan.floor;
            if (p.roadDensity > 0) foreach (var part in plan.Run()) yield return "/plan" + part;

            // A connector keeps a block's depth from any road alongside it too: one it runs nearly parallel to (a road
            // it meets or crosses doesn't count).
            double apart = floor + 2 * Math.Ceiling(Raster.RoadHalfWidth(RoadKind.Street, p) - 1e-9) - 2;
            bool Roomy(Vec2 a, Vec2 b, int[] ends)
            {
                double len = Geo.Dist(a, b);
                if (len < 1e-9) return true;
                var u = new Vec2((b.x - a.x) / len, (b.y - a.y) / len);
                var box = Geo.PolygonBounds(new List<Vec2> { a, b });
                foreach (var e in RoadGraphOps.LiveEdges(g))
                {
                    if (e.kind == RoadKind.Path || Array.IndexOf(ends, e.from) >= 0 || Array.IndexOf(ends, e.to) >= 0) continue;
                    if (!BoxesNear(box, Geo.PolygonBounds(e.points), apart)) continue;
                    for (int k = 0; k + 1 < e.points.Count; k++)
                    {
                        var s0 = e.points[k];
                        var s1 = e.points[k + 1];
                        double sl = Geo.Dist(s0, s1);
                        if (sl < 1e-9 || Math.Abs(Geo.Cross(u, new Vec2((s1.x - s0.x) / sl, (s1.y - s0.y) / sl))) > 0.5) continue;
                        for (double t = 0; t <= len; t += 0.5)
                            if (Geo.DistToSegment(Geo.Lerp(a, b, t / len), s0, s1) < apart) return false;
                    }
                }
                return true;
            }

            foreach (var part in ConnectDeadEnds(g, keep, p.streetSegment * p.deadEndReach, FirstHit, LandOk, AngleClash, Joinable, CulDeSac, Roomy)) yield return part;
            foreach (var part in AddFootpaths(model, g, water, cost, slope, terrainInfo, C)) yield return part;

            RoadGraphOps.Planarize(g);
            yield return "/planarize";
            RoadGraphOps.MergePassThrough(g, keep);
            yield return "/merge";
            foreach (var part in DropDuplicateStreets(g)) yield return part;
            yield return "/dedupe";
            // A dead end too short to serve anything, or without room for its bulb, is cut back, a stretch at a time.
            for (int pass = 0; pass < MAX_PRUNE_PASSES; pass++)
            {
                var cut = new List<int>();
                foreach (var b in Raster.Bulbs(RoadGraphOps.LiveEdges(g)))
                {
                    var e = g.edges[b.edge];
                    if (keep.Contains(b.atEnd ? e.to : e.from)) continue;
                    if (!BulbFits(b.at, b.edge) || Geo.PolylineLength(e.points) < Math.Max(CUL_DE_SAC_LENGTH, floor)) cut.Add(b.edge);
                    // The gridded core has no cul-de-sacs: one it couldn't join up goes, unless it's a street down to the water.
                    else if (TownPlan.EraAt(model, b.at) == Era.Core && !NearWater(b.at, water, w, h)) cut.Add(b.edge);
                }
                if (cut.Count == 0) break;
                foreach (int e in cut) g.dead.Add(e);
                RoadGraphOps.MergePassThrough(g, keep);
                yield return "/prune";
            }
            var roads = KeepConnected(RoadGraphOps.Compact(g), model.center);
            Classify(roads, model, plan.collectors);
            yield return "/compact";
            var next = model.With();
            next.roads = roads;
            next.bridges = RoadGraphOps.ComputeBridges(next, Raster.WaterMask(next));
            done(next);
        }

        /// <summary>A dead end this near the water (tiles, either way) is a street down to the shore.</summary>
        public const int SHORE = 4;

        public static bool NearWater(Vec2 at, byte[] water, int w, int h)
        {
            for (int y = (int)Math.Floor(at.y) - SHORE; y <= (int)Math.Floor(at.y) + SHORE; y++)
                for (int x = (int)Math.Floor(at.x) - SHORE; x <= (int)Math.Floor(at.x) + SHORE; x++)
                    if (x >= 0 && y >= 0 && x < w && y < h && water[Grid.Index(x, y, w)] != 0) return true;
            return false;
        }

        /// <summary>A street within this of a collector's line, all along it, is (part of) that collector (tiles).</summary>
        const double ON_COLLECTOR = 0.75;

        /// <summary>
        /// Each road's class (FHWA): the main roads are arterials, but for a spur off them that leads only to the airport
        /// or the zoo (a collector); the streets laid by the first split of each piece of land between the main roads are
        /// collectors; the rest, and the footpaths, are local.
        /// </summary>
        public static void Classify(RoadGraph roads, TownModel model, List<(Vec2 a, Vec2 b)> collectors)
        {
            // Spurs: peel the main roads back from every end that isn't a gate.
            var degree = new Dictionary<int, int>();
            foreach (var e in roads.edges)
                if (e.kind == RoadKind.Main)
                    foreach (int n in new[] { e.from, e.to }) degree[n] = (degree.TryGetValue(n, out int d) ? d : 0) + 1;
            var gate = new HashSet<int>();
            foreach (var n in roads.nodes)
                foreach (var q in model.gates)
                    if (Geo.Dist(q, n.at) < 0.01) gate.Add(n.id);
            var spur = new HashSet<int>();
            for (bool more = true; more;)
            {
                more = false;
                foreach (var e in roads.edges)
                {
                    if (e.kind != RoadKind.Main || spur.Contains(e.id)) continue;
                    int end = degree[e.from] == 1 && !gate.Contains(e.from) ? e.from : degree[e.to] == 1 && !gate.Contains(e.to) ? e.to : -1;
                    if (end < 0) continue;
                    spur.Add(e.id);
                    degree[e.from]--;
                    degree[e.to]--;
                    more = true;
                }
            }
            foreach (var e in roads.edges)
            {
                if (e.kind == RoadKind.Main) e.cls = spur.Contains(e.id) ? RoadClass.Collector : RoadClass.Arterial;
                else if (e.kind == RoadKind.Path) e.cls = RoadClass.Local;
                else
                {
                    e.cls = RoadClass.Local;
                    foreach (var (a, b) in collectors)
                    {
                        bool along = true;
                        foreach (var q in e.points) if (Geo.DistToSegment(q, a, b) > ON_COLLECTOR) { along = false; break; }
                        if (along)
                        {
                            e.cls = RoadClass.Collector;
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Of the length of a town's roads for cars (not footpaths) inside the town (its radius: FHWA's shares are of
        /// urban roads, not the highways out across the fields), the share in each class: arterial, collector, local.
        /// </summary>
        public static (double arterial, double collector, double local) ClassShares(TownModel model)
        {
            double a = 0, c = 0, l = 0;
            const double STEP = 0.25;
            foreach (var e in model.roads.edges)
            {
                if (e.kind == RoadKind.Path) continue;
                double len = 0;
                for (int k = 0; k + 1 < e.points.Count; k++)
                {
                    var p0 = e.points[k];
                    var p1 = e.points[k + 1];
                    double sl = Geo.Dist(p0, p1);
                    int n = Math.Max(1, (int)Math.Ceiling(sl / STEP));
                    for (int j = 0; j < n; j++)
                        if (Geo.Dist(Geo.Lerp(p0, p1, (j + 0.5) / n), model.center) <= model.townRadius) len += sl / n;
                }
                if (e.cls == RoadClass.Arterial) a += len;
                else if (e.cls == RoadClass.Collector) c += len;
                else l += len;
            }
            double all = Math.Max(1e-9, a + c + l);
            return (a / all, c / all, l / all);
        }

        static IEnumerable<string> AddFootpaths(TownModel m, GraphBuilder gb, byte[] wet, float[] stepCost, float[] slope, RoadTerrain terrainInfo, Vec2 C)
        {
            int w = m.width, h = m.height;
            var rock = Raster.RockMask(slope, m.@params);
            var ground = new byte[w * h];
            for (int i = 0; i < ground.Length; i++) ground[i] = wet[i] == 0 && rock[i] == 0 ? (byte)1 : (byte)0;
            var comp = new int[w * h];
            for (int i = 0; i < comp.Length; i++) comp[i] = -1;
            var comps = new List<List<int>>();
            for (int i = 0; i < ground.Length; i++)
            {
                if (ground[i] == 0 || comp[i] >= 0) continue;
                var cells = new List<int> { i };
                comp[i] = comps.Count;
                for (int head = 0; head < cells.Count; head++)
                {
                    int c = cells[head];
                    int cx = c % w;
                    int cy = (c - cx) / w;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = Grid.Index(nx, ny, w);
                        if (ground[j] != 0 && comp[j] < 0)
                        {
                            comp[j] = comps.Count;
                            cells.Add(j);
                        }
                    }
                }
                comps.Add(cells);
            }
            foreach (var cells in comps)
            {
                yield return "/footpaths";
                if (cells.Count < PATH_COMPONENT_MIN) continue;
                var raster = Raster.RoadRaster(m, RoadGraphOps.LiveEdges(gb));
                bool reached = false;
                foreach (int c in cells) if (raster.kind[c] != 0) { reached = true; break; }
                if (reached) continue;
                int centerCell = Grid.Index((int)Math.Floor(C.x), (int)Math.Floor(C.y), w);
                int start = cells[0];
                foreach (int c in cells) if (AStar.Octile(c, centerCell, w) < AStar.Octile(start, centerCell, w)) start = c;
                var path = AStar.FindPath(start, new GridSearch
                {
                    Width = w,
                    Height = h,
                    Step = (from, to) => AStar.Octile(from, to, w) * stepCost[to],
                    Goal = c => raster.kind[c] != 0,
                    Estimate = _ => 0,
                });
                if (path == null || path.Count < 3) continue;
                int last = path[path.Count - 1];
                var host = gb.edges[raster.edgeId[last]];
                var centers = new List<Vec2>(path.Count);
                foreach (int c in path) centers.Add(new Vec2((c % w) + 0.5, c / w + 0.5));
                var pts = Geo.SimplifyRDP(centers, 0.6, Raster.RoadSegmentOk(terrainInfo, m));
                var joint = Geo.ClosestOnPolyline(pts[pts.Count - 1], host.points).point;
                pts[pts.Count - 1] = joint;
                int end = RoadGraphOps.SplitEdgeAt(gb, host.id, joint, 0.75);
                int begin = RoadGraphOps.AddNode(gb, pts[0]);
                RoadGraphOps.AddEdge(gb, begin, end, RoadKind.Path, pts);
            }
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            var graph = model.roads;
            if (graph.nodes.Count > 0 && RoadGraphOps.ReachableNodes(graph, 0).Count != graph.nodes.Count)
                errors.Add("road graph is not a single connected component");
            errors.AddRange(CheckCrossings(graph));
            errors.AddRange(CheckDuplicates(graph));
            foreach (var c in CloseParallels(graph, MIN_APART))
            {
                errors.Add($"roads {c.a} and {c.b} run alongside each other {Numbers.ToFixed(c.apart, 1)} apart near {Numbers.ToFixed(c.at.x, 1)},{Numbers.ToFixed(c.at.y, 1)}");
                break;
            }
            errors.AddRange(RoadsStage.CheckRoadSlopes(model, RoadKind.Main));
            errors.AddRange(RoadsStage.CheckRoadSlopes(model, RoadKind.Street));
            errors.AddRange(RoadGraphOps.CheckBridges(model, Raster.WaterMask(model)));
            errors.AddRange(AirportStage.CheckRoadIn(model));
            return errors;
        }

        static IEnumerable<string> ConnectDeadEnds(
            GraphBuilder g,
            ISet<int> keep,
            double reach,
            Func<Vec2, Vec2, int[], int, Hit?> firstHit,
            Func<Vec2, Vec2, bool> landOk,
            Func<int, Vec2, bool> angleClash,
            Func<int, int, bool> joinable,
            Func<int, RoadEdge, bool> culDeSac,
            Func<Vec2, Vec2, int[], bool> roomy)
        {
            var inc = RoadGraphOps.Incidence(g);
            int seen = 0;
            foreach (var kv in inc)
            {
                if (++seen % DEAD_ENDS_A_STEP == 0) yield return "/dead-ends";
                int node = kv.Key;
                var list = kv.Value;
                if (list.Count != 1 || keep.Contains(node)) continue;
                var e = g.edges[list[0]];
                if (e.kind != RoadKind.Street || g.dead.Contains(e.id)) continue;
                var a = g.nodes[node].at;
                List<Vec2> along;
                if (e.from == node) along = e.points;
                else
                {
                    along = new List<Vec2>(e.points);
                    along.Reverse();
                }
                if (culDeSac(node, e)) continue;
                var dir = Geo.Scale(Heading(a, along), -1);
                // Straight on to the road ahead, or else round the corner to the nearer road to one side.
                var ahead = firstHit(a, Geo.Add(a, Geo.Scale(dir, reach)), new[] { node }, -1);
                var tries = new List<Hit>();
                if (ahead != null) tries.Add(ahead.Value);
                var sides = new List<Hit>();
                foreach (var side in new[] { new Vec2(-dir.y, dir.x), new Vec2(dir.y, -dir.x) })
                {
                    var hit = firstHit(a, Geo.Add(a, Geo.Scale(side, reach)), new[] { node }, -1);
                    if (hit != null) sides.Add(hit.Value);
                }
                Numbers.StableSort(sides, (x, y) => x.t - y.t);
                tries.AddRange(sides);
                bool joined = false;
                foreach (var hit in tries)
                {
                    if (hit.t * reach < MIN_SEGMENT || !landOk(a, hit.point) || !roomy(a, hit.point, new[] { node })) continue;
                    int n = RoadGraphOps.SplitEdgeAt(g, hit.edge, hit.point, SNAP);
                    if (!joinable(node, n)) continue;
                    RoadGraphOps.AddEdge(g, node, n, RoadKind.Street);
                    joined = true;
                    break;
                }
                if (joined) continue;
                int best = -1;
                double bestD = reach;
                foreach (var nd in g.nodes)
                {
                    if (nd.id == node || !inc.TryGetValue(nd.id, out var l) || l.Count == 0) continue;
                    var v = Geo.Sub(nd.at, a);
                    double d = Numbers.Hypot(v.x, v.y);
                    if (d < MIN_SEGMENT || d >= bestD) continue;
                    if (!OnGrid(Unit(v)) || Geo.Dot(Unit(v), dir) < Math.Cos((50 * Math.PI) / 180)) continue;
                    best = nd.id;
                    bestD = d;
                }
                if (best < 0 || !joinable(node, best) || !roomy(a, g.nodes[best].at, new[] { node, best })) continue;
                var dd = Unit(Geo.Sub(g.nodes[best].at, a));
                if (angleClash(best, Geo.Scale(dd, -1))) continue;
                RoadGraphOps.AddEdge(g, node, best, RoadKind.Street);
            }
        }

        static IEnumerable<string> DropDuplicateStreets(GraphBuilder g)
        {
            for (int pass = 0; pass < MAX_DUPLICATE_PASSES; pass++)
            {
                var copy = new GraphBuilder { nodes = g.nodes, edges = g.edges, dead = new HashSet<int>(g.dead) };
                var graph = RoadGraphOps.Compact(copy);
                var dup = FindDuplicate(graph, true);
                if (dup == null) yield break;
                RoadEdge victim = null;
                foreach (var e in RoadGraphOps.LiveEdges(g))
                {
                    if (e.kind != RoadKind.Main && SameLine(e.points, dup.Value.points))
                    {
                        victim = e;
                        break;
                    }
                }
                if (victim == null) yield break;
                g.dead.Add(victim.id);
                yield return "/dedupe";
            }
        }

        static bool SameLine(List<Vec2> a, List<Vec2> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!(Geo.Dist(a[i], b[i]) < 1e-9)) return false;
            return true;
        }

        static RoadGraph KeepConnected(RoadGraph graph, Vec2 center)
        {
            if (graph.nodes.Count == 0) return graph;
            int hub = 0;
            for (int i = 0; i < graph.nodes.Count; i++)
                if (Geo.Dist(graph.nodes[i].at, center) < Geo.Dist(graph.nodes[hub].at, center)) hub = i;
            var reached = RoadGraphOps.ReachableNodes(graph, hub);
            if (reached.Count == graph.nodes.Count) return graph;
            var g = new GraphBuilder();
            foreach (var n in graph.nodes) g.nodes.Add(new RoadNode { id = n.id, at = n.at });
            foreach (var e in graph.edges) g.edges.Add(e.Clone());
            foreach (var e in graph.edges) if (!reached.Contains(e.from)) g.dead.Add(e.id);
            return RoadGraphOps.Compact(g);
        }

        /// <summary>Edges may only meet at shared nodes.</summary>
        public static List<string> CheckCrossings(RoadGraph graph)
        {
            var edges = graph.edges;
            for (int i = 0; i < edges.Count; i++)
            {
                for (int j = i; j < edges.Count; j++)
                {
                    var A = edges[i];
                    var B = edges[j];
                    if (!BoxesNear(A.points, B.points, 0)) continue;
                    for (int s = 0; s + 1 < A.points.Count; s++)
                    {
                        for (int t = i == j ? s + 2 : 0; t + 1 < B.points.Count; t++)
                        {
                            var hit = Geo.SegmentIntersection(A.points[s], A.points[s + 1], B.points[t], B.points[t + 1]);
                            if (hit == null) continue;
                            var x = hit.Value;
                            bool sharedHere = false;
                            foreach (int n in new[] { A.from, A.to })
                                if ((n == B.from || n == B.to) && Geo.Dist(graph.nodes[n].at, x.point) < 1e-6) sharedHere = true;
                            if (sharedHere) continue;
                            if (i == j && A.from == A.to && Geo.Dist(graph.nodes[A.from].at, x.point) < 1e-6) continue;
                            return new List<string> { $"roads {A.id} and {B.id} cross at {Numbers.ToFixed(x.point.x, 1)},{Numbers.ToFixed(x.point.y, 1)} without a junction" };
                        }
                    }
                }
            }
            return new List<string>();
        }

        public static List<string> CheckDuplicates(RoadGraph graph)
        {
            var dup = FindDuplicate(graph);
            return dup != null
                ? new List<string> { $"roads {dup.Value.a} and {dup.Value.b} run alongside each other near {Numbers.ToFixed(dup.Value.at.x, 1)},{Numbers.ToFixed(dup.Value.at.y, 1)}" }
                : new List<string>();
        }

        struct Duplicate
        {
            public int a, b;
            public Vec2 at;
            public List<Vec2> points;
        }

        static Duplicate? FindDuplicate(RoadGraph graph, bool droppable = false)
        {
            var edges = graph.edges;
            // Asked of every half tile of every road: the nodes by cell, not all of them each time.
            var nodes = new PointGrid(JUNCTION_CLEARANCE);
            foreach (var n in graph.nodes) nodes.Add(n.at);
            bool NearNode(Vec2 pt) => nodes.AnyWithin(pt, JUNCTION_CLEARANCE);
            // And each road's box worked out once, not once for every other road.
            var boxes = new Bounds2[edges.Count];
            for (int i = 0; i < edges.Count; i++) boxes[i] = Geo.PolygonBounds(edges[i].points);
            for (int i = 0; i < edges.Count; i++)
            {
                var A = edges[i];
                var samples = new List<Vec2>();
                foreach (var pt in Geo.SamplePolyline(A.points, 0.5)) if (!NearNode(pt)) samples.Add(pt);
                if (samples.Count == 0) continue;
                for (int j = 0; j < edges.Count; j++)
                {
                    var B = edges[j];
                    if (A.id == B.id || !BoxesNear(boxes[i], boxes[j], DUPLICATE_DISTANCE)) continue;
                    if (droppable && A.kind == RoadKind.Main && B.kind == RoadKind.Main) continue;
                    int run = 0;
                    foreach (var pt in samples)
                    {
                        run = Geo.DistToPolyline(pt, B.points) < DUPLICATE_DISTANCE ? run + 1 : 0;
                        if (run >= 4)
                        {
                            var street = A.kind == RoadKind.Street ? A : B.kind == RoadKind.Street ? B : A.kind == RoadKind.Path ? A : B;
                            return new Duplicate { a = A.id, b = B.id, at = pt, points = street.points };
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>A street running alongside another road (not two main roads), nearer than `within` tiles centre to centre.</summary>
        public struct Alongside
        {
            public int a, b;
            public Vec2 at;
            public double apart;
        }

        /// <summary>
        /// Streets that run alongside another street or road, nearly parallel (within 15°) for at
        /// least two tiles, closer than `within` (centre to centre): the land between is too thin to build on.
        /// (Not where a road kinks right by a junction the two share.)
        /// </summary>
        public static List<Alongside> CloseParallels(RoadGraph graph, double within)
        {
            var o = new List<Alongside>();
            var edges = graph.edges;
            var boxes = new Bounds2[edges.Count];
            for (int i = 0; i < edges.Count; i++) boxes[i] = Geo.PolygonBounds(edges[i].points);
            const double sin = 0.25881904510252074; // sin 15°
            for (int i = 0; i < edges.Count; i++)
            {
                var A = edges[i];
                if (A.kind == RoadKind.Path) continue;
                for (int j = i + 1; j < edges.Count; j++)
                {
                    var B = edges[j];
                    if (B.kind == RoadKind.Path || (A.kind == RoadKind.Main && B.kind == RoadKind.Main) || !BoxesNear(boxes[i], boxes[j], within)) continue;
                    bool found = false;
                    for (int s = 0; s + 1 < A.points.Count && !found; s++)
                    {
                        var a0 = A.points[s];
                        var a1 = A.points[s + 1];
                        double la = Geo.Dist(a0, a1);
                        if (la < 1e-9) continue;
                        var u = new Vec2((a1.x - a0.x) / la, (a1.y - a0.y) / la);
                        for (int t = 0; t + 1 < B.points.Count && !found; t++)
                        {
                            var b0 = B.points[t];
                            var b1 = B.points[t + 1];
                            double lb = Geo.Dist(b0, b1);
                            if (lb < 1e-9) continue;
                            var v = new Vec2((b1.x - b0.x) / lb, (b1.y - b0.y) / lb);
                            if (Math.Abs(u.x * v.y - u.y * v.x) > sin) continue;
                            double p0 = Geo.Dot(Geo.Sub(b0, a0), u), p1 = Geo.Dot(Geo.Sub(b1, a0), u);
                            double lo = Math.Max(0, Math.Min(p0, p1)), hi = Math.Min(la, Math.Max(p0, p1));
                            if (hi - lo < 2) continue;
                            double mid = (lo + hi) / 2;
                            // Where B is at the middle of the stretch they share.
                            double k = Math.Abs(p1 - p0) < 1e-9 ? 0 : (mid - p0) / (p1 - p0);
                            var bm = Geo.Lerp(b0, b1, Math.Max(0, Math.Min(1, k)));
                            double apart = Math.Abs(Geo.Cross(u, Geo.Sub(bm, a0)));
                            if (apart <= 0.5 || apart >= within) continue;
                            // Not a road's own kink right at a junction the two share.
                            bool atJunction = false;
                            foreach (int n in new[] { A.from, A.to })
                                if ((n == B.from || n == B.to) && Geo.Dist(graph.nodes[n].at, bm) < 3) atJunction = true;
                            if (atJunction) continue;
                            o.Add(new Alongside { a = A.id, b = B.id, at = bm, apart = apart });
                            found = true;
                        }
                    }
                }
            }
            return o;
        }

        static bool BoxesNear(List<Vec2> a, List<Vec2> b, double pad) => BoxesNear(Geo.PolygonBounds(a), Geo.PolygonBounds(b), pad);

        static bool BoxesNear(Bounds2 A, Bounds2 B, double pad)
        {
            return !(B.x0 > A.x1 + pad || B.x1 < A.x0 - pad || B.y0 > A.y1 + pad || B.y1 < A.y0 - pad);
        }

        /// <summary>Direction a road leaves `at` along `pts`, judged a couple of tiles out.</summary>
        static Vec2 Heading(Vec2 at, List<Vec2> pts)
        {
            foreach (var p in pts) if (Geo.Dist(p, at) >= HEADING_REACH) return Unit(Geo.Sub(p, at));
            return Unit(Geo.Sub(pts[pts.Count - 1], at));
        }

        /// <summary>Nearest of the four grid directions to an angle.</summary>
        static double SnapToGrid(double angle) => Numbers.Round(angle / (Math.PI / 2)) * (Math.PI / 2);

        static bool OnGrid(Vec2 d)
        {
            double a = DetMath.Atan2(d.y, d.x);
            return Math.Abs(a - SnapToGrid(a)) <= GRID_TOLERANCE;
        }

        static Vec2 Unit(Vec2 v)
        {
            double len = Numbers.Hypot(v.x, v.y);
            if (len == 0 || double.IsNaN(len)) len = 1;
            return new Vec2(v.x / len, v.y / len);
        }

        static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));
    }
}

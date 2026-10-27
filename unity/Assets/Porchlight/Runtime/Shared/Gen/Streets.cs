// Stage 5: streets. Side streets run along the tile grid, sprouting at right
// angles from points along the main roads and growing in short straight
// segments (after Parish & Müller's local constraints): a segment that would
// cross a road stops there and joins it, one that ends just short of a node or
// road carries straight on to meet it, and one that hits water, steep ground,
// the town edge or crowds a parallel road is dropped. Afterwards dead ends get
// short connectors where one fits, and land the roads never reach gets a
// footpath back to the network.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class StreetsStage : IStage
    {
        static readonly double MIN_ANGLE = (35 * Math.PI) / 180;
        static readonly double GRID_TOLERANCE = (8 * Math.PI) / 180;
        const double MIN_SEGMENT = 1.5;
        const int MAX_PROPOSALS = 6000;
        const int MAX_TRIES = 3;
        const double JUNCTION_CLEARANCE = 3;
        const double HEADING_REACH = 2;
        const int PATH_COMPONENT_MIN = 150;
        const double DUPLICATE_DISTANCE = 1.5;
        const int MAX_DUPLICATE_PASSES = 200;

        struct Proposal
        {
            public int from;
            public double angle;
            public int depth;
            public double since;
            public int tries;
        }

        struct Hit
        {
            public int edge;
            public Vec2 point;
            public double t;
        }

        struct Grown
        {
            public int node;
            public bool stop;
            public double len;
        }

        public string Name => "streets";

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var C = model.center;
            double R = model.townRadius;
            var terrainInfo = Raster.RoadTerrain(model);
            var water = terrainInfo.water;
            var slope = terrainInfo.slope;
            var cost = terrainInfo.cost;
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
                return water[i] == 0 && slope[i] <= p.maxRoadSlope;
            }
            bool CellOk(Vec2 pt) => TileOk((int)Math.Floor(pt.x), (int)Math.Floor(pt.y));
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
            bool ClearOfOthers(Vec2[] pts, int[] skipNodes, double minDist)
            {
                foreach (var e in RoadGraphOps.LiveEdges(g))
                {
                    if (Array.IndexOf(skipNodes, e.from) >= 0 || Array.IndexOf(skipNodes, e.to) >= 0) continue;
                    foreach (var pt in pts) if (!(Geo.DistToPolyline(pt, e.points) >= minDist)) return false;
                }
                return true;
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

            Grown? Grow(Proposal prop)
            {
                var a = g.nodes[prop.from].at;
                var dir = new Vec2(Math.Cos(prop.angle), Math.Sin(prop.angle));
                if (AngleClash(prop.from, dir)) return null;
                double len = p.streetSegment * rng.Range(0.85, 1.15) * (1 - 0.25 * prop.tries);
                var b = Geo.Add(a, Geo.Scale(dir, len));
                double snap = len * 0.5;

                var hit = FirstHit(a, b, new[] { prop.from });
                if (hit != null)
                {
                    if (hit.Value.t * len < MIN_SEGMENT || !LandOk(a, hit.Value.point)) return null;
                    int n = RoadGraphOps.SplitEdgeAt(g, hit.Value.edge, hit.Value.point, 0.3);
                    if (!Joinable(prop.from, n)) return null;
                    RoadGraphOps.AddEdge(g, prop.from, n, RoadKind.Street);
                    return new Grown { node = n, stop = true, len = hit.Value.t * len };
                }
                if (Geo.Dist(b, C) > R) return null;

                var neighbours = new List<int>();
                foreach (var e in RoadGraphOps.LiveEdges(g))
                    if (e.from == prop.from || e.to == prop.from) neighbours.Add(e.from == prop.from ? e.to : e.from);
                int near = -1;
                double nearD = snap;
                foreach (var nd in g.nodes)
                {
                    if (nd.id == prop.from || neighbours.Contains(nd.id)) continue;
                    double d = Geo.Dist(nd.at, b);
                    if (d < nearD && IncidentDirs(nd.id).Count > 0 && OnGrid(Unit(Geo.Sub(nd.at, a))))
                    {
                        nearD = d;
                        near = nd.id;
                    }
                }
                if (near >= 0)
                {
                    var target = g.nodes[near].at;
                    var d = Unit(Geo.Sub(target, a));
                    if (Geo.Dist(a, target) < MIN_SEGMENT || AngleClash(prop.from, d) || AngleClash(near, Geo.Scale(d, -1))) return null;
                    if (FirstHit(a, target, new[] { prop.from, near }) != null || !LandOk(a, target)) return null;
                    RoadGraphOps.AddEdge(g, prop.from, near, RoadKind.Street);
                    return new Grown { node = near, stop = true, len = Geo.Dist(a, target) };
                }

                var ahead = FirstHit(a, Geo.Add(a, Geo.Scale(dir, len + snap)), new[] { prop.from });
                if (ahead != null)
                {
                    if (!LandOk(a, ahead.Value.point)) return null;
                    int n = RoadGraphOps.SplitEdgeAt(g, ahead.Value.edge, ahead.Value.point, 0.3);
                    if (!Joinable(prop.from, n)) return null;
                    RoadGraphOps.AddEdge(g, prop.from, n, RoadKind.Street);
                    return new Grown { node = n, stop = true, len = Geo.Dist(a, ahead.Value.point) };
                }

                if (!LandOk(a, b)) return null;
                double clearance = Math.Max(2.5, p.streetSpacing * 0.4);
                if (!ClearOfOthers(new[] { b, Geo.Lerp(a, b, 0.5) }, new[] { prop.from }, clearance) || Grazes(a, b, new[] { prop.from })) return null;
                int nn = RoadGraphOps.AddNode(g, b);
                RoadGraphOps.AddEdge(g, prop.from, nn, RoadKind.Street);
                return new Grown { node = nn, stop = false, len = len };
            }

            // Seeds: evenly spaced points along the main roads inside the town.
            var seeds = new List<(Vec2 at, Vec2 tangent)>();
            foreach (var e in RoadGraphOps.LiveEdges(g))
            {
                double total = Geo.PolylineLength(e.points);
                for (double s = p.streetSpacing * rng.Range(0.4, 0.8); s < total - 2; s += p.streetSpacing * rng.Range(0.7, 1.0))
                {
                    var seg = Geo.SubPolyline(e.points, s, Math.Min(total, s + 0.5));
                    if (seg.Count < 2) continue;
                    var at = seg[0];
                    if (Geo.Dist(at, C) > R || !CellOk(at)) continue;
                    bool crowded = false;
                    foreach (var n in g.nodes) if (Geo.Dist(n.at, at) < JUNCTION_CLEARANCE) crowded = true;
                    if (crowded) continue;
                    if (!rng.Chance(p.roadDensity * (0.6 + 0.4 * Falloff(at)))) continue;
                    seeds.Add((at, Unit(Geo.Sub(seg[seg.Count - 1], at))));
                }
            }

            var queue = new List<Proposal>();
            foreach (var seed in seeds)
            {
                RoadEdge host = null;
                foreach (var e in RoadGraphOps.LiveEdges(g))
                {
                    if (Geo.ClosestOnPolyline(seed.at, e.points).d < 1e-6)
                    {
                        host = e;
                        break;
                    }
                }
                if (host == null) continue;
                int node = RoadGraphOps.SplitEdgeAt(g, host.id, seed.at);
                double baseAngle = DetMath.Atan2(seed.tangent.y, seed.tangent.x);
                foreach (int side in new[] { 1, -1 })
                {
                    if (rng.Chance(0.9)) queue.Add(new Proposal { from = node, angle = SnapToGrid(baseAngle + (side * Math.PI) / 2), depth = 0, since = 0, tries = 0 });
                }
            }

            for (int q = 0; q < queue.Count && q < MAX_PROPOSALS; q++)
            {
                var prop = queue[q];
                var res = Grow(prop);
                if (res == null)
                {
                    if (prop.tries < MAX_TRIES)
                    {
                        var retry = prop;
                        retry.tries = prop.tries + 1;
                        queue.Add(retry);
                    }
                    continue;
                }
                var r = res.Value;
                if (r.stop) continue;
                var here = g.nodes[r.node].at;
                double f = Falloff(here);
                double since = prop.since + r.len;
                if (since >= p.streetSpacing)
                {
                    foreach (int side in new[] { 1, -1 })
                    {
                        if (rng.Chance(p.roadDensity * (0.4 + 0.6 * f)))
                        {
                            queue.Add(new Proposal { from = r.node, angle = SnapToGrid(prop.angle + (side * Math.PI) / 2), depth = prop.depth + 1, since = 0, tries = 0 });
                            since = 0;
                        }
                    }
                }
                if (rng.Chance(0.7 + 0.3 * f))
                    queue.Add(new Proposal { from = r.node, angle = prop.angle, depth = prop.depth, since = since, tries = 0 });
            }

            ConnectDeadEnds(g, keep, p.streetSegment * 1.8, FirstHit, LandOk, AngleClash, Joinable);
            AddFootpaths(model, g, water, cost, slope, terrainInfo, C);

            RoadGraphOps.Planarize(g);
            RoadGraphOps.MergePassThrough(g, keep);
            DropDuplicateStreets(g);
            var roads = KeepConnected(RoadGraphOps.Compact(g), model.center);
            var next = model.With();
            next.roads = roads;
            next.bridges = RoadGraphOps.ComputeBridges(next, Raster.WaterMask(next));
            return next;
        }

        static void AddFootpaths(TownModel m, GraphBuilder gb, byte[] wet, float[] stepCost, float[] slope, RoadTerrain terrainInfo, Vec2 C)
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
            errors.AddRange(RoadsStage.CheckRoadSlopes(model, RoadKind.Main));
            errors.AddRange(RoadsStage.CheckRoadSlopes(model, RoadKind.Street));
            errors.AddRange(RoadGraphOps.CheckBridges(model, Raster.WaterMask(model)));
            return errors;
        }

        static void ConnectDeadEnds(
            GraphBuilder g,
            ISet<int> keep,
            double reach,
            Func<Vec2, Vec2, int[], int, Hit?> firstHit,
            Func<Vec2, Vec2, bool> landOk,
            Func<int, Vec2, bool> angleClash,
            Func<int, int, bool> joinable)
        {
            var inc = RoadGraphOps.Incidence(g);
            foreach (var kv in inc)
            {
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
                var dir = Geo.Scale(Heading(a, along), -1);
                var hit = firstHit(a, Geo.Add(a, Geo.Scale(dir, reach)), new[] { node }, -1);
                if (hit != null && hit.Value.t * reach >= MIN_SEGMENT && landOk(a, hit.Value.point))
                {
                    int n = RoadGraphOps.SplitEdgeAt(g, hit.Value.edge, hit.Value.point, 0.75);
                    if (joinable(node, n)) RoadGraphOps.AddEdge(g, node, n, RoadKind.Street);
                    continue;
                }
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
                if (best < 0 || !joinable(node, best)) continue;
                var dd = Unit(Geo.Sub(g.nodes[best].at, a));
                if (angleClash(best, Geo.Scale(dd, -1))) continue;
                RoadGraphOps.AddEdge(g, node, best, RoadKind.Street);
            }
        }

        static void DropDuplicateStreets(GraphBuilder g)
        {
            for (int pass = 0; pass < MAX_DUPLICATE_PASSES; pass++)
            {
                var copy = new GraphBuilder { nodes = g.nodes, edges = g.edges, dead = new HashSet<int>(g.dead) };
                var graph = RoadGraphOps.Compact(copy);
                var dup = FindDuplicate(graph, true);
                if (dup == null) return;
                RoadEdge victim = null;
                foreach (var e in RoadGraphOps.LiveEdges(g))
                {
                    if (e.kind != RoadKind.Main && SameLine(e.points, dup.Value.points))
                    {
                        victim = e;
                        break;
                    }
                }
                if (victim == null) return;
                g.dead.Add(victim.id);
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
            bool NearNode(Vec2 pt)
            {
                foreach (var n in graph.nodes) if (Geo.Dist(n.at, pt) < JUNCTION_CLEARANCE) return true;
                return false;
            }
            foreach (var A in edges)
            {
                var samples = new List<Vec2>();
                foreach (var pt in Geo.SamplePolyline(A.points, 0.5)) if (!NearNode(pt)) samples.Add(pt);
                if (samples.Count == 0) continue;
                foreach (var B in edges)
                {
                    if (A.id == B.id || !BoxesNear(A.points, B.points, DUPLICATE_DISTANCE)) continue;
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

        static bool BoxesNear(List<Vec2> a, List<Vec2> b, double pad)
        {
            var A = Geo.PolygonBounds(a);
            var B = Geo.PolygonBounds(b);
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

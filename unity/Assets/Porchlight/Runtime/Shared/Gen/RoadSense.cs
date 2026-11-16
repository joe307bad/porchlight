// Stage 6b: RoadSense. Between the streets and the blocks: first (if the town
// asks for it, GenParams.roadSense) it repairs the road network the earlier
// stages left between them, then it says what every road is for and who goes
// first at every junction.
//
// The repairs, looped until nothing changes (at most MAX_PASSES):
//   - two junctions joined by a road shorter than JOIN (MAIN_JOIN on a main) become one,
//     at the more important one's spot, unless that would make more than MAX_LEGS legs,
//     two legs closer than MIN_LEG_ANGLE, a road over water or steep ground, or a crossing;
//   - a dead-end street shorter than STUB off a junction is dropped;
//   - roads meeting end to end (of a kind) become one road again.
// The gates, the centre, the airport and zoo road ends never move. If the repaired
// network fails any of the street checks it's thrown away and the town keeps the
// network it had: the repair only ever makes a town better or leaves it alone.
//
// Then, with or without the repair (it moves nothing), the classing:
//   - a main on the way from a gate to the centre is an arterial, the other mains collectors;
//   - a street is a collector if it's among the fifth of streets most through-traffic
//     takes (edge betweenness, from a sample of starts), a cul-de-sac if it only leads
//     to dead ends, otherwise local;
//   - each junction gets its control (JunctionControl) from its legs' classes: a signal
//     where arterials cross busily enough, priority to the major road elsewhere (a stop
//     sign for a minor leg that meets at a sharp angle, jogs or comes off a bridge), and
//     where every leg is alike an all-way stop or right-before-left (one or the other,
//     town by town).
// Pure, and needs no randomness (what it would draw comes from the seed's hash).

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class RoadSenseStage : IStage
    {
        /// <summary>Junctions closer than this along a street become one (tiles: SUMO's 10 m junction join at 4 m a tile).</summary>
        public const double JOIN = 2.5;
        /// <summary>The same along a main road.</summary>
        public const double MAIN_JOIN = 3.5;
        /// <summary>A dead-end street off a junction shorter than this is dropped.</summary>
        public const double STUB = 2;
        public const int MAX_LEGS = 4;
        /// <summary>A join may not leave two legs closer than this (AASHTO's 60°: the repair never makes a sharp junction).</summary>
        public static readonly double MIN_LEG_ANGLE = 60 * Math.PI / 180;
        /// <summary>A minor road meeting the major one sharper than this gets a stop sign.</summary>
        public static readonly double SHARP = 75 * Math.PI / 180;
        /// <summary>Opposite side roads closer than this along a street (a jog), or MAIN_JOG along a main, stop.</summary>
        public const double JOG = 9.5, MAIN_JOG = 15;
        /// <summary>A junction this close to the end of a bridge has its minor legs stop.</summary>
        public const double NEAR_BRIDGE = 2;
        const int MAX_PASSES = 8;
        const int MAX_MERGES = 2000;

        /// <summary>Speeds by class (tiles a second): 50, 40, 30 and 20 km/h at 4 m a tile.</summary>
        public static double SpeedOf(RoadClass c) => c switch
        {
            RoadClass.Arterial => Units.Kmh(50),
            RoadClass.Collector => Units.Kmh(40),
            RoadClass.Local => Units.Kmh(30),
            _ => Units.Kmh(20),
        };

        public static int PriorityOf(RoadClass c, RoadKind k) => k == RoadKind.Path ? -1 : c switch
        {
            RoadClass.Arterial => 3,
            RoadClass.Collector => 2,
            RoadClass.Local => 1,
            _ => 0,
        };

        /// <summary>A signal needs this much traffic in: Σ lanes × speed (m/s) of its legs (SUMO's test, a lower bar: a tuning guess).</summary>
        public const double SIGNAL_SCORE = 45;
        /// <summary>Signals no closer together than this (tiles: SUMO's 20 m tls.join).</summary>
        public const double SIGNAL_SPACING = 5;
        /// <summary>About one signal for each this many tiles of arterial.</summary>
        public const double ARTERIAL_PER_SIGNAL = 60;
        /// <summary>The share of streets, by through-traffic, that are collectors.</summary>
        const double COLLECTOR_SHARE = 0.2;
        const int BETWEENNESS_SOURCES = 48;
        const double HEADING_REACH = 2;

        public string Name => "roadsense";

        public TownModel Run(TownModel model, Rng rng)
        {
            var next = model.With();
            if (model.@params.roadSense >= 0.5 && model.roads.nodes.Count > 0)
            {
                var repaired = Repair(model);
                if (repaired != null)
                {
                    next.roads = repaired;
                    next.bridges = RoadGraphOps.ComputeBridges(next, Raster.WaterMask(next));
                }
            }
            next.roads = Classify(next);
            return next;
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            var graph = model.roads;
            if (!graph.classified) errors.Add("roads aren't classed");
            if (graph.nodes.Count > 0 && RoadGraphOps.ReachableNodes(graph, 0).Count != graph.nodes.Count)
                errors.Add("road graph is not a single connected component");
            errors.AddRange(StreetsStage.CheckCrossings(graph));
            errors.AddRange(StreetsStage.CheckDuplicates(graph));
            errors.AddRange(RoadsStage.CheckRoadSlopes(model, RoadKind.Main));
            errors.AddRange(RoadsStage.CheckRoadSlopes(model, RoadKind.Street));
            errors.AddRange(RoadGraphOps.CheckBridges(model, Raster.WaterMask(model)));
            errors.AddRange(AirportStage.CheckRoadIn(model));
            errors.AddRange(CheckClasses(graph));
            return errors;
        }

        /// <summary>Every road has a speed and rank; every junction a control that fits how many legs it has; give-way legs are its own.</summary>
        public static List<string> CheckClasses(RoadGraph graph)
        {
            var errors = new List<string>();
            var legs = LegCounts(graph);
            foreach (var e in graph.edges)
                if (e.kind != RoadKind.Path && !(e.speed > 0)) { errors.Add($"road {e.id} has no speed"); break; }
            foreach (var n in graph.nodes)
            {
                int k = legs[n.id];
                bool ok = k == 0 || k == 2 ? n.control == JunctionControl.None
                    : k == 1 ? n.control == JunctionControl.DeadEnd || n.control == JunctionControl.Gate
                    : n.control != JunctionControl.None && n.control != JunctionControl.DeadEnd && n.control != JunctionControl.Gate;
                if (!ok) { errors.Add($"junction {n.id} with {k} legs is {n.control}"); break; }
                if (n.minor != null)
                    foreach (int e in n.minor)
                        if (e < 0 || e >= graph.edges.Count || (graph.edges[e].from != n.id && graph.edges[e].to != n.id))
                        {
                            errors.Add($"junction {n.id} gives way to a road that isn't one of its own");
                            break;
                        }
            }
            return errors;
        }

        // ---------------------------------------------------------------- repair

        /// <summary>The repaired network, or null if it needed nothing or the repair didn't pass the street checks.</summary>
        public static RoadGraph Repair(TownModel model)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var terrain = Raster.RoadTerrain(model);
            var airport = Airfield.Mask(model.airport, w, h);
            var zoo = Menagerie.Mask(model.zoo, w, h, Menagerie.ROAD_CLEAR);
            bool LandOk(Vec2 a, Vec2 b)
            {
                foreach (var (x, y) in Grid.CellsOnSegment(a.x, a.y, b.x, b.y))
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) return false;
                    int i = y * w + x;
                    if (terrain.water[i] != 0 || airport[i] != 0 || zoo[i] != 0 || terrain.slope[i] > p.maxRoadSlope) return false;
                }
                return true;
            }
            var g = RoadGraphOps.BuilderFrom(model.roads);
            var keep = new HashSet<int>();
            var pinned = new List<Vec2> { model.center };
            pinned.AddRange(model.gates);
            if (model.airport != null) pinned.Add(Airfield.Entrance(model.airport));
            if (model.zoo != null) pinned.Add(Menagerie.RoadEnd(model.zoo));
            foreach (var n in g.nodes)
                foreach (var q in pinned)
                    if (Geo.Dist(q, n.at) < 0.01) keep.Add(n.id);
            int hub = RoadGraphOps.NearestNode(model.roads, model.center);
            if (hub >= 0) keep.Add(hub);

            int merges = 0;
            bool any = false;
            for (int pass = 0; pass < MAX_PASSES; pass++)
            {
                bool changed = false;
                // Join junctions too close together, nearest pair first, one at a time.
                for (int guard = 0; guard < 400 && merges < MAX_MERGES; guard++)
                {
                    var inc = Incidence(g);
                    var shorts = new List<(double len, int id)>();
                    foreach (var e in RoadGraphOps.LiveEdges(g))
                    {
                        if (e.from == e.to || e.kind == RoadKind.Path) continue;
                        if (Degree(inc, e.from) < 3 || Degree(inc, e.to) < 3) continue;
                        double len = Geo.PolylineLength(e.points);
                        if (len < (e.kind == RoadKind.Main ? MAIN_JOIN : JOIN)) shorts.Add((len, e.id));
                    }
                    shorts.Sort((a, b) => a.len != b.len ? a.len.CompareTo(b.len) : a.id.CompareTo(b.id));
                    bool merged = false;
                    foreach (var (_, id) in shorts)
                    {
                        if (TryJoin(g, inc, keep, id, LandOk))
                        {
                            merged = true;
                            merges++;
                            break;
                        }
                    }
                    if (!merged) break;
                    changed = true;
                }
                // Drop the stubs: dead-end streets shorter than STUB off a junction.
                {
                    var inc = Incidence(g);
                    foreach (var e in RoadGraphOps.LiveEdges(g))
                    {
                        if (e.kind != RoadKind.Street || e.from == e.to) continue;
                        int df = Degree(inc, e.from), dt = Degree(inc, e.to);
                        int end = df == 1 ? e.from : dt == 1 ? e.to : -1;
                        int other = end == e.from ? e.to : e.from;
                        if (end < 0 || keep.Contains(end) || Degree(inc, other) < 3) continue;
                        if (Geo.PolylineLength(e.points) >= STUB) continue;
                        g.dead.Add(e.id);
                        changed = true;
                    }
                }
                if (!changed) break;
                any = true;
                RoadGraphOps.MergePassThrough(g, keep);
            }
            if (!any) return null;
            RoadGraphOps.MergePassThrough(g, keep);
            var graph = RoadGraphOps.Compact(g);
            // Only a network that passes everything the streets had to pass.
            if (graph.nodes.Count == 0 || RoadGraphOps.ReachableNodes(graph, 0).Count != graph.nodes.Count) return null;
            if (StreetsStage.CheckCrossings(graph).Count > 0) return null;
            if (StreetsStage.CheckDuplicates(graph).Count > 0) return null;
            var trial = model.With();
            trial.roads = graph;
            trial.bridges = RoadGraphOps.ComputeBridges(trial, Raster.WaterMask(trial));
            if (RoadsStage.CheckRoadSlopes(trial, RoadKind.Main).Count > 0 || RoadsStage.CheckRoadSlopes(trial, RoadKind.Street).Count > 0) return null;
            if (RoadGraphOps.CheckBridges(trial, Raster.WaterMask(trial)).Count > 0 || AirportStage.CheckRoadIn(trial).Count > 0) return null;
            return graph;
        }

        static Dictionary<int, List<int>> Incidence(GraphBuilder g) => RoadGraphOps.Incidence(g);

        static int Degree(Dictionary<int, List<int>> inc, int node) => inc.TryGetValue(node, out var l) ? l.Count : 0;

        /// <summary>Make the two ends of edge `id` one junction, if that leaves good roads; true if it did.</summary>
        static bool TryJoin(GraphBuilder g, Dictionary<int, List<int>> inc, HashSet<int> keep, int id, Func<Vec2, Vec2, bool> landOk)
        {
            var e = g.edges[id];
            int u = e.from, v = e.to;
            if (keep.Contains(u) && keep.Contains(v)) return false;
            int Rank(int n)
            {
                int r = 0;
                foreach (int k in inc[n]) if (g.edges[k].kind == RoadKind.Main) r++;
                return r;
            }
            int stay, go;
            if (keep.Contains(u)) { stay = u; go = v; }
            else if (keep.Contains(v)) { stay = v; go = u; }
            else
            {
                int ru = Rank(u), rv = Rank(v);
                bool uFirst = ru != rv ? ru > rv : inc[u].Count != inc[v].Count ? inc[u].Count > inc[v].Count : u < v;
                stay = uFirst ? u : v;
                go = uFirst ? v : u;
            }
            if (inc[stay].Count + inc[go].Count - 2 > MAX_LEGS) return false;
            var T = g.nodes[stay].at;
            // Where each of the other node's roads would run now: from T instead.
            var neighbours = new HashSet<int>();
            foreach (int k in inc[stay])
            {
                if (k == id) continue;
                var f = g.edges[k];
                neighbours.Add(f.from == stay ? f.to : f.from);
            }
            var moved = new List<(RoadEdge old, List<Vec2> pts, int from, int to)>();
            foreach (int k in inc[go])
            {
                if (k == id) continue;
                var f = g.edges[k];
                if (f.from == f.to) return false;
                int far = f.from == go ? f.to : f.from;
                if (far == stay || neighbours.Contains(far)) return false;
                var pts = new List<Vec2>(f.points);
                int from = f.from, to = f.to;
                if (f.from == go) { pts[0] = T; from = stay; }
                else { pts[pts.Count - 1] = T; to = stay; }
                // The stretch that moved: from T to the road's next point.
                var a = f.from == go ? pts[0] : pts[pts.Count - 1];
                var b = f.from == go ? pts[1] : pts[pts.Count - 2];
                if (Geo.Dist(a, b) < 0.5 || !landOk(a, b)) return false;
                moved.Add((f, pts, from, to));
            }
            // No two legs too close in angle where they'd meet.
            var dirs = new List<Vec2>();
            foreach (int k in inc[stay])
            {
                if (k == id) continue;
                dirs.Add(LegHeading(g.edges[k], stay, T));
            }
            foreach (var mv in moved) dirs.Add(Heading(T, mv.from == stay ? mv.pts : Reversed(mv.pts)));
            for (int i = 0; i < dirs.Count; i++)
                for (int j = i + 1; j < dirs.Count; j++)
                    if (DetMath.Acos(Clamp(Geo.Dot(dirs[i], dirs[j]), -1, 1)) < MIN_LEG_ANGLE) return false;
            // No moved stretch may cross another road.
            var skip = new HashSet<int> { id };
            foreach (int k in inc[go]) skip.Add(k);
            foreach (var mv in moved)
            {
                bool atStart = mv.from == stay;
                var a = atStart ? mv.pts[0] : mv.pts[mv.pts.Count - 1];
                var b = atStart ? mv.pts[1] : mv.pts[mv.pts.Count - 2];
                foreach (var o in RoadGraphOps.LiveEdges(g))
                {
                    if (skip.Contains(o.id)) continue;
                    if (Crosses(a, b, o.points, T, b)) return false;
                }
                foreach (var other in moved)
                {
                    if (ReferenceEquals(other.old, mv.old)) continue;
                    if (Crosses(a, b, other.pts, T, b)) return false;
                }
                // Nor its own road further on.
                var rest = atStart ? mv.pts.GetRange(2, mv.pts.Count - 2) : mv.pts.GetRange(0, mv.pts.Count - 2);
                if (rest.Count >= 2 && Crosses(a, b, rest, T, b)) return false;
            }
            g.dead.Add(id);
            foreach (var mv in moved)
            {
                g.dead.Add(mv.old.id);
                RoadGraphOps.AddEdge(g, mv.from, mv.to, mv.old.kind, mv.pts);
            }
            if (keep.Contains(go)) keep.Add(stay);
            return true;
        }

        /// <summary>Whether segment ab crosses polyline `pts` anywhere but at `shared` (and `alsoShared`).</summary>
        static bool Crosses(Vec2 a, Vec2 b, List<Vec2> pts, Vec2 shared, Vec2? alsoShared = null)
        {
            for (int s = 0; s + 1 < pts.Count; s++)
            {
                var hit = Geo.SegmentIntersection(a, b, pts[s], pts[s + 1]);
                if (hit == null) continue;
                var x = hit.Value.point;
                if (Geo.Dist(x, shared) < 1e-6) continue;
                if (alsoShared != null && Geo.Dist(x, alsoShared.Value) < 1e-6) continue;
                return true;
            }
            return false;
        }

        static List<Vec2> Reversed(List<Vec2> pts)
        {
            var r = new List<Vec2>(pts);
            r.Reverse();
            return r;
        }

        static Vec2 LegHeading(RoadEdge e, int node, Vec2 at) => Heading(at, e.from == node ? e.points : Reversed(e.points));

        /// <summary>Direction a road leaves `at` along `pts`, judged a couple of tiles out.</summary>
        public static Vec2 Heading(Vec2 at, IReadOnlyList<Vec2> pts)
        {
            foreach (var q in pts) if (Geo.Dist(q, at) >= HEADING_REACH) return Unit(Geo.Sub(q, at));
            return Unit(Geo.Sub(pts[pts.Count - 1], at));
        }

        static Vec2 Unit(Vec2 v)
        {
            double len = Numbers.Hypot(v.x, v.y);
            if (len == 0 || double.IsNaN(len)) return new Vec2(1, 0);
            return new Vec2(v.x / len, v.y / len);
        }

        static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));

        // ---------------------------------------------------------------- classing

        /// <summary>How many driveable legs (not footpaths) each node has; a loop counts twice.</summary>
        public static int[] LegCounts(RoadGraph graph)
        {
            var legs = new int[graph.nodes.Count];
            foreach (var e in graph.edges)
            {
                if (e.kind == RoadKind.Path) continue;
                legs[e.from]++;
                legs[e.to]++;
            }
            return legs;
        }

        /// <summary>A copy of the model's road graph, every road classed and every junction given its control.</summary>
        public static RoadGraph Classify(TownModel model)
        {
            var src = model.roads;
            var graph = new RoadGraph { classified = true };
            foreach (var n in src.nodes) graph.nodes.Add(new RoadNode { id = n.id, at = n.at });
            foreach (var e in src.edges) graph.edges.Add(new RoadEdge { id = e.id, from = e.from, to = e.to, kind = e.kind, points = e.points });
            int N = graph.nodes.Count;
            if (N == 0) return graph;
            var edges = graph.edges;
            var lengths = new double[edges.Count];
            for (int i = 0; i < edges.Count; i++) lengths[i] = Math.Max(1e-3, Geo.PolylineLength(edges[i].points));
            // Driveable adjacency, in edge order (no Dictionary order decides anything).
            var adj = new List<int>[N];
            for (int i = 0; i < N; i++) adj[i] = new List<int>();
            foreach (var e in edges)
            {
                if (e.kind == RoadKind.Path) continue;
                adj[e.from].Add(e.id);
                if (e.to != e.from) adj[e.to].Add(e.id);
            }
            var legs = LegCounts(graph);

            // Gates and the centre.
            var gates = new List<int>();
            foreach (var n in graph.nodes)
                foreach (var q in model.gates)
                    if (Geo.Dist(q, n.at) < 0.01 && !gates.Contains(n.id)) gates.Add(n.id);
            int hub = RoadGraphOps.NearestNode(graph, model.center);

            // Cul-de-sacs: peel dead ends (not gates, not the centre) back to the loops, off streets only.
            var culDeSac = new bool[edges.Count];
            {
                var deg = (int[])legs.Clone();
                var stack = new List<int>();
                for (int i = 0; i < N; i++) if (deg[i] == 1 && !gates.Contains(i) && i != hub) stack.Add(i);
                while (stack.Count > 0)
                {
                    int n = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    foreach (int k in adj[n])
                    {
                        if (culDeSac[k]) continue;
                        var e = edges[k];
                        if (e.kind != RoadKind.Street) continue;
                        culDeSac[k] = true;
                        int far = e.from == n ? e.to : e.from;
                        deg[n]--;
                        deg[far]--;
                        if (deg[far] == 1 && !gates.Contains(far) && far != hub) stack.Add(far);
                    }
                }
            }

            // Arterials: the mains on a shortest way from each gate to the centre (every main, with no gates: a highway).
            var arterial = new bool[edges.Count];
            if (gates.Count == 0)
            {
                foreach (var e in edges) if (e.kind == RoadKind.Main) arterial[e.id] = true;
            }
            else
            {
                var (_, via) = Dijkstra(N, adj, edges, lengths, hub, null);
                foreach (int gate in gates)
                {
                    int n = gate;
                    for (int guard = 0; guard < N && n != hub && via[n] >= 0; guard++)
                    {
                        var e = edges[via[n]];
                        if (e.kind == RoadKind.Main) arterial[e.id] = true;
                        n = e.from == n ? e.to : e.from;
                    }
                }
            }

            // Collectors among the streets: the fifth most through-traffic takes.
            var between = Betweenness(N, adj, edges, lengths);
            var streetScores = new List<(double b, int id)>();
            foreach (var e in edges) if (e.kind == RoadKind.Street && !culDeSac[e.id]) streetScores.Add((between[e.id], e.id));
            streetScores.Sort((a, b) => a.b != b.b ? b.b.CompareTo(a.b) : a.id.CompareTo(b.id));
            var collectorStreet = new bool[edges.Count];
            int topStreets = (int)Math.Floor(streetScores.Count * COLLECTOR_SHARE);
            for (int i = 0; i < topStreets; i++) if (streetScores[i].b > 0) collectorStreet[streetScores[i].id] = true;

            foreach (var e in edges)
            {
                e.cls = e.kind == RoadKind.Main ? (arterial[e.id] ? RoadClass.Arterial : RoadClass.Collector)
                    : e.kind == RoadKind.Path ? RoadClass.Local
                    : culDeSac[e.id] ? RoadClass.CulDeSac
                    : collectorStreet[e.id] ? RoadClass.Collector
                    : RoadClass.Local;
                e.speed = e.kind == RoadKind.Path ? 0 : SpeedOf(e.cls);
                e.priority = PriorityOf(e.cls, e.kind);
            }

            AssignControl(model, graph, adj, legs, gates);
            return graph;
        }

        /// <summary>Shortest-path distances from `start` and, for each node, the edge it was reached by.</summary>
        static (double[] dist, int[] via) Dijkstra(int N, List<int>[] adj, List<RoadEdge> edges, double[] lengths, int start, Action<int> settled)
        {
            var dist = new double[N];
            var via = new int[N];
            for (int i = 0; i < N; i++)
            {
                dist[i] = double.PositiveInfinity;
                via[i] = -1;
            }
            if (start < 0) return (dist, via);
            dist[start] = 0;
            var heap = new MinHeap();
            heap.Push(0, start);
            var done = new bool[N];
            while (heap.Count > 0)
            {
                var (d, n) = heap.Pop();
                if (done[n]) continue;
                done[n] = true;
                settled?.Invoke(n);
                foreach (int k in adj[n])
                {
                    var e = edges[k];
                    int m = e.from == n ? e.to : e.from;
                    double nd = d + lengths[k];
                    if (nd < dist[m] - 1e-9 || (Math.Abs(nd - dist[m]) <= 1e-9 && via[m] >= 0 && k < via[m]))
                    {
                        dist[m] = nd;
                        via[m] = k;
                        heap.Push(nd, m);
                    }
                }
            }
            return (dist, via);
        }

        /// <summary>Brandes' edge betweenness (weighted), from an even sample of starting nodes.</summary>
        static double[] Betweenness(int N, List<int>[] adj, List<RoadEdge> edges, double[] lengths)
        {
            var score = new double[edges.Count];
            int step = Math.Max(1, N / BETWEENNESS_SOURCES);
            var sigma = new double[N];
            var delta = new double[N];
            var dist = new double[N];
            var preds = new List<int>[N];
            for (int i = 0; i < N; i++) preds[i] = new List<int>();
            for (int s = 0; s < N; s += step)
            {
                if (adj[s].Count == 0) continue;
                for (int i = 0; i < N; i++)
                {
                    sigma[i] = 0;
                    delta[i] = 0;
                    dist[i] = double.PositiveInfinity;
                    preds[i].Clear();
                }
                sigma[s] = 1;
                dist[s] = 0;
                var order = new List<int>();
                var done = new bool[N];
                var heap = new MinHeap();
                heap.Push(0, s);
                while (heap.Count > 0)
                {
                    var (d, n) = heap.Pop();
                    if (done[n]) continue;
                    done[n] = true;
                    order.Add(n);
                    foreach (int k in adj[n])
                    {
                        var e = edges[k];
                        int m = e.from == n ? e.to : e.from;
                        if (m == n) continue;
                        double nd = d + lengths[k];
                        if (nd < dist[m] - 1e-9)
                        {
                            dist[m] = nd;
                            sigma[m] = sigma[n];
                            preds[m].Clear();
                            preds[m].Add(k);
                            heap.Push(nd, m);
                        }
                        else if (Math.Abs(nd - dist[m]) <= 1e-9 && !done[m])
                        {
                            sigma[m] += sigma[n];
                            preds[m].Add(k);
                        }
                    }
                }
                for (int i = order.Count - 1; i >= 0; i--)
                {
                    int m = order[i];
                    foreach (int k in preds[m])
                    {
                        var e = edges[k];
                        int n = e.from == m ? e.to : e.from;
                        double c = sigma[m] > 0 ? sigma[n] / sigma[m] * (1 + delta[m]) : 0;
                        score[k] += c;
                        delta[n] += c;
                    }
                }
            }
            return score;
        }

        /// <summary>One leg of a junction while it's being classed: its road, the way it leaves, its rank.</summary>
        struct LegInfo
        {
            public int edge;
            public Vec2 dir;
            public int rank;
            public RoadClass cls;
            public double speed;
        }

        static void AssignControl(TownModel model, RoadGraph graph, List<int>[] adj, int[] legs, List<int> gates)
        {
            var edges = graph.edges;
            int N = graph.nodes.Count;
            // One kind of equal junction a town: all-way stops, or right-before-left.
            bool europe = (Rng.HashString(model.seed ?? "") & 1) == 1;
            var info = new List<LegInfo>[N];
            for (int n = 0; n < N; n++)
            {
                var node = graph.nodes[n];
                var list = new List<LegInfo>();
                foreach (int k in adj[n])
                {
                    var e = edges[k];
                    if (e.from == n) list.Add(Leg(e, e.points, node.at));
                    if (e.to == n) list.Add(Leg(e, Reversed(e.points), node.at));
                }
                info[n] = list;
            }

            // Signals first: the busiest arterial crossings, spaced out, within a budget.
            double arterialLength = 0;
            foreach (var e in edges) if (e.cls == RoadClass.Arterial) arterialLength += Geo.PolylineLength(e.points);
            int budget = Math.Max(1, (int)Math.Floor(arterialLength / ARTERIAL_PER_SIGNAL));
            var candidates = new List<(double score, int node)>();
            for (int n = 0; n < N; n++)
            {
                if (legs[n] < 3 || gates.Contains(n)) continue;
                int art = 0, ac = 0;
                double score = 0;
                foreach (var l in info[n])
                {
                    if (l.cls == RoadClass.Arterial) art++;
                    if ((l.cls == RoadClass.Arterial || l.cls == RoadClass.Collector) && edges[l.edge].kind != RoadKind.Path) ac++;
                    score += Units.MetresPerSecond(l.speed);
                }
                if (ac >= 3 && art >= 2 && score >= SIGNAL_SCORE) candidates.Add((score, n));
            }
            candidates.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : a.node.CompareTo(b.node));
            var signals = new List<int>();
            foreach (var (_, n) in candidates)
            {
                if (signals.Count >= budget) break;
                bool clear = true;
                foreach (int s in signals) if (Geo.Dist(graph.nodes[s].at, graph.nodes[n].at) < SIGNAL_SPACING) clear = false;
                if (clear) signals.Add(n);
            }

            for (int n = 0; n < N; n++)
            {
                var node = graph.nodes[n];
                node.minor = new List<int>();
                int k = legs[n];
                if (k == 0 || k == 2)
                {
                    node.control = JunctionControl.None;
                    continue;
                }
                if (k == 1)
                {
                    node.control = gates.Contains(n) ? JunctionControl.Gate : JunctionControl.DeadEnd;
                    continue;
                }
                var list = info[n];
                // The major pair: the two highest-ranked legs (of those, the straightest pair).
                int a = -1, b = -1;
                double best = double.NegativeInfinity;
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        double score = (list[i].rank + list[j].rank) * 10 - Geo.Dot(list[i].dir, list[j].dir);
                        if (score > best + 1e-9)
                        {
                            best = score;
                            a = i;
                            b = j;
                        }
                    }
                int minRank = int.MaxValue, maxRank = int.MinValue;
                foreach (var l in list)
                {
                    minRank = Math.Min(minRank, l.rank);
                    maxRank = Math.Max(maxRank, l.rank);
                }
                int pairRank = Math.Min(list[a].rank, list[b].rank);
                bool even = minRank == maxRank;
                for (int i = 0; i < list.Count; i++)
                    if (i != a && i != b && !node.minor.Contains(list[i].edge)) node.minor.Add(list[i].edge);
                node.minor.Sort();
                if (signals.Contains(n))
                {
                    node.control = JunctionControl.Signal;
                    node.roundabout = Roundabout(graph, n, list);
                    continue;
                }
                if (even && k >= 4)
                {
                    node.control = europe ? JunctionControl.RightBeforeLeft : JunctionControl.AllWayStop;
                    node.minor.Clear();
                    node.roundabout = Roundabout(graph, n, list);
                    continue;
                }
                // Priority to the major pair, if it outranks the rest or the junction is a T.
                bool pairOutranks = true;
                for (int i = 0; i < list.Count; i++) if (i != a && i != b && list[i].rank > pairRank) pairOutranks = false;
                if (!pairOutranks && k >= 4)
                {
                    node.control = europe ? JunctionControl.RightBeforeLeft : JunctionControl.AllWayStop;
                    node.minor.Clear();
                    continue;
                }
                node.control = JunctionControl.Priority;
                // A stop sign instead of a give-way if a minor leg comes in sharp, jogs, or the junction's by a bridge.
                bool stop = NearBridge(model, node.at);
                for (int i = 0; i < list.Count && !stop; i++)
                {
                    if (i == a || i == b) continue;
                    double toA = DetMath.Acos(Clamp(Geo.Dot(list[i].dir, list[a].dir), -1, 1));
                    double toB = DetMath.Acos(Clamp(Geo.Dot(list[i].dir, list[b].dir), -1, 1));
                    if (Math.Min(toA, toB) < SHARP) stop = true;
                    else if (Jogs(graph, info, legs, n, list[a], list[b], list[i])) stop = true;
                }
                if (stop) node.control = JunctionControl.Stop;
            }
        }

        static LegInfo Leg(RoadEdge e, List<Vec2> outward, Vec2 at) => new LegInfo
        {
            edge = e.id,
            dir = Heading(at, outward),
            rank = e.priority,
            cls = e.cls,
            speed = e.speed,
        };

        static bool NearBridge(TownModel model, Vec2 at)
        {
            foreach (var b in model.bridges)
                if (Geo.Dist(b.from, at) < NEAR_BRIDGE || Geo.Dist(b.to, at) < NEAR_BRIDGE) return true;
            return false;
        }

        /// <summary>
        /// A side road whose fellow on the other side of the major road comes in a little way along
        /// it rather than straight across (a jog, or dog-leg): drivers crossing over meet traffic twice.
        /// </summary>
        static bool Jogs(RoadGraph graph, List<LegInfo>[] info, int[] legs, int n, LegInfo a, LegInfo b, LegInfo stem)
        {
            var at = graph.nodes[n].at;
            double side = Geo.Cross(a.dir, stem.dir);
            foreach (var major in new[] { a, b })
            {
                var e = graph.edges[major.edge];
                int far = e.from == n ? e.to : e.from;
                if (far == n || legs[far] != 3) continue;
                double len = Geo.PolylineLength(e.points);
                if (len > (e.kind == RoadKind.Main ? MAIN_JOG : JOG)) continue;
                // The far junction's own side road: the leg that isn't on the major road.
                var farAt = graph.nodes[far].at;
                foreach (var l in info[far])
                {
                    if (l.edge == major.edge) continue;
                    if (Math.Abs(Geo.Dot(l.dir, a.dir)) > 0.7) continue; // carries the major road on
                    if (Geo.Cross(a.dir, l.dir) * side < 0 && Geo.Dist(farAt, at) > JOIN) return true;
                }
            }
            return false;
        }

        /// <summary>Four or five arterial legs about even, and nothing else within a roundabout's reach.</summary>
        static bool Roundabout(RoadGraph graph, int n, List<LegInfo> list)
        {
            if (list.Count < 4 || list.Count > 5) return false;
            foreach (var l in list) if (l.cls != RoadClass.Arterial) return false;
            var at = graph.nodes[n].at;
            foreach (var o in graph.nodes) if (o.id != n && Geo.Dist(o.at, at) < 6) return false;
            return true;
        }

        /// <summary>A small binary min-heap of (key, node), ties broken by node so the order never depends on insertion.</summary>
        sealed class MinHeap
        {
            readonly List<(double k, int n)> items = new List<(double, int)>();
            public int Count => items.Count;

            static bool Less((double k, int n) a, (double k, int n) b) => a.k < b.k || (a.k == b.k && a.n < b.n);

            public void Push(double k, int n)
            {
                items.Add((k, n));
                int i = items.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (!Less(items[i], items[p])) break;
                    (items[i], items[p]) = (items[p], items[i]);
                    i = p;
                }
            }

            public (double k, int n) Pop()
            {
                var top = items[0];
                var last = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                if (items.Count > 0)
                {
                    items[0] = last;
                    int i = 0;
                    while (true)
                    {
                        int l = i * 2 + 1, r = l + 1, m = i;
                        if (l < items.Count && Less(items[l], items[m])) m = l;
                        if (r < items.Count && Less(items[r], items[m])) m = r;
                        if (m == i) break;
                        (items[i], items[m]) = (items[m], items[i]);
                        i = m;
                    }
                }
                return top;
            }
        }
    }
}

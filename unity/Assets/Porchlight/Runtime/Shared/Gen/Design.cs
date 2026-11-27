// Stage: the junction design (docs/street-editor.md §5). Once the buildings are up, the town's morning peak is
// estimated: households and jobs from the buildings, trips spread between them by a gravity model, loaded onto
// the roads with four rounds of BPR (the method of successive averages, near enough Wardrop for a design). From
// those design flows, by published warrants and rules of thumb, each road and junction gets what a traffic
// engineer would give it:
//
// - two lanes each way where one can't carry the flow, and a median (a divided road) where it meets only big roads;
// - a relief route: a parallel collector upgraded when an arterial is still over capacity (kept only if it helps:
//   Braess);
// - roundabouts for five-leg and sharp junctions, and busy all-way stops, where the HCM's entry capacity allows;
// - left- and right-turn pockets sized to the queue, and a protected left stage at signals that need one;
// - slip roads for heavy right turns that bend gently, round the signal.
//
// An editor's overrides (Skeleton.roads, Skeleton.junctions) win at every step. Pure; draws no random numbers.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class DesignStage : IStepwiseStage
    {
        public string Name => "design";

        /// <summary>A lane's capacity between junctions (cars an hour): a main road's, a street's. (tune)</summary>
        public const double MAIN_LANE = 900, STREET_LANE = 600;
        /// <summary>Over this share of a lane's capacity, a main road gets a second lane each way.</summary>
        const double WIDEN_AT = 0.6;
        /// <summary>A through lane's width (tiles: 3 m), and a median's.</summary>
        public const double LANE = 0.75, MEDIAN = 0.75;
        /// <summary>Car trips leaving each household in the peak hour, and through each gate each way.</summary>
        const double TRIPS_PER_HOME = 0.45, GATE_TRIPS = 120, THROUGH_TRIPS = 50;
        /// <summary>The gravity model's fall-off with free-flow time (per minute).</summary>
        const double BETA = 0.12;
        const int MSA_ROUNDS = 4;
        /// <summary>Signal cycle assumed for pocket storage (s), and a queued car's length with its gap (tiles).</summary>
        const double CYCLE_S = 70, CAR_SPACE = 1.6;
        /// <summary>The least pocket (two cars), and its taper (tiles).</summary>
        public const double POCKET_MIN = 3.2, TAPER = 1.5;
        /// <summary>A roundabout's ring radius: among streets, and with a main road in it (tiles; FHWA's mini and single-lane sizes, at a game's scale).</summary>
        public const double MINI_RADIUS = 1.75, ROUNDABOUT_RADIUS = 2.5;
        /// <summary>A would-be all-way stop this busy (cars an hour entering) is a roundabout instead. (tune)</summary>
        const double ROUNDABOUT_ENTERING = 350;
        /// <summary>A signalled crossing out of the core this quiet (cars an hour entering) is a roundabout instead. (tune)</summary>
        const double SUBURB_SIGNAL_MOST = 1200;
        /// <summary>The most buildings a roundabout may take out of its corners without an editor asking for it.</summary>
        const int ROUNDABOUT_TAKES = 2;
        const int MOST_RELIEF = 3;
        /// <summary>How many origins' trips are loaded in a share of the assignment.</summary>
        const int ORIGINS_A_PART = 100;
        /// <summary>
        /// The warrants, at a game town's scale (tune). Real practice (NCHRP 457, the ITE cross-product rule) is a left
        /// pocket at a signal from 100 an hour turning, or 50,000 turning × oncoming; a town of a few thousand rarely
        /// gets there, so these are lower: the game's junctions get pockets where turners would hold up the queue.
        /// </summary>
        const double LEFT_AT_SIGNAL = 40, CROSS_PRODUCT = 15_000, PROTECTED_AT = 25_000, LEFT_AT_STOP = 30, OPPOSING_AT_STOP = 150, RIGHT_AT = 120, SLIP_AT = 150;
        const double STRAIGHT = 0.5;
        static readonly double SHARP = 60 * Math.PI / 180, SLIP_BEND = 75 * Math.PI / 180;

        public TownModel Run(TownModel model, Rng rng) => Stepwise.Drain<TownModel>(done => Steps(model, rng, done));

        /// <summary>The design a share at a time (the web build has no threads): demand, each round of the assignment, lanes, relief, the junctions.</summary>
        public IEnumerable<string> Steps(TownModel model, Rng rng, Action<TownModel> done)
        {
            var next = model.With();
            // The roads are changed (relief routes reclassed): a copy, so the model before this stage keeps its own.
            next.roads = CloneRoads(model.roads);
            var buildings = new List<Building>(model.buildings);
            foreach (var part in Design(next, buildings, x => next.design = x)) yield return part;
            if (buildings.Count != model.buildings.Count)
            {
                // Houses taken for a wider road or a ring: the rest renumbered (a building's id is its place in the
                // list), and the neighbourhoods' schools and corner shops with them.
                var newId = new Dictionary<int, int>();
                for (int i = 0; i < buildings.Count; i++)
                {
                    var b = buildings[i];
                    newId[b.id] = i;
                    buildings[i] = new Building { id = i, lotId = b.lotId, footprint = b.footprint, tiles = b.tiles, kind = b.kind, floors = b.floors, facing = b.facing };
                }
                var hoods = new List<Neighbourhood>();
                foreach (var h in model.neighbourhoods)
                {
                    var c = new Neighbourhood { id = h.id, blockIds = h.blockIds, middle = h.middle, area = h.area, parkBlockId = h.parkBlockId };
                    c.schoolBuildingId = h.schoolBuildingId >= 0 && newId.TryGetValue(h.schoolBuildingId, out int sid) ? sid : -1;
                    foreach (int k in h.cornerShopIds) if (newId.TryGetValue(k, out int nk)) c.cornerShopIds.Add(nk);
                    hoods.Add(c);
                }
                next.neighbourhoods = hoods;
                var sv = model.services;
                if (sv != null)
                {
                    int one(int k) => k >= 0 && newId.TryGetValue(k, out int v) ? v : -1;
                    List<int> many(List<int> ks) { var o = new List<int>(); foreach (int k in ks) if (newId.TryGetValue(k, out int v)) o.Add(v); return o; }
                    next.services = new TownServices
                    {
                        supermarkets = many(sv.supermarkets), cornerStores = many(sv.cornerStores), highStreet = many(sv.highStreet),
                        petrolStation = one(sv.petrolStation), fireStation = one(sv.fireStation), clinic = one(sv.clinic), households = sv.households,
                    };
                }
            }
            next.buildings = buildings;
            done(next);
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            var d = model.design;
            if (d == null) return errors;
            if (d.edges.Count != model.roads.edges.Count) errors.Add($"{d.edges.Count} road designs for {model.roads.edges.Count} roads");
            if (d.nodes.Count != model.roads.nodes.Count) errors.Add($"{d.nodes.Count} junction designs for {model.roads.nodes.Count} junctions");
            foreach (var e in d.edges)
            {
                var edge = model.roads.edges[e.edge];
                double len = Geo.PolylineLength(edge.points);
                double pocketsAt(bool atTo) { double s = 0; foreach (var p in e.pockets) if (p.atTo == atTo) s = Math.Max(s, p.length + TAPER); return s; }
                if (e.pockets.Count > 0 && pocketsAt(true) + pocketsAt(false) > len - 2) errors.Add($"road {e.edge}'s pockets overlap ({len:0.0} tiles long)");
                foreach (var p in e.pockets) if (p.length < POCKET_MIN - 1e-9) errors.Add($"road {e.edge} has a pocket shorter than two cars");
                if (e.lanesFwd < 0 || e.lanesBack < 0 || e.lanesFwd + e.lanesBack == 0 || e.lanesFwd > 3 || e.lanesBack > 3) errors.Add($"road {e.edge} has {e.lanesFwd}+{e.lanesBack} lanes");
            }
            foreach (var n in d.nodes)
            {
                if (n.form != JunctionForm.Roundabout) continue;
                foreach (var e in model.roads.edges)
                {
                    if (e.from != n.node && e.to != n.node) continue;
                    if (Geo.PolylineLength(e.points) < n.radius + 2) errors.Add($"a roundabout's leg (road {e.id}) is shorter than its ring");
                }
            }
            return errors;
        }

        static RoadGraph CloneRoads(RoadGraph g)
        {
            var o = new RoadGraph();
            foreach (var n in g.nodes) o.nodes.Add(new RoadNode { id = n.id, at = n.at });
            foreach (var e in g.edges) o.edges.Add(new RoadEdge { id = e.id, from = e.from, to = e.to, kind = e.kind, cls = e.cls, points = e.points });
            return o;
        }

        /// <summary>The main roads in from the gates (each to its first junction), which grow to the highway's lanes.</summary>
        public static List<List<Vec2>> GateRoads(TownModel model)
        {
            var o = new List<List<Vec2>>();
            // Only a big town's, and only where the road in stays out of the middle: a small town's road in from a gate
            // is its high street.
            if (model.townRadius < BOULEVARD_TOWN) return o;
            foreach (var e in model.roads.edges)
            {
                if (e.kind != RoadKind.Main) continue;
                double nearest = double.PositiveInfinity;
                foreach (var q in e.points) nearest = Math.Min(nearest, Geo.Dist(q, model.center));
                if (nearest < BOULEVARD_OUT * model.townRadius) continue;
                foreach (var g in model.gates)
                    if (Geo.Dist(g, model.roads.nodes[e.from].at) < 1.5 || Geo.Dist(g, model.roads.nodes[e.to].at) < 1.5) { o.Add(e.points); break; }
            }
            return o;
        }

        /// <summary>How far from a gate road's centre line the lots keep back: its widest design, parking, sidewalk and a little.</summary>
        /// <summary>Gate boulevards only in a town this big (radius, tiles), and only on roads in that keep this share of the radius from the centre.</summary>
        const double BOULEVARD_TOWN = 100, BOULEVARD_OUT = 0.45;

        public static double GateReach(GenParams p) => (Math.Max(BaseWidth(RoadKind.Main, p), 4 * LANE) + MEDIAN) / 2 + Raster.PARKING_WIDTH + Raster.SIDEWALK_WIDTH + 0.3;

        /// <summary>A road's rank (RoadNet.Rank's): an arterial or main road 3, a collector 2, a local street 1.</summary>
        public static int Rank(RoadEdge e) => e.kind == RoadKind.Main || e.cls == RoadClass.Arterial ? 3 : e.cls == RoadClass.Collector ? 2 : 1;

        /// <summary>A road's width with no design: the key's (Layout.RoadWidth's).</summary>
        public static double BaseWidth(RoadKind kind, GenParams p) => kind == RoadKind.Main ? p.mainRoadWidth : kind == RoadKind.Street ? p.streetWidth : 0.5;

        /// <summary>A road's carriageway width, designed or not.</summary>
        public static double Width(TownModel model, RoadEdge e) => model.design?.Edge(e.id)?.width ?? BaseWidth(e.kind, model.@params);

        // --- The network the design flows run on --------------------------------------------------------------

        sealed class Net
        {
            public int nodes;
            /// <summary>Arcs: 2e from→to, 2e+1 to→from. -1 tail: not driven (a path, or the wrong way of a one-way road).</summary>
            public int[] tail, head;
            public double[] t0, cap, flow, len;
            public List<int>[] outs;
            public readonly Dictionary<long, double> moves = new Dictionary<long, double>();
        }

        static long MoveKey(int inArc, int outArc) => ((long)inArc << 32) | (uint)outArc;

        // --- The design ---------------------------------------------------------------------------------------

        /// <summary>How many junctions are designed in a share.</summary>
        const int JUNCTIONS_A_PART = 40;

        static IEnumerable<string> Design(TownModel model, List<Building> buildings, Action<RoadDesign> done)
        {
            var roads = model.roads;
            var p = model.@params;
            var sk = p.skeleton;
            var d = new RoadDesign();
            foreach (var e in roads.edges) d.edges.Add(new EdgeDesign { edge = e.id, width = BaseWidth(e.kind, p) });
            foreach (var n in roads.nodes) d.nodes.Add(new NodeDesign { node = n.id });
            if (roads.edges.Count == 0)
            {
                done(d);
                yield break;
            }

            // Editors' overrides on roads: class first (it changes ranks), one-way, locks.
            foreach (var e in roads.edges)
            {
                var o = sk?.RoadAt(roads.nodes[e.from].at, roads.nodes[e.to].at);
                if (o == null) continue;
                var ed = d.edges[e.id];
                if (o.cls != null) e.cls = o.cls.Value;
                ed.locked = o.locked;
                if (o.oneWay == true)
                {
                    bool forward = Geo.Dist(o.a, roads.nodes[e.from].at) <= Geo.Dist(o.a, roads.nodes[e.to].at);
                    if (forward) ed.lanesBack = 0;
                    else ed.lanesFwd = 0;
                }
            }

            var degree = new int[roads.nodes.Count];
            foreach (var e in roads.edges) if (e.kind != RoadKind.Path) { degree[e.from]++; degree[e.to]++; }
            var gate = new bool[roads.nodes.Count];
            foreach (var n in roads.nodes) foreach (var g in model.gates) if (Geo.Dist(g, n.at) < 1.5) gate[n.id] = true;

            // Demand: homes and jobs from the buildings, at the nearest junction of a driven road.
            var homes = new double[roads.nodes.Count];
            var jobs = new double[roads.nodes.Count];
            foreach (var b in buildings)
            {
                if (b.tiles == null || b.tiles.Count == 0) continue;
                double cx = 0, cy = 0;
                foreach (var t in b.tiles) { cx += t.x + 0.5; cy += t.y + 0.5; }
                var at = new Vec2(cx / b.tiles.Count, cy / b.tiles.Count);
                int node = NearestNode(roads, at);
                if (node < 0) continue;
                var (h, j) = HomesAndJobs(b);
                homes[node] += h;
                jobs[node] += j;
            }

            var net = BuildNet(model, d);
            var demand = Demand(model, net, homes, jobs, gate);
            yield return " demand";
            foreach (var part in AssignSteps(net, demand)) yield return part;

            // Through lanes and medians, link by link.
            SetLanes(model, d, net, buildings, sk);
            yield return " lanes";

            // Relief routes for arterials still over capacity.
            int relieved = 0;
            var order = new List<int>();
            foreach (var e in roads.edges) if (e.kind == RoadKind.Main) order.Add(e.id);
            order.Sort((a, b) => VC(d.edges[b]).CompareTo(VC(d.edges[a])));
            foreach (int id in order)
            {
                if (relieved >= MOST_RELIEF) break;
                if (VC(d.edges[id]) <= 1.0 || d.edges[id].locked) continue;
                if (TryRelief(model, d, id, demand)) relieved++;
                yield return " relief";
            }
            if (relieved > 0)
            {
                net = BuildNet(model, d);
                foreach (var part in AssignSteps(net, demand)) yield return part;
                SetFlows(d, net);
            }

            // Junctions: first a roundabout in each area an editor regenerated wanting one, then the rest.
            var taken = new HashSet<int>();
            var designed = new HashSet<int>();
            for (int r = 0; r < Regions.Count(model); r++)
            {
                if (!Regions.Get(model, r).want.roundabout) continue;
                // Its junctions (and those on the roads round its edge), busiest first.
                var near = new List<(int node, double flow)>();
                for (int n = 0; n < roads.nodes.Count; n++)
                {
                    if (degree[n] < 3 || gate[n] || designed.Contains(n) || !NearRegion(model, r, roads.nodes[n].at)) continue;
                    double flow = 0;
                    foreach (var e in roads.edges) if (e.from == n || e.to == n) flow += d.edges[e.id].flowFwd + d.edges[e.id].flowBack;
                    near.Add((n, flow));
                }
                near.Sort((a, b) => b.flow != a.flow ? b.flow.CompareTo(a.flow) : a.node.CompareTo(b.node));
                bool made = near.Exists(c => d.nodes[c.node].form == JunctionForm.Roundabout);
                for (int k = 0; k < near.Count && k < REGION_ROUNDABOUT_TRIES && !made; k++)
                {
                    int n = near[k].node, notes = d.notes.Count;
                    DesignJunction(model, d, net, n, buildings, taken, sk, forceRoundabout: true);
                    designed.Add(n);
                    yield return " roundabout";
                    made = d.nodes[n].form == JunctionForm.Roundabout;
                    // Only the last try says why it couldn't.
                    if (!made) d.notes.RemoveRange(notes, d.notes.Count - notes);
                }
                if (!made) d.notes.Add(new DesignNote { at = Regions.Middle(model, r), text = "No room for a roundabout in the regenerated area" });
            }
            int junctions = 0;
            for (int n = 0; n < roads.nodes.Count; n++)
            {
                if (degree[n] < 3 || gate[n] || designed.Contains(n)) continue;
                DesignJunction(model, d, net, n, buildings, taken, sk);
                if (++junctions % JUNCTIONS_A_PART == 0) yield return " junctions";
            }
            if (taken.Count > 0) buildings.RemoveAll(b => taken.Contains(b.id));

            foreach (var ed in d.edges)
            {
                var e = roads.edges[ed.edge];
                if (ed.vc > 1.0 && e.kind != RoadKind.Path)
                    d.notes.Add(new DesignNote { at = Geo.PolylinePointAt(e.points, Geo.PolylineLength(e.points) / 2), text = $"Over capacity at the peak ({ed.vc:0.00} of what it carries)" });
            }
            done(d);
        }

        static double VC(EdgeDesign e) => e.vc;

        static (double homes, double jobs) HomesAndJobs(Building b)
        {
            double area = b.tiles.Count;
            int floors = Math.Max(1, b.floors);
            switch (b.kind)
            {
                case BuildingKind.House: return (Math.Max(1, floors * area / 10.0), 0);
                case BuildingKind.Shop: return (floors > 2 ? (floors - 1) * area / 14.0 : 0, Math.Max(2, (floors > 2 ? 1 : floors) * area / 2.5 + (floors > 2 ? (floors - 1) * area / 6.0 : 0)));
                case BuildingKind.Workshop: return (0, Math.Max(3, area / 3.0));
                case BuildingKind.School: return (0, 30);
                case BuildingKind.Supermarket: return (0, 25);
                case BuildingKind.PetrolStation: return (0, 4);
                case BuildingKind.FireStation: return (0, 8);
                case BuildingKind.Clinic: return (0, 20);
                case BuildingKind.Church: return (0, 2);
                case BuildingKind.Hall: return (0, 4);
                default: return (0, 1);
            }
        }

        static int NearestNode(RoadGraph roads, Vec2 at)
        {
            int best = -1;
            double bestD = double.PositiveInfinity;
            foreach (var e in roads.edges)
            {
                if (e.kind == RoadKind.Path) continue;
                var hit = Geo.ClosestOnPolyline(at, e.points);
                if (hit.d >= bestD) continue;
                bestD = hit.d;
                double along = 0;
                for (int k = 0; k < hit.seg; k++) along += Geo.Dist(e.points[k], e.points[k + 1]);
                along += Geo.Dist(e.points[hit.seg], hit.point);
                best = along < Geo.PolylineLength(e.points) / 2 ? e.from : e.to;
            }
            return best;
        }

        static Net BuildNet(TownModel model, RoadDesign d)
        {
            var roads = model.roads;
            int m = roads.edges.Count;
            var net = new Net
            {
                nodes = roads.nodes.Count,
                tail = new int[2 * m], head = new int[2 * m], t0 = new double[2 * m], cap = new double[2 * m], flow = new double[2 * m], len = new double[2 * m],
                outs = new List<int>[roads.nodes.Count],
            };
            for (int i = 0; i < net.outs.Length; i++) net.outs[i] = new List<int>();
            foreach (var e in roads.edges)
            {
                double len = Geo.PolylineLength(e.points);
                double speed = e.kind == RoadKind.Main ? 4.3 : 2.6;
                var ed = d.edges[e.id];
                for (int dir = 0; dir < 2; dir++)
                {
                    int a = 2 * e.id + dir;
                    int lanes = dir == 0 ? ed.lanesFwd : ed.lanesBack;
                    if (e.kind == RoadKind.Path || lanes == 0 || len < 0.5)
                    {
                        net.tail[a] = -1;
                        continue;
                    }
                    net.tail[a] = dir == 0 ? e.from : e.to;
                    net.head[a] = dir == 0 ? e.to : e.from;
                    net.len[a] = len;
                    net.t0[a] = len / speed + 2;
                    net.cap[a] = lanes * (e.kind == RoadKind.Main || Rank(e) == 3 ? MAIN_LANE : STREET_LANE);
                    net.outs[net.tail[a]].Add(a);
                }
            }
            return net;
        }

        sealed class Trips
        {
            public readonly List<int> origins = new List<int>();
            /// <summary>Per origin: (destination node, trips an hour).</summary>
            public readonly List<List<(int node, double trips)>> to = new List<List<(int, double)>>();
        }

        /// <summary>The peak hour's car trips, origin to destination, by a gravity model on free-flow time.</summary>
        static Trips Demand(TownModel model, Net net, double[] homes, double[] jobs, bool[] gate)
        {
            int n = net.nodes;
            var attract = new double[n];
            var produce = new double[n];
            for (int i = 0; i < n; i++)
            {
                attract[i] = jobs[i] + 0.3 * homes[i];
                produce[i] = homes[i] * TRIPS_PER_HOME;
            }
            var gates = new List<int>();
            for (int i = 0; i < n; i++) if (gate[i] && net.outs[i].Count > 0) gates.Add(i);
            foreach (int g in gates)
            {
                produce[g] += GATE_TRIPS;
                attract[g] += GATE_TRIPS * 4;
            }
            var trips = new Trips();
            var cost = new double[n];
            var pred = new int[n];
            for (int o = 0; o < n; o++)
            {
                if (produce[o] <= 0) continue;
                Dijkstra(net, o, net.t0, cost, pred);
                double sum = 0;
                for (int j = 0; j < n; j++) if (j != o && attract[j] > 0 && !double.IsInfinity(cost[j])) sum += attract[j] * Math.Exp(-BETA * cost[j] / 60);
                if (sum <= 0) continue;
                var list = new List<(int, double)>();
                for (int j = 0; j < n; j++)
                {
                    if (j == o || attract[j] <= 0 || double.IsInfinity(cost[j])) continue;
                    double t = produce[o] * attract[j] * Math.Exp(-BETA * cost[j] / 60) / sum;
                    if (t > 1e-3) list.Add((j, t));
                }
                // Through traffic: gate to gate.
                if (gate[o]) foreach (int g in gates) if (g != o && !double.IsInfinity(cost[g])) list.Add((g, THROUGH_TRIPS));
                trips.origins.Add(o);
                trips.to.Add(list);
            }
            return trips;
        }

        static void Dijkstra(Net net, int from, double[] arcCost, double[] cost, int[] pred) => Dijkstra(net, from, arcCost, cost, pred, null);

        /// <summary>Least-cost paths from `from`; `settled`, if given, gets the junctions reached in the order they were settled (cheapest first).</summary>
        static void Dijkstra(Net net, int from, double[] arcCost, double[] cost, int[] pred, List<int> settled)
        {
            settled?.Clear();
            int n = net.nodes;
            for (int i = 0; i < n; i++)
            {
                cost[i] = double.PositiveInfinity;
                pred[i] = -1;
            }
            cost[from] = 0;
            // A binary heap, least (cost, node) first: a junction whose cost falls is pushed again and its old entry
            // skipped when it comes up, so the junctions are settled in the very order a sorted set would give.
            var heap = new List<(double c, int node)> { (0, from) };
            while (heap.Count > 0)
            {
                var top = Pop(heap);
                int u = top.node;
                if (top.c > cost[u]) continue;
                settled?.Add(u);
                foreach (int a in net.outs[u])
                {
                    int v = net.head[a];
                    double c = top.c + arcCost[a];
                    if (c < cost[v] - 1e-12)
                    {
                        cost[v] = c;
                        pred[v] = a;
                        Push(heap, (c, v));
                    }
                }
            }
        }

        static bool Less((double c, int node) a, (double c, int node) b) => a.c < b.c || (a.c == b.c && a.node < b.node);

        static void Push(List<(double c, int node)> heap, (double c, int node) item)
        {
            heap.Add(item);
            for (int i = heap.Count - 1; i > 0;)
            {
                int up = (i - 1) / 2;
                if (!Less(heap[i], heap[up])) break;
                (heap[i], heap[up]) = (heap[up], heap[i]);
                i = up;
            }
        }

        static (double c, int node) Pop(List<(double c, int node)> heap)
        {
            var top = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            for (int i = 0; ;)
            {
                int l = 2 * i + 1, r = l + 1, least = i;
                if (l < heap.Count && Less(heap[l], heap[least])) least = l;
                if (r < heap.Count && Less(heap[r], heap[least])) least = r;
                if (least == i) break;
                (heap[i], heap[least]) = (heap[least], heap[i]);
                i = least;
            }
            return top;
        }

        /// <summary>Four rounds of all-or-nothing loading on BPR costs, averaged (MSA): flows on every arc and every turn.</summary>
        static void Assign(Net net, Trips trips)
        {
            foreach (var _ in AssignSteps(net, trips)) { }
        }

        /// <summary>The assignment (Assign) a share at a time: a round, or part of one in a big town.</summary>
        static IEnumerable<string> AssignSteps(Net net, Trips trips)
        {
            int n = net.nodes, arcs = net.tail.Length;
            var x = new double[arcs];
            var moves = new Dictionary<long, double>();
            var cost = new double[n];
            var pred = new int[n];
            var acc = new double[n];
            var settled = new List<int>(n);
            for (int round = 1; round <= MSA_ROUNDS; round++)
            {
                var arcCost = new double[arcs];
                for (int a = 0; a < arcs; a++)
                {
                    if (net.tail[a] < 0) continue;
                    double r = net.cap[a] > 0 ? x[a] / net.cap[a] : 0;
                    arcCost[a] = net.t0[a] * (1 + 0.15 * r * r * r * r);
                }
                var y = new double[arcs];
                var ym = new Dictionary<long, double>();
                for (int k = 0; k < trips.origins.Count; k++)
                {
                    int o = trips.origins[k];
                    if (k > 0 && k % ORIGINS_A_PART == 0) yield return " assign";
                    Dijkstra(net, o, arcCost, cost, pred, settled);
                    Array.Clear(acc, 0, n);
                    foreach (var (node, t) in trips.to[k]) acc[node] += t;
                    // Dearest first, so each junction's trips are all in before they're passed back along its path.
                    for (int s = settled.Count - 1; s >= 0; s--)
                    {
                        int v = settled[s];
                        if (double.IsInfinity(cost[v]) || v == o || acc[v] <= 0) continue;
                        int a = pred[v];
                        if (a < 0) continue;
                        y[a] += acc[v];
                        int u = net.tail[a];
                        int before = pred[u];
                        if (before >= 0)
                        {
                            long key = MoveKey(before, a);
                            ym[key] = (ym.TryGetValue(key, out var was) ? was : 0) + acc[v];
                        }
                        acc[u] += acc[v];
                    }
                }
                double share = 1.0 / round;
                for (int a = 0; a < arcs; a++) x[a] += share * (y[a] - x[a]);
                var keys = new HashSet<long>(moves.Keys);
                keys.UnionWith(ym.Keys);
                var next = new Dictionary<long, double>();
                foreach (long key in keys)
                {
                    double was = moves.TryGetValue(key, out var w) ? w : 0, now = ym.TryGetValue(key, out var z) ? z : 0;
                    double v = was + share * (now - was);
                    if (v > 1e-6) next[key] = v;
                }
                moves = next;
                yield return " assign";
            }
            Array.Copy(x, net.flow, arcs);
            net.moves.Clear();
            foreach (var kv in moves) net.moves[kv.Key] = kv.Value;
        }

        static void SetFlows(RoadDesign d, Net net)
        {
            foreach (var ed in d.edges)
            {
                ed.flowFwd = net.flow[2 * ed.edge];
                ed.flowBack = net.flow[2 * ed.edge + 1];
                double vc = 0;
                if (net.tail[2 * ed.edge] >= 0 && net.cap[2 * ed.edge] > 0) vc = Math.Max(vc, ed.flowFwd / net.cap[2 * ed.edge]);
                if (net.tail[2 * ed.edge + 1] >= 0 && net.cap[2 * ed.edge + 1] > 0) vc = Math.Max(vc, ed.flowBack / net.cap[2 * ed.edge + 1]);
                ed.vc = vc;
            }
        }

        /// <summary>The nearest a building's tile comes to a polyline (tiles, centre to centre), among those near it.</summary>
        static List<int> BuildingsWithin(List<Building> buildings, List<Vec2> pts, double reach)
        {
            var box = Geo.PolygonBounds(pts);
            var o = new List<int>();
            foreach (var b in buildings)
            {
                if (b.tiles == null) continue;
                foreach (var t in b.tiles)
                {
                    double x = t.x + 0.5, y = t.y + 0.5;
                    if (x < box.x0 - reach - 1 || x > box.x1 + reach + 1 || y < box.y0 - reach - 1 || y > box.y1 + reach + 1) continue;
                    if (Geo.DistToPolyline(new Vec2(x, y), pts) < reach) { o.Add(b.id); break; }
                }
            }
            return o;
        }

        /// <summary>What stops a road growing to `reach` either side: another road alongside, the zoo, the airport, a bridge (null: nothing).</summary>
        static string Blocked(TownModel model, RoadEdge e, double reach)
        {
            int w = model.width, h = model.height;
            foreach (var b in model.bridges) if (b.roadEdgeId == e.id) return "it crosses water on a bridge";
            foreach (var o in model.roads.edges)
            {
                if (o.id == e.id || o.from == e.from || o.from == e.to || o.to == e.from || o.to == e.to) continue;
                double room = reach + Raster.RoadReach(o.kind, model.@params);
                if (Geo.PolylineDistance(e.points, o.points) < room) return "another road runs too close";
            }
            var zoo = Menagerie.Mask(model.zoo, w, h, 1);
            var airport = Airfield.Mask(model.airport, w, h);
            var box = Geo.PolygonBounds(e.points);
            for (int y = Math.Max(0, (int)(box.y0 - reach - 1)); y <= Math.Min(h - 1, (int)(box.y1 + reach + 1)); y++)
                for (int x = Math.Max(0, (int)(box.x0 - reach - 1)); x <= Math.Min(w - 1, (int)(box.x1 + reach + 1)); x++)
                {
                    int i = Grid.Index(x, y, w);
                    if (zoo[i] == 0 && airport[i] == 0) continue;
                    if (Geo.DistToPolyline(new Vec2(x + 0.5, y + 0.5), e.points) < reach + 0.5) return zoo[i] != 0 ? "the zoo is in the way" : "the airport is in the way";
                }
            return null;
        }

        static void SetLanes(TownModel model, RoadDesign d, Net net, List<Building> buildings, Skeleton sk)
        {
            var roads = model.roads;
            var p = model.@params;
            var gateRoads = new HashSet<List<Vec2>>(GateRoads(model));
            var legClass = new Dictionary<int, int>();
            foreach (var e in roads.edges)
            {
                if (e.kind == RoadKind.Path) continue;
                foreach (int n in new[] { e.from, e.to })
                    legClass[n] = Math.Min(legClass.TryGetValue(n, out int c) ? c : 3, Rank(e));
            }
            foreach (var e in roads.edges)
            {
                var ed = d.edges[e.id];
                if (e.kind == RoadKind.Path) continue;
                double fwd = net.flow[2 * e.id], back = net.flow[2 * e.id + 1];
                var o = sk?.RoadAt(roads.nodes[e.from].at, roads.nodes[e.to].at);
                int lanes = 1;
                bool insisted = o != null && o.lanes > 0;
                // The highways come in at the gates two lanes each way (docs/highways.md): the main road in from a gate
                // keeps them, divided, to its first junction.
                bool fromGate = gateRoads.Contains(e.points);
                if (insisted) lanes = o.lanes;
                else if (!ed.locked && e.kind == RoadKind.Main && (fromGate || Math.Max(fwd, back) > WIDEN_AT * MAIN_LANE)) lanes = 2;
                bool divided = o?.divided ?? (!ed.locked && lanes >= 2 && (fromGate || (legClass.TryGetValue(e.from, out int a) && a >= 2 && legClass.TryGetValue(e.to, out int b) && b >= 2)));
                double baseW = BaseWidth(e.kind, p);
                double width = Math.Max(baseW, (ed.OneWay ? lanes : 2 * lanes) * LANE) + (divided ? MEDIAN : 0);
                if (lanes == 1 && !divided) width = baseW;
                if (width > baseW + 1e-9)
                {
                    // Wider only where the buildings leave room (the lots' front yards give it), unless an editor asked.
                    double reach = width / 2 + Raster.PARKING_WIDTH + Raster.SIDEWALK_WIDTH + 0.3;
                    var inWay = BuildingsWithin(buildings, e.points, reach);
                    string blocked = Blocked(model, e, reach);
                    if (blocked != null && !(insisted || o?.divided == true))
                    {
                        d.notes.Add(new DesignNote { at = Geo.PolylinePointAt(e.points, Geo.PolylineLength(e.points) / 2), text = "Needs another lane each way, but " + blocked });
                        lanes = 1;
                        divided = false;
                        width = baseW;
                    }
                    else if (inWay.Count > 0 && (!(insisted || o?.divided == true) || buildings.Exists(bd => bd.kind != BuildingKind.House && inWay.Contains(bd.id))))
                    {
                        d.notes.Add(new DesignNote { at = Geo.PolylinePointAt(e.points, Geo.PolylineLength(e.points) / 2), text = "Needs another lane each way, but buildings stand in the way" });
                        lanes = 1;
                        divided = false;
                        width = baseW;
                    }
                    else if (inWay.Count > 0)
                    {
                        var gone = new HashSet<int>(inWay);
                        buildings.RemoveAll(bd => gone.Contains(bd.id));
                    }
                }
                if (ed.lanesFwd > 0) ed.lanesFwd = lanes;
                if (ed.lanesBack > 0) ed.lanesBack = lanes;
                ed.divided = divided;
                ed.width = width;
                // Capacities follow the lanes.
                foreach (int dir in new[] { 0, 1 })
                {
                    int arc = 2 * e.id + dir;
                    int l = dir == 0 ? ed.lanesFwd : ed.lanesBack;
                    if (net.tail[arc] >= 0) net.cap[arc] = l * (e.kind == RoadKind.Main || Rank(e) == 3 ? MAIN_LANE : STREET_LANE);
                }
            }
            SetFlows(d, net);
        }

        /// <summary>
        /// A relief route for an arterial over capacity: the shortest way between its ends over collectors alone, no
        /// more than 1.4 times as long, kept off the local streets (no rat run through a neighbourhood: A/B Street's
        /// LTN idea). Upgraded to arterials, loaded again, and kept only if the arterial is less loaded for it.
        /// </summary>
        static bool TryRelief(TownModel model, RoadDesign d, int edgeId, Trips demand)
        {
            var roads = model.roads;
            var target = roads.edges[edgeId];
            double limit = 1.4 * Geo.PolylineLength(target.points);
            int n = roads.nodes.Count;
            var dist = new double[n];
            var pred = new int[n];
            for (int i = 0; i < n; i++) { dist[i] = double.PositiveInfinity; pred[i] = -1; }
            dist[target.from] = 0;
            var heap = new SortedSet<(double, int)> { (0, target.from) };
            while (heap.Count > 0)
            {
                var (c, u) = heap.Min;
                heap.Remove(heap.Min);
                if (c > dist[u] || c > limit) continue;
                foreach (var e in roads.edges)
                {
                    if (e.id == edgeId || e.kind != RoadKind.Street || e.cls != RoadClass.Collector || d.edges[e.id].locked) continue;
                    int v = e.from == u ? e.to : e.to == u ? e.from : -1;
                    if (v < 0) continue;
                    double nc = c + Geo.PolylineLength(e.points);
                    if (nc < dist[v])
                    {
                        if (!double.IsInfinity(dist[v])) heap.Remove((dist[v], v));
                        dist[v] = nc;
                        pred[v] = e.id;
                        heap.Add((nc, v));
                    }
                }
            }
            if (double.IsInfinity(dist[target.to]) || dist[target.to] > limit) return false;
            var path = new List<int>();
            for (int v = target.to; v != target.from; )
            {
                int e = pred[v];
                if (e < 0) return false;
                path.Add(e);
                var re = roads.edges[e];
                v = re.from == v ? re.to : re.from;
            }
            double before = d.edges[edgeId].vc;
            foreach (int e in path) roads.edges[e].cls = RoadClass.Arterial;
            var net = BuildNet(model, d);
            Assign(net, demand);
            double fwd = net.flow[2 * edgeId], back = net.flow[2 * edgeId + 1];
            var ed = d.edges[edgeId];
            double cap = Math.Max(1, Math.Max(ed.lanesFwd, ed.lanesBack)) * MAIN_LANE;
            double after = Math.Max(fwd, back) / cap;
            if (after >= before - 0.05)
            {
                foreach (int e in path) roads.edges[e].cls = RoadClass.Collector;
                return false;
            }
            foreach (int e in path) d.edges[e].relief = true;
            SetFlows(d, net);
            return true;
        }

        // --- Junctions ---------------------------------------------------------------------------------------

        sealed class Leg
        {
            public int edge, arcIn, arcOut;
            public Vec2 dir;
            public double angle;
            public int rank;
            public double inFlow, left, right, straight;
        }

        static Vec2 LeaveDir(RoadGraph roads, RoadEdge e, int node)
        {
            var pts = e.from == node ? e.points : new List<Vec2>(e.points);
            if (e.from != node) pts.Reverse();
            double len = Geo.PolylineLength(pts);
            var at = Geo.PolylinePointAt(pts, Math.Min(1.5, len / 2));
            var o = pts[0];
            double dx = at.x - o.x, dy = at.y - o.y, l = Numbers.Hypot(dx, dy);
            return l < 1e-9 ? new Vec2(1, 0) : new Vec2(dx / l, dy / l);
        }

        /// <summary>Which way a turn goes from an arriving way to a leaving way (RoadNet's convention: positive is right).</summary>
        static Turn TurnOf(Vec2 arriving, Vec2 leaving)
        {
            double angle = Math.Atan2(arriving.x * leaving.y - arriving.y * leaving.x, arriving.x * leaving.x + arriving.y * leaving.y);
            return Math.Abs(angle) < STRAIGHT ? Turn.Straight : angle > 0 ? Turn.Right : Turn.Left;
        }

        enum Turn { Straight, Right, Left }

        /// <summary>The junctions an editor's area tries for the roundabout it wanted (busiest first).</summary>
        const int REGION_ROUNDABOUT_TRIES = 6;
        /// <summary>A junction this near an editor's area (tiles) is one of its: those on the roads round its edge too.</summary>
        const int REGION_REACH = 3;

        static bool NearRegion(TownModel model, int r, Vec2 at)
        {
            for (int dy = -REGION_REACH; dy <= REGION_REACH; dy++)
                for (int dx = -REGION_REACH; dx <= REGION_REACH; dx++)
                    if (Regions.At(model, new Vec2(at.x + dx, at.y + dy)) == r) return true;
            return false;
        }

        static void DesignJunction(TownModel model, RoadDesign d, Net net, int node, List<Building> buildings, HashSet<int> taken, Skeleton sk, bool forceRoundabout = false)
        {
            var roads = model.roads;
            var nd = d.nodes[node];
            var at = roads.nodes[node].at;
            var legs = new List<Leg>();
            foreach (var e in roads.edges)
            {
                if (e.kind == RoadKind.Path || (e.from != node && e.to != node) || e.from == e.to) continue;
                var leg = new Leg { edge = e.id, dir = LeaveDir(roads, e, node), rank = Rank(e) };
                leg.arcIn = e.to == node ? 2 * e.id : 2 * e.id + 1;
                leg.arcOut = e.from == node ? 2 * e.id : 2 * e.id + 1;
                leg.angle = Math.Atan2(leg.dir.y, leg.dir.x);
                legs.Add(leg);
            }
            if (legs.Count < 3) return;
            legs.Sort((a, b) => a.angle.CompareTo(b.angle));
            foreach (var a in legs)
            {
                if (net.tail[a.arcIn] < 0) continue;
                a.inFlow = net.flow[a.arcIn];
                var arriving = new Vec2(-a.dir.x, -a.dir.y);
                foreach (var b in legs)
                {
                    if (b == a || net.tail[b.arcOut] < 0) continue;
                    double f = net.moves.TryGetValue(MoveKey(a.arcIn, b.arcOut), out var v) ? v : 0;
                    switch (TurnOf(arriving, b.dir))
                    {
                        case Turn.Left: a.left += f; break;
                        case Turn.Right: a.right += f; break;
                        default: a.straight += f; break;
                    }
                }
                nd.entering += a.inFlow;
            }

            // The control by rank (RoadNet.Control's rule, town-life §2).
            int arterials = 0, collectors = 0, top = 0, bottom = int.MaxValue;
            foreach (var l in legs)
            {
                if (l.rank == 3) arterials++;
                else if (l.rank == 2) collectors++;
                top = Math.Max(top, l.rank);
                bottom = Math.Min(bottom, l.rank);
            }
            DesignControl byRank = arterials >= 3 || (arterials == 2 && collectors >= 2) ? DesignControl.Signal
                : top == bottom && legs.Count >= 4 ? DesignControl.AllWayStop : DesignControl.Stop;
            double sharpest = Math.PI;
            for (int i = 0; i < legs.Count; i++)
            {
                double gap = legs[(i + 1) % legs.Count].angle - legs[i].angle;
                if (i == legs.Count - 1) gap += 2 * Math.PI;
                sharpest = Math.Min(sharpest, gap);
            }
            var o = sk?.JunctionAt(at);
            string asked = forceRoundabout ? "roundabout" : o?.control ?? "auto";
            // Where a roundabout serves better than signs or a signal (FHWA's guide): five legs or more, legs meeting
            // sharply, a busy all-way stop, a four-way crossing of collectors (or better) that would otherwise be a stop,
            // and, out of the old core, a signalled crossing quiet enough for one lane round a ring.
            int bigLegs = 0;
            foreach (var l in legs) if (l.rank >= 2) bigLegs++;
            bool core = TownPlan.EraAt(model, at) == Era.Core;
            bool roundabout = asked == "roundabout"
                || (asked == "auto" && (legs.Count >= 5 || sharpest < SHARP
                    || (byRank == DesignControl.AllWayStop && nd.entering >= ROUNDABOUT_ENTERING)
                    || (byRank == DesignControl.Stop && legs.Count == 4 && bigLegs == 4 && nd.entering >= ROUNDABOUT_ENTERING)
                    || (byRank == DesignControl.Signal && !core && legs.Count <= 4 && (nd.entering <= SUBURB_SIGNAL_MOST / 2 || (TownPlan.EraAt(model, at) == Era.PostWar && nd.entering <= SUBURB_SIGNAL_MOST)))));
            if (roundabout && TryRoundabout(model, d, node, legs, buildings, taken, asked == "roundabout"))
            {
                nd.form = JunctionForm.Roundabout;
                nd.control = DesignControl.Yield;
                return;
            }
            nd.control = asked switch
            {
                "free" => DesignControl.Free,
                "stop" => DesignControl.Stop,
                "allway" => DesignControl.AllWayStop,
                "signal" => DesignControl.Signal,
                _ => byRank,
            };

            // Pockets, protected lefts and slips.
            double most = 0;
            foreach (var l in legs) most = Math.Max(most, l.rank);
            foreach (var a in legs)
            {
                var e = roads.edges[a.edge];
                var ed = d.edges[a.edge];
                if (ed.locked || a.inFlow <= 0 || (e.kind != RoadKind.Main && e.cls == RoadClass.Local)) continue;
                var opposite = Opposite(legs, a);
                double opposing = opposite != null ? opposite.straight + opposite.right : 0;
                bool atTo = e.to == node;
                bool wantLeft = nd.control == DesignControl.Signal
                    ? a.left >= LEFT_AT_SIGNAL || a.left * opposing >= CROSS_PRODUCT
                    : nd.control == DesignControl.Stop && a.rank >= most && a.left >= LEFT_AT_STOP && opposing >= OPPOSING_AT_STOP;
                if (wantLeft)
                {
                    double queue = nd.control == DesignControl.Signal ? 2 * a.left * CYCLE_S / 3600 : a.left * 120 / 3600;
                    if (AddPocket(model, d, e, atTo, true, Math.Max(POCKET_MIN, queue * CAR_SPACE)) && nd.control == DesignControl.Signal && a.left * opposing >= PROTECTED_AT)
                        nd.protectedLeft = true;
                }
                bool wantRight = (a.right >= RIGHT_AT && a.rank == 3) || (nd.control == DesignControl.Signal && a.right >= RIGHT_AT / 2 && a.right >= a.inFlow / 3);
                if (!wantRight) continue;
                // A heavy right that bends gently, between two arterials at a signal: a slip road round it.
                var b = RightLeg(legs, a);
                if (nd.control == DesignControl.Signal && b != null && a.rank == 3 && b.rank == 3 && a.right >= SLIP_AT
                    && Math.Abs(Bend(a, b)) < SLIP_BEND && TrySlip(model, d, nd, a, b, buildings))
                    continue;
                double rq = nd.control == DesignControl.Signal ? 2 * a.right * CYCLE_S / 3600 : a.right * 120 / 3600;
                AddPocket(model, d, e, atTo, false, Math.Max(POCKET_MIN, rq * CAR_SPACE));
            }
        }

        /// <summary>The leg most nearly straight across from `a`.</summary>
        static Leg Opposite(List<Leg> legs, Leg a)
        {
            Leg best = null;
            double bestDot = 0;
            foreach (var b in legs)
            {
                if (b == a) continue;
                double dot = a.dir.x * b.dir.x + a.dir.y * b.dir.y;
                if (dot < bestDot) { bestDot = dot; best = b; }
            }
            return best;
        }

        /// <summary>The leg a right turn from `a` goes into: the one whose turn from `a` is most right.</summary>
        static Leg RightLeg(List<Leg> legs, Leg a)
        {
            Leg best = null;
            double bestAngle = 0;
            var arriving = new Vec2(-a.dir.x, -a.dir.y);
            foreach (var b in legs)
            {
                if (b == a) continue;
                double angle = Math.Atan2(arriving.x * b.dir.y - arriving.y * b.dir.x, arriving.x * b.dir.x + arriving.y * b.dir.y);
                if (angle > STRAIGHT && (best == null || angle > bestAngle)) { best = b; bestAngle = angle; }
            }
            return best;
        }

        static double Bend(Leg a, Leg b)
        {
            var arriving = new Vec2(-a.dir.x, -a.dir.y);
            return Math.Atan2(arriving.x * b.dir.y - arriving.y * b.dir.x, arriving.x * b.dir.x + arriving.y * b.dir.y);
        }

        /// <summary>A pocket at one end of a road, if the road's long enough for it and what's at its other end.</summary>
        static bool AddPocket(TownModel model, RoadDesign d, RoadEdge e, bool atTo, bool left, double length)
        {
            var ed = d.edges[e.id];
            if ((atTo ? ed.lanesFwd : ed.lanesBack) == 0) return false;
            double len = Geo.PolylineLength(e.points);
            double other = 0, here = 0;
            foreach (var p in ed.pockets)
            {
                if (p.atTo == atTo)
                {
                    if (p.left == left) return false;
                    here = Math.Max(here, p.length + TAPER);
                }
                else other = Math.Max(other, p.length + TAPER);
            }
            // A roundabout at the far end takes its ring and entry out of the road.
            int farNode = atTo ? e.from : e.to, nearNode = atTo ? e.to : e.from;
            var far = d.nodes[farNode];
            double farTrim = far.form == JunctionForm.Roundabout ? far.radius + 1.5 : 1.5;
            double room = len - farTrim - Math.Max(other, 0) - 1.5;
            length = Math.Min(length, room - TAPER);
            if (length < POCKET_MIN) return false;
            ed.pockets.Add(new Pocket { atTo = atTo, left = left, length = Math.Round(length * 10) / 10 });
            return true;
        }

        /// <summary>A roundabout where it fits: every leg longer than its ring and entry, and no more buildings in its way than allowed.</summary>
        static bool TryRoundabout(TownModel model, RoadDesign d, int node, List<Leg> legs, List<Building> buildings, HashSet<int> taken, bool insisted)
        {
            var roads = model.roads;
            var nd = d.nodes[node];
            bool main = false;
            foreach (var l in legs) if (l.rank == 3) main = true;
            double radius = main ? ROUNDABOUT_RADIUS : MINI_RADIUS;
            var at = roads.nodes[node].at;
            foreach (var l in legs)
            {
                var e = roads.edges[l.edge];
                double len = Geo.PolylineLength(e.points);
                int other = e.from == node ? e.to : e.from;
                double otherR = d.nodes[other].form == JunctionForm.Roundabout ? d.nodes[other].radius : 0;
                if (len < radius + otherR + 4)
                {
                    if (insisted) d.notes.Add(new DesignNote { at = at, text = "No room for a roundabout: a road out of it is too short" });
                    return false;
                }
            }
            // The HCM's entry capacity (single lane): c = 1380 e^(-0.00102 v_c), v_c the flow circulating past.
            foreach (var l in legs)
            {
                double vc = Math.Max(0, nd.entering - l.inFlow) * 0.5;
                double cap = 1380 * Math.Exp(-0.00102 * vc);
                if (l.inFlow > 0.85 * cap && !insisted) return false;
            }
            double reach = radius + 0.5 + Raster.SIDEWALK_WIDTH + 0.6;
            // No other road (a cul-de-sac's bulb, a street alongside) under its ring.
            foreach (var e in roads.edges)
            {
                if (e.from == node || e.to == node) continue;
                if (Geo.DistToPolyline(at, e.points) < radius + 0.9 + Raster.RoadReach(e.kind, model.@params))
                {
                    if (insisted) d.notes.Add(new DesignNote { at = at, text = "No room for a roundabout: another road runs too close" });
                    return false;
                }
            }
            {
                int w = model.width, h = model.height;
                var zoo = Menagerie.Mask(model.zoo, w, h, 1);
                var airport = Airfield.Mask(model.airport, w, h);
                for (int y = Math.Max(0, (int)(at.y - reach - 1)); y <= Math.Min(h - 1, (int)(at.y + reach + 1)); y++)
                    for (int x = Math.Max(0, (int)(at.x - reach - 1)); x <= Math.Min(w - 1, (int)(at.x + reach + 1)); x++)
                    {
                        int i = Grid.Index(x, y, w);
                        if ((zoo[i] != 0 || airport[i] != 0) && Numbers.Hypot(x + 0.5 - at.x, y + 0.5 - at.y) < reach + 0.5) return false;
                    }
            }
            var inWay = new List<int>();
            foreach (var b in buildings)
            {
                if (b.tiles == null || taken.Contains(b.id)) continue;
                foreach (var t in b.tiles)
                    if (Numbers.Hypot(t.x + 0.5 - at.x, t.y + 0.5 - at.y) < reach) { inWay.Add(b.id); break; }
            }
            // Only houses make way: a school, a shop or a church stays, and the junction keeps its signs.
            bool keeps = false;
            foreach (var b in buildings) if (inWay.Contains(b.id) && b.kind != BuildingKind.House) keeps = true;
            if (keeps)
            {
                if (insisted) d.notes.Add(new DesignNote { at = at, text = "No room for a roundabout: a building other than a house stands in its way" });
                return false;
            }
            if (inWay.Count > ROUNDABOUT_TAKES && !insisted)
            {
                d.notes.Add(new DesignNote { at = at, text = $"A roundabout would suit this junction, but {inWay.Count} buildings stand in its way" });
                return false;
            }
            foreach (int b in inWay) taken.Add(b);
            nd.radius = radius;
            // A pocket on any leg at this end makes no sense round a ring.
            foreach (var l in legs)
            {
                var e = roads.edges[l.edge];
                d.edges[l.edge].pockets.RemoveAll(p => p.atTo == (e.to == node));
            }
            return true;
        }

        /// <summary>A slip road round the corner from leg a to leg b, if no building's in its way.</summary>
        static bool TrySlip(TownModel model, RoadDesign d, NodeDesign nd, Leg a, Leg b, List<Building> buildings)
        {
            var roads = model.roads;
            var ea = roads.edges[a.edge];
            var eb = roads.edges[b.edge];
            if (Geo.PolylineLength(ea.points) < 10 || Geo.PolylineLength(eb.points) < 10) return false;
            var at = roads.nodes[nd.node].at;
            double wa = d.edges[a.edge].width, wb = d.edges[b.edge].width;
            // Out along each leg, on its right-hand side as a driver arriving on `a` and leaving on `b` sees it.
            var arriving = new Vec2(-a.dir.x, -a.dir.y);
            var rightA = new Vec2(-arriving.y, arriving.x);
            // RoadNet: positive cross is the driver's right, so the kerb side is the way that turn bends.
            if (Bend(a, b) < 0) rightA = new Vec2(-rightA.x, -rightA.y);
            var rightB = new Vec2(-b.dir.y, b.dir.x);
            if (rightB.x * rightA.x + rightB.y * rightA.y < 0) rightB = new Vec2(-rightB.x, -rightB.y);
            double back = 4.5, off = 0.6;
            var start = new Vec2(at.x + a.dir.x * back + rightA.x * (wa / 2 + off), at.y + a.dir.y * back + rightA.y * (wa / 2 + off));
            var end = new Vec2(at.x + b.dir.x * back + rightB.x * (wb / 2 + off), at.y + b.dir.y * back + rightB.y * (wb / 2 + off));
            var corner = new Vec2(at.x + (rightA.x + rightB.x) * 2.2, at.y + (rightA.y + rightB.y) * 2.2);
            var pts = new List<Vec2>();
            for (int i = 0; i <= 12; i++)
            {
                double t = i / 12.0, u = 1 - t;
                pts.Add(new Vec2(u * u * start.x + 2 * u * t * corner.x + t * t * end.x, u * u * start.y + 2 * u * t * corner.y + t * t * end.y));
            }
            if (BuildingsWithin(buildings, pts, 1.0).Count > 0) return false;
            nd.slips.Add(new Slip { fromEdge = a.edge, toEdge = b.edge, points = pts });
            return true;
        }
    }
}

// Stage 4: main roads. A* from each gate (and the airport, and the zoo's gate) to the center over the cost field,
// along the tile grid with a penalty per turn, so roads run straight. Cells
// already on a road are cheap and cells beside one are penalised, so later
// roads merge into earlier ones. Each stretch of new road becomes a graph edge;
// joins split the existing edge.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class RoadsStage : IStepwiseStage
    {
        const double REUSE_COST = 0.3;
        const double BESIDE_PENALTY = 3;
        const int BESIDE_RADIUS = 2;
        const double SIMPLIFY_EPSILON = 0.6;
        const double TURN_COST = 10;

        public string Name => "roads";

        public TownModel Run(TownModel model, Rng rng) => Stepwise.Drain<TownModel>(done => Steps(model, rng, done));

        /// <summary>The main roads, a route at a time (Run, all at once).</summary>
        public IEnumerable<string> Steps(TownModel model, Rng rng, Action<TownModel> done)
        {
            int w = model.width, h = model.height;
            var terrainInfo = Raster.RoadTerrain(model);
            var cost = terrainInfo.cost;
            var segmentOk = Raster.RoadSegmentOk(terrainInfo, model);
            yield return "/terrain";
            var onRoad = new byte[w * h];
            var beside = new byte[w * h];

            int centerCell = Grid.Index((int)Math.Floor(model.center.x), (int)Math.Floor(model.center.y), w);
            var nodeAt = new Dictionary<int, int>();
            var nodeCells = new List<int>();
            var edges = new List<List<int>>();
            var cellEdges = new Dictionary<int, List<int>>();

            List<int> Get(int c) => cellEdges.TryGetValue(c, out var l) ? l : new List<int>();
            int NewNode(int cell)
            {
                int id = nodeCells.Count;
                nodeCells.Add(cell);
                nodeAt[cell] = id;
                return id;
            }
            void MarkRoad(int c)
            {
                onRoad[c] = 1;
                int cx = c % w;
                int cy = (c - cx) / w;
                for (int dy = -BESIDE_RADIUS; dy <= BESIDE_RADIUS; dy++)
                    for (int dx = -BESIDE_RADIUS; dx <= BESIDE_RADIUS; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h) beside[Grid.Index(nx, ny, w)] = 1;
                    }
            }
            void IndexEdge(int e)
            {
                foreach (int c in edges[e])
                {
                    var next = new List<int>();
                    foreach (int x in Get(c)) if (x != e) next.Add(x);
                    next.Add(e);
                    cellEdges[c] = next;
                }
            }
            void AddEdgeCells(List<int> cells)
            {
                edges.Add(cells);
                IndexEdge(edges.Count - 1);
                foreach (int c in cells) MarkRoad(c);
            }
            int EnsureNode(int cell)
            {
                if (nodeAt.TryGetValue(cell, out int existing)) return existing;
                int id = NewNode(cell);
                foreach (int e in Get(cell))
                {
                    var cells = edges[e];
                    int k = cells.IndexOf(cell);
                    if (k <= 0 || k >= cells.Count - 1) continue;
                    edges[e] = cells.GetRange(0, k + 1);
                    edges.Add(cells.GetRange(k, cells.Count - k));
                    int added = edges.Count - 1;
                    for (int m = k + 1; m < cells.Count; m++)
                    {
                        int c = cells[m];
                        var mapped = new List<int>();
                        foreach (int x in Get(c)) mapped.Add(x == e ? added : x);
                        cellEdges[c] = mapped;
                    }
                    var withAdded = new List<int>(Get(cell)) { added };
                    cellEdges[cell] = withAdded;
                    break;
                }
                return id;
            }

            NewNode(centerCell);
            var gates = new List<int>();
            foreach (var g in model.gates) gates.Add(Grid.Index((int)Math.Floor(g.x), (int)Math.Floor(g.y), w));
            Numbers.StableSort(gates, (a, b) => AStar.Octile(a, centerCell, w) - AStar.Octile(b, centerCell, w));

            void Route(int from, Func<int, double> step, int goal = -1, double cheapest = REUSE_COST)
            {
                if (goal < 0) goal = centerCell;
                var path = AStar.FindGridPath(from, new GridSearch
                {
                    Width = w,
                    Height = h,
                    Step = (a, to) => step(to),
                    TurnCost = TURN_COST,
                    Goal = c => c == goal,
                    Estimate = c => AStar.Manhattan(c, goal, w) * cheapest,
                });
                if (path == null) return;
                int n = path.Count;
                int k = 0;
                while (k < n)
                {
                    if (onRoad[path[k]] != 0)
                    {
                        k++;
                        continue;
                    }
                    int end = k;
                    while (end + 1 < n && onRoad[path[end + 1]] == 0) end++;
                    int s = k == 0 ? 0 : k - 1;
                    int e = Math.Min(end + 1, n - 1);
                    EnsureNode(path[s]);
                    EnsureNode(path[e]);
                    AddEdgeCells(path.GetRange(s, e - s + 1));
                    k = end + 1;
                }
                EnsureNode(from);
            }
            double Step(int to) => onRoad[to] != 0 ? REUSE_COST : cost[to] + (beside[to] != 0 ? BESIDE_PENALTY : 0);
            foreach (int gate in gates)
            {
                Route(gate, Step);
                yield return "/gate";
            }
            // A big town's ring road, round the middle at RingRadius (TownPlan): the main roads' A*, from one
            // point on the circle to the next. It crosses the roads out from the centre rather than following them
            // (no discount for running along one), and keeps near its circle.
            double ring = TownPlan.RingRadius(model);
            if (ring > 0)
            {
                var stops = RingStops(model, ring, cost, rng);
                for (int k = 0; k < stops.Count; k++)
                {
                    var (fromCell, toCell) = (stops[k], stops[(k + 1) % stops.Count]);
                    Route(fromCell, to =>
                    {
                        if (float.IsPositiveInfinity(cost[to])) return double.PositiveInfinity;
                        double off = Math.Abs(Numbers.Hypot((to % w) + 0.5 - model.center.x, to / w + 0.5 - model.center.y) - ring);
                        return cost[to] + (beside[to] != 0 && onRoad[to] == 0 ? BESIDE_PENALTY : 0) + RING_PULL * off * off / (ring * ring);
                    }, toCell, 1);
                    yield return "/ring";
                }
            }
            // The road to the airport: out of its car park (straight across the airport's
            // own tiles, which the others go round) and on to the nearest road.
            if (model.airport != null)
            {
                var lane = Airfield.EntranceLane(model.airport, w, h);
                var inLane = new HashSet<int>(lane);
                // Running alongside another road is worse for this one: it would sooner join it.
                if (lane.Count > 0) Route(lane[0], to => inLane.Contains(to) ? 1 : onRoad[to] != 0 ? REUSE_COST : cost[to] + (beside[to] != 0 ? BESIDE_PENALTY * 4 : 0));
            }
            // And one up to the zoo's gate, from just past the tile kept clear round it.
            yield return "/airport";
            if (model.zoo != null)
            {
                var at = Menagerie.RoadEnd(model.zoo);
                int cell = Grid.Index((int)Math.Floor(at.x), (int)Math.Floor(at.y), w);
                if (Grid.InBounds((int)Math.Floor(at.x), (int)Math.Floor(at.y), w, h) && !float.IsPositiveInfinity(cost[cell])) Route(cell, Step);
            }

            yield return "/zoo";
            Vec2 Center(int c) => new Vec2((c % w) + 0.5, c / w + 0.5);
            var builder = new GraphBuilder();
            for (int id = 0; id < nodeCells.Count; id++) builder.nodes.Add(new RoadNode { id = id, at = Center(nodeCells[id]) });
            for (int id = 0; id < edges.Count; id++)
            {
                var cells = edges[id];
                var centers = new List<Vec2>(cells.Count);
                foreach (int c in cells) centers.Add(Center(c));
                builder.edges.Add(new RoadEdge
                {
                    id = id,
                    from = nodeAt[cells[0]],
                    to = nodeAt[cells[cells.Count - 1]],
                    kind = RoadKind.Main,
                    points = Geo.SimplifyRDP(centers, SIMPLIFY_EPSILON, segmentOk),
                });
            }
            yield return "/simplify";
            RoadGraphOps.Planarize(builder);
            PruneCenterStub(builder, Center(centerCell));
            var next = model.With();
            next.roads = RoadGraphOps.Compact(builder);
            next.bridges = RoadGraphOps.ComputeBridges(next, Raster.WaterMask(next));
            done(next);
        }

        /// <summary>Points round the ring road: RING_STOPS of them, each a dry tile on its circle (or as near it as there is one), clockwise.</summary>
        const int RING_STOPS = 8;
        /// <summary>The cost per tile of the ring straying a whole radius off its circle (the square of how far off, as a share of it).</summary>
        const double RING_PULL = 12;

        static List<int> RingStops(TownModel model, double ring, float[] cost, Rng rng)
        {
            int w = model.width, h = model.height;
            var o = new List<int>();
            var water = Raster.WaterMask(model);
            double turn = rng.Range(0, 2 * Math.PI / RING_STOPS);
            for (int k = 0; k < RING_STOPS; k++)
            {
                double a = turn + k * 2 * Math.PI / RING_STOPS;
                int found = -1;
                // Out or in along the spoke, nearest the circle first.
                for (int step = 0; step <= ring * 0.25 && found < 0; step++)
                    foreach (int sign in step == 0 ? new[] { 1 } : new[] { 1, -1 })
                    {
                        double r = ring + sign * step;
                        int x = (int)Math.Floor(model.center.x + DetMath.Cos(a) * r), y = (int)Math.Floor(model.center.y + DetMath.Sin(a) * r);
                        if (x < 2 || y < 2 || x >= w - 2 || y >= h - 2) continue;
                        int c = Grid.Index(x, y, w);
                        if (float.IsPositiveInfinity(cost[c]) || water[c] != 0) continue;
                        found = c;
                        break;
                    }
                if (found >= 0 && !o.Contains(found)) o.Add(found);
            }
            return o.Count >= 3 ? o : new List<int>();
        }

        /// <summary>The longest stub (tiles) left poking past a junction at the town centre.</summary>
        const double STUB = 3;

        /// <summary>
        /// The main roads all head for the centre's tile, so when one comes in beside it
        /// the others join it there and the last tile is left as a stub: a dead end too
        /// short for a car to turn round in. Drop it; the junction is the centre now.
        /// </summary>
        static void PruneCenterStub(GraphBuilder g, Vec2 center)
        {
            var degree = new Dictionary<int, int>();
            foreach (var e in RoadGraphOps.LiveEdges(g))
            {
                degree[e.from] = (degree.TryGetValue(e.from, out int a) ? a : 0) + 1;
                degree[e.to] = (degree.TryGetValue(e.to, out int b) ? b : 0) + 1;
            }
            foreach (var e in RoadGraphOps.LiveEdges(g))
            {
                int end = Geo.Dist(g.nodes[e.from].at, center) < 0.01 ? e.from : Geo.Dist(g.nodes[e.to].at, center) < 0.01 ? e.to : -1;
                if (end < 0 || degree[end] != 1) continue;
                int other = end == e.from ? e.to : e.from;
                if (degree[other] >= 3 && Geo.PolylineLength(e.points) <= STUB)
                {
                    g.dead.Add(e.id);
                    return;
                }
            }
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            var graph = model.roads;
            if (graph.nodes.Count == 0) return new List<string> { "no main roads were built" };
            int hub = RoadGraphOps.NearestNode(graph, model.center);
            var reached = RoadGraphOps.ReachableNodes(graph, hub);
            foreach (var g in model.gates)
            {
                int n = RoadGraphOps.NearestNode(graph, g);
                if (n < 0 || Numbers.Hypot(graph.nodes[n].at.x - g.x, graph.nodes[n].at.y - g.y) > 0.01 || !reached.Contains(n))
                    errors.Add($"gate {g.x},{g.y} is not connected to the center");
            }
            errors.AddRange(CheckRoadSlopes(model, RoadKind.Main));
            errors.AddRange(RoadGraphOps.CheckBridges(model, Raster.WaterMask(model)));
            errors.AddRange(AirportStage.CheckRoadIn(model));
            return errors;
        }

        /// <summary>No sampled point of a road of this kind sits on ground steeper than maxRoadSlope.</summary>
        public static List<string> CheckRoadSlopes(TownModel model, RoadKind kind)
        {
            var t = Raster.RoadTerrain(model);
            int w = model.width;
            // The airport's ground is levelled.
            var airport = Airfield.Mask(model.airport, w, model.height);
            foreach (var e in model.roads.edges)
            {
                if (e.kind != kind) continue;
                foreach (var p in Geo.SamplePolyline(e.points, 0.5))
                {
                    int i = Grid.Index((int)Math.Floor(p.x), (int)Math.Floor(p.y), w);
                    if (t.water[i] == 0 && airport[i] == 0 && t.slope[i] > model.@params.maxRoadSlope + 1e-6)
                        return new List<string> { $"{Names.Of(kind)} road {e.id} climbs a {Numbers.ToFixed(t.slope[i], 3)} slope at {Numbers.ToFixed(p.x, 1)},{Numbers.ToFixed(p.y, 1)}" };
                }
            }
            return new List<string>();
        }
    }
}

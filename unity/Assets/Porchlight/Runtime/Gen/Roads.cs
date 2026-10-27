// Stage 4: main roads. A* from each gate to the center over the cost field,
// along the tile grid with a penalty per turn, so roads run straight. Cells
// already on a road are cheap and cells beside one are penalised, so later
// roads merge into earlier ones. Each stretch of new road becomes a graph edge;
// joins split the existing edge. A port of gen/roads.ts.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class RoadsStage : IStage
    {
        const double REUSE_COST = 0.3;
        const double BESIDE_PENALTY = 3;
        const int BESIDE_RADIUS = 2;
        const double SIMPLIFY_EPSILON = 0.6;
        const double TURN_COST = 10;

        public string Name => "roads";

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var terrainInfo = Raster.RoadTerrain(model);
            var cost = terrainInfo.cost;
            var segmentOk = Raster.RoadSegmentOk(terrainInfo, model);
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
            Js.StableSort(gates, (a, b) => AStar.Octile(a, centerCell, w) - AStar.Octile(b, centerCell, w));

            foreach (int gate in gates)
            {
                var path = AStar.FindGridPath(gate, new GridSearch
                {
                    Width = w,
                    Height = h,
                    Step = (from, to) => onRoad[to] != 0 ? REUSE_COST : cost[to] + (beside[to] != 0 ? BESIDE_PENALTY : 0),
                    TurnCost = TURN_COST,
                    Goal = c => c == centerCell,
                    Estimate = c => AStar.Manhattan(c, centerCell, w) * REUSE_COST,
                });
                if (path == null) continue;
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
                EnsureNode(gate);
            }

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
            RoadGraphOps.Planarize(builder);
            var next = model.With();
            next.roads = RoadGraphOps.Compact(builder);
            next.bridges = RoadGraphOps.ComputeBridges(next, Raster.WaterMask(next));
            return next;
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
                if (n < 0 || Js.Hypot(graph.nodes[n].at.x - g.x, graph.nodes[n].at.y - g.y) > 0.01 || !reached.Contains(n))
                    errors.Add($"gate {g.x},{g.y} is not connected to the center");
            }
            errors.AddRange(CheckRoadSlopes(model, RoadKind.Main));
            errors.AddRange(RoadGraphOps.CheckBridges(model, Raster.WaterMask(model)));
            return errors;
        }

        /// <summary>No sampled point of a road of this kind sits on ground steeper than maxRoadSlope.</summary>
        public static List<string> CheckRoadSlopes(TownModel model, RoadKind kind)
        {
            var t = Raster.RoadTerrain(model);
            int w = model.width;
            foreach (var e in model.roads.edges)
            {
                if (e.kind != kind) continue;
                foreach (var p in Geo.SamplePolyline(e.points, 0.5))
                {
                    int i = Grid.Index((int)Math.Floor(p.x), (int)Math.Floor(p.y), w);
                    if (t.water[i] == 0 && t.slope[i] > model.@params.maxRoadSlope + 1e-6)
                        return new List<string> { $"{Names.Of(kind)} road {e.id} climbs a {Js.ToFixed(t.slope[i], 3)} slope at {Js.ToFixed(p.x, 1)},{Js.ToFixed(p.y, 1)}" };
                }
            }
            return new List<string>();
        }
    }
}

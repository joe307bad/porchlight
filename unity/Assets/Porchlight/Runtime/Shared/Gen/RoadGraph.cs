// Editing helpers for the road graph: adding and splitting edges, making the
// graph planar (every crossing becomes a node), merging pass-through nodes,
// and deriving bridges wherever a road runs over water.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    /// <summary>A mutable graph under construction; edges in `dead` are ignored and dropped by Compact().</summary>
    public sealed class GraphBuilder
    {
        public List<RoadNode> nodes = new List<RoadNode>();
        public List<RoadEdge> edges = new List<RoadEdge>();
        public HashSet<int> dead = new HashSet<int>();
    }

    public static class RoadGraphOps
    {
        public static GraphBuilder BuilderFrom(RoadGraph graph)
        {
            var g = new GraphBuilder();
            foreach (var n in graph.nodes) g.nodes.Add(new RoadNode { id = n.id, at = n.at });
            foreach (var e in graph.edges) g.edges.Add(new RoadEdge { id = e.id, from = e.from, to = e.to, kind = e.kind, points = new List<Vec2>(e.points) });
            return g;
        }

        public static List<RoadEdge> LiveEdges(GraphBuilder g)
        {
            var o = new List<RoadEdge>();
            foreach (var e in g.edges) if (!g.dead.Contains(e.id)) o.Add(e);
            return o;
        }

        /// <summary>Add a node, or reuse one within `snap` tiles.</summary>
        public static int AddNode(GraphBuilder g, Vec2 at, double snap = 1e-6)
        {
            foreach (var n in g.nodes) if (Geo.Dist(n.at, at) <= snap) return n.id;
            int id = g.nodes.Count;
            g.nodes.Add(new RoadNode { id = id, at = at });
            return id;
        }

        public static int AddEdge(GraphBuilder g, int from, int to, RoadKind kind, List<Vec2> points = null)
        {
            int id = g.edges.Count;
            var pts = points ?? new List<Vec2> { g.nodes[from].at, g.nodes[to].at };
            pts[0] = g.nodes[from].at;
            pts[pts.Count - 1] = g.nodes[to].at;
            g.edges.Add(new RoadEdge { id = id, from = from, to = to, kind = kind, points = pts });
            return id;
        }

        /// <summary>Split an edge at the point closest to `at`, returning the node there.</summary>
        public static int SplitEdgeAt(GraphBuilder g, int edgeId, Vec2 at, double snap = 0.05)
        {
            var edge = g.edges[edgeId];
            var c = Geo.ClosestOnPolyline(at, edge.points);
            if (Geo.Dist(c.point, g.nodes[edge.from].at) <= snap) return edge.from;
            if (Geo.Dist(c.point, g.nodes[edge.to].at) <= snap) return edge.to;
            int node = AddNode(g, c.point);
            var first = new List<Vec2>();
            for (int k = 0; k <= c.seg; k++) first.Add(edge.points[k]);
            first.Add(c.point);
            var second = new List<Vec2> { c.point };
            for (int k = c.seg + 1; k < edge.points.Count; k++) second.Add(edge.points[k]);
            g.dead.Add(edgeId);
            AddEdge(g, edge.from, node, edge.kind, Dedupe(first));
            AddEdge(g, node, edge.to, edge.kind, Dedupe(second));
            return node;
        }

        static List<Vec2> Dedupe(List<Vec2> pts)
        {
            var o = new List<Vec2>();
            foreach (var p in pts) if (o.Count == 0 || Geo.Dist(o[o.Count - 1], p) > 1e-9) o.Add(p);
            if (o.Count == 1) o.Add(o[0]);
            return o;
        }

        /// <summary>Live edges incident to each node (in first-seen order).</summary>
        public static Dictionary<int, List<int>> Incidence(GraphBuilder g)
        {
            var o = new Dictionary<int, List<int>>();
            foreach (var e in LiveEdges(g))
            {
                foreach (int n in new[] { e.from, e.to })
                {
                    if (o.TryGetValue(n, out var list)) list.Add(e.id);
                    else o[n] = new List<int> { e.id };
                }
            }
            return o;
        }

        struct Seg
        {
            public int e, s;
            public Vec2 a, b;
            public double x0, y0, x1, y1;
        }

        struct Cut
        {
            public double pos;
            public int node;
        }

        /// <summary>Insert a node at every place two edges cross, so the graph is planar.</summary>
        public static void Planarize(GraphBuilder g)
        {
            var segs = new List<Seg>();
            foreach (var e in LiveEdges(g))
            {
                for (int s = 0; s + 1 < e.points.Count; s++)
                {
                    var a = e.points[s];
                    var b = e.points[s + 1];
                    segs.Add(new Seg { e = e.id, s = s, a = a, b = b, x0 = Math.Min(a.x, b.x), y0 = Math.Min(a.y, b.y), x1 = Math.Max(a.x, b.x), y1 = Math.Max(a.y, b.y) });
                }
            }
            var cuts = new Dictionary<int, List<Cut>>();
            var cutOrder = new List<int>();
            void PushCut(int e, double pos, int node)
            {
                if (cuts.TryGetValue(e, out var list)) list.Add(new Cut { pos = pos, node = node });
                else
                {
                    cuts[e] = new List<Cut> { new Cut { pos = pos, node = node } };
                    cutOrder.Add(e);
                }
            }
            int EndpointNode(RoadEdge edge, double pos)
            {
                if (pos < 1e-7) return edge.from;
                if (pos > edge.points.Count - 1 - 1e-7) return edge.to;
                return -1;
            }
            for (int i = 0; i < segs.Count; i++)
            {
                var A = segs[i];
                for (int j = i + 1; j < segs.Count; j++)
                {
                    var B = segs[j];
                    if (B.x0 > A.x1 || B.x1 < A.x0 || B.y0 > A.y1 || B.y1 < A.y0) continue;
                    if (A.e == B.e && Math.Abs(A.s - B.s) <= 1) continue;
                    var hit = Geo.SegmentIntersection(A.a, A.b, B.a, B.b);
                    if (hit == null) continue;
                    var x = hit.Value;
                    var ea = g.edges[A.e];
                    var eb = g.edges[B.e];
                    double posA = A.s + x.t;
                    double posB = B.s + x.u;
                    int na = EndpointNode(ea, posA);
                    int nb = EndpointNode(eb, posB);
                    if (na >= 0 && nb >= 0) continue;
                    if (A.e == B.e && ea.from == ea.to && (na >= 0 || nb >= 0)) continue;
                    int node = na >= 0 ? na : nb >= 0 ? nb : AddNode(g, x.point, 0.05);
                    if (na < 0) PushCut(A.e, posA, node);
                    if (nb < 0) PushCut(B.e, posB, node);
                }
            }
            foreach (int edgeId in cutOrder)
            {
                var list = cuts[edgeId];
                var edge = g.edges[edgeId];
                Numbers.StableSort(list, (a, b) => a.pos - b.pos);
                int startNode = edge.from;
                double startPos = 0;
                var startPt = edge.points[0];
                var pieces = new List<(int from, int to, List<Vec2> pts)>();
                var all = new List<Cut>(list) { new Cut { pos = edge.points.Count - 1, node = edge.to } };
                foreach (var cut in all)
                {
                    if (cut.node == startNode && cut.pos - startPos < 1e-6) continue;
                    var pts = new List<Vec2> { startPt };
                    for (int k = (int)Math.Floor(startPos) + 1; k < cut.pos; k++) pts.Add(edge.points[k]);
                    var end = g.nodes[cut.node].at;
                    pts.Add(end);
                    pieces.Add((startNode, cut.node, Dedupe(pts)));
                    startNode = cut.node;
                    startPos = cut.pos;
                    startPt = end;
                }
                g.dead.Add(edgeId);
                foreach (var piece in pieces)
                {
                    if (piece.from == piece.to && piece.pts.Count <= 2) continue;
                    AddEdge(g, piece.from, piece.to, edge.kind, piece.pts);
                }
            }
        }

        /// <summary>Merge chains through nodes of degree 2 (same kind on both sides) into single edges.</summary>
        public static void MergePassThrough(GraphBuilder g, ISet<int> keep)
        {
            var inc = Incidence(g);
            var nodes = new List<int>(inc.Keys);
            foreach (int node in nodes)
            {
                var list = inc[node];
                if (keep.Contains(node) || list.Count != 2) continue;
                var e1 = g.edges[list[0]];
                var e2 = g.edges[list[1]];
                if (e1.id == e2.id || e1.kind != e2.kind) continue;
                var a = Orient(e1, node, true);
                var b = Orient(e2, node, false);
                int from = a.from;
                int to = b.to;
                if (from == to) continue;
                g.dead.Add(e1.id);
                g.dead.Add(e2.id);
                var pts = new List<Vec2>(a.points);
                for (int k = 1; k < b.points.Count; k++) pts.Add(b.points[k]);
                int id = AddEdge(g, from, to, e1.kind, pts);
                foreach (var (n, old) in new[] { (from, e1.id), (to, e2.id) })
                {
                    var l = inc[n];
                    int ix = l.IndexOf(old);
                    if (ix >= 0) l[ix] = id;
                }
                inc[node] = new List<int>();
            }
        }

        static (int from, int to, List<Vec2> points) Orient(RoadEdge e, int node, bool end)
        {
            bool forward = end ? e.to == node : e.from == node;
            if (forward) return (e.from, e.to, e.points);
            var rev = new List<Vec2>(e.points);
            rev.Reverse();
            return (e.to, e.from, rev);
        }

        /// <summary>Drop dead edges and orphan nodes, then renumber ids densely.</summary>
        public static RoadGraph Compact(GraphBuilder g)
        {
            var edges = new List<RoadEdge>();
            foreach (var e in LiveEdges(g)) if (e.from != e.to || e.points.Count > 2) edges.Add(e);
            var used = new Dictionary<int, int>();
            var seen = new List<int>();
            foreach (var e in edges)
                foreach (int n in new[] { e.from, e.to })
                    if (!used.ContainsKey(n))
                    {
                        used[n] = 0;
                        seen.Add(n);
                    }
            seen.Sort();
            for (int i = 0; i < seen.Count; i++) used[seen[i]] = i;
            var graph = new RoadGraph();
            for (int i = 0; i < seen.Count; i++) graph.nodes.Add(new RoadNode { id = i, at = g.nodes[seen[i]].at });
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                graph.edges.Add(new RoadEdge { id = i, from = used[e.from], to = used[e.to], kind = e.kind, points = e.points });
            }
            return graph;
        }

        const double BRIDGE_SAMPLE = 0.25;

        /// <summary>One bridge per stretch of road over water; from/to lie on the road just outside the water.</summary>
        public static List<Bridge> ComputeBridges(TownModel model, byte[] water)
        {
            int w = model.width, h = model.height;
            bool Wet(Vec2 p)
            {
                int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y);
                return x >= 0 && y >= 0 && x < w && y < h && water[y * w + x] == 1;
            }
            var bridges = new List<Bridge>();
            foreach (var edge in model.roads.edges)
            {
                var samples = Geo.SamplePolyline(edge.points, BRIDGE_SAMPLE);
                int start = -1;
                for (int k = 0; k <= samples.Count; k++)
                {
                    bool isWet = k < samples.Count && Wet(samples[k]);
                    if (isWet && start < 0) start = k;
                    if (!isWet && start >= 0)
                    {
                        var from = samples[Math.Max(0, start - 1)];
                        var to = samples[Math.Min(samples.Count - 1, k)];
                        bridges.Add(new Bridge { id = bridges.Count, roadEdgeId = edge.id, from = from, to = to });
                        start = -1;
                    }
                }
            }
            return bridges;
        }

        /// <summary>Every stretch of road over water must lie on a bridge of that edge.</summary>
        public static List<string> CheckBridges(TownModel model, byte[] water)
        {
            int w = model.width;
            var errors = new List<string>();
            var byEdge = new Dictionary<int, List<Bridge>>();
            foreach (var b in model.bridges)
            {
                if (!byEdge.TryGetValue(b.roadEdgeId, out var l)) byEdge[b.roadEdgeId] = l = new List<Bridge>();
                l.Add(b);
            }
            foreach (var edge in model.roads.edges)
            {
                var spans = new List<(double, double)>();
                if (byEdge.TryGetValue(edge.id, out var list))
                    foreach (var b in list) spans.Add((Geo.ArcPosition(b.from, edge.points), Geo.ArcPosition(b.to, edge.points)));
                foreach (var p in Geo.SamplePolyline(edge.points, BRIDGE_SAMPLE))
                {
                    if (water[(int)Math.Floor(p.y) * w + (int)Math.Floor(p.x)] == 0) continue;
                    double at = Geo.ArcPosition(p, edge.points);
                    bool covered = false;
                    foreach (var (a, b) in spans) if (at >= Math.Min(a, b) - 1e-6 && at <= Math.Max(a, b) + 1e-6) covered = true;
                    if (!covered)
                    {
                        errors.Add($"road {edge.id} crosses water at {Numbers.ToFixed(p.x, 1)},{Numbers.ToFixed(p.y, 1)} without a bridge");
                        break;
                    }
                }
            }
            return errors;
        }

        /// <summary>Node ids reachable from `start` over the graph.</summary>
        public static HashSet<int> ReachableNodes(RoadGraph graph, int start)
        {
            var adj = new Dictionary<int, List<int>>();
            foreach (var e in graph.edges)
            {
                if (!adj.TryGetValue(e.from, out var a)) adj[e.from] = a = new List<int>();
                a.Add(e.to);
                if (!adj.TryGetValue(e.to, out var b)) adj[e.to] = b = new List<int>();
                b.Add(e.from);
            }
            var seen = new HashSet<int> { start };
            var stack = new List<int> { start };
            while (stack.Count > 0)
            {
                int top = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (!adj.TryGetValue(top, out var next)) continue;
                foreach (int n in next)
                {
                    if (seen.Add(n)) stack.Add(n);
                }
            }
            return seen;
        }

        public static int NearestNode(RoadGraph graph, Vec2 at)
        {
            int best = -1;
            double bestD = double.PositiveInfinity;
            foreach (var n in graph.nodes)
            {
                double d = Geo.Dist(n.at, at);
                if (d < bestD)
                {
                    bestD = d;
                    best = n.id;
                }
            }
            return best;
        }
    }
}

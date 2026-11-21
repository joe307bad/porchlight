// Editing helpers for the road graph: adding and splitting edges, making the
// graph planar (every crossing becomes a node), merging pass-through nodes,
// and deriving bridges wherever a road runs over water.

using System;
using System.Collections.Generic;
using System.Linq;
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
            foreach (var e in graph.edges) g.edges.Add(new RoadEdge { id = e.id, from = e.from, to = e.to, kind = e.kind, cls = e.cls, points = new List<Vec2>(e.points) });
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

        public static int AddEdge(GraphBuilder g, int from, int to, RoadKind kind, List<Vec2> points = null, RoadClass cls = RoadClass.Local)
        {
            int id = g.edges.Count;
            var pts = points ?? new List<Vec2> { g.nodes[from].at, g.nodes[to].at };
            pts[0] = g.nodes[from].at;
            pts[pts.Count - 1] = g.nodes[to].at;
            g.edges.Add(new RoadEdge { id = id, from = from, to = to, kind = kind, cls = cls, points = pts });
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
            AddEdge(g, edge.from, node, edge.kind, Dedupe(first), edge.cls);
            AddEdge(g, node, edge.to, edge.kind, Dedupe(second), edge.cls);
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
                    AddEdge(g, piece.from, piece.to, edge.kind, piece.pts, edge.cls);
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
                int id = AddEdge(g, from, to, e1.kind, pts, e1.cls > e2.cls ? e1.cls : e2.cls);
                foreach (var (n, old) in new[] { (from, e1.id), (to, e2.id) })
                {
                    var l = inc[n];
                    int ix = l.IndexOf(old);
                    if (ix >= 0) l[ix] = id;
                }
                inc[node] = new List<int>();
            }
        }

        /// <summary>
        /// The nearest two junctions may be along a road (tiles, centre to centre): nearer, and they're one junction
        /// run apart into two, with a stretch between too short to queue in (a car's 1.1 tiles long; a junction's
        /// mouth, JUNCTION_REACH, 4). osm2streets' CollapseShortRoads and CollapseDegenerateIntersections do this to
        /// OpenStreetMap's streets [T51]: a road shorter than a junction's own size is merged into it.
        /// </summary>
        public const double JUNCTIONS_APART = 4;

        /// <summary>
        /// One junction where roads were run into a knot: two roads between the same two junctions (the shorter's
        /// kept), a road that comes back to where it began (dropped), a node that only joins two roads (merged
        /// through), and two junctions nearer along a road than `apart` (the one's roads moved onto the other: a
        /// pinned one stays where it is, else the pair meet half way). Until none's left.
        /// </summary>
        /// <summary>
        /// A link between two junctions that makes them one knot: shorter than `apart` along it; or, with `bent`
        /// (main roads), its ends closer than ACROSS × `apart` as the crow flies however it winds between them, or a
        /// dog-leg (a bend in it) shorter than twice `apart`. Ember Crossing's centre was the last two: a T, a
        /// six-tile dog-leg, and another T four tiles across from the first.
        /// </summary>
        public static bool Knotted(List<Vec2> pts, double apart, bool bent)
        {
            double length = Geo.PolylineLength(pts);
            if (length < apart) return true;
            if (!bent) return false;
            if (Geo.Dist(pts[0], pts[pts.Count - 1]) < ACROSS * apart) return true;
            return length < 2 * apart && Bends(pts) > 0;
        }

        /// <summary>A main road leaves a junction straight for at least this far (tiles) before it bends.</summary>
        public const double MOUTH = 3.5;

        /// <summary>
        /// A step at a junction's mouth: the road (from its end at the junction: `atEnd`, its last point, else its
        /// first) turns within MOUTH of it and then turns back the way it set off, sidestepping as it leaves. A
        /// single corner near a junction is a road turning off, and is fine.
        /// </summary>
        public static bool StepAtMouth(List<Vec2> pts, bool atEnd)
        {
            var p = atEnd ? Enumerable.Reverse(pts).ToList() : pts;
            double run = 0;
            Vec2 first = default;
            bool turned = false;
            for (int k = 1; k + 1 < p.Count; k++)
            {
                run += Geo.Dist(p[k - 1], p[k]);
                var a = Geo.Sub(p[k], p[k - 1]);
                var b = Geo.Sub(p[k + 1], p[k]);
                double la = Math.Sqrt(Geo.Dot(a, a)), lb = Math.Sqrt(Geo.Dot(b, b));
                if (la < 1e-9 || lb < 1e-9 || Geo.Dot(a, b) / (la * lb) >= 0.866) continue;
                if (!turned)
                {
                    // Round the corner in the junction itself (it turns there, it doesn't step aside).
                    if (run < 1) continue;
                    if (run >= MOUTH - 1e-6) return false;
                    turned = true;
                    first = new Vec2(a.x / la, a.y / la);
                    continue;
                }
                // The second corner: back to the way it set off (within 30°) is a step; on round is a U, a road's
                // own business.
                return Geo.Dot(first, new Vec2(b.x / lb, b.y / lb)) > 0.866;
            }
            return false;
        }

        /// <summary>Junctions on main roads no nearer than this many times JUNCTIONS_APART as the crow flies.</summary>
        public const double ACROSS = 1;

        public static void CollapseShortLinks(GraphBuilder g, ISet<int> pinned, double apart, Func<List<Vec2>, List<Vec2>, bool> lineOk, int centre = -1)
        {
            var tried = new HashSet<int>();
            for (int pass = 0; pass < 200; pass++)
            {
                bool changed = DropDoubles(g) | DropNewDeadEnds(g, pinned, centre);
                if (ZipSideBySide(g, pinned, centre, lineOk)) changed = true;
                if (StraightenJogs(g, pinned, centre, lineOk)) changed = true;
                if (StraightenMouths(g, lineOk, centre)) changed = true;
                if (changed) MergePassThrough(g, centre >= 0 ? new HashSet<int>(pinned) { centre } : pinned);
                var inc = Incidence(g);
                int Degree(int n) => inc.TryGetValue(n, out var l) ? l.Count : 0;
                // A junction: three roads or more, or the town's centre where two meet (it's where they all head).
                bool Junction(int n) => Degree(n) >= 3 || ((pinned.Contains(n) || n == centre) && Degree(n) >= 2);
                RoadEdge best = null;
                double bestLength = double.MaxValue;
                foreach (var e in LiveEdges(g))
                {
                    if (tried.Contains(e.id) || !Junction(e.from) || !Junction(e.to) || (pinned.Contains(e.from) && pinned.Contains(e.to))) continue;
                    if (!Knotted(e.points, apart, true)) continue;
                    double length = Geo.PolylineLength(e.points);
                    if (length < bestLength) { best = e; bestLength = length; }
                }
                if (best == null)
                {
                    if (!changed) return;
                    continue;
                }
                tried.Add(best.id);
                // Where the one junction goes: kept at either end (the centre's, if it's one), else half way.
                var tries = new List<(int keep, int gone, Vec2 at)>();
                // The centre's kept where it is if it can be; else it goes to the other.
                foreach (var (k, o) in new[] { (best.from, best.to), (best.to, best.from) }.OrderBy(t => t.Item2 == centre ? 1 : 0))
                    if (!pinned.Contains(o)) tries.Add((k, o, g.nodes[k].at));
                if (!pinned.Contains(best.from) && !pinned.Contains(best.to))
                    tries.Add((best.from, best.to, Geo.Lerp(g.nodes[best.from].at, g.nodes[best.to].at, 0.5)));
                int keep = -1, gone = -1;
                Vec2 at = default;
                Dictionary<int, List<Vec2>> moved = null;
                // Each way of moving its roads: the whole road swung to it, its last stretch bent, or its end squared
                // off along the tiles (an L).
                double sharpest = Sharpest(g, inc, best.from, best.id, -1);
                sharpest = Math.Max(sharpest, Sharpest(g, inc, best.to, best.id, -1));
                // At 60° or more if it can (AASHTO's least); failing that a fork as sharp as KNOT_SHARPEST, which is
                // still one junction, not a knot of them.
                foreach (double least in new[] { COS_SHARPEST, COS_KNOT })
                {
                    foreach (int bend in new[] { 0, 1, 2 })
                    {
                        foreach (var t in tries)
                        {
                            var m = Moved(g, inc, best.id, t.keep, t.gone, t.at, lineOk, bend, Math.Max(sharpest, least));
                            if (m == null) continue;
                            (keep, gone, at, moved) = (t.keep, t.gone, t.at, m);
                            break;
                        }
                        if (moved != null) break;
                    }
                    if (moved != null) break;
                }
                if (moved == null)
                {
                    // It can't be one junction: two of its roads head off the same way (a fork), or there'd be five.
                    // One of them goes, if the town's still joined up without it (gates to the centre).
                    if (DropRedundantLeg(g, inc, best, pinned, centre)) tried.Clear();
                    continue;
                }
                g.dead.Add(best.id);
                foreach (int id in inc[keep].Concat(inc[gone])) if (!moved.ContainsKey(id)) g.dead.Add(id);
                g.nodes[keep].at = at;
                if (gone == centre) centre = keep;
                foreach (var (id, pts) in moved)
                {
                    var e = g.edges[id];
                    if (e.from == gone) e.from = keep;
                    if (e.to == gone) e.to = keep;
                    e.points = pts;
                }
                tried.Clear();
            }
        }

        /// <summary>
        /// The roads at junctions `keep` and `gone` (but `link`, between them) as they'd be, ending at the one junction at
        /// `at`; null if one would cross land a road can't, or two would meet there at a sharp angle.
        /// </summary>
        static Dictionary<int, List<Vec2>> Moved(GraphBuilder g, Dictionary<int, List<int>> inc, int link, int keep, int gone, Vec2 at, Func<List<Vec2>, List<Vec2>, bool> lineOk, int bend, double sharpestBefore)
        {
            var moved = new Dictionary<int, List<Vec2>>();
            var heads = new List<Vec2>();
            // Two roads on to the same junction would be one too many: the longer goes (DropDoubles, after).
            var toFar = new Dictionary<int, int>();
            foreach (int id in inc[keep].Concat(inc[gone]))
            {
                if (id == link) continue;
                var e = g.edges[id];
                int far = e.from == keep || e.from == gone ? e.to : e.from;
                if (far == keep || far == gone) continue;
                if (!toFar.TryGetValue(far, out int other) || Geo.PolylineLength(e.points) < Geo.PolylineLength(g.edges[other].points)) toFar[far] = id;
            }
            var staying = new HashSet<int>(toFar.Values);
            foreach (int id in inc[keep].Concat(inc[gone]))
            {
                if (id == link || moved.ContainsKey(id)) continue;
                var e = g.edges[id];
                bool loop = (e.from == keep || e.from == gone) && (e.to == keep || e.to == gone);
                if (!loop && !staying.Contains(id)) continue;
                bool atStart = e.from == keep || e.from == gone, atEnd = e.to == keep || e.to == gone;
                var pts = new List<Vec2>(e.points);
                // Only its last stretch bends to the junction: it keeps its way till BEND_BACK short of where it
                // ended (or, if it doesn't move, it's as it was).
                if (bend == 0)
                {
                    if (atStart) pts[0] = at;
                    if (atEnd) pts[pts.Count - 1] = at;
                }
                else if (bend == 2)
                {
                    if (atEnd && Geo.Dist(pts[pts.Count - 1], at) > 1e-6) pts = SquareEnd(pts, at);
                    if (atStart && Geo.Dist(pts[0], at) > 1e-6)
                    {
                        pts.Reverse();
                        pts = SquareEnd(pts, at);
                        pts.Reverse();
                    }
                }
                else
                {
                    if (atEnd && Geo.Dist(pts[pts.Count - 1], at) > 1e-6) pts = BendEnd(pts, at);
                    if (atStart && Geo.Dist(pts[0], at) > 1e-6)
                    {
                        pts.Reverse();
                        pts = BendEnd(pts, at);
                        pts.Reverse();
                    }
                }
                if (loop) continue;
                if (!lineOk(e.points, pts)) return null;
                // Out of the junction straight: no step right at its mouth (unless the road already had one there).
                if ((atStart && StepAtMouth(pts, false) && !StepAtMouth(e.points, false)) || (atEnd && StepAtMouth(pts, true) && !StepAtMouth(e.points, true))) return null;
                if (atStart) heads.Add(Heading(pts, false));
                if (atEnd) heads.Add(Heading(pts, true));
                moved[id] = pts;
            }
            // Four legs at most: a crossroads, not a star (MUTCD 2B, A/B Street: five-way junctions are their own trouble).
            if (heads.Count > 4) return null;
            // No two meeting sharply: under 60°, unless the pair were as sharp already (it's no worse for being one).
            double limit = Math.Max(COS_SHARPEST, sharpestBefore);
            for (int a = 0; a < heads.Count; a++)
                for (int b = a + 1; b < heads.Count; b++)
                    if (Geo.Dot(heads[a], heads[b]) > limit) return null;
            return moved;
        }

        /// <summary>A road ending in nothing that the clean-up left (every dead end before it is pinned): gone. Whether any went.</summary>
        static bool DropNewDeadEnds(GraphBuilder g, ISet<int> pinned, int centre)
        {
            bool changed = false;
            for (bool again = true; again;)
            {
                again = false;
                var inc = Incidence(g);
                foreach (var (node, list) in inc)
                {
                    if (list.Count != 1 || pinned.Contains(node)) continue;
                    // The centre at the end of a stub off a junction: the stub goes, and the junction's the centre's
                    // (a dead end into the middle of town is no use to anyone). Never more than the stub.
                    if (node == centre)
                    {
                        var e = g.edges[list[0]];
                        int other = e.from == node ? e.to : e.from;
                        if (!inc.TryGetValue(other, out var at) || at.Count < 3) continue;
                    }
                    g.dead.Add(list[0]);
                    again = changed = true;
                    break;
                }
            }
            return changed;
        }

        /// <summary>How near (tiles, centre to centre) two roads out of a junction run for them to be side by side.</summary>
        const double SIDE_BY_SIDE = 3.5;

        /// <summary>
        /// Two roads that leave a junction the same way, side by side, and only part a few tiles on: the one that parts
        /// first is cut back to where they part and joined to the other there (a T), so there's one road where they
        /// ran together. Whether one was. (Then the short stretch from the junction to the T is a knot like any other.)
        /// </summary>
        static bool ZipSideBySide(GraphBuilder g, ISet<int> pinned, int centre, Func<List<Vec2>, List<Vec2>, bool> lineOk)
        {
            var inc = Incidence(g);
            foreach (var (node, list) in inc)
            {
                if (list.Count < 3 && node != centre) continue;
                for (int i = 0; i < list.Count; i++)
                    for (int j = 0; j < list.Count; j++)
                    {
                        if (i == j || list[i] == list[j]) continue;
                        var a = g.edges[list[i]];
                        var b = g.edges[list[j]];
                        var pa = Away(a, node);
                        var pb = Densify(Away(b, node));
                        if (Geo.Dot(Heading(pa, false, ZIP_HEADING_OVER), Heading(pb, false, ZIP_HEADING_OVER)) <= COS_SHARPEST) continue;
                        // Where b leaves a: the last of b's points still beside a, and the place on a beside it (its
                        // far end, if b runs beside it all the way: the two meet there).
                        int k = 0;
                        while (k + 1 < pb.Count && Geo.DistToPolyline(pb[k + 1], pa) <= SIDE_BY_SIDE) k++;
                        if (k < 2) continue;
                        Vec2 at;
                        List<Vec2> rest;
                        if (k == pb.Count - 1)
                        {
                            // Beside a all the way to its end (a dead end: the zoo's gate, say): it leaves a by a spur
                            // across, as near that end as the land lets it, and keeps its own way on from there.
                            at = default;
                            rest = null;
                            for (int kk = k; kk >= 2 && rest == null; kk--)
                            {
                                var on = Geo.ClosestOnPolyline(pb[kk], pa).point;
                                if (Geo.Dist(on, pb[kk]) < 0.5 || Geo.ArcPosition(pb[kk], pa) < 0.5) continue;
                                var tryRest = new List<Vec2> { on };
                                for (int m = kk; m < pb.Count; m++) tryRest.Add(pb[m]);
                                tryRest = Geo.SimplifyRDP(tryRest, 0.01);
                                if (!lineOk(pb, tryRest)) continue;
                                at = on;
                                rest = tryRest;
                            }
                            if (rest == null) continue;
                        }
                        else
                        {
                            if (Geo.ArcPosition(pb[k], pa) < 0.5) continue;
                            at = Geo.ClosestOnPolyline(pb[k], pa).point;
                            rest = Rest(pb, k, at);
                        }
                        if (!lineOk(pb, rest)) continue;
                        int far = b.from == node ? b.to : b.from;
                        int t = SplitEdgeAt(g, a.id, at, 0.05);
                        if (t == node || t == far) continue;
                        g.dead.Add(b.id);
                        // Back the way it was drawn, from its far end to the T.
                        if (b.to == node) AddEdge(g, t, far, b.kind, rest, b.cls);
                        else
                        {
                            rest.Reverse();
                            AddEdge(g, far, t, b.kind, rest, b.cls);
                        }
                        return true;
                    }
            }
            // Across a knot: one road out of each of two junctions a knotted link joins, side by side the same way
            // (two main roads laid a tile or two apart into it). The second is cut where it leaves the first, and joins
            // it there as a T; what's left of the knot is then one junction fewer legs.
            foreach (var link in LiveEdges(g).ToList())
            {
                if (link.from == link.to || !Knotted(link.points, JUNCTIONS_APART, true)) continue;
                foreach (var (n1, n2) in new[] { (link.from, link.to), (link.to, link.from) })
                {
                    if (!inc.TryGetValue(n1, out var l1) || !inc.TryGetValue(n2, out var l2)) continue;
                    foreach (int ia in l1)
                        foreach (int ib in l2)
                        {
                            if (ia == link.id || ib == link.id || ia == ib) continue;
                            var a = g.edges[ia];
                            var b = g.edges[ib];
                            if (g.dead.Contains(a.id) || g.dead.Contains(b.id)) continue;
                            var pa = Away(a, n1);
                            var pb = Densify(Away(b, n2));
                            if (Geo.Dot(Heading(pa, false, ZIP_HEADING_OVER), Heading(pb, false, ZIP_HEADING_OVER)) <= COS_SHARPEST) continue;
                            // b's run beside a: from the first of its points beside it to the last before it leaves.
                            int k0 = 0;
                            while (k0 < pb.Count && Geo.DistToPolyline(pb[k0], pa) > SIDE_BY_SIDE) k0++;
                            if (k0 >= pb.Count - 1 || k0 > 2) continue;
                            int k = k0;
                            while (k + 1 < pb.Count && Geo.DistToPolyline(pb[k + 1], pa) <= SIDE_BY_SIDE) k++;
                            if (k == pb.Count - 1 || k - k0 < 2) continue;
                            if (Geo.ArcPosition(pb[k], pa) < 0.5) continue;
                            var at = Geo.ClosestOnPolyline(pb[k], pa).point;
                            var rest = Rest(pb, k, at);
                            if (!lineOk(pb, rest)) continue;
                            int far = b.from == n2 ? b.to : b.from;
                            int t = SplitEdgeAt(g, a.id, at, 0.05);
                            if (t == n1 || t == far || t == n2) continue;
                            g.dead.Add(b.id);
                            if (b.to == n2) AddEdge(g, t, far, b.kind, rest, b.cls);
                            else
                            {
                                rest.Reverse();
                                AddEdge(g, far, t, b.kind, rest, b.cls);
                            }
                            return true;
                        }
                }
            }
            return false;
        }

        /// <summary>A road's points a tile apart at most (its corners kept), so how near it runs to another is seen all along.</summary>
        static List<Vec2> Densify(List<Vec2> pts)
        {
            var o = new List<Vec2> { pts[0] };
            for (int k = 1; k < pts.Count; k++)
            {
                double d = Geo.Dist(pts[k - 1], pts[k]);
                int n = (int)Math.Ceiling(d);
                for (int s = 1; s < n; s++) o.Add(Geo.Lerp(pts[k - 1], pts[k], s / (double)n));
                o.Add(pts[k]);
            }
            return o;
        }

        /// <summary>A road's way on from its kth point, starting instead at `at`, its corners only.</summary>
        static List<Vec2> Rest(List<Vec2> pb, int k, Vec2 at)
        {
            var rest = new List<Vec2> { at };
            for (int m = k + 1; m < pb.Count; m++) rest.Add(pb[m]);
            if (rest.Count < 2) rest.Add(pb[pb.Count - 1]);
            return Geo.SimplifyRDP(rest, 0.01);
        }

        /// <summary>The longest jog (tiles) a road may take just out of a junction before it straightens it.</summary>
        const double JOG = 3.5;

        /// <summary>
        /// A junction set a tile or three off the line its roads run on: one of them steps off its line to reach it
        /// and back. The junction moves onto that line (to the corner where the road comes back onto it) and each
        /// other road there goes straight to it from the first of its points square with it. Kept only if there are
        /// fewer bends near the junction after (and every road keeps to land, none meets another sharply, four at
        /// most). Whether one moved.
        /// </summary>
        static bool StraightenJogs(GraphBuilder g, ISet<int> pinned, int centre, Func<List<Vec2>, List<Vec2>, bool> lineOk)
        {
            var inc = Incidence(g);
            foreach (var (node, list) in inc)
            {
                if (pinned.Contains(node) || (list.Count < 3 && node != centre) || list.Count < 2 || list.Count > 4) continue;
                var at = g.nodes[node].at;
                var legs = list.Distinct().Select(id => (id, pts: Away(g.edges[id], node))).ToList();
                int before = legs.Sum(l => Bends(l.pts));
                if (before == 0) continue;
                foreach (var (_, pts) in legs)
                {
                    // Its corner: the end of a short first step that turns.
                    if (pts.Count < 3 || Geo.Dist(pts[0], pts[1]) > JOG) continue;
                    var p = pts[1];
                    var moved = new Dictionary<int, List<Vec2>>();
                    bool ok = true;
                    foreach (var (id, q) in legs)
                    {
                        // From the furthest of its points near the junction (JOG * 3 along it) square with the new spot.
                        int m = 0;
                        double run = 0;
                        for (int k = 0; k < q.Count; k++)
                        {
                            if (k > 0) run += Geo.Dist(q[k - 1], q[k]);
                            if (run > JOG * 3) break;
                            if (Math.Abs(q[k].x - p.x) < 1e-6 || Math.Abs(q[k].y - p.y) < 1e-6) m = k;
                        }
                        var o = new List<Vec2> { p };
                        for (int k = m; k < q.Count; k++) if (Geo.Dist(q[k], o[o.Count - 1]) > 1e-6) o.Add(q[k]);
                        if (o.Count < 2 || !lineOk(q, o)) { ok = false; break; }
                        moved[id] = o;
                    }
                    if (!ok || moved.Values.Sum(Bends) >= before) continue;
                    var heads = moved.Values.Select(o => Heading(o, false)).ToList();
                    for (int x = 0; ok && x < heads.Count; x++)
                        for (int y = x + 1; ok && y < heads.Count; y++)
                            if (Geo.Dot(heads[x], heads[y]) > COS_SHARPEST) ok = false;
                    if (!ok) continue;
                    g.nodes[node].at = p;
                    foreach (var (id, o) in moved)
                    {
                        var e = g.edges[id];
                        if (e.from == node) e.points = o;
                        else
                        {
                            o.Reverse();
                            e.points = o;
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>How many times a road turns (by more than 30°) in its first JOG * 2 tiles out of a junction.</summary>
        static int Bends(List<Vec2> pts)
        {
            int n = 0;
            double run = 0;
            for (int k = 1; k + 1 < pts.Count && run < JOG * 2; k++)
            {
                run += Geo.Dist(pts[k - 1], pts[k]);
                var a = Geo.Sub(pts[k], pts[k - 1]);
                var b = Geo.Sub(pts[k + 1], pts[k]);
                double la = Math.Sqrt(Geo.Dot(a, a)), lb = Math.Sqrt(Geo.Dot(b, b));
                if (la > 1e-9 && lb > 1e-9 && Geo.Dot(a, b) / (la * lb) < 0.866) n++;
            }
            return n;
        }

        /// <summary>An edge's points from `node` outward.</summary>
        /// <summary>
        /// A road that steps aside right at a junction's mouth (StepAtMouth: out a tile or three, round a corner,
        /// round again): the step slid further out, so it leaves straight for MOUTH and a tile; or, if that can't
        /// be, cut across with a slant from the junction to the step's far corner, if that meets the junction's
        /// other roads at 60° or more. Either on land a main road may cross.
        /// </summary>
        static bool StraightenMouths(GraphBuilder g, Func<List<Vec2>, List<Vec2>, bool> lineOk, int centre)
        {
            bool any = false;
            var inc = Incidence(g);
            foreach (var e in LiveEdges(g).ToList())
            {
                foreach (bool atEnd in new[] { false, true })
                {
                    int node = atEnd ? e.to : e.from;
                    // A junction: three roads or more, or the centre where two meet (as CheckKnots counts them).
                    if (e.from == e.to || !inc.TryGetValue(node, out var legs) || legs.Count < (node == centre ? 2 : 3) || !StepAtMouth(e.points, atEnd)) continue;
                    var p = new List<Vec2>(e.points);
                    if (atEnd) p.Reverse();
                    int a = -1, b = -1;
                    for (int k = 1; k + 1 < p.Count; k++)
                    {
                        var u = Geo.Sub(p[k], p[k - 1]);
                        var v = Geo.Sub(p[k + 1], p[k]);
                        double lu = Math.Sqrt(Geo.Dot(u, u)), lv = Math.Sqrt(Geo.Dot(v, v));
                        if (lu < 1e-9 || lv < 1e-9 || Geo.Dot(u, v) / (lu * lv) >= 0.866) continue;
                        if (a < 0) a = k;
                        else { b = k; break; }
                    }
                    if (a < 0 || b < 0) continue;
                    var d = Geo.Sub(p[a], p[0]);
                    double run = Math.Sqrt(Geo.Dot(d, d));
                    if (run < 1e-9) continue;
                    d = new Vec2(d.x / run, d.y / run);
                    var tries = new List<List<Vec2>>();
                    // Slid out: the step's two corners moved on along the way it set off, the run after it shortened.
                    double shift = Math.Ceiling(MOUTH + 1 - run);
                    if (b + 1 < p.Count)
                    {
                        var after = Geo.Sub(p[b + 1], p[b]);
                        double la = Math.Sqrt(Geo.Dot(after, after));
                        if (la > shift + 0.5 && Geo.Dot(after, d) / la > 0.99)
                        {
                            var slid = new List<Vec2>();
                            for (int k = 0; k < a; k++) slid.Add(p[k]);
                            slid.Add(new Vec2(p[a].x + d.x * shift, p[a].y + d.y * shift));
                            slid.Add(new Vec2(p[b].x + d.x * shift, p[b].y + d.y * shift));
                            for (int k = b + 1; k < p.Count; k++) slid.Add(p[k]);
                            tries.Add(slid);
                        }
                    }
                    // Turned at once: out of the junction straight the way the step went, its first run dropped (the
                    // run after the step a little longer).
                    if (b + 1 < p.Count)
                    {
                        var turned = new List<Vec2> { p[0], new Vec2(p[b].x - (p[a].x - p[0].x), p[b].y - (p[a].y - p[0].y)) };
                        for (int k = b + 1; k < p.Count; k++) turned.Add(p[k]);
                        if (Geo.Dot(Geo.Sub(turned[2], turned[1]), d) > 0) tries.Add(Geo.SimplifyRDP(turned, 0.01));
                    }
                    // Cut across: straight from the junction to the step's far corner.
                    var cut = new List<Vec2> { p[0] };
                    for (int k = b; k < p.Count; k++) cut.Add(p[k]);
                    tries.Add(cut);
                    int other = atEnd ? e.from : e.to;
                    bool otherJunction = inc.TryGetValue(other, out var otherLegs) && otherLegs.Count >= (other == centre ? 2 : 3);
                    foreach (var t in tries)
                    {
                        // No step at either end after (a road between two junctions mustn't trade one for another).
                        if (StepAtMouth(t, false) || (otherJunction && StepAtMouth(t, true))) continue;
                        var h = Heading(t, false);
                        bool sharp = false;
                        foreach (int id in legs)
                        {
                            if (id == e.id) continue;
                            var o = Heading(Away(g.edges[id], node), false);
                            if (Geo.Dot(h, o) > COS_SHARPEST) sharp = true;
                        }
                        if (sharp) continue;
                        var now = new List<Vec2>(t);
                        if (atEnd) now.Reverse();
                        if (!lineOk(e.points, now)) continue;
                        e.points = now;
                        any = true;
                        break;
                    }
                }
            }
            return any;
        }

        static List<Vec2> Away(RoadEdge e, int node)
        {
            var pts = new List<Vec2>(e.points);
            if (e.from != node) pts.Reverse();
            return pts;
        }

        /// <summary>
        /// At the knot `link` joins: of the roads out of its two junctions that head off nearly the same way as another
        /// one there (or any, if there are five or more), the shortest whose going leaves every pinned node (the gates,
        /// the centre) joined to the rest and no road ending in nothing. Whether one went.
        /// </summary>
        static bool DropRedundantLeg(GraphBuilder g, Dictionary<int, List<int>> inc, RoadEdge link, ISet<int> pinned, int centre)
        {
            var legs = new List<(int id, int node, Vec2 head)>();
            foreach (int node in new[] { link.from, link.to })
                foreach (int id in inc[node])
                {
                    if (id == link.id) continue;
                    var e = g.edges[id];
                    if (e.from == node) legs.Add((id, node, Heading(e.points, false)));
                    if (e.to == node) legs.Add((id, node, Heading(e.points, true)));
                }
            var candidates = new List<int>();
            for (int a = 0; a < legs.Count; a++)
                for (int b = a + 1; b < legs.Count; b++)
                    if (legs[a].id != legs[b].id && Geo.Dot(legs[a].head, legs[b].head) > COS_SHARPEST) { candidates.Add(legs[a].id); candidates.Add(legs[b].id); }
            if (legs.Count >= 5) foreach (var l in legs) candidates.Add(l.id);
            candidates = candidates.Distinct().OrderBy(id => Geo.PolylineLength(g.edges[id].points)).ToList();
            foreach (int id in candidates)
            {
                // It goes, and any road left ending in nothing with it (back to a junction), if every pinned place
                // (gates, the airport, the zoo) is still joined up after.
                var was = new HashSet<int>(g.dead);
                g.dead.Add(id);
                DropNewDeadEnds(g, pinned, centre);
                if (Joined(g, centre >= 0 ? new HashSet<int>(pinned) { centre } : pinned)) return true;
                g.dead.Clear();
                g.dead.UnionWith(was);
            }
            return false;
        }

        /// <summary>Whether every pinned node can be reached from every other along live roads.</summary>
        static bool Joined(GraphBuilder g, ISet<int> pinned)
        {
            if (pinned.Count == 0) return true;
            var next = new Dictionary<int, List<int>>();
            foreach (var e in LiveEdges(g))
            {
                if (!next.TryGetValue(e.from, out var a)) next[e.from] = a = new List<int>();
                if (!next.TryGetValue(e.to, out var b)) next[e.to] = b = new List<int>();
                a.Add(e.to);
                b.Add(e.from);
            }
            int start = pinned.First();
            var seen = new HashSet<int> { start };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
                if (next.TryGetValue(queue.Dequeue(), out var l))
                    foreach (int n in l) if (seen.Add(n)) queue.Enqueue(n);
            return pinned.All(seen.Contains);
        }

        /// <summary>The sharpest two roads meet at `node` (but `skip`): the greatest cosine between their ways out; -1 if none.</summary>
        static double Sharpest(GraphBuilder g, Dictionary<int, List<int>> inc, int node, int skip, int unused)
        {
            var heads = new List<Vec2>();
            foreach (int id in inc[node])
            {
                if (id == skip) continue;
                var e = g.edges[id];
                if (e.from == node) heads.Add(Heading(e.points, false));
                if (e.to == node) heads.Add(Heading(e.points, true));
            }
            double most = -1;
            for (int a = 0; a < heads.Count; a++)
                for (int b = a + 1; b < heads.Count; b++) most = Math.Max(most, Geo.Dot(heads[a], heads[b]));
            return most;
        }

        /// <summary>`pts` with its end moved to `at` along the tiles: from its last point but one, an L (whichever corner keeps to its way).</summary>
        static List<Vec2> SquareEnd(List<Vec2> pts, Vec2 at)
        {
            var o = CutBack(pts);
            var prev = o[o.Count - 1];
            if (Math.Abs(prev.x - at.x) > 1e-6 && Math.Abs(prev.y - at.y) > 1e-6)
            {
                // The corner that carries on the way it was going.
                var before = o.Count >= 2 ? o[o.Count - 2] : prev;
                bool alongX = Math.Abs(prev.y - before.y) < 1e-6 && Math.Abs(prev.x - before.x) > 1e-6;
                o.Add(alongX ? new Vec2(at.x, prev.y) : new Vec2(prev.x, at.y));
            }
            o.Add(at);
            return Dedupe(o);
        }

        /// <summary>How far back from its old end a road moved onto a merged junction starts to bend to it (tiles).</summary>
        const double BEND_BACK = 4;

        /// <summary>`pts` as far as BEND_BACK (or half its length) short of its end, then straight on to `at`.</summary>
        static List<Vec2> BendEnd(List<Vec2> pts, Vec2 at)
        {
            var o = CutBack(pts);
            if (Geo.Dist(at, o[o.Count - 1]) > 1e-6) o.Add(at);
            if (o.Count < 2) o.Add(at);
            return o;
        }

        /// <summary>`pts` as far as BEND_BACK (or half its length) short of its end.</summary>
        static List<Vec2> CutBack(List<Vec2> pts)
        {
            double length = Geo.PolylineLength(pts), keep = Math.Max(0, length - Math.Min(BEND_BACK, length / 2));
            var o = new List<Vec2> { pts[0] };
            double run = 0;
            for (int k = 1; k < pts.Count; k++)
            {
                double step = Geo.Dist(pts[k - 1], pts[k]);
                if (run + step >= keep)
                {
                    var cut = Geo.Lerp(pts[k - 1], pts[k], step < 1e-9 ? 0 : (keep - run) / step);
                    if (Geo.Dist(cut, o[o.Count - 1]) > 1e-6) o.Add(cut);
                    break;
                }
                run += step;
                o.Add(pts[k]);
            }
            return o;
        }

        /// <summary>The sharpest two roads may meet at a junction a knot's undone into (cos 60°: AASHTO's least).</summary>
        const double COS_SHARPEST = 0.5;
        /// <summary>The sharpest two roads may meet when that's the only way to make a knot one junction: 40°.</summary>
        const double COS_KNOT = 0.766;

        /// <summary>How far along a road its way out of a junction is read (tiles): past a jog of a tile or two.</summary>
        const double HEADING_OVER = 3;

        /// <summary>The way a road leaves its start (or, `atEnd`, its end), as a unit vector, over its first HEADING_OVER tiles.</summary>
        static Vec2 Heading(List<Vec2> pts, bool atEnd) => Heading(pts, atEnd, HEADING_OVER);

        /// <summary>Roads side by side are told by where they're going over this far (tiles): past an L out of the junction.</summary>
        const double ZIP_HEADING_OVER = 6;

        static Vec2 Heading(List<Vec2> pts, bool atEnd, double over)
        {
            var from = atEnd ? pts[pts.Count - 1] : pts[0];
            Vec2 to = from;
            for (int k = 1; k < pts.Count; k++)
            {
                to = atEnd ? pts[pts.Count - 1 - k] : pts[k];
                if (Geo.Dist(from, to) >= over) break;
            }
            double d = Geo.Dist(from, to);
            return d < 1e-9 ? new Vec2(1, 0) : new Vec2((to.x - from.x) / d, (to.y - from.y) / d);
        }

        /// <summary>
        /// Two roads between the same two junctions: the shorter's kept (a main road over a street); a road back to
        /// where it began: dropped. Whether any went.
        /// </summary>
        public static bool DropDoubles(GraphBuilder g)
        {
            bool changed = false;
            var between = new Dictionary<(int, int), RoadEdge>();
            foreach (var e in LiveEdges(g))
            {
                if (e.from == e.to) { g.dead.Add(e.id); changed = true; continue; }
                var k = (Math.Min(e.from, e.to), Math.Max(e.from, e.to));
                if (!between.TryGetValue(k, out var o)) { between[k] = e; continue; }
                bool better = e.kind != o.kind ? e.kind == RoadKind.Main || (e.kind == RoadKind.Street && o.kind == RoadKind.Path)
                    : Geo.PolylineLength(e.points) < Geo.PolylineLength(o.points);
                g.dead.Add(better ? o.id : e.id);
                if (better) between[k] = e;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Knots: two roads between the same two junctions, a road back to where it began, and two junctions (three
        /// roads or more; or the centre, `pinned`, with two) nearer along a road than `apart`. None's allowed.
        /// </summary>
        public static List<string> CheckKnots(RoadGraph g, double apart, Vec2? pinned = null, bool bent = false)
        {
            var errors = new List<string>();
            var degree = new Dictionary<int, int>();
            var between = new HashSet<(int, int)>();
            foreach (var e in g.edges)
            {
                degree[e.from] = (degree.TryGetValue(e.from, out int a) ? a : 0) + 1;
                degree[e.to] = (degree.TryGetValue(e.to, out int b) ? b : 0) + 1;
                var at = e.points[0];
                if (e.from == e.to) errors.Add($"road {e.id} comes back to where it began, near {Numbers.ToFixed(at.x, 1)},{Numbers.ToFixed(at.y, 1)}");
                else if (!between.Add((Math.Min(e.from, e.to), Math.Max(e.from, e.to))))
                    errors.Add($"two roads between the same two junctions, near {Numbers.ToFixed(at.x, 1)},{Numbers.ToFixed(at.y, 1)}");
            }
            bool Junction(int n) => degree[n] >= 3 || (pinned != null && degree[n] >= 2 && Geo.Dist(g.nodes[n].at, pinned.Value) < 0.01);
            if (bent)
                foreach (var e in g.edges)
                {
                    if (e.from == e.to) continue;
                    if (Junction(e.from) && StepAtMouth(e.points, false))
                        errors.Add($"a road steps aside within {Numbers.ToFixed(MOUTH, 1)} tiles of the junction it leaves, near {Numbers.ToFixed(e.points[0].x, 1)},{Numbers.ToFixed(e.points[0].y, 1)}");
                    if (Junction(e.to) && StepAtMouth(e.points, true))
                        errors.Add($"a road steps aside within {Numbers.ToFixed(MOUTH, 1)} tiles of the junction it leaves, near {Numbers.ToFixed(e.points[e.points.Count - 1].x, 1)},{Numbers.ToFixed(e.points[e.points.Count - 1].y, 1)}");
                }
            foreach (var e in g.edges)
            {
                if (e.from == e.to || !Junction(e.from) || !Junction(e.to)) continue;
                double length = Geo.PolylineLength(e.points), across = Geo.Dist(e.points[0], e.points[e.points.Count - 1]);
                if (Knotted(e.points, apart, bent))
                    errors.Add($"a knot of junctions: {Numbers.ToFixed(length, 1)} apart along the road, {Numbers.ToFixed(across, 1)} across{(Bends(e.points) > 0 ? ", with a bend between" : "")} (at least {Numbers.ToFixed(apart, 1)} along{(bent ? $", {Numbers.ToFixed(ACROSS * apart, 1)} across and {Numbers.ToFixed(2 * apart, 1)} if it bends" : "")}) near {Numbers.ToFixed(e.points[0].x, 1)},{Numbers.ToFixed(e.points[0].y, 1)}");
            }
            return errors;
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
                graph.edges.Add(new RoadEdge { id = i, from = used[e.from], to = used[e.to], kind = e.kind, cls = e.cls, points = e.points });
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

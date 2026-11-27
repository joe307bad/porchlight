// Ways on foot for the town's life: the sidewalks as a graph (each sidewalk's two
// ends, joined along it and to the sidewalks they meet), crossing at junctions
// where the sidewalk network marks a crossing, and straight over the street in the
// middle of a block only at three times the cost, so people go round by the
// corner unless it's far. A way comes as pieces: stretches of sidewalk, and each
// crossing at a junction a piece of its own (the junction must let the walker
// over, as it lets a car round a corner). Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;

namespace Porchlight.Life
{
    public sealed class WalkPiece
    {
        public List<Vec2> points;
        public List<double> at;
        public double length;
        /// <summary>A crossing at a junction: which (its node), and the road crossed; -1 otherwise.</summary>
        public int node = -1, road = -1;
        /// <summary>Over a main road (at a junction or not).</summary>
        public bool overMain;
        /// <summary>The sidewalk it starts on (for crowding), or -1.</summary>
        public int path = -1;
        /// <summary>A crossing at a junction: the sidewalk end it sets off from (path * 2 + end), where its walkers wait; -1 otherwise.</summary>
        public int from = -1;
    }

    public sealed class Walkways
    {
        /// <summary>Crossing in the middle of a block costs this many times the walk over.</summary>
        public const double MIDBLOCK = 3;

        readonly SidewalkNetwork net;
        readonly RoadNet roads;
        /// <summary>For each path end (path * 2 + end): (other end, cost, link kind: 0 along, 1 link, 2 junction crossing).</summary>
        readonly List<(int to, double cost, byte kind)>[] edges;
        readonly Dictionary<long, (int node, int road)> crossings = new Dictionary<long, (int, int)>();
        /// <summary>The longest crossing over each road at each junction (tiles), by (node, road): what a light must give walkers time for.</summary>
        public readonly Dictionary<(int node, int road), double> longestCrossing = new Dictionary<(int, int), double>();
        readonly int[] sibling;
        /// <summary>Each path's points bucketed by 8-tile cell, for finding the nearest quickly.</summary>
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        /// <summary>Which side of each sidewalk its road is on: +1 its left (going along it), -1 its right.</summary>
        readonly sbyte[] kerbSide;

        public SidewalkNetwork Net => net;

        public Walkways(SidewalkNetwork net, RoadNet roads)
        {
            this.net = net;
            this.roads = roads;
            int n = net.paths.Count;
            edges = new List<(int, double, byte)>[n * 2];
            for (int i = 0; i < edges.Length; i++) edges[i] = new List<(int, double, byte)>();
            sibling = new int[n];
            kerbSide = new sbyte[n];
            var byRoad = new Dictionary<int, List<int>>();
            for (int p = 0; p < n; p++)
            {
                var path = net.paths[p];
                if (!byRoad.TryGetValue(path.road, out var l)) byRoad[path.road] = l = new List<int>();
                l.Add(p);
                edges[p * 2].Add((p * 2 + 1, path.Length, 0));
                edges[p * 2 + 1].Add((p * 2, path.Length, 0));
                foreach (var q in path.points)
                {
                    long c = Cell(q);
                    if (!cells.TryGetValue(c, out var list)) cells[c] = list = new List<int>();
                    if (list.Count == 0 || list[list.Count - 1] != p) list.Add(p);
                }
            }
            for (int p = 0; p < n; p++)
            {
                kerbSide[p] = KerbSide(net.paths[p]);
                sibling[p] = -1;
                foreach (int q in byRoad[net.paths[p].road])
                    if (q != p && net.paths[q].side != net.paths[p].side) sibling[p] = q;
            }
            for (int e = 0; e < n * 2 && e < net.links.Count; e++)
            {
                foreach (int f in net.links[e])
                {
                    var a = EndPoint(e);
                    var b = EndPoint(f);
                    double d = Geo.Dist(a, b);
                    long key = SidewalkNetwork.LinkKey(e, f);
                    bool cross = net.crossings.Contains(key);
                    edges[e].Add((f, d, cross ? (byte)2 : (byte)1));
                    if (cross && !crossings.ContainsKey(key)) crossings[key] = CrossingAt(new Vec2((a.x + b.x) / 2, (a.y + b.y) / 2));
                    if (cross && crossings[key].node >= 0)
                    {
                        var x = crossings[key];
                        longestCrossing.TryGetValue((x.node, x.road), out double was);
                        longestCrossing[(x.node, x.road)] = Math.Max(was, d);
                    }
                }
            }
        }

        static long Cell(Vec2 p) => ((long)Math.Floor(p.x / 8) << 24) ^ (long)Math.Floor(p.y / 8);

        Vec2 EndPoint(int end)
        {
            var pts = net.paths[end >> 1].points;
            return (end & 1) == 0 ? pts[0] : pts[pts.Count - 1];
        }

        /// <summary>The junction a crossing is at, and the road it crosses.</summary>
        (int node, int road) CrossingAt(Vec2 m)
        {
            int node = -1, road = -1;
            double bestD = 5;
            foreach (var j in roads.junctions.Values)
            {
                if (j.fan) continue;
                double d = Geo.Dist(j.at, m);
                if (d < bestD)
                {
                    bestD = d;
                    node = j.node;
                }
            }
            if (node < 0) return (-1, -1);
            double bestR = double.PositiveInfinity;
            foreach (int r in roads.junctions[node].legs)
            {
                double d = Geo.DistToPolyline(m, roads.roads[r].points);
                if (d < bestR)
                {
                    bestR = d;
                    road = r;
                }
            }
            return (node, road);
        }

        /// <summary>The sidewalk nearest `p`, and how far along it.</summary>
        public (int path, double s) Nearest(Vec2 p)
        {
            int best = -1;
            double bestD = double.PositiveInfinity, bestS = 0;
            for (int ring = 0; ring < 64 && best < 0; ring++)
            {
                // Look in a widening square of cells until something's found, then one ring more.
                for (int pass = 0; pass < 2; pass++)
                {
                    var seen = new HashSet<int>();
                    int r = ring + pass;
                    long cx = (long)Math.Floor(p.x / 8), cy = (long)Math.Floor(p.y / 8);
                    for (long x = cx - r; x <= cx + r; x++)
                        for (long y = cy - r; y <= cy + r; y++)
                        {
                            if (!cells.TryGetValue((x << 24) ^ y, out var list)) continue;
                            foreach (int i in list)
                            {
                                if (!seen.Add(i)) continue;
                                var path = net.paths[i];
                                double s = Geo.ArcPosition(p, path.points);
                                var q = SidewalkWalk.PointAlong(path, s);
                                double d = Numbers.Hypot(q.x - p.x, q.y - p.y);
                                if (d < bestD || (d == bestD && i < best))
                                {
                                    bestD = d;
                                    best = i;
                                    bestS = s;
                                }
                            }
                        }
                    if (best < 0) break;
                }
            }
            return (best, bestS);
        }

        /// <summary>The way on foot from `from` to `to`, in pieces; null if there are no sidewalks.</summary>
        public List<WalkPiece> Route(Vec2 from, Vec2 to)
        {
            if (net.paths.Count == 0) return null;
            var (pa, sa) = Nearest(from);
            var (pb, sb) = Nearest(to);
            if (pa < 0 || pb < 0) return null;
            var A = net.paths[pa];
            var B = net.paths[pb];
            var startPt = SidewalkWalk.PointAlong(A, sa);
            var endPt = SidewalkWalk.PointAlong(B, sb);

            // Dijkstra over path ends, from two (or, crossing mid-block, four) starting points.
            int n = edges.Length;
            var cost = new double[n];
            var prev = new int[n];
            var kind = new byte[n];
            for (int i = 0; i < n; i++)
            {
                cost[i] = double.PositiveInfinity;
                prev[i] = -1;
            }
            var heap = new MinHeap();
            // prev: -2 straight from the start along pa; -3 across the street mid-block to pa's sibling, then along.
            void Seed(int end, double c, int how)
            {
                if (c >= cost[end]) return;
                cost[end] = c;
                prev[end] = how;
                heap.Push(end, c);
            }
            Seed(pa * 2, sa, -2);
            Seed(pa * 2 + 1, A.Length - sa, -2);
            int sib = sibling[pa];
            double sibS = 0;
            if (sib >= 0)
            {
                var S = net.paths[sib];
                sibS = Geo.ArcPosition(startPt, S.points);
                double over = Geo.Dist(startPt, SidewalkWalk.PointAlong(S, sibS)) * MIDBLOCK;
                Seed(sib * 2, over + sibS, -3);
                Seed(sib * 2 + 1, over + S.Length - sibS, -3);
            }
            double best = pa == pb ? Math.Abs(sa - sb) : double.PositiveInfinity;
            int bestEnd = -1;
            if (sib >= 0 && sib == pb)
            {
                var S = net.paths[sib];
                double c = Geo.Dist(startPt, SidewalkWalk.PointAlong(S, sibS)) * MIDBLOCK + Math.Abs(sibS - sb);
                if (c < best)
                {
                    best = c;
                    bestEnd = -3;
                }
            }
            while (heap.Size > 0)
            {
                int e = heap.Pop();
                double c = cost[e];
                if (c >= best) break;
                if (e >> 1 == pb)
                {
                    double total = c + ((e & 1) == 0 ? sb : B.Length - sb);
                    if (total < best)
                    {
                        best = total;
                        bestEnd = e;
                    }
                }
                foreach (var (f, w, k) in edges[e])
                {
                    double nc = c + w;
                    if (nc < cost[f])
                    {
                        cost[f] = nc;
                        prev[f] = e;
                        kind[f] = k;
                        heap.Push(f, nc);
                    }
                }
            }
            var pieces = new List<WalkPiece>();
            var pts = new List<Vec2> { from, startPt };
            int piecePath = pa;
            void Flush()
            {
                if (pts.Count >= 2) pieces.Add(Piece(pts, -1, -1, piecePath));
                pts = new List<Vec2> { pts[pts.Count - 1] };
            }
            if (bestEnd == -1 && pa == pb)
            {
                pts.AddRange(Geo.SubPolyline(A.points, sa, sb));
                pts.Add(to);
                Flush();
                return pieces;
            }
            if (bestEnd == -3)
            {
                var S = net.paths[sib];
                pts.Add(SidewalkWalk.PointAlong(S, sibS));
                pts.AddRange(Geo.SubPolyline(S.points, sibS, sb));
                pts.Add(to);
                Flush();
                pieces[0].overMain = roads.roads[S.road].kind == RoadKind.Main;
                return pieces;
            }
            if (bestEnd < 0)
            {
                // No way along the sidewalks: straight there.
                pts.Add(to);
                Flush();
                return pieces;
            }
            // The ends in order, from the first reached to the last.
            var chain = new List<int>();
            for (int e = bestEnd; e >= 0; e = prev[e]) chain.Add(e);
            chain.Reverse();
            int first = chain[0];
            if (prev[first] == -3)
            {
                var S = net.paths[sib];
                pts.Add(SidewalkWalk.PointAlong(S, sibS));
                if (roads.roads[S.road].kind == RoadKind.Main) crossedMain = true;
                pts.AddRange(Geo.SubPolyline(S.points, sibS, (first & 1) == 0 ? 0 : S.Length));
            }
            else pts.AddRange(Geo.SubPolyline(A.points, sa, (first & 1) == 0 ? 0 : A.Length));
            for (int i = 1; i < chain.Count; i++)
            {
                int e0 = chain[i - 1], e1 = chain[i];
                byte k = kind[e1];
                if (k == 0)
                {
                    // Along a sidewalk, end to end.
                    var P = net.paths[e1 >> 1];
                    pts.AddRange(Geo.SubPolyline(P.points, (e0 & 1) == 0 ? 0 : P.Length, (e1 & 1) == 0 ? 0 : P.Length));
                }
                else if (k == 2 && crossings.TryGetValue(SidewalkNetwork.LinkKey(e0, e1), out var x) && x.node >= 0)
                {
                    // A crossing at a junction: a piece of its own.
                    Flush();
                    var piece = Piece(new List<Vec2> { EndPoint(e0), EndPoint(e1) }, x.node, x.road, e0 >> 1);
                    piece.from = e0;
                    pieces.Add(piece);
                    pts = new List<Vec2> { EndPoint(e1) };
                    piecePath = e1 >> 1;
                }
                else pts.Add(EndPoint(e1));
            }
            pts.AddRange(Geo.SubPolyline(B.points, (bestEnd & 1) == 0 ? 0 : B.Length, sb));
            pts.Add(to);
            Flush();
            if (crossedMain && pieces.Count > 0) pieces[0].overMain = true;
            crossedMain = false;
            return pieces;
        }

        /// <summary>Set while a way's being put together that crosses a main road mid-block.</summary>
        bool crossedMain;

        WalkPiece Piece(List<Vec2> pts, int node, int road, int path)
        {
            // Drop repeated points.
            var clean = new List<Vec2>();
            foreach (var p in pts) if (clean.Count == 0 || Geo.Dist(clean[clean.Count - 1], p) > 1e-6) clean.Add(p);
            if (clean.Count == 1) clean.Add(clean[0]);
            var at = new List<double> { 0 };
            for (int i = 1; i < clean.Count; i++) at.Add(at[i - 1] + Geo.Dist(clean[i], clean[i - 1]));
            return new WalkPiece
            {
                points = clean, at = at, length = at[at.Count - 1], node = node, road = road, path = path,
                overMain = road >= 0 && roads.roads[road].kind == RoadKind.Main,
            };
        }

        /// <summary>A point `d` along a piece, and the way it's going there.</summary>
        public static (Vec2 at, Vec2 along) PointOn(WalkPiece piece, double d)
        {
            var pts = piece.points;
            d = Math.Max(0, Math.Min(piece.length, d));
            int k = 1;
            while (k < pts.Count - 1 && piece.at[k] < d) k++;
            var a = pts[k - 1];
            var b = pts[k];
            double seg = piece.at[k] - piece.at[k - 1];
            double t = seg < 1e-9 ? 0 : (d - piece.at[k - 1]) / seg;
            double dx = b.x - a.x, dy = b.y - a.y, l = Numbers.Hypot(dx, dy);
            return (new Vec2(a.x + dx * t, a.y + dy * t), l < 1e-9 ? new Vec2(0, 1) : new Vec2(dx / l, dy / l));
        }

        // A crowd at the kerb --------------------------------------------------------------------
        //
        // People waiting to cross stand in a staggered block on the sidewalk by the crossing: three abreast
        // across it, then two in the gaps a step behind, then three, and so on back along it from the corner.
        // Spot 0 is at the corner on the kerb side; each newcomer takes the frontmost spot free. A tile is 4 m,
        // so three abreast are 0.56 m apart on a 1.8 m sidewalk and a step back is 0.52 m: about 2.9 people a
        // square metre when it's full, a dense but orderly queue (Fruin's level E). Where the sidewalk bends
        // sharply at the corner, or a corner's curve brings it near a carriageway, a place that would stand
        // within half a metre of one already given, or in the road, is passed over for the next. A corner's
        // spots are worked out the first time someone waits there, and kept.

        /// <summary>Across the sidewalk between neighbours in a row (tiles).</summary>
        public const double CROWD_ABREAST = 0.14;
        /// <summary>From one row to the next back along the sidewalk (tiles).</summary>
        public const double CROWD_STEP = 0.13;
        /// <summary>No two in a crowd stand closer than this (tiles): half a metre.</summary>
        public const double CROWD_APART = 0.125;
        /// <summary>The front row stands this far back from the sidewalk's end (tiles).</summary>
        const double CROWD_FRONT = 0.04;
        /// <summary>A crowd keeps this far from a carriageway's edge (tiles).</summary>
        const double CROWD_CLEAR = 0.05;

        sealed class Crowd
        {
            public readonly List<Vec2> spots = new List<Vec2>();
            /// <summary>The next place in the block to try, and whether the sidewalk's run out.</summary>
            public int next;
            public bool full;
            /// <summary>The carriageways near enough to matter.</summary>
            public readonly List<int> near = new List<int>();
        }

        /// <summary>Each sidewalk end's crowd spots, worked out as they're wanted.</summary>
        readonly Dictionary<int, Crowd> crowdSpots = new Dictionary<int, Crowd>();

        /// <summary>
        /// Where the `k`th to wait at sidewalk end `end` (path * 2 + end) stands: on the sidewalk, `CROWD_FRONT` back from
        /// its end and in rows behind. Past the sidewalk's far end (a crowd of more than about eleven a tile of it) they
        /// stand over those in front, from the front again.
        /// </summary>
        public Vec2 CrowdSpot(int end, int k)
        {
            if (!crowdSpots.TryGetValue(end, out var crowd))
            {
                crowd = new Crowd();
                var corner = EndPoint(end);
                for (int r = 0; r < roads.roads.Count; r++)
                {
                    var road = roads.roads[r];
                    if (road.kind != RoadKind.Path && road.points.Count >= 2 && Geo.DistToPolyline(corner, road.points) < road.width / 2 + 4) crowd.near.Add(r);
                }
                crowdSpots[end] = crowd;
            }
            while (crowd.spots.Count <= k && !crowd.full)
            {
                if (!Place(end, crowd.next++, out var at))
                {
                    crowd.full = true;
                    break;
                }
                bool ok = true;
                foreach (int r in crowd.near)
                    if (ok && Geo.DistToPolyline(at, roads.roads[r].points) < roads.roads[r].width / 2 + CROWD_CLEAR) ok = false;
                foreach (var q in crowd.spots)
                    if (ok && Geo.Dist(q, at) < CROWD_APART) ok = false;
                if (ok) crowd.spots.Add(at);
            }
            if (crowd.spots.Count == 0) return EndPoint(end);
            return crowd.spots[k % crowd.spots.Count];
        }

        /// <summary>The `c`th place in the block at sidewalk end `end`; false once it runs past the sidewalk's far end.</summary>
        bool Place(int end, int c, out Vec2 place)
        {
            var path = net.paths[end >> 1];
            int pair = c / 5, r = c % 5;
            int row = pair * 2 + (r < 3 ? 0 : 1);
            // Kerb side first: three abreast at +1, 0, -1; two at +½, -½.
            double v = r < 3 ? (1 - r) * CROWD_ABREAST : (r == 3 ? 0.5 : -0.5) * CROWD_ABREAST;
            double u = CROWD_FRONT + row * CROWD_STEP;
            place = EndPoint(end);
            if (u > path.Length) return false;
            double s = (end & 1) == 0 ? u : path.Length - u;
            var at = SidewalkWalk.PointAlong(path, s);
            var a = SidewalkWalk.PointAlong(path, Math.Max(0, s - 0.05));
            var b = SidewalkWalk.PointAlong(path, Math.Min(path.Length, s + 0.05));
            double dx = b.x - a.x, dy = b.y - a.y, l = Numbers.Hypot(dx, dy);
            place = at;
            if (l < 1e-9) return true;
            // The left of the way the sidewalk runs, turned towards its road.
            double side = kerbSide[end >> 1] * v / l;
            place = new Vec2(at.x - dy * side, at.y + dx * side);
            return true;
        }

        /// <summary>Which side of a sidewalk its road is on: +1 the left, going along it from its start; -1 the right.</summary>
        sbyte KerbSide(SidewalkPath path)
        {
            if (path.road < 0 || path.road >= roads.roads.Count || path.points.Count < 2) return 1;
            var mid = SidewalkWalk.PointAlong(path, path.Length / 2);
            var a = SidewalkWalk.PointAlong(path, Math.Max(0, path.Length / 2 - 0.05));
            var b = SidewalkWalk.PointAlong(path, Math.Min(path.Length, path.Length / 2 + 0.05));
            double dx = b.x - a.x, dy = b.y - a.y, l = Numbers.Hypot(dx, dy);
            if (l < 1e-9) return 1;
            var road = roads.roads[path.road].points;
            var left = new Vec2(mid.x - dy / l * 0.1, mid.y + dx / l * 0.1);
            var right = new Vec2(mid.x + dy / l * 0.1, mid.y - dx / l * 0.1);
            return Geo.DistToPolyline(left, road) <= Geo.DistToPolyline(right, road) ? (sbyte)1 : (sbyte)-1;
        }

        /// <summary>How crowded a sidewalk is with `people` on it: people a square metre (a tile is 4 m).</summary>
        public double Crowding(int path, int people)
        {
            if (path < 0) return 0;
            double area = Math.Max(1e-6, net.paths[path].Length * 4 * Layout.SIDEWALK * 4);
            return people / area;
        }
    }
}

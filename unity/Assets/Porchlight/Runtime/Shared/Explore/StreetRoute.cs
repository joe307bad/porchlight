// The way from one spot to another along the streets: onto the nearest
// street, along streets (the shortest way over the road graph), and at the
// last off the street to the spot itself. Used to show the way to another
// player. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class StreetRoute
    {
        /// <summary>From `from`, along the street centrelines, to `to`.</summary>
        public List<Vec2> points = new List<Vec2>();
        /// <summary>Each street in turn, for directions (Directions.ForDrive).</summary>
        public List<Stretch> stretches = new List<Stretch>();

        sealed class Road
        {
            public RoadStroke stroke;
            public List<double> at;
            public double Length => at[at.Count - 1];
        }

        /// <summary>The way from `from` to `to` over these roads (footpaths left out); null if there are no streets.</summary>
        public static StreetRoute Between(IReadOnlyList<RoadStroke> strokes, Vec2 from, Vec2 to)
        {
            var roads = new List<Road>();
            foreach (var r in strokes)
            {
                var at = new List<double> { 0 };
                for (int k = 1; k < r.points.Count; k++) at.Add(at[k - 1] + Geo.Dist(r.points[k], r.points[k - 1]));
                roads.Add(new Road { stroke = r, at = at });
            }
            bool Usable(int i) => roads[i].stroke.kind != RoadKind.Path && roads[i].Length > 1e-6;
            int Nearest(Vec2 q)
            {
                int best = -1;
                double bestD = double.PositiveInfinity;
                for (int i = 0; i < roads.Count; i++)
                {
                    if (!Usable(i)) continue;
                    double d = Geo.DistToPolyline(q, roads[i].stroke.points);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }
                return best;
            }
            int ra = Nearest(from), rb = Nearest(to);
            if (ra < 0 || rb < 0) return null;
            double sa = Geo.ArcPosition(from, roads[ra].stroke.points);
            double sb = Geo.ArcPosition(to, roads[rb].stroke.points);

            // Legs: (road, s0, s1) in the order driven.
            var legs = new List<(int road, double s0, double s1)>();
            if (ra == rb) legs.Add((ra, sa, sb));
            else
            {
                var byNode = new Dictionary<int, List<int>>();
                for (int i = 0; i < roads.Count; i++)
                {
                    if (!Usable(i)) continue;
                    foreach (int n in new[] { roads[i].stroke.from, roads[i].stroke.to })
                    {
                        if (!byNode.TryGetValue(n, out var list)) byNode[n] = list = new List<int>();
                        list.Add(i);
                    }
                }
                var cost = new Dictionary<int, double>();
                var via = new Dictionary<int, int>();
                var heap = new MinHeap();
                var ids = new List<int>();
                int Id(int node)
                {
                    int k = ids.IndexOf(node);
                    if (k < 0)
                    {
                        ids.Add(node);
                        k = ids.Count - 1;
                    }
                    return k;
                }
                void Reach(int node, double c, int road)
                {
                    if (cost.TryGetValue(node, out var was) && was <= c) return;
                    cost[node] = c;
                    via[node] = road;
                    heap.Push(Id(node), c);
                }
                var a = roads[ra];
                Reach(a.stroke.from, sa, -1);
                Reach(a.stroke.to, a.Length - sa, -1);
                var b = roads[rb];
                double bestTotal = double.PositiveInfinity;
                int end = -1;
                var done = new HashSet<int>();
                while (heap.Size > 0)
                {
                    int node = ids[heap.Pop()];
                    if (!done.Add(node)) continue;
                    double c = cost[node];
                    if (c >= bestTotal) break;
                    if (node == b.stroke.from && c + sb < bestTotal)
                    {
                        bestTotal = c + sb;
                        end = node;
                    }
                    if (node == b.stroke.to && c + b.Length - sb < bestTotal)
                    {
                        bestTotal = c + b.Length - sb;
                        end = node;
                    }
                    if (!byNode.TryGetValue(node, out var ways)) continue;
                    foreach (int i in ways)
                    {
                        var r = roads[i].stroke;
                        int other = r.from == node ? r.to : r.from;
                        Reach(other, c + roads[i].Length, i);
                    }
                }
                if (end < 0) legs.Add((ra, sa, sa)); // Not joined up: as far as the street you're on.
                else
                {
                    var middle = new List<(int, double, double)>();
                    for (int node = end; via[node] >= 0;)
                    {
                        int i = via[node];
                        var r = roads[i].stroke;
                        bool forward = r.to == node;
                        middle.Add((i, forward ? 0 : roads[i].Length, forward ? roads[i].Length : 0));
                        node = forward ? r.from : r.to;
                    }
                    middle.Reverse();
                    int startNode = middle.Count > 0 ? (middle[0].Item2 == 0 ? roads[middle[0].Item1].stroke.from : roads[middle[0].Item1].stroke.to) : end;
                    legs.Add((ra, sa, startNode == a.stroke.to ? a.Length : 0));
                    legs.AddRange(middle);
                    legs.Add((rb, end == b.stroke.from ? 0 : b.Length, sb));
                }
            }

            var o = new StreetRoute();
            o.points.Add(from);
            foreach (var (road, s0, s1) in legs)
            {
                var r = roads[road];
                foreach (var q in Slice(r, s0, s1)) if (o.points.Count == 0 || Geo.Dist(o.points[o.points.Count - 1], q) > 1e-6) o.points.Add(q);
                double length = Math.Abs(s1 - s0);
                if (length < 1e-6) continue;
                int d = s1 >= s0 ? 1 : -1;
                o.stretches.Add(new Stretch
                {
                    road = road,
                    length = length,
                    start = Along(r, s0 + 0.3 * d * Math.Min(1, length / 0.6), d),
                    end = Along(r, s1 - 0.3 * d * Math.Min(1, length / 0.6), d),
                });
            }
            o.points.Add(to);
            return o;
        }

        static Vec2 PointAt(Road r, double s)
        {
            var pts = r.stroke.points;
            s = Math.Max(0, Math.Min(r.Length, s));
            for (int k = 1; k < pts.Count; k++)
            {
                if (r.at[k] < s && k < pts.Count - 1) continue;
                double seg = r.at[k] - r.at[k - 1];
                double f = seg > 0 ? (s - r.at[k - 1]) / seg : 0;
                return new Vec2(pts[k - 1].x + (pts[k].x - pts[k - 1].x) * f, pts[k - 1].y + (pts[k].y - pts[k - 1].y) * f);
            }
            return pts[0];
        }

        /// <summary>The way along road `r` at arc position s, heading `dir`: a unit vector.</summary>
        static Vec2 Along(Road r, double s, int dir)
        {
            var a = PointAt(r, s - 0.15);
            var b = PointAt(r, s + 0.15);
            double l = Geo.Dist(a, b);
            if (l < 1e-9) return new Vec2(0, 0);
            return new Vec2((b.x - a.x) / l * dir, (b.y - a.y) / l * dir);
        }

        /// <summary>Points along a road from s0 to s1 (either way), ends included.</summary>
        static List<Vec2> Slice(Road r, double s0, double s1)
        {
            var o = new List<Vec2> { PointAt(r, s0) };
            var inner = new List<Vec2>();
            double lo = Math.Min(s0, s1), hi = Math.Max(s0, s1);
            for (int k = 0; k < r.stroke.points.Count; k++) if (r.at[k] > lo && r.at[k] < hi) inner.Add(r.stroke.points[k]);
            if (s1 < s0) inner.Reverse();
            o.AddRange(inner);
            o.Add(PointAt(r, s1));
            return o;
        }
    }
}

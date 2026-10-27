// Small, readable 2D geometry helpers. Polygons are Vec2 rings without a
// repeated closing point.

using System;
using System.Collections.Generic;

namespace Porchlight.Core
{
    /// <summary>A point or direction on the map, in tiles (y runs down the map).</summary>
    [Serializable]
    public struct Vec2 : IEquatable<Vec2>
    {
        public double x;
        public double y;

        public Vec2(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        public bool Equals(Vec2 o) => x == o.x && y == o.y;
        public override bool Equals(object obj) => obj is Vec2 v && Equals(v);
        public override int GetHashCode() => x.GetHashCode() * 31 + y.GetHashCode();
        public override string ToString() => $"({x}, {y})";
    }

    public struct ClosestPoint
    {
        public Vec2 point;
        public double t;
    }

    public struct PolylineHit
    {
        public int seg;
        public double t;
        public Vec2 point;
        public double d;
    }

    public struct SegmentHit
    {
        public double t;
        public double u;
        public Vec2 point;
    }

    public struct Bounds2
    {
        public double x0, y0, x1, y1;
    }

    public static class Geo
    {
        public const double EPS = 1e-9;

        public static Vec2 V(double x, double y) => new Vec2(x, y);
        public static Vec2 Sub(Vec2 a, Vec2 b) => new Vec2(a.x - b.x, a.y - b.y);
        public static Vec2 Add(Vec2 a, Vec2 b) => new Vec2(a.x + b.x, a.y + b.y);
        public static Vec2 Scale(Vec2 a, double s) => new Vec2(a.x * s, a.y * s);
        public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new Vec2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        public static double Dist(Vec2 a, Vec2 b) => Numbers.Hypot(a.x - b.x, a.y - b.y);
        public static double Cross(Vec2 a, Vec2 b) => a.x * b.y - a.y * b.x;
        public static double Dot(Vec2 a, Vec2 b) => a.x * b.x + a.y * b.y;

        /// <summary>Closest point on segment ab to p, with its parameter t in [0, 1].</summary>
        public static ClosestPoint ClosestOnSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            var ab = Sub(b, a);
            double len2 = Dot(ab, ab);
            double t = len2 < EPS ? 0 : Math.Max(0, Math.Min(1, Dot(Sub(p, a), ab) / len2));
            return new ClosestPoint { point = Lerp(a, b, t), t = t };
        }

        public static double DistToSegment(Vec2 p, Vec2 a, Vec2 b) => Dist(p, ClosestOnSegment(p, a, b).point);

        public static double DistToPolyline(Vec2 p, IReadOnlyList<Vec2> pts)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i + 1 < pts.Count; i++) best = Math.Min(best, DistToSegment(p, pts[i], pts[i + 1]));
            return pts.Count == 1 ? Dist(p, pts[0]) : best;
        }

        /// <summary>Closest point on a polyline: segment index, parameter along it, and the point.</summary>
        public static PolylineHit ClosestOnPolyline(Vec2 p, IReadOnlyList<Vec2> pts)
        {
            var best = new PolylineHit { seg = 0, t = 0, point = pts[0], d = double.PositiveInfinity };
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var c = ClosestOnSegment(p, pts[i], pts[i + 1]);
                double d = Dist(p, c.point);
                if (d < best.d) best = new PolylineHit { seg = i, t = c.t, point = c.point, d = d };
            }
            return best;
        }

        /// <summary>Intersection of segments ab and cd with parameters along each, both in [0, 1]; null if parallel or apart.</summary>
        public static SegmentHit? SegmentIntersection(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            var r = Sub(b, a);
            var s = Sub(d, c);
            double denom = Cross(r, s);
            if (Math.Abs(denom) < EPS) return null;
            var ca = Sub(c, a);
            double t = Cross(ca, s) / denom;
            double u = Cross(ca, r) / denom;
            if (t < -EPS || t > 1 + EPS || u < -EPS || u > 1 + EPS) return null;
            return new SegmentHit { t = Math.Max(0, Math.Min(1, t)), u = Math.Max(0, Math.Min(1, u)), point = Lerp(a, b, t) };
        }

        /// <summary>Shortest distance between two segments (0 when they cross).</summary>
        public static double SegmentDistance(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            if (SegmentIntersection(a, b, c, d) != null) return 0;
            return Math.Min(Math.Min(DistToSegment(a, c, d), DistToSegment(b, c, d)), Math.Min(DistToSegment(c, a, b), DistToSegment(d, a, b)));
        }

        public static double PolylineDistance(IReadOnlyList<Vec2> p, IReadOnlyList<Vec2> q)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i + 1 < p.Count; i++)
                for (int j = 0; j + 1 < q.Count; j++)
                    best = Math.Min(best, SegmentDistance(p[i], p[i + 1], q[j], q[j + 1]));
            return best;
        }

        /// <summary>Distance along a polyline to the point on it closest to p.</summary>
        public static double ArcPosition(Vec2 p, IReadOnlyList<Vec2> pts)
        {
            var c = ClosestOnPolyline(p, pts);
            double len = 0;
            for (int i = 0; i < c.seg; i++) len += Dist(pts[i], pts[i + 1]);
            return len + (pts.Count > 1 ? Dist(pts[c.seg], c.point) : 0);
        }

        /// <summary>The part of a polyline between two arc positions.</summary>
        public static List<Vec2> SubPolyline(IReadOnlyList<Vec2> pts, double from, double to)
        {
            var o = new List<Vec2>();
            double acc = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double len = Dist(pts[i], pts[i + 1]);
                double a = acc;
                double b = acc + len;
                if (b >= from && a <= to && len > 0)
                {
                    if (o.Count == 0) o.Add(Lerp(pts[i], pts[i + 1], Math.Max(0, (from - a) / len)));
                    o.Add(Lerp(pts[i], pts[i + 1], Math.Min(1, (to - a) / len)));
                }
                acc = b;
            }
            return o;
        }

        public static double PolylineLength(IReadOnlyList<Vec2> pts)
        {
            double len = 0;
            for (int i = 0; i + 1 < pts.Count; i++) len += Dist(pts[i], pts[i + 1]);
            return len;
        }

        /// <summary>Points every `step` along a polyline, endpoints included.</summary>
        public static List<Vec2> SamplePolyline(IReadOnlyList<Vec2> pts, double step)
        {
            var o = new List<Vec2>();
            if (pts.Count == 0) return o;
            o.Add(pts[0]);
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double len = Dist(pts[i], pts[i + 1]);
                int n = (int)Math.Max(1, Math.Ceiling(len / step));
                for (int k = 1; k <= n; k++) o.Add(Lerp(pts[i], pts[i + 1], (double)k / n));
            }
            return o;
        }

        /// <summary>Signed area; positive for clockwise rings in y-down screen space.</summary>
        public static double PolygonArea(IReadOnlyList<Vec2> poly)
        {
            double a = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % poly.Count];
                a += p.x * q.y - q.x * p.y;
            }
            return a / 2;
        }

        public static Vec2 PolygonCentroid(IReadOnlyList<Vec2> poly)
        {
            double a = PolygonArea(poly);
            if (Math.Abs(a) < EPS)
            {
                var sum = new Vec2(0, 0);
                foreach (var p in poly) sum = Add(sum, p);
                return Scale(sum, 1.0 / Math.Max(1, poly.Count));
            }
            double cx = 0, cy = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % poly.Count];
                double f = p.x * q.y - q.x * p.y;
                cx += (p.x + q.x) * f;
                cy += (p.y + q.y) * f;
            }
            return new Vec2(cx / (6 * a), cy / (6 * a));
        }

        public static bool PointInPolygon(Vec2 p, IReadOnlyList<Vec2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        public static Bounds2 PolygonBounds(IReadOnlyList<Vec2> poly)
        {
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
            foreach (var p in poly)
            {
                x0 = Math.Min(x0, p.x);
                y0 = Math.Min(y0, p.y);
                x1 = Math.Max(x1, p.x);
                y1 = Math.Max(y1, p.y);
            }
            return new Bounds2 { x0 = x0, y0 = y0, x1 = x1, y1 = y1 };
        }

        /// <summary>
        /// Ramer–Douglas–Peucker simplification; endpoints are always kept. A chord is
        /// also split if `segmentOk` rejects it, so callers can forbid shortcuts across
        /// ground the original path avoided.
        /// </summary>
        public static List<Vec2> SimplifyRDP(IReadOnlyList<Vec2> pts, double epsilon, Func<Vec2, Vec2, bool> segmentOk = null)
        {
            if (pts.Count <= 2) return new List<Vec2>(pts);
            var keep = new byte[pts.Count];
            keep[0] = keep[pts.Count - 1] = 1;
            var stack = new List<(int, int)> { (0, pts.Count - 1) };
            while (stack.Count > 0)
            {
                var (i0, i1) = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                int worst = -1;
                double worstD = epsilon;
                for (int i = i0 + 1; i < i1; i++)
                {
                    double d = DistToSegment(pts[i], pts[i0], pts[i1]);
                    if (d > worstD)
                    {
                        worstD = d;
                        worst = i;
                    }
                }
                if (worst < 0 && i1 - i0 > 1 && segmentOk != null && !segmentOk(pts[i0], pts[i1])) worst = (i0 + i1) >> 1;
                if (worst >= 0)
                {
                    keep[worst] = 1;
                    stack.Add((i0, worst));
                    stack.Add((worst, i1));
                }
            }
            var o = new List<Vec2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i] != 0) o.Add(pts[i]);
            return o;
        }

        /// <summary>Chaikin corner cutting on an open polyline; endpoints are kept.</summary>
        public static List<Vec2> Chaikin(IReadOnlyList<Vec2> pts, int passes)
        {
            var cur = new List<Vec2>(pts);
            for (int p = 0; p < passes && cur.Count > 2; p++)
            {
                var next = new List<Vec2>(cur.Count * 2) { cur[0] };
                for (int i = 0; i + 1 < cur.Count; i++)
                {
                    next.Add(Lerp(cur[i], cur[i + 1], 0.25));
                    next.Add(Lerp(cur[i], cur[i + 1], 0.75));
                }
                next.Add(cur[cur.Count - 1]);
                cur = next;
            }
            return cur;
        }

        /// <summary>Drop vertices that sit on a straight line between their neighbours.</summary>
        public static List<Vec2> RemoveCollinear(IReadOnlyList<Vec2> ring)
        {
            var o = new List<Vec2>();
            for (int i = 0; i < ring.Count; i++)
            {
                var prev = ring[(i - 1 + ring.Count) % ring.Count];
                var p = ring[i];
                var next = ring[(i + 1) % ring.Count];
                if (Math.Abs(Cross(Sub(p, prev), Sub(next, p))) > EPS) o.Add(p);
            }
            return o;
        }

        /// <summary>
        /// Outline loops of the cells set in `mask`, traced along cell edges with the
        /// cells on the right (clockwise in y-down space). Rings sorted by descending
        /// area, so ring 0 is the outer boundary and the rest are holes.
        /// </summary>
        public static List<List<Vec2>> TraceCellOutlines(byte[] mask, int width, int height)
        {
            int W = width + 1;
            int[] DX = { 1, 0, -1, 0 };
            int[] DY = { 0, 1, 0, -1 };
            var edgesFrom = new Dictionary<int, List<int>>();
            var edgeStart = new List<int>();
            var edgeDir = new List<int>();
            bool At(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x] == 1;
            void AddEdge(int cx, int cy, int dir)
            {
                int id = edgeStart.Count;
                int corner = cy * W + cx;
                edgeStart.Add(corner);
                edgeDir.Add(dir);
                if (edgesFrom.TryGetValue(corner, out var list)) list.Add(id);
                else edgesFrom[corner] = new List<int> { id };
            }
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!At(x, y)) continue;
                    if (!At(x, y - 1)) AddEdge(x, y, 0);
                    if (!At(x + 1, y)) AddEdge(x + 1, y, 1);
                    if (!At(x, y + 1)) AddEdge(x + 1, y + 1, 2);
                    if (!At(x - 1, y)) AddEdge(x, y + 1, 3);
                }
            }
            var used = new byte[edgeStart.Count];
            var rings = new List<List<Vec2>>();
            var empty = new List<int>();
            int[] turns = { 1, 0, 3 };
            for (int e0 = 0; e0 < edgeStart.Count; e0++)
            {
                if (used[e0] != 0) continue;
                var ring = new List<Vec2>();
                int e = e0;
                while (used[e] == 0)
                {
                    used[e] = 1;
                    int corner = edgeStart[e];
                    ring.Add(new Vec2(corner % W, corner / W));
                    int d = edgeDir[e];
                    int endCorner = corner + DY[d] * W + DX[d];
                    var options = edgesFrom.TryGetValue(endCorner, out var l) ? l : empty;
                    int next = -1;
                    foreach (int turn in turns)
                    {
                        int want = (d + turn) % 4;
                        foreach (int o in options)
                        {
                            if (used[o] == 0 && edgeDir[o] == want)
                            {
                                next = o;
                                break;
                            }
                        }
                        if (next >= 0) break;
                    }
                    if (next < 0) break;
                    e = next;
                }
                var simplified = RemoveCollinear(ring);
                if (simplified.Count >= 3) rings.Add(simplified);
            }
            Numbers.StableSort(rings, (a, b) => Math.Abs(PolygonArea(b)) - Math.Abs(PolygonArea(a)));
            return rings;
        }
    }
}

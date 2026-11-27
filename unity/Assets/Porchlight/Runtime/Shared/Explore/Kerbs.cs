// The kerbs: every street's carriageway (its asphalt and parking lanes, and a
// cul-de-sac's bulb) lies a kerb's height below the sidewalks and the land
// behind them. Everything else stays at height 0, so buildings, trees, yards
// and whoever walks the sidewalks need nothing changed; only what's on the
// road goes down to it. Worked out from the layout's own strokes, the same
// shapes the explore view draws the asphalt with, so the kerb drawn and the
// kerb stood on are one line. Also what keeps the yards' fences off the
// sidewalks. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class Kerbs
    {
        /// <summary>How far a carriageway lies below the sidewalk (tiles: about 15 cm).</summary>
        public const double HEIGHT = 0.05;
        /// <summary>The kerb stone's top, the lighter strip along the sidewalk's road edge (tiles).</summary>
        public const double TOP = 0.04;
        /// <summary>The darker edging strip along the sidewalk's outer edge (tiles).</summary>
        public const double EDGING = 0.05;
        /// <summary>How far the drawn sidewalk reaches from the kerb line, its edging included.</summary>
        public const double REACH = Layout.SIDEWALK + EDGING;
        /// <summary>
        /// How far either side of the kerb line the ground eases from road to sidewalk height (tiles): a car or a
        /// walker going over it rises smoothly instead of jumping. Mostly on the road side, so anyone stood on
        /// the sidewalk's edge is up on it.
        /// </summary>
        const double EASE_DOWN = 0.1, EASE_UP = 0.03;
        /// <summary>How far from a kerb Edge measures exactly; past this it just says "far".</summary>
        public const double FAR = 2;

        readonly int w, h;
        /// <summary>Each shape a capsule round a segment (a disc where its ends are the same point).</summary>
        readonly List<(double ax, double ay, double bx, double by, double r)> shapes = new List<(double, double, double, double, double)>();
        /// <summary>For each tile, the shapes that come within FAR of it.</summary>
        readonly List<int>[] cells;

        Kerbs(int w, int h)
        {
            this.w = w;
            this.h = h;
            cells = new List<int>[w * h];
        }

        /// <summary>The town's kerbs, from its layout's streets and cul-de-sacs (footpaths have none).</summary>
        public static Kerbs Of(TownModel model, IsoLayout layout)
        {
            var k = new Kerbs(model.width, model.height);
            foreach (var r in layout.roads)
            {
                if (r.kind == RoadKind.Path) continue;
                // The asphalt and both parking lanes; round at its bends and ends, as the explore view draws it.
                double half = r.width / 2 + Layout.PARKING;
                if (r.points.Count == 1) k.Add(r.points[0], r.points[0], half);
                for (int i = 1; i < r.points.Count; i++) k.Add(r.points[i - 1], r.points[i], half);
            }
            double bulb = Gen.Raster.BulbRadius(model.@params) + Layout.PARKING;
            foreach (var b in layout.bulbs) k.Add(b.at, b.at, bulb);
            return k;
        }

        void Add(Vec2 a, Vec2 b, double r)
        {
            int s = shapes.Count;
            shapes.Add((a.x, a.y, b.x, b.y, r));
            double reach = r + FAR;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.x, b.x) - reach)), x1 = Math.Min(w - 1, (int)Math.Floor(Math.Max(a.x, b.x) + reach));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.y, b.y) - reach)), y1 = Math.Min(h - 1, (int)Math.Floor(Math.Max(a.y, b.y) + reach));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * w + x;
                    (cells[i] ??= new List<int>()).Add(s);
                }
        }

        /// <summary>
        /// How far a map point is from the nearest kerb line: negative on a carriageway, 0 on the kerb, the
        /// sidewalk out to Layout.SIDEWALK, the land beyond. FAR wherever no street comes within it.
        /// </summary>
        public double Edge(double x, double y)
        {
            int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
            if (cx < 0) cx = 0;
            if (cy < 0) cy = 0;
            if (cx >= w) cx = w - 1;
            if (cy >= h) cy = h - 1;
            var near = cells[cy * w + cx];
            double best = FAR;
            if (near == null) return best;
            foreach (int s in near)
            {
                var (ax, ay, bx, by, r) = shapes[s];
                double dx = bx - ax, dy = by - ay, px = x - ax, py = y - ay;
                double len2 = dx * dx + dy * dy;
                double t = len2 > 1e-12 ? Math.Max(0, Math.Min(1, (px * dx + py * dy) / len2)) : 0;
                double ex = px - t * dx, ey = py - t * dy;
                double d = Math.Sqrt(ex * ex + ey * ey) - r;
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>Whether a map point is on a carriageway (below the kerb).</summary>
        public bool OnRoad(double x, double y) => Edge(x, y) < 0;

        /// <summary>
        /// How high the ground is at a map point: 0 on the sidewalks and everywhere off the streets, -HEIGHT on a
        /// carriageway, easing between the two over the kerb itself.
        /// </summary>
        public double Ground(double x, double y)
        {
            double e = Edge(x, y);
            if (e >= EASE_UP) return 0;
            if (e <= -EASE_DOWN) return -HEIGHT;
            double t = (e + EASE_DOWN) / (EASE_DOWN + EASE_UP);
            return -HEIGHT * (1 - t * t * (3 - 2 * t));
        }

        // The ground cut along the kerbs -----------------------------------------------------------

        /// <summary>
        /// The bands across a street's edge, out from the road, that Cut makes pieces of ground in: the road, the
        /// sidewalk (out to REACH, its kerb stone's top and its edging drawn over its two edges), and the land.
        /// </summary>
        public const int ROAD = 0, SIDEWALK = 1, LAND = 2;
        /// <summary>Where each band after the road begins, as Edge measures it.</summary>
        static readonly double[] FROM = { double.NegativeInfinity, 0, REACH };
        /// <summary>Half a tile's diagonal: no point of a tile is farther than this from its middle.</summary>
        const double HALF_DIAGONAL = 0.7072;
        /// <summary>A tile is cut whole where Edge runs this straight across it (a straight street's side), else in quarters.</summary>
        const double STRAIGHT = 0.01;
        /// <summary>A quarter tile is cut in four round its middle where Edge there is this far from what its corners say.</summary>
        const double BENT = 0.04;

        static int BandOf(double e) => e < 0 ? ROAD : e < REACH ? SIDEWALK : LAND;

        /// <summary>
        /// A tile's ground cut along the kerbs, for the explore view to draw: flat pieces of it (convex, corners in
        /// order round their edge), each in its band; and the kerb's face, a stretch of the kerb line at a time,
        /// with which way it faces (towards the road), and likewise the sidewalk's outer edge (`outer`), facing
        /// away from it. Edge is taken as straight across each piece cut: exact along a straight street, where
        /// a tile is cut whole; a quarter of a tile at a time round a bend, a corner or a bulb, a little cornered.
        /// False, with nothing added, for a tile no sidewalk reaches into: drawn whole as before.
        /// </summary>
        public bool Cut(int x, int y, List<(int band, List<Vec2> poly)> pieces, List<(Vec2 a, Vec2 b, Vec2 facing, bool outer)> faces)
        {
            double mid = Edge(x + 0.5, y + 0.5);
            if (mid >= REACH + HALF_DIAGONAL) return false;
            if (mid <= -HALF_DIAGONAL)
            {
                pieces.Add((ROAD, new List<Vec2> { new Vec2(x, y), new Vec2(x + 1, y), new Vec2(x + 1, y + 1), new Vec2(x, y + 1) }));
                return true;
            }
            var e = new double[3, 3];
            for (int j = 0; j <= 2; j++)
                for (int i = 0; i <= 2; i++) e[i, j] = Edge(x + i * 0.5, y + j * 0.5);
            (Vec2, double) At(int i, int j) => (new Vec2(x + i * 0.5, y + j * 0.5), e[i, j]);
            // Straight across the whole tile: its middle and the middles of its sides where its corners say.
            bool straight =
                Math.Abs(e[1, 1] - (e[0, 0] + e[2, 0] + e[0, 2] + e[2, 2]) / 4) <= STRAIGHT &&
                Math.Abs(e[1, 0] - (e[0, 0] + e[2, 0]) / 2) <= STRAIGHT && Math.Abs(e[1, 2] - (e[0, 2] + e[2, 2]) / 2) <= STRAIGHT &&
                Math.Abs(e[0, 1] - (e[0, 0] + e[0, 2]) / 2) <= STRAIGHT && Math.Abs(e[2, 1] - (e[2, 0] + e[2, 2]) / 2) <= STRAIGHT;
            int first = faces.Count;
            if (straight) Piece(new List<(Vec2 p, double e)> { At(0, 0), At(2, 0), At(2, 2), At(0, 2) }, pieces, faces);
            else
                for (int j = 0; j < 2; j++)
                    for (int i = 0; i < 2; i++)
                        Cell(new List<(Vec2 p, double e)> { At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j + 1) }, pieces, faces);
            Join(faces, first);
            return true;
        }

        /// <summary>The faces added since `first` that carry straight on from one another, each pair made one: fewer to draw.</summary>
        static void Join(List<(Vec2 a, Vec2 b, Vec2 facing, bool outer)> faces, int first)
        {
            for (int i = first; i < faces.Count; i++)
                for (int j = i + 1; j < faces.Count; j++)
                {
                    var f = faces[i];
                    var g = faces[j];
                    if (f.outer != g.outer || f.facing.x * g.facing.x + f.facing.y * g.facing.y < 0.99999) continue;
                    Vec2 a, b;
                    if (Geo.Dist(f.b, g.a) < 1e-9) (a, b) = (f.a, g.b);
                    else if (Geo.Dist(g.b, f.a) < 1e-9) (a, b) = (g.a, f.b);
                    else if (Geo.Dist(f.a, g.a) < 1e-9) (a, b) = (f.b, g.b);
                    else if (Geo.Dist(f.b, g.b) < 1e-9) (a, b) = (f.a, g.a);
                    else continue;
                    faces[i] = (a, b, f.facing, f.outer);
                    faces.RemoveAt(j);
                    j = i;
                }
        }

        /// <summary>
        /// A square (Edge taken as straight along its sides) cut into its bands: whole if it's all one, else in two
        /// triangles, or in four round its middle where Edge bends inside it (a sidewalk's corner coming to a point).
        /// </summary>
        void Cell(List<(Vec2 p, double e)> quad, List<(int band, List<Vec2> poly)> pieces, List<(Vec2 a, Vec2 b, Vec2 facing, bool outer)> faces)
        {
            var m = new Vec2((quad[0].p.x + quad[2].p.x) / 2, (quad[0].p.y + quad[2].p.y) / 2);
            var mid = (m, Edge(m.x, m.y));
            int b0 = BandOf(quad[0].e);
            bool one = BandOf(quad[1].e) == b0 && BandOf(quad[2].e) == b0 && BandOf(quad[3].e) == b0;
            if (one && BandOf(mid.Item2) == b0)
            {
                pieces.Add((b0, new List<Vec2> { quad[0].p, quad[1].p, quad[2].p, quad[3].p }));
                return;
            }
            if (one || Math.Abs(mid.Item2 - (quad[0].e + quad[1].e + quad[2].e + quad[3].e) / 4) > BENT)
            {
                for (int k = 0; k < 4; k++) Piece(new List<(Vec2 p, double e)> { quad[k], quad[(k + 1) % 4], mid }, pieces, faces);
                return;
            }
            // Split along the diagonal whose ends are nearer level, so a kerb across a corner is cut cleanly.
            if (Math.Abs(quad[0].e - quad[2].e) <= Math.Abs(quad[1].e - quad[3].e))
            {
                Piece(new List<(Vec2 p, double e)> { quad[0], quad[1], quad[2] }, pieces, faces);
                Piece(new List<(Vec2 p, double e)> { quad[0], quad[2], quad[3] }, pieces, faces);
            }
            else
            {
                Piece(new List<(Vec2 p, double e)> { quad[1], quad[2], quad[3] }, pieces, faces);
                Piece(new List<(Vec2 p, double e)> { quad[1], quad[3], quad[0] }, pieces, faces);
            }
        }

        /// <summary>
        /// A convex polygon (Edge straight across it: a triangle, or a tile a straight street's side crosses) cut into
        /// its bands, and the kerb's face and the sidewalk's outer edge where they cross it.
        /// </summary>
        static void Piece(List<(Vec2 p, double e)> tri, List<(int band, List<Vec2> poly)> pieces, List<(Vec2 a, Vec2 b, Vec2 facing, bool outer)> faces)
        {
            var a = tri[0];
            var b = tri[1];
            var c = tri[2];
            int lo = LAND, hi = ROAD;
            foreach (var q in tri)
            {
                lo = Math.Min(lo, BandOf(q.e));
                hi = Math.Max(hi, BandOf(q.e));
            }
            for (int k = lo; k <= hi; k++)
            {
                var poly = tri;
                if (k > ROAD) poly = Clip(poly, FROM[k], true);
                if (k < LAND) poly = Clip(poly, FROM[k + 1], false);
                if (poly.Count < 3) continue;
                var pts = new List<Vec2>(poly.Count);
                foreach (var q in poly) pts.Add(q.p);
                pieces.Add((k, pts));
            }
            // Which way Edge rises across the triangle: away from the road.
            double ux = b.p.x - a.p.x, uy = b.p.y - a.p.y, vx = c.p.x - a.p.x, vy = c.p.y - a.p.y;
            double det = ux * vy - uy * vx;
            if (Math.Abs(det) < 1e-12) return;
            double du = b.e - a.e, dv = c.e - a.e;
            double gx = (du * vy - dv * uy) / det, gy = (dv * ux - du * vx) / det;
            double g = Math.Sqrt(gx * gx + gy * gy);
            if (g < 1e-9) return;
            gx /= g;
            gy /= g;
            foreach (var (level, outer) in new[] { (0.0, false), (REACH, true) })
            {
                var cross = new List<Vec2>(2);
                for (int k = 0; k < tri.Count; k++)
                {
                    var p = tri[k];
                    var q = tri[(k + 1) % tri.Count];
                    if ((p.e < level) == (q.e < level)) continue;
                    double t = (level - p.e) / (q.e - p.e);
                    cross.Add(new Vec2(p.p.x + (q.p.x - p.p.x) * t, p.p.y + (q.p.y - p.p.y) * t));
                }
                if (cross.Count == 2 && Geo.Dist(cross[0], cross[1]) > 1e-6) faces.Add((cross[0], cross[1], outer ? new Vec2(gx, gy) : new Vec2(-gx, -gy), outer));
            }
        }

        /// <summary>A convex polygon (Edge straight across it) cut to where Edge is at least `level` (`above`), or below it.</summary>
        static List<(Vec2 p, double e)> Clip(List<(Vec2 p, double e)> poly, double level, bool above)
        {
            var o = new List<(Vec2 p, double e)>(poly.Count + 2);
            for (int k = 0; k < poly.Count; k++)
            {
                var p = poly[k];
                var q = poly[(k + 1) % poly.Count];
                bool pIn = above ? p.e >= level : p.e < level;
                bool qIn = above ? q.e >= level : q.e < level;
                if (pIn) o.Add(p);
                if (pIn != qIn)
                {
                    double t = (level - p.e) / (q.e - p.e);
                    o.Add((new Vec2(p.p.x + (q.p.x - p.p.x) * t, p.p.y + (q.p.y - p.p.y) * t), level));
                }
            }
            return o;
        }

        /// <summary>The stretches of a line (a footpath) at least `clear` from every carriageway: up on the land, never out over a road below.</summary>
        public List<List<Vec2>> OffRoad(IReadOnlyList<Vec2> pts, double clear, double step = 0.1)
        {
            var runs = new List<List<Vec2>>();
            List<Vec2> run = null;
            foreach (var p in Geo.SamplePolyline(pts, step))
            {
                if (Edge(p.x, p.y) >= clear)
                {
                    (run ??= new List<Vec2>()).Add(p);
                    continue;
                }
                if (run != null && run.Count > 1) runs.Add(run);
                run = null;
            }
            if (run != null && run.Count > 1) runs.Add(run);
            return runs;
        }

        // Paving ------------------------------------------------------------------------------------

        /// <summary>How long a sidewalk's slabs are along it (tiles): about as long as the sidewalk is wide.</summary>
        public const double SLAB = 0.42;
        /// <summary>How wide a joint between slabs is drawn, and a crack.</summary>
        public const double JOINT = 0.012, CRACK = 0.008;
        /// <summary>The share of slabs with a crack across them.</summary>
        const double CRACKED = 0.06;
        /// <summary>A joint is drawn only where its street is the nearest along all of it (so none cross at a corner): to within this.</summary>
        const double TRUE_TO = 0.02;

        /// <summary>
        /// The sidewalks' paving: a joint between each slab and the next, square across the sidewalk (round a
        /// cul-de-sac's bulb too), and here and there a slab cracked. Each a short straight line to draw thin
        /// (`crack` tells which); the same lines every time for the same town. None at a street corner, where
        /// two sidewalks meet and the slabs would cross.
        /// </summary>
        public List<(Vec2 a, Vec2 b, bool crack)> Paving(IsoLayout layout, double bulbRadius)
        {
            var o = new List<(Vec2 a, Vec2 b, bool crack)>();
            const double IN = TOP, OUT = Layout.SIDEWALK;
            bool Across(Vec2 p, double want) => Math.Abs(Edge(p.x, p.y) - want) <= TRUE_TO;
            void Joint(Vec2 at, Vec2 n, double half)
            {
                var a = new Vec2(at.x + n.x * (half + IN), at.y + n.y * (half + IN));
                var b = new Vec2(at.x + n.x * (half + OUT), at.y + n.y * (half + OUT));
                var m = new Vec2((a.x + b.x) / 2, (a.y + b.y) / 2);
                if (Across(a, IN) && Across(b, OUT) && Across(m, (IN + OUT) / 2)) o.Add((a, b, false));
            }
            // A crack across a slab: from near one of its long edges, a few jagged steps over it.
            void Crack(int seed, Vec2 at, Vec2 along, Vec2 n, double half)
            {
                if (Unit(seed, 0) >= CRACKED) return;
                double u = SLAB * (0.2 + 0.6 * Unit(seed, 1));
                double v = Unit(seed, 2) < 0.5 ? IN + 0.02 : OUT - 0.02;
                double toward = v < (IN + OUT) / 2 ? 1 : -1;
                int steps = 2 + (int)(Unit(seed, 3) * 2);
                var pts = new List<Vec2>();
                for (int k = 0; k <= steps; k++)
                {
                    pts.Add(new Vec2(at.x + along.x * u + n.x * (half + v), at.y + along.y * u + n.y * (half + v)));
                    u = Math.Max(0.03, Math.Min(SLAB - 0.03, u + (Unit(seed, 10 + k) - 0.5) * 0.12));
                    v = Math.Max(IN + 0.01, Math.Min(OUT - 0.01, v + toward * (OUT - IN) * (0.15 + 0.2 * Unit(seed, 20 + k))));
                }
                // Only a crack wholly on the sidewalk (not at a corner, where another street comes near).
                foreach (var p in pts)
                {
                    double e = Edge(p.x, p.y);
                    if (e < IN || e > OUT) return;
                }
                for (int k = 1; k < pts.Count; k++) o.Add((pts[k - 1], pts[k], true));
            }
            for (int r = 0; r < layout.roads.Count; r++)
            {
                var road = layout.roads[r];
                if (road.kind == RoadKind.Path) continue;
                double half = road.width / 2 + Layout.PARKING;
                var pts = road.points;
                double travelled = 0;
                // A joint every SLAB along the street's own line, at the same lengths along both sides.
                double next = SLAB;
                for (int i = 1; i < pts.Count; i++)
                {
                    var a = pts[i - 1];
                    var b = pts[i];
                    double len = Geo.Dist(a, b);
                    if (len < 1e-9) continue;
                    var along = new Vec2((b.x - a.x) / len, (b.y - a.y) / len);
                    var left = new Vec2(along.y, -along.x);
                    while (next <= travelled + len)
                    {
                        double t = next - travelled;
                        var at = new Vec2(a.x + along.x * t, a.y + along.y * t);
                        int slab = (int)Math.Round(next / SLAB);
                        foreach (int side in new[] { 1, -1 })
                        {
                            var n = new Vec2(left.x * side, left.y * side);
                            Joint(at, n, half);
                            Crack((r * 2 + (side > 0 ? 0 : 1)) * 100003 + slab, at, along, n, half);
                        }
                        next += SLAB;
                    }
                    travelled += len;
                }
            }
            // Round a bulb: square across its sidewalk, about a slab apart at its middle.
            foreach (var q in layout.bulbs)
            {
                double half = bulbRadius + Layout.PARKING;
                int count = Math.Max(8, (int)Math.Round(2 * Math.PI * (half + (IN + OUT) / 2) / SLAB));
                for (int k = 0; k < count; k++)
                {
                    double ang = 2 * Math.PI * k / count;
                    Joint(q.at, new Vec2(Math.Cos(ang), Math.Sin(ang)), half);
                }
            }
            return o;
        }

        /// <summary>A number in 0..1 from a seed and a salt, the same every time (an integer hash: nothing platform-dependent).</summary>
        static double Unit(int seed, int salt)
        {
            unchecked
            {
                uint h = (uint)seed * 0x9E3779B1u ^ (uint)salt * 0x85EBCA77u;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                h *= 0x297A2D39u;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / (double)0x1000000;
            }
        }

        /// <summary>How far a yard's fence (its middle line) keeps from a kerb: past the drawn sidewalk and its edging, and half a fence's thickness more.</summary>
        public const double FENCE_CLEAR = REACH + 0.04;
        /// <summary>Shorter than this, what's left of a fence once it's clear of the sidewalk isn't worth standing.</summary>
        const double SHORTEST_FENCE = 0.2;

        /// <summary>
        /// The yards' fences, each cut back clear of the sidewalks and the roads (FENCE_CLEAR from every kerb line),
        /// or left out where too little of it is clear. A fence along a tile's edge often ran out over a sidewalk
        /// that reaches into the tile: the tiles' streets are only the tiles whose middles the road is near.
        /// </summary>
        public List<Fence> ClearOf(List<Fence> fences)
        {
            const int STEPS = 40;
            const double STEP = 1.0 / STEPS;
            var o = new List<Fence>(fences.Count);
            var ok = new bool[STEPS + 1];
            foreach (var f in fences)
            {
                var (a, b) = Layout.FenceLine(f);
                double len = Geo.Dist(a, b);
                // Sampled every STEP along it, with STEP / 2 to spare: Edge changes no faster than the
                // distance moved, so between two clear samples it's clear too.
                bool all = true;
                for (int i = 0; i <= STEPS; i++)
                {
                    double t = (double)i / STEPS;
                    ok[i] = Edge(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t) >= FENCE_CLEAR + STEP * len / 2;
                    all &= ok[i];
                }
                if (all)
                {
                    o.Add(f);
                    continue;
                }
                // The longest clear run along it, the first if there's a tie.
                int bestFrom = -1, bestTo = -1;
                for (int i = 0; i <= STEPS; i++)
                {
                    if (!ok[i]) continue;
                    int j = i;
                    while (j + 1 <= STEPS && ok[j + 1]) j++;
                    if (j - i > bestTo - bestFrom) (bestFrom, bestTo) = (i, j);
                    i = j;
                }
                if (bestFrom < 0 || (bestTo - bestFrom) * STEP * len < SHORTEST_FENCE) continue;
                var kept = f;
                kept.cut0 = f.cut0 + bestFrom * STEP * len;
                kept.cut1 = f.cut1 + (STEPS - bestTo) * STEP * len;
                o.Add(kept);
            }
            return o;
        }
    }
}

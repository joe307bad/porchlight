// The lines painted on the streets, as geometry (the game draws each as a flat ribbon, Rendering/World.cs): each
// street's edge lines, its dashed centre line, the dashes between two lanes each way, and each turn pocket's lane
// edge and arrow. Worked out here, not in the game, so a check can hold them to their rule (Checks `marks`).
//
// The rule: a street's markings stop before they reach another road's carriageway. Near a junction a street's lines
// are cut back until all three (both edges and the middle) are clear of every other road there, however the roads
// meet: square on, at a slant, or with a bend just short of the junction (a smoothed bend carries a street's lines
// across the road it meets). A roundabout's ring and a cul-de-sac's bulb cut them back too. And a line keeps its
// shape round a bend: an edge line only where it's really at the street's edge (on the inside of a bend tighter
// than half the street, shifting the line out folds it into a loop: it's left off there), and a dash only where
// it's nearly straight (one curled round a sharp bend reads as a turn arrow).

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public enum MarkKind { EdgeLine, CentreDash, LaneDash, PocketLine, Arrow }

    public sealed class Mark
    {
        public MarkKind kind;
        /// <summary>The street it belongs to (index into IsoLayout.roads).</summary>
        public int road;
        public List<Vec2> points;
        /// <summary>How wide the painted line is (tiles).</summary>
        public double width;
    }

    public static class Markings
    {
        /// <summary>The asphalt round a roundabout's island reaches this far past its design radius (the game's road layers).</summary>
        public const double RING_OUT = 0.55;
        /// <summary>The least a street's lines stop short of its junction (tiles): the edge lines, then the dashes and pockets.</summary>
        public const double EDGE_STOP = 1.3, DASH_STOP = 1.2;
        /// <summary>How far clear of another road's carriageway (past its kerb-to-kerb half width) a street's lines stop.</summary>
        public const double CLEAR = 0.15;
        const double STEP = 0.1;

        /// <summary>An edge line's points no nearer the street's middle than its half width less this (tiles).</summary>
        public const double ON_EDGE = 0.05;
        /// <summary>The most a dash turns, end to end (degrees).</summary>
        public const double DASH_TURN = 30;
        /// <summary>A piece of edge line shorter than this (tiles), what's left of it round a bend, isn't drawn.</summary>
        const double SHORTEST = 0.3;

        public const double EDGE_WIDTH = 0.04, CENTRE_WIDTH = 0.07, LANE_WIDTH = 0.06, POCKET_WIDTH = 0.05, ARROW_WIDTH = 0.07;

        /// <summary>How far from a road's end its markings stop for a roundabout: past the ring if the end is in one, else 0.</summary>
        public static double RingTrim(List<(Vec2 at, double radius)> rings, Vec2 end)
        {
            double most = 0;
            foreach (var (at, radius) in rings)
            {
                double d = Geo.Dist(at, end);
                if (d < radius + RING_OUT) most = Math.Max(most, radius + RING_OUT + 0.15 - d);
            }
            return most;
        }

        /// <summary>The roundabouts' rings (their middles and design radii), as the game draws them.</summary>
        public static List<(Vec2 at, double radius)> Rings(TownModel model, IsoLayout layout)
        {
            var rings = new List<(Vec2 at, double radius)>();
            var design = DesignOf(model, layout);
            if (design == null) return rings;
            foreach (var nd in design.nodes)
                if (nd.form == JunctionForm.Roundabout && nd.radius > 0) rings.Add((model.roads.nodes[nd.node].at, nd.radius));
            return rings;
        }

        /// <summary>The junction design, if it's for these roads (one per layout road).</summary>
        public static RoadDesign DesignOf(TownModel model, IsoLayout layout) =>
            model.design != null && model.design.edges.Count == layout.roads.Count ? model.design : null;

        /// <summary>Each road's box (null for a path), to find the roads near a point quickly.</summary>
        public static (double x0, double y0, double x1, double y1)?[] Boxes(IsoLayout layout)
        {
            var boxes = new (double, double, double, double)?[layout.roads.Count];
            for (int i = 0; i < layout.roads.Count; i++)
            {
                var s = layout.roads[i];
                if (s.kind == RoadKind.Path || s.points.Count < 2) continue;
                var b = Geo.PolygonBounds(s.points);
                boxes[i] = (b.x0, b.y0, b.x1, b.y1);
            }
            return boxes;
        }

        /// <summary>
        /// How far along the street from this end (tiles) its lines may begin: at least `least`, and on until its edge
        /// lines and middle are all CLEAR of the carriageway of every other road near that end (those it meets there,
        /// and any it ends beside without meeting). At most half the street (two junctions close together leave it
        /// unmarked rather than marked across them).
        /// </summary>
        public static double JunctionTrim(IsoLayout layout, int road, bool atEnd, double least, (double x0, double y0, double x1, double y1)?[] boxes = null)
        {
            boxes ??= Boxes(layout);
            var r = layout.roads[road];
            var end = atEnd ? r.points[r.points.Count - 1] : r.points[0];
            var others = new List<RoadStroke>();
            for (int i = 0; i < layout.roads.Count; i++)
            {
                if (i == road || boxes[i] is not { } b) continue;
                var s = layout.roads[i];
                // Near enough that the street's lines, as far as they're looked at (half its length), could reach it.
                double reach = s.width / 2 + r.width / 2 + CLEAR + 0.5;
                if (end.x < b.x0 - reach - 8 || end.x > b.x1 + reach + 8 || end.y < b.y0 - reach - 8 || end.y > b.y1 + reach + 8) continue;
                if (Geo.DistToPolyline(end, s.points) < reach + Math.Min(8, Geo.PolylineLength(r.points) / 2)) others.Add(s);
            }
            if (others.Count == 0) return least;
            var pts = new List<Vec2>(r.points);
            if (atEnd) pts.Reverse();
            double length = Geo.PolylineLength(pts), half = r.width / 2;
            for (double t = least; t <= length / 2; t += STEP)
            {
                var (at, dir) = Along(pts, t);
                var n = new Vec2(-dir.y, dir.x);
                bool clear = true;
                foreach (double o in new[] { -half, 0, half })
                {
                    var q = new Vec2(at.x + n.x * o, at.y + n.y * o);
                    foreach (var s in others)
                        if (Geo.DistToPolyline(q, s.points) < s.width / 2 + CLEAR)
                        {
                            clear = false;
                            break;
                        }
                    if (!clear) break;
                }
                if (clear) return t;
            }
            return Math.Max(least, length / 2);
        }

        /// <summary>The pieces of an edge line that are really at the street's edge (no nearer its middle than its half width).</summary>
        static List<List<Vec2>> OnEdge(List<Vec2> line, RoadStroke r) => Pieces(line, q => Geo.DistToPolyline(q, r.points) < r.width / 2 - ON_EDGE);

        /// <summary>
        /// The pieces of a line away from where it's `off`: its own points (as many as it had), each long stretch
        /// looked at along its length too; pieces shorter than SHORTEST left out.
        /// </summary>
        static List<List<Vec2>> Pieces(List<Vec2> line, Func<Vec2, bool> off)
        {
            var pieces = new List<List<Vec2>>();
            var run = new List<Vec2>();
            void Flush()
            {
                if (run.Count > 1 && Geo.PolylineLength(run) >= SHORTEST) pieces.Add(run);
                run = new List<Vec2>();
            }
            for (int i = 0; i < line.Count; i++)
            {
                if (i > 0)
                {
                    var a = line[i - 1];
                    var b = line[i];
                    int n = (int)Math.Floor(Geo.Dist(a, b) / STEP);
                    for (int k = 1; k < n; k++)
                    {
                        if (!off(Geo.Lerp(a, b, k / (double)n))) continue;
                        // Off partway along: the run stops just before, and starts again just after.
                        if (run.Count > 0) run.Add(Geo.Lerp(a, b, (k - 1) / (double)n));
                        Flush();
                        while (k < n && off(Geo.Lerp(a, b, k / (double)n))) k++;
                        if (k < n) run.Add(Geo.Lerp(a, b, k / (double)n));
                    }
                }
                if (off(line[i])) Flush();
                else run.Add(line[i]);
            }
            Flush();
            return pieces;
        }

        /// <summary>Whether a dash turns no more than DASH_TURN from one end to the other.</summary>
        public static bool Straight(List<Vec2> dash)
        {
            var a0 = dash[0];
            var a1 = dash[1];
            var b0 = dash[dash.Count - 2];
            var b1 = dash[dash.Count - 1];
            double h0 = Math.Atan2(a1.y - a0.y, a1.x - a0.x), h1 = Math.Atan2(b1.y - b0.y, b1.x - b0.x);
            double turn = Math.Abs(Math.IEEERemainder(h1 - h0, 2 * Math.PI)) * 180 / Math.PI;
            return turn <= DASH_TURN;
        }

        /// <summary>The point `t` along a polyline and the way it's heading there (a unit vector).</summary>
        static (Vec2 at, Vec2 dir) Along(List<Vec2> pts, double t)
        {
            double gone = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double seg = Geo.Dist(pts[i], pts[i + 1]);
                if (seg < 1e-9) continue;
                if (gone + seg >= t || i + 2 == pts.Count)
                {
                    double f = Math.Min(1, (t - gone) / seg);
                    var dir = new Vec2((pts[i + 1].x - pts[i].x) / seg, (pts[i + 1].y - pts[i].y) / seg);
                    return (new Vec2(pts[i].x + (pts[i + 1].x - pts[i].x) * f, pts[i].y + (pts[i + 1].y - pts[i].y) * f), dir);
                }
                gone += seg;
            }
            return (pts[pts.Count - 1], new Vec2(1, 0));
        }

        /// <summary>Every street's markings (not a roundabout's own: the game draws those round its ring).</summary>
        public static List<Mark> Plan(TownModel model, IsoLayout layout)
        {
            var marks = new List<Mark>();
            foreach (var _ in PlanSteps(model, layout, marks, int.MaxValue)) { }
            return marks;
        }

        /// <summary>The same, into `marks`, a step after each `roadsAStep` streets (Porchlight's are ~90 ms all told).</summary>
        public static IEnumerable<string> PlanSteps(TownModel model, IsoLayout layout, List<Mark> marks, int roadsAStep)
        {
            var design = DesignOf(model, layout);
            var rings = Rings(model, layout);
            var boxes = Boxes(layout);
            double bulb = Gen.Raster.BulbRadius(model.@params);
            bool Bulb(int road, bool atEnd) => layout.bulbs.Exists(q => q.edge == road && q.atEnd == atEnd);
            const double LANE = Gen.DesignStage.LANE;
            for (int ri = 0; ri < layout.roads.Count; ri++)
            {
                if ((ri + 1) % roadsAStep == 0) yield return "marks";
                var r = layout.roads[ri];
                if (r.kind == RoadKind.Path || r.points.Count < 2) continue;
                int first = marks.Count;
                Road(ri, r);
                // Off every roundabout's ring and every other road's carriageway wherever they are, not only at the
                // street's ends: a ring it passes, or a road that ends beside it without meeting it.
                var near = Near(layout, boxes, ri);
                bool Off(Vec2 q)
                {
                    foreach (var (at, radius) in rings) if (Geo.Dist(q, at) < radius + RING_OUT + 0.15) return true;
                    foreach (var s in near) if (Geo.DistToPolyline(q, s.points) < s.width / 2 + CLEAR) return true;
                    return false;
                }
                if (rings.Count > 0 || near.Count > 0) KeepOff(marks, first, Off);
            }

            void Road(int ri, RoadStroke r)
            {
                double length = Geo.PolylineLength(r.points);
                // Where its lines stop at each end: a bulb, a ring, or clear of the roads it meets.
                double Stop(bool atEnd, double least, double pastBulb)
                {
                    if (Bulb(ri, atEnd)) return bulb + pastBulb;
                    var end = atEnd ? r.points[r.points.Count - 1] : r.points[0];
                    return JunctionTrim(layout, ri, atEnd, Math.Max(least, RingTrim(rings, end)), boxes);
                }
                var ed = design?.Edge(ri);

                // Edge lines, both sides.
                double e0 = Stop(false, EDGE_STOP, 0.3), e1 = length - Stop(true, EDGE_STOP, 0.3);
                if (length >= 3 && e1 - e0 >= 0.4)
                {
                    var middle = Geo.SubPolyline(r.points, e0, e1);
                    foreach (int side in new[] { 1, -1 })
                        foreach (var piece in OnEdge(Layout.OffsetPolyline(middle, side * r.width / 2), r))
                            marks.Add(new Mark { kind = MarkKind.EdgeLine, road = ri, points = piece, width = EDGE_WIDTH });
                }

                // The dashed centre line (not on a divided or one-way road).
                double d0 = Stop(false, DASH_STOP, 0.2), d1 = length - Stop(true, DASH_STOP, 0.2);
                if (!(ed != null && (ed.divided || ed.OneWay)))
                {
                    var samples = Geo.SamplePolyline(r.points, 0.1);
                    double travelled = 0;
                    var run = new List<Vec2>();
                    void Flush()
                    {
                        if (run.Count > 1 && Straight(run)) marks.Add(new Mark { kind = MarkKind.CentreDash, road = ri, points = run, width = CENTRE_WIDTH });
                        run = new List<Vec2>();
                    }
                    for (int k = 1; k < samples.Count; k++)
                    {
                        double seg = Geo.Dist(samples[k - 1], samples[k]);
                        double mid = travelled + seg / 2;
                        travelled += seg;
                        if (mid < d0 || mid > d1 || mid % 0.9 > 0.45)
                        {
                            Flush();
                            continue;
                        }
                        if (run.Count == 0) run.Add(samples[k - 1]);
                        run.Add(samples[k]);
                    }
                    Flush();
                }

                if (ed == null || length < 3) return;
                double c0 = Stop(false, EDGE_STOP, 0.3), c1 = Stop(true, EDGE_STOP, 0.3);
                if (length - c0 - c1 < 0.5) return;
                var lanes = Geo.SubPolyline(r.points, c0, length - c1);
                // Between two lanes each way: a dashed line.
                int most = Math.Max(ed.lanesFwd, ed.lanesBack);
                if (most >= 2)
                    foreach (int side in new[] { 1, -1 })
                        for (int k = 1; k < most; k++)
                        {
                            var line = Layout.OffsetPolyline(lanes, side * (r.width / 2 - k * LANE));
                            // Each dash a stretch of the line (its own bends kept), every other 0.45 tiles.
                            double len = Geo.PolylineLength(line);
                            for (double a = 0; a + 0.45 <= len; a += 0.9)
                            {
                                var dash = Geo.SubPolyline(line, a, a + 0.45);
                                if (dash.Count > 1 && Straight(dash)) marks.Add(new Mark { kind = MarkKind.LaneDash, road = ri, points = dash, width = LANE_WIDTH });
                            }
                        }
                foreach (var pk in ed.pockets)
                {
                    // Along its road from the stop line back: its side of the road (the way into its junction), its lane's edge.
                    double stop = pk.atTo ? c1 : c0, back = Math.Min(length - 1.5, stop + pk.length);
                    if (back - stop < 0.5) continue;
                    var span = pk.atTo ? Geo.SubPolyline(r.points, length - back, length - stop) : Geo.SubPolyline(r.points, stop, back);
                    // A lane +1 (from→to) runs on the side OffsetPolyline calls positive (RoadNet.LanePoint's frame).
                    int side = pk.atTo ? 1 : -1;
                    double edge = pk.left ? 0.12 : r.width / 2 - 0.05;
                    double inner = pk.left ? 0.12 + 0.55 : r.width / 2 - 0.6;
                    marks.Add(new Mark { kind = MarkKind.PocketLine, road = ri, points = Layout.OffsetPolyline(span, side * inner), width = POCKET_WIDTH });
                    // Its arrow, a little back from the stop line: a shaft, and its head bent the way it turns.
                    if (back - stop < 1.3) continue;
                    var along = pk.atTo ? Geo.SubPolyline(r.points, length - stop - 1.2, length - stop - 0.4) : Geo.SubPolyline(r.points, stop + 0.4, stop + 1.2);
                    double lateral = side * (edge + inner) / 2;
                    var shaft = Layout.OffsetPolyline(along, lateral);
                    if (!pk.atTo) shaft.Reverse();
                    if (shaft.Count < 2) continue;
                    marks.Add(new Mark { kind = MarkKind.Arrow, road = ri, points = shaft, width = ARROW_WIDTH });
                    var tip = shaft[shaft.Count - 1];
                    var pre = shaft[shaft.Count - 2];
                    double dx = tip.x - pre.x, dy = tip.y - pre.y, l = Math.Sqrt(dx * dx + dy * dy);
                    if (l < 1e-6) continue;
                    dx /= l;
                    dy /= l;
                    // The driver's right is the side +1 lanes are on (RoadNet's mirrored frame): a left bends the other way.
                    double nx = -dy * side, ny = dx * side;
                    int turn = pk.left ? -1 : 1;
                    var head = new Vec2(tip.x + nx * 0.3 * turn * side + dx * 0.05, tip.y + ny * 0.3 * turn * side + dy * 0.05);
                    marks.Add(new Mark { kind = MarkKind.Arrow, road = ri, points = new List<Vec2> { tip, head }, width = ARROW_WIDTH });
                }
            }
        }

        /// <summary>The other roads whose carriageways come near this one's lines (their boxes, grown, overlap).</summary>
        static List<RoadStroke> Near(IsoLayout layout, (double x0, double y0, double x1, double y1)?[] boxes, int road)
        {
            var near = new List<RoadStroke>();
            if (boxes[road] is not { } a) return near;
            double grow = layout.roads[road].width / 2 + 0.5;
            for (int i = 0; i < boxes.Length; i++)
            {
                if (i == road || boxes[i] is not { } b) continue;
                double reach = grow + layout.roads[i].width / 2 + CLEAR;
                if (a.x1 + reach < b.x0 || b.x1 + reach < a.x0 || a.y1 + reach < b.y0 || b.y1 + reach < a.y0) continue;
                near.Add(layout.roads[i]);
            }
            return near;
        }

        /// <summary>The marks from `first` on kept away from where `off` says: a line cut, a dash or an arrow left out.</summary>
        static void KeepOff(List<Mark> marks, int first, Func<Vec2, bool> off)
        {
            var kept = new List<Mark>();
            for (int i = first; i < marks.Count; i++)
            {
                var mk = marks[i];
                if (mk.kind == MarkKind.EdgeLine || mk.kind == MarkKind.PocketLine)
                    foreach (var piece in Pieces(mk.points, off)) kept.Add(new Mark { kind = mk.kind, road = mk.road, points = piece, width = mk.width });
                else if (!Geo.SamplePolyline(mk.points, STEP).Exists(q => off(q))) kept.Add(mk);
            }
            marks.RemoveRange(first, marks.Count - first);
            marks.AddRange(kept);
        }
    }
}

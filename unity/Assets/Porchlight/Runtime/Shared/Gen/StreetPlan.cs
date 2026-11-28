// The side streets, laid out the way a town is platted: by hierarchical domain
// splitting (Yang, Wang, Vouga & Wonka, "Urban Pattern: Layout Design by
// Hierarchical Domain Splitting", SIGGRAPH Asia 2013; and the OBB split of
// Vanegas et al., "Procedural Generation of Parcels in Urban Modeling", 2012).
//
// The main roads, water, steep ground and the town's edge cut the land into
// regions. Each region is split in two by a street laid right across it, from
// the road on one side to the road on the other, at a whole number of blocks
// from its edge; each half is split again, until every piece is about one block.
// Blocks are a module long one way (blockLength spacings) and one module deep
// the other (streetSpacing, centre to centre), bigger out of the centre and where
// streets are sparse.
//
// A street is only laid where it leaves at least `floor` tiles of land (the
// shortest block) between itself and any road alongside it, the test Jobard &
// Lefer use to keep streamlines evenly spaced ("Creating Evenly-Spaced
// Streamlines of Arbitrary Density", 1997) and Chen et al. use for street
// networks ("Interactive Procedural Street Modeling", SIGGRAPH 2008). It meets a
// road at a decent angle and either lines up with a junction across that road (a
// crossroads) or keeps a block's width from every other junction on it: no jogs.
// So two streets never run side by side with a sliver between them, and no
// street is a stub: each one a split, running from road to road.
//
// Some last splits in the outskirts are courts instead: a street from one side
// that stops short in a cul-de-sac, so the block wraps round it.
//
// Each piece is planned as its era built (TownPlan.EraAt its middle): the old
// core a regular grid of small blocks (Siksna's ~60 × 80 m) with no courts, the
// garden suburbs as above with a few, and the post-war edge with the most.
// The streets of the first few splits of each piece are its collectors.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed partial class StreetsStage
    {
        sealed class Region
        {
            public int id;
            public List<int> tiles;
            /// <summary>0: blocks run long along x (the streets crossing x are a long block apart); 1: along y.</summary>
            public int orient;
            /// <summary>How many splits made it: 0, a piece of land between the main roads.</summary>
            public int depth;
            /// <summary>The era it was planned in (TownPlan.EraAt its middle).</summary>
            public Era era;
            /// <summary>Its own random stream, named by its tiles (Rng.Local): the same land is split the same however it was reached.</summary>
            public Rng rng;
        }

        /// <summary>One end of a street across a region: where it meets a road, or a dead end.</summary>
        sealed class End
        {
            public bool join;
            /// <summary>The middle of the run's last tile, and the ray from it on to the road it meets.</summary>
            public Vec2 from, to;
        }

        /// <summary>A stretch of tiles a street could take, along a line across a region.</summary>
        sealed class Stretch
        {
            /// <summary>The tiles of the line the street takes; and the whole stretch of the region it divides (a dead end stops short, to turn in).</summary>
            public int u0, u1, whole0, whole1;
            public End a, b;
        }

        sealed class StreetPlan
        {
            /// <summary>How much bigger blocks get at the town's edge than in its centre.</summary>
            const double OUTSKIRTS = 0.4;
            /// <summary>How far (in blocks, times 1 - irregularity) a street will move off its place in the plan to line up with one across a road.</summary>
            const double ALIGN_BONUS = 0.5;
            /// <summary>Of the last splits in the outskirts, the share (times culDeSacs) that are courts.</summary>
            const double COURT_SHARE = 0.6;
            /// <summary>A street looks this far past the end of its run for the road it meets (tiles).</summary>
            const double JOIN_REACH = 4;
            /// <summary>Regions split between one share of the work and the next.</summary>
            const int REGIONS_A_STEP = 6;
            const int MAX_REGIONS = 20000;

            readonly int w, h;
            readonly GenParams p;
            /// <summary>The stream of the region being split (Region.rng).</summary>
            Rng rng;
            readonly GraphBuilder g;
            readonly byte[] roadOk;
            /// <summary>The region each tile of land is in; -1 off the land (road, water, rock, out of town), -3 land past a dead end on its line.</summary>
            readonly int[] owner;
            readonly bool[] road;
            readonly double hw;
            readonly int hwT, sw;
            /// <summary>The fewest tiles of land a street leaves between itself and a road alongside it: the shortest block.</summary>
            public readonly int floor;
            readonly Func<Vec2, double> falloff;
            readonly Func<Vec2, int, bool> bulbFits;
            readonly Func<Vec2, Vec2, int[], int, Hit?> firstHit;
            readonly List<Region> queue = new List<Region>();
            readonly TownModel model;
            int nextId;
            /// <summary>The name of the regions' random streams: "streets", or an editor's regenerated area's (Regions.Stream).</summary>
            public string stream = "streets";
            /// <summary>An editor's regenerated area: the blocks turned either way as often as not, and the streets off their places more.</summary>
            public bool variety;

            /// <summary>Streets laid by splits this deep or shallower (0: the first across a piece of land between the main roads) are collectors.</summary>
            public const int COLLECTOR_DEPTH = 2;
            /// <summary>The streets laid by the first splits (to COLLECTOR_DEPTH) of each piece of land between the main roads: the collectors (end to end).</summary>
            public readonly List<(Vec2 a, Vec2 b)> collectors = new List<(Vec2 a, Vec2 b)>();

            public StreetPlan(TownModel model, GraphBuilder g, Rng rng, byte[] land, byte[] roadOk, Func<Vec2, double> falloff, Func<Vec2, int, bool> bulbFits, Func<Vec2, Vec2, int[], int, Hit?> firstHit)
            {
                this.model = model;
                w = model.width;
                h = model.height;
                p = model.@params;
                this.rng = rng;
                this.g = g;
                this.roadOk = roadOk;
                this.falloff = falloff;
                this.bulbFits = bulbFits;
                this.firstHit = firstHit;
                hw = Raster.RoadHalfWidth(RoadKind.Street, p);
                hwT = (int)Math.Ceiling(hw - 1e-9) - 1;
                sw = 2 * hwT + 1;
                floor = Floor(p);
                var raster = Raster.RoadRaster(model, RoadGraphOps.LiveEdges(g));
                road = new bool[w * h];
                owner = new int[w * h];
                for (int i = 0; i < owner.Length; i++)
                {
                    road[i] = raster.kind[i] != Raster.ROAD_NONE;
                    owner[i] = land[i] != 0 && !road[i] ? -2 : -1;
                }
            }

            /// <summary>The shortest block (tiles of land between a street and a road alongside it): the setting, but never more than a block's own depth allows.</summary>
            public static int Floor(GenParams p)
            {
                int sw = 2 * ((int)Math.Ceiling(Raster.RoadHalfWidth(RoadKind.Street, p) - 1e-9) - 1) + 1;
                int depth = (int)Math.Floor(p.streetSpacing) - sw;
                return Math.Max(2, Math.Min((int)Numbers.Round(p.streetSegment), depth - 1));
            }

            public IEnumerable<string> Run()
            {
                // The land between the main roads: each piece a region, its blocks long along its longer side.
                for (int i = 0; i < owner.Length; i++)
                {
                    if (owner[i] != -2) continue;
                    var tiles = Flood(i, -2);
                    var (x0, y0, x1, y1) = Box(tiles);
                    int orient = x1 - x0 >= y1 - y0 ? 0 : 1;
                    if (queue[queue.Count - 1].rng.Chance(variety ? 0.5 : 0.2 * p.irregularity)) orient = 1 - orient;
                    queue[queue.Count - 1].orient = orient;
                }
                yield return "/regions";
                for (int q = 0; q < queue.Count && q < MAX_REGIONS; q++)
                {
                    if (q % REGIONS_A_STEP == REGIONS_A_STEP - 1) yield return "/blocks";
                    Split(queue[q]);
                }
            }

            /// <summary>The tiles 4-connected to `start` whose owner is `was`, made a new region (queued).</summary>
            List<int> Flood(int start, int was)
            {
                int id = nextId++;
                var tiles = new List<int> { start };
                owner[start] = id;
                for (int head = 0; head < tiles.Count; head++)
                {
                    int c = tiles[head];
                    int cx = c % w, cy = c / w;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = Grid.Index(nx, ny, w);
                        if (owner[j] != was) continue;
                        owner[j] = id;
                        tiles.Add(j);
                    }
                }
                queue.Add(new Region { id = id, tiles = tiles, rng = Rng.Local(model.seed, stream, Signature.OfTiles(tiles)) });
                return tiles;
            }

            (int x0, int y0, int x1, int y1) Box(List<int> tiles)
            {
                int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
                foreach (int t in tiles)
                {
                    int x = t % w, y = t / w;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
                return (x0, y0, x1, y1);
            }

            void Split(Region r)
            {
                rng = r.rng;
                if (r.tiles.Count < 2 * floor * floor) return;
                var (x0, y0, x1, y1) = Box(r.tiles);
                double sx = 0, sy = 0;
                foreach (int t in r.tiles)
                {
                    sx += t % w;
                    sy += t / w;
                }
                var mid = new Vec2(sx / r.tiles.Count + 0.5, sy / r.tiles.Count + 0.5);
                double f = falloff(mid);
                var era = TownPlan.EraAt(model, mid);
                // A block's depth, centre to centre, and its length: bigger where streets are sparse and out of the centre.
                // The old core is a tight, regular grid of Siksna's small blocks (scaled with the street spacing) instead.
                double depth = era == Era.Core
                    ? Math.Max(p.streetSpacing * TownPlan.CORE_DEPTH / 9, floor + sw + 1)
                    : Math.Max(p.streetSpacing * (1 + (1 - p.roadDensity) * 0.5) * (1 + OUTSKIRTS * (1 - f)), floor + sw + 1);
                double length = era == Era.Core ? depth * TownPlan.CORE_LENGTH / TownPlan.CORE_DEPTH * p.blockLength / 2 : depth * p.blockLength;
                double mx = r.orient == 0 ? length : depth;
                double my = r.orient == 0 ? depth : length;
                int nx = Blocks(x1 - x0 + 1, mx), ny = Blocks(y1 - y0 + 1, my);
                if (nx <= 1 && ny <= 1) return;
                bool last = nx * ny == 2;
                // Where streets are sparse, some pieces stay two blocks big (never in the gridded core).
                if (last && era != Era.Core && rng.Chance((1 - p.roadDensity) * 0.5)) return;
                // Courts: none in the core, some in the garden suburbs, and most in the post-war loops and lollipops.
                double courts = era == Era.Core ? 0 : era == Era.GardenSuburb ? COURT_SHARE * 0.5 : Math.Min(1, COURT_SHARE * 1.5);
                bool court = last && p.culDeSacs > 0 && courts > 0 && rng.Chance(p.culDeSacs * courts * (1 - f));
                r.era = era;
                // The way with the most blocks to come is split first.
                bool xFirst = nx > ny || (nx == ny && (x1 - x0 + 1) / mx >= (y1 - y0 + 1) / my);
                foreach (int axis in xFirst ? new[] { 0, 1 } : new[] { 1, 0 })
                {
                    int n = axis == 0 ? nx : ny;
                    if (n <= 1) continue;
                    if (axis == 0 ? TrySplit(r, 0, n, x0, x1, y0, y1, court) : TrySplit(r, 1, n, y0, y1, x0, x1, court)) return;
                }
            }

            int Blocks(int extent, double module) => (int)Math.Floor((extent + sw) / module + 0.5);

            /// <summary>
            /// Split the region with a street across `axis` (0: a street running along y, at an x), `k` blocks of
            /// `n` from its low side, near the plan's place: lined up with a junction across the road if one is near.
            /// </summary>
            bool TrySplit(Region r, int axis, int n, int lo, int hi, int lo2, int hi2, bool court)
            {
                double module = (hi - lo + 1 + sw) / (double)n;
                int k = n / 2;
                if (n % 2 == 1 && rng.Chance(0.5)) k++;
                double ideal = lo - 1 - hwT + k * module;
                // The core's grid is regular: its streets keep to their places.
                double target = ideal + (r.era == Era.Core ? 0 : variety ? Math.Max(0.6, p.irregularity) : p.irregularity) * module * 0.3 * rng.Range(-1, 1);
                int window = Math.Max(1, (int)Math.Floor(module * 0.35 + 0.5));
                int cMin = Math.Max((int)Math.Floor(ideal) - window, lo + hwT + floor);
                int cMax = Math.Min((int)Math.Ceiling(ideal) + window, hi - hwT - floor);
                // And any line within half a block that a junction across the road already sits on: lined up, a crossroads.
                int aMin = Math.Max((int)Math.Floor(ideal - module / 2), lo + hwT + floor);
                int aMax = Math.Min((int)Math.Ceiling(ideal + module / 2), hi - hwT - floor);
                var aligned = AlignedLines(axis, aMin, aMax, lo2 - 8, hi2 + 8);
                var order = new List<(int c, double score)>();
                for (int c = Math.Min(cMin, aMin); c <= Math.Max(cMax, aMax); c++)
                {
                    bool lined = aligned.Contains(c);
                    if ((c < cMin || c > cMax) && !lined) continue;
                    order.Add((c, Math.Abs(c - target) - (lined ? ALIGN_BONUS * module * (1 - p.irregularity) : 0)));
                }
                Numbers.StableSort(order, (a, b) => a.score - b.score);
                // Failing those (a region of an odd shape: round a lake, say), anywhere across it there's a block's room, nearest the plan first.
                var tried = new HashSet<int>();
                foreach (var (c, _) in order) tried.Add(c);
                var rest = new List<(int c, double score)>();
                for (int c = lo + hwT + floor; c <= hi - hwT - floor; c++)
                    if (!tried.Contains(c)) rest.Add((c, Math.Abs(c - target)));
                Numbers.StableSort(rest, (a, b) => a.score - b.score);
                order.AddRange(rest);
                foreach (var (c, _) in order)
                {
                    var runs = Runs(r, axis, c, lo2, hi2);
                    if (runs.Count == 0) continue;
                    if (court && Court(axis, c, runs)) return true;
                    foreach (var run in runs)
                    {
                        Carve(axis, c, run);
                        if (r.depth <= COLLECTOR_DEPTH) collectors.Add((Centre(axis, c, run.u0), Centre(axis, c, run.u1)));
                    }
                    Regroup(r, axis);
                    return true;
                }
                return false;
            }

            /// <summary>Lines across `axis` between cMin and cMax that a junction already sits on, near this stretch.</summary>
            HashSet<int> AlignedLines(int axis, int cMin, int cMax, int lo2, int hi2)
            {
                var o = new HashSet<int>();
                foreach (var n in g.nodes)
                {
                    double along = axis == 0 ? n.at.x : n.at.y, across = axis == 0 ? n.at.y : n.at.x;
                    int c = (int)Math.Floor(along);
                    if (c < cMin || c > cMax || Math.Abs(along - (c + 0.5)) > 0.01 || across < lo2 || across > hi2) continue;
                    o.Add(c);
                }
                return o;
            }

            int Tile(int axis, int c, int u) => axis == 0 ? Grid.Index(c, u, w) : Grid.Index(u, c, w);

            bool InMap(int axis, int c, int u) => axis == 0 ? c >= 0 && c < w && u >= 0 && u < h : c >= 0 && c < h && u >= 0 && u < w;

            Vec2 Centre(int axis, int c, int u) => axis == 0 ? new Vec2(c + 0.5, u + 0.5) : new Vec2(u + 0.5, c + 0.5);

            /// <summary>A street's whole width fits at this step of its line: the region's land, fit for a road.</summary>
            bool Fits(Region r, int axis, int c, int u)
            {
                for (int k = -hwT; k <= hwT; k++)
                {
                    if (!InMap(axis, c + k, u)) return false;
                    int t = Tile(axis, c + k, u);
                    if (owner[t] != r.id || roadOk[t] == 0) return false;
                }
                return true;
            }

            /// <summary>The stretches of line c across the region a street can take: long enough, roomy, meeting a road at one end at least.</summary>
            List<Stretch> Runs(Region r, int axis, int c, int lo2, int hi2)
            {
                var o = new List<Stretch>();
                int u = lo2;
                while (u <= hi2)
                {
                    if (!Fits(r, axis, c, u))
                    {
                        u++;
                        continue;
                    }
                    int s = u;
                    while (u + 1 <= hi2 && Fits(r, axis, c, u + 1)) u++;
                    int e = u;
                    u++;
                    if (e - s + 1 < floor) continue;
                    var a = EndOf(axis, c, s, -1);
                    var b = EndOf(axis, c, e, 1);
                    if (a == null || b == null) continue;
                    if (!a.join && !b.join) continue;
                    // Near a road it meets, that road crosses it: there the land beside it may narrow.
                    int ends = Math.Min(floor, Math.Max(1, (e - s + 1) / 4));
                    bool roomy = true;
                    for (int v = a.join ? s + ends : s; v <= (b.join ? e - ends : e) && roomy; v++) roomy = Roomy(axis, c, v);
                    if (!roomy) continue;
                    // A dead end stops where its bulb fits, short of the water or the edge.
                    int s0 = s, e0 = e;
                    if (!a.join) while (e - s + 1 >= floor && !bulbFits(Centre(axis, c, s), -1)) s++;
                    if (!b.join) while (e - s + 1 >= floor && !bulbFits(Centre(axis, c, e), -1)) e--;
                    if (e - s + 1 < floor) continue;
                    if (!a.join) a = new End { join = false, from = Centre(axis, c, s), to = Centre(axis, c, s) };
                    if (!b.join) b = new End { join = false, from = Centre(axis, c, e), to = Centre(axis, c, e) };
                    o.Add(new Stretch { u0 = s, u1 = e, whole0 = s0, whole1 = e0, a = a, b = b });
                }
                return o;
            }

            /// <summary>
            /// No road within `floor` tiles on either side of the street at step u of its line (water, steep ground or the
            /// town's edge may be nearer: a street along a shore is fine): nothing runs alongside it close enough to leave a sliver.
            /// </summary>
            bool Roomy(int axis, int c, int u)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    for (int d = 1; d <= floor; d++)
                    {
                        int cc = c + side * (hwT + d);
                        if (!InMap(axis, cc, u)) break;
                        int t = Tile(axis, cc, u);
                        if (road[t]) return false;
                        if (owner[t] < 0) break;
                    }
                }
                return true;
            }

            /// <summary>
            /// How the street ends past tile u, going `dir` along its line: meeting the road there (at a decent angle, and
            /// at a junction already there or a block from any), or a dead end where there's no road. Null if it can't end well.
            /// </summary>
            End EndOf(int axis, int c, int u, int dir)
            {
                var from = Centre(axis, c, u);
                int next = u + dir;
                if (!InMap(axis, c, next) || !road[Tile(axis, c, next)]) return new End { join = false, from = from, to = from };
                var step = axis == 0 ? new Vec2(0, dir) : new Vec2(dir, 0);
                var to = Geo.Add(from, Geo.Scale(step, JOIN_REACH));
                var hit = firstHit(from, to, new int[0], -1);
                if (hit == null) return null;
                var x = hit.Value;
                // Everything between here and the road is the road's own surface: never across water to one beyond.
                foreach (var (cx, cy) in Grid.CellsOnSegment(from.x, from.y, x.point.x, x.point.y))
                {
                    if (cx == (int)Math.Floor(from.x) && cy == (int)Math.Floor(from.y)) continue;
                    if (cx < 0 || cy < 0 || cx >= w || cy >= h || !road[Grid.Index(cx, cy, w)]) return null;
                }
                var e = g.edges[x.edge];
                var tangent = Unit(Geo.Sub(e.points[Seg(e.points, x.point) + 1], e.points[Seg(e.points, x.point)]));
                if (Math.Abs(Geo.Dot(tangent, step)) > COS_MIN_ANGLE) return null;
                // The junctions either side of here on the road it meets: a crossroads at one, or a block from both.
                double near = Math.Min(Geo.Dist(g.nodes[e.from].at, x.point), Geo.Dist(g.nodes[e.to].at, x.point));
                if (near > SNAP && near < floor) return null;
                return new End { join = true, from = from, to = Geo.Add(x.point, Geo.Scale(step, 0.01)) };
            }

            /// <summary>The segment of `pts` nearest `at`.</summary>
            static int Seg(List<Vec2> pts, Vec2 at)
            {
                int best = 0;
                double bestD = double.PositiveInfinity;
                for (int s = 0; s + 1 < pts.Count; s++)
                {
                    double d = Geo.DistToSegment(at, pts[s], pts[s + 1]);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = s;
                    }
                }
                return best;
            }

            /// <summary>The node a street's end is at: a junction on the road it meets, or a new dead end.</summary>
            int NodeAt(End end)
            {
                if (end.join)
                {
                    var hit = firstHit(end.from, end.to, new int[0], -1);
                    if (hit != null) return RoadGraphOps.SplitEdgeAt(g, hit.Value.edge, hit.Value.point, SNAP);
                }
                return RoadGraphOps.AddNode(g, end.from);
            }

            void Carve(int axis, int c, Stretch run)
            {
                int a = NodeAt(run.a);
                int b = NodeAt(run.b);
                if (a == b) return;
                int id = RoadGraphOps.AddEdge(g, a, b, RoadKind.Street);
                Paint(g.edges[id].points);
                // Past a dead end the line still divides the region: its land there is left out of both halves' planning.
                for (int u = run.whole0; u <= run.whole1; u++)
                {
                    if (u >= run.u0 && u <= run.u1) continue;
                    for (int k = -hwT; k <= hwT; k++)
                        if (InMap(axis, c + k, u) && owner[Tile(axis, c + k, u)] >= 0) owner[Tile(axis, c + k, u)] = -3;
                }
            }

            /// <summary>Take the street's tiles out of the land.</summary>
            void Paint(List<Vec2> pts)
            {
                var box = Geo.PolygonBounds(pts);
                for (int y = Math.Max(0, (int)Math.Floor(box.y0 - hw)); y <= Math.Min(h - 1, (int)Math.Ceiling(box.y1 + hw)); y++)
                    for (int x = Math.Max(0, (int)Math.Floor(box.x0 - hw)); x <= Math.Min(w - 1, (int)Math.Ceiling(box.x1 + hw)); x++)
                    {
                        if (!(Geo.DistToPolyline(new Vec2(x + 0.5, y + 0.5), pts) < hw)) continue;
                        int i = Grid.Index(x, y, w);
                        road[i] = true;
                        owner[i] = -1;
                    }
            }

            /// <summary>The pieces the region's land is in now, each a region of its own (queued to split again).</summary>
            void Regroup(Region r, int axis)
            {
                foreach (int t in r.tiles)
                {
                    if (owner[t] != r.id) continue;
                    Flood(t, r.id);
                    var child = queue[queue.Count - 1];
                    child.orient = r.orient;
                    child.depth = r.depth + 1;
                    if (r.era != Era.Core && rng.Chance(0.15 * p.irregularity)) child.orient = 1 - child.orient;
                }
            }

            /// <summary>
            /// A court: a street from the road at one end of a run that stops a block short of the other side, in a
            /// cul-de-sac. The region stays one block, wrapped round it.
            /// </summary>
            bool Court(int axis, int c, List<Stretch> runs)
            {
                int bulb = (int)Math.Ceiling(Raster.BulbHalfWidth(p));
                foreach (var run in runs)
                {
                    int len = run.u1 - run.u0 + 1 - floor - bulb;
                    if (len < Math.Max(CUL_DE_SAC_LENGTH, floor)) continue;
                    var ends = new List<bool>();
                    if (run.a.join) ends.Add(true);
                    if (run.b.join) ends.Add(false);
                    bool fromA = ends.Count == 1 ? ends[0] : rng.Chance(0.5);
                    int u = fromA ? run.u0 + len - 1 : run.u1 - len + 1;
                    var stop = Centre(axis, c, u);
                    // A court of the garden suburbs stops short of the gridded core.
                    if (!bulbFits(stop, -1) || TownPlan.EraAt(model, stop) == Era.Core) continue;
                    int a = NodeAt(fromA ? run.a : run.b);
                    int b = RoadGraphOps.AddNode(g, stop);
                    int id = RoadGraphOps.AddEdge(g, a, b, RoadKind.Street);
                    Paint(g.edges[id].points);
                    return true;
                }
                return false;
            }
        }
    }
}

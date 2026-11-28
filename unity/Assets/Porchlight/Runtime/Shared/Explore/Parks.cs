// The parks and what's in them. A park is a block the generator kept green
// (TownModel.ParkBlockSet): here are its grass, its outline for the maps, and
// its recreation kit. Benches stand along its edges facing in, picnic tables
// out on the grass, bins by the benches, and the bigger parks have a
// playground (climbing frame, swings, slide, spinner, seesaw) grouped
// together away from the road.
//
// Worked out from the town as it stands (its model and layout), never by the
// generator, so the saved towns don't change when the kit does. Pure: the same
// town gives the same parks in the game, the simulation and the checks.
//
// Every piece stands wholly on the park's own grass, clear of the roads and
// sidewalks round it and of every other piece. Trees are the game's to plant,
// a tile at a time from the town's own random stream, after this; so instead
// of the kit dodging trees, the game plants none on the tiles the kit needs
// kept clear (KeptClear), and the rest of the town's trees stay where they were.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.Explore
{
    /// <summary>A piece of a park's kit. (Catalog.PARK_KIT names each one's model, in this order.)</summary>
    public enum ParkKit { Bench, PicnicTable, Slide, Swings, ClimbingFrame, Seesaw, Spinner, Bin }

    public struct ParkPiece
    {
        public ParkKit kind;
        /// <summary>The park (its block) it stands in.</summary>
        public int blockId;
        /// <summary>The middle of its footprint (map tiles).</summary>
        public Vec2 at;
        /// <summary>The way it faces, a unit vector in map space: where a bench's sitters look, where a slide runs down to.</summary>
        public Vec2 front;
        /// <summary>Its footprint (tiles): side to side, and front to back.</summary>
        public double width, depth;

        /// <summary>Its heading as the game turns a model (Coords.Yaw): facing map direction (sin r, cos r).</summary>
        public double Heading => DetMath.Atan2(front.x, front.y);

        /// <summary>A unit vector across it, square to its front.</summary>
        public Vec2 Side => new Vec2(-front.y, front.x);

        /// <summary>The corners of its footprint, round in order.</summary>
        public Vec2[] Corners()
        {
            var s = Side;
            double hw = width / 2, hd = depth / 2;
            return new[]
            {
                new Vec2(at.x - s.x * hw - front.x * hd, at.y - s.y * hw - front.y * hd),
                new Vec2(at.x + s.x * hw - front.x * hd, at.y + s.y * hw - front.y * hd),
                new Vec2(at.x + s.x * hw + front.x * hd, at.y + s.y * hw + front.y * hd),
                new Vec2(at.x - s.x * hw + front.x * hd, at.y - s.y * hw + front.y * hd),
            };
        }
    }

    /// <summary>A park: its block, its grass (tiles), and where its name or mark goes on a map.</summary>
    public sealed class Park
    {
        public int blockId;
        public List<int> tiles;
        /// <summary>The middle of its tile furthest from its edge: inside it, whatever its shape.</summary>
        public Vec2 middle;
    }

    public static class Parks
    {
        // Footprints, in metres (a tile is Directions.METRES_PER_TILE): side to side, then front to back. Guesses until
        // the models are measured; change them here and the placement and the check follow.
        public const double BENCH_WIDTH_M = 1.8, BENCH_DEPTH_M = 0.6;
        public const double PICNIC_WIDTH_M = 1.8, PICNIC_DEPTH_M = 1.5;
        /// <summary>A slide is narrow and runs front to back, down to its front.</summary>
        public const double SLIDE_WIDTH_M = 1, SLIDE_DEPTH_M = 3.5;
        /// <summary>A swing frame is wide; the swings go front to back.</summary>
        public const double SWINGS_WIDTH_M = 3.5, SWINGS_DEPTH_M = 2;
        public const double CLIMBING_FRAME_M = 3;
        /// <summary>A seesaw's riders face each other along it: front to back.</summary>
        public const double SEESAW_WIDTH_M = 0.4, SEESAW_DEPTH_M = 3.5;
        /// <summary>A spinner is round: this across (its footprint the square round it).</summary>
        public const double SPINNER_M = 2;
        public const double BIN_M = 0.5;

        // Room kept round the pieces, in metres.
        /// <summary>Between a piece and the outer edge of a sidewalk (or a path's edge, or a cul-de-sac's ring).</summary>
        public const double ROAD_GAP_M = 0.5;
        /// <summary>A bench by a road sits this much further in again, its back to the road.</summary>
        public const double BENCH_SETBACK_M = 0.6;
        /// <summary>Between a piece and the park's edge where it meets other land (a yard, the water).</summary>
        public const double EDGE_GAP_M = 0.3;
        /// <summary>Between any two pieces.</summary>
        public const double PIECE_GAP_M = 0.5;
        /// <summary>Between two pieces of a playground: room to run round each.</summary>
        public const double PLAY_GAP_M = 1.5;
        /// <summary>Between a piece and the trunk of a tree.</summary>
        public const double TREE_GAP_M = 1.5;
        /// <summary>Benches along the edge stand at least this far apart; picnic tables too.</summary>
        public const double BENCH_SPACING_M = 10, PICNIC_SPACING_M = 8;
        /// <summary>A bench has its own park this far in front of its middle, at least: it looks into the park.</summary>
        public const double BENCH_OUTLOOK_M = 2.4;
        /// <summary>A playground's pieces stand within this of its middle.</summary>
        public const double PLAYGROUND_REACH_M = 8;

        /// <summary>A park this big or bigger (tiles: about 1,600 m²) has a playground.</summary>
        public const int PLAYGROUND_TILES = 100;
        /// <summary>A bench for every so many tiles of park (at least one, at most MAX_BENCHES), a picnic table for every so many (at most MAX_PICNIC).</summary>
        public const int BENCH_TILES = 30, PICNIC_TILES = 60, MAX_BENCHES = 10, MAX_PICNIC = 4;
        /// <summary>A playground with fewer of its pieces than this, where they fit, isn't one: the park has none.</summary>
        const int PLAYGROUND_FEWEST = 3;
        /// <summary>Where a tree stands in its tile: its middle, give or take this much each way (WorldParts.AddTrees).</summary>
        public const double TREE_JITTER = 0.25;

        public static double Tiles(double metres) => metres / Directions.METRES_PER_TILE;

        /// <summary>A piece's footprint (tiles): side to side, front to back.</summary>
        public static (double width, double depth) Footprint(ParkKit k) => k switch
        {
            ParkKit.Bench => (Tiles(BENCH_WIDTH_M), Tiles(BENCH_DEPTH_M)),
            ParkKit.PicnicTable => (Tiles(PICNIC_WIDTH_M), Tiles(PICNIC_DEPTH_M)),
            ParkKit.Slide => (Tiles(SLIDE_WIDTH_M), Tiles(SLIDE_DEPTH_M)),
            ParkKit.Swings => (Tiles(SWINGS_WIDTH_M), Tiles(SWINGS_DEPTH_M)),
            ParkKit.ClimbingFrame => (Tiles(CLIMBING_FRAME_M), Tiles(CLIMBING_FRAME_M)),
            ParkKit.Seesaw => (Tiles(SEESAW_WIDTH_M), Tiles(SEESAW_DEPTH_M)),
            ParkKit.Spinner => (Tiles(SPINNER_M), Tiles(SPINNER_M)),
            _ => (Tiles(BIN_M), Tiles(BIN_M)),
        };

        /// <summary>Whether a piece is part of a playground.</summary>
        public static bool IsPlay(ParkKit k) => k == ParkKit.Slide || k == ParkKit.Swings || k == ParkKit.ClimbingFrame || k == ParkKit.Seesaw || k == ParkKit.Spinner;

        /// <summary>How far from a road's centreline its sidewalk's outer edge is (a path has none: its own edge).</summary>
        public static double Reach(RoadStroke r) => r.kind == RoadKind.Path ? r.width / 2 : r.width / 2 + Layout.PARKING + Layout.SIDEWALK;

        /// <summary>
        /// The town's parks, by block: each one's grass (the block's tiles that aren't road, water or built on), and
        /// its middle. Needs only the model, so the maps can draw them before the town's laid out.
        /// </summary>
        public static List<Park> All(TownModel model)
        {
            int w = model.width, h = model.height;
            var ids = new List<int>(model.ParkBlockSet());
            ids.Sort();
            var o = new List<Park>();
            foreach (int b in ids)
            {
                if (b < 0 || b >= model.blocks.Count) continue;
                var block = model.blocks[b];
                var tiles = Parcels.TilesIn(block.polygon, block.holes, w, h).FindAll(t =>
                    model.tiles.overlay[t] == Overlay.NONE && !Terrain.IsWater(model.tiles.terrain[t]) && model.tiles.buildingId[t] < 0);
                if (tiles.Count == 0) continue;
                var depth = Depths(tiles, w, h);
                int deepest = tiles[0];
                foreach (int t in tiles) if (depth[t] > depth[deepest]) deepest = t;
                o.Add(new Park { blockId = b, tiles = tiles, middle = new Vec2(deepest % w + 0.5, deepest / w + 0.5) });
            }
            return o;
        }

        /// <summary>A park's outline (Parcels.Outline: tile edges, outer rings and holes), for the maps.</summary>
        public static List<List<Vec2>> Outline(Park p, int w)
        {
            var o = new List<List<Vec2>>();
            foreach (var ring in Parcels.Outline(p.tiles, w)) o.Add(Geo.RemoveCollinear(ring));
            return o;
        }

        /// <summary>
        /// The parks as Manage's map draws them (the roadmap file, and the simulation's word on a town): each one's
        /// middle (x, y), and its outline's rings, each flat (x0, y0, x1, y1, ...), in tiles.
        /// </summary>
        public static List<(double[] at, List<double[]> rings)> Encode(TownModel model)
        {
            var o = new List<(double[] at, List<double[]> rings)>();
            foreach (var p in All(model))
            {
                var rings = new List<double[]>();
                foreach (var ring in Outline(p, model.width))
                {
                    var flat = new double[ring.Count * 2];
                    for (int i = 0; i < ring.Count; i++)
                    {
                        flat[i * 2] = ring[i].x;
                        flat[i * 2 + 1] = ring[i].y;
                    }
                    rings.Add(flat);
                }
                o.Add((new[] { p.middle.x, p.middle.y }, rings));
            }
            return o;
        }

        /// <summary>
        /// How far in from its edge each of these tiles is (1: on the edge, a side open to something else), by
        /// tile. Steps across the tiles' sides, so a tile two in from a road is 2.
        /// </summary>
        static Dictionary<int, int> Depths(List<int> tiles, int w, int h)
        {
            var set = new HashSet<int>(tiles);
            var depth = new Dictionary<int, int>();
            var queue = new List<int>();
            foreach (int t in tiles)
            {
                int x = t % w, y = t / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = x + dx, ny = y + dy;
                    if (Grid.InBounds(nx, ny, w, h) && set.Contains(Grid.Index(nx, ny, w))) continue;
                    depth[t] = 1;
                    queue.Add(t);
                    break;
                }
            }
            for (int head = 0; head < queue.Count; head++)
            {
                int t = queue[head], x = t % w, y = t / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!Grid.InBounds(nx, ny, w, h)) continue;
                    int n = Grid.Index(nx, ny, w);
                    if (!set.Contains(n) || depth.ContainsKey(n)) continue;
                    depth[n] = depth[t] + 1;
                    queue.Add(n);
                }
            }
            return depth;
        }

        /// <summary>Every park's kit, park by park.</summary>
        public static List<ParkPiece> Plan(TownModel model, IsoLayout layout) => Stepwise.Drain<List<ParkPiece>>(done => PlanSteps(model, layout, done));

        /// <summary>The same, a park at a time (for the web, which has no threads), then `done` with it.</summary>
        public static IEnumerable<string> PlanSteps(TownModel model, IsoLayout layout, Action<List<ParkPiece>> done)
        {
            var o = new List<ParkPiece>();
            foreach (var p in All(model))
            {
                o.AddRange(Furnish(model, layout, p));
                yield return "/park";
            }
            done(o);
        }

        /// <summary>
        /// The tiles in which no tree, bush or flower may be planted, so none stands within TREE_GAP_M of a piece:
        /// those whose spot for one (its middle, give or take TREE_JITTER) comes that near.
        /// </summary>
        public static HashSet<int> KeptClear(TownModel model, List<ParkPiece> pieces)
        {
            int w = model.width, h = model.height;
            double gap = Tiles(TREE_GAP_M);
            var o = new HashSet<int>();
            foreach (var piece in pieces)
            {
                var corners = piece.Corners();
                double reach = Math.Max(piece.width, piece.depth) / 2 + gap + 1;
                for (int y = (int)Math.Floor(piece.at.y - reach); y <= (int)Math.Floor(piece.at.y + reach); y++)
                    for (int x = (int)Math.Floor(piece.at.x - reach); x <= (int)Math.Floor(piece.at.x + reach); x++)
                    {
                        if (!Grid.InBounds(x, y, w, h)) continue;
                        double lo = 0.5 - TREE_JITTER, hi = 0.5 + TREE_JITTER;
                        var spot = new[] { new Vec2(x + lo, y + lo), new Vec2(x + hi, y + lo), new Vec2(x + hi, y + hi), new Vec2(x + lo, y + hi) };
                        if (Apart(corners, spot) < gap) o.Add(Grid.Index(x, y, w));
                    }
            }
            return o;
        }

        /// <summary>One park's kit: its playground first (it wants the middle), then the benches, picnic tables and bins.</summary>
        static List<ParkPiece> Furnish(TownModel model, IsoLayout layout, Park park)
        {
            int w = model.width, h = model.height;
            var rng = new Rng($"{model.seed}::park:{park.blockId}");
            // Its grass, as the layout has it (green, nothing on it).
            var grass = new HashSet<int>();
            foreach (int t in park.tiles) if (layout.use[t] == Use.GREEN) grass.Add(t);
            var placed = new List<ParkPiece>();
            if (grass.Count == 0) return placed;
            var tiles = new List<int>(grass);
            tiles.Sort();
            var depth = Depths(tiles, w, h);
            // The roads and cul-de-sacs near enough to matter.
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (int t in tiles)
            {
                x0 = Math.Min(x0, t % w);
                y0 = Math.Min(y0, t / w);
                x1 = Math.Max(x1, t % w + 1);
                y1 = Math.Max(y1, t / w + 1);
            }
            var box = new Vec2((x0 + x1) / 2.0, (y0 + y1) / 2.0);
            double boxReach = Numbers.Hypot(x1 - x0, y1 - y0) / 2 + 4;
            var roads = layout.roads.FindAll(r => r.points != null && r.points.Count >= 2 && Geo.DistToPolyline(box, r.points) < boxReach);
            double bulbReach = Raster.BulbReach(model.@params);
            var bulbs = layout.bulbs.FindAll(b => Geo.Dist(b.at, box) < boxReach + bulbReach);

            double edgeGap = Tiles(EDGE_GAP_M), roadGap = Tiles(ROAD_GAP_M);
            // Whether a piece could stand here: on the grass (EDGE_GAP_M in from any edge that isn't a road's), clear of
            // every road's sidewalk and cul-de-sac's ring, and `gap` from every piece already placed (PIECE_GAP_M at least).
            bool Fits(ParkPiece c, double gap)
            {
                if (c.kind == ParkKit.Bench)
                {
                    double look = Tiles(BENCH_OUTLOOK_M);
                    if (!Grass((int)Math.Floor(c.at.x + c.front.x * look), (int)Math.Floor(c.at.y + c.front.y * look))) return false;
                }
                var corners = c.Corners();
                foreach (var k in corners) if (!Grass((int)Math.Floor(k.x), (int)Math.Floor(k.y))) return false;
                // (Each test first by the circle round the piece: only what comes near it is looked at closer.)
                double round = Numbers.Hypot(c.width, c.depth) / 2;
                foreach (var q in placed)
                {
                    double need = IsPlay(q.kind) && IsPlay(c.kind) ? Math.Max(gap, Tiles(PIECE_GAP_M)) : Tiles(PIECE_GAP_M);
                    if (Geo.Dist(c.at, q.at) - round - Numbers.Hypot(q.width, q.depth) / 2 >= need) continue;
                    if (Apart(corners, q.Corners()) < need) return false;
                }
                var nearRoads = roads.FindAll(r => Geo.DistToPolyline(c.at, r.points) - round < Reach(r) + roadGap);
                var nearBulbs = bulbs.FindAll(b => Geo.Dist(c.at, b.at) - round < bulbReach + roadGap);
                foreach (var p in Samples(c))
                {
                    int tx = (int)Math.Floor(p.x), ty = (int)Math.Floor(p.y);
                    if (!Grass(tx, ty)) return false;
                    // Off the edge's own gap: where a side of its tile isn't grass, not too near that side.
                    double fx = p.x - tx, fy = p.y - ty;
                    if (fx < edgeGap && !Grass(tx - 1, ty)) return false;
                    if (fx > 1 - edgeGap && !Grass(tx + 1, ty)) return false;
                    if (fy < edgeGap && !Grass(tx, ty - 1)) return false;
                    if (fy > 1 - edgeGap && !Grass(tx, ty + 1)) return false;
                    foreach (var r in nearRoads) if (Geo.DistToPolyline(p, r.points) < Reach(r) + roadGap) return false;
                    foreach (var b in nearBulbs) if (Geo.Dist(p, b.at) < bulbReach + roadGap) return false;
                }
                return true;
            }
            bool Grass(int tx, int ty) => Grid.InBounds(tx, ty, w, h) && grass.Contains(Grid.Index(tx, ty, w));
            // A grid-aligned frame for the park, picked from its seed: playground pieces and picnic tables line up with it.
            var axes = new[] { new Vec2(0, 1), new Vec2(1, 0), new Vec2(0, -1), new Vec2(-1, 0) };
            int turn = rng.Int(0, 3);

            // The playground, in a park big enough: round the deepest spot in from the edge that takes enough of it.
            Vec2? playground = null;
            if (grass.Count >= PLAYGROUND_TILES)
            {
                // Deepest first, then nearest the park's middle; the seed decides between equals.
                var middles = new List<(int depth, double near, double roll, int t)>();
                foreach (int t in tiles)
                    if (depth[t] >= 2) middles.Add((depth[t], Math.Round(Geo.Dist(Middle(t, w), park.middle) * 1000), rng.Next(), t));
                middles.Sort((a, b) => a.depth != b.depth ? b.depth - a.depth : a.near != b.near ? a.near.CompareTo(b.near) : a.roll != b.roll ? a.roll.CompareTo(b.roll) : a.t - b.t);
                var kit = new[] { ParkKit.ClimbingFrame, ParkKit.Swings, ParkKit.Slide, ParkKit.Spinner, ParkKit.Seesaw };
                const int TRIES = 6;
                for (int k = 0; k < Math.Min(TRIES, middles.Count) && playground == null; k++)
                {
                    var c = Middle(middles[k].t, w);
                    var spots = Ring(c, Tiles(PLAYGROUND_REACH_M), 0.25);
                    int before = placed.Count;
                    foreach (var kind in kit)
                    {
                        // Each faces out from the middle, square to the park's frame (a slide runs down away from the rest).
                        foreach (var at in spots)
                        {
                            var away = Geo.Sub(at, c);
                            var face = axes[turn];
                            for (int a = 0; a < 4; a++)
                                if (Geo.Dot(away, axes[(turn + a) % 4]) > Geo.Dot(away, face) + 1e-9) face = axes[(turn + a) % 4];
                            var piece = Piece(kind, park.blockId, at, face);
                            if (!Fits(piece, Tiles(PLAY_GAP_M))) continue;
                            placed.Add(piece);
                            break;
                        }
                    }
                    if (placed.Count - before >= PLAYGROUND_FEWEST) playground = c;
                    else placed.RemoveRange(before, placed.Count - before);
                }
                // A bench facing it, for whoever's watching, and a bin by that.
                if (playground != null)
                {
                    var c = playground.Value;
                    foreach (var at in Ring(c, Tiles(PLAYGROUND_REACH_M) + 1, 0.25))
                    {
                        if (Geo.Dist(at, c) < Tiles(PLAYGROUND_REACH_M) - 0.5) continue;
                        var face = Unit(Geo.Sub(c, at));
                        var bench = Piece(ParkKit.Bench, park.blockId, at, face);
                        if (!Fits(bench, 0)) continue;
                        placed.Add(bench);
                        BinBeside(bench);
                        break;
                    }
                }
            }

            // Benches along the edges, their backs to the road (or to whatever's next door), facing in.
            int benches = Math.Max(1, Math.Min(MAX_BENCHES, grass.Count / BENCH_TILES));
            double bd = Footprint(ParkKit.Bench).depth;
            var edge = new List<ParkPiece>();
            foreach (int t in tiles)
            {
                if (depth[t] != 1) continue;
                var c = Middle(t, w);
                for (int d = 0; d < 4; d++)
                {
                    var (dx, dy) = Grid.DIRS4[d];
                    if (Grass(t % w + dx, t / w + dy)) continue;
                    var n = new Vec2(dx, dy);
                    var side = new Vec2(c.x + dx * 0.5, c.y + dy * 0.5);
                    RoadStroke near = null;
                    double nearBy = double.PositiveInfinity;
                    foreach (var r in roads)
                    {
                        double by = Geo.DistToPolyline(side, r.points) - Reach(r);
                        if (by < nearBy)
                        {
                            nearBy = by;
                            near = r;
                        }
                    }
                    if (near != null && nearBy < 1)
                    {
                        // By a road: square to it, just in from its sidewalk.
                        var q = Geo.ClosestOnPolyline(c, near.points).point;
                        var m = Unit(Geo.Sub(c, q));
                        if (m.x == 0 && m.y == 0) continue;
                        double off = Reach(near) + roadGap + Tiles(BENCH_SETBACK_M) + bd / 2;
                        edge.Add(Piece(ParkKit.Bench, park.blockId, new Vec2(q.x + m.x * off, q.y + m.y * off), m));
                    }
                    else edge.Add(Piece(ParkKit.Bench, park.blockId, new Vec2(c.x + n.x * (0.5 - edgeGap - bd / 2 - 0.01), c.y + n.y * (0.5 - edgeGap - bd / 2 - 0.01)), new Vec2(-n.x, -n.y)));
                }
            }
            rng.Shuffle(edge);
            var benchesPlaced = new List<ParkPiece>();
            foreach (var b in edge)
            {
                if (benchesPlaced.Count >= benches) break;
                if (benchesPlaced.Exists(o => Geo.Dist(o.at, b.at) < Tiles(BENCH_SPACING_M))) continue;
                if (!Fits(b, 0)) continue;
                placed.Add(b);
                benchesPlaced.Add(b);
            }

            // Picnic tables out on the grass, away from the playground, lined up with the park's frame.
            int tables = Math.Min(MAX_PICNIC, grass.Count / PICNIC_TILES);
            if (tables > 0)
            {
                var inner = tiles.FindAll(t => depth[t] >= 2);
                rng.Shuffle(inner);
                var tablesPlaced = new List<Vec2>();
                foreach (int t in inner)
                {
                    if (tablesPlaced.Count >= tables) break;
                    var at = new Vec2(t % w + 0.5 + rng.Range(-0.2, 0.2), t / w + 0.5 + rng.Range(-0.2, 0.2));
                    if (playground != null && Geo.Dist(at, playground.Value) < Tiles(PLAYGROUND_REACH_M) + 1) continue;
                    if (tablesPlaced.Exists(o => Geo.Dist(o, at) < Tiles(PICNIC_SPACING_M))) continue;
                    var table = Piece(ParkKit.PicnicTable, park.blockId, at, axes[(turn + rng.Int(0, 3)) % 4]);
                    if (!Fits(table, 0)) continue;
                    placed.Add(table);
                    tablesPlaced.Add(at);
                }
            }

            // Bins: by every other bench along the edge, and one at least in every park.
            for (int k = 0; k < benchesPlaced.Count; k += 2) BinBeside(benchesPlaced[k]);
            if (!placed.Exists(p => p.kind == ParkKit.Bin))
                foreach (var p in placed.ToArray())
                    if (BinBeside(p)) break;
            return placed;

            // A bin at one end of a bench (or beside a table), standing level with it.
            bool BinBeside(ParkPiece by)
            {
                var (nw, _) = Footprint(ParkKit.Bin);
                var s = by.Side;
                foreach (int sign in new[] { 1, -1 })
                {
                    double off = by.width / 2 + Tiles(PIECE_GAP_M) + nw / 2 + 0.02;
                    var bin = Piece(ParkKit.Bin, park.blockId, new Vec2(by.at.x + s.x * off * sign, by.at.y + s.y * off * sign), by.front);
                    if (!Fits(bin, 0)) continue;
                    placed.Add(bin);
                    return true;
                }
                return false;
            }
        }

        static ParkPiece Piece(ParkKit kind, int blockId, Vec2 at, Vec2 front)
        {
            var (width, depth) = Footprint(kind);
            return new ParkPiece { kind = kind, blockId = blockId, at = at, front = front, width = width, depth = depth };
        }

        static Vec2 Middle(int t, int w) => new Vec2(t % w + 0.5, t / w + 0.5);

        static Vec2 Unit(Vec2 v)
        {
            double l = Numbers.Hypot(v.x, v.y);
            return l < 1e-9 ? new Vec2(0, 0) : new Vec2(v.x / l, v.y / l);
        }

        /// <summary>Points on a grid `step` apart within `reach` of c, nearest first (ties in a fixed order).</summary>
        static List<Vec2> Ring(Vec2 c, double reach, double step)
        {
            var o = new List<(double d, int k, Vec2 p)>();
            int n = (int)Math.Ceiling(reach / step), k = 0;
            for (int j = -n; j <= n; j++)
                for (int i = -n; i <= n; i++)
                {
                    var p = new Vec2(c.x + i * step, c.y + j * step);
                    double d = Geo.Dist(p, c);
                    if (d <= reach) o.Add((d, k++, p));
                }
            o.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.k.CompareTo(b.k));
            return o.ConvertAll(e => e.p);
        }

        /// <summary>Points over a piece's footprint, edges and corners included, no more than a tenth of a tile apart.</summary>
        public static List<Vec2> Samples(ParkPiece c)
        {
            var o = new List<Vec2>();
            var s = c.Side;
            int nu = Math.Max(1, (int)Math.Ceiling(c.width / 0.1)), nv = Math.Max(1, (int)Math.Ceiling(c.depth / 0.1));
            for (int j = 0; j <= nv; j++)
                for (int i = 0; i <= nu; i++)
                {
                    double u = -c.width / 2 + c.width * i / nu, v = -c.depth / 2 + c.depth * j / nv;
                    o.Add(new Vec2(c.at.x + s.x * u + c.front.x * v, c.at.y + s.y * u + c.front.y * v));
                }
            return o;
        }

        /// <summary>How far apart two convex polygons are (0 if they touch or overlap).</summary>
        public static double Apart(Vec2[] a, Vec2[] b)
        {
            if (Overlap(a, b)) return 0;
            double best = double.PositiveInfinity;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    best = Math.Min(best, Geo.SegmentDistance(a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length]));
            return best;
        }

        /// <summary>Whether two convex polygons overlap (the separating axis test).</summary>
        static bool Overlap(Vec2[] a, Vec2[] b)
        {
            foreach (var poly in new[] { a, b })
                for (int i = 0; i < poly.Length; i++)
                {
                    var e = Geo.Sub(poly[(i + 1) % poly.Length], poly[i]);
                    var axis = new Vec2(-e.y, e.x);
                    double a0 = double.PositiveInfinity, a1 = double.NegativeInfinity, b0 = double.PositiveInfinity, b1 = double.NegativeInfinity;
                    foreach (var p in a)
                    {
                        double d = Geo.Dot(p, axis);
                        a0 = Math.Min(a0, d);
                        a1 = Math.Max(a1, d);
                    }
                    foreach (var p in b)
                    {
                        double d = Geo.Dot(p, axis);
                        b0 = Math.Min(b0, d);
                        b1 = Math.Max(b1, d);
                    }
                    if (a1 < b0 || b1 < a0) return false;
                }
            return true;
        }
    }
}

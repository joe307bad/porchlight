// Stage 9: docks. Each lake big enough gets a pier (two on a big one) out
// from open, dry shore near a road, with open water off its end and boats
// moored either side. Every boat's footprint, with a margin, keeps to the
// water and clear of the piers and the other boats: a berth that doesn't fit
// is left empty, and a dock with no room for a boat isn't built. Piers says
// where each part of a dock is.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class DocksStage : IStage
    {
        /// <summary>A lake this big gets a second dock.</summary>
        const int BIG_LAKE = 400;
        /// <summary>How far apart two docks on one lake are, at least (tiles, shore to shore).</summary>
        const int DOCK_SPACING = 14;
        /// <summary>A pier's length (tiles), the longer the better.</summary>
        static readonly int[] PIERS = { 4, 3 };
        /// <summary>Furthest from a road (walking over land, tiles) a dock goes.</summary>
        const int ROAD_REACH = 12;
        /// <summary>Open water kept off a pier's end (tiles).</summary>
        const int OPEN = 2;
        /// <summary>How far either side of a pier's centreline its water is kept to itself (tiles).</summary>
        const int BERTH = 2;

        public string Name => "docks";

        struct Candidate
        {
            public int shore, dir, length;
            public double score;
        }

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            var lake = Piers.Lakes(tiles);
            var fromRoad = RoadDistance(model);
            var lakes = new List<List<int>>();
            for (int id = 0; id < lake.count; id++) lakes.Add(new List<int>());
            for (int i = 0; i < w * h; i++) if (lake.of[i] >= 0) lakes[lake.of[i]].Add(i);
            var taken = new byte[w * h];
            var docks = new List<Dock>();

            for (int id = 0; id < lakes.Count; id++)
            {
                if (lakes[id].Count < Piers.LAKE_MIN) continue;
                bool Water(int x, int y)
                {
                    if (!Grid.InBounds(x, y, w, h)) return false;
                    int i = Grid.Index(x, y, w);
                    return lake.of[i] == id && tiles.overlay[i] == Overlay.NONE;
                }
                // Every bit of open shore near a road a pier fits out from, nearest the road first.
                var candidates = new List<Candidate>();
                foreach (int t in lakes[id])
                {
                    int tx = t % w, ty = t / w;
                    for (int d = 0; d < 4; d++)
                    {
                        // Drawn for every spot, so the stream stays aligned.
                        double jitter = rng.Next();
                        var (dx, dy) = Grid.DIRS4[d];
                        int sx = tx - dx, sy = ty - dy;
                        if (!Grid.InBounds(sx, sy, w, h)) continue;
                        int shore = Grid.Index(sx, sy, w);
                        if (fromRoad[shore] > ROAD_REACH || !Landing(tiles, shore)) continue;
                        foreach (int length in PIERS)
                        {
                            if (!Fits(Water, sx, sy, dx, dy, length)) continue;
                            candidates.Add(new Candidate { shore = shore, dir = d, length = length, score = fromRoad[shore] + jitter * 3 });
                            break;
                        }
                    }
                }
                Numbers.StableSort(candidates, (a, b) => a.score - b.score);

                var placed = new List<int>();
                int wanted = lakes[id].Count >= BIG_LAKE ? 2 : 1;
                foreach (var c in candidates)
                {
                    if (placed.Count >= wanted) break;
                    int sx = c.shore % w, sy = c.shore / w;
                    var (fx, fy) = Grid.DIRS4[c.dir];
                    bool crowded = false;
                    foreach (int q in placed) if (Math.Abs(q % w - sx) + Math.Abs(q / w - sy) < DOCK_SPACING) crowded = true;
                    var zone = Zone(sx, sy, fx, fy, c.length, w, h);
                    foreach (int i in zone) if (taken[i] != 0) crowded = true;
                    if (crowded) continue;
                    var dock = new Dock { id = docks.Count, shore = new TilePos(sx, sy), tiles = new List<TilePos>(), facing = (Facing)c.dir, boats = new List<Mooring>() };
                    for (int k = 1; k <= c.length; k++) dock.tiles.Add(new TilePos(sx + fx * k, sy + fy * k));
                    // Boats either side, bows out to the water, the outermost first: two at least, if there's room.
                    foreach (var berth in Piers.Berths(dock))
                    {
                        if (!Clear(berth, dock, docks, Water)) continue;
                        if (dock.boats.Count >= 2 && !rng.Chance(0.55)) continue;
                        dock.boats.Add(berth);
                    }
                    if (dock.boats.Count == 0) continue;
                    foreach (int i in zone) taken[i] = 1;
                    placed.Add(c.shore);
                    docks.Add(dock);
                }
            }
            var next = model.With();
            next.docks = docks;
            return next;
        }

        /// <summary>Dry, open ground a pier can start from: not a road, a building or the airport, and reachable on foot.</summary>
        static bool Landing(TileGrid tiles, int i) =>
            !Terrain.IsWater(tiles.terrain[i]) && tiles.walkable[i] != 0 && tiles.overlay[i] == Overlay.NONE && tiles.buildingId[i] < 0;

        /// <summary>A pier of `length` from shore tile (sx, sy) out along (dx, dy): open water of this lake under it, a tile either side and off its end, and wider still past the end.</summary>
        static bool Fits(Func<int, int, bool> water, int sx, int sy, int dx, int dy, int length)
        {
            for (int k = 1; k <= length + OPEN; k++)
            {
                int reach = k > length ? BERTH : 1;
                for (int j = -reach; j <= reach; j++)
                    if (!water(sx + dx * k - dy * j, sy + dy * k + dx * j)) return false;
            }
            return true;
        }

        /// <summary>The tiles a dock keeps to itself: from the shore to the open water off its end, BERTH either side.</summary>
        static List<int> Zone(int sx, int sy, int dx, int dy, int length, int w, int h)
        {
            var o = new List<int>();
            for (int k = 0; k <= length + OPEN; k++)
                for (int j = -BERTH; j <= BERTH; j++)
                {
                    int x = sx + dx * k - dy * j, y = sy + dy * k + dx * j;
                    if (Grid.InBounds(x, y, w, h)) o.Add(Grid.Index(x, y, w));
                }
            return o;
        }

        /// <summary>Whether a boat moored here keeps (with the margin) to open water, off every pier and clear of every other boat.</summary>
        static bool Clear(Mooring m, Dock dock, List<Dock> docks, Func<int, int, bool> water)
        {
            var box = Piers.Footprint(m, Piers.MARGIN);
            if (!Piers.OverWater(box, water)) return false;
            if (Piers.Overlap(box, Piers.Deck(dock))) return false;
            foreach (var b in dock.boats) if (Piers.Overlap(box, Piers.Footprint(b, Piers.MARGIN))) return false;
            foreach (var d in docks)
            {
                if (Piers.Overlap(box, Piers.Deck(d))) return false;
                foreach (var b in d.boats) if (Piers.Overlap(box, Piers.Footprint(b, Piers.MARGIN))) return false;
            }
            return true;
        }

        /// <summary>Steps over dry land (4-connected) from the nearest road, street or path, round buildings; Infinity where none reaches.</summary>
        static float[] RoadDistance(TownModel model)
        {
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            var d = new float[w * h];
            var queue = new List<int>();
            for (int i = 0; i < d.Length; i++)
            {
                byte o = tiles.overlay[i];
                bool road = o == Overlay.ROAD_MAIN || o == Overlay.ROAD_STREET || o == Overlay.PATH;
                d[i] = road ? 0 : float.PositiveInfinity;
                if (road) queue.Add(i);
            }
            for (int head = 0; head < queue.Count; head++)
            {
                int c = queue[head];
                int cx = c % w, cy = c / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (!Grid.InBounds(nx, ny, w, h)) continue;
                    int j = Grid.Index(nx, ny, w);
                    if (!float.IsPositiveInfinity(d[j]) || Terrain.IsWater(tiles.terrain[j]) || tiles.buildingId[j] >= 0) continue;
                    d[j] = d[c] + 1;
                    queue.Add(j);
                }
            }
            return d;
        }

        public List<string> Check(TownModel model) => Piers.Check(model);
    }

    /// <summary>Where each part of a dock is, in tiles: its deck, its berths, and its boats' footprints.</summary>
    public static class Piers
    {
        /// <summary>Water tiles a lake needs for a dock and boats.</summary>
        public const int LAKE_MIN = 60;
        /// <summary>Half the deck's width (tiles).</summary>
        public const double DECK_HALF = 0.42;
        /// <summary>A boat's half length and half width (tiles), as Driving steers one.</summary>
        public const double BOAT_HALF_LENGTH = 0.7;
        public const double BOAT_HALF_WIDTH = 0.3;
        /// <summary>Kept clear all round a moored boat (tiles).</summary>
        public const double MARGIN = 0.15;
        /// <summary>A moored boat's middle, out from the pier's centreline (tiles): its margin just clear of the deck.</summary>
        const double ABREAST = DECK_HALF + BOAT_HALF_WIDTH + MARGIN + 0.03;
        /// <summary>The outermost boats' middles, back from the pier's end (tiles).</summary>
        const double BOW = 0.6;
        /// <summary>Between one boat's stern and the next one's bow, along a side (tiles).</summary>
        const double BERTH_GAP = 0.3;

        /// <summary>The shore's edge where the pier starts, and the way it runs.</summary>
        static (double x, double y, int fx, int fy) Root(Dock d)
        {
            var (fx, fy) = Grid.DIRS4[(int)d.facing];
            return (d.shore.x + 0.5 + fx * 0.5, d.shore.y + 0.5 + fy * 0.5, fx, fy);
        }

        static (double x0, double y0, double x1, double y1) Span(double ax, double ay, double bx, double by, double half, int fx, int fy)
        {
            double rx = Math.Abs(fy) * half, ry = Math.Abs(fx) * half;
            return (Math.Min(ax, bx) - rx, Math.Min(ay, by) - ry, Math.Max(ax, bx) + rx, Math.Max(ay, by) + ry);
        }

        /// <summary>The pier's deck, from the shore's edge to its end.</summary>
        public static (double x0, double y0, double x1, double y1) Deck(Dock d)
        {
            var (x, y, fx, fy) = Root(d);
            int n = d.tiles.Count;
            return Span(x, y, x + fx * n, y + fy * n, DECK_HALF, fx, fy);
        }

        /// <summary>Every place along a pier a boat could moor, the outermost first, both sides of each.</summary>
        public static List<Mooring> Berths(Dock d)
        {
            var (x, y, fx, fy) = Root(d);
            var o = new List<Mooring>();
            for (double a = d.tiles.Count - BOW; a - BOAT_HALF_LENGTH >= 0; a -= BOAT_HALF_LENGTH * 2 + BERTH_GAP)
                foreach (int side in new[] { 1, -1 })
                    o.Add(new Mooring { x = x + fx * a - fy * ABREAST * side, y = y + fy * a + fx * ABREAST * side, facing = d.facing });
            return o;
        }

        /// <summary>A moored boat's footprint, grown by `grow` all round.</summary>
        public static (double x0, double y0, double x1, double y1) Footprint(Mooring m, double grow = 0)
        {
            bool alongX = m.facing == Facing.E || m.facing == Facing.W;
            double hx = (alongX ? BOAT_HALF_LENGTH : BOAT_HALF_WIDTH) + grow;
            double hy = (alongX ? BOAT_HALF_WIDTH : BOAT_HALF_LENGTH) + grow;
            return (m.x - hx, m.y - hy, m.x + hx, m.y + hy);
        }

        public static bool Overlap((double x0, double y0, double x1, double y1) a, (double x0, double y0, double x1, double y1) b) =>
            a.x0 < b.x1 && b.x0 < a.x1 && a.y0 < b.y1 && b.y0 < a.y1;

        /// <summary>Whether every tile the box touches is `water`.</summary>
        public static bool OverWater((double x0, double y0, double x1, double y1) box, Func<int, int, bool> water)
        {
            for (int y = (int)Math.Floor(box.y0); y < box.y1; y++)
                for (int x = (int)Math.Floor(box.x0); x < box.x1; x++)
                    if (!water(x, y)) return false;
            return true;
        }

        /// <summary>The tiles every moored boat's footprint (grown by the margin) touches.</summary>
        public static HashSet<int> BoatTiles(TownModel model)
        {
            int w = model.width, h = model.height;
            var o = new HashSet<int>();
            foreach (var d in model.docks)
                foreach (var m in d.boats)
                {
                    var box = Footprint(m, MARGIN);
                    for (int y = (int)Math.Floor(box.y0); y < box.y1; y++)
                        for (int x = (int)Math.Floor(box.x0); x < box.x1; x++)
                            if (Grid.InBounds(x, y, w, h)) o.Add(Grid.Index(x, y, w));
                }
            return o;
        }

        /// <summary>4-connected bodies of water (bridges and all): each tile's lake (or -1), and how many.</summary>
        public static (int[] of, int count) Lakes(TileGrid tiles)
        {
            int w = tiles.width, h = tiles.height;
            var of = new int[w * h];
            for (int i = 0; i < of.Length; i++) of[i] = -1;
            int count = 0;
            for (int i = 0; i < of.Length; i++)
            {
                if (of[i] >= 0 || !Terrain.IsWater(tiles.terrain[i])) continue;
                var queue = new List<int> { i };
                of[i] = count;
                for (int head = 0; head < queue.Count; head++)
                {
                    int c = queue[head];
                    int cx = c % w, cy = c / w;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (!Grid.InBounds(nx, ny, w, h)) continue;
                        int j = Grid.Index(nx, ny, w);
                        if (of[j] < 0 && Terrain.IsWater(tiles.terrain[j]))
                        {
                            of[j] = count;
                            queue.Add(j);
                        }
                    }
                }
                count++;
            }
            return (of, count);
        }

        /// <summary>
        /// Every dock runs out from dry land over open water, with a boat at
        /// least; every boat floats on open water, off the decks, and keeps its
        /// margin from every other boat; no two docks meet.
        /// </summary>
        public static List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            bool Water(int x, int y) =>
                Grid.InBounds(x, y, w, h) && Terrain.IsWater(tiles.terrain[Grid.Index(x, y, w)]) && tiles.overlay[Grid.Index(x, y, w)] == Overlay.NONE;
            var boats = new List<Mooring>();
            foreach (var d in model.docks)
            {
                var (fx, fy) = Grid.DIRS4[(int)d.facing];
                if (!Grid.InBounds(d.shore.x, d.shore.y, w, h) || Terrain.IsWater(tiles.terrain[Grid.Index(d.shore.x, d.shore.y, w)]))
                    errors.Add($"dock {d.id} doesn't start on dry land ({d.shore.x}, {d.shore.y})");
                for (int k = 0; k < d.tiles.Count; k++)
                {
                    var t = d.tiles[k];
                    if (t.x != d.shore.x + fx * (k + 1) || t.y != d.shore.y + fy * (k + 1)) errors.Add($"dock {d.id} isn't straight out from the shore");
                    else if (!Water(t.x, t.y)) errors.Add($"dock {d.id} isn't over open water at ({t.x}, {t.y})");
                }
                if (d.boats.Count == 0) errors.Add($"dock {d.id} has no boats");
                foreach (var e in model.docks)
                    if (e.id > d.id && Overlap(Deck(d), Deck(e))) errors.Add($"docks {d.id} and {e.id} meet");
                foreach (var m in d.boats)
                {
                    var box = Footprint(m);
                    if (!OverWater(box, Water)) errors.Add($"a boat at dock {d.id} touches land or a bridge at ({m.x:0.0}, {m.y:0.0})");
                    foreach (var e in model.docks)
                        if (Overlap(Footprint(m, MARGIN), Deck(e))) errors.Add($"a boat at dock {d.id} runs into dock {e.id} at ({m.x:0.0}, {m.y:0.0})");
                    foreach (var other in boats)
                        if (Overlap(Footprint(m, MARGIN), Footprint(other))) errors.Add($"boats at ({m.x:0.0}, {m.y:0.0}) and ({other.x:0.0}, {other.y:0.0}) touch");
                    boats.Add(m);
                }
            }
            return errors;
        }
    }
}

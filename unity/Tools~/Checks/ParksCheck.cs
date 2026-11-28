// The parks' recreation kit (Shared/Explore/Parks.cs), in each starter town:
// every bench, picnic table, bin and piece of playground wholly inside its
// park, off the roads, parking lanes and sidewalks, off any building, clear
// of where a tree may be planted, and clear of every other piece. Measured
// here on its own terms (points over each footprint, the sidewalks as the
// layout draws them), not by asking Parks.cs again. Also: every park has a
// bench and a bin, the playgrounds are only in the big parks and grouped
// together, the benches face into their park, and the same town gives the
// same kit twice.
//
//   dotnet run --project "unity/Tools~/Checks" -- parks [server/towns]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

static class ParksCheck
{
    /// <summary>Points over a footprint this far apart (tiles), at most: finer than the thinnest piece.</summary>
    const double STEP = 0.04;

    public static int Run(string dir)
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }
        int allPieces = 0, allPlaygrounds = 0;
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var (seed, settings) = TownKey.Parse(JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString());
            var model = Generator.Generate(seed, settings);
            var layout = Layout.Build(model);
            int w = model.width, h = model.height;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var pieces = Parks.Plan(model, layout);
            double planMs = timer.Elapsed.TotalMilliseconds;
            var again = Parks.Plan(model, Layout.Build(model));
            allPieces += pieces.Count;
            var parks = Parks.All(model);
            var byKind = pieces.GroupBy(p => p.kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}");
            Console.WriteLine($"{name}: {parks.Count} parks, {pieces.Count} pieces ({string.Join(", ", byKind)}), planned in {planMs:0} ms");
            Expect(again.Count == pieces.Count && again.Zip(pieces, (a, b) => a.kind == b.kind && a.at.Equals(b.at) && a.front.Equals(b.front)).All(x => x),
                $"{name}: the same town gives the same kit");

            // Each park's own ground: its block's tiles, less road, water and buildings.
            var parkTiles = new Dictionary<int, HashSet<int>>();
            foreach (var b in model.ParkBlockSet())
                parkTiles[b] = new HashSet<int>(Parcels.TilesIn(model.blocks[b].polygon, model.blocks[b].holes, w, h)
                    .Where(t => model.tiles.overlay[t] == Overlay.NONE && !Terrain.IsWater(model.tiles.terrain[t])));
            var houseTiles = new HashSet<int>(layout.houses.SelectMany(hs => hs.tiles.Select(t => Grid.Index(t.x, t.y, w))));
            var kept = Parks.KeptClear(model, pieces);
            double bulbAsphalt = Raster.BulbRadius(model.@params) + Layout.PARKING;
            double treeGap = Parks.Tiles(Parks.TREE_GAP_M);

            int outside = 0, onRoad = 0, onSidewalk = 0, onBuilding = 0, byTree = 0, overlapping = 0, facingOut = 0;
            var examples = new List<string>();
            void Note(string what, ParkPiece p)
            {
                if (examples.Count < 6) examples.Add($"{p.kind} in park {p.blockId} at ({p.at.x:0.00}, {p.at.y:0.00}): {what}");
            }
            var points = pieces.Select(Points).ToList();
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                var pts = points[i];
                // Inside its park.
                if (!parkTiles.TryGetValue(p.blockId, out var own) || pts.Any(q => !own.Contains(TileOf(q, w))))
                {
                    outside++;
                    Note("outside its park", p);
                }
                // Off the asphalt and parking lanes (a path's own width), and the cul-de-sacs' bulbs.
                if (pts.Any(q => layout.roads.Any(r => Geo.DistToPolyline(q, r.points) < r.width / 2 + (r.kind == RoadKind.Path ? 0 : Layout.PARKING))
                    || layout.bulbs.Any(b => Geo.Dist(q, b.at) < bulbAsphalt)))
                {
                    onRoad++;
                    Note("on a road", p);
                }
                // Off the sidewalks, as the layout lays them (their middle lines, SIDEWALK wide).
                if (pts.Any(q => layout.sidewalks.paths.Any(s => Geo.DistToPolyline(q, s.points) < Layout.SIDEWALK / 2)))
                {
                    onSidewalk++;
                    Note("on a sidewalk", p);
                }
                // Off any building.
                if (pts.Any(q => { int t = TileOf(q, w); return t < 0 || model.tiles.buildingId[t] >= 0 || houseTiles.Contains(t) || layout.use[t] == Use.HOUSE || layout.use[t] == Use.SHED; }))
                {
                    onBuilding++;
                    Note("on a building", p);
                }
                // Clear of every tree the game may plant: none in a tile kept clear, and wherever else one may
                // stand in a tile near it (its middle, give or take Parks.TREE_JITTER), at least TREE_GAP_M away.
                bool nearTree = false;
                double reach = Math.Max(p.width, p.depth) / 2 + treeGap + 1;
                for (int y = (int)Math.Floor(p.at.y - reach); y <= (int)Math.Floor(p.at.y + reach) && !nearTree; y++)
                    for (int x = (int)Math.Floor(p.at.x - reach); x <= (int)Math.Floor(p.at.x + reach) && !nearTree; x++)
                    {
                        if (!Grid.InBounds(x, y, w, h) || kept.Contains(Grid.Index(x, y, w))) continue;
                        double lo = x + 0.5 - Parks.TREE_JITTER, hi = x + 0.5 + Parks.TREE_JITTER;
                        double ly = y + 0.5 - Parks.TREE_JITTER, hy = y + 0.5 + Parks.TREE_JITTER;
                        // The nearest a tree in that square comes to any point of the piece.
                        foreach (var q in pts)
                        {
                            double dx = Math.Max(0, Math.Max(lo - q.x, q.x - hi)), dy = Math.Max(0, Math.Max(ly - q.y, q.y - hy));
                            if (Math.Sqrt(dx * dx + dy * dy) < treeGap - STEP) { nearTree = true; break; }
                        }
                    }
                if (nearTree)
                {
                    byTree++;
                    Note("where a tree may stand", p);
                }
                // Clear of every other piece: no point of one inside the other.
                for (int j = i + 1; j < pieces.Count; j++)
                {
                    var o = pieces[j];
                    if (Geo.Dist(p.at, o.at) > (p.width + p.depth + o.width + o.depth) / 2 + 0.1) continue;
                    if (pts.Any(q => Inside(o, q)) || points[j].Any(q => Inside(p, q)))
                    {
                        overlapping++;
                        Note($"overlaps a {o.kind}", p);
                    }
                }
                // A bench faces into its park: there's park ahead of it.
                if (p.kind == ParkKit.Bench)
                {
                    double look = Parks.Tiles(Parks.BENCH_OUTLOOK_M);
                    var ahead = new Vec2(p.at.x + p.front.x * look, p.at.y + p.front.y * look);
                    if (own == null || !own.Contains(TileOf(ahead, w)))
                    {
                        facingOut++;
                        Note("faces out of its park", p);
                    }
                }
            }
            foreach (var e in examples) Console.WriteLine($"     {e}");
            Expect(outside == 0, $"{name}: every piece inside its park ({outside} not)");
            Expect(onRoad == 0, $"{name}: every piece off the roads and parking lanes ({onRoad} on them)");
            Expect(onSidewalk == 0, $"{name}: every piece off the sidewalks ({onSidewalk} on them)");
            Expect(onBuilding == 0, $"{name}: every piece off the buildings ({onBuilding} on them)");
            Expect(byTree == 0, $"{name}: every piece clear of where a tree may stand ({byTree} not)");
            Expect(overlapping == 0, $"{name}: no piece overlaps another ({overlapping} pairs do)");
            Expect(facingOut == 0, $"{name}: every bench faces into its park ({facingOut} not)");

            // Every park has somewhere to sit and a bin; a playground only in a big park, its pieces together.
            int bare = parks.Count(pk => !pieces.Any(p => p.blockId == pk.blockId && p.kind == ParkKit.Bench) || !pieces.Any(p => p.blockId == pk.blockId && p.kind == ParkKit.Bin));
            Expect(bare == 0, $"{name}: every park has a bench and a bin ({bare} without)");
            int playgrounds = 0, small = 0, scattered = 0;
            foreach (var pk in parks)
            {
                var play = pieces.Where(p => p.blockId == pk.blockId && Parks.IsPlay(p.kind)).ToList();
                if (play.Count == 0) continue;
                playgrounds++;
                if (pk.tiles.Count < Parks.PLAYGROUND_TILES) small++;
                double span = Parks.Tiles(Parks.PLAYGROUND_REACH_M) * 2 + 0.01;
                if (play.Any(a => play.Any(b => Geo.Dist(a.at, b.at) > span))) scattered++;
                if (play.GroupBy(p => p.kind).Any(g => g.Count() > 1)) scattered++;
            }
            allPlaygrounds += playgrounds;
            Expect(small == 0, $"{name}: playgrounds only in parks of {Parks.PLAYGROUND_TILES} tiles or more ({small} in smaller)");
            Expect(scattered == 0, $"{name}: each playground's pieces together, one of each ({playgrounds} playgrounds, {scattered} not)");
        }
        Expect(allPieces > 0, $"the starter towns' parks have their kit ({allPieces} pieces)");
        Expect(allPlaygrounds > 0, $"the starter towns have playgrounds ({allPlaygrounds})");
        Console.WriteLine(fails.Count == 0 ? "parks: all ok" : $"parks: {fails.Count} failed");
        return fails.Count == 0 ? 0 : 1;
    }

    static int TileOf(Vec2 q, int w)
    {
        int x = (int)Math.Floor(q.x), y = (int)Math.Floor(q.y);
        return x < 0 || y < 0 || x >= w ? -1 : y * w + x;
    }

    /// <summary>Points over a piece's footprint, edges and corners included, STEP apart at most.</summary>
    static List<Vec2> Points(ParkPiece p)
    {
        var o = new List<Vec2>();
        var s = p.Side;
        int nu = Math.Max(1, (int)Math.Ceiling(p.width / STEP)), nv = Math.Max(1, (int)Math.Ceiling(p.depth / STEP));
        for (int j = 0; j <= nv; j++)
            for (int i = 0; i <= nu; i++)
            {
                double u = p.width * ((double)i / nu - 0.5), v = p.depth * ((double)j / nv - 0.5);
                o.Add(new Vec2(p.at.x + s.x * u + p.front.x * v, p.at.y + s.y * u + p.front.y * v));
            }
        return o;
    }

    /// <summary>Whether q is inside a piece's footprint (in its own frame).</summary>
    static bool Inside(ParkPiece p, Vec2 q)
    {
        var s = p.Side;
        double dx = q.x - p.at.x, dy = q.y - p.at.y;
        return Math.Abs(dx * s.x + dy * s.y) <= p.width / 2 && Math.Abs(dx * p.front.x + dy * p.front.y) <= p.depth / 2;
    }
}

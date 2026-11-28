// An editor's regenerated areas (Skeleton.regions, Gen/Regions.cs), town by town:
//
//   dotnet run -c Release --project "unity/Tools~/Checks" -- regions [server/towns]
//
// Picks a few patches of blocks in each town (a block a third of the way out in each direction and its
// neighbours), regenerates each with every option alone, all of them, and none, and three salts each, every stage
// checked. Fails a patch whose streets and buildings come out the same for every salt (no variety), that breaks a stage's
// rules on every salt (nothing the draft worker's retries could fix), or that changes the town outside the patch.
// Reports whether each option got what it asked for.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;
using Porchlight.Life;

static class RegionCheck
{
    static readonly (string name, RegionWant want)[] Options =
    {
        ("none", new RegionWant()),
        ("park", new RegionWant { park = true }),
        ("school", new RegionWant { school = true }),
        ("shops", new RegionWant { shops = true }),
        ("houses", new RegionWant { houses = true }),
        ("roundabout", new RegionWant { roundabout = true }),
        ("all", new RegionWant { park = true, school = true, shops = true, houses = true, roundabout = true }),
    };

    public static int Run(string dir)
    {
        int bad = 0;
        var asked = new Dictionary<string, (int got, int of)>();
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string town = Path.GetFileNameWithoutExtension(f);
            var key = JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString();
            var (seed, p) = TownKey.Parse(key);
            var baseModel = Generator.Generate(seed, p);
            var parks = LandStage.MoreParkMiddles(baseModel);
            foreach (var patch in Patches(baseModel))
            {
                foreach (var (name, want) in Options)
                {
                    var streets = new HashSet<string>();
                    int broke = 0, got = 0, outsideChanged = 0;
                    string firstBreak = null;
                    long ms = 0;
                    for (int salt = 1; salt <= 3; salt++)
                    {
                        var sk = new Skeleton();
                        sk.regions.Add(new SkeletonRegion { blocks = patch.Select(b => b.polygon).ToList(), salt = salt * 7919, want = want });
                        sk.parks = parks;
                        var q = p.Clone();
                        q.skeleton = sk;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        TownModel m;
                        try { m = Generator.Generate(seed, q, check: true); }
                        catch (InvariantException e)
                        {
                            broke++;
                            firstBreak ??= e.Message;
                            continue;
                        }
                        ms += sw.ElapsedMilliseconds;
                        streets.Add(StreetsInside(m) + "#" + BuildingsInside(m));
                        if (Got(m, want)) got++;
                        outsideChanged = Math.Max(outsideChanged, OutsideChanged(baseModel, m));
                    }
                    var was = asked.TryGetValue(name, out var a) ? a : (got: 0, of: 0);
                    asked[name] = (was.got + got, was.of + 3 - broke);
                    string at = $"({Regions.Middle(Probe(baseModel, patch), 0).x:0}, {Regions.Middle(Probe(baseModel, patch), 0).y:0})";
                    string line = $"{town,-15} {at,-11} {patch.Count} blocks  {name,-10}  {streets.Count} kinds, got it {got}/{3 - broke}, broke {broke}/3, buildings changed outside {outsideChanged}, {ms / Math.Max(1, 3 - broke)} ms";
                    bool fail = broke == 3 || (streets.Count < 2 && broke == 0) || outsideChanged > OUTSIDE_SLACK;
                    if (fail) bad++;
                    Console.WriteLine((fail ? "FAIL " : "     ") + line);
                    if (firstBreak != null) Console.WriteLine($"       broke: {firstBreak}");
                }
            }
        }
        foreach (var kv in asked) Console.WriteLine($"{kv.Key,-10} got what it asked for {kv.Value.got} of {kv.Value.of}");
        Console.WriteLine(bad == 0 ? "regions ok" : $"{bad} failures");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Buildings away from the patch (more than this many tiles out) that may change: a re-plan nudges the junctions on its edge.</summary>
    const int OUTSIDE_SLACK = 12;

    /// <summary>A model whose skeleton has just this patch, to ask Regions about it.</summary>
    static TownModel Probe(TownModel m, List<Block> patch)
    {
        var sk = new Skeleton();
        sk.regions.Add(new SkeletonRegion { blocks = patch.Select(b => b.polygon).ToList() });
        var probe = m.With();
        probe.@params = m.@params.Clone();
        probe.@params.skeleton = sk;
        return probe;
    }

    /// <summary>A block a third of the way out towards each side of the town, with the blocks whose middles are within 22 tiles of it.</summary>
    static IEnumerable<List<Block>> Patches(TownModel m)
    {
        var dirs = new[] { (1.0, 0.0), (0.0, 1.0), (-1.0, 0.0), (0.0, -1.0) };
        var used = new HashSet<int>();
        foreach (var (dx, dy) in dirs)
        {
            var target = new Vec2(m.center.x + dx * m.townRadius / 3, m.center.y + dy * m.townRadius / 3);
            var parks = m.ParkBlockSet();
            Block seedBlock = null;
            double best = double.PositiveInfinity;
            foreach (var b in m.blocks)
            {
                double d = Geo.Dist(Geo.PolygonCentroid(b.polygon), target);
                if (d < best && !used.Contains(b.id)) { best = d; seedBlock = b; }
            }
            if (seedBlock == null || best > m.townRadius / 4) continue;
            var mid = Geo.PolygonCentroid(seedBlock.polygon);
            var patch = m.blocks.Where(b => Geo.Dist(Geo.PolygonCentroid(b.polygon), mid) < 22).ToList();
            foreach (var b in patch) used.Add(b.id);
            if (patch.Count >= 2) yield return patch;
        }
    }

    static string StreetsInside(TownModel m)
    {
        var parts = new List<string>();
        foreach (var e in m.roads.edges)
            if (e.kind != RoadKind.Main && Regions.ShareInside(m, 0, e.points) >= 0.5)
                parts.Add(string.Join(";", e.points.Select(q => $"{q.x:0.0},{q.y:0.0}")));
        parts.Sort(StringComparer.Ordinal);
        return string.Join("|", parts);
    }

    static string BuildingsInside(TownModel m) =>
        string.Join("|", m.buildings.Where(b => Regions.At(m, Geo.PolygonCentroid(b.footprint)) == 0).Select(b => $"{b.kind}:{Geo.PolygonCentroid(b.footprint).x:0.0},{Geo.PolygonCentroid(b.footprint).y:0.0}").OrderBy(s => s, StringComparer.Ordinal));

    static bool Got(TownModel m, RegionWant want)
    {
        var inside = m.buildings.Where(b => Regions.At(m, Geo.PolygonCentroid(b.footprint)) == 0).ToList();
        if (want.park && !m.ParkBlockSet().Any(k => Regions.At(m, Geo.PolygonCentroid(m.blocks[k].polygon)) == 0)) return false;
        if (want.school && !inside.Any(b => b.kind == BuildingKind.School)) return false;
        int shops = inside.Count(b => b.kind == BuildingKind.Shop), houses = inside.Count(b => b.kind == BuildingKind.House);
        if (want.shops && !want.houses && shops < houses) return false;
        if (want.houses && !want.shops && shops > 0) return false;
        if (want.shops && want.houses && (shops == 0 || houses == 0)) return false;
        if (want.roundabout && !m.design.nodes.Any(n => n.form == JunctionForm.Roundabout && Near(m, m.roads.nodes[n.node].at))) return false;
        return true;
    }

    static bool Near(TownModel m, Vec2 at)
    {
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
                if (Regions.At(m, new Vec2(at.x + dx, at.y + dy)) == 0) return true;
        return false;
    }

    /// <summary>Buildings well away from the patch (OUTSIDE_SLACK tiles) that aren't where they were, or aren't what they were.</summary>
    static int OutsideChanged(TownModel before, TownModel after)
    {
        var mask = Regions.Mask(after, 0);
        var near = Raster.DistanceFrom(mask, after.width, after.height);
        string Sig(Building b) => $"{b.kind}:{string.Join(";", b.footprint.Select(q => $"{q.x:0.0},{q.y:0.0}"))}";
        bool Away(Building b)
        {
            var c = Geo.PolygonCentroid(b.footprint);
            int x = (int)Math.Floor(c.x), y = (int)Math.Floor(c.y);
            return near[Grid.Index(x, y, after.width)] > OUTSIDE_SLACK;
        }
        var had = new HashSet<string>(before.buildings.Where(Away).Select(Sig));
        var has = new HashSet<string>(after.buildings.Where(Away).Select(Sig));
        // Shops and services are spread over the whole town (Retail): a kind can move; where buildings stand can't.
        string Where(string s) => s.Substring(s.IndexOf(':') + 1);
        var hadAt = new HashSet<string>(had.Select(Where));
        return has.Select(Where).Count(s => !hadAt.Contains(s)) + hadAt.Count(s => !has.Select(Where).Contains(s));
    }
}

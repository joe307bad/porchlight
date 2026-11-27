// The yards' fences keep off the sidewalks and the roads: no fence, as the
// game draws it (Layout.FenceLine), comes within a street's drawn reach (its
// asphalt, parking lanes, sidewalk and the sidewalk's edging) or a
// cul-de-sac's. Measured here from the streets' own lines, not through Kerbs,
// so a mistake there shows. Over the starter towns, and a batch of generated
// ones as the towns check makes them.
//
//   dotnet run --project "unity/Tools~/Checks" -- fences [server/towns] [generated count]

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

static class FenceCheck
{
    /// <summary>How finely each fence is walked along (tiles).</summary>
    const double STEP = 0.01;

    public static int Run(string dir, int generated)
    {
        var fails = new List<string>();
        int towns = 0;
        long fences = 0, cut = 0;
        void Town(string name, TownModel model)
        {
            var layout = Layout.Build(model);
            var (count, trimmed, worst, near) = Measure(model, layout);
            towns++;
            fences += count;
            cut += trimmed;
            bool ok = near == 0;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: {count} fence pieces ({trimmed} cut short), none on a sidewalk or road ({near} too near; nearest {worst:0.000} tiles past the sidewalk's edge)");
            if (!ok) fails.Add(name);
        }
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            var (seed, settings) = TownKey.Parse(JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString());
            Town(Path.GetFileNameWithoutExtension(f), Generator.Generate(seed, settings));
        }
        for (int i = 0; i < generated; i++)
        {
            // The towns check's own towns (Program.Towns).
            string seed = $"check-{i}";
            var p = GenParams.Normalize(new GenParams { width = 64 + (i % 4) * 32, height = 64 + ((i / 4) % 4) * 32, roadDensity = 0.5 + (i % 5) * 0.1, frontSetback = (i / 2) % 4 });
            Town(seed, Generator.Generate(seed, p));
        }
        Console.WriteLine($"{towns - fails.Count}/{towns} towns' fences clear of the sidewalks: {fences} pieces, {cut} cut short");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>A town's fences: how many, how many cut short, the least room any leaves past a sidewalk's edge, and how many come inside one.</summary>
    static (int count, int trimmed, double worst, int near) Measure(TownModel model, IsoLayout layout)
    {
        // Each street's drawn reach from its line: half the asphalt, a parking lane, the sidewalk and its edging.
        var streets = layout.roads.Where(r => r.kind != RoadKind.Path).Select(r => (r.points, reach: r.width / 2 + Layout.PARKING + Kerbs.REACH)).ToList();
        double bulbReach = Raster.BulbRadius(model.@params) + Layout.PARKING + Kerbs.REACH;
        // Only the streets near a fence are looked at.
        double Room(Vec2 p)
        {
            double best = double.PositiveInfinity;
            foreach (var (points, reach) in streets)
                for (int k = 1; k < points.Count; k++)
                {
                    var a = points[k - 1];
                    var b = points[k];
                    if (Math.Max(Math.Abs(p.x - a.x), Math.Abs(p.y - a.y)) > reach + 1 + Geo.Dist(a, b)) continue;
                    best = Math.Min(best, Geo.DistToSegment(p, a, b) - reach);
                }
            foreach (var q in layout.bulbs) best = Math.Min(best, Geo.Dist(p, q.at) - bulbReach);
            return best;
        }
        int trimmed = 0, near = 0;
        double worst = double.PositiveInfinity;
        foreach (var f in layout.fences)
        {
            if (f.cut0 > 0 || f.cut1 > 0) trimmed++;
            var (a, b) = Layout.FenceLine(f);
            int n = Math.Max(1, (int)Math.Ceiling(Geo.Dist(a, b) / STEP));
            double least = double.PositiveInfinity;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                least = Math.Min(least, Room(new Vec2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t)));
            }
            worst = Math.Min(worst, least);
            if (least < 0) near++;
        }
        return (layout.fences.Count, trimmed, worst, near);
    }
}

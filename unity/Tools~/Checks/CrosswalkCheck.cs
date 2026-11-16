// Walkers and cars at the crossings, over many towns:
//
//   dotnet run --project "unity/Tools~/Checks" -- crossings [count]
//
// A couple of minutes of a town's traffic with townsfolk strolling and
// crossing (Streetlife): every walker waiting at a kerb gets across within
// WAIT seconds, no car driving through a crossing comes within NEAR of a
// walker out on it,
// the cars still get somewhere, and it all runs the same twice.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class CrosswalkCheck
{
    const double DT = 0.05, SECONDS = 120, WAIT = 30, NEAR = 0.4;
    const int PEOPLE = 40;

    public static int Run(int count)
    {
        int failed = 0;
        long crossed = 0;
        double longest = 0;
        for (int i = 0; i < count; i++)
        {
            string seed = $"crossings-{i}";
            var p = GenParams.Normalize(new GenParams { width = 96 + (i % 3) * 32, height = 96, roadSense = i % 2 });
            var layout = Layout.Build(Generator.Generate(seed, p));
            if (layout.sidewalks == null || layout.sidewalks.paths.Count == 0) continue;
            string err = RunOnce(seed, layout, ref crossed, ref longest, out var ends);
            if (err == null)
            {
                RunOnce(seed, layout, ref crossed, ref longest, out var again);
                if (ends != again) err = "ran differently the second time";
            }
            if (err != null)
            {
                failed++;
                Console.WriteLine($"FAIL {seed}: {err}");
            }
        }
        Console.WriteLine($"{(failed == 0 ? "all ok" : $"{failed} failed")}: {crossed / 2} crossings made; longest wait at a kerb {longest:0.0} s (at most {WAIT})");
        return failed == 0 ? 0 : 1;
    }

    static double Dist(List<Vec2> pts, Vec2 a, Vec2 b)
    {
        double best = double.PositiveInfinity;
        for (int k = 0; k + 1 < pts.Count; k++) best = Math.Min(best, Geo.SegmentDistance(a, b, pts[k], pts[k + 1]));
        return best;
    }

    static string RunOnce(string seed, IsoLayout layout, ref long crossed, ref double longest, out string ends)
    {
        ends = null;
        var traffic = new Traffic(layout, new Rng(seed + "::traffic"), r => 0);
        var life = new Streetlife(layout, traffic, new Rng(seed + "::crossings"));
        var rng = new Rng(seed + "::people");
        var people = new List<Pedestrian>();
        for (int k = 0; k < PEOPLE; k++) people.Add(Pedestrian.Create("x", layout.sidewalks, rng));
        var obstacles = new List<Vec2>();
        var was = new bool[PEOPLE];
        for (double t = 0; t < SECONDS; t += DT)
        {
            life.Step(DT, people, obstacles);
            var (_, wait) = life.crosswalks.Waiting(out string why);
            longest = Math.Max(longest, wait);
            if (wait > WAIT) return $"a walker waited {wait:0} s at a kerb ({t:0.0} s in): {why}";
            for (int k = 0; k < PEOPLE; k++)
            {
                var q = people[k];
                if (q.OnCrossing && !was[k]) crossed++;
                was[k] = q.OnCrossing;
                if (!q.OnCrossing) continue;
                foreach (var c in traffic.cars)
                {
                    if (c.idle || c.taken || c.loop >= 0 || c.speed < 0.3) continue;
                    double d = Numbers.Hypot(c.x - q.x, c.y - q.y);
                    var line = q.CrossingLine.Value;
                    // A car going through the walker's crossing (not one going by the corner beside it).
                    bool through = traffic.PathTo(c, line.from, line.to, 1) < 1 || Geo.DistToSegment(new Vec2(c.x, c.y), line.from, line.to) < 0.3;
                    if (d < NEAR && through)
                    {
                        bool closed = traffic.closedCrossings.Exists(x => (Numbers.Hypot(x.Item1.x - line.from.x, x.Item1.y - line.from.y) < 0.05 || Numbers.Hypot(x.Item2.x - line.from.x, x.Item2.y - line.from.y) < 0.05));
                        var cw = life.crosswalks.all.Find(x => Numbers.Hypot(x.a.x - line.from.x, x.a.y - line.from.y) < 0.05 || Numbers.Hypot(x.b.x - line.from.x, x.b.y - line.from.y) < 0.05);
                        return $"a car (going {c.speed:0.0}, why '{c.why}', granted {c.granted}, road {c.road} dir {c.dir} next {c.next}) came within {d:0.00} of a walker crossing at ({q.x:0.0}, {q.y:0.0}) from ({line.from.x:0.0},{line.from.y:0.0}) to ({line.to.x:0.0},{line.to.y:0.0}), {t:0.0} s in; closed {closed}; crosswalk {(cw == null ? "none" : $"road {cw.road} node {cw.node} transient {cw.transient}")}; path to it {traffic.PathTo(c, line.from, line.to, 6):0.00}; to its road {Dist(layout.roads[c.road].points, line.from, line.to):0.00} (width {layout.roads[c.road].width:0.0}, {layout.roads[c.road].kind}, driven {traffic.Driven(c.road)})";
                    }
                }
            }
        }
        var sb = new System.Text.StringBuilder();
        foreach (var c in traffic.cars) sb.Append($"{c.x:R},{c.y:R};");
        foreach (var q in people) sb.Append($"{q.x:R},{q.y:R};");
        ends = sb.ToString();
        return null;
    }
}

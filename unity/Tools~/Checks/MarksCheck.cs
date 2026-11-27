// The street markings (Shared/Explore/Markings.cs) keep to their rules, town by town:
//
//   dotnet run -c Release --project "unity/Tools~/Checks" -- marks [server/towns] [random towns]
//
// Every line painted on a street, looked at every 0.05 tiles:
//   - it's on its own street (no further from the middle than the street's half width);
//   - it's not on any other road's carriageway (past its half width and Markings.CLEAR), so no line runs out across
//     a junction, however the roads meet;
//   - it's not on a roundabout's ring or in a cul-de-sac's bulb;
//   - an edge line is at the street's edge (not folded back into it on the inside of a tight bend);
//   - a dash is nearly straight (not curled round a bend, where it reads as a turn arrow).
// Over the starter towns, a town whose junction broke these (Red town's High Street at Fox Road: High Street turns
// a right angle a tile and a half past the junction), and random towns big and small.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;
using Porchlight.Life;

static class MarksCheck
{
    /// <summary>A town whose High Street at Fox Road drew a looped edge line and a curled dash.</summary>
    const string RED_TOWN = "seed=g3gyzs&width=256&height=256&waterFraction=0.1&townRadius=0.5&roadDensity=1&buildingDensity=1";

    /// <summary>Slack for the lines' own sampling and for floating point (tiles).</summary>
    const double TOLERANCE = 0.03;

    public static int Run(string dir, int random)
    {
        var towns = new List<(string name, string seed, GenParams p)>();
        void Add(string name, string key)
        {
            var (seed, p) = TownKey.Parse(key);
            towns.Add((name, seed, p));
        }
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
            Add(Path.GetFileNameWithoutExtension(f), JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString());
        Add("red-town", RED_TOWN);
        for (int i = 0; i < random; i++)
        {
            // Small and big, sparse and dense, as the towns check grows them, and some at full size.
            var p = i % 2 == 0
                ? new GenParams { width = 64 + (i % 4) * 32, height = 64 + ((i / 4) % 4) * 32, roadDensity = 0.5 + (i % 5) * 0.1, frontSetback = (i / 2) % 4 }
                : new GenParams { width = 256, height = 256, roadDensity = 0.8 + (i % 3) * 0.1, waterFraction = (i % 4) * 0.05 };
            towns.Add(($"random-{i}", $"marks-{i}", GenParams.Normalize(p)));
        }
        int bad = 0;
        long marks = 0;
        foreach (var (name, seed, p) in towns)
        {
            var model = Generator.Generate(seed, p);
            var layout = Layout.Build(model);
            var plan = Markings.Plan(model, layout);
            marks += plan.Count;
            var faults = Faults(model, layout, plan);
            if (faults.Count == 0)
            {
                Console.WriteLine($"     {name,-16} {plan.Count} marks ok");
                continue;
            }
            bad++;
            Console.WriteLine($"FAIL {name,-16} {faults.Count} mark(s) break the rules; the first:");
            foreach (var f in faults.Take(5)) Console.WriteLine($"       {f}");
        }
        Console.WriteLine(bad == 0 ? $"marks: all ok ({towns.Count} towns, {marks} marks)" : $"marks: {bad} of {towns.Count} towns fail");
        return bad == 0 ? 0 : 1;
    }

    static List<string> Faults(TownModel model, IsoLayout layout, List<Mark> plan)
    {
        var faults = new List<string>();
        var streets = new List<int>();
        for (int i = 0; i < layout.roads.Count; i++) if (layout.roads[i].kind != RoadKind.Path && layout.roads[i].points.Count > 1) streets.Add(i);
        // Each street's box, out to its carriageway and the clearance: a mark's point only looks at the streets near it.
        var boxes = new Dictionary<int, (double x0, double y0, double x1, double y1)>();
        foreach (int i in streets)
        {
            var r = layout.roads[i];
            double reach = r.width / 2 + Markings.CLEAR;
            boxes[i] = (r.points.Min(q => q.x) - reach, r.points.Min(q => q.y) - reach, r.points.Max(q => q.x) + reach, r.points.Max(q => q.y) + reach);
        }
        var rings = Markings.Rings(model, layout);
        double bulb = Raster.BulbRadius(model.@params);
        var bulbs = layout.bulbs.Select(q => q.atEnd ? layout.roads[q.edge].points[layout.roads[q.edge].points.Count - 1] : layout.roads[q.edge].points[0]).ToList();
        foreach (var mk in plan)
        {
            var own = layout.roads[mk.road];
            string Where(Vec2 q) => $"{mk.kind} of road {mk.road} at ({q.x:0.00}, {q.y:0.00})";
            if (mk.kind == MarkKind.CentreDash || mk.kind == MarkKind.LaneDash)
                if (!Markings.Straight(mk.points)) faults.Add($"{Where(mk.points[0])} curls round a bend");
            foreach (var q in Geo.SamplePolyline(mk.points, 0.05))
            {
                double mine = Geo.DistToPolyline(q, own.points);
                if (mine > own.width / 2 + mk.width / 2 + TOLERANCE)
                {
                    faults.Add($"{Where(q)} is off its street ({mine:0.00} from its middle, half width {own.width / 2:0.00})");
                    break;
                }
                if (mk.kind == MarkKind.EdgeLine && mine < own.width / 2 - Markings.ON_EDGE - TOLERANCE)
                {
                    faults.Add($"{Where(q)} is inside its street, not at its edge ({mine:0.00} from its middle)");
                    break;
                }
                string across = null;
                foreach (int s in streets)
                {
                    if (s == mk.road) continue;
                    var b = boxes[s];
                    if (q.x < b.x0 || q.x > b.x1 || q.y < b.y0 || q.y > b.y1) continue;
                    var other = layout.roads[s];
                    double d = Geo.DistToPolyline(q, other.points);
                    if (d < other.width / 2 + Markings.CLEAR - TOLERANCE)
                    {
                        across = $"{Where(q)} is on road {s}'s carriageway ({d:0.00} from its middle, half width {other.width / 2:0.00})";
                        break;
                    }
                }
                if (across != null)
                {
                    faults.Add(across);
                    break;
                }
                var ring = rings.FirstOrDefault(g => Geo.Dist(q, g.at) < g.radius + Markings.RING_OUT - TOLERANCE);
                if (ring.radius > 0)
                {
                    faults.Add($"{Where(q)} is on a roundabout's ring");
                    break;
                }
                if (bulbs.Any(c => Geo.Dist(q, c) < bulb - TOLERANCE))
                {
                    faults.Add($"{Where(q)} is in a cul-de-sac's bulb");
                    break;
                }
            }
        }
        return faults;
    }
}

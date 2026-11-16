// RoadSense's repairs and classing, over many towns:
//
//   dotnet run --project "unity/Tools~/Checks" -- roadsense [count]
//
// Each town is grown twice, without the repair and with it: the repaired one
// passes every check the streets do, repairing it again changes nothing (the
// repair is a fixed point), and the defects it's for (junctions joined by a
// road shorter than RoadSense.JOIN, legs meeting at under 60°) are fewer or no
// more than before. Prints what it classed: how many roads of each class and
// junctions under each control.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

static class RoadSenseCheck
{
    public static int Run(int count)
    {
        int failed = 0;
        long closeBefore = 0, closeAfter = 0, sharpBefore = 0, sharpAfter = 0, repaired = 0;
        var classes = new Dictionary<RoadClass, int>();
        var controls = new Dictionary<JunctionControl, int>();
        int roundabouts = 0;
        for (int i = 0; i < count; i++)
        {
            string seed = $"roadsense-{i}";
            var p = new GenParams { width = 64 + (i % 4) * 32, height = 64 + ((i / 4) % 4) * 32, roadDensity = 0.5 + (i % 5) * 0.1, riverCount = i % 3 };
            p = GenParams.Normalize(p);
            var on = p.Clone();
            on.roadSense = 1;
            try
            {
                var before = Generator.Generate(seed, p, check: true, until: "roadsense");
                var after = Generator.Generate(seed, on, check: true, until: "roadsense");
                if (!SameGraph(before.roads, after.roads)) repaired++;
                var (cb, sb) = Defects(before.roads);
                var (ca, sa) = Defects(after.roads);
                closeBefore += cb;
                closeAfter += ca;
                sharpBefore += sb;
                sharpAfter += sa;
                if (ca > cb) Fail(ref failed, seed, $"more close junctions after the repair ({cb} → {ca})");
                if (sa > sb) Fail(ref failed, seed, $"more sharp legs after the repair ({sb} → {sa})");
                // A fixed point: repairing the repaired town changes nothing.
                var again = new RoadSenseStage().Run(after, Rng.Stage(seed, "roadsense"));
                if (!SameGraph(after.roads, again.roads)) Fail(ref failed, seed, "repairing again changed the roads");
                if (!SameClasses(after.roads, again.roads)) Fail(ref failed, seed, "classing again changed the classes");
                // And the whole town still grows, every check passing.
                Generator.Generate(seed, on, check: true);
                foreach (var e in after.roads.edges) if (e.kind != RoadKind.Path) classes[e.cls] = (classes.TryGetValue(e.cls, out int c) ? c : 0) + 1;
                foreach (var n in after.roads.nodes)
                {
                    controls[n.control] = (controls.TryGetValue(n.control, out int c) ? c : 0) + 1;
                    if (n.roundabout) roundabouts++;
                }
            }
            catch (InvariantException ex)
            {
                Fail(ref failed, seed, ex.Message);
            }
        }
        Console.WriteLine($"{count - failed}/{count} towns ok; {repaired} repaired. Junctions closer than {RoadSenseStage.JOIN} tiles: {closeBefore} → {closeAfter}; legs under 60°: {sharpBefore} → {sharpAfter}");
        Console.WriteLine("Roads: " + string.Join(", ", Enum.GetValues(typeof(RoadClass)).Cast<RoadClass>().Select(c => $"{c} {(classes.TryGetValue(c, out int n) ? n : 0)}")));
        Console.WriteLine("Junctions: " + string.Join(", ", Enum.GetValues(typeof(JunctionControl)).Cast<JunctionControl>().Select(c => $"{c} {(controls.TryGetValue(c, out int n) ? n : 0)}")) + $"; {roundabouts} would take a roundabout");
        return failed == 0 ? 0 : 1;
    }

    static void Fail(ref int failed, string seed, string what)
    {
        failed++;
        Console.WriteLine($"FAIL {seed}: {what}");
    }

    /// <summary>Junction pairs joined by a road shorter than JOIN, and legs meeting under 60°, at junctions of three legs or more.</summary>
    public static (int close, int sharp) Defects(RoadGraph g)
    {
        var legs = RoadSenseStage.LegCounts(g);
        int close = 0, sharp = 0;
        foreach (var e in g.edges)
        {
            if (e.kind == RoadKind.Path || e.from == e.to || legs[e.from] < 3 || legs[e.to] < 3) continue;
            if (Geo.PolylineLength(e.points) < RoadSenseStage.JOIN) close++;
        }
        foreach (var n in g.nodes)
        {
            if (legs[n.id] < 3) continue;
            var dirs = new List<Vec2>();
            foreach (var e in g.edges)
            {
                if (e.kind == RoadKind.Path) continue;
                if (e.from == n.id) dirs.Add(RoadSenseStage.Heading(n.at, e.points));
                if (e.to == n.id)
                {
                    var r = new List<Vec2>(e.points);
                    r.Reverse();
                    dirs.Add(RoadSenseStage.Heading(n.at, r));
                }
            }
            for (int i = 0; i < dirs.Count; i++)
                for (int j = i + 1; j < dirs.Count; j++)
                    if (DetMath.Acos(Math.Max(-1, Math.Min(1, Geo.Dot(dirs[i], dirs[j])))) < 60 * Math.PI / 180) sharp++;
        }
        return (close, sharp);
    }

    static bool SameGraph(RoadGraph a, RoadGraph b)
    {
        if (a.nodes.Count != b.nodes.Count || a.edges.Count != b.edges.Count) return false;
        for (int i = 0; i < a.nodes.Count; i++) if (Geo.Dist(a.nodes[i].at, b.nodes[i].at) > 1e-9) return false;
        for (int i = 0; i < a.edges.Count; i++)
        {
            var x = a.edges[i];
            var y = b.edges[i];
            if (x.from != y.from || x.to != y.to || x.kind != y.kind || x.points.Count != y.points.Count) return false;
            for (int k = 0; k < x.points.Count; k++) if (Geo.Dist(x.points[k], y.points[k]) > 1e-9) return false;
        }
        return true;
    }

    static bool SameClasses(RoadGraph a, RoadGraph b)
    {
        for (int i = 0; i < a.edges.Count; i++) if (a.edges[i].cls != b.edges[i].cls || a.edges[i].priority != b.edges[i].priority) return false;
        for (int i = 0; i < a.nodes.Count; i++) if (a.nodes[i].control != b.nodes[i].control) return false;
        return true;
    }
}

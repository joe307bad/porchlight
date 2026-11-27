// The junction design (docs/street-editor.md §5, Gen/Design.cs), town by town:
//
//   dotnet run -c Release --project "unity/Tools~/Checks" -- design [server/towns]
//
// How long each stage takes (the street editor re-plans from the roads on), and what the design gave each town:
// roads with two lanes each way, divided roads, relief routes, roundabouts, pockets, protected lefts, slip roads,
// signals, the busiest road's v/c and the notes. Fails a town whose design breaks its own rules (DesignStage.Check),
// or that comes out differently from the same key twice.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

static class DesignCheck
{
    public static int Run(string dir)
    {
        int bad = 0;
        var stageMs = new Dictionary<string, double>();
        Console.WriteLine("town             lanes2  divided  relief  roundabouts  pockets(L/R)  protected  slips  signals  worst v/c  notes  ms");
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            var key = JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString();
            var (seed, p) = TownKey.Parse(key);
            var model = TownModel.CreateEmpty(seed, GenParams.Normalize(p));
            var total = Stopwatch.StartNew();
            foreach (var stage in Generator.Stages)
            {
                var sw = Stopwatch.StartNew();
                model = stage.Run(model, Rng.Stage(seed, stage.Name));
                stageMs[stage.Name] = (stageMs.TryGetValue(stage.Name, out var was) ? was : 0) + sw.Elapsed.TotalMilliseconds;
                var v = stage.Check(model);
                if (v.Count > 0)
                {
                    bad++;
                    Console.WriteLine($"FAIL {Path.GetFileNameWithoutExtension(f)} {stage.Name}: {string.Join("; ", v.Take(3))}");
                }
            }
            long ms = total.ElapsedMilliseconds;
            var d = model.design;
            int lanes2 = d.edges.Count(e => Math.Max(e.lanesFwd, e.lanesBack) >= 2), divided = d.edges.Count(e => e.divided), relief = d.edges.Count(e => e.relief);
            int rb = d.nodes.Count(n => n.form == JunctionForm.Roundabout), pl = d.edges.Sum(e => e.pockets.Count(q => q.left)), pr = d.edges.Sum(e => e.pockets.Count(q => !q.left));
            int prot = d.nodes.Count(n => n.protectedLeft), slips = d.nodes.Sum(n => n.slips.Count), signals = d.nodes.Count(n => n.control == DesignControl.Signal);
            double worst = d.edges.Count == 0 ? 0 : d.edges.Max(e => e.vc);
            Console.WriteLine($"{Path.GetFileNameWithoutExtension(f),-16} {lanes2,6}  {divided,7}  {relief,6}  {rb,11}  {pl,6}/{pr,-5}  {prot,9}  {slips,5}  {signals,7}  {worst,9:0.00}  {d.notes.Count,5}  {ms}");
            foreach (var n in d.notes.Take(4)) Console.WriteLine($"    note ({n.at.x:0}, {n.at.y:0}): {n.text}");
            var again = Generator.Generate(seed, p);
            if (Fingerprint(again) != Fingerprint(model))
            {
                bad++;
                Console.WriteLine($"FAIL {Path.GetFileNameWithoutExtension(f)}: not the same twice");
            }
            // The life still runs on it: the road network builds, every lane goes somewhere.
            var net = new RoadNet(model, Porchlight.Explore.Layout.Build(model));
            if (net.lanes.Count == 0) { bad++; Console.WriteLine($"FAIL {Path.GetFileNameWithoutExtension(f)}: no lanes"); }
        }
        Console.WriteLine("stage ms (all towns): " + string.Join(", ", stageMs.Select(kv => $"{kv.Key} {kv.Value:0}")));
        Console.WriteLine(bad == 0 ? "design ok" : $"{bad} failures");
        return bad == 0 ? 0 : 1;
    }

    static string Fingerprint(TownModel m)
    {
        var d = m.design;
        var parts = new List<string>();
        foreach (var e in d.edges) parts.Add($"{e.lanesFwd}{e.lanesBack}{(e.divided ? 1 : 0)}{e.pockets.Count}{e.width:0.00}");
        foreach (var n in d.nodes) parts.Add($"{(int)n.form}{(int)n.control}{n.slips.Count}");
        parts.Add(m.buildings.Count.ToString());
        return string.Join(",", parts);
    }
}

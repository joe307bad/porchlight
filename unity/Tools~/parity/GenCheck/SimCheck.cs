// Runs the same simulations as ../sim.ts and compares (to 1e-9, since the
// trig functions underneath may differ in the last bit).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class SimCheck
{
    static JsonArray StepsJson(List<Step> steps)
    {
        var a = new JsonArray();
        foreach (var s in steps) a.Add(new JsonArray(KindName(s.kind), s.text, s.distance));
        return a;
    }

    static string KindName(StepKind k) => k switch
    {
        StepKind.SlightLeft => "slight-left",
        StepKind.SlightRight => "slight-right",
        _ => k.ToString().ToLowerInvariant(),
    };

    public static int Run(string path)
    {
        var expected = JsonNode.Parse(File.ReadAllText(path)).AsArray();
        int bad = 0;
        foreach (var e in expected)
        {
            var seed = (string)e["seed"];
            var m = Generator.Generate(seed, new GenParams());
            var layout = Layout.Build(m);
            var names = StreetNaming.Name(m, layout.roads);
            var traffic = new Traffic(layout, new Rng($"{seed}::traffic"), Catalog.PickCarModel);
            var rng = new Rng($"{seed}::townsfolk");
            var people = new List<Pedestrian>();
            for (int i = 0; i < 12; i++) people.Add(Pedestrian.Create(Catalog.RandomLook(rng), layout.sidewalks, rng));
            var autoRng = new Rng("auto-walk");
            var player = new Player(Catalog.RandomLook(autoRng), m.spawn.x, m.spawn.y, 1.05);
            player.StartAuto(layout.sidewalks, autoRng);
            var open = Streets.Mask(m.tiles);
            var canDrive = Vehicle.Drivable(m, layout);
            var spot = Vehicle.CarSpotNear(m.spawn, layout.roads, 0.45, canDrive, new List<Vec2>()).Value;
            var car = new Vehicle(1, spot.x, spot.y, spot.heading);
            var auto = new AutoDrive(layout.roads, new Rng("auto-drive"), car);
            var steps = new JsonArray();
            for (int i = 0; i < 400; i++)
            {
                double dt = 0.05;
                traffic.Update(dt, new List<Vec2> { new Vec2(player.x, player.y) });
                foreach (var p in people) p.Update(dt);
                player.Update(null, dt, m, open);
                auto.Step(car, dt, traffic.Positions().Select(c => new Vec2(c.x, c.y)).ToList());
                if (i % 50 == 49)
                {
                    var it = player.AutoItinerary();
                    var cars = new JsonArray();
                    foreach (var c in traffic.Positions()) cars.Add(new JsonObject { ["x"] = c.x, ["y"] = c.y, ["heading"] = c.heading });
                    var ppl = new JsonArray();
                    foreach (var p in people) ppl.Add(new JsonObject { ["x"] = p.x, ["y"] = p.y, ["heading"] = p.heading, ["speed"] = p.speed });
                    steps.Add(new JsonObject
                    {
                        ["cars"] = cars,
                        ["people"] = ppl,
                        ["player"] = new JsonObject { ["x"] = player.x, ["y"] = player.y, ["heading"] = player.heading },
                        ["route"] = Snapshot.Pts(player.AutoRoute()),
                        ["walk"] = it != null ? StepsJson(Directions.ForWalk(layout.sidewalks, names, it)) : null,
                        ["car"] = new JsonObject { ["x"] = car.x, ["y"] = car.y, ["heading"] = car.heading, ["speed"] = car.speed },
                        ["drive"] = StepsJson(Directions.ForDrive(names, auto.Stretches())),
                        ["driveRoute"] = Snapshot.Pts(auto.RoutePoints()),
                    });
                }
            }
            var manual = new Vehicle(0, spot.x, spot.y, spot.heading);
            var manualPath = new JsonArray();
            for (int i = 0; i < 300; i++)
            {
                manual.Drive(i % 90 < 60 ? 1 : -1, Math.Sin(i / 20.0), 0.05, canDrive, i % 120 < 40);
                if (i % 30 == 29) manualPath.Add(new JsonObject { ["x"] = manual.x, ["y"] = manual.y, ["heading"] = manual.heading, ["speed"] = manual.speed });
            }
            var diffs = new List<string>();
            Near("steps", e["steps"], steps, diffs);
            Near("manual", e["manual"], manualPath, diffs);
            Console.WriteLine(diffs.Count == 0 ? $"{seed}: same" : $"{seed}: {diffs.Count} differences, first: {string.Join(" | ", diffs.Take(3))}");
            if (diffs.Count > 0) bad++;
        }
        return bad == 0 ? 0 : 1;
    }

    static void Near(string path, JsonNode a, JsonNode b, List<string> diffs)
    {
        if (diffs.Count > 10) return;
        if (a == null || b == null)
        {
            if (a != null || b != null) diffs.Add($"{path}: {a?.ToJsonString() ?? "null"} vs {b?.ToJsonString() ?? "null"}");
            return;
        }
        if (a is JsonArray aa && b is JsonArray ba)
        {
            if (aa.Count != ba.Count) diffs.Add($"{path}: length {aa.Count} vs {ba.Count}");
            for (int i = 0; i < Math.Min(aa.Count, ba.Count); i++) Near($"{path}[{i}]", aa[i], ba[i], diffs);
            return;
        }
        if (a is JsonObject ao && b is JsonObject bo)
        {
            foreach (var kv in ao) Near($"{path}.{kv.Key}", kv.Value, bo[kv.Key], diffs);
            return;
        }
        if (a.AsValue().TryGetValue(out double ad) && b.AsValue().TryGetValue(out double bd))
        {
            if (Math.Abs(ad - bd) > 1e-9 * Math.Max(1, Math.Abs(ad))) diffs.Add($"{path}: {ad:R} vs {bd:R}");
            return;
        }
        if (a.ToJsonString() != b.ToJsonString()) diffs.Add($"{path}: {a.ToJsonString()} vs {b.ToJsonString()}");
    }
}

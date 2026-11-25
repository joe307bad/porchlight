// Driving into a town at speed, the view switches into it (ExploreView.SwitchSpace) in the one frame
// you cross its edge, so whatever's done there is a frame the game stalls. Porchlight's street life made
// there (Traffic: its road network, router and everyone's day; then its cars, signs and stalls) was
// most of a second. Now it's made ahead (CountyDrive, StreetLife): the traffic on a worker thread,
// started there, and the cars, signs and stalls kept for coming back, bound to a fresh traffic of the
// same town. This checks what that rests on, for each starter town:
//
//   - two traffics of the same town have the same cars in the same slots (Fleet.Bind), the same signal
//     posts at the same junctions (Signage.Bind) and the same market stalls (MarketStalls.Bind);
//   - catching a traffic started ahead up to the time now, a frame's share (ExploreView.CATCH_UP_MS),
//     a few ms of work a frame (Traffic.CatchUp), never minutes of it at once: a frame's no longer than
//     CATCH_UP_LIMIT. A big town has single moments of 20-30 ms either way (a crowd of trips planned at
//     once), so it's printed beside its slowest frame at the time now, not held to it.
//
// And it prints what each part costs, made all at once (as the crossing frame used to).
//
//   dotnet run --project "unity/Tools~/Checks" -c Release -- arrival [towns dir]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

static class ArrivalCheck
{
    /// <summary>ExploreView.CATCH_UP_MS: the town's life caught up this much work (ms) a frame (Traffic.CatchUp).</summary>
    const double CATCH_UP_MS = 4;
    /// <summary>The longest a frame of catching up may take (ms): all at once, 5 minutes of a big town's life is about 300.</summary>
    const double CATCH_UP_LIMIT = 100;

    static Traffic Make(TownModel model, IsoLayout layout) =>
        new Traffic(model, layout, null, new Rng($"{model.seed}::traffic"), Catalog.PickCarModel);

    public static int Run(string dir)
    {
        int fails = 0;
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            string key = JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString();
            var (seed, p) = TownKey.Parse(key);
            var model = Generator.Generate(seed, p);
            var layout = Layout.Build(model);
            _ = Make(model, layout); // the JIT paid for
            var sw = Stopwatch.StartNew();
            var a = Make(model, layout);
            double make = sw.Elapsed.TotalMilliseconds;
            var b = Make(model, layout);

            // The same town's twice over: what binding the kept cars, signs and stalls to a fresh one needs.
            if (a.cars.Count != b.cars.Count) { Console.WriteLine($"FAIL {name}: {a.cars.Count} cars, then {b.cars.Count}"); fails++; }
            else
                for (int i = 0; i < a.cars.Count; i++)
                    if (a.cars[i].model != b.cars[i].model) { Console.WriteLine($"FAIL {name}: car slot {i} is model {a.cars[i].model}, then {b.cars[i].model}"); fails++; break; }
            var sa = StreetFurniture.Signals(a.life.net).ToList();
            var sb = StreetFurniture.Signals(b.life.net).ToList();
            if (sa.Count != sb.Count || sa.Zip(sb, (x, y) => x.node == y.node && x.stage == y.stage && x.light == y.light).Any(same => !same))
            { Console.WriteLine($"FAIL {name}: the signal posts aren't the same twice ({sa.Count}, then {sb.Count})"); fails++; }
            if (sa.Any(s => s.node < 0 || s.node >= b.life.net.junctions.Count))
            { Console.WriteLine($"FAIL {name}: a signal post's junction isn't in a fresh road network"); fails++; }
            int stallsA = a.life.market?.stalls.Count ?? 0, stallsB = b.life.market?.stalls.Count ?? 0;
            if (stallsA != stallsB) { Console.WriteLine($"FAIL {name}: {stallsA} market stalls, then {stallsB}"); fails++; }

            // Started ahead, as on the worker thread, a few minutes before you drive in; then caught up a frame's share at a time.
            long now = TownClock.Now();
            sw.Restart();
            a.StartAt(now - 5 * TownClock.MINUTE);
            double start = sw.Elapsed.TotalMilliseconds;
            double worst = 0;
            int frames = 0;
            var none = new List<Vec2>();
            while (a.Clock < now)
            {
                sw.Restart();
                a.CatchUp(now, CATCH_UP_MS, none, 0.016);
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                frames++;
            }
            // Then five minutes of it at the time now, a frame at a time: its own slowest frame.
            double normal = 0;
            for (int k = 0; k < 3000; k++)
            {
                sw.Restart();
                a.Update(0.1, none);
                normal = Math.Max(normal, sw.Elapsed.TotalMilliseconds);
            }
            if (worst > CATCH_UP_LIMIT) { Console.WriteLine($"FAIL {name}: a frame's share of catching up took {worst:0} ms, its slowest frame at the time now {normal:0} ms"); fails++; }
            Console.WriteLine($"{name}: traffic made in {make:0} ms and started in {start:0} ms (on a worker thread now), {a.cars.Count} cars, {sa.Count} signal posts, {stallsA} stalls; 5 minutes caught up in {frames} frames, the longest {worst:0.0} ms (its slowest frame after, {normal:0.0} ms)");
        }
        Console.WriteLine(fails == 0 ? "arrival: all ok" : $"arrival: {fails} failure(s)");
        return fails == 0 ? 0 : 1;
    }
}

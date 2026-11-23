// The junctions' traffic signals and stop signs (Shared/Life/StreetFurniture.cs),
// in each starter town: a signal post at the stop line of every lane into every
// signal, a stop sign at every lane that stops, each on the kerb to the driver's
// right (so the game never has to mirror a model), clear of the lane and facing
// back down it; and the lights' timing: exactly one stage green at a time, its
// last three seconds amber, and green only while the cars may go.
//
//   dotnet run --project "unity/Tools~/Checks" -- furniture [server/towns]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;

static class FurnitureCheck
{
    public static int Run(string dir)
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }
        int allSignals = 0, allStops = 0;
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var (seed, settings) = TownKey.Parse(JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString());
            var model = Generator.Generate(seed, settings);
            var layout = Layout.Build(model);
            var net = new RoadNet(model, layout);
            var signals = StreetFurniture.Signals(net);
            var stops = StreetFurniture.Stops(net);
            allSignals += signals.Count;
            allStops += stops.Count;

            // One post a lane in: as many as the signals' movements have lanes they come from.
            int lanesIn = net.junctions.Values.Where(j => j.control == Control.Signal).Sum(j => j.movements.Select(m => net.movements[m].from).Distinct().Count());
            int lanesStopping = net.junctions.Values.Where(j => j.control == Control.Stop || j.control == Control.AllWayStop)
                .Sum(j => j.movements.Select(m => net.movements[m]).Where(m => m.stops).Select(m => m.from).Distinct().Count());
            Expect(signals.Count == lanesIn, $"{name}: a signal at each of the {lanesIn} lanes into its signals ({signals.Count})");
            Expect(stops.Count == lanesStopping, $"{name}: a stop sign at each of the {lanesStopping} lanes that stop ({stops.Count})");

            // On the kerb to the right of the cars, square to them, and clear of their lane.
            var all = signals.Select(s => (s.at, s.along, s.inward)).Concat(stops.Select(s => (s.at, s.along, s.inward))).ToList();
            int left = all.Count(p => p.inward.y * p.along.x - p.inward.x * p.along.y > 0);
            int askew = all.Count(p => Math.Abs(p.inward.x * p.along.x + p.inward.y * p.along.y) > 1e-6 || Math.Abs(Math.Sqrt(p.inward.x * p.inward.x + p.inward.y * p.inward.y) - 1) > 1e-6);
            Expect(left == 0, $"{name}: every post on the kerb to the drivers' right ({left} on the left)");
            Expect(askew == 0, $"{name}: every post looks square back down its lane ({askew} askew)");
            int onLane = signals.Count(s => s.reach < 0.5);
            Expect(onLane == 0, $"{name}: every signal's pole stands clear of its lane, its arm reaching over it ({onLane} too close)");
        }
        Expect(allSignals > 0, $"the starter towns have traffic signals ({allSignals} posts)");
        Expect(allStops > 0, $"the starter towns have stop signs ({allStops})");

        // The timing, over a whole cycle a second at a time, at a junction with an offset.
        var j = new Junction { offset = 12_345 };
        int[,] seconds = new int[2, 3];
        bool oneGreen = true;
        long t0 = 1_800_000_000_000;
        for (long t = t0; t < t0 + 2 * RoadNet.STAGE_MS; t += 1000)
        {
            int a = StreetFurniture.Showing(j, 0, t), b = StreetFurniture.Showing(j, 1, t);
            seconds[0, a]++;
            seconds[1, b]++;
            if ((a == 0) == (b == 0)) oneGreen = false;
            // Not red just when the cars on that stage may go (RoadNet.Stage, which the cars obey).
            if ((a != 0) != (RoadNet.Stage(j, t).stage == 0)) oneGreen = false;
        }
        Expect(oneGreen, "one stage at a time shows green or amber: the one the cars go on");
        long amber = StreetFurniture.AMBER_MS / 1000, stage = RoadNet.STAGE_MS / 1000;
        for (int s = 0; s < 2; s++)
            Expect(seconds[s, 0] == stage && seconds[s, 1] == amber && seconds[s, 2] == stage - amber,
                $"stage {s}: {seconds[s, 2]} s green, {seconds[s, 1]} s amber, {seconds[s, 0]} s red a cycle");

        Console.WriteLine(fails.Count == 0 ? "furniture: all ok" : $"furniture: {fails.Count} failed");
        return fails.Count == 0 ? 0 : 1;
    }
}

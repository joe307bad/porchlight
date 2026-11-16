// A town's traffic settings do what they say, over many towns:
//
//   dotnet run --project "unity/Tools~/Checks" -- settings [count]
//
// Edits (TrafficEdits, the operations the room checks too) apply, refuse what's
// out of bounds, and survive the trip through JSON. Then the town's busiest
// street is closed (as a road setting, or a timed closure: the marathon's
// kind of event) and, once the cars already on it have gone, no car drives
// onto it; the rest of the town keeps moving. Closure over, it's driven again.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class TrafficSettingsCheck
{
    const double DT = 0.05;

    public static int Run(int count)
    {
        int failed = 0;
        string e = Edits();
        if (e != null)
        {
            failed++;
            Console.WriteLine("FAIL edits: " + e);
        }
        int rerouted = 0;
        for (int i = 0; i < count; i++)
        {
            string seed = $"settings-{i}";
            var layout = Layout.Build(Generator.Generate(seed, GenParams.Normalize(new GenParams { width = 112, height = 112, roadSense = i % 2 })));
            string err = Closure(seed, layout, i % 2 == 1, ref rerouted);
            if (err == null) continue;
            failed++;
            Console.WriteLine($"FAIL {seed}: {err}");
        }
        Console.WriteLine($"{(failed == 0 ? "all ok" : $"{failed} failed")}: {rerouted} towns kept off a closed street, and back on it once open");
        return failed == 0 ? 0 : 1;
    }

    static string Edits()
    {
        var c = new TrafficConfig();
        string Do(Dictionary<string, object> op) => TrafficEdits.Apply(c, op);
        if (Do(TrafficEdits.Preset("busy")) != null || c.preset != "busy") return "preset";
        if (Do(TrafficEdits.Global("density", 1.5)) != null || c.density != 1.5) return "global";
        if (Do(TrafficEdits.Global("density", 9)) == null || c.density != 1.5) return "density out of bounds taken";
        if (Do(TrafficEdits.Global("density", double.NaN)) != null || !double.IsNaN(c.density)) return "global back to auto";
        if (Do(TrafficEdits.Flag("nightFlash", false)) != null || c.nightFlash != false) return "flag";
        if (Do(TrafficEdits.Control("j40_44", JunctionControl.AllWayStop)) != null || c.Junction("j40_44")?.control != JunctionControl.AllWayStop) return "control";
        if (Do(TrafficEdits.Control("nope", JunctionControl.Stop)) == null) return "bad junction key taken";
        if (Do(TrafficEdits.Crossings("j40_44", false)) != null || c.Junction("j40_44").crossings) return "crossings";
        if (Do(TrafficEdits.Closed("rj0_0~j4_0~street~2_0", true)) != null || c.Road("rj0_0~j4_0~street~2_0")?.closed != true) return "closed";
        if (Do(TrafficEdits.Speed("rj0_0~j4_0~street~2_0", 5)) == null) return "speed out of bounds taken";
        if (Do(TrafficEdits.Closure("Marathon", new[] { "rj0_0~j4_0~street~2_0" }, 1000, 5000)) != null || c.events.Count != 1) return "event";
        var again = TrafficConfig.Parse(c.ToJson());
        if (again.ToJson() != c.ToJson()) return "not the same through JSON: " + c.ToJson() + " vs " + again.ToJson();
        // Back to auto: overrides that say nothing go.
        Do(TrafficEdits.Control("j40_44", null));
        Do(TrafficEdits.Crossings("j40_44", true));
        if (c.junctions.Count != 0) return "an empty junction override kept";
        Do(TrafficEdits.Reset());
        if (c.ToJson() != new TrafficConfig { version = c.version }.ToJson()) return "reset left " + c.ToJson();
        return null;
    }

    static string Closure(string seed, IsoLayout layout, bool asEvent, ref int rerouted)
    {
        var traffic = new Traffic(layout, new Rng(seed + "::traffic"), r => 0);
        // The street most driven in the first half minute.
        var on = new int[layout.roads.Count];
        for (double t = 0; t < 30; t += DT)
        {
            traffic.Update(DT, null);
            foreach (var c in traffic.cars) if (!c.idle && c.loop < 0) on[c.road]++;
        }
        int busiest = -1;
        for (int r = 0; r < on.Length; r++)
            if (layout.roads[r].kind == RoadKind.Street && traffic.Driven(r) && (busiest < 0 || on[r] > on[busiest])) busiest = r;
        if (busiest < 0) return null;
        string key = TrafficKeys.Road(layout, busiest);
        var config = new TrafficConfig();
        long now = traffic.Now;
        TrafficEdits.Apply(config, asEvent ? TrafficEdits.Closure("Marathon", new[] { key }, now, now + 60_000) : TrafficEdits.Closed(key, true));
        var resolved = ResolvedTraffic.Resolve(config, layout, now);
        if (!resolved.closed[busiest]) return $"road {busiest} ({key}) not closed by its {(asEvent ? "event" : "setting")}";
        traffic.Apply(resolved);
        var was = new int[traffic.cars.Count];
        for (int k = 0; k < was.Length; k++) was[k] = traffic.cars[k].road;
        // Already let into the junction onto it as it closed: they carry on.
        var committed = new bool[was.Length];
        for (int k = 0; k < was.Length; k++) committed[k] = traffic.cars[k].next == busiest;
        double moving = 0;
        int samples = 0;
        for (double t = 0; t < 50; t += DT)
        {
            traffic.Update(DT, null);
            for (int k = 0; k < was.Length; k++)
            {
                var c = traffic.cars[k];
                if (c.idle || c.loop >= 0 || c.taken) continue;
                if (c.road == busiest && was[k] != busiest && !committed[k]) return $"car {k} drove onto closed road {busiest} {t:0.0} s after it closed";
                if (c.road != was[k]) committed[k] = false;
                was[k] = c.road;
            }
            if (t > 10) { moving += traffic.Flow(); samples++; }
        }
        if (samples > 0 && moving / samples < 0.2) return $"the town ground to a halt with road {busiest} closed ({moving / samples * 100:0}% of free speed)";
        // Open again: someone drives it.
        traffic.Apply(ResolvedTraffic.Resolve(new TrafficConfig(), layout, traffic.Now));
        bool driven = false;
        for (double t = 0; t < 90 && !driven; t += DT)
        {
            traffic.Update(DT, null);
            foreach (var c in traffic.cars) if (!c.idle && c.road == busiest) driven = true;
        }
        if (!driven) return $"road {busiest} not driven again once open";
        rerouted++;
        return null;
    }
}

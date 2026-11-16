// Debugging aid: one car's view of its junction over time.
//   dotnet run --project "unity/Tools~/Checks" -- trace <i> <car> [busy]
using System;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class TrafficTrace
{
    public static int Run(int i, int k, bool busy)
    {
        string seed = $"rules-{i}";
        var p = GenParams.Normalize(new GenParams { width = 96 + (i % 3) * 32, height = 96 + ((i / 3) % 3) * 32, roadDensity = 0.7 + (i % 4) * 0.1, roadSense = i % 2 });
        var layout = Layout.Build(Generator.Generate(seed, p));
        var settings = ResolvedTraffic.Resolve(new TrafficConfig { preset = busy ? TrafficConfig.BUSY : TrafficConfig.AUTO }, layout, 0);
        var traffic = new Traffic(layout, new Rng(seed + "::traffic"), r => 0, settings);
        var c = traffic.cars[k];
        for (double t = 0; t < 120; t += 0.05)
        {
            traffic.Update(0.05, null);
            if (t < 40 || t > 43.1) continue;
            var (done, rest) = traffic.Along(c);
            string lane = "";
            if (c.next >= 0)
            {
                int node = c.dir == 1 ? layout.roads[c.road].to : layout.roads[c.road].from;
                int nd = layout.roads[c.next].from == node ? 1 : -1;
                foreach (int o in traffic.LaneCars(c.next, nd)) { var oc = traffic.cars[o]; lane += $" {o}@{(nd == 1 ? oc.s : traffic.LengthOf(c.next) - oc.s):0.0}v{oc.speed:0.0}"; }
                var j = traffic.junctions[node];
                if (j != null) foreach (var oc in traffic.cars) if (oc.granted == node) lane += $" G{traffic.cars.IndexOf(oc)}:{oc.grantMove}";
            }
            Console.WriteLine($"{t:0.00} s {c.s:0.00} dir {c.dir} prev {c.prev} road {c.road} rest {rest:0.00} v {c.speed:0.00} next {c.next} granted {c.granted} held {c.held:0.0} why '{c.why}' | next lane:{lane}");
        }
        return 0;
    }
}

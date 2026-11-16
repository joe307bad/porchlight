// The traffic minds the junctions, over many towns:
//
//   dotnet run --project "unity/Tools~/Checks" -- rules [count]
//
// For each town (RoadSense's repair on and off, the usual number of cars and a
// busy town's): every junction's model holds together (exactly one of each
// pair of foes gives way; no stage of a signal gives two foes a green), and
// then a couple of minutes of traffic:
//   - no two cars with crossing movements are in a junction's box together;
//   - no car runs into the one ahead in its lane;
//   - no car is held at a junction (not at a red light) for more than JAM seconds;
//   - most cars get somewhere;
//   - the same town run twice runs the same.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class TrafficRulesCheck
{
    const double DT = 0.05, SECONDS = 120, JAM = 40;

    public static int Run(int count)
    {
        int failed = 0;
        long junctions = 0, signals = 0, boxes = 0, held = 0, trips = 0;
        double flow = 0;
        int runs = 0;
        for (int i = 0; i < count; i++)
        {
            string seed = $"rules-{i}";
            var p = new GenParams { width = 96 + (i % 3) * 32, height = 96 + ((i / 3) % 3) * 32, roadDensity = 0.7 + (i % 4) * 0.1, roadSense = i % 2 };
            p = GenParams.Normalize(p);
            var m = Generator.Generate(seed, p);
            var layout = Layout.Build(m);
            foreach (var busy in new[] { false, true })
            {
                var config = new TrafficConfig { preset = busy ? TrafficConfig.BUSY : TrafficConfig.AUTO };
                var settings = ResolvedTraffic.Resolve(config, layout, 0);
                string err = RunOnce(seed, layout, settings, ref junctions, ref signals, ref boxes, ref held, ref flow, ref trips, out string replay);
                runs++;
                if (err != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}{(busy ? " (busy)" : "")}: {err}");
                }
                else if (replay != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}{(busy ? " (busy)" : "")}: {replay}");
                }
            }
        }
        Console.WriteLine($"{runs - failed}/{runs} runs ok: {junctions} junctions ({signals} signals); {boxes} cars let through junctions; longest held at a junction {held / 10.0:0.0} s; cars moving at {flow / Math.Max(1, runs) * 100:0}% of their free speed on average; {trips} trips driven to the end");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>What's ahead of a stuck car, car by car.</summary>
    static string Chain(Traffic traffic, IsoLayout layout, int k)
    {
        var sb = new System.Text.StringBuilder();
        var seen = new HashSet<int>();
        for (int step = 0; step < 8 && seen.Add(k); step++)
        {
            var c = traffic.cars[k];
            var (done, rest) = traffic.Along(c);
            sb.Append($"\n   car {k}: road {c.road} ({layout.roads[c.road].kind}, len {traffic.LengthOf(c.road):0.0}) dir {c.dir} done {done:0.00} rest {rest:0.00} v {c.speed:0.00} next {c.next} prev {c.prev} granted {c.granted} held {c.held:0.0} why '{c.why}' wait {c.wait:0.0} at ({c.x:0.0},{c.y:0.0})");
            if (c.why == "red light" && c.next >= 0)
            {
                int node = c.dir == 1 ? layout.roads[c.road].to : layout.roads[c.road].from;
                var j = traffic.junctions[node];
                int mv = j.Move(j.LegIn(c.road, c.dir), j.LegOut(c.next, layout.roads[c.next].from == node ? 1 : -1));
                sb.Append($"\n     junction {node} legs {j.legs.Count} move {mv} ({(mv >= 0 ? j.moves[mv].turn.ToString() : "-")}) cycle {j.cycleMs} offset {j.offsetMs}; stages:");
                foreach (var st in j.stages) sb.Append($" [green {st.green:X} perm {st.permitted:X} {st.greenMs}/{st.yellowMs}/{st.redMs}]");
                sb.Append("\n     lamps over a cycle:");
                for (long ms = 0; ms < j.cycleMs; ms += 1000) sb.Append(" " + j.Light(mv, traffic.Now + ms, false).ToString()[0]);
            }
            // The next car ahead: in its lane, or past its junction.
            int best = -1;
            double bestD = double.PositiveInfinity;
            foreach (int o in traffic.LaneCars(c.road, c.dir))
            {
                double d = (traffic.cars[o].s - c.s) * c.dir;
                if (o != k && d > 0 && d < bestD) { bestD = d; best = o; }
            }
            if (best < 0 && c.next >= 0)
            {
                int node = c.dir == 1 ? layout.roads[c.road].to : layout.roads[c.road].from;
                int nd = layout.roads[c.next].from == node ? 1 : -1;
                foreach (int o in traffic.LaneCars(c.next, nd))
                {
                    var oc = traffic.cars[o];
                    double d = nd == 1 ? oc.s : traffic.LengthOf(c.next) - oc.s;
                    if (o != k && d < bestD) { bestD = d; best = o; }
                }
            }
            if (best < 0) break;
            k = best;
        }
        return sb.ToString() + "\n   ";
    }

    static string RunOnce(string seed, IsoLayout layout, ResolvedTraffic settings, ref long junctions, ref long signals, ref long boxes, ref long longestHeld, ref double flowSum, ref long trips, out string replay)
    {
        replay = null;
        var traffic = new Traffic(layout, new Rng(seed + "::traffic"), r => 0, settings);
        foreach (var j in traffic.junctions)
        {
            if (j == null) continue;
            junctions++;
            if (j.IsSignal) signals++;
            var errs = j.Check();
            if (errs.Count > 0) return errs[0];
        }
        var cars = traffic.cars;
        var heldFor = new double[cars.Count];
        var wasGranted = new int[cars.Count];
        for (int k = 0; k < cars.Count; k++) wasGranted[k] = -1;
        var travelled = new double[cars.Count];
        var last = new (double x, double y)[cars.Count];
        for (int k = 0; k < cars.Count; k++) last[k] = (cars[k].x, cars[k].y);
        double flow = 0;
        int samples = 0;
        for (double t = 0; t < SECONDS; t += DT)
        {
            traffic.Update(DT, null);
            long now = traffic.Now;
            // Foes together in a box.
            foreach (var j in traffic.junctions)
            {
                if (j == null) continue;
                var inBox = new List<int>();
                for (int k = 0; k < cars.Count; k++)
                {
                    var c = cars[k];
                    if (c.idle || c.taken || c.loop >= 0 || c.granted != j.node) continue;
                    if (Numbers.Hypot(c.x - j.at.x, c.y - j.at.y) < Traffic.CORNER) inBox.Add(k);
                }
                for (int a = 0; a < inBox.Count; a++)
                    for (int b = a + 1; b < inBox.Count; b++)
                    {
                        var ca = cars[inBox[a]];
                        var cb = cars[inBox[b]];
                        if (j.Foes(ca.grantMove, cb.grantMove))
                            return $"cars {inBox[a]} and {inBox[b]} cross each other in junction {j.node} ({j.control}) at {t:0.0} s";
                    }
            }
            for (int k = 0; k < cars.Count; k++)
            {
                var c = cars[k];
                if (c.idle || c.taken || c.loop >= 0) continue;
                if (c.granted >= 0 && wasGranted[k] != c.granted) boxes++;
                wasGranted[k] = c.granted;
                travelled[k] += Numbers.Hypot(c.x - last[k].x, c.y - last[k].y);
                last[k] = (c.x, c.y);
                // Held at a junction, other than at a red light.
                bool red = false;
                var (_, rest) = traffic.Along(c);
                int node = c.dir == 1 ? layout.roads[c.road].to : layout.roads[c.road].from;
                var j = node < traffic.junctions.Length ? traffic.junctions[node] : null;
                if (j != null && j.IsSignal && c.next >= 0)
                {
                    int mv = j.Move(j.LegIn(c.road, c.dir), j.LegOut(c.next, layout.roads[c.next].from == node ? 1 : -1));
                    var lamp = j.Light(mv, now, traffic.night);
                    red = lamp == Lamp.Red || lamp == Lamp.Yellow;
                }
                heldFor[k] = c.speed < 0.05 && !red ? heldFor[k] + DT : 0;
                longestHeld = Math.Max(longestHeld, (long)(heldFor[k] * 10));
                if (heldFor[k] > JAM && t > 5) return Chain(traffic, layout, k) + $"car {k} stuck {heldFor[k]:0} s at ({c.x:0.0}, {c.y:0.0}) on road {c.road} ({c.held:0.0} s held at its junction: {c.why}, granted {c.granted}, next {c.next})";
            }
            // Rear-ends: two cars in one lane closer than a car's length.
            for (int r = 0; r < layout.roads.Count; r++)
                foreach (int dir in new[] { 1, -1 })
                {
                    var lane = traffic.LaneCars(r, dir);
                    for (int a = 0; a + 1 < lane.Count; a++)
                    {
                        var ca = cars[lane[a]];
                        var cb = cars[lane[a + 1]];
                        if (Math.Abs(ca.s - cb.s) < Traffic.CAR_LENGTH * 0.7 && ca.prev < 0 && cb.prev < 0 && ca.next != Traffic.ROUND && cb.next != Traffic.ROUND)
                            return $"cars {lane[a]} and {lane[a + 1]} run into each other on road {r} at {t:0.0} s";
                    }
                }
            if (t > 10) { flow += traffic.Flow(); samples++; }
        }
        flowSum += samples > 0 ? flow / samples : 1;
        trips += traffic.Metrics().trips;
        int moving = 0, all = 0;
        for (int k = 0; k < cars.Count; k++)
        {
            if (cars[k].idle || cars[k].loop >= 0) continue;
            all++;
            if (travelled[k] > 20) moving++;
        }
        if (all > 0 && moving < all * 0.8) return $"only {moving} of {all} cars got anywhere in {SECONDS} s";
        // The same again: the same.
        var again = new Traffic(layout, new Rng(seed + "::traffic"), r => 0, settings);
        for (double t = 0; t < SECONDS; t += DT) again.Update(DT, null);
        for (int k = 0; k < cars.Count; k++)
            if (cars[k].x != again.cars[k].x || cars[k].y != again.cars[k].y)
            {
                replay = $"car {k} ran differently the second time";
                break;
            }
        return null;
    }
}

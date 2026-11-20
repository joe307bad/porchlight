// A whole day of each starter town's life (and a few generated towns'), run from
// midnight to three the next morning, as fast as it goes: everyone's day, every
// car and walker, every junction. Fails if the town wedges (people still out on
// trips well after the day's done, or a trip that's taken hours), if too many trips
// can't be made, if more are out at once than there are slots to draw them in, or
// if the same town's day doesn't come out the same twice. Prints how the day went,
// to compare across commits.
//
//   dotnet run --project "unity/Tools~/Checks" -c Release -- traffic-day [towns dir] [extra seeds]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

static class TrafficDay
{
    /// <summary>At three in the morning, at most this share of people may still be out.</summary>
    const double STILL_OUT = 0.002;
    /// <summary>No trip should take longer than this.</summary>
    const long LONGEST_TRIP = 3 * TownClock.HOUR;
    /// <summary>At most this share of trips may fail.</summary>
    const double MOST_FAILED = 0.02;
    /// <summary>A day in November 2026: any day will do, but always the same one.</summary>
    /// <summary>8:15 that day: the morning rush.</summary>
    public static long Morning => TownClock.At(DAY, 8 * TownClock.HOUR + 15 * TownClock.MINUTE);
    static readonly long DAY = TownClock.DayOf(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());

    public static int Run(string dir, int seeds)
    {
        var keys = new List<(string name, string key)>();
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
            keys.Add((Path.GetFileNameWithoutExtension(f), JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString()));
        for (int i = 0; i < seeds; i++) keys.Add(($"day-{i}", $"seed=day-{i}&width={(i % 2 == 0 ? 128 : 192)}"));
        int fails = 0;
        foreach (var (name, key) in keys)
        {
            var errs = Day(name, key, out string report);
            Console.WriteLine(report);
            foreach (var e in errs) Console.WriteLine($"FAIL {name}: {e}");
            fails += errs.Count;
        }
        Console.WriteLine(fails == 0 ? $"traffic-day: {keys.Count} towns, all ok" : $"traffic-day: {fails} failure(s)");
        return fails == 0 ? 0 : 1;
    }

    public static TownLife Make(string key, LifeOptions options = null)
    {
        var (seed, p) = TownKey.Parse(key);
        var model = Generator.Generate(seed, p);
        var layout = Layout.Build(model);
        var zoning = Zoning.Plan(model, layout);
        return new TownLife(model, layout, zoning, options);
    }

    static List<string> Day(string name, string key, out string report)
    {
        var errs = new List<string>();
        var sw = Stopwatch.StartNew();
        var life = Make(key);
        long made = sw.ElapsedMilliseconds;
        long start = TownClock.At(DAY, 0);
        life.Start(start);
        int peakCars = 0, peakOut = 0, peakDrawnCars = 0, peakDrawnPeople = 0;
        string peakAt = "";
        var fingerprint = new List<long>();
        for (long t = start; t <= start + 27 * TownClock.HOUR; t += TownClock.MINUTE)
        {
            life.RunUntil(t);
            int onRoad = life.CarsOnRoad(), outNow = life.OutNow();
            if (onRoad > peakCars)
            {
                peakCars = onRoad;
                peakAt = TownClock.Format(t);
            }
            peakOut = Math.Max(peakOut, outNow);
            life.Draw();
            peakDrawnCars = Math.Max(peakDrawnCars, life.carSlots.Count(s => s.shown));
            peakDrawnPeople = Math.Max(peakDrawnPeople, life.personSlots.Count(s => s.shown));
            if (t % TownClock.HOUR == 0) fingerprint.Add(life.handled * 31 + onRoad * 7 + outNow);
            CheckQueues(life, errs, t);
            if (errs.Count > 5) break;
        }
        var s = life.stats;
        long ms = sw.ElapsedMilliseconds;
        int stillOut = life.OutNow();
        if (stillOut > Math.Max(2, life.people.Count * STILL_OUT))
            errs.Add($"{stillOut} people still out at 03:00 the next day (gridlock?): {Stuck(life)}");
        if (s.longestTrip > LONGEST_TRIP) errs.Add($"a trip took {s.longestTrip / TownClock.MINUTE} minutes");
        if (s.started > 0 && s.failed > s.started * MOST_FAILED)
            errs.Add($"{s.failed} of {s.started} trips failed: {string.Join(", ", s.failures.Select(kv => $"{kv.Key} {kv.Value}"))}");
        if (s.undrawnCars > 0 || s.undrawnPeople > 0)
            errs.Add($"more out at once than slots to draw them: {s.undrawnCars} cars and {s.undrawnPeople} people undrawn ({life.busSlotsFrom} car slots, {life.personSlots.Length} people slots)");
        if (s.started < life.people.Count) errs.Add($"only {s.started} trips for {life.people.Count} people");
        // The same day again from the same key: the same, hour by hour.
        var again = Make(key);
        again.Start(start);
        var fp2 = new List<long>();
        for (long t = start; t <= start + 27 * TownClock.HOUR; t += TownClock.MINUTE)
        {
            again.RunUntil(t);
            if (t % TownClock.HOUR == 0) fp2.Add(again.handled * 31 + again.CarsOnRoad() * 7 + again.OutNow());
        }
        if (!fp2.SequenceEqual(fingerprint)) errs.Add("the same town's day came out differently the second time");
        var (taken, total) = life.Parking();
        string Mean(int m) => s.finishedBy[m] == 0 ? "-" : $"{s.durationBy[m] / s.finishedBy[m] / 1000.0 / 60:0.0} min";
        report = $"{name}: {life.people.Count} people, {life.cars.Count - life.buses.Count} cars, {life.buses.Count} bus(es){(life.busWhy != null ? $" ({life.busWhy})" : $" {life.busStops.Count} stops")}, {life.net.lanes.Count} lanes, {life.net.junctions.Count} junctions; made in {made} ms, day in {ms - made} ms ({life.handled} events)\n" +
                 $"  trips {s.started} started, {s.finished} made, {s.failed} failed; walk {s.finishedBy[0]} ({Mean(0)}), drive {s.finishedBy[1]} ({Mean(1)}), bus {s.finishedBy[2]} ({Mean(2)})\n" +
                 $"  peak {peakCars} cars on the road at {peakAt}, {peakOut} out; drawn at most {peakDrawnCars}/{life.carSlots.Length} cars, {peakDrawnPeople}/{life.personSlots.Length} people\n" +
                 $"  junction wait {life.stats.MeanCarWait() / 1000:0.0} s mean; gridlock broken {s.gridlockBroken}, hunts {s.hunts}, no spot {s.noSpot}, main-road crossings {s.mainCrossings}, problems {s.problemCount}; kerb spots {taken}/{total} at 03:00; longest trip {s.longestTrip / 60000} min";
        return errs;
    }

    /// <summary>The queues hold together: no car in front of the one ahead of it, none off its segment.</summary>
    static void CheckQueues(TownLife life, List<string> errs, long t)
    {
        foreach (var c in life.cars)
        {
            if (c.seg < 0) continue;
            double f = life.Front(c);
            if (double.IsNaN(f) || f > c.s1 + 1e-6) errs.Add($"{TownClock.Format(t)}: car {c.id} at {f:0.00} past its interval's end {c.s1:0.00}");
        }
        foreach (var (lead, follow) in life.Pairs())
        {
            double gap = life.Front(lead) - lead.length - life.Front(follow);
            if (gap < TownLife.FOLLOWING - 1e-6)
                errs.Add($"{TownClock.Format(t)}: car {follow.id} {gap:0.000} tiles behind car {lead.id} on segment {lead.seg}{(lead.seg >= life.net.lanes.Count ? " (turn)" : "")}: " +
                         $"lead {lead.state} front {life.Front(lead):0.00} len {lead.length:0.00} [{lead.s0:0.00}->{lead.s1:0.00}] laggy {lead.laggy}; follow {follow.state} front {life.Front(follow):0.00} [{follow.s0:0.00}->{follow.s1:0.00}] laggy {follow.laggy} step {follow.step}");
            if (errs.Count > 5) return;
        }
    }

    static string Stuck(TownLife life)
    {
        var o = life.people.Where(p => p.trip != null).Take(5).Select(p =>
        {
            var c = p.car >= 0 ? life.cars[p.car] : null;
            return $"#{p.id} {p.trip.mode} {p.trip.from}->{p.trip.to} leg {p.trip.leg} ({p.trip.legs[Math.Max(0, Math.Min(p.trip.leg, p.trip.legs.Count - 1))].kind}) car {(c == null ? "-" : $"{c.state} seg {c.seg} blocked by [{string.Join(",", life.BlockedBy(c.id))}]")}";
        });
        return string.Join("; ", o);
    }
}

static class LifeDebug
{
    public static int Run(string key, double hours)
    {
        long m0 = GC.GetTotalMemory(true);
        var (seed, settings) = TownKey.Parse(key);
        var model = Generator.Generate(seed, settings);
        var layout = Layout.Build(model);
        var zoning = Zoning.Plan(model, layout);
        long m1 = GC.GetTotalMemory(true);
        var net = new RoadNet(model, layout);
        long m2 = GC.GetTotalMemory(true);
        var walk = new Walkways(layout.sidewalks, net);
        long m3 = GC.GetTotalMemory(true);
        var life = new TownLife(model, layout, zoning);
        long m4 = GC.GetTotalMemory(true);
        Console.WriteLine($"memory: town {(m1 - m0) / 1048576.0:0.0} MB, roadnet {(m2 - m1) / 1048576.0:0.0}, walkways {(m3 - m2) / 1048576.0:0.0}, life (all) {(m4 - m3) / 1048576.0:0.0}");
        GC.KeepAlive(net); GC.KeepAlive(walk);
        long start = TownClock.At(TownClock.DayOf(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()), 0);
        life.Start(start);
        for (long t = start; t <= start + (long)(hours * TownClock.HOUR); t += TownClock.MINUTE)
        {
            life.RunUntil(t);
            if (t % TownClock.HOUR == 0) Console.WriteLine($"{TownClock.Format(t)} cars {life.CarsOnRoad()} out {life.OutNow()} events {life.handled}");
        }
        Console.WriteLine($"after: life {(GC.GetTotalMemory(true) - m3) / 1048576.0:0.0} MB");
        var states = life.cars.Where(c => c.seg >= 0).GroupBy(c => c.state).Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine(string.Join(", ", states));
        foreach (var c in life.cars.Where(c => c.seg >= 0).Take(15))
            Console.WriteLine($"car {c.id} {c.state} seg {c.seg}{(c.seg >= life.net.lanes.Count ? " (turn)" : "")} step {c.step}/{c.route?.Count} front {life.Front(c):0.00} s1 {c.s1:0.00} laggy {c.laggy} blockedBy [{string.Join(",", life.BlockedBy(c.id))}] arrived {(c.arrived < 0 ? "-" : TownClock.Format(c.arrived))}");
        foreach (var c in life.cars.Where(c => c.seg >= 0 && c.state == CarState.Waiting).Take(12))
            Console.WriteLine("  " + life.WhyWaiting(c));
        var walkers = life.people.Where(p => p.trip != null && p.trip.mode == Mode.Walk).Take(5);
        foreach (var p in walkers) Console.WriteLine($"walker {p.id} piece {p.piece}/{p.walk?.Count} w0 {p.w0} crossWait {p.crossWait} node {(p.walk != null && p.piece < p.walk.Count ? p.walk[p.piece].node : -9)}");
        return 0;
    }
}

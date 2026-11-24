// The town's service vehicles (Life/Services.cs) in each starter town: a weekday
// morning on which the first neighbourhood's bins are collected, with emergency
// calls thirty times as often as real so there are plenty to watch. Fails if a
// bin lorry doesn't come out on its day, doesn't work through its stops, or is
// still out by mid-afternoon; if no call is answered; if no driver pulls over or
// waits for a siren (in a town with traffic); or if a service vehicle gets stuck.
//
//   dotnet run --project "unity/Tools~/Checks" -c Release -- services [towns dir]

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Life;

static class ServicesCheck
{
    public static int Run(string dir)
    {
        int fails = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) fails++;
        }
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var options = new LifeOptions { callRate = 30 };
            var life = TrafficDay.Make(JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString(), options);
            var bins = life.services.Where(s => s.kind == ServiceKind.Bins).ToList();
            Expect(bins.Count > 0 && life.services.Any(s => s.kind == ServiceKind.Ambulance) && life.services.Any(s => s.kind == ServiceKind.FireEngine),
                $"{name}: {bins.Count} bin round(s), an ambulance and a fire engine");
            if (bins.Count == 0) continue;
            // A Monday (bins.First()'s day) in November 2026: from 6 am to 3 pm.
            var first = bins[0];
            long day = TownClock.DayOf(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
            while ((((day + 4) % 7) + 7) % 7 != first.weekday) day++;
            long start = TownClock.At(day, 6 * TownClock.HOUR);
            life.Start(start);
            bool outBy = false;
            long gaveUp0 = life.stats.gaveUp;
            for (long t = start; t <= start + 9 * TownClock.HOUR; t += TownClock.MINUTE)
            {
                life.RunUntil(t);
                if (t <= TownClock.At(day, 7 * TownClock.HOUR + 30 * TownClock.MINUTE) && first.car.seg >= 0) outBy = true;
            }
            var s = life.stats;
            Console.WriteLine($"     {name}: round of {first.stops.Count} stops ({first.stops.Sum(x => x.houses)} houses) on day {first.weekday}, {first.next} made; " +
                $"{s.binStops} bin stops in all ({s.binSkipped} left); {s.callsAnswered} calls answered, {s.pulledOver} pulled over, {s.sirenWaits} waits at a junction for a siren, {s.sirenOnRed} on red with a siren");
            Expect(outBy, $"{name}: the first round's lorry is out by 7:30 on its day");
            Expect(first.done > 0 && first.next >= first.stops.Count, $"{name}: it made all its stops and went home by 3 pm ({first.next} of {first.stops.Count})");
            Expect(s.binSkipped <= Math.Max(1, s.binStops / 20), $"{name}: bin stops left for want of a way there: {s.binSkipped} (at most 1 in 20)");
            Expect(s.callsAnswered > 0, $"{name}: emergency calls are answered ({s.callsAnswered})");
            Expect(life.people.Count < 2000 || s.pulledOver + s.sirenWaits > 0, $"{name}: drivers make way for a siren ({s.pulledOver} pulled over, {s.sirenWaits} waited)");
            bool stuck = life.services.Any(x => x.car.seg >= 0 && x.car.state != CarState.AtStop && life.Now - x.car.movedAt > Gridlock_STUCK);
            Expect(!stuck, $"{name}: no service vehicle stuck");
        }
        Console.WriteLine(fails == 0 ? "services: all ok" : $"services: {fails} failed");
        return fails == 0 ? 0 : 1;
    }

    const long Gridlock_STUCK = 10 * TownClock.MINUTE;
}

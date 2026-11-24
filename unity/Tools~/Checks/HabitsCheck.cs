// Drivers' habits over several days (Congestion.cs): one town run day after day
// from the same start. From the second day most drives are trips the driver has
// made before (one in ten works the way out afresh each day), and the town's
// driving doesn't get slower day on day as the habits settle towards Wardrop's
// equilibrium. Prints each day's figures.
//
//   dotnet run --project "unity/Tools~/Checks" -c Release -- habits [towns dir] [town] [days]

using System;
using System.IO;
using System.Text.Json;
using Porchlight.Life;

static class HabitsCheck
{
    /// <summary>From the second day, at least this share of drives go by habit (commutes repeat; errands and visits change from day to day).</summary>
    const double LEAST_HABITS = 0.3;
    /// <summary>No day's mean drive may be this much slower than the first's.</summary>
    const double MOST_SLOWER = 1.15;

    public static int Run(string dir, string town, int days)
    {
        string key = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, town + ".json"))).RootElement.GetProperty("key").GetString();
        var life = TrafficDay.Make(key, TrafficDay.Options());
        long start = TownClock.At(TownClock.DayOf(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()), 0);
        life.Start(start);
        var s = life.stats;
        int fails = 0;
        double first = 0;
        long drives0 = 0, time0 = 0, habits0 = 0, kept0 = 0, rerouted0 = 0;
        for (int d = 1; d <= days; d++)
        {
            life.RunUntil(start + d * TownClock.DAY);
            long drives = s.finishedBy[1] - drives0, time = s.durationBy[1] - time0, habits = s.habits - habits0, kept = s.keptWay - kept0, rerouted = s.rerouted - rerouted0;
            double mean = drives == 0 ? 0 : time / (double)drives / 60000;
            double share = drives == 0 ? 0 : habits / (double)drives;
            Console.WriteLine($"     {town} day {d}: {drives} drives, {mean:0.00} min on average; {habits} by habit ({100 * share:0}%), {kept} keeping to the way they knew, {rerouted} rerouted on the way");
            if (d == 1) first = mean;
            else
            {
                if (share < LEAST_HABITS) { Console.WriteLine($"FAIL day {d}: only {100 * share:0}% of drives by habit"); fails++; }
                if (mean > first * MOST_SLOWER) { Console.WriteLine($"FAIL day {d}: drives average {mean:0.00} min, the first day's {first:0.00}"); fails++; }
            }
            drives0 = s.finishedBy[1];
            time0 = s.durationBy[1];
            habits0 = s.habits;
            kept0 = s.keptWay;
            rerouted0 = s.rerouted;
        }
        Console.WriteLine(fails == 0 ? "habits: all ok" : $"habits: {fails} failed");
        return fails == 0 ? 0 : 1;
    }
}

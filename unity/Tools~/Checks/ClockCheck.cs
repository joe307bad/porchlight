// The county's clock (Shared/Life/Clock.cs): US Eastern time, daylight saving
// and all, said on a 12-hour clock, the same whatever the machine's own zone.
//
//   dotnet run --project "unity/Tools~/Checks" -- clock

using System;
using System.Collections.Generic;
using Porchlight.Life;

static class ClockCheck
{
    public static int Run()
    {
        var fails = new List<string>();
        void Expect(string got, string want, string what)
        {
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}: {got}{(ok ? "" : $", wanted {want}")}");
            if (!ok) fails.Add(what);
        }
        long Utc(int y, int mo, int d, int h, int mi = 0) => new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        Expect(TownClock.Format(Utc(2026, 12, 1, 12)), "7:00 AM", "winter: noon UTC is 7 AM Eastern Standard");
        Expect(TownClock.Format(Utc(2026, 7, 1, 12)), "8:00 AM", "summer: noon UTC is 8 AM Eastern Daylight");
        Expect(TownClock.Format(Utc(2026, 7, 1, 21, 30)), "5:30 PM", "afternoon on a 12-hour clock");
        Expect(TownClock.Format(Utc(2026, 12, 1, 5)), "12:00 AM", "midnight is 12 AM");
        Expect(TownClock.Format(Utc(2026, 12, 1, 17)), "12:00 PM", "noon is 12 PM");
        Expect(TownClock.FormatSeconds(Utc(2026, 12, 1, 13, 5) + 30_000), "8:05:30 AM", "with seconds");
        // The changeovers in 2026: 8 March and 1 November, at 2 am.
        Expect(TownClock.Format(Utc(2026, 3, 8, 6, 59)), "1:59 AM", "the spring changeover: a minute before, still standard time");
        Expect(TownClock.Format(Utc(2026, 3, 8, 7, 0)), "3:00 AM", "the spring changeover: 2 am becomes 3 am");
        Expect(TownClock.Format(Utc(2026, 11, 1, 5, 59)), "1:59 AM", "the autumn changeover: a minute before, daylight time");
        Expect(TownClock.Format(Utc(2026, 11, 1, 6, 0)), "1:00 AM", "the autumn changeover: 2 am goes back to 1 am");
        // A day and a time of day back to the moment, all year round.
        int round = 0;
        for (long m = Utc(2026, 1, 1, 0); m < Utc(2027, 1, 1, 0); m += 7 * TownClock.HOUR + 13 * TownClock.MINUTE)
        {
            long back = TownClock.At(TownClock.DayOf(m), TownClock.TimeOfDay(m));
            // The hour repeated in November has two moments; either is that time of day.
            if (back != m && TownClock.TimeOfDay(back) != TownClock.TimeOfDay(m)) { Console.WriteLine($"FAIL At(DayOf, TimeOfDay) at {m}: {back}"); fails.Add("round trip"); break; }
            round++;
        }
        Console.WriteLine($"ok   a year of moments ({round}) back from their day and time of day");
        Expect(TownClock.Format(TownClock.At(TownClock.DayOf(Utc(2026, 7, 1, 12)), 8 * TownClock.HOUR)), "8:00 AM", "8 am on a summer day is 8 AM");

        Console.WriteLine(fails.Count == 0 ? "clock: all ok" : $"clock: {fails.Count} failure(s)");
        return fails.Count == 0 ? 0 : 1;
    }
}

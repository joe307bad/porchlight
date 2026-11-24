// The town's calendar (Life/Calendar.cs) and what it does to a town's days
// (docs/town-calendar.md). First the calendar alone: its weekdays against the
// system's, 2026's public holidays as the US keeps them, the shops shut on
// Thanksgiving and Christmas Day, a school year of 180 days in each town with its
// breaks, market day every Saturday (shorter in winter), and the same
// calendar on every machine but not the same in every town. Then each starter
// town's life on a school Friday morning (20 November 2026) and the Saturday
// after: young children are taken to school (on foot or in the car) and all of
// them are there by ten; the Saturday has no morning rush (its 7-9 am trips are at most 60%
// of the Friday's), a tenth to a half of the workers work (the shops' and the shifts'), the stallholders are at
// their stalls when the market opens and gone an hour after it shuts, and
// shoppers come to it.
//
//   dotnet run --project "unity/Tools~/Checks" -c Release -- calendar [towns dir]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Life;

static class CalendarCheck
{
    public static int Run(string dir)
    {
        int fails = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) fails++;
        }
        long Day(int y, int m, int d) => TownClock.DayOf(new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());

        // --- The calendar alone ---
        bool weekdays = true;
        for (long d = Day(2025, 1, 1); d < Day(2028, 1, 1); d++)
        {
            var date = new DateTime(1970, 1, 1).AddDays(d);
            if (TownCalendar.Weekday(d) != (int)date.DayOfWeek) weekdays = false;
        }
        Expect(weekdays, "calendar: weekdays as the system has them, 2025 to 2027");
        var holidays = new (int m, int d, string name)[]
        {
            (1, 1, "New Year's Day"), (1, 19, "Martin Luther King Jr. Day"), (2, 16, "Presidents' Day"), (5, 25, "Memorial Day"),
            (6, 19, "Juneteenth"), (7, 3, "Independence Day"), (9, 7, "Labor Day"), (10, 12, "Columbus Day"), (11, 11, "Veterans Day"),
            (11, 26, "Thanksgiving"), (12, 25, "Christmas Day"),
        };
        var found = new List<string>();
        for (long d = Day(2026, 1, 1); d <= Day(2026, 12, 31); d++)
            if (TownCalendar.Holiday(d) is string h) found.Add($"{new DateTime(1970, 1, 1).AddDays(d):MM-dd} {h}");
        var want = holidays.Select(x => $"{x.m:00}-{x.d:00} {x.name}").ToList();
        Expect(found.SequenceEqual(want), $"calendar: 2026's public holidays as observed (Independence Day on Friday 3 July): {string.Join(", ", found)}");
        Expect(TownCalendar.Holiday(Day(2027, 12, 31)) == "New Year's Day", "calendar: New Year's Day 2028 (a Saturday) is kept on Friday 31 December 2027");
        var thanks = TownCalendar.Of("x", Day(2026, 11, 26));
        var xmas = TownCalendar.Of("x", Day(2026, 12, 25));
        var columbus = TownCalendar.Of("x", Day(2026, 10, 12));
        Expect(!thanks.shopsOpen && !xmas.shopsOpen && !thanks.officesOpen && columbus.officesOpen && columbus.shopsOpen,
            "calendar: the shops and offices shut on Thanksgiving and Christmas Day; offices open on Columbus Day");

        string[] seeds = { "porchlight", "ember", "lantern", "juniper", "a", "b", "c", "d" };
        var years = new List<int>();
        bool marketsRight = true, same = true;
        var springs = new HashSet<long>();
        foreach (var seed in seeds)
        {
            int school = 0;
            long spring = -1;
            for (long d = Day(2026, 8, 1); d <= Day(2027, 7, 31); d++)
            {
                var td = TownCalendar.Of(seed, d);
                var again = TownCalendar.Of(seed, d);
                if (td.schoolDay != again.schoolDay || td.market != again.market || td.stalls != again.stalls || td.bell != again.bell) same = false;
                if (td.schoolDay) school++;
                if (td.noSchool == "Spring break" && spring < 0) spring = d;
                bool saturday = td.weekday == 6, christmas = td.month == 12 && td.date == 25, newYear = td.month == 1 && td.date == 1;
                if (td.market != (saturday && !christmas && !newYear)) marketsRight = false;
                if (td.market)
                {
                    bool winter = td.month >= 11 || td.month <= 3;
                    if (td.marketOpen != (long)((winter ? 9 : 8) * TownClock.HOUR) || td.marketClose != (long)((winter ? 12 : 13) * TownClock.HOUR) || td.stalls <= 0 || td.stalls > 1) marketsRight = false;
                }
                if (td.schoolDay && (td.Weekend || (td.holiday != null && td.holiday != "Columbus Day" && td.holiday != "Veterans Day"))) school = -1000;
            }
            years.Add(school);
            springs.Add(spring);
        }
        Expect(years.All(n => n == TownCalendar.SCHOOL_YEAR), $"calendar: a school year of 180 days, never at a weekend or on a holiday it shuts for: {string.Join(", ", years)}");
        Expect(marketsRight, "calendar: market day every Saturday but Christmas Day and New Year's Day, 8 am to 1 pm (9 to noon, November to March)");
        Expect(same && springs.Count > 1, $"calendar: the same from the same town every time, and not the same in every town ({springs.Count} spring breaks among {seeds.Length})");
        var nov20 = TownCalendar.Of("x", Day(2026, 11, 20));
        Expect(nov20.schoolDay && nov20.officesOpen && !nov20.market && TownCalendar.Of("x", Day(2026, 11, 21)).market,
            "calendar: Friday 20 November 2026 is a school and working day; Saturday 21st is market day");

        // --- Each starter town's Friday morning and Saturday ---
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            string key = JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString();

            // Friday: the school run.
            var fri = TrafficDay.Make(key);
            var friday = fri.Day(Day(2026, 11, 20));
            long fStart = TownClock.At(friday.day, 6 * TownClock.HOUR);
            fri.Start(fStart);
            fri.RunUntil(TownClock.At(friday.day, 10 * TownClock.HOUR));
            var young = fri.people.Where(p => p.kind == PersonKind.Student && p.age < TownLife.ESCORT_AGE && !p.work.border).ToList();
            var students = fri.people.Where(p => p.kind == PersonKind.Student).ToList();
            long rushFri = Rush(fri);
            int atSchool = 0, stuck = 0;
            foreach (var p in students) if (p.where != Where.OnTrip && p.at.Same(p.work)) atSchool++;
            foreach (var p in fri.people) if (p.trip != null && p.trip.driver >= 0) stuck++;
            Console.WriteLine($"     {name}: Friday: {students.Count} at school age ({young.Count} under {TownLife.ESCORT_AGE}), {fri.stats.escorted} taken ({fri.stats.passengers} by car), {atSchool} at school at 10 am; {rushFri} trips set off 7-9 am");
            if (young.Count > 0) Expect(fri.stats.escorted >= young.Count / 4, $"{name}: young children are taken to school, on foot or in the car ({fri.stats.escorted} of {young.Count})");
            Expect(stuck == 0, $"{name}: nobody left sitting in a car after it's parked ({stuck})");
            Expect(students.Count == 0 || atSchool >= students.Count * 0.9, $"{name}: the children are at school by 10 ({atSchool} of {students.Count})");

            // Saturday: market day.
            var sat = TrafficDay.Make(key);
            var saturday = sat.Day(Day(2026, 11, 21));
            long sStart = TownClock.At(saturday.day, 6 * TownClock.HOUR);
            sat.Start(sStart);
            int up = sat.StallsUp(saturday), most = 0, atOpen = 0, shoppers = 0;
            var seen = new HashSet<int>();
            for (long t = sStart; t <= TownClock.At(saturday.day, 15 * TownClock.HOUR); t += TownClock.MINUTE)
            {
                sat.RunUntil(t);
                int now = sat.AtMarketNow;
                most = Math.Max(most, now);
                if (t == TownClock.At(saturday.day, saturday.marketOpen + 10 * TownClock.MINUTE))
                    atOpen = sat.people.Count(p => p.kind == PersonKind.Stallholder && p.where != Where.OnTrip && !p.at.border && p.at.id == TownLife.MARKET);
                foreach (var p in sat.people)
                    if (p.kind != PersonKind.Stallholder && p.browseSince >= 0 && seen.Add(p.id)) shoppers++;
            }
            int left = sat.AtMarketNow;
            long rushSat = Rush(sat);
            var workers = sat.people.Where(p => p.kind == PersonKind.Worker || p.kind == PersonKind.CommuterIn).ToList();
            int working = workers.Count(p => sat.Works(p, saturday));
            Console.WriteLine($"     {name}: Saturday: {rushSat} trips set off 7-9 am (Friday {rushFri}); {working} of {workers.Count} workers work; market {(sat.market == null ? "none" : $"{up} of {sat.market.stalls.Count} stalls")}, {atOpen} stallholders there at opening, {shoppers} shoppers, {most} there at once, {left} still there at 3 pm");
            Expect(rushSat <= rushFri * 0.6, $"{name}: no morning rush on a Saturday ({rushSat} trips 7-9 am, {rushFri} on the Friday)");
            Expect(workers.Count == 0 || (working >= workers.Count * 0.1 && working <= workers.Count * 0.5), $"{name}: a tenth to a half of the workers work on a Saturday, as the town's jobs have it ({working} of {workers.Count})");
            if (sat.market != null)
            {
                Expect(up >= Math.Min(2, sat.market.stalls.Count) && atOpen == up, $"{name}: the stallholders are at their stalls when the market opens ({atOpen} of {up})");
                Expect(shoppers > 0, $"{name}: shoppers come to the market ({shoppers})");
                Expect(left == 0, $"{name}: everyone's gone from the market by 3 pm ({left} left)");
            }
        }
        Console.WriteLine(fails == 0 ? "calendar: all ok" : $"calendar: {fails} failed");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Trips set off from 7 to 9 am.</summary>
    static long Rush(TownLife life)
    {
        long n = 0;
        for (int h = 7; h < 9; h++) for (int m = 0; m < 3; m++) n += life.stats.startedByHour[h, m];
        return n;
    }
}

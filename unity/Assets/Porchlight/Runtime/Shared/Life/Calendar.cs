// The town's calendar: what each day is, worked out from the town's seed and the
// date alone, so the server and every player's game agree on it without a word
// sent (as the clock does: Clock.cs). A day is a weekday or a weekend day, perhaps
// a public holiday (the US federal holidays, moved to the Friday or Monday when
// they fall at the weekend); a school day or not (the US school year: 180 days from
// late August, with its breaks, a spring break week and teacher in-service days
// drawn for each town); and on Saturdays in a town with a plaza, market day: the
// stalls up from 8 am to 1 pm (9 to noon in winter). docs/town-calendar.md has the
// rules and where they come from. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    /// <summary>One day in a town's calendar.</summary>
    public sealed class TownDay
    {
        /// <summary>The day (TownClock.DayOf: days since 1 Jan 1970, county time).</summary>
        public long day;
        public int year, month, date;
        /// <summary>0 Sunday to 6 Saturday.</summary>
        public int weekday;
        /// <summary>The public holiday it is (observed), or null.</summary>
        public string holiday;
        /// <summary>Offices (and anyone on a Monday to Friday week) are open: a weekday that isn't a holiday.</summary>
        public bool officesOpen;
        /// <summary>The shops are open (every day but Thanksgiving, Christmas Day and New Year's Day), and their hours.</summary>
        public bool shopsOpen;
        public long shopsOpenAt, shopsCloseAt;
        /// <summary>A school day, and if not, why not ("Saturday", "Winter break", ...).</summary>
        public bool schoolDay;
        public string noSchool;
        /// <summary>The school bells (ms since midnight): the first, and the last of the day.</summary>
        public long bell, endBell;
        /// <summary>Market day: the stalls are up from `marketOpen` to `marketClose` (ms since midnight), this share of them.</summary>
        public bool market;
        public long marketOpen, marketClose;
        public double stalls;

        public bool Weekend => weekday == 0 || weekday == 6;

        public static readonly string[] WEEKDAYS = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        public static readonly string[] MONTHS = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

        /// <summary>"Saturday 28 November".</summary>
        public string Name => $"{WEEKDAYS[weekday]} {date} {MONTHS[month - 1]}";

        /// <summary>What's on, in a few words each: the holiday, market day, school or why not.</summary>
        public List<string> Events()
        {
            var o = new List<string>();
            if (holiday != null) o.Add(holiday);
            if (market) o.Add($"Market day, {TownClock.Clock12(marketOpen / 1000, false)} to {TownClock.Clock12(marketClose / 1000, false)}");
            if (schoolDay) o.Add($"School, {TownClock.Clock12(bell / 1000, false)} to {TownClock.Clock12(endBell / 1000, false)}");
            else if (noSchool != null && !Weekend && noSchool != holiday) o.Add($"No school: {noSchool}");
            if (!shopsOpen) o.Add("The shops are shut");
            else if (!officesOpen && !Weekend) o.Add("Offices shut");
            return o;
        }
    }

    public static class TownCalendar
    {
        /// <summary>The market's hours (hours of the day): April to October, and November to March.</summary>
        public const double MARKET_OPEN = 8, MARKET_CLOSE = 13, WINTER_OPEN = 9, WINTER_CLOSE = 12;
        /// <summary>The shops' hours: Monday to Saturday, and Sunday (hours of the day).</summary>
        public const double SHOPS_OPEN = 9, SHOPS_CLOSE = 17.5, SUNDAY_OPEN = 11, SUNDAY_CLOSE = 16;
        /// <summary>A school day: the first bell is one of these (each town its own), the last this long after (hours).</summary>
        static readonly double[] BELLS = { 7.75, 8, 8.25, 8.5 };
        public const double SCHOOL_HOURS = 6.75;

        /// <summary>0 Sunday to 6 Saturday (1 Jan 1970 was a Thursday).</summary>
        public static int Weekday(long day) => (int)(((day + 4) % 7 + 7) % 7);

        static DateTime Date(long day) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day);

        static long DayOf(DateTime d) => (long)(d - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;

        /// <summary>The `nth` (1-based; -1 the last) `weekday` of a month.</summary>
        static DateTime Nth(int year, int month, DayOfWeek weekday, int nth)
        {
            if (nth < 0)
            {
                var last = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
                return last.AddDays(-(((int)last.DayOfWeek - (int)weekday + 7) % 7));
            }
            var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            return first.AddDays(((int)weekday - (int)first.DayOfWeek + 7) % 7 + 7 * (nth - 1));
        }

        /// <summary>A fixed-date holiday as it's kept: a Saturday's on the Friday before, a Sunday's on the Monday after.</summary>
        static DateTime Observed(DateTime d) =>
            d.DayOfWeek == DayOfWeek.Saturday ? d.AddDays(-1) : d.DayOfWeek == DayOfWeek.Sunday ? d.AddDays(1) : d;

        /// <summary>The US federal holidays of a year, as observed (5 U.S.C. 6103), by day.</summary>
        static IEnumerable<(DateTime at, string name)> Holidays(int year)
        {
            yield return (Observed(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)), "New Year's Day");
            yield return (Nth(year, 1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day");
            yield return (Nth(year, 2, DayOfWeek.Monday, 3), "Presidents' Day");
            yield return (Nth(year, 5, DayOfWeek.Monday, -1), "Memorial Day");
            yield return (Observed(new DateTime(year, 6, 19, 0, 0, 0, DateTimeKind.Utc)), "Juneteenth");
            yield return (Observed(new DateTime(year, 7, 4, 0, 0, 0, DateTimeKind.Utc)), "Independence Day");
            yield return (Nth(year, 9, DayOfWeek.Monday, 1), "Labor Day");
            yield return (Nth(year, 10, DayOfWeek.Monday, 2), "Columbus Day");
            yield return (Observed(new DateTime(year, 11, 11, 0, 0, 0, DateTimeKind.Utc)), "Veterans Day");
            yield return (Nth(year, 11, DayOfWeek.Thursday, 4), "Thanksgiving");
            yield return (Observed(new DateTime(year, 12, 25, 0, 0, 0, DateTimeKind.Utc)), "Christmas Day");
            // New Year's Day on a Saturday is kept on the Friday before: 31 December.
            var next = Observed(new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            if (next.Year == year) yield return (next, "New Year's Day");
        }

        /// <summary>The public holiday day `day` is (as observed), or null.</summary>
        public static string Holiday(long day)
        {
            var d = Date(day);
            foreach (var (at, name) in Holidays(d.Year)) if (at == d) return name;
            return null;
        }

        /// <summary>Holidays the shops shut for.</summary>
        static bool ShopsShut(string holiday) => holiday == "Thanksgiving" || holiday == "Christmas Day" || holiday == "New Year's Day";

        /// <summary>Holidays most offices stay open on (banks and the post office shut; most firms don't).</summary>
        static bool OfficesOpenOn(string holiday) => holiday == "Columbus Day" || holiday == "Veterans Day";

        /// <summary>A number from 0 to 1 for this town and this question, the same on every machine.</summary>
        static double Draw(string seed, string what) => new Rng($"{seed}::calendar::{what}").Next();

        /// <summary>The days in a school year (most states set 180).</summary>
        public const int SCHOOL_YEAR = 180;

        static readonly Dictionary<(string, int), DateTime> lastDays = new Dictionary<(string, int), DateTime>();

        /// <summary>
        /// The last day of the school year that starts in August of `year`: its 180th school day, counting from the
        /// first (the third Monday of August, or a week later: each town its own).
        /// </summary>
        static DateTime LastDay(string seed, int year, DateTime start)
        {
            lock (lastDays)
                if (lastDays.TryGetValue((seed, year), out var had)) return had;
            var d = start;
            for (int n = 0; ; d = d.AddDays(1))
            {
                if (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday) continue;
                if (Closed(seed, d, year) == null && ++n == SCHOOL_YEAR) break;
            }
            lock (lastDays)
            {
                if (lastDays.Count > 256) lastDays.Clear();
                lastDays[(seed, year)] = d;
            }
            return d;
        }

        /// <summary>
        /// Whether `d` is in the school year that starts in August of `year`, and why not if it isn't: from its first
        /// day (the third Monday of August, or a week later) to its 180th, less its breaks and the holidays it shuts for.
        /// </summary>
        static string OutOfTerm(string seed, DateTime d)
        {
            int year = d.Month >= 8 ? d.Year : d.Year - 1;
            var start = Nth(year, 8, DayOfWeek.Monday, 3).AddDays(Draw(seed, $"start {year}") < 0.5 ? 0 : 7);
            if (d < start || d > LastDay(seed, year, start)) return "Summer holidays";
            return Closed(seed, d, year);
        }

        /// <summary>A weekday in term that the school's shut anyway, and why: a holiday, a break, an in-service day.</summary>
        static string Closed(string seed, DateTime d, int year)
        {
            var h = Holiday(DayOf(d));
            if (h != null && !OfficesOpenOn(h)) return h;
            if (h == "Veterans Day" && Draw(seed, "veterans") < 0.5) return h;
            // Thanksgiving week from the Wednesday; winter break, 23 December to 2 January.
            var thanks = Nth(year, 11, DayOfWeek.Thursday, 4);
            if (d >= thanks.AddDays(-1) && d <= thanks.AddDays(1)) return "Thanksgiving break";
            if ((d.Month == 12 && d.Day >= 23) || (d.Month == 1 && d.Day <= 2)) return "Winter break";
            // Spring break: a week in March or early April, each town its own.
            var spring = Nth(year + 1, 3, DayOfWeek.Monday, 2).AddDays(7 * (int)Math.Floor(Draw(seed, $"spring {year}") * 3));
            if (d >= spring && d < spring.AddDays(5)) return "Spring break";
            // Teacher in-service days: a Friday in October, a Monday in February, the Friday before Memorial Day.
            if (d == Nth(year + 1, 5, DayOfWeek.Monday, -1).AddDays(-3)) return "Teacher in-service day";
            var october = Nth(year, 10, DayOfWeek.Friday, 1 + (int)Math.Floor(Draw(seed, $"inset oct {year}") * 3));
            var february = Nth(year + 1, 2, DayOfWeek.Monday, 1 + (int)Math.Floor(Draw(seed, $"inset feb {year}") * 2));
            if (d == october || d == february) return "Teacher in-service day";
            return null;
        }

        /// <summary>Day `day` in town `seed`'s calendar; `marketPlace`: the town has a plaza for a market.</summary>
        public static TownDay Of(string seed, long day, bool marketPlace = true)
        {
            var d = Date(day);
            long H = TownClock.HOUR;
            var td = new TownDay { day = day, year = d.Year, month = d.Month, date = d.Day, weekday = Weekday(day), holiday = Holiday(day) };
            bool weekend = td.Weekend;
            td.officesOpen = !weekend && (td.holiday == null || OfficesOpenOn(td.holiday));
            td.shopsOpen = td.holiday == null || !ShopsShut(td.holiday);
            bool sunday = td.weekday == 0;
            td.shopsOpenAt = (long)((sunday ? SUNDAY_OPEN : SHOPS_OPEN) * H);
            td.shopsCloseAt = (long)((sunday ? SUNDAY_CLOSE : SHOPS_CLOSE) * H);
            td.bell = (long)(BELLS[(int)Math.Floor(Draw(seed, "bell") * BELLS.Length)] * H);
            td.endBell = td.bell + (long)(SCHOOL_HOURS * H);
            // (Veterans Day: half of schools shut for it, each town its own.)
            string off = weekend ? TownDay.WEEKDAYS[td.weekday] : td.holiday != null && !OfficesOpenOn(td.holiday) ? td.holiday : OutOfTerm(seed, d);
            td.schoolDay = off == null;
            td.noSchool = off;
            // Market day: Saturdays, all year, but not on Christmas Day or New Year's Day.
            if (marketPlace && td.weekday == 6 && !(d.Month == 12 && d.Day == 25) && !(d.Month == 1 && d.Day == 1))
            {
                bool winter = d.Month >= 11 || d.Month <= 3;
                td.market = true;
                td.marketOpen = (long)((winter ? WINTER_OPEN : MARKET_OPEN) * H);
                td.marketClose = (long)((winter ? WINTER_CLOSE : MARKET_CLOSE) * H);
                // How many of the pitches are taken: most in summer, fewer in winter, and some weeks more than others.
                double season = winter ? 0.55 : d.Month >= 6 && d.Month <= 9 ? 0.85 : 0.7;
                td.stalls = Math.Min(1, season + 0.3 * Draw(seed, $"stalls {day}") - 0.1);
            }
            return td;
        }
    }
}

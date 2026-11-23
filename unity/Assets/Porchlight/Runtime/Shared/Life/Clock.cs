// The town clock: one for the whole county, running at real speed. A moment is
// whole milliseconds since 1970 (UTC), the same number everywhere; the time of
// day is that moment in county time: US Eastern, UTC-5, or UTC-4 while daylight
// saving's on (from 2 am on the second Sunday in March to 2 am on the first
// Sunday in November, the US rule since 2007), worked out from the moment alone
// so every machine agrees whatever its own zone. Since it's a function of real
// time, nothing has to tick it or send it: the server, a hosting player and
// Checks all read the same clock. Night is a time of day. Times are said the
// American way, on a 12-hour clock ("8:05 AM"). Pure.

using System;

namespace Porchlight.Life
{
    public static class TownClock
    {
        public const long SECOND = 1000, MINUTE = 60 * SECOND, HOUR = 60 * MINUTE, DAY = 24 * HOUR;
        /// <summary>Eastern Standard Time against UTC (hours); an hour more (-4) in daylight saving time.</summary>
        public const int ZONE_HOURS = -5;
        /// <summary>When it's dark: from dusk to dawn (hours of the day).</summary>
        public const double DUSK = 20, DAWN = 6.5;

        /// <summary>The moment now (ms since 1970, UTC).</summary>
        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>County time against UTC at `moment` (hours): -5, or -4 in daylight saving time.</summary>
        public static int ZoneHours(long moment)
        {
            int year = DateTimeOffset.FromUnixTimeMilliseconds(moment).UtcDateTime.Year;
            // 2 am EST (7 am UTC) on the second Sunday in March; 2 am EDT (6 am UTC) on the first Sunday in November.
            long starts = Sunday(year, 3, 2) + 7 * HOUR, ends = Sunday(year, 11, 1) + 6 * HOUR;
            return moment >= starts && moment < ends ? ZONE_HOURS + 1 : ZONE_HOURS;
        }

        /// <summary>The moment (UTC midnight) of the `nth` Sunday of a month.</summary>
        static long Sunday(int year, int month, int nth)
        {
            var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            int toSunday = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
            return new DateTimeOffset(first.AddDays(toSunday + 7 * (nth - 1))).ToUnixTimeMilliseconds();
        }

        /// <summary>Milliseconds since the county's midnight.</summary>
        public static long TimeOfDay(long moment) => Mod(moment + ZoneHours(moment) * HOUR, DAY);

        /// <summary>Which day it is in the county (days since 1 Jan 1970).</summary>
        public static long DayOf(long moment) => FloorDiv(moment + ZoneHours(moment) * HOUR, DAY);

        /// <summary>The moment that day `day` (DayOf) is `timeOfDay` ms old by the county's clocks.</summary>
        public static long At(long day, long timeOfDay)
        {
            long local = day * DAY + timeOfDay;
            long m = local - ZONE_HOURS * HOUR;
            // In daylight saving time the moment's an hour earlier (and on the changeover days, asked again from there).
            return local - ZoneHours(local - ZoneHours(m) * HOUR) * HOUR;
        }

        /// <summary>The time of day in hours (8.5 is half past eight).</summary>
        public static double Hours(long moment) => TimeOfDay(moment) / (double)HOUR;

        public static bool IsNight(long moment)
        {
            double h = Hours(moment);
            return h >= DUSK || h < DAWN;
        }

        /// <summary>"8:05 AM" for a moment.</summary>
        public static string Format(long moment) => Clock12(TimeOfDay(moment) / SECOND, false);

        /// <summary>"8:05:30 AM" for a moment.</summary>
        public static string FormatSeconds(long moment) => Clock12(TimeOfDay(moment) / SECOND, true);

        /// <summary>Seconds since midnight on a 12-hour clock: "12:00 AM" for midnight, "12:30 PM" for half past noon.</summary>
        public static string Clock12(long seconds, bool withSeconds)
        {
            long h = seconds / 3600 % 24, m = seconds / 60 % 60, s = seconds % 60;
            long h12 = h % 12 == 0 ? 12 : h % 12;
            return withSeconds ? $"{h12}:{m:00}:{s:00} {(h < 12 ? "AM" : "PM")}" : $"{h12}:{m:00} {(h < 12 ? "AM" : "PM")}";
        }

        static long Mod(long a, long m) => ((a % m) + m) % m;

        static long FloorDiv(long a, long m) => (a - Mod(a, m)) / m;
    }
}

// The town clock: one for the whole county, running at real speed. A moment is
// whole milliseconds since 1970 (UTC), the same number everywhere; the time of
// day is that moment in county time (UTC shifted by ZONE_HOURS). Since it's a
// function of real time, nothing has to tick it or send it: the server, a hosting
// player and Checks all read the same clock. Night is a time of day. Pure.

using System;

namespace Porchlight.Life
{
    public static class TownClock
    {
        public const long SECOND = 1000, MINUTE = 60 * SECOND, HOUR = 60 * MINUTE, DAY = 24 * HOUR;
        /// <summary>County time against UTC (hours). A constant, so every machine agrees.</summary>
        public const int ZONE_HOURS = -5;
        /// <summary>When it's dark: from dusk to dawn (hours of the day).</summary>
        public const double DUSK = 20, DAWN = 6.5;

        /// <summary>The moment now (ms since 1970, UTC).</summary>
        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Milliseconds since the county's midnight.</summary>
        public static long TimeOfDay(long moment) => Mod(moment + ZONE_HOURS * HOUR, DAY);

        /// <summary>Which day it is in the county (days since 1 Jan 1970).</summary>
        public static long DayOf(long moment) => FloorDiv(moment + ZONE_HOURS * HOUR, DAY);

        /// <summary>The moment that day `day` (DayOf) is `timeOfDay` ms old.</summary>
        public static long At(long day, long timeOfDay) => day * DAY + timeOfDay - ZONE_HOURS * HOUR;

        /// <summary>The time of day in hours (8.5 is half past eight).</summary>
        public static double Hours(long moment) => TimeOfDay(moment) / (double)HOUR;

        public static bool IsNight(long moment)
        {
            double h = Hours(moment);
            return h >= DUSK || h < DAWN;
        }

        /// <summary>"08:05" for a moment.</summary>
        public static string Format(long moment)
        {
            long t = TimeOfDay(moment) / MINUTE;
            return $"{t / 60:00}:{t % 60:00}";
        }

        /// <summary>"08:05:30" for a moment.</summary>
        public static string FormatSeconds(long moment)
        {
            long t = TimeOfDay(moment) / 1000;
            return $"{t / 3600:00}:{t / 60 % 60:00}:{t % 60:00}";
        }

        static long Mod(long a, long m) => ((a % m) + m) % m;

        static long FloorDiv(long a, long m) => (a - Mod(a, m)) / m;
    }
}

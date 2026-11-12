// Your own steering (Vehicle.Drive with `gentle`), by speed: up at top speed (or on
// boost) a quick tap left or right nudges the car instead of jerking it round, and
// let go just off a street's way it settles onto it; slowed down, the wheel is quick
// and the car turns harder than the autopilot's, and nothing settles it.
//
//   dotnet run --project "unity/Tools~/Checks" -- steering

using System;
using System.Collections.Generic;
using Porchlight.Explore;

static class SteeringCheck
{
    const double DT = 1.0 / 60;
    const double TOP = Vehicle.TOP_SPEED, BOOSTED = Vehicle.TOP_SPEED * Vehicle.CAR_BOOST, SLOW = Vehicle.TOP_SPEED * 0.45;

    public static int Run()
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }
        Func<double, double, bool> open = (x, y) => true;
        double Deg(double r) => r * 180 / Math.PI;
        // Well off the map's grid, so settling onto a street's way doesn't come into it.
        const double OFF = 0.3;

        // Held at `speed`, heading `heading`: `steer` for `secs`, then let go for `after`. How far it turned (degrees).
        double Turned(bool gentle, double speed, double steer, double secs, double after = 1, double heading = OFF)
        {
            var v = new Vehicle(0, 0, 0, heading);
            void Step(double s)
            {
                v.speed = speed;
                v.Drive(0, s, DT, open, speed > TOP, null, gentle);
            }
            for (double t = 0; t < secs; t += DT) Step(steer);
            for (double t = 0; t < after; t += DT) Step(0);
            return Deg(v.heading - heading);
        }

        // Calm at speed: a tap (a tenth of a second) a nudge.
        double oldTap = Turned(false, TOP, 1, 0.1), tap = Turned(true, TOP, 1, 0.1);
        Console.WriteLine($"   at top speed a tap turns the car {tap:0.0}° (the autopilot's wheel: {oldTap:0.0}°)");
        Expect(tap < oldTap / 3, "at top speed a tap is a nudge");
        double oldBoostTap = Turned(false, BOOSTED, 1, 0.1), boostTap = Turned(true, BOOSTED, 1, 0.1);
        Console.WriteLine($"   on boost, {boostTap:0.0}° ({oldBoostTap:0.0}°)");
        Expect(boostTap < oldBoostTap / 4, "on boost a tap is less still");

        // Quick slowed down: a tap does something, a held turn comes round faster than the autopilot's.
        double slowTap = Turned(true, SLOW, 1, 0.1), slowOldTap = Turned(false, SLOW, 1, 0.1);
        Console.WriteLine($"   slowed down ({SLOW:0.0} tiles a second) a tap turns it {slowTap:0.0}° ({slowOldTap:0.0}°)");
        Expect(slowTap > tap * 2.5, "slowed down a tap turns it far more than at speed");
        double slowHalf = Turned(true, SLOW, 1, 0.5, 0), oldHalf = Turned(false, SLOW, 1, 0.5, 0), fastHalf = Turned(true, TOP, 1, 0.5, 0);
        Console.WriteLine($"   held half a second: slowed down {slowHalf:0}° ({oldHalf:0}°), at top speed {fastHalf:0}°");
        Expect(slowHalf > oldHalf * 1.2, "slowed down a held turn comes round quicker than before");
        Expect(slowHalf > fastHalf * 1.4, "and much quicker than at speed");
        // Held a second at top speed: still comes round.
        double held = Turned(true, TOP, 1, 1, 0), oldHeld = Turned(false, TOP, 1, 1, 0);
        Console.WriteLine($"   held a second at top speed: {held:0}° ({oldHeld:0}°)");
        Expect(held > oldHeld * 0.8, "at top speed a held turn still turns the car round");

        // Tapping right then left at speed, to straighten up: about where it was.
        var v2 = new Vehicle(0, 0, 0, OFF);
        foreach (var (s, secs) in new[] { (1.0, 0.1), (-1.0, 0.1), (0.0, 0.5) })
            for (double t = 0; t < secs; t += DT) { v2.speed = TOP; v2.Drive(0, s, DT, open, false, null, true); }
        Console.WriteLine($"   right then left at speed: {Deg(v2.heading - OFF):0.0}° off where it was");
        Expect(Math.Abs(Deg(v2.heading - OFF)) < 3, "a tap right and a tap left come back to about straight");

        // Let go 5° off a street's way: at speed onto it within half a second; slowed down, left alone; 20° off, left alone.
        double near = 5 + Turned(true, TOP, 0, 0, 0.5, 5 * Math.PI / 180);
        Expect(Math.Abs(near) < 1, $"at top speed, let go 5° off the street, the car settles onto it (now {near:0.0}°)");
        double nearSlow = 5 + Turned(true, SLOW, 0, 0, 0.5, 5 * Math.PI / 180);
        Expect(Math.Abs(nearSlow - 5) < 0.01, $"slowed down, it's left where you put it (now {nearSlow:0.0}°)");
        double far = 20 + Turned(true, TOP, 0, 0, 0.5, 20 * Math.PI / 180);
        Expect(Math.Abs(far - 20) < 0.01, $"20° off it's your way, left alone (now {far:0.0}°)");
        // The autopilot and chasers steer as they did.
        Expect(Math.Abs(Turned(false, TOP, 0, 0, 0.5, 5 * Math.PI / 180)) < 1e-9, "without `gentle`, nothing settles");

        Console.WriteLine(fails.Count == 0 ? "steering: all ok" : $"steering: {fails.Count} FAILED");
        return fails.Count == 0 ? 0 : 1;
    }
}

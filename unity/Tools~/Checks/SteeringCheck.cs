// Your own steering (Vehicle.Drive with `gentle`): a quick tap left or right nudges
// the car instead of jerking it round, a held turn still turns it as hard, on boost
// it turns less, and let go just off a street's way it settles onto it.
//
//   dotnet run --project "unity/Tools~/Checks" -- steering

using System;
using System.Collections.Generic;
using Porchlight.Explore;

static class SteeringCheck
{
    const double DT = 1.0 / 60;

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

        // Up to speed (boost or not), heading along +x, then: `steer` for `secs`, then let go for `after`.
        Vehicle Run(bool gentle, bool boost, double steer, double secs, double after = 1, double heading = 0)
        {
            var v = new Vehicle(0, 0, 0, heading);
            for (int i = 0; i < 6 * 60; i++) v.Drive(1, 0, DT, open, boost, null, gentle);
            v.heading = heading;
            for (double t = 0; t < secs; t += DT) v.Drive(1, steer, DT, open, boost, null, gentle);
            for (double t = 0; t < after; t += DT) v.Drive(1, 0, DT, open, boost, null, gentle);
            return v;
        }

        // A tap (a tenth of a second) right, heading well off the grid (so settling onto a street's way doesn't count).
        const double OFF = 0.3;
        double oldTap = Deg(Run(false, false, 1, 0.1, 1, OFF).heading - OFF), tap = Deg(Run(true, false, 1, 0.1, 1, OFF).heading - OFF);
        Console.WriteLine($"   a tap turns the car {tap:0.0}° (it was {oldTap:0.0}°)");
        Expect(tap < oldTap / 3 && tap > 0.5, "a tap is a nudge, a third or less of what it was");
        double oldBoostTap = Deg(Run(false, true, 1, 0.1, 1, OFF).heading - OFF), boostTap = Deg(Run(true, true, 1, 0.1, 1, OFF).heading - OFF);
        Console.WriteLine($"   on boost, {boostTap:0.0}° (it was {oldBoostTap:0.0}°)");
        Expect(boostTap < oldBoostTap / 4, "on boost a tap is less still");

        // Tapping right then left (a tenth of a second each), as you would to straighten up: about where it was.
        var v2 = new Vehicle(0, 0, 0, 0);
        for (int i = 0; i < 6 * 60; i++) v2.Drive(1, 0, DT, open, false, null, true);
        v2.heading = 0.3; // well off the grid, so settling onto a street's way doesn't count
        for (double t = 0; t < 0.1; t += DT) v2.Drive(1, 1, DT, open, false, null, true);
        for (double t = 0; t < 0.1; t += DT) v2.Drive(1, -1, DT, open, false, null, true);
        for (double t = 0; t < 0.5; t += DT) v2.Drive(1, 0, DT, open, false, null, true);
        Console.WriteLine($"   right then left: {Deg(v2.heading - 0.3):0.0}° off where it was");
        Expect(Math.Abs(Deg(v2.heading - 0.3)) < 3, "a tap right and a tap left come back to about straight");

        // Held a second: it still comes round as far, near enough (a quarter turn or more at normal speed).
        double oldHeld = Deg(Run(false, false, 1, 1, 0).heading), held = Deg(Run(true, false, 1, 1, 0).heading);
        Console.WriteLine($"   held a second: {held:0}° (it was {oldHeld:0}°)");
        Expect(held > oldHeld * 0.8, "a held turn still turns the car round");

        // Let go 5° off a street's way: onto it within half a second. 20° off: left alone.
        double near = Deg(Run(true, false, 0, 0, 0.5, 5 * Math.PI / 180).heading);
        Expect(Math.Abs(near) < 1, $"let go 5° off the street, the car settles onto it (now {near:0.0}°)");
        double far = Deg(Run(true, false, 0, 0, 0.5, 20 * Math.PI / 180).heading);
        Expect(Math.Abs(far - 20) < 0.01, $"20° off it's your way, left alone (now {far:0.0}°)");
        // The autopilot and chasers steer as they did.
        Expect(Math.Abs(Deg(Run(false, false, 0, 0, 0.5, 5 * Math.PI / 180).heading) - 5) < 1e-9, "without `gentle`, nothing settles");

        Console.WriteLine(fails.Count == 0 ? "steering: all ok" : $"steering: {fails.Count} FAILED");
        return fails.Count == 0 ? 0 : 1;
    }
}

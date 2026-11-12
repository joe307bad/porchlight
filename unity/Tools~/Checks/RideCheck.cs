// Riding in someone else's car over a town line (Shared/Explore/Ride.cs), as a
// web build and a desktop build see it. The driver's at full boost; the car is
// seen a little in the past; the driver leaves the room a moment after crossing
// the line, and the passenger follows on, over the line, into the next room
// (joining it takes a while) and into the next space (on the web, it may not be
// built yet: you wait at its edge); and a web build stalls for most of a second
// now and then. Through all that the passenger stays in the car, never goes
// backwards, and is beside the driver again once they're in the same room.
//
//   dotnet run --project "unity/Tools~/Checks" -- ride

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;

static class RideCheck
{
    /// <summary>A boosted car (tiles a second).</summary>
    const double SPEED = Vehicle.TOP_SPEED * Vehicle.CAR_BOOST;
    /// <summary>Where the town line is (world tiles), and how far it is from the space you start in's corner to the next one's.</summary>
    const double LINE = 300, NEXT_SPACE = 300;
    /// <summary>How far in the past the car's seen (interpolation and the network), and how long the room takes to say someone's gone (s).</summary>
    const double SEEN_LATE = 0.3, GONE_LATE = 0.1;
    /// <summary>Over the line this long before a tab moves rooms (CountyDrive.ROOM_SETTLE_MS), and seen in the new room this long after joining it (s).</summary>
    const double ROOM_SETTLE = 0.4, FIRST_SIGHT = 0.2;

    sealed class Case
    {
        public string name;
        public double fps, joinSeconds, buildSeconds;
        /// <summary>Stalls: (when, how long) in seconds.</summary>
        public List<(double at, double length)> stalls = new List<(double, double)>();
        /// <summary>The driver stops playing (never seen again) at this time (s); null: they drive on.</summary>
        public double? driverQuits;
    }

    public static int Run()
    {
        int failed = 0;
        var web = new Case { name = "web passenger (30 fps, stalls, slow room join, next town still being built)", fps = 30, joinSeconds = 1.8, buildSeconds = 3.0 };
        web.stalls.Add((LINE / SPEED + 0.2, 0.9));
        web.stalls.Add((LINE / SPEED + 2.5, 0.7));
        var desktop = new Case { name = "desktop passenger (60 fps, quick room join, next town built)", fps = 60, joinSeconds = 0.3, buildSeconds = 0 };
        var slowWeb = new Case { name = "web passenger, very slow room join (4 s)", fps = 30, joinSeconds = 4, buildSeconds = 1 };
        foreach (var c in new[] { web, desktop, slowWeb })
        {
            var r = Ride(c);
            bool ok = !r.gotOut && r.backwards < 0.05 && r.gapAfter < 8 && r.worstGap < 60;
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {c.name}: {(r.gotOut ? $"put out of the car at {r.outAt:0.0} s" : "stayed in")}, went back {r.backwards:0.00} tiles at most, furthest behind {r.worstGap:0.0} tiles, beside the driver again within {r.gapAfter:0.0} tiles");
        }

        // A driver who stops playing, seen by a passenger settled in a room: out of the car in a couple of seconds, not left riding a ghost.
        var quits = new Case { name = "driver stops playing", fps = 30, joinSeconds = 1, buildSeconds = 0, driverQuits = 5 };
        var q = Ride(quits);
        double after = q.outAt - quits.driverQuits.Value;
        bool quitOk = q.gotOut && after > 1.5 && after < 3.5;
        if (!quitOk) failed++;
        Console.WriteLine($"{(quitOk ? "ok" : "FAIL")} {quits.name}: {(q.gotOut ? $"put out of the car {after:0.0} s after" : "still riding")}");

        // The view moving into the next space while the car's out of sight: where it was last seen moves with it.
        var f = new RideFollow();
        f.Seen(new Vec2(290, 10), 0);
        f.Seen(new Vec2(290 + SPEED * 0.1, 10), 100);
        var before = f.Unseen(200, false).Value;
        f.Shift(NEXT_SPACE, 0);
        var then = f.Unseen(233, false).Value;
        double moved = then.x - (before.x - NEXT_SPACE);
        bool shiftOk = moved > 0 && moved < SPEED * 0.05;
        if (!shiftOk) failed++;
        Console.WriteLine($"{(shiftOk ? "ok" : "FAIL")} into the next space while the car's out of sight: on {moved:0.00} tiles in a frame (was {before.x:0.0}, now {then.x + NEXT_SPACE:0.0} in the old space's terms)");

        Console.WriteLine(failed == 0 ? "riding: all ok" : $"riding: {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    struct Result
    {
        public bool gotOut;
        public double outAt, backwards, worstGap, gapAfter;
    }

    static Result Ride(Case c)
    {
        var ride = new RideFollow();
        double t = 0, origin = 0;
        double frame = 1 / c.fps;
        double driverLeaves = LINE / SPEED + ROOM_SETTLE;
        double? crossedAt = null, joinedAt = null;
        double lastWorld = double.NaN;
        var r = new Result();
        bool inB = false;
        double DriverAt(double when) => SPEED * Math.Min(when, c.driverQuits ?? double.PositiveInfinity);
        while (t < LINE / SPEED + 12)
        {
            // A frame (or a stall).
            double dt = frame;
            foreach (var (when, length) in c.stalls) if (t < when && t + dt >= when) dt = length;
            t += dt;
            // Which room you're in: over the line a moment, then joining the next, then in it.
            bool joining = crossedAt != null && t >= crossedAt + ROOM_SETTLE && (joinedAt == null || t < joinedAt);
            if (crossedAt != null && joinedAt == null && t >= crossedAt + ROOM_SETTLE) joinedAt = crossedAt + ROOM_SETTLE + c.joinSeconds;
            if (joinedAt != null && t >= joinedAt) inB = true;
            bool quit = c.driverQuits != null && t >= c.driverQuits + GONE_LATE;
            bool visible = !quit && (inB ? t >= joinedAt + FIRST_SIGHT : !joining && t < driverLeaves + GONE_LATE);
            bool waiting = !double.IsNaN(lastWorld) && lastWorld > LINE && crossedAt != null && t < crossedAt + c.buildSeconds;
            Vec2? at;
            if (visible) at = ride.Seen(new Vec2(DriverAt(t - SEEN_LATE) - origin, 10), t * 1000);
            else at = ride.Unseen(t * 1000, !joining && !waiting);
            if (at == null)
            {
                r.gotOut = true;
                r.outAt = t;
                return r;
            }
            double world = at.Value.x + origin;
            if (!double.IsNaN(lastWorld)) r.backwards = Math.Max(r.backwards, lastWorld - world);
            double gap = DriverAt(t) - world;
            r.worstGap = Math.Max(r.worstGap, gap);
            if (inB && t >= joinedAt + FIRST_SIGHT + 0.5) r.gapAfter = Math.Max(r.gapAfter, gap);
            lastWorld = world;
            if (crossedAt == null && world > LINE) crossedAt = t;
            // Into the next space once it's built: the view (and everything in it) shifts.
            if (origin == 0 && world > LINE && t >= crossedAt + c.buildSeconds)
            {
                origin = NEXT_SPACE;
                ride.Shift(NEXT_SPACE, 0);
            }
        }
        return r;
    }
}

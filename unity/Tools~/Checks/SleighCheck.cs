// The presents mission's flying, as sums (Explore/Sleigh.cs): a ring is flown
// through whichever way you go and however long the frames are, and missed
// past its rim; the story's landing takes your plane on from just where it is,
// as fast and the same way, without a jolt, and sets it down on its spot
// facing the way it's to stand, coming down all the way (over a few hundred
// made-up approaches from every side); the sleigh set down on a roof is along
// its ridge with its runners resting on it, the chimney beside it on the
// street's side; and down a chimney is a jump out of your seat onto the roof,
// across it, up onto the chimney's top and in out of sight, and back up it is
// the same played backwards, ending in your seat. Then the sleigh flown for real (Vehicle, the
// game's own flying) at a ring from a little way off, steered only by where
// the ring is, goes through it.
//
//   dotnet run --project "unity/Tools~/Checks" -- sleigh

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;

static class SleighCheck
{
    public static int Run()
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }

        Rings(Expect);
        Landings(Expect);
        Chimneys(Expect);
        Roofs(Expect);
        FlyThrough(Expect);

        Console.WriteLine(fails.Count == 0 ? "sleigh: all passed" : $"sleigh: {fails.Count} failed");
        return fails.Count == 0 ? 0 : 1;
    }

    static void Rings(Action<bool, string> expect)
    {
        // A ring 4 tiles across, 6 up at (10, 10), facing someone to its west.
        var ring = SkyRing.Facing(10, 10, 6, 4, 0, 10);
        expect(Math.Abs(ring.ax - 1) < 1e-9 && Math.Abs(ring.ay) < 1e-9 && ring.radius == 2, "a ring faces whoever it's hung for: the way through runs from them to it");
        expect(ring.Through(9, 10, 6, 11, 10, 6), "through its middle");
        expect(ring.Through(11, 10, 6, 9, 10, 6), "through its middle the other way");
        expect(ring.Through(9.5, 11.5, 7, 10.5, 11.5, 7), "through it near its rim (1.5 across, 1 up: 1.8 off its middle of 2)");
        expect(ring.Through(9.5, 12.25, 6, 10.5, 12.25, 6), "a near miss just past its rim counts (2.25 off its middle: within the slack)");
        expect(!ring.Through(9.5, 13, 6, 10.5, 13, 6), "past its side: no");
        expect(!ring.Through(9.5, 10, 9, 10.5, 10, 9), "over its top: no");
        expect(!ring.Through(9.5, 10, 3.5, 10.5, 10, 3.5), "under it: no");
        expect(!ring.Through(8, 10, 6, 9.9, 10, 6), "short of it: no");
        expect(!ring.Through(10.1, 10, 6, 12, 10, 6), "already through it: no");
        expect(!ring.Through(9, 9, 6, 9, 11, 6), "alongside its face without crossing it: no");
        // A long frame (a whole second at full boost) still crosses it.
        expect(ring.Through(4, 10.3, 6.2, 15.1, 10.1, 5.9), "a long frame's move clean through it still counts");
        // On the move along its face, at a slant through it.
        expect(ring.Through(9, 8.6, 6, 11, 11.4, 6), "slantwise through it");
        // Right under it, it faces any way, and still works.
        var under = SkyRing.Facing(5, 5, 4, 2, 5, 5);
        expect(!double.IsNaN(under.ax) && Math.Abs(Numbers.Hypot(under.ax, under.ay) - 1) < 1e-9 && under.Through(4.5, 5, 4, 5.5, 5, 4), "hung right over you, it still has a way through");
    }

    static void Landings(Action<bool, string> expect)
    {
        const double DT = 1.0 / 60;
        // Into the street from a plane cruising straight at it, 6 up.
        var l = new Landing(0, 0, 6, 0, Vehicle.PLANE_TOP, 12, 0, 0.03, 0);
        var start = l.At(0);
        expect(Math.Abs(start.x) < 1e-9 && Math.Abs(start.y) < 1e-9 && Math.Abs(start.h - 6) < 1e-9, "it takes over from just where the plane is");
        expect(Math.Abs(start.speed - Vehicle.PLANE_TOP) < 1e-6 && Math.Abs(start.heading) < 1e-6, "as fast as it was going, and the same way: no jolt");
        expect(l.duration >= Landing.MIN_TIME && l.duration <= Landing.MAX_TIME, $"it takes a few seconds ({l.duration:0.0} s)");

        // Hundreds of approaches, from every side and height, onto roofs and into streets.
        var rng = new Rng("sleigh::landings");
        int bad = 0, jolts = 0, ends = 0, ups = 0, fast = 0;
        string first = null;
        for (int k = 0; k < 400; k++)
        {
            double x = rng.Next() * 40 - 20, y = rng.Next() * 40 - 20, h = 3 + rng.Next() * 15;
            double heading = rng.Next() * Math.PI * 2, speed = 3.4 + rng.Next() * 7.7;
            double tx = rng.Next() * 10 - 5, ty = rng.Next() * 10 - 5, th = rng.Chance(0.5) ? 0.03 : 1 + rng.Next() * 2;
            double toHeading = rng.Next() * Math.PI * 2;
            var land = new Landing(x, y, h, heading, speed, tx, ty, th, toHeading);
            double px = x, py = y, ph = h, pheading = heading;
            double peak = speed;
            int steps = 0;
            while (!land.Done && steps++ < 2000)
            {
                var at = land.Step(DT);
                if (double.IsNaN(at.x) || double.IsNaN(at.y) || double.IsNaN(at.h) || double.IsNaN(at.heading)) bad++;
                double moved = Numbers.Hypot(at.x - px, at.y - py);
                // Never further in a frame than three times as fast as it came in (it swings round, not jumps).
                if (moved > Math.Max(speed, 6) * 3 * DT) jolts++;
                if (at.h > ph + 1e-9) ups++;
                peak = Math.Max(peak, at.speed);
                px = at.x;
                py = at.y;
                ph = at.h;
                pheading = at.heading;
            }
            if (Numbers.Hypot(px - tx, py - ty) > 1e-6 || Math.Abs(ph - th) > 1e-6 || Math.Abs(Orbit.ShortestTurn(pheading, toHeading)) > 1e-6 || !land.Done) ends++;
            if (peak > Math.Max(speed, 6) * 3) fast++;
            if (first == null && (ends > 0 || jolts > 0 || bad > 0)) first = $"#{k} from ({x:0.0}, {y:0.0}) {h:0.0} up, heading {heading:0.00} at {speed:0.0}, to ({tx:0.0}, {ty:0.0}) {th:0.00} up facing {toHeading:0.00}";
        }
        expect(bad == 0, "no landing ever goes wild (NaN)");
        expect(ends == 0, "every landing ends exactly on its spot, at its height, facing the way it's to stand");
        expect(jolts == 0, "no frame of any landing jumps");
        expect(fast == 0, "no landing speeds up past three times as fast as it came in");
        expect(ups == 0, "every landing comes down all the way, never up");
        if (first != null) Console.WriteLine($"  first bad one: {first}");
        // Once down, it stays put.
        var done = new Landing(0, 0, 5, 0, 5, 4, 4, 1, Math.PI / 2);
        while (!done.Done) done.Step(0.1);
        var after = done.Step(0.5);
        expect(after.x == 4 && after.y == 4 && after.h == 1 && after.speed == 0, "down, it stays down on its spot, stopped");
        // Its heading as it touches down is the spot's (a turn round onto the street's way, not a skid).
        var nearly = done.At(0.97);
        expect(Math.Abs(Orbit.ShortestTurn(nearly.heading, Math.PI / 2)) < 0.35, "just before it touches down it's already facing the way it's to stand");
    }

    static void Chimneys(Action<bool, string> expect)
    {
        var seat = new Vec2(5, 5);
        var jumpTo = new Vec2(5, 5.2);
        var chimney = new Vec2(5.6, 5.2);
        double roof = 1.6, seatHeight = 1.85, rim = 1.9;
        Func<double, double, double> flat = (x, y) => roof;
        var down = new ChimneyTrip(true, seat, seatHeight, jumpTo, chimney, rim, flat);
        var up = new ChimneyTrip(false, seat, seatHeight, jumpTo, chimney, rim, flat);
        var s0 = down.At(0);
        expect(Geo.Dist(s0.at, seat) < 1e-9 && Math.Abs(s0.floor + s0.lift - seatHeight) < 1e-9 && s0.shown, "down a chimney starts in your seat, in sight");
        var landed = down.At(ChimneyTrip.JUMP);
        expect(Geo.Dist(landed.at, jumpTo) < 1e-9 && Math.Abs(landed.floor - roof) < 1e-9 && Math.Abs(landed.lift) < 1e-9, "you jump out of your seat and land on the roof beside the sleigh");
        double jumpTop = 0;
        for (double t = 0; t <= ChimneyTrip.JUMP; t += 0.01) jumpTop = Math.Max(jumpTop, down.At(t).floor + down.At(t).lift);
        expect(jumpTop > seatHeight, $"jumping up and out of it ({jumpTop:0.00} over your seat's {seatHeight:0.00})");
        var walking = down.At(ChimneyTrip.JUMP + 0.05);
        expect(walking.pace > 0 && Math.Abs(walking.heading - DetMath.Atan2(chimney.x - jumpTo.x, chimney.y - jumpTo.y)) < 1e-9 && walking.lift == 0, "then walking across the roof, towards the chimney");
        var end = down.At(down.Duration);
        expect(Geo.Dist(end.at, chimney) < 1e-9 && !end.shown && end.floor + end.lift < rim, "and ends down inside the chimney, out of sight");
        // Over the chimney's top on the way in: the hop goes higher than its rim.
        double highest = 0;
        int flips = 0;
        bool was = true;
        for (double t = 0; t <= down.Duration; t += 0.01)
        {
            var a = down.At(t);
            if (t > ChimneyTrip.JUMP) highest = Math.Max(highest, a.floor + a.lift);
            if (a.shown != was) flips++;
            was = a.shown;
            if (a.floor + a.lift < rim - ChimneyTrip.DOWN_INTO - 1e-9 || a.lift < -1e-9) flips += 10;
        }
        expect(highest > rim, $"you hop up over the chimney's top ({highest:0.00} over its rim {rim:0.00})");
        expect(flips == 1, "out of sight once, as you drop in, and no lower than the drop");
        // Back up is down played backwards.
        bool same = Math.Abs(up.Duration - down.Duration) < 1e-9;
        for (double t = 0; t <= up.Duration && same; t += 0.05)
        {
            var a = up.At(t);
            var b = down.At(down.Duration - t);
            same = Geo.Dist(a.at, b.at) < 1e-9 && Math.Abs(a.floor + a.lift - b.floor - b.lift) < 1e-9 && a.shown == b.shown;
        }
        expect(same, "back up the chimney is the trip down played backwards");
        var back = up.At(up.Duration);
        expect(Geo.Dist(back.at, seat) < 1e-9 && Math.Abs(back.floor + back.lift - seatHeight) < 1e-9 && back.shown, "ending with a jump back up into your seat, in sight");
        expect(Math.Abs(Orbit.ShortestTurn(back.heading, DetMath.Atan2(seat.x - jumpTo.x, seat.y - jumpTo.y))) < 1e-9, "facing your seat as you jump back into it");
        // Stepped in frames, it gets there.
        var stepped = new ChimneyTrip(true, seat, seatHeight, jumpTo, chimney, rim, flat);
        int frames = 0;
        while (!stepped.Done && frames++ < 1000) stepped.Step(1.0 / 30);
        expect(stepped.Done && frames < 200, $"stepped a frame at a time it's done in a few seconds ({frames} frames)");
        // A chimney right by your seat: no walk, no NaN.
        var close = new ChimneyTrip(true, seat, seatHeight, seat, seat, rim, flat);
        var c = close.At(close.Duration / 2);
        expect(!double.IsNaN(c.at.x) && !double.IsNaN(c.lift) && close.Duration > 0, "a chimney right by your seat is still a jump, a hop and a drop");
        // On a pitched roof you walk on its slope, never in it or over it.
        Func<double, double, double> pitched = (x, y) => 1.8 - 0.6 * Math.Abs(y - 5);
        var slope = new ChimneyTrip(true, seat, 2.05, new Vec2(5, 5.25), new Vec2(5.5, 5.35), 1.9, pitched);
        bool onIt = true;
        for (double t = ChimneyTrip.JUMP; t < ChimneyTrip.JUMP + 0.3; t += 0.02)
        {
            var a = slope.At(t);
            if (a.pace > 0) onIt &= a.lift == 0 && Math.Abs(a.floor - pitched(a.at.x, a.at.y)) < 1e-9;
        }
        expect(onIt, "walking across a pitched roof, your feet are on its slope");
    }

    /// <summary>The sleigh set down on roofs: along the ridge, its runners resting on the roof, the chimney beside it.</summary>
    static void Roofs(Action<bool, string> expect)
    {
        const double LIFT = 0.035;
        // A gable roof 3 long (x 10 to 13) and 2 deep (y 20 to 22), its ridge along x at y 21, 1.8 up, the eaves 1.2.
        Func<double, double, double> gable = (x, y) => x < 10 || x > 13 || y < 20 || y > 22 ? double.NaN : 1.8 - 0.6 * Math.Abs(y - 21);
        var map = new RoofMap(10, 20, 13, 22, 0.1, gable);
        expect(Math.Abs(map.Top - 1.8) < 1e-9, "a roof's top is its ridge");
        expect(Math.Abs(map.At(11.53, 20.47) - gable(11.53, 20.47)) < 0.02, "a roof's height between its samples is near enough the roof's");
        expect(double.IsNaN(map.At(9.5, 21)) && double.IsNaN(map.At(11, 22.5)), "off the roof there's no roof");
        var (middle, along, length) = map.Ridge();
        expect(Math.Abs(along) < 1e-9 && Math.Abs(middle.y - 21) < 0.06 && Math.Abs(middle.x - 11.5) < 0.06 && Math.Abs(length - 3) < 0.01, $"the ridge runs along x through the middle of the roof ({middle.x:0.00}, {middle.y:0.00}, {length:0.0} long)");
        // The same roof turned: its ridge along y.
        Func<double, double, double> turned = (x, y) => gable(10 + (y - 20), 20 + (x - 10));
        var (tm, ta, tl) = new RoofMap(10, 20, 12, 23, 0.1, turned).Ridge();
        expect(Math.Abs(ta - Math.PI / 2) < 1e-9 && Math.Abs(tm.x - 11) < 0.06 && Math.Abs(tl - 3) < 0.01, "turned, the ridge runs along y");

        void Down(string what, RoofMap roof, Func<double, double, double> surface, double fromX, double fromY, Vec2 street)
        {
            var stop = RoofStop.Place(roof, fromX, fromY, street, Catalog.SLEIGH_SEAT, Catalog.SLEIGH_SEAT_HEIGHT,
                Catalog.SLEIGH_RUNNER_FROM, Catalog.SLEIGH_RUNNER_TO, Catalog.SLEIGH_RUNNER_GAUGE, Catalog.SLEIGH_RUNNER_LOW, LIFT);
            var f = new Vec2(Math.Cos(stop.heading), Math.Sin(stop.heading));
            var right = new Vec2(f.y, -f.x);
            double bottom = stop.altitude + LIFT + Catalog.SLEIGH_RUNNER_LOW;
            double highest = double.NegativeInfinity;
            bool sunk = false;
            for (int k = 0; k <= 4; k++)
                foreach (double s in new[] { -Catalog.SLEIGH_RUNNER_GAUGE, Catalog.SLEIGH_RUNNER_GAUGE })
                {
                    double u = Catalog.SLEIGH_RUNNER_FROM + (Catalog.SLEIGH_RUNNER_TO - Catalog.SLEIGH_RUNNER_FROM) * k / 4;
                    double h = surface(stop.middle.x - f.x * u + right.x * s, stop.middle.y - f.y * u + right.y * s);
                    if (double.IsNaN(h)) continue;
                    highest = Math.Max(highest, h);
                    if (h > bottom + 0.02) sunk = true;
                }
            expect(Math.Abs(highest - bottom) < 0.02 && !sunk, $"{what}: the runners rest on the roof (the roof under them {highest:0.000}, their undersides {bottom:0.000}), not sunk into it");
            var back = new Vec2(stop.middle.x - f.x * Catalog.SLEIGH_RUNNER_TO, stop.middle.y - f.y * Catalog.SLEIGH_RUNNER_TO);
            expect(!double.IsNaN(surface(back.x, back.y)) && !double.IsNaN(surface(stop.seat.x, stop.seat.y)), $"{what}: the sleigh is on the roof, its back and all");
            double wantX = roof.Ridge().middle.x - fromX, wantY = roof.Ridge().middle.y - fromY;
            expect(f.x * wantX + f.y * wantY >= 0, $"{what}: it faces along the ridge the way it came in");
            expect(Geo.Dist(stop.seat, new Vec2(stop.middle.x - f.x * Catalog.SLEIGH_SEAT, stop.middle.y - f.y * Catalog.SLEIGH_SEAT)) < 1e-9
                && Math.Abs(stop.seatHeight - (stop.altitude + LIFT + Catalog.SLEIGH_SEAT_HEIGHT)) < 1e-9, $"{what}: Santa's seat is where the sleigh's is");
            double here = surface(stop.chimney.x, stop.chimney.y);
            expect(!double.IsNaN(here) && stop.rim > roof.Top && stop.rim >= here + RoofStop.CHIMNEY_UP - 1e-9, $"{what}: the chimney stands on the roof, its top over the ridge");
            expect(Geo.Dot(new Vec2(stop.chimney.x - stop.seat.x, stop.chimney.y - stop.seat.y), street) > 0, $"{what}: the chimney's on the street's side of the roof");
            expect(!double.IsNaN(surface(stop.jumpTo.x, stop.jumpTo.y)) && Geo.Dist(stop.jumpTo, stop.seat) > Catalog.SLEIGH_RUNNER_GAUGE, $"{what}: Santa jumps out onto the roof, clear of the sleigh");
        }
        Down("on a gable roof, from the west", map, gable, 0, 21, new Vec2(0, -1));
        Down("on a gable roof, from the east", map, gable, 30, 21, new Vec2(0, 1));
        Down("on a turned gable roof", new RoofMap(10, 20, 12, 23, 0.1, turned), turned, 11, 0, new Vec2(1, 0));
        // A short roof: the team runs off its end, but the sleigh stays on.
        Func<double, double, double> shortRoof = (x, y) => x < 10 || x > 11.3 || y < 20 || y > 22 ? double.NaN : 1.8 - 0.6 * Math.Abs(y - 21);
        Down("on a short roof", new RoofMap(10, 20, 11.3, 22, 0.1, shortRoof), shortRoof, 0, 21, new Vec2(0, -1));
        // A flat roof: anywhere's the top.
        Func<double, double, double> flat = (x, y) => x < 4 || x > 7 || y < 4 || y > 6 ? double.NaN : 2.4;
        var flatMap = new RoofMap(4, 4, 7, 6, 0.1, flat);
        var (fm, fa, fl) = flatMap.Ridge();
        expect(Math.Abs(fa) < 1e-9 && Math.Abs(fl - 3) < 0.01 && !double.IsNaN(fm.x), "a flat roof runs the longer way");
        Down("on a flat roof", flatMap, flat, 0, 5, new Vec2(0, 1));
    }

    /// <summary>The sleigh flown at a ring by the game's own flying, steering for it: it goes through.</summary>
    static void FlyThrough(Action<bool, string> expect)
    {
        int model = Array.IndexOf(Catalog.CARS, Catalog.SLEIGH);
        expect(model >= 0 && Catalog.KindOf(model) == VehicleKind.Plane, "the sleigh is a plane");
        expect(Catalog.PlaneLength(Catalog.SLEIGH) > 0 && Catalog.SLEIGH_SEAT < Catalog.PlaneLength(Catalog.SLEIGH) / 2, "Santa sits in the sleigh, behind its middle and in front of its end");
        var rngPick = new Rng("sleigh::pick");
        bool never = true;
        for (int i = 0; i < 2000; i++) never &= Catalog.PickPlaneModel(rngPick) != model;
        expect(never, "the sleigh is never parked at the airport");
        expect(Catalog.IsLook(Catalog.SANTA) && Array.IndexOf(Catalog.CHARACTERS, Catalog.SANTA) < 0, "Santa is a look for missions, not in the character box");

        Func<double, double, bool> open = (x, y) => true;
        Func<double, double, double> floor = (x, y) => 0;
        int through = 0, tries = 0;
        var rng = new Rng("sleigh::fly");
        for (int k = 0; k < 40; k++)
        {
            // From 20 to 40 tiles off, any way round, the ring 6 up.
            double a = rng.Next() * Math.PI * 2, d = 20 + rng.Next() * 20;
            var v = new Vehicle(model, Math.Cos(a) * d, Math.Sin(a) * d, a + Math.PI + (rng.Next() - 0.5)) { altitude = 6 + rng.Next() * 6, speed = Vehicle.TAKEOFF_SPEED };
            var ring = SkyRing.Facing(0, 0, 6, 4.5, v.x, v.y);
            tries++;
            double px = v.x, py = v.y, ph = v.altitude;
            for (int s = 0; s < 60 * 30; s++)
            {
                // Steer for the ring; climb or dive to its height.
                double want = Math.Atan2(-v.y, -v.x);
                double turn = Orbit.ShortestTurn(v.heading, want);
                double steer = Math.Max(-1, Math.Min(1, turn * 3));
                double throttle = v.altitude < ring.h - 0.3 ? 1 : v.altitude > ring.h + 0.3 ? -1 : 0;
                v.Drive(throttle, steer, 1.0 / 30, open, false, floor);
                if (ring.Through(px, py, ph, v.x, v.y, v.altitude))
                {
                    through++;
                    break;
                }
                px = v.x;
                py = v.y;
                ph = v.altitude;
            }
        }
        expect(through == tries, $"the sleigh, steered at it, flies through its ring every time ({through} of {tries})");
    }
}

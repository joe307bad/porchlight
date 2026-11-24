// Does a player's game, following the server's town by its deltas, show the town
// as the server has it, and move it smoothly? Each starter town is run twice from
// its key: once as the server runs it, and once as a player's game makes it,
// following along as the game does. The game gets the whole town first, then only
// the deltas, and draws at 60 frames a second in between (WorldSync.Step).
//
// After each tick the drawn cars and people are compared with the server's (cars
// with where the server had them WorldSync.DELAY before: they're drawn that far
// behind on purpose). Nothing should drift: something left out of the deltas would
// show up here as a growing gap. And every frame each drawn car is checked for
// moving as a car does (Smoothness): no flick of its facing, no surge of speed, no
// sliding sideways at speed.
//
//   dotnet sim/bin/Release/net8.0/porchlight-sim.dll --follow server/towns

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Life;

namespace Porchlight.Sim
{
    /// <summary>
    /// How smoothly the cars a player's game draws move, frame to frame: a car on a curve turns at a rate that
    /// changes gradually (no flicks of its facing), its back axle goes the way it faces (no sliding sideways),
    /// and it changes speed gently (no surges). Boats aren't counted.
    /// </summary>
    sealed class Smoothness
    {
        /// <summary>A flick: its turn rate changing faster than this (rad/s²), a third of a radian a second from one frame to the next.</summary>
        public const double FLICK = 20;
        /// <summary>Sliding: its back axle moving more than this off the way it faces (radians), and sideways faster than a car going SLIDE_FROM would at that.</summary>
        public const double SLIDE = 10 * Math.PI / 180;
        /// <summary>A surge: speeding up or slowing faster than this (tiles/s², about 16 m/s²; a car brakes at 3).</summary>
        public const double SURGE = 4;
        /// <summary>Sliding's counted from this speed (tiles/s): slower, a short car's back axle is too far off where it's taken to be to tell.</summary>
        public const double SLIDE_FROM = 1.2;
        /// <summary>A car's turning when its facing turns faster than this (rad/s).</summary>
        const double TURNING = 0.3;
        /// <summary>How far behind a car's middle its back axle is (tiles), if its length isn't known: half a typical wheelbase.</summary>
        const double BACK = 0.55;
        /// <summary>How far behind each slot's middle its back axle is (tiles): half its wheelbase, as it's drawn (Traffic).</summary>
        public readonly double[] back;

        readonly double[] x, y, h, w, v;
        readonly int[] run;
        /// <summary>Every frame's change of turn rate (rad/s, one frame to the next), moving.</summary>
        readonly List<double> steps = new List<double>();
        public long frames, turning, flicks, fast, slides, surges;

        public Smoothness(int n)
        {
            x = new double[n]; y = new double[n]; h = new double[n]; w = new double[n]; v = new double[n];
            run = new int[n];
            back = new double[n];
            Array.Fill(back, BACK);
        }

        public void Frame(IReadOnlyList<TrafficCar> cars, double dt)
        {
            for (int i = 0; i < Math.Min(cars.Count, x.Length); i++)
            {
                var c = cars[i];
                // Boats (sailing their loops on the water) aren't the town's traffic.
                if (c.hidden || c.taken || c.loop >= 0) { run[i] = 0; continue; }
                double dx = c.x - x[i], dy = c.y - y[i], d = Numbers.Hypot(dx, dy);
                if (run[i] > 0 && d > 1) run[i] = 0; // put somewhere new (a slot handed to another car): start again
                double turn = run[i] > 0 ? Orbit.ShortestTurn(h[i], c.heading) / dt : 0, speed = d / dt;
                if (run[i] >= 2 && speed > 0.3)
                {
                    frames++;
                    if (Math.Abs(turn) > TURNING)
                    {
                        turning++;
                        if (Math.Abs(turn - w[i]) / dt > FLICK) flicks++;
                    }
                    steps.Add(Math.Abs(turn - w[i]));
                    if (speed >= SLIDE_FROM)
                    {
                        fast++;
                        double rx = (c.x - Math.Cos(c.heading) * back[i]) - (x[i] - Math.Cos(h[i]) * back[i]);
                        double ry = (c.y - Math.Sin(c.heading) * back[i]) - (y[i] - Math.Sin(h[i]) * back[i]);
                        // Sideways, and fast enough to see: a lorry swinging round on its back wheels (Traffic) barely moves them.
                        double sideways = Math.Abs(-rx * Math.Sin(c.heading) + ry * Math.Cos(c.heading)) / dt;
                        if (Math.Abs(Orbit.ShortestTurn(c.heading, Math.Atan2(ry, rx))) > SLIDE && sideways > SLIDE_FROM * Math.Sin(SLIDE)) slides++;
                    }
                    if (Math.Abs(speed - v[i]) / dt > SURGE) surges++;
                }
                x[i] = c.x; y[i] = c.y; h[i] = c.heading; w[i] = turn; v[i] = speed;
                run[i]++;
            }
        }

        public double Flicks => Share(flicks, turning);
        public double Slides => Share(slides, fast);
        public double Surges => Share(surges, frames);

        /// <summary>The change of turn rate from one frame to the next (rad/s) that only one frame in a thousand goes over.</summary>
        public double WorstStep
        {
            get
            {
                if (steps.Count == 0) return 0;
                steps.Sort();
                return steps[Math.Min(steps.Count - 1, (int)(steps.Count * 0.999))];
            }
        }

        static double Share(long n, long of) => of == 0 ? 0 : (double)n / of;

        public override string ToString() =>
            $"{frames} moving frames, {turning} turning: flicks {Flicks:0.00%} of turning frames, surges {Surges:0.00%}, " +
            $"sliding {Slides:0.00%} at speed; the turn rate steps {WorstStep:0.00} rad/s a frame at most (p99.9)";
    }

    static class Follow
    {
        /// <summary>Tiles a car or person drawn may be off the server's on average, and at the 99th percentile.</summary>
        const double MEAN = 0.1, P99 = 0.5;
        /// <summary>
        /// The most of a drawn car's frames that may flick round a turn, surge, or slide at speed; and the most its turn
        /// rate may step from one frame to the next (rad/s, p99.9). Drawn by guessing ahead and easing back (as before):
        /// 10–26% flicks, 2–5% surges, up to 4% sliding, 1.7–3.1 rad/s steps.
        /// </summary>
        const double MOST_FLICKS = 0.01, MOST_SURGES = 0.03, MOST_SLIDES = 0.02, MOST_STEP = 0.6;

        public static int Run(string dir)
        {
            var keys = Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f)
                .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString()).ToList();
            bool ok = true;
            foreach (var key in keys)
            {
                string name = key.Split('&')[0];
                long morning = Program.Morning();
                var server = new SimTown(1, key, morning);
                var game = new SimTown(2, key); // the player's own, made from the same key (following, its own life never runs)
                var sync = new WorldSync();
                var gaps = new List<double>();
                int hiddenWrong = 0;
                var look = new Smoothness(server.traffic.cars.Count);
                // Where the server had each car, tick by tick. Compared a tick after the word came (six frames on),
                // so a tick less behind than the game draws them.
                var was = new Queue<(double x, double y, bool hidden)[]>();
                int behind = (int)Math.Round(WorldSync.DELAY / SimTown.TICK) - 1;
                // The traffic lights: what the player's lamps show against what the server's showed as the cars are
                // drawn (DELAY behind the word, a tick back once six frames have gone), and against what the
                // player's own copy of the junctions would show by the clock alone (no early changes: as before).
                var serverLights = StreetFurniture.SignalJunctions(server.traffic.life.net);
                var gameLights = StreetFurniture.SignalJunctions(game.traffic.life.net);
                int lamps = 0, lampsWrong = 0, byClockWrong = 0;
                for (int tick = 0; tick < 600; tick++)
                {
                    server.Step(morning + (tick + 1) * 100);
                    var cars = server.Cars();
                    var people = server.People();
                    var delta = server.delta.Next(cars, people, tick * SimTown.TICK, server.Lights());
                    if (tick == 0) sync.Receive(cars, people);
                    else if (!sync.ReceiveDelta(delta)) { Console.WriteLine($"FAIL {key}: tick {tick}'s delta didn't fit"); ok = false; break; }
                    // Each slot's back axle, by what's in it (a lorry's is well behind its middle; it turns on it).
                    var slots = server.traffic.life.carSlots;
                    for (int i = 0; i < Math.Min(slots.Length, look.back.Length); i++)
                        if (slots[i].who >= 0) look.back[i] = Traffic.WheelBase(server.traffic.life.cars[slots[i].who].length) / 2;
                    // A tenth of a second at 60 frames a second, as the game draws.
                    for (int f = 0; f < 6; f++)
                    {
                        sync.Step(1.0 / 60, game.traffic.cars, game.people);
                        if (tick >= 20) look.Frame(game.traffic.cars, 1.0 / 60);
                    }
                    was.Enqueue(server.traffic.cars.Select(c => (c.x, c.y, c.hidden)).ToArray());
                    if (was.Count > behind + 1) was.Dequeue();
                    if (tick < 20) continue; // settling in from where the game made them
                    long drawn = morning + (tick + 1) * 100 - (long)(1000 * (WorldSync.DELAY - SimTown.TICK));
                    for (int k = 0; k < serverLights.Count; k++)
                        for (int st = 0; st < 2; st++)
                        {
                            int truth = StreetFurniture.Showing(serverLights[k], st, drawn);
                            lamps++;
                            if (sync.Showing(k, st) != truth) lampsWrong++;
                            if (StreetFurniture.Showing(gameLights[k], st, drawn) != truth) byClockWrong++;
                        }
                    var then = was.Peek();
                    for (int i = 0; i < server.traffic.cars.Count; i++)
                    {
                        if (server.traffic.cars[i].taken) continue;
                        // Hidden on the server: hidden here too, wherever it was.
                        if (server.traffic.cars[i].hidden != game.traffic.cars[i].hidden) { hiddenWrong++; continue; }
                        if (server.traffic.cars[i].hidden || then[i].hidden) continue;
                        gaps.Add(Numbers.Hypot(then[i].x - game.traffic.cars[i].x, then[i].y - game.traffic.cars[i].y));
                    }
                    for (int i = 0; i < server.people.Count; i++)
                    {
                        if (server.people[i].hidden != game.people[i].hidden) { hiddenWrong++; continue; }
                        if (server.people[i].hidden) continue;
                        gaps.Add(Numbers.Hypot(server.people[i].x - game.people[i].x, server.people[i].y - game.people[i].y));
                    }
                }
                if (hiddenWrong > 0)
                {
                    Console.WriteLine($"FAIL {name}: {hiddenWrong} times a car or person was shown on one side and hidden on the other");
                    ok = false;
                }
                if (lamps > 0)
                {
                    // Only a light changed early for a waiting car, within the tick before a word, may be off: the
                    // server's own offset has moved by then. Anything more is the player's lamps out of step.
                    bool lit = sync.Lights == serverLights.Count && lampsWrong <= lamps * 0.001;
                    ok &= lit;
                    Console.WriteLine($"{(lit ? "ok  " : "FAIL")} {name}: {serverLights.Count} signals' lamps as the server has them, {100.0 * lampsWrong / lamps:0.00}% of moments off (by the clock alone, as before: {100.0 * byClockWrong / lamps:0.0}%)");
                }
                if (gaps.Count == 0) continue;
                gaps.Sort();
                double mean = gaps.Average(), p99 = gaps[(int)(gaps.Count * 0.99)];
                bool near = mean <= MEAN && p99 <= P99;
                ok &= near;
                Console.WriteLine($"{(near ? "ok  " : "FAIL")} {name}: drawn off the server's by {mean:0.000} tiles on average, {p99:0.000} at p99, {gaps[^1]:0.000} at most");
                bool smooth = look.Flicks <= MOST_FLICKS && look.Surges <= MOST_SURGES && look.Slides <= MOST_SLIDES && look.WorstStep <= MOST_STEP;
                ok &= smooth;
                Console.WriteLine($"{(smooth ? "ok  " : "FAIL")} {name}: {look}");
            }
            return ok ? 0 : 1;
        }
    }
}

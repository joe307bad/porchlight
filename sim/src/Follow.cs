// Does a player's game, following the server's town by its deltas, show the town
// as the server has it? Each starter town is run twice from its key: once as the
// server runs it, and once as a player's game makes it, following along as the
// game does. The game gets the whole town first, then only the deltas, and draws
// at 60 frames a second in between (WorldSync.Step). After each tick the drawn
// cars and people are compared with the server's. Some lag is expected (the game
// eases toward the latest word), but nothing should drift: something left out of
// the deltas would show up here as a growing gap.
//
//   dotnet sim/bin/Release/net8.0/porchlight-sim.dll --follow server/towns

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;

namespace Porchlight.Sim
{
    static class Follow
    {
        /// <summary>Tiles a car or person drawn may be off the server's on average, and at the 99th percentile.</summary>
        const double MEAN = 0.1, P99 = 0.5;

        public static int Run(string dir)
        {
            var keys = Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f)
                .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString()).ToList();
            bool ok = true;
            foreach (var key in keys)
            {
                long morning = Program.Morning();
                var server = new SimTown(1, key, morning);
                var game = new SimTown(2, key); // the player's own, made from the same key (following, its own life never runs)
                var sync = new WorldSync();
                var gaps = new List<double>();
                int hiddenWrong = 0;
                for (int tick = 0; tick < 600; tick++)
                {
                    server.Step(morning + (tick + 1) * 100);
                    var cars = server.Cars();
                    var people = server.People();
                    var delta = server.delta.Next(cars, people, tick * SimTown.TICK);
                    if (tick == 0) sync.Receive(cars, people);
                    else if (!sync.ReceiveDelta(delta)) { Console.WriteLine($"FAIL {key}: tick {tick}'s delta didn't fit"); ok = false; break; }
                    // A tenth of a second at 60 frames a second, as the game draws.
                    for (int f = 0; f < 6; f++) sync.Step(1.0 / 60, game.traffic.cars, game.people);
                    if (tick < 20) continue; // settling in from where the game made them
                    for (int i = 0; i < server.traffic.cars.Count; i++)
                    {
                        if (server.traffic.cars[i].taken) continue;
                        // Hidden on the server: hidden here too, wherever it was.
                        if (server.traffic.cars[i].hidden != game.traffic.cars[i].hidden) { hiddenWrong++; continue; }
                        if (server.traffic.cars[i].hidden) continue;
                        gaps.Add(Numbers.Hypot(server.traffic.cars[i].x - game.traffic.cars[i].x, server.traffic.cars[i].y - game.traffic.cars[i].y));
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
                    Console.WriteLine($"FAIL {key.Split('&')[0]}: {hiddenWrong} times a car or person was shown on one side and hidden on the other");
                    ok = false;
                }
                if (gaps.Count == 0) continue;
                gaps.Sort();
                double mean = gaps.Average(), p99 = gaps[(int)(gaps.Count * 0.99)];
                bool good = mean <= MEAN && p99 <= P99;
                ok &= good;
                Console.WriteLine($"{(good ? "ok  " : "FAIL")} {key.Split('&')[0]}: drawn off the server's by {mean:0.000} tiles on average, {p99:0.000} at p99, {gaps[^1]:0.000} at most");
            }
            return ok ? 0 : 1;
        }
    }
}

// The towns' traffic and townsfolk, run on the server: the Node game server starts
// this (server/src/sim/pool.ts) and talks to it over stdin and stdout, in frames of
// a u32 length (little-endian, of what follows it), a u8 type and the rest:
//
//   in   1 TOWNS    JSON [{ id, key, skeleton? }]: the towns to run (any not listed stop); an edited
//                   town's skeleton (docs/street-editor-api.md) beside its key
//        2 PLAYERS  u16 n, n × (u32 town, u16 count, count × (f32 x, f32 y)): where players are (tiles)
//        3 TAKEN    JSON { id, taken: ["t3", …] }: the cars someone's in
//        4 ASK      JSON { ask, town, kind: car|person|building|ground…, i }: about one car, person or building now
//   out  1 READY    JSON { id, cars, people, roads, roadNames, zoning, buildings } once a town's made (roads: its
//                   streets, for Manage's map, SimTown.Roads; zoning and buildings: what each building
//                   is for, Zoning.cs), or { id, error }
//        2 TICK     f64 when (unix ms), u16 n, n × (u32 town, u16 cars, u16 people,
//                   records (7 bytes each, cars then people), u32 length, delta (Delta.cs))
//        3 LOOKS    JSON { id, looks }: the townsfolk past those the town came with
//        4 STATS    JSON { towns, ticks, avgMs, maxMs }, once a minute
//        5 LIFE     JSON (SimTown.Life): each town's clock, who's indoors where and off the map at each
//                   gate, and the day's figures, once a second
//        6 ANSWER   JSON { ask, answer }: to ASK (answer null: no such town, car, person or building)
//
// Ten ticks a second, each stepping every town. Towns are made off the tick's thread
// (up to a couple of seconds each) and join the ticks once made. Logs go to stderr.
//
//   porchlight-sim                          as the server's worker
//   porchlight-sim --draft                  the street editor's draft worker (Draft.cs): JSON lines
//   porchlight-sim --bench <towns dir> [n]  each town there n times over, stepped for a
//                                           minute: what a tick costs (CI's budget check)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Porchlight.Core;

namespace Porchlight.Sim
{
    static class Program
    {
        const byte IN_TOWNS = 1, IN_PLAYERS = 2, IN_TAKEN = 3, IN_ASK = 4;
        const byte OUT_READY = 1, OUT_TICK = 2, OUT_LOOKS = 3, OUT_STATS = 4, OUT_LIFE = 5, OUT_ANSWER = 6;
        const int LIFE_MS = 1000;
        const int TICK_MS = 100;
        const int STATS_MS = 60_000;

        static readonly ConcurrentQueue<(byte type, byte[] body)> inbox = new ConcurrentQueue<(byte, byte[])>();
        static readonly ConcurrentQueue<SimTown> made = new ConcurrentQueue<SimTown>();
        static readonly Dictionary<int, SimTown> towns = new Dictionary<int, SimTown>();
        /// <summary>
        /// SIM_CLOCK_SHIFT_MS: the towns' clock this far ahead of the real one, for tests (the server's run their
        /// towns at mid-morning whenever CI runs them, not at night with the streets empty). Unset: the real time.
        /// </summary>
        static readonly long clockShift = long.TryParse(Environment.GetEnvironmentVariable("SIM_CLOCK_SHIFT_MS"), out long shift) ? shift : 0;

        /// <summary>The towns' clock now (unix ms).</summary>
        static long ClockNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + clockShift;
        /// <summary>Towns asked for (id → key), made or being made.</summary>
        static readonly Dictionary<int, string> wanted = new Dictionary<int, string>();
        /// <summary>Each town's taken cars as last told (kept for a town still being made).</summary>
        static readonly Dictionary<int, List<string>> takenOf = new Dictionary<int, List<string>>();
        static Stream stdout;
        static readonly object writing = new object();

        static int Main(string[] args)
        {
            // Towns are grown here with every core (Gen.Work): the same towns, sooner.
            Porchlight.Gen.Work.ForEach = (n, body) => System.Threading.Tasks.Parallel.For(0, n, body);
            if (args.Length >= 2 && args[0] == "--follow") return Follow.Run(args[1]);
            // --draft: the street editor's draft worker (Draft.cs), JSON lines on stdin and stdout.
            if (args.Length >= 1 && args[0] == "--draft") return Draft.Run(Console.In, Console.Out);
            // --bench <towns dir> [copies] [budget: most ms a town's tick may average]
            if (args.Length >= 2 && args[0] == "--bench")
                return Bench(args[1], args.Length > 2 ? int.Parse(args[2]) : 1, args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : double.PositiveInfinity);
            stdout = Console.OpenStandardOutput();
            var reader = new Thread(Read) { IsBackground = true, Name = "stdin" };
            reader.Start();
            Log("ready");
            var clock = Stopwatch.StartNew();
            long next = 0;
            double total = 0, max = 0;
            int ticks = 0;
            long statsAt = STATS_MS, lifeAt = LIFE_MS;
            for (;;)
            {
                long wait = next - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                // Fell behind (a long pause): carry on from now, rather than tick to catch up.
                next = Math.Max(next + TICK_MS, clock.ElapsedMilliseconds);
                long t0 = Stopwatch.GetTimestamp();
                if (!Handle()) return 0;
                while (made.TryDequeue(out var town))
                {
                    if (!wanted.TryGetValue(town.id, out var key) || key != town.key) continue; // no longer wanted
                    towns[town.id] = town;
                    if (takenOf.TryGetValue(town.id, out var taken)) town.SetTaken(taken);
                    Send(OUT_READY, Json(new { id = town.id, cars = town.traffic.cars.Count, people = town.people.Count, roads = town.Roads(), roadNames = town.RoadNames(),
                        zoning = new { homes = town.zoning.homes, residents = town.zoning.residents, workers = town.zoning.workers, jobs = town.zoning.jobs },
                        buildings = Porchlight.Explore.Zoning.Encode(town.zoning) }));
                }
                Tick();
                if (clock.ElapsedMilliseconds >= lifeAt)
                {
                    lifeAt = clock.ElapsedMilliseconds + LIFE_MS;
                    foreach (var town in towns.Values) Send(OUT_LIFE, Json(town.Life()));
                }
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                total += ms;
                max = Math.Max(max, ms);
                ticks++;
                if (clock.ElapsedMilliseconds >= statsAt)
                {
                    statsAt = clock.ElapsedMilliseconds + STATS_MS;
                    Send(OUT_STATS, Json(new { towns = towns.Count, ticks, avgMs = Math.Round(total / Math.Max(1, ticks), 3), maxMs = Math.Round(max, 3) }));
                    total = max = 0;
                    ticks = 0;
                }
            }
        }

        /// <summary>Everything that came in since the last tick. False once the server's gone (stdin closed).</summary>
        static bool Handle()
        {
            while (inbox.TryDequeue(out var m))
            {
                if (m.body == null) return false;
                try
                {
                    switch (m.type)
                    {
                        case IN_TOWNS: SetTowns(m.body); break;
                        case IN_PLAYERS: SetPlayers(m.body); break;
                        case IN_TAKEN: SetTaken(m.body); break;
                        case IN_ASK: Ask(m.body); break;
                        default: Log($"unknown message {m.type}"); break;
                    }
                }
                catch (Exception e)
                {
                    Log($"message {m.type}: {e.Message}");
                }
            }
            return true;
        }

        static void SetTowns(byte[] body)
        {
            var list = JsonSerializer.Deserialize<List<TownSpec>>(body) ?? new List<TownSpec>();
            var keep = new HashSet<int>();
            foreach (var t in list)
            {
                keep.Add(t.id);
                // An edited town's skeleton (docs/street-editor-api.md), before the town is grown from its key.
                if (t.skeleton is JsonElement sk && sk.ValueKind == JsonValueKind.Object) Porchlight.Model.Skeletons.Put(t.key, sk.GetRawText());
                if (wanted.TryGetValue(t.id, out var had) && had == t.key) continue;
                wanted[t.id] = t.key;
                towns.Remove(t.id);
                int id = t.id;
                string key = t.key;
                Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var town = new SimTown(id, key);
                        // Started here too (its life run up to now from a little before), not in the first tick: that
                        // takes a while in a big town, and every other town's tick would wait on it.
                        town.Step(ClockNow());
                        Log($"town {id} made in {sw.ElapsedMilliseconds} ms: {town.traffic.cars.Count} cars, {town.people.Count} people");
                        made.Enqueue(town);
                    }
                    catch (Exception e)
                    {
                        Log($"town {id} couldn't be made: {e}");
                        Send(OUT_READY, Json(new { id, error = e.Message }));
                    }
                });
            }
            foreach (var id in wanted.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                wanted.Remove(id);
                towns.Remove(id);
                takenOf.Remove(id);
            }
        }

        static void SetPlayers(byte[] b)
        {
            var seen = new HashSet<int>();
            int o = 0;
            int n = BitConverter.ToUInt16(b, o);
            o += 2;
            for (int i = 0; i < n; i++)
            {
                int id = (int)BitConverter.ToUInt32(b, o);
                int count = BitConverter.ToUInt16(b, o + 4);
                o += 6;
                var at = new List<Vec2>(count);
                for (int k = 0; k < count; k++, o += 8) at.Add(new Vec2(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4)));
                seen.Add(id);
                if (towns.TryGetValue(id, out var town)) town.SetObstacles(at);
            }
            // Not listed: nobody's there.
            foreach (var town in towns.Values) if (!seen.Contains(town.id)) town.SetObstacles(new List<Vec2>());
        }

        static void SetTaken(byte[] body)
        {
            var m = JsonSerializer.Deserialize<TakenSpec>(body);
            if (m == null) return;
            takenOf[m.id] = m.taken ?? new List<string>();
            if (towns.TryGetValue(m.id, out var town)) town.SetTaken(takenOf[m.id]);
        }

        /// <summary>About one car, person or building in a town now (the debug view: /mp/sim/where, Manage's hover).</summary>
        static void Ask(byte[] body)
        {
            var q = JsonSerializer.Deserialize<AskSpec>(body);
            if (q == null) return;
            object answer = null;
            if (towns.TryGetValue(q.town, out var town))
            {
                var life = town.life;
                if (q.kind == "car") answer = life.AboutCar(q.i);
                else if (q.kind == "person")
                {
                    // The town's people slots come after its wanderers.
                    int slot = q.i - (town.basePeople - life.personSlots.Length);
                    answer = slot >= 0 ? life.AboutPersonSlot(slot) : new { wanderer = true };
                }
                else if (q.kind == "focus-car" || q.kind == "focus-person") answer = town.Focus(q.kind == "focus-car", q.by == "slot", q.i);
                else if (q.kind == "ground") answer = town.Ground();
                else if (q.kind == "signals") answer = town.Signals(q.i == 1);
                else if (q.kind == "congestion") answer = town.Congestion(q.i);
                else if (q.kind == "brief-car" || q.kind == "brief-person") answer = town.Focus(q.kind == "brief-car", q.by == "slot", q.i, brief: true);
                else if (q.kind == "building")
                {
                    int id = q.i >= 0 && q.i < town.zoning.buildings.Count ? town.zoning.buildings[q.i].buildingId : -1;
                    if (id >= 0) answer = new { building = q.i, inside = life.Inside(id) };
                }
            }
            Send(OUT_ANSWER, Json(new { ask = q.ask, answer }));
        }

        sealed class AskSpec
        {
            public int ask { get; set; }
            public int town { get; set; }
            public string kind { get; set; }
            public int i { get; set; }
            /// <summary>focus-*: "slot" (`i` is where it's drawn: a car's or person's place in the tick's records) or "id" (who: the town's life's own).</summary>
            public string by { get; set; }
        }

        /// <summary>Every town a tenth of a second on, and what each now is sent.</summary>
        static void Tick()
        {
            if (towns.Count == 0) return;
            double now = ClockNow();
            var parts = new List<byte[]>(towns.Count);
            int size = 10;
            foreach (var town in towns.Values)
            {
                try
                {
                    town.Step((long)now);
                }
                catch (Exception e)
                {
                    // One town's trouble isn't every town's: it stands still this tick.
                    Log($"town {town.id}: {e.Message}");
                }
                if (town.looksChanged)
                {
                    town.looksChanged = false;
                    Send(OUT_LOOKS, Json(new { id = town.id, looks = town.ExtraLooks() }));
                }
                var cars = town.Cars();
                var people = town.People();
                var delta = town.delta.Next(cars, people, now / 1000, town.Lights());
                var part = new byte[4 + 2 + 2 + cars.Length + people.Length + 4 + delta.Length];
                int o = 0;
                BitConverter.TryWriteBytes(part.AsSpan(o), (uint)town.id); o += 4;
                BitConverter.TryWriteBytes(part.AsSpan(o), (ushort)(cars.Length / Delta.RECORD)); o += 2;
                BitConverter.TryWriteBytes(part.AsSpan(o), (ushort)(people.Length / Delta.RECORD)); o += 2;
                cars.CopyTo(part, o); o += cars.Length;
                people.CopyTo(part, o); o += people.Length;
                BitConverter.TryWriteBytes(part.AsSpan(o), (uint)delta.Length); o += 4;
                delta.CopyTo(part, o);
                parts.Add(part);
                size += part.Length;
            }
            var body = new byte[size];
            BitConverter.TryWriteBytes(body.AsSpan(0), now);
            BitConverter.TryWriteBytes(body.AsSpan(8), (ushort)parts.Count);
            int at = 10;
            foreach (var p in parts)
            {
                p.CopyTo(body, at);
                at += p.Length;
            }
            Send(OUT_TICK, body);
        }

        static void Read()
        {
            var stdin = Console.OpenStandardInput();
            var head = new byte[5];
            try
            {
                for (;;)
                {
                    if (!Fill(stdin, head)) break;
                    int length = (int)BitConverter.ToUInt32(head, 0) - 1;
                    if (length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException($"a frame of {length} bytes");
                    var body = new byte[length];
                    if (!Fill(stdin, body)) break;
                    inbox.Enqueue((head[4], body));
                }
            }
            catch (Exception e)
            {
                Log("reading: " + e.Message);
            }
            inbox.Enqueue((0, null)); // the server's gone: stop
        }

        static bool Fill(Stream s, byte[] b)
        {
            int have = 0;
            while (have < b.Length)
            {
                int n = s.Read(b, have, b.Length - have);
                if (n <= 0) return false;
                have += n;
            }
            return true;
        }

        static void Send(byte type, byte[] body)
        {
            var head = new byte[5];
            BitConverter.TryWriteBytes(head.AsSpan(0), (uint)(body.Length + 1));
            head[4] = type;
            lock (writing)
            {
                stdout.Write(head, 0, 5);
                stdout.Write(body, 0, body.Length);
                stdout.Flush();
            }
        }

        static byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);

        /// <summary>8:15 on a fixed weekday: the morning's rush (for the bench and the follow check).</summary>
        public static long Morning() =>
            Porchlight.Life.TownClock.At(Porchlight.Life.TownClock.DayOf(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()), 8 * Porchlight.Life.TownClock.HOUR + 15 * Porchlight.Life.TownClock.MINUTE);

        static void Log(string line) => Console.Error.WriteLine("sim: " + line);

        sealed class TownSpec
        {
            public int id { get; set; }
            public string key { get; set; }
            /// <summary>The skeleton of the key's revision, for a key with `rev` above 0; else absent.</summary>
            public JsonElement? skeleton { get; set; }
        }

        sealed class TakenSpec
        {
            public int id { get; set; }
            public List<string> taken { get; set; }
        }

        // --- What a tick costs -------------------------------------------------------------

        static int Bench(string dir, int copies, double budget)
        {
            var keys = Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f)
                .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString()).ToList();
            var sw = Stopwatch.StartNew();
            // At the morning's busiest, the same morning every time.
            long peak = Morning();
            var made = keys.Select((k, i) => new SimTown(i, k, peak)).ToList();
            Console.WriteLine($"{made.Count} towns made in {sw.ElapsedMilliseconds} ms ({string.Join(", ", made.Select(t => $"{t.traffic.cars.Count}+{t.people.Count}"))})");
            // More towns: the same ones again (each its own, stepped separately).
            var all = new List<SimTown>();
            for (int c = 0; c < copies; c++) all.AddRange(c == 0 ? made : keys.Select((k, i) => new SimTown(c * 1000 + i, k, peak)));
            // Players in each, for the cars to stop for: a few standing in its busiest lanes.
            foreach (var t in all)
                t.SetObstacles(t.life.net.lanes.Where(l => l.dir > 0 && l.rank == 2).Take(3).Select(l => t.life.net.LanePoint(l, l.start + l.Span / 2).at).ToList());
            for (int i = 0; i < 50; i++) foreach (var t in all) t.Step();
            var times = new List<double>();
            long deltaBytes = 0, fullBytes = 0;
            for (int i = 0; i < 600; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                foreach (var t in all)
                {
                    t.Step();
                    var cars = t.Cars();
                    var people = t.People();
                    fullBytes += cars.Length + people.Length;
                    deltaBytes += t.delta.Next(cars, people, i * SimTown.TICK, t.Lights()).Length;
                }
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            times.Sort();
            double avg = times.Average();
            Console.WriteLine($"{all.Count} towns, a tick: avg {avg:0.000} ms, p95 {times[(int)(times.Count * 0.95)]:0.000} ms, max {times[^1]:0.000} ms ({avg / all.Count:0.000} ms a town)");
            Console.WriteLine($"sent a tick: in full {fullBytes / 600.0 / all.Count:0} B a town, as deltas {deltaBytes / 600.0 / all.Count:0} B a town ({100.0 * deltaBytes / fullBytes:0}%)");
            Console.WriteLine($"memory: {GC.GetTotalMemory(true) / 1048576.0:0} MB");
            Console.WriteLine($"on the road at {Porchlight.Life.TownClock.Format(made[0].life.Now)}: {string.Join(", ", made.Select(t => $"{t.life.CarsOnRoad()} cars, {t.life.OutNow()} out"))}");
            // Each tick has a tenth of a second; a town's share of it on the box is far less (and every
            // town in every county shares the one worker): over budget, CI fails.
            if (avg / all.Count > budget)
            {
                Console.WriteLine($"over budget: {avg / all.Count:0.000} ms a town, budget {budget} ms");
                return 1;
            }
            if (deltaBytes > fullBytes * 0.6)
            {
                Console.WriteLine("the deltas aren't saving enough: over 60% of the whole");
                return 1;
            }
            return 0;
        }
    }
}

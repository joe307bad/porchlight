// Checks the game's engine-free code (Assets/Porchlight/Runtime/Shared) on
// plain .NET, without Unity:
//
//   dotnet run --project "unity/Tools~/Checks" -- towns [count]     # grow towns with every invariant checked
//   dotnet run --project "unity/Tools~/Checks" -- net '<json>'      # play the real game server (NetCheck.cs)
//   dotnet run --project "unity/Tools~/Checks" -- mission <file>    # play a compiled mission (MissionCheck.cs)
//   dotnet run --project "unity/Tools~/Checks" -- chase [count]     # set chasers after someone (ChaseCheck.cs)
//   dotnet run --project "unity/Tools~/Checks" -- homes             # the town that comes with the game has houses to live in, and is up to date (from the repo's root)
//   dotnet run --project "unity/Tools~/Checks" -- town <file> [key] # regenerate it into its file (`npm run town` in server/)
//   dotnet run --project "unity/Tools~/Checks" -- measure <seed> 'width=256&riverCount=0'   # how big and built up a town is
//   dotnet run --project "unity/Tools~/Checks" -- county [vectors-file] # the county: its spaces, highways and rooms (CountyCheck.cs)
//   dotnet run --project "unity/Tools~/Checks" -- trips <key> [key] [key] # directions to a house in each of these towns (your slots), driven on the roads
//   dotnet run --project "unity/Tools~/Checks" -- traffic <seed> 'width=256&riverCount=0'   # how smooth its traffic looks following a host, as the host slows down

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Porchlight.Gen;
using Porchlight.Model;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "net") return NetCheck.Run(args[1]);
        if (args.Length > 1 && args[0] == "mission") return MissionCheck.Run(args[1]);
        if (args.Length > 0 && args[0] == "chase") return ChaseCheck.Run(args.Length > 1 ? int.Parse(args[1]) : 40);
        if (args.Length > 0 && args[0] == "homes") return HomesCheck();
        if (args.Length > 0 && args[0] == "course") return CourseCheck.Run(System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText("server/towns/porchlight.json")).RootElement.GetProperty("key").GetString(), args.Length > 1 ? args[1] : null);
        if (args.Length > 2 && args[0] == "measure") return Measure(args[1], args[2]);
        if (args.Length > 1 && args[0] == "town") return RegenerateTown(args[1], args.Length > 2 ? args[2] : null);
        if (args.Length > 2 && args[0] == "traffic") return TrafficCheck(args[1], args[2]);
        if (args.Length > 0 && args[0] == "county") return CountyCheck.Run(args.Length > 1 ? args[1] : null);
        if (args.Length > 1 && args[0] == "trips") return CountyCheck.Trips(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "towns") return Towns(args.Length > 1 ? int.Parse(args[1]) : 50);
        Console.Error.WriteLine("usage: towns [count] | course [picture dir] | chase [count] | net '<json from Tools~/net/server.ts>' | mission <compiled mission json>");
        return 2;
    }

    /// <summary>
    /// The towns that come with the game (the server's STARTER_TOWNS) and a few more:
    /// plenty of houses, each found again by the tile it's known by, none sharing it, and a zoo for the chase.
    /// </summary>
    /// <summary>One town, by its settings ("width=256&amp;riverCount=0"): how big, how built up, and any water.</summary>
    static int Measure(string seed, string settings)
    {
        var d = new Dictionary<string, double>();
        foreach (var kv in settings.Split('&', StringSplitOptions.RemoveEmptyEntries)) d[kv.Split('=')[0]] = double.Parse(kv.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
        var sw = Stopwatch.StartNew();
        var m = Generator.Generate(seed, GenParams.Normalize(d), check: true);
        double gen = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var layout = Porchlight.Explore.Layout.Build(m);
        double lay = sw.Elapsed.TotalSeconds;
        var houses = Porchlight.Explore.Homes.All(m);
        int built = m.buildings.Sum(b => b.tiles.Count);
        int water = m.heightmap.Count(h => h <= m.waterLevel);
        string bad = Ports(m, layout) ?? Zoo(m, layout) ?? CulDeSacs(m, layout) ?? Turning(layout);
        Console.WriteLine($"{m.width}x{m.height} buildings {m.buildings.Count} houses {houses.Count} built {100.0 * built / (m.width * m.height):0.0}% lots {m.lots.Count} roads {m.roads.edges.Count} cul-de-sacs {Raster.Bulbs(m.roads.edges).Count} water {water} rivers {m.rivers.Count} bridges {m.bridges.Count} docks {m.docks.Count} zoo {(m.zoo != null ? "yes" : "no")} airport {(m.airport != null ? "yes" : "no")} cars {layout.cars.Count} gen {gen:0.00}s layout {lay:0.00}s {(bad == null ? "ok" : "FAIL " + bad)}");
        return bad == null ? 0 : 1;
    }

    /// <summary>
    /// How smoothly a town's traffic looks on a client following a host (at 60 frames a second)
    /// as the host's frame rate falls: a window in the background gets about one frame a second.
    /// </summary>
    static int TrafficCheck(string seed, string settings)
    {
        var d = new Dictionary<string, double>();
        foreach (var kv in settings.Split('&', StringSplitOptions.RemoveEmptyEntries)) d[kv.Split('=')[0]] = double.Parse(kv.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
        var m = Generator.Generate(seed, GenParams.Normalize(d));
        var layout = Porchlight.Explore.Layout.Build(m);
        const double FRAME = 1 / 60.0, CRUISE = 2.4, SECONDS = 120;
        foreach (double hostFps in new[] { 60.0, 15, 5, 2.5, 2, 1 })
        {
            Porchlight.Explore.Traffic Make() => new Porchlight.Explore.Traffic(layout, new Porchlight.Core.Rng(seed + "::traffic"), r => 0);
            var host = Make();
            var follow = Make();
            var sync = new Porchlight.Explore.WorldSync();
            var noPeople = new List<Porchlight.Explore.Walker>();
            var none = new List<Porchlight.Core.Vec2>();
            int n = host.cars.Count;
            var at = follow.cars.Select(c => (c.x, c.y)).ToArray();
            long frames = 0, still = 0, back = 0, jump = 0;
            double nextFrame = 0, nextSend = 0;
            for (double t = 0; t < SECONDS; t += FRAME)
            {
                if (t >= nextFrame)
                {
                    // As the game does: at most half a second of the town to a frame, and a world message every 0.1 s at most.
                    for (double left = Math.Min(0.5, 1 / hostFps); left > 1e-9; left -= Math.Min(0.05, left)) host.Update(Math.Min(0.05, left), none);
                    nextFrame += 1 / hostFps;
                    if (t >= nextSend)
                    {
                        sync.Receive(Porchlight.Explore.WorldSync.EncodeCars(host.cars), Porchlight.Explore.WorldSync.EncodePeople(noPeople));
                        nextSend = t + 0.1;
                    }
                }
                sync.Step(FRAME, follow.cars, noPeople);
                frames++;
                for (int i = 0; i < n; i++)
                {
                    var f = follow.cars[i];
                    double dx = f.x - at[i].x, dy = f.y - at[i].y, moved = Math.Sqrt(dx * dx + dy * dy);
                    if (moved < CRUISE * FRAME * 0.2) still++;
                    if (moved > CRUISE * FRAME * 2) jump++;
                    if (dx * Math.Cos(f.heading) + dy * Math.Sin(f.heading) < -CRUISE * FRAME * 0.05) back++;
                    at[i] = (f.x, f.y);
                }
            }
            double per = 100.0 / (frames * n);
            Console.WriteLine($"host at {hostFps,4:0.#} fps: follower sees cars stopped {still * per,5:0.0}%, too fast {jump * per,5:0.0}%, going backwards {back * per,5:0.0}%");
        }
        return 0;
    }

    /// <summary>A town key's seed and settings (what the game's ParseUrlState does, for keys with nothing to unescape).</summary>
    internal static (string seed, GenParams settings) ParseKey(string key)
    {
        var settings = new Dictionary<string, double>();
        string seed = null;
        foreach (var kv in key.Split('&'))
        {
            if (kv.StartsWith("seed=")) seed = kv.Substring(5);
            else settings[kv.Split('=')[0]] = double.Parse(kv.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        return (seed, GenParams.Normalize(settings));
    }

    /// <summary>The key as the game would write it (EncodeUrlState): anything else would be another room.</summary>
    internal static string WrittenKey(string seed, GenParams p)
    {
        var written = new List<string> { "seed=" + seed };
        foreach (var spec in ParamSpec.All)
            if (p[spec.key] != GenParams.Defaults[spec.key]) written.Add(spec.key + "=" + Porchlight.Core.Numbers.NumberToString(p[spec.key]));
        return string.Join("&", written);
    }

    /// <summary>Generate a town and check it has what the game needs: its houses' tiles, or why it won't do.</summary>
    static (List<int[]> houses, string error) StarterHouses(string key)
    {
        var (seed, settings) = ParseKey(key);
        if (WrittenKey(seed, settings) != key) return (null, $"the game writes this key as {WrittenKey(seed, settings)}");
        var model = Generator.Generate(seed, settings, check: true);
        var houses = Porchlight.Explore.Homes.All(model);
        int lost = houses.Count(b => { var a = Porchlight.Explore.Homes.Anchor(b); return Porchlight.Explore.Homes.At(houses, a.x, a.y) != b; });
        if (houses.Count < 10 || lost != 0 || model.zoo == null) return (null, $"{houses.Count} houses, {lost} not found by their tile, zoo {(model.zoo != null ? "yes" : "no")}");
        return (houses.Select(b => { var a = Porchlight.Explore.Homes.Anchor(b); return new[] { a.x, a.y }; }).ToList(), null);
    }

    static string Quoted(string text) => System.Text.Json.JsonSerializer.Serialize(text, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    static string HousesJson(IEnumerable<int[]> houses) => string.Join(",", houses.Select(h => $"[{h[0]},{h[1]}]"));

    /// <summary>
    /// Regenerate the town that comes with the game (optionally from a new key) into its file. When the
    /// server next starts and finds the file changed, it gives everyone who lived there a new house.
    /// </summary>
    static int RegenerateTown(string file, string newKey)
    {
        var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file)).RootElement;
        string name = doc.GetProperty("name").GetString();
        string key = newKey ?? doc.GetProperty("key").GetString();
        var (houses, error) = StarterHouses(key);
        if (error != null)
        {
            Console.WriteLine($"FAIL {name}: {error}. {file} left as it was.");
            return 1;
        }
        var was = doc.GetProperty("houses").EnumerateArray().Select(h => new[] { h[0].GetInt32(), h[1].GetInt32() }).ToList();
        bool same = key == doc.GetProperty("key").GetString() && HousesJson(was) == HousesJson(houses);
        string place = doc.TryGetProperty("place", out var pl) ? $"\n  \"place\": {Quoted(pl.GetString())}," : "";
        System.IO.File.WriteAllText(file, $"{{\n  \"name\": {Quoted(name)},{place}\n  \"key\": {Quoted(key)},\n  \"houses\": [{HousesJson(houses)}]\n}}\n");
        Console.WriteLine(same
            ? $"{name}: no change ({houses.Count} houses)."
            : $"{name}: {houses.Count} houses, was {was.Count}. Everyone there gets a new house when the server next starts with this file.");
        return 0;
    }

    static int HomesCheck()
    {
        int failed = 0;
        // The town that comes with the game: its file has to be what the generator makes now.
        const string FILE = "server/towns/porchlight.json";
        var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(FILE)).RootElement;
        var (starter, error) = StarterHouses(doc.GetProperty("key").GetString());
        var listed = doc.GetProperty("houses").EnumerateArray().Select(h => new[] { h[0].GetInt32(), h[1].GetInt32() });
        if (error == null && HousesJson(listed) != HousesJson(starter)) error = "the generator has changed since it was saved: run `npm run town` in server/ to regenerate it and rehouse everyone";
        Console.WriteLine($"{(error == null ? "ok" : "FAIL")} {doc.GetProperty("name").GetString()}: {(error ?? starter.Count + " houses")}");
        if (error != null) failed++;
        foreach (string seed in new[] { "a1b2c3d4e5f6", "0f9e8d7c6b5a", "123456789abc" })
        {
            var model = Generator.Generate(seed, GenParams.Normalize(new Dictionary<string, double>()), check: true);
            var houses = Porchlight.Explore.Homes.All(model);
            int lost = houses.Count(b => { var a = Porchlight.Explore.Homes.Anchor(b); return Porchlight.Explore.Homes.At(houses, a.x, a.y) != b; });
            bool ok = houses.Count >= 10 && lost == 0 && model.zoo != null;
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {seed}: {houses.Count} houses, {lost} not found by their tile, zoo {(model.zoo != null ? "yes" : "no")}");
            if (!ok) failed++;
        }
        return failed == 0 ? 0 : 1;
    }
    static int Towns(int count)
    {
        int failed = 0;
        long buildings = 0, driveways = 0, beds = 0, parked = 0, airports = 0, zoos = 0, planes = 0, docks = 0, boats = 0, loops = 0, culs = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            string seed = $"check-{i}";
            var p = new GenParams { width = 64 + (i % 4) * 32, height = 64 + ((i / 4) % 4) * 32, roadDensity = 0.5 + (i % 5) * 0.1, frontSetback = (i / 2) % 4 };
            p = GenParams.Normalize(p);
            try
            {
                var a = Generator.Generate(seed, p, check: true);
                var b = Generator.Generate(seed, p);
                // The street scene too: parking lanes, driveways and gardens.
                var layout = Porchlight.Explore.Layout.Build(a);
                buildings += a.buildings.Count;
                culs += Raster.Bulbs(a.roads.edges).Count;
                parked += layout.cars.Count;
                if (a.airport != null) airports++;
                if (a.zoo != null) zoos++;
                docks += a.docks.Count;
                loops += layout.boatLoops.Count;
                foreach (var c in layout.cars)
                {
                    var kind = Porchlight.Explore.Catalog.KindOf(c.vehicle - 1);
                    if (c.vehicle > 0 && kind == Porchlight.Explore.VehicleKind.Plane) planes++;
                    if (c.vehicle > 0 && kind == Porchlight.Explore.VehicleKind.Boat) boats++;
                }
                string ports = Ports(a, layout);
                if (ports != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}: {ports}");
                }
                foreach (byte u in layout.use)
                {
                    if (u == Porchlight.Explore.Use.DRIVEWAY) driveways++;
                    if (u == Porchlight.Explore.Use.GARDEN) beds++;
                }
                string zoo = Zoo(a, layout);
                if (zoo != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}: {zoo}");
                }
                string culs2 = CulDeSacs(a, layout);
                if (culs2 != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}: {culs2}");
                }
                string turning = Turning(layout);
                if (turning != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}: traffic: {turning}");
                }
                string shared = i % 4 == 0 ? Shared(a, layout) : null;
                if (shared != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}: shared town: {shared}");
                }
                string why = Same(a, b);
                if (why != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}: not repeatable ({why})");
                }
            }
            catch (InvariantException e)
            {
                failed++;
                Console.WriteLine($"FAIL {seed}: {e.Message}");
            }
        }
        Console.WriteLine($"{count - failed}/{count} towns ok in {sw.Elapsed.TotalSeconds:0.0} s: {buildings} buildings, {driveways} driveway tiles, {beds} garden beds, {parked} parked cars, {culs} cul-de-sacs; {airports} airports, {zoos} zoos, {planes} planes, {docks} docks, {boats} moored boats, {loops} boat loops");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The airport has 8-10 planes, all on its apron; docks run out from dry
    /// land over open water, apart; moored boats float clear of the shore, the
    /// docks and each other; and a sailing boat anywhere on a loop does too.
    /// </summary>
    static string Ports(TownModel model, Porchlight.Explore.IsoLayout layout)
    {
        int w = model.width;
        bool Wet(double x, double y)
        {
            int tx = (int)Math.Floor(x), ty = (int)Math.Floor(y);
            return tx >= 0 && ty >= 0 && tx < w && ty < model.height && Terrain.IsWater(model.tiles.terrain[ty * w + tx]);
        }
        int planes = 0;
        foreach (var c in layout.cars)
        {
            if (c.vehicle <= 0) continue;
            var kind = Porchlight.Explore.Catalog.KindOf(c.vehicle - 1);
            if (kind == Porchlight.Explore.VehicleKind.Boat && !Wet(c.x, c.y)) return $"a boat moored on land at ({c.x:0.0}, {c.y:0.0})";
            if (kind != Porchlight.Explore.VehicleKind.Plane) continue;
            planes++;
            var a = model.airport;
            if (a == null || c.x < a.x0 || c.x > a.x1 + 1 || c.y < a.y0 || c.y > a.y1 + 1) return $"a plane off the airfield at ({c.x:0.0}, {c.y:0.0})";
        }
        if (model.airport != null && (planes < 8 || planes > 10)) return $"{planes} planes at the airport";
        // Docks: from dry land next to the water, out over it, the end clear of the shore.
        var decks = new List<(double x0, double y0, double x1, double y1)>();
        foreach (var d in model.docks)
        {
            if (Wet(d.shore.x + 0.5, d.shore.y + 0.5)) return $"dock {d.id} starts in the water at ({d.shore.x}, {d.shore.y})";
            var (fx, fy) = Porchlight.Core.Grid.DIRS4[(int)d.facing];
            foreach (var t in d.tiles)
                if (!Wet(t.x + 0.5, t.y + 0.5)) return $"a dock on land at ({t.x}, {t.y})";
            var end = d.tiles[d.tiles.Count - 1];
            for (int k = 1; k <= 2; k++)
                if (!Wet(end.x + 0.5 + fx * k, end.y + 0.5 + fy * k)) return $"dock {d.id} runs up to the shore at ({end.x}, {end.y})";
            var deck = Piers.Deck(d);
            foreach (var e in decks) if (Overlap(deck, e)) return $"two docks meet at ({end.x}, {end.y})";
            decks.Add(deck);
        }
        // Moored boats, as the street scene has them: one per berth, on open water, clear of the docks and each other.
        var moored = new List<(double x0, double y0, double x1, double y1)>();
        foreach (var c in layout.cars)
        {
            if (c.vehicle <= 0 || Porchlight.Explore.Catalog.KindOf(c.vehicle - 1) != Porchlight.Explore.VehicleKind.Boat) continue;
            var box = BoatBox(c.x, c.y, c.angle, 0);
            if (!OverWater(model, box)) return $"a moored boat touches land or a bridge at ({c.x:0.0}, {c.y:0.0})";
            foreach (var deck in decks) if (Overlap(box, deck)) return $"a moored boat runs into a dock at ({c.x:0.0}, {c.y:0.0})";
            foreach (var other in moored) if (Overlap(box, other)) return $"two moored boats overlap at ({c.x:0.0}, {c.y:0.0})";
            moored.Add(box);
        }
        int berths = 0;
        foreach (var d in model.docks) berths += d.boats.Count;
        if (berths != moored.Count) return $"{moored.Count} moored boats for {berths} berths";
        // Sailing boats: anywhere on a loop (where they start, and sail), a boat fits clear of the shore, the docks and the moored boats.
        foreach (var loop in layout.boatLoops)
            for (int k = 0; k + 1 < loop.Count; k++)
            {
                var q = loop[k];
                if (!Wet(q.x, q.y)) return $"a boat loop runs aground at ({q.x:0.0}, {q.y:0.0})";
                var box = BoatBox(q.x, q.y, Math.Atan2(loop[k + 1].y - q.y, loop[k + 1].x - q.x), Piers.MARGIN);
                if (!OverWater(model, box)) return $"a sailing boat would touch the shore or a bridge at ({q.x:0.0}, {q.y:0.0})";
                foreach (var deck in decks) if (Overlap(box, deck)) return $"a sailing boat would hit a dock at ({q.x:0.0}, {q.y:0.0})";
                foreach (var other in moored) if (Overlap(box, other)) return $"a sailing boat would hit a moored one at ({q.x:0.0}, {q.y:0.0})";
            }
        return null;
    }

    /// <summary>The box round a boat at (x, y) heading `angle`, grown by `grow`.</summary>
    static (double x0, double y0, double x1, double y1) BoatBox(double x, double y, double angle, double grow)
    {
        double c = Math.Abs(Math.Cos(angle)), s = Math.Abs(Math.Sin(angle));
        double hx = c * Piers.BOAT_HALF_LENGTH + s * Piers.BOAT_HALF_WIDTH + grow, hy = s * Piers.BOAT_HALF_LENGTH + c * Piers.BOAT_HALF_WIDTH + grow;
        return (x - hx, y - hy, x + hx, y + hy);
    }

    static bool Overlap((double x0, double y0, double x1, double y1) a, (double x0, double y0, double x1, double y1) b) =>
        a.x0 < b.x1 - 1e-9 && b.x0 < a.x1 - 1e-9 && a.y0 < b.y1 - 1e-9 && b.y0 < a.y1 - 1e-9;

    /// <summary>Whether every tile under the box is water with no bridge over it.</summary>
    static bool OverWater(TownModel model, (double x0, double y0, double x1, double y1) box)
    {
        int w = model.width;
        for (int y = (int)Math.Floor(box.y0); y < box.y1 - 1e-9; y++)
            for (int x = (int)Math.Floor(box.x0); x < box.x1 - 1e-9; x++)
            {
                if (x < 0 || y < 0 || x >= w || y >= model.height) return false;
                int i = y * w + x;
                if (!Terrain.IsWater(model.tiles.terrain[i]) || model.tiles.overlay[i] != Overlay.NONE) return false;
            }
        return true;
    }

    /// <summary>
    /// Every cul-de-sac's bulb is street underfoot, and its sidewalk round joins
    /// the street's own sidewalks at both ends, so townsfolk walk round it.
    /// </summary>
    static string CulDeSacs(TownModel model, Porchlight.Explore.IsoLayout layout)
    {
        int w = model.width;
        var net = layout.sidewalks;
        foreach (var b in layout.bulbs)
        {
            int x = (int)Math.Floor(b.at.x), y = (int)Math.Floor(b.at.y);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (model.tiles.overlay[(y + dy) * w + x + dx] != Overlay.ROAD_STREET) return $"cul-de-sac at ({b.at.x:0.0}, {b.at.y:0.0}) isn't all street";
            int ring = net.paths.FindIndex(q => q.road == b.edge && q.points.Count > 8 && Porchlight.Core.Geo.Dist(q.points[q.points.Count / 2], b.at) < Raster.BulbRadius(model.@params) + 1);
            if (ring < 0) return $"no sidewalk round the cul-de-sac at ({b.at.x:0.0}, {b.at.y:0.0})";
            foreach (int end in new[] { ring * 2, ring * 2 + 1 })
            {
                var q = net.paths[ring].points[end % 2 == 0 ? 0 : net.paths[ring].points.Count - 1];
                if (net.links[end].Count == 0) return $"the sidewalk round the cul-de-sac at ({b.at.x:0.0}, {b.at.y:0.0}) leads nowhere";
                for (int k = 0; k < net.paths.Count * 2; k++)
                {
                    var path = net.paths[k >> 1];
                    if (k >> 1 == ring || path.road != b.edge) continue;
                    var e = path.points[k % 2 == 0 ? 0 : path.points.Count - 1];
                    if (Porchlight.Core.Geo.Dist(e, q) < 0.01 && !net.links[end].Contains(k))
                        return $"the sidewalk round the cul-de-sac at ({b.at.x:0.0}, {b.at.y:0.0}) doesn't join its street's";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The town as players share it: the host's traffic and townsfolk survive
    /// being packed and followed, a client taking over carries them on, and the
    /// player finder's route reaches its target.
    /// </summary>
    /// <summary>
    /// Town traffic turns round at a dead end as a car would, a minute of it: slowly, in a
    /// swing out past the end and back, never swinging round faster than MAX_TURN (radians a
    /// second; no spinning on the spot), nor sliding sideways.
    /// </summary>
    static string Turning(Porchlight.Explore.IsoLayout layout)
    {
        const double DT = 0.05, MAX_TURN = 2.6, MAX_SLIP = 0.6;
        var traffic = new Porchlight.Explore.Traffic(layout, new Porchlight.Core.Rng("turning"), Porchlight.Explore.Catalog.PickCarModel);
        var cars = traffic.cars.FindAll(c => c.loop < 0);
        var was = cars.ConvertAll(c => (c.x, c.y, c.heading));
        for (double t = 0; t < 60; t += DT)
        {
            traffic.Update(DT, null);
            for (int k = 0; k < cars.Count; k++)
            {
                var c = cars[k];
                var (x, y, heading) = was[k];
                was[k] = (c.x, c.y, c.heading);
                // Past the first second (cars start out wherever they're put, some mid-turn), turning round.
                if (t < 1 || (c.next != Porchlight.Explore.Traffic.ROUND && c.prev != Porchlight.Explore.Traffic.ROUND)) continue;
                double turn = Math.Abs(Porchlight.Explore.Orbit.ShortestTurn(heading, c.heading)) / DT;
                double slip = Math.Abs(-(c.x - x) * Math.Sin(c.heading) + (c.y - y) * Math.Cos(c.heading)) / DT;
                if (turn > MAX_TURN) return $"car {k} swung round at {turn:0.0} rad/s turning round at ({c.x:0.0}, {c.y:0.0})";
                if (slip > MAX_SLIP) return $"car {k} slid sideways at {slip:0.0} tiles/s turning round at ({c.x:0.0}, {c.y:0.0})";
            }
        }
        return null;
    }

    static string Shared(TownModel model, Porchlight.Explore.IsoLayout layout)
    {
        var traffic = new Porchlight.Explore.Traffic(layout, new Porchlight.Core.Rng("t"), Porchlight.Explore.Catalog.PickCarModel);
        var copy = new Porchlight.Explore.Traffic(layout, new Porchlight.Core.Rng("other"), Porchlight.Explore.Catalog.PickCarModel);
        if (traffic.cars.Count == 0) return null;
        for (int k = 0; k < 40; k++) traffic.Update(0.05, null);
        var people = new System.Collections.Generic.List<Porchlight.Explore.Walker>();
        var rng = new Porchlight.Core.Rng("p");
        for (int k = 0; k < 10 && layout.sidewalks.paths.Count > 0; k++) people.Add(Porchlight.Explore.Pedestrian.Create("x", layout.sidewalks, rng));
        var others = new System.Collections.Generic.List<Porchlight.Explore.Walker>();
        foreach (var q in people) others.Add(new Porchlight.Explore.Walker("x", 0, 0));
        var sync = new Porchlight.Explore.WorldSync();
        sync.Receive(Porchlight.Explore.WorldSync.EncodeCars(traffic.cars), Porchlight.Explore.WorldSync.EncodePeople(people));
        sync.Step(0.2, copy.cars, others); // a full catch-up, carrying on 0.2 s at speed
        for (int k = 0; k < traffic.cars.Count; k++)
            if (Math.Abs(traffic.cars[k].x - copy.cars[k].x) > 0.6 || Math.Abs(traffic.cars[k].y - copy.cars[k].y) > 0.6) return $"traffic car {k} not followed";
        for (int k = 0; k < people.Count; k++)
            if (Math.Abs(people[k].x - others[k].x) > 0.4) return $"person {k} not followed";
        foreach (var c in copy.cars) copy.Adopt(c);
        for (int k = 0; k < 40; k++) copy.Update(0.05, null);
        foreach (var c in copy.cars) if (double.IsNaN(c.x) || c.x < 0 || c.x > model.width) return "adopted traffic went astray";
        // Sidewalks run clean: none crosses itself (a loop on the inside of a bend).
        foreach (var path in layout.sidewalks.paths)
            for (int i = 0; i + 1 < path.points.Count; i++)
                for (int j = i + 2; j + 1 < path.points.Count; j++)
                    if (Porchlight.Core.Geo.SegmentIntersection(path.points[i], path.points[i + 1], path.points[j], path.points[j + 1]) != null)
                        return $"a sidewalk of road {path.road} loops at ({path.points[i].x:0.0}, {path.points[i].y:0.0})";
        // Auto walk always finds its way (turning round if it must), along the streets.
        var roam = new Porchlight.Core.Rng("roam");
        for (int k = 0; k < 30 && layout.sidewalks.paths.Count > 0; k++)
        {
            var w = Porchlight.Explore.SidewalkWalk.Random(layout.sidewalks, roam);
            w.Roam();
            if (w.Itinerary() == null) return "auto walk found no way";
            for (int step = 0; step < 200; step++) w.Advance(0.2);
        }
        // The way to another player keeps to the streets, leaving them only to
        // start from you and, at the last, for them.
        var streets = layout.roads.FindAll(r => r.kind != RoadKind.Path);
        if (streets.Count > 1)
        {
            double OffStreet(Porchlight.Core.Vec2 q)
            {
                double best = double.PositiveInfinity;
                foreach (var r in streets) best = Math.Min(best, Porchlight.Core.Geo.DistToPolyline(q, r.points));
                return best;
            }
            var pick = new Porchlight.Core.Rng("finder");
            for (int k = 0; k < 40; k++)
            {
                var a = streets[pick.Int(0, streets.Count - 1)];
                var b = streets[pick.Int(0, streets.Count - 1)];
                var from = a.points[pick.Int(0, a.points.Count - 1)];
                var to = b.points[pick.Int(0, b.points.Count - 1)];
                from = new Porchlight.Core.Vec2(from.x + pick.Range(-2, 2), from.y + pick.Range(-2, 2));
                to = new Porchlight.Core.Vec2(to.x + pick.Range(-2, 2), to.y + pick.Range(-2, 2));
                var way = Porchlight.Explore.StreetRoute.Between(layout.roads, from, to);
                if (way == null || way.points.Count < 2) return "no way to a player";
                var pts = way.points;
                if (Porchlight.Core.Geo.Dist(pts[0], from) > 1e-6 || Porchlight.Core.Geo.Dist(pts[pts.Count - 1], to) > 1e-6) return "the way to a player doesn't run from you to them";
                for (int i = 1; i < pts.Count - 1; i++)
                    if (OffStreet(pts[i]) > 0.01) return $"the way to a player leaves the street ({pts[i].x:0.0}, {pts[i].y:0.0})";
                for (int i = 2; i < pts.Count - 1; i++)
                    if (Porchlight.Core.Geo.Dist(pts[i - 1], pts[i]) > 0.01 && OffStreet(new Porchlight.Core.Vec2((pts[i - 1].x + pts[i].x) / 2, (pts[i - 1].y + pts[i].y) / 2)) > 0.6)
                        return $"the way to a player cuts across from ({pts[i - 1].x:0.0}, {pts[i - 1].y:0.0}) to ({pts[i].x:0.0}, {pts[i].y:0.0})";
                var steps = Porchlight.Explore.Directions.ForDrive(Porchlight.Explore.StreetNaming.Name(model, layout.roads), way.stretches, "them");
                if (steps.Count == 0 || !steps[steps.Count - 1].text.StartsWith("Arrive at them")) return $"the directions to a player don't arrive ({way.stretches.Count} stretches, {pts.Count} points, from {from.x:0.0},{from.y:0.0} to {to.x:0.0},{to.y:0.0})";
            }
        }
        return null;
    }

    static string Same(TownModel a, TownModel b)
    {
        if (a.buildings.Count != b.buildings.Count) return "buildings";
        if (a.roads.edges.Count != b.roads.edges.Count) return "roads";
        if (a.lots.Count != b.lots.Count) return "lots";
        for (int i = 0; i < a.tiles.terrain.Length; i++)
            if (a.tiles.terrain[i] != b.tiles.terrain[i] || a.tiles.overlay[i] != b.tiles.overlay[i] || a.tiles.buildingId[i] != b.tiles.buildingId[i]) return "tiles";
        if (a.spawn.x != b.spawn.x || a.spawn.y != b.spawn.y) return "spawn";
        if (a.docks.Count != b.docks.Count) return "docks";
        for (int i = 0; i < a.docks.Count; i++)
        {
            Dock p = a.docks[i], q = b.docks[i];
            if (p.shore.x != q.shore.x || p.shore.y != q.shore.y || p.facing != q.facing || p.tiles.Count != q.tiles.Count || p.boats.Count != q.boats.Count) return "docks";
            for (int k = 0; k < p.boats.Count; k++)
                if (p.boats[k].x != q.boats[k].x || p.boats[k].y != q.boats[k].y || p.boats[k].facing != q.boats[k].facing) return "moored boats";
        }
        var ap = a.airport;
        var aq = b.airport;
        if ((ap == null) != (aq == null) || ap != null && (ap.x0 != aq.x0 || ap.y0 != aq.y0 || ap.x1 != aq.x1 || ap.y1 != aq.y1 || ap.front != aq.front || ap.entranceU != aq.entranceU)) return "airport";
        var zp = a.zoo;
        var zq = b.zoo;
        if ((zp == null) != (zq == null) || zp != null && (zp.x0 != zq.x0 || zp.y0 != zq.y0 || zp.x1 != zq.x1 || zp.y1 != zq.y1 || zp.front != zq.front)) return "zoo";
        return null;
    }

    /// <summary>
    /// Every town big enough has a zoo, with nothing built or driven on it, and
    /// a sidewalk close outside its gate (a mission's role there walks to it).
    /// </summary>
    static string Zoo(TownModel model, Porchlight.Explore.IsoLayout layout)
    {
        var z = model.zoo;
        if (z == null) return Math.Min(model.width, model.height) >= 96 ? "no zoo" : null;
        int w = model.width;
        foreach (int i in Menagerie.Tiles(z, w, model.height))
        {
            if (model.tiles.buildingId[i] >= 0) return $"a building on the zoo at {i % w},{i / w}";
            if (model.tiles.overlay[i] != Overlay.ZOO) return $"zoo tile {i % w},{i / w} is {Overlay.Names[model.tiles.overlay[i]]}";
            if (layout.use[i] != Porchlight.Explore.Use.ZOO) return $"zoo tile {i % w},{i / w} is used for something else";
        }
        var drive = Porchlight.Explore.Vehicle.Drivable(model, layout);
        var inside = Menagerie.Visitor(z);
        if (drive(inside.x, inside.y)) return "a car could drive into the zoo";
        var gate = Menagerie.Gate(z);
        var dv = Menagerie.AlongV(z);
        var outside = new Porchlight.Core.Vec2(gate.x + dv.x * 0.35, gate.y + dv.y * 0.35);
        double nearest = double.PositiveInfinity;
        foreach (var path in layout.sidewalks.paths) nearest = Math.Min(nearest, Porchlight.Core.Geo.ClosestOnPolyline(outside, path.points).d);
        if (nearest > 6) return $"no sidewalk near the zoo's gate ({nearest:0.0} tiles)";
        // The fence stands clear of every sidewalk and street (their edges, not just their middles).
        const double CLEAR = 0.05;
        var sides = new[] { "back", "side", "side", "front", "front" };
        int line = -1;
        foreach (var (a, b) in Menagerie.Fence(z))
        {
            line++;
            double len = Porchlight.Core.Geo.Dist(a, b);
            for (double s = 0; s <= len; s += 0.1)
            {
                var p = Porchlight.Core.Geo.Lerp(a, b, s / len);
                foreach (var path in layout.sidewalks.paths)
                {
                    double d = Porchlight.Core.Geo.ClosestOnPolyline(p, path.points).d - Porchlight.Explore.Layout.SIDEWALK / 2;
                    if (d < CLEAR) return $"the zoo's {sides[line]} fence at ({p.x:0.0}, {p.y:0.0}) is {(d < 0 ? "on" : "up against")} a sidewalk ({d:0.00} tiles off)";
                }
                foreach (var road in layout.roads)
                {
                    double d = Porchlight.Core.Geo.ClosestOnPolyline(p, road.points).d - road.width / 2;
                    if (d < CLEAR) return $"the zoo's {sides[line]} fence at ({p.x:0.0}, {p.y:0.0}) is {(d < 0 ? "on" : "up against")} a street ({d:0.00} tiles off)";
                }
            }
        }
        return null;
    }
}

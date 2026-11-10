// The county (Runtime/Shared/County): with none, some or all of your own towns,
// its spaces don't overlap (but where one stretch of highway hands over to the
// next), its highways start and end on the gates they join and bend no tighter
// than a car can take at speed, every road space builds as a town you can drive
// all the way along, the hand-overs between spaces have road under the car on
// both sides, and every point has a room. Then it writes the server's test
// vectors (server/test/county-vectors.json), so server/src/county.ts is held to
// the same answers.
//
//   dotnet run --project "unity/Tools~/Checks" -- county [vectors-file]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Porchlight.Core;
using Porchlight.County;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class CountyCheck
{
    /// <summary>The tightest a highway may bend (tiles of radius).</summary>
    const double MIN_RADIUS = 60;
    /// <summary>The biggest a space may be on a side: positions on the wire stop at 655 tiles (WorldSync).</summary>
    const int MAX_SIDE = 640;

    /// <summary>Sets of your own towns to lay out: none, one, two and three, of different sizes.</summary>
    static readonly (string name, PlacedTown[] mine)[] Cases =
    {
        ("none", new PlacedTown[0]),
        ("one", new[] { Mine(0, "seed=a1b2c3d4e5f6", 128, 128) }),
        ("two", new[] { Mine(1, "seed=0f9e8d7c6b5a&width=96&height=160", 96, 160), Mine(2, "seed=123456789abc&width=256&height=64", 256, 64) }),
        ("three", new[] { Mine(0, "seed=aaaaaaaaaaaa&width=32&height=32", 32, 32), Mine(1, "seed=bbbbbbbbbbbb", 128, 128), Mine(2, "seed=cccccccccccc&width=256&height=256", 256, 256) }),
    };

    static PlacedTown Mine(int slot, string key, int w, int h) =>
        new PlacedTown { place = CountyMap.MineSlot(slot), name = "Mine " + (slot + 1), key = key, width = w, height = h };

    public static List<PlacedTown> PublicTowns()
    {
        var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText("server/towns/porchlight.json")).RootElement;
        var (_, p) = Program.ParseKey(doc.GetProperty("key").GetString());
        var list = new List<PlacedTown> { new PlacedTown { place = Place.Porchlight, name = doc.GetProperty("name").GetString(), key = doc.GetProperty("key").GetString(), width = (int)p.width, height = (int)p.height } };
        list.AddRange(CountyMap.Cities);
        return list;
    }

    /// <summary>
    /// Directions to a house in each of these towns (keys, your own towns in your three slots), from
    /// Porchlight and from Lanternport, driven along the real streets: `trips key1 [key2 [key3]]`.
    /// </summary>
    public static int Trips(string[] keys)
    {
        var towns = PublicTowns();
        for (int i = 0; i < keys.Length && i < 3; i++)
        {
            var (_, p) = Program.ParseKey(keys[i]);
            towns.Add(Mine(i, keys[i], (int)p.width, (int)p.height));
        }
        var map = CountyMap.Build(towns, "acct");
        int failed = 0;
        for (int i = 0; i < keys.Length && i < 3; i++)
        {
            var dest = map.towns[CountyMap.MineSlot(i)];
            foreach (var start in new[] { map.towns[Place.Porchlight], map.towns[Place.East] })
            {
                var (error, km, spaces) = Trip(map, start, dest);
                if (error != null) { Console.WriteLine($"FAIL {start.name} to {dest.name} ({keys[i]}): {error}"); failed++; }
                else Console.WriteLine($"ok {start.name} to a house in {dest.name} ({keys[i]}): {Numbers.ToFixed(km, 1)} km through {spaces} spaces");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    public static int Run(string vectorsFile)
    {
        int failed = 0;
        var vectors = new StringBuilder();
        vectors.Append("{\n  \"cases\": [");
        bool firstCase = true;
        foreach (var (name, mine) in Cases)
        {
            var towns = PublicTowns();
            towns.AddRange(mine);
            var map = CountyMap.Build(towns, "acct");
            var errors = Check(map);
            // Directions from Porchlight (and from a city across the county) to a house in each of your own towns, driven.
            foreach (var t in mine)
            {
                var dest = map.towns[t.place];
                foreach (var start in new[] { map.towns[Place.Porchlight], map.towns[Place.East] })
                {
                    var (error, km, spaces) = Trip(map, start, dest);
                    if (error != null) errors.Add($"directions from {start.name} to a house in {dest.name}: {error}");
                    else Console.WriteLine($"ok directions ({name}): {start.name} to a house in {dest.name}, {Numbers.ToFixed(km, 1)} km through {spaces} spaces");
                }
            }
            // With all your towns: from a house in every town to a house in every other.
            if (mine.Length == 3)
                foreach (var a in map.towns.Values)
                    foreach (var b in map.towns.Values)
                    {
                        if (a.id == b.id) continue;
                        var (error, km, spaces) = Trip(map, a, b, fromHouse: true);
                        if (error != null) errors.Add($"directions from a house in {a.name} to one in {b.name}: {error}");
                        else Console.WriteLine($"ok house to house: {a.name} to {b.name}, {Numbers.ToFixed(km, 1)} km through {spaces} spaces");
                    }
            // Into each town by each of its highways, then to every house there: on the map all the way.
            if (mine.Length == 3)
                foreach (var hw in map.highways)
                    foreach (var port in new[] { hw.a, hw.b })
                    {
                        var town = map.spaces[port.space];
                        if (town.kind != SpaceKind.Town) continue;
                        var (model, layout, _) = Streets(map, town);
                        var gate = CountyMap.PortPoint(town, port.side);
                        var o = Compass.Outward(port.side);
                        var from = town.ToLocal(new Vec2(gate.x - o.x * CountyMap.ENTER, gate.y - o.y * CountyMap.ENTER));
                        int bad = 0;
                        foreach (var h in Homes.All(model))
                        {
                            var to = new Vec2(h.tiles[0].x + 0.5, h.tiles[0].y + 0.5);
                            var way = StreetRoute.Between(layout.roads, from, to);
                            string why = way == null ? "no way" : null;
                            if (way != null)
                                foreach (var q in way.points)
                                    if (q.x < 0 || q.y < 0 || q.x > model.width || q.y > model.height) { why = $"off the map at {N(q.x)},{N(q.y)}"; break; }
                            if (why != null && bad++ < 3) errors.Add($"in {town.name} from its {Compass.Name(port.side)} gate to the house at {h.tiles[0].x},{h.tiles[0].y}: {why}");
                        }
                        if (bad > 0) errors.Add($"in {town.name} from its {Compass.Name(port.side)} gate: {bad} of {Homes.All(model).Count} houses");
                    }
            foreach (var e in errors) Console.WriteLine($"FAIL county ({name}): {e}");
            if (errors.Count == 0)
                Console.WriteLine($"ok county ({name}): {map.spaces.Count} spaces, {map.highways.Count} highways, {Numbers.ToFixed(map.highways.Sum(h => h.length) * Directions.METRES_PER_TILE / 1000, 1)} km of road");
            failed += errors.Count;
            if (!firstCase) vectors.Append(',');
            firstCase = false;
            vectors.Append(Vectors(name, towns, map));
        }
        vectors.Append("\n  ]\n}\n");
        // The cities themselves: their keys are as the game writes them, and they grow.
        foreach (var c in CountyMap.Cities)
        {
            var (seed, p) = Program.ParseKey(c.key);
            string written = Program.WrittenKey(seed, p);
            if (written != c.key)
            {
                Console.WriteLine($"FAIL {c.name}: the game writes its key as {written}");
                failed++;
            }
            if ((int)p.width != c.width || (int)p.height != c.height)
            {
                Console.WriteLine($"FAIL {c.name}: its size is {p.width}x{p.height}, not {c.width}x{c.height}");
                failed++;
            }
            string file = $"server/towns/{FileName(c.name)}.json";
            if (System.IO.File.Exists(file))
            {
                var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file)).RootElement;
                if (doc.GetProperty("key").GetString() != c.key || doc.GetProperty("place").GetString() != c.place.ToString().ToLowerInvariant())
                {
                    Console.WriteLine($"FAIL {c.name}: {file} has another key or place than CountyMap.Cities");
                    failed++;
                }
            }
            else
            {
                Console.WriteLine($"FAIL {c.name}: no {file} (run `npm run towns` in server/)");
                failed++;
            }
        }
        if (vectorsFile != null)
        {
            System.IO.File.WriteAllText(vectorsFile, vectors.ToString());
            Console.WriteLine($"wrote {vectorsFile}");
        }
        return failed == 0 ? 0 : 1;
    }

    public static string FileName(string townName) => new string(townName.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());

    static List<string> Check(CountyMap map)
    {
        var errors = new List<string>();
        // Spaces: no overlap but between neighbouring stretches of one highway.
        for (int i = 0; i < map.spaces.Count; i++)
        {
            var a = map.spaces[i];
            if (a.width < 1 || a.height < 1) errors.Add($"{a.name} ({a.kind}) is empty");
            if (a.width > MAX_SIDE || a.height > MAX_SIDE) errors.Add($"{a.name} ({a.kind}) is {a.width}x{a.height}, over {MAX_SIDE} a side");
            for (int j = i + 1; j < map.spaces.Count; j++)
            {
                var b = map.spaces[j];
                bool overlap = a.x0 < b.x0 + b.width && b.x0 < a.x0 + a.width && a.y0 < b.y0 + b.height && b.y0 < a.y0 + a.height;
                if (!overlap) continue;
                bool neighbours = a.kind == SpaceKind.Highway && b.kind == SpaceKind.Highway && a.highway == b.highway && Math.Abs(map.highways[a.highway].segments.IndexOf(a.id) - map.highways[a.highway].segments.IndexOf(b.id)) == 1;
                // A highway's end stretches reach a little way into the spaces at its ends, and no further.
                bool end = false;
                foreach (var (hwSpace, other) in new[] { (a, b), (b, a) })
                {
                    if (hwSpace.kind != SpaceKind.Highway || other.kind == SpaceKind.Highway) continue;
                    var hw = map.highways[hwSpace.highway];
                    if (other.id != hw.a.space && other.id != hw.b.space) continue;
                    int depth = Math.Min(Math.Min(hwSpace.x0 + hwSpace.width - other.x0, other.x0 + other.width - hwSpace.x0), Math.Min(hwSpace.y0 + hwSpace.height - other.y0, other.y0 + other.height - hwSpace.y0));
                    end = depth <= CountyMap.INSET;
                }
                // Two highways out of one small town can pass close by its corner: they share the ground (each draws the tiles nearer its own road).
                bool sameTown = a.kind == SpaceKind.Highway && b.kind == SpaceKind.Highway && a.highway != b.highway
                    && new[] { map.highways[a.highway].a.space, map.highways[a.highway].b.space }.Intersect(new[] { map.highways[b.highway].a.space, map.highways[b.highway].b.space }).Any();
                // A highway that curves round a town's corner: its rectangle reaches over the corner, but its
                // road and verges (all it draws) keep well clear, and the town's own ground is the town's.
                if (neighbours || end || sameTown) continue;
                bool clear = false;
                foreach (var (hwSpace, other) in new[] { (a, b), (b, a) })
                {
                    if (hwSpace.kind != SpaceKind.Highway || other.kind == SpaceKind.Highway) continue;
                    var hw = map.highways[hwSpace.highway];
                    double nearest = double.PositiveInfinity;
                    foreach (var q in CountyMap.Stretch(hw, hwSpace.s0, hwSpace.s1)) nearest = Math.Min(nearest, CountyMap.DistanceTo(other, q));
                    clear = nearest > CountyTerrain.FLAT + CountyMap.VERGE;
                    if (!clear) errors.Add($"{hw.name} runs within {Numbers.ToFixed(nearest, 1)} tiles of {Describe(other)}");
                }
                if (!clear) errors.Add($"{Describe(a)} overlaps {Describe(b)}");
            }
        }
        foreach (var hw in map.highways)
        {
            var from = map.spaces[hw.a.space];
            var to = map.spaces[hw.b.space];
            if (Geo.Dist(hw.points[0], CountyMap.PortPoint(from, hw.a.side)) > 1e-6) errors.Add($"{hw.name} doesn't start at {from.name}'s {Compass.Name(hw.a.side)} gate");
            if (Geo.Dist(hw.points[hw.points.Count - 1], CountyMap.PortPoint(to, hw.b.side)) > 1e-6) errors.Add($"{hw.name} doesn't end at {to.name}'s {Compass.Name(hw.b.side)} gate");
            // Bends: the radius through each three points about 6 tiles apart.
            double tightest = double.PositiveInfinity;
            for (double s = 6; s < hw.length - 6; s += 2)
            {
                var p = CountyMap.PointAt(hw, s - 6);
                var q = CountyMap.PointAt(hw, s);
                var r = CountyMap.PointAt(hw, s + 6);
                double cross = (q.x - p.x) * (r.y - q.y) - (q.y - p.y) * (r.x - q.x);
                double ab = Geo.Dist(p, q), bc = Geo.Dist(q, r), ca = Geo.Dist(r, p);
                if (Math.Abs(cross) < 1e-9) continue;
                tightest = Math.Min(tightest, ab * bc * ca / (2 * Math.Abs(cross)));
            }
            if (tightest < MIN_RADIUS) errors.Add($"{hw.name} bends as tight as {Numbers.ToFixed(tightest, 1)} tiles");
            // Every point along it is in one of its own stretches (or the ends), with a room.
            Space at = null;
            for (double s = 0; s <= hw.length; s += 1)
            {
                var p = CountyMap.PointAt(hw, s);
                at = map.SpaceAt(p, at);
                if (at == null) { errors.Add($"{hw.name} has no space at {Numbers.ToFixed(s, 0)} tiles along"); break; }
                bool own = at.highway == hw.id || at.id == hw.a.space || at.id == hw.b.space;
                if (!own) { errors.Add($"{hw.name} runs through {Describe(at)} {Numbers.ToFixed(s, 0)} tiles along"); break; }
                if (map.RoomAt(p) == null) errors.Add($"{hw.name}: no room at {Numbers.ToFixed(s, 0)}");
            }
        }
        // The hills: level along every highway and its verges, and round every town, junction and plot; high elsewhere.
        var built0 = System.Diagnostics.Stopwatch.StartNew();
        var terrain = CountyTerrain.Build(map);
        Console.WriteLine($"  hills: {terrain.nx}x{terrain.ny} points in {built0.ElapsedMilliseconds} ms");
        foreach (var hw in map.highways)
        {
            for (double s = 0; s <= hw.length; s += 2)
            {
                var p = CountyMap.PointAt(hw, s);
                var q = CountyMap.PointAt(hw, Math.Min(hw.length, s + 1));
                double tx = q.x - p.x, ty = q.y - p.y, len = Math.Max(1e-9, Numbers.Hypot(tx, ty));
                foreach (double side in new[] { 0.0, 8, -8 })
                {
                    var w = new Vec2(p.x - ty / len * side, p.y + tx / len * side);
                    if (!terrain.Level(w.x, w.y)) { errors.Add($"{hw.name}: a hill {Numbers.ToFixed(terrain.Height(w.x, w.y), 2)} high {side} tiles off the road, {Numbers.ToFixed(s, 0)} tiles along"); goto nextHighway; }
                }
            }
            nextHighway:;
        }
        foreach (var s in map.spaces)
        {
            if (s.kind == SpaceKind.Highway) continue;
            for (double f = 0; f <= 1; f += 0.02)
                foreach (var w in new[] { new Vec2(s.x0 + f * s.width, s.y0 - 2), new Vec2(s.x0 + f * s.width, s.y0 + s.height + 2), new Vec2(s.x0 - 2, s.y0 + f * s.height), new Vec2(s.x0 + s.width + 2, s.y0 + f * s.height) })
                    if (!terrain.Level(w.x, w.y)) { errors.Add($"{s.name}: a hill by its edge"); goto nextSpace; }
            nextSpace:;
        }
        double peak = 0;
        for (int j = 0; j < terrain.ny; j++) for (int i = 0; i < terrain.nx; i++) peak = Math.Max(peak, terrain.At(i, j));
        if (peak < CountyTerrain.PEAK * 0.5) errors.Add($"the hills only reach {Numbers.ToFixed(peak, 1)} tiles");
        // The way shown ahead on a trip runs on along the highways to the town.
        foreach (var t in map.towns.Values)
        {
            if (!CountyMap.IsMine(t.place)) continue;
            var start = map.towns[Place.Porchlight];
            var ahead = CountyRoute.Ahead(map, start, new Vec2(start.x0 + start.width / 2.0, start.y0 + start.height / 2.0), t, 1e9);
            if (ahead.Count < 2 || !CountyMap.SidesOf(t.place).Any(side => Geo.Dist(ahead[ahead.Count - 1], CountyMap.PortPoint(t, side)) < 1e-6))
                errors.Add($"the way ahead from Porchlight doesn't reach a gate of {t.name}");
        }
        // Road spaces build, drive end to end, and hand over with road under the car on both sides.
        var built = new Dictionary<int, (TownModel model, Func<double, double, bool> drive)>();
        foreach (var s in map.spaces)
        {
            if (s.kind == SpaceKind.Town) continue;
            try
            {
                var model = RoadSpaces.Build(map, s);
                var layout = Layout.Build(model);
                var drive = Vehicle.Drivable(model, layout);
                var paved = model.tiles;
                var names = StreetNaming.Name(model, layout.roads);
                if (names.streets.Exists(st => st.name != s.name)) errors.Add($"{Describe(s)}: a road there isn't called {s.name}");
                // Asphalt (the tiles' own road), not just anywhere a car can go.
                bool OnRoad(double x, double y)
                {
                    int tx = (int)Math.Floor(x), ty = (int)Math.Floor(y);
                    if (tx < 0 || ty < 0 || tx >= model.width || ty >= model.height) return false;
                    byte o = paved.overlay[ty * model.width + tx];
                    return o == Overlay.ROAD_MAIN || o == Overlay.BRIDGE;
                }
                built[s.id] = (model, (x, y) => drive(x, y) && OnRoad(x, y));
            }
            catch (Exception e)
            {
                errors.Add($"{Describe(s)} doesn't build: {e.GetType().Name}: {e.Message}");
            }
        }
        foreach (var hw in map.highways)
        {
            for (int k = 0; k < hw.segments.Count; k++)
            {
                var seg = map.spaces[hw.segments[k]];
                if (!built.TryGetValue(seg.id, out var b)) continue;
                // All the way along its own stretch, and a little either side of each cut.
                double lo = k == 0 ? 0.6 : seg.s0 - CountyMap.HANDOVER - 1, hi = k == hw.segments.Count - 1 ? hw.length - 0.6 : seg.s1 + CountyMap.HANDOVER + 1;
                for (double s = lo; s <= hi; s += 0.5)
                {
                    var l = seg.ToLocal(CountyMap.PointAt(hw, s));
                    if (!b.drive(l.x, l.y)) { errors.Add($"{Describe(seg)}: no road under the car {Numbers.ToFixed(s, 1)} tiles along"); break; }
                }
            }
            // Into the road spaces at either end: their road meets the highway's at the edge.
            foreach (var (port, s) in new[] { (hw.a, 0.6), (hw.b, hw.length - 0.6) })
            {
                var end = map.spaces[port.space];
                if (end.kind == SpaceKind.Town || !built.TryGetValue(end.id, out var b)) continue;
                var gate = CountyMap.PortPoint(end, port.side);
                var o = Compass.Outward(port.side);
                var l = end.ToLocal(new Vec2(gate.x - o.x * 0.6, gate.y - o.y * 0.6));
                if (!b.drive(l.x, l.y)) errors.Add($"{Describe(end)} has no road at its {Compass.Name(port.side)} edge for {hw.name}");
            }
        }
        // Out of the towns that come with the game and back: well inside the town, the town's road;
        // at its edge, the highway's too, so the car is on both while it crosses.
        foreach (var hw in map.highways)
        {
            foreach (var (port, sAt) in new[] { (hw.a, 0.0), (hw.b, hw.length) })
            {
                var town = map.spaces[port.space];
                if (town.kind != SpaceKind.Town || CountyMap.IsMine(town.place)) continue;
                var (tModel, tDrive) = TownDrive(town.townKey);
                var gate = CountyMap.PortPoint(town, port.side);
                var o = Compass.Outward(port.side);
                foreach (double inward in new[] { CountyMap.ENTER, 1.0, CountyMap.STAY + 0.05 })
                {
                    var w = new Vec2(gate.x - o.x * inward, gate.y - o.y * inward);
                    var l = town.ToLocal(w);
                    if (!tDrive(l.x, l.y)) errors.Add($"{town.name} has no road {inward} tiles in from its {Compass.Name(port.side)} gate");
                    var seg = map.spaces[port.space == hw.a.space ? hw.segments[0] : hw.segments[hw.segments.Count - 1]];
                    if (inward <= CountyMap.ENTER && built.TryGetValue(seg.id, out var b))
                    {
                        var sl = seg.ToLocal(w);
                        if (!b.drive(sl.x, sl.y)) errors.Add($"{hw.name} has no road {inward} tiles inside {town.name}'s {Compass.Name(port.side)} gate");
                    }
                    var at = map.SpaceAt(w, inward >= CountyMap.ENTER ? null : seg);
                    if (inward < CountyMap.STAY + 0.1 && at?.id != seg.id) errors.Add($"at {town.name}'s {Compass.Name(port.side)} edge you're in {(at == null ? "nothing" : Describe(at))}, not on {hw.name}");
                }
            }
        }
        return errors;
    }

    static readonly Dictionary<string, (TownModel model, IsoLayout layout, StreetNames names)> streets = new Dictionary<string, (TownModel, IsoLayout, StreetNames)>();

    /// <summary>A space's streets, as the game has them (a town grown from its key, a road space built), once.</summary>
    static (TownModel model, IsoLayout layout, StreetNames names) Streets(CountyMap map, Space s)
    {
        string key = s.kind == SpaceKind.Town ? s.townKey : $"{s.kind}:{s.id}:{s.x0},{s.y0}";
        if (streets.TryGetValue(key, out var t)) return t;
        TownModel model;
        if (s.kind == SpaceKind.Town)
        {
            var (seed, p) = Program.ParseKey(s.townKey);
            model = Generator.Generate(seed, p);
        }
        else model = RoadSpaces.Build(map, s);
        var layout = Layout.Build(model);
        return streets[key] = (model, layout, StreetNaming.Name(model, layout.roads));
    }

    /// <summary>
    /// Follow the directions (CountyRoute) from the middle of `start` to a house in `dest`, as the game
    /// shows them: along each space's streets to where they say, into whichever space takes you over,
    /// and once in the town, along its streets to the house. Null if you get there.
    /// </summary>
    static (string error, double km, int spaces) Trip(CountyMap map, Space start, Space dest, bool fromHouse = false)
    {
        var here = start;
        var pos = new Vec2(start.x0 + start.width / 2.0, start.y0 + start.height / 2.0);
        if (fromHouse)
        {
            var homes = Homes.All(Streets(map, start).model);
            if (homes.Count == 0) return ($"{start.name} has no houses", 0, 1);
            var h = homes[homes.Count / 3];
            pos = start.ToWorld(new Vec2(h.tiles[0].x + 0.5, h.tiles[0].y + 0.5));
        }
        double travelled = 0;
        int count = 1;
        for (int leg = 0; leg < 200; leg++)
        {
            var (model, layout, names) = Streets(map, here);
            Vec2 target;
            string name;
            var stop = CountyRoute.Next(map, here, pos, dest);
            if (here.id == dest.id)
            {
                var houses = Homes.All(model);
                if (houses.Count == 0) return ($"{dest.name} has no houses", 0, count);
                var house = houses[houses.Count / 2];
                target = here.ToWorld(new Vec2(house.tiles[0].x + 0.5, house.tiles[0].y + 0.5));
                name = "the house";
            }
            else if (stop == null) return ($"no way on from {Describe(here)}", 0, count);
            else
            {
                target = stop.Value.at;
                name = stop.Value.name;
            }
            var way = StreetRoute.Between(layout.roads, here.ToLocal(pos), here.ToLocal(target));
            if (way == null) return ($"no streets in {Describe(here)} from {N(pos.x)},{N(pos.y)} to {name}", 0, count);
            Directions.ForDrive(names, way.stretches, name);
            if (here.id == dest.id)
            {
                // In the town: on its map all the way, and on its streets till the last bit off them to the door.
                var fin = Geo.SamplePolyline(way.points, 0.25);
                var townDrive = Vehicle.Drivable(model, layout);
                int lastOn = -1;
                for (int i = 0; i < fin.Count; i++)
                {
                    var l = fin[i];
                    if (l.x < 0 || l.y < 0 || l.x > model.width || l.y > model.height)
                        return ($"the way to the house leaves the map of {Describe(here)} at {N(l.x)},{N(l.y)} (from {N(here.ToLocal(pos).x)},{N(here.ToLocal(pos).y)})", 0, count);
                    if (townDrive(l.x, l.y)) lastOn = i;
                }
                bool on = false;
                for (int i = 0; i <= lastOn; i++)
                {
                    var l = fin[i];
                    bool ok = townDrive(l.x, l.y) && !model.OffVerge(l.x, l.y);
                    if (ok) on = true;
                    else if (on) return ($"the way to the house leaves the road in {Describe(here)} at {N(l.x)},{N(l.y)}", 0, count);
                }
                return (null, (travelled + Length(way.points)) * Directions.METRES_PER_TILE / 1000, count);
            }
            // Along the way, till another space takes you over: on the map and where a car can go all
            // the way (once it's on the street: it may start in a yard).
            Space next = null;
            var pts = Geo.SamplePolyline(way.points, 0.25);
            var drive = Vehicle.Drivable(model, layout);
            bool onStreet = false;
            for (int i = 0; i < pts.Count; i++)
            {
                var w = here.ToWorld(pts[i]);
                if (i > 0) travelled += Geo.Dist(pts[i - 1], pts[i]);
                var at = map.SpaceAt(w, here);
                if (at != null && at.id != here.id)
                {
                    next = at;
                    pos = w;
                    break;
                }
                var l = pts[i];
                bool inside = l.x > 0.05 && l.y > 0.05 && l.x < model.width - 0.05 && l.y < model.height - 0.05;
                bool ok = inside && drive(l.x, l.y) && !model.OffVerge(l.x, l.y);
                if (ok) onStreet = true;
                else if (onStreet) return ($"the way to {name} leaves {(inside ? "the road" : "the map")} in {Describe(here)} at {N(l.x)},{N(l.y)} (world {N(w.x)},{N(w.y)})", 0, count);
            }
            if (next == null) return ($"stuck in {Describe(here)} at {name}", 0, count);
            if (Environment.GetEnvironmentVariable("TRIP_DEBUG") != null) Console.WriteLine($"  {Describe(here)} -> {Describe(next)} at {N(pos.x)},{N(pos.y)} aiming {name} {N(target.x)},{N(target.y)}");
            here = next;
            count++;
        }
        return ("still going after 200 spaces", 0, count);
    }

    static double Length(List<Vec2> pts)
    {
        double d = 0;
        for (int i = 1; i < pts.Count; i++) d += Geo.Dist(pts[i - 1], pts[i]);
        return d;
    }

    static readonly Dictionary<string, (TownModel, Func<double, double, bool>)> towns =new Dictionary<string, (TownModel, Func<double, double, bool>)>();

    /// <summary>Where a car can go in a town (grown from its key, once).</summary>
    static (TownModel, Func<double, double, bool>) TownDrive(string key)
    {
        if (towns.TryGetValue(key, out var t)) return t;
        var (seed, p) = Program.ParseKey(key);
        var model = Generator.Generate(seed, p);
        var layout = Layout.Build(model);
        var drive = Vehicle.Drivable(model, layout);
        return towns[key] = (model, drive);
    }

    static string Describe(Space s) => $"{s.name} ({s.kind.ToString().ToLowerInvariant()} {s.x0},{s.y0} {s.width}x{s.height})";

    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>What the server's county.ts must agree with: the spaces, the highways' lengths, and rooms on a grid of points.</summary>
    static string Vectors(string name, List<PlacedTown> towns, CountyMap map)
    {
        var sb = new StringBuilder();
        sb.Append($"\n    {{\n      \"name\": \"{name}\",\n      \"account\": \"acct\",\n      \"towns\": [");
        sb.Append(string.Join(", ", towns.Select(t => $"{{ \"place\": \"{t.place.ToString().ToLowerInvariant()}\", \"key\": \"{t.key}\" }}")));
        sb.Append("],\n      \"spaces\": [");
        sb.Append(string.Join(", ", map.spaces.Select(s => $"[\"{s.kind.ToString().ToLowerInvariant()}\", {s.x0}, {s.y0}, {s.width}, {s.height}]")));
        sb.Append("],\n      \"highways\": [");
        sb.Append(string.Join(", ", map.highways.Select(h => N(h.length))));
        sb.Append("],\n      \"rooms\": [");
        var rooms = new List<string>();
        var rng = new Rng("county-vectors:" + name);
        for (int k = 0; k < 400; k++)
        {
            // Mostly along the roads (where it matters), some anywhere.
            Vec2 p;
            if (k % 4 != 0)
            {
                var hw = map.highways[rng.Int(0, map.highways.Count - 1)];
                var q = CountyMap.PointAt(hw, rng.Range(0, hw.length));
                p = new Vec2(q.x + rng.Range(-30, 30), q.y + rng.Range(-30, 30));
            }
            else p = new Vec2(rng.Range(-1000, 1200), rng.Range(-1200, 1200));
            rooms.Add($"[{N(p.x)}, {N(p.y)}, \"{map.RoomAt(p)}\"]");
        }
        sb.Append(string.Join(", ", rooms));
        sb.Append("]\n    }");
        return sb.ToString();
    }
}

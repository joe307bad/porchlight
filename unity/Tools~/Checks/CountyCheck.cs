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
                if (!neighbours) errors.Add($"{Describe(a)} overlaps {Describe(b)}");
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
                var paved = Vehicle.Paved(model, layout);
                var traffic = new Traffic(layout, new Rng("county"), Catalog.PickCarModel);
                for (int k = 0; k < 20; k++) traffic.Update(0.05, null);
                built[s.id] = (model, (x, y) => drive(x, y) && paved(x, y));
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
        return errors;
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

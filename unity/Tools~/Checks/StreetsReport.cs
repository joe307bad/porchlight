// How realistic a town's street layout is (`streets [picture dir]`): for the
// county's towns and a few check towns, the streets that run alongside another
// road too close to it, stubs, dead ends, the kinds of junction, and land left
// in slivers too thin to build on. With a directory, a picture of each town's
// roads (CoursePicture) over its water, land and lots to look at.
// STREETS_ONLY=<town> reports just that one; STREETS_CLOSE=x0,y0,x1,y1 adds a
// close look at part of it, with the roads there listed.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

static class StreetsReport
{
    public static int Run(string dir)
    {
        var towns = new List<(string name, string key)>();
        foreach (var f in new[] { "porchlight", "lanternport", "ember-crossing", "juniper-falls" })
        {
            var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText($"server/towns/{f}.json")).RootElement;
            towns.Add((f, doc.GetProperty("key").GetString()));
        }
        for (int i = 0; i < 6; i++) towns.Add(($"check-{i}", $"seed=check-{i}&width={64 + (i % 4) * 32}&height={64 + ((i / 4) % 4) * 32}&roadDensity={0.5 + (i % 5) * 0.1}"));
        towns.Add(("default-a", "seed=a1b2c3d4e5f6"));
        towns.Add(("default-b", "seed=0f9e8d7c6b5a"));
        if (dir != null) System.IO.Directory.CreateDirectory(dir);
        int bad = 0;
        Console.WriteLine("town              close<4  close<6  stubs  deadends  T  X  bend  slivers  blocks  built  art  col  local  ms");
        string only = Environment.GetEnvironmentVariable("STREETS_ONLY");
        foreach (var (name, key) in towns)
        {
            if (only != null && name != only) continue;
            var (seed, p) = Program.ParseKey(key);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var m = Generator.Generate(seed, p);
            long ms = sw.ElapsedMilliseconds;
            var g = m.roads;
            var shares = StreetsStage.ClassShares(m);
            int close4 = StreetsStage.CloseParallels(g, 4).Count, close6 = StreetsStage.CloseParallels(g, 6).Count;
            var degree = new Dictionary<int, int>();
            foreach (var e in g.edges) foreach (int n in new[] { e.from, e.to }) degree[n] = (degree.TryGetValue(n, out int d) ? d : 0) + 1;
            int stubs = 0, dead = 0;
            foreach (var e in g.edges)
            {
                if (e.kind != RoadKind.Street) continue;
                bool de = degree[e.from] == 1 || degree[e.to] == 1;
                if (de) dead++;
                if (de && Geo.PolylineLength(e.points) < 4) stubs++;
            }
            int t3 = degree.Values.Count(v => v == 3), x4 = degree.Values.Count(v => v >= 4), bend = degree.Values.Count(v => v == 2);
            var free = Parcels.Ground(m).free;
            int w = m.width, h = m.height, slivers = 0, freeCount = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (free[i] == 0) continue;
                    freeCount++;
                    int rx = Run(free, w, h, x, y, 1, 0), ry = Run(free, w, h, x, y, 0, 1);
                    if (rx <= 2 || ry <= 2) slivers++;
                }
            int lotTiles = 0;
            foreach (var lot in m.lots) lotTiles += Parcels.TilesIn(lot.polygon, null, w, h).Count;
            if (close4 > 0) bad++;
            foreach (var c in StreetsStage.CloseParallels(g, 4)) Console.WriteLine($"  {name}: {g.edges[c.a].kind} {c.a} [{string.Join(" ", g.edges[c.a].points.Select(q => $"{q.x:0.#},{q.y:0.#}"))}] and {g.edges[c.b].kind} {c.b} [{string.Join(" ", g.edges[c.b].points.Select(q => $"{q.x:0.#},{q.y:0.#}"))}] {c.apart:0.0} apart");
            Console.WriteLine($"{name,-17} {close4,7}  {close6,7}  {stubs,5}  {dead,8}  {t3} {x4} {bend,4}  {100.0 * slivers / Math.Max(1, freeCount),6:0.0}%  {m.blocks.Count,6}  {100.0 * lotTiles / Math.Max(1, freeCount),4:0}%  {100 * shares.arterial,3:0}% {100 * shares.collector,3:0}% {100 * shares.local,4:0}%  {ms}");
            Console.WriteLine($"  {name} junctions: {JunctionShapes(g)}");
            if (dir != null)
            {
                var layout = Porchlight.Explore.Layout.Build(m);
                var ground = Parcels.Ground(m);
                var lotOf = new int[w * h];
                foreach (var lot in m.lots) foreach (int t in Parcels.TilesIn(lot.polygon, null, w, h)) lotOf[t] = 1;
                int Ground(int x, int y)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) return -1;
                    int i = y * w + x;
                    if (ground.water[i] != 0) return 0x9cc3e6;
                    if (lotOf[i] != 0) return 0xe2d9c4;
                    if (ground.free[i] != 0) return 0xd8e8c8;
                    return -1;
                }
                CoursePicture.Write(System.IO.Path.Combine(dir, name + ".png"), layout.roads, null, 0, 0, m.width, m.height, 4, Ground);
                // STREETS_CLOSE=x0,y0,x1,y1: a close look at part of the town (with STREETS_ONLY, the one town).
                string close = Environment.GetEnvironmentVariable("STREETS_CLOSE");
                if (close != null)
                {
                    var b = close.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    CoursePicture.Write(System.IO.Path.Combine(dir, name + "-close.png"), layout.roads, null, b[0], b[1], b[2], b[3], 16, Ground);
                    foreach (var e in g.edges)
                        if (e.points.Any(q => q.x >= b[0] && q.x <= b[2] && q.y >= b[1] && q.y <= b[3]))
                            Console.WriteLine($"  {e.kind} {e.id} {e.from}->{e.to} [{string.Join(" ", e.points.Select(q => $"{q.x:0.##},{q.y:0.##}"))}]");
                }
            }
        }
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// The junctions a driver finds awkward: five legs or more, two legs meeting at a sharp angle (under 60°, and
    /// under 75°: AASHTO's Green Book calls under 60° undesirable and asks for near 90°), and junctions only a car or
    /// two apart (a road under 3 tiles between two junctions of three legs or more).
    /// </summary>
    public static string JunctionShapes(RoadGraph g)
    {
        var legs = new Dictionary<int, List<double>>();
        void Leg(int node, Vec2 from, Vec2 toward)
        {
            if (!legs.TryGetValue(node, out var l)) legs[node] = l = new List<double>();
            l.Add(Math.Atan2(toward.y - from.y, toward.x - from.x));
        }
        foreach (var e in g.edges)
        {
            if (e.points.Count < 2) continue;
            Leg(e.from, e.points[0], e.points[1]);
            Leg(e.to, e.points[e.points.Count - 1], e.points[e.points.Count - 2]);
        }
        int five = 0, under60 = 0, under75 = 0, close = 0;
        var where = new List<string>();
        foreach (var (node, l) in legs)
        {
            if (l.Count < 3) continue;
            if (l.Count >= 5) five++;
            l.Sort();
            double least = double.MaxValue;
            for (int i = 0; i < l.Count; i++)
            {
                double gap = (i + 1 < l.Count ? l[i + 1] : l[0] + 2 * Math.PI) - l[i];
                least = Math.Min(least, gap);
            }
            double deg = least * 180 / Math.PI;
            if (deg < 60) { under60++; if (where.Count < 6) where.Add($"{g.nodes[node].at.x:0},{g.nodes[node].at.y:0} {deg:0}° {l.Count} legs"); }
            if (deg < 75) under75++;
        }
        foreach (var e in g.edges)
            if (legs.TryGetValue(e.from, out var a) && a.Count >= 3 && legs.TryGetValue(e.to, out var b) && b.Count >= 3 && Geo.PolylineLength(e.points) < 3)
            {
                close++;
                if (where.Count < 10) where.Add($"{e.points[0].x:0},{e.points[0].y:0}-{e.points[e.points.Count - 1].x:0},{e.points[e.points.Count - 1].y:0} {Geo.PolylineLength(e.points):0.0} apart");
            }
        return $"{legs.Count(kv => kv.Value.Count >= 3)} of 3+ legs: {five} of 5+, {under60} under 60°, {under75} under 75°, {close} pairs under 3 tiles apart" + (where.Count > 0 ? $" ({string.Join("; ", where)})" : "");
    }

    /// <summary>How many free tiles in a row, through (x, y), along (dx, dy).</summary>
    static int Run(byte[] free, int w, int h, int x, int y, int dx, int dy)
    {
        int n = 1;
        for (int k = 1; ; k++) { int a = x + dx * k, b = y + dy * k; if (a < 0 || b < 0 || a >= w || b >= h || free[b * w + a] == 0) break; n++; }
        for (int k = 1; ; k++) { int a = x - dx * k, b = y - dy * k; if (a < 0 || b < 0 || a >= w || b >= h || free[b * w + a] == 0) break; n++; }
        return n;
    }
}

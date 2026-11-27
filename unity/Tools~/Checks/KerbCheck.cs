// The kerbs (Explore/Kerbs.cs), in each starter town: the ground the game draws
// cut along them covers every tile once over, each piece where its band is
// (road, the kerb's top, sidewalk, edging, land); what stands off the streets
// (every house, every sidewalk's walking line, a car in a driveway) is up at
// sidewalk height and a car parked in a parking lane down at the road's; the
// ground never jumps, only eases over a kerb; and the paving's joints and
// cracks all lie on a sidewalk. Also how much more the ground costs to draw.
//
//   dotnet run --project "unity/Tools~/Checks" -- kerbs [server/towns]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

static class KerbCheck
{
    /// <summary>How far out of its band a piece's middle may be: the cut takes Edge as straight across each piece, and it bends round a corner.</summary>
    const double MISPLACED = 0.1;

    public static int Run(string dir)
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var (seed, settings) = TownKey.Parse(JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString());
            var model = Generator.Generate(seed, settings);
            var layout = Layout.Build(model);
            var kerbs = layout.kerbs;
            int w = model.width, h = model.height;

            // The ground, cut: every tile covered once (its pieces' areas add up to it), each piece in its band.
            var pieces = new List<(int band, List<Vec2> poly)>();
            var faces = new List<(Vec2 a, Vec2 b, Vec2 facing, bool outer)>();
            int cutTiles = 0, badArea = 0, misplaced = 0, all = 0;
            long corners = 0;
            double kerbLength = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    pieces.Clear();
                    faces.Clear();
                    if (!kerbs.Cut(x, y, pieces, faces))
                    {
                        corners += 4;
                        continue;
                    }
                    cutTiles++;
                    double area = 0;
                    foreach (var (band, poly) in pieces)
                    {
                        area += Math.Abs(Geo.PolygonArea(poly));
                        corners += poly.Count;
                        all++;
                        if (Math.Abs(Geo.PolygonArea(poly)) < 1e-4) continue;
                        // Its middle in its band (to within the cut's own straight-line reckoning round a bend or corner).
                        var c = Centroid(poly);
                        double e = kerbs.Edge(c.x, c.y);
                        double lo = band == Kerbs.ROAD ? double.NegativeInfinity : band == Kerbs.SIDEWALK ? 0 : Kerbs.REACH;
                        double hi = band == Kerbs.ROAD ? 0 : band == Kerbs.SIDEWALK ? Kerbs.REACH : double.PositiveInfinity;
                        if (e < lo - MISPLACED || e > hi + MISPLACED) { misplaced++; if (Environment.GetEnvironmentVariable("KERB_DEBUG") != null) Console.WriteLine($"  misplaced tile ({x},{y}) band {band} edge {e:0.000} area {Math.Abs(Geo.PolygonArea(poly)):0.0000} poly {string.Join(" ", poly.Select(q => $"({q.x:0.00},{q.y:0.00})"))}"); }
                    }
                    foreach (var fc in faces)
                    {
                        // The face, and the kerb's top or the edging drawn along it.
                        corners += 8;
                        if (!fc.outer) kerbLength += Geo.Dist(fc.a, fc.b);
                    }
                    if (Math.Abs(area - 1) > 1e-6) badArea++;
                }
            Expect(badArea == 0, $"{name}: the ground cut along the kerbs covers each of its {cutTiles} tiles near a street once over ({badArea} not)");
            Expect(misplaced == 0, $"{name}: each piece of it in its band, road, sidewalk or land, to within {MISPLACED} tiles ({misplaced} of {all} out)");
            Expect(kerbLength > 0, $"{name}: {kerbLength:0} tiles of kerb");

            // Up on the land: every house's tiles, every sidewalk's walking line, a car in a driveway; down on the road: a car in a parking lane.
            int housesLow = layout.houses.Count(hs => hs.tiles.Any(t => new[] { (0.05, 0.05), (0.95, 0.05), (0.05, 0.95), (0.95, 0.95), (0.5, 0.5) }.Any(o => kerbs.Ground(t.x + o.Item1, t.y + o.Item2) != 0)));
            Expect(housesLow == 0, $"{name}: every house stands up off the road ({housesLow} with a corner below the kerb)");
            int walk = 0, walkLow = 0;
            foreach (var p in layout.sidewalks.paths)
                foreach (var q in p.points)
                {
                    walk++;
                    if (kerbs.Ground(q.x, q.y) != 0) walkLow++;
                }
            // (A sidewalk's ends reach a little way round a corner, over the cross street's kerb: a few points.)
            Expect(walkLow <= walk / 50, $"{name}: the sidewalks' walking lines up at sidewalk height ({walkLow} of {walk} points not)");
            int inLane = 0, inLaneHigh = 0, offStreet = 0, offStreetLow = 0;
            foreach (var c in layout.cars)
            {
                if (c.vehicle > 0) continue;
                int t = Grid.Index((int)Math.Floor(c.x), (int)Math.Floor(c.y), w);
                byte use = layout.use[t];
                if (use == Use.DRIVEWAY || use == Use.CAR_PARK)
                {
                    offStreet++;
                    if (kerbs.Ground(c.x, c.y) != 0) offStreetLow++;
                }
                else
                {
                    inLane++;
                    if (kerbs.Ground(c.x, c.y) != -Kerbs.HEIGHT) inLaneHigh++;
                }
            }
            Expect(inLaneHigh == 0, $"{name}: every car parked in a parking lane down on the road ({inLaneHigh} of {inLane} not)");
            Expect(offStreetLow == 0, $"{name}: every car in a driveway or car park up off the road ({offStreetLow} of {offStreet} not)");

            // The ground eases over a kerb: never more than a third of a kerb's height between two points a fiftieth of a tile apart.
            double steepest = 0;
            var rng = new Rng(name + "::kerbs");
            for (int k = 0; k < 200000; k++)
            {
                double x = rng.Next() * w, y = rng.Next() * h, a = rng.Next() * Math.PI * 2;
                double d = Math.Abs(kerbs.Ground(x, y) - kerbs.Ground(x + Math.Cos(a) * 0.02, y + Math.Sin(a) * 0.02));
                steepest = Math.Max(steepest, d);
            }
            Expect(steepest <= Kerbs.HEIGHT / 3, $"{name}: the ground eases over the kerbs, no jumps (at most {steepest:0.0000} tiles in a fiftieth of a tile)");

            // The paving on the sidewalks.
            var paving = kerbs.Paving(layout, Raster.BulbRadius(model.@params));
            int joints = paving.Count(p => !p.crack), cracks = paving.Count(p => p.crack);
            int offSidewalk = paving.Count(p => new[] { p.a, p.b }.Any(q => kerbs.Edge(q.x, q.y) < Kerbs.TOP - 0.03 || kerbs.Edge(q.x, q.y) > Layout.SIDEWALK + 0.03));
            Expect(joints > 0 && cracks > 0 && offSidewalk == 0, $"{name}: {joints} joints between the sidewalks' slabs and {cracks} lines of cracks, all on a sidewalk ({offSidewalk} off one)");
            corners += paving.Count * 4;
            // Against what's no longer drawn: the ground's tiles as they were, and the three layers the sidewalks were
            // drawn in under the asphalt (edging, sidewalk, kerb), each a ribbon along every street, as MeshBuilder lays one.
            long ribbons = 0;
            foreach (var r in layout.roads)
            {
                if (r.kind == RoadKind.Path) continue;
                Vec2? last = null;
                for (int k = 1; k < r.points.Count; k++)
                {
                    double dx = r.points[k].x - r.points[k - 1].x, dy = r.points[k].y - r.points[k - 1].y, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-9) continue;
                    ribbons += 4;
                    if (last != null && last.Value.x * dx / len + last.Value.y * dy / len < 0.9998) ribbons += 24;
                    last = new Vec2(dx / len, dy / len);
                }
                ribbons += 2 * 36;
            }
            long before = (long)w * h * 4 + ribbons * 3;
            Console.WriteLine($"     {name}: the ground, kerbs and paving {corners} corners to draw, against {before} for the ground and the sidewalks' layers before ({(double)corners / before:0.00} times)");
        }
        Console.WriteLine(fails.Count == 0 ? "kerbs: all ok" : $"kerbs: {fails.Count} failed");
        return fails.Count == 0 ? 0 : 1;
    }

    static Vec2 Centroid(List<Vec2> poly)
    {
        double x = 0, y = 0;
        foreach (var p in poly)
        {
            x += p.x;
            y += p.y;
        }
        return new Vec2(x / poly.Count, y / poly.Count);
    }
}

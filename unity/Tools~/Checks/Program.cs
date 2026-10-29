// Checks the game's engine-free code (Assets/Porchlight/Runtime/Shared) on
// plain .NET, without Unity:
//
//   dotnet run --project "unity/Tools~/Checks" -- towns [count]     # grow towns with every invariant checked
//   dotnet run --project "unity/Tools~/Checks" -- net '<json>'      # play the real game server (NetCheck.cs)

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
        if (args.Length > 0 && args[0] == "towns") return Towns(args.Length > 1 ? int.Parse(args[1]) : 50);
        Console.Error.WriteLine("usage: towns [count] | net '<json from Tools~/net/server.ts>'");
        return 2;
    }

    /// <summary>
    /// Grows `count` towns from fixed seeds, over a spread of sizes and densities,
    /// with the generator checking every stage's invariants, and twice each: the
    /// same seed must give the same town (everyone in a room grows it themselves).
    /// </summary>
    static int Towns(int count)
    {
        int failed = 0;
        long buildings = 0, driveways = 0, beds = 0, parked = 0, airports = 0, planes = 0, docks = 0, boats = 0, loops = 0, culs = 0;
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
                docks += layout.docks.Count;
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
                string culs2 = CulDeSacs(a, layout);
                if (culs2 != null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}: {culs2}");
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
        Console.WriteLine($"{count - failed}/{count} towns ok in {sw.Elapsed.TotalSeconds:0.0} s: {buildings} buildings, {driveways} driveway tiles, {beds} garden beds, {parked} parked cars, {culs} cul-de-sacs; {airports} airports, {planes} planes, {docks} docks, {boats} moored boats, {loops} boat loops");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The airport has 8-10 planes, all on its apron; docks run out over open
    /// water, their boats float, and the boats' loops keep to the water.
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
        foreach (var d in layout.docks)
            foreach (var t in d.tiles)
                if (!Wet(t.x + 0.5, t.y + 0.5)) return $"a dock on land at ({t.x}, {t.y})";
        foreach (var loop in layout.boatLoops)
            foreach (var q in loop)
                if (!Wet(q.x, q.y)) return $"a boat loop runs aground at ({q.x:0.0}, {q.y:0.0})";
        return null;
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
        return null;
    }
}

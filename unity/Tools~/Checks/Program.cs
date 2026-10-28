// Checks the game's engine-free code (Assets/Porchlight/Runtime/Shared) on
// plain .NET, without Unity:
//
//   dotnet run --project "unity/Tools~/Checks" -- towns [count]     # grow towns with every invariant checked
//   dotnet run --project "unity/Tools~/Checks" -- net '<json>'      # play the real game server (NetCheck.cs)

using System;
using System.Diagnostics;
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
        long buildings = 0, driveways = 0, beds = 0, parked = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            string seed = $"check-{i}";
            var p = new GenParams { width = 64 + (i % 4) * 32, height = 64 + ((i / 4) % 4) * 32, roadDensity = 0.5 + (i % 5) * 0.1 };
            p = GenParams.Normalize(p);
            try
            {
                var a = Generator.Generate(seed, p, check: true);
                var b = Generator.Generate(seed, p);
                // The street scene too: parking lanes, driveways and gardens.
                var layout = Porchlight.Explore.Layout.Build(a);
                buildings += a.buildings.Count;
                parked += layout.cars.Count;
                foreach (byte u in layout.use)
                {
                    if (u == Porchlight.Explore.Use.DRIVEWAY) driveways++;
                    if (u == Porchlight.Explore.Use.GARDEN) beds++;
                }
                string shared = i == 0 ? Shared(a, layout) : null;
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
        Console.WriteLine($"{count - failed}/{count} towns ok in {sw.Elapsed.TotalSeconds:0.0} s: {buildings} buildings, {driveways} driveway tiles, {beds} garden beds, {parked} parked cars");
        return failed == 0 ? 0 : 1;
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
        if (layout.sidewalks.paths.Count > 1)
        {
            var from = layout.sidewalks.paths[0].points[0];
            var to = layout.sidewalks.paths[layout.sidewalks.paths.Count - 1].points[0];
            var walk = Porchlight.Explore.SidewalkWalk.Toward(layout.sidewalks, new Porchlight.Core.Rng("f"), from, to);
            if (walk != null)
            {
                var route = walk.RoutePoints();
                var end = route[route.Count - 1];
                if (Math.Sqrt((end.x - to.x) * (end.x - to.x) + (end.y - to.y) * (end.y - to.y)) > 0.5) return "the way to a player stops short";
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

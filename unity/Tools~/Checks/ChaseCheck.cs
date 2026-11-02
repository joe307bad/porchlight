// Sets chasers (a mission's police) after a target in grown towns: one stood
// outside the zoo's gate, and one walking along a sidewalk. Each chaser starts
// in a parking lane 9 to 16 tiles off (and is sent in again from there if it
// loses them) and must come within reach in time.
//
//   dotnet run --project "unity/Tools~/Checks" -- chase [count]

using System;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class ChaseCheck
{
    const double SPEED = 4.4;
    const double REACH = 1.0;
    const double TIME = 40;

    public static int Run(int count)
    {
        int failed = 0, runs = 0, restarts = 0;
        double took = 0;
        for (int i = 0; i < count; i++)
        {
            string seed = $"check-{i}";
            var p = GenParams.Normalize(new GenParams { width = 64 + (i % 4) * 32, height = 64 + ((i / 4) % 4) * 32, roadDensity = 0.5 + (i % 5) * 0.1, frontSetback = (i / 2) % 4 });
            var model = Generator.Generate(seed, p);
            var layout = Layout.Build(model);
            var open = Vehicle.Drivable(model, layout);
            var rng = new Rng(seed + "::chase");
            if (model.zoo == null) continue;
            var gate = Menagerie.Gate(model.zoo);
            var dv = Menagerie.AlongV(model.zoo);
            var outside = new Vec2(gate.x + dv.x * 0.6, gate.y + dv.y * 0.6);
            var path = layout.sidewalks.paths.Count > 0 ? layout.sidewalks.paths[rng.Int(0, layout.sidewalks.paths.Count - 1)] : null;
            foreach (bool walking in new[] { false, true })
            {
                if (walking && (path == null || path.Length < 4)) continue;
                Vec2 Target(double t)
                {
                    if (!walking) return outside;
                    // Back and forth along the sidewalk at a walk.
                    double s = (t * 1.6) % (path.Length * 2);
                    if (s > path.Length) s = path.Length * 2 - s;
                    return AutoDrive.PointAt(path.points, path.at, s);
                }
                var start = Chaser.StartNear(Target(0), 9, 16, layout.roads, open, Array.Empty<Vec2>(), rng);
                runs++;
                if (start == null)
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}{(walking ? " walking" : "")}: nowhere to start a chaser");
                    continue;
                }
                var chaser = new Chaser(new Vehicle(Array.IndexOf(Catalog.CARS, "cars/police"), start.Value.x, start.Value.y, start.Value.heading));
                double t = 0, best = double.PositiveInfinity;
                for (; t < TIME; t += 0.05)
                {
                    var target = Target(t);
                    chaser.Step(target, SPEED, 0.05, layout.roads, open);
                    if (chaser.Lost)
                    {
                        var again = Chaser.StartNear(target, 9, 16, layout.roads, open, Array.Empty<Vec2>(), rng);
                        if (again != null) chaser.Restart(again.Value);
                        restarts++;
                    }
                    best = Math.Min(best, Geo.Dist(new Vec2(chaser.car.x, chaser.car.y), target));
                    if (best < REACH) break;
                }
                if (best < REACH) took += t;
                else
                {
                    failed++;
                    Console.WriteLine($"FAIL {seed}{(walking ? " walking" : "")}: closest {best:0.0} tiles in {TIME} s (from {Geo.Dist(new Vec2(start.Value.x, start.Value.y), Target(0)):0.0} tiles, ended at {chaser.car.x:0.0},{chaser.car.y:0.0})");
                }
            }
        }
        Console.WriteLine($"{runs - failed}/{runs} chases caught up, in {took / Math.Max(1, runs - failed):0.0} s on average, {restarts} sent in again");
        return failed == 0 ? 0 : 1;
    }
}

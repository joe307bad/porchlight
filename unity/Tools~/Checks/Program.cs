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
        Console.WriteLine($"{count - failed}/{count} towns ok in {sw.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
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

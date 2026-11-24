// What each town's buildings are for (Explore/Zoning.cs): every building zoned,
// every house still a home (people live in them), and the towns that come with
// the game balanced, about a job for each working resident. Also a few towns of
// every kind (the `towns` check's), to show the balance holds beyond those.
//
//   dotnet run -c Release --project "unity/Tools~/Checks" -- zoning [key ...]

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class ZoningCheck
{
    public static int Run(string[] keys)
    {
        var towns = keys.Length > 0
            ? keys.Select(k => (name: k, key: k)).ToList()
            : CountyCheck.PublicTowns().Select(t => (name: t.name, key: t.key)).ToList();
        var fails = new List<string>();
        foreach (var (name, key) in towns)
        {
            var (seed, p) = Program.ParseKey(key);
            var model = Generator.Generate(seed, p);
            var layout = Layout.Build(model);
            var z = Zoning.Plan(model, layout);
            Report(name, z);
            fails.AddRange(Check(name, model, layout, z, balanced: true));
            fails.AddRange(ShopsCheck.Check(name, model, layout, z, strict: true));
        }
        if (keys.Length == 0)
        {
            for (int i = 0; i < 6; i++)
            {
                var p = GenParams.Normalize(new GenParams { width = 96 + (i % 3) * 32, height = 96 + (i % 2) * 32, roadDensity = 0.6 + (i % 4) * 0.1 });
                var model = Generator.Generate($"zoning-{i}", p);
                var layout = Layout.Build(model);
                var z = Zoning.Plan(model, layout);
                Report($"zoning-{i}", z);
                // A small town may be all houses, too few to balance: only the bookkeeping is checked.
                fails.AddRange(Check($"zoning-{i}", model, layout, z, balanced: false));
                fails.AddRange(ShopsCheck.Check($"zoning-{i}", model, layout, z, strict: false));
            }
        }
        foreach (var f in fails) Console.WriteLine("FAIL " + f);
        return fails.Count == 0 ? 0 : 1;
    }

    static void Report(string name, TownZoning z)
    {
        var zones = string.Join(", ", Enum.GetValues<Zone>().Select(k => $"{z.Count(k)} {k}"));
        string band = z.JobsPerHome < Zoning.BALANCED_LOW ? "too few jobs" : z.JobsPerHome > Zoning.BALANCED_HIGH ? "too many jobs" : "balanced";
        int flats = z.buildings.Count(b => b.zone == Zone.Residential && b.homes > 1);
        Console.WriteLine($"{name}: {z.buildings.Count} buildings ({zones}; {flats} of the homes blocks of flats); {z.homes} homes, {z.residents} residents, {z.workers} workers, {z.jobs} jobs: {z.JobsPerHome:0.00} a home, {band}");
    }

    static IEnumerable<string> Check(string name, TownModel model, IsoLayout layout, TownZoning z, bool balanced)
    {
        if (z.buildings.Count != layout.houses.Count(h => h.tiles != null && h.tiles.Count > 0))
            yield return $"{name}: {z.buildings.Count} buildings zoned of {layout.houses.Count}";
        var byId = z.buildings.ToDictionary(b => b.buildingId);
        foreach (var house in Homes.All(model))
            if (byId.TryGetValue(house.id, out var b) && b.homes < 1) yield return $"{name}: house {house.id} ({b.zone}) has no home in it";
        foreach (var b in z.buildings)
        {
            if (b.homes < 0 || b.jobs < 0) yield return $"{name}: building {b.buildingId} has {b.homes} homes, {b.jobs} jobs";
            if (b.homes == 0 && b.jobs == 0) yield return $"{name}: building {b.buildingId} ({b.zone}) is for nothing";
            if ((b.zone == Zone.Residential) != (b.jobs == 0)) yield return $"{name}: building {b.buildingId} is {b.zone} with {b.jobs} jobs";
            if (b.x1 <= b.x0 || b.y1 <= b.y0 || b.x0 < 0 || b.y0 < 0 || b.x1 > model.width || b.y1 > model.height) yield return $"{name}: building {b.buildingId}'s box is off the map";
        }
        // The same town, the same zoning.
        var again = Zoning.Plan(Generator.Generate(model.seed, model.@params));
        if (!Zoning.Encode(again).SelectMany(x => x).SequenceEqual(Zoning.Encode(z).SelectMany(x => x))) yield return $"{name}: zoned differently the second time";
        if (balanced && (z.JobsPerHome < Zoning.BALANCED_LOW || z.JobsPerHome > Zoning.BALANCED_HIGH))
            yield return $"{name}: {z.JobsPerHome:0.00} jobs a home, outside {Zoning.BALANCED_LOW}–{Zoning.BALANCED_HIGH}";
    }
}

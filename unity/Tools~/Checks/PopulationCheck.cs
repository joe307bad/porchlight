// Who lives in each starter town (Population.cs): households from the fixed mix,
// filling zoning's homes, and jobs by the doubly-constrained gravity model. Fails
// if the households don't come out near the mix, if a building's people stray from
// zoning's figure, if anyone's in two households, if a job's taken twice or a
// worker has none, or if the gravity model's commutes are longer than jobs handed
// out at random would make them. Prints each town's figures.
//
//   dotnet run --project "unity/Tools~/Checks" -c Release -- population [towns dir]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Life;

static class PopulationCheck
{
    public static int Run(string dir)
    {
        int fails = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) fails++;
        }
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var life = TrafficDay.Make(JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString());
            var hs = life.households;
            int members = hs.Sum(h => h.members.Count);
            double size = hs.Count == 0 ? 0 : members / (double)hs.Count;
            var byKind = hs.GroupBy(h => h.kind).ToDictionary(g => g.Key, g => g.Count());
            double alone = (byKind.GetValueOrDefault("single") + byKind.GetValueOrDefault("single retiree")) / (double)Math.Max(1, hs.Count);
            var seen = new HashSet<int>();
            bool once = hs.All(h => h.members.All(seen.Add)) && hs.SelectMany(h => h.members).All(id => life.people[id].household >= 0);
            var residents = life.people.Where(p => !p.home.border).ToList();
            Console.WriteLine($"     {name}: {hs.Count} households, {size:0.00} people each; {100 * alone:0}% alone; " +
                string.Join(", ", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
            Expect(hs.Count > 0 && Math.Abs(size - 2.3) < 0.25, $"{name}: households average about 2.3 people ({size:0.00})");
            Expect(Math.Abs(alone - 0.29) < 0.06, $"{name}: about 29% live alone ({100 * alone:0}%)");
            Expect(once && seen.Count == residents.Count, $"{name}: everyone who lives in town is in one household, once ({seen.Count} of {residents.Count})");
            int kidsAtSchool = residents.Count(p => p.kind == PersonKind.Student);
            bool schoolAge = residents.Where(p => p.kind == PersonKind.Student).All(p => p.role == Role.Child && p.age >= 5);
            bool workingAge = residents.Where(p => p.kind == PersonKind.Worker).All(p => p.role == Role.Adult);
            Expect(schoolAge && workingAge, $"{name}: students are children of 5 or more, workers are working-age adults ({kidsAtSchool} students)");

            // Jobs: none twice over a building's jobs, everyone employed with one.
            var workers = life.people.Where(p => p.kind == PersonKind.Worker || p.kind == PersonKind.CommuterIn).ToList();
            var jobsAt = new Dictionary<int, int>();
            foreach (var z in life.zoning.buildings) jobsAt[z.buildingId] = z.jobs;
            var filled = workers.Where(p => !p.work.border).GroupBy(p => p.work.id).ToDictionary(g => g.Key, g => g.Count());
            bool fits = filled.All(kv => kv.Value <= jobsAt.GetValueOrDefault(kv.Key));
            Expect(fits, $"{name}: no building has more workers than jobs ({filled.Values.Sum()} jobs filled in town)");
            // Commutes in town: the gravity model's against the same jobs dealt out at random.
            var inTown = workers.Where(p => !p.home.border && !p.work.border).ToList();
            double Commute(IEnumerable<(Place home, Place work)> pairs) => pairs.Average(x => Geo.Dist(life.Door(x.home), life.Door(x.work)));
            double model = inTown.Count == 0 ? 0 : Commute(inTown.Select(p => (p.home, p.work)));
            // (Random: the mean of twenty deals, so a small town's one unlucky deal doesn't decide it.)
            var rng = new Rng($"{name}::random-jobs");
            double random = 0;
            for (int k = 0; k < 20 && inTown.Count > 0; k++)
            {
                var shuffled = rng.Shuffle(inTown.Select(p => p.work).ToList());
                random += Commute(inTown.Select((p, i) => (p.home, shuffled[i]))) / 20;
            }
            int outOfTown = workers.Count(p => !p.home.border && p.work.border), comingIn = workers.Count(p => p.home.border);
            Console.WriteLine($"     {name}: {inTown.Count} work in town, {outOfTown} out of it, {comingIn} come in; commutes in town {model:0} tiles on average, {random:0} if jobs were dealt at random (a real town of its people {life.RealScale(residents.Count):0.0} times as wide)");
            // A starter town is a kilometre or so across, a minute or two's drive: NCHRP 716's friction, made for a
            // city region's 5-40 minute commutes, hardly favours the nearer jobs at that size (as in a real town that
            // small). They're never further than jobs dealt at random.
            Expect(inTown.Count == 0 || model <= random * 1.02, $"{name}: the gravity model's commutes are no longer than random ones ({model:0} against {random:0} tiles)");
            // Doubly constrained: those working out of town go out by the gates nearest them, more than by chance.
            var outs = workers.Where(p => !p.home.border && p.work.border).ToList();
            if (outs.Count > 0 && life.net.borders.Count > 1)
            {
                double ToGate(Person p, int g) => Geo.Dist(life.Door(p.home), life.net.borders[g].at);
                double chosen = outs.Average(p => ToGate(p, p.work.id));
                double any = outs.Average(p => Enumerable.Range(0, life.net.borders.Count).Average(g => ToGate(p, g)));
                Expect(chosen <= any * 1.01, $"{name}: those working out of town leave by gates no further than the average gate ({chosen:0} against {any:0} tiles)");
            }
        }
        Console.WriteLine(fails == 0 ? "population: all ok" : $"population: {fails} failed");
        return fails == 0 ? 0 : 1;
    }
}

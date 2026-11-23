// Everyone's name (Shared/Life/PersonNames.cs) and what they're doing, in each
// starter town: every person and wanderer named "First Last" from faker-js's
// lists; the same name for the same person in a town built again, whatever the
// time (the game, the server and Manage each build their own); different
// people different names, and the same id in another town someone else; and
// what each is doing as their trip has it: going to work in the morning rush,
// going home in the evening's.
//
//   dotnet run --project "unity/Tools~/Checks" -- names [server/towns]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;

static class NamesCheck
{
    public static int Run(string dir)
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }
        var firstOfTown = new List<List<string>>();
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string town = Path.GetFileNameWithoutExtension(f);
            var (seed, settings) = TownKey.Parse(JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString());
            var model = Generator.Generate(seed, settings);
            var layout = Layout.Build(model);
            var zoning = Zoning.Plan(model, layout);
            var life = new TownLife(model, layout, zoning);
            life.Start(TrafficDay.Morning);
            int n = life.people.Count;
            var names = Enumerable.Range(0, n).Select(life.NameOf).ToList();
            firstOfTown.Add(names.Take(50).ToList());

            int malformed = names.Count(x => x == null || x.Split(' ').Length < 2 || x.Split(' ').Any(w => w.Length == 0));
            Expect(n > 0 && malformed == 0, $"{town}: all {n} people named \"First Last\" ({malformed} not)");
            int distinct = names.Distinct().Count();
            Expect(distinct >= n * 0.98, $"{town}: different people, different names ({distinct} of {n})");

            // Built again, as each game and the server do, later in the day: the same people, the same names.
            var again = new TownLife(model, layout, zoning);
            again.Start(TrafficDay.Morning + 9 * TownClock.HOUR);
            int differ = Enumerable.Range(0, n).Count(i => again.NameOf(i) != names[i] || PersonNames.Of(seed, i) != names[i]);
            Expect(again.people.Count == n && differ == 0, $"{town}: the same names in a town built again, hours later ({differ} differ)");

            // What they're doing: their trip's purpose, in words; the morning rush to work, the evening's home.
            string Expected(Person p) => p.trip.purpose switch
            {
                Purpose.Home => "Going home",
                Purpose.Work => "Going to work",
                Purpose.School => "Going to school",
                Purpose.Shop => "Going to the shops",
                _ => null,
            };
            int wrong = 0, toWork = 0;
            foreach (var p in life.people)
            {
                if (p.trip == null) continue;
                string want = Expected(p), got = life.DoingOf(p.id);
                if (want != null && got != want) wrong++;
                if (got == "Going to work") toWork++;
            }
            Expect(wrong == 0 && toWork > 0, $"{town}: at 8:15 AM, {toWork} going to work, each as their trip has it ({wrong} wrong)");
            life.RunUntil(TrafficDay.Morning + 9 * TownClock.HOUR + 15 * TownClock.MINUTE);
            int home = life.people.Count(p => p.trip != null && life.DoingOf(p.id) == "Going home");
            Expect(home > 0, $"{town}: at 5:30 PM, {home} going home");
            // Everyone drawn walking somewhere (the game's tags over them) is someone, with a name.
            life.Draw();
            int shown = 0, unnamed = 0;
            for (int s = 0; s < life.personSlots.Length; s++)
            {
                if (!life.personSlots[s].shown) continue;
                shown++;
                int id = life.PersonInSlot(s);
                if (id < 0 || string.IsNullOrEmpty(life.NameOf(id)) || string.IsNullOrEmpty(life.DoingOf(id))) unnamed++;
            }
            Expect(shown > 0 && unnamed == 0, $"{town}: each of the {shown} out walking has a name and something they're doing ({unnamed} not)");

            var wanderers = Enumerable.Range(0, 200).Select(k => PersonNames.Wanderer(seed, k)).ToList();
            Expect(wanderers.Distinct().Count() >= 196 && wanderers.SequenceEqual(Enumerable.Range(0, 200).Select(k => PersonNames.Wanderer(seed, k))),
                $"{town}: its wanderers named, each their own, the same every time");
        }
        if (firstOfTown.Count >= 2)
        {
            int same = firstOfTown[0].Zip(firstOfTown[1], (a, b) => a == b).Count(x => x);
            Expect(same <= 2, $"person 0–49 in one town aren't the same people in the next ({same} share a name)");
        }
        Expect(PersonNames.FirstCount > 1000 && PersonNames.LastCount > 400, $"faker-js's lists: {PersonNames.FirstCount} first names, {PersonNames.LastCount} last");
        Console.WriteLine(fails.Count == 0 ? "names: all ok" : $"names: {fails.Count} failed");
        return fails.Count == 0 ? 0 : 1;
    }
}

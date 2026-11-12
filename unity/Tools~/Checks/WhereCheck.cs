// The line under your name says where you are now (CountyMap.Where,
// Directions.Currently): "Currently in Ember Crossing", "Currently on the
// Porchlight – South road", never where you live.
//
//   dotnet run --project "unity/Tools~/Checks" -- where

using System;
using System.Collections.Generic;
using Porchlight.County;
using Porchlight.Explore;

static class WhereCheck
{
    public static int Run()
    {
        var fails = new List<string>();
        void Expect(string got, string want, string what)
        {
            bool ok = got == want;
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {what}: \"{got}\"{(ok ? "" : $", wanted \"{want}\"")}");
            if (!ok) fails.Add(what);
        }

        // Every space of the county says where it is.
        var map = CountyMap.Build(CountyCheck.PublicTowns(), "acct");
        int towns = 0, roads = 0;
        foreach (var s in map.spaces)
        {
            string w = map.Where(s);
            if (s.kind == SpaceKind.Town)
            {
                towns++;
                if (w != "in " + s.name) { fails.Add($"town {s.name}: {w}"); Console.WriteLine($"FAIL town {s.name}: \"{w}\""); }
            }
            else if (s.kind == SpaceKind.Highway)
            {
                roads++;
                if (w != $"on the {map.highways[s.highway].name} road") { fails.Add($"highway {s.name}: {w}"); Console.WriteLine($"FAIL highway {s.name}: \"{w}\""); }
            }
            else if (string.IsNullOrEmpty(w) || !(w.StartsWith("at ") || w.StartsWith("on "))) { fails.Add($"{s.kind} {s.name}: {w}"); Console.WriteLine($"FAIL {s.kind} {s.name}: \"{w}\""); }
        }
        Console.WriteLine($"{(fails.Count == 0 ? "ok" : "FAIL")} every space of the county says where it is ({towns} towns, {roads} stretches of highway, {map.spaces.Count - towns - roads} more)");
        Expect(map.Where(null), null, "nowhere known");

        // The line under your name.
        var ember = map.spaces.Find(s => s.kind == SpaceKind.Town && s.name == "Ember Crossing");
        Expect(Directions.Currently(map.Where(ember), ""), "Currently in Ember Crossing", "in a town, just joined");
        Expect(Directions.Currently(map.Where(ember), "2 here"), "Currently in Ember Crossing · 2 here", "in a town, online");
        var road = map.spaces.Find(s => s.kind == SpaceKind.Highway);
        Expect(Directions.Currently(map.Where(road), "Joining…"), $"Currently on the {map.highways[road.highway].name} road · Joining…", "out on a highway, joining the next room");
        Expect(Directions.Currently(null, "Just you here"), "Just you here", "where not known yet");
        Expect(Directions.Currently(null, ""), "", "nothing known (the line's hidden)");

        Console.WriteLine(fails.Count == 0 ? "where: all ok" : $"where: {fails.Count} failed");
        return fails.Count == 0 ? 0 : 1;
    }
}

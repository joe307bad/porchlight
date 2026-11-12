// One way shown at a time (Shared/Explore/WayChoice.cs), as the game chooses
// them: a spot clicked on the map is instead of following someone, or a trip
// to a house or town; and a trip or following someone is instead of a spot.
//
//   dotnet run --project "unity/Tools~/Checks" -- ways

using System;
using System.Collections.Generic;
using Porchlight.Explore;

static class WayCheck
{
    public static int Run()
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }

        // Following someone, then a spot picked on the map: following stops (the bug: it carried on and took the way back).
        var c = new WayChoice();
        Expect(c.Choose(Way.Player, "ada").way == Way.None, "following someone with nothing chosen stops nothing");
        var was = c.Choose(Way.Spot);
        Expect(was.way == Way.Player && was.id == "ada", "a spot on the map stops following them");
        Expect(!c.Showing(Way.Player, "ada"), "...and they're no longer the way shown (so following doesn't take it back)");
        Expect(c.Showing(Way.Spot), "...the spot is");

        // Following someone into another town (a trip there, which is still following): the map's spot stops it all.
        c = new WayChoice();
        c.Choose(Way.Player, "ada");
        c.Ended(Way.Trip, "player:ada");
        Expect(c.Showing(Way.Player, "ada"), "the way to someone in another town ending isn't following them ending");
        Expect(c.Choose(Way.Spot).way == Way.Player, "a spot on the map stops following someone in another town");

        // A trip to a house (or a town on the county map), then a spot: the trip stops.
        c = new WayChoice();
        c.Choose(Way.Trip, "house:1");
        was = c.Choose(Way.Spot);
        Expect(was.way == Way.Trip && was.id == "house:1", "a spot on the map stops the way to a house");
        c.Choose(Way.Trip, "town:abc");
        Expect(c.Choose(Way.Spot).way == Way.Trip, "a spot on the map stops the way to a town");

        // And the other way round: following someone, or a trip, instead of a spot.
        c = new WayChoice();
        c.Choose(Way.Spot);
        Expect(c.Choose(Way.Player, "bo").way == Way.Spot, "following someone is instead of the spot");
        Expect(c.Choose(Way.Trip, "house:2").way == Way.Player, "a trip to a house is instead of following someone");
        Expect(c.Choose(Way.Trip, "house:3").way == Way.Trip, "a trip to another house is instead of the first");

        // The same again stops nothing; an old trip ending doesn't end the one that replaced it.
        c = new WayChoice();
        c.Choose(Way.Player, "bo");
        Expect(c.Choose(Way.Player, "bo").way == Way.None, "following the same person again stops nothing");
        c.Choose(Way.Trip, "house:2");
        c.Choose(Way.Trip, "house:3");
        c.Ended(Way.Trip, "house:2");
        Expect(c.Showing(Way.Trip, "house:3"), "the trip replaced ending leaves the new one");
        c.Ended(Way.Trip, "house:3");
        Expect(c.Current == Way.None, "the trip ending (you got there) leaves nothing shown");
        c.Choose(Way.Spot);
        c.Ended(Way.Player, "bo");
        Expect(c.Showing(Way.Spot), "following someone ending (they stopped playing) doesn't clear a spot chosen since");

        Console.WriteLine(fails.Count == 0 ? "ways: all ok" : $"ways: {fails.Count} failed");
        return fails.Count == 0 ? 0 : 1;
    }
}

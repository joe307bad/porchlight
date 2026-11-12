// The turns a way shown takes (RouteTurns, for the arrows put up at them), and a
// trip's way on through a town ahead (CountyRoute.Ahead with the town's streets):
// along them, or stopped at its gate, never straight across it.
//
//   dotnet run --project "unity/Tools~/Checks" -- turns

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.County;
using Porchlight.Explore;
using Porchlight.Model;

static class TurnsCheck
{
    public static int Run()
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }
        const double MIN = 40 * Math.PI / 180;

        // East 20 tiles, then south (down the map: a right turn) 20.
        var corner = new List<Vec2> { new Vec2(0, 0), new Vec2(20, 0), new Vec2(20, 20) };
        var t = RouteTurns.Find(corner, MIN, 0, 100, 3);
        Expect(t.Count == 1, $"a corner is one turn (got {t.Count})");
        if (t.Count == 1)
        {
            Expect(Geo.Dist(t[0].at, new Vec2(20, 0)) < 1.01, $"at the corner (got {t[0].at})");
            Expect(t[0].dir.y > 0.95, $"pointing the new way, south (got {t[0].dir})");
            Expect(t[0].angle > 0, $"a right turn (angle {t[0].angle:0.00})");
            Expect(Math.Abs(t[0].along - 20) < 1.01, $"20 tiles along (got {t[0].along:0.0})");
        }
        // Too far along, or too near: none.
        Expect(RouteTurns.Find(corner, MIN, 0, 15, 3).Count == 0, "a turn past the reach isn't shown");
        Expect(RouteTurns.Find(corner, MIN, 25, 100, 3).Count == 0, "a turn already passed isn't shown");

        // Straight on, with a kink where two ways were joined (a step on and a step back): no turn.
        var kink = new List<Vec2> { new Vec2(0, 0), new Vec2(20, 0), new Vec2(21, 0), new Vec2(20.2, 0), new Vec2(40, 0) };
        Expect(RouteTurns.Find(kink, MIN, 0, 100, 3).Count == 0, "a kink isn't a turn");

        // A long, gentle bend (radius 60): no turn.
        var bend = new List<Vec2>();
        for (int k = 0; k <= 40; k++) bend.Add(new Vec2(60 * Math.Sin(k * 0.02), 60 - 60 * Math.Cos(k * 0.02)));
        Expect(RouteTurns.Find(bend, MIN, 0, 100, 3).Count == 0, "a gentle bend isn't a turn");

        // Left, then right: two turns, the nearest first, each its own way.
        var zig = new List<Vec2> { new Vec2(0, 0), new Vec2(20, 0), new Vec2(20, -20), new Vec2(40, -20) };
        var z = RouteTurns.Find(zig, MIN, 0, 100, 3);
        Expect(z.Count == 2 && z[0].angle < 0 && z[1].angle > 0 && z[0].along < z[1].along, $"left then right (got {string.Join(", ", z.Select(q => $"{q.angle:0.00} at {q.along:0}"))})");

        // A trip on through a stop: with no streets known there, the way ahead ends at its gate; with them, it
        // goes along them (here a stand-in way through a spot in the middle), never straight from gate to gate.
        var map = CountyMap.Build(CountyCheck.PublicTowns(), "acct");
        int tried = 0;
        foreach (var from in map.spaces.Where(s => s.kind == SpaceKind.Town))
            foreach (var to in map.spaces.Where(s => s.kind == SpaceKind.Town && s != from))
            {
                var mid = new Vec2(from.x0 + from.width / 2.0, from.y0 + from.height / 2.0);
                var plain = CountyRoute.Ahead(map, from, mid, to, 1e9);
                Space crossed = null;
                Vec2 into = default, outOf = default;
                var stopped = CountyRoute.Ahead(map, from, mid, to, 1e9, (s, a, b) =>
                {
                    crossed ??= s;
                    if (crossed == s)
                    {
                        into = a;
                        outOf = b;
                    }
                    return null;
                });
                if (crossed == null) continue;
                tried++;
                if (tried > 6) continue;
                Expect(stopped.Count > 0 && Geo.Dist(stopped[stopped.Count - 1], into) < 1e-6 && stopped.Count < plain.Count,
                    $"{from.name} to {to.name}: unknown streets in {crossed.name ?? crossed.kind.ToString()}, the way stops at its gate");
                var across = new Vec2((into.x + outOf.x) / 2 + 7, (into.y + outOf.y) / 2 + 7);
                var streets = CountyRoute.Ahead(map, from, mid, to, 1e9, (s, a, b) => new List<Vec2> { a, across, b });
                Expect(streets.Any(q => Geo.Dist(q, across) < 1e-6), $"{from.name} to {to.name}: through {crossed.name ?? crossed.kind.ToString()} along its streets");
            }
        Expect(tried > 0, $"trips through a stop tried ({tried})");

        Console.WriteLine(fails.Count == 0 ? "turns: all ok" : $"turns: {fails.Count} FAILED");
        return fails.Count == 0 ? 0 : 1;
    }
}

// Crowds at the kerb (Shared/Life/Walkways.cs CrowdSpot, Life/Walking.cs): whoever
// comes takes the frontmost spot free, and keeps it; and in each starter town, at
// every crossing at a junction, the first twenty to wait each get
// a spot of their own, at least half a metre from every other, on the sidewalk and
// off every carriageway; and at the morning rush, those waiting at a corner stand
// in their spots, none on another, the same spot from one look to the next, and
// facing the way over.
//
//   dotnet run --project "unity/Tools~/Checks" -c Release -- crowd [server/towns]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

static class CrowdCheck
{
    /// <summary>A crowd this big at every crossing.</summary>
    const int N = 20;
    /// <summary>Half a metre (a tile is 4 m), less a hair for rounding.</summary>
    const double APART = Walkways.CROWD_APART - 1e-9;

    public static int Run(string dir)
    {
        var fails = new List<string>();
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) fails.Add(what);
        }
        // The book: thirty come, five go, three more come and fill the front three of the gaps, in order.
        var book = new KerbCrowds();
        var got = Enumerable.Range(0, 30).Select(id => book.Join(7, id)).ToList();
        Expect(got.SequenceEqual(Enumerable.Range(0, 30)), "the book: each newcomer to an empty kerb takes the next spot back");
        foreach (int k in new[] { 11, 2, 25, 7, 29 }) book.Leave(7, k, k);
        book.Leave(7, 3, 99);
        var refill = new[] { book.Join(7, 100), book.Join(7, 101), book.Join(7, 102) };
        Expect(refill.SequenceEqual(new[] { 2, 7, 11 }), $"the book: newcomers take the frontmost spots free ({string.Join(", ", refill)}), and nobody's moved by another leaving ({book.At(7)[3]} in spot 3)");
        Expect(book.At(7).Count == 29 && book.At(7)[25] == -1, $"the book: a spot left at the back is let go, one in the middle kept empty ({book.At(7).Count} spots)");

        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            var (seed, settings) = TownKey.Parse(JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString());
            var model = Generator.Generate(seed, settings);
            var layout = Layout.Build(model);
            var zoning = Zoning.Plan(model, layout);
            var life = new TownLife(model, layout, zoning);
            var ways = life.walkways;
            var walks = layout.sidewalks;
            var carriageways = layout.roads.Where(r => r.kind != RoadKind.Path && r.points.Count >= 2).ToList();
            bool InRoad(Vec2 p) => carriageways.Any(r => Geo.DistToPolyline(p, r.points) < r.width / 2);
            bool OnSidewalk(Vec2 p, int path)
            {
                var pts = walks.paths[path].points;
                for (int k = 0; k + 1 < pts.Count; k++)
                    if (Geo.DistToSegment(p, pts[k], pts[k + 1]) <= Layout.SIDEWALK / 2) return true;
                return false;
            }

            // Every kerb a crossing at a junction sets off from, and twenty there.
            var ends = walks.crossings.Select(k => (int)(k / 1000003)).Distinct().OrderBy(e => e).ToList();
            int corners = 0, close = 0, inRoad = 0, offWalk = 0, tooShort = 0, backwards = 0;
            foreach (int end in ends)
            {
                var path = walks.paths[end >> 1];
                // Rows of five a pair of steps: as many as the sidewalk has room for.
                int rows = path.Length < 0.04 ? 0 : (int)Math.Floor((path.Length - 0.04) / Walkways.CROWD_STEP) + 1;
                int room = rows / 2 * 5 + rows % 2 * 3;
                if (room < N)
                {
                    tooShort++;
                    continue;
                }
                corners++;
                var spots = Enumerable.Range(0, N).Select(k => ways.CrowdSpot(end, k)).ToList();
                for (int a = 0; a < N; a++)
                {
                    if (InRoad(spots[a])) inRoad++;
                    if (!OnSidewalk(spots[a], end >> 1)) offWalk++;
                    for (int b = a + 1; b < N; b++)
                        if (Geo.Dist(spots[a], spots[b]) < APART) close++;
                }
                // The front of the crowd is at the corner: the first five nearer it than the last five.
                var corner = (end & 1) == 0 ? path.points[0] : path.points[path.points.Count - 1];
                if (spots.Take(5).Average(q => Geo.Dist(q, corner)) >= spots.Skip(N - 5).Average(q => Geo.Dist(q, corner))) backwards++;
            }
            Expect(corners > 0, $"{name}: {corners} kerbs where a crossing sets off ({tooShort} sidewalks too short for {N})");
            Expect(close == 0, $"{name}: {N} waiting at each kerb stand at least 0.5 m apart ({close} pairs closer)");
            Expect(inRoad == 0, $"{name}: none of them stands in a carriageway ({inRoad} do)");
            Expect(offWalk == 0, $"{name}: every one of them stands on the sidewalk ({offWalk} off it)");
            Expect(backwards == 0, $"{name}: the first to come stand at the front, by the corner ({backwards} kerbs the other way round)");

            // The morning rush, looked at every 5 s for 20 minutes.
            life.Start(TrafficDay.Morning);
            var was = new Dictionary<int, Vec2>();
            int looks = 0, waitingSeen = 0, biggest = 0, overlap = 0, moved = 0, roadSpots = 0, facing = 0, books = 0;
            for (int t = 0; t < 240; t++)
            {
                life.RunUntil(life.Now + 5000);
                looks++;
                var now = new Dictionary<int, Vec2>();
                var byEnd = new Dictionary<int, List<Vec2>>();
                foreach (var p in life.people)
                {
                    if (p.walk == null || p.piece >= p.walk.Count || p.w0 >= 0 || p.crowdSpot < 0) continue;
                    var piece = p.walk[p.piece];
                    waitingSeen++;
                    var crowd = life.CrowdAt(piece.from);
                    if (crowd == null || p.crowdSpot >= crowd.Count || crowd[p.crowdSpot] != p.id) books++;
                    var (at, along) = life.PersonPoint(p);
                    var cross = Walkways.PointOn(piece, 0).along;
                    if (Math.Abs(along.x - cross.x) > 1e-9 || Math.Abs(along.y - cross.y) > 1e-9) facing++;
                    var spot = life.walkways.CrowdSpot(piece.from, p.crowdSpot);
                    // Still walking up to it from the corner: not there yet.
                    if (Geo.Dist(at, spot) > 1e-9) continue;
                    if (InRoad(at)) roadSpots++;
                    now[p.id] = at;
                    if (was.TryGetValue(p.id, out var before) && Geo.Dist(before, at) > 1e-9) moved++;
                    if (!byEnd.TryGetValue(piece.from, out var list)) byEnd[piece.from] = list = new List<Vec2>();
                    list.Add(at);
                }
                foreach (var list in byEnd.Values)
                {
                    biggest = Math.Max(biggest, list.Count);
                    for (int a = 0; a < list.Count; a++)
                        for (int b = a + 1; b < list.Count; b++)
                            if (Geo.Dist(list[a], list[b]) < APART) overlap++;
                }
                was = now;
            }
            Expect(waitingSeen > 0, $"{name}: people wait at the kerbs in the morning rush ({waitingSeen} seen over {looks} looks, the biggest crowd {biggest})");
            Expect(books == 0, $"{name}: everyone waiting is in the crowd's book at their spot ({books} not)");
            Expect(overlap == 0, $"{name}: nobody waiting stands within 0.5 m of another at their corner ({overlap} pairs)");
            Expect(moved == 0, $"{name}: someone waiting stays in their spot from one look to the next ({moved} moved)");
            Expect(roadSpots == 0, $"{name}: nobody waits in a carriageway ({roadSpots} do)");
            Expect(facing == 0, $"{name}: everyone waiting faces the way over ({facing} don't)");
        }
        Console.WriteLine(fails.Count == 0 ? "crowd: all ok" : $"crowd: {fails.Count} failure(s)");
        return fails.Count == 0 ? 0 : 1;
    }
}

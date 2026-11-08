// Marathon Runner's course on real towns (Porchlight and a few seeds): from
// near one corner to near the opposite one, no junction passed twice, longer
// than the shortest way, run to the finish by going along it, and a short cut
// to a later stretch gains nothing.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class CourseCheck
{
    public static int Run(string porchlightKey, string pictures = null)
    {
        int failed = 0;
        var towns = new List<(string name, string seed, GenParams settings)>();
        var (seed0, settings0) = Program.ParseKey(porchlightKey);
        towns.Add(("Porchlight", seed0, settings0));
        foreach (string seed in new[] { "a1b2c3d4e5f6", "0f9e8d7c6b5a", "123456789abc" })
            towns.Add((seed, seed, GenParams.Normalize(new Dictionary<string, double>())));
        foreach (var (name, seed, settings) in towns)
        {
            var m = Generator.Generate(seed, settings);
            var layout = Layout.Build(m);
            var sw = Stopwatch.StartNew();
            var course = Course.Longest(layout.roads, m.width, m.height);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (course == null)
            {
                Console.WriteLine($"FAIL {name}: no course");
                failed++;
                continue;
            }
            var junctions = new List<int>();
            int node = -1;
            for (int i = 0; i < course.roads.Count; i++)
            {
                var r = layout.roads[course.roads[i]];
                if (i == 0) node = Geo.Dist(r.points[0], course.Start) < 1e-6 ? r.from : r.to;
                junctions.Add(node);
                node = r.from == node ? r.to : r.from;
            }
            junctions.Add(node);
            int twice = junctions.Count - junctions.Distinct().Count();
            double diag = Numbers.Hypot(m.width, m.height);
            double span = Geo.Dist(course.Start, course.Finish);
            // Run it: a step along at a time, and you're over the line.
            var run = new CourseRun(course);
            for (double s = 0; s <= course.Length + 1; s += 0.3) run.Step(course.At(s).point, true);
            // Cut across to three-quarters of the way: nothing gained.
            var cheat = new CourseRun(course);
            cheat.Step(course.At(0.5).point, true);
            cheat.Step(course.At(course.Length * 0.75).point, true);
            // In a car (doesn't count): nothing gained either.
            var drove = new CourseRun(course);
            for (double s = 0; s <= 20; s += 0.3) drove.Step(course.At(s).point, false);
            // The ribbon: walk straight out to either side from all along it, and you're stopped at it.
            var fence = course.Fence(0.04);
            int escaped = 0, tries = 0;
            for (double s = 1; s < course.Length - 1; s += 7)
                foreach (int side in new[] { 1, -1 })
                {
                    var (mid, along, half) = course.At(s);
                    var outward = new Vec2(-along.y * side, along.x * side);
                    var p = mid;
                    for (int k = 0; k < 200; k++)
                    {
                        var q = new Vec2(p.x + outward.x * 0.05 + along.x * 0.02, p.y + outward.y * 0.05 + along.y * 0.02);
                        if (fence.Blocks(p.x, p.y, q.x, q.y, Player.PLAYER_RADIUS)) break;
                        p = q;
                    }
                    tries++;
                    if (course.Nearest(p, 0, course.Length).d > half + 0.3) escaped++;
                }
            // The ribbon keeps to the edge: nowhere does it cut in towards the middle of the road (as it did where a wide road met a narrow one).
            int cutIn = 0;
            foreach (int side in new[] { 1, -1 })
                foreach (var (p, _) in course.Side(side, 0.04))
                {
                    var n = course.Nearest(p, 0, course.Length);
                    if (n.d < 0.4) cutIn++;
                }
            // The road's closed to cars for the runner: all along the middle is covered, and well off it isn't.
            int open = 0, shut = 0;
            for (double s = 0; s <= course.Length; s += 0.5)
            {
                var (mid, along, half) = course.At(s);
                if (!course.Covers(mid, 0.35)) open++;
                var off = new Vec2(mid.x - along.y * (half + 3), mid.y + along.x * (half + 3));
                if (course.Covers(off, 0.35) && PastEveryEdge(course, off) > 0.4) shut++;
            }
            var problems = new List<string>();
            if (cutIn > 0) problems.Add($"the ribbon cuts in towards the middle {cutIn} times");
            if (open > 0) problems.Add($"{open} points of the course not closed to cars");
            if (shut > 0) problems.Add($"{shut} points off the course closed to cars");
            if (twice > 0) problems.Add($"{twice} junctions twice");
            if (span < diag * 0.6) problems.Add($"only {span:0} tiles corner to corner");
            if (!run.Finished) problems.Add($"run along it, got {run.progress:0} of {course.Length:0}");
            if (cheat.progress > 5) problems.Add($"a short cut got {cheat.progress:0}");
            if (drove.progress > 0) problems.Add("driving counted");
            if (ms > 2000) problems.Add($"took {ms:0} ms");
            if (escaped > 0) problems.Add($"got past the ribbon {escaped} times in {tries}");
            bool ok = problems.Count == 0;
            if (!ok) failed++;
            // The straight runs between turns: the longest, and on average.
            var runs = new List<double>();
            double runLength = 0;
            for (int i = 0; i < course.roads.Count; i++)
            {
                runLength += Geo.PolylineLength(layout.roads[course.roads[i]].points);
                bool last = i == course.roads.Count - 1;
                if (last || Turned(layout.roads, course, i))
                {
                    runs.Add(runLength);
                    runLength = 0;
                }
            }
            if (pictures != null)
            {
                System.IO.Directory.CreateDirectory(pictures);
                CoursePicture.Write(System.IO.Path.Combine(pictures, $"{name}.png"), layout.roads, course, 0, 0, m.width, m.height, 4);
                var a = course.points[course.points.Count / 3];
                CoursePicture.Write(System.IO.Path.Combine(pictures, $"{name}-close.png"), layout.roads, course, a.x - 20, a.y - 20, a.x + 20, a.y + 20, 24);
                CoursePicture.Write(System.IO.Path.Combine(pictures, $"{name}-start.png"), layout.roads, course, course.Start.x - 15, course.Start.y - 15, course.Start.x + 15, course.Start.y + 15, 24);
                CoursePicture.Write(System.IO.Path.Combine(pictures, $"{name}-finish.png"), layout.roads, course, course.Finish.x - 15, course.Finish.y - 15, course.Finish.x + 15, course.Finish.y + 15, 24);
                // Each corner it turns, close up.
                int shot = 0;
                for (int i = 0; i + 1 < course.roads.Count && shot < 4; i++)
                {
                    if (!Turned(layout.roads, course, i)) continue;
                    var r = layout.roads[course.roads[i]].points;
                    var nx = layout.roads[course.roads[i + 1]].points;
                    var c = Geo.Dist(r[0], nx[0]) < 1e-6 || Geo.Dist(r[0], nx[nx.Count - 1]) < 1e-6 ? r[0] : r[r.Count - 1];
                    CoursePicture.Write(System.IO.Path.Combine(pictures, $"{name}-turn{shot++}.png"), layout.roads, course, c.x - 6, c.y - 6, c.x + 6, c.y + 6, 50);
                }
            }
            Console.WriteLine($"   {course.turns} turns, {100 * course.mainLength / course.Length:0}% on main roads, straight runs: longest {runs.Max():0} tiles, average {runs.Average():0}");
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {name}: {course.Length * Directions.METRES_PER_TILE / 1000:0.0} km over {course.roads.Count} streets, ends {span:0} tiles apart (diagonal {diag:0}), {course.points.Count} points, {fence.Count} fence segments held {tries - escaped} of {tries}, {ms:0} ms{(ok ? "" : ": " + string.Join(", ", problems))}");
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>How far `p` is past the asphalt's edge of the nearest stretch of the course, every stretch looked at (negative: on it).</summary>
    static double PastEveryEdge(Course course, Vec2 p)
    {
        double best = double.PositiveInfinity;
        for (int k = 1; k < course.points.Count; k++)
        {
            var a = course.points[k - 1];
            var b = course.points[k];
            double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
            double t = l2 > 1e-12 ? Math.Max(0, Math.Min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2)) : 0;
            double d = Numbers.Hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t));
            best = Math.Min(best, d - (course.half[k - 1] + (course.half[k] - course.half[k - 1]) * t));
        }
        return best;
    }

    /// <summary>Does the course turn a corner where its i-th street meets the next?</summary>
    static bool Turned(IReadOnlyList<RoadStroke> roads, Course course, int i)
    {
        var a = roads[course.roads[i]].points;
        var b = roads[course.roads[i + 1]].points;
        // Which ends meet.
        Vec2 shared = Geo.Dist(a[0], b[0]) < 1e-6 || Geo.Dist(a[0], b[b.Count - 1]) < 1e-6 ? a[0] : a[a.Count - 1];
        Vec2 Out(List<Vec2> pts)
        {
            bool atStart = Geo.Dist(pts[0], shared) < 1e-6;
            var from = atStart ? pts[0] : pts[pts.Count - 1];
            var to = atStart ? pts[Math.Min(pts.Count - 1, 2)] : pts[Math.Max(0, pts.Count - 3)];
            double l = Math.Max(1e-9, Geo.Dist(from, to));
            return new Vec2((to.x - from.x) / l, (to.y - from.y) / l);
        }
        var u = Out(a);
        var v = Out(b);
        return -(u.x * v.x + u.y * v.y) < Math.Cos(0.6);
    }
}

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
    public static int Run(string porchlightKey)
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
            var problems = new List<string>();
            if (twice > 0) problems.Add($"{twice} junctions twice");
            if (span < diag * 0.6) problems.Add($"only {span:0} tiles corner to corner");
            if (!run.Finished) problems.Add($"run along it, got {run.progress:0} of {course.Length:0}");
            if (cheat.progress > 5) problems.Add($"a short cut got {cheat.progress:0}");
            if (drove.progress > 0) problems.Add("driving counted");
            if (ms > 2000) problems.Add($"took {ms:0} ms");
            bool ok = problems.Count == 0;
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {name}: {course.Length * Directions.METRES_PER_TILE / 1000:0.0} km over {course.roads.Count} streets, ends {span:0} tiles apart (diagonal {diag:0}), {course.points.Count} points, {ms:0} ms{(ok ? "" : ": " + string.Join(", ", problems))}");
        }
        return failed == 0 ? 0 : 1;
    }
}

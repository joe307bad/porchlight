// The web build has no threads: a town ahead is grown and built a share at a
// time between frames (CountyDrive), and a share that takes too long is a frame
// that stalls, which at speed between towns is the car jumping. This drives
// every space of the county the way the web build does and checks:
//
//   - no share of the work is long (every town grown, its layout, its signposts;
//     every road space): longer than `limit` ms here fails;
//   - a share at a time makes exactly what all at once does (Fingerprint).
//
//   dotnet run --project "unity/Tools~/Checks" -- hitches [limit-ms] [show-ms]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Porchlight.County;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class Hitches
{
    /// <summary>
    /// The longest a share may take here (ms). Generous for a busy CI machine: before the work was split,
    /// Porchlight's streets were one 870 ms share, its buildings 590 and its layout 400.
    /// </summary>
    const double LIMIT = 100;
    /// <summary>Each space built this many times, each share's quickest kept.</summary>
    const int RUNS = 3;

    public static int Run(double limit = LIMIT, double show = 20)
    {
        var map = CountyMap.Build(CountyCheck.PublicTowns(), "acct");
        int failed = 0;
        double worstAll = 0;
        var sw = new Stopwatch();
        foreach (var s in map.spaces)
        {
            // Built a few times over, each share timed: its quickest. A pause for the garbage collector (or anything
            // else the machine's doing) lands on one share one time, not the same share every time; a long share is long each time.
            List<(string what, double ms)> times = null;
            TownModel m = null;
            IsoLayout layout = null;
            List<Signpost> signs = null;
            for (int run = 0; run < RUNS; run++)
            {
                var these = new List<(string, double)>();
                void Each(IEnumerable<string> steps, string what)
                {
                    using var e = steps.GetEnumerator();
                    while (true)
                    {
                        sw.Restart();
                        bool more = e.MoveNext();
                        these.Add(($"{what}{(more ? e.Current : "(end)")}", sw.Elapsed.TotalMilliseconds));
                        if (!more) break;
                    }
                }
                // Grown (or, a road space, built), as the web build does.
                if (s.kind == SpaceKind.Town)
                {
                    var (seed, p) = Program.ParseKey(s.townKey);
                    Each(Generator.GenerateSteps(seed, p, x => m = x), "");
                }
                else Each(RoadSpaces.BuildSteps(map, s, x => m = x), "road ");
                Each(Layout.BuildSteps(m, l => layout = l), "layout");
                sw.Restart();
                var names = StreetNaming.Name(m, layout.roads);
                these.Add(("names", sw.Elapsed.TotalMilliseconds));
                Each(Signs.PlanSteps(m, layout, names, x => signs = x), "signs");
                if (times == null) times = these;
                else for (int i = 0; i < times.Count && i < these.Count; i++) times[i] = (times[i].what, Math.Min(times[i].ms, these[i].Item2));
            }
            double worst = times.Max(t => t.ms), total = times.Sum(t => t.ms);
            var slow = times.Where(t => t.ms >= show).Select(t => $"{t.what} {t.ms:0}").ToList();

            // The same as all at once.
            var whole = s.kind == SpaceKind.Town ? Generate(s.townKey) : RoadSpaces.Build(map, s);
            var wholeLayout = Layout.Build(whole);
            var wholeSigns = Signs.Plan(whole, wholeLayout, StreetNaming.Name(whole, wholeLayout.roads));
            var differs = new List<string>();
            if (Fingerprint.Of(m) != Fingerprint.Of(whole)) differs.Add("town");
            if (Fingerprint.Of(layout) != Fingerprint.Of(wholeLayout)) differs.Add("layout");
            if (Fingerprint.Of(signs) != Fingerprint.Of(wholeSigns)) differs.Add("signposts");

            bool ok = worst <= limit && differs.Count == 0;
            if (!ok) failed++;
            worstAll = Math.Max(worstAll, worst);
            string why = differs.Count > 0 ? $" a share at a time makes a different {string.Join(", ", differs)}" : "";
            Console.WriteLine($"{(ok ? "ok" : "FAIL")} {s.kind} {s.name}: {total:0} ms in all, longest share {worst:0} ms{why}{(slow.Count > 0 ? "; over " + show + " ms: " + string.Join(", ", slow) : "")}");
        }
        Console.WriteLine($"{map.spaces.Count - failed}/{map.spaces.Count} spaces ok; the longest share anywhere {worstAll:0} ms (limit {limit:0} ms)");
        return failed == 0 ? 0 : 1;
    }

    static TownModel Generate(string key)
    {
        var (seed, p) = Program.ParseKey(key);
        return Generator.Generate(seed, p);
    }
}

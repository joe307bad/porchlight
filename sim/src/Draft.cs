// The street editor's draft worker (docs/street-editor-api.md): `porchlight-sim --draft`. One JSON
// request a line on stdin, one answer a line on stdout, each with the request's id; logs on stderr.
//
//   plan    a town grown from its key and a skeleton, as Manage's edit screen draws it: its roads and
//           their design, junctions, blocks, buildings, water; what's wrong with it (problems); and,
//           given a base, what changed from it
//   day     a whole day of the town's life (as Checks traffic-day runs it), summed up
//   houses  the tile each house goes by (as the starter towns' files have them: Checks StarterHouses)
//
// Each key's model through the zoo (terrain, rivers, site, airport, zoo) is kept while the skeleton's
// centre stays the same: a re-plan starts at the roads. The bullseye moves the site, so a new centre
// grows it again from the site.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

namespace Porchlight.Sim
{
    public static class Draft
    {
        /// <summary>The last stage a cached model has run.</summary>
        const string CACHED_THROUGH = "zoo";
        /// <summary>The stage the bullseye first matters to: a moved centre runs from here.</summary>
        const string CENTRE_STAGE = "site";
        const int CACHE_SIZE = 6;

        sealed class Cached
        {
            public string key, centre;
            public TownModel model;
            public Dictionary<string, long> ms;
            public long used;
        }

        static readonly List<Cached> cache = new List<Cached>();
        /// <summary>Terrain and rivers alone (before the site), by key: what a moved bullseye starts from.</summary>
        static readonly Dictionary<string, (TownModel model, Dictionary<string, long> ms)> terrain = new Dictionary<string, (TownModel, Dictionary<string, long>)>();
        /// <summary>Each base's summary (key + skeleton), so a re-plan against the same base doesn't grow it again.</summary>
        static readonly Dictionary<string, Summary> bases = new Dictionary<string, Summary>();
        static long tick;

        static readonly JsonSerializerOptions JSON = new JsonSerializerOptions { IncludeFields = true };

        public static int Run(TextReader input, TextWriter output)
        {
            Log("draft worker ready");
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                object answer;
                JsonElement id = default;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var req = doc.RootElement;
                    id = req.TryGetProperty("id", out var i) ? i.Clone() : default;
                    string op = Str(req, "op");
                    answer = op switch
                    {
                        "plan" => Plan(req, id),
                        "day" => Day(req, id),
                        "houses" => Houses(req, id),
                        _ => throw new ArgumentException($"no such op: {op}"),
                    };
                }
                catch (Exception e)
                {
                    Log($"request failed: {e.Message}");
                    answer = new Dictionary<string, object> { ["id"] = IdOf(id), ["ok"] = false, ["error"] = e is InvariantException ie ? $"{ie.Stage}: {string.Join("; ", ie.Violations)}" : e.Message };
                }
                output.WriteLine(JsonSerializer.Serialize(answer, JSON));
                output.Flush();
            }
            return 0;
        }

        static object IdOf(JsonElement id) => id.ValueKind == JsonValueKind.Undefined ? null : (object)id;

        static string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>The skeleton a request carries (null, absent or empty: none).</summary>
        static Skeleton SkeletonOf(JsonElement o, string name = "skeleton")
        {
            if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
            var s = Skeleton.FromJson(v.GetRawText());
            return s.IsEmpty ? null : s;
        }

        static string KeyOf(JsonElement o)
        {
            string key = Str(o, "key");
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("no key");
            return key;
        }

        // ---- growing a town ----

        static string CentreSig(Skeleton s) => s?.center is Vec2 c ? $"{c.x.ToString("R", CultureInfo.InvariantCulture)},{c.y.ToString("R", CultureInfo.InvariantCulture)}" : "";

        /// <summary>
        /// The town from its key and this skeleton, every stage checked: from the cached terrain unless `full`.
        /// Stage violations are kept (not thrown) so Manage can show them; `ms` is each stage's time.
        /// </summary>
        public static TownModel Grow(string key, Skeleton skeleton, bool full, out Dictionary<string, long> ms, out List<(string stage, string text)> violations)
        {
            var (seed, p) = TownKey.Parse(key);
            p = p.Clone();
            p.skeleton = skeleton;
            p = GenParams.Normalize(p);
            p.skeleton = skeleton;
            ms = new Dictionary<string, long>();
            violations = new List<(string, string)>();
            string baseKey = Skeletons.WithRev(key, 0);
            string centre = CentreSig(skeleton);
            TownModel model = null;
            int from = 0;
            if (!full)
            {
                var hit = cache.FirstOrDefault(c => c.key == baseKey && c.centre == centre);
                if (hit != null)
                {
                    hit.used = ++tick;
                    model = hit.model.With();
                    foreach (var kv in hit.ms) ms[kv.Key] = 0;
                    from = Array.FindIndex(Generator.Stages, s => s.Name == CACHED_THROUGH) + 1;
                }
                else if (terrain.TryGetValue(baseKey, out var t))
                {
                    model = t.model.With();
                    foreach (var kv in t.ms) ms[kv.Key] = 0;
                    from = Array.FindIndex(Generator.Stages, s => s.Name == CENTRE_STAGE);
                }
            }
            if (model == null) model = TownModel.CreateEmpty(seed, p);
            else model.@params = p;
            var sw = new Stopwatch();
            for (int i = from; i < Generator.Stages.Length; i++)
            {
                var stage = Generator.Stages[i];
                sw.Restart();
                model = stage.Run(model, Rng.Stage(seed, stage.Name));
                ms[stage.Name] = sw.ElapsedMilliseconds;
                foreach (var v in stage.Check(model)) violations.Add((stage.Name, v));
                if (i + 1 < Generator.Stages.Length && Generator.Stages[i + 1].Name == CENTRE_STAGE && !terrain.ContainsKey(baseKey))
                    terrain[baseKey] = (model.With(), Copy(ms));
                if (stage.Name == CACHED_THROUGH && (full || cache.All(c => !(c.key == baseKey && c.centre == centre))))
                    Keep(baseKey, centre, model.With(), Copy(ms));
            }
            return model;
        }

        static Dictionary<string, long> Copy(Dictionary<string, long> d) => new Dictionary<string, long>(d);

        static void Keep(string key, string centre, TownModel model, Dictionary<string, long> ms)
        {
            cache.RemoveAll(c => c.key == key && c.centre == centre);
            cache.Add(new Cached { key = key, centre = centre, model = model, ms = ms, used = ++tick });
            while (cache.Count > CACHE_SIZE) cache.Remove(cache.OrderBy(c => c.used).First());
        }

        // ---- plan ----

        sealed class Summary
        {
            public HashSet<(int, int)> houses;
            public int shops, roundabouts, signals;
            public List<(Vec2 a, Vec2 b)> roads;
            public List<Problem> geometry;
        }

        sealed class Problem
        {
            public string level, text;
            public Vec2? at;
        }

        static object Plan(JsonElement req, JsonElement id)
        {
            var sw = Stopwatch.StartNew();
            string key = KeyOf(req);
            var skeleton = SkeletonOf(req);
            bool full = req.TryGetProperty("full", out var f) && f.ValueKind == JsonValueKind.True;
            var model = Grow(key, skeleton, full, out var ms, out var violations);
            var layout = Layout.Build(model);
            var summary = Summarise(model, layout);
            var problems = new List<Problem>();
            foreach (var (stage, text) in violations) problems.Add(new Problem { level = "error", text = $"{stage}: {text}", at = null });

            Summary before = null;
            if (req.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                string baseKey = Str(b, "key") ?? key;
                var baseSkeleton = SkeletonOf(b);
                string sig = baseKey + "\n" + (baseSkeleton?.ToJson() ?? "");
                if (!bases.TryGetValue(sig, out before))
                {
                    var bm = Grow(baseKey, baseSkeleton, false, out _, out _);
                    before = Summarise(bm, Layout.Build(bm));
                    if (bases.Count > 16) bases.Clear();
                    bases[sig] = before;
                }
            }
            // A geometry problem the town had before this edit isn't the edit's: a warning, so the town can still be changed.
            foreach (var g in summary.geometry)
            {
                bool old = before != null && g.at is Vec2 at && before.geometry.Any(o => o.text == g.text && o.at is Vec2 oa && Dist(oa, at) < 1.5);
                problems.Add(old ? new Problem { level = "warning", text = g.text + " (as it was before)", at = g.at } : g);
            }
            if (model.design != null)
            {
                foreach (var n in model.design.notes) problems.Add(new Problem { level = "warning", text = n.text, at = n.at });
                foreach (var e in model.design.edges)
                    if (e != null && e.vc > 1 && e.edge >= 0 && e.edge < model.roads.edges.Count)
                        problems.Add(new Problem { level = "warning", text = $"a road over capacity ({e.vc:0.00})", at = Mid(model.roads.edges[e.edge].points) });
            }
            if (skeleton != null) problems.AddRange(Dropped(model, skeleton));

            object changes = null;
            if (before != null)
            {
                var lost = before.houses.Where(h => !summary.houses.Contains(h)).OrderBy(h => h.Item2).ThenBy(h => h.Item1).ToList();
                int added = summary.houses.Count(h => !before.houses.Contains(h));
                if (lost.Count > 0) problems.Add(new Problem { level = "warning", text = $"{lost.Count} house(s) lost: those living there move to the nearest free house", at = new Vec2(lost[0].Item1 + 0.5, lost[0].Item2 + 0.5) });
                changes = new Dictionary<string, object>
                {
                    ["housesLost"] = lost.Count,
                    ["housesNew"] = added,
                    ["lostHouses"] = lost.Select(h => new[] { h.Item1, h.Item2 }).ToList(),
                    ["shops"] = new[] { before.shops, summary.shops },
                    ["roadsAdded"] = summary.roads.Count(r => !before.roads.Any(o => Skeleton.SameEnds(r.a, r.b, o.a, o.b))),
                    ["roadsRemoved"] = before.roads.Count(r => !summary.roads.Any(o => Skeleton.SameEnds(r.a, r.b, o.a, o.b))),
                    ["roundabouts"] = new[] { before.roundabouts, summary.roundabouts },
                    ["signals"] = new[] { before.signals, summary.signals },
                };
            }
            var town = Town(model, layout, skeleton, summary, ms);
            var answer = new Dictionary<string, object>
            {
                ["id"] = IdOf(id),
                ["ok"] = true,
                ["ms"] = sw.ElapsedMilliseconds,
                ["town"] = town,
                ["problems"] = problems.Select(p => new Dictionary<string, object> { ["level"] = p.level, ["text"] = p.text, ["at"] = p.at is Vec2 a ? Pt(a) : null }).ToList(),
                ["changes"] = changes,
            };
            Log($"plan {key}{(skeleton != null ? " (edited)" : "")}{(full ? " full" : "")}: {sw.ElapsedMilliseconds} ms, {problems.Count(p => p.level == "error")} error(s), {problems.Count(p => p.level == "warning")} warning(s)");
            return answer;
        }

        /// <summary>The houses people can live in (Homes.All, with this layout) by the tile each goes by.</summary>
        static List<Building> HousesOf(TownModel model, IsoLayout layout)
        {
            var built = new HashSet<int>(layout.houses.Select(h => h.buildingId));
            return model.buildings.Where(b => b.kind == BuildingKind.House && b.tiles.Count > 0 && built.Contains(b.id)).ToList();
        }

        static Summary Summarise(TownModel model, IsoLayout layout)
        {
            var s = new Summary
            {
                houses = new HashSet<(int, int)>(HousesOf(model, layout).Select(h => { var a = Homes.Anchor(h); return (a.x, a.y); })),
                shops = model.buildings.Count(b => b.kind == BuildingKind.Shop),
                roads = model.roads.edges.Select(e => (model.roads.nodes[e.from].at, model.roads.nodes[e.to].at)).ToList(),
                geometry = Geometry(model),
            };
            if (model.design != null)
            {
                s.roundabouts = model.design.nodes.Count(n => n != null && n.form == JunctionForm.Roundabout);
                s.signals = model.design.nodes.Count(n => n != null && n.control == DesignControl.Signal);
            }
            return s;
        }

        // ---- problems of the town's shape ----

        const double JUNCTION_GAP_MAIN = 4, JUNCTION_GAP_STREET = 3, LEAST_ANGLE = 60;

        static List<Problem> Geometry(TownModel model)
        {
            var o = new List<Problem>();
            var g = model.roads;
            var legs = new int[g.nodes.Count];
            foreach (var e in g.edges)
            {
                legs[e.from]++;
                legs[e.to]++;
            }
            // Junctions too close along a road.
            foreach (var e in g.edges)
            {
                if (e.kind == RoadKind.Path || legs[e.from] < 3 || legs[e.to] < 3) continue;
                double gap = e.kind == RoadKind.Main ? JUNCTION_GAP_MAIN : JUNCTION_GAP_STREET;
                double len = Length(e.points);
                if (len < gap) o.Add(new Problem { level = "error", text = $"junctions {len:0.0} tiles apart along a {(e.kind == RoadKind.Main ? "main road" : "street")} (at least {gap})", at = Mid(e.points) });
            }
            // Roads meeting at too sharp an angle.
            var leaving = new List<(double heading, RoadEdge e)>[g.nodes.Count];
            foreach (var e in g.edges)
            {
                if (e.kind == RoadKind.Path || e.points == null || e.points.Count < 2) continue;
                (leaving[e.from] ??= new List<(double, RoadEdge)>()).Add((Heading(e.points, true), e));
                (leaving[e.to] ??= new List<(double, RoadEdge)>()).Add((Heading(e.points, false), e));
            }
            for (int n = 0; n < g.nodes.Count; n++)
            {
                var l = leaving[n];
                if (l == null || l.Count < 2) continue;
                l.Sort((a, b) => a.heading.CompareTo(b.heading));
                for (int i = 0; i < l.Count; i++)
                {
                    double a = l[i].heading, b = i + 1 < l.Count ? l[i + 1].heading : l[0].heading + 2 * Math.PI;
                    double deg = (b - a) * 180 / Math.PI;
                    if (deg < LEAST_ANGLE)
                    {
                        o.Add(new Problem { level = "error", text = $"two roads meet at {deg:0}° (at least {LEAST_ANGLE}°)", at = g.nodes[n].at });
                        break;
                    }
                }
            }
            // Two roads between the same two junctions.
            var pairs = new HashSet<(int, int)>();
            foreach (var e in g.edges)
            {
                if (e.kind == RoadKind.Path) continue;
                var pair = (Math.Min(e.from, e.to), Math.Max(e.from, e.to));
                if (!pairs.Add(pair)) o.Add(new Problem { level = "error", text = "two roads between the same junctions", at = Mid(e.points) });
            }
            // A road in water with no bridge.
            var water = Raster.WaterMask(model);
            var bridged = new HashSet<int>(model.bridges.Select(b => b.roadEdgeId));
            foreach (var e in g.edges)
            {
                if (e.kind == RoadKind.Path || bridged.Contains(e.id) || e.points == null) continue;
                var wet = Wet(e.points, water, model.width, model.height);
                if (wet is Vec2 at) o.Add(new Problem { level = "error", text = "a road in water with no bridge", at = at });
            }
            // A gate, the airport or the zoo cut off from the centre.
            if (g.nodes.Count > 0)
            {
                var part = Components(g);
                int heart = Nearest(g, model.center);
                foreach (var gate in model.gates)
                {
                    int n = Nearest(g, gate);
                    if (n >= 0 && Dist(g.nodes[n].at, gate) < 3 && part[n] != part[heart])
                        o.Add(new Problem { level = "error", text = "a gate cut off from the centre", at = gate });
                }
                void Site(string what, int x0, int y0, int x1, int y1)
                {
                    var middle = new Vec2((x0 + x1) / 2.0, (y0 + y1) / 2.0);
                    int best = -1;
                    double bd = double.MaxValue;
                    for (int i = 0; i < g.nodes.Count; i++)
                    {
                        if (legs[i] == 0) continue;
                        var p = g.nodes[i].at;
                        double dx = Math.Max(Math.Max(x0 - p.x, 0), p.x - x1), dy = Math.Max(Math.Max(y0 - p.y, 0), p.y - y1);
                        double d = dx * dx + dy * dy;
                        if (d < bd)
                        {
                            bd = d;
                            best = i;
                        }
                    }
                    if (best < 0 || part[best] != part[heart]) o.Add(new Problem { level = "error", text = $"the {what} cut off from the centre", at = middle });
                }
                if (model.airport != null) Site("airport", model.airport.x0, model.airport.y0, model.airport.x1, model.airport.y1);
                if (model.zoo != null) Site("zoo", model.zoo.x0, model.zoo.y0, model.zoo.x1, model.zoo.y1);
            }
            return o;
        }

        /// <summary>Pinned points the town couldn't keep: a point of a pinned road with no road of the town through it (within 1.5 tiles), each place once.</summary>
        static IEnumerable<Problem> Dropped(TownModel model, Skeleton s)
        {
            var edges = model.roads.edges.Where(e => e.points != null && e.points.Count >= 2).ToList();
            bool Kept(Vec2 p) => edges.Any(e => Geo.DistToPolyline(p, e.points) <= 1.5);
            var said = new HashSet<(long, long)>();
            foreach (var r in (s.mains ?? new List<SkeletonRoad>()).Concat(s.streets))
                foreach (var p in r.points)
                    if (!Kept(p) && said.Add(((long)Math.Round(p.x), (long)Math.Round(p.y))))
                        yield return new Problem { level = "warning", text = "a pinned point dropped", at = p };
        }

        static double Dist(Vec2 a, Vec2 b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

        static double Length(List<Vec2> pts)
        {
            double l = 0;
            for (int i = 1; i < pts.Count; i++) l += Dist(pts[i - 1], pts[i]);
            return l;
        }

        static Vec2 Mid(List<Vec2> pts)
        {
            if (pts == null || pts.Count == 0) return new Vec2(0, 0);
            double half = Length(pts) / 2, l = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double d = Dist(pts[i - 1], pts[i]);
                if (l + d >= half && d > 0)
                {
                    double t = (half - l) / d;
                    return new Vec2(pts[i - 1].x + (pts[i].x - pts[i - 1].x) * t, pts[i - 1].y + (pts[i].y - pts[i - 1].y) * t);
                }
                l += d;
            }
            return pts[0];
        }

        /// <summary>Which way the road leaves its `from` end (or its `to` end), measured over its first tile or two.</summary>
        static double Heading(List<Vec2> pts, bool atFrom)
        {
            var a = atFrom ? pts[0] : pts[^1];
            Vec2 b = atFrom ? pts[1] : pts[^2];
            double run = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var q = atFrom ? pts[i] : pts[pts.Count - 1 - i];
                b = q;
                run = Dist(a, q);
                if (run >= 1.5) break;
            }
            return Math.Atan2(b.y - a.y, b.x - a.x);
        }

        /// <summary>A point of the road inside the town's water (sampled every half tile, ends left out: a road meets the shore), or null.</summary>
        static Vec2? Wet(List<Vec2> pts, byte[] water, int w, int h)
        {
            double total = Length(pts), l = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                double d = Dist(a, b);
                for (double s = 0; s < d; s += 0.5)
                {
                    double along = l + s;
                    if (along < 1 || along > total - 1) continue;
                    double t = d == 0 ? 0 : s / d;
                    double x = a.x + (b.x - a.x) * t, y = a.y + (b.y - a.y) * t;
                    int tx = (int)Math.Floor(x), ty = (int)Math.Floor(y);
                    if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                    if (water[ty * w + tx] != 0) return new Vec2(x, y);
                }
                l += d;
            }
            return null;
        }

        static int[] Components(RoadGraph g)
        {
            var part = Enumerable.Repeat(-1, g.nodes.Count).ToArray();
            var adj = new List<int>[g.nodes.Count];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();
            foreach (var e in g.edges)
            {
                adj[e.from].Add(e.to);
                adj[e.to].Add(e.from);
            }
            int next = 0;
            var stack = new Stack<int>();
            for (int i = 0; i < part.Length; i++)
            {
                if (part[i] >= 0) continue;
                part[i] = next;
                stack.Push(i);
                while (stack.Count > 0)
                    foreach (int j in adj[stack.Pop()])
                        if (part[j] < 0)
                        {
                            part[j] = next;
                            stack.Push(j);
                        }
                next++;
            }
            return part;
        }

        static int Nearest(RoadGraph g, Vec2 p)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < g.nodes.Count; i++)
            {
                double d = Dist(g.nodes[i].at, p);
                if (d < bd)
                {
                    bd = d;
                    best = i;
                }
            }
            return best;
        }

        // ---- the town, as Manage draws it ----

        static double R(double v) => Math.Round(v * 100) / 100;
        static double[] Pt(Vec2 p) => new[] { R(p.x), R(p.y) };
        static List<double[]> Pts(IEnumerable<Vec2> ps) => ps?.Select(Pt).ToList() ?? new List<double[]>();

        static string Control(DesignControl c) => c switch
        {
            DesignControl.Free => "free",
            DesignControl.Stop => "stop",
            DesignControl.AllWayStop => "allway",
            DesignControl.Signal => "signal",
            DesignControl.Yield => "yield",
            _ => "auto",
        };

        static string Era(Era e) => e == Model.Era.Core ? "core" : e == Model.Era.GardenSuburb ? "garden" : "postwar";

        static string Kind(BuildingKind k) => k switch
        {
            BuildingKind.House => "house",
            BuildingKind.Shop => "shop",
            BuildingKind.Workshop => "workshop",
            BuildingKind.Church => "church",
            BuildingKind.Hall => "hall",
            BuildingKind.School => "school",
            BuildingKind.Supermarket => "supermarket",
            BuildingKind.PetrolStation => "petrol",
            BuildingKind.FireStation => "fire",
            BuildingKind.Clinic => "clinic",
            _ => k.ToString().ToLowerInvariant(),
        };

        /// <summary>The water, one bit a tile, row by row, lowest bit first, base64.</summary>
        static string WaterBits(TownModel model)
        {
            var mask = Raster.WaterMask(model);
            var bits = new byte[(mask.Length + 7) / 8];
            for (int i = 0; i < mask.Length; i++)
                if (mask[i] != 0) bits[i >> 3] |= (byte)(1 << (i & 7));
            return Convert.ToBase64String(bits);
        }

        static Dictionary<string, object> Town(TownModel model, IsoLayout layout, Skeleton skeleton, Summary summary, Dictionary<string, long> ms)
        {
            var g = model.roads;
            var design = model.design;
            var legs = new int[g.nodes.Count];
            foreach (var e in g.edges)
            {
                legs[e.from]++;
                legs[e.to]++;
            }
            var gateNodes = new HashSet<int>();
            foreach (var gate in model.gates)
            {
                int n = Nearest(g, gate);
                if (n >= 0 && Dist(g.nodes[n].at, gate) < 2) gateNodes.Add(n);
            }
            StreetNames names = null;
            try
            {
                names = StreetNaming.Name(model, layout.roads);
            }
            catch (Exception e)
            {
                Log($"street names: {e.Message}");
            }
            string NameOf(int road) => names != null && road < names.ofRoad.Count && names.ofRoad[road] >= 0 ? names.streets[names.ofRoad[road]].name ?? "" : "";

            var roads = new List<object>();
            int pockets = 0;
            for (int i = 0; i < g.edges.Count; i++)
            {
                var e = g.edges[i];
                var stroke = i < layout.roads.Count ? layout.roads[i] : null;
                var d = design?.Edge(e.id);
                object dj = null;
                if (d != null)
                {
                    pockets += d.pockets.Count;
                    dj = new Dictionary<string, object>
                    {
                        ["lanes"] = new[] { d.lanesFwd, d.lanesBack },
                        ["width"] = R(d.width),
                        ["divided"] = d.divided,
                        ["pockets"] = d.pockets.Select(p => new Dictionary<string, object> { ["atTo"] = p.atTo, ["left"] = p.left, ["length"] = R(p.length) }).ToList(),
                        ["flow"] = new[] { Math.Round(d.flowFwd), Math.Round(d.flowBack) },
                        ["vc"] = R(d.vc),
                        ["relief"] = d.relief,
                        ["locked"] = d.locked,
                    };
                }
                roads.Add(new Dictionary<string, object>
                {
                    ["id"] = e.id,
                    ["from"] = e.from,
                    ["to"] = e.to,
                    ["kind"] = Names.Of(e.kind),
                    ["cls"] = Names.Of(e.cls),
                    ["pts"] = Pts(e.points), // the graph's own points: sent back as a skeleton, they lay the road where it is
                    ["name"] = NameOf(i),
                    ["width"] = R(stroke?.width ?? Layout.RoadWidth(e.kind, model)),
                    ["design"] = dj,
                });
            }

            var junctions = new List<object>();
            for (int n = 0; n < g.nodes.Count; n++)
            {
                if (legs[n] < 3) continue;
                var nd = design?.Node(n);
                string control = nd != null ? Control(nd.control) : "auto";
                string form = nd != null && nd.form == JunctionForm.Roundabout ? "roundabout" : "plain";
                if (nd == null && skeleton?.JunctionAt(g.nodes[n].at) is JunctionOverride jo)
                {
                    // No design yet: the editor's own choice, as asked.
                    if (jo.control == "roundabout") form = "roundabout";
                    else control = jo.control;
                }
                junctions.Add(new Dictionary<string, object>
                {
                    ["node"] = n,
                    ["at"] = Pt(g.nodes[n].at),
                    ["form"] = form,
                    ["radius"] = R(nd?.radius ?? 0),
                    ["control"] = control,
                    ["protectedLeft"] = nd?.protectedLeft ?? false,
                    ["slips"] = nd?.slips.Select(s => Pts(s.points)).ToList() ?? new List<List<double[]>>(),
                    ["entering"] = Math.Round(nd?.entering ?? 0),
                });
            }

            var parks = model.ParkBlockSet();
            var blocks = model.blocks.Select(b => new Dictionary<string, object>
            {
                ["id"] = b.id,
                ["poly"] = Pts(b.polygon),
                ["transect"] = (int)b.transect,
                ["era"] = Era(b.era),
                ["park"] = parks.Contains(b.id),
            }).ToList();
            var buildings = model.buildings.Where(b => b.footprint != null && b.footprint.Count >= 3).Select(b => new Dictionary<string, object>
            {
                ["id"] = b.id,
                ["kind"] = Kind(b.kind),
                ["poly"] = Pts(b.footprint),
                ["floors"] = b.floors,
            }).ToList();

            return new Dictionary<string, object>
            {
                ["width"] = model.width,
                ["height"] = model.height,
                ["center"] = Pt(model.center),
                ["secondCentre"] = model.secondCentre is Vec2 sc ? Pt(sc) : null,
                ["townRadius"] = R(model.townRadius),
                ["gates"] = Pts(model.gates),
                ["water"] = WaterBits(model),
                ["nodes"] = g.nodes.Select(n => new Dictionary<string, object> { ["id"] = n.id, ["at"] = Pt(n.at), ["legs"] = legs[n.id], ["gate"] = gateNodes.Contains(n.id) }).ToList(),
                ["roads"] = roads,
                ["junctions"] = junctions,
                ["blocks"] = blocks,
                ["buildings"] = buildings,
                ["notes"] = design?.notes.Select(n => new Dictionary<string, object> { ["at"] = Pt(n.at), ["text"] = n.text }).ToList() ?? new List<Dictionary<string, object>>(),
                ["stats"] = new Dictionary<string, object>
                {
                    ["houses"] = summary.houses.Count,
                    ["shops"] = summary.shops,
                    ["buildings"] = model.buildings.Count,
                    ["roads"] = g.edges.Count,
                    ["junctions"] = junctions.Count,
                    ["roundabouts"] = summary.roundabouts,
                    ["pockets"] = pockets,
                    ["signals"] = summary.signals,
                    ["ms"] = ms,
                },
            };
        }

        // ---- houses ----

        static object Houses(JsonElement req, JsonElement id)
        {
            var sw = Stopwatch.StartNew();
            string key = KeyOf(req);
            var model = Grow(key, SkeletonOf(req), false, out _, out _);
            var houses = HousesOf(model, Layout.Build(model)).Select(b => { var a = Homes.Anchor(b); return new[] { a.x, a.y }; }).ToList();
            Log($"houses {key}: {houses.Count} in {sw.ElapsedMilliseconds} ms");
            return new Dictionary<string, object> { ["id"] = IdOf(id), ["ok"] = true, ["houses"] = houses };
        }

        // ---- day ----

        /// <summary>The same day Checks traffic-day runs: 20 November 2026.</summary>
        static readonly long DAY = TownClock.DayOf(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        const int WORST = 10;
        static readonly Dictionary<string, Dictionary<string, object>> days = new Dictionary<string, Dictionary<string, object>>();

        static object Day(JsonElement req, JsonElement id)
        {
            var sw = Stopwatch.StartNew();
            string key = KeyOf(req);
            var skeleton = SkeletonOf(req);
            // A day is a minute's work in a big town, and the same town's day is always the same: kept.
            string sig = key + "\n" + (skeleton?.ToJson() ?? "");
            if (days.TryGetValue(sig, out var known))
                return new Dictionary<string, object> { ["id"] = IdOf(id), ["ok"] = true, ["ms"] = sw.ElapsedMilliseconds, ["day"] = known };
            var model = Grow(key, skeleton, false, out _, out _);
            var layout = Layout.Build(model);
            var zoning = Zoning.Plan(model, layout);
            var life = new TownLife(model, layout, zoning, new LifeOptions());
            long start = TownClock.At(DAY, 0);
            life.Start(start);
            // Each drive's time, to find the slowest tenth: watched minute by minute (a trip begun and ended
            // inside one minute between two others is missed; they're the shortest, so p90 is barely moved).
            var last = new Trip[life.people.Count];
            var drives = new List<long>();
            void Watch()
            {
                for (int i = 0; i < life.people.Count; i++)
                {
                    var t = life.people[i].trip;
                    var was = last[i];
                    if (was != null && was != t && was.ended >= 0 && was.mode == Mode.Drive) drives.Add(was.ended - was.started);
                    last[i] = t;
                }
            }
            for (long t = start; t <= start + 27 * TownClock.HOUR; t += TownClock.MINUTE)
            {
                life.RunUntil(t);
                Watch();
            }
            foreach (var was in last)
                if (was != null && was.ended >= 0 && was.mode == Mode.Drive) drives.Add(was.ended - was.started);
            drives.Sort();
            var s = life.stats;
            double Mean(int m) => s.finishedBy[m] == 0 ? 0 : Math.Round(s.durationBy[m] / (double)s.finishedBy[m] / TownClock.MINUTE, 1);
            double p90 = drives.Count == 0 ? 0 : Math.Round(drives[Math.Min(drives.Count - 1, (int)(0.9 * drives.Count))] / (double)TownClock.MINUTE, 1);
            var worst = s.carWaits
                .OrderByDescending(kv => kv.Value.total)
                .Take(WORST)
                .Select(kv =>
                {
                    life.net.junctions.TryGetValue(kv.Key, out var j);
                    var at = j?.at ?? (kv.Key >= 0 && kv.Key < model.roads.nodes.Count ? model.roads.nodes[kv.Key].at : new Vec2(0, 0));
                    return new Dictionary<string, object>
                    {
                        ["at"] = Pt(at),
                        ["delayS"] = Math.Round(kv.Value.Mean / 1000, 1),
                        ["control"] = j == null ? "free" : j.control switch { Life.Control.Stop => "stop", Life.Control.AllWayStop => "allway", Life.Control.Signal => "signal", _ => "free" },
                    };
                }).ToList();
            var day = new Dictionary<string, object>
            {
                ["trips"] = s.started,
                ["meanCarMin"] = Mean((int)Mode.Drive),
                ["p90CarMin"] = p90,
                ["meanWalkMin"] = Mean((int)Mode.Walk),
                ["failed"] = s.failed,
                ["gaveUp"] = s.gaveUp,
                ["worst"] = worst,
                // TODO(street-editor): each turn pocket's storage and its worst queue, once RoadNet's lanes say which are pockets.
                ["pockets"] = new List<object>(),
            };
            Log($"day {key}: {s.started} trips in {sw.ElapsedMilliseconds} ms");
            if (days.Count >= 16) days.Clear();
            days[sig] = day;
            return new Dictionary<string, object> { ["id"] = IdOf(id), ["ok"] = true, ["ms"] = sw.ElapsedMilliseconds, ["day"] = day };
        }

        static void Log(string line) => Console.Error.WriteLine("draft: " + line);
    }
}

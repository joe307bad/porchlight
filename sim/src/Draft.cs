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
        /// <summary>The last stage a cached model has run: the main roads (the bullseye moves the downtown, which the streets on use).</summary>
        const string CACHED_THROUGH = "roads";
        /// <summary>The first stage after the terrain: a town with an editor's own main roads runs from here.</summary>
        const string CENTRE_STAGE = "site";
        const int CACHE_SIZE = 6;

        sealed class Cached
        {
            public string key;
            public TownModel model;
            public Dictionary<string, long> ms;
            public long used;
        }

        static readonly List<Cached> cache = new List<Cached>();
        /// <summary>Terrain and rivers alone (before the site), by key, and the site's water and road cost from them (for the downtown).</summary>
        static readonly Dictionary<string, (TownModel model, Dictionary<string, long> ms, byte[] water, float[] cost)> terrain = new Dictionary<string, (TownModel, Dictionary<string, long>, byte[], float[])>();
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

        /// <summary>
        /// What the stages before the streets depend on besides the key: an editor's own main roads, if any (the
        /// bullseye moves only the downtown, set afresh on a cached model).
        /// </summary>
        static string RoadsSig(string baseKey, Skeleton s)
        {
            if (s?.mains == null) return baseKey;
            var only = new Skeleton { mains = s.mains };
            return baseKey + "\n" + only.ToJson();
        }

        /// <summary>
        /// The town from its key and this skeleton, every stage checked: from the cached main roads (or terrain) unless
        /// `full`. Stage violations are kept (not thrown) so Manage can show them; `ms` is each stage's time.
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
            string sig = RoadsSig(baseKey, skeleton);
            TownModel model = null;
            int from = 0;
            if (!full)
            {
                var hit = cache.FirstOrDefault(c => c.key == sig);
                if (hit != null && terrain.TryGetValue(baseKey, out var site))
                {
                    hit.used = ++tick;
                    model = hit.model.With();
                    // The downtown is the one thing the site decides that an editor moves.
                    model.downtown = SiteStage.Downtown(skeleton?.center, model.center, model.townRadius, site.water, site.cost, model.width, model.height);
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
                {
                    var rt = Raster.RoadTerrain(model);
                    terrain[baseKey] = (model.With(), Copy(ms), rt.water, rt.cost);
                }
                if (stage.Name == CACHED_THROUGH && (full || cache.All(c => c.key != sig)))
                    Keep(sig, model.With(), Copy(ms));
            }
            return model;
        }

        static Dictionary<string, long> Copy(Dictionary<string, long> d) => new Dictionary<string, long>(d);

        static void Keep(string key, TownModel model, Dictionary<string, long> ms)
        {
            cache.RemoveAll(c => c.key == key);
            cache.Add(new Cached { key = key, model = model, ms = ms, used = ++tick });
            while (cache.Count > CACHE_SIZE) cache.Remove(cache.OrderBy(c => c.used).First());
        }

        // ---- plan ----

        sealed class Summary
        {
            public HashSet<(int, int)> houses;
            /// <summary>The people living in the town: its households (Retail) at LandStage.PEOPLE_A_HOME each.</summary>
            public int people;
            public int shops, roundabouts, signals;
            public List<(Vec2 a, Vec2 b)> roads;
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
            var (model, ms, violations, regions) = Settle(key, ref skeleton, full);
            long grown = sw.ElapsedMilliseconds;
            var layout = Layout.Build(model);
            long laid = sw.ElapsedMilliseconds;
            var summary = Summarise(model, layout);
            long summed = sw.ElapsedMilliseconds;
            // Kept as a base too: the edit screen's first plan is the town as it is, what every later plan is against.
            if (!full)
            {
                if (bases.Count > 16) bases.Clear();
                bases[key + "\n" + (skeleton?.ToJson() ?? "")] = summary;
            }
            var problems = new List<Problem>();
            // What Settle couldn't put right (it tries hard): the town can't be committed like this.
            foreach (var (stage, text) in violations) problems.Add(new Problem { level = "error", text = $"{stage}: {text}", at = null });
            foreach (var r in regions) if (!r.ok) problems.Add(new Problem { level = "warning", text = r.text, at = r.at });

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
            // The town's shape (junction gaps, angles) and the design's notes aren't the editor's to care about: the
            // generator plans the streets, and Settle tries again until its rules hold.

            object changes = null;
            if (before != null)
            {
                // Only the town's people, all told: houses come and go as an area is planned again, and anyone whose house
                // goes moves to the nearest free one when it's committed.
                if (summary.people < before.people)
                    problems.Add(new Problem { level = "warning", text = $"Population down by {before.people - summary.people:N0} ({before.people:N0} → {summary.people:N0})", at = null });
                changes = new Dictionary<string, object>
                {
                    ["population"] = new[] { before.people, summary.people },
                    ["shops"] = new[] { before.shops, summary.shops },
                    ["roadsAdded"] = summary.roads.Count(r => !before.roads.Any(o => Skeleton.SameEnds(r.a, r.b, o.a, o.b))),
                    ["roadsRemoved"] = before.roads.Count(r => !summary.roads.Any(o => Skeleton.SameEnds(r.a, r.b, o.a, o.b))),
                    ["roundabouts"] = new[] { before.roundabouts, summary.roundabouts },
                    ["signals"] = new[] { before.signals, summary.signals },
                };
            }
            long compared = sw.ElapsedMilliseconds;
            var town = Town(model, layout, skeleton, summary, ms);
            long told = sw.ElapsedMilliseconds;
            var answer = new Dictionary<string, object>
            {
                ["id"] = IdOf(id),
                ["ok"] = true,
                ["ms"] = sw.ElapsedMilliseconds,
                ["town"] = town,
                ["skeleton"] = skeleton == null ? new Dictionary<string, object>() : Json.Parse(skeleton.ToJson()),
                ["regions"] = regions.Select(r => new Dictionary<string, object> { ["index"] = r.index, ["ok"] = r.ok, ["text"] = r.text }).ToList(),
                ["problems"] = problems.Select(p => new Dictionary<string, object> { ["level"] = p.level, ["text"] = p.text, ["at"] = p.at is Vec2 a ? Pt(a) : null }).ToList(),
                ["changes"] = changes,
            };
            Log($"plan {key}{(skeleton != null ? " (edited)" : "")}{(full ? " full" : "")}: {sw.ElapsedMilliseconds} ms (grow {grown}, layout {laid - grown}, summary {summed - laid}, base {compared - summed}, answer {told - compared}), {problems.Count(p => p.level == "error")} error(s), {problems.Count(p => p.level == "warning")} warning(s)");
            return answer;
        }

        // ---- settling an edit: trying again until it works ----

        sealed class RegionOutcome
        {
            public int index;
            public bool ok;
            public string text;
            public Vec2? at;
        }

        /// <summary>Tries for the newest regenerated area: its salt moved on each time a stage's rule breaks or it lacks what was wanted.</summary>
        const int REGION_TRIES = 5;
        /// <summary>Tries for a moved downtown that breaks a stage's rule: pulled halfway back to the centre each time.</summary>
        const int DOWNTOWN_TRIES = 3;

        /// <summary>
        /// The town, put right where an edit broke it, so an editor never has to: the newest area regenerated again
        /// with another salt until every stage's rules hold and it has what was wanted (the first that keeps the rules
        /// if none has it all), else left out; a downtown that breaks them pulled back towards the centre. `skeleton`
        /// becomes what was planned. A commit (`full`) plans the skeleton as it's given: what it was settled to.
        /// </summary>
        static (TownModel model, Dictionary<string, long> ms, List<(string stage, string text)> violations, List<RegionOutcome> regions) Settle(string key, ref Skeleton skeleton, bool full)
        {
            if (!full && skeleton != null && skeleton.regions.Count > 0 && skeleton.parks == null)
            {
                // The first area regenerated: the parks the town kept green kept so (Skeleton.parks), from the town as it
                // was (kept from its plan when the edit screen opened, else grown again).
                var before = Skeleton.FromJson(skeleton.ToJson());
                before.regions.Clear();
                if (before.IsEmpty) before = null;
                if (!parksOf.TryGetValue(ParksSig(key, before), out var kept)) kept = LandStage.MoreParkMiddles(Grow(key, before, false, out _, out _));
                skeleton = Skeleton.FromJson(skeleton.ToJson());
                skeleton.parks = kept;
            }
            var model = Grow(key, skeleton, full, out var ms, out var violations);
            if (skeleton == null || skeleton.regions.Count == 0)
            {
                if (parksOf.Count > 32) parksOf.Clear();
                parksOf[ParksSig(key, skeleton)] = LandStage.MoreParkMiddles(model);
            }
            var outcomes = Outcomes(model);
            if (full || skeleton == null) return (model, ms, violations, outcomes);
            int last = skeleton.regions.Count - 1;
            bool Good(List<(string, string)> v, List<RegionOutcome> o) => v.Count == 0 && (last < 0 || o[last].ok);
            if (last >= 0 && !Good(violations, outcomes))
            {
                // Keeps the rules (and, best, has what was wanted): the first such try.
                (Skeleton sk, TownModel m, Dictionary<string, long> ms, List<(string, string)> v, List<RegionOutcome> o)? lawful = violations.Count == 0 ? (skeleton, model, ms, violations, outcomes) : null;
                for (int t = 1; t < REGION_TRIES && !Good(violations, outcomes); t++)
                {
                    var tried = Skeleton.FromJson(skeleton.ToJson());
                    tried.regions[last].salt = (int)((tried.regions[last].salt * 1103515245L + 12345L + t) & 0x7fffffff);
                    var m = Grow(key, tried, false, out var tms, out var tv);
                    var o = Outcomes(m);
                    Log($"settle: area {last} again with salt {tried.regions[last].salt}: {tv.Count} broken rule(s), {(o[last].ok ? "has what was wanted" : o[last].text)}");
                    if (tv.Count == 0 && lawful == null) lawful = (tried, m, tms, tv, o);
                    if (Good(tv, o)) (skeleton, model, ms, violations, outcomes) = (tried, m, tms, tv, o);
                }
                if (!Good(violations, outcomes) && lawful != null) (skeleton, model, ms, violations, outcomes) = lawful.Value;
                if (violations.Count > 0)
                {
                    // Nothing it tried keeps the rules: the area left out.
                    var without = Skeleton.FromJson(skeleton.ToJson());
                    without.regions.RemoveAt(last);
                    skeleton = without.IsEmpty ? null : without;
                    model = Grow(key, skeleton, false, out ms, out violations);
                    outcomes = Outcomes(model);
                    outcomes.Add(new RegionOutcome { index = last, ok = false, text = "Couldn't regenerate that area without breaking the town: try other blocks or fewer options" });
                }
            }
            for (int t = 0; t < DOWNTOWN_TRIES && violations.Count > 0 && skeleton?.center is Vec2 c; t++)
            {
                var tried = Skeleton.FromJson(skeleton.ToJson());
                tried.center = new Vec2((c.x + model.center.x) / 2, (c.y + model.center.y) / 2);
                var m = Grow(key, tried, false, out var tms, out var tv);
                Log($"settle: downtown pulled in to ({tried.center.Value.x:0}, {tried.center.Value.y:0}): {tv.Count} broken rule(s)");
                (skeleton, model, ms, violations, outcomes) = (tried, m, tms, tv, Outcomes(m));
            }
            return (model, ms, violations, outcomes);
        }

        /// <summary>The parks each town without regenerated areas kept green (LandStage.MoreParkMiddles), by ParksSig: what its first region keeps.</summary>
        static readonly Dictionary<string, List<Vec2>> parksOf = new Dictionary<string, List<Vec2>>();

        static string ParksSig(string key, Skeleton s) => Skeletons.WithRev(key, 0) + "\n" + (s?.ToJson() ?? "");

        /// <summary>Whether each regenerated area has what was wanted there.</summary>
        static List<RegionOutcome> Outcomes(TownModel m)
        {
            var o = new List<RegionOutcome>();
            for (int r = 0; r < Regions.Count(m); r++)
            {
                var want = Regions.Get(m, r).want;
                var middle = Regions.Middle(m, r);
                bool In(Vec2 at) => Regions.At(m, at) == r;
                var inside = m.buildings.Where(b => b.footprint != null && b.footprint.Count > 0 && In(Geo.PolygonCentroid(b.footprint))).ToList();
                var missing = new List<string>();
                if (want.park && !m.ParkBlockSet().Any(k => k < m.blocks.Count && In(Geo.PolygonCentroid(m.blocks[k].polygon)))) missing.Add("a park");
                if (want.school && !inside.Any(b => b.kind == BuildingKind.School)) missing.Add("a school");
                if (want.shops && !inside.Any(b => b.kind == BuildingKind.Shop)) missing.Add("shops");
                if (want.houses && !inside.Any(b => b.kind == BuildingKind.House)) missing.Add("houses");
                if (want.roundabout && (m.design == null || !m.design.nodes.Any(n => n != null && n.form == JunctionForm.Roundabout && Near(m, r, m.roads.nodes[n.node].at)))) missing.Add("a roundabout");
                o.Add(new RegionOutcome
                {
                    index = r,
                    ok = missing.Count == 0,
                    text = missing.Count == 0 ? null : $"No room for {string.Join(" or ", missing)} there",
                    at = middle,
                });
            }
            return o;
        }

        static bool Near(TownModel m, int r, Vec2 at)
        {
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                    if (Regions.At(m, new Vec2(at.x + dx, at.y + dy)) == r) return true;
            return false;
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
                people = (int)Math.Round((model.services?.households ?? 0) * LandStage.PEOPLE_A_HOME),
                shops = model.buildings.Count(b => b.kind == BuildingKind.Shop),
                roads = model.roads.edges.Select(e => (model.roads.nodes[e.from].at, model.roads.nodes[e.to].at)).ToList(),
            };
            if (model.design != null)
            {
                s.roundabouts = model.design.nodes.Count(n => n != null && n.form == JunctionForm.Roundabout);
                s.signals = model.design.nodes.Count(n => n != null && n.control == DesignControl.Signal);
            }
            return s;
        }

        static double Dist(Vec2 a, Vec2 b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

        static double Length(List<Vec2> pts)
        {
            double l = 0;
            for (int i = 1; i < pts.Count; i++) l += Dist(pts[i - 1], pts[i]);
            return l;
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
                ["downtown"] = Pt(model.downtown),
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
                    ["people"] = summary.people,
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

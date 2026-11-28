// A town's street skeleton: what an editor has decided about its streets, laid over what its key would grow
// (docs/street-editor.md §4). A town is its key plus its skeleton; the key carries `&rev=N`, and the skeleton
// of that revision is fetched (the game), sent (the sim worker) or read (the checks) before the town is grown.
// Everything not in the skeleton is still generated, so most of a town stays exactly as its key made it.
//
// Saved as JSON (town_revisions.skeleton, town_drafts.skeleton), matched by position rather than by ids: ids are
// generation order, and any change to the generator renumbers them.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Porchlight.Core;

namespace Porchlight.Model
{
    /// <summary>A road the editor drew or moved: kept exactly where it is.</summary>
    public sealed class SkeletonRoad
    {
        public List<Vec2> points = new List<Vec2>();
        public RoadKind kind = RoadKind.Street;
        /// <summary>Null: classed as the generator would.</summary>
        public RoadClass? cls;
    }

    /// <summary>What an editor set on one road (found by its two ends, within a tile), over what the design stage would choose.</summary>
    public sealed class RoadOverride
    {
        public Vec2 a, b;
        public RoadClass? cls;
        /// <summary>Through lanes each way (1–3); 0, the design stage's choice.</summary>
        public int lanes;
        public bool? divided;
        /// <summary>One way, from `a` to `b`.</summary>
        public bool? oneWay;
        /// <summary>The design stage leaves this road as it is.</summary>
        public bool locked;
    }

    /// <summary>What an editor set on one junction (found by where it is, within 1.5 tiles).</summary>
    public sealed class JunctionOverride
    {
        public Vec2 at;
        /// <summary>"auto", "free", "stop", "allway", "signal" or "roundabout".</summary>
        public string control = "auto";
    }

    /// <summary>What an editor asked a regenerated area to have (RegionStage): each wanted, else as the generator likes.</summary>
    public sealed class RegionWant
    {
        public bool roundabout, park, school, shops, houses;
    }

    /// <summary>
    /// An area an editor picked (blocks selected on the map) and regenerated: its streets planned afresh with their own
    /// random stream (`salt`), and its lots and buildings too, with what was wanted there. Found by the blocks'
    /// outlines as they were when picked, so it covers the same land however the town changes round it.
    /// </summary>
    public sealed class SkeletonRegion
    {
        public List<List<Vec2>> blocks = new List<List<Vec2>>();
        public int salt;
        public RegionWant want = new RegionWant();
    }

    public sealed class Skeleton
    {
        /// <summary>The town's centre, if an editor moved it (the bullseye): roads not pinned head here, and the zones, the high street and the shops arrange themselves round it.</summary>
        public Vec2? center;
        /// <summary>Every main road, once an editor has changed any of them; null while they're the generator's.</summary>
        public List<SkeletonRoad> mains;
        /// <summary>Streets an editor drew or moved: kept, and the streets round them planned to meet them.</summary>
        public List<SkeletonRoad> streets = new List<SkeletonRoad>();
        /// <summary>Generated roads an editor deleted, by their two ends.</summary>
        public List<(Vec2 a, Vec2 b)> removed = new List<(Vec2, Vec2)>();
        public List<RoadOverride> roads = new List<RoadOverride>();
        public List<JunctionOverride> junctions = new List<JunctionOverride>();
        /// <summary>Areas regenerated, in order: a later one over an earlier.</summary>
        public List<SkeletonRegion> regions = new List<SkeletonRegion>();
        /// <summary>
        /// The middles of the blocks the town kept green for its park land (LandStage.MoreParks) before its first area
        /// was regenerated, or null: kept green as they were, so regenerating an area never moves a park elsewhere.
        /// </summary>
        public List<Vec2> parks;

        public bool IsEmpty => center == null && mains == null && streets.Count == 0 && removed.Count == 0 && roads.Count == 0 && junctions.Count == 0 && regions.Count == 0;

        /// <summary>The areas' tiles, worked out once for a map of this size (Gen.Regions).</summary>
        [NonSerialized] public object areas;

        /// <summary>How near (tiles) a saved end has to be to a road's end to be that road.</summary>
        public const double MATCH = 1.0;

        public static bool SameEnds(Vec2 a, Vec2 b, Vec2 x, Vec2 y) =>
            (Geo.Dist(a, x) <= MATCH && Geo.Dist(b, y) <= MATCH) || (Geo.Dist(a, y) <= MATCH && Geo.Dist(b, x) <= MATCH);

        public RoadOverride RoadAt(Vec2 a, Vec2 b)
        {
            foreach (var r in roads) if (SameEnds(r.a, r.b, a, b)) return r;
            return null;
        }

        public JunctionOverride JunctionAt(Vec2 at)
        {
            foreach (var j in junctions) if (Geo.Dist(j.at, at) <= 1.5) return j;
            return null;
        }

        public bool Removed(Vec2 a, Vec2 b)
        {
            foreach (var (x, y) in removed) if (SameEnds(x, y, a, b)) return true;
            return false;
        }

        // JSON

        static List<object> Pt(Vec2 p) => new List<object> { Math.Round(p.x * 100) / 100, Math.Round(p.y * 100) / 100 };

        static Vec2 ReadPt(object o)
        {
            if (o is List<object> l && l.Count >= 2 && l[0] is double x && l[1] is double y && Numbers.IsFinite(x) && Numbers.IsFinite(y)) return new Vec2(x, y);
            throw new FormatException("a point is two numbers");
        }

        static string ClassName(RoadClass c) => c == RoadClass.Arterial ? "arterial" : c == RoadClass.Collector ? "collector" : "local";

        static RoadClass? ReadClass(string s) => s switch
        {
            "arterial" => RoadClass.Arterial,
            "collector" => RoadClass.Collector,
            "local" => RoadClass.Local,
            _ => null,
        };

        static Dictionary<string, object> RoadJson(SkeletonRoad r)
        {
            var pts = new List<object>();
            foreach (var p in r.points) pts.Add(Pt(p));
            var o = new Dictionary<string, object> { ["pts"] = pts, ["kind"] = r.kind == RoadKind.Main ? "main" : r.kind == RoadKind.Path ? "path" : "street" };
            if (r.cls != null) o["cls"] = ClassName(r.cls.Value);
            return o;
        }

        static SkeletonRoad ReadRoad(object o)
        {
            var r = new SkeletonRoad();
            foreach (var p in Json.Arr(o, "pts") ?? new List<object>()) r.points.Add(ReadPt(p));
            r.kind = Json.Str(o, "kind") switch { "main" => RoadKind.Main, "path" => RoadKind.Path, _ => RoadKind.Street };
            r.cls = ReadClass(Json.Str(o, "cls"));
            if (r.points.Count < 2) throw new FormatException("a road has two points or more");
            return r;
        }

        public string ToJson()
        {
            var o = new Dictionary<string, object> { ["v"] = 1.0 };
            if (center != null) o["center"] = Pt(center.Value);
            if (mains != null)
            {
                var l = new List<object>();
                foreach (var r in mains) l.Add(RoadJson(r));
                o["mains"] = l;
            }
            var streetsJ = new List<object>();
            foreach (var r in streets) streetsJ.Add(RoadJson(r));
            o["streets"] = streetsJ;
            var removedJ = new List<object>();
            foreach (var (a, b) in removed) removedJ.Add(new List<object> { Pt(a), Pt(b) });
            o["removed"] = removedJ;
            var roadsJ = new List<object>();
            foreach (var r in roads)
            {
                var x = new Dictionary<string, object> { ["a"] = Pt(r.a), ["b"] = Pt(r.b) };
                if (r.cls != null) x["cls"] = ClassName(r.cls.Value);
                if (r.lanes > 0) x["lanes"] = (double)r.lanes;
                if (r.divided != null) x["divided"] = r.divided.Value;
                if (r.oneWay != null) x["oneWay"] = r.oneWay.Value;
                if (r.locked) x["locked"] = true;
                roadsJ.Add(x);
            }
            o["roads"] = roadsJ;
            var junctionsJ = new List<object>();
            foreach (var j in junctions) junctionsJ.Add(new Dictionary<string, object> { ["at"] = Pt(j.at), ["control"] = j.control });
            o["junctions"] = junctionsJ;
            if (regions.Count > 0)
            {
                var regionsJ = new List<object>();
                foreach (var r in regions)
                {
                    var blocksJ = new List<object>();
                    foreach (var poly in r.blocks)
                    {
                        var pts = new List<object>();
                        foreach (var p in poly) pts.Add(Pt(p));
                        blocksJ.Add(pts);
                    }
                    regionsJ.Add(new Dictionary<string, object>
                    {
                        ["blocks"] = blocksJ,
                        ["salt"] = (double)r.salt,
                        ["want"] = new Dictionary<string, object> { ["roundabout"] = r.want.roundabout, ["park"] = r.want.park, ["school"] = r.want.school, ["shops"] = r.want.shops, ["houses"] = r.want.houses },
                    });
                }
                o["regions"] = regionsJ;
            }
            if (parks != null)
            {
                var parksJ = new List<object>();
                foreach (var p in parks) parksJ.Add(Pt(p));
                o["parks"] = parksJ;
            }
            return Json.Serialize(o);
        }

        static bool? ReadBool(object o, string key) => o is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v is bool b ? b : (bool?)null;

        /// <summary>Read a skeleton; throws FormatException on anything malformed.</summary>
        public static Skeleton FromJson(string json)
        {
            var o = Json.Parse(json) as Dictionary<string, object> ?? throw new FormatException("a skeleton is an object");
            var s = new Skeleton();
            if (o.TryGetValue("center", out var c) && c != null) s.center = ReadPt(c);
            var mains = Json.Arr(o, "mains");
            if (mains != null)
            {
                s.mains = new List<SkeletonRoad>();
                foreach (var r in mains)
                {
                    var road = ReadRoad(r);
                    road.kind = RoadKind.Main;
                    s.mains.Add(road);
                }
            }
            foreach (var r in Json.Arr(o, "streets") ?? new List<object>())
            {
                var road = ReadRoad(r);
                if (road.kind == RoadKind.Main) road.kind = RoadKind.Street;
                s.streets.Add(road);
            }
            foreach (var r in Json.Arr(o, "removed") ?? new List<object>())
                if (r is List<object> l && l.Count == 2) s.removed.Add((ReadPt(l[0]), ReadPt(l[1])));
            foreach (var r in Json.Arr(o, "roads") ?? new List<object>())
            {
                var lanes = Json.Num(r, "lanes", 0);
                s.roads.Add(new RoadOverride
                {
                    a = ReadPt(((Dictionary<string, object>)r)["a"]),
                    b = ReadPt(((Dictionary<string, object>)r)["b"]),
                    cls = ReadClass(Json.Str(r, "cls")),
                    lanes = Numbers.IsFinite(lanes) ? Math.Max(0, Math.Min(3, (int)lanes)) : 0,
                    divided = ReadBool(r, "divided"),
                    oneWay = ReadBool(r, "oneWay"),
                    locked = ReadBool(r, "locked") == true,
                });
            }
            foreach (var j in Json.Arr(o, "junctions") ?? new List<object>())
            {
                string control = Json.Str(j, "control") ?? "auto";
                if (Array.IndexOf(Controls, control) < 0) throw new FormatException($"no such control: {control}");
                s.junctions.Add(new JunctionOverride { at = ReadPt(((Dictionary<string, object>)j)["at"]), control = control });
            }
            foreach (var r in Json.Arr(o, "regions") ?? new List<object>())
            {
                var region = new SkeletonRegion();
                foreach (var poly in Json.Arr(r, "blocks") ?? new List<object>())
                {
                    if (!(poly is List<object> pl) || pl.Count < 3) throw new FormatException("a region's block is three points or more");
                    var pts = new List<Vec2>();
                    foreach (var p in pl) pts.Add(ReadPt(p));
                    region.blocks.Add(pts);
                }
                if (region.blocks.Count == 0) continue;
                double salt = Json.Num(r, "salt", 0);
                region.salt = Numbers.IsFinite(salt) ? (int)Math.Max(0, Math.Min(int.MaxValue, salt)) : 0;
                var want = r is Dictionary<string, object> rd && rd.TryGetValue("want", out var wv) ? wv : null;
                region.want = new RegionWant
                {
                    roundabout = ReadBool(want, "roundabout") == true,
                    park = ReadBool(want, "park") == true,
                    school = ReadBool(want, "school") == true,
                    shops = ReadBool(want, "shops") == true,
                    houses = ReadBool(want, "houses") == true,
                };
                s.regions.Add(region);
            }
            var parksJ = Json.Arr(o, "parks");
            if (parksJ != null)
            {
                s.parks = new List<Vec2>();
                foreach (var p in parksJ) s.parks.Add(ReadPt(p));
            }
            return s;
        }

        public static readonly string[] Controls = { "auto", "free", "stop", "allway", "signal", "roundabout" };
    }

    /// <summary>
    /// The skeletons known here, by town key (with its `rev`): put by whatever fetched or was sent them, read when a
    /// key is parsed (TownKey.Parse, the game's ParseUrlState). A key with `rev` above 0 whose skeleton isn't here
    /// grows as its key alone would (and says so once).
    /// </summary>
    public static class Skeletons
    {
        static readonly ConcurrentDictionary<string, Skeleton> known = new ConcurrentDictionary<string, Skeleton>();

        public static void Put(string key, Skeleton s)
        {
            if (key != null && s != null) known[Canonical(key)] = s;
        }

        public static void Put(string key, string json) => Put(key, Skeleton.FromJson(json));

        public static Skeleton For(string key) => key != null && known.TryGetValue(Canonical(key), out var s) ? s : null;

        public static bool Has(string key) => key != null && known.ContainsKey(Canonical(key));

        /// <summary>The revision a key names: its `rev`, or 0.</summary>
        public static int RevOf(string key)
        {
            foreach (var part in (key ?? "").TrimStart('#', '?').Split('&'))
                if (part.StartsWith("rev=", StringComparison.Ordinal) && int.TryParse(part.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)) return Math.Max(0, r);
            return 0;
        }

        /// <summary>The key with its `rev` set to `rev` (left out at 0), keeping the rest of it as it was.</summary>
        public static string WithRev(string key, int rev)
        {
            var parts = new List<string>();
            foreach (var part in (key ?? "").Split('&'))
                if (part.Length > 0 && !part.StartsWith("rev=", StringComparison.Ordinal)) parts.Add(part);
            if (rev > 0) parts.Add("rev=" + rev.ToString(CultureInfo.InvariantCulture));
            return string.Join("&", parts);
        }

        static string Canonical(string key) => key.TrimStart('#', '?');
    }
}

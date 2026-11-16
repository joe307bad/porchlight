// A town's traffic settings: what its owner has changed from what the town
// would do by itself. A cascade of sparse layers, each only saying what it
// changes:
//
//   built-in defaults → preset → town-wide settings → junction and road overrides → events on now
//
// resolved into flat arrays (ResolvedTraffic) only when the settings change, so
// the traffic's every step reads them as cheaply as the constants they replace.
// RoadSense's choice for each junction is the bottom of the cascade: "auto".
//
// Overrides are keyed by where things are, not by list index (which any change
// to the generator would shuffle): a junction by its position to a quarter
// tile, a road by its two ends' keys, its kind and its middle. A key that no
// longer matches exactly is matched to the nearest junction of as many legs
// within a tile; one that matches nothing is kept, and reported as orphaned,
// never dropped.

using System;
using System.Collections.Generic;
using System.Globalization;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class JunctionOverride
    {
        public string key;
        /// <summary>Its control, or null for RoadSense's own ("auto").</summary>
        public JunctionControl? control;
        /// <summary>Walkers may cross its legs (false: no crossing here, they go round).</summary>
        public bool crossings = true;
        /// <summary>Seconds walkers get to start across before the cars' green, at a signal.</summary>
        public double leading = double.NaN;
    }

    public sealed class RoadOverride
    {
        public string key;
        public bool closed;
        /// <summary>0 both ways; 1 only the way it was drawn (from its `from` end); -1 only the other way.</summary>
        public int oneWay;
        /// <summary>Times its usual speed (1: as RoadSense set it).</summary>
        public double speed = 1;
    }

    /// <summary>Something on for a while: roads closed (a parade, a race, roadworks) from `from` until `until` (ms since 1970, UTC).</summary>
    public sealed class TrafficEvent
    {
        public string name;
        public string kind = "closure";
        public List<string> roads = new List<string>();
        public long from, until;
    }

    public sealed class TrafficConfig
    {
        public const string AUTO = "auto", SLEEPY = "sleepy", BUSY = "busy";
        public static readonly string[] Presets = { AUTO, SLEEPY, BUSY };

        /// <summary>The server's count of changes; a patch names the version it was made against.</summary>
        public int version;
        public string preset = AUTO;
        // Town-wide settings: NaN (or null) where the preset decides.
        public double density = double.NaN, cruise = double.NaN, patience = double.NaN, maxCars = double.NaN;
        public bool? signals, nightFlash, wander;
        public List<JunctionOverride> junctions = new List<JunctionOverride>();
        public List<RoadOverride> roads = new List<RoadOverride>();
        public List<TrafficEvent> events = new List<TrafficEvent>();
        /// <summary>The town key and generator version the keys were made against.</summary>
        public string town;
        public int generator = GENERATOR;

        /// <summary>Bumped whenever the generator would place junctions differently.</summary>
        public const int GENERATOR = 1;

        /// <summary>The town-wide settings that can be set, with their bounds (as Params' sliders are).</summary>
        public static readonly ParamSpec[] Globals =
        {
            new ParamSpec { key = "density", label = "Cars on the road", group = "Traffic", min = 0, max = 2, step = 0.05 },
            new ParamSpec { key = "maxCars", label = "Most cars", group = "Traffic", min = 0, max = 120, step = 1 },
            new ParamSpec { key = "cruise", label = "Speed", group = "Traffic", min = 0.5, max = 1.5, step = 0.05 },
            new ParamSpec { key = "patience", label = "Patience (s)", group = "Traffic", min = 1, max = 20, step = 0.5 },
        };

        public TrafficConfig Clone() => Parse(ToJson());

        public string ToJson()
        {
            var o = new Dictionary<string, object> { ["version"] = version, ["preset"] = preset, ["generator"] = generator };
            if (town != null) o["town"] = town;
            foreach (var s in Globals) if (!double.IsNaN(Get(s.key))) o[s.key] = Get(s.key);
            if (signals != null) o["signals"] = signals.Value;
            if (nightFlash != null) o["nightFlash"] = nightFlash.Value;
            if (wander != null) o["wander"] = wander.Value;
            var js = new List<object>();
            foreach (var j in junctions)
            {
                var d = new Dictionary<string, object> { ["key"] = j.key };
                if (j.control != null) d["control"] = j.control.Value.ToString();
                if (!j.crossings) d["crossings"] = false;
                if (!double.IsNaN(j.leading)) d["leading"] = j.leading;
                js.Add(d);
            }
            o["junctions"] = js;
            var rs = new List<object>();
            foreach (var r in roads)
            {
                var d = new Dictionary<string, object> { ["key"] = r.key };
                if (r.closed) d["closed"] = true;
                if (r.oneWay != 0) d["oneWay"] = r.oneWay;
                if (r.speed != 1) d["speed"] = r.speed;
                rs.Add(d);
            }
            o["roads"] = rs;
            var es = new List<object>();
            foreach (var e in events)
                es.Add(new Dictionary<string, object> { ["name"] = e.name ?? "", ["kind"] = e.kind, ["roads"] = new List<object>(e.roads), ["from"] = e.from, ["until"] = e.until });
            o["events"] = es;
            return Json.Serialize(o);
        }

        public static TrafficConfig Parse(string json)
        {
            var c = new TrafficConfig();
            if (string.IsNullOrEmpty(json)) return c;
            object o;
            try { o = Json.Parse(json); }
            catch (FormatException) { return c; }
            c.version = (int)Json.Num(o, "version", 0);
            c.preset = Json.Str(o, "preset") ?? AUTO;
            if (Array.IndexOf(Presets, c.preset) < 0) c.preset = AUTO;
            c.town = Json.Str(o, "town");
            c.generator = (int)Json.Num(o, "generator", GENERATOR);
            foreach (var s in Globals) c.Set(s.key, Json.Num(o, s.key));
            c.signals = Bool(o, "signals");
            c.nightFlash = Bool(o, "nightFlash");
            c.wander = Bool(o, "wander");
            foreach (var x in Json.Arr(o, "junctions") ?? new List<object>())
            {
                var key = Json.Str(x, "key");
                if (key == null) continue;
                var j = new JunctionOverride { key = key, crossings = Bool(x, "crossings") ?? true, leading = Json.Num(x, "leading") };
                if (Enum.TryParse(Json.Str(x, "control") ?? "", out JunctionControl jc) && Enum.IsDefined(typeof(JunctionControl), jc)) j.control = jc;
                c.junctions.Add(j);
            }
            foreach (var x in Json.Arr(o, "roads") ?? new List<object>())
            {
                var key = Json.Str(x, "key");
                if (key == null) continue;
                c.roads.Add(new RoadOverride { key = key, closed = Bool(x, "closed") ?? false, oneWay = Math.Sign((int)Json.Num(x, "oneWay", 0)), speed = Clamp(Json.Num(x, "speed", 1), 0.25, 2, 1) });
            }
            foreach (var x in Json.Arr(o, "events") ?? new List<object>())
            {
                var e = new TrafficEvent { name = Json.Str(x, "name") ?? "", kind = Json.Str(x, "kind") ?? "closure", from = (long)Json.Num(x, "from", 0), until = (long)Json.Num(x, "until", 0) };
                foreach (var r in Json.Arr(x, "roads") ?? new List<object>()) if (r is string rk) e.roads.Add(rk);
                c.events.Add(e);
            }
            return c;
        }

        static bool? Bool(object o, string key) => o is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v is bool b ? b : (bool?)null;

        static double Clamp(double v, double lo, double hi, double fallback) => double.IsNaN(v) ? fallback : Math.Min(hi, Math.Max(lo, v));

        public double Get(string key) => key switch
        {
            "density" => density,
            "cruise" => cruise,
            "patience" => patience,
            "maxCars" => maxCars,
            _ => throw new ArgumentException("unknown traffic setting " + key),
        };

        /// <summary>Set a town-wide setting, snapped and clamped to its spec (NaN unsets it).</summary>
        public void Set(string key, double v)
        {
            ParamSpec spec = null;
            foreach (var s in Globals) if (s.key == key) spec = s;
            if (spec == null) throw new ArgumentException("unknown traffic setting " + key);
            if (!double.IsNaN(v)) v = Math.Min(spec.max, Math.Max(spec.min, Math.Round((v - spec.min) / spec.step) * spec.step + spec.min));
            switch (key)
            {
                case "density": density = v; break;
                case "cruise": cruise = v; break;
                case "patience": patience = v; break;
                case "maxCars": maxCars = v; break;
            }
        }

        public JunctionOverride Junction(string key, bool make = false)
        {
            foreach (var j in junctions) if (j.key == key) return j;
            if (!make) return null;
            var o = new JunctionOverride { key = key };
            junctions.Add(o);
            return o;
        }

        public RoadOverride Road(string key, bool make = false)
        {
            foreach (var r in roads) if (r.key == key) return r;
            if (!make) return null;
            var o = new RoadOverride { key = key };
            roads.Add(o);
            return o;
        }

        /// <summary>Drop overrides that say nothing any more (back to auto).</summary>
        public void Tidy()
        {
            junctions.RemoveAll(j => j.control == null && j.crossings && double.IsNaN(j.leading));
            roads.RemoveAll(r => !r.closed && r.oneWay == 0 && r.speed == 1);
        }
    }

    /// <summary>Keys for junctions and roads by where they are, stable across anything that doesn't move them.</summary>
    public static class TrafficKeys
    {
        static int Q(double v) => (int)Math.Round(v * 4);

        public static string Junction(Vec2 at) => "j" + Q(at.x).ToString(CultureInfo.InvariantCulture) + "_" + Q(at.y).ToString(CultureInfo.InvariantCulture);

        public static string Road(IsoLayout layout, int road)
        {
            var r = layout.roads[road];
            string a = Junction(layout.nodes[r.from].at), b = Junction(layout.nodes[r.to].at);
            if (string.CompareOrdinal(a, b) > 0) (a, b) = (b, a);
            var mid = Midpoint(r.points);
            return $"r{a}~{b}~{Names.Of(r.kind)}~{Q(mid.x).ToString(CultureInfo.InvariantCulture)}_{Q(mid.y).ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>The point halfway along a polyline.</summary>
        public static Vec2 Midpoint(IReadOnlyList<Vec2> pts)
        {
            double half = Geo.PolylineLength(pts) / 2, run = 0;
            for (int k = 1; k < pts.Count; k++)
            {
                double l = Geo.Dist(pts[k - 1], pts[k]);
                if (run + l >= half && l > 0) return Geo.Lerp(pts[k - 1], pts[k], (half - run) / l);
                run += l;
            }
            return pts[0];
        }

        /// <summary>A junction key's position, or null if it isn't one.</summary>
        public static Vec2? JunctionAt(string key)
        {
            if (key == null || key.Length < 2 || key[0] != 'j') return null;
            var parts = key.Substring(1).Split('_');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return null;
            return new Vec2(x / 4.0, y / 4.0);
        }
    }

    /// <summary>A town's traffic settings worked out for its roads and junctions as they are now: flat arrays the traffic reads every step.</summary>
    public sealed class ResolvedTraffic
    {
        public const double DENSITY = 1.0 / 12, CRUISE = 1, PATIENCE = 4;
        public const int MAX_CARS = 70;

        public double density = 1, cruise = CRUISE, patience = PATIENCE;
        public int maxCars = MAX_CARS;
        public bool signals = true, nightFlash = true, wander;
        /// <summary>By node id: who goes first there now.</summary>
        public JunctionControl[] control;
        /// <summary>By node id: what RoadSense chose (what "reset to auto" goes back to).</summary>
        public JunctionControl[] auto;
        public bool[] noCrossing;
        public double[] leading;
        /// <summary>By road: closed (by its override or an event on now), one way (see RoadOverride.oneWay), and times its speed.</summary>
        public bool[] closed;
        public int[] oneWay;
        public double[] speed;
        /// <summary>Overrides whose junction or road isn't in the town any more.</summary>
        public List<string> orphans = new List<string>();
        /// <summary>What the owner should know: junctions with no way out, roads that can't be reached.</summary>
        public List<string> warnings = new List<string>();

        public static ResolvedTraffic Defaults(IsoLayout layout) => Resolve(null, layout, 0);

        public static ResolvedTraffic Resolve(TrafficConfig config, IsoLayout layout, long nowMs)
        {
            int N = layout.nodes.Count, R = layout.roads.Count;
            var o = new ResolvedTraffic
            {
                control = new JunctionControl[N],
                auto = new JunctionControl[N],
                noCrossing = new bool[N],
                leading = new double[N],
                closed = new bool[R],
                oneWay = new int[R],
                speed = new double[R],
            };
            for (int n = 0; n < N; n++)
            {
                o.control[n] = o.auto[n] = layout.nodes[n].control;
                o.leading[n] = 1.5;
            }
            for (int r = 0; r < R; r++) o.speed[r] = 1;
            if (config == null) return o;

            // Preset, then the town's own settings.
            switch (config.preset)
            {
                case TrafficConfig.SLEEPY:
                    o.density = 0.5;
                    o.cruise = 0.85;
                    o.wander = true;
                    break;
                case TrafficConfig.BUSY:
                    o.density = 1.6;
                    o.maxCars = 110;
                    o.patience = 6;
                    break;
            }
            if (!double.IsNaN(config.density)) o.density = config.density;
            if (!double.IsNaN(config.cruise)) o.cruise = config.cruise;
            if (!double.IsNaN(config.patience)) o.patience = config.patience;
            if (!double.IsNaN(config.maxCars)) o.maxCars = (int)config.maxCars;
            if (config.signals != null) o.signals = config.signals.Value;
            if (config.nightFlash != null) o.nightFlash = config.nightFlash.Value;
            if (config.wander != null) o.wander = config.wander.Value;

            var legs = new int[N];
            foreach (var r in layout.roads)
            {
                if (r.kind == RoadKind.Path) continue;
                legs[r.from]++;
                legs[r.to]++;
            }
            var exact = new Dictionary<string, int>();
            for (int n = 0; n < N; n++) exact[TrafficKeys.Junction(layout.nodes[n].at)] = n;
            int FindJunction(string key)
            {
                if (exact.TryGetValue(key, out int n)) return n;
                var at = TrafficKeys.JunctionAt(key);
                if (at == null) return -1;
                int best = -1;
                double bestD = 1;
                for (int k = 0; k < N; k++)
                {
                    if (legs[k] < 3) continue;
                    double d = Geo.Dist(layout.nodes[k].at, at.Value);
                    if (d < bestD) { bestD = d; best = k; }
                }
                return best;
            }
            var roadKeys = new Dictionary<string, int>();
            for (int r = 0; r < R; r++) roadKeys[TrafficKeys.Road(layout, r)] = r;
            int FindRoad(string key)
            {
                if (roadKeys.TryGetValue(key, out int r)) return r;
                // Fuzzy: the same two junctions (matched as junctions are) and kind.
                var parts = key.StartsWith("r", StringComparison.Ordinal) ? key.Substring(1).Split('~') : new string[0];
                if (parts.Length != 4) return -1;
                int a = FindJunction(parts[0]), b = FindJunction(parts[1]);
                if (a < 0 || b < 0) return -1;
                for (int k = 0; k < R; k++)
                {
                    var rr = layout.roads[k];
                    if (Names.Of(rr.kind) == parts[2] && ((rr.from == a && rr.to == b) || (rr.from == b && rr.to == a))) return k;
                }
                return -1;
            }

            foreach (var j in config.junctions)
            {
                int n = FindJunction(j.key);
                if (n < 0)
                {
                    o.orphans.Add(j.key);
                    continue;
                }
                if (j.control != null && Fits(j.control.Value, legs[n])) o.control[n] = j.control.Value;
                o.noCrossing[n] = !j.crossings;
                if (!double.IsNaN(j.leading)) o.leading[n] = Math.Max(0, Math.Min(5, j.leading));
            }
            foreach (var ro in config.roads)
            {
                int r = FindRoad(ro.key);
                if (r < 0)
                {
                    o.orphans.Add(ro.key);
                    continue;
                }
                o.closed[r] |= ro.closed;
                o.oneWay[r] = ro.oneWay;
                o.speed[r] = ro.speed;
            }
            foreach (var e in config.events)
            {
                if (nowMs < e.from || nowMs >= e.until) continue;
                foreach (var key in e.roads)
                {
                    int r = FindRoad(key);
                    if (r >= 0) o.closed[r] = true;
                    else if (!o.orphans.Contains(key)) o.orphans.Add(key);
                }
            }
            if (!o.signals)
                for (int n = 0; n < N; n++)
                    if (o.control[n] == JunctionControl.Signal) o.control[n] = JunctionControl.AllWayStop;
            o.Warn(layout, legs);
            return o;
        }

        /// <summary>Whether a junction of `legs` legs can be run this way.</summary>
        public static bool Fits(JunctionControl c, int legs) => legs >= 3
            ? c != JunctionControl.None && c != JunctionControl.DeadEnd && c != JunctionControl.Gate
            : legs == 1 ? c == JunctionControl.DeadEnd || c == JunctionControl.Gate : c == JunctionControl.None;

        /// <summary>The controls a junction of `legs` legs can be set to.</summary>
        public static List<JunctionControl> Choices(int legs)
        {
            var o = new List<JunctionControl>();
            foreach (JunctionControl c in Enum.GetValues(typeof(JunctionControl))) if (Fits(c, legs) && c != JunctionControl.Roundabout) o.Add(c);
            return o;
        }

        /// <summary>Whether a car may drive `road` away from `node`.</summary>
        public bool MayLeave(IsoLayout layout, int road, int node)
        {
            if (closed[road]) return false;
            var r = layout.roads[road];
            int dir = r.from == node ? 1 : -1;
            return oneWay[road] == 0 || oneWay[road] == dir;
        }

        void Warn(IsoLayout layout, int[] legs)
        {
            int N = layout.nodes.Count;
            var byNode = new List<int>[N];
            for (int n = 0; n < N; n++) byNode[n] = new List<int>();
            for (int r = 0; r < layout.roads.Count; r++)
            {
                var rr = layout.roads[r];
                if (rr.kind == RoadKind.Path) continue;
                byNode[rr.from].Add(r);
                if (rr.to != rr.from) byNode[rr.to].Add(r);
            }
            // A junction a car can drive into but not out of.
            for (int n = 0; n < N; n++)
            {
                if (legs[n] < 2) continue;
                bool into = false, outOf = false;
                foreach (int r in byNode[n])
                {
                    var rr = layout.roads[r];
                    int other = rr.from == n ? rr.to : rr.from;
                    if (MayLeave(layout, r, other)) into = true;
                    if (MayLeave(layout, r, n)) outOf = true;
                }
                if (into && !outOf) warnings.Add($"every way out of the junction at {TrafficKeys.Junction(layout.nodes[n].at)} is closed");
            }
            // Roads that can't be reached from the centre over open roads.
            int hub = 0;
            for (int n = 1; n < N; n++) if (legs[n] > legs[hub]) hub = n;
            var seen = new bool[N];
            var stack = new List<int> { hub };
            seen[hub] = true;
            while (stack.Count > 0)
            {
                int n = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                foreach (int r in byNode[n])
                {
                    if (!MayLeave(layout, r, n)) continue;
                    var rr = layout.roads[r];
                    int m = rr.from == n ? rr.to : rr.from;
                    if (!seen[m])
                    {
                        seen[m] = true;
                        stack.Add(m);
                    }
                }
            }
            int unreachable = 0;
            for (int r = 0; r < layout.roads.Count; r++)
            {
                var rr = layout.roads[r];
                if (rr.kind != RoadKind.Path && !closed[r] && !seen[rr.from] && !seen[rr.to]) unreachable++;
            }
            if (unreachable > 0) warnings.Add($"{unreachable} road{(unreachable == 1 ? "" : "s")} can't be reached");
            if (orphans.Count > 0) warnings.Add($"{orphans.Count} override{(orphans.Count == 1 ? "" : "s")} no longer match a road or junction");
        }
    }
}

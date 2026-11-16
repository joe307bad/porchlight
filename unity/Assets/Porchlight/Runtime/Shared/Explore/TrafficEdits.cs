// Changes to a town's traffic settings as operations: what the game sends the
// room (traffic:patch), and what it applies itself when playing alone. The
// same operations, with the same bounds, as the server's
// (server/src/multiplayer/traffic.ts), which checks every one.
//
//   { op: "preset", value: "auto" | "sleepy" | "busy" }
//   { op: "global", key: "density" | "maxCars" | "cruise" | "patience", value: number | null }
//   { op: "flag", key: "signals" | "nightFlash" | "wander", value: bool | null }
//   { op: "junction", key, control?: JunctionControl name | null, crossings?: bool, leading?: number | null, reset?: true }
//   { op: "road", key, closed?: bool, oneWay?: -1 | 0 | 1, speed?: 0.25..2, reset?: true }
//   { op: "event", name, kind?: "closure", roads?: [road keys], from?, until?, remove?: true }
//   { op: "reset" }
//
// Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public static class TrafficEdits
    {
        public static Dictionary<string, object> Preset(string preset) => new Dictionary<string, object> { ["op"] = "preset", ["value"] = preset };

        /// <summary>A town-wide number; NaN puts it back to the preset's.</summary>
        public static Dictionary<string, object> Global(string key, double value) =>
            new Dictionary<string, object> { ["op"] = "global", ["key"] = key, ["value"] = double.IsNaN(value) ? null : (object)value };

        public static Dictionary<string, object> Flag(string key, bool? value) => new Dictionary<string, object> { ["op"] = "flag", ["key"] = key, ["value"] = value };

        /// <summary>A junction's control; null puts it back to RoadSense's own.</summary>
        public static Dictionary<string, object> Control(string junction, JunctionControl? control) =>
            new Dictionary<string, object> { ["op"] = "junction", ["key"] = junction, ["control"] = control?.ToString() };

        public static Dictionary<string, object> Crossings(string junction, bool on) => new Dictionary<string, object> { ["op"] = "junction", ["key"] = junction, ["crossings"] = on };

        public static Dictionary<string, object> ResetJunction(string junction) => new Dictionary<string, object> { ["op"] = "junction", ["key"] = junction, ["reset"] = true };

        public static Dictionary<string, object> Closed(string road, bool closed) => new Dictionary<string, object> { ["op"] = "road", ["key"] = road, ["closed"] = closed };

        public static Dictionary<string, object> OneWay(string road, int way) => new Dictionary<string, object> { ["op"] = "road", ["key"] = road, ["oneWay"] = (double)Math.Sign(way) };

        public static Dictionary<string, object> Speed(string road, double speed) => new Dictionary<string, object> { ["op"] = "road", ["key"] = road, ["speed"] = speed };

        public static Dictionary<string, object> ResetRoad(string road) => new Dictionary<string, object> { ["op"] = "road", ["key"] = road, ["reset"] = true };

        public static Dictionary<string, object> Closure(string name, IEnumerable<string> roads, long from, long until) =>
            new Dictionary<string, object> { ["op"] = "event", ["name"] = name, ["kind"] = "closure", ["roads"] = new List<object>(roads), ["from"] = (double)from, ["until"] = (double)until };

        /// <summary>A race course's roads closed (the marathon) from `from` until `until`, as an event.</summary>
        public static TrafficEvent CourseClosure(IsoLayout layout, Course course, long from, long until)
        {
            var e = new TrafficEvent { name = "Marathon", kind = "closure", from = from, until = until };
            foreach (int r in course.roads)
            {
                if (r < 0 || r >= layout.roads.Count) continue;
                string key = TrafficKeys.Road(layout, r);
                if (!e.roads.Contains(key)) e.roads.Add(key);
            }
            return e;
        }

        public static Dictionary<string, object> RemoveEvent(string name) => new Dictionary<string, object> { ["op"] = "event", ["name"] = name, ["remove"] = true };

        public static Dictionary<string, object> Reset() => new Dictionary<string, object> { ["op"] = "reset" };

        /// <summary>The next control a junction cycles to in the editor (null: back to auto).</summary>
        public static JunctionControl? NextControl(JunctionControl? current) => current switch
        {
            null => JunctionControl.Stop,
            JunctionControl.Stop => JunctionControl.AllWayStop,
            JunctionControl.AllWayStop => JunctionControl.Priority,
            JunctionControl.Priority => JunctionControl.RightBeforeLeft,
            JunctionControl.RightBeforeLeft => JunctionControl.Signal,
            _ => null,
        };

        /// <summary>Apply one operation to `c` (in place); null, or why not (and `c` is as it was).</summary>
        public static string Apply(TrafficConfig c, Dictionary<string, object> op)
        {
            if (op == null || !(op.TryGetValue("op", out var kind) && kind is string k)) return "bad change";
            switch (k)
            {
                case "reset":
                {
                    c.preset = TrafficConfig.AUTO;
                    c.density = c.cruise = c.patience = c.maxCars = double.NaN;
                    c.signals = c.nightFlash = c.wander = null;
                    c.junctions.Clear();
                    c.roads.Clear();
                    c.events.Clear();
                    return null;
                }
                case "preset":
                {
                    var p = Json.Str(op, "value");
                    if (Array.IndexOf(TrafficConfig.Presets, p) < 0) return "no such preset";
                    c.preset = p;
                    return null;
                }
                case "global":
                {
                    var key = Json.Str(op, "key");
                    ParamSpec spec = null;
                    foreach (var s in TrafficConfig.Globals) if (s.key == key) spec = s;
                    if (spec == null) return "no such setting";
                    op.TryGetValue("value", out var raw);
                    if (raw == null)
                    {
                        c.Set(key, double.NaN);
                        return null;
                    }
                    double v = Json.Num(op, "value");
                    if (double.IsNaN(v) || v < spec.min || v > spec.max) return key + " out of bounds";
                    c.Set(key, v);
                    return null;
                }
                case "flag":
                {
                    var key = Json.Str(op, "key");
                    op.TryGetValue("value", out var raw);
                    bool? v = raw is bool b ? b : (bool?)null;
                    if (raw != null && v == null) return "bad setting";
                    switch (key)
                    {
                        case "signals": c.signals = v; return null;
                        case "nightFlash": c.nightFlash = v; return null;
                        case "wander": c.wander = v; return null;
                        default: return "no such setting";
                    }
                }
                case "junction":
                {
                    var key = Json.Str(op, "key");
                    if (key == null || TrafficKeys.JunctionAt(key) == null) return "bad junction";
                    if (op.TryGetValue("reset", out var reset) && reset is bool rb && rb)
                    {
                        c.junctions.RemoveAll(j => j.key == key);
                        return null;
                    }
                    var was = c.Junction(key);
                    var o = was != null ? new JunctionOverride { key = key, control = was.control, crossings = was.crossings, leading = was.leading } : new JunctionOverride { key = key };
                    if (op.TryGetValue("control", out var ctl))
                    {
                        if (ctl == null) o.control = null;
                        else if (ctl is string cs && Enum.TryParse(cs, out JunctionControl jc) && Enum.IsDefined(typeof(JunctionControl), jc)) o.control = jc;
                        else return "no such control";
                    }
                    if (op.TryGetValue("crossings", out var cr))
                    {
                        if (!(cr is bool crb)) return "bad crossings";
                        o.crossings = crb;
                    }
                    if (op.TryGetValue("leading", out var ld))
                    {
                        double l = ld == null ? double.NaN : Json.Num(op, "leading");
                        if (ld != null && (double.IsNaN(l) || l < 0 || l > 10)) return "leading out of bounds";
                        o.leading = l;
                    }
                    if (was != null) c.junctions[c.junctions.IndexOf(was)] = o;
                    else c.junctions.Add(o);
                    c.Tidy();
                    return null;
                }
                case "road":
                {
                    var key = Json.Str(op, "key");
                    if (key == null || key.Length < 2 || key[0] != 'r' || key.IndexOf(' ') >= 0) return "bad road";
                    if (op.TryGetValue("reset", out var reset) && reset is bool rb && rb)
                    {
                        c.roads.RemoveAll(r => r.key == key);
                        return null;
                    }
                    var was = c.Road(key);
                    var o = was != null ? new RoadOverride { key = key, closed = was.closed, oneWay = was.oneWay, speed = was.speed } : new RoadOverride { key = key };
                    if (op.TryGetValue("closed", out var cl))
                    {
                        if (!(cl is bool clb)) return "bad closed";
                        o.closed = clb;
                    }
                    if (op.ContainsKey("oneWay"))
                    {
                        double w = Json.Num(op, "oneWay");
                        if (w != -1 && w != 0 && w != 1) return "bad one way";
                        o.oneWay = (int)w;
                    }
                    if (op.ContainsKey("speed"))
                    {
                        double sp = Json.Num(op, "speed");
                        if (double.IsNaN(sp) || sp < 0.25 || sp > 2) return "speed out of bounds";
                        o.speed = sp;
                    }
                    if (was != null) c.roads[c.roads.IndexOf(was)] = o;
                    else c.roads.Add(o);
                    c.Tidy();
                    return null;
                }
                case "event":
                {
                    var name = Json.Str(op, "name");
                    if (string.IsNullOrEmpty(name) || name.Length > 48) return "bad event name";
                    int at = c.events.FindIndex(e => e.name == name);
                    if (op.TryGetValue("remove", out var rm) && rm is bool rmb && rmb)
                    {
                        if (at >= 0) c.events.RemoveAt(at);
                        return null;
                    }
                    if ((Json.Str(op, "kind") ?? "closure") != "closure") return "no such event";
                    var roads = Json.Arr(op, "roads");
                    if (roads == null || roads.Count == 0 || roads.Count > 128) return "bad event roads";
                    var ev = new TrafficEvent { name = name, from = (long)Json.Num(op, "from", -1), until = (long)Json.Num(op, "until", -1) };
                    foreach (var r in roads)
                    {
                        if (!(r is string rk) || rk.Length < 2 || rk[0] != 'r') return "bad event roads";
                        ev.roads.Add(rk);
                    }
                    if (ev.from < 0 || ev.until <= ev.from) return "bad event times";
                    if (at >= 0) c.events[at] = ev;
                    else c.events.Add(ev);
                    return null;
                }
                default:
                    return "bad change";
            }
        }
    }
}

// The sidebar's Traffic section, while exploring: the town's traffic settings
// (TrafficConfig) and, for whoever made the town (or anyone playing alone),
// changing them. The preset and the town-wide numbers; signals, flashing at
// night, and cars that wander; then the junction nearest you (cycle its
// control, walkers' crossings on or off, back to auto) and the street you're
// on (close it, one way, slower or faster, back to auto). Changes go out as
// operations (TrafficEdits): online the room checks and keeps them and
// everyone's traffic follows. What no longer matches the town, and a change
// refused, are said at the top.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using UnityEngine;
using UnityEngine.UIElements;

namespace Porchlight.UI
{
    /// <summary>What the Traffic section shows.</summary>
    public sealed class TrafficView
    {
        public TrafficConfig config;
        /// <summary>Changes allowed (you made the town, or you're playing alone).</summary>
        public bool canEdit;
        public bool online;
        public string refused;
        public List<string> warnings = new List<string>();
        /// <summary>The junction nearest you (null: none near), what it is, and whether it's been changed from RoadSense's own.</summary>
        public string junctionKey, junctionLabel;
        public JunctionControl? junctionOverride;
        public bool junctionCrossings = true;
        /// <summary>The street you're on (null: none), what it is, and its settings.</summary>
        public string roadKey, roadLabel;
        public bool roadClosed;
        public int roadOneWay;
        public double roadSpeed = 1;
        public Action<List<object>> patch;
        /// <summary>How the traffic's doing (Traffic.Metrics), shown as it changes.</summary>
        public string scorecard;
    }

    public sealed class TrafficPanel
    {
        public readonly VisualElement root = new VisualElement();
        readonly Label score = El.Text("", "help-text");
        string shown;
        Action<List<object>> patch;

        void Send(params Dictionary<string, object>[] ops) => patch?.Invoke(new List<object>(ops));

        public void Show(TrafficView v)
        {
            patch = v?.patch;
            // Updated in place: rebuilding the section for it would drop a slider being dragged.
            score.text = v?.scorecard ?? "";
            string sig = v == null ? "-" : string.Join("|", v.config.ToJson(), v.canEdit, v.online, v.refused, string.Join(";", v.warnings), v.junctionKey, v.junctionLabel, v.roadKey, v.roadLabel);
            if (sig == shown) return;
            shown = sig;
            root.Clear();
            if (v == null)
            {
                root.Add(El.Text("There's no traffic here.", "help-text"));
                return;
            }
            var c = v.config;
            root.Add(score);
            root.Add(El.Text(v.canEdit
                ? (v.online ? "You made this town: changes here are for everyone in it." : "Playing alone: changes here are just for now.")
                : "Only whoever made the town can change its traffic.", "help-text"));
            if (!string.IsNullOrEmpty(v.refused)) root.Add(El.Text("Not changed: " + v.refused, "help-text"));
            foreach (var w in v.warnings) root.Add(El.Text("⚠ " + w, "help-text"));

            // The preset.
            var presets = El.Box("sb-row");
            presets.style.flexDirection = FlexDirection.Row;
            foreach (var p in TrafficConfig.Presets)
            {
                string preset = p;
                var b = El.Btn(Cap(p), $"{Cap(p)} traffic", () => Send(TrafficEdits.Preset(preset)), c.preset == p ? "btn-primary" : "mission-button");
                b.SetEnabled(v.canEdit);
                presets.Add(b);
            }
            root.Add(presets);

            // The town-wide numbers.
            foreach (var spec in TrafficConfig.Globals)
            {
                double now = c.Get(spec.key);
                root.Add(SliderRow(spec, now, v.canEdit));
            }

            // Signals, night flash, wandering.
            root.Add(Flag("Traffic signals", "signals", c.signals ?? true, v.canEdit));
            root.Add(Flag("Signals flash at night", "nightFlash", c.nightFlash ?? true, v.canEdit));
            root.Add(Flag("Cars wander (go nowhere in particular)", "wander", c.wander ?? false, v.canEdit));

            // The junction nearest you.
            root.Add(El.Text("Junction nearest you", "semibold"));
            if (v.junctionKey == null) root.Add(El.Text("None near.", "help-text"));
            else
            {
                root.Add(El.Text(v.junctionLabel, "help-text"));
                var row = Row();
                string jk = v.junctionKey;
                var next = TrafficEdits.NextControl(v.junctionOverride);
                row.Add(Btn(next == null ? "Back to auto" : $"Make it {Describe(next.Value)}", "Change how it's controlled", v.canEdit, () => Send(TrafficEdits.Control(jk, next))));
                bool crossings = v.junctionCrossings;
                row.Add(Btn(crossings ? "No crossings" : "Crossings", crossings ? "Walkers don't cross here" : "Walkers cross here again", v.canEdit, () => Send(TrafficEdits.Crossings(jk, !crossings))));
                row.Add(Btn("Reset", "Back to how the town has it", v.canEdit, () => Send(TrafficEdits.ResetJunction(jk))));
                root.Add(row);
            }

            // The street you're on.
            root.Add(El.Text("The street you're on", "semibold"));
            if (v.roadKey == null) root.Add(El.Text("Not on a street.", "help-text"));
            else
            {
                root.Add(El.Text(v.roadLabel, "help-text"));
                var row = Row();
                string rk = v.roadKey;
                bool closed = v.roadClosed;
                int way = v.roadOneWay;
                double speed = v.roadSpeed;
                row.Add(Btn(closed ? "Open" : "Close", closed ? "Open it to cars again" : "Close it to cars", v.canEdit, () => Send(TrafficEdits.Closed(rk, !closed))));
                row.Add(Btn(way == 0 ? "One way" : way == 1 ? "Other way" : "Both ways", "Which way cars may drive it", v.canEdit, () => Send(TrafficEdits.OneWay(rk, way == 0 ? 1 : way == 1 ? -1 : 0))));
                root.Add(row);
                var row2 = Row();
                row2.Add(Btn("Slower", "Cars drive it slower", v.canEdit && speed > 0.25, () => Send(TrafficEdits.Speed(rk, Math.Max(0.25, speed - 0.25)))));
                row2.Add(Btn("Faster", "Cars drive it faster", v.canEdit && speed < 2, () => Send(TrafficEdits.Speed(rk, Math.Min(2, speed + 0.25)))));
                row2.Add(Btn("Reset", "Back to how the town has it", v.canEdit, () => Send(TrafficEdits.ResetRoad(rk))));
                root.Add(row2);
            }

            var reset = Btn("Reset all to auto", "Every traffic setting back to how the town has it", v.canEdit, () => Send(TrafficEdits.Reset()));
            root.Add(reset);
        }

        static VisualElement Row()
        {
            var r = El.Box("sb-row");
            r.style.flexDirection = FlexDirection.Row;
            r.style.flexWrap = Wrap.Wrap;
            return r;
        }

        static Button Btn(string text, string tooltip, bool enabled, Action click)
        {
            var b = El.Btn(text, tooltip, click, "mission-button");
            b.SetEnabled(enabled);
            return b;
        }

        VisualElement Flag(string label, string key, bool on, bool enabled)
        {
            var t = new Toggle { text = label, value = on, focusable = false };
            t.AddToClassList("check");
            t.SetEnabled(enabled);
            t.RegisterValueChangedCallback(e => Send(TrafficEdits.Flag(key, e.newValue)));
            return t;
        }

        VisualElement SliderRow(ParamSpec spec, double now, bool enabled)
        {
            var input = new Slider((float)spec.min, (float)spec.max) { focusable = false };
            input.AddToClassList("range");
            input.showInputField = false;
            input.SetEnabled(enabled);
            var output = El.Text(double.IsNaN(now) ? "auto" : Numbers.NumberToString(now), "sb-slider-value");
            input.SetValueWithoutNotify(double.IsNaN(now) ? (float)spec.min : (float)now);
            input.RegisterValueChangedCallback(e => output.text = Numbers.NumberToString(Snap(e.newValue, spec)));
            void Commit() => Send(TrafficEdits.Global(spec.key, Snap(input.value, spec)));
            input.RegisterCallback<PointerCaptureOutEvent>(_ => Commit());
            input.RegisterCallback<PointerUpEvent>(_ => Commit(), TrickleDown.TrickleDown);
            var head = El.Box("sb-slider-head");
            head.Add(El.Text(spec.label));
            head.Add(output);
            if (enabled && !double.IsNaN(now)) head.Add(El.Btn("auto", "Back to the preset's", () => Send(TrafficEdits.Global(spec.key, double.NaN)), "mission-button"));
            var wrap = El.Box("sb-slider");
            wrap.Add(head);
            wrap.Add(input);
            return wrap;
        }

        static double Snap(double v, ParamSpec spec) => Math.Min(spec.max, Math.Max(spec.min, Numbers.Round((v - spec.min) / spec.step) * spec.step + spec.min));

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        public static string Describe(JunctionControl c) => c switch
        {
            JunctionControl.Stop => "stop signs on the side roads",
            JunctionControl.AllWayStop => "an all-way stop",
            JunctionControl.Priority => "give way to the main road",
            JunctionControl.RightBeforeLeft => "give way to the right",
            JunctionControl.Signal => "traffic signals",
            JunctionControl.Roundabout => "a roundabout",
            JunctionControl.None => "uncontrolled",
            _ => c.ToString(),
        };
    }
}

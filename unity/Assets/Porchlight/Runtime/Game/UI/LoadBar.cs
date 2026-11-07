// The loading bar: one bar from the page's first byte to the first real screen, and again
// whenever a town is being built. It fills by the steps actually done, each weighted by how
// long it took on this device last time (kept in localStorage, which the page reads too, so
// its own bar can hand over at the right place).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine.UIElements;

namespace Porchlight.UI
{
    public sealed class LoadBar
    {
        public const string KEY = "porchlight.load.";
        /// <summary>The first load, from the game starting to its first screen (the page's own steps are "page").</summary>
        public const string FIRST = "first";

        public readonly VisualElement root;
        readonly VisualElement fill;
        readonly Func<double> now;
        readonly Func<string, string> load;
        readonly Action<string, string> save;

        string flow;
        List<KeyValuePair<string, double>> last = new List<KeyValuePair<string, double>>();
        readonly List<KeyValuePair<string, double>> done = new List<KeyValuePair<string, double>>();
        double from, shown, stepAt;

        public LoadBar(Func<double> now, Func<string, string> load, Action<string, string> save)
        {
            this.now = now;
            this.load = load;
            this.save = save;
            root = El.Box("loader");
            var bar = El.Box("loader-bar");
            fill = El.Box("loader-fill");
            bar.Add(fill);
            root.Add(bar);
        }

        public bool Visible => root.style.display != DisplayStyle.None;

        /// <summary>How much of the first load the page took last time: where its bar hands over to this one.</summary>
        public double PageShare()
        {
            double page = Sum(Parse(load(KEY + "page"))), first = Sum(Parse(load(KEY + FIRST)));
            return page + first > 0 ? page / (page + first) : 0.5;
        }

        /// <summary>Show the bar for a load (already showing: carry on with the one under way).</summary>
        public void Begin(string flow, double from = 0)
        {
            if (Visible && this.flow != null) return;
            this.flow = flow;
            this.from = from;
            shown = from;
            last = Parse(load(KEY + flow));
            done.Clear();
            stepAt = now();
            root.style.display = DisplayStyle.Flex;
            Draw();
        }

        /// <summary>A step of it is done (names are unique within a load).</summary>
        public void Step(string name)
        {
            if (flow == null) return;
            double t = now();
            done.Add(new KeyValuePair<string, double>(name, t - stepAt));
            stepAt = t;
        }

        /// <summary>Hide it; `keep`: remember how long the steps took, for the next time.</summary>
        public void End(bool keep = true)
        {
            if (flow != null && keep && done.Count > 0)
            {
                var before = Weights(last);
                save(KEY + flow, Format(done.Select(d => new KeyValuePair<string, double>(d.Key,
                    before.TryGetValue(d.Key, out var was) ? (was + d.Value) / 2 : d.Value))));
            }
            flow = null;
            root.style.display = DisplayStyle.None;
        }

        public void Update()
        {
            if (flow == null) return;
            shown = Math.Max(shown, from + (1 - from) * Fraction(now()));
            Draw();
        }

        double Fraction(double t)
        {
            var expect = Weights(last);
            var finished = new HashSet<string>();
            double at = 0, total = 0;
            foreach (var d in done)
            {
                double w = expect.TryGetValue(d.Key, out var e) ? e : d.Value;
                at += w;
                total += w;
                finished.Add(d.Key);
            }
            string next = null;
            foreach (var l in last)
            {
                if (finished.Contains(l.Key)) continue;
                total += l.Value;
                next ??= l.Key;
            }
            double since = t - stepAt;
            if (next != null)
            {
                // Partway through the next step, by the clock, but never past it.
                double w = Math.Max(1, expect[next]);
                at += w * Math.Min(0.9, since / w);
            }
            else
            {
                // Past anything seen before (or never timed): creep on, without claiming to be done.
                double rest = Math.Max(1500, total * 0.3);
                total += rest;
                at += rest * (1 - Math.Exp(-since / rest));
            }
            return total > 0 ? Math.Min(1, at / total) : 0;
        }

        void Draw() => fill.style.width = Length.Percent((float)(shown * 100));

        static Dictionary<string, double> Weights(List<KeyValuePair<string, double>> steps)
        {
            var w = new Dictionary<string, double>();
            foreach (var s in steps) w[s.Key] = s.Value;
            return w;
        }

        static double Sum(List<KeyValuePair<string, double>> steps) => steps.Sum(s => s.Value);

        /// <summary>"name=ms;name=ms", in the order they happened.</summary>
        public static List<KeyValuePair<string, double>> Parse(string text)
        {
            var steps = new List<KeyValuePair<string, double>>();
            if (string.IsNullOrEmpty(text)) return steps;
            foreach (var part in text.Split(';'))
            {
                int i = part.LastIndexOf('=');
                if (i <= 0) continue;
                if (double.TryParse(part.Substring(i + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0)
                    steps.Add(new KeyValuePair<string, double>(part.Substring(0, i), ms));
            }
            return steps;
        }

        static string Format(IEnumerable<KeyValuePair<string, double>> steps) =>
            string.Join(";", steps.Select(s => s.Key + "=" + Math.Round(s.Value).ToString(CultureInfo.InvariantCulture)));
    }
}

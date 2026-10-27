// The right-hand sidebar, the app's one menu in both modes: Regenerate and
// Explore / Back to map, the seed and the handful of params most people want.
// On the map it adds layers and view/export actions; while exploring, the
// explore controls. Every other tunable lives in the debug panel. A port of
// ui/sidebar.ts (and the explore controls from index.html).

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Model;
using UnityEngine;
using UnityEngine.UIElements;

namespace Porchlight.UI
{
    public sealed class SidebarHandlers
    {
        public Action<GenParams> OnParamsChange;
        public Action OnRegenerate;
        public Action<string> OnSeed;
        public Action OnExplore;
        public Action OnBack;
        public Action OnResetParams;
        public Action<string, bool> OnLayerToggle;
        public Action OnFitView;
        public Action OnExportJson;
        public Action OnExportPng;
        public Action OnToggleDebug;
    }

    /// <summary>The explore-mode buttons (index.html's #explore-controls).</summary>
    public sealed class ExploreControls
    {
        public Button turnLeft, turnRight, tiltDown, tiltUp, closeUp, lookAhead, car, autoWalk, dayNight, walkMode, newChar;
        public VisualElement root;
    }

    public static class El
    {
        public static Button Btn(string text, string tooltip, Action onClick, params string[] classes)
        {
            var b = new Button(onClick) { text = text, tooltip = tooltip, focusable = false };
            b.AddToClassList("btn");
            foreach (var c in classes) b.AddToClassList(c);
            return b;
        }

        public static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        public static VisualElement Box(params string[] classes)
        {
            var v = new VisualElement();
            foreach (var c in classes) v.AddToClassList(c);
            return v;
        }

        /// <summary>
        /// A text field you click into, never one the arrow keys or a gamepad
        /// navigate to (UI Toolkit's navigation would otherwise land on it and
        /// swallow the game's controls); Enter or Escape gives the keys back.
        /// </summary>
        public static TextField ClickToFocus(TextField field)
        {
            field.tabIndex = -1;
            field.Query<VisualElement>().ForEach(e => e.tabIndex = -1);
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.Escape)
                    field.schedule.Execute(() => field.Blur());
            });
            return field;
        }

        /// <summary>A link-style button: muted, underlined text.</summary>
        public static Button Link(string text, Action onClick)
        {
            var b = new Button(onClick) { focusable = false };
            b.AddToClassList("link");
            var l = new Label(text);
            l.AddToClassList("link-text");
            l.pickingMode = PickingMode.Ignore;
            b.Add(l);
            return b;
        }

        /// <summary>A details/summary: a heading that opens and closes its body.</summary>
        public static (VisualElement root, VisualElement body) Details(string title, bool open = false)
        {
            var root = new VisualElement();
            var head = new VisualElement();
            head.AddToClassList("fold-head");
            var arrow = new Label(open ? "▼" : "▶");
            arrow.AddToClassList("fold-arrow");
            var label = new Label(title.ToUpperInvariant());
            label.AddToClassList("bold");
            label.style.fontSize = 11;
            label.style.color = new Color(0xa9 / 255f, 0x9f / 255f, 0x88 / 255f);
            head.Add(arrow);
            head.Add(label);
            var body = new VisualElement();
            body.AddToClassList("fold-body");
            body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            head.RegisterCallback<ClickEvent>(_ =>
            {
                bool now = body.style.display == DisplayStyle.None;
                body.style.display = now ? DisplayStyle.Flex : DisplayStyle.None;
                arrow.text = now ? "▼" : "▶";
            });
            root.Add(head);
            root.Add(body);
            return (root, body);
        }
    }

    public sealed class Sidebar
    {
        sealed class MainSlider
        {
            public string id, label, hint;
            public ParamSpec spec;
            public string[] keys;
            public Func<double, string> format;
        }

        static string Percent(double v) => $"{Js.RoundInt(v * 100)}%";

        static readonly MainSlider[] MAIN_SLIDERS =
        {
            new MainSlider { id = "size", label = "Map size", hint = "Tiles per side", spec = ParamSpec.Get("width"), keys = new[] { "width", "height" }, format = v => $"{Js.NumberToString(v)} × {Js.NumberToString(v)}" },
            new MainSlider { id = "town", label = "Town size", hint = "How far the town spreads", spec = ParamSpec.Get("townRadius"), keys = new[] { "townRadius" }, format = Percent },
            new MainSlider { id = "streets", label = "Street density", hint = "How often streets branch", spec = ParamSpec.Get("roadDensity"), keys = new[] { "roadDensity" }, format = Percent },
            new MainSlider { id = "buildings", label = "Building density", hint = "Share of lots built on", spec = ParamSpec.Get("buildingDensity"), keys = new[] { "buildingDensity" }, format = Percent },
            new MainSlider { id = "water", label = "Water", hint = "Lakes and low ground", spec = ParamSpec.Get("waterFraction"), keys = new[] { "waterFraction" }, format = Percent },
            new MainSlider { id = "rivers", label = "Rivers", hint = "Number of rivers", spec = ParamSpec.Get("riverCount"), keys = new[] { "riverCount" }, format = v => Js.NumberToString(v) },
        };

        public readonly VisualElement root;
        readonly VisualElement body;
        GenParams parameters;
        readonly Dictionary<string, (Slider input, Label output, MainSlider slider)> inputs = new Dictionary<string, (Slider, Label, MainSlider)>();
        readonly Dictionary<string, Toggle> layerBoxes = new Dictionary<string, Toggle>();
        public readonly TextField seedInput;
        readonly Button exploreButton;
        readonly Button debugButton;
        readonly VisualElement mapOnlyLayers, mapOnlyTools, exploreSection;
        bool exploring;
        VisualElement characterRow;
        Label characterName;
        string playingAs;
        readonly Action<bool> setOpen;

        public Sidebar(VisualElement host, GenParams initial, ISet<string> visible, SidebarHandlers h, ExploreControls exploreControls, Action<bool> setOpen)
        {
            this.setOpen = setOpen;
            parameters = initial.Clone();
            root = host;
            root.name = "sidebar";

            seedInput = El.ClickToFocus(new TextField { name = "seed", isDelayed = false });
            seedInput.AddToClassList("mono");
            seedInput.tooltip = "Edit and press Enter to load this seed";
            seedInput.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                string value = seedInput.value.Trim();
                if (value.Length > 0) h.OnSeed?.Invoke(value);
            }, TrickleDown.TrickleDown);
            var seedRow = El.Box("sb-seed");
            seedRow.Add(El.Text("Seed"));
            seedRow.Add(seedInput);

            var sliders = El.Box("sb-sliders");
            foreach (var slider in MAIN_SLIDERS)
            {
                var input = new Slider((float)slider.spec.min, (float)slider.spec.max) { focusable = false };
                input.AddToClassList("range");
                input.showInputField = false;
                var output = El.Text("", "sb-slider-value", "mono");
                var s = slider;
                input.RegisterValueChangedCallback(e =>
                {
                    double stepped = Snap(e.newValue, s.spec);
                    output.text = s.format(stepped);
                });
                input.RegisterCallback<PointerCaptureOutEvent>(_ => Commit(input, s, h));
                input.RegisterCallback<PointerUpEvent>(_ => Commit(input, s, h), TrickleDown.TrickleDown);
                var head = El.Box("sb-slider-head");
                var label = El.Text(slider.label);
                label.tooltip = slider.hint;
                head.Add(label);
                head.Add(output);
                var wrap = El.Box("sb-slider");
                wrap.Add(head);
                wrap.Add(input);
                sliders.Add(wrap);
                inputs[slider.id] = (input, output, slider);
            }

            var regen = El.Btn("Regenerate", "Build a new town from a fresh seed", () => h.OnRegenerate?.Invoke(), "btn-regenerate");
            exploreButton = El.Btn("Explore", "Spawn as a random character and walk the town", () =>
            {
                if (exploring) h.OnBack?.Invoke();
                else h.OnExplore?.Invoke();
            }, "btn-primary");
            var reset = El.Link("Reset to defaults", () => h.OnResetParams?.Invoke());

            var layerList = El.Box("sb-layers");
            foreach (var layer in BlueprintView.LAYERS)
            {
                if (layer.debug) continue;
                var box = new Toggle { text = layer.label, value = visible.Contains(layer.id), focusable = false };
                box.AddToClassList("check");
                string id = layer.id;
                box.RegisterValueChangedCallback(e => h.OnLayerToggle?.Invoke(id, e.newValue));
                layerList.Add(box);
                layerBoxes[layer.id] = box;
            }
            var (layersRoot, layersBody) = El.Details("Layers");
            layersBody.Add(layerList);
            mapOnlyLayers = layersRoot;

            debugButton = El.Btn("Debug panel", "Show every generator setting and debug views (`)", () => h.OnToggleDebug?.Invoke());
            mapOnlyTools = El.Box("sb-tools");
            mapOnlyTools.Add(El.Btn("Fit", "Fit the whole map in view (double-click the map)", () => h.OnFitView?.Invoke()));
            mapOnlyTools.Add(El.Btn("JSON", "Download the TownModel as JSON", () => h.OnExportJson?.Invoke()));
            mapOnlyTools.Add(El.Btn("PNG", "Download the blueprint as a PNG", () => h.OnExportPng?.Invoke()));
            mapOnlyTools.Add(debugButton);

            var collapse = new Button(() => this.setOpen(root.parent.ClassListContains("sidebar-closed"))) { focusable = false, text = "›" };
            collapse.AddToClassList("sb-collapse");
            collapse.tooltip = "Hide or show the sidebar";

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("sb-scroll");
            body = El.Box("sb-body");
            scroll.Add(body);
            var header = new VisualElement();
            header.Add(El.Text("Porchlight", "sb-title", "bold"));
            header.Add(El.Text("Town generator", "sb-subtitle"));
            body.Add(header);
            var actions = El.Box("sb-actions");
            regen.AddToClassList("first");
            actions.Add(regen);
            actions.Add(exploreButton);
            body.Add(actions);
            exploreSection = new VisualElement();
            exploreSection.Add(El.Text("EXPLORE", "section-title", "bold"));
            exploreSection.Add(exploreControls.root);
            body.Add(exploreSection);
            body.Add(seedRow);
            var shape = new VisualElement();
            shape.Add(El.Text("SHAPE", "section-title", "bold"));
            shape.Add(sliders);
            shape.Add(reset);
            body.Add(shape);
            body.Add(mapOnlyLayers);
            body.Add(mapOnlyTools);
            root.Add(scroll);
            root.Add(collapse);
            SetParams(initial);
            SetExploring(false);
            root.RegisterCallback<GeometryChangedEvent>(_ => collapse.text = root.parent != null && root.parent.ClassListContains("sidebar-closed") ? "‹" : "›");
        }

        static double Snap(double v, ParamSpec spec) => Math.Min(spec.max, Math.Max(spec.min, Js.Round((v - spec.min) / spec.step) * spec.step + spec.min));

        double lastCommitted = double.NaN;

        void Commit(Slider input, MainSlider s, SidebarHandlers h)
        {
            double v = Snap(input.value, s.spec);
            input.SetValueWithoutNotify((float)v);
            if (v == parameters[s.keys[0]]) return;
            foreach (var key in s.keys) parameters[key] = v;
            lastCommitted = v;
            h.OnParamsChange?.Invoke(parameters.Clone());
        }

        public void SetParams(GenParams p)
        {
            parameters = p.Clone();
            foreach (var (input, output, slider) in inputs.Values)
            {
                input.SetValueWithoutNotify((float)p[slider.keys[0]]);
                output.text = slider.format(p[slider.keys[0]]);
            }
        }

        public void SetCharacter(string name, Action onSwitch)
        {
            characterRow?.RemoveFromHierarchy();
            var button = El.Link(name != null ? "Switch" : "Play online", onSwitch);
            playingAs = name;
            characterName = El.Text(name != null ? $"Playing as {name}" : "Playing alone", "sb-account-name");
            characterRow = El.Box("sb-account");
            characterRow.Add(characterName);
            characterRow.Add(button);
            body.Add(characterRow);
        }

        public void SetOnline(string state, int here, string why)
        {
            if (characterName == null || playingAs == null) return;
            string detail = state == "joining" ? "joining…" : state == "online" ? (here == 1 ? "just you here" : $"{here} here") : $"offline: {why}";
            characterName.text = $"Playing as {playingAs} · {detail}";
            characterName.tooltip = characterName.text;
        }

        public void SetAccount(string name, string email, Action onSignOut)
        {
            var row = El.Box("sb-account");
            var n = El.Text(name, "sb-account-name");
            n.tooltip = email ?? name;
            row.Add(n);
            row.Add(El.Link("Sign out", onSignOut));
            body.Add(row);
        }

        public void SetSeed(string seed) => seedInput.SetValueWithoutNotify(seed);

        public void SetLoading(bool loading)
        {
            exploreButton.SetEnabled(!loading);
            SyncExploreButton(loading);
        }

        public void SetExploring(bool on)
        {
            exploring = on;
            exploreSection.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            mapOnlyLayers.style.display = on ? DisplayStyle.None : DisplayStyle.Flex;
            mapOnlyTools.style.display = on ? DisplayStyle.None : DisplayStyle.Flex;
            SyncExploreButton(false);
        }

        void SyncExploreButton(bool loading)
        {
            exploreButton.text = loading ? "Loading…" : exploring ? "Back to map" : "Explore";
            exploreButton.tooltip = exploring ? "Back to the blueprint (Esc)" : "Spawn as a random character and walk the town";
        }

        public void SetDebugOpen(bool open) => debugButton.EnableInClassList("active", open);

        /// <summary>The explore-mode controls (index.html), built here so the sidebar can hold them.</summary>
        public static ExploreControls BuildExploreControls()
        {
            var c = new ExploreControls { root = new VisualElement() };
            var grid4 = El.Box("grid4");
            c.turnLeft = El.Btn("⟲ Turn", "Turn the camera round the player (Q)", null);
            c.turnRight = El.Btn("Turn ⟳", "Turn the camera round the player (E)", null);
            c.tiltDown = El.Btn("Tilt ↓", "Tilt the camera down toward the horizon (Z)", null);
            c.tiltUp = El.Btn("Tilt ↑", "Tilt the camera up toward looking straight down (X)", null);
            c.tiltUp.AddToClassList("last");
            foreach (var b in new[] { c.turnLeft, c.turnRight, c.tiltDown, c.tiltUp }) grid4.Add(b);
            c.root.Add(grid4);
            var grid2 = El.Box("grid2");
            c.closeUp = El.Btn("Close up", "Switch to a close-up camera just behind your character (V)", null);
            c.lookAhead = El.Btn("Look ahead", "Look down the street you're walking (F)", null);
            c.car = El.Btn("Get in car", "Get into the nearest car (or ride in another player's), or out again (G or B)", null);
            c.autoWalk = El.Btn("Auto walk", "Wander the sidewalks on your own; arrow keys then turn round or cross the street (T)", null);
            c.dayNight = El.Btn("Night", "Toggle day and night (N)", null);
            c.walkMode = El.Btn("Walk: anywhere", "Walk anywhere, or stay on roads and sidewalks (R)", null);
            c.newChar = El.Btn("New character", "Become someone else (C)", null);
            var all = new[] { c.closeUp, c.lookAhead, c.car, c.autoWalk, c.dayNight, c.walkMode, c.newChar };
            for (int i = 0; i < all.Length; i++)
            {
                if (i % 2 == 0) all[i].AddToClassList("left");
                grid2.Add(all[i]);
            }
            c.root.Add(grid2);
            var (help, helpBody) = El.Details("Controls");
            help.style.marginTop = 6;
            var rows = new (string key, string text)[]
            {
                ("WASD / arrows", "Walk (walk up to a door to go in)"),
                ("Q / E", "Turn the camera"),
                ("Z / X", "Tilt the camera"),
                ("Scroll", "Zoom"),
                ("F", "Look down the street"),
                ("V", "Close up (W/S walk, A/D turn)"),
                ("G / B", "Get in / out of a car (W/S drive, A/D steer), or ride with another player"),
                ("T", "Auto walk or drive with directions; then arrows: back = turn round, towards road = cross"),
                ("N", "Day / night"),
                ("R", "Streets only"),
                ("C", "New character"),
                ("Space", "Jump"),
                ("Shift", "Sprint (in a car: boost)"),
                ("Minimap", "Click to jump there"),
                ("Esc", "Back to the map"),
                ("Controller", "Left stick walk / steer · RT sprint, or at the wheel RT drive and boost, LT brake · right stick turn and tilt · d-pad left / right turn · LB / RB or d-pad up / down zoom · A jump · B car · X auto · Y close up · left stick click day / night · right stick click look ahead · View new character · Menu back to the map"),
            };
            foreach (var (key, text) in rows)
            {
                var row = El.Box("help-row");
                row.Add(El.Text(key, "help-key", "mono"));
                row.Add(El.Text(text, "help-text"));
                helpBody.Add(row);
            }
            c.root.Add(help);
            return c;
        }
    }

    /// <summary>The debug panel (lil-gui): a slider per GenParams value grouped by stage, and the debug views.</summary>
    public sealed class DebugPanel
    {
        public readonly VisualElement root;
        readonly GenParams parameters;
        readonly List<(Slider slider, Label value, ParamSpec spec)> sliders = new List<(Slider, Label, ParamSpec)>();
        public bool IsOpen => root.style.display != DisplayStyle.None;

        public DebugPanel(GenParams initial, ISet<string> visible, Action<GenParams> onParamsChange, Action onReset, Action<string, bool> onLayerToggle)
        {
            parameters = initial.Clone();
            root = El.Box("debug-panel");
            root.Add(El.Text("Debug", "debug-title"));
            var (gen, genBody) = Folder("Generator", true);
            root.Add(gen);
            var groups = new Dictionary<string, VisualElement>();
            foreach (var spec in ParamSpec.All)
            {
                if (!groups.TryGetValue(spec.group, out var folderBody))
                {
                    var (f, b) = Folder(spec.group, spec.group == "Map");
                    genBody.Add(f);
                    groups[spec.group] = folderBody = b;
                }
                var row = El.Box("debug-row");
                row.Add(El.Text(spec.label, "debug-label"));
                var slider = new Slider((float)spec.min, (float)spec.max) { focusable = false };
                slider.AddToClassList("debug-slider");
                var value = El.Text("", "debug-value");
                var s = spec;
                slider.RegisterValueChangedCallback(e =>
                {
                    double v = Math.Min(s.max, Math.Max(s.min, Js.Round((e.newValue - s.min) / s.step) * s.step + s.min));
                    value.text = Js.NumberToString(Math.Round(v, 6));
                });
                void Finish()
                {
                    double v = Math.Min(s.max, Math.Max(s.min, Js.Round((slider.value - s.min) / s.step) * s.step + s.min));
                    if (v == parameters[s.key]) return;
                    parameters[s.key] = v;
                    onParamsChange?.Invoke(parameters.Clone());
                }
                slider.RegisterCallback<PointerCaptureOutEvent>(_ => Finish());
                row.Add(slider);
                row.Add(value);
                folderBody.Add(row);
                sliders.Add((slider, value, spec));
            }
            var reset = new Button(() => onReset?.Invoke()) { text = "Reset to defaults", focusable = false };
            reset.AddToClassList("debug-button");
            genBody.Add(reset);
            var (dbg, dbgBody) = Folder("Debug views", true);
            root.Add(dbg);
            foreach (var layer in BlueprintView.LAYERS)
            {
                if (!layer.debug) continue;
                var t = new Toggle { text = layer.label, value = visible.Contains(layer.id), focusable = false };
                t.AddToClassList("check");
                string id = layer.id;
                t.RegisterValueChangedCallback(e => onLayerToggle?.Invoke(id, e.newValue));
                dbgBody.Add(t);
            }
            SetParams(initial);
            root.style.display = DisplayStyle.None;
        }

        static (VisualElement root, VisualElement body) Folder(string title, bool open)
        {
            var r = new VisualElement();
            var head = El.Text((open ? "▾ " : "▸ ") + title, "debug-folder-head");
            var body = El.Box("debug-folder-body");
            body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            head.RegisterCallback<ClickEvent>(_ =>
            {
                bool now = body.style.display == DisplayStyle.None;
                body.style.display = now ? DisplayStyle.Flex : DisplayStyle.None;
                head.text = (now ? "▾ " : "▸ ") + title;
            });
            r.Add(head);
            r.Add(body);
            return (r, body);
        }

        public void SetOpen(bool open) => root.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;

        public void SetParams(GenParams p)
        {
            foreach (var key in ParamSpec.All) parameters[key.key] = p[key.key];
            foreach (var (slider, value, spec) in sliders)
            {
                slider.SetValueWithoutNotify((float)p[spec.key]);
                value.text = Js.NumberToString(p[spec.key]);
            }
        }
    }
}

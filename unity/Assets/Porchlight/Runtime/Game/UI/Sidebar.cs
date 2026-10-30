// The right-hand sidebar, the app's one menu in both modes. At the top, who
// you're playing as, the seed, and Regenerate and Explore / Back to map. Below,
// four sections, one open at a time: Controls (every controller button and
// key), Players (who else is in the town: tick one to be shown the way to
// them), Missions (from the server; Refresh fetches them again) and Map
// settings (a slider for every generator setting, the handful most people want
// first). The open section's heading stays pinned at the top as you scroll
// through it, so it can always be closed.

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
        public Action OnMissionsRefresh;
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

        static string Percent(double v) => $"{Numbers.RoundInt(v * 100)}%";

        static readonly MainSlider[] MAIN_SLIDERS =
        {
            new MainSlider { id = "size", label = "Map size", hint = "Tiles per side", spec = ParamSpec.Get("width"), keys = new[] { "width", "height" }, format = v => $"{Numbers.NumberToString(v)} × {Numbers.NumberToString(v)}" },
            new MainSlider { id = "town", label = "Town size", hint = "How far the town spreads", spec = ParamSpec.Get("townRadius"), keys = new[] { "townRadius" }, format = Percent },
            new MainSlider { id = "streets", label = "Street density", hint = "How often streets branch", spec = ParamSpec.Get("roadDensity"), keys = new[] { "roadDensity" }, format = Percent },
            new MainSlider { id = "buildings", label = "Building density", hint = "Share of lots built on", spec = ParamSpec.Get("buildingDensity"), keys = new[] { "buildingDensity" }, format = Percent },
            new MainSlider { id = "water", label = "Water", hint = "Lakes and low ground", spec = ParamSpec.Get("waterFraction"), keys = new[] { "waterFraction" }, format = Percent },
            new MainSlider { id = "rivers", label = "Rivers", hint = "Number of rivers", spec = ParamSpec.Get("riverCount"), keys = new[] { "riverCount" }, format = v => Numbers.NumberToString(v) },
        };

        /// <summary>
        /// The controls, each with its controller button and its key (null: none).
        /// Pad icons: A B X Y, LB RB LT RT, LS RS (sticks), DL DR DU DD (d-pad), View, Menu.
        /// </summary>
        static readonly (string what, string[] pad, string[] keys)[] CONTROLS =
        {
            ("Walk, steer", new[] { "LS" }, new[] { "W", "A", "S", "D" }),
            ("Jump", new[] { "A" }, new[] { "Space" }),
            ("Sprint, boost", new[] { "RT" }, new[] { "Shift" }),
            ("Drive, brake", new[] { "RT", "LT" }, new[] { "W", "S" }),
            ("Get in or out", new[] { "B" }, new[] { "G" }),
            ("Day or night", new[] { "Y" }, new[] { "N" }),
            ("Turn the camera", new[] { "RS", "DL", "DR" }, new[] { "Q", "E" }),
            ("Tilt the camera", new[] { "RS" }, new[] { "Z", "X" }),
            ("Zoom", new[] { "DU", "DD" }, new[] { "Scroll" }),
            ("Camera behind you", new[] { "X" }, new[] { "V" }),
            ("Look down the street", null, new[] { "F" }),
            ("Auto walk or drive", null, new[] { "T" }),
            ("Streets only", null, new[] { "R" }),
            ("New character", new[] { "View" }, new[] { "C" }),
            ("Ask someone along, or send them off", new[] { "A" }, new[] { "Space" }),
            ("Choose a character", new[] { "LB" }, null),
            ("The way to a player", new[] { "RB" }, null),
            ("Big map: set a waypoint", new[] { "Menu", "A" }, new[] { "M", "Click" }),
            ("Clear the waypoint (on the big map)", new[] { "Y" }, null),
            ("Back to the town map", new[] { "X" }, new[] { "Esc" }),
        };

        public readonly VisualElement root;
        readonly VisualElement body;
        GenParams parameters;
        readonly Dictionary<string, (Slider input, Label output, MainSlider slider)> inputs = new Dictionary<string, (Slider, Label, MainSlider)>();
        public readonly TextField seedInput;
        readonly Button exploreButton;
        readonly ScrollView scroll;
        /// <summary>The open section's heading, pinned over the top once it's scrolled off (click to close the section).</summary>
        readonly VisualElement pinned;
        readonly Label pinnedLabel;
        readonly VisualElement playersList;
        readonly VisualElement top;
        readonly Label playingName, playingDetail;
        readonly VisualElement links;
        readonly List<(VisualElement body, Label arrow, VisualElement head, string title)> sections = new List<(VisualElement, Label, VisualElement, string)>();
        string playersShown;
        readonly VisualElement missionsList;
        string missionsShown;
        Action<string> onFind;
        bool exploring;
        string accountName, characterName;
        Action onSwitch, onSignOut;
        readonly Action<bool> setOpen;

        public Sidebar(VisualElement host, GenParams initial, SidebarHandlers h, Action<bool> setOpen)
        {
            this.setOpen = setOpen;
            parameters = initial.Clone();
            root = host;
            root.name = "sidebar";

            seedInput = El.ClickToFocus(new TextField { name = "seed", isDelayed = false });
            seedInput.tooltip = "Edit and press Enter to load this seed";
            seedInput.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                string value = seedInput.value.Trim();
                if (value.Length > 0) h.OnSeed?.Invoke(value);
            }, TrickleDown.TrickleDown);

            // The top: who you're playing as, the seed, and the two big buttons.
            top = El.Box("sb-top");
            top.Add(El.Text("Playing as", "sb-head-label", "semibold"));
            playingName = El.Text("", "sb-playing-name", "semibold");
            top.Add(playingName);
            playingDetail = El.Text("", "sb-playing-detail");
            top.Add(playingDetail);
            links = El.Box("sb-links");
            top.Add(links);
            var seedRow = El.Box("sb-seed");
            seedRow.Add(El.Text("Seed", "sb-seed-label"));
            seedRow.Add(seedInput);
            top.Add(seedRow);
            var regen = El.Btn("Regenerate", "Build a new town from a fresh seed", () => h.OnRegenerate?.Invoke(), "btn-regenerate", "press");
            exploreButton = El.Btn("Explore", "Spawn as a random character and walk the town", () =>
            {
                if (exploring) h.OnBack?.Invoke();
                else h.OnExplore?.Invoke();
            }, "btn-primary", "press");
            var actions = El.Box("sb-actions");
            regen.AddToClassList("first");
            actions.Add(regen);
            actions.Add(exploreButton);
            top.Add(actions);

            // Controls: what every button and key does.
            var controls = El.Box("ctl-list");
            foreach (var (what, pad, keys) in CONTROLS)
            {
                var row = El.Box("ctl-row");
                var icons = El.Box("ctl-icons");
                var padIcons = El.Box("ctl-pad");
                foreach (var p in pad ?? new string[0]) padIcons.Add(PadIcon(p));
                icons.Add(padIcons);
                var keyIcons = El.Box("ctl-keys");
                foreach (var k in keys ?? new string[0]) keyIcons.Add(Keycap(k));
                icons.Add(keyIcons);
                row.Add(icons);
                row.Add(El.Text(what, "ctl-text"));
                controls.Add(row);
            }
            controls.Add(El.Text("Click the minimap to jump there, or the boxes beside it to choose your character or car.", "help-text"));

            // Players: everyone else in the town.
            playersList = new VisualElement();

            // Missions: from the server, fetched again with Refresh.
            var missions = new VisualElement();
            var refresh = El.Link("Refresh", () => h.OnMissionsRefresh?.Invoke());
            refresh.tooltip = "Fetch the missions again from the server (new and changed ones need no update to the game)";
            missions.Add(refresh);
            missionsList = new VisualElement();
            missions.Add(missionsList);

            // Map settings: every slider in one list, the ones most people want first.
            var settings = new VisualElement();
            var sliders = El.Box("sb-sliders");
            foreach (var slider in MAIN_SLIDERS) sliders.Add(SliderRow(slider, h));
            var shown = new HashSet<string>(MAIN_SLIDERS.SelectMany(m => m.keys));
            foreach (var spec in ParamSpec.All)
            {
                if (shown.Contains(spec.key)) continue;
                var one = new MainSlider { id = spec.key, label = spec.label, hint = $"{spec.label} ({Numbers.NumberToString(spec.min)} to {Numbers.NumberToString(spec.max)})", spec = spec, keys = new[] { spec.key }, format = FormatFor(spec) };
                sliders.Add(SliderRow(one, h));
            }
            settings.Add(sliders);
            settings.Add(El.Link("Reset to defaults", () => h.OnResetParams?.Invoke()));

            var collapse = new Button(() => this.setOpen(root.parent.ClassListContains("sidebar-closed"))) { focusable = false, text = "›" };
            collapse.AddToClassList("sb-collapse");
            collapse.tooltip = "Hide or show the sidebar";

            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("sb-scroll");
            body = El.Box("sb-body");
            scroll.Add(body);
            body.Add(top);
            body.Add(Section("Controls", controls));
            body.Add(Section("Players", playersList));
            body.Add(Section("Missions", missions));
            body.Add(Section("Map settings", settings));
            root.Add(scroll);
            pinned = El.Box("sb-section-head", "sb-pinned");
            pinnedLabel = El.Text("", "sb-head-label", "semibold");
            pinned.Add(pinnedLabel);
            var pinnedArrow = El.Text("›", "sb-section-arrow");
            pinnedArrow.style.rotate = new Rotate(90);
            pinned.Add(pinnedArrow);
            pinned.style.display = DisplayStyle.None;
            pinned.RegisterCallback<ClickEvent>(_ =>
            {
                int open = OpenIndex();
                Open(-1);
                if (open >= 0) scroll.schedule.Execute(() => scroll.ScrollTo(sections[open].head));
            });
            root.Add(pinned);
            scroll.verticalScroller.valueChanged += _ => Pin();
            scroll.RegisterCallback<GeometryChangedEvent>(_ => Pin());
            root.Add(collapse);
            SetParams(initial);
            SetPlayers(new List<(string, string)>(), null, null);
            ShowDetail("");
            SyncPlaying();
            SetExploring(false);
            root.RegisterCallback<GeometryChangedEvent>(_ => collapse.text = root.parent != null && root.parent.ClassListContains("sidebar-closed") ? "‹" : "›");
        }

        /// <summary>One of the sidebar's sections: a heading that opens it and closes the others.</summary>
        VisualElement Section(string title, VisualElement content)
        {
            var section = El.Box("sb-section");
            var head = El.Box("sb-section-head");
            head.Add(El.Text(title, "sb-head-label", "semibold"));
            var arrow = El.Text("›", "sb-section-arrow");
            head.Add(arrow);
            var sectionBody = El.Box("sb-section-body");
            sectionBody.Add(content);
            sectionBody.style.display = DisplayStyle.None;
            int index = sections.Count;
            sections.Add((sectionBody, arrow, head, title));
            head.RegisterCallback<ClickEvent>(_ => Open(sections[index].body.style.display == DisplayStyle.None ? index : -1));
            section.Add(head);
            section.Add(sectionBody);
            return section;
        }

        /// <summary>Open section `index` (-1: none), closing the rest.</summary>
        void Open(int index)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                bool on = i == index;
                sections[i].body.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
                sections[i].body.parent.EnableInClassList("open", on);
            }
            Pin();
        }

        int OpenIndex() => sections.FindIndex(x => x.body.style.display == DisplayStyle.Flex);

        /// <summary>Pin the open section's heading over the top while its own is scrolled out of sight and its body still shows.</summary>
        void Pin()
        {
            int open = OpenIndex();
            var view = scroll.contentViewport.worldBound;
            bool show = false;
            if (open >= 0)
            {
                var (sectionBody, _, head, title) = sections[open];
                var hb = head.worldBound;
                show = hb.yMax < view.yMin + 1 && sectionBody.worldBound.yMax > view.yMin + hb.height;
                pinnedLabel.text = title;
            }
            pinned.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>A controller button, drawn in its own shape: round face buttons, pill bumpers, triggers, sticks, the d-pad.</summary>
        static VisualElement PadIcon(string name)
        {
            string text = name;
            var e = El.Box("pad");
            switch (name)
            {
                case "A": case "B": case "X": case "Y":
                    e.AddToClassList("pad-face");
                    e.AddToClassList("pad-" + name.ToLowerInvariant());
                    break;
                case "LB": case "RB":
                    e.AddToClassList("pad-bumper");
                    break;
                case "LT": case "RT":
                    e.AddToClassList("pad-trigger");
                    break;
                case "LS": case "RS":
                    e.AddToClassList("pad-stick");
                    text = name.Substring(0, 1);
                    break;
                case "DL": case "DR": case "DU": case "DD":
                    e.AddToClassList("pad-dpad");
                    text = name == "DL" ? "←" : name == "DR" ? "→" : name == "DU" ? "↑" : "↓";
                    break;
                default:
                    // View: two overlapping windows; Menu: three lines.
                    e.AddToClassList("pad-small");
                    e.tooltip = name;
                    if (name == "View")
                    {
                        e.Add(El.Box("pad-view", "pad-view-back"));
                        e.Add(El.Box("pad-view", "pad-view-front"));
                    }
                    else for (int i = 0; i < 3; i++) e.Add(El.Box("pad-menu-line"));
                    return e;
            }
            var label = El.Text(text, "pad-label", "semibold");
            label.pickingMode = PickingMode.Ignore;
            e.Add(label);
            return e;
        }

        /// <summary>A keyboard key: a keycap with a lip along its bottom.</summary>
        static VisualElement Keycap(string name)
        {
            var e = El.Box("keycap");
            if (name.Length > 1) e.AddToClassList("keycap-wide");
            var label = El.Text(name, "keycap-label", "semibold");
            label.pickingMode = PickingMode.Ignore;
            e.Add(label);
            return e;
        }

        VisualElement SliderRow(MainSlider slider, SidebarHandlers h)
        {
            var input = new Slider((float)slider.spec.min, (float)slider.spec.max) { focusable = false };
            input.AddToClassList("range");
            input.showInputField = false;
            var output = El.Text("", "sb-slider-value");
            var s = slider;
            input.RegisterValueChangedCallback(e => output.text = s.format(Snap(e.newValue, s.spec)));
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
            inputs[slider.id] = (input, output, slider);
            return wrap;
        }

        /// <summary>A value as its step shows it: whole numbers, or as many decimals as the step has.</summary>
        static Func<double, string> FormatFor(ParamSpec spec)
        {
            int places = 0;
            for (double step = spec.step; places < 4 && Math.Abs(step - Math.Round(step)) > 1e-9; step *= 10) places++;
            if (spec.min == 0 && spec.max == 1) return Percent;
            return v => Numbers.ToFixed(v, places);
        }

        /// <summary>
        /// Everyone else in the town, a box each: tick one to be shown the way to
        /// them (one at a time; untick to stop). Says so when there's no one.
        /// </summary>
        public void SetPlayers(List<(string key, string name)> players, string finding, Action<string> find)
        {
            onFind = find;
            string shown = (exploring ? "e" : "m") + string.Join("|", players.Select(p => p.key + ":" + p.name)) + "#" + finding;
            if (shown == playersShown) return;
            playersShown = shown;
            playersList.Clear();
            if (players.Count == 0)
            {
                var none = El.Text(exploring
                    ? "No one else is in this town yet. When someone joins your seed, tick them here for the way to them."
                    : "Explore the town to see who else is in it.", "help-text");
                playersList.Add(none);
            }
            foreach (var (key, name) in players)
            {
                // A row of plain elements (a box and a name) rather than a Toggle, so it
                // draws the same everywhere; the click ticks this one and unticks the rest.
                string label = string.IsNullOrEmpty(name) ? "Someone" : name;
                bool on = key == finding;
                string k = key;
                var row = new Button(() => onFind?.Invoke(on ? null : k)) { focusable = false, tooltip = on ? "Stop showing the way" : $"Show the way to {label}" };
                row.AddToClassList("player-row");
                var tick = El.Box("player-tick");
                // In their name tag's colour: filled while you're being shown the way.
                var colour = Porchlight.Rendering.NameTag.ColourFor(key);
                tick.style.borderTopColor = tick.style.borderRightColor = tick.style.borderBottomColor = tick.style.borderLeftColor = colour;
                tick.style.backgroundColor = on ? colour : new Color(0, 0, 0, 0);
                tick.style.borderTopWidth = tick.style.borderRightWidth = tick.style.borderBottomWidth = tick.style.borderLeftWidth = 2;
                row.Add(tick);
                row.Add(El.Text(label, "player-name"));
                playersList.Add(row);
            }
            if (players.Count > 0 || finding != null) Debug.Log($"Porchlight: {players.Count} other player(s) here{(finding != null ? ", finding " + finding : "")}");
        }

        /// <summary>
        /// The missions, each with its summary and Start (or, the one being played,
        /// Abandon); a broken one shows its errors. A status line instead while
        /// they're loading or if the server can't be reached.
        /// </summary>
        public void SetMissions(List<Porchlight.App.MissionInfo> missions, string status, string playing, Action<string> start, Action abandon)
        {
            string shown = $"{status}#{playing}#" + (missions == null ? "" : string.Join("|", missions.Select(m => $"{m.id}:{m.title}:{m.summary}:{m.error}")));
            if (shown == missionsShown) return;
            missionsShown = shown;
            missionsList.Clear();
            if (status != null || missions == null || missions.Count == 0)
            {
                var note = El.Text(status ?? "No missions on the server yet.", "help-text");
                note.style.whiteSpace = WhiteSpace.Normal;
                missionsList.Add(note);
            }
            if (missions == null) return;
            foreach (var m in missions)
            {
                var row = El.Box("mission-row");
                bool on = m.id == playing;
                var title = El.Text(on ? $"{m.title} · playing" : m.title, "mission-title", "semibold");
                row.Add(title);
                if (!string.IsNullOrEmpty(m.summary))
                {
                    var summary = El.Text(m.summary, "help-text", "mission-summary");
                    summary.style.whiteSpace = WhiteSpace.Normal;
                    row.Add(summary);
                }
                if (m.error != null)
                {
                    var error = El.Text($"Can't be played: {m.error}", "mission-error");
                    error.style.whiteSpace = WhiteSpace.Normal;
                    row.Add(error);
                }
                else if (on) row.Add(El.Btn("Abandon", "Stop this mission", () => abandon?.Invoke(), "mission-button"));
                else
                {
                    string id = m.id;
                    row.Add(El.Btn(playing != null ? "Start instead" : "Start", playing != null ? "Abandon the mission you're on and start this one" : "Start this mission", () => start?.Invoke(id), "btn-primary", "mission-button"));
                }
                missionsList.Add(row);
            }
        }

        static double Snap(double v, ParamSpec spec) => Math.Min(spec.max, Math.Max(spec.min, Numbers.Round((v - spec.min) / spec.step) * spec.step + spec.min));

        void Commit(Slider input, MainSlider s, SidebarHandlers h)
        {
            double v = Snap(input.value, s.spec);
            input.SetValueWithoutNotify((float)v);
            if (v == parameters[s.keys[0]]) return;
            foreach (var key in s.keys) parameters[key] = v;
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

        /// <summary>The character you're playing as online (null: playing alone), and how to switch.</summary>
        public void SetCharacter(string name, Action onSwitch)
        {
            characterName = name;
            this.onSwitch = onSwitch;
            ShowDetail(name != null ? "" : "Playing alone");
            SyncPlaying();
        }

        public void SetOnline(string state, int here, string why)
        {
            if (characterName == null) return;
            ShowDetail(state == "joining" ? "Joining…" : state == "online" ? (here == 1 ? "Just you here" : $"{here} here") : $"Offline: {why}");
        }

        /// <summary>The line under your name (hidden when empty).</summary>
        void ShowDetail(string text)
        {
            playingDetail.text = text;
            playingDetail.tooltip = text;
            playingDetail.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        VisualElement joinRows;

        /// <summary>Under your name: "Join <name>" for each of your characters playing somewhere right now (null: none).</summary>
        public void SetJoins(List<(string name, Action join)> playing)
        {
            joinRows?.RemoveFromHierarchy();
            joinRows = null;
            if (playing == null || playing.Count == 0) return;
            joinRows = El.Box("sb-joins");
            foreach (var (name, join) in playing)
                joinRows.Add(El.Btn($"Join {name}", $"Go to {name}'s town, and be shown the way to them", join, "btn-primary", "press"));
            top.Insert(top.IndexOf(links) + 1, joinRows);
        }

        public void SetAccount(string name, string email, Action onSignOut)
        {
            accountName = name;
            this.onSignOut = onSignOut;
            playingName.tooltip = email ?? name;
            SyncPlaying();
        }

        /// <summary>Your character's name (or, until you've picked one, your account's), and the links under it.</summary>
        void SyncPlaying()
        {
            playingName.text = characterName ?? accountName ?? "Guest";
            links.Clear();
            if (onSwitch != null) links.Add(El.Link(characterName != null ? "Switch character" : "Play online", onSwitch));
            if (onSignOut != null) links.Add(El.Link("Sign out", onSignOut));
            links.style.display = links.childCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
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
            if (!on) SetPlayers(new List<(string, string)>(), null, onFind);
            SyncExploreButton(false);
        }

        void SyncExploreButton(bool loading)
        {
            exploreButton.text = loading ? "Loading…" : exploring ? "Back to map" : "Explore";
            exploreButton.tooltip = exploring ? "Back to the blueprint (Esc)" : "Spawn as a random character and walk the town";
        }
    }

    /// <summary>The debug panel: the blueprint's debug views (every generator setting is in the sidebar).</summary>
    public sealed class DebugPanel
    {
        public readonly VisualElement root;
        public bool IsOpen => root.style.display != DisplayStyle.None;

        public DebugPanel(ISet<string> visible, Action<string, bool> onLayerToggle)
        {
            root = El.Box("debug-panel");
            root.Add(El.Text("Debug", "debug-title"));
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
    }
}

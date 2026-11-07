// The screens between signing in and the town: who's playing, what a new
// character looks like, which town they live in, and (on the town's own map)
// which house. The controller works all of them: PadMenu steps through a
// screen's buttons, A presses, B goes back.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using Porchlight.Net;
using Porchlight.Rendering;
using Porchlight.UI;
using UnityEngine;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UIElements.Button;

namespace Porchlight.App
{
    /// <summary>A screen's buttons, for the controller: the stick or d-pad moves between them, A presses, B goes back.</summary>
    public sealed class PadMenu
    {
        readonly List<(VisualElement element, Action press)> items = new List<(VisualElement, Action)>();
        readonly Flick stickX = new Flick(), stickY = new Flick();
        int at, rows;
        /// <summary>How many buttons to a row (a grid moves by rows up and down, and one by one sideways).</summary>
        public int columns = 1;
        public Action back;
        /// <summary>Brings a button into view when the controller reaches it.</summary>
        public Action<VisualElement> reveal;

        public UiButton Add(UiButton button, Action press)
        {
            items.Add((button, press));
            return button;
        }

        /// <summary>A button in the list that's above the form: it arrives later, but goes before the form's.</summary>
        public UiButton AddRow(UiButton button, Action press)
        {
            items.Insert(rows++, (button, press));
            return button;
        }

        public void MoveTo(VisualElement element)
        {
            int i = items.FindIndex(x => x.element == element);
            if (i >= 0) at = i;
        }

        public void Update(PadState s, bool connected)
        {
            for (int i = 0; i < items.Count; i++) items[i].element.EnableInClassList("pad-on", connected && i == at);
            if (!connected) return;
            int across = stickX.Update(s.move.x) + (s.Pressed(Button.Left) ? -1 : 0) + (s.Pressed(Button.Right) ? 1 : 0);
            int down = stickY.Update(s.move.y) + (s.Pressed(Button.Up) ? -1 : 0) + (s.Pressed(Button.Down) ? 1 : 0);
            int step = columns > 1 ? across + down * columns : down;
            if (step != 0 && items.Count > 0)
            {
                at = Math.Max(0, Math.Min(items.Count - 1, at + step));
                reveal?.Invoke(items[at].element);
            }
            // Last: either may close this screen.
            if (s.Pressed(Button.B)) back?.Invoke();
            else if (s.Pressed(Button.A) && at < items.Count && items[at].element.enabledInHierarchy) items[at].press();
        }
    }

    public sealed class FrontDoor
    {
        const string KEY = "porchlight.character";
        static readonly string[] CHARACTER_NAMES = { "Robin", "Sam", "Alex", "Jo", "Charlie", "Frankie", "Pat", "Toby", "Wren", "Nell", "Ollie", "Maya" };
        static readonly string[] TOWN_FIRST = { "Oak", "Willow", "Lantern", "Clover", "Juniper", "Birch", "Thistle", "Honey", "Fern", "Amber" };
        static readonly string[] TOWN_LAST = { "Hollow", "Bend", "Falls", "Green", "Crossing", "Harbour", "Hill", "Springs", "Meadow", "Glen" };

        readonly VisualElement root;
        readonly Platform platform;
        readonly string origin;
        readonly Func<Portraits> portraits;
        readonly Func<string> randomSeed;
        readonly Action signOut;
        VisualElement overlay;
        PadMenu menu;
        /// <summary>What the account has unlocked: which looks a new character can have.</summary>
        HashSet<string> unlocked = new HashSet<string>();

        public FrontDoor(VisualElement root, Platform platform, string origin, Func<Portraits> portraits, Func<string> randomSeed, Action signOut)
        {
            this.signOut = signOut;
            this.root = root;
            this.platform = platform;
            this.origin = origin;
            this.portraits = portraits;
            this.randomSeed = randomSeed;
        }

        public bool Open => overlay != null;

        public void Update(PadState pad, bool connected) => menu?.Update(pad, connected);

        public void Close()
        {
            overlay?.RemoveFromHierarchy();
            overlay = null;
            menu = null;
        }

        // This tab's character, so a reload carries straight on.

        /// <summary>This tab's character from earlier (a reload), if it still exists.</summary>
        public static void Remembered(Platform platform, string origin, Action<Character> done)
        {
            string id = platform.Load(KEY, session: true);
            if (string.IsNullOrEmpty(id))
            {
                done(null);
                return;
            }
            Account.ListCharacters(platform, origin, (list, err) => done(list?.Find(c => c.id == id)));
        }

        public static void Remember(Platform platform, string id) => platform.Save(KEY, id, session: true);

        public static void Forget(Platform platform) => platform.Save(KEY, null, session: true);

        string Pick(string[] from) => from[(int)(new Rng(randomSeed()).Next() * from.Length) % from.Length];

        /// <summary>A fresh card on a fresh overlay, in place of whatever screen was showing.</summary>
        VisualElement Screen(string title, string hint)
        {
            Close();
            overlay = new VisualElement { name = "picker" };
            overlay.AddToClassList("overlay");
            var card = El.Box("card", "picker-card");
            card.Add(El.Text(title, "picker-title", "bold"));
            if (hint != null)
            {
                var h = El.Text(hint, "picker-hint");
                h.style.whiteSpace = WhiteSpace.Normal;
                card.Add(h);
            }
            overlay.Add(card);
            root.Add(overlay);
            menu = new PadMenu();
            return card;
        }

        static Label ErrorLabel(string message)
        {
            var error = El.Text(message ?? "", "picker-error");
            error.style.whiteSpace = WhiteSpace.Normal;
            error.EnableInClassList("hidden", string.IsNullOrEmpty(message));
            return error;
        }

        static void Say(Label label, string message)
        {
            label.text = message ?? "";
            label.EnableInClassList("hidden", string.IsNullOrEmpty(message));
        }

        ScrollView ListIn(VisualElement card, PadMenu forMenu)
        {
            var list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("picker-list");
            forMenu.reveal = e => list.ScrollTo(e);
            card.Add(list);
            return list;
        }

        /// <summary>A name to type (or be given) and a button: a character's or a town's.</summary>
        (TextField name, UiButton go) NameForm(VisualElement card, PadMenu forMenu, string placeholder, string go, int maxLength, Func<string> surprise, Action submit)
        {
            var name = El.ClickToFocus(new TextField { maxLength = maxLength, textEdition = { placeholder = placeholder } });
            name.AddToClassList("picker-name");
            name.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) submit();
            }, TrickleDown.TrickleDown);
            var create = forMenu.Add(El.Btn(go, null, submit, "picker-create", "semibold", "press"), submit);
            var form = El.Box("picker-new");
            form.Add(name);
            form.Add(create);
            card.Add(form);
            void Suggest() => name.value = surprise();
            card.Add(forMenu.Add(El.Btn("Suggest a name", "Pick a name for me", Suggest, "picker-suggest"), Suggest));
            return (name, create);
        }

        // Who's playing?

        /// <summary>The account's characters, and making a new one. play: this one, please. move: this one's moving home.</summary>
        public void Characters(Action<Character> play, Action<Character> move, string message = null)
        {
            var card = Screen("Who’s playing?", "Everyone on this sign-in can have their own character, and play at the same time on another tab or device. Missions and rewards are shared by all of them.");
            var thisMenu = menu;
            var earned = El.Text("", "picker-earned");
            card.Add(earned);
            var list = ListIn(card, thisMenu);
            list.Add(El.Text("Loading…", "picker-empty"));
            var error = ErrorLabel(message);

            void Submit(TextField field)
            {
                if (string.IsNullOrWhiteSpace(field.value))
                {
                    field.value = Pick(CHARACTER_NAMES);
                    return;
                }
                ChooseLook(field.value.Trim(), play, () => Characters(play, move));
            }

            TextField name = null;
            (name, _) = NameForm(card, thisMenu, "New character’s name", "Next", 20, () => Pick(CHARACTER_NAMES), () => Submit(name));
            card.Add(error);
            var leave = El.Link("Sign out", signOut);
            leave.style.marginTop = 10;
            card.Add(leave);

            Account.FetchProgress(platform, origin, (p, _) =>
            {
                if (p == null) return;
                unlocked = new HashSet<string>(p.unlocks.Where(u => u.at != null).Select(u => u.id));
                int done = p.missions.Count(m => m.times > 0);
                earned.text = $"{done} of {p.missions.Count} missions finished · {unlocked.Count} of {p.unlocks.Count} rewards unlocked";
            });
            Account.ListCharacters(platform, origin, (characters, err) =>
            {
                if (overlay == null || menu != thisMenu) return;
                list.Clear();
                if (characters == null)
                {
                    Say(error, err);
                    return;
                }
                if (characters.Count == 0) list.Add(El.Text("No characters yet. Make one below.", "picker-empty"));
                foreach (var c in characters) list.Add(CharacterRow(c, thisMenu, play, move, error));
                if (characters.Count > 0) thisMenu.MoveTo(list.Q<UiButton>(className: "picker-play"));
            });
        }

        VisualElement CharacterRow(Character c, PadMenu forMenu, Action<Character> play, Action<Character> move, Label error)
        {
            var row = El.Box("picker-row");
            var face = El.Box("picker-face");
            if (c.look != null && Catalog.IsLook(c.look)) face.style.backgroundImage = new StyleBackground(portraits().LookStills(new[] { c.look })[0]);
            row.Add(face);
            var middle = El.Box("picker-middle");
            void Play() => play(c);
            var button = forMenu.AddRow(El.Btn(c.name, null, Play, "picker-play", "semibold", "press"), Play);
            middle.Add(button);
            string where = c.home == null ? "Hasn’t chosen a town yet" : c.home.hasHouse ? $"Lives in {c.home.townName}" : $"In {c.home.townName}, choosing a house";
            middle.Add(El.Text(where, "picker-detail"));
            row.Add(middle);
            void Move() => move(c);
            row.Add(forMenu.AddRow(El.Btn("Move", $"Move {c.name} to another town or house", Move, "picker-move"), Move));
            row.Add(El.Btn("×", $"Delete {c.name}", () =>
            {
                if (!platform.Confirm($"Delete {c.name}? Where they live, where they were and how they look will be gone. Missions and rewards stay with the account.")) return;
                Account.DeleteCharacter(platform, origin, c.id, err =>
                {
                    if (err != null) Say(error, err);
                    else
                    {
                        row.RemoveFromHierarchy();
                        // Past this row's buttons, or the controller would press a character that's gone.
                        Characters(play, move);
                    }
                });
            }, "picker-delete"));
            return row;
        }

        // What a new character looks like.

        void ChooseLook(string name, Action<Character> made, Action back)
        {
            var card = Screen($"What does {name} look like?", "You can change this whenever you like in the town.");
            var thisMenu = menu;
            thisMenu.back = back;
            var looks = Catalog.CHARACTERS
                .Concat(unlocked.Contains("firefighter") ? new[] { Catalog.FIREFIGHTER } : new string[0])
                .Concat(unlocked.Contains("animals") ? Catalog.ANIMALS : new string[0]).ToList();
            var stills = portraits().LookStills(looks);
            var list = ListIn(card, thisMenu);
            var grid = El.Box("look-grid");
            list.Add(grid);
            thisMenu.columns = 5;
            var error = ErrorLabel(null);
            bool making = false;
            for (int i = 0; i < looks.Count; i++)
            {
                string look = looks[i];
                void Choose()
                {
                    if (making) return;
                    making = true;
                    Account.CreateCharacter(platform, origin, name, look, (c, err) =>
                    {
                        making = false;
                        if (c != null) made(c);
                        else Say(error, err);
                    });
                }
                var tile = thisMenu.Add(El.Btn("", null, Choose, "look-tile", "press"), Choose);
                tile.style.backgroundImage = new StyleBackground(stills[i]);
                grid.Add(tile);
            }
            card.Add(error);
            card.Add(El.Link("Back", back));
        }

        // Which town?

        /// <summary>Every town, to live in; or making one, up to the limit (past it, delete one first).</summary>
        public void Towns(Character c, Action<Character> chosen, Action back, string message = null)
        {
            var card = Screen($"Where does {c.name} live?", "Anyone can live in any town, and visit the others. Each town has room for everyone, and a house has room for two.");
            var thisMenu = menu;
            thisMenu.back = back;
            var list = ListIn(card, thisMenu);
            list.Add(El.Text("Loading…", "picker-empty"));
            var error = ErrorLabel(message);
            var limit = El.Text("", "picker-hint");
            limit.style.whiteSpace = WhiteSpace.Normal;

            void Reload() => Towns(c, chosen, back);

            void MoveIn(string town) => Account.MoveToTown(platform, origin, c.id, town, (moved, err) =>
            {
                if (moved != null) chosen(moved);
                else Say(error, err);
            });

            TextField name = null;
            UiButton make = null;
            void Submit()
            {
                if (string.IsNullOrWhiteSpace(name.value))
                {
                    name.value = Pick(TOWN_FIRST) + " " + Pick(TOWN_LAST);
                    return;
                }
                make.SetEnabled(false);
                Account.CreateTown(platform, origin, name.value.Trim(), (town, err) =>
                {
                    if (town != null) MoveIn(town.id);
                    else
                    {
                        make.SetEnabled(true);
                        Say(error, err);
                    }
                });
            }
            card.Add(limit);
            (name, make) = NameForm(card, thisMenu, "A new town’s name", "Make", 24, () => Pick(TOWN_FIRST) + " " + Pick(TOWN_LAST), Submit);
            card.Add(error);
            card.Add(El.Link("Back", back));

            Account.ListTowns(platform, origin, (towns, err) =>
            {
                if (overlay == null || menu != thisMenu) return;
                list.Clear();
                if (towns == null)
                {
                    Say(error, err);
                    return;
                }
                bool full = towns.made >= towns.max;
                limit.text = full ? $"You’ve made {towns.made} of {towns.max} towns. Delete one to make another." : $"You’ve made {towns.made} of {towns.max} towns.";
                make.SetEnabled(!full);
                foreach (var t in towns.towns) list.Add(TownRow(t, c, thisMenu, MoveIn, Reload, error));
                thisMenu.MoveTo(list.Q<UiButton>(className: "picker-play"));
            });
        }

        VisualElement TownRow(Town t, Character c, PadMenu forMenu, Action<string> moveIn, Action reload, Label error)
        {
            var row = El.Box("picker-row");
            var middle = El.Box("picker-middle");
            void Choose() => moveIn(t.id);
            middle.Add(forMenu.AddRow(El.Btn(t.name, null, Choose, "picker-play", "semibold", "press"), Choose));
            var about = new List<string> { t.residents == 1 ? "1 lives here" : $"{t.residents} live here" };
            if (t.playing > 0) about.Add($"{t.playing} playing now");
            if (t.mine) about.Add("yours");
            if (c.home?.townId == t.id) about.Add($"{c.name}’s home");
            middle.Add(El.Text(string.Join(" · ", about), "picker-detail"));
            row.Add(middle);
            if (t.mine)
                row.Add(El.Btn("×", $"Delete {t.name}", () =>
                {
                    if (!platform.Confirm($"Delete {t.name}? Everyone who lives there will have to choose a new town. Missions and rewards aren’t lost.")) return;
                    Account.DeleteTown(platform, origin, t.id, err =>
                    {
                        if (err != null) Say(error, err);
                        else reload();
                    });
                }, "picker-delete"));
            return row;
        }
    }

    /// <summary>
    /// Choosing a house on the town's own map: free ones in green (and your own
    /// with a room spare), taken ones in red. Click one, or with the controller, LB
    /// and RB step through the free ones and A moves in.
    /// </summary>
    public sealed class HousePicker
    {
        static readonly Color FREE = new Color(0.27f, 0.72f, 0.36f, 0.75f);
        static readonly Color TAKEN = new Color(0.78f, 0.25f, 0.2f, 0.75f);
        static readonly Color CHOSEN = new Color(1f, 0.8f, 0.15f, 0.95f);

        readonly BlueprintView map;
        readonly List<Building> houses;
        readonly Dictionary<int, HomeRow> lived = new Dictionary<int, HomeRow>();
        readonly List<Building> free = new List<Building>();
        readonly VisualElement panel;
        readonly Label status, error;
        readonly UiButton moveIn;
        readonly PadMenu menu = new PadMenu();
        readonly Func<double> random;
        Building chosen;

        public HousePicker(VisualElement root, BlueprintView map, TownModel model, string who, string townName, List<HomeRow> homes, string note, Func<double> random, Action<int, int> move, Action back)
        {
            this.map = map;
            this.random = random;
            houses = Homes.All(model);
            foreach (var home in homes)
                if (Homes.At(houses, home.x, home.y) is Building b) lived[b.id] = home;
            foreach (var b in houses)
                if (Choosable(b)) free.Add(b);

            panel = El.Box("card", "house-panel");
            panel.Add(El.Text($"Choose {who}’s house in {townName}", "picker-title", "bold"));
            if (note != null)
            {
                var n = El.Text(note, "picker-error");
                n.style.whiteSpace = WhiteSpace.Normal;
                panel.Add(n);
            }
            var hint = El.Text("Click a green house. Drag to move the map, scroll to zoom.", "picker-hint");
            hint.style.whiteSpace = WhiteSpace.Normal;
            panel.Add(hint);
            status = El.Text($"{free.Count} free", "house-status");
            status.style.whiteSpace = WhiteSpace.Normal;
            panel.Add(status);
            void MoveIn()
            {
                if (chosen == null) return;
                var at = Homes.Anchor(chosen);
                moveIn.SetEnabled(false);
                move(at.x, at.y);
            }
            void Surprise()
            {
                if (free.Count > 0) Choose(free[(int)(random() * free.Count) % free.Count]);
                menu.MoveTo(moveIn);
            }
            moveIn = menu.Add(El.Btn("Move in", null, MoveIn, "picker-play", "semibold", "press"), MoveIn);
            moveIn.SetEnabled(false);
            panel.Add(moveIn);
            var surprise = menu.Add(El.Btn("Pick one for me", null, Surprise, "picker-create", "press"), Surprise);
            surprise.style.marginTop = 8;
            panel.Add(surprise);
            menu.MoveTo(surprise);
            menu.back = back;
            error = El.Text("", "picker-error", "hidden");
            error.style.whiteSpace = WhiteSpace.Normal;
            panel.Add(error);
            var backLink = El.Link("Back to towns", back);
            backLink.style.marginTop = 10;
            panel.Add(backLink);
            root.Add(panel);
            Paint();
        }

        bool Choosable(Building b) => !lived.TryGetValue(b.id, out var home) || (home.mine && home.residents.Count < Homes.MAX_RESIDENTS);

        string Describe(Building b)
        {
            if (!lived.TryGetValue(b.id, out var home)) return "Free";
            string names = string.Join(" and ", home.residents);
            string they = home.residents.Count == 1 ? "lives" : "live";
            if (!home.mine) return $"{names} {they} here";
            return Choosable(b) ? $"{names} {they} here, with room for one more" : $"{names} {they} here, and it’s full";
        }

        void Paint()
        {
            map.highlights.Clear();
            foreach (var b in houses)
            {
                map.highlights.Add((b.footprint, b == chosen ? CHOSEN : Choosable(b) ? FREE : TAKEN));
            }
            map.Redraw();
        }

        void Choose(Building b)
        {
            chosen = Choosable(b) ? b : null;
            status.text = Describe(b);
            moveIn.SetEnabled(chosen != null);
            error.AddToClassList("hidden");
            Paint();
        }

        static (int x, int y) Tile(Vec2 world) => ((int)Math.Floor(world.x), (int)Math.Floor(world.y));

        /// <summary>The map was clicked here.</summary>
        public void Click(Vec2 world)
        {
            var (x, y) = Tile(world);
            if (Homes.At(houses, x, y) is Building b) Choose(b);
        }

        /// <summary>The pointer's over here: who lives there, until something's chosen.</summary>
        public void Hover(Vec2? world)
        {
            if (chosen != null) return;
            if (world == null)
            {
                status.text = $"{free.Count} free";
                return;
            }
            var (x, y) = Tile(world.Value);
            status.text = Homes.At(houses, x, y) is Building b ? Describe(b) : $"{free.Count} free";
        }

        public void Update(PadState pad, bool connected)
        {
            menu.Update(pad, connected);
            if (!connected || free.Count == 0) return;
            int step = (pad.Pressed(Button.RB) ? 1 : 0) - (pad.Pressed(Button.LB) ? 1 : 0);
            if (step == 0) return;
            int at = chosen != null ? free.IndexOf(chosen) : -1;
            Choose(free[(at + step + free.Count) % free.Count]);
            menu.MoveTo(moveIn);
        }

        /// <summary>Moving in didn't work (someone got there first): say so, and they can choose again.</summary>
        public void Refused(string why)
        {
            error.text = why ?? "";
            error.RemoveFromClassList("hidden");
            moveIn.SetEnabled(chosen != null);
        }

        public void Close()
        {
            panel.RemoveFromHierarchy();
            map.highlights.Clear();
            map.Redraw();
        }
    }

    /// <summary>The Journal: the missions and what they earn, for the whole account.</summary>
    public sealed class Journal
    {
        readonly VisualElement overlay;
        readonly ScrollView list;
        readonly Flick stick = new Flick();
        readonly Action closed;

        public Journal(VisualElement root, Platform platform, string origin, Action closed)
        {
            this.closed = closed;
            overlay = new VisualElement { name = "journal" };
            overlay.AddToClassList("overlay");
            var card = El.Box("card", "journal-card");
            card.Add(El.Text("Journal", "picker-title", "bold"));
            var hint = El.Text("Everyone on this sign-in shares these, in every town. Play a mission as often as you like; its reward is yours once.", "picker-hint");
            hint.style.whiteSpace = WhiteSpace.Normal;
            card.Add(hint);
            list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("journal-list");
            list.Add(El.Text("Loading…", "picker-empty"));
            card.Add(list);
            card.Add(El.Link("Close", Close));
            overlay.Add(card);
            root.Add(overlay);
            Account.FetchProgress(platform, origin, (p, err) =>
            {
                list.Clear();
                if (p == null) list.Add(Wrapped(err, "picker-error"));
                else Show(p);
            });
        }

        static Label Wrapped(string text, params string[] classes)
        {
            var l = El.Text(text, classes);
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        static string Day(string iso) =>
            DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                ? at.LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
                : "";

        void Show(Progress p)
        {
            list.Add(El.Text($"Missions: {p.missions.Count(m => m.times > 0)} of {p.missions.Count} finished", "journal-head", "semibold"));
            foreach (var m in p.missions)
            {
                var row = El.Box("journal-row");
                row.Add(El.Text(m.title, "mission-title", "semibold"));
                if (!string.IsNullOrEmpty(m.summary)) row.Add(Wrapped(m.summary, "help-text"));
                string times = m.times == 1 ? "once" : $"{m.times} times";
                string by = m.firstCharacter != null ? $", first by {m.firstCharacter}" : "";
                row.Add(Wrapped(m.times > 0 ? $"Finished {times}{by} on {Day(m.firstAt)}" : "Not finished yet", m.times > 0 ? "journal-done" : "journal-todo"));
                var reward = p.unlocks.Find(u => u.id == m.unlocks);
                if (reward != null) row.Add(Wrapped($"Reward: {reward.name}", "journal-reward"));
                list.Add(row);
            }
            list.Add(El.Text($"Rewards: {p.unlocks.Count(u => u.at != null)} of {p.unlocks.Count} unlocked", "journal-head", "semibold"));
            foreach (var u in p.unlocks)
            {
                var row = El.Box("journal-row");
                row.Add(El.Text(u.name, "mission-title", "semibold"));
                string how = u.mission != null ? $"Finish {u.mission} to unlock this" : "Locked";
                row.Add(Wrapped(u.at != null ? $"Unlocked on {Day(u.at)}" : how, u.at != null ? "journal-done" : "journal-todo"));
                list.Add(row);
            }
        }

        public void Update(PadState pad)
        {
            if (pad.Pressed(Button.B))
            {
                Close();
                return;
            }
            int step = stick.Update(pad.move.y) + (pad.Pressed(Button.Up) ? -1 : 0) + (pad.Pressed(Button.Down) ? 1 : 0);
            if (step != 0) list.scrollOffset += new Vector2(0, step * 90);
        }

        public void Close()
        {
            overlay.RemoveFromHierarchy();
            closed?.Invoke();
        }
    }
}

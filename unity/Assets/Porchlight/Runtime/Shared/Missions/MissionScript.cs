// A mission as it plays: an ink story (compiled by the server from
// server/missions, see the README there) run a line at a time, going on only
// once you've read each one (so what the story does after a line happens
// after you've read it). What it says is shown a line at a time (a line "sender: Hello" is spoken by whoever plays
// the sender, "you: …" by you); its ordinary choices are yours to pick; and a
// choice whose text starts with @ is a wait, picked by itself once the town
// says it's met (@talk sender: you've spoken to them, @park drop: your car is
// stopped in their spot, @wait 3: three seconds on). The story changes the
// town through its EXTERNAL functions, which the world (IMissionWorld) carries
// out. Pure: the game supplies the world.

using System;
using System.Collections.Generic;
using System.Globalization;
using Ink.Runtime;

namespace Porchlight.Missions
{
    /// <summary>What a mission can do to the town, and ask of it.</summary>
    public interface IMissionWorld
    {
        /// <summary>The line telling you what to do next ("" or null: none).</summary>
        void Objective(string text);
        /// <summary>
        /// Choose a house for a role: one whose front door is min..max tiles from the
        /// player ("" near) or from another role's house. Returns its street's name.
        /// </summary>
        string House(string role, string near, double min, double max);
        /// <summary>Someone waiting on the pavement outside the role's house. Returns their name.</summary>
        string Npc(string role);
        /// <summary>Directions to the role (their spot once it's marked, else them, else their door); "" stops.</summary>
        void Guide(string role);
        /// <summary>Mark the spot to park in outside the role's house.</summary>
        void Parking(string role);
        /// <summary>The role's person carries this many boxes from their house to your car.</summary>
        void Load(string role, int boxes);
        /// <summary>The role's person carries every box in your car into their house.</summary>
        void Unload(string role);
        /// <summary>The role's person goes indoors, and their spot is cleared.</summary>
        void Dismiss(string role);
        /// <summary>Is this wait met yet? talk, park, loaded or unloaded, for a role.</summary>
        bool Met(string wait, string role);
        /// <summary>Who plays the role, for their lines (null: no such role).</summary>
        string NameOf(string role);
    }

    public sealed class MissionLine
    {
        /// <summary>Who says it ("You" for you), or null for the story's own voice.</summary>
        public string speaker;
        public string text;
    }

    public struct MissionWait
    {
        /// <summary>talk, park, loaded, unloaded or wait.</summary>
        public string kind;
        public string role;
        /// <summary>For @wait: how many seconds.</summary>
        public double seconds;
    }

    public sealed class MissionScript
    {
        readonly Story story;
        readonly IMissionWorld world;
        MissionLine line;
        readonly List<string> choices = new List<string>();
        readonly List<int> choiceIndex = new List<int>();
        readonly List<MissionWait> waits = new List<MissionWait>();
        readonly List<int> waitIndex = new List<int>();
        double waited;

        /// <summary>The line being shown (null: none, so the choices or waits are up).</summary>
        public MissionLine Line => line;

        /// <summary>The choices to offer you, once the lines are read.</summary>
        public IReadOnlyList<string> Choices => line != null ? Array.Empty<string>() : (IReadOnlyList<string>)choices;

        /// <summary>What the story is waiting for, once the lines are read.</summary>
        public IReadOnlyList<MissionWait> Waits => line != null ? Array.Empty<MissionWait>() : (IReadOnlyList<MissionWait>)waits;

        /// <summary>The story has run to its end (or stopped on an error).</summary>
        public bool Done { get; private set; }

        /// <summary>What went wrong, if the story stopped on an error.</summary>
        public string Error { get; private set; }

        public MissionScript(string json, IMissionWorld world)
        {
            this.world = world;
            story = new Story(json);
            story.allowExternalFunctionFallbacks = true;
            story.onError += (message, type) =>
            {
                if (type == Ink.ErrorType.Error) Fail(message);
            };
            Bind("objective", a => { world.Objective(Str(a, 0)); return null; });
            Bind("house", a => world.House(Str(a, 0), Str(a, 1), Num(a, 2), Num(a, 3)) ?? "");
            Bind("npc", a => world.Npc(Str(a, 0)) ?? "");
            Bind("guide", a => { world.Guide(Str(a, 0)); return null; });
            Bind("parking", a => { world.Parking(Str(a, 0)); return null; });
            Bind("load", a => { world.Load(Str(a, 0), (int)Math.Round(Num(a, 1))); return null; });
            Bind("unload", a => { world.Unload(Str(a, 0)); return null; });
            Bind("dismiss", a => { world.Dismiss(Str(a, 0)); return null; });
            Pump();
        }

        /// <summary>
        /// The town's functions, each called as the story reaches it (never ahead of
        /// time while ink looks past a line for glue: they change the town).
        /// </summary>
        void Bind(string name, Func<object[], object> f) =>
            story.BindExternalFunctionGeneral(name, args =>
            {
                try
                {
                    return f(args);
                }
                catch (Exception e)
                {
                    Fail($"{name}: {e.Message}");
                    return null;
                }
            }, lookaheadSafe: false);

        static string Str(object[] a, int i) => i < a.Length && a[i] != null ? Convert.ToString(a[i], CultureInfo.InvariantCulture) : "";

        static double Num(object[] a, int i)
        {
            if (i >= a.Length || a[i] == null) return 0;
            try
            {
                return Convert.ToDouble(a[i], CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return 0;
            }
        }

        void Fail(string message)
        {
            if (Error != null) return;
            Error = message;
            Done = true;
            line = null;
            choices.Clear();
            waits.Clear();
        }

        /// <summary>Run the story on to the next thing it says, or else its next choice (or end).</summary>
        void Pump()
        {
            choices.Clear();
            choiceIndex.Clear();
            waits.Clear();
            waitIndex.Clear();
            waited = 0;
            try
            {
                while (!Done && story.canContinue)
                {
                    string text = story.Continue().Trim();
                    if (text.Length == 0) continue;
                    line = Parse(text);
                    return;
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
            if (Done) return;
            foreach (var c in story.currentChoices)
            {
                string text = c.text.Trim();
                if (text.StartsWith("@"))
                {
                    waits.Add(ParseWait(text));
                    waitIndex.Add(c.index);
                }
                else
                {
                    choices.Add(text);
                    choiceIndex.Add(c.index);
                }
            }
            if (choices.Count == 0 && waits.Count == 0) Done = true;
        }

        /// <summary>"sender: Hello" is the sender's line (and "you: …" yours); anything else is the story's own.</summary>
        MissionLine Parse(string text)
        {
            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                string who = text.Substring(0, colon).Trim();
                string said = text.Substring(colon + 1).Trim();
                if (who.Equals("you", StringComparison.OrdinalIgnoreCase)) return new MissionLine { speaker = "You", text = said };
                string name = who.IndexOf(' ') < 0 ? world.NameOf(who) : null;
                if (name != null) return new MissionLine { speaker = name, text = said };
            }
            return new MissionLine { text = text };
        }

        public static MissionWait ParseWait(string text)
        {
            var parts = text.Substring(1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var w = new MissionWait { kind = parts.Length > 0 ? parts[0].ToLowerInvariant() : "", role = parts.Length > 1 ? parts[1] : "" };
            if (w.kind == "wait" && parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                w.seconds = s;
                w.role = "";
            }
            return w;
        }

        /// <summary>On past the line being shown.</summary>
        public void Advance()
        {
            if (line == null) return;
            line = null;
            Pump();
        }

        /// <summary>Pick one of Choices.</summary>
        public void Choose(int i)
        {
            if (Done || line != null || i < 0 || i >= choices.Count) return;
            Take(choiceIndex[i]);
        }

        void Take(int index)
        {
            try
            {
                story.ChooseChoiceIndex(index);
            }
            catch (Exception e)
            {
                Fail(e.Message);
                return;
            }
            Pump();
        }

        /// <summary>Time passes: take the first wait the town says is met.</summary>
        public void Update(double dt)
        {
            if (Done || line != null) return;
            waited += dt;
            for (int i = 0; i < waits.Count; i++)
            {
                var w = waits[i];
                bool met = w.kind == "wait" ? waited >= w.seconds : world.Met(w.kind, w.role);
                if (!met) continue;
                Take(waitIndex[i]);
                return;
            }
        }
    }
}

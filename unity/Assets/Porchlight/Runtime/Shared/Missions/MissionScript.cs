// A mission as it plays: an ink story (compiled by the server from
// server/missions, see the README there) run a line at a time, going on only
// once you've read each one (so what the story does after a line happens
// after you've read it). What it says is shown a line at a time (a line "sender: Hello" is spoken by whoever plays
// the sender, "you: …" by you); its ordinary choices are yours to pick; and a
// choice whose text starts with @ is a wait, picked by itself once the town
// says it's met (@talk sender: you've spoken to them, @parked drop: your car
// is stopped in their spot, @idle sender: their errands are done, @wait 3:
// three seconds on). The story changes the town through its EXTERNAL
// functions, generic building blocks the world (IMissionWorld) carries out:
// what a mission is about lives in its story, not here. Pure: the game
// supplies the world.

using System;
using System.Collections.Generic;
using System.Globalization;
using Ink.Runtime;

namespace Porchlight.Missions
{
    /// <summary>
    /// What a mission can do to the town, and ask of it: small building blocks
    /// (a house for a role, someone at it, their errands one step at a time, a
    /// spot to park in, the way there), which the story puts together into
    /// whatever the mission is. Nothing here knows what any mission is about.
    /// </summary>
    public interface IMissionWorld
    {
        /// <summary>The line telling you what to do next ("" or null: none).</summary>
        void Objective(string text);
        /// <summary>A prompt to show while its condition holds (always, no_car, near or near_driving a role).</summary>
        void Hint(string when, string role, string text);
        /// <summary>Every hint goes.</summary>
        void ClearHints();
        /// <summary>Tune how the town plays the mission (talk_reach, park_reach, walk_speed and so on).</summary>
        void Setting(string name, double value);
        /// <summary>
        /// Choose a building for a role: of the kind asked ("" any), its front door
        /// min..max tiles from the player ("" near) or from another role's, with room
        /// to park outside if asked. Returns its street's name.
        /// </summary>
        string Place(string role, string kind, string near, double min, double max, bool parking);
        /// <summary>Someone on the pavement outside the role's building, called name, looking like look ("" any). Returns their name.</summary>
        string Spawn(string role, string name, string look);
        /// <summary>Queue: the role's person walks to inside, door, kerb or car.</summary>
        void Walk(string role, string to);
        /// <summary>Queue: the role's person waits this long where they are.</summary>
        void Pause(string role, double seconds);
        /// <summary>Queue: the role's person has a thing (a model, size tiles across) in their arms.</summary>
        void Hold(string role, string model, double size);
        /// <summary>Queue: what the role's person holds goes onto your car.</summary>
        void PutInCar(string role);
        /// <summary>Queue: the role's person takes the last thing put on your car into their arms.</summary>
        void TakeFromCar(string role);
        /// <summary>Queue: what the role's person holds is put down, and gone.</summary>
        void PutDown(string role);
        /// <summary>The role's person drops what they're doing, goes indoors and is gone.</summary>
        void Leave(string role);
        /// <summary>Mark the spot to park in outside the role's building.</summary>
        void MarkSpot(string role);
        /// <summary>The role's marked spot goes.</summary>
        void ClearSpot(string role);
        /// <summary>Directions to the role's spot, person or house, called label; "" role stops.</summary>
        void Guide(string role, string to, string label);
        /// <summary>Is this wait met yet? talk, parked or idle, for a role.</summary>
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
        /// <summary>talk, parked, idle or wait.</summary>
        public string kind;
        public string role;
        /// <summary>For @wait: how many seconds.</summary>
        public double seconds;
    }

    public sealed class MissionScript
    {
        /// <summary>The waits the town knows (@talk role, @parked role, @idle role, @wait seconds).</summary>
        static readonly string[] WAITS = { "talk", "parked", "idle", "wait" };

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
            // A story's stand-in functions are for Inky: here, an EXTERNAL the town
            // doesn't know stops the mission (on its first line) rather than
            // quietly doing nothing.
            story.allowExternalFunctionFallbacks = false;
            story.onError += (message, type) =>
            {
                if (type == Ink.ErrorType.Error) Fail(message);
            };
            Bind("objective", a => { world.Objective(Str(a, 0)); return null; });
            Bind("hint", a => { world.Hint(Str(a, 0), Str(a, 1), Str(a, 2)); return null; });
            Bind("clear_hints", a => { world.ClearHints(); return null; });
            Bind("setting", a => { world.Setting(Str(a, 0), Num(a, 1)); return null; });
            Bind("place", a => world.Place(Str(a, 0), Str(a, 1), Str(a, 2), Num(a, 3), Num(a, 4), Num(a, 5) != 0) ?? "");
            Bind("spawn", a => world.Spawn(Str(a, 0), Str(a, 1), Str(a, 2)) ?? "");
            Bind("walk", a => { world.Walk(Str(a, 0), Str(a, 1)); return null; });
            Bind("pause", a => { world.Pause(Str(a, 0), Num(a, 1)); return null; });
            Bind("hold", a => { world.Hold(Str(a, 0), Str(a, 1), Num(a, 2)); return null; });
            Bind("put_in_car", a => { world.PutInCar(Str(a, 0)); return null; });
            Bind("take_from_car", a => { world.TakeFromCar(Str(a, 0)); return null; });
            Bind("put_down", a => { world.PutDown(Str(a, 0)); return null; });
            Bind("leave", a => { world.Leave(Str(a, 0)); return null; });
            Bind("mark_spot", a => { world.MarkSpot(Str(a, 0)); return null; });
            Bind("clear_spot", a => { world.ClearSpot(Str(a, 0)); return null; });
            Bind("guide", a => { world.Guide(Str(a, 0), Str(a, 1), Str(a, 2)); return null; });
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
                    var w = ParseWait(text);
                    if (Array.IndexOf(WAITS, w.kind) < 0)
                    {
                        Fail($"no such wait '{text}' (@talk, @parked, @idle or @wait)");
                        return;
                    }
                    waits.Add(w);
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

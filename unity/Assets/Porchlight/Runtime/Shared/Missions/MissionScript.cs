// A mission as it plays: an ink story (compiled by the server from
// server/missions, see the README there) run a line at a time, going on only
// once you've read each one (so what the story does after a line happens
// after you've read it). What it says is shown a line at a time (a line "sender: Hello" is spoken by whoever plays
// the sender, "you: …" by you); its ordinary choices are yours to pick; and a
// choice whose text starts with @ is a wait, picked by itself once the town
// says it's met (@talk sender: you've spoken to them, @parked drop: your car
// is stopped in their spot, @idle sender: their errands are done, @inside zoo
// and @outside zoo: you're in or out of the role's grounds, @caught police:
// the role's chasers have had you, @countdown: the countdown's run out,
// @doused fire: one of the role's burning windows has gone out, @out fire:
// they all have, @finished race: you've run the role's course to its finish,
// @wait 3: three seconds on). The story changes the town through its EXTERNAL
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
        /// <summary>`count` of a thing (a model, size tiles across) piled in the back of your car (a pickup's bed; another car's roof), in place of any load before.</summary>
        void LoadBack(string model, int count, double size);
        /// <summary>Queue: the role's person, stood behind your car, takes the top thing off the pile in its back into their arms.</summary>
        void TakeFromBack(string role);
        /// <summary>The role's person drops what they're doing, goes indoors and is gone.</summary>
        void Leave(string role);
        /// <summary>A wheelie bin (a model, size tiles across) out on the pavement by the kerb outside the role's building, handle to the street.</summary>
        void Bin(string role, string model, double size);
        /// <summary>Someone called name, looking like look ("" any), hops out of your car's kerb-side door as the role's person (your car must be stopped by the role). Returns their name.</summary>
        string GetOut(string role, string name, string look);
        /// <summary>Queue: the role's person takes hold of the role's bin by its handle and tips it back onto its wheels to wheel it.</summary>
        void TakeBin(string role);
        /// <summary>Queue: the role's person lifts the bin they're wheeling and tips it into the back of your car, lid flapping open, and brings it back down.</summary>
        void Dump(string role);
        /// <summary>Queue: the role's person stands the bin they're wheeling back where it was.</summary>
        void ReturnBin(string role);
        /// <summary>Queue: the role's person walks to your car's door and hops back in (out of sight, riding with you).</summary>
        void GetIn(string role);
        /// <summary>Mark the spot to park in outside the role's building.</summary>
        void MarkSpot(string role);
        /// <summary>The role's marked spot goes.</summary>
        void ClearSpot(string role);
        /// <summary>Directions to the role's spot, person or house, called label; "" role stops.</summary>
        void Guide(string role, string to, string label);
        /// <summary>A blue dot on the ground at the role's spot, person, house (the pavement outside) or door (just outside it), no way shown to it; dot("", "") clears it.</summary>
        void Dot(string role, string to);
        /// <summary>You look like this (a character or an animal) until the mission's over; "" yourself again.</summary>
        void Look(string model);
        /// <summary>You're put at the role's place: inside, at its door or on the pavement in front (out of any car).</summary>
        void PutYou(string role, string at);
        /// <summary>
        /// A role that's a chase: `count` cars (a model) after you, each with someone at the
        /// wheel looking like look, called name between them. Leave(role) calls them off. Returns their name.
        /// </summary>
        string Chase(string role, string name, int count, string car, string look);
        /// <summary>A countdown from `seconds`, shown called label, running while `when` holds (always, inside or outside a role); 0 seconds stops it.</summary>
        void Countdown(double seconds, string label, string when, string role);
        /// <summary>
        /// A chase's cars got ready ahead of it: `count` of them (a model, someone at each wheel
        /// looking like `look`) parked out of the way all over the town. Its chase sends them all
        /// in, from every side; called off, they go back to wait.
        /// </summary>
        void Standby(string role, int count, string car, string look);
        /// <summary>Something the player keeps for finishing (Catalog.UNLOCKS: "animals", "firefighter", "sprint", "feast").</summary>
        void Unlock(string what);
        /// <summary>Your own car becomes a `car` (a model), and you're at its wheel; "" gives you back the one you had.</summary>
        void YourCar(string car);
        /// <summary>The role's building alight: up to `windows` of the windows a fire engine parked out front can reach, and its front door, smoke pouring out; 0 puts it out. How many are burning to be put out: those windows and the door.</summary>
        int Fire(string role, int windows);
        /// <summary>
        /// A water turret on your car's roof, yours to go up to (A at the wheel, stopped), aimed
        /// first at the role's building; `crew` people looking like `look` by your car spray where
        /// you do. "" role takes it away (and you down from it).
        /// </summary>
        void Turret(string role, int crew, string look);
        /// <summary>
        /// A race course as a role: the longest way along the streets from one corner of the town to the
        /// opposite one, fenced with ribbon both sides and lined with people (`crowd` 0 to 1, how packed).
        /// put_you(role, "start") puts you on its start line. Returns how long it is, in metres.
        /// </summary>
        int Course(string role, double crowd);
        /// <summary>
        /// A stopwatch shown called label: with a course's role, it starts as you cross its start line and
        /// stops as you finish (@finished); with "" it starts now. "" label takes it away.
        /// </summary>
        void Stopwatch(string label, string role);
        /// <summary>The stopwatch's time, in seconds (to the hundredth).</summary>
        double StopwatchTime();
        /// <summary>Keep a time as yours for this mission in this town (if it's your best there); your best there before it (0: none).</summary>
        double Record(double seconds);
        /// <summary>This town's best time for this mission, by anyone, as the mission began (0: none yet).</summary>
        double TownRecord();
        /// <summary>Who ran it ("" none).</summary>
        string TownRecordHolder();
        /// <summary>Is this wait met yet? talk, parked, idle, inside, outside, caught, doused, out or finished for a role, or countdown.</summary>
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
        /// <summary>talk, parked, idle, inside, outside, caught, doused, out, finished, countdown or wait.</summary>
        public string kind;
        public string role;
        /// <summary>For @wait: how many seconds.</summary>
        public double seconds;
    }

    public sealed class MissionScript
    {
        /// <summary>The waits the town knows (@talk, @parked, @idle, @inside, @outside, @caught, @doused, @out and @finished a role, @countdown, @wait seconds).</summary>
        static readonly string[] WAITS = { "talk", "parked", "idle", "inside", "outside", "caught", "doused", "out", "finished", "countdown", "wait" };

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
            Bind("load_back", a => { world.LoadBack(Str(a, 0), (int)Math.Round(Num(a, 1)), Num(a, 2)); return null; });
            Bind("take_from_back", a => { world.TakeFromBack(Str(a, 0)); return null; });
            Bind("leave", a => { world.Leave(Str(a, 0)); return null; });
            Bind("bin", a => { world.Bin(Str(a, 0), Str(a, 1), Num(a, 2)); return null; });
            Bind("get_out", a => world.GetOut(Str(a, 0), Str(a, 1), Str(a, 2)) ?? "");
            Bind("take_bin", a => { world.TakeBin(Str(a, 0)); return null; });
            Bind("dump", a => { world.Dump(Str(a, 0)); return null; });
            Bind("return_bin", a => { world.ReturnBin(Str(a, 0)); return null; });
            Bind("get_in", a => { world.GetIn(Str(a, 0)); return null; });
            Bind("mark_spot", a => { world.MarkSpot(Str(a, 0)); return null; });
            Bind("clear_spot", a => { world.ClearSpot(Str(a, 0)); return null; });
            Bind("guide", a => { world.Guide(Str(a, 0), Str(a, 1), Str(a, 2)); return null; });
            Bind("dot", a => { world.Dot(Str(a, 0), Str(a, 1)); return null; });
            Bind("look", a => { world.Look(Str(a, 0)); return null; });
            Bind("put_you", a => { world.PutYou(Str(a, 0), Str(a, 1)); return null; });
            Bind("chase", a => world.Chase(Str(a, 0), Str(a, 1), (int)Math.Round(Num(a, 2)), Str(a, 3), Str(a, 4)) ?? "");
            Bind("countdown", a => { world.Countdown(Num(a, 0), Str(a, 1), Str(a, 2), Str(a, 3)); return null; });
            Bind("standby", a => { world.Standby(Str(a, 0), (int)Math.Round(Num(a, 1)), Str(a, 2), Str(a, 3)); return null; });
            Bind("unlock", a => { world.Unlock(Str(a, 0)); return null; });
            Bind("your_car", a => { world.YourCar(Str(a, 0)); return null; });
            Bind("fire", a => world.Fire(Str(a, 0), (int)Math.Round(Num(a, 1))));
            Bind("turret", a => { world.Turret(Str(a, 0), (int)Math.Round(Num(a, 1)), Str(a, 2)); return null; });
            Bind("course", a => world.Course(Str(a, 0), Num(a, 1)));
            Bind("stopwatch", a => { world.Stopwatch(Str(a, 0), Str(a, 1)); return null; });
            Bind("stopwatch_time", a => (float)world.StopwatchTime());
            Bind("record", a => (float)world.Record(Num(a, 0)));
            Bind("town_record", a => (float)world.TownRecord());
            Bind("town_record_holder", a => world.TownRecordHolder() ?? "");
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
                        Fail($"no such wait '{text}' (@{string.Join(", @", WAITS)})");
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

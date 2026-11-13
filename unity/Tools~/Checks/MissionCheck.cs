// Plays a mission, as the server compiled it, through MissionScript and the
// ink runtime the game carries, against a stand-in town that meets every wait
// a moment after it's asked: that the story the server serves loads and runs
// here, and reaches its end. Prints what it said and did.
//
//   node server/scripts/mission.ts delivery > /tmp/delivery.json
//   dotnet run --project "unity/Tools~/Checks" -- mission /tmp/delivery.json

using System;
using System.Collections.Generic;
using System.IO;
using Porchlight.Missions;

static class MissionCheck
{
    sealed class Town : IMissionWorld
    {
        static readonly string[] HINTS = { "always", "no_car", "near", "near_driving", "off_course" };
        static readonly string[] SETTINGS = { "talk_reach", "talk_reach_car", "park_reach", "park_speed", "park_angle", "car_reach", "walk_speed",
            "your_pace", "chase_speed", "chase_speed_car", "pull_up", "officer_speed", "officer_reach", "ring", "catch_reach", "catch_time",
            "douse_time", "water_speed", "aim_speed" };
        static readonly string[] UNLOCKS = { "animals", "firefighter", "sprint", "feast" };
        static readonly string[] KINDS = { "", "house", "workshop", "shop", "office", "hall", "church", "zoo" };
        static readonly string[] COUNTDOWNS = { "always", "inside", "outside" };
        static readonly string[] PLACES = { "inside", "door", "kerb", "car", "bin", "back" };
        static readonly string[] BINS = { "props/wheelie-bin-trash", "props/wheelie-bin-recycle" };
        static readonly string[] GUIDES = { "spot", "person", "house", "door", "ring", "tree", "car" };

        public readonly List<string> log = new List<string>();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        readonly HashSet<string> placed = new HashSet<string>();
        readonly HashSet<string> present = new HashSet<string>();
        readonly HashSet<string> marked = new HashSet<string>();
        /// <summary>What each role's person will be holding once their errands so far are done.</summary>
        readonly Dictionary<string, string> holding = new Dictionary<string, string>();
        readonly HashSet<string> asked = new HashSet<string>();
        /// <summary>How many things ride on the car, once everyone's errands so far are done.</summary>
        int cargo;
        /// <summary>Who's after you, and how often they've caught you (once is enough here).</summary>
        readonly HashSet<string> chases = new HashSet<string>();
        readonly HashSet<string> standing = new HashSet<string>();
        public readonly List<string> unlocked = new List<string>();
        int catches;
        bool clock;
        string look = "";
        /// <summary>Each burning role's windows still alight, the role the turret's offered for, and the car the story gave you.</summary>
        readonly Dictionary<string, int> burning = new Dictionary<string, int>();
        string turret = "", car = "";
        /// <summary>Courses, and the stopwatch ("" none) and which course it's on.</summary>
        readonly HashSet<string> courses = new HashSet<string>();
        string stopwatch = "", stopwatchOn = "";
        /// <summary>Each role's bin (true: someone's wheeling it), who's back in your car, and how many bins have been tipped into it.</summary>
        readonly Dictionary<string, bool> bins = new Dictionary<string, bool>();
        readonly HashSet<string> aboard = new HashSet<string>();
        public int emptied;
        /// <summary>How many things are piled in the back of your car (load_back), once everyone's errands so far are done.</summary>
        int load;
        /// <summary>
        /// The plane the story gave you ("" none) and whether you're in its seat; each role's ring (true: flown
        /// through, not yet heard), where your plane's coming down (role and roof or kerb) or is down, the chimney
        /// trip under way (role, down or up) and where you are with it, and each role's tree (true: given).
        /// </summary>
        string plane = "";
        bool seated;
        readonly Dictionary<string, bool> rings = new Dictionary<string, bool>();
        string landing = "", landingAt = "";
        readonly Dictionary<string, string> landed = new Dictionary<string, string>();
        string climbing = "", climbWay = "";
        /// <summary>Down a role's chimney (its role), out of sight of your seat.</summary>
        string downAt = "";
        readonly Dictionary<string, bool> trees = new Dictionary<string, bool>();
        public int ringsFlown, landings, chimneys, gifts;

        static void OneOf(string what, string value, string[] known)
        {
            if (Array.IndexOf(known, value) < 0) throw new Exception($"no such {what} '{value}' ({string.Join(", ", known)})");
        }

        void Placed(string role)
        {
            if (!placed.Contains(role)) throw new Exception($"no building for '{role}' yet (call place first)");
        }

        void Present(string role)
        {
            Placed(role);
            if (!present.Contains(role)) throw new Exception($"no one plays '{role}' (call spawn first)");
        }

        public void Objective(string text) => log.Add($"objective: {text}");
        public void Hint(string when, string role, string text)
        {
            OneOf("condition", when, HINTS);
            if (when == "off_course" && !courses.Contains(role)) throw new Exception($"no course called '{role}'");
            log.Add($"hint {when} {role}: {text}");
        }
        public void ClearHints() => log.Add("clear hints");
        public void Setting(string name, double value)
        {
            OneOf("setting", name, SETTINGS);
            log.Add($"setting {name} {value}");
        }
        public string Place(string role, string kind, string near, double min, double max, bool parking)
        {
            OneOf("kind of building", kind, KINDS);
            if (chases.Contains(role)) throw new Exception($"'{role}' is a chase");
            if (near != "") Placed(near);
            placed.Add(role);
            present.Remove(role);
            marked.Remove(role);
            bins.Remove(role);
            aboard.Remove(role);
            rings.Remove(role);
            landed.Remove(role);
            trees.Remove(role);
            log.Add($"place {role} {kind} {(near == "" ? "you" : near)} {min}..{max}{(parking ? " parking" : "")}");
            return kind == "zoo" ? "the zoo" : role == "drop" ? "Elm Street" : "Mill Road";
        }
        public string Spawn(string role, string name, string look)
        {
            Placed(role);
            present.Add(role);
            holding.Remove(role);
            names[role] = name;
            log.Add($"spawn {role} = {name}");
            return name;
        }
        public void Walk(string role, string to)
        {
            Present(role);
            OneOf("place", to, PLACES);
            if (to == "bin" && !bins.ContainsKey(role)) throw new Exception($"there's no bin at '{role}' (call bin first)");
            if (to == "back" && car == "") throw new Exception("round the back of what? (your_car first)");
            log.Add($"  {role} walks to {to}");
        }
        public void Bin(string role, string model, double size)
        {
            Placed(role);
            OneOf("bin", model, BINS);
            if (size <= 0) throw new Exception("a bin needs a size (tiles across)");
            bins[role] = false;
            log.Add($"bin {role} {model}");
        }
        public string GetOut(string role, string name, string look)
        {
            Placed(role);
            if (car == "") throw new Exception("you've no car for anyone to get out of");
            present.Add(role);
            aboard.Remove(role);
            holding.Remove(role);
            names[role] = name;
            log.Add($"get out {role} = {name}");
            return name;
        }
        public void TakeBin(string role)
        {
            Present(role);
            if (!bins.TryGetValue(role, out bool held) || held) throw new Exception($"there's no bin at '{role}' to take");
            bins[role] = true;
            log.Add($"  {role} takes the bin");
        }
        public void Dump(string role)
        {
            Present(role);
            if (!bins.TryGetValue(role, out bool held) || !held) throw new Exception($"{role} has no bin to tip out");
            emptied++;
            log.Add($"  {role} tips it into the car ({emptied} emptied)");
        }
        public void ReturnBin(string role)
        {
            Present(role);
            if (!bins.TryGetValue(role, out bool held) || !held) throw new Exception($"{role} has no bin to put back");
            bins[role] = false;
            log.Add($"  {role} puts the bin back");
        }
        public void GetIn(string role)
        {
            Present(role);
            if (car == "") throw new Exception("get in what? (your_car first)");
            if (bins.TryGetValue(role, out bool held) && held) throw new Exception($"{role} gets in still holding the bin");
            aboard.Add(role);
            log.Add($"  {role} gets in");
        }
        public void Pause(string role, double seconds)
        {
            Present(role);
            log.Add($"  {role} waits {seconds}s");
        }
        public void Hold(string role, string model, double size)
        {
            Present(role);
            if (holding.ContainsKey(role)) throw new Exception($"{role} already has their hands full");
            if (string.IsNullOrEmpty(model) || size <= 0) throw new Exception("hold needs a model and a size");
            holding[role] = model;
            log.Add($"  {role} holds {model}");
        }
        public void PutInCar(string role)
        {
            Present(role);
            if (!holding.Remove(role)) throw new Exception($"{role} has nothing to put in the car");
            cargo++;
            log.Add($"  {role} puts it in the car ({cargo} on board)");
        }
        public void TakeFromCar(string role)
        {
            Present(role);
            if (cargo == 0) throw new Exception("there's nothing on the car to take");
            if (holding.ContainsKey(role)) throw new Exception($"{role} already has their hands full");
            cargo--;
            holding[role] = "cargo";
            log.Add($"  {role} takes one from the car ({cargo} on board)");
        }
        public void LoadBack(string model, int count, double size)
        {
            if (car == "") throw new Exception("load what car? (your_car first)");
            if (string.IsNullOrEmpty(model) || size <= 0) throw new Exception("load_back needs a model and a size");
            if (count < 0 || count > 40) throw new Exception($"load how many? {count} (0 to 40)");
            load = count;
            log.Add($"load {count} x {model} in the back");
        }
        public void TakeFromBack(string role)
        {
            Present(role);
            if (load == 0) throw new Exception("there's nothing left in the back to take");
            if (holding.ContainsKey(role)) throw new Exception($"{role} already has their hands full");
            load--;
            holding[role] = "load";
            log.Add($"  {role} takes one from the back ({load} left)");
        }
        public void PutDown(string role)
        {
            Present(role);
            if (!holding.Remove(role)) throw new Exception($"{role} has nothing to put down");
            log.Add($"  {role} puts it down");
        }
        public void Leave(string role)
        {
            present.Remove(role);
            holding.Remove(role);
            chases.Remove(role);
            log.Add($"leave {role}");
        }
        public void MarkSpot(string role)
        {
            Placed(role);
            marked.Add(role);
            log.Add($"mark spot {role}");
        }
        public void ClearSpot(string role)
        {
            marked.Remove(role);
            log.Add($"clear spot {role}");
        }
        public void Guide(string role, string to, string label)
        {
            if (role != "")
            {
                Placed(role);
                OneOf("guide", to, GUIDES);
            }
            log.Add(role == "" ? "guide off" : $"guide {role} {to}: {label}");
        }
        public void Dot(string role, string to)
        {
            if (role != "")
            {
                Placed(role);
                OneOf("dot", to, GUIDES);
            }
            log.Add(role == "" ? "dot off" : $"dot {role} {to}");
        }
        public void Look(string model)
        {
            look = model;
            log.Add(model == "" ? "look as yourself" : $"look like {model}");
        }
        public void PutYou(string role, string at)
        {
            if (courses.Contains(role))
            {
                OneOf("place on a course", at, new[] { "start", "finish" });
                log.Add($"put you at the {at} of {role}");
                return;
            }
            Placed(role);
            OneOf("place", at, PLACES);
            seated = false;
            downAt = "";
            log.Add($"put you {at} {role}");
        }
        public void YourPlane(string model, double height)
        {
            if (model == "")
            {
                plane = "";
                seated = false;
                log.Add("your plane gone");
                return;
            }
            if (!model.StartsWith("planes/")) throw new Exception($"no such plane '{model}'");
            if (height < 1 || height > 24) throw new Exception($"fly how high? {height} (1 to 24 tiles)");
            plane = model;
            seated = true;
            log.Add($"your plane: {model}, {height} up");
        }
        public void Ring(string role, double height, double size)
        {
            Placed(role);
            if (size <= 0)
            {
                rings.Remove(role);
                log.Add($"ring at {role} down");
                return;
            }
            if (height < 1 || height > 24) throw new Exception($"a ring how high? {height} (1 to 24 tiles)");
            if (size > 12) throw new Exception($"a ring how big? {size} (up to 12 tiles across)");
            rings[role] = false;
            log.Add($"ring at {role}, {height} up, {size} across");
        }
        public void Land(string role, string at)
        {
            Placed(role);
            OneOf("place to land", at, new[] { "roof", "kerb" });
            if (plane == "" || !seated) throw new Exception("you're not flying a plane (your_plane first)");
            landing = role;
            landingAt = at;
            landed.Remove(role);
            log.Add($"land on {role}'s {at}");
        }
        public void Chimney(string role, string way)
        {
            Placed(role);
            OneOf("way", way, new[] { "down", "up" });
            if (!landed.TryGetValue(role, out var at) || at != "roof") throw new Exception($"there's no chimney to climb at '{role}' (land on its roof first)");
            if (climbing != "") throw new Exception("you're already on your way up or down a chimney");
            if (way == "down" && !seated) throw new Exception("down the chimney from where? (you're not in your seat on the roof)");
            if (way == "up" && downAt != role) throw new Exception("up the chimney from where? (you're not down it)");
            climbing = role;
            climbWay = way;
            seated = false;
            log.Add($"chimney {way} at {role}");
        }
        public void Tree(string role, string gift)
        {
            Placed(role);
            if (string.IsNullOrEmpty(gift)) throw new Exception("a tree needs a present to go under it");
            trees[role] = false;
            log.Add($"tree at {role} for {gift}");
        }
        public string Chase(string role, string name, int count, string car, string model)
        {
            if (role == "" || placed.Contains(role)) throw new Exception("a chase needs a role that isn't a building's");
            if (count < (standing.Contains(role) ? 0 : 1) || count > 8) throw new Exception("a chase is 1 to 8 cars (0 with a standby)");
            if (!car.StartsWith("cars/")) throw new Exception($"no such car '{car}'");
            chases.Add(role);
            names[role] = name;
            log.Add($"chase {role} = {name}: {count} x {car}");
            return name;
        }
        public void Standby(string role, int count, string car, string model)
        {
            if (role == "" || placed.Contains(role)) throw new Exception("a standby needs a role that isn't a building's");
            if (count < 1 || count > 24) throw new Exception("a standby is 1 to 24 cars");
            if (!car.StartsWith("cars/")) throw new Exception($"no such car '{car}'");
            standing.Add(role);
            log.Add($"standby {role}: {count} x {car}");
        }
        public void Unlock(string what)
        {
            OneOf("unlock", what, UNLOCKS);
            unlocked.Add(what);
            log.Add($"unlock {what}");
        }
        public void Countdown(double seconds, string label, string when, string role)
        {
            clock = seconds > 0;
            if (clock)
            {
                OneOf("condition", when, COUNTDOWNS);
                if (when != "always") Placed(role);
            }
            log.Add(clock ? $"countdown {seconds}s {label} ({when} {role})" : "countdown off");
        }
        public void YourCar(string model)
        {
            if (model != "" && !model.StartsWith("cars/")) throw new Exception($"no such car '{model}'");
            car = model;
            log.Add(model == "" ? "your car back" : $"your car: {model}");
        }
        public int Fire(string role, int windows)
        {
            Placed(role);
            if (windows < 0 || windows > 8) throw new Exception("a fire is 0 to 8 windows");
            // A town's house can have fewer windows facing the street than asked for: this one has
            // two. Its door burns too, and is put out as they are.
            if (windows > 0) windows = Math.Min(windows, 2) + 1;
            if (windows > 0) burning[role] = windows;
            else burning.Remove(role);
            log.Add(windows > 0 ? $"fire {role}: {windows} windows" : $"fire {role} out");
            return windows;
        }
        public void Turret(string role, int crew, string model)
        {
            if (role != "")
            {
                Placed(role);
                if (crew < 0 || crew > 6) throw new Exception("a turret's crew is 0 to 6");
            }
            turret = role;
            log.Add(role == "" ? "turret off" : $"turret at {role}, {crew} crew");
        }
        public int Course(string role, double crowd)
        {
            if (string.IsNullOrEmpty(role) || placed.Contains(role) || chases.Contains(role)) throw new Exception($"a course needs a role of its own, not '{role}'");
            if (crowd < 0 || crowd > 1) throw new Exception("a crowd is 0 to 1");
            courses.Add(role);
            log.Add($"course {role}, crowd {crowd}");
            return 4321;
        }
        public void Stopwatch(string label, string role)
        {
            if (!string.IsNullOrEmpty(label) && role != "" && !courses.Contains(role)) throw new Exception($"no course called '{role}'");
            stopwatch = label ?? "";
            stopwatchOn = stopwatch == "" ? "" : role;
            log.Add(stopwatch == "" ? "stopwatch off" : $"stopwatch {label} on {role}");
        }
        public double StopwatchTime() => stopwatch != "" ? 754.32 : throw new Exception("there's no stopwatch");
        public double Record(double seconds)
        {
            log.Add($"record {seconds}");
            return 0;
        }
        public double TownRecord() => 0;
        public string TownRecordHolder() => "";
        public string NameOf(string role) => names.TryGetValue(role, out var n) ? n : null;

        /// <summary>
        /// Each wait is met the second time it's asked about, if it could be: someone
        /// to talk to or be idle, a spot to park in.
        /// </summary>
        public bool Met(string wait, string role)
        {
            // You fly through a ring, are brought down, go down or up a chimney, leave a present, climb back in.
            if (wait == "ring" && (!rings.ContainsKey(role) || plane == "" || !seated)) return false;
            if (wait == "landed" && landing != role && !landed.ContainsKey(role)) return false;
            if (wait == "chimney" && climbing != role) return false;
            if (wait == "gift" && (!trees.ContainsKey(role) || seated)) return false;
            if (wait == "aboard" && (plane == "" || downAt != "" || climbing != "")) return false;
            if (wait == "parked" && !marked.Contains(role)) return false;
            if ((wait == "talk" || wait == "idle") && !present.Contains(role)) return false;
            if ((wait == "inside" || wait == "outside") && !placed.Contains(role)) return false;
            if (wait == "caught" && (!chases.Contains(role) || catches > 0)) return false;
            if (wait == "countdown" && !clock) return false;
            if (wait == "finished" && (!courses.Contains(role) || stopwatchOn != role)) return false;
            // A fire goes out a window at a time, and only with the turret there to do it.
            if ((wait == "doused" || wait == "out") && (turret != role || !burning.TryGetValue(role, out int left) || left == 0 || (wait == "doused") != (left > 1))) return false;
            if (asked.Add($"{wait} {role} {log.Count}")) return false;
            if (wait == "caught") catches++;
            if (wait == "ring")
            {
                rings.Remove(role);
                ringsFlown++;
            }
            if (wait == "landed" && landing == role)
            {
                landed[role] = landingAt;
                landing = "";
                landings++;
            }
            if (wait == "chimney")
            {
                if (climbWay == "down") downAt = role;
                else
                {
                    downAt = "";
                    seated = true;
                }
                climbing = "";
                chimneys++;
            }
            if (wait == "gift" && !trees[role])
            {
                trees[role] = true;
                gifts++;
            }
            if (wait == "aboard") seated = true;
            if (wait == "doused" || wait == "out") burning[role] = wait == "out" ? 0 : burning[role] - 1;
            return true;
        }

        /// <summary>Things left on the car at the end.</summary>
        public int Cargo => cargo;
        /// <summary>Still looking like something else, or chased, at the end.</summary>
        public string Loose => look != "" ? $"still looking like {look}" : chases.Count > 0 ? $"still chased by {string.Join(", ", chases)}" : clock ? "a countdown still running"
            : car != "" ? $"still in the story's {car}" : plane != "" ? $"still flying the story's {plane}" : turret != "" ? "a turret still offered" : burning.Count > 0 ? "a fire still burning" : stopwatch != "" ? "a stopwatch still showing" : null;
    }

    public static int Run(string path)
    {
        var town = new Town();
        var script = new MissionScript(File.ReadAllText(path), town);
        int steps = 0;
        while (!script.Done && steps++ < 10000)
        {
            if (script.Line != null)
            {
                var l = script.Line;
                town.log.Add($"  {(l.speaker != null ? l.speaker + ": " : "")}{l.text}");
                script.Advance();
                continue;
            }
            if (script.Choices.Count > 0)
            {
                int pick = 0;
                for (int i = 0; i < script.Choices.Count; i++) if (script.Choices[i].StartsWith("Sure")) pick = i;
                town.log.Add($"  > {script.Choices[pick]}");
                script.Choose(pick);
                continue;
            }
            script.Update(0.1);
        }
        foreach (var line in town.log) Console.WriteLine(line);
        if (script.Error != null)
        {
            Console.Error.WriteLine($"mission stopped on an error: {script.Error}");
            return 1;
        }
        if (!script.Done)
        {
            Console.Error.WriteLine("mission never finished");
            return 1;
        }
        if (town.Cargo != 0) Console.Error.WriteLine($"warning: {town.Cargo} thing(s) left on the car at the end");
        if (town.Loose != null) Console.Error.WriteLine($"warning: {town.Loose} at the end");
        if (town.unlocked.Count > 0) Console.WriteLine($"mission: unlocked {string.Join(", ", town.unlocked)}");
        Console.WriteLine("mission: played to its end");
        return 0;
    }
}

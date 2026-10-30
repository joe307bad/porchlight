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
        static readonly string[] HINTS = { "always", "no_car", "near", "near_driving" };
        static readonly string[] SETTINGS = { "talk_reach", "talk_reach_car", "park_reach", "park_speed", "park_angle", "car_reach", "walk_speed" };
        static readonly string[] KINDS = { "", "house", "workshop", "shop", "office", "hall", "church" };
        static readonly string[] PLACES = { "inside", "door", "kerb", "car" };
        static readonly string[] GUIDES = { "spot", "person", "house" };

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
            if (near != "") Placed(near);
            placed.Add(role);
            present.Remove(role);
            marked.Remove(role);
            log.Add($"place {role} {kind} {(near == "" ? "you" : near)} {min}..{max}{(parking ? " parking" : "")}");
            return role == "drop" ? "Elm Street" : "Mill Road";
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
            log.Add($"  {role} walks to {to}");
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
        public string NameOf(string role) => names.TryGetValue(role, out var n) ? n : null;

        /// <summary>
        /// Each wait is met the second time it's asked about, if it could be: someone
        /// to talk to or be idle, a spot to park in.
        /// </summary>
        public bool Met(string wait, string role)
        {
            if (wait == "parked" && !marked.Contains(role)) return false;
            if ((wait == "talk" || wait == "idle") && !present.Contains(role)) return false;
            return !asked.Add($"{wait} {role} {log.Count}");
        }

        /// <summary>Things left on the car at the end.</summary>
        public int Cargo => cargo;
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
        Console.WriteLine("mission: played to its end");
        return 0;
    }
}

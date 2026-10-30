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
        public readonly List<string> log = new List<string>();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        readonly HashSet<string> asked = new HashSet<string>();
        int people;

        public void Objective(string text) => log.Add($"objective: {text}");
        public string House(string role, string near, double min, double max)
        {
            log.Add($"house {role} {(near == "" ? "you" : near)} {min}..{max}");
            return role == "drop" ? "Elm Street" : "Mill Road";
        }
        public string Npc(string role)
        {
            string name = new[] { "Ada", "Ben", "Cleo", "Dev", "Esme", "Finn" }[people++ % 6];
            names[role] = name;
            log.Add($"npc {role} = {name}");
            return name;
        }
        public void Guide(string role) => log.Add($"guide {role}");
        public void Parking(string role) => log.Add($"parking {role}");
        public void Load(string role, int boxes) => log.Add($"load {role} {boxes}");
        public void Unload(string role) => log.Add($"unload {role}");
        public void Dismiss(string role) => log.Add($"dismiss {role}");
        public string NameOf(string role) => names.TryGetValue(role, out var n) ? n : null;

        /// <summary>Each wait is met the second time it's asked about.</summary>
        public bool Met(string wait, string role) => !asked.Add($"{wait} {role} {log.Count}");
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
        Console.WriteLine("mission: played to its end");
        return 0;
    }
}

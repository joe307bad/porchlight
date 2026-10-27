// Checks the C# generator against the TypeScript one: regenerates every town
// in a fixture file written by ../export.ts and compares the two, field by
// field, exactly (every float to the last bit). Prints the first difference
// per town.
//
//   dotnet run --project "unity/Tools~/parity/GenCheck" -- parity.json

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Porchlight.Gen;
using Porchlight.Model;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "math") return MathCheck.Run(args[1]);
        if (args.Length > 1 && args[0] == "sim") return SimCheck.Run(args[1]);
        var path = args.Length > 0 ? args[0] : "parity.json";
        var only = args.Length > 1 ? args[1] : null;
        var towns = JsonNode.Parse(File.ReadAllText(path)).AsArray();
        int failed = 0;
        foreach (var t in towns)
        {
            var seed = (string)t["seed"];
            var p = new GenParams();
            foreach (var kv in t["params"].AsObject()) p[kv.Key] = (double)kv.Value;
            var sw = Stopwatch.StartNew();
            TownModel model;
            try
            {
                model = Generator.Generate(seed, p, check: true);
            }
            catch (InvariantException e)
            {
                Console.WriteLine($"{seed}: invariant failed: {e.Message}");
                model = Generator.Generate(seed, p);
            }
            var mine = Snapshot.Of(model);
            var diffs = new List<string>();
            foreach (var key in mine.Keys)
            {
                if (only != null && key != only) continue;
                Compare(key, t[key], mine[key], diffs);
            }
            Console.WriteLine(diffs.Count == 0 ? $"{seed}: same ({sw.ElapsedMilliseconds} ms)" : $"{seed}: {diffs.Count} differences, first: {string.Join(" | ", diffs.Take(3))}");
            if (diffs.Count > 0) failed++;
        }
        Console.WriteLine(failed == 0 ? $"all {towns.Count} towns match" : $"{failed} of {towns.Count} towns differ");
        return failed == 0 ? 0 : 1;
    }

    static void Compare(string path, JsonNode a, JsonNode b, List<string> diffs)
    {
        if (diffs.Count > 20) return;
        if (a == null || b == null)
        {
            if (a != null || b != null) diffs.Add($"{path}: {a?.ToJsonString() ?? "null"} vs {b?.ToJsonString() ?? "null"}");
            return;
        }
        if (a is JsonArray aa)
        {
            if (!(b is JsonArray ba))
            {
                diffs.Add($"{path}: array vs {b.ToJsonString()}");
                return;
            }
            if (aa.Count != ba.Count) diffs.Add($"{path}: length {aa.Count} vs {ba.Count}");
            for (int i = 0; i < Math.Min(aa.Count, ba.Count); i++) Compare($"{path}[{i}]", aa[i], ba[i], diffs);
            return;
        }
        if (a is JsonObject ao)
        {
            if (!(b is JsonObject bo))
            {
                diffs.Add($"{path}: object vs {b.ToJsonString()}");
                return;
            }
            foreach (var kv in ao)
            {
                if (!bo.ContainsKey(kv.Key))
                {
                    diffs.Add($"{path}.{kv.Key}: missing");
                    continue;
                }
                Compare($"{path}.{kv.Key}", kv.Value, bo[kv.Key], diffs);
            }
            return;
        }
        var av = a.AsValue();
        var bv = b.AsValue();
        if (av.TryGetValue(out double ad) && bv.TryGetValue(out double bd))
        {
            if (ad != bd && !(double.IsNaN(ad) && double.IsNaN(bd))) diffs.Add($"{path}: {ad:R} vs {bd:R}");
            return;
        }
        if (a.ToJsonString() != b.ToJsonString()) diffs.Add($"{path}: {a.ToJsonString()} vs {b.ToJsonString()}");
    }
}

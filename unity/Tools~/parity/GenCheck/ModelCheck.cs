// Flattens every static model with ModelData and compares with ../models.ts.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Porchlight.Core;

static class ModelCheck
{
    public static int Run(string path, string modelsDir)
    {
        var expected = JsonNode.Parse(File.ReadAllText(path)).AsArray();
        int bad = 0;
        foreach (var e in expected)
        {
            var name = (string)e["name"];
            var m = ModelData.Flatten(name, new Glb(File.ReadAllBytes(Path.Combine(modelsDir, name + ".glb"))));
            var diffs = new System.Collections.Generic.List<string>();
            var min = e["min"].AsArray().Select(x => (double)x).ToArray();
            var size = e["size"].AsArray().Select(x => (double)x).ToArray();
            if (min[0] != m.minX || min[1] != m.minY || min[2] != m.minZ) diffs.Add($"min {string.Join(",", min)} vs {m.minX},{m.minY},{m.minZ}");
            if (size[0] != m.sizeX || size[1] != m.sizeY || size[2] != m.sizeZ) diffs.Add($"size {string.Join(",", size)} vs {m.sizeX},{m.sizeY},{m.sizeZ}");
            var parts = e["parts"].AsArray();
            if (parts.Count != m.parts.Count) diffs.Add($"parts {parts.Count} vs {m.parts.Count}");
            for (int i = 0; i < Math.Min(parts.Count, m.parts.Count); i++)
            {
                var p = parts[i];
                var mp = m.parts[i];
                if ((int)p["count"] != mp.VertexCount) diffs.Add($"part {i} vertices {(int)p["count"]} vs {mp.VertexCount}");
                if ((int)p["index"] != (mp.index?.Length ?? -1)) diffs.Add($"part {i} index {(int)p["index"]} vs {mp.index?.Length ?? -1}");
                var pos = p["pos"].AsArray();
                for (int k = 0; k < pos.Count; k++) if ((double)pos[k] != mp.pos[k]) { diffs.Add($"part {i} pos[{k}] {(double)pos[k]} vs {mp.pos[k]}"); break; }
                if (p["normal"] is JsonArray nrm && mp.normal != null)
                    for (int k = 0; k < nrm.Count; k++) if ((double)nrm[k] != mp.normal[k]) { diffs.Add($"part {i} normal[{k}] {(double)nrm[k]:R} vs {mp.normal[k]:R}"); break; }
                if (p["color"] is JsonArray col)
                {
                    var mat = m.materials[mp.material];
                    if ((double)col[0] != mat.r || (double)col[1] != mat.g || (double)col[2] != mat.b) diffs.Add($"part {i} color {col.ToJsonString()} vs {mat.r},{mat.g},{mat.b}");
                }
            }
            if (diffs.Count > 0)
            {
                bad++;
                Console.WriteLine($"{name}: {string.Join(" | ", diffs.Take(3))}");
            }
        }
        Console.WriteLine(bad == 0 ? $"all {expected.Count} models match" : $"{bad} of {expected.Count} models differ");
        return bad == 0 ? 0 : 1;
    }
}

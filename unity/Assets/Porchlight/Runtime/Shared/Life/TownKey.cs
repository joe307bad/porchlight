// A town's key (its room's name: "seed=porchlight&width=256&…") read as the game
// reads it (PorchlightApp.ParseUrlState): the seed, and the settings. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Life
{
    public static class TownKey
    {
        public static (string seed, GenParams p) Parse(string key)
        {
            var q = new Dictionary<string, string>();
            foreach (var part in (key ?? "").Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = Uri.UnescapeDataString((eq < 0 ? part : part.Substring(0, eq)).Replace('+', ' '));
                string v = Uri.UnescapeDataString((eq < 0 ? "" : part.Substring(eq + 1)).Replace('+', ' '));
                if (!q.ContainsKey(k)) q[k] = v;
            }
            var partial = new Dictionary<string, double>();
            foreach (var spec in ParamSpec.All)
                if (q.TryGetValue(spec.key, out var raw)) partial[spec.key] = Numbers.ParseNumber(raw);
            q.TryGetValue("seed", out var s);
            s = s?.Trim();
            var p = GenParams.Normalize(partial);
            p.rev = Skeletons.RevOf(key);
            if (p.rev > 0) p.skeleton = Skeletons.For(key);
            return (string.IsNullOrEmpty(s) ? null : s, p);
        }
    }
}

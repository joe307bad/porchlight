// The people kit's clips (people/animations.glb) under the names the avatars
// play: idle, walk, sprint (our run), jump (rising), fall (in the air) and
// crouch (landing), keeping only what suits every person: bone rotations, and
// the hips' height. The jump starts at the bottom of its crouch and the
// landing at its lowest, and neither lifts the body. Pure: peopleClips in
// render/iso/characters.ts.

using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public sealed class PeopleClips
    {
        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            ["Idle_A"] = "idle",
            ["Walk_A"] = "walk",
            ["Runing_A"] = "sprint",
            ["Jump_A_Start"] = "jump",
            ["Jump_A_InAir"] = "fall",
            ["Jump_A_Landing"] = "crouch",
        };

        /// <summary>Hips' height standing, in the kit's units.</summary>
        public const double HIPS_REST = 0.411;

        public readonly List<Clip> clips = new List<Clip>();

        public Clip Get(string name) => clips.Find(c => c.name == name);

        public static PeopleClips From(Glb glb)
        {
            var o = new PeopleClips();
            foreach (var clip in Clip.Read(glb))
            {
                if (!Names.TryGetValue(clip.name, out var name)) continue;
                var trimmed = name == "jump" || name == "crouch" ? From(clip, LowestHips(clip)) : clip;
                bool grounded = name == "jump" || name == "fall" || name == "crouch";
                var c = new Clip { name = name, duration = trimmed.duration };
                foreach (var t in trimmed.tracks)
                {
                    bool hips = t.position && t.bone == "Hips";
                    if (t.position && !hips) continue;
                    if (hips && grounded)
                    {
                        var g = t.Clone();
                        for (int i = 1; i < g.values.Length; i += 3) if (g.values[i] > HIPS_REST) g.values[i] = (float)HIPS_REST;
                        c.tracks.Add(g);
                        continue;
                    }
                    c.tracks.Add(t);
                }
                o.clips.Add(c);
            }
            return o;
        }

        /// <summary>The time, in a clip, at which its hips are lowest.</summary>
        static double LowestHips(Clip clip)
        {
            var hips = clip.tracks.Find(t => t.position && t.bone == "Hips");
            if (hips == null) return 0;
            int best = 0;
            for (int i = 1; i < hips.times.Length; i++) if (hips.values[i * 3 + 1] < hips.values[best * 3 + 1]) best = i;
            return hips.times[best];
        }

        /// <summary>The clip from time `start` on, starting at 0.</summary>
        static Clip From(Clip clip, double start)
        {
            if (start <= 0) return clip;
            var c = new Clip { name = clip.name, duration = clip.duration - start };
            foreach (var t in clip.tracks)
            {
                var k = t.Clone();
                k.Trim(start, clip.duration);
                k.Shift(-start);
                c.tracks.Add(k);
            }
            return c;
        }
    }
}

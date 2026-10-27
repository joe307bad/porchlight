// Keyframe animation as Three plays it: clips of tracks read from glTF
// (linear or step keys, quaternions slerped), and an AnimationMixer that
// accumulates every playing action's pose by weight, bone by bone, and mixes
// in the bone's original pose wherever the weights add up to less than one.
// Engine-free; the game applies the result to its bones.

using System;
using System.Collections.Generic;

namespace Porchlight.Core
{
    public sealed class Track
    {
        /// <summary>The bone's name, sanitised as Three's PropertyBinding does.</summary>
        public string bone;
        /// <summary>True for position (3 values per key), false for quaternion (4).</summary>
        public bool position;
        public bool step;
        public float[] times;
        public float[] values;

        public int Stride => position ? 3 : 4;

        public Track Clone() => new Track { bone = bone, position = position, step = step, times = (float[])times.Clone(), values = (float[])values.Clone() };

        /// <summary>KeyframeTrack.trim: drop keys outside [start, end], keeping at least one.</summary>
        public void Trim(double start, double end)
        {
            int n = times.Length;
            int from = 0, to = n - 1;
            while (from != n && times[from] < start) ++from;
            while (to != -1 && times[to] > end) --to;
            ++to;
            if (from != 0 || to != n)
            {
                if (from >= to)
                {
                    to = Math.Max(to, 1);
                    from = to - 1;
                }
                int s = Stride;
                var t = new float[to - from];
                Array.Copy(times, from, t, 0, t.Length);
                var v = new float[(to - from) * s];
                Array.Copy(values, from * s, v, 0, v.Length);
                times = t;
                values = v;
            }
        }

        /// <summary>KeyframeTrack.shift.</summary>
        public void Shift(double offset)
        {
            if (offset == 0) return;
            for (int i = 0; i < times.Length; i++) times[i] = (float)(times[i] + offset);
        }

        /// <summary>The track's value at time t (Three's interpolants, clamped at the ends) into result.</summary>
        public void Evaluate(double t, double[] result)
        {
            int s = Stride;
            int n = times.Length;
            if (n == 0) return;
            if (t <= times[0] || n == 1)
            {
                for (int k = 0; k < s; k++) result[k] = values[k];
                return;
            }
            if (t >= times[n - 1])
            {
                for (int k = 0; k < s; k++) result[k] = values[(n - 1) * s + k];
                return;
            }
            int i1 = 1;
            while (i1 < n - 1 && times[i1] <= t) i1++;
            double t0 = times[i1 - 1], t1 = times[i1];
            if (step)
            {
                for (int k = 0; k < s; k++) result[k] = values[(i1 - 1) * s + k];
                return;
            }
            double alpha = (t - t0) / (t1 - t0);
            if (position)
            {
                double w0 = 1 - alpha;
                for (int k = 0; k < 3; k++) result[k] = values[(i1 - 1) * 3 + k] * w0 + values[i1 * 3 + k] * alpha;
                return;
            }
            int o0 = (i1 - 1) * 4, o1 = i1 * 4;
            var q = Quat.SlerpFlat(new Quat(values[o0], values[o0 + 1], values[o0 + 2], values[o0 + 3]), new Quat(values[o1], values[o1 + 1], values[o1 + 2], values[o1 + 3]), alpha);
            result[0] = q.x;
            result[1] = q.y;
            result[2] = q.z;
            result[3] = q.w;
        }
    }

    public sealed class Clip
    {
        public string name;
        public double duration;
        public List<Track> tracks = new List<Track>();

        /// <summary>Three's PropertyBinding.sanitizeNodeName: whitespace to _, and no [ ] . : /.</summary>
        public static string Sanitize(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c)) sb.Append('_');
                else if (c == '[' || c == ']' || c == '.' || c == ':' || c == '/') continue;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Every animation in a glTF file, as GLTFLoader makes them (rotation and translation tracks only).</summary>
        public static List<Clip> Read(Glb glb)
        {
            var clips = new List<Clip>();
            foreach (var a in glb.List("animations"))
            {
                var clip = new Clip { name = Json.Str(a, "name") };
                var samplers = Json.Arr(a, "samplers");
                double duration = 0;
                foreach (var ch in Json.Arr(a, "channels"))
                {
                    var target = Json.Obj(ch, "target");
                    string path = Json.Str(target, "path");
                    var sampler = samplers[(int)Json.Num(ch, "sampler")];
                    var times = glb.ReadFloats((int)Json.Num(sampler, "input"), out _);
                    if (times.Length > 0) duration = Math.Max(duration, times[times.Length - 1]);
                    if (path != "rotation" && path != "translation") continue;
                    var values = glb.ReadFloats((int)Json.Num(sampler, "output"), out _);
                    clip.tracks.Add(new Track
                    {
                        bone = Sanitize(glb.NodeName((int)Json.Num(target, "node"))),
                        position = path == "translation",
                        step = Json.Str(sampler, "interpolation") == "STEP",
                        times = times,
                        values = values,
                    });
                }
                clip.duration = duration;
                clips.Add(clip);
            }
            return clips;
        }
    }

    /// <summary>One clip playing on a mixer (AnimationAction).</summary>
    public sealed class Action
    {
        public readonly Clip clip;
        public double time;
        public double weight;
        public double timeScale = 1;
        public bool loopOnce;
        public bool clampWhenFinished;
        public bool paused;
        public bool enabled = true;
        internal int[] bindings;
        int loopCount = -1;

        internal Action(Clip clip)
        {
            this.clip = clip;
        }

        /// <summary>AnimationAction.reset() then play().</summary>
        public void Restart()
        {
            paused = false;
            enabled = true;
            time = 0;
            loopCount = -1;
        }

        internal double UpdateTime(double deltaTime)
        {
            double duration = clip.duration;
            double t = time + deltaTime;
            if (deltaTime == 0) return t;
            if (loopOnce)
            {
                if (loopCount == -1) loopCount = 0;
                if (t >= duration) t = duration;
                else if (t < 0) t = 0;
                else
                {
                    time = t;
                    return t;
                }
                if (clampWhenFinished) paused = true;
                else enabled = false;
                time = t;
                return t;
            }
            if (loopCount == -1) loopCount = 0;
            if (t >= duration || t < 0)
            {
                double loopDelta = Math.Floor(t / duration);
                t -= duration * loopDelta;
                loopCount += (int)Math.Abs(loopDelta);
            }
            time = t;
            return t;
        }
    }

    /// <summary>The bones a mixer moves, by name: read their original pose, set the mixed one.</summary>
    public interface IPoseTarget
    {
        int Find(string bone);
        void Get(int bone, bool position, double[] value);
        void Set(int bone, bool position, double[] value);
    }

    /// <summary>Three's AnimationMixer with normal (not additive) blending.</summary>
    public sealed class Mixer
    {
        sealed class Binding
        {
            public int bone;
            public bool position;
            public double[] original;
            public double[] accu;
            public double weight;
        }

        readonly IPoseTarget target;
        readonly List<Action> actions = new List<Action>();
        readonly List<Binding> bindings = new List<Binding>();
        readonly Dictionary<(int, bool), int> bindingOf = new Dictionary<(int, bool), int>();
        readonly double[] incoming = new double[4];

        public Mixer(IPoseTarget target)
        {
            this.target = target;
        }

        /// <summary>clipAction(clip).play(): bound to the bones it has, in the order actions are made.</summary>
        public Action Play(Clip clip)
        {
            var a = new Action(clip);
            var list = new List<int>();
            foreach (var track in clip.tracks)
            {
                int bone = target.Find(track.bone);
                if (bone < 0)
                {
                    list.Add(-1);
                    continue;
                }
                var key = (bone, track.position);
                if (!bindingOf.TryGetValue(key, out int b))
                {
                    b = bindings.Count;
                    var original = new double[track.Stride];
                    target.Get(bone, track.position, original);
                    bindings.Add(new Binding { bone = bone, position = track.position, original = original, accu = new double[track.Stride] });
                    bindingOf[key] = b;
                }
                list.Add(b);
            }
            a.bindings = list.ToArray();
            actions.Add(a);
            return a;
        }

        public void Update(double deltaTime)
        {
            foreach (var a in actions)
            {
                if (!a.enabled) continue;
                double scale = a.paused ? 0 : a.timeScale;
                double clipTime = a.UpdateTime(deltaTime * scale);
                double weight = a.enabled ? a.weight : 0;
                if (weight <= 0) continue;
                for (int j = 0; j < a.clip.tracks.Count; j++)
                {
                    int bi = a.bindings[j];
                    if (bi < 0) continue;
                    var track = a.clip.tracks[j];
                    track.Evaluate(clipTime, incoming);
                    Accumulate(bindings[bi], weight);
                }
            }
            foreach (var b in bindings)
            {
                double w = b.weight;
                b.weight = 0;
                if (w < 1) Mix(b, b.accu, b.original, 1 - w);
                target.Set(b.bone, b.position, b.accu);
            }
        }

        void Accumulate(Binding b, double weight)
        {
            int s = b.accu.Length;
            if (b.weight == 0)
            {
                for (int i = 0; i < s; i++) b.accu[i] = incoming[i];
                b.weight = weight;
                return;
            }
            b.weight += weight;
            Mix(b, b.accu, incoming, weight / b.weight);
        }

        static void Mix(Binding b, double[] dst, double[] src, double t)
        {
            if (b.position)
            {
                double s = 1 - t;
                for (int i = 0; i < 3; i++) dst[i] = dst[i] * s + src[i] * t;
                return;
            }
            var q = Quat.SlerpFlat(new Quat(dst[0], dst[1], dst[2], dst[3]), new Quat(src[0], src[1], src[2], src[3]), t);
            dst[0] = q.x;
            dst[1] = q.y;
            dst[2] = q.z;
            dst[3] = q.w;
        }
    }
}

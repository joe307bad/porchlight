// A race course in the town (MissionPlay's course): red and white ribbon on
// posts along both kerbs all the way, an arch over the start line and a
// chequered one over the finish, and crowds lining the road. The crowd is two
// kinds of people: a great many standing still, baked once into a few big
// meshes (posed by the people's own Animator, then merged, so thousands cost
// little to draw), and a few dozen real ones at the ribbon just where you are,
// turning to watch you go by and waving you on. Local to this player.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Avatar = Porchlight.Rendering.Avatar;

namespace Porchlight.App
{
    public sealed class RaceCourse
    {
        /// <summary>The ribbon stands this far out past the asphalt (tiles), clear of the parked cars.</summary>
        public const double FENCE = 0.04;
        /// <summary>A car this far past the asphalt (its nose through the ribbon) is on the closed road, and not drawn for the runner (tiles).</summary>
        public const double CLEAR = 0.35;
        const double TAPE_LOW = 0.15, TAPE_HIGH = 0.18, POST_HIGH = 0.2;
        const double STRIPE = 0.22;
        const int POST_EVERY = 4;
        const int RED = 0xd8342b, WHITE = 0xf3efe6, POST = 0xdedad2, BLUE = 0x2f6fd6, BLACK = 0x1d1d1f;
        /// <summary>The most people standing still along a course, however long.</summary>
        const int MOST_BAKED = 700;
        /// <summary>How many real people cheer by the ribbon around you, and how far along they reach.</summary>
        const int LIVE = 40;
        const double LIVE_REACH = 11;
        const double LIVE_STEP = 0.9;
        /// <summary>The crowd is thickest this near the start and the finish (tiles).</summary>
        const double ENDS = 30;
        /// <summary>How long the stretch each mesh covers (tiles): far ones aren't drawn.</summary>
        const double CHUNK = 60;

        struct Slot
        {
            public Vec2 at;
            public double heading;
        }

        sealed class Template
        {
            public Mesh mesh;
            public Material[] materials;
            public Matrix4x4 local;
        }

        sealed class Fan
        {
            public Avatar avatar;
            public Walker walker;
            public int slot = -1;
            public double wave;
        }

        /// <summary>How each person standing still came out of the bake: how tall, and how wide for it (arms out in a T is about 1; at ease, under half).</summary>
        public readonly List<string> Baked = new List<string>();

        readonly Course course;
        readonly ExploreView view;
        readonly Rng rng;
        readonly GameObject root;
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly List<Slot> liveSlots = new List<Slot>();
        readonly List<Fan> fans = new List<Fan>();
        readonly Dictionary<int, Fan> taken = new Dictionary<int, Fan>();
        double sinceSeated = double.PositiveInfinity;

        public RaceCourse(Course course, double crowd, ExploreView view, Rng rng)
        {
            this.course = course;
            this.view = view;
            this.rng = rng;
            root = new GameObject("race course");
            root.transform.SetParent(view.world.root.transform, false);
            var palette = new Palette();
            var sides = new[] { Offset(1, FENCE), Offset(-1, FENCE) };
            foreach (var side in sides) Fence(side, palette);
            // Closed off behind the start line and past the finish: the course is the only way.
            if (sides[0].Count > 1 && sides[1].Count > 1)
            {
                Fence(new List<(Vec2 p, double s)> { (sides[0][0].p, 1), (sides[1][0].p, 1) }, palette);
                Fence(new List<(Vec2 p, double s)> { (sides[0][sides[0].Count - 1].p, 1), (sides[1][sides[1].Count - 1].p, 1) }, palette);
            }
            Arch(0.6, palette, false);
            Arch(course.Length - 0.6, palette, true);
            crowd = Math.Max(0, Math.Min(1, crowd));
            if (crowd > 0)
            {
                Crowd(crowd);
                // Where the real people stand: at the ribbon, every so often along both sides.
                foreach (int side in new[] { 1, -1 })
                    foreach (var (p, s) in Resample(Offset(side, FENCE + 0.09), LIVE_STEP))
                        if (view.CanStandAt(p.x, p.y)) liveSlots.Add(new Slot { at = p, heading = Facing(p, s) });
                int live = (int)Math.Round(LIVE * crowd);
                for (int i = 0; i < live; i++)
                {
                    string look = Look();
                    fans.Add(new Fan { avatar = Avatar.Of(look, rng.Next(), root.transform), walker = new Walker(look, 0, 0), wave = rng.Range(0, 3) });
                    fans[i].avatar.Visible = false;
                }
            }
        }

        public void Dispose()
        {
            foreach (var f in fans) f.avatar.Dispose();
            fans.Clear();
            foreach (var m in meshes) UnityEngine.Object.Destroy(m);
            meshes.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
        }

        string Look()
        {
            string look;
            do look = Catalog.RandomLook(rng); while (look.Contains("policeman"));
            return look;
        }

        /// <summary>Facing the road from beside it, a little to one side or the other.</summary>
        double Facing(Vec2 p, double s)
        {
            var mid = course.At(s).point;
            return DetMath.Atan2(mid.x - p.x, mid.y - p.y) + rng.Range(-0.35, 0.35);
        }

        /// <summary>The course's edge on one side, `extra` past the asphalt, each point with how far along the course it is.</summary>
        List<(Vec2 p, double s)> Offset(int side, double extra) => course.Side(side, extra);

        /// <summary>Points every `step` along a line, each with how far along the course it is.</summary>
        static List<(Vec2 p, double s)> Resample(List<(Vec2 p, double s)> line, double step)
        {
            var o = new List<(Vec2, double)>();
            if (line.Count < 2) return o;
            double carry = 0;
            o.Add(line[0]);
            for (int i = 1; i < line.Count; i++)
            {
                var (a, sa) = line[i - 1];
                var (b, sb) = line[i];
                double len = Geo.Dist(a, b);
                double t = step - carry;
                while (t <= len)
                {
                    double f = t / len;
                    o.Add((Geo.Lerp(a, b, f), sa + (sb - sa) * f));
                    t += step;
                }
                carry = len - (t - step);
            }
            return o;
        }

        // The ribbon: posts, and tape between them in red and white stretches, a mesh to a stretch of course.

        void Fence(List<(Vec2 p, double s)> line, Palette palette)
        {
            var marks = Resample(line, STRIPE);
            MeshBuilder b = null;
            double chunkEnd = -1;
            int part = 0;
            void Flush()
            {
                if (b != null && !b.Empty) Keep(b.Build($"race ribbon {part++}", root.transform, false));
                b = new MeshBuilder(palette);
            }
            for (int i = 0; i + 1 < marks.Count; i++)
            {
                if (marks[i].s > chunkEnd)
                {
                    Flush();
                    chunkEnd = marks[i].s + CHUNK;
                }
                var p = marks[i].p;
                var q = marks[i + 1].p;
                int color = i % 2 == 0 ? RED : WHITE;
                var n = new V3(-(q.y - p.y), 0, q.x - p.x);
                var back = new V3(q.y - p.y, 0, -(q.x - p.x));
                foreach (var face in new[] { n, back })
                    b.Quad(MeshBuilder.v(p.x, TAPE_LOW, p.y), MeshBuilder.v(q.x, TAPE_LOW, q.y), MeshBuilder.v(q.x, TAPE_HIGH, q.y), MeshBuilder.v(p.x, TAPE_HIGH, p.y), color, face);
                if (i % POST_EVERY == 0) b.Box(p.x - 0.012, p.y - 0.012, p.x + 0.012, p.y + 0.012, 0, POST_HIGH, POST, RED);
            }
            Flush();
        }

        void Keep(GameObject go) => meshes.Add(go.GetComponent<MeshFilter>().sharedMesh);

        /// <summary>An arch over the road `s` along: posts at both kerbs and a banner across (chequered at the finish), and a line on the road.</summary>
        void Arch(double s, Palette palette, bool finish)
        {
            var (mid, along, half) = course.At(s);
            var across = new Vec2(-along.y, along.x);
            double reach = half + FENCE + 0.06;
            Vec2 Across(double d, double fwd = 0) => new Vec2(mid.x + across.x * d + along.x * fwd, mid.y + across.y * d + along.y * fwd);
            var b = new MeshBuilder(palette);
            const double TOP = 0.78, BANNER = 0.16, POLE = 0.035, DEPTH = 0.02, LINE = 0.07;
            foreach (int side in new[] { -1, 1 })
            {
                var p = Across(side * reach);
                b.Box(p.x - POLE, p.y - POLE, p.x + POLE, p.y + POLE, 0, TOP, finish ? BLACK : WHITE);
            }
            // The banner, in squares across (and the line on the road beneath, the same).
            int squares = Math.Max(6, (int)Math.Round(reach * 2 / 0.08));
            for (int row = 0; row < 2; row++)
                for (int k = 0; k < squares; k++)
                {
                    double d0 = -reach + reach * 2 * k / squares, d1 = -reach + reach * 2 * (k + 1) / squares;
                    double h0 = TOP - BANNER + row * BANNER / 2, h1 = h0 + BANNER / 2;
                    int color = finish ? ((k + row) % 2 == 0 ? BLACK : WHITE) : row == 0 ? BLUE : (k % 4 == 0 ? WHITE : BLUE);
                    foreach (int face in new[] { -1, 1 })
                    {
                        var a = Across(d0, face * DEPTH);
                        var c = Across(d1, face * DEPTH);
                        b.Quad(MeshBuilder.v(a.x, h0, a.y), MeshBuilder.v(c.x, h0, c.y), MeshBuilder.v(c.x, h1, c.y), MeshBuilder.v(a.x, h1, a.y), color, new V3(along.x * face, 0, along.y * face));
                    }
                    var g0 = Across(d0, -LINE / 2);
                    var g1 = Across(d1, -LINE / 2);
                    var g2 = Across(d1, LINE / 2);
                    var g3 = Across(d0, LINE / 2);
                    int ground = finish ? ((k + row) % 2 == 0 ? BLACK : WHITE) : WHITE;
                    if (row == 0) b.Quad(MeshBuilder.v(g0.x, 0.012, g0.y), MeshBuilder.v(g1.x, 0.012, g1.y), MeshBuilder.v(g2.x, 0.012, g2.y), MeshBuilder.v(g3.x, 0.012, g3.y), ground);
                }
            Keep(b.Build(finish ? "race finish" : "race start", root.transform, true));
        }

        // The crowd standing still: rows along both sides, thickest near the ends and in bunches between.

        void Crowd(double crowd)
        {
            var parked = view.world.layout.cars.Select(c => new Vec2(c.x, c.y)).ToList();
            bool ByACar(Vec2 p)
            {
                foreach (var c in parked) if (Math.Abs(c.x - p.x) < 0.45 && Math.Abs(c.y - p.y) < 0.45 && Geo.Dist(c, p) < 0.4) return true;
                return false;
            }
            // Rows: in the parking lane by the ribbon (between parked cars), and two along the pavement.
            var rows = new (double extra, double step)[]
            {
                (Layout.PARKING * 0.55, 0.16),
                (Layout.PARKING + Layout.SIDEWALK * 0.3, 0.14),
                (Layout.PARKING + Layout.SIDEWALK * 0.75, 0.17),
            };
            var candidates = new List<(Vec2 p, double s, double weight)>();
            double bunchPhase = rng.Range(0, 100);
            foreach (int side in new[] { 1, -1 })
                foreach (var (extra, step) in rows)
                    foreach (var (p, s) in Resample(Offset(side, extra), step))
                    {
                        double ends = Math.Max(0, 1 - Math.Min(s, course.Length - s) / ENDS);
                        // Bunches: people gather in groups, with thinner stretches between.
                        double bunch = 0.5 + 0.5 * Math.Sin(s * 0.35 + bunchPhase + side) * Math.Sin(s * 0.11 + bunchPhase * 0.7);
                        double weight = crowd * (0.25 + 0.75 * Math.Max(ends, bunch * bunch));
                        candidates.Add((p, s, weight));
                    }
            double total = candidates.Sum(c => c.weight);
            double scale = total > MOST_BAKED ? MOST_BAKED / total : 1;
            var templates = new Dictionary<string, List<Template>>();
            var chunks = new Dictionary<int, Dictionary<Material, List<CombineInstance>>>();
            foreach (var (p0, s, weight) in candidates)
            {
                if (rng.Next() >= weight * scale) continue;
                var p = new Vec2(p0.x + rng.Range(-0.03, 0.03), p0.y + rng.Range(-0.03, 0.03));
                if (!view.CanStandAt(p.x, p.y) || ByACar(p)) continue;
                string look = Look();
                if (!templates.TryGetValue(look, out var parts)) templates[look] = parts = Bake(look);
                double size = rng.Range(0.88, 1.08);
                var at = Matrix4x4.TRS(Coords.Map(p.x, p.y, 0.035), Coords.Yaw(Facing(p, s)), Vector3.one * (float)size);
                int chunk = (int)(s / CHUNK);
                if (!chunks.TryGetValue(chunk, out var byMaterial)) chunks[chunk] = byMaterial = new Dictionary<Material, List<CombineInstance>>();
                foreach (var t in parts)
                    for (int sub = 0; sub < t.mesh.subMeshCount && sub < t.materials.Length; sub++)
                    {
                        if (!byMaterial.TryGetValue(t.materials[sub], out var list)) byMaterial[t.materials[sub]] = list = new List<CombineInstance>();
                        list.Add(new CombineInstance { mesh = t.mesh, subMeshIndex = sub, transform = at * t.local });
                    }
            }
            foreach (var chunk in chunks)
                foreach (var kv in chunk.Value)
                {
                    var mesh = new Mesh { name = $"race crowd {chunk.Key}", indexFormat = IndexFormat.UInt32 };
                    mesh.CombineMeshes(kv.Value.ToArray(), true, true);
                    meshes.Add(mesh);
                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = kv.Key;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                }
            foreach (var t in templates.Values.SelectMany(l => l)) UnityEngine.Object.Destroy(t.mesh);
        }

        /// <summary>Someone who looks like `look`, standing at ease, as meshes in the pose their Animator gives them.</summary>
        List<Template> Bake(string look)
        {
            var holder = new GameObject("race crowd pose");
            var avatar = Avatar.Of(look, rng.Next(), holder.transform);
            // Posed by their Animator: made to run even off screen (people cull theirs when no one sees
            // them, which would leave them in the T of their bind pose), into a moment of standing at ease.
            foreach (var animator in avatar.root.GetComponentsInChildren<Animator>(true))
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Update(0);
                animator.Update((float)rng.Range(0.3, 2.5));
            }
            var into = avatar.root.transform.worldToLocalMatrix;
            var parts = new List<Template>();
            foreach (var smr in avatar.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                // Baked in the renderer's own space without its scale, which its full matrix then puts back.
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                smr.BakeMesh(mesh, false);
                parts.Add(new Template { mesh = mesh, materials = smr.sharedMaterials, local = into * smr.transform.localToWorldMatrix });
            }
            foreach (var mf in avatar.root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mf.sharedMesh == null) continue;
                parts.Add(new Template { mesh = UnityEngine.Object.Instantiate(mf.sharedMesh), materials = mr.sharedMaterials, local = into * mf.transform.localToWorldMatrix });
            }
            avatar.Dispose();
            UnityEngine.Object.Destroy(holder);
            // A person their usual height, standing on the ground: measured, and put right if the bake wasn't.
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity, wide = 0;
            foreach (var t in parts)
            {
                var b = t.mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    var c = t.local.MultiplyPoint3x4(new Vector3((corner & 1) == 0 ? b.min.x : b.max.x, (corner & 2) == 0 ? b.min.y : b.max.y, (corner & 4) == 0 ? b.min.z : b.max.z));
                    lo = Math.Min(lo, c.y);
                    hi = Math.Max(hi, c.y);
                    wide = Math.Max(wide, Math.Max(Math.Abs(c.x), Math.Abs(c.z)) * 2);
                }
            }
            // Exactly as tall as the same person walking about (sized by where their head starts, as an Avatar is),
            // whatever scale the bake came out at, with their feet on the ground.
            float tall = hi - lo;
            var person = ModelLibrary.Instance.Character(look);
            double h = !float.IsNaN(person.headY) ? person.headY / 0.71 : Math.Max(1e-3, person.maxY - person.minY);
            float should = (float)((person.maxY - person.minY) * Avatar.PERSON_HEIGHT / h);
            float k = should / Math.Max(1e-4f, tall);
            var fix = Matrix4x4.Translate(new Vector3(0, -lo * k, 0)) * Matrix4x4.Scale(Vector3.one * k);
            foreach (var t in parts) t.local = fix * t.local;
            Baked.Add($"{look.Substring(look.IndexOf('/') + 1)} baked {tall:0.00} tall, stands {should:0.00}, {wide / Math.Max(1e-4f, tall):0.00} wide for its height");
            return parts;
        }

        // The real people by the ribbon, wherever you are.

        public void Update(double dt, Vec2? you)
        {
            if (fans.Count == 0) return;
            sinceSeated += dt;
            if (you != null && sinceSeated > 0.3)
            {
                sinceSeated = 0;
                Seat(you.Value);
            }
            foreach (var f in fans)
            {
                if (f.slot < 0) continue;
                var w = f.walker;
                // Watching you go by.
                if (you != null && Geo.Dist(you.Value, new Vec2(w.x, w.y)) < 6) w.heading += Orbit.ShortestTurn(w.heading, DetMath.Atan2(you.Value.x - w.x, you.Value.y - w.y)) * Math.Min(1, dt * 4);
                w.Moved(0, 0, dt);
                f.wave -= dt;
                if (f.wave <= 0)
                {
                    f.wave = rng.Range(1.2, 4);
                    f.avatar.SetWaving(rng.Next() < 0.6);
                }
                f.avatar.Update(w, dt);
            }
        }

        /// <summary>The people go to the places by the ribbon nearest you: those still near stay put, the rest move up.</summary>
        void Seat(Vec2 you)
        {
            var near = new List<(int slot, double d)>();
            for (int i = 0; i < liveSlots.Count; i++)
            {
                double d = Geo.Dist(liveSlots[i].at, you);
                if (d < LIVE_REACH) near.Add((i, d));
            }
            var wanted = new HashSet<int>(near.OrderBy(n => n.d).Take(fans.Count).Select(n => n.slot));
            foreach (var f in fans)
            {
                if (f.slot < 0 || wanted.Contains(f.slot)) continue;
                taken.Remove(f.slot);
                f.slot = -1;
                f.avatar.Visible = false;
            }
            foreach (int slot in wanted)
            {
                if (taken.ContainsKey(slot)) continue;
                var f = fans.FirstOrDefault(o => o.slot < 0);
                if (f == null) break;
                var s = liveSlots[slot];
                f.slot = slot;
                taken[slot] = f;
                f.walker.x = s.at.x;
                f.walker.y = s.at.y;
                f.walker.heading = s.heading;
                f.avatar.SnapYaw(s.heading);
                f.avatar.Visible = true;
            }
        }
    }
}

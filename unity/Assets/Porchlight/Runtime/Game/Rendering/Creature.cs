// An animal, made here rather than loaded: a skeleton of joints (body, neck,
// head, legs with a knee each, a tail, ears, a trunk, wings) and over it one
// low-poly mesh of faceted pieces, each piece riding a single joint, skinned
// so the whole animal draws in one go. Sculpt makes them (Creatures.cs says
// what each animal is made of); Creature moves them each frame: legs swing
// in a walk that turns into a trot as it hurries (or a waddle, for the birds),
// the body bobs, the head glances about when it's standing still, tails sway,
// ears flap, and in the air the legs tuck and the wings beat.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public sealed class Creature
    {
        public enum Gait
        {
            /// <summary>Four legs: a walk (one foot at a time) that becomes a trot.</summary>
            Four,
            /// <summary>Two legs and a rolling body (the penguin, the parrot).</summary>
            Waddle,
        }

        public sealed class Leg
        {
            public Transform hip, knee;
            /// <summary>Where in the stride it is (0..1), walking and trotting.</summary>
            public float walk, trot;
            /// <summary>How far it swings and its knee bends (degrees).</summary>
            public float swing = 26, bend = 40;
            /// <summary>How it's held in the air (degrees, + back).</summary>
            public float tuck;
            internal Quaternion hipRest, kneeRest;
        }

        public sealed class Swing
        {
            public Transform joint;
            /// <summary>-1 on the left, 1 on the right (or 0, for something in the middle).</summary>
            public float side;
            internal Quaternion rest;
            internal float offset;
        }

        public readonly GameObject root;
        public Gait gait = Gait.Four;
        /// <summary>One stride's length (the animal's own units), and the most strides a second.</summary>
        public float stride = 1, cadenceTop = 3;
        /// <summary>How far the body bobs (units) and rolls (degrees) in a stride, and how far the head nods.</summary>
        public float bob = 0.02f, roll, nod = 3;
        public Transform body, neck, head;
        public readonly List<Leg> legs = new List<Leg>();
        public readonly List<Swing> tail = new List<Swing>(), trunk = new List<Swing>(), ears = new List<Swing>(), wings = new List<Swing>();
        /// <summary>Tail sway (degrees) and rate; ear flap (degrees) and rate; how far the wings are held out, and beat, in the air.</summary>
        public float tailSway = 12, tailRate = 2.2f, earFlap = 4, earRate = 0.7f, wingOut = 8, wingBeat = 45;
        /// <summary>How big it is (units), for breathing.</summary>
        public float size = 1;

        /// <summary>Its lowest and highest points standing (the animal's own units).</summary>
        public float minY, maxY;

        Vector3 bodyRest;
        Quaternion bodyTurn = Quaternion.identity, neckRest = Quaternion.identity, headRest = Quaternion.identity;
        float t, phase, move, air;
        float lookYaw, lookPitch, wantYaw, wantPitch, nextLook;
        readonly System.Random rng;

        public Creature(string name, double seed)
        {
            root = new GameObject("animal " + name);
            rng = new System.Random((int)(seed * 1e6));
            t = (float)(seed * 100);
            phase = (float)seed;
        }

        /// <summary>Note how everything stands, once it's all been made.</summary>
        internal void Rest()
        {
            if (body != null)
            {
                bodyRest = body.localPosition;
                bodyTurn = body.localRotation;
            }
            if (neck != null) neckRest = neck.localRotation;
            if (head != null) headRest = head.localRotation;
            foreach (var l in legs)
            {
                l.hipRest = l.hip.localRotation;
                l.kneeRest = l.knee != null ? l.knee.localRotation : Quaternion.identity;
            }
            foreach (var list in new[] { tail, trunk, ears, wings })
                for (int i = 0; i < list.Count; i++)
                {
                    list[i].rest = list[i].joint.localRotation;
                    list[i].offset = i * 0.75f + (float)rng.NextDouble() * 0.3f;
                }
        }

        static float Ease(float from, float to, float rate, float dt) => from + (to - from) * (1 - Mathf.Exp(-rate * dt));

        /// <summary>Move for a step of `dt` going `speed` (its own units a second), on the ground or in the air.</summary>
        public void Update(float speed, bool inAir, float dt)
        {
            t += dt;
            bool moving = speed > 0.05f;
            move = Ease(move, moving ? 1 : 0, moving ? 8 : 5, dt);
            air = Ease(air, inAir ? 1 : 0, inAir ? 14 : 9, dt);
            // Strides come quicker as it hurries, up to a point; past that they lengthen.
            float top = stride * cadenceTop;
            float hurry = speed / top;
            float trot = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.35f, 0.8f, hurry));
            float reach = 1 + 0.5f * trot + 0.3f * Mathf.Clamp01(hurry - 1);
            if (moving && !inAir) phase += Mathf.Min(speed / stride, cadenceTop) * dt;
            float ground = move * (1 - air);
            float tau = Mathf.PI * 2;

            foreach (var l in legs)
            {
                float p = tau * (phase + Mathf.Lerp(l.walk, l.trot, trot));
                float s = Mathf.Sin(p), c = Mathf.Cos(p);
                // Forward is a turn back about x (Unity's +x turn tips a hanging leg backwards);
                // the knee folds while the foot comes forward, off the ground.
                float hip = -l.swing * reach * s * ground + l.tuck * air;
                float knee = l.bend * Mathf.Max(0, c) * ground + l.bend * 0.7f * air;
                l.hip.localRotation = l.hipRest * Quaternion.Euler(hip, 0, 0);
                if (l.knee != null) l.knee.localRotation = l.kneeRest * Quaternion.Euler(knee, 0, 0);
            }

            if (body != null)
            {
                float up = bob * ground * (0.5f - 0.5f * Mathf.Cos(tau * 2 * phase)) * reach;
                float breathe = Mathf.Sin(t * 2.1f) * 0.006f * size;
                body.localPosition = bodyRest + new Vector3(0, up + breathe, 0);
                float rollNow = roll * ground * Mathf.Sin(tau * phase);
                float pitch = gait == Gait.Four ? -2.5f * ground * Mathf.Sin(tau * 2 * phase) : 0;
                body.localRotation = bodyTurn * Quaternion.Euler(pitch, 0, rollNow);
            }

            // Standing still, it looks about now and then; walking, ahead.
            if (t >= nextLook)
            {
                bool idle = move < 0.3f;
                wantYaw = idle && rng.NextDouble() < 0.75 ? (float)(rng.NextDouble() * 2 - 1) * 40 : 0;
                wantPitch = idle ? (float)(rng.NextDouble() * 2 - 1) * 12 : 0;
                nextLook = t + 1.2f + (float)rng.NextDouble() * 3.5f;
            }
            float settle = move > 0.5f ? 0 : 1;
            lookYaw = Ease(lookYaw, wantYaw * settle, 5, dt);
            lookPitch = Ease(lookPitch, wantPitch * settle, 5, dt);
            float nodNow = nod * ground * Mathf.Sin(tau * 2 * phase + 0.6f);
            if (neck != null)
            {
                neck.localRotation = neckRest * Quaternion.Euler(-nodNow * 0.6f + Mathf.Sin(t * 0.9f) * 1.5f, lookYaw * 0.45f, 0);
                if (head != null) head.localRotation = headRest * Quaternion.Euler(nodNow + lookPitch, lookYaw * 0.55f, lookYaw * 0.12f);
            }
            else if (head != null) head.localRotation = headRest * Quaternion.Euler(nodNow + lookPitch, lookYaw, lookYaw * 0.15f);

            for (int i = 0; i < tail.Count; i++)
            {
                var j = tail[i];
                float share = 0.5f + 0.5f * (i + 1) / tail.Count;
                float yaw = (tailSway + 8 * move) * share * Mathf.Sin(t * (tailRate + 2 * move) - j.offset);
                j.joint.localRotation = j.rest * Quaternion.Euler(-12 * air * share, yaw, 0);
            }
            for (int i = 0; i < trunk.Count; i++)
            {
                var j = trunk[i];
                float pitch = Mathf.Sin(t * 1.3f - j.offset) * 5 + 7 * ground * Mathf.Sin(tau * phase - j.offset) - 10 * air;
                j.joint.localRotation = j.rest * Quaternion.Euler(pitch, Mathf.Sin(t * 0.7f - j.offset) * 6, 0);
            }
            foreach (var e in ears)
            {
                float flap = earFlap * (0.5f + 0.5f * Mathf.Sin(t * earRate * tau + e.offset));
                e.joint.localRotation = e.rest * Quaternion.Euler(0, e.side * flap, 0);
            }
            foreach (var w in wings)
            {
                float beat = air * (wingBeat * 0.5f + wingBeat * 0.5f * Mathf.Sin(t * 26));
                float paddle = gait == Gait.Waddle ? 10 * ground * Mathf.Abs(Mathf.Sin(tau * phase)) : 0;
                w.joint.localRotation = w.rest * Quaternion.Euler(0, 0, w.side * (wingOut * move + paddle + beat));
            }
        }

        public void Dispose()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }

    /// <summary>
    /// Makes a Creature: joints, and faceted pieces on them (in each joint's own space,
    /// as it stands when made), then one skinned mesh of the lot, coloured from a palette
    /// all the animals share. +z is the animal's front, +y up, and its feet stand at y 0.
    /// </summary>
    public sealed class Sculpt
    {
        static Palette palette;
        /// <summary>Each animal's mesh, made once and shared by every one of its kind (they're made alike).</summary>
        static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();

        public readonly Creature creature;
        readonly Transform root;
        readonly List<Transform> bones = new List<Transform>();
        readonly Dictionary<Transform, int> boneOf = new Dictionary<Transform, int>();
        readonly List<Vector3> pos = new List<Vector3>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<BoneWeight> weights = new List<BoneWeight>();

        public Sculpt(Creature creature)
        {
            this.creature = creature;
            root = creature.root.transform;
            palette ??= new Palette();
        }

        /// <summary>A ring along a piece: how far along (0..1), its half-width and half-height, and how far it's lifted.</summary>
        public static Vector4 R(float at, float rx, float ry, float lift = 0) => new Vector4(at, rx, ry, lift);

        /// <summary>A joint at `at` in its parent's space (the animal's, if none), turned by `euler` degrees.</summary>
        public Transform Joint(string name, Transform parent, Vector3 at, Vector3 euler = default)
        {
            var j = new GameObject(name).transform;
            j.SetParent(parent != null ? parent : root, false);
            j.localPosition = at;
            j.localRotation = Quaternion.Euler(euler);
            boneOf[j] = bones.Count;
            bones.Add(j);
            return j;
        }

        void Tri(int bone, Vector2 color, Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-14f) return;
            // Wound to face away from the inside of its piece.
            if (Vector3.Dot(n, (a + b + c) / 3 - inside) < 0) (b, c) = (c, b);
            pos.Add(a);
            pos.Add(b);
            pos.Add(c);
            var w = new BoneWeight { boneIndex0 = bone, weight0 = 1 };
            for (int k = 0; k < 3; k++)
            {
                uv.Add(color);
                weights.Add(w);
            }
        }

        /// <summary>
        /// A faceted tube from a to b (in the joint's space): a ring of `sides` corners at each
        /// of `rings`, caps at the ends. Width runs across it, height up (or, for an upright
        /// piece, front to back). `spike` pushes every other corner out (a shaggy mane).
        /// </summary>
        public void Loft(Transform joint, int color, Vector3 a, Vector3 b, int sides, float spike, params Vector4[] rings)
        {
            int bone = boneOf[joint];
            var m = root.worldToLocalMatrix * joint.localToWorldMatrix;
            var tint = palette.UV(color);
            var d = b - a;
            var dir = d.normalized;
            Vector3 u, v;
            if (Mathf.Abs(dir.y) > 0.97f)
            {
                v = (Vector3.forward - dir * Vector3.Dot(Vector3.forward, dir)).normalized;
                u = Vector3.Cross(v, dir).normalized;
            }
            else
            {
                u = Vector3.Cross(Vector3.up, dir).normalized;
                v = Vector3.Cross(dir, u);
            }
            var ring = new Vector3[rings.Length][];
            var mid = new Vector3[rings.Length];
            for (int i = 0; i < rings.Length; i++)
            {
                var r = rings[i];
                mid[i] = a + d * r.x + v * r.w;
                ring[i] = new Vector3[sides];
                for (int k = 0; k < sides; k++)
                {
                    float th = Mathf.PI * 2 * k / sides + Mathf.PI / 2;
                    float s = k % 2 == 1 ? spike : 1;
                    ring[i][k] = m.MultiplyPoint3x4(mid[i] + u * (Mathf.Cos(th) * r.y * s) + v * (Mathf.Sin(th) * r.z * s));
                }
                mid[i] = m.MultiplyPoint3x4(mid[i]);
            }
            for (int i = 0; i + 1 < rings.Length; i++)
            {
                var inside = (mid[i] + mid[i + 1]) / 2;
                for (int k = 0; k < sides; k++)
                {
                    int k1 = (k + 1) % sides;
                    Tri(bone, tint, ring[i][k], ring[i + 1][k], ring[i + 1][k1], inside);
                    Tri(bone, tint, ring[i][k], ring[i + 1][k1], ring[i][k1], inside);
                }
            }
            var axis = m.MultiplyVector(dir) * 0.001f;
            int last = rings.Length - 1;
            for (int k = 0; k < sides; k++)
            {
                int k1 = (k + 1) % sides;
                Tri(bone, tint, mid[0], ring[0][k], ring[0][k1], mid[0] + axis);
                Tri(bone, tint, mid[last], ring[last][k], ring[last][k1], mid[last] - axis);
            }
        }

        public void Loft(Transform joint, int color, Vector3 a, Vector3 b, params Vector4[] rings) => Loft(joint, color, a, b, 6, 1, rings);

        /// <summary>A rounded lump centred on `at`, `length` long along `axis` and `rx` by `ry` across.</summary>
        public void Ball(Transform joint, int color, Vector3 at, Vector3 axis, float length, float rx, float ry, int sides = 6, float spike = 1)
        {
            var h = axis.normalized * (length / 2);
            Loft(joint, color, at - h, at + h, sides, spike, R(0, 0, 0), R(0.18f, rx * 0.72f, ry * 0.72f), R(0.5f, rx, ry), R(0.82f, rx * 0.72f, ry * 0.72f), R(1, 0, 0));
        }

        /// <summary>A small gem (an eye, a nose, a spot): a double pyramid along `axis`.</summary>
        public void Gem(Transform joint, int color, Vector3 at, Vector3 axis, float length, float rx, float ry, int sides = 5)
        {
            var h = axis.normalized * (length / 2);
            Loft(joint, color, at - h, at + h, sides, 1, R(0, 0, 0), R(0.5f, rx, ry), R(1, 0, 0));
        }

        /// <summary>Skin it: one mesh over every joint, drawn with the shared palette.</summary>
        public Creature Finish()
        {
            var skin = new GameObject("skin");
            skin.transform.SetParent(root, false);
            string name = creature.root.name;
            if (!meshes.TryGetValue(name, out var mesh) || mesh == null)
            {
                mesh = new Mesh { name = name, indexFormat = pos.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(pos);
                mesh.SetUVs(0, uv);
                mesh.boneWeights = weights.ToArray();
                var tris = new int[pos.Count];
                for (int i = 0; i < tris.Length; i++) tris[i] = i;
                mesh.SetTriangles(tris, 0, true);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var binds = new Matrix4x4[bones.Count];
                for (int i = 0; i < bones.Count; i++) binds[i] = bones[i].worldToLocalMatrix * skin.transform.localToWorldMatrix;
                mesh.bindposes = binds;
                meshes[name] = mesh;
            }
            var r = skin.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = mesh;
            r.bones = bones.ToArray();
            r.rootBone = root;
            r.quality = SkinQuality.Bone1;
            r.sharedMaterial = palette.Material;
            r.shadowCastingMode = ShadowCastingMode.On;
            var b = mesh.bounds;
            b.Expand(b.size.magnitude * 0.4f);
            r.localBounds = b;
            creature.minY = mesh.bounds.min.y;
            creature.maxY = mesh.bounds.max.y;
            creature.Rest();
            return creature;
        }
    }
}

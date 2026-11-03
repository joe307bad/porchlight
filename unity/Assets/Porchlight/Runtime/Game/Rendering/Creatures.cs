// What each animal is made of (Creature.cs makes and moves them): a body,
// legs with knees, a neck and head, a tail, and whatever makes it itself —
// the lion's shaggy mane and tufted tail, the tiger's stripes, the giraffe's
// neck, spots and ossicones, the elephant's trunk, tusks and flapping ears,
// the panda's patches, the koala's ears and nose, the monkey's curled tail,
// the penguin's flippers and the parrot's wings. A few faceted pieces each,
// in a handful of flat colours. Built in the animal's own units (feet at 0,
// +z its front) and sized to its height by the Avatar.

using System;
using UnityEngine;
using static Porchlight.Rendering.Sculpt;

namespace Porchlight.Rendering
{
    public static class Creatures
    {
        const int EYE = 0x1c1a19;

        /// <summary>An animal by its look (Catalog.ANIMALS: "animals/animal-lion").</summary>
        public static Creature Build(string look, double seed)
        {
            string kind = look.Substring(look.LastIndexOf('-') + 1);
            var s = new Sculpt(new Creature(kind, seed));
            switch (kind)
            {
                case "lion": Lion(s); break;
                case "tiger": Tiger(s); break;
                case "elephant": Elephant(s); break;
                case "giraffe": Giraffe(s); break;
                case "panda": Panda(s); break;
                case "polar": Polar(s); break;
                case "koala": Koala(s); break;
                case "monkey": Monkey(s); break;
                case "penguin": Penguin(s); break;
                case "parrot": Parrot(s); break;
                default: throw new Exception("no animal " + look);
            }
            return s.Finish();
        }

        // Shapes, as rings (along, width, height, lift) in shares of a piece's own size. --------

        /// <summary>A body, tail to chest: a rounded rump, full in the middle, curving in to the chest.</summary>
        static readonly Vector4[] TORSO =
        {
            R(0, 0.25f, 0.3f), R(0.04f, 0.6f, 0.66f), R(0.1f, 0.82f, 0.86f), R(0.2f, 0.95f, 0.97f), R(0.45f, 1, 1),
            R(0.75f, 0.97f, 0.98f), R(0.88f, 0.86f, 0.9f, 0.03f), R(0.96f, 0.62f, 0.7f, 0.06f), R(1, 0.3f, 0.4f, 0.08f),
        };
        /// <summary>Corners round a body: plenty, so its back is round rather than a ridge.</summary>
        const int ROUND = 12;
        /// <summary>A head, back to front, dipping a little towards the face.</summary>
        static readonly Vector4[] SKULL = { R(0, 0.5f, 0.5f), R(0.2f, 0.9f, 0.92f), R(0.55f, 1, 1), R(0.85f, 0.85f, 0.82f, -0.05f), R(1, 0.55f, 0.55f, -0.1f) };
        const float SKULL_BACK = 0.45f, SKULL_FRONT = 0.5f;

        static Vector4[] Scaled(Vector4[] shape, float w, float h, float bulge = 1)
        {
            var o = new Vector4[shape.Length];
            for (int i = 0; i < shape.Length; i++) o[i] = R(shape[i].x, shape[i].y * w * bulge, shape[i].z * h * bulge, shape[i].w * h);
            return o;
        }

        static Vector4 At(Vector4[] shape, float t)
        {
            for (int i = 1; i < shape.Length; i++)
                if (t <= shape[i].x)
                    return Vector4.Lerp(shape[i - 1], shape[i], Mathf.InverseLerp(shape[i - 1].x, shape[i].x, t));
            return shape[shape.Length - 1];
        }

        /// <summary>A point on a piece of this shape lying along z from z0 to z1: at z, `angle` degrees up from its side.</summary>
        static Vector3 On(Vector4[] shape, float z0, float z1, float w, float h, float z, float angle, float side, float depth = 0.95f)
        {
            var r = At(shape, Mathf.InverseLerp(z0, z1, z));
            float a = angle * Mathf.Deg2Rad;
            return new Vector3(side * Mathf.Cos(a) * r.y * w * depth, Mathf.Sin(a) * r.z * h * depth + r.w * h, z);
        }

        static Vector3 Out(float angle, float side, float forward = 0)
        {
            float a = angle * Mathf.Deg2Rad;
            return new Vector3(side * Mathf.Cos(a), Mathf.Sin(a), forward);
        }

        static Vector3 Dir(float up) => new Vector3(0, Mathf.Sin(up * Mathf.Deg2Rad), Mathf.Cos(up * Mathf.Deg2Rad));
        static Vector3 Back(float up) => new Vector3(0, Mathf.Sin(up * Mathf.Deg2Rad), -Mathf.Cos(up * Mathf.Deg2Rad));

        // Four legs ---------------------------------------------------------------------------

        sealed class Q
        {
            /// <summary>Body length, half-width, half-height; legs (below the body); legs' half-thickness; tilt (degrees, chest up).</summary>
            public float len, w, h, legs, legW, pitch;
            public float neckLen, neckUp = 30, neckW;
            public float headLen, headW, headH, headUp;
            public float snout, snoutW, snoutH;
            public float tailLen, tailW, tailUp = -20, tailCurl;
            public int tailSegs = 3;
            public float swing = 24;
            public int main, belly, feet, nose = 0x2e2522, muzzle;
            /// <summary>The legs' colour, if not the body's.</summary>
            public int leg = -1;
            /// <summary>The tail's segments' colours in turn, if not the body's.</summary>
            public int[] tailColors;
        }

        /// <summary>A four-legged animal's body, legs, neck, head, face and tail; the rest is the animal's own.</summary>
        static void Four(Sculpt s, Q q)
        {
            var c = s.creature;
            c.gait = Creature.Gait.Four;
            c.size = q.len;
            var body = c.body = s.Joint("body", null, new Vector3(0, q.legs + q.h, 0), new Vector3(-q.pitch, 0, 0));
            s.Loft(body, q.main, new Vector3(0, 0, -q.len / 2), new Vector3(0, 0, q.len / 2), ROUND, 1, Scaled(TORSO, q.w, q.h));
            s.Loft(body, q.belly, new Vector3(0, 0, -q.len * 0.36f), new Vector3(0, 0, q.len * 0.36f), ROUND, 1,
                R(0, 0.45f * q.w, 0.5f * q.h, -0.24f * q.h), R(0.3f, 0.72f * q.w, 0.77f * q.h, -0.24f * q.h), R(0.7f, 0.72f * q.w, 0.77f * q.h, -0.24f * q.h), R(1, 0.45f * q.w, 0.5f * q.h, -0.24f * q.h));

            // Legs: a walk goes back left, front left, back right, front right; a trot in diagonal pairs.
            float lw = q.legW;
            int legColor = q.leg >= 0 ? q.leg : q.main;
            float sumL = 0;
            foreach (var (side, front, walk, trot) in new[] { (-1f, false, 0f, 0f), (-1f, true, 0.25f, 0.5f), (1f, false, 0.5f, 0.5f), (1f, true, 0.75f, 0f) })
            {
                var hip = s.Joint(front ? "front leg" : "back leg", body, new Vector3(side * q.w * 0.6f, -q.h * 0.3f, (front ? 1 : -1) * q.len * 0.32f), new Vector3(q.pitch, 0, 0));
                float L = hip.position.y;
                sumL += L;
                float upper = L * 0.5f, foot = lw * 0.9f;
                // A round shoulder (or haunch) where it meets the body, and a round knee to bend at.
                s.Knob(hip, legColor, new Vector3(0, lw * 0.6f, 0), 1.4f * lw);
                s.Loft(hip, legColor, new Vector3(0, lw, 0), new Vector3(0, -upper, 0), R(0, 1.3f * lw, 1.45f * lw), R(0.5f, 1.08f * lw, 1.15f * lw), R(1, 0.88f * lw, 0.92f * lw));
                var knee = s.Joint("knee", hip, new Vector3(0, -upper, 0));
                s.Knob(knee, legColor, Vector3.zero, 0.98f * lw);
                float lower = L - upper;
                s.Loft(knee, legColor, new Vector3(0, lw * 0.4f, 0), new Vector3(0, -lower + foot, 0), R(0, 0.88f * lw, 0.92f * lw), R(1, 0.76f * lw, 0.8f * lw));
                s.Loft(knee, q.feet, new Vector3(0, -lower + foot, 0), new Vector3(0, -lower, 0), R(0, 0.78f * lw, 0.82f * lw), R(0.55f, 0.98f * lw, 1.15f * lw, 0.2f * lw), R(1, 0.92f * lw, 1.08f * lw, 0.25f * lw));
                c.legs.Add(new Creature.Leg { hip = hip, knee = knee, walk = walk, trot = trot, swing = q.swing, bend = front ? 45 : 35, tuck = front ? -35 : 30 });
            }
            float legL = sumL / 4;
            c.stride = legL * 1.7f;
            c.cadenceTop = Mathf.Clamp(1.4f / Mathf.Sqrt(legL), 1.8f, 3.6f);
            c.bob = legL * 0.05f;

            // The neck, then the head held level (or as tipped) whatever the body's tilt.
            var up = Dir(q.neckUp - q.pitch);
            var neck = c.neck = s.Joint("neck", body, new Vector3(0, q.h * 0.4f, q.len * 0.38f));
            s.Loft(neck, q.main, -up * q.neckW * 0.6f, up * q.neckLen, R(0, 1.1f * q.neckW, 1.2f * q.neckW), R(1, 0.85f * q.neckW, 0.9f * q.neckW));
            var head = c.head = s.Joint("head", neck, up * q.neckLen, new Vector3(q.pitch - q.headUp, 0, 0));
            s.Loft(head, q.main, new Vector3(0, 0, -q.headLen * SKULL_BACK), new Vector3(0, 0, q.headLen * SKULL_FRONT), Scaled(SKULL, q.headW, q.headH));
            if (q.snout > 0)
            {
                float y0 = -q.headH * 0.3f, y1 = -q.headH * 0.36f, tip = q.headLen * 0.5f + q.snout;
                s.Loft(head, q.muzzle, new Vector3(0, y0, q.headLen * 0.2f), new Vector3(0, y1, tip), R(0, q.snoutW, q.snoutH), R(0.7f, 0.95f * q.snoutW, 0.9f * q.snoutH), R(1, 0.7f * q.snoutW, 0.65f * q.snoutH));
                s.Gem(head, q.nose, new Vector3(0, y1 + q.snoutH * 0.5f, tip - q.snoutW * 0.15f), Vector3.forward, q.snoutW * 0.7f, q.snoutW * 0.5f, q.snoutH * 0.32f);
            }
            Eyes(s, head, q.headLen, q.headW, q.headH, 0.3f, 28);

            if (q.tailLen > 0)
                Tail(s, body, new Vector3(0, q.h * 0.45f, -q.len * 0.47f), q.tailLen, q.tailW, q.tailSegs, q.tailUp, q.tailCurl, q.tailColors ?? new[] { q.main });
        }

        /// <summary>Two eyes on a head (SKULL-shaped): `at` the share of the way forward, `angle` degrees up.</summary>
        static void Eyes(Sculpt s, Transform head, float len, float w, float h, float at, float angle, float size = 1, int color = EYE)
        {
            float z = len * at;
            foreach (float side in new[] { -1f, 1f })
            {
                var p = On(SKULL, -len * SKULL_BACK, len * SKULL_FRONT, w, h, z, angle, side);
                s.Gem(head, color, p, Out(angle, side, 0.35f), w * 0.3f * size, w * 0.13f * size, h * 0.16f * size);
            }
        }

        /// <summary>A tail of `segs` joints, `up` degrees above straight back and curling `curl` more each joint.</summary>
        static void Tail(Sculpt s, Transform from, Vector3 at, float len, float w, int segs, float up, float curl, int[] colors, int tuft = -1, float tuftSize = 0)
        {
            var c = s.creature;
            Transform parent = from;
            var pos = at;
            float seg = len / segs;
            for (int i = 0; i < segs; i++)
            {
                var j = s.Joint("tail", parent, pos);
                var d = Back(up + curl * i);
                float r0 = w * (1 - 0.45f * i / segs), r1 = w * (1 - 0.45f * (i + 1) / segs);
                s.Loft(j, colors[i % colors.Length], -d * r0 * 0.8f, d * seg, 6, 1, R(0, r0, r0), R(1, r1, r1));
                if (i > 0) s.Knob(j, colors[i % colors.Length], Vector3.zero, r0 * 1.05f);
                c.tail.Add(new Creature.Swing { joint = j });
                parent = j;
                pos = d * seg;
            }
            if (tuft >= 0) s.Ball(parent, tuft, pos, Back(up + curl * segs), tuftSize * 1.4f, tuftSize * 0.6f, tuftSize * 0.6f, 6, 1.2f);
        }

        /// <summary>A pointed ear (a four-sided pyramid), tipped out from upright by `lean` degrees.</summary>
        static void PointedEar(Sculpt s, Transform head, Vector3 at, float side, float tall, float wide, int color, float lean, int inner = -1)
        {
            var j = s.Joint("ear", head, at, new Vector3(-10, 0, -side * lean));
            s.Loft(j, color, new Vector3(0, -tall * 0.2f, 0), new Vector3(0, tall, 0), 4, 1, R(0, wide, wide * 0.35f), R(0.5f, wide * 0.7f, wide * 0.3f), R(1, 0, 0));
            if (inner >= 0) s.Loft(j, inner, new Vector3(0, 0, wide * 0.12f), new Vector3(0, tall * 0.75f, wide * 0.12f), 4, 1, R(0, wide * 0.6f, wide * 0.25f), R(1, 0, 0));
            s.creature.ears.Add(new Creature.Swing { joint = j, side = side });
        }

        /// <summary>A round ear (a flattened lump facing forward), with a paler middle if `inner`.</summary>
        static void RoundEar(Sculpt s, Transform head, Vector3 at, float side, float r, int color, int inner = -1, float spike = 1)
        {
            var j = s.Joint("ear", head, at, new Vector3(0, side * 20, 0));
            s.Ball(j, color, Vector3.zero, Vector3.forward, r * 0.7f, r, r, 7, spike);
            if (inner >= 0) s.Ball(j, inner, new Vector3(0, 0, r * 0.18f), Vector3.forward, r * 0.45f, r * 0.62f, r * 0.62f, 7);
            s.creature.ears.Add(new Creature.Swing { joint = j, side = side });
        }

        /// <summary>Bands round a body (TORSO-shaped) at each z, a little proud of it: stripes, or a panda's saddle.</summary>
        static void Bands(Sculpt s, Transform body, Q q, int color, float width, params float[] zs)
        {
            foreach (float z in zs)
            {
                // Rings sampled along the body, so the band follows its swell.
                var rings = new Vector4[5];
                for (int i = 0; i < rings.Length; i++)
                {
                    float f = i / (rings.Length - 1f);
                    var r = At(TORSO, Mathf.InverseLerp(-q.len / 2, q.len / 2, z - width / 2 + width * f));
                    rings[i] = R(f, r.y * q.w * 1.04f, r.z * q.h * 1.04f, r.w * q.h);
                }
                s.Loft(body, color, new Vector3(0, 0, z - width / 2), new Vector3(0, 0, z + width / 2), ROUND, 1, rings);
            }
        }

        // The animals -------------------------------------------------------------------------

        static void Lion(Sculpt s)
        {
            const int mane = 0x8e4a20;
            var q = new Q
            {
                len = 1, w = 0.2f, h = 0.22f, legs = 0.4f, legW = 0.065f,
                neckLen = 0.2f, neckUp = 38, neckW = 0.13f,
                headLen = 0.32f, headW = 0.15f, headH = 0.16f, snout = 0.08f, snoutW = 0.085f, snoutH = 0.075f,
                tailLen = 0.7f, tailW = 0.03f, tailSegs = 4, tailUp = 5, tailCurl = -14,
                main = 0xd9a24a, belly = 0xf0d49c, feet = 0xc68b3c, muzzle = 0xf4ddb0, nose = 0x3a2420,
            };
            Four(s, q);
            var head = s.creature.head;
            // A shaggy mane round the head and down the neck.
            s.Loft(head, mane, new Vector3(0, 0.01f, -0.2f), new Vector3(0, 0, 0.06f), 10, 1.32f, R(0, 0.17f, 0.19f), R(0.45f, 0.26f, 0.28f), R(1, 0.19f, 0.22f));
            var up = Dir(q.neckUp);
            s.Loft(s.creature.neck, mane, -up * 0.06f, up * 0.17f, 9, 1.25f, R(0, 0.15f, 0.17f), R(1, 0.2f, 0.22f));
            foreach (float side in new[] { -1f, 1f }) RoundEar(s, head, new Vector3(side * 0.12f, 0.19f, -0.01f), side, 0.05f, q.main, q.belly);
            var tail = s.creature.tail;
            s.Ball(tail[tail.Count - 1].joint, mane, Back(q.tailUp + q.tailCurl * q.tailSegs) * (q.tailLen / q.tailSegs), Back(q.tailUp + q.tailCurl * q.tailSegs), 0.13f, 0.05f, 0.05f, 6, 1.25f);
        }

        static void Tiger(Sculpt s)
        {
            const int stripe = 0x2a2220, white = 0xf6ede0;
            var q = new Q
            {
                len = 1.1f, w = 0.19f, h = 0.2f, legs = 0.37f, legW = 0.06f,
                neckLen = 0.14f, neckUp = 25, neckW = 0.12f,
                headLen = 0.3f, headW = 0.15f, headH = 0.15f, snout = 0.06f, snoutW = 0.085f, snoutH = 0.07f,
                tailLen = 0.75f, tailW = 0.028f, tailSegs = 6, tailUp = -5, tailCurl = 9,
                main = 0xe8792b, belly = white, feet = 0xe8792b, muzzle = white, nose = 0x3a2420,
                tailColors = new[] { 0xe8792b, stripe },
            };
            Four(s, q);
            var body = s.creature.body;
            // Stripes: slim slanting flashes down each flank from the spine.
            for (int k = 0; k < 6; k++)
            {
                float z = -0.38f + k * 0.13f;
                s.Gem(body, stripe, On(TORSO, -q.len / 2, q.len / 2, q.w, q.h, z, 90, 1, 0.97f), Vector3.up, 0.03f, 0.03f, 0.022f, 4);
                foreach (float side in new[] { -1f, 1f })
                    foreach (float a in new[] { 55f, 15f })
                    {
                        var p = On(TORSO, -q.len / 2, q.len / 2, q.w, q.h, z + (a < 30 ? 0.025f : 0.012f), a, side, 0.95f);
                        s.Gem(body, stripe, p, Out(a, side), 0.04f, 0.022f, a < 30 ? 0.085f : 0.075f, 4);
                    }
            }
            var head = s.creature.head;
            foreach (float side in new[] { -1f, 1f })
            {
                RoundEar(s, head, new Vector3(side * 0.11f, 0.13f, -0.04f), side, 0.045f, q.main, white);
                // Stripes down the cheeks.
                for (int k = 0; k < 2; k++)
                {
                    var p = On(SKULL, -q.headLen * SKULL_BACK, q.headLen * SKULL_FRONT, q.headW, q.headH, -0.06f + k * 0.06f, 5, side, 0.97f);
                    s.Gem(head, stripe, p, Out(5, side), 0.02f, 0.012f, 0.05f, 4);
                }
            }
            s.Gem(head, stripe, new Vector3(0, q.headH * 0.97f, 0), Vector3.up, 0.02f, 0.012f, 0.07f, 4);
        }

        static void Elephant(Sculpt s)
        {
            const int grey = 0x9aa0a6, darker = 0x858b92, tusk = 0xf3ead2;
            var q = new Q
            {
                len = 1, w = 0.3f, h = 0.33f, legs = 0.4f, legW = 0.11f, swing = 18,
                neckLen = 0.08f, neckUp = 10, neckW = 0.22f,
                headLen = 0.36f, headW = 0.24f, headH = 0.26f, headUp = -8,
                tailLen = 0.3f, tailW = 0.018f, tailSegs = 2, tailUp = -70,
                main = grey, belly = 0x8f959c, feet = 0xc9c2b4,
            };
            Four(s, q);
            var c = s.creature;
            var head = c.head;
            // The trunk: four joints, curving down from the face.
            Transform parent = head;
            var pos = new Vector3(0, -q.headH * 0.25f, q.headLen * 0.42f);
            float[] angles = { 40, 62, 80, 94 };
            for (int i = 0; i < angles.Length; i++)
            {
                var j = s.Joint("trunk", parent, pos);
                var d = new Vector3(0, -Mathf.Sin(angles[i] * Mathf.Deg2Rad), Mathf.Cos(angles[i] * Mathf.Deg2Rad));
                float r0 = 0.075f - 0.009f * i, r1 = r0 - 0.009f, seg = 0.13f;
                s.Loft(j, grey, -d * r0 * 0.5f, d * seg, 8, 1, R(0, r0, r0), R(1, r1, r1));
                if (i > 0) s.Knob(j, grey, Vector3.zero, r0 * 1.02f);
                c.trunk.Add(new Creature.Swing { joint = j });
                parent = j;
                pos = d * seg;
            }
            foreach (float side in new[] { -1f, 1f })
            {
                s.Loft(head, tusk, new Vector3(side * 0.08f, -0.13f, 0.12f), new Vector3(side * 0.1f, -0.2f, 0.3f), 5, 1, R(0, 0.025f, 0.025f), R(0.7f, 0.02f, 0.02f), R(1, 0, 0));
                // Big ears fanned out sideways from their front edge, flapping.
                var ear = s.Joint("ear", head, new Vector3(side * q.headW * 0.8f, q.headH * 0.15f, -q.headLen * 0.12f), new Vector3(0, -side * 62, 0));
                s.Loft(ear, darker, new Vector3(0, 0.2f, 0), new Vector3(0, -0.25f, 0), 6, 1, R(0, 0.02f, 0.07f, -0.05f), R(0.3f, 0.028f, 0.2f, -0.13f), R(0.75f, 0.028f, 0.18f, -0.12f), R(1, 0.02f, 0.05f, -0.04f));
                c.ears.Add(new Creature.Swing { joint = ear, side = -side });
            }
            c.earFlap = 22;
            c.earRate = 0.35f;
            c.nod = 2;
            var tail = c.tail;
            s.Ball(tail[tail.Count - 1].joint, 0x4a4d52, Back(q.tailUp) * (q.tailLen / q.tailSegs), Back(q.tailUp), 0.07f, 0.025f, 0.025f);
        }

        static void Giraffe(Sculpt s)
        {
            const int spot = 0x8b5128, hoof = 0x3a2b22;
            var q = new Q
            {
                len = 0.62f, w = 0.14f, h = 0.17f, legs = 0.78f, legW = 0.042f, pitch = 10, swing = 20,
                neckLen = 0.85f, neckUp = 64, neckW = 0.055f,
                headLen = 0.22f, headW = 0.065f, headH = 0.075f, headUp = -4,
                snout = 0.07f, snoutW = 0.05f, snoutH = 0.05f,
                tailLen = 0.35f, tailW = 0.014f, tailSegs = 2, tailUp = -62,
                main = 0xe8b85c, belly = 0xf3e2bb, feet = hoof, muzzle = 0xe9d2a5, nose = 0x4a3426,
            };
            Four(s, q);
            var c = s.creature;
            // Spots on the body, in staggered rows, and down the neck.
            var body = c.body;
            int row = 0;
            for (float z = -0.22f; z <= 0.23f; z += 0.11f, row++)
                for (float a = row % 2 == 0 ? -20 : 5; a <= 200; a += 50)
                {
                    float side = a > 90 ? -1 : 1, ang = a > 90 ? 180 - a : a;
                    var p = On(TORSO, -q.len / 2, q.len / 2, q.w, q.h, z, ang, side, 0.93f);
                    s.Gem(body, spot, p, Out(ang, side), 0.05f, 0.04f, 0.035f, 5);
                }
            var neck = c.neck;
            var up = Dir(q.neckUp - q.pitch);
            for (int k = 0; k < 5; k++)
                foreach (float side in new[] { -1f, 1f })
                {
                    float f = 0.12f + k * 0.17f + (side > 0 ? 0.05f : 0);
                    float r = q.neckW * (1.1f - 0.25f * f) * 0.92f;
                    s.Gem(neck, spot, up * (q.neckLen * f) + new Vector3(side * r, 0, 0), new Vector3(side, 0, 0), 0.04f, 0.035f, 0.035f, 5);
                }
            var head = c.head;
            foreach (float side in new[] { -1f, 1f })
            {
                // Ossicones, each with a dark knob.
                var base_ = new Vector3(side * 0.025f, q.headH * 0.8f, -0.05f);
                var top = base_ + new Vector3(side * 0.008f, 0.075f, -0.01f);
                s.Loft(head, q.main, base_, top, 5, 1, R(0, 0.016f, 0.016f), R(1, 0.012f, 0.012f));
                s.Ball(head, hoof, top, Vector3.up, 0.035f, 0.02f, 0.02f, 5);
                PointedEar(s, head, new Vector3(side * 0.055f, q.headH * 0.55f, -0.07f), side, 0.07f, 0.03f, q.main, 75, q.belly);
            }
            var tail = c.tail;
            s.Ball(tail[tail.Count - 1].joint, hoof, Back(q.tailUp) * (q.tailLen / q.tailSegs), Back(q.tailUp), 0.08f, 0.022f, 0.022f, 5, 1.2f);
            c.nod = 4;
        }

        static void Panda(Sculpt s)
        {
            const int black = 0x27272b, white = 0xf3f1ec;
            var q = new Q
            {
                len = 0.7f, w = 0.25f, h = 0.25f, legs = 0.2f, legW = 0.085f, swing = 22,
                neckLen = 0.05f, neckUp = 20, neckW = 0.2f,
                headLen = 0.3f, headW = 0.2f, headH = 0.19f, snout = 0.06f, snoutW = 0.08f, snoutH = 0.065f,
                tailLen = 0.06f, tailW = 0.04f, tailSegs = 1, tailUp = 10,
                main = white, belly = 0xe8e5de, feet = black, leg = black, muzzle = white, nose = black,
            };
            Four(s, q);
            var c = s.creature;
            // The black saddle over the shoulders.
            Bands(s, c.body, q, black, 0.16f, 0.17f);
            var head = c.head;
            foreach (float side in new[] { -1f, 1f })
            {
                RoundEar(s, head, new Vector3(side * 0.15f, 0.15f, -0.04f), side, 0.06f, black);
                // Eye patches, with the eye a glint in each.
                var p = On(SKULL, -q.headLen * SKULL_BACK, q.headLen * SKULL_FRONT, q.headW, q.headH, q.headLen * 0.3f, 22, side);
                s.Gem(head, black, p, Out(22, side, 0.6f), 0.04f, 0.06f, 0.045f, 6);
                s.Gem(head, white, p + Out(22, side, 0.6f).normalized * 0.016f + new Vector3(0, 0.008f, 0), Out(22, side, 0.6f), 0.02f, 0.016f, 0.016f, 5);
            }
            c.tailSway = 6;
        }

        static void Polar(Sculpt s)
        {
            var q = new Q
            {
                len = 0.85f, w = 0.22f, h = 0.24f, legs = 0.27f, legW = 0.085f, swing = 22,
                neckLen = 0.17f, neckUp = 14, neckW = 0.16f,
                headLen = 0.28f, headW = 0.14f, headH = 0.14f, snout = 0.09f, snoutW = 0.08f, snoutH = 0.07f,
                tailLen = 0.06f, tailW = 0.04f, tailSegs = 1, tailUp = 10,
                main = 0xf3efe2, belly = 0xe7e2d2, feet = 0xe7e2d2, muzzle = 0xece7d8, nose = 0x222428,
            };
            Four(s, q);
            var head = s.creature.head;
            foreach (float side in new[] { -1f, 1f }) RoundEar(s, head, new Vector3(side * 0.1f, 0.12f, -0.05f), side, 0.04f, q.main);
            s.creature.tailSway = 6;
        }

        static void Koala(Sculpt s)
        {
            const int light = 0xe8e6e1;
            var q = new Q
            {
                len = 0.42f, w = 0.17f, h = 0.18f, legs = 0.12f, legW = 0.058f, swing = 22,
                neckLen = 0.03f, neckUp = 25, neckW = 0.14f,
                headLen = 0.24f, headW = 0.17f, headH = 0.16f,
                main = 0x9a9ea4, belly = light, feet = 0x7d8187,
            };
            Four(s, q);
            var head = s.creature.head;
            // A big dark nose, and fluffy ears.
            s.Ball(head, 0x2a2a2e, new Vector3(0, -0.02f, q.headLen * 0.47f), Vector3.up, 0.11f, 0.042f, 0.04f, 6);
            foreach (float side in new[] { -1f, 1f }) RoundEar(s, head, new Vector3(side * 0.16f, 0.1f, -0.02f), side, 0.1f, q.main, light, 1.18f);
            Eyes(s, head, q.headLen, q.headW, q.headH, 0.32f, 18, 0.8f);
        }

        static void Monkey(Sculpt s)
        {
            const int face = 0xe9c79c;
            var q = new Q
            {
                len = 0.42f, w = 0.14f, h = 0.15f, legs = 0.26f, legW = 0.045f, pitch = 28, swing = 28,
                neckLen = 0.05f, neckUp = 40, neckW = 0.1f,
                headLen = 0.2f, headW = 0.13f, headH = 0.13f,
                snout = 0.035f, snoutW = 0.075f, snoutH = 0.055f,
                tailLen = 0.55f, tailW = 0.022f, tailSegs = 6, tailUp = 25, tailCurl = 28,
                main = 0x6e4a2e, belly = 0xc9a27a, feet = 0x4f3420, muzzle = face, nose = 0x3b2618,
            };
            Four(s, q);
            var head = s.creature.head;
            // A pale face, and round pale ears either side.
            s.Ball(head, face, new Vector3(0, 0.005f, q.headLen * 0.36f), Vector3.forward, 0.07f, 0.085f, 0.09f, 7);
            Eyes(s, head, q.headLen, q.headW, q.headH, 0.42f, 25);
            foreach (float side in new[] { -1f, 1f }) RoundEar(s, head, new Vector3(side * q.headW * 0.95f, 0.02f, -0.01f), side, 0.045f, face);
            s.creature.tailSway = 18;
        }

        static void Penguin(Sculpt s)
        {
            const int black = 0x24272c, white = 0xf4f4f0, orange = 0xf2992e;
            var c = s.creature;
            c.gait = Creature.Gait.Waddle;
            float bw = 0.2f, bd = 0.18f, bh = 0.32f, legs = 0.09f;
            c.size = bh * 2;
            var body = c.body = s.Joint("body", null, new Vector3(0, legs + bh, 0));
            s.Loft(body, black, new Vector3(0, -bh, 0), new Vector3(0, bh, 0), ROUND, 1, R(0, 0.45f * bw, 0.45f * bd), R(0.15f, 0.9f * bw, 0.9f * bd), R(0.45f, bw, bd), R(0.75f, 0.85f * bw, 0.85f * bd), R(1, 0.55f * bw, 0.55f * bd));
            // The white front (an upright piece's lift is forwards).
            s.Loft(body, white, new Vector3(0, -bh * 0.92f, 0), new Vector3(0, bh * 0.62f, 0), ROUND, 1, R(0, 0.4f * bw, 0.4f * bd, 0.04f), R(0.15f, 0.82f * bw, 0.85f * bd, 0.05f), R(0.5f, 0.9f * bw, 0.9f * bd, 0.05f), R(0.85f, 0.7f * bw, 0.75f * bd, 0.04f), R(1, 0.45f * bw, 0.5f * bd, 0.03f));
            var head = c.head = s.Joint("head", body, new Vector3(0, bh * 0.92f, 0));
            s.Ball(head, black, new Vector3(0, 0.09f, 0.01f), Vector3.up, 0.24f, 0.14f, 0.14f, 10);
            foreach (float side in new[] { -1f, 1f })
            {
                s.Gem(head, white, new Vector3(side * 0.08f, 0.12f, 0.085f), new Vector3(side * 0.6f, 0, 1), 0.035f, 0.032f, 0.03f, 6);
                s.Gem(head, EYE, new Vector3(side * 0.088f, 0.122f, 0.098f), new Vector3(side * 0.6f, 0, 1), 0.03f, 0.016f, 0.018f, 5);
            }
            s.Loft(head, orange, new Vector3(0, 0.08f, 0.1f), new Vector3(0, 0.065f, 0.21f), 4, 1, R(0, 0.034f, 0.03f), R(1, 0, 0));
            foreach (float side in new[] { -1f, 1f })
            {
                var flipper = s.Joint("flipper", body, new Vector3(side * bw * 0.92f, bh * 0.45f, 0), new Vector3(0, 0, side * 8));
                s.Knob(flipper, black, new Vector3(0, 0.01f, 0), 0.035f);
                s.Loft(flipper, black, new Vector3(0, 0.03f, 0), new Vector3(0, -0.3f, 0), 6, 1, R(0, 0.02f, 0.05f), R(0.4f, 0.025f, 0.065f), R(1, 0.012f, 0.02f));
                c.wings.Add(new Creature.Swing { joint = flipper, side = side });
                var hip = s.Joint("leg", body, new Vector3(side * 0.08f, -bh * 0.9f, 0.01f));
                float L = hip.position.y;
                s.Knob(hip, orange, Vector3.zero, 0.034f);
                s.Loft(hip, orange, new Vector3(0, 0.03f, 0), new Vector3(0, -L + 0.02f, 0), 6, 1, R(0, 0.03f, 0.03f), R(1, 0.022f, 0.022f));
                s.Loft(hip, orange, new Vector3(0, -L + 0.012f, -0.02f), new Vector3(0, -L + 0.012f, 0.1f), 5, 1, R(0, 0.03f, 0.012f), R(0.7f, 0.055f, 0.012f), R(1, 0.045f, 0.01f));
                c.legs.Add(new Creature.Leg { hip = hip, walk = side < 0 ? 0 : 0.5f, trot = side < 0 ? 0 : 0.5f, swing = 22, bend = 0, tuck = -10 });
            }
            c.stride = 0.2f;
            c.cadenceTop = 4.5f;
            c.roll = 9;
            c.bob = 0.015f;
            c.nod = 4;
            c.wingOut = 12;
            c.wingBeat = 55;
            c.tailSway = 0;
        }

        static void Parrot(Sculpt s)
        {
            const int red = 0xd9322b, blue = 0x2e6fd1, yellow = 0xf2c230, beak = 0xeee3cc, grey = 0x6b6b6b;
            var c = s.creature;
            c.gait = Creature.Gait.Waddle;
            float w = 0.1f, d = 0.1f, h = 0.16f, legs = 0.1f, lean = 22;
            c.size = h * 2;
            var body = c.body = s.Joint("body", null, new Vector3(0, legs + h, 0), new Vector3(lean, 0, 0));
            s.Loft(body, red, new Vector3(0, -h, 0), new Vector3(0, h, 0), ROUND, 1, R(0, 0.4f * w, 0.4f * d), R(0.2f, 0.9f * w, 0.9f * d), R(0.55f, w, d), R(0.85f, 0.8f * w, 0.8f * d), R(1, 0.5f * w, 0.5f * d));
            var head = c.head = s.Joint("head", body, new Vector3(0, h * 0.95f, 0), new Vector3(-lean, 0, 0));
            s.Ball(head, red, new Vector3(0, 0.05f, 0.01f), Vector3.up, 0.15f, 0.075f, 0.08f, 10);
            foreach (float side in new[] { -1f, 1f })
            {
                s.Gem(head, 0xf2efe6, new Vector3(side * 0.05f, 0.055f, 0.05f), new Vector3(side, 0, 0.8f), 0.025f, 0.035f, 0.03f, 6);
                s.Gem(head, EYE, new Vector3(side * 0.06f, 0.062f, 0.055f), new Vector3(side, 0, 0.8f), 0.02f, 0.012f, 0.013f, 5);
            }
            // A hooked beak: the top curving down over a dark lower half.
            s.Loft(head, beak, new Vector3(0, 0.07f, 0.07f), new Vector3(0, 0.0f, 0.135f), 4, 1, R(0, 0.026f, 0.03f), R(0.6f, 0.018f, 0.02f), R(1, 0, 0));
            s.Loft(head, 0x2b2b2b, new Vector3(0, 0.03f, 0.065f), new Vector3(0, 0.02f, 0.1f), 4, 1, R(0, 0.018f, 0.015f), R(1, 0, 0));
            foreach (float side in new[] { -1f, 1f })
            {
                var wing = s.Joint("wing", body, new Vector3(side * w * 0.9f, h * 0.55f, -0.01f), new Vector3(0, 0, side * 4));
                s.Knob(wing, blue, new Vector3(0, 0.01f, 0), 0.026f);
                s.Loft(wing, blue, new Vector3(0, 0.02f, 0), new Vector3(0, -0.24f, -0.03f), 6, 1, R(0, 0.022f, 0.05f), R(0.45f, 0.026f, 0.068f), R(1, 0.01f, 0.022f));
                s.Loft(wing, yellow, new Vector3(0, 0.025f, 0), new Vector3(0, -0.09f, -0.01f), 6, 1, R(0, 0.026f, 0.054f), R(1, 0.03f, 0.068f));
                c.wings.Add(new Creature.Swing { joint = wing, side = side });
                var hip = s.Joint("leg", body, new Vector3(side * 0.04f, -h * 0.85f, 0.02f), new Vector3(-lean, 0, 0));
                float L = hip.position.y;
                s.Knob(hip, grey, Vector3.zero, 0.018f);
                s.Loft(hip, grey, new Vector3(0, 0.02f, 0), new Vector3(0, -L + 0.012f, 0), 6, 1, R(0, 0.016f, 0.016f), R(1, 0.012f, 0.012f));
                s.Loft(hip, grey, new Vector3(0, -L + 0.008f, -0.025f), new Vector3(0, -L + 0.008f, 0.05f), 5, 1, R(0, 0.012f, 0.008f), R(0.6f, 0.03f, 0.008f), R(1, 0.022f, 0.006f));
                c.legs.Add(new Creature.Leg { hip = hip, walk = side < 0 ? 0 : 0.5f, trot = side < 0 ? 0 : 0.5f, swing = 20, bend = 0, tuck = 20 });
            }
            // Long tail feathers, red tipped with blue.
            var tail = s.Joint("tail", body, new Vector3(0, -h * 0.85f, -0.035f));
            s.Loft(tail, red, new Vector3(0, 0.02f, 0), new Vector3(0, -0.2f, -0.05f), 5, 1, R(0, 0.035f, 0.02f), R(0.6f, 0.032f, 0.012f), R(1, 0.026f, 0.008f));
            s.Loft(tail, blue, new Vector3(0, -0.18f, -0.045f), new Vector3(0, -0.3f, -0.075f), 5, 1, R(0, 0.027f, 0.009f), R(1, 0.012f, 0.004f));
            c.tail.Add(new Creature.Swing { joint = tail });
            c.stride = 0.14f;
            c.cadenceTop = 5;
            c.roll = 7;
            c.bob = 0.01f;
            c.nod = 10;
            c.wingOut = 4;
            c.wingBeat = 70;
            c.tailSway = 6;
        }
    }
}

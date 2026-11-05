// The firefighter, made from the policeman (the free Cartoon City pack has no
// firefighter): his uniform's strip of the people's atlas swapped for the tan
// of bunker gear (his boots and belt left black), a red helmet with a long
// brim at the back and a yellow shield on the front over his cap, and a
// silver air tank on his back. The helmet and tank are meshes of their own
// riding his head and torso bones, fitted to him as he stands in the idle
// pose (where the people face -z), coloured from the same atlas.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.EditorTools
{
    public static class Firefighter
    {
        const string Out = "Assets/Porchlight/Generated/Meshes";

        /// <summary>The uniform's strip of the atlas (u), and how far over the tan strip is.</summary>
        const float UNIFORM_U0 = 576 / 1024f, UNIFORM_U1 = 608 / 1024f, TO_TAN = -96 / 1024f;
        /// <summary>Below this v the strip is near black (boots and belt), which stay.</summary>
        const float BLACK_V = 0.478f;

        /// <summary>Flat colours in the atlas (Unity's uv: v up from the bottom).</summary>
        static readonly Vector2 Red = new Vector2(960 / 1024f, 1 - 350 / 1024f);
        static readonly Vector2 Yellow = new Vector2(896 / 1024f, 1 - 350 / 1024f);
        static readonly Vector2 Silver = new Vector2(700 / 1024f, 1 - 870 / 1024f);
        static readonly Vector2 Black = new Vector2(20 / 1024f, 1 - 1000 / 1024f);

        /// <summary>Whether his gear was made before the last change to how it's made (then the library is built again).</summary>
        public static bool Stale()
        {
            const string self = "Assets/Porchlight/Editor/Firefighter.cs";
            string helmet = $"{Out}/people-firefighter-helmet.asset";
            return !System.IO.File.Exists(helmet) || System.IO.File.GetLastWriteTimeUtc(self) > System.IO.File.GetLastWriteTimeUtc(helmet);
        }

        public static void Dress(GameObject go, AnimationClip idle)
        {
            var skin = go.GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault();
            if (skin == null) return;
            Recolour(skin);
            idle.SampleAnimation(go, 0);
            var baked = new Mesh();
            skin.BakeMesh(baked, true);
            var toWorld = skin.transform.localToWorldMatrix;
            var posed = baked.vertices.Select(v => toWorld.MultiplyPoint3x4(v)).ToArray();
            Object.DestroyImmediate(baked);
            var weights = skin.sharedMesh.boneWeights;
            var bones = skin.bones;
            List<Vector3> On(string bone) => posed.Where((_, i) => i < weights.Length && bones[weights[i].boneIndex0] != null && bones[weights[i].boneIndex0].name == bone).ToList();
            var atlas = skin.sharedMaterials[0];
            var head = bones.FirstOrDefault(b => b != null && b.name == "Head");
            var torso = bones.FirstOrDefault(b => b != null && b.name == "Torso");
            var headVerts = On("Head");
            if (head != null && headVerts.Count > 0) Attach(go, head, Helmet(headVerts), atlas, "helmet");
            var torsoVerts = On("Torso");
            if (torso != null && torsoVerts.Count > 0) Attach(go, torso, Tank(torsoVerts), atlas, "air tank");
        }

        /// <summary>The uniform's colours moved to the tan strip beside them, on a copy of his mesh.</summary>
        static void Recolour(SkinnedMeshRenderer skin)
        {
            var mesh = Object.Instantiate(skin.sharedMesh);
            mesh.name = "firefighter";
            var uv = mesh.uv;
            for (int i = 0; i < uv.Length; i++)
                if (uv[i].x >= UNIFORM_U0 && uv[i].x < UNIFORM_U1 && uv[i].y < BLACK_V) uv[i].x += TO_TAN;
            mesh.uv = uv;
            string path = $"{Out}/people-firefighter.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            skin.sharedMesh = mesh;
        }

        static void Attach(GameObject go, Transform bone, Mesh mesh, Material material, string name)
        {
            string path = $"{Out}/people-firefighter-{name.Replace(' ', '-')}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            // Built where it goes on him as he stands, then hung on the bone that moves it.
            var part = new GameObject(name);
            part.transform.SetPositionAndRotation(go.transform.position, go.transform.rotation);
            part.transform.localScale = go.transform.lossyScale;
            part.transform.SetParent(bone, true);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = part.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        sealed class Builder
        {
            readonly List<Vector3> vertices = new List<Vector3>();
            readonly List<Vector2> uvs = new List<Vector2>();
            readonly List<int> triangles = new List<int>();

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 uv)
            {
                int i = vertices.Count;
                vertices.AddRange(new[] { a, b, c, d });
                uvs.AddRange(new[] { uv, uv, uv, uv });
                triangles.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }

            /// <summary>Both faces of a quad (a brim, seen from above and below).</summary>
            public void Card(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 uv)
            {
                Quad(a, b, c, d, uv);
                Quad(d, c, b, a, uv);
            }

            public Mesh Build(string name)
            {
                // Flat-shaded, as the people are: every face its own corners.
                var m = new Mesh { name = name };
                m.SetVertices(vertices);
                m.SetUVs(0, uvs);
                m.SetTriangles(triangles, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }

        const int SIDES = 12;

        /// <summary>
        /// A round dome snug over the top of his head, a brim round it that runs out
        /// longer at the back, and a yellow shield up the front.
        /// </summary>
        static Mesh Helmet(List<Vector3> head)
        {
            float y0 = head.Min(v => v.y), y1 = head.Max(v => v.y);
            float band = y0 + (y1 - y0) * 0.6f;
            // Round from above, as a helmet is, over his crown: the top of his head (not the peak
            // of his cap, or his nose and chin, which would stretch it long).
            var crown = head.Where(v => v.y > band + (y1 - band) * 0.25f).ToList();
            if (crown.Count < 8) crown = head;
            float cx = (crown.Min(v => v.x) + crown.Max(v => v.x)) / 2, cz = (crown.Min(v => v.z) + crown.Max(v => v.z)) / 2;
            var c = new Vector3(cx, band, cz);
            var across = crown.Select(v => new Vector2(v.x - cx, v.z - cz).magnitude).OrderBy(d => d).ToList();
            float round = across[(int)((across.Count - 1) * 0.9f)];
            var r = new Vector3(round, (y1 - band) * 1.04f, round);
            // Just big enough that his crown doesn't poke through, going by all but the furthest few.
            var out_ = crown.Select(v => new Vector3((v.x - c.x) / r.x, (v.y - c.y) / r.y, (v.z - c.z) / r.z).magnitude)
                .OrderBy(d => d).ToList();
            float most = Mathf.Clamp(out_[(int)((out_.Count - 1) * 0.95f)], 1, 1.2f);
            r *= most * 1.03f;
            var b = new Builder();
            // Map angle a goes round from the front (-z); ring k is k/RINGS of the way up.
            const int RINGS = 5;
            Vector3 Dome(float a, float up)
            {
                float t = up * Mathf.PI / 2;
                return c + new Vector3(Mathf.Sin(a) * Mathf.Cos(t) * r.x, Mathf.Sin(t) * r.y, -Mathf.Cos(a) * Mathf.Cos(t) * r.z);
            }
            for (int s = 0; s < SIDES; s++)
            {
                float a0 = s * Mathf.PI * 2 / SIDES, a1 = (s + 1) * Mathf.PI * 2 / SIDES;
                for (int k = 0; k < RINGS; k++)
                {
                    float u0 = k / (float)RINGS, u1 = (k + 1) / (float)RINGS;
                    b.Quad(Dome(a0, u0), Dome(a0, u1), Dome(a1, u1), Dome(a1, u0), Red);
                }
            }
            // The brim: a little out at the front, more at the sides, long and tipped down at the back.
            Vector3 Rim(float a, float k) => c + new Vector3(Mathf.Sin(a) * r.x * k, 0, -Mathf.Cos(a) * r.z * k);
            float Out(float a)
            {
                float back = (1 - Mathf.Cos(a)) / 2;
                return 1.1f + 0.2f * back * back;
            }
            float Droop(float a) => -0.12f * r.y * Mathf.Max(0, -Mathf.Cos(a));
            for (int s = 0; s < SIDES; s++)
            {
                float a0 = s * Mathf.PI * 2 / SIDES, a1 = (s + 1) * Mathf.PI * 2 / SIDES;
                var in0 = Rim(a0, 0.98f);
                var in1 = Rim(a1, 0.98f);
                var out0 = Rim(a0, Out(a0)) + Vector3.up * Droop(a0);
                var out1 = Rim(a1, Out(a1)) + Vector3.up * Droop(a1);
                b.Card(in0, out0, out1, in1, Red);
            }
            // The shield: a plate on the front of the dome, standing just off it.
            const int ROWS = 3;
            float half = 0.32f;
            for (int k = 0; k < ROWS; k++)
            {
                float u0 = 0.05f + k * 0.16f, u1 = u0 + 0.16f;
                Vector3 P(float a, float up) => c + (Dome(a, up) - c) * 1.03f;
                b.Card(P(-half, u0), P(-half, u1), P(half, u1), P(half, u0), Yellow);
            }
            return b.Build("firefighter helmet");
        }

        /// <summary>A silver cylinder upright on his back, with black straps round it.</summary>
        static Mesh Tank(List<Vector3> torso)
        {
            float x0 = torso.Min(v => v.x), x1 = torso.Max(v => v.x);
            float y0 = torso.Min(v => v.y), y1 = torso.Max(v => v.y);
            // His back's furthest out behind the middle of him (+z: he faces -z).
            float mid = (x0 + x1) / 2, wide = x1 - x0;
            var back = torso.Where(v => Mathf.Abs(v.x - mid) < wide * 0.25f).ToList();
            float z1 = (back.Count > 0 ? back : torso).Max(v => v.z);
            float radius = wide * 0.17f;
            // Clear of the back of his neck at the top.
            float bottom = y0 + (y1 - y0) * 0.12f, top = y1 - (y1 - y0) * 0.14f;
            var axis = new Vector3(mid, 0, z1 + radius * 0.8f);
            var b = new Builder();
            // Round the way that faces each quad outwards.
            Vector3 Ring(int s, float y, float k) => axis + new Vector3(-Mathf.Sin(s * Mathf.PI * 2 / SIDES) * radius * k, y, Mathf.Cos(s * Mathf.PI * 2 / SIDES) * radius * k);
            float[] cuts = { bottom, bottom + (top - bottom) * 0.2f, bottom + (top - bottom) * 0.28f, bottom + (top - bottom) * 0.72f, bottom + (top - bottom) * 0.8f, top };
            for (int s = 0; s < SIDES; s++)
            {
                for (int k = 0; k < cuts.Length - 1; k++)
                {
                    bool strap = k == 1 || k == 3;
                    float w = strap ? 1.06f : 1;
                    b.Quad(Ring(s, cuts[k], w), Ring(s, cuts[k + 1], w), Ring(s + 1, cuts[k + 1], w), Ring(s + 1, cuts[k], w), strap ? Black : Silver);
                }
                // Rounded off at the top, with the valve's stub.
                var peak = axis + new Vector3(0, top + radius * 0.6f, 0);
                b.Quad(Ring(s, top, 1), peak, peak, Ring(s + 1, top, 1), Silver);
                var foot = axis + new Vector3(0, bottom, 0);
                b.Quad(Ring(s + 1, bottom, 1), foot, foot, Ring(s, bottom, 1), Silver);
            }
            return b.Build("firefighter air tank");
        }
    }
}

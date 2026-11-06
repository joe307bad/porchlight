// Fire, smoke and water, for a mission's burning building and the hoses that
// put it out. A Blaze is one burning opening (a window or a doorway): flames
// licking out of it (additive particles, bright enough to bloom) over a pane
// glowing orange, and smoke pouring up from its top (flat-sided puffs, as
// the town is drawn); doused, its flames die down, and once it's out the
// smoke turns to a little steam that thins away. A Plume is the column of
// smoke over the roof, seen from streets away. A Jet is a stream of water
// drawn as the town draws everything, low-poly: a flat-sided tube along the
// arc the hose's sums say (falling at Jet.GRAVITY), lumps rolling down it,
// and a spatter of little blobs where it lands. A Hose is a fire hose, a
// flat-sided tube along the ground. No custom shaders: URP's particle and
// lit materials.

using System;
using Porchlight.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public static class FireLook
    {
        static readonly Color Ember = Coords.Hex(0xff7a1a);
        static Material flames, smoke, glow;

        /// <summary>Flames: additive, and bright past 1 so they bloom.</summary>
        public static Material Flames
        {
            get
            {
                if (flames != null) return flames;
                flames = Materials.Particles(new Color(2.6f, 2.2f, 2f, 1));
                flames.name = "flames";
                flames.SetTexture("_BaseMap", Materials.Soft);
                flames.SetFloat("_Blend", 2);
                flames.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                flames.SetInt("_DstBlend", (int)BlendMode.One);
                return flames;
            }
        }

        /// <summary>Smoke and steam: flat-sided puffs (Puff), alpha-blended, their colour each particle's own.</summary>
        public static Material Smoke
        {
            get
            {
                if (smoke != null) return smoke;
                smoke = Materials.Particles(Color.white);
                smoke.name = "smoke";
                return smoke;
            }
        }

        /// <summary>A burning window's pane, glowing (how brightly is set per window).</summary>
        public static Material Glow => glow ??= Materials.Glowing(Ember);

        public static Color EmberColor => Ember;

        static Mesh puff;

        /// <summary>
        /// A puff of smoke as the town draws one: an icosahedron, one across, each face its own
        /// shade (lighter the more it faces up, as if lit from above) so it reads flat-sided
        /// even through the unlit particle material, which tints it with each particle's colour.
        /// </summary>
        public static Mesh Puff
        {
            get
            {
                if (puff != null) return puff;
                float t = (1 + Mathf.Sqrt(5)) / 2;
                var p = new[]
                {
                    new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                    new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                    new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
                };
                int[] f =
                {
                    0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                    3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
                };
                var v = new Vector3[f.Length];
                var c = new Color[f.Length];
                var tri = new int[f.Length];
                for (int i = 0; i < f.Length; i += 3)
                {
                    var a = p[f[i]].normalized * 0.5f;
                    var b = p[f[i + 1]].normalized * 0.5f;
                    var d = p[f[i + 2]].normalized * 0.5f;
                    var n = Vector3.Cross(b - a, d - a).normalized;
                    float shade = 0.72f + 0.28f * (n.y * 0.5f + 0.5f) - 0.06f * Mathf.Abs(n.x);
                    v[i] = a; v[i + 1] = b; v[i + 2] = d;
                    c[i] = c[i + 1] = c[i + 2] = new Color(shade, shade, shade, 1);
                    tri[i] = i; tri[i + 1] = i + 1; tri[i + 2] = i + 2;
                }
                puff = new Mesh { name = "puff", vertices = v, colors = c, triangles = tri };
                puff.RecalculateNormals();
                puff.RecalculateBounds();
                return puff;
            }
        }

        /// <summary>A particle system's particles drawn as puffs, each turned its own way and slowly tumbling.</summary>
        public static void Faceted(ParticleSystem ps)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = Puff;
            var m = ps.main;
            m.startRotation3D = true;
            m.startRotationX = m.startRotationY = m.startRotationZ = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
            var spin = ps.rotationOverLifetime;
            spin.enabled = true;
            spin.separateAxes = true;
            spin.x = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
            spin.y = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
            spin.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
        }

        /// <summary>A particle system's object, facing `forward` (world), its particles in world space and its renderer drawn with `material`.</summary>
        public static ParticleSystem System(string name, Transform parent, Vector3 at, Vector3 forward, Material material, int most)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = at;
            if (forward.sqrMagnitude > 1e-8) go.transform.rotation = Quaternion.LookRotation(forward, Mathf.Abs(forward.y) > 0.98f ? Vector3.forward : Vector3.up);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.duration = 1;
            main.maxParticles = most;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        public static Gradient Fade(params (Color color, float at)[] keys)
        {
            var g = new Gradient();
            var colors = new GradientColorKey[keys.Length];
            var alphas = new GradientAlphaKey[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                colors[i] = new GradientColorKey(keys[i].color, keys[i].at);
                alphas[i] = new GradientAlphaKey(keys[i].color.a, keys[i].at);
            }
            g.SetKeys(colors, alphas);
            return g;
        }

        /// <summary>A map direction (x, y) as a world one, flat.</summary>
        public static Vector3 Flat(Vec2 d) => new Vector3((float)d.x, 0, (float)-d.y);
    }

    /// <summary>One burning opening: flames out of it, its pane glowing, smoke from its top.</summary>
    public sealed class Blaze
    {
        readonly GameObject root;
        readonly ParticleSystem flames, smoke;
        readonly Renderer pane;
        readonly float rate, smokeRate;
        readonly double phase;
        double level = 1;
        /// <summary>Since it went out (seconds), or -1 while it burns.</summary>
        double outFor = -1;

        public bool Burning => outFor < 0;

        /// <summary>
        /// A fire in the opening whose middle is map (x, y) = `at`, facing `outward`, `half`
        /// wide either side, from `y0` up to `y1` (a doorway's bottom at the ground).
        /// </summary>
        public Blaze(Transform parent, Vec2 at, Vec2 outward, double half, double y0, double y1, double phase)
        {
            this.phase = phase;
            root = new GameObject("blaze");
            root.transform.SetParent(parent, false);
            var o = FireLook.Flat(outward);
            float w = (float)(half * 2), h = (float)(y1 - y0);
            var middle = Coords.Map(at.x, at.y, (y0 + y1) / 2);

            // The pane, glowing: a quad just off the glass, facing out.
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.name = "burning pane";
            quad.transform.SetParent(root.transform, false);
            quad.transform.position = middle + o * 0.008f;
            quad.transform.rotation = Quaternion.LookRotation(-o, Vector3.up);
            quad.transform.localScale = new Vector3(w, h, 1);
            pane = quad.GetComponent<Renderer>();
            pane.sharedMaterial = FireLook.Glow;
            pane.shadowCastingMode = ShadowCastingMode.Off;

            // Flames, from all over the opening, out and up.
            float area = Mathf.Clamp(w * h / 0.04f, 0.6f, 3f);
            flames = FireLook.System("flames", root.transform, middle + o * 0.02f, o, FireLook.Flames, 160);
            var main = flames.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.18f);
            main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Min(w, h) * 0.45f, Mathf.Min(w, h) * 0.8f);
            main.startColor = Color.white;
            var shape = flames.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(w * 0.85f, h * 0.7f, 0.02f);
            var rise = flames.velocityOverLifetime;
            rise.enabled = true;
            rise.space = ParticleSystemSimulationSpace.World;
            // (All three in the same mode, as Unity asks.)
            rise.x = new ParticleSystem.MinMaxCurve(-0.03f, 0.03f);
            rise.y = new ParticleSystem.MinMaxCurve(0.35f, 0.6f);
            rise.z = new ParticleSystem.MinMaxCurve(-0.03f, 0.03f);
            var size = flames.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 1, 1, 0.15f));
            var colour = flames.colorOverLifetime;
            colour.enabled = true;
            colour.color = FireLook.Fade((new Color(1, 0.95f, 0.55f, 0.9f), 0), (new Color(1, 0.55f, 0.12f, 0.85f), 0.35f), (new Color(0.75f, 0.12f, 0.04f, 0), 1));
            rate = 46 * area;
            var emission = flames.emission;
            emission.rateOverTime = rate;
            flames.Play();

            // Smoke, rolling out of the top of it and up.
            var top = Coords.Map(at.x, at.y, y1) + o * 0.06f;
            smoke = FireLook.System("smoke", root.transform, top, (o * 0.45f + Vector3.up).normalized, FireLook.Smoke, 260);
            FireLook.Faceted(smoke);
            var sm = smoke.main;
            sm.startLifetime = new ParticleSystem.MinMaxCurve(2.8f, 4.2f);
            sm.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
            sm.startSize = new ParticleSystem.MinMaxCurve(w * 0.5f, w * 0.85f);
            sm.startColor = new ParticleSystem.MinMaxGradient(new Color(0.16f, 0.15f, 0.15f), new Color(0.3f, 0.29f, 0.28f));
            var ss = smoke.shape;
            ss.shapeType = ParticleSystemShapeType.Cone;
            ss.angle = 18;
            ss.radius = w * 0.3f;
            var drift = smoke.velocityOverLifetime;
            drift.enabled = true;
            drift.space = ParticleSystemSimulationSpace.World;
            drift.x = new ParticleSystem.MinMaxCurve(0.08f, 0.16f);
            drift.y = new ParticleSystem.MinMaxCurve(0.15f, 0.3f);
            drift.z = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);
            var grow = smoke.sizeOverLifetime;
            grow.enabled = true;
            grow.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.EaseInOut(0, 0.6f, 1, 3.4f));
            var fade = smoke.colorOverLifetime;
            fade.enabled = true;
            fade.color = FireLook.Fade((new Color(1, 1, 1, 0), 0), (new Color(1, 1, 1, 0.92f), 0.1f), (new Color(1, 1, 1, 0.7f), 0.65f), (new Color(1, 1, 1, 0), 1));
            smokeRate = 15 * Mathf.Clamp(w / 0.25f, 0.7f, 2f);
            var se = smoke.emission;
            se.rateOverTime = smokeRate;
            smoke.Play();
        }

        /// <summary>How much of it is still alight (1 fully, 0 out): its flames and glow die down with it.</summary>
        public void SetLevel(double k)
        {
            if (!Burning) return;
            level = Math.Max(0, Math.Min(1, k));
            var e = flames.emission;
            e.rateOverTime = rate * (float)(0.15 + 0.85 * level);
            var m = flames.main;
            m.startSizeMultiplier = (float)(0.5 + 0.5 * level);
        }

        /// <summary>Put out: no more flames, and the smoke turns to steam that thins away.</summary>
        public void Out()
        {
            if (!Burning) return;
            outFor = 0;
            flames.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            var m = smoke.main;
            m.startColor = new ParticleSystem.MinMaxGradient(new Color(0.88f, 0.9f, 0.92f), new Color(0.97f, 0.97f, 0.98f));
            Materials.SetGlow(pane, FireLook.EmberColor, 0);
            pane.enabled = false;
        }

        public void Update(double dt, double time)
        {
            if (Burning)
            {
                // A flicker: two waves out of step, and the odd flare.
                double f = 0.75 + 0.25 * Math.Sin(time * 13 + phase) * Math.Sin(time * 5.3 + phase * 2);
                Materials.SetGlow(pane, FireLook.EmberColor, (float)((1.2 + 1.6 * f) * (0.25 + 0.75 * level)));
                return;
            }
            outFor += dt;
            var e = smoke.emission;
            e.rateOverTime = smokeRate * 1.6f * (float)Math.Max(0, 1 - outFor / 4);
            if (outFor > 4 && smoke.isEmitting) smoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        public void Dispose() => UnityEngine.Object.Destroy(root);
    }

    /// <summary>The column of smoke over a burning building's roof, seen from far off; it thins to nothing once the fire's out.</summary>
    public sealed class Plume
    {
        readonly GameObject root;
        readonly ParticleSystem smoke;
        readonly float rate;

        public Plume(Transform parent, Vec2 at, double roof, double wide)
        {
            root = new GameObject("plume");
            root.transform.SetParent(parent, false);
            smoke = FireLook.System("plume", root.transform, Coords.Map(at.x, at.y, roof), Vector3.up, FireLook.Smoke, 600);
            FireLook.Faceted(smoke);
            var m = smoke.main;
            m.startLifetime = new ParticleSystem.MinMaxCurve(6, 9);
            m.startSpeed = new ParticleSystem.MinMaxCurve(0.45f, 0.75f);
            m.startSize = new ParticleSystem.MinMaxCurve((float)wide * 0.4f, (float)wide * 0.7f);
            m.startColor = new ParticleSystem.MinMaxGradient(new Color(0.13f, 0.12f, 0.12f), new Color(0.26f, 0.25f, 0.24f));
            var shape = smoke.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 14;
            shape.radius = (float)wide * 0.45f;
            var drift = smoke.velocityOverLifetime;
            drift.enabled = true;
            drift.space = ParticleSystemSimulationSpace.World;
            drift.x = new ParticleSystem.MinMaxCurve(0.12f, 0.25f);
            drift.y = new ParticleSystem.MinMaxCurve(0.1f, 0.2f);
            drift.z = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);
            var grow = smoke.sizeOverLifetime;
            grow.enabled = true;
            grow.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.EaseInOut(0, 0.7f, 1, 3.2f));
            var fade = smoke.colorOverLifetime;
            fade.enabled = true;
            fade.color = FireLook.Fade((new Color(1, 1, 1, 0), 0), (new Color(1, 1, 1, 0.92f), 0.08f), (new Color(1, 1, 1, 0.7f), 0.7f), (new Color(1, 1, 1, 0), 1));
            rate = 20;
            var e = smoke.emission;
            e.rateOverTime = rate;
            smoke.Play();
        }

        /// <summary>How much of the building still burns (0..1): the column thins with it.</summary>
        public void SetLevel(double k)
        {
            var e = smoke.emission;
            e.rateOverTime = rate * (float)Math.Max(0, Math.Min(1, k));
        }

        public void Dispose() => UnityEngine.Object.Destroy(root);
    }

    /// <summary>A stream of water from a nozzle, along an arc, and a splash where it lands.</summary>
    public sealed class Jet
    {
        /// <summary>How fast the water falls (tiles a second, each second): the hoses' sums use the same.</summary>
        public const double GRAVITY = 2.6;
        /// <summary>The stream's tube: rings along the arc, and corners round each.</summary>
        const int RINGS = 14, SIDES = 5;

        static Material body, foam;
        static Mesh chunk;

        readonly GameObject root;
        readonly Transform tube;
        readonly MeshRenderer tubeRenderer;
        readonly Mesh mesh;
        readonly ParticleSystem splash;
        readonly float thick;
        readonly Vector3[] vertices = new Vector3[RINGS * SIDES * 4];
        readonly int[] triangles = new int[RINGS * SIDES * 6];
        bool on;

        /// <summary>A jet `thick` tiles across as it leaves the nozzle.</summary>
        public Jet(Transform parent, double thick)
        {
            this.thick = (float)thick;
            body ??= Materials.Lit(0x6fc3ef, 0.45f);
            foam ??= Materials.Lit(0xe4f4ff, 0.3f);
            chunk ??= Chunk();
            root = new GameObject("jet");
            root.transform.SetParent(parent, false);

            tube = new GameObject("water").transform;
            tube.SetParent(root.transform, false);
            mesh = new Mesh { name = "water" };
            mesh.MarkDynamic();
            for (int q = 0; q < RINGS * SIDES; q++)
            {
                int i = q * 4, k = q * 6;
                triangles[k] = i; triangles[k + 1] = i + 1; triangles[k + 2] = i + 2;
                triangles[k + 3] = i; triangles[k + 4] = i + 2; triangles[k + 5] = i + 3;
            }
            tube.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            tubeRenderer = tube.gameObject.AddComponent<MeshRenderer>();
            tubeRenderer.sharedMaterial = body;
            tubeRenderer.shadowCastingMode = ShadowCastingMode.Off;
            tubeRenderer.enabled = false;

            // The splash: a spatter of little flat-sided blobs, flung out and falling.
            splash = FireLook.System("splash", root.transform, Vector3.zero, Vector3.up, foam, 120);
            var r = splash.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = chunk;
            var sm = splash.main;
            sm.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
            sm.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            sm.startSize = new ParticleSystem.MinMaxCurve(this.thick * 0.8f, this.thick * 1.6f);
            sm.startRotation3D = true;
            sm.startRotationX = sm.startRotationY = sm.startRotationZ = new ParticleSystem.MinMaxCurve(0, Mathf.PI * 2);
            sm.gravityModifier = (float)(GRAVITY / 9.81);
            var shrink = splash.sizeOverLifetime;
            shrink.enabled = true;
            shrink.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 1, 1, 0.2f));
            var ss = splash.shape;
            ss.shapeType = ParticleSystemShapeType.Cone;
            ss.angle = 50;
            ss.radius = this.thick;
            var se = splash.emission;
            se.rateOverTime = 0;
            splash.Play();
        }

        /// <summary>An octahedron, a drop of water as the town draws one.</summary>
        static Mesh Chunk()
        {
            var p = new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            int[][] faces = { new[] { 0, 4, 3 }, new[] { 0, 3, 5 }, new[] { 0, 5, 2 }, new[] { 0, 2, 4 }, new[] { 1, 3, 4 }, new[] { 1, 5, 3 }, new[] { 1, 2, 5 }, new[] { 1, 4, 2 } };
            var v = new Vector3[24];
            var t = new int[24];
            for (int f = 0; f < 8; f++)
                for (int k = 0; k < 3; k++)
                {
                    v[f * 3 + k] = p[faces[f][k]] * 0.5f;
                    t[f * 3 + k] = f * 3 + k;
                }
            var m = new Mesh { name = "drop", vertices = v, triangles = t };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Spraying (or not) from `from` (world) at `velocity` (tiles a second, world), for
        /// `life` seconds of arc (where it lands), splashing there, against `facing` (world: up off the ground, out of a wall).
        /// </summary>
        public void Spray(bool spraying, Vector3 from, Vector3 velocity, double life, Vector3 lands, Vector3 facing)
        {
            on = spraying;
            tubeRenderer.enabled = spraying;
            var se = splash.emission;
            se.rateOverTime = spraying ? 45 : 0;
            if (!spraying) return;
            Shape(from, velocity, (float)Math.Max(0.05, life));
            splash.transform.position = lands;
            if (facing.sqrMagnitude > 1e-6) splash.transform.rotation = Quaternion.LookRotation(facing, Mathf.Abs(facing.y) > 0.98f ? Vector3.forward : Vector3.up);
        }

        /// <summary>The tube along the arc: a few flat faces round, fattening as it goes, with lumps rolling down it.</summary>
        void Shape(Vector3 from, Vector3 velocity, float life)
        {
            var g = new Vector3(0, -(float)GRAVITY, 0);
            float time = Time.time;
            var rings = new Vector3[RINGS + 1, SIDES];
            for (int i = 0; i <= RINGS; i++)
            {
                float t = life * i / RINGS;
                var at = from + velocity * t + g * (t * t / 2);
                var along = velocity + g * t;
                if (along.sqrMagnitude < 1e-8) along = Vector3.forward;
                along.Normalize();
                var side = Vector3.Cross(along, Vector3.up);
                if (side.sqrMagnitude < 1e-6) side = Vector3.right;
                side.Normalize();
                var up = Vector3.Cross(side, along);
                float k = i / (float)RINGS;
                float radius = thick * (0.45f + 0.4f * k) * (1 + 0.22f * Mathf.Sin(i * 1.9f - time * 16));
                // Each ring turned a little from the last, so the faces twist as water does.
                float spin = i * 0.5f;
                for (int s = 0; s < SIDES; s++)
                {
                    float a = spin + s * Mathf.PI * 2 / SIDES;
                    rings[i, s] = tube.InverseTransformPoint(at + (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * radius);
                }
            }
            for (int i = 0; i < RINGS; i++)
                for (int s = 0; s < SIDES; s++)
                {
                    int n = (s + 1) % SIDES, q = (i * SIDES + s) * 4;
                    vertices[q] = rings[i, s];
                    vertices[q + 1] = rings[i + 1, s];
                    vertices[q + 2] = rings[i + 1, n];
                    vertices[q + 3] = rings[i, n];
                }
            mesh.vertices = vertices;
            if (mesh.triangles.Length == 0) mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        public bool On => on;

        public void Dispose()
        {
            UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(root);
        }
    }

    /// <summary>A fire hose: a round, flat-sided tube following a smooth curve through the points it's given.</summary>
    public sealed class Hose
    {
        const int SIDES = 6, STEPS = 6;

        readonly GameObject go;
        readonly Mesh mesh;
        readonly float radius;
        Vector3[] vertices = new Vector3[0];
        int[] triangles = new int[0];

        public Hose(Transform parent, Material material, double radius)
        {
            this.radius = (float)radius;
            go = new GameObject("hose");
            go.transform.SetParent(parent, false);
            mesh = new Mesh { name = "hose" };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        /// <summary>The hose laid through `points` (world), curving smoothly between them but never under `floor` (world height: it lies on the street, not in it).</summary>
        public void Lay(float floor, params Vector3[] points)
        {
            // Catmull-Rom through the points, STEPS to each span.
            var path = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector3 p0 = points[Math.Max(0, i - 1)], p1 = points[i], p2 = points[i + 1], p3 = points[Math.Min(points.Length - 1, i + 2)];
                for (int k = 0; k < STEPS; k++)
                {
                    float t = k / (float)STEPS;
                    path.Add(0.5f * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * (t * t) + (3 * p1 - p0 - 3 * p2 + p3) * (t * t * t)));
                }
            }
            path.Add(points[points.Length - 1]);
            for (int i = 0; i < path.Count; i++)
                if (path[i].y < floor) path[i] = new Vector3(path[i].x, floor, path[i].z);
            int n = path.Count, quads = (n - 1) * SIDES;
            if (vertices.Length != quads * 4)
            {
                vertices = new Vector3[quads * 4];
                triangles = new int[quads * 6];
                for (int q = 0; q < quads; q++)
                {
                    int i = q * 4, k = q * 6;
                    triangles[k] = i; triangles[k + 1] = i + 1; triangles[k + 2] = i + 2;
                    triangles[k + 3] = i; triangles[k + 4] = i + 2; triangles[k + 5] = i + 3;
                }
                mesh.Clear();
            }
            // Each ring square to the hose there, its sides carried along from the last so the faces don't twist.
            var rings = new Vector3[n, SIDES];
            Vector3 side = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                var along = (path[Math.Min(n - 1, i + 1)] - path[Math.Max(0, i - 1)]).normalized;
                if (along.sqrMagnitude < 1e-8) along = Vector3.forward;
                side = i == 0 ? Vector3.Cross(along, Vector3.up) : side - along * Vector3.Dot(side, along);
                if (side.sqrMagnitude < 1e-6) side = Vector3.Cross(along, Vector3.right);
                side.Normalize();
                var up = Vector3.Cross(side, along);
                for (int s = 0; s < SIDES; s++)
                {
                    float a = s * Mathf.PI * 2 / SIDES;
                    rings[i, s] = go.transform.InverseTransformPoint(path[i] + (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * radius);
                }
            }
            for (int i = 0; i < n - 1; i++)
                for (int s = 0; s < SIDES; s++)
                {
                    int m = (s + 1) % SIDES, q = (i * SIDES + s) * 4;
                    vertices[q] = rings[i, s];
                    vertices[q + 1] = rings[i, m];
                    vertices[q + 2] = rings[i + 1, m];
                    vertices[q + 3] = rings[i + 1, s];
                }
            mesh.vertices = vertices;
            if (mesh.triangles.Length != triangles.Length) mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(go);
        }
    }
}

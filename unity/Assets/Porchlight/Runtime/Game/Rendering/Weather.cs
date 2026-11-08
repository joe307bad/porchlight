// Rain and snow. What falls follows the camera, and lands where the town's top is (the
// roofs, trees and streets: TownWorld.surface, how high the town reaches over each tile).
// It only builds up on the yards and the roofs: rain leaves a film of water on the yards,
// runs along the gutters and splashes; snow settles on the roofs and deepens in the yards.
// Whatever moves through either leaves tracks (wheels, feet) that fill in again while it
// keeps coming down. Sunny is neither: what's left dries and melts away.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public sealed class Weather : IDisposable
    {
        /// <summary>Something moving through it: where, which way (a unit vector, map terms), and on wheels or feet.</summary>
        public struct Mover
        {
            public double x, y, fx, fy;
            public bool wheels;
        }

        public bool Rain, Snow;
        /// <summary>How grey the sky is (0 clear, 1 heavy cloud), for Sky.</summary>
        public float Overcast => (float)overcast;

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        static readonly int Cull = Shader.PropertyToID("_Cull");

        // How fast it comes and goes (per second).
        const double WETTING = 1 / 6.0, DRYING = 1 / 25.0, SNOWING = 1 / 45.0, MELTING = 1 / 30.0, CLOUDING = 1 / 5.0;
        // How fast tracks fill in while it's falling (share of full a second).
        const double RAIN_REFILL = 1 / 8.0, SNOW_REFILL = 1 / 50.0;
        // Falling, per tile of ground in view, per second.
        const float RAIN_DENSITY = 2.2f, SNOW_DENSITY = 0.22f;
        /// <summary>Below a tile's top by more than this, a face is inside something (a room's furniture), out of the weather.</summary>
        const float SHELTERED = 1.4f;

        readonly TownWorld world;
        readonly int w, h;
        readonly GameObject root;
        readonly System.Random random = new System.Random(7);
        double wet, depth, overcast, time;
        ParticleSystem rain, snow, splash;
        Material rainMaterial, snowMaterial, splashMaterial;
        Texture2D dot;
        ParticleSystem.Particle[] buffer = new ParticleSystem.Particle[0];
        Cover film, sheet;
        GameObject flow;
        Material flowMaterial;
        Texture2D streaks;
        /// <summary>Settled snow is built in squares of this many tiles.</summary>
        const int AREA = 32;
        /// <summary>Faces smaller than this (tiles squared) are trim: no snow on them.</summary>
        const float MIN_FACE = 0.004f;
        readonly Dictionary<int, GameObject> shells = new Dictionary<int, GameObject>();
        readonly Dictionary<Mesh, (Vector3[] v, int[] t)> readBack = new Dictionary<Mesh, (Vector3[] v, int[] t)>();
        List<int>[] areaTops;
        int cw, chh, shellFaces, shellLids, shellAreas;
        Material shellMaterial;
        readonly List<Mesh> meshes = new List<Mesh>();
        string report = "";

        public Weather(TownWorld world)
        {
            this.world = world;
            w = world.model.width;
            h = world.model.height;
            root = new GameObject("weather");
            root.transform.SetParent(world.root.transform, false);
        }

        /// <summary>Start as it is now, not coming on (a town entered while it's raining or snowing).</summary>
        public IEnumerable<string> Settle()
        {
            wet = Rain ? 1 : 0;
            depth = Snow ? 1 : 0;
            overcast = Rain || Snow ? 1 : 0;
            yield break;
        }

        public string Report() =>
            $"{report}; falling: rain {rain?.particleCount ?? 0}, snow {snow?.particleCount ?? 0}; wet {wet:0.00}, snow depth {depth:0.00}, overcast {overcast:0.00}";

        /// <summary>
        /// A frame: `focus` is what the camera looks at (world), `span` about how many tiles across
        /// it sees, `dusk` 0 day to 1 night.
        /// </summary>
        public void Update(double dt, Vector3 focus, float span, float dusk, List<Mover> movers)
        {
            time += dt;
            wet = Ease(wet, Rain ? 1 : 0, WETTING, DRYING, dt);
            depth = Ease(depth, Snow ? 1 : 0, SNOWING, MELTING, dt);
            overcast = Ease(overcast, Rain || Snow ? 1 : 0, CLOUDING, CLOUDING, dt);

            if (Rain && rain == null) MakeRain();
            if (Snow && snow == null) MakeSnow();
            span = Mathf.Clamp(span * 1.4f, 20, 64);
            float snowTop = SnowY();
            if (rain != null) Fall(rain, Rain, RAIN_DENSITY, focus, span, 14, 0.2f, new Color(0.78f, 0.84f, 0.95f, 0.45f), rainMaterial, dusk, true, 0.03f);
            if (snow != null) Fall(snow, Snow, SNOW_DENSITY, focus, span, 7, 12f, new Color(1, 1, 1, 0.95f), snowMaterial, dusk, false, snowTop);

            // The yards: under a film of water, or a deepening layer of snow (the streets stay clear).
            if (wet > 0.001 && film == null) film = new Cover("rain film", world, new Color32(40, 52, 64, 255), new Color(1, 1, 1, 0), 0.95f, root.transform, 0.2f);
            if (depth > 0.001 && sheet == null) sheet = new Cover("snow", world, new Color32(244, 247, 252, 255), new Color(1, 1, 1, 0), 0.25f, root.transform, 0.18f);
            film?.Level((float)(wet * 0.5), 0.031f);
            sheet?.Level((float)Math.Min(1, depth * 2.5), snowTop);
            foreach (var m in movers)
            {
                if (film != null && wet > 0.05) Mark(film, m);
                if (sheet != null && depth > 0.05) Mark(sheet, m);
            }
            film?.Heal(Rain ? RAIN_REFILL : 0, dt);
            sheet?.Heal(Snow ? SNOW_REFILL : 0, dt);
            film?.Apply(dt);
            sheet?.Apply(dt);

            // Running along the gutters once it's properly wet.
            float running = Mathf.Clamp01(((float)wet - 0.3f) / 0.7f);
            if (running > 0 && flow == null) MakeFlow();
            if (flow != null)
            {
                flow.SetActive(running > 0.01f);
                flowMaterial.SetColor(BaseColor, new Color(0.72f, 0.82f, 0.95f, 0.6f * running));
                flowMaterial.SetTextureOffset(BaseMap, new Vector2(0, (float)(-time * 0.9 % 1)));
            }

            // Settled on every roof, ledge and tree.
            if (depth > 0.001 || shells.Count > 0)
                UpdateShells(focus, span, Mathf.SmoothStep(0, 1, (float)Math.Min(1, depth * 1.6)));
        }

        static double Ease(double v, double to, double up, double down, double dt) =>
            v < to ? Math.Min(to, v + up * dt) : Math.Max(to, v - down * dt);

        float SnowY() => 0.034f + (float)depth * 0.06f;

        /// <summary>How high the town reaches over a map point (outside it: below the slab).</summary>
        float Top(float x, float y)
        {
            int tx = (int)Math.Floor(x), ty = (int)Math.Floor(y);
            if (tx < 0 || ty < 0 || tx >= w || ty >= h) return -0.3f;
            return world.surface[ty * w + tx];
        }

        // Falling ------------------------------------------------------------------------------

        void Fall(ParticleSystem ps, bool falling, float density, Vector3 focus, float span, float above, float column,
            Color color, Material material, float dusk, bool isRain, float floor)
        {
            ps.transform.position = new Vector3(focus.x, focus.y + above, focus.z);
            var shape = ps.shape;
            shape.scale = new Vector3(span, column, span);
            var emission = ps.emission;
            emission.rateOverTime = falling ? Mathf.Min(isRain ? 5000 : 1200, density * span * span) : 0;
            material.SetColor(BaseColor, color * new Color(1 - 0.55f * dusk, 1 - 0.5f * dusk, 1 - 0.4f * dusk, 1));
            Land(ps, isRain, floor);
        }

        /// <summary>Whatever's reached the roof, tree or street under it stops there (rain splashes).</summary>
        void Land(ParticleSystem ps, bool isRain, float floor)
        {
            int n = ps.particleCount;
            if (n == 0) return;
            if (buffer.Length < ps.main.maxParticles) buffer = new ParticleSystem.Particle[ps.main.maxParticles];
            n = ps.GetParticles(buffer);
            int splashes = 0;
            for (int i = 0; i < n; i++)
            {
                var p = buffer[i].position;
                float top = Top(p.x, -p.z);
                float under = top > 0.1f ? top : Math.Max(top, floor);
                if (p.y > under) continue;
                buffer[i].remainingLifetime = 0;
                if (isRain && splash != null && splashes < 80 && top > -0.2f)
                {
                    splashes++;
                    splash.Emit(new ParticleSystem.EmitParams
                    {
                        position = new Vector3(p.x, under + 0.02f, p.z),
                        velocity = new Vector3((float)(random.NextDouble() - 0.5) * 0.8f, 0.9f + (float)random.NextDouble() * 0.6f, (float)(random.NextDouble() - 0.5) * 0.8f),
                        applyShapeToPosition = false,
                    }, 1);
                }
            }
            ps.SetParticles(buffer, n);
        }

        ParticleSystem MakeSystem(string name, int max, float lifetime, ParticleSystem.MinMaxCurve size, Material material,
            ParticleSystemRenderMode mode)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0;
            main.startLifetime = lifetime;
            main.startSize = size;
            main.maxParticles = max;
            main.startColor = Color.white;
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            var emission = ps.emission;
            emission.rateOverTime = 0;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = mode;
            r.material = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        void MakeRain()
        {
            rainMaterial = Materials.Particles(Color.white);
            rain = MakeSystem("rain", 6000, 2.2f, new ParticleSystem.MinMaxCurve(0.022f, 0.035f), rainMaterial, ParticleSystemRenderMode.Stretch);
            var r = rain.GetComponent<ParticleSystemRenderer>();
            r.velocityScale = 0.035f;
            r.lengthScale = 1;
            var v = rain.velocityOverLifetime;
            v.enabled = true;
            v.space = ParticleSystemSimulationSpace.World;
            v.x = new ParticleSystem.MinMaxCurve(0.4f, 0.8f);
            v.y = new ParticleSystem.MinMaxCurve(-17f, -15f);
            v.z = new ParticleSystem.MinMaxCurve(-0.1f, 0.1f);
            rain.Play();

            splashMaterial = Materials.Particles(new Color(0.85f, 0.9f, 1, 0.55f));
            splashMaterial.SetTexture(BaseMap, Dot());
            splash = MakeSystem("splashes", 1500, 0.28f, new ParticleSystem.MinMaxCurve(0.025f, 0.045f), splashMaterial, ParticleSystemRenderMode.Billboard);
            var main = splash.main;
            main.gravityModifier = 0.45f;
            var shape = splash.shape;
            shape.enabled = false;
            splash.Play();
        }

        void MakeSnow()
        {
            snowMaterial = Materials.Particles(Color.white);
            snowMaterial.SetTexture(BaseMap, Dot());
            snow = MakeSystem("snow", 6000, 9f, new ParticleSystem.MinMaxCurve(0.06f, 0.12f), snowMaterial, ParticleSystemRenderMode.Billboard);
            var v = snow.velocityOverLifetime;
            v.enabled = true;
            v.space = ParticleSystemSimulationSpace.World;
            v.x = new ParticleSystem.MinMaxCurve(-0.3f, 0.5f);
            v.y = new ParticleSystem.MinMaxCurve(-1.7f, -1.1f);
            v.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
            var noise = snow.noise;
            noise.enabled = true;
            noise.strength = 0.45f;
            noise.frequency = 0.35f;
            noise.scrollSpeed = 0.2f;
            snow.Play();
        }

        /// <summary>A soft round dot, for flakes and splashes.</summary>
        Texture2D Dot()
        {
            if (dot != null) return dot;
            const int S = 16;
            dot = new Texture2D(S, S, TextureFormat.RGBA32, false) { name = "weather dot", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(S / 2f, S / 2f)) / (S / 2f);
                    px[y * S + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(1 - d * d)));
                }
            dot.SetPixels32(px);
            dot.Apply(false);
            return dot;
        }

        // Tracks -------------------------------------------------------------------------------

        static void Mark(Cover cover, Mover m)
        {
            if (m.wheels)
            {
                // Two wheel tracks, a car's width apart.
                double px = -m.fy * 0.27, py = m.fx * 0.27;
                cover.Stamp(m.x + px, m.y + py, 0.09);
                cover.Stamp(m.x - px, m.y - py, 0.09);
            }
            else cover.Stamp(m.x, m.y, 0.07);
        }

        /// <summary>A layer over the yards and gardens (nowhere else: not the streets) that tracks are pressed into.</summary>
        sealed class Cover : IDisposable
        {
            readonly Texture2D texture;
            readonly Color32[] px;
            readonly byte[] full;
            readonly int tw, th, per;
            readonly HashSet<int> marked = new HashSet<int>();
            readonly List<int> filled = new List<int>();
            readonly Material material;
            readonly Mesh mesh;
            readonly GameObject go;
            readonly float trodden;
            Color tint;
            bool dirty;
            double applyIn, carry;

            public Cover(string name, TownWorld world, Color32 rgb, Color tint, float smoothness, Transform parent, float trodden)
            {
                int w = world.model.width, h = world.model.height;
                this.tint = tint;
                this.trodden = trodden;
                per = Mathf.Clamp(1024 / Mathf.Max(w, h), 2, 4);
                tw = w * per;
                th = h * per;
                px = new Color32[tw * th];
                full = new byte[tw * th];
                var terrain = world.model.tiles.terrain;
                var use = world.layout.use;
                for (int ty = 0; ty < th; ty++)
                    for (int tx = 0; tx < tw; tx++)
                    {
                        int tile = ty / per * w + tx / per;
                        byte u = use[tile];
                        bool open = (u == Use.YARD || u == Use.GARDEN || u == Use.COMMUNITY_GARDEN)
                            && !Porchlight.Model.Terrain.IsWater(terrain[tile]) && world.surface[tile] < 0.6f;
                        int i = ty * tw + tx;
                        full[i] = open ? (byte)255 : (byte)0;
                        px[i] = new Color32(rgb.r, rgb.g, rgb.b, full[i]);
                    }
                texture = new Texture2D(tw, th, TextureFormat.RGBA32, false)
                {
                    name = name,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                texture.SetPixels32(px);
                texture.Apply(false);
                material = new Material(ModelLibrary.Instance.clear) { name = name };
                material.SetTexture(BaseMap, texture);
                material.SetFloat(Smoothness, smoothness);
                material.SetColor(BaseColor, tint);
                if (material.HasProperty(Cull)) material.SetFloat(Cull, 0);
                mesh = new Mesh { name = name };
                mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(w, 0, 0), new Vector3(w, 0, -h), new Vector3(0, 0, -h) };
                mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
                mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateBounds();
                go = new GameObject(name);
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = ShadowCastingMode.Off;
                go.SetActive(false);
            }

            public void Level(float alpha, float y)
            {
                tint.a = alpha;
                material.SetColor(BaseColor, tint);
                go.transform.localPosition = new Vector3(0, y, 0);
                go.SetActive(alpha > 0.003f);
            }

            /// <summary>Press a round mark (radius in tiles) into it.</summary>
            public void Stamp(double x, double y, double radius)
            {
                double cx = x * per, cy = y * per, r = Math.Max(0.6, radius * per);
                int x0 = (int)Math.Floor(cx - r), x1 = (int)Math.Ceiling(cx + r);
                int y0 = (int)Math.Floor(cy - r), y1 = (int)Math.Ceiling(cy + r);
                for (int ty = Math.Max(0, y0); ty <= Math.Min(th - 1, y1); ty++)
                    for (int tx = Math.Max(0, x0); tx <= Math.Min(tw - 1, x1); tx++)
                    {
                        double dx = tx + 0.5 - cx, dy = ty + 0.5 - cy;
                        if (dx * dx + dy * dy > r * r) continue;
                        int i = ty * tw + tx;
                        if (full[i] == 0) continue;
                        byte low = (byte)(full[i] * trodden);
                        if (px[i].a <= low) continue;
                        px[i].a = low;
                        marked.Add(i);
                        dirty = true;
                    }
            }

            /// <summary>Tracks filling in again (a share of full a second).</summary>
            public void Heal(double perSecond, double dt)
            {
                if (perSecond <= 0 || marked.Count == 0) return;
                carry += perSecond * dt * 255;
                int step = (int)carry;
                if (step == 0) return;
                carry -= step;
                filled.Clear();
                foreach (int i in marked)
                {
                    int a = Math.Min(full[i], px[i].a + step);
                    px[i].a = (byte)a;
                    if (a >= full[i]) filled.Add(i);
                }
                foreach (int i in filled) marked.Remove(i);
                dirty = true;
            }

            /// <summary>Send the marks to the GPU, a few times a second.</summary>
            public void Apply(double dt)
            {
                applyIn -= dt;
                if (!dirty || applyIn > 0) return;
                texture.SetPixels32(px);
                texture.Apply(false);
                dirty = false;
                applyIn = 0.15;
            }

            public void Dispose()
            {
                UnityEngine.Object.Destroy(texture);
                UnityEngine.Object.Destroy(material);
                UnityEngine.Object.Destroy(mesh);
            }
        }

        // Gutters ------------------------------------------------------------------------------

        /// <summary>Water running along both kerbs of every street.</summary>
        void MakeFlow()
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            const float HALF = 0.07f, Y = 0.036f;
            foreach (var r in world.layout.roads)
            {
                if (r.kind == RoadKind.Path || r.points.Count < 2) continue;
                foreach (int side in new[] { 1, -1 })
                {
                    var line = Layout.OffsetPolyline(r.points, side * (r.width / 2 + Layout.PARKING - 0.08));
                    double along = 0;
                    for (int i = 0; i < line.Count; i++)
                    {
                        var prev = line[Math.Max(0, i - 1)];
                        var next = line[Math.Min(line.Count - 1, i + 1)];
                        double dx = next.x - prev.x, dy = next.y - prev.y, len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 1e-6) continue;
                        double nx = -dy / len * HALF, ny = dx / len * HALF;
                        if (i > 0) along += Math.Sqrt(Math.Pow(line[i].x - line[i - 1].x, 2) + Math.Pow(line[i].y - line[i - 1].y, 2));
                        int at = verts.Count;
                        verts.Add(Coords.Map(line[i].x + nx, line[i].y + ny, Y));
                        verts.Add(Coords.Map(line[i].x - nx, line[i].y - ny, Y));
                        uvs.Add(new Vector2(0, (float)(along / 1.6)));
                        uvs.Add(new Vector2(1, (float)(along / 1.6)));
                        if (i > 0 && at >= 2)
                        {
                            tris.AddRange(new[] { at - 2, at - 1, at, at - 1, at + 1, at });
                        }
                    }
                }
            }
            streaks = new Texture2D(8, 64, TextureFormat.RGBA32, false) { name = "running water", wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[8 * 64];
            var rng = new System.Random(3);
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    double ripple = 0.5 + 0.5 * Math.Sin(y / 64.0 * Math.PI * 6 + x * 1.3 + rng.NextDouble() * 0.8);
                    double edge = 1 - Math.Abs(x - 3.5) / 4.5;
                    px[y * 8 + x] = new Color32(255, 255, 255, (byte)(255 * Math.Clamp(edge * (0.35 + 0.65 * ripple), 0, 1)));
                }
            }
            streaks.SetPixels32(px);
            streaks.Apply(false);
            flowMaterial = new Material(ModelLibrary.Instance.clear) { name = "running water" };
            flowMaterial.SetTexture(BaseMap, streaks);
            flowMaterial.SetFloat(Smoothness, 0.95f);
            if (flowMaterial.HasProperty(Cull)) flowMaterial.SetFloat(Cull, 0);
            flow = Piece("gutters", verts, null, uvs, tris, flowMaterial);
        }

        // Settled snow --------------------------------------------------------------------------

        /// <summary>
        /// A white skin over the roofs: every face that looks up and is out in the open on
        /// the buildings' and sheds'. Built an area at a time around the camera (one a frame, so it never stalls,
        /// and only what's near: a big town's would be millions of faces), dropped once far off.
        /// Where a model can't be read back, a lid over its top instead.
        /// </summary>
        void UpdateShells(Vector3 focus, float span, float settled)
        {
            if (shellMaterial == null)
            {
                shellMaterial = new Material(ModelLibrary.Instance.clear) { name = "settled snow" };
                shellMaterial.SetFloat(Smoothness, 0.2f);
                cw = (w + AREA - 1) / AREA;
                chh = (h + AREA - 1) / AREA;
                areaTops = new List<int>[cw * chh];
                for (int i = 0; i < areaTops.Length; i++) areaTops[i] = new List<int>();
                for (int i = 0; i < world.roofs.Count; i++)
                {
                    var c = world.roofs[i].bounds.center;
                    int ax = Mathf.Clamp(Mathf.FloorToInt(c.x / AREA), 0, cw - 1), ay = Mathf.Clamp(Mathf.FloorToInt(-c.z / AREA), 0, chh - 1);
                    areaTops[ay * cw + ax].Add(i);
                }
            }
            shellMaterial.SetColor(BaseColor, new Color(0.96f, 0.97f, 1, settled));
            bool show = settled > 0.01f;
            float fx = focus.x / AREA, fy = -focus.z / AREA, reach = (span / 2 + AREA) / AREA, far = (span + AREA * 3) / AREA;
            // Out of the way: forget it.
            var gone = new List<int>();
            foreach (var kv in shells)
            {
                float dx = kv.Key % cw + 0.5f - fx, dy = kv.Key / cw + 0.5f - fy;
                if (dx * dx + dy * dy > far * far) gone.Add(kv.Key);
                else kv.Value.SetActive(show);
            }
            foreach (int k in gone)
            {
                var go = shells[k];
                var mesh = go.GetComponent<MeshFilter>().sharedMesh;
                meshes.Remove(mesh);
                UnityEngine.Object.Destroy(mesh);
                UnityEngine.Object.Destroy(go);
                shells.Remove(k);
            }
            if (!show) return;
            // In view and not built yet: the nearest one, this frame.
            int best = -1;
            float bestD = float.MaxValue;
            for (int ay = Mathf.Max(0, Mathf.FloorToInt(fy - reach)); ay <= Mathf.Min(chh - 1, Mathf.FloorToInt(fy + reach)); ay++)
                for (int ax = Mathf.Max(0, Mathf.FloorToInt(fx - reach)); ax <= Mathf.Min(cw - 1, Mathf.FloorToInt(fx + reach)); ax++)
                {
                    int k = ay * cw + ax;
                    if (shells.ContainsKey(k)) continue;
                    float dx = ax + 0.5f - fx, dy = ay + 0.5f - fy, d = dx * dx + dy * dy;
                    if (d < bestD && d <= reach * reach) { bestD = d; best = k; }
                }
            if (best < 0) return;
            var built = BuildShell(best);
            built.SetActive(true);
            shells[best] = built;
        }

        GameObject BuildShell(int area)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            int faces = 0, lids = 0;
            foreach (int index in areaTops[area])
            {
                var (mesh, at, bounds) = world.roofs[index];
                if (mesh == null) continue;
                if (!mesh.isReadable)
                {
                    Lid(bounds, verts, normals, tris);
                    lids++;
                    continue;
                }
                if (!readBack.TryGetValue(mesh, out var data)) readBack[mesh] = data = (mesh.vertices, mesh.triangles);
                var v = data.v;
                var t = data.t;
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    var a = at.MultiplyPoint3x4(v[t[k]]);
                    var b = at.MultiplyPoint3x4(v[t[k + 1]]);
                    var c = at.MultiplyPoint3x4(v[t[k + 2]]);
                    var n = Vector3.Cross(b - a, c - a);
                    if (n.magnitude < MIN_FACE * 2) continue; // trim too small to hold any
                    n.Normalize();
                    if (n.y < 0.5f) continue;
                    var mid = (a + b + c) / 3;
                    if (mid.y < Top(mid.x, -mid.z) - SHELTERED) continue;
                    var lift = n * 0.03f + Vector3.up * 0.008f;
                    int i = verts.Count;
                    verts.Add(a + lift);
                    verts.Add(b + lift);
                    verts.Add(c + lift);
                    normals.Add(n);
                    normals.Add(n);
                    normals.Add(n);
                    tris.Add(i);
                    tris.Add(i + 1);
                    tris.Add(i + 2);
                    faces++;
                }
            }
            shellFaces += faces;
            shellLids += lids;
            shellAreas++;
            report = $"snow settled on {shellFaces} faces and {shellLids} lids in {shellAreas} areas";
            return Piece("settled snow", verts, normals, null, tris, shellMaterial);
        }

        static void Lid(Bounds b, List<Vector3> verts, List<Vector3> normals, List<int> tris)
        {
            float y = b.max.y + 0.02f, ix = b.size.x * 0.05f, iz = b.size.z * 0.05f;
            int i = verts.Count;
            verts.Add(new Vector3(b.min.x + ix, y, b.min.z + iz));
            verts.Add(new Vector3(b.min.x + ix, y, b.max.z - iz));
            verts.Add(new Vector3(b.max.x - ix, y, b.max.z - iz));
            verts.Add(new Vector3(b.max.x - ix, y, b.min.z + iz));
            for (int k = 0; k < 4; k++) normals.Add(Vector3.up);
            tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }

        GameObject Piece(string name, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris, Material material)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            if (uvs != null) mesh.SetUVs(0, uvs);
            if (normals != null) mesh.SetNormals(normals);
            else
            {
                var up = new Vector3[verts.Count];
                for (int i = 0; i < up.Length; i++) up[i] = Vector3.up;
                mesh.normals = up;
            }
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            meshes.Add(mesh);
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            go.SetActive(false);
            return go;
        }

        public void Dispose()
        {
            film?.Dispose();
            sheet?.Dispose();
            foreach (var m in meshes) UnityEngine.Object.Destroy(m);
            foreach (var o in new UnityEngine.Object[] { rainMaterial, snowMaterial, splashMaterial, flowMaterial, shellMaterial, dot, streaks })
                if (o != null) UnityEngine.Object.Destroy(o);
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }
}

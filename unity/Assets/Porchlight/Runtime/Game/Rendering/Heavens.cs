// Clouds by day and twinkling stars by night, on Unity's own particle
// material. Most of the sky is a shell around the camera, drawn before
// anything else and without depth, so the town draws over it wherever it
// stands: the close-up camera sees it as the sky above the haze, and it's
// nearer than the fog begins, so the fog leaves it alone. The orbit camera
// looks at the shell as a level camera would (it shows where the town
// doesn't), and also sees a field of flat clouds drifting over the town, more
// of them the steeper it looks down. Clouds go the moment it's switched to
// night and come back the moment it's switched to day; stars come out only as
// it gets properly dark. Stars are crisp points of their
// own texture, sized in screen pixels (resized whenever the view's scale
// changes) so they stay sharp whatever the camera, and twinkle by their vertex
// colours, rewritten in place each frame they're out. Clouds are puffs of a
// noisy cumulus texture, each turned its own way, so no two edges match.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public sealed class Heavens
    {
        const int STARS = 700;
        /// <summary>A star's core, as a share of its quad's half-width (the rest is a faint glow).</summary>
        const float STAR_CORE = 0.32f;
        const int CLOUDS = 16;
        /// <summary>How far above the horizon the orbit camera's view of the shell looks, and half its height (degrees).</summary>
        const float LEVEL_PITCH = 10, LEVEL_HALF = 24;
        /// <summary>How fast the shell's clouds turn (degrees a second).</summary>
        const float DRIFT = 0.3f;
        /// <summary>The clouds over the town: how high (tiles, over the tallest towers), the square they repeat in, how many in it.</summary>
        const float FIELD_HEIGHT = 8, FIELD_SIZE = 80;
        const int FIELD_CLOUDS = 7;
        static readonly Vector3 Wind = new Vector3(0.5f, 0, 0.2f);

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly Color Sunset = Coords.Hex(0xffc4a8);

        readonly Camera camera;
        readonly GameObject root;
        readonly Transform shell, shellClouds;
        readonly Renderer starRenderer, cloudRenderer;
        readonly Renderer[] field = new Renderer[4];
        readonly Material starMaterial, cloudMaterial, fieldMaterial;
        readonly Texture2D puff, point;
        readonly Mesh stars, clouds, fieldMesh;
        readonly Color32[] starColors;
        readonly float[] starPhase, starRate, starDepth;
        /// <summary>Each star's direction on the shell and its core's radius (screen pixels).</summary>
        readonly Vector3[] starDir;
        readonly float[] starPixels;
        readonly Vector3[] starVerts;
        float starScale = -1;
        /// <summary>How far out the clouds are (0 gone, 1 out), and how fast they go or come back (per second).</summary>
        float cloudsOut = -1;
        const float CLOUD_FADE = 2.5f;

        public Heavens(Camera camera, Transform parent)
        {
            this.camera = camera;
            root = new GameObject("heavens");
            root.transform.SetParent(parent, false);
            puff = Puff(new System.Random(11));
            point = Point();
            var rng = new System.Random(7);

            shell = new GameObject("shell").transform;
            shell.SetParent(root.transform, false);
            starMaterial = Sprite("stars", (int)RenderQueue.Background);
            starMaterial.SetTexture(BaseMap, point);
            cloudMaterial = Sprite("sky clouds", (int)RenderQueue.Background + 1);
            fieldMaterial = Sprite("clouds", (int)RenderQueue.Transparent);

            stars = Stars(rng, out starColors, out starPhase, out starRate, out starDepth, out starDir, out starPixels);
            starVerts = new Vector3[STARS * 4];
            starRenderer = Part("stars", shell, stars, starMaterial);
            clouds = ShellClouds(rng);
            cloudRenderer = Part("sky clouds", shell, clouds, cloudMaterial);
            shellClouds = cloudRenderer.transform;

            fieldMesh = FieldClouds(rng);
            for (int i = 0; i < field.Length; i++) field[i] = Part("clouds " + i, root.transform, fieldMesh, fieldMaterial);
        }

        /// <summary>
        /// Place and fade the sky for this frame (once the camera's placed). `fogNear`: where
        /// the close camera's fog begins; `focus`: what the camera looks at (world).
        /// </summary>
        public void Update(float dusk, bool isNight, bool fog, float fogNear, Vector3 focus, bool indoors)
        {
            float t = Time.time;
            // Clouds follow the switch itself (a quick fade, not the slow change of light); stars the dark.
            if (cloudsOut < 0) cloudsOut = isNight ? 0 : 1;
            cloudsOut = Mathf.MoveTowards(cloudsOut, isNight ? 0 : 1, Time.deltaTime * CLOUD_FADE);
            float day = cloudsOut * cloudsOut * (3 - 2 * cloudsOut);
            float night = Mathf.SmoothStep(0.6f, 0.95f, dusk);
            var view = camera.transform;

            // The shell: round the camera, inside the fog (close) or filling the view as a level camera's would (orbit).
            shell.position = view.position;
            float r;
            if (camera.orthographic)
            {
                var flat = new Vector3(view.forward.x, 0, view.forward.z);
                if (flat.sqrMagnitude < 1e-6f) flat = new Vector3(view.up.x, 0, view.up.z);
                var level = Quaternion.LookRotation(flat.normalized, Vector3.up) * Quaternion.Euler(-LEVEL_PITCH, 0, 0);
                shell.rotation = view.rotation * Quaternion.Inverse(level);
                r = Mathf.Min(camera.orthographicSize / Mathf.Sin(LEVEL_HALF * Mathf.Deg2Rad), camera.farClipPlane * 0.9f);
            }
            else
            {
                shell.rotation = Quaternion.identity;
                r = fog ? fogNear * 0.85f : camera.farClipPlane * 0.5f;
            }
            shell.localScale = new Vector3(r, r, r);
            shellClouds.localRotation = Quaternion.Euler(0, t * DRIFT, 0);

            var tint = Color.Lerp(Color.white, Sunset, Mathf.SmoothStep(0.05f, 0.5f, dusk)) * Mathf.Lerp(1, 0.6f, dusk);
            cloudRenderer.enabled = day > 0.01f;
            cloudMaterial.SetColor(BaseColor, new Color(tint.r, tint.g, tint.b, 0.95f * day));

            starRenderer.enabled = night > 0.01f;
            if (starRenderer.enabled)
            {
                SizeStars();
                starMaterial.SetColor(BaseColor, new Color(1, 1, 1, night));
                for (int i = 0; i < STARS; i++)
                {
                    // A steady light that flickers now and then, more so low in the sky (through more air).
                    float p = starPhase[i], f = starRate[i];
                    float flicker = Mathf.Sin(t * f + p) * Mathf.Sin(t * f * 2.3f + p * 1.7f) * Mathf.Sin(t * f * 0.37f + p * 2.9f);
                    float level = starDepth[i] * (1 - (0.18f + 0.3f * (1 - starDir[i].y)) * Mathf.Abs(flicker));
                    byte a = (byte)(Mathf.Clamp01(level) * 255);
                    for (int k = 4 * i; k < 4 * i + 4; k++) starColors[k].a = a;
                }
                stars.SetColors(starColors);
            }

            // The clouds over the town: the orbit camera only, outdoors, faded in as it looks down.
            float over = camera.orthographic && !indoors ? Mathf.InverseLerp(0.3f, 0.7f, -view.forward.y) * day : 0;
            bool show = over > 0.01f;
            for (int i = 0; i < field.Length; i++) field[i].enabled = show;
            if (!show) return;
            fieldMaterial.SetColor(BaseColor, new Color(tint.r, tint.g, tint.b, 0.5f * over));
            // Copies of the square, 2 by 2, round the focus; the wind slides them all.
            float ox = Mathf.Repeat(Wind.x * t, FIELD_SIZE), oz = Mathf.Repeat(Wind.z * t, FIELD_SIZE);
            float x0 = Mathf.Floor((focus.x - ox - FIELD_SIZE / 2) / FIELD_SIZE) * FIELD_SIZE + ox;
            float z0 = Mathf.Floor((focus.z - oz - FIELD_SIZE / 2) / FIELD_SIZE) * FIELD_SIZE + oz;
            for (int i = 0; i < field.Length; i++)
                field[i].transform.position = new Vector3(x0 + (i & 1) * FIELD_SIZE, FIELD_HEIGHT, z0 + (i >> 1) * FIELD_SIZE);
        }

        /// <summary>
        /// Size every star's quad so its core is its few pixels across on screen: the
        /// shell's radians per pixel are the camera's (the orbit camera sees the shell as
        /// a level camera of LEVEL_HALF would). Only when that changes.
        /// </summary>
        void SizeStars()
        {
            float h = Math.Max(1, camera.pixelHeight);
            float perPixel = camera.orthographic
                ? 2 * Mathf.Sin(LEVEL_HALF * Mathf.Deg2Rad) / h
                : 2 * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / h;
            if (starScale > 0 && Mathf.Abs(perPixel / starScale - 1) < 0.03f) return;
            starScale = perPixel;
            for (int i = 0; i < STARS; i++)
            {
                float w = starPixels[i] / STAR_CORE * perPixel;
                var d = starDir[i];
                var right = Vector3.Cross(Vector3.up, d);
                right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
                var up = Vector3.Cross(d, right);
                starVerts[4 * i] = d - right * w - up * w;
                starVerts[4 * i + 1] = d + right * w - up * w;
                starVerts[4 * i + 2] = d + right * w + up * w;
                starVerts[4 * i + 3] = d - right * w + up * w;
            }
            stars.vertices = starVerts;
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(root);
            foreach (var o in new UnityEngine.Object[] { starMaterial, cloudMaterial, fieldMaterial, puff, point, stars, clouds, fieldMesh })
                UnityEngine.Object.Destroy(o);
        }

        // Building -------------------------------------------------------------------------------

        /// <summary>
        /// Unlit particles, alpha-blended, both sides, colour set per frame. Never in the
        /// depth passes: the shell's would hide the town behind it.
        /// </summary>
        Material Sprite(string name, int queue)
        {
            var m = new Material(ModelLibrary.Instance.particles) { name = name };
            m.SetTexture(BaseMap, puff);
            m.SetColor(BaseColor, Color.clear);
            m.SetFloat("_Cull", (float)CullMode.Off);
            m.SetShaderPassEnabled("DepthOnly", false);
            m.SetShaderPassEnabled("DepthNormals", false);
            m.SetShaderPassEnabled("DepthNormalsOnly", false);
            m.renderQueue = queue;
            return m;
        }

        static Renderer Part(string name, Transform parent, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.enabled = false;
            return r;
        }

        /// <summary>
        /// A cumulus puff: a soft lump whose edge is broken up by a few octaves of
        /// value noise, so it billows rather than ending in a circle.
        /// </summary>
        static Texture2D Puff(System.Random rng)
        {
            const int n = 128, lattice = 8;
            var noise = new float[4][];
            for (int o = 0; o < noise.Length; o++)
            {
                int m = lattice << o;
                noise[o] = new float[m * m];
                for (int i = 0; i < noise[o].Length; i++) noise[o][i] = (float)rng.NextDouble();
            }
            float Value(int o, float x, float y)
            {
                int m = lattice << o;
                x *= m;
                y *= m;
                int x0 = (int)Mathf.Floor(x), y0 = (int)Mathf.Floor(y);
                float fx = x - x0, fy = y - y0;
                fx = fx * fx * (3 - 2 * fx);
                fy = fy * fy * (3 - 2 * fy);
                float At(int i, int j) => noise[o][((j % m + m) % m) * m + (i % m + m) % m];
                return Mathf.Lerp(Mathf.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Mathf.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
            }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "puff", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
                    float dx = u * 2 - 1, dy = v * 2 - 1;
                    float fbm = 0, amp = 0.5f;
                    for (int o = 0; o < noise.Length; o++, amp *= 0.5f) fbm += Value(o, u, v) * amp;
                    // A lump flatter underneath than on top, eaten into by the noise toward its edge.
                    float r = Mathf.Sqrt(dx * dx + (dy < 0 ? dy * 1.25f : dy) * (dy < 0 ? dy * 1.25f : dy));
                    float body = 1 - r;
                    float k = Mathf.Clamp01((body + (fbm - 0.5f) * 0.9f) * 2.2f);
                    k = k * k * (3 - 2 * k);
                    // A little texture inside it too: denser and thinner patches.
                    float alpha = k * (0.75f + 0.25f * fbm);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>A star: a hard-edged white core (STAR_CORE of the way out, anti-aliased over a texel) in a faint glow. No mipmaps, so it stays crisp.</summary>
        static Texture2D Point()
        {
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "star", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float core = Mathf.Clamp01((STAR_CORE - r) * n / 2 + 0.5f);
                    float glow = Mathf.Pow(Mathf.Clamp01(1 - r), 3) * 0.28f;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(Mathf.Max(core, glow)) * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>A direction on the shell: azimuth round from +z, elevation up from the horizon (degrees).</summary>
        static Vector3 Dir(float az, float el)
        {
            az *= Mathf.Deg2Rad;
            el *= Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el));
        }

        /// <summary>A quad on the unit shell at `d`, facing its middle: w by h (radians), bottom and top colours.</summary>
        static void Billboard(List<Vector3> pos, List<Vector2> uv, List<Color32> col, Vector3 d, float w, float h, Color32 bottom, Color32 top, float turn = 0)
        {
            var right = Vector3.Cross(Vector3.up, d);
            right = right.sqrMagnitude < 1e-6f ? Vector3.right : right.normalized;
            var up = Vector3.Cross(d, right);
            if (turn != 0)
            {
                // Turned about its middle (a little: the shading still runs bottom to top).
                float c = Mathf.Cos(turn), sn = Mathf.Sin(turn);
                (right, up) = (right * c + up * sn, up * c - right * sn);
            }
            pos.Add(d - right * w - up * h);
            pos.Add(d + right * w - up * h);
            pos.Add(d + right * w + up * h);
            pos.Add(d - right * w + up * h);
            Corners(uv, col, bottom, top);
        }

        static void Corners(List<Vector2> uv, List<Color32> col, Color32 bottom, Color32 top)
        {
            uv.Add(new Vector2(0, 0));
            uv.Add(new Vector2(1, 0));
            uv.Add(new Vector2(1, 1));
            uv.Add(new Vector2(0, 1));
            col.Add(bottom);
            col.Add(bottom);
            col.Add(top);
            col.Add(top);
        }

        static Mesh Build(string name, List<Vector3> pos, List<Vector2> uv, List<Color32> col, Bounds bounds)
        {
            var ix = new int[pos.Count / 4 * 6];
            for (int q = 0, k = 0; k < ix.Length; q += 4, k += 6)
            {
                ix[k] = q;
                ix[k + 1] = q + 2;
                ix[k + 2] = q + 1;
                ix[k + 3] = q;
                ix[k + 4] = q + 3;
                ix[k + 5] = q + 2;
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(pos);
            mesh.SetUVs(0, uv);
            mesh.SetColors(col);
            mesh.SetTriangles(ix, 0, false);
            mesh.bounds = bounds;
            return mesh;
        }

        static float Range(System.Random rng, double lo, double hi) => (float)(lo + rng.NextDouble() * (hi - lo));

        /// <summary>
        /// Stars over the upper half of the shell, thicker toward the horizon (where the
        /// close camera sees sky): mostly faint pinpricks, a few bright ones, a few blue
        /// or yellow. Their quads are sized later (SizeStars), once the view's scale is known.
        /// </summary>
        static Mesh Stars(System.Random rng, out Color32[] colors, out float[] phase, out float[] rate, out float[] depth, out Vector3[] dirs, out float[] pixels)
        {
            var pos = new List<Vector3>();
            var uv = new List<Vector2>();
            var col = new List<Color32>();
            phase = new float[STARS];
            rate = new float[STARS];
            depth = new float[STARS];
            dirs = new Vector3[STARS];
            pixels = new float[STARS];
            for (int i = 0; i < STARS; i++)
            {
                float u = (float)rng.NextDouble();
                var d = Dir(Range(rng, 0, 360), 1 + 89 * u * u);
                // Brightness: most dim, a handful bright (and a touch bigger).
                float b = Mathf.Pow((float)rng.NextDouble(), 3.5f);
                depth[i] = 0.35f + 0.65f * b;
                pixels[i] = 0.7f + 1.3f * b;
                double hue = rng.NextDouble();
                var c = hue < 0.15 ? new Color32(205, 220, 255, 255) : hue < 0.25 ? new Color32(255, 232, 196, 255) : new Color32(255, 255, 255, 255);
                Billboard(pos, uv, col, d, 0.001f, 0.001f, c, c);
                dirs[i] = d;
                phase[i] = Range(rng, 0, 2 * Math.PI);
                rate[i] = Range(rng, 1.5, 5);
            }
            colors = col.ToArray();
            var mesh = Build("stars", pos, uv, col, new Bounds(Vector3.zero, Vector3.one * 2));
            mesh.MarkDynamic();
            return mesh;
        }

        /// <summary>Cumulus all round the horizon: each a row of puffs, the big ones in the middle, greyer underneath.</summary>
        static Mesh ShellClouds(System.Random rng)
        {
            var pos = new List<Vector3>();
            var uv = new List<Vector2>();
            var col = new List<Color32>();
            var under = new Color32(205, 212, 226, 255);
            var over = new Color32(255, 255, 255, 255);
            for (int c = 0; c < CLOUDS; c++)
            {
                float az = (c + Range(rng, -0.3, 0.3)) * 360f / CLOUDS;
                float el = 1 + 18 * Mathf.Pow((float)rng.NextDouble(), 1.6f);
                float width = Range(rng, 6, 13);
                int puffs = rng.Next(4, 7);
                for (int p = 0; p < puffs; p++)
                {
                    float along = (p + 0.5f) / puffs - 0.5f;
                    float big = 1 - Mathf.Abs(along) * 1.2f;
                    float size = width * Range(rng, 0.22, 0.32) * (0.6f + 0.6f * big) * Mathf.Deg2Rad;
                    var d = Dir(az + along * width + Range(rng, -0.5, 0.5), el + size * Mathf.Rad2Deg * Range(rng, 0.2, 0.5) * big);
                    Billboard(pos, uv, col, d, size * 1.3f, size, under, over, Range(rng, -0.35, 0.35));
                }
            }
            return Build("sky clouds", pos, uv, col, new Bounds(Vector3.zero, Vector3.one * 2));
        }

        /// <summary>Flat clouds in one square of the field (they may hang over its edge): clumps of puffs, lying level.</summary>
        static Mesh FieldClouds(System.Random rng)
        {
            var pos = new List<Vector3>();
            var uv = new List<Vector2>();
            var col = new List<Color32>();
            var white = new Color32(255, 255, 255, 255);
            var grey = new Color32(222, 228, 238, 255);
            for (int c = 0; c < FIELD_CLOUDS; c++)
            {
                var at = new Vector3(Range(rng, 0, FIELD_SIZE), 0, Range(rng, 0, FIELD_SIZE));
                float spread = Range(rng, 2, 4);
                int puffs = rng.Next(5, 9);
                for (int p = 0; p < puffs; p++)
                {
                    float a = Range(rng, 0, 2 * Math.PI), k = Mathf.Sqrt((float)rng.NextDouble());
                    var o = at + new Vector3(Mathf.Cos(a) * spread * 1.4f, 0, Mathf.Sin(a) * spread) * k;
                    float s = Range(rng, 1.2, 2.4) * (1.2f - 0.5f * k);
                    var turn = Quaternion.Euler(0, Range(rng, 0, 360), 0);
                    pos.Add(o + turn * new Vector3(-s, 0, -s));
                    pos.Add(o + turn * new Vector3(s, 0, -s));
                    pos.Add(o + turn * new Vector3(s, 0, s));
                    pos.Add(o + turn * new Vector3(-s, 0, s));
                    Corners(uv, col, grey, white);
                }
            }
            var bounds = new Bounds(new Vector3(FIELD_SIZE / 2, 0, FIELD_SIZE / 2), new Vector3(FIELD_SIZE + 30, 1, FIELD_SIZE + 30));
            return Build("clouds", pos, uv, col, bounds);
        }
    }
}

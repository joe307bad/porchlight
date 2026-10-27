// Kenney's CC0 3D kits and RG Poly's CC0 Cartoon City people, loaded as
// render/iso/assets.ts loads them: every static model flattened into one mesh
// per material, the city-kit buildings' window panes tagged and their
// materials clipped to stand on the modelled ground floors and lit at night,
// the kits' colour variations, and the rigged people with their shared clips.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Porchlight.Core;
using Porchlight.Explore;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public sealed class PrefabPart
    {
        public Mesh mesh;
        public Material material;
    }

    /// <summary>A static model baked to model-space geometry, one part per material.</summary>
    public sealed class Prefab
    {
        public string name;
        public List<PrefabPart> parts = new List<PrefabPart>();
        /// <summary>Bounding box (model units, Three space).</summary>
        public double minX, minY, minZ, sizeX, sizeY, sizeZ;
    }

    public sealed class TownAssets
    {
        readonly Dictionary<string, Prefab> prefabs = new Dictionary<string, Prefab>();
        readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        readonly Dictionary<string, Prefab> variants = new Dictionary<string, Prefab>();
        readonly Dictionary<string, Texture2D[]> variationMaps = new Dictionary<string, Texture2D[]>();
        readonly Dictionary<string, CharacterAsset> characters = new Dictionary<string, CharacterAsset>();
        readonly Dictionary<(Material, int), Material> recoloured = new Dictionary<(Material, int), Material>();
        public PeopleClips Clips { get; private set; }

        public Prefab Get(string name)
        {
            if (!prefabs.TryGetValue(name, out var p)) throw new Exception("unknown model " + name);
            return p;
        }

        public int VariationCount(string name) => Catalog.VariationCount(name);

        /// <summary>The same model in one of its kit's colour variations (0 = the default colourmap).</summary>
        public Prefab Variant(string name, int variation)
        {
            var b = Get(name);
            string kit = name.Split('/')[0];
            if (variation == 0 || !variationMaps.TryGetValue(kit, out var maps) || variation - 1 >= maps.Length) return b;
            string key = name + "#" + variation;
            if (variants.TryGetValue(key, out var p)) return p;
            p = new Prefab { name = b.name, minX = b.minX, minY = b.minY, minZ = b.minZ, sizeX = b.sizeX, sizeY = b.sizeY, sizeZ = b.sizeZ };
            foreach (var part in b.parts)
            {
                if (!recoloured.TryGetValue((part.material, variation), out var m))
                {
                    m = new Material(part.material);
                    var old = part.material.GetTexture("_MainTex") as Texture2D;
                    var tex = maps[variation - 1];
                    if (old != null)
                    {
                        tex.filterMode = old.filterMode;
                        tex.wrapMode = old.wrapMode;
                    }
                    m.SetTexture("_MainTex", tex);
                    recoloured[(part.material, variation)] = m;
                }
                p.parts.Add(new PrefabPart { mesh = part.mesh, material = m });
            }
            variants[key] = p;
            return p;
        }

        public CharacterAsset Character(string name)
        {
            if (!characters.TryGetValue(name, out var c)) throw new Exception("unknown character " + name);
            return c;
        }

        public static async Task<TownAssets> Load(Action<float> progress = null)
        {
            var assets = new TownAssets();
            var statics = Catalog.Statics.ToList();
            int total = statics.Count + Catalog.CHARACTERS.Length + 1 + 5;
            int done = 0;
            void Step()
            {
                done++;
                progress?.Invoke(done / (float)total);
            }

            // Fetch every file at once (the web loads them in parallel), then build in order.
            var staticFiles = statics.Select(n => Files.Model(n + ".glb")).ToList();
            var characterFiles = Catalog.CHARACTERS.Select(n => Files.Model(n + ".glb")).ToList();
            var clipFile = Files.Model(Catalog.PEOPLE_CLIPS + ".glb");
            var variationLoads = new List<(string kit, Task<Texture2D>[] maps)>();
            foreach (var kv in Catalog.VARIATIONS)
                variationLoads.Add((kv.Key, kv.Value.Select(f => assets.LoadTexture($"{kv.Key}/Textures/{f}.png")).ToArray()));

            for (int i = 0; i < statics.Count; i++)
            {
                var name = statics[i];
                var glb = new Glb(await staticFiles[i]);
                var data = ModelData.Flatten(name, glb);
                assets.prefabs[name] = await assets.BuildPrefab(name, data);
                Step();
            }
            foreach (var (kit, maps) in variationLoads)
            {
                var list = new Texture2D[maps.Length];
                for (int k = 0; k < maps.Length; k++)
                {
                    list[k] = await maps[k];
                    Step();
                }
                assets.variationMaps[kit] = list;
            }
            var clipGlb = new Glb(await clipFile);
            assets.Clips = PeopleClips.From(clipGlb);
            Step();
            Texture2D atlas = null;
            for (int i = 0; i < Catalog.CHARACTERS.Length; i++)
            {
                var name = Catalog.CHARACTERS[i];
                var glb = new Glb(await characterFiles[i]);
                // Every person shares the one atlas: load it once.
                if (atlas == null)
                {
                    var m = ModelData.ReadMaterial(glb, 0);
                    if (m.imageUri != null) atlas = await assets.LoadTexture(Files.Beside(name, m.imageUri), m);
                }
                assets.characters[name] = CharacterAsset.Create(name, glb, atlas, assets.Clips);
                Step();
            }
            return assets;
        }

        static bool IsBuilding(string name) => name.StartsWith("suburban/building") || name.StartsWith("commercial/building");

        async Task<Prefab> BuildPrefab(string name, ModelData data)
        {
            var prefab = new Prefab { name = name, minX = data.minX, minY = data.minY, minZ = data.minZ, sizeX = data.sizeX, sizeY = data.sizeY, sizeZ = data.sizeZ };
            var materials = new Material[data.materials.Count];
            var maps = new Texture2D[data.materials.Count];
            for (int i = 0; i < data.materials.Count; i++)
            {
                var mm = data.materials[i];
                Texture2D map = null;
                if (mm.imageUri != null) map = await LoadTexture(Files.Beside(name, mm.imageUri), mm);
                maps[i] = map;
                // The nature kit's materials say fully metallic: keep everything matte (as assets.ts does).
                var color = new Color((float)mm.r, (float)mm.g, (float)mm.b, 1);
                materials[i] = Mats.Standard(color, map, mm.roughness, windows: IsBuilding(name), clipGround: IsBuilding(name));
                materials[i].name = $"{name}:{mm.name}";
            }
            foreach (var part in data.parts)
            {
                var p = part;
                if (IsBuilding(name))
                {
                    var map = maps[part.material];
                    p = part.NonIndexed();
                    byte[] pixels = map != null ? TopFirstRgba(map) : null;
                    p.windows = WindowTags.Tag(p.pos, p.uv, pixels, map != null ? map.width : 0, map != null ? map.height : 0);
                }
                prefab.parts.Add(new PrefabPart { mesh = ToMesh(name, p), material = materials[part.material] });
            }
            return prefab;
        }

        /// <summary>A model part as a Unity mesh: Three's coordinates to Unity's, glTF UVs (v down the image) to Unity's (v up).</summary>
        public static Mesh ToMesh(string name, ModelPart p)
        {
            int n = p.VertexCount;
            var vertices = new Vector3[n];
            var normals = p.normal != null ? new Vector3[n] : null;
            var uv = p.uv != null ? new Vector2[n] : null;
            for (int i = 0; i < n; i++)
            {
                vertices[i] = new Vector3(p.pos[i * 3], p.pos[i * 3 + 1], -p.pos[i * 3 + 2]);
                if (normals != null) normals[i] = new Vector3(p.normal[i * 3], p.normal[i * 3 + 1], -p.normal[i * 3 + 2]);
                if (uv != null) uv[i] = new Vector2(p.uv[i * 2], 1 - p.uv[i * 2 + 1]);
            }
            var index = p.index;
            if (index == null)
            {
                index = new int[n];
                for (int i = 0; i < n; i++) index[i] = i;
            }
            var tris = new int[index.Length];
            for (int t = 0; t + 2 < index.Length; t += 3)
            {
                tris[t] = index[t];
                tris[t + 1] = index[t + 2];
                tris[t + 2] = index[t + 1];
            }
            var mesh = new Mesh { name = name, indexFormat = n > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            if (normals != null) mesh.SetNormals(normals);
            if (uv != null) mesh.SetUVs(0, uv);
            if (p.windows != null)
            {
                var seeds = new Vector2[n];
                var grid = new Vector4[n];
                for (int i = 0; i < n; i++)
                {
                    seeds[i] = new Vector2(p.windows.seeds[i], p.windows.axis[i]);
                    grid[i] = new Vector4(p.windows.grid[i * 4], p.windows.grid[i * 4 + 1], p.windows.grid[i * 4 + 2], p.windows.grid[i * 4 + 3]);
                }
                mesh.SetUVs(1, seeds);
                mesh.SetUVs(2, grid);
            }
            mesh.SetTriangles(tris, 0, true);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }

        /// <summary>A texture's pixels as RGBA rows, top row first (as a canvas reads them).</summary>
        static byte[] TopFirstRgba(Texture2D tex)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            var o = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int src = (h - 1 - y) * w;
                int dst = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    var c = px[src + x];
                    o[dst + x * 4] = c.r;
                    o[dst + x * 4 + 1] = c.g;
                    o[dst + x * 4 + 2] = c.b;
                    o[dst + x * 4 + 3] = c.a;
                }
            }
            return o;
        }

        readonly Dictionary<string, Task<Texture2D>> loading = new Dictionary<string, Task<Texture2D>>();

        /// <summary>An sRGB colour texture (cached per file), sampled the way the glTF sampler (or Three's defaults) say.</summary>
        public Task<Texture2D> LoadTexture(string path, ModelMaterial sampler = null)
        {
            if (!loading.TryGetValue(path, out var t))
            {
                t = LoadTextureNow(path, sampler);
                loading[path] = t;
            }
            return t;
        }

        async Task<Texture2D> LoadTextureNow(string path, ModelMaterial sampler)
        {
            var bytes = await Files.Model(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = path };
            tex.LoadImage(bytes, false);
            ApplySampler(tex, sampler);
            tex.Apply(true, false);
            textures[path] = tex;
            return tex;
        }

        /// <summary>glTF sampler filters as Three applies them (no sampler: linear, mipmapped).</summary>
        static void ApplySampler(Texture2D tex, ModelMaterial s)
        {
            int mag = s?.magFilter ?? -1;
            int min = s?.minFilter ?? -1;
            if (mag == 9728) tex.filterMode = FilterMode.Point;
            else if (min == 9729 || min == 9728 || min == 9985) tex.filterMode = FilterMode.Bilinear;
            else tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 1;
            tex.wrapModeU = Wrap(s?.wrapS ?? 10497);
            tex.wrapModeV = Wrap(s?.wrapT ?? 10497);
        }

        static TextureWrapMode Wrap(int gl) => gl == 33071 ? TextureWrapMode.Clamp : gl == 33648 ? TextureWrapMode.Mirror : TextureWrapMode.Repeat;
    }
}

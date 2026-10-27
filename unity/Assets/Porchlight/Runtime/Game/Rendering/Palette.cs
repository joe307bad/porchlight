// The colours of the town's procedural geometry (ground, roads, walls,
// sheds): each colour is one texel of a small texture, and a mesh's UVs point
// at its colours, so the whole lot draws with one URP Lit material. A colour
// can also glow (window glass at night): its texel in the emission map is
// lit, and Materials.WindowGlow sets how brightly.

using System.Collections.Generic;
using UnityEngine;

namespace Porchlight.Rendering
{
    public sealed class Palette
    {
        const int SIZE = 256;
        readonly Texture2D albedo;
        readonly Texture2D emission;
        readonly Color32[] albedoPixels = new Color32[SIZE * SIZE];
        readonly Color32[] emissionPixels = new Color32[SIZE * SIZE];
        readonly Dictionary<(int, int), int> index = new Dictionary<(int, int), int>();
        bool dirty;
        Material material;

        public Palette()
        {
            albedo = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false, false) { name = "palette", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            emission = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false, false) { name = "palette glow", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        }

        /// <summary>Where colour `hex` is (and, if `glow` isn't 0, the colour it glows at night).</summary>
        public Vector2 UV(int hex, int glow = 0)
        {
            if (!index.TryGetValue((hex, glow), out int i))
            {
                i = index.Count;
                if (i >= SIZE * SIZE) i = SIZE * SIZE - 1;
                else index[(hex, glow)] = i;
                albedoPixels[i] = (Color32)Coords.Hex(hex);
                emissionPixels[i] = glow != 0 ? (Color32)Coords.Hex(glow) : new Color32(0, 0, 0, 255);
                dirty = true;
            }
            return new Vector2((i % SIZE + 0.5f) / SIZE, (i / SIZE + 0.5f) / SIZE);
        }

        /// <summary>The material every palette mesh draws with (its textures brought up to date).</summary>
        public Material Material
        {
            get
            {
                if (dirty)
                {
                    albedo.SetPixels32(albedoPixels);
                    albedo.Apply(false);
                    emission.SetPixels32(emissionPixels);
                    emission.Apply(false);
                    dirty = false;
                }
                return material ??= Materials.Textured(albedo, emission, "palette");
            }
        }
    }
}

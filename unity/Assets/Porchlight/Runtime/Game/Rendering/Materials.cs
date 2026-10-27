// The town's materials: URP Lit and Unlit, copied from the library's
// templates and shared by everything that looks the same. A model's own
// materials can be swapped for one of its kit's colour variations, and a
// building's for a copy whose windows glow at night (WindowGlow sets how
// brightly, on every one at once).

using System.Collections.Generic;
using UnityEngine;

namespace Porchlight.Rendering
{
    public static class Materials
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");
        static readonly int Smoothness = Shader.PropertyToID("_Smoothness");

        static readonly Dictionary<(Color, float), Material> lit = new Dictionary<(Color, float), Material>();
        static readonly Dictionary<Color, Material> unlit = new Dictionary<Color, Material>();
        static readonly Dictionary<(Material, int, bool), Material> models = new Dictionary<(Material, int, bool), Material>();
        static readonly List<Material> windows = new List<Material>();

        static ModelLibrary Library => ModelLibrary.Instance;

        /// <summary>A matte colour (shininess 0..1).</summary>
        public static Material Lit(Color color, float smoothness = 0.1f)
        {
            if (!lit.TryGetValue((color, smoothness), out var m))
            {
                m = new Material(Library.lit) { name = "lit " + ColorUtility.ToHtmlStringRGB(color), enableInstancing = true };
                m.SetColor(BaseColor, color);
                m.SetFloat(Smoothness, smoothness);
                lit[(color, smoothness)] = m;
            }
            return m;
        }

        public static Material Lit(int hex, float smoothness = 0.1f) => Lit(Coords.Hex(hex), smoothness);

        /// <summary>A textured surface that can glow where its emission map is lit (night windows).</summary>
        public static Material Textured(Texture map, Texture emission, string name)
        {
            var m = new Material(emission != null ? Library.litEmissive : Library.lit) { name = name, enableInstancing = true };
            m.SetTexture(BaseMap, map);
            m.SetColor(BaseColor, Color.white);
            m.SetFloat(Smoothness, 0.1f);
            if (emission != null)
            {
                m.SetTexture(EmissionMap, emission);
                m.SetColor(EmissionColor, Color.black);
                windows.Add(m);
            }
            return m;
        }

        /// <summary>Flat colour, unaffected by light (lamp bulbs by day, the route line).</summary>
        public static Material Unlit(Color color)
        {
            if (!unlit.TryGetValue(color, out var m))
            {
                m = new Material(Library.unlit) { name = "unlit " + ColorUtility.ToHtmlStringRGB(color), enableInstancing = true };
                m.SetColor(BaseColor, color);
                unlit[color] = m;
            }
            return m;
        }

        /// <summary>A lamp: lit by day, glowing (with bloom) by night. Brightness is set per lamp with SetGlow.</summary>
        public static Material Glowing(Color color)
        {
            var m = new Material(Library.litEmissive) { name = "glow " + ColorUtility.ToHtmlStringRGB(color), enableInstancing = true };
            m.SetColor(BaseColor, color);
            m.SetTexture(EmissionMap, Texture2D.whiteTexture);
            m.SetColor(EmissionColor, Color.black);
            return m;
        }

        static MaterialPropertyBlock block;

        /// <summary>How brightly one renderer's glowing material shines (HDR: above 1 blooms).</summary>
        public static void SetGlow(Renderer r, Color color, float intensity)
        {
            block ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            block.SetColor(EmissionColor, color * intensity);
            r.SetPropertyBlock(block);
        }

        public static Material Particles(Color color)
        {
            var m = new Material(Library.particles) { name = "particles", enableInstancing = true };
            m.SetColor(BaseColor, color);
            return m;
        }

        /// <summary>
        /// A model's material in one of its kit's colour variations (0: as made), with
        /// its windows able to glow if `windows`.
        /// </summary>
        public static Material ForModel(Material source, ModelLibrary.Kit kit, int variation, bool windows)
        {
            if (kit == null || source == null) return source;
            bool recolour = variation > 0 && variation < kit.maps.Length;
            Texture2D glow = windows && kit.windows != null && variation < kit.windows.Length ? kit.windows[variation] : null;
            if (!recolour && glow == null) return source;
            var key = (source, variation, glow != null);
            if (models.TryGetValue(key, out var m)) return m;
            var map = recolour ? kit.maps[variation] : source.GetTexture(BaseMap);
            if (map == null || source.GetTexture(BaseMap) != kit.maps[0])
            {
                // Not drawn from the kit's colour map (a plain colour): leave it.
                models[key] = source;
                return source;
            }
            m = Textured(map, glow, $"{source.name} v{variation}{(glow != null ? " lit" : "")}");
            m.SetColor(BaseColor, source.GetColor(BaseColor));
            models[key] = m;
            return m;
        }

        /// <summary>Every lit window, 0 (day) to 1 (night).</summary>
        public static void WindowGlow(float level, Color warm)
        {
            var c = warm * (level * 1.6f);
            foreach (var m in windows) m.SetColor(EmissionColor, c);
        }
    }
}

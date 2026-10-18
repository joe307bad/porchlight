using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public enum FontWeight
    {
        Regular,
        SemiBold,
    }

    /// <summary>
    /// Inter, for everything smaller than a header. Text lies flat on the XZ plane like
    /// <see cref="BlockText"/>, lifted a little off its panel with a soft shadow underneath. Glyphs
    /// come from Unity's dynamic font atlas, so any character in the font works.
    /// </summary>
    public static class FlatText
    {
        /// <summary>Atlas pixels per em. Text is scaled from this, so it only affects sharpness.</summary>
        public const int AtlasSize = 64;

        private const string Printable = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~·…←→↑↓";

        private static Font[] _fonts;
        private static Material[] _materials;

        /// <summary>
        /// Raised when the atlas is rebuilt (a new character came in) and old glyph positions are stale.
        /// Anything showing text should rebuild its meshes.
        /// </summary>
        public static event Action Rebuilt;

        /// <summary>One material per <see cref="FontWeight"/>, in order; text meshes have a submesh for each.</summary>
        public static Material[] Materials
        {
            get
            {
                Load();
                return _materials;
            }
        }

        public static Font FontFor(FontWeight weight)
        {
            Load();
            return _fonts[(int)weight];
        }

        /// <summary>Width of <paramref name="text"/> in metres, where <paramref name="size"/> is the em size in metres.</summary>
        public static float Width(string text, float size, FontWeight weight = FontWeight.Regular)
        {
            var font = FontFor(weight);
            font.RequestCharactersInTexture(text, AtlasSize);
            var width = 0f;
            foreach (var c in text)
            {
                if (font.GetCharacterInfo(c, out var info, AtlasSize)) width += info.advance;
            }

            return width * size / AtlasSize;
        }

        private static void Load()
        {
            if (_fonts != null) return;
            _fonts = new[] { Resources.Load<Font>("Fonts/Inter-Regular"), Resources.Load<Font>("Fonts/Inter-SemiBold") };
            var shader = Resources.Load<Shader>("PorchlightText");
            if (shader == null) Debug.LogError("PorchlightText shader missing from Resources.");

            _materials = new Material[_fonts.Length];
            for (var i = 0; i < _fonts.Length; i++)
            {
                _fonts[i].RequestCharactersInTexture(Printable, AtlasSize);
                _materials[i] = new Material(shader) { name = _fonts[i].name, mainTexture = _fonts[i].material.mainTexture };
            }

            Font.textureRebuilt += OnTextureRebuilt;
        }

        private static void OnTextureRebuilt(Font font)
        {
            var index = Array.IndexOf(_fonts, font);
            if (index < 0) return;
            _materials[index].mainTexture = font.material.mainTexture;
            Rebuilt?.Invoke();
        }

        /// <summary>A child object that draws a <see cref="TextBuilder"/> mesh.</summary>
        public static MeshFilter CreateRenderer(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = Materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = new Mesh { name = name };
            return filter;
        }
    }

    /// <summary>Collects lines of <see cref="FlatText"/> into one mesh, with a submesh per font weight.</summary>
    public sealed class TextBuilder
    {
        /// <summary>How far text floats above its panel, and how far its shadow falls, per metre of text size.</summary>
        private const float Lift = 0.1f;
        private const float ShadowOffset = 0.06f;

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<Color> _colors = new List<Color>();
        private readonly List<int>[] _triangles = { new List<int>(), new List<int>() };

        /// <summary>
        /// Adds a line of text. <paramref name="anchor"/> is the surface it sits on: x is the left edge,
        /// centre or right edge depending on <paramref name="align"/>, and z is the middle of the
        /// capital letters. <paramref name="size"/> is the em size in metres. Text wider than
        /// <paramref name="maxWidth"/> (when set) is cut short with an ellipsis.
        /// </summary>
        public void Add(string text, Vector3 anchor, float size, Color color, TextAlign align = TextAlign.Left,
            FontWeight weight = FontWeight.Regular, float maxWidth = 0f)
        {
            if (string.IsNullOrEmpty(text)) return;
            var font = FlatText.FontFor(weight);
            text = Fit(text, size, weight, maxWidth);
            font.RequestCharactersInTexture(text + "H", FlatText.AtlasSize);

            var width = FlatText.Width(text, size, weight);
            var x = align == TextAlign.Right ? anchor.x - width : align == TextAlign.Center ? anchor.x - width * 0.5f : anchor.x;
            font.GetCharacterInfo('H', out var capital, FlatText.AtlasSize);
            var baseline = anchor.z - capital.maxY * size / FlatText.AtlasSize * 0.5f;

            // A soft shadow on the panel, then the text itself floating just above it.
            var shadow = new Color(0f, 0.05f, 0.15f, 0.28f * color.a);
            Emit(font, text, new Vector3(x + size * ShadowOffset, anchor.y + 0.005f, baseline - size * ShadowOffset * 1.4f), size, shadow, weight);
            Emit(font, text, new Vector3(x, anchor.y + size * Lift, baseline), size, color, weight);
        }

        public Mesh ToMesh(Mesh target)
        {
            target.Clear();
            target.indexFormat = _vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            target.SetVertices(_vertices);
            target.SetUVs(0, _uvs);
            target.SetColors(_colors);
            target.subMeshCount = _triangles.Length;
            for (var i = 0; i < _triangles.Length; i++)
            {
                target.SetTriangles(_triangles[i], i);
            }

            target.RecalculateBounds();
            return target;
        }

        private void Emit(Font font, string text, Vector3 origin, float size, Color color, FontWeight weight)
        {
            var scale = size / FlatText.AtlasSize;
            var pen = origin.x;
            var triangles = _triangles[(int)weight];
            foreach (var c in text)
            {
                if (!font.GetCharacterInfo(c, out var info, FlatText.AtlasSize)) continue;

                float x0 = pen + info.minX * scale, x1 = pen + info.maxX * scale;
                float z0 = origin.z + info.minY * scale, z1 = origin.z + info.maxY * scale;
                var i = _vertices.Count;
                _vertices.Add(new Vector3(x0, origin.y, z0));
                _vertices.Add(new Vector3(x0, origin.y, z1));
                _vertices.Add(new Vector3(x1, origin.y, z1));
                _vertices.Add(new Vector3(x1, origin.y, z0));
                _uvs.Add(info.uvBottomLeft);
                _uvs.Add(info.uvTopLeft);
                _uvs.Add(info.uvTopRight);
                _uvs.Add(info.uvBottomRight);
                for (var k = 0; k < 4; k++) _colors.Add(color);
                triangles.Add(i);
                triangles.Add(i + 1);
                triangles.Add(i + 2);
                triangles.Add(i);
                triangles.Add(i + 2);
                triangles.Add(i + 3);
                pen += info.advance * scale;
            }
        }

        private static string Fit(string text, float size, FontWeight weight, float maxWidth)
        {
            if (maxWidth <= 0f || FlatText.Width(text, size, weight) <= maxWidth) return text;
            for (var length = text.Length - 1; length > 0; length--)
            {
                var cut = text.Substring(0, length).TrimEnd() + "…";
                if (FlatText.Width(cut, size, weight) <= maxWidth) return cut;
            }

            return "…";
        }
    }
}

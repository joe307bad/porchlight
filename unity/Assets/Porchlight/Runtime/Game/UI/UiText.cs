// Text drawn into textures for the 3D town, as textures.ts and remotes.ts
// draw it on a canvas: the street-sign atlas (every name in white capitals on
// a green blade with a white border, two columns of 512 x 100 cells) and the
// players' name tags (a dark pill with the name in cream). Each is a UI
// Toolkit panel rendering into a render texture.

using System;
using System.Collections.Generic;
using Porchlight.Rendering;
using UnityEngine;
using Space = Porchlight.Rendering.Space;
using UnityEngine.UIElements;

namespace Porchlight.UI
{
    public sealed class UiText : ITextMaker
    {
        const int CELL_W = 512;
        const int CELL_H = 100;
        const int ATLAS_COLS = 2;
        static readonly Color SignGreen = Space.CssHex(0x24603f);
        static readonly Color Cream = Space.CssHex(0xf4f1ea);

        readonly PanelSettings template;
        readonly Fonts fonts;
        readonly Transform parent;
        readonly Dictionary<Texture, GameObject> panels = new Dictionary<Texture, GameObject>();

        public UiText(PanelSettings template, Fonts fonts, Transform parent)
        {
            this.template = template;
            this.fonts = fonts;
            this.parent = parent;
        }

        (RenderTexture, VisualElement) Panel(string name, int width, int height, bool mipmaps)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = name,
                useMipMap = mipmaps,
                autoGenerateMips = mipmaps,
                filterMode = mipmaps ? FilterMode.Trilinear : FilterMode.Bilinear,
                anisoLevel = mipmaps ? 4 : 1,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            var settings = UnityEngine.Object.Instantiate(template);
            settings.targetTexture = rt;
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            settings.scale = 1;
            settings.clearColor = true;
            settings.colorClearValue = new Color(0, 0, 0, 0);
            settings.sortingOrder = -100;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            var root = doc.rootVisualElement;
            root.style.width = width;
            root.style.height = height;
            root.pickingMode = PickingMode.Ignore;
            panels[rt] = go;
            return (rt, root);
        }

        public Texture SignAtlas(IReadOnlyList<string> names, out Func<int, Vector4> cell)
        {
            int rows = Math.Max(1, (int)Math.Ceiling(names.Count / (double)ATLAS_COLS));
            int width = CELL_W * ATLAS_COLS, height = CELL_H * rows;
            var (rt, root) = Panel("sign atlas", width, height, true);
            for (int i = 0; i < names.Count; i++)
            {
                int x = (i % ATLAS_COLS) * CELL_W;
                int y = (i / ATLAS_COLS) * CELL_H;
                var c = new VisualElement();
                c.style.position = Position.Absolute;
                c.style.left = x;
                c.style.top = y;
                c.style.width = CELL_W;
                c.style.height = CELL_H;
                c.style.backgroundColor = SignGreen;
                // strokeRect(x + 9, y + 9, W - 18, H - 18) with a 6 px line: the stroke spans 6..12 px in from each edge.
                var border = new VisualElement();
                border.style.position = Position.Absolute;
                border.style.left = 6;
                border.style.top = 6;
                border.style.width = CELL_W - 12;
                border.style.height = CELL_H - 12;
                foreach (var side in new Action<StyleFloat>[] { v => border.style.borderTopWidth = v, v => border.style.borderBottomWidth = v, v => border.style.borderLeftWidth = v, v => border.style.borderRightWidth = v }) side(6);
                border.style.borderTopColor = border.style.borderBottomColor = border.style.borderLeftColor = border.style.borderRightColor = Cream;
                c.Add(border);
                string text = names[i].ToUpperInvariant();
                double size = 54;
                const double room = CELL_W - 48;
                double wide = fonts.Measure(text, (int)size, true);
                if (wide > room) size = Math.Floor((size * room) / wide);
                var label = new Label(text);
                label.style.position = Position.Absolute;
                label.style.left = 0;
                label.style.right = 0;
                label.style.top = 3;
                label.style.bottom = 0;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.fontSize = (float)size;
                label.style.color = Cream;
                label.style.unityFontDefinition = new StyleFontDefinition(fonts.Bold);
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.paddingLeft = label.style.paddingRight = label.style.paddingTop = label.style.paddingBottom = 0;
                label.style.marginLeft = label.style.marginRight = label.style.marginTop = label.style.marginBottom = 0;
                c.Add(label);
                root.Add(c);
            }
            const double inset = 0.5;
            cell = i =>
            {
                int col = i % ATLAS_COLS;
                int row = i / ATLAS_COLS;
                return new Vector4(
                    (float)((col * CELL_W + inset) / width),
                    (float)(((col + 1) * CELL_W - inset) / width),
                    (float)(1 - ((row + 1) * CELL_H - inset) / height),
                    (float)(1 - (row * CELL_H + inset) / height));
            };
            return rt;
        }

        public Texture NameTag(string text, out int width, out int height)
        {
            width = (int)Math.Ceiling(fonts.Measure(text, 40, true)) + 36;
            height = 64;
            var (rt, root) = Panel("tag " + text, width, height, false);
            var pill = new VisualElement();
            pill.style.position = Position.Absolute;
            pill.style.left = 0;
            pill.style.top = 6;
            pill.style.width = width;
            pill.style.height = 52;
            pill.style.borderTopLeftRadius = pill.style.borderTopRightRadius = pill.style.borderBottomLeftRadius = pill.style.borderBottomRightRadius = 26;
            pill.style.backgroundColor = new Color(28 / 255f, 27 / 255f, 24 / 255f, 0.82f);
            root.Add(pill);
            var label = new Label(text);
            label.style.position = Position.Absolute;
            label.style.left = 0;
            label.style.right = 0;
            label.style.top = 2;
            label.style.bottom = 0;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.fontSize = 40;
            label.style.color = Space.CssHex(0xfff8e8);
            label.style.unityFontDefinition = new StyleFontDefinition(fonts.SemiBold);
            label.style.paddingLeft = label.style.paddingRight = label.style.paddingTop = label.style.paddingBottom = 0;
            root.Add(label);
            return rt;
        }

        public void Release(Texture t)
        {
            if (t == null) return;
            if (panels.TryGetValue(t, out var go))
            {
                UnityEngine.Object.Destroy(go);
                panels.Remove(t);
            }
            if (t is RenderTexture rt) rt.Release();
            UnityEngine.Object.Destroy(t);
        }
    }

    /// <summary>The fonts the game draws with (Inter, as the browser's system UI font; Roboto Mono for monospace).</summary>
    public sealed class Fonts
    {
        public Font Regular;
        public Font SemiBold;
        public Font Bold;
        public Font Mono;

        public static Fonts Load()
        {
            var f = new Fonts
            {
                Regular = Resources.Load<Font>("Porchlight/Fonts/Inter-Regular"),
                SemiBold = Resources.Load<Font>("Porchlight/Fonts/Inter-SemiBold"),
                Mono = Resources.Load<Font>("Porchlight/Fonts/RobotoMono-Regular"),
            };
            f.Bold = f.SemiBold;
            return f;
        }

        /// <summary>About how wide a line of text is at a pixel size (canvas measureText).</summary>
        public double Measure(string text, int size, bool bold)
        {
            var font = bold ? SemiBold : Regular;
            if (font == null) return text.Length * size * 0.55;
            var style = bold ? FontStyle.Bold : FontStyle.Normal;
            font.RequestCharactersInTexture(text, size, style);
            double w = 0;
            foreach (char ch in text) if (font.GetCharacterInfo(ch, out var info, size, style)) w += info.advance;
            return w;
        }
    }
}

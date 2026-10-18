using System;
using System.Collections.Generic;
using Porchlight.Rendering;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>What the bottom bar shows. The bar redraws only when <see cref="StateKey"/> changes.</summary>
    public interface IHudContent
    {
        /// <summary>Changes whenever anything drawn would change.</summary>
        string StateKey { get; }

        void Draw(HudCanvas canvas);
    }

    /// <summary>
    /// Drawing kit for the bottom bar, in the menus' style: block-lettered section headers, Inter for
    /// everything else, and square-cornered Material Design 3 components (buttons, cards, switches).
    /// Coordinates are bar units: x to the right, z up, with (0, 0) in the middle of the bar.
    /// </summary>
    public sealed class HudCanvas
    {
        public const float HeaderUnit = 0.16f;
        public const float TitleSize = 1.2f;
        public const float BodySize = 0.9f;
        public const float LabelSize = 0.78f;

        // Elevation and outline, scaled down from the plaza menus to suit the bar.
        private const float Rim = 0.12f;
        private const float Thickness = 0.06f;
        private const float Raise = 0.22f;
        private const float ShadowOffset = 0.16f;

        internal readonly MeshBuilder Shapes = new MeshBuilder();
        internal readonly TextBuilder Text = new TextBuilder();
        internal readonly List<(Rect Rect, Action Action)> Hits = new List<(Rect, Action)>();
        internal readonly List<(Texture Texture, Rect Rect, Rect Uv, float Y)> Images = new List<(Texture, Rect, Rect, float)>();

        public HudCanvas(float width, float height, Vector2? pointer)
        {
            Right = width * 0.5f;
            Left = -Right;
            Top = height * 0.5f;
            Bottom = -Top;
            Pointer = pointer;
        }

        public float Left { get; }
        public float Right { get; }
        public float Top { get; }
        public float Bottom { get; }

        /// <summary>Where the pointer is, in bar units, or null when it's outside the bar.</summary>
        public Vector2? Pointer { get; }

        public bool IsHovered(Rect rect) => Pointer.HasValue && rect.Contains(Pointer.Value);

        /// <summary>Makes <paramref name="rect"/> clickable.</summary>
        public void OnClick(Rect rect, Action action) => Hits.Add((rect, action));

        /// <summary>A section header in block lettering, its letters centred on <paramref name="z"/>.</summary>
        public void Header(string text, float x, float z)
        {
            BlockText.Add(Shapes, text, new Vector3(x, 0.02f, z), HeaderUnit, 0.08f, MenuStyle.OnPrimary, MenuStyle.Primary, TextAlign.Left, detail: 2);
        }

        public void Label(string text, float x, float z, float size, Color color, TextAlign align = TextAlign.Left,
            FontWeight weight = FontWeight.Regular, float maxWidth = 0f, float y = 0.02f)
        {
            Text.Add(text, new Vector3(x, y, z), size, color, align, weight, maxWidth);
        }

        /// <summary>
        /// A sharp panel. Resting panels are filled with the primary colour; hovered ones get a
        /// lighter state layer and lift a little; selected ones turn to surface white and lift.
        /// Returns the height of the top face.
        /// </summary>
        public float Panel(Rect rect, bool selected, bool hovered, bool enabled = true)
        {
            var lifted = selected || (hovered && enabled);
            var y = lifted ? Raise : 0.02f;
            var center = new Vector3(rect.center.x, 0f, rect.center.y);
            if (lifted) Shapes.Box(center + new Vector3(ShadowOffset, 0f, -ShadowOffset), rect.width, Raise, rect.height, MenuStyle.PrimaryShadow, MenuStyle.PrimaryShadow);

            var fill = !enabled ? MenuStyle.PrimaryShadow : selected ? MenuStyle.Surface : hovered ? MenuStyle.PrimaryHover : MenuStyle.Primary;
            Shapes.Box(center + Vector3.up * y, rect.width, Thickness, rect.height, MenuStyle.PrimaryShadow, fill);
            var top = y + Thickness;
            var edge = selected ? MenuStyle.Primary : MenuStyle.OnPrimary;
            Shapes.Box(new Vector3(center.x, top, rect.yMax - Rim * 0.5f), rect.width, 0.02f, Rim, edge, edge);
            Shapes.Box(new Vector3(center.x, top, rect.yMin + Rim * 0.5f), rect.width, 0.02f, Rim, edge, edge);
            Shapes.Box(new Vector3(rect.xMin + Rim * 0.5f, top, center.z), Rim, 0.02f, rect.height, edge, edge);
            Shapes.Box(new Vector3(rect.xMax - Rim * 0.5f, top, center.z), Rim, 0.02f, rect.height, edge, edge);
            return top + 0.02f;
        }

        /// <summary>A button with a label on the left and its keyboard shortcut on the right.</summary>
        public void Button(Rect rect, string label, string shortcut, bool selected, Action onClick, bool enabled = true)
        {
            var hovered = IsHovered(rect);
            var top = Panel(rect, selected, hovered, enabled);
            var ink = !enabled ? MenuStyle.OnPrimaryMuted : selected ? MenuStyle.OnSurface : MenuStyle.OnPrimary;
            var muted = selected ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted;
            Label(label, rect.xMin + 0.7f, rect.center.y, BodySize, ink, weight: FontWeight.SemiBold, maxWidth: rect.width - 1.4f - (shortcut == null ? 0f : 2f), y: top);
            if (shortcut != null) Label(shortcut, rect.xMax - 0.7f, rect.center.y, LabelSize, muted, TextAlign.Right, y: top);
            if (enabled) OnClick(rect, onClick);
        }

        /// <summary>A Material switch with its label after it. Clicking either toggles it.</summary>
        public void Switch(float x, float z, string label, bool on, Action toggle)
        {
            const float trackWidth = 2f, trackHeight = 1f;
            var labelWidth = FlatText.Width(label, LabelSize) + 0.5f;
            var hit = new Rect(x, z - trackHeight * 0.5f, trackWidth + labelWidth, trackHeight);
            var hovered = IsHovered(hit);

            var track = new Vector3(x + trackWidth * 0.5f, 0.02f, z);
            Shapes.Box(track, trackWidth, 0.06f, trackHeight, MenuStyle.PrimaryShadow, on ? MenuStyle.Primary : MenuStyle.PrimaryShadow);
            Shapes.Box(track + Vector3.up * 0.06f + new Vector3(0f, 0f, trackHeight * 0.5f - 0.05f), trackWidth, 0.02f, 0.1f, MenuStyle.OnPrimary, MenuStyle.OnPrimary);
            Shapes.Box(track + Vector3.up * 0.06f - new Vector3(0f, 0f, trackHeight * 0.5f - 0.05f), trackWidth, 0.02f, 0.1f, MenuStyle.OnPrimary, MenuStyle.OnPrimary);
            var thumbX = on ? x + trackWidth - trackHeight * 0.5f : x + trackHeight * 0.5f;
            var thumb = hovered ? 0.74f : 0.62f;
            Shapes.Box(new Vector3(thumbX, 0.1f, z), thumb, 0.08f, thumb, MenuStyle.PrimaryShadow, on ? MenuStyle.OnPrimary : MenuStyle.OnPrimaryMuted);

            Label(label, x + trackWidth + 0.5f, z, LabelSize, MenuStyle.OnBackground);
            OnClick(hit, toggle);
        }

        /// <summary>A picture lying in <paramref name="rect"/>, showing the <paramref name="uv"/> part of the texture.</summary>
        public void Image(Texture texture, Rect rect, Rect uv, float y) => Images.Add((texture, rect, uv, y + 0.01f));
    }
}

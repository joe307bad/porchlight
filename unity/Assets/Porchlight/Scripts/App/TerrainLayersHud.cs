using System.Text;
using Porchlight.Builder;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The terrain builder's layers: a list down the right-hand side with a row per placed shape,
    /// newest at the top. Clicking a row selects that shape and turns the camera to it; Copy and
    /// Delete do what they say. Longer lists page.
    /// </summary>
    public sealed class TerrainLayersHud : IHudContent
    {
        private const float Margin = 1.2f;
        private const float RowHeight = 2.2f;
        private const float RowGap = 0.3f;
        private const float SmallWidth = 3.6f;

        private readonly BuilderTool _builder;
        private int _page;

        public TerrainLayersHud(BuilderTool builder)
        {
            _builder = builder;
        }

        public string StateKey
        {
            get
            {
                var key = new StringBuilder();
                key.Append(_page).Append('|').Append(_builder.SelectedShapeId).Append('|');
                foreach (var shape in _builder.Shapes) key.Append(shape.Id).Append(shape.Name).Append(shape.Overlay).Append(',');
                return key.ToString();
            }
        }

        public void Draw(HudCanvas c)
        {
            var x = c.Left + Margin;
            var width = c.Right - c.Left - Margin * 2f;
            c.Header("Layers", x, c.Top - 1.6f);

            var shapes = _builder.Shapes;
            if (shapes.Count == 0)
            {
                c.Label("No shapes yet.", x, c.Top - 3.6f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: width);
                c.Label("Pick a triangle, sphere or cube below, then click to place it.", x, c.Top - 5f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: width);
                return;
            }

            c.Label("Click a layer to focus it", x, c.Top - 3.4f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: width);

            // Rows fill the panel; a pager takes the bottom row when they don't all fit.
            var top = c.Top - 4.6f;
            var fit = Mathf.Max(1, Mathf.FloorToInt((top - c.Bottom - Margin + RowGap) / (RowHeight + RowGap)));
            var pages = 1;
            if (shapes.Count > fit)
            {
                fit = Mathf.Max(1, fit - 1);
                pages = (shapes.Count + fit - 1) / fit;
            }

            _page = Mathf.Clamp(_page, 0, pages - 1);
            var nameWidth = width - 2f * (SmallWidth + RowGap);
            for (var row = 0; row < fit; row++)
            {
                // Newest first, like layers stacked on top of each other.
                var index = shapes.Count - 1 - (_page * fit + row);
                if (index < 0) break;
                var shape = shapes[index];
                var id = shape.Id;
                var rowTop = top - row * (RowHeight + RowGap);
                var name = string.IsNullOrEmpty(shape.Name) ? TerrainShapes.DisplayName(shape.Kind) : shape.Name;
                // "Ground" marks the shapes the overlay will shape the ground to.
                c.Button(new Rect(x, rowTop - RowHeight, nameWidth, RowHeight), name, shape.Overlay ? "Ground" : null, id == _builder.SelectedShapeId, () => _builder.SelectShape(id, true));
                c.Button(new Rect(x + nameWidth + RowGap, rowTop - RowHeight, SmallWidth, RowHeight), "Copy", null, false, () => _builder.DuplicateShape(id));
                c.Button(new Rect(x + nameWidth + SmallWidth + RowGap * 2f, rowTop - RowHeight, SmallWidth, RowHeight), "Del", null, false, () => _builder.DeleteShape(id));
            }

            if (pages <= 1) return;
            var pagerZ = c.Bottom + Margin;
            var half = (width - RowGap) * 0.5f;
            c.Button(new Rect(x, pagerZ, half, RowHeight), "Newer", null, false, () => _page--, enabled: _page > 0);
            c.Button(new Rect(x + half + RowGap, pagerZ, half, RowHeight), "Older", $"{_page + 1}/{pages}", false, () => _page++, enabled: _page < pages - 1);
        }
    }
}

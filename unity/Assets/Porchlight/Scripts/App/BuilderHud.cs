using System;
using Porchlight.Builder;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The builder's bottom bar: the town's numbers, the tool, a card for each building type or surface,
    /// placement or terrain options, and the save status / Walk / Back. Edits save themselves. Keyboard
    /// shortcuts still work (1-4, Q/E, Tab).
    /// </summary>
    public sealed class BuilderHud : IHudContent
    {
        // Rows, in bar units from the middle: headers, the main row of controls, and a caption row.
        private const float HeaderZ = 4.6f;
        private const float ContentTop = 3.5f;
        private const float ContentBottom = -3.6f;
        private const float CaptionZ = -4.8f;
        private const float Margin = 2f;
        private const float Gap = 2.5f;

        private const float TownWidth = 17f;
        private const float ToolWidth = 12f;
        private const float ActionWidth = 15f;

        // The Simple / Builder nav sits on the header row, just after "TERRAIN".
        private const float NavOffset = 8.2f;
        private const float NavWidth = 5.4f;

        private readonly BuilderTool _builder;
        private readonly Material _material;
        private readonly Action _walk;

        public BuilderHud(BuilderTool builder, Material material, Action walk)
        {
            _builder = builder;
            _material = material;
            _walk = walk;
        }

        public string StateKey
        {
            get
            {
                var b = _builder;
                var m = b.Metrics;
                return $"{b.Town?.Name}|{b.CurrentTool}|{b.BuildingTypeId}|{b.SurfaceId}|{b.Rotation}|{b.SnapToGrid}|{b.SnapToRoads}|{b.RaiseTerrain}|{b.PadSize}|{b.Hint}|" +
                       $"{m.Population}|{m.Jobs}|{m.BuildingCount}|{m.ConnectedBuildingCount}|{m.RoadLengthMeters:0}|{b.IsDirty}|{b.Saving}|{b.SaveStatus}|{b.ConfirmingLeave}|" +
                       $"{b.NatureErase}|{b.FoliageAreaCount}|{b.Foliage.Overall}|{b.Foliage.Trees}|{b.Foliage.Flowers}|{b.Foliage.Grass}|{b.CurrentTerrainMode}|{b.PlacingKind}|{b.SelectedShapeId}|{b.OverlayOn}|{b.FreeResize}|{b.Shapes.Count}|{ShapeSummary(b.SelectedShape)}";
            }
        }

        public void Draw(HudCanvas c)
        {
            if (_builder.Town == null) return;
            var x = c.Left + Margin;
            DrawTown(c, x);
            x += TownWidth + Gap;
            DrawTools(c, x);
            x += ToolWidth + Gap;
            var actionsLeft = c.Right - Margin - ActionWidth;
            DrawPalette(c, x, actionsLeft - Gap);
            DrawActions(c, actionsLeft);
        }

        private void DrawTown(HudCanvas c, float x)
        {
            var m = _builder.Metrics;
            c.Header("Town", x, HeaderZ);
            c.Label(_builder.Town.Name, x, 2.7f, HudCanvas.TitleSize, MenuStyle.OnBackground, weight: FontWeight.SemiBold, maxWidth: TownWidth);
            c.Label($"Pop {m.Population}  ·  {m.Jobs} jobs", x, 0.9f, HudCanvas.BodySize, MenuStyle.OnBackground);
            c.Label($"{m.BuildingCount} buildings, {m.ConnectedBuildingCount} on a street or path", x, -0.7f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: TownWidth);
            c.Label($"{m.RoadLengthMeters:0} m of street and path", x, -2.3f, HudCanvas.BodySize, MenuStyle.OnBackground);
            c.Label("Drag to pan  ·  Ctrl-drag to tilt and turn  ·  Scroll to zoom", x, CaptionZ, HudCanvas.LabelSize, MenuStyle.OnBackground, maxWidth: TownWidth + Gap + ToolWidth);
        }

        private void DrawTools(HudCanvas c, float x)
        {
            c.Header("Tool", x, HeaderZ);
            var tools = new[]
            {
                (BuilderTool.Tool.Build, "Build", "1"), (BuilderTool.Tool.Paint, "Paint", "2"),
                (BuilderTool.Tool.Remove, "Remove", "3"), (BuilderTool.Tool.Terrain, "Terrain", "4"),
                (BuilderTool.Tool.Nature, "Nature", "5"),
            };
            const float gap = 0.4f;
            var height = (ContentTop - ContentBottom - gap * (tools.Length - 1)) / tools.Length;
            for (var i = 0; i < tools.Length; i++)
            {
                var (tool, label, key) = tools[i];
                var top = ContentTop - i * (height + gap);
                c.Button(new Rect(x, top - height, ToolWidth, height), label, key, _builder.CurrentTool == tool, () => _builder.SetTool(tool));
            }
        }

        private void DrawPalette(HudCanvas c, float left, float right)
        {
            switch (_builder.CurrentTool)
            {
                case BuilderTool.Tool.Build:
                    c.Header("Buildings", left, HeaderZ);
                    DrawBuildingCards(c, left, right);
                    DrawPlacementOptions(c, left);
                    break;

                case BuilderTool.Tool.Paint:
                    c.Header("Paint", left, HeaderZ);
                    var cardRight = DrawSurfaceCards(c, left);
                    var textWidth = right - cardRight - 1.5f;
                    var line = Catalog.TryGetSurface(_builder.SurfaceId, out var chosen) && (chosen.Surface == TileSurface.Street || chosen.Surface == TileSurface.Path);
                    c.Label(line ? "Drag from tile to tile to lay a line;" : "Drag out a rectangle of tiles", cardRight + 1.5f, 1.4f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: textWidth);
                    c.Label(line ? "it ramps or steps up a level at a time." : "to pave, plant or put back to grass.", cardRight + 1.5f, -0.2f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: textWidth);
                    c.Label("Esc stops the drag you're making.", cardRight + 1.5f, -1.8f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: textWidth);
                    c.Label("Streets ramp up to streets a level higher; paths climb to any paving by steps.", left, CaptionZ, HudCanvas.LabelSize, MenuStyle.OnBackground, maxWidth: right - left);
                    break;

                case BuilderTool.Tool.Nature:
                    c.Header("Nature", left, HeaderZ);
                    DrawNature(c, left, right);
                    break;

                case BuilderTool.Tool.Terrain:
                    c.Header("Terrain", left, HeaderZ);
                    DrawTerrainNav(c, left + NavOffset);
                    if (_builder.CurrentTerrainMode == BuilderTool.TerrainMode.Builder) DrawShapeBuilder(c, left, right);
                    else DrawTerrainOptions(c, left, right);
                    break;

                default:
                    c.Header("Remove", left, HeaderZ);
                    c.Label("Click a building to remove it, or paving to put it back to grass.", left, 1.2f, HudCanvas.TitleSize, MenuStyle.OnBackground, weight: FontWeight.SemiBold, maxWidth: right - left);
                    c.Label("Removing can't be undone, and it saves straight away like any other edit.", left, -0.8f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: right - left);
                    break;
            }

            // Why the spot under the pointer won't work, on the header row where nothing else competes.
            if (!string.IsNullOrEmpty(_builder.Hint)) c.Label(_builder.Hint, right, HeaderZ, HudCanvas.BodySize, MenuStyle.Warning, TextAlign.Right, FontWeight.SemiBold, (right - left) * 0.7f);
        }

        /// <summary>A card per building type: a picture, the name, and what it adds to the town.</summary>
        private void DrawBuildingCards(HudCanvas c, float left, float right)
        {
            var types = Catalog.Buildable;
            const float gap = 0.5f;
            var width = Mathf.Min(8f, (right - left - gap * (types.Count - 1)) / types.Count);
            var height = ContentTop - ContentBottom;
            for (var i = 0; i < types.Count; i++)
            {
                var type = types[i];
                var rect = new Rect(left + i * (width + gap), ContentBottom, width, height);
                var selected = _builder.BuildingTypeId == type.Id;
                var top = c.Panel(rect, selected, c.IsHovered(rect));

                var picture = new Rect(rect.xMin + 0.35f, rect.yMax - 0.35f - (width - 0.7f) * Thumbnails.Height / Thumbnails.Width, width - 0.7f, (width - 0.7f) * Thumbnails.Height / Thumbnails.Width);
                c.Image(Thumbnails.Building(type, _material), picture, new Rect(0f, 0f, 1f, 1f), top);

                var ink = selected ? MenuStyle.OnSurface : MenuStyle.OnPrimary;
                var muted = selected ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted;
                c.Label(type.DisplayName, rect.xMin + 0.45f, picture.yMin - 0.9f, 0.82f, ink, weight: FontWeight.SemiBold, maxWidth: width - 0.9f, y: top);
                c.Label(Describe(type), rect.xMin + 0.45f, picture.yMin - 2.1f, 0.66f, muted, maxWidth: width - 0.9f, y: top);
                c.OnClick(rect, () =>
                {
                    _builder.BuildingTypeId = type.Id;
                    _builder.SetTool(BuilderTool.Tool.Build);
                });
            }
        }

        private void DrawPlacementOptions(HudCanvas c, float x)
        {
            c.Label($"Rotation {_builder.Rotation:0}°  ·  Q / E", x, CaptionZ, HudCanvas.LabelSize, MenuStyle.OnBackground);
            var buttonsX = x + 10.5f;
            c.Button(new Rect(buttonsX, CaptionZ - 0.6f, 3.4f, 1.2f), $"−{BuilderTool.RotationStep:0}°", null, false, () => _builder.Rotate(-BuilderTool.RotationStep));
            c.Button(new Rect(buttonsX + 3.8f, CaptionZ - 0.6f, 3.4f, 1.2f), $"+{BuilderTool.RotationStep:0}°", null, false, () => _builder.Rotate(BuilderTool.RotationStep));
            var switches = buttonsX + 9.2f;
            // Both off is free-form placement.
            c.Switch(switches, CaptionZ, "Snap to streets", _builder.SnapToRoads, () => _builder.SnapToRoads = !_builder.SnapToRoads);
            c.Switch(switches + 12f, CaptionZ, "Snap to grid", _builder.SnapToGrid, () => _builder.SnapToGrid = !_builder.SnapToGrid);
        }

        /// <summary>A card per surface the Paint tool lays, each with a little patch of it. Returns where the cards end.</summary>
        private float DrawSurfaceCards(HudCanvas c, float left)
        {
            const float width = 7.6f, gap = 0.5f;
            var height = ContentTop - ContentBottom;
            for (var i = 0; i < Catalog.Surfaces.Count; i++)
            {
                var type = Catalog.Surfaces[i];
                var rect = new Rect(left + i * (width + gap), ContentBottom, width, height);
                var selected = _builder.SurfaceId == type.Id;
                var top = c.Panel(rect, selected, c.IsHovered(rect));
                Swatch(c, type, new Rect(rect.xMin + 0.4f, rect.yMax - 3.6f, width - 0.8f, 3.2f), top);

                var ink = selected ? MenuStyle.OnSurface : MenuStyle.OnPrimary;
                var muted = selected ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted;
                c.Label(type.DisplayName, rect.xMin + 0.5f, rect.yMin + 2.4f, HudCanvas.BodySize, ink, weight: FontWeight.SemiBold, y: top);
                c.Label(type.Blurb, rect.xMin + 0.5f, rect.yMin + 1f, 0.62f, muted, maxWidth: width - 0.8f, y: top);
                c.OnClick(rect, () => _builder.SurfaceId = type.Id);
            }

            return left + Catalog.Surfaces.Count * (width + gap) - gap;
        }

        /// <summary>A small patch of a surface, laid out like it is in town.</summary>
        private static void Swatch(HudCanvas c, SurfaceType type, Rect r, float top)
        {
            var baseColor = LowPolyMeshes.Hex(type.Color);
            var middle = new Vector3(r.center.x, top, r.center.y);
            switch (type.Surface)
            {
                case TileSurface.Street:
                {
                    // Tarmac between two pale kerbs.
                    var kerb = LowPolyMeshes.Hex("#D8D1C4");
                    c.Shapes.Box(middle, r.width, 0.03f, r.height, MenuStyle.PrimaryShadow, baseColor);
                    c.Shapes.Box(new Vector3(r.center.x, top + 0.03f, r.yMax - 0.3f), r.width, 0.05f, 0.45f, kerb, kerb);
                    c.Shapes.Box(new Vector3(r.center.x, top + 0.03f, r.yMin + 0.3f), r.width, 0.05f, 0.45f, kerb, kerb);
                    break;
                }

                case TileSurface.Path:
                {
                    // Cobbles in staggered rows on grass.
                    var lawn = LowPolyMeshes.Hex("#A7CF7D");
                    var joint = LowPolyMeshes.Hex("#A69579");
                    c.Shapes.Box(middle, r.width, 0.02f, r.height, MenuStyle.PrimaryShadow, lawn);
                    c.Shapes.Box(middle + Vector3.up * 0.02f, r.width, 0.02f, r.height * 0.6f, joint, joint);
                    for (var row = 0; row < 3; row++)
                    {
                        var z = r.center.y - r.height * 0.2f + row * r.height * 0.2f;
                        for (var x = r.xMin + 0.15f + (row % 2) * 0.25f; x < r.xMax - 0.5f; x += 0.5f)
                        {
                            var shade = Color.Lerp(baseColor, Color.black, ((int)(x * 10f) + row * 7) % 3 * 0.04f);
                            c.Shapes.Box(new Vector3(x + 0.22f, top + 0.04f, z), 0.42f, 0.02f, r.height * 0.17f, shade, shade);
                        }
                    }

                    break;
                }

                case TileSurface.Plaza:
                {
                    // Flagstones on a mortar bed.
                    var joint = LowPolyMeshes.Hex("#BDAE91");
                    c.Shapes.Box(middle, r.width, 0.02f, r.height, MenuStyle.PrimaryShadow, joint);
                    const int across = 5, down = 2;
                    var w = r.width / across;
                    var h = r.height / down;
                    for (var a = 0; a < across; a++)
                    {
                        for (var b = 0; b < down; b++)
                        {
                            var shade = Color.Lerp(baseColor, Color.black, (a * 3 + b * 5) % 4 * 0.025f);
                            c.Shapes.Box(new Vector3(r.xMin + (a + 0.5f) * w, top + 0.02f, r.yMin + (b + 0.5f) * h), w - 0.12f, 0.02f, h - 0.12f, shade, shade);
                        }
                    }

                    break;
                }

                case TileSurface.Garden:
                {
                    // A bed of soil with flowers, framed in clipped hedge.
                    var hedge = LowPolyMeshes.Hex("#5E8C4A");
                    c.Shapes.Box(middle, r.width, 0.05f, r.height, MenuStyle.PrimaryShadow, hedge);
                    c.Shapes.Box(middle + Vector3.up * 0.05f, r.width - 0.7f, 0.01f, r.height - 0.7f, baseColor, baseColor);
                    var petals = new[] { "#F4B6C2", "#FFE08A", "#C9A7E8", "#F7F1E3", "#F29E7A" };
                    for (var k = 0; k < 9; k++)
                    {
                        var petal = LowPolyMeshes.Hex(petals[k % petals.Length]);
                        var x = r.xMin + 0.7f + (k * 0.73f) % (r.width - 1.4f);
                        var z = r.yMin + 0.7f + (k * 1.37f) % (r.height - 1.4f);
                        c.Shapes.Box(new Vector3(x, top + 0.07f, z), 0.32f, 0.02f, 0.32f, petal, petal);
                    }

                    break;
                }

                default:
                {
                    // Lawn in two greens.
                    var light = LowPolyMeshes.Hex("#ACD383");
                    c.Shapes.Box(middle, r.width, 0.02f, r.height, MenuStyle.PrimaryShadow, baseColor);
                    c.Shapes.Box(new Vector3(r.xMin + r.width * 0.35f, top + 0.02f, r.center.y + 0.3f), r.width * 0.45f, 0.01f, r.height * 0.5f, light, light);
                    break;
                }
            }
        }

        /// <summary>Raise and Lower cards, the pad sizes, and how the tool works.</summary>
        private void DrawTerrainOptions(HudCanvas c, float left, float right)
        {
            const float width = 8f, gap = 0.5f;
            var height = ContentTop - ContentBottom;
            var modes = new[] { (true, "Raise", "Up a level (1 m) a click"), (false, "Lower", "Down a level (1 m) a click") };
            for (var i = 0; i < modes.Length; i++)
            {
                var (raise, name, detail) = modes[i];
                var rect = new Rect(left + i * (width + gap), ContentBottom, width, height);
                var selected = _builder.RaiseTerrain == raise;
                var top = c.Panel(rect, selected, c.IsHovered(rect));

                // A pad seen from above, like contour lines: lighter in the middle for a hill, darker for a pit.
                var shades = raise ? new[] { "#6E8F5B", "#8DB274", "#B5D39A" } : new[] { "#93B97A", "#6E8F5B", "#4E6E40" };
                var middle = new Vector3(rect.center.x, top, rect.yMax - 2.1f);
                for (var k = 0; k < shades.Length; k++)
                {
                    var side = 3.2f - k * 1f;
                    var shade = LowPolyMeshes.Hex(shades[k]);
                    c.Shapes.Box(middle + Vector3.up * (k * 0.02f), side * 1.6f, 0.02f, side, shade, shade);
                }

                var ink = selected ? MenuStyle.OnSurface : MenuStyle.OnPrimary;
                var muted = selected ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted;
                c.Label(name, rect.xMin + 0.6f, rect.yMin + 2.4f, HudCanvas.BodySize, ink, weight: FontWeight.SemiBold, y: top);
                c.Label(detail, rect.xMin + 0.6f, rect.yMin + 1f, HudCanvas.LabelSize, muted, y: top);
                c.OnClick(rect, () => _builder.RaiseTerrain = raise);
            }

            // Pad sizes, stacked like the tools.
            var padX = left + modes.Length * (width + gap) + 1f;
            const float padWidth = 9f, padGap = 0.4f;
            var sizes = BuilderTool.PadSizes;
            var padHeight = (height - padGap * (sizes.Length - 1)) / sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                var index = i;
                var rect = new Rect(padX, ContentTop - i * (padHeight + padGap) - padHeight, padWidth, padHeight);
                c.Button(rect, sizes[i].Name, $"{sizes[i].HalfSize * 2f:0} m", _builder.PadSize == i, () => _builder.PadSize = index);
            }

            var textX = padX + padWidth + 1.5f;
            var textWidth = right - textX;
            var block = _builder.PadSize == BuilderTool.BlockPad;
            c.Label(block ? "Click a tile to raise or lower it." : "Click to raise or lower the tiles there.", textX, 1.4f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: textWidth);
            c.Label("Stone walls hold up the higher side;", textX, -0.2f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: textWidth);
            c.Label("streets ramp and paths step a level.", textX, -1.8f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: textWidth);
        }

        /// <summary>Sliders for how heavy the foliage is, and what grows where.</summary>
        private void DrawNature(HudCanvas c, float left, float right)
        {
            var f = _builder.Foliage;
            var width = Mathf.Min(34f, (right - left) * 0.6f);
            FoliageDto With(float overall, float trees, float flowers, float grass) => new FoliageDto { Overall = overall, Trees = trees, Flowers = flowers, Grass = grass };
            c.Slider(left, 2.6f, width, "Overall", f.Overall, FoliageDto.Max, v => _builder.SetFoliage(With(v, f.Trees, f.Flowers, f.Grass)));
            c.Slider(left, 0.9f, width, "Trees", f.Trees, FoliageDto.Max, v => _builder.SetFoliage(With(f.Overall, v, f.Flowers, f.Grass)));
            c.Slider(left, -0.8f, width, "Flowers", f.Flowers, FoliageDto.Max, v => _builder.SetFoliage(With(f.Overall, f.Trees, v, f.Grass)));
            c.Slider(left, -2.5f, width, "Grass", f.Grass, FoliageDto.Max, v => _builder.SetFoliage(With(f.Overall, f.Trees, f.Flowers, v)));

            // Painting or erasing areas, and clearing them all.
            var textX = left + width + 2f;
            var textWidth = right - textX;
            const float buttonWidth = 6f, buttonHeight = 1.6f;
            c.Button(new Rect(textX, 2.6f - buttonHeight * 0.5f, buttonWidth, buttonHeight), "Paint", null, !_builder.NatureErase, () => _builder.NatureErase = false);
            c.Button(new Rect(textX + buttonWidth + 0.4f, 2.6f - buttonHeight * 0.5f, buttonWidth, buttonHeight), "Erase", null, _builder.NatureErase, () => _builder.NatureErase = true);
            c.Button(new Rect(textX + (buttonWidth + 0.4f) * 2f, 2.6f - buttonHeight * 0.5f, buttonWidth + 1f, buttonHeight), "Clear all", null, false, _builder.ClearFoliageAreas, enabled: _builder.FoliageAreaCount > 0);

            c.Label(_builder.NatureErase ? "Drag on the ground to clear plants from an area." : "Drag on the ground to mark where plants grow.", textX, 0.6f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: textWidth);
            c.Label("Flower beds round houses and in gardens, verges by paving and walls.", textX, -1f, HudCanvas.LabelSize, MenuStyle.OnBackground, maxWidth: textWidth);
            c.Label(_builder.FoliageAreaCount == 0 ? "Nothing grows until you paint an area.  ·  Middle-drag pans." : $"{_builder.FoliageAreaCount} areas  ·  Middle-drag pans.", textX, -2.6f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: textWidth);
        }

        /// <summary>Simple (raise and lower pads) or Builder (shapes and the overlay), beside the Terrain header.</summary>
        private void DrawTerrainNav(HudCanvas c, float x)
        {
            var modes = new[] { (BuilderTool.TerrainMode.Simple, "Simple"), (BuilderTool.TerrainMode.Builder, "Builder") };
            for (var i = 0; i < modes.Length; i++)
            {
                var (mode, label) = modes[i];
                var rect = new Rect(x + i * (NavWidth + 0.3f), HeaderZ - 0.6f, NavWidth, 1.2f);
                c.Button(rect, label, null, _builder.CurrentTerrainMode == mode, () => _builder.SetTerrainMode(mode));
            }
        }

        /// <summary>A card per shape to place, the overlay and resize switches, and the selected shape and keys.</summary>
        private void DrawShapeBuilder(HudCanvas c, float left, float right)
        {
            const float width = 8f, gap = 0.5f;
            var height = ContentTop - ContentBottom;
            var kinds = TerrainShapes.Kinds;
            for (var i = 0; i < kinds.Length; i++)
            {
                var kind = kinds[i];
                var rect = new Rect(left + i * (width + gap), ContentBottom, width, height);
                var selected = _builder.PlacingKind == kind;
                var top = c.Panel(rect, selected, c.IsHovered(rect));

                // The shape itself, in its colour, standing on the card and turned a little to show its sides.
                var middle = new Vector3(rect.center.x, top + 1.2f, rect.yMax - 2.3f);
                var size = kind == TerrainShapes.Prism ? new Vector3(3f, 1.5f, 2.4f) : Vector3.one * 2.4f;
                ShapeMeshes.Add(c.Shapes, kind, ShapeMeshes.CardColors[kind], Matrix4x4.TRS(middle, Quaternion.Euler(-35f, 25f, 0f), size));

                var ink = selected ? MenuStyle.OnSurface : MenuStyle.OnPrimary;
                var muted = selected ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted;
                c.Label(TerrainShapes.DisplayName(kind), rect.xMin + 0.6f, rect.yMin + 2.4f, HudCanvas.BodySize, ink, weight: FontWeight.SemiBold, y: top);
                c.Label(selected ? "Click to place" : "Pick, then place", rect.xMin + 0.6f, rect.yMin + 1f, HudCanvas.LabelSize, muted, y: top);
                c.OnClick(rect, () => _builder.StartPlacing(kind));
            }

            var x = left + kinds.Length * (width + gap) + 1f;
            var textWidth = right - x;
            // The overlay belongs to the selected shape.
            if (_builder.SelectedShape != null) c.Switch(x, 2.6f, $"Overlay terrain on {_builder.SelectedShape.Name}", _builder.OverlayOn, () => _builder.SetOverlay(!_builder.OverlayOn));
            else c.Label("Select a shape to overlay terrain on it", x, 2.6f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: textWidth);
            c.Switch(x, 0.9f, "Free-form resize", _builder.FreeResize, () => _builder.FreeResize = !_builder.FreeResize);

            var shape = _builder.SelectedShape;
            string line;
            if (_builder.PlacingKind != null) line = $"Placing a {TerrainShapes.DisplayName(_builder.PlacingKind).ToLowerInvariant()}. Scroll lifts it, click drops it.";
            else if (shape != null) line = shape.Overlay
                ? $"{shape.Name}: the ground takes its shape when you leave Builder."
                : $"{shape.Name}: {shape.ScaleX:0.#} × {shape.ScaleY:0.#} × {shape.ScaleZ:0.#} m, {shape.Y:0.#} m up. Drag it to move, corners to resize.";
            else line = "Click a shape to select it, or drag it to move it.";
            c.Label(line, x, -0.9f, HudCanvas.LabelSize, MenuStyle.OnBackground, maxWidth: textWidth);
            c.Label("Arrows move  ·  Scroll up / down  ·  Q / E turn  ·  R / F tilt  ·  Del removes  ·  Esc lets go", x, -2.4f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: textWidth);
        }

        private static string ShapeSummary(TerrainShapeDto shape) =>
            shape == null ? "" : $"{shape.Name}:{shape.ScaleX:0.#}:{shape.ScaleY:0.#}:{shape.ScaleZ:0.#}:{shape.Y:0.#}:{shape.Overlay}";

        private void DrawActions(HudCanvas c, float x)
        {
            c.Header("Menu", x, HeaderZ);
            const float height = 2.1f, gap = 0.4f;
            // Edits save themselves; this shows where that's got to, and saves straight away if pressed.
            var canSave = _builder.IsDirty && !_builder.Saving;
            var saveLabel = _builder.Saving ? "Saving…" : _builder.IsDirty ? "Save now" : "Saved";
            c.Button(new Rect(x, ContentTop - height, ActionWidth, height), saveLabel, null, false, _builder.Save, enabled: canSave);
            c.Button(new Rect(x, ContentTop - height * 2f - gap, ActionWidth, height), "Walk around", "Tab", false, _walk);
            c.Button(new Rect(x, ContentTop - height * 3f - gap * 2f, ActionWidth, height),
                _builder.ConfirmingLeave ? "Leave without saving" : "Back to towns", null, _builder.ConfirmingLeave, _builder.Leave);

            var status = _builder.ConfirmingLeave ? "Your latest changes aren't saved." : _builder.SaveStatus;
            if (!string.IsNullOrEmpty(status)) c.Label(status, x, CaptionZ, HudCanvas.LabelSize, _builder.ConfirmingLeave ? MenuStyle.Warning : MenuStyle.OnBackground, maxWidth: ActionWidth);
        }

        private static string Describe(BuildingType type)
        {
            if (type.Population > 0) return $"{type.Population} residents";
            if (type.Jobs > 0) return $"{type.Jobs} jobs";
            return "Green space";
        }
    }
}

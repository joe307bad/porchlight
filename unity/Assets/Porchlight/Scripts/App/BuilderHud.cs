using System;
using Porchlight.Builder;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The builder's bottom bar: the town's numbers, the tool, a card for each building or road type,
    /// placement options, and Save / Walk / Back. Keyboard shortcuts still work (1-3, Q/E, Tab).
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
                return $"{b.Town?.Name}|{b.CurrentTool}|{b.BuildingTypeId}|{b.RoadTypeId}|{b.Rotation}|{b.AlignToRoads}|{b.SnapToGrid}|{b.Hint}|" +
                       $"{m.Population}|{m.Jobs}|{m.BuildingCount}|{m.ConnectedBuildingCount}|{m.RoadLengthMeters:0}|{b.IsDirty}|{b.Saving}|{b.SaveStatus}|{b.ConfirmingLeave}";
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
            c.Label($"{m.BuildingCount} buildings, {m.ConnectedBuildingCount} on a road", x, -0.7f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: TownWidth);
            c.Label($"{m.RoadLengthMeters:0} m of road", x, -2.3f, HudCanvas.BodySize, MenuStyle.OnBackground);
            c.Label("Drag to pan  ·  Ctrl-drag to tilt and turn  ·  Scroll to zoom", x, CaptionZ, HudCanvas.LabelSize, MenuStyle.OnBackground, maxWidth: TownWidth + Gap + ToolWidth);
        }

        private void DrawTools(HudCanvas c, float x)
        {
            c.Header("Tool", x, HeaderZ);
            var tools = new[] { (BuilderTool.Tool.Build, "Build", "1"), (BuilderTool.Tool.Road, "Road", "2"), (BuilderTool.Tool.Remove, "Remove", "3") };
            var height = (ContentTop - ContentBottom - 0.8f) / tools.Length;
            for (var i = 0; i < tools.Length; i++)
            {
                var (tool, label, key) = tools[i];
                var top = ContentTop - i * (height + 0.4f);
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

                case BuilderTool.Tool.Road:
                    c.Header("Roads", left, HeaderZ);
                    var cardRight = DrawRoadCards(c, left);
                    c.Label("Click to start a road, then click again", cardRight + 1.5f, 1.4f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: right - cardRight - 1.5f);
                    c.Label("for each stretch. Ends snap to junctions.", cardRight + 1.5f, -0.2f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: right - cardRight - 1.5f);
                    c.Label("Esc stops the road you're drawing.", cardRight + 1.5f, -1.8f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: right - cardRight - 1.5f);
                    break;

                default:
                    c.Header("Remove", left, HeaderZ);
                    c.Label("Click a building or road to remove it.", left, 1.2f, HudCanvas.TitleSize, MenuStyle.OnBackground, weight: FontWeight.SemiBold, maxWidth: right - left);
                    c.Label("Removing can't be undone, but nothing is saved until you press Save.", left, -0.8f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: right - left);
                    break;
            }

            // Why the spot under the pointer won't work, on the header row where nothing else competes.
            if (!string.IsNullOrEmpty(_builder.Hint)) c.Label(_builder.Hint, right, HeaderZ, HudCanvas.BodySize, MenuStyle.Warning, TextAlign.Right, FontWeight.SemiBold, (right - left) * 0.7f);
        }

        /// <summary>A card per building type: a picture, the name, and what it adds to the town.</summary>
        private void DrawBuildingCards(HudCanvas c, float left, float right)
        {
            var types = Catalog.Buildings;
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
            c.Switch(switches, CaptionZ, "Snap beside roads", _builder.AlignToRoads, () => _builder.AlignToRoads = !_builder.AlignToRoads);
            c.Switch(switches + 13.5f, CaptionZ, "Snap to grid", _builder.SnapToGrid, () => _builder.SnapToGrid = !_builder.SnapToGrid);
        }

        /// <summary>A card per road type, showing a stretch of that road. Returns where the cards end.</summary>
        private float DrawRoadCards(HudCanvas c, float left)
        {
            const float width = 11f, gap = 0.5f;
            var height = ContentTop - ContentBottom;
            for (var i = 0; i < Catalog.Roads.Count; i++)
            {
                var type = Catalog.Roads[i];
                var rect = new Rect(left + i * (width + gap), ContentBottom, width, height);
                var selected = _builder.RoadTypeId == type.Id;
                var top = c.Panel(rect, selected, c.IsHovered(rect));

                // A stretch of road with a dashed centre line, as wide as the real thing relative to the others.
                var roadWidth = Mathf.Lerp(1.2f, 2.4f, Mathf.InverseLerp(6f, 10f, type.Width));
                var roadZ = rect.yMax - 2f;
                c.Shapes.Box(new Vector3(rect.center.x, top, roadZ), width - 0.7f, 0.04f, roadWidth, MenuStyle.PrimaryShadow, LowPolyMeshes.Hex(type.Color));
                for (var dash = rect.xMin + 0.9f; dash < rect.xMax - 1f; dash += 1.4f)
                {
                    c.Shapes.Box(new Vector3(dash + 0.35f, top + 0.04f, roadZ), 0.7f, 0.02f, 0.14f, MenuStyle.Warning, MenuStyle.Warning);
                }

                var ink = selected ? MenuStyle.OnSurface : MenuStyle.OnPrimary;
                var muted = selected ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted;
                c.Label(type.DisplayName, rect.xMin + 0.6f, rect.yMin + 2.4f, HudCanvas.BodySize, ink, weight: FontWeight.SemiBold, y: top);
                c.Label($"{type.Width:0} m wide", rect.xMin + 0.6f, rect.yMin + 1f, HudCanvas.LabelSize, muted, y: top);
                c.OnClick(rect, () => _builder.RoadTypeId = type.Id);
            }

            return left + Catalog.Roads.Count * (width + gap) - gap;
        }

        private void DrawActions(HudCanvas c, float x)
        {
            c.Header("Menu", x, HeaderZ);
            const float height = 2.1f, gap = 0.4f;
            var saveLabel = _builder.Saving ? "Saving…" : "Save";
            c.Button(new Rect(x, ContentTop - height, ActionWidth, height), saveLabel, null, _builder.IsDirty && !_builder.Saving, _builder.Save, enabled: !_builder.Saving);
            c.Button(new Rect(x, ContentTop - height * 2f - gap, ActionWidth, height), "Walk around", "Tab", false, _walk);
            c.Button(new Rect(x, ContentTop - height * 3f - gap * 2f, ActionWidth, height),
                _builder.ConfirmingLeave ? "Leave without saving" : "Back to towns", null, _builder.ConfirmingLeave, _builder.Leave);

            var status = _builder.ConfirmingLeave ? "You have unsaved changes." : _builder.SaveStatus;
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

using System;
using System.Collections.Generic;
using Porchlight.Builder;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The bottom bar while editing inside a cottage: which floor, the tools, a card for each door,
    /// window and piece of furniture, and the way back out. Keys still work (F, 1-4, Q/E, Esc).
    /// </summary>
    public sealed class InteriorHud : IHudContent
    {
        private const float HeaderZ = 4.6f;
        private const float ContentTop = 3.5f;
        private const float ContentBottom = -3.6f;
        private const float CaptionZ = -4.8f;
        private const float Margin = 2f;
        private const float Gap = 2f;

        private const float FloorWidth = 11f;
        private const float ToolWidth = 9f;
        private const float ActionWidth = 13f;

        private readonly InteriorEditor _editor;
        private readonly BuilderTool _builder;
        private readonly Material _material;
        private readonly Action _walk;

        public InteriorHud(InteriorEditor editor, BuilderTool builder, Material material, Action walk)
        {
            _editor = editor;
            _builder = builder;
            _material = material;
            _walk = walk;
        }

        public string StateKey
        {
            get
            {
                var e = _editor;
                return $"{e.CurrentTool}|{e.Floor}|{e.ItemTypeId}|{e.Rotation}|{e.DrawingWall}|{e.Hint}|{_builder.IsDirty}|{_builder.Saving}|{_builder.SaveStatus}";
            }
        }

        public void Draw(HudCanvas c)
        {
            if (_editor.Building == null) return;
            var x = c.Left + Margin;
            DrawFloors(c, x);
            x += FloorWidth + Gap;
            DrawTools(c, x);
            x += ToolWidth + Gap;
            var actionsLeft = c.Right - Margin - ActionWidth;
            DrawPalette(c, x, actionsLeft - Gap);
            DrawActions(c, actionsLeft);
        }

        private void DrawFloors(HudCanvas c, float x)
        {
            c.Header(_editor.Building != null && _editor.Building.HouseNumber > 0 ? $"Cottage {_editor.Building.HouseNumber}" : "Cottage", x, HeaderZ);
            const float height = 3.2f, gap = 0.5f;
            var floors = new[] { "Upstairs", "Ground floor" };
            for (var i = 0; i < floors.Length; i++)
            {
                var floor = floors.Length - 1 - i;
                c.Button(new Rect(x, ContentTop - height - i * (height + gap), FloorWidth, height), floors[i], "F", _editor.Floor == floor, () => _editor.SetFloor(floor));
            }

            c.Label("Drag to turn  ·  Scroll to zoom", x, CaptionZ, HudCanvas.LabelSize, MenuStyle.OnBackground, maxWidth: FloorWidth + Gap + ToolWidth);
        }

        private void DrawTools(HudCanvas c, float x)
        {
            c.Header("Tool", x, HeaderZ);
            var tools = new[]
            {
                (InteriorEditor.Tool.Walls, "Walls", "1"), (InteriorEditor.Tool.Place, "Place", "2"),
                (InteriorEditor.Tool.Stairs, "Stairs", "3"), (InteriorEditor.Tool.Remove, "Remove", "4"),
            };
            const float gap = 0.4f;
            var height = (ContentTop - ContentBottom - gap * (tools.Length - 1)) / tools.Length;
            for (var i = 0; i < tools.Length; i++)
            {
                var (tool, label, key) = tools[i];
                var top = ContentTop - i * (height + gap);
                c.Button(new Rect(x, top - height, ToolWidth, height), label, key, _editor.CurrentTool == tool, () => _editor.SetTool(tool));
            }
        }

        private void DrawPalette(HudCanvas c, float left, float right)
        {
            var width = right - left;
            switch (_editor.CurrentTool)
            {
                case InteriorEditor.Tool.Walls:
                    c.Header("Walls", left, HeaderZ);
                    c.Label("Click to start a wall, then click where it ends.", left, 1.6f, HudCanvas.TitleSize, MenuStyle.OnBackground, weight: FontWeight.SemiBold, maxWidth: width);
                    c.Label("Walls run along or across the house and snap to wall ends, to other walls and to the outside", left, -0.2f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: width);
                    c.Label("walls, on a 25 cm grid. Keep clicking to carry on; Esc stops.", left, -1.6f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: width);
                    break;

                case InteriorEditor.Tool.Stairs:
                    c.Header("Stairs", left, HeaderZ);
                    c.Label("Click to move the stairs.  Q / E turns them.", left, 1.6f, HudCanvas.TitleSize, MenuStyle.OnBackground, weight: FontWeight.SemiBold, maxWidth: width);
                    c.Label("They line up with walls and furniture, and need room to step on downstairs", left, -0.2f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: width);
                    c.Label("and off upstairs (the pale patches at each end).", left, -1.6f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: width);
                    DrawRotation(c, left);
                    break;

                case InteriorEditor.Tool.Remove:
                    c.Header("Remove", left, HeaderZ);
                    c.Label("Click a wall, door, window or piece of furniture to take it out.", left, 1.2f, HudCanvas.TitleSize, MenuStyle.OnBackground, weight: FontWeight.SemiBold, maxWidth: width);
                    c.Label("A wall takes its doors and windows with it, and a dresser its TV. The front door stays.", left, -0.8f, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: width);
                    break;

                default:
                    c.Header("Place", left, HeaderZ);
                    DrawCards(c, left, right);
                    DrawRotation(c, left);
                    break;
            }

            if (!string.IsNullOrEmpty(_editor.Hint)) c.Label(_editor.Hint, right, HeaderZ, HudCanvas.BodySize, MenuStyle.Warning, TextAlign.Right, FontWeight.SemiBold, width * 0.65f);
        }

        /// <summary>A card for each door, window and piece of furniture.</summary>
        private void DrawCards(HudCanvas c, float left, float right)
        {
            var items = new List<(string Id, string Name, string Note, Texture Picture)>();
            foreach (var opening in Furnishings.Openings) items.Add((opening.Id, opening.DisplayName, "In a wall", Thumbnails.Opening(opening, _material)));
            foreach (var furniture in Furnishings.Furniture) items.Add((furniture.Id, furniture.DisplayName, Describe(furniture), Thumbnails.Furniture(furniture, _material)));

            const float gap = 0.35f;
            var width = (right - left - gap * (items.Count - 1)) / items.Count;
            var height = ContentTop - ContentBottom;
            for (var i = 0; i < items.Count; i++)
            {
                var (id, name, note, picture) = items[i];
                var rect = new Rect(left + i * (width + gap), ContentBottom, width, height);
                var selected = _editor.CurrentTool == InteriorEditor.Tool.Place && _editor.ItemTypeId == id;
                var top = c.Panel(rect, selected, c.IsHovered(rect));

                var pictureHeight = (width - 0.5f) * Thumbnails.Height / Thumbnails.Width;
                var image = new Rect(rect.xMin + 0.25f, rect.yMax - 0.25f - pictureHeight, width - 0.5f, pictureHeight);
                c.Image(picture, image, new Rect(0f, 0f, 1f, 1f), top);

                var ink = selected ? MenuStyle.OnSurface : MenuStyle.OnPrimary;
                var muted = selected ? MenuStyle.OnSurfaceMuted : MenuStyle.OnPrimaryMuted;
                c.Label(name, rect.xMin + 0.3f, image.yMin - 0.85f, 0.7f, ink, weight: FontWeight.SemiBold, maxWidth: width - 0.5f, y: top);
                c.Label(note, rect.xMin + 0.3f, image.yMin - 1.95f, 0.58f, muted, maxWidth: width - 0.5f, y: top);
                c.OnClick(rect, () => _editor.SelectItem(id));
            }
        }

        private void DrawRotation(HudCanvas c, float x)
        {
            c.Label($"Turn {_editor.Rotation:0}°  ·  Q / E  (things near a wall turn to face out from it)", x, CaptionZ, HudCanvas.LabelSize, MenuStyle.OnBackground);
        }

        private void DrawActions(HudCanvas c, float x)
        {
            c.Header("Menu", x, HeaderZ);
            const float height = 2.1f, gap = 0.4f;
            c.Button(new Rect(x, ContentTop - height, ActionWidth, height), "Done", "Esc", false, _editor.Done);
            c.Button(new Rect(x, ContentTop - height * 2f - gap, ActionWidth, height), "Walk around", "Tab", false, _walk);
            var saveLabel = _builder.Saving ? "Saving…" : _builder.IsDirty ? "Save now" : "Saved";
            c.Button(new Rect(x, ContentTop - height * 3f - gap * 2f, ActionWidth, height), saveLabel, null, false, _builder.Save, enabled: _builder.IsDirty && !_builder.Saving);
            if (!string.IsNullOrEmpty(_builder.SaveStatus)) c.Label(_builder.SaveStatus, x, CaptionZ, HudCanvas.LabelSize, MenuStyle.OnBackground, maxWidth: ActionWidth);
        }

        private static string Describe(FurnitureType type)
        {
            switch (type.Placement)
            {
                case FurniturePlacement.AgainstWall: return "Against a wall";
                case FurniturePlacement.DresserOrWall: return "Dresser or wall";
            }

            switch (type.Action)
            {
                case FurnitureAction.Sit: return "Walk in to sit";
                case FurnitureAction.LieDown: return "Walk in to lie down";
                default: return type.SnapsToWalls ? "Snaps to walls" : "Anywhere";
            }
        }
    }
}

using System;
using Porchlight.Avatars;
using Porchlight.Cameras;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using Porchlight.Shared.Metrics;
using Porchlight.Shared.Model;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The bottom bar while walking around a town: the town, who you're playing as, the controls,
    /// and the way out.
    /// </summary>
    public sealed class WalkHud : IHudContent
    {
        private const float HeaderZ = 4.6f;
        private const float ContentTop = 3.5f;
        private const float ContentBottom = -3.6f;
        private const float CaptionZ = -4.8f;
        private const float Margin = 2f;
        private const float Gap = 2.5f;

        private const float TownWidth = 20f;
        private const float PlayerWidth = 22f;
        private const float ActionWidth = 15f;

        private static readonly (string Action, string Keys)[] Controls =
        {
            ("Move", "Arrows  ·  Left stick"),
            ("Run", "Shift  ·  RT"),
            ("Jump", "Space  ·  A"),
            ("Camera", "WASD  ·  Right mouse / stick"),
            ("Zoom", "Scroll wheel"),
            ("Sit / lie", "Walk into a couch, chair or bed"),
        };

        private readonly TownDto _town;
        private readonly TownMetrics _metrics;
        private readonly CharacterType _character;
        private readonly Material _material;
        private readonly bool _building;
        private readonly Action _leave;

        /// <param name="building">Walking around from the builder: the way out goes back to building.</param>
        public WalkHud(TownDto town, CharacterType character, Material material, bool building, Action leave)
        {
            _town = town;
            _metrics = TownMetrics.Compute(town);
            _character = character;
            _material = material;
            _building = building;
            _leave = leave;
        }

        public string StateKey => $"{_town.Name}|{_character.Id}|{FlyCamera.InvertY}";

        public void Draw(HudCanvas c)
        {
            var x = c.Left + Margin;

            c.Header("Town", x, HeaderZ);
            c.Label(_town.Name, x, 2.7f, HudCanvas.TitleSize, MenuStyle.OnBackground, weight: FontWeight.SemiBold, maxWidth: TownWidth);
            c.Label($"Pop {_metrics.Population}  ·  {_metrics.Jobs} jobs", x, 0.9f, HudCanvas.BodySize, MenuStyle.OnBackground);
            c.Label($"{_metrics.BuildingCount} buildings", x, -0.7f, HudCanvas.BodySize, MenuStyle.OnBackground);
            c.Label($"{_metrics.RoadLengthMeters:0} m of road", x, -2.3f, HudCanvas.BodySize, MenuStyle.OnBackground);
            x += TownWidth + Gap;

            DrawPlayer(c, x);
            x += PlayerWidth + Gap;

            var actionsLeft = c.Right - Margin - ActionWidth;
            DrawControls(c, x, actionsLeft - Gap);

            c.Header("Menu", actionsLeft, HeaderZ);
            var label = _building ? "Back to building" : "Leave town";
            var keys = _building ? "Tab" : "Esc  ·  +";
            c.Button(new Rect(actionsLeft, ContentTop - 2.1f, ActionWidth, 2.1f), label, keys, false, _leave);
        }

        /// <summary>A card with the character's portrait, name and tagline.</summary>
        private void DrawPlayer(HudCanvas c, float x)
        {
            c.Header("Playing as", x, HeaderZ);
            var card = new Rect(x, ContentBottom, PlayerWidth, ContentTop - ContentBottom);
            var top = c.Panel(card, false, false);

            // A square from the middle of the portrait.
            var side = card.height - 0.7f;
            var square = (float)Portraits.Height / Portraits.Width;
            c.Image(Portraits.Get(_character, _material), new Rect(card.xMin + 0.35f, card.yMin + 0.35f, side, side),
                new Rect((1f - square) * 0.5f, 0f, square, 1f), top);

            var textX = card.xMin + side + 1.1f;
            var textWidth = card.xMax - textX - 0.5f;
            c.Label(_character.DisplayName, textX, card.yMax - 2f, HudCanvas.TitleSize, MenuStyle.OnPrimary, weight: FontWeight.SemiBold, maxWidth: textWidth, y: top);
            c.Label(_character.Tagline, textX, card.yMax - 3.6f, HudCanvas.LabelSize, MenuStyle.OnPrimaryMuted, maxWidth: textWidth, y: top);
        }

        /// <summary>The controls as action / keys pairs in two columns, with the camera option below.</summary>
        private static void DrawControls(HudCanvas c, float left, float right)
        {
            c.Header("Controls", left, HeaderZ);
            var columnWidth = (right - left) * 0.5f;
            for (var i = 0; i < Controls.Length; i++)
            {
                var (action, keys) = Controls[i];
                var x = left + (i % 2) * columnWidth;
                var z = 2.6f - (i / 2) * 2.2f;
                c.Label(action, x, z, HudCanvas.BodySize, MenuStyle.OnBackground, weight: FontWeight.SemiBold);
                c.Label(keys, x + 5f, z, HudCanvas.BodySize, MenuStyle.OnBackground, maxWidth: columnWidth - 5.5f);
            }

            c.Switch(left, CaptionZ, "Invert camera up / down", FlyCamera.InvertY, () => FlyCamera.InvertY = !FlyCamera.InvertY);
        }
    }
}

// What the 3D view shows over the town: the minimap in the bottom-left corner
// (the town the same way up as the blueprint, the camera's view outlined, the
// player, auto-walk's route and flag, other players in blue; click to jump
// there), the street panel top right (the street you're on and, while
// auto-walking, the turn-by-turn directions), a one-line prompt bottom centre,
// and a notice about the connection top centre. Beside the minimap, two boxes:
// your character and your car. Clicking either opens a grid of every
// character or car to pick from (a car picked is brought round). Start (or M)
// opens the big map over the view: the whole town, a reticle the stick moves
// and A drops a waypoint at (or click anywhere), and the way there dotted in blue.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.App;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Porchlight.UI
{
    public sealed class ExploreOverlay : IExploreOverlay
    {
        const float SIZE = 264;
        const int MAX_STEPS = 8;

        public readonly VisualElement root;
        readonly VisualElement minimap;
        readonly VisualElement minimapOverlay;
        readonly VisualElement hud;
        readonly Label hudName;
        readonly VisualElement hudSteps;
        readonly Label prompt;
        readonly Label notice;
        readonly VisualElement garage, personBox, carBox;
        VisualElement chooser;
        /// <summary>The character box or the car box was clicked.</summary>
        public Action OnPersonBox, OnCarBox;
        Texture2D baseTexture;
        TownModel model;
        List<Vec2> others = new List<Vec2>();
        Vec2? player;
        Vec2[] view = new Vec2[0];
        Vec2? goal;
        List<Vec2> route = new List<Vec2>();
        string shownHud = null;
        Action<Vec2> onPick;
        /// <summary>Device pixels per CSS pixel: the minimap's lines and marks are sized in device pixels.</summary>
        public float Dpr = 1;

        public Action<Vec2> OnMinimapPick
        {
            set => onPick = value;
        }

        VisualElement bigMap, bigMapPicture, bigMapMarks;
        Label bigMapHint;
        Vec2 reticle;
        Vec2? waypoint;
        Action<Vec2> onWaypoint;

        public Action<Vec2> OnWaypointPick
        {
            set => onWaypoint = value;
        }

        public bool MapOpen => bigMap != null && bigMap.style.display == DisplayStyle.Flex;

        public Vec2 Reticle => reticle;

        public void SetWaypoint(Vec2? at)
        {
            waypoint = at;
            minimapOverlay.MarkDirtyRepaint();
            bigMapMarks?.MarkDirtyRepaint();
        }

        public void OpenMap()
        {
            if (model == null) return;
            CloseChooser();
            if (bigMap == null) BuildBigMap();
            reticle = waypoint ?? player ?? new Vec2(model.width / 2.0, model.height / 2.0);
            bigMapPicture.style.backgroundImage = new StyleBackground(baseTexture);
            bigMap.style.display = DisplayStyle.Flex;
            bigMapHint.text = Gamepad.current != null
                ? "Left stick moves the cross · A sets a waypoint · Y clears it · X back to the town map · B or Start closes"
                : "Click to set a waypoint · M or Esc closes";
            FitBigMap();
        }

        public void CloseMap()
        {
            if (bigMap != null) bigMap.style.display = DisplayStyle.None;
        }

        public void MoveReticle(Vec2 by)
        {
            if (model == null) return;
            reticle = new Vec2(Math.Min(model.width, Math.Max(0, reticle.x + by.x)), Math.Min(model.height, Math.Max(0, reticle.y + by.y)));
            bigMapMarks?.MarkDirtyRepaint();
        }

        void BuildBigMap()
        {
            bigMap = new VisualElement();
            bigMap.AddToClassList("overlay");
            bigMap.AddToClassList("bigmap-backdrop");
            bigMap.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == bigMap) CloseMap();
                e.StopPropagation();
            });
            var frame = new VisualElement();
            frame.AddToClassList("bigmap");
            bigMapPicture = new VisualElement();
            bigMapPicture.AddToClassList("bigmap-picture");
            bigMapMarks = new VisualElement { pickingMode = PickingMode.Ignore };
            bigMapMarks.style.position = Position.Absolute;
            bigMapMarks.style.left = bigMapMarks.style.top = bigMapMarks.style.right = bigMapMarks.style.bottom = 0;
            bigMapMarks.generateVisualContent += DrawBigMap;
            bigMapPicture.Add(bigMapMarks);
            bigMapPicture.RegisterCallback<PointerDownEvent>(e =>
            {
                if (model == null) return;
                var r = bigMapPicture.contentRect;
                reticle = new Vec2(e.localPosition.x / r.width * model.width, e.localPosition.y / r.height * model.height);
                onWaypoint?.Invoke(reticle);
                bigMapMarks.MarkDirtyRepaint();
                e.StopPropagation();
            });
            frame.Add(bigMapPicture);
            bigMapHint = new Label();
            bigMapHint.AddToClassList("bigmap-hint");
            frame.Add(bigMapHint);
            bigMap.Add(frame);
            bigMap.style.display = DisplayStyle.None;
            root.Add(bigMap);
            root.RegisterCallback<GeometryChangedEvent>(_ => FitBigMap());
        }

        /// <summary>As big as the view allows, keeping the town's shape.</summary>
        void FitBigMap()
        {
            if (bigMapPicture == null || model == null) return;
            float w = root.layout.width, h = root.layout.height;
            if (float.IsNaN(w) || float.IsNaN(h)) return;
            float k = Math.Max(1, Math.Min((w - 64) / model.width, (h - 120) / model.height));
            bigMapPicture.style.width = model.width * k;
            bigMapPicture.style.height = model.height * k;
        }

        void DrawBigMap(MeshGenerationContext ctx)
        {
            if (model == null) return;
            var rect = bigMapMarks.contentRect;
            float k = rect.width / model.width;
            Vector2 P(Vec2 q) => new Vector2((float)q.x * k, (float)q.y * k);
            var painter = ctx.painter2D;
            // The way there, in blue dots.
            if (route != null && route.Count > 1)
            {
                painter.fillColor = Coords.Hex(0x3b9be0);
                foreach (var q in Geo.SamplePolyline(route, Math.Max(0.4, 7 / k)))
                {
                    painter.BeginPath();
                    painter.Arc(P(q), 2.2f, 0, 360);
                    painter.Fill();
                }
            }
            if (waypoint != null) Waypoint(painter, P(waypoint.Value), 1);
            foreach (var o in others) Dot(painter, P(o), 5, Coords.Hex(0x3b82c4));
            if (player != null) Dot(painter, P(player.Value), 6, Coords.Hex(0xc2412d));
            // The reticle: a ring with a cross through it.
            var c = P(reticle);
            painter.strokeColor = new Color(1, 1, 1, 0.95f);
            painter.lineWidth = 2;
            painter.BeginPath();
            painter.Arc(c, 10, 0, 360);
            painter.Stroke();
            painter.BeginPath();
            painter.MoveTo(c + new Vector2(-16, 0));
            painter.LineTo(c + new Vector2(-5, 0));
            painter.MoveTo(c + new Vector2(5, 0));
            painter.LineTo(c + new Vector2(16, 0));
            painter.MoveTo(c + new Vector2(0, -16));
            painter.LineTo(c + new Vector2(0, -5));
            painter.MoveTo(c + new Vector2(0, 5));
            painter.LineTo(c + new Vector2(0, 16));
            painter.Stroke();
        }

        static void Dot(Painter2D painter, Vector2 at, float r, Color fill)
        {
            painter.BeginPath();
            painter.Arc(at, r, 0, 360);
            painter.fillColor = fill;
            painter.Fill();
            painter.lineWidth = 2;
            painter.strokeColor = Coords.Hex(0xfff8e8);
            painter.Stroke();
        }

        /// <summary>A waypoint: a blue pin standing on its spot, `s` times its size on the big map.</summary>
        static void Waypoint(Painter2D painter, Vector2 at, float s)
        {
            var top = at + new Vector2(0, -16 * s);
            painter.strokeColor = Coords.Hex(0xfff8e8);
            painter.lineWidth = 2 * s;
            painter.BeginPath();
            painter.MoveTo(at);
            painter.LineTo(top);
            painter.Stroke();
            painter.BeginPath();
            painter.Arc(top, 5 * s, 0, 360);
            painter.fillColor = Coords.Hex(0x3b9be0);
            painter.Fill();
            painter.Stroke();
        }

        public ExploreOverlay(VisualElement host)
        {
            root = host;
            minimap = new VisualElement();
            minimap.AddToClassList("minimap");
            minimap.tooltip = "Click to jump there";
            minimapOverlay = new VisualElement();
            minimapOverlay.style.position = Position.Absolute;
            minimapOverlay.style.left = minimapOverlay.style.top = minimapOverlay.style.right = minimapOverlay.style.bottom = 0;
            minimapOverlay.pickingMode = PickingMode.Ignore;
            minimapOverlay.generateVisualContent += DrawMinimapOverlay;
            minimap.Add(minimapOverlay);
            minimap.RegisterCallback<PointerDownEvent>(e =>
            {
                if (model == null) return;
                var r = minimap.contentRect;
                var local = e.localPosition;
                onPick?.Invoke(new Vec2(local.x / r.width * model.width, local.y / r.height * model.height));
                e.StopPropagation();
            });
            host.Add(minimap);

            garage = new VisualElement();
            garage.AddToClassList("garage");
            personBox = Box("You", "Your character: click to choose someone else", () => OnPersonBox?.Invoke());
            carBox = Box("Car", "Your car: click to choose one, and have it brought right beside you", () => OnCarBox?.Invoke());
            garage.Add(personBox);
            garage.Add(carBox);
            host.Add(garage);

            hud = new VisualElement();
            hud.AddToClassList("street-hud");
            hud.pickingMode = PickingMode.Ignore;
            var nameRow = new VisualElement();
            nameRow.AddToClassList("street-hud-name");
            var sign = new VisualElement();
            sign.AddToClassList("street-hud-sign");
            nameRow.Add(sign);
            hudName = new Label();
            hudName.AddToClassList("bold");
            nameRow.Add(hudName);
            hud.Add(nameRow);
            hudSteps = new VisualElement();
            hudSteps.AddToClassList("street-hud-steps");
            hud.Add(hudSteps);
            hud.style.display = DisplayStyle.None;
            host.Add(hud);

            prompt = new Label();
            prompt.AddToClassList("hud-prompt");
            prompt.AddToClassList("semibold");
            prompt.pickingMode = PickingMode.Ignore;
            prompt.style.display = DisplayStyle.None;
            host.Add(prompt);
            notice = new Label();
            notice.AddToClassList("net-notice");
            notice.pickingMode = PickingMode.Ignore;
            notice.style.display = DisplayStyle.None;
            host.Add(notice);
            host.RegisterCallback<GeometryChangedEvent>(_ => Center());
            prompt.RegisterCallback<GeometryChangedEvent>(_ => Center());
            notice.RegisterCallback<GeometryChangedEvent>(_ => Center());
        }

        static VisualElement Box(string label, string tooltip, Action click)
        {
            var box = new VisualElement();
            box.AddToClassList("garage-box");
            box.tooltip = tooltip;
            var name = new Label(label);
            name.AddToClassList("garage-label");
            name.pickingMode = PickingMode.Ignore;
            box.Add(name);
            box.RegisterCallback<PointerDownEvent>(e =>
            {
                click();
                e.StopPropagation();
            });
            return box;
        }

        /// <summary>The boxes' pictures.</summary>
        public void SetGarage(Texture person, Texture car)
        {
            personBox.style.backgroundImage = Background.FromRenderTexture(person as RenderTexture);
            carBox.style.backgroundImage = Background.FromRenderTexture(car as RenderTexture);
        }

        public bool Choosing => chooser != null;

        /// <summary>What the open grid is choosing (as Choose was told), or null with none open.</summary>
        public string ChooserKind => chooser != null ? chooserKind : null;

        string chooserKind;
        List<VisualElement> chooserTiles;
        ScrollView chooserScroll;
        int highlighted = -1;
        Action<int> chooserPick;

        /// <summary>
        /// A grid of choices over the view, each a picture and a name, the current one
        /// marked: clicking one closes the grid and calls pick with its index; clicking
        /// outside, the close button or Escape (CloseChooser) closes it without.
        /// </summary>
        public void Choose(string title, IReadOnlyList<Texture> pictures, IReadOnlyList<string> names, int current, Action<int> pick, string kind = null)
        {
            CloseChooser();
            chooserKind = kind;
            chooserTiles = new List<VisualElement>();
            chooserPick = pick;
            highlighted = current >= 0 && current < pictures.Count ? current : 0;
            chooser = new VisualElement();
            chooser.AddToClassList("overlay");
            chooser.AddToClassList("chooser");
            chooser.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == chooser) CloseChooser();
                e.StopPropagation();
            });
            var card = El.Box("card", "chooser-card");
            var head = El.Box("chooser-head");
            head.Add(El.Text(title, "chooser-title", "bold"));
            head.Add(El.Btn("×", "Close (Esc)", CloseChooser, "chooser-close"));
            card.Add(head);
            var grid = new ScrollView();
            chooserScroll = grid;
            grid.AddToClassList("chooser-scroll");
            grid.contentContainer.AddToClassList("chooser-grid");
            for (int i = 0; i < pictures.Count; i++)
            {
                int at = i;
                var tile = El.Box("chooser-tile");
                if (i == current) tile.AddToClassList("current");
                tile.style.backgroundImage = pictures[i] is RenderTexture rt ? Background.FromRenderTexture(rt) : Background.FromTexture2D(pictures[i] as Texture2D);
                var label = El.Text(names[i], "garage-label");
                label.pickingMode = PickingMode.Ignore;
                tile.Add(label);
                tile.RegisterCallback<PointerDownEvent>(e =>
                {
                    e.StopPropagation();
                    CloseChooser();
                    pick(at);
                });
                grid.Add(tile);
                chooserTiles.Add(tile);
            }
            card.Add(grid);
            chooser.Add(card);
            root.Add(chooser);
        }

        public void CloseChooser()
        {
            chooser?.RemoveFromHierarchy();
            chooser = null;
            chooserTiles = null;
            chooserPick = null;
        }

        /// <summary>The controller's cursor in the grid: shown (from the current one) at 0, else moved on this many, round the end.</summary>
        public void Highlight(int step)
        {
            if (chooserTiles == null || chooserTiles.Count == 0) return;
            int n = chooserTiles.Count;
            highlighted = ((highlighted + step) % n + n) % n;
            for (int i = 0; i < n; i++) chooserTiles[i].EnableInClassList("highlighted", i == highlighted);
            var tile = chooserTiles[highlighted];
            // Once it's laid out (the first time, the grid has only just been added).
            chooserScroll.schedule.Execute(() => chooserScroll.ScrollTo(tile));
        }

        /// <summary>Pick the one under the controller's cursor, as a click on it would.</summary>
        public void PickHighlighted()
        {
            if (chooserPick == null || highlighted < 0) return;
            var pick = chooserPick;
            int at = highlighted;
            CloseChooser();
            pick(at);
        }

        /// <summary>Centre the prompt and notice (left 50%, translate -50%).</summary>
        void Center()
        {
            float w = root.layout.width;
            if (float.IsNaN(w)) return;
            prompt.style.left = (w - prompt.layout.width) / 2;
            notice.style.maxWidth = Math.Max(0, w - 32);
            notice.style.left = (w - notice.layout.width) / 2;
        }

        public void SetTown(TownModel m)
        {
            model = m;
            int w = m.width, h = m.height;
            var tiles = m.tiles;
            if (baseTexture != null) UnityEngine.Object.Destroy(baseTexture);
            baseTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int i = 0; i < w * h; i++)
            {
                var rgb = BlueprintStyle.TileTerrain(tiles.terrain[i]);
                var over = BlueprintStyle.TileOverlay(tiles.overlay[i]);
                if (over != null) rgb = over.Value;
                if (tiles.buildingId[i] >= 0) rgb = BlueprintStyle.TileBuilding;
                // Texture rows go bottom up; the map's go top down.
                int x = i % w, y = i / w;
                px[(h - 1 - y) * w + x] = rgb;
            }
            baseTexture.SetPixels32(px);
            baseTexture.Apply(false);
            minimap.style.backgroundImage = new StyleBackground(baseTexture);
            float scale = SIZE / Math.Max(w, h);
            minimap.style.width = w * scale;
            minimap.style.height = h * scale;
            Fit();
        }

        /// <summary>max-width: min(264px, 38vw), keeping the aspect.</summary>
        public void Fit()
        {
            if (model == null) return;
            float vw = root.panel?.visualTree.layout.width ?? float.NaN;
            float scale = SIZE / Math.Max(model.width, model.height);
            float width = model.width * scale;
            if (!float.IsNaN(vw)) width = Math.Min(width, Math.Min(264, vw * 0.38f));
            minimap.style.width = width;
            minimap.style.height = width * model.height / model.width;
            garage.style.left = 14 + width + 8;
        }

        public void SetOthers(List<Vec2> o) => others = o ?? new List<Vec2>();

        public void DrawMinimap(Vec2? p, Vec2[] v, Vec2? g, List<Vec2> r)
        {
            player = p;
            view = v;
            goal = g;
            route = r;
            minimapOverlay.MarkDirtyRepaint();
            if (MapOpen) bigMapMarks.MarkDirtyRepaint();
        }

        void DrawMinimapOverlay(MeshGenerationContext ctx)
        {
            if (model == null) return;
            var rect = minimapOverlay.contentRect;
            float k = rect.width / model.width; // CSS px per tile
            float dev = 1 / Dpr; // one device pixel, in CSS px
            Vector2 P(Vec2 q) => new Vector2((float)q.x * k, (float)q.y * k);
            var painter = ctx.painter2D;
            if (view.Length == 4)
            {
                painter.BeginPath();
                painter.MoveTo(P(view[0]));
                for (int i = 1; i < 4; i++) painter.LineTo(P(view[i]));
                painter.ClosePath();
                painter.fillColor = new Color(1, 1, 1, 0.18f);
                painter.Fill();
                painter.strokeColor = new Color(1, 1, 1, 0.9f);
                painter.lineWidth = 1.5f * dev;
                painter.Stroke();
            }
            if (route != null && route.Count > 1)
            {
                painter.BeginPath();
                painter.MoveTo(P(route[0]));
                for (int i = 1; i < route.Count; i++) painter.LineTo(P(route[i]));
                painter.strokeColor = Coords.Hex(0xff6a00);
                painter.lineWidth = 1.5f * dev;
                painter.lineJoin = LineJoin.Miter;
                painter.lineCap = LineCap.Butt;
                painter.Stroke();
            }
            if (goal != null)
            {
                var gp = P(goal.Value);
                painter.strokeColor = Coords.Hex(0xfff8e8);
                painter.lineWidth = 1.5f * dev;
                painter.BeginPath();
                painter.MoveTo(gp);
                painter.LineTo(gp + new Vector2(0, -9 * dev));
                painter.Stroke();
                painter.fillColor = Coords.Hex(0xff6a00);
                painter.BeginPath();
                painter.MoveTo(gp + new Vector2(0, -9 * dev));
                painter.LineTo(gp + new Vector2(6 * dev, -7 * dev));
                painter.LineTo(gp + new Vector2(0, -5 * dev));
                painter.ClosePath();
                painter.Fill();
            }
            if (waypoint != null) Waypoint(painter, P(waypoint.Value), 0.6f * dev);
            foreach (var o in others)
            {
                painter.BeginPath();
                painter.Arc(P(o), 3 * dev, 0, 360);
                painter.fillColor = Coords.Hex(0x3b82c4);
                painter.Fill();
                painter.lineWidth = 1.5f * dev;
                painter.strokeColor = Coords.Hex(0xfff8e8);
                painter.Stroke();
            }
            if (player != null)
            {
                painter.BeginPath();
                painter.Arc(P(player.Value), 4 * dev, 0, 360);
                painter.fillColor = Coords.Hex(0xc2412d);
                painter.Fill();
                painter.lineWidth = 2 * dev;
                painter.strokeColor = Coords.Hex(0xfff8e8);
                painter.Stroke();
            }
        }

        public void SetPrompt(string text)
        {
            if (prompt.text != (text ?? "")) prompt.text = text ?? "";
            prompt.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        public void Notify(string text)
        {
            flashing?.Pause();
            flashing = null;
            notice.text = text ?? "";
            notice.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        IVisualElementScheduledItem flashing;
        const long FLASH_MS = 3000;

        /// <summary>A notice that clears itself after a few seconds (unless something else is said first).</summary>
        public void Flash(string text)
        {
            Notify(text);
            flashing = notice.schedule.Execute(() =>
            {
                flashing = null;
                Notify(null);
            }).StartingIn(FLASH_MS);
        }

        public void SetHud(string streetName, List<Step> steps)
        {
            string key = $"{streetName}|{(steps == null ? "" : string.Join("|", steps.Select(s => $"{s.text}:{Directions.FormatDistance(s.distance)}")))}";
            if (key == shownHud) return;
            shownHud = key;
            bool show = streetName != null || steps != null;
            hud.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            hudName.text = streetName ?? (steps != null ? "Making your way to the street" : "");
            hudSteps.Clear();
            hudSteps.style.display = steps == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (steps == null) return;
            for (int i = 0; i < Math.Min(MAX_STEPS, steps.Count); i++)
            {
                var s = steps[i];
                var row = new VisualElement();
                row.AddToClassList("hud-step");
                if (i == 0) row.AddToClassList("current");
                var icon = new Label(Directions.Icon(s.kind));
                icon.AddToClassList("hud-step-icon");
                icon.AddToClassList("bold");
                row.Add(icon);
                var t = new Label(s.text);
                t.AddToClassList("hud-step-text");
                row.Add(t);
                string d = Directions.FormatDistance(s.distance);
                if (d.Length > 0 && s.kind != StepKind.Arrive)
                {
                    var dist = new Label(d);
                    dist.AddToClassList("hud-step-dist");
                    row.Add(dist);
                }
                hudSteps.Add(row);
            }
            if (steps.Count > MAX_STEPS)
            {
                var more = new VisualElement();
                more.AddToClassList("hud-step");
                more.AddToClassList("more");
                var t = new Label($"and {steps.Count - MAX_STEPS} more");
                t.AddToClassList("hud-step-text");
                more.Add(t);
                hudSteps.Add(more);
            }
        }
    }
}

// The blueprint: the town drawn as a map, layer by layer. Rasters (terrain shading, water, the
// tile view, debug views) are images placed and scaled under the camera;
// every vector layer is stroked and filled with Painter2D in screen pixels.
// Pan by dragging, zoom with the wheel about the cursor, double-click to fit.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace Porchlight.UI
{
    public static class BlueprintStyle
    {
        public static readonly Color Background = Coords.Hex(0x2b2a27);
        public static readonly int[] Parchment = { 239, 230, 207 };
        public static readonly int[] ParchmentShadow = { 196, 180, 146 };
        public static readonly Color Border = Coords.Hex(0x6b5d45);
        public static readonly Color ContourMajor = Coords.Rgb(120, 98, 64, 0.55f);
        public static readonly Color ContourMinor = Coords.Rgb(120, 98, 64, 0.2f);
        public static readonly int[] Water = { 156, 195, 217 };
        public static readonly int[] WaterDeep = { 112, 160, 196 };
        public static readonly Color Shore = Coords.Hex(0x5d88a8);
        public static readonly Color River = Coords.Hex(0x8fbad6);
        public static readonly Color RiverEdge = Coords.Hex(0x5d88a8);
        public static readonly Color Block = Coords.Rgb(214, 199, 160, 0.35f);
        public static readonly Color BlockEdge = Coords.Rgb(140, 118, 80, 0.45f);
        public static readonly Color LotEdge = Coords.Rgb(120, 100, 70, 0.55f);
        public static Color Road(RoadKind k) => k == RoadKind.Main ? Coords.Hex(0x3d342b) : k == RoadKind.Street ? Coords.Hex(0x6a5b4b) : Coords.Hex(0x8d7556);
        public static readonly Color Sidewalk = Coords.Hex(0xd9cfb4);
        public static readonly Color BridgeDeck = Coords.Hex(0xc49a6c);
        public static readonly Color BridgeRail = Coords.Hex(0x5b3a1e);
        public static Color Building(BuildingKind k) => k switch
        {
            BuildingKind.House => Coords.Hex(0x7a5c4a),
            BuildingKind.Shop => Coords.Hex(0xa0583c),
            BuildingKind.Workshop => Coords.Hex(0x6e6a5e),
            BuildingKind.Church => Coords.Hex(0x7d2e3e),
            _ => Coords.Hex(0x3e5a7d),
        };
        public static readonly Color BuildingEdge = Coords.Hex(0x2e241d);
        public static readonly Color Door = Coords.Hex(0xf4e9cf);
        public static readonly Color Plaza = Coords.Hex(0xe8d9b0);
        public static readonly Color PlazaEdge = Coords.Hex(0xa48b5c);
        public static readonly Color Landmark = Coords.Hex(0x2e241d);
        public static readonly Color Gate = Coords.Hex(0x3d342b);
        public static readonly Color GridColor = Coords.Rgb(90, 76, 52, 0.22f);
        public static readonly Color Spawn = Coords.Hex(0xc2412d);
        public static readonly Color SpawnRing = Coords.Hex(0xfff8e8);

        static readonly Color32[] Terrain =
        {
            new Color32(184, 204, 140, 255),
            new Color32(176, 146, 104, 255),
            new Color32(226, 210, 156, 255),
            new Color32(126, 176, 214, 255),
            new Color32(72, 120, 178, 255),
            new Color32(142, 136, 126, 255),
        };

        static readonly Color32?[] Overlays =
        {
            null,
            new Color32(70, 58, 46, 255),
            new Color32(112, 96, 80, 255),
            new Color32(150, 122, 84, 255),
            new Color32(164, 104, 52, 255),
            new Color32(222, 204, 160, 255),
        };

        public static readonly Color32 TileBuilding = new Color32(128, 64, 52, 255);

        public static Color32 TileTerrain(byte t) => t < Terrain.Length ? Terrain[t] : new Color32(255, 0, 255, 255);

        public static Color32? TileOverlay(byte o) => o < Overlays.Length ? Overlays[o] : null;
    }

    /// <summary>Screen <-> world for the blueprint: `scale` is CSS pixels per tile; (ox, oy) is where world (0, 0) lands.</summary>
    public sealed class BlueprintCamera
    {
        public double ox, oy, scale = 4;
        public const double MIN_SCALE = 0.5;
        public const double MAX_SCALE = 96;

        public Vec2 WorldToScreen(Vec2 p) => new Vec2(p.x * scale + ox, p.y * scale + oy);
        public Vec2 ScreenToWorld(Vec2 p) => new Vec2((p.x - ox) / scale, (p.y - oy) / scale);

        public void Fit(double width, double height, double viewW, double viewH, double margin = 32)
        {
            scale = Math.Max(MIN_SCALE, Math.Min((viewW - margin * 2) / width, (viewH - margin * 2) / height));
            ox = (viewW - width * scale) / 2;
            oy = (viewH - height * scale) / 2;
        }

        public void Pan(double dx, double dy)
        {
            ox += dx;
            oy += dy;
        }

        public void ZoomAt(Vec2 screen, double factor)
        {
            var before = ScreenToWorld(screen);
            scale = Math.Min(MAX_SCALE, Math.Max(MIN_SCALE, scale * factor));
            ox = screen.x - before.x * scale;
            oy = screen.y - before.y * scale;
        }
    }

    public sealed class LayerInfo
    {
        public string id;
        public string label;
        public bool defaultOn;
        public bool debug;
    }

    public sealed class BlueprintView : VisualElement
    {
        public static readonly LayerInfo[] LAYERS =
        {
            new LayerInfo { id = "terrain", label = "Terrain shading", defaultOn = true },
            new LayerInfo { id = "contours", label = "Contours", defaultOn = true },
            new LayerInfo { id = "tileView", label = "Tile view", defaultOn = false },
            new LayerInfo { id = "water", label = "Water", defaultOn = true },
            new LayerInfo { id = "rivers", label = "Rivers", defaultOn = true },
            new LayerInfo { id = "blocks", label = "Blocks", defaultOn = true },
            new LayerInfo { id = "lots", label = "Lots", defaultOn = true },
            new LayerInfo { id = "roads", label = "Roads", defaultOn = true },
            new LayerInfo { id = "bridges", label = "Bridges", defaultOn = true },
            new LayerInfo { id = "buildings", label = "Buildings", defaultOn = true },
            new LayerInfo { id = "landmarks", label = "Landmarks", defaultOn = true },
            new LayerInfo { id = "gates", label = "Center & gates", defaultOn = true },
            new LayerInfo { id = "spawn", label = "Spawn", defaultOn = true },
            new LayerInfo { id = "debugHeight", label = "Raw heightmap", defaultOn = false, debug = true },
            new LayerInfo { id = "debugFlow", label = "Flow accumulation", defaultOn = false, debug = true },
            new LayerInfo { id = "debugCost", label = "A* cost field", defaultOn = false, debug = true },
            new LayerInfo { id = "debugWalkable", label = "Walkable mask", defaultOn = false, debug = true },
            new LayerInfo { id = "tileGrid", label = "Tile grid", defaultOn = false },
        };

        public readonly BlueprintCamera camera = new BlueprintCamera();
        public readonly HashSet<string> visibleLayers = new HashSet<string>(LAYERS.Where(l => l.defaultOn).Select(l => l.id));
        public Vec2? marker;
        public List<Vec2> route = new List<Vec2>();
        public Vec2? goal;
        public TownModel model;
        public Action OnViewChanged;
        public Action<Vec2?> OnHover;
        public Action OnDoubleClick;
        public Func<double> WheelDelta;

        readonly Dictionary<string, VisualElement> layers = new Dictionary<string, VisualElement>();
        readonly VisualElement pinLayer;
        readonly Label pinLabel;
        readonly Dictionary<string, Texture2D> rasters = new Dictionary<string, Texture2D>();
        List<(Vec2 a, Vec2 b)> contourMajor, contourMinor, contourShore;
        Vec2? dragging;

        public BlueprintView()
        {
            name = "map";
            foreach (var l in LAYERS)
            {
                var e = new VisualElement { name = "layer-" + l.id, pickingMode = PickingMode.Ignore };
                e.style.position = Position.Absolute;
                e.style.left = e.style.top = e.style.right = e.style.bottom = 0;
                string id = l.id;
                if (!IsRaster(id)) e.generateVisualContent += ctx => DrawLayer(id, ctx);
                else
                {
                    var img = new VisualElement { pickingMode = PickingMode.Ignore };
                    img.style.position = Position.Absolute;
                    e.Add(img);
                    if (id == "terrain") e.generateVisualContent += ctx => DrawLayer(id, ctx);
                    if (id == "water") e.generateVisualContent += ctx => DrawLayer(id, ctx);
                }
                layers[id] = e;
                Add(e);
            }
            pinLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            pinLayer.style.position = Position.Absolute;
            pinLayer.style.left = pinLayer.style.top = pinLayer.style.right = pinLayer.style.bottom = 0;
            pinLayer.generateVisualContent += DrawOverlay;
            Add(pinLayer);
            pinLabel = new Label("You are here") { pickingMode = PickingMode.Ignore };
            pinLabel.AddToClassList("semibold");
            pinLabel.style.position = Position.Absolute;
            pinLabel.style.fontSize = 12;
            pinLabel.style.color = BlueprintStyle.SpawnRing;
            pinLabel.style.backgroundColor = Coords.Rgb(28, 27, 24, 0.85f);
            pinLabel.style.borderTopLeftRadius = pinLabel.style.borderTopRightRadius = pinLabel.style.borderBottomLeftRadius = pinLabel.style.borderBottomRightRadius = 4;
            pinLabel.style.paddingLeft = pinLabel.style.paddingRight = 6;
            pinLabel.style.height = 18;
            pinLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            pinLabel.style.display = DisplayStyle.None;
            Add(pinLabel);

            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                if (e.clickCount == 2)
                {
                    OnDoubleClick?.Invoke();
                    return;
                }
                dragging = new Vec2(e.position.x, e.position.y);
                this.CapturePointer(e.pointerId);
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                if (dragging != null)
                {
                    camera.Pan(e.position.x - dragging.Value.x, e.position.y - dragging.Value.y);
                    dragging = new Vec2(e.position.x, e.position.y);
                    OnViewChanged?.Invoke();
                }
                OnHover?.Invoke(camera.ScreenToWorld(new Vec2(e.localPosition.x, e.localPosition.y)));
            });
            RegisterCallback<PointerUpEvent>(e =>
            {
                if (dragging == null) return;
                dragging = null;
                this.ReleasePointer(e.pointerId);
            });
            RegisterCallback<PointerLeaveEvent>(_ => OnHover?.Invoke(null));
            RegisterCallback<WheelEvent>(e =>
            {
                double deltaY = WheelDelta != null ? WheelDelta() : e.delta.y * 33;
                if (deltaY == 0) deltaY = e.delta.y * 33;
                bool ctrl = e.ctrlKey;
                var local = new Vec2(e.localMousePosition.x, e.localMousePosition.y);
                camera.ZoomAt(local, Math.Exp(-deltaY * (ctrl ? 0.01 : 0.0015)));
                OnViewChanged?.Invoke();
                OnHover?.Invoke(camera.ScreenToWorld(local));
                e.StopPropagation();
            });
            RegisterCallback<GeometryChangedEvent>(_ => Redraw());
        }

        static bool IsRaster(string id) => id == "terrain" || id == "tileView" || id == "water" || id.StartsWith("debug") && id != "debugGrid";

        public float ViewW => float.IsNaN(layout.width) ? 0 : layout.width;
        public float ViewH => float.IsNaN(layout.height) ? 0 : layout.height;

        public void SetModel(TownModel m)
        {
            model = m;
            foreach (var t in rasters.Values) UnityEngine.Object.Destroy(t);
            rasters.Clear();
            contourMajor = contourMinor = contourShore = null;
            Redraw();
        }

        public void Fit()
        {
            if (model == null) return;
            camera.Fit(model.width, model.height, ViewW, ViewH);
        }

        public bool IsInView(Vec2 at, double margin)
        {
            var p = camera.WorldToScreen(at);
            double w = ViewW, h = ViewH;
            return p.x > w * margin && p.x < w * (1 - margin) && p.y > h * margin && p.y < h * (1 - margin);
        }

        public void CenterOn(Vec2 at)
        {
            const double minScale = 6;
            if (camera.scale < minScale) camera.scale = minScale;
            camera.ox = ViewW / 2 - at.x * camera.scale;
            camera.oy = ViewH / 2 - at.y * camera.scale;
        }

        /// <summary>Re-place the rasters and redraw every vector layer (the camera or the model changed).</summary>
        public void Redraw()
        {
            foreach (var l in LAYERS)
            {
                var e = layers[l.id];
                bool on = model != null && visibleLayers.Contains(l.id);
                e.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
                if (!on) continue;
                if (IsRaster(l.id))
                {
                    var img = e[0];
                    var tex = Raster(l.id);
                    img.style.backgroundImage = new StyleBackground(tex);
                    img.style.left = (float)camera.ox;
                    img.style.top = (float)camera.oy;
                    img.style.width = (float)(model.width * camera.scale);
                    img.style.height = (float)(model.height * camera.scale);
                    img.style.opacity = l.id == "debugFlow" ? 0.9f : l.id == "debugCost" ? 0.85f : 1f;
                }
                e.MarkDirtyRepaint();
            }
            pinLayer.MarkDirtyRepaint();
            if (marker != null)
            {
                var p = camera.WorldToScreen(marker.Value);
                float cy = (float)p.y - 22;
                float w = (float)(pinLabel.text.Length * 6.6 + 12);
                pinLabel.style.display = DisplayStyle.Flex;
                pinLabel.style.left = (float)p.x - w / 2;
                pinLabel.style.width = w;
                pinLabel.style.top = cy - 9 - 24;
            }
            else pinLabel.style.display = DisplayStyle.None;
        }

        // Rasters -------------------------------------------------------------------------------

        const int DETAIL = 4;
        const int MINOR_PER_STEP = 3;

        Texture2D Raster(string id)
        {
            if (rasters.TryGetValue(id, out var t)) return t;
            switch (id)
            {
                case "terrain": t = TerrainImage(); break;
                case "water": t = WaterImage(); break;
                case "tileView": t = TileImage(); break;
                default: t = DebugImage(id); break;
            }
            rasters[id] = t;
            return t;
        }

        double SampleHeight(double x, double y)
        {
            int w = model.width, h = model.height;
            var hm = model.heightmap;
            double fx = Math.Min(w - 1, Math.Max(0, x - 0.5));
            double fy = Math.Min(h - 1, Math.Max(0, y - 0.5));
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
            double tx = fx - x0, ty = fy - y0;
            double a = hm[y0 * w + x0] * (1 - tx) + hm[y0 * w + x1] * tx;
            double b = hm[y1 * w + x0] * (1 - tx) + hm[y1 * w + x1] * tx;
            return a * (1 - ty) + b * ty;
        }

        /// <summary>An image whose rows are given top first, as images are usually stored.</summary>
        static Texture2D Image(int w, int h, Color32[] topFirst, bool smooth)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++) Array.Copy(topFirst, y * w, px, (h - 1 - y) * w, w);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                filterMode = smooth ? FilterMode.Bilinear : FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        static byte U8(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v, MidpointRounding.ToEven)));

        Texture2D TerrainImage()
        {
            int W = model.width * DETAIL, H = model.height * DETAIL;
            var px = new Color32[W * H];
            var l = BlueprintStyle.Parchment;
            var d = BlueprintStyle.ParchmentShadow;
            for (int py = 0; py < H; py++)
            {
                for (int pxi = 0; pxi < W; pxi++)
                {
                    double x = (pxi + 0.5) / DETAIL, y = (py + 0.5) / DETAIL;
                    double gx = SampleHeight(x + 0.5, y) - SampleHeight(x - 0.5, y);
                    double gy = SampleHeight(x, y + 0.5) - SampleHeight(x, y - 0.5);
                    double light = Math.Max(0, Math.Min(1, 0.5 + (gx + gy) * 14));
                    double height = SampleHeight(x, y);
                    double t = Math.Max(0, Math.Min(1, light * 0.45 + (1 - height) * 0.12));
                    px[py * W + pxi] = new Color32(U8(l[0] + (d[0] - l[0]) * t), U8(l[1] + (d[1] - l[1]) * t), U8(l[2] + (d[2] - l[2]) * t), 255);
                }
            }
            return Image(W, H, px, true);
        }

        Texture2D WaterImage()
        {
            int W = model.width * DETAIL, H = model.height * DETAIL;
            var px = new Color32[W * H];
            var a = BlueprintStyle.Water;
            var b = BlueprintStyle.WaterDeep;
            for (int py = 0; py < H; py++)
            {
                for (int pxi = 0; pxi < W; pxi++)
                {
                    double v = SampleHeight((pxi + 0.5) / DETAIL, (py + 0.5) / DETAIL);
                    if (v >= model.waterLevel) continue;
                    double deep = Math.Min(1, (model.waterLevel - v) / 0.06);
                    px[py * W + pxi] = new Color32(U8(a[0] + (b[0] - a[0]) * deep), U8(a[1] + (b[1] - a[1]) * deep), U8(a[2] + (b[2] - a[2]) * deep), 255);
                }
            }
            return Image(W, H, px, true);
        }

        Texture2D TileImage()
        {
            var t = model.tiles;
            var px = new Color32[t.width * t.height];
            for (int i = 0; i < px.Length; i++)
            {
                var rgb = BlueprintStyle.TileTerrain(t.terrain[i]);
                var over = BlueprintStyle.TileOverlay(t.overlay[i]);
                if (over != null) rgb = over.Value;
                if (t.buildingId[i] >= 0) rgb = BlueprintStyle.TileBuilding;
                px[i] = rgb;
            }
            return Image(t.width, t.height, px, false);
        }

        Texture2D DebugImage(string id)
        {
            int n = model.width * model.height;
            var px = new Color32[n];
            switch (id)
            {
                case "debugHeight":
                    for (int i = 0; i < n; i++)
                    {
                        byte v = U8(Numbers.Round(model.heightmap[i] * 255));
                        px[i] = new Color32(v, v, v, 255);
                    }
                    break;
                case "debugFlow":
                {
                    double max = 1;
                    foreach (var v in model.fields.flow) max = Math.Max(max, v);
                    for (int i = 0; i < n; i++)
                    {
                        double f = model.fields.flow[i];
                        double t = Math.Log(f == 0 ? 1 : f) / Math.Log(max);
                        px[i] = new Color32(20, U8(60 + 120 * t), U8(120 + 135 * t), U8(Numbers.Round(60 + 195 * t)));
                    }
                    break;
                }
                case "debugCost":
                {
                    double max = 1;
                    foreach (var v in model.fields.roadCost) if (!float.IsInfinity(v) && !float.IsNaN(v)) max = Math.Max(max, v);
                    for (int i = 0; i < n; i++)
                    {
                        double v = model.fields.roadCost[i];
                        if (double.IsInfinity(v) || double.IsNaN(v))
                        {
                            px[i] = new Color32(90, 0, 30, 220);
                            continue;
                        }
                        double t = Math.Min(1, Math.Log(v) / Math.Log(max));
                        px[i] = new Color32(U8(255 * t), U8(220 * (1 - t) + 30), 60, 200);
                    }
                    break;
                }
                default:
                    for (int i = 0; i < n; i++) px[i] = model.tiles.walkable[i] != 0 ? new Color32(40, 170, 70, 110) : new Color32(200, 40, 40, 140);
                    break;
            }
            return Image(model.width, model.height, px, false);
        }

        void BuildContours()
        {
            int w = model.width, h = model.height;
            var hm = model.heightmap;
            double top = model.waterLevel;
            foreach (var v in hm) top = Math.Max(top, v);
            int steps = (int)model.@params.contourSteps;
            double span = (top - model.waterLevel) / steps;
            contourMajor = new List<(Vec2, Vec2)>();
            contourMinor = new List<(Vec2, Vec2)>();
            contourShore = new List<(Vec2, Vec2)>();
            var levels = new List<(double v, List<(Vec2, Vec2)> path)> { (model.waterLevel, contourShore) };
            for (int s = 0; s < steps; s++)
                for (int m = 0; m < MINOR_PER_STEP; m++)
                {
                    if (s == 0 && m == 0) continue;
                    levels.Add((model.waterLevel + span * (s + (double)m / MINOR_PER_STEP), m == 0 ? contourMajor : contourMinor));
                }
            foreach (var (v, path) in levels)
            {
                for (int y = 0; y + 1 < h; y++)
                {
                    for (int x = 0; x + 1 < w; x++)
                    {
                        double a = hm[y * w + x], b = hm[y * w + x + 1], c = hm[(y + 1) * w + x + 1], d = hm[(y + 1) * w + x];
                        int code = (a >= v ? 8 : 0) | (b >= v ? 4 : 0) | (c >= v ? 2 : 0) | (d >= v ? 1 : 0);
                        if (code == 0 || code == 15) continue;
                        double cx = x + 0.5, cy = y + 0.5;
                        double T(double p, double q) => (v - p) / (q - p);
                        var top_ = new Vec2(cx + T(a, b), cy);
                        var right = new Vec2(cx + 1, cy + T(b, c));
                        var bottom = new Vec2(cx + T(d, c), cy + 1);
                        var left = new Vec2(cx, cy + T(a, d));
                        switch (code)
                        {
                            case 1: case 14: path.Add((left, bottom)); break;
                            case 2: case 13: path.Add((bottom, right)); break;
                            case 3: case 12: path.Add((left, right)); break;
                            case 4: case 11: path.Add((top_, right)); break;
                            case 5:
                                path.Add((left, top_));
                                path.Add((bottom, right));
                                break;
                            case 6: case 9: path.Add((top_, bottom)); break;
                            case 7: case 8: path.Add((left, top_)); break;
                            case 10:
                                path.Add((left, bottom));
                                path.Add((top_, right));
                                break;
                        }
                    }
                }
            }
        }

        // Vector layers --------------------------------------------------------------------------

        Vector2 S(Vec2 p) => new Vector2((float)(p.x * camera.scale + camera.ox), (float)(p.y * camera.scale + camera.oy));
        Vector2 S(double x, double y) => new Vector2((float)(x * camera.scale + camera.ox), (float)(y * camera.scale + camera.oy));

        /// <summary>A world length in screen pixels.</summary>
        float L(double world) => (float)(world * camera.scale);

        static void Polygon(Painter2D p, IReadOnlyList<Vector2> pts)
        {
            p.MoveTo(pts[0]);
            for (int i = 1; i < pts.Count; i++) p.LineTo(pts[i]);
            p.ClosePath();
        }

        void Polyline(Painter2D p, IReadOnlyList<Vec2> pts)
        {
            p.MoveTo(S(pts[0]));
            for (int i = 1; i < pts.Count; i++) p.LineTo(S(pts[i]));
        }

        /// <summary>A dashed polyline (screen pixels), the dash pattern restarting at the line's start.</summary>
        static void Dashed(Painter2D p, IReadOnlyList<Vector2> pts, float on, float off)
        {
            bool drawing = true;
            float left = on;
            Vector2 a = pts[0];
            bool open = false;
            for (int k = 1; k < pts.Count; k++)
            {
                var b = pts[k];
                float seg = Vector2.Distance(a, b);
                var start = a;
                float done = 0;
                while (seg - done > 1e-4f)
                {
                    float step = Math.Min(left, seg - done);
                    var from = Vector2.Lerp(start, b, done / seg);
                    var to = Vector2.Lerp(start, b, (done + step) / seg);
                    if (drawing)
                    {
                        if (!open)
                        {
                            p.MoveTo(from);
                            open = true;
                        }
                        p.LineTo(to);
                    }
                    done += step;
                    left -= step;
                    if (left <= 1e-4f)
                    {
                        drawing = !drawing;
                        left = drawing ? on : off;
                        open = false;
                    }
                }
                a = b;
            }
        }

        static float Px => 1f;

        void DrawLayer(string id, MeshGenerationContext ctx)
        {
            if (model == null) return;
            var p = ctx.painter2D;
            p.lineJoin = LineJoin.Miter;
            p.lineCap = LineCap.Butt;
            switch (id)
            {
                case "terrain":
                {
                    p.strokeColor = BlueprintStyle.Border;
                    p.lineWidth = 1.5f;
                    p.BeginPath();
                    Polygon(p, new[] { S(0, 0), S(model.width, 0), S(model.width, model.height), S(0, model.height) });
                    p.Stroke();
                    break;
                }
                case "contours":
                {
                    if (contourMajor == null) BuildContours();
                    p.lineWidth = 1;
                    p.strokeColor = BlueprintStyle.ContourMinor;
                    StrokeSegments(p, contourMinor);
                    p.lineWidth = 1.2f;
                    p.strokeColor = BlueprintStyle.ContourMajor;
                    StrokeSegments(p, contourMajor);
                    break;
                }
                case "water":
                {
                    if (contourShore == null) BuildContours();
                    p.strokeColor = BlueprintStyle.Shore;
                    p.lineWidth = 1.2f;
                    StrokeSegments(p, contourShore);
                    break;
                }
                case "rivers":
                {
                    p.lineCap = LineCap.Round;
                    p.lineJoin = LineJoin.Round;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        foreach (var r in model.rivers)
                        {
                            for (int k = 0; k + 1 < r.points.Count; k++)
                            {
                                p.BeginPath();
                                p.MoveTo(S(r.points[k]));
                                p.LineTo(S(r.points[k + 1]));
                                float width = Math.Max(L(r.widthAt[k]), 3);
                                p.lineWidth = pass == 0 ? width + 2 : width;
                                p.strokeColor = pass == 0 ? BlueprintStyle.RiverEdge : BlueprintStyle.River;
                                p.Stroke();
                            }
                        }
                    }
                    break;
                }
                case "blocks":
                {
                    p.fillColor = BlueprintStyle.Block;
                    p.strokeColor = BlueprintStyle.BlockEdge;
                    p.lineWidth = 1;
                    foreach (var b in model.blocks)
                    {
                        p.BeginPath();
                        Polygon(p, b.polygon.Select(S).ToList());
                        foreach (var hole in b.holes) Polygon(p, hole.Select(S).ToList());
                        p.Fill(FillRule.OddEven);
                        p.Stroke();
                    }
                    break;
                }
                case "lots":
                {
                    p.strokeColor = BlueprintStyle.LotEdge;
                    p.lineWidth = 0.75f;
                    p.BeginPath();
                    foreach (var l in model.lots)
                    {
                        var ring = l.polygon.Select(S).ToList();
                        ring.Add(ring[0]);
                        Dashed(p, ring, 3, 2);
                    }
                    p.Stroke();
                    break;
                }
                case "roads":
                {
                    p.lineCap = LineCap.Round;
                    p.lineJoin = LineJoin.Round;
                    p.strokeColor = BlueprintStyle.Sidewalk;
                    foreach (var kind in new[] { RoadKind.Street, RoadKind.Main })
                    {
                        p.lineWidth = L(Porchlight.Gen.Raster.RoadHalfWidth(kind, model.@params) * 2 + 0.9);
                        p.BeginPath();
                        foreach (var e in model.roads.edges) if (e.kind == kind) Polyline(p, e.points);
                        p.Stroke();
                    }
                    foreach (var kind in new[] { RoadKind.Path, RoadKind.Street, RoadKind.Main })
                    {
                        p.strokeColor = BlueprintStyle.Road(kind);
                        p.lineWidth = Math.Max(L(RoadWidth(kind)), kind == RoadKind.Main ? 2 : 1);
                        p.BeginPath();
                        foreach (var e in model.roads.edges)
                        {
                            if (e.kind != kind) continue;
                            if (kind == RoadKind.Path) Dashed(p, e.points.Select(S).ToList(), L(1), L(0.6));
                            else Polyline(p, e.points);
                        }
                        p.Stroke();
                    }
                    break;
                }
                case "bridges":
                {
                    p.lineCap = LineCap.Butt;
                    p.lineJoin = LineJoin.Round;
                    foreach (var b in model.bridges)
                    {
                        var edge = model.roads.edges[b.roadEdgeId];
                        double a = Geo.ArcPosition(b.from, edge.points);
                        double c = Geo.ArcPosition(b.to, edge.points);
                        var span = Geo.SubPolyline(edge.points, Math.Min(a, c), Math.Max(a, c));
                        if (span.Count < 2) continue;
                        double width = RoadWidth(edge.kind);
                        p.BeginPath();
                        Polyline(p, span);
                        p.strokeColor = BlueprintStyle.BridgeRail;
                        p.lineWidth = L(width + 0.8) + 2;
                        p.Stroke();
                        p.strokeColor = BlueprintStyle.BridgeDeck;
                        p.lineWidth = L(width + 0.4);
                        p.Stroke();
                    }
                    break;
                }
                case "buildings":
                {
                    p.lineWidth = 1;
                    p.strokeColor = BlueprintStyle.BuildingEdge;
                    foreach (var b in model.buildings)
                    {
                        p.BeginPath();
                        Polygon(p, b.footprint.Select(S).ToList());
                        p.fillColor = BlueprintStyle.Building(b.kind);
                        p.Fill();
                        p.Stroke();
                        var f = b.footprint;
                        var (q0, q1) = b.facing switch
                        {
                            Facing.N => (f[0], f[1]),
                            Facing.E => (f[1], f[2]),
                            Facing.S => (f[2], f[3]),
                            _ => (f[3], f[0]),
                        };
                        var mid = new Vec2((q0.x + q1.x) / 2, (q0.y + q1.y) / 2);
                        const double s = 0.35;
                        p.fillColor = BlueprintStyle.Door;
                        p.BeginPath();
                        Polygon(p, new[] { S(mid.x - s / 2, mid.y - s / 2), S(mid.x + s / 2, mid.y - s / 2), S(mid.x + s / 2, mid.y + s / 2), S(mid.x - s / 2, mid.y + s / 2) });
                        p.Fill();
                    }
                    break;
                }
                case "landmarks":
                {
                    foreach (var lm in model.landmarks)
                    {
                        if (lm.kind != LandmarkKind.Plaza) continue;
                        p.BeginPath();
                        foreach (int id2 in lm.lotIds) Polygon(p, model.lots[id2].polygon.Select(S).ToList());
                        p.fillColor = BlueprintStyle.Plaza;
                        p.Fill();
                        p.strokeColor = BlueprintStyle.PlazaEdge;
                        p.lineWidth = 1;
                        p.Stroke();
                    }
                    p.strokeColor = BlueprintStyle.Landmark;
                    p.lineWidth = 1.5f;
                    double r = Math.Max(0.5, 5 / camera.scale);
                    foreach (var lm in model.landmarks)
                    {
                        if (lm.kind == LandmarkKind.Well)
                        {
                            p.BeginPath();
                            p.Arc(S(lm.at), L(r * 0.8), 0, 360);
                            p.fillColor = BlueprintStyle.River;
                            p.Fill();
                            p.strokeColor = BlueprintStyle.Landmark;
                            p.Stroke();
                        }
                        else if (lm.kind == LandmarkKind.Church)
                        {
                            p.strokeColor = BlueprintStyle.Door;
                            p.BeginPath();
                            p.MoveTo(S(lm.at.x, lm.at.y - r));
                            p.LineTo(S(lm.at.x, lm.at.y + r));
                            p.MoveTo(S(lm.at.x - r * 0.6, lm.at.y - r * 0.35));
                            p.LineTo(S(lm.at.x + r * 0.6, lm.at.y - r * 0.35));
                            p.Stroke();
                        }
                    }
                    break;
                }
                case "gates":
                {
                    double r = Math.Max(0.8, 6 / camera.scale);
                    p.fillColor = BlueprintStyle.Gate;
                    foreach (var g in model.gates)
                    {
                        double a = Math.Atan2(model.height / 2.0 - g.y, model.width / 2.0 - g.x);
                        p.BeginPath();
                        Polygon(p, new[]
                        {
                            S(g.x + Math.Cos(a) * r, g.y + Math.Sin(a) * r),
                            S(g.x + Math.Cos(a + 2.4) * r, g.y + Math.Sin(a + 2.4) * r),
                            S(g.x + Math.Cos(a - 2.4) * r, g.y + Math.Sin(a - 2.4) * r),
                        });
                        p.Fill();
                    }
                    p.strokeColor = BlueprintStyle.Gate;
                    p.lineWidth = 1.5f;
                    var circle = new List<Vector2>();
                    int n = Math.Max(48, (int)(L(model.townRadius) / 3));
                    for (int k = 0; k <= n; k++)
                    {
                        double t = (double)k / n * Math.PI * 2;
                        circle.Add(S(model.center.x + Math.Cos(t) * model.townRadius, model.center.y + Math.Sin(t) * model.townRadius));
                    }
                    p.BeginPath();
                    Dashed(p, circle, 4, 4);
                    p.Stroke();
                    break;
                }
                case "spawn":
                {
                    double r = Math.Max(0.6, 6 / camera.scale);
                    p.BeginPath();
                    p.Arc(S(model.spawn), L(r), 0, 360);
                    p.fillColor = BlueprintStyle.Spawn;
                    p.Fill();
                    p.lineWidth = 2;
                    p.strokeColor = BlueprintStyle.SpawnRing;
                    p.Stroke();
                    break;
                }
                case "tileGrid":
                {
                    if (camera.scale < 4) break;
                    var tl = camera.ScreenToWorld(new Vec2(0, 0));
                    var br = camera.ScreenToWorld(new Vec2(ViewW, ViewH));
                    int x0 = Math.Max(0, (int)Math.Floor(tl.x)), y0 = Math.Max(0, (int)Math.Floor(tl.y));
                    int x1 = Math.Min(model.width, (int)Math.Ceiling(br.x)), y1 = Math.Min(model.height, (int)Math.Ceiling(br.y));
                    p.strokeColor = BlueprintStyle.GridColor;
                    p.lineWidth = 1;
                    p.BeginPath();
                    for (int x = x0; x <= x1; x++)
                    {
                        p.MoveTo(S(x, y0));
                        p.LineTo(S(x, y1));
                    }
                    for (int y = y0; y <= y1; y++)
                    {
                        p.MoveTo(S(x0, y));
                        p.LineTo(S(x1, y));
                    }
                    p.Stroke();
                    break;
                }
            }
        }

        double RoadWidth(RoadKind kind) => kind == RoadKind.Main ? model.@params.mainRoadWidth : kind == RoadKind.Street ? model.@params.streetWidth : 0.4;

        void StrokeSegments(Painter2D p, List<(Vec2 a, Vec2 b)> segs)
        {
            float w = ViewW, h = ViewH;
            p.BeginPath();
            foreach (var (a, b) in segs)
            {
                var sa = S(a);
                var sb = S(b);
                if ((sa.x < -4 && sb.x < -4) || (sa.y < -4 && sb.y < -4) || (sa.x > w + 4 && sb.x > w + 4) || (sa.y > h + 4 && sb.y > h + 4)) continue;
                p.MoveTo(sa);
                p.LineTo(sb);
            }
            p.Stroke();
        }

        /// <summary>The route as a dashed orange line with a flag at its end, and the "you are here" pin, in screen pixels.</summary>
        void DrawOverlay(MeshGenerationContext ctx)
        {
            if (model == null) return;
            var p = ctx.painter2D;
            if (route.Count > 1)
            {
                p.strokeColor = Coords.Hex(0xff6a00);
                p.lineWidth = 3;
                p.lineCap = LineCap.Round;
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                Dashed(p, route.Select(S).ToList(), 7, 6);
                p.Stroke();
                if (goal != null)
                {
                    var g = S(goal.Value);
                    p.strokeColor = Coords.Hex(0x2e241d);
                    p.lineWidth = 2;
                    p.lineCap = LineCap.Butt;
                    p.BeginPath();
                    p.MoveTo(g);
                    p.LineTo(g + new Vector2(0, -22));
                    p.Stroke();
                    p.fillColor = Coords.Hex(0xff6a00);
                    p.BeginPath();
                    p.MoveTo(g + new Vector2(0, -22));
                    p.LineTo(g + new Vector2(14, -17));
                    p.LineTo(g + new Vector2(0, -12));
                    p.ClosePath();
                    p.Fill();
                }
            }
            if (marker != null)
            {
                var at = S(marker.Value);
                p.BeginPath();
                p.Arc(at, 14, 0, 360);
                p.fillColor = Coords.Rgb(194, 65, 45, 0.22f);
                p.Fill();
                const float r = 9;
                float cy = at.y - 22;
                p.BeginPath();
                p.MoveTo(at);
                p.BezierCurveTo(new Vector2(at.x - 4, cy + r), new Vector2(at.x - r, cy + 4), new Vector2(at.x - r, cy));
                p.Arc(new Vector2(at.x, cy), r, 180, 360);
                p.BezierCurveTo(new Vector2(at.x + r, cy + 4), new Vector2(at.x + 4, cy + r), at);
                p.fillColor = BlueprintStyle.Spawn;
                p.Fill();
                p.lineWidth = 2;
                p.strokeColor = BlueprintStyle.SpawnRing;
                p.Stroke();
                p.BeginPath();
                p.Arc(new Vector2(at.x, cy), 3.5f, 0, 360);
                p.fillColor = BlueprintStyle.SpawnRing;
                p.Fill();
            }
        }
    }
}

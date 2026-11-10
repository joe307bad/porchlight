// The big map's County tab: the whole county at a glance. Porchlight in the
// middle, the public cities round it and your own towns up north, each named;
// the highways that join them (yours in purple), the hills between drawn as a
// shaded picture, where you are, and on a trip the way there dotted in blue.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.County;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace Porchlight.UI
{
    public sealed class CountyMapView
    {
        /// <summary>Tiles a pixel of the hills' picture, and the countryside shown round the county's spaces (tiles).</summary>
        const double TILES_PER_PIXEL = 4, MARGIN = 120;

        public readonly VisualElement root;
        readonly VisualElement marks, labels;
        CountyMap map;
        Texture2D picture;
        /// <summary>The part of the world shown (world tiles).</summary>
        double vx0, vy0, vw = 1, vh = 1;
        readonly List<(List<Vec2> line, bool mine)> roads = new List<(List<Vec2>, bool)>();
        IVisualElementScheduledItem repaint;

        /// <summary>Where you are (world tiles), and on a trip the way there (world tiles); either may be null.</summary>
        public Func<Vec2?> You;
        public Func<List<Vec2>> Way;
        /// <summary>The town you live in (its key), named a little larger.</summary>
        public string HomeKey;

        public CountyMapView()
        {
            root = new VisualElement();
            root.AddToClassList("bigmap-picture");
            marks = new VisualElement { pickingMode = PickingMode.Ignore };
            Fill(marks);
            marks.generateVisualContent += Draw;
            root.Add(marks);
            labels = new VisualElement { pickingMode = PickingMode.Ignore };
            Fill(labels);
            root.Add(labels);
        }

        static void Fill(VisualElement e)
        {
            e.style.position = Position.Absolute;
            e.style.left = e.style.top = e.style.right = e.style.bottom = 0;
        }

        public bool HasCounty => map != null;

        /// <summary>The county to show (null: none), and the hills between its towns.</summary>
        public void Set(CountyMap county, CountyTerrain terrain)
        {
            map = county;
            labels.Clear();
            roads.Clear();
            if (picture != null) UnityEngine.Object.Destroy(picture);
            picture = null;
            if (county == null) return;
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
            foreach (var s in county.spaces)
            {
                x0 = Math.Min(x0, s.x0); y0 = Math.Min(y0, s.y0);
                x1 = Math.Max(x1, s.x0 + s.width); y1 = Math.Max(y1, s.y0 + s.height);
            }
            vx0 = x0 - MARGIN; vy0 = y0 - MARGIN;
            vw = x1 - x0 + 2 * MARGIN; vh = y1 - y0 + 2 * MARGIN;
            picture = Hills(terrain);
            root.style.backgroundImage = new StyleBackground(picture);
            foreach (var hw in county.highways)
            {
                var line = new List<Vec2>();
                for (double s = 0; s < hw.length; s += 6) line.Add(CountyMap.PointAt(hw, s));
                line.Add(CountyMap.PointAt(hw, hw.length));
                roads.Add((line, hw.isPrivate));
            }
            foreach (var s in county.spaces)
            {
                if (s.kind != SpaceKind.Town) continue;
                var label = new Label(s.name);
                label.AddToClassList("county-label");
                if (s.isPrivate) label.AddToClassList("mine");
                if (s.townKey == HomeKey || s.place == Place.Porchlight) label.AddToClassList("home");
                label.style.left = Length.Percent((float)((s.x0 + s.width / 2.0 - vx0) / vw * 100));
                label.style.top = Length.Percent((float)((s.y0 - 4 - vy0) / vh * 100));
                labels.Add(label);
            }
            marks.MarkDirtyRepaint();
        }

        /// <summary>The countryside as a picture: grass, darker up the hills, lit from the north-west.</summary>
        Texture2D Hills(CountyTerrain t)
        {
            int w = Math.Max(1, (int)Math.Ceiling(vw / TILES_PER_PIXEL)), h = Math.Max(1, (int)Math.Ceiling(vh / TILES_PER_PIXEL));
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "county hills", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[w * h];
            Color low = Coords.Hex(0x9ccb6e), high = Coords.Hex(0x4f7d37);
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    // Row 0 is the picture's bottom: the county's south.
                    double x = vx0 + (c + 0.5) * TILES_PER_PIXEL, y = vy0 + vh - (r + 0.5) * TILES_PER_PIXEL;
                    double z = t.Height(x, y);
                    double slope = t.Height(x - TILES_PER_PIXEL, y - TILES_PER_PIXEL) - t.Height(x + TILES_PER_PIXEL, y + TILES_PER_PIXEL);
                    var col = Color.Lerp(low, high, (float)(z / CountyTerrain.PEAK));
                    float shade = Mathf.Clamp((float)(1 + slope * 0.12), 0.7f, 1.25f);
                    px[r * w + c] = new Color(col.r * shade, col.g * shade, col.b * shade, 1);
                }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        /// <summary>As big as `maxW` by `maxH` allows, keeping the county's shape.</summary>
        public void Fit(float maxW, float maxH)
        {
            if (map == null || float.IsNaN(maxW) || float.IsNaN(maxH)) return;
            float k = (float)Math.Min(maxW / vw, maxH / vh);
            root.style.width = (float)(vw * k);
            root.style.height = (float)(vh * k);
        }

        /// <summary>On screen or not: while it is, where you are is kept up to date.</summary>
        public void Show(bool on)
        {
            root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            repaint?.Pause();
            if (!on) return;
            marks.MarkDirtyRepaint();
            repaint ??= marks.schedule.Execute(() => marks.MarkDirtyRepaint()).Every(300);
            repaint.Resume();
        }

        static readonly Color Edge = Coords.Hex(0x3c342b), Road = Coords.Hex(0xf2e6c8), MineRoad = Coords.Hex(0xc9b2f0);
        static readonly Color Town = Coords.Hex(0xe9dcc0), MineTown = Coords.Hex(0xd8ccf2), Plot = Coords.Hex(0xc8d2b4);

        void Draw(MeshGenerationContext ctx)
        {
            if (map == null) return;
            var rect = marks.contentRect;
            float kx = (float)(rect.width / vw), ky = (float)(rect.height / vh);
            Vector2 P(Vec2 q) => new Vector2((float)(q.x - vx0) * kx, (float)(q.y - vy0) * ky);
            var painter = ctx.painter2D;
            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;
            // The highways: a dark edge, then the road.
            foreach (var (width, mineToo) in new[] { (6f, true), (3.5f, false) })
                foreach (var (line, mine) in roads)
                {
                    painter.strokeColor = mineToo ? Edge : mine ? MineRoad : Road;
                    painter.lineWidth = width;
                    Line(painter, line, P);
                }
            // The towns, the junction and the empty plots.
            foreach (var s in map.spaces)
            {
                if (s.kind == SpaceKind.Highway) continue;
                var a = P(new Vec2(s.x0, s.y0));
                var b = P(new Vec2(s.x0 + s.width, s.y0 + s.height));
                painter.fillColor = s.kind == SpaceKind.Town ? (s.isPrivate ? MineTown : Town) : Plot;
                painter.strokeColor = Edge;
                painter.lineWidth = s.kind == SpaceKind.Town && s.place == Place.Porchlight ? 3 : 1.5f;
                painter.BeginPath();
                painter.MoveTo(a);
                painter.LineTo(new Vector2(b.x, a.y));
                painter.LineTo(b);
                painter.LineTo(new Vector2(a.x, b.y));
                painter.ClosePath();
                painter.Fill();
                painter.Stroke();
            }
            // The way there, dotted in blue.
            var way = Way?.Invoke();
            if (way != null && way.Count > 1)
            {
                painter.fillColor = Coords.Hex(0x3b9be0);
                foreach (var q in Geo.SamplePolyline(way, Math.Max(4, 7 / kx)))
                {
                    painter.BeginPath();
                    painter.Arc(P(q), 2.4f, 0, 360);
                    painter.Fill();
                }
            }
            // You.
            var you = You?.Invoke();
            if (you != null)
            {
                painter.BeginPath();
                painter.Arc(P(you.Value), 6, 0, 360);
                painter.fillColor = Coords.Hex(0xc2412d);
                painter.Fill();
                painter.lineWidth = 2;
                painter.strokeColor = Coords.Hex(0xfff8e8);
                painter.Stroke();
            }
        }

        /// <summary>
        /// A line through these points, a point a pixel or more on from the last, in strokes of a few hundred
        /// (a highway has hundreds of points, and one stroke past 65535 vertices isn't drawn at all).
        /// </summary>
        static void Line(Painter2D painter, List<Vec2> pts, Func<Vec2, Vector2> P)
        {
            const int STROKE = 300;
            var last = P(pts[0]);
            painter.BeginPath();
            painter.MoveTo(last);
            int n = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var q = P(pts[i]);
                if (i < pts.Count - 1 && (q - last).sqrMagnitude < 1) continue;
                painter.LineTo(q);
                last = q;
                if (++n >= STROKE && i < pts.Count - 1)
                {
                    painter.Stroke();
                    painter.BeginPath();
                    painter.MoveTo(last);
                    n = 0;
                }
            }
            painter.Stroke();
        }

        public void Dispose()
        {
            repaint?.Pause();
            if (picture != null) UnityEngine.Object.Destroy(picture);
            picture = null;
        }
    }
}

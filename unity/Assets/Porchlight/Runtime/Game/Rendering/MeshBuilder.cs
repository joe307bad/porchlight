// A mesh builder for the town's procedural parts (ground tiles, roads,
// bridges, sheds, facades, rooms): flat-coloured quads, ribbons along
// polylines, walls and boxes, given in map terms (x, height, y) and built
// into one Unity mesh coloured through a Palette.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>A point in map terms: x east, h up, y south.</summary>
    public struct V3
    {
        public double x, h, y;

        public V3(double x, double h, double y)
        {
            this.x = x;
            this.h = h;
            this.y = y;
        }

        public Vector3 World => Coords.Map(x, y, h);
    }

    public sealed class MeshBuilder
    {
        readonly Palette palette;
        readonly List<Vector3> pos = new List<Vector3>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<int> idx = new List<int>();

        public static readonly V3 Up = new V3(0, 1, 0);

        public MeshBuilder(Palette palette) => this.palette = palette;

        public bool Empty => idx.Count == 0;

        /// <summary>A map point (x, y) at height h.</summary>
        public static V3 v(double x, double h, double y) => new V3(x, h, y);

        /// <summary>A quad (corners in order round its edge) facing roughly `facing` (wound to match). `glow`: its colour at night, if it lights up.</summary>
        public void Quad(V3 a, V3 b, V3 c, V3 d, int color, V3? facing = null, int glow = 0)
        {
            var f = (facing ?? Up).World;
            Vector3 pa = a.World, pb = b.World, pc = c.World, pd = d.World;
            bool flip = Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), f) < 0;
            int start = pos.Count;
            if (flip)
            {
                pos.Add(pa);
                pos.Add(pd);
                pos.Add(pc);
                pos.Add(pb);
            }
            else
            {
                pos.Add(pa);
                pos.Add(pb);
                pos.Add(pc);
                pos.Add(pd);
            }
            var t = palette.UV(color, glow);
            for (int k = 0; k < 4; k++) uv.Add(t);
            idx.Add(start);
            idx.Add(start + 1);
            idx.Add(start + 2);
            idx.Add(start);
            idx.Add(start + 2);
            idx.Add(start + 3);
        }

        /// <summary>A flat axis-aligned rectangle at height h.</summary>
        public void Rect(double x0, double y0, double x1, double y1, double h, int color) =>
            Quad(v(x0, h, y0), v(x1, h, y0), v(x1, h, y1), v(x0, h, y1), color);

        /// <summary>A flat strip `width` wide along a polyline, at height h.</summary>
        public void Ribbon(IReadOnlyList<Vec2> pts, double width, double h, int color)
        {
            if (pts.Count < 2) return;
            var l = Layout.OffsetPolyline(pts, width / 2);
            var r = Layout.OffsetPolyline(pts, -width / 2);
            for (int k = 1; k < pts.Count; k++)
                Quad(v(l[k - 1].x, h, l[k - 1].y), v(l[k].x, h, l[k].y), v(r[k].x, h, r[k].y), v(r[k - 1].x, h, r[k - 1].y), color);
        }

        /// <summary>A flat disc (a round road end or junction cap).</summary>
        public void Disc(Vec2 c, double radius, double h, int color, int segments = 18)
        {
            V3 At(int i)
            {
                double a = ((double)i / segments) * Math.PI * 2;
                return v(c.x + Math.Cos(a) * radius, h, c.y + Math.Sin(a) * radius);
            }
            for (int k = 0; k < segments; k += 2) Quad(v(c.x, h, c.y), At(k), At(k + 1), At(k + 2), color);
        }

        /// <summary>A vertical wall along a polyline offset sideways by `offset`, from h0 up to h1, facing away from the line.</summary>
        public void Wall(IReadOnlyList<Vec2> pts, double offset, double h0, double h1, int color)
        {
            var side = Layout.OffsetPolyline(pts, offset);
            for (int k = 1; k < side.Count; k++)
            {
                var a = side[k - 1];
                var b = side[k];
                double midx = (a.x + b.x) / 2, midy = (a.y + b.y) / 2;
                double cx = (pts[k - 1].x + pts[k].x) / 2, cy = (pts[k - 1].y + pts[k].y) / 2;
                Quad(v(a.x, h0, a.y), v(b.x, h0, b.y), v(b.x, h1, b.y), v(a.x, h1, a.y), color, new V3(midx - cx, 0, midy - cy));
            }
        }

        /// <summary>A low wall `thickness` thick centred on a polyline offset by `offset`: two sides and a top.</summary>
        public void Parapet(IReadOnlyList<Vec2> pts, double offset, double thickness, double h0, double h1, int color, int shade)
        {
            double t = thickness / 2;
            double s = Math.Sign(offset == 0 ? 1 : offset);
            Wall(pts, offset + s * t, h0, h1, shade);
            Wall(pts, offset - s * t, h0, h1, color);
            Ribbon(Layout.OffsetPolyline(pts, offset), thickness, h1, color);
        }

        /// <summary>An axis-aligned box, with an optional colour for its top.</summary>
        public void Box(double x0, double y0, double x1, double y1, double h0, double h1, int color, int? top = null)
        {
            Rect(x0, y0, x1, y1, h1, top ?? color);
            Quad(v(x0, h0, y0), v(x1, h0, y0), v(x1, h1, y0), v(x0, h1, y0), color, new V3(0, 0, -1));
            Quad(v(x0, h0, y1), v(x1, h0, y1), v(x1, h1, y1), v(x0, h1, y1), color, new V3(0, 0, 1));
            Quad(v(x0, h0, y0), v(x0, h0, y1), v(x0, h1, y1), v(x0, h1, y0), color, new V3(-1, 0, 0));
            Quad(v(x1, h0, y0), v(x1, h0, y1), v(x1, h1, y1), v(x1, h1, y0), color, new V3(1, 0, 0));
        }

        public Mesh Build(string name = "built")
        {
            var mesh = new Mesh { name = name, indexFormat = pos.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(pos);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(idx, 0, true);
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>The mesh as a GameObject drawn with the palette.</summary>
        public GameObject Build(string name, Transform parent, bool castShadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Build(name);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = palette.Material;
            r.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }
    }
}

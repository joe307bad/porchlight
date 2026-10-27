// A little mesh builder for the procedural parts of the town (ground tiles,
// roads, bridges, sheds, facades): flat-coloured quads, ribbons along
// polylines and walls, collected into one vertex-coloured mesh. Points are in
// Three's space (map (x, y) is (x, z); height is y); Build() converts to
// Unity's and works out normals as Three's computeVertexNormals does. A port
// of render/iso/mesh.ts.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public struct V3
    {
        public double x, y, z;

        public V3(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    public sealed class MeshBuilder
    {
        readonly List<float> pos = new List<float>();
        readonly List<Color> col = new List<Color>();
        readonly List<int> idx = new List<int>();
        /// <summary>Per vertex: 0, or a window pane's seed (see WindowTags).</summary>
        readonly List<float> seeds = new List<float>();

        public static readonly V3 Up = new V3(0, 1, 0);

        public bool Empty => idx.Count == 0;

        /// <summary>A map point (x, y) at height h.</summary>
        public static V3 v(double x, double h, double y) => new V3(x, h, y);

        /// <summary>A quad (corners in order round its edge) facing roughly `facing` (winding fixed to match).</summary>
        public void Quad(V3 a, V3 b, V3 c, V3 d, Color color, V3? facing = null, double seed = 0)
        {
            var f = facing ?? Up;
            int start = pos.Count / 3;
            double abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
            double acx = c.x - a.x, acy = c.y - a.y, acz = c.z - a.z;
            double nx = aby * acz - abz * acy;
            double ny = abz * acx - abx * acz;
            double nz = abx * acy - aby * acx;
            bool flip = nx * f.x + ny * f.y + nz * f.z < 0;
            foreach (var p in flip ? new[] { a, d, c, b } : new[] { a, b, c, d })
            {
                pos.Add((float)p.x);
                pos.Add((float)p.y);
                pos.Add((float)p.z);
            }
            for (int k = 0; k < 4; k++)
            {
                col.Add(color);
                seeds.Add((float)seed);
            }
            idx.Add(start);
            idx.Add(start + 1);
            idx.Add(start + 2);
            idx.Add(start);
            idx.Add(start + 2);
            idx.Add(start + 3);
        }

        public void Quad(V3 a, V3 b, V3 c, V3 d, int hex, V3? facing = null, double seed = 0) => Quad(a, b, c, d, Space.Hex(hex), facing, seed);

        /// <summary>A flat axis-aligned rectangle at height y (map coordinates).</summary>
        public void Rect(double x0, double y0, double x1, double y1, double y, int color) =>
            Quad(v(x0, y, y0), v(x1, y, y0), v(x1, y, y1), v(x0, y, y1), color);

        /// <summary>A flat strip `width` wide along a polyline, at height y.</summary>
        public void Ribbon(IReadOnlyList<Vec2> pts, double width, double y, int color)
        {
            if (pts.Count < 2) return;
            var l = Layout.OffsetPolyline(pts, width / 2);
            var r = Layout.OffsetPolyline(pts, -width / 2);
            var c = Space.Hex(color);
            for (int k = 1; k < pts.Count; k++)
                Quad(v(l[k - 1].x, y, l[k - 1].y), v(l[k].x, y, l[k].y), v(r[k].x, y, r[k].y), v(r[k - 1].x, y, r[k - 1].y), c);
        }

        /// <summary>A flat disc (a round road end or junction cap).</summary>
        public void Disc(Vec2 c, double radius, double y, int color, int segments = 18)
        {
            var colr = Space.Hex(color);
            V3 At(int i)
            {
                double a = ((double)i / segments) * Math.PI * 2;
                return v(c.x + Math.Cos(a) * radius, y, c.y + Math.Sin(a) * radius);
            }
            for (int k = 0; k < segments; k += 2) Quad(v(c.x, y, c.y), At(k), At(k + 1), At(k + 2), colr);
        }

        /// <summary>A vertical wall along a polyline offset sideways by `offset`, from y0 up to y1, facing away from the line.</summary>
        public void Wall(IReadOnlyList<Vec2> pts, double offset, double y0, double y1, int color)
        {
            var side = Layout.OffsetPolyline(pts, offset);
            var c = Space.Hex(color);
            for (int k = 1; k < side.Count; k++)
            {
                var a = side[k - 1];
                var b = side[k];
                double midx = (a.x + b.x) / 2, midy = (a.y + b.y) / 2;
                double cx = (pts[k - 1].x + pts[k].x) / 2, cy = (pts[k - 1].y + pts[k].y) / 2;
                var o = new V3(midx - cx, 0, midy - cy);
                Quad(v(a.x, y0, a.y), v(b.x, y0, b.y), v(b.x, y1, b.y), v(a.x, y1, a.y), c, o);
            }
        }

        /// <summary>A low wall `thickness` thick centred on a polyline offset by `offset`: two sides and a top.</summary>
        public void Parapet(IReadOnlyList<Vec2> pts, double offset, double thickness, double y0, double y1, int color, int shade)
        {
            double o = offset;
            double t = thickness / 2;
            double s = Math.Sign(o == 0 ? 1 : o);
            Wall(pts, o + s * t, y0, y1, shade);
            Wall(pts, o - s * t, y0, y1, color);
            Ribbon(Layout.OffsetPolyline(pts, o), thickness, y1, color);
        }

        /// <summary>An axis-aligned box (map coordinates), with an optional colour for its top.</summary>
        public void Box(double x0, double y0, double x1, double y1, double h0, double h1, int color, int? top = null)
        {
            Rect(x0, y0, x1, y1, h1, top ?? color);
            var c = Space.Hex(color);
            Quad(v(x0, h0, y0), v(x1, h0, y0), v(x1, h1, y0), v(x0, h1, y0), c, new V3(0, 0, -1));
            Quad(v(x0, h0, y1), v(x1, h0, y1), v(x1, h1, y1), v(x0, h1, y1), c, new V3(0, 0, 1));
            Quad(v(x0, h0, y0), v(x0, h0, y1), v(x0, h1, y1), v(x0, h1, y0), c, new V3(-1, 0, 0));
            Quad(v(x1, h0, y0), v(x1, h0, y1), v(x1, h1, y1), v(x1, h1, y0), c, new V3(1, 0, 0));
        }

        /// <summary>The geometry as a Unity mesh (vertex colours in .colors, window seeds in uv2.x).</summary>
        public Mesh Build(string name = "built")
        {
            int n = pos.Count / 3;
            // computeVertexNormals: area-weighted face normals, accumulated per vertex in floats, then normalised.
            var nrm = new float[n * 3];
            for (int t = 0; t < idx.Count; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                double pax = pos[a * 3], pay = pos[a * 3 + 1], paz = pos[a * 3 + 2];
                double pbx = pos[b * 3], pby = pos[b * 3 + 1], pbz = pos[b * 3 + 2];
                double pcx = pos[c * 3], pcy = pos[c * 3 + 1], pcz = pos[c * 3 + 2];
                double cbx = pcx - pbx, cby = pcy - pby, cbz = pcz - pbz;
                double abx = pax - pbx, aby = pay - pby, abz = paz - pbz;
                double nx = cby * abz - cbz * aby;
                double ny = cbz * abx - cbx * abz;
                double nz = cbx * aby - cby * abx;
                foreach (int vtx in new[] { a, b, c })
                {
                    nrm[vtx * 3] = (float)(nrm[vtx * 3] + nx);
                    nrm[vtx * 3 + 1] = (float)(nrm[vtx * 3 + 1] + ny);
                    nrm[vtx * 3 + 2] = (float)(nrm[vtx * 3 + 2] + nz);
                }
            }
            var vertices = new Vector3[n];
            var normals = new Vector3[n];
            var uv2 = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                vertices[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], -pos[i * 3 + 2]);
                double x = nrm[i * 3], y = nrm[i * 3 + 1], z = nrm[i * 3 + 2];
                double len = Math.Sqrt(x * x + y * y + z * z);
                double k = len == 0 ? 1 : 1 / len;
                normals[i] = new Vector3((float)(x * k), (float)(y * k), (float)(-z * k));
                uv2[i] = new Vector2(seeds[i], 0);
            }
            var tris = new int[idx.Count];
            for (int t = 0; t < idx.Count; t += 3)
            {
                tris[t] = idx[t];
                tris[t + 1] = idx[t + 2];
                tris[t + 2] = idx[t + 1];
            }
            var mesh = new Mesh { name = name, indexFormat = n > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(col);
            mesh.SetUVs(1, uv2);
            mesh.SetTriangles(tris, 0, true);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

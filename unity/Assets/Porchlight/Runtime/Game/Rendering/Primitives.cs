// Three's BoxGeometry, CylinderGeometry, SphereGeometry and PlaneGeometry (one
// segment where the town uses one), in Three's space as ModelParts, with the
// translate / rotate / merge helpers the world uses on them. TownAssets.ToMesh
// turns a part into a Unity mesh. Also the soft radial glow texture for lamp
// halos and light pools (textures.ts).

using System;
using System.Collections.Generic;
using Porchlight.Core;
using UnityEngine;

namespace Porchlight.Rendering
{
    public static class Primitives
    {
        public static ModelPart Box(double width, double height, double depth)
        {
            var pos = new List<float>();
            var nrm = new List<float>();
            var uv = new List<float>();
            var idx = new List<int>();
            void Plane(int u, int v, int w, double udir, double vdir, double pw, double ph, double pd)
            {
                double widthHalf = pw / 2, heightHalf = ph / 2, depthHalf = pd / 2;
                int start = pos.Count / 3;
                for (int iy = 0; iy < 2; iy++)
                {
                    double y = iy * ph - heightHalf;
                    for (int ix = 0; ix < 2; ix++)
                    {
                        double x = ix * pw - widthHalf;
                        var vec = new double[3];
                        vec[u] = x * udir;
                        vec[v] = y * vdir;
                        vec[w] = depthHalf;
                        pos.Add((float)vec[0]);
                        pos.Add((float)vec[1]);
                        pos.Add((float)vec[2]);
                        var n = new double[3];
                        n[w] = pd > 0 ? 1 : -1;
                        nrm.Add((float)n[0]);
                        nrm.Add((float)n[1]);
                        nrm.Add((float)n[2]);
                        uv.Add(ix);
                        uv.Add(1 - iy);
                    }
                }
                int a = start, b = start + 2, c = start + 3, d = start + 1;
                idx.AddRange(new[] { a, b, d, b, c, d });
            }
            Plane(2, 1, 0, -1, -1, depth, height, width);
            Plane(2, 1, 0, 1, -1, depth, height, -width);
            Plane(0, 2, 1, 1, 1, width, depth, height);
            Plane(0, 2, 1, 1, -1, width, depth, -height);
            Plane(0, 1, 2, 1, -1, width, height, depth);
            Plane(0, 1, 2, -1, -1, width, height, -depth);
            return new ModelPart { pos = pos.ToArray(), normal = nrm.ToArray(), uv = uv.ToArray(), index = idx.ToArray() };
        }

        public static ModelPart Cylinder(double radiusTop, double radiusBottom, double height, int radialSegments)
        {
            var pos = new List<float>();
            var nrm = new List<float>();
            var uv = new List<float>();
            var idx = new List<int>();
            double halfHeight = height / 2;
            double slope = (radiusBottom - radiusTop) / height;
            var rows = new List<int[]>();
            for (int y = 0; y <= 1; y++)
            {
                var row = new int[radialSegments + 1];
                double v = y;
                double radius = v * (radiusBottom - radiusTop) + radiusTop;
                for (int x = 0; x <= radialSegments; x++)
                {
                    double u = (double)x / radialSegments;
                    double theta = u * Math.PI * 2;
                    double sin = Math.Sin(theta), cos = Math.Cos(theta);
                    row[x] = pos.Count / 3;
                    pos.Add((float)(radius * sin));
                    pos.Add((float)(-v * height + halfHeight));
                    pos.Add((float)(radius * cos));
                    double nx = sin, ny = slope, nz = cos;
                    double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    nrm.Add((float)(nx / l));
                    nrm.Add((float)(ny / l));
                    nrm.Add((float)(nz / l));
                    uv.Add((float)u);
                    uv.Add((float)(1 - v));
                }
                rows.Add(row);
            }
            for (int x = 0; x < radialSegments; x++)
            {
                int a = rows[0][x], b = rows[1][x], c = rows[1][x + 1], d = rows[0][x + 1];
                idx.AddRange(new[] { a, b, d, b, c, d });
            }
            void Cap(bool top)
            {
                double radius = top ? radiusTop : radiusBottom;
                double sign = top ? 1 : -1;
                int centerStart = pos.Count / 3;
                for (int x = 1; x <= radialSegments; x++)
                {
                    pos.Add(0);
                    pos.Add((float)(halfHeight * sign));
                    pos.Add(0);
                    nrm.Add(0);
                    nrm.Add((float)sign);
                    nrm.Add(0);
                    uv.Add(0.5f);
                    uv.Add(0.5f);
                }
                int centerEnd = pos.Count / 3;
                for (int x = 0; x <= radialSegments; x++)
                {
                    double theta = ((double)x / radialSegments) * Math.PI * 2;
                    double cos = Math.Cos(theta), sin = Math.Sin(theta);
                    pos.Add((float)(radius * sin));
                    pos.Add((float)(halfHeight * sign));
                    pos.Add((float)(radius * cos));
                    nrm.Add(0);
                    nrm.Add((float)sign);
                    nrm.Add(0);
                    uv.Add((float)(cos * 0.5 + 0.5));
                    uv.Add((float)(sin * 0.5 * sign + 0.5));
                }
                for (int x = 0; x < radialSegments; x++)
                {
                    int c = centerStart + x;
                    int i = centerEnd + x;
                    if (top) idx.AddRange(new[] { i, i + 1, c });
                    else idx.AddRange(new[] { i + 1, i, c });
                }
            }
            if (radiusTop > 0) Cap(true);
            if (radiusBottom > 0) Cap(false);
            return new ModelPart { pos = pos.ToArray(), normal = nrm.ToArray(), uv = uv.ToArray(), index = idx.ToArray() };
        }

        public static ModelPart Sphere(double radius, int widthSegments, int heightSegments)
        {
            var pos = new List<float>();
            var nrm = new List<float>();
            var uv = new List<float>();
            var idx = new List<int>();
            var grid = new List<int[]>();
            int index = 0;
            for (int iy = 0; iy <= heightSegments; iy++)
            {
                var row = new int[widthSegments + 1];
                double v = (double)iy / heightSegments;
                double uOffset = iy == 0 ? 0.5 / widthSegments : iy == heightSegments ? -0.5 / widthSegments : 0;
                for (int ix = 0; ix <= widthSegments; ix++)
                {
                    double u = (double)ix / widthSegments;
                    double x = -radius * Math.Cos(u * Math.PI * 2) * Math.Sin(v * Math.PI);
                    double y = radius * Math.Cos(v * Math.PI);
                    double z = radius * Math.Sin(u * Math.PI * 2) * Math.Sin(v * Math.PI);
                    pos.Add((float)x);
                    pos.Add((float)y);
                    pos.Add((float)z);
                    double l = Math.Sqrt(x * x + y * y + z * z);
                    if (l == 0) l = 1;
                    nrm.Add((float)(x / l));
                    nrm.Add((float)(y / l));
                    nrm.Add((float)(z / l));
                    uv.Add((float)(u + uOffset));
                    uv.Add((float)(1 - v));
                    row[ix] = index++;
                }
                grid.Add(row);
            }
            for (int iy = 0; iy < heightSegments; iy++)
            {
                for (int ix = 0; ix < widthSegments; ix++)
                {
                    int a = grid[iy][ix + 1], b = grid[iy][ix], c = grid[iy + 1][ix], d = grid[iy + 1][ix + 1];
                    if (iy != 0) idx.AddRange(new[] { a, b, d });
                    if (iy != heightSegments - 1) idx.AddRange(new[] { b, c, d });
                }
            }
            return new ModelPart { pos = pos.ToArray(), normal = nrm.ToArray(), uv = uv.ToArray(), index = idx.ToArray() };
        }

        public static ModelPart Plane(double width, double height)
        {
            var pos = new float[12];
            var nrm = new float[12];
            var uv = new float[8];
            int k = 0;
            for (int iy = 0; iy < 2; iy++)
            {
                double y = iy * height - height / 2;
                for (int ix = 0; ix < 2; ix++)
                {
                    double x = ix * width - width / 2;
                    pos[k * 3] = (float)x;
                    pos[k * 3 + 1] = (float)-y;
                    pos[k * 3 + 2] = 0;
                    nrm[k * 3 + 2] = 1;
                    uv[k * 2] = ix;
                    uv[k * 2 + 1] = 1 - iy;
                    k++;
                }
            }
            return new ModelPart { pos = pos, normal = nrm, uv = uv, index = new[] { 0, 2, 1, 2, 3, 1 } };
        }

        public static ModelPart Translate(ModelPart p, double x, double y, double z)
        {
            for (int i = 0; i < p.pos.Length; i += 3)
            {
                p.pos[i] = (float)(p.pos[i] + x);
                p.pos[i + 1] = (float)(p.pos[i + 1] + y);
                p.pos[i + 2] = (float)(p.pos[i + 2] + z);
            }
            return p;
        }

        /// <summary>BufferGeometry.rotateX(angle).</summary>
        public static ModelPart RotateX(ModelPart p, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            void Rot(float[] a)
            {
                for (int i = 0; i < a.Length; i += 3)
                {
                    double y = a[i + 1], z = a[i + 2];
                    a[i + 1] = (float)(y * c - z * s);
                    a[i + 2] = (float)(y * s + z * c);
                }
            }
            Rot(p.pos);
            if (p.normal != null) Rot(p.normal);
            return p;
        }

        /// <summary>mergeGeometries: one part from several (all indexed, or all not).</summary>
        public static ModelPart Merge(params ModelPart[] parts)
        {
            var pos = new List<float>();
            var nrm = new List<float>();
            var uv = new List<float>();
            var idx = new List<int>();
            bool indexed = parts[0].index != null;
            foreach (var p in parts)
            {
                int start = pos.Count / 3;
                pos.AddRange(p.pos);
                nrm.AddRange(p.normal);
                uv.AddRange(p.uv);
                if (indexed) foreach (int i in p.index) idx.Add(i + start);
            }
            return new ModelPart { pos = pos.ToArray(), normal = nrm.ToArray(), uv = uv.ToArray(), index = indexed ? idx.ToArray() : null };
        }

        public static Mesh ToMesh(ModelPart p, string name) => TownAssets.ToMesh(name, p);

        static Texture2D glow;

        /// <summary>A white radial falloff on transparent black (alpha 1 at the centre, 0.45 at 35%, 0 at the edge), 64 px.</summary>
        public static Texture2D Glow
        {
            get
            {
                if (glow != null) return glow;
                const int n = 64;
                glow = new Texture2D(n, n, TextureFormat.RGBA32, true, false) { name = "glow", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        double d = Math.Sqrt((x + 0.5 - 32) * (x + 0.5 - 32) + (y + 0.5 - 32) * (y + 0.5 - 32)) / 32;
                        double a = d < 0.35 ? 1 + (0.45 - 1) * (d / 0.35) : d < 1 ? 0.45 + (0 - 0.45) * ((d - 0.35) / 0.65) : 0;
                        px[y * n + x] = new Color32(255, 255, 255, (byte)Math.Round(Math.Max(0, a) * 255));
                    }
                }
                glow.SetPixels32(px);
                glow.Apply(true, false);
                return glow;
            }
        }
    }
}

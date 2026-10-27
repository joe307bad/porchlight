// Where a car's lamps are (headlights.ts), and the window-pane tagging that
// lights windows at night (windows.ts): each glass pane in a building's
// geometry gets a seed shared by every vertex of that pane, so a window is lit
// or dark as a whole; curtain walls get the grid they light cell by cell on.
// Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public struct CarShape
    {
        public double halfLength, halfWidth, lampHeight;
    }

    public struct Lamp3
    {
        public double x, y, h;
    }

    public struct CarLights
    {
        public Lamp3 front0, front1, back0, back1;
        public Vec2 beam;
        public Vec2 forward;
    }

    public static class CarLamps
    {
        public const double BEAM_LENGTH = 2.6;
        public const double BEAM_WIDTH = 1.15;
        const double LAMP_INSET = 0.06;

        public static CarShape Shape(double sizeX, double sizeY, double sizeZ, double scale) =>
            new CarShape { halfLength = (sizeZ * scale) / 2, halfWidth = (sizeX * scale) / 2, lampHeight = sizeY * scale * 0.36 };

        public static CarLights Of(double x, double y, double heading, CarShape shape)
        {
            double fx = Math.Cos(heading), fy = Math.Sin(heading);
            double rx = -fy, ry = fx;
            double along = shape.halfLength - 0.012;
            double side = Math.Max(0.05, shape.halfWidth - LAMP_INSET * 1.4);
            Lamp3 L(double a, double b) => new Lamp3 { x = x + fx * a + rx * b, y = y + fy * a + ry * b, h = shape.lampHeight };
            double reach = shape.halfLength + BEAM_LENGTH / 2 - 0.1;
            return new CarLights
            {
                front0 = L(along, side),
                front1 = L(along, -side),
                back0 = L(-along, side),
                back1 = L(-along, -side),
                beam = new Vec2(x + fx * reach, y + fy * reach),
                forward = new Vec2(fx, fy),
            };
        }
    }

    public static class WindowTags
    {
        /// <summary>True if an sRGB colour (0..255) is window glass: the kits' pale sky-blue swatches.</summary>
        public static bool IsGlass(int r, int g, int b) => b >= 200 && b - r >= 40 && g - r >= 20 && g - r <= 45 && b - g >= 15;

        /// <summary>A stable pseudo-random 0..1 per pane number.</summary>
        public static double PaneHash(int n)
        {
            unchecked
            {
                uint x = ((uint)n ^ 0x9e3779b9u) * 0x85ebca6bu;
                x ^= x >> 13;
                x *= 0xc2b2ae35u;
                x ^= x >> 16;
                return x / 4294967296.0;
            }
        }

        /// <summary>Per-vertex results for an un-indexed triangle list.</summary>
        public sealed class Result
        {
            public float[] seeds;
            /// <summary>(first floor line, floor spacing, first mullion, mullion spacing) per vertex.</summary>
            public float[] grid;
            public float[] axis;
        }

        /// <summary>
        /// Tag an un-indexed triangle list (positions xyz per vertex, uvs per vertex)
        /// against a colourmap (RGBA rows, top row first; glTF UVs, v down the image).
        /// </summary>
        public static Result Tag(float[] pos, float[] uv, byte[] pixels, int width, int height)
        {
            int count = pos.Length / 3;
            var res = new Result { seeds = new float[count], grid = new float[count * 4], axis = new float[count] };
            if (pixels == null || uv == null) return res;
            int tris = count / 3;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            int Find(int i)
            {
                while (parent[i] != i) i = parent[i] = parent[parent[i]];
                return i;
            }
            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
            var byPosition = new Dictionary<(long, long, long), int>();
            (long, long, long) Key(int i) => ((long)Js.Round(pos[i * 3] * 1e4), (long)Js.Round(pos[i * 3 + 1] * 1e4), (long)Js.Round(pos[i * 3 + 2] * 1e4));
            var glass = new List<int>();
            for (int t = 0; t < tris; t++)
            {
                int a = t * 3, b = t * 3 + 1, c = t * 3 + 2;
                double u = ((double)uv[a * 2] + uv[b * 2] + uv[c * 2]) / 3;
                double v = ((double)uv[a * 2 + 1] + uv[b * 2 + 1] + uv[c * 2 + 1]) / 3;
                int px = (int)Math.Min(width - 1, Math.Max(0, Math.Floor((u - Math.Floor(u)) * width)));
                int py = (int)Math.Min(height - 1, Math.Max(0, Math.Floor((v - Math.Floor(v)) * height)));
                int o = (py * width + px) * 4;
                if (!IsGlass(pixels[o], pixels[o + 1], pixels[o + 2])) continue;
                glass.Add(t);
                foreach (int i in new[] { a, b, c })
                {
                    var k = Key(i);
                    if (!byPosition.TryGetValue(k, out int same)) byPosition[k] = i;
                    else Union(i, same);
                }
                Union(a, b);
                Union(b, c);
            }
            var extent = new Dictionary<int, (double[] lo, double[] hi)>();
            foreach (int t in glass)
            {
                for (int k = 0; k < 3; k++)
                {
                    int i = t * 3 + k;
                    int root = Find(i);
                    var p = new double[] { pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2] };
                    if (!extent.TryGetValue(root, out var e)) extent[root] = (p, (double[])p.Clone());
                    else
                        for (int d = 0; d < 3; d++)
                        {
                            e.lo[d] = Math.Min(e.lo[d], p[d]);
                            e.hi[d] = Math.Max(e.hi[d], p[d]);
                        }
                }
            }
            double[] bmin = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            double[] bmax = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            for (int i = 0; i < count; i++)
                for (int d = 0; d < 3; d++)
                {
                    bmin[d] = Math.Min(bmin[d], pos[i * 3 + d]);
                    bmax[d] = Math.Max(bmax[d], pos[i * 3 + d]);
                }
            double sx = bmax[0] - bmin[0], sy = bmax[1] - bmin[1], sz = bmax[2] - bmin[2];
            var isGlassTri = new byte[tris];
            foreach (int t in glass) isGlassTri[t] = 1;
            var seedOf = new Dictionary<int, (double seed, double[] grid, double axis)>();
            foreach (int t in glass)
            {
                for (int k = 0; k < 3; k++)
                {
                    int i = t * 3 + k;
                    int root = Find(i);
                    if (!seedOf.TryGetValue(root, out var pane))
                    {
                        var e = extent[root];
                        bool big = e.hi[1] - e.lo[1] > sy * 0.2 || Math.Max(e.hi[0] - e.lo[0], e.hi[2] - e.lo[2]) > Math.Max(sx, sz) * 0.2;
                        double[] grid = { 0, 0, 0, 0 };
                        double axis = 0;
                        if (big) CurtainGrid(e.lo, e.hi, pos, tris, isGlassTri, out grid, out axis);
                        pane = ((big ? 3 : 1) + PaneHash(seedOf.Count + 1), grid, axis);
                        seedOf[root] = pane;
                    }
                    res.seeds[i] = (float)pane.seed;
                    for (int g = 0; g < 4; g++) res.grid[i * 4 + g] = (float)pane.grid[g];
                    res.axis[i] = (float)pane.axis;
                }
            }
            return res;
        }

        static void CurtainGrid(double[] lo0, double[] hi0, float[] pos, int tris, byte[] isGlassTri, out double[] grid, out double axisOut)
        {
            bool flatInX = hi0[0] - lo0[0] < hi0[2] - lo0[2];
            int across = flatInX ? 2 : 0;
            int depth = flatInX ? 0 : 2;
            double plane = (lo0[depth] + hi0[depth]) / 2;
            double height = hi0[1] - lo0[1];
            double width = hi0[across] - lo0[across];
            var floors = new List<double>();
            var mullions = new List<double>();
            var lo = new double[3];
            var hi = new double[3];
            for (int t = 0; t < tris; t++)
            {
                if (isGlassTri[t] != 0) continue;
                for (int d = 0; d < 3; d++)
                {
                    double a = pos[(t * 3) * 3 + d], b = pos[(t * 3 + 1) * 3 + d], c = pos[(t * 3 + 2) * 3 + d];
                    lo[d] = Math.Min(a, Math.Min(b, c));
                    hi[d] = Math.Max(a, Math.Max(b, c));
                }
                if (lo[depth] > plane + 0.12 || hi[depth] < plane - 0.12) continue;
                if (hi[1] < lo0[1] || lo[1] > hi0[1] || hi[across] < lo0[across] || lo[across] > hi0[across]) continue;
                double tall = hi[1] - lo[1];
                double wide = hi[across] - lo[across];
                if (tall < 0.08 && wide > width * 0.5)
                {
                    double y = (lo[1] + hi[1]) / 2;
                    if (y > lo0[1] + 0.02 && y < hi0[1] - 0.02) floors.Add(y);
                }
                else if (wide < 0.08 && tall > Math.Min(height * 0.5, 0.15))
                {
                    double c = (lo[across] + hi[across]) / 2;
                    if (c > lo0[across] + 0.02 && c < hi0[across] - 0.02) mullions.Add(c);
                }
            }
            var bays = Spacing(mullions);
            if (bays[1] == 0)
            {
                double n = Math.Max(1, Js.Round(width / 0.3));
                bays = new[] { lo0[across], width / n };
            }
            var f = Spacing(floors);
            grid = new[] { f[0], f[1], bays[0], bays[1] };
            axisOut = across == 2 ? 1 : 0;
        }

        static double[] Spacing(List<double> hits)
        {
            var sorted = new List<double>(hits);
            sorted.Sort();
            var lines = new List<List<double>>();
            foreach (var h in sorted)
            {
                var last = lines.Count > 0 ? lines[lines.Count - 1] : null;
                if (last != null && h - last[last.Count - 1] < 0.06) last.Add(h);
                else lines.Add(new List<double> { h });
            }
            var centres = new List<double>();
            foreach (var l in lines)
            {
                double sum = 0;
                foreach (var v in l) sum += v;
                centres.Add(sum / l.Count);
            }
            if (centres.Count < 2) return new[] { 0.0, 0.0 };
            return new[] { centres[0], (centres[centres.Count - 1] - centres[0]) / (centres.Count - 1) };
        }
    }
}

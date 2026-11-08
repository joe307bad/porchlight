// The hills between the towns. The county is flat where anything is (every
// town, the junction, your plots, and either side of every highway), and rises
// into rolling hills away from them, so the roads wind along valleys. Nothing
// drives, walks or lands on the hills: they're the countryside's to draw, and
// a plane's to clear. Heights are kept on a grid (world tiles) and read back
// between its points.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.County
{
    public sealed class CountyTerrain
    {
        /// <summary>Between the grid's points (tiles).</summary>
        public const double CELL = 6;
        /// <summary>Flat this far from a highway's middle line, or a town's (junction's, plot's) edge (tiles)...</summary>
        public const double FLAT = 12;
        /// <summary>...then rising to the full height of the hills over this much more.</summary>
        public const double RAMP = 40;
        /// <summary>The highest a hill reaches (tiles: 4 m each).</summary>
        public const double PEAK = 11;
        /// <summary>Lower than this (tiles) still counts as level ground.</summary>
        public const double LEVEL = 0.15;
        /// <summary>The grid reaches this far past every space (tiles); past that, the fog.</summary>
        const double MARGIN = 420;

        public readonly double x0, y0;
        public readonly int nx, ny;
        readonly float[] heights;

        CountyTerrain(double x0, double y0, int nx, int ny)
        {
            this.x0 = x0;
            this.y0 = y0;
            this.nx = nx;
            this.ny = ny;
            heights = new float[nx * ny];
        }

        /// <summary>A grid point's height (tiles).</summary>
        public float At(int i, int j) => heights[Math.Max(0, Math.Min(ny - 1, j)) * nx + Math.Max(0, Math.Min(nx - 1, i))];

        /// <summary>The ground's height at a world point (tiles), between the grid's points.</summary>
        public double Height(double x, double y)
        {
            double fx = (x - x0) / CELL, fy = (y - y0) / CELL;
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy);
            double u = fx - i, v = fy - j;
            return (At(i, j) * (1 - u) + At(i + 1, j) * u) * (1 - v) + (At(i, j + 1) * (1 - u) + At(i + 1, j + 1) * u) * v;
        }

        public bool Level(double x, double y) => Height(x, y) < LEVEL;

        public static CountyTerrain Build(CountyMap map)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var s in map.spaces)
            {
                minX = Math.Min(minX, s.x0); minY = Math.Min(minY, s.y0);
                maxX = Math.Max(maxX, s.x0 + s.width); maxY = Math.Max(maxY, s.y0 + s.height);
            }
            double gx = Math.Floor((minX - MARGIN) / CELL) * CELL, gy = Math.Floor((minY - MARGIN) / CELL) * CELL;
            int nx = (int)Math.Ceiling((maxX + MARGIN - gx) / CELL) + 1, ny = (int)Math.Ceiling((maxY + MARGIN - gy) / CELL) + 1;
            var t = new CountyTerrain(gx, gy, nx, ny);

            // How far each point is from anything (only as far as the hills take to rise).
            double reach = FLAT + RAMP;
            var near = new float[nx * ny];
            for (int k = 0; k < near.Length; k++) near[k] = (float)reach;
            foreach (var s in map.spaces)
            {
                if (s.kind == SpaceKind.Highway) continue;
                int i0 = Math.Max(0, (int)Math.Floor((s.x0 - reach - gx) / CELL)), i1 = Math.Min(nx - 1, (int)Math.Ceiling((s.x0 + s.width + reach - gx) / CELL));
                int j0 = Math.Max(0, (int)Math.Floor((s.y0 - reach - gy) / CELL)), j1 = Math.Min(ny - 1, (int)Math.Ceiling((s.y0 + s.height + reach - gy) / CELL));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        double px = gx + i * CELL, py = gy + j * CELL;
                        double dx = Math.Max(0, Math.Max(s.x0 - px, px - (s.x0 + s.width)));
                        double dy = Math.Max(0, Math.Max(s.y0 - py, py - (s.y0 + s.height)));
                        float d = (float)Numbers.Hypot(dx, dy);
                        int k = j * nx + i;
                        if (d < near[k]) near[k] = d;
                    }
            }
            int r = (int)Math.Ceiling(reach / CELL) + 1;
            foreach (var hw in map.highways)
            {
                for (double s = 0; s <= hw.length; s += CELL / 2)
                {
                    var p = CountyMap.PointAt(hw, s);
                    int ci = (int)Math.Round((p.x - gx) / CELL), cj = (int)Math.Round((p.y - gy) / CELL);
                    for (int j = Math.Max(0, cj - r); j <= Math.Min(ny - 1, cj + r); j++)
                        for (int i = Math.Max(0, ci - r); i <= Math.Min(nx - 1, ci + r); i++)
                        {
                            float d = (float)Numbers.Hypot(gx + i * CELL - p.x, gy + j * CELL - p.y);
                            int k = j * nx + i;
                            if (d < near[k]) near[k] = d;
                        }
                }
            }

            // Rolling hills (two sizes of them), faded up from the flat.
            var noise = new SimplexNoise2D(new Rng("county:hills").Next);
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    double f = (near[k] - FLAT) / RAMP;
                    if (f <= 0) continue;
                    f = Math.Min(1, f);
                    f = f * f * (3 - 2 * f);
                    double px = gx + i * CELL, py = gy + j * CELL;
                    double n = 0.65 * noise.Noise(px / 140, py / 140) + 0.35 * noise.Noise(px / 55 + 31.7, py / 55 - 12.3);
                    double hill = 0.3 + 0.7 * Math.Max(0, Math.Min(1, 0.5 + 0.5 * n));
                    t.heights[k] = (float)(f * PEAK * hill);
                }
            return t;
        }
    }
}

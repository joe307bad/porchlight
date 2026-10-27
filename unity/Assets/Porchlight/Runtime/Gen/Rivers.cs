// Stage 2: rivers. Priority-flood fills pits so every cell drains to an outlet
// (lake or map edge), flow accumulation sizes the channels, and each river
// walks steepest descent from a high source until it reaches water, the map
// edge, or another river. The channel is then carved so it never runs uphill.
// A port of gen/rivers.ts.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class RiversStage : IStage
    {
        const double FILL_EPSILON = 1e-5;
        const double WIDTH_PER_SQRT_FLOW = 0.12;
        const double MIN_WIDTH = 0.8;
        const int MAX_SOURCE_TRIES = 300;

        public string Name => "rivers";

        public struct Drainage
        {
            /// <summary>Downstream neighbour per cell, or -1 for outlets.</summary>
            public int[] down;
            /// <summary>Cells draining through each cell, itself included.</summary>
            public float[] flow;
        }

        /// <summary>Priority-flood (Barnes et al.) plus steepest descent on the filled surface.</summary>
        public static Drainage ComputeDrainage(TownModel model)
        {
            int w = model.width, h = model.height;
            var hm = model.heightmap;
            double waterLevel = model.waterLevel;
            int n = w * h;
            var filled = new float[n];
            var seen = new byte[n];
            var down = new int[n];
            for (int i = 0; i < n; i++) down[i] = -1;
            var heap = new MinHeap();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                    if (edge || hm[i] < waterLevel)
                    {
                        filled[i] = hm[i];
                        seen[i] = 1;
                        heap.Push(i, hm[i]);
                    }
                }
            }
            var order = new List<int>(n);
            while (heap.Size > 0)
            {
                int c = heap.Pop();
                order.Add(c);
                int cx = c % w;
                int cy = (c - cx) / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = Grid.Index(nx, ny, w);
                    if (seen[j] != 0) continue;
                    seen[j] = 1;
                    filled[j] = (float)Math.Max(hm[j], filled[c] + FILL_EPSILON);
                    heap.Push(j, filled[j]);
                }
            }

            for (int i = 0; i < n; i++)
            {
                int x = i % w;
                int y = (i - x) / w;
                bool outlet = x == 0 || y == 0 || x == w - 1 || y == h - 1 || hm[i] < waterLevel;
                if (outlet) continue;
                int best = -1;
                float bestH = filled[i];
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int j = Grid.Index(x + dx, y + dy, w);
                    if (filled[j] < bestH)
                    {
                        bestH = filled[j];
                        best = j;
                    }
                }
                down[i] = best;
            }

            var flow = new float[n];
            for (int i = 0; i < n; i++) flow[i] = 1;
            for (int k = order.Count - 1; k >= 0; k--)
            {
                int i = order[k];
                if (down[i] >= 0) flow[down[i]] += flow[i];
            }
            return new Drainage { down = down, flow = flow };
        }

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var hm = (float[])model.heightmap.Clone();
            var drainage = ComputeDrainage(model);
            var down = drainage.down;
            var flow = drainage.flow;
            var riverAt = new int[w * h];
            for (int i = 0; i < riverAt.Length; i++) riverAt[i] = -1;

            var land = new List<int>();
            for (int i = 0; i < hm.Length; i++) if (hm[i] >= model.waterLevel) land.Add(i);
            var heights = new float[land.Count];
            for (int k = 0; k < land.Count; k++) heights[k] = hm[land[k]];
            Array.Sort(heights);
            int cutIndex = (int)Math.Floor(heights.Length * 0.7);
            double highCut = cutIndex < heights.Length ? heights[cutIndex] : 1;
            const int margin = 4;
            var candidates = new List<int>();
            foreach (int i in land)
            {
                int x = i % w;
                int y = (i - x) / w;
                if (hm[i] >= highCut && x >= margin && y >= margin && x < w - margin && y < h - margin) candidates.Add(i);
            }
            rng.Shuffle(candidates);

            var paths = new List<List<int>>();
            for (int k = 0; k < candidates.Count && k < MAX_SOURCE_TRIES && paths.Count < p.riverCount; k++)
            {
                int source = candidates[k];
                if (NearRiver(source, riverAt, w, h, 12)) continue;
                var cells = new List<int> { source };
                int c = source;
                while (down[c] >= 0)
                {
                    c = down[c];
                    cells.Add(c);
                    if (riverAt[c] >= 0) break;
                }
                if (cells.Count < p.riverMinLength) continue;
                int id = paths.Count;
                foreach (int cell in cells) if (riverAt[cell] < 0) riverAt[cell] = id;
                paths.Add(cells);
            }

            for (int pass = 0; pass < 20; pass++)
            {
                bool changed = false;
                foreach (var cells in paths)
                {
                    for (int k = 1; k < cells.Count; k++)
                    {
                        if (hm[cells[k]] > hm[cells[k - 1]])
                        {
                            hm[cells[k]] = hm[cells[k - 1]];
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }

            var riverList = new List<River>();
            for (int id = 0; id < paths.Count; id++)
            {
                var cells = paths[id];
                var centers = new List<Vec2>(cells.Count);
                foreach (int c in cells) centers.Add(new Vec2((c % w) + 0.5, c / w + 0.5));
                var points = Geo.Chaikin(centers, 2);
                var widthAt = new List<double>(points.Count);
                foreach (var pt in points)
                {
                    int cell = Grid.Index((int)Math.Floor(pt.x), (int)Math.Floor(pt.y), w);
                    widthAt.Add(Math.Min(p.riverMaxWidth, MIN_WIDTH + WIDTH_PER_SQRT_FLOW * Math.Sqrt(flow[cell])));
                }
                for (int k = 1; k < widthAt.Count; k++) widthAt[k] = Math.Max(widthAt[k], widthAt[k - 1]);
                riverList.Add(new River { id = id, points = points, widthAt = widthAt });
            }

            var next = model.With();
            next.heightmap = hm;
            next.rivers = riverList;
            next.fields = new DebugFields { flow = flow, roadCost = model.fields.roadCost };
            return next;
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            int w = model.width, h = model.height;
            var hm = model.heightmap;
            foreach (var river in model.rivers)
            {
                var pts = river.points;
                if (pts.Count < 2)
                {
                    errors.Add($"river {river.id} has fewer than 2 points");
                    continue;
                }
                double prev = double.PositiveInfinity;
                foreach (var pt in pts)
                {
                    double v = hm[Grid.Index((int)Math.Floor(pt.x), (int)Math.Floor(pt.y), w)];
                    if (v > prev + 1e-6)
                    {
                        errors.Add($"river {river.id} flows uphill near {Js.ToFixed(pt.x, 1)},{Js.ToFixed(pt.y, 1)}");
                        break;
                    }
                    prev = v;
                }
                var end = pts[pts.Count - 1];
                int ex = (int)Math.Floor(end.x), ey = (int)Math.Floor(end.y);
                bool inWater = hm[Grid.Index(ex, ey, w)] < model.waterLevel;
                bool atEdge = ex == 0 || ey == 0 || ex == w - 1 || ey == h - 1;
                bool onRiver = false;
                foreach (var o in model.rivers) if (o.id != river.id && Geo.DistToPolyline(end, o.points) < 0.75) onRiver = true;
                if (!inWater && !atEdge && !onRiver) errors.Add($"river {river.id} ends on dry land at {ex},{ey}");
                if (SelfIntersects(pts)) errors.Add($"river {river.id} crosses itself");
                for (int k = 1; k < river.widthAt.Count; k++)
                {
                    if (river.widthAt[k] < river.widthAt[k - 1])
                    {
                        errors.Add($"river {river.id} narrows downstream");
                        break;
                    }
                }
            }
            return errors;
        }

        static bool NearRiver(int cell, int[] riverAt, int w, int h, int r)
        {
            int x = cell % w;
            int y = (cell - x) / w;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && riverAt[Grid.Index(nx, ny, w)] >= 0) return true;
                }
            return false;
        }

        static bool SelfIntersects(List<Vec2> pts)
        {
            for (int i = 0; i + 1 < pts.Count; i++)
                for (int j = i + 2; j + 1 < pts.Count; j++)
                    if (SegmentsCross(pts[i], pts[i + 1], pts[j], pts[j + 1])) return true;
            return false;
        }

        static bool SegmentsCross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            double O(Vec2 p, Vec2 q, Vec2 r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
            double d1 = O(c, d, a), d2 = O(c, d, b), d3 = O(a, b, c), d4 = O(a, b, d);
            return d1 * d2 < -1e-12 && d3 * d4 < -1e-12;
        }
    }
}

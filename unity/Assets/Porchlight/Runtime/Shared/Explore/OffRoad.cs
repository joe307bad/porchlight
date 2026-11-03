// Where a car can get to across the whole town, roads or not: a half-tile grid
// of everywhere a car fits (never a building, the zoo or water), and the
// shortest way across it from one place to another (A*), pulled straight
// wherever the ground between is open. A chase's cars plan with it, so they
// cut across yards and lawns after you instead of keeping to the streets. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public sealed class DriveGrid
    {
        /// <summary>A cell's size (tiles).</summary>
        const double CELL = 0.5;
        /// <summary>How far round a cell's middle a car must fit (tiles): its half-width, and a little.</summary>
        const double ROOM = Vehicle.CAR_HALF_WIDTH + 0.08;
        /// <summary>Most cells a search looks at before it gives up (too far round: better by the streets). A few ms at most.</summary>
        const int MOST = 8000;
        /// <summary>A search leans this much toward heading straight for the goal: a way a little longer than the shortest, found much sooner.</summary>
        const double EAGER = 1.5;
        /// <summary>The way's pulled straight this many cells at a time at most.</summary>
        const int SIGHT = 40;

        readonly int w, h;
        readonly bool[] free;
        /// <summary>Which stretch of open ground each free cell is in (cells in the same one reach each other); -1 for none.</summary>
        readonly int[] part;
        // A search's working space, kept from one to the next: a cell's cost and where
        // it was reached from count only when its mark is this search's.
        readonly double[] cost;
        readonly int[] came;
        readonly int[] mark;
        int search;
        readonly Heap heap = new Heap();
        readonly List<int> cells = new List<int>();

        public DriveGrid(double width, double height, Func<double, double, bool> open)
        {
            w = (int)Math.Ceiling(width / CELL);
            h = (int)Math.Ceiling(height / CELL);
            free = new bool[w * h];
            for (int cy = 0; cy < h; cy++)
                for (int cx = 0; cx < w; cx++)
                {
                    double x = (cx + 0.5) * CELL, y = (cy + 0.5) * CELL;
                    free[cy * w + cx] = open(x, y) && open(x + ROOM, y) && open(x - ROOM, y) && open(x, y + ROOM) && open(x, y - ROOM)
                        && open(x + ROOM * 0.7, y + ROOM * 0.7) && open(x - ROOM * 0.7, y + ROOM * 0.7) && open(x + ROOM * 0.7, y - ROOM * 0.7) && open(x - ROOM * 0.7, y - ROOM * 0.7);
                }
            cost = new double[w * h];
            came = new int[w * h];
            mark = new int[w * h];
            part = new int[w * h];
            for (int i = 0; i < part.Length; i++) part[i] = -1;
            var fill = new Stack<int>();
            int parts = 0;
            for (int i = 0; i < part.Length; i++)
            {
                if (!free[i] || part[i] >= 0) continue;
                part[i] = parts;
                fill.Push(i);
                while (fill.Count > 0)
                {
                    int c = fill.Pop(), cx = c % w, cy = c / w;
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = cx + DX[k], ny = cy + DY[k];
                        if (!Step(cx, cy, nx, ny)) continue;
                        int n = ny * w + nx;
                        if (part[n] >= 0) continue;
                        part[n] = parts;
                        fill.Push(n);
                    }
                }
                parts++;
            }
        }

        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>A car can go from one cell to the next one over (diagonally only past two free cells: no squeezing between corners).</summary>
        bool Step(int cx, int cy, int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= w || ny >= h || !free[ny * w + nx]) return false;
            return nx == cx || ny == cy || (free[cy * w + nx] && free[ny * w + cx]);
        }

        /// <summary>How far (cells) from one cell to another, for a search to go by: as far as with nothing in the way, leaned on.</summary>
        double Guess(int a, int b)
        {
            int dx = Math.Abs(a % w - b % w), dy = Math.Abs(a / w - b / w);
            return EAGER * (Math.Max(dx, dy) + 0.4142 * Math.Min(dx, dy));
        }

        public bool Free(Vec2 p)
        {
            int cx = (int)Math.Floor(p.x / CELL), cy = (int)Math.Floor(p.y / CELL);
            return cx >= 0 && cy >= 0 && cx < w && cy < h && free[cy * w + cx];
        }

        Vec2 Middle(int c) => new Vec2((c % w + 0.5) * CELL, (c / w + 0.5) * CELL);

        /// <summary>The free cell nearest `p` (within `within` cells), in stretch `inPart` if it's 0 or more; or -1.</summary>
        int Nearest(Vec2 p, int within, int inPart = -1)
        {
            int px = (int)Math.Floor(p.x / CELL), py = (int)Math.Floor(p.y / CELL);
            for (int r = 0; r <= within; r++)
            {
                int best = -1;
                double bestD = double.PositiveInfinity;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int cx = px + dx, cy = py + dy;
                        if (cx < 0 || cy < 0 || cx >= w || cy >= h || !free[cy * w + cx]) continue;
                        if (inPart >= 0 && part[cy * w + cx] != inPart) continue;
                        double d = Geo.Dist(Middle(cy * w + cx), p);
                        if (d < bestD)
                        {
                            bestD = d;
                            best = cy * w + cx;
                        }
                    }
                if (best >= 0) return best;
            }
            return -1;
        }

        /// <summary>
        /// The shortest way a car can drive from `from` to `to` (or the nearest it can get to it,
        /// within a dozen tiles), as few straight runs as the ground allows; null if there's none.
        /// </summary>
        public List<Vec2> Route(Vec2 from, Vec2 to)
        {
            int start = Nearest(from, 8);
            if (start < 0) return null;
            // Somewhere it can't get to from here (the zoo, over a river) is never searched for:
            // it makes for the nearest it can.
            int goal = Nearest(to, 24, part[start]);
            if (goal < 0) return null;
            search++;
            heap.Clear();
            cost[start] = 0;
            came[start] = -1;
            mark[start] = search;
            heap.Push(start, Guess(start, goal));
            bool reached = false;
            for (int looked = 0; heap.Count > 0 && looked < MOST; looked++)
            {
                var (c, key) = heap.Pop();
                if (c == goal)
                {
                    reached = true;
                    break;
                }
                double here = cost[c];
                int cx = c % w, cy = c / w;
                // Stale (it's been reached more cheaply since it was put in).
                if (key - Guess(c, goal) > here + 1e-9) continue;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + DX[k], ny = cy + DY[k];
                    if (!Step(cx, cy, nx, ny)) continue;
                    int n = ny * w + nx;
                    double next = here + (k < 4 ? 1 : 1.4142);
                    if (mark[n] == search && cost[n] <= next) continue;
                    mark[n] = search;
                    cost[n] = next;
                    came[n] = c;
                    heap.Push(n, next + Guess(n, goal));
                }
            }
            if (!reached) return null;
            cells.Clear();
            for (int c = goal; c >= 0; c = came[c]) cells.Add(c);
            cells.Reverse();
            // Pulled straight: from each point, on as far as it can see along the way.
            var way = new List<Vec2> { from };
            var p = from;
            int at = 0;
            while (at < cells.Count - 1)
            {
                int far = at + 1;
                while (far + 1 < cells.Count && far - at < SIGHT && Clear(p, Middle(cells[far + 1]))) far++;
                p = Middle(cells[far]);
                way.Add(p);
                at = far;
            }
            if (Clear(p, to)) way.Add(to);
            return way;
        }

        /// <summary>Free cells all the way from a to b.</summary>
        public bool Clear(Vec2 a, Vec2 b)
        {
            double d = Geo.Dist(a, b);
            for (double s = 0; s <= d; s += CELL * 0.5)
                if (!Free(Geo.Lerp(a, b, d < 1e-9 ? 0 : s / d))) return false;
            return Free(b);
        }

        /// <summary>A binary heap of cells by priority, least first.</summary>
        sealed class Heap
        {
            readonly List<(int cell, double key)> items = new List<(int, double)>();
            public int Count => items.Count;

            public void Push(int cell, double key)
            {
                items.Add((cell, key));
                int i = items.Count - 1;
                while (i > 0)
                {
                    int up = (i - 1) / 2;
                    if (items[up].key <= items[i].key) break;
                    (items[up], items[i]) = (items[i], items[up]);
                    i = up;
                }
            }

            public void Clear() => items.Clear();

            public (int cell, double key) Pop()
            {
                var top = items[0];
                items[0] = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, least = i;
                    if (l < items.Count && items[l].key < items[least].key) least = l;
                    if (r < items.Count && items[r].key < items[least].key) least = r;
                    if (least == i) break;
                    (items[least], items[i]) = (items[i], items[least]);
                    i = least;
                }
                return top;
            }
        }
    }
}

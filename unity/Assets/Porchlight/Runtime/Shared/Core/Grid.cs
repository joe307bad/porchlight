// Row-major grid helpers, a binary min-heap, and grid A*.

using System;
using System.Collections.Generic;

namespace Porchlight.Core
{
    public static class Grid
    {
        public static int Index(int x, int y, int width) => y * width + x;

        public static bool InBounds(int x, int y, int width, int height) => x >= 0 && y >= 0 && x < width && y < height;

        /// <summary>N, E, S, W — the order autotile bitmasks use (bit 0 = N).</summary>
        public static readonly (int dx, int dy)[] DIRS4 = { (0, -1), (1, 0), (0, 1), (-1, 0) };

        public static readonly (int dx, int dy)[] DIRS8 = { (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1) };

        /// <summary>Every cell a segment passes through (Amanatides &amp; Woo), in order from a to b.</summary>
        public static List<(int x, int y)> CellsOnSegment(double ax, double ay, double bx, double by)
        {
            int x = (int)Math.Floor(ax);
            int y = (int)Math.Floor(ay);
            int ex = (int)Math.Floor(bx);
            int ey = (int)Math.Floor(by);
            double dx = bx - ax;
            double dy = by - ay;
            int stepX = Math.Sign(dx);
            int stepY = Math.Sign(dy);
            double tDeltaX = stepX != 0 ? Math.Abs(1 / dx) : double.PositiveInfinity;
            double tDeltaY = stepY != 0 ? Math.Abs(1 / dy) : double.PositiveInfinity;
            double tMaxX = stepX > 0 ? (x + 1 - ax) * tDeltaX : stepX < 0 ? (ax - x) * tDeltaX : double.PositiveInfinity;
            double tMaxY = stepY > 0 ? (y + 1 - ay) * tDeltaY : stepY < 0 ? (ay - y) * tDeltaY : double.PositiveInfinity;
            var o = new List<(int, int)> { (x, y) };
            for (int guard = 0; (x != ex || y != ey) && guard < 100000; guard++)
            {
                if (tMaxX < tMaxY)
                {
                    x += stepX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    y += stepY;
                    tMaxY += tDeltaY;
                }
                o.Add((x, y));
            }
            return o;
        }
    }

    /// <summary>Binary min-heap of integer items keyed by a priority. Ties pop in insertion order.</summary>
    public sealed class MinHeap
    {
        readonly List<int> items = new List<int>();
        readonly List<double> prio = new List<double>();
        readonly List<int> order = new List<int>();
        int counter;

        public int Size => items.Count;

        public void Push(int item, double priority)
        {
            items.Add(item);
            prio.Add(priority);
            order.Add(counter++);
            Up(items.Count - 1);
        }

        public int Pop()
        {
            int top = items[0];
            int last = items.Count - 1;
            int lastItem = items[last];
            double lastPrio = prio[last];
            int lastOrder = order[last];
            items.RemoveAt(last);
            prio.RemoveAt(last);
            order.RemoveAt(last);
            if (items.Count > 0)
            {
                items[0] = lastItem;
                prio[0] = lastPrio;
                order[0] = lastOrder;
                Down(0);
            }
            return top;
        }

        bool Less(int a, int b) => prio[a] < prio[b] || (prio[a] == prio[b] && order[a] < order[b]);

        void Swap(int a, int b)
        {
            (items[a], items[b]) = (items[b], items[a]);
            (prio[a], prio[b]) = (prio[b], prio[a]);
            (order[a], order[b]) = (order[b], order[a]);
        }

        void Up(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Less(i, parent)) break;
                Swap(i, parent);
                i = parent;
            }
        }

        void Down(int i)
        {
            int n = items.Count;
            for (;;)
            {
                int l = i * 2 + 1;
                int r = l + 1;
                int best = i;
                if (l < n && Less(l, best)) best = l;
                if (r < n && Less(r, best)) best = r;
                if (best == i) return;
                Swap(i, best);
                i = best;
            }
        }
    }

    public interface IGridSearch
    {
        int Width { get; }
        int Height { get; }
        double StepCost(int from, int to);
        bool IsGoal(int cell);
        double Heuristic(int cell);
    }

    public sealed class GridSearch : IGridSearch
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public Func<int, int, double> Step;
        public Func<int, bool> Goal;
        public Func<int, double> Estimate;
        public double TurnCost;
        public double StepCost(int from, int to) => Step(from, to);
        public bool IsGoal(int cell) => Goal(cell);
        public double Heuristic(int cell) => Estimate(cell);
    }

    public static class AStar
    {
        /// <summary>Cheapest 8-connected path from start to any goal cell, start and goal included, or null.</summary>
        public static List<int> FindPath(int start, GridSearch search)
        {
            int width = search.Width, height = search.Height;
            int n = width * height;
            var g = new double[n];
            for (int i = 0; i < n; i++) g[i] = double.PositiveInfinity;
            var came = new int[n];
            for (int i = 0; i < n; i++) came[i] = -1;
            var closed = new byte[n];
            var open = new MinHeap();
            g[start] = 0;
            open.Push(start, search.Heuristic(start));
            while (open.Size > 0)
            {
                int cur = open.Pop();
                if (closed[cur] != 0) continue;
                closed[cur] = 1;
                if (search.IsGoal(cur))
                {
                    var path = new List<int> { cur };
                    for (int c = came[cur]; c != -1; c = came[c]) path.Add(c);
                    path.Reverse();
                    return path;
                }
                int cx = cur % width;
                int cy = (cur - cx) / width;
                foreach (var (dx, dy) in Grid.DIRS8)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int next = ny * width + nx;
                    if (closed[next] != 0) continue;
                    double step = search.StepCost(cur, next);
                    if (!Numbers.IsFinite(step)) continue;
                    double cost = g[cur] + step;
                    if (cost < g[next])
                    {
                        g[next] = cost;
                        came[next] = cur;
                        open.Push(next, cost + search.Heuristic(next));
                    }
                }
            }
            return null;
        }

        /// <summary>Octile distance between two cells.</summary>
        public static double Octile(int a, int b, int width)
        {
            double dx = Math.Abs((a % width) - (b % width));
            double dy = Math.Abs(a / width - b / width);
            return Math.Max(dx, dy) + (Math.Sqrt(2) - 1) * Math.Min(dx, dy);
        }

        static readonly int[] DX4 = { 0, 1, 0, -1 };
        static readonly int[] DY4 = { -1, 0, 1, 0 };

        /// <summary>A* over (cell, heading) with 4-connected moves and a penalty per turn.</summary>
        public static List<int> FindGridPath(int start, GridSearch search)
        {
            int width = search.Width, height = search.Height;
            int n = width * height;
            var g = new double[n * 5];
            for (int i = 0; i < g.Length; i++) g[i] = double.PositiveInfinity;
            var came = new int[n * 5];
            for (int i = 0; i < came.Length; i++) came[i] = -1;
            var closed = new byte[n * 5];
            var open = new MinHeap();
            int s0 = start * 5 + 4;
            g[s0] = 0;
            open.Push(s0, search.Heuristic(start));
            while (open.Size > 0)
            {
                int state = open.Pop();
                if (closed[state] != 0) continue;
                closed[state] = 1;
                int cell = state / 5;
                int heading = state % 5;
                if (search.IsGoal(cell))
                {
                    var path = new List<int> { cell };
                    for (int s = came[state]; s != -1; s = came[s]) path.Add(s / 5);
                    path.Reverse();
                    return path;
                }
                int cx = cell % width;
                int cy = (cell - cx) / width;
                for (int d = 0; d < 4; d++)
                {
                    if (heading != 4 && d == (heading + 2) % 4) continue;
                    int nx = cx + DX4[d], ny = cy + DY4[d];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int next = ny * width + nx;
                    int ns = next * 5 + d;
                    if (closed[ns] != 0) continue;
                    double step = search.StepCost(cell, next);
                    if (!Numbers.IsFinite(step)) continue;
                    double cost = g[state] + step + (heading != 4 && heading != d ? search.TurnCost : 0);
                    if (cost < g[ns])
                    {
                        g[ns] = cost;
                        came[ns] = state;
                        open.Push(ns, cost + search.Heuristic(next));
                    }
                }
            }
            return null;
        }

        public static double Manhattan(int a, int b, int width) =>
            Math.Abs((a % width) - (b % width)) + Math.Abs(a / width - b / width);
    }
}

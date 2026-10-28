// Grids derived from the vector layers, and the road cost field. Every stage that needs to know where water,
// roads or steep ground are uses these, and so does rasterize, so blocks, lots
// and buildings always agree with the final tile grid.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class RoadRaster
    {
        /// <summary>ROAD_* code per cell.</summary>
        public byte[] kind;
        /// <summary>Id of the road edge that owns each cell, or -1.</summary>
        public int[] edgeId;
    }

    public sealed class RoadTerrain
    {
        public byte[] water;
        public float[] slope;
        /// <summary>Cost of entering each cell; Infinity where blocked.</summary>
        public float[] cost;
    }

    public static class Raster
    {
        public const byte ROAD_NONE = 0;
        public const byte ROAD_MAIN = 1;
        public const byte ROAD_STREET = 2;
        public const byte ROAD_PATH = 3;

        static byte KindCode(RoadKind k) => k == RoadKind.Main ? ROAD_MAIN : k == RoadKind.Street ? ROAD_STREET : ROAD_PATH;

        /// <summary>Lower wins when roads of different kinds overlap a tile.</summary>
        static readonly int[] KIND_RANK = { 99, 0, 1, 2 };

        /// <summary>Width of the sidewalk the explore view draws along each side of a road or street (tiles).</summary>
        public const double SIDEWALK_WIDTH = 0.45;

        /// <summary>Width of the parking lane between each side of a road or street and its sidewalk (tiles): room for a parked car.</summary>
        public const double PARKING_WIDTH = 0.5;

        /// <summary>How far a road's drawn surface reaches from its centreline, parking lanes and sidewalks included.</summary>
        public static double RoadReach(RoadKind kind, GenParams p) =>
            RoadDrawWidth(kind, p) / 2 + (kind == RoadKind.Path ? 0 : PARKING_WIDTH + SIDEWALK_WIDTH);

        public static double RoadDrawWidth(RoadKind kind, GenParams p)
        {
            if (kind == RoadKind.Main) return p.mainRoadWidth;
            if (kind == RoadKind.Street) return p.streetWidth;
            return 0.4;
        }

        /// <summary>Tiles whose centre is closer than this to a road's centreline belong to the road.</summary>
        public static double RoadHalfWidth(RoadKind kind, GenParams p) =>
            Math.Max(0.75, RoadDrawWidth(kind, p) / 2 + (kind == RoadKind.Path ? 0 : PARKING_WIDTH) + 0.5);

        public static byte[] LakeMask(TownModel model)
        {
            var o = new byte[model.width * model.height];
            for (int i = 0; i < o.Length; i++) o[i] = model.heightmap[i] < model.waterLevel ? (byte)1 : (byte)0;
            return o;
        }

        public static double RiverHalfWidth(double width) => Math.Max(0.75, width / 2);

        public static byte[] RiverMask(TownModel model)
        {
            int w = model.width, h = model.height;
            var o = new byte[w * h];
            foreach (var river in model.rivers)
            {
                var pts = river.points;
                for (int s = 0; s + 1 < pts.Count; s++)
                {
                    var a = pts[s];
                    var b = pts[s + 1];
                    double r = Math.Max(RiverHalfWidth(river.widthAt[s]), RiverHalfWidth(river.widthAt[s + 1]));
                    int x0 = (int)Math.Max(0, Math.Floor(Math.Min(a.x, b.x) - r));
                    int x1 = (int)Math.Min(w - 1, Math.Ceiling(Math.Max(a.x, b.x) + r));
                    int y0 = (int)Math.Max(0, Math.Floor(Math.Min(a.y, b.y) - r));
                    int y1 = (int)Math.Min(h - 1, Math.Ceiling(Math.Max(a.y, b.y) + r));
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            var c = Geo.ClosestOnSegment(new Vec2(x + 0.5, y + 0.5), a, b);
                            double hw = RiverHalfWidth(river.widthAt[s] + (river.widthAt[s + 1] - river.widthAt[s]) * c.t);
                            if (Numbers.Hypot(c.point.x - x - 0.5, c.point.y - y - 0.5) < hw) o[Grid.Index(x, y, w)] = 1;
                        }
                    }
                }
            }
            return o;
        }

        public static byte[] WaterMask(TownModel model)
        {
            var o = LakeMask(model);
            var rivers = RiverMask(model);
            for (int i = 0; i < o.Length; i++) o[i] |= rivers[i];
            return o;
        }

        /// <summary>Largest height change to a dry 4-neighbour; water cells and banks facing water ignore the water.</summary>
        public static float[] SlopeField(TownModel model, byte[] water)
        {
            int w = model.width, h = model.height;
            var hm = model.heightmap;
            var o = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    if (water[i] != 0) continue;
                    double s = 0;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = Grid.Index(nx, ny, w);
                        if (water[j] == 0) s = Math.Max(s, Math.Abs((double)hm[i] - hm[j]));
                    }
                    o[i] = (float)s;
                }
            }
            return o;
        }

        /// <summary>Max of a field over each cell's 3x3 neighbourhood.</summary>
        public static float[] Dilate(float[] field, int w, int h)
        {
            var o = new float[field.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float m = float.NegativeInfinity;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h) m = Math.Max(m, field[ny * w + nx]);
                        }
                    o[y * w + x] = m;
                }
            }
            return o;
        }

        public static RoadRaster RoadRaster(TownModel model, IReadOnlyList<RoadEdge> edges = null)
        {
            edges ??= model.roads.edges;
            int w = model.width, h = model.height;
            var p = model.@params;
            var kind = new byte[w * h];
            var edgeId = new int[w * h];
            var best = new float[w * h];
            for (int i = 0; i < edgeId.Length; i++)
            {
                edgeId[i] = -1;
                best[i] = float.PositiveInfinity;
            }
            foreach (var edge in edges)
            {
                byte code = KindCode(edge.kind);
                double hw = RoadHalfWidth(edge.kind, p);
                var pts = edge.points;
                for (int s = 0; s + 1 < pts.Count; s++)
                {
                    var a = pts[s];
                    var b = pts[s + 1];
                    int x0 = (int)Math.Max(0, Math.Floor(Math.Min(a.x, b.x) - hw));
                    int x1 = (int)Math.Min(w - 1, Math.Ceiling(Math.Max(a.x, b.x) + hw));
                    int y0 = (int)Math.Max(0, Math.Floor(Math.Min(a.y, b.y) - hw));
                    int y1 = (int)Math.Min(h - 1, Math.Ceiling(Math.Max(a.y, b.y) + hw));
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            double d = Geo.DistToSegment(new Vec2(x + 0.5, y + 0.5), a, b);
                            if (d >= hw) continue;
                            int i = Grid.Index(x, y, w);
                            byte cur = kind[i];
                            bool better = cur == ROAD_NONE || KIND_RANK[code] < KIND_RANK[cur] || (code == cur && d < best[i]);
                            if (better)
                            {
                                kind[i] = code;
                                edgeId[i] = edge.id;
                                best[i] = (float)d;
                            }
                        }
                    }
                }
            }
            return new RoadRaster { kind = kind, edgeId = edgeId };
        }

        /// <summary>Ground too steep to build or walk on.</summary>
        public static byte[] RockMask(float[] slope, GenParams p)
        {
            var o = new byte[slope.Length];
            for (int i = 0; i < slope.Length; i++) o[i] = slope[i] > p.rockSlope ? (byte)1 : (byte)0;
            return o;
        }

        /// <summary>Multi-source BFS distance (4-connected steps) from every set cell.</summary>
        public static float[] DistanceFrom(byte[] mask, int w, int h)
        {
            var d = new float[w * h];
            for (int i = 0; i < d.Length; i++) d[i] = float.PositiveInfinity;
            var queue = new List<int>();
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0)
                {
                    d[i] = 0;
                    queue.Add(i);
                }
            }
            for (int head = 0; head < queue.Count; head++)
            {
                int i = queue[head];
                int x = i % w;
                int y = (i - x) / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (float.IsPositiveInfinity(d[j]))
                    {
                        d[j] = d[i] + 1;
                        queue.Add(j);
                    }
                }
            }
            return d;
        }

        // Road costs

        const double SLOPE_PENALTY = 1.5;
        /// <summary>Roads shouldn't run along the map border; tiles this close to it cost extra.</summary>
        const int BORDER_BAND = 6;
        const double BORDER_PENALTY = 2;

        public static RoadTerrain RoadTerrain(TownModel model)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var water = WaterMask(model);
            var slope = SlopeField(model, water);
            var near = Dilate(slope, w, h);
            var cost = new float[w * h];
            for (int i = 0; i < cost.Length; i++)
            {
                int x = i % w;
                int y = (i - x) / w;
                double border = Math.Max(0, BORDER_BAND - Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y))) * BORDER_PENALTY;
                if (water[i] != 0) cost[i] = (float)(1 + p.bridgeCost + border);
                else if (near[i] > p.maxRoadSlope) cost[i] = float.PositiveInfinity;
                else
                {
                    double k = near[i] / p.maxRoadSlope;
                    cost[i] = (float)(1 + SLOPE_PENALTY * (k * k) + border);
                }
            }
            return new RoadTerrain { water = water, slope = slope, cost = cost };
        }

        /// <summary>True if every tile under segment ab is water or gentle enough for a road.</summary>
        public static Func<Vec2, Vec2, bool> RoadSegmentOk(RoadTerrain t, TownModel model)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            return (a, b) =>
            {
                foreach (var (x, y) in Grid.CellsOnSegment(a.x, a.y, b.x, b.y))
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) return false;
                    int i = y * w + x;
                    if (!(t.water[i] == 1 || t.slope[i] <= p.maxRoadSlope)) return false;
                }
                return true;
            };
        }
    }
}

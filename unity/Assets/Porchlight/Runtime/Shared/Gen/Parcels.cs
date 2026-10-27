// Shared helpers for blocks, lots and buildings: which tiles can be built on,
// which tiles a polygon covers, and which side of a parcel faces a road. Also
// stage 6, blocks.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class Ground
    {
        public byte[] water;
        public RoadRaster road;
        /// <summary>Dry, gentle, off-road tiles inside the town radius.</summary>
        public byte[] free;
    }

    public static class Parcels
    {
        /// <summary>Same order as DIRS4.</summary>
        public static readonly Facing[] FACINGS = { Facing.N, Facing.E, Facing.S, Facing.W };

        public static Ground Ground(TownModel model)
        {
            int w = model.width, h = model.height;
            var C = model.center;
            double R = model.townRadius;
            var water = Raster.WaterMask(model);
            var road = Raster.RoadRaster(model);
            var rock = Raster.RockMask(Raster.SlopeField(model, water), model.@params);
            var free = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    bool inside = Numbers.Hypot(x + 0.5 - C.x, y + 0.5 - C.y) <= R;
                    free[i] = inside && water[i] == 0 && rock[i] == 0 && road.kind[i] == 0 ? (byte)1 : (byte)0;
                }
            }
            return new Ground { water = water, road = road, free = free };
        }

        /// <summary>A tile a building can face: road on dry land.</summary>
        public static bool IsStreetTile(Ground g, int i) => g.road.kind[i] != 0 && g.water[i] == 0;

        /// <summary>Road edges whose street tiles touch one of these tiles, with how many touches each (first-seen order).</summary>
        public static List<KeyValuePair<int, int>> AdjacentRoadEdges(IReadOnlyList<int> tiles, Ground g, int w, int h)
        {
            var votes = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (int t in tiles)
            {
                int x = t % w;
                int y = (t - x) / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int n = Grid.Index(nx, ny, w);
                    if (!IsStreetTile(g, n)) continue;
                    int e = g.road.edgeId[n];
                    if (votes.TryGetValue(e, out int v)) votes[e] = v + 1;
                    else
                    {
                        votes[e] = 1;
                        order.Add(e);
                    }
                }
            }
            var o = new List<KeyValuePair<int, int>>();
            foreach (int e in order) o.Add(new KeyValuePair<int, int>(e, votes[e]));
            return o;
        }

        /// <summary>Tiles whose centres fall inside the polygon and outside its holes.</summary>
        public static List<int> TilesIn(IReadOnlyList<Vec2> polygon, IReadOnlyList<List<Vec2>> holes, int w, int h)
        {
            var b = Geo.PolygonBounds(polygon);
            var o = new List<int>();
            int yEnd = (int)Math.Min(h, Math.Ceiling(b.y1));
            int xEnd = (int)Math.Min(w, Math.Ceiling(b.x1));
            for (int y = (int)Math.Max(0, Math.Floor(b.y0)); y < yEnd; y++)
            {
                for (int x = (int)Math.Max(0, Math.Floor(b.x0)); x < xEnd; x++)
                {
                    var c = new Vec2(x + 0.5, y + 0.5);
                    if (!Geo.PointInPolygon(c, polygon)) continue;
                    bool inHole = false;
                    if (holes != null) foreach (var hole in holes) if (Geo.PointInPolygon(c, hole)) { inHole = true; break; }
                    if (!inHole) o.Add(Grid.Index(x, y, w));
                }
            }
            return o;
        }

        /// <summary>For each facing (N, E, S, W): how many of these tiles have a street tile right next to them on that side.</summary>
        public static int[] FrontageCounts(IReadOnlyList<int> tiles, Ground g, int w, int h)
        {
            var counts = new int[4];
            foreach (int i in tiles)
            {
                int x = i % w;
                int y = (i - x) / w;
                for (int d = 0; d < 4; d++)
                {
                    var (dx, dy) = Grid.DIRS4[d];
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && IsStreetTile(g, Grid.Index(nx, ny, w))) counts[d]++;
                }
            }
            return counts;
        }

        /// <summary>4-connected components of the given tiles.</summary>
        public static List<List<int>> Components(IReadOnlyList<int> tiles, int w)
        {
            var set = new HashSet<int>(tiles);
            var seen = new HashSet<int>();
            var o = new List<List<int>>();
            foreach (int t in tiles)
            {
                if (seen.Contains(t)) continue;
                var comp = new List<int> { t };
                seen.Add(t);
                for (int head = 0; head < comp.Count; head++)
                {
                    int c = comp[head];
                    int x = c % w;
                    int n0 = c - w, n1 = c + w, n2 = x > 0 ? c - 1 : -1, n3 = x < w - 1 ? c + 1 : -1;
                    foreach (int n in new[] { n0, n1, n2, n3 })
                    {
                        if (n >= 0 && set.Contains(n) && !seen.Contains(n))
                        {
                            seen.Add(n);
                            comp.Add(n);
                        }
                    }
                }
                o.Add(comp);
            }
            return o;
        }

        /// <summary>Outline rings of a tile set, in world coordinates (outer ring first).</summary>
        public static List<List<Vec2>> Outline(IReadOnlyList<int> tiles, int w)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (int t in tiles)
            {
                int x = t % w;
                int y = (t - x) / w;
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
            }
            int lw = x1 - x0 + 1;
            int lh = y1 - y0 + 1;
            var mask = new byte[lw * lh];
            foreach (int t in tiles)
            {
                int x = t % w;
                int y = (t - x) / w;
                mask[(y - y0) * lw + (x - x0)] = 1;
            }
            var rings = Geo.TraceCellOutlines(mask, lw, lh);
            var o = new List<List<Vec2>>();
            foreach (var ring in rings)
            {
                var r = new List<Vec2>(ring.Count);
                foreach (var p in ring) r.Add(new Vec2(p.x + x0, p.y + y0));
                o.Add(r);
            }
            return o;
        }
    }

    /// <summary>
    /// Stage 6: blocks. Roads (at their full raster width), water, rock and the
    /// town radius cut the land into faces; each connected face of buildable tiles is a block.
    /// </summary>
    public sealed class BlocksStage : IStage
    {
        public string Name => "blocks";

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width;
            var free = Parcels.Ground(model).free;
            var tiles = new List<int>();
            for (int i = 0; i < free.Length; i++) if (free[i] != 0) tiles.Add(i);
            var list = new List<Block>();
            foreach (var comp in Parcels.Components(tiles, w))
            {
                if (comp.Count < model.@params.lotMinSize) continue;
                var rings = Parcels.Outline(comp, w);
                if (rings.Count == 0) continue;
                list.Add(new Block { id = list.Count, polygon = rings[0], holes = rings.GetRange(1, rings.Count - 1) });
            }
            var next = model.With();
            next.blocks = list;
            return next;
        }

        public List<string> Check(TownModel model)
        {
            int w = model.width, h = model.height;
            var free = Parcels.Ground(model).free;
            var owner = new int[w * h];
            for (int i = 0; i < owner.Length; i++) owner[i] = -1;
            foreach (var b in model.blocks)
            {
                foreach (int t in Parcels.TilesIn(b.polygon, b.holes, w, h))
                {
                    if (free[t] == 0) return new List<string> { $"block {b.id} covers road, water or rock at {t % w},{t / w}" };
                    if (owner[t] >= 0) return new List<string> { $"blocks {owner[t]} and {b.id} overlap" };
                    owner[t] = b.id;
                }
            }
            return new List<string>();
        }
    }
}

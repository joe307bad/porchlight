// Stage 7a: lots. Each block is split recursively across its longer side until
// the pieces are no bigger than lotMaxSize. Pieces smaller than lotMinSize, or
// with no road along any side, are dropped. The lots nearest the center become
// the plaza, with a well in the middle.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class LotsStage : IStage
    {
        const double SPLIT_JITTER = 0.15;
        const int PLAZA_LOTS = 3;
        const double PLAZA_REACH = 8;

        public string Name => "lots";

        static void Split(List<int> tiles, int w, double maxSize, Rng rng, List<List<int>> o)
        {
            if (tiles.Count <= maxSize)
            {
                o.Add(tiles);
                return;
            }
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
            bool alongX = x1 - x0 >= y1 - y0;
            int lo = alongX ? x0 : y0;
            int hi = alongX ? x1 : y1;
            if (hi == lo)
            {
                o.Add(tiles);
                return;
            }
            double cut = Math.Max(lo + 1, Math.Min(hi, Numbers.Round(lo + (hi - lo + 1) * (0.5 + rng.Range(-SPLIT_JITTER, SPLIT_JITTER)))));
            var a = new List<int>();
            var b = new List<int>();
            foreach (int t in tiles)
            {
                int x = t % w;
                int v = alongX ? x : (t - x) / w;
                (v < cut ? a : b).Add(t);
            }
            foreach (var part in new[] { a, b })
                foreach (var comp in Parcels.Components(part, w))
                    Split(comp, w, maxSize, rng, o);
        }

        static Vec2 TileCenter(int t, int w) => new Vec2((t % w) + 0.5, t / w + 0.5);

        static double Dist2(Vec2 a, Vec2 b)
        {
            double dx = a.x - b.x, dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var g = Parcels.Ground(model);
            var list = new List<Lot>();
            foreach (var block in model.blocks)
            {
                var tiles = new List<int>();
                foreach (int t in Parcels.TilesIn(block.polygon, block.holes, w, h)) if (g.free[t] != 0) tiles.Add(t);
                var pieces = new List<List<int>>();
                foreach (var comp in Parcels.Components(tiles, w)) Split(comp, w, p.lotMaxSize, rng, pieces);
                foreach (var piece in pieces)
                {
                    if (piece.Count < p.lotMinSize) continue;
                    var counts = Parcels.FrontageCounts(piece, g, w, h);
                    if (counts[0] == 0 && counts[1] == 0 && counts[2] == 0 && counts[3] == 0) continue;
                    var rings = Parcels.Outline(piece, w);
                    if (rings.Count != 1) continue;
                    list.Add(new Lot { id = list.Count, blockId = block.id, polygon = rings[0], frontageEdgeId = FrontageEdge(piece, g, w, h) });
                }
            }

            var landmarks = new List<Landmark>();
            if (list.Count > 0)
            {
                Vec2 Centroid(Lot l) => Geo.PolygonCentroid(l.polygon);
                var byDistance = new List<Lot>(list);
                Numbers.StableSort(byDistance, (a, b) => Dist2(Centroid(a), model.center) - Dist2(Centroid(b), model.center));
                var first = byDistance[0];
                var plaza = new List<Lot> { first };
                for (int k = 1; k < byDistance.Count; k++)
                {
                    var l = byDistance[k];
                    if (l.blockId == first.blockId && Math.Sqrt(Dist2(Centroid(l), Centroid(first))) < PLAZA_REACH) plaza.Add(l);
                }
                if (plaza.Count > PLAZA_LOTS) plaza.RemoveRange(PLAZA_LOTS, plaza.Count - PLAZA_LOTS);
                double ax = 0, ay = 0, area = 0;
                foreach (var l in plaza)
                {
                    double a = Math.Abs(Geo.PolygonArea(l.polygon));
                    var c = Centroid(l);
                    ax += c.x * a;
                    ay += c.y * a;
                    area += a;
                }
                var at = new Vec2(ax / area, ay / area);
                var lotIds = new List<int>();
                foreach (var l in plaza) lotIds.Add(l.id);
                landmarks.Add(new Landmark { id = 0, kind = LandmarkKind.Plaza, at = at, lotIds = lotIds });
                var plazaTiles = new List<int>();
                foreach (var l in plaza) plazaTiles.AddRange(Parcels.TilesIn(l.polygon, null, w, h));
                if (plazaTiles.Count > 0)
                {
                    int well = plazaTiles[0];
                    foreach (int t in plazaTiles) if (Dist2(TileCenter(t, w), at) < Dist2(TileCenter(well, w), at)) well = t;
                    landmarks.Add(new Landmark { id = 1, kind = LandmarkKind.Well, at = TileCenter(well, w), lotIds = new List<int>() });
                }
            }
            var next = model.With();
            next.lots = list;
            next.landmarks = landmarks;
            return next;
        }

        public List<string> Check(TownModel model)
        {
            int w = model.width, h = model.height;
            var g = Parcels.Ground(model);
            foreach (var lot in model.lots)
            {
                var tiles = Parcels.TilesIn(lot.polygon, null, w, h);
                if (tiles.Count == 0) return new List<string> { $"lot {lot.id} covers no tiles" };
                foreach (int t in tiles) if (g.free[t] == 0) return new List<string> { $"lot {lot.id} covers road, water or rock" };
                var touching = Parcels.AdjacentRoadEdges(tiles, g, w, h);
                if (touching.Count == 0) return new List<string> { $"lot {lot.id} doesn't touch a road" };
                bool has = false;
                foreach (var kv in touching) if (kv.Key == lot.frontageEdgeId) has = true;
                if (!has) return new List<string> { $"lot {lot.id}'s frontage road {lot.frontageEdgeId} isn't beside it" };
            }
            return new List<string>();
        }

        /// <summary>The road edge most of the lot's road-side tiles look onto.</summary>
        static int FrontageEdge(List<int> tiles, Ground g, int w, int h)
        {
            int best = -1;
            int bestVotes = 0;
            foreach (var kv in Parcels.AdjacentRoadEdges(tiles, g, w, h))
            {
                int edge = kv.Key, v = kv.Value;
                if (v > bestVotes || (v == bestVotes && edge < best))
                {
                    best = edge;
                    bestVotes = v;
                }
            }
            return best;
        }
    }
}

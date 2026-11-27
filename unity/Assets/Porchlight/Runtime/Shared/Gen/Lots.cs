// Stage 7a: lots. Each block is split recursively across its longer side until
// the pieces are no bigger than lotMaxSize (less, nearer the middle: its
// transect zone's lot size, TownPlan.Rules). Pieces smaller than lotMinSize, or
// with no road along any side, are dropped. The lots nearest the center (not in
// a park) become the plaza, with a well in the middle. A sliver (no square of
// MIN_WIDTH tiles fits in it) goes to the lot beside it, so no lot is too narrow
// to build on.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class LotsStage : IStepwiseStage
    {
        const double SPLIT_JITTER = 0.15;
        /// <summary>The narrowest a lot can be (tiles): a cut never leaves a strip narrower, and a piece that's no wider joins the next.</summary>
        public const int MIN_WIDTH = 2;
        /// <summary>The most a lot made of slivers joined together can be, against the zone's largest lot.</summary>
        const double MERGE_MOST = 2.5;
        const int PLAZA_LOTS = 3;
        const double PLAZA_REACH = 8;

        public string Name => "lots";

        const int PIECES_A_STEP = 48;

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
            // Wide enough for two lots of MIN_WIDTH: neither side of the cut is narrower.
            if (hi - lo + 1 >= 2 * MIN_WIDTH) cut = Math.Max(lo + MIN_WIDTH, Math.Min(hi + 1 - MIN_WIDTH, cut));
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

        /// <summary>Whether a square of MIN_WIDTH tiles a building may stand on (`ok`) fits inside the piece somewhere.</summary>
        static bool Buildable(List<int> piece, HashSet<int> ok, int w)
        {
            var set = new HashSet<int>(piece);
            foreach (int t in piece)
            {
                bool all = true;
                for (int dy = 0; dy < MIN_WIDTH && all; dy++)
                    for (int dx = 0; dx < MIN_WIDTH && all; dx++)
                    {
                        int u = t + dx + dy * w;
                        if (t % w + dx >= w || !set.Contains(u) || !ok.Contains(u)) all = false;
                    }
                if (all) return true;
            }
            return false;
        }

        /// <summary>
        /// Each sliver of a block's pieces (no MIN_WIDTH square of tiles a building may stand on, `ok`) joined to the
        /// piece beside it it shares most edge with, again until it has room (a strip joined to a strip can still be
        /// one), so the lot beside gets the bigger building. A join must keep one outline and no more than `most`
        /// tiles; a sliver nothing takes is left, and gets no building (it's grass).
        /// </summary>
        static List<List<int>> MergeSlivers(List<List<int>> pieces, HashSet<int> ok, int w, double most)
        {
            if (pieces.Count < 2) return pieces;
            var owner = new Dictionary<int, int>();
            for (int i = 0; i < pieces.Count; i++) foreach (int t in pieces[i]) owner[t] = i;
            var gone = new bool[pieces.Count];
            for (bool joinedAny = true; joinedAny;)
            {
                joinedAny = false;
                for (int i = 0; i < pieces.Count; i++)
                {
                    if (gone[i] || Buildable(pieces[i], ok, w)) continue;
                    var shared = new Dictionary<int, int>();
                    foreach (int t in pieces[i])
                    {
                        int x = t % w;
                        foreach (int n in new[] { x > 0 ? t - 1 : -1, x < w - 1 ? t + 1 : -1, t - w, t + w })
                            if (n >= 0 && owner.TryGetValue(n, out int j) && j != i && !gone[j]) shared[j] = (shared.TryGetValue(j, out int c) ? c : 0) + 1;
                    }
                    // Most edge shared, then the lower index: the same choice every time.
                    var order = new List<int>(shared.Keys);
                    order.Sort((a, b) => shared[a] != shared[b] ? shared[b] - shared[a] : a - b);
                    foreach (int j in order)
                    {
                        if (pieces[i].Count + pieces[j].Count > most) continue;
                        var joined = new List<int>(pieces[j]);
                        joined.AddRange(pieces[i]);
                        if (Parcels.Outline(joined, w).Count != 1) continue;
                        pieces[j] = joined;
                        foreach (int t in pieces[i]) owner[t] = j;
                        gone[i] = true;
                        joinedAny = true;
                        break;
                    }
                }
            }
            var kept = new List<List<int>>();
            for (int i = 0; i < pieces.Count; i++) if (!gone[i]) kept.Add(pieces[i]);
            return kept;
        }

        static Vec2 TileCenter(int t, int w) => new Vec2((t % w) + 0.5, t / w + 0.5);

        static double Dist2(Vec2 a, Vec2 b)
        {
            double dx = a.x - b.x, dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        public TownModel Run(TownModel model, Rng rng) => Stepwise.Drain<TownModel>(done => Steps(model, rng, done));

        /// <summary>The lots, a block at a time (Run, all at once).</summary>
        public IEnumerable<string> Steps(TownModel model, Rng rng, Action<TownModel> done)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var g = Parcels.Ground(model);
            yield return "/ground";
            var list = new List<Lot>();
            var parks = model.ParkBlockSet();
            // The right of way along the roads in from the gates: lots keep back from them by the width they'll grow to
            // (the junction design widens them to the highway's two lanes each way, DesignStage).
            var reserve = DesignStage.GateRoads(model);
            double reach = DesignStage.GateReach(p);
            // Each block's lots, a pure function of the block: shared out over threads where there are some.
            List<List<int>> PiecesOf(Block block)
            {
                var tiles = new List<int>();
                foreach (int t in Parcels.TilesIn(block.polygon, block.holes, w, h))
                {
                    if (g.free[t] == 0) continue;
                    if (reserve.Count > 0)
                    {
                        var c = new Vec2(t % w + 0.5, t / w + 0.5);
                        bool kept = false;
                        foreach (var pts in reserve) if (Geo.DistToPolyline(c, pts) < reach) { kept = true; break; }
                        if (kept) continue;
                    }
                    tiles.Add(t);
                }
                var pieces = new List<List<int>>();
                double maxSize = Math.Max(1.5 * p.lotMinSize, p.lotMaxSize * TownPlan.Rules(block.transect).lotScale);
                var blockRng = Rng.Local(model.seed, Regions.Stream(model, "lots", Geo.PolygonCentroid(block.polygon)), Signature.OfPolygon(block.polygon));
                foreach (var comp in Parcels.Components(tiles, w)) Split(comp, w, maxSize, blockRng, pieces);
                // Where a building may stand: clear of the sidewalks by the least yard its zone allows.
                var gaps = BuildingsStage.KerbGaps(model, tiles);
                double floor = BuildingsStage.YardFloor(p, TownPlan.Rules(block.transect));
                var ok = new HashSet<int>();
                foreach (var kv in gaps) if (kv.Value >= floor) ok.Add(kv.Key);
                return MergeSlivers(pieces, ok, w, MERGE_MOST * maxSize);
            }
            Lot LotOf(Block block, List<int> piece)
            {
                if (piece.Count < p.lotMinSize) return null;
                var counts = Parcels.FrontageCounts(piece, g, w, h);
                if (counts[0] == 0 && counts[1] == 0 && counts[2] == 0 && counts[3] == 0) return null;
                var rings = Parcels.Outline(piece, w);
                if (rings.Count != 1) return null;
                return new Lot { blockId = block.id, polygon = rings[0], frontageEdgeId = FrontageEdge(piece, g, w, h) };
            }
            Regions.Count(model);
            if (model.blocks.Count > 0) Regions.At(model, Geo.PolygonCentroid(model.blocks[0].polygon));
            var made = new List<Lot>[model.blocks.Count];
            if (Work.ForEach != null)
            {
                Work.ForEach(made.Length, i =>
                {
                    var mine = new List<Lot>();
                    foreach (var piece in PiecesOf(model.blocks[i])) mine.Add(LotOf(model.blocks[i], piece));
                    made[i] = mine;
                });
                yield return "/block";
            }
            else
                for (int i = 0; i < made.Length; i++)
                {
                    yield return "/block";
                    var mine = new List<Lot>();
                    int looked = 0;
                    foreach (var piece in PiecesOf(model.blocks[i]))
                    {
                        // A big block's lots a share at a time too (a town's farmland was one block of 100 ms).
                        if (++looked % PIECES_A_STEP == 0) yield return "/block";
                        mine.Add(LotOf(model.blocks[i], piece));
                    }
                    made[i] = mine;
                }
            foreach (var lots in made)
                foreach (var lot in lots)
                {
                    if (lot == null) continue;
                    lot.id = list.Count;
                    list.Add(lot);
                }

            var landmarks = new List<Landmark>();
            if (list.Count > 0)
            {
                Vec2 Centroid(Lot l) => Geo.PolygonCentroid(l.polygon);
                var byDistance = list.FindAll(l => !parks.Contains(l.blockId));
                if (byDistance.Count == 0) byDistance = new List<Lot>(list);
                Numbers.StableSort(byDistance, (a, b) => Dist2(Centroid(a), model.downtown) - Dist2(Centroid(b), model.downtown));
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
            done(next);
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

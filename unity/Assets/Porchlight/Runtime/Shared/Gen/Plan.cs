// Stage 6b: the plan. Once the blocks are known: a big town's second centre
// (TownPlan.SecondCentre), each block's era and transect zone (TownPlan), and
// the neighbourhood units (Perry, 1929) the blocks make up.
//
// A neighbourhood is a piece of the land the arterials (and water) cut the
// town into, about 400 m across. A piece too small for a school of its own is
// joined to the smallest one beside it across an arterial (so a small town is
// one or two neighbourhoods, arterials and all); one much bigger is split into
// as many as it holds. Each with enough blocks keeps the block nearest its
// middle as its park; the buildings stage gives it a school near its middle
// and shops at its corners, where the arterials meet.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class PlanStage : IStage
    {
        /// <summary>A neighbourhood's land, about 400 m (100 tiles) square, in tiles (its streets too).</summary>
        public const int TARGET_AREA = 100 * 100;
        /// <summary>Land between arterials smaller than this (a 220 m square) joins a neighbour; bigger than MAX_AREA, it's split.</summary>
        public const int MIN_AREA = 55 * 55, MAX_AREA = TARGET_AREA * 8 / 5;
        /// <summary>A neighbourhood is split if it's further across than this (tiles, between its blocks' middles: 520 m).</summary>
        public const double MAX_ACROSS = 130;
        /// <summary>A neighbourhood with this many blocks, and this much land in them (tiles: a hamlet has neither), has a park and a school.</summary>
        public const int MIN_BLOCKS = 4, MIN_LAND = 1000;

        /// <summary>Big enough for a park and a school of its own.</summary>
        public static bool Served(Neighbourhood hood) => hood.blockIds.Count >= MIN_BLOCKS && hood.area >= MIN_LAND;
        /// <summary>A park is a whole block of at least this many tiles, and no more than PARK_MAX_SHARE of its neighbourhood's.</summary>
        public const int PARK_MIN_TILES = 24;
        public const double PARK_MAX_SHARE = 0.25;
        const int SPLIT_ROUNDS = 10;

        public string Name => "plan";

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var next = model.With();
            next.secondCentre = TownPlan.SecondCentre(model);

            // Each block's tiles, middle, era and zone.
            int nb = model.blocks.Count;
            var blockTiles = new List<List<int>>(nb);
            var mids = new List<Vec2>(nb);
            var blocks = new List<Block>(nb);
            foreach (var b in model.blocks)
            {
                var tiles = Parcels.TilesIn(b.polygon, b.holes, w, h);
                blockTiles.Add(tiles);
                var mid = Middle(tiles, w, b);
                mids.Add(mid);
                blocks.Add(new Block { id = b.id, polygon = b.polygon, holes = b.holes, era = TownPlan.EraAt(next, mid), transect = TownPlan.TransectAt(next, mid) });
            }
            next.blocks = blocks;

            // The pieces of land the arterials and water cut the town into, and which touch across an arterial.
            var label = Regions(next, out int count);
            var area = new int[count];
            foreach (int l in label) if (l >= 0) area[l]++;
            var pairs = Touching(label, next, count);

            // Join the smallest piece to its smallest neighbour, until none is too small (or it has no neighbour).
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            int Find(int a)
            {
                while (parent[a] != a) a = parent[a] = parent[parent[a]];
                return a;
            }
            var groupArea = (int[])area.Clone();
            var settled = new bool[count];
            for (; ; )
            {
                int small = -1;
                for (int i = 0; i < count; i++)
                    if (Find(i) == i && !settled[i] && groupArea[i] < MIN_AREA && (small < 0 || groupArea[i] < groupArea[small])) small = i;
                if (small < 0) break;
                int into = -1;
                foreach (var (a, b) in pairs)
                {
                    int ga = Find(a), gb = Find(b);
                    if (ga == gb || (ga != small && gb != small)) continue;
                    int other = ga == small ? gb : ga;
                    if (into < 0 || groupArea[other] < groupArea[into] || (groupArea[other] == groupArea[into] && other < into)) into = other;
                }
                if (into < 0)
                {
                    settled[small] = true;
                    continue;
                }
                parent[small] = into;
                groupArea[into] += groupArea[small];
                settled[into] = false;
            }

            // Each block goes with the piece most of its tiles are in (or, none, the nearest block's).
            var groupOfBlock = new int[nb];
            for (int k = 0; k < nb; k++)
            {
                var votes = new Dictionary<int, int>();
                int best = -1;
                foreach (int t in blockTiles[k])
                {
                    if (label[t] < 0) continue;
                    int gk = Find(label[t]);
                    votes[gk] = (votes.TryGetValue(gk, out int v) ? v : 0) + 1;
                    if (best < 0 || votes[gk] > votes[best] || (votes[gk] == votes[best] && gk < best)) best = gk;
                }
                groupOfBlock[k] = best;
            }
            for (int k = 0; k < nb; k++)
            {
                if (groupOfBlock[k] >= 0) continue;
                int near = -1;
                for (int j = 0; j < nb; j++)
                    if (groupOfBlock[j] >= 0 && (near < 0 || Geo.Dist(mids[j], mids[k]) < Geo.Dist(mids[near], mids[k]))) near = j;
                groupOfBlock[k] = near >= 0 ? groupOfBlock[near] : 0;
            }

            // The neighbourhoods: each piece's blocks (a big piece split into as many as it holds), in order of their first block.
            var members = new Dictionary<int, List<int>>();
            var order = new List<int>();
            for (int k = 0; k < nb; k++)
            {
                if (!members.TryGetValue(groupOfBlock[k], out var list))
                {
                    members[groupOfBlock[k]] = list = new List<int>();
                    order.Add(groupOfBlock[k]);
                }
                list.Add(k);
            }
            var hoods = new List<Neighbourhood>();
            foreach (int gk in order)
            {
                var list = members[gk];
                // Too much land, or too far across (an arc of land between the ring and the edge): as many as it holds.
                int parts = gk >= 0 && gk < count && groupArea[gk] > MAX_AREA ? Math.Max(2, (int)Math.Round((double)groupArea[gk] / TARGET_AREA)) : 1;
                parts = Math.Max(parts, (int)Math.Ceiling(Across(list, mids) / MAX_ACROSS));
                foreach (var part in Split(list, mids, blockTiles, Math.Min(parts, list.Count)))
                {
                    var hood = new Neighbourhood { id = hoods.Count, blockIds = part };
                    double x = 0, y = 0;
                    foreach (int k in part)
                    {
                        int n = Math.Max(1, blockTiles[k].Count);
                        x += mids[k].x * n;
                        y += mids[k].y * n;
                        hood.area += n;
                    }
                    hood.middle = new Vec2(x / Math.Max(1, hood.area), y / Math.Max(1, hood.area));
                    foreach (int k in part) blocks[k].neighbourhood = hood.id;
                    hoods.Add(hood);
                }
            }

            // Each big enough neighbourhood's park: the block nearest its middle that's a fair size (not the town's own middle, where the plaza is).
            int plazaBlock = -1;
            for (int k = 0; k < nb; k++)
                if (plazaBlock < 0 || Geo.Dist(mids[k], model.center) < Geo.Dist(mids[plazaBlock], model.center)) plazaBlock = k;
            foreach (var hood in hoods)
            {
                if (!Served(hood)) continue;
                int park = -1;
                foreach (int k in hood.blockIds)
                {
                    int n = blockTiles[k].Count;
                    if (k == plazaBlock || n < PARK_MIN_TILES || n > PARK_MAX_SHARE * hood.area) continue;
                    if (park < 0 || Geo.Dist(mids[k], hood.middle) < Geo.Dist(mids[park], hood.middle)) park = k;
                }
                hood.parkBlockId = park;
            }
            next.neighbourhoods = hoods;
            return next;
        }

        /// <summary>The furthest apart two of these blocks' middles are.</summary>
        public static double Across(List<int> list, List<Vec2> mids)
        {
            double most = 0;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++) most = Math.Max(most, Geo.Dist(mids[list[i]], mids[list[j]]));
            return most;
        }

        /// <summary>The middle of a block's tiles (its polygon's centroid if it has none).</summary>
        public static Vec2 Middle(List<int> tiles, int w, Block b)
        {
            if (tiles.Count == 0) return Geo.PolygonCentroid(b.polygon);
            double x = 0, y = 0;
            foreach (int t in tiles)
            {
                x += t % w + 0.5;
                y += t / w + 0.5;
            }
            return new Vec2(x / tiles.Count, y / tiles.Count);
        }

        /// <summary>Under an arterial: within its half width (and half a tile) of its line.</summary>
        public static byte[] ArterialMask(TownModel m)
        {
            int w = m.width, h = m.height;
            var o = new byte[w * h];
            double r = Raster.RoadHalfWidth(RoadKind.Main, m.@params) + 0.5;
            foreach (var e in m.roads.edges)
            {
                if (e.cls != RoadClass.Arterial) continue;
                foreach (var q in Geo.SamplePolyline(e.points, 0.25))
                    for (int y = (int)Math.Floor(q.y - r); y <= (int)Math.Floor(q.y + r); y++)
                        for (int x = (int)Math.Floor(q.x - r); x <= (int)Math.Floor(q.x + r); x++)
                            if (x >= 0 && y >= 0 && x < w && y < h && Numbers.Hypot(x + 0.5 - q.x, y + 0.5 - q.y) <= r) o[Grid.Index(x, y, w)] = 1;
            }
            return o;
        }

        /// <summary>
        /// Each tile in town that isn't water or under an arterial, labelled by the piece of land (4-connected) it's in;
        /// -1 for the rest. `count` pieces.
        /// </summary>
        public static int[] Regions(TownModel m, out int count)
        {
            int w = m.width, h = m.height;
            var water = Raster.WaterMask(m);
            var arterial = ArterialMask(m);
            var label = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    bool open = Numbers.Hypot(x + 0.5 - m.center.x, y + 0.5 - m.center.y) <= m.townRadius && water[i] == 0 && arterial[i] == 0;
                    label[i] = open ? -2 : -1;
                }
            count = 0;
            var queue = new List<int>();
            for (int i = 0; i < label.Length; i++)
            {
                if (label[i] != -2) continue;
                queue.Clear();
                queue.Add(i);
                label[i] = count;
                for (int head = 0; head < queue.Count; head++)
                {
                    int c = queue[head];
                    int cx = c % w, cy = c / w;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = Grid.Index(nx, ny, w);
                        if (label[j] != -2) continue;
                        label[j] = count;
                        queue.Add(j);
                    }
                }
                count++;
            }
            return label;
        }

        /// <summary>
        /// Pieces that touch across an arterial: each piece grown out over the dry arterial tiles round it (nearest
        /// first), and the pieces whose growth meets. Pairs (a &lt; b), each once, in order.
        /// </summary>
        static List<(int a, int b)> Touching(int[] label, TownModel m, int count)
        {
            int w = m.width, h = m.height;
            var water = Raster.WaterMask(m);
            var grown = (int[])label.Clone();
            var queue = new List<int>();
            for (int i = 0; i < grown.Length; i++) if (grown[i] >= 0) queue.Add(i);
            for (int head = 0; head < queue.Count; head++)
            {
                int c = queue[head];
                int cx = c % w, cy = c / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = Grid.Index(nx, ny, w);
                    if (grown[j] >= 0 || water[j] != 0 || Numbers.Hypot(nx + 0.5 - m.center.x, ny + 0.5 - m.center.y) > m.townRadius) continue;
                    grown[j] = grown[c];
                    queue.Add(j);
                }
            }
            var seen = new HashSet<long>();
            var o = new List<(int a, int b)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int a = grown[Grid.Index(x, y, w)];
                    if (a < 0) continue;
                    foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                    {
                        if (x + dx >= w || y + dy >= h) continue;
                        int b = grown[Grid.Index(x + dx, y + dy, w)];
                        if (b < 0 || b == a) continue;
                        int lo = Math.Min(a, b), hi = Math.Max(a, b);
                        if (seen.Add((long)lo * count + hi)) o.Add((lo, hi));
                    }
                }
            o.Sort((p, q) => p.a != q.a ? p.a.CompareTo(q.a) : p.b.CompareTo(q.b));
            return o;
        }

        /// <summary>
        /// Blocks split into `parts` groups of blocks near each other (k-means on their middles, weighted by their
        /// tiles; seeded far apart, so the same blocks always split the same way). Empty groups are dropped.
        /// </summary>
        static List<List<int>> Split(List<int> list, List<Vec2> mids, List<List<int>> tiles, int parts)
        {
            if (parts <= 1) return new List<List<int>> { new List<int>(list) };
            double mx = 0, my = 0, all = 0;
            foreach (int k in list)
            {
                double n = Math.Max(1, tiles[k].Count);
                mx += mids[k].x * n;
                my += mids[k].y * n;
                all += n;
            }
            var middle = new Vec2(mx / all, my / all);
            var seeds = new List<Vec2>();
            int first = list[0];
            foreach (int k in list) if (Geo.Dist(mids[k], middle) > Geo.Dist(mids[first], middle)) first = k;
            seeds.Add(mids[first]);
            while (seeds.Count < parts)
            {
                int far = -1;
                double farD = -1;
                foreach (int k in list)
                {
                    double d = double.PositiveInfinity;
                    foreach (var s in seeds) d = Math.Min(d, Geo.Dist(mids[k], s));
                    if (d > farD)
                    {
                        farD = d;
                        far = k;
                    }
                }
                seeds.Add(mids[far]);
            }
            var of = new int[list.Count];
            for (int round = 0; round < SPLIT_ROUNDS; round++)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    int best = 0;
                    for (int s = 1; s < seeds.Count; s++) if (Geo.Dist(mids[list[i]], seeds[s]) < Geo.Dist(mids[list[i]], seeds[best])) best = s;
                    of[i] = best;
                }
                for (int s = 0; s < seeds.Count; s++)
                {
                    double x = 0, y = 0, n = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (of[i] != s) continue;
                        double wgt = Math.Max(1, tiles[list[i]].Count);
                        x += mids[list[i]].x * wgt;
                        y += mids[list[i]].y * wgt;
                        n += wgt;
                    }
                    if (n > 0) seeds[s] = new Vec2(x / n, y / n);
                }
            }
            var o = new List<List<int>>();
            for (int s = 0; s < seeds.Count; s++)
            {
                var part = new List<int>();
                for (int i = 0; i < list.Count; i++) if (of[i] == s) part.Add(list[i]);
                if (part.Count > 0) o.Add(part);
            }
            return o;
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            var hoods = model.neighbourhoods;
            var listed = new int[model.blocks.Count];
            foreach (var hood in hoods)
            {
                if (hood.blockIds.Count == 0) errors.Add($"neighbourhood {hood.id} has no blocks");
                foreach (int k in hood.blockIds)
                {
                    listed[k]++;
                    if (model.blocks[k].neighbourhood != hood.id) errors.Add($"block {k} is listed in neighbourhood {hood.id} but says it's in {model.blocks[k].neighbourhood}");
                }
                if (hood.parkBlockId >= 0 && !hood.blockIds.Contains(hood.parkBlockId)) errors.Add($"neighbourhood {hood.id}'s park is block {hood.parkBlockId}, not one of its own");
            }
            for (int k = 0; k < listed.Length; k++)
                if (listed[k] != 1) errors.Add($"block {k} is in {listed[k]} neighbourhoods");
            if (errors.Count > 3) errors.RemoveRange(3, errors.Count - 3);
            return errors;
        }
    }
}

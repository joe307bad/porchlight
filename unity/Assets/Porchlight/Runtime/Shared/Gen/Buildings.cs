// Stage 7b: buildings. Each lot (except the plaza) may get one rectangular
// building on whole tiles: the largest rectangle inside the lot whose whole
// front edge looks onto the road across a front yard, set back a little from
// the rear and sides when there's room. The building faces the road it fronts.
// The biggest lot near the center gets the church, and the building nearest
// the plaza becomes the hall.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class BuildingsStage : IStage
    {
        const double INSET = 0.15;
        const double SMOOTHING_SLACK = 0.1;
        const double MIN_YARD = 0.5;
        const double CHURCH_REACH = 0.45;

        sealed class Rect
        {
            public int x0, y0, x1, y1; // inclusive

            public Rect Copy() => new Rect { x0 = x0, y0 = y0, x1 = x1, y1 = y1 };
        }

        public string Name => "buildings";

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var C = model.center;
            double R = model.townRadius;
            var g = Parcels.Ground(model);
            var plaza = new HashSet<int>();
            foreach (var l in model.landmarks) if (l.kind == LandmarkKind.Plaza) foreach (int id in l.lotIds) plaza.Add(id);

            int church = -1;
            double churchArea = 0;
            foreach (var lot in model.lots)
            {
                if (plaza.Contains(lot.id) || Geo.Dist(Geo.PolygonCentroid(lot.polygon), C) > R * CHURCH_REACH) continue;
                double a = Math.Abs(Geo.PolygonArea(lot.polygon));
                if (a > churchArea)
                {
                    churchArea = a;
                    church = lot.id;
                }
            }

            var list = new List<Building>();
            var landmarks = new List<Landmark>(model.landmarks);
            foreach (var lot in model.lots)
            {
                if (plaza.Contains(lot.id)) continue;
                bool isChurch = lot.id == church;
                double d = Math.Min(1, Geo.Dist(Geo.PolygonCentroid(lot.polygon), C) / R);
                if (!isChurch && !rng.Chance(p.buildingDensity * (0.7 + 0.3 * (1 - d)))) continue;
                var tiles = Parcels.TilesIn(lot.polygon, null, w, h);
                var counts = Parcels.FrontageCounts(tiles, g, w, h);
                int max = Math.Max(Math.Max(counts[0], counts[1]), Math.Max(counts[2], counts[3]));
                int facingIndex = Array.IndexOf(counts, max);
                double maxSide = p.maxBuildingSize + (isChurch ? 2 : 0);
                var lotTiles = new HashSet<int>(tiles);
                var gaps = p.frontSetback > 0 ? KerbGaps(model, tiles) : null;
                Rect rect;
                double yard = p.frontSetback;
                for (; ; yard -= 0.5)
                {
                    rect = BestRect(tiles, lotTiles, gaps, facingIndex, maxSide, yard, g, w, h);
                    if (rect != null || yard <= MIN_YARD) break;
                }
                if (rect == null) continue;
                SetBack(rect, facingIndex, lotTiles, ReachFor(yard), g, w, h, rng);

                var kind = isChurch ? BuildingKind.Church : PickKind(d, rng);
                var footprint = new List<Vec2>
                {
                    new Vec2(rect.x0 + INSET, rect.y0 + INSET),
                    new Vec2(rect.x1 + 1 - INSET, rect.y0 + INSET),
                    new Vec2(rect.x1 + 1 - INSET, rect.y1 + 1 - INSET),
                    new Vec2(rect.x0 + INSET, rect.y1 + 1 - INSET),
                };
                var covered = new List<TilePos>();
                for (int y = rect.y0; y <= rect.y1; y++)
                    for (int x = rect.x0; x <= rect.x1; x++)
                        covered.Add(new TilePos(x, y));
                var building = new Building
                {
                    id = list.Count,
                    lotId = lot.id,
                    footprint = footprint,
                    tiles = covered,
                    kind = kind,
                    floors = FloorsFor(kind, d, rng),
                    facing = Parcels.FACINGS[facingIndex],
                };
                list.Add(building);
                if (isChurch) landmarks.Add(new Landmark { id = landmarks.Count, kind = LandmarkKind.Church, at = Geo.PolygonCentroid(footprint), lotIds = new List<int> { lot.id } });
            }

            Landmark plazaMark = null;
            foreach (var l in model.landmarks) if (l.kind == LandmarkKind.Plaza) { plazaMark = l; break; }
            if (plazaMark != null)
            {
                var plazaAt = plazaMark.at;
                Building hall = null;
                foreach (var b in list)
                {
                    if (b.kind == BuildingKind.Church || b.tiles.Count < 9) continue;
                    if (hall == null || Geo.Dist(Geo.PolygonCentroid(b.footprint), plazaAt) < Geo.Dist(Geo.PolygonCentroid(hall.footprint), plazaAt)) hall = b;
                }
                if (hall != null)
                {
                    hall.kind = BuildingKind.Hall;
                    hall.floors = 2;
                }
            }
            var next = model.With();
            next.buildings = list;
            next.landmarks = landmarks;
            return next;
        }

        public List<string> Check(TownModel model) => CheckBuildings(model, Parcels.Ground(model));

        public static List<string> CheckBuildings(TownModel model, Ground g)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var owner = new int[w * h];
            for (int i = 0; i < owner.Length; i++) owner[i] = -1;
            foreach (var b in model.buildings)
            {
                if (b.tiles.Count == 0) return new List<string> { $"building {b.id} has no tiles" };
                foreach (var t in b.tiles)
                {
                    int i = Grid.Index(t.x, t.y, w);
                    if (owner[i] >= 0) return new List<string> { $"buildings {owner[i]} and {b.id} overlap" };
                    owner[i] = b.id;
                    if (g.water[i] != 0) return new List<string> { $"building {b.id} stands in water" };
                    if (g.road.kind[i] != 0) return new List<string> { $"building {b.id} stands on a road" };
                    if (!Geo.PointInPolygon(new Vec2(t.x + 0.5, t.y + 0.5), b.footprint)) return new List<string> { $"building {b.id}'s tiles don't match its footprint" };
                }
                var ring = new List<Vec2>(b.footprint) { b.footprint[0] };
                foreach (var e in model.roads.edges)
                {
                    double clearance = Raster.RoadDrawWidth(e.kind, p) / 2;
                    if (Geo.PolylineDistance(ring, e.points) < clearance - 1e-6) return new List<string> { $"building {b.id} overlaps road {e.id}" };
                }
                int d = (int)b.facing;
                var (dx, dy) = Grid.DIRS4[d];
                var own = new HashSet<int>();
                foreach (var t in b.tiles) own.Add(Grid.Index(t.x, t.y, w));
                foreach (var t in b.tiles)
                {
                    int nx = t.x + dx, ny = t.y + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && own.Contains(Grid.Index(nx, ny, w))) continue;
                    bool fronts = false;
                    for (int k = 1; k <= ReachFor(p.frontSetback); k++)
                    {
                        int x = t.x + dx * k, y = t.y + dy * k;
                        if (x < 0 || y < 0 || x >= w || y >= h) break;
                        int i = Grid.Index(x, y, w);
                        if (Parcels.IsStreetTile(g, i))
                        {
                            fronts = true;
                            break;
                        }
                        if (owner[i] >= 0 || g.water[i] != 0 || g.road.kind[i] != 0) break;
                    }
                    if (!fronts) return new List<string> { $"building {b.id}'s front at {t.x},{t.y} doesn't meet the road it faces ({b.facing})" };
                }
            }
            return new List<string>();
        }

        /// <summary>How far each lot tile's edge is from the nearest drawn sidewalk (or footpath), in tiles.</summary>
        static Dictionary<int, double> KerbGaps(TownModel model, List<int> tiles)
        {
            int w = model.width;
            var p = model.@params;
            var o = new Dictionary<int, double>();
            if (tiles.Count == 0) return o;
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
            foreach (int t in tiles)
            {
                int x = t % w;
                int y = (t - x) / w;
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x + 1);
                y1 = Math.Max(y1, y + 1);
            }
            double near = p.frontSetback + 3;
            var box = new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1), new Vec2(x0, y0) };
            var roads = new List<RoadEdge>();
            foreach (var e in model.roads.edges)
            {
                bool inside = false;
                foreach (var q in e.points) if (q.x > x0 - near && q.x < x1 + near && q.y > y0 - near && q.y < y1 + near) { inside = true; break; }
                if (inside || Geo.PolylineDistance(box, e.points) < near) roads.Add(e);
            }
            foreach (int t in tiles)
            {
                int x = t % w;
                var c = new Vec2(x + 0.5, (t - x) / w + 0.5);
                double gap = double.PositiveInfinity;
                foreach (var e in roads) gap = Math.Min(gap, Geo.DistToPolyline(c, e.points) - Raster.RoadReach(e.kind, p) - 0.5 - SMOOTHING_SLACK);
                o[t] = gap;
            }
            return o;
        }

        /// <summary>How many tiles ahead a building's front may look for its street, for a yard of `yard` tiles.</summary>
        static int ReachFor(double yard) => yard == 0 ? 1 : (int)Math.Ceiling(yard) + 2;

        static Rect BestRect(List<int> tiles, HashSet<int> lotTiles, Dictionary<int, double> gaps, int facing, double maxSide, double yard, Ground g, int w, int h)
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
            int bw = x1 - x0 + 1;
            int bh = y1 - y0 + 1;
            var inLot = new byte[bw * bh];
            var front = new byte[bw * bh];
            var (fx, fy) = Grid.DIRS4[facing];
            foreach (int t in tiles)
            {
                int x = t % w;
                int y = (t - x) / w;
                int li = (y - y0) * bw + (x - x0);
                if (yard > 0 && gaps != null && (gaps.TryGetValue(t, out var gap) ? gap : double.PositiveInfinity) < yard) continue;
                inLot[li] = 1;
                if (StreetAhead(x, y, fx, fy, ReachFor(yard), lotTiles, g, w, h)) front[li] = 1;
            }
            var sat = new int[(bw + 1) * (bh + 1)];
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    sat[(y + 1) * (bw + 1) + x + 1] = inLot[y * bw + x] + sat[y * (bw + 1) + x + 1] + sat[(y + 1) * (bw + 1) + x] - sat[y * (bw + 1) + x];
            bool Filled(int ax, int ay, int bx, int by) =>
                sat[(by + 1) * (bw + 1) + bx + 1] - sat[ay * (bw + 1) + bx + 1] - sat[(by + 1) * (bw + 1) + ax] + sat[ay * (bw + 1) + ax] == (bx - ax + 1) * (by - ay + 1);
            bool HasFront(int ax, int ay, int bx, int by)
            {
                if (facing == 0) for (int x = ax; x <= bx; x++) if (front[ay * bw + x] == 0) return false;
                if (facing == 2) for (int x = ax; x <= bx; x++) if (front[by * bw + x] == 0) return false;
                if (facing == 1) for (int y = ay; y <= by; y++) if (front[y * bw + bx] == 0) return false;
                if (facing == 3) for (int y = ay; y <= by; y++) if (front[y * bw + ax] == 0) return false;
                return true;
            }
            Rect best = null;
            int bestArea = 0;
            for (int ay = 0; ay < bh; ay++)
            {
                for (int ax = 0; ax < bw; ax++)
                {
                    if (inLot[ay * bw + ax] == 0) continue;
                    for (int by = ay; by < Math.Min(bh, ay + maxSide); by++)
                    {
                        if (!Filled(ax, ay, ax, by)) break;
                        for (int bx = ax; bx < Math.Min(bw, ax + maxSide); bx++)
                        {
                            if (!Filled(ax, ay, bx, by)) break;
                            int area = (bx - ax + 1) * (by - ay + 1);
                            if (area < 2) continue;
                            if (area > bestArea && HasFront(ax, ay, bx, by))
                            {
                                bestArea = area;
                                best = new Rect { x0 = ax + x0, y0 = ay + y0, x1 = bx + x0, y1 = by + y0 };
                            }
                        }
                    }
                }
            }
            return best;
        }

        static void SetBack(Rect rect, int facing, HashSet<int> lotTiles, int reach, Ground g, int w, int h, Rng rng)
        {
            bool depthAlongY = facing == 0 || facing == 2;
            int depth = depthAlongY ? rect.y1 - rect.y0 + 1 : rect.x1 - rect.x0 + 1;
            int width = depthAlongY ? rect.x1 - rect.x0 + 1 : rect.y1 - rect.y0 + 1;
            if (depth >= 4 && rng.Chance(0.7))
            {
                if (facing == 0) rect.y1--;
                else if (facing == 2) rect.y0++;
                else if (facing == 1) rect.x0++;
                else rect.x1--;
            }
            if (width >= 5 && rng.Chance(0.5))
            {
                var trial = rect.Copy();
                bool low = rng.Chance(0.5);
                if (depthAlongY)
                {
                    if (low) trial.x0++;
                    else trial.x1--;
                }
                else if (low) trial.y0++;
                else trial.y1--;
                if (FrontsStreet(trial, facing, lotTiles, reach, g, w, h))
                {
                    rect.x0 = trial.x0;
                    rect.y0 = trial.y0;
                    rect.x1 = trial.x1;
                    rect.y1 = trial.y1;
                }
            }
        }

        static bool FrontsStreet(Rect r, int facing, HashSet<int> lotTiles, int reach, Ground g, int w, int h)
        {
            var (dx, dy) = Grid.DIRS4[facing];
            for (int y = r.y0; y <= r.y1; y++)
                for (int x = r.x0; x <= r.x1; x++)
                    if (StreetAhead(x, y, dx, dy, reach, lotTiles, g, w, h)) return true;
            return false;
        }

        static bool StreetAhead(int x, int y, int dx, int dy, int reach, HashSet<int> lotTiles, Ground g, int w, int h)
        {
            for (int k = 1; k <= reach; k++)
            {
                int nx = x + dx * k, ny = y + dy * k;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) return false;
                int i = Grid.Index(nx, ny, w);
                if (Parcels.IsStreetTile(g, i)) return true;
                if (!lotTiles.Contains(i) || g.water[i] != 0) return false;
            }
            return false;
        }

        static BuildingKind PickKind(double d, Rng rng)
        {
            if (d < 0.35 && rng.Chance(0.6)) return BuildingKind.Shop;
            if (d > 0.7 && rng.Chance(0.25)) return BuildingKind.Workshop;
            return BuildingKind.House;
        }

        static int FloorsFor(BuildingKind kind, double d, Rng rng)
        {
            switch (kind)
            {
                case BuildingKind.Shop: return rng.Int(2, 3);
                case BuildingKind.Church:
                case BuildingKind.Hall: return 2;
                case BuildingKind.Workshop: return 1;
                default: return d < 0.5 ? rng.Int(1, 2) : 1;
            }
        }
    }
}

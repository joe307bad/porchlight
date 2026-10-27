// Turns the blueprint into a street scene for the 3D explore view: smoothed
// roads with sidewalks, every building on its footprint facing its street,
// yards with fences, sheds and gardens, community gardens, parked cars, and
// the sidewalk network townsfolk walk. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public enum HouseStyle { House, Workshop, Shop, Office, Hall, Church }

    public sealed class House
    {
        public int x, y;
        public List<TilePos> tiles;
        public Facing facing;
        public BuildingKind kind;
        public HouseStyle style;
        public int floors;
        public int buildingId;
        public int blockId;
    }

    public struct Fence
    {
        public int x, y;
        public Facing side;
    }

    public struct ParkedCar
    {
        public double x, y;
        /// <summary>Direction along the road, radians in world space.</summary>
        public double angle;
        /// <summary>Stable pick for model and colour.</summary>
        public int variant;
    }

    public sealed class RoadStroke
    {
        public RoadKind kind;
        public int from, to;
        /// <summary>Smoothed centreline, endpoints exactly on the graph nodes.</summary>
        public List<Vec2> points;
        /// <summary>Asphalt width (tiles).</summary>
        public double width;
    }

    public sealed class SidewalkPath
    {
        public List<Vec2> points;
        /// <summary>Which side of its road (+1 left, -1 right of the road's direction).</summary>
        public int side;
        /// <summary>Index of the road (in IsoLayout.roads) this sidewalk runs beside.</summary>
        public int road;
        /// <summary>Cumulative length at each point.</summary>
        public List<double> at;

        public double Length => at[at.Count - 1];
    }

    public sealed class SidewalkNetwork
    {
        public List<SidewalkPath> paths = new List<SidewalkPath>();
        /// <summary>For each path end (path * 2 + 0 start, + 1 end), the other path ends close enough to walk on to.</summary>
        public List<List<int>> links = new List<List<int>>();
    }

    public static class Use
    {
        public const byte NONE = 0;
        public const byte HOUSE = 1;
        public const byte YARD = 2;
        public const byte GARDEN = 3;
        public const byte SHED = 4;
        public const byte COMMUNITY_GARDEN = 5;
        public const byte GREEN = 6;
    }

    public struct BlockPlan
    {
        public int blockId;
        public int wanted;
        public int placed;
    }

    public sealed class LayoutBridge
    {
        public RoadKind kind;
        public List<Vec2> points;
        public double width;
    }

    public sealed class IsoLayout
    {
        public List<House> houses;
        public byte[] use;
        public List<Fence> fences;
        public List<BlockPlan> blocks;
        public List<ParkedCar> cars;
        public List<RoadStroke> roads;
        public List<LayoutBridge> bridges;
        public SidewalkNetwork sidewalks;
    }

    public static class Layout
    {
        /// <summary>Width of the sidewalk along each side of a road (tiles).</summary>
        public const double SIDEWALK = Raster.SIDEWALK_WIDTH;
        const int SMOOTHING_PASSES = 2;
        const double CORNER_RADIUS = 0.9;
        static readonly Facing[] FACINGS = { Facing.N, Facing.E, Facing.S, Facing.W };

        /// <summary>Add a point r either side of every bend, so Chaikin only rounds the corner itself.</summary>
        static List<Vec2> PadCorners(List<Vec2> pts, double r)
        {
            if (pts.Count < 3) return new List<Vec2>(pts);
            var o = new List<Vec2> { pts[0] };
            for (int i = 1; i < pts.Count - 1; i++)
            {
                var prev = pts[i - 1];
                var p = pts[i];
                var next = pts[i + 1];
                double toPrev = Geo.Dist(prev, p);
                double toNext = Geo.Dist(p, next);
                if (toPrev > r * 2) o.Add(new Vec2(p.x + ((prev.x - p.x) * r) / toPrev, p.y + ((prev.y - p.y) * r) / toPrev));
                o.Add(p);
                if (toNext > r * 2) o.Add(new Vec2(p.x + ((next.x - p.x) * r) / toNext, p.y + ((next.y - p.y) * r) / toNext));
            }
            o.Add(pts[pts.Count - 1]);
            return o;
        }

        public static double RoadWidth(RoadKind kind, TownModel model) =>
            kind == RoadKind.Main ? model.@params.mainRoadWidth : kind == RoadKind.Street ? model.@params.streetWidth : 0.5;

        public static IsoLayout Build(TownModel model)
        {
            var roads = new List<RoadStroke>();
            foreach (var e in model.roads.edges)
                roads.Add(new RoadStroke
                {
                    kind = e.kind,
                    from = e.from,
                    to = e.to,
                    points = Geo.Chaikin(PadCorners(e.points, CORNER_RADIUS), SMOOTHING_PASSES),
                    width = RoadWidth(e.kind, model),
                });
            var bridges = FindBridges(model, roads);
            var layout = PlanBlocks(model);
            layout.cars = ParkCars(model, roads);
            layout.roads = roads;
            layout.bridges = bridges;
            layout.sidewalks = SidewalkNetworkOf(roads);
            return layout;
        }

        static IsoLayout PlanBlocks(TownModel model)
        {
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            var use = new byte[w * h];
            var houses = new List<House>();
            var fences = new List<Fence>();
            var plans = new List<BlockPlan>();
            bool Street(int x, int y) =>
                Grid.InBounds(x, y, w, h) && (tiles.overlay[Grid.Index(x, y, w)] == Overlay.ROAD_MAIN || tiles.overlay[Grid.Index(x, y, w)] == Overlay.ROAD_STREET);
            bool Open(int t)
            {
                byte ter = tiles.terrain[t];
                return tiles.overlay[t] == Overlay.NONE && ter != Terrain.WATER_SHALLOW && ter != Terrain.WATER_DEEP;
            }
            var C = model.center;
            var buildingOnLot = new Dictionary<int, Building>();
            foreach (var b in model.buildings) buildingOnLot[b.lotId] = b;

            foreach (var block in model.blocks)
            {
                var lots = model.lots.FindAll(l => l.blockId == block.id);
                int wanted = 0, placed = 0;
                int builtLots = 0;
                foreach (var l in lots) if (buildingOnLot.ContainsKey(l.id)) builtLots++;
                foreach (var lot in lots)
                {
                    var lotTiles = Parcels.TilesIn(lot.polygon, null, w, h).FindAll(Open);
                    var own = new HashSet<int>(lotTiles);
                    if (!buildingOnLot.TryGetValue(lot.id, out var b))
                    {
                        bool garden = builtLots >= 2 && Hash(lot.id, 1) < 0.45;
                        foreach (int t in lotTiles) use[t] = garden ? Use.COMMUNITY_GARDEN : Use.GREEN;
                        if (garden) foreach (int t in lotTiles) fences.AddRange(FenceAround(t, own, w, h, Street));
                        continue;
                    }
                    wanted++;
                    var footprint = b.tiles.FindAll(t => own.Contains(Grid.Index(t.x, t.y, w)) || Open(Grid.Index(t.x, t.y, w)));
                    if (footprint.Count == 0) continue;
                    placed++;
                    double cx = 0, cy = 0;
                    foreach (var t in footprint)
                    {
                        cx = cx + t.x + 0.5;
                        cy = cy + t.y + 0.5;
                    }
                    bool nearCentre = Numbers.Hypot(cx / footprint.Count - C.x, cy / footprint.Count - C.y) < model.townRadius * 0.3;
                    var style = b.kind == BuildingKind.Shop && nearCentre && b.floors >= 2 ? HouseStyle.Office : StyleOf(b.kind);
                    int floors = style == HouseStyle.Office ? Math.Min(5, b.floors + 2) : b.floors;
                    var house = new House { x = footprint[0].x, y = footprint[0].y, tiles = footprint, facing = b.facing, kind = b.kind, style = style, floors = floors, buildingId = b.id, blockId = block.id };
                    houses.Add(house);
                    foreach (var t in footprint) use[Grid.Index(t.x, t.y, w)] = Use.HOUSE;

                    var yard = lotTiles.FindAll(t => use[t] == Use.NONE);
                    foreach (int t in yard) use[t] = Use.YARD;
                    int rndSeed = b.id + 7919;
                    if ((style == HouseStyle.House || style == HouseStyle.Workshop) && yard.Count >= 3)
                    {
                        int far = yard[0];
                        foreach (int t in yard) if (BackDistance(t, house, w) > BackDistance(far, house, w)) far = t;
                        if (Hash(rndSeed, 1) < 0.65) use[far] = Use.SHED;
                        if (style == HouseStyle.House && Hash(rndSeed, 2) < 0.6)
                        {
                            var beds = yard.FindAll(t2 => use[t2] == Use.YARD && t2 != far);
                            for (int k = 0; k < Math.Min(2, beds.Count); k++) use[beds[k]] = Use.GARDEN;
                        }
                    }
                    if (style == HouseStyle.House || style == HouseStyle.Workshop)
                    {
                        var plot = new HashSet<int>(yard);
                        foreach (var t in footprint) plot.Add(Grid.Index(t.x, t.y, w));
                        foreach (int t in yard) fences.AddRange(FenceAround(t, plot, w, h, Street));
                    }
                }
                foreach (int t in Parcels.TilesIn(block.polygon, block.holes, w, h)) if (Open(t) && use[t] == Use.NONE) use[t] = Use.GREEN;
                plans.Add(new BlockPlan { blockId = block.id, wanted = wanted, placed = placed });
            }
            return new IsoLayout { houses = houses, use = use, fences = fences, blocks = plans };
        }

        public static HouseStyle StyleOf(BuildingKind k) => k switch
        {
            BuildingKind.House => HouseStyle.House,
            BuildingKind.Shop => HouseStyle.Shop,
            BuildingKind.Workshop => HouseStyle.Workshop,
            BuildingKind.Church => HouseStyle.Church,
            _ => HouseStyle.Hall,
        };

        public static string StyleName(HouseStyle s) => s.ToString().ToLowerInvariant();

        static double BackDistance(int t, House house, int w)
        {
            var (fx, fy) = Grid.DIRS4[(int)house.facing];
            double best = double.PositiveInfinity;
            foreach (var p in house.tiles) best = Math.Min(best, -((t % w) - p.x) * fx - (t / w - p.y) * fy);
            return best;
        }

        static List<Fence> FenceAround(int t, HashSet<int> own, int w, int h, Func<int, int, bool> street)
        {
            int x = t % w;
            int y = (t - x) / w;
            var o = new List<Fence>();
            for (int k = 0; k < 4; k++)
            {
                var (dx, dy) = Grid.DIRS4[k];
                int nx = x + dx, ny = y + dy;
                if (own.Contains(Grid.Index(nx, ny, w)) && Grid.InBounds(nx, ny, w, h)) continue;
                if (street(nx, ny)) continue;
                o.Add(new Fence { x = x, y = y, side = FACINGS[k] });
            }
            return o;
        }

        /// <summary>Roads with an end no other road meets (cul-de-sacs and the map-edge stubs).</summary>
        public static HashSet<int> DeadEnds(IReadOnlyList<RoadStroke> roads)
        {
            var degree = new Dictionary<int, int>();
            foreach (var r in roads)
                foreach (int n in new[] { r.from, r.to })
                    degree[n] = (degree.TryGetValue(n, out int d) ? d : 0) + 1;
            var o = new HashSet<int>();
            for (int i = 0; i < roads.Count; i++)
                if (degree[roads[i].from] == 1 || degree[roads[i].to] == 1) o.Add(i);
            return o;
        }

        /// <summary>A small deterministic hash-based random in 0..1.</summary>
        public static double Hash(int seed, int k)
        {
            unchecked
            {
                uint x = ((uint)seed + 0x9e3779b9u) * 0x85ebca6bu ^ ((uint)k + 0x7f4a7c15u) * 0xc2b2ae35u;
                x ^= x >> 15;
                x *= 0x2c1b3c6du;
                x ^= x >> 12;
                return x / 4294967296.0;
            }
        }

        static List<ParkedCar> ParkCars(TownModel model, List<RoadStroke> roads)
        {
            var cars = new List<ParkedCar>();
            var dead = DeadEnds(roads);
            for (int n = 0; n < roads.Count; n++)
            {
                var r = roads[n];
                if (r.kind != RoadKind.Street || !dead.Contains(n)) continue;
                int seed = n + 1000;
                double total = 0;
                for (int k2 = 1; k2 < r.points.Count; k2++) total += Geo.Dist(r.points[k2], r.points[k2 - 1]);
                int k = 0;
                for (double s = 2.5; s < total - 2.5; s += 2.6)
                {
                    if (Hash(seed, k++) > 0.3) continue;
                    var pts = Geo.SubPolyline(r.points, s, s + 0.2);
                    if (pts.Count < 2) continue;
                    var a = pts[0];
                    var b = pts[pts.Count - 1];
                    double len = Geo.Dist(a, b);
                    if (len == 0) len = 1;
                    double tx = (b.x - a.x) / len;
                    double ty = (b.y - a.y) / len;
                    int side = Hash(seed, k++) < 0.5 ? 1 : -1;
                    double off = r.width / 2 - 0.24;
                    var p = new Vec2(a.x + ty * off * side, a.y - tx * off * side);
                    if (IsWaterTile(model, p) || IsWaterTile(model, a)) continue;
                    cars.Add(new ParkedCar { x = p.x, y = p.y, angle = DetMath.Atan2(ty, tx), variant = (int)Math.Floor(Hash(seed, k++) * 1000) });
                }
            }
            return cars;
        }

        static bool IsWaterTile(TownModel model, Vec2 p)
        {
            int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y);
            if (!Grid.InBounds(x, y, model.width, model.height)) return false;
            return Terrain.IsWater(model.tiles.terrain[Grid.Index(x, y, model.width)]);
        }

        static List<LayoutBridge> FindBridges(TownModel model, List<RoadStroke> roads)
        {
            var o = new List<LayoutBridge>();
            foreach (var r in roads)
            {
                var samples = Geo.SamplePolyline(r.points, 0.2);
                int start = -1;
                for (int k = 0; k <= samples.Count; k++)
                {
                    bool wet = k < samples.Count && IsWaterTile(model, samples[k]);
                    if (wet && start < 0) start = k;
                    if (!wet && start >= 0)
                    {
                        double a = Math.Max(0, Geo.ArcPosition(samples[start], r.points) - 0.6);
                        double b = Geo.ArcPosition(samples[k - 1], r.points) + 0.6;
                        var points = Geo.SubPolyline(r.points, a, b);
                        if (points.Count >= 2) o.Add(new LayoutBridge { kind = r.kind, points = points, width = r.width });
                        start = -1;
                    }
                }
            }
            return o;
        }

        /// <summary>A polyline shifted sideways by d (positive = left of travel direction in y-down space).</summary>
        public static List<Vec2> OffsetPolyline(IReadOnlyList<Vec2> pts, double d)
        {
            var o = new List<Vec2>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[Math.Max(0, i - 1)];
                var b = pts[Math.Min(pts.Count - 1, i + 1)];
                double tx = b.x - a.x;
                double ty = b.y - a.y;
                double len = Numbers.Hypot(tx, ty);
                if (len == 0 || double.IsNaN(len)) len = 1;
                tx /= len;
                ty /= len;
                o.Add(new Vec2(pts[i].x + ty * d, pts[i].y - tx * d));
            }
            return o;
        }

        static SidewalkNetwork SidewalkNetworkOf(List<RoadStroke> roads)
        {
            var net = new SidewalkNetwork();
            for (int road = 0; road < roads.Count; road++)
            {
                var r = roads[road];
                if (r.kind == RoadKind.Path) continue;
                double d = r.width / 2 + SIDEWALK / 2;
                foreach (int side in new[] { 1, -1 })
                {
                    var points = OffsetPolyline(r.points, d * side);
                    var at = new List<double> { 0 };
                    for (int k = 1; k < points.Count; k++) at.Add(at[k - 1] + Geo.Dist(points[k], points[k - 1]));
                    if (at[at.Count - 1] > 0.5) net.paths.Add(new SidewalkPath { points = points, at = at, side = side, road = road });
                }
            }
            var ends = new List<Vec2>();
            foreach (var p in net.paths)
            {
                ends.Add(p.points[0]);
                ends.Add(p.points[p.points.Count - 1]);
            }
            const double reach = 2.6;
            for (int i = 0; i < ends.Count; i++)
            {
                var near = new List<int>();
                for (int j = 0; j < ends.Count; j++)
                    if (j != i && j >> 1 != i >> 1 && Geo.Dist(ends[i], ends[j]) < reach) near.Add(j);
                net.links.Add(near);
            }
            return net;
        }
    }
}

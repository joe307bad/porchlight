// Turns the blueprint into a street scene for the 3D explore view: smoothed
// roads with sidewalks, every building on its footprint facing its street,
// yards with fences, sheds, gardens and driveways, community gardens, parked
// cars (along the parking lane between each road and its sidewalks, and in
// driveways), the sidewalk network townsfolk walk, and (Ports) the docks and
// the airport. Pure.

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
        /// <summary>A set model (index into Catalog.CARS) + 1: a boat, a plane, or a car parked just so; 0 for an everyday car picked by variant, either way round.</summary>
        public int vehicle;
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
        /// <summary>Links that cross a street (the mouth of one, at a junction), keyed by LinkKey(from end, to end).</summary>
        public HashSet<long> crossings = new HashSet<long>();

        public static long LinkKey(int from, int to) => (long)from * 1000003 + to;
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
        public const byte DRIVEWAY = 7;
        /// <summary>The airport's hangars and control tower.</summary>
        public const byte HANGAR = 8;
        /// <summary>A dock's pier, out over the water.</summary>
        public const byte DOCK = 9;
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
        public List<Dock> docks = new List<Dock>();
        /// <summary>A loop round the open water of each lake big enough, for boats to sail (closed: its last point is its first).</summary>
        public List<List<Vec2>> boatLoops = new List<List<Vec2>>();
    }

    public static class Layout
    {
        /// <summary>Width of the sidewalk along each side of a road (tiles).</summary>
        public const double SIDEWALK = Raster.SIDEWALK_WIDTH;
        /// <summary>Width of the parking lane between each side of a road and its sidewalk (tiles).</summary>
        public const double PARKING = Raster.PARKING_WIDTH;
        /// <summary>Most cars parked along the streets.</summary>
        const int MAX_STREET_PARKED = 400;
        /// <summary>Houses this tall or shorter count as small: they get driveways, and gardens outside the inner city.</summary>
        const int SMALL_FLOORS = 2;
        const int SMOOTHING_PASSES = 3;
        const double CORNER_RADIUS = 0.9;
        /// <summary>How far back from a junction a sidewalk may be cut short at the corner (tiles).</summary>
        const double CORNER_TRIM = 4;
        /// <summary>A link between sidewalks may turn you this far and no further (the cosine: about 115°).</summary>
        const double TURN_BACK = -0.42;
        /// <summary>A link over a street closer to running along it than this (the cosine: about 50°) is slantwise.</summary>
        const double SLANTWISE = 0.64;
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
            layout.cars.AddRange(ParkCars(model, roads, layout.use));
            layout.roads = roads;
            layout.bridges = bridges;
            layout.sidewalks = SidewalkNetworkOf(roads);
            Ports.Add(model, layout);
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
            var cars = new List<ParkedCar>();
            var p = model.@params;
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
                    // The inner city (where buildings stand on the sidewalk) has no gardens.
                    bool inner = Geo.Dist(Geo.PolygonCentroid(lot.polygon), C) / model.townRadius < p.innerCity;
                    if (!buildingOnLot.TryGetValue(lot.id, out var b))
                    {
                        bool garden = !inner && builtLots >= 2 && Hash(lot.id, 1) < 0.45;
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
                    bool small = style == HouseStyle.House && floors <= SMALL_FLOORS;
                    if (small && Hash(rndSeed, 3) < p.driveways)
                    {
                        var drive = Driveway(house, yard, w, h, Street);
                        if (drive != null)
                        {
                            foreach (int t in drive) use[t] = Use.DRIVEWAY;
                            if (drive.Count >= 2 && Hash(rndSeed, 4) < 0.75)
                            {
                                // A car in it, beside the house, nose to the street or the house.
                                var (fx, fy) = Grid.DIRS4[(int)house.facing];
                                double cx2 = ((drive[0] % w) + (drive[1] % w)) / 2.0 + 0.5;
                                double cy2 = (drive[0] / w + drive[1] / w) / 2.0 + 0.5;
                                cars.Add(new ParkedCar { x = cx2, y = cy2, angle = DetMath.Atan2(fy, fx), variant = (int)Math.Floor(Hash(rndSeed, 5) * 1000) });
                            }
                        }
                    }
                    if ((style == HouseStyle.House || style == HouseStyle.Workshop) && yard.Count >= 3)
                    {
                        int far = -1;
                        foreach (int t in yard) if (use[t] == Use.YARD && (far < 0 || BackDistance(t, house, w) > BackDistance(far, house, w))) far = t;
                        if (far >= 0 && Hash(rndSeed, 1) < 0.65) use[far] = Use.SHED;
                    }
                    if (small && !inner && Hash(rndSeed, 2) < p.gardens)
                    {
                        // Beds beside and behind the house: yard touching it, never in front.
                        var beds = yard.FindAll(t => use[t] == Use.YARD && Touches(t, footprint, w) && FrontDistance(t, house, w) <= 0);
                        Numbers.StableSort(beds, (a, c) => BackDistance(c, house, w) - BackDistance(a, house, w));
                        int count = Math.Max(2, (int)Math.Round(p.gardens * 6));
                        for (int k = 0; k < Math.Min(count, beds.Count); k++) use[beds[k]] = Use.GARDEN;
                    }
                    if (style == HouseStyle.House || style == HouseStyle.Workshop)
                    {
                        var plot = new HashSet<int>(yard);
                        foreach (var t in footprint) plot.Add(Grid.Index(t.x, t.y, w));
                        foreach (int t in yard) if (use[t] != Use.DRIVEWAY) fences.AddRange(FenceAround(t, plot, w, h, Street));
                    }
                }
                foreach (int t in Parcels.TilesIn(block.polygon, block.holes, w, h)) if (Open(t) && use[t] == Use.NONE) use[t] = Use.GREEN;
                plans.Add(new BlockPlan { blockId = block.id, wanted = wanted, placed = placed });
            }
            return new IsoLayout { houses = houses, use = use, fences = fences, blocks = plans, cars = cars };
        }

        /// <summary>
        /// A strip of yard beside a house, from level with its front out to the
        /// street (on whichever side has room), or null. Ordered from the house out.
        /// </summary>
        static List<int> Driveway(House house, List<int> yard, int w, int h, Func<int, int, bool> street)
        {
            var (fx, fy) = Grid.DIRS4[(int)house.facing];
            int lx = -fy, ly = fx;
            var mine = new HashSet<int>(yard);
            int lo = int.MaxValue, hi = int.MinValue, front = int.MinValue;
            foreach (var t in house.tiles)
            {
                int lat = t.x * lx + t.y * ly;
                lo = Math.Min(lo, lat);
                hi = Math.Max(hi, lat);
                front = Math.Max(front, t.x * fx + t.y * fy);
            }
            List<int> best = null;
            foreach (int lat in new[] { hi + 1, lo - 1 })
            {
                var run = new List<int>();
                bool reaches = false;
                for (int f = front - 1; f <= front + 8; f++)
                {
                    int x = f * fx + lat * lx, y = f * fy + lat * ly;
                    if (!Grid.InBounds(x, y, w, h)) break;
                    if (street(x, y))
                    {
                        reaches = true;
                        break;
                    }
                    int i = Grid.Index(x, y, w);
                    if (!mine.Contains(i))
                    {
                        // Off the lot: fine if the street is just beyond (the kerb's own tile).
                        int nx = x + fx, ny = y + fy;
                        reaches = run.Count > 0 && Grid.InBounds(nx, ny, w, h) && street(nx, ny);
                        break;
                    }
                    run.Add(i);
                }
                if (reaches && run.Count >= 2 && (best == null || run.Count > best.Count)) best = run;
            }
            return best;
        }

        static bool Touches(int t, List<TilePos> footprint, int w)
        {
            int x = t % w, y = t / w;
            foreach (var p in footprint) if (Math.Abs(p.x - x) <= 1 && Math.Abs(p.y - y) <= 1) return true;
            return false;
        }

        /// <summary>How far in front of the house's front a tile is (0 or less: level with it or behind).</summary>
        static double FrontDistance(int t, House house, int w) => -BackDistance(t, house, w);

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

        /// <summary>Cars along the parking lanes, now and then, kept clear of driveways and bridges.</summary>
        static List<ParkedCar> ParkCars(TownModel model, List<RoadStroke> roads, byte[] use)
        {
            var cars = new List<ParkedCar>();
            int w = model.width;
            bool ByDriveway(Vec2 p)
            {
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = (int)Math.Floor(p.x) + dx, y = (int)Math.Floor(p.y) + dy;
                        if (Grid.InBounds(x, y, w, model.height) && use[Grid.Index(x, y, w)] == Use.DRIVEWAY) return true;
                    }
                return false;
            }
            for (int n = 0; n < roads.Count && cars.Count < MAX_STREET_PARKED; n++)
            {
                var r = roads[n];
                if (r.kind == RoadKind.Path) continue;
                double chance = r.kind == RoadKind.Main ? 0.18 : 0.3;
                int seed = n + 1000;
                double total = 0;
                for (int k2 = 1; k2 < r.points.Count; k2++) total += Geo.Dist(r.points[k2], r.points[k2 - 1]);
                int k = 0;
                for (double s = 2.5; s < total - 2.5; s += 2.6)
                {
                    if (Hash(seed, k++) > chance) continue;
                    var pts = Geo.SubPolyline(r.points, s, s + 0.2);
                    if (pts.Count < 2) continue;
                    var a = pts[0];
                    var b = pts[pts.Count - 1];
                    double len = Geo.Dist(a, b);
                    if (len == 0) len = 1;
                    double tx = (b.x - a.x) / len;
                    double ty = (b.y - a.y) / len;
                    int side = Hash(seed, k++) < 0.5 ? 1 : -1;
                    double off = r.width / 2 + PARKING / 2;
                    var p = new Vec2(a.x + ty * off * side, a.y - tx * off * side);
                    if (IsWaterTile(model, p) || IsWaterTile(model, a) || ByDriveway(p)) continue;
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
            return RemoveLoops(o, Math.Max(2, Math.Abs(d) * 8));
        }

        /// <summary>
        /// Cut out the little loops a polyline ties in itself (shifted to the inside
        /// of a bend tighter than the shift, it crosses back over itself): each runs
        /// from the crossing straight on. Only loops up to `longest` round.
        /// </summary>
        public static List<Vec2> RemoveLoops(List<Vec2> pts, double longest)
        {
            var o = new List<Vec2>(pts);
            for (int i = 0; i + 1 < o.Count; i++)
            {
                double round = 0;
                for (int j = i + 1; j + 1 < o.Count; j++)
                {
                    round += Geo.Dist(o[j - 1], o[j]);
                    if (round > longest) break;
                    if (j < i + 2) continue;
                    var hit = Geo.SegmentIntersection(o[i], o[i + 1], o[j], o[j + 1]);
                    if (hit == null) continue;
                    o.RemoveRange(i + 1, j - i);
                    o.Insert(i + 1, hit.Value.point);
                    break;
                }
            }
            return o;
        }

        static SidewalkNetwork SidewalkNetworkOf(List<RoadStroke> roads)
        {
            var net = new SidewalkNetwork();
            var lines = new List<(List<Vec2> points, int side, int road)>();
            double widest = 0;
            for (int road = 0; road < roads.Count; road++)
            {
                var r = roads[road];
                if (r.kind == RoadKind.Path) continue;
                widest = Math.Max(widest, r.width);
                double d = r.width / 2 + PARKING + SIDEWALK / 2;
                foreach (int side in new[] { 1, -1 }) lines.Add((OffsetPolyline(r.points, d * side), side, road));
            }
            // Each sidewalk stops at the corner, where it meets the sidewalk of a
            // street joining at that junction, rather than running on into the road.
            for (int i = 0; i < lines.Count; i++)
            {
                var (points, side, road) = lines[i];
                var at = ArcPositions(points);
                double length = at[at.Count - 1];
                // Half the road, but on a short one at least as far as the corner itself.
                double reachIn = Math.Min(Math.Max(length / 2, widest + PARKING * 2 + SIDEWALK), CORNER_TRIM);
                double from = 0, to = length;
                for (int j = 0; j < lines.Count; j++)
                {
                    var other = lines[j];
                    if (other.road == road) continue;
                    var ro = roads[other.road];
                    bool atStart = ro.from == roads[road].from || ro.to == roads[road].from;
                    bool atEnd = ro.from == roads[road].to || ro.to == roads[road].to;
                    if (!atStart && !atEnd) continue;
                    for (int k = 0; k + 1 < points.Count; k++)
                    {
                        if (at[k] > reachIn && at[k + 1] < length - reachIn) continue;
                        for (int m = 0; m + 1 < other.points.Count; m++)
                        {
                            var hit = Geo.SegmentIntersection(points[k], points[k + 1], other.points[m], other.points[m + 1]);
                            if (hit == null) continue;
                            double sAt = at[k] + (at[k + 1] - at[k]) * hit.Value.t;
                            if (atStart && sAt <= reachIn) from = Math.Max(from, sAt);
                            if (atEnd && sAt >= length - reachIn) to = Math.Min(to, sAt);
                        }
                    }
                }
                var trimmed = from > 0 || to < length ? Geo.SubPolyline(points, from, to) : points;
                if (trimmed.Count < 2) continue;
                var tat = ArcPositions(trimmed);
                if (tat[tat.Count - 1] > 0.5) net.paths.Add(new SidewalkPath { points = trimmed, at = tat, side = side, road = road });
            }
            var ends = new List<Vec2>();
            foreach (var p in net.paths)
            {
                ends.Add(p.points[0]);
                ends.Add(p.points[p.points.Count - 1]);
            }
            // Far enough to cross the mouth of the widest street at a junction.
            double reach = Math.Max(2.6, widest + 2 * PARKING + SIDEWALK + 0.5);
            // Which way you're going arriving at an end, and leaving from one along its sidewalk.
            Vec2 Unit(Vec2 a, Vec2 b)
            {
                double l = Geo.Dist(a, b);
                return l < 1e-9 ? new Vec2(0, 0) : new Vec2((b.x - a.x) / l, (b.y - a.y) / l);
            }
            Vec2 Arriving(int e)
            {
                var pts = net.paths[e >> 1].points;
                return (e & 1) == 0 ? Unit(pts[1], pts[0]) : Unit(pts[pts.Count - 2], pts[pts.Count - 1]);
            }
            Vec2 Leaving(int e) => new Vec2(-Arriving(e).x, -Arriving(e).y);
            // The roads meeting at the junction either end is at.
            int NodeAt(int e)
            {
                var r = roads[net.paths[e >> 1].road];
                return (e & 1) == 0 ? r.from : r.to;
            }
            List<int> RoadsAt(int a, int b)
            {
                var o = new List<int>();
                for (int rd = 0; rd < roads.Count; rd++)
                {
                    var r = roads[rd];
                    if (r.kind == RoadKind.Path) continue;
                    if (r.from == NodeAt(a) || r.to == NodeAt(a) || r.from == NodeAt(b) || r.to == NodeAt(b)) o.Add(rd);
                }
                return o;
            }
            double Dot(Vec2 a, Vec2 b) => a.x * b.x + a.y * b.y;
            for (int i = 0; i < ends.Count; i++)
            {
                var near = new List<int>();
                var arrive = Arriving(i);
                for (int j = 0; j < ends.Count; j++)
                {
                    if (j == i || j >> 1 == i >> 1 || Geo.Dist(ends[i], ends[j]) >= reach) continue;
                    // Round a corner or straight on across a junction, never doubling back.
                    var leave = Leaving(j);
                    if (Dot(arrive, leave) < TURN_BACK) continue;
                    if (Geo.Dist(ends[i], ends[j]) > 0.3 && (Dot(arrive, Unit(ends[i], ends[j])) < TURN_BACK || Dot(Unit(ends[i], ends[j]), leave) < TURN_BACK)) continue;
                    // Over a street only straight across it (the mouth of a side street),
                    // never slantwise along one, from one side of it to the other.
                    bool crosses = false, slantwise = false;
                    foreach (int rd in RoadsAt(i, j))
                    {
                        var pts = roads[rd].points;
                        for (int k = 0; k + 1 < pts.Count; k++)
                        {
                            if (Geo.SegmentIntersection(ends[i], ends[j], pts[k], pts[k + 1]) == null) continue;
                            crosses = true;
                            if (Math.Abs(Dot(Unit(ends[i], ends[j]), Unit(pts[k], pts[k + 1]))) > SLANTWISE) slantwise = true;
                        }
                    }
                    if (slantwise) continue;
                    if (crosses) net.crossings.Add(SidewalkNetwork.LinkKey(i, j));
                    near.Add(j);
                }
                net.links.Add(near);
            }
            return net;
        }

        static List<double> ArcPositions(List<Vec2> points)
        {
            var at = new List<double> { 0 };
            for (int k = 1; k < points.Count; k++) at.Add(at[k - 1] + Geo.Dist(points[k], points[k - 1]));
            return at;
        }
    }
}

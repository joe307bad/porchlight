// TownModel: the single contract between generation and every renderer.
// Plain data only — entities refer to each other by integer id, never by
// reference.

using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Model
{
    public static class Terrain
    {
        public const byte GRASS = 0;
        public const byte DIRT = 1;
        public const byte SAND = 2;
        public const byte WATER_SHALLOW = 3;
        public const byte WATER_DEEP = 4;
        public const byte ROCK = 5;

        public static readonly string[] Names = { "GRASS", "DIRT", "SAND", "WATER_SHALLOW", "WATER_DEEP", "ROCK" };

        public static bool IsWater(byte t) => t == WATER_SHALLOW || t == WATER_DEEP;
    }

    public static class Overlay
    {
        public const byte NONE = 0;
        public const byte ROAD_MAIN = 1;
        public const byte ROAD_STREET = 2;
        public const byte PATH = 3;
        public const byte BRIDGE = 4;
        public const byte PLAZA = 5;
        /// <summary>The airport's grounds: taxiway, apron, hangars and car park, and the grass between.</summary>
        public const byte AIRPORT = 6;
        public const byte RUNWAY = 7;
        /// <summary>The zoo's grounds, inside its fence.</summary>
        public const byte ZOO = 8;

        public static readonly string[] Names = { "NONE", "ROAD_MAIN", "ROAD_STREET", "PATH", "BRIDGE", "PLAZA", "AIRPORT", "RUNWAY", "ZOO" };
    }

    public enum RoadKind { Main, Street, Path }

    /// <summary>
    /// Where a road sits in the town's hierarchy (FHWA's functional classes): arterials carry traffic
    /// across town (the main roads through the centre, and the ring), collectors gather it from the
    /// neighbourhoods (the first streets laid across each piece of land between them), and local
    /// streets reach the doors. Separate from <see cref="RoadKind"/>, which is how a road is built.
    /// </summary>
    public enum RoadClass { Local, Collector, Arterial }

    /// <summary>When a part of town was built (TownPlan): an old gridded core, a garden suburb round it, post-war loops at the edge.</summary>
    public enum Era { Core, GardenSuburb, PostWar }

    /// <summary>The SmartCode transect's zones a town has (TownPlan.Rules): sub-urban, general urban, urban centre, urban core.</summary>
    public enum Transect { T3 = 3, T4 = 4, T5 = 5, T6 = 6 }

    public enum Facing { N = 0, E = 1, S = 2, W = 3 }

    /// <summary>What a building is. New kinds go on the end (a school looks like a hall wherever a kind isn't listed).</summary>
    public enum BuildingKind { House, Shop, Workshop, Church, Hall, School, Supermarket, PetrolStation, FireStation, Clinic }

    public enum LandmarkKind { Plaza, Well, Church, Market }

    public static class Names
    {
        public static string Of(RoadKind k) => k == RoadKind.Main ? "main" : k == RoadKind.Street ? "street" : "path";
        public static string Of(RoadClass k) => k == RoadClass.Arterial ? "arterial" : k == RoadClass.Collector ? "collector" : "local";
        public static string Of(BuildingKind k) => k switch
        {
            BuildingKind.House => "house",
            BuildingKind.Shop => "shop",
            BuildingKind.Workshop => "workshop",
            BuildingKind.Church => "church",
            BuildingKind.School => "school",
            BuildingKind.Supermarket => "supermarket",
            BuildingKind.PetrolStation => "petrol station",
            BuildingKind.FireStation => "fire station",
            BuildingKind.Clinic => "clinic",
            _ => "hall",
        };
        public static string Of(LandmarkKind k) => k switch
        {
            LandmarkKind.Plaza => "plaza",
            LandmarkKind.Well => "well",
            LandmarkKind.Church => "church",
            _ => "market",
        };
        public static string Of(Facing f) => f.ToString();
    }

    public sealed class River
    {
        public int id;
        public List<Vec2> points;
        /// <summary>Width in tiles at each point, same length as points.</summary>
        public List<double> widthAt;
    }

    public sealed class RoadNode
    {
        public int id;
        public Vec2 at;
    }

    public sealed class RoadEdge
    {
        public int id;
        public int from;
        public int to;
        public RoadKind kind;
        /// <summary>Arterial, collector or local (set once the streets are laid; Local until then).</summary>
        public RoadClass cls;
        /// <summary>Polyline from `from` to `to`, endpoints included.</summary>
        public List<Vec2> points;

        public RoadEdge Clone() => new RoadEdge { id = id, from = from, to = to, kind = kind, cls = cls, points = points };
    }

    public sealed class RoadGraph
    {
        public List<RoadNode> nodes = new List<RoadNode>();
        public List<RoadEdge> edges = new List<RoadEdge>();
    }

    public sealed class Bridge
    {
        public int id;
        public int roadEdgeId;
        public Vec2 from;
        public Vec2 to;
    }

    public sealed class Block
    {
        public int id;
        public List<Vec2> polygon;
        /// <summary>Enclosed areas that aren't part of the block (ponds, rock).</summary>
        public List<List<Vec2>> holes;
        /// <summary>When it was built, so its houses can look their age (PlanStage).</summary>
        public Era era;
        /// <summary>Its transect zone: how built up it is (setbacks, heights, lot sizes; TownPlan.Rules).</summary>
        public Transect transect = Transect.T3;
        /// <summary>The neighbourhood it's in (an index into TownModel.neighbourhoods), or -1.</summary>
        public int neighbourhood = -1;
    }

    /// <summary>
    /// A neighbourhood unit (Perry, 1929): blocks bounded by arterials, about a 400 m walk across, with
    /// a park and a school near its middle and shops at its corners, where the arterials meet.
    /// </summary>
    public sealed class Neighbourhood
    {
        public int id;
        public List<int> blockIds = new List<int>();
        /// <summary>The middle of its blocks (by area).</summary>
        public Vec2 middle;
        /// <summary>Tiles of land in its blocks.</summary>
        public int area;
        /// <summary>The block kept as its park (every lot in it left green), or -1.</summary>
        public int parkBlockId = -1;
        /// <summary>Its school (a building id), or -1: set by the buildings stage.</summary>
        public int schoolBuildingId = -1;
        /// <summary>The shops at its corners (building ids): set by the buildings stage.</summary>
        public List<int> cornerShopIds = new List<int>();
    }

    /// <summary>
    /// A car park behind the high street (Gen/Land.cs): a lot left unbuilt and paved, its bays the lot's tiles,
    /// its way in from the street it fronts.
    /// </summary>
    public sealed class CarPark
    {
        public int id;
        public int lotId;
        /// <summary>The road it's entered from (a road edge id).</summary>
        public int edgeId;
    }

    /// <summary>
    /// The industrial wedge (Hoyt's sectors): out along the main road to one gate, where the county highway
    /// gives the cheapest transport, from `from` of the town's radius out. Industry goes there, not round the edge.
    /// </summary>
    public sealed class IndustryWedge
    {
        /// <summary>The gate it reaches (an index into TownModel.gates).</summary>
        public int gate;
        /// <summary>Its bearing from the centre (radians, world axes) and half its angle.</summary>
        public double bearing, halfAngle;
        /// <summary>Where it starts, a share of the town's radius from the centre.</summary>
        public double from;

        public bool Contains(Vec2 centre, double radius, Vec2 at)
        {
            double dx = at.x - centre.x, dy = at.y - centre.y;
            if (radius <= 0 || System.Math.Sqrt(dx * dx + dy * dy) < from * radius) return false;
            double a = System.Math.Atan2(dy, dx) - bearing;
            while (a > System.Math.PI) a -= 2 * System.Math.PI;
            while (a < -System.Math.PI) a += 2 * System.Math.PI;
            return System.Math.Abs(a) <= halfAngle;
        }
    }

    /// <summary>
    /// Where the town's shops and services went (docs/city-research.md §2): the buildings chosen by threshold and
    /// the Huff model, the petrol station at the busiest junction, the fire station central to every lot by road.
    /// Building ids; -1 for none.
    /// </summary>
    public sealed class TownServices
    {
        public List<int> supermarkets = new List<int>();
        public List<int> cornerStores = new List<int>();
        public int petrolStation = -1;
        public int fireStation = -1;
        public int clinic = -1;
        /// <summary>The high street's shops: mixed-use buildings on the arterials through the centre.</summary>
        public List<int> highStreet = new List<int>();
        /// <summary>Households the town was planned for (estimated from its buildings as zoning will count them).</summary>
        public int households;
    }

    public sealed class Lot
    {
        public int id;
        public int blockId;
        public List<Vec2> polygon;
        public int frontageEdgeId;
    }

    public struct TilePos
    {
        public int x;
        public int y;

        public TilePos(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    public sealed class Building
    {
        public int id;
        public int lotId;
        public List<Vec2> footprint;
        /// <summary>Tiles covered by the footprint.</summary>
        public List<TilePos> tiles;
        public BuildingKind kind;
        public int floors;
        public Facing facing;
    }

    public sealed class Landmark
    {
        public int id;
        public LandmarkKind kind;
        public Vec2 at;
        /// <summary>Lots the landmark occupies (plaza lots, the church lot); empty for point landmarks.</summary>
        public List<int> lotIds;
    }

    /// <summary>
    /// The town's airport: a rectangle of tiles (x1, y1 exclusive), its long side
    /// `front` facing the town. Airfield (Gen) says where each part of it is.
    /// </summary>
    public sealed class Airport
    {
        public int x0, y0, x1, y1;
        public Facing front;
        /// <summary>How far along the front (Airfield's u) the road comes in.</summary>
        public double entranceU;
    }

    /// <summary>
    /// The town's zoo: a fenced rectangle of tiles (x1, y1 exclusive), its gate in
    /// the middle of its `front` side, facing the town. Menagerie (Gen) says where
    /// its paths and pens are.
    /// </summary>
    public sealed class Zoo
    {
        public int x0, y0, x1, y1;
        public Facing front;
    }

    /// <summary>A boat tied up at a dock: where it floats, and which way its bow points.</summary>
    public struct Mooring
    {
        public double x, y;
        public Facing facing;
    }

    /// <summary>
    /// A pier out from the shore over a lake, with the boats moored along it.
    /// Piers (Gen) says where its deck and each boat's footprint are.
    /// </summary>
    public sealed class Dock
    {
        public int id;
        /// <summary>The dry tile it starts from.</summary>
        public TilePos shore;
        /// <summary>The pier's tiles, from the shore out.</summary>
        public List<TilePos> tiles;
        /// <summary>Which way it runs out over the water.</summary>
        public Facing facing;
        public List<Mooring> boats;
    }

    /// <summary>Derived fields kept for the debug views.</summary>
    public sealed class DebugFields
    {
        /// <summary>Upstream tile count draining through each tile.</summary>
        public float[] flow;
        /// <summary>A* cost of entering each tile for main roads; Infinity where blocked.</summary>
        public float[] roadCost;
    }

    public sealed class TileGrid
    {
        public int width;
        public int height;
        public byte[] terrain;
        public byte[] overlay;
        /// <summary>-1 or index into buildings.</summary>
        public int[] buildingId;
        /// <summary>Quantized height steps (always 0: the town is flat).</summary>
        public byte[] elevation;
        public byte[] walkable;

        public static TileGrid Create(int width, int height)
        {
            int n = width * height;
            var g = new TileGrid
            {
                width = width,
                height = height,
                terrain = new byte[n],
                overlay = new byte[n],
                buildingId = new int[n],
                elevation = new byte[n],
                walkable = new byte[n],
            };
            for (int i = 0; i < n; i++)
            {
                g.terrain[i] = Terrain.GRASS;
                g.buildingId[i] = -1;
            }
            return g;
        }
    }

    public sealed class TownModel
    {
        public string seed;
        public GenParams @params;
        public int width;
        public int height;

        /// <summary>0..1, row-major.</summary>
        public float[] heightmap;
        public double waterLevel;

        public Vec2 center;
        public double townRadius;
        /// <summary>A big town's second centre (multiple nuclei: by its airport or its waterfront), or null.</summary>
        public Vec2? secondCentre;
        /// <summary>The town's neighbourhoods (PlanStage); every block is in one.</summary>
        public List<Neighbourhood> neighbourhoods = new List<Neighbourhood>();
        public List<Vec2> gates = new List<Vec2>();

        public List<River> rivers = new List<River>();
        public RoadGraph roads = new RoadGraph();
        public List<Bridge> bridges = new List<Bridge>();
        public List<Block> blocks = new List<Block>();
        public List<Lot> lots = new List<Lot>();
        public List<Building> buildings = new List<Building>();
        public List<Landmark> landmarks = new List<Landmark>();
        /// <summary>Null on a map with no room for one.</summary>
        public Airport airport;
        /// <summary>Null on a map with no room for one.</summary>
        public Zoo zoo;
        /// <summary>What a tile's land is worth, 0..1 (LandStage): centrality, main-road frontage, corners, parks, minus industry.</summary>
        public float[] landValue;
        /// <summary>The industrial wedge, or null (a town too small for one).</summary>
        public IndustryWedge industry;
        /// <summary>Every park block: each neighbourhood's own, and those added to give the town its park land (LandStage).</summary>
        public List<int> parkBlocks = new List<int>();
        /// <summary>Car parks behind the high street (LandStage).</summary>
        public List<CarPark> carParks = new List<CarPark>();
        /// <summary>The shops and services the buildings stage placed.</summary>
        public TownServices services = new TownServices();

        /// <summary>The park blocks, as a set: each neighbourhood's own and any more (parkBlocks).</summary>
        public HashSet<int> ParkBlockSet()
        {
            var o = new HashSet<int>(parkBlocks);
            foreach (var hood in neighbourhoods) if (hood.parkBlockId >= 0) o.Add(hood.parkBlockId);
            return o;
        }

        /// <summary>Lots that are car parks.</summary>
        public HashSet<int> CarParkLots()
        {
            var o = new HashSet<int>();
            foreach (var c in carParks) o.Add(c.lotId);
            return o;
        }

        /// <summary>Piers out over the lakes, each with its boats (DocksStage).</summary>
        public List<Dock> docks = new List<Dock>();

        public TileGrid tiles;
        public Vec2 spawn;

        /// <summary>
        /// A stretch of highway, the junction or a plot (County/RoadSpaces), not a town: null in a
        /// town. Tiles marked here aren't this space's to draw: 1, the stretch next to this one draws
        /// them (they overlap a little, so a car is on both while it crosses); COUNTRYSIDE, off the
        /// road's verge, the county's hills are there and no car goes.
        /// </summary>
        public byte[] foreign;
        public const byte COUNTRYSIDE = 2;

        /// <summary>Off a road space's verge (the countryside's, not this space's): no driving or walking there.</summary>
        public bool OffVerge(double x, double y)
        {
            if (foreign == null) return false;
            int tx = (int)System.Math.Floor(x), ty = (int)System.Math.Floor(y);
            return tx >= 0 && ty >= 0 && tx < width && ty < height && foreign[ty * width + tx] == COUNTRYSIDE;
        }
        /// <summary>A road space's name for signs ("North road"), or null.</summary>
        public string roadName;
        public bool IsRoad => roadName != null;

        public DebugFields fields;

        /// <summary>A model with every layer present but empty; pipeline stages fill it in.</summary>
        public static TownModel CreateEmpty(string seed, GenParams p)
        {
            int w = (int)p.width, h = (int)p.height;
            return new TownModel
            {
                seed = seed,
                @params = p.Clone(),
                width = w,
                height = h,
                heightmap = new float[w * h],
                waterLevel = 0,
                center = new Vec2(w / 2.0, h / 2.0),
                townRadius = 0,
                tiles = TileGrid.Create(w, h),
                spawn = new Vec2(w / 2.0, h / 2.0),
                fields = new DebugFields { flow = new float[w * h], roadCost = new float[w * h] },
            };
        }

        /// <summary>A shallow copy, for a stage that replaces some layers.</summary>
        public TownModel With() => (TownModel)MemberwiseClone();
    }
}

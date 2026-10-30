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

        public static readonly string[] Names = { "NONE", "ROAD_MAIN", "ROAD_STREET", "PATH", "BRIDGE", "PLAZA", "AIRPORT", "RUNWAY" };
    }

    public enum RoadKind { Main, Street, Path }

    public enum Facing { N = 0, E = 1, S = 2, W = 3 }

    public enum BuildingKind { House, Shop, Workshop, Church, Hall }

    public enum LandmarkKind { Plaza, Well, Church, Market }

    public static class Names
    {
        public static string Of(RoadKind k) => k == RoadKind.Main ? "main" : k == RoadKind.Street ? "street" : "path";
        public static string Of(BuildingKind k) => k switch
        {
            BuildingKind.House => "house",
            BuildingKind.Shop => "shop",
            BuildingKind.Workshop => "workshop",
            BuildingKind.Church => "church",
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
        /// <summary>Polyline from `from` to `to`, endpoints included.</summary>
        public List<Vec2> points;

        public RoadEdge Clone() => new RoadEdge { id = id, from = from, to = to, kind = kind, points = points };
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
        /// <summary>Piers out over the lakes, each with its boats (DocksStage).</summary>
        public List<Dock> docks = new List<Dock>();

        public TileGrid tiles;
        public Vec2 spawn;

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

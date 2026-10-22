using System;
using System.Collections.Generic;

namespace Porchlight.Shared.Model
{
    // These are the wire and storage format. Keep them as plain classes with public get/set properties:
    // System.Text.Json (server) and Newtonsoft (Unity) both serialise them without attributes, and
    // IL2CPP can construct them (link.xml in the Unity project keeps them from being stripped).
    //
    // Coordinates are metres on the ground plane, with the town centred on the origin and spanning
    // -Size/2..Size/2 on both axes. Rotations are degrees about +Y in Unity's convention.

    public sealed class TownDto
    {
        /// <summary>
        /// Bump this when the layout format changes, and add a migration (see Rules.TownTiles.Upgrade,
        /// which the server and clients run on every town they load). 2: the ground is tiles.
        /// </summary>
        public const int CurrentSchemaVersion = 2;

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>Optimistic-concurrency token. The server rejects a save whose version isn't current.</summary>
        public int Version { get; set; }

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Side length of the square buildable area, in metres.</summary>
        public float Size { get; set; } = TownLimits.DefaultSize;

        public List<BuildingDto> Buildings { get; set; } = new List<BuildingDto>();

        /// <summary>
        /// The ground: a square of 4 m tiles, each with a level and a surface (grass, street, path, plaza
        /// or garden). See <see cref="Rules.TownTiles"/>. Null only in towns saved before tiles, which are
        /// converted as they load.
        /// </summary>
        public TileMapDto Tiles { get; set; }

        /// <summary>Before tiles: roads as a graph. Only read to convert old towns (Rules.TownTiles.Upgrade), then emptied.</summary>
        public RoadNetworkDto Roads { get; set; } = new RoadNetworkDto();

        /// <summary>Before tiles: a heightmap. Only read to convert old towns, then dropped.</summary>
        public TerrainDto Terrain { get; set; }

        /// <summary>
        /// The terrain builder's shapes, floating anywhere in the air or half buried. They don't touch
        /// the ground until the overlay is applied (<see cref="Rules.TerrainShapes.Overlay"/>), which
        /// raises the tiles under them to their tops. Empty for towns that never used the builder.
        /// </summary>
        public List<TerrainShapeDto> TerrainShapes { get; set; } = new List<TerrainShapeDto>();

        /// <summary>How much grass, flowers and trees grow on their own (see <see cref="Rules.Foliage"/>).</summary>
        public FoliageDto Foliage { get; set; } = new FoliageDto();

        /// <summary>
        /// Where foliage grows: rectangles painted in the builder, in order. A point grows plants if the
        /// last area covering it was painted rather than erased. Empty: nothing grows.
        /// </summary>
        public List<FoliageAreaDto> FoliageAreas { get; set; } = new List<FoliageAreaDto>();

        /// <summary>A copy sharing everything with this town but its tiles (a preview of an edit, say). Not for editing: its lists are this town's.</summary>
        public TownDto WithTiles(TileMapDto tiles)
        {
            var copy = (TownDto)MemberwiseClone();
            copy.Tiles = tiles;
            return copy;
        }

        public TownDto Clone()
        {
            var copy = (TownDto)MemberwiseClone();
            copy.Buildings = Buildings.ConvertAll(b => b.Clone());
            copy.Tiles = Tiles?.Clone();
            copy.Roads = Roads?.Clone();
            copy.Terrain = Terrain?.Clone();
            copy.TerrainShapes = TerrainShapes?.ConvertAll(s => s?.Clone());
            copy.Foliage = Foliage?.Clone();
            copy.FoliageAreas = FoliageAreas?.ConvertAll(a => a?.Clone());
            return copy;
        }
    }

    /// <summary>A rectangle of ground, X0..X1 by Z0..Z1, where foliage grows, or (with <see cref="Erase"/>) doesn't.</summary>
    public sealed class FoliageAreaDto
    {
        public float X0 { get; set; }
        public float Z0 { get; set; }
        public float X1 { get; set; }
        public float Z1 { get; set; }
        public bool Erase { get; set; }

        public bool Contains(float x, float z) => x >= X0 && x <= X1 && z >= Z0 && z <= Z1;

        public FoliageAreaDto Clone() => (FoliageAreaDto)MemberwiseClone();
    }

    /// <summary>
    /// How heavy the town's foliage is: an overall amount and one each for trees (and bushes), flowers,
    /// and grass and weeds. 1 is the usual amount, 0 none, up to <see cref="Max"/>.
    /// </summary>
    public sealed class FoliageDto
    {
        public const float Max = 2f;

        public float Overall { get; set; } = 1f;
        public float Trees { get; set; } = 1f;
        public float Flowers { get; set; } = 1f;
        public float Grass { get; set; } = 1f;

        public FoliageDto Clone() => (FoliageDto)MemberwiseClone();
    }

    /// <summary>
    /// A shape placed with the terrain builder: a unit <see cref="Kind"/> (see <see cref="Rules.TerrainShapes"/>)
    /// stretched by the scales, turned by the rotations, and centred on (X, Y, Z). Rotations are Unity's
    /// Euler angles in degrees (applied Z, then X, then Y), and Y is metres above sea level, like the terrain.
    /// </summary>
    public sealed class TerrainShapeDto
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";

        /// <summary>What the layers panel calls it, like "Cube 3".</summary>
        public string Name { get; set; } = "";

        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }
        public float ScaleX { get; set; } = 1f;
        public float ScaleY { get; set; } = 1f;
        public float ScaleZ { get; set; } = 1f;

        /// <summary>
        /// The ground is to take this shape's form. The builder previews it, and makes it the ground
        /// (<see cref="Editing.ApplyTerrainOverlayCommand"/>, which turns this back off) on the way out.
        /// </summary>
        public bool Overlay { get; set; }

        public TerrainShapeDto Clone() => (TerrainShapeDto)MemberwiseClone();
    }

    /// <summary>
    /// The town's ground as tiles, <see cref="Cells"/> a side, row by row from -Z, each row from -X.
    /// <see cref="Levels"/> are whole metres above (or below) the countryside; <see cref="Surfaces"/>
    /// are <see cref="TileSurface"/> values. <see cref="Hills"/>, when there is one, marks (with 1) the
    /// grass tiles that slope smoothly to their neighbours instead of standing flat behind walls; it's
    /// null until the first hill goes in. See <see cref="Rules.TownTiles"/>.
    /// </summary>
    public sealed class TileMapDto
    {
        public int Cells { get; set; }
        public List<int> Levels { get; set; } = new List<int>();
        public List<int> Surfaces { get; set; } = new List<int>();
        public List<int> Hills { get; set; }

        public TileMapDto Clone() => new TileMapDto
        {
            Cells = Cells, Levels = new List<int>(Levels), Surfaces = new List<int>(Surfaces), Hills = Hills == null ? null : new List<int>(Hills),
        };
    }

    /// <summary>What a tile's top is.</summary>
    public enum TileSurface
    {
        Grass = 0,
        Street = 1,
        Path = 2,
        Plaza = 3,
        Garden = 4,
    }

    /// <summary>
    /// Before tiles: a heightmap over the town: <see cref="Cells"/> squares a side, so (Cells + 1)² corner heights,
    /// row by row from -Z, each row from -X. Heights are whole centimetres, so saved data stays small
    /// and every client agrees exactly. See <see cref="Rules.TownTerrain"/> for how it's read and shaped.
    /// </summary>
    public sealed class TerrainDto
    {
        public int Cells { get; set; }
        public List<int> Heights { get; set; } = new List<int>();

        public TerrainDto Clone() => new TerrainDto { Cells = Cells, Heights = new List<int>(Heights) };
    }

    public sealed class BuildingDto
    {
        /// <summary>Generated by whoever creates the building, so edits can be replayed and merged later.</summary>
        public string Id { get; set; } = "";

        public string TypeId { get; set; } = "";
        public float X { get; set; }
        public float Z { get; set; }
        public float RotationDeg { get; set; }

        /// <summary>
        /// The inside of a building that has one (only cottages so far). Null until it's first edited,
        /// which means the default layout from <see cref="Rules.CottageInterior.DefaultFor"/>. New
        /// cottages get a generated one (<see cref="Rules.CottageGenerator"/>) as they're placed.
        /// </summary>
        public InteriorDto Interior { get; set; }

        /// <summary>
        /// The number on the front of a cottage, handed out in order as they're placed (one more than
        /// the highest in the town). 0 for buildings without one.
        /// </summary>
        public int HouseNumber { get; set; }

        public BuildingDto Clone()
        {
            var copy = (BuildingDto)MemberwiseClone();
            copy.Interior = Interior?.Clone();
            return copy;
        }
    }

    /// <summary>
    /// The inside of a cottage, in the building's own frame: metres from its centre, +X to its right
    /// and +Z out of its front door, with rotations in multiples of 90 degrees. See
    /// <see cref="Rules.CottageInterior"/> for the layout and <see cref="Rules.InteriorRules"/> for what's allowed.
    /// </summary>
    public sealed class InteriorDto
    {
        public StairsDto Stairs { get; set; } = new StairsDto();

        /// <summary>Ground floor first. Starts empty (not pre-filled) so JSON readers that add to lists don't double up.</summary>
        public List<InteriorFloorDto> Floors { get; set; } = new List<InteriorFloorDto>();

        public InteriorDto Clone() => new InteriorDto
        {
            Stairs = Stairs?.Clone(),
            Floors = Floors?.ConvertAll(f => f?.Clone()),
        };
    }

    /// <summary>
    /// A straight flight from the ground floor to the one above. (X, Z) is its middle; it climbs
    /// towards its local +Z, so at 0 degrees the bottom step is at the -Z end.
    /// </summary>
    public sealed class StairsDto
    {
        public float X { get; set; }
        public float Z { get; set; }
        public float RotationDeg { get; set; }

        public StairsDto Clone() => (StairsDto)MemberwiseClone();
    }

    public sealed class InteriorFloorDto
    {
        public List<InteriorWallDto> Walls { get; set; } = new List<InteriorWallDto>();
        public List<OpeningDto> Openings { get; set; } = new List<OpeningDto>();
        public List<FurnitureDto> Items { get; set; } = new List<FurnitureDto>();

        public InteriorFloorDto Clone() => new InteriorFloorDto
        {
            Walls = Walls?.ConvertAll(w => w?.Clone()),
            Openings = Openings?.ConvertAll(o => o?.Clone()),
            Items = Items?.ConvertAll(i => i?.Clone()),
        };
    }

    /// <summary>An inside wall along its centre line, which runs along either X or Z.</summary>
    public sealed class InteriorWallDto
    {
        public string Id { get; set; } = "";
        public float X0 { get; set; }
        public float Z0 { get; set; }
        public float X1 { get; set; }
        public float Z1 { get; set; }

        public InteriorWallDto Clone() => (InteriorWallDto)MemberwiseClone();
    }

    /// <summary>
    /// A door or window in a wall. <see cref="WallId"/> is an inside wall's id or one of the outside
    /// walls (<see cref="Rules.CottageInterior.FrontWall"/> and friends). <see cref="At"/> is where its
    /// middle is along the wall: the X coordinate for a wall running along X, otherwise Z.
    /// </summary>
    public sealed class OpeningDto
    {
        public string Id { get; set; } = "";
        public string TypeId { get; set; } = "";
        public string WallId { get; set; } = "";
        public float At { get; set; }

        public OpeningDto Clone() => (OpeningDto)MemberwiseClone();
    }

    /// <summary>
    /// A piece of furniture. (X, Z) is its middle and it faces its local +Z. A TV standing on a
    /// dresser names it in <see cref="OnId"/>; everything else stands on the floor or hangs on a wall.
    /// </summary>
    public sealed class FurnitureDto
    {
        public string Id { get; set; } = "";
        public string TypeId { get; set; } = "";
        public float X { get; set; }
        public float Z { get; set; }
        public float RotationDeg { get; set; }
        public string OnId { get; set; }

        public FurnitureDto Clone() => (FurnitureDto)MemberwiseClone();
    }

    /// <summary>
    /// Roads form a graph: nodes are junctions or ends, and segments join two nodes. Segments are
    /// straight for now. Curves can be added later as optional control points on the segment.
    /// </summary>
    public sealed class RoadNetworkDto
    {
        public List<RoadNodeDto> Nodes { get; set; } = new List<RoadNodeDto>();
        public List<RoadSegmentDto> Segments { get; set; } = new List<RoadSegmentDto>();

        public RoadNetworkDto Clone() => new RoadNetworkDto
        {
            Nodes = Nodes.ConvertAll(n => n.Clone()),
            Segments = Segments.ConvertAll(s => s.Clone()),
        };
    }

    public sealed class RoadNodeDto
    {
        public string Id { get; set; } = "";
        public float X { get; set; }
        public float Z { get; set; }

        public RoadNodeDto Clone() => (RoadNodeDto)MemberwiseClone();
    }

    public sealed class RoadSegmentDto
    {
        public string Id { get; set; } = "";
        public string TypeId { get; set; } = "";
        public string StartNodeId { get; set; } = "";
        public string EndNodeId { get; set; } = "";

        public RoadSegmentDto Clone() => (RoadSegmentDto)MemberwiseClone();
    }

    /// <summary>A row in the town list. The server stores the metrics as columns so listing towns is cheap.</summary>
    public sealed class TownSummaryDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Version { get; set; }
        public int Population { get; set; }
        public int Jobs { get; set; }
        public int BuildingCount { get; set; }
        public float RoadLengthMeters { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class CreateTownRequest
    {
        public string Name { get; set; } = "";
        public float? Size { get; set; }
    }

    public sealed class ApiError
    {
        public string Error { get; set; } = "";
        public List<string> Details { get; set; } = new List<string>();
    }

    public static class TownLimits
    {
        public const float MinSize = 64f;
        public const float MaxSize = 1024f;
        public const float DefaultSize = 384f;

        public const int MaxNameLength = 64;
        public const int MaxBuildings = 5000;
        public const int MaxHouseNumber = 99999;
        public const int MaxTerrainShapes = 200;
        public const int MaxFoliageAreas = 500;
        public const int MaxShapeNameLength = 32;

        /// <summary>Positions are rounded to this grid so saved data stays small and deterministic.</summary>
        public const float PositionQuantum = 0.05f;

        /// <summary>A building counts as connected when a street, path or plaza tile is within this distance of its walls.</summary>
        public const float RoadConnectionSlack = 2.5f;
    }

    public static class TownFactory
    {
        public static TownDto CreateEmpty(string id, string name, float size = TownLimits.DefaultSize) => new TownDto
        {
            Id = id,
            Name = name,
            Version = 1,
            Size = size,
            Tiles = Rules.TownTiles.Flat(size),
        };
    }

    public static class Ids
    {
        public static string New() => Guid.NewGuid().ToString("N");
    }
}

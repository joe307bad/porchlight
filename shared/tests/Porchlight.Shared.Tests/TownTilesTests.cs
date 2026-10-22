using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class TownTilesTests
{
    private static TownDto NewTown() => TownFactory.CreateEmpty(Ids.New(), "Tiles");

    private static EditResult Raise(TownDto town, float x, float z, float levels, float halfSize = 2f) =>
        TownEditor.Apply(town, new ShapeTerrainCommand { X = x, Z = z, PadHalfSize = halfSize, Delta = levels });

    private static EditResult Paint(TownDto town, Vec2 a, Vec2 b, string surface, bool line = true) =>
        TownEditor.Apply(town, new PaintTilesCommand { Cells = line ? TownTiles.Line(town, a, b) : TownTiles.Area(town, a, b), SurfaceId = surface, Line = line });

    private static float H(TownDto town, float x, float z) => TownTerrain.HeightAt(town, new Vec2(x, z));

    [Fact]
    public void NewTownsAreFlatGrass()
    {
        var town = NewTown();
        Assert.True(TownTiles.Has(town));
        Assert.Equal(TownTiles.CellsFor(town.Size), town.Tiles.Cells);
        Assert.All(town.Tiles.Levels, l => Assert.Equal(0, l));
        Assert.All(town.Tiles.Surfaces, s => Assert.Equal((int)TileSurface.Grass, s));
        Assert.Equal(0f, H(town, 13f, -7f));
        Assert.Equal(0f, H(town, 1000f, 0f));
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void RaisingMovesWholeTilesInTheSquare()
    {
        var town = NewTown();

        // A block: just the tile at (2, 2), a level at a time.
        Assert.True(Raise(town, 2, 2, 1).Ok);
        Assert.True(Raise(town, 2, 2, 1).Ok);
        Assert.Equal(2f, H(town, 1f, 3f));
        Assert.Equal(0f, H(town, 5f, 3f));

        // A larger square about a grid point: the four tiles around it.
        Assert.True(Raise(town, 40, 40, -1, halfSize: 4f).Ok);
        foreach (var p in new[] { new Vec2(38, 38), new Vec2(42, 38), new Vec2(38, 42), new Vec2(42, 42) }) Assert.Equal(-1f, H(town, p.X, p.Z));
        Assert.Equal(0f, H(town, 46, 40));

        // The limits.
        Assert.True(Raise(town, 100, 100, 10).Ok);
        Assert.True(Raise(town, 100, 100, 10).Ok);
        Assert.True(Raise(town, 100, 100, 10).Ok);
        Assert.Equal(TownTiles.MaxLevel, H(town, 102, 102));
        Assert.Equal("The ground can't go any higher here", Raise(town, 100, 100, 1).Error);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void HousesHoldTheirTilesUnlessTheyRideAlong()
    {
        var town = NewTown();
        Assert.True(TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = "h", TypeId = "cottage", X = 2, Z = 2 }).Ok);
        var house = town.Buildings[0];

        // A square half over the house: its tiles stay, the rest rise.
        Assert.True(Raise(town, 8, 2, 1, halfSize: 8f).Ok);
        Assert.Equal(0f, TownTerrain.BaseHeight(town, house));
        Assert.Equal(1f, H(town, 14, 2));

        // A square over all of it: the house rides up with the ground.
        Assert.True(Raise(town, 0, 0, 1, halfSize: 16f).Ok);
        Assert.Equal(1f, TownTerrain.BaseHeight(town, house));
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void PlacingLevelsTheTilesUnderABuilding()
    {
        var town = NewTown();

        // A step in the ground across where the house goes.
        Assert.True(Raise(town, 8, 2, 2, halfSize: 4f).Ok);
        Assert.True(TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = "h", TypeId = "cottage", X = 2, Z = 2 }).Ok);
        Assert.True(TownRules.TryFootprint(town.Buildings[0], out var footprint));
        var level = TownTiles.BaseLevel(town, footprint);
        foreach (var (i, j) in TownTiles.Under(town, footprint)) Assert.Equal(level, TownTiles.Level(town, i, j));
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void StreetsRampAndPathsClimbBySteps()
    {
        var town = NewTown();

        // Two tiles north-south: (2, 2) at level 0 and (2, 6) at level 1.
        Assert.True(Raise(town, 2, 6, 1).Ok);
        Assert.True(Paint(town, new Vec2(2, 2), new Vec2(2, 6), "street").Ok);

        // The lower street tile ramps the whole way up to the next.
        Assert.Equal(0f, H(town, 2, 0.01f), 2);
        Assert.Equal(0.5f, H(town, 2, 2), 2);
        Assert.Equal(1f, H(town, 2, 3.99f), 2);
        Assert.Equal(1f, H(town, 2, 6), 2);

        // A path does it by a flight of steps against the higher side, with grass either side.
        Assert.True(Raise(town, 30, 6, 1).Ok);
        Assert.True(Paint(town, new Vec2(30, 2), new Vec2(30, 6), "path").Ok);
        Assert.Equal(0f, H(town, 30, 1), 2);
        Assert.Equal(0f, H(town, 30, 4f - TownTiles.StairRun - 0.01f), 2);
        Assert.Equal(0.5f, H(town, 30, 4f - TownTiles.StairRun * 0.5f), 2);
        Assert.Equal(0f, H(town, 31.9f, 3.5f), 2); // beside the flight
        Assert.True(TownTiles.TryCell(town, new Vec2(30, 2), out var i, out var j));
        Assert.True(TownTiles.TryClimb(town, i, j, out var side));
        Assert.Equal(TileSide.North, side);
    }

    [Fact]
    public void PaintedLinesStayClimbable()
    {
        var town = NewTown();
        Assert.True(Raise(town, 18, 2, 4).Ok);
        Assert.True(Paint(town, new Vec2(2, 2), new Vec2(34, 2), "street").Ok);

        var line = TownTiles.Line(town, new Vec2(2, 2), new Vec2(34, 2));
        for (var k = 1; k < line.Count; k++) Assert.InRange(town.Tiles.Levels[line[k]] - town.Tiles.Levels[line[k - 1]], -1, 1);
        Assert.All(line, k => Assert.Equal((int)TileSurface.Street, town.Tiles.Surfaces[k]));
    }

    [Fact]
    public void LinesAreJoinedSideToSide()
    {
        var town = NewTown();
        var line = TownTiles.Line(town, new Vec2(-30, -10), new Vec2(25, 17));
        var n = town.Tiles.Cells;
        for (var k = 1; k < line.Count; k++)
        {
            var (i0, j0) = (line[k - 1] % n, line[k - 1] / n);
            var (i1, j1) = (line[k] % n, line[k] / n);
            Assert.Equal(1, System.Math.Abs(i1 - i0) + System.Math.Abs(j1 - j0));
        }

        Assert.Equal(6 * 4, TownTiles.Area(town, new Vec2(0, 0), new Vec2(22, 13)).Count);
    }

    [Fact]
    public void OldTownsConvertToTiles()
    {
        // A town as saved before tiles: a 4 m heightmap with a 3 m hill, a street and a path, and a house.
        var town = new TownDto { Id = Ids.New(), Name = "Old", Version = 3, SchemaVersion = 1, Size = TownLimits.DefaultSize };
        var oldCells = (int)(town.Size / 4f);
        var heights = new List<int>();
        for (var j = 0; j <= oldCells; j++)
        for (var i = 0; i <= oldCells; i++)
            heights.Add(Math.Abs(i - oldCells / 2 - 10) <= 3 && Math.Abs(j - oldCells / 2) <= 3 ? 300 : 0);
        town.Terrain = new TerrainDto { Cells = oldCells, Heights = heights };
        town.Roads.Nodes.AddRange(new[]
        {
            new RoadNodeDto { Id = "a", X = -40, Z = -20 }, new RoadNodeDto { Id = "b", X = 40, Z = -20 },
            new RoadNodeDto { Id = "c", X = 0, Z = -20 }, new RoadNodeDto { Id = "d", X = 0, Z = -60 },
        });
        town.Roads.Segments.Add(new RoadSegmentDto { Id = "s", TypeId = "street", StartNodeId = "a", EndNodeId = "b" });
        town.Roads.Segments.Add(new RoadSegmentDto { Id = "p", TypeId = "path", StartNodeId = "c", EndNodeId = "d" });
        town.Buildings.Add(new BuildingDto { Id = "h", TypeId = "cottage", X = 40, Z = 2 });

        Assert.True(TownTiles.Upgrade(town));
        Assert.True(TownTiles.Has(town));
        Assert.Null(town.Terrain);
        Assert.Empty(town.Roads.Segments);
        Assert.Equal(TownDto.CurrentSchemaVersion, town.SchemaVersion);

        // The hill, the street and the path came across.
        Assert.Equal(3f, H(town, 40, 0), 1);
        Assert.True(TownTiles.TryCell(town, new Vec2(10, -20), out var si, out var sj));
        Assert.Equal(TileSurface.Street, TownTiles.Surface(town, si, sj));
        Assert.True(TownTiles.TryCell(town, new Vec2(0, -40), out var pi, out var pj));
        Assert.Equal(TileSurface.Path, TownTiles.Surface(town, pi, pj));

        // The house stands on level tiles, and the town is valid.
        Assert.True(TownRules.ValidateTown(town).IsValid, string.Join("; ", TownRules.ValidateTown(town).Errors));
        Assert.False(TownTiles.Upgrade(town));
    }

    [Fact]
    public void ConvertingTidiesLoneBumpsAndPits()
    {
        // A flat old town with one 4 m square of ground a metre up and another a metre down, and a
        // broad 2 m rise that should stay.
        var town = new TownDto { Id = Ids.New(), Name = "Bumpy", SchemaVersion = 1, Size = TownLimits.DefaultSize };
        var oldCells = (int)(town.Size / 4f);
        var heights = new List<int>();
        for (var j = 0; j <= oldCells; j++)
        for (var i = 0; i <= oldCells; i++)
        {
            var x = -town.Size * 0.5f + i * 4f;
            var z = -town.Size * 0.5f + j * 4f;
            heights.Add(x >= 0 && x <= 4 && z >= 0 && z <= 4 ? 100 : x >= 40 && x <= 44 && z >= 0 && z <= 4 ? -100 : x >= 80 && x <= 120 && z >= 80 && z <= 120 ? 200 : 0);
        }

        town.Terrain = new TerrainDto { Cells = oldCells, Heights = heights };
        Assert.True(TownTiles.Upgrade(town));
        Assert.Equal(0f, H(town, 2, 2));
        Assert.Equal(0f, H(town, 42, 2));
        Assert.Equal(2f, H(town, 100, 100));
    }

    [Fact]
    public void ValidationCatchesBrokenTiles()
    {
        var town = NewTown();
        town.Tiles.Levels[5] = 99;
        town.Tiles.Surfaces[6] = 42;
        var errors = TownRules.ValidateTown(town).Errors;
        Assert.Contains(errors, e => e.Contains("levels"));
        Assert.Contains(errors, e => e.Contains("surface"));

        var shortMap = NewTown();
        shortMap.Tiles.Levels.RemoveAt(0);
        Assert.Contains(TownRules.ValidateTown(shortMap).Errors, e => e.Contains("a side"));
    }

    [Fact]
    public void TheStockTownIsATidyValidTown()
    {
        var town = Porchlight.Shared.Content.StockTown.Create();
        Assert.True(TownRules.ValidateTown(town).IsValid, string.Join("; ", TownRules.ValidateTown(town).Errors));
        Assert.True(town.Buildings.Count > 20, $"only {town.Buildings.Count} buildings");
        Assert.True(TownTiles.TryCell(town, Vec2.Zero, out var i, out var j));
        Assert.Equal(TileSurface.Plaza, TownTiles.Surface(town, i, j));
        Assert.True(TownTerrain.HeightAt(town, new Vec2(100f, 170f)) >= 1f);

        // The menus lie on the plaza (±28 by -19..21, plus a margin), so it's flat paving all under them.
        for (var z = -21f; z <= 23f; z += 1f)
        {
            for (var x = -30f; x <= 30f; x += 1f)
            {
                Assert.True(TownTiles.TryCell(town, new Vec2(x, z), out i, out j));
                Assert.Equal(TileSurface.Plaza, TownTiles.Surface(town, i, j));
                Assert.Equal(0, TownTiles.Level(town, i, j));
            }
        }
        Assert.True(Porchlight.Shared.Metrics.TownMetrics.Compute(town).ConnectedBuildingCount > 20);
    }
}

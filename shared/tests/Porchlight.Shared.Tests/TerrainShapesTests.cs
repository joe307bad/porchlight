using System.Text.Json;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class TerrainShapesTests
{
    private static TownDto NewTown() => TownFactory.CreateEmpty(Ids.New(), "Shapes");

    private static TerrainShapeDto Shape(string kind, float x, float y, float z, float scale = 10f) => new()
    {
        Id = Ids.New(),
        Kind = kind,
        Name = TerrainShapes.DisplayName(kind),
        X = x,
        Y = y,
        Z = z,
        ScaleX = scale,
        ScaleY = scale,
        ScaleZ = scale,
        Overlay = true,
    };

    private static EditResult Add(TownDto town, TerrainShapeDto shape) =>
        TownEditor.Apply(town, new AddTerrainShapeCommand { Shape = shape });

    [Theory]
    [InlineData(TerrainShapes.Cube, 15f)]
    [InlineData(TerrainShapes.Sphere, 15f)]
    [InlineData(TerrainShapes.Prism, 15f)]
    public void TopsAreWhereTheShapesAre(string kind, float top)
    {
        // A 10 m shape centred 10 m up reaches 15 m: the cube's lid, the ball's crown, the prism's ridge.
        var shape = Shape(kind, 0f, 10f, 0f);
        Assert.True(TerrainShapes.TopAt(shape, new Vec2(0f, 0f), out var height));
        Assert.Equal(top, height, 2);
        Assert.False(TerrainShapes.TopAt(shape, new Vec2(20f, 0f), out _));
    }

    [Fact]
    public void PrismSlopesDownToItsSidesAndRunsEndToEnd()
    {
        // Halfway from the ridge to the eave the roof is halfway down: 10 m tall, so 5 m above the floor at -5.
        var shape = Shape(TerrainShapes.Prism, 0f, 0f, 0f);
        Assert.True(TerrainShapes.TopAt(shape, new Vec2(2.5f, 4.9f), out var height));
        Assert.Equal(0f, height, 2);
        Assert.False(TerrainShapes.TopAt(shape, new Vec2(0f, 5.1f), out _));
    }

    [Fact]
    public void OldPyramidsLoadAsPrisms()
    {
        var town = NewTown();
        var shape = Shape("pyramid", 0f, 0f, 0f);
        Assert.True(TownEditor.Apply(town, new AddTerrainShapeCommand { Shape = shape }).Ok);
        Assert.Equal(TerrainShapes.Prism, town.TerrainShapes[0].Kind);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void RotationMatchesUnity()
    {
        // Quaternion.Euler(0, 90, 0) * forward = right; Euler(90, 0, 0) * forward = down; Euler(0, 0, 90) * right = up.
        AssertClose(new Vec3(1f, 0f, 0f), TerrainShapes.Rotate(new Vec3(0f, 0f, 1f), 0f, 90f, 0f));
        AssertClose(new Vec3(0f, -1f, 0f), TerrainShapes.Rotate(new Vec3(0f, 0f, 1f), 90f, 0f, 0f));
        AssertClose(new Vec3(0f, 1f, 0f), TerrainShapes.Rotate(new Vec3(1f, 0f, 0f), 0f, 0f, 90f));

        var v = new Vec3(0.3f, -1.2f, 2f);
        AssertClose(v, TerrainShapes.Unrotate(TerrainShapes.Rotate(v, 20f, 130f, -45f), 20f, 130f, -45f));
    }

    [Fact]
    public void TiltedCubeTopsOutAtACorner()
    {
        var shape = Shape(TerrainShapes.Cube, 0f, 0f, 0f);
        shape.RotationZ = 45f;
        Assert.True(TerrainShapes.TopAt(shape, Vec2.Zero, out var height));
        Assert.Equal(5f * MathF.Sqrt(2f), height, 2);
    }

    [Fact]
    public void OverlayRaisesTheTilesUnderAShapeToItsTop()
    {
        var town = NewTown();
        Assert.True(Add(town, Shape(TerrainShapes.Cube, 2f, 6f, 2f)).Ok);
        Assert.True(TownEditor.Apply(town, new ApplyTerrainOverlayCommand()).Ok);

        // Over the middle of the cube the ground is at its lid, rounding off towards its edges, then
        // falling away a level a tile; far away it's untouched.
        Assert.Equal(11f, TownTerrain.HeightAt(town, new Vec2(2f, 2f)), 2);
        Assert.InRange(TownTerrain.HeightAt(town, new Vec2(-2f, 6f)), 10f, 11f);
        Assert.Equal(6f, TownTerrain.HeightAt(town, new Vec2(26f, 2f)), 2);
        Assert.Equal(0f, TownTerrain.HeightAt(town, new Vec2(100f, 100f)));
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void OnlyShapesWithTheOverlayOnShapeTheGround()
    {
        var town = NewTown();
        var off = Shape(TerrainShapes.Cube, -62f, 6f, 2f);
        off.Overlay = false;
        Assert.True(Add(town, off).Ok);
        Assert.True(Add(town, Shape(TerrainShapes.Cube, 62f, 6f, 2f)).Ok);
        Assert.True(TownEditor.Apply(town, new ApplyTerrainOverlayCommand()).Ok);

        Assert.Equal(0f, TownTerrain.HeightAt(town, new Vec2(-62f, 2f)));
        Assert.Equal(11f, TownTerrain.HeightAt(town, new Vec2(62f, 2f)), 2);
        Assert.All(town.TerrainShapes, s => Assert.False(s.Overlay));
    }

    [Fact]
    public void ShapesBecomeHillsThatSlopeGentlyDownToTheGround()
    {
        // A 16 x 8 ridge running north-south: grass rises over it and slopes gently away all round.
        var town = NewTown();
        var prism = Shape(TerrainShapes.Prism, 0f, 4f, 0f);
        prism.ScaleX = 16f;
        prism.ScaleY = 8f;
        prism.ScaleZ = 24f;
        Assert.True(Add(town, prism).Ok);
        Assert.True(TownEditor.Apply(town, new ApplyTerrainOverlayCommand()).Ok);

        var middle = TownTerrain.HeightAt(town, new Vec2(2f, 2f));
        var side = TownTerrain.HeightAt(town, new Vec2(10f, 2f));
        var further = TownTerrain.HeightAt(town, new Vec2(18f, 2f));
        Assert.True(middle > side && side > further && further > 0f, $"{middle}, {side}, {further}");
        Assert.Equal(0f, TownTerrain.HeightAt(town, new Vec2(60f, 2f)));

        // No walls anywhere on it: every step from tile to tile is smooth, and never steeper than 1 in 4 or so.
        TownTiles.TryCell(town, Vec2.Zero, out var ci, out var cj);
        for (var j = cj - 12; j <= cj + 12; j++)
        {
            for (var i = ci - 12; i <= ci + 12; i++)
            {
                for (var side2 = 0; side2 < 4; side2++) Assert.True(TownTiles.Step(town, i, j, (TileSide)side2) < 0.05f, $"wall at {i},{j} side {side2}");
            }
        }

        for (var x = -40f; x < 40f; x += 0.5f)
        {
            var rise = MathF.Abs(TownTerrain.HeightAt(town, new Vec2(x + 0.5f, 3f)) - TownTerrain.HeightAt(town, new Vec2(x, 3f)));
            Assert.True(rise <= 0.5f * 0.4f, $"{rise} at {x}");
        }

        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void HillsMeetFlatGroundWithoutAWallAndBuildingsFlattenThem()
    {
        var town = NewTown();
        Assert.True(Add(town, Shape(TerrainShapes.Sphere, 0f, 0f, 0f, 16f)).Ok);
        Assert.True(TownEditor.Apply(town, new ApplyTerrainOverlayCommand()).Ok);
        Assert.True(TownTiles.TryCell(town, new Vec2(10f, 2f), out var i, out var j));
        Assert.True(TownTiles.IsHill(town, i, j));

        // Paving a hillside tile flattens it; painting it grass again doesn't bring the slope back.
        var k = TownTiles.Index(town, i, j);
        Assert.True(TownEditor.Apply(town, new PaintTilesCommand { Cells = new() { k }, SurfaceId = "plaza" }).Ok);
        Assert.False(TownTiles.IsHill(town, i, j));
        Assert.Equal(TownTiles.Level(town, i, j), TownTerrain.HeightAt(town, TownTiles.Center(town, i, j)), 2);

        // A building on the hill stands on flat tiles.
        var placed = TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = Ids.New(), TypeId = "cottage", X = -2f, Z = 14f });
        Assert.True(placed.Ok, placed.Error);
        Assert.True(TownRules.ValidateTown(town).IsValid, string.Join("; ", TownRules.ValidateTown(town).Errors));
        Assert.True(TownRules.TryFootprint(town.Buildings[0], out var footprint));
        foreach (var (ti, tj) in TownTiles.Under(town, footprint)) Assert.False(TownTiles.IsHill(town, ti, tj));

        // Hillside marks survive a save.
        var back = System.Text.Json.JsonSerializer.Deserialize<TownDto>(System.Text.Json.JsonSerializer.Serialize(town));
        Assert.Equal(town.Tiles.Hills, back!.Tiles.Hills);
    }

    [Fact]
    public void ShapesUnderTheGroundChangeNothing()
    {
        var town = NewTown();
        Assert.True(Add(town, Shape(TerrainShapes.Sphere, 0f, -20f, 0f)).Ok);
        var before = town.Tiles.Levels.ToList();
        Assert.False(TownEditor.Apply(town, new ApplyTerrainOverlayCommand()).Ok);
        Assert.Equal(before, town.Tiles.Levels);
    }

    [Fact]
    public void OverlayIsIdempotent()
    {
        var town = NewTown();
        Assert.True(Add(town, Shape(TerrainShapes.Prism, 30f, 4f, -20f, 20f)).Ok);
        Assert.True(TownEditor.Apply(town, new ApplyTerrainOverlayCommand()).Ok);
        town.TerrainShapes[0].Overlay = true;
        Assert.Empty(TerrainShapes.Overlay(town));
    }

    [Fact]
    public void OverlayLeavesBuildingsAlone()
    {
        var town = NewTown();
        Assert.True(TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = Ids.New(), TypeId = "house_small", X = 0f, Z = 0f }).Ok);
        Assert.True(Add(town, Shape(TerrainShapes.Cube, 0f, 3f, 0f, 30f)).Ok);
        Assert.True(TownEditor.Apply(town, new ApplyTerrainOverlayCommand()).Ok);

        Assert.Equal(0f, TownTerrain.BaseHeight(town, town.Buildings[0]), 2);
        Assert.True(TownTerrain.HeightAt(town, new Vec2(14f, 2f)) > 5f);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void ShapesCanBeMovedAndRemoved()
    {
        var town = NewTown();
        var shape = Shape(TerrainShapes.Cube, 0f, 0f, 0f);
        Assert.True(Add(town, shape).Ok);
        Assert.False(Add(town, shape).Ok);

        var moved = shape.Clone();
        moved.X = 12.34f;
        moved.ScaleY = 0.1f;
        moved.RotationY = 370f;
        Assert.True(TownEditor.Apply(town, new UpdateTerrainShapeCommand { Shape = moved }).Ok);
        Assert.Equal(12.35f, town.TerrainShapes[0].X, 3);
        Assert.Equal(TerrainShapes.MinScale, town.TerrainShapes[0].ScaleY);
        Assert.Equal(10f, town.TerrainShapes[0].RotationY);

        Assert.False(TownEditor.Apply(town, new UpdateTerrainShapeCommand { Shape = Shape(TerrainShapes.Cube, 900f, 0f, 0f) }).Ok);
        Assert.True(TownEditor.Apply(town, new RemoveTerrainShapeCommand { ShapeId = shape.Id }).Ok);
        Assert.Empty(town.TerrainShapes);
    }

    [Fact]
    public void ServerRejectsBadShapes()
    {
        var town = NewTown();
        town.TerrainShapes.Add(Shape("blob", 0f, 0f, 0f));
        Assert.False(TownRules.ValidateTown(town).IsValid);

        town.TerrainShapes[0] = Shape(TerrainShapes.Cube, 0f, float.NaN, 0f);
        Assert.False(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void NamesCountUpPerKind()
    {
        var town = NewTown();
        Assert.Equal("Cube 1", TerrainShapes.NextName(town, TerrainShapes.Cube));
        var cube = Shape(TerrainShapes.Cube, 0f, 0f, 0f);
        cube.Name = "Cube 4";
        Assert.True(Add(town, cube).Ok);
        Assert.Equal("Cube 5", TerrainShapes.NextName(town, TerrainShapes.Cube));
        Assert.Equal("Triangle 1", TerrainShapes.NextName(town, TerrainShapes.Prism));
    }

    [Fact]
    public void ShapesSurviveJson()
    {
        var town = NewTown();
        Assert.True(Add(town, Shape(TerrainShapes.Sphere, 1f, 2f, 3f)).Ok);
        var json = JsonSerializer.Serialize(town, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<TownDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(back.TerrainShapes);
        Assert.Equal(TerrainShapes.Sphere, back.TerrainShapes[0].Kind);
        Assert.Equal(2f, back.TerrainShapes[0].Y);
    }

    private static void AssertClose(Vec3 expected, Vec3 actual)
    {
        Assert.True((expected - actual).Length < 1e-4f, $"Expected {expected}, got {actual}");
    }
}

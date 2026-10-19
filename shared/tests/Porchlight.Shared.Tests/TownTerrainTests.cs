using System.Text.Json;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class TownTerrainTests
{
    private static TownDto NewTown() => TownFactory.CreateEmpty(Ids.New(), "Hills");

    private static EditResult Shape(TownDto town, float x, float z, float delta, float pad = 8f) =>
        TownEditor.Apply(town, new ShapeTerrainCommand { X = x, Z = z, PadHalfSize = pad, Delta = delta });

    private static EditResult Place(TownDto town, string type, float x, float z) =>
        TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = Ids.New(), TypeId = type, X = x, Z = z });

    [Fact]
    public void NewTownsAreFlat()
    {
        var town = NewTown();
        Assert.Null(town.Terrain);
        Assert.Equal(0f, TownTerrain.HeightAt(town, new Vec2(13f, -7f)));
    }

    [Fact]
    public void RaisingMakesALevelPadWithGentleSlopes()
    {
        var town = NewTown();
        for (var i = 0; i < 5; i++) Assert.True(Shape(town, 0, 0, TownTerrain.Step).Ok);

        // The pad is level at the new height.
        Assert.Equal(5f, TownTerrain.HeightAt(town, new Vec2(0f, 0f)), 2);
        Assert.Equal(5f, TownTerrain.HeightAt(town, new Vec2(7.5f, -7.5f)), 2);

        // The ground falls away no faster than the maximum slope, and is untouched far away.
        Assert.Equal(5f - TownTerrain.MaxSlope * 4f, TownTerrain.HeightAt(town, new Vec2(12f, 0f)), 2);
        Assert.Equal(0f, TownTerrain.HeightAt(town, new Vec2(40f, 0f)));
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void LoweringDigsAPit()
    {
        var town = NewTown();
        Assert.True(Shape(town, 20, 20, -2f).Ok);
        Assert.Equal(-2f, TownTerrain.HeightAt(town, new Vec2(20f, 20f)), 2);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void EdgeOfTownStaysAtGroundLevel()
    {
        var town = NewTown();
        var edge = town.Size * 0.5f;
        Assert.True(Shape(town, edge - 4f, 0, 10f).Ok);
        Assert.Equal(0f, TownTerrain.HeightAt(town, new Vec2(edge, 0f)));
        Assert.True(TownTerrain.HeightAt(town, new Vec2(edge - 4f, 0f)) <= TownTerrain.MaxSlope * 4f + 0.01f);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void CantGoPastTheHeightLimit()
    {
        var town = NewTown();
        var result = Shape(town, 0, 0, TownTerrain.MaxHeight + 5f);
        Assert.True(result.Ok);
        Assert.Equal(TownTerrain.MaxHeight, TownTerrain.HeightAt(town, Vec2.Zero), 2);
        Assert.Equal("The ground can't go any higher here", Shape(town, 0, 0, 1f).Error);
    }

    [Fact]
    public void BuildingsGoOnPadsWithoutChangingThem()
    {
        var town = NewTown();
        Assert.True(Shape(town, 0, 0, 3f, pad: 12f).Ok);
        var before = town.Terrain.Heights.ToList();
        Assert.True(Place(town, "house_small", 0, 0).Ok);
        Assert.Equal(3f, TownTerrain.BaseHeight(town, town.Buildings[0]), 2);
        Assert.Equal(before, town.Terrain.Heights);
    }

    /// <summary>A long ramp rising towards +X, about as steep as terrain gets.</summary>
    private static TownDto Hillside()
    {
        var town = NewTown();
        for (var i = 0; i < 12; i++) Assert.True(Shape(town, 60, 0, 1f, pad: 16f).Ok);
        return town;
    }

    [Fact]
    public void PlacingOnASlopeLevelsTheGroundUnderIt()
    {
        var town = Hillside();
        var slopeHeight = TownTerrain.HeightAt(town, new Vec2(20f, 0f));
        Assert.True(TownTerrain.HeightAt(town, new Vec2(26f, 0f)) - TownTerrain.HeightAt(town, new Vec2(14f, 0f)) > 2f);

        Assert.True(Place(town, "house_large", 20, 0).Ok);
        var house = town.Buildings[0];
        Assert.True(TownRules.TryFootprint(house, out var footprint));
        Assert.True(TownTerrain.IsLevel(town, footprint, 0.01f));
        Assert.Equal(slopeHeight, TownTerrain.BaseHeight(town, house), 1);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void LevellingLeavesAFlatStepInFrontOfTheDoor()
    {
        var town = Hillside();

        // Facing +X, uphill: the door side is levelled a grid square further out.
        Assert.True(TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = Ids.New(), TypeId = "house_small", X = 20, Z = 0, RotationDeg = 90 }).Ok);
        var level = TownTerrain.BaseHeight(town, town.Buildings[0]);
        var doorstep = new OrientedRect(new Vec2(20f + 3f + TownGrid.Spacing * 0.5f, 0f), TownGrid.Spacing * 0.5f, 3.5f, 0f);
        TownTerrain.Range(town, doorstep, out var min, out var max);
        Assert.Equal(level, min, 2);
        Assert.Equal(level, max, 2);

        // Behind the house the ground isn't flattened beyond the footprint's own cells.
        Assert.NotEqual(level, TownTerrain.HeightAt(town, new Vec2(20f - 12f, 0f)), 1);
    }

    [Fact]
    public void LevellingLeavesTheHillsideAroundAlone()
    {
        var town = Hillside();
        var before = town.Terrain.Heights.ToList();
        Assert.True(Place(town, "house_large", 20, 0).Ok);
        Assert.True(TownRules.TryFootprint(town.Buildings[0], out var footprint));

        // Only the cells under the house and its doorstep change; banks take up the difference.
        var front = TownGrid.Spacing * 0.5f;
        var area = new OrientedRect(footprint.Center + footprint.AxisZ * front, footprint.HalfWidth, footprint.HalfDepth + front, footprint.RotationDeg);
        var underneath = TownTerrain.Underneath(town, area);
        var n = town.Terrain.Cells + 1;
        for (var k = 0; k < before.Count; k++)
        {
            if (!underneath.Contains((k % n, k / n))) Assert.Equal(before[k], town.Terrain.Heights[k]);
        }

        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void ARowOfHousesOnASlopeLevelsToMatch()
    {
        var town = Hillside();
        Assert.True(Place(town, "house_small", 20, 0).Ok);
        var first = town.Buildings[0];
        var height = TownTerrain.BaseHeight(town, first);

        // Uphill, sharing a row of lattice points with the first house, which mustn't move.
        Assert.True(Place(town, "house_small", 29, 0).Ok);
        Assert.Equal(height, TownTerrain.BaseHeight(town, first), 2);
        Assert.Equal(height, TownTerrain.BaseHeight(town, town.Buildings[1]), 1);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void BuildingsRideALevelPadUpButBlockSlopingThem()
    {
        var town = NewTown();
        Assert.True(Shape(town, 0, 0, 2f, pad: 12f).Ok);
        Assert.True(Place(town, "house_small", 0, 0).Ok);

        Assert.True(Shape(town, 0, 0, 1f, pad: 12f).Ok);
        Assert.Equal(3f, TownTerrain.BaseHeight(town, town.Buildings[0]), 2);

        // A small pad off to one side would tip the house.
        Assert.Equal("That would leave a building on a slope", Shape(town, 8, 0, 2f, pad: 4f).Error);
    }

    [Fact]
    public void WalkingFollowsTheGround()
    {
        var town = NewTown();
        Assert.True(Shape(town, 0, 0, 4f).Ok);
        Assert.Equal(4f, TownWalking.GroundHeight(town, Vec2.Zero), 2);
    }

    [Fact]
    public void ServerRejectsCliffs()
    {
        var town = NewTown();
        Assert.True(Shape(town, 0, 0, 1f).Ok);
        var n = town.Terrain.Cells + 1;
        town.Terrain.Heights[(n / 2) * n + n / 2] = 2000;
        Assert.Contains("Terrain is too steep", TownRules.ValidateTown(town).Errors);
    }

    [Fact]
    public void TerrainRoundTripsThroughJson()
    {
        var town = NewTown();
        Assert.True(Shape(town, 0, 0, 2f).Ok);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var copy = JsonSerializer.Deserialize<TownDto>(JsonSerializer.Serialize(town, options), options);
        Assert.Equal(town.Terrain.Heights, copy.Terrain.Heights);
        Assert.True(TownRules.ValidateTown(copy).IsValid);
    }
}

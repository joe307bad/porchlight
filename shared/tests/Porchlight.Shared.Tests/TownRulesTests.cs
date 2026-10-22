using System.Text.Json;
using Porchlight.Shared.Content;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Metrics;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class TownRulesTests
{
    private static TownDto NewTown() => TownFactory.CreateEmpty(Ids.New(), "Test Town");

    private static EditResult Place(TownDto town, string type, float x, float z, float rot = 0f, string id = null) =>
        TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = id ?? Ids.New(), TypeId = type, X = x, Z = z, RotationDeg = rot });

    /// <summary>A street painted as a line of tiles from one point to another.</summary>
    private static EditResult Street(TownDto town, float x0, float z0, float x1, float z1, string surface = "street") =>
        TownEditor.Apply(town, new PaintTilesCommand { Cells = TownTiles.Line(town, new Vec2(x0, z0), new Vec2(x1, z1)), SurfaceId = surface, Line = true });

    [Fact]
    public void PlacesBuildingInEmptyTown()
    {
        var town = NewTown();
        Assert.True(Place(town, "house_small", 0, 0).Ok);
        Assert.Single(town.Buildings);
    }

    [Fact]
    public void RejectsOverlappingBuildings()
    {
        var town = NewTown();
        Assert.True(Place(town, "house_small", 0, 0).Ok);
        var second = Place(town, "house_small", 3, 2);
        Assert.False(second.Ok);
        Assert.Equal("Overlaps another building", second.Error);
    }

    [Fact]
    public void AllowsBuildingsFlushAgainstEachOther()
    {
        var town = NewTown();
        Assert.True(Place(town, "house_small", 0, 0).Ok);
        Assert.True(Place(town, "house_small", 7, 0).Ok); // the cottage is 7 m wide
    }

    [Fact]
    public void RotatedRectanglesUseOrientedOverlap()
    {
        // Two long, thin rectangles side by side at 45 degrees. Their axis-aligned boxes overlap,
        // but the rectangles themselves don't.
        var a = new OrientedRect(new Vec2(0, 0), 1, 5, 45);
        var offset = new Vec2(0.70710678f, -0.70710678f) * 3f;
        var b = new OrientedRect(offset, 1, 5, 45);
        Assert.False(a.Overlaps(b));
        Assert.True(a.Overlaps(new OrientedRect(offset * 0.5f, 1, 5, 45)));
    }

    [Fact]
    public void RejectsBuildingsOutsideTheBoundary()
    {
        var town = NewTown();
        Assert.Equal("Outside the town boundary", Place(town, "office", town.Size / 2 - 2, 0).Error);
    }

    [Fact]
    public void BuildingsCantStandOnStreetsOrPaths()
    {
        var town = NewTown();
        Assert.True(Street(town, -50, 2, 50, 2).Ok);
        Assert.Equal("Buildings can't stand on a street", Place(town, "house_small", 0, 2).Error);
        Assert.True(Street(town, 20, 30, 20, 60, "path").Ok);
        Assert.Equal("Buildings can't stand on a path", Place(town, "house_small", 21, 40).Error);

        // Plazas are fine.
        Assert.True(TownEditor.Apply(town, new PaintTilesCommand { Cells = TownTiles.Area(town, new Vec2(-60, 60), new Vec2(-40, 80)), SurfaceId = "plaza" }).Ok);
        Assert.True(Place(town, "shop", -50, 70).Ok);
    }

    [Fact]
    public void StreetsCantBePaintedUnderBuildings()
    {
        var town = NewTown();
        Assert.True(Place(town, "house_small", 0, 10).Ok);
        Assert.Equal("There's a building in the way", Street(town, -20, 10, 20, 10).Error);
    }

    [Fact]
    public void SnapToRoadPlacesAValidFacingBuilding()
    {
        var town = NewTown();
        Assert.True(Street(town, -50, 2, 50, 2).Ok);
        Catalog.TryGetBuilding("house_small", out var cottage);

        Assert.True(TownQueries.TrySnapToRoad(town, cottage, new Vec2(5, 9), out var pos, out var rot));
        Assert.True(pos.Z > 4f);
        Assert.Equal(180f, TownMath.NormalizeAngle(rot)); // north of the street, so it faces south
        Assert.InRange(pos.Z, 4f + cottage.Depth * 0.5f, 4f + cottage.Depth * 0.5f + 0.1f); // its front just back from the street's edge
        Assert.True(Place(town, "house_small", pos.X, pos.Z, rot).Ok);
        Assert.Equal(1, TownMetrics.Compute(town).ConnectedBuildingCount);
    }

    [Fact]
    public void BuildingBesideAStreetCountsAsConnected()
    {
        var town = NewTown();
        Assert.True(Street(town, -50, 2, 50, 2).Ok);
        Catalog.TryGetBuilding("house_small", out var cottage);

        // North of the street, facing south onto it.
        Assert.True(Place(town, "house_small", 6, 4f + cottage.Depth * 0.5f + 0.1f, 180f).Ok);

        var metrics = TownMetrics.Compute(town);
        Assert.Equal(1, metrics.ConnectedBuildingCount);
        Assert.Equal(cottage.Population, metrics.Population);
        Assert.Equal(TownTiles.Line(town, new Vec2(-50, 2), new Vec2(50, 2)).Count * TownTiles.Size, metrics.RoadLengthMeters, 2);
    }

    [Fact]
    public void UnconnectedBuildingsDoNotCountTowardsPopulation()
    {
        var town = NewTown();
        Assert.True(Place(town, "apartment", 0, 0).Ok);
        var metrics = TownMetrics.Compute(town);
        Assert.Equal(1, metrics.BuildingCount);
        Assert.Equal(0, metrics.Population);
    }

    [Fact]
    public void ServerValidationAcceptsEditorOutputAndCatchesTampering()
    {
        var town = NewTown();
        Assert.True(Street(town, -60, 2, 60, 2).Ok);
        Assert.True(Place(town, "shop", 2, 10, 180).Ok);
        Assert.True(Place(town, "house_large", 20, 10, 180).Ok);
        Assert.True(TownRules.ValidateTown(town).IsValid);

        var tampered = town.Clone();
        tampered.Buildings[1].X = 4; // drag it on top of the shop
        tampered.Buildings.Add(new BuildingDto { Id = "x", TypeId = "castle" });
        var result = TownRules.ValidateTown(tampered);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("overlap"));
        Assert.Contains(result.Errors, e => e.Contains("unknown type"));

        // A building dragged onto the street, or tiles raised under half of it.
        var onStreet = town.Clone();
        onStreet.Buildings[0].Z = 3;
        Assert.Contains(TownRules.ValidateTown(onStreet).Errors, e => e.Contains("street"));
        var tilted = town.Clone();
        Assert.True(TownTiles.TryCell(tilted, new Vec2(2 + 4, 12), out var i, out var j));
        tilted.Tiles.Levels[TownTiles.Index(tilted, i, j)] = 3;
        Assert.Contains(TownRules.ValidateTown(tilted).Errors, e => e.Contains("level ground"));
    }

    [Fact]
    public void RoundTripsThroughServerJson()
    {
        var town = NewTown();
        Assert.True(Street(town, -60, 2, 60, 2).Ok);
        Assert.True(Place(town, "park", 0, 20).Ok);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(town, options);
        Assert.Contains("\"tiles\"", json);
        var back = JsonSerializer.Deserialize<TownDto>(json, options)!;

        Assert.Equal(town.Buildings[0].Id, back.Buildings[0].Id);
        Assert.Equal(town.Tiles.Surfaces, back.Tiles.Surfaces);
        Assert.True(TownRules.ValidateTown(back).IsValid);
    }
}

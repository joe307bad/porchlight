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

    private static EditResult Road(TownDto town, float x0, float z0, float x1, float z1, string id = null) =>
        TownEditor.Apply(town, new AddRoadCommand { SegmentId = id ?? Ids.New(), TypeId = "street", StartX = x0, StartZ = z0, EndX = x1, EndZ = z1 });

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
    public void RoadEndsSnapToExistingNodes()
    {
        var town = NewTown();
        Assert.True(Road(town, 0, 0, 40, 0).Ok);
        Assert.True(Road(town, 41, 1.5f, 40, 40).Ok);
        Assert.Equal(3, town.Roads.Nodes.Count);
        Assert.Equal(2, town.Roads.Segments.Count);
    }

    [Fact]
    public void RejectsDuplicateAndTinyRoads()
    {
        var town = NewTown();
        Assert.True(Road(town, 0, 0, 40, 0).Ok);
        Assert.Equal("That road already exists", Road(town, 40, 0, 0, 0).Error);
        Assert.Equal("Road is too short", Road(town, 100, 100, 101, 100).Error);
    }

    [Fact]
    public void RoadsAndBuildingsCannotOverlap()
    {
        var town = NewTown();
        Assert.True(Road(town, -50, 0, 50, 0).Ok);
        Assert.Equal("Overlaps a road", Place(town, "house_small", 0, 1).Error);

        Assert.True(Place(town, "house_small", 0, 30).Ok);
        Assert.Equal("Road would cut through a building", Road(town, 0, 10, 0, 50).Error);
    }

    [Fact]
    public void SnapToRoadPlacesAValidFacingBuilding()
    {
        var town = NewTown();
        Assert.True(Road(town, -50, 0, 50, 0).Ok);
        Catalog.TryGetBuilding("house_small", out var cottage);

        Assert.True(TownQueries.TrySnapToRoad(town, cottage, new Vec2(5, 6), out var pos, out var rot));
        Assert.True(pos.Z > 0);
        Assert.Equal(180f, TownMath.NormalizeAngle(rot)); // north of the road, so it faces south
        Assert.True(Place(town, "house_small", pos.X, pos.Z, rot).Ok);

        var metrics = TownMetrics.Compute(town);
        Assert.Equal(1, metrics.ConnectedBuildingCount);
        Assert.Equal(cottage.Population, metrics.Population);
        Assert.Equal(100f, metrics.RoadLengthMeters, 2);
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
    public void RemovingARoadRemovesOrphanedNodes()
    {
        var town = NewTown();
        Assert.True(Road(town, 0, 0, 40, 0, "r1").Ok);
        Assert.True(Road(town, 40, 0, 40, 40, "r2").Ok);
        Assert.True(TownEditor.Apply(town, new RemoveRoadCommand { SegmentId = "r2" }).Ok);
        Assert.Equal(2, town.Roads.Nodes.Count);
        Assert.True(TownEditor.Apply(town, new RemoveRoadCommand { SegmentId = "r1" }).Ok);
        Assert.Empty(town.Roads.Nodes);
    }

    [Fact]
    public void ServerValidationAcceptsEditorOutputAndCatchesTampering()
    {
        var town = NewTown();
        Assert.True(Road(town, -60, 0, 60, 0).Ok);
        Assert.True(Place(town, "shop", 0, 8, 180).Ok);
        Assert.True(Place(town, "house_large", 20, 10, 180).Ok);
        Assert.True(TownRules.ValidateTown(town).IsValid);

        var tampered = town.Clone();
        tampered.Buildings[1].X = 2; // drag it on top of the shop
        tampered.Buildings.Add(new BuildingDto { Id = "x", TypeId = "castle" });
        var result = TownRules.ValidateTown(tampered);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("overlap"));
        Assert.Contains(result.Errors, e => e.Contains("unknown type"));
    }

    [Fact]
    public void RoundTripsThroughServerJson()
    {
        var town = NewTown();
        Assert.True(Road(town, -60, 0, 60, 0).Ok);
        Assert.True(Place(town, "park", 0, 20).Ok);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(town, options);
        Assert.Contains("\"buildings\"", json);
        var back = JsonSerializer.Deserialize<TownDto>(json, options);

        Assert.Equal(town.Buildings[0].Id, back.Buildings[0].Id);
        Assert.Equal(town.Roads.Segments[0].StartNodeId, back.Roads.Segments[0].StartNodeId);
        Assert.True(TownRules.ValidateTown(back).IsValid);
    }
}

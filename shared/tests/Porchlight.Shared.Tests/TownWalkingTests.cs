using Porchlight.Shared.Content;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class TownWalkingTests
{
    private static TownDto TownWith(string type, float x, float z, float rot = 0f)
    {
        var town = TownFactory.CreateEmpty(Ids.New(), "Walk Town");
        var placed = TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = Ids.New(), TypeId = type, X = x, Z = z, RotationDeg = rot });
        Assert.True(placed.Ok);
        return town;
    }

    [Fact]
    public void WalksFreelyInOpenGround()
    {
        var town = TownFactory.CreateEmpty(Ids.New(), "Empty");
        var end = TownWalking.Move(town, Vec2.Zero, new Vec2(3f, 4f));
        Assert.Equal(new Vec2(3f, 4f), end);
    }

    [Fact]
    public void StopsAtAWallAndSlidesAlongIt()
    {
        // Cottage is 7 m wide and 6 m deep, so its south wall is at z = -3.
        var town = TownWith("house_small", 0, 0);
        var end = TownWalking.Move(town, new Vec2(0f, -5f), new Vec2(1f, 3f));
        Assert.Equal(1f, end.X, 3);
        Assert.Equal(-3f - TownWalking.CharacterRadius, end.Z, 3);
    }

    [Fact]
    public void RespectsRotatedBuildings()
    {
        var town = TownWith("shop", 0, 0, 45);
        var end = TownWalking.Move(town, new Vec2(-10f, 0f), new Vec2(10f, 0f));
        Assert.True(TownWalking.IsClear(town, end));
    }

    [Fact]
    public void StaysInsideTheTown()
    {
        var town = TownFactory.CreateEmpty(Ids.New(), "Empty");
        var half = town.Size * 0.5f;
        var end = TownWalking.Move(town, new Vec2(half - 1f, 0f), new Vec2(10f, 0f));
        Assert.True(end.X <= half);
    }

    [Fact]
    public void ParksAreWalkableAndRaiseTheGround()
    {
        var town = TownWith("park", 0, 0);
        Assert.Equal(new Vec2(0f, 1f), TownWalking.Move(town, new Vec2(0f, -10f), new Vec2(0f, 11f)));
        Assert.True(TownWalking.GroundHeight(town, Vec2.Zero) > 0f);
    }

    [Fact]
    public void SpawnsOutsideBuildings()
    {
        var town = TownWith("office", 0, 0);
        var spawn = TownWalking.FindSpawn(town, Vec2.Zero);
        Assert.True(TownWalking.IsClear(town, spawn));
        Assert.False(TownWalking.IsInsideBuilding(town, spawn, 1f));
    }

    [Fact]
    public void CharacterIdsAreUniqueAndComplete()
    {
        Assert.Equal(2, Characters.All.Count);
        foreach (var character in Characters.All)
        {
            Assert.True(Characters.TryGet(character.Id, out var found));
            Assert.Same(character, found);
            Assert.NotEmpty(character.ShortsStripes);
        }
    }
}

public class StockTownTests
{
    [Fact]
    public void IsAValidTownWithAnEmptyPlaza()
    {
        var town = StockTown.Create();
        Assert.True(TownRules.ValidateTown(town).IsValid);
        Assert.True(town.Buildings.Count >= 40, $"only {town.Buildings.Count} buildings");
        Assert.True(TownWalking.IsClear(town, Vec2.Zero, StockTown.PlazaHalfDepth - 1f));
    }

    [Fact]
    public void GridSnapsToTheNearestIntersection()
    {
        Assert.Equal(new Vec2(4f, -8f), TownGrid.Snap(new Vec2(5.9f, -6.1f)));
        Assert.Equal(new Vec2(0f, 0f), TownGrid.Snap(new Vec2(1.9f, -1.9f)));
    }
}

using System;
using System.Text.Json;
using Porchlight.Shared.Content;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class InteriorTests
{
    // The small cottage is 7 x 6 m, so inside its 0.25 m walls the floor runs x -3.25..3.25, z -2.75..2.75.
    // Its default stairs run along the back wall: x -0.5..2.5, z -2.75..-1.75, climbing towards -X.
    private static readonly BuildingType Cottage = Small();

    private static BuildingType Small()
    {
        Assert.True(Catalog.TryGetBuilding(CottageInterior.SmallTypeId, out var type));
        return type;
    }

    private static (TownDto Town, BuildingDto Cottage) TownWithCottage(float rotation = 0f)
    {
        var town = TownFactory.CreateEmpty(Ids.New(), "Inside Town");
        var placed = TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = "c", TypeId = CottageInterior.SmallTypeId, X = 10, Z = 10, RotationDeg = rotation });
        Assert.True(placed.Ok, placed.Error);
        return (town, town.Buildings[0]);
    }

    private static EditResult Wall(TownDto town, int floor, float x0, float z0, float x1, float z1, string id = null) =>
        TownEditor.Apply(town, new AddInteriorWallCommand { BuildingId = "c", Floor = floor, WallId = id ?? Ids.New(), X0 = x0, Z0 = z0, X1 = x1, Z1 = z1 });

    private static EditResult Item(TownDto town, int floor, string type, float x, float z, float rotation = 0f, string id = null, string onId = null) =>
        TownEditor.Apply(town, new PlaceFurnitureCommand { BuildingId = "c", Floor = floor, ItemId = id ?? Ids.New(), TypeId = type, X = x, Z = z, RotationDeg = rotation, OnId = onId });

    private static EditResult Opening(TownDto town, int floor, string type, string wallId, float at, string id = null) =>
        TownEditor.Apply(town, new AddOpeningCommand { BuildingId = "c", Floor = floor, OpeningId = id ?? Ids.New(), TypeId = type, WallId = wallId, At = at });

    [Fact]
    public void TheDefaultLayoutIsValidAndHasTwoFloorsAndStairs()
    {
        var interior = CottageInterior.Default();
        Assert.Equal(2, interior.Floors.Count);
        Assert.True(InteriorRules.CanMoveStairs(Cottage, interior, interior.Stairs.X, interior.Stairs.Z, interior.Stairs.RotationDeg).Ok);

        var (town, cottage) = TownWithCottage();
        cottage.Interior = interior;
        var validation = TownRules.ValidateTown(town);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void EditingStartsFromTheDefaultAndLeavesItAlone()
    {
        var (town, cottage) = TownWithCottage();
        Assert.Null(cottage.Interior);
        Assert.True(Wall(town, 0, -1f, -1f, -1f, 2.75f).Ok);
        Assert.Single(cottage.Interior.Floors[0].Walls);
        Assert.Empty(CottageInterior.Default().Floors[0].Walls);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void WallsRunStraightAndStayClearOfTheStairs()
    {
        var (town, _) = TownWithCottage();
        Assert.False(Wall(town, 0, -2f, -1f, 1f, 1f).Ok);
        Assert.Equal("The stairs are in the way", Wall(town, 0, 0f, -2.75f, 0f, 0f).Error);

        // Upstairs, the head of the stairs (at -X) needs a landing.
        Assert.False(Wall(town, 1, -0.75f, -2.75f, -0.75f, 0f).Ok);
        Assert.True(Wall(town, 1, -1.5f, -2.75f, -1.5f, 0f).Ok);
    }

    [Fact]
    public void WallsCantDoubleUpOrHugTheOutsideWalls()
    {
        var (town, _) = TownWithCottage();
        Assert.True(Wall(town, 0, -3.25f, 0.5f, 0f, 0.5f).Ok);
        Assert.Equal("There's a wall there already", Wall(town, 0, -2f, 0.5f, -1f, 0.5f).Error);
        Assert.Equal("Too close to another wall", Wall(town, 0, -2f, 0.6f, -1f, 0.6f).Error);
        Assert.Equal("Too close to the outside wall", Wall(town, 0, -2f, 2.6f, 1f, 2.6f).Error);
    }

    [Fact]
    public void WallEndsSnapToWallsAndStayInLine()
    {
        var (town, cottage) = TownWithCottage();
        Assert.True(Wall(town, 0, -1f, 0f, -1f, 2.75f).Ok);
        var interior = cottage.Interior;

        // Near the end of a wall, a new one starts there.
        Assert.Equal(new Vec2(-1f, 0f), InteriorSnap.WallPoint(Cottage, interior, 0, new Vec2(-0.85f, 0.2f), null));

        // Drawing along X from there, it stays at z = 0 and stops on the outside wall.
        Assert.Equal(new Vec2(-3.25f, 0f), InteriorSnap.WallPoint(Cottage, interior, 0, new Vec2(-3.05f, 0.4f), new Vec2(-1f, 0f)));

        // Otherwise it goes on the grid.
        Assert.Equal(new Vec2(1.25f, 1.5f), InteriorSnap.WallPoint(Cottage, interior, 0, new Vec2(1.2f, 1.45f), null));
    }

    [Fact]
    public void DoorsGoInInsideWallsAndWindowsInAnyWall()
    {
        var (town, cottage) = TownWithCottage();
        Assert.True(Wall(town, 0, -1f, -1.5f, -1f, 2.75f, "w").Ok);
        Assert.Equal("The front door is the only way in for now", Opening(town, 0, Furnishings.Door, CottageInterior.BackWall, -2f).Error);
        Assert.True(Opening(town, 0, Furnishings.Door, "w", 0.5f).Ok);
        Assert.Equal("Too close to another door or window", Opening(town, 0, Furnishings.Window, "w", 1f).Error);
        Assert.True(Opening(town, 0, Furnishings.Window, CottageInterior.BackWall, -2.25f).Ok);

        // The front door is fixed, and windows keep clear of it.
        Assert.False(Opening(town, 0, Furnishings.Window, CottageInterior.FrontWall, 0.5f).Ok);

        // Snapping finds the wall under the pointer and, near its middle, centres on it.
        Assert.True(InteriorSnap.Opening(Cottage, cottage.Interior, 0, Furnishings.Door, new Vec2(-0.8f, 0.75f), out var wallId, out var at));
        Assert.Equal("w", wallId);
        Assert.Equal(0.625f, at, 3);

        // Removing a wall takes its doors and windows with it.
        Assert.True(TownEditor.Apply(town, new RemoveInteriorPartCommand { BuildingId = "c", Floor = 0, PartId = "w" }).Ok);
        Assert.DoesNotContain(cottage.Interior.Floors[0].Openings, o => o.WallId == "w");
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void CountersGoAgainstAWallAndFurnitureSnapsBackToIt()
    {
        var (town, cottage) = TownWithCottage();
        Assert.Equal("A counter goes against a wall", Item(town, 0, "counter", 0f, 0f).Error);

        Furnishings.TryGetFurniture("counter", out var counter);
        var (position, rotation, _) = InteriorSnap.Item(Cottage, CottageInterior.Of(cottage), 0, counter, new Vec2(-2.4f, 0.4f), 0f);

        // Backed onto the left wall, facing into the room, with its end on the grid.
        Assert.Equal(90f, rotation);
        Assert.Equal(-3.25f + 0.3f, position.X, 3);
        Assert.True(Item(town, 0, "counter", position.X, position.Z, rotation, "k1").Ok);

        // The next one lines up flush beside it.
        var (next, nextRotation, _) = InteriorSnap.Item(Cottage, cottage.Interior, 0, counter, new Vec2(-2.95f, position.Z + 1.1f), 0f);
        Assert.Equal(position.Z + 1f, next.Z, 3);
        Assert.True(Item(town, 0, "counter", next.X, next.Z, nextRotation).Ok);
    }

    [Fact]
    public void FurnitureKeepsClearOfWallsDoorsAndEachOther()
    {
        var (town, _) = TownWithCottage();
        Assert.Equal("Keep the doorway clear", Item(town, 0, "dining", 0f, 1.5f).Error);
        Assert.Equal("The stairs are in the way", Item(town, 0, "dining", 2f, -1.2f).Error);
        Assert.True(Item(town, 0, "dining", -1.5f, 0f).Ok);
        Assert.Equal("Something's in the way", Item(town, 0, "couch", -1.5f, 0f).Error);
        Assert.Equal("Furniture is in the way", Wall(town, 0, -1.5f, -2.75f, -1.5f, 1.5f).Error);
        Assert.False(Item(town, 0, "bed", 0f, 0f, 45f).Ok);
    }

    [Fact]
    public void TvsStandOnDressersOrHangOnWalls()
    {
        var (town, cottage) = TownWithCottage();
        Assert.Equal("A TV goes on a dresser or a wall", Item(town, 0, Furnishings.Tv, 0f, 0f).Error);
        Assert.True(Item(town, 0, Furnishings.Dresser, -2f, -2.5f, 0f, "d").Ok);

        Furnishings.TryGetFurniture(Furnishings.Tv, out var tv);
        var (onTop, rotation, onId) = InteriorSnap.Item(Cottage, cottage.Interior, 0, tv, new Vec2(-2f, -2.4f), 90f);
        Assert.Equal("d", onId);
        Assert.True(Item(town, 0, Furnishings.Tv, onTop.X, onTop.Z, rotation, "t", onId).Ok);
        Assert.Equal("There's a TV on that dresser already", Item(town, 0, Furnishings.Tv, onTop.X, onTop.Z, rotation, null, onId).Error);

        // On the right wall, but not over a window.
        Assert.True(Item(town, 0, Furnishings.Tv, 3.2f, 0f, 270f).Ok);
        Assert.True(Item(town, 1, Furnishings.Tv, 3.2f, 1.5f, 270f).Ok);
        Assert.Equal("There's a door or window there", Item(town, 1, Furnishings.Tv, 3.2f, 0f, 270f).Error);

        // Taking the dresser away takes its TV too.
        Assert.True(TownEditor.Apply(town, new RemoveInteriorPartCommand { BuildingId = "c", Floor = 0, PartId = "d" }).Ok);
        Assert.DoesNotContain(cottage.Interior.Floors[0].Items, i => i.Id == "t");
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void RemovingAWallTakesWhatHangsOnItOrNeedsIt()
    {
        var (town, cottage) = TownWithCottage();
        Assert.True(Wall(town, 0, -1f, -1f, -1f, 2.75f, "w").Ok);

        // A TV on the wall's right face, a counter against its left face, and a couch beside it that doesn't need it.
        Assert.True(Item(town, 0, Furnishings.Tv, -0.9f, 0.5f, 90f, "tv").Ok);
        Assert.True(Item(town, 0, "counter", -1.35f, 1.5f, 270f, "k").Ok);
        Assert.True(Item(town, 0, "couch", -0.5f, 0.5f, 90f, "sofa").Ok);
        Assert.True(Item(town, 0, Furnishings.Tv, 3.2f, 0f, 270f, "other-tv").Ok);

        Assert.True(TownEditor.Apply(town, new RemoveInteriorPartCommand { BuildingId = "c", Floor = 0, PartId = "w" }).Ok);
        var left = cottage.Interior.Floors[0].Items.ConvertAll(i => i.Id);
        Assert.DoesNotContain("tv", left);
        Assert.DoesNotContain("k", left);
        Assert.Contains("sofa", left);
        Assert.Contains("other-tv", left);
        Assert.True(TownRules.ValidateTown(town).IsValid);

        // And a TV on a wall can be removed by itself.
        Assert.True(TownEditor.Apply(town, new RemoveInteriorPartCommand { BuildingId = "c", Floor = 0, PartId = "other-tv" }).Ok);
    }

    [Fact]
    public void StairsMoveButKeepTheirLandingsClear()
    {
        var (town, cottage) = TownWithCottage();
        Assert.True(TownEditor.Apply(town, new MoveStairsCommand { BuildingId = "c", X = -2.75f, Z = 0f, RotationDeg = 0f }).Ok);
        Assert.Equal(-2.75f, cottage.Interior.Stairs.X);

        // Climbing into the back wall leaves no room to step off at the top.
        Assert.Equal("Leave room to step on and off the stairs", TownEditor.Apply(town, new MoveStairsCommand { BuildingId = "c", X = -2.5f, Z = 1.25f, RotationDeg = 0f }).Error);

        Assert.True(Item(town, 1, "bed", 0f, 0f).Ok);
        Assert.Equal("Furniture upstairs is in the way", TownEditor.Apply(town, new MoveStairsCommand { BuildingId = "c", X = 0f, Z = 0f, RotationDeg = 90f }).Error);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void TheServerRejectsABadInterior()
    {
        var (town, cottage) = TownWithCottage();
        cottage.Interior = CottageInterior.Default();
        cottage.Interior.Floors[0].Items.Add(new FurnitureDto { Id = "x", TypeId = "couch", X = 1f, Z = -2f });
        Assert.False(TownRules.ValidateTown(town).IsValid);

        var shop = new BuildingDto { Id = "s", TypeId = "shop", X = -40, Z = -40, Interior = CottageInterior.Default() };
        var other = TownFactory.CreateEmpty(Ids.New(), "Shop");
        other.Buildings.Add(shop);
        Assert.False(TownRules.ValidateTown(other).IsValid);
    }

    [Fact]
    public void SurvivesAJsonRoundTrip()
    {
        var (town, _) = TownWithCottage();
        Assert.True(Wall(town, 0, -1f, 0f, -1f, 2.75f).Ok);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var copy = JsonSerializer.Deserialize<TownDto>(JsonSerializer.Serialize(town, options), options)!;
        Assert.Equal(2, copy.Buildings[0].Interior.Floors.Count);
        Assert.Single(copy.Buildings[0].Interior.Floors[0].Walls);
        Assert.True(TownRules.ValidateTown(copy).IsValid);
    }
}

public class InteriorWalkingTests
{
    // A small cottage at the origin, facing +Z: the front door spans x -0.6..0.6 in the wall at z = 2.75..3.
    private static TownDto TownWithCottage()
    {
        var town = TownFactory.CreateEmpty(Ids.New(), "Walk In");
        Assert.True(TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = "c", TypeId = CottageInterior.SmallTypeId }).Ok);
        return town;
    }

    private static Vec2 Walk(TownDto town, Vec2 from, Vec2 to, ref float y)
    {
        // Small steps, like frames.
        var p = from;
        for (var i = 0; i < 200; i++)
        {
            var delta = to - p;
            if (delta.Length < 0.01f) break;
            if (delta.Length > 0.1f) delta = delta.Normalized * 0.1f;
            p = TownWalking.MoveAt(town, p, delta, y);
            y = TownWalking.GroundHeightAt(town, p, y);
        }

        return p;
    }

    [Fact]
    public void WalksInThroughTheFrontDoorOnly()
    {
        var town = TownWithCottage();
        var y = 0f;
        var inside = Walk(town, new Vec2(0f, 6f), new Vec2(0f, 1f), ref y);
        Assert.Equal(1f, inside.Z, 2);
        Assert.Equal(CottageInterior.Plinth, y, 3);
        Assert.True(TownWalking.TryFindInterior(town, inside, y, out var building, out var floor));
        Assert.Equal("c", building.Id);
        Assert.Equal(0, floor);

        // Beside the door, the wall's in the way.
        y = 0f;
        var blocked = Walk(town, new Vec2(2f, 6f), new Vec2(2f, 1f), ref y);
        Assert.True(blocked.Z > 3f);
    }

    [Fact]
    public void ClimbsTheStairsToTheFloorAbove()
    {
        var town = TownWithCottage();
        var y = 0f;

        // In the door, round to the foot of the stairs (at x = 2.5), then up them towards -X.
        var p = Walk(town, new Vec2(0f, 6f), new Vec2(0f, 1f), ref y);
        p = Walk(town, p, new Vec2(2.9f, -0.5f), ref y);
        p = Walk(town, p, new Vec2(2.9f, -2.25f), ref y);
        p = Walk(town, p, new Vec2(-1.2f, -2.25f), ref y);
        Assert.Equal(CottageInterior.FloorHeight(1), y, 2);
        Assert.True(TownWalking.TryFindInterior(town, p, y, out _, out var floor));
        Assert.Equal(1, floor);

        // Downstairs, the side of the stairs is closed.
        var low = 0f;
        var side = Walk(town, new Vec2(1f, 6f), new Vec2(1f, 0f), ref low);
        side = Walk(town, side, new Vec2(1f, -2.5f), ref low);
        Assert.True(side.Z > -1.75f);
    }

    [Fact]
    public void WalkingIntoACouchSitsOnIt()
    {
        var town = TownWithCottage();
        Assert.True(TownEditor.Apply(town, new PlaceFurnitureCommand { BuildingId = "c", Floor = 0, ItemId = "sofa", TypeId = "couch", X = -2.15f, Z = 0f, RotationDeg = 90f }).Ok);

        var y = CottageInterior.Plinth;
        Assert.True(TownWalking.TryFindFurnitureSpot(town, new Vec2(-1.3f, 0.2f), new Vec2(-0.1f, 0f), y, out var spot));
        Assert.Equal("sofa", spot.ItemId);
        Assert.Equal(FurnitureAction.Sit, spot.Action);
        Assert.Equal(90f, spot.Heading);
        Assert.Equal(CottageInterior.Plinth + 0.45f, spot.Height, 3);
        Assert.True(spot.Exit.X > -1.7f);

        // Walking away from it does nothing.
        Assert.False(TownWalking.TryFindFurnitureSpot(town, new Vec2(-1.3f, 0.2f), new Vec2(0.1f, 0f), y, out _));
    }

    [Fact]
    public void GettingUpGoesOffTheSidePushedTowardsIfThereIsRoom()
    {
        // A bed along the left wall, head to the back, with a wall close beside it on the right.
        var town = TownWithCottage();
        Assert.True(TownEditor.Apply(town, new PlaceFurnitureCommand { BuildingId = "c", Floor = 0, ItemId = "bed", TypeId = "bed", X = -2.55f, Z = -0.3f, RotationDeg = 0f }).Ok);
        Assert.True(TownEditor.Apply(town, new AddInteriorWallCommand { BuildingId = "c", Floor = 0, WallId = "w", X0 = -1.65f, Z0 = -1.5f, X1 = -1.65f, Z1 = 2f }).Ok);

        var y = CottageInterior.Plinth;
        Assert.True(TownWalking.TryFindFurnitureSpot(town, new Vec2(-2.55f, 1.1f), new Vec2(0f, -0.1f), y, out var spot));
        Assert.Equal(FurnitureAction.LieDown, spot.Action);

        // Off the foot, when pushing that way.
        var front = TownWalking.ExitToward(town, spot, new Vec2(0f, 1f));
        Assert.True(front.Z > 0.7f && Math.Abs(front.X + 2.55f) < 0.1f, front.ToString());

        // Pushing right runs into the wall, and pushing left into the outside wall, so off the foot instead.
        var right = TownWalking.ExitToward(town, spot, new Vec2(1f, 0f));
        Assert.True(right.X < -1.7f && right.Z > 0.7f, right.ToString());
        var left = TownWalking.ExitToward(town, spot, new Vec2(-1f, 0f));
        Assert.True(left.Z > 0.7f, left.ToString());

        // Never through the wall into the next room.
        foreach (var direction in new[] { new Vec2(1f, 0f), new Vec2(1f, -1f), new Vec2(0f, -1f), new Vec2(-1f, 0f) })
        {
            Assert.True(TownWalking.ExitToward(town, spot, direction).X < -1.7f);
        }
    }
}

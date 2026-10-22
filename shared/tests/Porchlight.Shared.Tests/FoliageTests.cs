using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class FoliageTests
{
    private static TownDto Village()
    {
        var town = TownFactory.CreateEmpty("village", "Village");
        // A street along z = 0..4, a cottage north of it, and a raised block of ground with walls round it.
        Assert.True(TownEditor.Apply(town, new PaintTilesCommand { Cells = TownTiles.Line(town, new Vec2(-100, 2), new Vec2(100, 2)), SurfaceId = "street", Line = true }).Ok);
        Assert.True(TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = "h", TypeId = "cottage", X = 2, Z = 12, RotationDeg = 180 }).Ok);
        Assert.True(TownEditor.Apply(town, new ShapeTerrainCommand { X = 80, Z = 80, PadHalfSize = 8, Delta = 2 }).Ok);

        // Plants everywhere.
        Assert.True(TownEditor.Apply(town, new AddFoliageAreaCommand { Area = new FoliageAreaDto { X0 = -500, Z0 = -500, X1 = 500, Z1 = 500 } }).Ok);
        return town;
    }

    [Fact]
    public void PlantsOnlyGrowInPaintedAreas()
    {
        var town = Village();
        Assert.True(TownEditor.Apply(town, new ClearFoliageAreasCommand()).Ok);
        Assert.Empty(Foliage.Plan(town));

        // A patch, with a hole erased out of it after.
        Assert.True(TownEditor.Apply(town, new AddFoliageAreaCommand { Area = new FoliageAreaDto { X0 = 60, Z0 = -60, X1 = 20, Z1 = -20 } }).Ok);
        Assert.True(TownEditor.Apply(town, new AddFoliageAreaCommand { Area = new FoliageAreaDto { X0 = 30, Z0 = -50, X1 = 50, Z1 = -30, Erase = true } }).Ok);
        Assert.Equal(20f, town.FoliageAreas[0].X0);

        var plants = Foliage.Plan(town);
        Assert.NotEmpty(plants);
        foreach (var plant in plants)
        {
            Assert.InRange(plant.Position.X, 20f, 60f);
            Assert.InRange(plant.Position.Z, -60f, -20f);
            Assert.False(plant.Position.X > 30f && plant.Position.X < 50f && plant.Position.Z > -50f && plant.Position.Z < -30f, $"plant in the erased hole at {plant.Position}");
        }

        // Painting over the hole again fills it.
        Assert.True(TownEditor.Apply(town, new AddFoliageAreaCommand { Area = new FoliageAreaDto { X0 = 30, Z0 = -50, X1 = 50, Z1 = -30 } }).Ok);
        Assert.Contains(Foliage.Plan(town), p => p.Position.X > 32f && p.Position.X < 48f && p.Position.Z > -48f && p.Position.Z < -32f);
        Assert.True(TownRules.ValidateTown(town).IsValid);

        Assert.False(TownEditor.Apply(town, new AddFoliageAreaCommand { Area = new FoliageAreaDto { X0 = 0, Z0 = 0, X1 = 0.5f, Z1 = 10 } }).Ok);
    }

    [Fact]
    public void TheSameTownAlwaysGrowsTheSamePlants()
    {
        var a = Foliage.Plan(Village());
        var b = Foliage.Plan(Village());
        Assert.NotEmpty(a);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Kind, b[i].Kind);
            Assert.Equal(a[i].Position, b[i].Position);
        }
    }

    [Fact]
    public void NothingGrowsOnPavingOrInHouses()
    {
        var town = Village();
        Assert.True(TownRules.TryFootprint(town.Buildings[0], out var house));
        foreach (var plant in Foliage.Plan(town))
        {
            Assert.True(TownTiles.TryCell(town, plant.Position, out var i, out var j));
            Assert.False(TownTiles.IsPaved(TownTiles.Surface(town, i, j)), $"{plant.Kind} on the street at {plant.Position}");
            Assert.False(house.Contains(plant.Position), $"{plant.Kind} in the house at {plant.Position}");
        }
    }

    [Fact]
    public void FlowersGrowAroundHousesAndWallsGetSoftened()
    {
        var plants = Foliage.Plan(Village());
        Assert.Contains(plants, p => p.Kind is FoliageKind.TallFlower or FoliageKind.MediumFlower or FoliageKind.SmallFlower && Vec2.Distance(p.Position, new Vec2(2f, 12f)) < 9f);

        // Round the raised block, the walls are lined with shrubs and long grass.
        var town = Village();
        var byWalls = plants.Count(p => p.Kind is FoliageKind.Bush or FoliageKind.Grass && MathF.Abs(MathF.Max(MathF.Abs(p.Position.X - 80f), MathF.Abs(p.Position.Z - 80f)) - 8f) < 1.2f);
        Assert.True(byWalls > 10, $"only {byWalls} plants by the walls");
        Assert.Contains(plants, p => Vec2.Distance(p.Position, new Vec2(-150f, -150f)) < 20f);
    }

    [Fact]
    public void GardensGrowFlowersWithoutAnArea()
    {
        var town = TownFactory.CreateEmpty("garden", "Garden");
        Assert.Empty(Foliage.Plan(town));
        Assert.True(TownEditor.Apply(town, new PaintTilesCommand { Cells = TownTiles.Area(town, new Vec2(0, 0), new Vec2(10, 10)), SurfaceId = "garden" }).Ok);
        var plants = Foliage.Plan(town);
        Assert.NotEmpty(plants);
        Assert.All(plants, p => Assert.InRange(p.Position.X, -0.1f, 12.1f));
        Assert.True(plants.Count(p => p.Kind is FoliageKind.TallFlower or FoliageKind.MediumFlower or FoliageKind.SmallFlower) > plants.Count / 2);
    }

    [Fact]
    public void TheSlidersMakeItHeavierOrLighter()
    {
        var town = Village();
        var usual = Foliage.Plan(town).Count;

        Assert.True(TownEditor.Apply(town, new SetFoliageCommand { Foliage = new FoliageDto { Overall = 2f, Trees = 1f, Flowers = 1f, Grass = 1f } }).Ok);
        Assert.True(Foliage.Plan(town).Count > usual);

        Assert.True(TownEditor.Apply(town, new SetFoliageCommand { Foliage = new FoliageDto { Overall = 1f, Trees = 0f, Flowers = 1f, Grass = 1f } }).Ok);
        Assert.DoesNotContain(Foliage.Plan(town), p => p.Kind is FoliageKind.Tree or FoliageKind.Bush);

        Assert.True(TownEditor.Apply(town, new SetFoliageCommand { Foliage = new FoliageDto { Overall = 0f } }).Ok);
        Assert.Empty(Foliage.Plan(town));

        Assert.True(TownEditor.Apply(town, new SetFoliageCommand { Foliage = new FoliageDto { Overall = 9f, Trees = -1f } }).Ok);
        Assert.Equal(FoliageDto.Max, town.Foliage.Overall);
        Assert.Equal(0f, town.Foliage.Trees);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }
}

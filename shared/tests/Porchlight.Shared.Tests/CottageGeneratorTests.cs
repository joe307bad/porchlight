using System.Linq;
using System.Text.Json;
using Porchlight.Shared.Content;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using Xunit;

namespace Porchlight.Shared.Tests;

public class CottageGeneratorTests
{
    private static EditResult Place(TownDto town, string id, string typeId, float x, float z, float rotation = 0f) =>
        TownEditor.Apply(town, new PlaceBuildingCommand { BuildingId = id, TypeId = typeId, X = x, Z = z, RotationDeg = rotation });

    private static int Count(InteriorFloorDto floor, string typeId) => floor.Items.Count(i => i.TypeId == typeId);

    [Fact]
    public void EveryNewCottageIsFurnishedAndValid()
    {
        // Lots of seeds, to go through every combination of choices.
        var town = TownFactory.CreateEmpty(Ids.New(), "Cottages", 1024f);
        for (var i = 0; i < 300; i++)
        {
            var placed = Place(town, Ids.New(), CottageInterior.TypeId, -480f + i % 40 * 24f, -480f + i / 40 * 24f, i % 4 * 90f);
            Assert.True(placed.Ok, placed.Error);
        }

        var validation = TownRules.ValidateTown(town);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Take(10)));

        foreach (var cottage in town.Buildings)
        {
            var ground = cottage.Interior.Floors[0];
            var upstairs = cottage.Interior.Floors[1];

            // Living room, kitchen and dining room downstairs.
            Assert.Equal(1, Count(ground, "couch"));
            Assert.Equal(1, Count(ground, Furnishings.Tv));
            Assert.Equal(1, Count(ground, "dining"));
            Assert.Equal(5, Count(ground, "counter"));
            Assert.Equal(3, ground.Walls.Count);
            Assert.Equal(3, ground.Openings.Count(o => o.TypeId == Furnishings.Door));

            // A bedroom and a bathroom upstairs, plus a second bedroom.
            Assert.Equal(2, Count(upstairs, "bed"));
            Assert.Equal(1, Count(upstairs, "toilet"));
            Assert.Equal(1, Count(upstairs, "shower"));
            Assert.Equal(1, Count(upstairs, "sink"));
            Assert.Equal(3, upstairs.Walls.Count);
            Assert.Equal(3, upstairs.Openings.Count(o => o.TypeId == Furnishings.Door));

            Assert.True(ground.Openings.Count(o => o.TypeId == Furnishings.Window) >= 4);
            Assert.True(upstairs.Openings.Count(o => o.TypeId == Furnishings.Window) >= 4);
        }

        // Not all the same.
        Assert.True(town.Buildings.Select(b => JsonSerializer.Serialize(b.Interior)).Distinct().Count() > 20);
    }

    [Fact]
    public void TheSameIdGivesTheSameLayout()
    {
        Catalog.TryGetBuilding(CottageInterior.TypeId, out var type);
        var a = JsonSerializer.Serialize(CottageGenerator.Generate(type, "some-id"));
        var b = JsonSerializer.Serialize(CottageGenerator.Generate(type, "some-id"));
        Assert.Equal(a, b);
        Assert.NotEqual(a, JsonSerializer.Serialize(CottageGenerator.Generate(type, "another-id")));
    }

    [Fact]
    public void TheDefaultForAnUnfurnishedCottageIsValid()
    {
        var town = TownFactory.CreateEmpty(Ids.New(), "Bare");
        Assert.True(Place(town, "c", CottageInterior.TypeId, 0f, 0f).Ok);
        town.Buildings[0].Interior = null;
        Assert.Equal(2, CottageInterior.Of(town.Buildings[0]).Floors.Count);
        Assert.True(TownRules.ValidateTown(town).IsValid);
    }

    [Fact]
    public void CottagesAreNumberedInOrder()
    {
        var town = TownFactory.CreateEmpty(Ids.New(), "Numbers");
        Assert.True(Place(town, "a", CottageInterior.TypeId, 0f, 0f).Ok);
        Assert.True(Place(town, "shop", "shop", 30f, 0f).Ok);
        Assert.True(Place(town, "b", CottageInterior.TypeId, 60f, 0f).Ok);
        Assert.True(Place(town, "c", CottageInterior.SmallTypeId, 90f, 0f).Ok);

        int Number(string id) => town.Buildings.Single(b => b.Id == id).HouseNumber;
        Assert.Equal(1, Number("a"));
        Assert.Equal(0, Number("shop"));
        Assert.Equal(2, Number("b"));
        Assert.Equal(3, Number("c"));

        // Taking one down doesn't renumber the rest, or give its number to the next.
        Assert.True(TownEditor.Apply(town, new RemoveBuildingCommand { BuildingId = "b" }).Ok);
        Assert.True(Place(town, "d", CottageInterior.TypeId, 60f, 0f).Ok);
        Assert.Equal(4, Number("d"));
        Assert.Equal(3, Number("c"));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var copy = JsonSerializer.Deserialize<TownDto>(JsonSerializer.Serialize(town, options), options)!;
        Assert.Equal(4, copy.Buildings.Single(b => b.Id == "d").HouseNumber);
        Assert.True(TownRules.ValidateTown(copy).IsValid);

        copy.Buildings.Single(b => b.Id == "shop").HouseNumber = 7;
        Assert.False(TownRules.ValidateTown(copy).IsValid);
    }

    [Fact]
    public void TheBuilderOffersOnlyTheNewCottage()
    {
        Assert.Contains(Catalog.Buildable, b => b.Id == CottageInterior.TypeId);
        Assert.DoesNotContain(Catalog.Buildable, b => b.Id == CottageInterior.SmallTypeId);
        Assert.True(Catalog.TryGetBuilding(CottageInterior.SmallTypeId, out _));
    }

    [Fact]
    public void TheStockTownIsStillValid()
    {
        var town = StockTown.Create();
        Assert.Contains(town.Buildings, b => b.TypeId == CottageInterior.TypeId && b.HouseNumber > 0 && b.Interior != null);
        var validation = TownRules.ValidateTown(town);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Take(10)));
    }
}

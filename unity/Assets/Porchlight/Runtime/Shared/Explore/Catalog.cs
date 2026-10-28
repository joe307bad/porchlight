// The model catalog (which Kenney and RG Poly models the town uses, by name).
// Names are paths under Models/ without the .glb. Models/ holds every model of
// each kit (the Asset Browser shows them all); only those named here are built
// into the library and drawn. Left out: the pieces made for tile-by-tile
// building (cliffs, ground, river and path tiles, bridges, walls, floors,
// stairs), the commercial kit's low-detail copies, car parts, karts and
// wall-mounted furniture.

using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public static class Catalog
    {
        static string[] Range(string letters, System.Func<char, string> f) => letters.Select(f).ToArray();

        public static readonly string[] HOUSES = Range("abcdefghijklmnopqrstu", c => $"suburban/building-type-{c}");
        public static readonly string[] SHOPS = Range("abcdefghijklmn", c => $"commercial/building-{c}");
        public static readonly string[] TOWERS = Range("abcde", c => $"commercial/building-skyscraper-{c}");
        /// <summary>Factories and warehouses (City Kit Industrial), for workshops.</summary>
        public static readonly string[] INDUSTRIAL = Range("abcdefghijklmnopqrst", c => $"industrial/building-{c}");

        /// <summary>Boats (Kenney's Watercraft Kit): moored at the docks, and sailing the lakes.</summary>
        public static readonly string[] BOATS = new[]
        {
            "boat-row-small", "boat-row-large", "boat-sail-a", "boat-sail-b", "boat-fishing-small",
            "boat-speed-a", "boat-speed-c", "boat-speed-e", "boat-speed-g", "boat-tug-a",
        }.Select(b => $"boats/{b}").ToArray();
        /// <summary>The boats with a motor or a sail, which go out on the lakes by themselves (indices into BOATS).</summary>
        static readonly int[] SAILING = { 2, 3, 4, 5, 6, 7, 8, 9 };
        /// <summary>Boat models are drawn at one scale (tiles per model unit), so they keep their sizes.</summary>
        public const double BOAT_SCALE = 0.38;

        /// <summary>
        /// Planes (from Poly Pizza, CC BY 3.0: see Models/planes/License.txt), parked at the
        /// airport. Each with the turn (degrees about the vertical) that points its nose
        /// down +z as the cars' are, its length (tiles) and how often it turns up.
        /// </summary>
        static readonly (string name, float yaw, double length, double weight)[] PLANE_TABLE =
        {
            ("prop-small", 180, 2.0, 3),
            ("biplane-b", -90, 1.9, 2),
            ("biplane-c", 180, 1.8, 2),
            ("prop-warbird", 0, 2.0, 1),
            ("jet-private", 0, 2.4, 2),
            ("airliner-a", 0, 3.2, 1),
            ("airliner-d", 90, 3.0, 1.5),
            ("airliner-f", 0, 2.6, 1.5),
            ("airliner-g", 0, 3.2, 1),
        };
        public static readonly string[] PLANES = PLANE_TABLE.Select(p => $"planes/{p.name}").ToArray();

        /// <summary>
        /// Every vehicle model, by index: the cars, then the boats and planes. The index
        /// goes over the wire (the server takes up to 254), so new models go on the end.
        /// </summary>
        public static readonly string[] CARS = new[]
        {
            "sedan", "hatchback-sports", "suv", "van", "taxi", "delivery", "truck", "police",
            "sedan-sports", "suv-luxury", "ambulance", "firetruck", "garbage-truck", "delivery-flat", "truck-flat",
            "tractor", "tractor-police", "tractor-shovel", "race", "race-future",
        }.Select(c => $"cars/{c}").Concat(BOATS).Concat(PLANES).ToArray();
        /// <summary>How often each car model turns up (police, trucks and the odd ones out are rare).</summary>
        public static readonly double[] CAR_WEIGHTS = { 5, 3, 4, 2, 1, 1, 1, 0.4, 2, 2, 0.3, 0.3, 0.5, 0.6, 0.6, 0.3, 0.1, 0.3, 0.2, 0.2 };
        /// <summary>How many of CARS are cars (the garage's choice): those before the boats.</summary>
        public static int CarCount => CAR_WEIGHTS.Length;
        /// <summary>The everyday cars (indices into CARS) that park along the streets.</summary>
        public static readonly int[] PARKED = { 0, 1, 2, 3, 4, 5, 8, 9 };

        /// <summary>Townsfolk and players: RG Poly's Cartoon City people, one shared skeleton.</summary>
        public static readonly string[] CHARACTERS = new[]
        {
            "character-1-2-2", "character-2-1-3", "character-3-2-3", "character-4-1-1", "character-5-2-3", "character-5-3-1",
            "character-6-2-2", "character-8-3-1", "character-9-3-4", "character-9-5-7", "character-10-4-3", "character-11-3-1",
            "character-b-1", "policeman-a-4-1",
        }.Select(n => $"people/{n}").ToArray();

        public const string PEOPLE_CLIPS = "people/animations";

        static string[] Nature(params string[] names) => names.Select(n => $"nature/{n}").ToArray();

        /// <summary>Broadleaf trees, green: in town and out.</summary>
        public static readonly string[] TREES = Nature(
            "tree_default", "tree_oak", "tree_detailed", "tree_fat", "tree_simple", "tree_small", "tree_tall", "tree_thin", "tree_blocks", "tree_plateau",
            "tree_default_dark", "tree_oak_dark", "tree_detailed_dark", "tree_fat_darkh", "tree_simple_dark", "tree_small_dark", "tree_tall_dark",
            "tree_thin_dark", "tree_blocks_dark", "tree_plateau_dark").Concat(new[] { "suburban/tree-large", "suburban/tree-small" }).ToArray();
        /// <summary>The same trees turning: a few among the green ones.</summary>
        public static readonly string[] FALL_TREES = Nature(
            "tree_default_fall", "tree_oak_fall", "tree_detailed_fall", "tree_fat_fall", "tree_simple_fall", "tree_small_fall", "tree_tall_fall",
            "tree_thin_fall", "tree_blocks_fall", "tree_plateau_fall", "tree_cone_fall");
        /// <summary>Pines and firs: out of town only.</summary>
        public static readonly string[] PINES = Nature(
            "tree_pineRoundA", "tree_pineRoundB", "tree_pineRoundC", "tree_pineRoundD", "tree_pineRoundE", "tree_pineRoundF",
            "tree_pineDefaultA", "tree_pineDefaultB", "tree_pineGroundA", "tree_pineGroundB",
            "tree_pineSmallA", "tree_pineSmallB", "tree_pineSmallC", "tree_pineSmallD",
            "tree_pineTallA", "tree_pineTallB", "tree_pineTallC", "tree_pineTallD",
            "tree_pineTallA_detailed", "tree_pineTallB_detailed", "tree_pineTallC_detailed", "tree_pineTallD_detailed",
            "tree_cone", "tree_cone_dark");
        /// <summary>On sand.</summary>
        public static readonly string[] PALMS = Nature("tree_palm", "tree_palmBend", "tree_palmShort", "tree_palmTall", "tree_palmDetailedShort", "tree_palmDetailedTall");
        public static readonly string[] BUSHES = Nature(
            "plant_bush", "plant_bushLarge", "plant_bushSmall", "plant_bushDetailed", "plant_bushTriangle", "plant_bushLargeTriangle",
            "grass", "grass_large", "grass_leafs", "grass_leafsLarge", "plant_flatShort", "plant_flatTall");
        public static readonly string[] FLOWERS = Nature(
            "flower_redA", "flower_redB", "flower_redC", "flower_purpleA", "flower_purpleB", "flower_purpleC", "flower_yellowA", "flower_yellowB", "flower_yellowC");
        public static readonly string[] CROPS = Nature(
            "crop_carrot", "crop_pumpkin", "crop_turnip", "crop_melon", "crops_cornStageA", "crops_cornStageB", "crops_cornStageC", "crops_cornStageD",
            "crops_leafsStageA", "crops_leafsStageB", "crops_wheatStageA", "crops_wheatStageB", "crops_bambooStageA", "crops_bambooStageB");
        /// <summary>Small rocks and stones (rocky ground, sand, the woods' floor).</summary>
        public static readonly string[] ROCKS = Nature("ABCDEFGHI".Select(c => $"rock_small{c}")
            .Concat("ABC".Select(c => $"rock_smallFlat{c}")).Concat("AB".Select(c => $"rock_smallTop{c}"))
            .Concat("ABCDEFGHI".Select(c => $"stone_small{c}")).Concat("ABC".Select(c => $"stone_smallFlat{c}")).Concat("AB".Select(c => $"stone_smallTop{c}")).ToArray());
        /// <summary>Boulders and standing rocks (rocky ground).</summary>
        public static readonly string[] BOULDERS = Nature("ABCDEF".Select(c => $"rock_large{c}").Concat("ABCDEFGHIJ".Select(c => $"rock_tall{c}"))
            .Concat("ABCDEF".Select(c => $"stone_large{c}")).Concat("ABCDEFGHIJ".Select(c => $"stone_tall{c}")).ToArray());
        /// <summary>The woods' floor out of town: mushrooms, stumps and fallen logs.</summary>
        public static readonly string[] WILDS = Nature(
            "mushroom_red", "mushroom_redGroup", "mushroom_redTall", "mushroom_tan", "mushroom_tanGroup", "mushroom_tanTall",
            "stump_old", "stump_oldTall", "stump_round", "stump_roundDetailed", "stump_square", "stump_squareDetailed", "stump_squareDetailedWide",
            "log", "log_large", "log_stackLarge");
        /// <summary>Campsites out of town: a tent, and a fire beside it.</summary>
        public static readonly string[] TENTS = Nature("tent_detailedClosed", "tent_detailedOpen", "tent_smallClosed", "tent_smallOpen");
        public static readonly string[] CAMPFIRES = Nature("campfire_bricks", "campfire_logs", "campfire_planks", "campfire_stones");
        /// <summary>Community gardens' odds and ends.</summary>
        public static readonly string[] GARDEN_EXTRAS = Nature("log_stack", "log_stackLarge", "pot_large", "pot_small");
        /// <summary>Plazas: statues, and café parasols.</summary>
        public static readonly string[] STATUES = Nature("statue_block", "statue_column", "statue_columnDamaged", "statue_head", "statue_obelisk", "statue_ring");
        public static readonly string[] PARASOLS = { "commercial/detail-parasol-a", "commercial/detail-parasol-b" };

        /// <summary>Every furniture model a room plan may use.</summary>
        public static readonly string[] FURNITURE =
        {
            "table", "tableRound", "tableCloth", "tableCross", "tableCrossCloth", "tableGlass",
            "tableCoffee", "tableCoffeeGlass", "tableCoffeeSquare", "tableCoffeeGlassSquare",
            "chair", "chairCushion", "chairModernCushion", "chairModernFrameCushion", "chairRounded", "chairDesk",
            "desk", "deskCorner", "computerScreen", "computerKeyboard", "laptop",
            "kitchenBar", "kitchenBarEnd", "kitchenCabinet", "kitchenCabinetDrawer", "kitchenSink",
            "kitchenFridge", "kitchenFridgeLarge", "kitchenFridgeSmall", "kitchenFridgeBuiltIn", "kitchenStove", "kitchenStoveElectric",
            "kitchenMicrowave", "kitchenCoffeeMachine", "kitchenBlender", "toaster", "washer", "dryer", "washerDryerStacked",
            "loungeSofa", "loungeSofaLong", "loungeDesignSofa", "loungeChair", "loungeChairRelax", "loungeDesignChair",
            "bookcaseOpen", "bookcaseOpenLow", "bookcaseClosed", "bookcaseClosedDoors", "bookcaseClosedWide", "books",
            "pottedPlant", "plantSmall1", "plantSmall2", "plantSmall3", "lampRoundFloor", "lampSquareFloor",
            "rugRectangle", "rugRound", "rugRounded", "rugSquare", "rugDoormat",
            "cardboardBoxClosed", "cardboardBoxOpen", "bench", "benchCushion", "benchCushionLow", "stoolBar", "stoolBarSquare",
            "trashcan", "radio", "speaker", "coatRackStanding", "cabinetTelevision", "cabinetTelevisionDoors",
            "televisionModern", "televisionVintage", "bear",
        };

        public static readonly string[] FURNISHINGS = FURNITURE.Select(n => $"furniture/{n}").ToArray();
        public static readonly string[] PROPS = { "suburban/planter", "suburban/fence", "commercial/detail-awning" };

        /// <summary>Every static model the town loads.</summary>
        public static IEnumerable<string> Statics =>
            new[] { HOUSES, SHOPS, TOWERS, INDUSTRIAL, CARS, TREES, FALL_TREES, PINES, PALMS, BUSHES, FLOWERS, CROPS, ROCKS, BOULDERS,
                WILDS, TENTS, CAMPFIRES, GARDEN_EXTRAS, STATUES, PARASOLS, PROPS, FURNISHINGS }.SelectMany(x => x).Distinct();

        /// <summary>Which model families suit each kind of building ("office" is a style, not a blueprint kind).</summary>
        public static string[] KindModels(HouseStyle style) => style switch
        {
            HouseStyle.House => HOUSES,
            HouseStyle.Workshop => INDUSTRIAL.Concat(SHOPS.Take(6)).ToArray(),
            HouseStyle.Shop => SHOPS,
            HouseStyle.Office => SHOPS.Skip(6).Concat(TOWERS).ToArray(),
            HouseStyle.Hall => SHOPS.Skip(8).ToArray(),
            _ => SHOPS.Skip(10).ToArray(),
        };

        /// <summary>Alternative colourmaps each kit ships (roof and facade colours).</summary>
        public static readonly Dictionary<string, string[]> VARIATIONS = new Dictionary<string, string[]>
        {
            ["suburban"] = new[] { "variation-a", "variation-b", "variation-c" },
            ["commercial"] = new[] { "variation-a", "variation-b" },
            ["industrial"] = new[] { "variation-a", "variation-b", "variation-c" },
        };

        public static int VariationCount(string name)
        {
            var kit = name.Split('/')[0];
            return 1 + (VARIATIONS.TryGetValue(kit, out var v) ? v.Length : 0);
        }

        public static VehicleKind KindOf(string name) =>
            name.StartsWith("boats/") ? VehicleKind.Boat : name.StartsWith("planes/") ? VehicleKind.Plane : VehicleKind.Car;

        public static VehicleKind KindOf(int model) => model >= 0 && model < CARS.Length ? KindOf(CARS[model]) : VehicleKind.Car;

        public static bool IsCar(int model) => KindOf(model) == VehicleKind.Car;

        /// <summary>The turn (degrees) that points a vehicle model's nose down +z, as built into the library.</summary>
        public static float YawOf(string name)
        {
            foreach (var p in PLANE_TABLE) if (name == $"planes/{p.name}") return p.yaw;
            return 0;
        }

        /// <summary>How long a plane is drawn (tiles), or 0 for anything else.</summary>
        public static double PlaneLength(string name)
        {
            foreach (var p in PLANE_TABLE) if (name == $"planes/{p.name}") return p.length;
            return 0;
        }

        /// <summary>A boat model (index into CARS); `sailing`: one that goes out by itself, not a rowing boat.</summary>
        public static int PickBoatModel(Rng rng, bool sailing = false)
        {
            int k = sailing ? SAILING[rng.Int(0, SAILING.Length - 1)] : rng.Int(0, BOATS.Length - 1);
            return System.Array.IndexOf(CARS, BOATS[k]);
        }

        /// <summary>A plane model (index into CARS), picked by how common each is.</summary>
        public static int PickPlaneModel(Rng rng)
        {
            double total = PLANE_TABLE.Sum(p => p.weight);
            double r = rng.Next() * total;
            foreach (var p in PLANE_TABLE)
            {
                r -= p.weight;
                if (r <= 0) return System.Array.IndexOf(CARS, $"planes/{p.name}");
            }
            return System.Array.IndexOf(CARS, PLANES[0]);
        }

        /// <summary>A random look (character model) from a seeded stream.</summary>
        public static string RandomLook(Rng rng) => rng.Pick(CHARACTERS);

        /// <summary>A car model index, picked by how common each model is.</summary>
        public static int PickCarModel(Rng rng)
        {
            double total = 0;
            foreach (var w in CAR_WEIGHTS) total += w;
            double r = rng.Next() * total;
            for (int k = 0; k < CAR_WEIGHTS.Length; k++)
            {
                r -= CAR_WEIGHTS[k];
                if (r <= 0) return k;
            }
            return 0;
        }

        public static bool IsCharacter(string look) => System.Array.IndexOf(CHARACTERS, look) >= 0;
    }
}

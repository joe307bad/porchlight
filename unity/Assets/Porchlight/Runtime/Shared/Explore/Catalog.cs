// The model catalog (which Kenney and RG Poly models the town uses, by name).
// Names are paths under Models/ without the .glb.

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
        public static readonly string[] CARS = new[] { "sedan", "hatchback-sports", "suv", "van", "taxi", "delivery", "truck", "police" }.Select(c => $"cars/{c}").ToArray();
        /// <summary>How often each car model turns up (police and trucks are rare).</summary>
        public static readonly double[] CAR_WEIGHTS = { 5, 3, 4, 2, 1, 1, 1, 0.4 };

        /// <summary>Townsfolk and players: RG Poly's Cartoon City people, one shared skeleton.</summary>
        public static readonly string[] CHARACTERS = new[]
        {
            "character-1-2-2", "character-2-1-3", "character-3-2-3", "character-4-1-1", "character-5-2-3", "character-5-3-1",
            "character-6-2-2", "character-8-3-1", "character-9-3-4", "character-9-5-7", "character-10-4-3", "character-11-3-1",
            "character-b-1", "policeman-a-4-1",
        }.Select(n => $"people/{n}").ToArray();

        public const string PEOPLE_CLIPS = "people/animations";

        public static readonly string[] TREES = new[] { "tree_default", "tree_oak", "tree_detailed", "tree_fat", "tree_pineRoundA", "tree_pineRoundC", "tree_cone", "tree_simple", "tree_small" }.Select(n => $"nature/{n}").ToArray();
        public static readonly string[] BUSHES = new[] { "plant_bush", "plant_bushLarge", "plant_bushSmall", "grass" }.Select(n => $"nature/{n}").ToArray();
        public static readonly string[] FLOWERS = new[] { "flower_redA", "flower_purpleA", "flower_yellowA" }.Select(n => $"nature/{n}").ToArray();
        public static readonly string[] CROPS = new[] { "crop_carrot", "crop_pumpkin", "crop_turnip", "crops_cornStageC", "crops_leafsStageB", "crops_wheatStageB" }.Select(n => $"nature/{n}").ToArray();

        /// <summary>Every furniture model a room plan may use.</summary>
        public static readonly string[] FURNITURE =
        {
            "table", "tableRound", "tableCoffee", "chair", "chairCushion", "chairDesk", "desk", "computerScreen",
            "kitchenBar", "kitchenBarEnd", "kitchenCabinet", "kitchenCabinetDrawer", "kitchenFridge", "kitchenStove", "kitchenSink",
            "loungeSofa", "loungeChair", "bookcaseOpen", "bookcaseClosedWide", "pottedPlant", "plantSmall1", "lampRoundFloor",
            "rugRectangle", "rugRound", "cardboardBoxClosed", "cardboardBoxOpen", "bench", "stoolBar", "trashcan", "radio",
            "coatRackStanding", "cabinetTelevision",
        };

        public static readonly string[] FURNISHINGS = FURNITURE.Select(n => $"furniture/{n}").ToArray();
        public static readonly string[] PROPS = { "nature/rock_smallA", "nature/log_stack", "nature/pot_large", "suburban/planter", "suburban/fence", "commercial/detail-awning" };

        /// <summary>Every static model the town loads.</summary>
        public static IEnumerable<string> Statics =>
            HOUSES.Concat(SHOPS).Concat(TOWERS).Concat(CARS).Concat(TREES).Concat(BUSHES).Concat(FLOWERS).Concat(CROPS).Concat(PROPS).Concat(FURNISHINGS);

        /// <summary>Which model families suit each kind of building ("office" is a style, not a blueprint kind).</summary>
        public static string[] KindModels(HouseStyle style) => style switch
        {
            HouseStyle.House => HOUSES,
            HouseStyle.Workshop => SHOPS.Take(6).ToArray(),
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
        };

        public static int VariationCount(string name)
        {
            var kit = name.Split('/')[0];
            return 1 + (VARIATIONS.TryGetValue(kit, out var v) ? v.Length : 0);
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

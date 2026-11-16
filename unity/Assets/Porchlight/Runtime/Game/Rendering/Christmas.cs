// Christmas in every house (the `christmas` unlock, earned by delivering the
// presents): most houses have a decorated tree standing in a clear corner of
// the room (ChristmasCorner, where the presents mission stands its own), with a
// few wrapped presents heaped at its foot on the room's side.

using System;
using Porchlight.Core;
using Porchlight.Explore;
using UnityEngine;

namespace Porchlight.Rendering
{
    public static class Christmas
    {
        /// <summary>How tall the tree stands, and how big a present is across (tiles), as the presents mission has them.</summary>
        public const double TREE_HEIGHT = 0.62, GIFT_SIZE = 0.15;
        /// <summary>How many houses in ten have a tree (the rest none: not everyone has one).</summary>
        const int OUT_OF_TEN = 8;

        /// <summary>Whether a house (by its room's id) has a tree, the same every time the town's built.</summary>
        public static bool HasTree(int room) => (uint)(room * 2654435761u) % 10 < OUT_OF_TEN;

        /// <summary>
        /// The tree and its presents in `room` (furnished as `plan` has it), under parent; hidden until shown.
        /// Null if there's nowhere clear for it.
        /// </summary>
        public static GameObject Stand(ModelLibrary library, Transform parent, Room room, RoomPlan plan, int seed)
        {
            var spot = ChristmasCorner.Find(room, plan);
            if (spot == null) return null;
            var (at, stand) = spot.Value;
            var root = new GameObject("christmas");
            root.transform.SetParent(parent, false);
            var rng = new System.Random(seed);
            var tree = library.Get(Catalog.CHRISTMAS[0]);
            double k = TREE_HEIGHT / Math.Max(1e-3, tree.size.y);
            Props.Place(tree, root.transform, at.x, at.y, rng.NextDouble() * Math.PI * 2, k, k, k, 0.012);
            // Three or four presents round its foot, towards where you'd stand, each turned a little its own way.
            var gift = library.Get(Catalog.CHRISTMAS[1]);
            double d = Geo.Dist(at, stand);
            double toward = d < 1e-6 ? 0 : Math.Atan2(stand.y - at.y, stand.x - at.x);
            int n = 3 + rng.Next(2);
            for (int i = 0; i < n; i++)
            {
                double a = toward + (i - (n - 1) / 2.0) * 0.75 + (rng.NextDouble() - 0.5) * 0.2;
                double r = 0.17 + rng.NextDouble() * 0.05;
                double size = GIFT_SIZE * (0.7 + rng.NextDouble() * 0.45);
                double g = size / Math.Max(1e-3, Math.Max(gift.size.x, gift.size.z));
                Props.Place(gift, root.transform, at.x + Math.Cos(a) * r, at.y + Math.Sin(a) * r, rng.NextDouble() * Math.PI, g, g, g, 0.012);
            }
            return root;
        }
    }
}

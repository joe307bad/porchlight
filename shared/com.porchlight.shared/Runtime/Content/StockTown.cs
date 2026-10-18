using System;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Content
{
    /// <summary>
    /// A ready-made town for the title screen. Roads frame an empty plaza in the middle, where the
    /// menu lies, with houses and shops along every road and taller buildings towards the back.
    /// It's built with the same commands as the builder, so it always follows the town rules.
    /// </summary>
    public static class StockTown
    {
        /// <summary>The open square in the middle, kept free of buildings: -X..X by -Z..Z.</summary>
        public const float PlazaHalfWidth = 36f;
        public const float PlazaHalfDepth = 28f;

        private const float Ring = 40f;
        private const float Reach = 112f;

        public static TownDto Create()
        {
            var town = TownFactory.CreateEmpty("stock-town", "Porchlight");

            // Junctions share end points, so roads meet cleanly.
            var roads = new (string Type, float X0, float Z0, float X1, float Z1)[]
            {
                ("avenue", -Reach, -Ring, -Ring, -Ring), ("avenue", -Ring, -Ring, Ring, -Ring), ("avenue", Ring, -Ring, Reach, -Ring),
                ("street", -Ring, Ring, 0f, Ring), ("street", 0f, Ring, Ring, Ring), ("street", 0f, Ring, 0f, Reach),
                ("street", -Ring, -Reach, -Ring, -Ring), ("street", -Ring, -Ring, -Ring, Ring), ("street", -Ring, Ring, -Ring, Reach),
                ("street", Ring, -Reach, Ring, -Ring), ("street", Ring, -Ring, Ring, Ring), ("street", Ring, Ring, Ring, Reach),
            };

            var segment = 0;
            foreach (var road in roads)
            {
                var added = TownEditor.Apply(town, new AddRoadCommand
                {
                    SegmentId = $"stock-road-{segment++}", TypeId = road.Type,
                    StartX = road.X0, StartZ = road.Z0, EndX = road.X1, EndZ = road.Z1,
                });
                if (!added.Ok) throw new InvalidOperationException("Stock town road: " + added.Error);
            }

            var count = 0;
            foreach (var road in roads)
            {
                Catalog.TryGetRoad(road.Type, out var roadType);
                var a = new Vec2(road.X0, road.Z0);
                var b = new Vec2(road.X1, road.Z1);
                var direction = (b - a).Normalized;
                var length = Vec2.Distance(a, b);
                var normal = new Vec2(direction.Z, -direction.X);

                foreach (var side in new[] { 1f, -1f })
                {
                    // Leave room at the junctions, then line the road with buildings.
                    for (var along = 12f; along < length - 10f;)
                    {
                        var type = Pick(a + direction * along + normal * (side * 20f), count);
                        var center = a + direction * (along + type.Width * 0.5f)
                            + normal * (side * (roadType.Width * 0.5f + type.Depth * 0.5f + 1.5f));
                        along += type.Width + 3f;

                        if (InPlaza(center, type)) continue;
                        var placed = TownEditor.Apply(town, new PlaceBuildingCommand
                        {
                            BuildingId = $"stock-building-{count}", TypeId = type.Id, X = center.X, Z = center.Z,
                            RotationDeg = TownMath.NormalizeAngle(Vec2.HeadingOf(-normal * side)),
                        });
                        if (placed.Ok) count++;
                    }
                }
            }

            return town;
        }

        /// <summary>Homes and shops near the plaza, bigger buildings further out, offices only at the back.</summary>
        private static BuildingType Pick(Vec2 near, int index)
        {
            string id;
            var far = Math.Max(Math.Abs(near.X), Math.Abs(near.Z)) > 70f;
            if (near.Z > 50f && far) id = index % 3 == 0 ? "office" : "apartment";
            else if (far) id = new[] { "house_large", "factory", "park", "house_small" }[index % 4];
            else id = new[] { "house_small", "shop", "house_large", "house_small", "park" }[index % 5];

            Catalog.TryGetBuilding(id, out var type);
            return type;
        }

        private static bool InPlaza(Vec2 center, BuildingType type)
        {
            var reach = Math.Max(type.Width, type.Depth) * 0.5f;
            return Math.Abs(center.X) - reach < PlazaHalfWidth && Math.Abs(center.Z) - reach < PlazaHalfDepth;
        }
    }
}

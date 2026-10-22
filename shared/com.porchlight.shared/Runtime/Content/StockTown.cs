using System;
using System.Collections.Generic;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;

namespace Porchlight.Shared.Content
{
    /// <summary>
    /// A ready-made town for the title screen. Streets frame a green in the middle: a paved square,
    /// where the menu lies, ringed by flower beds, with cobbled paths out to the streets. Houses and
    /// shops line every street, taller buildings towards the back, and the back of town climbs in
    /// terraces, with paths stepping up them.
    /// It's built with the same commands as the builder, so it always follows the town rules.
    /// </summary>
    public static class StockTown
    {
        /// <summary>
        /// The paved square in the middle, -X..X by -Z..Z, on tile edges. It reaches past everything the
        /// menus lay on it (<c>MenuStyle</c>'s content area, ±28 by -19..21), so no flower bed or hedge
        /// pokes up through them.
        /// </summary>
        public const float PlazaHalfWidth = 32f;
        public const float PlazaHalfDepth = 24f;

        /// <summary>The green round it, kept free of buildings: flower beds and lawn out to here.</summary>
        private const float GreenHalfWidth = 36f;
        private const float GreenHalfDepth = 28f;

        private const float Ring = 40f;
        private const float Reach = 112f;

        public static TownDto Create()
        {
            var town = TownFactory.CreateEmpty("stock-town", "Porchlight");

            // The back of town climbs in two terraces, so there are walls and ramps to see.
            foreach (var (z, delta) in new[] { (120f, 1f), (152f, 1f) })
            {
                for (var x = -160f; x <= 160f; x += 64f)
                {
                    var raised = TownEditor.Apply(town, new ShapeTerrainCommand { X = x, Z = z, PadHalfSize = 32f, Delta = delta });
                    if (!raised.Ok) throw new InvalidOperationException("Stock town terrace: " + raised.Error);
                }
            }

            // Streets (avenues two tiles wide), painted as lines so they ramp up the terraces.
            var roads = new (bool Avenue, float X0, float Z0, float X1, float Z1)[]
            {
                (true, -Reach, -Ring, Reach, -Ring),
                (false, -Ring, Ring, Ring, Ring), (false, 0f, Ring, 0f, Reach),
                (false, -Ring, -Reach, -Ring, Reach), (false, Ring, -Reach, Ring, Reach),
            };

            foreach (var road in roads)
            {
                var a = new Vec2(road.X0, road.Z0);
                var b = new Vec2(road.X1, road.Z1);
                Paint(town, TownTiles.Line(town, a, b), "street", true);
                if (road.Avenue)
                {
                    var side = new Vec2(-(b - a).Normalized.Z, (b - a).Normalized.X) * TownTiles.Size;
                    Paint(town, TownTiles.Line(town, a + side, b + side), "street", true);
                }
            }

            // The green in the middle: a ring of flower beds, the square inside it, where the menus lie,
            // and a path out from the middle of each side to the street.
            Paint(town, TownTiles.Area(town, new Vec2(-PlazaHalfWidth - 2f, -PlazaHalfDepth - 2f), new Vec2(PlazaHalfWidth + 2f, PlazaHalfDepth + 2f)), "garden", false);
            Paint(town, TownTiles.Area(town, new Vec2(-PlazaHalfWidth + 2f, -PlazaHalfDepth + 2f), new Vec2(PlazaHalfWidth - 2f, PlazaHalfDepth - 2f)), "plaza", false);
            Paint(town, TownTiles.Line(town, new Vec2(2f, PlazaHalfDepth + 2f), new Vec2(2f, Ring - 4f)), "path", true);
            Paint(town, TownTiles.Line(town, new Vec2(2f, -PlazaHalfDepth - 2f), new Vec2(2f, -Ring + 8f)), "path", true);
            Paint(town, TownTiles.Line(town, new Vec2(PlazaHalfWidth + 2f, 2f), new Vec2(Ring - 4f, 2f)), "path", true);
            Paint(town, TownTiles.Line(town, new Vec2(-PlazaHalfWidth - 2f, 2f), new Vec2(-Ring + 4f, 2f)), "path", true);

            // Paths climbing the terraces at the back, a flight of steps up each wall.
            foreach (var x in new[] { -62f, 62f })
            {
                Paint(town, TownTiles.Line(town, new Vec2(x, -Ring + 8f), new Vec2(x, 170f)), "path", true);
            }

            // A row of garden plots along the first terrace's wall.
            foreach (var x in new[] { -98f, -94f, -90f, 90f, 94f, 98f })
            {
                Paint(town, TownTiles.Area(town, new Vec2(x, 82f), new Vec2(x, 82f)), "garden", false);
            }

            // Houses and shops along every street, facing it.
            var count = 0;
            foreach (var road in roads)
            {
                var a = new Vec2(road.X0, road.Z0);
                var b = new Vec2(road.X1, road.Z1);
                var direction = (b - a).Normalized;
                var length = Vec2.Distance(a, b);
                var normal = new Vec2(direction.Z, -direction.X);
                foreach (var side in new[] { 1f, -1f })
                {
                    for (var along = 12f; along < length - 10f;)
                    {
                        var probe = a + direction * along + normal * (side * (road.Avenue ? 14f : 10f));
                        var type = Pick(probe, count);
                        along += type.Width + 3f;
                        if (!TownQueries.TrySnapToRoad(town, type, probe, out var position, out var rotation)) continue;
                        if (InGreen(position, type)) continue;
                        var placed = TownEditor.Apply(town, new PlaceBuildingCommand
                        {
                            BuildingId = $"stock-building-{count}", TypeId = type.Id, X = position.X, Z = position.Z,
                            RotationDeg = TownMath.NormalizeAngle(rotation),
                        });
                        if (placed.Ok) count++;
                    }
                }
            }

            return town;
        }

        private static void Paint(TownDto town, List<int> cells, string surface, bool line)
        {
            var painted = TownEditor.Apply(town, new PaintTilesCommand { Cells = cells, SurfaceId = surface, Line = line });
            if (!painted.Ok && painted.Error != "It's already like that") throw new InvalidOperationException("Stock town " + surface + ": " + painted.Error);
        }

        /// <summary>Homes and shops near the plaza, bigger buildings further out, offices only at the back.</summary>
        private static BuildingType Pick(Vec2 near, int index)
        {
            string id;
            var far = Math.Max(Math.Abs(near.X), Math.Abs(near.Z)) > 70f;
            if (near.Z > 50f && far) id = index % 3 == 0 ? "office" : "apartment";
            else if (far) id = new[] { "house_large", "factory", "park", "cottage" }[index % 4];
            else id = new[] { "cottage", "shop", "house_large", "cottage", "park" }[index % 5];

            Catalog.TryGetBuilding(id, out var type);
            return type;
        }

        private static bool InGreen(Vec2 center, BuildingType type)
        {
            var reach = Math.Max(type.Width, type.Depth) * 0.5f;
            return Math.Abs(center.X) - reach < GreenHalfWidth && Math.Abs(center.Z) - reach < GreenHalfDepth;
        }
    }
}

// Where people live: a house is named by a tile of its building, not the
// building's id. Ids are generation order, which any change to the generator
// can renumber; a tile either still has a house on it or it doesn't. Pure.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public static class Homes
    {
        /// <summary>How many characters can live in a house (the server's MAX_RESIDENTS).</summary>
        public const int MAX_RESIDENTS = 2;

        /// <summary>The houses people can live in: those the town actually builds.</summary>
        public static List<Building> All(TownModel model)
        {
            var built = new HashSet<int>(Layout.Build(model).houses.Select(h => h.buildingId));
            return model.buildings.Where(b => b.kind == BuildingKind.House && b.tiles.Count > 0 && built.Contains(b.id)).ToList();
        }

        /// <summary>The tile a house is known by.</summary>
        public static TilePos Anchor(Building house) => house.tiles[0];

        /// <summary>The house on this tile, of these; null if there isn't one (any more).</summary>
        public static Building At(IEnumerable<Building> houses, int x, int y) =>
            houses.FirstOrDefault(b => b.tiles.Any(t => t.x == x && t.y == y));

        /// <summary>Just outside a building's front door, and facing out; null if it has no room with a door.</summary>
        public static (Vec2 at, double heading)? Door(IReadOnlyList<Room> rooms, int buildingId)
        {
            foreach (var room in rooms)
            {
                if (room.buildingId != buildingId) continue;
                var doors = Interiors.Doors(room);
                if (doors.Count == 0) continue;
                var door = doors.FirstOrDefault(d => d.side == room.facing);
                if (door.width == 0) door = doors[0];
                return (new Vec2(door.at.x + door.outward.x * 0.35, door.at.y + door.outward.y * 0.35), DetMath.Atan2(door.outward.x, door.outward.y));
            }
            return null;
        }
    }
}

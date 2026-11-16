// Where a Christmas tree stands in a room: a corner well inside its walls,
// clear of the furniture, its doors and whoever keeps it, as far from the door
// as it can be, with room in front of it to stand and put a present under it.
// The presents mission's tree (MissionSky.Tree) and the trees every house has
// once it's done (the `christmas` unlock, WorldParts) both stand there.

using System;
using System.Linq;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public static class ChristmasCorner
    {
        /// <summary>How far in from the walls the tree stands, the grid it's looked for on, and how far out in front of it you stand (tiles).</summary>
        const double INSET = 0.3, STEP = 0.12, STAND = 0.42;

        /// <summary>
        /// The tree's spot in `room` (furnished as `plan` has it) and where you stand to put a present under it,
        /// or null with nowhere clear. `canStand`: where someone may stand (null: anywhere clear of the furniture).
        /// </summary>
        public static (Vec2 at, Vec2 stand)? Find(Room room, RoomPlan plan, Func<double, double, bool> canStand = null)
        {
            var doors = Interiors.Doors(room);
            var middle = new Vec2((room.x0 + room.x1) / 2, (room.y0 + room.y1) / 2);
            double Clear(double x, double y)
            {
                double clear = 1;
                if (plan != null)
                    foreach (var f in plan.furniture)
                    {
                        if (f.flat) continue;
                        double dx = Math.Max(0, Math.Max(f.box.x0 - x, x - f.box.x1)), dy = Math.Max(0, Math.Max(f.box.y0 - y, y - f.box.y1));
                        clear = Math.Min(clear, Numbers.Hypot(dx, dy));
                    }
                return clear;
            }
            bool Standable(Vec2 p) => Interiors.InRoom(room, p, -0.08) && (canStand?.Invoke(p.x, p.y) ?? Clear(p.x, p.y) >= 0.1);
            Vec2? best = null, bestStand = null;
            double bestScore = double.NegativeInfinity;
            for (double x = room.x0 + INSET; x <= room.x1 - INSET + 1e-9; x += STEP)
                for (double y = room.y0 + INSET; y <= room.y1 - INSET + 1e-9; y += STEP)
                {
                    var p = new Vec2(x, y);
                    double clear = Clear(x, y);
                    if (clear < 0.2) continue;
                    // Inside its walls, not out through them.
                    if (!Interiors.InRoom(room, p, -0.2) || (canStand != null && !canStand(x, y))) continue;
                    double fromDoor = doors.Count > 0 ? doors.Min(d => Geo.Dist(d.at, p)) : 3;
                    if (fromDoor < 0.8) continue;
                    if (plan?.keeper != null && Numbers.Hypot(plan.keeper.Value.x - x, plan.keeper.Value.y - y) < 0.45) continue;
                    // Where you stand: out from it towards the middle of the room, somewhere you can.
                    double md = Geo.Dist(p, middle);
                    var toMiddle = md < 1e-6 ? new Vec2(0, 1) : new Vec2((middle.x - x) / md, (middle.y - y) / md);
                    var stand = new Vec2(x + toMiddle.x * STAND, y + toMiddle.y * STAND);
                    if (!Standable(stand)) continue;
                    double score = Math.Min(clear, 0.5) * 2 + Math.Min(fromDoor, 3) + md * 0.5;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = p;
                    bestStand = stand;
                }
            return best == null ? null : (best.Value, bestStand.Value);
        }
    }
}

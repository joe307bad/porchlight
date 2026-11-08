// Where a town's gates are: one in the middle of each edge, north, east, south
// and west, the same for every town. A gate's place depends only on the map's
// size, so where the highways meet a town can be worked out from its key alone,
// without growing it (see World/County.cs, and server/src/county.ts).

using System;
using Porchlight.Core;

namespace Porchlight.Model
{
    /// <summary>The four sides of a map, in the order a town's gates are listed.</summary>
    public enum Side { North = 0, East = 1, South = 2, West = 3 }

    public static class Compass
    {
        public static readonly Side[] Sides = { Side.North, Side.East, Side.South, Side.West };

        /// <summary>The edge tile a side's gate is on.</summary>
        public static (int x, int y) GateTile(Side side, int w, int h)
        {
            switch (side)
            {
                case Side.North: return (w / 2, 0);
                case Side.East: return (w - 1, h / 2);
                case Side.South: return (w / 2, h - 1);
                default: return (0, h / 2);
            }
        }

        /// <summary>The middle of the gate's tile (where the town's main road ends).</summary>
        public static Vec2 GateCenter(Side side, int w, int h)
        {
            var (x, y) = GateTile(side, w, h);
            return new Vec2(x + 0.5, y + 0.5);
        }

        /// <summary>Where the gate's road crosses the map's edge.</summary>
        public static Vec2 GateEdge(Side side, int w, int h)
        {
            var (x, y) = GateTile(side, w, h);
            switch (side)
            {
                case Side.North: return new Vec2(x + 0.5, 0);
                case Side.East: return new Vec2(w, y + 0.5);
                case Side.South: return new Vec2(x + 0.5, h);
                default: return new Vec2(0, y + 0.5);
            }
        }

        /// <summary>Straight out of town through that side (map y runs south).</summary>
        public static Vec2 Outward(Side side)
        {
            switch (side)
            {
                case Side.North: return new Vec2(0, -1);
                case Side.East: return new Vec2(1, 0);
                case Side.South: return new Vec2(0, 1);
                default: return new Vec2(-1, 0);
            }
        }

        public static Side Opposite(Side side) => (Side)(((int)side + 2) % 4);

        public static string Name(Side side) => side.ToString().ToLowerInvariant();

        public static Side Parse(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "north": case "n": return Side.North;
                case "east": case "e": return Side.East;
                case "south": case "s": return Side.South;
                case "west": case "w": return Side.West;
            }
            throw new ArgumentException("not a side: " + s);
        }
    }
}

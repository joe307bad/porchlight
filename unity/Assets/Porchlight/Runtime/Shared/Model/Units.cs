// Tiles and the real world. A tile is METRES_PER_TILE metres across, and the
// game runs in real seconds, so a car at 2.4 tiles a second is doing about
// 35 km/h: real driving numbers (speeds, braking, yellow lights, headways)
// carry over by dividing metres by four. Walkers are another matter: they
// stroll at 0.9 to 1.5 tiles a second, three to five times real walking pace,
// so walking timings are worked out in game seconds from their own pace, never
// borrowed from the real world.

namespace Porchlight.Model
{
    public static class Units
    {
        public const double METRES_PER_TILE = 4;

        public static double Tiles(double metres) => metres / METRES_PER_TILE;

        public static double Metres(double tiles) => tiles * METRES_PER_TILE;

        /// <summary>km/h as tiles a second.</summary>
        public static double Kmh(double kmh) => kmh / 3.6 / METRES_PER_TILE;

        /// <summary>Tiles a second as km/h.</summary>
        public static double ToKmh(double tilesPerSecond) => tilesPerSecond * METRES_PER_TILE * 3.6;

        /// <summary>Tiles a second as metres a second (or tiles a second² as m/s²).</summary>
        public static double MetresPerSecond(double tilesPerSecond) => tilesPerSecond * METRES_PER_TILE;
    }
}

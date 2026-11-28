// Work a stage can share out over threads where the host has them: the simulation and the draft worker set
// ForEach; the game (WebGL, no threads) leaves it null and the stages run a share at a time as before. Each
// piece of shared-out work is a pure function of its index (its own random stream, nothing shared written), so
// a town comes out the same either way.

using System;

namespace Porchlight.Gen
{
    public static class Work
    {
        /// <summary>Runs body(i) for every i in [0, n), on as many threads as suit; null: no threads here.</summary>
        public static Action<int, Action<int>> ForEach;
    }
}

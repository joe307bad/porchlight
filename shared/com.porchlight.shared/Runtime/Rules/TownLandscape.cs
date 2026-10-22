using System;
using System.Collections.Generic;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// Readies a town's ground for play. In the builder the tiles are exactly as built, walls and all;
    /// heading out to walk, a copy is landscaped so every part of it can be reached on foot, and so a
    /// jumble of raised tiles reads as somewhere rather than a maze of walls:
    /// <list type="bullet">
    /// <item>Grass terraces soften into hillside (<see cref="TownTiles.IsHill"/>): grass a level or two
    /// above or below grass beside it slopes to it instead of standing behind a wall.</item>
    /// <item>Then every patch of ground still walled off from the rest is joined to it, by the
    /// cheapest way across one of its walls: a flight of stone steps carved into the lower side
    /// (for a rise of one or two levels, where there's room), or failing that a ladder up the wall.
    /// Each patch gets one way in, chosen smallest first, so a jumble gets a few flights and ladders
    /// rather than one on every wall.</item>
    /// </list>
    /// The same town always comes out the same, and the original isn't changed. The steps and ladders
    /// live in <see cref="TileMapDto.Flights"/> and <see cref="TileMapDto.Ladders"/>, which are never saved.
    /// </summary>
    public static class TownLandscape
    {
        /// <summary>The most levels a carved flight of steps climbs; taller walls get a ladder.</summary>
        public const int MaxStairRise = 2;

        /// <summary>The most levels grass softens into a slope across; taller drops stay walls.</summary>
        public const int MaxSoftenedDrop = 2;

        /// <summary>A rise across a tile's edge that can still be walked up (as the player can).</summary>
        public const float WalkableStep = 0.45f;

        /// <summary>A copy of the town readied for play (see the class summary). Returns the town itself if it has no tiles.</summary>
        public static TownDto ForPlay(TownDto town)
        {
            if (town == null || !TownTiles.Has(town)) return town;
            var play = town.WithTiles(town.Tiles.Clone());
            var held = TownTiles.HeldByBuildings(play, null);
            Soften(play, held);
            Connect(play, held);
            return play;
        }

        /// <summary>Grass a level or two off the grass beside it becomes hillside, so terraces turn into slopes.</summary>
        private static void Soften(TownDto town, HashSet<int> held)
        {
            var n = TownTiles.Cells(town);
            var tiles = town.Tiles;
            var soften = new List<int>();
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    var k = TownTiles.Index(town, i, j);
                    if (held.Contains(k) || TownTiles.Surface(town, i, j) != TileSurface.Grass || TownTiles.HillMark(town, k)) continue;
                    var level = TownTiles.Level(town, i, j);
                    foreach (var (di, dj) in TownTiles.Neighbours)
                    {
                        if (!TownTiles.Inside(town, i + di, j + dj) || TownTiles.Surface(town, i + di, j + dj) != TileSurface.Grass) continue;
                        var drop = Math.Abs(TownTiles.Level(town, i + di, j + dj) - level);
                        if (drop < 1 || drop > MaxSoftenedDrop) continue;
                        soften.Add(k);
                        break;
                    }
                }
            }

            if (soften.Count == 0) return;
            if (tiles.Hills == null || tiles.Hills.Count != tiles.Levels.Count) tiles.Hills = new List<int>(new int[tiles.Levels.Count]);
            foreach (var k in soften) tiles.Hills[k] = 1;
        }

        private readonly struct Crossing
        {
            public Crossing(int cost, int lower, TileSide side, int upper, int rise)
            {
                Cost = cost;
                Lower = lower;
                Side = side;
                Upper = upper;
                Rise = rise;
            }

            public int Cost { get; }

            /// <summary>The tile at the foot of the wall, and the side of it the wall is on.</summary>
            public int Lower { get; }
            public TileSide Side { get; }

            /// <summary>The tile at the top.</summary>
            public int Upper { get; }

            /// <summary>Levels from the foot to the top.</summary>
            public int Rise { get; }
        }

        /// <summary>
        /// Joins every walled-off patch of ground to the rest: patches are the tiles you can walk between,
        /// and each wall between two patches is a possible way across, steps before ladders and low
        /// walls before high ones. Taking them cheapest first, and only where they join two patches not
        /// yet joined, links everything with as few and as gentle crossings as it can.
        /// </summary>
        private static void Connect(TownDto town, HashSet<int> held)
        {
            var n = TownTiles.Cells(town);
            var count = n * n;
            var parent = new int[count];
            for (var k = 0; k < count; k++) parent[k] = k;

            int Find(int k)
            {
                while (parent[k] != k)
                {
                    parent[k] = parent[parent[k]];
                    k = parent[k];
                }

                return k;
            }

            // Each edge once: every tile's north and east sides.
            var crossings = new List<Crossing>();
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    foreach (var side in new[] { TileSide.North, TileSide.East })
                    {
                        var (di, dj) = TownTiles.Neighbours[(int)side];
                        if (!TownTiles.Inside(town, i + di, j + dj)) continue;
                        var a = TownTiles.Index(town, i, j);
                        var b = TownTiles.Index(town, i + di, j + dj);
                        if (Walkable(town, i, j, side))
                        {
                            parent[Find(a)] = Find(b);
                            continue;
                        }

                        // The foot of the wall is the lower side.
                        var aLow = EdgeHeight(town, i, j, side) <= EdgeHeight(town, i + di, j + dj, Opposite(side));
                        var lower = aLow ? a : b;
                        var upper = aLow ? b : a;
                        var footSide = aLow ? side : Opposite(side);
                        var rise = Math.Max(1, town.Tiles.Levels[upper] - town.Tiles.Levels[lower]);
                        var cost = rise <= MaxStairRise ? rise : 10 + rise;
                        if (held.Contains(upper)) cost += 5;
                        crossings.Add(new Crossing(cost, lower, footSide, upper, rise));
                    }
                }
            }

            // Cheapest first; ties in tile order, so the same town always gets the same crossings.
            crossings.Sort((x, y) => x.Cost != y.Cost ? x.Cost.CompareTo(y.Cost) : x.Lower != y.Lower ? x.Lower.CompareTo(y.Lower) : x.Side.CompareTo(y.Side));
            foreach (var crossing in crossings)
            {
                var a = Find(crossing.Lower);
                var b = Find(crossing.Upper);
                if (a == b || held.Contains(crossing.Lower)) continue;
                if (CanCarveSteps(town, crossing)) SetFlight(town, crossing);
                else if (!TownTiles.TryFlight(town, crossing.Lower % n, crossing.Lower / n, out var taken, out _) || taken != crossing.Side) SetLadder(town, crossing);
                else continue;
                parent[a] = b;
            }
        }

        /// <summary>
        /// Steps fit when the wall is a level or two, the foot is grass, plaza or path standing flat with
        /// no other way up already on it, and the top is flat where they arrive.
        /// </summary>
        private static bool CanCarveSteps(TownDto town, Crossing c)
        {
            if (c.Rise > MaxStairRise) return false;
            var n = TownTiles.Cells(town);
            int i = c.Lower % n, j = c.Lower / n, ui = c.Upper % n, uj = c.Upper / n;
            var surface = TownTiles.Surface(town, i, j);
            if (surface != TileSurface.Grass && surface != TileSurface.Plaza && surface != TileSurface.Path) return false;
            if (TownTiles.IsHill(town, i, j) || TownTiles.IsHill(town, ui, uj)) return false;
            if (TownTiles.TryFlight(town, i, j, out _, out _) || TownTiles.TryClimb(town, i, j, out _)) return false;
            return !TownTiles.HasLadder(town, i, j, c.Side);
        }

        private static void SetFlight(TownDto town, Crossing c)
        {
            var tiles = town.Tiles;
            if (tiles.Flights == null) tiles.Flights = new List<int>(new int[tiles.Levels.Count]);
            tiles.Flights[c.Lower] = 1 + (int)c.Side + 4 * (c.Rise - 1);
        }

        private static void SetLadder(TownDto town, Crossing c)
        {
            var tiles = town.Tiles;
            if (tiles.Ladders == null) tiles.Ladders = new List<int>(new int[tiles.Levels.Count]);
            tiles.Ladders[c.Lower] |= 1 << (int)c.Side;
        }

        /// <summary>Whether somewhere along a tile's side the ground runs on to the next tile closely enough to walk across.</summary>
        private static bool Walkable(TownDto town, int i, int j, TileSide side)
        {
            var (di, dj) = TownTiles.Neighbours[(int)side];
            int ni = i + di, nj = j + dj;

            // Two flat tiles: walkable at the same level, not otherwise, unless one climbs to the other.
            var plain = !TownTiles.IsHill(town, i, j) && !TownTiles.IsHill(town, ni, nj) && !Climbs(town, i, j) && !Climbs(town, ni, nj);
            if (plain) return TownTiles.Level(town, i, j) == TownTiles.Level(town, ni, nj);

            var c = TownTiles.Center(town, i, j);
            var outward = new Vec2(di, dj);
            var along = new Vec2(-dj, di);
            var half = TownTiles.Spacing(town) * 0.5f;
            for (var t = -0.9f; t <= 0.91f; t += 0.15f)
            {
                var edge = c + outward * half + along * (half * t);
                var mine = TownTiles.HeightAt(town, edge - outward * 0.01f);
                var theirs = TownTiles.HeightAt(town, edge + outward * 0.01f);
                if (MathF.Abs(mine - theirs) < WalkableStep) return true;
            }

            return false;
        }

        /// <summary>
        /// How many patches of ground in a town (readied for play) can't be walked between, counting
        /// steps and ladders as ways across. 1 when everywhere can be reached.
        /// </summary>
        public static int CountPatches(TownDto town)
        {
            if (!TownTiles.Has(town)) return 1;
            var n = TownTiles.Cells(town);
            var parent = new int[n * n];
            for (var k = 0; k < parent.Length; k++) parent[k] = k;
            int Find(int k)
            {
                while (parent[k] != k) k = parent[k] = parent[parent[k]];
                return k;
            }

            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    foreach (var side in new[] { TileSide.North, TileSide.East })
                    {
                        var (di, dj) = TownTiles.Neighbours[(int)side];
                        if (!TownTiles.Inside(town, i + di, j + dj)) continue;
                        if (Walkable(town, i, j, side) || TownTiles.HasLadder(town, i, j, side) || TownTiles.HasLadder(town, i + di, j + dj, Opposite(side)))
                            parent[Find(TownTiles.Index(town, i, j))] = Find(TownTiles.Index(town, i + di, j + dj));
                    }
                }
            }

            var patches = 0;
            for (var k = 0; k < parent.Length; k++) patches += Find(k) == k ? 1 : 0;
            return patches;
        }

        /// <summary>
        /// Whether a point on a tile is somewhere plants shouldn't grow: on a flight of steps made for
        /// play, or at the foot of a ladder.
        /// </summary>
        public static bool KeepClear(TownDto town, int i, int j, Vec2 p)
        {
            var size = TownTiles.Spacing(town);
            if (TownTiles.TryFlight(town, i, j, out var side, out var rise))
            {
                var (along, across) = TownTiles.Local(town, i, j, side, p);
                if (MathF.Abs(across) < TownTiles.PathWidth * 0.5f + 0.4f && along > size - TownTiles.StairRun * rise - 0.4f) return true;
            }

            for (var s = 0; s < 4; s++)
            {
                if (!TownTiles.HasLadder(town, i, j, (TileSide)s)) continue;
                var (along, across) = TownTiles.Local(town, i, j, (TileSide)s, p);
                if (MathF.Abs(across) < 0.9f && along > size - 1.2f) return true;
            }

            return false;
        }

        private static bool Climbs(TownDto town, int i, int j) => TownTiles.TryClimb(town, i, j, out _) || TownTiles.TryFlight(town, i, j, out _, out _);

        /// <summary>The average height along a tile's side, just inside it.</summary>
        private static float EdgeHeight(TownDto town, int i, int j, TileSide side)
        {
            var (di, dj) = TownTiles.Neighbours[(int)side];
            var c = TownTiles.Center(town, i, j);
            var outward = new Vec2(di, dj);
            var along = new Vec2(-dj, di);
            var half = TownTiles.Spacing(town) * 0.5f - 0.01f;
            var edge = c + outward * half;
            return (TownTiles.HeightAt(town, edge - along * half) + TownTiles.HeightAt(town, edge) + TownTiles.HeightAt(town, edge + along * half)) / 3f;
        }

        private static TileSide Opposite(TileSide side) => (TileSide)(((int)side + 2) % 4);
    }
}

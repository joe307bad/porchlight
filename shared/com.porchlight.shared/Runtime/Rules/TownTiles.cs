using System;
using System.Collections.Generic;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>A change to one tile: its index in the map, and the level, surface and hillside mark it ends up with.</summary>
    public readonly struct TileChange
    {
        public TileChange(int index, int level, TileSurface surface, bool hill)
        {
            Index = index;
            Level = level;
            Surface = surface;
            Hill = hill;
        }

        public int Index { get; }
        public int Level { get; }
        public TileSurface Surface { get; }
        public bool Hill { get; }
    }

    /// <summary>A side of a tile: north is +Z, east +X.</summary>
    public enum TileSide
    {
        North,
        East,
        South,
        West,
    }

    /// <summary>
    /// The town's ground: a square of <see cref="Size"/> metre tiles, each with a whole-metre level and a
    /// surface. Everything follows from that, the same in the builder and in play, with nothing to
    /// guess or optimise:
    /// <list type="bullet">
    /// <item>A tile is flat at its level. Where neighbours differ, the higher one is held up by a
    /// retaining wall (the renderer's job; walking can't climb it).</item>
    /// <item>A street tile next to a street one level up is a ramp the whole way across; a path tile
    /// next to a path, street or plaza one level up climbs to it by a flight of steps
    /// (<see cref="StairRun"/> long, <see cref="PathWidth"/> wide) against that side, with grass either side.</item>
    /// <item>A hillside tile (grass marked in <see cref="TileMapDto.Hills"/>) isn't flat: its ground
    /// slopes between its corners, each the average of the hillside tiles round it, so hillsides run
    /// smoothly into each other. A corner also touching ground that isn't hillside, within
    /// <see cref="HillBlend"/> of that average, takes that ground's height instead, so a hill runs
    /// out onto flat ground with no wall. Further apart, a wall makes up the difference as usual.</item>
    /// <item>Buildings stand on whole tiles at one level; placing one levels the tiles under it
    /// (and flattens any hillside there).</item>
    /// <item>Outside the town, the countryside is at level 0.</item>
    /// </list>
    /// </summary>
    public static class TownTiles
    {
        /// <summary>Metres a side: the building grid's spacing.</summary>
        public const float Size = TownGrid.Spacing;

        public const int MinLevel = -4;
        public const int MaxLevel = 24;

        /// <summary>How long a flight of steps up one level is, and how wide (a path's width).</summary>
        public const float StairRun = 1.6f;
        public const int StairSteps = 5;
        public const float PathWidth = 2.4f;

        /// <summary>How far a hillside corner reaches up or down to meet the flat ground beside it.</summary>
        public const float HillBlend = 1.5f;

        /// <summary>The neighbour across each side, in <see cref="TileSide"/> order.</summary>
        public static readonly (int DI, int DJ)[] Neighbours = { (0, 1), (1, 0), (0, -1), (-1, 0) };

        public static int CellsFor(float townSize) => Math.Max(1, (int)MathF.Round(townSize / Size));

        /// <summary>A town's worth of grass at level 0.</summary>
        public static TileMapDto Flat(float townSize)
        {
            var cells = CellsFor(townSize);
            var tiles = new TileMapDto { Cells = cells };
            for (var k = 0; k < cells * cells; k++)
            {
                tiles.Levels.Add(0);
                tiles.Surfaces.Add((int)TileSurface.Grass);
            }

            return tiles;
        }

        /// <summary>Whether the town has a usable tile map.</summary>
        public static bool Has(TownDto town)
        {
            var tiles = town?.Tiles;
            if (tiles?.Levels == null || tiles.Surfaces == null) return false;
            var count = tiles.Cells * tiles.Cells;
            return tiles.Cells == CellsFor(town.Size) && tiles.Levels.Count == count && tiles.Surfaces.Count == count;
        }

        public static int Cells(TownDto town) => town.Tiles.Cells;

        public static int Index(TownDto town, int i, int j) => j * town.Tiles.Cells + i;

        public static bool Inside(TownDto town, int i, int j) => i >= 0 && j >= 0 && i < town.Tiles.Cells && j < town.Tiles.Cells;

        /// <summary>The tile a point is on, if it's in the town.</summary>
        public static bool TryCell(TownDto town, Vec2 p, out int i, out int j)
        {
            var half = town.Size * 0.5f;
            var spacing = town.Size / town.Tiles.Cells;
            i = (int)MathF.Floor((p.X + half) / spacing);
            j = (int)MathF.Floor((p.Z + half) / spacing);
            if (p.X >= half) i = town.Tiles.Cells - 1;
            if (p.Z >= half) j = town.Tiles.Cells - 1;
            return p.X >= -half && p.Z >= -half && p.X <= half && p.Z <= half && Inside(town, i, j);
        }

        public static float Spacing(TownDto town) => town.Size / town.Tiles.Cells;

        public static Vec2 Center(TownDto town, int i, int j)
        {
            var spacing = Spacing(town);
            var half = town.Size * 0.5f;
            return new Vec2(-half + (i + 0.5f) * spacing, -half + (j + 0.5f) * spacing);
        }

        /// <summary>A tile's level; the countryside's (0) outside the town.</summary>
        public static int Level(TownDto town, int i, int j) => Inside(town, i, j) ? town.Tiles.Levels[Index(town, i, j)] : 0;

        public static TileSurface Surface(TownDto town, int i, int j) =>
            Inside(town, i, j) ? (TileSurface)town.Tiles.Surfaces[Index(town, i, j)] : TileSurface.Grass;

        public static bool IsPaved(TileSurface surface) => surface == TileSurface.Street || surface == TileSurface.Path || surface == TileSurface.Plaza;

        /// <summary>Whether a path or street can join a tile of this surface: streets join streets (and plazas); paths join anything paved.</summary>
        public static bool Joins(TileSurface from, TileSurface to) =>
            from == TileSurface.Path ? IsPaved(to) : from == TileSurface.Street && (to == TileSurface.Street || to == TileSurface.Plaza);

        /// <summary>
        /// Whether a tile climbs one level to a neighbour: a street to a street (a ramp), or a path to
        /// any paved tile (steps). The first side that qualifies, north, east, south, then west.
        /// </summary>
        public static bool TryClimb(TownDto town, int i, int j, out TileSide side)
        {
            side = TileSide.North;
            var surface = Surface(town, i, j);
            if (surface != TileSurface.Street && surface != TileSurface.Path) return false;
            var level = Level(town, i, j);
            for (var s = 0; s < 4; s++)
            {
                var (di, dj) = Neighbours[s];
                if (!Inside(town, i + di, j + dj) || Level(town, i + di, j + dj) != level + 1) continue;
                if (!Joins(surface, Surface(town, i + di, j + dj))) continue;
                side = (TileSide)s;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Where a point is across a tile, seen from a side: how far in from the opposite side (0 to
        /// <see cref="Size"/>), and how far off the middle line running towards that side.
        /// </summary>
        public static (float Along, float Across) Local(TownDto town, int i, int j, TileSide side, Vec2 p)
        {
            var c = Center(town, i, j);
            var half = Spacing(town) * 0.5f;
            switch (side)
            {
                case TileSide.North: return (p.Z - (c.Z - half), p.X - c.X);
                case TileSide.East: return (p.X - (c.X - half), p.Z - c.Z);
                case TileSide.South: return (c.Z + half - p.Z, p.X - c.X);
                default: return (c.X + half - p.X, p.Z - c.Z);
            }
        }

        /// <summary>Whether the tile is marked hillside, whatever its surface.</summary>
        public static bool HillMark(TownDto town, int k)
        {
            var hills = town.Tiles.Hills;
            return hills != null && hills.Count == town.Tiles.Levels.Count && hills[k] != 0;
        }

        /// <summary>Whether a tile is hillside: grass marked as such. Anything paved or planted on it stands flat.</summary>
        public static bool IsHill(TownDto town, int i, int j) =>
            Inside(town, i, j) && Surface(town, i, j) == TileSurface.Grass && HillMark(town, Index(town, i, j));

        /// <summary>
        /// The height to stand at: the tile's level, up a street's ramp or a path's steps where it
        /// climbs, across the slope of a hillside, and 0 in the countryside. Walls are where it jumps
        /// from one tile to the next.
        /// </summary>
        public static float HeightAt(TownDto town, Vec2 p)
        {
            if (!TryCell(town, p, out var i, out var j)) return 0f;
            if (!IsHill(town, i, j)) return FlatHeightAt(town, i, j, p);

            // Across the tile between its four corners.
            var size = Spacing(town);
            var half = town.Size * 0.5f;
            var u = Math.Clamp((p.X + half) / size - i, 0f, 1f);
            var v = Math.Clamp((p.Z + half) / size - j, 0f, 1f);
            var h00 = Corner(town, i, j);
            var h10 = Corner(town, i + 1, j);
            var h01 = Corner(town, i, j + 1);
            var h11 = Corner(town, i + 1, j + 1);
            return (h00 * (1f - u) + h10 * u) * (1f - v) + (h01 * (1f - u) + h11 * u) * v;
        }

        /// <summary>
        /// The height of a hillside corner, numbered on the lattice of tile corners (0 to Cells a side):
        /// the average of the hillside tiles round it, or the height of the flat ground round it closest
        /// to that average, if it's within <see cref="HillBlend"/>.
        /// </summary>
        public static float Corner(TownDto town, int ci, int cj)
        {
            var size = Spacing(town);
            var half = town.Size * 0.5f;
            var point = new Vec2(-half + ci * size, -half + cj * size);
            float sum = 0f;
            var count = 0;
            Span<float> flat = stackalloc float[4];
            var flats = 0;
            for (var dj = -1; dj <= 0; dj++)
            {
                for (var di = -1; di <= 0; di++)
                {
                    int i = ci + di, j = cj + dj;
                    if (IsHill(town, i, j))
                    {
                        sum += Level(town, i, j);
                        count++;
                    }
                    else
                    {
                        // Read just inside the tile, towards its middle.
                        var inside = point + new Vec2(di < 0 ? -0.01f : 0.01f, dj < 0 ? -0.01f : 0.01f);
                        flat[flats++] = Inside(town, i, j) ? FlatHeightAt(town, i, j, inside) : 0f;
                    }
                }
            }

            if (count == 0) return flats > 0 ? flat[0] : 0f;
            var average = sum / count;
            var best = average;
            var closest = HillBlend + 1e-4f;
            for (var k = 0; k < flats; k++)
            {
                var gap = MathF.Abs(flat[k] - average);
                if (gap > closest) continue;
                closest = gap;
                best = flat[k];
            }

            return best;
        }

        /// <summary>
        /// How far the ground jumps across one side of a tile (to its neighbour, or the countryside):
        /// the biggest difference between the two sides' heights along it. Near 0 where they run on.
        /// </summary>
        public static float Step(TownDto town, int i, int j, TileSide side)
        {
            var c = Center(town, i, j);
            var (di, dj) = Neighbours[(int)side];
            var outward = new Vec2(di, dj);
            var along = new Vec2(-dj, di);
            var half = Spacing(town) * 0.5f;
            var step = 0f;
            foreach (var t in new[] { -0.95f, 0f, 0.95f })
            {
                var edge = c + outward * half + along * (half * t);
                step = Math.Max(step, MathF.Abs(HeightAt(town, edge - outward * 0.01f) - HeightAt(town, edge + outward * 0.01f)));
            }

            return step;
        }

        /// <summary>The height on a tile that isn't hillside: its level, or up its ramp or steps.</summary>
        private static float FlatHeightAt(TownDto town, int i, int j, Vec2 p)
        {
            float level = Level(town, i, j);
            if (!TryClimb(town, i, j, out var side)) return level;

            var size = Spacing(town);
            var (along, across) = Local(town, i, j, side, p);
            if (Surface(town, i, j) == TileSurface.Street) return level + Math.Clamp(along / size, 0f, 1f);
            if (MathF.Abs(across) > PathWidth * 0.5f + 0.05f) return level;
            return level + Math.Clamp((along - (size - StairRun)) / StairRun, 0f, 1f);
        }

        /// <summary>The tiles a building's footprint stands on (any part of it, bar a sliver along an edge).</summary>
        public static List<(int I, int J)> Under(TownDto town, in OrientedRect footprint)
        {
            var cells = new List<(int, int)>();
            var spacing = Spacing(town);
            var half = town.Size * 0.5f;
            var reachX = footprint.ExtentAlong(new Vec2(1f, 0f));
            var reachZ = footprint.ExtentAlong(new Vec2(0f, 1f));
            var i0 = Math.Max(0, (int)MathF.Floor((footprint.Center.X - reachX + half) / spacing));
            var i1 = Math.Min(town.Tiles.Cells - 1, (int)MathF.Floor((footprint.Center.X + reachX + half) / spacing));
            var j0 = Math.Max(0, (int)MathF.Floor((footprint.Center.Z - reachZ + half) / spacing));
            var j1 = Math.Min(town.Tiles.Cells - 1, (int)MathF.Floor((footprint.Center.Z + reachZ + half) / spacing));
            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    var tile = new OrientedRect(Center(town, i, j), spacing * 0.5f - 0.05f, spacing * 0.5f - 0.05f, 0f);
                    if (tile.Overlaps(footprint)) cells.Add((i, j));
                }
            }

            return cells;
        }

        /// <summary>The level a building stands at: that of the tile under its middle.</summary>
        public static int BaseLevel(TownDto town, in OrientedRect footprint) =>
            TryCell(town, footprint.Center, out var i, out var j) ? Level(town, i, j) : 0;

        /// <summary>
        /// The tiles to level for a building with this footprint: every tile under it to the level of the
        /// one under its middle. Fails if it would stand on a street or path, or share a tile with another
        /// building on a different level.
        /// </summary>
        public static EditResult CanLevelFor(TownDto town, in OrientedRect footprint, string ignoreBuildingId, out List<TileChange> changes)
        {
            changes = new List<TileChange>();
            var under = Under(town, footprint);
            var level = BaseLevel(town, footprint);
            foreach (var (i, j) in under)
            {
                var surface = Surface(town, i, j);
                if (surface == TileSurface.Street || surface == TileSurface.Path) return EditResult.Fail(surface == TileSurface.Street ? "Buildings can't stand on a street" : "Buildings can't stand on a path");
            }

            var held = HeldByBuildings(town, ignoreBuildingId);
            foreach (var (i, j) in under)
            {
                var k = Index(town, i, j);
                var current = town.Tiles.Levels[k];
                if (current == level && !HillMark(town, k)) continue;
                if (held.Contains(k) && current != level) return EditResult.Fail("Can't level this beside a building on different ground");
                changes.Add(new TileChange(k, level, Surface(town, i, j), false));
            }

            return EditResult.Success;
        }

        /// <summary>
        /// Raising or lowering the ground: every tile whose middle is within <paramref name="halfSize"/> of
        /// <paramref name="center"/> (a square) goes up or down by <paramref name="delta"/> levels. Tiles
        /// under a building stay put, unless the whole building is in the square, which then rides along.
        /// </summary>
        public static EditResult CanRaise(TownDto town, Vec2 center, float halfSize, int delta, out List<TileChange> changes)
        {
            changes = new List<TileChange>();
            if (delta == 0) return EditResult.Fail("Nothing to change");

            var inSquare = new HashSet<int>();
            var n = town.Tiles.Cells;
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    var c = Center(town, i, j);
                    if (MathF.Abs(c.X - center.X) <= halfSize + 0.01f && MathF.Abs(c.Z - center.Z) <= halfSize + 0.01f) inSquare.Add(Index(town, i, j));
                }
            }

            // A building whose tiles are all in the square rides along; any other's tiles hold.
            var held = new HashSet<int>();
            foreach (var building in town.Buildings)
            {
                if (!TownRules.TryFootprint(building, out var footprint)) continue;
                var cells = Under(town, footprint).ConvertAll(c => Index(town, c.I, c.J));
                if (!cells.TrueForAll(inSquare.Contains)) held.UnionWith(cells);
            }

            foreach (var k in inSquare)
            {
                if (held.Contains(k)) continue;
                var level = Math.Clamp(town.Tiles.Levels[k] + delta, MinLevel, MaxLevel);
                if (level != town.Tiles.Levels[k]) changes.Add(new TileChange(k, level, (TileSurface)town.Tiles.Surfaces[k], HillMark(town, k)));
            }

            return changes.Count > 0 ? EditResult.Success : EditResult.Fail(delta > 0 ? "The ground can't go any higher here" : "The ground can't go any lower here");
        }

        /// <summary>
        /// Painting tiles a surface. Streets and paths can't go under buildings. Painting anything but
        /// grass flattens a hillside tile (at its level); grass keeps it hillside. Painted as a
        /// <paramref name="line"/> (in order), a street or path keeps each tile within one level of the
        /// last, so it can always ramp or step from one to the next.
        /// </summary>
        public static EditResult CanPaint(TownDto town, IList<int> cells, TileSurface surface, bool line, out List<TileChange> changes)
        {
            changes = new List<TileChange>();
            if (cells == null || cells.Count == 0) return EditResult.Fail("Nothing to paint");
            if (!Enum.IsDefined(typeof(TileSurface), surface)) return EditResult.Fail("Unknown surface");
            var count = town.Tiles.Cells * town.Tiles.Cells;
            foreach (var k in cells)
            {
                if (k < 0 || k >= count) return EditResult.Fail("Outside the town boundary");
            }

            var underBuildings = HeldByBuildings(town, null);
            if (surface == TileSurface.Street || surface == TileSurface.Path)
            {
                foreach (var k in cells)
                {
                    if (underBuildings.Contains(k)) return EditResult.Fail("There's a building in the way");
                }
            }

            var chain = line && (surface == TileSurface.Street || surface == TileSurface.Path);
            var previous = int.MinValue;
            var seen = new HashSet<int>();
            foreach (var k in cells)
            {
                if (!seen.Add(k)) continue;
                var level = town.Tiles.Levels[k];
                if (chain && previous != int.MinValue) level = Math.Clamp(level, previous - 1, previous + 1);
                previous = level;
                var hill = surface == TileSurface.Grass && HillMark(town, k);
                if (level != town.Tiles.Levels[k] || (int)surface != town.Tiles.Surfaces[k] || hill != HillMark(town, k)) changes.Add(new TileChange(k, level, surface, hill));
            }

            return changes.Count > 0 ? EditResult.Success : EditResult.Fail("It's already like that");
        }

        public static void Apply(TownDto town, List<TileChange> changes)
        {
            var tiles = town.Tiles;
            foreach (var change in changes)
            {
                tiles.Levels[change.Index] = change.Level;
                tiles.Surfaces[change.Index] = (int)change.Surface;
                if (tiles.Hills == null || tiles.Hills.Count != tiles.Levels.Count)
                {
                    if (!change.Hill) continue;
                    tiles.Hills = new List<int>(new int[tiles.Levels.Count]);
                }

                tiles.Hills[change.Index] = change.Hill ? 1 : 0;
            }
        }

        /// <summary>
        /// The tiles from one point's to another's, in order, each sharing a side with the last: a line
        /// for painting a street or path.
        /// </summary>
        public static List<int> Line(TownDto town, Vec2 from, Vec2 to)
        {
            var cells = new List<int>();
            if (!TryCell(town, from, out var i, out var j) || !TryCell(town, to, out var i1, out var j1)) return cells;
            var di = Math.Abs(i1 - i);
            var dj = Math.Abs(j1 - j);
            var si = Math.Sign(i1 - i);
            var sj = Math.Sign(j1 - j);
            cells.Add(Index(town, i, j));

            // Step whichever way keeps closest to the straight line.
            int ki = 0, kj = 0;
            while (ki < di || kj < dj)
            {
                if (di == 0 || (dj > 0 && (0.5f + ki) / di > (0.5f + kj) / dj))
                {
                    j += sj;
                    kj++;
                }
                else
                {
                    i += si;
                    ki++;
                }

                cells.Add(Index(town, i, j));
            }

            return cells;
        }

        /// <summary>Every tile in the rectangle between two points' tiles.</summary>
        public static List<int> Area(TownDto town, Vec2 from, Vec2 to)
        {
            var cells = new List<int>();
            if (!TryCell(town, from, out var i0, out var j0) || !TryCell(town, to, out var i1, out var j1)) return cells;
            for (var j = Math.Min(j0, j1); j <= Math.Max(j0, j1); j++)
            {
                for (var i = Math.Min(i0, i1); i <= Math.Max(i0, i1); i++) cells.Add(Index(town, i, j));
            }

            return cells;
        }

        /// <summary>The tiles under every building but one.</summary>
        public static HashSet<int> HeldByBuildings(TownDto town, string ignoreBuildingId)
        {
            var held = new HashSet<int>();
            foreach (var building in town.Buildings)
            {
                if (building.Id == ignoreBuildingId || !TownRules.TryFootprint(building, out var footprint)) continue;
                foreach (var (i, j) in Under(town, footprint)) held.Add(Index(town, i, j));
            }

            return held;
        }

        /// <summary>
        /// Brings a town saved before tiles up to date: each tile takes the old ground's height at its
        /// middle (to the nearest metre), roads become street and path tiles (each road's tiles within a
        /// level of the last, so they ramp and step), and buildings level the tiles under them. Then the
        /// old roads and heightmap go. Returns whether anything changed.
        /// </summary>
        public static bool Upgrade(TownDto town)
        {
            if (town == null) return false;
            if (Has(town))
            {
                var changed = town.SchemaVersion != TownDto.CurrentSchemaVersion || town.Terrain != null || (town.Roads?.Segments?.Count ?? 0) > 0;
                town.SchemaVersion = TownDto.CurrentSchemaVersion;
                town.Terrain = null;
                town.Roads = new RoadNetworkDto();
                return changed;
            }

            var legacy = town.Clone();
            town.Tiles = Flat(town.Size);
            var n = town.Tiles.Cells;
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    var h = TownTerrain.LegacyHeightAt(legacy, Center(town, i, j));
                    town.Tiles.Levels[Index(town, i, j)] = Math.Clamp((int)MathF.Round(h), MinLevel, MaxLevel);
                }
            }

            Tidy(town);

            // Roads, widest first so a path never paves over a street.
            var nodes = new Dictionary<string, Vec2>(StringComparer.Ordinal);
            foreach (var node in legacy.Roads?.Nodes ?? new List<RoadNodeDto>())
            {
                if (node?.Id != null) nodes[node.Id] = new Vec2(node.X, node.Z);
            }

            var roads = new List<(Vec2 A, Vec2 B, float Width, TileSurface Surface)>();
            foreach (var segment in legacy.Roads?.Segments ?? new List<RoadSegmentDto>())
            {
                if (segment == null || !nodes.TryGetValue(segment.StartNodeId ?? "", out var a) || !nodes.TryGetValue(segment.EndNodeId ?? "", out var b)) continue;
                var (width, surface) = segment.TypeId == "avenue" ? (10f, TileSurface.Street) : segment.TypeId == "path" ? (3f, TileSurface.Path) : (6f, TileSurface.Street);
                roads.Add((a, b, width, surface));
            }

            roads.Sort((x, y) => y.Width.CompareTo(x.Width));
            foreach (var (a, b, width, surface) in roads)
            {
                var length = Vec2.Distance(a, b);
                var along = length > 1e-3f ? (b - a) * (1f / length) : new Vec2(1f, 0f);
                var reach = Math.Max(width * 0.5f, Spacing(town) * 0.5f);
                var paved = new List<(float T, int K)>();
                for (var j = 0; j < n; j++)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var c = Center(town, i, j);
                        if (Segments.Distance(c, a, b) > reach) continue;
                        var k = Index(town, i, j);
                        if (surface == TileSurface.Path && town.Tiles.Surfaces[k] == (int)TileSurface.Street) continue;
                        paved.Add((Vec2.Dot(c - a, along), k));
                    }
                }

                paved.Sort((x, y) => x.T.CompareTo(y.T));
                var previous = int.MinValue;
                foreach (var (_, k) in paved)
                {
                    var level = town.Tiles.Levels[k];
                    if (previous != int.MinValue) level = Math.Clamp(level, previous - 1, previous + 1);
                    town.Tiles.Levels[k] = previous = level;
                    town.Tiles.Surfaces[k] = (int)surface;
                }
            }

            // Buildings keep their ground: the tiles under each, at the height it stood at, and never paved.
            foreach (var building in town.Buildings)
            {
                if (!TownRules.TryFootprint(building, out var footprint)) continue;
                var level = Math.Clamp((int)MathF.Round(TownTerrain.LegacyBaseHeight(legacy, footprint)), MinLevel, MaxLevel);
                foreach (var (i, j) in Under(town, footprint))
                {
                    var k = Index(town, i, j);
                    town.Tiles.Levels[k] = level;
                    if (town.Tiles.Surfaces[k] == (int)TileSurface.Street || town.Tiles.Surfaces[k] == (int)TileSurface.Path) town.Tiles.Surfaces[k] = (int)TileSurface.Grass;
                }
            }

            foreach (var shape in town.TerrainShapes ?? new List<TerrainShapeDto>())
            {
                if (shape != null) shape.Overlay = false;
            }

            town.Roads = new RoadNetworkDto();
            town.Terrain = null;
            town.SchemaVersion = TownDto.CurrentSchemaVersion;
            return true;
        }

        /// <summary>
        /// Smooths out the odd tile a rounded heightmap leaves standing alone: one whose level at least
        /// three of its four neighbours share a different level from takes theirs, so a slope converts
        /// to clean terraces rather than a scatter of one-tile bumps and pits, each with its own walls.
        /// </summary>
        private static void Tidy(TownDto town)
        {
            var n = town.Tiles.Cells;
            var levels = town.Tiles.Levels;
            for (var pass = 0; pass < 3; pass++)
            {
                var changed = false;
                for (var j = 0; j < n; j++)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var k = Index(town, i, j);
                        var mine = levels[k];
                        foreach (var (di, dj) in Neighbours)
                        {
                            var other = Level(town, i + di, j + dj);
                            if (other == mine) continue;
                            var agree = 0;
                            foreach (var (ei, ej) in Neighbours)
                            {
                                if (Level(town, i + ei, j + ej) == other) agree++;
                            }

                            if (agree < 3) continue;
                            levels[k] = other;
                            changed = true;
                            break;
                        }
                    }
                }

                if (!changed) break;
            }
        }

        /// <summary>The server's check of a saved tile map.</summary>
        internal static void Validate(TownDto town, ValidationResult result)
        {
            var tiles = town.Tiles;
            if (tiles == null)
            {
                result.Add("Tiles are required");
                return;
            }

            var cells = CellsFor(town.Size);
            if (tiles.Cells != cells || tiles.Levels == null || tiles.Surfaces == null || tiles.Levels.Count != cells * cells || tiles.Surfaces.Count != cells * cells)
            {
                result.Add($"Tiles must be {cells} a side");
                return;
            }

            foreach (var level in tiles.Levels)
            {
                if (level < MinLevel || level > MaxLevel)
                {
                    result.Add($"Tile levels must be between {MinLevel} and {MaxLevel}");
                    break;
                }
            }

            foreach (var surface in tiles.Surfaces)
            {
                if (!Enum.IsDefined(typeof(TileSurface), surface))
                {
                    result.Add($"Unknown tile surface {surface}");
                    break;
                }
            }

            if (tiles.Hills != null && (tiles.Hills.Count != cells * cells || tiles.Hills.Exists(h => h != 0 && h != 1)))
                result.Add("Hillside marks must be 0 or 1, one a tile");

            if ((town.Roads?.Segments?.Count ?? 0) > 0 || (town.Roads?.Nodes?.Count ?? 0) > 0) result.Add("Roads must be tiles now");
            if (town.Terrain != null) result.Add("The ground must be tiles now");
        }
    }
}

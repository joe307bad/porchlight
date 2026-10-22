using System.Collections.Generic;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// The town's ground drawn from its tiles (<see cref="TownTiles"/>), the same in the builder and in
    /// play. Every tile is built from a small kit of cozy pieces chosen by its surface and its
    /// neighbours, nothing guessed:
    /// <list type="bullet">
    /// <item>Grass: soft lawn in gently varied greens.</item>
    /// <item>Garden: a raised soil bed with timber edging (the foliage system plants the flowers).</item>
    /// <item>Plaza: flagstones a metre square on a mortar bed.</item>
    /// <item>Street: warm tarmac with a gutter and a raised stone kerb wherever it meets something
    /// other than street or plaza. A street climbing a level ramps the whole tile.</item>
    /// <item>Path: cobbles on the grass in the shape the tile joins its neighbours (a square in the
    /// middle, an arm to each joined side). Climbing a level, a flight of stone steps with cheek walls.</item>
    /// <item>Walls: wherever a tile stands above its neighbour, a dry-stone retaining wall in coursed,
    /// varied stones under a capping stone, with a white picket fence along tall drops by paving.</item>
    /// </list>
    /// Tiles are drawn in chunks of <see cref="Chunk"/> a side so an edit rebuilds only its corner.
    /// </summary>
    public static class TileMeshes
    {
        /// <summary>Tiles a side per mesh.</summary>
        public const int Chunk = 16;

        private static readonly Color[] Lawn = { Hex("#A7CF7D"), Hex("#A1CA77"), Hex("#ACD383"), Hex("#9EC674") };
        private static readonly Color Soil = Hex("#7A5A3D");
        private static readonly Color Hedge = Hex("#6A9A52");
        private static readonly Color HedgeTop = Hex("#7DAF60");
        private static readonly Color[] Petals = { Hex("#F4B6C2"), Hex("#FFE08A"), Hex("#C9A7E8"), Hex("#F7F1E3"), Hex("#F29E7A") };
        private static readonly Color Flag = Hex("#DCCDB0");
        private static readonly Color FlagJoint = Hex("#C9BA9C");
        private static readonly Color Tarmac = Hex("#8C867E");
        private static readonly Color Gutter = Hex("#7D776F");
        private static readonly Color KerbStone = Hex("#D8D1C4");
        private static readonly Color Cobble = Hex("#CDBB9E");
        private static readonly Color CobbleJoint = Hex("#A69579");
        private static readonly Color Step = Hex("#C9BFAC");
        private static readonly Color[] WallStones = { Hex("#C4BAA5"), Hex("#B5AA94"), Hex("#CCC3AF"), Hex("#ABA08A"), Hex("#BFB49D") };
        private static readonly Color Mortar = Hex("#8E8471");
        private static readonly Color Coping = Hex("#DCD4C4");
        private static readonly Color Picket = Hex("#F1EBDD");

        private const float Course = 0.3f;
        private const float KerbWidth = 0.3f;
        private const float KerbHeight = 0.12f;
        private const float GutterWidth = 0.25f;
        private const float CopingDepth = 0.32f;
        private const float CopingHeight = 0.1f;
        private const float FenceFrom = 1.5f;

        private static Color Hex(string hex) => LowPolyMeshes.Hex(hex);

        /// <summary>How many chunks a side the town's tiles make.</summary>
        public static int Chunks(TownDto town) => (TownTiles.Cells(town) + Chunk - 1) / Chunk;

        /// <summary>The chunk a tile is in.</summary>
        public static (int CI, int CJ) ChunkOf(int i, int j) => (i / Chunk, j / Chunk);

        public static void BuildChunk(TownDto town, int ci, int cj, Mesh target)
        {
            var mb = new MeshBuilder();
            var n = TownTiles.Cells(town);
            for (var j = cj * Chunk; j < Mathf.Min(n, (cj + 1) * Chunk); j++)
            {
                for (var i = ci * Chunk; i < Mathf.Min(n, (ci + 1) * Chunk); i++) Tile(mb, town, i, j);
            }

            mb.ToMesh(target, $"tiles {ci},{cj}");
        }

        private static void Tile(MeshBuilder mb, TownDto town, int i, int j)
        {
            var piece = new Piece(town, i, j);
            switch (TownTiles.Surface(town, i, j))
            {
                case TileSurface.Street: piece.Street(mb); break;
                case TileSurface.Path: piece.Path(mb); break;
                case TileSurface.Plaza: piece.Plaza(mb); break;
                case TileSurface.Garden: piece.Garden(mb); break;
                default: piece.Lawn(mb); break;
            }

            piece.Walls(mb);
        }

        /// <summary>One tile being drawn: where it is, and its ground's height anywhere on it.</summary>
        private readonly struct Piece
        {
            private readonly TownDto _town;
            private readonly int _i;
            private readonly int _j;
            private readonly float _x0;
            private readonly float _z0;
            private readonly float _size;
            private readonly uint _seed;

            public Piece(TownDto town, int i, int j)
            {
                _town = town;
                _i = i;
                _j = j;
                _size = TownTiles.Spacing(town);
                var c = TownTiles.Center(town, i, j);
                _x0 = c.X - _size * 0.5f;
                _z0 = c.Z - _size * 0.5f;
                _seed = (uint)(i * 73856093 ^ j * 19349663);
            }

            /// <summary>Height of this tile's ground at (x, z), even on its very edge.</summary>
            private float H(float x, float z) =>
                TownTiles.HeightAt(_town, new Vec2(Mathf.Clamp(x, _x0 + 0.002f, _x0 + _size - 0.002f), Mathf.Clamp(z, _z0 + 0.002f, _z0 + _size - 0.002f)));

            private Vector3 P(float x, float z, float lift) => new Vector3(x, H(x, z) + lift, z);

            /// <summary>A rectangle of this tile's ground, lifted a little, in one colour.</summary>
            private void Patch(MeshBuilder mb, float x0, float z0, float x1, float z1, float lift, Color color)
            {
                mb.Quad(P(x0, z0, lift), P(x0, z1, lift), P(x1, z1, lift), P(x1, z0, lift), color, Vector3.up);
            }

            private float Rand(int salt) => ((_seed ^ (uint)salt * 2654435761u) * 2246822519u >> 8) / 16777216f;

            private Color Vary(Color color, int salt, float amount)
            {
                var shade = 1f + (Rand(salt) - 0.5f) * 2f * amount;
                return new Color(color.r * shade, color.g * shade, color.b * shade, 1f);
            }

            /// <summary>Lawn, in soft patches of lighter and darker green across the town, each tile a touch its own.</summary>
            public void Lawn(MeshBuilder mb)
            {
                var patch = Mathf.PerlinNoise(_x0 * 0.035f + 11.3f, _z0 * 0.035f + 4.7f);
                var green = patch < 0.5f
                    ? Color.Lerp(TileMeshes.Lawn[1], TileMeshes.Lawn[0], patch * 2f)
                    : Color.Lerp(TileMeshes.Lawn[0], TileMeshes.Lawn[2], (patch - 0.5f) * 2f);
                Patch(mb, _x0, _z0, _x0 + _size, _z0 + _size, 0f, Vary(green, 1, 0.015f));
            }

            /// <summary>
            /// A flower bed: dug soil dotted with low flowers, and a clipped box hedge along each side that
            /// doesn't run on into another bed, so neighbouring garden tiles make one big bed.
            /// </summary>
            public void Garden(MeshBuilder mb)
            {
                Patch(mb, _x0, _z0, _x0 + _size, _z0 + _size, 0.012f, Vary(Soil, 2, 0.05f));

                // Tufts of flowers in loose rows: a low mound of leaves, and a head of petals on top.
                const float spacing = 0.6f;
                var tufts = Mathf.Max(1, Mathf.RoundToInt(_size / spacing));
                for (var a = 0; a < tufts; a++)
                {
                    for (var b = 0; b < tufts; b++)
                    {
                        var salt = 500 + a * 17 + b * 31;
                        if (Rand(salt) < 0.12f) continue;
                        var x = _x0 + (a + 0.5f + (Rand(salt + 1) - 0.5f) * 0.5f) * (_size / tufts);
                        var z = _z0 + (b + 0.5f + (Rand(salt + 2) - 0.5f) * 0.5f) * (_size / tufts);
                        var petal = Petals[(int)(Rand(salt + 3) * Petals.Length) % Petals.Length];
                        var leaf = Vary(Hedge, salt + 4, 0.08f);
                        var y = H(x, z);
                        var size = 0.8f + Rand(salt + 5) * 0.4f;
                        mb.Prism(new Vector3(x, y, z), 0.24f * size, 0.14f * size, 0.18f * size, leaf, 5);
                        mb.Prism(new Vector3(x, y + 0.18f * size, z), 0.12f * size, 0.08f * size, 0.08f, petal, 5);
                    }
                }

                const float thick = 0.42f, tall = 0.55f;
                for (var s = 0; s < 4; s++)
                {
                    var (di, dj) = TownTiles.Neighbours[s];
                    if (TownTiles.Surface(_town, _i + di, _j + dj) == TileSurface.Garden && TownTiles.Level(_town, _i + di, _j + dj) == TownTiles.Level(_town, _i, _j) && TownTiles.Inside(_town, _i + di, _j + dj)) continue;
                    var (a, b) = EdgeLine(s);
                    var mid = (a + b) * 0.5f - Outward(s) * (thick * 0.5f);
                    var alongX = Mathf.Abs(b.x - a.x) > 0.01f;
                    var y = H(mid.x, mid.z);
                    mb.Box(new Vector3(mid.x, y, mid.z), alongX ? _size : thick, tall, alongX ? thick : _size, Vary(Hedge, 600 + s, 0.05f), Vary(HedgeTop, 610 + s, 0.05f));
                }
            }

            /// <summary>Flagstones in running bond, four courses a tile, each stone its own shade, on a mortar bed.</summary>
            public void Plaza(MeshBuilder mb)
            {
                Patch(mb, _x0, _z0, _x0 + _size, _z0 + _size, 0.01f, FlagJoint);
                const float gap = 0.03f;
                var courses = Mathf.Max(1, Mathf.RoundToInt(_size));
                var depth = _size / courses;
                var length = _size / 3f;
                for (var c = 0; c < courses; c++)
                {
                    var z0 = _z0 + c * depth;
                    var shift = (Mathf.FloorToInt(z0 / depth + 0.5f) & 1) * length * 0.5f;
                    for (var x = _x0 - shift; x < _x0 + _size - 0.01f; x += length)
                    {
                        var xa = Mathf.Max(_x0, x) + gap;
                        var xb = Mathf.Min(_x0 + _size, x + length) - gap;
                        if (xb - xa < 0.1f) continue;
                        Patch(mb, xa, z0 + gap, xb, z0 + depth - gap, 0.03f, Vary(Flag, 10 + c * 13 + Mathf.RoundToInt(x * 3f), 0.035f));
                    }
                }
            }

            public void Street(MeshBuilder mb)
            {
                Patch(mb, _x0, _z0, _x0 + _size, _z0 + _size, 0.01f, Vary(Tarmac, 4, 0.025f));
                var climbs = TownTiles.TryClimb(_town, _i, _j, out var climb);
                var level = TownTiles.Level(_town, _i, _j);
                for (var s = 0; s < 4; s++)
                {
                    var (di, dj) = TownTiles.Neighbours[s];
                    var surface = TownTiles.Surface(_town, _i + di, _j + dj);
                    var nl = TownTiles.Level(_town, _i + di, _j + dj);
                    var joined = (surface == TileSurface.Street || surface == TileSurface.Plaza) && (nl == level || (climbs && (int)climb == s) || ClimbsTo(_i + di, _j + dj, (s + 2) % 4));
                    if (joined) continue;

                    // A path at the same level meets the street at a dropped kerb, flush, so you can
                    // step across; anything else gets the full kerb.
                    if (surface == TileSurface.Path && nl == level && TownTiles.Inside(_town, _i + di, _j + dj)) DroppedKerb(mb, s);
                    else Kerb(mb, s);
                }
            }

            /// <summary>Whether the tile at (i, j) climbs up across its side <paramref name="side"/>.</summary>
            private bool ClimbsTo(int i, int j, int side) => TownTiles.Inside(_town, i, j) && TownTiles.TryClimb(_town, i, j, out var climb) && (int)climb == side;

            /// <summary>A gutter and a raised kerb along one side of a street tile.</summary>
            private void Kerb(MeshBuilder mb, int side)
            {
                Edge(side, KerbWidth, out var k0, out var k1);
                Edge(side, KerbWidth + GutterWidth, out var g0, out var g1);
                Band(mb, g0, g1, KerbWidth, KerbWidth + GutterWidth, side, 0.015f, Gutter);
                Band(mb, k0, k1, 0f, KerbWidth, side, 0.01f + KerbHeight, KerbStone);

                // The kerb's face, down to the gutter.
                var (a, b) = Inner(side, KerbWidth);
                var ya = H(a.x, a.z);
                var yb = H(b.x, b.z);
                mb.Quad(new Vector3(a.x, ya + 0.015f, a.z), new Vector3(a.x, ya + 0.01f + KerbHeight, a.z), new Vector3(b.x, yb + 0.01f + KerbHeight, b.z), new Vector3(b.x, yb + 0.015f, b.z), Color.Lerp(KerbStone, Color.black, 0.12f), -Outward(side));
            }

            /// <summary>Flush kerb stones where a path meets the street, either side of the path's mouth raised as usual.</summary>
            private void DroppedKerb(MeshBuilder mb, int side)
            {
                var (a, b) = EdgeLine(side);
                var length = Vector3.Distance(a, b);
                var dir = (b - a) / length;
                var mouth0 = length * 0.5f - TownTiles.PathWidth * 0.5f;
                var mouth1 = length * 0.5f + TownTiles.PathWidth * 0.5f;
                var shift = -Outward(side);
                Band(mb, a, b, KerbWidth, KerbWidth + GutterWidth, side, 0.015f, Gutter);

                // The raised kerb either side of the mouth, the flush stones across it.
                Strip(mb, a, dir, shift, 0f, mouth0, 0.01f + KerbHeight, KerbStone, side);
                Strip(mb, a, dir, shift, mouth0, mouth1, 0.025f, Color.Lerp(KerbStone, Cobble, 0.35f), side);
                Strip(mb, a, dir, shift, mouth1, length, 0.01f + KerbHeight, KerbStone, side);
            }

            /// <summary>A stretch of kerb along a side, from <paramref name="from"/> to <paramref name="to"/> metres along it.</summary>
            private void Strip(MeshBuilder mb, Vector3 a, Vector3 dir, Vector3 shift, float from, float to, float lift, Color color, int side)
            {
                if (to - from < 0.01f) return;
                var p0 = a + dir * from;
                var p1 = a + dir * to;
                var q0 = p0 + shift * KerbWidth;
                var q1 = p1 + shift * KerbWidth;
                mb.Quad(P(p0.x, p0.z, lift), P(p1.x, p1.z, lift), P(q1.x, q1.z, lift), P(q0.x, q0.z, lift), color, Vector3.up);
                if (lift < 0.05f) return;
                mb.Quad(new Vector3(q0.x, H(q0.x, q0.z) + 0.015f, q0.z), new Vector3(q0.x, H(q0.x, q0.z) + lift, q0.z), new Vector3(q1.x, H(q1.x, q1.z) + lift, q1.z), new Vector3(q1.x, H(q1.x, q1.z) + 0.015f, q1.z), Color.Lerp(color, Color.black, 0.12f), -Outward(side));
            }

            public void Path(MeshBuilder mb)
            {
                Lawn(mb);
                var half = TownTiles.PathWidth * 0.5f;
                var cx = _x0 + _size * 0.5f;
                var cz = _z0 + _size * 0.5f;
                var level = TownTiles.Level(_town, _i, _j);
                var climbs = TownTiles.TryClimb(_town, _i, _j, out var climb);

                // The middle, then an arm to each side it joins, all stopping short of a flight of steps.
                var stop = _size * 0.5f - TownTiles.StairRun;
                Cobbles(mb,
                    cx - (climbs && climb == TileSide.West ? Mathf.Min(half, stop) : half),
                    cz - (climbs && climb == TileSide.South ? Mathf.Min(half, stop) : half),
                    cx + (climbs && climb == TileSide.East ? Mathf.Min(half, stop) : half),
                    cz + (climbs && climb == TileSide.North ? Mathf.Min(half, stop) : half));
                for (var s = 0; s < 4; s++)
                {
                    var (di, dj) = TownTiles.Neighbours[s];
                    var surface = TownTiles.Surface(_town, _i + di, _j + dj);
                    var nl = TownTiles.Level(_town, _i + di, _j + dj);
                    var stairs = climbs && (int)climb == s;
                    var joined = TownTiles.IsPaved(surface) && TownTiles.Inside(_town, _i + di, _j + dj) && (nl == level || stairs || ClimbsTo(_i + di, _j + dj, (s + 2) % 4));
                    if (!joined) continue;

                    var end = stairs ? _size * 0.5f - TownTiles.StairRun : _size * 0.5f;
                    switch (s)
                    {
                        case 0: Cobbles(mb, cx - half, cz + half, cx + half, cz + end); break;
                        case 1: Cobbles(mb, cx + half, cz - half, cx + end, cz + half); break;
                        case 2: Cobbles(mb, cx - half, cz - end, cx + half, cz - half); break;
                        default: Cobbles(mb, cx - end, cz - half, cx - half, cz + half); break;
                    }
                }

                if (climbs) Flight(mb, (int)climb, level);
            }

            /// <summary>Cobbles over a rectangle: a joint bed, then stones in staggered rows.</summary>
            private void Cobbles(MeshBuilder mb, float x0, float z0, float x1, float z1)
            {
                if (x1 - x0 < 0.05f || z1 - z0 < 0.05f) return;
                Patch(mb, x0, z0, x1, z1, 0.02f, CobbleJoint);
                const float stone = 0.42f, gap = 0.04f;
                var rows = Mathf.Max(1, Mathf.RoundToInt((z1 - z0) / stone));
                var rowDepth = (z1 - z0) / rows;
                for (var r = 0; r < rows; r++)
                {
                    var za = z0 + r * rowDepth + gap * 0.5f;
                    var zb = za + rowDepth - gap;
                    var shift = (Mathf.FloorToInt((z0 + r * rowDepth) / stone) & 1) * stone * 0.5f;
                    for (var x = Mathf.Floor((x0 - shift) / stone) * stone + shift; x < x1; x += stone)
                    {
                        var xa = Mathf.Max(x0, x) + gap * 0.5f;
                        var xb = Mathf.Min(x1, x + stone) - gap * 0.5f;
                        if (xb - xa < 0.08f) continue;
                        Patch(mb, xa, za, xb, zb, 0.045f, Vary(Cobble, 100 + r * 31 + Mathf.RoundToInt(x * 7f), 0.08f));
                    }
                }
            }

            /// <summary>Stone steps up to the side the path climbs to, with a cheek wall either side.</summary>
            private void Flight(MeshBuilder mb, int side, int level)
            {
                var half = TownTiles.PathWidth * 0.5f;
                var run = TownTiles.StairRun;
                var steps = TownTiles.StairSteps;
                var tread = run / steps;
                var rise = 1f / steps;
                var outward = Outward(side);
                var across = new Vector3(-outward.z, 0f, outward.x);
                var center = new Vector3(_x0 + _size * 0.5f, 0f, _z0 + _size * 0.5f);
                var start = center + outward * (_size * 0.5f - run);

                // Rebuild the path under the flight as cobbles, so its sides sit on something.
                for (var k = 0; k < steps; k++)
                {
                    var a = start + outward * (k * tread);
                    var b = a + outward * tread;
                    var top = level + (k + 1) * rise + 0.02f;
                    var bottom = level - 0.02f;
                    Block(mb, a, b, across, half, bottom, top, Vary(Step, 200 + k, 0.05f), outward);
                }

                // Cheek walls, stepping up with the treads and standing a little proud of them.
                const float cheek = 0.28f, proud = 0.32f;
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    for (var k = 0; k < steps; k++)
                    {
                        var a = start + outward * (k * tread) + across * (sign * (half + cheek * 0.5f));
                        var b = a + outward * tread;
                        var top = level + (k + 1) * rise + proud;
                        Block(mb, a, b, across, cheek * 0.5f, level - 0.02f, top, Vary(WallStones[k % WallStones.Length], 300 + k + sign * 11, 0.04f), outward);
                    }
                }
            }

            /// <summary>A box from a to b (along the ground), <paramref name="halfWidth"/> either side, between two heights.</summary>
            private static void Block(MeshBuilder mb, Vector3 a, Vector3 b, Vector3 across, float halfWidth, float bottom, float top, Color color, Vector3 forward)
            {
                var side = across * halfWidth;
                Vector3 At(Vector3 p, float y) => new Vector3(p.x, y, p.z);
                var face = Color.Lerp(color, Color.black, 0.15f);
                mb.Quad(At(a - side, top), At(b - side, top), At(b + side, top), At(a + side, top), color, Vector3.up);
                mb.Quad(At(a - side, bottom), At(a - side, top), At(a + side, top), At(a + side, bottom), face, -forward);
                mb.Quad(At(b - side, bottom), At(b - side, top), At(b + side, top), At(b + side, bottom), face, forward);
                mb.Quad(At(a + side, bottom), At(a + side, top), At(b + side, top), At(b + side, bottom), face, across);
                mb.Quad(At(a - side, bottom), At(a - side, top), At(b - side, top), At(b - side, bottom), face, -across);
            }

            /// <summary>
            /// Retaining walls: along each side where this tile's ground stands above the next tile's
            /// (or, at the edge of town, the countryside's), and along the edge of town where the
            /// countryside stands above this tile.
            /// </summary>
            public void Walls(MeshBuilder mb)
            {
                // Segments of 0.4 m line up with a path's edges (its middle 2.4 m of a 4 m side), so
                // the opening a flight of steps leaves in the wall above it is cut cleanly.
                var segments = Mathf.Max(1, Mathf.RoundToInt(_size / 0.4f));
                var paved = TownTiles.IsPaved(TownTiles.Surface(_town, _i, _j));
                for (var s = 0; s < 4; s++)
                {
                    var (di, dj) = TownTiles.Neighbours[s];
                    var outside = !TownTiles.Inside(_town, _i + di, _j + dj);
                    var other = new Piece(_town, _i + di, _j + dj);
                    var (a, b) = EdgeLine(s);
                    var tall = false;
                    for (var k = 0; k < segments; k++)
                    {
                        float t0 = (float)k / segments, t1 = (float)(k + 1) / segments;
                        var p0 = Vector3.Lerp(a, b, t0);
                        var p1 = Vector3.Lerp(a, b, t1);
                        var (mine0, mine1) = Ends(this, p0, p1);
                        var (theirs0, theirs1) = outside ? (0f, 0f) : Ends(other, p0, p1);

                        if (mine0 > theirs0 + 0.02f || mine1 > theirs1 + 0.02f)
                        {
                            Masonry(mb, p0, p1, Mathf.Min(theirs0, mine0), Mathf.Min(theirs1, mine1), mine0, mine1, Outward(s), Vector3.Distance(a, p0));
                            CopingStone(mb, p0, p1, mine0, mine1, s);
                            if (Mathf.Max(mine0 - theirs0, mine1 - theirs1) >= FenceFrom) tall = true;
                        }
                        else if (outside && (theirs0 > mine0 + 0.02f || theirs1 > mine1 + 0.02f))
                        {
                            // The countryside holding up the edge of town over a dug-down tile.
                            Masonry(mb, p1, p0, mine1, mine0, theirs1, theirs0, -Outward(s), Vector3.Distance(b, p1));
                        }
                    }

                    if (tall && paved) Fence(mb, a, b, s);
                }
            }

            /// <summary>
            /// A tile's ground at the two ends of a stretch of its edge, read just inside the ends and
            /// carried out to them: exact on a ramp, and on steps the stretch is either all opening or
            /// all wall, never a sloping mix of the two.
            /// </summary>
            private static (float, float) Ends(Piece piece, Vector3 p0, Vector3 p1)
            {
                const float nudge = 0.08f;
                var length = Vector3.Distance(p0, p1);
                var dir = (p1 - p0) / length;
                var a = p0 + dir * nudge;
                var b = p1 - dir * nudge;
                var ha = piece.H(a.x, a.z);
                var hb = piece.H(b.x, b.z);
                var slope = (hb - ha) / Mathf.Max(1e-3f, length - nudge * 2f);
                return (ha - slope * nudge, hb + slope * nudge);
            }

            /// <summary>The two ends of a side of the tile, running anticlockwise round it seen from above.</summary>
            private (Vector3 A, Vector3 B) EdgeLine(int side)
            {
                float x0 = _x0, z0 = _z0, x1 = _x0 + _size, z1 = _z0 + _size;
                switch (side)
                {
                    case 0: return (new Vector3(x1, 0f, z1), new Vector3(x0, 0f, z1));
                    case 1: return (new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1));
                    case 2: return (new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0));
                    default: return (new Vector3(x0, 0f, z1), new Vector3(x0, 0f, z0));
                }
            }

            private static Vector3 Outward(int side)
            {
                var (di, dj) = TownTiles.Neighbours[side];
                return new Vector3(di, 0f, dj);
            }

            /// <summary>
            /// A stretch of dry-stone wall from p0 to p1, its face looking along <paramref name="outward"/>:
            /// a mortar backing between the bottom and top heights at each end, and stones laid on it in
            /// courses, each stone its own length and shade. Courses follow world heights and stones run on
            /// from <paramref name="along"/> metres, so neighbouring stretches line up.
            /// </summary>
            private void Masonry(MeshBuilder mb, Vector3 p0, Vector3 p1, float bottom0, float bottom1, float top0, float top1, Vector3 outward, float along)
            {
                var length = Vector3.Distance(p0, p1);
                if (length < 1e-3f) return;
                var dir = (p1 - p0) / length;
                mb.Quad(new Vector3(p0.x, bottom0, p0.z), new Vector3(p0.x, top0, p0.z), new Vector3(p1.x, top1, p1.z), new Vector3(p1.x, bottom1, p1.z), Mortar, outward);

                var low = Mathf.Min(bottom0, bottom1);
                var high = Mathf.Max(top0, top1);
                var proud = outward * 0.025f;
                for (var y = Mathf.Floor(low / Course) * Course; y < high; y += Course)
                {
                    var row = Mathf.RoundToInt(y / Course);

                    // Stones of a few lengths, staggered course by course.
                    var u = -((along + row * 0.37f) % 0.7f);
                    var n = 0;
                    while (u < length)
                    {
                        var size = 0.45f + Hash(row, Mathf.FloorToInt((along + u) * 3f)) * 0.5f;
                        float ua = Mathf.Max(0f, u) + 0.03f, ub = Mathf.Min(length, u + size) - 0.03f;
                        u += size;
                        n++;
                        if (ub - ua < 0.12f) continue;

                        float ta = ua / length, tb = ub / length;
                        var floor = Mathf.Max(Mathf.Lerp(bottom0, bottom1, ta), Mathf.Lerp(bottom0, bottom1, tb));
                        var ceiling = Mathf.Min(Mathf.Lerp(top0, top1, ta), Mathf.Lerp(top0, top1, tb));
                        var sa = Mathf.Max(y + 0.025f, floor + 0.01f);
                        var sb = Mathf.Min(y + Course - 0.025f, ceiling - 0.02f);
                        if (sb - sa < 0.07f) continue;

                        var a = p0 + dir * ua + proud;
                        var b = p0 + dir * ub + proud;
                        var color = WallStones[(int)(Hash(row * 13, n) * WallStones.Length) % WallStones.Length];
                        color = Color.Lerp(color, Color.black, Hash(row, n * 7) * 0.08f);
                        mb.Quad(new Vector3(a.x, sa, a.z), new Vector3(a.x, sb, a.z), new Vector3(b.x, sb, b.z), new Vector3(b.x, sa, b.z), color, outward);
                    }
                }
            }

            /// <summary>Capping stones along the top of a wall, overhanging its face a little.</summary>
            private void CopingStone(MeshBuilder mb, Vector3 p0, Vector3 p1, float top0, float top1, int side)
            {
                var outward = Outward(side);
                var back = -outward * CopingDepth;
                var front = outward * 0.06f;
                Vector3 At(Vector3 p, float y) => new Vector3(p.x, y, p.z);
                var t0 = top0 + CopingHeight;
                var t1 = top1 + CopingHeight;
                mb.Quad(At(p0 + back, t0), At(p0 + front, t0), At(p1 + front, t1), At(p1 + back, t1), Coping, Vector3.up);
                mb.Quad(At(p0 + front, top0 - 0.02f), At(p0 + front, t0), At(p1 + front, t1), At(p1 + front, top1 - 0.02f), Color.Lerp(Coping, Color.black, 0.1f), outward);
            }

            /// <summary>A white picket fence along a tall drop beside paving, set back on the capping.</summary>
            private void Fence(MeshBuilder mb, Vector3 a, Vector3 b, int side)
            {
                var outward = Outward(side);
                var inset = -outward * 0.16f;
                var length = Vector3.Distance(a, b);
                var dir = (b - a) / length;
                var posts = Mathf.Max(2, Mathf.RoundToInt(length / 0.5f) + 1);
                for (var k = 0; k < posts; k++)
                {
                    var p = a + dir * (length * k / (posts - 1)) + inset;
                    var y = H(p.x, p.z) + CopingHeight;
                    mb.Box(new Vector3(p.x, y, p.z), 0.08f, 0.85f, 0.08f, Picket, Picket);
                }

                foreach (var height in new[] { 0.35f, 0.7f })
                {
                    var p0 = a + inset;
                    var p1 = b + inset;
                    var y0 = H(p0.x, p0.z) + CopingHeight + height;
                    var y1 = H(p1.x, p1.z) + CopingHeight + height;
                    var thick = new Vector3(0f, 0.06f, 0f);
                    mb.Quad(new Vector3(p0.x, y0, p0.z), new Vector3(p0.x, y0, p0.z) + thick, new Vector3(p1.x, y1, p1.z) + thick, new Vector3(p1.x, y1, p1.z), Picket, outward);
                    mb.Quad(new Vector3(p0.x, y0, p0.z), new Vector3(p0.x, y0, p0.z) + thick, new Vector3(p1.x, y1, p1.z) + thick, new Vector3(p1.x, y1, p1.z), Picket, -outward);
                }
            }

            private static float Hash(int a, int b) => ((uint)(a * 374761393 + b * 668265263) * 2246822519u >> 8) / 16777216f;

            /// <summary>A side's line set in from the edge by <paramref name="inset"/>, as two ends.</summary>
            private (Vector3 A, Vector3 B) Inner(int side, float inset)
            {
                var (a, b) = EdgeLine(side);
                var shift = -Outward(side) * inset;
                return (a + shift, b + shift);
            }

            private void Edge(int side, float inset, out Vector3 a, out Vector3 b) => (a, b) = Inner(side, inset);

            /// <summary>A strip along a side between two insets from it, lifted a little.</summary>
            private void Band(MeshBuilder mb, Vector3 innerA, Vector3 innerB, float from, float to, int side, float lift, Color color)
            {
                var (a, b) = EdgeLine(side);
                var shift = -Outward(side);
                var a0 = a + shift * from;
                var b0 = b + shift * from;
                var a1 = a + shift * to;
                var b1 = b + shift * to;
                mb.Quad(P(a0.x, a0.z, lift), P(b0.x, b0.z, lift), P(b1.x, b1.z, lift), P(a1.x, a1.z, lift), color, Vector3.up);
            }
        }

        /// <summary>
        /// The countryside round the town: a meadow at level 0 along the town's edge, rising into soft
        /// rolling hills further out, faceted like everything else.
        /// </summary>
        public static Mesh Countryside(float townSize)
        {
            var mb = new MeshBuilder();
            var half = townSize * 0.5f;
            var reach = half * 3f;
            const float cell = 12f;
            var count = Mathf.CeilToInt(reach * 2f / cell);
            float Height(float x, float z)
            {
                // Flat for a little way out from the town, then hills that grow with distance.
                var outside = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) - half;
                var rise = Mathf.Clamp01((outside - 24f) / 140f);
                var hills = Mathf.Sin(x * 0.021f + 1.3f) * Mathf.Cos(z * 0.017f - 0.4f) + 0.6f * Mathf.Sin((x + z) * 0.033f) + 0.35f * Mathf.Cos((x - z) * 0.051f + 2f);
                return Mathf.Max(0f, rise * (8f + hills * 9f));
            }

            Color Meadow(float x, float z, float y)
            {
                // The town's lawn greens, deepening up the hills.
                var c = Color.Lerp(TileMeshes.Lawn[1], TileMeshes.Lawn[2], Mathf.PerlinNoise(x * 0.013f + 7f, z * 0.013f + 3f));
                return Color.Lerp(c, Hex("#86B266"), Mathf.Clamp01(y / 18f));
            }

            for (var a = 0; a < count; a++)
            {
                for (var b = 0; b < count; b++)
                {
                    float x0 = -reach + a * cell, z0 = -reach + b * cell, x1 = x0 + cell, z1 = z0 + cell;

                    // The town itself is drawn by its tiles.
                    if (x1 > -half && x0 < half && z1 > -half && z0 < half)
                    {
                        // Only the ring of meadow between the grid and the town's edge.
                        Clip(mb, x0, z0, x1, z1, half, Meadow);
                        continue;
                    }

                    var p00 = new Vector3(x0, Height(x0, z0) - 0.05f, z0);
                    var p10 = new Vector3(x1, Height(x1, z0) - 0.05f, z0);
                    var p11 = new Vector3(x1, Height(x1, z1) - 0.05f, z1);
                    var p01 = new Vector3(x0, Height(x0, z1) - 0.05f, z1);
                    mb.Triangle(p00, p10, p11, Meadow(x0, z0, (p00.y + p11.y) * 0.5f), Vector3.up);
                    mb.Triangle(p00, p11, p01, Meadow(x1, z1, (p00.y + p11.y) * 0.5f), Vector3.up);
                }
            }

            return mb.ToMesh(name: "countryside");
        }

        /// <summary>The parts of a flat cell outside the town square, at level 0.</summary>
        private static void Clip(MeshBuilder mb, float x0, float z0, float x1, float z1, float half, System.Func<float, float, float, Color> color)
        {
            var parts = new List<(float, float, float, float)>
            {
                (x0, z0, Mathf.Min(x1, -half), z1), (Mathf.Max(x0, half), z0, x1, z1),
                (Mathf.Max(x0, -half), z0, Mathf.Min(x1, half), Mathf.Min(z1, -half)), (Mathf.Max(x0, -half), Mathf.Max(z0, half), Mathf.Min(x1, half), z1),
            };
            foreach (var (a0, b0, a1, b1) in parts)
            {
                if (a1 - a0 < 0.01f || b1 - b0 < 0.01f) continue;
                mb.Quad(new Vector3(a0, -0.05f, b0), new Vector3(a0, -0.05f, b1), new Vector3(a1, -0.05f, b1), new Vector3(a1, -0.05f, b0), color(a0, b0, 0f), Vector3.up);
            }
        }

        /// <summary>
        /// The tops of the tiles as one surface (ramps and steps as slopes), lifted a little, for the
        /// builder's grid to lie on.
        /// </summary>
        public static Mesh GridSurface(TownDto town, float lift)
        {
            var mb = new MeshBuilder();
            var n = TownTiles.Cells(town);
            var size = TownTiles.Spacing(town);
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    var c = TownTiles.Center(town, i, j);
                    float x0 = c.X - size * 0.5f + 0.002f, z0 = c.Z - size * 0.5f + 0.002f, x1 = c.X + size * 0.5f - 0.002f, z1 = c.Z + size * 0.5f - 0.002f;
                    Vector3 P(float x, float z) => new Vector3(x, TownTiles.HeightAt(town, new Vec2(x, z)) + lift, z);
                    mb.Quad(P(x0, z0), P(x0, z1), P(x1, z1), P(x1, z0), Color.white, Vector3.up);
                }
            }

            return mb.ToMesh(name: "grid surface");
        }
    }
}

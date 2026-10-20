using System;
using Porchlight.Shared.Content;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// Lays out and furnishes a new cottage, so it isn't empty when you walk in. The plan is always
    /// the same shape, with the details picked by a random number generator seeded from the
    /// building's id, so every client lays out the same building the same way.
    ///
    /// A hall runs from the front door to the back, with the stairs in it climbing towards the back.
    /// Downstairs, the living room takes one side, and the kitchen and dining room share the other,
    /// one behind the other. Upstairs, the main bedroom takes the side above the stairs, and the
    /// bathroom and a second bedroom share the other. Then it all may be mirrored.
    ///
    /// Every part goes through <see cref="InteriorRules"/> as it's added, and anything that doesn't
    /// fit is left out (or tried somewhere else), so the result is always a valid inside.
    /// </summary>
    public static class CottageGenerator
    {
        /// <summary>The hall's walls run front to back this far either side of the middle.</summary>
        public const float Hall = 1.25f;

        private const float Face = CottageInterior.InnerWall * 0.5f;

        private enum Side
        {
            Back,
            Front,
            Left,
            Right,
        }

        public static InteriorDto Generate(BuildingType type, string seed)
        {
            var layout = new Layout(type, new Rng(seed));
            layout.Lay();
            return layout.Interior;
        }

        private sealed class Layout
        {
            private readonly BuildingType _type;
            private readonly Rng _rng;
            private readonly Aabb _inside;
            private int _next;

            public Layout(BuildingType type, Rng rng)
            {
                _type = type;
                _rng = rng;
                _inside = CottageInterior.Inside(type);
                Interior = new InteriorDto();
                for (var i = 0; i < CottageInterior.FloorCount; i++) Interior.Floors.Add(new InteriorFloorDto());
            }

            public InteriorDto Interior { get; }

            public void Lay()
            {
                var minX = _inside.MinX;
                var maxX = _inside.MaxX;
                var minZ = _inside.MinZ;
                var maxZ = _inside.MaxZ;

                // The stairs stand against the right-hand hall wall, climbing towards the back, with
                // room at the back to step off upstairs.
                var stairs = new StairsDto
                {
                    X = Q(Hall - Face - 0.05f - CottageInterior.StairsWidth * 0.5f),
                    Z = Q(minZ + CottageInterior.Landing + CottageInterior.StairsLength * 0.5f + 0.05f),
                    RotationDeg = 180f,
                };
                if (InteriorRules.CanMoveStairs(_type, Interior, stairs.X, stairs.Z, stairs.RotationDeg).Ok) Interior.Stairs = stairs;
                else Interior.Stairs = CottageInterior.Default().Stairs;

                // A door into the hall's right-hand wall has to clear the foot of the stairs.
                var clearOfStairs = Interior.Stairs.Z + CottageInterior.StairsLength * 0.5f + CottageInterior.Landing + 0.55f;

                // ----- Ground floor -----

                var split = _rng.Pick(-0.5f, 0f, 0.5f);
                var hallLeft = Wall(0, -Hall, minZ, -Hall, maxZ);
                var hallRight = Wall(0, Hall, minZ, Hall, maxZ);
                var between = Wall(0, Hall, split, maxX, split);

                var living = new Aabb(minX, minZ, -Hall - Face, maxZ);
                var rightFront = new Aabb(Hall + Face, split + Face, maxX, maxZ);
                var rightBack = new Aabb(Hall + Face, minZ, maxX, split - Face);
                var kitchenAtBack = _rng.Chance();
                var kitchen = kitchenAtBack ? rightBack : rightFront;
                var dining = kitchenAtBack ? rightFront : rightBack;

                // The front room on the right opens off the hall, and the back one off it.
                var livingDoor = _rng.Pick(2f, -2f);
                Door(0, hallLeft, livingDoor, -livingDoor);
                Door(0, hallRight, Math.Max(rightFront.Center.Z, clearOfStairs), maxZ - 0.6f);
                Door(0, between, (Hall + maxX) * 0.5f, maxX - 0.8f);

                // Living room: a couch against the outside wall, facing a TV across the room.
                var sofaZ = -livingDoor * 0.2f + _rng.Pick(-0.25f, 0f, 0.25f);
                Against(0, living, Side.Left, "couch", sofaZ, sofaZ - livingDoor * 0.25f);
                if (_rng.Chance(0.6f))
                {
                    var stand = Against(0, living, Side.Right, Furnishings.Dresser, sofaZ, sofaZ - livingDoor * 0.25f);
                    if (stand != null) OnTop(0, stand, Furnishings.Tv);
                }
                else
                {
                    Against(0, living, Side.Right, Furnishings.Tv, sofaZ, sofaZ - livingDoor * 0.25f);
                }

                if (_rng.Chance()) Against(0, living, livingDoor > 0f ? Side.Back : Side.Front, Furnishings.Dresser, living.MaxX - 0.8f, living.Center.X);

                // Kitchen: counters along the outside walls, round the far corner.
                var kitchenEnd = kitchenAtBack ? Side.Back : Side.Front;
                for (var i = 0; i < 3; i++) Against(0, kitchen, kitchenEnd, "counter", kitchen.MaxX - 0.5f - i);
                var inward = kitchenAtBack ? 1f : -1f;
                var corner = kitchenAtBack ? kitchen.MinZ : kitchen.MaxZ;
                for (var i = 0; i < 2; i++) Against(0, kitchen, Side.Right, "counter", corner + inward * (1.1f + i));

                // Dining room: the table in the middle, maybe a sideboard.
                var tableTurn = _rng.Pick(0f, 90f);
                if (Item(0, "dining", dining.Center.X, dining.Center.Z, tableTurn) == null) Item(0, "dining", dining.Center.X, dining.Center.Z, 90f - tableTurn);
                if (_rng.Chance(0.6f)) Against(0, dining, Side.Right, Furnishings.Dresser, dining.Center.Z);

                var hall = new Aabb(-Hall + Face, minZ, Hall - Face, maxZ);
                foreach (var room in new[] { living, rightFront, rightBack, hall }) Windows(0, room);

                // ----- Upstairs -----

                var upSplit = _rng.Pick(-0.5f, 0f, 0.5f);
                var upLeft = Wall(1, -Hall, minZ, -Hall, maxZ);
                var upRight = Wall(1, Hall, minZ, Hall, maxZ);
                Wall(1, minX, upSplit, -Hall, upSplit);

                var main = new Aabb(Hall + Face, minZ, maxX, maxZ);
                var leftFront = new Aabb(minX, upSplit + Face, -Hall - Face, maxZ);
                var leftBack = new Aabb(minX, minZ, -Hall - Face, upSplit - Face);
                var bathAtBack = _rng.Chance();
                var bath = bathAtBack ? leftBack : leftFront;
                var spare = bathAtBack ? leftFront : leftBack;

                Door(1, upRight, _rng.Pick(1.75f, 2.5f), clearOfStairs);
                Door(1, upLeft, leftFront.Center.Z);
                Door(1, upLeft, leftBack.Center.Z);

                // Main bedroom: the bed against the outside wall with a TV facing it, or against the back.
                if (_rng.Chance())
                {
                    var bedZ = _rng.Pick(-1f, -0.5f, 0f);
                    Against(1, main, Side.Right, "bed", bedZ);
                    var stand = Against(1, main, Side.Left, Furnishings.Dresser, bedZ, bedZ - 0.5f);
                    if (stand != null && _rng.Chance(0.7f)) OnTop(1, stand, Furnishings.Tv);
                }
                else
                {
                    Against(1, main, Side.Back, "bed", main.Center.X);
                    Against(1, main, Side.Right, Furnishings.Dresser, 0.5f, 1f);
                }

                // Bathroom: shower in the outside corner, then the toilet and the sink along the same wall.
                var bathEnd = bathAtBack ? Side.Back : Side.Front;
                Against(1, bath, bathEnd, "shower", bath.MinX + 0.45f);
                Against(1, bath, bathEnd, "toilet", bath.MinX + 1.3f);
                Against(1, bath, bathEnd, "sink", bath.MinX + 2f, bath.MinX + 2.4f);

                // Second bedroom: a bed against the outside wall and a dresser out of the doorway.
                var spareEnd = bathAtBack ? Side.Front : Side.Back;
                Against(1, spare, Side.Left, "bed", spare.Center.Z, spare.Center.Z + 0.25f, spare.Center.Z - 0.25f);
                Against(1, spare, spareEnd, Furnishings.Dresser, spare.MaxX - 0.7f, spare.Center.X);

                var landing = new Aabb(-Hall + Face, minZ, Hall - Face, maxZ);
                foreach (var room in new[] { main, leftFront, leftBack, landing }) Windows(1, room);

                if (_rng.Chance()) Mirror();
            }

            // ----- Parts -----

            private string NextId(int floor, string kind) => $"{floor}-{kind}-{++_next}";

            private string Wall(int floor, float x0, float z0, float x1, float z1)
            {
                var a = new Vec2(Q(x0), Q(z0));
                var b = new Vec2(Q(x1), Q(z1));
                if (!InteriorRules.CanAddWall(_type, Interior, floor, a, b).Ok) return null;
                var id = NextId(floor, "wall");
                Interior.Floors[floor].Walls.Add(new InteriorWallDto { Id = id, X0 = a.X, Z0 = a.Z, X1 = b.X, Z1 = b.Z });
                return id;
            }

            /// <summary>A door in a wall, at the first of the places along it that works.</summary>
            private void Door(int floor, string wallId, params float[] at)
            {
                if (wallId == null) return;
                foreach (var candidate in at)
                {
                    var spot = Q(candidate);
                    if (!InteriorRules.CanAddOpening(_type, Interior, floor, Furnishings.Door, wallId, spot).Ok) continue;
                    Interior.Floors[floor].Openings.Add(new OpeningDto { Id = NextId(floor, "door"), TypeId = Furnishings.Door, WallId = wallId, At = spot });
                    return;
                }
            }

            private string Item(int floor, string typeId, float x, float z, float rotationDeg, string onId = null)
            {
                x = Q(x);
                z = Q(z);
                if (!InteriorRules.CanPlaceItem(_type, Interior, floor, typeId, x, z, rotationDeg, onId).Ok) return null;
                var id = NextId(floor, typeId);
                Interior.Floors[floor].Items.Add(new FurnitureDto { Id = id, TypeId = typeId, X = x, Z = z, RotationDeg = rotationDeg, OnId = onId });
                return id;
            }

            /// <summary>
            /// Something with its back to one side of a room, facing in, at the first of the places
            /// along that side that works.
            /// </summary>
            private string Against(int floor, in Aabb room, Side side, string typeId, params float[] along)
            {
                if (!Furnishings.TryGetFurniture(typeId, out var kind)) return null;
                var half = kind.Depth * 0.5f;
                foreach (var a in along)
                {
                    var id = side switch
                    {
                        Side.Back => Item(floor, typeId, a, room.MinZ + half, 0f),
                        Side.Front => Item(floor, typeId, a, room.MaxZ - half, 180f),
                        Side.Left => Item(floor, typeId, room.MinX + half, a, 90f),
                        _ => Item(floor, typeId, room.MaxX - half, a, 270f),
                    };
                    if (id != null) return id;
                }

                return null;
            }

            /// <summary>A TV standing on a dresser.</summary>
            private void OnTop(int floor, string dresserId, string typeId)
            {
                var dresser = Interior.Floors[floor].Items.Find(i => i.Id == dresserId);
                if (dresser != null) Item(floor, typeId, dresser.X, dresser.Z, dresser.RotationDeg, dresserId);
            }

            /// <summary>
            /// Windows in the outside walls of a room: one per wall, or two along a long one, nudged
            /// along to clear doors, other walls, and anything tall standing against the wall.
            /// </summary>
            private void Windows(int floor, in Aabb room)
            {
                Furnishings.TryGetOpening(Furnishings.Window, out var window);
                foreach (var wall in CottageInterior.OutsideWalls(_type))
                {
                    float from, to;
                    if (wall.AlongX && Near(wall.Fixed > 0f ? room.MaxZ : room.MinZ, wall.Fixed > 0f ? _inside.MaxZ : _inside.MinZ)) (from, to) = (room.MinX, room.MaxX);
                    else if (!wall.AlongX && Near(wall.Fixed > 0f ? room.MaxX : room.MinX, wall.Fixed > 0f ? _inside.MaxX : _inside.MinX)) (from, to) = (room.MinZ, room.MaxZ);
                    else continue;

                    var count = to - from >= 6f ? 2 : 1;
                    for (var i = 0; i < count; i++)
                    {
                        var preferred = from + (to - from) * (i + 0.5f) / count;
                        foreach (var nudge in new[] { 0f, 0.25f, -0.25f, 0.5f, -0.5f, 0.75f, -0.75f, 1f, -1f })
                        {
                            var at = Q(preferred + nudge);
                            if (at - window.Width * 0.5f < from || at + window.Width * 0.5f > to) continue;
                            if (Blocked(floor, wall, at, window.Width)) continue;
                            if (!InteriorRules.CanAddOpening(_type, Interior, floor, Furnishings.Window, wall.Id, at).Ok) continue;
                            Interior.Floors[floor].Openings.Add(new OpeningDto { Id = NextId(floor, "window"), TypeId = Furnishings.Window, WallId = wall.Id, At = at });
                            break;
                        }
                    }
                }
            }

            /// <summary>Whether a shower, a bed's headboard or a TV stands in front of this stretch of wall.</summary>
            private bool Blocked(int floor, in CottageInterior.WallLine wall, float at, float width)
            {
                var zone = wall.Span(at - width * 0.5f - 0.1f, at + width * 0.5f + 0.1f, 0.7f);
                foreach (var item in Interior.Floors[floor].Items)
                {
                    if (item.TypeId != "shower" && item.TypeId != "bed" && item.TypeId != Furnishings.Tv) continue;
                    if (CottageInterior.TryItemBox(item, out _, out var box) && box.Overlaps(zone)) return true;
                }

                return false;
            }

            /// <summary>Flips the whole inside left to right. The cottage is symmetrical, so it all still fits.</summary>
            private void Mirror()
            {
                Interior.Stairs.X = Q(-Interior.Stairs.X);
                Interior.Stairs.RotationDeg = TownMath.NormalizeAngle(360f - Interior.Stairs.RotationDeg);
                foreach (var floor in Interior.Floors)
                {
                    // Openings first: they look up their walls, which are about to move.
                    foreach (var opening in floor.Openings)
                    {
                        if (opening.WallId == CottageInterior.LeftWall) opening.WallId = CottageInterior.RightWall;
                        else if (opening.WallId == CottageInterior.RightWall) opening.WallId = CottageInterior.LeftWall;
                        else if (CottageInterior.TryFindWall(_type, floor, opening.WallId, out var wall) && wall.AlongX) opening.At = Q(-opening.At);
                    }

                    foreach (var wall in floor.Walls)
                    {
                        wall.X0 = Q(-wall.X0);
                        wall.X1 = Q(-wall.X1);
                    }

                    foreach (var item in floor.Items)
                    {
                        item.X = Q(-item.X);
                        item.RotationDeg = TownMath.NormalizeAngle(360f - item.RotationDeg);
                    }
                }
            }

            private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;
        }

        /// <summary>Rounded onto the position grid, and never -0.</summary>
        private static float Q(float value) => TownMath.Quantize(value) + 0f;

        /// <summary>A small, fast generator that gives the same numbers on every platform (unlike string.GetHashCode).</summary>
        private sealed class Rng
        {
            private uint _state;

            public Rng(string seed)
            {
                // FNV-1a over the seed, then a few rounds to spread it out.
                var hash = 2166136261u;
                foreach (var c in seed ?? "")
                {
                    hash ^= c;
                    hash *= 16777619u;
                }

                _state = hash == 0 ? 0x9E3779B9u : hash;
                for (var i = 0; i < 4; i++) Next();
            }

            private uint Next()
            {
                var x = _state;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                return _state = x;
            }

            public bool Chance(float probability = 0.5f) => Next() % 10000u < probability * 10000f;

            public T Pick<T>(params T[] options) => options[(int)(Next() % (uint)options.Length)];
        }
    }
}

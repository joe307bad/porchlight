using System;
using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// The shape of a cottage's inside, worked out from its <see cref="InteriorDto"/>. Everything is in
    /// the building's frame (metres from its middle, +Z out of the front door) and lines up with its
    /// axes. Heights are above the bottom of the building.
    ///
    /// Two storeys of <see cref="StoryHeight"/> stand on a <see cref="Plinth"/>. The outside walls are
    /// <see cref="OuterWall"/> thick, inside walls <see cref="InnerWall"/>, and a fixed front door in
    /// the middle of the front wall leads in. Stairs climb from the ground floor through a hole in the
    /// floor above.
    /// </summary>
    public static class CottageInterior
    {
        public const string TypeId = "house_small";

        public const string FrontWall = "front";
        public const string BackWall = "back";
        public const string LeftWall = "left";
        public const string RightWall = "right";
        public const string FrontDoorId = "front-door";

        public const int FloorCount = 2;
        public const float Plinth = 0.25f;
        public const float StoryHeight = 2.6f;
        public const float SlabThickness = 0.2f;
        public const float OuterWall = 0.25f;
        public const float InnerWall = 0.1f;

        /// <summary>The snapping grid. The inside of the outside walls lies on it.</summary>
        public const float Grid = 0.25f;

        public const float FrontDoorWidth = 1.2f;
        public const float MinWallLength = 0.5f;

        /// <summary>Parallel walls need at least this much between their centre lines.</summary>
        public const float MinWallGap = 0.3f;

        public const float StairsWidth = 1f;
        public const float StairsLength = 3f;
        public const int StairsSteps = 12;

        /// <summary>The space kept clear at the foot and the head of the stairs.</summary>
        public const float Landing = 0.7f;

        /// <summary>The space kept clear either side of a door.</summary>
        public const float DoorSpace = 0.6f;

        /// <summary>How far above the floor a wall-hung TV's bottom edge is.</summary>
        public const float WallTvBottom = 1.2f;

        public const int MaxWallsPerFloor = 60;
        public const int MaxOpeningsPerFloor = 40;
        public const int MaxItemsPerFloor = 60;

        private static InteriorDto _default;

        public static float FloorHeight(int floor) => Plinth + floor * StoryHeight;
        public static float WallTop => Plinth + FloorCount * StoryHeight;

        /// <summary>Which floor someone this far above the building's base is on. Halfway up the stairs is the switch.</summary>
        public static int LevelAt(float heightAboveBase) => heightAboveBase > Plinth + StoryHeight * 0.5f ? 1 : 0;

        public static bool HasInterior(string typeId) => typeId == TypeId;
        public static bool HasInterior(BuildingDto building) => building != null && HasInterior(building.TypeId);

        /// <summary>The floor space inside the outside walls.</summary>
        public static Aabb Inside(BuildingType type) =>
            new Aabb(-type.Width * 0.5f + OuterWall, -type.Depth * 0.5f + OuterWall, type.Width * 0.5f - OuterWall, type.Depth * 0.5f - OuterWall);

        /// <summary>
        /// A building's inside, or the default layout for one that's never been edited. The default is
        /// shared, so don't change what this returns: edit a <see cref="InteriorDto.Clone"/>.
        /// </summary>
        public static InteriorDto Of(BuildingDto building) => building.Interior ?? (_default ??= Default());

        /// <summary>A new cottage: stairs along the back wall, empty rooms, and windows either side of the front door.</summary>
        public static InteriorDto Default()
        {
            var interior = new InteriorDto { Stairs = new StairsDto { X = 1f, Z = -2.25f, RotationDeg = 270f } };
            for (var i = 0; i < FloorCount; i++) interior.Floors.Add(new InteriorFloorDto());

            interior.Floors[0].Openings.Add(new OpeningDto { Id = "window-0a", TypeId = Furnishings.Window, WallId = FrontWall, At = -2f });
            interior.Floors[0].Openings.Add(new OpeningDto { Id = "window-0b", TypeId = Furnishings.Window, WallId = FrontWall, At = 2f });
            interior.Floors[1].Openings.Add(new OpeningDto { Id = "window-1a", TypeId = Furnishings.Window, WallId = FrontWall, At = -1.75f });
            interior.Floors[1].Openings.Add(new OpeningDto { Id = "window-1b", TypeId = Furnishings.Window, WallId = FrontWall, At = 1.75f });
            interior.Floors[1].Openings.Add(new OpeningDto { Id = "window-1c", TypeId = Furnishings.Window, WallId = LeftWall, At = 0f });
            interior.Floors[1].Openings.Add(new OpeningDto { Id = "window-1d", TypeId = Furnishings.Window, WallId = RightWall, At = 0f });
            return interior;
        }

        // ----- Stairs -----

        /// <summary>Rotations inside a cottage are quarter turns.</summary>
        public static bool IsQuarterTurn(float rotationDeg)
        {
            var r = rotationDeg % 90f;
            return MathF.Abs(r) < 0.05f || MathF.Abs(MathF.Abs(r) - 90f) < 0.05f;
        }

        /// <summary>Which way something facing <paramref name="rotationDeg"/> faces, as an exact axis.</summary>
        public static Vec2 Facing(float rotationDeg)
        {
            var f = Vec2.FromHeading(rotationDeg);
            return MathF.Abs(f.X) > MathF.Abs(f.Z) ? new Vec2(MathF.Sign(f.X), 0f) : new Vec2(0f, MathF.Sign(f.Z));
        }

        private static bool Sideways(float rotationDeg) => MathF.Abs(Facing(rotationDeg).X) > 0.5f;

        /// <summary>A footprint of <paramref name="width"/> by <paramref name="depth"/> facing <paramref name="rotationDeg"/>.</summary>
        public static Aabb Footprint(Vec2 center, float width, float depth, float rotationDeg) =>
            Sideways(rotationDeg) ? Aabb.Around(center, depth * 0.5f, width * 0.5f) : Aabb.Around(center, width * 0.5f, depth * 0.5f);

        public static Aabb StairsBox(StairsDto stairs) => Footprint(new Vec2(stairs.X, stairs.Z), StairsWidth, StairsLength, stairs.RotationDeg);

        /// <summary>The space kept clear in front of the bottom step (ground floor) or the top one (upstairs).</summary>
        public static Aabb StairsLanding(StairsDto stairs, int floor)
        {
            var up = Facing(stairs.RotationDeg);
            var offset = (StairsLength + Landing) * 0.5f * (floor == 0 ? -1f : 1f);
            return Footprint(new Vec2(stairs.X, stairs.Z) + up * offset, StairsWidth, Landing, stairs.RotationDeg);
        }

        /// <summary>The stairs plus the landing at their end on this floor: nothing else may go here.</summary>
        public static Aabb StairsKeepOut(StairsDto stairs, int floor) => StairsBox(stairs).Union(StairsLanding(stairs, floor));

        /// <summary>How far up the stairs a point is, 0 at the bottom step to 1 at the top.</summary>
        public static float StairsProgress(StairsDto stairs, Vec2 point)
        {
            var along = Vec2.Dot(point - new Vec2(stairs.X, stairs.Z), Facing(stairs.RotationDeg));
            return Math.Clamp(along / StairsLength + 0.5f, 0f, 1f);
        }

        // ----- Walls -----

        /// <summary>
        /// A wall as a line along one axis: centred on <see cref="Fixed"/> across it, and running
        /// <see cref="From"/>..<see cref="To"/> along it. For the outside walls that's just the inside span.
        /// </summary>
        public readonly struct WallLine
        {
            public WallLine(string id, bool alongX, float fixedAt, float from, float to, bool outside)
            {
                Id = id;
                AlongX = alongX;
                Fixed = fixedAt;
                From = Math.Min(from, to);
                To = Math.Max(from, to);
                Outside = outside;
            }

            public string Id { get; }
            public bool AlongX { get; }
            public float Fixed { get; }
            public float From { get; }
            public float To { get; }
            public bool Outside { get; }
            public float HalfThickness => Outside ? OuterWall * 0.5f : InnerWall * 0.5f;
            public float Length => To - From;

            /// <summary>A coordinate along the wall and one across it, as a point.</summary>
            public Vec2 Point(float along, float across) => AlongX ? new Vec2(along, across) : new Vec2(across, along);

            public float AlongOf(Vec2 p) => AlongX ? p.X : p.Z;
            public float AcrossOf(Vec2 p) => AlongX ? p.Z : p.X;

            /// <summary>The solid part. Inside walls reach past their ends by half their thickness, so corners close.</summary>
            public Aabb Box
            {
                get
                {
                    var h = HalfThickness;
                    var cap = Outside ? 0f : h;
                    return AlongX ? new Aabb(From - cap, Fixed - h, To + cap, Fixed + h) : new Aabb(Fixed - h, From - cap, Fixed + h, To + cap);
                }
            }

            /// <summary>A stretch of the wall, through its whole thickness and <paramref name="spill"/> beyond each face.</summary>
            public Aabb Span(float from, float to, float spill = 0f)
            {
                var h = HalfThickness + spill;
                return AlongX ? new Aabb(from, Fixed - h, to, Fixed + h) : new Aabb(Fixed - h, from, Fixed + h, to);
            }
        }

        public static bool TryWallLine(InteriorWallDto wall, out WallLine line)
        {
            line = default;
            if (wall == null) return false;
            if (MathF.Abs(wall.Z0 - wall.Z1) < 0.001f) line = new WallLine(wall.Id, true, wall.Z0, wall.X0, wall.X1, false);
            else if (MathF.Abs(wall.X0 - wall.X1) < 0.001f) line = new WallLine(wall.Id, false, wall.X0, wall.Z0, wall.Z1, false);
            else return false;
            return true;
        }

        public static IEnumerable<WallLine> OutsideWalls(BuildingType type)
        {
            var inside = Inside(type);
            const float c = OuterWall * 0.5f;
            yield return new WallLine(FrontWall, true, inside.MaxZ + c, inside.MinX, inside.MaxX, true);
            yield return new WallLine(BackWall, true, inside.MinZ - c, inside.MinX, inside.MaxX, true);
            yield return new WallLine(LeftWall, false, inside.MinX - c, inside.MinZ, inside.MaxZ, true);
            yield return new WallLine(RightWall, false, inside.MaxX + c, inside.MinZ, inside.MaxZ, true);
        }

        /// <summary>The outside walls and this floor's inside walls.</summary>
        public static List<WallLine> Walls(BuildingType type, InteriorFloorDto floor)
        {
            var lines = new List<WallLine>(OutsideWalls(type));
            foreach (var wall in floor.Walls)
            {
                if (TryWallLine(wall, out var line)) lines.Add(line);
            }

            return lines;
        }

        public static bool TryFindWall(BuildingType type, InteriorFloorDto floor, string wallId, out WallLine line)
        {
            foreach (var outside in OutsideWalls(type))
            {
                if (outside.Id != wallId) continue;
                line = outside;
                return true;
            }

            foreach (var wall in floor.Walls)
            {
                if (wall?.Id == wallId && TryWallLine(wall, out line)) return true;
            }

            line = default;
            return false;
        }

        // ----- Doors and windows -----

        /// <summary>This floor's doors and windows, plus the front door on the ground floor.</summary>
        public static List<OpeningDto> Openings(InteriorDto interior, int floor)
        {
            var openings = new List<OpeningDto>(interior.Floors[floor].Openings);
            if (floor == 0) openings.Add(new OpeningDto { Id = FrontDoorId, TypeId = Furnishings.Door, WallId = FrontWall, At = 0f });
            return openings;
        }

        public static float OpeningWidth(OpeningDto opening) =>
            opening.Id == FrontDoorId ? FrontDoorWidth : Furnishings.TryGetOpening(opening.TypeId, out var type) ? type.Width : 1f;

        public static Aabb OpeningBox(in WallLine wall, float at, float width) => wall.Span(at - width * 0.5f, at + width * 0.5f);

        /// <summary>The floor in front of a door, on both sides (just the inside for an outside door).</summary>
        public static Aabb DoorClearance(in WallLine wall, float at, float width)
        {
            float from = at - width * 0.5f, to = at + width * 0.5f;
            if (!wall.Outside) return wall.Span(from, to, DoorSpace);

            // Outside walls: only the side facing in.
            var inward = wall.Fixed > 0f ? -1f : 1f;
            var face = wall.Fixed + inward * wall.HalfThickness;
            var far = face + inward * DoorSpace;
            return wall.AlongX ? new Aabb(from, face, to, far) : new Aabb(face, from, far, to);
        }

        /// <summary>The clear space in front of every door on a floor.</summary>
        public static List<Aabb> DoorClearances(BuildingType type, InteriorDto interior, int floor)
        {
            var boxes = new List<Aabb>();
            foreach (var opening in Openings(interior, floor))
            {
                if (!Furnishings.TryGetOpening(opening.TypeId, out var kind) || !kind.IsDoor) continue;
                if (TryFindWall(type, interior.Floors[floor], opening.WallId, out var wall)) boxes.Add(DoorClearance(wall, opening.At, OpeningWidth(opening)));
            }

            return boxes;
        }

        // ----- Furniture -----

        public static Aabb ItemBox(FurnitureType type, float x, float z, float rotationDeg) => Footprint(new Vec2(x, z), type.Width, type.Depth, rotationDeg);

        public static bool TryItemBox(FurnitureDto item, out FurnitureType type, out Aabb box)
        {
            box = default;
            if (item == null || !Furnishings.TryGetFurniture(item.TypeId, out type)) { type = null; return false; }
            box = ItemBox(type, item.X, item.Z, item.RotationDeg);
            return true;
        }

        /// <summary>A TV on a wall or a dresser: it doesn't take up any floor.</summary>
        public static bool IsOffTheFloor(FurnitureDto item) => item.TypeId == Furnishings.Tv;

        /// <summary>
        /// One face of a wall or of a piece of furniture, as a line: it lies at <see cref="Fixed"/>
        /// across its axis, runs <see cref="From"/>..<see cref="To"/>, and faces <see cref="Normal"/>
        /// (+1 or -1 across the axis).
        /// </summary>
        public readonly struct Face
        {
            public Face(bool alongX, float fixedAt, float from, float to, int normal, string ownerId)
            {
                AlongX = alongX;
                Fixed = fixedAt;
                From = from;
                To = to;
                Normal = normal;
                OwnerId = ownerId;
            }

            public bool AlongX { get; }
            public float Fixed { get; }
            public float From { get; }
            public float To { get; }
            public int Normal { get; }
            public string OwnerId { get; }

            /// <summary>The rotation of something standing with its back to this face.</summary>
            public float FacingRotation => AlongX ? (Normal > 0 ? 0f : 180f) : (Normal > 0 ? 90f : 270f);
        }

        /// <summary>The room-side faces of the outside walls and both faces of each inside wall.</summary>
        public static List<Face> WallFaces(BuildingType type, InteriorFloorDto floor)
        {
            var faces = new List<Face>();
            var inside = Inside(type);
            faces.Add(new Face(true, inside.MaxZ, inside.MinX, inside.MaxX, -1, FrontWall));
            faces.Add(new Face(true, inside.MinZ, inside.MinX, inside.MaxX, 1, BackWall));
            faces.Add(new Face(false, inside.MinX, inside.MinZ, inside.MaxZ, 1, LeftWall));
            faces.Add(new Face(false, inside.MaxX, inside.MinZ, inside.MaxZ, -1, RightWall));
            foreach (var wall in floor.Walls)
            {
                if (!TryWallLine(wall, out var line)) continue;
                var h = InnerWall * 0.5f;
                faces.Add(new Face(line.AlongX, line.Fixed + h, line.From - h, line.To + h, 1, line.Id));
                faces.Add(new Face(line.AlongX, line.Fixed - h, line.From - h, line.To + h, -1, line.Id));
            }

            return faces;
        }

        /// <summary>The four sides of a rectangle as faces pointing out of it.</summary>
        public static void AddBoxFaces(List<Face> faces, in Aabb box, string ownerId)
        {
            faces.Add(new Face(true, box.MaxZ, box.MinX, box.MaxX, 1, ownerId));
            faces.Add(new Face(true, box.MinZ, box.MinX, box.MaxX, -1, ownerId));
            faces.Add(new Face(false, box.MaxX, box.MinZ, box.MaxZ, 1, ownerId));
            faces.Add(new Face(false, box.MinX, box.MinZ, box.MaxZ, -1, ownerId));
        }

        /// <summary>
        /// The wall face that something facing <paramref name="rotationDeg"/> with this footprint has its
        /// back flat against, if any. Its whole back must be against the wall.
        /// </summary>
        public static bool TryBackingFace(List<Face> wallFaces, in Aabb box, float rotationDeg, out Face backing, float tolerance = 0.02f)
        {
            var front = Facing(rotationDeg);
            var alongX = MathF.Abs(front.Z) > 0.5f;
            var normal = alongX ? (int)MathF.Sign(front.Z) : (int)MathF.Sign(front.X);
            var back = alongX ? (normal > 0 ? box.MinZ : box.MaxZ) : (normal > 0 ? box.MinX : box.MaxX);
            float from = alongX ? box.MinX : box.MinZ, to = alongX ? box.MaxX : box.MaxZ;
            foreach (var face in wallFaces)
            {
                if (face.AlongX != alongX || face.Normal != normal || MathF.Abs(face.Fixed - back) > tolerance) continue;
                if (from < face.From - tolerance || to > face.To + tolerance) continue;
                backing = face;
                return true;
            }

            backing = default;
            return false;
        }

        // ----- Floors, for walking -----

        /// <summary>
        /// How high the floor is at a point, for someone at this height (both above the building's base).
        /// Null outside the building and its plinth.
        /// </summary>
        public static float? FloorAt(BuildingType type, InteriorDto interior, Vec2 local, float heightAboveBase)
        {
            var plinth = Aabb.Around(Vec2.Zero, type.Width * 0.5f + PlinthMargin, type.Depth * 0.5f + PlinthMargin);
            if (!plinth.Contains(local)) return null;

            if (interior.Stairs != null && StairsBox(interior.Stairs).Contains(local))
                return Plinth + StairsProgress(interior.Stairs, local) * StoryHeight;

            // Only the inside has an upstairs.
            return Inside(type).Contains(local) ? FloorHeight(LevelAt(heightAboveBase)) : Plinth;
        }

        /// <summary>How far the plinth sticks out past the walls.</summary>
        public const float PlinthMargin = 0.2f;

        /// <summary>
        /// The solid parts on one level, for walking into: the outside walls (with the front door open
        /// downstairs), the inside walls less their doorways, rails along the stairs, and furniture.
        /// </summary>
        public static void Colliders(BuildingType type, InteriorDto interior, int level, List<Aabb> into)
        {
            level = Math.Clamp(level, 0, FloorCount - 1);
            var floor = interior.Floors.Count > level ? interior.Floors[level] : null;
            var openings = floor == null ? new List<OpeningDto>() : Openings(interior, level);
            float hw = type.Width * 0.5f, hd = type.Depth * 0.5f;

            foreach (var wall in OutsideWalls(type))
            {
                // Outside walls cover the corners too.
                var full = wall.AlongX ? new WallLine(wall.Id, true, wall.Fixed, -hw, hw, true) : wall;
                AddWithDoorways(into, full, openings);
            }

            if (floor != null)
            {
                foreach (var wall in floor.Walls)
                {
                    if (TryWallLine(wall, out var line)) AddWithDoorways(into, line, openings);
                }

                foreach (var item in floor.Items)
                {
                    if (!IsOffTheFloor(item) && TryItemBox(item, out _, out var box)) into.Add(box);
                }
            }

            // Rails down the sides, and across the end that isn't a way on or off at this level.
            if (interior.Stairs == null) return;
            const float rail = 0.05f;
            var stairs = StairsBox(interior.Stairs);
            var up = Facing(interior.Stairs.RotationDeg);
            if (MathF.Abs(up.Z) > 0.5f)
            {
                into.Add(new Aabb(stairs.MinX, stairs.MinZ, stairs.MinX + rail, stairs.MaxZ));
                into.Add(new Aabb(stairs.MaxX - rail, stairs.MinZ, stairs.MaxX, stairs.MaxZ));
                var closesPlusZ = (up.Z > 0f) == (level == 0);
                into.Add(closesPlusZ ? new Aabb(stairs.MinX, stairs.MaxZ - rail, stairs.MaxX, stairs.MaxZ) : new Aabb(stairs.MinX, stairs.MinZ, stairs.MaxX, stairs.MinZ + rail));
            }
            else
            {
                into.Add(new Aabb(stairs.MinX, stairs.MinZ, stairs.MaxX, stairs.MinZ + rail));
                into.Add(new Aabb(stairs.MinX, stairs.MaxZ - rail, stairs.MaxX, stairs.MaxZ));
                var closesPlusX = (up.X > 0f) == (level == 0);
                into.Add(closesPlusX ? new Aabb(stairs.MaxX - rail, stairs.MinZ, stairs.MaxX, stairs.MaxZ) : new Aabb(stairs.MinX, stairs.MinZ, stairs.MinX + rail, stairs.MaxZ));
            }
        }

        /// <summary>A wall's solid stretches between its doorways.</summary>
        private static void AddWithDoorways(List<Aabb> into, in WallLine wall, List<OpeningDto> openings)
        {
            var box = wall.Box;
            float start = wall.AlongX ? box.MinX : box.MinZ, end = wall.AlongX ? box.MaxX : box.MaxZ;
            var gaps = new List<(float From, float To)>();
            foreach (var opening in openings)
            {
                if (opening.WallId != wall.Id || !Furnishings.TryGetOpening(opening.TypeId, out var kind) || !kind.IsDoor) continue;
                var half = OpeningWidth(opening) * 0.5f;
                gaps.Add((opening.At - half, opening.At + half));
            }

            gaps.Sort((a, b) => a.From.CompareTo(b.From));
            foreach (var (from, to) in gaps)
            {
                if (from > start) into.Add(wall.Span(start, from));
                start = Math.Max(start, to);
            }

            if (end > start) into.Add(wall.Span(start, end));
        }
    }
}

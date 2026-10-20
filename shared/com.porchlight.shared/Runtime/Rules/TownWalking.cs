using System;
using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>A place to sit or lie down that a character walked into.</summary>
    public readonly struct FurnitureSpot
    {
        public FurnitureSpot(string buildingId, string itemId, FurnitureAction action, Vec2 position, float height, float heading, Vec2 exit)
        {
            BuildingId = buildingId;
            ItemId = itemId;
            Action = action;
            Position = position;
            Height = height;
            Heading = heading;
            Exit = exit;
        }

        public string BuildingId { get; }
        public string ItemId { get; }
        public FurnitureAction Action { get; }

        /// <summary>Where the character's feet go: the front of the seat, or the foot of the bed.</summary>
        public Vec2 Position { get; }

        /// <summary>The seat's height, or the top of the mattress, in the town.</summary>
        public float Height { get; }

        /// <summary>Which way the character faces. Lying down, their head points the other way.</summary>
        public float Heading { get; }

        /// <summary>Where they stand up to.</summary>
        public Vec2 Exit { get; }
    }

    /// <summary>
    /// Where a character on foot can go. Buildings are solid; anything no taller than
    /// <see cref="StepHeight"/> (parks) can be walked onto. Cottages can be walked into through their
    /// front door, and up their stairs (see <see cref="CottageInterior"/>), so inside them the
    /// character's height decides which floor's walls and furniture are in the way. Kept in the
    /// shared library so the server can check player movement once towns are multiplayer.
    /// </summary>
    public static class TownWalking
    {
        public const float StepHeight = 0.5f;
        public const float CharacterRadius = 0.3f;

        /// <summary>The longest stretch moved before checking for walls again; less than an inside wall plus a character's width.</summary>
        private const float MaxStep = 0.2f;

        [ThreadStatic] private static List<Aabb> _colliders;

        /// <summary>
        /// Moves from <paramref name="from"/> by <paramref name="delta"/>, sliding along buildings
        /// and staying inside the town. For someone at ground level outside.
        /// </summary>
        public static Vec2 Move(TownDto town, Vec2 from, Vec2 delta, float radius = CharacterRadius) => MoveAt(town, from, delta, float.NaN, radius);

        /// <summary>
        /// Moves a character whose feet are at height <paramref name="y"/> (NaN for ground level), which
        /// decides which floor of a cottage they're on.
        /// </summary>
        public static Vec2 MoveAt(TownDto town, Vec2 from, Vec2 delta, float y, float radius = CharacterRadius)
        {
            // Short steps, so a slow frame can't carry anyone through a thin wall.
            var steps = Math.Max(1, (int)MathF.Ceiling(delta.Length / MaxStep));
            var p = from;
            var pushedBy = Vec2.Zero;
            for (var i = 1; i <= steps; i++)
            {
                // Measured from the start each time, so an unobstructed walk ends exactly at from + delta.
                var unobstructed = from + delta * ((float)i / steps);
                p = unobstructed + pushedBy;

                // A few passes so a corner between two buildings settles.
                for (var pass = 0; pass < 3; pass++)
                {
                    var pushed = false;
                    foreach (var building in town.Buildings)
                    {
                        if (CottageInterior.HasInterior(building))
                        {
                            if (PushOutOfInterior(town, building, ref p, y, radius)) pushed = true;
                        }
                        else if (IsSolid(building, out var footprint) && PushOut(footprint, ref p, radius))
                        {
                            pushed = true;
                        }
                    }

                    if (!pushed) break;
                }

                pushedBy = p - unobstructed;
            }

            var half = town.Size * 0.5f - radius;
            return new Vec2(Math.Clamp(p.X, -half, half), Math.Clamp(p.Z, -half, half));
        }

        /// <summary>Height of the ground at a point for someone at ground level: the top of a park, a cottage floor, or the terrain.</summary>
        public static float GroundHeight(TownDto town, Vec2 point) => GroundHeightAt(town, point, float.NaN);

        /// <summary>
        /// Height of the ground under someone whose feet are at <paramref name="y"/> (NaN for ground level).
        /// Inside a cottage that's the floor they're on, or the stairs.
        /// </summary>
        public static float GroundHeightAt(TownDto town, Vec2 point, float y)
        {
            foreach (var building in town.Buildings)
            {
                if (!CottageInterior.HasInterior(building) || !TryNear(building, point, CottageInterior.PlinthMargin, out var type, out var footprint)) continue;
                var bottom = TownTerrain.BaseHeight(town, footprint);
                var local = Frames.ToLocal(footprint.Center, building.RotationDeg, point);
                var floor = CottageInterior.FloorAt(type, CottageInterior.Of(building), local, float.IsNaN(y) ? 0f : y - bottom);
                if (floor.HasValue) return bottom + floor.Value;
            }

            var height = TownTerrain.HeightAt(town, point);
            foreach (var building in town.Buildings)
            {
                if (!Catalog.TryGetBuilding(building.TypeId, out var type) || type.Height > StepHeight) continue;
                var footprint = TownRules.Footprint(type, building.X, building.Z, building.RotationDeg);
                if (footprint.Contains(point)) height = Math.Max(height, TownTerrain.BaseHeight(town, footprint) + type.Height);
            }

            return height;
        }

        /// <summary>
        /// True when a point is inside a building, for keeping cameras out of walls. The camera may go
        /// inside <paramref name="openBuildingId"/>, the cottage the player is in, whose roof is off.
        /// </summary>
        public static bool IsInsideBuilding(TownDto town, Vec2 point, float y, string openBuildingId = null)
        {
            foreach (var building in town.Buildings)
            {
                if (building.Id == openBuildingId || !IsSolid(building, out var footprint) || !footprint.Contains(point)) continue;
                Catalog.TryGetBuilding(building.TypeId, out var type);

                // Gabled roofs rise above the walls, up to about 0.8 of the half-depth.
                var roof = type.Roof == RoofStyle.Gabled ? Math.Min(type.Width, type.Depth) * 0.4f : 0f;
                if (y <= TownTerrain.BaseHeight(town, footprint) + type.Height + roof) return true;
            }

            return false;
        }

        public static bool IsClear(TownDto town, Vec2 point, float radius = CharacterRadius)
        {
            foreach (var building in town.Buildings)
            {
                var probe = point;
                if (IsSolid(building, out var footprint) && PushOut(footprint, ref probe, radius)) return false;
            }

            return true;
        }

        /// <summary>The nearest open spot to <paramref name="near"/>, searching outward in rings.</summary>
        public static Vec2 FindSpawn(TownDto town, Vec2 near, float radius = CharacterRadius)
        {
            var half = town.Size * 0.5f - radius;
            for (var ring = 0; ring < 100; ring++)
            {
                var steps = ring == 0 ? 1 : ring * 8;
                for (var i = 0; i < steps; i++)
                {
                    var candidate = near + Vec2.FromHeading(i * 360f / steps) * (ring * 2f);
                    if (Math.Abs(candidate.X) > half || Math.Abs(candidate.Z) > half) continue;
                    if (IsClear(town, candidate, radius)) return candidate;
                }
            }

            return near;
        }

        /// <summary>
        /// The cottage, and the floor in it, that a character standing here is inside of, if any.
        /// Floor is 0 downstairs; on the stairs it changes halfway up.
        /// </summary>
        public static bool TryFindInterior(TownDto town, Vec2 point, float y, out BuildingDto building, out int floor)
        {
            foreach (var candidate in town.Buildings)
            {
                if (!CottageInterior.HasInterior(candidate) || !TryNear(candidate, point, 0f, out var type, out var footprint)) continue;
                var local = Frames.ToLocal(footprint.Center, candidate.RotationDeg, point);
                if (!CottageInterior.Inside(type).Contains(local)) continue;
                building = candidate;
                floor = CottageInterior.LevelAt(y - TownTerrain.BaseHeight(town, footprint));
                return true;
            }

            building = null;
            floor = 0;
            return false;
        }

        /// <summary>
        /// Whether moving from <paramref name="from"/> by <paramref name="delta"/> walks into something
        /// to sit or lie on, and where the character would go.
        /// </summary>
        public static bool TryFindFurnitureSpot(TownDto town, Vec2 from, Vec2 delta, float y, out FurnitureSpot spot, float radius = CharacterRadius)
        {
            spot = default;
            if (delta.LengthSquared < 1e-8f || !TryFindInterior(town, from, y, out var building, out var level)) return false;

            TryNear(building, from, 1f, out var type, out var footprint);
            var interior = CottageInterior.Of(building);
            var localFrom = Frames.ToLocal(footprint.Center, building.RotationDeg, from);
            var localDelta = Frames.ToLocal(Vec2.Zero, building.RotationDeg, delta);
            var target = localFrom + localDelta;
            foreach (var item in interior.Floors[level].Items)
            {
                if (!CottageInterior.TryItemBox(item, out var kind, out var box) || kind.Action == FurnitureAction.None) continue;
                if (!box.Expanded(radius + 0.06f).Contains(target) || Vec2.Dot(localDelta, box.Center - localFrom) <= 0f) continue;

                var center = new Vec2(item.X, item.Z);
                var contact = Frames.ToLocal(center, item.RotationDeg, localFrom);
                Vec2 seat, exit;
                float height, turn = 0f;
                if (kind.Action == FurnitureAction.LieDown)
                {
                    // Feet at the foot of the bed, head on the pillow, getting off on the near side.
                    seat = new Vec2(0f, kind.Depth * 0.5f - 0.2f);
                    exit = new Vec2((contact.X < 0f ? -1f : 1f) * (kind.Width * 0.5f + 0.45f), 0f);
                    height = kind.Height;
                }
                else if (item.TypeId == "dining")
                {
                    // The nearest of the four chairs, facing the table.
                    var chair = new Vec2(contact.X < 0f ? -0.35f : 0.35f, contact.Z < 0f ? -0.7f : 0.7f);
                    seat = chair;
                    exit = chair + new Vec2(0f, chair.Z < 0f ? -0.6f : 0.6f);
                    turn = chair.Z > 0f ? 180f : 0f;
                    height = 0.47f;
                }
                else
                {
                    var room = Math.Max(0f, kind.Width * 0.5f - 0.45f);
                    var along = Math.Clamp(contact.X, -room, room);
                    seat = new Vec2(along, 0.05f);
                    exit = new Vec2(along, kind.Depth * 0.5f + 0.45f);
                    height = item.TypeId == "toilet" ? 0.42f : 0.45f;
                }

                Vec2 ToTown(Vec2 itemLocal) => Frames.ToWorld(footprint.Center, building.RotationDeg, Frames.ToWorld(center, item.RotationDeg, itemLocal));
                var bottom = TownTerrain.BaseHeight(town, footprint) + CottageInterior.FloorHeight(level);
                spot = new FurnitureSpot(building.Id, item.Id, kind.Action, ToTown(seat), bottom + height,
                    TownMath.NormalizeAngle(building.RotationDeg + item.RotationDeg + turn), ToTown(exit));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Where to stand up to from a seat or bed: off the side that faces most nearly along
        /// <paramref name="direction"/> (in the town) and has room to stand, with no wall between.
        /// A zero direction means the spot's usual side. Falls back to <see cref="FurnitureSpot.Exit"/>
        /// when no side has room.
        /// </summary>
        public static Vec2 ExitToward(TownDto town, FurnitureSpot spot, Vec2 direction, float radius = CharacterRadius)
        {
            if (direction.LengthSquared < 1e-6f) direction = spot.Exit - spot.Position;
            var building = town.Buildings.Find(b => b.Id == spot.BuildingId);
            if (building == null || direction.LengthSquared < 1e-6f || !Catalog.TryGetBuilding(building.TypeId, out var type)) return spot.Exit;

            var footprint = TownRules.Footprint(type, building.X, building.Z, building.RotationDeg);
            var interior = CottageInterior.Of(building);

            // The seat or mattress is a little way above the floor it stands on.
            var level = CottageInterior.LevelAt(spot.Height - TownTerrain.BaseHeight(town, footprint) - 0.3f);
            var item = interior.Floors[level].Items.Find(i => i.Id == spot.ItemId);
            if (item == null || !CottageInterior.TryItemBox(item, out var kind, out var box)) return spot.Exit;

            var colliders = new List<Aabb>();
            CottageInterior.Colliders(type, interior, level, colliders);
            var inside = CottageInterior.Inside(type);

            // Everything from here on is in the item's own frame (front is +Z), then the building's.
            var center = new Vec2(item.X, item.Z);
            Vec2 ToBuilding(Vec2 itemLocal) => Frames.ToWorld(center, item.RotationDeg, itemLocal);
            var wanted = Frames.ToLocal(Vec2.Zero, item.RotationDeg, Frames.ToLocal(Vec2.Zero, building.RotationDeg, direction)).Normalized;

            // Get off level with where they are: beside the middle of a bed, or the chair or stretch of couch they're on.
            var seat = kind.Action == FurnitureAction.LieDown
                ? Vec2.Zero
                : Frames.ToLocal(center, item.RotationDeg, Frames.ToLocal(footprint.Center, building.RotationDeg, spot.Position));
            float halfX = kind.Width * 0.5f, halfZ = kind.Depth * 0.5f, margin = radius + 0.15f;

            var sides = new[] { new Vec2(0f, 1f), new Vec2(0f, -1f), new Vec2(1f, 0f), new Vec2(-1f, 0f) };
            Array.Sort(sides, (a, b) => Vec2.Dot(b, wanted).CompareTo(Vec2.Dot(a, wanted)));
            foreach (var side in sides)
            {
                var edge = side.X != 0f ? new Vec2(side.X * halfX, seat.Z) : new Vec2(seat.X, side.Z * halfZ);
                var stand = ToBuilding(edge + side * margin);
                if (!inside.Contains(stand) || !Clear(stand, radius)) continue;
                if (interior.Stairs != null && CottageInterior.StairsBox(interior.Stairs).Expanded(radius).Contains(stand)) continue;

                // Nothing (a wall, most likely) between the edge and where they'd stand.
                var from = ToBuilding(edge);
                var blocked = false;
                for (var t = 0.1f; t < 1f && !blocked; t += 0.1f)
                {
                    blocked = !Clear(from + (stand - from) * t, 0.02f);
                }

                if (!blocked) return Frames.ToWorld(footprint.Center, building.RotationDeg, stand);
            }

            return spot.Exit;

            bool Clear(Vec2 point, float r)
            {
                foreach (var c in colliders)
                {
                    if (c.MinX == box.MinX && c.MinZ == box.MinZ && c.MaxX == box.MaxX && c.MaxZ == box.MaxZ) continue;
                    var dx = Math.Max(Math.Max(c.MinX - point.X, point.X - c.MaxX), 0f);
                    var dz = Math.Max(Math.Max(c.MinZ - point.Z, point.Z - c.MaxZ), 0f);
                    if (dx * dx + dz * dz < r * r) return false;
                }

                return true;
            }
        }

        /// <summary>A cottage's walls, stairs and furniture on the floor at height <paramref name="y"/>, pushing a circle out of them.</summary>
        private static bool PushOutOfInterior(TownDto town, BuildingDto building, ref Vec2 p, float y, float radius)
        {
            if (!TryNear(building, p, radius, out var type, out var footprint)) return false;

            var level = float.IsNaN(y) ? 0 : CottageInterior.LevelAt(y - TownTerrain.BaseHeight(town, footprint));
            var colliders = _colliders ??= new List<Aabb>();
            colliders.Clear();
            CottageInterior.Colliders(type, CottageInterior.Of(building), level, colliders);

            var pushed = false;
            foreach (var box in colliders)
            {
                if (PushOut(Frames.ToWorld(footprint.Center, building.RotationDeg, box), ref p, radius)) pushed = true;
            }

            return pushed;
        }

        /// <summary>A building's type and footprint, when <paramref name="point"/> is within <paramref name="margin"/> of its bounding circle.</summary>
        private static bool TryNear(BuildingDto building, Vec2 point, float margin, out BuildingType type, out OrientedRect footprint)
        {
            footprint = default;
            if (!Catalog.TryGetBuilding(building.TypeId, out type)) return false;
            footprint = TownRules.Footprint(type, building.X, building.Z, building.RotationDeg);
            var reach = footprint.BoundingRadius + margin;
            return (point - footprint.Center).LengthSquared <= reach * reach;
        }

        private static bool IsSolid(BuildingDto building, out OrientedRect footprint)
        {
            footprint = default;
            if (!Catalog.TryGetBuilding(building.TypeId, out var type) || type.Height <= StepHeight) return false;
            footprint = TownRules.Footprint(type, building.X, building.Z, building.RotationDeg);
            return true;
        }

        /// <summary>Pushes a circle out of a rectangle. Returns false when they don't touch.</summary>
        private static bool PushOut(in OrientedRect rect, ref Vec2 point, float radius)
        {
            var d = point - rect.Center;
            var reach = rect.BoundingRadius + radius;
            if (d.LengthSquared > reach * reach) return false;

            // Work in the rectangle's own axes.
            var x = Vec2.Dot(d, rect.AxisX);
            var z = Vec2.Dot(d, rect.AxisZ);
            var closestX = Math.Clamp(x, -rect.HalfWidth, rect.HalfWidth);
            var closestZ = Math.Clamp(z, -rect.HalfDepth, rect.HalfDepth);
            var offX = x - closestX;
            var offZ = z - closestZ;
            var distanceSq = offX * offX + offZ * offZ;
            if (distanceSq >= radius * radius) return false;

            if (distanceSq > 1e-8f)
            {
                var scale = radius / MathF.Sqrt(distanceSq);
                x = closestX + offX * scale;
                z = closestZ + offZ * scale;
            }
            else if (rect.HalfWidth - Math.Abs(x) < rect.HalfDepth - Math.Abs(z))
            {
                // The centre is inside: leave through the nearest side.
                x = (x >= 0f ? 1f : -1f) * (rect.HalfWidth + radius);
            }
            else
            {
                z = (z >= 0f ? 1f : -1f) * (rect.HalfDepth + radius);
            }

            point = rect.Center + rect.AxisX * x + rect.AxisZ * z;
            return true;
        }
    }
}

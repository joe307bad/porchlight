using System;
using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// What may go where inside a cottage. The interior editor asks these for instant feedback,
    /// <see cref="Editing.TownEditor"/> checks them before every change, and the server runs
    /// <see cref="Validate"/> on every save. Each check can skip one part by id, so a part already in
    /// the house can be checked against everything else.
    /// </summary>
    public static class InteriorRules
    {
        public static EditResult CanAddWall(BuildingType type, InteriorDto interior, int floor, Vec2 a, Vec2 b,
            string ignoreId = null, float tolerance = TownRules.ClientTolerance)
        {
            var check = CheckFloor(interior, floor);
            if (!check.Ok) return check;
            if (!Finite(a) || !Finite(b)) return EditResult.Fail("Invalid position");

            var level = interior.Floors[floor];
            if (ignoreId == null && level.Walls.Count >= CottageInterior.MaxWallsPerFloor) return EditResult.Fail("This floor has as many walls as it can take");

            var wall = new InteriorWallDto { Id = ignoreId ?? "", X0 = a.X, Z0 = a.Z, X1 = b.X, Z1 = b.Z };
            if (!CottageInterior.TryWallLine(wall, out var line)) return EditResult.Fail("Walls run straight along or across the house");
            if (line.Length < CottageInterior.MinWallLength - tolerance) return EditResult.Fail("That wall is too short");

            var inside = CottageInterior.Inside(type);
            if (!inside.Contains(a, tolerance) || !inside.Contains(b, tolerance)) return EditResult.Fail("Walls go inside the house");

            var limit = (line.AlongX ? inside.MaxZ : inside.MaxX) - CottageInterior.MinWallGap;
            if (MathF.Abs(line.Fixed) > limit + tolerance) return EditResult.Fail("Too close to the outside wall");

            foreach (var other in level.Walls)
            {
                if (other.Id == ignoreId || !CottageInterior.TryWallLine(other, out var otherLine) || otherLine.AlongX != line.AlongX) continue;
                if (line.From >= otherLine.To - tolerance || otherLine.From >= line.To - tolerance) continue;
                var gap = MathF.Abs(otherLine.Fixed - line.Fixed);
                if (gap < 0.01f) return EditResult.Fail("There's a wall there already");
                if (gap < CottageInterior.MinWallGap - tolerance) return EditResult.Fail("Too close to another wall");
            }

            var box = line.Box;
            if (interior.Stairs != null && box.Overlaps(CottageInterior.StairsKeepOut(interior.Stairs, floor), tolerance))
                return EditResult.Fail("The stairs are in the way");

            foreach (var item in level.Items)
            {
                if (CottageInterior.TryItemBox(item, out _, out var itemBox) && box.Overlaps(itemBox, tolerance)) return EditResult.Fail("Furniture is in the way");
            }

            foreach (var opening in CottageInterior.Openings(interior, floor))
            {
                if (!CottageInterior.TryFindWall(type, level, opening.WallId, out var host) || host.Id == ignoreId) continue;
                var width = CottageInterior.OpeningWidth(opening);
                if (box.Overlaps(CottageInterior.OpeningBox(host, opening.At, width), tolerance)) return EditResult.Fail("A door or window is in the way");
                if (IsDoor(opening) && box.Overlaps(CottageInterior.DoorClearance(host, opening.At, width), tolerance)) return EditResult.Fail("Leave room in front of the door");
            }

            return EditResult.Success;
        }

        public static EditResult CanAddOpening(BuildingType type, InteriorDto interior, int floor, string typeId, string wallId, float at,
            string ignoreId = null, float tolerance = TownRules.ClientTolerance)
        {
            var check = CheckFloor(interior, floor);
            if (!check.Ok) return check;
            if (!Furnishings.TryGetOpening(typeId, out var kind)) return EditResult.Fail($"Unknown door or window '{typeId}'");
            if (!TownRules.IsFinite(at)) return EditResult.Fail("Invalid position");

            var level = interior.Floors[floor];
            if (ignoreId == null && level.Openings.Count >= CottageInterior.MaxOpeningsPerFloor) return EditResult.Fail("This floor has as many doors and windows as it can take");
            if (!CottageInterior.TryFindWall(type, level, wallId, out var wall)) return EditResult.Fail(kind.IsDoor ? "Doors go in walls" : "Windows go in walls");
            if (kind.IsDoor && wall.Outside) return EditResult.Fail("The front door is the only way in for now");

            var half = kind.Width * 0.5f;
            var margin = wall.Outside ? 0.15f : 0.1f;
            if (at - half < wall.From + margin - tolerance || at + half > wall.To - margin + tolerance) return EditResult.Fail("It doesn't fit in this wall");

            foreach (var other in CottageInterior.Openings(interior, floor))
            {
                if (other.Id == ignoreId || other.WallId != wallId) continue;
                if (MathF.Abs(other.At - at) < half + CottageInterior.OpeningWidth(other) * 0.5f + 0.1f - tolerance) return EditResult.Fail("Too close to another door or window");
            }

            // Walls joining or crossing this one there would show through.
            var hole = CottageInterior.OpeningBox(wall, at, kind.Width);
            var reach = wall.Span(at - half, at + half, 0.02f);
            foreach (var other in level.Walls)
            {
                if (other.Id == wallId || !CottageInterior.TryWallLine(other, out var line)) continue;
                if (line.Box.Overlaps(reach, tolerance)) return EditResult.Fail("Another wall meets this one there");
            }

            foreach (var item in level.Items)
            {
                if (!CottageInterior.TryItemBox(item, out _, out var itemBox)) continue;

                // A TV hung on the wall would cover it.
                if (item.TypeId == Furnishings.Tv && item.OnId == null && itemBox.Overlaps(wall.Span(at - half, at + half, 0.15f), tolerance))
                    return EditResult.Fail("A TV is hanging there");
            }

            if (!kind.IsDoor) return EditResult.Success;

            var clearance = CottageInterior.DoorClearance(wall, at, kind.Width);
            if (interior.Stairs != null && clearance.Overlaps(CottageInterior.StairsKeepOut(interior.Stairs, floor), tolerance)) return EditResult.Fail("The stairs are in the way of the door");
            foreach (var item in level.Items)
            {
                if (!CottageInterior.IsOffTheFloor(item) && CottageInterior.TryItemBox(item, out _, out var itemBox) && clearance.Overlaps(itemBox, tolerance))
                    return EditResult.Fail("Furniture is in the way of the door");
            }

            return EditResult.Success;
        }

        public static EditResult CanPlaceItem(BuildingType type, InteriorDto interior, int floor, string typeId, float x, float z, float rotationDeg, string onId,
            string ignoreId = null, float tolerance = TownRules.ClientTolerance)
        {
            var check = CheckFloor(interior, floor);
            if (!check.Ok) return check;
            if (!Furnishings.TryGetFurniture(typeId, out var kind)) return EditResult.Fail($"Unknown furniture '{typeId}'");
            if (!TownRules.IsFinite(x) || !TownRules.IsFinite(z) || !TownRules.IsFinite(rotationDeg)) return EditResult.Fail("Invalid position");
            if (!CottageInterior.IsQuarterTurn(rotationDeg)) return EditResult.Fail("Furniture lines up with the walls");

            var level = interior.Floors[floor];
            if (ignoreId == null && level.Items.Count >= CottageInterior.MaxItemsPerFloor) return EditResult.Fail("This floor has as much furniture as it can take");

            var box = CottageInterior.ItemBox(kind, x, z, rotationDeg);
            if (!CottageInterior.Inside(type).Contains(box, tolerance)) return EditResult.Fail("Furniture goes inside the house");

            if (onId != null)
            {
                if (kind.Placement != FurniturePlacement.DresserOrWall) return EditResult.Fail($"A {kind.DisplayName.ToLowerInvariant()} goes on the floor");
                var dresser = level.Items.Find(i => i != null && i.Id == onId);
                if (dresser == null || dresser.TypeId != Furnishings.Dresser) return EditResult.Fail("That isn't a dresser");
                if (!CottageInterior.TryItemBox(dresser, out _, out var top) || !top.Contains(box, tolerance)) return EditResult.Fail("It needs to stand on the dresser");
                if (MathF.Abs(TownMathAngle(dresser.RotationDeg - rotationDeg)) > 0.5f) return EditResult.Fail("It needs to face the same way as the dresser");
                if (level.Items.Exists(i => i != null && i.Id != ignoreId && i.OnId == onId)) return EditResult.Fail("There's a TV on that dresser already");
                return EditResult.Success;
            }

            var faces = CottageInterior.WallFaces(type, level);
            var backed = CottageInterior.TryBackingFace(faces, box, rotationDeg, out var backing, 0.02f + tolerance);
            if (kind.Placement == FurniturePlacement.AgainstWall && !backed) return EditResult.Fail($"A {kind.DisplayName.ToLowerInvariant()} goes against a wall");
            if (kind.Placement == FurniturePlacement.DresserOrWall) return CanHang(type, interior, floor, kind, box, backed, backing, ignoreId, tolerance);

            foreach (var wall in level.Walls)
            {
                if (CottageInterior.TryWallLine(wall, out var line) && box.Overlaps(line.Box, tolerance)) return EditResult.Fail("A wall is in the way");
            }

            foreach (var other in level.Items)
            {
                if (other == null || other.Id == ignoreId || CottageInterior.IsOffTheFloor(other)) continue;
                if (CottageInterior.TryItemBox(other, out _, out var otherBox) && box.Overlaps(otherBox, tolerance)) return EditResult.Fail("Something's in the way");
            }

            if (interior.Stairs != null && box.Overlaps(CottageInterior.StairsKeepOut(interior.Stairs, floor), tolerance)) return EditResult.Fail("The stairs are in the way");

            foreach (var clearance in CottageInterior.DoorClearances(type, interior, floor))
            {
                if (box.Overlaps(clearance, tolerance)) return EditResult.Fail("Keep the doorway clear");
            }

            return EditResult.Success;
        }

        /// <summary>A TV on the wall: flat against it, clear of doors, windows and other TVs.</summary>
        private static EditResult CanHang(BuildingType type, InteriorDto interior, int floor, FurnitureType kind, in Aabb box, bool backed,
            CottageInterior.Face backing, string ignoreId, float tolerance)
        {
            if (!backed) return EditResult.Fail($"A {kind.DisplayName} goes on a dresser or a wall");

            var level = interior.Floors[floor];
            foreach (var opening in CottageInterior.Openings(interior, floor))
            {
                if (opening.WallId != backing.OwnerId || !CottageInterior.TryFindWall(type, level, opening.WallId, out var wall)) continue;
                var half = CottageInterior.OpeningWidth(opening) * 0.5f;
                if (box.Overlaps(wall.Span(opening.At - half, opening.At + half, 0.15f), tolerance)) return EditResult.Fail("There's a door or window there");
            }

            foreach (var wall in level.Walls)
            {
                if (wall.Id != backing.OwnerId && CottageInterior.TryWallLine(wall, out var line) && box.Overlaps(line.Box, tolerance)) return EditResult.Fail("A wall is in the way");
            }

            foreach (var other in level.Items)
            {
                if (other == null || other.Id == ignoreId || other.TypeId != Furnishings.Tv || other.OnId != null) continue;
                if (CottageInterior.TryItemBox(other, out _, out var otherBox) && box.Overlaps(otherBox, tolerance)) return EditResult.Fail("There's a TV there already");
            }

            return EditResult.Success;
        }

        public static EditResult CanMoveStairs(BuildingType type, InteriorDto interior, float x, float z, float rotationDeg,
            float tolerance = TownRules.ClientTolerance)
        {
            if (interior.Floors == null || interior.Floors.Count != CottageInterior.FloorCount) return EditResult.Fail("A cottage has two floors");
            if (!TownRules.IsFinite(x) || !TownRules.IsFinite(z) || !TownRules.IsFinite(rotationDeg)) return EditResult.Fail("Invalid position");
            if (!CottageInterior.IsQuarterTurn(rotationDeg)) return EditResult.Fail("Stairs line up with the walls");

            var stairs = new StairsDto { X = x, Z = z, RotationDeg = rotationDeg };
            var inside = CottageInterior.Inside(type);
            for (var floor = 0; floor < CottageInterior.FloorCount; floor++)
            {
                var keepOut = CottageInterior.StairsKeepOut(stairs, floor);
                if (!inside.Contains(keepOut, tolerance))
                    return EditResult.Fail(inside.Contains(CottageInterior.StairsBox(stairs), tolerance) ? "Leave room to step on and off the stairs" : "The stairs go inside the house");

                var level = interior.Floors[floor];
                foreach (var wall in level.Walls)
                {
                    if (CottageInterior.TryWallLine(wall, out var line) && keepOut.Overlaps(line.Box, tolerance))
                        return EditResult.Fail(floor == 0 ? "A wall is in the way" : "A wall upstairs is in the way");
                }

                foreach (var item in level.Items)
                {
                    if (!CottageInterior.IsOffTheFloor(item) && CottageInterior.TryItemBox(item, out _, out var itemBox) && keepOut.Overlaps(itemBox, tolerance))
                        return EditResult.Fail(floor == 0 ? "Furniture is in the way" : "Furniture upstairs is in the way");
                }

                foreach (var clearance in CottageInterior.DoorClearances(type, interior, floor))
                {
                    if (keepOut.Overlaps(clearance, tolerance)) return EditResult.Fail("Keep the doorway clear");
                }
            }

            return EditResult.Success;
        }

        /// <summary>Server-side check of a saved interior. Every part is checked against the rest, as if it had just been added.</summary>
        public static void Validate(BuildingDto building, ValidationResult result)
        {
            var interior = building.Interior;
            if (interior == null) return;

            var prefix = $"Building {building.Id}";
            if (!CottageInterior.HasInterior(building) || !Catalog.TryGetBuilding(building.TypeId, out var type))
            {
                result.Add($"{prefix} can't have an inside");
                return;
            }

            if (interior.Stairs == null || interior.Floors == null || interior.Floors.Count != CottageInterior.FloorCount)
            {
                result.Add($"{prefix} needs stairs and {CottageInterior.FloorCount} floors");
                return;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal) { CottageInterior.FrontDoorId };
            foreach (var floor in interior.Floors)
            {
                if (floor?.Walls == null || floor.Openings == null || floor.Items == null
                    || floor.Walls.Contains(null) || floor.Openings.Contains(null) || floor.Items.Contains(null))
                {
                    result.Add($"{prefix} has a floor with missing parts");
                    return;
                }

                if (floor.Walls.Count > CottageInterior.MaxWallsPerFloor || floor.Openings.Count > CottageInterior.MaxOpeningsPerFloor || floor.Items.Count > CottageInterior.MaxItemsPerFloor)
                {
                    result.Add($"{prefix} has too much on one floor");
                    return;
                }

                foreach (var id in Ids(floor))
                {
                    if (string.IsNullOrEmpty(id) || !ids.Add(id)) result.Add($"{prefix} has a missing or repeated part id '{id}'");
                }
            }

            const float tolerance = TownRules.ServerTolerance;
            var stairs = CanMoveStairs(type, interior, interior.Stairs.X, interior.Stairs.Z, interior.Stairs.RotationDeg, tolerance);
            if (!stairs.Ok) result.Add($"{prefix} stairs: {stairs.Error}");

            for (var i = 0; i < CottageInterior.FloorCount; i++)
            {
                var floor = interior.Floors[i];
                foreach (var wall in floor.Walls)
                {
                    var check = CanAddWall(type, interior, i, new Vec2(wall.X0, wall.Z0), new Vec2(wall.X1, wall.Z1), wall.Id, tolerance);
                    if (!check.Ok) result.Add($"{prefix} wall {wall.Id}: {check.Error}");
                }

                foreach (var opening in floor.Openings)
                {
                    var check = CanAddOpening(type, interior, i, opening.TypeId, opening.WallId, opening.At, opening.Id, tolerance);
                    if (!check.Ok) result.Add($"{prefix} opening {opening.Id}: {check.Error}");
                }

                foreach (var item in floor.Items)
                {
                    var check = CanPlaceItem(type, interior, i, item.TypeId, item.X, item.Z, item.RotationDeg, item.OnId, item.Id, tolerance);
                    if (!check.Ok) result.Add($"{prefix} item {item.Id}: {check.Error}");
                }
            }
        }

        private static IEnumerable<string> Ids(InteriorFloorDto floor)
        {
            foreach (var wall in floor.Walls) yield return wall.Id;
            foreach (var opening in floor.Openings) yield return opening.Id;
            foreach (var item in floor.Items) yield return item.Id;
        }

        private static EditResult CheckFloor(InteriorDto interior, int floor)
        {
            if (interior?.Floors == null || interior.Floors.Count != CottageInterior.FloorCount) return EditResult.Fail("A cottage has two floors");
            if (floor < 0 || floor >= CottageInterior.FloorCount) return EditResult.Fail("No such floor");
            return interior.Floors[floor] == null ? EditResult.Fail("No such floor") : EditResult.Success;
        }

        private static bool IsDoor(OpeningDto opening) => Furnishings.TryGetOpening(opening.TypeId, out var kind) && kind.IsDoor;

        private static bool Finite(Vec2 v) => TownRules.IsFinite(v.X) && TownRules.IsFinite(v.Z);

        /// <summary>An angle difference wrapped to -180..180.</summary>
        private static float TownMathAngle(float degrees)
        {
            var d = degrees % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }
    }
}

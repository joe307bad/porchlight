using System;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;

namespace Porchlight.Shared.Editing
{
    // Every change to a town goes through a command. Today the browser applies commands locally and
    // saves a whole snapshot. For multiplayer, the same commands can go over the wire and the server
    // can apply them with this exact code. Commands carry ids chosen by the client, so replaying a
    // command gives the same result everywhere.

    public abstract class TownCommand
    {
    }

    public sealed class PlaceBuildingCommand : TownCommand
    {
        public string BuildingId { get; set; } = "";
        public string TypeId { get; set; } = "";
        public float X { get; set; }
        public float Z { get; set; }
        public float RotationDeg { get; set; }
    }

    public sealed class RemoveBuildingCommand : TownCommand
    {
        public string BuildingId { get; set; } = "";
    }

    /// <summary>
    /// Adds a straight road. Each end snaps to an existing node when close enough. Otherwise it creates
    /// a node whose id comes from the segment id (SegmentId + "-a" or "-b").
    /// </summary>
    public sealed class AddRoadCommand : TownCommand
    {
        public string SegmentId { get; set; } = "";
        public string TypeId { get; set; } = "";
        public float StartX { get; set; }
        public float StartZ { get; set; }
        public float EndX { get; set; }
        public float EndZ { get; set; }
    }

    /// <summary>Removes a segment, plus any of its end nodes that no longer join anything.</summary>
    public sealed class RemoveRoadCommand : TownCommand
    {
        public string SegmentId { get; set; } = "";
    }

    /// <summary>
    /// The terrain tool: levels a square pad around a point at the height there plus <see cref="Delta"/>,
    /// sloping the ground around it. See <see cref="TownTerrain.Shape"/>.
    /// </summary>
    public sealed class ShapeTerrainCommand : TownCommand
    {
        public float X { get; set; }
        public float Z { get; set; }
        public float PadHalfSize { get; set; }
        public float Delta { get; set; }
    }

    /// <summary>Changes the inside of a building. <see cref="Floor"/> is 0 for the ground floor.</summary>
    public abstract class InteriorCommand : TownCommand
    {
        public string BuildingId { get; set; } = "";
        public int Floor { get; set; }
    }

    /// <summary>An inside wall from (X0, Z0) to (X1, Z1). See <see cref="InteriorRules.CanAddWall"/>.</summary>
    public sealed class AddInteriorWallCommand : InteriorCommand
    {
        public string WallId { get; set; } = "";
        public float X0 { get; set; }
        public float Z0 { get; set; }
        public float X1 { get; set; }
        public float Z1 { get; set; }
    }

    /// <summary>A door or window in a wall. See <see cref="InteriorRules.CanAddOpening"/>.</summary>
    public sealed class AddOpeningCommand : InteriorCommand
    {
        public string OpeningId { get; set; } = "";
        public string TypeId { get; set; } = "";
        public string WallId { get; set; } = "";
        public float At { get; set; }
    }

    /// <summary>A piece of furniture. See <see cref="InteriorRules.CanPlaceItem"/>.</summary>
    public sealed class PlaceFurnitureCommand : InteriorCommand
    {
        public string ItemId { get; set; } = "";
        public string TypeId { get; set; } = "";
        public float X { get; set; }
        public float Z { get; set; }
        public float RotationDeg { get; set; }
        public string OnId { get; set; }
    }

    /// <summary>Moves the stairs (they span both floors, so <see cref="InteriorCommand.Floor"/> is ignored).</summary>
    public sealed class MoveStairsCommand : InteriorCommand
    {
        public float X { get; set; }
        public float Z { get; set; }
        public float RotationDeg { get; set; }
    }

    /// <summary>
    /// Removes a wall, door, window or piece of furniture from a floor, along with whatever depended
    /// on it: the doors and windows in a wall, or the TV on a dresser.
    /// </summary>
    public sealed class RemoveInteriorPartCommand : InteriorCommand
    {
        public string PartId { get; set; } = "";
    }

    public static class TownEditor
    {
        public static EditResult Apply(TownDto town, TownCommand command)
        {
            switch (command)
            {
                case PlaceBuildingCommand c: return PlaceBuilding(town, c);
                case RemoveBuildingCommand c: return RemoveBuilding(town, c);
                case AddRoadCommand c: return AddRoad(town, c);
                case RemoveRoadCommand c: return RemoveRoad(town, c);
                case ShapeTerrainCommand c: return ShapeTerrain(town, c);
                case InteriorCommand c: return EditInterior(town, c);
                default: return EditResult.Fail($"Unknown command {command?.GetType().Name ?? "null"}");
            }
        }

        private static EditResult PlaceBuilding(TownDto town, PlaceBuildingCommand c)
        {
            if (string.IsNullOrEmpty(c.BuildingId)) return EditResult.Fail("Missing building id");
            if (town.Buildings.Exists(b => b.Id == c.BuildingId)) return EditResult.Fail("Building id already used");

            var x = TownMath.Quantize(c.X);
            var z = TownMath.Quantize(c.Z);
            var rotation = TownMath.NormalizeAngle(c.RotationDeg);

            var check = TownRules.CanPlaceBuilding(town, c.TypeId, x, z, rotation, out var ground);
            if (!check.Ok) return check;

            // On a slope, the ground under the building is levelled first.
            if (ground != null) town.Terrain = ground;

            var building = new BuildingDto { Id = c.BuildingId, TypeId = c.TypeId, X = x, Z = z, RotationDeg = rotation };
            if (CottageInterior.HasInterior(building))
            {
                // Cottages are numbered in the order they're built, and new ones come furnished.
                building.HouseNumber = NextHouseNumber(town);
                if (c.TypeId == CottageInterior.TypeId && Content.Catalog.TryGetBuilding(c.TypeId, out var type))
                    building.Interior = CottageGenerator.Generate(type, c.BuildingId);
            }

            town.Buildings.Add(building);
            return EditResult.Success;
        }

        /// <summary>One more than the highest house number in the town, so numbers aren't reused while it stands.</summary>
        public static int NextHouseNumber(TownDto town)
        {
            var highest = 0;
            foreach (var building in town.Buildings) highest = Math.Max(highest, building.HouseNumber);
            return Math.Min(highest + 1, TownLimits.MaxHouseNumber);
        }

        private static EditResult RemoveBuilding(TownDto town, RemoveBuildingCommand c)
        {
            var index = town.Buildings.FindIndex(b => b.Id == c.BuildingId);
            if (index < 0) return EditResult.Fail("No such building");

            town.Buildings.RemoveAt(index);
            return EditResult.Success;
        }

        private static EditResult AddRoad(TownDto town, AddRoadCommand c)
        {
            if (string.IsNullOrEmpty(c.SegmentId)) return EditResult.Fail("Missing segment id");
            if (town.Roads.Segments.Exists(s => s.Id == c.SegmentId)) return EditResult.Fail("Segment id already used");

            var start = TownMath.Quantize(new Vec2(c.StartX, c.StartZ));
            var end = TownMath.Quantize(new Vec2(c.EndX, c.EndZ));

            var check = TownRules.CanAddRoad(town, c.TypeId, start, end);
            if (!check.Ok) return check;

            var a = TownRules.SnapToNode(town, start);
            var b = TownRules.SnapToNode(town, end);
            var startNodeId = a.NodeId ?? AddNode(town, c.SegmentId + "-a", a.Position);
            var endNodeId = b.NodeId ?? AddNode(town, c.SegmentId + "-b", b.Position);

            town.Roads.Segments.Add(new RoadSegmentDto
            {
                Id = c.SegmentId,
                TypeId = c.TypeId,
                StartNodeId = startNodeId,
                EndNodeId = endNodeId,
            });
            return EditResult.Success;
        }

        private static EditResult RemoveRoad(TownDto town, RemoveRoadCommand c)
        {
            var index = town.Roads.Segments.FindIndex(s => s.Id == c.SegmentId);
            if (index < 0) return EditResult.Fail("No such road");

            var segment = town.Roads.Segments[index];
            town.Roads.Segments.RemoveAt(index);
            RemoveNodeIfOrphaned(town, segment.StartNodeId);
            RemoveNodeIfOrphaned(town, segment.EndNodeId);
            return EditResult.Success;
        }

        private static EditResult ShapeTerrain(TownDto town, ShapeTerrainCommand c)
        {
            var check = TownRules.CanShapeTerrain(town, TownMath.Quantize(new Vec2(c.X, c.Z)), c.PadHalfSize, c.Delta, out var heights);
            if (!check.Ok) return check;

            town.Terrain = new TerrainDto { Cells = TownTerrain.CellsFor(town.Size), Heights = heights };
            return EditResult.Success;
        }

        /// <summary>
        /// Interior edits work on a copy of the inside (the default layout, the first time), which
        /// replaces the building's only if the change is allowed.
        /// </summary>
        private static EditResult EditInterior(TownDto town, InteriorCommand c)
        {
            var building = town.Buildings.Find(b => b.Id == c.BuildingId);
            if (building == null) return EditResult.Fail("No such building");
            if (!CottageInterior.HasInterior(building) || !Content.Catalog.TryGetBuilding(building.TypeId, out var type)) return EditResult.Fail("This building has no inside to edit");

            var interior = CottageInterior.Of(building).Clone();
            if (!(c is MoveStairsCommand) && (c.Floor < 0 || c.Floor >= interior.Floors.Count)) return EditResult.Fail("No such floor");

            EditResult result;
            switch (c)
            {
                case AddInteriorWallCommand w:
                {
                    if (string.IsNullOrEmpty(w.WallId) || HasPart(interior, w.WallId)) return EditResult.Fail("Wall id missing or already used");
                    var a = TownMath.Quantize(new Vec2(w.X0, w.Z0));
                    var b = TownMath.Quantize(new Vec2(w.X1, w.Z1));
                    result = InteriorRules.CanAddWall(type, interior, w.Floor, a, b);
                    if (result.Ok) interior.Floors[w.Floor].Walls.Add(new InteriorWallDto { Id = w.WallId, X0 = a.X, Z0 = a.Z, X1 = b.X, Z1 = b.Z });
                    break;
                }

                case AddOpeningCommand o:
                {
                    if (string.IsNullOrEmpty(o.OpeningId) || HasPart(interior, o.OpeningId)) return EditResult.Fail("Opening id missing or already used");
                    var at = TownMath.Quantize(o.At);
                    result = InteriorRules.CanAddOpening(type, interior, o.Floor, o.TypeId, o.WallId, at);
                    if (result.Ok) interior.Floors[o.Floor].Openings.Add(new OpeningDto { Id = o.OpeningId, TypeId = o.TypeId, WallId = o.WallId, At = at });
                    break;
                }

                case PlaceFurnitureCommand f:
                {
                    if (string.IsNullOrEmpty(f.ItemId) || HasPart(interior, f.ItemId)) return EditResult.Fail("Item id missing or already used");
                    var x = TownMath.Quantize(f.X);
                    var z = TownMath.Quantize(f.Z);
                    var rotation = TownMath.NormalizeAngle(f.RotationDeg);
                    result = InteriorRules.CanPlaceItem(type, interior, f.Floor, f.TypeId, x, z, rotation, f.OnId);
                    if (result.Ok) interior.Floors[f.Floor].Items.Add(new FurnitureDto { Id = f.ItemId, TypeId = f.TypeId, X = x, Z = z, RotationDeg = rotation, OnId = f.OnId });
                    break;
                }

                case MoveStairsCommand m:
                {
                    var x = TownMath.Quantize(m.X);
                    var z = TownMath.Quantize(m.Z);
                    var rotation = TownMath.NormalizeAngle(m.RotationDeg);
                    result = InteriorRules.CanMoveStairs(type, interior, x, z, rotation);
                    if (result.Ok) interior.Stairs = new StairsDto { X = x, Z = z, RotationDeg = rotation };
                    break;
                }

                case RemoveInteriorPartCommand r:
                    result = RemovePart(type, interior.Floors[r.Floor], r.PartId);
                    break;

                default:
                    return EditResult.Fail($"Unknown command {c.GetType().Name}");
            }

            if (result.Ok) building.Interior = interior;
            return result;
        }

        private static EditResult RemovePart(Content.BuildingType type, InteriorFloorDto floor, string id)
        {
            var wall = floor.Walls.Find(w => w.Id == id);
            if (wall != null)
            {
                // Its doors and windows go with it, and so does anything that has to be against a wall
                // and was against this one: counters, and TVs hung on it.
                var faces = CottageInterior.WallFaces(type, floor);
                floor.Walls.Remove(wall);
                floor.Openings.RemoveAll(o => o.WallId == id);
                floor.Items.RemoveAll(i => NeedsWall(i) && CottageInterior.TryItemBox(i, out _, out var box)
                    && CottageInterior.TryBackingFace(faces, box, i.RotationDeg, out var backing) && backing.OwnerId == id);
                return EditResult.Success;
            }

            if (floor.Openings.RemoveAll(o => o.Id == id) > 0) return EditResult.Success;

            if (floor.Items.RemoveAll(i => i.Id == id) > 0)
            {
                floor.Items.RemoveAll(i => i.OnId == id);
                return EditResult.Success;
            }

            return EditResult.Fail(id == CottageInterior.FrontDoorId ? "The front door stays" : "Nothing to remove there");
        }

        private static bool NeedsWall(FurnitureDto item) =>
            Content.Furnishings.TryGetFurniture(item.TypeId, out var kind)
            && (kind.Placement == Content.FurniturePlacement.AgainstWall || (kind.Placement == Content.FurniturePlacement.DresserOrWall && item.OnId == null));

        private static bool HasPart(InteriorDto interior, string id)
        {
            if (id == CottageInterior.FrontDoorId) return true;
            foreach (var floor in interior.Floors)
            {
                if (floor.Walls.Exists(w => w.Id == id) || floor.Openings.Exists(o => o.Id == id) || floor.Items.Exists(i => i.Id == id)) return true;
            }

            return false;
        }

        private static string AddNode(TownDto town, string id, Vec2 position)
        {
            town.Roads.Nodes.Add(new RoadNodeDto { Id = id, X = position.X, Z = position.Z });
            return id;
        }

        private static void RemoveNodeIfOrphaned(TownDto town, string nodeId)
        {
            var used = town.Roads.Segments.Exists(s => s.StartNodeId == nodeId || s.EndNodeId == nodeId);
            if (!used) town.Roads.Nodes.RemoveAll(n => n.Id == nodeId);
        }
    }

    public static class TownMath
    {
        public static float Quantize(float value) =>
            MathF.Round(value / TownLimits.PositionQuantum) * TownLimits.PositionQuantum;

        public static Vec2 Quantize(Vec2 value) => new Vec2(Quantize(value.X), Quantize(value.Z));

        /// <summary>Wraps to [0, 360) and rounds to 0.1 degrees.</summary>
        public static float NormalizeAngle(float degrees)
        {
            var wrapped = degrees % 360f;
            if (wrapped < 0f) wrapped += 360f;
            var rounded = MathF.Round(wrapped * 10f) / 10f;
            return rounded >= 360f ? 0f : rounded;
        }
    }
}

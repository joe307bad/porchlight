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
    /// Paints tiles a surface (street, path, plaza, garden or grass): <see cref="Cells"/> are tile indices
    /// (row by row from -Z). As a <see cref="Line"/>, in order, a street or path keeps each tile within one
    /// level of the last so it can ramp or step. See <see cref="TownTiles.CanPaint"/>.
    /// </summary>
    public sealed class PaintTilesCommand : TownCommand
    {
        public System.Collections.Generic.List<int> Cells { get; set; } = new System.Collections.Generic.List<int>();
        public string SurfaceId { get; set; } = "";
        public bool Line { get; set; }
    }

    /// <summary>
    /// Puts tiles back as they were (the builder's undo): <see cref="Cells"/> are tile indices, and the
    /// other lists give each one's level, surface and hillside mark, in the same order. See
    /// <see cref="TownTiles.CanRestore"/>.
    /// </summary>
    public sealed class RestoreTilesCommand : TownCommand
    {
        public System.Collections.Generic.List<int> Cells { get; set; } = new System.Collections.Generic.List<int>();
        public System.Collections.Generic.List<int> Levels { get; set; } = new System.Collections.Generic.List<int>();
        public System.Collections.Generic.List<int> Surfaces { get; set; } = new System.Collections.Generic.List<int>();
        public System.Collections.Generic.List<bool> Hills { get; set; } = new System.Collections.Generic.List<bool>();
    }

    /// <summary>
    /// The terrain tool: raises or lowers the tiles in a square around a point by <see cref="Delta"/>
    /// levels (rounded to whole metres). See <see cref="TownTiles.CanRaise"/>.
    /// </summary>
    public sealed class ShapeTerrainCommand : TownCommand
    {
        public float X { get; set; }
        public float Z { get; set; }
        public float PadHalfSize { get; set; }
        public float Delta { get; set; }
    }

    /// <summary>Adds a terrain builder shape. Its id must be new; it's rounded to what gets stored (<see cref="TerrainShapes.Normalize"/>).</summary>
    public sealed class AddTerrainShapeCommand : TownCommand
    {
        public TerrainShapeDto Shape { get; set; }
    }

    /// <summary>Moves, turns or resizes a terrain builder shape: replaces the one with the same id.</summary>
    public sealed class UpdateTerrainShapeCommand : TownCommand
    {
        public TerrainShapeDto Shape { get; set; }
    }

    public sealed class RemoveTerrainShapeCommand : TownCommand
    {
        public string ShapeId { get; set; } = "";
    }

    /// <summary>
    /// Makes the terrain builder's overlay the ground: raises the tiles under the shapes with their
    /// overlay on to their tops (see <see cref="TerrainShapes.Overlay"/>), then turns their overlay off,
    /// since it's done. The shapes stay, so they can be changed and overlaid again.
    /// </summary>
    public sealed class ApplyTerrainOverlayCommand : TownCommand
    {
    }

    /// <summary>Sets how heavy the town's foliage is. Each amount is clamped to 0..<see cref="FoliageDto.Max"/>.</summary>
    public sealed class SetFoliageCommand : TownCommand
    {
        public FoliageDto Foliage { get; set; }
    }

    /// <summary>Paints (or erases) an area where foliage grows. Corners can come in any order; it's kept inside the town.</summary>
    public sealed class AddFoliageAreaCommand : TownCommand
    {
        public FoliageAreaDto Area { get; set; }
    }

    /// <summary>Removes every foliage area, so nothing grows.</summary>
    public sealed class ClearFoliageAreasCommand : TownCommand
    {
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
                case PaintTilesCommand c: return PaintTiles(town, c);
                case RestoreTilesCommand c:
                {
                    var check = TownTiles.CanRestore(town, c.Cells, c.Levels, c.Surfaces, c.Hills, out var changes);
                    if (!check.Ok) return check;
                    TownTiles.Apply(town, changes);
                    return EditResult.Success;
                }

                case ShapeTerrainCommand c: return ShapeTerrain(town, c);
                case AddTerrainShapeCommand c: return AddTerrainShape(town, c);
                case UpdateTerrainShapeCommand c: return UpdateTerrainShape(town, c);
                case RemoveTerrainShapeCommand c: return RemoveTerrainShape(town, c);
                case ApplyTerrainOverlayCommand _: return ApplyTerrainOverlay(town);
                case SetFoliageCommand c:
                {
                    if (c.Foliage == null) return EditResult.Fail("Missing foliage");
                    float Amount(float v) => float.IsNaN(v) ? 1f : Math.Clamp(v, 0f, FoliageDto.Max);

                    // Changed in place: views of the town that share its settings (with smoothed roads, say) see it too.
                    var foliage = town.Foliage ??= new FoliageDto();
                    foliage.Overall = Amount(c.Foliage.Overall);
                    foliage.Trees = Amount(c.Foliage.Trees);
                    foliage.Flowers = Amount(c.Foliage.Flowers);
                    foliage.Grass = Amount(c.Foliage.Grass);
                    return EditResult.Success;
                }

                case AddFoliageAreaCommand c:
                {
                    var area = c.Area;
                    if (area == null || float.IsNaN(area.X0 + area.X1 + area.Z0 + area.Z1) || float.IsInfinity(area.X0 + area.X1 + area.Z0 + area.Z1)) return EditResult.Fail("Invalid area");
                    if (town.FoliageAreas == null) town.FoliageAreas = new System.Collections.Generic.List<FoliageAreaDto>();
                    if (town.FoliageAreas.Count >= TownLimits.MaxFoliageAreas) return EditResult.Fail("That's as many areas as a town can have. Clear some first.");
                    var half = town.Size * 0.5f;
                    float Fit(float v) => TownMath.Quantize(Math.Clamp(v, -half, half));
                    var fitted = new FoliageAreaDto
                    {
                        X0 = Fit(Math.Min(area.X0, area.X1)), X1 = Fit(Math.Max(area.X0, area.X1)),
                        Z0 = Fit(Math.Min(area.Z0, area.Z1)), Z1 = Fit(Math.Max(area.Z0, area.Z1)),
                        Erase = area.Erase,
                    };
                    if (fitted.X1 - fitted.X0 < 1f || fitted.Z1 - fitted.Z0 < 1f) return EditResult.Fail("Drag out a bigger area");
                    town.FoliageAreas.Add(fitted);
                    return EditResult.Success;
                }

                case ClearFoliageAreasCommand _:
                    if (town.FoliageAreas == null || town.FoliageAreas.Count == 0) return EditResult.Fail("There are no areas to clear");
                    town.FoliageAreas.Clear();
                    return EditResult.Success;

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

            var check = TownRules.CanPlaceBuilding(town, c.TypeId, x, z, rotation, out var levelling);
            if (!check.Ok) return check;

            // The tiles under the building are levelled first.
            TownTiles.Apply(town, levelling);

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

        private static EditResult PaintTiles(TownDto town, PaintTilesCommand c)
        {
            if (!Content.Catalog.TryGetSurface(c.SurfaceId, out var type)) return EditResult.Fail($"Unknown surface '{c.SurfaceId}'");
            var check = TownTiles.CanPaint(town, c.Cells, type.Surface, c.Line, out var changes);
            if (!check.Ok) return check;
            TownTiles.Apply(town, changes);
            return EditResult.Success;
        }

        private static EditResult ShapeTerrain(TownDto town, ShapeTerrainCommand c)
        {
            var check = TownRules.CanShapeTerrain(town, TownMath.Quantize(new Vec2(c.X, c.Z)), c.PadHalfSize, c.Delta, out var changes);
            if (!check.Ok) return check;

            TownTiles.Apply(town, changes);
            return EditResult.Success;
        }

        private static EditResult AddTerrainShape(TownDto town, AddTerrainShapeCommand c)
        {
            if (c.Shape == null) return EditResult.Fail("Missing shape");
            if (town.TerrainShapes == null) town.TerrainShapes = new System.Collections.Generic.List<TerrainShapeDto>();
            if (town.TerrainShapes.Count >= TownLimits.MaxTerrainShapes) return EditResult.Fail("That's as many shapes as a town can have");
            if (town.TerrainShapes.Exists(s => s?.Id == c.Shape.Id)) return EditResult.Fail("Shape id already used");

            var shape = TerrainShapes.Normalize(c.Shape);
            var check = TerrainShapes.Check(town, shape);
            if (!check.Ok) return check;

            town.TerrainShapes.Add(shape);
            return EditResult.Success;
        }

        private static EditResult UpdateTerrainShape(TownDto town, UpdateTerrainShapeCommand c)
        {
            if (c.Shape == null) return EditResult.Fail("Missing shape");
            var index = town.TerrainShapes?.FindIndex(s => s?.Id == c.Shape.Id) ?? -1;
            if (index < 0) return EditResult.Fail("No such shape");

            var shape = TerrainShapes.Normalize(c.Shape);
            var check = TerrainShapes.Check(town, shape);
            if (!check.Ok) return check;

            town.TerrainShapes[index] = shape;
            return EditResult.Success;
        }

        private static EditResult RemoveTerrainShape(TownDto town, RemoveTerrainShapeCommand c)
        {
            if ((town.TerrainShapes?.RemoveAll(s => s?.Id == c.ShapeId) ?? 0) == 0) return EditResult.Fail("No such shape");
            return EditResult.Success;
        }

        private static EditResult ApplyTerrainOverlay(TownDto town)
        {
            var changes = TerrainShapes.Overlay(town);
            if (changes.Count == 0)
                return EditResult.Fail(town.TerrainShapes == null || !town.TerrainShapes.Exists(t => t != null && t.Overlay) ? "Turn on the overlay for a shape first" : "The ground already covers these shapes");

            TownTiles.Apply(town, changes);
            foreach (var shape in town.TerrainShapes) shape.Overlay = false;
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

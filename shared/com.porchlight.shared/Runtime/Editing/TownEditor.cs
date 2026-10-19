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

            town.Buildings.Add(new BuildingDto { Id = c.BuildingId, TypeId = c.TypeId, X = x, Z = z, RotationDeg = rotation });
            return EditResult.Success;
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

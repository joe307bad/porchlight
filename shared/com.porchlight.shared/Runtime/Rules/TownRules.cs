using System;
using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    public readonly struct EditResult
    {
        private EditResult(bool ok, string error)
        {
            Ok = ok;
            Error = error;
        }

        public bool Ok { get; }
        public string Error { get; }

        public static EditResult Success => new EditResult(true, null);
        public static EditResult Fail(string error) => new EditResult(false, error);
    }

    public sealed class ValidationResult
    {
        private const int MaxErrors = 25;

        public List<string> Errors { get; } = new List<string>();
        public bool IsValid => Errors.Count == 0;

        internal void Add(string error)
        {
            if (Errors.Count < MaxErrors) Errors.Add(error);
        }
    }

    /// <summary>A road segment resolved to world positions.</summary>
    public readonly struct RoadGeometry
    {
        public RoadGeometry(RoadSegmentDto segment, RoadType type, Vec2 a, Vec2 b)
        {
            Segment = segment;
            Type = type;
            A = a;
            B = b;
            Rect = OrientedRect.FromSegment(a, b, type.Width);
        }

        public RoadSegmentDto Segment { get; }
        public RoadType Type { get; }
        public Vec2 A { get; }
        public Vec2 B { get; }
        public OrientedRect Rect { get; }
        public float Length => Vec2.Distance(A, B);
    }

    /// <summary>Where a road end lands after snapping. <see cref="NodeId"/> is null for a new node.</summary>
    public readonly struct RoadEndpoint
    {
        public RoadEndpoint(Vec2 position, string nodeId)
        {
            Position = position;
            NodeId = nodeId;
        }

        public Vec2 Position { get; }
        public string NodeId { get; }
    }

    /// <summary>
    /// The placement rules. Clients call the Can* methods for instant feedback, and the server runs
    /// <see cref="ValidateTown"/> on every save so it never has to trust a client.
    /// </summary>
    public static class TownRules
    {
        /// <summary>
        /// Clients check overlaps strictly, but the server allows a little more slack. MathF trig can
        /// differ by an ulp between CoreCLR and IL2CPP, so a building sitting exactly flush against a road
        /// on the Switch could otherwise fail validation on the server.
        /// </summary>
        public const float ClientTolerance = 0.01f;
        public const float ServerTolerance = 0.05f;

        public static OrientedRect Footprint(BuildingType type, float x, float z, float rotationDeg) =>
            new OrientedRect(new Vec2(x, z), type.Width * 0.5f, type.Depth * 0.5f, rotationDeg);

        public static bool TryFootprint(BuildingDto building, out OrientedRect footprint)
        {
            if (Catalog.TryGetBuilding(building.TypeId, out var type))
            {
                footprint = Footprint(type, building.X, building.Z, building.RotationDeg);
                return true;
            }

            footprint = default;
            return false;
        }

        public static Dictionary<string, RoadNodeDto> NodesById(TownDto town)
        {
            var map = new Dictionary<string, RoadNodeDto>(StringComparer.Ordinal);
            foreach (var node in town.Roads.Nodes)
            {
                if (node?.Id != null) map[node.Id] = node;
            }

            return map;
        }

        /// <summary>Resolves every valid segment to positions. Segments with missing nodes or unknown types are skipped.</summary>
        public static List<RoadGeometry> Roads(TownDto town)
        {
            var nodes = NodesById(town);
            var result = new List<RoadGeometry>(town.Roads.Segments.Count);
            foreach (var segment in town.Roads.Segments)
            {
                if (segment == null
                    || !Catalog.TryGetRoad(segment.TypeId, out var type)
                    || !nodes.TryGetValue(segment.StartNodeId ?? "", out var a)
                    || !nodes.TryGetValue(segment.EndNodeId ?? "", out var b))
                {
                    continue;
                }

                result.Add(new RoadGeometry(segment, type, new Vec2(a.X, a.Z), new Vec2(b.X, b.Z)));
            }

            return result;
        }

        public static EditResult CanPlaceBuilding(
            TownDto town, string typeId, float x, float z, float rotationDeg,
            string ignoreBuildingId = null, float tolerance = ClientTolerance)
        {
            if (!Catalog.TryGetBuilding(typeId, out var type)) return EditResult.Fail($"Unknown building type '{typeId}'");
            if (!IsFinite(x) || !IsFinite(z) || !IsFinite(rotationDeg)) return EditResult.Fail("Invalid position");
            if (ignoreBuildingId == null && town.Buildings.Count >= TownLimits.MaxBuildings) return EditResult.Fail("Town is full");

            var footprint = Footprint(type, x, z, rotationDeg);
            if (!footprint.FitsInSquare(town.Size * 0.5f)) return EditResult.Fail("Outside the town boundary");

            foreach (var other in town.Buildings)
            {
                if (other.Id == ignoreBuildingId || !TryFootprint(other, out var otherFootprint)) continue;
                if (footprint.Overlaps(otherFootprint, tolerance)) return EditResult.Fail("Overlaps another building");
            }

            foreach (var road in Roads(town))
            {
                if (footprint.Overlaps(road.Rect, tolerance)) return EditResult.Fail("Overlaps a road");
            }

            if (!TownTerrain.IsLevel(town, footprint)) return EditResult.Fail("The ground here isn't level");

            return EditResult.Success;
        }

        /// <summary>
        /// Checks a terrain tool click (see <see cref="TownTerrain.Shape"/>) and hands back the heights it
        /// would leave. Buildings ride up and down with the ground, but none may be left on a slope.
        /// </summary>
        public static EditResult CanShapeTerrain(TownDto town, Vec2 center, float padHalfSize, float delta, out List<int> heights)
        {
            heights = null;
            if (!IsFinite(center.X) || !IsFinite(center.Z)) return EditResult.Fail("Invalid position");
            if (!InSquare(center, town.Size * 0.5f)) return EditResult.Fail("Outside the town boundary");
            if (!(padHalfSize >= 0f && padHalfSize <= TownTerrain.MaxPadHalfSize)) return EditResult.Fail("Invalid pad size");
            if (!(MathF.Abs(delta) <= TownTerrain.MaxHeight - TownTerrain.MinHeight)) return EditResult.Fail("Invalid height change");

            var shaped = TownTerrain.Shape(town, center, padHalfSize, delta);

            // Only buildings near ground that moved need checking.
            var cells = TownTerrain.CellsFor(town.Size);
            var spacing = town.Size / cells;
            var half = town.Size * 0.5f;
            int i0 = int.MaxValue, i1 = -1, j0 = int.MaxValue, j1 = -1;
            for (var j = 0; j <= cells; j++)
            {
                for (var i = 0; i <= cells; i++)
                {
                    var before = (int)MathF.Round(TownTerrain.HeightAt(town, i, j) * 100f);
                    if (shaped[j * (cells + 1) + i] == before) continue;
                    i0 = Math.Min(i0, i);
                    i1 = Math.Max(i1, i);
                    j0 = Math.Min(j0, j);
                    j1 = Math.Max(j1, j);
                }
            }

            if (i1 < 0) return EditResult.Fail(delta > 0f ? "The ground can't go any higher here" : "The ground can't go any lower here");

            // Ground moves up to a cell beyond the last lattice point that changed.
            var min = new Vec2(-half + (i0 - 1) * spacing, -half + (j0 - 1) * spacing);
            var max = new Vec2(-half + (i1 + 1) * spacing, -half + (j1 + 1) * spacing);
            var after = new TownDto { Size = town.Size, Terrain = new TerrainDto { Cells = cells, Heights = shaped } };
            foreach (var building in town.Buildings)
            {
                if (!TryFootprint(building, out var footprint)) continue;
                var reachX = footprint.ExtentAlong(new Vec2(1f, 0f));
                var reachZ = footprint.ExtentAlong(new Vec2(0f, 1f));
                if (footprint.Center.X + reachX < min.X || footprint.Center.X - reachX > max.X
                    || footprint.Center.Z + reachZ < min.Z || footprint.Center.Z - reachZ > max.Z) continue;
                if (!TownTerrain.IsLevel(after, footprint)) return EditResult.Fail("That would leave a building on a slope");
            }

            heights = shaped;
            return EditResult.Success;
        }

        /// <summary>Snaps <paramref name="point"/> to the nearest road node within <see cref="TownLimits.NodeSnapRadius"/>.</summary>
        public static RoadEndpoint SnapToNode(TownDto town, Vec2 point)
        {
            RoadNodeDto best = null;
            var bestDistance = TownLimits.NodeSnapRadius;
            foreach (var node in town.Roads.Nodes)
            {
                var distance = Vec2.Distance(point, new Vec2(node.X, node.Z));
                if (distance <= bestDistance)
                {
                    best = node;
                    bestDistance = distance;
                }
            }

            return best == null ? new RoadEndpoint(point, null) : new RoadEndpoint(new Vec2(best.X, best.Z), best.Id);
        }

        public static EditResult CanAddRoad(TownDto town, string typeId, Vec2 start, Vec2 end, float tolerance = ClientTolerance)
        {
            if (!Catalog.TryGetRoad(typeId, out var type)) return EditResult.Fail($"Unknown road type '{typeId}'");
            if (!IsFinite(start.X) || !IsFinite(start.Z) || !IsFinite(end.X) || !IsFinite(end.Z)) return EditResult.Fail("Invalid position");
            if (town.Roads.Segments.Count >= TownLimits.MaxRoadSegments) return EditResult.Fail("Too many roads");

            var a = SnapToNode(town, start);
            var b = SnapToNode(town, end);
            if (a.NodeId != null && a.NodeId == b.NodeId) return EditResult.Fail("Road is too short");
            if (Vec2.Distance(a.Position, b.Position) < TownLimits.MinRoadLength) return EditResult.Fail("Road is too short");

            var half = town.Size * 0.5f;
            if (!InSquare(a.Position, half) || !InSquare(b.Position, half)) return EditResult.Fail("Outside the town boundary");

            if (a.NodeId != null && b.NodeId != null)
            {
                foreach (var segment in town.Roads.Segments)
                {
                    if ((segment.StartNodeId == a.NodeId && segment.EndNodeId == b.NodeId)
                        || (segment.StartNodeId == b.NodeId && segment.EndNodeId == a.NodeId))
                    {
                        return EditResult.Fail("That road already exists");
                    }
                }
            }

            var rect = OrientedRect.FromSegment(a.Position, b.Position, type.Width);
            foreach (var building in town.Buildings)
            {
                if (TryFootprint(building, out var footprint) && rect.Overlaps(footprint, tolerance))
                {
                    return EditResult.Fail("Road would cut through a building");
                }
            }

            return EditResult.Success;
        }

        /// <summary>
        /// Full validation of a town received from a client. This is O(n²) on buildings, which is fine at
        /// prototype scale. Switch to a spatial hash before raising <see cref="TownLimits.MaxBuildings"/>.
        /// </summary>
        public static ValidationResult ValidateTown(TownDto town)
        {
            var result = new ValidationResult();
            if (town == null)
            {
                result.Add("Town is missing");
                return result;
            }

            if (string.IsNullOrWhiteSpace(town.Name) || town.Name.Length > TownLimits.MaxNameLength)
                result.Add($"Name must be 1-{TownLimits.MaxNameLength} characters");
            if (!(town.Size >= TownLimits.MinSize && town.Size <= TownLimits.MaxSize))
                result.Add($"Size must be between {TownLimits.MinSize} and {TownLimits.MaxSize}");
            if (town.SchemaVersion != TownDto.CurrentSchemaVersion)
                result.Add($"Unsupported schema version {town.SchemaVersion}");
            if (town.Buildings == null || town.Roads?.Nodes == null || town.Roads.Segments == null)
            {
                result.Add("Buildings and roads are required");
                return result;
            }

            if (town.Buildings.Count > TownLimits.MaxBuildings) result.Add("Too many buildings");
            if (town.Roads.Segments.Count > TownLimits.MaxRoadSegments) result.Add("Too many road segments");
            if (town.Roads.Nodes.Count > TownLimits.MaxRoadNodes) result.Add("Too many road nodes");
            if (!result.IsValid) return result;

            var half = town.Size * 0.5f;
            var ids = new HashSet<string>(StringComparer.Ordinal);

            var footprints = new List<(BuildingDto Building, OrientedRect Footprint)>(town.Buildings.Count);
            foreach (var building in town.Buildings)
            {
                if (building == null) { result.Add("Null building"); continue; }
                if (string.IsNullOrEmpty(building.Id) || !ids.Add(building.Id)) { result.Add($"Building id '{building.Id}' is missing or duplicated"); continue; }
                if (!IsFinite(building.X) || !IsFinite(building.Z) || !IsFinite(building.RotationDeg)) { result.Add($"Building {building.Id} has an invalid position"); continue; }
                if (!TryFootprint(building, out var footprint)) { result.Add($"Building {building.Id} has unknown type '{building.TypeId}'"); continue; }
                if (!footprint.FitsInSquare(half, ServerTolerance)) result.Add($"Building {building.Id} is outside the town");
                footprints.Add((building, footprint));
            }

            for (var i = 0; i < footprints.Count; i++)
            {
                for (var j = i + 1; j < footprints.Count; j++)
                {
                    if (footprints[i].Footprint.Overlaps(footprints[j].Footprint, ServerTolerance))
                        result.Add($"Buildings {footprints[i].Building.Id} and {footprints[j].Building.Id} overlap");
                }
            }

            foreach (var node in town.Roads.Nodes)
            {
                if (node == null) { result.Add("Null road node"); continue; }
                if (string.IsNullOrEmpty(node.Id) || !ids.Add(node.Id)) { result.Add($"Road node id '{node.Id}' is missing or duplicated"); continue; }
                if (!IsFinite(node.X) || !IsFinite(node.Z) || !InSquare(new Vec2(node.X, node.Z), half + ServerTolerance))
                    result.Add($"Road node {node.Id} is outside the town");
            }

            var nodes = NodesById(town);
            foreach (var segment in town.Roads.Segments)
            {
                if (segment == null) { result.Add("Null road segment"); continue; }
                if (string.IsNullOrEmpty(segment.Id) || !ids.Add(segment.Id)) { result.Add($"Road segment id '{segment.Id}' is missing or duplicated"); continue; }
                if (!Catalog.TryGetRoad(segment.TypeId, out _)) result.Add($"Road segment {segment.Id} has unknown type '{segment.TypeId}'");
                if (segment.StartNodeId == segment.EndNodeId) result.Add($"Road segment {segment.Id} starts and ends at the same node");
                if (!nodes.TryGetValue(segment.StartNodeId ?? "", out var a) || !nodes.TryGetValue(segment.EndNodeId ?? "", out var b))
                {
                    result.Add($"Road segment {segment.Id} references a missing node");
                    continue;
                }

                if (Vec2.Distance(new Vec2(a.X, a.Z), new Vec2(b.X, b.Z)) < TownLimits.MinRoadLength - 0.1f)
                    result.Add($"Road segment {segment.Id} is too short");
            }

            foreach (var road in Roads(town))
            {
                foreach (var (building, footprint) in footprints)
                {
                    if (footprint.Overlaps(road.Rect, ServerTolerance))
                        result.Add($"Building {building.Id} overlaps road {road.Segment.Id}");
                }
            }

            if (TownTerrain.Validate(town, result))
            {
                foreach (var (building, footprint) in footprints)
                {
                    if (!TownTerrain.IsLevel(town, footprint, TownTerrain.LevelTolerance + ServerTolerance))
                        result.Add($"Building {building.Id} is on a slope");
                }
            }

            return result;
        }

        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool InSquare(Vec2 point, float half) => MathF.Abs(point.X) <= half && MathF.Abs(point.Z) <= half;
    }
}

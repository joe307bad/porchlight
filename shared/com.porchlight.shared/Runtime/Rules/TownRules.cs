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

    /// <summary>
    /// The placement rules. Clients call the Can* methods for instant feedback, and the server runs
    /// <see cref="ValidateTown"/> on every save so it never has to trust a client.
    /// </summary>
    public static class TownRules
    {
        /// <summary>
        /// Clients check overlaps strictly, but the server allows a little more slack. MathF trig can
        /// differ by an ulp between CoreCLR and IL2CPP, so a building sitting exactly flush against another
        /// on the Switch could otherwise fail validation on the server.
        /// </summary>
        public const float ClientTolerance = 0.01f;
        public const float ServerTolerance = 0.05f;

        /// <summary>The most levels one click of the terrain tool may raise or lower the ground by.</summary>
        public const int MaxShapeLevels = 10;

        /// <summary>The largest square the terrain tool raises or lowers, as half its side.</summary>
        public const float MaxPadHalfSize = 32f;

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

        public static EditResult CanPlaceBuilding(
            TownDto town, string typeId, float x, float z, float rotationDeg,
            string ignoreBuildingId = null, float tolerance = ClientTolerance) =>
            CanPlaceBuilding(town, typeId, x, z, rotationDeg, out _, ignoreBuildingId, tolerance);

        /// <summary>
        /// Checks a building placement. Buildings stand on grass, gardens or plazas, never streets or
        /// paths; the tiles under one are levelled to the tile under its middle, which
        /// <paramref name="levelling"/> lists (empty when they're level already).
        /// </summary>
        public static EditResult CanPlaceBuilding(
            TownDto town, string typeId, float x, float z, float rotationDeg, out List<TileChange> levelling,
            string ignoreBuildingId = null, float tolerance = ClientTolerance)
        {
            levelling = new List<TileChange>();
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

            return TownTiles.CanLevelFor(town, footprint, ignoreBuildingId, out levelling);
        }

        /// <summary>
        /// Checks a terrain tool click: the tiles in a square raised or lowered a whole number of levels
        /// (see <see cref="TownTiles.CanRaise"/>).
        /// </summary>
        public static EditResult CanShapeTerrain(TownDto town, Vec2 center, float padHalfSize, float delta, out List<TileChange> changes)
        {
            changes = null;
            if (!IsFinite(center.X) || !IsFinite(center.Z)) return EditResult.Fail("Invalid position");
            if (!InSquare(center, town.Size * 0.5f)) return EditResult.Fail("Outside the town boundary");
            if (!(padHalfSize >= 0f && padHalfSize <= MaxPadHalfSize)) return EditResult.Fail("Invalid pad size");
            if (!(MathF.Abs(delta) <= MaxShapeLevels)) return EditResult.Fail("Invalid height change");
            return TownTiles.CanRaise(town, center, padHalfSize, (int)MathF.Round(delta), out changes);
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
            if (town.Buildings == null)
            {
                result.Add("Buildings are required");
                return result;
            }

            if (town.Buildings.Count > TownLimits.MaxBuildings) result.Add("Too many buildings");
            if (!result.IsValid) return result;

            TownTiles.Validate(town, result);
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
                if (building.HouseNumber < 0 || building.HouseNumber > TownLimits.MaxHouseNumber || (building.HouseNumber != 0 && !CottageInterior.HasInterior(building)))
                    result.Add($"Building {building.Id} has an invalid house number");
                InteriorRules.Validate(building, result);
                footprints.Add((building, footprint));

                // On level tiles, none of them street or path.
                var level = TownTiles.BaseLevel(town, footprint);
                foreach (var (i, j) in TownTiles.Under(town, footprint))
                {
                    var surface = TownTiles.Surface(town, i, j);
                    if (TownTiles.Level(town, i, j) != level || TownTiles.IsHill(town, i, j)) { result.Add($"Building {building.Id} isn't on level ground"); break; }
                    if (surface == TileSurface.Street || surface == TileSurface.Path) { result.Add($"Building {building.Id} stands on a street or path"); break; }
                }
            }

            for (var i = 0; i < footprints.Count; i++)
            {
                for (var j = i + 1; j < footprints.Count; j++)
                {
                    if (footprints[i].Footprint.Overlaps(footprints[j].Footprint, ServerTolerance))
                        result.Add($"Buildings {footprints[i].Building.Id} and {footprints[j].Building.Id} overlap");
                }
            }

            TerrainShapes.Validate(town, ids, result);
            if (town.FoliageAreas != null)
            {
                if (town.FoliageAreas.Count > TownLimits.MaxFoliageAreas) result.Add("Too many foliage areas");
                foreach (var area in town.FoliageAreas)
                {
                    if (area == null || !IsFinite(area.X0) || !IsFinite(area.X1) || !IsFinite(area.Z0) || !IsFinite(area.Z1) || area.X1 < area.X0 || area.Z1 < area.Z0)
                    {
                        result.Add("Invalid foliage area");
                        break;
                    }
                }
            }

            if (town.Foliage != null)
            {
                foreach (var amount in new[] { town.Foliage.Overall, town.Foliage.Trees, town.Foliage.Flowers, town.Foliage.Grass })
                {
                    if (!(amount >= 0f && amount <= FoliageDto.Max)) result.Add($"Foliage amounts must be between 0 and {FoliageDto.Max}");
                }
            }

            return result;
        }

        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool InSquare(Vec2 point, float half) => MathF.Abs(point.X) <= half && MathF.Abs(point.Z) <= half;
    }
}

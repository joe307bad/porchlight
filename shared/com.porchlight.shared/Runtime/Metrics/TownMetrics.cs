using System;
using Porchlight.Shared.Content;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;

namespace Porchlight.Shared.Metrics
{
    /// <summary>
    /// Town stats. The client computes them for its HUD. The server computes them again on save and
    /// stores them as columns so the town list doesn't have to load layouts.
    /// </summary>
    public sealed class TownMetrics
    {
        /// <summary>Residents in buildings that reach a street, path or plaza.</summary>
        public int Population { get; set; }

        /// <summary>Jobs in buildings that reach a street, path or plaza.</summary>
        public int Jobs { get; set; }

        public int BuildingCount { get; set; }
        public int ConnectedBuildingCount { get; set; }

        /// <summary>Metres of street and path: a tile's length for each tile of them.</summary>
        public float RoadLengthMeters { get; set; }

        public static TownMetrics Compute(TownDto town)
        {
            var metrics = new TownMetrics();
            if (!TownTiles.Has(town))
            {
                metrics.BuildingCount = town.Buildings.Count;
                return metrics;
            }

            var spacing = TownTiles.Spacing(town);
            foreach (var surface in town.Tiles.Surfaces)
            {
                if (surface == (int)TileSurface.Street || surface == (int)TileSurface.Path) metrics.RoadLengthMeters += spacing;
            }

            foreach (var building in town.Buildings)
            {
                metrics.BuildingCount++;
                if (!Catalog.TryGetBuilding(building.TypeId, out var type) || !TownRules.TryFootprint(building, out var footprint)) continue;

                // Connected when a paved tile comes within reach of its walls.
                var grown = new Geometry.OrientedRect(footprint.Center, footprint.HalfWidth + TownLimits.RoadConnectionSlack, footprint.HalfDepth + TownLimits.RoadConnectionSlack, footprint.RotationDeg);
                foreach (var (i, j) in TownTiles.Under(town, grown))
                {
                    if (!TownTiles.IsPaved(TownTiles.Surface(town, i, j))) continue;
                    metrics.ConnectedBuildingCount++;
                    metrics.Population += type.Population;
                    metrics.Jobs += type.Jobs;
                    break;
                }
            }

            return metrics;
        }
    }
}

using System;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
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
        /// <summary>Residents in buildings that reach a road.</summary>
        public int Population { get; set; }

        /// <summary>Jobs in buildings that reach a road.</summary>
        public int Jobs { get; set; }

        public int BuildingCount { get; set; }
        public int ConnectedBuildingCount { get; set; }
        public float RoadLengthMeters { get; set; }

        public static TownMetrics Compute(TownDto town)
        {
            var metrics = new TownMetrics();
            var roads = TownRules.Roads(town);

            foreach (var road in roads)
            {
                metrics.RoadLengthMeters += road.Length;
            }

            foreach (var building in town.Buildings)
            {
                metrics.BuildingCount++;
                if (!Catalog.TryGetBuilding(building.TypeId, out var type)) continue;

                var center = new Vec2(building.X, building.Z);
                var reach = MathF.Max(type.Width, type.Depth) * 0.5f + TownLimits.RoadConnectionSlack;
                foreach (var road in roads)
                {
                    if (Segments.Distance(center, road.A, road.B) <= reach + road.Type.Width * 0.5f)
                    {
                        metrics.ConnectedBuildingCount++;
                        metrics.Population += type.Population;
                        metrics.Jobs += type.Jobs;
                        break;
                    }
                }
            }

            return metrics;
        }
    }
}

using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>Lookups used by builder tools (hover, remove, snap). These never change the town.</summary>
    public static class TownQueries
    {
        public static BuildingDto FindBuildingAt(TownDto town, Vec2 point)
        {
            // Search back to front so the most recently placed building wins.
            for (var i = town.Buildings.Count - 1; i >= 0; i--)
            {
                var building = town.Buildings[i];
                if (TownRules.TryFootprint(building, out var footprint) && footprint.Contains(point)) return building;
            }

            return null;
        }

        public static RoadGeometry? FindRoadAt(TownDto town, Vec2 point)
        {
            RoadGeometry? best = null;
            var bestDistance = float.MaxValue;
            foreach (var road in TownRules.Roads(town))
            {
                var distance = Segments.Distance(point, road.A, road.B);
                if (distance <= road.Type.Width * 0.5f && distance < bestDistance)
                {
                    best = road;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>
        /// When the cursor is near a road, places the building beside it with its front facing the road.
        /// The building lands on whichever side of the road the cursor is on.
        /// </summary>
        public static bool TrySnapToRoad(TownDto town, BuildingType type, Vec2 cursor, out Vec2 position, out float rotationDeg)
        {
            position = cursor;
            rotationDeg = 0f;

            RoadGeometry? best = null;
            var bestDistance = float.MaxValue;
            var bestClosest = Vec2.Zero;
            foreach (var road in TownRules.Roads(town))
            {
                var closest = Segments.ClosestPoint(cursor, road.A, road.B);
                var distance = Vec2.Distance(cursor, closest);
                var reach = road.Type.Width * 0.5f + type.Depth + 2f;
                if (distance <= reach && distance < bestDistance)
                {
                    best = road;
                    bestDistance = distance;
                    bestClosest = closest;
                }
            }

            if (best == null) return false;

            var road0 = best.Value;
            var direction = (road0.B - road0.A).Normalized;
            var normal = new Vec2(direction.Z, -direction.X);
            if (Vec2.Dot(cursor - bestClosest, normal) < 0f) normal = -normal;

            position = bestClosest + normal * (road0.Type.Width * 0.5f + type.Depth * 0.5f + 0.25f);
            rotationDeg = Vec2.HeadingOf(-normal);
            return true;
        }
    }
}

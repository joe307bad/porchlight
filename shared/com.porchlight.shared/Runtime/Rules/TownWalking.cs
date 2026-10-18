using System;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// Where a character on foot can go. Buildings are solid; anything no taller than
    /// <see cref="StepHeight"/> (parks) can be walked onto. Kept in the shared library so the server
    /// can check player movement once towns are multiplayer.
    /// </summary>
    public static class TownWalking
    {
        public const float StepHeight = 0.5f;
        public const float CharacterRadius = 0.3f;

        /// <summary>
        /// Moves from <paramref name="from"/> by <paramref name="delta"/>, sliding along buildings
        /// and staying inside the town.
        /// </summary>
        public static Vec2 Move(TownDto town, Vec2 from, Vec2 delta, float radius = CharacterRadius)
        {
            var p = from + delta;

            // A few passes so a corner between two buildings settles.
            for (var pass = 0; pass < 3; pass++)
            {
                var pushed = false;
                foreach (var building in town.Buildings)
                {
                    if (IsSolid(building, out var footprint) && PushOut(footprint, ref p, radius)) pushed = true;
                }

                if (!pushed) break;
            }

            var half = town.Size * 0.5f - radius;
            return new Vec2(Math.Clamp(p.X, -half, half), Math.Clamp(p.Z, -half, half));
        }

        /// <summary>Height of the ground at a point: the top of a park, or 0.</summary>
        public static float GroundHeight(TownDto town, Vec2 point)
        {
            var height = 0f;
            foreach (var building in town.Buildings)
            {
                if (!Catalog.TryGetBuilding(building.TypeId, out var type) || type.Height > StepHeight) continue;
                if (TownRules.Footprint(type, building.X, building.Z, building.RotationDeg).Contains(point)) height = Math.Max(height, type.Height);
            }

            return height;
        }

        /// <summary>True when a point is inside a building, for keeping cameras out of walls.</summary>
        public static bool IsInsideBuilding(TownDto town, Vec2 point, float y)
        {
            foreach (var building in town.Buildings)
            {
                if (!IsSolid(building, out var footprint) || !footprint.Contains(point)) continue;
                Catalog.TryGetBuilding(building.TypeId, out var type);

                // Gabled roofs rise above the walls, up to about 0.8 of the half-depth.
                var roof = type.Roof == RoofStyle.Gabled ? Math.Min(type.Width, type.Depth) * 0.4f : 0f;
                if (y <= type.Height + roof) return true;
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

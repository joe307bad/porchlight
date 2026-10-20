using System;
using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;

namespace Porchlight.Shared.Rules
{
    /// <summary>
    /// Where things go when the pointer is near: the interior editor's snapping, kept in the shared
    /// library with the rules so it's tested alongside them. Walls snap to wall ends, to other walls
    /// and to the outside walls; doors and windows snap into walls; furniture backs onto walls, lines
    /// up with the furniture and walls around it, and otherwise sits on the <see cref="CottageInterior.Grid"/>.
    /// Snapping only says where something would go; <see cref="InteriorRules"/> says whether it may.
    /// </summary>
    public static class InteriorSnap
    {
        /// <summary>How close, in metres, the pointer has to be for something to snap.</summary>
        public const float Reach = 0.35f;

        public static float ToGrid(float value) => MathF.Round(value / CottageInterior.Grid) * CottageInterior.Grid;

        /// <summary>
        /// One end of a wall. With no <paramref name="start"/> yet, it lands on a wall end, on another
        /// wall, or on the grid. After that it stays in line with the start, along or across the house,
        /// and stops on a crossing wall or in line with a wall end when one is near.
        /// </summary>
        public static Vec2 WallPoint(BuildingType type, InteriorDto interior, int floor, Vec2 cursor, Vec2? start)
        {
            var level = interior.Floors[floor];
            var inside = CottageInterior.Inside(type);
            var lines = new List<CottageInterior.WallLine>();
            foreach (var wall in level.Walls)
            {
                if (CottageInterior.TryWallLine(wall, out var line)) lines.Add(line);
            }

            if (start == null)
            {
                // A wall end close by wins.
                Vec2? best = null;
                var bestDistance = Reach;
                foreach (var line in lines)
                {
                    foreach (var end in new[] { line.Point(line.From, line.Fixed), line.Point(line.To, line.Fixed) })
                    {
                        var distance = Vec2.Distance(end, cursor);
                        if (distance > bestDistance) continue;
                        best = end;
                        bestDistance = distance;
                    }
                }

                if (best.HasValue) return best.Value;

                // Then a point on a wall (along it, on the grid), then the outside walls, then the grid.
                foreach (var line in lines)
                {
                    var across = line.AcrossOf(cursor) - line.Fixed;
                    var along = line.AlongOf(cursor);
                    if (MathF.Abs(across) > Reach || along < line.From - 0.05f || along > line.To + 0.05f) continue;
                    return Clamp(line.Point(Math.Clamp(ToGrid(along), line.From, line.To), line.Fixed), inside);
                }

                var x = NearAny(cursor.X, inside.MinX, inside.MaxX) ? SnapTo(cursor.X, Reach, inside.MinX, inside.MaxX) : ToGrid(cursor.X);
                var z = NearAny(cursor.Z, inside.MinZ, inside.MaxZ) ? SnapTo(cursor.Z, Reach, inside.MinZ, inside.MaxZ) : ToGrid(cursor.Z);
                return Clamp(new Vec2(x, z), inside);
            }

            // Along or across the house, whichever the pointer is further along.
            var from = start.Value;
            var d = cursor - from;
            var alongX = MathF.Abs(d.X) >= MathF.Abs(d.Z);
            var fixedAt = alongX ? from.Z : from.X;
            var target = alongX ? cursor.X : cursor.Z;

            var stops = new List<float> { alongX ? inside.MinX : inside.MinZ, alongX ? inside.MaxX : inside.MaxZ };
            foreach (var line in lines)
            {
                // Crossing walls, and the ends of walls (so parallel walls line up).
                if (line.AlongX != alongX && fixedAt >= line.From - 0.05f && fixedAt <= line.To + 0.05f) stops.Add(line.Fixed);
                if (line.AlongX == alongX)
                {
                    stops.Add(line.From);
                    stops.Add(line.To);
                }
            }

            var snapped = ToGrid(target);
            var nearest = Reach;
            foreach (var stop in stops)
            {
                var distance = MathF.Abs(stop - target);
                if (distance > nearest) continue;
                snapped = stop;
                nearest = distance;
            }

            var end2 = alongX ? new Vec2(snapped, fixedAt) : new Vec2(fixedAt, snapped);
            return Clamp(end2, inside);
        }

        /// <summary>
        /// The wall a door or window under the pointer would go in, and where along it: on the grid,
        /// or in the middle of the wall when it's close.
        /// </summary>
        public static bool Opening(BuildingType type, InteriorDto interior, int floor, string typeId, Vec2 cursor, out string wallId, out float at)
        {
            wallId = null;
            at = 0f;
            if (!Furnishings.TryGetOpening(typeId, out var kind)) return false;

            var bestDistance = 0.6f;
            CottageInterior.WallLine? best = null;
            foreach (var line in CottageInterior.Walls(type, interior.Floors[floor]))
            {
                var across = MathF.Abs(line.AcrossOf(cursor) - line.Fixed);
                var along = line.AlongOf(cursor);
                if (across > bestDistance || along < line.From - 0.2f || along > line.To + 0.2f) continue;
                best = line;
                bestDistance = across;
            }

            if (best == null) return false;

            var wall = best.Value;
            var half = kind.Width * 0.5f;
            var margin = wall.Outside ? 0.15f : 0.1f;
            var middle = (wall.From + wall.To) * 0.5f;
            var position = wall.AlongOf(cursor);
            at = MathF.Abs(position - middle) < Reach ? middle : ToGrid(position);

            float lo = wall.From + half + margin, hi = wall.To - half - margin;
            if (lo <= hi) at = Math.Clamp(at, lo, hi);
            wallId = wall.Id;
            return true;
        }

        /// <summary>
        /// Where a piece of furniture under the pointer would go, which way it faces, and what it stands
        /// on (a TV on a dresser). Things that like walls turn round and back onto one that's near;
        /// everything lines up with the walls and furniture around it, or sits on the grid.
        /// </summary>
        public static (Vec2 Position, float RotationDeg, string OnId) Item(BuildingType type, InteriorDto interior, int floor, FurnitureType kind,
            Vec2 cursor, float rotationDeg)
        {
            var level = interior.Floors[floor];
            var inside = CottageInterior.Inside(type);

            if (kind.Placement == FurniturePlacement.DresserOrWall)
            {
                foreach (var item in level.Items)
                {
                    if (item.TypeId != Furnishings.Dresser || !CottageInterior.TryItemBox(item, out var dresser, out var top) || !top.Contains(cursor)) continue;

                    // Towards the back of the top.
                    var back = (dresser.Depth - kind.Depth) * 0.5f - 0.05f;
                    var position = new Vec2(item.X, item.Z) - CottageInterior.Facing(item.RotationDeg) * back;
                    return (position, item.RotationDeg, item.Id);
                }
            }

            var walls = CottageInterior.WallFaces(type, level);
            var lines = new List<CottageInterior.Face>(walls);
            foreach (var item in level.Items)
            {
                if (!CottageInterior.IsOffTheFloor(item) && CottageInterior.TryItemBox(item, out _, out var box)) CottageInterior.AddBoxFaces(lines, box, item.Id);
            }

            if (interior.Stairs != null && floor == 0) CottageInterior.AddBoxFaces(lines, CottageInterior.StairsBox(interior.Stairs), "stairs");

            if (kind.SnapsToWalls && TryNearestWall(walls, cursor, kind.Depth + 0.6f, out var face))
            {
                var rotation = face.FacingRotation;
                var across = face.Fixed + face.Normal * kind.Depth * 0.5f;
                var half = kind.Width * 0.5f;
                var alongCursor = face.AlongX ? cursor.X : cursor.Z;
                var along = SnapEdges(alongCursor, half, lines, !face.AlongX, across - kind.Depth * 0.5f, across + kind.Depth * 0.5f);
                if (face.To - face.From >= kind.Width) along = Math.Clamp(along, face.From + half, face.To - half);
                var position = face.AlongX ? new Vec2(along, across) : new Vec2(across, along);
                return (ClampInside(position, kind.Width, kind.Depth, rotation, inside), rotation, null);
            }

            var footprint = CottageInterior.Footprint(cursor, kind.Width, kind.Depth, rotationDeg);
            var x = SnapEdges(cursor.X, footprint.HalfX, lines, false, footprint.MinZ, footprint.MaxZ);
            var z = SnapEdges(cursor.Z, footprint.HalfZ, lines, true, x - footprint.HalfX, x + footprint.HalfX);
            return (ClampInside(new Vec2(x, z), kind.Width, kind.Depth, rotationDeg, inside), rotationDeg, null);
        }

        /// <summary>Where the stairs under the pointer would go: lined up with the walls and furniture downstairs, or on the grid.</summary>
        public static Vec2 Stairs(BuildingType type, InteriorDto interior, Vec2 cursor, float rotationDeg)
        {
            var inside = CottageInterior.Inside(type);
            var lines = new List<CottageInterior.Face>();
            for (var floor = 0; floor < CottageInterior.FloorCount; floor++)
            {
                lines.AddRange(CottageInterior.WallFaces(type, interior.Floors[floor]));
                foreach (var item in interior.Floors[floor].Items)
                {
                    if (!CottageInterior.IsOffTheFloor(item) && CottageInterior.TryItemBox(item, out _, out var box)) CottageInterior.AddBoxFaces(lines, box, item.Id);
                }
            }

            var footprint = CottageInterior.Footprint(cursor, CottageInterior.StairsWidth, CottageInterior.StairsLength, rotationDeg);
            var x = SnapEdges(cursor.X, footprint.HalfX, lines, false, footprint.MinZ, footprint.MaxZ);
            var z = SnapEdges(cursor.Z, footprint.HalfZ, lines, true, x - footprint.HalfX, x + footprint.HalfX);
            return ClampInside(new Vec2(x, z), CottageInterior.StairsWidth, CottageInterior.StairsLength, rotationDeg, inside);
        }

        /// <summary>The wall face nearest the pointer on its room side, within <paramref name="reach"/>.</summary>
        private static bool TryNearestWall(List<CottageInterior.Face> faces, Vec2 cursor, float reach, out CottageInterior.Face nearest)
        {
            nearest = default;
            var found = false;
            var best = reach;
            foreach (var face in faces)
            {
                var across = face.AlongX ? cursor.Z : cursor.X;
                var along = face.AlongX ? cursor.X : cursor.Z;
                var distance = (across - face.Fixed) * face.Normal;
                if (distance < -0.1f || distance > best || along < face.From - 0.2f || along > face.To + 0.2f) continue;
                nearest = face;
                best = distance;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Snaps a centre with half-size <paramref name="half"/> along X (or Z when <paramref name="alongZ"/>)
        /// so an edge sits flush against a face pointing at it, from faces that overlap
        /// <paramref name="spanFrom"/>..<paramref name="spanTo"/> on the other axis. Otherwise its edge goes on the grid.
        /// </summary>
        private static float SnapEdges(float center, float half, List<CottageInterior.Face> faces, bool alongZ, float spanFrom, float spanTo)
        {
            var best = float.NaN;
            var bestShift = Reach;
            foreach (var face in faces)
            {
                // Faces across this axis: X positions are set by faces running along Z.
                if (face.AlongX != alongZ) continue;
                if (face.To <= spanFrom + 0.01f || face.From >= spanTo - 0.01f) continue;

                // The low edge rests on a face pointing +, the high edge on one pointing -.
                var edge = face.Normal > 0 ? center - half : center + half;
                var shift = face.Fixed - edge;
                if (MathF.Abs(shift) > bestShift) continue;
                best = center + shift;
                bestShift = MathF.Abs(shift);
            }

            return float.IsNaN(best) ? ToGrid(center - half) + half : best;
        }

        private static Vec2 ClampInside(Vec2 center, float width, float depth, float rotationDeg, in Aabb inside)
        {
            var box = CottageInterior.Footprint(center, width, depth, rotationDeg);
            var x = box.HalfX * 2f <= inside.MaxX - inside.MinX ? Math.Clamp(center.X, inside.MinX + box.HalfX, inside.MaxX - box.HalfX) : center.X;
            var z = box.HalfZ * 2f <= inside.MaxZ - inside.MinZ ? Math.Clamp(center.Z, inside.MinZ + box.HalfZ, inside.MaxZ - box.HalfZ) : center.Z;
            return new Vec2(x, z);
        }

        private static Vec2 Clamp(Vec2 p, in Aabb inside) => new Vec2(Math.Clamp(p.X, inside.MinX, inside.MaxX), Math.Clamp(p.Z, inside.MinZ, inside.MaxZ));

        /// <summary>The nearest of <paramref name="stops"/> within <paramref name="reach"/>, or the value itself.</summary>
        private static float SnapTo(float value, float reach, params float[] stops)
        {
            foreach (var stop in stops)
            {
                if (MathF.Abs(stop - value) <= reach) return stop;
            }

            return value;
        }

        private static bool NearAny(float value, params float[] stops)
        {
            foreach (var stop in stops)
            {
                if (MathF.Abs(stop - value) <= Reach) return true;
            }

            return false;
        }
    }
}

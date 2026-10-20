using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Cottages built from their insides (<see cref="CottageInterior"/>): floors, the stairs, walls
    /// with real holes for doors and windows, and the furniture, all in the building's frame. The same
    /// mesh serves from outside (with the roof on) and, cut away, for looking in while editing or
    /// walking around inside: floors above the one you're on are left off, and so are the roof and the
    /// outside walls between the camera and the room (<see cref="Front"/> and friends), down to a stub.
    /// Door leaves are separate (<see cref="DoorLeaf"/>) so they can swing.
    /// </summary>
    public static class InteriorMeshes
    {
        /// <summary>Outside walls to lower, as a bit mask, for seeing in from that side.</summary>
        public const int Front = 1, Back = 2, Left = 4, Right = 8;

        /// <summary>How much of a lowered wall is left standing.</summary>
        public const float StubHeight = 0.45f;

        public const float DoorLeafHeight = 2.05f;

        private static readonly Color PlinthColor = LowPolyMeshes.Hex("#8E8A82");
        private static readonly Color Boards = LowPolyMeshes.Hex("#C9A27A");
        private static readonly Color BoardsUpstairs = LowPolyMeshes.Hex("#BE966E");
        private static readonly Color Plaster = LowPolyMeshes.Hex("#F6EFE2");
        private static readonly Color Ceiling = LowPolyMeshes.Hex("#EDE5D6");
        private static readonly Color Trim = LowPolyMeshes.Hex("#FFFDF8");
        private static readonly Color Tread = LowPolyMeshes.Hex("#A87C55");
        private static readonly Color Riser = LowPolyMeshes.Hex("#8C6545");
        private static readonly Color Rail = LowPolyMeshes.Hex("#6E4F37");
        private static readonly Color Wood = LowPolyMeshes.Hex("#9A6B47");
        private static readonly Color DarkWood = LowPolyMeshes.Hex("#6B4A33");
        private static readonly Color Metal = LowPolyMeshes.Hex("#B8C2C8");
        public static readonly Color FrontDoorColor = LowPolyMeshes.Hex("#5A4636");
        public static readonly Color InsideDoorColor = LowPolyMeshes.Hex("#C49466");

        /// <summary>A whole cottage, roof on. Interiors that are the same share a mesh.</summary>
        public static Mesh Cottage(BuildingType type, InteriorDto interior) => Cottage(type, interior, CottageInterior.FloorCount - 1, true, 0);

        /// <param name="topFloor">The highest floor to draw.</param>
        /// <param name="roof">Whether to put the roof on (only when the top floor is drawn).</param>
        /// <param name="lowered">Outside walls to cut down to a stub (<see cref="Front"/> etc.).</param>
        public static Mesh Cottage(BuildingType type, InteriorDto interior, int topFloor, bool roof, int lowered)
        {
            var mb = new MeshBuilder();
            float hw = type.Width * 0.5f, hd = type.Depth * 0.5f;
            var wallColor = LowPolyMeshes.Hex(type.WallColor);
            mb.Box(Vector3.zero, type.Width + CottageInterior.PlinthMargin * 2f, CottageInterior.Plinth, type.Depth + CottageInterior.PlinthMargin * 2f, PlinthColor, PlinthColor);

            var inside = CottageInterior.Inside(type);
            topFloor = Mathf.Clamp(topFloor, 0, CottageInterior.FloorCount - 1);
            for (var floor = 0; floor <= topFloor; floor++)
            {
                var y = CottageInterior.FloorHeight(floor);
                var level = interior.Floors[floor];
                var openings = CottageInterior.Openings(interior, floor);
                var top = CottageInterior.FloorHeight(floor + 1);

                if (floor == 0) FlatRect(mb, inside, y + 0.004f, Boards, Vector3.up);
                else Slab(mb, inside, interior.Stairs, y);

                foreach (var wall in CottageInterior.OutsideWalls(type))
                {
                    var bit = wall.Id == CottageInterior.FrontWall ? Front : wall.Id == CottageInterior.BackWall ? Back : wall.Id == CottageInterior.LeftWall ? Left : Right;
                    // Only the floor in view is cut down; the walls below it stand full height.
                    var height = floor == topFloor && (lowered & bit) != 0 ? StubHeight : top - y;

                    // The front and back walls run the full width, covering the corners.
                    var full = wall.AlongX ? new CottageInterior.WallLine(wall.Id, true, wall.Fixed, -hw, hw, true) : wall;
                    WallWithOpenings(mb, full, openings, y, height, wallColor, Plaster);
                }

                foreach (var wall in level.Walls)
                {
                    if (CottageInterior.TryWallLine(wall, out var line)) WallWithOpenings(mb, line, openings, y, top - y - (floor == CottageInterior.FloorCount - 1 ? 0f : CottageInterior.SlabThickness), Plaster, Plaster);
                }

                foreach (var item in level.Items)
                {
                    if (!Furnishings.TryGetFurniture(item.TypeId, out var kind)) continue;
                    var dresser = item.OnId == null ? null : level.Items.Find(i => i.Id == item.OnId);
                    Furniture(mb, kind, item.X, item.Z, item.RotationDeg, y, dresser != null, item.OnId == null && kind.Placement == FurniturePlacement.DresserOrWall);
                }
            }

            if (interior.Stairs != null) Stairs(mb, interior.Stairs, topFloor >= 1);

            if (roof && topFloor == CottageInterior.FloorCount - 1) LowPolyMeshes.GabledRoof(mb, hw, hd, CottageInterior.WallTop, wallColor, LowPolyMeshes.Hex(type.RoofColor));
            return mb.ToMesh(name: type.Id + " inside");
        }

        /// <summary>A swinging door: hinged at the origin, running along +X, standing on y = 0.</summary>
        public static Mesh DoorLeaf(float width, Color color)
        {
            var mb = new MeshBuilder();
            var leaf = width - 0.04f;
            mb.Box(new Vector3(leaf * 0.5f + 0.02f, 0f, 0f), leaf, DoorLeafHeight, 0.05f, color, color);
            var knob = LowPolyMeshes.Hex("#E2C08D");
            foreach (var side in new[] { -1f, 1f })
            {
                mb.Box(new Vector3(leaf - 0.08f, 1f, side * 0.045f), 0.06f, 0.06f, 0.04f, knob, knob);
            }

            return mb.ToMesh(name: "door leaf");
        }

        /// <summary>
        /// A wall between heights <paramref name="y"/> and y + <paramref name="height"/>, with holes for
        /// its doors and windows. Outside walls are <paramref name="outer"/> coloured on the outside.
        /// </summary>
        public static void WallWithOpenings(MeshBuilder mb, in CottageInterior.WallLine wall, List<OpeningDto> openings, float y, float height,
            Color outer, Color inner)
        {
            var holes = new List<(float From, float To, float Bottom, float Top, bool Window)>();
            foreach (var opening in openings)
            {
                if (opening.WallId != wall.Id || !Furnishings.TryGetOpening(opening.TypeId, out var kind)) continue;
                var half = CottageInterior.OpeningWidth(opening) * 0.5f;
                holes.Add((opening.At - half, opening.At + half, kind.Bottom, kind.Top, !kind.IsDoor));
            }

            holes.Sort((a, b) => a.From.CompareTo(b.From));
            var cap = wall.Outside ? 0f : wall.HalfThickness;
            var start = wall.From - cap;
            var end = wall.To + cap;
            var at = start;
            foreach (var hole in holes)
            {
                if (hole.From > at) WallPiece(mb, wall, at, hole.From, y, y + height, outer, inner);
                if (hole.Bottom > 0f) WallPiece(mb, wall, hole.From, hole.To, y, y + Mathf.Min(height, hole.Bottom), outer, inner);
                if (hole.Top < height) WallPiece(mb, wall, hole.From, hole.To, y + hole.Top, y + height, outer, inner);
                if (hole.Window && hole.Bottom < height) WindowFrame(mb, wall, hole.From, hole.To, y + hole.Bottom, y + Mathf.Min(height, hole.Top));
                at = Mathf.Max(at, hole.To);
            }

            if (end > at) WallPiece(mb, wall, at, end, y, y + height, outer, inner);
        }

        /// <summary>A solid stretch of wall. Outside walls show <paramref name="outer"/> on the face away from the middle.</summary>
        private static void WallPiece(MeshBuilder mb, CottageInterior.WallLine wall, float from, float to, float y0, float y1, Color outer, Color inner)
        {
            if (to - from < 0.001f || y1 - y0 < 0.001f) return;
            var h = wall.HalfThickness;
            float a0 = wall.Fixed - h, a1 = wall.Fixed + h;
            Vector3 P(float along, float across, float yy) => wall.AlongX ? new Vector3(along, yy, across) : new Vector3(across, yy, along);
            var acrossAxis = wall.AlongX ? Vector3.forward : Vector3.right;
            var alongAxis = wall.AlongX ? Vector3.right : Vector3.forward;

            // Which face looks outside: the one further from the middle of the house.
            var plusOuter = wall.Outside && wall.Fixed > 0f;
            var minusOuter = wall.Outside && wall.Fixed < 0f;
            mb.Quad(P(from, a1, y0), P(from, a1, y1), P(to, a1, y1), P(to, a1, y0), plusOuter ? outer : inner, acrossAxis);
            mb.Quad(P(from, a0, y0), P(from, a0, y1), P(to, a0, y1), P(to, a0, y0), minusOuter ? outer : inner, -acrossAxis);
            mb.Quad(P(from, a0, y1), P(from, a1, y1), P(to, a1, y1), P(to, a0, y1), Trim, Vector3.up);
            mb.Quad(P(from, a0, y0), P(from, a1, y0), P(from, a1, y1), P(from, a0, y1), Trim, -alongAxis);
            mb.Quad(P(to, a0, y0), P(to, a1, y0), P(to, a1, y1), P(to, a0, y1), Trim, alongAxis);
            if (y0 > 0.3f) mb.Quad(P(from, a0, y0), P(from, a1, y0), P(to, a1, y0), P(to, a0, y0), Trim, Vector3.down);
        }

        /// <summary>A white frame and cross bars in a window hole.</summary>
        private static void WindowFrame(MeshBuilder mb, CottageInterior.WallLine wall, float from, float to, float y0, float y1)
        {
            const float bar = 0.05f;
            var depth = wall.HalfThickness * 0.6f;
            Vector3 C(float along, float yy) => wall.AlongX ? new Vector3(along, yy, wall.Fixed) : new Vector3(wall.Fixed, yy, along);
            void Bar(float along, float yy, float length, float height)
            {
                if (wall.AlongX) mb.Box(C(along, yy), length, height, depth * 2f, Trim, Trim);
                else mb.Box(C(along, yy), depth * 2f, height, length, Trim, Trim);
            }

            var middle = (from + to) * 0.5f;
            Bar(middle, y0, to - from, bar);
            Bar(middle, y1 - bar, to - from, bar);
            Bar(from + bar * 0.5f, y0, bar, y1 - y0);
            Bar(to - bar * 0.5f, y0, bar, y1 - y0);
            Bar(middle, y0, bar, y1 - y0);
            Bar(middle, (y0 + y1) * 0.5f - bar * 0.5f, to - from, bar);
        }

        /// <summary>The upstairs floor: boards on top, ceiling underneath, with a hole for the stairs.</summary>
        private static void Slab(MeshBuilder mb, in Aabb inside, StairsDto stairs, float y)
        {
            var bottom = y - CottageInterior.SlabThickness;
            var pieces = new List<Aabb>();
            if (stairs == null)
            {
                pieces.Add(inside);
            }
            else
            {
                var hole = CottageInterior.StairsBox(stairs);
                if (hole.MinX > inside.MinX) pieces.Add(new Aabb(inside.MinX, inside.MinZ, hole.MinX, inside.MaxZ));
                if (hole.MaxX < inside.MaxX) pieces.Add(new Aabb(hole.MaxX, inside.MinZ, inside.MaxX, inside.MaxZ));
                if (hole.MinZ > inside.MinZ) pieces.Add(new Aabb(hole.MinX, inside.MinZ, hole.MaxX, hole.MinZ));
                if (hole.MaxZ < inside.MaxZ) pieces.Add(new Aabb(hole.MinX, hole.MaxZ, hole.MaxX, inside.MaxZ));

                // The edges of the hole.
                mb.Sides(new Vector3(hole.Center.X, bottom, hole.Center.Z), hole.HalfX, hole.HalfZ, 0f, CottageInterior.SlabThickness, Trim);
            }

            foreach (var piece in pieces)
            {
                FlatRect(mb, piece, y, BoardsUpstairs, Vector3.up);
                FlatRect(mb, piece, bottom, Ceiling, Vector3.down);
            }
        }

        private static void FlatRect(MeshBuilder mb, in Aabb r, float y, Color color, Vector3 normal)
        {
            mb.Quad(new Vector3(r.MinX, y, r.MinZ), new Vector3(r.MinX, y, r.MaxZ), new Vector3(r.MaxX, y, r.MaxZ), new Vector3(r.MaxX, y, r.MinZ), color, normal);
        }

        /// <summary>The flight of stairs, plus rails round the hole upstairs when that floor is shown.</summary>
        public static void Stairs(MeshBuilder mb, StairsDto stairs, bool upstairs)
        {
            var center = new Vec2(stairs.X, stairs.Z);
            var run = CottageInterior.StairsLength / CottageInterior.StairsSteps;
            var rise = CottageInterior.StoryHeight / CottageInterior.StairsSteps;
            var y = CottageInterior.Plinth;
            for (var i = 0; i < CottageInterior.StairsSteps; i++)
            {
                var v = -CottageInterior.StairsLength * 0.5f + (i + 0.5f) * run;
                Block(mb, center, stairs.RotationDeg, 0f, v, y, CottageInterior.StairsWidth, (i + 1) * rise, run, Riser, Tread);
            }

            // A handrail up the open side is a nice touch, but the posts read better.
            for (var side = -1; side <= 1; side += 2)
            {
                var u = side * (CottageInterior.StairsWidth * 0.5f - 0.03f);
                Block(mb, center, stairs.RotationDeg, u, -CottageInterior.StairsLength * 0.5f + 0.05f, y, 0.06f, 0.95f, 0.06f, Rail, Rail);
            }

            if (!upstairs) return;

            // Upstairs, rails along both sides of the hole and across its foot.
            var top = CottageInterior.FloorHeight(1);
            var halfLength = CottageInterior.StairsLength * 0.5f;
            var halfWidth = CottageInterior.StairsWidth * 0.5f;
            const float post = 0.06f, height = 0.9f;
            for (var side = -1; side <= 1; side += 2)
            {
                var u = side * (halfWidth - post * 0.5f);
                Block(mb, center, stairs.RotationDeg, u, 0f, top + height - post, post, post, CottageInterior.StairsLength, Rail, Rail);
                for (var v = -halfLength + post * 0.5f; v <= halfLength; v += 0.5f)
                {
                    Block(mb, center, stairs.RotationDeg, u, v, top, 0.04f, height, 0.04f, Rail, Rail);
                }
            }

            Block(mb, center, stairs.RotationDeg, 0f, -halfLength + post * 0.5f, top + height - post, CottageInterior.StairsWidth, post, post, Rail, Rail);
            for (var u = -halfWidth + post * 0.5f; u <= halfWidth; u += 0.25f)
            {
                Block(mb, center, stairs.RotationDeg, u, -halfLength + post * 0.5f, top, 0.04f, height, 0.04f, Rail, Rail);
            }
        }

        /// <summary>
        /// A piece of furniture standing on the floor at height <paramref name="floorY"/>, made of boxes in
        /// its own frame (facing +Z). A TV goes on its dresser or up on the wall.
        /// </summary>
        public static void Furniture(MeshBuilder mb, FurnitureType type, float x, float z, float rotationDeg, float floorY, bool onDresser, bool onWall)
        {
            var c = new Vec2(x, z);
            var r = rotationDeg;
            var main = LowPolyMeshes.Hex(type.Color);
            var accent = LowPolyMeshes.Hex(type.Accent);
            float w = type.Width, d = type.Depth, y = floorY;
            void B(float u, float v, float bottom, float bw, float bh, float bd, Color sides, Color top) => Block(mb, c, r, u, v, y + bottom, bw, bh, bd, sides, top);

            switch (type.Id)
            {
                case "couch":
                    B(0f, 0f, 0f, w, 0.42f, d, main, main);
                    B(0f, -d * 0.5f + 0.11f, 0.42f, w, 0.43f, 0.22f, main, main);
                    B(-w * 0.5f + 0.1f, 0.05f, 0.42f, 0.2f, 0.2f, d - 0.1f, main, main);
                    B(w * 0.5f - 0.1f, 0.05f, 0.42f, 0.2f, 0.2f, d - 0.1f, main, main);
                    B(-w * 0.25f + 0.05f, 0.1f, 0.42f, w * 0.5f - 0.24f, 0.07f, d - 0.3f, accent, accent);
                    B(w * 0.25f - 0.05f, 0.1f, 0.42f, w * 0.5f - 0.24f, 0.07f, d - 0.3f, accent, accent);
                    break;

                case Furnishings.Dresser:
                    B(0f, 0f, 0f, w, 0.86f, d, main, main);
                    B(0f, 0f, 0.86f, w + 0.02f, 0.04f, d + 0.02f, accent, accent);
                    for (var k = 0; k < 3; k++)
                    {
                        B(0f, d * 0.5f + 0.005f, 0.12f + k * 0.26f, w - 0.12f, 0.2f, 0.01f, accent, accent);
                        B(0f, d * 0.5f + 0.02f, 0.2f + k * 0.26f, 0.12f, 0.03f, 0.02f, DarkWood, DarkWood);
                    }

                    break;

                case "dining":
                    B(0f, 0f, 0.7f, 1.1f, 0.05f, 0.8f, main, main);
                    foreach (var (lu, lv) in new[] { (-0.5f, -0.35f), (0.5f, -0.35f), (-0.5f, 0.35f), (0.5f, 0.35f) }) B(lu, lv, 0f, 0.06f, 0.7f, 0.06f, accent, accent);
                    foreach (var cu in new[] { -0.35f, 0.35f })
                    {
                        foreach (var cv in new[] { -0.7f, 0.7f })
                        {
                            var back = cv > 0f ? 0.17f : -0.17f;
                            B(cu, cv, 0f, 0.05f, 0.45f, 0.05f, accent, accent);
                            B(cu, cv, 0.43f, 0.42f, 0.05f, 0.38f, main, main);
                            B(cu, cv + back, 0.48f, 0.42f, 0.45f, 0.05f, main, main);
                        }
                    }

                    break;

                case "counter":
                    B(0f, -0.02f, 0f, w, 0.86f, d - 0.04f, main, main);
                    B(0f, 0f, 0.86f, w, 0.04f, d, accent, accent);
                    B(-w * 0.25f, d * 0.5f - 0.015f, 0.12f, w * 0.5f - 0.06f, 0.66f, 0.01f, LowPolyMeshes.Hex("#6B8796"), LowPolyMeshes.Hex("#6B8796"));
                    B(w * 0.25f, d * 0.5f - 0.015f, 0.12f, w * 0.5f - 0.06f, 0.66f, 0.01f, LowPolyMeshes.Hex("#6B8796"), LowPolyMeshes.Hex("#6B8796"));
                    break;

                case Furnishings.Tv:
                    var lift = onDresser ? 0.9f + 0.06f : onWall ? CottageInterior.WallTvBottom : 0f;
                    if (onDresser) B(0f, -0.01f, 0.9f, 0.25f, 0.06f, 0.08f, main, main);
                    B(0f, 0f, lift, w, type.Height, d * 0.6f, main, main);
                    B(0f, d * 0.3f, lift + 0.05f, w - 0.1f, type.Height - 0.1f, 0.01f, accent, accent);
                    break;

                case "bed":
                    B(0f, 0f, 0f, w, 0.3f, d, Wood, Wood);
                    B(0f, 0.02f, 0.3f, w - 0.06f, 0.22f, d - 0.12f, main, main);
                    B(0f, d * 0.18f, 0.52f, w - 0.02f, 0.04f, d * 0.58f, accent, accent);
                    B(0f, -d * 0.5f + 0.3f, 0.52f, w - 0.45f, 0.1f, 0.32f, main, main);
                    B(0f, -d * 0.5f + 0.04f, 0f, w, 0.95f, 0.08f, Wood, Wood);
                    break;

                case "toilet":
                    B(0f, 0.06f, 0f, 0.3f, 0.36f, 0.4f, main, main);
                    B(0f, 0.08f, 0.36f, 0.42f, 0.06f, 0.48f, main, accent);
                    B(0f, -d * 0.5f + 0.1f, 0.36f, 0.46f, 0.42f, 0.2f, main, main);
                    B(0f, -d * 0.5f + 0.1f, 0.78f, 0.48f, 0.03f, 0.22f, accent, accent);
                    break;

                case "shower":
                    B(0f, 0f, 0f, w, 0.08f, d, main, accent);
                    B(-w * 0.5f + 0.02f, 0f, 0.08f, 0.04f, 1.95f, d, accent, accent);
                    B(w * 0.5f - 0.02f, 0f, 0.08f, 0.04f, 1.95f, d, accent, accent);
                    B(0f, -d * 0.5f + 0.02f, 0.08f, w, 1.95f, 0.04f, main, main);
                    B(0f, d * 0.5f - 0.02f, 1.98f, w, 0.04f, 0.04f, Metal, Metal);
                    B(0f, -d * 0.5f + 0.12f, 1.75f, 0.18f, 0.04f, 0.18f, Metal, Metal);
                    break;

                case "sink":
                    B(0f, -0.04f, 0f, 0.14f, 0.75f, 0.14f, main, main);
                    B(0f, 0f, 0.75f, w, 0.15f, d, main, accent);
                    B(0f, -d * 0.5f + 0.06f, 0.9f, 0.05f, 0.12f, 0.05f, Metal, Metal);
                    break;

                default:
                    B(0f, 0f, 0f, w, type.Height, d, main, accent);
                    break;
            }
        }

        /// <summary>A box at (<paramref name="u"/>, <paramref name="v"/>) in something's own frame, placed and turned with it.</summary>
        private static void Block(MeshBuilder mb, Vec2 origin, float rotationDeg, float u, float v, float bottom, float width, float height, float depth, Color sides, Color top)
        {
            var p = Frames.ToWorld(origin, rotationDeg, new Vec2(u, v));
            var sideways = Mathf.Abs(CottageInterior.Facing(rotationDeg).X) > 0.5f;
            mb.Box(new Vector3(p.X, bottom, p.Z), sideways ? depth : width, height, sideways ? width : depth, sides, top);
        }

        /// <summary>Everything that changes how an interior looks, as text, so identical cottages can share a mesh.</summary>
        public static string Key(InteriorDto interior)
        {
            var sb = new StringBuilder();
            void N(float f) => sb.Append(f.ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
            if (interior.Stairs != null)
            {
                N(interior.Stairs.X);
                N(interior.Stairs.Z);
                N(interior.Stairs.RotationDeg);
            }

            foreach (var floor in interior.Floors)
            {
                sb.Append('|');
                foreach (var w in floor.Walls)
                {
                    sb.Append('w');
                    N(w.X0);
                    N(w.Z0);
                    N(w.X1);
                    N(w.Z1);
                }

                foreach (var o in floor.Openings)
                {
                    sb.Append(o.TypeId).Append(o.WallId).Append(o.Id).Append(';');
                    N(o.At);
                }

                foreach (var i in floor.Items)
                {
                    sb.Append(i.TypeId).Append(i.OnId).Append(';');
                    N(i.X);
                    N(i.Z);
                    N(i.RotationDeg);
                }
            }

            return sb.ToString();
        }
    }
}

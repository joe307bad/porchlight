// Ground-floor rooms: every building piece has one room behind its street
// front, with walls you can't walk through, the building's own doors and
// windows in them (or, without any, one door in the middle of the front wall),
// and furniture picked for the kind of building. Pure. Room-local frame: u runs
// along the street front (0..width), v from the front wall (0) to the back wall
// (depth). Furniture models face +Z.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class Room
    {
        public int id;
        public double x0, y0, x1, y1;
        public Facing facing;
        public HouseStyle style;
        /// <summary>How tall the walls stand.</summary>
        public double height = Interiors.WALL_HEIGHT;
        /// <summary>The ways in; null for one door in the middle of the front wall.</summary>
        public List<RoomDoor> doors;
        public List<RoomWindow> windows = new List<RoomWindow>();
    }

    /// <summary>
    /// An opening in one of a room's walls: which wall, where along it (map x for
    /// the N and S walls, map y for E and W) and how tall.
    /// </summary>
    public struct RoomDoor
    {
        public Facing side;
        public double a0, a1, height;
    }

    /// <summary>A window in one of a room's walls, from its sill (y0) to its head (y1).</summary>
    public struct RoomWindow
    {
        public Facing side;
        public double a0, a1, y0, y1;
    }

    public struct FurnitureSize
    {
        public double w, d, h;
    }

    public sealed class Placement
    {
        public string name;
        public double x, y;
        public double lift;
        public double rot;
        public Rect box;
        public bool flat;
    }

    public struct KeeperSpot
    {
        public double x, y, rot;
    }

    public sealed class RoomPlan
    {
        public List<Placement> furniture;
        public KeeperSpot? keeper;
    }

    public sealed class RoomFrame
    {
        public double width, depth;
        public Vec2 outward, along, frontMid;

        public Vec2 ToMap(double u, double v) =>
            new Vec2(frontMid.x + along.x * (u - width / 2) - outward.x * v, frontMid.y + along.y * (u - width / 2) - outward.y * v);

        public (double u, double v) ToLocal(Vec2 p)
        {
            double dx = p.x - frontMid.x, dy = p.y - frontMid.y;
            return (width / 2 + dx * along.x + dy * along.y, -(dx * outward.x + dy * outward.y));
        }

        /// <summary>A direction in the room's frame (+v is into the room) as a map rotation for a model facing +Z.</summary>
        public double RotFor(double du, double dv)
        {
            double dx = along.x * du - outward.x * dv;
            double dy = along.y * du - outward.y * dv;
            return DetMath.Atan2(dx, dy);
        }
    }

    /// <summary>A doorway: the middle of its sill on the wall's outside line, which way out is, and its size.</summary>
    public struct Door
    {
        public Vec2 at, outward, along;
        public Facing side;
        public double width, height;
    }

    public static class Interiors
    {
        public const double WALL = 0.07;
        public const double WALL_HEIGHT = 0.8;
        public const double DOOR_WIDTH = 0.46;
        public const double DOOR_HEIGHT = 0.62;
        /// <summary>
        /// The narrowest way through a doorway: a little wider than the player, however
        /// narrow the building's door is (they brush its frame rather than stick in it).
        /// </summary>
        public const double DOORWAY = 0.34;
        public const double FURNITURE_SCALE = 0.6;
        public const double ENTER_MARGIN = -0.05;
        public const double LEAVE_MARGIN = WALL + 0.1;

        public static Vec2 Outward(Facing f) => f switch
        {
            Facing.N => new Vec2(0, -1),
            Facing.E => new Vec2(1, 0),
            Facing.S => new Vec2(0, 1),
            _ => new Vec2(-1, 0),
        };

        public static RoomFrame Frame(Room room)
        {
            var f = Outward(room.facing);
            bool alongX = room.facing == Facing.N || room.facing == Facing.S;
            double width = alongX ? room.x1 - room.x0 : room.y1 - room.y0;
            double depth = alongX ? room.y1 - room.y0 : room.x1 - room.x0;
            var a = new Vec2(-f.y, f.x);
            var frontMid = new Vec2((room.x0 + room.x1) / 2 + (f.x * (room.x1 - room.x0)) / 2, (room.y0 + room.y1) / 2 + (f.y * (room.y1 - room.y0)) / 2);
            return new RoomFrame { width = width, depth = depth, outward = f, along = a, frontMid = frontMid };
        }

        static List<RoomDoor> DoorsOf(Room room)
        {
            if (room.doors != null) return room.doors;
            var mid = Frame(room).frontMid;
            double a = room.facing == Facing.N || room.facing == Facing.S ? mid.x : mid.y;
            return new List<RoomDoor> { new RoomDoor { side = room.facing, a0 = a - DOOR_WIDTH / 2, a1 = a + DOOR_WIDTH / 2, height = DOOR_HEIGHT } };
        }

        public static List<Door> Doors(Room room)
        {
            var o = new List<Door>();
            foreach (var d in DoorsOf(room))
            {
                var f = Outward(d.side);
                double mid = (d.a0 + d.a1) / 2;
                var at = d.side == Facing.N || d.side == Facing.S
                    ? new Vec2(mid, d.side == Facing.N ? room.y0 : room.y1)
                    : new Vec2(d.side == Facing.W ? room.x0 : room.x1, mid);
                o.Add(new Door { at = at, outward = f, along = new Vec2(-f.y, f.x), side = d.side, width = d.a1 - d.a0, height = d.height });
            }
            return o;
        }

        public static Facing Opposite(Facing f) => (Facing)(((int)f + 2) % 4);

        public static Rect InnerRect(Room room) => new Rect(room.x0 + WALL, room.y0 + WALL, room.x1 - WALL, room.y1 - WALL);

        public static bool InRoom(Room room, Vec2 p, double margin = 0)
        {
            var r = InnerRect(room);
            return p.x > r.x0 - margin && p.x < r.x1 + margin && p.y > r.y0 - margin && p.y < r.y1 + margin;
        }

        public static double RoomCameraYaw(Room room, int turn)
        {
            var o = Outward(room.facing);
            return DetMath.Atan2(o.x, o.y) + (turn * Math.PI) / 2;
        }

        public static bool WallFacesCamera(Facing side, Vec2 camera)
        {
            var n = Outward(side);
            double len = Numbers.Hypot(camera.x, camera.y);
            if (len == 0 || double.IsNaN(len)) len = 1;
            return (n.x * camera.x + n.y * camera.y) / len > 0.25;
        }

        /// <summary>The stretch of wall `side` from a0 to a1 along it (see RoomDoor), `t` thick.</summary>
        static Rect WallRect(Room room, Facing side, double a0, double a1, double t) => side switch
        {
            Facing.N => new Rect(a0, room.y0, a1, room.y0 + t),
            Facing.S => new Rect(a0, room.y1 - t, a1, room.y1),
            Facing.W => new Rect(room.x0, a0, room.x0 + t, a1),
            _ => new Rect(room.x1 - t, a0, room.x1, a1),
        };

        static (double from, double to) WallSpan(Room room, Facing side) =>
            side == Facing.N || side == Facing.S ? (room.x0, room.x1) : (room.y0, room.y1);

        /// <summary>What's left of [from, to] once the gaps are taken out.</summary>
        static List<(double a0, double a1)> Between(double from, double to, IEnumerable<(double a0, double a1)> gaps)
        {
            var o = new List<(double, double)>();
            double at = from;
            foreach (var g in gaps.OrderBy(g => g.a0))
            {
                if (g.a0 > at) o.Add((at, Math.Min(g.a0, to)));
                at = Math.Max(at, g.a1);
            }
            if (at < to) o.Add((at, to));
            return o;
        }

        static readonly Facing[] SIDES = { Facing.N, Facing.E, Facing.S, Facing.W };

        /// <summary>The walls you can't walk through: every side, broken only by the doorways (at least DOORWAY wide).</summary>
        public static List<Rect> RoomWalls(Room room)
        {
            var o = new List<Rect>();
            var doors = DoorsOf(room);
            foreach (var side in SIDES)
            {
                var (from, to) = WallSpan(room, side);
                var gaps = doors.Where(d => d.side == side).Select(d =>
                {
                    double half = Math.Max(d.a1 - d.a0, DOORWAY) / 2, mid = (d.a0 + d.a1) / 2;
                    return (mid - half, mid + half);
                });
                foreach (var (a0, a1) in Between(from, to, gaps)) o.Add(WallRect(room, side, a0, a1, WALL));
            }
            return o;
        }

        /// <summary>The way through a doorway, kept clear of solids: DOORWAY wide at least, and a little either side of the wall.</summary>
        public static Rect Passage(Door d)
        {
            double half = Math.Max(d.width, DOORWAY) / 2, deep = 0.25;
            double hx = Math.Abs(d.along.x) * half + Math.Abs(d.outward.x) * deep;
            double hy = Math.Abs(d.along.y) * half + Math.Abs(d.outward.y) * deep;
            return new Rect(d.at.x - hx, d.at.y - hy, d.at.x + hx, d.at.y + hy);
        }

        /// <summary>What's left of rectangle r once every one of `holes` is taken out of it.</summary>
        public static IEnumerable<Rect> Without(Rect r, IEnumerable<Rect> holes)
        {
            var parts = new List<Rect> { r };
            foreach (var h in holes)
            {
                var next = new List<Rect>();
                foreach (var p in parts)
                {
                    if (h.x0 >= p.x1 || h.x1 <= p.x0 || h.y0 >= p.y1 || h.y1 <= p.y0)
                    {
                        next.Add(p);
                        continue;
                    }
                    if (p.x0 < h.x0) next.Add(new Rect(p.x0, p.y0, h.x0, p.y1));
                    if (h.x1 < p.x1) next.Add(new Rect(h.x1, p.y0, p.x1, p.y1));
                    double x0 = Math.Max(p.x0, h.x0), x1 = Math.Min(p.x1, h.x1);
                    if (p.y0 < h.y0) next.Add(new Rect(x0, p.y0, x1, h.y0));
                    if (h.y1 < p.y1) next.Add(new Rect(x0, h.y1, x1, p.y1));
                }
                parts = next;
            }
            return parts;
        }

        /// <summary>A piece of wall as drawn: its side, its footprint and how high it runs.</summary>
        public struct WallPiece
        {
            public Facing side;
            public Rect rect;
            public double y0, y1;
        }

        /// <summary>The walls as drawn: whole height between the openings, above every door and below and above every window.</summary>
        public static List<WallPiece> WallPieces(Room room)
        {
            var o = new List<WallPiece>();
            var doors = DoorsOf(room);
            void Add(Facing side, double a0, double a1, double y0, double y1)
            {
                if (y1 - y0 > 1e-3 && a1 - a0 > 1e-3) o.Add(new WallPiece { side = side, rect = WallRect(room, side, a0, a1, WALL), y0 = y0, y1 = y1 });
            }
            foreach (var side in SIDES)
            {
                var (from, to) = WallSpan(room, side);
                var gaps = doors.Where(d => d.side == side).Select(d => (d.a0, d.a1))
                    .Concat(room.windows.Where(w => w.side == side).Select(w => (w.a0, w.a1)));
                foreach (var (a0, a1) in Between(from, to, gaps)) Add(side, a0, a1, 0, room.height);
                foreach (var d in doors.Where(d => d.side == side)) Add(side, d.a0, d.a1, d.height, room.height);
                foreach (var w in room.windows.Where(w => w.side == side))
                {
                    Add(side, w.a0, w.a1, 0, w.y0);
                    Add(side, w.a0, w.a1, w.y1, room.height);
                }
            }
            return o;
        }

        struct LocalBox
        {
            public double u0, v0, u1, v1;
        }

        enum Face { Front, Back, Left, Right }

        static (double, double) FaceDir(Face f) => f switch
        {
            Face.Front => (0, -1),
            Face.Back => (0, 1),
            Face.Left => (-1, 0),
            _ => (1, 0),
        };

        sealed class PutOpts
        {
            public double lift;
            public bool flat;
            public bool on;
        }

        /// <summary>Furnish a room: things against the walls, a few pieces in the middle, clear of the door and each other.</summary>
        public static RoomPlan FurnishRoom(Room room, Func<string, FurnitureSize> size, Rng rng)
        {
            var fr = Frame(room);
            double W = fr.width;
            double D = fr.depth;
            var placed = new List<(LocalBox box, bool flat)>();
            var spots = new List<(string name, double u, double v, Face face)>();
            var o = new List<Placement>();
            var inner = new LocalBox { u0 = WALL + 0.02, v0 = WALL + 0.02, u1 = W - WALL - 0.02, v1 = D - WALL - 0.02 };
            // Clear floor in front of every door, so nothing stands in the way in.
            var doorways = new List<LocalBox>();
            double doorU = W / 2;
            bool frontDoor = false;
            foreach (var d in Doors(room))
            {
                bool across = d.side == room.facing || d.side == Opposite(room.facing);
                double deep = Math.Min((across ? D : W) * 0.45, 0.9);
                double half = Math.Max(0.42, d.width / 2 + 0.19);
                var corners = new[] { -1, 1 }.SelectMany(k => new[] { 0.0, deep }.Select(inward =>
                    fr.ToLocal(new Vec2(d.at.x + d.along.x * k * half - d.outward.x * inward, d.at.y + d.along.y * k * half - d.outward.y * inward)))).ToList();
                doorways.Add(new LocalBox { u0 = corners.Min(c => c.u), v0 = corners.Min(c => c.v), u1 = corners.Max(c => c.u), v1 = corners.Max(c => c.v) });
                if (d.side == room.facing && !frontDoor)
                {
                    doorU = fr.ToLocal(d.at).u;
                    frontDoor = true;
                }
            }
            bool Overlaps(LocalBox a, LocalBox b) => a.u0 < b.u1 && b.u0 < a.u1 && a.v0 < b.v1 && b.v0 < a.v1;

            bool Put(string name, double u, double v, Face face, PutOpts opts = null)
            {
                opts ??= new PutOpts();
                var s = size(name);
                bool sideways = face == Face.Left || face == Face.Right;
                double hu = (sideways ? s.d : s.w) / 2;
                double hv = (sideways ? s.w : s.d) / 2;
                var box = new LocalBox { u0 = u - hu, v0 = v - hv, u1 = u + hu, v1 = v + hv };
                if (box.u0 < inner.u0 - 1e-6 || box.v0 < inner.v0 - 1e-6 || box.u1 > inner.u1 + 1e-6 || box.v1 > inner.v1 + 1e-6) return false;
                bool flat = opts.flat;
                if (!flat && !opts.on)
                {
                    foreach (var dw in doorways) if (Overlaps(box, dw)) return false;
                    foreach (var p in placed) if (!p.flat && Overlaps(p.box, box)) return false;
                }
                if (!opts.on)
                {
                    placed.Add((box, flat));
                    spots.Add((name, u, v, face));
                }
                var (du, dv) = FaceDir(face);
                var c = fr.ToMap(u, v);
                var p0 = fr.ToMap(box.u0, box.v0);
                var p1 = fr.ToMap(box.u1, box.v1);
                o.Add(new Placement
                {
                    name = name,
                    x = c.x,
                    y = c.y,
                    lift = opts.lift,
                    rot = fr.RotFor(du, dv),
                    box = new Rect(Math.Min(p0.x, p1.x), Math.Min(p0.y, p1.y), Math.Max(p0.x, p1.x), Math.Max(p0.y, p1.y)),
                    flat = flat,
                });
                return true;
            }

            int AlongWall(string wall, IList<string> names, double from = 0, double gap = 0.02)
            {
                int count = 0;
                double span = wall == "back" ? inner.u1 - inner.u0 : inner.v1 - inner.v0;
                double at = (wall == "back" ? inner.u0 : inner.v0) + span * from;
                foreach (var name in names)
                {
                    var s = size(name);
                    bool ok;
                    if (wall == "back") ok = Put(name, at + s.w / 2, inner.v1 - s.d / 2, Face.Front);
                    else if (wall == "left") ok = Put(name, inner.u0 + s.d / 2, at + s.w / 2, Face.Right);
                    else ok = Put(name, inner.u1 - s.d / 2, at + s.w / 2, Face.Left);
                    if (ok) count++;
                    at += s.w + gap;
                }
                return count;
            }

            string Pick(params string[] xs) => xs[(int)Math.Floor(rng.Next() * xs.Length)];
            Face PickFace(params Face[] xs) => xs[(int)Math.Floor(rng.Next() * xs.Length)];
            List<string> Many(int n, Func<string> f)
            {
                var l = new List<string>();
                for (int i = 0; i < n; i++) l.Add(f());
                return l;
            }
            // Set something on top of each `under` already placed, with chance `p`.
            void OnTop(string under, Func<string> what, double p = 1)
            {
                foreach (var sp in spots.Where(x => x.name == under).ToList())
                    if (rng.Chance(p)) Put(what(), sp.u, sp.v, sp.face, new PutOpts { lift = size(under).h, on = true });
            }
            string Plant() => Pick("pottedPlant", "pottedPlant", "plantSmall1", "plantSmall2", "plantSmall3");
            string Shelf() => Pick("bookcaseOpen", "bookcaseClosedWide", "bookcaseOpen", "bookcaseClosed", "bookcaseClosedDoors", "bookcaseOpenLow");
            double cu = W / 2;
            double cv = D / 2 + 0.15;
            KeeperSpot? keeper = null;

            switch (room.style)
            {
                case HouseStyle.Shop:
                {
                    double counterV = Math.Min(D - WALL - 0.55, Math.Max(0.9, D * 0.55));
                    var bar = size("kitchenBar");
                    int n = (int)Math.Max(1, Math.Min(5, Math.Floor((W * 0.55) / bar.w)));
                    double start = cu - (n * bar.w) / 2 + (rng.Chance(0.5) ? 0.25 : -0.25);
                    for (int k = 0; k < n; k++) Put("kitchenBar", start + (k + 0.5) * bar.w, counterV, Face.Front);
                    Put("kitchenBarEnd", start - size("kitchenBarEnd").w / 2, counterV, Face.Front);
                    Put("kitchenBarEnd", start + n * bar.w + size("kitchenBarEnd").w / 2, counterV, Face.Front);
                    Put(Pick("radio", "computerScreen", "laptop", "kitchenCoffeeMachine"), start + bar.w / 2, counterV - 0.02, Face.Front, new PutOpts { lift = bar.h, on = true });
                    var kp = fr.ToMap(start + (n * bar.w) / 2, counterV + bar.d / 2 + 0.16);
                    keeper = new KeeperSpot { x = kp.x, y = kp.y, rot = fr.RotFor(0, -1) };
                    AlongWall("back", Many(8, Shelf), 0.02);
                    AlongWall("left", new[] { Pick("bookcaseOpen", "bookcaseClosedWide", "kitchenFridgeLarge"), "cardboardBoxClosed", Shelf() }, 0.05);
                    AlongWall("right", new[] { Shelf(), Pick("cardboardBoxOpen", "pottedPlant", "plantSmall2"), Shelf() }, 0.05);
                    Put(Plant(), inner.u0 + 0.15, inner.v0 + 0.15, Face.Front);
                    Put(Pick("stoolBar", "stoolBarSquare"), start + bar.w * 0.5, counterV - bar.d / 2 - 0.15, Face.Back);
                    break;
                }
                case HouseStyle.Office:
                {
                    keeper = null;
                    var bar = size("kitchenBar");
                    if (Put("kitchenBar", cu - 0.6, 1.0, Face.Front) && Put("kitchenBar", cu - 0.6 - bar.w, 1.0, Face.Front))
                    {
                        Put(Pick("kitchenCoffeeMachine", "computerScreen", "laptop"), cu - 0.6, 1.0, Face.Front, new PutOpts { lift = bar.h, on = true });
                        var kp = fr.ToMap(cu - 0.6 - bar.w / 2, 1.0 + bar.d / 2 + 0.16);
                        keeper = new KeeperSpot { x = kp.x, y = kp.y, rot = fr.RotFor(0, -1) };
                    }
                    string deskName = Pick("desk", "desk", "deskCorner");
                    var desk = size(deskName);
                    for (double v = 1.6; v < D - WALL - desk.d; v += Math.Max(0.85, desk.d + 0.45))
                    {
                        for (double u = inner.u0 + 0.1 + desk.w / 2; u < inner.u1 - desk.w / 2; u += desk.w + 0.35)
                        {
                            if (!Put(deskName, u, v, Face.Back)) continue;
                            if (rng.Chance(0.3)) Put("laptop", u, v, Face.Back, new PutOpts { lift = desk.h, on = true });
                            else
                            {
                                Put("computerScreen", u, v + 0.04, Face.Back, new PutOpts { lift = desk.h, on = true });
                                Put("computerKeyboard", u, v - 0.06, Face.Back, new PutOpts { lift = desk.h, on = true });
                            }
                            Put("chairDesk", u, v - desk.d / 2 - 0.12, Face.Back);
                        }
                    }
                    AlongWall("left", new[] { Pick("bookcaseClosedWide", "bookcaseClosedDoors"), Plant(), "kitchenFridgeSmall" }, 0.4);
                    AlongWall("right", new[] { Plant(), Shelf(), "speaker" }, 0.4);
                    Put("trashcan", inner.u1 - 0.1, inner.v0 + 0.5, Face.Left);
                    break;
                }
                case HouseStyle.Workshop:
                {
                    AlongWall("back", Many(6, () => Pick("bookcaseOpen", "bookcaseOpenLow", "cardboardBoxClosed", "cardboardBoxOpen", "kitchenCabinetDrawer", "washer", "dryer")), 0.02);
                    AlongWall("left", new[] { "table", "cardboardBoxClosed", "cardboardBoxOpen" }, 0.15);
                    AlongWall("right", new[] { Pick("bookcaseClosedWide", "kitchenCabinet", "washerDryerStacked"), "trashcan" }, 0.2);
                    Put("table", cu, cv, PickFace(Face.Front, Face.Back));
                    OnTop("table", () => Pick("cardboardBoxOpen", "cardboardBoxOpen", "radio", "laptop", "books"), 0.8);
                    Put(Pick("stoolBar", "stoolBarSquare"), cu + 0.5, cv, Face.Left);
                    break;
                }
                case HouseStyle.Hall:
                case HouseStyle.Church:
                {
                    bool church = room.style == HouseStyle.Church;
                    Put(church ? Pick("table", "tableCrossCloth") : Pick("tableRound", "tableCloth", "tableCross"), cu, inner.v1 - 0.4, Face.Front);
                    Put(Plant(), inner.u0 + 0.15, inner.v1 - 0.2, Face.Front);
                    Put(Plant(), inner.u1 - 0.15, inner.v1 - 0.2, Face.Front);
                    if (!church)
                        foreach (int side in new[] { -1, 1 })
                            Put("speaker", cu + side * 0.75, inner.v1 - 0.2, Face.Front);
                    string benchName = church ? "bench" : Pick("bench", "benchCushion", "benchCushionLow");
                    var bench = size(benchName);
                    for (double v = inner.v1 - 1.1; v > 1.0; v -= bench.d + 0.32)
                        foreach (int side in new[] { -1, 1 })
                            for (int k = 0; k < 3; k++)
                                Put(benchName, cu + side * (0.45 + bench.w * (k + 0.5)), v, Face.Back);
                    Put(church ? "rugRectangle" : Pick("rugRectangle", "rugSquare"), cu, D * 0.55, Face.Back, new PutOpts { flat = true });
                    break;
                }
                default:
                {
                    var kitchen = new List<string>
                    {
                        Pick("kitchenFridge", "kitchenFridgeLarge", "kitchenFridgeSmall", "kitchenFridgeBuiltIn"),
                        Pick("kitchenStove", "kitchenStoveElectric"), "kitchenSink", "kitchenCabinet", "kitchenCabinetDrawer",
                    };
                    if (rng.Chance(0.4)) kitchen.Add(Pick("washer", "dryer"));
                    bool left = rng.Chance(0.5);
                    var reversed = new List<string>(kitchen);
                    reversed.Reverse();
                    AlongWall("back", left ? kitchen : reversed, left ? 0 : 0.45);
                    string sofaWall = left ? "right" : "left";
                    OnTop("kitchenCabinet", () => Pick("kitchenMicrowave", "kitchenCoffeeMachine", "toaster", "kitchenBlender"), 0.8);
                    OnTop("kitchenCabinetDrawer", () => Pick("toaster", "kitchenBlender", "kitchenCoffeeMachine"), 0.4);
                    if (AlongWall(sofaWall, new[] { Pick("loungeSofa", "loungeSofaLong", "loungeDesignSofa") }, 0.35) == 0)
                        AlongWall(sofaWall, new[] { Pick("loungeChair", "loungeChairRelax", "loungeDesignChair") }, 0.35);
                    string tvCabinet = Pick("cabinetTelevision", "cabinetTelevisionDoors");
                    AlongWall(sofaWall == "left" ? "right" : "left", new[] { Pick("bookcaseOpen", "bookcaseClosedWide", "bookcaseClosed", "bookcaseOpenLow"), tvCabinet }, 0.3);
                    OnTop(tvCabinet, () => Pick("televisionModern", "televisionModern", "televisionVintage"));
                    Put(Pick("rugRectangle", "rugRound", "rugRounded", "rugSquare"), cu, cv, Face.Front, new PutOpts { flat = true });
                    string table = Pick("table", "tableRound", "tableCloth", "tableCross", "tableCrossCloth", "tableGlass");
                    if (Put(table, cu, cv, Face.Front))
                    {
                        var t = size(table);
                        string chair = Pick("chair", "chairCushion", "chairModernCushion", "chairModernFrameCushion", "chairRounded");
                        Put(chair, cu - t.w / 2 - 0.12, cv, Face.Right);
                        Put(chair, cu + t.w / 2 + 0.12, cv, Face.Left);
                        Put(chair, cu, cv + t.d / 2 + 0.12, Face.Front);
                    }
                    else
                    {
                        string coffee = Pick("tableCoffee", "tableCoffeeGlass", "tableCoffeeSquare", "tableCoffeeGlassSquare");
                        if (Put(coffee, cu, cv, Face.Front) && rng.Chance(0.5)) Put("books", cu, cv, Face.Front, new PutOpts { lift = size(coffee).h, on = true });
                    }
                    Put(Plant(), inner.u0 + 0.14, inner.v0 + 0.14, Face.Front);
                    Put(Pick("lampRoundFloor", "lampSquareFloor"), inner.u1 - 0.12, inner.v0 + 0.12, Face.Front);
                    if (frontDoor) Put("coatRackStanding", doorU + 0.62, inner.v0 + 0.16, Face.Front);
                    Put(Plant(), inner.u0 + 0.12, inner.v1 - 0.12, Face.Front);
                    if (frontDoor) Put("rugDoormat", doorU, inner.v0 + size("rugDoormat").d / 2, Face.Front, new PutOpts { flat = true });
                    if (rng.Chance(0.25)) Put("bear", inner.u1 - 0.14, inner.v1 - 0.12, Face.Front);
                    break;
                }
            }
            return new RoomPlan { furniture = o, keeper = keeper };
        }
    }
}

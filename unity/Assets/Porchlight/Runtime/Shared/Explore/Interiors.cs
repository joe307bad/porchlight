// Ground-floor rooms: every building piece has one room behind its street
// front, with walls you can't walk through, a door in the middle of the front
// wall, and furniture picked for the kind of building. Pure. A port of
// render/iso/interiors.ts. Room-local frame: u runs along the street front
// (0..width), v from the front wall (0) to the back wall (depth). Furniture
// models face +Z.

using System;
using System.Collections.Generic;
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

        /// <summary>A direction in the room's frame (+v is into the room) as a map rotation for a model facing +Z.</summary>
        public double RotFor(double du, double dv)
        {
            double dx = along.x * du - outward.x * dv;
            double dy = along.y * du - outward.y * dv;
            return JsMath.Atan2(dx, dy);
        }
    }

    public struct Door
    {
        public Vec2 at, outward, along;
    }

    public static class Interiors
    {
        public const double WALL = 0.07;
        public const double WALL_HEIGHT = 0.8;
        public const double DOOR_WIDTH = 0.46;
        public const double DOOR_HEIGHT = 0.62;
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

        public static Door DoorOf(Room room)
        {
            var fr = Frame(room);
            return new Door { at = fr.frontMid, outward = fr.outward, along = fr.along };
        }

        public static Rect InnerRect(Room room) => new Rect(room.x0 + WALL, room.y0 + WALL, room.x1 - WALL, room.y1 - WALL);

        public static bool InRoom(Room room, Vec2 p, double margin = 0)
        {
            var r = InnerRect(room);
            return p.x > r.x0 - margin && p.x < r.x1 + margin && p.y > r.y0 - margin && p.y < r.y1 + margin;
        }

        public static Facing WallSide(Room room, Rect w)
        {
            double t = WALL + 1e-6;
            if (w.y1 - room.y0 <= t) return Facing.N;
            if (room.y1 - w.y0 <= t) return Facing.S;
            if (w.x1 - room.x0 <= t) return Facing.W;
            return Facing.E;
        }

        public static double RoomCameraYaw(Room room, int turn)
        {
            var o = Outward(room.facing);
            return JsMath.Atan2(o.x, o.y) + (turn * Math.PI) / 2;
        }

        public static bool WallFacesCamera(Facing side, Vec2 camera)
        {
            var n = Outward(side);
            double len = Js.Hypot(camera.x, camera.y);
            if (len == 0 || double.IsNaN(len)) len = 1;
            return (n.x * camera.x + n.y * camera.y) / len > 0.25;
        }

        public static List<Rect> RoomWalls(Room room)
        {
            double x0 = room.x0, y0 = room.y0, x1 = room.x1, y1 = room.y1;
            double t = WALL;
            var o = new List<Rect>();
            foreach (var side in new[] { Facing.N, Facing.E, Facing.S, Facing.W })
            {
                var w = side switch
                {
                    Facing.N => new Rect(x0, y0, x1, y0 + t),
                    Facing.S => new Rect(x0, y1 - t, x1, y1),
                    Facing.W => new Rect(x0, y0, x0 + t, y1),
                    _ => new Rect(x1 - t, y0, x1, y1),
                };
                if (side != room.facing)
                {
                    o.Add(w);
                    continue;
                }
                var door = DoorOf(room).at;
                double half = DOOR_WIDTH / 2;
                if (side == Facing.N || side == Facing.S)
                {
                    o.Add(new Rect(w.x0, w.y0, door.x - half, w.y1));
                    o.Add(new Rect(door.x + half, w.y0, w.x1, w.y1));
                }
                else
                {
                    o.Add(new Rect(w.x0, w.y0, w.x1, door.y - half));
                    o.Add(new Rect(w.x0, door.y + half, w.x1, w.y1));
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
            var o = new List<Placement>();
            var inner = new LocalBox { u0 = WALL + 0.02, v0 = WALL + 0.02, u1 = W - WALL - 0.02, v1 = D - WALL - 0.02 };
            var doorway = new LocalBox { u0 = W / 2 - 0.42, v0 = 0, u1 = W / 2 + 0.42, v1 = Math.Min(D * 0.45, 0.9) };
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
                    if (Overlaps(box, doorway)) return false;
                    foreach (var p in placed) if (!p.flat && Overlaps(p.box, box)) return false;
                }
                if (!opts.on) placed.Add((box, flat));
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
                    Put(Pick("radio", "computerScreen"), start + bar.w / 2, counterV - 0.02, Face.Front, new PutOpts { lift = bar.h, on = true });
                    var kp = fr.ToMap(start + (n * bar.w) / 2, counterV + bar.d / 2 + 0.16);
                    keeper = new KeeperSpot { x = kp.x, y = kp.y, rot = fr.RotFor(0, -1) };
                    AlongWall("back", Many(8, () => Pick("bookcaseOpen", "bookcaseClosedWide", "bookcaseOpen")), 0.02);
                    AlongWall("left", new[] { Pick("bookcaseOpen", "bookcaseClosedWide"), "cardboardBoxClosed", "bookcaseOpen" }, 0.05);
                    AlongWall("right", new[] { "bookcaseOpen", Pick("cardboardBoxOpen", "pottedPlant"), "bookcaseOpen" }, 0.05);
                    Put("pottedPlant", inner.u0 + 0.15, inner.v0 + 0.15, Face.Front);
                    Put("stoolBar", start + bar.w * 0.5, counterV - bar.d / 2 - 0.15, Face.Back);
                    break;
                }
                case HouseStyle.Office:
                {
                    keeper = null;
                    var bar = size("kitchenBar");
                    if (Put("kitchenBar", cu - 0.6, 1.0, Face.Front) && Put("kitchenBar", cu - 0.6 - bar.w, 1.0, Face.Front))
                    {
                        var kp = fr.ToMap(cu - 0.6 - bar.w / 2, 1.0 + bar.d / 2 + 0.16);
                        keeper = new KeeperSpot { x = kp.x, y = kp.y, rot = fr.RotFor(0, -1) };
                    }
                    var desk = size("desk");
                    for (double v = 1.6; v < D - WALL - desk.d; v += 0.85)
                    {
                        for (double u = inner.u0 + 0.1 + desk.w / 2; u < inner.u1 - desk.w / 2; u += desk.w + 0.35)
                        {
                            if (!Put("desk", u, v, Face.Back)) continue;
                            Put("computerScreen", u, v + 0.04, Face.Back, new PutOpts { lift = desk.h, on = true });
                            Put("chairDesk", u, v - desk.d / 2 - 0.12, Face.Back);
                        }
                    }
                    AlongWall("left", new[] { "bookcaseClosedWide", "pottedPlant" }, 0.4);
                    AlongWall("right", new[] { "pottedPlant", "bookcaseOpen" }, 0.4);
                    Put("trashcan", inner.u1 - 0.1, inner.v0 + 0.5, Face.Left);
                    break;
                }
                case HouseStyle.Workshop:
                {
                    AlongWall("back", Many(6, () => Pick("bookcaseOpen", "cardboardBoxClosed", "cardboardBoxOpen", "kitchenCabinetDrawer")), 0.02);
                    AlongWall("left", new[] { "table", "cardboardBoxClosed", "cardboardBoxOpen" }, 0.15);
                    AlongWall("right", new[] { Pick("bookcaseClosedWide", "kitchenCabinet"), "trashcan" }, 0.2);
                    if (Put("table", cu, cv, PickFace(Face.Front, Face.Back))) Put("cardboardBoxOpen", cu, cv, Face.Front, new PutOpts { lift = size("table").h, on = true });
                    Put("stoolBar", cu + 0.5, cv, Face.Left);
                    break;
                }
                case HouseStyle.Hall:
                case HouseStyle.Church:
                {
                    Put(room.style == HouseStyle.Church ? "table" : "tableRound", cu, inner.v1 - 0.4, Face.Front);
                    Put("pottedPlant", inner.u0 + 0.15, inner.v1 - 0.2, Face.Front);
                    Put("pottedPlant", inner.u1 - 0.15, inner.v1 - 0.2, Face.Front);
                    var bench = size("bench");
                    for (double v = inner.v1 - 1.1; v > 1.0; v -= bench.d + 0.32)
                        foreach (int side in new[] { -1, 1 })
                            for (int k = 0; k < 3; k++)
                                Put("bench", cu + side * (0.45 + bench.w * (k + 0.5)), v, Face.Back);
                    Put("rugRectangle", cu, D * 0.55, Face.Back, new PutOpts { flat = true });
                    break;
                }
                default:
                {
                    var kitchen = new List<string> { "kitchenFridge", "kitchenStove", "kitchenSink", "kitchenCabinet", "kitchenCabinetDrawer" };
                    bool left = rng.Chance(0.5);
                    var reversed = new List<string>(kitchen);
                    reversed.Reverse();
                    AlongWall("back", left ? kitchen : reversed, left ? 0 : 0.45);
                    string sofaWall = left ? "right" : "left";
                    if (AlongWall(sofaWall, new[] { "loungeSofa" }, 0.35) == 0) AlongWall(sofaWall, new[] { "loungeChair" }, 0.35);
                    AlongWall(sofaWall == "left" ? "right" : "left", new[] { Pick("bookcaseOpen", "bookcaseClosedWide"), "cabinetTelevision" }, 0.3);
                    Put(Pick("rugRectangle", "rugRound"), cu, cv, Face.Front, new PutOpts { flat = true });
                    string table = Pick("table", "tableRound");
                    if (Put(table, cu, cv, Face.Front))
                    {
                        var t = size(table);
                        string chair = Pick("chair", "chairCushion");
                        Put(chair, cu - t.w / 2 - 0.12, cv, Face.Right);
                        Put(chair, cu + t.w / 2 + 0.12, cv, Face.Left);
                        Put(chair, cu, cv + t.d / 2 + 0.12, Face.Front);
                    }
                    else Put("tableCoffee", cu, cv, Face.Front);
                    Put("pottedPlant", inner.u0 + 0.14, inner.v0 + 0.14, Face.Front);
                    Put("lampRoundFloor", inner.u1 - 0.12, inner.v0 + 0.12, Face.Front);
                    Put("coatRackStanding", cu + 0.62, inner.v0 + 0.16, Face.Front);
                    Put("plantSmall1", inner.u0 + 0.12, inner.v1 - 0.12, Face.Front);
                    break;
                }
            }
            return new RoomPlan { furniture = o, keeper = keeper };
        }
    }
}

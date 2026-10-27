// The rest of the town (world.ts): buildings on their footprints, ground
// floors and their rooms, doors, yards, trees, plaza decor, parked cars,
// lamps, street signs, river streaks and the fountain.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using UnityEngine;
using Grid = Porchlight.Core.Grid;
using Rect = Porchlight.Explore.Rect;

namespace Porchlight.Rendering
{
    /// <summary>Draws the street-sign atlas and the players' name tags (UiText.cs).</summary>
    public interface ITextMaker
    {
        /// <summary>Every name on a green blade, one cell each in a two-column atlas; cell(i) gives (u0, u1, v0, v1).</summary>
        Texture SignAtlas(IReadOnlyList<string> names, out Func<int, Vector4> cell);
        /// <summary>A name tag (pill and text) and its size in pixels.</summary>
        Texture NameTag(string text, out int width, out int height);
        void Release(Texture t);
    }

    /// <summary>A room's furnished ground floor: built the first time it's needed, kept, shown and hidden.</summary>
    public sealed class Interior
    {
        public readonly List<Drawable> parts = new List<Drawable>();
        /// <summary>The wall meshes by side, and how tall each is drawn (0..1, cut down facing the camera).</summary>
        public readonly Dictionary<Facing, Drawable> walls = new Dictionary<Facing, Drawable>();
        public readonly Dictionary<Facing, double> wallScale = new Dictionary<Facing, double>();
        public readonly List<Mesh> meshes = new List<Mesh>();
        bool visible = true;

        public bool Visible
        {
            get => visible;
            set
            {
                visible = value;
                foreach (var d in parts) d.visible = value;
            }
        }

        public void SetWallScale(Facing side, double s)
        {
            wallScale[side] = s;
            if (walls.TryGetValue(side, out var d)) d.Matrix = Matrix4x4.Scale(new Vector3(1, (float)s, 1));
        }

        public void Dispose(Stage stage)
        {
            foreach (var d in parts) stage.Remove(d);
            foreach (var m in meshes) UnityEngine.Object.Destroy(m);
        }
    }

    public sealed partial class TownWorld
    {
        static Vec2 Front(Facing f) => Interiors.Outward(f);

        void AddHouses(Rng rng)
        {
            for (int hi = 0; hi < layout.houses.Count; hi++)
            {
                var house = layout.houses[hi];
                int x0 = house.tiles.Min(t => t.x), y0 = house.tiles.Min(t => t.y);
                int x1 = house.tiles.Max(t => t.x) + 1, y1 = house.tiles.Max(t => t.y) + 1;
                var front = Front(house.facing);
                double rot = JsMath.Atan2(front.x, front.y) + BUILDING_TURN;
                bool alongX = house.facing == Facing.N || house.facing == Facing.S;
                double frontage = alongX ? x1 - x0 : y1 - y0;
                double depth = alongX ? y1 - y0 : x1 - x0;
                var candidates = ModelsFor(house);
                bool splittable = house.style == HouseStyle.House || house.style == HouseStyle.Shop || house.style == HouseStyle.Workshop || house.style == HouseStyle.Office;
                int pieces = splittable ? BestSplit(candidates, frontage, depth) : 1;
                double len = frontage / pieces;
                int colours = assets.VariationCount(candidates[0]);
                int variation = rng.Int(0, colours - 1);
                for (int k = 0; k < pieces; k++)
                {
                    string name = PickModel(candidates, len / depth, house, rng);
                    var prefab = assets.Variant(name, variation);
                    const double gap = 0.04;
                    double sx = (len - gap * 2) / prefab.sizeX;
                    double sz = (depth - gap * 2) / prefab.sizeZ;
                    double sy = Math.Sqrt(sx * sz) * HeightFactor(prefab, Math.Sqrt(sx * sz), house);
                    double along = (alongX ? x0 : y0) + (k + 0.5) * len;
                    var at = alongX ? new Vec2(along, (y0 + y1) / 2.0) : new Vec2((x0 + x1) / 2.0, along);
                    int index = instancer.Add(prefab, Placement(prefab, at.x, at.y, rot, sx, sy, sz));
                    double halfX = alongX ? len / 2 : depth / 2;
                    double halfY = alongX ? depth / 2 : len / 2;
                    boxes.Add(new Box { x0 = at.x - halfX, y0 = at.y - halfY, x1 = at.x + halfX, y1 = at.y + halfY, top = prefab.sizeY * sy });
                    shells.Add((prefab, index));
                    roomBuilding.Add(hi);
                    rooms.Add(new Room { id = rooms.Count, x0 = at.x - halfX + gap, y0 = at.y - halfY + gap, x1 = at.x + halfX - gap, y1 = at.y + halfY - gap, facing = house.facing, style = house.style });
                }
            }
        }

        static string[] ModelsFor(House house) =>
            house.style == HouseStyle.Office && house.floors >= 4 ? Catalog.TOWERS : Catalog.KindModels(house.style);

        int BestSplit(string[] candidates, double frontage, double depth)
        {
            int best = 1;
            double bestCost = double.PositiveInfinity;
            for (int n = 1; n <= 4; n++)
            {
                double len = frontage / n;
                if (n > 1 && len < 1.8) break;
                double fit = double.PositiveInfinity;
                foreach (var c in candidates) fit = Math.Min(fit, AspectMiss(assets.Get(c), len / depth));
                double cost = fit + (n - 1) * 0.12;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = n;
                }
            }
            return best;
        }

        static double AspectMiss(Prefab p, double wanted) => Math.Abs(Math.Log(p.sizeX / p.sizeZ / wanted));

        string PickModel(string[] candidates, double aspect, House house, Rng rng)
        {
            var scored = candidates.Select(c =>
            {
                var p = assets.Get(c);
                double tall = p.sizeY / Math.Sqrt(p.sizeX * p.sizeZ);
                return (c, cost: AspectMiss(p, aspect) + Math.Abs(tall - StoreyRatio(house)) * 0.3);
            }).ToList();
            Js.StableSort(scored, (a, b) => a.cost - b.cost);
            return rng.Pick(scored.GetRange(0, Math.Min(4, scored.Count))).c;
        }

        static double StoreyRatio(House house) => 0.45 + house.floors * 0.22;

        static double HeightFactor(Prefab p, double scale, House house)
        {
            double natural = p.sizeY * scale;
            double wanted = house.floors * FLOOR_H + (house.style == HouseStyle.House ? 0.9 : 0.4);
            return Math.Min(1.35, Math.Max(0.75, wanted / natural));
        }

        void Furnish(Rng rng)
        {
            FurnitureSize Size(string name)
            {
                var p = assets.Get("furniture/" + name);
                return new FurnitureSize { w = p.sizeX * Interiors.FURNITURE_SCALE, d = p.sizeZ * Interiors.FURNITURE_SCALE, h = p.sizeY * Interiors.FURNITURE_SCALE };
            }
            var solid = new List<Rect>();
            foreach (var room in rooms)
            {
                var plan = Interiors.FurnishRoom(room, Size, rng);
                plans.Add(plan);
                solid.AddRange(Interiors.RoomWalls(room));
                foreach (var f in plan.furniture) if (!f.flat && f.lift == 0) solid.Add(f.box);
            }
            solids = new Solids(solid, model.width);
        }

        void AddFacades(Rng rng)
        {
            var material = Mats.Standard(Color.white, null, 0.9, vertexColors: true, windows: true);
            int lastBuilding = -1;
            int colour = FACADES[0];
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                if (roomBuilding[i] != lastBuilding)
                {
                    lastBuilding = roomBuilding[i];
                    colour = FACADES[(int)Math.Floor(rng.Next() * FACADES.Length)];
                }
                var b = new MeshBuilder();
                double H = GROUND_FLOOR;
                int shade = Darker(colour, 0.78);
                var door = Interiors.DoorOf(room);
                foreach (var wl in Interiors.RoomWalls(room)) b.Box(wl.x0, wl.y0, wl.x1, wl.y1, 0, H, colour, shade);
                double half = Interiors.DOOR_WIDTH / 2;
                var corners = new List<Vec2>();
                foreach (int side in new[] { -1, 1 })
                    foreach (double inward in new[] { 0, Interiors.WALL })
                        corners.Add(new Vec2(door.at.x + door.along.x * side * half - door.outward.x * inward, door.at.y + door.along.y * side * half - door.outward.y * inward));
                b.Box(corners.Min(q => q.x), corners.Min(q => q.y), corners.Max(q => q.x), corners.Max(q => q.y), Interiors.DOOR_HEIGHT + 0.06, H, colour, shade);
                const double p = 0.015;
                int baseColour = Darker(colour, 0.6);
                double rx0 = room.x0 - p, ry0 = room.y0 - p, rx1 = room.x1 + p, ry1 = room.y1 + p;
                double t = Interiors.WALL + p;
                b.Box(rx0, ry0, rx1, ry0 + t, 0, 0.05, baseColour);
                b.Box(rx0, ry1 - t, rx1, ry1, 0, 0.05, baseColour);
                b.Box(rx0, ry0 + t, rx0 + t, ry1 - t, 0, 0.05, baseColour);
                b.Box(rx1 - t, ry0 + t, rx1, ry1 - t, 0, 0.05, baseColour);
                const double c = 0.035;
                b.Box(room.x0 - c, room.y0 - c, room.x1 + c, room.y1 + c, H - 0.05, H + 0.02, TRIM, 0x8c8a84);
                bool shop = room.style == HouseStyle.Shop || room.style == HouseStyle.Office;
                foreach (var side in new[] { Facing.N, Facing.E, Facing.S, Facing.W })
                {
                    bool isFront = side == room.facing;
                    var n = Interiors.Outward(side);
                    var alongV = new Vec2(Math.Abs(n.y), Math.Abs(n.x));
                    double len = alongV.x != 0 ? room.x1 - room.x0 : room.y1 - room.y0;
                    var origin = new Vec2(n.x > 0 ? room.x1 : room.x0, n.y > 0 ? room.y1 : room.y0);
                    bool wide = isFront && shop;
                    double width = wide ? 0.62 : 0.28;
                    double spacing = wide ? 0.82 : 0.6;
                    double sill = wide ? 0.12 : 0.26;
                    double top = 0.58;
                    int count = (int)Math.Floor((len - 0.3) / spacing);
                    if (count < 1) continue;
                    double start = (len - count * spacing) / 2 + spacing / 2;
                    for (int k = 0; k < count; k++)
                    {
                        double tt = start + k * spacing;
                        double cx = origin.x + alongV.x * tt;
                        double cy = origin.y + alongV.y * tt;
                        if (isFront && Js.Hypot(cx - door.at.x, cy - door.at.y) < half + width / 2 + 0.08) continue;
                        WindowPane(b, new Vec2(cx, cy), alongV, n, width, sill, top);
                    }
                }
                var mesh = b.Build("facade");
                meshes.Add(mesh);
                var d = stage.Add(new Drawable(mesh, material, Matrix4x4.identity, true));
                owned.Add(d);
                facades.Add(d);
            }
        }

        static void WindowPane(MeshBuilder b, Vec2 c, Vec2 along, Vec2 n, double width, double y0, double y1)
        {
            var face = new V3(n.x, 0, n.y);
            double seed = 1 + WindowTags.PaneHash(++paneCount);
            void Quad(double half, double lo, double hi, double outD, int color, bool glass)
            {
                double ox = c.x + n.x * outD;
                double oy = c.y + n.y * outD;
                b.Quad(v(ox - along.x * half, lo, oy - along.y * half), v(ox + along.x * half, lo, oy + along.y * half),
                    v(ox + along.x * half, hi, oy + along.y * half), v(ox - along.x * half, hi, oy - along.y * half), color, face, glass ? seed : 0);
            }
            Quad(width / 2 + 0.03, y0 - 0.03, y1 + 0.03, 0.004, TRIM, false);
            Quad(width / 2, y0, y1, 0.008, GLASS, true);
            Quad(width / 2 + 0.05, y0 - 0.05, y0 - 0.02, 0.012, 0xdcd8cf, false);
        }

        static readonly Dictionary<HouseStyle, int> FLOORS = new Dictionary<HouseStyle, int>
        {
            [HouseStyle.House] = 0xb98b5e,
            [HouseStyle.Shop] = 0xd9d2c5,
            [HouseStyle.Office] = 0xcfd3d6,
            [HouseStyle.Workshop] = 0x9a958c,
            [HouseStyle.Hall] = 0xa8794f,
            [HouseStyle.Church] = 0xa8794f,
        };

        /// <summary>The furnished ground floor of a room, built on first use and kept.</summary>
        public Interior InteriorOf(int id)
        {
            if (interiors.TryGetValue(id, out var it)) return it;
            it = new Interior();
            var room = rooms[id];
            var floor = new MeshBuilder();
            var inner = Interiors.InnerRect(room);
            floor.Rect(inner.x0, inner.y0, inner.x1, inner.y1, 0.012, FLOORS[room.style]);
            var floorMesh = floor.Build("floor");
            it.meshes.Add(floorMesh);
            it.parts.Add(stage.Add(new Drawable(floorMesh, interiorMaterial, Matrix4x4.identity, false)));
            var sides = new Dictionary<Facing, MeshBuilder>();
            var sideOrder = new List<Facing>();
            foreach (var wl in Interiors.RoomWalls(room))
            {
                var side = Interiors.WallSide(room, wl);
                if (!sides.TryGetValue(side, out var b))
                {
                    sides[side] = b = new MeshBuilder();
                    sideOrder.Add(side);
                }
                const double e = 0.006;
                b.Box(wl.x0 + e, wl.y0 + e, wl.x1 - e, wl.y1 - e, 0, Interiors.WALL_HEIGHT - e, 0xefe6d6, 0xb8ad9a);
            }
            foreach (var side in sideOrder)
            {
                var mesh = sides[side].Build("wall");
                it.meshes.Add(mesh);
                var d = stage.Add(new Drawable(mesh, interiorMaterial, Matrix4x4.identity, true));
                it.parts.Add(d);
                it.walls[side] = d;
                it.wallScale[side] = 1;
            }
            foreach (var p in plans[id].furniture)
            {
                var prefab = assets.Get("furniture/" + p.name);
                var m = Space.M(Placement(prefab, p.x, p.y, p.rot, Interiors.FURNITURE_SCALE, Interiors.FURNITURE_SCALE, Interiors.FURNITURE_SCALE, p.lift + 0.012));
                foreach (var part in prefab.parts) it.parts.Add(stage.Add(new Drawable(part.mesh, part.material, m, !p.flat)));
            }
            interiors[id] = it;
            return it;
        }

        void AddDoors(Rng rng)
        {
            var frames = new MeshBuilder();
            int count = Math.Max(1, rooms.Count);
            var panel = Primitives.Translate(Primitives.Box(Interiors.DOOR_WIDTH - 0.03, Interiors.DOOR_HEIGHT - 0.02, 0.035), (Interiors.DOOR_WIDTH - 0.03) / 2, (Interiors.DOOR_HEIGHT - 0.02) / 2, 0);
            var panelMesh = Primitives.ToMesh(panel, "door");
            meshes.Add(panelMesh);
            var panelMat = Mats.Lambert(Color.white, instanceColors: true);
            Mats.SetReceiveShadow(panelMat, false);
            var colors = new Vector4[count];
            var matrices = new Matrix4x4[count];
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                var d = Interiors.DoorOf(room);
                var at = d.at;
                var outward = d.outward;
                var along = d.along;
                const double post = 0.045;
                double depth = Interiors.WALL + 0.05;
                foreach (int side in new[] { -1, 1 })
                {
                    var c = new Vec2(at.x + along.x * side * (Interiors.DOOR_WIDTH / 2 + post / 2) - outward.x * (Interiors.WALL / 2), at.y + along.y * side * (Interiors.DOOR_WIDTH / 2 + post / 2) - outward.y * (Interiors.WALL / 2));
                    double hx = Math.Abs(along.x) * post / 2 + Math.Abs(outward.x) * depth / 2;
                    double hy = Math.Abs(along.y) * post / 2 + Math.Abs(outward.y) * depth / 2;
                    frames.Box(c.x - hx, c.y - hy, c.x + hx, c.y + hy, 0, Interiors.DOOR_HEIGHT + 0.04, 0xf2eee4);
                }
                var lc = new Vec2(at.x - outward.x * (Interiors.WALL / 2), at.y - outward.y * (Interiors.WALL / 2));
                double lx = Math.Abs(along.x) * (Interiors.DOOR_WIDTH / 2 + post) + Math.Abs(outward.x) * depth / 2;
                double ly = Math.Abs(along.y) * (Interiors.DOOR_WIDTH / 2 + post) + Math.Abs(outward.y) * depth / 2;
                frames.Box(lc.x - lx, lc.y - ly, lc.x + lx, lc.y + ly, Interiors.DOOR_HEIGHT - 0.01, Interiors.DOOR_HEIGHT + 0.06, 0xf2eee4);
                var hinge = new Vec2(at.x - along.x * (Interiors.DOOR_WIDTH / 2 - 0.01) - outward.x * (Interiors.WALL / 2), at.y - along.y * (Interiors.DOOR_WIDTH / 2 - 0.01) - outward.y * (Interiors.WALL / 2));
                double shut = JsMath.Atan2(-along.y, along.x);
                int swing = -Math.Sin(shut) * -outward.x + -Math.Cos(shut) * -outward.y > 0 ? 1 : -1;
                doors.Add(new DoorState { hinge = hinge, shut = shut, swing = swing, open = 0 });
                var col = Space.Hex(DOOR_COLOURS[(int)Math.Floor(rng.Next() * DOOR_COLOURS.Length)]);
                colors[i] = new Vector4(col.r, col.g, col.b, 1);
            }
            doorPanels = stage.Add(new Drawable(panelMesh, panelMat, matrices, rooms.Count, true, colors));
            owned.Add(doorPanels);
            for (int i = 0; i < rooms.Count; i++) SwingDoor(i);
            var frameMesh = frames.Build("door frames");
            meshes.Add(frameMesh);
            owned.Add(stage.Add(new Drawable(frameMesh, lambert, Matrix4x4.identity, true)));
        }

        /// <summary>Set door i's panel to its current opening.</summary>
        public void SwingDoor(int i)
        {
            var d = doors[i];
            double eased = d.open * d.open * (3 - 2 * d.open);
            var m = Mat4.Compose(d.hinge.x, 0.01, d.hinge.y, Quat.AboutY(d.shut + d.swing * eased * DOOR_SWING), 1, 1, 1);
            doorPanels.matrices[i] = Space.M(m);
        }

        void AddYards(MeshBuilder sheds, Rng rng)
        {
            int w = model.width, h = model.height;
            var crops = Catalog.CROPS.Select(c => assets.Get(c)).ToArray();
            var props = new[] { assets.Get("nature/log_stack"), assets.Get("nature/pot_large") };
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte use = layout.use[Grid.Index(x, y, w)];
                    if (use == Use.GARDEN || use == Use.COMMUNITY_GARDEN)
                    {
                        double roll = rng.Next();
                        if (use == Use.COMMUNITY_GARDEN && roll < 0.15)
                        {
                            var p = props[(int)Math.Floor(rng.Next() * props.Length)];
                            double s = 0.7 / Math.Max(p.sizeX, p.sizeZ);
                            instancer.Add(p, Placement(p, x + 0.5, y + 0.5, rng.Range(0, Math.PI * 2), s, s, s));
                            continue;
                        }
                        var crop = crops[(int)Math.Floor(rng.Next() * crops.Length)];
                        double cs = 0.42 / Math.Max(crop.sizeX, crop.sizeZ);
                        foreach (var (ox, oy) in new[] { (0.27, 0.27), (0.73, 0.27), (0.27, 0.73), (0.73, 0.73) })
                        {
                            double r = rng.Range(0, Math.PI * 2);
                            double sy = cs * rng.Range(0.85, 1.15);
                            instancer.Add(crop, Placement(crop, x + ox, y + oy, r, cs, sy, cs), false);
                        }
                    }
                    else if (use == Use.SHED) AddShed(sheds, x + 0.5, y + 0.5, rng.Chance(0.5));
                }
            }
            var fence = assets.Get("suburban/fence");
            double fsx = 1 / fence.sizeX;
            foreach (var f in layout.fences)
            {
                const double inset = 0.05;
                var at = f.side switch
                {
                    Facing.N => new Vec2(f.x + 0.5, f.y + inset),
                    Facing.S => new Vec2(f.x + 0.5, f.y + 1 - inset),
                    Facing.W => new Vec2(f.x + inset, f.y + 0.5),
                    _ => new Vec2(f.x + 1 - inset, f.y + 0.5),
                };
                double rot = f.side == Facing.N || f.side == Facing.S ? 0 : Math.PI / 2;
                instancer.Add(fence, Placement(fence, at.x, at.y, rot, fsx, 1.3, 1.3));
            }
        }

        static void AddShed(MeshBuilder b, double cx, double cy, bool turned)
        {
            double hw = turned ? 0.3 : 0.36;
            double hd = turned ? 0.36 : 0.3;
            const double wall = 0.42;
            const double ridge = 0.64;
            b.Box(cx - hw, cy - hd, cx + hw, cy + hd, 0, wall, C.shedWall);
            const double o = 0.05;
            if (turned)
            {
                b.Quad(v(cx - hw - o, wall - 0.03, cy - hd - o), v(cx, ridge, cy - hd - o), v(cx, ridge, cy + hd + o), v(cx - hw - o, wall - 0.03, cy + hd + o), C.shedRoof, new V3(-1, 1, 0));
                b.Quad(v(cx + hw + o, wall - 0.03, cy - hd - o), v(cx, ridge, cy - hd - o), v(cx, ridge, cy + hd + o), v(cx + hw + o, wall - 0.03, cy + hd + o), C.shedRoof, new V3(1, 1, 0));
                foreach (double y in new[] { cy - hd, cy + hd })
                    b.Quad(v(cx - hw, wall, y), v(cx, ridge - 0.02, y), v(cx, ridge - 0.02, y), v(cx + hw, wall, y), C.shedWall, new V3(0, 0, y < cy ? -1 : 1));
            }
            else
            {
                b.Quad(v(cx - hw - o, wall - 0.03, cy - hd - o), v(cx - hw - o, ridge, cy), v(cx + hw + o, ridge, cy), v(cx + hw + o, wall - 0.03, cy - hd - o), C.shedRoof, new V3(0, 1, -1));
                b.Quad(v(cx - hw - o, wall - 0.03, cy + hd + o), v(cx - hw - o, ridge, cy), v(cx + hw + o, ridge, cy), v(cx + hw + o, wall - 0.03, cy + hd + o), C.shedRoof, new V3(0, 1, 1));
                foreach (double x in new[] { cx - hw, cx + hw })
                    b.Quad(v(x, wall, cy - hd), v(x, ridge - 0.02, cy), v(x, ridge - 0.02, cy), v(x, wall, cy + hd), C.shedWall, new V3(x < cx ? -1 : 1, 0, 0));
            }
            const int door = 0x4a3426;
            if (turned) b.Quad(v(cx + hw + 0.005, 0, cy - 0.1), v(cx + hw + 0.005, 0, cy + 0.1), v(cx + hw + 0.005, 0.32, cy + 0.1), v(cx + hw + 0.005, 0.32, cy - 0.1), door, new V3(1, 0, 0));
            else b.Quad(v(cx - 0.1, 0, cy + hd + 0.005), v(cx + 0.1, 0, cy + hd + 0.005), v(cx + 0.1, 0.32, cy + hd + 0.005), v(cx - 0.1, 0.32, cy + hd + 0.005), door, new V3(0, 0, 1));
        }

        void AddTrees(Rng rng)
        {
            var tiles = model.tiles;
            int w = model.width, h = model.height;
            var trees = Catalog.TREES.Select(t => assets.Get(t)).ToArray();
            var bushes = Catalog.BUSHES.Select(t => assets.Get(t)).ToArray();
            var flowers = Catalog.FLOWERS.Select(t => assets.Get(t)).ToArray();
            var rock = assets.Get("nature/rock_smallA");
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    byte use = layout.use[i];
                    byte terrain = tiles.terrain[i];
                    if (tiles.overlay[i] != Overlay.NONE) continue;
                    if (use == Use.HOUSE || use == Use.SHED || use == Use.GARDEN || use == Use.COMMUNITY_GARDEN) continue;
                    if (terrain == Terrain.ROCK)
                    {
                        if (rng.Chance(0.22))
                        {
                            double s = rng.Range(1.2, 2.4);
                            double ax = x + rng.Range(0.25, 0.75);
                            double ay = y + rng.Range(0.25, 0.75);
                            instancer.Add(rock, Placement(rock, ax, ay, rng.Range(0, 6.3), s, s, s));
                        }
                        continue;
                    }
                    if (terrain != Terrain.GRASS) continue;
                    bool nearStreet = false;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (!Grid.InBounds(nx, ny, w, h)) continue;
                        int j = Grid.Index(nx, ny, w);
                        if (tiles.overlay[j] != Overlay.NONE || IsWaterTerrain(tiles.terrain[j]) || layout.use[j] == Use.HOUSE) nearStreet = true;
                    }
                    bool inTown = Js.Hypot(x + 0.5 - model.center.x, y + 0.5 - model.center.y) < model.townRadius;
                    double chance = use == Use.YARD ? (nearStreet ? 0.04 : 0.14) : use == Use.GREEN ? 0.28 : nearStreet ? 0.03 : inTown ? 0.1 : 0.22;
                    double roll = rng.Next();
                    double atX = x + 0.5 + rng.Range(-0.25, 0.25);
                    double atY = y + 0.5 + rng.Range(-0.25, 0.25);
                    double spin = rng.Range(0, Math.PI * 2);
                    if (roll < chance)
                    {
                        var pool = inTown ? trees.Where((_, k) => k < 4 || k > 6).ToArray() : trees;
                        var t = pool[(int)Math.Floor(rng.Next() * pool.Length)];
                        double s = rng.Range(1.3, 2.1) / t.sizeY;
                        double sy = s * rng.Range(0.9, 1.1);
                        instancer.Add(t, Placement(t, atX, atY, spin, s, sy, s));
                    }
                    else if (roll < chance + (use == Use.YARD || use == Use.GREEN ? 0.08 : 0.03))
                    {
                        var b = bushes[(int)Math.Floor(rng.Next() * bushes.Length)];
                        double s = rng.Range(0.4, 0.65) / Math.Max(b.sizeX, b.sizeZ);
                        instancer.Add(b, Placement(b, atX, atY, spin, s, s, s), false);
                    }
                    else if (roll < chance + 0.14 && (use == Use.YARD || use == Use.GREEN))
                    {
                        var f = flowers[(int)Math.Floor(rng.Next() * flowers.Length)];
                        for (int k = 0; k < 3; k++)
                        {
                            const double s = 1.2;
                            double fx = atX + rng.Range(-0.25, 0.25);
                            double fy = atY + rng.Range(-0.25, 0.25);
                            instancer.Add(f, Placement(f, fx, fy, rng.Range(0, 6.3), s, s, s), false);
                        }
                    }
                }
            }
        }

        void AddPlazaDecor(Rng rng)
        {
            var tiles = model.tiles;
            int w = model.width, h = model.height;
            var planter = assets.Get("suburban/planter");
            var tree = assets.Get("nature/tree_small");
            var well = model.landmarks.Find(l => l.kind == LandmarkKind.Well);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (tiles.overlay[Grid.Index(x, y, w)] != Overlay.PLAZA) continue;
                    if (well != null && Math.Floor(well.at.x) == x && Math.Floor(well.at.y) == y) continue;
                    var edges = Grid.DIRS4.Where(d => !Grid.InBounds(x + d.dx, y + d.dy, w, h) || tiles.overlay[Grid.Index(x + d.dx, y + d.dy, w)] != Overlay.PLAZA).ToList();
                    double roll = rng.Next();
                    if (edges.Count == 0 || roll > 0.3) continue;
                    var (dx, dy) = edges[0];
                    double ax = x + 0.5 + dx * 0.3, ay = y + 0.5 + dy * 0.3;
                    if (roll < 0.2)
                    {
                        double s = 0.8 / Math.Max(planter.sizeX, planter.sizeZ);
                        instancer.Add(planter, Placement(planter, ax, ay, JsMath.Atan2(dx, dy), s, s, s));
                    }
                    else
                    {
                        double s = 1.4 / tree.sizeY;
                        instancer.Add(tree, Placement(tree, ax, ay, rng.Range(0, 6.3), s, s, s));
                    }
                }
            }
        }

        void AddParkedCars()
        {
            foreach (var car in layout.cars)
            {
                double heading = car.angle + (car.variant % 2 != 0 ? Math.PI : 0);
                int m = car.variant % 6;
                var p = assets.Get(Catalog.CARS[m]);
                double s = CarScale(p);
                parked.Add(new ParkedPose { x = car.x, y = car.y, heading = heading, model = m });
                parkedCopies.Add((p, instancer.Add(p, Placement(p, car.x, car.y, JsMath.Atan2(Math.Cos(heading), Math.Sin(heading)), s, s, s))));
            }
        }

        void AddLamps(Rng rng)
        {
            var placed = new List<Vec2>();
            var posts = new List<Matrix4x4>();
            for (int n = 0; n < layout.sidewalks.paths.Count; n++)
            {
                var path = layout.sidewalks.paths[n];
                double total = path.Length;
                for (double s = (n % 2) * (LAMP_SPACING / 2) + 1.5; s < total - 1; s += LAMP_SPACING)
                {
                    int k = 1;
                    while (k < path.at.Count - 1 && path.at[k] < s) k++;
                    var a = path.points[k - 1];
                    var b = path.points[k];
                    double f = (s - path.at[k - 1]) / Math.Max(1e-6, path.at[k] - path.at[k - 1]);
                    var on = new Vec2(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f);
                    if (placed.Any(q => Geo.Dist(q, on) < 2.5)) continue;
                    int tx = (int)Math.Floor(on.x), ty = (int)Math.Floor(on.y);
                    if (!Grid.InBounds(tx, ty, model.width, model.height) || IsWaterTerrain(model.tiles.terrain[Grid.Index(tx, ty, model.width)])) continue;
                    if (layout.use[Grid.Index(tx, ty, model.width)] == Use.HOUSE) continue;
                    placed.Add(on);
                    double tgx = b.x - a.x, tgy = b.y - a.y;
                    double tl = Js.Hypot(tgx, tgy);
                    if (tl == 0) tl = 1;
                    var normal = new Vec2((tgy / tl) * path.side, (-tgx / tl) * path.side);
                    var basePt = new Vec2(on.x + normal.x * (Layout.SIDEWALK / 2 - 0.08), on.y + normal.y * (Layout.SIDEWALK / 2 - 0.08));
                    double rot = JsMath.Atan2(-normal.x, -normal.y);
                    posts.Add(Space.M(Mat4.Compose(basePt.x, 0, basePt.y, Quat.AboutY(rot), 1, 1, 1)));
                    lamps.Add(new Lamp { x = basePt.x - normal.x * LAMP_REACH, y = basePt.y - normal.y * LAMP_REACH, h = LAMP_HEIGHT - 0.05, phase = rng.Range(0, 100), faulty = rng.Chance(0.12) });
                }
            }

            var postMesh = Primitives.ToMesh(LampGeometry(), "lamp post");
            meshes.Add(postMesh);
            var postMat = Mats.Lambert(Space.Hex(C.lampPost));
            Mats.SetReceiveShadow(postMat, false);
            owned.Add(stage.Add(new Drawable(postMesh, postMat, posts.ToArray(), posts.Count, true)));

            int count = Math.Max(1, lamps.Count);
            var bulbMesh = Primitives.ToMesh(Primitives.Sphere(0.05, 10, 6), "bulb");
            meshes.Add(bulbMesh);
            var bulbMat = Mats.Basic(Color.white, instanceColors: true);
            var bulbMatrices = new Matrix4x4[count];
            var poolMatrices = new Matrix4x4[count];
            for (int i = 0; i < lamps.Count; i++)
            {
                var l = lamps[i];
                bulbMatrices[i] = Matrix4x4.Translate(Space.Map(l.x, l.y, l.h));
                poolMatrices[i] = Matrix4x4.Translate(Space.Map(l.x, l.y, 0.04));
            }
            var off = Space.Hex(0x8a8a80);
            var bulbColors = Enumerable.Repeat(new Vector4(off.r, off.g, off.b, 1), count).ToArray();
            bulbs = stage.Add(new Drawable(bulbMesh, bulbMat, bulbMatrices, lamps.Count, false, bulbColors));
            owned.Add(bulbs);
            var poolMesh = Primitives.ToMesh(Primitives.RotateX(Primitives.Plane(2.2, 2.2), -Math.PI / 2), "pool");
            meshes.Add(poolMesh);
            var poolMat = Mats.Basic(Color.white, Blending.Additive, false, map: Primitives.Glow, instanceColors: true, renderOrder: 6);
            pools = stage.Add(new Drawable(poolMesh, poolMat, poolMatrices, lamps.Count, false, new Vector4[count]));
            owned.Add(pools);
            halos = new Halos(lamps.Count, 0.9, 7, stage);
            owned.Add(halos.drawable);
            for (int i = 0; i < lamps.Count; i++) halos.Set(i, Space.Map(lamps[i].x, lamps[i].y, lamps[i].h), 0, 0, 0);
            halos.Commit(lamps.Count);
        }

        /// <summary>A street lamp: a post, an arm reaching out along +Z, and a lamp head.</summary>
        static ModelPart LampGeometry()
        {
            var post = Primitives.Translate(Primitives.Cylinder(0.022, 0.03, LAMP_HEIGHT, 6), 0, LAMP_HEIGHT / 2, 0).NonIndexed();
            var arm = Primitives.Translate(Primitives.Box(0.03, 0.03, LAMP_REACH), 0, LAMP_HEIGHT - 0.01, LAMP_REACH / 2).NonIndexed();
            var head = Primitives.Translate(Primitives.Box(0.09, 0.04, 0.12), 0, LAMP_HEIGHT - 0.02, LAMP_REACH).NonIndexed();
            return Primitives.Merge(post, arm, head);
        }

        void AddStreetSigns(ITextMaker text)
        {
            if (signs.Count == 0 || text == null) return;
            var atlas = text.SignAtlas(names.streets.Select(s => s.name).ToList(), out var cell);
            int tallest = signs.Max(s => s.blades.Count);
            double postHeight = Signs.BLADE_Y + (tallest - 1) * Signs.BLADE_STEP + Signs.BLADE_HEIGHT / 2 + 0.03;
            var postMesh = Primitives.ToMesh(Primitives.Translate(Primitives.Cylinder(Signs.POST_RADIUS, Signs.POST_RADIUS * 1.2, postHeight, 6), 0, postHeight / 2, 0), "sign post");
            meshes.Add(postMesh);
            var postMat = Mats.Lambert(Space.Hex(0x5b6067));
            Mats.SetReceiveShadow(postMat, false);
            var postMatrices = signs.Select(s => Matrix4x4.Translate(Space.Map(s.at.x, s.at.y, 0))).ToArray();
            owned.Add(stage.Add(new Drawable(postMesh, postMat, postMatrices, postMatrices.Length, true)));

            var pos = new List<float>();
            var nor = new List<float>();
            var uv = new List<float>();
            foreach (var s in signs)
            {
                for (int k = 0; k < s.blades.Count; k++)
                {
                    var c = cell(s.blades[k].street);
                    Blade(pos, nor, uv, s.at, s.blades[k].along, Signs.BLADE_Y + k * Signs.BLADE_STEP, c);
                }
            }
            var part = new ModelPart { pos = pos.ToArray(), normal = nor.ToArray(), uv = uv.ToArray() };
            // Blade UVs are already in the atlas's terms (v up), so skip ToMesh's glTF flip.
            for (int i = 1; i < part.uv.Length; i += 2) part.uv[i] = 1 - part.uv[i];
            var bladeMesh = Primitives.ToMesh(part, "sign blades");
            meshes.Add(bladeMesh);
            var bladeMat = Mats.Lambert(Color.white, map: atlas);
            Mats.SetReceiveShadow(bladeMat, false);
            owned.Add(stage.Add(new Drawable(bladeMesh, bladeMat, Matrix4x4.identity, true)));
        }

        static void Blade(List<float> pos, List<float> nor, List<float> uv, Vec2 at, Vec2 d, double y, Vector4 cell)
        {
            var n = new Vec2(-d.y, d.x);
            double hl = Signs.BLADE_LENGTH / 2;
            double hh = Signs.BLADE_HEIGHT / 2;
            double ht = Signs.BLADE_THICK / 2;
            double outD = Signs.POST_RADIUS + Signs.BLADE_GAP + hl;
            double[] P(double a, double b, double c) => new[] { at.x + d.x * (outD + a) + n.x * b, y + c, at.y + d.y * (outD + a) + n.y * b };
            void Quad(double[][] p, double[] normal, double[][] uvs)
            {
                foreach (int k in new[] { 0, 1, 2, 0, 2, 3 })
                {
                    pos.Add((float)p[k][0]);
                    pos.Add((float)p[k][1]);
                    pos.Add((float)p[k][2]);
                    nor.Add((float)normal[0]);
                    nor.Add((float)normal[1]);
                    nor.Add((float)normal[2]);
                    uv.Add((float)uvs[k][0]);
                    uv.Add((float)uvs[k][1]);
                }
            }
            double u0 = cell.x, u1 = cell.y, v0 = cell.z, v1 = cell.w;
            var nn = new[] { n.x, 0, n.y };
            var nb = new[] { -n.x, 0, -n.y };
            var faceUv = new[] { new[] { u0, v0 }, new[] { u1, v0 }, new[] { u1, v1 }, new[] { u0, v1 } };
            Quad(new[] { P(-hl, ht, -hh), P(hl, ht, -hh), P(hl, ht, hh), P(-hl, ht, hh) }, nn, faceUv);
            Quad(new[] { P(hl, -ht, -hh), P(-hl, -ht, -hh), P(-hl, -ht, hh), P(hl, -ht, hh) }, nb, faceUv);
            var e = new[] { u0 + 0.001, v1 - 0.001 };
            var edge = new[] { e, e, e, e };
            Quad(new[] { P(-hl, -ht, hh), P(-hl, ht, hh), P(hl, ht, hh), P(hl, -ht, hh) }, new double[] { 0, 1, 0 }, edge);
            Quad(new[] { P(-hl, ht, -hh), P(-hl, -ht, -hh), P(hl, -ht, -hh), P(hl, ht, -hh) }, new double[] { 0, -1, 0 }, edge);
            Quad(new[] { P(hl, ht, -hh), P(hl, -ht, -hh), P(hl, -ht, hh), P(hl, ht, hh) }, new[] { d.x, 0, d.y }, edge);
            Quad(new[] { P(-hl, -ht, -hh), P(-hl, ht, -hh), P(-hl, ht, hh), P(-hl, -ht, hh) }, new[] { -d.x, 0, -d.y }, edge);
        }

        void AddStreaks()
        {
            bool Wet(Vec2 p) => IsWaterTerrain(model.tiles.terrain[(int)Math.Floor(p.y) * model.width + (int)Math.Floor(p.x)]);
            foreach (var river in model.rivers)
            {
                var path = river.points.Where(Wet).ToList();
                if (path.Count < 2) continue;
                var at = new List<double> { 0 };
                for (int k = 1; k < path.Count; k++) at.Add(at[k - 1] + Js.Hypot(path[k].x - path[k - 1].x, path[k].y - path[k - 1].y));
                double total = at[at.Count - 1];
                int count = (int)Math.Floor(total / 1.6);
                for (int k = 0; k < count; k++) streaks.Add(new Streak { path = path, at = at, s = ((double)k / count) * total + (k % 3) * 0.37, speed = 1.1 + (k % 5) * 0.15 });
            }
            var mesh = Primitives.ToMesh(Primitives.RotateX(Primitives.Plane(0.55, 0.05), -Math.PI / 2), "streak");
            meshes.Add(mesh);
            var mat = Mats.Basic(Color.white, Blending.Additive, false, renderOrder: 5, instanceColors: true);
            int n = Math.Max(1, streaks.Count);
            streaksDrawable = stage.Add(new Drawable(mesh, mat, new Matrix4x4[n], streaks.Count, false, new Vector4[n]));
            owned.Add(streaksDrawable);
        }

        void AddFountain()
        {
            var well = model.landmarks.Find(l => l.kind == LandmarkKind.Well);
            if (well == null) return;
            fountainAt = new Vec2(Math.Floor(well.at.x) + 0.5, Math.Floor(well.at.y) + 0.5);
            var stone = Primitives.Translate(Primitives.Merge(
                Primitives.Translate(Primitives.Cylinder(0.44, 0.47, 0.2, 20), 0, 0.1, 0),
                Primitives.Translate(Primitives.Cylinder(0.06, 0.08, 0.5, 8), 0, 0.25, 0),
                Primitives.Translate(Primitives.Cylinder(0.17, 0.1, 0.06, 12), 0, 0.5, 0)), fountainAt.x, 0, fountainAt.y);
            var waterPart = Primitives.Translate(Primitives.Cylinder(0.39, 0.39, 0.02, 20), fountainAt.x, 0.19, fountainAt.y);
            var stoneMesh = Primitives.ToMesh(stone, "fountain");
            var waterMesh = Primitives.ToMesh(waterPart, "fountain water");
            meshes.Add(stoneMesh);
            meshes.Add(waterMesh);
            owned.Add(stage.Add(new Drawable(stoneMesh, Mats.Lambert(Space.Hex(C.stone)), Matrix4x4.identity, true)));
            var waterMat = Mats.Lambert(Space.Hex(0x7fbde3));
            waterMat.SetColor("_Emissive", Space.Hex(0x1d3f5c));
            Mats.SetReceiveShadow(waterMat, false);
            owned.Add(stage.Add(new Drawable(waterMesh, waterMat, Matrix4x4.identity, false)));
            var dropMesh = Primitives.ToMesh(Primitives.Sphere(0.025, 6, 4), "drop");
            meshes.Add(dropMesh);
            var dropColor = Space.Hex(0xd8f0ff);
            dropColor.a = 0.85f;
            var dropMat = Mats.Basic(dropColor, Blending.Normal, true);
            for (int k = 0; k < 24; k++) droplets.Add(new Droplet { t = k / 24.0, vx = Math.Cos(k * 2.4) * 0.3, vy = Math.Sin(k * 2.4) * 0.3 });
            drops = stage.Add(new Drawable(dropMesh, dropMat, new Matrix4x4[24], 24, false));
            owned.Add(drops);
        }
    }
}

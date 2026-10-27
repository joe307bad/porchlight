// The rest of the town: buildings on their footprints, ground floors and
// their rooms, doors, yards, trees, plaza decor, parked cars, lamps, street
// signs and the fountain.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using Grid = Porchlight.Core.Grid;
using Rect = Porchlight.Explore.Rect;

namespace Porchlight.Rendering
{
    /// <summary>A room's furnished ground floor: built the first time it's needed, kept, shown and hidden.</summary>
    public sealed class Interior
    {
        public readonly GameObject root;
        /// <summary>The walls by side, and how tall each is drawn (0..1, cut down facing the camera).</summary>
        public readonly Dictionary<Facing, Transform> walls = new Dictionary<Facing, Transform>();
        public readonly Dictionary<Facing, double> wallScale = new Dictionary<Facing, double>();

        public Interior(Transform parent, string name)
        {
            root = new GameObject(name);
            root.transform.SetParent(parent, false);
        }

        public bool Visible
        {
            get => root.activeSelf;
            set => root.SetActive(value);
        }

        public void SetWallScale(Facing side, double s)
        {
            wallScale[side] = s;
            if (walls.TryGetValue(side, out var t)) t.localScale = new Vector3(1, (float)s, 1);
        }

        public void Dispose() => UnityEngine.Object.Destroy(root);
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
                double rot = DetMath.Atan2(front.x, front.y) + BUILDING_TURN;
                bool alongX = house.facing == Facing.N || house.facing == Facing.S;
                double frontage = alongX ? x1 - x0 : y1 - y0;
                double depth = alongX ? y1 - y0 : x1 - x0;
                var candidates = ModelsFor(house);
                bool splittable = house.style == HouseStyle.House || house.style == HouseStyle.Shop || house.style == HouseStyle.Workshop || house.style == HouseStyle.Office;
                int pieces = splittable ? BestSplit(candidates, frontage, depth) : 1;
                double len = frontage / pieces;
                int colours = Catalog.VariationCount(candidates[0]);
                int variation = rng.Int(0, colours - 1);
                for (int k = 0; k < pieces; k++)
                {
                    var m = library.Get(PickModel(candidates, len / depth, house, rng));
                    const double gap = 0.04;
                    double sx = (len - gap * 2) / m.size.x;
                    double sz = (depth - gap * 2) / m.size.z;
                    double sy = Math.Sqrt(sx * sz) * HeightFactor(m, Math.Sqrt(sx * sz), house);
                    double along = (alongX ? x0 : y0) + (k + 0.5) * len;
                    var at = alongX ? new Vec2(along, (y0 + y1) / 2.0) : new Vec2((x0 + x1) / 2.0, along);
                    var shell = Props.Place(m, still, at.x, at.y, rot, sx, sy, sz, variation: variation, windows: true);
                    // The building's own ground floor gives way to the one built below (doors, rooms).
                    Slice.Above(shell, (float)GROUND_FLOOR);
                    double halfX = alongX ? len / 2 : depth / 2;
                    double halfY = alongX ? depth / 2 : len / 2;
                    boxes.Add(new Box { x0 = at.x - halfX, y0 = at.y - halfY, x1 = at.x + halfX, y1 = at.y + halfY, top = m.size.y * sy });
                    shells.Add(shell);
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
                foreach (var c in candidates) fit = Math.Min(fit, AspectMiss(library.Get(c), len / depth));
                double cost = fit + (n - 1) * 0.12;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = n;
                }
            }
            return best;
        }

        static double AspectMiss(ModelLibrary.Model m, double wanted) => Math.Abs(Math.Log(m.size.x / m.size.z / wanted));

        string PickModel(string[] candidates, double aspect, House house, Rng rng)
        {
            var scored = candidates.Select(c =>
            {
                var m = library.Get(c);
                double tall = m.size.y / Math.Sqrt(m.size.x * m.size.z);
                return (c, cost: AspectMiss(m, aspect) + Math.Abs(tall - StoreyRatio(house)) * 0.3);
            }).ToList();
            Numbers.StableSort(scored, (a, b) => a.cost - b.cost);
            return rng.Pick(scored.GetRange(0, Math.Min(4, scored.Count))).c;
        }

        static double StoreyRatio(House house) => 0.45 + house.floors * 0.22;

        static double HeightFactor(ModelLibrary.Model m, double scale, House house)
        {
            double natural = m.size.y * scale;
            double wanted = house.floors * FLOOR_H + (house.style == HouseStyle.House ? 0.9 : 0.4);
            return Math.Min(1.35, Math.Max(0.75, wanted / natural));
        }

        void Furnish(Rng rng)
        {
            FurnitureSize Size(string name)
            {
                var m = library.Get("furniture/" + name);
                return new FurnitureSize { w = m.size.x * Interiors.FURNITURE_SCALE, d = m.size.z * Interiors.FURNITURE_SCALE, h = m.size.y * Interiors.FURNITURE_SCALE };
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

        /// <summary>Each room's outside walls: the building's ground floor, its door opening, windows and trim.</summary>
        void AddFacades(Rng rng)
        {
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
                var b = new MeshBuilder(palette);
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
                        if (isFront && Numbers.Hypot(cx - door.at.x, cy - door.at.y) < half + width / 2 + 0.08) continue;
                        WindowPane(b, new Vec2(cx, cy), alongV, n, width, sill, top);
                    }
                }
                facades.Add(b.Build("facade", still));
            }
        }

        static void WindowPane(MeshBuilder b, Vec2 c, Vec2 along, Vec2 n, double width, double y0, double y1)
        {
            var face = new V3(n.x, 0, n.y);
            void Quad(double half, double lo, double hi, double outD, int color, int glow)
            {
                double ox = c.x + n.x * outD;
                double oy = c.y + n.y * outD;
                b.Quad(v(ox - along.x * half, lo, oy - along.y * half), v(ox + along.x * half, lo, oy + along.y * half),
                    v(ox + along.x * half, hi, oy + along.y * half), v(ox - along.x * half, hi, oy - along.y * half), color, face, glow);
            }
            Quad(width / 2 + 0.03, y0 - 0.03, y1 + 0.03, 0.004, TRIM, 0);
            Quad(width / 2, y0, y1, 0.008, GLASS, WINDOW_LIGHT);
            Quad(width / 2 + 0.05, y0 - 0.05, y0 - 0.02, 0.012, 0xdcd8cf, 0);
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
            it = new Interior(root.transform, "room " + id);
            var room = rooms[id];
            var floor = new MeshBuilder(palette);
            var inner = Interiors.InnerRect(room);
            floor.Rect(inner.x0, inner.y0, inner.x1, inner.y1, 0.012, FLOORS[room.style]);
            floor.Build("floor", it.root.transform, false);
            var sides = new Dictionary<Facing, MeshBuilder>();
            var sideOrder = new List<Facing>();
            foreach (var wl in Interiors.RoomWalls(room))
            {
                var side = Interiors.WallSide(room, wl);
                if (!sides.TryGetValue(side, out var b))
                {
                    sides[side] = b = new MeshBuilder(palette);
                    sideOrder.Add(side);
                }
                const double e = 0.006;
                b.Box(wl.x0 + e, wl.y0 + e, wl.x1 - e, wl.y1 - e, 0, Interiors.WALL_HEIGHT - e, 0xefe6d6, 0xb8ad9a);
            }
            foreach (var side in sideOrder)
            {
                it.walls[side] = sides[side].Build("wall " + side, it.root.transform).transform;
                it.wallScale[side] = 1;
            }
            foreach (var p in plans[id].furniture)
                Props.Place(library.Get("furniture/" + p.name), it.root.transform, p.x, p.y, p.rot, Interiors.FURNITURE_SCALE, Interiors.FURNITURE_SCALE, Interiors.FURNITURE_SCALE, p.lift + 0.012, castShadows: !p.flat);
            interiors[id] = it;
            return it;
        }

        void AddDoors(Rng rng)
        {
            var frames = new MeshBuilder(palette);
            var doorsRoot = new GameObject("doors").transform;
            doorsRoot.SetParent(root.transform, false);
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
                double shut = DetMath.Atan2(-along.y, along.x);
                int swing = -Math.Sin(shut) * -outward.x + -Math.Cos(shut) * -outward.y > 0 ? 1 : -1;
                // A hinge to turn, and the panel off to one side of it.
                var hingeT = new GameObject("door").transform;
                hingeT.SetParent(doorsRoot, false);
                hingeT.localPosition = Coords.Map(hinge.x, hinge.y, 0.01);
                double w = Interiors.DOOR_WIDTH - 0.03, h = Interiors.DOOR_HEIGHT - 0.02;
                var colour = DOOR_COLOURS[(int)Math.Floor(rng.Next() * DOOR_COLOURS.Length)];
                Shape(PrimitiveType.Cube, hingeT, new Vector3((float)(w / 2), (float)(h / 2), 0), new Vector3((float)w, (float)h, 0.035f), Materials.Lit(colour), true, "panel");
                doors.Add(new DoorState { hinge = hinge, shut = shut, swing = swing, open = 0, panel = hingeT });
            }
            for (int i = 0; i < rooms.Count; i++) SwingDoor(i);
            frames.Build("door frames", still);
        }

        /// <summary>Set door i's panel to its current opening.</summary>
        public void SwingDoor(int i)
        {
            var d = doors[i];
            double eased = d.open * d.open * (3 - 2 * d.open);
            // The panel runs along local +x from the hinge, which points along map (cos a, -sin a):
            // Unity turns +x towards -z (map +y) as the angle grows, hence the minus.
            double angle = d.shut + d.swing * eased * DOOR_SWING;
            d.panel.localRotation = Quaternion.Euler(0, (float)(-angle * Mathf.Rad2Deg), 0);
        }

        void AddYards(MeshBuilder sheds, Rng rng)
        {
            int w = model.width, h = model.height;
            var crops = Catalog.CROPS.Select(c => library.Get(c)).ToArray();
            var props = new[] { library.Get("nature/log_stack"), library.Get("nature/pot_large") };
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
                            double s = 0.7 / Math.Max(p.size.x, p.size.z);
                            Props.Place(p, props, x + 0.5, y + 0.5, rng.Range(0, Math.PI * 2), s, s, s);
                            continue;
                        }
                        var crop = crops[(int)Math.Floor(rng.Next() * crops.Length)];
                        double cs = 0.42 / Math.Max(crop.size.x, crop.size.z);
                        foreach (var (ox, oy) in new[] { (0.27, 0.27), (0.73, 0.27), (0.27, 0.73), (0.73, 0.73) })
                        {
                            double r = rng.Range(0, Math.PI * 2);
                            double sy = cs * rng.Range(0.85, 1.15);
                            Props.Place(crop, props, x + ox, y + oy, r, cs, sy, cs, castShadows: false);
                        }
                    }
                    else if (use == Use.SHED) AddShed(sheds, x + 0.5, y + 0.5, rng.Chance(0.5));
                }
            }
            var fence = library.Get("suburban/fence");
            double fsx = 1 / fence.size.x;
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
                Props.Place(fence, props, at.x, at.y, rot, fsx, 1.3, 1.3);
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
            var trees = Catalog.TREES.Select(t => library.Get(t)).ToArray();
            var bushes = Catalog.BUSHES.Select(t => library.Get(t)).ToArray();
            var flowers = Catalog.FLOWERS.Select(t => library.Get(t)).ToArray();
            var rock = library.Get("nature/rock_smallA");
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
                            Props.Place(rock, props, ax, ay, rng.Range(0, 6.3), s, s, s);
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
                    bool inTown = Numbers.Hypot(x + 0.5 - model.center.x, y + 0.5 - model.center.y) < model.townRadius;
                    double chance = use == Use.YARD ? (nearStreet ? 0.04 : 0.14) : use == Use.GREEN ? 0.28 : nearStreet ? 0.03 : inTown ? 0.1 : 0.22;
                    double roll = rng.Next();
                    double atX = x + 0.5 + rng.Range(-0.25, 0.25);
                    double atY = y + 0.5 + rng.Range(-0.25, 0.25);
                    double spin = rng.Range(0, Math.PI * 2);
                    if (roll < chance)
                    {
                        var pool = inTown ? trees.Where((_, k) => k < 4 || k > 6).ToArray() : trees;
                        var t = pool[(int)Math.Floor(rng.Next() * pool.Length)];
                        double s = rng.Range(1.3, 2.1) / t.size.y;
                        double sy = s * rng.Range(0.9, 1.1);
                        Props.Place(t, props, atX, atY, spin, s, sy, s);
                    }
                    else if (roll < chance + (use == Use.YARD || use == Use.GREEN ? 0.08 : 0.03))
                    {
                        var b = bushes[(int)Math.Floor(rng.Next() * bushes.Length)];
                        double s = rng.Range(0.4, 0.65) / Math.Max(b.size.x, b.size.z);
                        Props.Place(b, props, atX, atY, spin, s, s, s, castShadows: false);
                    }
                    else if (roll < chance + 0.14 && (use == Use.YARD || use == Use.GREEN))
                    {
                        var f = flowers[(int)Math.Floor(rng.Next() * flowers.Length)];
                        for (int k = 0; k < 3; k++)
                        {
                            const double s = 1.2;
                            double fx = atX + rng.Range(-0.25, 0.25);
                            double fy = atY + rng.Range(-0.25, 0.25);
                            Props.Place(f, props, fx, fy, rng.Range(0, 6.3), s, s, s, castShadows: false);
                        }
                    }
                }
            }
        }

        void AddPlazaDecor(Rng rng)
        {
            var tiles = model.tiles;
            int w = model.width, h = model.height;
            var planter = library.Get("suburban/planter");
            var tree = library.Get("nature/tree_small");
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
                        double s = 0.8 / Math.Max(planter.size.x, planter.size.z);
                        Props.Place(planter, props, ax, ay, DetMath.Atan2(dx, dy), s, s, s);
                    }
                    else
                    {
                        double s = 1.4 / tree.size.y;
                        Props.Place(tree, props, ax, ay, rng.Range(0, 6.3), s, s, s);
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
                var p = library.Get(Catalog.CARS[m]);
                double s = Props.CarScale(p);
                parked.Add(new ParkedPose { x = car.x, y = car.y, heading = heading, model = m });
                parkedCars.Add(Props.Place(p, props, car.x, car.y, DetMath.Atan2(Math.Cos(heading), Math.Sin(heading)), s, s, s));
            }
        }

        void AddLamps(Rng rng)
        {
            var placed = new List<Vec2>();
            var post = Materials.Lit(C.lampPost);
            var bulbMaterial = Materials.Glowing(Coords.Hex(0xfff6e0));
            var poolMaterial = Materials.LightPool();
            var lampsRoot = new GameObject("lamps").transform;
            lampsRoot.SetParent(root.transform, false);
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
                    double tl = Numbers.Hypot(tgx, tgy);
                    if (tl == 0) tl = 1;
                    var normal = new Vec2((tgy / tl) * path.side, (-tgx / tl) * path.side);
                    var basePt = new Vec2(on.x + normal.x * (Layout.SIDEWALK / 2 - 0.08), on.y + normal.y * (Layout.SIDEWALK / 2 - 0.08));
                    double rot = DetMath.Atan2(-normal.x, -normal.y);
                    // A post, an arm reaching out over the street (+z), and the lamp's head.
                    var lamp = new GameObject("lamp").transform;
                    lamp.SetParent(props, false);
                    lamp.localPosition = Coords.Map(basePt.x, basePt.y);
                    lamp.localRotation = Coords.Yaw(rot);
                    float H = (float)LAMP_HEIGHT, R = (float)LAMP_REACH;
                    Shape(PrimitiveType.Cylinder, lamp, new Vector3(0, H / 2, 0), new Vector3(0.052f, H / 2, 0.052f), post);
                    Shape(PrimitiveType.Cube, lamp, new Vector3(0, H - 0.01f, R / 2), new Vector3(0.03f, 0.03f, R), post);
                    Shape(PrimitiveType.Cube, lamp, new Vector3(0, H - 0.02f, R), new Vector3(0.09f, 0.04f, 0.12f), post);
                    // The bulb moves with nothing but glows per lamp, so it stays out of the static batch.
                    var bulb = Shape(PrimitiveType.Sphere, lampsRoot, Coords.Map(basePt.x - normal.x * LAMP_REACH, basePt.y - normal.y * LAMP_REACH, LAMP_HEIGHT - 0.05), Vector3.one * 0.1f, bulbMaterial, false, "bulb");
                    double lx = basePt.x - normal.x * LAMP_REACH, ly = basePt.y - normal.y * LAMP_REACH;
                    var pool = Shape(PrimitiveType.Quad, lampsRoot, Coords.Map(lx, ly, 0.045), new Vector3(2.4f, 2.4f, 1), poolMaterial, false, "light pool");
                    pool.transform.localRotation = Quaternion.Euler(90, 0, 0);
                    lamps.Add(new Lamp { x = lx, y = ly, h = LAMP_HEIGHT - 0.05, phase = rng.Range(0, 100), faulty = rng.Chance(0.12), bulb = bulb.GetComponent<Renderer>(), pool = pool.GetComponent<Renderer>() });
                }
            }
        }

        /// <summary>A signpost at each named corner, with a blade per street, its name on both faces.</summary>
        void AddStreetSigns()
        {
            if (signs.Count == 0) return;
            int tallest = signs.Max(s => s.blades.Count);
            float postHeight = (float)(Signs.BLADE_Y + (tallest - 1) * Signs.BLADE_STEP + Signs.BLADE_HEIGHT / 2 + 0.03);
            var postMaterial = Materials.Lit(0x5b6067);
            var bladeMaterial = Materials.Lit(0x24603f);
            var signsRoot = new GameObject("signs").transform;
            signsRoot.SetParent(root.transform, false);
            float r = (float)Signs.POST_RADIUS;
            foreach (var s in signs)
            {
                var at = Coords.Map(s.at.x, s.at.y);
                Shape(PrimitiveType.Cylinder, props, at + new Vector3(0, postHeight / 2, 0), new Vector3(r * 2.2f, postHeight / 2, r * 2.2f), postMaterial, true, "sign post");
                for (int k = 0; k < s.blades.Count; k++)
                {
                    var blade = s.blades[k];
                    var d = blade.along;
                    double outD = Signs.POST_RADIUS + Signs.BLADE_GAP + Signs.BLADE_LENGTH / 2;
                    var centre = Coords.Map(s.at.x + d.x * outD, s.at.y + d.y * outD, Signs.BLADE_Y + k * Signs.BLADE_STEP);
                    var t = new GameObject("blade").transform;
                    t.SetParent(signsRoot, false);
                    t.localPosition = centre;
                    // Local x along the blade (map direction d), z across it.
                    t.localRotation = Quaternion.LookRotation(Coords.Map(-d.y, d.x) - Coords.Map(0, 0), Vector3.up);
                    Shape(PrimitiveType.Cube, t, Vector3.zero, new Vector3((float)Signs.BLADE_LENGTH, (float)Signs.BLADE_HEIGHT, (float)Signs.BLADE_THICK), bladeMaterial, true, "board");
                    string name = names.streets[blade.street].name.ToUpperInvariant();
                    foreach (int face in new[] { 1, -1 }) SignText(t, name, face);
                }
            }
        }

        /// <summary>A street name on one face of a blade (white on green, as street signs are).</summary>
        void SignText(Transform blade, string name, int face)
        {
            var go = new GameObject("name");
            go.transform.SetParent(blade, false);
            go.transform.localPosition = new Vector3(0, 0, (float)(face * (Signs.BLADE_THICK / 2 + 0.002)));
            go.transform.localRotation = face > 0 ? Quaternion.Euler(0, 180, 0) : Quaternion.identity;
            var text = go.AddComponent<TextMeshPro>();
            text.font = library.font;
            text.text = name;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = true;
            text.fontSizeMin = 0.1f;
            text.fontSizeMax = 1.2f;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.rectTransform.sizeDelta = new Vector2((float)(Signs.BLADE_LENGTH * 0.9), (float)(Signs.BLADE_HEIGHT * 0.8));
            go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        }

        /// <summary>The plaza fountain: a stone basin and spout, water, and spray (a particle system).</summary>
        void AddFountain()
        {
            var well = model.landmarks.Find(l => l.kind == LandmarkKind.Well);
            if (well == null) return;
            fountainAt = new Vec2(Math.Floor(well.at.x) + 0.5, Math.Floor(well.at.y) + 0.5);
            var at = Coords.Map(fountainAt.x, fountainAt.y);
            var stone = Materials.Lit(C.stone);
            Shape(PrimitiveType.Cylinder, still, at + new Vector3(0, 0.1f, 0), new Vector3(0.92f, 0.1f, 0.92f), stone, true, "basin");
            Shape(PrimitiveType.Cylinder, still, at + new Vector3(0, 0.25f, 0), new Vector3(0.14f, 0.25f, 0.14f), stone, true, "spout");
            Shape(PrimitiveType.Cylinder, still, at + new Vector3(0, 0.5f, 0), new Vector3(0.3f, 0.03f, 0.3f), stone, true, "bowl");
            Shape(PrimitiveType.Cylinder, still, at + new Vector3(0, 0.19f, 0), new Vector3(0.78f, 0.011f, 0.78f), Materials.Lit(0x7fbde3, 0.9f), false, "pool");

            var spray = new GameObject("spray");
            spray.transform.SetParent(root.transform, false);
            spray.transform.localPosition = at + new Vector3(0, 0.52f, 0);
            spray.transform.localRotation = Quaternion.Euler(-90, 0, 0);
            var ps = spray.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 1;
            main.loop = true;
            main.startLifetime = 0.75f;
            main.startSpeed = 1.1f;
            main.startSize = 0.05f;
            main.gravityModifier = 0.32f;
            main.maxParticles = 200;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new Color(0.85f, 0.94f, 1f, 0.85f);
            var emission = ps.emission;
            emission.rateOverTime = 60;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18;
            shape.radius = 0.02f;
            var renderer = spray.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = Materials.Particles(Color.white);
            ps.Play();
        }
    }
}

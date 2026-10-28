// The rest of the town: buildings on their footprints, the rooms on their
// ground floors and the doors into them, yards, trees, plaza decor, parked
// cars, lamps, street signs and the fountain.

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
    /// <summary>A room's furnished inside, always there to see through its windows (and lit at night).</summary>
    public sealed class Interior
    {
        public readonly GameObject root;
        /// <summary>The walls by side, and how tall each is drawn (0..1, cut down facing the camera).</summary>
        public readonly Dictionary<Facing, Transform> walls = new Dictionary<Facing, Transform>();
        public readonly Dictionary<Facing, double> wallScale = new Dictionary<Facing, double>();
        /// <summary>What casts shadows while you're inside (seen from outside, the building shades it all anyway).</summary>
        public readonly List<Renderer> casters = new List<Renderer>();

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

        public void CastShadows(bool on)
        {
            foreach (var r in casters) r.shadowCastingMode = on ? ShadowCastingMode.On : ShadowCastingMode.Off;
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
                int split = splittable ? BestSplit(candidates, frontage, depth) : 1;
                double len = frontage / split;
                int colours = Catalog.VariationCount(candidates[0]);
                int variation = rng.Int(0, colours - 1);
                for (int k = 0; k < split; k++)
                {
                    var m = library.Get(PickModel(candidates, len / depth, house, rng));
                    const double gap = Porchlight.Gen.BuildingsStage.DRAWN_INSET;
                    double sx = (len - gap * 2) / m.size.x;
                    double sz = (depth - gap * 2) / m.size.z;
                    double sy = Math.Sqrt(sx * sz) * HeightFactor(m, Math.Sqrt(sx * sz), house);
                    double along = (alongX ? x0 : y0) + (k + 0.5) * len;
                    var at = alongX ? new Vec2(along, (y0 + y1) / 2.0) : new Vec2((x0 + x1) / 2.0, along);
                    var shell = Props.Place(m, still, at.x, at.y, rot, sx, sy, sz, variation: variation, windows: true);
                    // The room's windows are clear: what lights them at night is the room behind.
                    var glass = shell.transform.GetChild(0).Find(ModelLibrary.Ground.GLASS);
                    if (glass != null) glass.GetComponent<Renderer>().sharedMaterial = Materials.Glass();
                    double halfX = alongX ? len / 2 : depth / 2;
                    double halfY = alongX ? depth / 2 : len / 2;
                    boxes.Add(new Box { x0 = at.x - halfX, y0 = at.y - halfY, x1 = at.x + halfX, y1 = at.y + halfY, top = m.size.y * sy });
                    shells.Add(shell);
                    var piece = new Piece { model = shell.transform.GetChild(0), ground = m.ground };
                    pieces.Add(piece);
                    rooms.Add(RoomOf(piece, rooms.Count, house));
                    foreach (var w in m.ground.walls)
                    {
                        // Thin solids along the building's own walls (porch posts and all), at walking height.
                        var a = MapPoint(piece.model, w.x, 0, w.y);
                        var b = MapPoint(piece.model, w.z, 0, w.w);
                        const double t = 0.02;
                        groundWalls.Add(new Rect(Math.Min(a.x, b.x) - t, Math.Min(a.y, b.y) - t, Math.Max(a.x, b.x) + t, Math.Max(a.y, b.y) + t));
                    }
                }
            }
        }

        /// <summary>A building piece as placed: its model (the prefab instance) and what its ground floor holds.</summary>
        sealed class Piece
        {
            public Transform model;
            public ModelLibrary.Ground ground;
        }

        /// <summary>A point in a placed model's own space, on the map.</summary>
        static Vec2 MapPoint(Transform model, float x, float y, float z)
        {
            var w = model.TransformPoint(new Vector3(x, y, z));
            return new Vec2(w.x, -w.z);
        }

        static double HeightOf(Transform model, float y) => model.TransformPoint(new Vector3(0, y, 0)).y;

        /// <summary>Which way a placed model's side (numbered as Ground's: 0 -z, 1 +z, 2 -x, 3 +x) faces on the map.</summary>
        static Facing SideFacing(Transform model, int side)
        {
            var local = side switch { 0 => Vector3.back, 1 => Vector3.forward, 2 => Vector3.left, _ => Vector3.right };
            var w = model.TransformDirection(local);
            double x = w.x, y = -w.z;
            return Math.Abs(x) > Math.Abs(y) ? (x > 0 ? Facing.E : Facing.W) : (y > 0 ? Facing.S : Facing.N);
        }

        /// <summary>The room on a piece's ground floor, with its doors and windows, on the map.</summary>
        static Room RoomOf(Piece piece, int id, House house)
        {
            var g = piece.ground;
            var c0 = MapPoint(piece.model, g.room.x, 0, g.room.y);
            var c1 = MapPoint(piece.model, g.room.z, 0, g.room.w);
            var room = new Room
            {
                id = id,
                x0 = Math.Min(c0.x, c1.x),
                y0 = Math.Min(c0.y, c1.y),
                x1 = Math.Max(c0.x, c1.x),
                y1 = Math.Max(c0.y, c1.y),
                facing = house.facing,
                style = house.style,
                height = HeightOf(piece.model, g.storey),
                doors = new List<RoomDoor>(),
            };
            // An opening's ends along its wall, on the map (x along N and S walls, y along E and W).
            (Facing side, double a0, double a1) Along(int side, float plane, float a0, float a1)
            {
                var p = side < 2 ? MapPoint(piece.model, a0, 0, plane) : MapPoint(piece.model, plane, 0, a0);
                var q = side < 2 ? MapPoint(piece.model, a1, 0, plane) : MapPoint(piece.model, plane, 0, a1);
                var f = SideFacing(piece.model, side);
                bool alongX = f == Facing.N || f == Facing.S;
                double u = alongX ? p.x : p.y, v = alongX ? q.x : q.y;
                return (f, Math.Min(u, v), Math.Max(u, v));
            }
            float Plane(int side) => side switch { 0 => g.room.y, 1 => g.room.w, 2 => g.room.x, _ => g.room.z };
            foreach (var d in g.doors)
            {
                var (side, a0, a1) = Along(d.side, Plane(d.side), d.a0, d.a1);
                room.doors.Add(new RoomDoor { side = side, a0 = a0, a1 = a1, height = HeightOf(piece.model, d.top) });
            }
            foreach (var w in g.windows)
            {
                var (side, a0, a1) = Along(w.side, Plane(w.side), w.a0, w.a1);
                room.windows.Add(new RoomWindow { side = side, a0 = a0, a1 = a1, y0 = HeightOf(piece.model, w.y0), y1 = HeightOf(piece.model, w.y1) });
            }
            return room;
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
            // The buildings' own walls, but never across a way in.
            var passages = rooms.SelectMany(r => Interiors.Doors(r).Select(Interiors.Passage)).ToList();
            var solid = new List<Rect>();
            foreach (var w in groundWalls) solid.AddRange(Interiors.Without(w, passages.Where(p => p.x0 < w.x1 && w.x0 < p.x1 && p.y0 < w.y1 && w.y0 < p.y1)));
            foreach (var room in rooms)
            {
                var plan = Interiors.FurnishRoom(room, Size, rng);
                plans.Add(plan);
                solid.AddRange(Interiors.RoomWalls(room));
                foreach (var f in plan.furniture) if (!f.flat && f.lift == 0) solid.Add(f.box);
            }
            solids = new Solids(solid, model.width);
        }

        /// <summary>
        /// Televisions on behind a few of the buildings' windows (not the rooms', which
        /// show what's really inside): panes over their glass, in groups that each
        /// flicker blue on their own at night.
        /// </summary>
        void AddScreens()
        {
            var tv = new MeshBuilder[SCREEN_GROUPS];
            for (int g = 0; g < tv.Length; g++) tv[g] = new MeshBuilder(palette);
            int pane = 0;
            foreach (var piece in pieces)
            {
                foreach (var p in piece.ground.panes)
                {
                    double roll = Layout.Hash(pane++, 77);
                    if (roll >= SCREEN_SHARE) continue;
                    // Just off the glass, on its outside.
                    float off = (p.side % 2 == 0 ? -1 : 1) * 0.004f;
                    V3 Corner(float a, float y)
                    {
                        var m = p.side < 2 ? MapPoint(piece.model, a, y, p.plane + off) : MapPoint(piece.model, p.plane + off, y, a);
                        return v(m.x, HeightOf(piece.model, y), m.y);
                    }
                    var f = Front(SideFacing(piece.model, p.side));
                    tv[(int)(roll / SCREEN_SHARE * SCREEN_GROUPS)].Quad(Corner(p.a0, p.y0), Corner(p.a1, p.y0), Corner(p.a1, p.y1), Corner(p.a0, p.y1), GLASS, new V3(f.x, 0, f.y));
                }
            }
            var screenMaterial = Materials.Glowing(Coords.Hex(0x6f9fe8));
            for (int g = 0; g < tv.Length; g++)
            {
                if (tv[g].Empty) continue;
                var go = new GameObject("screens " + g);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = tv[g].Build("screens");
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = screenMaterial;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.enabled = false;
                screens.Add(r);
            }
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

        /// <summary>The warm light a room's floor and walls give off at night (how brightly: Materials.WindowGlow).</summary>
        const int ROOM_GLOW = 0x8c6a40, FLOOR_GLOW = 0x5e4630;

        /// <summary>The furnished inside of a room (every one is built with the town).</summary>
        public Interior InteriorOf(int id)
        {
            if (interiors.TryGetValue(id, out var it)) return it;
            it = new Interior(root.transform, "room " + id);
            var room = rooms[id];
            var floor = new MeshBuilder(palette);
            var inner = Interiors.InnerRect(room);
            floor.Rect(inner.x0, inner.y0, inner.x1, inner.y1, 0.012, FLOORS[room.style], FLOOR_GLOW);
            floor.Build("floor", it.root.transform, false);
            var sides = new Dictionary<Facing, MeshBuilder>();
            var sideOrder = new List<Facing>();
            foreach (var wp in Interiors.WallPieces(room))
            {
                if (!sides.TryGetValue(wp.side, out var b))
                {
                    sides[wp.side] = b = new MeshBuilder(palette);
                    sideOrder.Add(wp.side);
                }
                const double e = 0.006;
                // Kept back from the building's own wall, so the two never fight over which shows.
                const double back = 0.02;
                var wl = wp.rect;
                double x0 = wl.x0 + (wp.side == Facing.W ? back : e), y0 = wl.y0 + (wp.side == Facing.N ? back : e);
                double x1 = wl.x1 - (wp.side == Facing.E ? back : e), y1 = wl.y1 - (wp.side == Facing.S ? back : e);
                b.Box(x0, y0, x1, y1, wp.y0, wp.y1 - e, 0xefe6d6, 0xb8ad9a, ROOM_GLOW);
            }
            foreach (var side in sideOrder)
            {
                var wall = sides[side].Build("wall " + side, it.root.transform, false);
                it.walls[side] = wall.transform;
                it.wallScale[side] = 1;
                it.casters.Add(wall.GetComponent<Renderer>());
            }
            foreach (var p in plans[id].furniture)
            {
                var piece = Props.Place(library.Get("furniture/" + p.name), it.root.transform, p.x, p.y, p.rot, Interiors.FURNITURE_SCALE, Interiors.FURNITURE_SCALE, Interiors.FURNITURE_SCALE, p.lift + 0.012, castShadows: false);
                if (!p.flat) it.casters.AddRange(piece.GetComponentsInChildren<Renderer>());
            }
            interiors[id] = it;
            return it;
        }

        /// <summary>
        /// Every room's doors: each building's own, lifted out of it onto a pivot of its
        /// own (on its hinge, or a garage door's top edge), set to swing into the room.
        /// </summary>
        void AddDoors()
        {
            var doorsRoot = new GameObject("doors").transform;
            doorsRoot.SetParent(root.transform, false);
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                var piece = pieces[i];
                var ways = Interiors.Doors(room);
                var middle = Coords.Map((room.x0 + room.x1) / 2, (room.y0 + room.y1) / 2);
                for (int k = 0; k < piece.ground.doors.Count; k++)
                {
                    var gd = piece.ground.doors[k];
                    var part = piece.model.Find(gd.part);
                    var pivot = new GameObject("door").transform;
                    pivot.SetParent(doorsRoot, false);
                    pivot.SetPositionAndRotation(part.position, part.rotation);
                    part.SetParent(pivot, true);
                    var d = new DoorState
                    {
                        room = i,
                        at = ways[k].at,
                        pivot = pivot,
                        shut = pivot.localRotation,
                        axis = gd.lifts ? (gd.side < 2 ? Vector3.right : Vector3.forward) : Vector3.up,
                        turn = gd.lifts ? DOOR_LIFT : DOOR_SWING,
                    };
                    // Whichever way takes the door's middle into the room.
                    var centre = part.GetComponent<MeshFilter>().sharedMesh.bounds.center;
                    var before = part.TransformPoint(centre);
                    pivot.localRotation = d.shut * Quaternion.AngleAxis(d.turn * 0.25f, d.axis);
                    var after = part.TransformPoint(centre);
                    if (Vector3.Distance(after, middle) > Vector3.Distance(before, middle)) d.turn = -d.turn;
                    doors.Add(d);
                    SwingDoor(doors.Count - 1);
                }
            }
        }

        /// <summary>Set door i to its current opening.</summary>
        public void SwingDoor(int i)
        {
            var d = doors[i];
            float eased = (float)(d.open * d.open * (3 - 2 * d.open));
            d.pivot.localRotation = d.shut * Quaternion.AngleAxis(d.turn * eased, d.axis);
        }

        void AddYards(MeshBuilder sheds, Rng rng)
        {
            int w = model.width, h = model.height;
            var crops = Catalog.CROPS.Select(c => library.Get(c)).ToArray();
            var extras = Catalog.GARDEN_EXTRAS.Select(c => library.Get(c)).ToArray();
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
                            var p = extras[(int)Math.Floor(rng.Next() * extras.Length)];
                            double s = (p.name.EndsWith("_small") ? 0.35 : 0.7) / Math.Max(p.size.x, p.size.z);
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
            ModelLibrary.Model[] Get(string[] names) => names.Select(n => library.Get(n)).ToArray();
            var trees = Get(Catalog.TREES);
            var fall = Get(Catalog.FALL_TREES);
            var pines = Get(Catalog.PINES);
            var palms = Get(Catalog.PALMS);
            var bushes = Get(Catalog.BUSHES);
            var flowers = Get(Catalog.FLOWERS);
            var rocks = Get(Catalog.ROCKS);
            var boulders = Get(Catalog.BOULDERS);
            var wilds = Get(Catalog.WILDS);
            var tents = Get(Catalog.TENTS);
            var fires = Get(Catalog.CAMPFIRES);
            ModelLibrary.Model Any(ModelLibrary.Model[] pool) => pool[(int)Math.Floor(rng.Next() * pool.Length)];
            // Sized by footprint (in tiles), or for trees by height.
            void Spread(ModelLibrary.Model m, double x, double y, double footprint, bool shadows = true)
            {
                double s = footprint / Math.Max(m.size.x, m.size.z);
                Props.Place(m, props, x, y, rng.Range(0, Math.PI * 2), s, s, s, castShadows: shadows);
            }
            void Tree(ModelLibrary.Model t, double x, double y, double spin)
            {
                double s = rng.Range(1.3, 2.1) / t.size.y;
                double sy = s * rng.Range(0.9, 1.1);
                Props.Place(t, props, x, y, spin, s, sy, s);
            }
            void Wild(double x, double y)
            {
                var m = Any(wilds);
                double footprint = m.name.Contains("mushroom") ? rng.Range(0.12, 0.2) : m.name.Contains("stump") ? rng.Range(0.18, 0.3) : rng.Range(0.35, 0.6);
                Spread(m, x, y, footprint, !m.name.Contains("mushroom"));
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    byte use = layout.use[i];
                    byte terrain = tiles.terrain[i];
                    if (tiles.overlay[i] != Overlay.NONE) continue;
                    if (use == Use.HOUSE || use == Use.SHED || use == Use.GARDEN || use == Use.COMMUNITY_GARDEN || use == Use.DRIVEWAY) continue;
                    if (terrain == Terrain.ROCK)
                    {
                        if (rng.Chance(0.22))
                        {
                            double ax = x + rng.Range(0.25, 0.75);
                            double ay = y + rng.Range(0.25, 0.75);
                            if (rng.Chance(0.35)) Spread(Any(boulders), ax, ay, rng.Range(0.55, 0.95));
                            else Spread(Any(rocks), ax, ay, rng.Range(0.4, 0.85));
                        }
                        continue;
                    }
                    if (terrain == Terrain.SAND)
                    {
                        double r = rng.Next();
                        double ax = x + rng.Range(0.25, 0.75), ay = y + rng.Range(0.25, 0.75);
                        if (r < 0.05) Tree(Any(palms), ax, ay, rng.Range(0, Math.PI * 2));
                        else if (r < 0.1) Spread(Any(rocks), ax, ay, rng.Range(0.2, 0.4));
                        continue;
                    }
                    if (terrain == Terrain.DIRT)
                    {
                        double r = rng.Next();
                        double ax = x + rng.Range(0.25, 0.75), ay = y + rng.Range(0.25, 0.75);
                        if (r < 0.06) Wild(ax, ay);
                        else if (r < 0.1) Spread(Any(rocks), ax, ay, rng.Range(0.2, 0.45));
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
                        var pool = rng.Chance(0.12) ? fall : !inTown && rng.Chance(0.45) ? pines : trees;
                        Tree(Any(pool), atX, atY, spin);
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
                    else if (!inTown && use == Use.NONE && roll < chance + 0.09) Wild(atX, atY);
                    else if (!inTown && use == Use.NONE && !nearStreet && roll > 0.996)
                    {
                        // A campsite: a tent, and its fire a step in front.
                        double dx = Math.Cos(spin), dy = Math.Sin(spin);
                        var tent = Any(tents);
                        double s = rng.Range(0.55, 0.7) / Math.Max(tent.size.x, tent.size.z);
                        Props.Place(tent, props, x + 0.5 - dx * 0.12, y + 0.5 - dy * 0.12, DetMath.Atan2(dx, dy), s, s, s);
                        Spread(Any(fires), x + 0.5 + dx * 0.32, y + 0.5 + dy * 0.32, 0.22, false);
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
            var statues = Catalog.STATUES.Select(n => library.Get(n)).ToArray();
            var parasols = Catalog.PARASOLS.Select(n => library.Get(n)).ToArray();
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
                    if (roll < 0.14)
                    {
                        double s = 0.8 / Math.Max(planter.size.x, planter.size.z);
                        Props.Place(planter, props, ax, ay, DetMath.Atan2(dx, dy), s, s, s);
                    }
                    else if (roll < 0.2)
                    {
                        double s = 1.4 / tree.size.y;
                        Props.Place(tree, props, ax, ay, rng.Range(0, 6.3), s, s, s);
                    }
                    else if (roll < 0.25)
                    {
                        var p = parasols[(int)Math.Floor(rng.Next() * parasols.Length)];
                        double s = 0.62 / p.size.y;
                        Props.Place(p, props, ax, ay, rng.Range(0, 6.3), s, s, s);
                    }
                    else
                    {
                        var p = statues[(int)Math.Floor(rng.Next() * statues.Length)];
                        double s = rng.Range(0.45, 0.6) / p.size.y;
                        Props.Place(p, props, ax, ay, DetMath.Atan2(-dx, -dy), s, s, s);
                    }
                }
            }
        }

        void AddParkedCars()
        {
            foreach (var car in layout.cars)
            {
                // An everyday car parks either way round; a set vehicle (a boat at its mooring, a plane on the apron) as it's put.
                bool set = car.vehicle > 0;
                double heading = car.angle + (!set && car.variant % 2 != 0 ? Math.PI : 0);
                int m = set ? car.vehicle - 1 : Catalog.PARKED[car.variant % Catalog.PARKED.Length];
                var p = library.Get(Catalog.CARS[m]);
                double s = CarView.ScaleOf(p);
                parked.Add(new ParkedPose { x = car.x, y = car.y, heading = heading, model = m });
                parkedCars.Add(Props.Place(p, props, car.x, car.y, DetMath.Atan2(Math.Cos(heading), Math.Sin(heading)), s, s, s, Catalog.KindOf(m) == VehicleKind.Boat ? CarView.LiftOf(VehicleKind.Boat) : 0));
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

// Builds the 3D town for a TownModel from its layout: tiled ground with sunken
// rivers, flat vector roads with sidewalks and kerbs, stone bridges, Kenney
// houses, shops and towers fitted to every blueprint footprint (long street
// fronts split into rows), modelled ground floors with doors and furnished
// rooms, fenced yards with sheds and vegetable beds, trees, street lamps,
// street name signs, parked cars and the plaza fountain. Every copy of a
// Kenney model is drawn instanced, one batch per model part. A port of
// render/iso/world.ts (map (x, y) is Three's (x, z); see Space.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using UnityEngine;
using Grid = Porchlight.Core.Grid;
using UnityEngine.Rendering;
using Rect = Porchlight.Explore.Rect;

namespace Porchlight.Rendering
{
    public sealed class Lamp
    {
        public double x, y, h, phase;
        public bool faulty;
    }

    public sealed class Streak
    {
        public List<Vec2> path;
        public List<double> at;
        public double s, speed;
    }

    public sealed class Droplet
    {
        public double t, vx, vy;
    }

    public sealed class DoorState
    {
        public Vec2 hinge;
        public double shut;
        public int swing;
        public double open;
    }

    public struct ParkedPose
    {
        public double x, y, heading;
        public int model;
    }

    /// <summary>The camera-facing glow squares (lamp halos, car-light halos), one quad per light.</summary>
    public sealed class Halos
    {
        public readonly Mesh mesh;
        readonly Vector3[] centres;
        readonly Color[] colors;
        readonly Vector2[] sizes;
        readonly int capacity;
        public readonly Drawable drawable;
        public readonly Material material;

        public Halos(int capacity, double size, int renderOrder, Stage stage)
        {
            this.capacity = Math.Max(1, capacity);
            mesh = new Mesh { name = "halos" };
            int n = this.capacity * 4;
            centres = new Vector3[n];
            colors = new Color[n];
            sizes = new Vector2[n];
            var corners = new Vector2[n];
            var tris = new int[this.capacity * 6];
            for (int i = 0; i < this.capacity; i++)
            {
                corners[i * 4] = new Vector2(0, 0);
                corners[i * 4 + 1] = new Vector2(1, 0);
                corners[i * 4 + 2] = new Vector2(1, 1);
                corners[i * 4 + 3] = new Vector2(0, 1);
                for (int k = 0; k < 4; k++) sizes[i * 4 + k] = new Vector2((float)size, (float)size);
                tris[i * 6] = i * 4;
                tris[i * 6 + 1] = i * 4 + 2;
                tris[i * 6 + 2] = i * 4 + 1;
                tris[i * 6 + 3] = i * 4;
                tris[i * 6 + 4] = i * 4 + 3;
                tris[i * 6 + 5] = i * 4 + 2;
            }
            mesh.SetVertices(centres);
            mesh.SetUVs(0, corners);
            mesh.SetUVs(1, sizes);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000);
            material = Mats.BillboardMaterial(Primitives.Glow, Blending.Additive, true, renderOrder);
            drawable = stage.Add(new Drawable(mesh, material, Matrix4x4.identity, false));
        }

        public void Set(int i, Vector3 at, float r, float g, float b)
        {
            for (int k = 0; k < 4; k++)
            {
                centres[i * 4 + k] = at;
                colors[i * 4 + k] = new Color(r, g, b, 1);
            }
        }

        /// <summary>Show only the first `count` halos (the rest collapse to nothing).</summary>
        public void Commit(int count)
        {
            for (int i = count; i < capacity; i++)
                for (int k = 0; k < 4; k++) colors[i * 4 + k] = new Color(0, 0, 0, 0);
            mesh.SetVertices(centres);
            mesh.SetColors(colors);
        }
    }

    /// <summary>Collects every placement of each prefab, then draws them instanced.</summary>
    public sealed class Instancer
    {
        readonly Dictionary<Prefab, List<Matrix4x4>> items = new Dictionary<Prefab, List<Matrix4x4>>();
        readonly List<Prefab> order = new List<Prefab>();
        readonly HashSet<Prefab> noShadow = new HashSet<Prefab>();
        readonly Dictionary<Prefab, List<Drawable>> drawables = new Dictionary<Prefab, List<Drawable>>();
        static readonly Matrix4x4 Zero = Matrix4x4.Scale(Vector3.zero);

        /// <summary>Add a copy; returns its index among this prefab's copies.</summary>
        public int Add(Prefab p, Mat4 m, bool castShadow = true)
        {
            if (!castShadow) noShadow.Add(p);
            if (!items.TryGetValue(p, out var list))
            {
                items[p] = list = new List<Matrix4x4>();
                order.Add(p);
            }
            list.Add(Space.M(m));
            return list.Count - 1;
        }

        public void SetVisible(Prefab p, int index, bool visible)
        {
            if (!drawables.TryGetValue(p, out var ds)) return;
            var m = visible ? items[p][index] : Zero;
            foreach (var d in ds) d.matrices[index] = m;
        }

        public void Build(Stage stage, List<Drawable> owned)
        {
            foreach (var prefab in order)
            {
                var matrices = items[prefab].ToArray();
                var list = new List<Drawable>();
                foreach (var part in prefab.parts)
                {
                    var d = stage.Add(new Drawable(part.mesh, part.material, (Matrix4x4[])matrices.Clone(), matrices.Length, !noShadow.Contains(prefab)));
                    list.Add(d);
                    owned.Add(d);
                }
                drawables[prefab] = list;
            }
        }
    }

    public sealed partial class TownWorld
    {
        public const double WATER_Y = -0.14;
        const double BED_Y = -0.45;
        const double SLAB = 1.6;
        const double LAMP_SPACING = 7;
        const double FLOOR_H = 0.95;
        public const double CAR_LENGTH = 1.05;
        public const double GROUND_FLOOR = Interiors.WALL_HEIGHT;
        const double DOOR_SWING = 1.75;
        const double LAMP_HEIGHT = 0.95;
        const double LAMP_REACH = 0.3;
        static readonly double BUILDING_TURN = Math.PI;

        static class C
        {
            public const int grass = 0x86bd5b, lawn = 0x95c964, sand = 0xe6d49a, rock = 0x9c968c, soil = 0x7a5638, paving = 0xbdb6a4;
            public const int plazaA = 0xddd2b6, plazaB = 0xcdc1a2, bed = 0x6f8a86, bank = 0x8f7656, slab = 0x6b5238, asphalt = 0x6b6b6b;
            public const int sidewalk = 0xd8d3bf, curb = 0xb3ad98, lane = 0xece7d2, path = 0xc9a46e, stone = 0xa39d8a, stoneShade = 0x7d7768;
            public const int shedWall = 0xb07a4a, shedRoof = 0x5b4636, lampPost = 0x3b3f47;
        }

        static readonly int[] FACADES = { 0xb5563f, 0xe9dcc0, 0xf1efe9, 0xa9b99a, 0x9aa8b5, 0xd8bf8f, 0xc9c4b8, 0xc98a5a, 0x8fa6a0 };
        const int GLASS = 0x8fb8e8;
        const int TRIM = 0xf4f1ea;
        static readonly int[] DOOR_COLOURS = { 0x8a3b2e, 0x2f5d50, 0x2e4a7a, 0x6b4a2f, 0xb08a3e, 0x5b3b5e, 0xe8e2d0 };

        /// <summary>Window panes numbered across the session, as windows.ts's module-level counter.</summary>
        static int paneCount;

        public readonly GameObject root;
        public readonly TownModel model;
        public readonly IsoLayout layout;
        public SidewalkNetwork sidewalks => layout.sidewalks;
        public readonly StreetNames names;
        public List<Signpost> signs = new List<Signpost>();
        public readonly List<Box> boxes = new List<Box>();
        public readonly List<Lamp> lamps = new List<Lamp>();
        public Drawable bulbs;
        public Halos halos;
        public Drawable pools;
        public Material water;
        public Drawable streaksDrawable;
        public readonly List<Streak> streaks = new List<Streak>();
        public Vec2 fountainAt;
        public Drawable drops;
        public readonly List<Droplet> droplets = new List<Droplet>();
        public readonly List<Room> rooms = new List<Room>();
        public readonly List<int> roomBuilding = new List<int>();
        public readonly List<RoomPlan> plans = new List<RoomPlan>();
        public readonly List<DoorState> doors = new List<DoorState>();
        public Drawable doorPanels;
        public Solids solids;
        public readonly List<ParkedPose> parked = new List<ParkedPose>();
        public readonly Stage stage;

        readonly TownAssets assets;
        readonly Instancer instancer = new Instancer();
        readonly List<Drawable> owned = new List<Drawable>();
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly List<(Prefab prefab, int index)> shells = new List<(Prefab, int)>();
        readonly List<(Prefab prefab, int index)> parkedCopies = new List<(Prefab, int)>();
        readonly List<Drawable> facades = new List<Drawable>();
        readonly Dictionary<int, Interior> interiors = new Dictionary<int, Interior>();
        Material interiorMaterial;
        Material lambert;

        public TownWorld(TownModel model, TownAssets assets, Stage stage, ITextMaker text)
        {
            this.model = model;
            this.assets = assets;
            this.stage = stage;
            layout = Layout.Build(model);
            names = StreetNaming.Name(model, layout.roads);
            root = new GameObject("town");
            var rng = new Rng($"{model.seed}::iso");

            lambert = Mats.Lambert(Color.white, vertexColors: true);
            AddStatic(BuildGround(rng), lambert, false, true);
            water = AddWater();
            AddStatic(BuildRoads(), lambert, true, true);

            var sheds = new MeshBuilder();
            AddHouses(rng);
            Furnish(rng);
            AddFacades(rng);
            AddDoors(rng);
            AddYards(sheds, rng);
            AddTrees(rng);
            AddPlazaDecor(rng);
            AddParkedCars();
            if (!sheds.Empty) AddStatic(sheds.Build("sheds"), lambert, true, true);
            AddLamps(rng);
            signs = Signs.Plan(model, layout, names);
            AddStreetSigns(text);
            AddStreaks();
            AddFountain();
            instancer.Build(stage, owned);
            interiorMaterial = Mats.Lambert(Color.white, vertexColors: true);
        }

        Drawable AddStatic(Mesh mesh, Material material, bool cast, bool receive)
        {
            meshes.Add(mesh);
            var m = receive ? material : NoReceive(material);
            var d = stage.Add(new Drawable(mesh, m, Matrix4x4.identity, cast));
            owned.Add(d);
            return d;
        }

        readonly Dictionary<Material, Material> noReceive = new Dictionary<Material, Material>();

        Material NoReceive(Material m)
        {
            if (!noReceive.TryGetValue(m, out var o))
            {
                o = new Material(m);
                Mats.SetReceiveShadow(o, false);
                noReceive[m] = o;
            }
            return o;
        }

        public void SetParkedVisible(int i, bool visible) => instancer.SetVisible(parkedCopies[i].prefab, parkedCopies[i].index, visible);

        public void SetShellVisible(int room, bool visible)
        {
            instancer.SetVisible(shells[room].prefab, shells[room].index, visible);
            facades[room].visible = visible;
        }

        public void Dispose()
        {
            foreach (var d in owned) stage.Remove(d);
            foreach (var i in interiors.Values) i.Dispose(stage);
            foreach (var m in meshes) UnityEngine.Object.Destroy(m);
            UnityEngine.Object.Destroy(root);
        }

        static int Darker(int c, double k) =>
            (Js.RoundInt(((c >> 16) & 255) * k) << 16) | (Js.RoundInt(((c >> 8) & 255) * k) << 8) | Js.RoundInt((c & 255) * k);

        static V3 v(double x, double h, double y) => new V3(x, h, y);

        static readonly Quat Identity = Quat.Identity;

        /// <summary>
        /// Where to draw a prefab: its footprint centred on map point `at`, turned by
        /// `rot` about the vertical (its front, +Z, then faces (sin rot, cos rot)), scaled
        /// by (sx, sy, sz) in its own axes. Three space.
        /// </summary>
        public static Mat4 Placement(Prefab p, double atX, double atY, double rot, double sx, double sy, double sz, double lift = 0)
        {
            var centre = Mat4.Translation(-(p.minX + p.sizeX / 2), -p.minY, -(p.minZ + p.sizeZ / 2));
            var m = Mat4.Compose(atX, lift, atY, Quat.AboutY(rot), sx, sy, sz);
            return m.Multiply(centre);
        }

        public static double CarScale(Prefab p) => CAR_LENGTH / p.sizeZ;

        static bool IsWaterTerrain(byte t) => Terrain.IsWater(t);

        // Ground --------------------------------------------------------------------------------

        Mesh BuildGround(Rng rng)
        {
            var tiles = model.tiles;
            int w = model.width, h = model.height;
            var b = new MeshBuilder();
            bool Wet(int x, int y) => Grid.InBounds(x, y, w, h) && IsWaterTerrain(tiles.terrain[Grid.Index(x, y, w)]);
            int Jitter(int c, double amount)
            {
                double k = 1 - rng.Next() * amount;
                int r = Js.RoundInt(((c >> 16) & 255) * k);
                int g = Js.RoundInt(((c >> 8) & 255) * (1 - (1 - k) * 0.5));
                int bl = Js.RoundInt((c & 255) * k);
                return (r << 16) | (g << 8) | bl;
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    byte terrain = tiles.terrain[i];
                    byte overlay = tiles.overlay[i];
                    byte use = layout.use[i];
                    if (IsWaterTerrain(terrain))
                    {
                        b.Rect(x, y, x + 1, y + 1, BED_Y, C.bed);
                        continue;
                    }
                    int color;
                    if (use == Use.HOUSE || overlay == Overlay.ROAD_MAIN || overlay == Overlay.ROAD_STREET || overlay == Overlay.BRIDGE) color = C.paving;
                    else if (overlay == Overlay.PLAZA) color = (x + y) % 2 != 0 ? C.plazaA : C.plazaB;
                    else if (use == Use.GARDEN || use == Use.COMMUNITY_GARDEN) color = Jitter(C.soil, 0.08);
                    else if (terrain == Terrain.SAND) color = Jitter(C.sand, 0.05);
                    else if (terrain == Terrain.ROCK) color = Jitter(C.rock, 0.08);
                    else if (use == Use.YARD || use == Use.SHED) color = Jitter(C.lawn, 0.05);
                    else color = Jitter(C.grass, 0.08);
                    b.Rect(x, y, x + 1, y + 1, 0, color);
                    int bank = terrain == Terrain.SAND ? C.sand : C.bank;
                    if (Wet(x, y - 1)) b.Quad(v(x, 0, y), v(x + 1, 0, y), v(x + 1, BED_Y, y), v(x, BED_Y, y), bank, new V3(0, 0, -1));
                    if (Wet(x, y + 1)) b.Quad(v(x, 0, y + 1), v(x + 1, 0, y + 1), v(x + 1, BED_Y, y + 1), v(x, BED_Y, y + 1), bank, new V3(0, 0, 1));
                    if (Wet(x - 1, y)) b.Quad(v(x, 0, y), v(x, 0, y + 1), v(x, BED_Y, y + 1), v(x, BED_Y, y), bank, new V3(-1, 0, 0));
                    if (Wet(x + 1, y)) b.Quad(v(x + 1, 0, y), v(x + 1, 0, y + 1), v(x + 1, BED_Y, y + 1), v(x + 1, BED_Y, y), bank, new V3(1, 0, 0));
                }
            }
            b.Quad(v(0, 0, 0), v(w, 0, 0), v(w, -SLAB, 0), v(0, -SLAB, 0), C.slab, new V3(0, 0, -1));
            b.Quad(v(0, 0, h), v(w, 0, h), v(w, -SLAB, h), v(0, -SLAB, h), C.slab, new V3(0, 0, 1));
            b.Quad(v(0, 0, 0), v(0, 0, h), v(0, -SLAB, h), v(0, -SLAB, 0), C.slab, new V3(-1, 0, 0));
            b.Quad(v(w, 0, 0), v(w, 0, h), v(w, -SLAB, h), v(w, -SLAB, 0), C.slab, new V3(1, 0, 0));
            return b.Build("ground");
        }

        Material AddWater()
        {
            var tiles = model.tiles;
            int w = model.width, h = model.height;
            var pos = new List<Vector3>();
            var deep = new List<Vector2>();
            var idx = new List<int>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte t = tiles.terrain[Grid.Index(x, y, w)];
                    if (!IsWaterTerrain(t)) continue;
                    int start = pos.Count;
                    pos.Add(Space.P(x, WATER_Y, y));
                    pos.Add(Space.P(x, WATER_Y, y + 1));
                    pos.Add(Space.P(x + 1, WATER_Y, y + 1));
                    pos.Add(Space.P(x + 1, WATER_Y, y));
                    float d = t == Terrain.WATER_DEEP ? 1 : 0;
                    for (int k = 0; k < 4; k++) deep.Add(new Vector2(d, 0));
                    // Three's (0, 1, 2), (0, 2, 3), wound the other way for Unity.
                    idx.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
                }
            }
            var material = Mats.WaterMaterial();
            if (idx.Count == 0) return material;
            var mesh = new Mesh { name = "water", indexFormat = pos.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(pos);
            mesh.SetUVs(0, deep);
            mesh.SetTriangles(idx, 0, true);
            meshes.Add(mesh);
            owned.Add(stage.Add(new Drawable(mesh, material, Matrix4x4.identity, false)));
            return material;
        }

        Mesh BuildRoads()
        {
            var b = new MeshBuilder();
            var streets = layout.roads.Where(r => r.kind != RoadKind.Path).ToList();
            bool InMap(Vec2 p) => p.x > 0.01 && p.y > 0.01 && p.x < model.width - 0.01 && p.y < model.height - 0.01;
            void Layer(double y, double extra, int color)
            {
                foreach (var r in streets)
                {
                    b.Ribbon(r.points, r.width + extra, y, color);
                    foreach (var p in new[] { r.points[0], r.points[r.points.Count - 1] }) if (InMap(p)) b.Disc(p, (r.width + extra) / 2, y, color);
                }
            }
            foreach (var br in layout.bridges)
            {
                double half = br.width / 2 + Layout.SIDEWALK + 0.25;
                b.Ribbon(br.points, half * 2, 0.01, C.stone);
                b.Wall(br.points, half, BED_Y, 0.01, C.stoneShade);
                b.Wall(br.points, -half, BED_Y, 0.01, C.stoneShade);
                foreach (int side in new[] { 1, -1 }) b.Parapet(br.points, side * (half - 0.07), 0.12, 0.01, 0.24, C.stone, C.stoneShade);
            }
            Layer(0.014, Layout.SIDEWALK * 2 + 0.1, C.curb);
            Layer(0.018, Layout.SIDEWALK * 2, C.sidewalk);
            Layer(0.022, 0.08, C.curb);
            Layer(0.026, 0, C.asphalt);
            foreach (var r in layout.roads.Where(rd => rd.kind == RoadKind.Path))
            {
                b.Ribbon(r.points, r.width, 0.012, C.path);
                foreach (var p in new[] { r.points[0], r.points[r.points.Count - 1] }) b.Disc(p, r.width / 2, 0.012, C.path);
            }
            foreach (var r in streets)
            {
                double len = Geo.PolylineLength(r.points);
                var samples = Geo.SamplePolyline(r.points, 0.1);
                double travelled = 0;
                var run = new List<Vec2>();
                void Flush()
                {
                    if (run.Count > 1) b.Ribbon(run, 0.07, 0.03, C.lane);
                    run = new List<Vec2>();
                }
                for (int k = 1; k < samples.Count; k++)
                {
                    double seg = Geo.Dist(samples[k - 1], samples[k]);
                    double mid = travelled + seg / 2;
                    travelled += seg;
                    if (mid < 1.2 || mid > len - 1.2 || mid % 0.9 > 0.45)
                    {
                        Flush();
                        continue;
                    }
                    if (run.Count == 0) run.Add(samples[k - 1]);
                    run.Add(samples[k]);
                }
                Flush();
            }
            return b.Build("roads");
        }
    }
}

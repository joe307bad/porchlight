// The 3D town for a TownModel, built as GameObjects from its layout: tiled
// ground with sunken rivers, roads with sidewalks and kerbs, stone bridges,
// Kenney houses, shops and towers fitted to every blueprint footprint (long
// street fronts split into rows), their doors opening onto furnished rooms
// behind see-through windows, fenced yards with sheds and vegetable beds, trees,
// street lamps, street name signs, parked cars and the plaza fountain, and
// (WorldPorts) the airport and the docks.
// One-of-a-kind geometry is combined by static batching; the many copies of
// the same models are drawn with GPU instancing. The
// map's (x, y) is Coords.Map's (x, -z).

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using UnityEngine;
using UnityEngine.Rendering;
using Grid = Porchlight.Core.Grid;
using Rect = Porchlight.Explore.Rect;

namespace Porchlight.Rendering
{
    public sealed class Lamp
    {
        public double x, y, h, phase;
        public bool faulty;
        /// <summary>The bulb, which glows at night, and the pool of light it throws on the ground.</summary>
        public Renderer bulb, pool;
    }

    public sealed class DoorState
    {
        /// <summary>The room it opens into, and the middle of its doorway.</summary>
        public int room;
        public Vec2 at;
        /// <summary>How far open, 0..1.</summary>
        public double open;
        /// <summary>What it turns on (its hinge, or a garage door's top edge): shut, about which axis, and how far (degrees) open.</summary>
        public Transform pivot;
        public Quaternion shut;
        public Vector3 axis;
        public float turn;
    }

    public struct ParkedPose
    {
        public double x, y, heading;
        public int model;
    }

    public sealed partial class TownWorld
    {
        public const double WATER_Y = -0.14;
        const double BED_Y = -0.45;
        const double SLAB = 1.6;
        const double LAMP_SPACING = 7;
        const double FLOOR_H = 0.95;
        public const double CAR_LENGTH = 1.05;
        /// <summary>How far doors open (degrees): just short of square, clear of the wall beside them.</summary>
        const float DOOR_SWING = 86;
        const float DOOR_LIFT = 85;
        const double LAMP_HEIGHT = 0.95;
        const double LAMP_REACH = 0.3;
        static readonly double BUILDING_TURN = Math.PI;

        static class C
        {
            public const int grass = 0x86bd5b, lawn = 0x95c964, sand = 0xe6d49a, rock = 0x9c968c, soil = 0x7a5638, paving = 0xbdb6a4;
            public const int plazaA = 0xddd2b6, plazaB = 0xcdc1a2, bed = 0x6f8a86, bank = 0x8f7656, slab = 0x6b5238, asphalt = 0x6b6b6b;
            public const int sidewalk = 0xd8d3bf, curb = 0xb3ad98, lane = 0xece7d2, parking = 0x747472, driveway = 0xb9b2a0, path = 0xc9a46e, stone = 0xa39d8a, stoneShade = 0x7d7768;
            public const int shedWall = 0xb07a4a, shedRoof = 0x5b4636, lampPost = 0x3b3f47, water = 0x5d9bc8;
        }

        const int GLASS = 0x8fb8e8;

        public readonly GameObject root;
        /// <summary>
        /// What never moves and is one of a kind (ground, roads, the
        /// building shells): combined by static batching once built.
        /// </summary>
        readonly Transform still;
        /// <summary>
        /// Copies of the same models (trees, crops, fences, lamps, parked cars): drawn
        /// with GPU instancing, which shares one copy of each mesh. Static batching
        /// them would copy every one's geometry, more than a big town fits in memory.
        /// </summary>
        readonly Transform props;
        public readonly TownModel model;
        public readonly IsoLayout layout;
        public SidewalkNetwork sidewalks => layout.sidewalks;
        public readonly StreetNames names;
        public List<Signpost> signs = new List<Signpost>();
        public readonly List<Box> boxes = new List<Box>();
        public readonly List<Lamp> lamps = new List<Lamp>();
        /// <summary>
        /// Windows with a television on behind them: a few of every building's, in
        /// groups that each flicker blue on their own at night.
        /// </summary>
        public readonly List<Renderer> screens = new List<Renderer>();
        public const int SCREEN_GROUPS = 6;
        /// <summary>Roughly one window in this many has the television on.</summary>
        const double SCREEN_SHARE = 1 / 14.0;
        public Vec2 fountainAt;
        public readonly List<Room> rooms = new List<Room>();
        public readonly List<RoomPlan> plans = new List<RoomPlan>();
        public readonly List<DoorState> doors = new List<DoorState>();
        public Solids solids;
        public readonly List<ParkedPose> parked = new List<ParkedPose>();

        readonly ModelLibrary library;
        readonly Palette palette;
        readonly List<GameObject> shells = new List<GameObject>();
        readonly List<Piece> pieces = new List<Piece>();
        /// <summary>The buildings' own walls at walking height, as solids.</summary>
        readonly List<Rect> groundWalls = new List<Rect>();
        readonly List<GameObject> parkedCars = new List<GameObject>();
        readonly Dictionary<int, Interior> interiors = new Dictionary<int, Interior>();

        public TownWorld(TownModel model, ModelLibrary library)
        {
            this.model = model;
            this.library = library;
            palette = new Palette();
            layout = Layout.Build(model);
            names = StreetNaming.Name(model, layout.roads);
            root = new GameObject("town");
            still = new GameObject("still").transform;
            still.SetParent(root.transform, false);
            props = new GameObject("props").transform;
            props.SetParent(root.transform, false);
            var rng = new Rng($"{model.seed}::iso");

            BuildGround(rng).Build("ground", still);
            AddWater();
            BuildRoads().Build("roads", still);
            var sheds = new MeshBuilder(palette);
            AddHouses(rng);
            // Rooms are furnished from a stream of their own, so the rest of the town doesn't move when they change.
            Furnish(new Rng($"{model.seed}::rooms"));
            AddScreens();
            AddDoors();
            for (int i = 0; i < rooms.Count; i++) InteriorOf(i);
            AddYards(sheds, rng);
            AddTrees(rng);
            AddPlazaDecor(rng);
            AddParkedCars();
            AddAirport();
            AddDocks();
            if (!sheds.Empty) sheds.Build("sheds", still);
            AddLamps(rng);
            signs = Signs.Plan(model, layout, names);
            AddStreetSigns();
            AddFountain();
            _ = palette.Material;
            StaticBatchingUtility.Combine(still.gameObject);
        }

        public void SetParkedVisible(int i, bool visible) => Show(parkedCars[i], visible);

        public void SetShellVisible(int room, bool visible)
        {
            Show(shells[room], visible);
        }


        /// <summary>Shown or hidden without moving anything (static-batched objects can't be switched off whole).</summary>
        static void Show(GameObject go, bool visible)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        }

        public void Dispose()
        {
            foreach (var i in interiors.Values) i.Dispose();
            UnityEngine.Object.Destroy(root);
        }

        static int Darker(int c, double k) =>
            (Numbers.RoundInt(((c >> 16) & 255) * k) << 16) | (Numbers.RoundInt(((c >> 8) & 255) * k) << 8) | Numbers.RoundInt((c & 255) * k);

        static V3 v(double x, double h, double y) => new V3(x, h, y);

        static bool IsWaterTerrain(byte t) => Terrain.IsWater(t);

        /// <summary>A Unity primitive mesh (cube, cylinder, sphere) for simple shapes.</summary>
        static GameObject Shape(PrimitiveType type, Transform parent, Vector3 at, Vector3 size, Material material, bool shadows = true, string name = null)
        {
            var go = GameObject.CreatePrimitive(type);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.name = name ?? type.ToString().ToLowerInvariant();
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }

        // Ground --------------------------------------------------------------------------------

        MeshBuilder BuildGround(Rng rng)
        {
            var tiles = model.tiles;
            int w = model.width, h = model.height;
            var b = new MeshBuilder(palette);
            bool Wet(int x, int y) => Grid.InBounds(x, y, w, h) && IsWaterTerrain(tiles.terrain[Grid.Index(x, y, w)]);
            int Jitter(int c, double amount)
            {
                double k = 1 - rng.Next() * amount;
                int r = Numbers.RoundInt(((c >> 16) & 255) * k);
                int g = Numbers.RoundInt(((c >> 8) & 255) * (1 - (1 - k) * 0.5));
                int bl = Numbers.RoundInt((c & 255) * k);
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
                    else if (use == Use.DRIVEWAY) color = C.driveway;
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
            return b;
        }

        /// <summary>The water's surface: glossy, so it catches the sky and the sun.</summary>
        void AddWater()
        {
            var tiles = model.tiles;
            int w = model.width, h = model.height;
            var b = new MeshBuilder(palette);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte t = tiles.terrain[Grid.Index(x, y, w)];
                    if (IsWaterTerrain(t)) b.Rect(x, y, x + 1, y + 1, WATER_Y, t == Terrain.WATER_DEEP ? 0x4b86b8 : C.water);
                }
            if (b.Empty) return;
            var go = new GameObject("water");
            go.transform.SetParent(still, false);
            go.AddComponent<MeshFilter>().sharedMesh = b.Build("water");
            var r = go.AddComponent<MeshRenderer>();
            var m = new Material(palette.Material) { name = "water" };
            m.SetFloat("_Smoothness", 0.85f);
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        MeshBuilder BuildRoads()
        {
            var b = new MeshBuilder(palette);
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
                double half = br.width / 2 + Layout.PARKING + Layout.SIDEWALK + 0.25;
                b.Ribbon(br.points, half * 2, 0.01, C.stone);
                b.Wall(br.points, half, BED_Y, 0.01, C.stoneShade);
                b.Wall(br.points, -half, BED_Y, 0.01, C.stoneShade);
                foreach (int side in new[] { 1, -1 }) b.Parapet(br.points, side * (half - 0.07), 0.12, 0.01, 0.24, C.stone, C.stoneShade);
            }
            // Out from the middle: the asphalt, a parking lane each side (a shade
            // lighter, edged with a thin line), the kerb, then the sidewalk.
            double park = Layout.PARKING * 2;
            Layer(0.012, park + Layout.SIDEWALK * 2 + 0.1, C.curb);
            Layer(0.016, park + Layout.SIDEWALK * 2, C.sidewalk);
            Layer(0.02, park + 0.08, C.curb);
            Layer(0.023, park, C.parking);
            Layer(0.026, 0, C.asphalt);
            foreach (var r in streets)
            {
                // Stopping short of the junctions, as the centre line does.
                double length = Geo.PolylineLength(r.points);
                if (length < 3) continue;
                var middle = Geo.SubPolyline(r.points, 1.3, length - 1.3);
                foreach (int side in new[] { 1, -1 }) b.Ribbon(Layout.OffsetPolyline(middle, side * r.width / 2), 0.04, 0.028, C.lane);
            }
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
            return b;
        }
    }
}

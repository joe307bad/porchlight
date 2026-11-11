// The 3D town for a TownModel, built as GameObjects from its layout: tiled
// ground with sunken rivers, roads with sidewalks and kerbs, stone bridges,
// Kenney houses, shops and towers fitted to every blueprint footprint (long
// street fronts split into rows), their doors opening onto furnished rooms
// behind see-through windows, fenced yards with sheds and vegetable beds, trees,
// street lamps, street name signs, parked cars and the plaza fountain, and
// (WorldPorts) the airport and the docks, and (WorldZoo) the zoo.
// One-of-a-kind geometry is combined by static batching; the many copies of
// the same models are drawn with GPU instancing. The
// map's (x, y) is Coords.Map's (x, -z).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        /// <summary>
        /// In the county (CountyDrive): the countryside goes on past the map's edge, so no slab walls
        /// round it; and a road space draws only its own ground (not the tiles the next one draws).
        /// </summary>
        readonly bool inCounty;

        /// <summary>
        /// What a town's world is built from that needs no engine (its layout, street names and signposts):
        /// worked out on a worker thread where there are threads, so the main one only builds.
        /// </summary>
        public sealed class Prepared
        {
            public IsoLayout layout;
            public StreetNames names;
            public List<Signpost> signs;

            public static Prepared Of(TownModel model)
            {
                var layout = Layout.Build(model);
                var names = StreetNaming.Name(model, layout.roads);
                return new Prepared { layout = layout, names = names, signs = Signs.Plan(model, layout, names) };
            }
        }

        /// <summary>
        /// `stepwise`: only begun; <see cref="Build"/> does the rest, a part at a time. `inCounty`: no slab round it.
        /// `prepared`: its layout, names and signposts already worked out (Prepared.Of), else they're worked out here.
        /// </summary>
        public TownWorld(TownModel model, ModelLibrary library, bool stepwise = false, bool inCounty = false, Prepared prepared = null)
        {
            this.model = model;
            this.library = library;
            this.inCounty = inCounty;
            palette = new Palette();
            layout = prepared?.layout ?? Layout.Build(model);
            names = prepared?.names ?? StreetNaming.Name(model, layout.roads);
            plannedSigns = prepared?.signs;
            root = new GameObject("town");
            still = new GameObject("still").transform;
            still.SetParent(root.transform, false);
            props = new GameObject("props").transform;
            props.SetParent(root.transform, false);
            if (!stepwise) foreach (var _ in Build()) { }
        }

        readonly List<Signpost> plannedSigns;

        /// <summary>Rooms furnished, doors hung and signposts put up between one part and the next: each part a few milliseconds.</summary>
        const int ROOMS_A_PART = 16, DOORS_A_PART = 64, SIGNS_A_PART = 12, BATCH_A_PART = 250;
        /// <summary>Rows of tiles planted, sidewalks lit, meshes surveyed for the weather and walls kept clear of doorways, between one part and the next.</summary>
        const int TREE_ROWS_A_PART = 24, LAMP_PATHS_A_PART = 40, SURVEY_A_PART = 400, WALLS_A_PART = 300;

        /// <summary>
        /// A part's name ends in this while it waits on a worker thread: nothing to do this frame but
        /// ask again next (CountyDrive stops building for the frame, rather than spinning on it).
        /// </summary>
        public const string WAITING = "…";

        /// <summary>The town's parts, built in turn: each one's name once it's done.</summary>
        public IEnumerable<string> Build()
        {
            var rng = new Rng($"{model.seed}::iso");
            // The ground's tiles worked out off the main thread too, where there are threads (waited for, so
            // the town's random numbers come out in the same order).
            MeshBuilder ground = null;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
            var laying = Task.Run(() => BuildGround(rng));
            while (!laying.IsCompleted) yield return "ground" + WAITING;
            ground = laying.Result;
#endif
            (ground ?? BuildGround(rng)).Build("ground", still);
            yield return "ground";
            AddWater();
            yield return "water";
            // The town's longest part: its streets' shapes worked out off the main thread where there are
            // threads, and only the mesh made here.
            MeshBuilder roads = null;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
            var drawing = Task.Run(BuildRoads);
            while (!drawing.IsCompleted) yield return "roads" + WAITING;
            roads = drawing.Result;
#endif
            (roads ?? BuildRoads()).Build("roads", still);
            yield return "roads";
            var sheds = new MeshBuilder(palette);
            AddHouses(rng);
            yield return "houses";
            // Rooms are furnished from a stream of their own, so the rest of the town doesn't move when they change.
            foreach (var part in Furnish(new Rng($"{model.seed}::rooms"), ROOMS_A_PART)) yield return "furnishing" + part;
            AddScreens();
            yield return "screens";
            foreach (var part in AddDoors(DOORS_A_PART)) yield return "doors" + part;
            yield return "rooms";
            const int ROOMS_A_STEP = 64;
            for (int i = 0; i < rooms.Count; i++)
            {
                InteriorOf(i);
                if (i % ROOMS_A_STEP == ROOMS_A_STEP - 1) yield return "interiors" + i / ROOMS_A_STEP;
            }
            yield return "interiors";
            AddYards(sheds, rng);
            yield return "yards";
            foreach (var part in AddTrees(rng)) yield return "trees" + part;
            yield return "trees";
            AddPlazaDecor(rng);
            yield return "plazas";
            AddParkedCars();
            yield return "parked";
            AddAirport();
            AddZoo();
            AddDocks();
            if (!sheds.Empty) sheds.Build("sheds", still);
            yield return "ports";
            foreach (var part in AddLamps(rng)) yield return "lamps" + part;
            yield return "places";
            signs = plannedSigns ?? Signs.Plan(model, layout, names);
            yield return "signposts";
            foreach (var part in AddStreetSigns(SIGNS_A_PART)) yield return "signs" + part;
            AddFountain();
            _ = palette.Material;
            yield return "signs";
            foreach (var part in Survey()) yield return "survey" + part;
            yield return "survey";
            // Combined a share at a time (each share its own batches), not the whole town in one frame.
            var parts = still.GetComponentsInChildren<MeshRenderer>().Select(r => r.gameObject).ToArray();
            for (int i = 0; i < parts.Length; i += BATCH_A_PART)
            {
                StaticBatchingUtility.Combine(parts.Skip(i).Take(BATCH_A_PART).ToArray(), still.gameObject);
                if (i + BATCH_A_PART < parts.Length) yield return "batching" + i / BATCH_A_PART;
            }
            yield return "batching";
        }

        /// <summary>The material the ground, roads and sheds are drawn with (the weather wets it).</summary>
        public Material GroundMaterial => palette.Material;

        /// <summary>How high the town reaches over each tile (map y * width + x): where rain and snow land.</summary>
        public float[] surface;

        /// <summary>Everything standing that could hold snow: its mesh, where it is, and its bounds (world).</summary>
        public readonly List<(Mesh mesh, Matrix4x4 at, Bounds bounds)> tops = new List<(Mesh, Matrix4x4, Bounds)>();

        /// <summary>
        /// Of those, the buildings and the sheds: what snow settles on. `building` is which (its
        /// shell, the room's number), so its snow goes with its roof when that's lifted; -1 the sheds.
        /// </summary>
        public readonly List<(Mesh mesh, Matrix4x4 at, Bounds bounds, int building)> roofs = new List<(Mesh, Matrix4x4, Bounds, int)>();

        /// <summary>A building's shell (what's hidden while you're in its room).</summary>
        public Transform ShellOf(int building) => shells[building].transform;

        /// <summary>For the weather: the town's height over each tile, and what stands in it (before batching merges it), a share of its meshes a part.</summary>
        IEnumerable<int> Survey()
        {
            int w = model.width, h = model.height;
            surface = new float[w * h];
            var parked = new HashSet<Transform>();
            foreach (var p in parkedCars) parked.Add(p.transform);
            var buildings = new Dictionary<Transform, int>();
            for (int i = 0; i < shells.Count; i++) buildings[shells[i].transform] = i;
            // In the town's own terms (where its corner is the origin): a space of the county is built off to
            // one side of the one you're in, and is at the origin by the time its weather starts.
            // Asked afresh for each mesh: a space being built moves (with every other) when you drive into the next.
            int seen = 0;
            foreach (var f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (++seen % SURVEY_A_PART == 0) yield return seen / SURVEY_A_PART - 1;
                if (f == null) continue;
                var origin = root.transform.position;
                var toTown = Matrix4x4.Translate(-origin);
                var r = f.GetComponent<MeshRenderer>();
                if (r == null || f.sharedMesh == null) continue;
                bool isParked = false;
                for (var t = f.transform; t != null && !isParked; t = t.parent) isParked = parked.Contains(t);
                if (isParked) continue; // they drive off
                var b = r.bounds;
                b.center -= origin;
                if (b.max.y < 0.1f) continue; // the ground, roads and water
                tops.Add((f.sharedMesh, toTown * f.transform.localToWorldMatrix, b));
                int owner = -2;
                for (var t = f.transform; t != null && owner == -2; t = t.parent)
                {
                    if (buildings.TryGetValue(t, out int i)) owner = i;
                    else if (t.name == "sheds") owner = -1;
                }
                if (owner != -2) roofs.Add((f.sharedMesh, toTown * f.transform.localToWorldMatrix, b, owner));
                if (b.size.x > 24 || b.size.z > 24) continue; // one mesh for things all over town (the sheds): no single top
                int x0 = Mathf.Max(0, Mathf.FloorToInt(b.min.x)), x1 = Mathf.Min(w - 1, Mathf.FloorToInt(b.max.x));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(-b.max.z)), y1 = Mathf.Min(h - 1, Mathf.FloorToInt(-b.min.z));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = y * w + x;
                        if (b.max.y > surface[i]) surface[i] = b.max.y;
                    }
            }
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

        /// <summary>
        /// Dispose a share at a time, for a town let go while you play (far off, or the farthest of those built):
        /// taken apart a few hundred pieces a part. A whole town destroyed in one frame stalls it, and so does
        /// switching it off whole.
        /// </summary>
        public IEnumerable<int> DisposeSteps(int perPart = 400)
        {
            foreach (var i in interiors.Values) i.Dispose();
            interiors.Clear();
            yield return 0;
            // Deepest first, so each piece goes before what it hangs from.
            var all = root.GetComponentsInChildren<Transform>(true);
            int done = 0;
            for (int k = all.Length - 1; k > 0; k--)
            {
                if (all[k] != null) UnityEngine.Object.Destroy(all[k].gameObject);
                if (++done % perPart == 0) yield return done / perPart;
            }
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
                    if (model.foreign != null && model.foreign[i] != 0) continue; // the next stretch's to draw
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
            if (inCounty) return b; // the countryside carries on
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
            // A cul-de-sac's end widens into its bulb.
            double bulb = Gen.Raster.BulbRadius(model.@params);
            bool Bulb(RoadStroke r, bool atEnd) => layout.bulbs.Exists(q => layout.roads[q.edge] == r && q.atEnd == atEnd);
            void Layer(double y, double extra, int color)
            {
                foreach (var r in streets)
                {
                    b.Ribbon(r.points, r.width + extra, y, color);
                    foreach (bool atEnd in new[] { false, true })
                    {
                        var p = atEnd ? r.points[r.points.Count - 1] : r.points[0];
                        if (!InMap(p) || model.IsRoad) continue; // a road space's road carries on into the next
                        if (Bulb(r, atEnd)) b.Disc(p, bulb + extra / 2, y, color, 36);
                        else b.Disc(p, (r.width + extra) / 2, y, color);
                    }
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
                double from = Bulb(r, false) ? bulb + 0.3 : 1.3, to = length - (Bulb(r, true) ? bulb + 0.3 : 1.3);
                if (length < 3 || to - from < 0.4) continue;
                var middle = Geo.SubPolyline(r.points, from, to);
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
                double from = Bulb(r, false) ? bulb + 0.2 : 1.2, to = len - (Bulb(r, true) ? bulb + 0.2 : 1.2);
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
                    if (mid < from || mid > to || mid % 0.9 > 0.45)
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

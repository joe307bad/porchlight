// A mission's fires, and the means to put them out (MissionPlay's fire and
// turret blocks). A role's building burns: some of its windows (the front's
// first), and its front door, each a Blaze, with a Plume over the roof. Water
// on a burning window for long enough (douse_time) puts it out; the door goes
// out with the last window. The water comes from a turret on your car's roof:
// once the story offers it, press A (Enter) at the wheel, stopped, and you're
// up there, turning it all the way round and spraying, the camera behind it;
// B (or G) climbs back down. The crew stand round your car with hoses, and
// spray at wherever your water lands (a little help to it). The water's arc
// is worked out here, and the Jets draw the same arc: it stops at the burning
// building's walls or the ground, and counts on a window where it crosses it.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using Avatar = Porchlight.Rendering.Avatar;

namespace Porchlight.App
{
    public sealed class Firefight
    {
        /// <summary>How much further than its edges water still counts on a window (tiles).</summary>
        const double MARGIN = 0.06;
        /// <summary>The turret's reach up and down (radians), and how fast it turns at full stick: up and down, as a share of round.</summary>
        const double PITCH_LOW = -0.6, PITCH_HIGH = 0.95, PITCH_SHARE = 0.6;
        const double BARREL = 0.2;
        /// <summary>How much each of the crew adds to your water on a window.</summary>
        const double CREW_HELP = 0.2;
        const double CREW_SPEED = 3.2;
        /// <summary>Narrower than this (tiles), a pane is too small to burn.</summary>
        const double SMALLEST = 0.07;
        /// <summary>The straight steps the water's arc is followed in, to see what it hits.</summary>
        const int ARC_STEPS = 32;
        /// <summary>How high the turret's nozzle is, about, when there's no car to say.</summary>
        const double NOZZLE_UP = 0.8;
        /// <summary>How far behind a wall's face (tiles) a window set in it can be.</summary>
        const double RECESS = 0.15;

        sealed class Opening
        {
            public Blaze blaze;
            public Vec2 at, outward;
            public double half, y0, y1;
            /// <summary>Water on it so far (seconds' worth).</summary>
            public double water;
        }

        sealed class Fire
        {
            public int room;
            public readonly List<Opening> windows = new List<Opening>();
            /// <summary>The building made solid while it burns, for water that misses the windows and the door to land on.</summary>
            public List<GameObject> solid;
            public Plume plume;
            public double roof;
            /// <summary>Windows put out that the story hasn't heard of yet (@doused).</summary>
            public int doused;
            public bool Out => windows.All(w => !w.blaze.Burning);
        }

        sealed class Hand
        {
            public Avatar avatar;
            public Walker walker;
            public Jet jet;
            public Hose hose;
            public GameObject nozzle;
        }

        readonly ExploreView view;
        readonly ModelLibrary library;
        readonly GameInput input;
        readonly Rng rng;
        readonly Func<string, double> setting;
        readonly Dictionary<string, Fire> fires = new Dictionary<string, Fire>();
        GameObject root;

        /// <summary>The turret offered (the role whose building it's aimed at first; null: none), and whether you're up at it.</summary>
        string offered;
        bool manned;
        double yaw, pitch;
        int crewCount;
        string crewLook;
        GameObject turret;
        Transform monitor;
        Jet jet;
        readonly List<Hand> crew = new List<Hand>();
        /// <summary>Where your water lands (map x, y and height), while you spray.</summary>
        Vector3? lands;
        /// <summary>Where your water would land, shown while you're up at the turret: orange on a burning window.</summary>
        GameObject reticle;
        MeshRenderer reticleRenderer;
        static Material red, steel, hoseMaterial, aim, aimHot;
        static Mesh ring;

        public Firefight(ExploreView view, ModelLibrary library, GameInput input, Rng rng, Func<string, double> setting)
        {
            this.view = view;
            this.library = library;
            this.input = input;
            this.rng = rng;
            this.setting = setting;
            root = new GameObject("mission fires");
            root.transform.SetParent(view.world.root.transform, false);
        }

        public bool Manned => manned;

        // Fires.

        /// <summary>
        /// The role's building (room) alight: up to `count` of the windows your engine can reach from
        /// `from` (where it parks), and the door, each to be put out with water. How many that is
        /// (the door and the windows).
        /// </summary>
        public int Ignite(string role, int room, Door door, Vec2 from, int count)
        {
            Extinguish(role);
            if (count <= 0) return 0;
            var world = view.world;
            var f = new Fire { room = room, roof = world.RoofOf(room) };
            // Not the door's own glass, nor any too small to see burn.
            var all = world.WindowsOf(room)
                .Where(w => w.half * 2 >= SMALLEST && w.y1 - w.y0 >= SMALLEST)
                .Where(w => !(Math.Abs(Geo.Dot(new Vec2(w.at.x - door.at.x, w.at.y - door.at.y), door.along)) < door.width / 2 + 0.02 && w.y0 < door.height && Geo.Dot(w.outward, door.outward) > 0.7))
                .ToList();
            // Water only goes in a window from in front of it: those the parking spot is well out in front of, facing the street first.
            bool Seen(TownWorld.Facade w) => Geo.Dot(new Vec2(from.x - w.at.x, from.y - w.at.y), w.outward) > 0.25;
            var seen = all.Where(Seen).ToList();
            var front = rng.Shuffle(seen.Where(w => Geo.Dot(w.outward, door.outward) > 0.7).ToList());
            var sides = rng.Shuffle(seen.Where(w => Geo.Dot(w.outward, door.outward) <= 0.7).ToList());
            // And none behind a porch's rail or roof, or anything else of the building: only those its
            // water can be thrown into from the turret, parked there.
            f.solid = world.Solid(room);
            double up = view.OwnVehicle != null && view.OwnVehicle.kind == VehicleKind.Car ? Base().z + 0.13 : NOZZLE_UP;
            var turret = new Vector3((float)from.x, (float)from.y, (float)up);
            foreach (var w in front.Concat(sides))
            {
                if (f.windows.Count >= Math.Min(count, 8)) break;
                var o = new Opening { at = w.at, outward = w.outward, half = w.half, y0 = w.y0, y1 = w.y1 };
                if (!Reachable(turret, o)) continue;
                o.blaze = new Blaze(root.transform, w.at, w.outward, w.half, w.y0, w.y1, rng.Next() * 10);
                f.windows.Add(o);
            }
            double doorHeight = Math.Min(door.height, f.roof * 0.8);
            var doorway = new Opening { at = door.at, outward = door.outward, half = door.width / 2, y0 = 0.02, y1 = doorHeight };
            doorway.blaze = new Blaze(root.transform, door.at, door.outward, door.width / 2 * 0.9, 0.02, doorHeight, rng.Next() * 10);
            f.windows.Add(doorway);
            var r = world.rooms[room];
            f.plume = new Plume(root.transform, new Vec2((r.x0 + r.x1) / 2, (r.y0 + r.y1) / 2), f.roof, Math.Min(r.x1 - r.x0, r.y1 - r.y0) * 0.6);
            fires[role] = f;
            return f.windows.Count;
        }

        /// <summary>The role's fire, gone at once.</summary>
        public void Extinguish(string role)
        {
            if (!fires.TryGetValue(role ?? "", out var f)) return;
            foreach (var w in f.windows) w.blaze.Dispose();
            f.plume.Dispose();
            foreach (var go in f.solid) UnityEngine.Object.Destroy(go);
            fires.Remove(role);
        }

        /// <summary>Every one of the role's burning windows is out (@out).</summary>
        public bool Out(string role) => fires.TryGetValue(role ?? "", out var f) && f.Out;

        /// <summary>One of the role's windows has gone out since the story last asked (@doused).</summary>
        public bool TakeDoused(string role)
        {
            if (!fires.TryGetValue(role ?? "", out var f) || f.doused == 0) return false;
            f.doused--;
            return true;
        }

        // The turret.

        /// <summary>The turret on your car's roof, yours to go up to, aimed first at `towards`; `count` of the crew, looking like `look`, by your car. Null: none.</summary>
        public void Offer(string role, Vec2? towards, int count, string look)
        {
            if (role == null)
            {
                Withdraw();
                return;
            }
            offered = role;
            crewCount = count;
            crewLook = look;
            if (towards != null) aimAt = towards;
            if (manned) PlaceCrew();
        }

        Vec2? aimAt;

        void Withdraw()
        {
            Dismount();
            offered = null;
            aimAt = null;
            if (turret != null) UnityEngine.Object.Destroy(turret);
            turret = null;
            if (reticle != null) UnityEngine.Object.Destroy(reticle);
            reticle = null;
            jet?.Dispose();
            jet = null;
            ClearCrew();
        }

        /// <summary>Up to the turret, if it's offered and you're at your car's wheel, stopped: true if you went.</summary>
        public bool TryMount()
        {
            var car = view.OwnVehicle;
            if (offered == null || manned || car == null || !view.DrivingMyCar || car.kind != VehicleKind.Car || Math.Abs(car.speed) > 0.3) return false;
            manned = true;
            car.speed = 0;
            BuildTurret();
            // Pointing at the fire to begin with, a little up.
            var at = aimAt ?? new Vec2(car.x + car.Forward.x, car.y + car.Forward.y);
            yaw = DetMath.Atan2(at.x - car.x, at.y - car.y);
            pitch = 0.3;
            PlaceCrew();
            return true;
        }

        void Dismount()
        {
            if (!manned) return;
            manned = false;
            lands = null;
            jet?.Spray(false, default, default, 0, default, default);
            foreach (var h in crew) h.jet.Spray(false, default, default, 0, default, default);
            if (turret != null) turret.SetActive(false);
            if (reticle != null) reticle.SetActive(false);
        }

        /// <summary>The turret's keys while you're up there: true if it used the key.</summary>
        public bool OnKey(Key code)
        {
            if (!manned) return false;
            if (code == Key.G || code == Key.B)
            {
                Dismount();
                return true;
            }
            return code == Key.Space || code == Key.Enter || KeyAims(code);
        }

        static bool KeyAims(Key k) => k == Key.W || k == Key.A || k == Key.S || k == Key.D || k == Key.UpArrow || k == Key.DownArrow || k == Key.LeftArrow || k == Key.RightArrow;

        public string Prompt() => input.Connected
            ? "Water turret · stick aims · hold A (or RT or RB) to spray · B to climb down"
            : "Water turret · arrows or WASD aim · hold Space to spray · G to climb down";

        /// <summary>The camera while you're up at the turret: behind it, looking where it points.</summary>
        public (Vector3 at, Vector3 look)? Camera()
        {
            if (!manned || monitor == null) return null;
            var nozzle = Nozzle();
            var d = new Vec2(Math.Sin(yaw), Math.Cos(yaw));
            var at = Coords.Map(nozzle.x - d.x * 2.6, nozzle.y - d.y * 2.6, nozzle.z + 0.95);
            double ahead = 3.5;
            var look = Coords.Map(nozzle.x + d.x * ahead, nozzle.y + d.y * ahead, nozzle.z + Math.Tan(pitch) * ahead * 0.7);
            return (at, look);
        }

        Material Red => red ??= Materials.Lit(0xc8261e, 0.3f);
        Material Steel => steel ??= Materials.Lit(0xc9ccd1, 0.6f);

        void BuildTurret()
        {
            if (turret != null)
            {
                turret.SetActive(true);
                return;
            }
            turret = new GameObject("water turret");
            turret.transform.SetParent(root.transform, false);
            Part(PrimitiveType.Cylinder, turret.transform, new Vector3(0, 0.02f, 0), new Vector3(0.14f, 0.02f, 0.14f), Red);
            Part(PrimitiveType.Cylinder, turret.transform, new Vector3(0, 0.08f, 0), new Vector3(0.05f, 0.05f, 0.05f), Steel);
            monitor = new GameObject("monitor").transform;
            monitor.SetParent(turret.transform, false);
            monitor.localPosition = new Vector3(0, 0.13f, 0);
            Part(PrimitiveType.Sphere, monitor, Vector3.zero, Vector3.one * 0.06f, Red);
            // The barrel lies along the monitor's z.
            var barrel = Part(PrimitiveType.Cylinder, monitor, new Vector3(0, 0, (float)BARREL / 2), new Vector3(0.035f, (float)BARREL / 2, 0.035f), Steel);
            barrel.transform.localRotation = Quaternion.Euler(90, 0, 0);
            Part(PrimitiveType.Cylinder, monitor, new Vector3(0, 0, (float)BARREL), new Vector3(0.05f, 0.012f, 0.05f), Red).transform.localRotation = Quaternion.Euler(90, 0, 0);
            jet = new Jet(root.transform, 0.045);
            reticle = new GameObject("reticle");
            reticle.transform.SetParent(root.transform, false);
            reticle.AddComponent<MeshFilter>().sharedMesh = ring ??= Ring();
            reticleRenderer = reticle.AddComponent<MeshRenderer>();
            reticleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            reticleRenderer.receiveShadows = false;
            aim ??= Materials.Unlit(Color.white);
            aimHot ??= Materials.Unlit(Coords.Hex(0xff8a1f));
        }

        static GameObject Part(PrimitiveType type, Transform parent, Vector3 at, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            return go;
        }

        /// <summary>Where the turret stands: on the roof of your car (map x, y and height).</summary>
        Vector3 Base()
        {
            var car = view.OwnVehicle;
            var m = library.Get(Catalog.CARS[car.model]);
            double roof = m.size.y * CarView.ScaleOf(m) * 0.92 + CarView.LIFT;
            var f = car.Forward;
            return new Vector3((float)(car.x - f.x * 0.12), (float)(car.y - f.y * 0.12), (float)roof);
        }

        /// <summary>The tip of the barrel (map x, y and height).</summary>
        Vector3 Nozzle()
        {
            var b = Base();
            var d = Aim();
            return new Vector3(b.x + d.x * (float)BARREL, b.y + d.y * (float)BARREL, b.z + 0.13f + d.z * (float)BARREL);
        }

        /// <summary>Which way the turret points: map x, y and up, one long.</summary>
        Vector3 Aim() => new Vector3((float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)(Math.Cos(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch));

        static Vector3 World(Vector3 map) => Coords.Map(map.x, map.y, map.z);

        static Vector3 WorldDir(Vector3 map) => new Vector3(map.x, map.z, -map.y);

        // Each frame.

        /// <summary>A moment: aiming and spraying (unless you're reading), the water on the windows, and the fires burning or going out.</summary>
        public void Update(double dt, bool reading)
        {
            double time = Time.realtimeSinceStartupAsDouble;
            var car = view.OwnVehicle;
            if (manned && (car == null || !view.DrivingMyCar)) Dismount();
            bool spraying = false;
            if (manned)
            {
                if (!reading) Steer(dt);
                spraying = !reading && Spraying();
                if (input.Pad.Pressed(Button.B) && !reading) Dismount();
            }
            if (manned)
            {
                PlaceTurret(dt);
                PlaceReticle();
            }
            lands = null;
            if (manned && spraying) Spray(dt);
            else jet?.Spray(false, default, default, 0, default, default);
            UpdateCrew(dt, spraying);
            foreach (var f in fires.Values)
            {
                int burning = 0;
                foreach (var w in f.windows)
                {
                    if (w.blaze.Burning)
                    {
                        w.blaze.SetLevel(1 - w.water / Math.Max(0.1, setting("douse_time")));
                        if (w.water >= setting("douse_time"))
                        {
                            w.blaze.Out();
                            f.doused++;
                        }
                    }
                    if (w.blaze.Burning) burning++;
                    w.blaze.Update(dt, time);
                }
                f.plume.SetLevel(burning / (double)Math.Max(1, f.windows.Count));
            }
        }

        /// <summary>For the smoke run: spraying as if the button were held.</summary>
        public bool holdSpray;

        bool Spraying()
        {
            var kb = Keyboard.current;
            var pad = input.Pad;
            // A and RB as well as RT: Firefox gives some pads no triggers.
            return holdSpray || pad.rt > 0.3 || pad.Held(Button.A) || pad.Held(Button.RB) || (kb != null && (kb.spaceKey.isPressed || kb.enterKey.isPressed));
        }

        void Steer(double dt)
        {
            double x = 0, y = 0;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y -= 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y += 1;
            }
            var pad = input.Pad;
            x += pad.move.x + pad.look.x;
            y += pad.move.y + pad.look.y;
            x = Math.Max(-1, Math.Min(1, x));
            y = Math.Max(-1, Math.Min(1, y));
            double rate = setting("aim_speed");
            // Right turns it right (as walking does), up raises it.
            yaw -= x * rate * dt;
            pitch = Math.Max(PITCH_LOW, Math.Min(PITCH_HIGH, pitch - y * rate * PITCH_SHARE * dt));
        }

        void PlaceTurret(double dt)
        {
            var b = Base();
            turret.transform.localPosition = World(b);
            turret.transform.localRotation = Quaternion.identity;
            monitor.localRotation = Quaternion.LookRotation(WorldDir(Aim()), Vector3.up);
        }

        /// <summary>The reticle where your water would land, lying on what it lands on, and bigger the further off that is so it's as easy to see.</summary>
        void PlaceReticle()
        {
            var n = Nozzle();
            var v = Aim() * (float)setting("water_speed");
            var (t, facing) = Landing(n, v);
            var p = At(n, v, t);
            bool hot = fires.Values.Any(f => f.windows.Any(w => w.blaze.Burning && Wets(n, v, t, w)));
            reticle.SetActive(true);
            reticleRenderer.sharedMaterial = hot ? aimHot : aim;
            reticle.transform.position = World(p) + facing * 0.015f;
            reticle.transform.rotation = Quaternion.LookRotation(facing, Mathf.Abs(facing.y) > 0.98f ? WorldDir(Aim()) : Vector3.up);
            float far = Vector3.Distance(n, p);
            reticle.transform.localScale = Vector3.one * (0.05f + 0.018f * far);
        }

        /// <summary>A ring with four ticks pointing in, flat in x and y, one across, seen from either side.</summary>
        static Mesh Ring()
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i = v.Count;
                v.AddRange(new[] { a, b, c, d });
                t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3, i, i + 2, i + 1, i, i + 3, i + 2 });
            }
            const int SIDES = 10;
            for (int s = 0; s < SIDES; s++)
            {
                float a0 = s * Mathf.PI * 2 / SIDES, a1 = (s + 1) * Mathf.PI * 2 / SIDES;
                Vector3 P(float a, float r) => new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0);
                Quad(P(a0, 0.38f), P(a0, 0.5f), P(a1, 0.5f), P(a1, 0.38f));
            }
            for (int k = 0; k < 4; k++)
            {
                var d = new Vector3(Mathf.Cos(k * Mathf.PI / 2), Mathf.Sin(k * Mathf.PI / 2), 0);
                var side = new Vector3(-d.y, d.x, 0) * 0.04f;
                Quad(d * 0.14f - side, d * 0.14f + side, d * 0.3f + side, d * 0.3f - side);
            }
            var m = new Mesh { name = "reticle" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Your water: its arc, where it lands, and how much it puts on any burning window it crosses on the way.</summary>
        void Spray(double dt)
        {
            var n = Nozzle();
            var v = Aim() * (float)setting("water_speed");
            var (t, facing) = Landing(n, v);
            var p = At(n, v, t);
            lands = p;
            jet.Spray(true, World(n), WorldDir(v), t, World(p), facing);
            int helping = crew.Count;
            foreach (var f in fires.Values)
                foreach (var w in f.windows)
                    if (w.blaze.Burning && Wets(n, v, t, w)) w.water += dt * (1 + CREW_HELP * helping);
        }

        /// <summary>When (seconds) water thrown from `n` at `v` lands: in a burning building's window, else on its wall, else on the ground (and which way what it lands on faces, world).</summary>
        (double t, Vector3 facing) Landing(Vector3 n, Vector3 v) => Landing(n, v, fires.Values.SelectMany(f => f.windows));

        (double t, Vector3 facing) Landing(Vector3 n, Vector3 v, IEnumerable<Opening> openings)
        {
            double g = Jet.GRAVITY;
            double ground = (v.z + Math.Sqrt(v.z * v.z + 2 * g * Math.Max(0, n.z))) / g, t = ground, first = double.PositiveInfinity;
            Vector3 facing = Vector3.up;
            // Along the arc in short straight steps, into whatever of a burning building it meets first.
            for (int i = 0; i < ARC_STEPS; i++)
            {
                    double t0 = ground * i / ARC_STEPS, t1 = ground * (i + 1) / ARC_STEPS;
                    var a = World(At(n, v, t0));
                    var b = World(At(n, v, t1));
                    var d = b - a;
                if (Physics.Raycast(a, d.normalized, out var hit, d.magnitude, 1 << TownWorld.SOLID_LAYER, QueryTriggerInteraction.Ignore))
                {
                    t = t0 + (t1 - t0) * hit.distance / Math.Max(1e-6, d.magnitude);
                    facing = hit.normal;
                    break;
                }
            }
            // A window (or the door) is where the water goes if it gets there first, or no further
            // past a wall's face than the wall is thick (the window's set in it); not deep behind it.
            double walled = t;
            double speed = Math.Sqrt(v.x * v.x + v.y * v.y);
            foreach (var w in openings)
            {
                double hit = Cross(n, v, w.at, w.outward, w.half + MARGIN, w.y0 - MARGIN, w.y1 + MARGIN);
                if (hit < ground && hit < first && (hit - walled) * speed <= RECESS)
                {
                    first = t = hit;
                    facing = WorldDir(new Vector3((float)w.outward.x, (float)w.outward.y, 0));
                }
            }
            return (t, facing);
        }

        /// <summary>Whether water thrown from `n` at `v`, landing after `t` seconds, crosses the window on the way.</summary>
        static bool Wets(Vector3 n, Vector3 v, double t, Opening w) =>
            Cross(n, v, w.at, w.outward, w.half + MARGIN, w.y0 - MARGIN, w.y1 + MARGIN) <= t + 0.05;

        /// <summary>Whether water from a turret standing at `turret` (map x, y and its height) can be put on `w`, at some tip of its barrel.</summary>
        bool Reachable(Vector3 turret, Opening w)
        {
            var to = new Vec2(w.at.x + w.outward.x * 0.05, w.at.y + w.outward.y * 0.05);
            double turn = DetMath.Atan2(to.x - turret.x, to.y - turret.y), speed = setting("water_speed");
            for (double tip = PITCH_LOW; tip <= PITCH_HIGH; tip += 0.01)
            {
                var d = new Vector3((float)(Math.Sin(turn) * Math.Cos(tip)), (float)(Math.Cos(turn) * Math.Cos(tip)), (float)Math.Sin(tip));
                var n = turret + d * (float)BARREL;
                var v = d * (float)speed;
                if (Wets(n, v, Landing(n, v, new[] { w }).t, w)) return true;
            }
            return false;
        }

        /// <summary>For the smoke run: the turret turned and tipped to put its water on a burning window, the lowest arc that reaches one. False if none is in reach.</summary>
        /// <summary>For the smoke run: how many solid parts each fire's building has, for water to land on.</summary>
        public string Walls() => string.Join("; ", fires.Values.Select(f =>
        {
            // Turned on the door and tipped from low to high: where the water lands, measured out
            // from the door's line (behind the front shows as below about -0.15).
            var door = f.windows[f.windows.Count - 1];
            double keepYaw = yaw, keepPitch = pitch, deepest = double.PositiveInfinity;
            int onHouse = 0, tries = 0;
            for (pitch = PITCH_LOW; pitch <= PITCH_HIGH; pitch += 0.05)
            {
                var b0 = Nozzle();
                yaw = DetMath.Atan2(door.at.x - b0.x, door.at.y - b0.y);
                var n = Nozzle();
                var v = Aim() * (float)setting("water_speed");
                var (t, facing) = Landing(n, v);
                var p = At(n, v, t);
                tries++;
                if (facing.y > 0.9f && p.z < 0.01) continue;
                onHouse++;
                // The roof is behind the front, rightly: only where it lands on a wall counts.
                if (Math.Abs(facing.y) < 0.5f) deepest = Math.Min(deepest, Geo.Dot(new Vec2(p.x - door.at.x, p.y - door.at.y), door.outward));
            }
            yaw = keepYaw;
            pitch = keepPitch;
            return $"{f.solid.Count} solid parts; aimed at the door, {onHouse} of {tries} land on the house, on a wall at the deepest {deepest:0.00} out from the door's line";
        }));

        /// <summary>For the smoke run: each burning window, how high and far it is, and whether it's in front of the nozzle.</summary>
        public string Burning()
        {
            var n = Nozzle();
            return string.Join("; ", fires.Values.SelectMany(f => f.windows.Where(w => w.blaze.Burning).Select(w =>
            {
                var r = view.world.rooms[f.room];
                double ahead = new[] { new Vec2(r.x0, r.y0), new Vec2(r.x1, r.y0), new Vec2(r.x0, r.y1), new Vec2(r.x1, r.y1) }.Max(c => Geo.Dot(new Vec2(c.x - w.at.x, c.y - w.at.y), w.outward));
                var v = Aim() * (float)setting("water_speed");
                return $"{w.y0:0.00}-{w.y1:0.00} up, {Geo.Dist(new Vec2(n.x, n.y), w.at):0.00} off, {Geo.Dot(new Vec2(n.x - w.at.x, n.y - w.at.y), w.outward):0.00} out in front (nozzle {n.z:0.00} up), room {ahead:0.00} in front of it, roof {f.roof:0.00}, aim yaw {yaw:0.00} pitch {pitch:0.00} lands after {Landing(n, v).t:0.00} s, window at {Cross(n, v, w.at, w.outward, w.half + MARGIN, -10, 10):0.00} s";
            })));
        }

        public bool AimAtBurning()
        {
            if (!manned) return false;
            foreach (var f in fires.Values)
                foreach (var w in f.windows)
                {
                    if (!w.blaze.Burning) continue;
                    var to = new Vec2(w.at.x + w.outward.x * 0.05, w.at.y + w.outward.y * 0.05);
                    for (pitch = PITCH_LOW; pitch <= PITCH_HIGH; pitch += 0.005)
                    {
                        // The nozzle moves a little as the barrel turns: turn twice.
                        for (int k = 0; k < 2; k++)
                        {
                            var b = Nozzle();
                            yaw = DetMath.Atan2(to.x - b.x, to.y - b.y);
                        }
                        var n = Nozzle();
                        var v = Aim() * (float)setting("water_speed");
                        if (Wets(n, v, Landing(n, v).t, w)) return true;
                    }
                }
            return false;
        }

        /// <summary>
        /// When (seconds) water thrown from `n` at `v` (map x, y, up) goes into the wall at `at`
        /// facing `outward`, within `half` of `at` along it and between heights `low` and `high`;
        /// infinity if it doesn't.
        /// </summary>
        static double Cross(Vector3 n, Vector3 v, Vec2 at, Vec2 outward, double half, double low, double high)
        {
            double toward = v.x * outward.x + v.y * outward.y;
            double outside = (n.x - at.x) * outward.x + (n.y - at.y) * outward.y;
            if (toward >= -1e-6 || outside < 0) return double.PositiveInfinity;
            double t = -outside / toward;
            var p = At(n, v, t);
            double along = (p.x - at.x) * -outward.y + (p.y - at.y) * outward.x;
            if (Math.Abs(along) > half || p.z < low || p.z > high || p.z < 0) return double.PositiveInfinity;
            return t;
        }

        static Vector3 At(Vector3 n, Vector3 v, double t) =>
            new Vector3((float)(n.x + v.x * t), (float)(n.y + v.y * t), (float)(n.z + v.z * t - Jet.GRAVITY * t * t / 2));

        // The crew.

        void ClearCrew()
        {
            foreach (var h in crew)
            {
                h.avatar.Dispose();
                h.jet.Dispose();
                h.hose.Dispose();
                UnityEngine.Object.Destroy(h.nozzle);
            }
            crew.Clear();
        }

        /// <summary>The crew round your car: on its side towards the fire, along its length, facing the fire.</summary>
        void PlaceCrew()
        {
            ClearCrew();
            var car = view.OwnVehicle;
            if (car == null || crewCount <= 0) return;
            var f = car.Forward;
            var side = new Vec2(-f.y, f.x);
            var towards = aimAt ?? new Vec2(car.x + side.x, car.y + side.y);
            if (Geo.Dot(side, new Vec2(towards.x - car.x, towards.y - car.y)) < 0) side = new Vec2(-side.x, -side.y);
            hoseMaterial ??= Materials.Lit(0x8a2a1c, 0.2f);
            for (int i = 0; i < crewCount; i++)
            {
                // Along the car (and past its ends), two rows out from its side.
                double along = crewCount == 1 ? 0 : -0.75 + 1.5 * i / (crewCount - 1);
                double out_ = car.halfWidth + 0.3 + (i % 2) * 0.22;
                var at = new Vec2(car.x + f.x * along + side.x * out_, car.y + f.y * along + side.y * out_);
                var look = string.IsNullOrEmpty(crewLook) ? Catalog.FIREFIGHTER : crewLook;
                var walker = new Walker(look, at.x, at.y) { heading = DetMath.Atan2(towards.x - at.x, towards.y - at.y) };
                var avatar = Avatar.Of(look, rng.Next(), root.transform);
                avatar.SnapYaw(walker.heading);
                var hose = new Hose(root.transform, hoseMaterial, 0.014);
                var nozzle = Part(PrimitiveType.Cylinder, root.transform, Vector3.zero, new Vector3(0.022f, 0.045f, 0.022f), Steel);
                crew.Add(new Hand { avatar = avatar, walker = walker, jet = new Jet(root.transform, 0.035), hose = hose, nozzle = nozzle });
            }
        }

        /// <summary>The crew stand by your car, and spray where your water lands while you spray.</summary>
        void UpdateCrew(double dt, bool spraying)
        {
            var car = view.OwnVehicle;
            foreach (var h in crew)
            {
                var w = h.walker;
                var target = lands != null ? new Vec2(lands.Value.x, lands.Value.y) : aimAt;
                if (target != null) w.heading += Orbit.ShortestTurn(w.heading, DetMath.Atan2(target.Value.x - w.x, target.Value.y - w.y)) * Math.Min(1, dt * 4);
                w.Moved(0, 0, dt);
                h.avatar.Update(w, dt);
                // The branch held out in front at the hip, and the hose back along the ground to the car.
                var d = new Vec2(Math.Sin(w.heading), Math.Cos(w.heading));
                var hand = new Vector3((float)(w.x + d.x * 0.1), (float)(w.y + d.y * 0.1), (float)(Avatar.PERSON_HEIGHT * 0.5));
                double lift = 0.2;
                if (lands != null)
                {
                    var to = lands.Value;
                    double dx = to.x - hand.x, dy = to.y - hand.y, flat = Math.Max(0.05, Numbers.Hypot(dx, dy));
                    lift = Math.Atan2(to.z - hand.z, flat) + 0.25;
                }
                var dir = new Vector3((float)(d.x * Math.Cos(lift)), (float)(d.y * Math.Cos(lift)), (float)Math.Sin(lift));
                h.nozzle.transform.localPosition = World(hand + dir * 0.04f);
                h.nozzle.transform.localRotation = Quaternion.LookRotation(WorldDir(dir), Vector3.up) * Quaternion.Euler(90, 0, 0);
                if (car != null)
                {
                    var f = car.Forward;
                    var pump = new Vec2(car.x - f.x * 0.25, car.y - f.y * 0.25);
                    h.hose.Lay(
                        Coords.Map(pump.x, pump.y, 0.12),
                        Coords.Map(pump.x + (w.x - pump.x) * 0.3, pump.y + (w.y - pump.y) * 0.3, 0.016),
                        Coords.Map(w.x - d.x * 0.08, w.y - d.y * 0.08, 0.016),
                        World(hand));
                }
                if (spraying && lands != null) h.jet.Spray(true, World(hand + dir * 0.08f), WorldDir(Lob(hand, lands.Value)), Flight(hand, lands.Value), World(lands.Value), Vector3.up);
                else h.jet.Spray(false, default, default, 0, default, default);
            }
        }

        /// <summary>The throw (map x, y, up; tiles a second) that lands water from `from` on `to`: the lower arc, as fast as needs be.</summary>
        static Vector3 Lob(Vector3 from, Vector3 to)
        {
            double g = Jet.GRAVITY;
            double dx = to.x - from.x, dy = to.y - from.y, flat = Math.Max(0.05, Numbers.Hypot(dx, dy)), up = to.z - from.z;
            double least = Math.Sqrt(g * (up + Math.Sqrt(flat * flat + up * up)));
            double v = Math.Max(CREW_SPEED, least * 1.05);
            double v2 = v * v;
            double root = Math.Sqrt(Math.Max(0, v2 * v2 - g * (g * flat * flat + 2 * up * v2)));
            double angle = Math.Atan((v2 - root) / (g * flat));
            return new Vector3((float)(dx / flat * Math.Cos(angle) * v), (float)(dy / flat * Math.Cos(angle) * v), (float)(Math.Sin(angle) * v));
        }

        static double Flight(Vector3 from, Vector3 to)
        {
            var v = Lob(from, to);
            double flat = Math.Max(0.05, Numbers.Hypot(to.x - from.x, to.y - from.y));
            return flat / Math.Max(0.05, Numbers.Hypot(v.x, v.y));
        }

        public void Dispose()
        {
            Withdraw();
            foreach (var role in fires.Keys.ToList()) Extinguish(role);
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
        }
    }
}

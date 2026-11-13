// A mission's flying (MissionPlay's building blocks for it): a plane of your
// own to fly (your_plane: Santa's sleigh, say), rings hung in the sky to fly
// through (ring, @ring), the story taking the controls from you, gently, to
// bring the plane down on a role's roof or in the street outside (land,
// @landed), out of your seat on the roof (A: a jump out onto the roof), across
// to the chimney and down it, the camera swinging round to watch from the
// street, and back up it to your seat (chimney, @chimney), a hop out of your
// seat onto the pavement (put_you "kerb" from the plane, A), and a Christmas
// tree in a corner of the role's building to put a present under (tree,
// @gift). The sums are Explore/Sleigh.cs's (SkyRing, Landing, RoofMap,
// RoofStop, ChimneyTrip, Hop); this puts the plane and you where they say,
// reads the roof off the building itself, and draws the ring, the chimney and
// the tree.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Rendering;
using UnityEngine;

namespace Porchlight.App
{
    public sealed partial class MissionPlay
    {
        /// <summary>A ring hangs this far out past the kerb in front of its building (tiles: over the street).</summary>
        const double RING_OUT = 2.5;
        /// <summary>How thick a ring's tube is (tiles).</summary>
        const double RING_TUBE = 0.13;
        static readonly Color RingGold = Coords.Hex(0xffc93a);
        /// <summary>How wide a chimney is (tiles), and how far down into the roof it goes.</summary>
        const double CHIMNEY_WIDE = 0.18, CHIMNEY_ROOTS = 0.35;
        static readonly Color Brick = Coords.Hex(0x9a3b2b), Snow = Coords.Hex(0xf4f7fb);
        /// <summary>How tall a Christmas tree stands, and the present under it (tiles).</summary>
        const double TREE_HEIGHT = 0.62, GIFT_SIZE = 0.15;
        /// <summary>How near a tree you come to put the present under it (tiles), and where you stand to (out from it).</summary>
        const double GIFT_REACH = 0.8, TREE_STAND = 0.42;
        /// <summary>The landing curve keeps clear of the roofs it passes until it's this near its spot (tiles).</summary>
        const double LANDING_CLEAR = 1.6;
        /// <summary>A plane set down in the street is this high (tiles): its runners on the road, but still up, so it lifts off on its own way when you fly on.</summary>
        const double KERB_HOVER = 0.002;
        /// <summary>How finely a roof is read (tiles between samples).</summary>
        const double ROOF_STEP = 0.08;
        /// <summary>A hop out of your seat onto the pavement: how long it takes (seconds) and how high over the line it goes (tiles).</summary>
        const double HOP_TIME = 0.6, HOP_HEIGHT = 0.18;
        /// <summary>
        /// The camera going down a chimney with you: how long it takes to swing round (seconds), and where it ends up,
        /// out over the street (how far round from straight out the front, how far up and back: radians, tiles);
        /// how long it watches the chimney once you're in it before you're inside by the tree.
        /// </summary>
        const double SHOT_TURN = 1.8, SHOT_SIDE = 0.55, SHOT_ELEVATION = 0.42, SHOT_DISTANCE = 3.2, SHOT_HOLD = 0.8;
        static readonly Func<double, double, double> Street = (x, y) => 0;

        /// <summary>What a role has of the sky: its ring, where the story brought your plane down at it, its chimney and its tree.</summary>
        sealed class SkyStop
        {
            public SkyRing? ring;
            public GameObject ringGo;
            public Renderer ringGlow;
            /// <summary>Flown through since it was hung (and not yet heard by the story).</summary>
            public bool through;
            /// <summary>Where the story's bringing (or brought) your plane down here ("roof" or "kerb", null: not), and whether it's down.</summary>
            public string landAt;
            public bool landed;
            /// <summary>Where the plane is to stand once down (its middle, and its altitude).</summary>
            public Vec2 at;
            public double atHeight;
            /// <summary>The roof as it is, and where the sleigh stands on it, Santa's seat, where he jumps out to, the chimney and how high its top is.</summary>
            public RoofMap roofMap;
            public RoofStop stop;
            public GameObject chimneyGo;
            /// <summary>A trip down or up the chimney done, and not yet heard by the story.</summary>
            public bool chimneyDone;
            public GameObject treeGo, giftGo;
            public Vec2 tree, treeStand;
            public string gift;
            public bool given;
        }

        /// <summary>The plane the story gave you (your_plane; a model index), -1 none.</summary>
        int storyPlane = -1;
        /// <summary>The story bringing your plane down, and where.</summary>
        Landing landing;
        Role landingFor;
        /// <summary>Down, and to be held still while you read (or until you jump out).</summary>
        bool parked;
        /// <summary>
        /// The jump out of your seat the story's waiting for you to make (A, or Enter): at which role, and whether
        /// it's down the chimney or onto the pavement (where to, and facing which way).
        /// </summary>
        Role jumpFor;
        bool jumpChimney;
        Vec2 hopTo;
        double hopFacing;
        /// <summary>Your hop out onto the pavement, on its way.</summary>
        Hop hop;
        /// <summary>Your trip down or up a chimney, at which role, and where it has you now (or your hop).</summary>
        ChimneyTrip trip;
        Role tripFor;
        (Vec2 at, double floor, double lift, double heading, double pace, bool shown)? pose;
        /// <summary>Down inside a chimney: how much longer the camera watches it before you're in by the tree (seconds).</summary>
        double? inChimney;
        /// <summary>The camera's move round the house while you're on its roof (null: the game's own camera).</summary>
        Shot shot;
        /// <summary>Where your plane was last frame (for flying through a ring).</summary>
        double? lastX, lastY, lastH;
        static Material ringMaterial, brickMaterial, snowMaterial;
        /// <summary>Trees with their presents under them, left in the houses the story's moved on from (until the mission's over).</summary>
        readonly List<GameObject> leftBehind = new List<GameObject>();
        static Mesh ringMesh;

        /// <summary>
        /// A camera move (map terms): round a point that follows you, from one way, height and distance to another
        /// over `turn` seconds, eased in and out.
        /// </summary>
        sealed class Shot
        {
            public double yaw0, elevation0, distance0, yaw1, elevation1, distance1, turn, time;
            public Vec2 anchor;
            public double anchorHeight;

            /// <summary>How far round it is (0 to 1), eased.</summary>
            public double K
            {
                get
                {
                    double k = Math.Max(0, Math.Min(1, time / Math.Max(1e-3, turn)));
                    return k * k * (3 - 2 * k);
                }
            }
        }

        /// <summary>How many rings you've flown through, landings made, chimney trips and presents left (the smoke run checks).</summary>
        public int RingsFlown { get; private set; }
        public int Landings { get; private set; }
        public int ChimneyTrips { get; private set; }
        public int Gifts { get; private set; }
        /// <summary>The role's ring, if one's hung (the smoke run flies through it).</summary>
        public SkyRing? RingOf(string role) => roles.TryGetValue(role ?? "", out var r) ? r.sky?.ring : null;
        /// <summary>Whether the story has your plane's controls (the smoke run keeps its hands off).</summary>
        public bool IsLanding => landing != null;
        /// <summary>Whether the story's waiting for you to jump out of your seat (the smoke run presses A).</summary>
        public bool JumpWaiting => jumpFor != null;
        /// <summary>Whether you're on your way out of your seat onto the pavement.</summary>
        public bool Hopping => hop != null;
        /// <summary>Where you're to stand to put the present under the role's tree (the smoke run walks there).</summary>
        public Vec2? TreeStandOf(string role) => roles.TryGetValue(role ?? "", out var r) && r.sky?.treeGo != null ? r.sky.treeStand : (Vec2?)null;

        void StartSky()
        {
            view.FlyOverride = FlyOverride;
            view.Scripted = Scripted;
        }

        void DisposeSky()
        {
            foreach (var r in roles.Values) ClearSky(r);
            foreach (var go in leftBehind) if (go != null) UnityEngine.Object.Destroy(go);
            leftBehind.Clear();
            StopMoving();
            var rig = view.OwnCarView?.Sleigh;
            if (rig != null) rig.Ground = null;
            if (view.FlyOverride == (Func<Vehicle, double, bool>)FlyOverride) view.FlyOverride = null;
            if (view.Scripted == (Func<(Vec2 at, double floor, double lift, double heading, double pace, bool shown)?>)Scripted) view.Scripted = null;
        }

        /// <summary>Whatever the story had your plane or you doing, stopped: a landing, a jump waited for, a hop or a chimney trip, the camera's move.</summary>
        void StopMoving()
        {
            landing = null;
            landingFor = null;
            trip = null;
            tripFor = null;
            hop = null;
            jumpFor = null;
            pose = null;
            inChimney = null;
            shot = null;
            parked = false;
        }

        /// <summary>A role's sky goes (place moved it on, or the mission's over): its ring, chimney and tree.</summary>
        void ClearSky(Role r)
        {
            var s = r.sky;
            if (s == null) return;
            ClearRing(s);
            if (s.chimneyGo != null) UnityEngine.Object.Destroy(s.chimneyGo);
            // A tree with its present under it stays where it is; one still waiting for it goes.
            if (s.given)
            {
                leftBehind.Add(s.treeGo);
                leftBehind.Add(s.giftGo);
            }
            else
            {
                if (s.treeGo != null) UnityEngine.Object.Destroy(s.treeGo);
                if (s.giftGo != null) UnityEngine.Object.Destroy(s.giftGo);
            }
            if (landingFor == r)
            {
                landing = null;
                landingFor = null;
            }
            if (jumpFor == r) jumpFor = null;
            if (tripFor == r)
            {
                trip = null;
                tripFor = null;
                pose = null;
                inChimney = null;
                shot = null;
            }
            r.sky = null;
        }

        static void ClearRing(SkyStop s)
        {
            if (s.ringGo != null) UnityEngine.Object.Destroy(s.ringGo);
            s.ringGo = null;
            s.ringGlow = null;
            s.ring = null;
        }

        // The story's building blocks.

        public void YourPlane(string plane, double height)
        {
            if (string.IsNullOrEmpty(plane))
            {
                GiveCarBack();
                return;
            }
            int model = Array.IndexOf(Catalog.CARS, plane);
            if (model < 0 || Catalog.KindOf(model) != VehicleKind.Plane) throw new Exception($"no such plane '{plane}' ({Catalog.SLEIGH}, planes/prop-small, ...)");
            if (height < 1 || height > 24) throw new Exception($"fly how high? {height} (1 to 24 tiles)");
            int had = view.CarModel >= 0 && Catalog.IsCar(view.CarModel) ? view.CarModel : -1;
            StopMoving();
            if (!view.TakeFlight(model, height)) throw new Exception("you can't take off from here");
            carBefore ??= had;
            storyPlane = model;
            lastX = lastY = lastH = null;
        }

        public void Ring(string role, double height, double size)
        {
            var r = RoleOf(role);
            r.sky ??= new SkyStop();
            ClearRing(r.sky);
            r.sky.through = false;
            if (size <= 0) return;
            if (height < 1 || height > 24) throw new Exception($"a ring how high? {height} (1 to 24 tiles)");
            if (size > 12) throw new Exception($"a ring how big? {size} (up to 12 tiles across)");
            // Out over the street in front of the building, facing you.
            var at = new Vec2(r.kerb.x + r.outward.x * RING_OUT, r.kerb.y + r.outward.y * RING_OUT);
            var you = view.PlayerPosition ?? r.door;
            var ring = SkyRing.Facing(at.x, at.y, height, size, you.x, you.y);
            r.sky.ring = ring;
            r.sky.ringGo = DrawRing(ring, out r.sky.ringGlow);
        }

        public void Land(string role, string at)
        {
            var r = RoleOf(role);
            if (at != "roof" && at != "kerb") throw new Exception($"land where? '{at}' (roof or kerb)");
            var car = view.OwnVehicle;
            if (car == null || car.kind != VehicleKind.Plane || !view.DrivingMyCar) throw new Exception("you're not flying a plane (your_plane first)");
            if (at == "roof" && r.room < 0) throw new Exception($"'{role}' has no roof to land on");
            bool sleigh = Catalog.CARS[car.model] == Catalog.SLEIGH;
            double seatBack = sleigh ? Catalog.SLEIGH_SEAT : 0;
            r.sky ??= new SkyStop();
            var s = r.sky;
            Vec2 to;
            double heading, height;
            if (at == "roof")
            {
                // Read off the building itself: along its ridge, the runners resting on the roof under them.
                var map = RoofMapOf(r);
                var stop = RoofStop.Place(map, car.x, car.y, r.outward, seatBack, sleigh ? Catalog.SLEIGH_SEAT_HEIGHT : 0.2,
                    sleigh ? Catalog.SLEIGH_RUNNER_FROM : 0, sleigh ? Catalog.SLEIGH_RUNNER_TO : car.halfLength,
                    sleigh ? Catalog.SLEIGH_RUNNER_GAUGE : car.halfWidth * 0.5, sleigh ? Catalog.SLEIGH_RUNNER_LOW : 0, CarView.LIFT);
                s.roofMap = map;
                s.stop = stop;
                to = stop.middle;
                heading = stop.heading;
                height = stop.altitude;
                if (s.chimneyGo != null) UnityEngine.Object.Destroy(s.chimneyGo);
                double under = map.At(stop.chimney.x, stop.chimney.y);
                s.chimneyGo = DrawChimney(stop.chimney, double.IsNaN(under) ? stop.rim - RoofStop.CHIMNEY_UP : under, stop.rim);
            }
            else
            {
                // In the street outside, along it: the role's parking spot if it has one, else the kerb's lane.
                var spot = r.spot ?? SpotFor(r);
                var seat = spot != null ? new Vec2(spot.Value.x, spot.Value.y) : r.kerb;
                heading = Toward(spot?.heading ?? DetMath.Atan2(r.outward.x, -r.outward.y), car, seat);
                height = KERB_HOVER;
                to = new Vec2(seat.x + Math.Cos(heading) * seatBack, seat.y + Math.Sin(heading) * seatBack);
                s.roofMap = null;
                s.stop = null;
            }
            s.landAt = at;
            s.landed = false;
            s.at = to;
            s.atHeight = height;
            landing = new Landing(car.x, car.y, car.altitude, car.heading, car.speed, to.x, to.y, height, heading);
            landingFor = r;
            parked = false;
        }

        /// <summary>Of a way along a line (a map heading) and its reverse, the one nearer the way from the plane to `to` (else its heading).</summary>
        static double Toward(double along, Vehicle car, Vec2 to)
        {
            double dx = to.x - car.x, dy = to.y - car.y;
            double wantX = dx, wantY = dy;
            if (Numbers.Hypot(dx, dy) < 1)
            {
                wantX = Math.Cos(car.heading);
                wantY = Math.Sin(car.heading);
            }
            return Math.Cos(along) * wantX + Math.Sin(along) * wantY >= 0 ? along : along + Math.PI;
        }

        /// <summary>The building box (its walls and how high its roof goes) that the role's building is, the one most over its room.</summary>
        Box RoofOf(Role r)
        {
            var room = view.world.rooms[r.room];
            Box? best = null;
            double most = 0;
            foreach (var b in view.world.boxes)
            {
                double w = Math.Min(b.x1, room.x1) - Math.Max(b.x0, room.x0), h = Math.Min(b.y1, room.y1) - Math.Max(b.y0, room.y0);
                if (w <= 0 || h <= 0 || w * h <= most) continue;
                most = w * h;
                best = b;
            }
            // No box over it (there always is): its room, with a roof a little over its walls.
            return best ?? new Box { x0 = room.x0, y0 = room.y0, x1 = room.x1, y1 = room.y1, top = room.height + 0.6 };
        }

        /// <summary>
        /// The role's roof, as it is: straight down onto the building's own meshes (made solid for the moment, as the
        /// fire's water lands on them) at every point of a grid over it. A building that can't be read (or anything
        /// that isn't one) has a flat roof at the top of its box.
        /// </summary>
        RoofMap RoofMapOf(Role r)
        {
            var box = RoofOf(r);
            var world = view.world;
            double eaves = world.rooms[r.room].height * 0.75;
            var solids = world.Solid(r.room);
            var root = world.root.transform;
            try
            {
                if (solids.Count > 0)
                {
                    var map = new RoofMap(box.x0, box.y0, box.x1, box.y1, ROOF_STEP, (x, y) =>
                    {
                        var from = root.TransformPoint(Coords.Map(x, y, box.top + 2));
                        if (!Physics.Raycast(from, root.TransformDirection(Vector3.down), out var hit, (float)(box.top + 3), 1 << TownWorld.SOLID_LAYER, QueryTriggerInteraction.Ignore))
                            return double.NaN;
                        double h = root.InverseTransformPoint(hit.point).y;
                        // A porch's floor or a step is no roof.
                        return h < eaves ? double.NaN : h;
                    });
                    if (!double.IsNaN(map.Top)) return map;
                }
            }
            finally
            {
                foreach (var go in solids) UnityEngine.Object.Destroy(go);
            }
            return new RoofMap(box.x0, box.y0, box.x1, box.y1, ROOF_STEP, (x, y) => box.top);
        }

        public void Chimney(string role, string way)
        {
            var r = RoleOf(role);
            var s = r.sky;
            if (way != "down" && way != "up") throw new Exception($"chimney which way? '{way}' (down or up)");
            if (s == null || s.landAt != "roof" || !s.landed || s.stop == null) throw new Exception($"there's no chimney to climb at '{role}' (land(\"{role}\", \"roof\") first)");
            if (trip != null) throw new Exception("you're already on your way up or down a chimney");
            s.chimneyDone = false;
            if (way == "down")
            {
                if (!view.DrivingMyCar) throw new Exception("down the chimney from where? (you're not in your seat on the roof)");
                // You jump out yourself (A): till then the sleigh's held where it is.
                jumpFor = r;
                jumpChimney = true;
                parked = true;
                return;
            }
            if (view.InCar) throw new Exception("up the chimney from where? (you're not down it)");
            StartTrip(r, false);
        }

        void StartTrip(Role r, bool down)
        {
            var s = r.sky;
            var st = s.stop;
            trip = new ChimneyTrip(down, st.seat, st.seatHeight, st.jumpTo, st.chimney, st.rim, s.roofMap.At);
            tripFor = r;
            pose = trip.At(0);
            inChimney = null;
            // The camera swings round to watch from out over the street (coming back up, from there round behind the sleigh).
            var street = Math.Atan2(r.outward.y, r.outward.x) + SHOT_SIDE * (Geo.Dot(new Vec2(st.chimney.x - st.seat.x, st.chimney.y - st.seat.y), new Vec2(-r.outward.y, r.outward.x)) >= 0 ? -1 : 1);
            var anchor = AnchorOf(st.chimney, st.rim);
            if (down)
            {
                var cam = view.CameraAt;
                double ox = cam.x - anchor.x, oy = -cam.z - anchor.y, oh = cam.y - anchor.z;
                double flat = Math.Max(1e-3, Numbers.Hypot(ox, oy));
                shot = new Shot
                {
                    yaw0 = Math.Atan2(oy, ox),
                    elevation0 = Math.Atan2(oh, flat),
                    distance0 = Math.Max(1.5, Math.Min(10, Numbers.Hypot(flat, oh))),
                    yaw1 = street,
                    elevation1 = SHOT_ELEVATION,
                    distance1 = SHOT_DISTANCE,
                    turn = SHOT_TURN,
                };
            }
            else
            {
                double behind = st.heading + Math.PI;
                shot = new Shot
                {
                    yaw0 = street,
                    elevation0 = SHOT_ELEVATION,
                    distance0 = SHOT_DISTANCE,
                    yaw1 = behind,
                    elevation1 = 0.35,
                    distance1 = 2.6,
                    turn = trip.Duration,
                };
            }
            shot.anchor = new Vec2(anchor.x, anchor.y);
            shot.anchorHeight = anchor.z;
        }

        /// <summary>What the camera looks at on a roof: between the chimney's top and you (map x, y and height).</summary>
        Vector3 AnchorOf(Vec2 chimney, double rim)
        {
            var at = pose?.at ?? chimney;
            double h = pose != null ? pose.Value.floor + pose.Value.lift + 0.15 : rim;
            return new Vector3((float)(chimney.x * 0.55 + at.x * 0.45), (float)(chimney.y * 0.55 + at.y * 0.45), (float)(rim * 0.55 + h * 0.45));
        }

        /// <summary>Out onto the pavement from the story's plane: once you press A, you hop out of your seat to `to`, facing `facing`.</summary>
        void WaitToHopOut(Role r, Vec2 to, double facing)
        {
            jumpFor = r;
            jumpChimney = false;
            hopTo = to;
            hopFacing = facing;
            parked = true;
        }

        /// <summary>Out of your seat, if the story's waiting for you to jump (A, Enter or Space): true if you did.</summary>
        bool TryJump()
        {
            var r = jumpFor;
            if (r == null || !view.DrivingMyCar || view.InDialogue || landing != null) return false;
            jumpFor = null;
            parked = false;
            if (jumpChimney)
            {
                if (r.sky?.stop == null) return false;
                view.LeaveSeat();
                StartTrip(r, true);
                return true;
            }
            var car = view.OwnVehicle;
            var seat = YourSeat();
            double from = car.altitude + CarView.LIFT + (Catalog.CARS[car.model] == Catalog.SLEIGH ? Catalog.SLEIGH_SEAT_HEIGHT : 0.2);
            view.LeaveSeat();
            hop = new Hop(seat, from, hopTo, 0, HOP_HEIGHT, HOP_TIME, Street);
            pose = hop.At(0);
            return true;
        }

        /// <summary>What to press to jump out of your seat, if the story's waiting for you to.</summary>
        string JumpPrompt(string key)
        {
            if (jumpFor == null || !view.DrivingMyCar || landing != null) return null;
            return jumpChimney ? $"Press {key} to jump out of the sleigh and down the chimney" : $"Press {key} to hop out of the sleigh";
        }

        public void Tree(string role, string gift)
        {
            var r = RoleOf(role);
            if (r.room < 0) throw new Exception($"'{role}' has no room for a tree");
            if (string.IsNullOrEmpty(gift)) throw new Exception("a tree needs a present to go under it (a model's name)");
            library.Get(gift);
            r.sky ??= new SkyStop();
            var s = r.sky;
            if (s.treeGo != null) UnityEngine.Object.Destroy(s.treeGo);
            if (s.giftGo != null) UnityEngine.Object.Destroy(s.giftGo);
            s.giftGo = null;
            var (at, stand) = TreeSpot(r);
            s.tree = at;
            s.treeStand = stand;
            s.gift = gift;
            s.given = false;
            var m = library.Get(Catalog.CHRISTMAS[0]);
            double k = TREE_HEIGHT / Math.Max(1e-3, m.size.y);
            s.treeGo = Props.Place(m, view.world.root.transform, at.x, at.y, rng.Next() * Math.PI * 2, k, k, k);
        }

        /// <summary>
        /// Where a tree goes in the role's room, and where you stand to put a present under it: a corner clear of
        /// the furniture, its doors and whoever keeps it, the furthest from the door it can be.
        /// </summary>
        (Vec2 at, Vec2 stand) TreeSpot(Role r)
        {
            var world = view.world;
            var room = world.rooms[r.room];
            var plan = r.room < world.plans.Count ? world.plans[r.room] : null;
            var doors = Interiors.Doors(room);
            var middle = new Vec2((room.x0 + room.x1) / 2, (room.y0 + room.y1) / 2);
            Vec2? best = null, bestStand = null;
            double bestScore = double.NegativeInfinity;
            const double INSET = 0.3, STEP = 0.12;
            for (double x = room.x0 + INSET; x <= room.x1 - INSET + 1e-9; x += STEP)
                for (double y = room.y0 + INSET; y <= room.y1 - INSET + 1e-9; y += STEP)
                {
                    var p = new Vec2(x, y);
                    double clear = 1;
                    if (plan != null)
                        foreach (var f in plan.furniture)
                        {
                            if (f.flat) continue;
                            double dx = Math.Max(0, Math.Max(f.box.x0 - x, x - f.box.x1)), dy = Math.Max(0, Math.Max(f.box.y0 - y, y - f.box.y1));
                            clear = Math.Min(clear, Numbers.Hypot(dx, dy));
                        }
                    if (clear < 0.2) continue;
                    double fromDoor = doors.Count > 0 ? doors.Min(d => Geo.Dist(d.at, p)) : 3;
                    if (fromDoor < 0.8) continue;
                    if (plan?.keeper != null && Numbers.Hypot(plan.keeper.Value.x - x, plan.keeper.Value.y - y) < 0.45) continue;
                    // Where you stand: out from it towards the middle of the room, somewhere you can.
                    double md = Geo.Dist(p, middle);
                    var toMiddle = md < 1e-6 ? new Vec2(0, 1) : new Vec2((middle.x - x) / md, (middle.y - y) / md);
                    var stand = new Vec2(x + toMiddle.x * TREE_STAND, y + toMiddle.y * TREE_STAND);
                    if (!view.CanStandAt(stand.x, stand.y)) continue;
                    double score = Math.Min(clear, 0.5) * 2 + Math.Min(fromDoor, 3) + md * 0.5;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = p;
                    bestStand = stand;
                }
            if (best != null) return (best.Value, bestStand.Value);
            // Nowhere clear: just inside the door, to one side.
            var inside = r.Inside;
            return (new Vec2(inside.x - r.outward.x * 0.5, inside.y - r.outward.y * 0.5), inside);
        }

        // Each frame.

        /// <summary>The story bringing your plane down (true: it has it); down, it holds it still while you read, or till you jump out.</summary>
        bool FlyOverride(Vehicle car, double dt)
        {
            if (landing == null)
            {
                if (!parked) return false;
                if (!view.InDialogue && jumpFor == null)
                {
                    parked = false;
                    return false;
                }
                car.speed = 0;
                return true;
            }
            var (x, y, h, heading, speed) = landing.Step(dt);
            // Clear of the roofs it passes, until it's coming down onto its spot.
            if (Numbers.Hypot(landing.toX - x, landing.toY - y) > LANDING_CLEAR)
            {
                double floor = view.FlyFloor(x, y);
                if (!double.IsInfinity(floor)) h = Math.Max(h, floor);
            }
            double was = car.altitude;
            car.x = x;
            car.y = y;
            car.altitude = h;
            car.speed = speed;
            double turn = Orbit.ShortestTurn(car.heading, heading);
            car.heading = heading;
            // Banked into its turns and nosed down as it descends, then level as it touches down.
            double k = Math.Min(1, dt * 3);
            car.bank += (Math.Max(-0.5, Math.Min(0.5, turn / Math.Max(1e-3, dt) * 0.25)) - car.bank) * k;
            car.pitch += (DetMath.Atan2((h - was) / Math.Max(1e-3, dt), Math.Max(1, speed)) - car.pitch) * k;
            if (!landing.Done) return true;
            car.bank = 0;
            car.pitch = 0;
            car.speed = 0;
            if (landingFor?.sky != null) landingFor.sky.landed = true;
            Landings++;
            landing = null;
            landingFor = null;
            parked = true;
            return true;
        }

        (Vec2 at, double floor, double lift, double heading, double pace, bool shown)? Scripted() => pose;

        void UpdateSky(double dt)
        {
            // Through a ring: your plane's move this frame crossed one's face, inside its rim.
            var car = view.OwnVehicle;
            if (car != null && car.kind == VehicleKind.Plane && view.DrivingMyCar)
            {
                if (lastX != null)
                    foreach (var r in roles.Values)
                    {
                        var s = r.sky;
                        if (s?.ring == null || s.through) continue;
                        if (!s.ring.Value.Through(lastX.Value, lastY.Value, lastH.Value, car.x, car.y, car.altitude)) continue;
                        s.through = true;
                        RingsFlown++;
                        ClearRing(s);
                    }
                lastX = car.x;
                lastY = car.y;
                lastH = car.altitude;
            }
            else lastX = lastY = lastH = null;
            // The sleigh down on a roof (or in the street): its reindeer stand on it.
            var rig = view.OwnCarView?.Sleigh;
            if (rig != null) rig.Ground = GroundUnder(car);
            // Out onto the pavement.
            if (hop != null)
            {
                pose = hop.Step(dt);
                if (hop.Done)
                {
                    hop = null;
                    pose = null;
                    view.PutPlayer(hopTo, hopFacing);
                }
            }
            // Down or up a chimney.
            if (trip != null)
            {
                if (shot != null)
                {
                    shot.time += dt;
                    // The camera keeps you and the chimney in view as you go.
                    var a = AnchorOf(tripFor?.sky?.stop?.chimney ?? shot.anchor, tripFor?.sky?.stop?.rim ?? shot.anchorHeight);
                    double ease = Math.Min(1, dt * 4);
                    shot.anchor = new Vec2(shot.anchor.x + (a.x - shot.anchor.x) * ease, shot.anchor.y + (a.y - shot.anchor.y) * ease);
                    shot.anchorHeight += (a.z - shot.anchorHeight) * ease;
                }
                if (!trip.Done) pose = trip.Step(dt);
                else if (trip.down && (inChimney ??= SHOT_HOLD) > 0) inChimney -= dt;
                else
                {
                    var r = tripFor;
                    bool down = trip.down;
                    trip = null;
                    tripFor = null;
                    pose = null;
                    inChimney = null;
                    shot = null;
                    ChimneyTrips++;
                    if (r?.sky != null)
                    {
                        r.sky.chimneyDone = true;
                        if (down)
                        {
                            // Out of the hearth by the tree (or just in at the door), facing into the room.
                            var s = r.sky;
                            if (s.treeGo != null) view.PutPlayer(s.treeStand, DetMath.Atan2(s.tree.x - s.treeStand.x, s.tree.y - s.treeStand.y));
                            else view.PutPlayer(r.Inside, DetMath.Atan2(-r.outward.x, -r.outward.y));
                        }
                        else
                        {
                            view.Board();
                            parked = true;
                        }
                    }
                }
            }
            // The rings glow and turn a little; a light over a tree waiting for its present.
            float glow = 1.6f + 0.8f * Mathf.Sin((float)Time.realtimeSinceStartupAsDouble * 3);
            foreach (var r in roles.Values)
                if (r.sky?.ringGlow != null)
                {
                    Materials.SetGlow(r.sky.ringGlow, RingGold, glow);
                    var t = r.sky.ringGo.transform;
                    var ring = r.sky.ring.Value;
                    t.localPosition = Coords.Map(ring.x, ring.y, ring.h + Math.Sin(Time.realtimeSinceStartupAsDouble * 1.4) * 0.12);
                }
        }

        /// <summary>What your plane's stood on, if it's down where the story set it (a roof, or the street): null in the air.</summary>
        Func<double, double, double> GroundUnder(Vehicle car)
        {
            if (car == null) return null;
            foreach (var r in roles.Values)
            {
                var s = r.sky;
                if (s == null || !s.landed) continue;
                if (Numbers.Hypot(car.x - s.at.x, car.y - s.at.y) > 0.05 || Math.Abs(car.altitude - s.atHeight) > 0.05) continue;
                return s.landAt == "roof" && s.roofMap != null ? s.roofMap.At : Street;
            }
            return null;
        }

        /// <summary>The camera's move round the house as you go down (or come up) its chimney, while it's on.</summary>
        (Vector3 at, Vector3 look)? SkyCamera()
        {
            var sh = shot;
            if (sh == null) return null;
            double k = sh.K;
            double yaw = sh.yaw0 + Orbit.ShortestTurn(sh.yaw0, sh.yaw1) * k;
            double elevation = sh.elevation0 + (sh.elevation1 - sh.elevation0) * k;
            double distance = sh.distance0 + (sh.distance1 - sh.distance0) * k;
            double flat = Math.Cos(elevation) * distance;
            var at = Coords.Map(sh.anchor.x + Math.Cos(yaw) * flat, sh.anchor.y + Math.Sin(yaw) * flat, sh.anchorHeight + Math.Sin(elevation) * distance);
            return (at, Coords.Map(sh.anchor.x, sh.anchor.y, sh.anchorHeight));
        }

        /// <summary>A tree waiting for its present, if the story is.</summary>
        Role GiftWaiting()
        {
            foreach (var w in script.Waits)
                if (w.kind == "gift" && roles.TryGetValue(w.role, out var r) && r.sky?.treeGo != null && !r.sky.given) return r;
            return null;
        }

        /// <summary>The tree you're near enough to put the present under, that the story's waiting for.</summary>
        Role GiftTarget()
        {
            var r = GiftWaiting();
            var you = view.PlayerPosition;
            if (r == null || you == null || view.InCar || view.InDialogue) return null;
            return Geo.Dist(you.Value, r.sky.tree) < GIFT_REACH ? r : null;
        }

        /// <summary>The present under the tree you're by (Enter, or the pad's A): true if it went.</summary>
        bool TryGift()
        {
            var r = GiftTarget();
            if (r == null) return false;
            var s = r.sky;
            var m = library.Get(s.gift);
            double k = GIFT_SIZE / Math.Max(1e-3, Math.Max(m.size.x, m.size.z));
            // At the foot of the tree, on your side of it.
            double d = Geo.Dist(s.tree, s.treeStand);
            var toYou = d < 1e-6 ? new Vec2(0, 1) : new Vec2((s.treeStand.x - s.tree.x) / d, (s.treeStand.y - s.tree.y) / d);
            var at = new Vec2(s.tree.x + toYou.x * 0.2, s.tree.y + toYou.y * 0.2);
            s.giftGo = Props.Place(m, view.world.root.transform, at.x, at.y, rng.Next() * 0.6 - 0.3, k, k, k);
            s.given = true;
            Gifts++;
            return true;
        }

        /// <summary>The light over the tree waiting for its present (the beacon, when no one's waiting to talk).</summary>
        void UpdateTreeBeacon()
        {
            if (beacon != null && beacon.activeSelf) return;
            var r = GiftWaiting();
            if (r == null) return;
            EnsureBeacon();
            beacon.SetActive(true);
            double bob = Math.Sin(Time.realtimeSinceStartupAsDouble * 3) * 0.025;
            beacon.transform.localPosition = Coords.Map(r.sky.tree.x, r.sky.tree.y, TREE_HEIGHT + 0.12 + bob);
        }

        /// <summary>Where you'd sit in your own vehicle (the sleigh's seat is at its back; a car's is its middle).</summary>
        Vec2 YourSeat()
        {
            var car = view.OwnVehicle;
            if (car == null) return view.PlayerPosition ?? default;
            double back = Catalog.CARS[car.model] == Catalog.SLEIGH ? Catalog.SLEIGH_SEAT : 0;
            return new Vec2(car.x - Math.Cos(car.heading) * back, car.y - Math.Sin(car.heading) * back);
        }

        // Drawing.

        GameObject DrawRing(SkyRing ring, out Renderer glow)
        {
            var go = new GameObject("mission ring");
            go.transform.SetParent(view.world.root.transform, false);
            go.transform.localPosition = Coords.Map(ring.x, ring.y, ring.h);
            // Its face across the way through it (map (ax, ay) is the scene's (ax, 0, -ay)).
            go.transform.localRotation = Quaternion.LookRotation(new Vector3((float)ring.ax, 0, (float)-ring.ay), Vector3.up);
            go.transform.localScale = Vector3.one * (float)ring.radius;
            ringMesh ??= Torus(1, (float)(RING_TUBE / 2), 40, 8);
            go.AddComponent<MeshFilter>().sharedMesh = ringMesh;
            var mr = go.AddComponent<MeshRenderer>();
            ringMaterial ??= Materials.Glowing(RingGold);
            mr.sharedMaterial = ringMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            glow = mr;
            return go;
        }

        /// <summary>A ring round +z, `radius` across its middle, its tube `tube` thick (as a share of the radius, scaled with it).</summary>
        static Mesh Torus(float radius, float tube, int around, int across)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i <= around; i++)
            {
                float a = i * Mathf.PI * 2 / around;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                for (int j = 0; j <= across; j++)
                {
                    float b = j * Mathf.PI * 2 / across;
                    var n = dir * Mathf.Cos(b) + Vector3.forward * Mathf.Sin(b);
                    verts.Add(dir * radius + n * tube);
                    normals.Add(n);
                }
            }
            int row = across + 1;
            for (int i = 0; i < around; i++)
                for (int j = 0; j < across; j++)
                {
                    int a = i * row + j, b = (i + 1) * row + j;
                    tris.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
                }
            var m = new Mesh { name = "mission ring" };
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>A brick chimney through the roof (the roof `roof` up where it stands, its top `rim` up), snow on its top.</summary>
        GameObject DrawChimney(Vec2 at, double roof, double rim)
        {
            var go = new GameObject("mission chimney");
            go.transform.SetParent(view.world.root.transform, false);
            go.transform.localPosition = Coords.Map(at.x, at.y, 0);
            brickMaterial ??= Materials.Glowing(Brick);
            snowMaterial ??= Materials.Glowing(Snow);
            // Down into the roof's slope far enough to meet it, up past the ridge.
            double bottom = roof - CHIMNEY_ROOTS;
            void Block(string name, double y0, double y1, double wide, Material mat)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(b.GetComponent<Collider>());
                b.name = name;
                b.transform.SetParent(go.transform, false);
                b.transform.localPosition = new Vector3(0, (float)((y0 + y1) / 2), 0);
                b.transform.localScale = new Vector3((float)wide, (float)(y1 - y0), (float)wide);
                b.GetComponent<Renderer>().sharedMaterial = mat;
            }
            Block("stack", bottom, rim, CHIMNEY_WIDE, brickMaterial);
            Block("rim", rim - 0.04, rim, CHIMNEY_WIDE + 0.04, brickMaterial);
            Block("snow", rim, rim + 0.03, CHIMNEY_WIDE + 0.06, snowMaterial);
            return go;
        }
    }
}

// A mission's flying (MissionPlay's building blocks for it): a plane of your
// own to fly (your_plane: Santa's sleigh, say), seen from one place only,
// straight behind you and a little above; rings hung in the sky to fly through
// or just near (ring, @ring), an arrow at the edge of the screen pointing the
// way to the next; the story taking the controls from you to bring the plane
// down as a pilot would, round onto a straight run-in, gliding down it,
// flaring and sliding to a stop on a role's roof or in the street outside
// (land, @landed); out of your seat on the roof (A) in one leap onto the
// chimney's top and down it, the camera swinging round to watch from the
// street, and back up it to your seat (chimney, @chimney); a leap out of your
// seat onto the pavement (put_you "kerb" from the plane, A); and a Christmas
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
        /// <summary>A plane coming down keeps this far over the roofs it passes (tiles), and this far out from their walls.</summary>
        const double LANDING_CLEAR = 0.3, LANDING_MARGIN = 0.15;
        /// <summary>A plane set down in the street is this high (tiles): its runners on the road, but still up, so it lifts off on its own way when you fly on.</summary>
        const double KERB_HOVER = 0.002;
        /// <summary>How far a plane set down in the street slides on its runners before it stops (tiles).</summary>
        const double KERB_SLIDE = 0.9;
        /// <summary>How finely a roof is read (tiles between samples).</summary>
        const double ROOF_STEP = 0.08;
        /// <summary>A leap out of your seat onto the pavement lands this far out to the side of the sleigh (tiles).</summary>
        const double HOP_OUT = 0.42;
        /// <summary>
        /// The camera flying the sleigh (or any plane the story gives you): straight behind you, this far back and up
        /// from the point it looks at (tiles), that ahead of you among the team; how quickly it swings round after
        /// you as you turn (a rate).
        /// </summary>
        const double CAM_BACK = 3.9, CAM_UP = 1.45, CAM_TURN = 5;
        /// <summary>
        /// The camera going down a chimney with you: where it ends up, out over the street (how far round from
        /// straight out the front, how far up and back: radians, tiles); how long it watches the chimney once you're
        /// in it before you're inside by the tree (seconds). And hopping out onto the pavement: where it ends up,
        /// behind you as you face the house.
        /// </summary>
        const double SHOT_SIDE = 0.55, SHOT_ELEVATION = 0.38, SHOT_DISTANCE = 2.9, SHOT_HOLD = 0.9;
        const double HOP_SHOT_SIDE = 0.5, HOP_SHOT_ELEVATION = 0.42, HOP_SHOT_DISTANCE = 2.6;
        /// <summary>A ring reached grows and fades over this long (seconds); a puff of soot and snow from a chimney lasts this long.</summary>
        const double RING_POP = 0.45, PUFF_TIME = 0.9;
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
        WalkerPose? pose;
        /// <summary>The flying camera's way round (a map heading), once it's following you.</summary>
        double? camYaw;
        double camLast = -1;
        /// <summary>Rings reached, growing and fading away; puffs from a chimney (each with how long it's been going).</summary>
        readonly List<(GameObject go, double age)> popping = new List<(GameObject, double)>();
        readonly List<(GameObject go, double age)> puffs = new List<(GameObject, double)>();
        static Material puffMaterial;
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
        /// A camera move (map terms): round a point, from one way, height and distance to another, and from looking
        /// at one point to another, over `turn` seconds, eased in and out.
        /// </summary>
        sealed class Shot
        {
            public double yaw0, elevation0, distance0, yaw1, elevation1, distance1, turn, time;
            /// <summary>What it looks at, from and to (map x, y and height).</summary>
            public Vector3 anchor0, anchor1;

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
            view.PointerTarget = NextRing;
        }

        void DisposeSky()
        {
            foreach (var r in roles.Values) ClearSky(r);
            foreach (var go in leftBehind) if (go != null) UnityEngine.Object.Destroy(go);
            leftBehind.Clear();
            foreach (var (go, _) in popping) if (go != null) UnityEngine.Object.Destroy(go);
            popping.Clear();
            foreach (var (go, _) in puffs) if (go != null) UnityEngine.Object.Destroy(go);
            puffs.Clear();
            if (view.PointerTarget == (Func<Vector3?>)NextRing) view.PointerTarget = null;
            StopMoving();
            var rig = view.OwnCarView?.Sleigh;
            if (rig != null) rig.Ground = null;
            if (view.FlyOverride == (Func<Vehicle, double, bool>)FlyOverride) view.FlyOverride = null;
            if (view.Scripted == (Func<WalkerPose?>)Scripted) view.Scripted = null;
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
            // The sleigh is where Santa sits; its model's middle is out ahead of him (RoofStop works from the middle).
            double ahead = sleigh ? Catalog.SLEIGH_AHEAD : 0;
            double tail = sleigh ? Catalog.SLEIGH_TAIL : car.halfLength;
            r.sky ??= new SkyStop();
            var s = r.sky;
            Vec2 to;
            double heading, height, slide;
            Box? own = null;
            if (at == "roof")
            {
                // Read off the building itself: along its ridge, the runners resting on the roof under them.
                var map = RoofMapOf(r);
                var stop = RoofStop.Place(map, car.x, car.y, r.outward, ahead, sleigh ? Catalog.SLEIGH_SEAT_HEIGHT : 0.2,
                    sleigh ? Catalog.SLEIGH_RUNNER_FROM : 0, sleigh ? Catalog.SLEIGH_RUNNER_TO : car.halfLength,
                    sleigh ? Catalog.SLEIGH_RUNNER_GAUGE : car.halfWidth * 0.5, sleigh ? Catalog.SLEIGH_RUNNER_LOW : 0, CarView.LIFT);
                s.roofMap = map;
                s.stop = stop;
                heading = stop.heading;
                to = new Vec2(stop.middle.x - Math.Cos(heading) * ahead, stop.middle.y - Math.Sin(heading) * ahead);
                height = stop.altitude;
                // Touching down on the roof itself and sliding along its ridge to a stop: as far back as the roof goes under the back of it.
                slide = RoofSlide(map, to, heading, tail, stop.altitude + CarView.LIFT + (sleigh ? Catalog.SLEIGH_RUNNER_LOW : 0));
                own = RoofOf(r);
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
                to = seat;
                slide = KERB_SLIDE;
                s.roofMap = null;
                s.stop = null;
            }
            s.landAt = at;
            s.landed = false;
            s.at = to;
            s.atHeight = height;
            landing = new Landing(car.x, car.y, car.altitude, car.heading, car.speed, to.x, to.y, height, heading, slide, LandingFloor(car, own));
            landingFor = r;
            parked = false;
        }

        /// <summary>
        /// How far a plane set down on a roof at `to` facing `heading` can slide along it as it stops: as far as the
        /// roof's still under the back of it (`tail` behind it), at the height its runners touch down at (tiles).
        /// </summary>
        static double RoofSlide(RoofMap map, Vec2 to, double heading, double tail, double runners)
        {
            double fx = Math.Cos(heading), fy = Math.Sin(heading);
            for (double slide = Landing.SLIDE; slide > 0.04; slide -= 0.05)
            {
                bool ok = true;
                for (double u = 0; u <= slide + 1e-9 && ok; u += 0.05)
                {
                    double h = map.At(to.x - fx * (tail + u), to.y - fy * (tail + u));
                    ok = !double.IsNaN(h) && Math.Abs(h - runners) < 0.08;
                }
                if (ok) return slide;
            }
            return 0;
        }

        /// <summary>
        /// How low a plane coming down may be at a point, facing a way: clear over the buildings any of it (from its
        /// tail to its nose) is over, by LANDING_CLEAR; `own` (the building it's setting down on) aside.
        /// </summary>
        Func<double, double, double, double> LandingFloor(Vehicle car, Box? own)
        {
            var boxes = view.Boxes;
            bool sleigh = Catalog.CARS[car.model] == Catalog.SLEIGH;
            double back = sleigh ? Catalog.SLEIGH_TAIL : car.halfLength, ahead = sleigh ? Catalog.SLEIGH_NOSE : car.halfLength;
            return (x, y, heading) =>
            {
                double fx = Math.Cos(heading), fy = Math.Sin(heading), most = double.NegativeInfinity;
                for (int k = 0; k <= 4; k++)
                {
                    double u = -back + (back + ahead) * k / 4;
                    double px = x + fx * u, py = y + fy * u;
                    foreach (var b in boxes)
                    {
                        if (own != null && SameBox(b, own.Value)) continue;
                        if (px < b.x0 - LANDING_MARGIN || px > b.x1 + LANDING_MARGIN || py < b.y0 - LANDING_MARGIN || py > b.y1 + LANDING_MARGIN) continue;
                        most = Math.Max(most, b.top + LANDING_CLEAR - CarView.LIFT);
                    }
                }
                return most;
            };
        }

        static bool SameBox(Box a, Box b) => a.x0 == b.x0 && a.y0 == b.y0 && a.x1 == b.x1 && a.y1 == b.y1 && a.top == b.top;

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
            // On the chimney's top, turned to the street.
            double perched = DetMath.Atan2(r.outward.x, r.outward.y);
            trip = new ChimneyTrip(down, st.seat, st.seatHeight, st.chimney, st.rim, perched);
            tripFor = r;
            pose = trip.At(0);
            inChimney = null;
            // The camera swings round to watch from out over the street (coming back up, from there round behind the sleigh).
            var across = new Vec2(-r.outward.y, r.outward.x);
            double street = Math.Atan2(r.outward.y, r.outward.x) + SHOT_SIDE * (Geo.Dot(new Vec2(st.chimney.x - st.seat.x, st.chimney.y - st.seat.y), across) >= 0 ? -1 : 1);
            var top = new Vector3((float)st.chimney.x, (float)st.chimney.y, (float)(st.rim + 0.12));
            if (down)
            {
                shot = From(view.CameraAt, FlyingLook(view.OwnVehicle));
                shot.yaw1 = street;
                shot.elevation1 = SHOT_ELEVATION;
                shot.distance1 = SHOT_DISTANCE;
                shot.anchor1 = top;
                // Round by the time you're stood on top of it.
                shot.turn = trip.OnTop + ChimneyTrip.PERCH * 0.5;
            }
            else
            {
                var car = view.OwnVehicle;
                shot = new Shot
                {
                    yaw0 = street,
                    elevation0 = SHOT_ELEVATION,
                    distance0 = SHOT_DISTANCE,
                    anchor0 = top,
                    // Ending just where the flying camera is, behind the sleigh.
                    yaw1 = (car?.heading ?? st.heading) + Math.PI,
                    elevation1 = Math.Atan2(CAM_UP, CAM_BACK),
                    distance1 = Numbers.Hypot(CAM_UP, CAM_BACK),
                    anchor1 = FlyingLook(car) ?? top,
                    turn = trip.Duration,
                };
            }
        }

        /// <summary>A camera move starting just where the camera is (a scene position), looking at `anchor` (map terms; else straight on down).</summary>
        static Shot From(Vector3 cam, Vector3? anchor)
        {
            var a = anchor ?? new Vector3(cam.x, -cam.z, cam.y - 1);
            double ox = cam.x - a.x, oy = -cam.z - a.y, oh = cam.y - a.z;
            double flat = Math.Max(1e-3, Numbers.Hypot(ox, oy));
            return new Shot
            {
                yaw0 = Math.Atan2(oy, ox),
                elevation0 = Math.Atan2(oh, flat),
                distance0 = Math.Max(0.8, Numbers.Hypot(flat, oh)),
                anchor0 = a,
                anchor1 = a,
            };
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
            // Out over the side nearer where you're going (the pavement), and on from there.
            var f = car.Forward;
            var side = new Vec2(-f.y, f.x);
            if (Geo.Dot(side, new Vec2(hopTo.x - seat.x, hopTo.y - seat.y)) < 0) side = new Vec2(f.y, -f.x);
            var landAt = new Vec2(seat.x + side.x * HOP_OUT, seat.y + side.y * HOP_OUT);
            if (!view.CanStandAt(landAt.x, landAt.y) || Geo.Dist(seat, hopTo) < HOP_OUT) landAt = hopTo;
            var look = FlyingLook(car);
            view.LeaveSeat();
            hop = new Hop(seat, from, landAt, hopTo, hopFacing, Street);
            pose = hop.At(0);
            // The camera comes round behind you as you go, to look at the house over your shoulder.
            double behind = Math.Atan2(-Math.Cos(hopFacing), -Math.Sin(hopFacing)) + HOP_SHOT_SIDE * (Geo.Dot(side, new Vec2(Math.Sin(hopFacing), Math.Cos(hopFacing))) >= 0 ? 1 : -1);
            shot = From(view.CameraAt, look);
            shot.yaw1 = behind;
            shot.elevation1 = HOP_SHOT_ELEVATION;
            shot.distance1 = HOP_SHOT_DISTANCE;
            shot.anchor1 = new Vector3((float)hopTo.x, (float)hopTo.y, 0.25f);
            shot.turn = hop.Duration;
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
            double was = car.altitude;
            car.x = x;
            car.y = y;
            car.altitude = h;
            car.speed = speed;
            double turn = Orbit.ShortestTurn(car.heading, heading);
            car.heading = heading;
            // Banked into its turns and nosed down a little as it descends, then level as it touches down (and sliding).
            // The sleigh hardly noses down at all: its team's out ahead, and would be in the roof.
            double k = Math.Min(1, dt * 4);
            bool sleigh = Catalog.CARS[car.model] == Catalog.SLEIGH;
            double bankTo = landing.Down ? 0 : Math.Max(-0.45, Math.Min(0.45, turn / Math.Max(1e-3, dt) * 0.3));
            double pitchTo = landing.Down ? 0 : DetMath.Atan2((h - was) / Math.Max(1e-3, dt), Math.Max(1, speed)) * (sleigh ? 0.25 : 1);
            car.bank += (bankTo - car.bank) * k;
            car.pitch += (pitchTo - car.pitch) * k;
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

        WalkerPose? Scripted() => pose;

        /// <summary>The ring the arrow at the edge of the screen points to: the nearest still to fly through (map x, y and height).</summary>
        Vector3? NextRing()
        {
            if (landing != null || !view.DrivingMyCar) return null;
            var you = view.OwnVehicle;
            Vector3? best = null;
            double bestD = double.PositiveInfinity;
            foreach (var r in roles.Values)
            {
                var ring = r.sky?.ring;
                if (ring == null || r.sky.through) continue;
                double d = you != null ? Numbers.Hypot(ring.Value.x - you.x, ring.Value.y - you.y) : 0;
                if (d >= bestD) continue;
                bestD = d;
                best = new Vector3((float)ring.Value.x, (float)ring.Value.y, (float)ring.Value.h);
            }
            return best;
        }

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
                        // Through it, or near enough: from Santa at the back of the sleigh to the reindeer out in front.
                        var f = car.Forward;
                        double reach = Catalog.CARS[car.model] == Catalog.SLEIGH ? Catalog.SLEIGH_AHEAD : 0;
                        var ring = s.ring.Value;
                        bool near = ring.Reached(lastX.Value, lastY.Value, lastH.Value, car.x, car.y, car.altitude)
                            || ring.Reached(lastX.Value + f.x * reach, lastY.Value + f.y * reach, lastH.Value, car.x + f.x * reach, car.y + f.y * reach, car.altitude);
                        if (!near) continue;
                        s.through = true;
                        RingsFlown++;
                        // It grows and fades as you pass.
                        if (s.ringGo != null) popping.Add((s.ringGo, 0));
                        s.ringGo = null;
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
            if (shot != null) shot.time += dt;
            // Out onto the pavement.
            if (hop != null)
            {
                pose = hop.Step(dt);
                if (hop.Done)
                {
                    hop = null;
                    pose = null;
                    shot = null;
                    view.PutPlayer(hopTo, hopFacing);
                }
            }
            // Down or up a chimney.
            if (trip != null)
            {
                bool wasShown = pose?.shown ?? true;
                if (!trip.Done) pose = trip.Step(dt);
                // A puff of soot and snow out of the top as you drop in (or pop out).
                if (pose != null && pose.Value.shown != wasShown && tripFor?.sky?.stop != null) Puff(tripFor.sky.stop.chimney, tripFor.sky.stop.rim);
                // Down it, the camera watches the chimney a moment before you're inside.
                if (trip.Done && trip.down && (inChimney ??= SHOT_HOLD) > 0) inChimney -= dt;
                else if (trip.Done)
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
            // Rings reached grow and fade; puffs rise and spread.
            for (int i = popping.Count - 1; i >= 0; i--)
            {
                var (go, age) = popping[i];
                age += dt;
                if (go == null || age >= RING_POP)
                {
                    if (go != null) UnityEngine.Object.Destroy(go);
                    popping.RemoveAt(i);
                    continue;
                }
                popping[i] = (go, age);
                double k = age / RING_POP;
                go.transform.localScale *= (float)(1 + dt * 1.6);
                var glowing = go.GetComponent<Renderer>();
                if (glowing != null) Materials.SetGlow(glowing, RingGold, (float)(4 * (1 - k)));
                if (k > 0.6) go.transform.localScale *= (float)Math.Max(0, 1 - dt * 7);
            }
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                var (go, age) = puffs[i];
                age += dt;
                if (go == null || age >= PUFF_TIME)
                {
                    if (go != null) UnityEngine.Object.Destroy(go);
                    puffs.RemoveAt(i);
                    continue;
                }
                puffs[i] = (go, age);
                double k = age / PUFF_TIME;
                foreach (Transform t in go.transform)
                {
                    var dir = new Vector3(t.localPosition.x, 0, t.localPosition.z).normalized;
                    t.localPosition += (dir * 0.3f + Vector3.up * 0.3f) * (float)(dt * (1 - k));
                    t.localScale = Vector3.one * (float)(0.06 + 0.11 * Math.Sin(Math.PI * k));
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

        /// <summary>The camera's move round the house as you go down (or come up) its chimney, or hop out, while it's on.</summary>
        (Vector3 at, Vector3 look)? SkyCamera()
        {
            var sh = shot;
            if (sh == null) return null;
            double k = sh.K;
            double yaw = sh.yaw0 + Orbit.ShortestTurn(sh.yaw0, sh.yaw1) * k;
            double elevation = sh.elevation0 + (sh.elevation1 - sh.elevation0) * k;
            double distance = sh.distance0 + (sh.distance1 - sh.distance0) * k;
            var a = Vector3.Lerp(sh.anchor0, sh.anchor1, (float)k);
            double flat = Math.Cos(elevation) * distance;
            var at = Coords.Map(a.x + Math.Cos(yaw) * flat, a.y + Math.Sin(yaw) * flat, a.z + Math.Sin(elevation) * distance);
            return (at, Coords.Map(a.x, a.y, a.z));
        }

        /// <summary>What the flying camera looks at: just ahead of you among the team, a little over the sleigh (map x, y and height).</summary>
        static Vector3? FlyingLook(Vehicle car)
        {
            if (car == null) return null;
            bool sleigh = Catalog.CARS[car.model] == Catalog.SLEIGH;
            double ahead = sleigh ? ExploreView.SLEIGH_FOCUS : 0;
            return new Vector3((float)(car.x + Math.Cos(car.heading) * ahead), (float)(car.y + Math.Sin(car.heading) * ahead), (float)(car.altitude + CarView.LIFT + 0.22));
        }

        /// <summary>
        /// Flying the story's plane (and set down in it), one place for the camera only: straight behind you, a little
        /// above, looking at the sleigh so it's in the middle of the view; swinging round after you as you turn.
        /// </summary>
        (Vector3 at, Vector3 look)? FlyingCamera()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double dt = camLast < 0 ? 0 : Math.Max(0, Math.Min(0.1, now - camLast));
            camLast = now;
            var car = view.OwnVehicle;
            if (storyPlane < 0 || car == null || car.model != storyPlane || !view.DrivingMyCar)
            {
                camYaw = null;
                return null;
            }
            camYaw = camYaw == null ? car.heading : camYaw.Value + Orbit.ShortestTurn(camYaw.Value, car.heading) * Math.Min(1, dt * CAM_TURN);
            var look = FlyingLook(car).Value;
            double yaw = camYaw.Value;
            var at = Coords.Map(look.x - Math.Cos(yaw) * CAM_BACK, look.y - Math.Sin(yaw) * CAM_BACK, look.z + CAM_UP);
            return (at, Coords.Map(look.x, look.y, look.z));
        }

        /// <summary>A puff of soot and snow out of a chimney's top.</summary>
        void Puff(Vec2 at, double rim)
        {
            puffMaterial ??= Materials.Glowing(Coords.Hex(0xdfe3ea));
            var go = new GameObject("chimney puff");
            go.transform.SetParent(view.world.root.transform, false);
            go.transform.localPosition = Coords.Map(at.x, at.y, rim + 0.05);
            for (int i = 0; i < 7; i++)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(b.GetComponent<Collider>());
                b.transform.SetParent(go.transform, false);
                double a = i * Math.PI * 2 / 7 + rng.Next() * 0.5;
                b.transform.localPosition = new Vector3((float)(Math.Cos(a) * 0.05), (float)(rng.Next() * 0.05), (float)(Math.Sin(a) * 0.05));
                b.transform.localScale = Vector3.one * 0.06f;
                var mr = b.GetComponent<Renderer>();
                mr.sharedMaterial = puffMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            puffs.Add((go, 0));
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

        /// <summary>Where you'd sit in your own vehicle: where it is (the sleigh's Santa's seat, its team out ahead).</summary>
        Vec2 YourSeat()
        {
            var car = view.OwnVehicle;
            if (car == null) return view.PlayerPosition ?? default;
            return new Vec2(car.x, car.y);
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

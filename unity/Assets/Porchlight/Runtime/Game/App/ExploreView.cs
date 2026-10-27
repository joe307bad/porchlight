// The explorable 3D town: an orthographic
// camera at an isometric-style pitch orbiting the player between 12 fixed
// headings (Q / E), tilted through fixed stops (Z / X), turned to look down the
// street (F), and a close-up chase camera (V). WASD or the arrows walk (or
// auto-walk wanders the sidewalks), G / B gets in and out of cars, doors swing
// open into furnished ground floors, townsfolk stroll and traffic drives, and
// day and night blend with the street lamps (a few faulty) lighting up. Other
// players come and go through the network (NetHost).

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;
using Porchlight.Net;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using Grid = Porchlight.Core.Grid;
using Rect = Porchlight.Explore.Rect;
using Avatar = Porchlight.Rendering.Avatar;

namespace Porchlight.App
{
    public enum CameraMode { Iso, Close }

    public enum PadAction { Car, Auto, CloseUp, Night, NewCharacter, Menu }

    /// <summary>The local player as the network sees them. Ground is (x, z); y is height.</summary>
    public struct LocalState
    {
        public double x, y, z, yaw, speed;
        public int car;
        public string ride;
        /// <summary>When this position was worked out (ms on this client's clock).</summary>
        public double at;
    }

    /// <summary>What the view shows besides the 3D scene (the minimap, street panel and notices: ExploreOverlay).</summary>
    public interface IExploreOverlay
    {
        void SetTown(TownModel model);
        void DrawMinimap(Vec2? player, Vec2[] view, Vec2? goal, List<Vec2> route);
        void SetOthers(List<Vec2> others);
        void SetHud(string street, List<Step> steps);
        void SetPrompt(string text);
        void Notify(string text);
        Action<Vec2> OnMinimapPick { set; }
    }

    public sealed class ExploreView : INetHost
    {
        const double VIEW_HEIGHT = 20;
        const double MIN_ZOOM = 0.35;
        const double MAX_ZOOM = 2.5;
        const int MAX_PEDESTRIANS = 70;
        /// <summary>How far back the orthographic camera sits (the view doesn't change with it; URP's shadows reach this far and more).</summary>
        const double CAMERA_DISTANCE = 60;
        const double SIGHT_HEIGHT = 0.26;
        const double HEADING_SMOOTHING = 1.2;
        const double MANUAL_HOLD = 5;
        const double CHASE_DISTANCE = 2.4;
        const double CHASE_MIN = 1.1;
        const double CHASE_MAX = 8;
        const double CHASE_FOV = 50;
        const double CHASE_LOOK_HEIGHT = 0.42;
        const double CHASE_LOOK_AHEAD = 0.6;
        const double TURN_RATE = 2.6;
        const double FOG_NEAR = 22;
        const double FOG_FAR = 60;
        const double DOOR_REACH = 0.85;
        const double DOOR_SPEED = 3;
        static readonly double ROOM_PITCH = (55 * Math.PI) / 180;
        const double ROOM_MARGIN = 1.35;
        const double CHASE_TILT_GUESS = 0.3;
        const double AUTO_TURN_MIN_ZOOM = 0.7;
        const double PLAYER_SCALE = 1.05;
        const double CAR_REACH = 1.0;
        const double DRIVING_CHASE = 1.7;
        const double PAD_SWING_RATE = 2.4;
        const double PAD_ZOOM_RATE = 1.4;

        static readonly Dictionary<Key, (int x, int y)> KEYS = new Dictionary<Key, (int, int)>
        {
            [Key.W] = (0, -1), [Key.UpArrow] = (0, -1),
            [Key.S] = (0, 1), [Key.DownArrow] = (0, 1),
            [Key.A] = (-1, 0), [Key.LeftArrow] = (-1, 0),
            [Key.D] = (1, 0), [Key.RightArrow] = (1, 0),
        };

        sealed class OwnCar
        {
            public Vehicle car;
            public CarView view;
            public bool seeThrough;
        }

        sealed class Keeper
        {
            public Walker walker;
            public Avatar avatar;
        }

        readonly Camera camera;
        readonly ModelLibrary library;
        readonly Sky sky;
        readonly IExploreOverlay overlay;
        readonly GameInput input;

        public TownWorld world;
        RemotePlayers remotes;
        string riding;
        public Func<double, double, bool> TeleportHook;
        Player player;
        Avatar playerAvatar;
        readonly List<Pedestrian> people = new List<Pedestrian>();
        readonly List<Avatar> avatars = new List<Avatar>();
        Traffic traffic;
        Fleet fleet;
        CameraMode mode = CameraMode.Iso;
        double chaseYawNow;
        double chaseSwing;
        double chaseDistance = CHASE_DISTANCE;
        double shownChaseDistance = CHASE_DISTANCE;
        Vector3 chasePos;
        Vector3 chaseLook;
        int? room;
        readonly Dictionary<int, Keeper> keepers = new Dictionary<int, Keeper>();
        int roomTurn;
        double inputYaw = Orbit.StopYaw(Orbit.DEFAULT_STOP);
        bool screenKeys;
        int isoTilt = Orbit.DEFAULT_ISO_TILT;
        int chaseTilt = Orbit.DEFAULT_CHASE_TILT;
        double pitch = Orbit.TiltPitch(Orbit.ISO_TILTS, Orbit.DEFAULT_ISO_TILT);
        double yaw = Orbit.StopYaw(Orbit.DEFAULT_STOP);
        double targetYaw = Orbit.StopYaw(Orbit.DEFAULT_STOP);
        double zoom = 1;
        double shownZoom = 1;
        Vector3 focus;
        readonly Follow focusFollow = new Follow(8);
        readonly Follow lookFollow = new Follow(10);
        double simAt;
        bool night;
        byte[] open = new byte[0];
        bool streetsOnly;
        bool hidden;
        double sightTimer;
        Vec2? walkDir;
        double manualHold;
        GameObject route;
        double routeTimer;
        bool background;
        double dusk;
        double time;
        double[] lampLevels = new double[0];
        bool snap = true;
        readonly List<Key> held = new List<Key>();
        PadState padState = PadState.Idle;
        readonly Flick lookFlickX = new Flick();
        readonly Flick lookFlickY = new Flick();
        readonly Flick moveFlick = new Flick();
        bool shift;
        bool space;
        bool steering;
        public Action<PadAction> OnPadAction;
        readonly List<OwnCar> cars = new List<OwnCar>();
        int? driving;
        AutoDrive autoDrive;
        public Action OnAutoChange;
        public Action<bool> OnCarChange;
        readonly HashSet<int> takenParked = new HashSet<int>();
        Func<double, double, bool> canDrive = (x, y) => false;
        double hudTimer;
        int? street;
        List<Step> steps;
        readonly Rng autoRng = new Rng("auto-walk");
        double last;
        double lastFrame;
        /// <summary>The part of the screen the view draws in (pixels, origin bottom left).</summary>
        public UnityEngine.Rect viewport;

        public ExploreView(Camera camera, ModelLibrary library, IExploreOverlay overlay, GameInput input)
        {
            this.camera = camera;
            this.library = library;
            this.overlay = overlay;
            this.input = input;
            sky = new Sky(camera);
            overlay.OnMinimapPick = Teleport;
            input.KeyDown += OnKeyDown;
            input.KeyUp += OnKeyUp;
            input.Blur += () =>
            {
                held.Clear();
                shift = false;
                space = false;
            };
            last = lastFrame = Now;
        }

        static double Now => Time.realtimeSinceStartupAsDouble * 1000;

        /// <summary>Build the scene for a town (replacing any previous one) and fill the sidewalks with people.</summary>
        public void SetTown(TownModel model)
        {
            remotes?.Clear();
            world?.Dispose();
            fleet?.Dispose();
            foreach (var a in avatars) a.Dispose();
            playerAvatar?.Dispose();
            foreach (var k in keepers.Values) k.avatar.Dispose();
            keepers.Clear();
            room = null;
            foreach (var c in cars) RemoveCar(c);
            cars.Clear();
            driving = null;
            riding = null;
            autoDrive = null;
            takenParked.Clear();
            ClearRoute();
            avatars.Clear();
            people.Clear();
            player = null;
            playerAvatar = null;
            world = new TownWorld(model, library);
            remotes = new RemotePlayers(world.root.transform, library);
            snap = true;
            open = WalkMask();
            canDrive = Vehicle.Drivable(model, world.layout);
            overlay.SetTown(model);
            LeaveCarAtSpawn();

            traffic = new Traffic(world.layout, new Rng($"{model.seed}::traffic"), Catalog.PickCarModel);
            fleet = new Fleet(traffic, library, world.root.transform);
            var rng = new Rng($"{model.seed}::townsfolk");
            var paths = world.sidewalks.paths;
            double walkable = paths.Sum(p => p.Length);
            int count = paths.Count > 0 ? Math.Min(MAX_PEDESTRIANS, (int)Math.Floor(walkable / 12)) : 0;
            for (int k = 0; k < count; k++)
            {
                var p = Pedestrian.Create(Catalog.RandomLook(rng), world.sidewalks, rng);
                var a = new Avatar(library.Character(p.look), Avatar.PERSON_HEIGHT, rng.Next(), world.root.transform);
                people.Add(p);
                avatars.Add(a);
            }
        }

        /// <summary>Put a new character with the given look at the town's spawn point (or swap the look in place).</summary>
        public void Spawn(string look)
        {
            if (world == null) return;
            GetOut();
            var keep = player;
            var at = keep != null ? new Vec2(keep.x, keep.y) : world.model.spawn;
            bool wasAuto = keep?.IsAuto ?? false;
            playerAvatar?.Dispose();
            player = new Player(look, at.x, at.y, PLAYER_SCALE);
            if (keep != null) player.heading = keep.heading;
            playerAvatar = new Avatar(library.Character(look), Avatar.PERSON_HEIGHT, 0, world.root.transform);
            playerAvatar.SeeThrough();
            playerAvatar.SnapYaw(player.heading);
            if (wasAuto) player.StartAuto(world.sidewalks, autoRng);
            if (keep == null) snap = true;
        }

        void Teleport(Vec2 at)
        {
            if (player == null || world == null) return;
            var to = StandableNear(at);
            if (TeleportHook != null && TeleportHook(to.x, to.y)) return;
            MoveTo(to.x, to.y);
        }

        Vec2 StandableNear(Vec2 at)
        {
            var model = world.model;
            int w = model.width, h = model.height;
            double x = Math.Min(w - 0.2, Math.Max(0.2, at.x));
            double y = Math.Min(h - 0.2, Math.Max(0.2, at.y));
            var solids = world.solids;
            if (open.Length > 0 && !Player.Standable(x, y, open, model, solids))
            {
                double bestD = double.PositiveInfinity;
                for (int i = 0; i < open.Length; i++)
                {
                    if (open[i] == 0 || !Player.Standable((i % w) + 0.5, i / w + 0.5, open, model, solids)) continue;
                    double d = Numbers.Hypot((i % w) + 0.5 - x, i / w + 0.5 - y);
                    if (d < bestD)
                    {
                        bestD = d;
                        x = (i % w) + 0.5;
                        y = i / w + 0.5;
                    }
                }
            }
            return new Vec2(x, y);
        }

        void MoveTo(double x, double y)
        {
            if (player == null || world == null) return;
            GetOut();
            riding = null;
            player.x = x;
            player.y = y;
            player.Halt();
            if (player.IsAuto) player.StartAuto(world.sidewalks, autoRng);
            snap = true;
        }

        // The network's side (NetHost).

        public LocalState? GetLocalState()
        {
            var p = player;
            if (p == null) return null;
            int car = driving != null ? cars[driving.Value].car.model + 1 : 0;
            return new LocalState { x = p.x, y = p.lift, z = p.y, yaw = p.heading, speed = p.speed, car = car, ride = riding ?? "", at = simAt };
        }

        public void ApplySpawn(double[] at, string look)
        {
            if (player == null || world == null) return;
            if (look != null && look != player.look && Catalog.IsCharacter(look)) Spawn(look);
            if (at != null)
            {
                var to = StandableNear(new Vec2(at[0], at[1]));
                MoveTo(to.x, to.y);
                player.heading = at[2];
            }
            else
            {
                var spawn = world.model.spawn;
                double a = autoRng.Next() * Math.PI * 2;
                double r = 0.2 + autoRng.Next() * 0.5;
                var to = StandableNear(new Vec2(spawn.x + Math.Cos(a) * r, spawn.y + Math.Sin(a) * r));
                MoveTo(to.x, to.y);
            }
        }

        public void PlacePlayer(double x, double z) => MoveTo(x, z);

        public void SyncRemotes(List<RemoteState> players, string me) => remotes?.Sync(players, me);

        public void ClearRemotes() => remotes?.Clear();

        public void SetDots(List<Vec2> dots) => overlay.SetOthers(dots);

        public void Notify(string t) => overlay.Notify(t);

        public List<Vec2> Route => autoDrive?.RoutePoints() ?? player?.AutoRoute() ?? new List<Vec2>();

        public Vec2? Goal => autoDrive != null ? autoDrive.GoalPoint() : player?.AutoGoal();

        public void SetBackground(bool on)
        {
            background = on;
            held.Clear();
            shift = false;
            space = false;
        }

        public string LookModel => player?.look;

        public Vec2? PlayerPosition => player != null ? new Vec2(player.x, player.y) : (Vec2?)null;

        public void SetAuto(bool on)
        {
            if (player == null || world == null || riding != null) return;
            held.Clear();
            if (driving != null)
            {
                autoDrive = on ? new AutoDrive(world.layout.roads, autoRng, cars[driving.Value].car) : null;
                return;
            }
            if (on) player.StartAuto(world.sidewalks, autoRng);
            else player.StopAuto();
        }

        public bool IsAuto => (player?.IsAuto ?? false) || autoDrive != null;
        public bool IsDriving => driving != null;
        public bool InCar => driving != null || riding != null;

        public void Jump()
        {
            if (!InCar) player?.Jump();
        }

        public bool ToggleCar()
        {
            bool was = InCar;
            if (riding != null) LeaveRide();
            else if (driving != null) GetOut();
            else GetIn();
            return InCar != was;
        }

        enum PickKind { Own, Parked, Traffic, Ride }

        struct Pick
        {
            public PickKind kind;
            public int i;
            public RemoteDriver? driver;
        }

        Pick? CarInReach()
        {
            var p = player;
            if (p == null || room != null) return null;
            Pick? best = null;
            double bestD = CAR_REACH;
            void Consider(PickKind kind, int i, double x, double y, RemoteDriver? driver = null)
            {
                double d = Numbers.Hypot(x - p.x, y - p.y);
                if (d < bestD)
                {
                    bestD = d;
                    best = new Pick { kind = kind, i = i, driver = driver };
                }
            }
            for (int i = 0; i < cars.Count; i++) Consider(PickKind.Own, i, cars[i].car.x, cars[i].car.y);
            for (int i = 0; i < world.parked.Count; i++) if (!takenParked.Contains(i)) Consider(PickKind.Parked, i, world.parked[i].x, world.parked[i].y);
            int t = traffic?.Nearest(new Vec2(p.x, p.y), bestD) ?? -1;
            if (t >= 0) Consider(PickKind.Traffic, t, traffic.cars[t].x, traffic.cars[t].y);
            if (remotes != null) foreach (var d in remotes.Drivers()) if (d.riders < Protocol.CAR_SEATS - 1) Consider(PickKind.Ride, -1, d.x, d.y, d);
            return best;
        }

        void GetIn()
        {
            var p = player;
            if (world == null || p == null) return;
            var pick = CarInReach();
            if (pick == null) return;
            var pk = pick.Value;
            if (pk.kind == PickKind.Ride)
            {
                if (p.IsAuto)
                {
                    p.StopAuto();
                    OnAutoChange?.Invoke();
                }
                riding = pk.driver.Value.key;
                held.Clear();
                p.Halt();
                OnCarChange?.Invoke(true);
                return;
            }
            if (pk.kind == PickKind.Own) driving = pk.i;
            else
            {
                Vehicle car;
                if (pk.kind == PickKind.Parked)
                {
                    var c = world.parked[pk.i];
                    takenParked.Add(pk.i);
                    world.SetParkedVisible(pk.i, false);
                    car = new Vehicle(c.model, c.x, c.y, c.heading);
                }
                else
                {
                    var c = traffic.Take(pk.i);
                    car = new Vehicle(c.model, c.x, c.y, c.heading) { speed = Math.Min(c.speed, 1) };
                    EjectDriver(c.x, c.y, c.heading);
                }
                driving = AddCar(car);
            }
            held.Clear();
            SyncDriver();
            if (p.IsAuto)
            {
                p.StopAuto();
                SetAuto(true);
            }
            OnCarChange?.Invoke(true);
        }

        void EjectDriver(double x, double y, double heading)
        {
            if (world.sidewalks.paths.Count == 0) return;
            var rng = autoRng;
            var look = Catalog.RandomLook(rng);
            var door = new Vec2(x + Math.Sin(heading) * 0.45, y - Math.Cos(heading) * 0.45);
            var driver = Pedestrian.Create(look, world.sidewalks, rng, door);
            var avatar = new Avatar(library.Character(look), Avatar.PERSON_HEIGHT, rng.Next(), world.root.transform);
            people.Add(driver);
            avatars.Add(avatar);
        }

        int AddCar(Vehicle car)
        {
            cars.Add(new OwnCar { car = car, view = new CarView(library.Get(Catalog.CARS[car.model]), world.root.transform) });
            return cars.Count - 1;
        }

        void RemoveCar(OwnCar c) => c.view.Dispose();

        void LeaveCarAtSpawn()
        {
            var rng = new Rng($"{world.model.seed}::spawn-car");
            var taken = world.parked.Select(c => new Vec2(c.x, c.y)).ToList();
            var spot = Vehicle.CarSpotNear(world.model.spawn, world.layout.roads, Layout.SIDEWALK, canDrive, taken);
            if (spot != null) AddCar(new Vehicle(rng.Pick(new[] { 0, 1, 2, 4 }), spot.Value.x, spot.Value.y, spot.Value.heading));
        }

        void GetOut()
        {
            var p = player;
            if (driving == null || world == null || p == null) return;
            var car = cars[driving.Value].car;
            var spot = ExitBeside(car);
            car.speed = 0;
            driving = null;
            if (autoDrive != null)
            {
                autoDrive = null;
                OnAutoChange?.Invoke();
            }
            held.Clear();
            p.x = spot.x;
            p.y = spot.y;
            p.Halt();
            var f = car.Forward;
            p.heading = DetMath.Atan2(f.x, f.y);
            p.speed = 0;
            OnCarChange?.Invoke(false);
        }

        Vec2 ExitBeside(Vehicle car) =>
            Vehicle.ExitSpot(car, (x, y) => Player.Standable(x, y, open, world.model, world.solids)) ?? StandableNear(new Vec2(car.x, car.y));

        void LeaveRide()
        {
            var p = player;
            if (riding == null || p == null) return;
            riding = null;
            var car = new Vehicle(0, p.x, p.y, Math.PI / 2 - p.heading);
            var spot = ExitBeside(car);
            p.x = spot.x;
            p.y = spot.y;
            p.Halt();
            p.speed = 0;
            held.Clear();
            OnCarChange?.Invoke(false);
        }

        void FollowRide()
        {
            var p = player;
            if (riding == null || p == null) return;
            RemoteDriver? d = null;
            if (remotes != null) foreach (var r in remotes.Drivers()) if (r.key == riding) d = r;
            if (d == null)
            {
                LeaveRide();
                return;
            }
            p.x = d.Value.x;
            p.y = d.Value.y;
            p.heading = d.Value.heading;
            p.Halt();
            p.speed = 0;
        }

        void SyncDriver()
        {
            if (driving == null || player == null) return;
            var car = cars[driving.Value].car;
            player.x = car.x;
            player.y = car.y;
            player.Halt();
            var f = car.Forward;
            player.heading = DetMath.Atan2(f.x, f.y);
            player.speed = 0;
        }

        public void SetStreetsOnly(bool on)
        {
            streetsOnly = on;
            open = WalkMask();
            if (player != null && world != null)
            {
                int here = Grid.Index((int)Math.Floor(player.x), (int)Math.Floor(player.y), world.model.width);
                if (open[here] == 0 || !Player.Standable(player.x, player.y, open, world.model, world.solids)) Teleport(new Vec2(player.x, player.y));
            }
        }

        byte[] WalkMask()
        {
            if (world == null) return new byte[0];
            if (streetsOnly) return Streets.Mask(world.model.tiles);
            var o = new byte[world.layout.use.Length];
            for (int i = 0; i < o.Length; i++) o[i] = world.layout.use[i] == Use.SHED ? (byte)0 : (byte)1;
            return o;
        }

        public bool IsStreetsOnly => streetsOnly;

        public void Rotate(int step)
        {
            if (room != null)
            {
                roomTurn += step;
                return;
            }
            if (mode == CameraMode.Close)
            {
                chaseSwing += (step * Math.PI) / 6;
                return;
            }
            SetStop(CameraStop + step);
            manualHold = MANUAL_HOLD;
        }

        public void Tilt(int step)
        {
            if (mode == CameraMode.Close) chaseTilt = Orbit.ClampTilt(Orbit.CHASE_TILTS, chaseTilt + step);
            else isoTilt = Orbit.ClampTilt(Orbit.ISO_TILTS, isoTilt + step);
        }

        double TargetPitch => mode == CameraMode.Close ? Orbit.TiltPitch(Orbit.CHASE_TILTS, chaseTilt) : Orbit.TiltPitch(Orbit.ISO_TILTS, isoTilt);

        public void SetCameraMode(CameraMode m)
        {
            if (m == mode) return;
            mode = m;
            held.Clear();
            chaseSwing = 0;
            if (player != null) chaseYawNow = Orbit.ChaseYaw(player.heading);
            shownChaseDistance = chaseDistance;
            snap = true;
        }

        public CameraMode Mode => mode;

        public void LookAlongWalk()
        {
            var p = player;
            if (p == null) return;
            if (room != null)
            {
                roomTurn = 0;
                return;
            }
            if (mode == CameraMode.Close)
            {
                chaseSwing = 0;
                return;
            }
            var dir = p.AutoHeading() ?? new Vec2(Math.Sin(p.heading), Math.Cos(p.heading));
            if (Numbers.Hypot(dir.x, dir.y) < 1e-6) return;
            SetStop(Orbit.StopLookingAlong(dir));
            manualHold = MANUAL_HOLD;
        }

        public int CameraStop => Orbit.NearestStop(targetYaw);

        void SetStop(int k) => targetYaw += Orbit.ShortestTurn(targetYaw, Orbit.StopYaw(k));

        public void SetNight(bool on) => night = on;

        public bool IsNight => night;

        // Input ----------------------------------------------------------------------------------

        void OnKeyDown(Key code, bool repeat)
        {
            if (background) return;
            if (code == Key.LeftShift || code == Key.RightShift) shift = true;
            if (code == Key.Q || code == Key.E)
            {
                if (!repeat) Rotate(code == Key.Q ? -1 : 1);
                return;
            }
            if (code == Key.Z || code == Key.X || code == Key.PageUp || code == Key.PageDown)
            {
                if (!repeat) Tilt(code == Key.X || code == Key.PageUp ? 1 : -1);
                return;
            }
            if (code == Key.F)
            {
                if (!repeat) LookAlongWalk();
                return;
            }
            if (code == Key.G || code == Key.B)
            {
                if (!repeat) ToggleCar();
                return;
            }
            if (code == Key.Space)
            {
                space = true;
                if (!repeat) Jump();
                return;
            }
            if (!KEYS.ContainsKey(code)) return;
            if (autoDrive != null)
            {
                autoDrive = null;
                OnAutoChange?.Invoke();
            }
            if (player != null && player.IsAuto)
            {
                var k = KEYS[code];
                var d = Orbit.ScreenToWorld(new Vec2(k.x, k.y), inputYaw);
                if (!repeat && d != null) player.Command(d.Value);
                return;
            }
            if (!held.Contains(code)) held.Add(code);
        }

        void OnKeyUp(Key code)
        {
            held.Remove(code);
            if (code == Key.LeftShift || code == Key.RightShift) shift = input.ShiftHeld;
            if (code == Key.Space) space = false;
        }

        /// <summary>The mouse wheel over the view.</summary>
        public void Wheel(double deltaY, bool ctrl)
        {
            double k = Math.Exp(-deltaY * (ctrl ? 0.01 : 0.0015));
            if (mode == CameraMode.Close) chaseDistance = Math.Min(CHASE_MAX, Math.Max(CHASE_MIN, chaseDistance / k));
            else zoom = Math.Min(MAX_ZOOM, Math.Max(MIN_ZOOM, zoom * k));
        }

        void ReadPad(double dt)
        {
            var s = input.Pad;
            if (background)
            {
                padState = PadState.Idle;
                if (s.Pressed(Button.Menu)) OnPadAction?.Invoke(PadAction.Menu);
                return;
            }
            padState = s;
            if (s.Pressed(Button.A)) Jump();
            var actions = new[] { (Button.B, PadAction.Car), (Button.X, PadAction.Auto), (Button.Y, PadAction.CloseUp), (Button.LeftStick, PadAction.Night), (Button.View, PadAction.NewCharacter), (Button.Menu, PadAction.Menu) };
            foreach (var (b, action) in actions) if (s.Pressed(b)) OnPadAction?.Invoke(action);
            if (s.Pressed(Button.RightStick)) LookAlongWalk();
            if (s.Pressed(Button.Left)) Rotate(-1);
            if (s.Pressed(Button.Right)) Rotate(1);
            if (mode == CameraMode.Close && room == null) chaseSwing += s.look.x * PAD_SWING_RATE * dt;
            else
            {
                int turn = lookFlickX.Update(s.look.x);
                if (turn != 0) Rotate(turn);
            }
            int tilt = lookFlickY.Update(s.look.y);
            if (tilt != 0) Tilt(-tilt);
            int zoomDir = (s.Held(Button.RB) || s.Held(Button.Up) ? 1 : 0) - (s.Held(Button.LB) || s.Held(Button.Down) ? 1 : 0);
            if (zoomDir != 0)
            {
                double k = Math.Exp(zoomDir * PAD_ZOOM_RATE * dt);
                if (mode == CameraMode.Close) chaseDistance = Math.Min(CHASE_MAX, Math.Max(CHASE_MIN, chaseDistance / k));
                else zoom = Math.Min(MAX_ZOOM, Math.Max(MIN_ZOOM, zoom * k));
            }
            int flick = moveFlick.Update(Numbers.Hypot(s.move.x, s.move.y));
            if (autoDrive != null)
            {
                if (flick != 0 || s.Pressed(Button.RT) || s.Pressed(Button.LT))
                {
                    autoDrive = null;
                    OnAutoChange?.Invoke();
                }
            }
            else if (flick != 0 && player != null && player.IsAuto)
            {
                var d = Orbit.ScreenToWorld(s.move, inputYaw);
                if (d != null) player.Command(d.Value);
            }
        }

        /// <summary>One display frame: read the pad, step the town in small slices, then draw.</summary>
        public void Update()
        {
            double now = Now;
            double left = Math.Min(0.5, Math.Max(0, (now - last) / 1000));
            last = now;
            ReadPad(Math.Min(0.1, left));
            while (left > 1e-6)
            {
                double dt = Math.Min(0.05, left);
                Tick(dt);
                left -= dt;
            }
            simAt = now;
            Frame(Math.Min(0.1, (now - lastFrame) / 1000));
            lastFrame = now;
        }

        static double Clamp1(double v) => Math.Max(-1, Math.Min(1, v));

        void Tick(double dt)
        {
            if (world == null) return;
            time += dt;
            double kx = 0, ky = 0;
            foreach (var code in held)
            {
                kx += KEYS[code].x;
                ky += KEYS[code].y;
            }
            var pad = padState;
            var inputV = new Vec2(kx + pad.move.x, ky + pad.move.y);
            var p = player;
            steering = held.Count > 0 || pad.move.x != 0 || pad.move.y != 0;
            if (p != null) p.sprinting = shift || pad.rt > 0.3;
            if (p != null) p.jumpHeld = space || pad.Held(Button.A);
            if (!steering)
            {
                inputYaw = ViewYaw;
                screenKeys = false;
            }
            if (p != null && riding != null) FollowRide();
            else if (p != null && driving != null)
            {
                var car = cars[driving.Value].car;
                if (autoDrive != null)
                {
                    var others = traffic?.Positions().Select(c => new Vec2(c.x, c.y)).ToList() ?? new List<Vec2>();
                    for (int i = 0; i < cars.Count; i++) if (i != driving) others.Add(new Vec2(cars[i].car.x, cars[i].car.y));
                    autoDrive.Step(car, dt, others);
                }
                else
                {
                    double triggers = pad.rt - pad.lt;
                    double throttle = Clamp1(-Numbers.Sign(ky) + (triggers != 0 ? triggers : -pad.move.y));
                    car.Drive(throttle, Clamp1(inputV.x), dt, canDrive, shift || pad.rt > 0.3);
                }
                SyncDriver();
            }
            else if (p != null && mode == CameraMode.Close && !p.IsAuto && room == null && !screenKeys)
            {
                if (inputV.y < 0 && chaseSwing != 0)
                {
                    p.heading += chaseSwing;
                    chaseSwing = 0;
                }
                double heading = p.heading - Clamp1(inputV.x) * TURN_RATE * dt;
                var ahead = new Vec2(Math.Sin(heading), Math.Cos(heading));
                double back = Clamp1(inputV.y);
                p.Update(back != 0 ? new Vec2(-ahead.x * back, -ahead.y * back) : (Vec2?)null, dt, world.model, open, world.solids);
                p.heading = heading;
            }
            else
            {
                var d = Orbit.ScreenToWorld(inputV, inputYaw);
                double k = Math.Min(1, Numbers.Hypot(inputV.x, inputV.y));
                p?.Update(d != null ? new Vec2(d.Value.x * k, d.Value.y * k) : (Vec2?)null, dt, world.model, open, world.solids);
            }
            UpdateDoors(dt);
            UpdateRoom();
            foreach (var q in people) q.Update(dt);
            var obstacles = cars.Select(c => new Vec2(c.car.x, c.car.y)).ToList();
            if (p != null && !InCar && room == null) obstacles.Add(new Vec2(p.x, p.y));
            traffic?.Update(dt, obstacles);
            dusk += ((night ? 1 : 0) - dusk) * Math.Min(1, dt * 2.5);
            KeepInSight(dt);
        }

        void UpdateDoors(double dt)
        {
            var p = player;
            for (int i = 0; i < world.doors.Count; i++)
            {
                var d = world.doors[i];
                var at = Interiors.DoorOf(world.rooms[i]).at;
                bool near = p != null && !InCar && Math.Abs(p.x - at.x) < DOOR_REACH && Math.Abs(p.y - at.y) < DOOR_REACH && Numbers.Hypot(p.x - at.x, p.y - at.y) < DOOR_REACH;
                double target = near ? 1 : 0;
                if (d.open == target) continue;
                double was = d.open;
                d.open = target > d.open ? Math.Min(1, d.open + dt * DOOR_SPEED) : Math.Max(0, d.open - dt * DOOR_SPEED);
                world.SwingDoor(i);
                if (was == 0 && d.open > 0) ShowRoom(i, true);
                if (d.open == 0 && i != room) ShowRoom(i, false);
            }
        }

        void UpdateRoom()
        {
            var p = player;
            int? now = null;
            if (p != null)
            {
                var at = new Vec2(p.x, p.y);
                if (room != null && Interiors.InRoom(world.rooms[room.Value], at, Interiors.LEAVE_MARGIN)) now = room;
                else
                {
                    int found = world.rooms.FindIndex(r => Interiors.InRoom(r, at, Interiors.ENTER_MARGIN));
                    now = found < 0 ? (int?)null : found;
                }
            }
            if (now == room) return;
            if (room != null)
            {
                world.SetShellVisible(room.Value, true);
                var it = world.InteriorOf(room.Value);
                foreach (var side in it.walls.Keys.ToList()) it.SetWallScale(side, 1);
                if (world.doors[room.Value].open == 0) ShowRoom(room.Value, false);
            }
            var left = room;
            room = now;
            roomTurn = 0;
            if (left != null && now == null && mode == CameraMode.Iso)
            {
                SetStop(Orbit.NearestStop(yaw));
                manualHold = MANUAL_HOLD;
            }
            if (left != null && now == null && mode == CameraMode.Close && p != null)
            {
                if (steering) screenKeys = true;
                var cp = MapOf(chasePos);
                chaseYawNow = DetMath.Atan2(cp.x - p.x, cp.z - p.y);
                shownChaseDistance = Math.Min(CHASE_MAX, Numbers.Hypot(cp.x - p.x, cp.z - p.y) / Math.Cos(CHASE_TILT_GUESS));
            }
            if (now != null)
            {
                world.SetShellVisible(now.Value, false);
                ShowRoom(now.Value, true);
            }
        }

        void ShowRoom(int id, bool on)
        {
            world.InteriorOf(id).Visible = on;
            if (on) ShowKeeper(id);
            else if (keepers.TryGetValue(id, out var k)) k.avatar.Visible = false;
        }

        void ShowKeeper(int id)
        {
            var spot = world.plans[id].keeper;
            if (spot == null) return;
            if (!keepers.TryGetValue(id, out var k))
            {
                var rng = new Rng($"{world.model.seed}::keeper::{id}");
                var look = Catalog.RandomLook(rng);
                var walker = new Walker(look, spot.Value.x, spot.Value.y) { heading = spot.Value.rot };
                var avatar = new Avatar(library.Character(look), Avatar.PERSON_HEIGHT, rng.Next(), world.root.transform);
                avatar.SnapYaw(spot.Value.rot);
                k = new Keeper { walker = walker, avatar = avatar };
                keepers[id] = k;
            }
            k.avatar.Visible = true;
        }

        void KeepInSight(double dt)
        {
            var p = player;
            manualHold -= dt;
            sightTimer -= dt;
            if (p == null)
            {
                hidden = false;
                return;
            }
            if (sightTimer <= 0)
            {
                sightTimer = 0.1;
                hidden = room == null && Blocked(new Vec2(p.x, p.y), Orbit.StopYaw(CameraStop));
            }
            var car = driving != null ? cars[driving.Value].car : null;
            if (!p.IsAuto && car == null)
            {
                walkDir = null;
                return;
            }
            Vec2? h = car != null ? (Math.Abs(car.speed) > 0.4 ? car.Forward : (Vec2?)null) : p.AutoHeading();
            if (h != null && Numbers.Hypot(h.Value.x, h.Value.y) > 1e-6)
            {
                if (walkDir == null) walkDir = h;
                double k = Math.Min(1, dt * HEADING_SMOOTHING);
                walkDir = new Vec2(walkDir.Value.x + (h.Value.x - walkDir.Value.x) * k, walkDir.Value.y + (h.Value.y - walkDir.Value.y) * k);
            }
            if (walkDir == null || manualHold > 0 || room != null) return;
            if (mode == CameraMode.Iso && zoom < AUTO_TURN_MIN_ZOOM) return;
            int next = Orbit.StopBehind(CameraStop, walkDir.Value);
            if (next != CameraStop) SetStop(next);
        }

        bool Blocked(Vec2 at, double yawAt)
        {
            var o = Orbit.CameraOffset(yawAt, pitch);
            foreach (var b in world.boxes) if (Orbit.RayHitsBox(at, SIGHT_HEIGHT, o, b)) return true;
            return false;
        }

        // Drawing -------------------------------------------------------------------------------

        struct Fixed
        {
            public double yaw, pitch, zoom, half;
            public Vector3 focus; // map terms: (x, height, map y)
        }

        void Frame(double dt)
        {
            if (world == null) return;
            if (background)
            {
                camera.enabled = false;
                return;
            }
            camera.enabled = true;
            var p = player;
            if (p != null && playerAvatar != null)
            {
                playerAvatar.Visible = !InCar;
                playerAvatar.Update(p, dt);
            }
            for (int i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                // The car you're driving shows through buildings, as you do on foot.
                bool through = i == driving;
                if (c.seeThrough != through)
                {
                    c.seeThrough = through;
                    c.view.SetLayer(through ? LayerMask.NameToLayer(Avatar.PLAYER_LAYER) : 0);
                }
                var f = c.car.Forward;
                c.view.Place(c.car.x, c.car.y, DetMath.Atan2(f.x, f.y));
                c.view.Lights(dusk);
            }
            for (int k = 0; k < people.Count; k++) avatars[k].Update(people[k], dt);
            remotes?.Update(dt, driving != null && p != null ? new Vec2(p.x, p.y) : (Vec2?)null, dusk);
            if (riding != null) FollowRide();
            fleet?.Update(dusk);
            foreach (var k in keepers.Values) if (k.avatar.Visible) k.avatar.Update(k.walker, dt);

            Fixed? fixedView = room != null ? RoomView(room.Value) : (Fixed?)null;
            double yawTarget = fixedView?.yaw ?? targetYaw;
            double zoomTarget = fixedView?.zoom ?? zoom;
            double pitchTarget = fixedView?.pitch ?? TargetPitch;
            yaw += Orbit.ShortestTurn(yaw, yawTarget) * (snap ? 1 : Orbit.EaseBy(5, dt));
            shownZoom += (zoomTarget - shownZoom) * (snap ? 1 : Math.Min(1, dt * (fixedView != null ? 5 : 10)));
            var target = fixedView?.focus ?? (p != null ? new Vector3((float)p.x, 0.3f, (float)p.y) : new Vector3((float)world.model.spawn.x, 0.3f, (float)world.model.spawn.y));
            focusFollow.Update(target.x, target.y, target.z, dt, snap);
            focus = new Vector3((float)focusFollow.ax, (float)focusFollow.ay, (float)focusFollow.az);
            pitch += (pitchTarget - pitch) * (snap ? 1 : Math.Min(1, dt * 6));
            if (mode == CameraMode.Close && p != null && fixedView == null) PlaceChase(p, dt);
            if (mode == CameraMode.Close && fixedView != null) PlaceChaseInRoom(fixedView.Value, dt);
            if (room != null) CutWalls(room.Value, dt);
            Vector3? roomLight = fixedView != null ? Coords.Map(fixedView.Value.focus.x, fixedView.Value.focus.z, Interiors.WALL_HEIGHT * 0.9) : (Vector3?)null;
            snap = false;
            PlaceCamera();

            AnimateDayNight(roomLight);
            routeTimer -= dt;
            if (routeTimer <= 0)
            {
                DrawRoute();
                routeTimer = 0.2;
            }
            overlay.DrawMinimap(p != null ? new Vec2(p.x, p.y) : (Vec2?)null, ViewQuad(), Goal, Route);
            hudTimer -= dt;
            if (hudTimer <= 0)
            {
                UpdateHud();
                hudTimer = 0.25;
            }
        }

        void UpdateHud()
        {
            var p = player;
            var it = p?.AutoItinerary();
            steps = autoDrive != null
                ? Directions.ForDrive(world.names, autoDrive.Stretches())
                : it != null ? Directions.ForWalk(world.sidewalks, world.names, it) : null;
            if (it != null && !it.joining) street = world.names.ofRoad[world.sidewalks.paths[it.start.path].road];
            else street = p != null && room == null ? StreetAt(new Vec2(p.x, p.y)) : null;
            string name = street != null ? world.names.streets[street.Value].name : null;
            overlay.SetHud(name, steps);
            bool pad = input.Connected;
            if (driving != null)
            {
                int kmh = Numbers.RoundInt((Math.Abs(cars[driving.Value].car.speed) / Vehicle.TOP_SPEED) * 50);
                overlay.SetPrompt(autoDrive != null
                    ? pad ? $"Auto drive · {kmh} km/h · stick or triggers take the wheel · X stop · B to get out" : $"Auto drive · {kmh} km/h · arrows take the wheel · T stop · G to get out"
                    : pad ? $"Driving · {kmh} km/h · RT boost, LT brake, stick steers · X auto drive · B to get out" : $"Driving · {kmh} km/h · W/S speed and brake, Shift boost, A/D steer · T auto drive · G to get out");
            }
            else if (riding != null)
            {
                string driverName = null;
                if (remotes != null) foreach (var d in remotes.Drivers()) if (d.key == riding) driverName = d.name;
                overlay.SetPrompt($"Riding with {driverName ?? "them"} · {(pad ? "B" : "G")} to get out");
            }
            else
            {
                var pick = CarInReach();
                string key = pad ? "B" : "G";
                overlay.SetPrompt(pick == null ? null : pick.Value.driver != null ? $"Press {key} to ride with {pick.Value.driver.Value.name}" : $"Press {key} to get in the car");
            }
        }

        int? StreetAt(Vec2 at)
        {
            int best = -1;
            double bestD = double.PositiveInfinity;
            for (int i = 0; i < world.layout.roads.Count; i++)
            {
                var r = world.layout.roads[i];
                double d = Geo.DistToPolyline(at, r.points) - r.width / 2;
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best >= 0 && bestD < Layout.SIDEWALK + 0.35 ? world.names.ofRoad[best] : (int?)null;
        }

        /// <summary>A world position in map terms: (x, height, map y), as `focus` is kept.</summary>
        static Vector3 MapOf(Vector3 u) => new Vector3(u.x, u.y, -u.z);

        /// <summary>A point in map terms (x, height, map y) as a world position.</summary>
        static Vector3 WorldOf(double x, double h, double y) => Coords.Map(x, y, h);

        double Aspect => Math.Max(1, viewport.width) / Math.Max(1, viewport.height);

        void PlaceChaseInRoom(Fixed f, double dt)
        {
            var o = Orbit.CameraOffset(f.yaw, f.pitch);
            double d = (f.half / Math.Tan(((CHASE_FOV / 2) * Math.PI) / 180)) * 1.05;
            var at = new Vector3((float)(f.focus.x + o.x * d), (float)(f.focus.y + o.up * d), (float)(f.focus.z + o.y * d));
            float k = (float)(snap ? 1 : Math.Min(1, dt * 4));
            chasePos = Vector3.Lerp(chasePos, WorldOf(at.x, at.y, at.z), k);
            chaseLook = Vector3.Lerp(chaseLook, WorldOf(f.focus.x, f.focus.y, f.focus.z), k);
            var cl = MapOf(chaseLook);
            lookFollow.Update(cl.x, cl.y, cl.z, dt, true);
        }

        void CutWalls(int id, double dt)
        {
            var cam = MapOf(mode == CameraMode.Close ? chasePos : camera.transform.position);
            var r = world.rooms[id];
            var dir = new Vec2(cam.x - (r.x0 + r.x1) / 2, cam.z - (r.y0 + r.y1) / 2);
            double low = 0.12 / Interiors.WALL_HEIGHT;
            var it = world.InteriorOf(id);
            foreach (var side in it.walls.Keys.ToList())
            {
                double want = Interiors.WallFacesCamera(side, dir) ? low : 1;
                double s = it.wallScale[side];
                it.SetWallScale(side, s + (want - s) * (snap ? 1 : Math.Min(1, dt * 8)));
            }
        }

        Fixed RoomView(int id)
        {
            var r = world.rooms[id];
            var fr = Interiors.Frame(r);
            double aspect = Aspect;
            bool quarter = Math.Abs(roomTurn) % 2 == 1;
            double across = quarter ? fr.depth : fr.width;
            double deep = quarter ? fr.width : fr.depth;
            double tall = deep * Math.Sin(ROOM_PITCH) + Interiors.WALL_HEIGHT * Math.Cos(ROOM_PITCH);
            double half = Math.Max(1.6, Math.Max((tall / 2) * ROOM_MARGIN, ((across / 2) * ROOM_MARGIN) / aspect));
            return new Fixed
            {
                yaw = Interiors.RoomCameraYaw(r, roomTurn),
                pitch = ROOM_PITCH,
                zoom = VIEW_HEIGHT / 2 / half,
                half = half,
                focus = new Vector3((float)((r.x0 + r.x1) / 2), 0.2f, (float)((r.y0 + r.y1) / 2)),
            };
        }

        double ViewYaw => mode == CameraMode.Close && room == null ? chaseYawNow : yaw;

        void PlaceChase(Player p, double dt)
        {
            double want = Orbit.ChaseYaw(p.heading) + chaseSwing;
            chaseYawNow += Orbit.ShortestTurn(chaseYawNow, want) * (snap ? 1 : Orbit.EaseBy(3.5, dt));
            double wanted = chaseDistance * (InCar ? DRIVING_CHASE : 1);
            shownChaseDistance += (wanted - shownChaseDistance) * (snap ? 1 : Orbit.EaseBy(8, dt));
            var o = Orbit.CameraOffset(chaseYawNow, pitch);
            double dist = shownChaseDistance;
            var at = new Vec2(p.x, p.y);
            foreach (var b in world.boxes) dist = Math.Min(dist, Math.Max(0.5, Orbit.RayBoxDistance(at, CHASE_LOOK_HEIGHT, o, b) - 0.2));
            double flat = Numbers.Hypot(o.x, o.y);
            var ahead = new Vec2(-o.x / flat, -o.y / flat);
            lookFollow.Update(p.x + ahead.x * CHASE_LOOK_AHEAD, CHASE_LOOK_HEIGHT, p.y + ahead.y * CHASE_LOOK_AHEAD, dt, snap);
            chaseLook = WorldOf(lookFollow.ax, lookFollow.ay, lookFollow.az);
            chasePos = WorldOf(p.x + o.x * dist, CHASE_LOOK_HEIGHT + o.up * dist, p.y + o.y * dist);
        }

        void PlaceCamera()
        {
            camera.pixelRect = viewport;
            if (mode == CameraMode.Close)
            {
                camera.orthographic = false;
                camera.fieldOfView = (float)CHASE_FOV;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 300;
                camera.transform.position = chasePos;
                camera.transform.rotation = Quaternion.LookRotation(chaseLook - chasePos, Vector3.up);
                return;
            }
            double half = VIEW_HEIGHT / 2 / shownZoom;
            camera.orthographic = true;
            camera.orthographicSize = (float)half;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = (float)(CAMERA_DISTANCE * 3);
            var o = Orbit.CameraOffset(yaw, pitch);
            var pos = WorldOf(focus.x + o.x * CAMERA_DISTANCE, focus.y + o.up * CAMERA_DISTANCE, focus.z + o.y * CAMERA_DISTANCE);
            var f = WorldOf(focus.x, focus.y, focus.z);
            camera.transform.position = pos;
            camera.transform.rotation = Quaternion.LookRotation(f - pos, Vector3.up);
        }

        /// <summary>What the camera sees, as a quad on the ground (map tiles); rays above the horizon stop at the fog.</summary>
        Vec2[] ViewQuad()
        {
            var o = new Vec2[4];
            var corners = new[] { (0f, 1f), (1f, 1f), (1f, 0f), (0f, 0f) };
            for (int i = 0; i < 4; i++)
            {
                var ray = camera.ViewportPointToRay(new Vector3(corners[i].Item1, corners[i].Item2, 0));
                var near = ray.origin;
                var dir = ray.direction;
                double t = dir.y < -1e-4 ? -near.y / dir.y : double.PositiveInfinity;
                if (mode == CameraMode.Close)
                {
                    double flat = Numbers.Hypot(dir.x, dir.z);
                    if (flat == 0) flat = 1;
                    t = Math.Min(t, FOG_FAR / flat);
                }
                o[i] = new Vec2(near.x + dir.x * t, -(near.z + dir.z * t));
            }
            return o;
        }

        void ClearRoute()
        {
            if (route == null) return;
            UnityEngine.Object.Destroy(route);
            route = null;
        }

        static Material routeMaterial;

        /// <summary>Where auto-walk or auto-drive is headed: an orange line along the way, and a flag at the end.</summary>
        void DrawRoute()
        {
            ClearRoute();
            var pts = Route;
            var goal = Goal;
            if (pts.Count < 2 || goal == null) return;
            routeMaterial ??= Materials.Unlit(Coords.Hex(0xff6a00));
            route = new GameObject("route");
            route.transform.SetParent(world.root.transform, false);
            // The line lies flat: it faces its transform's z, turned to point up.
            var lineGo = new GameObject("line");
            lineGo.transform.SetParent(route.transform, false);
            lineGo.transform.rotation = Quaternion.Euler(90, 0, 0);
            var line = lineGo.AddComponent<LineRenderer>();
            line.sharedMaterial = routeMaterial;
            line.useWorldSpace = true;
            line.alignment = LineAlignment.TransformZ;
            line.widthMultiplier = 0.12f;
            line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.positionCount = pts.Count;
            for (int k = 0; k < pts.Count; k++) line.SetPosition(k, Coords.Map(pts[k].x, pts[k].y, 0.034));
            var g = goal.Value;
            var at = Coords.Map(g.x, g.y);
            Marker(PrimitiveType.Cylinder, at + new Vector3(0, 0.02f, 0), new Vector3(0.36f, 0.01f, 0.36f), routeMaterial);
            Marker(PrimitiveType.Cylinder, at + new Vector3(0, 0.45f, 0), new Vector3(0.03f, 0.45f, 0.03f), Materials.Unlit(Coords.Hex(0xfff8e8)));
            Marker(PrimitiveType.Cube, at + new Vector3(0.17f, 0.8f, 0), new Vector3(0.33f, 0.2f, 0.01f), routeMaterial);
        }

        void Marker(PrimitiveType type, Vector3 at, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(route.transform, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        static double Noise(double t)
        {
            double i = Math.Floor(t);
            double f = t - i;
            double H(double n)
            {
                double s = Math.Sin(n * 127.1) * 43758.5453;
                return s - Math.Floor(s);
            }
            double u = f * f * (3 - 2 * f);
            return H(i) * (1 - u) + H(i + 1) * u;
        }

        /// <summary>The street lamps' flicker, then the sky's light for this time of day.</summary>
        void AnimateDayNight(Vector3? roomLight)
        {
            double t = time;
            if (lampLevels.Length != world.lamps.Count) lampLevels = new double[world.lamps.Count];
            for (int i = 0; i < world.lamps.Count; i++)
            {
                var lamp = world.lamps[i];
                double flicker = lamp.faulty
                    ? Noise(t * 9 + lamp.phase) > 0.35 ? 0.15 + 0.2 * Noise(t * 30 + lamp.phase) : 1
                    : 0.9 + 0.1 * Math.Sin(t * 11 + lamp.phase) * Math.Sin(t * 3.7 + lamp.phase * 2);
                lampLevels[i] = dusk * flicker;
            }
            sky.Update(dusk, mode == CameraMode.Close, FOG_NEAR, FOG_FAR, WorldOf(focus.x, 0, focus.z), world.lamps, lampLevels, roomLight);
        }
    }
}

// The explorable 3D town: an orthographic
// camera at an isometric-style pitch orbiting the player between 12 fixed
// headings (Q / E), tilted through fixed stops (Z / X), turned to look down the
// street (F), and a close-up chase camera (V). WASD or the arrows walk (or
// auto-walk wanders the sidewalks), G / B gets in and out of cars, doors swing
// open into furnished ground floors, townsfolk stroll and traffic drives, and
// day and night blend with the street lamps (a few faulty) lighting up. Other
// players come and go through the network (NetHost); in a shared town one
// client runs the traffic and townsfolk and the rest follow it (WorldSync), so
// everyone sees the same. You have one car of your own at a time, and can ask
// for the way to another player (Find), or be shown the way somewhere by a
// mission (Guide; MissionPlay runs the mission's people and parking spots).
// Walk up to a townsperson and they stop to look at you; jump then asks them
// along, and they follow the way you go (riding along in whatever you drive)
// until you face them and jump again.

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

    public enum PadAction { Car, Night, NewCharacter, Menu, Center, Characters, Cars, ChooserPick, ChooserClose, BackToMap }

    /// <summary>The local player as the network sees them. Ground is (x, z); y is height.</summary>
    public struct LocalState
    {
        public double x, y, z, yaw, speed;
        public int car;
        public string ride;
        /// <summary>When this position was worked out (ms on this client's clock).</summary>
        public double at;
    }

    /// <summary>Someone else in the town, as the room's once-a-second roll call has them.</summary>
    public struct OtherPlayer
    {
        public string key;
        public string name;
        public double x, y;
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
        /// <summary>A notice that goes away by itself after a few seconds.</summary>
        void Flash(string text);
        /// <summary>A grid of choices (characters, cars) is open over the view.</summary>
        bool Choosing { get; }
        Action<Vec2> OnMinimapPick { set; }
        /// <summary>The big map (Start, or M): open over the view, with a reticle to place a waypoint.</summary>
        bool MapOpen { get; }
        void OpenMap();
        void CloseMap();
        /// <summary>Nudge the big map's reticle by (dx, dy) tiles.</summary>
        void MoveReticle(Vec2 by);
        Vec2 Reticle { get; }
        /// <summary>A point on the big map was clicked.</summary>
        Action<Vec2> OnWaypointPick { set; }
        /// <summary>Your waypoint, for the maps to mark (null: none).</summary>
        void SetWaypoint(Vec2? at);
    }

    public sealed class ExploreView : INetHost
    {
        const double VIEW_HEIGHT = 20;
        const double MIN_ZOOM = 0.35;
        const double MAX_ZOOM = 2.5;
        /// <summary>
        /// Behind the sign-in card: close in on the middle of a small town, a little
        /// lower than play, so there's little to draw on weak machines and phones (at this
        /// zoom and pitch the top of the view is about 18 tiles past the middle and a wide
        /// screen's corners about 24, inside the town).
        /// </summary>
        const double ATTRACT_ZOOM = 1.1;
        const double ATTRACT_PITCH = 30 * Math.PI / 180;
        const int MAX_PEDESTRIANS = 70;
        /// <summary>How far back the orthographic camera sits (the view doesn't change with it; URP's shadows reach this far and more).</summary>
        const double CAMERA_DISTANCE = 60;
        const double SIGHT_HEIGHT = 0.26;
        const double HEADING_SMOOTHING = 1.2;
        const double CHASE_DISTANCE = 2.4;
        const double CHASE_MIN = 1.1;
        const double CHASE_MAX = 8;
        const double CHASE_FOV = 50;
        const double CHASE_LOOK_HEIGHT = 0.42;
        const double CHASE_LOOK_AHEAD = 0.6;
        const double TURN_RATE = 2.6;
        /// <summary>
        /// The close camera sees this far into a haze and no further: drawing the whole
        /// town to the horizon cost too much frame time.
        /// </summary>
        const double FOG_NEAR = 22;
        const double FOG_FAR = 60;
        /// <summary>How far from you rooms are furnished behind their windows (further off they're dark glass).</summary>
        const double INTERIOR_REACH = 24;
        const double DOOR_REACH = 0.85;
        const double DOOR_SPEED = 3;
        /// <summary>How close on foot a room's keeper is about (rooms themselves are always there).</summary>
        const double ROOM_NEAR = 2.5;
        static readonly double ROOM_PITCH = (55 * Math.PI) / 180;
        const double ROOM_MARGIN = 1.35;
        const double CHASE_TILT_GUESS = 0.3;
        const double PLAYER_SCALE = 1.05;
        const double CAR_REACH = 1.0;
        const double DRIVING_CHASE = 1.7;
        const double PAD_SWING_RATE = 2.4;
        const double PAD_ZOOM_RATE = 1.4;
        /// <summary>Seconds A is held, on foot, before the jump it gave runs into a sprint.</summary>
        const double A_SPRINT_AFTER = 0.25;

        static readonly Dictionary<Key, (int x, int y)> KEYS = new Dictionary<Key, (int, int)>
        {
            [Key.W] = (0, -1), [Key.UpArrow] = (0, -1),
            [Key.S] = (0, 1), [Key.DownArrow] = (0, 1),
            [Key.A] = (-1, 0), [Key.LeftArrow] = (-1, 0),
            [Key.D] = (1, 0), [Key.RightArrow] = (1, 0),
        };

        sealed class MyCar
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
        /// <summary>When (ms) the car we're riding in went missing from the town, if it has: a dropped update shouldn't throw us out.</summary>
        double? rideLostAt;
        const double RIDE_LOST_AFTER = 2000;
        public Func<double, double, bool> TeleportHook;
        Player player;
        Avatar playerAvatar;
        readonly List<Pedestrian> people = new List<Pedestrian>();
        readonly List<Avatar> avatars = new List<Avatar>();
        /// <summary>Townsfolk following you, first nearest; the way you've walked (newest last), for them to follow along.</summary>
        readonly List<Pedestrian> followers = new List<Pedestrian>();
        readonly List<Vec2> trail = new List<Vec2>();
        /// <summary>The townsperson who's stopped to look at you (jump asks them along), or null.</summary>
        Pedestrian noticing;
        bool followersAboard;
        Vec2? lastSpot;
        /// <summary>How near a townsperson notices you, how far back a follower can be sent off from, and the trail's spacing and length (tiles).</summary>
        const double NOTICE = 1.0;
        const double SEND_OFF = 1.6;
        const double TRAIL_STEP = 0.12;
        const double TRAIL_LENGTH = 14;
        /// <summary>Each follower after the first keeps this much further back.</summary>
        const double FOLLOW_SPACING = 0.55;
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
        readonly HashSet<int> shownRooms = new HashSet<int>();
        readonly HashSet<int> wantedRooms = new HashSet<int>();
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
        GameObject route;
        double routeTimer;
        bool background;
        double dusk;
        double time;
        double[] lampLevels = new double[0];
        bool snap = true;
        readonly List<Key> held = new List<Key>();
        PadState padState = PadState.Idle;
        double aHeldFor;
        readonly Flick lookFlickX = new Flick();
        readonly Flick lookFlickY = new Flick();
        readonly Flick moveFlick = new Flick();
        bool shift;
        bool space;
        bool steering;
        public Action<PadAction> OnPadAction;
        readonly List<MyCar> cars = new List<MyCar>();
        int? driving;
        AutoDrive autoDrive;
        public Action OnAutoChange;
        public Action<bool> OnCarChange;
        readonly HashSet<int> takenParked = new HashSet<int>();
        /// <summary>Running the traffic and townsfolk here (alone, or the room's host), rather than following the host's.</summary>
        bool authority = true;
        readonly WorldSync sync = new WorldSync();
        /// <summary>How many townsfolk the town starts with (more join: drivers put out of their cars).</summary>
        int basePeople;
        /// <summary>The town's cars someone has taken ("p3", "t12"), applied here.</summary>
        readonly HashSet<string> taken = new HashSet<string>();
        bool takenSynced;
        /// <summary>A parked or traffic car got into ("p3", "t12"): the network tells everyone.</summary>
        public Action<string> OnTake;
        /// <summary>Night or day changed (by someone in the room, or here).</summary>
        public Action OnNightChange;
        List<OtherPlayer> others = new List<OtherPlayer>();
        readonly Dictionary<string, CarView> othersParked = new Dictionary<string, CarView>();
        readonly Dictionary<string, Vec2> othersParkedAt = new Dictionary<string, Vec2>();
        /// <summary>Who we're finding the way to, the way there and its directions.</summary>
        string finding;
        List<Vec2> findRoute = new List<Vec2>();
        List<Step> findSteps;
        double findTimer;
        /// <summary>A spot picked on the big map to be shown the way to (instead of a player), until you get there.</summary>
        Vec2? waypoint;
        /// <summary>How close counts as there (tiles), and how fast the big map's reticle moves at full stick (tiles a second, per 100 tiles of town).</summary>
        const double WAYPOINT_REACHED = 1.0;
        const double RETICLE_RATE = 45;
        GameObject findPath;
        Palette findPalette;
        /// <summary>The finder's target left the town.</summary>
        public Action OnFindLost;
        /// <summary>Where a mission is showing the way to (Guide), and what to call it.</summary>
        Func<Vec2?> guideTo;
        string guideName;
        /// <summary>Besides you, who opens doors by walking up to them (a mission's people).</summary>
        public readonly List<Vec2> DoorOpeners = new List<Vec2>();
        bool inDialogue;
        /// <summary>The pad's A, before it's a jump: true if it was used (to talk to someone).</summary>
        public Func<bool> OnPadA;
        /// <summary>A prompt that wins over the view's own (null: none).</summary>
        public Func<string> PromptOverride;
        /// <summary>The sign-in screen's town: no controls, an auto walk and a slowly turning camera.</summary>
        public bool Attract;
        Func<double, double, bool> canDrive = (x, y) => false;
        Func<double, double, bool> canSail = (x, y) => false;
        /// <summary>What's drawn as sidewalk or road, short of sheds and hangars: walkable where it overhangs a closed tile (the water's edge).</summary>
        Func<double, double, bool> walkPaved;
        /// <summary>How low a plane may fly over each tile: clear of the roofs.</summary>
        float[] skyFloor = new float[0];
        /// <summary>How far above a roof a plane keeps.</summary>
        const double ROOF_CLEARANCE = 0.6;
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
            overlay.OnWaypointPick = SetWaypoint;
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
            shownRooms.Clear();
            room = null;
            foreach (var c in cars) RemoveCar(c);
            cars.Clear();
            driving = null;
            riding = null;
            autoDrive = null;
            takenParked.Clear();
            taken.Clear();
            takenSynced = false;
            sync.Clear();
            foreach (var v in othersParked.Values) v.Dispose();
            othersParked.Clear();
            othersParkedAt.Clear();
            finding = null;
            waypoint = null;
            overlay.SetWaypoint(null);
            overlay.CloseMap();
            guideTo = null;
            findRoute = new List<Vec2>();
            findSteps = null;
            ClearFindPath();
            ClearRoute();
            DoorOpeners.Clear();
            avatars.Clear();
            people.Clear();
            followers.Clear();
            trail.Clear();
            noticing = null;
            followersAboard = false;
            lastSpot = null;
            player = null;
            playerAvatar = null;
            world = new TownWorld(model, library);
            remotes = new RemotePlayers(world.root.transform, library);
            snap = true;
            open = WalkMask();
            var paved = Vehicle.Paved(model, world.layout);
            var use = world.layout.use;
            int width = model.width;
            walkPaved = (x, y) =>
            {
                if (!paved(x, y)) return false;
                byte u = use[Grid.Index((int)Math.Floor(x), (int)Math.Floor(y), width)];
                return u != Use.SHED && u != Use.HANGAR;
            };
            canDrive = Vehicle.Drivable(model, world.layout);
            canSail = Vehicle.Sailable(model, world.layout);
            skyFloor = SkyFloor();
            overlay.SetTown(model);

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
            basePeople = people.Count;
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
            if (open.Length > 0 && !Player.Standable(x, y, open, model, solids, walkPaved))
            {
                double bestD = double.PositiveInfinity;
                for (int i = 0; i < open.Length; i++)
                {
                    if (!Player.Standable((i % w) + 0.5, i / w + 0.5, open, model, solids, walkPaved)) continue;
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

        public void PlacePlayer(double x, double z, bool teleported)
        {
            // Put back by the server while driving: back there in the car, still driving.
            if (!teleported && driving != null && player != null && world != null)
            {
                var car = cars[driving.Value].car;
                car.x = x;
                car.y = z;
                car.speed = 0;
                player.x = x;
                player.y = z;
                snap = true;
                return;
            }
            // Riding: the server only put us where it last saw the car; we go on riding with it.
            if (!teleported && riding != null && player != null && world != null) return;
            MoveTo(x, z);
        }

        public void SyncRemotes(List<RemoteState> players, string me) => remotes?.Sync(players, me);

        public void ClearRemotes()
        {
            remotes?.Clear();
            SyncParked(new List<RemoteParked>(), "");
        }

        public void SetOthers(List<OtherPlayer> list)
        {
            others = list ?? new List<OtherPlayer>();
            overlay.SetOthers(others.Select(o => new Vec2(o.x, o.y)).ToList());
            if (finding != null && !others.Any(o => o.key == finding))
            {
                Find(null);
                OnFindLost?.Invoke();
            }
        }

        public List<OtherPlayer> Others => others;

        /// <summary>Run the town here (true), or follow the host's (false).</summary>
        public void SetHost(bool host)
        {
            if (host == authority) return;
            authority = host;
            if (!authority || world == null) return;
            // Carry on from where the host had everyone.
            foreach (var c in traffic.cars) if (!c.taken) traffic.Adopt(c);
            foreach (var q in people) q.Rejoin(world.sidewalks);
            sync.Clear();
        }

        public bool IsHost => authority;

        /// <summary>The town's traffic and townsfolk, to send everyone else (while running it), or null.</summary>
        public WorldState? World()
        {
            if (!authority || world == null || traffic == null) return null;
            var looks = new List<string>();
            for (int i = basePeople; i < people.Count; i++) looks.Add(people[i].look);
            return new WorldState { cars = WorldSync.EncodeCars(traffic.cars), people = WorldSync.EncodePeople(people), looks = looks };
        }

        /// <summary>The host's traffic and townsfolk.</summary>
        public void ApplyWorld(byte[] carBytes, byte[] peopleBytes, List<string> looks)
        {
            if (authority || world == null) return;
            sync.Receive(carBytes, peopleBytes);
            int want = Math.Max(basePeople, sync.PeopleCount);
            while (people.Count < want && world.sidewalks.paths.Count > 0)
            {
                int extra = people.Count - basePeople;
                string look = looks != null && extra < looks.Count && Catalog.IsCharacter(looks[extra]) ? looks[extra] : Catalog.RandomLook(autoRng);
                AddPerson(Pedestrian.Create(look, world.sidewalks, autoRng));
            }
            while (people.Count > want)
            {
                avatars[avatars.Count - 1].Dispose();
                avatars.RemoveAt(avatars.Count - 1);
                people.RemoveAt(people.Count - 1);
            }
        }

        /// <summary>The room's list of taken cars: out of the traffic and off the street.</summary>
        public void SetTaken(string list)
        {
            bool first = !takenSynced;
            takenSynced = true;
            foreach (var id in (list ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                // Drivers put out of their cars long ago aren't put out again.
                ApplyTake(id, authority && !first);
        }

        void ApplyTake(string id, bool eject)
        {
            if (world == null || id.Length < 2 || !taken.Add(id) || !int.TryParse(id.Substring(1), out int i) || i < 0) return;
            if (id[0] == 'p' && i < world.parked.Count)
            {
                takenParked.Add(i);
                world.SetParkedVisible(i, false);
            }
            else if (id[0] == 't' && traffic != null && i < traffic.cars.Count && !traffic.cars[i].taken)
            {
                var c = traffic.Take(i);
                if (eject && Catalog.IsCar(c.model)) EjectDriver(c.x, c.y, c.heading);
            }
        }

        /// <summary>Into one of the town's cars: off the street here, and (online) for everyone.</summary>
        void TakeCar(string id)
        {
            ApplyTake(id, authority);
            OnTake?.Invoke(id);
        }

        public void SetTownNight(bool on)
        {
            if (night == on) return;
            night = on;
            OnNightChange?.Invoke();
        }

        /// <summary>Everyone else's parked cars.</summary>
        public void SyncParked(List<RemoteParked> list, string me)
        {
            if (world == null) return;
            var seen = new HashSet<string>();
            foreach (var c in list)
            {
                if (c.key == me || c.model < 0 || c.model >= Catalog.CARS.Length) continue;
                seen.Add(c.key);
                if (othersParked.TryGetValue(c.key, out var v) && v.model != c.model)
                {
                    v.Dispose();
                    othersParked.Remove(c.key);
                }
                if (!othersParked.TryGetValue(c.key, out v))
                {
                    v = new CarView(library.Get(Catalog.CARS[c.model]), world.root.transform) { model = c.model };
                    othersParked[c.key] = v;
                }
                var f = new Vec2(Math.Cos(c.heading), Math.Sin(c.heading));
                v.Place(c.x, c.y, DetMath.Atan2(f.x, f.y));
                othersParkedAt[c.key] = new Vec2(c.x, c.y);
            }
            foreach (var key in othersParked.Keys.ToList())
            {
                if (seen.Contains(key)) continue;
                othersParked[key].Dispose();
                othersParked.Remove(key);
                othersParkedAt.Remove(key);
            }
        }

        /// <summary>Your car, if it's parked (not while you drive it), for everyone else.</summary>
        public ParkedState OwnCar()
        {
            if (cars.Count == 0 || driving != null) return new ParkedState();
            var c = cars[0].car;
            return new ParkedState { model = c.model + 1, x = c.x, z = c.y, yaw = c.heading };
        }

        /// <summary>Where another player is: close by, as the room last moved them; further off, the roll call's spot.</summary>
        Vec2? PositionOf(string key)
        {
            var near = remotes?.PositionOf(key);
            if (near != null) return near;
            foreach (var o in others) if (o.key == key) return new Vec2(o.x, o.y);
            return null;
        }

        public void Notify(string t) => overlay.Notify(t);

        public List<Vec2> Route => autoDrive?.RoutePoints() ?? player?.AutoRoute() ?? new List<Vec2>();

        public Vec2? Goal => autoDrive != null ? autoDrive.GoalPoint() : player?.AutoGoal();

        // Finding another player: a dotted line along the streets to them, and directions.

        /// <summary>Show the way to this player (null: stop).</summary>
        public void Find(string key)
        {
            finding = key;
            findTimer = 0;
            if (key != null || waypoint != null)
            {
                waypoint = null;
                overlay.SetWaypoint(null);
            }
            if (key != null || guideTo != null) return;
            findRoute = new List<Vec2>();
            findSteps = null;
            ClearFindPath();
        }

        /// <summary>
        /// Show the way to wherever `to` says (asked again every second, as it may move),
        /// as to another player; null stops. Your waypoint, or finding a player, wins while there is one.
        /// </summary>
        public void Guide(Func<Vec2?> to, string name)
        {
            guideTo = to;
            guideName = name;
            findTimer = 0;
            if (to != null || finding != null || waypoint != null) return;
            findRoute = new List<Vec2>();
            findSteps = null;
            ClearFindPath();
        }

        public string Finding => finding;

        /// <summary>Show the way to this spot (in place of any player you were being shown the way to).</summary>
        public void SetWaypoint(Vec2 at)
        {
            Find(null);
            waypoint = at;
            findTimer = 0;
            overlay.SetWaypoint(at);
        }

        void ClearWaypoint() => Find(null);

        /// <summary>The big map open or shut (Start, or M).</summary>
        public void ToggleMap()
        {
            if (overlay.MapOpen) overlay.CloseMap();
            else if (world != null) overlay.OpenMap();
        }

        public bool MapOpen => overlay.MapOpen;

        public void CloseMap() => overlay.CloseMap();

        /// <summary>Where you're being shown the way to: your waypoint, the player you're finding, or where a mission's guiding you.</summary>
        Vec2? FindTarget => waypoint ?? (finding != null ? PositionOf(finding) : guideTo?.Invoke());

        /// <summary>The way to the nearest other player, or (already showing one) stop.</summary>
        public void FindNearest()
        {
            if (finding != null || player == null)
            {
                Find(null);
                return;
            }
            string best = null;
            double bestD = double.PositiveInfinity;
            foreach (var o in others)
            {
                double d = Numbers.Hypot(o.x - player.x, o.y - player.y);
                if (d < bestD)
                {
                    bestD = d;
                    best = o.key;
                }
            }
            if (best == null) overlay.Flash("No one else is in town");
            else Find(best);
        }

        /// <summary>The camera close behind you, at its usual distance, looking the way you face.</summary>
        public void CenterBehind()
        {
            var p = player;
            if (p == null) return;
            if (room != null)
            {
                roomTurn = 0;
                return;
            }
            SetCameraMode(CameraMode.Close);
            chaseSwing = 0;
            chaseDistance = CHASE_DISTANCE;
            chaseYawNow = Orbit.ChaseYaw(p.heading);
            shownChaseDistance = chaseDistance;
        }

        void UpdateFind(double dt)
        {
            findTimer -= dt;
            if ((finding == null && waypoint == null && guideTo == null) || player == null || world == null) return;
            var from = new Vec2(player.x, player.y);
            if (waypoint != null && Geo.Dist(from, waypoint.Value) < WAYPOINT_REACHED)
            {
                ClearWaypoint();
                overlay.Flash("You've reached your waypoint");
                return;
            }
            if (findTimer > 0) return;
            findTimer = 1;
            var target = FindTarget;
            if (target == null) return;
            string name = waypoint != null ? "your waypoint" : finding != null ? others.FirstOrDefault(o => o.key == finding).name ?? "them" : guideName;
            if (waypoint == null && Geo.Dist(from, target.Value) < 1.5)
            {
                findRoute = new List<Vec2>();
                findSteps = new List<Step> { new Step { kind = StepKind.Arrive, text = finding != null ? $"You're with {name}" : $"You're at {name}", distance = 0 } };
                ClearFindPath();
                return;
            }
            // Along the streets themselves, and off the street only at the last, to them.
            var way = StreetRoute.Between(world.layout.roads, from, target.Value);
            if (way != null)
            {
                findRoute = way.points;
                findSteps = Directions.ForDrive(world.names, way.stretches, name);
            }
            else
            {
                findRoute = new List<Vec2> { from, target.Value };
                findSteps = null;
            }
            DrawFindPath(target.Value);
        }

        void ClearFindPath()
        {
            if (findPath == null) return;
            UnityEngine.Object.Destroy(findPath.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.Destroy(findPath);
            findPath = null;
        }

        static Material findMaterial;

        /// <summary>The way to them as a line of dots on the ground, and a ring where they are.</summary>
        void DrawFindPath(Vec2 target)
        {
            ClearFindPath();
            if (findRoute.Count < 2) return;
            findPalette ??= new Palette();
            var b = new MeshBuilder(findPalette);
            const int color = 0x3b9be0;
            foreach (var p in Geo.SamplePolyline(findRoute, 0.32)) b.Disc(p, 0.065, 0.036, color, 8);
            b.Disc(target, 0.3, 0.035, color);
            findMaterial ??= Materials.Unlit(Coords.Hex(color));
            findPath = new GameObject("finding");
            findPath.transform.SetParent(world.root.transform, false);
            findPath.AddComponent<MeshFilter>().sharedMesh = b.Build("finding");
            var r = findPath.AddComponent<MeshRenderer>();
            r.sharedMaterial = findMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // Your car: one at a time.

        /// <summary>A car of this model right beside you (or, with no room there, in the nearest parking lane), in place of any car you had. False if there's no room near.</summary>
        public bool SpawnCar(int model)
        {
            if (player == null || world == null || riding != null) return false;
            GetOut();
            if (driving != null) return false;
            var near = new Vec2(player.x, player.y);
            var busy = CarsAbout();
            var spot = SpotBeside(busy) ?? Vehicle.CarSpotNear(near, world.layout.roads, Layout.PARKING / 2, canDrive, busy);
            if (spot == null) return false;
            AddCar(new Vehicle(model, spot.Value.x, spot.Value.y, spot.Value.heading));
            return true;
        }

        /// <summary>Where the town's cars are, besides yours: parked (by the town or other players) and in traffic.</summary>
        public List<Vec2> CarsAbout()
        {
            var busy = new List<Vec2>();
            if (world == null) return busy;
            for (int i = 0; i < world.parked.Count; i++) if (!takenParked.Contains(i)) busy.Add(new Vec2(world.parked[i].x, world.parked[i].y));
            busy.AddRange(othersParkedAt.Values);
            if (traffic != null) foreach (var c in traffic.Positions()) busy.Add(new Vec2(c.x, c.y));
            return busy;
        }

        /// <summary>
        /// Somewhere a car fits right beside you, facing the way you are (or turned, if
        /// only that fits): your right first, then your left, then a little further out.
        /// </summary>
        Vehicle.Spot? SpotBeside(List<Vec2> busy)
        {
            var f = new Vec2(Math.Sin(player.heading), Math.Cos(player.heading));
            double facing = Math.Atan2(f.y, f.x); // a car's heading points along (cos, sin)
            foreach (double off in new[] { 0.7, 0.9, 1.2 })
                foreach (int side in new[] { 1, -1 })
                {
                    var p = new Vec2(player.x + f.y * off * side, player.y - f.x * off * side);
                    if (busy.Any(q => Geo.Dist(q, p) < 1.3)) continue;
                    foreach (double turn in new[] { 0, Math.PI, Math.PI / 4, -Math.PI / 4, Math.PI / 2 })
                        if (Vehicle.Fits(p.x, p.y, facing + turn, canDrive)) return new Vehicle.Spot { x = p.x, y = p.y, heading = facing + turn };
                }
            return null;
        }

        /// <summary>Your car becomes this model, where it is (even with you at the wheel).</summary>
        public void ChangeCar(int model)
        {
            if (cars.Count == 0 || world == null) return;
            var old = cars[0].car;
            // Boats and planes are where they are: only a car becomes another.
            if (old.model == model || old.kind != VehicleKind.Car || !Catalog.IsCar(model)) return;
            bool wasDriving = driving != null;
            AddCar(new Vehicle(model, old.x, old.y, old.heading) { speed = old.speed });
            if (wasDriving) driving = 0;
        }

        public bool HasCar => cars.Count > 0;

        /// <summary>Your car (null: none yet), and whether you're at its wheel.</summary>
        public Vehicle OwnVehicle => cars.Count > 0 ? cars[0].car : null;
        public bool DrivingMyCar => driving == 0;

        /// <summary>Could a car be here (on a road, its parking lanes, a driveway…)?</summary>
        public bool CanDriveAt(double x, double y) => canDrive(x, y);

        public bool InRoom => room != null;

        /// <summary>Talking to someone: the keys and the pad are the conversation's, and you stand still.</summary>
        public bool InDialogue
        {
            get => inDialogue;
            set
            {
                inDialogue = value;
                if (!value) return;
                held.Clear();
                space = false;
            }
        }

        public int CarModel => cars.Count > 0 ? cars[0].car.model : -1;

        /// <summary>The next (or previous) character's look, in place.</summary>
        /// <summary>Become this character where you stand (null if there's no one to change).</summary>
        public string ChangeLook(string look)
        {
            if (player == null) return null;
            if (player.look != look) Spawn(look);
            return player.look;
        }

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
                var car = cars[driving.Value].car;
                autoDrive = on && car.kind == VehicleKind.Car ? new AutoDrive(world.layout.roads, autoRng, car) : null;
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
            if (InCar || player == null) return;
            if (TalkTo()) return;
            player.Jump();
        }

        /// <summary>
        /// Jump while someone's looking at you asks them along; facing someone who's
        /// following you sends them off. True if it did either (and so isn't a jump).
        /// </summary>
        bool TalkTo()
        {
            var p = player;
            if (p == null || world == null || p.lift > 0) return false;
            var you = new Vec2(p.x, p.y);
            var leaving = FacedFollower();
            if (leaving != null)
            {
                leaving.Leave(you, world.sidewalks);
                followers.Remove(leaving);
                return true;
            }
            if (noticing == null) return false;
            // One follower at a time: asking someone new along sends the last one off.
            foreach (var f in followers) f.Leave(you, world.sidewalks);
            followers.Clear();
            noticing.lookAt = null;
            noticing.Join();
            followers.Add(noticing);
            noticing = null;
            return true;
        }

        /// <summary>The follower nearest in front of you (or right beside you), within SEND_OFF, or null.</summary>
        Pedestrian FacedFollower()
        {
            var p = player;
            if (p == null || followersAboard) return null;
            double fx = Math.Sin(p.heading), fy = Math.Cos(p.heading);
            Pedestrian best = null;
            double bestD = SEND_OFF;
            foreach (var f in followers)
            {
                double dx = f.x - p.x, dy = f.y - p.y, d = Numbers.Hypot(dx, dy);
                if (d >= bestD || (d > 0.35 && (dx * fx + dy * fy) / d < 0.5)) continue;
                best = f;
                bestD = d;
            }
            return best;
        }

        /// <summary>
        /// Your company: who's stopped to look at you, and your followers walked along
        /// the way you went (or carried along, hidden, while you're in a vehicle).
        /// </summary>
        void UpdateCompany(double dt)
        {
            var p = player;
            if (p == null || world == null) return;
            if (followers.Count > 0) followers.RemoveAll(f => !people.Contains(f));
            var you = new Vec2(p.x, p.y);
            if (InCar)
            {
                followersAboard = followers.Count > 0;
                foreach (var f in followers) f.Place(p.x, p.y);
                trail.Clear();
                lastSpot = you;
                Notice(null);
                return;
            }
            // Out of a vehicle, or across the map in one go: everyone lines up behind you.
            if (followersAboard || (lastSpot != null && Numbers.Hypot(lastSpot.Value.x - you.x, lastSpot.Value.y - you.y) > 2.5)) LineUpBehind();
            followersAboard = false;
            lastSpot = you;
            if (trail.Count == 0 || Numbers.Hypot(trail[trail.Count - 1].x - you.x, trail[trail.Count - 1].y - you.y) >= TRAIL_STEP) trail.Add(you);
            while (trail.Count > 2 && trail.Count * TRAIL_STEP > TRAIL_LENGTH) trail.RemoveAt(0);
            for (int i = 0; i < followers.Count; i++)
                followers[i].Follow(BackAlong(Pedestrian.FOLLOW_GAP + FOLLOW_SPACING * i), you, p.speed, dt);
            // The nearest townsperson within reach stops to look at you (not while you auto-walk past).
            Pedestrian near = null;
            if (!p.IsAuto && p.lift <= 0)
            {
                double bestD = NOTICE;
                foreach (var q in people)
                {
                    if (q.aloof > 0) q.aloof -= dt;
                    if (q.following || q.aloof > 0) continue;
                    double d = Numbers.Hypot(q.x - p.x, q.y - p.y);
                    if (d < bestD)
                    {
                        bestD = d;
                        near = q;
                    }
                }
            }
            Notice(near);
        }

        void Notice(Pedestrian q)
        {
            if (noticing != null && noticing != q) noticing.lookAt = null;
            noticing = q;
            if (q != null && player != null) q.lookAt = new Vec2(player.x, player.y);
        }

        /// <summary>The point `back` tiles behind you along the way you walked (its far end if it's shorter).</summary>
        Vec2 BackAlong(double back)
        {
            var at = new Vec2(player.x, player.y);
            for (int i = trail.Count - 1; i >= 0; i--)
            {
                var q = trail[i];
                double d = Numbers.Hypot(q.x - at.x, q.y - at.y);
                if (d >= back && d > 1e-9)
                {
                    double k = back / d;
                    return new Vec2(at.x + (q.x - at.x) * k, at.y + (q.y - at.y) * k);
                }
                back -= d;
                at = q;
            }
            return at;
        }

        /// <summary>Stand your followers in a line behind you (on open ground, or else right where you are).</summary>
        void LineUpBehind()
        {
            var p = player;
            trail.Clear();
            trail.Add(new Vec2(p.x, p.y));
            double fx = Math.Sin(p.heading), fy = Math.Cos(p.heading);
            for (int i = 0; i < followers.Count; i++)
            {
                double back = Pedestrian.FOLLOW_GAP + FOLLOW_SPACING * i;
                double x = p.x - fx * back, y = p.y - fy * back;
                if (!Player.Standable(x, y, open, world.model, world.solids, walkPaved))
                {
                    x = p.x;
                    y = p.y;
                }
                followers[i].Place(x, y);
            }
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
                rideLostAt = null;
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
                    TakeCar("p" + pk.i);
                    car = new Vehicle(c.model, c.x, c.y, c.heading);
                }
                else
                {
                    var c = traffic.cars[pk.i];
                    car = new Vehicle(c.model, c.x, c.y, c.heading) { speed = Math.Min(c.speed, 1) };
                    TakeCar("t" + pk.i);
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
            AddPerson(Pedestrian.Create(look, world.sidewalks, rng, door));
        }

        void AddPerson(Pedestrian p)
        {
            people.Add(p);
            avatars.Add(new Avatar(library.Character(p.look), Avatar.PERSON_HEIGHT, autoRng.Next(), world.root.transform));
        }

        /// <summary>Your car, in place of the one you had (you only ever have one).</summary>
        int AddCar(Vehicle car)
        {
            foreach (var c in cars) RemoveCar(c);
            cars.Clear();
            driving = null;
            cars.Add(new MyCar { car = car, view = new CarView(library.Get(Catalog.CARS[car.model]), world.root.transform) });
            return 0;
        }

        void RemoveCar(MyCar c) => c.view.Dispose();

        void GetOut()
        {
            var p = player;
            if (driving == null || world == null || p == null) return;
            var car = cars[driving.Value].car;
            if (car.Airborne)
            {
                overlay.Flash("Land first: ease back to come down on open ground");
                return;
            }
            Vec2 spot;
            if (car.kind == VehicleKind.Boat)
            {
                // Off a boat only onto a dock or the shore, never into the water.
                var dry = Vehicle.ExitSpot(car, (x, y) => Player.Standable(x, y, open, world.model, world.solids, walkPaved));
                if (dry == null)
                {
                    overlay.Flash("Pull in beside a dock or the shore to get out");
                    return;
                }
                spot = dry.Value;
            }
            else spot = ExitBeside(car);
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
            Vehicle.ExitSpot(car, (x, y) => Player.Standable(x, y, open, world.model, world.solids, walkPaved)) ?? StandableNear(new Vec2(car.x, car.y));

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
                rideLostAt ??= Now;
                if (Now - rideLostAt > RIDE_LOST_AFTER) LeaveRide();
                return;
            }
            rideLostAt = null;
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
            // Up with a plane in the air (and so for everyone else, as the height they see you at).
            player.lift = car.altitude;
        }

        public void SetStreetsOnly(bool on)
        {
            streetsOnly = on;
            open = WalkMask();
            if (player != null && world != null && !Player.Standable(player.x, player.y, open, world.model, world.solids, walkPaved))
                Teleport(new Vec2(player.x, player.y));
        }

        byte[] WalkMask()
        {
            if (world == null) return new byte[0];
            if (streetsOnly) return Streets.Mask(world.model.tiles);
            // Not into sheds or hangars, or the water but over a bridge or a dock.
            var o = new byte[world.layout.use.Length];
            var tiles = world.model.tiles;
            for (int i = 0; i < o.Length; i++)
            {
                byte use = world.layout.use[i];
                bool wet = Terrain.IsWater(tiles.terrain[i]) && tiles.overlay[i] == Overlay.NONE && use != Use.DOCK;
                o[i] = use == Use.SHED || use == Use.HANGAR || wet ? (byte)0 : (byte)1;
            }
            return o;
        }

        float[] SkyFloor()
        {
            int w = world.model.width, h = world.model.height;
            var o = new float[w * h];
            foreach (var b in world.boxes)
                for (int y = Math.Max(0, (int)Math.Floor(b.y0)); y <= Math.Min(h - 1, (int)Math.Floor(b.y1)); y++)
                    for (int x = Math.Max(0, (int)Math.Floor(b.x0)); x <= Math.Min(w - 1, (int)Math.Floor(b.x1)); x++)
                        o[Grid.Index(x, y, w)] = Math.Max(o[Grid.Index(x, y, w)], (float)(b.top + ROOF_CLEARANCE));
            return o;
        }

        /// <summary>The lowest a plane may fly at (x, y); Infinity off the map, which it can't fly past.</summary>
        double FloorAt(double x, double y)
        {
            int w = world.model.width, h = world.model.height;
            if (x < 0.5 || y < 0.5 || x > w - 0.5 || y > h - 0.5) return double.PositiveInfinity;
            return skyFloor[Grid.Index((int)Math.Floor(x), (int)Math.Floor(y), w)];
        }

        Func<double, double, bool> OpenFor(Vehicle car) => car.kind == VehicleKind.Boat ? canSail : canDrive;

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
        }

        public int CameraStop => Orbit.NearestStop(targetYaw);

        void SetStop(int k) => targetYaw += Orbit.ShortestTurn(targetYaw, Orbit.StopYaw(k));

        public void SetNight(bool on) => night = on;

        public bool IsNight => night;

        /// <summary>Night or day straight away, without the fade.</summary>
        public double Dusk => dusk;

        // The sign-in screen's change of towns: the last one left standing (on the
        // Fading layer, which the camera leaves out) and drawn on its own, lit as it
        // was, from wherever the camera is now, while the next fades in over it.

        /// <summary>The layer a town fading out is moved to (Editor/Setup.cs adds it).</summary>
        public const string FADING_LAYER = "Fading";
        TownWorld fadingWorld;
        Fleet fadingFleet;
        readonly List<Avatar> fadingAvatars = new List<Avatar>();
        bool fadingNight;
        double fadingDusk;
        double[] fadingLamps = new double[0];

        /// <summary>Whether a town can be faded out live (the Fading layer exists).</summary>
        public static bool CanFade => LayerMask.NameToLayer(FADING_LAYER) >= 0;

        /// <summary>Like SetTown, but keep the old town standing, still, for RenderFading to draw until EndFade.</summary>
        public void SetTownFading(TownModel model)
        {
            EndFade();
            int layer = LayerMask.NameToLayer(FADING_LAYER);
            if (world != null && layer >= 0)
            {
                fadingWorld = world;
                fadingFleet = fleet;
                fadingAvatars.AddRange(avatars);
                foreach (var a in avatars) a.Freeze();
                fadingNight = night;
                fadingDusk = dusk;
                fadingLamps = world.lamps.Select(l => dusk).ToArray();
                foreach (var t in world.root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                world = null;
                fleet = null;
                avatars.Clear();
            }
            SetTown(model);
            // The camera keeps on round the new town's middle; carry the old one along so
            // its middle sits there too, and it fades out where it was on screen.
            if (fadingWorld != null)
            {
                var was = fadingWorld.model.center;
                fadingWorld.root.transform.position += WorldOf(model.center.x, 0, model.center.y) - WorldOf(was.x, 0, was.y);
            }
        }

        /// <summary>Draw the old town into `target` from where the camera is now, in its own light (call after Update).</summary>
        public void RenderFading(RenderTexture target)
        {
            if (fadingWorld == null || world == null) return;
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target };
            if (!UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request)) return;
            int mask = camera.cullingMask;
            camera.cullingMask = 1 << LayerMask.NameToLayer(FADING_LAYER);
            sky.Update(fadingDusk, fadingNight, mode == CameraMode.Close, FOG_NEAR, FOG_FAR, WorldOf(focus.x, 0, focus.z), fadingWorld.lamps, fadingLamps, null, withHeavens: false);
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
            camera.cullingMask = mask;
            // Back to this town's light for the frame itself.
            sky.Update(dusk, night, mode == CameraMode.Close, FOG_NEAR, FOG_FAR, WorldOf(focus.x, 0, focus.z), world.lamps, lampLevels, null, withHeavens: false);
        }

        /// <summary>Zoom to `z` (MIN_ZOOM: as far out as play goes), clamped.</summary>
        public void SetZoom(double z) => zoom = Math.Min(MAX_ZOOM, Math.Max(MIN_ZOOM, z));

        /// <summary>Draw the town as the camera sees it now into `target`, at the target's shape (no UI).</summary>
        public void Capture(RenderTexture target)
        {
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target };
            if (!UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request)) return;
            var rect = camera.rect;
            camera.rect = new UnityEngine.Rect(0, 0, 1, 1);
            camera.aspect = (float)target.width / target.height;
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
            camera.ResetAspect();
            camera.rect = rect;
        }

        /// <summary>Done fading: the old town goes.</summary>
        public void EndFade()
        {
            if (fadingWorld == null) return;
            fadingFleet?.Dispose();
            foreach (var a in fadingAvatars) a.Dispose();
            fadingAvatars.Clear();
            fadingWorld.Dispose();
            fadingWorld = null;
            fadingFleet = null;
        }

        public void SetNightNow(bool on)
        {
            night = on;
            dusk = on ? 1 : 0;
            OnNightChange?.Invoke();
        }

        // Input ----------------------------------------------------------------------------------

        void OnKeyDown(Key code, bool repeat)
        {
            if (background || Attract || inDialogue) return;
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
            if (Attract)
            {
                padState = PadState.Idle;
                return;
            }
            if (background)
            {
                padState = PadState.Idle;
                if (s.Pressed(Button.Menu)) OnPadAction?.Invoke(PadAction.Menu);
                return;
            }
            if (inDialogue)
            {
                // The conversation reads the pad itself (MissionPlay).
                padState = PadState.Idle;
                if (s.Pressed(Button.Menu)) OnPadAction?.Invoke(PadAction.Menu);
                return;
            }
            if (overlay.MapOpen)
            {
                // The big map: the left stick moves the reticle, A sets a waypoint there, Y clears it, X goes back to the town map, B or Start closes.
                padState = PadState.Idle;
                double rate = RETICLE_RATE * Math.Max(world?.model.width ?? 100, world?.model.height ?? 100) / 100;
                if (s.move.x != 0 || s.move.y != 0) overlay.MoveReticle(new Vec2(s.move.x * rate * dt, s.move.y * rate * dt));
                if (s.Pressed(Button.A)) SetWaypoint(overlay.Reticle);
                if (s.Pressed(Button.Y)) ClearWaypoint();
                if (s.Pressed(Button.B) || s.Pressed(Button.Menu)) overlay.CloseMap();
                if (s.Pressed(Button.X))
                {
                    overlay.CloseMap();
                    OnPadAction?.Invoke(PadAction.BackToMap);
                }
                return;
            }
            if (overlay.Choosing)
            {
                // A grid of characters or cars is open: LB and RB step through them (and switch grids), A picks, B closes.
                padState = PadState.Idle;
                var chooser = new[] { (Button.LB, PadAction.Characters), (Button.RB, PadAction.Cars), (Button.A, PadAction.ChooserPick), (Button.B, PadAction.ChooserClose), (Button.Menu, PadAction.ChooserClose) };
                foreach (var (b, action) in chooser) if (s.Pressed(b)) OnPadAction?.Invoke(action);
                return;
            }
            padState = s;
            // The one place the pad's buttons are given their jobs (A jump, and A held or RT for turbo, are read in Tick).
            if (s.Pressed(Button.A) && !(OnPadA?.Invoke() ?? false)) Jump();
            var actions = new[] { (Button.B, PadAction.Car), (Button.X, PadAction.Center), (Button.Y, PadAction.Night), (Button.View, PadAction.NewCharacter) };
            foreach (var (b, action) in actions) if (s.Pressed(b)) OnPadAction?.Invoke(action);
            if (s.Pressed(Button.Menu)) ToggleMap();
            if (s.Pressed(Button.RB)) FindNearest();
            if (s.Pressed(Button.LB)) OnPadAction?.Invoke(PadAction.Characters);
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
            int zoomDir = (s.Held(Button.Up) ? 1 : 0) - (s.Held(Button.Down) ? 1 : 0);
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
                if (overlay.MapOpen) break;
                kx += KEYS[code].x;
                ky += KEYS[code].y;
            }
            var pad = padState;
            var inputV = new Vec2(kx + pad.move.x, ky + pad.move.y);
            var p = player;
            steering = held.Count > 0 || pad.move.x != 0 || pad.move.y != 0;
            // Turbo: RT, or A held (at once in a vehicle; on foot a tap of A is a jump, and holding it on runs into a sprint).
            aHeldFor = pad.Held(Button.A) ? aHeldFor + dt : 0;
            bool turbo = shift || pad.rt > 0.3 || (pad.Held(Button.A) && (InCar || aHeldFor >= A_SPRINT_AFTER));
            if (p != null) p.sprinting = turbo;
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
                    car.Drive(throttle, Clamp1(inputV.x), dt, OpenFor(car), turbo, FloorAt);
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
                p.Update(back != 0 ? new Vec2(-ahead.x * back, -ahead.y * back) : (Vec2?)null, dt, world.model, open, world.solids, walkPaved);
                p.heading = heading;
            }
            else
            {
                var d = Orbit.ScreenToWorld(inputV, inputYaw);
                double k = Math.Min(1, Numbers.Hypot(inputV.x, inputV.y));
                p?.Update(d != null ? new Vec2(d.Value.x * k, d.Value.y * k) : (Vec2?)null, dt, world.model, open, world.solids, walkPaved);
            }
            UpdateDoors(dt);
            UpdateRoom();
            UpdateCompany(dt);
            if (authority)
            {
                foreach (var q in people) if (!q.following) q.Update(dt);
                // Traffic stops for you, everyone else and every car left at the kerb.
                var obstacles = cars.Select(c => new Vec2(c.car.x, c.car.y)).ToList();
                if (p != null && !InCar && room == null) obstacles.Add(new Vec2(p.x, p.y));
                foreach (var o in others) obstacles.Add(PositionOf(o.key) ?? new Vec2(o.x, o.y));
                obstacles.AddRange(othersParkedAt.Values);
                traffic?.Update(dt, obstacles);
            }
            else if (traffic != null)
            {
                // Your company is walked here, not by the host: keep them where this client has them.
                var mine = followers.Select(f => (f, f.x, f.y, f.heading, f.speed)).ToList();
                if (noticing != null) mine.Add((noticing, noticing.x, noticing.y, noticing.heading, 0));
                sync.Step(dt, traffic.cars, people);
                foreach (var (f, x, y, heading, speed) in mine)
                {
                    f.x = x;
                    f.y = y;
                    f.heading = heading;
                    f.speed = speed;
                }
                noticing?.Update(dt);
            }
            UpdateFind(dt);
            dusk += ((night ? 1 : 0) - dusk) * Math.Min(1, dt * 2.5);
            KeepInSight(dt);
        }

        void UpdateDoors(double dt)
        {
            var p = player;
            for (int i = 0; i < world.doors.Count; i++)
            {
                var d = world.doors[i];
                var at = d.at;
                bool near = p != null && !InCar && Math.Abs(p.x - at.x) < DOOR_REACH && Math.Abs(p.y - at.y) < DOOR_REACH && Numbers.Hypot(p.x - at.x, p.y - at.y) < DOOR_REACH;
                foreach (var o in DoorOpeners) near = near || Numbers.Hypot(o.x - at.x, o.y - at.y) < DOOR_REACH;
                double target = near ? 1 : 0;
                if (d.open == target) continue;
                d.open = target > d.open ? Math.Min(1, d.open + dt * DOOR_SPEED) : Math.Max(0, d.open - dt * DOOR_SPEED);
                world.SwingDoor(i);
            }
            ShowRooms();
        }

        /// <summary>Put keepers in the rooms there's a way to see into: the one you're in, any with a door open, and those close by on foot.</summary>
        void ShowRooms()
        {
            var p = player;
            wantedRooms.Clear();
            if (room != null) wantedRooms.Add(room.Value);
            foreach (var d in world.doors) if (d.open > 0) wantedRooms.Add(d.room);
            if (p != null && !InCar)
            {
                for (int i = 0; i < world.rooms.Count; i++)
                {
                    var r = world.rooms[i];
                    double dx = Math.Max(0, Math.Max(r.x0 - p.x, p.x - r.x1));
                    double dy = Math.Max(0, Math.Max(r.y0 - p.y, p.y - r.y1));
                    if (dx * dx + dy * dy < ROOM_NEAR * ROOM_NEAR) wantedRooms.Add(i);
                }
            }
            foreach (int id in shownRooms.Where(id => !wantedRooms.Contains(id)).ToList()) ShowRoom(id, false);
            foreach (int id in wantedRooms) ShowRoom(id, true);
            ShowInteriors();
        }

        /// <summary>Only the rooms near you are drawn: every one in the town at once was a lot of draw calls for windows too far off to see into.</summary>
        void ShowInteriors()
        {
            double vx = player != null ? player.x : focus.x, vy = player != null ? player.y : focus.z;
            for (int i = 0; i < world.rooms.Count; i++)
            {
                var r = world.rooms[i];
                double dx = Math.Max(0, Math.Max(r.x0 - vx, vx - r.x1));
                double dy = Math.Max(0, Math.Max(r.y0 - vy, vy - r.y1));
                bool on = wantedRooms.Contains(i) || dx * dx + dy * dy < INTERIOR_REACH * INTERIOR_REACH;
                var it = world.InteriorOf(i);
                if (it.Visible != on) it.Visible = on;
            }
        }

        void UpdateRoom()
        {
            var p = player;
            int? now = null;
            // At the wheel (or flying over a building) you're never in its rooms.
            if (p != null && !InCar)
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
                it.CastShadows(false);
            }
            var left = room;
            room = now;
            roomTurn = 0;
            if (left != null && now == null && mode == CameraMode.Iso)
            {
                SetStop(Orbit.NearestStop(yaw));
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
                world.InteriorOf(now.Value).CastShadows(true);
                ShowRoom(now.Value, true);
            }
        }

        void ShowRoom(int id, bool on)
        {
            if (on == shownRooms.Contains(id)) return;
            if (on) shownRooms.Add(id);
            else shownRooms.Remove(id);
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
            sightTimer -= dt;
            if (p == null)
            {
                hidden = false;
                return;
            }
            if (sightTimer <= 0)
            {
                sightTimer = 0.1;
                // Flying over the roofs, nothing's in the way.
                hidden = room == null && !(InCar && p.lift > 0) && Blocked(new Vec2(p.x, p.y), Orbit.StopYaw(CameraStop));
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
                c.view.Place(c.car.x, c.car.y, DetMath.Atan2(f.x, f.y), c.car.altitude, c.car.pitch, c.car.bank);
                c.view.Lights(dusk);
            }
            for (int k = 0; k < people.Count; k++)
            {
                var q = people[k];
                avatars[k].Visible = !(followersAboard && q.following);
                if (!avatars[k].Visible) continue;
                avatars[k].Update(q, dt);
                avatars[k].SetWaving(q.waving > 0);
            }
            remotes?.Update(dt, driving != null && p != null ? new Vec2(p.x, p.y) : (Vec2?)null, dusk);
            if (riding != null) FollowRide();
            fleet?.Update(dusk);
            foreach (var v in othersParked.Values) v.Lights(dusk);
            if (Attract) targetYaw += dt * 0.06;
            foreach (var k in keepers.Values) if (k.avatar.Visible) k.avatar.Update(k.walker, dt);

            Fixed? fixedView = room != null ? RoomView(room.Value) : (Fixed?)null;
            double yawTarget = fixedView?.yaw ?? targetYaw;
            double zoomTarget = fixedView?.zoom ?? (Attract ? ATTRACT_ZOOM : zoom);
            double pitchTarget = fixedView?.pitch ?? (Attract ? ATTRACT_PITCH : TargetPitch);
            // Behind the sign-in card the turn is steady, and goes on unbroken from town to town.
            yaw += Orbit.ShortestTurn(yaw, yawTarget) * (snap || Attract ? 1 : Orbit.EaseBy(5, dt));
            shownZoom += (zoomTarget - shownZoom) * (snap ? 1 : Math.Min(1, dt * (fixedView != null ? 5 : 10)));
            var target = fixedView?.focus
                ?? (Attract ? new Vector3((float)world.model.center.x, 0.3f, (float)world.model.center.y)
                : p != null ? new Vector3((float)p.x, 0.3f + (float)(InCar ? p.lift : 0), (float)p.y) : new Vector3((float)world.model.spawn.x, 0.3f, (float)world.model.spawn.y));
            focusFollow.Update(target.x, target.y, target.z, dt, snap);
            focus = new Vector3((float)focusFollow.ax, (float)focusFollow.ay, (float)focusFollow.az);
            pitch += (pitchTarget - pitch) * (snap ? 1 : Math.Min(1, dt * 6));
            if (mode == CameraMode.Close && p != null && fixedView == null) PlaceChase(p, dt);
            if (mode == CameraMode.Close && fixedView != null) PlaceChaseInRoom(fixedView.Value, dt);
            if (room != null) CutWalls(room.Value, dt);
            Vector3? roomLight = fixedView != null ? Coords.Map(fixedView.Value.focus.x, fixedView.Value.focus.z, world.rooms[room.Value].height * 0.9) : (Vector3?)null;
            snap = false;
            PlaceCamera();

            AnimateDayNight(roomLight);
            routeTimer -= dt;
            if (routeTimer <= 0)
            {
                DrawRoute();
                routeTimer = 0.2;
            }
            bool auto = IsAuto;
            overlay.DrawMinimap(p != null ? new Vec2(p.x, p.y) : (Vec2?)null, ViewQuad(), auto || FindTarget == null ? Goal : FindTarget, auto || FindTarget == null ? Route : findRoute);
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
                : it != null ? Directions.ForWalk(world.sidewalks, world.names, it) : findSteps;
            if (it != null && !it.joining) street = world.names.ofRoad[world.sidewalks.paths[it.start.path].road];
            else street = p != null && room == null ? StreetAt(new Vec2(p.x, p.y)) : null;
            string name = street != null ? world.names.streets[street.Value].name : null;
            overlay.SetHud(name, steps);
            bool pad = input.Connected;
            if (driving != null)
            {
                var car = cars[driving.Value].car;
                int kmh = Numbers.RoundInt((Math.Abs(car.speed) / Vehicle.TOP_SPEED) * 50);
                if (car.kind == VehicleKind.Plane)
                    overlay.SetPrompt(car.Airborne
                        ? pad ? $"Flying · {kmh} km/h · {Numbers.RoundInt(car.altitude * 4)} m up · RT climb, hold A boost, LT descend, stick banks · land to get out" : $"Flying · {kmh} km/h · {Numbers.RoundInt(car.altitude * 4)} m up · W climb, S descend, Shift boost, A/D bank · land to get out"
                        : pad ? $"Taxiing · {kmh} km/h · RT full throttle to take off, LT brake, stick steers · B to get out" : $"Taxiing · {kmh} km/h · hold W to take off, Shift boost, S brake, A/D steer · G to get out");
                else if (car.kind == VehicleKind.Boat)
                    overlay.SetPrompt(pad ? $"Sailing · {kmh} km/h · RT throttle, hold A boost, LT astern, stick steers · B to get out at a dock or the shore" : $"Sailing · {kmh} km/h · W/S throttle and astern, Shift boost, A/D steer · G to get out at a dock or the shore");
                else overlay.SetPrompt(autoDrive != null
                    ? pad ? $"Auto drive · {kmh} km/h · stick or triggers take the wheel · B to get out" : $"Auto drive · {kmh} km/h · arrows take the wheel · T stop · G to get out"
                    : pad ? $"Driving · {kmh} km/h · RT or hold A boost, LT brake, stick steers · B to get out" : $"Driving · {kmh} km/h · W/S speed and brake, Shift boost, A/D steer · T auto drive · G to get out");
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
                string key = pad ? "B" : "G", jump = pad ? "A" : "Space";
                if (pick != null) overlay.SetPrompt(pick.Value.driver != null ? $"Press {key} to ride with {pick.Value.driver.Value.name}" : $"Press {key} to get in the {NameOf(pick.Value)}");
                else if (FacedFollower() != null) overlay.SetPrompt($"Press {jump} to say goodbye");
                else if (noticing != null) overlay.SetPrompt($"Press {jump} to ask them along");
                else overlay.SetPrompt(null);
            }
            string over = PromptOverride?.Invoke();
            if (over != null) overlay.SetPrompt(over);
        }

        string NameOf(Pick pick)
        {
            int model = pick.kind == PickKind.Own ? cars[pick.i].car.model : pick.kind == PickKind.Parked ? world.parked[pick.i].model : traffic.cars[pick.i].model;
            return Catalog.KindOf(model) switch { VehicleKind.Boat => "boat", VehicleKind.Plane => "plane", _ => "car" };
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
            return best >= 0 && bestD < Layout.PARKING + Layout.SIDEWALK + 0.35 ? world.names.ofRoad[best] : (int?)null;
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
            double low = 0.12 / r.height;
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
            double tall = deep * Math.Sin(ROOM_PITCH) + r.height * Math.Cos(ROOM_PITCH);
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
            // Up with a plane in flight.
            double look = CHASE_LOOK_HEIGHT + (InCar ? p.lift : 0);
            foreach (var b in world.boxes) dist = Math.Min(dist, Math.Max(0.5, Orbit.RayBoxDistance(at, look, o, b) - 0.2));
            double flat = Numbers.Hypot(o.x, o.y);
            var ahead = new Vec2(-o.x / flat, -o.y / flat);
            lookFollow.Update(p.x + ahead.x * CHASE_LOOK_AHEAD, look, p.y + ahead.y * CHASE_LOOK_AHEAD, dt, snap);
            chaseLook = WorldOf(lookFollow.ax, lookFollow.ay, lookFollow.az);
            chasePos = WorldOf(p.x + o.x * dist, look + o.up * dist, p.y + o.y * dist);
        }

        void PlaceCamera()
        {
            camera.pixelRect = viewport;
            if (mode == CameraMode.Close)
            {
                camera.orthographic = false;
                camera.fieldOfView = (float)CHASE_FOV;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = (float)(FOG_FAR + 40);
                camera.transform.position = chasePos;
                camera.transform.rotation = Quaternion.LookRotation(chaseLook - chasePos, Vector3.up);
                return;
            }
            double half = VIEW_HEIGHT / 2 / shownZoom;
            camera.orthographic = true;
            camera.orthographicSize = (float)half;
            camera.nearClipPlane = 0.1f;
            // Zoomed far out and low (behind the sign-in card), the bottom of the view
            // reaches the ground half / tan(pitch) nearer than the middle and the top as
            // much further: stand back and see far enough for both.
            double reach = half / Math.Tan(Math.Max(0.1, pitch));
            double distance = Attract ? Math.Max(CAMERA_DISTANCE, reach + 20) : CAMERA_DISTANCE;
            camera.farClipPlane = (float)(Attract ? Math.Max(CAMERA_DISTANCE * 3, distance + reach + 60) : CAMERA_DISTANCE * 3);
            var o = Orbit.CameraOffset(yaw, pitch);
            var pos = WorldOf(focus.x + o.x * distance, focus.y + o.up * distance, focus.z + o.y * distance);
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
            sky.Update(dusk, night, mode == CameraMode.Close, FOG_NEAR, FOG_FAR, WorldOf(focus.x, 0, focus.z), world.lamps, lampLevels, roomLight);
            // Televisions: a cold blue flicker in a few windows, each group to its own programme.
            for (int g = 0; g < world.screens.Count; g++)
            {
                var r = world.screens[g];
                r.enabled = dusk > 0.3;
                if (!r.enabled) continue;
                double cut = Noise(t * 1.3 + g * 17) > 0.7 ? 0.45 : 1; // a scene change
                double level = (0.55 + 0.45 * Noise(t * 7 + g * 5.3)) * cut;
                var blue = Color.Lerp(Coords.Hex(0x5b8cff), Coords.Hex(0xb4d0ff), (float)Noise(t * 2.1 + g * 3.1));
                Materials.SetGlow(r, blue, (float)(dusk * level * 1.4));
            }
        }
    }
}

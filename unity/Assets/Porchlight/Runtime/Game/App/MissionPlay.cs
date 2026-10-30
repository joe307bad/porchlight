// A mission being played in the explore view: the town's side of its story
// (MissionScript runs the ink). Each role the story names ("sender", "drop")
// is a house: its front door, the pavement in front of it, its street, and
// once asked for, a person waiting there and a spot marked in the parking lane
// outside. The people walk their errands (in at the door, out with a box, to
// your car's kerb side; or the other way), boxes ride on your car's roof, and
// the story's waits are checked against all of it: you've talked to someone
// (walk or drive up to them, Enter or A), your car is stopped in a spot, an
// errand's done. Local to this player: no one else sees a mission's people.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Missions;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using Avatar = Porchlight.Rendering.Avatar;

namespace Porchlight.App
{
    public sealed class MissionPlay : IMissionWorld
    {
        /// <summary>How close you come to someone to talk: on foot, and from the driver's seat.</summary>
        const double TALK_REACH = 1.1;
        const double TALK_REACH_CAR = 1.9;
        /// <summary>Parked: the car's middle this close to the spot's, near enough stopped, and lined up with the kerb (either way round).</summary>
        const double PARK_REACH = 0.45;
        const double PARK_SPEED = 0.12;
        const double PARK_ANGLE = 0.45;
        /// <summary>How far your car may stand from its spot while boxes go in and out of it.</summary>
        const double CAR_STAYS = 0.8;
        const double WALK_SPEED = 1.0;
        /// <summary>How long someone is indoors fetching (or putting down) a box.</summary>
        const double INDOORS = 1.1;
        /// <summary>A box's side (tiles: about 40 cm).</summary>
        const double BOX = 0.1;
        /// <summary>The pavement in front of a house is no further than this from its door.</summary>
        const double KERB_REACH = 6;
        const double SPOT_REACH = 3.5;
        const int SPOT_COLOR = 0xff6a00;
        static readonly string BOX_MODEL = "furniture/cardboardBoxClosed";
        static readonly string[] FIRST_NAMES =
        {
            "Ada", "Bea", "Cal", "Dot", "Eli", "Fay", "Gus", "Hal", "Ivy", "Jo", "Kit", "Lou", "Mae", "Ned", "Oona", "Pip", "Ray", "Sam", "Tess", "Vi", "Wes", "Zara",
            "Ines", "Omar", "Priya", "Kenji", "Lena", "Mateo", "Nia", "Rosa", "Theo", "Yusuf",
        };

        enum Act { Walk, ToCar, Indoors, PickUp, Stow, TakeOut, PutDown, Vanish, Then }

        sealed class Step
        {
            public Act act;
            /// <summary>Walk: where to, in order.</summary>
            public List<Vec2> path;
            public Action then;
        }

        sealed class Person
        {
            public string name;
            public Walker walker;
            public Avatar avatar;
            /// <summary>The box in their arms, if any.</summary>
            public GameObject box;
            public readonly Queue<Step> steps = new Queue<Step>();
            public double timer;
            /// <summary>Gone in for good (dismissed): removed once indoors.</summary>
            public bool leaving;
        }

        sealed class Role
        {
            public int room;
            public Vec2 door, outward, kerb;
            public string street;
            public Person person;
            public string name;
            public Vehicle.Spot? spot;
            public GameObject mark;
            public bool loaded, unloaded;
            /// <summary>Spoken to (since the story last asked).</summary>
            public bool talked;
            public Vec2 Outside => new Vec2(door.x + outward.x * 0.35, door.y + outward.y * 0.35);
            public Vec2 Inside => new Vec2(door.x - outward.x * 0.4, door.y - outward.y * 0.4);
        }

        readonly ExploreView view;
        readonly ModelLibrary library;
        readonly GameInput input;
        readonly Rng rng;
        readonly Dictionary<string, Role> roles = new Dictionary<string, Role>();
        readonly List<Person> people = new List<Person>();
        readonly Palette palette = new Palette();
        static Material spotMaterial, beaconMaterial;
        GameObject cargoRoot;
        readonly List<GameObject> cargo = new List<GameObject>();
        GameObject beacon;
        int highlighted;

        public readonly string id;
        public readonly string title;
        public readonly MissionScript script;
        /// <summary>What to do next, as the story last said ("" for nothing).</summary>
        public string CurrentObjective { get; private set; } = "";
        /// <summary>Which of the conversation's choices the keys or pad are on.</summary>
        public int Highlighted => highlighted;

        public MissionPlay(string id, string title, string story, ExploreView view, ModelLibrary library, GameInput input)
        {
            this.id = id;
            this.title = title;
            this.view = view;
            this.library = library;
            this.input = input;
            rng = new Rng($"{id}::{DateTime.UtcNow.Ticks}");
            cargoRoot = new GameObject("mission cargo");
            cargoRoot.transform.SetParent(view.world.root.transform, false);
            view.PromptOverride = Prompt;
            view.OnPadA = TryTalk;
            script = new MissionScript(story, this);
        }

        /// <summary>Over: everyone it put in the town, its marks and its boxes go, and the view is as it was.</summary>
        public void Dispose()
        {
            foreach (var p in people) DisposePerson(p);
            people.Clear();
            foreach (var r in roles.Values) ClearMark(r);
            roles.Clear();
            if (cargoRoot != null) UnityEngine.Object.Destroy(cargoRoot);
            cargo.Clear();
            if (beacon != null) UnityEngine.Object.Destroy(beacon);
            view.Guide(null, null);
            view.DoorOpeners.Clear();
            view.InDialogue = false;
            if (view.PromptOverride == (Func<string>)Prompt) view.PromptOverride = null;
            if (view.OnPadA == (Func<bool>)TryTalk) view.OnPadA = null;
        }

        // The story's functions (IMissionWorld).

        public void Objective(string text) => CurrentObjective = text ?? "";

        public string House(string role, string near, double min, double max)
        {
            var world = view.world;
            Vec2 from = !string.IsNullOrEmpty(near) && roles.TryGetValue(near, out var other) ? other.door : view.PlayerPosition ?? world.model.spawn;
            var taken = new HashSet<int>(roles.Where(r => r.Key != role).Select(r => r.Value.room));
            // Homes with a front door, nearest the distance asked for first (any in range in a random order).
            var homes = new List<(int room, Door door, double miss)>();
            for (int i = 0; i < world.rooms.Count; i++)
            {
                var room = world.rooms[i];
                if (room.style != HouseStyle.House || taken.Contains(i)) continue;
                var doors = Interiors.Doors(room);
                if (doors.Count == 0) continue;
                var door = doors.FirstOrDefault(d => d.side == room.facing);
                if (door.width == 0) door = doors[0];
                double d = Geo.Dist(from, door.at);
                homes.Add((i, door, d < min ? min - d : d > max ? d - max : 0));
            }
            if (homes.Count == 0) throw new Exception("there are no houses in this town");
            var order = rng.Shuffle(homes).OrderBy(h => h.miss).ToList();
            foreach (var h in order.Take(40))
            {
                var r = RoleAt(h.room, h.door);
                if (r == null) continue;
                if (roles.TryGetValue(role, out var old)) Forget(old);
                roles[role] = r;
                return r.street;
            }
            throw new Exception("couldn't find a house by a street with room to park");
        }

        /// <summary>A house as a role: its door, the pavement in front, the street, and room to park outside (or null without).</summary>
        Role RoleAt(int room, Door door)
        {
            var world = view.world;
            var outside = new Vec2(door.at.x + door.outward.x * 0.35, door.at.y + door.outward.y * 0.35);
            SidewalkPath best = null;
            Vec2 kerb = default;
            double bestD = KERB_REACH;
            foreach (var path in world.sidewalks.paths)
            {
                var hit = Geo.ClosestOnPolyline(outside, path.points);
                if (hit.d >= bestD) continue;
                bestD = hit.d;
                best = path;
                kerb = hit.point;
            }
            if (best == null) return null;
            var r = new Role { room = room, door = door.at, outward = door.outward, kerb = kerb, street = StreetName(best.road) };
            r.spot = SpotFor(r);
            return r.spot == null ? null : r;
        }

        string StreetName(int road)
        {
            var names = view.world.names;
            int street = road >= 0 && road < names.ofRoad.Count ? names.ofRoad[road] : -1;
            return street >= 0 && street < names.streets.Count ? names.streets[street].name : "the street";
        }

        /// <summary>Room for a car in the parking lane just outside, clear of every other car and the other roles' spots.</summary>
        Vehicle.Spot? SpotFor(Role r)
        {
            var busy = view.CarsAbout();
            foreach (var other in roles.Values) if (other.spot != null) busy.Add(new Vec2(other.spot.Value.x, other.spot.Value.y));
            var spot = Vehicle.CarSpotNear(r.kerb, view.world.layout.roads, Layout.PARKING / 2, view.CanDriveAt, busy);
            if (spot == null || Geo.Dist(new Vec2(spot.Value.x, spot.Value.y), r.kerb) > SPOT_REACH) return null;
            return spot;
        }

        public string Npc(string role)
        {
            var r = RoleOf(role);
            if (r.person != null) GoIndoors(r.person);
            var taken = new HashSet<string>(roles.Values.Where(o => o != r).Select(o => o.name));
            var names = FIRST_NAMES.Where(n => !taken.Contains(n)).ToList();
            string look;
            do look = Catalog.RandomLook(rng); while (look == view.LookModel && Catalog.CHARACTERS.Length > 1);
            var walker = new Walker(look, r.kerb.x, r.kerb.y);
            // Facing the street, from their door.
            walker.heading = DetMath.Atan2(r.kerb.x - r.door.x, r.kerb.y - r.door.y);
            var avatar = new Avatar(library.Character(look), Avatar.PERSON_HEIGHT, rng.Next(), view.world.root.transform);
            avatar.SnapYaw(walker.heading);
            var p = new Person { name = rng.Pick(names.Count > 0 ? names : FIRST_NAMES.ToList()), walker = walker, avatar = avatar };
            people.Add(p);
            r.person = p;
            r.name = p.name;
            r.loaded = r.unloaded = r.talked = false;
            return p.name;
        }

        public void Guide(string role)
        {
            if (string.IsNullOrEmpty(role) || !roles.TryGetValue(role, out var r))
            {
                view.Guide(null, null);
                return;
            }
            string label = r.mark != null ? $"{r.name ?? "the house"}'s parking spot" : r.person != null ? r.name : $"the house on {r.street}";
            view.Guide(() =>
            {
                if (r.mark != null && r.spot != null) return new Vec2(r.spot.Value.x, r.spot.Value.y);
                if (r.person != null) return new Vec2(r.person.walker.x, r.person.walker.y);
                return r.kerb;
            }, label);
        }

        public void Parking(string role)
        {
            var r = RoleOf(role);
            if (r.spot == null) r.spot = SpotFor(r);
            if (r.spot == null) throw new Exception($"no room to park outside {role}'s house");
            ClearMark(r);
            r.mark = DrawSpot(r.spot.Value);
        }

        public void Load(string role, int boxes)
        {
            var r = RoleOf(role);
            var p = r.person ?? throw new Exception($"no one is at {role}'s house (call npc first)");
            r.loaded = false;
            for (int i = 0; i < Math.Max(1, boxes); i++)
            {
                Walk(p, r.kerb, r.Outside, r.Inside);
                p.steps.Enqueue(new Step { act = Act.Indoors });
                p.steps.Enqueue(new Step { act = Act.PickUp });
                Walk(p, r.Outside, r.kerb);
                p.steps.Enqueue(new Step { act = Act.ToCar });
                p.steps.Enqueue(new Step { act = Act.Stow });
            }
            Walk(p, r.kerb);
            p.steps.Enqueue(new Step { act = Act.Then, then = () => r.loaded = true });
        }

        public void Unload(string role)
        {
            var r = RoleOf(role);
            var p = r.person ?? throw new Exception($"no one is at {role}'s house (call npc first)");
            r.unloaded = false;
            for (int i = 0; i < cargo.Count; i++)
            {
                p.steps.Enqueue(new Step { act = Act.ToCar });
                p.steps.Enqueue(new Step { act = Act.TakeOut });
                Walk(p, r.kerb, r.Outside, r.Inside);
                p.steps.Enqueue(new Step { act = Act.Indoors });
                p.steps.Enqueue(new Step { act = Act.PutDown });
                Walk(p, r.Outside, r.kerb);
            }
            p.steps.Enqueue(new Step { act = Act.Then, then = () => r.unloaded = true });
        }

        public void Dismiss(string role)
        {
            if (!roles.TryGetValue(role, out var r)) return;
            ClearMark(r);
            if (r.person != null) GoIndoors(r.person);
            r.person = null;
        }

        public string NameOf(string role) => roles.TryGetValue(role, out var r) ? r.name : null;

        public bool Met(string wait, string role)
        {
            if (!roles.TryGetValue(role, out var r)) return false;
            switch (wait)
            {
                case "talk":
                    if (!r.talked) return false;
                    r.talked = false;
                    return true;
                case "park":
                    return Parked(r);
                case "loaded":
                    return r.loaded;
                case "unloaded":
                    return r.unloaded;
                default:
                    return false;
            }
        }

        Role RoleOf(string role) =>
            roles.TryGetValue(role ?? "", out var r) ? r : throw new Exception($"no house for '{role}' yet (call house first)");

        /// <summary>Your car stopped in the role's spot, lined up with the kerb.</summary>
        bool Parked(Role r)
        {
            var car = view.OwnVehicle;
            if (car == null || r.spot == null || car.kind != VehicleKind.Car) return false;
            var s = r.spot.Value;
            if (Numbers.Hypot(car.x - s.x, car.y - s.y) > PARK_REACH || Math.Abs(car.speed) > PARK_SPEED) return false;
            double off = Math.Abs(Orbit.ShortestTurn(s.heading, car.heading));
            return Math.Min(off, Math.PI - off) < PARK_ANGLE;
        }

        // People's errands.

        static void Walk(Person p, params Vec2[] points) => p.steps.Enqueue(new Step { act = Act.Walk, path = points.ToList() });

        void GoIndoors(Person p)
        {
            var r = roles.Values.FirstOrDefault(o => o.person == p);
            p.steps.Clear();
            p.leaving = true;
            if (p.box != null)
            {
                UnityEngine.Object.Destroy(p.box);
                p.box = null;
            }
            if (r != null) Walk(p, r.kerb, r.Outside, r.Inside);
            p.steps.Enqueue(new Step { act = Act.Vanish });
        }

        /// <summary>Where someone hands boxes into your car: at its kerb side, by the back door.</summary>
        Vec2? CarSide(Vec2 from)
        {
            var car = view.OwnVehicle;
            if (car == null) return null;
            var f = car.Forward;
            var side = new Vec2(-f.y, f.x);
            if (Geo.Dot(side, new Vec2(from.x - car.x, from.y - car.y)) < 0) side = new Vec2(-side.x, -side.y);
            return new Vec2(car.x + side.x * 0.42 - f.x * 0.22, car.y + side.y * 0.42 - f.y * 0.22);
        }

        void StepPerson(Person p, double dt)
        {
            var w = p.walker;
            if (p.steps.Count == 0)
            {
                w.Moved(0, 0, dt);
                // Waiting for you: turn to face you as you come near.
                var you = view.PlayerPosition;
                if (you != null && Geo.Dist(you.Value, new Vec2(w.x, w.y)) < 4) w.heading = DetMath.Atan2(you.Value.x - w.x, you.Value.y - w.y);
                return;
            }
            var step = p.steps.Peek();
            switch (step.act)
            {
                case Act.Walk:
                    if (WalkAlong(p, step.path, dt)) p.steps.Dequeue();
                    return;
                case Act.ToCar:
                {
                    // Only while your car is in its spot: otherwise they wait at the kerb.
                    var r = roles.Values.FirstOrDefault(o => o.person == p);
                    var car = view.OwnVehicle;
                    bool there = car != null && r?.spot != null && Numbers.Hypot(car.x - r.spot.Value.x, car.y - r.spot.Value.y) < CAR_STAYS && Math.Abs(car.speed) < PARK_SPEED * 2;
                    var side = there ? CarSide(r.kerb) : null;
                    if (side == null)
                    {
                        WalkAlong(p, new List<Vec2> { r?.kerb ?? new Vec2(w.x, w.y) }, dt);
                        return;
                    }
                    if (WalkAlong(p, new List<Vec2> { side.Value }, dt))
                    {
                        w.heading = DetMath.Atan2(car.x - w.x, car.y - w.y);
                        p.steps.Dequeue();
                    }
                    return;
                }
                case Act.Indoors:
                    w.Moved(0, 0, dt);
                    p.avatar.Visible = false;
                    p.timer += dt;
                    if (p.timer < INDOORS) return;
                    p.timer = 0;
                    p.avatar.Visible = true;
                    w.heading += Math.PI;
                    p.steps.Dequeue();
                    return;
                case Act.PickUp:
                    Carry(p);
                    p.steps.Dequeue();
                    return;
                case Act.Stow:
                    if (p.box != null)
                    {
                        UnityEngine.Object.Destroy(p.box);
                        p.box = null;
                    }
                    AddCargo();
                    p.steps.Dequeue();
                    return;
                case Act.TakeOut:
                    if (cargo.Count > 0)
                    {
                        UnityEngine.Object.Destroy(cargo[cargo.Count - 1]);
                        cargo.RemoveAt(cargo.Count - 1);
                        Carry(p);
                    }
                    p.steps.Dequeue();
                    return;
                case Act.PutDown:
                    if (p.box != null)
                    {
                        UnityEngine.Object.Destroy(p.box);
                        p.box = null;
                    }
                    p.steps.Dequeue();
                    return;
                case Act.Vanish:
                    p.avatar.Visible = false;
                    p.steps.Dequeue();
                    return;
                case Act.Then:
                    p.steps.Dequeue();
                    step.then?.Invoke();
                    return;
            }
        }

        /// <summary>On along the points (dropping each as it's reached); true once at the last.</summary>
        bool WalkAlong(Person p, List<Vec2> path, double dt)
        {
            var w = p.walker;
            double budget = WALK_SPEED * dt;
            double moved = 0, mx = 0, my = 0;
            while (path.Count > 0 && budget > 1e-9)
            {
                var to = path[0];
                double dx = to.x - w.x, dy = to.y - w.y, d = Numbers.Hypot(dx, dy);
                if (d <= budget)
                {
                    w.x = to.x;
                    w.y = to.y;
                    budget -= d;
                    moved += d;
                    mx += dx;
                    my += dy;
                    path.RemoveAt(0);
                    continue;
                }
                w.x += dx / d * budget;
                w.y += dy / d * budget;
                mx += dx / d * budget;
                my += dy / d * budget;
                moved += budget;
                budget = 0;
            }
            if (moved > 1e-9) w.Moved(mx, my, dt);
            else w.Moved(0, 0, dt);
            return path.Count == 0;
        }

        ModelLibrary.Model Box => library.Get(BOX_MODEL);

        GameObject PlaceBox(Transform parent)
        {
            var m = Box;
            double s = BOX / Math.Max(m.size.x, m.size.z);
            return Props.Place(m, parent, 0, 0, 0, s, s, s);
        }

        /// <summary>A box in their arms, held out in front at chest height.</summary>
        void Carry(Person p)
        {
            if (p.box != null) return;
            p.box = PlaceBox(p.avatar.root.transform);
            p.box.transform.localPosition = new Vector3(0, (float)(Avatar.PERSON_HEIGHT * 0.42), 0.075f);
        }

        /// <summary>A box onto your car's roof rack: rows of three along the roof, then a layer up.</summary>
        void AddCargo()
        {
            var box = PlaceBox(cargoRoot.transform);
            cargo.Add(box);
            PlaceCargo();
        }

        void PlaceCargo()
        {
            var car = view.OwnVehicle;
            bool show = car != null && car.kind == VehicleKind.Car && cargo.Count > 0;
            cargoRoot.SetActive(show);
            if (!show) return;
            var model = library.Get(Catalog.CARS[car.model]);
            double roof = model.size.y * CarView.ScaleOf(model) * 0.92;
            var f = car.Forward;
            cargoRoot.transform.localPosition = Coords.Map(car.x, car.y, roof + car.altitude);
            cargoRoot.transform.localRotation = Coords.Yaw(DetMath.Atan2(f.x, f.y));
            for (int i = 0; i < cargo.Count; i++)
            {
                int row = i % 3, layer = i / 3;
                cargo[i].transform.localPosition = new Vector3(0, (float)(layer * BOX), (float)((row - 1) * BOX * 1.15));
                cargo[i].transform.localRotation = Quaternion.Euler(0, (i * 37) % 12 - 6, 0);
            }
        }

        // Marks: the spot to park in, and a light over whoever's waiting to talk.

        GameObject DrawSpot(Vehicle.Spot s)
        {
            var b = new MeshBuilder(palette);
            var f = new Vec2(Math.Cos(s.heading), Math.Sin(s.heading));
            var side = new Vec2(-f.y, f.x);
            double hl = TownWorld.CAR_LENGTH * 0.62, hw = 0.3;
            Vec2 Corner(double a, double c) => new Vec2(s.x + f.x * a * hl + side.x * c * hw, s.y + f.y * a * hl + side.y * c * hw);
            var ring = new List<Vec2> { Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1), Corner(-1, -1) };
            foreach (var p in Geo.SamplePolyline(ring, 0.11)) b.Disc(p, 0.04, 0.03, SPOT_COLOR, 8);
            b.Disc(new Vec2(s.x, s.y), 0.12, 0.028, SPOT_COLOR, 16);
            spotMaterial ??= Materials.Unlit(Coords.Hex(SPOT_COLOR));
            var go = new GameObject("mission spot");
            go.transform.SetParent(view.world.root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = b.Build("mission spot");
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = spotMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        static void ClearMark(Role r)
        {
            if (r.mark == null) return;
            UnityEngine.Object.Destroy(r.mark.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.Destroy(r.mark);
            r.mark = null;
        }

        void Forget(Role r)
        {
            ClearMark(r);
            if (r.person != null) GoIndoors(r.person);
        }

        void DisposePerson(Person p)
        {
            if (p.box != null) UnityEngine.Object.Destroy(p.box);
            p.avatar.Dispose();
        }

        void UpdateBeacon(Role waiting, double dt)
        {
            if (waiting?.person == null || !waiting.person.avatar.Visible)
            {
                if (beacon != null) beacon.SetActive(false);
                return;
            }
            if (beacon == null)
            {
                beacon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(beacon.GetComponent<Collider>());
                beacon.name = "mission beacon";
                beacon.transform.SetParent(view.world.root.transform, false);
                beacon.transform.localScale = Vector3.one * 0.07f;
                beaconMaterial ??= Materials.Glowing(Coords.Hex(SPOT_COLOR));
                var r = beacon.GetComponent<Renderer>();
                r.sharedMaterial = beaconMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            beacon.SetActive(true);
            var w = waiting.person.walker;
            double bob = Math.Sin(Time.realtimeSinceStartupAsDouble * 3) * 0.025;
            beacon.transform.localPosition = Coords.Map(w.x, w.y, Avatar.PERSON_HEIGHT + 0.13 + bob);
        }

        // Talking: walk (or drive) up to someone the story is waiting for you to talk to.

        /// <summary>The role you're close enough to talk to, that the story wants you to.</summary>
        Role TalkTarget()
        {
            var you = view.PlayerPosition;
            if (you == null || view.InDialogue || view.InRoom) return null;
            double reach = view.InCar ? TALK_REACH_CAR : TALK_REACH;
            foreach (var w in script.Waits)
            {
                if (w.kind != "talk" || !roles.TryGetValue(w.role, out var r) || r.person == null || r.person.steps.Count > 0) continue;
                if (Geo.Dist(you.Value, new Vec2(r.person.walker.x, r.person.walker.y)) < reach) return r;
            }
            return null;
        }

        Role WaitingToTalk()
        {
            foreach (var w in script.Waits)
                if (w.kind == "talk" && roles.TryGetValue(w.role, out var r)) return r;
            return null;
        }

        /// <summary>Talk to whoever's in reach (Enter, or the pad's A): true if there was someone.</summary>
        public bool TryTalk()
        {
            var r = TalkTarget();
            if (r == null) return false;
            r.talked = true;
            var you = view.PlayerPosition.Value;
            r.person.walker.heading = DetMath.Atan2(you.x - r.person.walker.x, you.y - r.person.walker.y);
            return true;
        }

        string Prompt()
        {
            var pad = input.Connected;
            var r = TalkTarget();
            if (r != null) return $"Press {(pad ? "A" : "Enter")} to talk to {r.name}";
            foreach (var w in script.Waits)
            {
                if (w.kind != "park" || !roles.TryGetValue(w.role, out var spot) || spot.spot == null) continue;
                if (view.OwnVehicle == null) return "You'll need a car: click the Car box by the minimap";
                var you = view.PlayerPosition;
                var s = spot.spot.Value;
                if (you != null && Geo.Dist(you.Value, new Vec2(s.x, s.y)) < 3 && view.DrivingMyCar && !Parked(spot))
                    return "Pull into the marked spot and stop, lined up with the kerb";
            }
            return null;
        }

        // Each frame.

        /// <summary>Keys for the conversation (and Enter to start one): true if used.</summary>
        public bool OnKey(Key code)
        {
            bool ok = code == Key.Enter || code == Key.NumpadEnter || code == Key.Space;
            if (script.Line == null && script.Choices.Count == 0) return code != Key.Space && ok && TryTalk();
            if (script.Line != null)
            {
                if (ok) script.Advance();
                return true;
            }
            int n = script.Choices.Count;
            if (code == Key.UpArrow || code == Key.W) highlighted = (highlighted + n - 1) % n;
            else if (code == Key.DownArrow || code == Key.S) highlighted = (highlighted + 1) % n;
            else if (ok) Choose(highlighted);
            else if (code >= Key.Digit1 && code <= Key.Digit9) Choose(code - Key.Digit1);
            return true;
        }

        public void Advance() => script.Advance();

        public void Choose(int i)
        {
            highlighted = 0;
            script.Choose(i);
        }

        public void Update(double dt)
        {
            dt = Math.Min(dt, 0.1);
            // The conversation, from the pad (ExploreView leaves it the pad while it's on).
            var pad = input.Pad;
            if (script.Line != null && pad.Pressed(Button.A)) script.Advance();
            else if (script.Choices.Count > 0)
            {
                int n = script.Choices.Count;
                if (pad.Pressed(Button.Up)) highlighted = (highlighted + n - 1) % n;
                if (pad.Pressed(Button.Down)) highlighted = (highlighted + 1) % n;
                if (pad.Pressed(Button.A)) Choose(highlighted);
            }
            highlighted = Math.Min(highlighted, Math.Max(0, script.Choices.Count - 1));
            script.Update(dt);
            view.InDialogue = !script.Done && (script.Line != null || script.Choices.Count > 0);

            view.DoorOpeners.Clear();
            for (int i = people.Count - 1; i >= 0; i--)
            {
                var p = people[i];
                StepPerson(p, dt);
                p.avatar.Update(p.walker, dt);
                if (p.leaving && p.steps.Count == 0)
                {
                    DisposePerson(p);
                    people.RemoveAt(i);
                    continue;
                }
                view.DoorOpeners.Add(new Vec2(p.walker.x, p.walker.y));
            }
            PlaceCargo();
            UpdateBeacon(WaitingToTalk(), dt);
        }
    }
}

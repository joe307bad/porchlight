// A mission being played in the explore view: the town's side of its story
// (MissionScript runs the ink). It knows no mission: it carries out the
// story's building blocks, and the story puts them together. Each role the
// story names ("sender", "drop") is a building: its front door, the pavement
// in front of it, its street, and once asked for, a person waiting there and
// a spot marked in the parking lane outside. The story queues a person's
// errands a step at a time (walk inside, to the kerb or to your car; wait;
// hold something; put it on your car or take it off; put it down), and the
// waits are checked against all of it: you've talked to someone (walk or
// drive up to them, Enter or A), your car is stopped in a spot, someone's
// errands are done, you're in or out of a role's grounds. A role can be the
// zoo rather than a building (its gate its door). A mission can make you look
// like someone (or something) else and put you somewhere; a chase is a role
// too, cars with someone at the wheel that drive after you (Chaser) until
// they've been close enough for long enough; and a countdown runs while
// you're where it says. Local to this player: no one else sees a mission's
// people, or what it's made you.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Missions;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.InputSystem;
using Avatar = Porchlight.Rendering.Avatar;

namespace Porchlight.App
{
    public sealed class MissionPlay : IMissionWorld
    {
        /// <summary>The pavement in front of a building is no further than this from its door.</summary>
        const double KERB_REACH = 6;
        const double SPOT_REACH = 3.5;
        /// <summary>How near you come to a role for its "near" hints to show.</summary>
        const double HINT_REACH = 3;
        /// <summary>With no spot marked, how much further from the kerb your car may stand for someone to walk to it.</summary>
        const double KERB_CAR_EXTRA = 1.5;
        const int SPOT_COLOR = 0xff6a00;

        /// <summary>Where a person can walk to, in the order they pass them.</summary>
        enum Where { Inside, Door, Kerb, Car }

        enum Act { Walk, Pause, Hold, PutInCar, TakeFromCar, PutDown, Vanish }

        sealed class Step
        {
            public Act act;
            /// <summary>Walk: where to.</summary>
            public Where to;
            /// <summary>Walk: the points still to go (worked out as the walk starts).</summary>
            public List<Vec2> path;
            /// <summary>Pause: how long.</summary>
            public double seconds;
            /// <summary>Hold: what, and how big.</summary>
            public string model;
            public double size;
        }

        /// <summary>A thing someone carries, or that rides on your car.</summary>
        sealed class Item
        {
            public string model;
            public double size;
            public GameObject go;
        }

        sealed class Person
        {
            public string name;
            public Role role;
            public Walker walker;
            public Avatar avatar;
            /// <summary>Where they last got to.</summary>
            public Where at = Where.Kerb;
            /// <summary>What's in their arms, if anything.</summary>
            public Item item;
            public readonly Queue<Step> steps = new Queue<Step>();
            public double timer;
            /// <summary>Gone in for good (left): removed once indoors.</summary>
            public bool leaving;
        }

        sealed class Role
        {
            /// <summary>Its building (-1: the zoo).</summary>
            public int room;
            /// <summary>Where "inside" is, for a role that isn't a building (else just in at the door).</summary>
            public Vec2? within;
            /// <summary>Whether a point is on its grounds (in its building's room, or the zoo).</summary>
            public Func<Vec2, bool> holds;
            public Vec2 door, outward, kerb;
            public string street;
            public Person person;
            public string name;
            public Vehicle.Spot? spot;
            public GameObject mark;
            /// <summary>Spoken to (since the story last asked).</summary>
            public bool talked;
            public Vec2 Outside => new Vec2(door.x + outward.x * 0.35, door.y + outward.y * 0.35);
            public Vec2 Inside => within ?? new Vec2(door.x - outward.x * 0.4, door.y - outward.y * 0.4);
            public bool Marked => mark != null && spot != null;
            public Vec2 Spot => new Vec2(spot.Value.x, spot.Value.y);
        }

        sealed class HintLine
        {
            public string when, role, text;
        }

        /// <summary>One of a chase's cars, and who's at its wheel (out beside it once it's pulled up by you).</summary>
        sealed class Cop
        {
            public Chaser chaser;
            public CarView view;
            public Renderer[] siren;
            public Avatar officer;
            public Walker walker;
        }

        sealed class Pursuit
        {
            public string name;
            public readonly List<Cop> cops = new List<Cop>();
            /// <summary>How long they've had you (one of them close enough) without a break.</summary>
            public double caught;
        }

        sealed class Clock
        {
            public double left;
            public string label, when, role;
            public bool running;
        }

        static readonly Color SirenRed = Coords.Hex(0xff2a2a);
        static readonly Color SirenBlue = Coords.Hex(0x2a6bff);

        readonly ExploreView view;
        readonly ModelLibrary library;
        readonly GameInput input;
        readonly Rng rng;
        readonly Dictionary<string, Role> roles = new Dictionary<string, Role>();
        readonly List<Person> people = new List<Person>();
        readonly List<HintLine> hints = new List<HintLine>();
        /// <summary>What a mission can tune with setting(name, value), and what each is until it does.</summary>
        readonly Dictionary<string, double> settings = new Dictionary<string, double>
        {
            // How close you come to someone to talk: on foot, and from the driver's seat.
            ["talk_reach"] = 1.1,
            ["talk_reach_car"] = 1.9,
            // Parked: the car's middle this close to the spot's, near enough stopped, and lined up with the kerb (either way round).
            ["park_reach"] = 0.45,
            ["park_speed"] = 0.12,
            ["park_angle"] = 0.45,
            // How far your car may stand from a role's spot for their person to walk to it.
            ["car_reach"] = 0.8,
            ["walk_speed"] = 1.0,
            // How fast you go on foot, against your usual.
            ["your_pace"] = 1.0,
            // A chase: how fast its cars go after you on foot, and while you drive (past a car's top speed, they use its boost).
            ["chase_speed"] = 4.4,
            ["chase_speed_car"] = 6.4,
            // How close one must come (tiles from the car's side to you), and for how long (seconds), to have you.
            ["catch_reach"] = 0.45,
            ["catch_time"] = 1.0,
        };
        readonly Dictionary<string, Pursuit> chases = new Dictionary<string, Pursuit>();
        Clock clock;
        readonly Palette palette = new Palette();
        static Material spotMaterial, beaconMaterial, redMaterial, blueMaterial;
        GameObject cargoRoot;
        readonly List<Item> cargo = new List<Item>();
        GameObject beacon;
        int highlighted;

        public readonly string id;
        public readonly string title;
        public readonly MissionScript script;
        string objective = "";

        /// <summary>What to do next, as the story last said ("" for nothing), and how long's left on the countdown.</summary>
        public string CurrentObjective
        {
            get
            {
                if (clock == null) return objective;
                int s = (int)Math.Ceiling(clock.left);
                string time = $"{clock.label} {s / 60}:{s % 60:00}{(clock.running ? "" : " (stopped)")}";
                return objective.Length == 0 ? time : $"{objective} · {time}";
            }
        }
        /// <summary>Which of the conversation's choices the keys or pad are on.</summary>
        public int Highlighted => highlighted;
        /// <summary>How many cars are after you.</summary>
        public int Chasing => chases.Values.Sum(c => c.cops.Count);

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

        /// <summary>Over: everyone it put in the town, its marks and what rides on your car go, and the view is as it was.</summary>
        public void Dispose()
        {
            foreach (var p in people) DisposePerson(p);
            people.Clear();
            foreach (var r in roles.Values) ClearMark(r);
            roles.Clear();
            foreach (var c in chases.Values) CallOff(c);
            chases.Clear();
            clock = null;
            view.Obstacles.Clear();
            view.LookOverride = null;
            view.YourPace = 1;
            hints.Clear();
            if (cargoRoot != null) UnityEngine.Object.Destroy(cargoRoot);
            cargo.Clear();
            if (beacon != null) UnityEngine.Object.Destroy(beacon);
            view.Guide(null, null);
            view.DoorOpeners.Clear();
            view.InDialogue = false;
            if (view.PromptOverride == (Func<string>)Prompt) view.PromptOverride = null;
            if (view.OnPadA == (Func<bool>)TryTalk) view.OnPadA = null;
        }

        // The story's building blocks (IMissionWorld).

        public void Objective(string text) => objective = text ?? "";

        public void Hint(string when, string role, string text)
        {
            if (when != "always" && when != "no_car" && when != "near" && when != "near_driving")
                throw new Exception($"no such condition '{when}' (always, no_car, near or near_driving)");
            if (string.IsNullOrEmpty(text)) return;
            hints.Add(new HintLine { when = when, role = role ?? "", text = text });
        }

        public void ClearHints() => hints.Clear();

        public void Setting(string name, double value)
        {
            if (!settings.ContainsKey(name ?? "")) throw new Exception($"no such setting '{name}' ({string.Join(", ", settings.Keys)})");
            settings[name] = value;
            if (name == "your_pace") view.YourPace = Math.Max(0.1, value);
        }

        double S(string name) => settings[name];

        public string Place(string role, string kind, string near, double min, double max, bool parking)
        {
            if (string.IsNullOrEmpty(role)) throw new Exception("a role needs a name");
            if (kind == "zoo") return PlaceZoo(role, parking);
            HouseStyle? style = null;
            if (!string.IsNullOrEmpty(kind))
            {
                if (!Enum.TryParse(kind, true, out HouseStyle s) || !Enum.IsDefined(typeof(HouseStyle), s))
                    throw new Exception($"no such kind of building '{kind}' ({string.Join(", ", Enum.GetNames(typeof(HouseStyle)).Select(n => n.ToLowerInvariant()))}, or zoo)");
                style = s;
            }
            string what = string.IsNullOrEmpty(kind) ? "building" : kind.ToLowerInvariant();
            var world = view.world;
            Vec2 from = !string.IsNullOrEmpty(near) && roles.TryGetValue(near, out var other) ? other.door : view.PlayerPosition ?? world.model.spawn;
            var taken = new HashSet<int>(roles.Where(r => r.Key != role).Select(r => r.Value.room));
            // Buildings with a front door, nearest the distance asked for first (any in range in a random order).
            var found = new List<(int room, Door door, double miss)>();
            for (int i = 0; i < world.rooms.Count; i++)
            {
                var room = world.rooms[i];
                if ((style != null && room.style != style.Value) || taken.Contains(i)) continue;
                var doors = Interiors.Doors(room);
                if (doors.Count == 0) continue;
                var door = doors.FirstOrDefault(d => d.side == room.facing);
                if (door.width == 0) door = doors[0];
                double d = Geo.Dist(from, door.at);
                found.Add((i, door, d < min ? min - d : d > max ? d - max : 0));
            }
            if (found.Count == 0) throw new Exception($"there's no {what} in this town");
            var order = rng.Shuffle(found).OrderBy(h => h.miss).ToList();
            foreach (var h in order.Take(40))
            {
                var r = RoleAt(h.room, h.door, parking);
                if (r == null) continue;
                if (roles.TryGetValue(role, out var old)) Forget(old);
                roles[role] = r;
                return r.street;
            }
            throw new Exception(parking ? $"couldn't find a {what} by a street with room to park" : $"couldn't find a {what} by a street");
        }

        /// <summary>A building as a role: its door, the pavement in front, the street, and if asked, room to park outside (or null without).</summary>
        Role RoleAt(int room, Door door, bool parking)
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
            var inRoom = world.rooms[room];
            r.holds = p => Interiors.InRoom(inRoom, p);
            if (!parking) return r;
            r.spot = SpotFor(r);
            return r.spot == null ? null : r;
        }

        /// <summary>The zoo as a role: its gate its door, the path just inside by the penguins its "inside", its fence round its grounds.</summary>
        string PlaceZoo(string role, bool parking)
        {
            var z = view.world.model.zoo ?? throw new Exception("there's no zoo in this town");
            var gate = Menagerie.Gate(z);
            var outward = Menagerie.AlongV(z);
            var r = new Role { room = -1, door = gate, outward = outward, within = Menagerie.Visitor(z), holds = p => Menagerie.Inside(z, p), street = "the zoo" };
            // The pavement nearest the gate, else just outside it.
            r.kerb = new Vec2(gate.x + outward.x * 1.2, gate.y + outward.y * 1.2);
            double bestD = KERB_REACH;
            foreach (var path in view.world.sidewalks.paths)
            {
                var hit = Geo.ClosestOnPolyline(r.Outside, path.points);
                if (hit.d >= bestD) continue;
                bestD = hit.d;
                r.kerb = hit.point;
                r.street = StreetName(path.road);
            }
            if (parking)
            {
                r.spot = SpotFor(r);
                if (r.spot == null) throw new Exception("no room to park outside the zoo");
            }
            if (roles.TryGetValue(role, out var old)) Forget(old);
            roles[role] = r;
            return r.street;
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
            foreach (var other in roles.Values) if (other != r && other.spot != null) busy.Add(other.Spot);
            var spot = Vehicle.CarSpotNear(r.kerb, view.world.layout.roads, Layout.PARKING / 2, view.CanDriveAt, busy);
            if (spot == null || Geo.Dist(new Vec2(spot.Value.x, spot.Value.y), r.kerb) > SPOT_REACH) return null;
            return spot;
        }

        public string Spawn(string role, string name, string look)
        {
            var r = RoleOf(role);
            if (r.person != null) GoIndoors(r.person);
            if (string.IsNullOrEmpty(look))
            {
                do look = Catalog.RandomLook(rng); while (look == view.LookModel && Catalog.CHARACTERS.Length > 1);
            }
            else if (!Catalog.IsLook(look)) throw new Exception($"no such look '{look}'");
            var walker = new Walker(look, r.kerb.x, r.kerb.y);
            // Facing the street, from their door.
            walker.heading = DetMath.Atan2(r.kerb.x - r.door.x, r.kerb.y - r.door.y);
            var avatar = new Avatar(library.Character(look), Avatar.HeightOf(look), rng.Next(), view.world.root.transform);
            avatar.SnapYaw(walker.heading);
            var p = new Person { name = string.IsNullOrEmpty(name) ? "Someone" : name, role = r, walker = walker, avatar = avatar };
            people.Add(p);
            r.person = p;
            r.name = p.name;
            r.talked = false;
            return p.name;
        }

        public void Walk(string role, string to) => PersonOf(role).steps.Enqueue(new Step { act = Act.Walk, to = WhereOf(to) });

        public void Pause(string role, double seconds) => PersonOf(role).steps.Enqueue(new Step { act = Act.Pause, seconds = Math.Max(0, seconds) });

        public void Hold(string role, string model, double size)
        {
            var p = PersonOf(role);
            if (string.IsNullOrEmpty(model)) throw new Exception("hold what? (a model's name)");
            library.Get(model);
            if (size <= 0) throw new Exception("a thing to hold needs a size (tiles across)");
            p.steps.Enqueue(new Step { act = Act.Hold, model = model, size = size });
        }

        public void PutInCar(string role) => PersonOf(role).steps.Enqueue(new Step { act = Act.PutInCar });

        public void TakeFromCar(string role) => PersonOf(role).steps.Enqueue(new Step { act = Act.TakeFromCar });

        public void PutDown(string role) => PersonOf(role).steps.Enqueue(new Step { act = Act.PutDown });

        public void Leave(string role)
        {
            if (chases.TryGetValue(role ?? "", out var c)) CallOff(c);
            if (!roles.TryGetValue(role ?? "", out var r) || r.person == null) return;
            GoIndoors(r.person);
            r.person = null;
        }

        public void MarkSpot(string role)
        {
            var r = RoleOf(role);
            if (r.spot == null) r.spot = SpotFor(r);
            if (r.spot == null) throw new Exception($"no room to park outside {role}'s (place it with parking true)");
            ClearMark(r);
            r.mark = DrawSpot(r.spot.Value);
        }

        public void ClearSpot(string role)
        {
            if (roles.TryGetValue(role ?? "", out var r)) ClearMark(r);
        }

        public void Guide(string role, string to, string label)
        {
            if (string.IsNullOrEmpty(role))
            {
                view.Guide(null, null);
                return;
            }
            var r = RoleOf(role);
            Func<Vec2?> target;
            switch (to)
            {
                case "spot":
                    target = () => r.Marked ? r.Spot : r.kerb;
                    break;
                case "person":
                    target = () => r.person != null ? new Vec2(r.person.walker.x, r.person.walker.y) : r.kerb;
                    break;
                case "house":
                    target = () => r.kerb;
                    break;
                default:
                    throw new Exception($"guide to what? '{to}' (spot, person or house)");
            }
            view.Guide(target, string.IsNullOrEmpty(label) ? r.street : label);
        }

        public string NameOf(string role) => roles.TryGetValue(role, out var r) ? r.name : chases.TryGetValue(role, out var c) ? c.name : null;

        public void Look(string model)
        {
            if (string.IsNullOrEmpty(model))
            {
                view.LookOverride = null;
                return;
            }
            if (!Catalog.IsLook(model)) throw new Exception($"no such look '{model}'");
            view.LookOverride = model;
        }

        public void PutYou(string role, string at)
        {
            var r = RoleOf(role);
            var to = WhereOf(at);
            if (to == Where.Car) throw new Exception("put you where? 'car' (inside, door or kerb)");
            // Facing out, the way you'd go.
            view.PutPlayer(PointOf(r, to), DetMath.Atan2(r.outward.x, r.outward.y));
        }

        public string Chase(string role, string name, int count, string car, string look)
        {
            if (string.IsNullOrEmpty(role)) throw new Exception("a chase needs a role's name");
            if (roles.ContainsKey(role)) throw new Exception($"'{role}' is already a building's role");
            int model = Array.IndexOf(Catalog.CARS, car ?? "");
            if (model < 0 || !Catalog.IsCar(model)) throw new Exception($"no such car '{car}' (cars/police, cars/sedan, ...)");
            if (string.IsNullOrEmpty(look)) look = Catalog.RandomLook(rng);
            else if (!Catalog.IsLook(look)) throw new Exception($"no such look '{look}'");
            if (count < 1 || count > 8) throw new Exception("a chase is 1 to 8 cars");
            var you = view.PlayerPosition ?? throw new Exception("you're not in the town");
            if (chases.TryGetValue(role, out var old)) CallOff(old);
            var c = new Pursuit { name = string.IsNullOrEmpty(name) ? "Someone" : name };
            chases[role] = c;
            var busy = view.CarsAbout();
            for (int i = 0; i < count; i++)
            {
                var spot = StartFor(you, busy);
                if (spot == null) break;
                busy.Add(new Vec2(spot.Value.x, spot.Value.y));
                c.cops.Add(NewCop(model, spot.Value, look));
            }
            if (c.cops.Count == 0) throw new Exception("no street near you for a chase to start from");
            return c.name;
        }

        /// <summary>Somewhere for a chase's car to start (or start again): in a parking lane out of the way, else nearer, else further.</summary>
        Vehicle.Spot? StartFor(Vec2 you, List<Vec2> busy)
        {
            var roads = view.world.layout.roads;
            return Chaser.StartNear(you, 9, 16, roads, view.CanDriveAt, busy, rng)
                ?? Chaser.StartNear(you, 5, 24, roads, view.CanDriveAt, busy, rng);
        }

        Cop NewCop(int model, Vehicle.Spot at, string look)
        {
            var m = library.Get(Catalog.CARS[model]);
            var cop = new Cop
            {
                chaser = new Chaser(new Vehicle(model, at.x, at.y, at.heading)),
                view = new CarView(m, view.world.root.transform) { model = model },
                officer = new Avatar(library.Character(look), Avatar.HeightOf(look), rng.Next(), view.world.root.transform),
                walker = new Walker(look, at.x, at.y),
            };
            cop.officer.Visible = false;
            // A light bar on the roof: red and blue, flashing.
            double scale = CarView.ScaleOf(m);
            cop.siren = new Renderer[2];
            for (int k = 0; k < 2; k++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                go.name = "siren";
                go.transform.SetParent(cop.view.root.transform, false);
                go.transform.localPosition = new Vector3((k == 0 ? -1 : 1) * m.size.x * 0.17f, m.size.y * 0.98f, 0);
                go.transform.localScale = Vector3.one * (float)(0.07 / scale);
                var r = go.GetComponent<Renderer>();
                redMaterial ??= Materials.Glowing(SirenRed);
                blueMaterial ??= Materials.Glowing(SirenBlue);
                r.sharedMaterial = k == 0 ? redMaterial : blueMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                cop.siren[k] = r;
            }
            PlaceCop(cop);
            return cop;
        }

        static void PlaceCop(Cop cop)
        {
            var car = cop.chaser.car;
            var f = car.Forward;
            cop.view.Place(car.x, car.y, DetMath.Atan2(f.x, f.y));
        }

        /// <summary>The chase is over: its cars and who was in them go (their name still voices the role's lines).</summary>
        void CallOff(Pursuit c)
        {
            foreach (var cop in c.cops)
            {
                cop.view.Dispose();
                cop.officer.Dispose();
            }
            c.cops.Clear();
            c.caught = 0;
        }

        public void Countdown(double seconds, string label, string when, string role)
        {
            if (seconds <= 0)
            {
                clock = null;
                return;
            }
            if (when != "always" && when != "inside" && when != "outside")
                throw new Exception($"no such condition '{when}' (always, inside or outside)");
            if (when != "always") RoleOf(role);
            clock = new Clock { left = seconds, label = string.IsNullOrEmpty(label) ? "Time left" : label, when = when, role = role ?? "" };
        }

        /// <summary>You're on the role's grounds (on the ground: flying over doesn't count).</summary>
        bool YouInside(Role r)
        {
            var you = view.PlayerPosition;
            return you != null && view.YourHeight < 0.5 && r.holds != null && r.holds(you.Value);
        }

        public bool Met(string wait, string role)
        {
            if (wait == "countdown") return clock != null && clock.left <= 0;
            if (wait == "caught")
            {
                if (!chases.TryGetValue(role ?? "", out var c) || c.caught < S("catch_time")) return false;
                c.caught = 0;
                return true;
            }
            if (!roles.TryGetValue(role ?? "", out var r)) return false;
            switch (wait)
            {
                case "talk":
                    if (!r.talked) return false;
                    r.talked = false;
                    return true;
                case "parked":
                    return Parked(r);
                case "idle":
                    return r.person != null && r.person.steps.Count == 0;
                case "inside":
                    return YouInside(r);
                case "outside":
                    return view.PlayerPosition != null && !YouInside(r);
                default:
                    return false;
            }
        }

        Role RoleOf(string role) =>
            roles.TryGetValue(role ?? "", out var r) ? r : throw new Exception($"no building for '{role}' yet (call place first)");

        Person PersonOf(string role) => RoleOf(role).person ?? throw new Exception($"no one plays '{role}' (call spawn first)");

        static Where WhereOf(string to)
        {
            switch (to)
            {
                case "inside": return Where.Inside;
                case "door": return Where.Door;
                case "kerb": return Where.Kerb;
                case "car": return Where.Car;
                default: throw new Exception($"walk where? '{to}' (inside, door, kerb or car)");
            }
        }

        /// <summary>Your car stopped in the role's marked spot, lined up with the kerb.</summary>
        bool Parked(Role r)
        {
            var car = view.OwnVehicle;
            if (car == null || !r.Marked || car.kind != VehicleKind.Car) return false;
            var s = r.spot.Value;
            if (Numbers.Hypot(car.x - s.x, car.y - s.y) > S("park_reach") || Math.Abs(car.speed) > S("park_speed")) return false;
            double off = Math.Abs(Orbit.ShortestTurn(s.heading, car.heading));
            return Math.Min(off, Math.PI - off) < S("park_angle");
        }

        /// <summary>Your car near enough stopped by the role: in their spot if one's marked, else by the kerb outside.</summary>
        bool CarThere(Role r)
        {
            var car = view.OwnVehicle;
            if (car == null || car.kind != VehicleKind.Car || Math.Abs(car.speed) > S("park_speed") * 2) return false;
            var at = r.Marked ? r.Spot : r.kerb;
            double reach = r.Marked ? S("car_reach") : S("car_reach") + KERB_CAR_EXTRA;
            return Numbers.Hypot(car.x - at.x, car.y - at.y) < reach;
        }

        // People's errands.

        void GoIndoors(Person p)
        {
            p.steps.Clear();
            p.leaving = true;
            DropItem(p);
            if (p.at != Where.Inside || p.avatar.Visible) p.steps.Enqueue(new Step { act = Act.Walk, to = Where.Inside });
            p.steps.Enqueue(new Step { act = Act.Vanish });
        }

        static Vec2 PointOf(Role r, Where at) => at == Where.Inside ? r.Inside : at == Where.Door ? r.Outside : r.kerb;

        /// <summary>The way from where they are to where they're going, past each place between (the car's own leg is walked separately, from the kerb).</summary>
        static List<Vec2> PathTo(Person p, Where to)
        {
            var path = new List<Vec2>();
            int a = (int)p.at, b = (int)(to == Where.Car ? Where.Kerb : to);
            int dir = Math.Sign(b - a);
            if (dir == 0) return path;
            for (int i = a + dir; i != b + dir; i += dir) path.Add(PointOf(p.role, (Where)i));
            return path;
        }

        /// <summary>Where someone puts things into your car: at its kerb side, by the back door.</summary>
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
            var r = p.role;
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
                {
                    if (step.path == null)
                    {
                        step.path = PathTo(p, step.to);
                        if (p.at == Where.Inside) p.avatar.Visible = true;
                    }
                    if (step.path.Count > 0)
                    {
                        WalkAlong(p, step.path, dt);
                        if (step.path.Count > 0 || step.to == Where.Car) return;
                    }
                    if (step.to == Where.Car)
                    {
                        // Only while your car is there: otherwise they wait at the kerb.
                        var side = CarThere(r) ? CarSide(r.kerb) : null;
                        if (side == null)
                        {
                            WalkAlong(p, new List<Vec2> { r.kerb }, dt);
                            return;
                        }
                        if (!WalkAlong(p, new List<Vec2> { side.Value }, dt)) return;
                        var car = view.OwnVehicle;
                        w.heading = DetMath.Atan2(car.x - w.x, car.y - w.y);
                    }
                    p.at = step.to;
                    if (p.at == Where.Inside) p.avatar.Visible = false;
                    p.steps.Dequeue();
                    return;
                }
                case Act.Pause:
                    w.Moved(0, 0, dt);
                    p.timer += dt;
                    if (p.timer < step.seconds) return;
                    p.timer = 0;
                    p.steps.Dequeue();
                    return;
                case Act.Hold:
                    if (p.item == null) Carry(p, step.model, step.size);
                    p.steps.Dequeue();
                    return;
                case Act.PutInCar:
                    if (p.item != null)
                    {
                        var item = new Item { model = p.item.model, size = p.item.size };
                        item.go = PlaceItem(item, cargoRoot.transform);
                        cargo.Add(item);
                        DropItem(p);
                        PlaceCargo();
                    }
                    p.steps.Dequeue();
                    return;
                case Act.TakeFromCar:
                    if (cargo.Count > 0 && p.item == null)
                    {
                        var last = cargo[cargo.Count - 1];
                        cargo.RemoveAt(cargo.Count - 1);
                        UnityEngine.Object.Destroy(last.go);
                        Carry(p, last.model, last.size);
                    }
                    p.steps.Dequeue();
                    return;
                case Act.PutDown:
                    DropItem(p);
                    p.steps.Dequeue();
                    return;
                case Act.Vanish:
                    p.avatar.Visible = false;
                    p.steps.Dequeue();
                    return;
            }
        }

        /// <summary>On along the points (dropping each as it's reached); true once at the last.</summary>
        bool WalkAlong(Person p, List<Vec2> path, double dt)
        {
            var w = p.walker;
            double budget = S("walk_speed") * dt;
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

        GameObject PlaceItem(Item item, Transform parent)
        {
            var m = library.Get(item.model);
            double s = item.size / Math.Max(m.size.x, m.size.z);
            return Props.Place(m, parent, 0, 0, 0, s, s, s);
        }

        /// <summary>A thing in their arms, held out in front at chest height.</summary>
        void Carry(Person p, string model, double size)
        {
            if (p.item != null) return;
            p.item = new Item { model = model, size = size };
            p.item.go = PlaceItem(p.item, p.avatar.root.transform);
            p.item.go.transform.localPosition = new Vector3(0, (float)(Avatar.PERSON_HEIGHT * 0.42), (float)(size * 0.75));
        }

        static void DropItem(Person p)
        {
            if (p.item == null) return;
            if (p.item.go != null) UnityEngine.Object.Destroy(p.item.go);
            p.item = null;
        }

        /// <summary>What rides on your car's roof: rows of three along the roof, then a layer up.</summary>
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
            double gap = cargo.Max(c => c.size);
            for (int i = 0; i < cargo.Count; i++)
            {
                int row = i % 3, layer = i / 3;
                cargo[i].go.transform.localPosition = new Vector3(0, (float)(layer * gap), (float)((row - 1) * gap * 1.15));
                cargo[i].go.transform.localRotation = Quaternion.Euler(0, (i * 37) % 12 - 6, 0);
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
            DropItem(p);
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
            double reach = view.InCar ? S("talk_reach_car") : S("talk_reach");
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

        /// <summary>The prompt: how to talk to whoever's in reach, else the first of the story's hints whose condition holds.</summary>
        string Prompt()
        {
            var r = TalkTarget();
            if (r != null) return $"Press {(input.Connected ? "A" : "Enter")} to talk to {r.name}";
            foreach (var h in hints)
                if (HintShows(h)) return h.text;
            return null;
        }

        bool HintShows(HintLine h)
        {
            switch (h.when)
            {
                case "always":
                    return true;
                case "no_car":
                    return view.OwnVehicle == null;
                case "near":
                case "near_driving":
                {
                    if (h.when == "near_driving" && !view.DrivingMyCar) return false;
                    var you = view.PlayerPosition;
                    return you != null && roles.TryGetValue(h.role, out var r) && Geo.Dist(you.Value, Locate(r)) < HINT_REACH;
                }
                default:
                    return false;
            }
        }

        /// <summary>Where a role is, for being near it: their spot if marked, else their person, else the kerb outside.</summary>
        static Vec2 Locate(Role r) => r.Marked ? r.Spot : r.person != null ? new Vec2(r.person.walker.x, r.person.walker.y) : r.kerb;

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
            UpdateChases(dt);
            if (clock != null)
            {
                clock.running = clock.when == "always" || (roles.TryGetValue(clock.role, out var r) && YouInside(r) == (clock.when == "inside"));
                // It doesn't run while you read.
                if (clock.running && !view.InDialogue) clock.left = Math.Max(0, clock.left - dt);
            }
        }

        /// <summary>The chases' cars after you, the one at the wheel out beside any that's pulled up by you, and whether they've had you.</summary>
        void UpdateChases(double dt)
        {
            view.Obstacles.Clear();
            var you = view.PlayerPosition;
            if (you == null) return;
            bool still = view.InDialogue;
            double top = view.IsDriving ? S("chase_speed_car") : S("chase_speed");
            var roads = view.world.layout.roads;
            bool flash = (Time.realtimeSinceStartupAsDouble * 3) % 1 < 0.5;
            foreach (var c in chases.Values)
            {
                bool near = false;
                foreach (var cop in c.cops)
                {
                    var car = cop.chaser.car;
                    // They wait while you read, as you do.
                    if (!still) cop.chaser.Step(you.Value, top, dt, roads, view.CanDriveAt);
                    if (cop.chaser.Lost)
                    {
                        var busy = view.CarsAbout();
                        foreach (var o in c.cops) if (o != cop) busy.Add(new Vec2(o.chaser.car.x, o.chaser.car.y));
                        var spot = StartFor(you.Value, busy);
                        if (spot != null) cop.chaser.Restart(spot.Value);
                    }
                    PlaceCop(cop);
                    cop.view.Lights(view.Dusk);
                    Materials.SetGlow(cop.siren[0], SirenRed, flash ? 3f : 0.3f);
                    Materials.SetGlow(cop.siren[1], SirenBlue, flash ? 0.3f : 3f);
                    view.Obstacles.Add(new Vec2(car.x, car.y));
                    double reach = Reach(car, you.Value) - (view.IsDriving ? Vehicle.CAR_HALF_WIDTH : Player.PLAYER_RADIUS);
                    bool close = reach < S("catch_reach") && view.YourHeight < 0.8;
                    near |= close;
                    // Pulled up by you: out they get, beside the driver's door, facing you.
                    bool outOfCar = Math.Abs(car.speed) < 0.3 && Geo.Dist(new Vec2(car.x, car.y), you.Value) < 2.2;
                    cop.officer.Visible = outOfCar;
                    if (outOfCar)
                    {
                        var f = car.Forward;
                        var w = cop.walker;
                        w.x = car.x + f.x * 0.05 + f.y * (car.halfWidth + 0.18);
                        w.y = car.y + f.y * 0.05 - f.x * (car.halfWidth + 0.18);
                        w.Moved(0, 0, dt);
                        w.heading = DetMath.Atan2(you.Value.x - w.x, you.Value.y - w.y);
                        cop.officer.Update(w, dt);
                    }
                }
                c.caught = near ? c.caught + dt : 0;
            }
        }

        /// <summary>How far a point is from a car's sides (0 if it's under it).</summary>
        static double Reach(Vehicle car, Vec2 p)
        {
            var f = car.Forward;
            double dx = p.x - car.x, dy = p.y - car.y;
            double along = Math.Abs(dx * f.x + dy * f.y) - car.halfLength, across = Math.Abs(-dx * f.y + dy * f.x) - car.halfWidth;
            return Numbers.Hypot(Math.Max(0, along), Math.Max(0, across));
        }
    }
}

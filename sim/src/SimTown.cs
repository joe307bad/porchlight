// One town's traffic and townsfolk, as the game runs them (ExploreView.Populate and
// its Tick), so the town here is the one every player's game makes from the same key:
// the same slots in the same order, started from the same seeds. The traffic is the
// town's life (Life/TownLife.cs: its people's days, run by the town clock); the
// wanderers stroll as ever.
//
// Each tick the town's life is run up to the clock (the players in it obstacles) and
// the wanderers stepped a tenth of a second, and it's all packed twice: in full (7
// bytes a car or person, WorldSync's records; an empty slot is the hidden sentinel)
// and as a delta of only what a client carrying each one on along its heading at its
// speed would now have wrong (Delta below).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

namespace Porchlight.Sim
{
    public sealed class SimTown
    {
        /// <summary>A tick's time, in the slices the game steps a town by.</summary>
        public const double TICK = 0.1, SLICE = 0.05;

        public readonly int id;
        public readonly string key;
        readonly IsoLayout layout;
        readonly int width, height;
        /// <summary>Each street's name, as the game's directions say it (Explore/Names.cs).</summary>
        readonly StreetNames streets;
        /// <summary>What each of its buildings is for (Zoning.cs), for Manage's land-use map.</summary>
        public readonly TownZoning zoning;
        /// <summary>Its parks, for Manage's map: each one's middle and outline (Parks.Encode).</summary>
        public readonly List<(double[] at, List<double[]> rings)> parks;
        public readonly Traffic traffic;
        public readonly List<Pedestrian> people = new List<Pedestrian>();
        /// <summary>Townsfolk made with the town (the rest got out of cars someone took).</summary>
        public readonly int basePeople;
        /// <summary>For drivers put out of their cars (the game's own is "auto-walk" too).</summary>
        readonly Rng autoRng = new Rng("auto-walk");
        /// <summary>The cars someone's in ("t3"; the town's parked ones, "p…", aren't traffic).</summary>
        readonly HashSet<string> taken = new HashSet<string>();
        bool takenSynced;
        /// <summary>The players here (tiles), which cars stop for.</summary>
        List<Vec2> obstacles = new List<Vec2>();
        public readonly Delta delta = new Delta();
        /// <summary>The townsfolk's looks past `basePeople`, changed since last asked.</summary>
        public bool looksChanged;

        /// <summary>The town as the game makes it from `key`; its life starts at `start` (ms since 1970), or by the clock at the first step.</summary>
        public SimTown(int id, string key, long? start = null)
        {
            this.id = id;
            this.key = key;
            var (seed, p) = ParseKey(key);
            var model = Generator.Generate(seed, p);
            layout = Layout.Build(model);
            width = model.width;
            height = model.height;
            streets = StreetNaming.Name(model, layout.roads);
            zoning = Zoning.Plan(model, layout);
            parks = Parks.Encode(model);
            // ExploreView.Populate, in the same order, so each car and person here is the game's.
            traffic = new Traffic(model, layout, zoning, new Rng($"{model.seed}::traffic"), Catalog.PickCarModel);
            var rng = new Rng($"{model.seed}::townsfolk");
            var paths = layout.sidewalks.paths;
            double walkable = paths.Sum(x => x.Length);
            int count = paths.Count > 0 ? TownLife.Wanderers(walkable) : 0;
            for (int k = 0; k < count; k++)
            {
                people.Add(Pedestrian.Create(Catalog.RandomLook(rng), layout.sidewalks, rng));
                rng.Next(); // the game draws each one's avatar seed here
            }
            people.AddRange(traffic.walkers);
            basePeople = people.Count;
            if (start != null) traffic.StartAt(start.Value);
        }

        /// <summary>The town's life (people, trips, the clock).</summary>
        public TownLife life => traffic.life;

        /// <summary>A town's key (its room's name: "seed=porchlight&amp;width=256&amp;…") as the game reads it (PorchlightApp.ParseUrlState).</summary>
        public static (string seed, GenParams p) ParseKey(string key) => TownKey.Parse(key);

        /// <summary>Where the players here are now (tiles, the town's frame).</summary>
        public void SetObstacles(List<Vec2> at) => obstacles = at;

        /// <summary>
        /// The room's list of taken cars ("t3,p12,…"): out of the traffic, their drivers onto the
        /// pavement (as the host's game did), except those taken before this town was told.
        /// Empty again (the room's closed): back into the traffic, and the extra townsfolk gone.
        /// </summary>
        public void SetTaken(IEnumerable<string> list)
        {
            bool first = !takenSynced;
            takenSynced = true;
            var now = new HashSet<string>(list);
            if (now.Count == 0 && taken.Count > 0)
            {
                traffic.ReleaseAll();
                taken.Clear();
                if (people.Count > basePeople)
                {
                    people.RemoveRange(basePeople, people.Count - basePeople);
                    looksChanged = true;
                }
                return;
            }
            foreach (var id in now)
            {
                if (id.Length < 2 || !taken.Add(id) || id[0] != 't' || !int.TryParse(id.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int i)) continue;
                if (i < 0 || i >= traffic.cars.Count || traffic.cars[i].taken) continue;
                bool empty = traffic.cars[i].hidden;
                var c = traffic.Take(i);
                // An empty slot (nobody driving it just now) has nobody to put out.
                if (!first && !empty && Catalog.IsCar(c.model) && layout.sidewalks.paths.Count > 0)
                {
                    // ExploreView.EjectDriver: out of the driver's door.
                    var look = Catalog.RandomLook(autoRng);
                    var door = new Vec2(c.x + Math.Sin(c.heading) * 0.45, c.y - Math.Cos(c.heading) * 0.45);
                    people.Add(Pedestrian.Create(look, layout.sidewalks, autoRng, door));
                    looksChanged = true;
                }
            }
        }

        /// <summary>The looks of the townsfolk past those the town came with (each client makes the same ones).</summary>
        public List<string> ExtraLooks() => people.Skip(basePeople).Select(p => p.look).ToList();

        /// <summary>
        /// A tick: the town's life up to `moment` (ms since 1970; null: a tenth of a second on), the wanderers
        /// a tenth of a second on. Fallen a long way behind (the box slept), it starts again from everyone's day.
        /// </summary>
        public void Step(long? moment = null)
        {
            if (moment != null && traffic.life.Started && moment.Value - traffic.Clock > CATCH_UP_MS) traffic.Restart(moment.Value);
            for (double left = TICK; left > 1e-9; left -= SLICE)
            {
                double dt = Math.Min(SLICE, left);
                foreach (var p in people) p.Update(dt);
            }
            long to = moment ?? (traffic.life.Started ? traffic.Clock + (long)(TICK * 1000) : TownClock.Now());
            traffic.RunTo(to, obstacles, TICK);
        }

        /// <summary>Behind the clock by more than this, start again rather than run every event since.</summary>
        const long CATCH_UP_MS = 10 * TownClock.MINUTE;

        /// <summary>Who's indoors where, and off the map at each gate (for Manage), and the day's figures.</summary>
        public object Life() => new
        {
            id,
            clock = traffic.Clock,
            time = TownClock.Format(traffic.Clock),
            // [building (its place in Zoning.Encode's list, as Manage has them), people inside]
            inside = traffic.life.inside.Where(kv => BuildingIndex().ContainsKey(kv.Key)).Select(kv => new[] { BuildingIndex()[kv.Key], kv.Value }).OrderBy(x => x[0]).ToList(),
            offMap = traffic.life.offMap ?? new int[0],
            gates = traffic.life.net.borders.Select(b => new[] { Math.Round(b.at.x, 1), Math.Round(b.at.y, 1) }).ToList(),
            stops = traffic.life.busStops.Select(b => new[] { Math.Round(b.at.x, 1), Math.Round(b.at.y, 1) }).ToList(),
            summary = traffic.life.Summary(),
            kinds = Kinds(),
            calendar = Calendar(),
            market = Market(),
        };

        /// <summary>Today and the week ahead in the town's calendar (Life/Calendar.cs), for Manage's town view.</summary>
        public object Calendar()
        {
            var life = traffic.life;
            long today = TownClock.DayOf(traffic.Clock);
            var bins = life.services.Where(s => s.kind == ServiceKind.Bins).ToList();
            return Enumerable.Range(0, 8).Select(i =>
            {
                var td = life.Day(today + i);
                var events = td.Events();
                var round = bins.Where(s => s.weekday == td.weekday).ToList();
                if (round.Count > 0) events.Add($"Bins: {round.Sum(s => s.stops.Count)} stops");
                return new { day = td.day, name = td.Name, weekend = td.Weekend, holiday = td.holiday, school = td.schoolDay, market = td.market, events };
            }).ToList();
        }

        /// <summary>The market in the plaza now: its stalls, how many are up today, and who's there.</summary>
        public object Market()
        {
            var life = traffic.life;
            if (life.market == null) return null;
            var td = life.Today;
            long tod = traffic.Clock - TownClock.At(td.day, 0);
            return new
            {
                stalls = life.market.stalls.Count,
                up = life.StallsUp(td),
                open = td.market && tod >= td.marketOpen && tod < td.marketClose,
                hours = td.market ? $"{TownClock.Clock12(td.marketOpen / 1000, false)} to {TownClock.Clock12(td.marketClose / 1000, false)}" : null,
                people = life.AtMarketNow,
                at = new[] { Math.Round(life.market.middle.x, 1), Math.Round(life.market.middle.y, 1) },
            };
        }

        /// <summary>
        /// What each of the tick's car and person records is (Manage shows and hides each kind, and lists them), a
        /// letter a record, '-' for an empty one. Cars: r a resident's car, c a commuter's, t through traffic, v a van
        /// (or taxi) on its rounds, b a bus, g a bin lorry, e an ambulance or fire engine, s a boat. People: w a worker, s a student, h someone at home in the day
        /// (out on errands), c a commuter on foot, m at the market (shopping, or at their stall), x a wanderer (strolling, with no day).
        /// </summary>
        public object Kinds()
        {
            var life = traffic.life;
            var cars = new System.Text.StringBuilder(traffic.cars.Count);
            for (int i = 0; i < traffic.cars.Count; i++)
            {
                var c = traffic.cars[i];
                if (c.loop >= 0) { cars.Append('s'); continue; }
                int id = i < life.carSlots.Length ? life.CarInSlot(i) : -1;
                if (id < 0) { cars.Append(c.taken ? 'r' : '-'); continue; }
                var car = life.cars[id];
                if (car.seg < 0 && car.state == CarState.Parked) cars.Append('p');
                else if (car.bus >= 0) cars.Append('b');
                else if (car.service >= 0) cars.Append(life.services[car.service].kind == ServiceKind.Bins ? 'g' : 'e');
                else if (car.van >= 0) cars.Append('v');
                else if (car.owner >= 0) cars.Append(PersonLetter(life.people[car.owner].kind));
                else cars.Append('r');
            }
            var people = new System.Text.StringBuilder(this.people.Count);
            int wanderers = basePeople - life.personSlots.Length;
            for (int i = 0; i < this.people.Count; i++)
            {
                if (i < wanderers) { people.Append('x'); continue; }
                int slot = i - wanderers;
                int id = slot < life.personSlots.Length ? life.PersonInSlot(slot) : -1;
                people.Append(id >= 0 ? (life.people[id].browseSince >= 0 ? 'm' : PersonLetter(life.people[id].kind, onFoot: true)) : slot < life.personSlots.Length ? '-' : 'h');
            }
            return new { cars = cars.ToString(), people = people.ToString() };
        }

        static char PersonLetter(PersonKind k, bool onFoot = false) => k switch
        {
            PersonKind.Worker => onFoot ? 'w' : 'r',
            PersonKind.Student => onFoot ? 's' : 'r',
            PersonKind.AtHome => onFoot ? 'h' : 'r',
            PersonKind.CommuterIn => 'c',
            PersonKind.Through => onFoot ? 'c' : 't',
            _ => onFoot ? 'h' : 'r',
        };

        /// <summary>
        /// Manage's focus view (Life/Focus.cs) on one car or person: by who they are (`bySlot` false: the life's
        /// own id), or by where they're drawn now (a place in the tick's car or person records). A wanderer, or
        /// an empty slot, has no day to explain. Buildings are given as Manage knows them (zoning's order).
        /// </summary>
        /// <summary>The town's traffic lights now (Manage's map).</summary>
        public object Signals(bool withStops) => traffic.life.Signals(withStops);

        /// <summary>Manage's congestion slider: the streets that held up cars most.</summary>
        public const int CONGESTED_STREETS = 12;

        /// <summary>
        /// The last day's hold-ups an hour at a time (oldest first, this hour last), and the streets that held cars up
        /// most in all in the hour `ago` hours back: each with its kind, how many cars left it, their hold-ups
        /// (seconds, in all and on average) and its centreline (the town's tiles), to draw.
        /// </summary>
        public object Congestion(int ago)
        {
            var life = traffic.life;
            double R(double v) => Math.Round(v * 10) / 10;
            var hours = life.HoldsByHour().Select(h => new { from = h.from, time = TownClock.Format(h.from), cars = h.cars, hold = R(h.hold) }).ToList();
            var streets = life.MostHeldUp(ago, CONGESTED_STREETS).Select(r =>
            {
                var road = life.net.roads[r.road];
                return new
                {
                    road = r.road,
                    name = NameOf(r.road),
                    stretch = StreetNaming.Stretch(this.streets, layout.roads, r.road),
                    kind = (int)road.kind,
                    cars = r.cars,
                    hold = R(r.hold),
                    mean = R(r.Mean),
                    points = road.points.SelectMany(p => new[] { R(p.x), R(p.y) }).ToArray(),
                };
            }).ToList();
            return new { ago, hours, streets };
        }

        public object Focus(bool car, bool bySlot, int i, bool brief = false)
        {
            var life = traffic.life;
            int id = i;
            if (bySlot)
            {
                if (car) id = life.CarInSlot(i);
                else
                {
                    // The townsfolk records: the wanderers, then the life's people slots, then drivers put out of cars someone took.
                    int slot = i - (basePeople - life.personSlots.Length);
                    // A wanderer, out for a walk: nobody's record in the town's life, but named all the same (PersonNames).
                    if (slot < 0 && i >= 0)
                    {
                        string name = PersonNames.Wanderer(life.seed, i);
                        return new { wanderer = true, name, doing = PersonNames.WANDERING, line = $"{name} · {PersonNames.WANDERING}" };
                    }
                    if (slot < 0 || slot >= life.personSlots.Length) return new { wanderer = true };
                    id = life.PersonInSlot(slot);
                }
                if (id < 0) return new { empty = true };
            }
            // Just a line (the map's hover): who, what they're doing, and where in their day.
            // With a person's name and what they're doing on its own, for the tag over them in the game.
            if (brief) return car ? (object)new { id, line = life.BriefCar(id) } : new { id, line = life.BriefPerson(id), name = life.NameOf(id), doing = life.DoingOf(id) };
            var index = BuildingIndex();
            Func<int, int> building = b => index.TryGetValue(b, out int k) ? k : -1;
            object o = car ? life.FocusCar(id, building) : life.FocusPerson(id, building);
            if (o is Dictionary<string, object> d && !car && d["drawn"] is string drawn && drawn == "person")
                // Their place in the tick's records (past the wanderers), for Manage to follow.
                d["slot"] = (int)d["slot"] + basePeople - life.personSlots.Length;
            return o;
        }

        Dictionary<int, int> buildingIndex;

        /// <summary>Each building id's place in Zoning.Encode's list: Manage knows buildings by that.</summary>
        public Dictionary<int, int> BuildingIndex()
        {
            if (buildingIndex != null) return buildingIndex;
            buildingIndex = new Dictionary<int, int>();
            for (int i = 0; i < zoning.buildings.Count; i++) buildingIndex[zoning.buildings[i].buildingId] = i;
            return buildingIndex;
        }

        /// <summary>
        /// Its streets, as the Manage map draws them (Checks roadmap writes the same for the towns that come
        /// with the game): [kind, width, x0, y0, x1, y1, …] in tiles, the town's frame, to a tenth.
        /// </summary>
        public List<double[]> Roads()
        {
            double R(double v) => Math.Round(v * 10) / 10;
            return layout.roads.Where(r => r.points != null && r.points.Count >= 2)
                .Select(r => new[] { (double)(int)r.kind, R(r.width) }.Concat(r.points.SelectMany(q => new[] { R(q.x), R(q.y) })).ToArray())
                .ToList();
        }

        /// <summary>A road's street's name ("Elm Street"), or "" for none (a path, say).</summary>
        public string NameOf(int road) =>
            road >= 0 && road < streets.ofRoad.Count ? streets.streets[streets.ofRoad[road]].name ?? "" : "";

        /// <summary>Each of Roads()'s streets' names, in the same order.</summary>
        public List<string> RoadNames()
        {
            var o = new List<string>();
            for (int i = 0; i < layout.roads.Count; i++)
                if (layout.roads[i].points != null && layout.roads[i].points.Count >= 2) o.Add(NameOf(i));
            return o;
        }

        /// <summary>
        /// What the server checks players' word against: the race course as the game lays it out (each point [x, y,
        /// half the road's width]; null: none) and where the town's parked cars are, in the game's order ("p3" is the third on).
        /// </summary>
        public object Ground()
        {
            double R(double v) => Math.Round(v * 100) / 100;
            var c = Course.Longest(layout.roads, width, height);
            return new
            {
                course = c == null ? null : new { points = c.points.Select((p, i) => new[] { R(p.x), R(p.y), R(c.half[i]) }).ToList() },
                parked = layout.cars.Select(p => new[] { R(p.x), R(p.y) }).ToList(),
            };
        }

        public byte[] Cars() => WorldSync.EncodeCars(traffic.cars);
        public byte[] People() => WorldSync.EncodePeople(people);
        /// <summary>The town's traffic lights now, for the end of a delta (StreetFurniture.EncodeLights).</summary>
        public byte[] Lights() => StreetFurniture.EncodeLights(traffic.life.net, traffic.life.Now);
    }
}

// The town's people and their days. Who they are comes from the town's zoning
// alone: every home has its households (Population.cs), every building its jobs.
// Workers are matched to jobs by a gravity model, and some of each to the gates
// instead (a fifth more people than either, so some commute in, some out, and
// some only pass through). Each person's day is a chain of trips, each
// from where the last ended: to work and home, to school, to the shops, to lunch,
// to see someone. A trip's legs are on foot, by car (walk to it, drive, park, walk
// from it) or by bus (walk to the stop, wait, ride, walk on). A trip that can't be
// made puts the person where they were going, so a bad day never wedges the town.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;

namespace Porchlight.Life
{
    public sealed partial class TownLife
    {
        /// <summary>
        /// How many more people a town's life has than its plan's figures (Zoning's 2.5 a home), all of them at home
        /// in the day: children, the retired, those working from home. Its workers and jobs are the plan's (so the
        /// rushes are what the roads were laid out for); the rest make their trips through the day, to the shops,
        /// school and visiting, mostly on foot. It's per home, so a bigger town has that many more.
        /// </summary>
        public const double CROWD = 3;
        /// <summary>A household has at most this many cars a home: those at home in the day share what's left.</summary>
        const double CARS_A_HOME = 2;
        /// <summary>More people than homes or jobs by this much: the rest commute in or out, or pass through.</summary>
        public const double THROUGH = 1.2;
        /// <summary>The chance of a home slot or job slot, kept within these.</summary>
        const double P_LOW = 0.05, P_HIGH = 0.9;
        /// <summary>The trips each person remembers, for Manage's focus view (a day's worth: the busiest days have five or six).</summary>
        const int RECENT = 6;
        /// <summary>Closer than this (tiles: 240 m), people walk.</summary>
        public const double WALK_TILES = 60;
        /// <summary>Who has a car: workers, and those at home in the day.</summary>
        const double WORKER_CARS = 0.9, AT_HOME_CARS = 0.7;
        /// <summary>A share of people make one more trip, at no particular time.</summary>
        const double BACKGROUND = 0.1;

        readonly List<int> shops = new List<int>(), schools = new List<int>(), homesList = new List<int>();
        /// <summary>Each shop's size for the Huff model: the people who work there (a supermarket's many, a corner store's few).</summary>
        readonly Dictionary<int, double> shopSize = new Dictionary<int, double>();

        // --- Who lives here ---------------------------------------------------------------------

        void MakePeople()
        {
            var rng = new Rng($"{seed}::people");
            var built = new List<(int b, List<(string kind, List<Member> members)> households)>();
            var workerHomes = new List<int>();
            var jobSlots = new List<int>();
            double workerCarry = 0, residentCarry = 0;
            var homesIn = new Dictionary<int, int>();
            var carsIn = new Dictionary<int, int>();
            foreach (var b in zoning?.buildings ?? new List<ZonedBuilding>())
            {
                if (!net.access.ContainsKey(b.buildingId)) continue;
                if (b.homes > 0) homesList.Add(b.buildingId);
                if (b.zone == Zone.Retail || b.zone == Zone.MixedUse)
                {
                    shops.Add(b.buildingId);
                    shopSize[b.buildingId] = Math.Max(1, b.jobs);
                }
                // Children go to school: a school, or in a town without one the hall or the church. Never the fire station or the doctor's.
                if (b.zone == Zone.Civic && b.kind != BuildingKind.FireStation && b.kind != BuildingKind.Clinic) schools.Add(b.buildingId);
                homesIn[b.buildingId] = b.homes;
                workerCarry += b.homes * Zoning.WORKERS_A_HOME;
                residentCarry += b.homes * Zoning.PEOPLE_A_HOME * CROWD;
                int workers = (int)Math.Floor(workerCarry);
                workerCarry -= workers;
                int residents = (int)Math.Floor(residentCarry);
                residentCarry -= residents;
                for (int k = 0; k < b.jobs; k++) jobSlots.Add(b.buildingId);
                if (residents <= 0) continue;
                var hs = Households(b.buildingId, residents, workers);
                built.Add((b.buildingId, hs));
                foreach (var h in hs)
                    foreach (var m in h.members)
                        if (m.worker) workerHomes.Add(b.buildingId);
            }
            if (schools.Exists(id => KindOf(id) == BuildingKind.School)) schools.RemoveAll(id => KindOf(id) != BuildingKind.School);
            if (shops.Count == 0) foreach (var b in zoning?.buildings ?? new List<ZonedBuilding>()) if (b.jobs > 0 && net.access.ContainsKey(b.buildingId)) shops.Add(b.buildingId);
            rng.Shuffle(jobSlots);

            // How many work where (as before households: a fifth more people than homes or jobs, so some commute in,
            // some out, and some pass through), then who works where by the gravity model.
            int W = workerHomes.Count, J = jobSlots.Count, gates = net.borders.Count;
            int total = (int)Math.Round(THROUGH * Math.Max(W, J));
            double pHome = Clamp(total == 0 ? 0 : W / (double)total);
            double pJob = Clamp(total == 0 ? 0 : J / (double)total);
            int employed, residentsOut, commutersIn, through;
            if (gates == 0)
            {
                employed = Math.Min(W, J);
                residentsOut = commutersIn = through = 0;
            }
            else
            {
                employed = Math.Min(W, (int)Math.Round(total * pHome));
                residentsOut = (int)Math.Round(employed * (1 - pJob));
                int filled = Math.Min(J, (int)Math.Round(total * pJob));
                if (employed - residentsOut > filled) residentsOut = employed - filled;
                commutersIn = Math.Max(0, filled - (employed - residentsOut));
                through = (int)Math.Round(total * (1 - pHome) * (1 - pJob));
            }
            // The employed: a seeded draw of the households' workers (the rest have no job: at home in the day).
            var order = new List<int>();
            for (int i = 0; i < W; i++) order.Add(i);
            rng.Shuffle(order);
            var employedIdx = new HashSet<int>();
            for (int i = 0; i < employed; i++) employedIdx.Add(order[i]);
            var origins = new List<int>();
            for (int i = 0; i < W; i++) if (employedIdx.Contains(i)) origins.Add(workerHomes[i]);
            var gateOf = new List<int>();
            for (int i = 0; i < commutersIn; i++) gateOf.Add(rng.Int(0, gates - 1));
            foreach (int g in gateOf) origins.Add(-1 - g);
            var destinations = new List<int>();
            int inTown = employed - residentsOut + commutersIn;
            for (int i = 0; i < Math.Min(inTown, J); i++) destinations.Add(jobSlots[i]);
            for (int i = 0; i < residentsOut; i++) destinations.Add(-1 - i % gates);
            int population = 0;
            foreach (var (_, hs) in built) foreach (var h in hs) population += h.members.Count;
            var jobOf = Gravity(rng, origins, destinations, population);
            var jobs = new Queue<int>(jobOf);

            Place PlaceOf(int place) => place < 0 ? Place.Gate(-1 - place) : Place.Building(place);
            // (Each household's cars, up to CARS_A_HOME a home: the workers' first, then the rest who want one.)
            bool CarFor(int b, bool wants)
            {
                int had = carsIn.TryGetValue(b, out int n) ? n : 0;
                if (!wants || had >= CARS_A_HOME * Math.Max(1, homesIn.TryGetValue(b, out int homes) ? homes : 1)) return false;
                carsIn[b] = had + 1;
                return true;
            }
            int w = 0;
            foreach (var (b, hs) in built)
            {
                var home = Place.Building(b);
                foreach (var h in hs)
                {
                    var members = new List<int>();
                    foreach (var m in h.members)
                    {
                        Person p;
                        if (m.worker && employedIdx.Contains(w++) && jobs.Count > 0 && jobs.Peek() != int.MinValue)
                        {
                            var work = PlaceOf(jobs.Dequeue());
                            p = AddPerson(rng, PersonKind.Worker, home, work, work.border || CarFor(b, rng.Chance(WORKER_CARS)));
                        }
                        else
                        {
                            if (m.worker && employedIdx.Contains(w - 1) && jobs.Count > 0) jobs.Dequeue();
                            bool student = m.role == Role.Child && m.age >= SCHOOL_FROM && schools.Count > 0;
                            bool drives = m.role != Role.Child && rng.Chance(AT_HOME_CARS);
                            p = AddPerson(rng, student ? PersonKind.Student : PersonKind.AtHome, home, student ? Place.Building(Nearest(schools, b, rng, 0)) : home, CarFor(b, drives));
                        }
                        p.household = households.Count;
                        p.role = m.role;
                        p.age = m.age;
                        members.Add(p.id);
                    }
                    households.Add((h.kind, members));
                }
            }
            // Commuters in from beyond the gates (their jobs by the gravity model too), and those passing through.
            foreach (int g in gateOf)
            {
                int job = jobs.Count > 0 ? jobs.Dequeue() : int.MinValue;
                if (job == int.MinValue || job < 0) continue;
                AddPerson(rng, PersonKind.CommuterIn, Place.Gate(g), Place.Building(job), true);
            }
            for (int i = 0; i < through && gates > 0; i++)
            {
                var from = Place.Gate(rng.Int(0, gates - 1));
                var to = gates > 1 ? Place.Gate((from.id + 1 + rng.Int(0, gates - 2)) % gates) : from;
                AddPerson(rng, PersonKind.Through, from, to, true);
            }
        }

        static double Clamp(double p) => Math.Max(P_LOW, Math.Min(P_HIGH, p));

        Person AddPerson(Rng rng, PersonKind kind, Place home, Place work, bool car)
        {
            var p = new Person { id = people.Count, kind = kind, home = home, work = work, hasCar = car, at = home };
            p.where = home.border ? Where.OffMap : Where.Inside;
            Count(p, 1);
            // Walking pace: 2-3 mph, and now and then a jogger.
            p.pace = rng.Chance(0.05) ? rng.Range(0.6, 0.8) : rng.Range(0.22, 0.34);
            people.Add(p);
            if (car) GiveCar(p, home);
            return p;
        }

        /// <summary>A car of their own, parked at `at` (off the map, if that's a gate).</summary>
        void GiveCar(Person p, Place at)
        {
            var c = new Car { id = cars.Count, owner = p.id, length = 1.1 + 0.5 * Layout.Hash(p.id + 101, cars.Count + 7) };
            cars.Add(c);
            p.car = c.id;
            ParkNear(c, at);
        }

        /// <summary>Put a parked car near a place straight away (its driveway, a free kerb spot near, or anyhow).</summary>
        void ParkNear(Car c, Place at)
        {
            // Put somewhere new at once: not drawn moving there.
            if (c.seg < 0) FreeCarSlot(c);
            if (c.spot >= 0 && spotHolder[c.spot] == c.id) spotHolder[c.spot] = -1;
            c.spot = -1;
            c.parkedBy = -1;
            c.offMap = false;
            if (at.border)
            {
                c.offMap = true;
                return;
            }
            c.parkedBy = at.id;
            if (options.infiniteParking || !net.access.TryGetValue(at.id, out var a)) return;
            var owner = c.owner >= 0 ? people[c.owner] : null;
            if (a.driveway >= 0 && spotHolder[a.driveway] < 0 && owner != null && !owner.home.border && owner.home.id == at.id)
            {
                c.spot = a.driveway;
                spotHolder[c.spot] = c.id;
                return;
            }
            // Out over the lanes from the door's, nearest first.
            int spot = NearestFreeSpot(a);
            if (spot >= 0)
            {
                c.spot = spot;
                spotHolder[spot] = c.id;
            }
        }

        int NearestFreeSpot(Access a)
        {
            var seen = new HashSet<int>();
            var frontier = new List<int>(net.LanesFor(a));
            for (int ring = 0; ring < 6 && frontier.Count > 0; ring++)
            {
                int best = -1;
                double bestD = double.PositiveInfinity;
                var next = new List<int>();
                foreach (int l in frontier)
                {
                    if (!seen.Add(l)) continue;
                    foreach (int s in net.lanes[l].spots)
                    {
                        if (!Free(s)) continue;
                        double d = Geo.Dist(net.spots[s].at, a.door);
                        if (d < bestD || (d == bestD && s < best))
                        {
                            bestD = d;
                            best = s;
                        }
                    }
                    foreach (int m in net.lanes[l].outs) next.Add(net.movements[m].to);
                    next.Add(net.Reverse(net.lanes[l]).id);
                }
                if (best >= 0) return best;
                frontier = next;
            }
            return -1;
        }

        /// <summary>Of a few of `list` picked at random, the nearest to building `b`.</summary>
        /// <summary>The nearest of this many picked at random (ERRAND_CHOICES with nearErrands): more, and errands are nearer home.</summary>
        const int CHOICES = 3, ERRAND_CHOICES = 6;

        int Nearest(List<int> list, int b, Rng rng, int tries = -1)
        {
            if (tries < 0) tries = options.nearErrands ? ERRAND_CHOICES : CHOICES;
            var from = Door(Place.Building(b));
            if (tries <= 0)
            {
                int best = list[0];
                double bestD = double.PositiveInfinity;
                foreach (int x in list)
                {
                    double d = Geo.Dist(Door(Place.Building(x)), from);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = x;
                    }
                }
                return best;
            }
            int pick = rng.Pick(list);
            double pickD = Geo.Dist(Door(Place.Building(pick)), from);
            for (int k = 1; k < tries; k++)
            {
                int x = rng.Pick(list);
                double d = Geo.Dist(Door(Place.Building(x)), from);
                if (d < pickD)
                {
                    pick = x;
                    pickD = d;
                }
            }
            return pick;
        }

        BuildingKind KindOf(int building)
        {
            foreach (var b in zoning?.buildings ?? new List<ZonedBuilding>()) if (b.buildingId == building) return b.kind;
            return BuildingKind.House;
        }

        /// <summary>
        /// Where someone at `from` goes shopping, by the Huff model (docs/town-shops.md): each shop's pull is its size over
        /// the distance squared, and they pick one in proportion, so a supermarket draws from further than a corner store.
        /// </summary>
        int Shop(int from, Rng rng)
        {
            if (!options.huffShops) return Nearest(shops, from, rng);
            var at = Door(Place.Building(from));
            double total = 0;
            var pull = new double[shops.Count];
            for (int k = 0; k < shops.Count; k++)
            {
                pull[k] = Porchlight.Gen.Retail.Pull(shopSize.TryGetValue(shops[k], out var size) ? size : 1, Geo.Dist(Door(Place.Building(shops[k])), at));
                total += pull[k];
            }
            double r = rng.Range(0, total);
            for (int k = 0; k < shops.Count; k++)
            {
                r -= pull[k];
                if (r < 0) return shops[k];
            }
            return shops[shops.Count - 1];
        }

        public Vec2 Door(Place p) => p.border ? net.borders[p.id].at : net.access.TryGetValue(p.id, out var a) ? a.door : new Vec2(0, 0);

        // --- Their days -------------------------------------------------------------------------

        /// <summary>A person's trips on day `day`, in order, each from where the last ended, ending where they live.</summary>
        public void PlanDay(Person p, long day)
        {
            p.plan.Clear();
            p.next = 0;
            p.day = day;
            var rng = new Rng($"{seed}::day::{day}::{p.id}");
            long H = TownClock.HOUR;
            long t = 0;
            var at = p.home;
            void Go(double hours, Place to, Purpose why)
            {
                long when = Math.Max((long)(hours * H), t + 5 * TownClock.MINUTE);
                if (when >= TownClock.DAY - 5 * TownClock.MINUTE || to.Same(at)) return;
                p.plan.Add(new Planned { depart = when, to = to, purpose = why });
                t = when;
                at = to;
            }
            double Hours() => t / (double)H;
            switch (p.kind)
            {
                case PersonKind.Worker:
                case PersonKind.CommuterIn:
                {
                    Go(rng.Range(6.5, 9.5), p.work, Purpose.Work);
                    double start = Hours();
                    if (!p.work.border && shops.Count > 0 && rng.Chance(0.3))
                    {
                        Go(start + rng.Range(3.5, 4.5), Place.Building(Nearest(shops, p.work.id, rng)), Purpose.Lunch);
                        Go(Hours() + rng.Range(0.5, 1), p.work, Purpose.Work);
                    }
                    double off = start + rng.Range(8, 9.5);
                    if (!p.home.border && shops.Count > 0 && rng.Chance(0.35))
                    {
                        Go(off, Place.Building(Shop(p.home.id, rng)), Purpose.Shop);
                        off = Hours() + rng.Range(0.3, 0.8);
                    }
                    Go(off, p.home, Purpose.Home);
                    if (!p.home.border && rng.Chance(BACKGROUND)) Visit(rng, p, Hours() + rng.Range(1, 3), Go, Hours);
                    break;
                }
                case PersonKind.Through:
                {
                    // In at one gate, out at another, and back later.
                    Go(rng.Chance(0.5) ? rng.Range(6.5, 9.5) : rng.Range(10, 15), p.work, Purpose.Through);
                    Go(Math.Max(Hours() + 0.5, rng.Range(15.5, 19.5)), p.home, Purpose.Through);
                    break;
                }
                case PersonKind.Student:
                {
                    Go(rng.Range(7.5, 8.5), p.work, Purpose.School);
                    Go(rng.Range(15, 16), p.home, Purpose.Home);
                    if (rng.Chance(0.5)) Visit(rng, p, rng.Range(16.5, 18), Go, Hours);
                    break;
                }
                default:
                {
                    if (shops.Count > 0) Go(rng.Range(9, 11.5), Place.Building(Shop(p.home.id, rng)), Purpose.Shop);
                    Go(Hours() + rng.Range(0.5, 1.5), p.home, Purpose.Home);
                    if (rng.Chance(0.5)) Visit(rng, p, rng.Range(13, 16.5), Go, Hours);
                    if (rng.Chance(BACKGROUND)) Visit(rng, p, rng.Range(Math.Max(Hours() + 0.5, 17), 21), Go, Hours);
                    break;
                }
            }
            // Always home by the end of the day.
            if (!at.Same(p.home))
            {
                long when = Math.Min(TownClock.DAY - 2 * TownClock.MINUTE, t + 30 * TownClock.MINUTE);
                p.plan.Add(new Planned { depart = when, to = p.home, purpose = Purpose.Home });
            }
        }

        /// <summary>Out to see someone (or a shop), and home again.</summary>
        void Visit(Rng rng, Person p, double hours, Action<double, Place, Purpose> go, Func<double> now)
        {
            if (homesList.Count == 0 || p.home.border) return;
            int where = rng.Chance(0.6) ? Nearest(homesList, p.home.id, rng) : shops.Count > 0 ? Shop(p.home.id, rng) : rng.Pick(homesList);
            go(hours, Place.Building(where), Purpose.Visit);
            go(now() + rng.Range(0.5, 2), p.home, Purpose.Home);
        }

        long DayStart(long day) => TownClock.At(day, 0);

        /// <summary>Everyone where their day has them at `moment` (trips before it done, as if instantly), the next trip of each due.</summary>
        void Settle(long moment)
        {
            long day = TownClock.DayOf(moment);
            long tod = TownClock.TimeOfDay(moment);
            foreach (var p in people)
            {
                Count(p, -1);
                PlanDay(p, day);
                while (p.next < p.plan.Count && p.plan[p.next].depart < tod)
                {
                    var to = p.plan[p.next].to;
                    var c = p.car >= 0 ? cars[p.car] : null;
                    if (c != null && WouldDrive(p, c, p.at, to)) ParkNear(c, to);
                    p.at = to;
                    p.next++;
                }
                p.where = p.at.border ? Where.OffMap : Where.Inside;
                Count(p, 1);
                ScheduleNext(p);
            }
        }

        void ScheduleNext(Person p)
        {
            if (p.next >= p.plan.Count)
            {
                // A car left where its driver gave up in the traffic (Gridlock.cs) is fetched home by morning.
                var c = p.car >= 0 ? cars[p.car] : null;
                if (c != null && !p.home.border && p.at.Same(p.home) && c.seg < 0 && !c.offMap && c.parkedBy >= 0 && c.parkedBy != p.home.id && !wantsOut.Contains(c.id))
                    ParkNear(c, p.home);
                PlanDay(p, p.day + 1);
            }
            if (p.plan.Count == 0)
            {
                // A day with nothing in it: look again tomorrow.
                p.day++;
                Schedule(K_TRIP, p.id, DayStart(p.day));
                p.next = 0;
                p.plan.Add(new Planned { depart = -1 });
                return;
            }
            Schedule(K_TRIP, p.id, DayStart(p.day) + p.plan[p.next].depart);
        }

        void OnTripDue(Person p)
        {
            if (p.next < p.plan.Count && p.plan[p.next].depart < 0)
            {
                // An empty day's placeholder.
                p.plan.Clear();
                ScheduleNext(p);
                return;
            }
            if (p.where == Where.OnTrip)
            {
                p.delayed = true;
                stats.delayedTrips++;
                return;
            }
            var planned = p.plan[p.next];
            p.next++;
            ScheduleNext(p);
            if (planned.to.Same(p.at)) return;
            StartTrip(p, planned.to, planned.purpose);
        }

        /// <summary>Would they take their car from `from` to `to`?</summary>
        bool WouldDrive(Person p, Car c, Place from, Place to)
        {
            if (from.border || to.border) return true;
            if (Geo.Dist(Door(from), Door(to)) < WALK_TILES) return false;
            return !c.offMap && c.parkedBy == from.id;
        }

        void StartTrip(Person p, Place to, Purpose why)
        {
            var from = p.at;
            var trip = new Trip { id = (int)stats.started, from = from, to = to, purpose = why, started = Now };
            var c = p.car >= 0 ? cars[p.car] : null;
            trip.why.gate = from.border || to.border;
            if ((from.border || to.border) && c == null)
            {
                // Through a gate needs a car: anyone without one has one waiting there.
                GiveCar(p, from);
                c = cars[p.car];
                trip.why.given = true;
            }
            if (c != null && from.border && !c.offMap) ParkNear(c, from);
            trip.why.hasCar = c != null;
            trip.why.dist = Geo.Dist(Door(from), Door(to));
            trip.why.carHere = c != null && !c.offMap && c.parkedBy == from.id;
            if (c != null && WouldDrive(p, c, from, to))
            {
                trip.mode = Mode.Drive;
                if (!from.border) trip.legs.Add(new Leg { kind = LegKind.Walk, from = Door(from) });
                trip.legs.Add(new Leg { kind = LegKind.Drive, dest = to });
                if (!to.border) trip.legs.Add(new Leg { kind = LegKind.Walk, to = Door(to) });
            }
            else if (BusBetter(p, Door(from), Door(to), out int board, out int alight, ref trip.why))
            {
                trip.mode = Mode.Bus;
                trip.legs.Add(new Leg { kind = LegKind.Walk, from = Door(from), to = busStops[board].at });
                trip.legs.Add(new Leg { kind = LegKind.Wait, route = 0, board = board, alight = alight });
                trip.legs.Add(new Leg { kind = LegKind.Walk, from = busStops[alight].at, to = Door(to) });
            }
            else
            {
                trip.mode = Mode.Walk;
                trip.legs.Add(new Leg { kind = LegKind.Walk, from = Door(from), to = Door(to) });
            }
            p.trip = trip;
            Count(p, -1);
            p.where = Where.OnTrip;
            stats.Started(trip, Now);
            NextLeg(p);
        }

        void NextLeg(Person p)
        {
            var trip = p.trip;
            if (trip == null) return;
            trip.leg++;
            if (trip.leg >= trip.legs.Count)
            {
                EndTrip(p, true);
                return;
            }
            var leg = trip.legs[trip.leg];
            var c = p.car >= 0 ? cars[p.car] : null;
            switch (leg.kind)
            {
                case LegKind.Walk:
                {
                    // To the car or from it: where it stands is known only now.
                    bool toCar = trip.leg + 1 < trip.legs.Count && trip.legs[trip.leg + 1].kind == LegKind.Drive;
                    bool fromCar = trip.leg > 0 && trip.legs[trip.leg - 1].kind == LegKind.Drive;
                    var from = fromCar && c != null ? CarDoor(c) : leg.from;
                    var to = toCar && c != null ? CarDoor(c) : leg.to;
                    // Off to their car: it's drawn where it's parked as they walk up to it.
                    if (toCar && c != null) ShowParked(c, true);
                    StartWalk(p, from, to);
                    break;
                }
                case LegKind.Drive:
                    FreePersonSlot(p);
                    if (c == null || !Depart(c, trip.from, leg.dest)) Fail(p, "no way by road");
                    break;
                case LegKind.Wait:
                    WaitForBus(p, leg);
                    break;
            }
        }

        /// <summary>The car's parked (or gone off the map): on to the next leg.</summary>
        void DriveDone(Person p)
        {
            if (p.trip == null) return;
            NextLeg(p);
        }

        void EndTrip(Person p, bool made)
        {
            var trip = p.trip;
            p.trip = null;
            StopWalking(p);
            FreePersonSlot(p);
            if (trip != null)
            {
                p.at = trip.to;
                if (made) stats.Finished(trip, Now);
                trip.ended = Now;
                trip.made = made;
                // Kept only to say how it went: its legs (and their points) aren't.
                trip.legs.Clear();
                trip.legs.TrimExcess();
                if (p.recent == null) p.recent = new List<Trip>(RECENT);
                if (p.recent.Count >= RECENT) p.recent.RemoveAt(0);
                p.recent.Add(trip);
            }
            p.where = p.at.border ? Where.OffMap : Where.Inside;
            Count(p, 1);
            if (p.delayed)
            {
                p.delayed = false;
                Schedule(K_TRIP, p.id, Now);
            }
        }

        /// <summary>A trip that can't be made: they're put where they were going, and their car near it.</summary>
        void Fail(Person p, string why)
        {
            var trip = p.trip;
            if (trip == null) return;
            trip.failed = why;
            stats.Failed(trip, why, Now);
            LeaveBus(p);
            if (p.car >= 0 && trip.mode == Mode.Drive)
            {
                var c = cars[p.car];
                int drive = trip.legs.FindIndex(l => l.kind == LegKind.Drive);
                if (trip.leg <= drive)
                {
                    if (c.target >= 0 && spotHolder[c.target] == c.id) spotHolder[c.target] = -1;
                    c.target = -1;
                    if (c.seg >= 0 || wantsOut.Contains(c.id)) RemoveFromRoad(c);
                    ParkNear(c, trip.to);
                }
            }
            EndTrip(p, false);
        }
    }
}

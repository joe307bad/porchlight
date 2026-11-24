// A town's life: its people going about their day (to work, school and the shops
// and home again, on foot, by car or by bus), their cars on the roads, queueing at
// junctions and parking, and the buses on their loop. It's driven by events, not
// ticks: everything sits in one timetable (Core/EventQueue) and is only worked on
// when something happens to it. A car crossing a road is just the time it set off
// and the time it'll reach the end; where it is in between is worked out only when
// someone asks (to draw it). Someone indoors is just the time they next go out.
//
// The parts:
//   TownLife.cs   the clock, the timetable, what's drawn (the wire's slots)
//   Cars.cs       lanes as queues, junctions, parking
//   People.cs     who lives and works here, their days, their trips
//   Walking.cs    on foot, crossing at junctions
//   Buses.cs      the bus loop
//   Analytics.cs  counts for Manage and the checks
//
// docs/town-life.md explains it all. Pure, and the same from the same town on
// every machine.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;

namespace Porchlight.Life
{
    public sealed class LifeOptions
    {
        /// <summary>Park anywhere, as many as like: no spots (gets trips going without parking).</summary>
        public bool infiniteParking;
        public bool buses = true;
        /// <summary>Vans on their rounds (Rounds.cs).</summary>
        public bool vans = true;
        /// <summary>Routes by the traffic as measured, and drivers held up thinking again (Congestion.cs).</summary>
        public bool liveRoutes = true;
        /// <summary>Drivers keep to the ways they know, and one in ten a day works theirs out afresh (Congestion.HabitualRoute).</summary>
        public bool habits = true;
        /// <summary>Bin lorries on their rounds, and ambulances and a fire engine on calls (Services.cs).</summary>
        public bool services = true;
        /// <summary>Emergency calls this many times as often as the national rates (the checks, to see plenty).</summary>
        public double callRate = 1;
        /// <summary>A parking spot near the door held from setting off (Cars.cs Plan), not looked for on arrival.</summary>
        public bool reserveSpots = true;
        /// <summary>Drivers let a car waiting to pull out in (Cars.cs PullOut).</summary>
        public bool letIn = true;
        /// <summary>A signal with nobody coming the way that's green lets the other way go (Cars.cs Judge).</summary>
        public bool skipEmpty = true;
        /// <summary>Errands to places near home more than far (People.cs PlanDay).</summary>
        public bool nearErrands = true;
        /// <summary>Shoppers choose a shop by the Huff model (its size over the distance squared), not the nearest of a few.</summary>
        public bool huffShops = true;
        /// <summary>How many cars and people can be drawn at once (the wire's slots); -1: sized to the town.</summary>
        public int carSlots = -1, peopleSlots = -1;
        /// <summary>How long before the start to begin, so trips under way at the start are on the road.</summary>
        public long warmup = 20 * TownClock.MINUTE;
    }

    /// <summary>Where something's drawn: hidden, or at (x, y) facing `heading` (cars (cos h, sin h); people (sin h, cos h)).</summary>
    public struct Pose
    {
        public bool shown;
        public double x, y, heading;
        /// <summary>Who's in the slot (a car's or person's id), -1 if nobody.</summary>
        public int who;
    }

    public sealed partial class TownLife
    {
        const long K_CAR = 1, K_CLEAR = 2, K_TRIP = 3, K_WALK = 4, K_BUS = 5, K_VAN = 6, K_SWEEP = 7, K_RETIME = 8, K_SERVICE = 9, K_SIREN = 10;

        public readonly TownModel model;
        public readonly TownZoning zoning;
        public readonly RoadNet net;
        public readonly Router router;
        public readonly Walkways walkways;
        public readonly LifeOptions options;
        public readonly string seed;
        /// <summary>The seed as a number, the same on every machine (string.GetHashCode isn't).</summary>
        readonly long seedHash;

        public readonly List<Car> cars = new List<Car>();
        public readonly List<Person> people = new List<Person>();
        readonly Queue[] queues;
        readonly EventQueue events = new EventQueue();

        /// <summary>The moment the town's life has reached (ms since 1970).</summary>
        /// <summary>The town clock where its life has got to (ms since 1970). Moving it on stales every car's remembered front.</summary>
        public long Now
        {
            get => now;
            private set
            {
                if (value == now) return;
                now = value;
                version++;
            }
        }
        long now;
        public bool Started { get; private set; }

        /// <summary>The wire's slots: what's drawn in each.</summary>
        public readonly Pose[] carSlots, personSlots;
        readonly Stack<int> freeCarSlots = new Stack<int>(), freePersonSlots = new Stack<int>();
        /// <summary>Slots the player's taken (their car): not given to anyone else until let go.</summary>
        readonly HashSet<int> takenSlots = new HashSet<int>();
        /// <summary>Buses' slots come last, drawn as a bus.</summary>
        public readonly int busSlotsFrom;
        /// <summary>The service vehicles' slots (one each: bin lorries, then emergency vehicles) start here, after the buses'.</summary>
        public readonly int serviceSlotsFrom;

        /// <summary>The model slot `slot` is drawn as: a bus's or a service vehicle's own, or null (any car: the drawing picks).</summary>
        public string SlotModel(int slot)
        {
            if (slot >= serviceSlotsFrom && slot - serviceSlotsFrom < services.Count) return ServiceModel(services[slot - serviceSlotsFrom].kind);
            if (slot >= busSlotsFrom && slot < serviceSlotsFrom) return Traffic.BUS_MODEL;
            return null;
        }

        public TownLife(TownModel model, IsoLayout layout, TownZoning zoning, LifeOptions options = null)
        {
            this.model = model;
            this.zoning = zoning;
            this.options = options ?? new LifeOptions();
            seed = model?.seed ?? "town";
            ulong sh = 1469598103934665603UL;
            foreach (char ch in seed)
            {
                sh ^= ch;
                sh *= 1099511628211UL;
            }
            seedHash = (long)sh;
            net = new RoadNet(model, layout);
            router = new Router(net);
            walkways = new Walkways(layout.sidewalks ?? new SidewalkNetwork(), net);
            queues = new Queue[net.lanes.Count + net.movements.Count];
            for (int i = 0; i < queues.Length; i++) queues[i] = new Queue { length = SegLength(i) };
            spotHolder = new int[net.spots.Count];
            for (int i = 0; i < spotHolder.Length; i++) spotHolder[i] = -1;
            MakePeople();
            MakeBusRoute();
            if (this.options.vans) MakeVans();
            if (this.options.services) MakeServices();
            int carN = this.options.carSlots >= 0 ? this.options.carSlots : CarSlotsFor(people.Count) + vans.Count;
            int personN = this.options.peopleSlots >= 0 ? this.options.peopleSlots : PersonSlotsFor(people.Count);
            int busN = busRoute != null ? buses.Count : 0;
            busSlotsFrom = carN;
            serviceSlotsFrom = carN + busN;
            carSlots = new Pose[carN + busN + services.Count];
            personSlots = new Pose[personN];
            for (int i = 0; i < carSlots.Length; i++) carSlots[i].who = -1;
            for (int i = 0; i < personSlots.Length; i++) personSlots[i].who = -1;
            for (int i = carN - 1; i >= 0; i--) freeCarSlots.Push(i);
            for (int i = personN - 1; i >= 0; i--) freePersonSlots.Push(i);
            for (int k = 0; k < busN; k++)
            {
                buses[k].slot = carN + k;
                carSlots[carN + k].who = buses[k].id;
            }
            for (int k = 0; k < services.Count; k++)
            {
                services[k].car.slot = serviceSlotsFrom + k;
                carSlots[serviceSlotsFrom + k].who = services[k].car.id;
            }
            frontMemo = new double[cars.Count + 16];
            frontStamp = new long[cars.Count + 16];
        }

        /// <summary>The most wanderers a town has: townsfolk strolling with nowhere to be (Pedestrian), besides those on their way somewhere.</summary>
        public const int MOST_WANDERERS = 24;

        /// <summary>How many wanderers a town with this much sidewalk (tiles) has.</summary>
        public static int Wanderers(double walkable) => (int)Math.Min(MOST_WANDERERS, Math.Floor(walkable / 48));

        /// <summary>Car slots for a town of this many people: enough for its busiest hour, with room to spare.</summary>
        public static int CarSlotsFor(int population) => Math.Max(12, Math.Min(900, (int)Math.Ceiling(population * 0.05)));

        public static int PersonSlotsFor(int population) => Math.Max(16, Math.Min(1200, (int)Math.Ceiling(population * 0.06)));

        // --- Time ------------------------------------------------------------------------------

        /// <summary>
        /// Begin at `moment`: everyone where their day has them a little before it (options.warmup), then
        /// run up to it, so the trips under way then are on the road.
        /// </summary>
        public void Start(long moment)
        {
            Started = true;
            long from = moment - options.warmup;
            Now = from;
            Settle(from);
            StartBuses(from);
            StartVans(from);
            StartServices(from);
            Schedule(K_SWEEP, 0, from + TownClock.MINUTE);
            countedSince = from;
            Schedule(K_RETIME, 0, NextQuarter(from));
            RunUntil(moment);
        }

        /// <summary>Everything that happens up to `moment`, in order.</summary>
        public void RunUntil(long moment)
        {
            if (!Started) Start(moment);
            while (events.TryPop(moment, out long key, out long at))
            {
                if (at > Now)
                {
                    Now = at;
                    version++;
                }
                long kind = key >> 32;
                int id = (int)(key & 0xffffffffL);
                Handle(kind, id);
                handled++;
            }
            if (moment > Now)
            {
                Now = moment;
                version++;
            }
        }

        /// <summary>Events handled so far.</summary>
        public long handled;

        /// <summary>Events waiting in the timetable.</summary>
        public int Pending => events.Count;

        static long Key(long kind, int id) => (kind << 32) | (uint)id;

        void Schedule(long kind, int id, long at) => events.Schedule(Key(kind, id), Math.Max(at, Now));

        void Cancel(long kind, int id) => events.Cancel(Key(kind, id));

        void Handle(long kind, int id)
        {
            switch (kind)
            {
                case K_CAR: OnCar(cars[id]); break;
                case K_CLEAR: OnClear(cars[id]); break;
                case K_TRIP: OnTripDue(people[id]); break;
                case K_WALK: OnWalk(people[id]); break;
                case K_BUS: OnBusDue(id); break;
                case K_VAN: OnVanDue(id); break;
                case K_SWEEP: Sweep(); break;
                case K_RETIME: Retime(); break;
                case K_SERVICE: OnServiceDue(id); break;
                case K_SIREN: SirenTick(id); break;
            }
        }

        // --- What's drawn -----------------------------------------------------------------------

        /// <summary>
        /// Parked cars still drawn at their spot, longest parked first. A parked car keeps its slot (its driver gets
        /// out and walks off from it, or walks up and gets in) while a car on the road doesn't need it.
        /// </summary>
        readonly List<int> parkedShown = new List<int>();

        int TakeCarSlot(Car c)
        {
            if (c.slot >= 0)
            {
                parkedShown.Remove(c.id);
                return c.slot;
            }
            if (freeCarSlots.Count == 0 && parkedShown.Count > 0) FreeCarSlot(cars[parkedShown[0]]);
            if (freeCarSlots.Count == 0)
            {
                stats.undrawnCars++;
                return -1;
            }
            c.slot = freeCarSlots.Pop();
            carSlots[c.slot].who = c.id;
            return c.slot;
        }

        /// <summary>Car `c`, parked in a spot: drawn there, if there's a slot free (or, `evict`, the longest parked one's).</summary>
        void ShowParked(Car c, bool evict)
        {
            if (c.spot < 0 || c.seg >= 0 || c.bus >= 0 || c.van >= 0 || c.offMap) return;
            if (c.slot < 0)
            {
                if (freeCarSlots.Count == 0 && (!evict || parkedShown.Count == 0)) return;
                if (TakeCarSlot(c) < 0) return;
            }
            if (!parkedShown.Contains(c.id)) parkedShown.Add(c.id);
        }

        void FreeCarSlot(Car c)
        {
            if (c.slot < 0 || c.bus >= 0 || c.service >= 0) return;
            parkedShown.Remove(c.id);
            carSlots[c.slot] = new Pose { who = -1 };
            if (!takenSlots.Contains(c.slot)) freeCarSlots.Push(c.slot);
            c.slot = -1;
        }

        int TakePersonSlot(Person p)
        {
            if (p.slot >= 0) return p.slot;
            if (freePersonSlots.Count == 0)
            {
                stats.undrawnPeople++;
                return -1;
            }
            p.slot = freePersonSlots.Pop();
            personSlots[p.slot].who = p.id;
            return p.slot;
        }

        void FreePersonSlot(Person p)
        {
            if (p.slot < 0) return;
            personSlots[p.slot] = new Pose { who = -1 };
            freePersonSlots.Push(p.slot);
            p.slot = -1;
        }

        /// <summary>Work out where everything drawn is now (into carSlots and personSlots).</summary>
        public void Draw()
        {
            for (int i = 0; i < carSlots.Length; i++)
            {
                int who = carSlots[i].who;
                if (who >= 0 && cars[who].seg < 0 && cars[who].spot >= 0 && !cars[who].offMap)
                {
                    // Parked: standing in its spot, facing the way its lane goes.
                    var spot = net.spots[cars[who].spot];
                    var way = net.LanePoint(net.lanes[spot.lane], spot.s).along;
                    carSlots[i] = new Pose { shown = true, x = spot.at.x, y = spot.at.y, heading = DetMath.Atan2(way.y, way.x), who = who };
                    continue;
                }
                if (who < 0 || cars[who].seg < 0)
                {
                    carSlots[i].shown = false;
                    continue;
                }
                var (at, along) = CarPoint(cars[who]);
                carSlots[i] = new Pose { shown = true, x = at.x, y = at.y, heading = DetMath.Atan2(along.y, along.x), who = who };
            }
            for (int i = 0; i < personSlots.Length; i++)
            {
                int who = personSlots[i].who;
                if (who < 0)
                {
                    personSlots[i].shown = false;
                    continue;
                }
                var p = people[who];
                var (at, along) = PersonPoint(p);
                personSlots[i] = new Pose { shown = true, x = at.x, y = at.y, heading = DetMath.Atan2(along.x, along.y), who = who };
            }
        }

        /// <summary>Where a car on the road is drawn: its middle, half its length behind its front.</summary>
        public (Vec2 at, Vec2 along) CarPoint(Car c)
        {
            // From its front wheels to its back, both on the way it's going: a road's bends, and the
            // step from a lane onto a turn, swing the car round over its length rather than at once.
            var front = PointBehindFront(c, 0.15).at;
            var back = PointBehindFront(c, c.length - 0.15).at;
            double dx = front.x - back.x, dy = front.y - back.y, l = Numbers.Hypot(dx, dy);
            if (l < 1e-6) return PointBehindFront(c, c.length / 2);
            var along = new Vec2(dx / l, dy / l);
            var mid = new Vec2((front.x + back.x) / 2, (front.y + back.y) / 2);
            // Pulled over for a siren: towards the kerb (the driver's right: see RoadNet.SideLane).
            double aside = Aside(c);
            if (aside > 0) mid = new Vec2(mid.x - along.y * aside, mid.y + along.x * aside);
            return (mid, along);
        }

        (Vec2 at, Vec2 along) PointBehindFront(Car c, double back)
        {
            double d = Front(c) - back;
            if (d >= 0) return SegPoint(c.seg, d);
            // Back over the way it came (a short turn's shorter than a car): the drawing's, not the queues'.
            if (c.route != null && c.step > 0 && c.step < c.route.Count && c.route[c.step] == c.seg)
            {
                int k = c.step - 1;
                for (; k >= 0; k--)
                {
                    d += SegLength(c.route[k]);
                    if (d >= 0 || k == 0) return SegPoint(c.route[k], Math.Max(0, d));
                }
            }
            if (c.laggy >= 0) return SegPoint(c.laggy, Math.Max(0, SegLength(c.laggy) + d));
            return SegPoint(c.seg, 0);
        }

        (Vec2 at, Vec2 along) SegPoint(int seg, double d)
        {
            if (seg < net.lanes.Count)
            {
                var lane = net.lanes[seg];
                return net.LanePoint(lane, lane.start + Math.Max(0, Math.Min(lane.Span, d)));
            }
            return net.MovementPoint(net.movements[seg - net.lanes.Count], d);
        }

        // --- The player -------------------------------------------------------------------------

        /// <summary>The car drawn in slot `slot` is the player's now: off the road, its owner's trip given up (they're put where they were going).</summary>
        public void TakeSlot(int slot)
        {
            if (slot < 0 || slot >= carSlots.Length) return;
            takenSlots.Add(slot);
            int who = carSlots[slot].who;
            if (who < 0) return;
            var c = cars[who];
            if (c.bus >= 0)
            {
                TakeBus(c);
                return;
            }
            if (c.service >= 0)
            {
                TakeService(c);
                return;
            }
            RemoveFromRoad(c);
            c.slot = -1;
            carSlots[slot] = new Pose { who = -1 };
            // Taken from where it was parked: the spot's free.
            if (c.spot >= 0 && spotHolder[c.spot] == c.id) spotHolder[c.spot] = -1;
            c.spot = -1;
            stats.taken++;
            // A van: another comes out on its rounds a little later.
            if (c.van >= 0) Schedule(K_VAN, c.van, Now + 10 * TownClock.MINUTE);
            if (c.owner >= 0)
            {
                var p = people[c.owner];
                // They'll have a new one at home.
                c.owner = -1;
                p.car = -1;
                if (p.hasCar) GiveCar(p, p.home);
                if (p.trip != null) Fail(p, "their car was taken");
            }
        }

        /// <summary>The player's let go of every car they took: their slots are free again.</summary>
        public void ReleaseSlots()
        {
            foreach (int s in takenSlots)
            {
                if (s >= serviceSlotsFrom) RestoreService(s);
                else if (s >= busSlotsFrom) RestoreBus(s);
                else if (carSlots[s].who < 0) freeCarSlots.Push(s);
            }
            takenSlots.Clear();
        }

        public bool IsTaken(int slot) => takenSlots.Contains(slot);
    }
}

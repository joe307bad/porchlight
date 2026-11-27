// Manage's focus view (docs/town-life.md §11): one person or one car, followed
// by who they are rather than the slot they're drawn in, with
//
//   - their decision tree: each question their day and the town's rules asked
//     to get them doing what they're doing now, the answer, and the figures it
//     went by (why this trip, why this way of getting there, and for a car the
//     junction's rules in the order it asks them: Judge, the very code that
//     decides);
//   - their decision queue: what comes up next, in order, with when where it's
//     known (the rest of this trip's legs, the junctions ahead and where the car
//     will stop, the rest of the day's plan);
//   - where they are now and the way ahead (to draw on the map).
//
// Read-only: looking never changes the town. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    /// <summary>
    /// One question in a decision tree: its answer, what it went by, and (a yes-or-no decision, or a check that
    /// passed or didn't) which way it went; a fact along the way (Info) has no way it went.
    /// </summary>
    public sealed class Why
    {
        public readonly string question, answer, note;
        public readonly bool? yes;
        public List<Why> under;

        public Why(string question, string answer, bool yes, string note = null) : this(question, answer, note, yes) { }

        Why(string question, string answer, string note, bool? yes)
        {
            this.question = question;
            this.answer = answer;
            this.yes = yes;
            this.note = note;
        }

        /// <summary>A fact, not a decision: how long, how fast, which way, who.</summary>
        public static Why Info(string question, string answer, string note = null) => new Why(question, answer, note, null);

        public Why Add(Why w)
        {
            (under ??= new List<Why>()).Add(w);
            return this;
        }

        public Dictionary<string, object> ToMap()
        {
            var o = new Dictionary<string, object> { ["q"] = question, ["a"] = answer };
            if (yes != null) o["yes"] = yes.Value;
            if (note != null) o["note"] = note;
            if (under != null) o["under"] = under.ConvertAll(w => w.ToMap());
            return o;
        }
    }

    public sealed partial class TownLife
    {
        /// <summary>The way ahead drawn for a focus: at most this many points.</summary>
        const int FOCUS_POINTS = 400;
        /// <summary>The junctions ahead listed in a car's queue: at most this many.</summary>
        const int FOCUS_JUNCTIONS = 12;

        /// <summary>Who's drawn in person slot `slot` now (-1: nobody).</summary>
        public int PersonInSlot(int slot) => slot >= 0 && slot < personSlots.Length ? personSlots[slot].who : -1;

        /// <summary>Who's drawn in car slot `slot` now (-1: nobody).</summary>
        public int CarInSlot(int slot) => slot >= 0 && slot < carSlots.Length ? carSlots[slot].who : -1;

        /// <summary>
        /// One person in focus. `building` turns a building id into how the caller knows it (Manage: its place in
        /// zoning's list); null: the id.
        /// </summary>
        public Dictionary<string, object> FocusPerson(int id, Func<int, int> building = null)
        {
            if (id < 0 || id >= people.Count) return null;
            var p = people[id];
            var o = new Dictionary<string, object>
            {
                ["kind"] = "person",
                ["id"] = p.id,
                ["clock"] = Now,
                ["time"] = TownClock.Format(Now),
                ["name"] = NameOf(p.id),
                ["who"] = KindText(p.kind),
                ["household"] = HouseholdText(p),
                ["home"] = PlaceMap(p.home, building),
                ["work"] = p.kind == PersonKind.AtHome ? null : PlaceMap(p.work, building),
                ["car"] = p.car,
                ["where"] = p.where.ToString(),
            };
            var tree = new List<Why>();
            var queue = new List<Dictionary<string, object>>();
            var path = new List<Vec2>();
            Vec2? dest = null;
            var c = p.car >= 0 ? cars[p.car] : null;
            int busCar = riding.TryGetValue(p.id, out int bc) ? bc : -1;

            // Where they are, and what they're drawn as.
            Vec2 at;
            string drawn;
            int slot = -1;
            if (p.slot >= 0 && personSlots[p.slot].shown)
            {
                at = new Vec2(personSlots[p.slot].x, personSlots[p.slot].y);
                drawn = "person";
                slot = p.slot;
            }
            else if (busCar >= 0 && cars[busCar].slot >= 0)
            {
                at = cars[busCar].seg >= 0 ? CarPoint(cars[busCar]).at : Door(p.at);
                drawn = "car";
                slot = cars[busCar].slot;
            }
            else if (c != null && c.seg >= 0)
            {
                at = CarPoint(c).at;
                drawn = c.slot >= 0 ? "car" : "none";
                slot = c.slot;
            }
            else
            {
                at = Door(p.at);
                drawn = "none";
            }
            o["carSlot"] = c != null && (c.seg >= 0 || ParkedShown(c)) ? c.slot : -1;
            o["at"] = new[] { Round(at.x), Round(at.y) };
            o["drawn"] = drawn;
            o["slot"] = slot;

            var trip = p.trip;
            if (trip != null)
            {
                o["doing"] = $"{PurposeText(trip.purpose)}, by {ModeText(trip.mode)}";
                tree.Add(WhyOut(p, trip, building));
                tree.Add(WhyMode(trip));
                tree.Add(WhyNow(p, trip, c, busCar, building));
                dest = Door(trip.to);
                // The rest of this trip.
                // When this leg's done (about): known for a walk, a drive, a bus to come; the legs after, only that far.
                long done = trip.leg >= 0 && trip.leg < trip.legs.Count ? LegDone(p, trip.legs[trip.leg], c, busCar) : -1;
                for (int k = Math.Max(0, trip.leg); k < trip.legs.Count; k++)
                    queue.Add(Item(k == trip.leg ? Now : k == trip.leg + 1 ? done : -1, LegText(p, trip, k, building), k == trip.leg));
                queue.Add(Item(trip.leg == trip.legs.Count - 1 ? done : -1, $"Arrive: {PlaceText(trip.to, building)}", false));
                // The way ahead.
                var leg0 = trip.leg >= 0 && trip.leg < trip.legs.Count ? trip.legs[trip.leg] : null;
                if (leg0 != null && leg0.kind == LegKind.Drive && c != null && c.seg >= 0) CarPath(c, path);
                else if (p.walk != null) WalkPath(p, path);
            }
            else
            {
                o["doing"] = p.where == Where.OffMap ? "Out of town" : !p.at.border && p.at.id == MARKET ? (p.kind == PersonKind.Stallholder ? "At their stall" : "At the market") : "Indoors";
                tree.Add(Why.Info("Where are they?", p.where == Where.OffMap ? $"Out of town, past {PlaceText(p.at, building)}" : p.at.id == MARKET && !p.at.border ? (p.kind == PersonKind.Stallholder ? "At their stall at the market" : "At the market, browsing the stalls") : $"Indoors: {PlaceText(p.at, building)}",
                    p.at.Same(p.home) ? "Home." : p.at.Same(p.work) ? (p.kind == PersonKind.Student ? "At school." : "At work.") : null));
                if (p.waitingFor >= 0) tree.Add(new Why("Waiting for someone?", $"Yes: for {NameOf(p.waitingFor)} to take them", false, $"They wait up to {RIDE_WAIT / TownClock.MINUTE} minutes, then go on their own."));
                if (p.next < p.plan.Count && p.plan[p.next].depart >= 0)
                {
                    var n = p.plan[p.next];
                    tree.Add(Why.Info("When do they go out next?", $"{TownClock.Format(DayStart(p.day) + n.depart)}, {Lower(PurposeText(n.purpose))}: {PlaceText(n.to, building)}", DayRule(p)));
                }
            }
            // Their next planned trip (the rest of the day is in `day`). A trip due before the one they're on is done
            // goes as soon as they're there (OnTripDue's delayed).
            long after = Now;
            foreach (var item in queue) if (item["at"] is long t && t > after) after = t;
            for (int k = p.next; k < p.plan.Count; k++)
            {
                var n = p.plan[k];
                if (n.depart < 0) continue;
                long due = DayStart(p.day) + n.depart;
                string what = $"Set off, {Lower(PurposeText(n.purpose))}: {PlaceText(n.to, building)}";
                if (due < after) queue.Add(Item(after, $"{what} (due {TownClock.Format(due)}: as soon as they're there)", false));
                else queue.Add(Item(due, what, false, seconds: false));
                break;
            }
            o["tree"] = tree.ConvertAll(w => w.ToMap());
            o["queue"] = queue;
            o["path"] = Points(path);
            o["dest"] = dest == null ? null : new[] { Round(dest.Value.x), Round(dest.Value.y) };
            o["day"] = Day(p, building);
            if (p.recent != null)
                o["recent"] = p.recent.ConvertAll(t => new Dictionary<string, object>
                {
                    ["purpose"] = PurposeText(t.purpose),
                    ["mode"] = ModeText(t.mode),
                    ["to"] = PlaceText(t.to, building),
                    ["started"] = TownClock.Format(t.started),
                    ["ended"] = t.ended >= 0 ? TownClock.Format(t.ended) : null,
                    ["made"] = t.made,
                    ["failed"] = t.failed,
                });
            return o;
        }

        /// <summary>One car (or bus) in focus.</summary>
        public Dictionary<string, object> FocusCar(int id, Func<int, int> building = null)
        {
            if (id < 0 || id >= cars.Count) return null;
            var c = cars[id];
            var o = new Dictionary<string, object>
            {
                ["kind"] = "car",
                ["id"] = c.id,
                ["clock"] = Now,
                ["time"] = TownClock.Format(Now),
                ["who"] = c.bus >= 0 ? "The town bus" : c.service >= 0 ? ServiceText(c) : c.van >= 0 ? "A van on its rounds" : "A car",
                ["service"] = c.service >= 0 ? services[c.service].kind.ToString() : null,
                ["siren"] = c.siren,
                ["owner"] = c.owner,
                ["ownerName"] = c.owner >= 0 ? NameOf(c.owner) : null,
                ["bus"] = c.bus,
                ["van"] = c.van,
                ["state"] = c.state.ToString(),
                ["slot"] = c.seg >= 0 || c.bus >= 0 || ParkedShown(c) ? c.slot : -1,
            };
            var at = c.seg >= 0 ? CarPoint(c).at : c.spot >= 0 ? net.spots[c.spot].at : c.offMap ? GateNear(c) : Door(Place.Building(c.parkedBy >= 0 ? c.parkedBy : c.dest.id));
            o["at"] = new[] { Round(at.x), Round(at.y) };
            o["drawn"] = (c.seg >= 0 || ParkedShown(c)) && c.slot >= 0 ? "car" : "none";
            var tree = new List<Why>();
            var queue = new List<Dictionary<string, object>>();
            var path = new List<Vec2>();
            Vec2? dest = null;
            CarTree(c, tree, building);
            if (c.seg >= 0 && c.route != null)
            {
                CarQueue(c, queue, building);
                CarPath(c, path);
                dest = EndOf(c);
            }
            // Parked (drawn in its bay) or off the map: no route, but its queue still starts with what it's doing now.
            else queue.Add(Item(Now, CarDoing(c, building), true));
            o["doing"] = CarDoing(c, building);
            // Whose it is: their day is what sends it out and brings it home.
            if (c.owner >= 0) o["day"] = Day(people[c.owner], building);
            o["tree"] = tree.ConvertAll(w => w.ToMap());
            o["queue"] = queue;
            o["path"] = Points(path);
            o["dest"] = dest == null ? null : new[] { Round(dest.Value.x), Round(dest.Value.y) };
            return o;
        }

        // --- A day, and a line ---------------------------------------------------------------------

        /// <summary>
        /// Where they are in their day: today's trips so far (how each went), the one under way, the ones still to
        /// come (and tomorrow's, once that's planned), who they are and the rule their day was drawn up by, and in
        /// a sentence what happens next and what decides it.
        /// </summary>
        Dictionary<string, object> Day(Person p, Func<int, int> building)
        {
            long today = TownClock.DayOf(Now);
            var entries = new List<Dictionary<string, object>>();
            Dictionary<string, object> Entry(long at, Purpose why, Place to, string mode, string status, string note = null) => new Dictionary<string, object>
            {
                ["at"] = at,
                ["time"] = TownClock.Format(at),
                ["tomorrow"] = TownClock.DayOf(at) > today,
                ["what"] = $"{PurposeText(why)}: {PlaceText(to, building)}",
                ["mode"] = mode,
                ["status"] = status,
                ["note"] = note,
            };
            if (p.recent != null)
                foreach (var t in p.recent)
                {
                    if (TownClock.DayOf(t.started) != today) continue;
                    string note = t.made ? $"there at {TownClock.Format(t.ended)}" : $"couldn't be made{(t.failed != null ? $": {t.failed}" : "")}";
                    entries.Add(Entry(t.started, t.purpose, t.to, $"by {ModeText(t.mode)}", t.made ? "done" : "failed", note));
                }
            if (p.trip != null) entries.Add(Entry(p.trip.started, p.trip.purpose, p.trip.to, $"by {ModeText(p.trip.mode)}", "now", LegsText(p)));
            long nextAt = -1;
            Planned? next = null;
            for (int k = p.next; k < p.plan.Count; k++)
            {
                var n = p.plan[k];
                if (n.depart < 0) continue;
                long due = DayStart(p.day) + n.depart;
                if (next == null)
                {
                    next = n;
                    nextAt = due;
                }
                entries.Add(Entry(due, n.purpose, n.to, null, next.Value.depart == n.depart && nextAt == due ? "next" : "later"));
            }
            string decides;
            string nextText = next == null ? "" : $"{(TownClock.DayOf(nextAt) > today ? "tomorrow " : "")}at {TownClock.Format(nextAt)}, {Lower(PurposeText(next.Value.purpose))} ({PlaceText(next.Value.to, building)})";
            if (p.trip != null)
                decides = $"They're on their way: each leg ends when they get there, and the next starts. Then their plan's next trip is due {nextText}" +
                          (next != null && nextAt < Now + 60_000 ? "; if it's due before they're back, they set off again as soon as they are." : ".");
            else if (next != null)
                decides = $"Their plan: they set off {nextText}. Nothing else moves them: the plan was drawn up at midnight from who they are, and each trip goes when it's due (or as soon as they're back, if they're still out).";
            else decides = "Nothing more planned: the next day's plan is drawn up at midnight.";
            return new Dictionary<string, object>
            {
                ["who"] = KindText(p.kind),
                ["rule"] = DayRule(p),
                ["entries"] = entries,
                ["decides"] = decides,
            };
        }

        /// <summary>Where they are in the trip under way: "leg 2 of 3, driving".</summary>
        string LegsText(Person p)
        {
            var t = p.trip;
            if (t == null || t.leg < 0 || t.leg >= t.legs.Count) return null;
            return $"leg {t.leg + 1} of {t.legs.Count}: {Lower(LegText(p, t, t.leg, null))}";
        }

        /// <summary>Their name (PersonNames): the same everywhere, every day.</summary>
        public string NameOf(int id) => PersonNames.Of(seed, id);

        /// <summary>
        /// What they're doing, in a few words, for the tag over them in the game: "Going to work", "Going home",
        /// "Going to the shops"; out of a trip, where they are.
        /// </summary>
        public string DoingOf(int id)
        {
            if (id < 0 || id >= people.Count) return null;
            var p = people[id];
            return p.trip != null ? PurposeText(p.trip.purpose) : p.where == Where.OffMap ? "Out of town" : !p.at.border && p.at.id == MARKET ? (p.kind == PersonKind.Stallholder ? "At their stall" : "At the market") : "Indoors";
        }

        /// <summary>A line for the map's hover: who, what they're doing, and where in their day.</summary>
        public string BriefPerson(int id)
        {
            if (id < 0 || id >= people.Count) return null;
            var p = people[id];
            long today = TownClock.DayOf(Now);
            int done = 0, left = 0;
            if (p.recent != null) foreach (var t in p.recent) if (TownClock.DayOf(t.started) == today) done++;
            for (int k = p.next; k < p.plan.Count; k++) if (p.plan[k].depart >= 0 && TownClock.DayOf(DayStart(p.day) + p.plan[k].depart) == today) left++;
            string doing = p.trip != null ? $"{PurposeText(p.trip.purpose)}, by {ModeText(p.trip.mode)}" : p.where == Where.OffMap ? "Out of town" : !p.at.border && p.at.id == MARKET ? (p.kind == PersonKind.Stallholder ? "At their stall" : "At the market") : "Indoors";
            string where = p.trip != null ? $"trip {done + 1} of {done + 1 + left} today" : $"{done} trip{(done == 1 ? "" : "s")} today, {left} to go";
            return $"{NameOf(p.id)} · {doing} · {where}";
        }

        /// <summary>A line for the map's hover: the car, what it's doing, and whose errand.</summary>
        public string BriefCar(int id)
        {
            if (id < 0 || id >= cars.Count) return null;
            var c = cars[id];
            if (c.bus >= 0) return $"The bus · {CarDoing(c, null)}";
            if (c.van >= 0) return $"Van #{c.van} · {CarDoing(c, null)} · on its rounds";
            if (c.service >= 0) return $"{ServiceText(c)} · {CarDoing(c, null)}";
            string doing = CarDoing(c, null);
            if (c.owner < 0) return $"Car #{c.id} · {doing}";
            var o = people[c.owner];
            return $"Car #{c.id} · {doing} · {NameOf(o.id)}{(o.trip != null ? $", {Lower(PurposeText(o.trip.purpose))}" : "")}";
        }

        // --- People's trees -----------------------------------------------------------------------

        Why WhyOut(Person p, Trip trip, Func<int, int> building)
        {
            var w = Why.Info("Why are they out?", $"{PurposeText(trip.purpose)}: {PlaceText(trip.to, building)}", DayRule(p));
            w.Add(Why.Info("When did they set off?", TownClock.Format(trip.started), "When their day's plan had them go (drawn up at midnight from who they are)."));
            if (p.delayed) w.Add(new Why("Is their next trip waiting on this one?", "Yes: it came due while they were still out; they set off again as soon as they're there", false));
            return w;
        }

        Why WhyMode(Trip trip)
        {
            var y = trip.why;
            var w = Why.Info("How are they getting there?", $"By {ModeText(trip.mode)}");
            if (y.gate)
            {
                w.Add(new Why("In or out of town by a gate?", "Yes: that needs a car", true, "The roads beyond the gates are driven."));
                if (y.given) w.Add(new Why("Do they have a car?", "No: one was waiting for them there", false));
                return w;
            }
            w.Add(new Why("Do they have a car?", y.hasCar ? "Yes" : "No", y.hasCar));
            if (y.hasCar)
            {
                bool far = y.dist >= WALK_TILES;
                w.Add(new Why($"Further than {WALK_TILES:0} tiles ({WALK_TILES * 4:0} m)?", $"{(far ? "Yes" : "No")}: {y.dist:0} tiles door to door", far, far ? null : "Closer than that, people walk."));
                if (far) w.Add(new Why("Is their car parked where they are?", y.carHere ? "Yes: they drive" : "No: it's elsewhere", y.carHere));
            }
            if (trip.mode == Mode.Drive) return w;
            if (y.busWeighed)
            {
                double bus = y.toStopS + y.waitS + y.rideS + y.fromStopS;
                bool better = trip.mode == Mode.Bus;
                var b = new Why($"Is the bus at least a fifth quicker than walking?", $"{(better ? "Yes" : "No")}: {Minutes(bus)} by bus, {Minutes(y.walkS)} walking", better);
                b.Add(Why.Info($"Walk to stop {y.board + 1}", Minutes(y.toStopS)));
                b.Add(Why.Info("Wait for the bus", Minutes(y.waitS), "By the timetable, for the first bus after they'd get to the stop."));
                b.Add(Why.Info($"Ride to stop {y.alight + 1}", Minutes(y.rideS)));
                b.Add(Why.Info("Walk from the stop", Minutes(y.fromStopS)));
                w.Add(b);
            }
            else
            {
                string no = busStops.Count < 2 || buses.Count == 0 ? "No: the town has no bus" : "No: the same stop's nearest at both ends";
                w.Add(new Why("Is there a bus that helps?", no, false));
            }
            return w;
        }

        Why WhyNow(Person p, Trip trip, Car c, int busCar, Func<int, int> building)
        {
            if (trip.leg < 0 || trip.leg >= trip.legs.Count) return Why.Info("What are they doing now?", "Arriving");
            var leg = trip.legs[trip.leg];
            var w = Why.Info("What are they doing now?", $"Leg {trip.leg + 1} of {trip.legs.Count}: {LegText(p, trip, trip.leg, building)}");
            switch (leg.kind)
            {
                case LegKind.Walk:
                    if (p.crossWait >= 0)
                        w.Add(new Why("Waiting to cross?", $"Yes, since {TownClock.FormatSeconds(p.crossWait)}", false, "At a junction they cross when no turn across their way is under way (and on the green, at a signal)."));
                    else if (p.crossing >= 0) w.Add(Why.Info("Crossing a road?", $"Yes, at junction {p.crossing}"));
                    w.Add(Why.Info("How fast?", $"{p.pace * 4 * 3.6:0.0} km/h", p.pace > 0.5 ? "A jogger." : "Their own pace; crowded pavements slow them."));
                    break;
                case LegKind.Wait:
                    if (busCar >= 0)
                    {
                        w.Add(Why.Info("On the bus?", $"Yes: car {busCar}, getting off at stop {leg.alight + 1}"));
                        break;
                    }
                    w.Add(Why.Info("When's the next bus?", TownClock.FormatSeconds(NextAt(leg.board, Now)), "By the timetable."));
                    w.Add(Why.Info("How long will they wait?", $"Until {Minutes(GIVE_UP_WAITING / 1000.0)} have gone, then they give up"));
                    break;
                case LegKind.Ride:
                    w.Add(Why.Info("Which bus?", busCar >= 0 ? $"Car {busCar}, getting off at stop {leg.alight + 1}" : "Getting on"));
                    break;
                case LegKind.Passenger:
                    w.Add(Why.Info("Who's driving?", $"{NameOf(trip.driver)}, taking them", "Children under " + ESCORT_AGE + " are taken to school by someone in their household when someone can; they're out when the car's parked."));
                    break;
                case LegKind.Drive:
                    if (c != null)
                    {
                        var d = Why.Info("Their car", CarDoing(c, building));
                        var under = new List<Why>();
                        CarTree(c, under, building, driver: false);
                        foreach (var u in under) d.Add(u);
                        w.Add(d);
                    }
                    break;
            }
            return w;
        }

        // --- Cars' trees --------------------------------------------------------------------------

        void CarTree(Car c, List<Why> tree, Func<int, int> building, bool driver = true)
        {
            if (driver)
            {
                if (c.bus >= 0) tree.Add(Why.Info("Who's driving, and where to?", "The town bus, round and round its loop", $"{busStops.Count} stops, by the timetable."));
                else if (c.van >= 0 && c.seg >= 0)
                {
                    long off = OffDuty(c.van, Now);
                    tree.Add(Why.Info("Who's driving, and where to?", $"A van on its rounds: next, {PlaceText(c.dest, building)}",
                        $"Deliveries, tradespeople and taxis, day and night: {Math.Round(100 * Working(Now))}% of the town's vans are out now, as much commercial traffic as real towns have at this hour; this one goes off at {TownClock.Format(off)}. Each call's the nearest of {VAN_CHOICES} picked at random, so it works one part of town at a time; it drops off and goes on, without parking."));
                }
                else if (c.owner >= 0 && people[c.owner].trip != null && c.seg >= 0)
                {
                    var t = people[c.owner].trip;
                    tree.Add(Why.Info("Who's driving, and where to?", $"Person {c.owner}: {PurposeText(t.purpose)}, to {PlaceText(c.dest, building)}"));
                }
                else tree.Add(new Why("Who's driving?", "Nobody: it's parked", false));
            }
            if (c.seg < 0)
            {
                string where = c.offMap ? "Off the map, past a gate" : c.spot >= 0 ? $"In a {SpotText(c.spot)} by {PlaceText(Place.Building(c.parkedBy >= 0 ? c.parkedBy : c.dest.id), building)}" : $"By {PlaceText(Place.Building(c.parkedBy >= 0 ? c.parkedBy : c.dest.id), building)}";
                tree.Add(Why.Info("Where is it?", where));
                if (wantsOut.Contains(c.id)) tree.Add(new Why("Is it pulling out?", "Waiting for a gap in the traffic", false));
                return;
            }
            if (c.route != null && c.bus < 0)
            {
                int turns = 0;
                for (int k = c.step + 1; k < c.route.Count; k++) if (c.route[k] >= Lanes) turns++;
                tree.Add(Why.Info("Which way?", $"The quickest as the roads are: {Minutes(c.routeS)}, {turns} junction{(turns == 1 ? "" : "s")} still to go" + (c.reroutes > 0 ? $" (changed {c.reroutes} time{(c.reroutes == 1 ? "" : "s")} for the traffic)" : ""),
                    $"Each street at its limit plus how held up it's been lately (measured every {TownLife.ROUTE_EVERY_MS / TownClock.MINUTE} minutes), each turn at its speed; a signal counts as {Router.SIGNAL_S:0.#} s (its wait, on average), and pulling out from a stop sign onto a bigger road {Router.UNPROTECTED_S:0} s. Held up a minute, a driver looks for a way at least a fifth quicker. Worked out at {TownClock.Format(c.routed)}."));
            }
            tree.Add(StateWhy(c, building));
            if (c.bus < 0) tree.Add(StopWhy(c, building));
        }

        Why StateWhy(Car c, Func<int, int> building)
        {
            bool onTurn = c.seg >= Lanes;
            var q = queues[c.seg];
            switch (c.state)
            {
                case CarState.Waiting:
                {
                    if (c.route == null || c.step + 1 >= c.route.Count || onTurn) return Why.Info("What's it doing?", "Waiting");
                    var m = net.movements[c.route[c.step + 1] - Lanes];
                    var j = net.junctions[m.node];
                    var explain = new List<Why>();
                    var verdict = Judge(c, m, j, out long again, out _, out _, explain);
                    var w = new Why("May it take its turn?", verdict == Verdict.Go ? "Yes: it goes now" : $"Not yet: {VerdictText(verdict)} (it asks again at {TownClock.FormatSeconds(again)})", verdict == Verdict.Go,
                        $"At junction {j.node} ({ControlText(j.control)}), {TurnText(m)}; waiting since {TownClock.FormatSeconds(c.arrived)}.");
                    foreach (var e in explain) w.Add(e);
                    return w;
                }
                case CarState.Queued:
                {
                    int i = q.cars.IndexOf(c.id);
                    if (onTurn && i == 0 && c.step + 1 < c.route.Count)
                        return new Why("Why's it stopped?", $"At the end of its turn, waiting for space at the start of lane {c.route[c.step + 1]}", false);
                    if (i > 0) return new Why("Why's it stopped?", $"Behind car {q.cars[i - 1]}", false, $"Cars keep {FOLLOWING} tiles apart.");
                    if (q.blockFirst == c.id) return new Why("Why's it stopped?", "A player's standing in its way", false, $"It slows to stop {STAND_OFF} tiles short (never backing up), and waits till they've gone.");
                    if (q.laggy >= 0) return new Why("Why's it stopped?", $"Behind car {q.laggy}, still pulling off the lane", false);
                    return new Why("Why's it stopped?", "Queued", false);
                }
                case CarState.Crossing:
                {
                    double speed = Math.Min(SegSpeed(c.seg), c.laggy >= 0 ? SegSpeed(c.laggy) : double.MaxValue);
                    var w = Why.Info("What's it doing?", onTurn ? $"Going round its turn at junction {net.movements[c.seg - Lanes].node}" : $"Driving along lane {c.seg}",
                        $"Up to {speed * KMH:0} km/h here ({(c.laggy >= 0 && SegSpeed(c.laggy) < SegSpeed(c.seg) ? "its back's still on the turn behind it, at that speed" : onTurn ? "the curve's speed" : "the street's limit, and slower where it bends")}).");
                    if (q.blockFirst == c.id && !double.IsNaN(q.blockAt)) w.Add(new Why("Is anyone in its way?", "A player: it stops short of them", false));
                    return w;
                }
                case CarState.Unparking: return Why.Info("What's it doing?", "Pulling out of its spot", $"Out onto the road at {TownClock.FormatSeconds(events.When(Key(K_CAR, c.id)))}.");
                case CarState.Parking: return Why.Info("What's it doing?", "Pulling into its spot", $"Parked at {TownClock.FormatSeconds(events.When(Key(K_CAR, c.id)))}.");
                case CarState.AtStop: return Why.Info("What's it doing?", $"At stop {c.busStop + 1}, letting people on and off", $"Off again at {TownClock.FormatSeconds(events.When(Key(K_CAR, c.id)))}.");
                default: return Why.Info("What's it doing?", c.state.ToString());
            }
        }

        Why StopWhy(Car c, Func<int, int> building)
        {
            if (c.dest.border) return Why.Info("Where will it stop?", "It won't: it leaves the map by the gate");
            if (c.van >= 0) return Why.Info("Where will it stop?", $"It won't park: it drops off at {PlaceText(c.dest, building)} and goes on to its next call");
            if (options.infiniteParking) return Why.Info("Where will it stop?", $"By the door of {PlaceText(c.dest, building)}", "Parking's endless in this town (a check's setting).");
            if (c.target >= 0)
            {
                var kind = net.spots[c.target].kind;
                bool drive = kind == SpotKind.Driveway;
                var w = Why.Info("Where will it stop?", drive ? "In the driveway at home" : $"A {SpotText(c.target)} it's holding, {Geo.Dist(net.spots[c.target].at, Door(c.dest)):0.0} tiles from the door",
                    drive ? "Home with a driveway: in it." : kind == SpotKind.CarPark ? "The free bay nearest the door is in the car park behind the high street; nobody else can have it now." : "On its last street it takes the free spot nearest the door; nobody else can have it now.");
                if (c.hunts > 0) w.Add(new Why("Did it have to hunt?", $"Yes: the street by the door was full ({c.hunts} hunt{(c.hunts == 1 ? "" : "s")})", false, "It took the nearest free spot by road."));
                return w;
            }
            if (c.huntFailed) return Why.Info("Where will it stop?", "By the door anyway", "It hunted and found nowhere free.");
            return Why.Info("Where will it stop?", "Not chosen yet", "On its last street it takes the free kerb spot nearest the door, or hunts nearby if the street's full.");
        }

        string CarDoing(Car c, Func<int, int> building)
        {
            if (c.seg < 0) return c.offMap ? "Off the map" : "Parked";
            if (c.bus >= 0) return c.state == CarState.AtStop ? $"At stop {c.busStop + 1}" : $"On its loop, to stop {(c.busStop + 1) % Math.Max(1, busStops.Count) + 1}";
            string to = c.dest.border ? "the gate" : PlaceText(c.dest, building);
            switch (c.state)
            {
                case CarState.Waiting: return $"Waiting at a junction, on the way to {to}";
                case CarState.Queued: return $"Queued, on the way to {to}";
                case CarState.Unparking: return $"Pulling out, for {to}";
                case CarState.Parking: return $"Parking at {to}";
                default: return $"Driving to {to}";
            }
        }

        // --- Queues -------------------------------------------------------------------------------

        void CarQueue(Car c, List<Dictionary<string, object>> queue, Func<int, int> building)
        {
            queue.Add(Item(Now, CarDoing(c, building), true));
            // Each junction ahead, with when it'd take its turn: at the limits from where it is, waiting out the
            // junction it's at (by its own rules), each stop sign's stop, and each signal to its turn's green
            // (signals keep time, so when they're green is known).
            double first = (c.step == c.route.Count - 1 ? Math.Min(c.goal, SegLength(c.seg)) : SegLength(c.seg)) - Math.Max(0, Front(c));
            long at = Now + (long)(Math.Max(0, first) / Math.Max(0.05, SegSpeed(c.seg)) * 1000);
            if (c.state == CarState.Waiting && c.seg < Lanes && c.step + 1 < c.route.Count && c.route[c.step + 1] >= Lanes)
            {
                var m0 = net.movements[c.route[c.step + 1] - Lanes];
                if (Judge(c, m0, net.junctions[m0.node], out long again, out _, out _, null) != Verdict.Go) at = Math.Max(at, again);
            }
            int shown = 0;
            for (int k = c.step + 1; k < c.route.Count; k++)
            {
                int seg = c.route[k];
                if (seg >= Lanes)
                {
                    var m = net.movements[seg - Lanes];
                    var j = net.junctions[m.node];
                    if (k > c.step + 1 || c.state != CarState.Waiting)
                    {
                        if (m.stops) at += STOP_MS;
                        if (j.control == Control.Signal)
                        {
                            var (stage, ends) = RoadNet.Stage(j, at);
                            if (stage != m.stage) at = ends + 1;
                        }
                    }
                    if (shown++ < FOCUS_JUNCTIONS)
                        queue.Add(Item(at, $"Junction {j.node}: {Capital(TurnText(m))} ({ControlText(j.control)}{(m.stops ? ", stops first" : "")}{(j.control == Control.Signal ? $", stage {m.stage + 1}" : "")})", false));
                }
                double length = k == c.route.Count - 1 ? Math.Min(c.goal, SegLength(seg)) : SegLength(seg);
                at += (long)(length / Math.Max(0.05, SegSpeed(seg)) * 1000);
            }
            double t = at / 1000.0;
            if (c.bus >= 0)
            {
                queue.Add(Item((long)(t * 1000), $"Stop {(c.busStop + 1) % Math.Max(1, busStops.Count) + 1}", false));
                return;
            }
            string end = c.dest.border ? "Leave the map by the gate"
                : c.van >= 0 ? $"Drop off at {PlaceText(c.dest, building)}, then on to its next call"
                : c.target >= 0 ? $"Park: {(net.spots[c.target].kind == SpotKind.Driveway ? "the driveway" : $"the {SpotText(c.target)} it holds")} at {PlaceText(c.dest, building)}"
                : $"Park near {PlaceText(c.dest, building)} (the free spot nearest the door, or hunt)";
            queue.Add(Item((long)(t * 1000), end, false));
        }

        /// <summary>When the leg they're on is done, about (-1: can't say): a walk at their pace, a drive at the limits, the next bus come.</summary>
        long LegDone(Person p, Leg leg, Car c, int busCar)
        {
            switch (leg.kind)
            {
                case LegKind.Walk:
                {
                    if (p.walk == null) return -1;
                    var path = new List<Vec2>();
                    WalkPath(p, path);
                    double length = 0;
                    for (int i = 1; i < path.Count; i++) length += Geo.Dist(path[i - 1], path[i]);
                    return Now + (long)(length / Math.Max(0.05, p.pace) * 1000);
                }
                case LegKind.Drive:
                {
                    if (c == null || c.seg < 0 || c.route == null) return -1;
                    var q = new List<Dictionary<string, object>>();
                    CarQueue(c, q, null);
                    return q[q.Count - 1]["at"] is long t ? t : -1;
                }
                case LegKind.Wait: return busCar >= 0 ? -1 : NextAt(leg.board, Now);
                default: return -1;
            }
        }

        string LegText(Person p, Trip trip, int k, Func<int, int> building)
        {
            var leg = trip.legs[k];
            switch (leg.kind)
            {
                case LegKind.Walk:
                {
                    bool toCar = k + 1 < trip.legs.Count && trip.legs[k + 1].kind == LegKind.Drive;
                    bool fromCar = k > 0 && trip.legs[k - 1].kind == LegKind.Drive;
                    bool toStop = k + 1 < trip.legs.Count && trip.legs[k + 1].kind == LegKind.Wait;
                    if (toCar) return "Walk to the car";
                    if (toStop) return $"Walk to stop {trip.legs[k + 1].board + 1}";
                    if (fromCar) return $"Walk from the car to {PlaceText(trip.to, building)}";
                    return $"Walk to {PlaceText(trip.to, building)}";
                }
                case LegKind.Drive: return $"Drive to {PlaceText(leg.dest, building)}";
                case LegKind.Passenger: return $"In {NameOf(trip.driver)}'s car to {PlaceText(leg.dest, building)}";
                case LegKind.Wait:
                    return k == trip.leg && riding.ContainsKey(p.id) ? $"Ride the bus to stop {leg.alight + 1}" : $"Wait for the bus at stop {leg.board + 1}, ride to stop {leg.alight + 1}";
                default: return $"Ride the bus to stop {leg.alight + 1}";
            }
        }

        // --- The way ahead ------------------------------------------------------------------------

        /// <summary>Parked in a spot and drawn there.</summary>
        bool ParkedShown(Car c) => c.seg < 0 && c.spot >= 0 && !c.offMap && c.slot >= 0;

        void CarPath(Car c, List<Vec2> path)
        {
            if (c.route == null) return;
            // From where it's drawn (its middle), then on from its nose.
            path.Add(CarPoint(c).at);
            for (int k = c.step; k < c.route.Count && path.Count < FOCUS_POINTS; k++)
            {
                int seg = c.route[k];
                double from = k == c.step ? Math.Max(0, Front(c)) : 0;
                double to = k == c.route.Count - 1 ? Math.Min(SegLength(seg), c.goal) : SegLength(seg);
                for (double d = from; ; d += 1)
                {
                    double s = Math.Min(d, to);
                    path.Add(seg < Lanes ? net.LanePoint(net.lanes[seg], net.lanes[seg].start + s).at : net.MovementPoint(net.movements[seg - Lanes], s).at);
                    if (s >= to || path.Count >= FOCUS_POINTS) break;
                }
            }
            // Into a car park: on from its mouth to the bay.
            if (c.target >= 0 && net.spots[c.target].kind == SpotKind.CarPark && path.Count < FOCUS_POINTS) path.Add(net.spots[c.target].at);
        }

        void WalkPath(Person p, List<Vec2> path)
        {
            path.Add(PersonPoint(p).at);
            // How far along the piece they're on they've walked: the way ahead goes on from there, not from its start.
            double done = p.piece >= 0 && p.w0 >= 0 ? (Now - p.w0) / 1000.0 * p.wSpeed : 0;
            for (int k = Math.Max(0, p.piece); k < p.walk.Count && path.Count < FOCUS_POINTS; k++)
            {
                var piece = p.walk[k];
                for (int i = 0; i < piece.points.Count && path.Count < FOCUS_POINTS; i++)
                    if (k != p.piece || piece.at[i] > done) path.Add(piece.points[i]);
            }
        }

        Vec2 EndOf(Car c)
        {
            if (c.dest.border) return net.borders[c.dest.id].at;
            if (c.target >= 0) return net.spots[c.target].at;
            // A bus: the stop it's standing at (it's counted as reached), or the next it's driving to.
            if (c.bus >= 0) return busStops[c.state == CarState.AtStop ? c.busStop : (c.busStop + 1) % Math.Max(1, busStops.Count)].at;
            // No spot yet: where on its last street it'll stop (level with the door, unless it finds a spot first).
            if (c.route != null && c.route.Count > 0)
            {
                int last = c.route[c.route.Count - 1];
                if (last < Lanes) return net.LanePoint(net.lanes[last], net.lanes[last].start + Math.Min(c.goal, SegLength(last))).at;
            }
            return Door(c.dest);
        }

        Vec2 GateNear(Car c)
        {
            if (c.dest.border) return net.borders[c.dest.id].at;
            var o = c.owner >= 0 ? people[c.owner] : null;
            if (o != null && o.at.border) return net.borders[o.at.id].at;
            return net.borders.Count > 0 ? net.borders[0].at : new Vec2(0, 0);
        }

        // --- Words --------------------------------------------------------------------------------

        /// <summary>Which way turn `m` goes, to its driver: left, right, straight on, or a U-turn.</summary>
        public string TurnText(Movement m) => m.turn switch
        {
            Turn.UTurn => "a U-turn",
            Turn.Straight => "straight on",
            Turn.Right => "turn right",
            _ => "turn left",
        };

        static string VerdictText(Verdict v)
        {
            switch (v)
            {
                case Verdict.StopFirst: return "stopping at the sign";
                case Verdict.Red: return "its light's red";
                case Verdict.TooLateOnGreen: return "not enough green left to get round";
                case Verdict.NoGap: return "waiting for a gap in the traffic it gives way to";
                case Verdict.Siren: return "a siren's coming: it waits for it to go by";
                case Verdict.PlayerOnTurn: return "a player's on the turn";
                case Verdict.PeopleCrossing: return "townsfolk are crossing";
                case Verdict.Blocked: return "a crossing turn's under way, or there's no room beyond";
                default: return "it may go";
            }
        }

        static string ControlText(Control c)
        {
            switch (c)
            {
                case Control.Stop: return "stop signs on the smaller road";
                case Control.AllWayStop: return "an all-way stop";
                case Control.Signal: return "signals";
                default: return "no control";
            }
        }

        string SpotText(int spot) => net.spots[spot].kind switch { SpotKind.Driveway => "driveway", SpotKind.CarPark => "car-park bay", _ => "kerb spot" };

        /// <summary>Their age and household in a line ("34, in a family with two children: 4 of them"), or null (from beyond the gates).</summary>
        string HouseholdText(Person p)
        {
            if (p.household < 0 || p.household >= households.Count) return null;
            var (kind, members) = households[p.household];
            string role = p.role == Role.Child ? "a child" : p.role == Role.Retiree ? "retired" : "an adult";
            return members.Count == 1 ? $"{p.age}, {role}, living alone" : $"{p.age}, {role}, in a household of {members.Count} ({(kind.StartsWith("single") || kind.StartsWith("family") ? "a " : "")}{kind})";
        }

        /// <summary>What a service vehicle's about, in a few words.</summary>
        string ServiceText(Car c)
        {
            var s = services[c.service];
            switch (s.kind)
            {
                case ServiceKind.Bins:
                    string[] days = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
                    return $"A bin lorry on its {days[s.weekday]} round ({Math.Min(s.next + 1, s.stops.Count)} of {s.stops.Count} stops)";
                case ServiceKind.Ambulance:
                    return c.siren ? "An ambulance on a call, siren going" : c.state == CarState.AtStop ? "An ambulance at a call" : "An ambulance going back";
                default:
                    return c.siren ? "A fire engine on a call, siren going" : c.state == CarState.AtStop ? "A fire engine at a call" : "A fire engine going back";
            }
        }

        static string KindText(PersonKind k)
        {
            switch (k)
            {
                case PersonKind.Worker: return "Works, and lives in town";
                case PersonKind.Student: return "Goes to school";
                case PersonKind.AtHome: return "At home in the day";
                case PersonKind.CommuterIn: return "Lives out of town, works here";
                case PersonKind.Stallholder: return "Keeps a stall at the market, from out of town";
                default: return "Drives through, gate to gate";
            }
        }

        static string RosterText(Roster r)
        {
            switch (r)
            {
                case Roster.Shop: return "the shops' week (five days of seven, a weekday off and most Sundays)";
                case Roster.Shifts: return "shifts (five days on, two off; a week of earlies, 6 to 2, then a week of lates, 2 to 10)";
                case Roster.School: return "the school's days";
                default: return "Monday to Friday";
            }
        }

        /// <summary>The rule their day's plan was drawn up by (PlanDay), in words: what today is to them.</summary>
        string DayRule(Person p)
        {
            var td = Day(p.day);
            string today = $"{td.Name}{(td.holiday != null ? $" ({td.holiday})" : "")}";
            switch (p.kind)
            {
                case PersonKind.Worker:
                case PersonKind.CommuterIn:
                    if (!Works(p, td))
                        return $"{today}: a day off (they work {RosterText(p.roster)}). The market in the morning for a few, the week's shop for half, someone to see in the afternoon.";
                    return $"{today}: a working day (they work {RosterText(p.roster)}). Three in ten of those working in town go out for lunch; a third of those living in town call at a shop on the way home; now and then a visit in the evening.";
                case PersonKind.Student:
                    if (!td.schoolDay) return $"{today}: no school ({td.noSchool}). Someone to see in the afternoon for half; the shops or the market with the family for a few.";
                    return $"{today}: school from {TownClock.Clock12(td.bell / 1000, false)} to {TownClock.Clock12(td.endBell / 1000, false)}. Under-{ESCORT_AGE}s are taken there in three households in four, when someone can; the rest walk or take the bus.";
                case PersonKind.AtHome:
                    if (!td.officesOpen) return $"{today}: the market in the morning for a few, the week's shop late in the morning for half, someone to see in the afternoon.";
                    return $"{today}: the school run if there are young children, a shop mid-morning, someone to see in the afternoon for half.";
                case PersonKind.Stallholder:
                    return td.market ? $"{today}: market day, {TownClock.Clock12(td.marketOpen / 1000, false)} to {TownClock.Clock12(td.marketClose / 1000, false)}. In an hour before to set up, home after packing away." : $"{today}: not market day.";
                default:
                    return td.officesOpen ? "Through traffic: in at one gate (morning, or midday) and out at another, and back later." : "Through traffic at the weekend: fewer, from mid-morning.";
            }
        }

        static string PurposeText(Purpose p)
        {
            switch (p)
            {
                case Purpose.Home: return "Going home";
                case Purpose.Work: return "Going to work";
                case Purpose.School: return "Going to school";
                case Purpose.Shop: return "Going to the shops";
                case Purpose.Lunch: return "Out for lunch";
                case Purpose.Visit: return "Visiting";
                case Purpose.Leave: return "Leaving town";
                case Purpose.Market: return "Going to the market";
                case Purpose.DropOff: return "Taking the children to school";
                case Purpose.PickUp: return "Fetching the children from school";
                default: return "Passing through";
            }
        }

        static string ModeText(Mode m) => m == Mode.Drive ? "car" : m == Mode.Bus ? "bus" : "foot";

        static string PlaceText(Place p, Func<int, int> building) =>
            p.border ? $"gate {p.id + 1}" : p.id == MARKET ? "the market" : $"building {(building != null ? building(p.id) : p.id)}";

        static Dictionary<string, object> PlaceMap(Place p, Func<int, int> building) =>
            p.border ? new Dictionary<string, object> { ["gate"] = p.id }
            : p.id == MARKET ? new Dictionary<string, object> { ["market"] = true }
            : new Dictionary<string, object> { ["building"] = building != null ? building(p.id) : p.id };

        static string Minutes(double s) => s < 90 ? $"{s:0} s" : $"{s / 60:0.0} min";

        static string Lower(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static double Round(double v) => Math.Round(v * 100) / 100;

        static List<double[]> Points(List<Vec2> path) => path.ConvertAll(v => new[] { Round(v.x), Round(v.y) });

        /// <summary>A step in a queue, and when it starts (-1: not known); `seconds`: to the second (soon), or to the minute (later in the day).</summary>
        static Dictionary<string, object> Item(long at, string what, bool now, bool seconds = true) => new Dictionary<string, object>
        {
            ["at"] = at >= 0 ? (object)at : null,
            ["time"] = at < 0 ? null : seconds ? TownClock.FormatSeconds(at) : TownClock.Format(at),
            ["what"] = what,
            ["now"] = now,
        };

        /// <summary>Tiles a second to km/h (a tile's 4 m).</summary>
        const double KMH = 14.4;
    }
}

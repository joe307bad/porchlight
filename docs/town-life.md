# Town life: people with days, cars that queue, a bus

Every town has a population. Each person has a home, and often a job or a
school, and goes about their day by the county clock: off to work in the
morning rush, out for lunch, to the shops, home in the evening, now and then
to visit someone. They walk, drive or take the bus. Their cars queue at
junctions, wait at stop signs and signals, park at the kerb or in their
driveway, and hunt for a spot when the street is full. A day of the busiest
town (about 5,500 people and 16,600 trips) runs in about 5 seconds.

This is the design the [A/B Street research](abstreet.md) recommended. It
was written from that doc's description of the ideas. No A/B Street code was
copied or translated. See [Credits](#credits).

The code is in `unity/Assets/Porchlight/Runtime/Shared/Life/`. It's pure C#
(no Unity), so the game, the server's worker (`sim/`) and the checks all
run the same code.

| File | What it holds |
| --- | --- |
| `Core/EventQueue.cs` | The event queue. |
| `Life/Clock.cs` | The town clock: real time, county-wide. |
| `Life/RoadNet.cs` | Lanes, turns, junction controls, parking spots, and each building's way onto the street. |
| `Life/Router.cs` | Quickest routes, and the parking hunt. |
| `Life/Walkways.cs` | The walking network over the sidewalks. |
| `Life/Agents.cs` | Cars, people, trips and legs. |
| `Life/TownLife.cs` | The whole: start, run until, and the drawing slots. |
| `Life/Cars.cs` | Lanes as queues, junctions, parking, gridlock. |
| `Life/People.cs` | Who lives where, their day plans, and their trips. |
| `Life/Walking.cs` | Walkers, crossings and crowding. |
| `Life/Buses.cs` | The bus loop, stops and riders. |
| `Life/Analytics.cs` | Figures for Manage, and who's indoors where. |
| `Life/TownKey.cs` | Parses a town's key (shared with the server worker). |
| `Explore/Traffic.cs` | What the game draws: the slots as cars and walkers, plus the boats. |

---

## 1. Time

### The clock

The town clock is **real time**, in milliseconds since 1970, shown in the
county's time zone (`TownClock.ZONE_HOURS = -5`). It's the same in every
town and for every player, so it's night (20:00–06:30) everywhere at once,
and rush hour is when it's rush hour where you are.

- `TownClock.Now()` is the clock.
- `TimeOfDay`, `DayOf`, `At(day, timeOfDay)` and `Format` do the arithmetic.

### The event queue

Nothing is stepped frame by frame. Each car, walker, trip and bus has **one
pending event**, keyed by `(kind << 32) | id`. The kinds are car, clear,
trip, walk and bus.

- Scheduling a key that's already due moves it; cancelling removes it.
- Old heap entries are skipped lazily, and the heap is compacted once it's
  four times the live count.
- Times are integer ms, and ties go by key. A run is therefore
  deterministic: the same town and the same start give the same day,
  event for event. `traffic-day` checks this.

`TownLife.RunUntil(t)` pops and handles every event up to `t`. Drawing at
any moment needs no events: each car's position is a pure function of its
interval (below). Each car's front is remembered until anything changes,
including the clock, so looking at the town never changes what it does
next.

### Starting mid-day

`Start(moment)` doesn't simulate from midnight. It first makes everyone's
plan for the day. Then **Settle** puts each person where their plan says
they'd be, assuming every trip already due was made: indoors at work, at
the shops, and so on. Each car is parked near its owner. Finally the town
runs a 20-minute **warm-up** so trips already under way are on the road
when the player looks.

A server that falls more than 10 minutes behind the clock (a box that slept)
starts afresh the same way (`SimTown.Step`). A player taking over a town
from someone else does too (`Traffic.Restart`).

---

## 2. Roads: lanes, turns, junctions

### Lanes

`RoadNet` turns each drivable road into two lanes, one each way. Lanes
are measured along the road's centre line, offset to their side by a
quarter of the road's width.

- **The lane's direction is taken over 0.6 tiles either side** (`TANGENT`),
  not at a point. This way a lane rounds a tight corner of the road's line
  instead of looping back on itself on the inside of it.
- **Speed limits:** 3.5 tiles/s on Main roads (about 30 mph) and 2.1 on
  streets.
- **Bendy lanes are slower.** A car's length is laid along the lane and
  stepped down it 0.05 tiles at a time. The lane is no faster than:
  - the speed at which that length swings round at `SWING` (1.2 rad/s);
  - the speed at which its nose, on the outside of a bend, would outrun the
    limit.
- Each lane's queue starts and ends short of its junctions, leaving room
  for the turns. Lanes from a gate (entry) and to a gate (exit) end at the
  town's edge.

### Turns

A movement is one way through a junction, from one lane's end to another's
start.

- Each turn is a cubic Bézier that leaves and arrives along each lane's
  direction, so the heading never jumps.
- A straight-on turn keeps the slower lane's speed. A turn goes at 0.6 of
  it, and no faster than `SWING` allows at the curve's tightest point.
- A U-turn at a dead end swings out (1.4 tiles across) at 0.6 tiles/s.

### Conflicts

Each junction has a table of which turns conflict. Two turns conflict if
their curves cross, or if they go into the same lane. Two turns that merely
start from the same lane don't.

### Junction controls

Each junction's control is chosen by its legs:

| Control | When |
| --- | --- |
| Free | Two legs or fewer (a bend, a dead end). |
| Signal | Three or more Main legs. |
| All-way stop | All legs the same rank. |
| Stop | On the minor legs, where they meet bigger roads. |

**Signals** have two 30-second stages, one per pair of opposite legs.

- The stage is a pure function of the clock plus a per-junction offset, so
  any two copies of the town agree on it.
- A car never starts a turn it can't finish before its green ends (its
  length plus the turn, at the turn's speed).

**Stop signs** cost a 500 ms stop before a car may ask to go. Crossing or
joining a bigger road from a stop sign is *unprotected*: routes count it as
an extra 30 s, so drivers prefer the signals.

---

## 3. Cars: lanes as queues

A car is 1.1–1.6 tiles long (fixed per car; a bus is 3.1). The catalog's
models don't carry their lengths yet.

A car on a lane or turn ("segment") holds an **interval**: it goes from
`s0` at `t0` to `s1` at `t1`, at the segment's speed. Its front at any
moment is interpolated, and capped where the car ahead's back is less
`FOLLOWING` (0.25 tiles). A car never overlaps the one ahead, and nothing
is stepped.

**States:**

- **Parked**, then **Unparking**;
- **Crossing** (on its interval), then **Queued** (at the end, behind
  someone), then **Waiting** (first in the queue, asking the junction);
- **Parking**;
- **AtStop**, for a bus.

### Laggy heads

A car that has just turned still has its back in the segment it left. It
stays counted there, so the car behind can't close up, until its front is
a car-length into the new segment. Until then it goes no faster than the
segment it's leaving. That way it swings out of a slow turn, rather than
snapping to the next lane's speed.

### The end of a turn

A car reaching the end of its turn moves onto the lane beyond only when
there's space at the lane's start: the last car in must be a following gap
clear of it. Otherwise it waits on the turn, in the box, and looks again
every 300 ms. Two cars can be on turns into the same lane at once (one let
through to undo a knot), and they never end up inside each other.

### Room ahead

Each lane keeps a tally of the room the cars on it (or turning into it)
have **committed**: their length plus the following gap. A car may only
start a turn if the lane out has room for it. This is "don't block the
box": nobody stops on a junction because the lane beyond is full.

### Junctions

A car at the front asks to go (`Ask`). It's refused if any of these hold:

- it hasn't made its stop yet (stop signs);
- its turn isn't green, or can't be finished on this green (signals);
- **someone's standing on the turn** (a player, below);
- a walker is on a crossing it would cut;
- a turn under way conflicts with its own;
- there's no room on the lane out.

A refused car notes **who it's waiting on** (`blockedBy`). It's woken when
any of them clears the junction. Waiting cars are woken in the order they
arrived, so the junction is first come, first served. A refused car also
asks again after 10 s as a safety net.

### Gridlock

Before refusing, the junction follows the `blockedBy` chain from each car
the newcomer would wait on. If that chain comes back to the newcomer, it's
a knot: everyone would wait forever. The newcomer is let through, and
`stats.gridlockBroken` counts it. A car that has waited 3 minutes
(`GIVE_WAY_MS`) is let through regardless. Neither happens often (a few
times a day in the biggest town). Without them, an early version of Ember
Crossing wedged with 480 cars by mid-morning.

### Players in the road

`SetObstacles(points)` takes where the players are, on every update:

- **On a lane:** the first car behind them stops 1.2 tiles short. When they
  move, it goes on from where it is.
- **In a junction:** every turn whose curve passes within 0.5 tiles of them
  is blocked. A car already on that turn stops short. No car may start it
  until they've gone, and going wakes the junction.

A car held behind a player for 15 s turns round if it can: a U-turn at the
next junction, and a new route from there.

---

## 4. Routes

`Router` finds the quickest way by free-flow time:

- each lane's span at its limit;
- each turn's curve at its speed;
- a stop's 2 s, and an unprotected turn's 30 s.

Trips to the same lane share one **tree of ways**: a reverse Dijkstra from
the target over the turns into each lane, made the first time it's asked
for. The 16 most recent trees are kept. A route is a list of segments,
lanes and turns alternating (a turn's id is offset by the lane count).

Routes go to any of several targets: a building's kerb spots, its
driveway, or a gate's exit. The cheapest one wins.

### Hunting for a spot

When the spot a driver was making for is taken by the time they reach its
lane, they **hunt**. They search outward from where they are, nearest
first, for a lane with a free spot. Each turn's cost is scaled by a factor
in [0.1, 0.9) that is particular to that car and that lane. Drivers hunting
at once therefore fan out instead of all circling the same block. If
there's no free spot within reach (600 lanes), the car parks at the door
anyway, as if there were room, and `stats.noSpot` counts it.

---

## 5. Parking

Spots are made from the town as drawn:

- **Kerb spots** sit on each side of each street: the first 2.5 tiles in,
  then every 2.6 tiles. The street's scenery cars (the parked cars drawn
  with the town) take their spots for good.
- **Driveways** belong to their house: only its residents park there.

Timings: parking takes 15 s at the kerb and unparking 10 s; a driveway
takes 5 s either way. A spot is held from the moment a driver picks it, so
two cars never make for the same spot.

`LifeOptions.infiniteParking` gives every building a spot at its door. It's
for tests, and for towns where parking isn't the point.

---

## 6. People and their day

### Who lives here

The population is made from the town's zoning (`Zoning.cs`), by the
"proletariat robot" method:

- There are **home slots**: 1.2 working residents per household.
- There are **job slots**: one per job.
- People = 1.2 × the larger of the two. The extra fifth are through
  traffic.
- Each person tries for a home slot, then a job slot. The chances of each
  are the slots left over the people left, clamped to 0.05–0.9.
- Anyone without a slot comes in from (or goes out by) a **gate**, one of
  the town's border roads.

The kinds:

| Kind | Who |
| --- | --- |
| Worker | Lives here and works, here or out of town. |
| Student | 45% of those without a job slot. |
| AtHome | The rest of the residents. |
| CommuterIn | Lives out of town, works here. |
| Through | Drives from one gate to another. |

**Cars:** 90% of workers and 70% of the at-home have one, parked at home.
Commuters and through drivers arrive in theirs.

### A day

Each person's plan is a chain of stops with departure times. It's seeded by
`{seed}::day::{day}::{id}`, so the same day comes out the same everywhere
and a new day comes out different.

- **Workers:**
  - leave between 06:30 and 09:30;
  - 30% go out for lunch;
  - head home 8–9½ hours after starting, 35% by way of the shops;
  - 10% visit someone in the evening.
- **Students:** leave 07:30–08:30, are home 15:00–16:00, and half visit a
  friend or the shops after.
- **At home:** shops in the morning; half go out in the afternoon, and some
  in the evening.
- **Commuters** come and go by the same windows as workers.
- **Through drivers** pass in the morning or midday, and back out in the
  afternoon.

A visit goes to a nearby home 60% of the time, otherwise to a shop. A plan
always ends at home.

### A trip

When a stop's time comes, the person picks a mode:

- **Walk** if it's under 60 tiles (about 240 m).
- **Drive** if their car is parked where they are, or the trip starts or
  ends at a gate.
- **Bus** if, by the timetable, the walk to the stop, the wait, the ride
  and the walk from the stop beat walking all the way by a fifth.
- **Walk** otherwise.

A trip is a list of legs (walk, drive, wait, ride), each started when the
last ends. A drive goes:

1. walk to the car;
2. unpark;
3. drive to a spot near the destination (or the gate);
4. park;
5. walk to the door.

If a trip can't be made (no way there, the hunt gave up, no bus came in 45
minutes), the person is **put where they were going**, and their car near
it. `stats.failures` counts why. Nobody is ever stuck for good, and the
town's day stays on its plan.

**Who's where.** At any moment each person is indoors (in a building), off
the map (at a gate) or out on a trip. `TownLife.inside` and `offMap` keep
the counts up to date as trips start and end. That's what Manage draws: a
few hundred buildings instead of thousands of dots.

---

## 7. Walking

`Walkways` builds a graph over the sidewalks' ends:

- along each sidewalk;
- across the road at each junction;
- across mid-block. A mid-block crossing costs 3× its length, so walkers
  prefer the corner.

Each step of a walk is a **piece**: a stretch of sidewalk, or a crossing.

- **Pace:** 0.22–0.34 tiles/s (2–3 mph). 5% are joggers at 0.6–0.8.
- **Crowding:** at 1.5 or more people per m² on a piece, everyone on it goes
  at half pace.
- **Crossing at a junction** needs leave, as a turn does. A walker waits for
  cars whose turns cross it. A car whose turn a walker is on waits for the
  walker. A walker yields to a car that has waited longer than the crossing
  takes plus 3 s, so cars aren't held for ever on a busy corner.

The townsfolk who just stroll, with nowhere to be (`Pedestrian`), are still
there. There are fewer of them now (at most 24, one per 48 tiles of
sidewalk), and they're the ones a player can talk to.

---

## 8. The bus

Each town with enough roads gets one loop:

- **The route:** the middle junction, then the first junction in from each
  gate, in order round the town, and back to the middle. Each waypoint is
  passed on a lane into it (the longest, Main roads first). If any leg of
  the loop has no way through, the town has no bus (`busWhy` says why).
  Every starter town has one.
- **Stops** are about every 45 tiles. A stop is never on a lane in from a
  gate, since nothing turns onto those.
- **Buses** start every 20 minutes (`HEADWAY`), dwell 10 s at each stop,
  and are 3.1 tiles long. A bus that finishes its round before its next
  departure lays over off the road.

A rider walks to the stop, waits, boards the first bus, rides, gets off at
the stop nearest their destination, and walks the rest. Choosing the bus,
they read the timetable: the wait counted is the real one at their stop.
In Porchlight about 40 trips a day go by bus. Most trips in a small town
are short enough to walk.

---

## 9. Drawing it: the slots

The game draws a fixed set of **slots**, not every agent:

- **Car slots:** `clamp(ceil(people × 0.05), 12, 300)`, with the buses'
  slots after them. A congested town's rush puts 4–6% of its people on the
  road at once.
- **Person slots:** `clamp(ceil(people × 0.06), 16, 400)`.

A car takes a slot when it comes out onto the road, and gives it back when
it parks. A walker takes one when they step outside, and gives it back at
the door. An empty slot is **hidden**. Each slot keeps one car model or one
walker look for good, so nothing about a slot changes but where it is and
whether it's shown. `traffic-day` fails if more are ever out than there are
slots.

`TownLife.Draw()` fills `carSlots` and `personSlots` with a pose (shown, x,
y, heading, who) from each agent's interval or walk. `Explore/Traffic.cs`
turns those into `TrafficCar`s and puppet `Pedestrian`s for the game.

- **A car's nose is where the town's life says.** Its back wheels follow it,
  pulled after the front and never sideways, so a car swings round a
  corner the way a real one does.
- **Taking a car** (a player getting in) takes its slot out of the town's
  life. Its driver's trip is given up: they're put where they were going.
  Letting it go hands the slot back.
- **Starting.** The game starts a town's life on its first update, at
  `TownClock.Now()`, and advances it by the frame's time. The server
  (`SimTown.Step`) runs it to its own clock each tick.

### On the wire

The slots go out exactly as cars and people always have (7 bytes each,
`WorldSync`). An empty slot is the **hidden sentinel**: x and y both
0xFFFF. A real position is clamped one hundredth of a tile short of it.

- A follower sees the sentinel and hides that car or person.
- When the slot fills again, the follower puts it straight where it is,
  instead of easing it from where it was last.
- Deltas (`sim/src/Delta.cs`) send a slot when it empties or fills, and
  don't dead-reckon an empty one.

---

## 10. What it counts, and Manage

`LifeStats` counts as the day goes:

- trips started (by hour and mode), made, failed and why;
- each mode's mean trip time, and the longest trip;
- the cars onto each road each hour;
- each junction's mean and longest wait;
- gridlock broken, hunts, cars that found no spot, U-turns, buses boarded,
  crowded sidewalks, and walkers over Main roads.

Waits of over 30 s for a car, or 15 s for a walker, are kept as
**problems**.

`TownLife.Summary()` is the figures as JSON. The server's worker sends it,
with who's indoors in each building and who's off the map at each gate,
once a second (`LIFE`, [sim/README.md](../sim/README.md)). The server
serves:

| Route | What it returns |
| --- | --- |
| `GET /mp/sim/life?town=<key>` | The town's clock, who's indoors where, who's off the map at each gate, and the day's figures. |
| `GET /mp/sim/building?town=<key>&i=<n>` | Who's in that building, and when each next goes out. |
| `GET /mp/sim/where?town=<key>&kind=car\|person&i=<n>` | As before, plus whether the slot is empty, and who's in it and what they're doing (the agent's state, trip and legs). |

**Manage** (`manage/src/life.ts`) shows this:

- **On the map,** with land use on, each building has a disc whose area is
  the number of people inside.
- **Hovering** a building says how many are inside.
- **Clicking** a building lists them, with when each next goes out.
- **The panel's Town life section** gives each town's clock and who's
  indoors, out and out of town. It also gives trips made and failed, each
  mode's share and mean time, the busiest hour, how full the kerbs are,
  and the slow junctions.

Hidden slots aren't drawn, hovered or counted.

---

## 11. Checks

All of these run in CI (`.github/workflows/deploy.yml`).

**`Checks traffic-day server/towns 2`** runs a whole day, from midnight to
03:00 the next morning, of each starter town and two generated ones. It
fails if:

- more than 0.2% of people are still out at the end (a town that wedged);
- a trip took over 3 hours;
- more than 2% of trips failed;
- more were out at once than there are slots;
- there were fewer trips than people;
- a car overtook the one ahead or left its interval (checked every minute);
- the same day doesn't come out the same twice.

It prints how each day went, for comparing across commits:

| Town | People | Trips | Failed | Walk / drive / bus | Peak cars on the road | Day |
| --- | --- | --- | --- | --- | --- | --- |
| Ember Crossing | 5,502 | 16,595 | 93 | 10,402 / 6,075 / 25 | 236 | 5.4 s |
| Porchlight | 5,418 | 16,427 | 7 | 10,133 / 6,252 / 35 | 54 | 4.4 s |
| Lanternport | 1,205 | 3,637 | 16 | 2,705 / 910 / 6 | 14 | 0.3 s |
| Juniper Falls | 391 | 1,196 | 2 | 1,032 / 162 / 0 | 6 | 0.1 s |

Ember Crossing's planned core (short blocks between back-to-back signals,
from the town planning work) makes the only real rush hour: its drives take
about twice as long as Porchlight's.

**`Checks life server/towns`** checks one rule per scenario, each as
given/when/then:

- **The event queue.** Given events scheduled out of order, some moved and
  some cancelled, when they're popped, then they come in time order (ties
  by key), and each live one fires exactly once.
- **The wire.** Given empty slots, when they're packed and followed, then
  they're hidden on the follower too. A slot that fills again appears
  where it is. A car at the far corner of the map is never mistaken for an
  empty slot.
- **A player on a lane.** Given a car going along a lane, when a player
  stands in front of it, then within two seconds it has stopped short of
  them, and it goes again once they've gone.
- **Nobody's run over.** Given the morning rush, when a player steps out
  in a clear spot just in front of a moving car, wherever it is (on a
  lane, on a turn, about to take one), then over the next three seconds
  no car drives into them. This is tried 200 times a town. With players
  ignored, it fails in every town.
- **A player in a junction.** Given a player in the middle of the busiest
  junction through the rush, then no car on a turn there reaches them,
  cars do wait, and the waiting cars go once the player leaves.
- **A whole day.** Given a town's whole day, then:
  - no car starts a turn on red;
  - no two parked cars share a spot;
  - a bus waits its dwell at every stop;
  - every minute, indoors + off the map + out = everyone;
  - at 03:00, every resident is home.
- **Infinite parking.** Given the infinite-parking option, then nobody
  hunts, and the day still ends with everyone home.

To check that the checks bite: with signals made to ignore red, the day
scenario fails in every town.

**`Checks towns 60`** gains one check: town traffic drives as a car would.
Over a minute of the morning rush, no car swings round faster than 2.6
rad/s, and no car's back wheels slide sideways faster than 0.6 tiles/s.

**`sim --bench`** and **`sim --follow`** run the towns at 08:15 on a fixed
weekday:

- a tick costs about 0.18 ms a town (the budget is 1 ms);
- deltas are about 5% of the whole;
- a follower's drawing stays within 0.3 tiles of the server's, and every
  car or person is hidden on both sides or shown on both.

**`server/test/sim.test.ts`** runs the real worker:

- the town's life has a clock of now;
- indoors + off the map + out add up to everyone;
- every building index is the town's;
- the fullest building's people are listed;
- a car on the road is described;
- a car that's out is the one taken.

**`manage/test/life.test.ts`** covers:

- the figures add up;
- a bigger crowd draws a bigger disc;
- who's in a building is said a line each;
- the hidden sentinel reads as nobody;
- a building is found by its index.

---

## Credits

The architecture follows ideas that the traffic simulation game
[A/B Street](https://github.com/a-b-street/abstreet) (by Dustin Carlino and
contributors, Apache-2.0) describes in its design documents:

- a discrete-event core;
- lanes as queues of intervals;
- "laggy heads";
- reserving room on the lane out;
- breaking cycles of waiting cars;
- generating trips from building capacities;
- a jittered hunt for parking.

[abstreet.md](abstreet.md) is our study of it. Porchlight's code is its own
implementation of those ideas, written for our towns, wire and game. No
A/B Street source was copied or translated, so the Apache licence's terms
for derivative works don't apply. We credit it anyway, gladly.

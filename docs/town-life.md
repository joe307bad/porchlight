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
| `Life/Rounds.cs` | Vans on their rounds: how many, their shifts, their calls. |
| `Life/Gridlock.cs` | Giving up in the traffic: a car that hasn't moved in 10 minutes. |
| `Life/Congestion.cs` | Routing by the traffic: each lane's hold-up measured, and held-up drivers taking a quicker way. |
| `Life/Focus.cs` | Manage's focus on one car or person: its decision tree, its queue, and its way ahead. |
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
- A straight-on turn keeps the slower lane's speed. A turn goes at up to
  0.6 of it, and no faster than a driver corners comfortably:
  v = √(a / curvature), with a = 0.32 g (`CORNERING`, the side friction
  AASHTO designs low-speed street curves for at 15 mph [T48](city-research.md#references)), at the curve's
  tightest car's length. A tile's about 4 m (50 km/h is 3.5 tiles a
  second). Turns run at about 38% of the road's limit (19 km/h off a
  50 km/h road), the slowest tenth at about a third. Before this they were
  held to a turn rate at the curve's sharpest point: 31% on average and 17%
  for the slowest tenth, which looked like hesitating.
- A U-turn at a dead end swings out (1.4 tiles across) at 0.6 tiles/s.

### Conflicts

Each junction has a table of which turns conflict. Two turns conflict if
their curves cross, or if they go into the same lane. Two turns that merely
start from the same lane don't.

### Junction controls

Each road has a **rank** by its functional class in the town's plan
(FHWA's): an arterial 3, a collector 2, a local street 1. A/B Street ranks
roads the same way, by their OSM highway type. Each junction's control is
chosen by its legs' ranks:

| Control | When | Source |
| --- | --- | --- |
| Free | Two legs or fewer (a bend, a dead end). | A/B Street: no signs where two roads meet. |
| Signal | Three arterial legs or more, or two arterials crossing two collectors or bigger. | MUTCD 4C.01–4C.02 [T49](city-research.md#references): signals are warranted by heavy traffic on both roads (Warrant 1: 500 an hour on the major road and 150 on the minor, for 8 hours). We have no counts when a town is made, so the class stands in for them. |
| Stop | Where ranks differ: the smaller roads stop for the biggest. | MUTCD 2B.04 [T50](city-research.md#references): "the roadway carrying the lowest volume of traffic should be controlled"; A/B Street's `ControlStopSign::new`: everything stops but the highest rank. |
| Stop, at a T | A T of equal roads: the stem stops, the two that run straight through don't. | MUTCD 2B.04 again: the through road carries more than the stem. |
| All-way stop | Four legs or more, all one rank. | MUTCD 2B.07 [T50](city-research.md#references): multi-way stops are for roads with about equal volumes crossing. |

A/B Street makes an all-way stop of any junction whose roads share a rank,
T junctions too. We don't: that made 197 of Porchlight's 307 junctions
all-way stops, and cars stopped at most corners. Now it's 31 (7 signals,
247 stop signs, 22 free). Where signals go, A/B Street takes from
OpenStreetMap (osm2streets' `IntersectionControl`); we have no map to read,
hence the class rule.

**Signals** have two 30-second stages, one per pair of opposite legs: the
straight-on movements of a pair go together, and turns across them give way
to cars crossing. At a T, the two legs that run most nearly straight through
share a stage and the stem has the other. This is A/B Street's
`four_way_two_stage` and `three_way` plans.

- The stage is a pure function of the clock plus a per-junction offset, so
  any two copies of the town agree on it.
- A car never starts a turn it can't finish before its green ends (its
  length plus the turn, at the turn's speed).

**A light changes for a car waiting** (`skipEmpty`). A car at red asks
again; if nobody is at, near (4 s at the limit) or crossing the junction on
the turns that are green, and the green has run at least 8 s, the light
changes now: the junction's offset moves so its stage is the car's, a whole
stage long from now. Every copy still agrees, since the stage is still the
clock plus the offset. A quiet side street no longer holds a car for half a
minute in front of an empty main road. `stats.lightsChanged` counts them.

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

A car at the front asks to go (`Ask`). The junction's rules are one
function, `Judge`, that changes nothing: it asks its questions in order and
returns go, or why not and when to ask again. `Ask` acts on that verdict.
Manage's focus view calls the same function, so the reasons it shows are
the rule that ran (§11). A car is refused if any of these hold, in this
order:

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

### Giving up in the traffic

A knot at a junction isn't the only way to wedge. A whole grid can lock: a
ring of full lanes round a block, each car waiting for room the next one
holds, with none of them at a junction to ask. Ember Crossing's evening
rush did this, with 1,219 cars still in a ring at 3 am, because the knot
check only runs when a car asks at a junction.

So once a minute every car on the road is looked at (`Sweep`,
`Life/Gridlock.cs`). One that hasn't moved 0.3 tiles in 10 minutes
(`STUCK_MS`) **gives up its drive**: it pulls over by the nearest building
and its driver walks the rest of the way. A driver bound for a gate gives
up the trip instead; a van goes off its rounds. That frees a car's length
in the ring, and the ring moves. The car is fetched home by the next
morning. `stats.gaveUp` counts them: none in the small towns, a few
hundred a day in Ember Crossing and Porchlight. (Cancelling drivers held up
past a threshold is A/B Street's idea.)

### Letting a driver in

A car pulling out from the kerb needs a gap in the lane (`letIn`). After two
tries without one, the nearest car coming up behind it, far enough back to
stop, **holds back** for it: that car's front is kept behind the spot until
the driver is out, and the driver takes the gap it leaves. Only one car on a
lane is held at a time, and it's let go when the driver is out or gives up.
`stats.letIn` counts them. Without it, a car could wait out a whole rush at
the kerb of a busy street.

### Players in the road

`SetObstacles(points)` takes where the players are, on every update:

- **On a lane:** the first car behind them stops 1.2 tiles short. When they
  move, it goes on from where it is. They stand in the way of each lane
  whose middle is within 0.7 tiles of them (half a car's width and a
  margin): their own side's, and the other side's too when they're near the
  middle of the road. Every road near them counts, not only the nearest:
  at the mouth of a junction the end of the lane coming in is as near as
  the road across. Buses stop for them as cars do.
- **In a junction:** every turn whose curve passes within 0.5 tiles of them
  is blocked. A car already on that turn stops short. No car may start it
  until they've gone, and going wakes the junction.

A car held behind a player for 15 s turns round if it can: a U-turn at the
next junction, and a new route from there.

---

## 4. Routes

`Router` finds the quickest way as the roads are:

- each lane's span at its limit, plus its **hold-up** as last measured;
- each turn's curve at its speed;
- a stop's 2 s, and an unprotected turn's 30 s;
- a signal's 7.5 s (`SIGNAL_S`): the wait, on average, of a car turning up
  at any moment, red half the time for half a 30 s stage on average.

Counting signals keeps routes from threading every signal in town when
there's a way round. Without it, Ember Crossing's long trips all funnelled
through its core, where signals stand 1–3 tiles apart, and jammed it. With
it, its mean drive fell from 27 to 13 minutes and its rush-hour peak from
754 cars to 304.

Coordinating close signals (one timing for signals under 16 tiles apart,
a green wave along each road) was tried and made it worse: every cross
street went red at once, which starved it (38 minutes, 765 cars). Each
signal keeps its own timing.

### Routing by the traffic

`Life/Congestion.cs` (`liveRoutes`). As each car leaves a lane, the time it
took over its time at the limit is noted (less a signal's 7.5 s, which the
turn already counts). Every 5 minutes each lane's hold-up is worked out
again: half the last figure, half what was measured since, counting the car
waiting at the front too. The router is told, and its trees are made again
from the new times.

A driver held up a minute (queued or waiting at a junction) looks again,
at most every 3 minutes: if there's a way at least a fifth quicker from
where they are, they take it. `stats.rerouted` counts them. So traffic
spreads off a jammed main road onto the streets round it, and back when it
clears.

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

### Holding a spot as you set off

A driver going to a building picks the nearest free kerb spot within 30
tiles of its door as they set off (`reserveSpots`), and drives to it. It's
held from then, so nobody else takes it on the way and there's no hunt on
arrival. Spots are found through a grid of 10-tile cells. With none free
in reach, the driver goes to the door's own spots as before, and hunts if
they're taken. `stats.reserved` counts them; the hunts in the big towns
fall by about a fifth.

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
- Each home has **three times** the plan's 2.5 people (`CROWD`). The extra
  people are all at home in the day: children, the retired, those working
  from home. The workers and jobs are the plan's, so the rushes are what
  the roads were laid out for, and the extra people's trips (to the shops,
  school and visiting) come through the day, mostly on foot. It's per home,
  so a bigger town gets proportionally more.
- A household has at most **2 cars a home** (`CARS_A_HOME`): workers' come
  first, then those at home in the day share what's left.

Tripling workers and jobs as well was tried. Rush hour tripled and Ember
Crossing and Porchlight wedged, without the streets looking much busier
in between: commuters bunch at the rushes.
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

A visit goes to a nearby home 60% of the time, otherwise to a shop.
"Nearby" is the nearest of 6 picked at random (`nearErrands`; 3 without):
lunch, the shops and visits stay closer to home, so more of them are on
foot and the drives are shorter. A plan
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

## 9. Vans on their rounds

A town's streets aren't only its own people's comings and goings. Vans
call at its homes, shops and offices day and night: deliveries,
tradespeople, the post, taxis. They're most of what keeps a side street
from being empty between the rushes, and the only traffic at night.
Without them, only 1–6% of a town's blocks had a car on them by day, and
at night a town had one car on the road. `Life/Rounds.cs` has them:

- **How many:** 0.6 a block (`VANS_A_BLOCK`; a block is a street between
  two junctions, either way). A bigger town has more.
- **How many are out, hour by hour,** follows real commercial traffic:
  Detroit's weekday commercial profile in Batterman, Cook & Justin (2015),
  Table 7 [T52](city-research.md#references). Divided by its busiest hour (09:00), it's the share of the
  vans out: about a fifth at 01:00–02:00, half at 06:00, all at 09:00,
  under half by 19:00. Each van has a fixed place in line, from a hash of
  its id. It's out while the share is above its place, so vans come out
  one by one as the share rises and the last out go off first as it falls.
  Porchlight has about 50 cars on the road at night, 140 at 05:00 and
  around 300 through the day.
- **The rushes are sat out** (07:45–09:15 and 16:15–18:15). A van that
  finishes a call in one waits off the road until it's over, plus up to
  10 minutes, so they don't all come back at once. The rushes
  have the commuters, and a van then only holds them up: all-day vans
  doubled Ember Crossing's rush-hour drives.
- **Calls:** a van comes out at a building and drives to its next call:
  the nearest of three buildings picked at random, at least 12 tiles away.
  So it works one part of town at a time. It drops off and goes on: vans
  never park, so they don't take the kerb spots residents need.

Vans are cars with no owner (`Car.van`). They're drawn like any car, can
be taken by a player (another comes out 10 minutes later), and give up in
the traffic like anyone else. In Manage, hovering one says "Van #n · … · on
its rounds", and focusing on it says where its next call is and when its
shift ends.

---

## 10. Drawing it: the slots

The game draws a fixed set of **slots**, not every agent:

- **Car slots:** `clamp(ceil(people × 0.05), 12, 900)` plus one a van, with the buses'
  slots after them. A congested town's rush puts 4–6% of its people on the
  road at once.
- **Person slots:** `clamp(ceil(people × 0.06), 16, 1200)`.

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

## 11. What it counts, and Manage

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
| `GET /mp/sim/signals?town=<key>[&stops=1]` | The town's traffic lights now: each signal's stage showing green and when it ends (`ends`, town ms), and a lamp on the kerb by each stop line with the stage it goes on. With `&stops=1`, its stop signs too: x, y, and 1 if all-way. |
| `GET /mp/sim/where?town=<key>&kind=car\|person&i=<n>` | As before, plus whether the slot is empty, and who's in it and what they're doing (the agent's state, trip and legs). |

**Manage** (`manage/src/life.ts`) shows this:

- **On the map,** with land use on, each building has a disc whose area is
  the number of people inside.
- **Hovering** a building says how many are inside.
- **Clicking** a building lists them at the top of the panel, with when
  each next goes out; click someone for their focus.

**The panel** is as simple as it can be, top to bottom:

1. **What was clicked:** a car or townsperson's focus, or who's in a
   building; with nothing clicked, how to look closer.
2. **Towns:** each in a line or two (its clock, people, who's out, cars on
   the road and stopped, trips today, the share by car and how long they
   take). Click one to go there.
3. **Show:** a checkbox for each layer, with its key beside it (car speeds,
   townsfolk, lamp colours, stop signs, land use, players).
4. **Playing now,** only when someone is: click to follow them.

- **Traffic lights,** close in (2 pixels a tile or more), are a small lamp
  in a dark housing on the kerb by each stop line: green, amber in a
  stage's last 3 s, red. Manage asks for the lights of the towns in view
  once a second, and in between works out each lamp from the stage and
  when it ends (`lampColor`), as the town does. How the game shows them is
  still to be decided.
- **Stop signs,** at the same zoom, are a small red octagon on the kerb by
  each lane that stops (paler at an all-way stop). They come with a town's
  first lights (`&stops=1`), since they don't change.

Hidden slots aren't drawn, hovered or counted.

### Focus: one car or person

Clicking a car or townsperson on the map focuses on them; so does clicking
someone in a building's list, or a focus's "Its driver" or "Their car"
button. A focus follows **who** it is, not the slot it's drawn in: a person
walking to their car, driving, then indoors stays in focus throughout. Its
panel is asked for again every second:

| Route | What it returns |
| --- | --- |
| `GET /mp/sim/focus?town=<key>&kind=car\|person&slot=<n>` | Who's drawn at that place in the feed's records (a click), and their focus. A wanderer or an empty slot: `{ wanderer: true }` or `{ empty: true }`. |
| `GET /mp/sim/focus?town=<key>&kind=car\|person&id=<n>` | The same, by who it is (the `id` an earlier answer gave). |

A focus (`Life/Focus.cs`, read-only) has three parts.

**The decision tree** is each question that got them doing what they're
doing now, with its answer and what it went by. A yes-or-no decision is
marked ✓ or ✗; a fact (how long, how fast, which way) is unmarked.

- *Why are they out?* The trip, and the rule their day was drawn up by
  (§6, `PlanDay`), by who they are.
- *How are they getting there?* The mode choice with its figures, kept on
  the trip when it was made (`Trip.why`): through a gate (needs a car),
  has a car, further than 60 tiles, car parked here, then the bus weighed
  against walking (to the stop, the timetable's wait, the ride, from the
  stop; it must be a fifth quicker).
- *What are they doing now?* The leg, and its own questions: waiting to
  cross, the next bus, or their car's tree.
- A car's tree: who's driving where, the route (its free-flow time as the
  router reckoned it), what it's doing (behind which car, a player in the
  way), and where it'll stop (driveway, the kerb spot it holds, hunting).
  A car waiting at a junction gets `Judge`'s questions, each answered.

**Their day** comes first: where they are in their routine. It lists
today's trips in order: those made (✓, with when they got there) or
failed (✗, and why), the one under way (▶, with which leg), the next (→)
and the rest (·), then tomorrow's once it's planned. Above it, a sentence
says what decides what happens next: on a trip, each leg ends when they get
there; otherwise only their plan moves them, and the next trip goes when
it's due (or as soon as they're back). Below it, who they are and the rule
their day was drawn up by. A car shows its owner's day, since that's what
sends it out. Each person remembers their last six trips for this.

**The decision queue** is what comes next, in order, each with when it
starts where that's known:

- a person: the rest of this trip's legs (the one under way first; when it
  ends, about, for a walk at their pace or a drive at the limits), arriving,
  then their plan's next trip (the rest are in their day). A trip due before they're back goes as
  soon as they are (it's shown then, with when it was due);
- a car: what it's doing now, each junction ahead (up to 12) with the turn
  it takes and the junction's control, then where it parks or the gate it
  leaves by. The times wait out the junction it's at (by `Judge`), each stop
  sign's stop, and each signal to its turn's green (signals keep time, so
  their greens are known, though one may change early for it); traffic
  ahead isn't counted.

**The way ahead** is drawn on the map from where it is: the car's lanes
and turns to where it'll stop, or the walker's pieces of sidewalk and
crossings. A ring marks the focus, dashed when it isn't out (indoors, or
parked), and a cross marks where it's going. **Follow** keeps it in the
middle of the map.

**Hovering** a car or townsperson asks for a line about them
(`&brief=1`): who, what they're doing, and where in their day ("trip 2 of
4 today"), or whose errand a car is on. Clicking opens the focus.

Turns are named the driver's way. Lane +1 runs on the side `SideLane` calls
left of the road's direction, because the tile frame is mirrored; that is
the driver's right, so a turn towards it is a right turn.

Looking never changes the town: a focus reads, and the only thing kept for
it is each trip's mode figures and each person's last three trips (without
their legs).

---

## 12. Checks

All of these run in CI (`.github/workflows/deploy.yml`).

**`Checks traffic-day server/towns 2`** runs a whole day, from midnight to
03:00 the next morning, of each starter town and two generated ones. It
fails if:

- more than 0.2% of people are still out at the end (a town that wedged);
- a trip took over 3 hours;
- more than 2% of trips failed;
- more were out at once than there are slots;
- there were fewer trips than people;
- by day (08:00–20:00) fewer than 15% of the town's blocks had a car on
  them, on average (`BlocksInUse`);
- a van set off on a call in a rush;
- a car overtook the one ahead or left its interval (checked every minute);
- the same day doesn't come out the same twice.

It prints how each day went, for comparing across commits:

| Town | People | Trips | Failed | Walk / drive / bus | Peak cars on the road | Blocks with a car by day |
| --- | --- | --- | --- | --- | --- | --- |
| Ember Crossing | 15,542 | 47,625 | 407 | 39,849 / 7,328 / 41 | 627 | 23% |
| Porchlight | 15,303 | 47,159 | 203 | 39,057 / 7,818 / 81 | 492 | 25% |
| Lanternport | 3,405 | 10,481 | 35 | 9,273 / 1,154 / 19 | 118 | 23% |
| Juniper Falls | 1,106 | 3,374 | 3 | 3,181 / 190 / 0 | 63 | 26% |

(Before the extra people and the vans: 5,502 / 5,418 / 1,205 / 391 people,
and 6% / 4% / 2% / 1% of blocks with a car on them.)

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
  in a clear spot just in front of a moving car or the bus (half a tile
  past its front bumper), wherever it is (on a lane, on a turn, about to
  take one, at the mouth of a junction), then over the next three seconds
  nothing drives into them. This is tried 200 times a town, every fifth on
  the bus. With players ignored, it fails in every town.
- **Manage's focus.** Given the morning rush, when every person on a trip
  and every car on the road is focused on, a minute at a time, then each
  tree's way of getting there is the mode the trip took. A waiting car's
  tree has the junction's questions. Each queue is in time order, with the
  step under way first. The way ahead starts where they are and ends where
  a car will stop. The slot each is drawn in leads back to them.
- **Focus only looks.** Given the same town twice through two hours of the
  morning, when one is focused on (everyone out, every ten seconds), then
  both come out the same: the same trips, every car where the other's is.
- **The rush travels smart.** Given a town's morning rush (07:30–09:30),
  then: drivers held spots as they set off, none more than 30 tiles from
  the door; a light changed for a waiting car (in a town with lights); and
  in a big town (over 10,000 people), held-up drivers took another way and
  drivers were let in.
- **Vans at midday.** Given a town at noon, then at least a quarter of its
  vans are out, none has an owner or holds a parking spot, and Manage's
  focus and hover each say it's a van on its rounds. An hour on, at least
  half are on to another call.
- **Turn names.** Given every turn in a town, right turns are on average
  shorter than left (traffic keeps right), and there are some of each.
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

### What each part of travelling smart is worth

`LIFE_OFF=<options>` (comma-separated `LifeOptions` fields) turns those
options off for `traffic-day`, to see what each does. A day of Ember
Crossing and Porchlight, the same people and cars:

| | Ember: mean drive | failed trips | gave up | blocks busy | Porchlight: mean drive | failed | gave up | busy |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| None of them | 24.7 min | 407 | 612 | 23% | 12.8 min | 203 | 113 | 25% |
| All of them | 9.1 min | 39 | 0 | 30% | 8.7 min | 16 | 0 | 32% |
| All but `liveRoutes` | 13.6 min | 125 | 250 | 23% | 10.4 min | 73 | 49 | 25% |
| All but `reserveSpots` | 9.5 min | 28 | 0 | 31% | 9.0 min | 11 | 0 | 32% |
| All but `letIn` | 9.2 min | 47 | 0 | 31% | 8.6 min | 23 | 0 | 32% |
| All but `skipEmpty` | 9.6 min | 38 | 0 | 30% | 8.6 min | 20 | 0 | 31% |
| All but `nearErrands` | 9.3 min | 50 | 2 | 31% | 9.0 min | 15 | 0 | 32% |

Routing by the traffic is most of it. The rest each take a little off the
drive or the hunts (`reserveSpots`: about 700 fewer hunts a day in each;
`nearErrands`: about 1,500 fewer drives). The streets are busier, not
quieter: the same cars, moving, are on more blocks than when they stood in
one queue downtown.

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
- cancelling drivers held up past a threshold;
- routing by measured travel times, and rerouting drivers held up;
- generating trips from building capacities;
- a jittered hunt for parking.

[abstreet.md](abstreet.md) is our study of it. Porchlight's code is its own
implementation of those ideas, written for our towns, wire and game. No
A/B Street source was copied or translated, so the Apache licence's terms
for derivative works don't apply. We credit it anyway, gladly.

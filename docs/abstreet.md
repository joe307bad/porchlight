# What to port from A/B Street

[A/B Street](https://github.com/a-b-street/abstreet) is a traffic simulation
game in Rust (Apache-2.0). It is the closest open-source match to what
[city-research.md](city-research.md) asks of our towns: people with homes
and jobs, trips through the day, cars that queue at junctions, park and walk
to the door, and a full day that runs on a laptop. This doc goes through its
code and says, for each part, **what it does**, **what Porchlight has
today**, and **how to port it**. The phased plan is at the end, and it lines
up with the to-do list in city-research.md
([issue #17](https://github.com/joe307bad/porchlight/issues/17)). All of
it is now built: see [town-life.md](town-life.md).

A/B Street is cloned at `../abstreet` (beside this repo). Its paths below are
relative to that checkout. Scale: a tile is 4 m. A/B Street works in metres
and seconds, so each constant is also given in tiles.

---

## The short version

**Take:**

1. **The discrete-event core.** A priority queue of timed commands, and
   cars that hold a time interval and a distance interval rather than a
   position. Nothing ticks. A car's position is worked out only when someone
   asks for it. That is why a whole city runs cheaply, and it fixes the
   problem that sank our last traffic system (see below).
2. **Lanes as queues.** Following is "no closer than 1 m behind the car
   ahead". There's no car-following physics.
3. **Junctions that can't gridlock for long:**
   - reserve room on the road out before entering (don't block the box);
   - first come, first served at stop signs;
   - a `blocked_by` graph whose cycles are broken by letting one car through.
4. **The "proletariat robot":** trips made from building capacities alone,
   with no census. We already have every building's homes and jobs
   (`Explore/Zoning.cs`).
5. **The person and trip state machine:** inside a building, on a trip, or
   off the map. Trips are made of legs (walk → drive → park → walk), and a
   failed trip warps the person to where they were going.
6. **Parking as a trip step**, with a jittered search when the street is
   full.
7. **The analytics and the "problems" list**, for Manage. **Full-day
   regression runs** (`prebake`), for a `Checks traffic-day`.

**Leave:**

- OSM import and the lane-level map model.
- Contraction hierarchies: our road graph is tiny.
- Lane changing, overtaking and uber-turns: we have one lane each way.
- Bikes and light rail, for now.
- The SEIR pandemic model.

---

## Why it fits, and what the last attempt taught us

Commit `930a1c3` reverted our first traffic system: RoadSense, junctions,
signals, crossings and traffic settings, about 4,000 lines. The commit
message says it was **"slow and not what we want."** Its junction model was
SUMO's. It worked out foes and right of way for every movement, and checked
them for every car near a junction, every tick.

A/B Street avoids that cost by design:

- **It only does work when something changes.** A car crossing a road costs
  nothing until its interval ends. A car waiting at a junction costs nothing
  until the junction wakes it.
- **Junction logic runs on arrival and release events,** not every tick for
  every car near the junction.
- **Its retrospective's lessons** ([T18] in city-research.md):
  - players rarely miss smooth acceleration;
  - gridlock is the failure that matters;
  - stricter priority rules made gridlock worse, not better (see
    `sim/src/mechanics/intersection.rs:868-879`).

The server sim has a budget of 1 ms a town a tick, and spends about 0.14 ms
today with 70 wandering cars (`sim/README.md`). An event-driven sim keeps
that budget with real populations, because most people are indoors most of
the day, and an indoor person costs nothing.

Some of the reverted geometry is still worth reusing: legs sorted by angle,
and two movements being foes if their chords cross (`git show
930a1c3^:unity/Assets/Porchlight/Runtime/Shared/Explore/Junctions.cs`). The
control logic is what to replace.

---

## How the two map onto each other

| A/B Street | Porchlight today | Port as |
| --- | --- | --- |
| `Lane` (one direction of a road, with a length) | `RoadStroke` (`Explore/Layout.cs:48`) driven `dir = ±1`; one lane each way | a **directed road**: (road, dir). Two per road, one queue each |
| `Intersection` | a road-graph node (`RoadStroke.from/to`, `Traffic.byNode`) | a junction; nodes where only 2 roads meet are bends, not junctions |
| `Turn` / `Movement` | `Traffic.Corner`'s Bézier from one road's lane into the next | a movement (in-road, out-road) with its own short queue and length |
| `Building` with `driving_connection` / `sidewalk_pos` | buildings with `Homes.Door`, driveways and zoning | a building with a door spot on its sidewalk and a kerb spot on its road |
| `Border` intersections | the four gates and the county highways (`docs/highways.md`) | trip endpoints off the map |
| `ParkingSpot::Onstreet / Offstreet / Lot` | `ParkedCar`s along the parking lane, plus driveways (scenery) | real spots, with today's parked cars as their first occupants |
| `Time` (seconds since midnight) | none (night is a toggle) | a town clock, server-side |
| `Scenario` / `PersonSpec` | none: `min(70, length/12)` cars and people at random | made from zoning when the town is made |
| `Analytics` | Manage's live map | per-town counters, sent to Manage |

---

## 1. The clock and the event queue

### What A/B Street does

`sim/src/scheduler.rs` (229 lines):

- **Commands:**
  - `StartTrip(person, trip)`
  - `UpdateCar(car)` and `UpdateLaggyHead(car)`
  - `UpdatePed(ped)`
  - `UpdateIntersection(i)` (signals only)
  - `SpawnCar`, `SpawnPed` and `StartBus`
- **One pending command per key.** Each command reduces to a `CommandType`
  key such as `Car(id)`, and only one may be pending for each key.
- **Storage:** a `BinaryHeap<(Time, CommandType)>`, plus a
  `HashMap<CommandType, (Command, Time)>` holding the real command and its
  time.
- **`update`** reschedules by overwriting the map entry and pushing a new
  heap item. **`cancel`** removes the map entry.
- **`get_next`** pops from the heap and skips any item whose map entry is
  gone or now has a different time (lazy deletion). This gives you
  decrease-key without a mutable heap.
- **Time** (`sim/src/sim/mod.rs:389-423`, `minimal_step`) jumps straight to
  the next event's time. A step is "run every event up to `now + dt`".
- **Positions between events:** a car's state holds a `TimeInterval` and a
  `DistanceInterval` (`sim/src/lib.rs:510-551`). Its front at time t is
  `lerp(dist_int, percent(time_int, t))`, capped by the car ahead
  (`sim/src/mechanics/queue.rs:238`). Rendering asks for every car at `now`
  (`driving.rs:1359`).
- **Speed:** constant on each road. It is the road's limit or the vehicle's
  maximum, whichever is lower; on a turn it is the lower of the two roads'
  limits (`map_model/src/traversable.rs:234-250`). There is **no
  acceleration**.

### Port

- **`Core/EventQueue.cs`:** .NET's `PriorityQueue<TKey, TPriority>` plus a
  `Dictionary<key, (cmd, time)>`, with the same lazy deletion. About 100
  lines.
- **Time as an integer** (milliseconds since the town's midnight), not a
  double. Order equal times by the command key, so a run is the same on
  every machine. A/B Street gets the same effect from `BTreeMap`s and
  `+Duration::EPSILON` nudges (`intersection.rs:212-285`).
- **`SimTown.Tick`** (`sim/src/SimTown.cs`, `TICK = 0.1`) becomes "run
  events up to `now`, then sample positions for the wire". The 0.05 s
  `SLICE` sub-steps go away.
- **The clock is server state.** It is sent with each full town and is a
  function of real time after that, so clients need no ticks of their own.
  Night becomes a time of day (city-research §5, "A town clock").

### Bonus: intervals suit our wire format

`Delta.cs` sends a car only when the client's dead reckoning (carry on along
its heading at its speed) would have it wrong. A car crossing a straight
road at constant speed is exactly what dead reckoning predicts, so it costs
nothing on the wire until its interval ends.

Curved roads still cost deltas. A later step could send the interval itself
(road, dir, s₀, t₀, t₁) and have clients lerp along the road polyline they
already have. That's an option, not a requirement: today's 7-byte records
keep working unchanged.

---

## 2. Cars: lanes as queues

### What A/B Street does

**Car states** (`sim/src/mechanics/car.rs:323-354`):
- `Crossing{time_int, dist_int}`
- `Queued{blocked_since}`
- `WaitingToAdvance{blocked_since}`
- `Unparking` and `Parking`
- `IdlingAtStop` (buses)
- `ChangingLanes`, which we don't need

**The cycle** (`driving.rs:299-318, 384-572`):

1. A car starts `Crossing` a lane. Its event fires at the time it would
   reach the end at the speed limit.
2. When that event fires, the car becomes `Queued`. If it is first in its
   queue, it becomes `WaitingToAdvance` instead and asks the junction to
   start its turn.
3. If the junction refuses, **the car schedules nothing**. The junction
   wakes it later.
4. If the junction accepts:
   - the car joins the turn's queue and starts a new `Crossing`;
   - it stays the old lane's **laggy head** until its back has cleared,
     which takes `length + FOLLOWING_DISTANCE` of travel and is scheduled as
     `UpdateLaggyHead`.

**The queue** (`queue.rs:31-45, 116-294`):
- One queue per lane and one per turn. Each holds its members in order, the
  laggy head, the lane's length and its `reserved_length`.
- Each car's front is `min(where its interval puts it, back of the car ahead
  − FOLLOWING_DISTANCE)`. That is the whole following model.
- A car that catches up with its leader just sits at that bound, still on
  its old interval.
- When a leader leaves, `update_follower` (`driving.rs:870-960`) gives the
  follower a fresh interval from where it actually is, so it never jumps.

**Room** (`queue.rs:378-404`):
- `room_for_car` means `reserved + length + FD < lane length`, or the lane
  is empty. One over-long car is always let in.
- Room is reserved when a turn is accepted and freed when the laggy head
  clears (`driving.rs:1030-1058`).

**Constants** (`sim/src/lib.rs:74-87`):

| Constant | Value | In tiles |
| --- | --- | --- |
| `FOLLOWING_DISTANCE` | 1.0 m, at every speed | 0.25 |
| Car length | 4.5–6.5 m, uniform | 1.1–1.6 |
| Bus length | 12.5 m | 3.1 |
| Default speed limit (no `maxspeed`) | 20 mph = 8.9 m/s | 2.2 /s |
| Primary/secondary roads | 40 mph = 17.9 m/s | 4.5 /s |
| Living street | 20 km/h = 5.6 m/s | 1.4 /s |
| Service road | 10 mph = 4.5 m/s | 1.1 /s |

Speed limits come from `map_model/src/objects/road.rs:246-283`.

### Porchlight today

`Explore/Traffic.cs` (463 lines):
- Each car eases its speed toward `CRUISE = 2.4` tiles/s.
- It stops under `GAP = 1.6` tiles, judged by checking every car on the same
  road each tick, which is O(n²).
- It picks a random road at each junction and passes through other cars
  there.
- After `PATIENCE = 4` s blocked, it turns round.

### Port

- **A `Lane`** for each (road, dir), holding a `List<int>` of cars in order,
  a laggy head and `reserved`. Its length comes from `Traffic.lengths`.
- **`TrafficCar` gains** `t0, t1, s0, s1` (its interval), `state`, a
  `length`, and the route it's following (§5).
- **`Positions()`** walks each lane front to back, lerps, caps at the leader,
  and then applies today's `Place()`: lane offset, `Corner` Béziers and
  `UTurn`.
  - Keep `Corner` as the drawing of a turn, but make the turn a real queue
    of its own with its own length, so a car in the junction has somewhere
    to be.
  - Keep the U-turn at dead ends as a movement from (road, dir) to
    (road, −dir).
- **Speed by road kind** (city-research §1, §4):
  - `Main` 50 km/h = 3.5 tiles/s
  - `Street` 30 km/h = 2.1 tiles/s
  - turns at the lower of the two roads' limits
  - U-turns at today's `UTURN_SPEED` share
- **Car lengths** from the catalog model's real size, or 1.1–1.6 tiles.
  Following distance 0.25 tiles.
- **Easing for looks only.** A/B Street has no acceleration, and its
  retrospective says nobody missed it. If cars snapping from 0 to cruise
  looks bad up close, ease the *drawn* speed on the client (`WorldSync`
  already eases positions), not the sim's.
- **Keep `BlockedAhead`** (the player and obstacles in the lane) as a
  blockage in the queue: a fixed interval nobody may pass. A/B Street's
  `StaticBlockage` (`queue.rs:12-17`) is exactly this. Re-plan the car if
  the blockage stays (today's `PATIENCE`).
- **Drop:** `ChangingLanes`, `DynamicBlockage`, overtaking, and
  opportunistic lane choice (`router.rs:324-400`). With one lane each way
  they never happen.

---

## 3. Junctions

### What A/B Street does

`sim/src/mechanics/intersection.rs` (1,175 lines).

**State:**
- Each junction keeps `accepted` (turns in progress), `waiting` (requests
  and their arrival times) and an optional signal state.
- One global `blocked_by: Set<(car, car)>`.

**`maybe_start_turn`** (390-595) runs in order:

1. Record the request in `waiting`. It is urgent if its lane is overflowing.
2. Refuse if the turn **conflicts** with any accepted turn. Every conflict
   adds a `blocked_by` edge. Two turns conflict if:
   - they end in the same lane, or
   - their paths cross, unless they start from the same lane.

   See `map_model/src/objects/turn.rs:83-103`.
3. Apply the junction's control: stop sign or signal (below).
4. **Don't block the box:** reserve room on the lane out (`queue.rs:378`).
   If there's none, refuse, and add a `blocked_by` edge to whatever fills
   that lane.
5. **Break gridlock:** if the `blocked_by` graph now has a cycle back to
   this car (a DFS, `detect_conflict_cycle`, 1061-1103), reserve anyway. One
   car blocks the box and the jam unwinds.
6. Move the request from `waiting` to `accepted`.

**Waking** (`wakeup_waiting`, 212-285):
- When a turn finishes or room frees, every request waiting at that
  junction is rescheduled for now.
- Order: urgent first, then by how long each has waited, then protected
  before yield. Each gets a tiny nudge so the order is fixed.
- Waiting cars never poll.

**Stop signs** (845-916, placement in `map_model/src/objects/stop_signs.rs:54-154`):
- **Placement:**
  - a junction of 2 roads has no stop;
  - if every road has the same rank, it's an **all-way stop**;
  - otherwise every road below the top rank stops.
- **Behaviour:**
  - a car that has to stop waits **0.5 s** after arriving, then asks again;
  - after that it's **first come, first served**, which falls out of event
    order.
- The comments say a richer rule ("yield waits for every protected car")
  caused gridlock and was taken out.

**Signals:**
- **Plans** come from templates tried in order (`map_model/src/make/traffic_signals/mod.rs:21-70`):
  - four-way two-stage: N/S straight protected with turns yielding, then
    E/W;
  - three-way: the straight-through pair gets the main stage;
  - then fallbacks.
- **Timing:** stages are **30 s fixed** by default.
  - A plan is valid only if every movement is covered and each stage gives
    walkers its crossing length at 1.4 m/s.
  - Variable stages extend in steps while protected cars wait (288-381).
- **Behaviour:**
  - a yielding car waits 0.2 s;
  - **no car starts a turn it can't finish before the stage ends** (918-978).
- **Where signals go:** A/B Street takes them from OSM tags and has no rule
  of its own.

**Exemptions:**
- Junctions of 2 roads may always be blocked (`intersection.rs:1137-1175`).
- Clusters of signals joined by roads of 25 m or less are "uber-turns",
  reserved all at once.

### Port

- **Junctions:** every node with 3 or more roads. Dead ends get the U-turn
  movement, and 2-road nodes are free-flowing bends.
- **Movements:** with one lane each way, a junction of k roads has k(k−1)
  movements. Precompute each junction's conflict table when the town is
  made, as a bitset per movement. Two movements conflict if:
  - they leave by the same (road, dir), or
  - their `Corner` curves cross.

  The reverted `Junctions.cs` already did this geometry.
- **Control:**
  - **Stop signs by road rank**, after `stop_signs.rs`: equal ranks make an
    all-way stop; otherwise the minor roads stop.
  - **Signals** on Main × Main junctions of 3 or more roads (A/B Street has
    no rule; city-research §4 sets ours).
  - Today's ranks are only `Main` and `Street`. When the road hierarchy
    lands (city-research §1), collectors slot in between.
- **Signal plans:** port only the four-way two-stage and three-way
  templates, with 30 s fixed stages. The stage is a pure function of the
  clock and the junction's offset, so clients light the lamps with nothing
  sent, as the reverted code did.
- **Port exactly as written:**
  - don't block the box by reservation;
  - the `blocked_by` cycle check that lets one car through;
  - the wake-up order.

  Together these are why A/B Street's full days finish.
- **Skip uber-turns** unless `Checks traffic-day` (§9) finds gridlock at
  signals a few tiles apart. Our streets are platted into blocks 9 tiles
  deep, so this should be rare.
- **The player** is a blockage on whichever lane they stand in. A car
  waiting behind them is just `Queued`, which is what `Traffic.BlockedAhead`
  does today.

---

## 4. People and their day

### What A/B Street does

**The data:**
- `Scenario` → `PersonSpec` → a list of `IndividTrip{depart, origin,
  destination, mode, purpose}` (`synthpop/src/scenario.rs:16-69`).
- A person's trips must chain: each starts where the last ended, and
  departures increase (`check_schedule`, :166).
- An endpoint is a building, a border, or "suddenly appear"
  (`endpoint.rs:9`).

**The proletariat robot** (`synthpop/src/make/activity_model.rs`), for maps
with no census:

1. Make one slot per resident (their home building) and one per job (their
   workplace). Shuffle both lists.
2. Trips = **1.2 × (residents + workers)**. The extra 20% is through
   traffic.
3. Work out two probabilities, each clamped to 0.05–0.90:
   - `p_local_resident = residents / trips`
   - `p_local_worker = workers / trips`
4. For each person, roll each probability. A success takes a slot; a failure
   takes a random border. That makes four kinds of person: local, commuting
   in, commuting out, and passing through.
5. Departures: **07:00–10:00** to work and **17:00–19:00** home, uniform. 10%
   of people go at a uniform random time instead (background traffic).
6. **Mode** (`select_trip_mode`, :279-316):
   - under 0.5 mi: walk;
   - under 3 mi: 15% bike, else drive;
   - beyond: 30% bus, else drive;
   - anything to or from a border drives.

**Richer days** (`popdat/src/activities.rs`):
- **Workers:** start 6–9h, breakfast 80%, work 4–5h, lunch, work 4h,
  errands 80%, home.
- **Students:** start 8–11h, school, lunch 30%, entertainment 60%.
- 30–60 min of slack between activities.
- Each activity maps to a kind of building (cafe → breakfast, pub → lunch;
  `make_person.rs:62-150`). A border is used if there's none.

**State** (`sim/src/trips.rs`):
- A `Person` (:1387) is `Inside(building) | Trip(id) | OffMap`.
- Each `Trip` (:1310) holds a queue of `TripLeg`s: `Walk(spot) |
  Drive(car, ParkNear(b) | Border) | RideBus(route, stop)`.
- A drive is `[walk to the car, drive and park near b, walk to b]`
  (`sim/src/make/spawner.rs:110-118`).
- A trip that comes due while the last is still going waits in
  `delayed_trips` and starts when it ends (:140-160, :897).
- **Failure** (`cancel_trip`, :976): no path, no parking, or no room to
  spawn. The person is put at the trip's destination, and their car on a
  free spot near it, so tomorrow still works.

**Cars belong to people** (`sim/src/sim/scenario.rs:106`):
- A drive uses the person's car if it's parked where the trip starts.
- Otherwise the person gets a new car, parked at home.
- Parked cars are seeded by a BFS out from the home's road (:313).

### Porchlight today

- Zoning (`Explore/Zoning.cs`) gives every building its `homes` and `jobs`.
  It also sets the town's residents (2.5 a home) and workers (1.2 a home),
  and balances jobs to about one per worker.
- Townsfolk (`Explore/Actors.cs`, `Walking.cs`) are `min(70,
  sidewalkLength/12)` random walkers. They walk at **0.9–1.5 tiles/s =
  3.6–6 m/s, a jog**.

### Port

- **People, made from zoning when the town is made** (`Explore/Population.cs`).
  This is pure and seeded like zoning, so the game, the sim and Checks make
  the same people.
  - Run the proletariat robot over `ZonedBuilding.homes/jobs`, with the four
    gates and county highways as the borders.
  - The 1.2 through-traffic factor and the 0.05–0.90 clamps carry over
    unchanged. Through traffic is what fills the highways
    (city-research §4, "Traffic through the gates").
- **Scale the mode thresholds.** A/B Street's 0.5 mi (800 m = 200 tiles) is
  most of our 256-tile town, so nearly every trip would be a walk. Start
  with:
  - walk under 100 tiles (400 m);
  - drive if the household has a car;
  - walk otherwise.

  Tune until the streets look right, and log the shares in
  `Checks zoning`.
- **Then the richer days:** popdat's worker and student chains. Map
  breakfast, lunch and errands to `Zone.Retail` / `MixedUse` buildings, and
  school to `Civic` until schools exist (city-research §2). That gives
  about 3 trips a person a day ([T33]).
- **`Person{home, work, trips, state, car}`** with A/B Street's three
  states. **Indoors costs nothing:** a person is just their next
  `StartTrip` in the event queue. This is the level of detail city-research
  §5 asks for, for free.
- **Keep failure-warping.** It is what stops a bad seed from wedging a
  town.
- **Walking pace** (city-research §5's one-line fix), from A/B Street:
  uniform 2–3 mph = **0.9–1.34 m/s = 0.22–0.34 tiles/s**
  (`sim/src/sim/scenario.rs:225`). A few joggers above that.
- **Sample, don't simulate everyone, if we have to.** A town of a few
  thousand residents makes thousands of trips a day. A/B Street simulates
  every one; MATSim simulates a 10% sample with capacities scaled to match
  ([T21]). Measure first with the bench (§9). If peak-hour cars on the wire
  get too many, simulate 1 in N people and scale lane room by 1/N.

### The wire: agents come and go

Today `WorldSync` sends a fixed list of cars and people: a record's place
in the list is its identity, and `LOOKS` announces newcomers. With trips,
agents start and end all day. Two ways to handle that:

- **(recommended) A pool of slots.** A town has N car slots and M person
  slots, sized to its peak. An agent takes a free slot when its trip starts
  and gives it back when it ends. An empty slot is a sentinel record (say
  x = 0xFFFF), which the client hides. The 7-byte record and `Delta.cs`
  don't change.
- **Send ids.** This is cleaner, but it changes the format and every client.

A person's look (`Actors.cs`'s 14 characters) is a function of their id,
so a slot's new occupant is drawn right once the client knows who it is.
Send the id with the slot's first record after it changes hands, much as
`LOOKS` does today.

---

## 5. Routing

### What A/B Street does

`map_model/src/pathfind/`:
- **Graphs:** one per mode. The car graph's nodes are directed roads and its
  edges are movements.
- **Cost** (`vehicles.rs:300`): length ÷ speed limit, plus turn length ÷
  speed, **+30 s for an unprotected turn** (from a stop sign onto a bigger
  road).
- **Walking:** length ÷ 1.34 m/s, with unmarked crossings costing **3×**
  (`pathfind/walking.rs:313`).
- **Engine:** contraction hierarchies (`fast_paths`) for speed on city-sized
  maps.
- **No congestion re-routing.** Costs are free-flow. Cars re-route only to
  hunt for parking, or after a map edit.

### Port

- **Plain Dijkstra or A\*** over (road, dir) nodes. A town has a few hundred
  road segments; contraction hierarchies would be wasted. `StreetRoute`
  (`Explore/StreetRoute.cs`) already does the shortest path over
  `RoadStroke`s. Extend its `Net` with directed edges, travel-time costs
  and the +30 s unprotected turn.
- **Cache one shortest-path tree per destination building cluster**, built
  lazily. Many trips go to the same few workplaces.
- **Walking routes:** `SidewalkWalk` already has a crossing cost. Set it to
  3× the street's width while there are no marked crossings, so walkers
  prefer junctions.
- **Leave congestion-aware routing (BPR) for later.** A/B Street shows a
  convincing day without it. The `blocked_by` cycle-breaking and a
  "stuck too long → re-plan" rule handle the rest.

---

## 6. Parking

### What A/B Street does

**Spots** (`sim/src/mechanics/parking.rs`, 1,072 lines):
- **Kerb:** on-street spots per lane = **floor(length ÷ 8 m) − 2**, with
  none next to the junctions. Spot i is at 8 m × (2 + i).
- **Buildings:** each has **1 private spot** by default, used only by cars
  going to that building (`:461-466`). Garages and lots hold more.

**Picking a spot** (`sim/src/router.rs:198-305`):
- On the last road of its route, a car looks for free spots still ahead. It
  takes the one nearest its building if the building is on this road, or
  else the first one.

**Searching** (`path_to_free_parking_spot`, :561-629):
- If nothing is free, the car runs a Dijkstra over roads.
- Each turn's cost is multiplied by a random factor in [0.1, 0.9), seeded
  from the car and road, so searching cars spread out instead of all taking
  the same loop.
- The search stops at the first road with a free spot. If there's none
  anywhere, the trip is cancelled (and warped).

**Timings** (`driving.rs:66-69`): unparking 10 s and parking 15 s at the
kerb; 5 s each in a driveway or lot.

**Infinite parking** (`SimOptions.infinite_parking`): every building gets
unlimited spots, for maps with no parking data.

### Porchlight today

- `Layout` puts `ParkedCar`s along the parking lane between each road and
  its sidewalks, and cars on driveways. They're scenery.
- `Traffic` never parks.

### Port

- **Kerb spots:** every 2 tiles (8 m) along each side's parking lane,
  leaving the first and last 2 out. `Layout` already places parked cars
  there, so make those slots the spots and today's cars their first
  occupants.
- **Driveways:** each house's driveway is its private spot. Flats and shops
  get kerb parking only, until car parks come (city-research §2).
- **Search:** port the jittered-Dijkstra search as written.
  - Cruising for parking then shows up on its own downtown ([T17]).
  - Players see cars pulling into and out of the same spots their owners
    walk to and from.
- **Park and unpark** take 15 s / 10 s at the kerb and 5 s at a driveway.
  - During that time the car holds its spot and blocks its lane (a
    `StaticBlockage`).
  - Shorten the times if it looks slow in play.
- **Start with infinite parking behind a flag.** That gets trips working
  first; real spots can come one step later.

---

## 7. Walking and crossing

### What A/B Street does

`sim/src/mechanics/walking.rs`, 1,052 lines:
- **No collisions.** Walkers pass through each other on the sidewalk, and
  each walks a segment at a fixed speed.
- **Crowding** (`crowdedness_penalty`, :1022): people ÷ (sidewalk width ×
  length). At **1.5 people/m²** or more, walkers go at half speed.
- **Crossing:** a crosswalk is a turn request at the junction, like a car's.
  - At signals, a walker won't start if they can't finish before the stage
    ends.
  - A walker gives way to a car that has waited longer than the crossing
    time + 3 s (`intersection.rs:887-910`), so walkers can't starve cars.
- **Doors:** going in and out of a building takes the path's length at
  walking speed.

### Port

- **Keep our walkers' looks and social moments** (`Actors.cs`). Swap only
  their brain: a walk leg from a door to a car or a destination, along
  `SidewalkWalk`'s itinerary, as an interval per sidewalk segment.
- **Cross at junctions** as a turn request against the junction's conflict
  table: the crossing conflicts with movements over that road. This gives
  city-research §4's "townsfolk as obstacles" with no extra system.
- **Port the crowd slow-down** for market days and events (city-research
  §6). It's cheap: one count per sidewalk segment.
- **Townsfolk with nowhere to be:** a few wanderers (dog walkers, kids)
  keep today's random walk, so the streets aren't empty between rush hours.
  Give each one a trip that ends back home.

---

## 8. Buses

### What A/B Street does

`sim/src/transit.rs`, 381 lines:
- **Routes:** a list of stops, with a bus every **30 min**. Buses are 12.5 m
  long and stop **10 s** at each stop.
- **Bus states:** driving to stop i → at stop i → … → off the map.
- **Riders:**
  - riders wait at a stop for their route, and board every bus that comes;
    there's no capacity limit;
  - they get off at their stop.
- **Choosing the bus:** the walking graph gets "ride from stop to stop"
  edges, costed at the bus's driving time plus 10 s each end. If the
  shortest walk uses them, the person takes the bus. One route only, with
  no transfers.

### Port (later, after trips and parking)

- **One loop route per town:** the high street plus the gates, with a bus
  every 15–30 min from the catalog's bus model (city-research §4,
  "Vehicles with jobs").
- **Dwelling at a stop** is just a queue blockage for 10 s, so cars behind
  it wait. That is the "holds up the lane" behaviour city-research asks for.
- **Riders** come from the same walking-graph trick.

---

## 9. Analytics, Manage and checks

### What A/B Street records

`sim/src/analytics.rs`, 895 lines:
- **Throughput** per road and per junction, by hour and agent type.
- **Trips:** started and finished trips with their durations and modes, and
  each trip's phases.
- **Signal delay** per movement.
- **Parking:** occupancy over time.
- **Problems** (`:68`):
  - `IntersectionDelay`: a car waited over 30 s or a walker over 15 s;
  - `ArterialIntersectionCrossing`: a walker crossed a main road;
  - `PedestrianOvercrowding`.
- **The `blocked_by` graph** as a debug view (`driving.rs:1609`).

### What A/B Street tests with

- **`prebake`** (`sim/src/prebake.rs`) runs each known scenario for a full
  day with a fixed seed and saves the analytics as a baseline. It **panics
  if more than 200 agents are still out** 3 hours after the day ends, which
  is the gridlock alarm.
- **`traffic_seitan`** runs a day while making random map edits, to shake
  out crashes.

### Port

- **Counters per town on the server:**
  - trips by hour and mode;
  - each road's flow by hour;
  - each junction's delay and problems;
  - parking occupancy.

  Send them to Manage once a minute, like the worker's `STATS` message.
  This is city-research §5's "Manage views", sourced from the sim.
- **`Checks traffic-day`:** port `prebake`'s rule. Make the starter towns
  (`server/towns`) and a few seeds, run each from midnight to 03:00 the next
  day, and **fail if agents are still out** or a car has been stuck over N
  minutes. Print trip counts and mean delays to compare across commits.
- **Keep the bench** (`sim --bench`). It must still hold 1 ms a town a tick,
  now with full populations at the 08:00 peak. That is the test the last
  system failed.
- **Later, a seitan-style fuzz:** close random roads mid-day (as the
  marathon course does) and check nothing wedges.

### Manage: seeing every car and townsperson

"Nothing ticks" doesn't hide anything from Manage. Manage is just one more
"someone asking". The last system was slow because it *decided* motion for
every car near a junction every tick. Here those decisions happen only at
events. Reading a car's position between events is a lerp along its
distance interval, capped by the car ahead: pure arithmetic that changes
nothing in the sim. Every car in a town can be read every feed frame well
inside the 1 ms budget.

**1. Moving agents: sample at the feed's rate.**
- Keep today's feed (`server/src/multiplayer/feed.ts`) as it is. Each
  frame, the worker calls `Positions()` at `now` and packs the 7-byte
  `WorldSync` records it already sends. Focused towns come 10 times a
  second, the rest once a second.
- The cost now scales with agents on a trip, not with the population or
  with traffic physics.
- The slot pool (§4, "The wire") handles agents coming and going for
  Manage as it does for players.

**2. Everyone indoors: show buildings, not dots.**
- An `Inside(building)` person has no position to sample: they are the
  building. Thousands of dots stacked on doorways wouldn't tell you
  anything anyway.
- Keep a count per building, changed by the trip start and end events the
  sim already handles. Nothing is worked out per frame.
- Draw it on Manage's Land use layer as a count or heat. Hovering a building
  lists who's inside and each person's next `StartTrip`.
- Count `OffMap` people at each gate and highway: commuters out, and
  through traffic.
- These counts change only on events. Send them as a small JSON frame when
  they change, or once a second.

**3. Later, optionally: send intervals, not positions.**
- Between events, an agent is `(road, dir, s₀, s₁, t₀, t₁)`. Stream those
  changes and let Manage lerp along the road polylines it already has from
  `/mp/traffic/county`. Motion is smooth at any frame rate, and the wire
  carries only events.
- The catch is the queue cap. A car that has caught its leader stays on its
  old interval, so Manage has to apply the same `min(lerp, leader's back −
  0.25 tiles)` rule. That means knowing who is on each lane. Lane entries
  are events, so it's doable, but it is a second copy of the sim's rule.
  The `--follow`-style drift check would have to cover it.
- Only worth doing if bandwidth or smoothness in Manage turns out to
  matter.

**Clicking one agent.** `/mp/sim/where` already exists. With a real sim
behind it, it can return more than a position:
- the trip's legs, route and destination, and the next scheduled event;
- for a car, whether it is `Queued` or `WaitingToAdvance`, and what it's
  blocked by (`blocked_by`).

That is A/B Street's debug view, and it's what makes gridlock legible.

**When:** 1 and 2 come with step 5 of the plan ("People"), since they fit
today's wire with almost no change. 3 waits until it's needed.

---

## 10. Other A/B Street apps worth a look

- **`apps/fifteen_min`** gives each building's walk-time isochrone to
  shops, schools and so on (`isochrone.rs:65`, `times_from`). A ready-made
  planning check for `Checks zoning`: the share of homes within 10 minutes'
  walk of a shop, a school or a park (city-research §2).
- **`apps/ltn`** (low-traffic neighbourhoods) finds **rat runs**: shortest
  paths that cut through a neighbourhood between its main roads
  (`logic/shortcuts.rs`). That's the check for city-research §1's
  neighbourhood units: arterials round the edge, nothing through the
  middle.
- **`apps/santa`** is a delivery game. Houses are scored by their housing
  units, the player refuels at shops, and the score ticks down as they go
  (`buildings.rs`). That's a model for a sleigh mission (`Explore/Sleigh.cs`)
  scored by real households from zoning.
- **The SEIR model** (`sim/src/pandemic`) spreads a state between people
  sharing a building or a bus. A playful version could spread news or
  gossip through townsfolk once they have homes and jobs. That's an idea,
  not a plan.

---

## The plan

**Done.** All eight steps are in. [town-life.md](town-life.md) describes
what was built, and how it's checked. Each step lines up with a step of
city-research.md's list (in brackets).

1. **Cheap fixes** [1], done:
   - walking pace 0.22–0.34 tiles/s, plus 5% joggers;
   - speed limits by `RoadKind`, and slower on bends;
   - following distance 0.25 tiles.

   Car lengths are 1.1–1.6 tiles per car, since the catalog doesn't carry
   lengths yet.
2. **The clock and the event queue** [2], done:
   - `Core/EventQueue.cs`;
   - the real-time county clock (`Life/Clock.cs`);
   - the server worker runs each town's events to its clock every tick.

   Night is still drawn by the client's own sky. The clock's `IsNight`
   is there for the room.
3. **Cars as queues** [7], done: intervals, laggy heads and committed room
   (`Life/Cars.cs`), behind `Traffic`'s `Positions()` / `Take()`. The bench
   costs 0.18 ms a town.
4. **Junctions** [5], done:
   - conflicts from crossing curves;
   - Free, Stop, All-way stop and two-stage Signal;
   - don't block the box, and `blockedBy` cycles broken;
   - `Checks traffic-day`.
5. **People** [3], done:
   - the proletariat robot from zoning;
   - person states;
   - the slot pool on the wire, with the hidden sentinel;
   - walkers walk their trips, and the wanderers stay;
   - Manage shows who's indoors per building.
6. **Trips by car** [4], done: routes over lanes, and gates at the borders.
   `infiniteParking` is an option.
7. **Parking** [6], done: kerb and driveway spots, the jittered hunt, and
   park/unpark timings.
8. **Richer days, buses, Manage** [3, 4, 10], done:
   - activity chains (work, lunch, shops, school, visits);
   - one bus loop, ridden by the timetable;
   - the figures and problems in Manage's Town life panel.

**The host fallback still works.** With `SIM=off`, one player's game runs
the town's life from the clock (it's all in `Runtime/Shared`). A player
taking over restarts it from everyone's day at the time now.

---

## Open questions, answered

- **How long is a day?** Real time: 24 hours, in the county's time zone
  (UTC−5). Rush hour is when it's rush hour.
- **Do all players share one clock?** Yes, one clock county-wide, so night
  is the same everywhere.
- **How many people do we simulate?** Everyone: 5,900 in Porchlight, a day
  in 3.5 s. Only those out are drawn, through fixed slots.
- **Does a stopped car snapping to cruise speed look fine up close?** Speed
  changes stay instant in the simulation. Drawing eases a car's heading
  (its back wheels follow its front). Turns are taken at the turn's speed
  until the car's back is clear.

---

## Licence

A/B Street is Apache-2.0. Porchlight took its **ideas** (event queues,
queue following, reservations, cycle breaking, the proletariat robot) and
no code. Nothing was copied or translated from its source. The code in
`Runtime/Shared/Life/` is our own implementation, written from this doc's
description of how the parts behave. Apache-2.0's terms for redistributing
its code and derivatives therefore don't apply. We credit A/B Street
anyway, in [town-life.md](town-life.md#credits).

## References

- A/B Street: https://github.com/a-b-street/abstreet (local: `../abstreet`)
- Discrete-event traffic sim:
  https://a-b-street.github.io/docs/tech/trafficsim/discrete_event.html
- Gridlock: https://a-b-street.github.io/docs/tech/trafficsim/gridlock.html
- Retrospective:
  https://a-b-street.github.io/docs/project/history/retrospective/index.html
- See also [city-research.md](city-research.md) for [P#]/[T#] citations.

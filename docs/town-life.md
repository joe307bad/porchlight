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
| `Life/Services.cs` | Bin lorries on their weekly rounds, and ambulances and a fire engine on calls, with drivers pulling over for sirens. |
| `Life/Signals.cs` | Signal timing by Webster's method, every quarter hour. |
| `Life/Population.cs` | Households, and who works where (a gravity model). |
| `Life/Gridlock.cs` | Giving up in the traffic: a car that hasn't moved in 10 minutes. |
| `Life/Congestion.cs` | Routing by the traffic: each lane's hold-up measured, and held-up drivers taking a quicker way. |
| `Life/Focus.cs` | Manage's focus on one car or person: its decision tree, its queue, and its way ahead. |
| `Life/TownKey.cs` | Parses a town's key (shared with the server worker). |
| `Explore/Traffic.cs` | What the game draws: the slots as cars and walkers, plus the boats. |

---

## 1. Time

### The clock

The town clock is **real time**, in milliseconds since 1970, shown in US
Eastern time and on a 12-hour clock ("8:05 AM"). It's the same in every
town and for every player, so it's night (8 PM–6:30 AM) everywhere at once,
and rush hour is when it's rush hour on the East Coast.

- `TownClock.Now()` is the clock.
- `ZoneHours(moment)` is the county's offset from UTC then: −4 in daylight
  saving time (from 2 AM on the second Sunday of March to 2 AM on the first
  Sunday of November, the US rule since 2007), else −5. It's worked out
  from the date alone, so it doesn't depend on the machine's own zone or
  time zone database.
- `TimeOfDay`, `DayOf` and `At(day, timeOfDay)` do the arithmetic in local
  time. On the spring-forward day `At` takes a time that doesn't exist
  (2:30 AM) to the hour after it; on the fall-back day, a time that comes
  twice is the first.
- `Format` ("8:05 AM"), `FormatSeconds` ("8:05:30 AM") and `Clock12` say a
  time. Midnight is 12:00 AM, noon 12:00 PM.

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
- **Speed limits:** 4.3 tiles/s on Main roads (about 61 km/h) and 2.6 on
  streets (about 37 km/h). They were 3.5 and 2.1 (50 and 30 km/h, a real
  town's), but at a game's scale cars looked like they were crawling, so
  the limits are a fifth over, cars corner at 0.38 g, and swing round a bend
  in a lane at up to 1.4 rad/s (`SWING`). Pulling away and braking stay as
  gentle as they were. Faster limits need cars to see what's ahead in time
  (next bullet): before they did, a tenth over was as far as the
  smoothness check (§ below) allowed, and 65/40 km/h passes it now but
  with turns at its very limit.
- **Looking ahead:** a car chooses how fast to come onto its next segment by
  looking up to four segments down its route (`Arrival`): no faster than it
  can still slow, braking at `BRAKE`, for each one's limit (a turn's swing),
  a stop sign, and where it parks; and no faster than it can still stop for
  a traffic light that may be red by then, braking at `AMBER` (3.4 m/s²,
  the rate ITE's guidance times amber lights for). Without that, at 61 km/h
  a car came onto a short lane with a light at its end, learned there it
  was red, and braked at 1.3 tiles/s². Looking only at the next segment, a car
  came off a junction at full speed onto a short lane with a stop sign at
  its end and had to brake three times as hard as it should; at the faster
  limits that was 1.1–1.5% of all moments driven, against a limit of 1%. With
  the look-ahead it's 0.2–0.8%. (Whether a light's red, and turns it'll be
  judged to wait for, it still only learns when it gets near: they change.
  It's only ready for them.)
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
  direction, so the heading never jumps. It's kept as 16 points along the
  curve, and a car's heading is blended from the way the curve goes at one
  point to the way at the next (`RoadNet.MovementPoint`), rather than
  taken from the straight piece between them. Taken that way, it stepped
  9° at each of the 11 points a turn had, and the car's front flicked with
  it.
- A straight-on turn keeps the slower lane's speed. A turn goes at up to
  0.6 of it, and no faster than a driver corners comfortably:
  v = √(a / curvature), with a = 0.38 g (`CORNERING`, brisker than the
  0.32 g side friction AASHTO designs low-speed street curves for at
  15 mph [T48](city-research.md#references)), at the curve's
  tightest car's length. A tile's about 4 m (55 km/h is 3.85 tiles a
  second). The shares below were measured at 0.32 g. Turns run at about 38% of the road's limit (19 km/h off a
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

**Signals** have two stages, one per pair of opposite legs: the
straight-on movements of a pair go together, and turns across them give way
to cars crossing (gap acceptance, below). At a T, the two legs that run most nearly straight through
share a stage and the stem has the other. This is A/B Street's
`four_way_two_stage` and `three_way` plans.

- The stage is a pure function of the clock, a per-junction offset and the
  two stages' greens, so any two copies of the town with the same timing
  agree on it.

**Webster timing** (`Signals.cs`). Each stage starts at 30 s. Every quarter
hour (the HCM's analysis period) every signal is retimed from the cars it
let through in the quarter just gone, by Webster's method (Webster 1958,
*Traffic signal settings*) [T10](city-research.md#references):

- each stage's critical flow ratio y is its busiest lane's flow over the
  saturation flow, 1,900 cars an hour of green [T8];
- the cycle is C₀ = (1.5 L + 5) / (1 − Σy), with L = 8 s lost (4 s a
  stage: starting up, and the end of the amber nobody uses);
- the green, C₀ − L, is shared in proportion to the y's, and each stage gets
  its lost time back.

The cycle is held between 40 s (Webster's formula wants less when it's
quiet, but walkers need to get across) and 120 s, and a stage between 12 s
and 60 s, in whole seconds. A busy main road crossing a quiet one gets the
long green, and a quiet junction cycles quickly. The stage showing keeps its
end (or ends sooner, if its new green is shorter), so no light jumps.
`stats.retimed` counts the changes. Players following the town are told each
light's greens with its stage (`EncodeLights`, two bytes a light after the
stages), and Manage gets them as `greens`. An older player reads the stages
and stops there, timing them at 30 s.
- A car never starts a turn it can't finish before its green ends (its
  length plus the turn, at the turn's speed).

**A light changes for a car waiting** (`skipEmpty`). A car at red asks
again; if nobody is at, near (4 s at the limit) or crossing the junction on
the turns that are green, and the green has run at least 8 s, the light
changes now: the junction's offset moves so its stage is the car's, its
whole green from now. Every copy still agrees, since the stage is still the
clock plus the offset. A quiet side street no longer holds a car for half a
minute in front of an empty main road. `stats.lightsChanged` counts them.

**Stop signs** cost a 500 ms stop before a car may ask to go. Crossing or
joining a bigger road from a stop sign is *unprotected*: routes count it as
an extra 30 s, so drivers prefer the signals.

**Gap acceptance** (`GivesWay`, `CriticalGap`, `GapClear`). A driver who
gives way waits for a gap in the traffic they give way to, as the HCM's
two-way-stop method has it [T12](city-research.md#references):

- at a two-way stop, a turn that stops gives way to the turns that don't;
- between two turns that both have the road (both off the major road at a
  stop, or on the same green at a signal), a left turn or U-turn gives way to
  what's coming straight on or turning right.

A car coming is one moving up a lane towards a turn it gives way to; one
standing at the line is the junction's to judge, and one that'll meet a red
before it arrives leaves no gap to worry about. The gap needed is the HCM's
base critical headway for a two-lane major road (HCM 2010 Exhibit 19-10):

| Turn | Critical gap |
| --- | --- |
| left off the major road, across what's coming | 4.1 s |
| right out of the minor road | 6.2 s |
| straight across from the minor road | 6.5 s |
| left out of the minor road | 7.1 s |

The longer a driver has waited, the shorter the gap they'll take: 2 s
shorter after a minute, never under 3 s (Mahmassani & Sheffi 1981 measured
the critical gap falling with the wait). Without that, a side street on a
busy main road can starve. Refused, a car asks again when the cars too near
have gone through. `stats.noGap` counts the refusals. Which way a turn goes,
from its driver's seat, is `Movement.turn`.

### Signals and signs you can see

`Life/StreetFurniture.cs` says where each one stands, and both the game and
Manage draw from it, so they agree:

- a **signal post** for every lane into a signal;
- a **stop sign** for every lane that stops.

Each stands on the **corner of the sidewalk** to the driver's right, facing
back down the lane:

- **Where:** the corner is where that sidewalk meets the cross street's.
  Layout ends the sidewalk there, so the post stands where the sidewalk
  ends. It's a quarter of a sidewalk in from its middle, towards the kerb.
- **No corner:** on a T's far side, where the sidewalk runs straight past,
  the post stands level with the corner across the road, so the posts line
  up across the junction.
- **Straight-on roads:** the road carrying straight on (within about 30°)
  isn't a cross street.
- **Fallback:** a lane with no corner either side keeps the old place, on
  the kerb at the stop line.
- **Arm:** a signal's arm reaches from the post to over the middle of the
  lane, however far that is.

They used to stand 0.35 tiles out from the road's edge, at the stop line,
which is on the parking strip and short of the corner. `Checks furniture`
checks two things:

- every post is within 0.3 tiles of a sidewalk's end, or level with one
  across the road (all 1,158 in the starter towns);
- it stands on a sidewalk. One in Lanternport stands where Layout cuts a
  T's far sidewalk short at a bend, so the check allows 1 in 100.
`Showing(junction, stage, moment)` gives what a lane's light shows: red
while the other stage is green, green, then amber for the last
`AMBER_MS` (3 s; ITE's rule of about a second for each 10 mph) before its
stage ends. A light that changes early for a waiting car moves the
junction's offset, so its amber comes early with it.

The models come from `unity/Tools~/streets/signals.py`. It's a Blender
script, run with `blender --background --python unity/Tools~/streets/signals.py`,
and writes `Models/props/traffic-signal.glb` and `stop-sign.glb`:

- **traffic-signal:** a mast-arm signal, as American towns hang them. It has
  a 5.6 m pole on the kerb and a 4.2 m arm with a brace. One three-lamp head
  hangs over the lane, with a second on the pole for drivers stopped close
  to it. Its lenses are dark.
- **stop-sign:** a red octagon with a white border and STOP, on a 2 m post.

`Rendering/Signage.cs` stands them in a town (`ExploreView.Populate`):

- The models are at 0.2 tiles a metre (a tile is 4 m), like the street lamps.
- Each signal is stretched along its arm, so the head hangs over the middle
  of its lane, however wide the road.
- Each lamp is a glowing disc in front of its lens: dark red, amber or green
  until lit. Lit lamps glow brighter at night.
- The lamps show the light as the cars obey it:
  - **Following the server's town** (every multiplayer town): from the
    server's word. Each delta ends with each signal's green stage and the
    milliseconds until it ends (`StreetFurniture.EncodeLights`). The player's
    game doesn't run the town's life then, and the server's lights change
    early for waiting cars, so the clock alone was wrong 35–49% of the time.
  - **Running the town here:** from the town's own stages
    (`traffic.life.Now`).
- The lamps are drawn `WorldSync.DELAY` behind the latest word, as the cars
  are. Every stage lasts `STAGE_MS` from its start, even one changed early, so
  a word's stage began `ends − STAGE_MS` before it. Before that, the other
  stage was green (`StreetFurniture.Showing`).

`sim --follow` checks it: in each starter town, a player's lamps match what
the server's showed as the cars are drawn at 99.9% of moments or more (they
come out at 99.97–100%).

---

## 3. Cars: lanes as queues

A car is 1.1–1.6 tiles long (fixed per car; a bus is 3.1). The catalog's
models don't carry their lengths yet.

A car on a lane or turn ("segment") holds an **interval**: it goes from
`s0` at `t0` to `s1` at `t1`. Its front at any moment comes from a speed
profile over the interval (below), capped where the car ahead's back is
less `FOLLOWING` (0.25 tiles). A car never overlaps the one ahead, and
nothing is stepped.

### Braking into a turn, pulling away out of it

Cars used to cover each interval at one speed: full speed down the lane,
then the turn's speed the instant it began, and full speed again the
instant it ended. Drawn, that was a car that lurched into every corner and
hesitated round it. Now each interval is a profile of up to three parts
(`Life/Cars.cs`, `Drive`):

1. **Pull away** at `ACCEL` (0.6 tiles/s², about 2.4 m/s²) from the speed
   the car has (`va`: 0 if it was standing, or held behind someone);
2. **cruise** at the segment's speed (`vp`), if there's room to reach it;
3. **brake** at `BRAKE` (0.75 tiles/s², about 3 m/s²) to the speed it should
   leave the segment at (`vb`).

If there's no room to cruise, the profile is a triangle (up, then straight
down), or one even change of speed. `t1` is the sum of the parts.

The speed it leaves a segment at (`ExitSpeed`) is 0 if it must stop at the
end: the last step of its route, a stop sign, a light that isn't green (or
turns within 3 s), or a junction that won't let it straight through
(`Judge`). Otherwise it's the turn's speed (or, off a turn, the next lane's),
no faster than it could stop from in the room it can see beyond. A car
whose car ahead is stopped or stopping plans to stop behind it (`stopAt`),
and is planned again when that car moves off, or parks and leaves the lane.

A/B Street's cars cross each stretch at one speed, as ours used to; the
profile is our own addition on top of its queue idea.

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

The car behind rolls up on that back meanwhile, with no interval of its
own. So whether the back is clear is looked at again every 100 ms
(`CLEAR_AGAIN_MS`) until it is. Looked at every 250 ms, the car behind could
reach the line first and stand there a moment, stopped dead from speed:
`life`'s smooth-driving rule caught it once a Friday rush.

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

Room in the tally isn't yet room on the road, though: the cars in the lane
may not have closed up, and others may be turning into it ahead. So a car
also needs a **clear way through** (`ClearBeyond`): space for its whole
length past the junction, behind where the last car in the lane stops, less
a car and a gap for each already on a turn into it. Short of that, it waits
at its line, not in the box. While the lane's last car is still moving
there's no stop to measure from, and nothing is counted: a queue rolling
off on a green follows its leader through bumper to bumper, nobody braking
at a green to wait for room. Before this, a car let in on the tally alone
could reach the far side at speed and stop dead in the junction (four times
in an Ember Crossing morning on Market Avenue, a straight main road with a
side street every 8 to 12 tiles).

Following through, the car behind is held back by its leader's back until
that's a full gap (`FOLLOWING`) past the line, not just over it
(`OnClear`). Let go with only its back over, the follower could roll right
up to the line, and on the turn be put back by the gap it hadn't kept: a car
drawn jumping a tile backwards (once in Porchlight's rush).

### Junctions

A car at the front asks to go (`Ask`). The junction's rules are one
function, `Judge`, that changes nothing: it asks its questions in order and
returns go, or why not and when to ask again. `Ask` acts on that verdict.
Manage's focus view calls the same function, so the reasons it shows are
the rule that ran (§11). A car is refused if any of these hold, in this
order:

- it hasn't made its stop yet (stop signs);
- its turn isn't green, or can't be finished on this green (signals);
- it gives way, and there's no gap long enough in what it gives way to;
- **someone's standing on the turn** (a player, below);
- a walker is on a crossing it would cut;
- a turn under way conflicts with its own;
- there's no room on the lane out, or no clear way through to it.

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

- **On a lane:** the first car they're in front of, or beside, slows down
  for them, and that's all it does. It brakes (firmly, about 0.6 g) to stop
  1.2 tiles short. If they step out nearer than it can stop at that, it
  brakes as hard as it must, and never comes within 0.3 tiles. It doesn't
  speed up meanwhile, and a car that's stopped stays stopped. Its stop point
  only ever moves forward, never back past where the car is, so a player
  walking up to a car never makes it back up or lurch. A car they're
  standing beside holds where it is. When they move off, it goes on from
  where it is. They stand in the way of each lane whose middle is within
  0.7 tiles of them (half a car's width and a margin): their own side's,
  and the other side's too when they're near the middle of the road. Every
  road near them counts, not only the nearest: at the mouth of a junction
  the end of the lane coming in is as near as the road across. Buses stop
  for them as cars do.
- **In a junction:** every turn whose curve passes within 0.5 tiles of them
  is blocked. A car already on that turn stops short. No car may start it
  until they've gone, and going wakes the junction.

A car held behind a player waits for them. It used to turn round after
15 s, but that put it on the other lane in one step, a jump, so now it
doesn't. A car that hasn't moved in 10 minutes still gives up, as any car
does (§3).

Before, the first car behind them had its front cut off at 1.2 tiles short
of them, applied at once. Nearer than that, the car jumped back to it, and
as the player walked, back and forth with them. `Checks life` "a player
walking up to a car" walks up to 40 cars from 4 tiles in front of them, past
their side: none goes back, speeds up or comes within 0.3 tiles. The old
way, a car went back 0.03 tiles.

---

## 4. Routes

`Router` finds the quickest way as the roads are:

- each lane's span at its limit, plus its **hold-up** as last measured;
- each turn's curve at its speed;
- a stop's 2 s, and an unprotected turn's 30 s;
- a signal's 7.5 s (`SIGNAL_S`): the wait, on average, of a car turning up
  at any moment, red half the time for half a 30 s stage on average (a
  rough figure since Webster timing, kept so routes stay stable).

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
took over its time at the limit is noted (and logged by the hour for
Manage's congestion slider, §11) (less a signal's 7.5 s, which the
turn already counts). Every 5 minutes each lane's hold-up is worked out
again: half the last figure, half what was measured since, counting the car
waiting at the front too. The router is told, and its trees are made again
from the new times.

A driver held up a minute (queued or waiting at a junction) looks again,
at most every 3 minutes: if there's a way at least a fifth quicker from
where they are, they take it. `stats.rerouted` counts them. So traffic
spreads off a jammed main road onto the streets round it, and back when it
clears.

### BPR: delay before the queue shows

Each lane also counts the cars onto it over the last quarter hour (three
5-minute buckets), and the BPR curve (Bureau of Public Roads 1964)
[T14](city-research.md#references) forecasts its delay from that flow v
and its capacity c:

  delay = t₀ · 0.15 · (v/c)⁴,  t₀ the lane at its limit

held under 10 minutes. A lane's capacity (`Capacity`) is what its end lets
out:

- through, or onto a free junction: the saturation flow, 1,900 an hour;
- at a signal: its share of the green, 1,900 · (green − 4 s lost) / cycle;
- at an all-way stop: a car every 5 s (the HCM's departure headways run
  about 4 to 7 s);
- out of a two-way stop: the HCM's potential capacity for each of its turns
  into the traffic it gives way to (flow v꜀), averaged:
  c = v꜀ e^(−v꜀ t꜀/3600) / (1 − e^(−v꜀ t_f/3600)), with the critical gaps
  t꜀ above and the follow-up headways t_f of 3.3 s (right), 4.0 s (straight)
  and 3.5 s (left) (HCM 2010 Exhibit 19-10).

Every 5 minutes the router takes, lane by lane, the larger of the measured
hold-up and the forecast. A side street out onto a busy main road costs
routes more as the main road fills, before any queue has formed.

### Habits: day on day towards equilibrium

Drivers remember the lanes they took on each trip they drive, by where
from and where to (`Person.habits`). Next time, those lanes cost them a
fifth less (`Router.FAMILIAR`), so they keep to the way they know unless
another's clearly quicker: Mahmassani & Chang's (1987) boundedly rational
drivers, who switch only past an indifference band. And each day one driver
in ten (seeded by the driver, the trip and the day: `REPLAN_ONE_IN`) works
the trip out afresh from the traffic as it is, as MATSim's agents replan a
tenth of their plans each iteration [T21](city-research.md#references).
Whatever way they take becomes the habit.

So the town's routes settle, day on day, towards Wardrop's user
equilibrium [T15](city-research.md#references), where nobody's way is
clearly slower than another they could take, without every driver jumping
to the same street at once the moment it's quicker. A known driver routes
with a forward Dijkstra of their own (no shared tree), about as quick in
these towns. `stats.habits` counts the drives made by habit, and
`stats.keptWay` those that kept wholly to the lanes they knew. Held-up
drivers still look for another way on the road.

`Checks habits [towns] [town] [days]` runs a town day after day: from the
second day at least 60% of drives must be by habit, and no day's mean drive
more than 15% slower than the first.

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
- **Car-park bays:** each paved tile of a car park behind the high street
  ([town-shops.md](town-shops.md)) is a spot anyone may park in, as at the
  kerb. All a car park's bays are entered from the lane at its mouth. The
  car park's scenery cars take their bays for good.

Timings: parking takes 15 s at the kerb and unparking 10 s; a driveway or
a car-park bay takes 5 s either way. A spot is held from the moment a driver picks it, so
two cars never make for the same spot.

`LifeOptions.infiniteParking` gives every building a spot at its door. It's
for tests, and for towns where parking isn't the point.

---

## 6. People and their day

### Who lives here

The population is made from the town's zoning (`Zoning.cs`), household by
household (`Population.cs`).

**Households.** Each building with homes is filled with households drawn
from a fixed mix, seeded by the town and the building, until it holds its
people: three times the plan's 2.5 a home (`CROWD`). The mix is rounded
from the Census Bureau's *America's Families and Living Arrangements: 2023*
(the shares are rounded, not checked line by line):

| Household | Share | Members |
| --- | --- | --- |
| single | 18% | one adult |
| single retiree | 11% | one retiree |
| couple | 17% | two adults |
| retired couple | 12% | two retirees |
| family with one child | 11% | two adults, a child |
| family with two children | 10% | two adults, two children |
| family with three children | 6% | two adults, three children |
| single parent | 8% | an adult, one or two children |
| sharers | 7% | two or three adults |

That's about 2.3 people a household and 29% living alone, near the
national figures. Every member has a role (adult, retiree, child) and an
age (20–64, 65–90, 0–17). Filling each building to zoning's figure keeps
the totals where zoning has them, so no rebalancing (PopulationSim's
[T29](city-research.md#references)) is needed. `TownLife.households` lists
them; each person has their `household`, `role` and `age`.

- **Workers** are the building's working-age adults, as many as the plan's
  1.2 a home, picked in a seeded order. The plan's workers and jobs are kept
  so the rushes are what the roads were laid out for; the extra people's
  trips (to the shops, school and visiting) come through the day, mostly on
  foot. Tripling workers and jobs as well was tried: rush hour tripled and
  Ember Crossing and Porchlight wedged, without the streets looking much
  busier in between.
- **Children** of 5 to 17 go to school (`Student`), the nearest school.
- Everyone else is at home in the day (`AtHome`): younger children,
  retirees, and the working-age adults who aren't the building's workers.
- A building has at most **2 cars a home** (`CARS_A_HOME`): workers' come
  first, then adults and retirees at home in the day share what's left.
  Children don't drive.

**How many work where.** As before households: people = 1.2 × the larger
of workers and jobs, and the chances of a home in town and a job in town are
the workers and the jobs over that, clamped to 0.05–0.9. That fixes how many
residents work in town, how many work out of it, how many commuters come in
for the town's surplus jobs, and how many only drive through, gate to gate.

**Who works where: a doubly-constrained gravity model** (NCHRP Report 716
§4.5 [T35](city-research.md#references)). The town is cut into zones 12
tiles a side, and each gate is a zone standing for the towns beyond. A
worker in zone i works in zone j with odds ∝ jobsⱼ · F(tᵢⱼ), balanced by
Furness's method (40 rounds) so each zone's workers are all placed and each
zone's jobs are each filled once. Residents working out of town work at a
gate, and commuters in come from one. Then each worker in turn draws a zone
by what's left of their zone's trips, and takes that zone's next job.

- F is NCHRP 716's home-based-work friction, e^(−0.123 t) for t minutes of
  driving (its gamma function's power term, −0.02, is all but flat and is
  left out, so it's `DetMath`'s and the same on every machine).
- t is the straight line × 1.3 for the roads, at 3 tiles a second, plus 15
  minutes on beyond a gate. A starter town is drawn far smaller than a real
  town of its people: Porchlight's 16,500 would spread about 4 km across at
  an American town's ~1,000 people a km² (Census 2020 urban areas, about
  2,500 a square mile), not a kilometre. So t is scaled by that ratio
  (`RealScale`, 2.5–4.5 for the starter towns) and the friction reads real
  trip lengths, as NCHRP says to calibrate it.
- At that, commutes in town come out 3–5% shorter than jobs dealt at random
  (Porchlight 87 tiles against 92), and those working out of town leave by
  gates nearer them than the average (129 tiles against 143). NCHRP's
  home-based-work friction is shallow: people will cross a town for a job.

`Checks population [towns]` checks the household mix, that everyone in town
is in one household, that students are children and workers adults, that
no building has more workers than jobs, and the two comparisons above.

The kinds:

| Kind | Who |
| --- | --- |
| Worker | Lives here and works, here or out of town. |
| Student | A child of 5 to 17. |
| AtHome | The rest of the residents. |
| CommuterIn | Lives out of town, works here. |
| Through | Drives from one gate to another. |

**Cars:** 90% of workers and 70% of adults and retirees at home have one,
parked at home, up to the building's two a home. Commuters and through
drivers arrive in theirs.

### Their names

Everyone has a name (`Life/PersonNames.cs`). The first and last names come
from faker-js's English lists (`@faker-js/faker`, MIT), copied in as data by
`unity/Tools~/names/generate.mjs` into `PersonNames.Lists.cs`: 3,005 first
names and 473 last. Only the lists come over; picking one is ours:

- One of the town's people (`TownLife.people`) is named by
  `Rng("<town seed>::person::<id>")`; a wanderer by
  `Rng("<town seed>::wanderer::<k>")`, `k` its place among them (the first
  townsfolk records).
- So the same person has the same name in every player's game, on the server
  and in Manage, every day, without it being sent anywhere. Their id in
  another town is someone else.

`TownLife.DoingOf` says what they're doing in a few words: their trip's
purpose ("Going to work", "Going home", "Going to the shops", "Visiting"…),
or "Indoors" / "Out of town" between trips. A wanderer is always "Out for a
walk".

**Over them in the game.** Walk up to a townsperson and stay by them for
0.9 s (`NAME_DELAY` in `ExploreView.cs`, within 1.6 tiles): a tag comes up
over their head (the players' `NameTag`, in a quiet cream) with their name,
and what they're doing on a smaller line under it. Step away, or to someone
else, and it goes. Where the town is run:

- **here** (playing alone, or the room's host): from the town's life, looked
  at again every 2 s, so it changes when they get where they were going;
- **on the server:** the game asks it once (`GET /mp/sim/focus?…&brief=1` by
  their record, `ExploreView.AskWho`, set in `PorchlightApp.Connect`). The
  delay covers the wait, and an answer that comes after you've moved on is
  dropped.

**In Manage** the same name heads their focus and their crumb, and starts
their line in a town's People list and the map's hover. A car's line and
focus name its driver.

`Checks names` (in CI) builds each starter town:

- everyone is named "First Last", and different people get different names
  (98% or more);
- the town built again hours later gives the same names;
- at 8:15 AM people are going to work, each as their trip has it, and at
  5:30 PM they're going home;
- everyone drawn walking has a name and something they're doing;
- the same ids in the next town are other people.

`server/test/sim.test.ts` checks the sim's brief answer: a name and doing
for someone out walking, matching their focus, and "Out for a walk" for a
wanderer.

### A day

Each person's plan is a chain of stops with departure times. It's seeded by
`{seed}::day::{day}::{id}`, so the same day comes out the same everywhere
and a new day comes out different.

Which day it is comes first: the town's calendar
([town-calendar.md](town-calendar.md)) says whether it's a weekday, a
weekend, a holiday, a school day or market day. Each worker has a roster
(Monday to Friday, the shops' week, shifts, or the school's days) that says
whether they work it.

- **Workers, on a working day:**
  - office hours: leave between 06:30 and 09:30, home 8–9½ hours later;
  - the shops: in before opening, home after closing;
  - shifts: earlies 6 to 2 or lates 2 to 10, a week of each;
  - 30% go out for lunch, 35% go home by way of the shops (while they're open);
  - 10% visit someone in the evening.
- **Students, on a school day:** at school 10–55 minutes before the bell (the
  under-11s mostly taken by a grown-up), home after the last bell, and half
  visit a friend or the shops after.
- **At home, on a weekday:** the school run if there are young children, the
  shops in the morning, half go out in the afternoon, some in the evening.
- **A day off** (the weekend, a holiday, a rest day, no school): the market on
  market day for a few, the week's shop late in the morning, someone to see in
  the afternoon, and on Saturday the evening out.
- **Commuters** come in on their working days; their days off are spent
  beyond the gates.
- **Stallholders** drive in from the gates to set up before the market opens,
  and leave after it shuts.
- **Through drivers** pass in the morning or midday, and back out in the
  afternoon. At the weekend fewer pass, from mid-morning.

A visit goes to a nearby home 60% of the time, otherwise to a shop.
"Nearby" is the nearest of 6 picked at random (`nearErrands`; 3 without):
lunch and visits stay closer to home, so more of them are on foot and the
drives are shorter. Which shop is the Huff model's choice (`huffShops`;
[town-shops.md](town-shops.md)): each shop's pull is the people who work
there over the distance squared, picked in proportion, so the supermarket
draws from across town and a corner store from its own streets. A plan
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
  takes plus 3 s, so cars aren't held for ever on a busy corner. A walker
  also doesn't step out in front of a car coming up at speed to go round
  without stopping (due within twice the crossing's time, for a crowded
  kerb's half pace): it couldn't stop short, and would stop dead at the line.

### A crowd at the kerb

People waiting to cross don't all stand on the one point at the corner.
Each takes a **spot** in a crowd on the sidewalk there (`Walkways.CrowdSpot`,
`KerbCrowds`):

- **The block:** three abreast across the sidewalk, 0.14 tiles (0.56 m)
  apart, kerb side first; then two in the gaps a step behind (0.13 tiles,
  0.52 m); then three again, and so on back along the sidewalk from the
  corner. Nobody is nearer anyone else than 0.5 m. Full, that's about 2.9
  people a m², a dense but orderly queue (Fruin's level E).
- **Front first:** a newcomer takes the frontmost spot free, and keeps it
  till they go. Someone leaving from the middle leaves a gap for the next to
  come; nobody shuffles up.
- **On the sidewalk:** where it bends sharply at the corner, or its curve
  brings it near a carriageway, a place that would stand within 0.5 m of one
  already given, or within 0.05 tiles of a road's edge, is passed over. A
  corner's spots are worked out the first time someone waits there, and kept.
  Past the sidewalk's far end (more than about eleven a tile of it), they
  stand over those in front.
- **Moving:** they walk to their spot from the corner at their pace, stand
  facing the crossing, and step out from it, closing in on the crossing's
  line over the first steps.

On the move, each walker keeps to a **lane** of their own, one of seven
across the middle 0.27 tiles of the way (by their id), easing in and out of
it over the first and last 0.25 tiles of a piece, so a busy sidewalk isn't a
single file of people standing in each other.

A/B Street waits its walkers at the end of the sidewalk, all on one point,
and draws a bunch that overlap as one blob with a count (`DrawPedCrowd`)
[T25](city-research.md#references). We have fewer people and a closer
camera, so we give each a place.

`crowd` in `Tools~/Checks` checks it: the book hands out the frontmost spot
free; at every kerb a crossing sets off from in the starter towns, twenty
waiting stand at least 0.5 m apart, on the sidewalk, off every carriageway,
the first by the corner; and at the morning rush everyone waiting is in
their spot, apart, in the same spot from one look to the next, and facing
the way over.

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

### Bin lorries

Each neighbourhood (from the town plan) is a collection zone, and every home
belongs to the one whose middle is nearest. The zones take Monday to Friday
in turn. On a zone's day at 7:00 (a few minutes later for each zone on the
same day, so they don't all come in together), a lorry comes in by the gate
nearest the zone's first home and works its way round:

- **The order:** from the home nearest a gate, then the nearest home not yet
  done, and so on.
- **Stops:** houses within 6 tiles of the last stop and on the same lane are
  emptied from it, so it stops for a stretch of kerb, not at every door. It
  stops in the lane, not at the kerb, and holds up whoever's behind: 20 s a
  stop plus 8 s for each house after the first, up to a minute (estimates).
- **Lanes:** a home on a road's gate lane (one that only leads out of town),
  or on one coming in from the gate, is served from the lane going the other
  way.
- A stop it can't get to from where it is gets skipped and counted
  (`binSkipped`). The check allows 1 in 20; in the starter towns it's 0.
- When it's done, it leaves by the nearest gate. The depot is beyond the
  town.

A lorry stands in the lane, never in a kerb spot, so it's left out of
looking for a spot on its last lane. A held-up lorry re-routes only to the
stop it's going to, never "the other side of the road" as a resident would.
Both of those once sent a lorry out of town by a gate in the middle of its
round.

### Emergency vehicles

A town has an ambulance for every 12,000 people (at least one) and a fire
engine. Calls come at random, as a Poisson process: each minute a seeded
chance of the vehicle's share of the town's calls a year. The rates are
rough estimates of the national order of magnitude: about 0.09 ambulance
calls a person a year and 0.04 for a fire engine. They aren't a dataset's
figures. A call is at a random building with an address. The fire engine
is kept at the town's fire station ([town-shops.md](town-shops.md)): it
pulls out onto the lane in front, and after the call drives back and goes
in. With no hospital in town, an ambulance comes in from the gate nearest
the call (and so does the fire engine, in a town with no station).

With its **siren going**:

- **It treats a red light and a stop sign as a give-way.** It doesn't stop
  or wait for a gap, only for what's actually in its way.
- **Drivers ahead of it in its lane, within 20 tiles, pull over.** They
  slow to 0.4 tiles a second and are drawn 0.35 of a tile towards the kerb.
  It goes by them; once it's clear of one, it's ahead of that one in the
  lane's queue. A driver already at the line goes, on red if need be, to
  clear the way.
- **Drivers about to enter a junction it's coming to wait for it**: it's on
  a turn through it, or within 6 s of it on a lane into it (`Verdict.Siren`).
  Manage's focus says so: "a siren's coming: it waits for it to go by".

At the call it stands 15 minutes (an ambulance) or 25 (a fire engine), siren
off. The drivers who pulled over go on. Then it goes back out by the nearest
gate without its siren, and waits for its next call.

Service vehicles are cars with no owner (`Car.service`), each with a slot
of its own after the buses', drawn as its own model (`cars/garbage-truck`,
`cars/ambulance`, `cars/firetruck`). A player can take one; it comes back
on its next round or call once they let it go. A lorry or an ambulance
standing at its stop isn't "stuck". In Manage they're under **Services**
(kinds `g` and `e`). Focusing on one says what it's about, such as "A bin
lorry on its Monday round (12 of 77 stops)" or "An ambulance on a call,
siren going".

`Checks services [towns]` runs a weekday morning on which the first zone's
bins are collected, with calls 30 times as often as real so there are
plenty. It fails if:

- the lorry isn't out by 7:30, hasn't made every stop and gone by 3 pm, or
  skipped more than 1 in 20;
- no call is answered;
- in a town of 2,000 people or more, nobody pulled over or waited for a
  siren;
- a service vehicle is stuck.

| Town | Round | Calls answered | Pulled over | Waited at a junction |
| --- | --- | --- | --- | --- |
| Ember Crossing | 77 stops, 108 houses | 32 | 19 | 1,009 |
| Juniper Falls | 51 stops, 65 houses | 4 | 2 | 60 |
| Lanternport | 44 stops, 64 houses | 10 | 15 | 926 |
| Porchlight | 120 stops, 212 houses | 30 | 18 | 1,430 |

---

## 10. Drawing it: the slots

The game draws a fixed set of **slots**, not every agent:

- **Car slots:** `clamp(ceil(people × 0.05), 12, 900)` plus one a van, with the buses'
  slots after them, then one for each service vehicle (§9). A bus's or a
  service vehicle's slot is drawn as its own model (`TownLife.SlotModel`). A congested town's rush puts 4–6% of its people on the
  road at once.
- **Person slots:** `clamp(ceil(people × 0.06), 16, 1200)`.

A car takes a slot when it comes out onto the road. When it parks in a
spot it **keeps it**, drawn standing there, so its driver gets out and
walks off from it, and later walks up to it and gets in (a driver setting
off for a car that's lost its slot gets one back for it). A parked car's
slot is the first given up: a car coming out onto the road with no slot
free takes the one of the car parked longest. A car parked off the map,
or put somewhere new at once (gridlock, a gate), gives its slot back. A
player can take a parked car too; its spot is freed. A walker takes one when they step outside, and gives it back at
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
  They're pulled, never pushed. A dead end's U-turn is sized for a car, and
  round its tip a lorry's front (a fire engine's, a bin lorry's: a wheelbase
  of 2 tiles or more) comes back nearer than its wheelbase. Pulled as a car
  is, its back wheels would be shoved backwards and the lorry would seem to
  reverse. Instead they stay put and it swings round on them until its front
  draws away again.
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
- **Drawing cars between updates.** Updates come 10 times a second; the
  game draws at 60. Guessing a car ahead from its last update and easing it
  back when the next comes (as the game did) shows every correction as a
  nudge sideways and a flick of its facing, worst round a corner. Instead
  the game draws each car a moment behind (`WorldSync.DELAY`, 0.2 s, two
  ticks), between the updates either side of that moment. Like a lot of
  networked games, it trades a delay you can't see for motion with no
  corrections in it.
  - **Where:** a cubic Hermite curve through each update's place, leaving
    and arriving at each as the car was moving there (from the update
    before it to the one after).
  - **Facing:** blended from each update's to the next, turning at the rate
    it was turning at each. The middle of a car doesn't quite go the way it
    faces round a corner, because its back wheels trail, so facing and
    place are blended separately.
  - **Past the last update,** the car carries straight on at its speed, as
    the server expects, for as long as the server's still talking. When the
    next update comes, the car curves to it from where and how it's
    drawn, so it never jumps.
  - **A slot handed to another car** (one parks, another comes out, in the
    same tick) puts the car straight where it is, rather than drawing it
    across town.
  - Updates arrive unevenly, so each is taken to be from a usual gap after
    the last, drawn a little toward when it actually came.
- **The server sends a turning car every tick.** A car is sent again if
  its facing turns more than 0.02 rad (it was 0.12). Turning cars are a
  small share, so deltas stay about 3% of the whole town.
- People are still eased toward their latest update.

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
| `GET /mp/sim/life?town=<key>` | The town's clock, who's indoors where, who's off the map at each gate, the day's figures (with the population table), and what each car and person record is (`kinds`). |
| `GET /mp/sim/building?town=<key>&i=<n>` | Who's in that building, and when each next goes out. |
| `GET /mp/sim/congestion?town=<key>&ago=<0–23>` | The last 24 hours' hold-ups, oldest first (`from`, `time`, `cars`, `hold` in seconds), and the 12 streets that held cars up most in all in the hour `ago` hours back: each one's `road`, `kind`, `cars`, `hold`, `mean` and `points` (its centreline, the town's tiles). |
| `GET /mp/sim/signals?town=<key>[&stops=1]` | The town's traffic lights now: each signal's stage showing green and when it ends (`ends`, town ms), and a lamp on the kerb by each stop line with the stage it goes on. With `&stops=1`, its stop signs too: x, y, and 1 if all-way. |
| `GET /mp/sim/where?town=<key>&kind=car\|person&i=<n>` | As before, plus whether the slot is empty, and who's in it and what they're doing (the agent's state, trip and legs). |

**Manage** (`manage/src/life.ts`) shows this:

- **On the map,** with land use on, each building has a disc whose area is
  the number of people inside.
- **Hovering** a building says its number (its place in the town's list of
  buildings, the same number its breadcrumb shows) and how many are inside.
- **Clicking** a building lists them at the top of the panel, with when
  each next goes out; click someone for their focus.

**The sidebar** is breadcrumbs (`manage/src/crumbs.ts`), and shows only
what each one is for:

| Crumb | Shows |
| --- | --- |
| **County** | The towns, a line each with its people. |
| **› A town** | Collapsible sections, as the game's own sidebar has them (`Sidebar.cs`: a heading with a › that turns when open). First its congestion (below), then one for each sort of thing out in it now, with how many: Cars (residents', commuters', through traffic), Vans, Buses, People (workers, students, out on errands, commuters on foot, wanderers), Parked, Boats. Sorts with none out are left out. Opening one closes the others, and clicking an open heading closes it. A list shows 40 at a time, each with the simulation's line ("Ada Brekke · Going to work, by car · trip 1 of 3 today"); "Show more" adds 40. |
| **› Car 12 / Ada Brekke** | One car or townsperson (by name, once their focus has answered), followed on the map: what it is, what it's doing and its next four steps. A car says whose it is ("Ada Brekke's car"). Its driver or car (the person/car button) takes its place, so toggling between them stays one crumb under the town or building. |
| **› Building n** | Who's inside (a building clicked on the map, by its number). Each person is a crumb further on. |

**Room for it all.** Inside a town the sidebar itself never scrolls. The
headings stay where they are, the ones before the open section above it and
the ones after below, and only the open section's body scrolls, in whatever
height is left. So a heading is never scrolled out of the window, however
long the list (`.panel-body.fill`, `.sb-section.open` in `styles.css`).

How the crumbs move:

- Clicking a town in the county goes into it, and the map fits to it.
- Clicking someone in a town's list or a building adds a crumb and follows
  them.
- A focus's driver or car button swaps the last crumb for them: back and forth
  between a van and its driver is always County › town › one of them.
- Clicking a car, person or building on the map starts again from the
  county: County › its town › it.
- The **Whole county** button goes back to the first crumb.

**Going back** to a crumb picks up as it was left, and moves the map to
match:

- **The county:** the whole county, with the town last opened highlighted.
- **A town:** the map on the town. It keeps its tab, the hour of congestion
  it was on, how far down its list was shown, and who was clicked from it
  (highlighted and scrolled into view). Each town keeps this (`Memory`), so
  it's the same if you come back to it from the county later.
- **A car or person:** followed again.

Nothing in the sidebar scrolls on its own; only the sidebar does. Long
street names wrap. What the map shows is in the ⓘ, with the key: its
layers (cars, people, lights, stop signs, land use, players, streets,
names, trails, reach) and each kind of car and person, a checkbox each.

**Congestion.** To see where the town's traffic jams, and when, a town's
first section (open the first time in) is its last day of hold-ups (`GET /mp/sim/congestion`, asked
again every 10 s while it's open):

- a bar for each of the last 24 hours (this one so far on the right), as
  tall as that hour's hold-ups in all against the worst hour's;
- a slider under them (or click a bar) picks an hour: its times, how long
  cars were held up in all, and how many left a lane;
- that hour's 12 worst streets, worst first: the street's name and which
  stretch of it ("High Street, Elm Street to Oak Avenue"; "Main road 4"
  only if the sim is older than names), its hold-ups in all, and the cars.
  They're drawn on the map in red, wider the worse, each with its number at
  its middle. Clicking one takes the map to it.

The names are the game's own (`StreetNaming` in `Explore/Names.cs`, which
joins road strokes into named streets). `StreetNaming.Stretch` names a
stretch by the streets at its ends, or "at" the one street it meets.

**Street names on the map.** Every town's streets come with their names
(`roadNames` in `GET /mp/traffic/county`):

- from `names` in `server/towns/*.roads.json` for the starter towns,
  written by `Checks roadmap`;
- from the sim's `READY` for the rest (`SimTown.RoadNames`).

Close in (6 pixels a tile or more, with the roads and names layers on),
Manage writes each street's name once, along its longest straight stretch
and never upside down (`streets.ts`, `streetLabels`). Main roads come first,
and a name that would overlap one already written is left out.

**Right-click: directions, or take me there.** Right-click a spot on the map
for a menu (`SpotMenu.tsx`):

- It names the spot by its town and the nearest street within 6 tiles
  ("Lanternport, near Elm Street").
- For each of your characters playing now (`GET /mp/goto`), it offers
  **Directions here for …** and **Take … here**.
- Outside every town it says so. With nobody playing, it says that.

Picking one posts `{character, town, x, y, how, label}` to `/mp/goto`.
The server checks the request:

- The character must be yours (404 if not) and allowed in the town (403
  if not).
- The spot must be 0–1024 tiles (400 if not), and `how` must be
  `directions` or `transport` (400 if not).
- The character must be playing (409 if not).

The server then sends `goto` to the game playing them (`CityRoom.tell`). The
game (`PorchlightApp.GotoFromManage`) does one of two things:

- **directions:** draws the way there as it does for any destination,
  across the county if need be, with the label as where to;
- **transport:** puts you beside the spot. In another town, it travels
  there first, then puts you beside it.

A hold-up is a car's time over a lane less its time at the limit, as
routing measures it (§4, Routing by the traffic), but with the whole wait
at a light counted: it's what the driver sat through. The town's life
keeps it an hour at a time for 24 hours (`TownLife.HoldsByHour`,
`MostHeldUp`): each lane's cars and their hold-ups, in a ring of 24 slots
by the hour since the epoch, a slot cleared when its hour comes round
again. A road's figure is its two lanes' together. Only cars that have
left a lane are counted, so a queue nobody's got out of yet shows when
they do. The log starts with the town's life: hours before it are empty.

**The kinds.** Every car and person record has a kind (`SimTown.Kinds`,
sent with the town's life as two strings, a letter a record, `-` for an
empty one):

| Vehicles | | People on foot | |
| --- | --- | --- | --- |
| `r` | residents' cars | `w` | workers |
| `c` | commuters' cars (driven in from out of town) | `s` | students |
| `t` | through traffic (gate to gate) | `h` | at home in the day, out on errands |
| `v` | vans on their rounds (deliveries, trades, the post, taxis) | `c` | commuters walking to and from their cars |
| `b` | buses | `x` | wanderers (strolling, with no day of their own) |
| `s` | boats | | |
| `p` | parked cars | | |

A car's kind comes from what's in its slot: parked, a bus, a van, or a
person's car by its owner's kind. People's records start with the wanderers, then
the life's person slots, then drivers put out of cars a player took
(counted at home in the day). Hidden kinds aren't drawn, hovered, picked
or left as trails; they're remembered in the browser, like the layers.

- **Traffic lights,** close in (2 pixels a tile or more), are a small lamp
  in a dark housing on the kerb by each stop line: green, amber in a
  stage's last 3 s, red. Manage asks for the lights of the towns in view
  once a second, and in between works out each lamp from the stage and
  when it ends (`lampColor`), as the town does. The lamps are where the
  game's signal posts stand (`StreetFurniture`), and the game lights its
  models the same way (§2, Signals and signs you can see).
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

**The panel shows only what it is and what it'll do:** a header line (Car,
Van, Bus or Person, its number and town, who it is in the tooltip, and
buttons to follow it and to go to its driver or car), a line for what it's
doing now, and the next four things in its queue. The tree, its day and
the rest below are still worked out and served (the life check checks
them), but Manage no longer shows them.

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
crossings, from as far along the one they're on as they've walked (not
from its start, back where they came from). A ring marks the focus, dashed when it isn't out (indoors, or
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
- **Smooth driving.** Given five minutes of the morning rush drawn 10
  times a second, then cars brake into turns and pull away out of them: under
  1% of moments brake or speed up harder than 1 tile/s² (0.5% on turns),
  under 0.2% harder than 3 tiles/s², no car jumps more than 0.6 tiles
  between frames, and almost none stall on a turn. With cars at one speed
  per segment (as before), about 4% are harsh (7% on turns) and cars jump.
  All four towns pass. The 1 tile/s² limit is timed on the driver's speed
  along the road; the 3 tiles/s² limit, the jumps and the stalls are timed
  on the car as drawn. The drawing pulls the back wheels round after the
  front (§10), so on a tight turn of a tile or so the drawn car's middle
  slows by half and speeds up again while the driver keeps a steady pace.
  That's geometry, not braking. Timing the drawn middle failed Lanternport
  (1.2% on turns) once households put more cars on its short turns; on the
  driver's speed it's 0.00%, and Porchlight's 0.22%.
- **No car starts a turn on red**, except a siren or a driver clearing the
  way for one (`Car.wentOnRed`). Run one scenario alone with
  `Checks life server/towns "drive smoothly"` (a scenario's name has the
  last argument in).
- **A corner on the wire.** Given a car going round a curve (1.2 tiles
  radius, 1.3 tiles/s) sent 10 times a second and drawn at 60, then it's
  drawn within 0.02 tiles of the curve, facing within 2° of it, and its
  turn rate never steps more than 0.1 rad/s from one frame to the next.
  Now it's 0.006 tiles, 0.3° and no step. Guessed ahead and eased back,
  it was 15° off.
- **Turn names.** Given every turn in a town, right turns are on average
  shorter than left (traffic keeps right), and there are some of each.
- **A player in a junction.** Given a player in the middle of the busiest
  junction through the rush, then no car on a turn there reaches them,
  cars do wait, and the waiting cars go once the player leaves.
- **Parked cars stay.** Given the morning rush, when a car parks in a spot,
  then it's drawn standing in it for as long as it keeps its slot, and no
  car vanishes as it parks; when a driver walks out to their parked car,
  it's there (every moment, in all four towns).
- **Congestion by the hour.** Given the rush run on half an hour, when
  Manage asks for the last day's congestion, then there are 24 hours an
  hour apart, the last this one; this hour's streets come worst first and
  add up to the hour's hold-ups; and the hours before the town started are
  empty.
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

- a tick costs about 0.3 ms a town (the budget is 1 ms);
- deltas are about 3% of the whole;
- a follower's drawing stays within 0.3 tiles of the server's 0.2 s before
  (it's drawn that far behind), and every car or person is hidden on both
  sides or shown on both;
- **the drawn cars move smoothly**, frame by frame at 60 fps, through each
  town's morning rush, as a player's game draws them from the real deltas:

  | | Limit | Now | Guessed ahead and eased back (before) |
  |---|---|---|---|
  | Turning frames where the turn rate jumps (over 20 rad/s²) | 1% | 0–0.14% | 10–26% |
  | Frames where speed surges (over 4 tiles/s²) | 3% | 0.7–1.7% | 2–5% |
  | Frames at speed where the back axle slides (over 10°, and sideways at 0.2 tiles/s or more) | 2% | 0% | up to 40% |
  | Turn-rate step from one frame to the next, 99.9th percentile | 0.6 rad/s | 0.22–0.34 | 1.7–3.1 |

  Boats aren't counted, nor are cars parking or pulling out: the game places
  them between the lane and the spot (`Traffic`), and the follower's easing of
  that step into a car-park bay read as a car swinging in at 40 mph (Juniper
  Falls' flicks at 1.3% on a Friday, and all of the sliding that was left).
  Sliding is counted from 1.2 tiles/s, measured at
  each vehicle's own back axle (half its wheelbase behind its middle, as
  it's drawn). A lorry swinging round on its back wheels at a dead end
  barely moves them, so it isn't sliding. Lanternport's bin lorry was, from
  PR #22 until this was fixed: drawn reversing round a cul-de-sac's tip,
  at 2.3% of frames.

**`Checks clock`** checks the county clock:

- winter and summer offsets;
- AM, PM, midnight and noon;
- 2026's changeovers, both ways;
- a round trip through `At` for moments every 7 h 13 min through a year.

**`Checks furniture server/towns`** checks each starter town's signals and
signs:

- a signal post at every lane into a signal, and a stop sign at every lane
  that stops;
- every post on the drivers' right, square to its lane, and its pole clear
  of the lane;
- over a whole cycle, exactly one stage at a time shows green or amber, the
  one the cars go on: 27 s green, 3 s amber and 30 s red.

**`manage/test-browser/sidebar.mjs`** (`npm run test:browser`) clicks
through the real sidebar in Chromium against a stand-in server:

- the towns with their people and nothing else;
- into a town: a heading for its congestion (open) then each sort with its
  count; its most congested street by name, the cross streets under it;
- Cars opened: the congestion closes; with 203 cars the list scrolls on its
  own while the sidebar and the page don't, and every heading stays in the
  window, scrolled to the end too; clicked again, it closes;
- a building hovered: its number;
- picking an hour, Vans, a van (a crumb on, followed, its driver named), then
  its driver and back (by name, in its place: never a crumb more);
- back along the crumbs: the town as it was left (Vans open, the van
  highlighted, the hour), then the county with the town highlighted;
- into another town: its own state, its congestion open.

`manage/test/crumbs.test.ts` covers where each click takes the crumbs,
going back, and each town's memory.

**`manage/test-browser/menu.mjs`** (`npm run test:browser`) right-clicks the
real page in Chromium against a stand-in server. It checks:

- the spot's name;
- **Take … here** and **Directions here for …**, and what each posts;
- outside a town;
- nobody playing.

`server/test/room.test.ts` covers `/mp/goto`'s answers and that the game
gets `goto`.

**`server/test/sim.test.ts`** runs the real worker:

- the town's life has a clock of now, said on a 12-hour clock;
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

The townsfolk's first and last names are the English name lists from
[faker-js](https://github.com/faker-js/faker) (`@faker-js/faker` 9.9.0, MIT,
Copyright (c) 2022–present Faker contributors), copied in as data by
`unity/Tools~/names/generate.mjs`. Which name each person gets is our own
(`PersonNames`).

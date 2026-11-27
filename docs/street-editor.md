# Turn lanes, roundabouts and a street editor: a plan

This is a plan, not a record. Nothing in it is built yet.

It has two halves that share one foundation:

- **A. Junctions designed for the traffic.** When a town is made, a new
  stage looks at the traffic each road and junction will carry, and gives
  it what a traffic engineer would: turn lanes, roundabouts, slip roads,
  wider roads, divided roads, and relief routes that take traffic off a
  road that can't cope.
- **B. A street editor in Manage.** A screen for one town where you drag
  the anchor points of its streets. The town re-plans itself round the
  drag as you go: streets, blocks, lots, houses, and the junction design
  from part A. All of it is a draft. The live town carries on behind it.
  **Commit** takes the players out of the town, rebuilds it, and brings
  them back.
- **The foundation:** a town that is its key **plus** a saved street
  skeleton, generated so that a change in one place leaves the rest of
  the town exactly as it was.

It follows [abstreet.md](abstreet.md) and [town-life.md](town-life.md),
and it sits beside the mayor proposal in
[PR #25](https://github.com/joe307bad/porchlight/pull/25)
(`docs/mayor.md`), which needs the same foundation for road rules.
A/B Street's ideas are borrowed, not its code: see [Credits](#credits).

---

## Contents

1. [The short version](#1-the-short-version)
2. [What A/B Street does](#2-what-ab-street-does)
3. [What Porchlight has today](#3-what-porchlight-has-today)
4. [The foundation: a skeleton on top of the key](#4-the-foundation-a-skeleton-on-top-of-the-key)
5. [Part A: junctions designed for the traffic](#5-part-a-junctions-designed-for-the-traffic)
6. [Part B: the street editor](#6-part-b-the-street-editor)
7. [Commit: out, rebuilt, back in](#7-commit-out-rebuilt-back-in)
8. [Budgets](#8-budgets)
9. [Checks](#9-checks)
10. [The order to build it in](#10-the-order-to-build-it-in)
11. [Open questions](#11-open-questions)
12. [What's built](#12-whats-built)
13. [Picking blocks and regenerating them](#13-picking-blocks-and-regenerating-them) (replaces dragging roads)
14. [Credits](#credits) and [References](#references)

---

## 1. The short version

**Generation (part A):**

1. After the buildings are placed, estimate the morning peak on every
   road and turn. The households, jobs and gravity model already exist
   (`Life/Population.cs`). Load them onto the road graph with a few BPR
   rounds. That gives *design flows*.
2. From the design flows, a new **junction design stage** decides, by
   published warrants:
   - **left- and right-turn pockets** at junctions, with storage sized to
     the queue;
   - **protected left-turn stages** at signals with a left pocket;
   - **roundabouts** in place of all-way stops, five-leg junctions and
     knots the clean-up can't untangle;
   - **slip roads** (channelised right turns) where a heavy right turn
     meets a shallow angle;
   - **two lanes each way**, and a **divided road** (split in two, with a
     median) where one lane can't carry the flow;
   - a **relief route**: an existing collector upgraded so traffic
     branches off the overloaded road.
3. Turn pockets take the parking lane's space for their last stretch, as
   real streets do. So most of it changes nothing outside the kerb, and
   the lots and houses stay put.
4. The simulation gains **lanes per direction**. A car picks its lane
   where the road fans out, by the turn it will make next, and does not
   change lanes mid-block. This is how A/B Street started.

**The editor (part B):**

1. **Manage → a town → Edit streets** opens a full screen for that town:
   its roads, blocks, lots and houses, with handles on every junction and
   bend.
2. Drag a handle. The roads it touches rubber-band at 60 fps in the
   browser. When you pause, a **draft worker** on the server re-plans just
   the land the drag affected and sends back the new streets, lots,
   houses, lanes and junctions, and any rule broken.
3. Every change goes in a **draft**, saved as you go, with undo. Nobody
   playing sees it. Run a day of traffic on it and compare with the live
   town.
4. **Commit** checks the draft, saves it as the town's next revision,
   warns the players inside, then takes them out, rebuilds the town from
   the new revision, rehouses anyone whose house went, and lets them
   back in.

**The foundation:**

1. A town is its key plus a **street skeleton**: the roads someone has
   pinned, and any overrides. The key carries `&rev=N`, so a new revision
   is a new room, a new cache entry, and a new town everywhere at once.
2. **Generation becomes local.** Each piece of land between the big roads
   gets its own random stream, keyed by its shape, not by the order it was
   reached in. Each block does the same. Change one corner of the town and
   every block elsewhere is identical, down to the house a player lives in.

---

## 2. What A/B Street does

A/B Street is the traffic game [abstreet.md](abstreet.md) studied. Its
ideas below come from its documentation, its blog and how its tools
behave. No code was read for this plan.

| A/B Street | What it does | What we take |
| --- | --- | --- |
| **Lanes with turn restrictions** | Every road is a list of lanes, left to right, each with a type (driving, parking, bus, bike, sidewalk). Turns are made lane to lane. Which lanes may turn which way comes from OSM's `turn:lanes`, or a default: left lanes turn left, right lanes turn right. | Lanes per direction, each with the turns it allows. Our own default rules, since we have no OSM. |
| **Lane choice, not lane changing** | Its retrospective says lane changing is hard in a discrete-event sim. Cars choose their lane when they enter a road, picking among those allowed for their next turn, by the shortest queue. Later it added an opportunistic change in limited cases. | Choose at the start of a road (or where it fans out to a pocket). No mid-block change. |
| **Roundabouts** | Imported from OSM as a ring of short one-way roads. Each entry is an ordinary junction where the entry yields. Gridlock on the ring is broken by the same cycle check as everywhere else. | The same model: a ring of one-way lanes, yield on entry, our existing gap acceptance and `blockedBy` cycle break. |
| **Map edits** (`MapEdits`) | A list of commands: change a road's lanes, change a junction's control, close a road. Undoable. Applied in place. Only the touched roads and junctions are rebuilt. Applying them resets the running simulation. | A command list for undo and history, but the saved form is a snapshot (see §4.3). Rebuild only what an edit touches. A commit restarts the town. |
| **Edit mode** | A separate mode. The simulation is paused behind it. Leaving with changes asks to apply them. | Our edit screen is the same idea, except the live town keeps running for its players while you edit. |
| **The map editor** (`map_editor`, the old RawMap editor) | Edits the raw map before it is turned into lanes and junctions: drag an intersection, drag a road's points, add or delete a road, then rebuild the map from it. | The core of part B: drag the skeleton, regenerate everything downstream. |
| **osm2streets** | Turns raw roads into junction shapes: trims roads back where they meet, collapses short roads into one junction, merges dual carriageways into one road. | Already borrowed for "no knots" (city-research §1). Now also: a divided road is one edge with a median, not two edges. Trim and shape roundabouts and pockets the same way. |
| **Signal editor** | Edit a signal's stages and timing, with a preview. | Protected-left stages, and in the editor a junction's control can be overridden. |
| **LTN tool** | Finds rat runs through neighbourhoods. Filters (bollards) stop them. | Check that an editor change doesn't route arterial traffic through a neighbourhood (a relief route must be a collector at the edge, not a local street). |

**What we leave:** OSM import, lane-level drawing of every lane type
(bike lanes, bus lanes), uber-turns (unless `traffic-day` finds signals
too close together), and grade separation (overpasses) for now.

---

## 3. What Porchlight has today

The facts this plan rests on.

**The road model** (`Model/Types.cs`)
- `RoadGraph` is nodes (`RoadNode`: an id and a point) and edges
  (`RoadEdge`: from, to, a `RoadKind` of Main, Street or Path, a
  `RoadClass` of Arterial, Collector or Local, and a polyline).
- Widths come from the key: `mainRoadWidth` 2 tiles, `streetWidth` 1
  tile (`Model/Params.cs`, `Explore/Layout.cs` `RoadWidth`). Each road has
  a parking lane and a sidewalk each side.

**Generation** (`Gen/Generator.cs`)
- Fourteen stages, each a pure function of the model and its own random
  stream: Terrain, Rivers, Site, Airport, Zoo, **Roads**, **Streets**,
  Blocks, Plan, Lots, Land, Buildings, Rasterize, Docks.
- Roads routes the main roads (A\* from each gate to the centre, a ring
  in big towns) and cleans up knots (`RoadGraphOps`).
- Streets plats the land between the main roads by splitting it, region
  by region (`StreetPlan.cs`). Each `Region` gets an id in the order it is
  reached, so one change upstream renumbers everything after it.
- **A full generation of Porchlight takes 2.6 s** (measured for this
  plan: `Checks measure porchlight …`, Release build, checks on). 2,171
  buildings, 1,789 houses, 3,133 lots, 502 roads.

**The road network as driven** (`Life/RoadNet.cs`)
- **One lane each way.** A `Lane` is (road, dir). A `Movement` is one lane
  into another through a junction, with a Bézier curve, a speed and a turn
  (straight, right, left, U).
- Controls: Free, Stop, All-way stop, Signal, chosen by the legs' ranks
  (town-life §2). Signals are two-stage, retimed by Webster every quarter
  hour (`Life/Signals.cs`). Gap acceptance by HCM critical gaps.
- Cars are queues on lanes, event-driven (`Life/Cars.cs`), with don't
  block the box and the `blockedBy` cycle break.

**A town is its key**
- Everything is built from the key (`seed=…&width=…`): the game, the
  server's sim worker (`sim/`), Manage's map (roads sent by the worker)
  and the checks. The room is named by the key (`CityRoom`).
- The key's settings are numbers only:
  `^seed=…(&[A-Za-z0-9]+=[0-9.eE+-]+)*$` (`CityRoom.ts`).
- **Regeneration already exists for starter towns:**
  `settleStarterTown` (`server/src/multiplayer/persistence.ts`) spots a
  new layout by its hash, changes the town's key, and rehouses each
  household in a random house. Homes are stored by tile, not by building
  id, for exactly this reason (`tables.ts`).
- The game makes a town's street life ahead on a worker thread
  (`unity/README.md`), and keeps a town for coming back. Both are keyed,
  so a new key invalidates them for free.

**Manage**
- A county map on a canvas (`manage/src/render.ts`): pan by dragging,
  zoom with the wheel or a pinch, layers, street names. The panel beside
  it (`Panel.tsx`) has towns, players and one town's life.
- Anyone signed in sees their own county. There is no editor role yet.

---

## 4. The foundation: a skeleton on top of the key

Both halves need a town that can change after it's made. Part A's design
stage could run from the key alone, but the editor's overrides need a
place to live, and part A's own output must not reshuffle the town when
an editor nudges one road.

### 4.1 Local generation

Today, moving one main road a tile renumbers every region and block after
it, and every later random draw comes out different. The whole town
would be rebuilt, every house moved, everyone rehoused. So first:

- **Each street region gets a stable seed:** `Rng.Stage(seed,
  "streets:" + signature)`, where the signature is a hash of the
  region's boundary, rounded to whole tiles and started from its
  lowest-leftmost corner. Same land, same streets.
- **Each block the same:** lots and buildings drawn from
  `Rng.Stage(seed, "block:" + signature)`, the block's polygon rounded.
- **Ids stay generation-order** (nothing else needs to change), but
  nothing that persists uses them. Homes are already by tile.
- **Town-wide things** that depend on everything (land value, the high
  street, neighbourhoods, the park block, schools) are re-run whole. They
  can shift a little near an edit. They do not move houses unless a house
  becomes a shop (see §7.4, rehousing).
- **The check:** `Checks locality` moves one main-road node a few tiles
  in each starter town and counts blocks whose buildings changed. Blocks
  more than one region away from the edit must be identical.

### 4.2 The skeleton

A **skeleton** is what an editor has decided. Everything else is still
generated.

```jsonc
{
  "rev": 3,
  "base": "seed=porchlight&width=256&…",  // the key it edits, rev stripped
  "roads": [                                 // pinned roads: kept exactly
    { "id": "r1", "pts": [[120,88],[131,88],[140,97]],
      "cls": "arterial", "lanes": null, "divided": false, "oneWay": false }
  ],
  "removed": [[[60,40],[60,52]]],            // generated roads not to lay (by their end points)
  "junctions": [                             // overrides at a point
    { "at": [131,88], "control": "roundabout" }
  ],
  "locks": [[[200,150],[200,170]]]           // generated roads part A may not change
}
```

- **Pinned roads** replace whatever Roads and Streets would have laid
  there. A pinned road's points are exact.
- **Removed** roads are generated ones the editor deleted. They are
  matched by their end points to within a tile.
- **Overrides** win over part A's design: a junction's control or form,
  a road's lanes, divided or not, one-way.
- **Locks** stop part A changing a road at all.

### 4.3 Why a snapshot and not a replay

A/B Street saves edits as commands against stable ids. Ours aren't
stable: a change to the generator renumbers them, and we change the
generator often. So the saved form is the **skeleton snapshot**, matched
by position. The editor keeps a command list too, but only for undo and
the history panel.

If a generator change makes a skeleton fail (a pinned road now crosses a
new river), the town still builds: the failing pins are dropped, logged,
and listed in Manage for an editor to fix. A town never fails to load
because of its edits.

### 4.4 The key and where the skeleton lives

- **The key gets `&rev=N`.** It is a number, so it passes today's key
  regex unchanged. `rev=0` (or none) means no skeleton: every town today
  is unchanged.
- **New table `town_revisions`:** `town_id`, `rev`, `skeleton jsonb`,
  `committed_by`, `committed_at`, `hash`. Immutable.
- **New table `town_drafts`:** `town_id`, `author`, `base_rev`,
  `skeleton jsonb`, `commands jsonb` (undo), `updated_at`. One per author
  per town.
- `towns.key` is updated to the new rev's key on commit, as
  `settleStarterTown` does.
- **The game** fetches `GET /mp/towns/skeleton?key=…` before generating a
  town whose key has `rev>0`. It is immutable per rev, so it is cached
  forever (and kept beside the town's other caches on disk).
- **The sim worker** gets it in the TOWNS frame:
  `[{ id, key, skeleton }]`.
- **Starter towns** keep their file in `server/towns/` for rev 0. The
  file's `houses` list is recomputed for each rev, and the layout hash
  takes in the rev.
- **`Generator.Generate(seed, params, skeleton)`**: the skeleton is
  applied inside Roads (pinned main roads routed round, removed ones not
  laid) and Streets (pinned streets kept, regions planned round them),
  and its overrides read by part A's stage. Everything is still pure.

---

## 5. Part A: junctions designed for the traffic

### 5.1 The lane model

Each `RoadEdge` gains a **cross-section**, built by the design stage
unless an editor overrode it:

```text
RoadEdge
  lanesFwd, lanesBack : 1–3     through lanes each way (0 one way: one-way road)
  median              : none | painted | raised(width)
  pockets             : per end, per side: { turn: left|right, length }
  slip                : per junction corner: a separate right-turn road
```

- **Width.** A through lane is 0.75 tile (3 m). Today's main road, 2
  tiles, is two lanes plus some. Two lanes each way is 3 tiles; divided
  with a 1-tile median, 4. The parking lane and sidewalk are added
  outside, as now.
- **Pockets eat the parking.** A left pocket shifts the through lanes
  right into the parking lane over the pocket's length plus its taper. A
  right pocket takes the parking lane directly. No kerb moves, so no lot
  changes. Kerb spots along the pocket go (`ParkingSpot`s are dropped).
- **Bigger roads need a reserve.** A road that might widen needs its
  land kept. Main roads already sit a block's depth from any street
  alongside (`StreetsStage.MIN_APART`). The Lots stage gets a
  **right-of-way**: lots stay back from an arterial by the width it could
  grow to (3 tiles each side of the centre line for an arterial, 2 for a
  collector). Most of that is today's front yards and setbacks anyway
  (measure how many lots it shrinks).

`RoadNet` turns this into lanes:

- a `Lane` gains `index` (0 is the kerb lane) and `allows`: the set of
  `Turn`s its movements may make;
- **where a pocket starts, the road is cut in two** inside `RoadNet` (not
  in the town's graph): a short *fan* where one lane becomes two. The cut
  is a free two-leg junction. A car picks its lane at the fan, by its next
  turn and the shortest queue;
- **a full pocket blocks the through lane behind it**, for free: a car at
  the fan waiting for room in the pocket holds up the queue behind it.
  That is real spillback, and it is what the storage length is sized to
  avoid;
- movements are made lane to lane. Through lanes go straight to the
  matching lane beyond. A left pocket's movements are left and U-turns.
  Default rules, as A/B Street's: the leftmost lane turns left, the
  rightmost turns right, any lane goes straight unless it is a pocket;
- conflicts are worked out as now (curves crossing, same lane out).
  Two lanes side by side turning the same way don't conflict.

### 5.2 Design flows

We have no traffic counts when a town is made, only its people. So:

1. After Buildings, make the households and jobs exactly as
   `Life/Population.cs` does at startup (it is pure and seeded).
2. Take the **morning peak hour**: the share of commute trips that start
   7:30–8:30 (from `People.cs`'s day plans, measure the share; NHTS puts
   it near 10–11% of the day's trips).
3. Route them on free-flow times, then **four rounds of BPR** (the
   method of successive averages), each time re-costing every link with
   t = t₀ [1 + 0.15 (v/c)⁴]. That is close enough to Wardrop for a
   design. (`Life/Congestion.cs` already has BPR; this is a static use of
   it.)
4. Result: an hourly flow on every lane and every movement. Do the same
   for the evening peak and take the larger.

This is the same model the live town uses, so what the generator designs
for is what the town will see. After a commit, the design stage can run
again with the **measured** flows of the last weekday
(`Life/Analytics.cs` counts movements), which is better still.

### 5.3 The rules

Each rule is a published warrant or rule of thumb, with a constant we can
tune against `Checks traffic-day`. Numbers marked *(tune)* are starting
points, not gospel.

**Left-turn pockets**
- At a **signal**: where an approach's left turns are ≥ 100 an hour in
  the peak *(tune)*, or the left-turn × opposing-through product is ≥
  50,000 (the cross-product rule of thumb for protected left phasing).
- At a **stop-controlled** junction, on the major road: by the Harmelink
  curves (NCHRP 457's simplification): roughly, where the left turn is
  over ~10% of the approach and the opposing flow is over ~400 an hour
  *(tune)*.
- **Storage:** twice the average queue a cycle at a signal, so a
  pocket rarely overflows: L = 2 × (left flow × cycle / 3600) × 1.6
  tiles (a car plus its gap). At a stop: the left turns in two minutes.
  At least 2 cars (3.2 tiles), at most the road's length to its last
  junction less 4 tiles (`JUNCTIONS_APART`).
- **Taper:** 1:8, as a lane width over 6 tiles at town speeds *(tune)*.

**Protected left stages**
- A signal approach with a left pocket and a cross-product ≥ 50,000 gets
  a **leading left** stage: three or four stages instead of two. Webster
  (`Life/Signals.cs`) is already written over stages, so it times them.
  Lost time grows by 4 s a stage.

**Right-turn pockets**
- Where right turns are ≥ 300 an hour on an arterial approach
  *(tune)*, or where the right turn is over a third of the approach at a
  signal. Storage as for left.

**Slip roads (channelised right turns)**
- At arterial × arterial, where the right turn is ≥ 300 an hour **and**
  the turn bends less than 75° (a wide corner, taken fast anyway).
- The slip is a short one-way road of its own, from one leg to the next,
  yielding where it joins (gap acceptance). The corner lot loses a
  triangle; it is drawn as a raised island with a crossing.
- A slip takes right-turners out of the signal entirely: that is the
  "turn that branches" off the junction.

**Roundabouts**
- **Where:**
  - a junction that would be an **all-way stop** with at least 900
    vehicles an hour entering (below that, the stop is cheaper and
    nobody notices), *(tune)*;
  - **five legs or more**, or two legs meeting under 60°: shapes that
    signals and stops handle badly and the clean-up today refuses
    (city-research §1, "Forks");
  - **knots the clean-up can't merge** (check-64, check-81 today): a
    roundabout swallows them, if its circle fits;
  - never at a junction with an arterial whose busiest approach is over
    the roundabout's capacity (below).
- **Capacity** (HCM 6th edition, single-lane entry):
  c = 1380 e^(−0.00102 v_c) cars an hour, v_c the flow circulating past
  the entry. An entry whose design flow is over 85% of that gets a
  signal instead.
- **Size** (FHWA *Roundabouts: An Informational Guide*): a mini
  roundabout 13–27 m across (3.5–7 tiles), with a mountable island; a
  single-lane one 27–55 m (7–14 tiles). We use 7 tiles in the core and
  9–10 at the edge, where land is cheaper.
- **Fitting it in:** the circle plus a sidewalk is cut from the corner
  lots. The corners' blocks are re-lotted (they are near an arterial
  junction, so usually shops). If the circle would cut a pinned feature
  (the high street's anchor shop, a landmark), it shrinks to a mini or
  isn't built.
- **In the sim:** a ring of one-way lanes, cut into segments between the
  entries, at 0.6 of the slower leg's speed. Each entry yields to the
  ring by gap acceptance, with a critical gap of about 5 s and a
  follow-up of about 3 s (HCM's single-lane values; measure ours). Exits
  are free. The ring is lanes like any other, so don't-block-the-box and
  the `blockedBy` cycle break already cover the classic roundabout
  lock-up (every entry full, every car on the ring waiting to exit into a
  full lane).
- **Drawn as:** an island (grass, a tree or a lamp in the middle), a
  splitter island on each leg, yield lines, and the crossings set back a
  car's length from the ring.

**Two lanes each way**
- Where an arterial link's design flow is over 0.85 × one lane's
  capacity. A lane's capacity on an arterial is set by its junctions:
  1,900 an hour of green × the green share at its signals (Webster's
  numbers). Mid-block, it is about 1,000 an hour a lane at town speeds
  *(tune)*.
- Lanes are added for the **whole link between two junctions** (no
  lane drops mid-block), and runs of links are smoothed so a road doesn't
  go 2–1–2 lanes along its length.

**Divided roads (split in two)**
- Two lanes each way **and** the road crosses no local streets (only
  collectors and arterials), or the editor asked for it. A raised
  median: left turns into and out of driveways stop, and side streets
  without a median break become right-in, right-out.
- A median break is left at every collector and arterial junction (with
  its left pocket in the median). This is the dual carriageway that
  osm2streets merges into one road; we keep it one edge with a median, so
  the graph and the routing stay simple.
- Where the road crosses the ring road or meets a gate, it stays divided
  to the gate: the highway's lanes come in that way (docs/highways.md).

**Relief routes (traffic that branches)**
- After lanes are added, any arterial link still over 1.0 v/c in the
  design flow looks for a **parallel collector**: one whose detour from
  the link's start to its end is under 1.4× the link's length, and which
  doesn't pass a school or run through the middle of a neighbourhood
  (the LTN rat-run test, A/B Street's `ltn` idea).
- The best one is **upgraded**: its class goes to arterial (its
  junction controls and speed follow), and a pocket or roundabout is
  added where it leaves and rejoins the main road.
- Re-run the BPR rounds once. Keep the upgrade only if the worst v/c
  falls. (It can rise: Braess's paradox. The check is the point.)

**Order inside the stage**
1. Design flows.
2. Relief routes (they change the flows).
3. Through lanes and divided roads, link by link.
4. Junction forms: roundabouts first, then signals and stops as today
   (town-life §2), then protected-left stages.
5. Pockets and slips, sized from the flows.
6. Overrides from the skeleton win at each step; locked roads are
   skipped.

The stage is `JunctionsStage`, after Buildings and before Rasterize, so
the tiles get the new lane markings and islands. Its own `Check`s (§9)
fail a town that breaks a rule.

### 5.4 Drawing it

- **Lane markings:** dashed white between lanes the same way, double
  yellow between the two ways, solid white at a pocket, turn arrows
  painted in each pocket and each fanned lane (Rasterize and the road
  meshes).
- **Islands:** medians, roundabout centres and splitters as raised kerbs
  with grass; slip roads' triangles the same.
- **Signals:** a head over each lane, on the existing mast arm (it
  already stretches to the lane's middle). A left-arrow head over a
  protected left pocket. `StreetFurniture` gives the posts, so game and
  Manage still agree.
- **Signs:** a yield sign at each roundabout entry and slip-road merge;
  a keep-right bollard on each splitter.
- **Manage** draws lanes, pockets, medians and roundabout rings when
  zoomed in, and the design flows as a heat layer.

---

## 6. Part B: the street editor

> **Changed after the first build:** dragging roads by their handles, the road and junction side panel, and
> the Problems list are gone. The editor now picks blocks and regenerates them, and the bullseye moves the
> downtown rather than the town. See [§13](#13-picking-blocks-and-regenerating-them). The rest of this part
> is the original plan.

### 6.1 Who may edit

- **Your own towns:** you, the town's owner (`towns.createdBy`).
- **The starter towns** (Porchlight and the three public cities):
  accounts listed in a new server setting, `EDITORS` (account ids). The
  mayor proposal (PR #25) asks who should run shared towns; until that's
  answered, it is the people running the server.
- Everyone else gets a read-only view (no Edit streets button).

### 6.2 The screen

`/manage/edit/<town key>`, full screen. One town only.

```text
+------------------------------------------------------------------------+
| < Porchlight  Editing  DRAFT 3->4  Undo Redo  [Run a day]  [Commit...] |
+--------------------------------------------------+---------------------+
|                                                  | Selected            |
|   @-------------@=============@                  |  Elm Street         |
|   |  [] [] []   ||  [] []     |   @ junction     |  Class   collector v|
|   |  [] [] []   ||  [] []     |   o bend         |  Lanes   auto v     |
|   @------o------(X)-----------@   . add a bend   |  [ ] Divided        |
|   |             ||  <- dragging   (X) selected   |  [ ] One way        |
|   |  [] [] []   ||                               |  [ ] Lock           |
|   @-------------@=============@                  |                     |
|                                                  | Problems (2)        |
| == arterial -- collector > pocket O roundabout   |  ! junctions 3 apart|
|                                                  |  ! 14 houses go     |
|                                                  |                     |
|                                                  | Changes             |
|                                                  |  moved Elm & 3rd    |
|                                                  |  + roundabout       |
|                                                  |  12 houses->4 shops |
+--------------------------------------------------+---------------------+
```

- **The map** is Manage's renderer (`render.ts`), drawing the **draft**
  town, not the live one: roads with their lanes, blocks, lots, building
  footprints, and the junction forms. The live town's traffic is not
  drawn here (it would be on the old streets).
- **Handles** appear when zoomed in far enough to grab them (a junction
  at least 12 px across):
  - **junctions** (@): every node of 3+ legs, and dead ends;
  - **bends** (o): every point of a road's polyline;
  - **add a bend** (.): a ghost handle half way along each straight
    piece; drag it to make a new bend.
- **Tools** (keyboard in brackets):
  - **Select and drag** (V), the default;
  - **Draw a road** (R): from a junction or any point on a road, click
    bends, double-click to end. It joins whatever it ends on;
  - **Delete** (Del) a road or a bend; deleting a junction joins its two
    roads if it had two;
  - **Split** (S): click a road to put a junction there;
  - **Merge** (M): drag one junction onto another;
  - **Undo / redo** (Ctrl Z / Ctrl Shift Z), back through every step since
    the draft began.
- **The side panel** edits what's selected:
  - a road: its class (local, collector, arterial), lanes (auto, 1, 2, 3
    each way), divided, one way, lock;
  - a junction: its control (auto, free, stop, all-way, signal,
    roundabout), and its form if part A gave it pockets or slips;
  - **Problems:** every rule the draft breaks (§6.5), each a link that
    zooms to it;
  - **Changes:** what the draft does against the live town: roads moved,
    added, removed, houses gone and new, shops moved, **residents who'll
    need a new house** (by count, and by name for players).
- **Layers** (as Manage's): terrain, lots, buildings, design flows
  (heat), the live town's roads as a faint ghost under the draft.

### 6.3 Dragging, and what moves with it

Dragging has to feel instant, but a full re-plan takes too long to run on
every mouse move. So two levels:

**In the browser, every frame (rubber band)**
- **A junction moves;** its roads' ends move with it. Each road bends
  smoothly over its last stretch: points within a falloff radius (6
  tiles, *(tune)*) move by a share of the drag that eases from 1 at the
  junction to 0 at the radius (a soft selection, as 3D modellers have).
  Beyond the radius the road stays put.
- **A bend moves;** its road re-bends through it, the same falloff
  either side.
- **Snapping**, shown as you drag:
  - to 0°/90° against the road it leaves, and to straight-on through a
    junction, within 8° (`StreetsStage.GRID_TOLERANCE`);
  - onto a junction within 1.5 tiles: let go and they **merge**;
  - onto a road within 1 tile: let go and it **joins** there as a T.
- **Live warnings** at the handle: red if the junction is under 4 tiles
  from another along a road (`JUNCTIONS_APART`), if two legs meet under
  60° (AASHTO), if it is in water no bridge can span, or over the main
  roads' slope limit (`maxRoadSlope`).
- **Crossing a road** while dragging makes a junction where they cross
  (shown as a ghost junction); let go and it's there.

**On the server, when you pause (the re-plan)**
- After 150 ms without moving, or on letting go, the browser sends the
  draft's skeleton to the **draft worker** (§6.4), with a sequence
  number. An older answer that arrives after a newer request is thrown
  away.
- The worker re-plans the town round the skeleton and returns the
  difference: roads, lanes, junction forms, blocks, lots, buildings, and
  problems.
- While it works, the parts that will change are drawn faded. An answer
  usually lands in a quarter of a second (§8).

### 6.4 The draft worker and what it re-plans

The draft worker is the sim worker binary in another mode
(`porchlight-sim --draft`), started beside it, so it runs the very same
generator. It keeps each open draft's last model in memory.

When a skeleton comes in:

1. **Which stages to re-run.** A skeleton change can't touch Terrain,
   Rivers, Site, Airport or Zoo: those are cached once per draft. The
   re-plan starts at Roads.
2. **Roads.** Pinned main roads are laid as given. Unpinned main roads
   are routed as before, but a pinned road counts as one to join. The
   knot clean-up (`RoadGraphOps.CollapseShortLinks`) runs, **but may not
   move a pinned point**; a knot it can't fix without one is a problem
   (§6.5), not a silent change.
3. **Streets, region by region.** The land between the main roads is cut
   into regions as now. A region whose boundary is unchanged (same
   signature, §4.1) keeps its streets from the last answer, untouched. A
   changed region is re-planned by `StreetPlan` from its own stable seed,
   with any pinned streets inside it kept and planned round. This is the
   step that makes "move one road" re-lay the streets near it, and only
   there.
4. **Blocks, lots, buildings.** Unchanged blocks (same signature) keep
   their lots and buildings. Changed blocks are re-lotted and rebuilt
   from their own seeds. Town-wide passes (Plan, Land, the high street,
   neighbourhoods) run whole; they're cheap next to the rest (measure).
5. **Junctions (part A)** on the new graph. Design flows are worked out
   again only for the regions that changed and the arterials they touch;
   elsewhere last answer's flows stand, scaled by the change in
   households (a cheap approximation; the full BPR rounds run on Run a
   day and on Commit).
6. **Checks.** Every stage's `Check` runs, and becomes the problems list.
7. **The answer:** what changed against the last answer, by signature:
   roads and blocks added and removed, and the new ones' geometry and
   buildings. A small JSON diff, not the whole town.

**So the town reacts logically.** Drag a main road and:
- the streets either side re-plat themselves to meet it square, a
  block's depth away, as the generator always does;
- blocks on both sides re-lot, and houses face the road they're on;
- where it now crosses another main road at 5 legs or a sharp angle, a
  roundabout appears; where a junction now carries more, it gains turn
  pockets; where a road now takes the traffic of two, it widens to two
  lanes each way, or splits into a divided road if it can;
- if the change overloads a road, the problems list says so, and the
  design stage tries a relief route.

### 6.5 Problems

Each is one of the generator's own checks, now shown to a person instead
of failing a test. **Errors** stop Commit; **warnings** don't.

| Problem | Kind | From |
| --- | --- | --- |
| Two junctions under 4 tiles apart along a main road, 3 along a street | error | `RoadGraphOps.CheckKnots`, `Knotted` |
| Two roads meeting under 60° (40° if the editor insists) | error | city-research §1 |
| A road crossing water with no bridge, or too steep | error | `Roads.CheckRoadSlopes`, `Streets.CheckCrossings` |
| A gate, the airport or the zoo not joined to the centre | error | Roads' checks |
| Two roads between the same two junctions | error | `Streets.CheckDuplicates` |
| A street within 4 tiles alongside another road | warning (it'll be dropped) | `StreetsStage.MIN_APART` |
| A link over 1.0 v/c in the design flow | warning | part A |
| Arterial traffic routed through a neighbourhood's local streets | warning | the LTN rat-run test |
| The town's population falls | warning, with the net loss (§13) | §7.4 |
| A pinned point dropped (it broke after a generator change) | warning | §4.3 |

### 6.6 Run a day

**Run a day** runs the draft through a full simulated day in the draft
worker, as `Checks traffic-day` does (a day of Porchlight in about 5 s),
from the same population. It shows, beside the live town's last weekday:

- the mean and 90th-percentile trip time, by car and on foot;
- failed and given-up trips (`Life/Gridlock.cs`);
- the ten worst junctions by delay, with a heat map;
- each pocket's worst queue against its storage (overflowing pockets
  are listed).

This is the mayor proposal's "preview tomorrow" (PR #25), for streets.

### 6.7 Drafts and staging

- **Saved as you go:** the skeleton and undo commands are written to
  `town_drafts` a second after each change. Close the tab, come back
  tomorrow, it's there.
- **One draft per editor per town.** Two editors each have their own.
- **The live town doesn't know.** Players play on the current revision.
  Nothing in a draft reaches the game, the sim worker, or the county map.
- **Out of date.** If someone else commits while your draft is open,
  your draft's `base_rev` is behind. The editor says so and **rebases**:
  your skeleton is laid on the new revision's. Pins that land in the
  same place stay; ones that clash (you both moved one junction) are
  listed for you to choose.
- **Discard** throws the draft away.

---

## 7. Commit: out, rebuilt, back in

### 7.1 What the editor sees

**Commit…** opens a summary: the changes, the problems (Commit is
disabled while there are errors), how many players are in the town now,
how many households need a new house, and a message field (it goes in
the revision's history). Confirm starts it.

### 7.2 What the server does

1. **Validate.** The draft worker generates the town from the new key
   and skeleton with every check on, from scratch (not the incremental
   cache), so what's committed is exactly what everyone will build.
2. **Save.** In one transaction, with the town's row locked: write
   `town_revisions` rev N+1, set `towns.key` to the key with
   `&rev=N+1`, compute the houses from the model, rehouse (§7.4).
3. **Warn.** The room on the old key broadcasts **town-rebuilding**:
   the town's name, who's rebuilding it, and a countdown (10 s). The game
   shows a banner: "Porchlight's streets are being rebuilt. You'll be
   back in a moment."
4. **Out.** At zero, the room saves everyone (as on leaving) and closes
   each connection with a new close code, `TOWN_REBUILT`, carrying the
   new key. Players driving in on a highway are held at the gate.
5. **The sim worker** gets a TOWNS frame with the old key gone and the
   new one (with its skeleton) in. It builds the town and starts its life
   **mid-day**, from the clock, as it does for any town it picks up
   (town-life §1, "Starting mid-day"). Drivers' habits
   (`Life/Congestion.cs`) carry over for lanes that are unchanged
   (matched by their road's end points and direction); the rest start
   fresh.
6. **Manage** refreshes the town's roads on the county map from the
   worker's READY.

### 7.3 What the player sees

- A loading screen: "Rebuilding Porchlight…", the progress bar the game
  already shows for generation.
- The game fetches the skeleton, generates the town from the new key, and
  rejoins the new room. A town made ahead or kept for coming back has the
  old key, so it's simply not used.
- **Where they stand:** their saved spot if it's still on a road,
  sidewalk or open ground in the new town; otherwise the nearest such
  point (within 8 tiles); otherwise their front door.
- **A taken car** is handed back to the town, and they're on foot.
- **Players in a mission** are told why it stopped, and can restart it.

### 7.4 Rehousing

`settleStarterTown`'s rehousing already moves households when a town is
regenerated. For a commit:

- a household whose **house tile is still a house** stays (with locality,
  §4.1, that's nearly everyone);
- one whose house went is moved to the **nearest free house** to where
  theirs was (not a random one, as today), a household together;
- if there's no free house, they choose again the next time they come in,
  as today;
- the commit summary named them, and they get a note in game the next
  time they play: "Your house on Elm Street was replaced by the new
  roundabout; you've moved to 14 Oak Lane."

---

## 8. Budgets

| Work | Budget | Today, or how we know |
| --- | --- | --- |
| Rubber-band drag in the browser | 16 ms a frame | moves a few hundred points; trivial |
| Draft re-plan after a drag | under 300 ms | a full generation is 2.6 s; a drag touches a few regions of ~30 in Porchlight, so a tenth of Streets, Lots and Buildings *(measure each stage's share)* |
| Commit's full validation | under 5 s | a full generation with checks: 2.6 s |
| Design flows (BPR × 4) in a full generation | under 300 ms | one day of routing all trips is part of the 5 s day; a peak hour is a tenth of the trips, four times |
| Run a day | under 10 s | `traffic-day`: about 5 s for Porchlight |
| Sim cost with lanes | 1 ms a town a tick | 0.26 ms today; pockets and second lanes add maybe a third more lanes *(measure)* |
| Players out of the town on commit | under 15 s | 10 s warning, then the normal load |

---

## 9. Checks

New and changed checks, in `unity/Tools~/Checks`, run by CI where the
existing ones are:

- **`Checks locality`** (§4.1): move a node, count blocks changed far
  from it. Must be zero beyond one region.
- **`Checks skeleton`**: for each starter town, a skeleton that pins
  every road as generated builds the identical town (round trip); a
  skeleton saved, loaded and built on the sim worker and the game's code
  path gives the same houses (the game and the server must agree, as
  `sim --follow` holds them today).
- **`Checks junctions`**: every movement allowed by some lane; every
  lane reaches somewhere; every pocket at least 2 cars long and clear of
  the junction behind it; no lane drops mid-block; every roundabout's
  entries under 85% of capacity at design flow; every slip road joins at
  60° or more.
- **`Checks traffic-day`** gains, for each town: no pocket overflowing
  for more than 5% of the peak hour, and the day's mean trip time no
  worse than before the design stage (it should be better).
- **`Checks plan`** and **`Checks streets`** gain the share of arterial
  length with two lanes, the number of roundabouts, and that FHWA's class
  shares still hold after relief routes upgrade collectors.
- **`Checks edit`**: a scripted set of drags on each starter town (move
  the centre, drag a main road across another, delete a street, draw a
  new one): each must answer with no exceptions, the problems expected,
  and a full regeneration of the result equal to the incremental one.
- **Server tests** (`server/test`): a commit with players in the room
  sends `town-rebuilding`, closes them with `TOWN_REBUILT` and the new
  key, and rehouses by tile; two editors' drafts rebase; a non-editor
  can't open or commit.

---

## 10. The order to build it in

Each step is a PR of its own, testable on its own. The two tracks can
run side by side once step 1 is in.

**Foundation**
1. **Local generation** (§4.1): per-region and per-block seeds,
   `Checks locality`. Starter towns regenerate once (everyone rehoused
   by the existing code) and are stable from then on.
2. **The skeleton** (§4.2–4.4): `&rev`, the tables, the generator taking
   a skeleton, the game and sim fetching it, `Checks skeleton`. Tested
   with a skeleton written by hand.
3. **Commit** (§7): the server flow, the close code, the game's rebuild
   and rejoin, nearest-house rehousing, server tests. Tested with an API
   call that commits a hand-written skeleton.

**Track A: junctions**

4. **Lanes per direction** in `RoadNet` and `Cars`: lanes with `allows`,
   fans, lane choice, lane-to-lane movements. No generator change yet:
   tested with hand-made roads in the checks, and `traffic-day` unchanged
   with every road at one lane.
5. **Design flows** and **turn pockets**, with protected left stages,
   drawn in game and Manage.
6. **Roundabouts**: generation, the ring in the sim, drawing,
   `Checks junctions`. Re-try check-64 and check-81's knots.
7. **Wider roads, divided roads, slip roads, relief routes**, and the
   right-of-way in Lots.

**Track B: the editor**

8. **The edit screen, read-only:** the route, the draft town drawn,
   handles, editors only.
9. **Drag and re-plan:** rubber band, snapping, the draft worker,
   incremental Streets and Lots, the problems list, undo, drafts saved,
   Commit wired to step 3.
10. **The rest of the tools:** draw, delete, split, merge, road and
    junction overrides (these need track A's model), locks.
11. **Run a day** and the comparison; the Changes panel's resident
    names; the in-game "you've moved" note; rebasing drafts.

---

## 11. Open questions

- **Who edits the starter towns** in the long run? `EDITORS` for now;
  the mayor proposal (PR #25) suggests an election among residents.
- **How often may a town be rebuilt?** A commit throws everyone out. A
  cool-down (one commit an hour for a shared town) *(decide)*.
- **Should part A run on towns already made?** Turning on the design
  stage changes every town's roads, so every starter town is regenerated
  once (as step 1 does anyway). Players' own towns too, or only on their
  next commit? *(decide)*
- **Measured flows after a commit:** re-running part A from the town's
  measured traffic could keep a town improving on its own, weekly. That
  overlaps with the mayor proposal's "the player does it". Probably an
  editor's button, "re-design from this week's traffic", not automatic.
- **Grade separation** (an overpass where a divided road crosses the
  ring) would need bridges over roads, not just water. Later.
- **Multi-lane roundabouts** (two lanes on the ring) are where real
  towns get it wrong; leave until a town needs one.

---

## 12. What's built

Everything above is in, with these differences from the plan. The
contract between the parts is [street-editor-api.md](street-editor-api.md).

**Generation** (`Gen/Design.cs`, a stage after Buildings)
- The warrants are scaled down for a game town (`LEFT_AT_SIGNAL` 40 an
  hour, the cross-product 15,000, `RIGHT_AT` 120, `SLIP_AT` 150). A town
  of a few thousand rarely reaches real-world numbers. Widening is at 60%
  of a lane's capacity, not 85%.
- Roundabouts also replace a busy four-way stop between collectors, and,
  outside the old core, a signal quiet enough for one lane round a ring.
  They never take anything but houses (at most two, unless an editor asks).
- The roads in from the gates are divided, two lanes each way, to their
  first junction, but only in a big town (radius 100 tiles or more) and
  only where they stay out of the middle. Lots keep back from those roads
  by the width they grow to (the right of way in Lots).
- Houses taken for a wider road or a ring are removed and the remaining
  buildings renumbered, with the neighbourhoods' schools and corner shops
  and the town's services renumbered too.
- Relief routes are written and checked, but no starter town needs one at
  the design flows: the worst v/c is about 0.4.

**The road network** (`Life/RoadNet.cs`, `Life/Cars.cs`)
- **Lanes:** a road with two lanes each way has a kerb lane (rights,
  straight on) and an inner lane (lefts, straight on). Cars choose their
  lane at the junction before, not by changing lanes along the road.
  `LaneOf` is still the kerb lane, where every spot and door is.
- **Pockets:** a fan splits the lane into a through lane and the pocket.
  The stretch past the fan has no parking, as on a real road. A pocket is
  left out where the road is too short for its fan.
- **Roundabouts:** a junction with `Control.Yield`. Its turns run round
  the ring; entering gives way to anything already going round past the
  entry (the HCM's 4.5 s gap). Two turns conflict when one enters where
  the other is going round.
- **Protected lefts:** a *leading* left inside the two-stage cycle, not a
  third stage. The way opposite the left pocket is held for the first
  7 s of its green (`LEAD_MS`), so the lefts go first. The lamps and the
  wire still have two stages; the hold isn't shown on the lights.
- **Slip roads:** the right turn is free of the signal and gives way to
  what it joins. Cars take the corner's own curve, not the slip's drawn
  line.

**Drawing:** the game draws each road at its designed width. It also
draws medians, the dashed line between lanes, each pocket's edge and
arrow, roundabout rings with grass islands, and slip roads. Manage's
editor draws the same, plus signals, stop and yield signs. The game
doesn't draw yield signs yet.

**Commit:** the close code is **4103** (4100 was already taken for
"signed in elsewhere"). The close reason is `rev=N`, because a WebSocket
close reason can't hold a whole key; the new key arrives in
`town-rebuilding`. On 4103 the game fetches the character again (the
server may have moved their household), grows the new revision from its
skeleton and goes back in.

**Known gaps:** a town edited without moving anything still loses a few
dozen houses, because streets re-plan round pinned main roads. `day` in
the draft worker takes over a minute. Old keys can still be joined after a
commit. The Ember Crossing plan check fails on block size (10×19 tiles
against about 15×20) since local generation went in. `Checks life` has
three failures that main doesn't: a car in Ember Crossing speeds up by
0.06 tiles/s with a player 3.6 tiles ahead; Juniper Falls' busiest
junction gets no car waiting for a player standing in it; and
Lanternport's smoothness is 0.26% of moments over 3 tiles/s² (limit
0.2%, from 8 of 3,122). In that last one a car arrives at a stop junction
at speed and stops dead. Two of the three also failed before the lanes
went in, with only the design stage changing the towns.

---

## 13. Picking blocks and regenerating them

Dragging junctions and bends asked the editor to do the street planner's job, and every drag could break
one of its rules (junctions too close, roads meeting too sharply). The editor now says *where* and *what*,
and the generator plans the streets.

**The screen.**
- Click blocks on the map to pick them, and click again to put one back. Shift-drag, or a drag in Box
  mode, picks every block whose middle is inside the box.
- **Regenerate** re-plans the picked area. The boxes below it ask for what the area should have:
  **Roundabout**, **Park**, **School**, **Shops** and **Houses**. With none ticked, the area is just
  planned again differently.
- Pressing Regenerate again re-rolls the same area: after each plan, the blocks inside the area are
  picked again.
- **Undo** sits beside Regenerate. Each Regenerate press and each bullseye move is one step.
- **Commit** is live only while the draft differs from the town as it is, and not while a plan is in flight.
- No Problems list: the worker puts problems right itself (below). One line under the boxes says when an
  area couldn't have what was asked ("No room for a roundabout there").

**The skeleton** (`Skeleton.regions`, docs/street-editor-api.md). Each press adds a region:
- the picked blocks' outlines as they were,
- a salt (any number; another number gives another plan),
- what was wanted.

Regions apply in order, a later one over an earlier. The first region also records where the town's
extra parks are (`parks`), so they stay put.

**The area** (`Gen/Regions.cs`).
- The picked outlines are filled in and then closed: grown by two tiles and shrunk back. That takes in the
  streets between the picked blocks but not the streets round the edge.
- A road the area clears must lie at least half inside it, with both ends within 3½ tiles of it. A road
  that runs on out of the area, such as the airport's, stays.
- Main roads are never cleared. The main roads, the town's circle and everything outside the area stay
  as they were.

**What each stage does with an area.**
- **Streets:** clears the area's streets and runs the street planner again over its land, with a random
  stream named by the salt. In an area, the blocks are turned either way as often as not, and the streets
  stray further from their places, so each salt looks different.
- **Lots, buildings and cul-de-sacs:** draw from the area's stream instead of their own.
- **Park:** the area's block nearest its middle, of a park's size, is kept green, unless one of its blocks
  is a park already.
- **Shops:** the area's blocks become T5, and its houses and workshops become shops.
- **Houses:** blocks are built up no more than T4, and shops and workshops become houses.
- **Shops and houses together:** each block is one or the other, by the area's stream.
- **School:** the building nearest the area's middle with room for one, unless the area has a school
  already. It is in addition to the neighbourhood's own school.
- **Roundabout:** the design stage tries the area's junctions first, busiest first (with those on the
  roads round its edge), insisting as an editor's junction override does. It tries up to six.

**Parks elsewhere stay put.** The town's extra parks (`LandStage.MoreParks`: blocks kept green until
the town has its park land a head) are chosen over the whole town, furthest from any park first. New
houses or a new park in an area would move them. So the first region records where they are
(`Skeleton.parks`, from the town without regions), and from then on exactly those blocks are kept green
outside the areas.

**Problems put right.** The draft worker's `plan` settles an edit before it answers:
- If a stage's rules break, or the newest area lacks what was asked for, the newest area is planned again
  with another salt, up to five tries. The first try that keeps the rules and has everything wins. If none
  has everything, the first that keeps the rules is used, with a one-line note.
- If nothing keeps the rules, the area is left out ("Couldn't regenerate that area …").
- A moved downtown that breaks a rule is pulled halfway back to the centre, up to three times.
- The answer carries the skeleton it settled on, and Manage adopts it. A commit grows that skeleton as
  it is.
- The junction gap and angle checks of §6.5 are gone, along with the pinned-point warnings: the
  generator's own rules (each stage's `Check`) are what count.

**The bullseye is the downtown** (`TownModel.downtown`, `SiteStage.Downtown`).
- Moving it no longer moves the town. The circle, the gates and the main roads stay where they are.
- The core era, the transect zones, the land value's centrality, the high street, the plaza and the
  church, the shops' spread, block sizes and the buildings' heights all gather round the downtown instead.
- It is held within 0.7 of the town's radius from the centre, on dry ground a road can reach (the nearest
  within 8 tiles).
- With no bullseye, the downtown is the centre, so a town nobody edited grows exactly as before.

**The roundabout's markings** (the game's `World.RingMarks`).
- The approach roads' centre dashes and edge lines stop at the ring.
- The ring has a solid edge line, broken where each road comes in, and a dashed give-way line across
  each entry.
- A ring wide enough for two lanes has dashes between them all the way round.

**What changed is the town's people, not its houses.** Planning an area again moves, adds and takes away
houses, so a list of the houses lost (and crosses on the map) said little. The plan answers the town's
population before and after (households × `LandStage.PEOPLE_A_HOME`), and Manage shows one warning line
when it falls ("Population down by 40 (1,210 → 1,170)"). A note that the newest area couldn't have what
was asked for comes first. Anyone whose house goes still moves to the nearest free one on commit (§7.4).

**Speed.** A regenerate takes about a second in Porchlight, the first one included:
- The draft worker keeps each town through its main roads, by key. The bullseye moves only the downtown,
  which is set again on the kept town, so moving it doesn't grow the main roads again.
- The lots and buildings stages place each block's lots and each lot's building on every core in the
  simulation and the draft worker (`Gen/Work.cs`). Each is a pure function of its block or lot and is put
  together in order afterwards, so the town is the same to the byte. The game (WebGL, no threads) places
  them one at a time, as before.
- The buildings check looks only at the roads near each building (it was a third of a second).
- The parks the town kept green are taken from the edit screen's first plan, so the first regenerate
  doesn't grow the town twice.

**Run a day has a worker of its own.** A day of Porchlight is over a minute's work (draft and live, one
after the other). It used to share the draft worker, so a regenerate clicked meanwhile waited for both.
Days now run in a second worker, and a second click while one is running waits for it instead of
starting another.

**Checks.** `Checks regions` picks patches of blocks in each starter town and regenerates each with every
option alone, all of them and none, three salts each, every stage checked. It fails a patch that comes
out the same for every salt, that breaks the rules on every salt, or that changes buildings more than 12
tiles outside it.

---

## Credits

The ideas of lane-level turns with lane choice at the start of a road,
roundabouts as a ring of one-way roads, map edits as a list of commands
with a reset of the simulation, and the raw map editor whose road points
you drag and rebuild from, come from **A/B Street** (Dustin Carlino and
contributors, Apache-2.0) and **osm2streets**. This plan was written from
how those tools behave and are described; no A/B Street code was read,
copied or translated, and Porchlight's code will be its own
implementation, as with [town-life.md](town-life.md#credits).

## References

- A/B Street: <https://a-b-street.github.io/docs/> (the map editor, lane
  editing, edits and the simulation reset), and its retrospective on
  lane changing in a discrete-event sim (city-research [T18]).
- osm2streets: <https://github.com/a-b-street/osm2streets> (junction
  geometry, collapsing short roads, dual carriageways).
- FHWA, *Roundabouts: An Informational Guide*, 2nd ed. (NCHRP Report
  672, 2010): sizes, single-lane and mini roundabouts.
- *Highway Capacity Manual*, 6th ed. (TRB, 2016): roundabout entry
  capacity c = 1380 e^(−0.00102 v_c); two-way-stop critical gaps
  (city-research [T12]); saturation flow (city-research [T8]).
- NCHRP Report 457, *Evaluating Intersection Improvements* (2001): left-
  and right-turn lane guidelines (the Harmelink curves).
- Webster, F. V., *Traffic Signal Settings* (1958): already used for
  signal timing (city-research [T10]).
- BPR function and successive averages for assignment (city-research
  [T14], [T15]).
- AASHTO, *A Policy on Geometric Design of Highways and Streets*: the 60°
  least angle at a junction (city-research [T48]).
- Braess, D. (1968): a new road can make traffic worse; the reason relief
  routes are tested, not assumed.

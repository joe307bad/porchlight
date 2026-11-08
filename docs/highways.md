# Highways between towns: research and a plan

How Porchlight could link its towns with highways so you can drive from one town to the next with no loading screen.

## What's built

The plan below is now in the code. How it's used day to day is in `unity/README.md` (Driving the county) and `infra/README.md` (The county).

**Generator**
- Every town has four gates, one in the middle of each edge, on a level, dry pad that rivers keep away from.
- Porchlight is regenerated: 1,509 houses, everyone rehoused.

**The county**
- There are three new public cities: Lanternport (east, 224×192, a harbour), Ember Crossing (south, 256×256, towers) and Juniper Falls (west, 144×160, hills and rivers).
- `Shared/County` lays out the county and builds each road space as a town. `server/src/county.ts` does the same layout on the server, held to it by test vectors.
- `Checks county` drives every highway end to end, crossing every hand-over, including into the real public cities.

**Server**
- Rooms own territories: their town, half of each of their highways, and the nearest countryside.
- A join can say where it's `arrive`-ing.
- The north room (`north:<account>`) holds the junction and your empty plots.
- `towns.slot` records which of your three places a town is in.
- Night and day are the same across the whole county.

**Game**
- `CountyDrive` builds spaces as you approach, switches you into them with a floating origin, and changes rooms at the territory line.
- `NetFrame` translates positions between each room's terms and the space you're in.
- Planes fly and land anywhere over the countryside.
- Directions to any of the account's houses in another town of the county, your own towns included: out by the right gate and along the highways, then along the town's streets to the door (`County/CountyRoute`, followed end to end by `Checks county`).
- Highways wind, and the countryside between them rises into hills (`County/CountyTerrain`), level along every road and round every town.
- Seamless hand-overs: the weather, the clouds, the lamps and the way shown all carry on over the line.
- The big map's County tab shows every town and how the highways join them to Porchlight.

**Where it differs from the plan below**
- **Two towns are held at once:** the one you're in and the next, so turning round is cheap.
- **Highway stretches are short spaces of their own:** at most 180 tiles, about 15 of them per highway once building and release ranges are counted. A highway is not one long space.
- **Building is not yet split finely:** a town's parts (its houses, its rooms) are each still built in a single step, so a big town can cost a frame or two as it goes up. Splitting those parts further is the next step if it shows.
- **No highway traffic yet:** the roads are yours alone.

**Not yet tried in Unity.** This session had no editor. The shared code is tested on .NET. The game code compiles against Unity's reference assemblies, with stubs standing in for the packages they lack. The parts to try first:
- the floating-origin shift of a static-batched town;
- the hand-over at a town's gate, in a car and on foot;
- frame times while a town builds on the web.

This is research, not a commitment. Wherever a number below comes from the code, it gives the file and line (paths are under `unity/Assets/Porchlight/Runtime/` unless they say otherwise). Numbers taken from outside sources are linked at the end, and anything unmeasured is marked **(measure)**.

---

## 1. The short version

- **The generator already gets us halfway.** Every town has 2–4 **gates**: main roads that `SiteStage` runs out to the map edge (`Shared/Gen/Site.cs:1-4`, `gateCount`, default 3). Today they end in a dead end, and traffic U-turns at them. A highway only has to start where a gate road stops.
- **Don't build one big world. Build a graph of small spaces.** Each town keeps its own local coordinates and its own `w×h` arrays, so the simulation code stays as it is. Each highway becomes a small space of its own: a long, thin "town" made of nothing but a main road. You drive in exactly one space at a time and hand over to the next at a gate, where the two overlap for a few tiles. That one choice lets nearly every system (`Drivable`, `Traffic`, `Walking`, `WorldSync`, the server's bounds checks) work almost unchanged.
- **Never hold two towns at once.** The close camera's fog ends at 60 tiles (`ExploreView.cs:108-112`). If a highway is longer than about two fog distances plus the build time at full speed, the town behind can be torn down before the town ahead is built. Memory stays at one town, as it is today.
- **The hard part is the build budget, not the highway.** Today a town is generated and built behind a loading bar in 50 ms slices (`PorchlightApp.cs:246`), and each generation stage runs in one uninterrupted piece. To build while you drive, the work has to fit in about 4–6 ms a frame, which means splitting the big stages and `TownWorld.Build` into much smaller steps.
- **The county has a fixed shape, decided in advance** (§4.2):
  - Porchlight sits in the middle with four gates, one at each compass point.
  - **North** always leads to *your own* towns: a private loop of highways with up to three town slots on it.
  - **East, south and west** lead to three public cities, joined to each other in a public ring.
- **Every town's gate sits at the middle of an edge**, and every slot has a fixed position. So the whole layout (where each town sits, every highway, which room owns which ground) can be worked out from the town keys alone, with no generator. The server can work it out too, from a small shared piece of layout code.
- **Planes fly between any two towns** in a straight line, over countryside that the same layout describes (§4.7).
- **Multiplayer v1: each town room owns half of each of its highways.** At the highway's midpoint, far from anyone's line of sight, the client leaves one room and joins the next. Movement is already client-side, so the car keeps driving through the gap of about a second. Later, joining the next room early (a ghost band) can close even that gap.

How long a highway needs to be, worked through in §5: roughly **300–500 tiles (1.2–2 km), or 35–60 s at boost**. That is long enough to feel like a journey, and it lines up with the ~1:15–1:20 compression Euro Truck Simulator uses.

---

## 2. What the code does today (the facts this plan rests on)

| Area | Today | Why it matters |
|---|---|---|
| Scale | 1 tile = 1 Unity unit = 4 m (`Coords.cs`, `Directions.cs:24`). Maps run 32–256 tiles a side, 128 m–1 km (`Params.cs:281+`). Porchlight is 256×256. | Highways can be measured in tiles too. |
| Gates | 2–4 per town on edge tiles, spread by angle around the centre with ±0.3 rad of jitter, each with a main road A*-routed to the centre (`Site.cs:123-152`, `Roads.cs`). Porchlight has 3 (its key leaves `gateCount` at the default). | These are the highway on-ramps. |
| Map edge | A floating slab: four brown walls 1.6 units deep at x=0/w and y=0/h (`World.cs:326-329`), with nothing beyond them. `edgeFalloff` evens the terrain out toward the edge. | Needs countryside "skirts" beyond the edge, and an opening wherever a highway joins. |
| Terrain | Flat. `elevation` is always 0 (`Types.cs:222`), and only riverbeds sink. | The highway can be flat to begin with. Hills to block the view would be new work. |
| Generation | Deterministic (mulberry32 seeded from a string hash, fdlibm math), 12 stages, yielding only *between* stages (`Generator.cs:36-72`). | Each stage has to be split into smaller pieces to run in the background. |
| Building | `TownWorld.Build` yields per part (ground, roads, houses, rooms, …), then one `StaticBatchingUtility.Combine(still)` (`World.cs:120-180`). No chunks, no pooling. | The same splitting is needed here, plus a teardown that is spread across frames too. |
| Switching towns | `SetTownSteps` disposes everything and *then* builds the new town (`ExploreView.cs:339-439`). Behind the loading bar. | The order is right (never two towns at once); the timing is wrong. |
| Driving | Kinematic. Top speed 5.2 tiles/s (75 km/h), boost ×1.7 = 8.84 tiles/s (127 km/h). Stops dead at the map edge (`Driving.cs:3-4, 21-23, 268`). | Lets us work out the build budget (§5). |
| Camera | Close: fog 22→60 tiles. Iso: orthographic, camera 60 back. | Distant towns never need drawing, so no far LOD or impostors are needed at first. |
| Sync | `WorldSync` sends traffic positions as u16 × 1/100 tile, which caps at **655 tiles** (`WorldSync.cs:15-21`). | Fine as long as each space is under 655 tiles a side, so keep highway spaces shorter than that. |
| Server | One room per town key, 100 players (`index.ts:72`). Movement is checked against `0..width × 0..height` and 11.1 tiles/s (`movement.ts:13, 67`). Joining evicts the character's session in *any* room. `Multiplayer.Join` disconnects before it joins (`Multiplayer.cs:178, 272`). | The bounds checks have to stretch to cover half of each highway, and handing over between rooms needs a path that doesn't evict. |
| Day/night | Kept per town, in memory (`CityRoom.ts:143`). | Driving into the next town could flip night to day. It needs one shared clock. |
| Platform | WebGL 2, **no threads** (`webGLThreadsSupport: 0`), up to 4 GB of memory, no Addressables. Unity 6000.6.5f1. | Main-thread time slicing is the only option that works on every platform. |

---

## 3. What other games do (the parts that apply here)

**Stream by cells, and cap the player's speed to what the streaming can keep up with.** Spider-Man (PS4) streams about 800 tiles of ~128 m and loads about one new tile a second at full swing. The PS5 sequel doubled the swing speed because its SSD could keep up. GTA III couldn't load fast enough on PS2, so it slowed players down: it redesigned a long straight road into a winding one, added about 5% extra air drag in certain places, and made the plane slow. Forza Horizon 2 locked to 30 fps partly for its streaming budget. *For us:* top speed is the knob that sets how long a highway has to be. A hidden "roadworks" slowdown is the fallback if a slow device falls behind.

**Shrink the distances.** ETS2 runs at about 1:19 (mainland) and 1:15 (UK) on a grid of 4 km sectors. The Crew shrinks the whole of the USA. A highway only has to be long enough to *feel* like travel and to hide the build.

**Hide loads with level design** ("loading corridors"):
- Tunnels: no line of sight at all, so they are the safest place to do heavy work.
- Hill crests and long curves: the next town can't be seen until it is ready.
- Toll plazas, level crossings, roadworks: they force you to slow down, and can step in only when needed.
- Haze: we already have it. Fog at 60 tiles does most of the work for free.

**Seamless multiplayer zones** all follow the same pattern:
- Exactly one server is in charge of each entity.
- Neighbouring zones show read-only "ghosts" inside a border band.
- The client is already connected to the next zone before it crosses.
- Authority passes at a line a little past the visual edge (hysteresis), so it doesn't flap.

New World's hubs, Star Citizen's server meshing, SpatialOS and old seamless MMOs all do this. EVE skips it and hides a hard handoff behind the jump animation. Colyseus clients can sit in several rooms at once, and the official Unity MMO demo moves players between grid-cell rooms using seat reservations.

**Procedural road networks between settlements.** These are standard:
- Galin et al. 2010 route each road with anisotropic A* over terrain, with costs for slope, water, and bridges and tunnels.
- Galin et al. 2011 decide *which* towns to link using proximity graphs. In order of density: MST ⊂ relative-neighbourhood graph (RNG) ⊂ Gabriel ⊂ Delaunay. RNG is the usual choice for highways: a tree with a few natural loops.

For worlds generated lazily, Minecraft-style, put one town per region. Place each town at `hash(worldSeed, rx, ry)` with an edge margin, so any chunk can find its neighbours by hashing only the regions around it.

---

## 4. Proposed design

### 4.1 A world made of spaces

```
            ┌──────────── highway H1 (space) ────────────┐
 Town A ═gate0═══════════════════╪═══════════════════gate2═ Town B
 (space)   │  overlap ~8 tiles   midpoint  overlap      │   (space)
           └─ A's room owns ───────┘└──── B's room owns ──┘
```

- **Space**: anything with its own local tile grid and its own `Drivable`, `Traffic`, `Paved` and so on. A town is a space, using today's `TownModel` unchanged. A highway is a space too: a `HighwayModel` that rasterizes into a `TileGrid` the size of its bounding box, with one `RoadKind.Main` road along a spline, countryside ground, pines, maybe a lay-by.
  - Because a highway produces the same kind of grid, `Drivable`, `Traffic` and the driving code take it without modification.
  - Keep each highway space under 655 tiles on its long side (the `WorldSync` limit, and a sensible raster size). Split longer roads into segments.
- **World placement**: each space has an integer tile offset in world coordinates. Keep them axis-aligned (no rotation), so converting between spaces is just adding the offset.
- **Overlap**: each highway's raster takes in the last ~8 tiles of the gate road inside the town, and the town's `Drivable` gets the first ~8 tiles of highway beyond its edge. In that strip both spaces say "open", so the active space can switch over without the car ever hitting the `x ≥ w−0.05` wall (`Driving.cs:268`).
- **Active space**: exactly one at a time, which is what `ExploreView` assumes today. The car's position is held in the active space's local coordinates. When it crosses the gate line (with a few tiles of hysteresis), convert its position by `offsetA − offsetB` and swap which model `ExploreView` is driving.
- **Floating origin is unnecessary at first.** A county a few kilometres across sits well within float precision (about 1 mm of error at 10 km). Because the simulation already runs in local doubles, only the render roots carry the offset. If counties grow past ~10 km, re-centre at each space switch by moving town roots. `Combine(still.gameObject)` batches under a root that can be moved, though check that before relying on it.

### 4.2 The county's shape

```
                 ┌──────── your private loop (yours alone) ────────┐
                 │   [slot 1]══════════[slot 2]══════════[slot 3]  │
                 │      ║                                   ║      │
                 │      ╚══════════════╗   ╔════════════════╝      │
                 │                  junction                       │
                 └──────────────────────╫──────────────────────────┘
                                        ║  north: private
  [West city]═══════════════════════[PORCHLIGHT]═══════════════════════[East city]
       ║            west: public         ║          east: public            ║
       ║                                 ║  south: public                   ║
       ╚═════════════ rim ═══════[South city]══════════ rim ═══════════════╝
```

**Public: a half wheel.** Porchlight's east, south and west gates run straight out (as spokes) to three public cities. Two rim highways link East to South and South to West, so you can drive the whole ring without passing back through Porchlight.
- The three cities are fixed keys, the same for everyone, like Porchlight. They should be bigger and denser than a default town, so they feel like *cities*. Each has a name and is committed in `server/towns/`, the same way `porchlight.json` is.
- Each city uses two or three of its gates: East uses W and S, South uses N, E and W, West uses E and S. Any gate left over ends in a "Road closed" barrier, a deliberate dead end.

**Private: a loop off the north exit.** North of Porchlight, the highway runs to a junction. From the junction, a loop runs through **three town slots** and back again. Your up to three private towns (`MAX_TOWNS = 3`, `persistence.ts:105`) each sit in a slot.
- **The loop keeps the same shape however many towns you have.** An empty slot is a vacant plot the road runs straight through, with a sign ("Your town could go here") that opens the town editor. With one town, the loop is still a loop.
- **The highway goes through each town.** It comes in at one gate and leaves at another: slot 1 S→E, slot 2 W→E, slot 3 W→S. Every gate road already meets at the town centre, so the loop passes through downtown, the way old highways ran down Main Street.
- **Everyone's north exit leads to their own loop.** Two players from different accounts can drive north together as far as the midpoint of the north highway. Past that point, each of them is in their own world, and the other fades out into the fog. That is the same effect as private towns today, now with a road in place of a menu.
- **Which slot a town takes** needs a new `slot` column (0–2) on `towns`. A new town takes the first free slot, and deleting a town frees its slot.

**Gates on compass points, at edge midpoints.** Today gates are spread at a random starting angle (`Site.cs:126`), and each is moved to the nearest dry, reachable edge tile. To make the layout fixed:
- Add a gate mode (a param, e.g. `gates=compass`) that puts the gates exactly at `(w/2, 0)` (N), `(w, h/2)` (E), `(w/2, h)` (S) and `(0, h/2)` (W).
- The terrain stage keeps a dry band from each gate to the centre, so a gate can never land in a lake.
- Then **a gate's position depends only on `w` and `h`**, and both are already in the key. So the layout code needs nothing from the generator.

**Slots are fixed-size boxes.** Every town slot is a 256×256 box, the biggest map there is. A smaller town sits in the middle of its box, and the highway carries on through the box to reach its gate. So the shape of the county never depends on how big each town is. Rough placements (world tiles, y = south, Porchlight at 0..256):

| Place | Box (x, y) | Notes |
|---|---|---|
| Porchlight | 0..256, 0..256 | 4 gates |
| East city | 656..912, 0..256 | spoke: 400 tiles straight |
| South city | 0..256, 656..912 | spoke: 400 tiles straight |
| West city | −656..−400, 0..256 | spoke: 400 tiles straight |
| East↔South, South↔West rims | — | quarter-circle curves of about 830 tiles, each split into 2 highway segments (under the 655-tile limit), with a rest stop in the middle |
| Junction | 104..152, −352..−304 | end of the north highway (about 300 tiles long) |
| Private slots 1–3 | y −912..−656, x as West, Porchlight, East | the loop's segments are 400–700 tiles each |

The 400-tile gap comes from the budget in §5. Re-run that sum once the build has been measured.

**Shaping each highway:** a cubic Bézier between two gate points.
- The tangents are each gate's outward direction, so a road always leaves a town straight. The spokes are simply straight; the rims and the private loop are smooth quarter turns.
- Sample every tile and check the minimum radius. Something like 60–100 tiles is enough for a car this arcade-like.
- Add a crest or an S-bend where a long straight would let you see too far ahead.

**One piece of layout code, in both languages.** `CountyLayout(publicKeys, myPrivateTownsBySlot)` returns the boxes, gates, highway curves and room territories. It is small (boxes, Béziers, no generator), so it can be written in C# for the game and in TypeScript for the server's bounds checks. A shared test file of example layouts checks that the two agree.

**What this changes for existing towns.** Moving gates moves the main roads, and so the streets and houses too.
- Porchlight goes from 3 random gates to 4 compass gates. That is a one-off regeneration with `npm run town`. The server already rehouses everyone (households together) when that file changes.
- Existing private towns get compass gates as well, so their layouts change. People living in them will be asked to choose a house again; the game already does this when a house's tile no longer has a house.
- Do it once, with a clear note to players. Keeping the old gates instead would mean a "legacy" flag in every existing town's key.

**Later, for a bigger world:** add more rings beyond the cities, or move to a region grid (one town per region, placed by hash, linked by a relative-neighbourhood graph, so it can be generated lazily). Only worth it once the county feels small.

### 4.3 Building in the background

This is most of the work.

1. **Split the work into small pieces.** Turn each heavy generator stage (roads A*, streets, lots, buildings, rasterize) and each `TownWorld` part (houses, rooms, yards, places) into iterators that yield every *n* units of work, not once per stage. Keep the stage RNGs exactly as they are so towns don't change. The Checks `towns N` run will catch any drift.
2. **Make the budget adjustable.** Keep 50 ms behind the loading bar, use **4–6 ms a frame** while driving, and allow more inside a tunnel or when stopped. Scale the budget to the frame rate the device actually achieves.
3. **Spread out the teardown.** `TownWorld.Dispose()` destroys everything at once today, which is a big hitch for a 1,500-house town. Destroy in batches, or pool and reuse where it is cheap.
4. **Separate building from switching.** Today `SetTownSteps` disposes the old town and builds the new one in a single flow. Split it into `Prepare(space)` (in the background, renders nothing yet), `Activate(space)` and `Release(space)`.
5. **Keep allocations low.** On WebGL, garbage is only collected at the end of a frame and there is no incremental GC, so pool the big temporary lists.
6. **Use threads where we can, and nowhere else.** `Shared/` uses no engine code, so on **desktop** `Generator.Generate` can just run on a worker thread today. On the web, Unity 6.4+ can run *Burst jobs* on workers, but only with threads turned on and COOP/COEP headers, and it would mean porting the generator to Burst. That is not worth it at first: time slicing works everywhere.
7. **Optional: bake county towns ahead of time.** The offline tool could save each county town's model in a compact binary format, so the game downloads and unpacks it instead of generating it. This only pays off if measurements show generation, not GameObject building, is the slow part.

### 4.4 What happens on a drive (one town at a time)

Distances are in tiles, measured along the highway from A's gate (*L* = total length).

| Where you are | What runs in the background |
|---|---|
| Leave A's gate (0) | The highway space is already built: it is small and was prepared while you were in A. A is still drawn behind you. |
| 0 → ~80 | Nothing new. A falls into the fog at 60. |
| ~80 | **Release A**, spread over frames (traffic, people, interiors, then meshes). |
| A is gone → *L*−80 | **Prepare B**: generation steps, then build steps, at 4–6 ms a frame. Show progress somewhere subtle. |
| *L*−120 (the check) | If B isn't ready, tighten the hidden speed cap (more drag, GTA III style) or close a roadworks lane. The toll plaza can be the hard stop. |
| *L*−60 → *L* | B appears out of the fog. Cross the gate, B becomes the active space, and the highway is released. |

### 4.5 Multiplayer

**Rooms own ground, called territories.** The county layout splits all the ground (towns, highways and the countryside planes fly over) into territories, and each territory belongs to exactly one room:
- **Porchlight's room:** its box, plus the near half of all four of its highways.
- **Each public city's room:** its box, plus the near half of its spoke and rims.
- **Each private town's room** (owner only, as today): its slot box, plus the near half of the loop segments on either side.
- **A new private road room per account** (e.g. `north:<accountId>`, owner only): the far half of the north highway, the junction, empty slots, and whatever is left of the loop.
- **Countryside** (for planes): each patch goes to the nearest territory within the same world, public or your private one. Store the split as a coarse grid of 16-tile cells, the same size as the interest cells.

**v1: half highways, with a short swap at the midpoint.**
- Each room's bounds become its territory, not just its own rectangle. The server computes them with the shared TypeScript layout code; for private rooms it uses the owner's town keys and slots, which are already in the database. `inBounds` takes the territory instead of `0..w`.
- Highway positions are sent to the server in the town's own coordinates. They can be negative or beyond `w`, which is fine for the schema's floats. Highway traffic sync then needs either a signed offset or its own host stream.
- At the midpoint the client leaves A's room and joins B's. It sends `{arrive: {from: A, highway: H1, x, z, heading, speed}}` so the server places the character there instead of at their home door (today a new key goes through `ApplySpawn`).
  - The server must take that first move as the anchor. Today the first move is "taken as given" (`movement.ts:31`). Clamp it to the midpoint band so it can't be used to teleport.
  - Joining the next room must not count as a second tab and evict this one. Let the eviction pass for the same tab id when the old session is in the neighbouring room.
- The car keeps driving locally through the swap of about a second. Other players near the midpoint blink out and back. It happens on an empty stretch of road, and fog hides most of it.

**v2: pre-join (no blink).** About 3–5 s before the midpoint:
- A's room reserves a seat in B's room (`matchMaker.reserveSeatFor`) and sends it to the client, which joins as an observer. The client is now in two rooms and shows other players from both, de-duplicated by character.
- At the midpoint plus hysteresis, authority passes over, A keeps a ghost for a moment, and the client leaves A once it is well past.
- This needs `Multiplayer.cs` to hold two rooms. Today it holds exactly one, and `Join` always calls `Disconnect` first.

**A shared clock:** day/night has to come from one shared source (the county, or simply wall-clock time) rather than a separate per-town `nights` map.

**Private towns** are reached by the north exit (§4.2). Who may enter is still decided by `mayEnter`, now for the account's road room as well as its towns. Loading straight into a town (today's flow, and "Join") stays as fast travel.

### 4.6 What the player sees

- **Countryside skirts**: replace the brown slab walls with a ground mesh that runs past the edge (on all sides in the iso view), and open it where a highway joins.
- **Signs**: "Elmford 1.4 km" at each gate, built from `Names.cs` and `Signs.cs`, plus a county view on the big map with your town highlighted. Today the minimap is a texture exactly `w×h` in size, so it will need one per space.
- **Highway traffic**: the same `Traffic` class running on the highway space, sparse, driving the whole length and handed over to or despawned at the gates. If highway traffic ever needs to feel truly busy, IDM car-following and MOBIL lane changes are the standard models.
- **New missions** (later): deliveries between towns, a county-wide race, "drive a friend home".

### 4.7 Flying between towns

Planes already have most of what they need:
- They cruise at 20 tiles, above the tallest towers.
- They boost to 11.1 tiles/s, which is exactly the server's speed limit.
- They land on any open ground (`Driving.cs:6, 198`).

All that keeps them in a town is `FloorAt` returning Infinity off the map (`ExploreView.cs:1557-1561`).

- **Sky space.** Between towns a plane is in a "sky" space. It needs no tile grid: the floor is 0 (open countryside), and the edge of the county is the only limit. So `FloorAt` asks the active town while you are over it, and returns 0 between towns. Fly into another town's box and that town becomes the active space, just as at a gate.
- **Land anywhere, take off anywhere.** Landing in open country already works, so a plane can set down in a field between towns. Taking off again works from any open ground too. Do you want that, or should landing be limited to airports?
- **Build the town ahead.** A plane can come from any direction, and in a straight line, so it gets to a town sooner than the highway does. Spokes are 400 tiles either way, but between East and South (or South and West) the straight line is about 15% shorter than the rim road, and a plane can head anywhere. Each frame, cast a ray along the plane's heading. The first box it hits within about 400 tiles is the town to prepare (still one at a time). If the plane turns away, release that town.
- **When a plane arrives before the town is built:** a cloud bank around the town (the sky already draws clouds), and a headwind. The headwind is the hidden speed cap again, dressed up as weather. Fog keeps the view to 60 tiles in the close camera. The iso camera sees a little further, so the countryside ground has to run all the way out to the next town's box.
- **Flying north.** Over the private side, only *your* towns exist, as on the road. The territory grid decides which room you are in, by the same rule as for cars.
- **Private towns by air:** a plane over the north side flies into your own towns and nobody else's. Another player's loop simply isn't there for you.
- **Server:** no change to speed limits. The height cap (24) already allows cruising. The bounds check uses the territory, as cars do.

---

## 5. The budget, worked through

```
time to have B ready  = (T_release(A) + T_generate(B) + T_build(B)) / budget_per_frame × frame_time
distance needed       = that time × speed + fog distance (60) + release distance (80)
```

**Example**, with the build time still to measure:
- Say T_total = 3,000 ms of main-thread work, at a budget of 5 ms a frame at 60 fps. That is 600 frames, or **10 s**.
- At boost speed (8.84 tiles/s) that is **88 tiles**, or 265 tiles with a 3× margin for slow phones and Safari.
- Add 60 (fog) and 80 (release) and the highway needs about **400 tiles (1.6 km)**, which takes **45 s at boost** and 77 s at normal top speed.

If measurements show T is much larger (Porchlight has 1,497 houses), there are three ways to compensate:
- longer highways;
- a lower top speed on highways;
- spending more of the budget in a tunnel (at, say, 12 ms a frame for 15 s).

**Measure first.** The LoadBar already records how long each step took in the browser's localStorage (`porchlight.load.town`). Reading it from WebGL builds in Chrome, Firefox and Safari, for Porchlight and for a default 128×128 town, turns this table from guesses into a plan.

---

## 6. A suggested order

1. **Measure.** Get per-stage and per-part timings on WebGL and desktop, for Porchlight and for a default town. Also check how much memory one built town takes. *(This decides how long highways need to be.)*
2. **Background build.** Smaller iterators, an adjustable budget, teardown spread over frames, and `Prepare/Activate/Release` instead of dispose-then-build. Done on its own, this already shrinks today's loading screens and makes "Join" smoother.
3. **Compass gates.** Add the `gates=compass` mode, with gates at edge midpoints and a dry band to each. Regenerate Porchlight and rehouse its residents, then do the same for private towns.
4. **County layout code** in C# and TypeScript, with a shared test file: boxes, gates, Bézier highways and territories, built from the public keys and an account's towns by slot. Add a `slot` column on `towns`.
5. **East first.** Porchlight to the East city: `HighwayModel` as a space, overlap and active-space switching, countryside skirts and an open edge.
6. **Server territories.** Bounds come from the layout, plus the arrive-at join, eviction that allows a handover, a shared clock, and the private road room.
7. **The rest of the county.** South and West cities, the two rims with their rest stops, the north highway, the junction, the private loop and the vacant-plot signs.
8. **Planes between towns.** Sky space, `FloorAt` returning 0 between towns, the heading ray choosing which town to build, a cloud bank as the safety valve.
9. **Polish.** Signs ("East city 1.6 km"), a county map, highway traffic, the hidden speed cap, crests and a tunnel.
10. **Later.** Pre-join handoff (v2), more rings, missions between towns.

## 7. Open questions

- **How long do generating and building take on WebGL?** Measure this first: everything in §5 depends on it.
- **Does a 256×256 town plus a highway space fit in mobile browser memory?** One town at a time suggests yes, but check.
- **Do people *want* 45–60 s drives?** A fast-travel option (the current load) should stay alongside driving.
- **What happens to a town's day/night, weather and its single traffic host** when players flow in and out continuously instead of loading in?
- **Does the self-written Colyseus client open one WebSocket per room?** That matters for v2 pre-join. Test it with two rooms open at once.
- **Can planes land in open country?** Today they can, anywhere there's room. Or should landing between towns be limited to airports?
- **What are the three cities called, and how big are they?** 256×256 and denser than Porchlight, or Porchlight's size with a different character (harbour city, mountain town and so on)?
- **Is it all right for every town's gates to change once?** Porchlight's 1,497 houses would be reassigned, and private towns' residents would choose their houses again.

## Sources

- Spider-Man streaming: https://80.lv/articles/marvels-spider-man-a-technical-postmortem , https://www.gamesradar.com/marvels-spider-man-2-map-is-about-twice-the-size-of-the-original
- GTA III slowing players down: https://80.lv/articles/gta-3-developer-explained-why-he-had-to-slow-players-down/ , https://hackaday.com/2024/04/27/how-additional-aerodynamic-drag-helped-make-gta-iii-work-on-ps2
- ETS2 sectors and scale: https://github.com/sk-zk/map-docs/wiki , https://racinggames.gg/article/euro-truck-simulator-2-is-roughly-119-and-thats-why-it-works
- The Crew's streaming: https://gamedeveloper.com/design/q-a-inside-the-tools-and-techniques-used-to-build-i-the-crew-2-i-s-open-world
- Forza Horizon 2 and 30 fps: https://mp1st.com/news/forza-horizon-2-runs-1080p-30fps-xbox-one-says-creative-director
- Galin et al., Procedural Generation of Roads (2010): https://perso.liris.cnrs.fr/egalin/Articles/2010-roads.pdf ; Authoring Hierarchical Road Networks (2011): https://www.cs.purdue.edu/homes/bbenes/papers/Galin11CGF.pdf
- Parish & Müller, Procedural Modeling of Cities: https://phiresky.github.io/procedural-cities/presentation.html
- Minecraft structure placement (region spacing/separation/salt): https://learn.microsoft.com/en-us/minecraft/creator/reference/content/worldgenreference/examples/jigsawstructuresets
- Centripetal Catmull-Rom: https://cemyuksel.com/research/catmullrom_param/ ; curve radius vs speed (AASHTO tables): https://www.txdot.gov/manuals/des/rdw/chapter-4--basic-design-criteria/4-7-horizontal-alignment/4-7-5-superelevation-rate-on-rural-and-high-speed-.html
- New World hubs: https://aws.amazon.com/blogs/gametech/the-unique-architecture-behind-amazon-games-seamless-mmo-new-world ; EVE: https://highscalability.com/eve-online-architecture/
- Colyseus, several rooms per client: https://discuss.colyseus.io/post/844 ; grid-room MMO tutorial: https://docs.colyseus.io/learn/tutorial/unity/mmo ; matchmaker and seat reservations: https://docs.colyseus.io/matchmaker
- IDM: https://traffic-simulation.de/info/info_IDM.html ; MOBIL: https://arxiv.org/pdf/1012.4913
- Unity WebGL memory and GC: https://docs.unity3d.com/Manual/webgl-memory.html ; web multithreading with Burst: https://docs.unity3d.com/Manual/web-multithreading-burst.html ; InstantiateAsync: https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/object/instantiateasync ; static batching: https://docs.unity3d.com/Manual/static-batching.html ; Mesh API benchmarks: https://github.com/Unity-Technologies/MeshApiExamples

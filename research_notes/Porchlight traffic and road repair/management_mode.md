# Traffic Management Mode for Porchlight (UX, data model, architecture)

Research notes, 2026-11-08. Network caveat: `colossalorder.fi` (CS2 dev diaries) and `steamcommunity.com` (TM:PE Workshop page, Steam reviews) could not be reached from this session. They would have to be allowed in the session's network settings before the primary CS2 traffic-AI diary or Steam review threads can be read. Claims about them below come from search-result summaries and secondary coverage, and are marked as such. The TM:PE feature docs at doc.tmpe.me were not reachable either. TM:PE details come from the GitHub wiki and changelog search snippets.

Local code grounding (read directly from the repo):
- `Shared/Explore/Traffic.cs` hardcodes `CRUISE = 2.4`, `GAP = 1.6`, `CARS_PER_TILE = 1/12`, `MAX_CARS = 70`, `PATIENCE = 4`. Cars drive only "mains" (not paths, cul-de-sacs or stubs). At each junction a car picks its `next` road just before reaching it, so traffic is random wandering, not origin–destination (OD) trips. Each car stores `road`, `s`, `dir`, `speed`, `wait`. It slows for `BlockedAhead(car, obstacles)` and turns round when held up longer than `PATIENCE`.
- `RoadStroke` (`Shared/Explore/Layout.cs`) has `kind`, `from`, `to` (graph node ids), `points` and `width`. Road identity is a list index (`int road`), which is not stable if generation changes.
- `Shared/Model/Params.cs`: `GenParams` plus `ParamSpec {key,label,group,min,max,step}`. The groups are "Map", "Terrain", "Rivers", "Town & roads", "Streets", "Lots & buildings" and "Yards". This slider pattern can be reused directly for traffic parameters.
- `Shared/Explore/WorldSync.cs`: the host encodes cars as fixed-point records (`SCALE=100`, `RECORD=7` bytes) at about 10 Hz. Receivers ease toward these (`EASE`, `JUMP`, `STALE`).
- Server (`server/src/multiplayer/CityRoom.ts`, `tables.ts`): one room per town key. The host client runs traffic and townsfolk, and the room relays them. The Postgres/drizzle `towns` table has `key`, `name` and `layout` columns. `missionRecords` is keyed by `townKey` and `missionId` (best times). Commits 60bd8f3 ("Towns you make are yours alone…") and 85e89ed/a3e3842 (the marathon closes the road to cars) mean ownership and a road-closure event already exist.

---

## 1. How games expose traffic management (tools, overlays, auto-defaults; what's liked vs disliked)

### Takeaway
Players like granular, per-junction tools: TM:PE became the de facto standard for Cities: Skylines. Those tools only work when backed by (a) a readable congestion overlay, (b) route inspection ("where do these cars come from or go?"), and (c) routing that responds to congestion. The most-criticised failures are naive shortest-path routing (SimCity 2013), tool conflicts that silently deadlock traffic, and custom settings lost when the network is edited.

### Cited Findings
- TM:PE describes itself as a mod that "improves lane usage and parking, and provides tools for customising traffic management on roads, rails and junctions". Its wiki feature table lists lane connectors, lane arrows, timed traffic lights and junction restrictions. Junction-restriction sub-options include entering a blocked junction, pedestrian crossings, U-turns, turn-on-red and lane changes. Vehicle restrictions and parking restrictions are segment-level features. — [TM:PE wiki Home](https://github.com/CitiesSkylinesMods/TMPE/wiki/Home) (via search snippet)
- TM:PE features can be toggled individually, and the Maintenance page suggests disabling unused features to improve performance. — [TM:PE wiki Maintenance](https://github.com/CitiesSkylinesMods/TMPE/wiki/Maintenance)
- TM:PE's "Disable Despawn" gained per-vehicle-type filters in 11.7.0.0. A feature label groups "clear traffic", "toggle despawn" and vehicle/parked/cim despawns together, so despawning is exposed as a player-facing setting. — [TM:PE Steam changelog](https://steamcommunity.com/sharedfiles/filedetails/changelog/1637663252?p=2) (search snippet; page itself unreachable)
- Tool conflict example: lane connectors force traffic straight on while a traffic light allows a turn. The vehicle waits to turn but never can, which is a deadlock the player created without knowing it. — [Steam discussion](https://steamcommunity.com/app/255710/discussions/0/5234891274097709772/?ctp=2) (search snippet)
- The TM:PE wiki points to the separate "Broken Node Detector" mod "to find and fix traffic despawning issues and a few other game bugs". Diagnosing broken network state is a recurring pain point. — [TM:PE GitHub README](https://github.com/CitiesSkylinesMods/TMPE)
- CS2's successor mod, "Traffic" by krzychu124, provides a Priorities Tool and a Lane Connector tool (Ctrl+R; modifier keys pick the connection type: unsafe, track-only, road-only, shared-only). It stores data "inside the savegame file". Custom lane connections may be removed "when the network composition has changed", when road direction is changed with the replace tool, or when a tram upgrade is applied. Adding an intersecting road means "connections need to be created manually or simply reset". Using Road Builder on a road resets custom priorities and connections. — [krzychu124/Traffic](https://github.com/krzychu124/Traffic)
- CS2 traffic AI (per search summary of the Colossal Order dev diary): routing chooses by pathfinding cost rather than proximity. Vehicles use more lanes, move aside for emergency vehicles and adapt to congestion. There are no hard agent limits. Road maintenance and streetlights lower accident probability. — [Colossal Order diary](https://colossalorder.fi/?p=1597) (unreachable; summary only), [mp1st coverage](https://mp1st.com/news/cities-skylines-2-gameplay-deep-dive-goes-in-depth-about-new-traffic-ai-dev-diary-roadmap-released)
- CS2 Detailer's Patch #2 (Dec 2024) added 8 roads and a "Traffic Routes" feature. — [Paradox news](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/detailers-patch-2)
- CS2 road tools: a grid tool, a replace tool that changes layouts and accessories with one click, and simpler intersections. — [Paradox road-tools feature page](https://paradoxinteractive.com/games/cities-skylines-ii/features/road-tools), [PC Gamer](https://pcgamer.com/cities-skylines-2s-cool-new-road-tools-are-already-making-me-think-my-city-wont-be-a-traffic-filled-hellscape)
- CS2 traffic info view: a city-wide "Average Traffic Flow" percentage (higher is better) and roads coloured green (smooth) through orange to red. — [gameplay.tips CS2 traffic guide](https://gameplay.tips/guides/cities-skylines-ii-traffic-guide.html), [Prima Games](https://primagames.com/tips/how-to-manage-traffic-cities-skylines-2)
- CS1 Mass Transit added a Traffic Routes info view: click a road, building or station to see the trips through it. Its Junctions tab shows stop-sign and traffic-light icons at every junction. — [Paradox Mass Transit Dev Diary 1](https://forum.paradoxplaza.com/forum/threads/cities-skylines-mass-transit-dev-diary-1-more-than-just-new-public-transport-types.1005446/), [GameSkinny guide](https://www.gameskinny.com/mzcx8/cities-skylines-mass-transit-traffic-guide); TM:PE documents its own [Traffic Routes Info View](https://github.com/krzychu124/Cities-Skylines-Traffic-Manager-President-Edition/wiki/Traffic-Routes-Info-View)
- A CS1 congestion-report mod separates "speed-based" congestion from "amount-based" (volume) congestion and notes that vanilla is amount-based. "Volume is simply a report of which roads are used most heavily", not a measure of slowdown. A user also reports empty roads showing red. — search summary of [Steam Workshop comments 1651036644](https://steamcommunity.com/sharedfiles/filedetails/comments/1651036644)
- SimCity 2013 (GlassBox): agents took the shortest route. Maxis acknowledged this caused "unavoidable, illogical jams" and patched roads to become less attractive as they fill. Critics called shortest-path-to-nearest-job/home "rudimentary". SimCity 4 used a macro-level commute heuristic instead. — [AusGamers](https://www.ausgamers.com/news/read/3282101/simcity-lead-designer-reveals-upcoming-changes-to-traffic-congestion-modders-discover-offline-capabilities-and-inflated-population), [AOL/GameZone](https://www.aol.com/2013-03-15-maxis-fixing-simcity-buggy-congested-traffic.html), [BGR](https://bgr.com/?p=372284), [PC Gamer](https://www.pcgamer.com/2013/03/14/simcity-traffic-patc/), [HN thread](https://news.ycombinator.com/item?id=41449372)
- Mini Motorways: the developers spent about a year prototyping and balancing to make "traffic control fun and accessible". Roundabouts and traffic lights are scarce upgrades placed on junctions. Players are split: one user review calls them ineffective ("helpless bystander"), while Steam players say roundabouts give much smoother flow. The roundabout mechanic was drawn from New Zealand. — [Gfinity](https://www.Gfinityesports.Com/features/the-making-of-mini-motorways-steam/), [GamesHub](https://www.gameshub.com/?p=4748), [Game Developer](https://gamedeveloper.com/audio/-i-mini-motorways-i-and-the-delicate-art-of-marrying-complexity-and-minimalism), [Metacritic](https://legacy.prod.metacritic.com/game/pc/mini-motorways)
- Heatmap UX (non-game analytics): warm colours for high flow, intensity and radius sliders, an hourly time slider. Red-to-green palettes cause colour-blindness problems, so prefer blue-to-orange or purple-to-yellow, and keep overlays from hiding critical content. — [Vemco heatmaps](https://intercom.help/vemco/en/articles/15444298-maps-and-maps-2-0-tracking-heatmaps), [Amplitude heatmap guide](https://www.amplitude.com/explore/analytics/heatmaps.md)

### Inferences
- Porchlight's town scale is small (70 cars, residential streets). A Mini Motorways or Workers & Resources level of abstraction suits it better than the full TM:PE lane-level suite. The useful TM:PE primitives are: junction control type (none/yield/stop/lights/all-way), timed light phases, per-road speed limit, per-road closure or vehicle restriction, no-U-turn, and global despawn/patience. Lane arrows and lane connectors make little sense on two-lane roads.
- Pair every tool with immediate visual feedback in the Blueprint (icons at junctions, as in CS1's Junctions tab) and offer a "reset this junction" action, to avoid the CS1 deadlock-by-conflict problem.
- Expose two overlay modes, as the CS1 mod shows they differ: volume (cars/min) and congestion (actual speed / free speed). Use a colour-blind-safe ramp.
- Keep overrides attached to network identity that survives regeneration. Losing settings on network change is the main complaint about the CS2 Traffic mod.

### Gaps
- Primary CS2 traffic dev diaries and Steam review sentiment are unread because of host blocks.
- No primary sources found on Transport Fever 2 or Workers & Resources signal/traffic tools, Shapez overlays, or Planet Coaster / Two Point heatmaps. Searches returned nothing usable.
- TM:PE's full timed-traffic-light editor workflow (steps, min/max durations, flow/wait sensitivity) is not documented here because doc.tmpe.me was not fetched.

---

## 2. Layered configuration (global → zone → per-road/junction; presets; time-of-day; events)

### Takeaway
None of the sources I could reach describes an explicit multi-layer config model. TM:PE is effectively "vanilla defaults + per-node/per-segment overrides + global options", and events in CS-style games are separate systems. For Porchlight, a small, explicit cascade (Preset → Town globals → Area → Road/Junction → active Events) is the recommended inference. Each layer is a sparse override, and the effective value is resolved once when the config changes.

### Cited Findings
- TM:PE settings sit at two levels: global mod options (e.g. despawn toggles with vehicle-type filters, features enabled or disabled) and per-junction/per-segment tools (junction restrictions, timed lights, vehicle/parking restrictions). — [TM:PE wiki Home](https://github.com/CitiesSkylinesMods/TMPE/wiki/Home), [TM:PE changelog](https://steamcommunity.com/sharedfiles/filedetails/changelog/1637663252?p=2)
- CS2 vehicles move aside for emergency vehicles (built-in event behaviour, not a player override). — [mp1st on CS2 traffic AI](https://mp1st.com/news/cities-skylines-2-gameplay-deep-dive-goes-in-depth-about-new-traffic-ai-dev-diary-roadmap-released)
- SUMO's ActivityGen generates time-dependent demand from a population description (work, school, free-time activities), which gives a time-of-day profile without hand-authoring. — [SUMO Activity-based Demand Generation](https://sumo.dlr.de/docs/Demand/Activity-based_Demand_Generation.html)
- Porchlight already implements an event override: the marathon closes its course road to cars, and "no cars at all for the runner" (commits 85e89ed, a3e3842; `Explore/Course.cs`).

### Inferences
- Recommended cascade, lowest to highest priority:
  1. **Built-in defaults**: today's constants.
  2. **Preset**: "Sleepy village", "Market day", "Rush hour", "Gridlock challenge". A preset is just a named bundle of global values plus a demand curve.
  3. **Town globals**: density multiplier, max cars, cruise speed, patience, despawn-when-stuck, default junction control by road kind, and a 24-slot hourly demand profile.
  4. **Area overrides** (optional, later): a polygon or a set of road ids, e.g. "school zone: 60% speed 08–09 and 15–16".
  5. **Road/junction overrides**: speed, closed, one-way, no-trucks; junction control and light timing.
  6. **Events**: time-boxed overrides that win over everything (road closures, parades, the marathon) plus pre-emption (an emergency vehicle sets green on its route and nearby cars pull over).
- Resolve into a flat `ResolvedTraffic` (arrays indexed by road and node) on config change, not per tick, so the sim's hot loop stays as cheap as the constants it replaces.
- Time-of-day: the town already has night/day (`CityRoom.ts` keeps day/night per town). Index the demand profile by that clock. Target car count = `maxCars × density × profile[hour]`, and spawn or despawn smoothly toward it.

### Gaps
- No game source found that documents a district-level traffic policy layer (CS1/CS2 have district *policies*, e.g. heavy-traffic bans, but I did not fetch documentation).

---

## 3. Data model & persistence (stable IDs on procedural graph, size, server storage, permissions)

### Takeaway
The main data-model risk, shown by the CS2 Traffic mod's limitations, is overrides keyed to volatile network ids being silently lost. Porchlight towns are deterministic from seed and params, so overrides can be keyed by geometric identity: quantised node positions for junctions, and an unordered pair of endpoint hashes for roads. Store them as a small versioned JSON blob per town on the server, editable only by the owner, and report orphaned overrides when a regeneration invalidates them.

### Cited Findings
- The CS2 Traffic mod stores data in the savegame and loses it when "the network composition has changed", on road replacement, or on Road Builder edits. — [krzychu124/Traffic](https://github.com/krzychu124/Traffic)
- Porchlight `RoadStroke` exposes `from`/`to` graph node ids and `points` whose endpoints sit "exactly on the graph nodes". Cars refer to roads by list index. — `Shared/Explore/Layout.cs`, `Shared/Explore/Traffic.cs` (local)
- The server already keeps per-town rows: `towns(key unique, name, layout text)` and `missionRecords(townKey, missionId, …)` in drizzle/Postgres. Towns belong to the player who made them (commit 60bd8f3). — `server/src/multiplayer/tables.ts` (local)

### Inferences
- **Stable IDs**:
  - `JunctionKey = hash(round(x*4), round(y*4))` from the node position in tiles, quantised to 0.25 tiles.
  - `RoadKey = hash(min(JA,JB), max(JA,JB), kind)`, plus a midpoint hash to tell apart parallel roads between the same junctions.
  - Direction-specific settings (one-way) store the `fromJunction` key.
- On load, build `Dictionary<ulong,int>` maps from key to the current index.
- Allow fuzzy matching for unmatched keys: the nearest node within 1 tile with the same degree. Overrides that still don't match are marked "orphaned" and shown in the UI rather than dropped silently, so the owner sees what broke when they change generator params.
- Version the blob with `schema` and also store the `GenParams` hash it was authored against. If the hash matches, keys match exactly.
- **Size**: a town might have about 50–300 junctions and roads, with typically a few dozen overrides. A sparse JSON list (`{"j":"9f3a…","ctl":"lights","phases":[…]}`) is a few KB at most. Use a `jsonb` column `traffic` on `towns`, or a `town_traffic(town_key PK, config jsonb, version int, updated_at)` table. Optimistic concurrency via `version`.
- **Permissions**: the server validates that the editor's account owns the town (same check that makes towns "yours alone"). It clamps every value against the same `ParamSpec` min/max used client-side and caps the override count. Non-owners may preview locally ("sandbox") but cannot save. An optional later step lets owners grant "traffic warden" rights to co-owners.

### Gaps
- No external source found on stable ID schemes for procedural road graphs in shipped games; the hashing scheme is my design, not sourced.

---

## 4. Simulation architecture: policy vs agent layers, demand, routing, LOD, metrics

### Takeaway
Separate (1) a **policy/config layer** (resolved rules: speeds, closures, junction controls, light phase clocks, demand curve), (2) a **demand layer** (spawning cars with trips), (3) a **routing layer** (congestion-aware A* over the road graph), (4) the existing **agent layer** (car following on `RoadStroke`s), and (5) a **metrics layer**. Moving from random wandering to OD trips between houses, shops and work sites is what makes management meaningful: with pure wandering, a closure only reshuffles randomness. Routing must include congestion cost (SimCity 2013's lesson). Off-screen meso LOD is optional at 70 cars but cheap to add later.

### Cited Findings
- Shortest-path routing without congestion feedback caused "illogical jams" in SimCity 2013. Maxis's fix was to make filling roads less attractive. — [AusGamers](https://www.ausgamers.com/news/read/3282101/simcity-lead-designer-reveals-upcoming-changes-to-traffic-congestion-modders-discover-offline-capabilities-and-inflated-population)
- CS2 routes by a pathfinding cost rather than straight-line proximity and adapts to congestion. — [mp1st](https://mp1st.com/news/cities-skylines-2-gameplay-deep-dive-goes-in-depth-about-new-traffic-ai-dev-diary-roadmap-released)
- SUMO demand families: random/heuristic (randomTrips), OD-matrix (od2trips) and activity-based (ActivityGen: work, school, free time from a population description). The pipeline is ActivityGen → duarouter → SUMO. — [SUMO Intro to demand modelling](https://sumo.dlr.de/docs/Demand/Introduction_to_demand_modelling_in_SUMO.html), [SUMO ActivityGen](https://sumo.dlr.de/docs/Demand/Activity-based_Demand_Generation.html)
- Research pipelines build activity chains upstream (Eqasim/MATSim), then microsimulate in SUMO. — [SUMO preprint](https://sumo.dlr.de/preprints/pre-print-3322.pdf)
- Mesoscopic models keep individual vehicles but use speed–density or queue dynamics, and resolve lanes only when queues form. Event-based models (MEZZO) update vehicles only on link entry and exit. Hybrid micro-in-area / meso-elsewhere is an established pattern. One vendor claims meso runs 30–60× faster than micro. — [KTH discrete-event mesoscopic paper](https://kth.se/polopoly_fs/1.742066.1600689246!/a%20discrete%20event%20mesoscopic.pdf), [KTH Mezzo](https://www.kth.se/ctr/research/model-development/mezzo-mesoscopic-traffic-simulator-1.726113), [PTV Vissim blog](https://blog.ptvgroup.com/en/technologyplus/mesoscopic-and-hybrid-simulations-in-ptv-vissim/) (vendor claim)
- Level-of-detail by distance from a focal vehicle (macro/meso/micro tiers) has been proposed in traffic research. — [BME traffic](https://traffic.bme.hu/?p=557)
- CS1 vanilla reports volume. Speed-based congestion is a different, more meaningful measure. — [Steam Workshop comments 1651036644](https://steamcommunity.com/sharedfiles/filedetails/comments/1651036644) (search summary)

### Inferences
- **Demand**: a lightweight ActivityGen-style generator. Origins are houses (already on the maps); destinations are shops, workplaces, school, park, plus "through traffic" from town-edge roads. Trip purpose times the hourly profile gives the spawn rate. Cars spawn at a driveway or edge and despawn at the destination (parking abstracted away). Keep "wander" as a fallback and as the "Sleepy village" preset feel.
- **Routing**: A* over the junction graph (small, so cache all-pairs or per-destination trees). Cost = length / effective speed × (1 + α·congestion) + junction penalty (stop = +2 s, light = expected wait) + closure = ∞. Reroute periodically or when the next road becomes closed. Because cars already choose `next` near each junction, routing only replaces how `next` is picked. The car-following code stays.
- **Junction control** (new): a per-node controller. Uncontrolled means priority to the major road by width or kind. Stop/yield means a gap check. Lights means a phase clock derived from the shared sim time, so peers agree without syncing light state. All-way stop is a FIFO.
- **LOD**: with MAX_CARS at about 70, full micro everywhere is affordable. If density sliders allow several hundred cars, run micro within N tiles of any player and queue/meso (per-road FIFO with travel time = length/speed(density)) elsewhere. The host is the only one simulating, so LOD can use all players' positions.
- **Metrics** (computed by the host, sent at about 1 Hz): mean trip delay (actual vs free-flow time), throughput per junction (cars/min), stops per trip, mean speed per road (congestion ratio), longest queue, stuck/despawn count, pedestrian wait at crossings. Show these as overlays plus a small scorecard ("Traffic flow 87%", as CS2 does).

### Gaps
- No primary game-dev sources (GDC talks or postmortems) were found on traffic sim architecture in city builders; architecture recommendations are inferred from SUMO/meso literature and the SimCity and CS behaviour above.

---

## 5. Multiplayer / determinism implications

### Takeaway
Keep Porchlight's existing model: one authoritative simulator (the host client) and others interpolating. Treat traffic config as authoritative server-side state owned by the town owner. The server validates, persists and broadcasts config with a version number. The host applies it at the next tick, and clients use it only for drawing (icons, overlays, light colours).

### Cited Findings
- Porchlight already has the host client run traffic and townsfolk and send them about 10×/s through the room. Clients ease toward the snapshots. — `server/src/multiplayer/CityRoom.ts`, `Shared/Explore/WorldSync.cs` (local)
- Server-side per-town records and ownership exist (best times, owned towns). — `server/src/multiplayer/tables.ts`, commit 60bd8f3 (local)

### Inferences
- Flow: the owner edits in the Blueprint and sends `traffic:patch {baseVersion, ops[]}`. The server checks ownership, clamps values and bumps the version, then persists the result and broadcasts `traffic:config {version, blob}` (or the patch) to the room. The host applies it at the next tick and everyone redraws. Late joiners get the full blob on join.
- Light phases: derive them from `(serverTime + offset) mod cycle`, so no per-tick state needs syncing. Cars are already snapped from the host.
- Host migration: config lives on the server, so a new host just rebuilds `ResolvedTraffic`. Car positions resume from the last snapshot, as today.
- Events (parade, marathon) become server-scheduled override entries with start and end times. That generalises the marathon's road closure, and players who join mid-event see the same closures.
- Sandbox preview for non-owners: apply config locally only when playing solo, or show a "ghost" overlay without sending it.

### Gaps
- No external sources consulted on deterministic lockstep vs host authority for traffic; the recommendations follow from Porchlight's existing architecture.

---

## 6. Concrete recommended design for Porchlight

### Takeaway
Ship an MVP that turns the constants into a `TrafficConfig` with presets and Sidebar sliders, adds a Blueprint "Traffic" layer with a congestion overlay and click-to-edit roads and junctions (close, speed, control type), and persists owner-only config on the server. Next come OD demand with congestion-aware routing, timed lights, events, and metrics.

### Cited Findings
- The Sidebar slider pattern (`ParamSpec` min/max/step/group) and the Blueprint layered map with pan and zoom already exist. — `Shared/Model/Params.cs`, `Game/UI/Blueprint.cs` (local)
- The most valued overlays are congestion colouring plus route inspection (CS1 Traffic Routes, CS2 traffic flow %). — [Paradox Mass Transit diary](https://forum.paradoxplaza.com/forum/threads/cities-skylines-mass-transit-dev-diary-1-more-than-just-new-public-transport-types.1005446/), [gameplay.tips](https://gameplay.tips/guides/cities-skylines-ii-traffic-guide.html)

### Inferences
**Types (Shared, engine-free, JSON via `Core/Json.cs`):**
```csharp
public sealed class TrafficConfig {            // persisted, sparse
    public int schema = 1; public string paramsHash;
    public string preset = "default";          // "sleepy","market","rush","gridlock"
    public TrafficGlobals globals = new();     // null fields = inherit preset
    public List<RoadRule> roads = new();       // keyed by RoadKey
    public List<JunctionRule> junctions = new();
    public List<TrafficEvent> events = new();
}
public sealed class TrafficGlobals {
    public double? density, cruise, patience; public int? maxCars;
    public bool? despawnStuck; public double[] hourly; // 24 entries, 0..2
    public JunctionControl? defaultMinor, defaultMajor;
}
public sealed class RoadRule { public ulong key; public double? speed; public bool? closed;
    public ulong? oneWayFrom; public bool? noTrucks; }
public enum JunctionControl { Auto, Uncontrolled, Yield, Stop, AllWayStop, Lights, Roundabout }
public sealed class JunctionRule { public ulong key; public JunctionControl ctl;
    public List<Phase> phases; public double offset; }   // Phase{ulong[] greenRoads; double secs;}
public sealed class TrafficEvent { public string kind; // closure, parade, emergency, marathon
    public double start, end; public ulong[] roads; public bool pedestriansOnly; }
public sealed class ResolvedTraffic { // built on change; arrays by current road/node index
    public double[] speed; public bool[] closed; public int[] oneWay; public JunctionControl[] ctl; ... }
```
- `Traffic` takes `ResolvedTraffic` instead of the constants. `TrafficPresets` holds the bundles. Traffic slider specs are added as a "Traffic" `ParamSpec` group (density 0–2 step 0.1, max cars 0–200, cruise 1.5–3.5, patience 1–10).

**Management view (Blueprint "Traffic" mode):**
- Layers: road congestion (speed ratio, colour-blind-safe ramp), volume, junction-control icons, closures/events (hatched), and selected-car route (polyline to its destination).
- Tools (toolbar, one active at a time):
  1. Select/inspect: a road or junction card with live stats and overrides, with "Reset to auto".
  2. Close road / one-way, by click or drag along a road.
  3. Speed brush.
  4. Junction control cycle: click to cycle Auto → Stop → Lights → Roundabout.
  5. Light timing editor: a phase list with seconds, preview animation and an "auto-time from volume" button.
  6. Event planner: draw a route, pick a time window and type. The marathon becomes one of these.
- A time scrubber or hour dial previews the demand curve, and a scorecard shows flow %, mean delay and stuck cars.
- Validation warnings in the CS1-conflict spirit: "Junction X: all exits closed", "Road Y unreachable", "Orphaned overrides after regeneration (3)".

**MVP roadmap:**
1. **M1 Config plumbing**: `TrafficConfig`, presets, resolved arrays, Sidebar Traffic group (globals only), local save. The sim reads config instead of constants. Determinism tests in Shared.
2. **M2 Blueprint traffic layer**: congestion and volume overlay from host metrics, road close/speed and junction Stop/Lights (fixed default timing). Stable keys and orphan reporting.
3. **M3 Server persistence & authority**: `town_traffic` jsonb with version, owner-only patch messages in `CityRoom`, broadcast, and full blob on join.
4. **M4 Demand & routing**: house→shop/work/edge trips, hourly profile, congestion-aware A*, despawn at destination. "Wander" remains as a preset.
5. **M5 Lights editor & events**: phase editor, server-scheduled events (port the marathon closure), emergency pre-emption (green wave plus pull over).
6. **M6 Metrics & goals**: scorecard, per-junction stats, optional challenges ("keep mean delay under 10 s at rush hour"), and meso LOD if car caps rise.

### Gaps
- Not validated against Blueprint.cs internals (only file size and role skimmed); the overlay hook points need confirming in code.
- Pedestrian crossing interaction (walkers vs cars) is not covered by sources here.

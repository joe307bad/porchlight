# Traffic signal control and intersection modelling for Porchlight

Research method note: this session's network only allowed `raw.githubusercontent.com` plus web search. sumo.dlr.de, ops.fhwa.dot.gov, mutcd.fhwa.dot.gov, cityflow.readthedocs.io, traffic-simulation.de, wikipedia and gdcvault were blocked. SUMO documentation was therefore read from its Markdown source in the eclipse-sumo/sumo GitHub repo (`docs/web/docs/...`), which is the source of sumo.dlr.de. Citations give the public sumo.dlr.de URL for each page. SUMO, CityFlow and TM:PE source code was read directly from GitHub (main/master branches as of 2026-11-08). The US engineering numbers (MUTCD/ITE/FHWA) come from search results that quote state DOT and agency documents, because the FHWA pages could not be fetched. Those numbers are marked as such.

Codebase context, read from `/home/user/porchlight/unity/Assets/Porchlight/Runtime/Shared/Explore/Traffic.cs`:
- Constants: `CRUISE = 2.4` tiles/s, `GAP = 1.6` tiles, `CORNER = 0.9` tiles, `PATIENCE = 4` s, `MAX_CARS = 70`.
- Speed eases toward its target with `speed += (target-speed)*min(1, dt*3)`. That is a first-order lag with a time constant of about 0.33 s, so braking is very sharp.
- `GapAhead` only looks at cars on the same road going the same direction.
- At a junction, `PickNext` picks a random road from `byNode[node]`, excluding the road it came from and stubs.
- `Place` draws a quadratic Bézier from `reach` before the end of the incoming lane, through the midpoint of the lane ends, to `reach` past the start of the outgoing lane.
- A car that has been blocked for more than `PATIENCE` seconds reverses direction, which is the current deadlock escape.
- Lane offset is `(r.width/4)*side*dir`: one lane per direction, at a quarter of the road width.
- `RoadStroke` has `kind` (Main/Street/Path), `from`/`to` node ids, `points` and `width`. Nothing describes junction geometry.

---

## 1. Junction data model: connectors/movements, conflict points, foe matrices, stop lines, internal lanes

### Takeaway
The standard model, used by both SUMO and CityFlow, is: incoming lane → **connection (link/movement)** → outgoing lane. Each connection has an internal path through the junction box. Each pair of connections is pre-classified as compatible, conflicting with a priority order (`response`), or conflicting with no priority, and the result is stored as per-junction bitsets. Signals then index connections, not lanes. For Porchlight (one lane per direction, at most about 5 legs, so at most about 20 movements), a precomputed `ulong` foe mask per movement plus a geometric crossing point on each movement's Bézier is cheap and enough.

### Cited Findings
- **SUMO links rather than lanes:** "a signal does not control lanes, but links - each connecting a lane which is incoming into a junction and one which is outgoing from this junction." Each signal is one character in the phase `state` string, indexed by the connection's `linkIndex`. — [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)
- **SUMO's per-junction right-of-way matrix:** it is written as `<request index response foes cont>`.
  - `response` is a bitstring of the links that "prohibit un-decelerated passing" for this link, i.e. the links it must yield to.
  - `foes` is "a super-set of `response`, adding streams that have a lower priority". It lists every link it geometrically conflicts with.
  - `cont` says whether "a vehicle may pass the first stop line to wait within the intersection". This is typically the left turn from the priority direction.
  - Bitstrings are read right to left: bit 0 is link 0.
  - Link indices are ordered by incoming edge angle starting at 0° (up), then lane (right-most first), then direction (right-most first).
  - Source: [SUMO Road Networks: Requests](https://sumo.dlr.de/docs/Networks/SUMO_Road_Networks.html#requests)
- **How netconvert computes the matrix:** in `NBRequest::buildBitfieldLogic()`, for every (incoming, outgoing) pair, `computeRight/LeftOutgoingLinkCrossings` walk the edges around the junction clockwise and counter-clockwise. A link from A to B conflicts with every link whose source lies strictly between A and B on one side and whose target lies on the other side; this is a purely topological crossing test on the cyclic order of legs. Each conflicting pair is passed to `setBlocking()`, which decides who yields, in this order:
  1. At an `allway_stop` junction, both links forbid each other ("Conflict resolution happens via arrival time").
  2. A U-turn always yields.
  3. The link from the edge with the higher junction priority wins.
  4. At equal priority, a straight movement beats a turning one, unless the junction is a "bent priority" junction.
  5. Otherwise, the right-before-left rule decides by walking clockwise from one source to see whether the other link's target comes first.
  - Source: [NBRequest.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBRequest.cpp)
- **Movement classes come from angles:** `NBNode::getDirection()` classifies a movement from the relative angle between incoming and outgoing edges:
  - |angle| < 44° is straight (or partleft/partright if another exit is straighter).
  - More than 90° right is right; more than 90° left is left.
  - Less than −170° on reversed geometry is a turnaround.
  - Source: [NBNode.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBNode.cpp)
- **Internal lanes and internal junctions:** "each connection will typically correspond to exactly one internal lane". Internal junctions split a connection where drivers wait inside the box, typically "left-turning vehicles that yield to oncoming traffic" and "right-turning vehicles that yield to pedestrian crossings". — [SUMO Road Networks](https://sumo.dlr.de/docs/Networks/SUMO_Road_Networks.html)
  - Without internal links, vehicles "will appear instantly on the other side of the intersection after passing the stop line. They cannot block the intersection, wait within the intersection for left turns nor collide." — [SUMO Intersections](https://sumo.dlr.de/docs/Simulation/Intersections.html)
- **Stop line placement:** "Vehicles from a minor road wait exactly at the end of the lane… Vehicles waiting at a traffic light wait with an offset of 1m ahead of the lane end." The lane ends where the junction shape starts, and `endOffset`/`stopOffset` can move it. — [SUMO Intersections](https://sumo.dlr.de/docs/Simulation/Intersections.html)
- **Turn speed:** speed on internal lanes is capped at `sqrt(radius * factor)`, with factor 5.5 by default (`--junctions.limit-turn-speed`), and never above the average of the source and destination lane speeds. — [SUMO Intersections](https://sumo.dlr.de/docs/Simulation/Intersections.html)
- **Approach behaviour:** a vehicle that must yield "prepares to brake until reaching a specific distance (`visibilityDistance`, default 4.5 m)". At that point it checks for higher-priority foes and either brakes or crosses. — [SUMO Intersections](https://sumo.dlr.de/docs/Simulation/Intersections.html); [PlainXML connection attributes](https://sumo.dlr.de/docs/Networks/PlainXML.html)
- **CityFlow's simpler model:**
  - An intersection has `roadLinks` (type `go_straight` / `turn_left` / `turn_right`), each made of `laneLinks` with polyline `points`. A signal phase is just `{time, availableRoadLinks[]}`. — [CityFlow roadnet.rst](https://github.com/cityflow-project/CityFlow/blob/master/docs/source/roadnet.rst)
  - Conflict points are found geometrically: `Intersection::initCrosses()` intersects every pair of lane-link polylines segment by segment and stores a `Cross` with the distance along each lane link and a "safe distance" computed from lane widths and crossing angle. The crosses on each lane link are sorted by distance. — [CityFlow roadnet.cpp](https://github.com/cityflow-project/CityFlow/blob/master/src/roadnet/roadnet.cpp)
  - `Cross::canPass()` gives way by movement priority (enum `go_straight=3 > turn_left=2 > turn_right=1`). Between equals it compares the steps each vehicle needs to reach the crossing, then the time it entered the lane link, then distance. Any vehicle that "cannot yield", i.e. cannot stop in time, goes. — [CityFlow roadnet.cpp](https://github.com/cityflow-project/CityFlow/blob/master/src/roadnet/roadnet.cpp), [roadnet.h](https://github.com/cityflow-project/CityFlow/blob/master/src/roadnet/roadnet.h)

### Inferences
- Porchlight has one lane per direction, so a "connection" is just (incoming road end, outgoing road end). A k-leg junction has at most k·(k−1) movements, plus k U-turns if they are allowed. A 5-leg junction therefore has at most 20 movements, so a `ulong` foe mask and a `ulong` yield (`response`) mask per movement fit easily.
- Two foe tests are possible:
  - SUMO's topological test, based on the cyclic order of leg angles. It is cheap and robust to odd geometry.
  - CityFlow's geometric test, which intersects the actual Bézier curves Traffic.cs already draws.
  - Recommendation: use the topological test for the foe/yield bits and the geometric one to get *where* the conflict point lies on each curve, so cars can keep going until just before it.
  - Merges (two movements into the same outgoing lane) must also count as foes. In SUMO they are conflicts with the same target edge (see `mergeConflict` in NBRequest.cpp).
- The stop line should sit at the start of the corner curve, i.e. `reach` (≤ `CORNER` = 0.9 tiles) before the node along the incoming road. Traffic.cs already defines that point, so "stop line" = `s` where `rest == Reach(road, next)`. Moving it back by about half the cross road's width (0.5 tiles for a Street, 1 tile for a Main) would keep queued cars out of the crossing traffic's curves.
- Near-coincident junctions (two nodes a fraction of a tile apart) should be merged into one logical junction for control purposes, as SUMO's `--junctions.join` does (default join distance 10 m) and `--tls.join` does for signals within 20 m. — [netconvert options](https://sumo.dlr.de/docs/netconvert.html)

### Gaps
- I did not find a published, simple analytic description of SUMO's `cont`/internal-junction placement beyond the docs and source. The rule above comes from reading the source.

---

## 2. Right-of-way without signals: priority/minor roads, stop/yield, all-way stops, right-before-left, and choosing among them

### Takeaway
SUMO's default heuristic fits a procedural town well:
- If all legs have equal class and low speed, use **right-before-left**.
- If any leg differs in class or speed, use **priority**: the two most important legs are major and the rest yield, with stop signs on minor legs if you want `priority_stop`.
- **All-way stop** is first-come-first-served by arrival time.

In US-flavoured towns, the realistic default for Street×Street is a 2-way or all-way stop, and for Main×Street it is a stop sign on the Street leg.

### Cited Findings
- **SUMO node types:**
  - `priority`: "Vehicles on a low-priority edge have to wait until vehicles on a high-priority edge have passed".
  - `priority_stop`: "vehicles on minor links always have to stop before passing".
  - `right_before_left`: "Vehicles yield to traffic coming from the right".
  - `allway_stop`.
  - `traffic_light`, `traffic_light_right_on_red`, `zipper`, `unregulated`, `dead_end`.
  - Source: [SUMO PlainXML: Node types](https://sumo.dlr.de/docs/Networks/PlainXML.html#node_types)
- **How the major legs are chosen:** `rightOfWay="default'` "sorts edges according to attributes *priority*, *speed* and *laneNumber*. The two incoming edges with the highest position are determined and will receive right-of-way. All other edges will be classified as minor." To model a turning priority road, all its edges must carry the higher priority. — [SUMO PlainXML: Right-of-way](https://sumo.dlr.de/docs/Networks/PlainXML.html#right-of-way)
- **How netconvert guesses the type:** `NBNodeTypeComputer::computeNodeTypes`:
  - A node with one incoming edge, or a simple continuation, becomes `priority` (no real junction).
  - Otherwise it starts as `right_before_left`. It switches to `priority` if any pair of non-opposite incoming edges differs in speed by more than 9.5 km/h, or differs in edge priority, or has max speed ≥ `junctions.right-before-left.speed-threshold`. That threshold defaults to 13.61 m/s (49 km/h).
  - A source comment reads "@todo figure out when PRIORITY_STOP is appropriate".
  - Sources: [NBAlgorithms.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBAlgorithms.cpp); [netconvert options](https://sumo.dlr.de/docs/netconvert.html)
- **Link states in SUMO's matrix:** a stop-controlled minor link is `LINKSTATE_STOP`, otherwise `LINKSTATE_MINOR`. — [NBNode.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBNode.cpp)
- **Driver parameters:**
  - `jmStopSignWait`: the minimum stop duration at a stop sign, default one sim step.
  - `jmTimegapMinor`: the minimum time gap when passing ahead of or behind a prioritized vehicle, default 1 s.
  - `impatience`: grows with waiting time (`--time-to-impatience`, default 180 s) and lets drivers force priority vehicles to brake.
  - Source: [SUMO vehicle type junction model params](https://sumo.dlr.de/docs/Definition_of_Vehicles,_Vehicle_Types,_and_Routes.html#junction_model_parameters)
- **TM:PE (Cities: Skylines):**
  - Priority signs are Main, Yield and Stop per segment end. `TrafficPriorityManager.HasPriority()` treats an unsigned segment as Main.
  - It iterates over the up to 8 segments at the node, estimates time-to-node from distance/speed, and checks each approaching vehicle for conflict using lane order and turn direction (`IsLaneOrderConflictFree`, `HasPriorityOnSameLevel`).
  - A foe whose state is Approach/Leave but which is not moving is reclassified as `Blocked`.
  - Source: [TrafficPriorityManager.cs](https://github.com/CitiesSkylinesMods/TMPE/blob/master/TLM/TLM/Manager/Impl/TrafficPriorityManager.cs)
- **All-way stop in SUMO:** every conflicting pair forbids each other and "conflict resolution happens via arrival time". — [NBRequest.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBRequest.cpp)
- **US MUTCD multiway-stop volume warrant**, as quoted by agency documents:
  - At least 300 vehicles/h entering from the major street for any 8 hours, plus at least 200 units/h (vehicles, pedestrians, bikes) from the minor street, with at least 30 s average delay in the peak hour.
  - The thresholds drop to 70% if the major-street 85th-percentile speed exceeds 40 mph.
  - Sources: [Iowa State InTrans TSIS FAQ 20](https://www.intrans.iastate.edu/wp-content/uploads/2019/11/TSIS_faq20l.pdf); [Prince George's County multiway stop fact sheet](https://princegeorgescountymd.gov/sites/default/files/media-document/dcv33825_factsheet_multiway-stops.pdf); [PennDOT LTAP TS 213](https://gis.penndot.pa.gov/BPR_PDF_FILES/Documents/LTAP/TechSheets/TS_213_Multiway_Stop_Control.pdf)
- **CityFlow's unsignalised resolution:** by movement type priority, then estimated arrival steps, then entry time, then distance, then vehicle priority. This works as a cheap generic "who goes first" comparator. — [CityFlow roadnet.cpp](https://github.com/cityflow-project/CityFlow/blob/master/src/roadnet/roadnet.cpp)

### Inferences
Suggested policy for Porchlight, to be computed once per junction at generation time from road kinds:
- **Dead end / cul-de-sac / one-leg node:** no control. Keep the existing U-turn code.
- **2 legs (a bend):** no control (SUMO treats it as a continuation).
- **Main×Main (3–5 legs):** signal, or all-way stop in small towns. Any junction with ≥ 2 Main legs plus extra legs is the natural candidate for a light.
- **Main×Street:** priority junction. Main legs are major, Street legs get a stop sign. A Main that bends at a T stays the priority road, which is SUMO's "turning priority road" case.
- **Street×Street:**
  - US feel: an all-way stop at 4-leg junctions, and a stop on the stem of a T.
  - European feel: right-before-left.
  - Make it a per-town seed choice so the town has character.
- The FCFS queue for all-way stops can be deterministic: record `arrivalTick` when a car comes to a stop at the stop line. A car may go when it has the lowest `(arrivalTick, legIndex)` among stopped cars whose movements are foes of its own, and no foe is inside the box. Non-foe movements, such as opposite through movements, may go together.

### Gaps
- No primary source was found for the formal US/European legal rules beyond the agency summaries above (MUTCD Part 2B could not be fetched).

---

## 3. Signal control: phases and rings, fixed-time (Webster), timing values, actuation, pedestrians, protected/permitted lefts, coordination, night flash

### Takeaway
A traffic light is a timed (or detector-extended) sequence of **phases**. Each phase assigns every movement one of {protected green `G`, permitted green `g` = go but yield to foes, yellow, red}. Every green is followed by yellow (about 3–5 s) and optionally all-red (about 1–2 s). Isolated fixed-time cycles are sized with Webster's formula, which typically gives 60–120 s in the real world. Actuated control extends green between `minDur` and `maxDur` while detectors see gaps shorter than `max-gap`. NEMA dual-ring is the US way to describe the same thing per movement. Late night, US signals often flash yellow on the major road and red on the minor.

### Cited Findings
**Phase/state representation (SUMO):**
- The state letters are:
  - `r` red.
  - `y` yellow: decelerate if far away, otherwise pass.
  - `g` green without priority: "may pass the junction if no vehicle uses a higher prioritized foe stream".
  - `G` green with priority.
  - `s` green right-turn arrow that requires stopping, for right-on-red.
  - `u` red+yellow.
  - `o` off-blinking: yield.
  - `O` off: priority.
  - Source: [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)
- "In SUMO a new phase is introduced whenever at least one signal changes its state", unlike engineering diagrams, which separate phases from transitions. — [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)
- Example default 4-leg static program (from the docs):
  - 31 s `GGgg…`
  - 5 s yellow
  - 6 s protected left
  - 5 s yellow
  - The same four phases for the cross street.
  - Source: [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)

**NEMA dual-ring:**
- "A NEMA phase is defined by a certain flow movement". Left turns are usually odd-numbered and through/right movements even-numbered; phases 2 and 6 are usually the main street.
- "Only one phase from a ring can be activated at a time. Phases from different rings could be activated together as long as they are not from the different sides of a barrier." The allowed pairs are 1+5, 1+6, 2+6, 3+7, 3+8 and 4+8, and 2+5 if phase 1 gaps out early.
- SUMO's NEMA example uses ring1 = 1,2,3,4, ring2 = 5,6,7,8, barriers at 2,6 and 4,8, with per-phase `minDur`, `maxDur`, `vehext=2`, `yellow=3`, `red=2`.
- NEMA lights default to all-way-stop behaviour when switched off.
- Sources: [SUMO NEMA](https://sumo.dlr.de/docs/Simulation/NEMA.html); [SUMO Traffic Lights: off](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)

**Fixed-time sizing (Webster):**
- Webster's optimum cycle is C₀ = (1.5L + 5)/(1 − Y), where L is the total lost time per cycle (sum of intergreens, or about 4–5 s per phase) and Y is the sum of the critical flow ratios v/s. Round up to a multiple of 5 s. As Y → 0, C₀ → 31 s for the Idaho example's lost times. The formula blows up as saturation approaches 1. — [U. Idaho NIATT signal timing lab manual](https://www.webpages.uidaho.edu/niatt_labmanual/Chapters/signaltimingdesign/theoryandconcepts/CycleLengthDetermination.htm)
- SUMO's `tlsCycleAdaptation.py` applies Webster with a default saturation headway of 2 s (1800 veh/lane/h). — [SUMO tls tools](https://sumo.dlr.de/docs/Tools/tls.html)
- The FHWA Signal Timing Manual (as summarised in search results) says isolated cycle lengths are generally computed with Webster's equation, and system cycle length is set by the longest minimum cycle in the coordinated group. Las Vegas reports 120–180 s system cycles. — [Las Vegas Traffic Signal Timing fact sheet](https://files.lasvegasnevada.gov/public-works/Traffic%20Engineering/Traffic%20Signal%20Timing.pdf); [FHWA STM ch. 6](https://ops.fhwa.dot.gov/publications/fhwahop08024/chapter6.htm) (not fetched)
- I could not verify a "typical 60–120 s" figure in a primary source. SUMO's default of 90 s sits inside that range.

**Clearance intervals:**
- ITE kinematic yellow: Y = t + v/(2a + 2Gg), with t = 1 s perception-reaction, a = 10 ft/s², g = 32.2 ft/s² and G = grade. Florida uses t = 1.4 s.
- Red clearance: R = (W + L)/v, where W is the stop line to far-side clearance point and L is vehicle length, about 20 ft.
- Maryland bounds yellow at 3.5–6.0 s. New Jersey's shortcut is mph/10.
- Sources: [NJDOT appendix](https://www.nj.gov/transportation/about/publicat/lmreports/pdf/rlrappendix11.2011.pdf); [ITE change & clearance intervals](https://ite.org/technical-resources/topics/traffic-engineering/traffic-signal-change-and-clearance-intervals); [Baltimore Metro Council summary](https://www.baltometro.org/sites/default/files/bmc_documents/committee/presentations/tss/TSS200617pres_Summary-Yellow-Change-and-AR-Clearance.pdf)
- SUMO's automatic yellow (`computeBrakingTime`) follows German rules:
  - Up to 50 km/h: 3 s. 60 km/h: 4 s. 70 km/h: 5 s.
  - Above 71 km/h: `1.8 + v/(2·minDecel)`, with minDecel = 3 m/s².
  - Source: [NBTrafficLightDefinition.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBTrafficLightDefinition.cpp)
- SUMO builds no all-red by default (`--tls.allred.time` defaults to 0). When it is enabled, it can compute an "escape time" from crossing length and speed. — [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html); [NBOwnTLDef.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBOwnTLDef.cpp)

**Minimum green:**
- FHWA guidance says phases generally shouldn't be shorter than about 5–10 s. A rule of thumb is min green = 5 + 2n, where n is the number of vehicles stored between the stop line and the detector. — [FHWA signal timing (search summary)](https://ops-dr.fhwa.dot.gov/Publications/signal_timing/04.htm)
- SUMO's generated min duration is `round(v_kmh/6 − 3.3)` s, about 5 s at 50 km/h and 10 s at 80 km/h, floored at `--tls.min-dur` (default 5). The default actuated max is 50 s. — [NBOwnTLDef.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBOwnTLDef.cpp); [netconvert options](https://sumo.dlr.de/docs/netconvert.html)

**Actuated (gap-based) control in SUMO:**
- "prolonging traffic phases whenever a continuous stream of traffic is detected. It switches to the next phase after detecting a sufficient time gap".
- Parameters and defaults:
  - `max-gap = 3.0` s.
  - `detector-gap = 2.0` s: the detector sits about 2 s of travel upstream of the stop line.
  - `passing-time = 2.0` s.
  - `jam-threshold`: ignore detectors that have been occupied too long.
- Phase `next` lists allow phase skipping. `earlyTarget` conditions allow custom rules. `earliestEnd`/`latestEnd` with `coordinated=true` tie actuated phases to a fixed cycle.
- Source: [SUMO Traffic Lights: actuated](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)
- `delay_based` extends green while vehicles within `detectorRange` (example 100 m) have time loss above `minTimeLoss` (1 s). — [SUMO Traffic Lights: delay_based](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)

**Protected vs permitted lefts:**
- SUMO lets the left turn run with oncoming through traffic as yielding `g` when speeds are below `tls.minor-left.max-speed` (19.44 m/s = 70 km/h). Otherwise it uses a protected left phase, which needs a dedicated turn lane.
- After a phase with partially conflicting streams, it adds a protected left phase of `tls.left-green.time` (default 6 s) if a dedicated turn lane exists.
- Source: [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html); [NBOwnTLDef.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBOwnTLDef.cpp)

**Pedestrians (MUTCD, as quoted by agencies):**
- WALK is normally ≥ 7 s and may be as short as 4 s at low pedestrian volume. Clearance (flashing DON'T WALK) is computed at 3.5 ft/s walking speed (3.0 ft/s near schools and seniors per Bellevue).
- A leading pedestrian interval should be ≥ 3 s. NCDOT sets walk = LPI + 7 s.
- There is a ≥ 3 s buffer before conflicting vehicles are released. A countdown is required when the change interval exceeds 7 s.
- Sources: [NCDOT LPI guidance](https://connect.ncdot.gov/resources/safety/Teppl/TEPPL%20All%20Documents%20Library/NCDOT%20Leading%20Pedestrian%20Interval%20(LPI)%20Guidance.pdf); [FHWA Proven Safety Countermeasures: LPI](https://highways.dot.gov/safety/proven-safety-countermeasures/leading-pedestrian-interval); [Bellevue pedestrian signal guidelines](https://bellevuewa.gov/sites/default/files/media/pdf_document/2025/bellevue-pedestrian-signal-operations-guidelines.pdf)
- SUMO defaults are `tls.crossing-min.time` 4 s and `tls.crossing-clearance.time` 5 s. An actuated program can skip the pedestrian phase until someone is waiting. — [netconvert options](https://sumo.dlr.de/docs/netconvert.html); [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)

**Coordination / green waves:**
- SUMO's `tlsCoordinator.py` "modifies the traffic-light offsets to coordinate them for a given traffic demand". Generated programs start at offset 0, and `--tls.half-offset` / `--tls.quarter-offset` shift chosen lights by half or a quarter of the cycle. — [SUMO tls tools](https://sumo.dlr.de/docs/Tools/tls.html); [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)

**Night flash:**
- In late-night flash, the major street flashes yellow and the minor streets flash red (or red on all approaches).
- Research found flashing yellow/red cuts total delay by about 50% versus pretimed/actuated at low volume, and red/red gives the most delay.
- FDOT allows yellow/red flash when two-way main-street volume is below 200 veh/h.
- Some references warn against it where sight distance is poor.
- Sources: [USDOT ROSA P: Guidelines for Operating Traffic Signals During Low-Volume Conditions](https://rosap.ntl.bts.gov/view/dot/16200); [TxDOT Traffic Signal Manual: Flashing Operation](https://onlinemanuals.txdot.gov/TxDOTOnlineManuals/TxDOTManuals/tff/flashing_operation.htm); [FDOT TEM ch. 3](https://www.fdot.gov/docs/default-source/traffic/trafficservices/Studies/TEM/TEM2017/TEM-Chapter-3-June-2018.pdf)
- SUMO models "off" with `o` (blinking, yield) and `O` (off, priority) and behaves like a priority junction, or like an all-way stop when `rightOfWay="allwayStop"`. — [SUMO Traffic Lights: off](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)

### Inferences
- Porchlight only needs three of the eight state letters to simulate: `Red`, `Yellow`, `GreenProtected`, `GreenPermitted` (yield to foes), plus `FlashYellow`/`FlashRed` for night. Car logic:
  - G: go.
  - g: go only if no higher-priority foe is approaching or inside the box. The same check as an unsignalised minor approach.
  - Y: go if `distanceToStopLine < v²/(2·decel)`, i.e. it cannot stop comfortably; otherwise stop.
  - R: stop at the stop line.
  - Flash red: treat as a stop sign (all-way stop if every leg is red).
  - Flash yellow: treat as major priority.
- NEMA rings are overkill for a town of 3–5-leg junctions with no turn lanes. SUMO-style stage lists (a list of phases, each with a movement-state array) are simpler and cover everything Porchlight needs.
- Protected lefts need a left-turn lane, which Porchlight lacks. As in SUMO, every left turn should be permitted (`g`) and yield to oncoming traffic. Main roads (2 tiles wide) could optionally get a short protected-left "lagging" phase, as SUMO does when it cannot build a lane-based one.
- Webster with Porchlight's tiny flows (Y near 0) gives the lower bound C₀ ≈ (1.5L + 5). With 2 phases × (3 s yellow + 1 s all-red), L = 8 s, so C₀ ≈ 17 s. Real-world 60–120 s cycles would feel absurd in a small town where the player is driving. A 24–40 s game-second cycle is a better fit (see §7).

### Gaps
- MUTCD Part 4 primary text and the FHWA Signal Timing Manual tables (5-3 min green by facility, passage-time tables) could not be fetched. The numbers above are from agency summaries.
- No primary source confirming a "typical 60–120 s" cycle range was retrieved.

---

## 4. Automatically generating a signal program for an arbitrary 3/4/5-leg junction

### Takeaway
SUMO's netconvert gives a well-tested recipe that maps directly to Porchlight:
1. Sort incoming legs by importance.
2. Repeatedly pick the best pair of opposite legs (or a single leg) and give them a green stage.
3. Add every other movement that doesn't conflict with what is already green.
4. Downgrade conflicting turns to permitted `g`.
5. Append yellow and optional all-red.
6. Split the cycle's green equally (default 90 s cycle, about 31 s greens).

T-junctions fall out as two or three stages. Five-leg junctions get extra stages.

### Cited Findings
- **Defaults:**
  - "all traffic lights are generated with a fixed cycle and a cycle time of 90s".
  - "The green time is split equally between the main phases".
  - "All green phases are followed by a yellow phase", with length computed from the maximum incoming speed.
  - A default 4-arm junction gets: straight phase → left phase (only with a dedicated left lane) → orthogonal straight → orthogonal left. "The green phases usually have a duration of 31s".
  - "If there are more than 4 roads meeting at an intersection, additional green phases are generated."
  - Layout `incoming` gives each incoming edge its own phase with all movements. It may be chosen automatically when the highest-priority roads form a turning stream.
  - Actuated variants use `minDur` 5 s / `maxDur` 50 s.
  - Source: [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)
- **Algorithm (`NBOwnTLDef::computeLogicAndConts`):**
  - While incoming edges remain unprocessed, `getBestPair()` sorts them by incoming priority and keeps the top-priority group (or all of them if fewer than 2 share the top priority).
  - `getBestCombination()` scores every pair with `computeUnblockedWeightedStreamNumber`, which counts the streams the pair can run without mutual blocking. Ties go to the more nearly opposite pair, and the pair is only grouped if the score is positive.
  - For a 2-incoming-edge junction, the edges are paired only if the angle between them is ≥ 135°.
  - The chosen edges' links get `G`. Then:
    - `allowCompatible()` turns on any other non-conflicting link.
    - `allowUnrelated()` handles links that don't interact.
    - `correctConflicting()` downgrades links that conflict with a higher-priority green to `g` and flags "forbidden left movers".
  - If the incoming speed exceeds `minor-left.max-speed` and a turn lane exists, a separate left phase is built. Otherwise `g` is kept with a warning.
  - Each green is followed by yellow (`brakingTime`) and all-red (`allRedTime`, extended to cover the longest crossing time `maxCross`).
  - Source: [NBOwnTLDef.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBOwnTLDef.cpp)
- **When to signalise at all (guessing):** `--tls.guess` (off by default) uses a threshold on the sum of incoming lane speeds (69.44 m/s). `--tls.join` merges lights within 20 m into one controller. — [netconvert options](https://sumo.dlr.de/docs/netconvert.html)
- **Right-on-red:** with `traffic_light_right_on_red`, right turns are permitted in all phases and treated like a priority stop when not green. — [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)
- **CityFlow:** a phase is just `{time, availableRoadLinks}`, with no generation heuristics in the format. — [CityFlow roadnet.rst](https://github.com/cityflow-project/CityFlow/blob/master/docs/source/roadnet.rst)
- **TM:PE timed traffic lights:**
  - A light is a list of steps, each with `MinTime`, `MaxTime`, a `ChangeMetric` and a `WaitFlowBalance`. Below MinTime it never switches. At or above MaxTime it always switches.
  - In between, the default metric switches when smoothed `flow − wait < 0`, i.e. more vehicles waiting at red than flowing at green. Flow is divided by `WaitFlowBalance` ("a value smaller than 1 rewards steady traffic currents").
  - Alternative metrics are FirstFlow, FirstWait, NoFlow and NoWait.
  - Transitions are GreenToRed (yellow) and RedToGreen (red+yellow).
  - Source: [TimedTrafficLightsStep.cs](https://github.com/CitiesSkylinesMods/TMPE/blob/master/TLM/TLM/TrafficLight/Impl/TimedTrafficLightsStep.cs)

### Inferences
Porchlight version of the netconvert recipe (one lane per direction, no turn lanes):
1. **Legs:** for the junction node, collect legs (road end, outward unit direction, kind) sorted by angle. Leg importance = (Main ? 2 : 1).
2. **Movements:** for every ordered pair of legs (i ≠ j), create a movement. Classify it as straight, right, left or U by relative angle using SUMO's thresholds (|θ| < 44° straight; >90° right or left; reversed is U).
3. **Foes:** compute foe bits topologically (SUMO's cyclic test plus a merge test for the same exit). Compute yield bits: lower-importance leg yields; at equal importance, turning yields to straight; otherwise right-before-left (or the local convention).
4. **Stages:** greedily, pick the best remaining leg pair by importance, then by angle closest to 180°. Mark both legs' movements green. Add any other movement not foe to a protected green. Mark lefts that conflict with oncoming straights as permitted. Remove the legs and repeat.
   - 4-leg: 2 stages (N–S, E–W).
   - T (3-leg) with a Main through road: stage 1 is the main pair (left into the stem permitted); stage 2 is the stem alone.
   - 5-leg: 3 stages.
   - A leg that cannot pair (angle < 135° to every other leg) gets its own stage, like SUMO's `incoming` layout.
5. **Timing:** yellow 2.5–3 s, all-red 1 s, green split equally with a Main-weighted bias (e.g. green ∝ importance), and total cycle 24–40 s of game time (see §7). Optional actuation à la TM:PE/SUMO: `minGreen` 5 s, `maxGreen` 20 s, end green early when no car is within about 2 s of travel of the stop line on green legs and someone is queued at red.

### Gaps
- I did not find documentation of how Cities: Skylines (vanilla) or Cities: Skylines II generate their default light programs. TM:PE's source covers the user-timed and adaptive logic only.

---

## 5. How games model and render signals cheaply

### Takeaway
Use one small state machine per junction, not per signal head. Every signal head is a pure function of (junction phase, the head's leg), so rendering just looks up a colour. Cars never see the head. They query the junction's controller for their movement's state at the stop line. With fixed-time control, phase = f(globalTime + offset), so nothing needs syncing beyond the clock.

### Cited Findings
- SUMO's whole controller state is (program, phase index, time in phase). The rendered and simulated state of every signal is `state[linkIndex]`. — [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)
- TM:PE keeps per-node step state (`startFrame`, `endTransitionStart`, `CurrentFlow`, `CurrentWait`) and exposes `RoadBaseAI.TrafficLightState` values (Green, RedToGreen, GreenToRed, Red) for the vanilla renderer. — [TimedTrafficLightsStep.cs](https://github.com/CitiesSkylinesMods/TMPE/blob/master/TLM/TLM/TrafficLight/Impl/TimedTrafficLightsStep.cs)
- Stop-line queueing in SUMO is the ordinary car-following model with a virtual stopped leader at the stop line (1 m before the lane end for lights), plus a yield check at `visibilityDistance`. — [SUMO Intersections](https://sumo.dlr.de/docs/Simulation/Intersections.html)
- Drivers' red/yellow compliance can be modelled with `jmDriveAfterYellowTime` / `jmDriveAfterRedTime` (default −1 = comply). "Vehicles that are too fast to brake always drive at yellow." — [SUMO vType params](https://sumo.dlr.de/docs/Definition_of_Vehicles,_Vehicle_Types,_and_Routes.html#junction_model_parameters)
- I found no GDC talk or developer blog on signal rendering through search. Results were dominated by Cities: Skylines player forums (see Gaps).

### Inferences
Rendering (Game/Rendering, WorldParts.cs):
- **Placement:** one pole per incoming leg, at the far-right corner of the junction (or a mast arm over Main roads). The head faces the incoming leg's traffic (the head's facing = −leg outward direction).
- **Lamps:** three emissive "bulb" quads per head (red/amber/green). Reuse the glowing-bulb material system that already exists for the day/night cycle. Lit lamps get emission; unlit lamps get a dim lens colour. Flashing means `on = ((tick / FLASH_HALF_PERIOD) & 1) == 0`, about a 1 s period. Real US flashers run 50–60 flashes/min, but I did not find a citable value (see Gaps).
- **Instancing:** the lamp colour per head is derived each frame from `controller.HeadState(leg)`, i.e. the most restrictive state among that leg's movements, or a separate arrow head if the leg has a protected left. Since there are only a few dozen heads, per-frame `MaterialPropertyBlock` colour changes are cheap.
- **Stop signs:** a static octagon prop on the right of each stop-controlled leg, at the stop line.
- **Optional:** paint a white stop bar at each signalised or stop-controlled leg's stop line.

### Gaps
- No primary game-developer source (GDC Vault or studio blog) on signal implementation could be retrieved. gdcvault.com was blocked and searches surfaced only forum posts.
- MUTCD flash rate (flashes per minute) was not verified.

---

## 6. Deadlock/gridlock avoidance in game traffic

### Takeaway
Use layered defences:
1. Conflict resolution that is strictly ordered and deterministic, so two cars never wait on each other at a single junction.
2. "Don't block the box": don't enter unless the exit has room for your whole car.
3. Impatience or timeouts that eventually let a car ignore a blocker or reroute.
4. A last-resort teleport/despawn (SUMO teleports after a wait; Cities: Skylines despawns).

Porchlight already has a fallback at step 4: the `PATIENCE` U-turn.

### Cited Findings
- **SUMO no-block heuristic:** "drivers are forbidden to enter an intersection if the outbound road is jammed… By default, vehicles in SUMO try to prevent blocking intersections. This is accomplished by the **no-block-heuristic**". It can be disabled per node or connection with `keepClear="false"`, or per vType after `jmIgnoreKeepClearTime` seconds of waiting. — [SUMO Intersections: Junction Blocking](https://sumo.dlr.de/docs/Simulation/Intersections.html)
- **SUMO deadlock fallbacks:** stuck vehicles are teleported to resolve deadlock. `--ignore-junction-blocker <time>` lets vehicles ignore an intersection blocker after a set time, "modeling the real-life behavior of eventually finding a way around the offending vehicle". — [SUMO Intersections](https://sumo.dlr.de/docs/Simulation/Intersections.html)
- **SUMO generator safety:** when grouping non-car movements, `NBOwnTLDef` avoids "a mutual conflict for minor green streams which would create deadlock" (`tls.ignore-internal-junction-jam`). Mutual yield is a known deadlock source. — [NBOwnTLDef.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBOwnTLDef.cpp)
- **SUMO request building:** if two loaded prohibitions forbid each other mutually, the constructor unmarks them so the algorithm recomputes them. This guarantees the yield relation is antisymmetric. — [NBRequest.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBRequest.cpp)
- **TM:PE "Enter blocked junction":** a per-segment-end flag, `AllowEnterWhenBlocked`. By default it is only allowed where the node is not a real junction or has just 2 segments. A stopped foe in the Approach/Leave state is classified as `Blocked` and no longer counted as a conflicting vehicle. — [JunctionRestrictionsManager.cs](https://github.com/CitiesSkylinesMods/TMPE/blob/master/TLM/TLM/Manager/Impl/JunctionRestrictionsManager.cs); [TrafficPriorityManager.cs](https://github.com/CitiesSkylinesMods/TMPE/blob/master/TLM/TLM/Manager/Impl/TrafficPriorityManager.cs)
- **Cities: Skylines despawn:** players report vehicles despawning when stuck, and many disable it via mods. The debate is anecdotal (Steam forums). — e.g. [Steam discussion](https://steamcommunity.com/app/255710/discussions/0/520518053453308309)
- **Reservation-based control (Dresner & Stone AIM):** the intersection is an n×n grid of reservation tiles. The manager simulates each requesting vehicle's trajectory and rejects it if any space-time tile is already reserved, otherwise reserves them, first come first served. In simulation this cut delay and raised throughput versus lights. It is designed for autonomous agents. — [Dresner & Stone, AAMAS 2005](https://www.cs.utexas.edu/~aim/papers/AAMAS05-intersection.pdf); [IJCAI 2007](https://www.cs.utexas.edu/~aim/papers/IJCAI07-kurt.pdf)
- **CityFlow tie-breaking:** total order of type priority, reach steps, entry time, distance, vehicle priority. "Cannot yield" (cannot stop) vehicles always proceed, which prevents a car from stopping in the middle of a crossing. — [CityFlow roadnet.cpp](https://github.com/cityflow-project/CityFlow/blob/master/src/roadnet/roadnet.cpp)

### Inferences
Porchlight-specific deadlock rules:
1. **Box occupancy:** each junction keeps a small list of cars currently on its internal curves (between leaving the stop line and passing `reach` on the outgoing road). A car may enter only if none of them is on a foe movement.
   - This is a lightweight version of AIM in which the whole conflict area of a movement is one "tile". Movement-level foe masks make it O(cars in box).
2. **Keep clear:** before entering, check that the outgoing road has at least `GAP + carLength` free from its start in the target direction. This needs a "last car position from the start" query per road end. If it fails, wait at the stop line even on green.
   - Skip this check at 2-leg nodes, as TM:PE does by default.
3. **Total order for ties:** all-way-stop and same-priority conflicts break ties by `(arrivalTick, carIndex)`. This is deterministic and antisymmetric, so circular waits cannot form within one junction.
4. **Cycles across junctions** (e.g. a ring of short blocks each blocked by the next) are prevented mostly by rule 2. Keep a fallback: after `PATIENCE` seconds at a stop line with `keepClear` failing, the existing U-turn or "pick another exit" logic can apply. Re-picking `next` among exits that do have room is the gentlest option.
5. **Signal sanity:** generated stages must never give `G` to two foe movements. Assert `(greenMask & foes[m]) == 0` for every protected movement m at generation time. Permitted `g` relies on yield bits, which must be antisymmetric (never both i yields to j and j yields to i).

### Gaps
- No quantitative data on how often gridlock arises in small-town-scale game traffic. No primary developer-side source on Cities: Skylines' despawn timer value was found.

---

## 7. Concrete recommended design for Porchlight

### Takeaway
Build the following:
- A `Junctions` build step that turns `RoadStroke` graph nodes into `Junction` objects. Each has legs, movements, `ulong` foe/yield masks, stop-line `s` per leg, a control type (None / Priority / Stop / AllWayStop / Signal), and a deterministic `SignalController`, which is fixed-time by default with optional gap-actuation.
- In `Traffic.Update`, each car approaching a junction calls `junction.MayEnter(car, movement, now)`. The result becomes a virtual obstacle at the stop line through the existing `GapAhead`/`target` speed logic.
- Run signals in integer milliseconds of sim time. Phase = f((t + offset) mod cycle), so multiplayer clients compute lights locally from the synced clock.

### Cited Findings
(The design is synthesised from the sources above; supporting facts:)
- Lights as a phase list of per-link states (SUMO `tlLogic`/`phase`), with `minDur`/`maxDur` for actuation and offset for coordination. — [SUMO Traffic Lights](https://sumo.dlr.de/docs/Simulation/Traffic_Lights.html)
- Foe/response bitsets per link. — [SUMO Road Networks: Requests](https://sumo.dlr.de/docs/Networks/SUMO_Road_Networks.html#requests)
- Stop at the lane end (minor) or 1 m before it (light). — [SUMO Intersections](https://sumo.dlr.de/docs/Simulation/Intersections.html)
- Junction-type choice from speed and priority (SUMO `computeNodeTypes`), and the netconvert phase-building recipe. — [NBAlgorithms.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBAlgorithms.cpp), [NBOwnTLDef.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBOwnTLDef.cpp)
- TM:PE min/max plus a wait-vs-flow switching metric. — [TimedTrafficLightsStep.cs](https://github.com/CitiesSkylinesMods/TMPE/blob/master/TLM/TLM/TrafficLight/Impl/TimedTrafficLightsStep.cs)
- Kinematic yellow t + v/(2a) and red clearance (W + L)/v. — [NJDOT](https://www.nj.gov/transportation/about/publicat/lmreports/pdf/rlrappendix11.2011.pdf)
- Late-night flash: yellow on the major road, red on the minor. — [ROSA P low-volume guidelines](https://rosap.ntl.bts.gov/view/dot/16200)

### Inferences

**Time scale.** Calibrate in tiles and seconds, not real-world numbers:
- Cars cruise at 2.4 tiles/s, and braking is the code's lag (time constant 0.33 s), so a car stops from cruise in about 0.8–1 tile. Kinematic yellow t + v/(2a), with t = 1 s and an effective a of about 3 tiles/s² (pick a comfortable value and use it for the "can I stop?" test too), gives ≈ 1 + 2.4/6 ≈ 1.4 s. Use **yellow = 2.5 s**, which reads clearly to a player.
- A junction is about 1–2 tiles across, so the curve is about 2·`CORNER` ≈ 1.8 tiles. Red clearance (W + L)/v ≈ (1.8 + 0.6)/2.4 ≈ 1 s. Use **all-red = 1 s**.
- **minGreen = 5 s**, which matches SUMO's and FHWA's floor and is enough to discharge 3–4 queued cars at GAP 1.6 tiles.
- **Cycle:** 2 stages × (green + 3.5 s). With green 10–14 s, the cycle is about 27–35 s. A 3-stage T or 5-leg junction is about 40–50 s. That keeps a player's worst red wait under about 30 s. Real 90 s cycles would feel broken in a small town.
- Make these constants in one `SignalTiming` struct so they can be tuned. They should scale with the game's real-time speed, not the compressed day/night clock (the day cycle is presumably much faster than real time, and signals should not speed up with it).

**Data structures** (Shared/Explore, pure C#, deterministic):
```csharp
enum Control : byte { None, Priority, PriorityStop, AllWayStop, RightBeforeLeft, Signal }
enum Turn : byte { Straight, Right, Left, U }
enum Light : byte { Red, Yellow, Green /*protected G*/, GreenYield /*permitted g*/, FlashYellow, FlashRed, Off }

sealed class Leg {            // one road end at the junction
  public int road; public int dirIn;   // car.dir that drives INTO the node on this road
  public Vec2 outward;                 // unit vector from node along the road
  public double angle;                 // atan2(outward), legs sorted by this
  public byte importance;              // 2 Main, 1 Street
  public double stopS;                 // distance-from-node of the stop line (≈ Reach + crossHalfWidth)
  public bool major;                   // priority leg (Priority control)
}
sealed class Movement {       // leg i -> leg j
  public int from, to; public Turn turn;
  public ulong foes;          // movements that physically conflict (incl. merges into same exit)
  public ulong yieldTo;       // subset of foes this one must yield to (SUMO 'response')
  public double conflictT;    // earliest t on its corner Bézier where it meets a foe (CityFlow-style)
}
sealed class Stage { public Light[] perMovement; public int minMs, maxMs; }
sealed class SignalPlan {
  public Stage[] stages;      // greens; yellow/all-red derived between them
  public int yellowMs = 2500, allRedMs = 1000, offsetMs;
  public int CycleMs => /* sum(green) + n*(yellow+allRed) */ 0;
}
sealed class Junction {
  public int node; public Leg[] legs; public Movement[] moves; public Control control;
  public SignalPlan plan;     // null unless Signal
  public SignalState sig;     // { int stage; int sub /*0 green,1 yellow,2 allred*/; int elapsedMs; }
  public List<int> inBox;     // car indices on a movement's curve now (with their movement id)
  public List<(int car, int leg, long arrivalTick)> stopped; // for stop/all-way-stop FCFS
}
```

**Build** (`Junctions.Build(IsoLayout, Rng)` next to `Layout.DeadEnds`):
1. **Merge nodes:** union-find over `byNode` nodes closer than about 1.5 tiles into one logical junction. This handles near-coincident nodes, like SUMO's `junctions.join` at 10 m. Legs whose road connects two merged nodes are internal and dropped.
2. **Legs:** one leg per non-internal road end (skip Paths, and keep cul-de-sac and stub rules as in `mains`). Sort by angle.
3. **Movements and turns:** for each i ≠ j, make a movement and classify the turn by relative angle (SUMO thresholds: 44° and 90°). Skip U-turns unless the junction has only one leg.
4. **Foes:** movements a (i→j) and b (k→l) are foes if their endpoints interleave on the circular leg order (chords cross), if they share an exit (merge), or if one of them is a left turn opposed by the other's straight. The chord test is essentially SUMO's CW/CCW walk.
5. **Yield** (only for non-signal control, and for `GreenYield` under signals), in SUMO's `setBlocking` order: U-turn yields; lower-importance leg yields; turning yields to straight; otherwise right-before-left (the first of the two legs met going clockwise from the other's source yields). Assert antisymmetry.
6. **Control choice:** legs ≤ 2 → None. Otherwise:
   - ≥ 3 legs and ≥ 2 Main legs plus a Street leg, or ≥ 4 legs with any Main → Signal, subject to a town-size budget. A small town might have 1–4 lights, chosen by the highest summed importance and placed downtown.
   - Main with Streets → PriorityStop: Main legs major, stop signs on Streets.
   - All Streets → AllWayStop for 4+ legs, PriorityStop on the stem for T-junctions. Or RightBeforeLeft by town seed.
7. **Signal plan:** use the netconvert-style greedy pairing described in §4. Green per stage = `clamp(baseGreen * importanceWeight, minGreen, maxGreen)`. `offsetMs` = distance along the Main from the first signal / CRUISE, for a green wave on Main Street (inference from `tlsCoordinator` and the half/quarter-offset options).

**Controller tick** (deterministic integer ms, run in `Traffic.Update` before cars):
- **Fixed-time:** `int t = (int)((simMs + offsetMs) % cycleMs)`, then walk the stage durations to get (stage, sub). It is stateless, so multiplayer clients reproduce it exactly from the synced sim clock and the host doesn't need to send light state.
- **Actuated (optional, TM:PE/SUMO style):**
  - Stay in green while `elapsed < minMs`.
  - Then end the green if either: (no car within `2 s × CRUISE` ≈ 5 tiles of the stop line on any green-movement leg for `maxGap` ≈ 2 s, and some red leg has a stopped car), or `elapsed ≥ maxMs`.
  - Skip stages with no demand. Rest in the main-road green when idle (a "recall" to Main).
  - The host must sync (stage, sub, elapsedMs) per signal: 3 small ints at 10 Hz, or only on change.
- **Night:** when the day/night clock is in the late-night window (e.g. 23:00–05:00) and the junction's plan says `nightFlash`, switch to FlashYellow on major legs and FlashRed on minor legs, or FlashRed everywhere when no leg is Main, as an all-way stop. This matches the MUTCD and LNF practice above and is a nice visual beat with the glowing bulbs.

**Car side** (changes to Traffic.cs):
- **Choose the turn early.** `car.next` already exists, chosen when `rest < max(CORNER, TurnReach)`. Choose it earlier instead, at about `stopS + brakingDistance` ≈ 2.5 tiles out, so the car knows its movement before the stop line.
- **Ask at the stop line.** Compute `distToStop = rest − leg.stopS`. If `distToStop` is inside the look-ahead window (about 2·GAP), call `junction.MayEnter(carIdx, moveId, simMs)`. If the answer is false, take `ahead = min(ahead, distToStop)` so the existing `target` formula stops the car at the line. No new kinematics are needed.
- **`MayEnter` logic:**
  1. The car is already in the box → true. Never stop mid-junction, CityFlow's "cannot yield" rule.
  2. **Signal:**
     - `Red` → false.
     - `Yellow` → true only if `distToStop < v²/(2·aComfort)` (cannot stop). Remember the decision per car so it doesn't flip-flop.
     - `Green` → keep-clear check only.
     - `GreenYield` → step 4.
  3. **Stop / AllWayStop / FlashRed:** the car must have been stopped (speed < 0.1) at the line for ≥ `STOP_DWELL` (about 0.5–1 s, cf. SUMO `jmStopSignWait`). Record `arrivalTick` on first stop. For AllWayStop, it must also be the earliest `(arrivalTick, carIdx)` among stopped cars whose movement is a foe.
  4. **Yield check:** no car in `inBox` on a movement in `foes`. No car approaching on a movement in `yieldTo` within a time gap: `distToTheirStop/v < 1.0 s + ownCrossTime`, using SUMO's `jmTimegapMinor` of 1 s and TM:PE's time-to-node estimate. Major-road cars on `Green` skip the approaching check but still respect `inBox` foes. That avoids a crash with a straggler, and the rule set stays safe even if the plan is wrong.
  5. **Keep clear:** the outgoing road has free length ≥ `GAP + 0.6` from its start in the exit direction. This needs a per-road-direction "rear-most car s" cache, rebuilt each tick with an O(cars) pass. At 70 cars it is trivial.
- **Box bookkeeping:** on entering the corner curve (passing the stop line with `MayEnter == true`), add `(car, move)` to `junction.inBox`. Remove it when `car.prev` is cleared (`done ≥ CORNER`), the point where Traffic.cs already ends the curve.
- **Deadlock fallback:** if `wait > PATIENCE` while held by keep-clear (not by red), re-pick `car.next` among exits with room. If there are none, after `2×PATIENCE` fall back to the existing reverse/U-turn. Never time out a red light, because players notice.
- **GapAhead across the junction:** also check the first car on `car.next`'s road in the exit direction, so cars don't drive into the back of a queue just past the junction. This is the same data as keep-clear.
- **Determinism:** iterate cars in list order. Never use `Dictionary` enumeration order for decisions; keep `byNode` lists sorted. All tie-breaks go by `(arrivalTick, carIndex)`. Signal time is integer ms from the sim clock. Keep the existing `Rng` only for route choice.

**Multiplayer:**
- Fixed-time lights need zero extra sync (derived from the clock + `offsetMs`).
- Actuated lights need (stage, sub, elapsedMs) per signal in the 10 Hz state.
- Clients render heads from the same function. Cars are already host-simulated, so client-side compliance is automatic.

**The player's car:**
- Treat it as an obstacle in `inBox` when it is inside a junction polygon, so AI cars won't enter crossing paths. This extends the existing `obstacles` list.
- Optional: a "ran a red" event for gameplay (police, reputation), detected when the player crosses a stop line whose head is Red for longer than about 0.5 s.

**Rendering:**
- Expose `Junction.HeadLight(legIdx, simMs) → Light` (most restrictive state among movements from that leg, with Left shown separately only if there is a protected stage) and a list of `(position, facing, legIdx)` heads computed at build time.
- WorldParts.cs builds a pole and a 3-lamp head per leg at signalised junctions, plus stop-sign props and painted stop bars at stop-controlled legs. Per frame, set lamp emission from `HeadLight`. Flashing uses `(simMs / 500) & 1`.

### Gaps
- The game's tile-to-metre scale and the ratio of the day/night clock to real time were not determined. The timing values above are reasoned from `CRUISE`, `GAP` and `CORNER` and should be playtested.
- Not verified: how Porchlight's Main roads represent 2 lanes per direction versus 1 wide lane. Traffic.cs places one lane per direction at width/4, so I assumed a single lane per direction everywhere and no turn lanes.

# Road network validation, repair and auto-configuration ("RoadSense") for Porchlight

Scope note: research done 2026-11-08. Network limits applied: sumo.dlr.de and mutcd.fhwa.dot.gov could not be fetched from this session (DNS blocked). SUMO facts were taken instead from the SUMO docs and C++ source on GitHub (raw.githubusercontent.com, eclipse-sumo/sumo `main`). OSMnx and osm2streets/A/B Street facts come from their source on GitHub. MUTCD/AASHTO facts come from web-search snippets of state, municipal and FHWA pages, so treat exact table values as needing a check against the primary text.

Grounding in the Porchlight code (read 2026-11-08):
- Scale: `Directions.METRES_PER_TILE = 4` (Explore/Directions.cs). Main road asphalt is `mainRoadWidth = 2` tiles (8 m), streets `streetWidth = 1` tile (4 m), plus parking 0.5 and sidewalk 0.45 tiles per side (Raster.cs, Params.cs). Every metre threshold below is converted at 4 m/tile.
- Pipeline (Generator.cs): Terrain, Rivers, Site, Airport, Zoo, Roads, Streets, Blocks, Lots, Buildings, Rasterize, Docks. Each stage is a pure `Run(model, rng)` with `Check(model)`, and each gets its own RNG stream `Rng.Stage(seed, name)`.
- RoadsStage: grid A* (8-neighbour, TURN_COST 10) from gates, airport and zoo to the centre. Polylines are RDP-simplified (eps 0.6), then `Planarize`, `Compact`, and bridges come from water sampling at 0.25 tiles. All edges are `RoadKind.Main`.
- StreetsStage: grid-snapped (±8°) growth from seeds spaced `streetSpacing` (9) along mains. `MIN_ANGLE` is 35°, `JUNCTION_CLEARANCE` 3 tiles, `MIN_SEGMENT` 1.5, `DUPLICATE_DISTANCE` 1.5, `CUL_DE_SAC_LENGTH` 4. After that come dead-end connectors, footpaths, Planarize, MergePassThrough(keep), DropDuplicateStreets, bulb-fit pruning, and `KeepConnected` (largest component from the centre). Check(): crossings only at shared nodes, no duplicates, slopes, bridges, airport road.
- Model: `RoadNode{id, at}`, `RoadEdge{id, from, to, kind, points}`, `RoadKind {Main, Street, Path}`, and `Bridge{roadEdgeId, from, to}`. The model has no junction control, speed, lanes or one-way fields yet.
- Traffic.cs drives every non-Path edge of length ≥ 1 tile, except Street edges with a degree-1 end (`Layout.DeadEnds`). Cars cruise at 2.4 tiles/s with a 0.9-tile corner curve and U-turn at dead ends. They do not yield or stop at junctions.

---

## 1. What do SUMO netconvert, OSMnx, osm2streets/A/B Street, CityEngine and Cities: Skylines/TM:PE do to clean networks and assign control?

### Takeaway
The mature importers all use the same core operations. They cluster nearby junctions within about 10 m and rebuild one node per cluster, with guard rules (no parallel approaches, at most about 5 incoming legs, connected sub-clusters only, skip map-edge nodes). They remove degree-2 "geometry" nodes, drop isolated or small components, and guess signals from summed approach speed × lanes, with a too-fast cutoff. Roundabouts are found as directed loops with no U-turns, and priority/stop signs come from road rank: the highest rank gets priority, everyone else stops, and all-way stop applies when every rank is equal.

### Cited Findings
**SUMO netconvert (options and defaults from docs/web/docs/netconvert.md on GitHub main)**
- `--junctions.join` "Joins junctions that are close to each other (recommended for OSM import)", default false. `--junctions.join-dist` default **10** (m). `--junctions.join.parallel-threshold` default **30°** ("angular threshold ... for rejection of parallel edges when joining"). `--junctions.join-same FLOAT` "joins junctions that have similar coordinates even if not connected", default -1 (off). `--junctions.join-exclude`, `--junctions.join-reset`. — [SUMO netconvert docs (GitHub)](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/netconvert.md)
- Older SUMO man pages list the join-dist default as 15, so the default has changed between versions. — [Ubuntu man page, sumo 0.32](https://manpages.ubuntu.com/manpages/bionic/man1/netconvert.1.html); current value 10 per [netconvert.md](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/netconvert.md)
- Junction shape options: `--junctions.corner-detail` 5 intermediate corner points, `--junctions.internal-link-detail` 5, `--default.junctions.radius` **4 m** turning radius, `--junctions.small-radius` 1.5 m for junctions that need no wide turns, plus `--junctions.minimal-shape` and `--junctions.endpoint-shape` alternatives. `--junctions.limit-turn-speed` caps speed through a junction at an average lateral acceleration of **5.5 m/s²**, ignoring angle changes below **15°**. — [netconvert.md](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/netconvert.md)
- `--geometry.remove` (-R) "Replace nodes which only define edge geometry by geometry points (joins edges)", default false. Options include `.min-length` (merge edges with differing attributes when shorter than this), `.width-tolerance`, and `.max-junction-size`. `--geometry.min-dist` "reduces too similar geometry points". `--geometry.max-angle` warns at **99°** between successive segments, and `--geometry.max-angle.fix` straightens them. — [netconvert.md](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/netconvert.md)
- Geometry-node removal targets nodes "where one street comes in and one with the same attributes goes out or where two parallel edges come in and two (with the same attributes) come out". The removed node becomes a geometry point. — [SUMO Further Options (GitHub)](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/Networks/Further_Options.md)
- `--roundabouts.guess` default **true**. `--roundabouts.guess.max-length` **3500** m (structures with a larger circumference are not roundabouts). `--roundabouts.visibility-distance` 9 m. — [netconvert.md](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/netconvert.md)
- Roundabout guessing algorithm: candidates are edges with no turnaround and no reverse connection. From each candidate it walks the loop "using always the leftmost" connected edge until it returns to an edge already in the loop. It aborts at right-before-left nodes or nodes marked not-roundabout. — [NBEdgeCont::guessRoundabouts (GitHub)](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBEdgeCont.cpp)
- `--tls.guess` default false. `--tls.guess.threshold` default **69.4444** ("minimum value for the sum of all incoming lane speeds", in m/s; 69.44 m/s = 250 km/h summed). `--tls.guess.joining` (guess on node clusters). `--tls.join` / `--tls.join-dist` **20** m. `--tls.guess-signals` with `.dist` **25** m treats signal nodes around an intersection as one TLS. `--tls.minor-left.max-speed` **19.44** m/s: below it, left turns share the phase with oncoming straight traffic. `--tls.cycle.time` 90 s, `--tls.green.time` 31 s. — [netconvert.md](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/netconvert.md)
- The TLS heuristic in source (`shouldBeTLSControlled`) sums `numLanes × laneSpeed` over incoming edges from outside the cluster. A junction gets a TLS iff that sum ≥ threshold **and no incident edge is faster than 79 km/h** ("tooFast"), and the node is not geometry-like. For joined clusters the threshold scales by `size / min(size, 4)` ("assume that at most 4 nodes should be needed"). — [NBNodeCont.cpp (GitHub)](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBNodeCont.cpp)
- Right-before-left (uncontrolled equal-priority) junctions are built only when incoming speeds are below **13.61 m/s (49 km/h)** (`--junctions.right-before-left.speed-threshold`). Default junction types include priority, right_before_left, priority_stop and allway_stop. Default edge speed is 13.89 m/s and the default lane count is 1. — [netconvert.md](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/netconvert.md)
- Junction-join algorithm (source): `generateNodeClusters` flood-fills over edges shorter than maxDist. It then removes excluded nodes and runs `pruneClusterFringe`, which removes nodes that geometry.remove could eliminate, then `pruneSlipLaneNodes`. `feasibleCluster` rejects clusters with **> 5 incoming edges** ("too complex"). It also rejects clusters with **parallel incoming/outgoing edges** whose angle difference is below 30° (below 10° if they target the same node or end within maxDist); when that happens it tries again without the lower-priority node. If the cluster is still infeasible, `reduceToCircle(cluster, 4, …)` tries to shrink it to a 4-node loop. `pruneLongEdges` rejects joins that would swallow long edges. — [NBNodeCont.cpp (GitHub)](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBNodeCont.cpp)
- Ramps: `--ramps.guess` adds acceleration lanes where a highway junction has 1 in, 1 out and 1 incoming minor edge, and deceleration lanes for 1 outgoing minor edge. Lanes are added only when incoming and outgoing lane sums differ; `--ramps.min-highway-speed` and `--ramps.max-ramp-speed` set the class split. — [Further Options](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/Networks/Further_Options.md)
- Pruning: `--remove-edges.isolated` "Removes isolated edges" (warning "Removed a road without junctions"). `--keep-edges.components INT` "Only keep the INT largest weakly connected components". `--keep-edges.min-speed`. `--no-turnarounds*` family (default: no turnarounds at geometry-like nodes). — [netconvert.md](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/netconvert.md)

**OSMnx (osmnx/simplification.py on GitHub main)**
- `simplify_graph` keeps a node as a "true endpoint" if any of these holds: (1) it self-loops, (2) it has no in-edges or no out-edges, (3) it does NOT have exactly 2 neighbours with degree 2 or 4 (that covers dead ends, 3+-way junctions and one-way↔two-way changes), (4) it has a listed node attribute, or (5) its incident edges differ in a listed edge attribute (`edge_attrs_differ`). Every other node is interstitial and gets merged into the edge geometry. Rings with no endpoints are removed. — [OSMnx simplification.py](https://github.com/gboeing/osmnx/blob/main/osmnx/simplification.py)
- `consolidate_intersections(G, tolerance=10, rebuild_graph=True, max_length=None, dead_ends=False, reconnect_edges=True)`. Tolerance is a per-node **buffer radius**: "to consolidate nodes within 10 meters of each other, use tolerance=5". The algorithm buffers nodes, unions the overlapping buffers, and takes the centroid. Each cluster is then split into its weakly connected components inside the cluster subgraph, so nearby dead ends or a bridge over a street do not merge. `max_length` ignores long edges in that connectivity test so cloverleaf ramps don't merge their ends. Dead ends are discarded unless `dead_ends=True`. Edges are reconnected to the cluster nodes with geometry extended. — [OSMnx simplification.py](https://github.com/gboeing/osmnx/blob/main/osmnx/simplification.py)
- The algorithm is documented in Boeing, G. 2025, "Topological Graph Simplification Solutions to the Street Intersection Miscount Problem", Transactions in GIS 29(3) e70037. — [OSMnx docstring citing doi:10.1111/tgis.70037](https://github.com/gboeing/osmnx/blob/main/osmnx/simplification.py)

**osm2streets / A/B Street**
- osm2streets transformations: `ZipSidepaths`, `RemoveDisconnectedRoads`, `CollapseShortRoads`, `CollapseDegenerateIntersections`, `MergeDualCarriageways`. The standard sequence for clipped areas is CollapseShortRoads → CollapseDegenerateIntersections → CollapseShortRoads again ("The above may discover more roads to collapse"). `apply_transformations_with_invariant_checks` runs `check_invariants()` after every step. — [osm2streets transform/mod.rs](https://github.com/a-b-street/osm2streets/blob/main/osm2streets/src/transform/mod.rs)
- `collapse_short_road` keeps src and destroys dst. It refuses to collapse a road that touches a `MapEdge` intersection. If an earlier collapse already made the road a loop (src == dst) it just removes it, and it repairs turn restrictions within 3 roads. — [osm2streets operations/collapse_short_road.rs](https://github.com/a-b-street/osm2streets/blob/main/osm2streets/src/operations/collapse_short_road.rs)
- `CollapseDegenerateIntersections` merges degree-2 intersections when lane specs, name and layer match and there is no crossing. It refuses to merge two one-ways that point at each other. The authors note it might need a fixed-point loop: "It's possible we need to do this in a fixed-point until there are no changes, but meh." — [collapse_intersections.rs](https://github.com/a-b-street/osm2streets/blob/main/osm2streets/src/transform/collapse_intersections.rs)
- `remove_disconnected_roads` flood-fills the partitions (all roads treated as bidirectional), keeps the largest, also removes cul-de-sacs for now, then removes intersections with no roads. — [remove_disconnected.rs](https://github.com/a-b-street/osm2streets/blob/main/osm2streets/src/transform/remove_disconnected.rs)
- `IntersectionKind` includes `MapEdge` (road crosses the map boundary), `Terminus` (the roadway really ends), and a "connection" kind where roads meet but no flows interact (e.g. a lane-count change). `IntersectionControl` is `{Uncontrolled, Signed, Signalled, Construction}`. Invariants assert that every intersection has roads. — [osm2streets intersection.rs](https://github.com/a-b-street/osm2streets/blob/main/osm2streets/src/intersection.rs); [validate.rs](https://github.com/a-b-street/osm2streets/blob/main/osm2streets/src/validate.rs)
- A/B Street stop-sign heuristic (`ControlStopSign::new`) works as follows. Outbound-only one-ways are skipped. Junctions with ≤ 2 roads (degenerate) and dead ends get no signs. Each road is ranked by OSM highway class plus a sub-priority: cycleway/service lowest, `_link` ramps middle, roundabouts prioritised "so they clear out faster than people enter them". "If all roads have the same rank, all-way stop. Otherwise, everything stops except the highest-priority roads." No stop sign goes on an "extremely short" road, because that is "likely actually an intersection". — [A/B Street stop_signs.rs](https://github.com/a-b-street/abstreet/blob/main/map_model/src/objects/stop_signs.rs)
- A/B Street downgrades a signal to a stop sign when it has no movements. — [A/B Street map_model make/mod.rs](https://github.com/a-b-street/abstreet/blob/main/map_model/src/make/mod.rs)

**Cities: Skylines / TM:PE**
- When TM:PE's "High Priority Road" tool is applied, joining traffic yields by default (can be changed to stop), traffic on the priority road always has priority, and pedestrian crossings at those junctions are disallowed by default. Main-road detection relies on road width and lane count and "gets confused" when all roads are the same size. — [TM:PE wiki: High Priority Roads](https://github.com/CitiesSkylinesMods/TMPE/wiki/High-Priority-Roads); [TM:PE wiki: Policies](https://github.com/CitiesSkylinesMods/TMPE/wiki/Policies)
- TM:PE Junction Restrictions are per-junction toggles (U-turns, lane changing, entering a blocked junction, pedestrian crossing, turn on red). — [TM:PE wiki: Junction Restrictions](https://github.com/CitiesSkylinesMods/TMPE/wiki/Junction-Restrictions)

### Inferences
- At 4 m/tile, SUMO's 10 m join distance is **2.5 tiles**. OSMnx's "10 m" consolidation is tolerance 5 m = **1.25-tile buffer radius**. SUMO's 20 m TLS-join is **5 tiles**, its 4 m default junction radius is **1 tile**, and the 25 m signal-guess distance is about 6 tiles.
- SUMO's TLS rule translates to a small-town game like this. Assign nominal speeds per class (say main 50 km/h ≈ 13.9 m/s with 1 lane per direction, street 30 km/h ≈ 8.3 m/s). A 4-way main×main junction sums 4 × 13.9 = 55.6 < 69.4, so SUMO by default would NOT signal it unless mains have 2 lanes per direction. A game should pick its own threshold. The structure (Σ lanes×speed over incoming legs, with a too-fast cutoff) is easy to port.
- osm2streets' fixed-point need (collapse → degenerate merge → collapse) suggests the Porchlight repair loop should iterate to a fixed point with a pass cap, not run each operation once.
- Porchlight's gates and other map-edge nodes are the `MapEdge` equivalent and must be excluded from collapse and join, as osm2streets does.

### Gaps
- CityEngine's "Cleanup Graph" tool (merge-node distance, snap, intersect-segments options) was not researched. I found no fetched primary documentation, so no values are given.
- Cities: Skylines (vanilla, CS2) automatic traffic-light placement rules: only a 2017 user forum post was found, so it is not cited as fact.
- SUMO's default `--tls.default-type` and the exact `pruneLongEdges` length rule were not extracted.

---

## 2. Academic work on procedural road networks and their cleanup

### Takeaway
Procedural road papers handle validity mostly *during* growth through local constraints: snap to a nearby crossing, extend to an intersection, cut at a crossing, and shorten or rotate at obstacles. Later work adds hierarchy: major roads first, then minor roads, sometimes driven by traffic simulation. Explicit post-hoc cleanup passes are mostly found in GIS and simulation tools (section 1), not in the graphics literature, so Porchlight's repair stage borrows from GIS/sim practice.

### Cited Findings
- Parish & Müller, "Procedural Modeling of Cities", SIGGRAPH 2001, pp. 301–308. — [SIGGRAPH history archive](https://history.siggraph.org/?p=116923)
- Local constraints (as summarised by an implementer):
  - if two streets intersect, generate a crossing;
  - if a street ends close to an existing crossing, extend it to reach the crossing;
  - if a street is close to intersecting, extend it to form an intersection.
  — [tfdml blog, "Procedural city generation" (2015)](https://tfdml.blogspot.com/2015/03/procedural-city-generation.html)
- Lecture slides on the same paper summarise two more rules: a segment near an existing street adjusts its endpoint and creates an intersection, and a segment ending in an obstacle (water, park) is shortened or rotated to fit. — [phiresky procedural-cities presentation](https://phiresky.github.io/procedural-cities/presentation.html)
- "Interactive procedural street modeling" (Chen, Esch, Wonka, Müller, Zhang, SIGGRAPH 2008) traces streets from tensor fields. — [SIGGRAPH history archive entry](https://history.siggraph.org/?p=141564)
- Galin, Peytavie, Guérin, Beneš, "Authoring Hierarchical Road Networks", Computer Graphics Forum 30(7), 2011: hierarchical road networks connecting cities, towns and villages over large terrains, with user control of density and pattern. — [Eurographics DL](https://diglib.eg.org/items/33f74b9d-6792-4d51-8c32-c1d6c1824ea0); [PDF (Purdue)](https://www.cs.purdue.edu/homes/bbenes/papers/Galin11CGF.pdf)
- Beneš, Wilkie, Křivánek, "Procedural Modelling of Urban Road Networks", CGF 33(6), 2014: "A simple traffic simulation ... is then used to grow new major roads and to influence the locations of minor road growth." — [Eurographics DL](https://diglib.eg.org/handle/10.1111/v33i6pp132-142); [CGG Charles Univ.](https://cgg.mff.cuni.cz/?p=1672)
- Boeing (2025) frames intersection over-counting from multi-node junctions as the "street intersection miscount problem" and solves it with topological consolidation (section 1). — [OSMnx docstring / doi:10.1111/tgis.70037](https://github.com/gboeing/osmnx/blob/main/osmnx/simplification.py)

### Inferences
- Porchlight's StreetsStage already implements the Parish & Müller rules: cross → join, carry straight on to meet a nearby node or road, drop on obstacles. Most remaining defects therefore come from the *interaction* of stages: main roads from RDP-simplified 8-connected A*, streets seeded on mains with only a 3-tile clearance, connectors and footpaths added late, and pruning after planarisation. That fits a single post-pass after StreetsStage.
- Beneš 2014's idea of using traffic to decide major roads supports deriving the hierarchy from simulated or shortest-path through-flow (section 4) rather than only from `RoadKind`.

### Gaps
- No graphics paper found that gives explicit numeric validity metrics (minimum angle, minimum edge length) for generated road graphs. The Parish & Müller snap radius was not found in the sources I fetched.
- I did not fetch the Chen 2008 or Galin 2011 full text, so their specific cleanup steps (if any) are unverified.

---

## 3. Engineering rules: junction spacing, angles, offset T's, signal warrants, all-way stop, roundabouts

### Takeaway
Use these defaults:
- **Angles:** junction angles of 75–90° preferred, 60° the absolute floor.
- **Offset junctions:** two streets meeting a third from opposite sides should share a point or be offset ≥ 125 ft (38 m ≈ 9.5 tiles), and ≥ 200 ft (61 m ≈ 15 tiles) on arterials.
- **Signals:** MUTCD signal warrants are volume-based (8-hour, 4-hour, peak-hour), so a game should proxy volume with road class and simulated through-flow.
- **All-way stop:** for roughly balanced volumes, about 300 vph major and 200 vph minor in the MUTCD.
- **Roundabouts:** suit balanced flows up to about 15–20k vehicles/day for single-lane designs.

### Cited Findings
**Angles**
- The AASHTO Green Book (Ch. 9) recognises intersection angles between 60° and 120° as acceptable. NCHRP Report 279 recommends 75°–90° wherever possible, with 60° acceptable. — [Iowa DOT Design Manual 6A-8](https://iowadot.gov/design-manual/chapter-6-geometric-design/6a-8-intersection-alignment-and-realignment)
- ITE: "Skewed intersections should be avoided, and in no case should the angle be less than 75 degrees". FHWA's older-driver handbook endorses the 75° minimum and notes inconsistency between sources (ITE 75° vs AASHTO 60°). — [FHWA Older Road User Handbook ch. 2](https://highways.dot.gov/safety/other/older-road-user/desk-reference-handbook-designing-roadways-aging-population/chapter-2); [FHWA-RD-01-103 ch.1](https://www.fhwa.dot.gov/publications/research/safety/humanfac/01103/ch1.cfm)
- FDOT: angles "as close to 90 degrees as practical", and angles less than 75° should be avoided. — [FDOT FDM 212](https://www.fdot.gov/docs/default-source/roadway/FDM/current/2018FDM212Intersections.pdf)
- For realigning a skewed crossroad, Iowa DOT prefers an offset where crossroad traffic turns left onto the main road and then right off it. Each resulting intersection must still meet all design criteria. — [Iowa DOT 6A-8](https://iowadot.gov/design-manual/chapter-6-geometric-design/6a-8-intersection-alignment-and-realignment)

**Offset ("jog" / dog-leg) spacing**
- Many US subdivision codes require that streets meeting a third street from opposite sides "meet at the same point" or have centrelines offset ≥ 125 ft. Examples: [Anacortes WA 19.52.060](https://anacortes.municipal.codes/AMC/19.52.060); [Aurora MN ch.152](https://codelibrary.amlegal.com/codes/Aurora/latest/aurora_mn/0-0-0-9635); [Plymouth Twp MI](https://codelibrary.amlegal.com/codes/plymouthtwpmi/latest/plymouthtwp_mi/0-0-0-4892).
- Stricter variants: Dover DE requires 125 ft, or 200 ft at arterials ([Dover appendix](https://codelibrary.amlegal.com/codes/doverid/latest/dover_id/0-0-0-3260)). Rigby ID requires 125 ft for minor streets (160 ft desirable) and 300 ft for collectors ([Rigby ch.5](https://codelibrary.amlegal.com/codes/rigbyid/latest/rigby_id/0-0-0-5052)). Goodyear AZ prohibits jogs under 125 ft without engineer approval and may require 400 ft ([Goodyear 15-3-3](https://goodyear.municipal.codes/Code/15-3-3)).
- There is no single national standard. The numbers are local-code conventions. — (same sources)

**Signal warrants (MUTCD Chapter 4C)**
- The 2009 MUTCD Chapter 4C defines the signal needs study warrants. Warrant 1 (Eight-Hour Vehicular Volume) has Condition A (minimum vehicular volume) and Condition B (interruption of continuous traffic). Thresholds come from Table 4C-1, with 70% columns used for high speed or small communities and a 56% combination case. — [2009 MUTCD Ch. 4C (UNT web archive)](https://webarchive.library.unt.edu/web/20130225063604/http:/mutcd.fhwa.dot.gov/htm/2009/part4/part4c.htm); [TxDOT TMUTCD signal warrant guidelines](https://www.txdot.gov/manuals/des/tsp/chapter-10---appendix-k---intersection-analysis/section-5--tmutcd-signal-warrant-analysis-guidelin.html); [NCDOT signal guidelines](https://connect.ncdot.gov/resources/safety/SafetyPlanningResources/TIP%20Project%20Signal%20Recommendation%20Guidelines%2003112021.docx)
- Example 70% Condition A value for 1-lane major × 1-lane minor: 350 vph major (both approaches) / 105 vph minor approach, which implies 500/150 at 100%. — [Redmond WA study](https://cod.redmond.gov/2015HEDocuments6\doc42.pdf)

**All-way (multi-way) stop (MUTCD 2009 §2B.07)**
- Criteria:
  - 5 or more reported crashes in 12 months that a multi-way stop could correct (right-turn, left-turn, right-angle);
  - major-street volume ≥ 300 vph for any 8 hours, with the minor street averaging ≥ 200 units/h entering in the same 8 hours;
  - reduced to 70% (≈ 240/160 in one Vermont analysis) when the 85th-percentile major-street speed exceeds 40 mph.
  — [PennDOT LTAP TS-213](https://gis.penndot.pa.gov/BPR_PDF_FILES/Documents/LTAP/TechSheets/TS_213_Multiway_Stop_Control.pdf); [CCRPC Vermont AWSC analysis](https://www.ccrpcvt.org/wp-content/uploads/2018/10/NWR_MVR_AllWayStopWarrantAnalysis_20180508.pdf); [Turlock multi-way stop warrant](https://cij.turlock.ca.us/_pdf/files/multiwaystopwarrant.pdf)

**Roundabouts**
- Planning guidance puts single-lane roundabouts at up to about 15,000 vehicles/day (sources are garbled, and around 20,000/day needs further analysis). FDOT slides: ≤ 1,000 vph entering suits single-lane, 1,300–1,800 vph suggests two lanes. — [FHWA Roundabouts Informational Guide ch.4 (PDF)](https://www.fhwa.dot.gov/publications/research/safety/00067/000674.pdf); [FDOT multilane roundabout slides 2025](https://fdotwww.blob.core.windows.net/sitefinity/docs/default-source/design/training/transportationsymposium/2025-october/29-09-kuhlow-multilane-roundabout.pdf)
- Inscribed circle diameter: SCDOT says at least 105 ft for a WB-62 design vehicle, and 130–150 ft is typical where large vehicles are expected. FDOT gives 120–160 ft as typical for single-lane. — [SCDOT Roundabout Criteria](https://www.scdot.org/business/pdf/I85/RoadwayDesign/Roundabout_Criteria.pdf); [FDOT slides](https://fdotwww.blob.core.windows.net/sitefinity/docs/default-source/design/training/transportationsymposium/2025-october/29-09-kuhlow-multilane-roundabout.pdf)

### Inferences
Converted to Porchlight tiles (4 m/tile):
- 60° floor and 75° target: Streets already forbids < 35° (`MIN_ANGLE`) and snaps to grid ±8°. RoadSense should flag < 60° as an error to repair and 60–75° as a warning. The risk is mainly where grid streets meet RDP-simplified diagonal (45°) main roads.
- 125 ft = 38 m ≈ **9.5 tiles** offset minimum for street jogs, and 200 ft ≈ 61 m ≈ **15 tiles** on mains. Below a much smaller gap, about **≤ 2.5 tiles** (SUMO's 10 m), the two T's should be *merged* into one 4-way junction. Between 2.5 and ~9.5 tiles the dog-leg is "bad but tolerable". It can be repaired by sliding one T along the through road when the free end allows, or else left in place and given stop control on both stems.
- A roundabout with an inscribed diameter of 105–160 ft (32–49 m) is **8–12 tiles** across. That is large against Porchlight's street spacing of 9 tiles, so real roundabouts fit only where mains meet with space, such as the town centre or gates. A mini-roundabout of about 13–25 m (3–6 tiles) is a plausible game alternative. That range is an inference; I found no source for mini-roundabout sizes.
- Simple game proxy for warrants. Volume isn't known at generation time, but class and through-flow are:
  - signal if ≥ 2 legs are arterial/main and the node has ≥ 3 legs with top-quartile flow;
  - all-way stop if ≥ 3 legs of equal class with no dominant pair (A/B Street's "all same rank" rule);
  - two-way stop/yield on the minor legs otherwise.

### Gaps
- Exact current (2023 MUTCD 11th edition) Table 4C-1 values and the multi-way-stop section number were not verified from primary text, because mutcd.fhwa.dot.gov is blocked. The 500/150 and 750/75 figures are recall, partly implied by the Redmond 70% example.
- No authoritative national value for minimum junction spacing along local streets was found beyond the offset-jog codes.

---

## 4. Road hierarchy inference: betweenness, through-traffic, strokes

### Takeaway
Classify by two signals. The first is **strokes**: chains of edges joined at each node by the best-continuing pair under a deflection threshold. The second is **through-flow**: edge betweenness, or counts of all-pairs or gate-to-gate shortest paths. Rank strokes by length × mean betweenness, then cut into arterial, collector and local by quantiles. Both are deterministic and cheap at Porchlight's scale of hundreds of edges.

### Cited Findings
- "Natural streets" or strokes are built by linking street segments based on the interior/deflection angle between them. Stroke length then gives a street hierarchy. The COINS algorithm is released as a Python script and QGIS plugin. — [Tripathy et al. 2021, EPB (IDEAS)](https://ideas.repec.org/a/sae/envirb/v48y2021i8p2188-2205.html); [SAGE doi:10.1177/2399808320967680](https://journals.sagepub.com/doi/10.1177/2399808320967680)
- Thomson's strokes are smoothly continuous centre-line segments grouped by "good continuation" of direction and of character (width or line type). — [Thomson, Space Syntax Symposium 4 paper](https://www.spacesyntax.net/symposia-archive/SSS4/fullpapers/50Thomsonfpaper.pdf)
- Ma et al. 2024 (Int. J. Digital Earth) determine street hierarchies from the power-law distribution of degree and betweenness centrality across 20 Chinese cities. — [DOAJ record](https://doaj.org/article/c97a5b6511594bcd976a6a716e139bf6)
- Beneš et al. 2014 use traffic simulation to decide where major roads grow. — [Eurographics DL](https://diglib.eg.org/handle/10.1111/v33i6pp132-142)
- TM:PE picks the "main" road by width and lane count and fails when all widths are equal. That is an argument for adding continuity and flow signals. — [TM:PE wiki](https://github.com/CitiesSkylinesMods/TMPE/wiki/High-Priority-Roads)

### Inferences
Stroke building for Porchlight (deterministic):
- At each node, compute each incident edge's heading 2 tiles out (Streets already has `Heading(at, pts)`).
- For all pairs, deflection = π − angle between the outgoing headings.
- Greedily pair the edges with the smallest deflection ≤ 30° (common COINS-style threshold; the exact COINS default was not verified), preferring same `RoadKind`. Break ties by (deflection, min edge id).
- Unpaired edges end strokes.

Through-flow:
- Run Brandes edge betweenness on the undirected graph weighted by length, or a cheaper proxy: shortest paths between all gate pairs and gate→centre, plus a sample of block-centroid pairs taken in fixed node-id order.
- With N ≈ a few hundred nodes, exact Brandes is O(N·E log N), so it is trivially fast.
- For determinism, break ties in Dijkstra by node id and use a deterministic priority queue (no `HashSet` iteration order).

Classification:
- A Main edge on a gate→centre path is an **arterial**.
- Other Main edges, and Street strokes in the top ~20% of stroke-betweenness or ≥ 3 × streetSpacing long that connect two arterials, are **collectors**.
- The rest are **local**.
- Cul-de-sac branches (bridges in the graph-theoretic sense, leading to a degree-1 end) are always **local**.
- Paths stay **path**.

Class becomes per-edge config (suggested game defaults, not sourced standards):

| class | speed | lanes per direction | parking |
|---|---|---|---|
| arterial | 50 km/h ≈ 3.5 tiles/s | 1 (2 if the width is ever raised) | as now |
| collector | 40 km/h | 1 | as now |
| local | 30 km/h | 1 | as now |
| cul-de-sac | 20 km/h | 1 | as now |

  Today's Traffic CRUISE of 2.4 tiles/s is about 35 km/h at 4 m/tile.
- One-way: SUMO's and osm2streets' rules about one-ways (degenerate merge refuses opposing one-ways) imply one-way assignment needs a strong-connectivity check. Assign one-way only to pairs of parallel local streets 1 block apart in alternating directions, and keep it only if the directed graph stays strongly connected (Tarjan). Otherwise revert. With 1-tile streets, a game may simply keep everything two-way.

### Gaps
- No source found that gives specific betweenness quantile cut-offs for arterial/collector/local. The quantiles above are a design choice.
- COINS' default deflection-angle threshold was not confirmed from source.

---

## 5. Order of operations, idempotence, determinism, invariant checks

### Takeaway
Run topological clean-up first: snap/join nodes, collapse short edges, merge degree-2 nodes, planarize. Then geometric fixes (angles, dog-legs, bridge clearance), then pruning (stubs, components), then a final degree-2 merge. Loop to a fixed point with a cap, and only then classify and assign control, because control depends on the final topology. Check invariants after every operation in debug builds, as osm2streets does.

### Cited Findings
- osm2streets runs CollapseShortRoads, then CollapseDegenerateIntersections, then CollapseShortRoads again because the first steps "may discover more roads to collapse". Its debug mode checks invariants after each transformation. — [osm2streets transform/mod.rs](https://github.com/a-b-street/osm2streets/blob/main/osm2streets/src/transform/mod.rs)
- SUMO junction joining first prunes nodes that geometry.remove would eliminate (`pruneClusterFringe`), then checks feasibility, then falls back to reducing to a 4-circle. TLS guessing "assumes nodes are already joined". — [NBNodeCont.cpp](https://github.com/eclipse-sumo/sumo/blob/main/src/netbuild/NBNodeCont.cpp)
- SUMO recommends running netconvert a second time with `--geometry.remove` after `--geometry.split`, which is a two-pass order. — [SUMO Further Options](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/Networks/Further_Options.md)
- OSMnx splits each spatial cluster into its connected sub-components before merging, so unconnected nearby nodes (a bridge over a street, adjacent dead ends) are never fused. — [OSMnx simplification.py](https://github.com/gboeing/osmnx/blob/main/osmnx/simplification.py)
- osm2streets never collapses roads touching a MapEdge intersection. — [collapse_short_road.rs](https://github.com/a-b-street/osm2streets/blob/main/osm2streets/src/operations/collapse_short_road.rs)

### Inferences
- **Determinism in Porchlight:**
  - Iterate nodes and edges in ascending id. Porchlight's `GraphBuilder` ids are dense list indices, which is good.
  - Sort clusters by min node id and use `Numbers.StableSort`.
  - Never iterate `Dictionary` or `HashSet` for decisions without sorting keys. `RoadGraphOps.Incidence` returns a Dictionary; sort `inc.Keys` before use.
  - Use `DetMath` for angles (Streets already uses `DetMath.Atan2`).
  - Merged node position: use the "most important member" (gate > main×main > degree) rather than a floating centroid, so repeated runs don't drift. If a centroid is used, round to 1/64 tile.
  - RoadSense needs no RNG. If it ever needs one, use its own stage stream `Rng.Stage(seed, "roadsense")`.
- **Idempotence:**
  - Run each operation until no change, or up to `MAX_PASSES = 8`.
  - A test `Run(Run(m)) == Run(m)` on many seeds catches oscillation, e.g. dog-leg slide vs angle fix.
  - Thresholds must have hysteresis gaps. For example, join if < 2.5 tiles, but the angle fix must never move a node to within 2.5 tiles of another.
- **Invariants to keep:**
  - connectivity from the centre;
  - every gate still a degree-1 node at its exact position;
  - planarity (`StreetsStage.CheckCrossings`);
  - no duplicates (`CheckDuplicates`);
  - slopes;
  - bridges (`CheckBridges`, recomputing `ComputeBridges` after geometry edits);
  - airport road;
  - no zero-length edges and no self-loops with fewer than 3 points;
  - polyline endpoints equal node positions;
  - every node referenced by an edge, and node ids dense.

### Gaps
- No published source specifically on idempotence testing of road repair pipelines. The above is engineering inference.

---

## 6. Concrete "RoadSense" stage design for Porchlight

### Takeaway
Insert a pure `RoadSenseStage` between `StreetsStage` and `BlocksStage`. It runs after all road topology exists (connectors, footpaths, pruning, KeepConnected) and before blocks/lots are cut from road polygons, so repairs reshape blocks consistently. It does topology and geometry repair in tile units, then writes per-edge class, speed and lanes and per-node control into new model fields. Traffic.cs then honours them. Every rule below cites the source analogue in sections 1–5. The thresholds are the converted values from those sections.

### Cited Findings
- Pipeline ordering (Streets before Blocks), the stage interface and the per-stage Check exist as described. — local code: /home/user/porchlight/unity/Assets/Porchlight/Runtime/Shared/Gen/Generator.cs
- Scale is 4 m per tile. — local code: Explore/Directions.cs `METRES_PER_TILE = 4`
- Existing helpers to reuse: `Planarize`, `MergePassThrough(keep)`, `Compact`, `SplitEdgeAt`, `ComputeBridges`/`CheckBridges`, `ReachableNodes`, and `StreetsStage.CheckCrossings`/`CheckDuplicates`. — local code: Gen/RoadGraph.cs, Gen/Streets.cs
- Analogues: SUMO join 10 m / parallel 30° / > 5 legs infeasible; OSMnx connected-subcluster split; osm2streets map-edge exclusion and fixed-point; A/B Street rank-based stop signs; SUMO Σ(lanes×speed) TLS rule with a 79 km/h cutoff; TM:PE yield-by-default on priority roads. — sources in section 1

### Inferences (the design)

**Model additions** (Model/Types.cs):
- `enum RoadClass { Arterial, Collector, Local, CulDeSac, Path }`
- `enum JunctionControl { None, Priority /*minor legs yield*/, Stop /*minor legs stop*/, AllWayStop, Signal, Roundabout, DeadEnd, Gate }`
- `RoadEdge` gains `cls`, `speed` (tiles/s), `lanesFwd`, `lanesBack` (0 means one-way), and `bool priority` (major through-pair at its ends).
- `RoadNode` gains `control` and `int[] stopLegs` (edge ids that must stop or yield).
- Keep `kind` (Main/Street/Path) as the *physical* width, and `cls` as the *functional* class. Not every Main needs to be an arterial, which mirrors Thomson's "character" vs continuity.

**Thresholds (tiles, at 4 m/tile)**

| constant | value | basis |
|---|---|---|
| JOIN_DIST | 2.5 | SUMO 10 m |
| MIN_EDGE (non-dead-end edge between two junctions) | 2.5 for streets, 3.5 for mains | ≈ SUMO join, bumped by the 2-tile main width plus a 1-tile corner radius (SUMO default radius 4 m) |
| MIN_ANGLE_ERR / MIN_ANGLE_WARN | 60° / 75° | AASHTO / ITE |
| PARALLEL_REJECT | 30° | SUMO parallel-threshold: don't join clusters whose external legs would be < 30° apart |
| MAX_LEGS | 4 for game junctions (error at 5+; SUMO refuses to join > 5 incoming) | |
| DOGLEG_MERGE | ≤ 2.5 | → make a 4-way |
| DOGLEG_MIN | 9.5 streets / 15 mains | 125 ft / 200 ft |
| BRIDGE_CLEAR | ≥ 2 tiles between a bridge end (`Bridge.from/to`) and any junction node with degree ≥ 3 | inference: room for the 0.9-tile corner curve plus a car length |
| STUB_MIN | Street dead end shorter than `CUL_DE_SAC_LENGTH` (4) with no bulb → remove | existing pruning already approximates this |
| TINY_LOOP | cycle whose total length < 4 × JOIN_DIST (10 tiles), or a face with area < 4 tiles² | collapse |
| SIGNAL_SCORE | see control rules below | |

**Operations, in order** (each loops to a fixed point, max 8 passes; Check after each in debug builds):

0. **Snapshot** the protected node set: gates, the airport lane end, the zoo road end and the centre hub. This is the same `keep` set Streets uses. These are never moved or merged (osm2streets MapEdge rule).
1. **Join near-coincident junctions.**
   - Cluster nodes of degree ≥ 3 that lie within JOIN_DIST *along an edge* (SUMO flood-fill over short edges).
   - Split each cluster into components connected by edges ≤ JOIN_DIST (OSMnx sub-cluster rule), so a bridge over a street never fuses.
   - Reject a cluster if the merged node would have > 4 external legs or two external legs < 30° apart (SUMO feasibility). If rejected, try dropping the lowest-class member (SUMO `tryRemove`).
   - Merge to the position of the highest-priority member. Re-point edges, extending their polylines to the new point, and delete intra-cluster edges.
   - Re-run `Planarize`.
2. **Collapse short edges** between two junctions (length < MIN_EDGE, both ends degree ≥ 3, neither end protected). This is osm2streets `collapse_short_road`: keep the higher-class endpoint, and if a later collapse turns an edge into a self-loop, delete it.
3. **Merge pass-through nodes** (`MergePassThrough`, with the keep set). Only merge where the kinds match (OSMnx rule 5 / SUMO geometry.remove). Also straighten polyline vertices with turns > 99° (SUMO max-angle) where `RoadSegmentOk` allows.
4. **Dog-leg repair.**
   - For each pair of T-junctions on the same through-stroke whose stems leave on opposite sides with offset d:
     - if d ≤ DOGLEG_MERGE, slide the shorter stem's end along the through road to the other T, making a 4-way;
     - if DOGLEG_MERGE < d < DOGLEG_MIN, try sliding the stem whose far end is free (a dead end, or a stem of length < streetSegment) to merge or to reach DOGLEG_MIN, using the Streets clearance tests (LandOk, slope, water) to accept the move;
     - otherwise leave it and mark it `dogleg=true` so control assigns Stop on both stems.
   - Never slide a protected node or a Main stem.
5. **Angle repair.** For each junction leg meeting another leg at < 60°:
   - (a) if the leg is a Street, re-route its last segment to arrive perpendicular. Insert a bend point `MIN_SEGMENT` (1.5) tiles back along the perpendicular, accepted only if it creates no crossing, duplicate or slope violation (a "T-up" realignment, as in Iowa DOT's preferred perpendicular realignment);
   - (b) else, if it's a footpath, allow it;
   - (c) else, warn.
   - 60–75° is a warning only.
6. **Bridge clearance.** If a junction of degree ≥ 3 lies within BRIDGE_CLEAR of a bridge end on any incident edge:
   - move the junction (if it's a Street T) along the main road away from the water by up to 3 tiles;
   - else delete the offending Street stub if it is a dead end;
   - otherwise warn.
   - Recompute bridges afterwards.
7. **Stubs and tiny loops.**
   - Remove Street dead ends shorter than STUB_MIN that aren't cul-de-sacs (Streets' `CulDeSac` logic decides which are). Traffic.cs already refuses to drive them.
   - Collapse cycles shorter than TINY_LOOP that are all Street, by removing the cycle's lowest-betweenness edge, provided connectivity holds.
   - Paths are exempt.
8. **Components.** Keep only the component containing the centre hub (SUMO `keep-edges.components 1`, osm2streets remove_disconnected; Streets already does this via `KeepConnected`, so this re-asserts it after edits). Then `Compact`.
9. **Final `MergePassThrough`, then `Planarize`, then `Compact`**, and recompute `bridges`. If anything changed, loop back to step 1, up to MAX_PASSES. If MAX_PASSES is hit, emit a Check warning listing the oscillating nodes.
10. **Classify** (section 4):
    - strokes (deflection ≤ 30°);
    - Brandes betweenness on lengths with id tie-breaks;
    - arterial = Main on any gate→centre or airport/zoo→centre shortest path; collector = other Main edges, and Street strokes in the top 20% of betweenness that connect two Main nodes; local = the rest; cul-de-sac = edges whose removal disconnects a subtree containing a degree-1 node (graph bridges).
    - Set speed by class (arterial 3.5 tiles/s ≈ 50 km/h, collector 2.8, local 2.1, cul-de-sac 1.4) and one lane per direction.
    - One-way: off by default. If enabled, only alternate parallel local pairs, and revert unless the directed graph is strongly connected (Tarjan).
11. **Junction control** (deterministic, in ascending node id):
    - degree 1 → `DeadEnd` (or `Gate` if protected);
    - degree 2 → `None` (geometry-like; SUMO skips TLS on geometry-like nodes);
    - degree ≥ 3: rank legs by (class, stroke betweenness, `RoadKind`). The **priority pair** is the two highest-ranked legs on the same stroke (best continuation). If the top two ranks aren't on one stroke, there is no priority pair.
    - Score S = Σ over incoming legs of lanes × classSpeed in m/s (SUMO structure). Use classSpeed 13.9 for arterial, 11.1 collector, 8.3 local.
    - **Signal** if: ≥ 3 legs are arterial/collector with ≥ 2 of them arterial, S ≥ SIGNAL_THRESHOLD (choose 45 m/s so a 4-way arterial×collector qualifies), no leg is a cul-de-sac, the node is not within 5 tiles (SUMO tls.join 20 m) of another signal (else join or skip), and the node is not a dog-leg. Cap the number of signals at about 1 per 60 tiles of arterial length so small towns get 0–4.
    - **Roundabout** if: degree 4–5 with all legs arterial/collector, legs evenly spread (max gap between leg headings ≤ 120°), roughly balanced classes (no single priority pair), and a disc of radius 4–6 tiles fits on flat dry land clear of other roads. Gate and centre-hub junctions are good candidates. The geometry change (the ring) is a separate Layout concern, so mark it and let Layout draw it. Otherwise, fall through.
    - **AllWayStop** if: degree ≥ 3, all legs share the same class (A/B Street "all same rank"), and the class is collector or local with ≥ 4 legs, or with 3 legs and no straight-through pair (Y-junction).
    - **Priority/Stop** otherwise: the priority pair has right of way. Other legs get `Stop` if they meet the priority road at < 75°, are dog-legged, or there's a bridge within BRIDGE_CLEAR; else `Priority` (yield) (TM:PE default: joining traffic yields). Local×local 3-way T's can stay `Priority` (yield), with the stem yielding.
    - Never put a stop on an edge shorter than MIN_EDGE: re-check, because step 2 should have removed such edges (A/B Street "extremely short" rule).
12. **Crosswalks.** Place them at every Signal, AllWayStop and Stop junction across each leg, about 1 tile back from the junction (beyond the 0.9-tile corner curve). At Priority junctions, place them only across the minor legs (TM:PE disallows crossings across the priority road by default). Place none at Roundabout entries closer than 1 car length, using 1.5 tiles back instead. The explore view already has sidewalk links "that cross a street (the mouth of one, at a junction)" (`IsoLayout` LinkKey), which are the natural attach points.

**Check() invariants for RoadSenseStage**
- The existing ones re-run: `StreetsStage.CheckCrossings`, `CheckDuplicates`, `CheckRoadSlopes(Main/Street)`, `CheckBridges`, `AirportStage.CheckRoadIn`, gates connected to the centre.
- Topology:
  - no edge with `from == to` and ≤ 2 points;
  - no edge with length < 1e-6;
  - no edge between two degree-≥3 nodes shorter than MIN_EDGE;
  - no two degree-≥3 nodes within JOIN_DIST unless explicitly rejected-as-parallel (log the reason);
  - every node has degree ≥ 1;
  - ids dense;
  - polyline endpoints equal node positions.
- Geometry:
  - no junction leg pair < 60° except where a Path is involved;
  - no unmerged dog-leg < DOGLEG_MERGE;
  - no degree-≥3 node within BRIDGE_CLEAR of a bridge end.
- Config completeness: every edge has `cls` and speed > 0; every node has `control`; Signal and AllWayStop only at degree ≥ 3; `stopLegs` ⊆ incident edges, never including both edges of the priority pair; degree-2 nodes are `None`.
- Directed connectivity (if one-way is enabled): strongly connected over drivable edges.
- Idempotence (test, not runtime): `RoadSense.Run(RoadSense.Run(m))` equals `RoadSense.Run(m)` for seeds 0..N.

**Downstream changes:**
- `Layout.Build` copies `cls`, `speed` and `control` into `RoadStroke`.
- `Traffic` uses per-road speed instead of the CRUISE constant, and at a junction:
  - Stop legs: decelerate to 0 within 1 tile and wait about 1 s, then go if no priority car is within N tiles;
  - Priority (yield) legs: slow to 0.4×, then go if clear;
  - Signal: a fixed two-phase cycle (SUMO defaults 90 s cycle / 31 s green, scaled to game time), offset per node by its id hash for determinism;
  - AllWayStop: first-come by arrival time, ties broken by car index.

### Gaps
- Tile thresholds are conversions of real-world or SUMO metres at 4 m/tile plus judgement. They should be tuned by histogramming defects over many seeds: count of junction pairs within 2.5 tiles, edges < 2.5 tiles, angles < 60°, and dog-legs < 9.5 tiles, before and after.
- The signal and roundabout thresholds for a small game town have no published basis. They are proxies modelled on SUMO's Σ(lanes×speed) rule and A/B Street's rank rule.
- I didn't verify whether repairs in steps 4–6 can violate `CheckRoadSlopes`. Every move must re-run `Raster.RoadSegmentOk` / Streets' `LandOk` before being accepted.

# Realistic towns: research and the to-do list

This covers what real towns are built around, where their shops go, what our
traffic and townsfolk need to look like a living population, and how events
(a match, a concert, a market) could shake things up. It follows on from
[land-use.md](land-use.md), which zones every building as homes, work or both.

Each section has three parts: **where Porchlight was** when this was written
(from the code, before PRs #18–#22; the to-do lists say what has landed since),
**what the research says** (cited), and **the to-do list**. The whole list,
in order, is at the end. Citations: `[P#]` are planning and procedural
generation, `[T#]` traffic, population and events; both lists are at the
bottom. *(unverified)* marks a figure we couldn't trace to its primary source.

The to-do lists are tracked in
[issue #17](https://github.com/joe307bad/porchlight/issues/17); tick items off
there as they land.

Scale: a tile is 4 m, a town about 256 × 256 tiles (1 km square).

---

## 1. Is our town planned like a real one?

### Today

- **One centre.** `Gen/Site.cs:66-87` scores tiles (flat, dry, near a river,
  near the middle of the map). There is always one centre and four gates, one
  in the middle of each edge (`Site.cs:91-92`).
- **Radial main roads.** A\* runs from each gate (and the airport and zoo)
  to the centre, merging where routes overlap (`Gen/Roads.cs`). That makes a
  star with no ring road or bypass.
- **One junction where roads meet, never a knot.** See "Junctions: no
  knots" below.
- **Streets by hierarchical domain splitting** (`Gen/StreetPlan.cs`, Yang et
  al. 2013 [P24]). Blocks are 9 tiles deep and grow up to 40% larger towards
  the edge; the outskirts get courts and cul-de-sacs.
- **Three road kinds only:** `RoadKind { Main, Street, Path }`
  (`Model/Types.cs:41`). Main roads are 8 m and streets 4 m, each with a
  parking lane and sidewalk. There are no collectors, no one-way streets and
  no multi-lane roads.
- **A density gradient from distance alone.** Buildings get rarer, lower and
  set back further from the centre (`Gen/Buildings.cs:69,81`). Towers rise
  linearly towards the middle (`Explore/Layout.cs:265-271`).
- **Missing:** neighbourhoods, schools, parks (only leftover green lots and
  community gardens), parking lots, transit, a second centre, and any sense
  of the town's age.

### Junctions: no knots

Main roads used to meet in knots. Each is pathfound to the centre on its
own, and a later road joins an earlier one wherever is cheapest, often a
tile or three from an existing junction. In one player's town the middle
was a T, a 3-tile stub, and two curved roads looping back to the next
junction: four junctions in 4 tiles, with a lamp post on the island
between them. Real street networks avoid this, and osm2streets cleans the
same shapes out of OpenStreetMap: `CollapseShortRoads` merges a road
shorter than its junctions into one junction, and
`CollapseDegenerateIntersections` removes junctions that only join two
roads [T51]. We borrow the idea, not the code.

What happens now (`Gen/Roads.cs`, `Gen/RoadGraph.cs`):

1. **Routing.** A main road joining another within 4 tiles of one of its
   junctions pays a penalty of 30 (`JOIN_PENALTY`; a turn costs 10). So it
   meets the road at the junction itself, or well clear of it.
2. **Clean-up** (`RoadGraphOps.CollapseShortLinks`), repeated until nothing
   changes:
   - **Doubles.** Of two roads between the same two junctions, the shorter
     is kept (a main road over a street). A road that comes back to its own
     start goes.
   - **Side by side.** Two roads that leave a junction the same way (told by
     where they are 6 tiles out, past any L), within 3.5 tiles of each
     other, are zipped. The one that turns off first is cut back and joined
     to the other where they part, as a T. One that runs beside the other
     all the way to a dead end leaves it by a spur across, as near its end
     as the land allows. Across a knot, two roads laid side by side out of
     the knot's two junctions are zipped the same way, and the knot loses a
     leg.
   - **Steps at the mouth.** A road that turns within 3.5 tiles of a
     junction (`MOUTH`) and then turns back the way it set off steps aside
     in the junction's mouth: that was the east road at Ember Crossing's
     centre. The step is slid further out, or the road turns at once and
     drops the first run, or cuts across with a slant, whichever keeps to
     land and leaves no step at either end. Rounding the corner inside the
     junction itself (under 1 tile) doesn't count.
   - **Jogs.** A junction set a tile or three off the line its roads run on
     moves onto that line, and the others go straight to it. This is kept
     only if it means fewer bends.
   - **Short links.** Two junctions closer than 4 tiles along a road
     (`JUNCTIONS_APART`, a junction's mouth) become one. One stays where it
     is, or failing that they meet half way. The other's roads are swung
     whole to it, bent over their last 4 tiles, or squared off along the
     tiles, whichever is allowed first. A merge must keep every road on land
     a main road may cross. It can't leave two roads meeting under 60°
     (AASHTO's least, [T48]) unless they already did, or a junction with
     more than four legs, or a new step at a mouth. If no merge passes,
     40° is tried as a last resort: a sharp fork beats a knot.
   - **Forks.** If a knot can't become one junction (two roads head off the
     same way, or it would have five legs), the shortest such road goes,
     with any dead end it leaves. That happens only if every gate, the
     airport and the zoo stay joined up.
   - **Leftovers.** A dead end the clean-up left behind goes. Every dead end
     from before (gates, the airport's car park, the zoo's gate) is pinned.
     The centre can move a few tiles to become a merged junction, and its
     stub goes if all it is is a dead end off a junction.
3. **Streets** (`Gen/Streets.cs`): after the dead ends are cut back, two
   streets between the same two junctions become one (the shorter), the
   same as doubles above. A street laid within 4 tiles alongside a main
   road (beside a step in it, say) goes, and the blocks either side become
   one. The main road was routed there on purpose; the street was only
   where the block plan happened to put it.
4. **Checks.** The generator's own checks fail a town (as `Checks towns`
   runs them) with two roads between the same two junctions, a road back
   to its own start, any two junctions under 3 tiles apart along a street
   (`RoadGraphOps.CheckKnots`), or, for main roads (`Knotted`):
   - junctions under 4 tiles apart along the road, or under 4 apart as the
     crow flies (a road can't loop round to dodge the first rule);
   - a link with a bend in it under 8 tiles long (a dog-leg between two
     junctions, as at Ember Crossing's centre);
   - a step within 3.5 tiles of a junction.

How it measured, with the stricter rules (PR #18):
- **CI's `towns 60`:** 60 of 60 pass.
- **`towns 300`:** 292 of 300, as before the stricter rules. The first
  try at them passed 167 (every corner near a junction counted, not just
  steps); zips across knots, spurs to dead ends and the 40° fallback
  brought it back. Two knots are left: check-64's zoo spur (below) and
  check-81, where the land blocks every merge. The other six failures are
  the same as before.
- **`plan`:** all 11 towns pass.
- **Ember Crossing:** its centre is one junction, north, west and south;
  the east road's step is 5 tiles out.

How it measured first (PR #19):
- **CI's `towns 60`:** 60 of 60 pass, with no knots. Before, 9 of them had
  main-road junctions under 4 tiles apart; nothing checked for it then.
- **`towns 300`:** 292 of 300 pass, against 294 before. One knot is left
  (check-64). The zoo's access road joins 3 tiles from another junction,
  and the zoo's own boundary blocks every way of moving it. Two small towns
  now fail pavement checks they passed before (a sidewalk loops where two
  main roads run side by side). Two towns that failed before pass. The
  other five failures were there before.
- **`plan`:** all 11 towns pass. With the centre tidied, Porchlight has 4
  T6 blocks rather than 2, enough for the lot-size check to look at T6. It
  found T6 lots no smaller than T5's (6.5 against 6.4 tiles), which was
  true before too (6.33 against 6.36). The cap on a lot was the larger of
  30 tiles × the zone's scale and twice the smallest lot (16 tiles). For
  T6 (0.55, so 16.5) the floor all but cancelled the scale. The floor is
  now 1.5 × the smallest lot (12). Halving a piece still leaves at least
  the smallest lot, since a cut falls near the middle. T6's scale is now
  0.45 (13.5 tiles), against T5's 0.7 (21).
- **Starter towns:** regenerated. The streets report finds 0 junctions of
  5+ legs, 0 under 60° and 0 pairs under 3 tiles apart in all four.
- **Blue town** (the player's, `seed=35puey`): its centre is one
  crossroads.

### What real towns do

- **A road hierarchy.** FHWA classifies urban roads as arterial, collector or
  local. Of the street length in urban areas, local streets are 67–76%,
  collectors 7–13% each (major and minor), minor arterials 7–12% and
  principal arterials 4–5% [P1]. Downtown arterials are spaced ⅛–½ mile
  (200–800 m) apart [P1]. A 1 km town has one or two arterials crossing near
  the middle, a handful of collectors, and local streets for the rest.
- **Block size depends on era.** Siksna's study of 12 city centres found
  small blocks (~60 × 80 m, about 15 × 20 of our tiles) adapt best [P2].
  Post-war suburbs replaced the grid with "loops and lollipops": long loops
  and cul-de-sacs with poor connectivity (Southworth & Owens; Southworth &
  Ben-Joseph) [P3][P4]. Garden suburbs sit in between, with curving streets
  that still connect.
- **The neighbourhood unit** (Perry, 1929) [P5]:
  - arterials run round its edge, not through it
  - shops sit at its corners, where arterials meet
  - a school and community buildings sit in the middle
  - at least 10% of it is parks
  - it is about a ¼-mile (400 m) walk across *(unverified against the 1929
    text)*

  Our whole town is one or two of these.
- **Central place theory** (Christaller): each good has a *threshold* (the
  fewest customers that keep it open) and a *range* (how far people will go
  for it). Market areas nest as hexagons [P6]. Within a small town, this
  means one high street and a corner shop or two.
- **The transect** (SmartCode): zones T1 natural to T6 urban core, plus
  special districts for things like industry. Each zone fixes lot width,
  setback, height and frontage type [P7]. It's a ready-made parameter table
  for a generator.
- **Growth over time.** Bid-rent (Alonso) gives rings; Hoyt's sectors pull
  growth outward in wedges along roads, rail and water; Harris & Ullman add
  extra centres [P8]. Real towns are layered: an irregular old core, then grid
  additions, then suburban rings. Weber et al. simulate exactly this, a city
  growing through time with parcels changing use [P28].

### To-do

All six are in: `Gen/TownPlan.cs` (eras, the transect table, the ring's radius,
the second centre), `Gen/Plan.cs` (the plan stage: each block's era and zone,
and the neighbourhoods), with the roads, streets, lots and buildings stages
following them. `Checks plan` holds every town to it (below), and CI runs it.

- [x] **Road hierarchy.** Every road edge has a class (`RoadClass`, beside its
  `RoadKind`): main roads are arterials, but for a spur that only leads to the
  airport or the zoo (a collector); the streets laid by the first three levels
  of splitting each piece of land between the main roads are collectors; the
  rest are local. `Checks streets` reports the length shares inside the town,
  and `Checks plan` holds the big dry towns to FHWA's: Porchlight is 15%
  arterial, 14% collector, 70% local [P1].
- [x] **A ring or bypass.** A town at least 320 m in radius gets a ring road at
  half its radius (never nearer than 200 m: 256 m in Porchlight), routed by the
  main roads' A* from point to point round its circle. It crosses the roads out
  from the centre rather than following them. `Checks plan`: from each gate to
  the next on arterials without coming within half the ring's radius of the
  middle, and a small town has no such way round [P1].
- [x] **Era rings.** Each block's `era`: the core within ~0.3 of the radius,
  garden suburbs to ~0.65, post-war beyond (the edges wander). The core is a
  regular grid of ~15 × 20-tile blocks (scaled with the street spacing; 16 × 22
  in Porchlight, median) with no courts or cul-de-sacs (a dead end it can't join
  is cut back, unless it's a street down to the water); the garden suburbs have
  some courts; post-war has the most, and keeps more dead ends as cul-de-sacs
  [P2][P3]. Stored per block so houses can look their age later [P28].
- [x] **Transect zones.** Each block's `transect`, T3–T6, by distance from the
  nearer centre plus a wobble. The town's own settings place the zones
  (`downtown`: T5, with T6 at its heart; `innerCity`: T4), so a key keeps its
  character, and `TownPlan.Rules` is the table: front yards (T3 only), lot size
  (lotMaxSize × 1, 0.85, 0.7, 0.45), house heights, shops and towers (T5–T6),
  gardens and courtyards. It replaces the `innerCity`/`downtown` thresholds in
  the buildings stage and the street scene [P7].
- [x] **Neighbourhood units.** The land the arterials and water cut the town
  into, each piece a neighbourhood about 400 m across: a piece smaller than a
  220 m square joins the smallest one beside it, and one bigger than 1.6 × 400 m
  square or more than 520 m across is split. Each with four blocks or more keeps
  the block nearest its middle as a park (green, nothing built), and gets a
  school (a new building kind, drawn as a hall) near its middle and up to four
  shops on the arterial where an arterial meets another or a collector.
  Porchlight has ten [P5].
- [x] **A second centre** in large towns: just outside the airport's gate,
  towards the town (pulled to 0.45–0.8 of the radius), or failing that its
  bay. It's a T5 centre of its own (half the main one's reach), with offices,
  their towers half as tall [P8]. (No station yet, and the docks come later in
  the pipeline than the plan, so it's the airport or the waterfront.)
- [x] **Junctions, not knots:** main roads meet at one junction, never a
  tangle of junctions a car or two apart (osm2streets' idea [T51]; see
  "Junctions: no knots" above, PR #19). Dog-legs between junctions and
  steps in a junction's mouth too (PR #18). Two test towns' knots (check-64's
  zoo spur, check-81) still can't be untangled.

What `Checks plan` holds, for the county's towns, four big towns and three small
ones: every road classed and the arterials reaching every gate; FHWA's shares
(big dry towns); the ring's way round (big towns) and none in small ones; the
eras in order out from the middle, the core's block size and no cul-de-sacs in
it, and more a block post-war; the zones in order, lots smaller and houses taller
nearer the middle, front yards only in T3 and shops in T3–T4 only at
neighbourhood corners; every neighbourhood under 600 m across, its park and
school nearer its middle than most of it, the park green and the corner shops on
an arterial, and most neighbourhoods kept within the arterials (big dry towns);
the second centre with T5 blocks and offices round it that the main centre alone
wouldn't give; and all of it the same again from the same key.

## 2. Are the shops and businesses in realistic places?

### Today

- `PickKind` (`Gen/Buildings.cs:496-501`) decides by the block's transect and its distance
  from the centre, nothing else:
  - a shop with probability `towerShare` × the block's transect `shopShare`
    (T5–T6, the centre and core)
  - beyond 70% of the radius, a workshop with probability 0.25
  - otherwise a house
- Main-road frontage plays no part. The only use of it anywhere is
  zoning's balancing, which can give a main-road house a shop downstairs
  (`Explore/Zoning.cs:238`).
- Workshops (industry) are scattered around the edge, not near the
  highways, a railway or water.
- No corner stores, supermarket, petrol station, school, fire station or
  car parks are generated. `LandmarkKind.Market` exists
  (`Model/Types.cs:47`) but nothing makes one.

### What real towns do

- **Retail follows thresholds.** From Gibbs's retail primer for the
  Congress for the New Urbanism [P9]:

  | Type | Needs about | Where |
  | --- | --- | --- |
  | Corner store | ~1,000 households (fewer with 15,000+ cars/day passing) | the busiest entrance to a neighbourhood, or by a school or park |
  | Convenience centre | ~2,000 households | a main road at the shared entrance to two neighbourhoods |
  | Neighbourhood centre with supermarket | 6,000–8,000 households | — |
  | Community centre | 50,000+ people | — |

  ICSC's shopping-centre classes say the same [P10].
- **Petrol stations** live on passing traffic, so they sit at arterial
  junctions [P9].
- **Shops cluster.** Competing shops end up side by side (Hotelling) [P11],
  which is why one high street forms. How shoppers choose among shops is
  the **Huff model**: P(shop j) ∝ size_j / distance^λ, with λ ≈ 2, normalised
  over all shops [P13]. It's cheap enough to evaluate for every lot.
- **Industry** goes where transport access is cheapest: highway
  interchanges, rail, water. That puts it at the edge, in a wedge along the
  route out (bid-rent and Hoyt) [P8].
- **Offices** take the highest-value centre, near transit [P8].
- **Civic standards:**
  - **Parks:** about 10 acres per 1,000 residents, the classic NRPA figure;
    today's typical agency has 10.6 [P14][P15].
  - **Schools:** one elementary school per neighbourhood [P5].
  - **Fire:** the ISO rating gives full credit when every building is within
    1.5 road miles (2.4 km) of an engine [P16]. NFPA 1710 sets a 240 s
    travel time [P17]. One station covers our whole town.

### To-do

Done in [town-shops.md](town-shops.md) (#23).

- [x] **A land-value field per tile:** centrality, arterial frontage, corner
  lots and nearness to parks, minus nearness to industry. Use it to choose
  each building's kind and height, replacing distance alone [P29][P13].
  This is Vanegas et al.'s link between behaviour and geometry, without
  UrbanSim.
- [x] **Shops by threshold** (`Zoning.cs`, or a new stage before
  buildings). Done in `Gen/Retail.cs`, a supermarket from 300 households
  for game feel and one a 6,000 beyond:
  - one high street of mixed-use buildings along the arterial through the
    centre [P11]
  - one corner store per ~1,000 households, at collector entrances to
    neighbourhoods [P9]
  - a supermarket only from ~6,000 households, or one per town for game
    feel [P9]
- [x] **Score shop lots with Huff** [P13]: arterial frontage, a corner
  bonus and passing traffic, minus overlap with existing shops' catchments.
  Shoppers in the town's life choose shops by Huff too.
- [x] **A petrol station or garage** at the busiest arterial junction
  outside the centre [P9].
- [x] **Industry in a wedge** along the main road out to the county highway,
  or by the docks and rail, not scattered round the edge [P8].
- [x] **Civic buildings:**
  - [x] a school and a park per neighbourhood (PR #18);
  - [x] a fire station, with the fire engine kept there (and a doctor's
    office);
  - [x] park land at ~10 acres per 1,000 residents (we have residents from
    zoning) [P5][P14][P16]. 4 acres in town: NRPA's 10 counts the
    countryside's parks, and would make a fifth of our towns park;
  - [x] car parks behind the high street, whose bays the town's life parks in.
- [x] **Generate the Market landmark** (the plaza on market day: see §6).
  The landmark is made; its stalls came with §6's calendar (#24).
- [x] **Planning checks** in `Checks zoning`:
  - parks per 1,000 residents
  - households per corner store
  - the fire station's road distance to every lot
  - that a shop fronts an arterial or collector

## 3. What is a town planned around?

### What the research says

Comprehensive plans start from the land and work up: water, flood plain and
slope first; then the corridors (the old road, the railway, the river
crossing); then the market and civic centre; then schools, parks and
utilities [P5][P7]. Historic cores sit at the crossing: the bridge, the
ford, the junction, the station. Growth then follows the corridors outward
[P8].

The procedural-generation literature, and what's cheap to borrow from each:

| Work | Approach | Borrow |
| --- | --- | --- |
| Parish & Müller 2001 (CityEngine) [P18] | streets grown by L-system, guided by population and terrain maps | a population-density map steering density |
| Chen et al. 2008 [P21]; probabletrain/MapGenerator [P23]; Martin Evans [P22] | streets traced along tensor fields | a field per district: grid in one part, radial in another, terrain-following on hills |
| Yang et al. 2013 [P24] (what we use) | hierarchical domain splitting | its layout-quality metrics for `Checks streets` |
| Groenewegen et al. 2009 [P25] | land-use districts for West European vs North American cities, from simple rules | per-town "European" or "American" presets |
| Lechner et al. 2003/2006 [P26][P27] | agent-based growth: developers place uses by land value | growing the town in steps |
| Weber et al. 2009 [P28] | the city simulated through time, parcels changing use | era rings, growth |
| Vanegas et al. 2009/2010 [P29][P30] | an UrbanSim-style behaviour model coupled to geometry | the land-value field |
| Emilien et al. 2012 [P31] | villages laid out from "interest maps" (slope, water, roads) | small-town siting |
| Waddell, UrbanSim [P32][P33] | households and firms choose locations; developers respond | its demand loop, in miniature |
| Watabou, Medieval Fantasy City Generator [P34] | wards round a plaza, typed by rules | ward typing |
| t-mw/citygen [P35]; josauder/procedural_city_generation [P36] | open-source Parish & Müller-style generators | reference code |
| Kelly & McCabe [P19]; Smelik et al. [P20] | surveys | the map of the field |
| Cities: Skylines (I and II) [P37][P38] | zone demand from jobs and households; shops from household spending | the RCI-style demand loop |

### To-do

- [ ] **Site the centre at a reason:** a river crossing, a gate road junction
  or the docks, rather than the best-scoring flat tile. Let the old core be
  irregular (Emilien-style interest maps) [P31][P8].
- [ ] **Corridors first.** Lay the arterials as the town's growth wedges
  (Hoyt) before streets fill in. Industry follows the corridor to the
  highway; better housing follows water or high ground [P8].
- [ ] **Per-town character presets** (European: compact, mixed, irregular;
  American: grid core, zoned suburbs), after Groenewegen et al. [P25].
- [ ] **A demand loop** (Skylines/UrbanSim in miniature): jobs ↔ households
  ↔ shop workers. Place uses until the jobs balance (1.2 a household) and
  the retail thresholds are met, instead of placing everything at once and
  rebalancing afterwards [P37][P32].
- [ ] **Extend `Checks streets`** with Yang et al.'s layout metrics, FHWA
  length shares and arterial spacing [P24][P1].

## 4. What does the driving need to simulate better traffic?

### Today

From `Explore/Traffic.cs`:

- **Count:** `min(70, roadLength/12)` cars, placed at random.
- **No trips.** At each junction a car takes a random road (`:269-273`).
  Shortest-path routing exists (`StreetRoute.cs`) but only for the player's
  directions and chases.
- **Following:** cars stop under a 1.6-tile gap and cruise at 2.4 tiles/s
  (~35 km/h) everywhere, with no IDM and no speed limits (`:212-216`). They
  see only the car ahead on the same edge, nothing across a junction.
- **Junctions:** no signals, stop signs or yielding. Cars pass through each
  other.
- **Lanes:** one each way, and no overtaking.
- **Parking:** parked cars are scenery. Moving cars never park, and no car
  enters or leaves town; the highways have no traffic (`docs/highways.md`).
- **No clock.** Nothing changes with time of day, and nothing models
  congestion.
- **Where it runs:** the server sim (`sim/`) or a host client, from shared
  code with the same seeds.

### What the research says

**Car following and lane changes**
- **IDM** (Treiber, Hennecke & Helbing 2000): a smooth single-lane model
  that produces congestion at bottlenecks [T1]. Reference code is
  movsim/traffic-simulation-de: small, readable JavaScript scenarios (ring,
  on-ramp, road works, junction), easy to port [T2][T3].
- **Gipps** (1981) is used in AIMSUN [T4]. **Krauss**, SUMO's default, is
  collision-free by construction, with a 1 s step and 4.5 m/s² braking [T5][T6].
- **MOBIL** (Kesting, Treiber & Helbing 2007) decides lane changes by how
  much they help the driver versus slow the cars round them. It has a
  "politeness" factor and works on top of any car-following model [T7].

**Junctions and capacity**
- **Saturation flow:** about 1,900 cars per hour of green per lane, ~1.9 s
  between cars at a stop line [T8] *(the HCM itself unread)*.
- **Webster's signal cycle:** C₀ = (1.5L + 5) / (1 − ΣY), from lost time L
  and the critical flow ratios Y [T10][T11].
- **Two-way stop gap acceptance** (HCM 2010 base values) [T12]:
  - left turn from the major road: 4.1 s critical gap, 2.2 s follow-up
  - minor road turning right onto a two-lane road: 6.2 s
- **Roundabout capacity** falls as circulating flow rises [T13].

**Congestion and route choice**
- **BPR:** t = t₀ [1 + 0.15 (v/c)⁴] [T14].
- **Wardrop** user equilibrium: nobody can save time by switching route [T15].
- **Dynamic traffic assignment** sits between static planning models and
  micro-simulation [T16].

**Parking**
- Cruising for parking averaged ~30% of downtown traffic in Shoup's studies,
  where kerb parking is underpriced; a Stuttgart field study measured ~15%
  [T17].
- A/B Street found that leaving parking search out makes driving look
  misleadingly easy [T18].

**Open source to learn from**

| Project | What to take |
| --- | --- |
| SUMO [T19] | MESO, its queue-based mode, is up to 100× faster than micro [T20] |
| MATSim [T21] | a plan per agent and a fast queue sim, iterated to equilibrium |
| CityFlow [T22] | 20× SUMO's speed, multithreaded |
| OpenTrafficSim [T23] | micro and macro simulation mixed |
| TM:PE [T24] (Cities: Skylines mod) | priority signs, timed lights, lane arrows, parking AI |

**A/B Street** (Rust, open source) is the closest match to a game like ours
[T25][T26]. [abstreet.md](abstreet.md) goes through its code and what to port:
- It is a **discrete-event** sim: each agent schedules its next transition
  in a priority queue, so a quiet map costs almost nothing.
- Its retrospective's lessons [T18]:
  - gridlock is the worst failure (bad geometry, over-cautious cars, bad
    signal timing, bad demand)
  - lane changing in a discrete-event sim is hard
  - players rarely miss smooth acceleration
  - full-day regression runs on known maps catch gridlock creeping back

### To-do

Most of this is in with the town's life (PR #19, [town-life.md](town-life.md)).

- [x] **Trips, not wandering.** Every car has an origin and destination (a
  home, a job, a shop, a gate) and is routed over the lanes on free-flow
  travel time (`Life/Router.cs`, not `StreetRoute`). This is the change
  everything else builds on.
- [x] **Junction control by road class** (PR #19: ranked by functional
  class, arterial 3, collector 2, local 1; [town-life.md](town-life.md) §2):
  - [x] equal roads crossing (four legs): all-way stop (MUTCD 2B.07 [T50]);
    a T of equal roads, the stem stops (MUTCD 2B.04)
  - [x] collector × local: two-way stop with HCM gap acceptance (4.1 s /
    6.2 s) [T12]. Drivers who give way wait for a gap by the HCM's critical
    gaps for their turn, and take a shorter one the longer they've waited
    ([town-life.md](town-life.md) §2, gap acceptance).
  - [x] arterial × arterial: signal with its cycle from Webster [T10]. Not
    worked out once when the town is made: every quarter hour each signal
    is retimed from the cars counted through it, never shorter than its
    slowest turn or widest crossing needs, and it still changes early for a
    car waiting with nobody coming the other way (`Life/Signals.cs`).
- [x] **Turns at a comfortable cornering speed:** √(a / curvature) with
  a = 0.32 g (AASHTO side friction at 15 mph [T48]), over a car's length.
  Now 0.38 g, with the limits a tenth over a real town's (see below).
- [x] **Don't block the box:** no car enters a junction unless there's room
  where it's going. A car held 3 minutes is let through, and one held behind
  a player for 15 s turns round (PR #19) [T18].
- [x] **Queue-based server sim (A/B Street / SUMO MESO style):** each lane
  is a queue of cars on intervals, event-driven, with no per-tick physics
  (PR #19: 0.18 ms a town a tick) [T25][T20][T8].
- [ ] **IDM near the camera on the host** [T1][T2]. Keep its entry and exit
  times pinned to the server's queue schedule so the two never disagree.
  Use MOBIL only on multi-lane county highways [T7].
- [x] **Speed limits by road class** (Main 4.3 tiles/s, about 61 km/h;
  streets 2.6, about 37 km/h), slower on bends and turns (PR #19). Real
  towns' 50 and 30 km/h looked like crawling at a game's scale, so the
  limits are a fifth over, and cars corner a little brisker (PR #18).
- [x] **Congestion-aware routing:** BPR edge costs from the last ~15 minutes
  of flow. Re-route a random ~10% of drivers each simulated day, which
  converges towards Wardrop equilibrium cheaply, as MATSim does [T14][T15][T21].
  Routing by the traffic is in (PR #18: each lane's hold-up measured as cars
  leave it, routes costed by it, and held-up drivers taking a quicker way;
  `Life/Congestion.cs`, [town-life.md](town-life.md)). Now a lane costs the
  larger of its measured hold-up and BPR's forecast from the last 15
  minutes' flow, and drivers keep to lanes they know (×0.8) with a tenth
  working their way out afresh each day, so routes settle towards Wardrop
  day on day.
- [x] **Parking as a real step:** kerb and driveway spots (PR #19). Arriving
  cars take a free spot near the door, or hunt for one (jittered, so hunters
  fan out), then walk to the door [T17][T18]. Car parks behind the high
  street too ([town-shops.md](town-shops.md)).
- [ ] **Traffic through the gates:**
  - [x] commuters and through traffic arrive and leave by the gates (PR #19);
  - [ ] the county highways get traffic (`docs/highways.md`).
- [x] **Vehicles with jobs:**
  - [x] buses on a timetable that stop and hold up the lane (PR #19: one
    loop a town, every 20 minutes, ridden by the timetable)
  - [x] delivery vans and taxis, more or fewer by the hour as real
    commercial traffic runs, day and night, out of the rushes (PR #19:
    [town-life.md](town-life.md) §9, [T52])
  - [x] bin lorries by zone and weekday: each neighbourhood on its weekday
    from 7:00, stopping in the lane ([town-life.md](town-life.md) §9)
  - [x] emergency vehicles that make others pull over: ambulances and a fire
    engine on random calls, the ambulances from beyond the gates and the fire
    engine from the town's fire station

  The models already exist in `Catalog.cs`.
- [x] **A gridlock check:** `Checks traffic-day` runs a full simulated day
  of each starter town and two seeds, and fails on anyone stuck, a trip over
  3 hours, too many failed trips, or a day that doesn't repeat. CI runs it,
  with `Checks life` (PR #19) [T18].
- [x] **Townsfolk as obstacles:** cars wait for walkers on their turn, and
  walkers cross at junctions with leave, or mid-block at a cost (PR #19).
  Players in the road stop cars too, on a lane or in a junction.

## 5. What do the townsfolk need to look like a real population?

### Today

From `Explore/Actors.cs` and `Walking.cs`:

- **Count:** `min(70, sidewalkLength/12)` people, placed at random. The
  only extras are drivers put out of cars someone took.
- **A pure random walk.** At a sidewalk's end they take a random link (8%
  turn back), pause for 1–4 s now and then, and cross the street at a random
  moment, straight across, ignoring cars (`Walking.cs:473-524`).
- **Too fast:** their pace is 0.9–1.5 tiles/s, which is **3.6–6 m/s**, a
  jog. People walk at about 1.3–1.4 m/s.
- **No identity:** no home, job or schedule. They never go indoors, never
  react to night, and never come from or go to parked cars.
- **Already there:** looks (14 characters, random scale) and social moments
  with the player (stopping to look, following, being sent off).

### What the research says

**Who lives here**
- **Synthetic population.** Iterative proportional fitting fits sample
  households to census totals (Beckman et al. 1996) [T27]; it has a known
  zero-cell problem [T28]. ActivitySim's **PopulationSim** balances households
  across geography levels [T29].
- For one small town, a fixed mix of household types is enough.

**What they do all day**
- **Activity-based demand:**
  - **ActivitySim** (open source) runs a chain: household → daily pattern
    (work/school, other, home) → tours → destination and time → mode [T30].
  - **CEMDAP** [T31] and **TASHA** [T32] organise the day into tours and
    household schedules.
  - **MATSim** gives each agent a plan of activities with end times [T21].
- **Trips a day:** 3.37 per person in the 2017 NHTS [T33]. The 2022 NHTS
  gives about 2.1–2.3, with a method change and after COVID; workers make
  2.38 and non-workers 1.71 [T34]. NCHRP 716 has transferable trip rates,
  gravity-model friction factors, time-of-day curves and occupancy [T35].
- **When they're where:** 87% of full-time workers work on an average
  weekday and 29% on a weekend day (ATUS 2024) [T36]. Work clusters 9–5 with
  a lunch dip [T37].

**How they move**
- **Social force** (Helbing & Molnár 1995) produces lanes and other
  self-organisation in crowds [T38].
- **ORCA/RVO2** (Apache-2.0 C++, with a C# port) shares avoidance between
  agents and is cheap [T39].

**What games do**
- **Hitman: Absolution:** crowds of 1,200 at 30 fps, each with a small brain
  [T40].
- **Assassin's Creed Unity:** "AI recycling" swaps cheap and detailed
  characters unseen, keeping thousands persistent [T41].
- **A/B Street:** off-screen agents are just events in a queue [T25].

### To-do

Most of this is in with the town's life (PR #19, [town-life.md](town-life.md)).

- [x] **Fix walking pace:** 0.22–0.34 tiles/s (2–3 mph), with 5% joggers
  (PR #19).
- [x] **Households** for every home zoning makes, seeded from the town:
  single, couple, family with 1–3 children, retirees. Use a fixed mix, with
  PopulationSim-style rebalancing only if totals drift [T27][T29]. This
  turns zoning's `residents` into actual people. PR #19 makes the people (1.2
  working residents a home, by the proletariat robot). Now each building
  is filled with households from a Census-rounded mix, each member with a
  role and an age; filling to zoning's figure keeps the totals, so no
  rebalancing is needed (`Life/Population.cs`).
- [x] **A job for every worker**, by a doubly-constrained gravity model:
  P(job j | home i) ∝ jobs_j · e^(−β·t_ij), filling each job once [T35].
  - The town's surplus jobs (~0.2 a household) go to commuters from
    neighbouring towns, which puts peaks on the county highways.
  - Some residents work out of town.

  Done with NCHRP 716's home-based-work friction, Furness balancing and the
  gates as zones (15 minutes on to the town beyond), the times scaled to a
  real town of the same people. Commutes in town come out 3–5% shorter
  than jobs dealt at random: the friction is shallow.
- [x] **A daily plan per person (ActivitySim-style)** [T30]. PR #19: work
  (with lunch and shops on the way home), school, errands and visits, about
  3 trips a day. Weekends, shifts and school drop-offs are done (#24,
  [town-calendar.md](town-calendar.md)).
  - the day: work, school or home, plus 0–2 stops (shop, leisure, errand),
    for ~3 trips a day [T33]
  - workers: shift starts around 8:00 ± 60 min, plus some retail, evening
    and night shifts; weekend work at ~29% of weekdays [T36]
  - school: bell times, with parents dropping children off on the way to
    work
- [x] **A town clock** (server-side, shared): real time, county-wide (UTC−5)
  (PR #19). The sky's night still comes from the client. Departure = activity start − expected travel time − a buffer, so
  rush hours and their jams follow from the plans.
- [x] **Mode choice:** walk under 60 tiles (240 m), drive if their car is
  parked where they are, bus if the timetable beats walking (PR #19).
- [x] **Level of detail** [T41][T25]. PR #19: everyone is simulated as
  events, and only those out are drawn, through fixed slots. No RVO2 yet.
  - off-screen, a person is their plan plus where they are in it, with
    departures and arrivals as events on the server
  - near a player, the host gives them a body that walks or drives
  - RVO2 for sidewalk avoidance [T39]; social force only for dense crowds at
    events [T38]
- [x] **Regulars.** Keep each person's home and job across days (PR #19), so the
  same faces walk to the same bakery. Players notice that more than
  accuracy. Light needs (hunger, fun) can choose errands later.
- [x] **Going indoors:** people enter and leave buildings through their
  doors, and parked cars pick up and drop off their drivers (PR #19).
- [x] **Manage views:**
  - [x] people by activity: who's indoors in each building, out, or out of
    town (PR #19)
  - [x] trips by hour (PR #19: the busiest hour and each mode's share)
  - [x] each street's flow and hold-ups (PR #18: Manage's congestion slider,
    the last 24 hours, with each hour's worst streets drawn on the map)
  - [x] the junctions that jam (PR #19: the slow junctions, as a count)

## 6. Randomness: matches, concerts and the logistics they bring

### Today

- **Night** is a toggle any player flips (`CityRoom.ts`, `MSG_NIGHT`).
  Streetlamps come on, but traffic and townsfolk carry on as before.
- **Weather** (rain, snow) is a client-side look only: not shared, no effect.
- **Missions** can empty the streets (`Deserted`), close a race course, or
  send police after you. These are the only events that change the street
  scene.
- **Special places** (the zoo, airport, docks, plaza, church, hall) draw
  nobody.

### What the research says

**FHWA, *Managing Travel for Planned Special Events*** [T42][T43]:
- **Event types:**
  - discrete events at a venue (a match, a concert) end all at once, giving
    a sharp departure peak
  - reserved seats give a sharper arrival peak
  - continuous events (fairs, markets) have no sharp peaks
- **Demand:** attendance × share by car = person trips, ÷ occupancy =
  vehicles, studied within 0.5–1 mile of the venue.
- **Tools:**
  - staggering arrivals and departures
  - car-pool incentives
  - road closures and detours
  - shuttles
  - police directing traffic at junctions

**Arrivals and incidents**
- Good highway access makes arrivals peak sharply just before kick-off (TRR
  1735) [T44].
- One NBA case: 68% of arrivals predicted and 63% observed in the last hour
  *(single case, secondary)* [T45].
- Event car occupancy of ~2.5–3 is common but *unverified*; FHWA leaves it
  local [T43].
- **Non-recurrent congestion:** crashes, road works and weather are best
  modelled as capacity cut for a time [T16]. IDM shows a bottleneck as a
  local capacity drop [T1].

**How games pace events**
- **Left 4 Dead's AI Director** runs build-up → peak → fade → relax cycles
  [T46].
- **RimWorld's storytellers** alternate "on" and "off" stretches (Cassandra
  ~8.5 major events a year), scaling them to the colony's size [T47].

### To-do

- [x] **A seeded calendar per town:** derive each day's schedule from
  hash(town seed, day), so the server and every client agree without
  syncing. Done in [town-calendar.md](town-calendar.md) (#24): weekdays and
  weekends, the US holidays, rosters and shifts.
  - [x] weekly market in the plaza (Saturday 8–13, 9–12 in winter): stalls,
    stallholders and shoppers (#24)
  - [x] bin day by district (#19; now read from the calendar)
  - [x] school terms and bell times: 180 days, breaks and the school run (#24)
  - a monthly match or concert if the town has a venue
  - an annual festival or parade
- [ ] **A venue:** a sports ground or arena on the edge of larger towns, on
  an arterial, with a car park. Use the ICSC/park standards for its land
  [P10][P14].
- [ ] **Event demand from the FHWA formula** [T43]:
  - vehicles = attendance × car share ÷ occupancy (2.5 to start, tunable)
  - **discrete events:** ~60–70% arrive in the last hour, with a tail from 2
    hours before [T44][T45]; departures surge over 30–60 minutes from the
    final whistle *(assumed)*
  - **continuous events:** steady arrivals and departures through opening
    hours [T42]
  - **who comes:** mostly townsfolk whose leisure stop becomes the event,
    plus visitors from the county highways, which fills the highways
- [ ] **Event logistics:**
  - a parade closes a list of streets for a time, so routing sees them
    blocked
  - a big match gets a special signal plan or police directing junctions
  - overflow parking in fields or the school car park
  - an optional shuttle loop
  - all of it shows as diversions and queues
- [ ] **Incidents as a Poisson process,** seeded per day: crashes at a rate
  per vehicle-km *(to tune)*, each closing a lane for an exponentially
  distributed time [T16]. Road works use the same mechanism, scheduled for
  days at a time.
- [ ] **Shared weather:** weather becomes a server state that cuts speeds
  and capacity (rain −10–20%, *assumed*) and thins the townsfolk.
- [ ] **A director (later):** track the town's "tension" from congestion
  and recent events. Lean random events towards quiet stretches and avoid
  stacking them, as RimWorld and L4D do [T46][T47]. Keep it seeded from
  (town seed, day) so it replays.
- [ ] **Events in Manage:** the calendar, today's events, closures, and the
  arrival and departure curves against the traffic actually seen. The
  calendar, today's events and the market are shown (#24); closures and the
  curves are left.

---

## The whole list, in order

Each step stands on the one before. Start with the cheap ones. Progress is
tracked in [issue #17](https://github.com/joe307bad/porchlight/issues/17).

1. **Cheap fixes now:** walking pace (§5) and speed limits (§4) are done
   (#19); the road hierarchy tags and their check (§1) are done (#18).
2. **A town clock** shared by the server and clients (§5): done (#19).
   Night still comes from the client's sky.
3. **People:** daily plans are done (#19); household make-up and jobs by
   gravity model (§5) are done (#22). Weekends, shifts and school drop-offs
   are done (#24).
4. **Trips:** cars driving those plans, and traffic through the gates, are
   done (#19). Traffic on the highways (§4) is left.
5. **Junction control** (stops and signals), don't-block-the-box and the
   gridlock check (§4): done (#19). Gap acceptance and Webster cycles are
   done (#22).
6. **Parking as a trip step**, with going indoors (§4, §5): done (#19).
7. **Queue-based server traffic** (§4): done (#19). Congestion-aware
   re-routing is done (#18, BPR and habits in #22). IDM near players is left.
8. **Better towns:** era rings, transect zones, neighbourhoods with their
   schools and parks, the ring road and a second centre are done (§1, #18).
   The land-value field, shops by threshold, industry wedges and the other
   civic buildings (§2) are done (#23, [town-shops.md](town-shops.md)); §3
   is left.
9. **The calendar and events:** the calendar and market day are done (#24,
   [town-calendar.md](town-calendar.md)); a venue with match days, parades,
   incidents and shared weather are left (§6).
10. **Manage** shows each layer as it lands. Activities, trips by hour and
    jams are done (#19); street flows are done (#18); the calendar and the
   market are done (#24). Other events are left.

---

## References

### Planning and procedural generation

- **[P1]** FHWA, *Highway Functional Classification Concepts, Criteria and
  Procedures* (2013), §3:
  https://www.fhwa.dot.gov/planning/processes/statewide/related/highway_functional_classifications/section03.cfm
- **[P2]** Siksna, "The evolution of block size and form in North American
  and Australian city centres", *Urban Morphology* 1 (1997):
  https://journal.urbanform.org/index.php/jum/article/view/4048
- **[P3]** Southworth & Ben-Joseph, "Reconsidering the cul-de-sac", *Access*
  24: https://web.mit.edu/ebj/www/doc/culdesacs.pdf
- **[P4]** Marshall, *Streets and Patterns* (Southworth & Owens typology):
  https://people.utm.my/lylai/wp-content/uploads/sites/630/2017/06/Stephen_Marshall_Streets_and_Patterns_The_StrucBookFi.org_.289.pdf
- **[P5]** Perry, "The Neighborhood Unit", *Regional Survey of New York and
  its Environs* vol. 7 (1929); summary:
  https://en.wikipedia.org/wiki/Neighbourhood_unit
- **[P6]** Christaller's central place theory:
  https://hypergeo.eu/christaller-model-of/?lang=en ·
  https://www.geog.leeds.ac.uk/people/a.turner/projects/medalus3/cpt.htm
- **[P7]** Duany, Sorlien & Wright, *SmartCode* v9.2, Center for Applied
  Transect Studies: https://transect.org/codes.html
- **[P8]** Urban structure models (Burgess, Hoyt, Harris & Ullman, Alonso):
  https://en.wikipedia.org/wiki/Urban_structure
- **[P9]** Gibbs, "A primer on retail types and urban centers", CNU (2007):
  https://www.cnu.org/publicsquare/primer-retail-types-and-urban-centers
- **[P10]** ICSC, *U.S. Shopping-Center Classification*:
  https://www.icsc.com/uploads/research/general/US_CENTER_CLASSIFICATION.pdf
- **[P11]** Hotelling, "Stability in Competition", *Economic Journal* 39
  (1929): https://doi.org/10.2307/2224214
- **[P13]** Huff model:
  https://www.esri.com/news/ArcUser/1003/files/huff.pdf · calibration:
  https://esri.com/library/whitepapers/pdfs/calibrating-huff-model.pdf
- **[P14]** NRPA (Lancaster 1983) park standards as applied, Little Rock:
  https://LittleRock.gov/!userfiles/editor/docs/parks/chapter4.pdf
- **[P15]** NRPA, *2024 Agency Performance Review*:
  https://www.NRPA.org/siteassets/research/2024-agency-performance-review.pdf
- **[P16]** ISO PPC, *Criteria for deployment analysis of companies*:
  https://www.isomitigation.com/ppc/technical/criteria-for-deployment-analysis-of-companies/
- **[P17]** NFPA 1710 summary (IAFF):
  https://www.iaff.org/wp-content/uploads/English_Version_-_NFPA_1710_standards_DFSR_Summary_2022_new.pdf
- **[P18]** Parish & Müller, "Procedural Modeling of Cities", SIGGRAPH 2001:
  https://history.siggraph.org/?p=116923
- **[P19]** Kelly & McCabe, "A Survey of Procedural Techniques for City
  Generation", *ITB Journal* 14 (2006):
  https://citygen.pages.dev/2006/11/Procedural-City-Generation-Survey
- **[P20]** Smelik et al., "A Survey on Procedural Modelling for Virtual
  Worlds" (2014):
  https://www.cs.princeton.edu/courses/archive/fall16/cos526/papers/smelik14.pdf
- **[P21]** Chen et al., "Interactive Procedural Street Modeling", SIGGRAPH
  2008: https://web.engr.oregonstate.edu/~zhange/images/street_sig08.pdf
- **[P22]** Martin Evans, "Procedural Generation for Dummies: Roads" (2015):
  https://martindevans.me/game-development/2015/12/11/Procedural-Generation-For-Dummies-Roads/
- **[P23]** probabletrain/MapGenerator: https://github.com/probabletrain/mapgenerator
- **[P24]** Yang, Wang, Vouga & Wonka, "Urban Pattern: Layout Design by
  Hierarchical Domain Splitting", *ToG* 32(6) (2013):
  https://peterwonka.net/Publications/pdfs/2013.SGA.Yongliang.UrbanPattern.pdf
- **[P25]** Groenewegen, Smelik, de Kraker & Bidarra, procedural city land
  use, Eurographics 2009:
  https://diglib.eg.org/items/ce0c30db-a64c-4320-a3b3-704a45484a3c · thesis:
  https://graphics.tudelft.nl/Publications-new/2009/Gro09/Thesis-SGroenewegen-lowres.pdf
- **[P26]** Lechner, Watson, Wilensky & Felsen, "Procedural City Modeling"
  (2003): https://ccl.northwestern.edu/testsite/rp/cities/papers/cities.tr.pdf
- **[P27]** Lechner et al., "Procedural Modeling of Urban Land Use" (2006):
  https://history.siggraph.org/?p=139525
- **[P28]** Weber, Müller, Wonka & Gross, "Interactive Geometric Simulation of
  4D Cities", *CGF* 28(2) (2009):
  https://diglib.eg.org/handle/10.2312/CGF.v28i2pp481-492
- **[P29]** Vanegas, Aliaga, Beneš & Waddell, "Interactive Design of Urban
  Spaces using Geometrical and Behavioral Modeling", *ToG* 28(5) (2009):
  https://www.cs.purdue.edu/homes/bbenes/papers/Vanegas09ToG.pdf
- **[P30]** Vanegas et al., "Modelling the Appearance and Behaviour of Urban
  Spaces", *CGF* (2010):
  https://www.cs.purdue.edu/cgvlab/www/resources/papers/Vanegas-CGF-2010-Modelling_the_Appearance_and_Behaviour_of_Urban_Spaces.pdf
- **[P31]** Emilien et al., "Procedural Generation of Villages on Arbitrary
  Terrains", *The Visual Computer* 28 (2012):
  https://perso.liris.cnrs.fr/egalin/Articles/2012-villages.pdf
- **[P32]** Waddell, "UrbanSim", *JAPA* 68(3) (2002):
  https://nature.berkeley.edu/~penggong/PDFpapers/UrbanSim-JAPA.pdf
- **[P33]** UDST/urbansim: https://github.com/UDST/urbansim
- **[P34]** watabou/TownGeneratorOS: https://github.com/watabou/TownGeneratorOS
- **[P35]** t-mw/citygen: https://github.com/t-mw/citygen
- **[P36]** josauder/procedural_city_generation:
  https://josauder.github.io/procedural_city_generation
- **[P37]** Cities: Skylines zoning and demand (community wiki):
  https://community.fandom.com/w:c:skylines:Zoning
- **[P38]** Paradox, *Cities: Skylines II* dev diary, "Economy part one":
  https://www.paradoxinteractive.com/games/cities-skylines-ii/news/dev-diary-economy-part-one

### Traffic, population and events

- **[T1]** Treiber, Hennecke & Helbing, "Congested traffic states in
  empirical observations and microscopic simulations" (IDM), 2000:
  https://arxiv.org/abs/cond-mat/0002177
- **[T2]** movsim/traffic-simulation-de: https://github.com/movsim/traffic-simulation-de
- **[T3]** Treiber & Kesting, an open-source microscopic traffic simulator:
  https://arxiv.org/abs/1012.4913
- **[T4]** Gipps, "A behavioural car-following model", *TR-B* 15(2) (1981):
  https://ideas.repec.org/a/eee/transb/v15y1981i2p105-111.html
- **[T5]** Krauss, PhD thesis (1998): https://elib.dlr.de/8380/
- **[T6]** SUMO car-following models: https://sumo.dlr.de/docs/Car-Following-Models/index.html
- **[T7]** Kesting, Treiber & Helbing, "General lane-changing model MOBIL",
  *TRR* 1999 (2007): https://journals.sagepub.com/doi/10.3141/1999-10
- **[T8]** Saturation flow (U. Idaho NIATT):
  https://www.webpages.uidaho.edu/niatt_labmanual/Chapters/signaltimingdesign/theoryandconcepts/CapacityAndSaturationFlowRate.htm
- **[T9]** FHWA *Signal Timing Manual*: https://ops.fhwa.dot.gov/publications/fhwahop08024/chapter3.htm
- **[T10]** Webster cycle length (U. Idaho NIATT):
  https://www.webpages.uidaho.edu/niatt_labmanual/Chapters/signaltimingdesign/theoryandconcepts/CycleLengthDetermination.htm
- **[T11]** Survey of traffic signal control: https://arxiv.org/pdf/1904.08117
- **[T12]** HCM 2010 two-way stop values, via SwashSim:
  https://swash17.github.io/SwashSim/ChapterUnsignalized.html
- **[T13]** FHWA roundabout capacity: https://highways.dot.gov/media/11441 ·
  NCHRP 572: https://rosap.ntl.bts.gov/view/dot/49387
- **[T14]** BPR volume-delay function: https://arxiv.org/pdf/2011.11121
- **[T15]** Wardrop, "Some theoretical aspects of road traffic research"
  (1952): https://people.irisa.fr/Nicolas.Markey/PDF/Papers/pice1(3)-War.pdf
- **[T16]** TRB E-C153, *Dynamic Traffic Assignment: A Primer*:
  https://trid.trb.org/view/1112932 · FHWA DTA guide:
  https://ops.fhwa.dot.gov/publications/fhwahop13015/index.htm
- **[T17]** Shoup, "Cruising for parking", *Transport Policy* 13(6) (2006):
  https://escholarship.org/content/qt0xc27671/qt0xc27671.pdf
- **[T18]** A/B Street retrospective:
  https://a-b-street.github.io/docs/project/history/retrospective/index.html
- **[T19]** Lopez et al., "Microscopic Traffic Simulation using SUMO", ITSC
  2018: https://elib.dlr.de/124092/ · https://github.com/eclipse-sumo/sumo
- **[T20]** SUMO MESO: https://sumo.dlr.de/docs/Simulation/Meso.html
- **[T21]** Horni, Nagel & Axhausen, *The Multi-Agent Transport Simulation
  MATSim* (2016, open access): https://ubiquitypress.com/site/books/10.5334/baw
  · https://github.com/matsim-org/matsim-libs
- **[T22]** CityFlow: https://arxiv.org/abs/1905.05217 ·
  https://github.com/cityflow-project/CityFlow
- **[T23]** OpenTrafficSim: https://opentrafficsim.org
- **[T24]** TM:PE: https://github.com/CitiesSkylinesMods/TMPE
- **[T25]** A/B Street, discrete-event traffic sim:
  https://a-b-street.github.io/docs/tech/trafficsim/discrete_event/index.html
- **[T26]** A/B Street: https://github.com/a-b-street/abstreet
- **[T27]** Beckman, Baggerly & McKay (1996), synthetic populations, via Guo &
  Bhat: https://www.caee.utexas.edu/prof/bhat/ABSTRACTS/SPG_Guo_Bhat.pdf
- **[T28]** The zero-cell problem in IPF, *JASSS* 20(4):
  https://jasss.soc.surrey.ac.uk/20/4/16.html
- **[T29]** PopulationSim: https://github.com/ActivitySim/populationsim
- **[T30]** ActivitySim: https://github.com/ActivitySim/activitysim ·
  https://activitysim.github.io/activitysim
- **[T31]** Bhat et al., CEMDAP:
  https://www.caee.utexas.edu/prof/bhat/ABSTRACTS/CEMDAP_revised3.pdf
- **[T32]** Miller, TASHA and activity-based models (workshop, 2025):
  https://tmg.utoronto.ca/wp-content/uploads/2025/09/ABM-Workshop_EJMiller_UNSW_Sept-02-25.pdf
- **[T33]** FHWA, *2017 NHTS Summary of Travel Trends*:
  https://www.fhwa.dot.gov/policyinformation/documents/2017_nhts_summary_travel_trends.pdf
- **[T34]** 2022 NHTS: https://rosap.ntl.bts.gov/view/dot/73764
- **[T35]** NCHRP Report 716, *Travel Demand Forecasting: Parameters and
  Techniques*: https://nap.nationalacademies.org/catalog/14665/travel-demand-forecasting-parameters-and-techniques
- **[T36]** BLS, American Time Use Survey charts: https://www.bls.gov/charts/american-time-use
- **[T37]** ATUS, work by hour of day (archived):
  https://webarchive.library.unt.edu/web/20120916053335/http://www.bls.gov/tus/charts/chart17.pdf
- **[T38]** Helbing & Molnár, "Social force model for pedestrian dynamics"
  (1995): https://arxiv.org/abs/cond-mat/9805244
- **[T39]** RVO2 (ORCA): https://github.com/snape/RVO2 · https://gamma.cs.unc.edu/RVO2/
- **[T40]** "Crowds in Hitman: Absolution", GDC 2012:
  https://gdcvault.com/play/1015315/Crowds-in-Hitman
- **[T41]** "Massive Crowd on Assassin's Creed Unity: AI Recycling", GDC
  2015: https://www.gdcvault.com/play/1022141/Massive-Crowd-on-Assassin-s
- **[T42]** FHWA, *Managing Travel for Planned Special Events*
  (FHWA-OP-04-010): https://ops.fhwa.dot.gov/publications/fhwaop04010/toc_table.htm
- **[T43]** Same, demand analysis:
  https://ops.fhwa.dot.gov/publications/fhwaop04010/chapter3_05.htm
- **[T44]** Baseball stadium access and arrival times, *TRR* 1735:
  https://doi.org/10.3141/1735-12
- **[T45]** Fehr & Peers, sports venue traffic (secondary):
  https://www.fehrandpeers.com/?p=822298
- **[T46]** Booth, "The AI Systems of Left 4 Dead" (Valve, 2009):
  https://cdn.fastly.steamstatic.com/apps/valve/2009/ai_systems_of_l4d_mike_booth.pdf
- **[T47]** RimWorld AI storytellers: https://rimworldwiki.com/wiki/AI_Storytellers

- **[T48]** AASHTO, *A Policy on Geometric Design of Highways and Streets*
  ("Green Book"), 7th ed. (2018), Table 3-7: side friction factor 0.32 at
  15 mph, via the Iowa DOT Design Manual, Chapter 2B:
  https://ia.iowadot.gov/design/dmanual/2B.pdf
- **[T49]** FHWA, *MUTCD* (2009), Chapter 4C, "Traffic Control Signal
  Needs Studies": Section 4C.01 lists the warrants; 4C.02, Warrant 1
  (Table 4C-1, 500 and 150 vehicles an hour):
  https://mutcd.fhwa.dot.gov/htm/2009/part4/part4c.htm
- **[T50]** FHWA, *MUTCD* (2009), Chapter 2B: Sections 2B.04 "Right-of-Way
  at Intersections", 2B.06 "STOP Sign Applications", 2B.07 "Multi-Way Stop
  Applications": https://mutcd.fhwa.dot.gov/htm/2009/part2/part2b.htm
- **[T51]** A/B Street's junction control: stop signs by road rank,
  `map_model/src/objects/stop_signs.rs` (`ControlStopSign::new`); signal
  plans, `map_model/src/make/traffic_signals/mod.rs` (`four_way_two_stage`,
  `three_way`); where signals go, osm2streets' `IntersectionControl`, from
  OpenStreetMap: https://github.com/a-b-street/abstreet ·
  https://github.com/a-b-street/osm2streets
- **[T52]** Batterman, S., Cook, R. & Justin, T. (2015). Temporal variation
  of traffic on highways and the development of accurate temporal
  allocation factors for air pollution analyses. *Atmospheric Environment*
  107, 351–363. doi:10.1016/j.atmosenv.2015.02.047. Table 7: hourly
  allocation factors, weekday, for total and commercial traffic (Detroit
  metro) and the SMOKE national default. Measured on freeways; the paper
  notes arterials and collectors weren't studied. We use the commercial
  weekday column for the vans out each hour.
  https://pmc.ncbi.nlm.nih.gov/articles/PMC4380130

**Unverified or secondary:**
- the Green Book's Table 3-7 itself (read through Iowa DOT's copy) [T48]
- MUTCD 2B.07's numeric criteria (only its Support and opening Guidance read) [T50]
- Perry's ¼-mile and 160-acre figures [P5]
- the IDM default parameters
- 1,900 vehicles/hour from the HCM itself [T8]
- event car occupancy and the departure surge length [T43]
- the Cities: Skylines figures, which come from community sources [P37]
- the TASHA 2003 original [T32]
- the roundabout capacity constants [T13]

Reilly's law and Sugarscape are mentioned in the research but have no
verified link, so they're left out of the list.

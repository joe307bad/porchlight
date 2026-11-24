# Shops and services in realistic places

What a building is used to depend only on how far it stood from the middle of
town. Now it comes from the land under it, the way real towns sort
themselves out:

- the shops stand on the dearest land and along the main road through the
  centre;
- industry sits in a wedge out along the road to the county highway;
- the town gets a supermarket, a petrol station, corner stores, a fire
  station, a doctor's office, car parks behind the high street, and enough
  park land for its people.

This covers §2 of [city-research.md](city-research.md), whose citations
(`[P#]`) this doc uses, and its tracking issue
[#17](https://github.com/joe307bad/porchlight/issues/17).

Code: `Gen/Land.cs` (the land stage), `Gen/Retail.cs` (the shops and
services), `Gen/Buildings.cs` (each building's kind), and in the town's life
`Life/People.cs` (where people shop), `Life/Services.cs` (the fire engine) and
`Life/RoadNet.cs` (car-park bays). Judge changes with:

```
dotnet run -c Release --project "unity/Tools~/Checks" -- zoning     # the planning checks below, for each town
dotnet run -c Release --project "unity/Tools~/Checks" -- plan       # §1's checks, which allow the high street and corner stores
dotnet run -c Release --project "unity/Tools~/Checks" -- services server/towns   # the fire engine goes out from its station
```

## The land stage

A new generator stage, `land`, runs between the lots and the buildings. It's
pure, like every stage: the same key gives the same land.

### The industrial wedge

Industry goes where transport is cheapest: along the main road to a highway,
by the docks or the railway. Hoyt's sector model puts it in a wedge out from
the centre, not in a ring [P8]. Each of our towns has four gates, each one
the start of a county highway. The wedge points at one of them:

- it spans 25° either side of the line from the centre to that gate;
- it starts 55% of the way out from the centre;
- the gate is the one with the most dry land in the wedge (no lakes, airport
  or zoo), scored half again by how far the wedge points from the town's
  second centre (or its airport), where the offices are.

Only a big town (80 tiles in radius or more, the size that gets a ring road
and a second centre) has a wedge. A village's few workshops stay scattered
round its edge as before.

### Land value

Every tile gets a value from 0 to 1 (`TownModel.landValue`):

| Part | Weight | Reach |
| --- | --- | --- |
| a base | 0.05 | |
| centrality: nearness to the centre, or 0.8 of nearness to the second centre | 0.45 | the town's radius (the second centre's: half of it) |
| main-road frontage | 0.2 | 6 tiles |
| collector frontage | 0.1 | 4 tiles |
| a corner: where main roads meet, or a main road meets a collector | 0.1 | 8 tiles |
| a park nearby | 0.1 | 12 tiles |
| inside the industrial wedge | −0.3 | |

Each "reach" term is 1 at the road, corner or park and falls in a straight
line to 0 at the reach. This is Vanegas et al.'s link between how people
behave and the town's geometry, done without UrbanSim [P29]. In the starter
towns, houses stand on land worth about 0.25, shops about 0.45, and workshops
about 0.05.

### Park land

Each neighbourhood already has a park block near its middle (PR #18). Once
the buildings stand, the buildings stage keeps more blocks green
(`LandStage.MoreParks`) until the town has **4 acres of park for every 1,000
residents** (`LandStage.PARK_ACRES`). The residents are counted from the
buildings as zoning will count them, at 2.5 a household, less those on any
block turned to park. The blocks furthest from any park go first. Each must be
of a park's size, have homes on it, lie outside the centre (T5 and T6) and the
industrial wedge, and never hold the plaza or the church. Its buildings are
taken away, and the land-value field is worked out again with the new parks in
it before the shops and services are placed.

NRPA's figure is about 10 acres per 1,000 [P14][P15]. That counts a
jurisdiction's regional parks and countryside too, and our towns stop at their
own edge, a kilometre across. Ten acres per 1,000 would turn a fifth of the
town into park. Four keeps the parks within walking distance of every home and
leaves the town built up. Zoning later turns some of the centre's towers into
flats, so the final count of residents can run a little over the one the parks
were planned for. The planning checks pass a town at 60% of the target or
more, which allows for that. In the starter towns the result is 3.4–4.2 acres
per 1,000 (Lanternport, with no more blocks to spare among its lakes, 3.6).

### Car parks

Behind the high street, some lots in the centre are left unbuilt and paved.
Each is a lot in a T5 or T6 block that has lots on a main road, while the lot
itself doesn't front the main road but does front a side street. A town gets
one car park for about every dozen lots on its centre's main roads, at most
four, each at least 24 tiles from the next and nearest the middle first. A car
park has 12 to 60 tiles. In a town of small lots the minimum is smaller (most
of the centre's lots, and never under 6), so Porchlight, with its 12-tile
lots, still gets them.

Each paved tile is a bay. The town draws a car in a quarter of the bays
(scenery), and the town's life parks in the rest (see below).

## Each building's kind

`BuildingsStage.PickKind` now takes the land:

- **Shops in the centre** (T5 and T6) with the chance `towerShare × shopShare`
  as before, times 0.6 + 0.8 × the land value, so the dearest corners are
  shops and the cheaper backs of blocks homes.
- **The high street.** A lot in T4 that fronts a main road and lies within the
  centre plus 20% of the radius is a shop with the chance `towerShare`, so 60%
  at the default. Competing shops cluster on one street (Hotelling) [P11].
  These shops keep flats over them (mixed use).
- **Workshops in the wedge:** 60% of the wedge's lots. Outside it, past 70% of
  the radius, only 4% (a garage, a yard). A town without a wedge keeps the old
  25%.
- **Houses** everywhere else.

## The shops and services

Once the buildings stand, `Retail.Place` turns some of them to new uses. It
changes a building's kind and floors, and nothing else, so every invariant the
buildings stage checks still holds. It runs a share at a time, so the web
build never stalls on it. The order matters, because each step takes its
building out of the next steps' choices:

1. **The fire station.** ISO's rating gives full credit when every building
   is within 1.5 road miles (2.4 km) of an engine [P16], and NFPA 1710 sets a
   240 s drive [P17]. The station goes at the road node on a main road or
   collector from which the furthest building (by its nearest node) is nearest
   by road, out of the 24 such nodes nearest the buildings' middle. It takes
   the nearest building with 6 tiles or more on a main road or collector,
   outside the core, and has two floors. A town under 100 households has
   none.
2. **The clinic** (a doctor's office): a building with 4 tiles or more on a
   main road or collector, outside the core, as near as possible to a fifth of
   the radius from the middle (just off the centre), in a town of 150
   households or more.
3. **Supermarkets, by threshold.** Gibbs's retail primer for CNU puts a
   supermarket at 6,000–8,000 households [P9]. Our towns have about 2,000, so
   for the game's sake a town of 300 households or more gets one, and a town
   of 6,000 or more gets one for each 6,000. It's a big building (12 tiles, or
   the biggest the town has on a main road if smaller) on a main road or
   collector, between 15% and 85% of the way out, outside the core. Of those,
   it takes the one where the Huff model (below) says it would draw the most
   custom, times 1 + the land value. It has one floor.
4. **The petrol station** lives on passing traffic, so it goes at the busiest
   junction of main roads outside the centre [P9]. "Busiest" counts the
   shortest drives between every pair of the gates, the centre and the second
   centre (main roads cost 1 a tile, collectors 1.3 and local streets 2, as
   drivers prefer the main roads), and how many of them pass through each
   node. The station is the building on a main road within 12 tiles of the
   busiest such node in T3 or T4, or of the next busiest if there's none. It
   has one floor, in a town of 150 households or more.
5. **Corner stores**, one for about every 1,000 households (Gibbs) [P9], in a
   town of 150 or more. They're placed one at a time, each at a
   neighbourhood's entrance: within 10 tiles of where a collector meets a
   main road (or main roads meet), on a main road or collector, outside the
   centre, in a house or a shop that isn't on the high street. Of those, each
   takes the one where the Huff model says it would draw the most custom the
   shops already there don't, times 1 + half the land value. The households
   are counted before zoning, which later turns the outer towers into flats,
   so the count runs low. To make up for it, the next store comes 60% of the
   way to its next thousand: 1,600 households get two.

### The Huff model

How shoppers share themselves among shops [P13]: each shop pulls on a home
with its size over the distance squared,

    P(home i shops at j) = (size_j / d_ij²) / Σ_k (size_k / d_ik²)

with distances under 4 tiles counted as 4, so the shop next door doesn't take
everything. A new shop's custom is the sum over the homes of each home's
households times the share it would take. The shops it competes with are the
centre's and the high street's (size 1 each), the supermarkets (size 12) and
the corner stores placed so far (size 1). So a store goes where homes are many
and other shops far: Huff's answer to "minus overlap with existing shops'
catchments".

### The market

The plaza is now also the town's **Market** landmark: the same place, with the
plaza's lots. Stalls on market day come with §6's calendar.

## In the town's life

- **Where people shop.** People going to the shops (after work, in the
  morning at home, on a visit) now choose by the same Huff model: each shop's
  pull is its jobs (its size) over the distance squared, and they pick one in
  proportion. So the supermarket draws from across town, while the corner
  store gets its own streets. Lunch still goes to the nearest of a few near
  work. `LifeOptions.huffShops = false` restores the old nearest-of-a-few.
- **Schools** are the town's schools. In a town without one, the hall or
  church serves, as before. The fire station and the clinic never do.
- **The fire engine is kept at the fire station.** On a call it pulls out onto
  the lane in front of the station and, siren going, drives to the call.
  After the call it drives back to the station without the siren and goes in.
  Ambulances still come from the hospital beyond the gates.
- **Car parks.** Each bay is a parking spot (`SpotKind.CarPark`) open to
  anyone, like the kerb. A driver holds one as they set off if it's the free
  spot nearest the door, or hunts for one by road. All a car park's bays are
  entered from the lane by the bay nearest its street, on that side of the
  road. Pulling in or out takes as long as a driveway (5 s). Manage's focus
  calls it a "car-park bay".

## Drawing it

A car park is paved in the parking lane's grey, with no trees or bushes. The
new buildings use the existing models for now: a supermarket or petrol
station looks like a one-floor shop, and a fire station or clinic looks like
the hall. The blueprint colours each one its own. Manage names them when you
hover over them ("Supermarket (Retail) · 22 jobs"), from an eighth number
added to each building on the wire (`Zoning.Encode`: the building's kind).

## The planning checks

`ShopsCheck`, run by `Checks zoning` for each town, prints a line per town
like

    Porchlight: planned for 1659 households; parks 28.5 acres, 6.7 a 1,000 residents; 2 corner stores
    (842 households each); 1 supermarkets; petrol yes; clinic yes; fire station 773 m from the furthest
    building; high street 63 shops; 4 car parks; workshops 129/163 in the wedge; land value house 0.27
    shop 0.45 workshop 0.04

and fails a town if:

- its park land is under 60% of 4 acres per 1,000 residents (the county's
  towns);
- its corner stores have fewer than 500 or more than 2,000 households each
  (the county's towns), or it has 300 households and no corner store;
- it has 600 households and no supermarket, or 300 and no fire station;
- any building is further than ISO's 2.4 km from the fire station by road,
  or more than 240 s away at a side street's 2.6 tiles a second;
- any corner store, supermarket, petrol station, clinic or fire station
  doesn't front a main road or collector, or isn't in the street scene; the
  petrol station or a high-street shop doesn't front a main road;
- fewer than 60% of its workshops are in the industrial wedge;
- its shops don't stand on dearer land than its houses, or (with a wedge) its
  workshops on cheaper;
- a car park has a building on it or isn't paved, or a centre of shops has no
  car park;
- it has a plaza and no market.

`Checks plan` (§1) now allows shops in T4 on the high street and as corner
stores, and a corner store may front a collector as well as a main road. Its
"planned the same twice" fingerprint covers the new layers: the park blocks,
car parks, services, every building's kind and the wedge.

## Changing the towns

The towns come out differently from the same keys: some houses are now shops,
workshops or services, and some lots are parks or car parks. The starter
towns' house lists (`server/towns/*.json`, `npm run towns`) are made again
from the new towns. A house a player owned may now be a shop.

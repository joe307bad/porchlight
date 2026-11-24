# Land use: homes, work, and the trips between them

Every town gets its land use when it's made, from its seed: each building is a
home, a workplace, or both, and holds a number of households and jobs. Manage
shows it (the *Land use* layer and panel section). This is step one of a town
whose townsfolk each live somewhere, work somewhere else, and travel between
the two, so that traffic gathers where real traffic does, rather than
wandering at random.

Code: `unity/Assets/Porchlight/Runtime/Shared/Explore/Zoning.cs` (pure, shared
by the game, the simulation in `sim/` and the Checks tool). Judge changes with
`dotnet run -c Release --project "unity/Tools~/Checks" -- zoning`.

## What city planning says

### Zoning categories

Most zoning codes (US "Euclidean" zoning since *Village of Euclid v. Ambler
Realty*, 1926; the APA's Land-Based Classification Standards) sort land into a
handful of uses, usually drawn in the same colours: **residential** (yellow),
**commercial**, split into retail (red) and office, **industrial** (purple),
**civic/institutional** (blue), and, increasingly, **mixed use**: shops on the
street with flats over them. The game uses the same six:

| Zone | In the game | Holds |
| --- | --- | --- |
| Residential | houses; towers turned to flats | homes |
| Mixed use | high-street shops; houses on a main road with a shop downstairs | homes + jobs |
| Retail | high-street shops whose upper floors are offices; the supermarket and the petrol station | jobs |
| Office | the towers in the middle | jobs |
| Industrial | workshops in the industrial wedge ([town-shops.md](town-shops.md)) | jobs |
| Civic | the church, the town hall, the schools, the fire station and the clinic | jobs |

### Where each use goes

The classic models of urban structure agree on a dense centre of work, homes
further out, and industry along the edge or the transport corridors:

- **Burgess, concentric zones (1925):** a central business district, then a
  zone in transition (industry, old housing), then rings of homes, wealthier
  further out.
- **Hoyt, sectors (1939):** uses grow outwards in wedges along transport
  lines, so commerce strings out along the main roads ("commercial strips").
- **Harris & Ullman, multiple nuclei (1945):** bigger towns grow more than one
  centre (a port, a university, a factory district).
- **Alonso, bid-rent (1964):** each use pays what being near the centre is
  worth to it. Offices value access most and outbid everyone in the middle;
  homes win further out, where land is cheap.

The generator follows the first two: towers in the centre, houses round
them, and since [town-shops.md](town-shops.md) a high street along the main
roads and the workshops in Hoyt's wedge out to one gate. Zoning adds bid-rent
(towers become flats from the outside in) and Hoyt's strips (houses on the
main roads open shops).

### Jobs–housing balance

A town with about one job for each working resident lets people work near
home. With far more jobs, commuters pour in; with far fewer, residents all
drive out. US EPA EnviroAtlas counts **0.75–1.5 jobs a household** as
balanced, while above 1.5 the town imports commuters. (The evidence that
balance alone cuts driving is mixed: Ewing & Cervero's 2010 meta-analysis
found little effect, and Cervero & Duncan (2006) found that putting jobs near
housing cut driving more than mixing in shops did. For a game, though,
balance is what makes the trips look right.)

A household is about 2.5 people (the US average) with about 1.2 workers.
Zoning aims for 1.2 jobs a household: a job for every worker.

### Floor space per job

How many jobs a building holds follows from its floor area. Planning offices
publish floor space per worker. Toronto City Planning's 2024 medians are
29 m² for office, 28 m² for retail, and 85 m² for industrial (storage
excluded). Sacramento's code uses 250–350 sq ft for offices, 450 for retail,
and 800 for light industry. The Twin Cities Metropolitan Council's are
looser (600, 920, 1,500 sq ft). The game uses Toronto's. A flat takes about
100 m² gross, and a house up to 320 m² is one household's.

A tile is 4 m square (16 m²), so a building's floor area is its tiles × 16 ×
its floors (as drawn: Layout's floors, towers included).

## How a town is zoned

1. **Start from the building's look** (Layout's `HouseStyle`): House →
   residential, Workshop → industrial, Shop → mixed use, Office → office,
   Church/Hall → civic.
2. **Fill it:** homes and jobs for its floor area, at the densities above. A
   mixed-use building has a shop on the ground floor and flats above; an
   office has shops or a lobby downstairs.
3. **Balance the town** towards 1.2 jobs a household, one building at a time,
   keeping each change only if it brings the town nearer:
   - *Too many jobs* (a town of towers: Porchlight's 16-floor centre alone
     would hold tens of thousands): the towers **furthest from the middle**
     become flats first (bid-rent).
   - *Too few jobs:* the shops **nearest the middle** turn their upper floors
     to offices, then the houses **on main roads, nearest the middle**, open a
     shop downstairs (Hoyt's strips; at most a fifth of the houses). A house
     always keeps its home, since players live in them.

No randomness: the same seed and settings always give the same zoning, and
since it's computed from the finished town rather than inside a generator
stage, it doesn't move a single building or house.

### The starter towns

| Town | Buildings | Homes | Jobs | Jobs a home |
| --- | ---: | ---: | ---: | ---: |
| Porchlight | 2,299 | 2,146 | 2,569 | 1.20 |
| Ember Crossing | 1,092 | 1,772 | 2,092 | 1.18 |
| Lanternport | 360 | 420 | 505 | 1.20 |
| Juniper Falls | 153 | 178 | 211 | 1.19 |

Small towns with only one or two towers can't always reach the band (they
balance as near as a whole building allows); the `zoning` check only holds
the starter towns to it.

## Where it goes

Manage gets each town's buildings as `[zone, x0, y0, x1, y1, homes, jobs]`
(tiles, the town's frame) with its figures (homes, residents, workers, jobs).
For the starter towns these come from `server/towns/*.roads.json` (written by
`Checks roadmap`, so `npm run towns` keeps them current); for players' towns
they come from the simulation's READY once it has made the town.

## Next: commuting

**Status:** the town's life ([town-life.md](town-life.md), PR #19) now does
most of this, from the zoning here:

- **Generation:** everyone is simulated, not sampled.
- **Distribution:** jobs are filled at random from the slots, with
  commuters in and out by the gates; since PR #22, by a doubly-constrained
  gravity model with the gates as zones.
- **Mode choice:** walk under 240 m, drive, or bus.
- **Assignment:** routing by measured hold-ups (PR #18), and since PR #22
  by BPR costs with day-to-day habits.
- **Time of day:** the plans' peaks.

Below is the plan as it was written.

The plan towards trips, in the order transport planners build their models
(the four-step model; an activity-based model like MATSim is the richer
alternative once townsfolk have whole days):

1. **Trip generation.** Each townsperson gets a home (a household in a
   building with homes) and, if a worker, a job (a slot in a building with
   jobs). The game's townsfolk are far fewer than the households, so sample
   them in proportion.
2. **Trip distribution.** Pair homes with jobs by a *gravity model*: the
   chance of working at a place falls off with travel time and grows with
   its jobs. Make it doubly constrained so every job is filled and every
   worker employed. Commuters from other towns in the county fill a town's
   surplus jobs, along the highways.
3. **Mode choice.** Drive, walk or ride, by distance (walk under ~800 m) and
   whether the household has a car (the driveways and parked cars already
   placed).
4. **Assignment.** Route trips over the street graph with a volume–delay
   function (the BPR curve: travel time × (1 + 0.15 (volume / capacity)⁴)),
   re-routing as streets fill, so congestion appears at the bridges, the
   main-road junctions and the way into the centre.
5. **Time of day.** Departures spread around a morning and evening peak (a
   road's busiest hour commonly carries 8–12% of its day's traffic, its
   "K-factor"), with errands to
   the shops (retail jobs double as destinations) in between.

Manage can then show what this builds: trips by hour, flows on each street,
and the junctions that jam.

The wider research (is the town planned like a real one, where shops go,
traffic, townsfolk and events) and the full to-do list are in
[city-research.md](city-research.md).

## Sources

- US EPA EnviroAtlas, *Employment-to-housing ratio* fact sheet:
  <https://enviroatlas.epa.gov/enviroatlas/DataFactSheets/pdf/Supplemental/Employmenthousingratio.pdf>
- City of Toronto, *Floor Space per Worker Ratios* (2024):
  <https://www.toronto.ca/wp-content/uploads/2024/02/8b6d-CityPlanning-Floor-Space-Per-Worker-Ratios.pdf>
- Sacramento City Code, employment densities by zone:
  <https://codelibrary.amlegal.com/codes/sacramentoca/latest/sacramento_ca/0-0-0-35143>
- Metropolitan Council, *How to Measure Employment Intensity and Capacity*:
  <https://metrocouncil.org/Handbook/Files/Resources/Fact-Sheet/LAND-USE/How-to-Measure-Employment-Intensity-and-Capacity.aspx>
- Cervero & Duncan, *Which Reduces Vehicle Travel More: Jobs-Housing Balance
  or Retail-Housing Mixing?* (JAPA, 2006); Ewing & Cervero, *Travel and the
  Built Environment* (JAPA, 2010).
- Burgess, *The Growth of the City* (1925); Hoyt, *The Structure and Growth
  of Residential Neighborhoods in American Cities* (1939); Harris & Ullman,
  *The Nature of Cities* (1945); Alonso, *Location and Land Use* (1964).
- APA, *Land-Based Classification Standards*.
- Ortúzar & Willumsen, *Modelling Transport* (four-step model, gravity
  distribution); US Bureau of Public Roads, *Traffic Assignment Manual*
  (1964) for the BPR function; Horni, Nagel & Axhausen, *The Multi-Agent
  Transport Simulation MATSim* (2016).

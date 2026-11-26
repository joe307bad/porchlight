# The mayor: making the town's traffic the game

Porchlight's towns already have a lot of traffic, and a lot of code that
tunes it automatically. This proposal turns that around: instead of the
game making the traffic better on its own, the player does it, as the
town's planner or mayor, in the spirit of SimCity. This page covers what
would make that unique to Porchlight, the one big thing in the way, and an
order to build it in.

Nothing here is built yet.

---

## 1. What makes it different from SimCity

SimCity shows you a crowd. Porchlight already simulates each person as an
individual. All of this is in the code today:

- **Every resident is real.** Each one has a name, a household, a job
  picked by a gravity model and a plan for the day. Their trips run on a
  real road network with lanes, signals, parking hunts and buses
  (`Life/People.cs`, `Life/Population.cs`; [town-life.md](town-life.md)).
- **You can ask anyone why.** `Life/Focus.cs` already gives one car or
  person's decision tree, its queue and its way ahead.
- **The town learns.** Drivers change their routes day by day until the
  traffic settles (`Life/Congestion.cs`, the habits part). So a change you
  make plays out over several days, and the classic surprises can happen: a
  new road that makes traffic worse, or traffic that grows to fill a wider
  road.
- **You can go down to the street.** You can drive to the junction you just
  changed and sit in the queue yourself, with other players around you.
- **A town's whole day runs in about 5 seconds**
  ([town-life.md](town-life.md)).

**The pitch: the mayor who lives on the street.** Complaints don't come from
an abstract happiness meter. They come from a named neighbour: "Martha at
14 Elm sat 11 minutes at Main & 3rd this morning." You can click through to
her trip, change the signal, and drive there to see if it worked.

## 2. The biggest gap: you can't change a town after it's made

A town is built entirely from its key string
(`seed=porchlight&width=256…`). The game, the server's simulation worker
(`sim/`), Manage and the checks all rebuild it the same way from that key.
Signal timing (`Life/Signals.cs`, Webster's method), rerouting and habits
all tune themselves automatically, so nobody has any lever over the
traffic.

So the foundation is a list of changes stored on top of the key: the town
becomes its key plus your changes, applied in order. Every part that builds
a town today would apply the same list, so they all keep agreeing. The
`towns.layout` column already stores a hash of the key and houses
(`server/src/multiplayer/persistence.ts`); adding the change list to that
hash is a natural place to start.

## 3. The core loop

1. **Notice.** Residents send complaints and petitions, drawn from the
   delays and failed trips the town already records (`LifeStats.problems`,
   `carWaits`, `failures` in `Life/Analytics.cs`). The ink missions can
   deliver them as civic requests, with no new game build needed
   (`server/missions/README.md`).
2. **Propose.** Make a change in the existing blueprint view.
3. **Preview tomorrow.** Run the proposed change through a full day in the
   background and compare before and after: average commute, worst
   junctions, failed trips. The whole-day traffic check (`Checks
   traffic-day`) already does this, and nobody else can show you
   tomorrow's traffic before you commit.
4. **Enact.** The town lives with it, and habits shift over the next few
   in-game days.
5. **Answer for it.** Approval comes from residents' actual measured
   commutes. A budget comes from households and businesses.

## 4. Suggested order

### Phase 1: road rules only

Changes here don't move any buildings, so the drawing code barely changes:

- swap a junction between a stop sign, an all-way stop and a signal, or set
  its timing by hand
- one-way streets, no-left-turn bans, closed lanes
- a speed limit per road
- kerb parking on or off, and parking meters (these feed into how long
  drivers hunt for a spot)
- bus stops and how often the bus comes

### Phase 2: complaints, approval and the preview

Grow the Manage views into an in-game mayor's office: the residents'
complaints, the approval figure, and the before-and-after of a proposal.

### Phase 3: rezoning

Change a lot's use, for example homes to shops. Zoning, land value and shop
placement already exist ([land-use.md](land-use.md),
[town-shops.md](town-shops.md)); the hard part is swapping buildings in the
drawing, which is statically batched today.

### Phase 4: events as tools

Market days, a sports venue and road closures for parades, using the
calendar, which already exists ([town-calendar.md](town-calendar.md)).

## 5. The first step

Start with Phase 1 and its first item: changing a junction's control. It
needs the change list, a single new action in the blueprint and a hook in
the road network (`Life/RoadNet.cs`). It exercises the whole idea from
start to finish without touching buildings.

## 6. Open questions

- **Who is mayor of the shared towns?** Your own towns are easy: you're
  mayor. Porchlight and the other starter towns are shared by everyone
  ([cross-account.md](cross-account.md)). Options are an election among
  the players who own houses there, or letting players only propose
  changes in those towns.
- **What happens to time?** The county clock runs in real time. Shared
  towns can't skip ahead, so the "preview tomorrow" step becomes the main
  way to see consequences. Private towns could perhaps speed up their
  clock.
- **How do changes take effect mid-day?** A change has to reach the
  server's simulation, every player in the town and any town built ahead
  of time (the street life made ahead, in `unity/README.md`), all at the
  same moment.

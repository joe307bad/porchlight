# sim: the towns' traffic and townsfolk, run on the server

Every town in every county has its cars and townsfolk moved on here, ten times a
second, by the game's own code (`unity/Assets/Porchlight/Runtime/Shared`, compiled
in). It doesn't have a copy of that code, so a town here is the one each player's
game builds from the same key: the same cars and people, in the same order, from
the same seeds (`src/SimTown.cs` follows `ExploreView.Populate` step for step).

The game server (`server/src/sim/`) starts this as a child process (or several:
`SIM_WORKERS`, with the towns shared between them). It tells the worker which towns
there are, where the players in each are (the traffic stops for them) and which
cars they've taken. From each tick it keeps every town's latest state, and it never
copies that state for each viewer:

- **A player in a town** gets the whole town when they arrive (about 1 KB). After that
  each tick sends only the cars and people the player's game would have wrong by
  carrying each one on along its heading at its speed (`src/Delta.cs`). That's about
  a fifth of the whole. Nobody hosts any more: the room's `host` is `"server"`, and
  every player's game follows (`WorldSync.ReceiveDelta`). A car carried on toward
  you waits short of you until the server's next word on it.
- **Manage** (`/mp/traffic/feed`) has every town's traffic, whether anyone's in it
  or not. It gets the towns on screen close up ten times a second (it says which:
  `POST /mp/traffic/focus`), and the rest's traffic once a second. While a player
  runs a town instead (see below), Manage shows theirs, since that's what the players see.
- **Anyone** can ask where a car or townsperson is right now:
  `GET /mp/sim/where?town=<key>&kind=car|person&i=<n>`.

If the worker isn't there (no build of it, or `SIM=off`), or it stops, or someone
in the town is on a game too old to follow the server (the game says it can when it
joins: `follows: 1`), the server falls back to how it was before: each town's
players run its traffic (one of them hosts). A worker that stops is restarted, waiting longer each time it keeps
stopping, and towns come back once they're made again.

## The wire (stdin and stdout)

Each frame is a u32 (little-endian) giving the length of what follows, a u8 type,
then the body. Log lines go to stderr.

| In  | Body |
| --- | --- |
| 1 TOWNS   | JSON `[{ id, key }]`: every town to run (the rest stop) |
| 2 PLAYERS | u16 towns, then for each: u32 id, u16 n, n × (f32 x, f32 y), little-endian |
| 3 TAKEN   | JSON `{ id, taken: ["t3", "p12"] }` |

| Out | Body |
| --- | --- |
| 1 READY | JSON `{ id, cars, people }` or `{ id, error }` |
| 2 TICK  | f64 time (unix ms), u16 towns, then for each: u32 id, u16 cars, u16 people, the records (7 bytes each, `WorldSync`'s), u32 delta length, delta |
| 3 LOOKS | JSON `{ id, looks }`: the townsfolk added since the town was made (drivers put out of taken cars). The next tick goes out whole. |
| 4 STATS | JSON `{ towns, ticks, avgMs, maxMs }`, once a minute |

## Building, running, measuring

```sh
dotnet build sim/Porchlight.Sim.csproj -c Release   # dev.sh does this when dotnet is installed
dotnet sim/bin/Release/net8.0/porchlight-sim.dll --bench server/towns 10 1.0
```

`--follow server/towns` runs each starter town twice, once as the server does
and once as a player's game following its deltas, and fails if what's drawn
strays from the server's (it's about 0.03 tiles off on average).

The bench makes the starter towns ten times over (40 towns), ticks them all for a
minute's worth, and prints the cost of a tick and how much the deltas save. It fails
if a town's tick averages more than the budget (1 ms; here it's about 0.14) or if
the deltas save too little. CI runs it, and also runs the real worker through the
real server (`server/test/sim.test.ts`, `sim-room.test.ts`). CI then publishes the
worker as a self-contained binary for the box (linux-musl-arm64) into `server/sim/`,
where the image picks it up.

Settings (on the server): `SIM=off`, `SIM_COMMAND` (a worker command of your own),
`SIM_WORKERS` (default 1), and .NET's own, such as `DOTNET_GCHeapHardLimit`
(192 MB on the box).

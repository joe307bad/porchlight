# Between accounts

What one account (one Google sign-in) can do to another's, and what the server
checks before it believes a game. Tests: `server/test/room.test.ts` (the end of it),
`played.test.ts`, `sim-room.test.ts`.

## Shared and not

The towns that come with the game, and the highways between them, are everyone's:
players there see each other, ride with each other, follow each other, and share
the traffic, the time of day and each mission's best times. A house there belongs to
one account. A town an account makes is its own, and so is the junction north of
Porchlight: there are no invites, on purpose. Two accounts may have characters of the
same name, also on purpose. Characters, looks, unlocks and missions finished are each
account's own.

## Someone else's town

- `CityRoom.onAuth` checks the room's own town, not the one the client names, and the
  two must be the same. (Matchmaking puts a client that names no town in any open room,
  and `joinById` in any room at all.)
- `GET /mp/players/:id` is a 404 for someone in a town the asker may not enter.
- `/mp/records` is a 403 for a town the asker may not enter, to read or to write.
- Making a town whose key is taken says what it's called only to its maker (or if it
  comes with the game).

A key nobody has saved is still anyone's to enter (`mayEnter`).

## What's earned

`server/src/multiplayer/played.ts` keeps, in memory, what the server has seen:

- **Missions.** The game fetches a mission's story each time it starts one
  (`GET /mp/missions/:id`), which counts as its start, for the account. `unlock` and
  `mission_done` are kept only with a start at least the mission's `# least:` seconds ago,
  and 10 tiles moved since. Each start can be finished once.
- **Races** (`# race: yes`). The simulation works out the town's course with the game's
  own code (`SimTown.Ground`). The room follows each player's moves along it, in order, on
  foot, at no more than a sprint (`RaceWatch`), and times them. `POST /mp/records` is kept only
  for a race seen run in that town, once each. The game's time is kept (it has the line to
  the frame), unless it's over 3 s shorter than the server's; then the server's is.

So a cheat has to send a believable run in real time. The server doesn't play the
missions, so one who does that still gets their unlock, for their own account only.
Starts and races are lost when the server restarts: a mission being played then
earns nothing until it's played again. Where the simulation isn't running a town, no
times are kept there.

## The town's cars

`take` is for a car within 6 tiles (the game's reach is 1; the rest is the network's
lag), one a second at most. Where the cars are is the simulation's to say; without it,
only the one a second holds.

## Houses

In the towns that come with the game a house is claimed by the one tile in the town's
file (`houses`), which is the tile the game sends, so no house can be claimed twice by two
of its tiles. In a town someone made, only they can claim houses, so nothing is checked.

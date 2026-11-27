# Street editor: the wire between Manage, the server and the draft worker

The formats the street editor's three parts share ([street-editor.md](street-editor.md)):
Manage's edit screen (`manage/src/edit/`), the server (`server/src/multiplayer/edit.ts`) and the
draft worker (`porchlight-sim --draft`, `sim/src/Draft.cs`). Points are `[x, y]` in the town's
tiles (not the county's). Keys are town keys, `rev` included.

## The skeleton

What an editor decided, as `Model/Skeleton.cs` reads and writes it:

```jsonc
{
  "v": 1,
  "center": [128.5, 120.5],                 // the bullseye, or absent
  "mains": [ { "pts": [[x,y], …], "kind": "main", "cls": "arterial" } ],   // every main road once any is edited, else absent
  "streets": [ { "pts": [[x,y], …], "kind": "street", "cls": "local" } ],  // pinned streets
  "removed": [ [[x,y],[x,y]] ],              // generated roads deleted, by their two ends
  "roads": [ { "a": [x,y], "b": [x,y], "cls": "collector", "lanes": 2, "divided": true, "oneWay": false, "locked": true } ],
  "junctions": [ { "at": [x,y], "control": "auto|free|stop|allway|signal|roundabout" } ]
}
```

Rules the edit screen keeps when it turns a drag into a skeleton:

- **A main road changed** (a junction or bend of one dragged, one drawn or deleted): `mains` becomes every
  main road in the town as it's drawn now, with the change made. From then on main-road edits change
  `mains`. Gates (the four road ends at the map's edges) never move.
- **A street changed:** each street touched goes into `streets` with its new points, and its old ends
  into `removed`. A pinned street that's changed again just changes in `streets`.
- **A junction moved** moves the ends of every road that meets there (main roads and streets alike).
- **The bullseye:** `center`. With `mains` absent, the main roads are routed to it again; either way
  the zones, land value, the high street and the shops arrange themselves round it.
- **Deleted:** a pinned street leaves `streets`; a generated one's ends go into `removed`; a main road
  leaves `mains`.
- **Side panel:** class, lanes, divided, one way and lock go in `roads` (matched by the road's two
  ends within a tile); a junction's control in `junctions` (within 1.5 tiles).

## The draft worker

`dotnet sim/bin/Release/net8.0/porchlight-sim.dll --draft`: one JSON request a line on stdin, one
answer a line on stdout, each with the request's `id`. Logs on stderr. It keeps each key's terrain
(the stages before the roads) so a re-plan starts at the roads.

| Op | Request | Answer |
| --- | --- | --- |
| `plan` | `{ id, op: "plan", key, skeleton, base?: { key, skeleton }, full?: bool }` | `{ id, ok: true, ms, town, problems, changes }` |
| `day` | `{ id, op: "day", key, skeleton }` | `{ id, ok: true, ms, day }` |
| `houses` | `{ id, op: "houses", key, skeleton }` | `{ id, ok: true, houses: [[x, y], …] }` |

Any op can answer `{ id, ok: false, error }`. `full: true` grows the town from scratch with every
stage's checks (what Commit runs); otherwise from the cached terrain, checks on too. The cache holds
each key's model through the zoo while the skeleton's `center` is unchanged (a moved bullseye grows it
again from the site), and a `day` is kept by key and skeleton (a day is over a minute's work in Porchlight).

**`town`**

```jsonc
{
  "width": 256, "height": 256, "center": [x,y], "secondCentre": [x,y] | null, "townRadius": 128,
  "gates": [[x,y]],
  "water": "<base64: one bit a tile, row by row, lowest bit first>",
  "nodes": [ { "id": 0, "at": [x,y], "legs": 3, "gate": false } ],
  "roads": [ {   // pts: the road graph's own points (not the drawn, smoothed line), so sent back in a skeleton they lay the road where it is
      "id": 0, "from": 0, "to": 1, "kind": "main|street|path", "cls": "arterial|collector|local",
      "pts": [[x,y], …], "name": "Elm Street", "width": 2,
      "design": { "lanes": [1, 1], "width": 2, "divided": false,
                  "pockets": [ { "atTo": true, "left": true, "length": 4.8 } ],
                  "flow": [420, 380], "vc": 0.62, "relief": false, "locked": false } | null
  } ],
  "junctions": [ { "node": 3, "at": [x,y], "form": "plain|roundabout", "radius": 0,
                   "control": "free|stop|allway|signal|yield|auto", "protectedLeft": false,
                   "slips": [ [[x,y], …] ], "entering": 1200 } ],
  "blocks": [ { "id": 0, "poly": [[x,y], …], "transect": 4, "era": "core|garden|postwar", "park": false } ],
  "buildings": [ { "id": 0, "kind": "house|shop|workshop|church|hall|school|supermarket|petrol|fire|clinic", "poly": [[x,y], …], "floors": 2 } ],
  "notes": [ { "at": [x,y], "text": "Elm Street is still over capacity (1.2)" } ],
  "stats": { "houses": 1789, "shops": 120, "buildings": 2171, "roads": 502, "junctions": 307,
             "roundabouts": 2, "pockets": 14, "signals": 7, "ms": { "roads": 40, "streets": 120 } }
}
```

**`problems`**: `[{ level: "error" | "warning", text, at: [x,y] | null }]`. Errors stop Commit:
junctions under 4 tiles apart along a main road (3 along a street), two roads meeting under 60°, a
road in water with no bridge, a gate, the airport or the zoo cut off from the centre, two roads
between the same junctions, and any stage's own check (`at: null`). One of these the base already had
(the same text within 1.5 tiles) is a warning instead, "… (as it was before)": the edit didn't make it.
Warnings: a road over capacity (`design.vc` above 1), the design's notes, houses lost, a pinned point
dropped (a point of a pinned road with no road of the town within 1.5 tiles). (A street dropped for
running alongside another isn't reported: the streets stage doesn't say.) With no design yet
(`model.design` null), roads have `design: null`, junctions `form: "plain"` and the skeleton's own
control for them (else `"auto"`), and the roundabout, pocket and signal counts are 0.

**`changes`** (when `base` is given): `{ housesLost, housesNew, lostHouses: [[x,y]], shops: [before, after],
roadsAdded, roadsRemoved, roundabouts: [before, after], signals: [before, after] }`.

**`day`**: `{ trips, meanCarMin, p90CarMin, meanWalkMin, failed, gaveUp,
worst: [{ at, delayS, control }], pockets: [{ at, storage, worstQueue }] }`. `worst`: the ten junctions
with the most car delay in all, `delayS` their mean wait. `p90CarMin` is from each drive's time, watched
minute by minute. `pockets` is `[]` until the road network's lanes say which are pockets.

## The server

Signed-in, by the session cookie, as the rest of `/mp`. An **editor** of a town is its owner
(`towns.createdBy`), or, for the towns that come with the game, an account in `EDITORS` (account
ids, comma-separated; `*` for any signed-in account, which `dev.sh` sets).

| Call | Body or query | Answer |
| --- | --- | --- |
| `GET /mp/towns/skeleton` | `?key=` | `{ key, rev, skeleton }`; 404 if that key has no skeleton |
| `GET /mp/edit/towns` | | `[{ key, name, rev, mine, editable }]` |
| `GET /mp/edit/draft` | `?town=<key>` | `{ town, baseRev, skeleton, commands, updatedAt, stale, current: { rev, skeleton } }` (a new draft from the current revision if there's none) |
| `PUT /mp/edit/draft` | `{ town, baseRev, skeleton, commands }` | `{ updatedAt }` |
| `DELETE /mp/edit/draft` | `?town=<key>` | `{}` |
| `POST /mp/edit/plan` | `{ town, skeleton, seq }` | the worker's `plan` answer (base: the current revision), plus `seq`; `{ ok: false, error: "superseded", seq }` if a newer plan from the same editor for the same town came while it waited |
| `POST /mp/edit/day` | `{ town, skeleton }` | `{ draft: day, live: day }` |
| `POST /mp/edit/commit` | `{ town, skeleton, message }` | `{ key, rev, rehoused, players }` (households moved; characters playing there now); 409 `{ problems }` if it has errors, 409 `{ error }` if a commit's under way or the town changed since; 400 if the worker can't grow it |
| `GET /mp/edit/history` | `?town=<key>` | `[{ rev, message, by, at }]` |

`town` is any revision's key of the town (it's found by its key without `rev`). `GET /mp/edit/draft`
keeps the new draft it makes.

**Tables:** `town_revisions (id, town_id, rev, key, skeleton jsonb, houses jsonb, committed_by, message,
committed_at; unique town_id + rev)`: `key` is that revision's whole key (what `/mp/towns/skeleton` and the
simulation look it up by), `houses` its house tiles from the worker (what a character may claim in it,
as a starter town's file says for revision 0). `town_drafts (id, town_id, author, base_rev, skeleton jsonb,
commands jsonb, updated_at; unique town_id + author)`. `notices (id, character_id, text, created_at)`:
told to the character as `notice { text }` when they next join a room, then deleted.

**Commit**, in order: the worker's `plan` with `full: true` (409 on errors) and `houses`; in one
transaction with the town's row locked: the revision row, `towns.key` set to the key with
`rev=N+1`, households whose house tile is no longer a house moved to the nearest free house (a
household together), each told by a notice; then the old room broadcasts `town-rebuilding`
`{ name, by, seconds: 10, key }` (and takes nobody new), and after 10 s saves everyone, carries where
each stood over to the new key, and closes each connection with code **4103** (`CLOSE_TOWN_REBUILT`;
4100 is already `CLOSE_SIGNED_IN_ELSEWHERE`), reason `rev=N` (a close reason holds 123 bytes, and a key is
longer: the whole key came in `town-rebuilding`). The sim worker is sent the new key and its skeleton in
TOWNS (`[{ id, key, skeleton }]`), and drops the old.

**Starting up:** a town that comes with the game and has been edited is never put back as its file has
it: with the file's key unchanged it stays as it is; if the file's key changed, the new key takes the
edit's `rev` and skeleton (and its households are rehoused into the file's houses, as for any regenerated
starter town).

**The game:** before growing a town whose key has `rev` above 0 it fetches `/mp/towns/skeleton`
and puts it in `Skeletons`. On `town-rebuilding` it shows a banner; on close code 4103 it grows the
town from the new key and joins its room, where it stood if that's still open ground, else at the
nearest street, else at its door.

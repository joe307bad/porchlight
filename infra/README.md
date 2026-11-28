# Porchlight infrastructure

Sign-in works the way it does in topspin: Keycloak sits in front of Google but the user never sees it. Everything runs on one small EC2 box.

```
browser ──https──> Caddy ──┬── /            the game: Unity WebGL build (unity/, built in CI)
                           ├── /auth/*      auth service (server/) ──back channel──┐
                           ├── /mp/*        the same service: multiplayer ──┐      │
                           └── auth.<domain> Keycloak ── Postgres ◄─────────┘      │
                                               ▲──────────────────────────────────┘
```

## How sign-in works

1. The page asks `GET /auth/me`. A 401 shows the sign-in screen.
2. **Sign in with Google** goes to `/auth/login`. The auth service sends the browser to Keycloak with `kc_idp_hint=google`. Keycloak's browser flow is also set to redirect to Google and its password form is disabled, so even a request without the hint never shows a Keycloak page.
3. Google sends the browser back through Keycloak to `/auth/callback`. The auth service then:
   - redeems the code on the back channel, with the client secret and PKCE;
   - verifies the id token against Keycloak's signing keys;
   - sets the session cookie.
4. **Tokens never reach page JavaScript.** Both cookies are `HttpOnly; SameSite=Lax`, and `Secure` over HTTPS:
   - `pl_signin` holds the in-flight state, nonce and PKCE verifier. It lasts 10 minutes and is scoped to `/auth`.
   - `pl_session` holds the user and their id token, HMAC-signed. It lasts as long as Keycloak's session: 14 days idle, 30 days at most.
5. **Sign out** POSTs to `/auth/logout`. The service clears the cookie and ends the Keycloak session using `id_token_hint`, so it goes straight back to the app with no "are you sure?" page.

## How multiplayer works

The auth service is also the game server: Colyseus on the same port, under `/mp`.

- **Missions:** `/mp/missions` lists the ink stories in `server/missions` and serves each one compiled. Files are read fresh on every request, and the folder is mounted into the container, so a changed mission is live without rebuilding the image or the game.
- **Rooms:** everyone exploring the same town (seed and params) shares a room, up to 100 players. Past that, a second copy of the town opens. Out on the highways, a room has its own half of each of them (see The county).
- **Characters:** one account (one Google sign-in) has up to 8 characters. Several of them can be played at once from different tabs or devices, so two people can share an account. After sign-in, the game asks **Who's playing?** (`/mp/characters`), which town they live in and which house, then goes to their front door. The choice is remembered for that tab only.
- **Towns:** four come with the game, the same for everyone: Porchlight, the biggest map there is, built up as far as the settings go, with no water and few cul-de-sacs, and three cities of different sizes round it, Lanternport (east), Ember Crossing (south) and Juniper Falls (west) (`server/towns/*.json`, each with its `place`). When the generator or their settings change, run `npm run towns` in `server/` (`npm run town` for Porchlight alone) and commit the files; on next starting, the server gives everyone who lived in a changed town a new house, households together, and clears where they were standing. `dotnet run --project "unity/Tools~/Checks" -- homes` fails if Porchlight's file has fallen behind the generator, and `-- county` if the cities' have. An account can make up to 3 more of its own, with a fresh seed or designed in the town editor and saved by name; to make another it deletes one (`/mp/towns`). Each goes in one of the account's three places north of Porchlight (`towns.slot`, 0-2: the first free one). There's one town to a key, so saving a town that already exists says what it's called. Deleting one sends those who lived there back to choosing a town, and is refused while anyone's playing there. A town is its seed and params, so `towns.key` is also its room's name. Any number of characters can live in a town. Only the towns that come with the game are shared between accounts: a town an account makes is its alone, so only that account's characters see it in the picker, live there or play there (`mayEnter` refuses its room to anyone else; migration 0004 moved other accounts' characters out of towns they didn't make).
- **The county:** the towns are joined by highways you can drive (and fields you can fly over) with no loading screen. Every town sits in the middle of a 256-tile slot with a gate in the middle of each edge, so the layout follows from the keys alone: Porchlight in the middle, the cities east, south and west of it joined in a ring, and north of it a junction and a loop through your own three places (an empty one is a plot the road runs through). `server/src/county.ts` lays it out as the game does (`Shared/County/CountyMap.cs`), held to it by `test/county-vectors.json` (written by `Checks county`). Each room's ground is its town, the near half of each of its highways, and the countryside nearest it; the junction and your plots are the north room's (`north:<account>`, only that account may enter). A room accepts moves anywhere on its ground (16 tiles' slack), so it loads its county when it opens (and again when its owner makes or deletes a town). Driving over into another room's ground, the game leaves its room and joins that one with `arrive: { x, y, z, yaw }` (its terms): the server takes that as where they are, if it's that room's ground, and holds the next move to the speed limit from there. Night and day are the same all over the county.
- **Houses:** a house belongs to one account and holds up to 2 of its characters (`PUT /mp/characters/:id/home`). It's named by a tile of its building, not the building's id, which any change to the generator could renumber; if the tile no longer has a house, the game asks them to choose again. It goes when nobody lives there. Only the game runs the generator, so the server checks a house isn't taken or full, but not that the tile is a house.
- **Progress:** finished missions and unlocks belong to the account (`/mp/progress`), so all its characters have them, in every town. A mission can be played again and counted again; its reward is earned once. Only the game runs missions, so the server checks they're real missions and real rewards, but takes the game's word that they were finished.
- **Auth:** joining checks the same `pl_session` cookie. It goes along because `/mp` is same-origin. The room also checks the character belongs to that account. Nothing new is stored in the page.
- **Live state** is in memory only. Clients send moves about 30 times a second while moving, each stamped with when the client took it. The server checks each one as it arrives (well-formed, inside the town, in order, no faster than a boosted car) and snaps back any that fail. It sends each client only the players within a cell or two of them (16-tile cells), as delta patches about 30 times a second. Clients play each other player back on that player's own timestamps, about 120 ms behind, so motion stays even however the network bunches the moves up. Everyone else in the room shows as a minimap dot, updated once a second.
- **Saved state** goes to Postgres, through Drizzle (tables in `server/src/multiplayer/tables.ts`; migrations generated into `server/drizzle` by `npm run db:generate`, and applied by the server on start): accounts and what they've earned, towns and houses, characters, each character's last position (per town) and look, play time, and sessions. Writes are batched every 30 s, plus on leave and on shutdown. A failed write is retried later and never holds up the game. If the database is down, joins are refused.
- **Edge cases:** a dropped connection has 20 s to reconnect, and its character stands still meanwhile. Quitting the desktop app closes its connection as a leave (close code 4000), so it doesn't hold the seat for those 20 s; and the game never joins the same town twice at once. A character can only be played on one tab or device at a time: anywhere else the character screen shows them as in use, and the server refuses them (`CLOSE_IN_USE`). The exceptions are the same tab (a reload takes over from its old connection) and a connection that's dropped, which anyone on the account can take over rather than wait out the 20 s; the old session then goes back to the character screen. Each tab makes up its own id for this and keeps it in `sessionStorage`, so a duplicated tab counts as the same one. Spamming messages or repeated bad moves gets a client kicked. On SIGTERM every room saves before the process exits.

The server migrates its database on start and retries until the database is reachable. Sign-in works whether or not the database is up.

The realm is provisioned by `keycloak/init-keycloak.sh`, an idempotent `kcadm` script that runs on every deploy. Like topspin's, it is used instead of a one-shot realm import.

## Development

```
./dev.sh
```

`dev.sh` starts four things:
- **Keycloak** (Docker) on http://localhost:8080, with the realm provisioned in `dev` mode.
- **The game database** (Docker, Postgres 17) on localhost:5434 (`game` / `devpassword`, database `city`).
- **The auth + game server** on :3017.
- **The game** on http://localhost:5173: the Unity WebGL build, served by `unity/Tools~/serve.mjs`, which passes `/auth` and `/mp` (WebSockets too) through to the server.

The first run builds the game from `unity/`, which needs Unity 6000.6.5f1 with WebGL support. That takes a few minutes. `./dev.sh --build` rebuilds after a change, and the editor's **Porchlight ▸ Build WebGL** does the same.

To try multiplayer, sign in in two windows (a private one works), or open two tabs on one account and pick a different character in each. Then have them live in the same town, or join: once you've picked a character, **Where to?** offers each of your others that's playing, and **Join** under your name does the same from the town. Either way you arrive beside them, in whichever town they're in, even if the new character has no home yet. To load-test, run `npm run bots -- 100 60` in `server/` (100 bots for 60 s; the server logs tick times every minute). Run `npm run test:db` in `server/` to also test the Postgres code against the dev database.

There are two dev accounts:

| What | Where | Login |
| --- | --- | --- |
| The game (porchlight realm) | http://localhost:5173 | **dev / dev** |
| Keycloak admin console (master realm) | http://localhost:8080/admin | admin / admin |

Without Docker there's no Keycloak or game server: the game finds no sign-in service and starts blank in dev mode: any seed, the blueprint and its settings, nothing saved.

## Production

`porchlight.joebad.com` hosts the game and `auth.porchlight.joebad.com` hosts Keycloak. Both names are set in `.github/workflows/deploy.yml` (`DOMAIN`) and `terraform/main.tf`.

**The box is a t4g.small** (arm64, 2 burstable vCPUs, 2 GB RAM, 2 GB swap, a 20 GB root disk). It started as a t4g.micro (1 GB), where the stack's ~0.9 GB lived partly in swap; 2 GB holds it all in memory:
- A t4g.nano (0.5 GB) is too small for the JVM.
- At rest, measured on arm64 with the production settings:

  | Service | Memory |
  | --- | --- |
  | Keycloak | ~490 MB |
  | Postgres | ~50 MB |
  | Auth + game server | ~110 MB idle, ~125 MB with 30 players (heap capped at 160 MB) |

  Caddy and the OS take the rest.
- Nothing is built from the game on the box. The only image builds there are the Keycloak and auth-service images.
- **Resizing:** change `instance_type` (or a disk size) in `terraform/main.tf`. A new instance type stops and starts the box in place: same disks, same Elastic IP, a minute or two of downtime. Disks grow in place too, and `deploy.sh` grows the partition and filesystems to match. EBS allows one change per volume every six hours.

**Persistent data** lives on a separate 10 GB EBS volume mounted at `/data`: the Postgres server and Caddy's certificates. To keep one Postgres on the small box, that server holds two databases: `keycloak`, and `porchlight` for the game. Each has its own role. `deploy.sh` creates the game's database and role, and resets that role's password (`GAME_DB_PASSWORD`) from Secrets Manager on every deploy. The volume has `prevent_destroy`, and AWS takes a daily snapshot and keeps 7.

**Bandwidth and crawlers.** In November 2026 a crawler (Reflectionbot) ran up about 3.6 TB of EC2 egress, roughly $310, in ten days (#26). Every made-up path answered with the game's page, and each page it rendered pulled the ~24 MB build. What's in place now:
- **Only real files are served.** Unknown paths are a 404, not the game (`docker/Caddyfile`).
- **The build comes from a CDN.** `cdn.porchlight.joebad.com` is CloudFront in front of the box, serving `/Build/*` only (`terraform/main.tf`). CloudFront keeps the build at the edge for a day, and each deploy invalidates `/Build/*` (deploy.yml), so the box sends each build file about once per edge per deploy. Browsers revalidate with the edge (a 304), not with the box. The page loads the whole build, loader included, from the CDN only (`unity/Assets/WebGLTemplates/Porchlight/index.html`).
- **Only CloudFront can fetch the build from the box.** CloudFront sends a secret header (`X-Porchlight-Origin`, Terraform's `random_password`, kept in the `porchlight/cdn-origin` secret). `deploy.sh` hands it to Caddy, and Caddy answers 403 to `/Build/*` without it. So nobody can skip the CDN and pull the build at EC2 prices. `deploy.sh` stops if the secret is missing, because Caddy won't start with an empty one. The deploy waits for CloudFront to finish deploying before Caddy starts checking. CloudFront's first 1 TB a month is free, then $0.085/GB.
- **Known bad crawlers are dropped** by user agent in the Caddyfile, and `/robots.txt` keeps the rest out of `/Build/`, `/mp/`, `/auth/` and `/manage/`.
- **Alarms** email the `porchlight-alerts` topic within the hour if the box sends over 5 GB in an hour (`porchlight-egress`) or the CDN sends over 10 GB (`porchlight-cdn-egress`). An AWS Budget (`data-transfer-out`, $10 a month, made by hand) catches it on the bill too, a day later.
- The CloudWatch dashboard `porchlight-egress` graphs the box's egress.

### One-time setup

1. **AWS credentials** for the deploy. The IAM user needs rights over:
   - EC2/VPC, IAM roles and instance profiles, Secrets Manager, DLM;
   - S3, for the state bucket `porchlight-terraform-state-<account>`, which the workflow creates;
   - Route53 on the `joebad.com` zone (the CDN's records, and optionally the two A records);
   - CloudFront and ACM (the build's CDN), and CloudWatch alarms and SNS topics named `porchlight-*`.

   The local, untracked `scripts/provision.local.sh` (`*.local.sh` is gitignored) creates the user with exactly these.
1. **Alert email.** Subscribe yourself to the alarms' topic (Terraform's `alerts_topic_arn` output), and click the link AWS emails:
   `aws sns subscribe --topic-arn <alerts_topic_arn> --protocol email --notification-endpoint <you>`
   It's done by hand so the address stays out of this public repo.
2. **A Google OAuth client** (web application) in the Google Cloud console:
   - Authorized redirect URI: `https://auth.porchlight.joebad.com/realms/porchlight/broker/google/endpoint`
   - Authorized JavaScript origin: `https://auth.porchlight.joebad.com`
3. **GitHub repository secrets:**

   | Secret | What |
   | --- | --- |
   | `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | the deploy user |
   | `EC2_SSH_PRIVATE_KEY` | a private key; its public half is installed on the box |
   | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | from step 2 |
   | `UNITY_EMAIL`, `UNITY_PASSWORD` | the Unity account the build runs as |
   | `UNITY_LICENSE` | that account's licence file: the contents of `Unity_lic.ulf` (on a Mac, `/Library/Application Support/Unity/`) |

4. **DNS.** The workflow upserts both A records if `joebad.com` is a Route53 zone in that account. Otherwise it prints them as a warning; add them where the zone lives:
   - `porchlight.joebad.com A <elastic ip>`
   - `auth.porchlight.joebad.com A <elastic ip>`

   Caddy keeps retrying certificates until the records resolve.

### Deploys

Every push to `main` that touches `unity/`, `server/`, `infra/` or the workflow deploys. So does running the workflow by hand. Each run:
1. Checks the game's town generator, builds the game for WebGL in Unity's Docker image ([game-ci](https://game.ci); the import cache, `unity/Library`, is kept between runs), and tests and typechecks the auth service.
2. Runs `terraform apply`. This is a no-op when nothing changed.
3. Copies `infra/`, `server/` and the game's build to `/opt/porchlight`.
4. Runs `infra/docker/deploy.sh` on the box. That script:
   - generates any missing secrets (database and admin passwords, client and session secrets) and stores them in Secrets Manager under `porchlight/prod`;
   - starts the stack;
   - waits for Postgres, then creates the game's database and role if they're missing;
   - waits for Keycloak, then runs `init-keycloak.sh prod`.

### On the box

```
ssh ec2-user@<elastic ip>
cd /opt/porchlight/infra/docker
docker compose ps
docker compose logs -f keycloak
```

The Keycloak admin console is at `https://auth.porchlight.joebad.com/admin`. The username is `admin`, and the password is `KEYCLOAK_ADMIN_PASSWORD` in the `porchlight/prod` secret.

### Investigating lag, errors and restarts

The game server (`auth`) logs a line for each of these. Sessions and characters are named by the first 8 characters of their ids, never by name.

| Line | When |
| --- | --- |
| `process: started <commit> …` | Start, with the commit the deploy wrote to `server/src/REVISION`. |
| `process: SIGTERM after …` / `process: exit <code> …` | A stop: which signal, uptime, how many were playing. Exit 0 after a signal is a deliberate stop; 1 is a crash, with the `uncaught exception` line above it. |
| `process: uncaught exception after … : <stack> … caused by: …` | A crash, told in full, cause and all (Colyseus's own printing skips its `ServerError`s, so before beta #2's fixes a crash could exit 1 with nothing said). |
| `process: unhandled rejection after …, carrying on: <stack>` | A promise nothing caught. Told in full, and the process carries on rather than take every room down for it. |
| `process: N player(s) … event loop p99 … max …` | Every minute while anyone plays (or the loop stalled over 200 ms). A high max is lag for everyone at once. |
| `session <id> start: … web/Windows 11, web.87 · a1b2c3d, device 9f8e7d6c, Firefox/153.0` | Each join: platform, OS, build, the game's anonymous install id, and the browser. |
| `perf <id>: 58 fps, 3 slow frame(s), worst 140 ms, rtt 45 ms (max 120) …` | Once a minute from each game: its frame rate, frames over 50 ms, and the room's measured round trip. |
| `session <id> end: kicked (too many messages) after 12.0 min, 1 drop(s) …` | Each leave: why (and the kick's reason or close code), drops, and the session's frame rate and round trips. |
| `city <room>: … tick avg …[, N not acked (M message(s) held)]` | The room's tick each minute, and any client that hasn't acked its join, with how many messages Colyseus is holding for it. |
| `city: session <id> … never acked its join: sending it off (M message(s) held)` | A client 30 s past joining that never acked the room's `JOIN_ROOM` (a hidden web tab: its loop doesn't run). Until it acks, Colyseus keeps every message to it and frees none, which grew the process by ~80 MB an hour in beta #2. It's sent off with close code 4104 (`CLOSE_JOIN_UNACKED`), and its game joins again once it runs. |
| `session <id>: dropped, and can't come back (not joined)` | A client that dropped before it acked its join: no reconnection window, it just ends. (Uncaught, Colyseus's refusal took the whole process down: four restarts in beta #2.) |
| `sim: N town(s), … avg/max ms, heap H MB (committed C MB), gc g0/g1/g2, gc pause P ms; <town>: Cc/Pp life …, events due/heap, waits …, flow …, failures …, walkers …, taken …` | The simulation each minute: its tick, its managed heap and collections, and each town's sizes that could grow while it runs. One that climbs hour on hour is what's filling the heap. |
| `sim 0: out of memory after N min: stopping it now` | The worker printed `Out of memory.`: killed at once and started again, rather than waiting the ~2 min 21 s it took to exit on its own (every town's traffic frozen meanwhile). The heap's held to 320 MB (`DOTNET_GCHeapHardLimit`), with crash dumps off (`DOTNET_DbgEnableMiniDump=0`). When a worker starts again it makes the towns with players in them first. |

```
docker compose logs --timestamps --since 2h auth | grep -E 'session|perf|process'
```

The same is kept on the `sessions` table: `client` (what it was played on), `perf` (the session's summary), `end_reason`, `end_detail` and `drops`. For example, everyone's sessions on Macs in the last day:

```
docker compose exec postgres psql -U porchlight porchlight -c "select id, started_at, end_reason, end_detail, drops, client->>'os' os, perf->>'fpsAvg' fps, perf->>'rttMax' rtt_max from sessions where client->>'platform' = 'mac' or client->>'ua' like '%Macintosh%' order by started_at desc limit 50"
```

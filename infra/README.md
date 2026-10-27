# Porchlight infrastructure

Sign-in works the way it does in topspin: Keycloak sits in front of Google but the user never sees it. Everything runs on one small EC2 box.

```
browser ──https──> Caddy ──┬── /            static app (app/dist, built in CI)
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

- **Rooms:** everyone exploring the same town (seed and params) shares a room, up to 100 players. Past that, a second copy of the town opens.
- **Characters:** one account (one Google sign-in) has up to 8 characters. Several of them can be played at once from different tabs or devices, so two people can share an account. After sign-in, explore mode asks **Who's playing?** (`/mp/characters`). The choice is remembered for that tab only.
- **Auth:** joining checks the same `pl_session` cookie. It goes along because `/mp` is same-origin. The room also checks the character belongs to that account. Nothing new is stored in the page.
- **Live state** is in memory only. Clients send moves about 30 times a second while moving, each stamped with when the client took it. The server checks each one as it arrives (well-formed, inside the town, in order, no faster than a boosted car) and snaps back any that fail. It sends each client only the players within a cell or two of them (16-tile cells), as delta patches about 30 times a second. Clients play each other player back on that player's own timestamps, about 120 ms behind, so motion stays even however the network bunches the moves up. Everyone else in the room shows as a minimap dot, updated once a second.
- **Saved state** goes to Postgres, through Drizzle (tables in `server/src/multiplayer/tables.ts`; migrations generated into `server/drizzle` by `npm run db:generate`, and applied by the server on start): accounts, characters, each character's last position (per town) and look, play time, and sessions. Writes are batched every 30 s, plus on leave and on shutdown. A failed write is retried later and never holds up the game. If the database is down, joins are refused.
- **Edge cases:** a dropped connection has 20 s to reconnect, and its character stands still meanwhile. Opening the same character again elsewhere signs out the older session ("newest wins"). Spamming messages or repeated bad moves gets a client kicked. On SIGTERM every room saves before the process exits.

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
- **Vite** on http://localhost:5173, which proxies `/auth` and `/mp` (WebSockets too) to the server.

To try multiplayer, sign in in two windows (a private one works), or open two tabs on one account and pick a different character in each. Then explore the same town: copy the URL, which carries the seed. To load-test, run `npm run bots -- 100 60` in `server/` (100 bots for 60 s; the server logs tick times every minute). Run `npm run test:db` in `server/` to also test the Postgres code against the dev database.

There are two dev accounts:

| What | Where | Login |
| --- | --- | --- |
| The app (porchlight realm) | http://localhost:5173 | **dev / dev** |
| Keycloak admin console (master realm) | http://localhost:8080/admin | admin / admin |

Without Docker, `dev.sh` turns sign-in off (`VITE_AUTH_ENABLED=false`).

## Production

`porchlight.joebad.com` hosts the app and `auth.porchlight.joebad.com` hosts Keycloak. Both names are set in `.github/workflows/deploy.yml` (`DOMAIN`) and `terraform/main.tf`.

**The box is a t4g.small** (arm64, 2 burstable vCPUs, 2 GB RAM, 2 GB swap, a 20 GB root disk). It started as a t4g.micro (1 GB), where the stack's ~0.9 GB lived partly in swap; 2 GB holds it all in memory:
- A t4g.nano (0.5 GB) is too small for the JVM.
- At rest, measured on arm64 with the production settings:

  | Service | Memory |
  | --- | --- |
  | Keycloak | ~490 MB |
  | Postgres | ~50 MB |
  | Auth + game server | ~110 MB idle, ~125 MB with 30 players (heap capped at 160 MB) |

  Caddy and the OS take the rest.
- Nothing is built from the app on the box. The only image builds there are the Keycloak and auth-service images.
- **Resizing:** change `instance_type` (or a disk size) in `terraform/main.tf`. A new instance type stops and starts the box in place: same disks, same Elastic IP, a minute or two of downtime. Disks grow in place too, and `deploy.sh` grows the partition and filesystems to match. EBS allows one change per volume every six hours.

**Persistent data** lives on a separate 10 GB EBS volume mounted at `/data`: the Postgres server and Caddy's certificates. To keep one Postgres on the small box, that server holds two databases: `keycloak`, and `porchlight` for the game. Each has its own role. `deploy.sh` creates the game's database and role, and resets that role's password (`GAME_DB_PASSWORD`) from Secrets Manager on every deploy. The volume has `prevent_destroy`, and AWS takes a daily snapshot and keeps 7.

### One-time setup

1. **AWS credentials** for the deploy. The IAM user needs rights over:
   - EC2/VPC, IAM roles and instance profiles, Secrets Manager, DLM;
   - S3, for the state bucket `porchlight-terraform-state-<account>`, which the workflow creates;
   - Route53 on the `joebad.com` zone, optionally.
2. **A Google OAuth client** (web application) in the Google Cloud console:
   - Authorized redirect URI: `https://auth.porchlight.joebad.com/realms/porchlight/broker/google/endpoint`
   - Authorized JavaScript origin: `https://auth.porchlight.joebad.com`
3. **GitHub repository secrets:**

   | Secret | What |
   | --- | --- |
   | `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | the deploy user |
   | `EC2_SSH_PRIVATE_KEY` | a private key; its public half is installed on the box |
   | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | from step 2 |

4. **DNS.** The workflow upserts both A records if `joebad.com` is a Route53 zone in that account. Otherwise it prints them as a warning; add them where the zone lives:
   - `porchlight.joebad.com A <elastic ip>`
   - `auth.porchlight.joebad.com A <elastic ip>`

   Caddy keeps retrying certificates until the records resolve.

### Deploys

Every push to `main` that touches `app/`, `server/`, `infra/` or the workflow deploys. So does running the workflow by hand. Each run:
1. Tests and typechecks the app and the auth service, then builds the app.
2. Runs `terraform apply`. This is a no-op when nothing changed.
3. Copies `infra/`, `server/` and `app/dist` to `/opt/porchlight`.
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

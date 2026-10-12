# Porchlight

A low-poly town builder. You build towns in the browser; they're stored on the server; the
Nintendo Switch build loads them and lets you fly around.

## Layout

```
shared/        Engine-free C# (netstandard2.1, C# 9): DTOs, catalog, placement rules, edit commands, metrics
  com.porchlight.shared/   ← a Unity package (Unity imports it directly)
  Porchlight.Shared.csproj ← the same sources as a .NET library (server + tests)
  tests/                   ← xUnit tests for the rules
server/        ASP.NET Core (.NET 10) API + Npgsql; also serves the WebGL build
unity/         One Unity 6 (URP) project → WebGL builder build AND Switch viewer build
browser/       WebGL build output (dist/) and any web-only extras
infra/dev/     docker-compose for Postgres
infra/prod/    Fly.io config for the server and Postgres apps (deployed by GitHub Actions)
browser-dev.sh Starts Postgres + the server (optionally builds WebGL first)
```

### How code is shared

| Layer | Where | Used by |
|---|---|---|
| Town model, catalog, rules, commands, metrics, API routes | `shared/` | server, browser build, Switch build |
| Meshes, rendering, camera, builder tool, UI, HTTP client | `unity/Assets/Porchlight` | browser build, Switch build |
| Persistence, validation on save | `server/` | — |

The browser and the Switch are **two build targets of the same Unity project**. Editing is turned on
or off by `AppConfig.CanEdit`: off under `UNITY_SWITCH` or `PORCHLIGHT_VIEWER_ONLY`, or when
**Porchlight > Viewer Mode** is checked in the editor.

The server runs `TownRules.ValidateTown` on every save, the same rules the client used to allow each
edit, and stores `TownMetrics` as columns so the town list never loads layouts.

## Prerequisites

- Docker Desktop
- .NET 10 SDK
- Unity 6 (the project is pinned to 6000.6.5f1) with the **WebGL Build Support** module. The Switch module comes through the Nintendo Developer Portal.

## First run

1. Start the backend:
   ```sh
   ./browser-dev.sh
   ```
   This starts Postgres on `localhost:5433` and the API on http://localhost:5080. The schema is created on startup.
2. Open the Unity project. In Unity Hub: **Add > Add project from disk**, pick `unity/`, and choose a Unity 6 editor.
   On first open:
   - Packages resolve from `Packages/manifest.json`, including `/shared` as a local package.
   - The Input System package may ask to enable the new input backend. Either answer works, since `PInput` supports both. Pick **Yes** for Switch controllers.
   - `PorchlightProjectSetup` creates and assigns a URP asset, sets the WebGL player settings, and creates `Assets/Porchlight/Scenes/Main.unity`. If anything is skipped, re-run it from **Porchlight > Run Project Setup**.
3. Press **Play**. Create a town, then build.
4. To test in a browser, build WebGL (close the Unity editor first):
   ```sh
   ./browser-dev.sh --build
   ```
   Then open http://localhost:5080. You can also build from **Porchlight > Build > WebGL** and refresh the page; the server picks up new builds without a restart.

## Controls

| | Keyboard / mouse | Gamepad |
|---|---|---|
| Move | Arrow keys | Left stick |
| Up / down | Space / C (PageUp / PageDown) | Right / left trigger |
| Look | WASD (A/D turn, W/S tilt) or hold right mouse button | Right stick |
| Fast | Shift | Left stick click |
| Builder tools | 1 Build · 2 Road · 3 Remove · Q/E rotate · Esc end road | — |
| Back to towns (viewer) | Esc | East button |

## Production (Fly.io)

Production is driven entirely by GitHub Actions (`.github/workflows/deploy.yml`). Pushing to `main`
under `infra/prod/`, `server/`, `shared/` or `unity/` (or running the workflow by hand) runs it.
Every run is idempotent:

1. **infra** creates the Fly apps, the Postgres volume and the database credentials if they're missing.
   The password is generated once and stored only as Fly secrets.
2. **deploy-db** deploys Postgres when `infra/prod/db/` changed or it has no machine.
3. **webgl** builds the browser builder with [GameCI](https://game.ci), cached by the content of
   `unity/` + `shared/`, so server-only changes skip Unity.
4. **deploy-server** builds `infra/prod/server/Dockerfile` with that WebGL build inside, deploys it,
   and smoke-tests `/api/health`.

| App | What | Reachable at |
|---|---|---|
| `porchlight-server` | API + browser builder | https://porchlight-server.fly.dev |
| `porchlight-db` | `postgres:17-alpine` on a 1 GB Fly volume | `porchlight-db.internal:5432` (private network only) |

### One-time setup (repo secrets)

| Secret | How to get it |
|---|---|
| `FLY_API_TOKEN` | `flyctl tokens create org personal`. It must be an **org** token so the workflow can create apps. |
| `UNITY_LICENSE` | Contents of `/Library/Application Support/Unity/Unity_lic.ulf` (macOS). If that file doesn't exist, follow GameCI's [activation guide](https://game.ci/docs/github/activation) to create one. Without it, deploys are API-only. |
| `UNITY_EMAIL`, `UNITY_PASSWORD` | Your Unity ID login, used by GameCI to activate the license |

Set them with `gh secret set NAME`. Optionally set the repo variable `FLY_ORG` if you don't use `personal`.

The Switch and macOS viewer builds are compiled with `PORCHLIGHT_PROD`, so they talk to
https://porchlight-server.fly.dev. In the editor, toggle **Porchlight > Use Production Server**.

The server machine stops when idle and wakes on the next request (a few seconds cold). The
database machine runs all the time. Back it up with
`flyctl ssh console -a porchlight-db -C "pg_dump -U porchlight porchlight" > backup.sql`.

## Useful commands

```sh
dotnet test shared/tests/Porchlight.Shared.Tests      # rule tests
docker compose -f infra/dev/docker-compose.yml down   # stop Postgres (add -v to wipe data)
curl localhost:5080/api/towns                         # list towns
```

Point a non-WebGL build at another server with `-api http://host:5080` or `PORCHLIGHT_API`.
WebGL builds always use the server that served the page.

## Next steps

- **Multiplayer:** send `TownCommand`s over WebSockets (SignalR) instead of saving whole snapshots. The server applies them with the same `TownEditor`. Ids are generated by clients and new node ids come from segment ids, so replay is deterministic.
- **Real art:** replace `MeshCache.Building` with a type-id-to-prefab lookup (a ScriptableObject). Keep one material and atlas for batching.
- **UI:** move from IMGUI to UI Toolkit, with gamepad navigation for the Switch.
- **Scale:** chunk road and building meshes, use GPU instancing per type, and add a spatial hash to `TownRules` before raising `TownLimits`.
- **Roads:** curves (control points on `RoadSegmentDto`), T-junctions that split segments, and better junction meshes.
- **Schema:** `TownDto.SchemaVersion` is stored per town; add server-side migrations when the layout changes.

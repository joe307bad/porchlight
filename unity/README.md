# Porchlight in Unity

The whole front end of Porchlight, written in C#, so it can be built for consoles as well as the web. It is a port of `app/` (the TypeScript and three.js game). The server stays as it is: `server/`, Node and Colyseus.

It is meant to look exactly like the web game:

- **Same towns.** The generator gives the same output bit for bit.
- **Same simulation.** The explore logic (walking, driving, traffic, auto-walk) is a direct port.
- **Same look.** The shaders reproduce three.js r186's Lambert and Standard shading, its hemisphere light, sun shadows and fog.

Unity 6000.6.5f1 with URP, the Input System and UI Toolkit.

## Layout

| Folder | What's in it |
| --- | --- |
| `Assets/Porchlight/Runtime/Shared` | Engine-free C#: core math, the town generator, the explore logic, and the network client (msgpack, the Colyseus schema decoder, rooms). It builds on plain .NET too. |
| `Assets/Porchlight/Runtime/Game` | The Unity side. `Rendering`: the 3D town, people and cars. `UI`: the blueprint, sidebar, overlay, sign-in and picker. `App`: the explore view, input, the app itself (`PorchlightApp`, a port of `main.ts`) and the exports. `Net`: multiplayer, accounts and the platform layer. |
| `Assets/Porchlight/Resources/Porchlight` | Shaders, fonts, the panel settings, and `UI/Porchlight.uss` (`style.css` in UI Toolkit terms). |
| `Assets/Porchlight/Plugins/WebGL` | `Porchlight.jslib`: the browser half of the platform layer (WebSockets, the URL, storage, downloads). |
| `Assets/Porchlight/Editor/Setup.cs` | Project setup and builds, from the **Porchlight** menu or batch mode. |
| `Assets/WebGLTemplates/Porchlight` | The page around the WebGL build. |
| `Tools~` | Checks that run outside Unity (see below), and `serve.mjs`. |

The models aren't copied into the project. In the editor they're read straight from `app/public/models`. A build copies them into `Assets/StreamingAssets/models`, which is gitignored.

## Testing the browser build

1. **Build it.** Pick **Porchlight ▸ Build WebGL** in the editor, or run this from `unity/`:

   ```
   /Applications/Unity/Hub/Editor/6000.6.5f1/Unity.app/Contents/MacOS/Unity -batchmode -nographics \
     -projectPath . -executeMethod Porchlight.EditorTools.Setup.BuildWebGL -logFile -
   ```

   The build lands in `unity/Builds/WebGL`.
2. **Start the server** the usual way. `./dev.sh` starts Keycloak, the database and the server on :3017. It also starts Vite on :5173, so stop that one (Ctrl-C once the server is up). Or start just the server with `npm --prefix server run dev`.
3. **Serve the build** in Vite's place:

   ```
   node unity/Tools~/serve.mjs
   ```

   This serves the build at http://localhost:5173/ and passes `/auth` and `/mp` (WebSockets included) through to :3017. Sign-in cookies stay first-party, and the server's `APP_URL` still matches.
4. **Open http://localhost:5173/** and sign in as dev / dev.

Without the server, the build still runs: it finds no sign-in service, and you play alone.

## Running it in the editor

1. Open `unity/` in Unity 6000.6.5f1.
2. If it hasn't been done yet, run **Porchlight ▸ Set Up Project**. It sets up URP, the panel, the boot scene and the player settings, and is safe to run again.
3. Open `Assets/Porchlight/Scenes/Boot.unity` and press Play.

Off the web there's no page to take the address from, so the game talks to `PORCHLIGHT_ORIGIN`, or `http://localhost:3017` if that isn't set. There are no browser cookies either. Signing in is browser-only for now, so the editor plays offline unless you hand it a session.

To play online from the editor, set `PORCHLIGHT_COOKIE=pl_session=<value>` before starting Unity. You can copy the value from a signed-in browser's cookies, or use one that `Tools~/net/server.ts` prints against its test server.

## Checks outside Unity

`Tools~/parity/GenCheck` is a .NET 8 console app. It compiles the `Shared` code and checks it against the TypeScript game, using fixtures written by the `Tools~/parity/*.ts` exporters (run them with `npx --prefix app vite-node`):

```
dotnet run --project "unity/Tools~/parity/GenCheck" -- parity.json        # towns, bit for bit
dotnet run --project "unity/Tools~/parity/GenCheck" -- sim sim.json       # the explore simulation
dotnet run --project "unity/Tools~/parity/GenCheck" -- math math.json     # JS math (fdlibm ports)
dotnet run --project "unity/Tools~/parity/GenCheck" -- models models.json app/public/models
```

The network client runs against the real server:

```
node unity/Tools~/net/server.ts           # prints a line of JSON: the port and two test sessions
dotnet run --project "unity/Tools~/parity/GenCheck" -- net '<that line>'
```

`Tools~/check.sh` compile-checks every assembly with `dotnet`. It needs no Unity, but it does need the `.csproj` files Unity generates when it opens the project.

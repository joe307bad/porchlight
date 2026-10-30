# Porchlight

The game: grow a town from a seed, see its blueprint, then walk and drive its streets with everyone else exploring the same town. It's a Unity 6000.6.5f1 project in C#, using URP, the Input System, UI Toolkit, TextMeshPro and glTFast. It builds for the web (WebGL) and is set up for consoles and desktop.

The server is `server/`, Node and Colyseus. It handles sign-in, the characters, and the shared town rooms.

## Layout

| Folder | What's in it |
| --- | --- |
| `Assets/Porchlight/Runtime/Shared` | Engine-free C#: the town generator, the explore simulation (walking, driving, traffic, directions), and the network client (msgpack, the Colyseus schema decoder, rooms). It builds on plain .NET too, so `Tools~/Checks` can test it without Unity. |
| `Assets/Porchlight/Runtime/Game` | The Unity side. `Rendering`: the 3D town as GameObjects, people, cars, the sky and lights. `UI`: the blueprint, sidebar, overlay, sign-in and character picker (UI Toolkit). `App`: the explore view, input, and the app itself (`PorchlightApp`). `Net`: multiplayer, accounts, and the platform layer. |
| `Assets/Porchlight/Models` | The Kenney and RG Poly models (glTF), imported by glTFast. |
| `Assets/Porchlight/Editor` | `Setup.cs`, which sets up the project and makes the builds (the **Porchlight** menu, or batch mode). `Library.cs` turns the models into the game's prefabs. |
| `Assets/Porchlight/Resources/Porchlight` | Fonts, the UI's stylesheet (`UI/Porchlight.uss`) and panel settings, and the generated model library. |
| `Assets/Porchlight/Plugins/WebGL` | `Porchlight.jslib`: the browser side of the platform layer (WebSockets, the page's URL, storage, downloads). |
| `Assets/WebGLTemplates/Porchlight` | The page around the WebGL build. |
| `Tools~` | `Checks` (the .NET test harness), `net/server.ts` (a throwaway game server for the network check), `serve.mjs` (serves a build for development), and `check.sh`. |

## How it draws

Everything is Unity's own; there are no custom shaders:

- **Models:** glTFast imports the models. **Porchlight ▸ Set Up Project** runs `Editor/Library.cs`, which turns each model into a prefab with URP Lit materials and records its bounds. For the city kits, it also masks each colour map's window glass so the windows can glow at night, and cuts the tall kits' big panes into a grid of smaller ones. All of this goes in `Assets/Porchlight/Generated` and `Resources/Porchlight/Library.asset`, which are gitignored.
- **People:** the characters share one armature. Their clips (`people/animations.glb`) are rebound to it, and an Animator controller blends idle, walk and run by pace, with jump, fall and land in the air.
- **The town:** GameObjects. What never moves is combined with static batching. Procedural parts (ground, roads, walls) are meshes coloured through a palette texture, so they draw with URP Lit like everything else.
- **Light:** a directional sun with URP soft shadows, Trilight ambient, and linear fog for the close-up camera. Clouds by day and twinkling stars by night are particle-material quads on a shell round the camera, drawn first and nearer than the fog (plus flat clouds over the town for the orbit camera). A global volume adds bloom and Neutral tonemapping. At night, bulbs and windows glow, and a few real point lights follow the street lamps nearest the camera.
- **Showing through buildings:** two URP Render Objects passes. One draws you (and the car you drive) in orange wherever you're hidden. The other draws name tags (TextMeshPro) over everything.

## Running it

**In the editor:** open `unity/` in Unity 6000.6.5f1. A fresh checkout builds its model library as it opens; run **Porchlight ▸ Set Up Project** to rebuild it. Then open `Assets/Porchlight/Scenes/Boot.unity` and press Play.

Off the web there's no page address and no browser cookies. The game talks to `PORCHLIGHT_ORIGIN`, or `http://localhost:3017` if that isn't set. It plays offline unless you hand it a session with `PORCHLIGHT_COOKIE=pl_session=<value>` (copy it from a signed-in browser).

**In the browser:** from the repository root, run

```
./dev.sh
```

This starts Keycloak, the database and the server. It builds the game if there's no build yet, and serves it at http://localhost:5173/ (sign in as dev / dev). After changing the game, run `./dev.sh --build`, or **Porchlight ▸ Build WebGL** in the editor.

**Deploys:** pushing to `main` builds the WebGL player in CI and ships it. See `infra/README.md`.

## Checks

```
dotnet run --project "unity/Tools~/Checks" -- towns 60     # towns grow with every invariant checked, and repeat exactly

node unity/Tools~/net/server.ts                              # a game server; prints a line of JSON
dotnet run --project "unity/Tools~/Checks" -- net '<that line>'   # two C# clients play it
```

To smoke-test the whole app without a window, build macOS (**Porchlight ▸ Build macOS**) and run it like this:

```
PORCHLIGHT_SMOKE=1 Builds/macOS/Porchlight.app/Contents/MacOS/Porchlight -batchmode -logFile -
```

It grows a town, explores, auto-walks for 8 seconds, goes back to the map, regenerates and quits, logging `smoke:` lines along the way. Leave out `-nographics`: without a GPU, text and instanced drawing throw.

`Tools~/check.sh` compile-checks every assembly with `dotnet`, using the `.csproj` files Unity generates.

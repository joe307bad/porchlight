# Porchlight

The game: sign in, make a character, choose a town and a house in it, then walk and drive its streets with everyone else who's there, playing missions for rewards. With no sign-in service it starts blank in dev mode, where you grow towns from a seed, see their blueprints and explore them alone. It's a Unity 6000.6.5f1 project in C#, using URP, the Input System, UI Toolkit, TextMeshPro and glTFast. It builds for the web (WebGL) and is set up for consoles and desktop.

The server is `server/`, Node and Colyseus. It handles sign-in, the characters, the shared town rooms, and the missions: [ink](https://github.com/inkle/ink) stories in `server/missions` that the server compiles and the game plays, so new ones need no new build (see `server/missions/README.md`).

## Layout

| Folder | What's in it |
| --- | --- |
| `Assets/Porchlight/Runtime/Shared` | Engine-free C#: the town generator, the county the towns make (`County/`: where each goes, the highways between them, which room owns which ground, and each road space as a town of its own), the explore simulation (walking, driving, traffic, directions), the network client (msgpack, the Colyseus schema decoder, rooms), and missions as they play (`Missions/`, running ink). It builds on plain .NET too, so `Tools~/Checks` can test it without Unity. |
| `Assets/Porchlight/Runtime/Game` | The Unity side. `Rendering`: the 3D town as GameObjects, people, cars, the sky and lights. `UI`: the blueprint, sidebar, overlay, sign-in, character picker and a mission's conversation (UI Toolkit). `App`: the explore view, input, a mission's people and parking spots (`MissionPlay`), driving the county from town to town (`CountyDrive`), and the app itself (`PorchlightApp`). `Net`: multiplayer, accounts, the mission list, and the platform layer. |
| `Assets/Ink` | ink's runtime engine, as it is upstream (MIT), which plays the missions. |
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

## Driving the county

Signed in, the town on screen is one of the county's (see `infra/README.md`, The county), and you can drive out of any of its gates along the highway to the next town, or take a plane and fly anywhere over the fields, with no loading screen. `CountyDrive` does it:

- **Spaces:** each town, the junction north of Porchlight, each of your empty plots and each stretch of highway (at most 180 tiles) is a space with its own tile grid; a road space is built as a town of its own (`County/RoadSpaces`), so walking, driving and drawing take it as they are. You're in one at a time; next to each other they overlap a little (a highway reaches 3 tiles into the town at its end), so the car is on both while it crosses.
- **Building as you go:** road spaces within 240 tiles of you are built, and the nearest towns within 330 (two at most: the one you're in and the next), a few milliseconds a frame. Off the web a town grows on a worker thread; on the web a stage a step. Spaces well behind are let go. A town's parts (its houses, its rooms) are each still built in one go, so a big town can cost a hitch of a frame or two as it goes up.
- **Moving in:** over the line into the next space, once it's built, the view moves into it (`ExploreView.SwitchSpace`): you and the car you're driving carry on, in its terms; the space you've left keeps nothing of yours. The space you're in is always at the scene's origin, so every built space shifts by the same amount and nothing on screen moves. If the next space isn't built yet, you wait at its edge. Missions keep you in the town you started them in, and start only in a town.
- **The countryside:** one ground under the whole county, with holes for the towns. Off a town's map is open country: a plane flies low and lands anywhere, and a car can follow it.
- **Directions across the county:** under your name, a button for each house on the account (yours and your other characters') in this town or any town of the county, your own towns too. The dotted line and the turn-by-turn steps lead you out by the right gate, along each stretch of highway and through the junction, then along the town's streets to the door. Each second, `County/CountyRoute` works out where to head in the space you're in, and the way is kept from space to space. `Checks county` follows those directions with each space's real streets, from Porchlight and from Lanternport to a house in each of your towns.
- **Rooms:** over into another room's ground (see The county), this tab leaves its room and joins that one, carrying on from where it is; `NetFrame` gives each room positions in its own terms, and shares the room's traffic and townsfolk only while you're in its town (on the road, your traffic is your own).

## Running it

**In the editor:** open `unity/` in Unity 6000.6.5f1. A fresh checkout builds its model library as it opens; run **Porchlight ▸ Set Up Project** to rebuild it. Then open `Assets/Porchlight/Scenes/Boot.unity` and press Play.

Off the web there's no page address and no browser cookies. The game talks to `PORCHLIGHT_ORIGIN`, or `http://localhost:3017` if that isn't set. It starts in dev mode unless you hand it a session with `PORCHLIGHT_COOKIE=pl_session=<value>` (copy it from a signed-in browser).

**In the browser:** from the repository root, run

```
./dev.sh
```

This starts Keycloak, the database and the server. It builds the game if there's no build yet, and serves it at http://localhost:5173/ (sign in as dev / dev). After changing the game, run `./dev.sh --build`, or **Porchlight ▸ Build WebGL** in the editor.

**Deploys:** pushing to `main` builds the WebGL player in CI and ships it. See `infra/README.md`.

## Checks

```
dotnet run --project "unity/Tools~/Checks" -- towns 60     # towns grow with every invariant checked, and repeat exactly
dotnet run --project "unity/Tools~/Checks" -- county server/test/county-vectors.json   # the county lays out, drives end to end, and the server's copy agrees

node unity/Tools~/net/server.ts                              # a game server; prints a line of JSON
dotnet run --project "unity/Tools~/Checks" -- net '<that line>'   # two C# clients play it

(cd server && node scripts/mission.ts delivery) > /tmp/delivery.json
dotnet run --project "unity/Tools~/Checks" -- mission /tmp/delivery.json   # a mission plays to its end
```

To smoke-test the whole app without a window, build macOS (**Porchlight ▸ Build macOS**) and run it like this:

```
PORCHLIGHT_SMOKE=1 Builds/macOS/Porchlight.app/Contents/MacOS/Porchlight -batchmode -logFile -
```

It grows a town, explores, auto-walks for 8 seconds, goes back to the map, regenerates and quits, logging `smoke:` lines along the way. Leave out `-nographics`: without a GPU, text and instanced drawing throw.

`Tools~/check.sh` compile-checks every assembly with `dotnet`, using the `.csproj` files Unity generates.

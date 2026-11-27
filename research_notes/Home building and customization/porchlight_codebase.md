# Porchlight codebase: home building and customization

All sources are repo-relative paths in /home/user/porchlight, cited with line numbers (`file:line`). Abbreviations: `RT/` = `unity/Assets/Porchlight/Runtime/`, `SV/` = `server/src/multiplayer/`.

## How houses/buildings are generated, what defines a house, and is it deterministic and shared with the server?

### Takeaway
A house is a rectangle of whole tiles on a lot, with only `kind`, `floors` and `facing`. Generation is a pure, seeded C# pipeline. The 3D model, colour, roof, doors and windows are not in the model: the renderer picks a Kenney glTF prefab and a colour-map "variation" from a sequential RNG. The server never generates towns. It knows a town only by its key string, and a house only by one anchor tile.

### Cited Findings
- `TownModel` is "the single contract between generation and every renderer", plain data with integer ids — [RT/Shared/Model/Types.cs:1-3](unity/Assets/Porchlight/Runtime/Shared/Model/Types.cs). `Building` = `id, lotId, footprint (List<Vec2>), tiles (List<TilePos>), kind, floors, facing` — [Types.cs:138-148](unity/Assets/Porchlight/Runtime/Shared/Model/Types.cs). `BuildingKind { House, Shop, Workshop, Church, Hall }` — [Types.cs:45](unity/Assets/Porchlight/Runtime/Shared/Model/Types.cs). `TileGrid.buildingId[]` maps tiles to buildings — [Types.cs:220-221](unity/Assets/Porchlight/Runtime/Shared/Model/Types.cs).
- Pipeline: Terrain, Rivers, Site, Airport, Zoo, Roads, Streets, Blocks, Lots, Buildings, Rasterize, Docks. Each stage gets its own RNG stream `Rng.Stage(seed, stage.Name)`, and the header says "Pure: no engine, no rendering, no System.Random" — [RT/Shared/Gen/Generator.cs:1-2, 36-66](unity/Assets/Porchlight/Runtime/Shared/Gen/Generator.cs). `GenerateSteps` yields between stages so the game can draw while it waits — [Generator.cs:72-86](unity/Assets/Porchlight/Runtime/Shared/Gen/Generator.cs).
- Lots: each block is split recursively across its longer side down to `lotMaxSize`. The 3 lots nearest the centre become the plaza — [RT/Shared/Gen/Lots.cs:1-4, 15-17](unity/Assets/Porchlight/Runtime/Shared/Gen/Lots.cs).
- BuildingsStage: one building per lot at most. It is the largest rectangle whose whole front faces the road (`BestRect`, capped at `maxBuildingSize`), then `SetBack` trims it. The footprint is inset 0.15 (`INSET`) and drawn inset 0.04 (`DRAWN_INSET`). Kind comes from `PickKind` (shops/towers in the centre, workshops past 0.7 of the radius, otherwise House). Floors come from `FloorsFor` (houses: 1–2 within half the radius, else 1). The biggest central lot becomes the Church, and the building with ≥9 tiles nearest the plaza becomes the Hall — [RT/Shared/Gen/Buildings.cs:1-6, 17-19, 58-135, 383-400](unity/Assets/Porchlight/Runtime/Shared/Gen/Buildings.cs). `CheckBuildings` enforces no overlap, no water/road/sidewalk, and front meets road — [Buildings.cs:139-203](unity/Assets/Porchlight/Runtime/Shared/Gen/Buildings.cs).
- Params that affect houses: `lotMinSize, lotMaxSize, buildingDensity, maxBuildingSize, frontSetback, innerCity, downtown, towerFloors, towerShare, courtyards, driveways, gardens` — [RT/Shared/Model/Params.cs:43-61](unity/Assets/Porchlight/Runtime/Shared/Model/Params.cs). These are town-wide sliders (`ParamSpec.All`, with group, min, max and step) — [Params.cs:179-229](unity/Assets/Porchlight/Runtime/Shared/Model/Params.cs). No per-house params exist.
- Layout (pure) turns buildings into `House { x, y, tiles, facing, kind, style (HouseStyle: House/Workshop/Shop/Office/Hall/Church), floors, buildingId, blockId }` — [RT/Shared/Explore/Layout.cs:17-29](unity/Assets/Porchlight/Runtime/Shared/Explore/Layout.cs). It also places yards, driveways (+ parked car), sheds, garden beds and fences, using a deterministic `Hash(b.id+7919, k)` per house — [Layout.cs:255-297, 410-421](unity/Assets/Porchlight/Runtime/Shared/Explore/Layout.cs). Use codes: HOUSE/YARD/GARDEN/SHED/DRIVEWAY… — [Layout.cs:82-98](unity/Assets/Porchlight/Runtime/Shared/Explore/Layout.cs).
- Exterior look is chosen in rendering. `AddHouses` picks the candidate model set (`Catalog.KindModels(style)`; houses use `suburban/building-type-a..u`, 21 models). `BestSplit` cuts the frontage into 1–4 "pieces", each its own prefab. `variation = rng.Int(0, colours-1)` is picked once per house and shared by its pieces. Models are scaled non-uniformly to fit, and height comes from `HeightFactor`/floors — [RT/Game/Rendering/WorldParts.cs:58-111, 252-307](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs); [RT/Shared/Explore/Catalog.cs:20, 235-243](unity/Assets/Porchlight/Runtime/Shared/Explore/Catalog.cs).
- "Colour" of a building = one of the kit's alternative colour maps: suburban has `variation-a/b/c` (so 4 options), commercial 2, industrial 3 — [Catalog.cs:245-257](unity/Assets/Porchlight/Runtime/Shared/Explore/Catalog.cs). Applied by `Materials.ForModel`, which swaps the base map texture per (material, variation) and caches it — [RT/Game/Rendering/Materials.cs:164-184](unity/Assets/Porchlight/Runtime/Game/Rendering/Materials.cs); [RT/Game/Rendering/Props.cs:14-38](unity/Assets/Porchlight/Runtime/Game/Rendering/Props.cs).
- Roof, doors and windows are baked into each Kenney prefab. `Editor/Buildings.cs` detects each prefab's ground-floor room, doors (moving parts, swing or garage-lift), window panes and walking-height walls at import time and stores them in `ModelLibrary.Ground` — [unity/Assets/Porchlight/Editor/Buildings.cs:1-12](unity/Assets/Porchlight/Editor/Buildings.cs); [RT/Game/Rendering/ModelLibrary.cs:29-71](unity/Assets/Porchlight/Runtime/Game/Rendering/ModelLibrary.cs).
- The renderer RNG is one sequential stream `new Rng($"{model.seed}::iso")`, shared by ground, houses, yards, trees and lamps in order. Rooms use a separate stream `"{seed}::rooms"` "so the rest of the town doesn't move when they change" — [RT/Game/Rendering/World.cs:138-151](unity/Assets/Porchlight/Runtime/Game/Rendering/World.cs).
- Houses are named by a tile, not an id: "Ids are generation order, which any change to the generator can renumber". `Homes.All` = House-kind buildings that Layout actually builds. `Anchor` = `tiles[0]`. `At(x,y)` finds by any tile — [RT/Shared/Explore/Homes.cs:1-30](unity/Assets/Porchlight/Runtime/Shared/Explore/Homes.cs).
- The server only stores town keys (`seed=...&param=value`), validated by regex — [SV/persistence.ts:150-156](server/src/multiplayer/persistence.ts). For the starter town, the C# checks tool writes the house tiles to `server/towns/porchlight.json` (`npm run town`), because "only the game can generate it" — [SV/persistence.ts:110-122](server/src/multiplayer/persistence.ts); [server/package.json "town" script](server/package.json); [server/towns/porchlight.json](server/towns/porchlight.json).

### Inferences
- Exterior options that exist without new art: (1) colour variation (0–3 for suburban), (2) choice among the 21 suburban models (this changes the room, door and window layout, because `Ground` comes per prefab), (3) yard features (shed, garden beds, driveway, fence) driven by `use[]` in Layout. Custom roofs, doors or windows would need new art, or procedural meshes through `MeshBuilder`/`Palette`. Kenney kits ship fixed roofs.
- Any override has to keep the `::iso` RNG stream unchanged for every other house: still call `rng.Int`/`PickModel` as now, then replace the result. Otherwise trees, lamps and other houses after this one shift for everyone. Model choice also feeds `rooms`, `boxes`, `groundWalls` and `doors`, so swapping a model changes collision and door positions on every client that renders the override.
- Customization must be keyed by the home's anchor tile (town key + x,y), as homes are now, never by building id.

### Gaps
- No explicit statement on whether the Kenney suburban kit includes separate roof/door meshes that could be recoloured independently. Materials are split per kit colour map, not per part. This needs a check in the editor's Asset Browser (`Editor/AssetBrowser.cs`).

## How do interiors work: rooms, furniture, catalog, meshes?

### Takeaway
Every building piece has one ground-floor room. Its rect, doors and windows come from the prefab's imported `Ground` data. `Interiors.FurnishRoom` (pure, seeded) furnishes it procedurally by `HouseStyle`, choosing from `Catalog.FURNITURE` (about 80 Kenney furniture models). The output is a `RoomPlan` of `Placement`s (name, x, y, lift, rot, box, flat). There is no player-editable furniture, no item metadata (price, category) and no persistence of interiors.

### Cited Findings
- `Room { id, buildingId, x0,y0,x1,y1, facing, style, height, doors, windows }`. The room-local frame has u along the front and v into the room. Furniture models face +Z — [RT/Shared/Explore/Interiors.cs:1-29](unity/Assets/Porchlight/Runtime/Shared/Explore/Interiors.cs). `Placement { name, x, y, lift, rot, box (Rect), flat }`, `RoomPlan { furniture, keeper }` — [Interiors.cs:53-72](unity/Assets/Porchlight/Runtime/Shared/Explore/Interiors.cs). `RoomFrame.ToMap/ToLocal/RotFor` convert between the room frame and the map — [Interiors.cs:74-95](unity/Assets/Porchlight/Runtime/Shared/Explore/Interiors.cs).
- Constants: `WALL=0.07, WALL_HEIGHT=0.8, DOOR_WIDTH=0.46, DOORWAY=0.34, FURNITURE_SCALE=0.6` (tiles; 1 tile ≈ 4 m per Protocol) — [Interiors.cs:106-121](unity/Assets/Porchlight/Runtime/Shared/Explore/Interiors.cs).
- `FurnishRoom(room, size, rng)`: keeps clear boxes in front of every door, and `Put` rejects anything outside the inner rect or overlapping a door box or another non-flat item. Flat items are rugs. `on` items sit on top of others with `lift` = height of the item below. Helpers: `AlongWall(back/left/right)`, `OnTop`. Per-style recipes: Shop (counter + keeper + shelves), Office (desks with screens), Workshop, Hall/Church (benches), and default House (kitchen run on the back wall, sofa, TV cabinet + TV, rug, table + chairs or coffee table, plants, floor lamp, coat rack and doormat by the front door, 25% teddy bear) — [Interiors.cs:321-552](unity/Assets/Porchlight/Runtime/Shared/Explore/Interiors.cs).
- Catalog: `FURNITURE` lists the furniture model names (tables, chairs, desks, computers, kitchen units, appliances, sofas, lounge chairs, bookcases, plants, lamps, rugs, boxes, benches, stools, trashcan, radio, speaker, coat rack, TV cabinets, TVs, bear). `FURNISHINGS` = `furniture/<name>`. The header says wall-mounted furniture is deliberately left out — [RT/Shared/Explore/Catalog.cs:1-7, 207-226](unity/Assets/Porchlight/Runtime/Shared/Explore/Catalog.cs). Only models named in `Catalog.Statics` are built into the library — [Catalog.cs:229-232](unity/Assets/Porchlight/Runtime/Shared/Explore/Catalog.cs). Outdoor props also exist: TREES, BUSHES, FLOWERS, CROPS, ROCKS, GARDEN_EXTRAS (`pot_large`, `log_stack`…), STATUES, PARASOLS, `PROPS` (`suburban/planter`, `suburban/fence`, `commercial/detail-awning`) — [Catalog.cs:157-227](unity/Assets/Porchlight/Runtime/Shared/Explore/Catalog.cs).
- Sizes come from library bounds × `FURNITURE_SCALE` (`Size(name)` in `Furnish`). `Furnish` builds the global `Solids` collision grid from the buildings' walls (minus door passages), every room's walls, and every non-flat, unlifted furniture box — [RT/Game/Rendering/WorldParts.cs:309-326](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs).
- `InteriorOf(id)` builds a floor (`MeshBuilder.Rect`, floor colour per style from `FLOORS`) and walls per side (`MeshBuilder.Box`, cream 0xefe6d6 with a night glow). It then instantiates each furniture prefab with `Props.Place(... FURNITURE_SCALE ..., castShadows:false)` and caches the result in `interiors[id]` — [WorldParts.cs:380-428](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs). Every room's interior is built during `Build()`, 64 a step — [World.cs:154-160](unity/Assets/Porchlight/Runtime/Game/Rendering/World.cs).
- `RoomOf` converts a prefab's `Ground` (room rect, doors, windows) into map space — [WorldParts.cs:136-173](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs). A house with `split>1` yields several rooms sharing one `buildingId` — [WorldParts.cs:72-93](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs).
- Explore shows only nearby interiors (`ShowInteriors`, `INTERIOR_REACH`). Entering a room hides that building's shell (`SetShellVisible`), so the roof lifts — [RT/Game/App/ExploreView.cs:1947-1981, 1983-2022](unity/Assets/Porchlight/Runtime/Game/App/ExploreView.cs); [World.cs:240-249](unity/Assets/Porchlight/Runtime/Game/Rendering/World.cs).
- `MeshBuilder` builds flat-coloured quads, ribbons, walls and boxes in map terms, coloured through one `Palette` texture (one texel per colour, plus an optional emission texel), so everything draws with one URP Lit material — [RT/Game/Rendering/MeshBuilder.cs:1-4](unity/Assets/Porchlight/Runtime/Game/Rendering/MeshBuilder.cs); [RT/Game/Rendering/Palette.cs:1-61](unity/Assets/Porchlight/Runtime/Game/Rendering/Palette.cs).
- The whole static town (`still`, including the building shells) is merged by `StaticBatchingUtility.Combine` at the end of `Build()`. That is why shells are hidden by disabling renderers ("static-batched objects can't be switched off whole") — [World.cs:178-179, 244-249](unity/Assets/Porchlight/Runtime/Game/Rendering/World.cs). Interiors are parented to `root`, not `still` — [WorldParts.cs:384](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs).
- The README says assets are generated in code or imported Kenney/RG Poly glTF. There are no custom shaders, and the generated library is gitignored — [unity/README.md "How it draws"](unity/README.md).

### Inferences
- A furniture-placement feature can reuse `Placement`/`RoomFrame`/`Put`-style validation: inner-rect bounds, door clearance, overlap of non-flat items, `on`/`lift` stacking. The cleanest route is a pure `HomeInterior` in `Shared/Explore` that takes a saved list of (catalog index, u, v, rot step, lift/on) and validates it against `Interiors.Frame(room)`. That makes it testable in `Tools~/Checks`.
- Interiors are rebuildable at runtime: they are not static-batched, so you can destroy `interiors[id]` and call `InteriorOf` again. But `solids` is built once in `Furnish`, so a re-furnished room needs the solids rebuilt or patched (`new Solids(list, width)`).
- Customizing a house's rooms should not consume the `::rooms` RNG differently from today, or every later room's furniture changes. The simplest fix: run `FurnishRoom` as now, then replace that room's plan with the saved one.
- Rooms are only the ground floor and one room per piece. Wall-mounted items are excluded from the library, and there are no upper floors to furnish.

### Gaps
- `Solids` internals (Explore/Rect, Solids) and whether partial updates are cheap were not read in detail.

## How is a player's house assigned and stored; what does the server persist; what is the database?

### Takeaway
Postgres via Drizzle ORM, with migrations in `server/drizzle`. `MemoryPersistence` mirrors it for tests and DB-less servers. A `homes` row is (townId, x, y, playerId[account]) with a unique (town, x, y). Characters point at a home, at most 2 residents per house. Nothing about a house's look or contents is stored. Homes are deleted when the last resident leaves, and deleted and reassigned randomly when the starter town regenerates.

### Cited Findings
- Tables: `players`, `towns (name, key unique, createdBy, layout)`, `homes (id, townId, x, y, playerId, createdAt; unique town_tile)`, `characters (playerId, name, townId, homeId)`, `character_state (town, pos, rotY, appearance jsonb {look}, stats jsonb, playSeconds, version)`, `sessions`, `account_unlocks (playerId, what)`, `mission_completions`, `mission_records` — [SV/tables.ts:16-163](server/src/multiplayer/tables.ts). The header says to change the file, run `npm run db:generate`, and the server applies migrations on start — [tables.ts:1-4](server/src/multiplayer/tables.ts). Migrations 0000–0004 exist — [server/drizzle/](server/drizzle).
- "A house belongs to one account, and goes when nobody lives there" — [tables.ts:37-41](server/src/multiplayer/tables.ts). `emptyHouse` deletes the home when no character references it — [SV/persistence.ts:347](server/src/multiplayer/persistence.ts). `MAX_RESIDENTS = 2`, `MAX_TOWNS = 3`, `MAX_CHARACTERS = 8` — [persistence.ts:103-107](server/src/multiplayer/persistence.ts).
- `claimHome` locks the account row, inserts the home with onConflictDoNothing, refuses if another account owns it or it's full, then moves the character and empties the old house — [persistence.ts:565-585](server/src/multiplayer/persistence.ts). `settleStarterTown` deletes all homes in a regenerated starter town and re-houses households at random free tiles — [persistence.ts:521-563](server/src/multiplayer/persistence.ts).
- Routes: `PUT /mp/characters/:id/home {x,y}`, `GET /mp/towns/:id/homes` (x, y, mine, residents), and others — [SV/accounts.ts:5-16, 168-185](server/src/multiplayer/accounts.ts). JSON bodies are capped at 1024 bytes (`readJson(req, limit = 1024)`) — [accounts.ts:64-77](server/src/multiplayer/accounts.ts). Tiles are integers 0..1023 — [accounts.ts:80](server/src/multiplayer/accounts.ts).
- Client: `Home { townId, townName, townKey, hasHouse, x, y }`, `HomeRow { x, y, mine, residents }`, and `Account.Call` hits `/mp...` through `INetPlatform.Request` — [RT/Game/Net/Account.cs:13-19, 87-92, 130-140](unity/Assets/Porchlight/Runtime/Game/Net/Account.cs). The house picker (`HousePicker` in FrontDoor) highlights free, taken and chosen footprints on the BlueprintView. You can click a house, step with LB/RB, use "Move in" or "Pick one for me" — [RT/Game/UI/FrontDoor.cs:500-640](unity/Assets/Porchlight/Runtime/Game/UI/FrontDoor.cs).
- The app resolves your house with `Homes.At(model.buildings, home.x, home.y)` and your door with `Homes.Door(world.rooms, house.id)`. `GoHome` travels there — [RT/Game/App/PorchlightApp.cs:1170-1200](unity/Assets/Porchlight/Runtime/Game/App/PorchlightApp.cs).

### Inferences
- New persistent data per home (exterior choices, furniture list) belongs in a new table keyed by the home, or by (townId, x, y, playerId). Because homes are deleted on move-out and on starter-town regeneration, decide on purpose whether a customization survives. One option: keep it per account ("my furniture") and apply it to whatever house they live in, which survives rehousing. The other: keep it per house, which is lost when the house is.
- The 1 KB HTTP body limit would reject a furniture list of more than about 20 items in JSON. Raise the limit for a dedicated route, or use a compact encoding.

### Gaps
- The `PgPersistence.listHomes` dedupe and `MemoryPersistence.claimHome` were skimmed, not read in full.

## How does multiplayer sync work, and how would per-house state reach visitors?

### Takeaway
One Colyseus `CityRoom` per town key (`filterBy(['town'])`, up to 100 clients). Synced schema state is players (filtered by interest/StateView), parked cars, host, night and the taken-cars list. Other data travels as messages. The C# client decodes schema by reflection, so new server fields need no client codegen. Per-house state would fit best as an HTTP fetch when entering a town or a house, plus a room broadcast on change. The server holds no town geometry, so it cannot validate placements spatially.

### Cited Findings
- Room: `maxClients=100`, `patchRate=33ms`, one room per town key — [SV/CityRoom.ts:1-18, 157-163](server/src/multiplayer/CityRoom.ts); [SV/index.ts:72](server/src/multiplayer/index.ts). The WebSocket `maxPayload` is 4 KB ("plenty for any message the game sends") — [index.ts:50-52](server/src/multiplayer/index.ts).
- Schema: `Player {id,name,x,y,z,rotY,anim,car,ride,appearance,away,t}`, `ParkedCar`, `CityState {players (view), cars, host, night, taken}` — [SV/schema.ts:10-69](server/src/multiplayer/schema.ts). Messages and limits (`MSG_*`, `WORLD_MAX_BYTES=16384`) — [SV/protocol.ts:77-214](server/src/multiplayer/protocol.ts). The client mirror says to "change both together" — [RT/Shared/Net/Protocol.cs:1-57](unity/Assets/Porchlight/Runtime/Shared/Net/Protocol.cs).
- The C# decoder is "reflection-driven… Nothing here knows the server's classes". You read fields with `SchemaObj.Get(name)` — [RT/Shared/Net/Schema.cs:1-5](unity/Assets/Porchlight/Runtime/Shared/Net/Schema.cs). The client reads `night`, `host`, `taken` and `cars` from state on change — [RT/Game/Net/Multiplayer.cs:384-402](unity/Assets/Porchlight/Runtime/Game/Net/Multiplayer.cs).
- Validation patterns: `MSG_UNLOCK` accepts only names in `UNLOCKS` that aren't held yet, pushes the unlock to the account's other online characters, and persists it — [CityRoom.ts:226-234](server/src/multiplayer/CityRoom.ts). `MSG_CAR` range-checks and clamps — [CityRoom.ts:260-276](server/src/multiplayer/CityRoom.ts). `MSG_WORLD` is host-only, size-checked and rate-limited — [CityRoom.ts:277-287](server/src/multiplayer/CityRoom.ts). Joining a private town requires `mayEnter` — [CityRoom.ts:294-307](server/src/multiplayer/CityRoom.ts).
- Server movement checks only bounds and speed (`judgeMove`, `inBounds`), not walls — [SV/movement.ts:24, 67-83](server/src/multiplayer/movement.ts).
- WorldSync packs the host's traffic and people as 7-byte records about 10 Hz — [RT/Shared/Explore/WorldSync.cs:1-26](unity/Assets/Porchlight/Runtime/Shared/Explore/WorldSync.cs).
- Precedent for per-house data: `RefreshNeighbours` calls `GET /mp/towns/:id/homes` on entering a town and each time the big map opens, then paints neighbours' footprints — [PorchlightApp.cs:1124-1168](unity/Assets/Porchlight/Runtime/Game/App/PorchlightApp.cs).

### Inferences
- Recommended: a `GET /mp/towns/:id/homes` extension (or a new `GET /mp/towns/:id/homes/:x,:y`) returning each lived-in home's exterior (small: model index, variation and so on), fetched before or while the town builds, so overrides go in before static batching. Furniture would be fetched lazily on approaching or entering a house. On save, the owner sends HTTP PUT. The server then broadcasts a small `MSG_HOME {x,y,rev}` to the room so visitors re-fetch. That avoids putting every house into the 30 Hz schema and stays under the 4 KB WebSocket limit.
- Alternative: a `homes: {map: HomeLook}` in `CityState`. It reaches everyone automatically, but only while they're in the room, and private-town and offline visits still need HTTP.
- Movement isn't authoritative about walls, so furniture collision is client-side only. A cheater could walk through furniture, which is harmless.

### Gaps
- `interest.ts` details (visibility radius) were not read. They matter only if per-house state is put into the player-filtered view.

## Town editor, Blueprint, and input patterns reusable for a placement mode

### Takeaway
The "town editor" is not a spatial editor. It is the sidebar's seed and slider panel plus a name/save box, posting a town key. Blueprint is a pan/zoom 2D map with click callbacks and footprint highlights (used by the house picker). Reusable pieces: BlueprintView highlights and click/hover, the HousePicker's LB/RB stepping and PadMenu, the overlay's `Choose()` picture grid (garage/characters), and ExploreView's single place for pad bindings (`ReadPad`) with its `OnPadA` hook.

### Cited Findings
- Town editor: `BuildEditorBox` (name field, Save, Back), `SetEditor(on, save, back)` shows the seed row, Regenerate and Map settings, and hides everything else. Saving calls `POST /mp/towns {name, key}` — [git show ea17c9c, RT/Game/UI/Sidebar.cs](unity/Assets/Porchlight/Runtime/Game/UI/Sidebar.cs); [SV/accounts.ts:153-161](server/src/multiplayer/accounts.ts). Commit message: "The picker's new button opens the blueprint with the seed and map settings; saving moves the character there to choose a house" (commit ea17c9c).
- BlueprintView: `BlueprintCamera` (WorldToScreen/ScreenToWorld, Fit, Pan, ZoomAt), `highlights` list of (shape, fill), a click callback "at this map point", drag threshold — [RT/Game/UI/Blueprint.cs:1-4, 100-189](unity/Assets/Porchlight/Runtime/Game/UI/Blueprint.cs).
- Overlay grid chooser: `Choose(title, pictures, names, current, pick, kind)` with render-texture tiles (Portraits) and pad stepping — [RT/Game/UI/ExploreOverlay.cs:411-470](unity/Assets/Porchlight/Runtime/Game/UI/ExploreOverlay.cs). ExploreView: "LB and RB step through them, A picks, B closes" — [ExploreView.cs:1762-1769](unity/Assets/Porchlight/Runtime/Game/App/ExploreView.cs).
- Input: `Button` constants (A,B,X,Y,LB,RB,LT,RT,View,Menu,sticks,dpad), `PadState.Held/Pressed`, `Flick` for stick-as-step, key repeat, `Typing` gate, and a Firefox Xbox Bluetooth Y/LB workaround (hold 0.4 s = LB) — [RT/Game/App/GameInput.cs:15-243](unity/Assets/Porchlight/Runtime/Game/App/GameInput.cs).
- Pad jobs in explore: A jump (or `OnPadA` first, e.g. talk in missions), hold A or RT turbo/sprint, B car, X centre, Y night, View new character, Menu map, RB nearest player, LB characters, d-pad left/right rotate camera, up/down zoom. Modal states (map, chooser, dialogue) take over the pad first — [ExploreView.cs:1720-1800, 1846-1850](unity/Assets/Porchlight/Runtime/Game/App/ExploreView.cs); [RT/Game/App/MissionPlay.cs:309, 346](unity/Assets/Porchlight/Runtime/Game/App/MissionPlay.cs). Keyboard in the app: J journal, N night, R walk mode, T auto walk, V close-up, C new look, M map, Esc, backquote debug — [PorchlightApp.cs:1798-1824](unity/Assets/Porchlight/Runtime/Game/App/PorchlightApp.cs). The keys the game listens to are whitelisted in `GameInput.Keys` — [GameInput.cs:80-85](unity/Assets/Porchlight/Runtime/Game/App/GameInput.cs).

### Inferences
- A placement mode should be another modal branch in `ReadPad`, like `overlay.MapOpen`/`overlay.Choosing`: left stick moves a ghost item on the room's u/v grid, LB/RB cycle the item, the d-pad or a `Flick` rotates in 90° steps, A places, Y removes, B exits, Menu saves. Keyboard needs new keys added to `GameInput.Keys`. Mouse placement can raycast to the floor (Coords.Map inverse). The room camera already exists (`RoomCameraYaw`).
- An exterior customization panel fits the FrontDoor/PadMenu card style or the overlay `Choose` grid (pictures of the house model/variations via `Portraits`).

### Gaps
- Portraits.cs (how chooser pictures are rendered) and PadMenu were not read in detail.

## Unlocks/missions: could furniture be rewards?

### Takeaway
Yes, with the existing pattern. Unlocks are a fixed whitelist of strings shared by C# `Catalog.UNLOCKS` and TS `UNLOCKS`. Missions (ink, compiled on the server, no client build needed) call `unlock("x")` and declare `# unlocks: x`. The server stores them per account in `account_unlocks` and pushes them to the account's other characters.

### Cited Findings
- `Catalog.UNLOCKS = { "animals", "firefighter", "sprint" }`, and `GarageCars(unlocks)` gates the fire engine — [Catalog.cs:145-155](unity/Assets/Porchlight/Runtime/Shared/Explore/Catalog.cs). Server `UNLOCKS`, `UNLOCK_NAMES` (Journal text), `lookAllowed` — [SV/protocol.ts:154-169](server/src/multiplayer/protocol.ts). `account_unlocks` table — [tables.ts:112-123](server/src/multiplayer/tables.ts). Journal data `GET /mp/progress` — [accounts.ts:108-122](server/src/multiplayer/accounts.ts).
- Missions: `# unlocks:` tag, `EXTERNAL unlock(what)`; a new building block needs a game build (IMissionWorld, MissionScript, MissionPlay, both stand-in towns, README) — [server/missions/README.md](server/missions/README.md); [server/missions/chase.ink:3, 21, 110](server/missions/chase.ink); [server/src/missions.ts:13, 32-33, 65](server/src/missions.ts).

### Inferences
- Furniture rewards: add unlock ids like `furniture-<set>` to both lists (and `UNLOCK_NAMES`), and give `Catalog` a `FurnitureFor(unlocks)` like `GarageCars`. The server should validate saved furniture against the account's unlocks, as it does with `lookAllowed`. No new mission blocks are needed, since `unlock()` already exists.

### Gaps
- None significant.

## Existing colour palette system ("your house in its own colour")

### Takeaway
"Your house in its own colour" means the 2D minimap and big map only. Your house's footprint is filled 0xffc83d, and neighbours' lived-in houses 0x7fb2e5. The 3D `Palette` is a rendering utility (a colour-to-texel atlas for procedural meshes). It is not a per-player colour system.

### Cited Findings
- `HomeHouse` fills `0xffc83d`, `NeighbourHouses` fills `0x7fb2e5` — [RT/Game/UI/ExploreOverlay.cs:87-134](unity/Assets/Porchlight/Runtime/Game/UI/ExploreOverlay.cs); commits 6cc761b and abba384.
- `Palette`: a 256×256 albedo + emission texture, `UV(hex, glow)` allocates a texel, and one material — [Palette.cs:1-61](unity/Assets/Porchlight/Runtime/Game/Rendering/Palette.cs).
- Building colour in 3D = kit colour-map variation (see Q1) — [Materials.cs:164-184](unity/Assets/Porchlight/Runtime/Game/Rendering/Materials.cs).

### Inferences
- A custom house colour in 3D can be (a) the kit variation index (cheap, already supported by `Props.Place(variation:)`), or (b) a tint via `BaseColor` on a cloned material. (b) multiplies the whole colour map, roof included, unless roof and walls are separate materials.

### Gaps
- Whether the Kenney suburban colour maps put walls and roof in separate texture regions, which would allow region-specific tinting with a custom map, was not verified.

## Tests/checks, CI, build targets: constraints to respect

### Takeaway
Shared code must compile on plain .NET 8 with **C# 9** (Checks.csproj `LangVersion 9.0`) and stay engine-free. CI runs `towns 60` (generation invariants and repeatability), server `typecheck` and `test`, then WebGL, Windows and macOS builds. The `homes` check guards that `porchlight.json` matches the generator.

### Cited Findings
- Checks.csproj compiles `Runtime/Shared/**` + the Ink runtime with `net8.0`, `LangVersion 9.0`, `Nullable disable` — [unity/Tools~/Checks/Checks.csproj](unity/Tools~/Checks/Checks.csproj). Subcommands: net, mission, chase, homes, course, measure, town, traffic, towns — [unity/Tools~/Checks/Program.cs:24-32](unity/Tools~/Checks/Program.cs). `HomesCheck` checks that the starter file equals the generator output and that every house is found by its anchor tile — [Program.cs:176-197](unity/Tools~/Checks/Program.cs).
- Deploy CI: `dotnet run ... -- towns 60`, WebGL build (game-ci), server `npm ci && npm run typecheck && npm test`, then Terraform/deploy — [.github/workflows/deploy.yml:53-54, 65-78, 99-105](.github/workflows/deploy.yml). Desktop CI builds Windows and macOS and packs self-updating releases — [.github/workflows/desktop.yml](.github/workflows/desktop.yml). Missions CI deploys ink files only — [.github/workflows/missions.yml](.github/workflows/missions.yml).
- Server tests: persistence, room, missions, movement, interest, auth, records — [server/test/](server/test). `test:db` runs against real Postgres — [server/package.json](server/package.json). `Tools~/check.sh` compile-checks the Unity assemblies with dotnet — [unity/Tools~/check.sh](unity/Tools~/check.sh). WebGL: no threads, and a jslib platform layer for WebSockets, storage and downloads — [unity/README.md](unity/README.md).

### Inferences
- New pure logic (home layout validation, encoding) belongs in `Runtime/Shared/Explore`, in C# 9 with no Unity types, and gets a new `Checks` subcommand (e.g. `homes-decor`) wired into CI. Server changes need a Drizzle migration plus `MemoryPersistence` parity and tests in `persistence.test.ts`/`room.test.ts`. Changing generation (Buildings/Layout) would trigger the `homes` check and force `npm run town`, which rehouses everyone. Customization must not alter generation.

### Gaps
- Whether deploy CI also runs `homes`/`net` checks was not confirmed. Only `towns 60` was seen in the excerpt.

## Code style conventions a new feature should follow

### Takeaway
Plain-English, British-spelt prose everywhere: file header comments describing what the file is, often ending "Pure." for engine-free code; doc comments as full sentences; commit subjects as user-facing sentences with no type prefix; assets generated in code or from Kenney/RG Poly glTF, with no custom shaders.

### Cited Findings
- Commit subjects such as "Town editor: design a town from the town picker and save it by name" and "Everyone's houses on the maps, in their own colour, asked afresh when the map comes up", with bodies in prose and `Co-Authored-By: Claude Opus 5.5` — `git log` (commits ea17c9c, abba384, 2652a37).
- Headers: "Where people live: … Pure." — [Homes.cs:1-3](unity/Assets/Porchlight/Runtime/Shared/Explore/Homes.cs). Index-over-the-wire rule: "The index goes over the wire (the server takes up to 254), so new models go on the end" — [Catalog.cs:56-59](unity/Assets/Porchlight/Runtime/Shared/Explore/Catalog.cs). Route lists documented at the top of accounts.ts and index.ts — [accounts.ts:1-21](server/src/multiplayer/accounts.ts).
- Server TS runs with Node type-stripping (no decorators, so `schema()` form) — [SV/schema.ts:5-6](server/src/multiplayer/schema.ts).

### Inferences
- Furniture ids on the wire/DB should be stable names (e.g. "furniture/sofa"), or indices that only ever get appended to. Names are safer for persistence.

### Gaps
- No linter/formatter config was examined.

## Concrete recommendation: files to add/modify, and phased plan

### Takeaway
Do it in four phases. (1) Exterior: model + colour variation per home, overriding render picks without disturbing RNG streams. (2) Interior: a pure validated furniture layout replacing the procedural plan for your rooms, plus an in-room placement mode. (3) Persistence: a new table and routes, with unlock gating. (4) Visiting: fetch on enter, broadcast on change, apply before static batching.

### Cited Findings (anchor points)
- Exterior override point: `AddHouses` model and variation pick — [WorldParts.cs:73-82](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs). Must happen before `StaticBatchingUtility.Combine` — [World.cs:178-179](unity/Assets/Porchlight/Runtime/Game/Rendering/World.cs).
- Interior override point: `Furnish` loop / `plans` — [WorldParts.cs:316-322](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs). Interior rebuild: `InteriorOf`, which caches — [WorldParts.cs:380-428](unity/Assets/Porchlight/Runtime/Game/Rendering/WorldParts.cs).
- Home identity: `Homes.Anchor/At` — [Homes.cs:25-30](unity/Assets/Porchlight/Runtime/Shared/Explore/Homes.cs). HTTP precedent: `RefreshNeighbours` — [PorchlightApp.cs:1124-1168](unity/Assets/Porchlight/Runtime/Game/App/PorchlightApp.cs).

### Inferences (proposed plan; not yet implemented)
**(a) Exterior customization**
- Add `RT/Shared/Explore/HomeLook.cs` (pure): `HomeLook { model (index into Catalog.HOUSES, -1 = as generated), variation, shed/garden/fence toggles }`, with validation for a given House (allowed model list = `KindModels(House)`, variation < `VariationCount`). Keep the encode/decode compact.
- Modify `WorldParts.AddHouses`: take a `Func<House, HomeLook>` (or a dictionary keyed by anchor tile) on `TownWorld`. Still consume `rng` for split, variation and PickModel as now, then override. Forcing `split=1` for a chosen model may be needed for a coherent single-model house.
- Optional yard tweaks: `Layout.PlanBlocks` uses deterministic hashes per house — [Layout.cs:261-297](unity/Assets/Porchlight/Runtime/Shared/Explore/Layout.cs). Overrides there are safe for other houses, but `Homes.All` calls `Layout.Build` and must stay unchanged.
- UI: a "Your house" card (FrontDoor/PadMenu style) or the overlay `Choose` grid with Portraits of each model/variation. Live preview needs a rebuild of that house's shell outside `still`. Simplest: build the owner's own home shell under `props`/`root` rather than `still`, so it can be swapped without re-batching.
**(b) Interior furniture placement**
- Add `RT/Shared/Explore/HomeInterior.cs` (pure): `SavedItem { name, u, v, rot (0-3 quarter turns), on (index of item below or -1) }`, plus `Validate(room, items, size)` and `ToPlan(room, items, size) -> RoomPlan`, reusing the `Interiors` frame and overlap rules. This may need `FurnishRoom`'s private `Put` logic factored into a shared helper in `Interiors.cs`.
- Modify `WorldParts.Furnish`: after `FurnishRoom`, if the room belongs to a customized home, replace `plan`. Add a `RebuildInterior(roomId)` that destroys `interiors[id]`, rebuilds it and updates `solids`.
- Add a placement mode in `ExploreView.ReadPad`/`OnKeyDown`: a modal branch, a ghost `Props.Place` instance, snapping to the room grid, A place, Y remove, LB/RB item, d-pad rotate, B leave. Prompt text goes through `overlay.SetPrompt`. Available only when `room` belongs to `HouseOfHome()`.
- Catalog: add `FURNITURE_SETS`/unlock gating (`FurnitureFor(unlocks)`). Any new models must be appended to `Catalog.FURNITURE` (and so to `Statics`) for the library to build them.
**(c) Persistence and sync**
- `server/src/multiplayer/tables.ts`: a new `home_looks` table (or jsonb columns on `homes`): `{ homeId or (townId,x,y), look jsonb, furniture jsonb, rev int, updatedAt }`. Decide whether it survives move-out: `emptyHouse` deletes homes, and `settleStarterTown` wipes them. A per-account "decor" kept with the player and applied to their current home survives rehousing. Run `npm run db:generate` (new `0005_*.sql`).
- `persistence.ts`: add `homeLook`/`saveHomeLook` to the `Persistence` interface and both implementations. Validate size, known names and unlock gating (as with `cleanLook`/`lookAllowed`).
- `accounts.ts`: `GET /mp/towns/:id/homes` includes the look. Add a new `PUT /mp/characters/:id/home/look` and `.../furniture` with a larger body limit than 1024. The owner check is `homes.playerId === account.id`.
- `protocol.ts` + `Protocol.cs`: `MSG_HOME = 'home'` (server→clients `{x, y, rev}`), broadcast by the room when a save lands (the HTTP route needs a hook into the town's room, via a `GameDeps` callback like `playingIn`).
- `RT/Game/Net/Account.cs`: DTOs and calls. `Multiplayer.cs`: handle `MSG_HOME`, re-fetch, then `RebuildInterior`/swap shell.
- Server-side spatial validation isn't possible (no generator in TS). Validate structure only, and let clients drop items that fail `HomeInterior.Validate` for that room. Because rooms come from prefab `Ground` data, a stored layout is tied to the chosen model, so store the model alongside the furniture and invalidate furniture when the model changes.
**(d) Visiting**
- On entering a town: fetch looks with homes (extend `RefreshNeighbours` timing to before `TownWorld.Build`, or rebuild affected shells after). Furniture: fetch lazily when within `INTERIOR_REACH`/`ROOM_NEAR`. Private towns already restrict entry (`mayEnter`). Consider an optional "front door open/closed to visitors" flag; doors are currently open to all.
- Checks: add a `Tools~/Checks` subcommand that round-trips HomeInterior encode/validate across generated towns' rooms (rooms need prefab Ground data, which lives in Unity. The check would need synthetic rooms, or a dump of Ground data, which is a constraint).

**Suggested phases:** P1 exterior variation + model (client-only dev-mode toggle, then persisted). P2 persistence table, routes and MSG_HOME broadcast for looks. P3 interior placement mode + HomeInterior + furniture persistence. P4 unlock-gated furniture sets via missions, visitor polish (open-house flag, Journal entries).

**Risks:** RNG stream drift if overrides change `::iso`/`::rooms` consumption; static batching blocking runtime shell swaps; prefab-dependent rooms invalidating saved layouts on model change; starter-town regeneration and move-out deleting `homes` rows; the 1 KB HTTP and 4 KB WebSocket payload limits; C# 9 / engine-free constraint for Shared; multi-piece houses (`split` 1–4) meaning a home has several rooms; performance (every interior is built at load; per-home custom furniture adds instantiations, so keep them lazy).

### Gaps
- Room `Ground` data exists only in the Unity-generated library (gitignored), so pure .NET checks cannot validate layouts against real rooms without new tooling.
- No existing undo/edit history pattern in the codebase to reuse for placement.

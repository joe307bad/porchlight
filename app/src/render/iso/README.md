# 3D explore view

Click **Explore** on the blueprint page to walk the generated town in 3D
(Three.js, loaded on demand, so the blueprint page stays light). The camera is
orthographic and looks down at a 30° pitch, the same 2:1 isometric look as
before, but the town is real geometry, so the camera can turn.

- **Controls:** WASD or arrow keys walk (relative to the screen, whichever way
  the camera faces). `Q` / `E` (or the ⟲ ⟳ buttons) turn the camera one stop
  round the player. `F` (**Look ahead**) turns it to look down the street
  you're walking. `T` toggles auto walk: your character heads to the nearest
  sidewalk, then keeps walking to far-off parts of town by sidewalk and street
  crossings (Dijkstra over the sidewalk network). The route is drawn as a
  dashed orange line on the ground and on the minimap, with a flag at the
  destination. While auto-walking, a direction key pointing back turns you
  round and one pointing at the road crosses it. Scroll zooms, `N` toggles day
  and night, `C` swaps to a new random character, `Esc` returns to the
  blueprint. The minimap (bottom left) shows where you are and what the camera
  sees; click it to jump there.
- **Street names (`names.ts`, `signs.ts`):** on entering explore mode every
  road gets a street name, seeded from the town. Road edges join into one
  street where they carry straight on through a junction (same kind of road,
  within about 40° of straight). The main road nearest the centre is the
  **High Street**; other main roads are Roads and Avenues; side streets are
  named after trees, flowers and birds, a short dead end being a Close, Court
  or Mews and a winding one a Crescent or Drive; footpaths are Walks. One
  street by the church is Church …, one by the plaza Market …, one over a
  bridge Bridge …. Small green signposts stand on a corner of each junction
  where differently named streets meet, one blade per street, each running
  along its street and lettered on both sides (all names drawn once into one
  texture atlas, `signAtlas` in `textures.ts`; all blades are one mesh). The
  panel top right says which street you're on.
- **Directions (`directions.ts`):** while auto-walking, the panel lists
  turn-by-turn directions to the flag, read off the planned route: "Head
  north on Elm Street", "Turn left onto High Street", "Bear right onto …",
  "Cross Mill Lane", "Turn round at the end of …", "Arrive at your
  destination on …", each with the distance to walk after it (about 4 m a
  tile). The step you're on is highlighted; steps drop off as you pass them,
  and a new list starts with each new destination.
- **Camera stops (`orbit.ts`):** 12 fixed headings 30° apart, at 15°, 45°,
  75° and so on. 45°, 135°, 225° and 315° are the four classic isometric
  views; the ones in between look down avenues from 15° off-axis. Turns ease
  the short way round. The camera always follows the player.
- **Tilt:** `Z` / `X` (or **Tilt ↓** / **Tilt ↑**, or Page Down / Page Up)
  move the camera through fixed tilt stops, eased like the turns: the
  overview from 10° (nearly level, looking along the streets) to 80° (nearly
  top-down), default 30°; the close-up from 2° (eye level) to 70° (high over
  the shoulder), default 17°. Each mode remembers its own tilt
  (`ISO_TILTS`, `CHASE_TILTS` in `orbit.ts`).
- **Auto-walk camera:** while auto-walking, the camera turns with the player
  to stay behind them, looking down the street ahead. It follows their
  heading smoothed over about a second, and only moves to the next stop once
  the heading is clearly past halfway to it (`stopBehind`), so curves and quick
  road crossings don't swing it about. `Q` / `E` / `F` still work; the camera
  goes back to following five seconds later.
- **Close-up (`V`, or the **Close up** button):** a perspective
  third-person camera just behind and above the player, turning with them
  (`chaseYaw`). `W` / `S` walk forward and back, `A` / `D` turn, `Q` / `E`
  swing the camera 30° round them (`F` puts it back behind), scroll moves it
  nearer or further. If a building is between the player and the camera, the
  camera comes in closer rather than sit inside it. Fog fades the town into
  the sky. Auto walk works here too, with the camera following from behind.
- **Ground floors (`interiors.ts`):** every building piece has a room behind
  its street front, with walls and a door in the middle of the front wall.
  Rooms are furnished at random (seeded) for the kind of building:
  - **Homes:** a kitchen run along the back wall, a sofa, a bookcase and TV
    cabinet, a table and chairs on a rug, plants, a lamp and a coat rack.
  - **Shops:** a counter across the room with a shopkeeper behind it, a radio
    or till on top, and shelves and boxes along the walls.
  - **Offices:** a reception counter, then rows of desks with screens and
    chairs.
  - **Workshops:** workbenches, cabinets and boxes.
  - **Halls and the church:** a table at the far end and rows of benches
    facing it.

  Nothing blocks the door. Walls and furniture are solid (`collide.ts`), so
  the door is the only way in.
- **Modelled ground floors:** every building's ground floor is modelled by
  the town itself, not by the Kenney model: facade walls in the building's
  colour (one colour per building, so a row reads as a terrace), the
  doorway the door hangs in, framed windows on every wall (wide shop windows
  on shops and offices), a plinth, a cornice band and a slab on top. The
  Kenney model stands on it as the upper floors and roof: a clipping plane
  on the city-kit materials (`ABOVE_GROUND_FLOOR` in `assets.ts`) cuts away
  everything of it below the ground floor's height (0.8 tiles), including its
  own painted door. So the door you walk through is the building's only door,
  and the room behind it matches the outside.
- **Doors:** walk up to one and it swings open, showing the furnished room
  behind it, then shuts once you've gone (no key needed).
- **Inside:** you're in once you're a little way past the doorway, and stay
  in until you're clearly back out, so the threshold doesn't flick between
  the two. Inside, that building's upper floors and facade hide, and the
  camera eases to a fixed view of the room: from the street side, pitched
  down at 55°, zoomed to fit the whole room. In close-up mode it's the
  perspective camera easing to a fixed spot instead, and walking back out
  eases it round behind you again. Walking out, the overview camera stays on the street side you came out of (the nearest stop to the room view) instead of swinging back to where it was, and in close-up the keys stay screen-relative until you let go, so the key that walked you out keeps walking you out. `Q` / `E` turn the room view a quarter
  round the room, and `F` brings it back to the street side. Whichever walls
  face the camera are cut down low, so you see in over them. A warm light
  comes on, brighter at night.
- **Keys keep their direction:** the movement keys are read against the
  camera's heading from when you started holding them, and only catch up
  once you let go. So when the camera swings round as you go through a door
  (or on a Q / E turn), the way you're walking doesn't bend under you.
- **Lit windows (`windows.ts`):** decided window by window, so a window is
  either all lit or all dark. On load, each Kenney building's window panes
  are found: triangles whose colourmap texel is window blue, grouped into
  connected panes. Each pane gets its own `windowSeed` vertex attribute, and
  the geometry is un-indexed first so no wall triangle shares a corner with
  glass. Glass is the kits' pale sky-blue swatches only (`isGlass`), not
  their lavender-white wall paint, which is faintly blue too. Panes spanning
  more than a fifth of a building (tower curtain walls) light cell by cell,
  on the tower's own grid: the floor bars standing in front of the glass are
  found on load (`curtainGrid`) and give each wall's first floor line and
  storey spacing, in model units. Each floor is split into equal bays from
  the pane's edges (or between mullions, where a model has them), so lit
  cells line up with the floors instead of straddling them. The town's own ground-floor panes get seeds as they're built. At
  night a shader patch lights a random share of panes warm yellow: 15% low
  down rising to 36% at tower heights, varied per building. Low windows glow
  at half brightness and about one lit window in eight flickers. Tall
  buildings (curtain walls, or anything above 2.2 tiles) glow at a quarter
  brightness and steady, and about 3% of their windows glow a steady cool
  blue. None of them flicker.
- **Zoomed out:** past overview zoom 0.7, auto walk no longer turns the
  camera.
- **Seeing through buildings:** when a building stands between the player
  and the camera, their silhouette is drawn through it in orange.
- **X-ray:** the player carries a copy of their skinned meshes in flat orange
  that is drawn with `depthFunc: GreaterDepth`, after the town but before the
  player themself (render order), so it only shows where something already
  drawn is in front of them, and never where they overlap themself.
- **Flat:** the generator keeps `tiles.elevation` at 0, so the town is flat;
  rivers are sunk below the land with earth banks, and the map stands on an
  earth slab.
- **Who walks where:** the player walks anywhere except through buildings and
  sheds by default; **Walk** (or `R`) switches to streets only (`streets.ts`).
- **Layout (`layout.ts`):** the blueprint becomes a street scene, block by
  block and lot by lot (unchanged from the 2D view: pure and unit-tested).
- **Buildings (`world.ts`):** every blueprint building fills its exact
  footprint, front to the street. A long street front is split into a row of
  narrower pieces whose proportions suit the models; each piece gets one of the
  few best-fitting Kenney models, stretched to fill its share, with its height
  nudged toward the building's floor count. One colour variation per building,
  so a row reads as a terrace. Houses use City Kit Suburban; shops, workshops,
  halls and the church use City Kit Commercial; tall central offices use its
  skyscrapers.
- **Drawing:** every copy of a model is one instance of an InstancedMesh per
  model part. Ground, roads, bridges and sheds are built procedurally
  (`mesh.ts`) as vertex-coloured meshes. Road layers sit a few millimetres
  apart (outer kerb, sidewalk, inner kerb, asphalt, centre dashes) so they
  stack in order without z-fighting.
- **Traffic (`traffic.ts`, `fleet.ts`):** cars drive every through road in the
  right-hand lane, turn at junctions and queue; dead-end side streets have
  parked cars.
- **People (`actors.ts`, `characters.ts`):** Kenney mini characters with their
  idle and walk clips blended by speed, the walk sped up to match the pace.
- **Day and night:** the sky, the hemisphere light and the sun ease to
  moonlight. At night the lamps' bulbs light, each with a halo (screen-space
  points) and a pool of light on the pavement; a few faulty ones flicker hard.
- **Animation:** water shimmers in a small shader, pale streaks run down the
  rivers, and the plaza fountain sprays.

## Files

| File | Role |
| --- | --- |
| `orbit.ts` | Camera stops, turning, screen to ground directions, sight-line box test (pure, unit-tested) |
| `assets.ts` | Kenney GLB loading, flattening into per-material geometry, colour variations, model catalog |
| `layout.ts` | Blueprint to street scene: smoothed roads, bridges, houses on street fronts, yards, sidewalk network (pure, unit-tested) |
| `mesh.ts` | Builder for flat-coloured quads, ribbons, walls and boxes |
| `world.ts` | Builds the 3D town from a `TownModel` |
| `characters.ts` | Rigged characters, animation blending, the x-ray copy |
| `actors.ts` | Player (manual, streets-only or auto walk) and pedestrians: pure state |
| `walking.ts` | Following, turning round on and crossing between sidewalks (pure, unit-tested) |
| `traffic.ts` | Moving cars on the through roads: pure state |
| `fleet.ts` | Draws the moving cars |
| `interiors.ts` | Ground-floor rooms: walls with a door gap, furnishing by building kind (pure, unit-tested) |
| `collide.ts` | Solid walls and furniture the player can't walk through (pure, unit-tested) |
| `names.ts` | Joins roads into streets and names them (pure, unit-tested) |
| `signs.ts` | Where street signs stand (pure, unit-tested) and their meshes |
| `directions.ts` | Auto walk's route as turn-by-turn steps on named streets (pure, unit-tested) |
| `hud.ts` | The street panel: the street you're on, and the directions |
| `windows.ts` | Night-time lit and flickering windows (a shader patch on the building materials) |
| `streets.ts` | Tiles allowed in streets-only walking |
| `minimap.ts` | Corner map with player marker and camera outline |
| `textures.ts` | The glow texture for lamp halos and light pools, and the street sign atlas |
| `view.ts` | Renderer, lights, camera, input, sight-keeping, day/night and animation loop |

## Art

Models are Kenney's CC0 3D kits (www.kenney.nl), served from `public/models`,
each kit in its own folder with its licence and `Textures/colormap.png`:
**City Kit Suburban** (houses, fences, planters, plus three colour
variations), **City Kit Commercial** (shops, offices, skyscrapers, plus two
colour variations), **Car Kit**, **Mini Characters** (rigged, with animation
clips), **Furniture Kit** (room furniture and counters, facing +Z) and **Nature Kit** (trees, bushes, flowers, crops, rocks).

Orientation: Kenney's cars and characters face +Z, but the city-kit buildings
have their fronts on -Z, so the world turns them half round (`BUILDING_TURN`;
`tests/models.test.ts` checks this from which side each model's detail is on).
The nature kit's materials are marked fully metallic, which renders near-black
without an environment map, so every kit material is made matte on load.

## World space

Map `(x, y)` (tiles, y down the map, as on the blueprint) is Three's `(x, z)`;
Three's `y` is up; 1 unit = 1 tile. A townsperson is about 0.47 tiles tall; furniture is drawn at 0.6 tiles per kit unit.

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
- **Camera stops (`orbit.ts`):** 12 fixed headings 30° apart, at 15°, 45°,
  75° and so on. 45°, 135°, 225° and 315° are the four classic isometric
  views; the ones in between look down avenues from 15° off-axis. Turns ease
  the short way round. The camera always follows the player.
- **Auto-walk camera:** while auto-walking, the camera turns with the player
  to stay behind them, looking down the street ahead. It follows their
  heading smoothed over about a second, and only moves to the next stop once
  the heading is clearly past halfway to it (`stopBehind`), so curves and quick
  road crossings don't swing it about. `Q` / `E` / `F` still work; the camera
  goes back to following five seconds later.
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
| `streets.ts` | Tiles allowed in streets-only walking |
| `minimap.ts` | Corner map with player marker and camera outline |
| `textures.ts` | The glow texture for lamp halos and light pools |
| `view.ts` | Renderer, lights, camera, input, sight-keeping, day/night and animation loop |

## Art

Models are Kenney's CC0 3D kits (www.kenney.nl), served from `public/models`,
each kit in its own folder with its licence and `Textures/colormap.png`:
**City Kit Suburban** (houses, fences, planters, plus three colour
variations), **City Kit Commercial** (shops, offices, skyscrapers, plus two
colour variations), **Car Kit**, **Mini Characters** (rigged, with animation
clips) and **Nature Kit** (trees, bushes, flowers, crops, rocks).

Orientation: Kenney's cars and characters face +Z, but the city-kit buildings
have their fronts on -Z, so the world turns them half round (`BUILDING_TURN`;
`tests/models.test.ts` checks this from which side each model's detail is on).
The nature kit's materials are marked fully metallic, which renders near-black
without an environment map, so every kit material is made matte on load.

## World space

Map `(x, y)` (tiles, y down the map, as on the blueprint) is Three's `(x, z)`;
Three's `y` is up; 1 unit = 1 tile. A townsperson is about 0.6 tiles tall.

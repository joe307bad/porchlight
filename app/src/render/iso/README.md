# Isometric explore view

Click **Explore** on the blueprint page to walk the generated town in a PixiJS v8
isometric view (loaded on demand, so the blueprint page stays light).

- **Controls:** WASD or arrow keys walk (screen-relative), `T` toggles auto
  walk (your character heads to the nearest sidewalk, then keeps walking to
  far-off parts of town by sidewalk and street crossings, planned with
  Dijkstra over the sidewalk network; the route is drawn as a dashed orange
  line on the ground and on the minimap, with a flag at the destination; a
  direction key pointing back turns you round, one pointing at the road
  crosses it), scroll zooms, `N`
  toggles day and night, `C` swaps to a new random character, `Esc` returns to
  the blueprint. The minimap (bottom left) shows where you are and what the
  camera sees; click it to jump there.
- **Flat:** the generator keeps `tiles.elevation` at 0, so the town is drawn
  flat (cliff stacking is still supported if elevation comes back).
- **Who walks where:** the player walks anywhere on the map except through
  buildings and sheds by default; the
  **Walk** button (or `R`) switches to streets only, keeping them on roads,
  sidewalks, bridges, paths and the plaza (`streets.ts`).
- **Layout (`layout.ts`):** the blueprint becomes a street scene, block by
  block and lot by lot. Every blueprint building is built at its exact
  footprint and position, facing the street its lot fronts; only the street-side
  row gets entrances. The rest of each lot is that building's fenced back yard,
  often with a shed and a vegetable garden. Empty lots among houses become
  community gardens; other leftover ground stays green with trees. Nothing
  crosses its lot, so blocks fit together cleanly. Roads are each edge's
  centreline smoothed with Chaikin and drawn as vectors (asphalt, kerbs,
  sidewalks, dashed centre lines; bridges as stone decks the road carries
  across), with cars parked along the kerbs.
- **Back to the map:** the town keeps running behind the blueprint (nothing is
  drawn, but auto-walk, townsfolk and traffic carry on), and the blueprint
  shows your character's pin moving along the streets with the route ahead,
  scrolling to keep them in view.
- **Traffic:** cars (drawn in `textures.ts` from 3D boxes, so they face any
  road exactly) drive every through road in the right-hand lane, turn at
  junctions and queue behind each other (`traffic.ts`); dead-end side streets
  have parked cars.
- **See-through outline:** when a building in front hides your character, a
  fluorescent orange silhouette is drawn on top (white silhouettes of each walk
  frame, tinted), so you never lose track of yourself.
- **Animation:** water tiles shimmer, pale streaks run down the rivers, the
  plaza fountain sprays, and at night lamps (a few faulty ones flicker hard) and
  windows glow.

## Files

| File | Role |
| --- | --- |
| `coords.ts` | Projection: tile `(x, y, z)` to screen, depth key |
| `assets.ts` | Kenney sprite loading and the building style catalog |
| `layout.ts` | Blueprint to street scene: smoothed roads, bridges, houses on street fronts, yards, sidewalk network (pure, unit-tested) |
| `textures.ts` | Procedural plaza block in Kenney's style, plus glow/streak/droplet sprites |
| `characters.ts` | Random looks and their baked walk cycles |
| `streets.ts` | Tiles allowed in streets-only walking |
| `minimap.ts` | Corner map with player marker and camera outline |
| `world.ts` | Builds the scene from a `TownModel` |
| `actors.ts` | Player (manual, streets-only or auto walk) and pedestrians |
| `walking.ts` | Following, turning round on and crossing between sidewalks (pure, unit-tested) |
| `traffic.ts` | Moving cars on the main roads |
| `view.ts` | Pixi app, camera, input, day/night and animation loop |

## Art

Sprites are Kenney's CC0 packs (www.kenney.nl), served from `public/kenney`:
**Isometric Buildings** (chosen as the free pack with the most building types:
~50 ground floors, 24 upper floors and 50+ roofs that stack into houses, shops
and offices), **Isometric Landscape** (grass, sand, dirt, water blocks) and
**Isometric City** (fountain, lamp posts, trees, benches, paving),
**Isometric Miniature Farm** (fences, tilled soil, corn, hay, crates),
and **Isometric Roads** (conifers and slim trees). Cars are drawn procedurally
in the same flat style. Roads are vectors in the same palette, so they follow the blueprint's
road lines and curve instead of stepping tile by tile.

Building pieces stack: a ground floor anchored by its bottom, then upper floors
every 33px, then a roof, all drawn at 4/3 size so each box fills its tile and
multi-tile footprints join into one building. The ground floor is cut down to
its box (`groundFloor`); paving is drawn in the ground layer instead, so
buildings sit flush. Each ground floor is tagged with the side its entrance is
on (`ENTRANCE`, read off the sprites; mirroring swaps S/E and N/W), so the
entrance always faces the street; homes whose street is behind them show their
back. Pitched roofs are tagged by ridge direction (`ROOF_RIDGE`) and mirrored so
ridges run along the street. Farm fences are mapped to the tile edge they stand
on (`FENCE_FILE`); parked cars pick the frame whose axis matches the road
(`CAR_FRAMES`).

## Projection

Kenney blocks have a 130x64 top diamond and are 32px thick, so:

```
sx = (x - y) * 65
sy = (x + y) * 32 - elevation * 32
```

Ground draws in diagonal 16x16 chunks (culled per chunk); buildings, trees,
lamps and people share one container sorted by `x + y`.

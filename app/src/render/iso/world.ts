// Builds the Pixi scene for a TownModel from its iso layout (layout.ts):
// ground blocks in diagonal chunks (culled per chunk), smooth vector roads with
// sidewalks, houses stacked from Kenney floor pieces along the street fronts,
// yards and trees, street lamps, the plaza fountain, and the animated bits:
// water tiles, river streaks, lamp and window glows.

import { Container, Graphics, Matrix, Rectangle, Sprite, type Texture } from 'pixi.js';
import { DIRS4, index, inBounds } from '../../core/grid';
import { dist, polylineLength, samplePolyline } from '../../core/geometry';
import { createRng, type Rng } from '../../core/rng';
import { Overlay, Terrain, type Facing, type TownModel, type Vec2 } from '../../model/types';
import {
  ARCHED_ROOFS,
  ENTRANCE,
  FAMILIES,
  FARM_SCALE,
  FARM_TOP,
  FLAT_ROOFS,
  GABLE_ROOFS,
  GREY_ROOFS,
  KIND_FAMILIES,
  PLAIN_GROUND,
  ROOF_RIDGE,
  type IsoAssets,
} from './assets';
import { HALF_H, HALF_W, STEP, depthOf, toScreen } from './coords';
import { SIDEWALK, Use, buildLayout, type House, type IsoLayout, type SidewalkNetwork } from './layout';
import { CAR_COLOURS, CAR_TEX, carTexture, dropTexture, glowTexture, plazaTexture, shedTexture, streakTexture } from './textures';

const CHUNK = 16;
/** Water sits this far below the land around it. */
const WATER_DROP = 16;
/** Ground floor box top, relative to the tile's centre on screen. */
const GROUND_TOP = -60;
const FLOOR_H = 33;
/** Roof sprites sit with their bottom this far below the top floor's top. */
const ROOF_BOTTOM = 52;
/** Distance between street lamps along a sidewalk (tiles). */
const LAMP_SPACING = 7;

const COLORS = {
  asphalt: 0x696969,
  sidewalk: 0xd8d3bf,
  curb: 0xb3ad98,
  lane: 0xece7d2,
  path: 0xc9a46e,
  parapet: 0xa39d8a,
  parapetShade: 0x6f6a5c,
};

export interface Lamp {
  glow: Sprite;
  pool: Sprite;
  phase: number;
  /** Some lamps are faulty and flicker hard. */
  faulty: boolean;
}

export interface WaterTile {
  sprite: Sprite;
  x: number;
  y: number;
  deep: boolean;
}

export interface Streak {
  sprite: Sprite;
  path: Vec2[];
  /** Cumulative length at each path point. */
  at: number[];
  s: number;
  speed: number;
}

export interface Droplet {
  sprite: Sprite;
  t: number;
  vx: number;
  vy: number;
}

export interface IsoWorld {
  root: Container;
  /** Ground blocks, in chunks. */
  ground: Container;
  /** Roads and sidewalks, drawn in world units through the iso matrix. */
  roads: Container;
  /** River streaks; above the water, below roads and bridges. */
  waterFx: Container;
  /** Buildings, trees, lamps and people, depth-sorted. */
  objects: Container;
  /** Spare layer above everything (lamp glows are depth-sorted in `objects`). */
  lights: Container;
  /** Per tile, how far a building rises above the tile centre on screen (0 = no building). */
  roofTops: Float32Array;
  /** Lamp glows inside `objects`: they skip the night tint so they stay warm. */
  glows: Set<Sprite>;
  water: WaterTile[];
  streaks: Streak[];
  lamps: Lamp[];
  droplets: Droplet[];
  fountainAt: Vec2 | null;
  sidewalks: SidewalkNetwork;
  layout: IsoLayout;
  model: TownModel;
}

export function buildWorld(model: TownModel, assets: IsoAssets): IsoWorld {
  const layout = buildLayout(model);
  const rng = createRng(`${model.seed}::iso`);
  const root = new Container();
  const ground = new Container();
  const roads = new Container();
  const waterFx = new Container();
  const objects = new Container({ sortableChildren: true });
  const lights = new Container();
  root.addChild(ground, waterFx, roads, objects, lights);

  const world: IsoWorld = {
    root,
    ground,
    roads,
    waterFx,
    objects,
    lights,
    glows: new Set(),
    roofTops: new Float32Array(model.width * model.height),
    water: [],
    streaks: [],
    lamps: [],
    droplets: [],
    fountainAt: null,
    sidewalks: layout.sidewalks,
    layout,
    model,
  };

  buildGround(world, assets, rng, layout);
  drawRoads(world, layout);
  addHouses(world, assets, layout, rng);
  addYards(world, assets, layout, rng);
  addTrees(world, assets, rng, layout);
  addLamps(world, assets, rng, layout);
  addCars(world, layout);
  addStreaks(world);
  return world;
}

const isWaterTerrain = (t: number) => t === Terrain.WATER_SHALLOW || t === Terrain.WATER_DEEP;

/** A sprite placed so its block's top vertex sits on the tile's top corner. */
function blockSprite(tex: Texture, x: number, y: number, z: number, dy = 0): Sprite {
  const s = new Sprite(tex);
  const p = toScreen(x, y, z);
  s.position.set(p.x - 66, p.y - 1 + dy);
  return s;
}

function buildGround(world: IsoWorld, assets: IsoAssets, rng: Rng, layout: IsoLayout): void {
  const { model } = world;
  const { tiles, width: w, height: h } = model;
  const chunks = new Map<string, Container>();
  const chunkFor = (x: number, y: number) => {
    const key = `${Math.floor(x / CHUNK)},${Math.floor(y / CHUNK)}`;
    let c = chunks.get(key);
    if (!c) {
      c = new Container({ cullable: true, cullableChildren: false });
      chunks.set(key, c);
    }
    return c;
  };
  const well = model.landmarks.find((l) => l.kind === 'well');
  const wellTile = well ? Math.floor(well.at.y) * w + Math.floor(well.at.x) : -1;

  for (let s = 0; s <= w + h - 2; s++) {
    for (let x = Math.max(0, s - h + 1); x <= Math.min(w - 1, s); x++) {
      const y = s - x;
      const i = index(x, y, w);
      const chunk = chunkFor(x, y);
      const terrain = tiles.terrain[i];
      const overlay = tiles.overlay[i];
      const z = tiles.elevation[i];

      if (isWaterTerrain(terrain)) {
        // Bridges are drawn over the water with the roads.
        const sprite = blockSprite(assets.water, x, y, 0, WATER_DROP);
        chunk.addChild(sprite);
        world.water.push({ sprite, x, y, deep: terrain === Terrain.WATER_DEEP });
      } else if (layout.use[i] === Use.HOUSE || overlay === Overlay.ROAD_MAIN || overlay === Overlay.ROAD_STREET) {
        // Paving under buildings and across each road's full width, so the
        // sidewalk runs right up to every building front.
        chunk.addChild(blockSprite(assets.paving, x, y, z));
      } else if (overlay === Overlay.PLAZA) {
        if (i === wellTile) {
          chunk.addChild(blockSprite(assets.fountain, x, y, z, -2));
          world.fountainAt = { x: x + 0.5, y: y + 0.5 };
          addFountainSpray(world, x + 0.5, y + 0.5, z);
        } else {
          chunk.addChild(blockSprite(plazaTexture(), x, y, z));
          addPlazaDecor(world, assets, rng, x, y, z);
        }
      } else {
        const tex = terrain === Terrain.SAND ? assets.sand : terrain === Terrain.ROCK ? assets.dirt : assets.grass;
        const b = blockSprite(tex, x, y, z);
        const use = layout.use[i];
        if (terrain === Terrain.ROCK) b.tint = 0x9c968c;
        else if (use === Use.YARD || use === Use.SHED || use === Use.GARDEN) b.tint = 0xe6ffd9; // lawns a touch lusher than the fields
        else b.tint = grassTint(rng);
        chunk.addChild(b);
        if (use === Use.GARDEN || use === Use.COMMUNITY_GARDEN) chunk.addChild(farmSprite(assets.farmland, x, y, z));
      }
    }
  }
  const keys = [...chunks.keys()].sort((a, b) => {
    const [ax, ay] = a.split(',').map(Number);
    const [bx, by] = b.split(',').map(Number);
    return ax + ay - (bx + by) || ax - bx;
  });
  for (const key of keys) {
    const [cx, cy] = key.split(',').map(Number);
    const c = chunks.get(key)!;
    const left = toScreen(cx * CHUNK, (cy + 1) * CHUNK).x - 70;
    const right = toScreen((cx + 1) * CHUNK, cy * CHUNK).x + 70;
    const top = toScreen(cx * CHUNK, cy * CHUNK).y - STEP * 8;
    const bottom = toScreen((cx + 1) * CHUNK, (cy + 1) * CHUNK).y + 120;
    c.cullArea = new Rectangle(left, top, right - left, bottom - top);
    world.ground.addChild(c);
  }
}

/** Slight per-tile variation so big fields don't look like wallpaper. */
function grassTint(rng: Rng): number {
  const c = Math.round(255 * (0.93 + rng.next() * 0.07));
  return (c << 16) | (255 << 8) | c;
}

/**
 * Roads as vectors in world units: the container's matrix is the iso
 * projection, so widths and round joins foreshorten correctly. Bridge decks go
 * down first, then sidewalks, then asphalt (so junctions blend), then centre lines.
 */
function drawRoads(world: IsoWorld, layout: IsoLayout): void {
  const container = world.roads;
  container.setFromMatrix(new Matrix(HALF_W, HALF_H, -HALF_W, HALF_H, 0, 0));
  const { model } = world;
  const g = new Graphics();
  const trace = (pts: readonly Vec2[]) => {
    g.moveTo(pts[0].x, pts[0].y);
    for (let k = 1; k < pts.length; k++) g.lineTo(pts[k].x, pts[k].y);
  };
  const streets = layout.roads.filter((r) => r.kind !== 'path');
  const inMap = (p: Vec2) => p.x > 0.01 && p.y > 0.01 && p.x < model.width - 0.01 && p.y < model.height - 0.01;

  // Round caps fill junctions, but would spill past the map edge at the gates.
  const stroke = (width: number, color: number, pass: (r: (typeof streets)[number]) => number) => {
    for (const r of streets) {
      trace(r.points);
      g.stroke({ width: pass(r) + width, color, cap: 'butt', join: 'round' });
      for (const p of [r.points[0], r.points[r.points.length - 1]]) {
        if (inMap(p)) g.circle(p.x, p.y, (pass(r) + width) / 2).fill(color);
      }
    }
  };
  // Bridges first: a stone deck a little wider than the road, with parapets;
  // the road and its sidewalks then carry straight on across it.
  for (const b of layout.bridges) {
    trace(b.points);
    g.stroke({ width: b.width + SIDEWALK * 2 + 0.7, color: COLORS.parapetShade, cap: 'butt', join: 'round' });
    trace(b.points);
    g.stroke({ width: b.width + SIDEWALK * 2 + 0.5, color: COLORS.parapet, cap: 'butt', join: 'round' });
  }
  stroke(SIDEWALK * 2 + 0.1, COLORS.curb, (r) => r.width);
  stroke(SIDEWALK * 2, COLORS.sidewalk, (r) => r.width);
  stroke(0.08, COLORS.curb, (r) => r.width);
  stroke(0, COLORS.asphalt, (r) => r.width);

  for (const r of layout.roads.filter((rd) => rd.kind === 'path')) {
    trace(r.points);
    g.stroke({ width: r.width, color: COLORS.path, cap: 'round', join: 'round' });
  }

  // Dashed centre lines.
  for (const r of streets) {
    const len = polylineLength(r.points);
    const samples = samplePolyline(r.points, 0.1);
    let travelled = 0;
    for (let k = 1; k < samples.length; k++) {
      const seg = dist(samples[k - 1], samples[k]);
      const mid = travelled + seg / 2;
      travelled += seg;
      // Skip a little near each end so the dashes don't run into junctions.
      if (mid < 1.2 || mid > len - 1.2 || mid % 0.9 > 0.45) continue;
      g.moveTo(samples[k - 1].x, samples[k - 1].y).lineTo(samples[k].x, samples[k].y);
    }
    g.stroke({ width: 0.07, color: COLORS.lane, cap: 'butt' });
  }

  container.addChild(g);
}

function addFountainSpray(world: IsoWorld, x: number, y: number, z: number): void {
  const tex = dropTexture();
  for (let k = 0; k < 18; k++) {
    const d = new Sprite(tex);
    d.anchor.set(0.5);
    d.zIndex = depthOf(x, y, 15);
    world.objects.addChild(d);
    world.droplets.push({ sprite: d, t: k / 18, vx: Math.cos(k * 2.4) * 14, vy: Math.sin(k * 2.4) * 7 });
  }
  const p = toScreen(x, y, z);
  world.droplets.forEach((d) => d.sprite.position.set(p.x, p.y - 30));
}

/** Benches and the odd tree around the edge of the plaza. */
function addPlazaDecor(world: IsoWorld, assets: IsoAssets, rng: Rng, x: number, y: number, z: number): void {
  const { tiles, width: w, height: h } = world.model;
  const edges = DIRS4.filter(([dx, dy]) => !inBounds(x + dx, y + dy, w, h) || tiles.overlay[index(x + dx, y + dy, w)] !== Overlay.PLAZA);
  const roll = rng.next();
  if (!edges.length || roll > 0.3) return;
  const [dx, dy] = edges[0];
  const px = x + 0.5 + dx * 0.3;
  const py = y + 0.5 + dy * 0.3;
  const p = toScreen(px, py, z);
  const s = new Sprite(roll < 0.2 ? assets.benches[(x + y) % 2] : assets.tree);
  s.anchor.set(0.5, 0.9);
  if (roll >= 0.2) s.scale.set(1.8);
  s.position.set(p.x, p.y);
  s.zIndex = depthOf(px, py, 2);
  s.cullable = true;
  world.objects.addChild(s);
}

interface Palette {
  grounds: readonly number[];
  uppers: readonly number[];
  roofs: readonly number[];
  vary: boolean;
}

/**
 * One look per blueprint building: a facade family, a roof style and a usual
 * height, so related houses read as a matching set.
 */
function paletteFor(house: House, rng: Rng): Palette {
  const family = FAMILIES[rng.pick(KIND_FAMILIES[house.kind])];
  switch (house.style) {
    case 'office':
      return { grounds: FAMILIES.glass.shops, uppers: FAMILIES.glass.uppers, roofs: rng.chance(0.4) ? FLAT_ROOFS : [], vary: true };
    case 'shop':
      return { grounds: family.shops, uppers: family.uppers, roofs: rng.chance(0.5) ? FLAT_ROOFS : [], vary: true };
    case 'workshop':
      return { grounds: [...PLAIN_GROUND, ...family.homes], uppers: family.uppers, roofs: rng.chance(0.5) ? GREY_ROOFS : ARCHED_ROOFS, vary: false };
    case 'church':
      return { grounds: FAMILIES.beige.homes, uppers: [rng.pick(FAMILIES.beige.uppers)], roofs: [rng.pick([74, 82, 88])], vary: false };
    case 'hall':
      return { grounds: [...PLAIN_GROUND, ...FAMILIES.beige.shops], uppers: [rng.pick(FAMILIES.beige.uppers)], roofs: [rng.pick(FLAT_ROOFS)], vary: false };
    default:
      return { grounds: family.homes, uppers: family.uppers, roofs: rng.chance(0.85) ? [rng.pick(GABLE_ROOFS)] : [], vary: true };
  }
}

const MIRRORED: Record<Facing, Facing> = { S: 'E', E: 'S', N: 'W', W: 'N' };

/**
 * A ground floor whose entrance faces the street. Pieces with the entrance on
 * that side are used as they are; ones with it on the mirror side are flipped.
 * When nothing fits (homes whose street is behind them, to the north or west),
 * the house shows its back: an upper-floor piece (windows, no door) stands in
 * for the ground floor, since the real door faces away from the camera.
 */
function groundFloorFor(palette: Palette, facing: Facing, rng: Rng): { piece: number; mirror: boolean; back: boolean } {
  const options = [
    ...palette.grounds.filter((p) => ENTRANCE[p] === facing).map((piece) => ({ piece, mirror: false })),
    ...palette.grounds.filter((p) => ENTRANCE[p] === MIRRORED[facing]).map((piece) => ({ piece, mirror: true })),
  ];
  if (options.length) return { ...rng.pick(options), back: false };
  const plain = palette.grounds.filter((p) => ENTRANCE[p] === undefined);
  if (plain.length) return { piece: rng.pick(plain), mirror: false, back: false };
  return { piece: rng.pick(palette.uppers), mirror: rng.chance(0.5), back: true };
}

/**
 * Building pieces are drawn 4/3 size: Kenney's boxes cover three quarters of a
 * tile, so at this scale each fills its tile and a multi-tile footprint reads
 * as one continuous building instead of a cluster of separate towers.
 */
const PIECE_SCALE = 4 / 3;

function addHouses(world: IsoWorld, assets: IsoAssets, layout: IsoLayout, rng: Rng): void {
  const { model } = world;
  const k = PIECE_SCALE;
  for (const house of layout.houses) {
    const palette = paletteFor(house, rng);
    // One look for the whole building: same front, same upper floor, same roof, same height.
    const front = groundFloorFor(palette, house.facing, rng);
    const plain = palette.grounds.filter((p) => ENTRANCE[p] === undefined);
    const upper = rng.pick(palette.uppers);
    const roof = palette.roofs.length ? rng.pick(palette.roofs) : null;
    const floors = palette.vary ? Math.max(1, Math.min(6, house.floors + rng.pick([-1, 0, 0, 1]))) : house.floors;
    const roofMirror = roof !== null ? roofMirrorFor(roof, house.facing) : false;
    const own = new Set(house.tiles.map((t) => `${t.x},${t.y}`));
    const [fx, fy] = DIRS4[['N', 'E', 'S', 'W'].indexOf(house.facing)];

    for (const t of house.tiles) {
      // Only the street-side row gets entrances; the rest show plain walls.
      const onFront = !own.has(`${t.x + fx},${t.y + fy}`);
      const ground = onFront
        ? front
        : plain.length
          ? { piece: plain[(t.x + t.y) % plain.length], mirror: front.mirror, back: false }
          : { piece: upper, mirror: front.mirror, back: true };
      const z = model.tiles.elevation[index(t.x, t.y, model.width)];
      const cx = t.x + 0.5;
      const cy = t.y + 0.5;
      const c = toScreen(cx, cy, z);
      const depth = depthOf(cx, cy);
      const piece = (tex: Texture, y: number, order: number, bottomAnchored: boolean, flip: boolean) => {
        const s = new Sprite(tex);
        s.anchor.set(0.5, bottomAnchored ? 1 : 0);
        s.position.set(c.x, y);
        s.scale.set(flip ? -k : k, k);
        s.zIndex = depth + order;
        s.cullable = true;
        s.cullArea = new Rectangle(-tex.width / 2, bottomAnchored ? -tex.height : 0, tex.width, tex.height);
        world.objects.addChild(s);
      };
      let top = c.y + GROUND_TOP * k;
      if (ground.back) piece(assets.building(ground.piece), top - k, 1, false, ground.mirror);
      else piece(assets.groundFloor(ground.piece), c.y + 65 * k, 1, true, ground.mirror);
      for (let f = 1; f < floors; f++) {
        top -= FLOOR_H * k;
        piece(assets.building(upper), top - k, 1 + f, false, ground.mirror);
      }
      if (roof !== null) piece(assets.building(roof), top + ROOF_BOTTOM * k, 8, true, roofMirror);
      world.roofTops[index(t.x, t.y, model.width)] = c.y - top + (roof !== null ? 20 * k : 0);
    }
  }
}

/** A farm-pack sprite (256px tile) scaled and placed on tile (x, y). */
function farmSprite(tex: Texture, x: number, y: number, z: number): Sprite {
  const s = new Sprite(tex);
  const p = toScreen(x, y, z);
  s.scale.set(FARM_SCALE);
  s.position.set(p.x - FARM_TOP.x * FARM_SCALE, p.y - FARM_TOP.y * FARM_SCALE);
  return s;
}

/** Midpoint of one side of a tile, for depth-sorting things that stand on it. */
function edgeMid(x: number, y: number, side: Facing): Vec2 {
  return {
    N: { x: x + 0.5, y },
    E: { x: x + 1, y: y + 0.5 },
    S: { x: x + 0.5, y: y + 1 },
    W: { x, y: y + 0.5 },
  }[side];
}

/** Crops, sheds, hay and fences: everything that stands in the yards and gardens. */
function addYards(world: IsoWorld, assets: IsoAssets, layout: IsoLayout, rng: Rng): void {
  const { model } = world;
  const { width: w, height: h } = model;
  const shed = shedTexture();
  const add = (s: Sprite, at: Vec2, order: number) => {
    s.zIndex = depthOf(at.x, at.y, order);
    s.cullable = true;
    world.objects.addChild(s);
  };
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = index(x, y, w);
      const use = layout.use[i];
      const z = model.tiles.elevation[i];
      if (use === Use.GARDEN || use === Use.COMMUNITY_GARDEN) {
        const roll = rng.next();
        if (use === Use.COMMUNITY_GARDEN && roll < 0.12) add(farmSprite(assets.hay, x, y, z), { x: x + 0.5, y: y + 0.5 }, 4);
        else if (use === Use.COMMUNITY_GARDEN && roll < 0.2) add(farmSprite(assets.crates, x, y, z), { x: x + 0.5, y: y + 0.5 }, 4);
        else add(farmSprite(assets.crops[roll < 0.6 ? 0 : 1], x, y, z), { x: x + 0.5, y: y + 0.5 }, 4);
      } else if (use === Use.SHED) {
        const s = new Sprite(shed);
        const p = toScreen(x + 0.5, y + 0.5, z);
        s.anchor.set(0.5, 0.78);
        s.position.set(p.x, p.y);
        if (rng.chance(0.5)) s.scale.x = -1;
        add(s, { x: x + 0.5, y: y + 0.5 }, 4);
      }
    }
  }
  for (const f of layout.fences) {
    const z = model.tiles.elevation[index(f.x, f.y, w)];
    add(farmSprite(assets.fence(f.side), f.x, f.y, z), edgeMid(f.x, f.y, f.side), 3);
  }
}

/** Mirror a pitched roof so its ridge runs along the street (parallel to the house front). */
function roofMirrorFor(roof: number, facing: Facing): boolean {
  const ridge = ROOF_RIDGE[roof];
  if (!ridge) return facing === 'E' || facing === 'N';
  // A south or north front runs along x, so the ridge should too.
  const want = facing === 'S' || facing === 'N' ? 'x' : 'y';
  return ridge !== want;
}

function addTrees(world: IsoWorld, assets: IsoAssets, rng: Rng, layout: IsoLayout): void {
  const { model } = world;
  const { tiles, width: w, height: h, center, townRadius } = model;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = index(x, y, w);
      const use = layout.use[i];
      if (tiles.terrain[i] !== Terrain.GRASS || tiles.overlay[i] !== Overlay.NONE) continue;
      if (use === Use.HOUSE || use === Use.SHED || use === Use.GARDEN || use === Use.COMMUNITY_GARDEN) continue;
      let nearStreet = false;
      for (const [dx, dy] of DIRS4) {
        const nx = x + dx;
        const ny = y + dy;
        if (!inBounds(nx, ny, w, h)) continue;
        const j = index(nx, ny, w);
        if (tiles.overlay[j] !== Overlay.NONE || isWaterTerrain(tiles.terrain[j]) || layout.use[j] === Use.HOUSE) nearStreet = true;
      }
      const inTown = Math.hypot(x + 0.5 - center.x, y + 0.5 - center.y) < townRadius;
      const chance = use === Use.YARD ? (nearStreet ? 0.04 : 0.16) : use === Use.GREEN ? 0.3 : nearStreet ? 0.03 : inTown ? 0.1 : 0.28;
      const roll = rng.next();
      const jx = rng.range(-0.25, 0.25);
      const jy = rng.range(-0.25, 0.25);
      const size = rng.range(1.6, 2.3);
      if (roll > chance) continue;
      const px = x + 0.5 + jx;
      const py = y + 0.5 + jy;
      const p = toScreen(px, py, tiles.elevation[i]);
      // Mix the round city tree with conifers and slimmer trees.
      const kind = rng.next();
      const tex = kind < 0.5 ? assets.tree : assets.foliage[Math.floor(kind * 1000) % assets.foliage.length];
      const t = new Sprite(tex);
      t.anchor.set(0.5, 0.97);
      t.scale.set(tex === assets.tree ? size : size * 1.45);
      t.position.set(p.x, p.y);
      t.zIndex = depthOf(px, py, 2);
      t.cullable = true;
      t.cullArea = new Rectangle(-tex.width / 2, -tex.height, tex.width, tex.height + 2);
      world.objects.addChild(t);
    }
  }
}

/** Lamps along the outer edge of the sidewalks, arms reaching over the road. */
function addLamps(world: IsoWorld, assets: IsoAssets, rng: Rng, layout: IsoLayout): void {
  const { model } = world;
  const glow = glowTexture();
  const placed: Vec2[] = [];
  layout.sidewalks.paths.forEach((path, n) => {
    const total = path.at[path.at.length - 1];
    // Stagger the two sides of a road so lamps alternate.
    for (let s = (n % 2) * (LAMP_SPACING / 2) + 1.5; s < total - 1; s += LAMP_SPACING) {
      let k = 1;
      while (k < path.at.length - 1 && path.at[k] < s) k++;
      const a = path.points[k - 1];
      const b = path.points[k];
      const f = (s - path.at[k - 1]) / Math.max(1e-6, path.at[k] - path.at[k - 1]);
      const on = { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f };
      if (placed.some((q) => dist(q, on) < 2.5)) continue;
      const tx = Math.floor(on.x);
      const ty = Math.floor(on.y);
      if (!inBounds(tx, ty, model.width, model.height) || isWaterTerrain(model.tiles.terrain[index(tx, ty, model.width)])) continue;
      if (layout.use[index(tx, ty, model.width)] === Use.HOUSE) continue;
      placed.push(on);
      // Outward = away from the road: the side the sidewalk was offset to.
      const tangent = { x: b.x - a.x, y: b.y - a.y };
      const tl = Math.hypot(tangent.x, tangent.y) || 1;
      const normal = { x: (tangent.y / tl) * path.side, y: (-tangent.x / tl) * path.side };
      const edge = { x: on.x + normal.x * (SIDEWALK / 2 - 0.08), y: on.y + normal.y * (SIDEWALK / 2 - 0.08) };
      const z = model.tiles.elevation[index(tx, ty, model.width)];
      const base = toScreen(edge.x, edge.y, z);
      const inward = toScreen(-normal.x, -normal.y);
      const armRight = inward.x >= 0;
      const lamp = new Sprite(assets.lamp);
      lamp.anchor.set(3 / 22, 44 / 46);
      lamp.scale.set(armRight ? 1.25 : -1.25, 1.25);
      lamp.position.set(base.x, base.y);
      lamp.zIndex = depthOf(edge.x, edge.y, 3);
      lamp.cullable = true;
      world.objects.addChild(lamp);

      const tip = { x: base.x + (armRight ? 20 : -20), y: base.y - 52 };
      const bulb = new Sprite(glow);
      bulb.anchor.set(0.5);
      bulb.scale.set(0.42);
      bulb.position.set(tip.x, tip.y);
      bulb.tint = 0xffd890;
      bulb.blendMode = 'add';
      bulb.alpha = 0;
      const pool = new Sprite(glow);
      pool.anchor.set(0.5);
      pool.scale.set(1.3, 0.62);
      pool.position.set(tip.x, base.y + 4);
      pool.tint = 0xffc870;
      pool.blendMode = 'add';
      pool.alpha = 0;
      // Sorted with everything else, so a building in front hides a lamp's glow.
      bulb.zIndex = lamp.zIndex + 1;
      pool.zIndex = lamp.zIndex - 1;
      bulb.cullable = true;
      pool.cullable = true;
      world.objects.addChild(pool, bulb);
      world.glows.add(bulb);
      world.glows.add(pool);
      world.lamps.push({ glow: bulb, pool, phase: rng.range(0, 100), faulty: rng.chance(0.12) });
    }
  });
}

/** Parked cars along the side-street kerbs, lined up with the road. */
function addCars(world: IsoWorld, layout: IsoLayout): void {
  for (const car of layout.cars) {
    // Parked either way round.
    const heading = car.angle + (car.variant % 2 ? Math.PI : 0);
    const s = new Sprite(carTexture(CAR_COLOURS[car.variant % CAR_COLOURS.length], heading));
    const p = toScreen(car.x, car.y);
    s.anchor.set(CAR_TEX.cx / CAR_TEX.w, CAR_TEX.cy / CAR_TEX.h);
    s.position.set(p.x, p.y);
    s.zIndex = depthOf(car.x, car.y, 5);
    s.cullable = true;
    world.objects.addChild(s);
  }
}

/** Pale streaks drifting down each river, so the water visibly runs. */
function addStreaks(world: IsoWorld): void {
  const { model } = world;
  const tex = streakTexture();
  const wet = (p: Vec2) => isWaterTerrain(model.tiles.terrain[Math.floor(p.y) * model.width + Math.floor(p.x)]);
  for (const river of model.rivers) {
    const path = river.points.filter(wet);
    if (path.length < 2) continue;
    const at: number[] = [0];
    for (let k = 1; k < path.length; k++) at.push(at[k - 1] + Math.hypot(path[k].x - path[k - 1].x, path[k].y - path[k - 1].y));
    const total = at[at.length - 1];
    const count = Math.floor(total / 1.6);
    for (let k = 0; k < count; k++) {
      const sprite = new Sprite(tex);
      sprite.anchor.set(0.5);
      sprite.blendMode = 'add';
      sprite.alpha = 0.55;
      sprite.cullable = true;
      world.waterFx.addChild(sprite);
      world.streaks.push({ sprite, path, at, s: (k / count) * total + (k % 3) * 0.37, speed: 1.1 + (k % 5) * 0.15 });
    }
  }
}

export { WATER_DROP };

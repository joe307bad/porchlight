// Stage 8: paint everything into the tile grid the iso view will use.
// Terrain comes from the heightmap and water, then roads and bridges, the
// plaza, and buildings. The grid is flat (elevation 0 everywhere). A tile is
// walkable if a character could stand on it and reach it from spawn; ground
// cut off from spawn (an island, a yard boxed in by houses) is marked unwalkable.

import { DIRS4, index, inBounds } from '../core/grid';
import { arcPosition, closestOnPolyline } from '../core/geometry';
import { createTileGrid } from '../model/empty';
import { Overlay, Terrain, type TileGrid, type TownModel } from '../model/types';
import { tilesIn } from './parcels';
import { ROAD_MAIN, ROAD_PATH, ROAD_STREET, lakeMask, riverMask, rockMask, roadHalfWidth, roadRaster, slopeField } from './raster';
import type { Stage } from './stage';

/** Lake tiles deeper than this below the water level are deep water. */
const DEEP_WATER = 0.04;
/** Rivers at least this wide have a deep channel. */
const DEEP_RIVER_WIDTH = 2.5;
/** Dry tiles this close above the water level, beside water, are sand. */
const BEACH_HEIGHT = 0.015;

const WALKABLE_TERRAIN = new Set<number>([Terrain.GRASS, Terrain.DIRT, Terrain.SAND]);
const WALKABLE_OVERLAY = new Set<number>([
  Overlay.ROAD_MAIN,
  Overlay.ROAD_STREET,
  Overlay.PATH,
  Overlay.BRIDGE,
  Overlay.PLAZA,
]);

export const rasterize: Stage = {
  name: 'rasterize',
  run(model) {
    const { width: w, height: h, heightmap: hm, waterLevel, params: p } = model;
    const tiles = createTileGrid(w, h);
    const lake = lakeMask(model);
    const river = riverMask(model);
    const water = new Uint8Array(w * h);
    for (let i = 0; i < water.length; i++) water[i] = lake[i] | river[i];
    const rock = rockMask(slopeField(model, water), p);

    // Terrain.
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const i = index(x, y, w);
        if (lake[i]) tiles.terrain[i] = hm[i] < waterLevel - DEEP_WATER ? Terrain.WATER_DEEP : Terrain.WATER_SHALLOW;
        else if (river[i]) tiles.terrain[i] = Terrain.WATER_SHALLOW;
        else if (rock[i]) tiles.terrain[i] = Terrain.ROCK;
        else if (hm[i] < waterLevel + BEACH_HEIGHT && besideWater(x, y, w, h, water)) tiles.terrain[i] = Terrain.SAND;
        else tiles.terrain[i] = Terrain.GRASS;
      }
    }
    // Deep channel down the middle of wide rivers.
    for (const r of model.rivers) {
      for (let k = 0; k < r.points.length; k++) {
        if (r.widthAt[k] < DEEP_RIVER_WIDTH) continue;
        const x = Math.floor(r.points[k].x);
        const y = Math.floor(r.points[k].y);
        if (inBounds(x, y, w, h)) tiles.terrain[index(x, y, w)] = Terrain.WATER_DEEP;
      }
    }

    // Roads; over water only where a bridge carries them.
    const road = roadRaster(model);
    const bridgeSpans = model.bridges.map((b) => {
      const pts = model.roads.edges[b.roadEdgeId].points;
      const a = arcPosition(b.from, pts);
      const c = arcPosition(b.to, pts);
      return { edge: b.roadEdgeId, lo: Math.min(a, c), hi: Math.max(a, c) };
    });
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const i = index(x, y, w);
        if (!road.kind[i]) continue;
        if (!water[i]) {
          tiles.overlay[i] = road.kind[i] === ROAD_MAIN ? Overlay.ROAD_MAIN : road.kind[i] === ROAD_STREET ? Overlay.ROAD_STREET : Overlay.PATH;
          if (road.kind[i] === ROAD_PATH) tiles.terrain[i] = Terrain.DIRT;
          continue;
        }
        const edge = model.roads.edges[road.edgeId[i]];
        const at = arcPosition({ x: x + 0.5, y: y + 0.5 }, edge.points);
        const near = closestOnPolyline({ x: x + 0.5, y: y + 0.5 }, edge.points).d < roadHalfWidth(edge.kind, p);
        if (near && bridgeSpans.some((s) => s.edge === edge.id && at >= s.lo - 0.5 && at <= s.hi + 0.5)) {
          tiles.overlay[i] = Overlay.BRIDGE;
        }
      }
    }

    // Plaza.
    for (const lm of model.landmarks) {
      if (lm.kind !== 'plaza') continue;
      for (const lotId of lm.lotIds) {
        for (const t of tilesIn(model.lots[lotId].polygon, [], w, h)) tiles.overlay[t] = Overlay.PLAZA;
      }
    }

    // Buildings.
    for (const b of model.buildings) for (const t of b.tiles) tiles.buildingId[index(t.x, t.y, w)] = b.id;

    // The town is drawn flat: tiles.elevation stays 0 everywhere (the heightmap
    // still shapes water, rock, roads and contours).

    computeWalkable(tiles);
    const spawnTile = pickSpawn(model, tiles);
    pruneUnreachable(tiles, spawnTile);
    const spawn = { x: (spawnTile % w) + 0.5, y: Math.floor(spawnTile / w) + 0.5 };
    return { ...model, tiles, spawn };
  },
  check(model) {
    const errors: string[] = [];
    const { width: w, height: h, tiles: t } = model;
    const n = w * h;
    for (const [name, arr] of Object.entries({
      terrain: t.terrain,
      overlay: t.overlay,
      buildingId: t.buildingId,
      elevation: t.elevation,
      walkable: t.walkable,
    })) {
      if (arr.length !== n) errors.push(`tiles.${name} has length ${arr.length}, expected ${n}`);
    }
    const sx = Math.floor(model.spawn.x);
    const sy = Math.floor(model.spawn.y);
    if (!inBounds(sx, sy, w, h)) return [...errors, 'spawn is outside the map'];
    const start = index(sx, sy, w);
    if (!t.walkable[start]) errors.push('spawn is not walkable');

    // Every walkable tile is reachable from spawn.
    const seen = flood(t, start);
    for (let i = 0; i < n; i++) {
      if (t.walkable[i] && !seen[i]) {
        errors.push(`walkable tile ${i % w},${Math.floor(i / w)} can't be reached from spawn`);
        break;
      }
    }
    // The tile grid agrees with the vector layers.
    for (const b of model.buildings) {
      for (const p of b.tiles) {
        const i = index(p.x, p.y, w);
        if (t.buildingId[i] !== b.id) errors.push(`tile ${p.x},${p.y} should belong to building ${b.id}`);
        if (t.walkable[i]) errors.push(`building tile ${p.x},${p.y} is walkable`);
      }
    }
    if (t.elevation.some((e) => e !== 0)) errors.push('the tile grid should be flat (elevation 0)');
    let buildingTiles = 0;
    for (let i = 0; i < n; i++) if (t.buildingId[i] >= 0) buildingTiles++;
    const expected = model.buildings.reduce((s, b) => s + b.tiles.length, 0);
    if (buildingTiles !== expected) errors.push(`${buildingTiles} building tiles, expected ${expected}`);
    for (const e of model.roads.edges) {
      for (const p of e.points) {
        const x = Math.floor(p.x);
        const y = Math.floor(p.y);
        if (!inBounds(x, y, w, h)) continue;
        const i = index(x, y, w);
        const wet = t.terrain[i] === Terrain.WATER_SHALLOW || t.terrain[i] === Terrain.WATER_DEEP;
        if (!wet && t.overlay[i] === Overlay.NONE) errors.push(`road ${e.id} vertex at ${x},${y} has no road tile`);
      }
    }
    return errors.slice(0, 5);
  },
};

function besideWater(x: number, y: number, w: number, h: number, water: Uint8Array): boolean {
  for (const [dx, dy] of DIRS4) {
    const nx = x + dx;
    const ny = y + dy;
    if (inBounds(nx, ny, w, h) && water[index(nx, ny, w)]) return true;
  }
  return false;
}

export function computeWalkable(tiles: TileGrid): void {
  for (let i = 0; i < tiles.walkable.length; i++) {
    const onOverlay = WALKABLE_OVERLAY.has(tiles.overlay[i]);
    const onGround = WALKABLE_TERRAIN.has(tiles.terrain[i]);
    tiles.walkable[i] = tiles.buildingId[i] === -1 && (onOverlay || onGround) ? 1 : 0;
  }
}

/** The plaza or road tile nearest the town center (any walkable tile as a fallback). */
function pickSpawn(model: TownModel, tiles: TileGrid): number {
  const { width: w, height: h } = model;
  const start = index(Math.floor(model.center.x), Math.floor(model.center.y), w);
  const good = (i: number) => tiles.walkable[i] === 1 && tiles.overlay[i] !== Overlay.NONE;
  const any = (i: number) => tiles.walkable[i] === 1;
  for (const accept of [good, any]) {
    const seen = new Uint8Array(w * h);
    const queue = [start];
    seen[start] = 1;
    for (let head = 0; head < queue.length; head++) {
      const i = queue[head];
      if (accept(i)) return i;
      const x = i % w;
      const y = (i - x) / w;
      for (const [dx, dy] of DIRS4) {
        const nx = x + dx;
        const ny = y + dy;
        if (!inBounds(nx, ny, w, h)) continue;
        const j = index(nx, ny, w);
        if (!seen[j]) {
          seen[j] = 1;
          queue.push(j);
        }
      }
    }
  }
  return start;
}

/** 4-connected flood fill over walkable tiles. */
function flood(tiles: TileGrid, start: number): Uint8Array {
  const { width: w, height: h } = tiles;
  const seen = new Uint8Array(w * h);
  if (!tiles.walkable[start]) return seen;
  const queue = [start];
  seen[start] = 1;
  for (let head = 0; head < queue.length; head++) {
    const i = queue[head];
    const x = i % w;
    const y = (i - x) / w;
    for (const [dx, dy] of DIRS4) {
      const nx = x + dx;
      const ny = y + dy;
      if (!inBounds(nx, ny, w, h)) continue;
      const j = index(nx, ny, w);
      if (!seen[j] && tiles.walkable[j]) {
        seen[j] = 1;
        queue.push(j);
      }
    }
  }
  return seen;
}

function pruneUnreachable(tiles: TileGrid, spawn: number): void {
  const seen = flood(tiles, spawn);
  for (let i = 0; i < seen.length; i++) if (!seen[i]) tiles.walkable[i] = 0;
}

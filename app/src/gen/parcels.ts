// Shared helpers for blocks, lots and buildings: which tiles can be built on,
// which tiles a polygon covers, and which side of a parcel faces a road.

import { DIRS4, index } from '../core/grid';
import { pointInPolygon, polygonBounds, traceCellOutlines } from '../core/geometry';
import type { Facing, TownModel, Vec2 } from '../model/types';
import { rockMask, roadRaster, slopeField, waterMask, type RoadRaster } from './raster';

export const FACINGS: readonly Facing[] = ['N', 'E', 'S', 'W']; // same order as DIRS4

export interface Ground {
  water: Uint8Array;
  road: RoadRaster;
  /** Dry, gentle, off-road tiles inside the town radius. */
  free: Uint8Array;
}

export function ground(model: TownModel): Ground {
  const { width: w, height: h, center: C, townRadius: R } = model;
  const water = waterMask(model);
  const road = roadRaster(model);
  const rock = rockMask(slopeField(model, water), model.params);
  const free = new Uint8Array(w * h);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = index(x, y, w);
      const inside = Math.hypot(x + 0.5 - C.x, y + 0.5 - C.y) <= R;
      free[i] = inside && !water[i] && !rock[i] && road.kind[i] === 0 ? 1 : 0;
    }
  }
  return { water, road, free };
}

/** A tile a building can face: road on dry land. */
export const isStreetTile = (g: Ground, i: number) => g.road.kind[i] !== 0 && !g.water[i];

/** Road edges whose street tiles touch one of these tiles, with how many touches each. */
export function adjacentRoadEdges(tiles: readonly number[], g: Ground, w: number, h: number): Map<number, number> {
  const votes = new Map<number, number>();
  for (const t of tiles) {
    const x = t % w;
    const y = (t - x) / w;
    for (const [dx, dy] of DIRS4) {
      const nx = x + dx;
      const ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
      const n = index(nx, ny, w);
      if (isStreetTile(g, n)) votes.set(g.road.edgeId[n], (votes.get(g.road.edgeId[n]) ?? 0) + 1);
    }
  }
  return votes;
}

/** Tiles whose centres fall inside the polygon and outside its holes. */
export function tilesIn(polygon: readonly Vec2[], holes: readonly Vec2[][], w: number, h: number): number[] {
  const b = polygonBounds(polygon);
  const out: number[] = [];
  for (let y = Math.max(0, Math.floor(b.y0)); y < Math.min(h, Math.ceil(b.y1)); y++) {
    for (let x = Math.max(0, Math.floor(b.x0)); x < Math.min(w, Math.ceil(b.x1)); x++) {
      const c = { x: x + 0.5, y: y + 0.5 };
      if (pointInPolygon(c, polygon) && !holes.some((hole) => pointInPolygon(c, hole))) out.push(index(x, y, w));
    }
  }
  return out;
}

/** For each facing (N, E, S, W): how many of these tiles have a street tile right next to them on that side. */
export function frontageCounts(tiles: readonly number[], g: Ground, w: number, h: number): number[] {
  const counts = [0, 0, 0, 0];
  for (const i of tiles) {
    const x = i % w;
    const y = (i - x) / w;
    DIRS4.forEach(([dx, dy], d) => {
      const nx = x + dx;
      const ny = y + dy;
      if (nx >= 0 && ny >= 0 && nx < w && ny < h && isStreetTile(g, index(nx, ny, w))) counts[d]++;
    });
  }
  return counts;
}

/** 4-connected components of the given tiles. */
export function components(tiles: readonly number[], w: number): number[][] {
  const set = new Set(tiles);
  const seen = new Set<number>();
  const out: number[][] = [];
  for (const t of tiles) {
    if (seen.has(t)) continue;
    const comp = [t];
    seen.add(t);
    for (let head = 0; head < comp.length; head++) {
      const c = comp[head];
      const x = c % w;
      for (const n of [c - w, c + w, x > 0 ? c - 1 : -1, x < w - 1 ? c + 1 : -1]) {
        if (n >= 0 && set.has(n) && !seen.has(n)) {
          seen.add(n);
          comp.push(n);
        }
      }
    }
    out.push(comp);
  }
  return out;
}

/** Outline rings of a tile set, in world coordinates (outer ring first). */
export function outline(tiles: readonly number[], w: number): Vec2[][] {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const t of tiles) {
    const x = t % w;
    const y = (t - x) / w;
    x0 = Math.min(x0, x);
    y0 = Math.min(y0, y);
    x1 = Math.max(x1, x);
    y1 = Math.max(y1, y);
  }
  const lw = x1 - x0 + 1;
  const lh = y1 - y0 + 1;
  const mask = new Uint8Array(lw * lh);
  for (const t of tiles) {
    const x = t % w;
    const y = (t - x) / w;
    mask[(y - y0) * lw + (x - x0)] = 1;
  }
  return traceCellOutlines(mask, lw, lh).map((ring) => ring.map((p) => ({ x: p.x + x0, y: p.y + y0 })));
}

// Grids derived from the vector layers. Every stage that needs to know where
// water, roads or steep ground are uses these, and so does rasterize, so the
// blocks, lots and buildings always agree with the final tile grid.

import { DIRS4, index } from '../core/grid';
import { closestOnSegment, distToSegment } from '../core/geometry';
import type { GenParams } from '../model/params';
import type { RoadEdge, RoadKind, TownModel } from '../model/types';

export const ROAD_NONE = 0;
export const ROAD_MAIN = 1;
export const ROAD_STREET = 2;
export const ROAD_PATH = 3;

const KIND_CODE: Record<RoadKind, number> = { main: ROAD_MAIN, street: ROAD_STREET, path: ROAD_PATH };
/** Lower wins when roads of different kinds overlap a tile. */
const KIND_RANK = [99, 0, 1, 2];

/** Width a road is drawn at in the blueprint, in tiles. */
/** Width of the sidewalk the explore view draws along each side of a road or street (tiles). */
export const SIDEWALK_WIDTH = 0.45;

/** How far a road's drawn surface reaches from its centreline, sidewalks included (footpaths have none). */
export function roadReach(kind: RoadKind, params: GenParams): number {
  return roadDrawWidth(kind, params) / 2 + (kind === 'path' ? 0 : SIDEWALK_WIDTH);
}

export function roadDrawWidth(kind: RoadKind, params: GenParams): number {
  if (kind === 'main') return params.mainRoadWidth;
  if (kind === 'street') return params.streetWidth;
  return 0.4;
}

/**
 * Tiles whose centre is closer than this to a road's centreline belong to the road.
 * Half a tile wider than the drawn road, so buildings on neighbouring tiles never
 * touch the stroke, and at least 0.75 so diagonal roads stay 4-connected.
 */
export function roadHalfWidth(kind: RoadKind, params: GenParams): number {
  return Math.max(0.75, roadDrawWidth(kind, params) / 2 + 0.5);
}

/** Cells below the water level. */
export function lakeMask(model: TownModel): Uint8Array {
  const out = new Uint8Array(model.width * model.height);
  for (let i = 0; i < out.length; i++) out[i] = model.heightmap[i] < model.waterLevel ? 1 : 0;
  return out;
}

/** Half-width a river occupies on the grid (at least enough to cover its own cells). */
export const riverHalfWidth = (width: number): number => Math.max(0.75, width / 2);

/** Cells covered by river channels. */
export function riverMask(model: TownModel): Uint8Array {
  const { width: w, height: h } = model;
  const out = new Uint8Array(w * h);
  for (const river of model.rivers) {
    const pts = river.points;
    for (let s = 0; s + 1 < pts.length; s++) {
      const a = pts[s];
      const b = pts[s + 1];
      const r = Math.max(riverHalfWidth(river.widthAt[s]), riverHalfWidth(river.widthAt[s + 1]));
      const x0 = Math.max(0, Math.floor(Math.min(a.x, b.x) - r));
      const x1 = Math.min(w - 1, Math.ceil(Math.max(a.x, b.x) + r));
      const y0 = Math.max(0, Math.floor(Math.min(a.y, b.y) - r));
      const y1 = Math.min(h - 1, Math.ceil(Math.max(a.y, b.y) + r));
      for (let y = y0; y <= y1; y++) {
        for (let x = x0; x <= x1; x++) {
          const c = closestOnSegment({ x: x + 0.5, y: y + 0.5 }, a, b);
          const hw = riverHalfWidth(river.widthAt[s] + (river.widthAt[s + 1] - river.widthAt[s]) * c.t);
          if (Math.hypot(c.point.x - x - 0.5, c.point.y - y - 0.5) < hw) out[index(x, y, w)] = 1;
        }
      }
    }
  }
  return out;
}

export function waterMask(model: TownModel): Uint8Array {
  const out = lakeMask(model);
  const rivers = riverMask(model);
  for (let i = 0; i < out.length; i++) out[i] |= rivers[i];
  return out;
}

/** Largest height change to a dry 4-neighbour; water cells and banks facing water ignore the water. */
export function slopeField(model: TownModel, water: Uint8Array): Float32Array {
  const { width: w, height: h, heightmap: hm } = model;
  const out = new Float32Array(w * h);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = index(x, y, w);
      if (water[i]) continue;
      let s = 0;
      for (const [dx, dy] of DIRS4) {
        const nx = x + dx;
        const ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        const j = index(nx, ny, w);
        if (!water[j]) s = Math.max(s, Math.abs(hm[i] - hm[j]));
      }
      out[i] = s;
    }
  }
  return out;
}

/** Max of a field over each cell's 3x3 neighbourhood. */
export function dilate(field: Float32Array, w: number, h: number): Float32Array {
  const out = new Float32Array(field.length);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      let m = -Infinity;
      for (let dy = -1; dy <= 1; dy++) {
        for (let dx = -1; dx <= 1; dx++) {
          const nx = x + dx;
          const ny = y + dy;
          if (nx >= 0 && ny >= 0 && nx < w && ny < h) m = Math.max(m, field[ny * w + nx]);
        }
      }
      out[y * w + x] = m;
    }
  }
  return out;
}

export interface RoadRaster {
  /** ROAD_* code per cell. */
  kind: Uint8Array;
  /** Id of the road edge that owns each cell, or -1. */
  edgeId: Int32Array;
}

export function roadRaster(model: TownModel, edges: readonly RoadEdge[] = model.roads.edges): RoadRaster {
  const { width: w, height: h, params } = model;
  const kind = new Uint8Array(w * h);
  const edgeId = new Int32Array(w * h).fill(-1);
  const best = new Float32Array(w * h).fill(Infinity);
  for (const edge of edges) {
    const code = KIND_CODE[edge.kind];
    const hw = roadHalfWidth(edge.kind, params);
    const pts = edge.points;
    for (let s = 0; s + 1 < pts.length; s++) {
      const a = pts[s];
      const b = pts[s + 1];
      const x0 = Math.max(0, Math.floor(Math.min(a.x, b.x) - hw));
      const x1 = Math.min(w - 1, Math.ceil(Math.max(a.x, b.x) + hw));
      const y0 = Math.max(0, Math.floor(Math.min(a.y, b.y) - hw));
      const y1 = Math.min(h - 1, Math.ceil(Math.max(a.y, b.y) + hw));
      for (let y = y0; y <= y1; y++) {
        for (let x = x0; x <= x1; x++) {
          const d = distToSegment({ x: x + 0.5, y: y + 0.5 }, a, b);
          if (d >= hw) continue;
          const i = index(x, y, w);
          const cur = kind[i];
          const better = cur === ROAD_NONE || KIND_RANK[code] < KIND_RANK[cur] || (code === cur && d < best[i]);
          if (better) {
            kind[i] = code;
            edgeId[i] = edge.id;
            best[i] = d;
          }
        }
      }
    }
  }
  return { kind, edgeId };
}

/** Ground too steep to build or walk on. */
export function rockMask(slope: Float32Array, params: GenParams): Uint8Array {
  const out = new Uint8Array(slope.length);
  for (let i = 0; i < slope.length; i++) out[i] = slope[i] > params.rockSlope ? 1 : 0;
  return out;
}

/** Multi-source BFS distance (4-connected steps) from every set cell. */
export function distanceFrom(mask: Uint8Array, w: number, h: number): Float32Array {
  const d = new Float32Array(w * h).fill(Infinity);
  const queue: number[] = [];
  for (let i = 0; i < mask.length; i++) {
    if (mask[i]) {
      d[i] = 0;
      queue.push(i);
    }
  }
  for (let head = 0; head < queue.length; head++) {
    const i = queue[head];
    const x = i % w;
    const y = (i - x) / w;
    for (const [dx, dy] of DIRS4) {
      const nx = x + dx;
      const ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
      const j = ny * w + nx;
      if (d[j] === Infinity) {
        d[j] = d[i] + 1;
        queue.push(j);
      }
    }
  }
  return d;
}

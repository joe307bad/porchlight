// A* cost of entering each cell when routing a road. Water is allowed but
// expensive (it becomes a bridge, so crossings land at narrow points and run
// straight across); ground steeper than maxRoadSlope within a tile is blocked.

import { cellsOnSegment } from '../core/grid';
import type { TownModel, Vec2 } from '../model/types';
import { dilate, slopeField, waterMask } from './raster';

const SLOPE_PENALTY = 1.5;
/** Roads shouldn't run along the map border; tiles this close to it cost extra. */
const BORDER_BAND = 6;
const BORDER_PENALTY = 2;

export interface RoadTerrain {
  water: Uint8Array;
  slope: Float32Array;
  /** Cost of entering each cell; Infinity where blocked. */
  cost: Float32Array;
}

export function roadTerrain(model: TownModel): RoadTerrain {
  const { width: w, height: h, params: p } = model;
  const water = waterMask(model);
  const slope = slopeField(model, water);
  const near = dilate(slope, w, h); // a road is a few tiles wide, so look one tile around
  const cost = new Float32Array(w * h);
  for (let i = 0; i < cost.length; i++) {
    const x = i % w;
    const y = (i - x) / w;
    const border = Math.max(0, BORDER_BAND - Math.min(x, y, w - 1 - x, h - 1 - y)) * BORDER_PENALTY;
    if (water[i]) cost[i] = 1 + p.bridgeCost + border;
    else if (near[i] > p.maxRoadSlope) cost[i] = Infinity;
    else cost[i] = 1 + SLOPE_PENALTY * (near[i] / p.maxRoadSlope) ** 2 + border;
  }
  return { water, slope, cost };
}

/** True if every tile under segment ab is water or gentle enough for a road. */
export function roadSegmentOk(t: RoadTerrain, model: TownModel): (a: Vec2, b: Vec2) => boolean {
  const { width: w, height: h, params } = model;
  return (a, b) =>
    cellsOnSegment(a.x, a.y, b.x, b.y).every(([x, y]) => {
      if (x < 0 || y < 0 || x >= w || y >= h) return false;
      const i = y * w + x;
      return t.water[i] === 1 || t.slope[i] <= params.maxRoadSlope;
    });
}

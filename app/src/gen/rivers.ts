// Stage 2: rivers. Priority-flood fills pits so every cell drains to an outlet
// (lake or map edge), flow accumulation sizes the channels, and each river
// walks steepest descent from a high source until it reaches water, the map
// edge, or another river. The channel is then carved so it never runs uphill.

import { DIRS4, index } from '../core/grid';
import { chaikin, distToPolyline } from '../core/geometry';
import { MinHeap } from '../core/heap';
import type { River, TownModel, Vec2 } from '../model/types';
import type { Stage } from './stage';

const FILL_EPSILON = 1e-5;
const WIDTH_PER_SQRT_FLOW = 0.12;
const MIN_WIDTH = 0.8;
const MAX_SOURCE_TRIES = 300;

export interface Drainage {
  /** Downstream neighbour per cell, or -1 for outlets (water and the map edge). */
  down: Int32Array;
  /** Cells draining through each cell, itself included. */
  flow: Float32Array;
}

/** Priority-flood (Barnes et al.) plus steepest descent on the filled surface. */
export function computeDrainage(model: TownModel): Drainage {
  const { width: w, height: h, heightmap: hm, waterLevel } = model;
  const n = w * h;
  const filled = new Float32Array(n);
  const seen = new Uint8Array(n);
  const down = new Int32Array(n).fill(-1);
  const heap = new MinHeap();

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = index(x, y, w);
      const edge = x === 0 || y === 0 || x === w - 1 || y === h - 1;
      if (edge || hm[i] < waterLevel) {
        filled[i] = hm[i];
        seen[i] = 1;
        heap.push(i, hm[i]);
      }
    }
  }
  const order: number[] = [];
  while (heap.size) {
    const c = heap.pop();
    order.push(c);
    const cx = c % w;
    const cy = (c - cx) / w;
    for (const [dx, dy] of DIRS4) {
      const nx = cx + dx;
      const ny = cy + dy;
      if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
      const j = index(nx, ny, w);
      if (seen[j]) continue;
      seen[j] = 1;
      filled[j] = Math.max(hm[j], filled[c] + FILL_EPSILON);
      heap.push(j, filled[j]);
    }
  }

  // Steepest descent on the filled surface; every non-outlet has a lower neighbour.
  for (let i = 0; i < n; i++) {
    const x = i % w;
    const y = (i - x) / w;
    const outlet = x === 0 || y === 0 || x === w - 1 || y === h - 1 || hm[i] < waterLevel;
    if (outlet) continue;
    let best = -1;
    let bestH = filled[i];
    for (const [dx, dy] of DIRS4) {
      const j = index(x + dx, y + dy, w);
      if (filled[j] < bestH) {
        bestH = filled[j];
        best = j;
      }
    }
    down[i] = best;
  }

  // Accumulate from high to low (reverse of flood order).
  const flow = new Float32Array(n).fill(1);
  for (let k = order.length - 1; k >= 0; k--) {
    const i = order[k];
    if (down[i] >= 0) flow[down[i]] += flow[i];
  }
  return { down, flow };
}

export const rivers: Stage = {
  name: 'rivers',
  run(model, rng) {
    const { width: w, height: h, params: p } = model;
    const hm = Float32Array.from(model.heightmap);
    const { down, flow } = computeDrainage(model);
    const riverAt = new Int32Array(w * h).fill(-1); // river id per cell

    // Sources: high, dry cells away from the edge, tried in random order.
    const land: number[] = [];
    for (let i = 0; i < hm.length; i++) if (hm[i] >= model.waterLevel) land.push(i);
    const heights = land.map((i) => hm[i]).sort((a, b) => a - b);
    const highCut = heights[Math.floor(heights.length * 0.7)] ?? 1;
    const margin = 4;
    const candidates = land.filter((i) => {
      const x = i % w;
      const y = (i - x) / w;
      return hm[i] >= highCut && x >= margin && y >= margin && x < w - margin && y < h - margin;
    });
    rng.shuffle(candidates);

    const paths: { cells: number[]; joins: number }[] = [];
    for (let k = 0; k < candidates.length && k < MAX_SOURCE_TRIES && paths.length < p.riverCount; k++) {
      const source = candidates[k];
      if (nearRiver(source, riverAt, w, h, 12)) continue;
      const cells = [source];
      let joins = -1;
      let c = source;
      while (down[c] >= 0) {
        c = down[c];
        cells.push(c);
        if (riverAt[c] >= 0) {
          joins = riverAt[c];
          break;
        }
      }
      if (cells.length < p.riverMinLength) continue;
      const id = paths.length;
      for (const cell of cells) if (riverAt[cell] < 0) riverAt[cell] = id;
      paths.push({ cells, joins });
    }

    // Carve: heights along every river must never rise. Joins can lower a cell
    // that another river already passed through, so repeat until stable.
    for (let pass = 0; pass < 20; pass++) {
      let changed = false;
      for (const { cells } of paths) {
        for (let k = 1; k < cells.length; k++) {
          if (hm[cells[k]] > hm[cells[k - 1]]) {
            hm[cells[k]] = hm[cells[k - 1]];
            changed = true;
          }
        }
      }
      if (!changed) break;
    }

    const riverList: River[] = paths.map(({ cells }, id) => {
      const centers = cells.map((c) => ({ x: (c % w) + 0.5, y: Math.floor(c / w) + 0.5 }));
      const points = chaikin(centers, 2);
      const widthAt = points.map((pt) => {
        const cell = index(Math.floor(pt.x), Math.floor(pt.y), w);
        return Math.min(p.riverMaxWidth, MIN_WIDTH + WIDTH_PER_SQRT_FLOW * Math.sqrt(flow[cell]));
      });
      // Width can only grow downstream (a point near a bend may sample a smaller cell).
      for (let k = 1; k < widthAt.length; k++) widthAt[k] = Math.max(widthAt[k], widthAt[k - 1]);
      return { id, points, widthAt };
    });

    return { ...model, heightmap: hm, rivers: riverList, fields: { ...model.fields, flow } };
  },
  check(model) {
    const errors: string[] = [];
    const { width: w, height: h, heightmap: hm } = model;
    for (const river of model.rivers) {
      const pts = river.points;
      if (pts.length < 2) {
        errors.push(`river ${river.id} has fewer than 2 points`);
        continue;
      }
      // Never uphill: heights under successive points don't increase.
      let prev = Infinity;
      for (const pt of pts) {
        const v = hm[index(Math.floor(pt.x), Math.floor(pt.y), w)];
        if (v > prev + 1e-6) {
          errors.push(`river ${river.id} flows uphill near ${pt.x.toFixed(1)},${pt.y.toFixed(1)}`);
          break;
        }
        prev = v;
      }
      // Ends in water, at the edge, or on another river.
      const end = pts[pts.length - 1];
      const ex = Math.floor(end.x);
      const ey = Math.floor(end.y);
      const inWater = hm[index(ex, ey, w)] < model.waterLevel;
      const atEdge = ex === 0 || ey === 0 || ex === w - 1 || ey === h - 1;
      const onRiver = model.rivers.some((o) => o.id !== river.id && distToPolyline(end, o.points) < 0.75);
      if (!inWater && !atEdge && !onRiver) errors.push(`river ${river.id} ends on dry land at ${ex},${ey}`);
      if (selfIntersects(pts)) errors.push(`river ${river.id} crosses itself`);
      for (let k = 1; k < river.widthAt.length; k++) {
        if (river.widthAt[k] < river.widthAt[k - 1]) {
          errors.push(`river ${river.id} narrows downstream`);
          break;
        }
      }
    }
    return errors;
  },
};

function nearRiver(cell: number, riverAt: Int32Array, w: number, h: number, r: number): boolean {
  const x = cell % w;
  const y = (cell - x) / w;
  for (let dy = -r; dy <= r; dy++) {
    for (let dx = -r; dx <= r; dx++) {
      const nx = x + dx;
      const ny = y + dy;
      if (nx >= 0 && ny >= 0 && nx < w && ny < h && riverAt[index(nx, ny, w)] >= 0) return true;
    }
  }
  return false;
}

function selfIntersects(pts: readonly Vec2[]): boolean {
  for (let i = 0; i + 1 < pts.length; i++) {
    for (let j = i + 2; j + 1 < pts.length; j++) {
      if (segmentsCross(pts[i], pts[i + 1], pts[j], pts[j + 1])) return true;
    }
  }
  return false;
}

/** Strict crossing (shared endpoints don't count). */
function segmentsCross(a: Vec2, b: Vec2, c: Vec2, d: Vec2): boolean {
  const o = (p: Vec2, q: Vec2, r: Vec2) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
  const d1 = o(c, d, a);
  const d2 = o(c, d, b);
  const d3 = o(a, b, c);
  const d4 = o(a, b, d);
  return d1 * d2 < -1e-12 && d3 * d4 < -1e-12;
}

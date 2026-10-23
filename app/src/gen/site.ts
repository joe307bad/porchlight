// Stage 3: pick the town center and the gates where main roads leave the map.
// The center scores well when the ground is flat, dry, near (not on) a river,
// and not too far from the middle of the map. Gates are spread around the map
// edge on dry tiles a road can actually reach from the center.

import { DIRS8, index } from '../core/grid';
import type { Vec2 } from '../model/types';
import { roadTerrain } from './costs';
import { distanceFrom, riverMask } from './raster';
import type { Stage } from './stage';

const FLAT_RADIUS = 6;
const IDEAL_RIVER_DISTANCE = 6;

/** Summed-area table for fast box means. */
function boxMean(field: ArrayLike<number>, w: number, h: number, r: number): Float32Array {
  const sat = new Float64Array((w + 1) * (h + 1));
  for (let y = 0; y < h; y++) {
    let row = 0;
    for (let x = 0; x < w; x++) {
      row += field[y * w + x];
      sat[(y + 1) * (w + 1) + x + 1] = sat[y * (w + 1) + x + 1] + row;
    }
  }
  const out = new Float32Array(w * h);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const x0 = Math.max(0, x - r);
      const y0 = Math.max(0, y - r);
      const x1 = Math.min(w, x + r + 1);
      const y1 = Math.min(h, y + r + 1);
      const sum =
        sat[y1 * (w + 1) + x1] - sat[y0 * (w + 1) + x1] - sat[y1 * (w + 1) + x0] + sat[y0 * (w + 1) + x0];
      out[y * w + x] = sum / ((x1 - x0) * (y1 - y0));
    }
  }
  return out;
}

export const site: Stage = {
  name: 'site',
  run(model, rng) {
    const { width: w, height: h, params: p } = model;
    const { water, slope, cost } = roadTerrain(model);
    const townRadius = p.townRadius * Math.min(w, h);

    const flat = boxMean(slope, w, h, FLAT_RADIUS);
    const wet = boxMean(water, w, h, Math.round(townRadius / 2));
    const rivers = riverMask(model);
    const riverDist = model.rivers.length ? distanceFrom(rivers, w, h) : null;
    const margin = Math.min(townRadius * 0.6, Math.min(w, h) / 4);
    const half = Math.min(w, h) / 2;

    let best = -1;
    let bestScore = -Infinity;
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const i = index(x, y, w);
        const jitter = rng.next() * 0.15; // drawn for every cell so the stream stays aligned
        if (water[i] || !Number.isFinite(cost[i])) continue;
        if (x < margin || y < margin || x >= w - margin || y >= h - margin) continue;
        let score = -flat[i] / p.maxRoadSlope - 2 * wet[i] - (2 * Math.hypot(x + 0.5 - w / 2, y + 0.5 - h / 2)) / half;
        if (riverDist) score -= Math.min(1, Math.abs(riverDist[i] - IDEAL_RIVER_DISTANCE) / 12);
        score += jitter;
        if (score > bestScore) {
          bestScore = score;
          best = i;
        }
      }
    }
    if (best < 0) best = index(Math.floor(w / 2), Math.floor(h / 2), w);
    const center: Vec2 = { x: (best % w) + 0.5, y: Math.floor(best / w) + 0.5 };

    // Edge cells a road can reach from the center.
    const reach = new Uint8Array(w * h);
    const queue = [best];
    reach[best] = 1;
    for (let head = 0; head < queue.length; head++) {
      const c = queue[head];
      const cx = c % w;
      const cy = (c - cx) / w;
      for (const [dx, dy] of DIRS8) {
        const nx = cx + dx;
        const ny = cy + dy;
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        const j = index(nx, ny, w);
        if (!reach[j] && Number.isFinite(cost[j])) {
          reach[j] = 1;
          queue.push(j);
        }
      }
    }
    const edgeCells: number[] = [];
    for (let x = 0; x < w; x++) edgeCells.push(index(x, 0, w), index(x, h - 1, w));
    for (let y = 1; y < h - 1; y++) edgeCells.push(index(0, y, w), index(w - 1, y, w));
    const open = edgeCells.filter((c) => reach[c] && !water[c]);

    const gates: Vec2[] = [];
    const minSpacing = (2 * (w + h)) / (p.gateCount * 2.5);
    const base = rng.range(0, Math.PI * 2);
    for (let g = 0; g < p.gateCount && open.length; g++) {
      const angle = base + (g * Math.PI * 2) / p.gateCount + rng.range(-0.3, 0.3);
      const ideal = rayToEdge(center, angle, w, h);
      let pick = -1;
      let pickD = Infinity;
      for (const c of open) {
        const pt = { x: (c % w) + 0.5, y: Math.floor(c / w) + 0.5 };
        if (gates.some((q) => Math.hypot(q.x - pt.x, q.y - pt.y) < minSpacing)) continue;
        const d = Math.hypot(pt.x - ideal.x, pt.y - ideal.y);
        if (d < pickD) {
          pickD = d;
          pick = c;
        }
      }
      if (pick >= 0) gates.push({ x: (pick % w) + 0.5, y: Math.floor(pick / w) + 0.5 });
    }

    return { ...model, center, townRadius, gates, fields: { ...model.fields, roadCost: cost } };
  },
  check(model) {
    const errors: string[] = [];
    const { width: w, height: h } = model;
    const cx = Math.floor(model.center.x);
    const cy = Math.floor(model.center.y);
    if (model.heightmap[index(cx, cy, w)] < model.waterLevel) errors.push('town center is in water');
    // Fewer gates than asked for is fine when the terrain only lets roads reach part of the edge.
    if (model.gates.length < 1) errors.push('no gate could be placed: no map edge is reachable by road');
    for (const g of model.gates) {
      const gx = Math.floor(g.x);
      const gy = Math.floor(g.y);
      if (gx !== 0 && gy !== 0 && gx !== w - 1 && gy !== h - 1) errors.push(`gate ${gx},${gy} is not on the map edge`);
    }
    return errors;
  },
};

function rayToEdge(from: Vec2, angle: number, w: number, h: number): Vec2 {
  const dx = Math.cos(angle);
  const dy = Math.sin(angle);
  let t = Infinity;
  if (dx > 1e-9) t = Math.min(t, (w - from.x) / dx);
  if (dx < -1e-9) t = Math.min(t, -from.x / dx);
  if (dy > 1e-9) t = Math.min(t, (h - from.y) / dy);
  if (dy < -1e-9) t = Math.min(t, -from.y / dy);
  return { x: from.x + dx * t, y: from.y + dy * t };
}

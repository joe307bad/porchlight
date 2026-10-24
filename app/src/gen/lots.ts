// Stage 7a: lots. Each block is split recursively across its longer side until
// the pieces are no bigger than lotMaxSize. Pieces smaller than lotMinSize, or
// with no road along any side, are dropped. The lots nearest the center become
// the plaza, with a well in the middle.

import { polygonArea, polygonCentroid } from '../core/geometry';
import type { Landmark, Lot, Vec2 } from '../model/types';
import type { Rng } from '../core/rng';
import { adjacentRoadEdges, components, frontageCounts, ground, outline, tilesIn, type Ground } from './parcels';
import type { Stage } from './stage';

const SPLIT_JITTER = 0.15;
const PLAZA_LOTS = 3;
const PLAZA_REACH = 8;

function split(tiles: number[], w: number, maxSize: number, rng: Rng, out: number[][]): void {
  if (tiles.length <= maxSize) {
    out.push(tiles);
    return;
  }
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const t of tiles) {
    const x = t % w;
    const y = (t - x) / w;
    x0 = Math.min(x0, x);
    y0 = Math.min(y0, y);
    x1 = Math.max(x1, x);
    y1 = Math.max(y1, y);
  }
  const alongX = x1 - x0 >= y1 - y0;
  const lo = alongX ? x0 : y0;
  const hi = alongX ? x1 : y1;
  if (hi === lo) {
    out.push(tiles);
    return;
  }
  const cut = Math.max(lo + 1, Math.min(hi, Math.round(lo + (hi - lo + 1) * (0.5 + rng.range(-SPLIT_JITTER, SPLIT_JITTER)))));
  const a: number[] = [];
  const b: number[] = [];
  for (const t of tiles) {
    const x = t % w;
    const v = alongX ? x : (t - x) / w;
    (v < cut ? a : b).push(t);
  }
  for (const part of [a, b]) for (const comp of components(part, w)) split(comp, w, maxSize, rng, out);
}

export const lots: Stage = {
  name: 'lots',
  run(model, rng) {
    const { width: w, height: h, params: p } = model;
    const g = ground(model);
    const list: Lot[] = [];
    for (const block of model.blocks) {
      const tiles = tilesIn(block.polygon, block.holes, w, h).filter((t) => g.free[t]);
      const pieces: number[][] = [];
      for (const comp of components(tiles, w)) split(comp, w, p.lotMaxSize, rng, pieces);
      for (const piece of pieces) {
        if (piece.length < p.lotMinSize) continue;
        const counts = frontageCounts(piece, g, w, h);
        if (!counts.some((c) => c > 0)) continue;
        const rings = outline(piece, w);
        if (rings.length !== 1) continue; // a lot wrapped around another lot would overlap it
        list.push({ id: list.length, blockId: block.id, polygon: rings[0], frontageEdgeId: frontageEdge(piece, g, w, h) });
      }
    }

    const landmarks: Landmark[] = [];
    if (list.length) {
      const centroid = (l: Lot) => polygonCentroid(l.polygon);
      const byDistance = list.slice().sort((a, b) => dist2(centroid(a), model.center) - dist2(centroid(b), model.center));
      const first = byDistance[0];
      const plaza = [first, ...byDistance.slice(1).filter((l) => l.blockId === first.blockId && Math.sqrt(dist2(centroid(l), centroid(first))) < PLAZA_REACH)].slice(0, PLAZA_LOTS);
      let ax = 0, ay = 0, area = 0;
      for (const l of plaza) {
        const a = Math.abs(polygonArea(l.polygon));
        const c = centroid(l);
        ax += c.x * a;
        ay += c.y * a;
        area += a;
      }
      const at = { x: ax / area, y: ay / area };
      landmarks.push({ id: 0, kind: 'plaza', at, lotIds: plaza.map((l) => l.id) });
      // The well goes on the plaza tile closest to the plaza's middle.
      const plazaTiles = plaza.flatMap((l) => tilesIn(l.polygon, [], w, h));
      let well = plazaTiles[0];
      for (const t of plazaTiles) {
        if (dist2(tileCenter(t, w), at) < dist2(tileCenter(well, w), at)) well = t;
      }
      landmarks.push({ id: 1, kind: 'well', at: tileCenter(well, w), lotIds: [] });
    }
    return { ...model, lots: list, landmarks };
  },
  check(model) {
    const { width: w, height: h } = model;
    const g = ground(model);
    for (const lot of model.lots) {
      const tiles = tilesIn(lot.polygon, [], w, h);
      if (!tiles.length) return [`lot ${lot.id} covers no tiles`];
      if (tiles.some((t) => !g.free[t])) return [`lot ${lot.id} covers road, water or rock`];
      const touching = adjacentRoadEdges(tiles, g, w, h);
      if (!touching.size) return [`lot ${lot.id} doesn't touch a road`];
      if (!touching.has(lot.frontageEdgeId)) return [`lot ${lot.id}'s frontage road ${lot.frontageEdgeId} isn't beside it`];
    }
    return [];
  },
};

/** The road edge most of the lot's road-side tiles look onto. */
function frontageEdge(tiles: readonly number[], g: Ground, w: number, h: number): number {
  let best = -1;
  let bestVotes = 0;
  for (const [edge, v] of adjacentRoadEdges(tiles, g, w, h)) {
    if (v > bestVotes || (v === bestVotes && edge < best)) {
      best = edge;
      bestVotes = v;
    }
  }
  return best;
}

const tileCenter = (t: number, w: number): Vec2 => ({ x: (t % w) + 0.5, y: Math.floor(t / w) + 0.5 });
const dist2 = (a: Vec2, b: Vec2) => (a.x - b.x) ** 2 + (a.y - b.y) ** 2;

// Stage 7b: buildings. Each lot (except the plaza) may get one rectangular
// building on whole tiles: the largest rectangle inside the lot whose whole
// front edge meets the road, set back a little from the rear and sides when
// there's room (never from the front). The building faces the road it fronts. The biggest lot near the center
// gets the church, and the building nearest the plaza becomes the hall.

import { DIRS4, index } from '../core/grid';
import { dist, pointInPolygon, polygonArea, polygonCentroid, polylineDistance } from '../core/geometry';
import type { Rng } from '../core/rng';
import type { Building, BuildingKind, Landmark, TownModel, Vec2 } from '../model/types';
import { FACINGS, frontageCounts, ground, isStreetTile, tilesIn, type Ground } from './parcels';
import { roadDrawWidth } from './raster';
import type { Stage } from './stage';

const INSET = 0.15;
const CHURCH_REACH = 0.45; // of the town radius

interface Rect {
  x0: number;
  y0: number;
  x1: number; // inclusive
  y1: number; // inclusive
}

export const buildings: Stage = {
  name: 'buildings',
  run(model, rng) {
    const { width: w, height: h, params: p, center: C, townRadius: R } = model;
    const g = ground(model);
    const plaza = new Set(model.landmarks.filter((l) => l.kind === 'plaza').flatMap((l) => l.lotIds));

    let church = -1;
    let churchArea = 0;
    for (const lot of model.lots) {
      if (plaza.has(lot.id) || dist(polygonCentroid(lot.polygon), C) > R * CHURCH_REACH) continue;
      const a = Math.abs(polygonArea(lot.polygon));
      if (a > churchArea) {
        churchArea = a;
        church = lot.id;
      }
    }

    const list: Building[] = [];
    const landmarks: Landmark[] = model.landmarks.slice();
    for (const lot of model.lots) {
      if (plaza.has(lot.id)) continue;
      const isChurch = lot.id === church;
      const d = Math.min(1, dist(polygonCentroid(lot.polygon), C) / R);
      if (!isChurch && !rng.chance(p.buildingDensity * (0.7 + 0.3 * (1 - d)))) continue;
      const tiles = tilesIn(lot.polygon, [], w, h);
      const counts = frontageCounts(tiles, g, w, h);
      const facingIndex = counts.indexOf(Math.max(...counts));
      const maxSide = p.maxBuildingSize + (isChurch ? 2 : 0);
      const rect = bestRect(tiles, facingIndex, maxSide, g, w, h);
      if (!rect) continue;
      setBack(rect, facingIndex, g, w, h, rng);

      const kind: BuildingKind = isChurch ? 'church' : pickKind(d, rng);
      const footprint = [
        { x: rect.x0 + INSET, y: rect.y0 + INSET },
        { x: rect.x1 + 1 - INSET, y: rect.y0 + INSET },
        { x: rect.x1 + 1 - INSET, y: rect.y1 + 1 - INSET },
        { x: rect.x0 + INSET, y: rect.y1 + 1 - INSET },
      ];
      const covered: Vec2[] = [];
      for (let y = rect.y0; y <= rect.y1; y++) for (let x = rect.x0; x <= rect.x1; x++) covered.push({ x, y });
      const building: Building = {
        id: list.length,
        lotId: lot.id,
        footprint,
        tiles: covered,
        kind,
        floors: floorsFor(kind, d, rng),
        facing: FACINGS[facingIndex],
      };
      list.push(building);
      if (isChurch) {
        landmarks.push({ id: landmarks.length, kind: 'church', at: polygonCentroid(footprint), lotIds: [lot.id] });
      }
    }

    // The hall: the ordinary building closest to the plaza.
    const plazaAt = model.landmarks.find((l) => l.kind === 'plaza')?.at;
    if (plazaAt) {
      let hall: Building | null = null;
      for (const b of list) {
        if (b.kind === 'church' || b.tiles.length < 9) continue;
        if (!hall || dist(polygonCentroid(b.footprint), plazaAt) < dist(polygonCentroid(hall.footprint), plazaAt)) hall = b;
      }
      if (hall) {
        hall.kind = 'hall';
        hall.floors = 2;
      }
    }
    return { ...model, buildings: list, landmarks };
  },
  check(model) {
    return checkBuildings(model, ground(model));
  },
};

export function checkBuildings(model: TownModel, g: Ground): string[] {
  const { width: w, height: h, params } = model;
  const owner = new Int32Array(w * h).fill(-1);
  for (const b of model.buildings) {
    if (!b.tiles.length) return [`building ${b.id} has no tiles`];
    for (const t of b.tiles) {
      const i = index(t.x, t.y, w);
      if (owner[i] >= 0) return [`buildings ${owner[i]} and ${b.id} overlap`];
      owner[i] = b.id;
      if (g.water[i]) return [`building ${b.id} stands in water`];
      if (g.road.kind[i]) return [`building ${b.id} stands on a road`];
      if (!pointInPolygon({ x: t.x + 0.5, y: t.y + 0.5 }, b.footprint)) return [`building ${b.id}'s tiles don't match its footprint`];
    }
    for (const e of model.roads.edges) {
      const clearance = roadDrawWidth(e.kind, params) / 2;
      if (polylineDistance([...b.footprint, b.footprint[0]], e.points) < clearance - 1e-6) {
        return [`building ${b.id} overlaps road ${e.id}`];
      }
    }
    // Facing: every tile along the front edge meets a street (road, path or avenue).
    const d = FACINGS.indexOf(b.facing);
    const [dx, dy] = DIRS4[d];
    const own = new Set(b.tiles.map((t) => index(t.x, t.y, w)));
    for (const t of b.tiles) {
      const nx = t.x + dx;
      const ny = t.y + dy;
      const inside = nx >= 0 && ny >= 0 && nx < w && ny < h;
      if (inside && own.has(index(nx, ny, w))) continue; // not on the front row
      if (!inside || !isStreetTile(g, index(nx, ny, w))) {
        return [`building ${b.id}'s front at ${t.x},${t.y} doesn't meet the road it faces (${b.facing})`];
      }
    }
  }
  return [];
}

/** Largest rectangle of lot tiles, at most maxSide per side, whose whole front row borders a street. */
function bestRect(tiles: readonly number[], facing: number, maxSide: number, g: Ground, w: number, h: number): Rect | null {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const t of tiles) {
    const x = t % w;
    const y = (t - x) / w;
    x0 = Math.min(x0, x);
    y0 = Math.min(y0, y);
    x1 = Math.max(x1, x);
    y1 = Math.max(y1, y);
  }
  const bw = x1 - x0 + 1;
  const bh = y1 - y0 + 1;
  const inLot = new Uint8Array(bw * bh);
  const front = new Uint8Array(bw * bh);
  const [fx, fy] = DIRS4[facing];
  for (const t of tiles) {
    const x = t % w;
    const y = (t - x) / w;
    const li = (y - y0) * bw + (x - x0);
    inLot[li] = 1;
    const nx = x + fx;
    const ny = y + fy;
    if (nx >= 0 && ny >= 0 && nx < w && ny < h && isStreetTile(g, index(nx, ny, w))) front[li] = 1;
  }
  // Summed-area table of lot membership.
  const sat = new Int32Array((bw + 1) * (bh + 1));
  for (let y = 0; y < bh; y++) {
    for (let x = 0; x < bw; x++) {
      sat[(y + 1) * (bw + 1) + x + 1] = inLot[y * bw + x] + sat[y * (bw + 1) + x + 1] + sat[(y + 1) * (bw + 1) + x] - sat[y * (bw + 1) + x];
    }
  }
  const filled = (ax: number, ay: number, bx: number, by: number) =>
    sat[(by + 1) * (bw + 1) + bx + 1] - sat[ay * (bw + 1) + bx + 1] - sat[(by + 1) * (bw + 1) + ax] + sat[ay * (bw + 1) + ax] ===
    (bx - ax + 1) * (by - ay + 1);
  // The whole front row must sit against the street, so the building meets the road along its full width.
  const hasFront = (ax: number, ay: number, bx: number, by: number) => {
    if (facing === 0) for (let x = ax; x <= bx; x++) { if (!front[ay * bw + x]) return false; }
    if (facing === 2) for (let x = ax; x <= bx; x++) { if (!front[by * bw + x]) return false; }
    if (facing === 1) for (let y = ay; y <= by; y++) { if (!front[y * bw + bx]) return false; }
    if (facing === 3) for (let y = ay; y <= by; y++) { if (!front[y * bw + ax]) return false; }
    return true;
  };
  let best: Rect | null = null;
  let bestArea = 0;
  for (let ay = 0; ay < bh; ay++) {
    for (let ax = 0; ax < bw; ax++) {
      if (!inLot[ay * bw + ax]) continue;
      for (let by = ay; by < Math.min(bh, ay + maxSide); by++) {
        if (!filled(ax, ay, ax, by)) break;
        for (let bx = ax; bx < Math.min(bw, ax + maxSide); bx++) {
          if (!filled(ax, ay, bx, by)) break;
          const area = (bx - ax + 1) * (by - ay + 1);
          // At least two tiles; a narrow house one tile wide is fine where the street steps diagonally.
          if (area < 2) continue;
          if (area > bestArea && hasFront(ax, ay, bx, by)) {
            bestArea = area;
            best = { x0: ax + x0, y0: ay + y0, x1: bx + x0, y1: by + y0 };
          }
        }
      }
    }
  }
  return best;
}

/** Leave a yard at the back and sometimes a gap at one side, keeping the front on the road. */
function setBack(rect: Rect, facing: number, g: Ground, w: number, h: number, rng: Rng): void {
  const depthAlongY = facing === 0 || facing === 2;
  const depth = depthAlongY ? rect.y1 - rect.y0 + 1 : rect.x1 - rect.x0 + 1;
  const width = depthAlongY ? rect.x1 - rect.x0 + 1 : rect.y1 - rect.y0 + 1;
  if (depth >= 4 && rng.chance(0.7)) {
    if (facing === 0) rect.y1--;
    else if (facing === 2) rect.y0++;
    else if (facing === 1) rect.x0++;
    else rect.x1--;
  }
  if (width >= 5 && rng.chance(0.5)) {
    const trial = { ...rect };
    const low = rng.chance(0.5);
    if (depthAlongY) {
      if (low) trial.x0++;
      else trial.x1--;
    } else if (low) trial.y0++;
    else trial.y1--;
    if (frontsStreet(trial, facing, g, w, h)) Object.assign(rect, trial);
  }
}

function frontsStreet(r: Rect, facing: number, g: Ground, w: number, h: number): boolean {
  const [dx, dy] = DIRS4[facing];
  for (let y = r.y0; y <= r.y1; y++) {
    for (let x = r.x0; x <= r.x1; x++) {
      const nx = x + dx;
      const ny = y + dy;
      if (nx >= 0 && ny >= 0 && nx < w && ny < h && isStreetTile(g, index(nx, ny, w))) return true;
    }
  }
  return false;
}

function pickKind(d: number, rng: Rng): BuildingKind {
  if (d < 0.35 && rng.chance(0.6)) return 'shop';
  if (d > 0.7 && rng.chance(0.25)) return 'workshop';
  return 'house';
}

function floorsFor(kind: BuildingKind, d: number, rng: Rng): number {
  switch (kind) {
    case 'shop':
      return rng.int(2, 3);
    case 'church':
    case 'hall':
      return 2;
    case 'workshop':
      return 1;
    default:
      return d < 0.5 ? rng.int(1, 2) : 1;
  }
}


// Turns the blueprint into a street scene for the iso view. Pure (no Pixi, no
// DOM), so it's unit-tested in Node.
//
// - Roads: each road edge's centreline, smoothed with Chaikin so bends curve,
//   drawn as vectors (asphalt plus a sidewalk either side) instead of tiles.
// - Blocks, lot by lot: each blueprint building keeps its exact footprint
//   and faces the street its lot fronts; the rest of the lot is its back yard
//   (fenced, often with a shed and a vegetable garden). Empty lots among houses
//   become community gardens, others stay green. Nothing leaves its lot, so
//   blocks tile together cleanly.
// - Sidewalks: offset lines either side of each road, joined where they meet at
//   junctions, for townsfolk to stroll along. Parked cars line the kerbs.

import { DIRS4, index, inBounds } from '../../core/grid';
import { arcPosition, chaikin, dist, samplePolyline, subPolyline } from '../../core/geometry';
import { tilesIn } from '../../gen/parcels';
import type { BuildingKind, Facing, RoadKind, TownModel, Vec2 } from '../../model/types';
import { Overlay, Terrain } from '../../model/types';

/** Width of the sidewalk along each side of a road (tiles). */
export const SIDEWALK = 0.45;
const SMOOTHING_PASSES = 2;
/** Roads round their corners within this distance of each bend (tiles), so long straight runs stay put. */
const CORNER_RADIUS = 0.9;
const FACINGS: readonly Facing[] = ['N', 'E', 'S', 'W']; // DIRS4 order

export type HouseStyle = 'house' | 'workshop' | 'shop' | 'office' | 'hall' | 'church';

export interface House {
  /** First footprint tile. */
  x: number;
  y: number;
  /** The blueprint building's whole footprint. */
  tiles: { x: number; y: number }[];
  /** The side the entrance is on: always towards the street the house stands on. */
  facing: Facing;
  kind: BuildingKind;
  style: HouseStyle;
  floors: number;
  /** Blueprint building this house stands for (for a shared look within a block). */
  buildingId: number;
  blockId: number;
}

/** A fence along one side of a tile. */
export interface Fence {
  x: number;
  y: number;
  side: Facing;
}

export interface ParkedCar {
  x: number;
  y: number;
  /** Direction along the road, radians in world space. */
  angle: number;
  /** Stable pick for model and colour. */
  variant: number;
}

export interface RoadStroke {
  kind: RoadKind;
  /** Road graph nodes at the two ends (points[0] is at `from`). */
  from: number;
  to: number;
  /** Smoothed centreline, endpoints exactly on the graph nodes. */
  points: Vec2[];
  /** Asphalt width (tiles). */
  width: number;
}

export interface SidewalkPath {
  points: Vec2[];
  /** Which side of its road (+1 left, -1 right of the road's direction); the outward normal's sign. */
  side: 1 | -1;
  /** Index of the road (in IsoLayout.roads) this sidewalk runs beside. */
  road: number;
  /** Cumulative length at each point. */
  at: number[];
}

export interface SidewalkNetwork {
  paths: SidewalkPath[];
  /**
   * For each path end (path * 2 + 0 for the start, + 1 for the end), the other
   * path ends close enough to walk on to.
   */
  links: number[][];
}

/** What each tile of a block is used for. */
export const Use = {
  NONE: 0,
  HOUSE: 1,
  YARD: 2,
  GARDEN: 3,
  SHED: 4,
  COMMUNITY_GARDEN: 5,
  GREEN: 6,
} as const;

export interface BlockPlan {
  blockId: number;
  /** Blueprint buildings in this block. */
  wanted: number;
  /** Buildings placed (each at its blueprint footprint). */
  placed: number;
}

export interface IsoLayout {
  houses: House[];
  /** Use.* per tile. */
  use: Uint8Array;
  fences: Fence[];
  blocks: BlockPlan[];
  cars: ParkedCar[];
  roads: RoadStroke[];
  /** Stretches of road over water, along the smoothed centreline. */
  bridges: { kind: RoadKind; points: Vec2[]; width: number }[];
  sidewalks: SidewalkNetwork;
}

/**
 * Add a point `r` either side of every bend, so Chaikin smoothing only rounds
 * the corner itself instead of shaving a quarter off each long straight run.
 */
function padCorners(pts: readonly Vec2[], r: number): Vec2[] {
  if (pts.length < 3) return pts.slice();
  const out: Vec2[] = [pts[0]];
  for (let i = 1; i < pts.length - 1; i++) {
    const prev = pts[i - 1];
    const p = pts[i];
    const next = pts[i + 1];
    const toPrev = dist(prev, p);
    const toNext = dist(p, next);
    if (toPrev > r * 2) out.push({ x: p.x + ((prev.x - p.x) * r) / toPrev, y: p.y + ((prev.y - p.y) * r) / toPrev });
    out.push(p);
    if (toNext > r * 2) out.push({ x: p.x + ((next.x - p.x) * r) / toNext, y: p.y + ((next.y - p.y) * r) / toNext });
  }
  out.push(pts[pts.length - 1]);
  return out;
}

export function roadWidth(kind: RoadKind, model: TownModel): number {
  return kind === 'main' ? model.params.mainRoadWidth : kind === 'street' ? model.params.streetWidth : 0.5;
}

export function buildLayout(model: TownModel): IsoLayout {
  const roads = model.roads.edges.map((e) => ({
    kind: e.kind,
    from: e.from,
    to: e.to,
    points: chaikin(padCorners(e.points, CORNER_RADIUS), SMOOTHING_PASSES),
    width: roadWidth(e.kind, model),
  }));
  const bridges = findBridges(model, roads);
  return {
    ...planBlocks(model),
    cars: parkCars(model, roads),
    roads,
    bridges,
    sidewalks: sidewalkNetwork(roads),
  };
}

/**
 * Block by block, lot by lot: every blueprint building is placed at its exact
 * footprint and position (so it's the same size as on the map), facing the
 * street its lot fronts. The rest of its lot becomes its back yard, fenced,
 * often with a shed and a vegetable garden. Lots without a building become a
 * community garden when they sit among built lots, or stay green. Nothing
 * crosses its lot, so blocks fit together cleanly.
 */
function planBlocks(model: TownModel): Pick<IsoLayout, 'houses' | 'use' | 'fences' | 'blocks'> {
  const { width: w, height: h, tiles } = model;
  const use = new Uint8Array(w * h);
  const houses: House[] = [];
  const fences: Fence[] = [];
  const plans: BlockPlan[] = [];
  const street = (x: number, y: number) =>
    inBounds(x, y, w, h) && (tiles.overlay[index(x, y, w)] === Overlay.ROAD_MAIN || tiles.overlay[index(x, y, w)] === Overlay.ROAD_STREET);
  const open = (t: number) => {
    const ter = tiles.terrain[t];
    return tiles.overlay[t] === Overlay.NONE && ter !== Terrain.WATER_SHALLOW && ter !== Terrain.WATER_DEEP;
  };
  const C = model.center;
  const buildingOnLot = new Map(model.buildings.map((b) => [b.lotId, b]));

  for (const block of model.blocks) {
    const lots = model.lots.filter((l) => l.blockId === block.id);
    let wanted = 0;
    let placed = 0;
    const builtLots = lots.filter((l) => buildingOnLot.has(l.id)).length;
    for (const lot of lots) {
      const lotTiles = tilesIn(lot.polygon, [], w, h).filter(open);
      const own = new Set(lotTiles);
      const b = buildingOnLot.get(lot.id);
      if (!b) {
        // An empty lot among houses becomes a fenced community garden; otherwise it stays green.
        const garden = builtLots >= 2 && hash(lot.id)(1) < 0.45;
        for (const t of lotTiles) use[t] = garden ? Use.COMMUNITY_GARDEN : Use.GREEN;
        if (garden) for (const t of lotTiles) fences.push(...fenceAround(t, own, w, h, street));
        continue;
      }
      wanted++;
      const footprint = b.tiles.filter((t) => own.has(index(t.x, t.y, w)) || open(index(t.x, t.y, w)));
      if (!footprint.length) continue;
      placed++;
      const centre = footprint.reduce((acc, t) => ({ x: acc.x + t.x + 0.5, y: acc.y + t.y + 0.5 }), { x: 0, y: 0 });
      const nearCentre = Math.hypot(centre.x / footprint.length - C.x, centre.y / footprint.length - C.y) < model.townRadius * 0.3;
      const style: HouseStyle = b.kind === 'shop' && nearCentre && b.floors >= 2 ? 'office' : b.kind;
      const floors = style === 'office' ? Math.min(5, b.floors + 2) : b.floors;
      const house: House = { x: footprint[0].x, y: footprint[0].y, tiles: footprint, facing: b.facing, kind: b.kind, style, floors, buildingId: b.id, blockId: block.id };
      houses.push(house);
      for (const t of footprint) use[index(t.x, t.y, w)] = Use.HOUSE;

      // The rest of the lot is this building's yard.
      const yard = lotTiles.filter((t) => use[t] === Use.NONE);
      for (const t of yard) use[t] = Use.YARD;
      const rnd = hash(b.id + 7919);
      if ((style === 'house' || style === 'workshop') && yard.length >= 3) {
        const far = yard.reduce((best, t) => (backDistance(t, house, w) > backDistance(best, house, w) ? t : best), yard[0]);
        if (rnd(1) < 0.65) use[far] = Use.SHED;
        if (style === 'house' && rnd(2) < 0.6) {
          for (const t of yard.filter((t2) => use[t2] === Use.YARD && t2 !== far).slice(0, 2)) use[t] = Use.GARDEN;
        }
      }
      if (style === 'house' || style === 'workshop') {
        const plot = new Set([...yard, ...footprint.map((t) => index(t.x, t.y, w))]);
        for (const t of yard) fences.push(...fenceAround(t, plot, w, h, street));
      }
    }
    // Block tiles outside any lot (corners the lots didn't cover) stay green.
    for (const t of tilesIn(block.polygon, block.holes, w, h)) if (open(t) && use[t] === Use.NONE) use[t] = Use.GREEN;
    plans.push({ blockId: block.id, wanted, placed });
  }
  return { houses, use, fences, blocks: plans };
}

/** How far a tile lies behind a building's front (deeper = further back in the yard). */
function backDistance(t: number, house: House, w: number): number {
  const [fx, fy] = DIRS4[FACINGS.indexOf(house.facing)];
  let best = Infinity;
  for (const p of house.tiles) best = Math.min(best, -((t % w) - p.x) * fx - (Math.floor(t / w) - p.y) * fy);
  return best;
}

/** Fences on the sides of tile t that face outside `own` (but not onto a street: those stay open). */
function fenceAround(t: number, own: Set<number>, w: number, h: number, street: (x: number, y: number) => boolean): Fence[] {
  const x = t % w;
  const y = (t - x) / w;
  const out: Fence[] = [];
  DIRS4.forEach(([dx, dy], k) => {
    const nx = x + dx;
    const ny = y + dy;
    if (own.has(index(nx, ny, w)) && inBounds(nx, ny, w, h)) return;
    if (street(nx, ny)) return;
    out.push({ x, y, side: FACINGS[k] });
  });
  return out;
}

/** Roads with an end no other road meets (cul-de-sacs and the map-edge stubs). */
export function deadEnds(roads: readonly RoadStroke[]): Set<number> {
  const degree = new Map<number, number>();
  for (const r of roads) for (const n of [r.from, r.to]) degree.set(n, (degree.get(n) ?? 0) + 1);
  const out = new Set<number>();
  roads.forEach((r, i) => {
    if (degree.get(r.from) === 1 || degree.get(r.to) === 1) out.add(i);
  });
  return out;
}

/** A small deterministic hash-based random in 0..1, keyed per block. */
function hash(seed: number): (k: number) => number {
  return (k: number) => {
    let x = Math.imul(seed + 0x9e3779b9, 0x85ebca6b) ^ Math.imul(k + 0x7f4a7c15, 0xc2b2ae35);
    x ^= x >>> 15;
    x = Math.imul(x, 0x2c1b3c6d);
    x ^= x >>> 12;
    return (x >>> 0) / 4294967296;
  };
}

/** Cars parked along the kerbs, clear of junctions and bridges. */
function parkCars(model: TownModel, roads: RoadStroke[]): ParkedCar[] {
  const cars: ParkedCar[] = [];
  const dead = deadEnds(roads);
  roads.forEach((r, n) => {
    // Quiet dead-end streets only: the through roads carry the moving traffic.
    if (r.kind !== 'street' || !dead.has(n)) return;
    const rnd = hash(n + 1000);
    const total = r.points.reduce((s, p, k) => (k ? s + dist(p, r.points[k - 1]) : 0), 0);
    let k = 0;
    for (let s = 2.5; s < total - 2.5; s += 2.6) {
      if (rnd(k++) > 0.3) continue;
      const pts = subPolyline(r.points, s, s + 0.2);
      if (pts.length < 2) continue;
      const [a, b] = [pts[0], pts[pts.length - 1]];
      const len = dist(a, b) || 1;
      const tx = (b.x - a.x) / len;
      const ty = (b.y - a.y) / len;
      const side = rnd(k++) < 0.5 ? 1 : -1;
      const off = r.width / 2 - 0.24;
      const p = { x: a.x + ty * off * side, y: a.y - tx * off * side };
      if (isWaterTile(model, p) || isWaterTile(model, a)) continue;
      cars.push({ x: p.x, y: p.y, angle: Math.atan2(ty, tx), variant: Math.floor(rnd(k++) * 1000) });
    }
  });
  return cars;
}

const isWaterTile = (model: TownModel, p: Vec2) => {
  const x = Math.floor(p.x);
  const y = Math.floor(p.y);
  if (!inBounds(x, y, model.width, model.height)) return false;
  const t = model.tiles.terrain[index(x, y, model.width)];
  return t === Terrain.WATER_SHALLOW || t === Terrain.WATER_DEEP;
};

/** Spans of each smoothed road that cross water, padded so the deck reaches the banks. */
function findBridges(model: TownModel, roads: RoadStroke[]): IsoLayout['bridges'] {
  const out: IsoLayout['bridges'] = [];
  for (const r of roads) {
    const samples = samplePolyline(r.points, 0.2);
    let start = -1;
    for (let k = 0; k <= samples.length; k++) {
      const wet = k < samples.length && isWaterTile(model, samples[k]);
      if (wet && start < 0) start = k;
      if (!wet && start >= 0) {
        const a = Math.max(0, arcPosition(samples[start], r.points) - 0.6);
        const b = arcPosition(samples[k - 1], r.points) + 0.6;
        const points = subPolyline(r.points, a, b);
        if (points.length >= 2) out.push({ kind: r.kind, points, width: r.width });
        start = -1;
      }
    }
  }
  return out;
}

/** A polyline shifted sideways by `d` (positive = left of travel direction in y-down space). */
export function offsetPolyline(pts: readonly Vec2[], d: number): Vec2[] {
  const out: Vec2[] = [];
  for (let i = 0; i < pts.length; i++) {
    const a = pts[Math.max(0, i - 1)];
    const b = pts[Math.min(pts.length - 1, i + 1)];
    let tx = b.x - a.x;
    let ty = b.y - a.y;
    const len = Math.hypot(tx, ty) || 1;
    tx /= len;
    ty /= len;
    out.push({ x: pts[i].x + ty * d, y: pts[i].y - tx * d });
  }
  return out;
}

function sidewalkNetwork(roads: RoadStroke[]): SidewalkNetwork {
  const paths: SidewalkPath[] = [];
  roads.forEach((r, road) => {
    if (r.kind === 'path') return;
    const d = r.width / 2 + SIDEWALK / 2;
    for (const side of [1, -1] as const) {
      const points = offsetPolyline(r.points, d * side);
      const at = [0];
      for (let k = 1; k < points.length; k++) at.push(at[k - 1] + dist(points[k], points[k - 1]));
      if (at[at.length - 1] > 0.5) paths.push({ points, at, side, road });
    }
  });
  // Join path ends that meet at a junction.
  const ends = paths.flatMap((p) => [p.points[0], p.points[p.points.length - 1]]);
  const reach = 2.6;
  const links = ends.map((e, i) => {
    const near: number[] = [];
    ends.forEach((o, j) => {
      if (j !== i && j >> 1 !== i >> 1 && dist(e, o) < reach) near.push(j);
    });
    return near;
  });
  return { paths, links };
}

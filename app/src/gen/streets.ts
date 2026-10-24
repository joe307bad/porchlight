// Stage 5: streets. Side streets run along the tile grid (so buildings, which
// sit on the grid, front them flush). They sprout at right angles from points
// along the main roads and grow in short straight segments (after Parish &
// Müller's local constraints): a segment that would cross a road stops there
// and joins it, one that ends just short of a node or road carries straight on
// to meet it, and one that
// hits water, steep ground, the town edge or crowds a parallel road is dropped.
// Branching thins out with distance from the center. Afterwards dead ends get
// short connectors where one fits, and land the roads never reach (across a
// river, say) gets a footpath back to the network.

import { findPath, octile } from '../core/astar';
import { DIRS4, cellsOnSegment, index } from '../core/grid';
import {
  add,
  closestOnPolyline,
  dist,
  distToPolyline,
  dot,
  lerp,
  polylineDistance,
  polylineLength,
  samplePolyline,
  scale,
  segmentIntersection,
  simplifyRDP,
  sub,
  subPolyline,
} from '../core/geometry';
import type { RoadGraph, TownModel, Vec2 } from '../model/types';
import { roadSegmentOk, roadTerrain } from './costs';
import {
  addEdge,
  addNode,
  builderFrom,
  checkBridges,
  compact,
  computeBridges,
  incidence,
  liveEdges,
  mergePassThrough,
  planarize,
  reachableNodes,
  splitEdgeAt,
  type GraphBuilder,
} from './roadGraph';
import { rockMask, roadRaster, waterMask } from './raster';
import { checkRoadSlopes } from './roads';
import type { Stage } from './stage';

const MIN_ANGLE = (35 * Math.PI) / 180;
/** Streets run along the tile grid; a join may stray this far from a grid direction. */
const GRID_TOLERANCE = (8 * Math.PI) / 180;
const MIN_SEGMENT = 1.5;
const MAX_PROPOSALS = 6000;
const MAX_TRIES = 3;
const JUNCTION_CLEARANCE = 3;
const HEADING_REACH = 2;
const PATH_COMPONENT_MIN = 150;
/** Two roads closer than this, away from junctions, count as duplicates. */
const DUPLICATE_DISTANCE = 1.5;

interface Proposal {
  from: number;
  angle: number;
  depth: number;
  /** Distance grown since the last side branch. */
  since: number;
  /** Failed attempts so far; a blocked proposal retries with a shorter segment. */
  tries: number;
}

export const streets: Stage = {
  name: 'streets',
  run(model, rng) {
    const { width: w, height: h, params: p, center: C, townRadius: R } = model;
    const terrainInfo = roadTerrain(model);
    const { water, slope, cost } = terrainInfo;
    const g = builderFrom(model.roads);
    const falloff = (pt: Vec2) => Math.max(0, 1 - dist(pt, C) / R);
    const keep = new Set<number>(
      g.nodes.filter((n) => [C, ...model.gates].some((q) => dist(q, n.at) < 0.01)).map((n) => n.id),
    );

    const tileOk = (x: number, y: number) => {
      if (x < 1 || y < 1 || x >= w - 1 || y >= h - 1) return false;
      const i = index(x, y, w);
      return !water[i] && slope[i] <= p.maxRoadSlope;
    };
    const cellOk = (pt: Vec2) => tileOk(Math.floor(pt.x), Math.floor(pt.y));
    /** Dry, gentle ground under every tile the segment touches. */
    const landOk = (a: Vec2, b: Vec2) => cellsOnSegment(a.x, a.y, b.x, b.y).every(([x, y]) => tileOk(x, y));

    const incidentDirs = (node: number): Vec2[] => {
      const at = g.nodes[node].at;
      const out: Vec2[] = [];
      for (const e of liveEdges(g)) {
        if (e.from === node) out.push(heading(at, e.points));
        if (e.to === node) out.push(heading(at, e.points.slice().reverse()));
      }
      return out;
    };
    const angleClash = (node: number, dir: Vec2) => incidentDirs(node).some((d) => Math.acos(clamp(dot(d, dir), -1, 1)) < MIN_ANGLE);
    const connected = (a: number, b: number) =>
      liveEdges(g).some((e) => (e.from === a && e.to === b) || (e.from === b && e.to === a));

    /** First crossing of segment ab with a live edge, ignoring edges at the given nodes. */
    const firstHit = (a: Vec2, b: Vec2, skipNodes: number[], skipEdge = -1) => {
      let best: { edge: number; point: Vec2; t: number } | null = null;
      for (const e of liveEdges(g)) {
        if (e.id === skipEdge || skipNodes.includes(e.from) || skipNodes.includes(e.to)) continue;
        for (let s = 0; s + 1 < e.points.length; s++) {
          const x = segmentIntersection(a, b, e.points[s], e.points[s + 1]);
          if (x && x.t > 1e-6 && (!best || x.t < best.t)) best = { edge: e.id, point: x.point, t: x.t };
        }
      }
      return best;
    };
    /** A new street a→b must not graze a road it doesn't join (a near miss reads as a duplicate road). */
    const grazes = (a: Vec2, b: Vec2, ends: number[]) =>
      liveEdges(g).some(
        (e) => !ends.includes(e.from) && !ends.includes(e.to) && polylineDistance([a, b], e.points) < DUPLICATE_DISTANCE,
      );
    const clearOfOthers = (pts: Vec2[], skipNodes: number[], minDist: number) =>
      liveEdges(g).every(
        (e) => skipNodes.includes(e.from) || skipNodes.includes(e.to) || pts.every((pt) => distToPolyline(pt, e.points) >= minDist),
      );

    /**
     * A straight street from `from` to node `to` is allowed: distinct, not already
     * linked, meeting both nodes at a decent angle, and (since a split may snap to
     * a nearby endpoint) still dry, gentle and uncrossed along the final line.
     */
    const joinable = (from: number, to: number) => {
      if (from === to || connected(from, to)) return false;
      const a = g.nodes[from].at;
      const b = g.nodes[to].at;
      if (dist(a, b) < MIN_SEGMENT || !landOk(a, b) || firstHit(a, b, [from, to]) || grazes(a, b, [from, to])) return false;
      const d = unit(sub(b, a));
      return onGrid(d) && !angleClash(from, d) && !angleClash(to, scale(d, -1));
    };

    /** Try to grow one segment; returns the node it ended at and whether growth stops. */
    const grow = (prop: Proposal): { node: number; stop: boolean; len: number } | null => {
      const a = g.nodes[prop.from].at;
      const dir = { x: Math.cos(prop.angle), y: Math.sin(prop.angle) };
      if (angleClash(prop.from, dir)) return null;
      const len = p.streetSegment * rng.range(0.85, 1.15) * (1 - 0.25 * prop.tries);
      const b = add(a, scale(dir, len));
      const snap = len * 0.5;

      const hit = firstHit(a, b, [prop.from]);
      if (hit) {
        if (hit.t * len < MIN_SEGMENT || !landOk(a, hit.point)) return null;
        const n = splitEdgeAt(g, hit.edge, hit.point, 0.3);
        if (!joinable(prop.from, n)) return null;
        addEdge(g, prop.from, n, 'street');
        return { node: n, stop: true, len: hit.t * len };
      }
      if (dist(b, C) > R) return null;

      const neighbours = liveEdges(g)
        .filter((e) => e.from === prop.from || e.to === prop.from)
        .map((e) => (e.from === prop.from ? e.to : e.from));
      let near = -1;
      let nearD = snap;
      for (const n of g.nodes) {
        if (n.id === prop.from || neighbours.includes(n.id)) continue;
        const d = dist(n.at, b);
        if (d < nearD && incidentDirs(n.id).length > 0 && onGrid(unit(sub(n.at, a)))) {
          nearD = d;
          near = n.id;
        }
      }
      if (near >= 0) {
        const target = g.nodes[near].at;
        const d = unit(sub(target, a));
        if (dist(a, target) < MIN_SEGMENT || angleClash(prop.from, d) || angleClash(near, scale(d, -1))) return null;
        if (firstHit(a, target, [prop.from, near]) || !landOk(a, target)) return null;
        addEdge(g, prop.from, near, 'street');
        return { node: near, stop: true, len: dist(a, target) };
      }

      // A road just beyond the segment's end: carry straight on to meet it.
      const ahead = firstHit(a, add(a, scale(dir, len + snap)), [prop.from]);
      if (ahead) {
        if (!landOk(a, ahead.point)) return null;
        const n = splitEdgeAt(g, ahead.edge, ahead.point, 0.3);
        if (!joinable(prop.from, n)) return null;
        addEdge(g, prop.from, n, 'street');
        return { node: n, stop: true, len: dist(a, ahead.point) };
      }

      if (!landOk(a, b)) return null;
      const clearance = Math.max(2.5, p.streetSpacing * 0.4);
      if (!clearOfOthers([b, lerp(a, b, 0.5)], [prop.from], clearance) || grazes(a, b, [prop.from])) return null;
      const n = addNode(g, b);
      addEdge(g, prop.from, n, 'street');
      return { node: n, stop: false, len };
    };

    // Seeds: evenly spaced points along the main roads inside the town.
    const seeds: { at: Vec2; tangent: Vec2 }[] = [];
    for (const e of liveEdges(g)) {
      const total = polylineLength(e.points);
      for (let s = p.streetSpacing * rng.range(0.4, 0.8); s < total - 2; s += p.streetSpacing * rng.range(0.7, 1.0)) {
        const seg = subPolyline(e.points, s, Math.min(total, s + 0.5));
        if (seg.length < 2) continue;
        const at = seg[0];
        if (dist(at, C) > R || !cellOk(at)) continue;
        if (g.nodes.some((n) => dist(n.at, at) < JUNCTION_CLEARANCE)) continue;
        if (!rng.chance(p.roadDensity * (0.6 + 0.4 * falloff(at)))) continue;
        seeds.push({ at, tangent: unit(sub(seg[seg.length - 1], at)) });
      }
    }

    const queue: Proposal[] = [];
    for (const seed of seeds) {
      const host = liveEdges(g).find((e) => closestOnPolyline(seed.at, e.points).d < 1e-6);
      if (!host) continue;
      const node = splitEdgeAt(g, host.id, seed.at);
      const base = Math.atan2(seed.tangent.y, seed.tangent.x);
      for (const side of [1, -1]) {
        if (rng.chance(0.9)) queue.push({ from: node, angle: snapToGrid(base + (side * Math.PI) / 2), depth: 0, since: 0, tries: 0 });
      }
    }

    for (let q = 0; q < queue.length && q < MAX_PROPOSALS; q++) {
      const prop = queue[q];
      const res = grow(prop);
      if (!res) {
        if (prop.tries < MAX_TRIES) queue.push({ ...prop, tries: prop.tries + 1 });
        continue;
      }
      if (res.stop) continue;
      const here = g.nodes[res.node].at;
      const f = falloff(here);
      let since = prop.since + res.len;
      if (since >= p.streetSpacing) {
        for (const side of [1, -1]) {
          if (rng.chance(p.roadDensity * (0.4 + 0.6 * f))) {
            queue.push({ from: res.node, angle: snapToGrid(prop.angle + (side * Math.PI) / 2), depth: prop.depth + 1, since: 0, tries: 0 });
            since = 0;
          }
        }
      }
      if (rng.chance(0.7 + 0.3 * f)) {
        queue.push({ from: res.node, angle: prop.angle, depth: prop.depth, since, tries: 0 });
      }
    }

    connectDeadEnds(g, keep, p.streetSegment * 1.8, firstHit, landOk, angleClash, joinable);
    addFootpaths(model, g, water, cost);

    planarize(g);
    mergePassThrough(g, keep);
    dropDuplicateStreets(g);
    const roads = keepConnected(compact(g), model.center);
    const next = { ...model, roads };
    return { ...next, bridges: computeBridges(next, waterMask(next)) };

    function addFootpaths(m: TownModel, gb: GraphBuilder, wet: Uint8Array, stepCost: Float32Array): void {
      const rock = rockMask(slope, m.params);
      const ground = new Uint8Array(w * h);
      for (let i = 0; i < ground.length; i++) ground[i] = !wet[i] && !rock[i] ? 1 : 0;
      const comp = new Int32Array(w * h).fill(-1);
      const comps: number[][] = [];
      for (let i = 0; i < ground.length; i++) {
        if (!ground[i] || comp[i] >= 0) continue;
        const cells = [i];
        comp[i] = comps.length;
        for (let head = 0; head < cells.length; head++) {
          const c = cells[head];
          const cx = c % w;
          const cy = (c - cx) / w;
          for (const [dx, dy] of DIRS4) {
            const nx = cx + dx;
            const ny = cy + dy;
            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
            const j = index(nx, ny, w);
            if (ground[j] && comp[j] < 0) {
              comp[j] = comps.length;
              cells.push(j);
            }
          }
        }
        comps.push(cells);
      }
      for (const cells of comps) {
        if (cells.length < PATH_COMPONENT_MIN) continue;
        const raster = roadRaster(m, liveEdges(gb));
        if (cells.some((c) => raster.kind[c] !== 0)) continue;
        const centerCell = index(Math.floor(C.x), Math.floor(C.y), w);
        let start = cells[0];
        for (const c of cells) if (octile(c, centerCell, w) < octile(start, centerCell, w)) start = c;
        const path = findPath(start, {
          width: w,
          height: h,
          stepCost: (from, to) => octile(from, to, w) * stepCost[to],
          isGoal: (c) => raster.kind[c] !== 0,
          heuristic: () => 0,
        });
        if (!path || path.length < 3) continue;
        const last = path[path.length - 1];
        const host = gb.edges[raster.edgeId[last]];
        const pts = simplifyRDP(path.map((c) => ({ x: (c % w) + 0.5, y: Math.floor(c / w) + 0.5 })), 0.6, roadSegmentOk(terrainInfo, m));
        const joint = closestOnPolyline(pts[pts.length - 1], host.points).point;
        pts[pts.length - 1] = joint;
        const end = splitEdgeAt(gb, host.id, joint, 0.75);
        const begin = addNode(gb, pts[0]);
        addEdge(gb, begin, end, 'path', pts);
      }
    }
  },
  check(model) {
    const errors: string[] = [];
    const graph = model.roads;
    if (graph.nodes.length && reachableNodes(graph, 0).size !== graph.nodes.length) {
      errors.push('road graph is not a single connected component');
    }
    errors.push(...checkCrossings(graph), ...checkDuplicates(graph));
    errors.push(...checkRoadSlopes(model, 'main'), ...checkRoadSlopes(model, 'street'));
    errors.push(...checkBridges(model, waterMask(model)));
    return errors;
  },
};

function connectDeadEnds(
  g: GraphBuilder,
  keep: ReadonlySet<number>,
  reach: number,
  firstHit: (a: Vec2, b: Vec2, skip: number[], skipEdge?: number) => { edge: number; point: Vec2; t: number } | null,
  landOk: (a: Vec2, b: Vec2) => boolean,
  angleClash: (node: number, dir: Vec2) => boolean,
  joinable: (a: number, b: number) => boolean,
): void {
  const inc = incidence(g);
  for (const [node, list] of inc) {
    if (list.length !== 1 || keep.has(node)) continue;
    const e = g.edges[list[0]];
    if (e.kind !== 'street' || g.dead.has(e.id)) continue;
    const a = g.nodes[node].at;
    const dir = scale(heading(a, e.from === node ? e.points : e.points.slice().reverse()), -1);
    const hit = firstHit(a, add(a, scale(dir, reach)), [node]);
    if (hit && hit.t * reach >= MIN_SEGMENT && landOk(a, hit.point)) {
      const n = splitEdgeAt(g, hit.edge, hit.point, 0.75);
      if (joinable(node, n)) addEdge(g, node, n, 'street');
      continue;
    }
    // Otherwise join the nearest node roughly ahead.
    let best = -1;
    let bestD = reach;
    for (const n of g.nodes) {
      if (n.id === node || !(inc.get(n.id)?.length)) continue;
      const v = sub(n.at, a);
      const d = Math.hypot(v.x, v.y);
      if (d < MIN_SEGMENT || d >= bestD) continue;
      if (!onGrid(unit(v)) || dot(unit(v), dir) < Math.cos((50 * Math.PI) / 180)) continue;
      best = n.id;
      bestD = d;
    }
    if (best < 0 || !joinable(node, best)) continue;
    const d = unit(sub(g.nodes[best].at, a));
    if (angleClash(best, scale(d, -1))) continue;
    addEdge(g, node, best, 'street');
  }
}

/** Remove any street that ends up running alongside another road (a duplicate). */
function dropDuplicateStreets(g: GraphBuilder): void {
  for (let pass = 0; pass < 3; pass++) {
    const graph = compact({ ...g, dead: new Set(g.dead) });
    const dup = findDuplicate(graph);
    if (!dup) return;
    // Map the compacted edge back to the builder by its geometry.
    const victim = liveEdges(g).find((e) => e.kind === 'street' && sameLine(e.points, dup.points));
    if (!victim) return;
    g.dead.add(victim.id);
  }
}

function sameLine(a: readonly Vec2[], b: readonly Vec2[]): boolean {
  return a.length === b.length && a.every((p, i) => dist(p, b[i]) < 1e-9);
}

/** Keep only the roads connected to the one nearest the center (dropping any cut off by removals). */
function keepConnected(graph: RoadGraph, center: Vec2): RoadGraph {
  if (!graph.nodes.length) return graph;
  let hub = 0;
  graph.nodes.forEach((n, i) => {
    if (dist(n.at, center) < dist(graph.nodes[hub].at, center)) hub = i;
  });
  const reached = reachableNodes(graph, hub);
  if (reached.size === graph.nodes.length) return graph;
  const g: GraphBuilder = { nodes: graph.nodes.map((n) => ({ ...n })), edges: graph.edges.map((e) => ({ ...e })), dead: new Set() };
  for (const e of graph.edges) if (!reached.has(e.from)) g.dead.add(e.id);
  return compact(g);
}

/** Edges may only meet at shared nodes. */
export function checkCrossings(graph: RoadGraph): string[] {
  const edges = graph.edges;
  for (let i = 0; i < edges.length; i++) {
    for (let j = i; j < edges.length; j++) {
      const A = edges[i];
      const B = edges[j];
      if (!boxesNear(A.points, B.points, 0)) continue;
      for (let s = 0; s + 1 < A.points.length; s++) {
        for (let t = i === j ? s + 2 : 0; t + 1 < B.points.length; t++) {
          const x = segmentIntersection(A.points[s], A.points[s + 1], B.points[t], B.points[t + 1]);
          if (!x) continue;
          const shared = [A.from, A.to].filter((n) => n === B.from || n === B.to);
          if (shared.some((n) => dist(graph.nodes[n].at, x.point) < 1e-6)) continue;
          if (i === j && A.from === A.to && dist(graph.nodes[A.from].at, x.point) < 1e-6) continue;
          return [`roads ${A.id} and ${B.id} cross at ${x.point.x.toFixed(1)},${x.point.y.toFixed(1)} without a junction`];
        }
      }
    }
  }
  return [];
}

/** Away from junctions, no two roads may run within DUPLICATE_DISTANCE of each other. */
export function checkDuplicates(graph: RoadGraph): string[] {
  const dup = findDuplicate(graph);
  return dup ? [`roads ${dup.a} and ${dup.b} run alongside each other near ${dup.at.x.toFixed(1)},${dup.at.y.toFixed(1)}`] : [];
}

/** The first pair of roads running alongside each other; `points` is the street to drop when one is a street. */
function findDuplicate(graph: RoadGraph): { a: number; b: number; at: Vec2; points: Vec2[] } | null {
  const edges = graph.edges;
  const nearNode = (pt: Vec2) => graph.nodes.some((n) => dist(n.at, pt) < JUNCTION_CLEARANCE);
  for (const A of edges) {
    const samples = samplePolyline(A.points, 0.5).filter((pt) => !nearNode(pt));
    if (!samples.length) continue;
    for (const B of edges) {
      if (A.id === B.id || !boxesNear(A.points, B.points, DUPLICATE_DISTANCE)) continue;
      let run = 0;
      for (const pt of samples) {
        run = distToPolyline(pt, B.points) < DUPLICATE_DISTANCE ? run + 1 : 0;
        if (run >= 4) {
          const street = A.kind === 'street' ? A : B.kind === 'street' ? B : A;
          return { a: A.id, b: B.id, at: pt, points: street.points };
        }
      }
    }
  }
  return null;
}

function boxesNear(a: readonly Vec2[], b: readonly Vec2[], pad: number): boolean {
  const box = (pts: readonly Vec2[]) => {
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const p of pts) {
      x0 = Math.min(x0, p.x);
      y0 = Math.min(y0, p.y);
      x1 = Math.max(x1, p.x);
      y1 = Math.max(y1, p.y);
    }
    return { x0, y0, x1, y1 };
  };
  const A = box(a);
  const B = box(b);
  return !(B.x0 > A.x1 + pad || B.x1 < A.x0 - pad || B.y0 > A.y1 + pad || B.y1 < A.y0 - pad);
}

/**
 * Direction a road leaves `at` along `pts` (which start at `at`), judged a couple
 * of tiles out so a tiny first segment can't hide the road's real heading.
 */
function heading(at: Vec2, pts: readonly Vec2[]): Vec2 {
  for (const p of pts) if (dist(p, at) >= HEADING_REACH) return unit(sub(p, at));
  return unit(sub(pts[pts.length - 1], at));
}

/** Nearest of the four grid directions to an angle. */
function snapToGrid(angle: number): number {
  return Math.round(angle / (Math.PI / 2)) * (Math.PI / 2);
}

/** A direction within GRID_TOLERANCE of one of the four grid directions. */
function onGrid(d: Vec2): boolean {
  const a = Math.atan2(d.y, d.x);
  return Math.abs(a - snapToGrid(a)) <= GRID_TOLERANCE;
}

function unit(v: Vec2): Vec2 {
  const len = Math.hypot(v.x, v.y) || 1;
  return { x: v.x / len, y: v.y / len };
}

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));


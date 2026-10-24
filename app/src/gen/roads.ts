// Stage 4: main roads. A* from each gate to the center over the cost field,
// along the tile grid with a penalty per turn, so roads run straight.
// Cells already on a road are cheap and cells beside one are penalised, so
// later roads merge into earlier ones instead of running alongside them. Each
// stretch of new road becomes a graph edge; joins split the existing edge.

import { findGridPath, manhattan, octile } from '../core/astar';
import { index } from '../core/grid';
import { samplePolyline, simplifyRDP } from '../core/geometry';
import type { TownModel, Vec2 } from '../model/types';
import { roadSegmentOk, roadTerrain } from './costs';
import { checkBridges, compact, computeBridges, nearestNode, planarize, reachableNodes, type GraphBuilder } from './roadGraph';
import { waterMask } from './raster';
import type { Stage } from './stage';

const REUSE_COST = 0.3;
const BESIDE_PENALTY = 3;
const BESIDE_RADIUS = 2;
const SIMPLIFY_EPSILON = 0.6;
/**
 * Extra A* cost per turn. Main roads move along the tile grid only, and turning
 * is expensive, so they run in long straight stretches meeting at right angles;
 * that keeps building fronts flush with the road.
 */
const TURN_COST = 10;

export const roads: Stage = {
  name: 'roads',
  run(model) {
    const { width: w, height: h } = model;
    const terrainInfo = roadTerrain(model);
    const { cost } = terrainInfo;
    const segmentOk = roadSegmentOk(terrainInfo, model);
    const onRoad = new Uint8Array(w * h);
    const beside = new Uint8Array(w * h);

    const centerCell = index(Math.floor(model.center.x), Math.floor(model.center.y), w);
    const nodeAt = new Map<number, number>(); // cell -> node id
    const nodeCells: number[] = [];
    const edges: { cells: number[] }[] = [];
    const cellEdges = new Map<number, number[]>(); // cell -> edges through it

    const newNode = (cell: number) => {
      const id = nodeCells.length;
      nodeCells.push(cell);
      nodeAt.set(cell, id);
      return id;
    };
    const indexEdge = (e: number) => {
      for (const c of edges[e].cells) cellEdges.set(c, [...(cellEdges.get(c) ?? []).filter((x) => x !== e), e]);
    };
    const addEdge = (cells: number[]) => {
      edges.push({ cells });
      indexEdge(edges.length - 1);
      for (const c of cells) markRoad(c);
    };
    const markRoad = (c: number) => {
      onRoad[c] = 1;
      const cx = c % w;
      const cy = (c - cx) / w;
      for (let dy = -BESIDE_RADIUS; dy <= BESIDE_RADIUS; dy++) {
        for (let dx = -BESIDE_RADIUS; dx <= BESIDE_RADIUS; dx++) {
          const nx = cx + dx;
          const ny = cy + dy;
          if (nx >= 0 && ny >= 0 && nx < w && ny < h) beside[index(nx, ny, w)] = 1;
        }
      }
    };
    /** Node at a road cell, splitting the edge that passes through it if needed. */
    const ensureNode = (cell: number): number => {
      const existing = nodeAt.get(cell);
      if (existing !== undefined) return existing;
      const id = newNode(cell);
      for (const e of cellEdges.get(cell) ?? []) {
        const cells = edges[e].cells;
        const k = cells.indexOf(cell);
        if (k <= 0 || k >= cells.length - 1) continue;
        edges[e].cells = cells.slice(0, k + 1);
        edges.push({ cells: cells.slice(k) });
        const added = edges.length - 1;
        for (const c of cells.slice(k + 1)) cellEdges.set(c, (cellEdges.get(c) ?? []).map((x) => (x === e ? added : x)));
        cellEdges.set(cell, [...(cellEdges.get(cell) ?? []), added]);
        break;
      }
      return id;
    };

    newNode(centerCell);
    const gates = model.gates
      .map((g) => index(Math.floor(g.x), Math.floor(g.y), w))
      .sort((a, b) => octile(a, centerCell, w) - octile(b, centerCell, w));

    for (const gate of gates) {
      const path = findGridPath(gate, {
        width: w,
        height: h,
        stepCost: (_from, to) => (onRoad[to] ? REUSE_COST : cost[to] + (beside[to] ? BESIDE_PENALTY : 0)),
        turnCost: TURN_COST,
        isGoal: (c) => c === centerCell,
        heuristic: (c) => manhattan(c, centerCell, w) * REUSE_COST,
      });
      if (!path) continue;
      // Each run of new cells becomes an edge between the road cells (or the gate) around it.
      const n = path.length;
      let k = 0;
      while (k < n) {
        if (onRoad[path[k]]) {
          k++;
          continue;
        }
        let end = k;
        while (end + 1 < n && !onRoad[path[end + 1]]) end++;
        const s = k === 0 ? 0 : k - 1;
        const e = Math.min(end + 1, n - 1);
        ensureNode(path[s]);
        ensureNode(path[e]);
        addEdge(path.slice(s, e + 1));
        k = end + 1;
      }
      ensureNode(gate); // a gate that sits on an earlier road still needs its own node
    }

    const center = (c: number): Vec2 => ({ x: (c % w) + 0.5, y: Math.floor(c / w) + 0.5 });
    const builder: GraphBuilder = {
      nodes: nodeCells.map((c, id) => ({ id, at: center(c) })),
      edges: edges.map((e, id) => ({
        id,
        from: nodeAt.get(e.cells[0])!,
        to: nodeAt.get(e.cells[e.cells.length - 1])!,
        kind: 'main' as const,
        points: simplifyRDP(e.cells.map(center), SIMPLIFY_EPSILON, segmentOk),
      })),
      dead: new Set(),
    };
    planarize(builder);
    const next = { ...model, roads: compact(builder) };
    return { ...next, bridges: computeBridges(next, waterMask(next)) };
  },
  check(model) {
    const errors: string[] = [];
    const { roads: graph } = model;
    if (!graph.nodes.length) return ['no main roads were built'];
    const hub = nearestNode(graph, model.center);
    const reached = reachableNodes(graph, hub);
    for (const g of model.gates) {
      const n = nearestNode(graph, g);
      if (n < 0 || Math.hypot(graph.nodes[n].at.x - g.x, graph.nodes[n].at.y - g.y) > 0.01 || !reached.has(n)) {
        errors.push(`gate ${g.x},${g.y} is not connected to the center`);
      }
    }
    errors.push(...checkRoadSlopes(model, 'main'), ...checkBridges(model, waterMask(model)));
    return errors;
  },
};

/** No sampled point of a road of this kind sits on ground steeper than maxRoadSlope. */
export function checkRoadSlopes(model: TownModel, kind: 'main' | 'street'): string[] {
  const { slope, water } = roadTerrain(model);
  const { width: w } = model;
  for (const e of model.roads.edges) {
    if (e.kind !== kind) continue;
    for (const p of samplePolyline(e.points, 0.5)) {
      const i = index(Math.floor(p.x), Math.floor(p.y), w);
      if (!water[i] && slope[i] > model.params.maxRoadSlope + 1e-6) {
        return [`${kind} road ${e.id} climbs a ${slope[i].toFixed(3)} slope at ${p.x.toFixed(1)},${p.y.toFixed(1)}`];
      }
    }
  }
  return [];
}

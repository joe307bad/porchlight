// Editing helpers for the road graph: adding and splitting edges, making the
// graph planar (every crossing becomes a node), merging pass-through nodes,
// and deriving bridges wherever a road runs over water.

import { arcPosition, closestOnPolyline, dist, samplePolyline, segmentIntersection } from '../core/geometry';
import type { Bridge, RoadEdge, RoadGraph, RoadKind, TownModel, Vec2 } from '../model/types';

/** A mutable graph under construction; edges in `dead` are ignored and dropped by compact(). */
export interface GraphBuilder {
  nodes: { id: number; at: Vec2 }[];
  edges: RoadEdge[];
  dead: Set<number>;
}

export function builderFrom(graph: RoadGraph): GraphBuilder {
  return {
    nodes: graph.nodes.map((n) => ({ id: n.id, at: { ...n.at } })),
    edges: graph.edges.map((e) => ({ ...e, points: e.points.map((p) => ({ ...p })) })),
    dead: new Set(),
  };
}

export const liveEdges = (g: GraphBuilder): RoadEdge[] => g.edges.filter((e) => !g.dead.has(e.id));

/** Add a node, or reuse one within `snap` tiles. */
export function addNode(g: GraphBuilder, at: Vec2, snap = 1e-6): number {
  for (const n of g.nodes) if (dist(n.at, at) <= snap) return n.id;
  const id = g.nodes.length;
  g.nodes.push({ id, at: { x: at.x, y: at.y } });
  return id;
}

export function addEdge(g: GraphBuilder, from: number, to: number, kind: RoadKind, points?: Vec2[]): number {
  const id = g.edges.length;
  const pts = points ?? [g.nodes[from].at, g.nodes[to].at];
  pts[0] = { ...g.nodes[from].at };
  pts[pts.length - 1] = { ...g.nodes[to].at };
  g.edges.push({ id, from, to, kind, points: pts });
  return id;
}

/**
 * Split an edge at the point closest to `at`, returning the node there. Points
 * within `snap` of an endpoint reuse that endpoint instead.
 */
export function splitEdgeAt(g: GraphBuilder, edgeId: number, at: Vec2, snap = 0.05): number {
  const edge = g.edges[edgeId];
  const c = closestOnPolyline(at, edge.points);
  if (dist(c.point, g.nodes[edge.from].at) <= snap) return edge.from;
  if (dist(c.point, g.nodes[edge.to].at) <= snap) return edge.to;
  const node = addNode(g, c.point);
  const first = [...edge.points.slice(0, c.seg + 1), c.point];
  const second = [c.point, ...edge.points.slice(c.seg + 1)];
  g.dead.add(edgeId);
  addEdge(g, edge.from, node, edge.kind, dedupe(first));
  addEdge(g, node, edge.to, edge.kind, dedupe(second));
  return node;
}

function dedupe(pts: Vec2[]): Vec2[] {
  const out: Vec2[] = [];
  for (const p of pts) if (!out.length || dist(out[out.length - 1], p) > 1e-9) out.push(p);
  if (out.length === 1) out.push({ ...out[0] });
  return out;
}

/** Live edges incident to each node. */
export function incidence(g: GraphBuilder): Map<number, number[]> {
  const out = new Map<number, number[]>();
  for (const e of liveEdges(g)) {
    for (const n of [e.from, e.to]) {
      const list = out.get(n);
      if (list) list.push(e.id);
      else out.set(n, [e.id]);
    }
  }
  return out;
}

/** Insert a node at every place two edges cross, so the graph is planar. */
export function planarize(g: GraphBuilder): void {
  interface Seg { e: number; s: number; a: Vec2; b: Vec2; x0: number; y0: number; x1: number; y1: number }
  const segs: Seg[] = [];
  for (const e of liveEdges(g)) {
    for (let s = 0; s + 1 < e.points.length; s++) {
      const a = e.points[s];
      const b = e.points[s + 1];
      segs.push({ e: e.id, s, a, b, x0: Math.min(a.x, b.x), y0: Math.min(a.y, b.y), x1: Math.max(a.x, b.x), y1: Math.max(a.y, b.y) });
    }
  }
  const cuts = new Map<number, { pos: number; node: number }[]>();
  const pushCut = (e: number, pos: number, node: number) => {
    const list = cuts.get(e);
    if (list) list.push({ pos, node });
    else cuts.set(e, [{ pos, node }]);
  };
  const endpointNode = (edge: RoadEdge, pos: number): number => {
    if (pos < 1e-7) return edge.from;
    if (pos > edge.points.length - 1 - 1e-7) return edge.to;
    return -1;
  };
  for (let i = 0; i < segs.length; i++) {
    const A = segs[i];
    for (let j = i + 1; j < segs.length; j++) {
      const B = segs[j];
      if (B.x0 > A.x1 || B.x1 < A.x0 || B.y0 > A.y1 || B.y1 < A.y0) continue;
      if (A.e === B.e && Math.abs(A.s - B.s) <= 1) continue;
      const x = segmentIntersection(A.a, A.b, B.a, B.b);
      if (!x) continue;
      const ea = g.edges[A.e];
      const eb = g.edges[B.e];
      const posA = A.s + x.t;
      const posB = B.s + x.u;
      const na = endpointNode(ea, posA);
      const nb = endpointNode(eb, posB);
      if (na >= 0 && nb >= 0) continue; // meet at endpoints: either shared already or a near-miss
      if (A.e === B.e && ea.from === ea.to && (na >= 0 || nb >= 0)) continue;
      const node = na >= 0 ? na : nb >= 0 ? nb : addNode(g, x.point, 0.05);
      if (na < 0) pushCut(A.e, posA, node);
      if (nb < 0) pushCut(B.e, posB, node);
    }
  }
  for (const [edgeId, list] of cuts) {
    const edge = g.edges[edgeId];
    list.sort((a, b) => a.pos - b.pos);
    let startNode = edge.from;
    let startPos = 0;
    let startPt = edge.points[0];
    const pieces: { from: number; to: number; pts: Vec2[] }[] = [];
    for (const cut of [...list, { pos: edge.points.length - 1, node: edge.to }]) {
      if (cut.node === startNode && cut.pos - startPos < 1e-6) continue;
      const pts: Vec2[] = [startPt];
      for (let k = Math.floor(startPos) + 1; k < cut.pos; k++) pts.push(edge.points[k]);
      const end = g.nodes[cut.node].at;
      pts.push(end);
      pieces.push({ from: startNode, to: cut.node, pts: dedupe(pts) });
      startNode = cut.node;
      startPos = cut.pos;
      startPt = end;
    }
    g.dead.add(edgeId);
    for (const piece of pieces) {
      if (piece.from === piece.to && piece.pts.length <= 2) continue;
      addEdge(g, piece.from, piece.to, edge.kind, piece.pts);
    }
  }
}

/** Merge chains through nodes of degree 2 (same kind on both sides) into single edges. */
export function mergePassThrough(g: GraphBuilder, keep: ReadonlySet<number>): void {
  const inc = incidence(g);
  for (const [node, list] of inc) {
    if (keep.has(node) || list.length !== 2) continue;
    const [e1, e2] = list.map((id) => g.edges[id]);
    if (e1.id === e2.id || e1.kind !== e2.kind) continue;
    const a = orient(e1, node, 'end');
    const b = orient(e2, node, 'start');
    const from = a.from;
    const to = b.to;
    if (from === to) continue;
    g.dead.add(e1.id);
    g.dead.add(e2.id);
    const id = addEdge(g, from, to, e1.kind, [...a.points, ...b.points.slice(1)]);
    // Keep incidence current for the neighbours.
    for (const [n, old] of [[from, e1.id], [to, e2.id]] as const) {
      const l = inc.get(n)!;
      l[l.indexOf(old)] = id;
    }
    inc.set(node, []);
  }
}

function orient(e: RoadEdge, node: number, side: 'start' | 'end'): { from: number; to: number; points: Vec2[] } {
  const forward = side === 'end' ? e.to === node : e.from === node;
  return forward
    ? { from: e.from, to: e.to, points: e.points }
    : { from: e.to, to: e.from, points: e.points.slice().reverse() };
}

/** Drop dead edges and orphan nodes, then renumber ids densely. */
export function compact(g: GraphBuilder): RoadGraph {
  const edges = liveEdges(g).filter((e) => e.from !== e.to || e.points.length > 2);
  const used = new Map<number, number>();
  for (const e of edges) for (const n of [e.from, e.to]) if (!used.has(n)) used.set(n, 0);
  const order = [...used.keys()].sort((a, b) => a - b);
  order.forEach((old, i) => used.set(old, i));
  return {
    nodes: order.map((old, i) => ({ id: i, at: g.nodes[old].at })),
    edges: edges.map((e, i) => ({
      id: i,
      from: used.get(e.from)!,
      to: used.get(e.to)!,
      kind: e.kind,
      points: e.points,
    })),
  };
}

const BRIDGE_SAMPLE = 0.25;

/** One bridge per stretch of road over water; `from`/`to` lie on the road just outside the water. */
export function computeBridges(model: TownModel, water: Uint8Array): Bridge[] {
  const { width: w, height: h } = model;
  const wet = (p: Vec2) => {
    const x = Math.floor(p.x);
    const y = Math.floor(p.y);
    return x >= 0 && y >= 0 && x < w && y < h && water[y * w + x] === 1;
  };
  const bridges: Bridge[] = [];
  for (const edge of model.roads.edges) {
    const samples = samplePolyline(edge.points, BRIDGE_SAMPLE);
    let start = -1;
    for (let k = 0; k <= samples.length; k++) {
      const isWet = k < samples.length && wet(samples[k]);
      if (isWet && start < 0) start = k;
      if (!isWet && start >= 0) {
        const from = samples[Math.max(0, start - 1)];
        const to = samples[Math.min(samples.length - 1, k)];
        bridges.push({ id: bridges.length, roadEdgeId: edge.id, from: { ...from }, to: { ...to } });
        start = -1;
      }
    }
  }
  return bridges;
}

/** Every stretch of road over water must lie on a bridge of that edge. */
export function checkBridges(model: TownModel, water: Uint8Array): string[] {
  const { width: w } = model;
  const errors: string[] = [];
  const byEdge = new Map<number, Bridge[]>();
  for (const b of model.bridges) byEdge.set(b.roadEdgeId, [...(byEdge.get(b.roadEdgeId) ?? []), b]);
  for (const edge of model.roads.edges) {
    const spans = (byEdge.get(edge.id) ?? []).map((b) => [arcPosition(b.from, edge.points), arcPosition(b.to, edge.points)]);
    for (const p of samplePolyline(edge.points, BRIDGE_SAMPLE)) {
      if (!water[Math.floor(p.y) * w + Math.floor(p.x)]) continue;
      const at = arcPosition(p, edge.points);
      const covered = spans.some(([a, b]) => at >= Math.min(a, b) - 1e-6 && at <= Math.max(a, b) + 1e-6);
      if (!covered) {
        errors.push(`road ${edge.id} crosses water at ${p.x.toFixed(1)},${p.y.toFixed(1)} without a bridge`);
        break;
      }
    }
  }
  return errors;
}

/** Node ids reachable from `start` over the graph. */
export function reachableNodes(graph: RoadGraph, start: number): Set<number> {
  const adj = new Map<number, number[]>();
  for (const e of graph.edges) {
    adj.set(e.from, [...(adj.get(e.from) ?? []), e.to]);
    adj.set(e.to, [...(adj.get(e.to) ?? []), e.from]);
  }
  const seen = new Set([start]);
  const stack = [start];
  while (stack.length) {
    for (const n of adj.get(stack.pop()!) ?? []) {
      if (!seen.has(n)) {
        seen.add(n);
        stack.push(n);
      }
    }
  }
  return seen;
}

export function nearestNode(graph: RoadGraph, at: Vec2): number {
  let best = -1;
  let bestD = Infinity;
  for (const n of graph.nodes) {
    const d = dist(n.at, at);
    if (d < bestD) {
      bestD = d;
      best = n.id;
    }
  }
  return best;
}


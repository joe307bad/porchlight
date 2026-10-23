// Small, readable 2D geometry helpers. Polygons are Vec2 rings without a
// repeated closing point.

import type { Vec2 } from '../model/types';

export const EPS = 1e-9;

export const vec = (x: number, y: number): Vec2 => ({ x, y });
export const sub = (a: Vec2, b: Vec2): Vec2 => ({ x: a.x - b.x, y: a.y - b.y });
export const add = (a: Vec2, b: Vec2): Vec2 => ({ x: a.x + b.x, y: a.y + b.y });
export const scale = (a: Vec2, s: number): Vec2 => ({ x: a.x * s, y: a.y * s });
export const lerp = (a: Vec2, b: Vec2, t: number): Vec2 => ({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
export const dist = (a: Vec2, b: Vec2): number => Math.hypot(a.x - b.x, a.y - b.y);
export const cross = (a: Vec2, b: Vec2): number => a.x * b.y - a.y * b.x;
export const dot = (a: Vec2, b: Vec2): number => a.x * b.x + a.y * b.y;

/** Closest point on segment ab to p, with its parameter t in [0, 1]. */
export function closestOnSegment(p: Vec2, a: Vec2, b: Vec2): { point: Vec2; t: number } {
  const ab = sub(b, a);
  const len2 = dot(ab, ab);
  const t = len2 < EPS ? 0 : Math.max(0, Math.min(1, dot(sub(p, a), ab) / len2));
  return { point: lerp(a, b, t), t };
}

export function distToSegment(p: Vec2, a: Vec2, b: Vec2): number {
  return dist(p, closestOnSegment(p, a, b).point);
}

export function distToPolyline(p: Vec2, pts: readonly Vec2[]): number {
  let best = Infinity;
  for (let i = 0; i + 1 < pts.length; i++) best = Math.min(best, distToSegment(p, pts[i], pts[i + 1]));
  return pts.length === 1 ? dist(p, pts[0]) : best;
}

/** Closest point on a polyline: segment index, parameter along it, and the point. */
export function closestOnPolyline(p: Vec2, pts: readonly Vec2[]): { seg: number; t: number; point: Vec2; d: number } {
  let best = { seg: 0, t: 0, point: pts[0], d: Infinity };
  for (let i = 0; i + 1 < pts.length; i++) {
    const c = closestOnSegment(p, pts[i], pts[i + 1]);
    const d = dist(p, c.point);
    if (d < best.d) best = { seg: i, t: c.t, point: c.point, d };
  }
  return best;
}

/**
 * Intersection of segments ab and cd, with parameters t (along ab) and u (along cd),
 * both in [0, 1]. Parallel segments return null.
 */
export function segmentIntersection(a: Vec2, b: Vec2, c: Vec2, d: Vec2): { t: number; u: number; point: Vec2 } | null {
  const r = sub(b, a);
  const s = sub(d, c);
  const denom = cross(r, s);
  if (Math.abs(denom) < EPS) return null;
  const ca = sub(c, a);
  const t = cross(ca, s) / denom;
  const u = cross(ca, r) / denom;
  if (t < -EPS || t > 1 + EPS || u < -EPS || u > 1 + EPS) return null;
  return { t: Math.max(0, Math.min(1, t)), u: Math.max(0, Math.min(1, u)), point: lerp(a, b, t) };
}

/** Shortest distance between two segments (0 when they cross). */
export function segmentDistance(a: Vec2, b: Vec2, c: Vec2, d: Vec2): number {
  if (segmentIntersection(a, b, c, d)) return 0;
  return Math.min(distToSegment(a, c, d), distToSegment(b, c, d), distToSegment(c, a, b), distToSegment(d, a, b));
}

/** Shortest distance between two polylines. */
export function polylineDistance(p: readonly Vec2[], q: readonly Vec2[]): number {
  let best = Infinity;
  for (let i = 0; i + 1 < p.length; i++) {
    for (let j = 0; j + 1 < q.length; j++) best = Math.min(best, segmentDistance(p[i], p[i + 1], q[j], q[j + 1]));
  }
  return best;
}

/** Distance along a polyline to the point on it closest to `p`. */
export function arcPosition(p: Vec2, pts: readonly Vec2[]): number {
  const c = closestOnPolyline(p, pts);
  let len = 0;
  for (let i = 0; i < c.seg; i++) len += dist(pts[i], pts[i + 1]);
  return len + (pts.length > 1 ? dist(pts[c.seg], c.point) : 0);
}

/** The part of a polyline between two arc positions. */
export function subPolyline(pts: readonly Vec2[], from: number, to: number): Vec2[] {
  const out: Vec2[] = [];
  let acc = 0;
  for (let i = 0; i + 1 < pts.length; i++) {
    const len = dist(pts[i], pts[i + 1]);
    const a = acc;
    const b = acc + len;
    if (b >= from && a <= to && len > 0) {
      if (!out.length) out.push(lerp(pts[i], pts[i + 1], Math.max(0, (from - a) / len)));
      out.push(lerp(pts[i], pts[i + 1], Math.min(1, (to - a) / len)));
    }
    acc = b;
  }
  return out;
}

export function polylineLength(pts: readonly Vec2[]): number {
  let len = 0;
  for (let i = 0; i + 1 < pts.length; i++) len += dist(pts[i], pts[i + 1]);
  return len;
}

/** Points every `step` along a polyline, endpoints included. */
export function samplePolyline(pts: readonly Vec2[], step: number): Vec2[] {
  if (pts.length === 0) return [];
  const out: Vec2[] = [pts[0]];
  for (let i = 0; i + 1 < pts.length; i++) {
    const len = dist(pts[i], pts[i + 1]);
    const n = Math.max(1, Math.ceil(len / step));
    for (let k = 1; k <= n; k++) out.push(lerp(pts[i], pts[i + 1], k / n));
  }
  return out;
}

/** Signed area; positive for clockwise rings in y-down screen space. */
export function polygonArea(poly: readonly Vec2[]): number {
  let a = 0;
  for (let i = 0; i < poly.length; i++) {
    const p = poly[i];
    const q = poly[(i + 1) % poly.length];
    a += p.x * q.y - q.x * p.y;
  }
  return a / 2;
}

export function polygonCentroid(poly: readonly Vec2[]): Vec2 {
  const a = polygonArea(poly);
  if (Math.abs(a) < EPS) {
    const sum = poly.reduce((s, p) => add(s, p), vec(0, 0));
    return scale(sum, 1 / Math.max(1, poly.length));
  }
  let cx = 0;
  let cy = 0;
  for (let i = 0; i < poly.length; i++) {
    const p = poly[i];
    const q = poly[(i + 1) % poly.length];
    const f = p.x * q.y - q.x * p.y;
    cx += (p.x + q.x) * f;
    cy += (p.y + q.y) * f;
  }
  return { x: cx / (6 * a), y: cy / (6 * a) };
}

export function pointInPolygon(p: Vec2, poly: readonly Vec2[]): boolean {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const a = poly[i];
    const b = poly[j];
    if (a.y > p.y !== b.y > p.y && p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x) inside = !inside;
  }
  return inside;
}

export function polygonBounds(poly: readonly Vec2[]): { x0: number; y0: number; x1: number; y1: number } {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const p of poly) {
    x0 = Math.min(x0, p.x);
    y0 = Math.min(y0, p.y);
    x1 = Math.max(x1, p.x);
    y1 = Math.max(y1, p.y);
  }
  return { x0, y0, x1, y1 };
}

/**
 * Ramer–Douglas–Peucker simplification; endpoints are always kept. A chord is
 * also split if `segmentOk` rejects it, so callers can forbid shortcuts across
 * ground the original path avoided.
 */
export function simplifyRDP(
  pts: readonly Vec2[],
  epsilon: number,
  segmentOk: (a: Vec2, b: Vec2) => boolean = () => true,
): Vec2[] {
  if (pts.length <= 2) return pts.slice();
  const keep = new Uint8Array(pts.length);
  keep[0] = keep[pts.length - 1] = 1;
  const stack: [number, number][] = [[0, pts.length - 1]];
  while (stack.length) {
    const [i0, i1] = stack.pop()!;
    let worst = -1;
    let worstD = epsilon;
    for (let i = i0 + 1; i < i1; i++) {
      const d = distToSegment(pts[i], pts[i0], pts[i1]);
      if (d > worstD) {
        worstD = d;
        worst = i;
      }
    }
    if (worst < 0 && i1 - i0 > 1 && !segmentOk(pts[i0], pts[i1])) worst = (i0 + i1) >> 1;
    if (worst >= 0) {
      keep[worst] = 1;
      stack.push([i0, worst], [worst, i1]);
    }
  }
  return pts.filter((_, i) => keep[i]);
}

/** Chaikin corner cutting on an open polyline; endpoints are kept. */
export function chaikin(pts: readonly Vec2[], passes: number): Vec2[] {
  let cur = pts.slice();
  for (let p = 0; p < passes && cur.length > 2; p++) {
    const next: Vec2[] = [cur[0]];
    for (let i = 0; i + 1 < cur.length; i++) {
      next.push(lerp(cur[i], cur[i + 1], 0.25), lerp(cur[i], cur[i + 1], 0.75));
    }
    next.push(cur[cur.length - 1]);
    cur = next;
  }
  return cur;
}

/** Drop vertices that sit on a straight line between their neighbours. */
export function removeCollinear(ring: readonly Vec2[]): Vec2[] {
  const out: Vec2[] = [];
  for (let i = 0; i < ring.length; i++) {
    const prev = ring[(i - 1 + ring.length) % ring.length];
    const p = ring[i];
    const next = ring[(i + 1) % ring.length];
    if (Math.abs(cross(sub(p, prev), sub(next, p))) > EPS) out.push(p);
  }
  return out;
}

/**
 * Outline loops of the cells set in `mask`, traced along cell edges with the
 * cells on the right (clockwise in y-down space). Returns rings sorted by
 * descending area, so ring 0 is the outer boundary and the rest are holes.
 * Consistent right turns at pinch corners keep every ring simple.
 */
export function traceCellOutlines(mask: Uint8Array, width: number, height: number): Vec2[][] {
  const W = width + 1;
  // Directions: 0 E, 1 S, 2 W, 3 N (clockwise in y-down space).
  const DX = [1, 0, -1, 0];
  const DY = [0, 1, 0, -1];
  const edgesFrom = new Map<number, number[]>(); // corner -> edge ids
  const edgeStart: number[] = [];
  const edgeDir: number[] = [];
  const at = (x: number, y: number) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x] === 1;
  const addEdge = (cx: number, cy: number, dir: number) => {
    const id = edgeStart.length;
    const corner = cy * W + cx;
    edgeStart.push(corner);
    edgeDir.push(dir);
    const list = edgesFrom.get(corner);
    if (list) list.push(id);
    else edgesFrom.set(corner, [id]);
  };
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (!at(x, y)) continue;
      if (!at(x, y - 1)) addEdge(x, y, 0);
      if (!at(x + 1, y)) addEdge(x + 1, y, 1);
      if (!at(x, y + 1)) addEdge(x + 1, y + 1, 2);
      if (!at(x - 1, y)) addEdge(x, y + 1, 3);
    }
  }
  const used = new Uint8Array(edgeStart.length);
  const rings: Vec2[][] = [];
  for (let e0 = 0; e0 < edgeStart.length; e0++) {
    if (used[e0]) continue;
    const ring: Vec2[] = [];
    let e = e0;
    while (!used[e]) {
      used[e] = 1;
      const corner = edgeStart[e];
      ring.push({ x: corner % W, y: Math.floor(corner / W) });
      const d = edgeDir[e];
      const endCorner = corner + DY[d] * W + DX[d];
      const options = edgesFrom.get(endCorner) ?? [];
      let next = -1;
      for (const turn of [1, 0, 3]) {
        const want = (d + turn) % 4;
        const found = options.find((o) => !used[o] && edgeDir[o] === want);
        if (found !== undefined) {
          next = found;
          break;
        }
      }
      if (next < 0) break;
      e = next;
    }
    const simplified = removeCollinear(ring);
    if (simplified.length >= 3) rings.push(simplified);
  }
  rings.sort((a, b) => Math.abs(polygonArea(b)) - Math.abs(polygonArea(a)));
  return rings;
}

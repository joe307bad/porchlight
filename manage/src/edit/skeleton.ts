// Turning what an editor does on the edit screen into a skeleton, by the contract's rules
// (docs/street-editor-api.md, "Rules the edit screen keeps"): a main road changed snapshots every main
// road into `mains` and changes it there; a street changed is pinned in `streets` and its old ends go
// into `removed`; a junction moved moves the ends of every road that meets there; the bullseye is
// `center`; the side panel writes `roads` and `junctions`. Also the drag's rubber band (the roads near a
// handle bend smoothly after it), snapping, the live warnings at the handle, and rebasing a draft.
// Pure: no DOM, so the tests run it as it is.

import type { Control, JunctionOverride, Pt, RoadClass, RoadOverride, Skeleton, SkeletonRoad, Town, TownRoad } from './types.ts';

/** How far a drag's pull reaches along the roads from the handle (tiles). */
export const FALLOFF = 6;
/** Within this of a junction, a dragged junction merges with it on release (tiles). */
export const MERGE = 1.5;
/** Within this of a road, a dragged end joins it as a T (tiles). */
export const JOIN = 1;
/** Within this of square or straight on, a drag snaps to it (radians: 8°). */
export const SNAP_ANGLE = (8 * Math.PI) / 180;
/** The nearest two junctions may be along a main road, and along a street (tiles). */
export const APART_MAIN = 4;
export const APART_STREET = 3;
/** The sharpest two roads may meet (radians: 60°). */
export const LEAST_ANGLE = Math.PI / 3;
/** How near a saved end has to be to a road's end, and a road's end to a pinned road's line, to be it (tiles). */
export const MATCH = 1;
const ON_LINE = 0.5;

// --- Geometry --------------------------------------------------------------------------------------

export const dist = (a: Pt, b: Pt): number => Math.hypot(a[0] - b[0], a[1] - b[1]);

export function polyLength(pts: Pt[]): number {
  let s = 0;
  for (let i = 1; i < pts.length; i++) s += dist(pts[i - 1], pts[i]);
  return s;
}

export interface OnLine {
  point: Pt;
  /** The segment it's on, and how far along that segment (0..1). */
  seg: number;
  t: number;
  /** How far from the line, and how far along it from its start. */
  d: number;
  s: number;
}

export function closestOn(p: Pt, pts: Pt[]): OnLine {
  let best: OnLine = { point: pts[0], seg: 0, t: 0, d: dist(p, pts[0]), s: 0 };
  let along = 0;
  for (let i = 0; i + 1 < pts.length; i++) {
    const [a, b] = [pts[i], pts[i + 1]];
    const dx = b[0] - a[0], dy = b[1] - a[1];
    const len2 = dx * dx + dy * dy;
    const t = len2 < 1e-12 ? 0 : Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / len2));
    const q: Pt = [a[0] + dx * t, a[1] + dy * t];
    const d = dist(p, q);
    if (d < best.d) best = { point: q, seg: i, t, d, s: along + Math.sqrt(len2) * t };
    along += Math.sqrt(len2);
  }
  return best;
}

/** The point `s` tiles along a polyline. */
export function pointAt(pts: Pt[], s: number): Pt {
  for (let i = 0; i + 1 < pts.length; i++) {
    const len = dist(pts[i], pts[i + 1]);
    if (s <= len) {
      const t = len < 1e-12 ? 0 : Math.max(0, s) / len;
      return [pts[i][0] + (pts[i + 1][0] - pts[i][0]) * t, pts[i][1] + (pts[i + 1][1] - pts[i][1]) * t];
    }
    s -= len;
  }
  return pts[pts.length - 1];
}

/** The way a road leaves `from` (its first or last point), as a unit vector: towards the point a tile or so on. */
function leaving(pts: Pt[], atStart: boolean): Pt {
  const p = atStart ? pts : [...pts].reverse();
  const q = pointAt(p, Math.min(2, polyLength(p)));
  const dx = q[0] - p[0][0], dy = q[1] - p[0][1], l = Math.hypot(dx, dy) || 1;
  return [dx / l, dy / l];
}

const round = (p: Pt): Pt => [Math.round(p[0] * 100) / 100, Math.round(p[1] * 100) / 100];
const ease = (u: number): number => u * u * (3 - 2 * u);

export function sameEnds(a: Pt, b: Pt, x: Pt, y: Pt, tol = MATCH): boolean {
  return (dist(a, x) <= tol && dist(b, y) <= tol) || (dist(a, y) <= tol && dist(b, x) <= tol);
}

// --- The rubber band -------------------------------------------------------------------------------

/**
 * A road with one end moved by `delta`: every point within `radius` (along the road) of that end moves by a
 * share easing from 1 at the end to 0 at the radius, so the road bends smoothly after the handle.
 */
export function pullEnd(pts: Pt[], atStart: boolean, delta: Pt, radius = FALLOFF): Pt[] {
  const p = atStart ? pts : [...pts].reverse();
  const out: Pt[] = [];
  let s = 0;
  for (let i = 0; i < p.length; i++) {
    if (i > 0) s += dist(p[i - 1], p[i]);
    const share = i === 0 ? 1 : s >= radius || i === p.length - 1 ? 0 : ease(1 - s / radius);
    out.push(round([p[i][0] + delta[0] * share, p[i][1] + delta[1] * share]));
  }
  return atStart ? out : out.reverse();
}

/** A road with its `index`th point moved to `to`; the points either side within `radius` follow, easing off. Its ends stay. */
export function pullBend(pts: Pt[], index: number, to: Pt, radius = FALLOFF): Pt[] {
  const delta: Pt = [to[0] - pts[index][0], to[1] - pts[index][1]];
  const along: number[] = [0];
  for (let i = 1; i < pts.length; i++) along.push(along[i - 1] + dist(pts[i - 1], pts[i]));
  return pts.map((q, i) => {
    if (i === 0 || i === pts.length - 1) return q;
    if (i === index) return round(to);
    const s = Math.abs(along[i] - along[index]);
    const share = s >= radius ? 0 : ease(1 - s / radius);
    return round([q[0] + delta[0] * share, q[1] + delta[1] * share]);
  });
}

/** A new bend on a road's `seg`th piece, at `at` (the ghost handle half way along it). */
export function withBend(pts: Pt[], seg: number, at: Pt): Pt[] {
  return [...pts.slice(0, seg + 1), round(at), ...pts.slice(seg + 1)];
}

/** Each road meeting at `node` as it bends while the junction is dragged to `to` (what's drawn every frame). */
export function dragNode(town: Town, node: number, to: Pt, radius = FALLOFF): Map<number, Pt[]> {
  const at = town.nodes.find((n) => n.id === node)?.at;
  const out = new Map<number, Pt[]>();
  if (!at) return out;
  const delta: Pt = [to[0] - at[0], to[1] - at[1]];
  for (const r of town.roads) {
    if (r.from === node && r.to === node) continue;
    if (r.from === node) out.set(r.id, pullEnd(r.pts, true, delta, radius));
    else if (r.to === node) out.set(r.id, pullEnd(r.pts, false, delta, radius));
  }
  return out;
}

// --- Skeleton edits --------------------------------------------------------------------------------

/** A skeleton with every list there (a draft's may have come with some missing). */
export function normalize(s: Partial<Skeleton> | null | undefined): Skeleton {
  return {
    v: 1,
    ...(s?.center ? { center: s.center } : {}),
    ...(s?.mains ? { mains: s.mains.map((r) => ({ ...r, pts: r.pts.map((p) => [p[0], p[1]] as Pt) })) } : {}),
    streets: (s?.streets ?? []).map((r) => ({ ...r, pts: r.pts.map((p) => [p[0], p[1]] as Pt) })),
    removed: (s?.removed ?? []).map(([a, b]) => [a, b] as [Pt, Pt]),
    roads: (s?.roads ?? []).map((r) => ({ ...r })),
    junctions: (s?.junctions ?? []).map((j) => ({ ...j })),
  };
}

const clone = (s: Skeleton): Skeleton => normalize(s);

/** Every main road as the town has it now: what `mains` becomes the first time one's changed. */
export function snapshotMains(town: Town): SkeletonRoad[] {
  return town.roads.filter((r) => r.kind === 'main').map((r) => ({ pts: r.pts.map(round), kind: 'main' as const, cls: r.cls }));
}

/** The skeleton road (in `list`) a town road is part of: both its ends on that road's line. */
function containing(list: SkeletonRoad[], a: Pt, b: Pt): number {
  let best = -1, bestD = Infinity;
  list.forEach((r, i) => {
    if (r.pts.length < 2) return;
    const d = Math.max(closestOn(a, r.pts).d, closestOn(b, r.pts).d);
    if (d <= ON_LINE && d < bestD) {
      bestD = d;
      best = i;
    }
  });
  return best;
}

/** `pts` with the stretch between the points nearest `a` and `b` replaced by `span` (from `a`'s end to `b`'s). */
function replaceSpan(pts: Pt[], a: Pt, b: Pt, span: Pt[] | null): Pt[][] {
  let ca = closestOn(a, pts), cb = closestOn(b, pts);
  let s = span ? [...span] : null;
  if (ca.s > cb.s) {
    [ca, cb] = [cb, ca];
    if (s) s.reverse();
  }
  const before = [...pts.slice(0, ca.seg + 1), ca.point];
  const after = [cb.point, ...pts.slice(cb.seg + 1)];
  const tidy = (q: Pt[]) => q.filter((p, i) => i === 0 || dist(p, q[i - 1]) > 1e-6);
  if (s) return [tidy([...before.slice(0, -1), ...s, ...after.slice(1)])];
  // Taken out: what's left either side, each piece long enough to be a road.
  return [tidy(before), tidy(after)].filter((q) => q.length >= 2 && polyLength(q) > 0.5);
}

/** The ends a town road might be found by in the skeleton: its own, or with one end where the junction was moved to. */
function endsToTry(road: TownRoad, moved?: { node: number; to: Pt }): [Pt, Pt][] {
  const a = road.pts[0], b = road.pts[road.pts.length - 1];
  const out: [Pt, Pt][] = [[a, b]];
  if (moved && road.from === moved.node) out.push([moved.to, b]);
  if (moved && road.to === moved.node) out.push([a, moved.to]);
  return out;
}

/**
 * The skeleton with one of the town's roads laid along `pts` (null: taken out): a main road in `mains` (every
 * main road snapshotted first, if they weren't yet); a street pinned in `streets`, its old ends in `removed` if it
 * was the generator's.
 */
export function withRoad(skel: Skeleton, town: Town, road: TownRoad, pts: Pt[] | null, moved?: { node: number; to: Pt }): Skeleton {
  const s = clone(skel);
  const rounded = pts ? pts.map(round) : null;
  if (road.kind === 'main') {
    s.mains = s.mains ?? snapshotMains(town);
    for (const [a, b] of endsToTry(road, moved)) {
      const i = containing(s.mains, a, b);
      if (i < 0) continue;
      const pieces = replaceSpan(s.mains[i].pts, a, b, rounded);
      s.mains.splice(i, 1, ...pieces.map((q) => ({ ...s.mains![i], pts: q })));
      return s;
    }
    // Not in the snapshot (it shouldn't happen): added as it is now.
    if (rounded) s.mains.push({ pts: rounded, kind: 'main', cls: road.cls });
    return s;
  }
  for (const [a, b] of endsToTry(road, moved)) {
    const i = containing(s.streets, a, b);
    if (i < 0) continue;
    const pieces = replaceSpan(s.streets[i].pts, a, b, rounded);
    s.streets.splice(i, 1, ...pieces.map((q) => ({ ...s.streets[i], pts: q })));
    return s;
  }
  // The generator's street: pinned where it now goes, and not laid where it was.
  const a = round(road.pts[0]), b = round(road.pts[road.pts.length - 1]);
  if (!s.removed.some(([x, y]) => sameEnds(x, y, a, b))) s.removed.push([a, b]);
  if (rounded) s.streets.push({ pts: rounded, kind: road.kind === 'path' ? 'path' : 'street' });
  return s;
}

/** A junction dragged to `to`: every road meeting there bent after it (the rubber band), each laid in the skeleton. */
export function moveNode(skel: Skeleton, town: Town, node: number, to: Pt, radius = FALLOFF): Skeleton {
  const bent = dragNode(town, node, to, radius);
  let s = skel;
  for (const [id, pts] of bent) {
    const road = town.roads.find((r) => r.id === id)!;
    s = withRoad(s, town, road, pts, { node, to });
  }
  return s;
}

/** A road's bend dragged to `to`. */
export function moveBend(skel: Skeleton, town: Town, road: number, index: number, to: Pt, radius = FALLOFF): Skeleton {
  const r = town.roads.find((x) => x.id === road);
  if (!r || index <= 0 || index >= r.pts.length - 1) return skel;
  return withRoad(skel, town, r, pullBend(r.pts, index, to, radius));
}

/** A road taken out. */
export function deleteRoad(skel: Skeleton, town: Town, road: number): Skeleton {
  const r = town.roads.find((x) => x.id === road);
  return r ? withRoad(skel, town, r, null) : skel;
}

/** The bullseye: the town's centre moved. */
export function moveCenter(skel: Skeleton, at: Pt): Skeleton {
  return { ...clone(skel), center: round(at) };
}

/** A road drawn through `pts`: an arterial is a main road (every main road snapshotted first), anything else a street. */
export function drawRoad(skel: Skeleton, town: Town, pts: Pt[], cls: RoadClass): Skeleton {
  const s = clone(skel);
  if (pts.length < 2 || polyLength(pts) < 1) return s;
  const rounded = pts.map(round);
  if (cls === 'arterial') {
    s.mains = s.mains ?? snapshotMains(town);
    s.mains.push({ pts: rounded, kind: 'main', cls });
  } else s.streets.push({ pts: rounded, kind: 'street', ...(cls === 'collector' ? { cls } : {}) });
  return s;
}

/** A road split at `at` into two that meet there (a junction to draw from). */
export function splitRoad(skel: Skeleton, town: Town, road: number, at: Pt): Skeleton {
  const r = town.roads.find((x) => x.id === road);
  if (!r) return skel;
  const c = closestOn(at, r.pts);
  const p = round(c.point);
  const first = [...r.pts.slice(0, c.seg + 1), p];
  const second = [p, ...r.pts.slice(c.seg + 1)];
  if (polyLength(first) < 1 || polyLength(second) < 1) return skel;
  // Laid as one road through the new point, then cut there: two skeleton roads sharing an end.
  let s = withRoad(skel, town, r, [...first.slice(0, -1), ...second]);
  const list = r.kind === 'main' ? s.mains! : s.streets;
  const i = containing(list, r.pts[0], r.pts[r.pts.length - 1]);
  if (i >= 0) {
    const pts = list[i].pts;
    const k = pts.findIndex((q) => dist(q, p) < 1e-6);
    if (k > 0 && k < pts.length - 1) {
      s = clone(s);
      const l = r.kind === 'main' ? s.mains! : s.streets;
      l.splice(i, 1, { ...l[i], pts: pts.slice(0, k + 1) }, { ...l[i], pts: pts.slice(k) });
    }
  }
  return s;
}

type OverridePatch = Partial<Omit<RoadOverride, 'a' | 'b'>>;

/** What the side panel set on a road (by its two ends): class, lanes, divided, one way, lock. `undefined` clears one. */
export function setRoad(skel: Skeleton, a: Pt, b: Pt, patch: OverridePatch): Skeleton {
  const s = clone(skel);
  let o = s.roads.find((r) => sameEnds(r.a, r.b, a, b));
  if (!o) {
    o = { a: round(a), b: round(b) };
    s.roads.push(o);
  }
  for (const [k, v] of Object.entries(patch) as [keyof OverridePatch, unknown][]) {
    if (v === undefined || v === null || v === 0 || v === false) delete o[k];
    else (o as unknown as Record<string, unknown>)[k] = v;
  }
  if (Object.keys(o).length === 2) s.roads = s.roads.filter((r) => r !== o);
  return s;
}

export function roadOverride(skel: Skeleton, a: Pt, b: Pt): RoadOverride | undefined {
  return skel.roads.find((r) => sameEnds(r.a, r.b, a, b));
}

/** A junction's control (within 1.5 tiles); "auto" takes the override away. */
export function setJunction(skel: Skeleton, at: Pt, control: Control): Skeleton {
  const s = clone(skel);
  s.junctions = s.junctions.filter((j) => dist(j.at, at) > 1.5);
  if (control !== 'auto') s.junctions.push({ at: round(at), control });
  return s;
}

export function junctionOverride(skel: Skeleton, at: Pt): JunctionOverride | undefined {
  return skel.junctions.find((j) => dist(j.at, at) <= 1.5);
}

// --- Snapping and warnings -------------------------------------------------------------------------

export interface Snap {
  p: Pt;
  /** Let go here and it merges with this junction. */
  merge?: number;
  /** Let go here and it joins this road as a T. */
  join?: number;
  /** Lines to draw: what it snapped to. */
  guides: [Pt, Pt][];
}

/** Where a dragged junction (or a road being drawn's end) lands: onto a junction, onto a road, square or straight on. */
export function snapNode(town: Town, node: number | null, p: Pt): Snap {
  // Onto a junction.
  let best: { id: number; d: number } | null = null;
  for (const n of town.nodes) {
    if (n.id === node) continue;
    const d = dist(n.at, p);
    if (d <= MERGE && (!best || d < best.d)) best = { id: n.id, d };
  }
  if (best) return { p: town.nodes.find((n) => n.id === best!.id)!.at, merge: best.id, guides: [] };
  // Onto a road it doesn't already meet.
  let road: { id: number; q: Pt; d: number } | null = null;
  for (const r of town.roads) {
    if (node !== null && (r.from === node || r.to === node)) continue;
    const c = closestOn(p, r.pts);
    if (c.d <= JOIN && (!road || c.d < road.d)) road = { id: r.id, q: c.point, d: c.d };
  }
  if (road) return { p: road.q, join: road.id, guides: [] };
  // Square or straight on, against the points the roads meeting here come from.
  const from: Pt[] = [];
  if (node !== null)
    for (const r of town.roads) {
      if (r.from === node && r.pts.length > 1) from.push(r.pts[Math.min(1, r.pts.length - 1)]);
      else if (r.to === node && r.pts.length > 1) from.push(r.pts[Math.max(0, r.pts.length - 2)]);
    }
  const guides: [Pt, Pt][] = [];
  let q: Pt = p;
  // Straight through: on the line between two of them.
  for (let i = 0; i < from.length; i++)
    for (let k = i + 1; k < from.length; k++) {
      const [a, b] = [from[i], from[k]];
      const c = closestOn(q, [a, b]);
      const angle = Math.atan2(c.d, Math.min(dist(q, a), dist(q, b)) || 1);
      if (c.t > 0.05 && c.t < 0.95 && angle < SNAP_ANGLE) {
        q = c.point;
        guides.push([a, b]);
      }
    }
  // Square: level or plumb with one of them.
  for (const a of from) {
    const dx = q[0] - a[0], dy = q[1] - a[1];
    const ang = Math.atan2(Math.abs(dy), Math.abs(dx));
    if (ang < SNAP_ANGLE) {
      q = [q[0], a[1]];
      guides.push([a, q]);
    } else if (Math.PI / 2 - ang < SNAP_ANGLE) {
      q = [a[0], q[1]];
      guides.push([a, q]);
    }
  }
  return { p: round(q), guides };
}

/** Whether a tile is water (the town's bitmap). */
export function decodeWater(town: Town): Uint8Array {
  const out = new Uint8Array(town.width * town.height);
  if (!town.water) return out;
  const bin = typeof atob === 'function' ? atob(town.water) : Buffer.from(town.water, 'base64').toString('binary');
  for (let i = 0; i < out.length; i++) {
    const byte = bin.charCodeAt(i >> 3);
    out[i] = (byte >> (i & 7)) & 1;
  }
  return out;
}

export function isWater(town: Town, water: Uint8Array, p: Pt): boolean {
  const x = Math.floor(p[0]), y = Math.floor(p[1]);
  return x >= 0 && y >= 0 && x < town.width && y < town.height && water[y * town.width + x] === 1;
}

/** What's wrong where a junction's being dragged: too near the next junction along a road, a sharp corner, water. */
export function dragWarnings(town: Town, node: number, p: Pt, bent: Map<number, Pt[]>, water: Uint8Array): string[] {
  const out: string[] = [];
  const legs: Pt[] = [];
  for (const [id, pts] of bent) {
    const r = town.roads.find((x) => x.id === id)!;
    const len = polyLength(pts);
    const apart = r.kind === 'main' ? APART_MAIN : APART_STREET;
    const other = town.nodes.find((n) => n.id === (r.from === node ? r.to : r.from));
    if (other && (other.legs >= 3 || other.legs === 1) && len < apart) out.push(`${len.toFixed(1)} tiles from the next junction along ${r.name || 'the road'} (under ${apart})`);
    legs.push(leaving(pts, r.from === node));
  }
  for (let i = 0; i < legs.length; i++)
    for (let k = i + 1; k < legs.length; k++) {
      const a = Math.acos(Math.max(-1, Math.min(1, legs[i][0] * legs[k][0] + legs[i][1] * legs[k][1])));
      if (a < LEAST_ANGLE) {
        out.push(`two roads meet at ${Math.round((a * 180) / Math.PI)}° (under 60°)`);
        i = legs.length;
        break;
      }
    }
  if (isWater(town, water, p)) out.push('in the water');
  return out;
}

// --- Rebasing --------------------------------------------------------------------------------------

const key = (v: unknown): string => JSON.stringify(v);

/**
 * A draft whose base revision is behind: its own changes (from `base` to `draft`) laid over the `current`
 * revision's skeleton. Where both changed the same thing, the draft wins and it's listed as a clash.
 */
export function rebase(base: Skeleton, draft: Skeleton, current: Skeleton): { skeleton: Skeleton; clashes: string[] } {
  const [b, d, c] = [normalize(base), normalize(draft), normalize(current)];
  const out = clone(c);
  const clashes: string[] = [];
  if (key(d.center) !== key(b.center)) {
    if (key(c.center) !== key(b.center) && key(c.center) !== key(d.center)) clashes.push('the town centre (yours kept)');
    if (d.center) out.center = d.center;
    else delete out.center;
  }
  if (key(d.mains) !== key(b.mains)) {
    if (key(c.mains) !== key(b.mains) && key(c.mains) !== key(d.mains)) clashes.push('the main roads (yours kept)');
    if (d.mains) out.mains = d.mains;
    else delete out.mains;
  }
  // Lists: what the draft added, added; what it took away, taken away.
  const merge = <T>(field: 'streets' | 'removed' | 'roads' | 'junctions', id: (x: T) => string, label: (x: T) => string) => {
    const was = new Set((b[field] as T[]).map(id));
    const now = new Set((d[field] as T[]).map(id));
    let list = out[field] as T[];
    list = list.filter((x) => !(was.has(id(x)) && !now.has(id(x))));
    for (const x of d[field] as T[]) {
      if (was.has(id(x))) continue;
      if (list.some((y) => id(y) === id(x))) continue;
      list.push(x);
    }
    // The same road or junction set differently by both.
    for (const x of d[field] as T[]) {
      const same = (c[field] as T[]).find((y) => label(y) === label(x) && id(y) !== id(x) && !was.has(id(y)));
      if (same && !was.has(id(x))) {
        clashes.push(`${field === 'junctions' ? 'the junction' : 'the road'} at ${label(x)} (yours kept)`);
        list = list.filter((y) => id(y) !== id(same));
      }
    }
    (out as unknown as Record<string, T[]>)[field] = list;
  };
  const at = (p: Pt) => `${Math.round(p[0])},${Math.round(p[1])}`;
  merge<SkeletonRoad>('streets', key, (r) => at(r.pts[0]) + '–' + at(r.pts[r.pts.length - 1]));
  merge<[Pt, Pt]>('removed', key, (r) => at(r[0]) + '–' + at(r[1]));
  merge<RoadOverride>('roads', key, (r) => at(r.a) + '–' + at(r.b));
  merge<JunctionOverride>('junctions', key, (j) => at(j.at));
  return { skeleton: out, clashes };
}

/** How many things a skeleton changes, roughly (for the toolbar). */
export function changeCount(s: Skeleton): number {
  return (s.center ? 1 : 0) + (s.mains ? 1 : 0) + s.streets.length + s.removed.length + s.roads.length + s.junctions.length;
}

// The edit screen's skeleton (docs/street-editor-api.md, "The skeleton"): the downtown bullseye, and the
// regions an editor has asked to be made again (the blocks they picked, a salt, and what they'd like there).
// Also picking blocks (a block's middle, toggling, a box, finding the new blocks inside an old area once a
// plan's come back), and rebasing a draft. A skeleton from an older draft may still carry `mains`, `streets`,
// `removed`, `roads` and `junctions`; they're kept as they are, but nothing here makes new ones.
// Pure: no DOM, so the tests run it as it is.

import type { JunctionOverride, PlanAnswer, Pt, Region, RoadOverride, Skeleton, SkeletonRoad, TownBlock, Want } from './types.ts';

/** How near a saved end has to be to a road's end to be it (tiles). */
export const MATCH = 1;
/** How far from the town's centre the downtown may go, as a share of the town's radius. */
export const DOWNTOWN_REACH = 0.7;

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

const round = (p: Pt): Pt => [Math.round(p[0] * 100) / 100, Math.round(p[1] * 100) / 100];

export function sameEnds(a: Pt, b: Pt, x: Pt, y: Pt, tol = MATCH): boolean {
  return (dist(a, x) <= tol && dist(b, y) <= tol) || (dist(a, y) <= tol && dist(b, x) <= tol);
}

/** Whether a point's inside a polygon (its edges crossed an odd number of times going right). */
export function inPoly(p: Pt, poly: Pt[]): boolean {
  let inside = false;
  for (let i = 0, k = poly.length - 1; i < poly.length; k = i++) {
    const [a, b] = [poly[i], poly[k]];
    if (a[1] > p[1] !== b[1] > p[1] && p[0] < ((b[0] - a[0]) * (p[1] - a[1])) / (b[1] - a[1]) + a[0]) inside = !inside;
  }
  return inside;
}

/**
 * A block's middle: its centroid, or, for a block bent round so far that the centroid's outside it, the middle
 * of the widest stretch of it level with the centroid. What a box and a re-selection go by.
 */
export function middle(poly: Pt[]): Pt {
  let a = 0, x = 0, y = 0;
  for (let i = 0, k = poly.length - 1; i < poly.length; k = i++) {
    const c = poly[k][0] * poly[i][1] - poly[i][0] * poly[k][1];
    a += c;
    x += (poly[k][0] + poly[i][0]) * c;
    y += (poly[k][1] + poly[i][1]) * c;
  }
  let m: Pt;
  if (Math.abs(a) < 1e-9) {
    const n = poly.length || 1;
    m = [poly.reduce((s, p) => s + p[0], 0) / n, poly.reduce((s, p) => s + p[1], 0) / n];
  } else m = [x / (3 * a), y / (3 * a)];
  if (poly.length < 3 || inPoly(m, poly)) return m;
  const xs: number[] = [];
  for (let i = 0, k = poly.length - 1; i < poly.length; k = i++) {
    const [p, q] = [poly[i], poly[k]];
    if (p[1] > m[1] !== q[1] > m[1]) xs.push(p[0] + ((m[1] - p[1]) * (q[0] - p[0])) / (q[1] - p[1]));
  }
  xs.sort((u, v) => u - v);
  let best: Pt = m, wide = -1;
  for (let i = 0; i + 1 < xs.length; i += 2)
    if (xs[i + 1] - xs[i] > wide) {
      wide = xs[i + 1] - xs[i];
      best = [(xs[i] + xs[i + 1]) / 2, m[1]];
    }
  return best;
}

// --- Picking blocks --------------------------------------------------------------------------------

const key = (v: unknown): string => JSON.stringify(v);

/** The selection with one block's polygon added, or taken out if it was in it. */
export function toggleBlock(sel: Pt[][], poly: Pt[]): Pt[][] {
  const k = key(poly);
  return sel.some((p) => key(p) === k) ? sel.filter((p) => key(p) !== k) : [...sel, poly];
}

/** Whether a block's polygon is in the selection. */
export function isSelected(sel: Pt[][], poly: Pt[]): boolean {
  const k = key(poly);
  return sel.some((p) => key(p) === k);
}

/** The blocks whose middle is inside the box between two corners (either way round). */
export function blocksInBox(blocks: TownBlock[], a: Pt, b: Pt): Pt[][] {
  const [x0, x1] = [Math.min(a[0], b[0]), Math.max(a[0], b[0])];
  const [y0, y1] = [Math.min(a[1], b[1]), Math.max(a[1], b[1])];
  return blocks
    .map((bl) => bl.poly)
    .filter((poly) => {
      const m = middle(poly);
      return m[0] >= x0 && m[0] <= x1 && m[1] >= y0 && m[1] <= y1;
    });
}

/** The selection with more blocks added (a box's), each once. */
export function addBlocks(sel: Pt[][], polys: Pt[][]): Pt[][] {
  const out = [...sel];
  for (const p of polys) if (!isSelected(out, p)) out.push(p);
  return out;
}

/**
 * The blocks of a new answer whose middle is inside the area the old blocks covered: once a region's been
 * planned its blocks are new ones, so this picks them again (and pressing Regenerate re-rolls the same area).
 */
export function reselect(blocks: TownBlock[], area: Pt[][]): Pt[][] {
  if (!area.length) return [];
  return blocks.map((b) => b.poly).filter((poly) => {
    const m = middle(poly);
    return area.some((a) => inPoly(m, a));
  });
}

// --- Skeleton edits --------------------------------------------------------------------------------

const clonePts = (pts: Pt[]): Pt[] => pts.map((p) => [p[0], p[1]] as Pt);

/** A skeleton tidied and copied: the lists it has, each copied; empty ones left out (they mean the same as none). */
export function normalize(s: Partial<Skeleton> | null | undefined): Skeleton {
  const out: Skeleton = { v: 1 };
  if (!s) return out;
  if (s.center) out.center = [s.center[0], s.center[1]];
  const roads = (l: SkeletonRoad[]) => l.map((r) => ({ ...r, pts: clonePts(r.pts) }));
  if (s.mains) out.mains = roads(s.mains);
  if (s.streets?.length) out.streets = roads(s.streets);
  if (s.removed?.length) out.removed = s.removed.map(([a, b]) => [[a[0], a[1]], [b[0], b[1]]] as [Pt, Pt]);
  if (s.roads?.length) out.roads = s.roads.map((r) => ({ ...r }));
  if (s.junctions?.length) out.junctions = s.junctions.map((j) => ({ ...j }));
  if (s.regions?.length) out.regions = s.regions.map((r) => ({ blocks: r.blocks.map(clonePts), salt: r.salt, want: { ...NO_WANT, ...r.want } }));
  return out;
}

export const NO_WANT: Want = { roundabout: false, park: false, school: false, shops: false, houses: false };

/** Whether two skeletons say the same thing. */
export function sameSkeleton(a: Skeleton, b: Skeleton): boolean {
  return key(normalize(a)) === key(normalize(b));
}

/** Where the bullseye may be: no further than 0.7 of the town's radius from its centre. */
export function clampDowntown(p: Pt, center: Pt, radius: number, reach = DOWNTOWN_REACH): Pt {
  const max = Math.max(0, radius * reach);
  const d = dist(p, center);
  if (d <= max) return p;
  return [center[0] + ((p[0] - center[0]) / d) * max, center[1] + ((p[1] - center[1]) / d) * max];
}

/** The bullseye moved: the downtown, kept inside its reach of the town's centre when that's known. */
export function moveDowntown(skel: Skeleton, at: Pt, town?: { center: Pt; townRadius: number } | null): Skeleton {
  const p = town ? clampDowntown(at, town.center, town.townRadius) : at;
  return { ...normalize(skel), center: round(p) };
}

/** A salt for a new region: any non-negative whole number. */
export const newSalt = (): number => Math.floor(Math.random() * 0x7fffffff);

/** A region asked for: the selected blocks' polygons as they are, a salt, and what's ticked. Appended after the rest. */
export function addRegion(skel: Skeleton, blocks: Pt[][], want: Want, salt: number = newSalt()): Skeleton {
  const s = normalize(skel);
  if (!blocks.length) return s;
  const region: Region = { blocks: blocks.map(clonePts), salt, want: { ...NO_WANT, ...want } };
  return { ...s, regions: [...(s.regions ?? []), region] };
}

/** Whether a tile is water (the town's bitmap). */
export function decodeWater(town: { width: number; height: number; water: string }): Uint8Array {
  const out = new Uint8Array(town.width * town.height);
  if (!town.water) return out;
  const bin = typeof atob === 'function' ? atob(town.water) : Buffer.from(town.water, 'base64').toString('binary');
  for (let i = 0; i < out.length; i++) {
    const byte = bin.charCodeAt(i >> 3);
    out[i] = (byte >> (i & 7)) & 1;
  }
  return out;
}

// --- Rebasing --------------------------------------------------------------------------------------

type ListField = 'streets' | 'removed' | 'roads' | 'junctions' | 'regions';

/**
 * A draft whose base revision is behind: its own changes (from `base` to `draft`) laid over the `current`
 * revision's skeleton. Where both changed the same thing, the draft wins and it's listed as a clash. The
 * draft's new regions go after the current revision's, so they're made over it.
 */
export function rebase(base: Skeleton, draft: Skeleton, current: Skeleton): { skeleton: Skeleton; clashes: string[] } {
  const [b, d, c] = [normalize(base), normalize(draft), normalize(current)];
  const out = normalize(c);
  const clashes: string[] = [];
  if (key(d.center) !== key(b.center)) {
    if (key(c.center) !== key(b.center) && key(c.center) !== key(d.center)) clashes.push('downtown (yours kept)');
    if (d.center) out.center = d.center;
    else delete out.center;
  }
  if (key(d.mains) !== key(b.mains)) {
    if (key(c.mains) !== key(b.mains) && key(c.mains) !== key(d.mains)) clashes.push('the main roads (yours kept)');
    if (d.mains) out.mains = d.mains;
    else delete out.mains;
  }
  // Lists: what the draft added, added; what it took away, taken away.
  const merge = <T>(field: ListField, label: (x: T) => string, what: string) => {
    const [bl, dl, cl] = [(b[field] ?? []) as T[], (d[field] ?? []) as T[], (c[field] ?? []) as T[]];
    const was = new Set(bl.map(key));
    const now = new Set(dl.map(key));
    let list = ((out[field] ?? []) as T[]).filter((x) => !(was.has(key(x)) && !now.has(key(x))));
    for (const x of dl) {
      if (was.has(key(x)) || list.some((y) => key(y) === key(x))) continue;
      list.push(x);
    }
    // The same thing set differently by both (regions never clash: a later one's simply made over an earlier).
    if (field !== 'regions')
      for (const x of dl) {
        if (was.has(key(x))) continue;
        const same = cl.find((y) => label(y) === label(x) && key(y) !== key(x) && !was.has(key(y)));
        if (same) {
          clashes.push(`${what} at ${label(x)} (yours kept)`);
          list = list.filter((y) => key(y) !== key(same));
        }
      }
    (out as unknown as Record<string, T[]>)[field] = list;
  };
  const at = (p: Pt) => `${Math.round(p[0])},${Math.round(p[1])}`;
  merge<SkeletonRoad>('streets', (r) => at(r.pts[0]) + '–' + at(r.pts[r.pts.length - 1]), 'the road');
  merge<[Pt, Pt]>('removed', (r) => at(r[0]) + '–' + at(r[1]), 'the road');
  merge<RoadOverride>('roads', (r) => at(r.a) + '–' + at(r.b), 'the road');
  merge<JunctionOverride>('junctions', (j) => at(j.at), 'the junction');
  merge<Region>('regions', () => '', 'the area');
  return { skeleton: normalize(out), clashes };
}

/** How many things a skeleton changes from `base` (the revision it began from), roughly (for the panel's head). */
export function changeCount(s: Skeleton, base: Skeleton = normalize(null)): number {
  const [n, b] = [normalize(s), normalize(base)];
  const diff = (x: unknown[] = [], y: unknown[] = []) => {
    const was = new Set(y.map(key)), now = new Set(x.map(key));
    return x.filter((v) => !was.has(key(v))).length + y.filter((v) => !now.has(key(v))).length;
  };
  return (
    (key(n.center) !== key(b.center) ? 1 : 0) +
    (key(n.mains) !== key(b.mains) ? 1 : 0) +
    diff(n.streets, b.streets) +
    diff(n.removed, b.removed) +
    diff(n.roads, b.roads) +
    diff(n.junctions, b.junctions) +
    diff(n.regions, b.regions)
  );
}

/** Whether Commit's open: the draft says something the revision it's from doesn't, and no plan's on its way. */
export function canCommit(s: Skeleton, base: Skeleton, pending: boolean): boolean {
  return !pending && changeCount(s, base) > 0;
}

/** What the plan said of the latest region (the last in its skeleton), if it said anything. */
export function latestRegionNote(a: PlanAnswer, planned: Skeleton): { ok: boolean; text: string } | null {
  const last = (planned.regions?.length ?? 0) - 1;
  if (last < 0) return null;
  const r = a.regions?.find((x) => x.index === last);
  return r?.text ? { ok: r.ok, text: r.text } : null;
}

/** A warning when the draft houses fewer people than the town does now (no list of houses lost: just the net loss). */
export function populationNote(a: PlanAnswer): { ok: boolean; text: string } | null {
  const p = a.changes?.population;
  if (!p || p[1] >= p[0]) return null;
  return { ok: false, text: `Population down by ${(p[0] - p[1]).toLocaleString()} (${p[0].toLocaleString()} → ${p[1].toLocaleString()})` };
}

/** The one line under the boxes: what the newest area couldn't have, else a fall in population, else the area's note. */
export function noteOf(a: PlanAnswer, planned: Skeleton): { ok: boolean; text: string } | null {
  const region = latestRegionNote(a, planned);
  if (region && !region.ok) return region;
  return populationNote(a) ?? region;
}

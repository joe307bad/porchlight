// Street names on Manage's map: where to write each one (along the longest
// straight stretch it has, once a town), and which street a point is on (the
// right-click menu's "near Elm Street"). The names are the game's own
// (Explore/Names.cs), sent with each town's streets (CountyMap roadNames).

import type { CountyMap } from './api.ts';

type Town = CountyMap['towns'][number];

/** Where a street's name goes: the middle of its longest stretch (county tiles), the stretch's angle and length. */
export interface StreetLabel {
  name: string;
  /** 0 a main road, 1 a street (paths aren't labelled). */
  kind: number;
  x: number;
  y: number;
  /** Radians, turned so the text never reads upside down. */
  angle: number;
  /** The stretch's length (tiles): the name's only written where it fits. */
  length: number;
}

/** One label for each named street in each town, at its longest stretch; main roads first. */
export function streetLabels(towns: Town[]): StreetLabel[] {
  const out: StreetLabel[] = [];
  for (const t of towns) {
    if (!t.roads || !t.roadNames) continue;
    const best = new Map<string, StreetLabel>();
    t.roads.forEach((r, i) => {
      const name = t.roadNames?.[i];
      const kind = r[0];
      if (!name || kind > 1) return;
      for (let k = 2; k + 3 < r.length; k += 2) {
        const ax = r[k], ay = r[k + 1], bx = r[k + 2], by = r[k + 3];
        const length = Math.hypot(bx - ax, by - ay);
        const had = best.get(name);
        if (had && had.length >= length) continue;
        let angle = Math.atan2(by - ay, bx - ax);
        if (angle > Math.PI / 2) angle -= Math.PI;
        else if (angle < -Math.PI / 2) angle += Math.PI;
        best.set(name, { name, kind, x: t.x0 + (ax + bx) / 2, y: t.y0 + (ay + by) / 2, angle, length });
      }
    });
    out.push(...best.values());
  }
  return out.sort((a, b) => a.kind - b.kind || b.length - a.length);
}

/** The named street nearest a point in a town (its own tiles), within `within` tiles; null if none. */
export function nearestStreet(t: Town, x: number, y: number, within = 6): string | null {
  if (!t.roads || !t.roadNames) return null;
  let best: string | null = null;
  let bestD = within;
  t.roads.forEach((r, i) => {
    const name = t.roadNames?.[i];
    if (!name) return;
    for (let k = 2; k + 3 < r.length; k += 2) {
      const d = toSegment(x, y, r[k], r[k + 1], r[k + 2], r[k + 3]);
      if (d < bestD) {
        bestD = d;
        best = name;
      }
    }
  });
  return best;
}

function toSegment(px: number, py: number, ax: number, ay: number, bx: number, by: number): number {
  const dx = bx - ax, dy = by - ay;
  const len2 = dx * dx + dy * dy;
  const u = len2 > 0 ? Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / len2)) : 0;
  return Math.hypot(px - (ax + u * dx), py - (ay + u * dy));
}

/** The town a county point is in, and the point in its own tiles; null outside every town. */
export function townAt(towns: Town[], x: number, y: number): { town: Town; x: number; y: number } | null {
  for (const t of towns) {
    if (x >= t.x0 && y >= t.y0 && x < t.x0 + t.width && y < t.y0 + t.height) return { town: t, x: x - t.x0, y: y - t.y0 };
  }
  return null;
}

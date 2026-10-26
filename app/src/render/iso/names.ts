// Street names for the explore view. Road edges are joined into streets
// where they carry straight on through a junction (same kind of road, within
// about 40° of straight), and each street gets a name from the town's seed:
// the main road nearest the centre is the High Street, others are Roads and
// Avenues; side streets are named after trees and flowers, and a dead end is
// a Close or a Court, a winding one a Crescent or a Drive; footpaths are
// Walks. A street past the church is Church Street, one over a bridge Bridge
// Street, one by the plaza Market Street. Pure (no Three, no DOM).

import { dist, distToPolyline } from '../../core/geometry';
import { createRng, type Rng } from '../../core/rng';
import type { RoadKind, TownModel, Vec2 } from '../../model/types';
import type { RoadStroke } from './layout';

export interface Street {
  name: string;
  kind: RoadKind;
  /** Road indices (into IsoLayout.roads) making up this street. */
  roads: number[];
}

export interface StreetNames {
  streets: Street[];
  /** Street index for each road (index into IsoLayout.roads). */
  ofRoad: number[];
}

/** How far from a junction to look for a road's direction (tiles). */
const LOOK = 1.2;
/** Two roads carry straight on through a junction when their directions are at least this opposed (cos 140°). */
const STRAIGHT = -0.77;

const MAIN_PREFIXES = ['Station', 'Mill', 'King', 'Queen', 'Long', 'North', 'West', 'East', 'South', 'Abbey', 'Castle', 'Victoria', 'Albert', 'Park', 'Old', 'New', 'Harbour', 'Farm', 'Manor', 'Chapel'];
const MAIN_SUFFIXES = ['Road', 'Road', 'Road', 'Avenue', 'Street'];
const STREET_PREFIXES = [
  'Elm', 'Oak', 'Maple', 'Birch', 'Willow', 'Chestnut', 'Hazel', 'Rowan', 'Holly', 'Ash', 'Linden', 'Juniper', 'Cedar', 'Laurel',
  'Primrose', 'Bluebell', 'Clover', 'Heather', 'Foxglove', 'Poppy', 'Daisy', 'Hawthorn', 'Alder', 'Beech', 'Cherry', 'Plum', 'Pear',
  'Apple', 'Orchard', 'Meadow', 'Brook', 'Spring', 'Fern', 'Ivy', 'Lavender', 'Rose', 'Thistle', 'Sycamore', 'Poplar', 'Larch',
  'Magnolia', 'Mulberry', 'Quince', 'Sorrel', 'Tansy', 'Yarrow', 'Wren', 'Robin', 'Finch', 'Lark', 'Swallow', 'Heron', 'Kestrel',
  'Badger', 'Fox', 'Otter', 'Honey', 'Clay', 'Flint', 'Copper',
];
const STREET_SUFFIXES = ['Street', 'Street', 'Lane', 'Lane', 'Road', 'Way', 'Grove', 'Avenue', 'Row', 'Terrace', 'Gardens'];
const DEAD_END_SUFFIXES = ['Close', 'Close', 'Court', 'Mews'];
const WINDING_SUFFIXES = ['Crescent', 'Drive'];
const PATH_SUFFIXES = ['Walk', 'Walk', 'Path', 'Passage'];

/** Join the roads into streets and name every one. */
export function nameStreets(model: TownModel, roads: readonly RoadStroke[]): StreetNames {
  const ofRoad = joinRoads(roads);
  const groups = new Map<number, number[]>();
  ofRoad.forEach((g, r) => {
    if (!groups.has(g)) groups.set(g, []);
    groups.get(g)!.push(r);
  });
  const streets: Street[] = [...groups.values()].map((rs) => ({ name: '', kind: roads[rs[0]].kind, roads: rs }));
  const index = new Map<number, number>();
  [...groups.keys()].forEach((g, i) => index.set(g, i));
  const out = { streets, ofRoad: ofRoad.map((g) => index.get(g)!) };
  giveNames(model, roads, streets, createRng(`${model.seed}::streets`));
  return out;
}

/** Union roads that carry straight on through their junctions; returns a group id per road. */
function joinRoads(roads: readonly RoadStroke[]): number[] {
  const parent = roads.map((_, i) => i);
  const find = (i: number): number => (parent[i] === i ? i : (parent[i] = find(parent[i])));
  // Every road end at every graph node, with the direction it leaves in.
  const atNode = new Map<number, { road: number; dir: Vec2 }[]>();
  roads.forEach((r, i) => {
    for (const [node, pts] of [[r.from, r.points], [r.to, [...r.points].reverse()]] as const) {
      if (!atNode.has(node)) atNode.set(node, []);
      atNode.get(node)!.push({ road: i, dir: leaving(pts) });
    }
  });
  for (const ends of atNode.values()) {
    const pairs: { a: number; b: number; dot: number }[] = [];
    for (let a = 0; a < ends.length; a++) {
      for (let b = a + 1; b < ends.length; b++) {
        const ra = roads[ends[a].road];
        const rb = roads[ends[b].road];
        if (ends[a].road === ends[b].road || ra.kind !== rb.kind) continue;
        const dot = ends[a].dir.x * ends[b].dir.x + ends[a].dir.y * ends[b].dir.y;
        if (dot < STRAIGHT) pairs.push({ a, b, dot });
      }
    }
    // Straightest pairs first; each road end carries on into at most one other.
    pairs.sort((p, q) => p.dot - q.dot);
    const used = new Set<number>();
    for (const p of pairs) {
      if (used.has(p.a) || used.has(p.b)) continue;
      used.add(p.a);
      used.add(p.b);
      parent[find(ends[p.a].road)] = find(ends[p.b].road);
    }
  }
  return roads.map((_, i) => find(i));
}

/** Unit direction a polyline leaves its first point in, judged a little way along. */
export function leaving(pts: readonly Vec2[]): Vec2 {
  const a = pts[0];
  let b = pts[pts.length - 1];
  for (const p of pts) {
    if (dist(a, p) >= LOOK) {
      b = p;
      break;
    }
  }
  const l = dist(a, b) || 1;
  return { x: (b.x - a.x) / l, y: (b.y - a.y) / l };
}

function giveNames(model: TownModel, roads: readonly RoadStroke[], streets: Street[], rng: Rng): void {
  const used = new Set<string>();
  const usedSpecial = new Set<string>();
  const prefixes = {
    main: rng.shuffle([...MAIN_PREFIXES]),
    street: rng.shuffle([...STREET_PREFIXES]),
  };
  const degree = new Map<number, number>();
  for (const r of roads) for (const n of [r.from, r.to]) degree.set(n, (degree.get(n) ?? 0) + 1);
  const bridged = new Set(model.bridges.map((b) => model.roads.edges.findIndex((e) => e.id === b.roadEdgeId)));
  const church = model.landmarks.find((l) => l.kind === 'church');
  const plaza = model.landmarks.find((l) => l.kind === 'plaza' || l.kind === 'market');
  const near = (s: Street, at: Vec2 | undefined, d: number) => !!at && s.roads.some((r) => distToPolyline(at, roads[r].points) < d);
  const length = (s: Street) => s.roads.reduce((sum, r) => sum + polyLength(roads[r].points), 0);

  // The High Street: the longest main road passing close to the centre.
  const mains = streets.filter((s) => s.kind === 'main');
  const central = mains.filter((s) => near(s, model.center, Math.max(6, model.townRadius * 0.3)));
  const high = (central.length ? central : mains).reduce<Street | null>((best, s) => (!best || length(s) > length(best) ? s : best), null);

  const claim = (s: Street, name: string): boolean => {
    if (used.has(name)) return false;
    used.add(name);
    s.name = name;
    return true;
  };
  // Longest streets first, so the special names go to the ones you'd notice.
  const order = [...streets].sort((a, b) => length(b) - length(a));
  for (const s of order) {
    if (s === high && claim(s, 'High Street')) continue;
    const kind = s.kind;
    const deadEnd = s.roads.some((r) => degree.get(roads[r].from) === 1 || degree.get(roads[r].to) === 1) && length(s) < 14;
    const suffixes =
      kind === 'path' ? PATH_SUFFIXES : kind === 'main' ? MAIN_SUFFIXES : deadEnd ? DEAD_END_SUFFIXES : winding(s, roads) ? WINDING_SUFFIXES : STREET_SUFFIXES;
    const suffix = rng.pick(suffixes);
    // Named for what it passes, where it does.
    const special: string[] = [];
    if (near(s, church?.at, 4)) special.push('Church');
    if (near(s, plaza?.at, 5)) special.push('Market');
    if (s.roads.some((r) => bridged.has(r))) special.push('Bridge');
    const landmark = special.find((p) => !usedSpecial.has(p));
    if (landmark && claim(s, `${landmark} ${suffix}`)) {
      usedSpecial.add(landmark); // one Church Street, not a Church Street and a Church Lane
      continue;
    }
    const pool = kind === 'main' ? prefixes.main : prefixes.street;
    let named = false;
    while (pool.length && !named) named = claim(s, `${pool.shift()} ${suffix}`);
    // Run out of names: make compounds (Elmfield Road, Oakwood Lane).
    for (let k = 0; !named; k++) {
      const base = rng.pick(STREET_PREFIXES);
      const end = ['field', 'wood', 'ley', 'brook', 'hill', 'mead'][k % 6];
      named = claim(s, `${base}${end} ${suffix}`);
    }
  }
}

/** True when a street turns through more than about 100° overall: a Crescent or a Drive. */
function winding(s: Street, roads: readonly RoadStroke[]): boolean {
  let turn = 0;
  for (const r of s.roads) {
    const pts = roads[r].points;
    let own = 0;
    for (let k = 1; k < pts.length - 1; k++) {
      const a = Math.atan2(pts[k].y - pts[k - 1].y, pts[k].x - pts[k - 1].x);
      const b = Math.atan2(pts[k + 1].y - pts[k].y, pts[k + 1].x - pts[k].x);
      let d = b - a;
      while (d > Math.PI) d -= Math.PI * 2;
      while (d < -Math.PI) d += Math.PI * 2;
      own += d;
    }
    turn += Math.abs(own); // roads in a street can run either way, so add up each one's bend
  }
  return Math.abs(turn) > 1.75;
}

function polyLength(pts: readonly Vec2[]): number {
  let s = 0;
  for (let k = 1; k < pts.length; k++) s += dist(pts[k - 1], pts[k]);
  return s;
}

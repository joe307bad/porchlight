// A stand-in for the server and its draft worker (`?mock=1`), so the edit screen can be tried without
// either: a small made-up town grown from a skeleton roughly the way the real generator treats one (main
// roads to the town's centre, a street grid round it, shops and the tall blocks round downtown, houses
// further out), with lanes, pockets and controls roughly as the design stage would choose. A region lays
// a street across some of its blocks (where, by its salt), and makes a park, a school, shops, houses or a
// roundabout there if asked. An older draft's pinned and removed streets are still kept. It's a toy: the
// real answers come from `porchlight-sim --draft`.

import { clampDowntown, closestOn, dist, inPoly, middle, normalize, polyLength, sameEnds } from './skeleton.ts';
import type { BuildingKind, Changes, CommitAnswer, Day, Draft, EditableTown, PlanAnswer, Problem, Pt, RegionResult, Skeleton, Town, TownBuilding, TownJunction, TownRoad, Want } from './types.ts';

const W = 160, H = 160;
const NAMES = ['High Street', 'Elm Street', 'Oak Lane', 'Mill Road', 'Church Street', 'Station Road', 'Park Avenue', 'Brook Lane', 'Maple Road', 'Hill Street', 'Bridge Street', 'Cedar Close', 'Ash Grove', 'Willow Way', 'Kings Road', 'Queens Walk'];
const GATES: Pt[] = [
  [80.5, 0.5],
  [W - 0.5, 80.5],
  [80.5, H - 0.5],
  [0.5, 80.5],
];
const LAKE = { x: 132, y: 128, r: 15 };

interface Seg {
  pts: Pt[];
  kind: 'main' | 'street' | 'path';
  cls?: TownRoad['cls'];
}

const wet = (x: number, y: number) => Math.hypot(x + 0.5 - LAKE.x, y + 0.5 - LAKE.y) < LAKE.r;

function waterBits(): string {
  const bytes = new Uint8Array(Math.ceil((W * H) / 8));
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) if (wet(x, y)) bytes[(y * W + x) >> 3] |= 1 << ((y * W + x) & 7);
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return typeof btoa === 'function' ? btoa(s) : Buffer.from(s, 'binary').toString('base64');
}

const WATER = waterBits();

/** Where two segments cross (strictly inside both), or null. */
function cross(a: Pt, b: Pt, c: Pt, d: Pt): { t: number; u: number; p: Pt } | null {
  const rx = b[0] - a[0], ry = b[1] - a[1], sx = d[0] - c[0], sy = d[1] - c[1];
  const den = rx * sy - ry * sx;
  if (Math.abs(den) < 1e-9) return null;
  const t = ((c[0] - a[0]) * sy - (c[1] - a[1]) * sx) / den;
  const u = ((c[0] - a[0]) * ry - (c[1] - a[1]) * rx) / den;
  if (t <= 1e-6 || t >= 1 - 1e-6 || u <= 1e-6 || u >= 1 - 1e-6) return null;
  return { t, u, p: [a[0] + rx * t, a[1] + ry * t] };
}

/** Roads cut wherever they cross or one's end lands on another, joined at shared ends: nodes and edges. */
function planarize(segs: Seg[]): { nodes: Pt[]; edges: { from: number; to: number; seg: Seg; pts: Pt[] }[] } {
  // Cut points along each road, as distances along it.
  const cuts: number[][] = segs.map(() => []);
  const along = (pts: Pt[], seg: number, t: number) => {
    let s = 0;
    for (let i = 0; i < seg; i++) s += dist(pts[i], pts[i + 1]);
    return s + dist(pts[seg], pts[seg + 1]) * t;
  };
  for (let i = 0; i < segs.length; i++)
    for (let k = i + 1; k < segs.length; k++) {
      const p = segs[i].pts, q = segs[k].pts;
      for (let a = 0; a + 1 < p.length; a++)
        for (let b = 0; b + 1 < q.length; b++) {
          const x = cross(p[a], p[a + 1], q[b], q[b + 1]);
          if (!x) continue;
          cuts[i].push(along(p, a, x.t));
          cuts[k].push(along(q, b, x.u));
        }
    }
  // Ends on another road's side: a T.
  segs.forEach((s, i) => {
    for (const end of [s.pts[0], s.pts[s.pts.length - 1]])
      segs.forEach((o, k) => {
        if (k === i) return;
        const c = closestOn(end, o.pts);
        const len = polyLength(o.pts);
        if (c.d < 0.75 && c.s > 0.75 && c.s < len - 0.75) cuts[k].push(c.s);
      });
  });
  const nodes: Pt[] = [];
  const nodeAt = (p: Pt) => {
    const i = nodes.findIndex((n) => dist(n, p) < 0.75);
    if (i >= 0) return i;
    nodes.push([Math.round(p[0] * 100) / 100, Math.round(p[1] * 100) / 100]);
    return nodes.length - 1;
  };
  const edges: { from: number; to: number; seg: Seg; pts: Pt[] }[] = [];
  segs.forEach((s, i) => {
    const len = polyLength(s.pts);
    const marks = [0, ...cuts[i].filter((c) => c > 0.05 && c < len - 0.05).sort((a, b) => a - b), len];
    for (let m = 0; m + 1 < marks.length; m++) {
      const [s0, s1] = [marks[m], marks[m + 1]];
      if (s1 - s0 < 0.2) continue;
      const pts: Pt[] = [];
      let acc = 0;
      for (let k = 0; k < s.pts.length; k++) {
        if (k > 0) acc += dist(s.pts[k - 1], s.pts[k]);
        if (acc > s0 + 1e-6 && acc < s1 - 1e-6) pts.push(s.pts[k]);
      }
      const at = (d: number): Pt => {
        let a = 0;
        for (let k = 0; k + 1 < s.pts.length; k++) {
          const l = dist(s.pts[k], s.pts[k + 1]);
          if (d <= a + l) {
            const t = l ? (d - a) / l : 0;
            return [s.pts[k][0] + (s.pts[k + 1][0] - s.pts[k][0]) * t, s.pts[k][1] + (s.pts[k + 1][1] - s.pts[k][1]) * t];
          }
          a += l;
        }
        return s.pts[s.pts.length - 1];
      };
      const from = nodeAt(at(s0)), to = nodeAt(at(s1));
      if (from === to) continue;
      edges.push({ from, to, seg: s, pts: [nodes[from], ...pts, nodes[to]] });
    }
  });
  return { nodes, edges };
}

function mainsFor(center: Pt): Seg[] {
  const [cx, cy] = center;
  const line = (pts: Pt[]): Seg => ({ pts: pts.filter((p, i) => i === 0 || dist(p, pts[i - 1]) > 0.01), kind: 'main', cls: 'arterial' });
  return [
    line([GATES[0], [80.5, Math.min(cy, 40)], [cx, Math.min(cy, 40)], [cx, cy]]),
    line([GATES[1], [Math.max(cx, 120), 80.5], [Math.max(cx, 120), cy], [cx, cy]]),
    line([GATES[2], [80.5, Math.max(cy, 120)], [cx, Math.max(cy, 120)], [cx, cy]]),
    line([GATES[3], [Math.min(cx, 40), 80.5], [Math.min(cx, 40), cy], [cx, cy]]),
  ];
}

/** The town's centre (fixed: the site's) and its radius; downtown moves about inside it. */
const CENTRE: Pt = [80.5, 80.5];
const R = 60;
/** The street grid's spacing: a block of 9 and a street's gap round it. */
const CELL = 12;

/** The grid of streets round the centre, a block apart, kept clear of the main roads and the lake. */
function streetsFor(center: Pt, mains: Seg[]): Seg[] {
  const out: Seg[] = [];
  const [cx, cy] = center;
  for (let k = -4; k <= 4; k++) {
    if (k === 0) continue;
    for (const horizontal of [true, false]) {
      const off = k * CELL;
      const half = Math.sqrt(Math.max(0, R * R - off * off));
      if (half < 10) continue;
      const a: Pt = horizontal ? [cx - half, cy + off] : [cx + off, cy - half];
      const b: Pt = horizontal ? [cx + half, cy + off] : [cx + off, cy + half];
      const clip = (p: Pt): Pt => [Math.max(2, Math.min(W - 2, p[0])), Math.max(2, Math.min(H - 2, p[1]))];
      const pts = [clip(a), clip(b)];
      // Not along a main road, nor close beside one.
      const beside = (m: Seg) => {
        let n = 0;
        for (let k = 0; k <= 10; k++) if (closestOn([pts[0][0] + ((pts[1][0] - pts[0][0]) * k) / 10, pts[0][1] + ((pts[1][1] - pts[0][1]) * k) / 10], m.pts).d < 5) n++;
        return n >= 4;
      };
      if (mains.some(beside)) continue;
      out.push({ pts, kind: 'street', cls: Math.abs(k) === 2 ? 'collector' : 'local' });
    }
  }
  return out;
}

/** A number in [0, 1) from a salt and a few more, the same every time. */
function hash(...n: number[]): number {
  let h = 2166136261;
  for (const v of n) {
    h ^= Math.round(v * 1000) | 0;
    h = Math.imul(h, 16777619);
    h ^= h >>> 13;
  }
  return ((h >>> 0) % 100000) / 100000;
}

/** What a region made of one grid cell: a street across it (or none), which half is a park, the kinds wanted. */
interface Remade {
  region: number;
  salt: number;
  want: Want;
  /** The street across it: upright or level, how far in (tiles), and how far its far end leans. */
  split: { upright: boolean; at: number; lean: number } | null;
  park: boolean;
}

/** The grid cells each region covers (by its blocks' middles), later regions over earlier ones, and how each is made. */
function remade(s: Skeleton): Map<string, Remade> {
  const out = new Map<string, Remade>();
  (s.regions ?? []).forEach((r, region) => {
    const cells = new Set<string>();
    for (const poly of r.blocks) {
      const m = middle(poly);
      cells.add(`${Math.floor((m[0] - CENTRE[0]) / CELL)},${Math.floor((m[1] - CENTRE[1]) / CELL)}`);
    }
    // A park, if one's wanted: one cell of the region, chosen by the salt.
    const list = [...cells];
    const parkCell = r.want.park ? list[Math.floor(hash(r.salt, 7) * list.length)] : null;
    for (const c of list) {
      const [i, k] = c.split(',').map(Number);
      const h = hash(r.salt, i, k);
      const split = h < 0.75 ? { upright: hash(r.salt, i, k, 1) < 0.5, at: 3 + hash(r.salt, i, k, 2) * 3, lean: (hash(r.salt, i, k, 3) - 0.5) * 2 } : null;
      out.set(c, { region, salt: r.salt, want: r.want, split, park: c === parkCell });
    }
  });
  return out;
}

/** A cell's corner, and the street across it from one grid street to the next. */
const cellAt = (i: number, k: number): Pt => [CENTRE[0] + i * CELL + 1.5, CENTRE[1] + k * CELL + 1.5];

function splitStreet(i: number, k: number, m: Remade): Seg | null {
  if (!m.split) return null;
  const [x0, y0] = cellAt(i, k);
  const { upright, at, lean } = m.split;
  const pts: Pt[] = upright
    ? [[x0 + at, y0 - 1.5], [x0 + at + lean, y0 + 9 + 1.5]]
    : [[x0 - 1.5, y0 + at], [x0 + 9 + 1.5, y0 + at + lean]];
  return { pts, kind: 'street', cls: 'local' };
}

/** A cell's blocks: the whole cell, or its two halves either side of the street across it. */
function cellBlocks(i: number, k: number, m: Remade | undefined): Pt[][] {
  const [x0, y0] = cellAt(i, k);
  const [x1, y1] = [x0 + 9, y0 + 9];
  if (!m?.split) return [[[x0, y0], [x1, y0], [x1, y1], [x0, y1]]];
  const { upright, at, lean } = m.split;
  const g = 0.75;
  // Where the street crosses the cell's near and far sides.
  const a = (upright ? x0 : y0) + at, b = a + lean;
  return upright
    ? [
        [[x0, y0], [a - g, y0], [b - g, y1], [x0, y1]],
        [[a + g, y0], [x1, y0], [x1, y1], [b + g, y1]],
      ]
    : [
        [[x0, y0], [x1, y0], [x1, b - g], [x0, a - g]],
        [[x0, a + g], [x1, b + g], [x1, y1], [x0, y1]],
      ];
}

function grow(skel: Skeleton): { town: Town; regions: RegionResult[] } {
  const s = normalize(skel);
  const center = CENTRE;
  const downtown = clampDowntown(s.center ?? CENTRE, CENTRE, R);
  const cells = remade(s);
  const mains: Seg[] = s.mains ? s.mains.map((m) => ({ pts: m.pts, kind: 'main', cls: m.cls ?? 'arterial' })) : mainsFor(center);
  const pinned: Seg[] = (s.streets ?? []).map((r) => ({ pts: r.pts, kind: r.kind === 'path' ? 'path' : 'street', cls: r.cls ?? 'local' }));
  const removed = s.removed ?? [];
  const splits: Seg[] = [];
  for (const [c, m] of cells) {
    const [i, k] = c.split(',').map(Number);
    const st = splitStreet(i, k, m);
    if (st) splits.push(st);
  }
  const generated = [...streetsFor(center, mains), ...splits].filter((g) => !pinned.some((p) => closestOn(g.pts[0], p.pts).d < 3 && closestOn(g.pts[1], p.pts).d < 3));
  const g = planarize([...mains, ...pinned, ...generated]);
  // Removed ones dropped; and the generator's own stubs (under 3 tiles, or into the lake) never laid.
  const wetLine = (pts: Pt[]) => pts.some((p, i) => i > 0 && [0.25, 0.5, 0.75, 1].some((t) => wet(Math.floor(pts[i - 1][0] + (p[0] - pts[i - 1][0]) * t), Math.floor(pts[i - 1][1] + (p[1] - pts[i - 1][1]) * t))));
  const edges = g.edges.filter((e) => {
    if (e.seg.kind === 'main' || pinned.includes(e.seg)) return true;
    if (polyLength(e.pts) < 3 || wetLine(e.pts) || wet(Math.floor(e.pts[0][0]), Math.floor(e.pts[0][1]))) return false;
    return !removed.some(([a, b]) => sameEnds(a, b, e.pts[0], e.pts[e.pts.length - 1]));
  });
  const legs = new Map<number, number>();
  for (const e of edges) {
    legs.set(e.from, (legs.get(e.from) ?? 0) + 1);
    legs.set(e.to, (legs.get(e.to) ?? 0) + 1);
  }
  const nodes = g.nodes.map((at, id) => ({ id, at, legs: legs.get(id) ?? 0, gate: GATES.some((q) => dist(q, at) < 1) })).filter((n) => n.legs > 0);
  const roads: TownRoad[] = edges.map((e, id) => {
    const o = (s.roads ?? []).find((r) => sameEnds(r.a, r.b, e.pts[0], e.pts[e.pts.length - 1]));
    const main = e.seg.kind === 'main';
    const near = closestOn(downtown, e.pts).d;
    const flow = Math.round((main ? 900 : e.seg.cls === 'collector' ? 400 : 120) * Math.max(0.3, 1 - near / 90));
    const lanes = o?.lanes || (main && near < 30 ? 2 : 1);
    const width = main ? (lanes === 2 ? 3 : 2) + (o?.divided ? 1 : 0) : 1;
    const legsTo = legs.get(e.to) ?? 0, legsFrom = legs.get(e.from) ?? 0;
    return {
      id,
      from: e.from,
      to: e.to,
      kind: e.seg.kind,
      cls: o?.cls ?? e.seg.cls ?? (main ? 'arterial' : 'local'),
      pts: e.pts,
      name: NAMES[(id * 7) % NAMES.length],
      width,
      design: {
        lanes: o?.oneWay ? [lanes, 0] : [lanes, lanes],
        width,
        divided: !!o?.divided,
        pockets: main
          ? [
              ...(legsTo >= 3 ? [{ atTo: true, left: true, length: 4 + (flow % 3) }] : []),
              ...(legsFrom >= 3 ? [{ atTo: false, left: true, length: 4 }] : []),
            ]
          : [],
        flow: [flow, Math.round(flow * 0.8)],
        vc: Math.round((flow / (lanes * 1000)) * 100) / 100,
        relief: false,
        locked: !!o?.locked,
      },
    };
  });
  // A roundabout for each region that asked for one: its junction nearest the region's middle, on its cells' edges.
  const results: RegionResult[] = (s.regions ?? []).map((_, index) => ({ index, ok: true }));
  const roundabouts = new Set<number>();
  (s.regions ?? []).forEach((r, index) => {
    const mine = [...cells].filter(([, m]) => m.region === index).map(([c]) => c.split(',').map(Number));
    if (!mine.length) {
      results[index] = { index, ok: false, text: 'Nothing left to make there' };
      return;
    }
    if (!r.want.roundabout) return;
    const mid: Pt = [mine.reduce((a, [i]) => a + cellAt(i, 0)[0] + 4.5, 0) / mine.length, mine.reduce((a, [, k]) => a + cellAt(0, k)[1] + 4.5, 0) / mine.length];
    const on = (p: Pt) => mine.some(([i, k]) => {
      const [x0, y0] = cellAt(i, k);
      return p[0] >= x0 - 2.5 && p[0] <= x0 + 11.5 && p[1] >= y0 - 2.5 && p[1] <= y0 + 11.5;
    });
    let best: (typeof nodes)[number] | null = null;
    for (const n of nodes) if (n.legs >= 3 && !n.gate && !roundabouts.has(n.id) && on(n.at) && (!best || dist(n.at, mid) < dist(best.at, mid))) best = n;
    if (best) roundabouts.add(best.id);
    else results[index] = { index, ok: false, text: 'No room for a roundabout there' };
  });
  const junctions: TownJunction[] = nodes
    .filter((n) => n.legs >= 3)
    .map((n) => {
      const here = roads.filter((r) => r.from === n.id || r.to === n.id);
      const mainsHere = here.filter((r) => r.kind === 'main').length;
      const o = (s.junctions ?? []).find((j) => dist(j.at, n.at) <= 1.5);
      let control: TownJunction['control'] = mainsHere >= 3 ? 'signal' : mainsHere > 0 ? 'stop' : n.legs >= 4 ? 'allway' : 'stop';
      let form: TownJunction['form'] = 'plain';
      if (roundabouts.has(n.id) || o?.control === 'roundabout' || (!o && n.legs >= 5)) {
        form = 'roundabout';
        control = 'yield';
      } else if (o && o.control !== 'auto') control = o.control;
      return { node: n.id, at: n.at, form, radius: form === 'roundabout' ? 3 : 0, control, protectedLeft: control === 'signal' && mainsHere >= 4, slips: [], entering: here.reduce((a, r) => a + (r.design?.flow[0] ?? 0), 0) };
    });
  // Blocks: the grid's cells round the centre (halved where a region laid a street across one); buildings
  // along their edges, clear of every road. Zones and shops by how far they are from downtown.
  const blocks: Town['blocks'] = [];
  const buildings: TownBuilding[] = [];
  const schools = new Set<number>();
  let bid = 0;
  for (let i = -5; i < 5; i++)
    for (let k = -5; k < 5; k++) {
      const [x0, y0] = cellAt(i, k);
      const [x1, y1] = [x0 + 9, y0 + 9];
      const mid: Pt = [(x0 + x1) / 2, (y0 + y1) / 2];
      if (dist(mid, center) > R + 4 || x0 < 1 || y0 < 1 || x1 > W - 1 || y1 > H - 1 || wet(Math.floor(mid[0]), Math.floor(mid[1]))) continue;
      const m = cells.get(`${i},${k}`);
      const d = dist(mid, downtown);
      const transect = d < 14 ? 6 : d < 26 ? 5 : d < 42 ? 4 : 3;
      const era = d < 20 ? 'core' : d < 40 ? 'garden' : 'postwar';
      const park = m ? m.park : (i === 2 && k === -3) || (i === -3 && k === 2);
      const halves = cellBlocks(i, k, m);
      halves.forEach((poly, h) => blocks.push({ id: blocks.length, poly, transect, era, park: park && h === 0 }));
      const kinds: BuildingKind[] = m ? [...(m.want.shops ? ['shop' as const] : []), ...(m.want.houses ? ['house' as const] : [])] : [];
      for (let a = 0; a < 3; a++)
        for (let b = 0; b < 3; b++) {
          if (a === 1 && b === 1) continue;
          const bx = x0 + a * 3, by = y0 + b * 3;
          const c: Pt = [bx + 1.25, by + 1.25];
          const half = halves.findIndex((p) => inPoly(c, p));
          if (half < 0 || (park && half === 0)) continue;
          if (roads.some((r) => closestOn(c, r.pts).d < r.width / 2 + 1.6) || wet(Math.floor(c[0]), Math.floor(c[1]))) continue;
          const dd = dist(c, downtown);
          const h = m ? hash(m.salt, bx, by) : Math.abs(Math.sin(bx * 12.9898 + by * 78.233) * 43758.5453) % 1;
          let kind: BuildingKind = 'house';
          if (kinds.length) kind = kinds[Math.floor(h * kinds.length)];
          else if (dd < 16) kind = h < 0.75 ? 'shop' : 'house';
          else if (dd < 28) kind = h < 0.3 ? 'shop' : h < 0.35 ? 'clinic' : 'house';
          else if (c[0] > center[0] + 30 && c[1] < center[1] - 10) kind = h < 0.6 ? 'workshop' : 'house';
          if (!m && i === -2 && k === -2 && a === 0 && b === 0) kind = 'school';
          if (m?.want.school && !schools.has(m.region)) {
            kind = 'school';
            schools.add(m.region);
          }
          if (!m && Math.floor((downtown[0] - CENTRE[0]) / CELL) === i && Math.floor((downtown[1] - CENTRE[1]) / CELL) === k && a === 2 && b === 2) kind = 'church';
          buildings.push({ id: bid++, kind, poly: [[bx, by], [bx + 2.5, by], [bx + 2.5, by + 2.5], [bx, by + 2.5]], floors: kind === 'shop' ? (dd < 16 ? 4 : 3) : kind === 'house' ? 2 : 1 });
        }
    }
  (s.regions ?? []).forEach((r, index) => {
    if (r.want.school && !schools.has(index) && results[index].ok) results[index] = { index, ok: false, text: 'No room for a school there' };
  });
  const count = (k: BuildingKind) => buildings.filter((b) => b.kind === k).length;
  const town: Town = {
    width: W,
    height: H,
    center,
    downtown,
    secondCentre: null,
    townRadius: R,
    gates: GATES,
    water: WATER,
    nodes,
    roads,
    junctions,
    blocks,
    buildings,
    notes: roads.filter((r) => (r.design?.vc ?? 0) > 1).map((r) => ({ at: r.pts[Math.floor(r.pts.length / 2)], text: `${r.name} is over capacity (${r.design!.vc.toFixed(2)})` })),
    stats: {
      houses: count('house'),
      shops: count('shop'),
      buildings: buildings.length,
      roads: roads.length,
      junctions: junctions.length,
      roundabouts: junctions.filter((j) => j.form === 'roundabout').length,
      pockets: roads.reduce((a, r) => a + (r.design?.pockets.length ?? 0), 0),
      signals: junctions.filter((j) => j.control === 'signal').length,
    },
  };
  return { town, regions: results };
}

/** Only warnings: the worker sorts out the rest itself. */
function problemsOf(town: Town): Problem[] {
  return town.notes.map((note) => ({ level: 'warning' as const, text: note.text, at: note.at }));
}

function changesOf(base: Town, draft: Town): Changes {
  const people = (t: Town) => Math.round(t.buildings.filter((b) => b.kind === 'house').length * 2.5);
  const roadKey = (r: TownRoad) => r.pts.map((p) => p.map(Math.round).join(',')).join(';');
  const rb = new Set(base.roads.map(roadKey)), rd = new Set(draft.roads.map(roadKey));
  return {
    population: [people(base), people(draft)],
    shops: [base.stats.shops as number, draft.stats.shops as number],
    roadsAdded: [...rd].filter((r) => !rb.has(r)).length,
    roadsRemoved: [...rb].filter((r) => !rd.has(r)).length,
    roundabouts: [base.stats.roundabouts as number, draft.stats.roundabouts as number],
    signals: [base.stats.signals as number, draft.stats.signals as number],
  };
}

// --- The pretend server ----------------------------------------------------------------------------

const KEY = 'seed=mockton&width=160&height=160';
const state = {
  rev: 0,
  current: normalize(null),
  draft: null as Draft | null,
  history: [] as { rev: number; message: string; by: string; at: string }[],
};

const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
const keyOf = () => (state.rev > 0 ? `${KEY}&rev=${state.rev}` : KEY);

export const mock = {
  async towns(): Promise<EditableTown[]> {
    return [{ key: keyOf(), name: 'Mockton', rev: state.rev, mine: true, editable: true }];
  },
  async draft(): Promise<Draft> {
    if (!state.draft) state.draft = { town: keyOf(), baseRev: state.rev, skeleton: normalize(state.current), commands: null, updatedAt: new Date().toISOString(), stale: false, current: { rev: state.rev, skeleton: state.current } };
    return { ...state.draft, stale: state.draft.baseRev < state.rev, current: { rev: state.rev, skeleton: state.current } };
  },
  async saveDraft(d: { baseRev: number; skeleton: Skeleton; commands: unknown }): Promise<{ updatedAt: string }> {
    const updatedAt = new Date().toISOString();
    state.draft = { town: keyOf(), baseRev: d.baseRev, skeleton: d.skeleton, commands: d.commands as Draft['commands'], updatedAt, stale: false, current: { rev: state.rev, skeleton: state.current } };
    return { updatedAt };
  },
  async discard(): Promise<void> {
    state.draft = null;
  },
  async plan(skeleton: Skeleton, seq: number): Promise<PlanAnswer> {
    const t0 = performance.now();
    await wait(120);
    const { town, regions } = grow(skeleton);
    const base = grow(state.current).town;
    return { ok: true, seq, ms: Math.round(performance.now() - t0), town, problems: problemsOf(town), changes: changesOf(base, town), skeleton: normalize(skeleton), regions };
  },
  async live(): Promise<Town> {
    return grow(state.current).town;
  },
  async day(skeleton: Skeleton): Promise<{ draft: Day; live: Day }> {
    await wait(600);
    const dayOf = (t: Town): Day => {
      const signals = t.stats.signals as number, rb = t.stats.roundabouts as number;
      const mean = 7.5 - rb * 0.3 + Math.max(0, 4 - signals) * 0.2 + (t.roads.length % 5) * 0.1;
      return {
        trips: 4200,
        meanCarMin: Math.round(mean * 10) / 10,
        p90CarMin: Math.round(mean * 2.1 * 10) / 10,
        meanWalkMin: 6.2,
        failed: Math.max(0, 6 - rb),
        gaveUp: 2,
        worst: t.junctions.slice(0, 5).map((j, i) => ({ at: j.at, delayS: Math.round(40 - i * 6 + (j.entering % 7)), control: j.control })),
        pockets: t.roads.filter((r) => r.design?.pockets.length).slice(0, 4).map((r) => ({ at: r.pts[r.pts.length - 1], storage: 3, worstQueue: 2 + (r.id % 3) })),
      };
    };
    return { draft: dayOf(grow(skeleton).town), live: dayOf(grow(state.current).town) };
  },
  async commit(skeleton: Skeleton, message: string): Promise<CommitAnswer | { problems: Problem[] }> {
    await wait(400);
    const [was, now] = changesOf(grow(state.current).town, grow(skeleton).town).population;
    const lost = Math.max(0, Math.round((was - now) / 2.5));
    state.rev++;
    state.current = normalize(skeleton);
    state.draft = null;
    state.history.unshift({ rev: state.rev, message, by: 'you', at: new Date().toISOString() });
    return { key: keyOf(), rev: state.rev, rehoused: Math.min(lost, 3), players: 2 };
  },
  async history() {
    return state.history;
  },
};

/** The town the mock edits, grown from a skeleton (for tests). */
export const mockGrow = (s: Skeleton): Town => grow(s).town;
/** The same, with how each region came out (for tests). */
export const mockPlan = grow;

// A stand-in for the server and its draft worker (`?mock=1`), so the edit screen can be tried without
// either: a small made-up town grown from a skeleton the way the real generator treats one (main roads
// to the centre unless pinned, a street grid round the centre, pinned streets kept and removed ones
// dropped, shops near the centre and houses further out), with lanes, pockets and controls roughly as
// the design stage would choose. It's a toy: the real answers come from `porchlight-sim --draft`.

import { closestOn, dist, normalize, polyLength, sameEnds } from './skeleton.ts';
import type { BuildingKind, Changes, CommitAnswer, Day, Draft, EditableTown, PlanAnswer, Problem, Pt, Skeleton, Town, TownBuilding, TownJunction, TownRoad } from './types.ts';

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

const R = 60;

/** The grid of streets round the centre, a block (12 tiles) apart, kept clear of the main roads and the lake. */
function streetsFor(center: Pt, mains: Seg[]): Seg[] {
  const out: Seg[] = [];
  const [cx, cy] = center;
  for (let k = -4; k <= 4; k++) {
    if (k === 0) continue;
    for (const horizontal of [true, false]) {
      const off = k * 12;
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

function grow(skel: Skeleton): Town {
  const s = normalize(skel);
  const center: Pt = s.center ?? [80.5, 80.5];
  const mains: Seg[] = s.mains ? s.mains.map((m) => ({ pts: m.pts, kind: 'main', cls: m.cls ?? 'arterial' })) : mainsFor(center);
  const pinned: Seg[] = s.streets.map((r) => ({ pts: r.pts, kind: r.kind === 'path' ? 'path' : 'street', cls: r.cls ?? 'local' }));
  const generated = streetsFor(center, mains).filter((g) => !pinned.some((p) => closestOn(g.pts[0], p.pts).d < 3 && closestOn(g.pts[1], p.pts).d < 3));
  const g = planarize([...mains, ...pinned, ...generated]);
  // Removed: the generator's streets with those ends.
  // Removed ones dropped; and the generator's own stubs (under 3 tiles, or into the lake) never laid.
  const wetLine = (pts: Pt[]) => pts.some((p, i) => i > 0 && [0.25, 0.5, 0.75, 1].some((t) => wet(Math.floor(pts[i - 1][0] + (p[0] - pts[i - 1][0]) * t), Math.floor(pts[i - 1][1] + (p[1] - pts[i - 1][1]) * t))));
  const edges = g.edges.filter((e) => {
    if (e.seg.kind === 'main' || pinned.includes(e.seg)) return true;
    if (polyLength(e.pts) < 3 || wetLine(e.pts) || wet(Math.floor(e.pts[0][0]), Math.floor(e.pts[0][1]))) return false;
    return !s.removed.some(([a, b]) => sameEnds(a, b, e.pts[0], e.pts[e.pts.length - 1]));
  });
  const legs = new Map<number, number>();
  for (const e of edges) {
    legs.set(e.from, (legs.get(e.from) ?? 0) + 1);
    legs.set(e.to, (legs.get(e.to) ?? 0) + 1);
  }
  const nodes = g.nodes.map((at, id) => ({ id, at, legs: legs.get(id) ?? 0, gate: GATES.some((q) => dist(q, at) < 1) })).filter((n) => n.legs > 0);
  const roads: TownRoad[] = edges.map((e, id) => {
    const o = s.roads.find((r) => sameEnds(r.a, r.b, e.pts[0], e.pts[e.pts.length - 1]));
    const main = e.seg.kind === 'main';
    const near = closestOn(center, e.pts).d;
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
  const junctions: TownJunction[] = nodes
    .filter((n) => n.legs >= 3)
    .map((n) => {
      const here = roads.filter((r) => r.from === n.id || r.to === n.id);
      const mainsHere = here.filter((r) => r.kind === 'main').length;
      const o = s.junctions.find((j) => dist(j.at, n.at) <= 1.5);
      let control: TownJunction['control'] = mainsHere >= 3 ? 'signal' : mainsHere > 0 ? 'stop' : n.legs >= 4 ? 'allway' : 'stop';
      let form: TownJunction['form'] = 'plain';
      if (o && o.control !== 'auto') {
        if (o.control === 'roundabout') {
          form = 'roundabout';
          control = 'yield';
        } else control = o.control;
      } else if (n.legs >= 5) {
        form = 'roundabout';
        control = 'yield';
      }
      return { node: n.id, at: n.at, form, radius: form === 'roundabout' ? 3 : 0, control, protectedLeft: control === 'signal' && mainsHere >= 4, slips: [], entering: here.reduce((a, r) => a + (r.design?.flow[0] ?? 0), 0) };
    });
  // Blocks: the grid's cells round the centre; buildings along their edges, clear of every road.
  const blocks: Town['blocks'] = [];
  const buildings: TownBuilding[] = [];
  const [cx, cy] = center;
  let bid = 0;
  for (let i = -5; i < 5; i++)
    for (let k = -5; k < 5; k++) {
      const x0 = cx + i * 12 + 1.5, y0 = cy + k * 12 + 1.5, x1 = x0 + 9, y1 = y0 + 9;
      const mid: Pt = [(x0 + x1) / 2, (y0 + y1) / 2];
      const d = dist(mid, center);
      if (d > R + 4 || x0 < 1 || y0 < 1 || x1 > W - 1 || y1 > H - 1 || wet(Math.floor(mid[0]), Math.floor(mid[1]))) continue;
      const transect = d < 14 ? 6 : d < 26 ? 5 : d < 42 ? 4 : 3;
      const park = (i === 2 && k === -3) || (i === -3 && k === 2);
      blocks.push({ id: blocks.length, poly: [[x0, y0], [x1, y0], [x1, y1], [x0, y1]], transect, era: d < 20 ? 'core' : d < 40 ? 'garden' : 'postwar', park });
      if (park) continue;
      for (let a = 0; a < 3; a++)
        for (let b = 0; b < 3; b++) {
          if (a === 1 && b === 1) continue;
          const bx = x0 + a * 3, by = y0 + b * 3;
          const c: Pt = [bx + 1.25, by + 1.25];
          if (roads.some((r) => closestOn(c, r.pts).d < r.width / 2 + 1.6) || wet(Math.floor(c[0]), Math.floor(c[1]))) continue;
          const dd = dist(c, center);
          const hash = Math.abs(Math.sin(bx * 12.9898 + by * 78.233) * 43758.5453) % 1;
          let kind: BuildingKind = 'house';
          if (dd < 16) kind = hash < 0.75 ? 'shop' : 'house';
          else if (dd < 28) kind = hash < 0.3 ? 'shop' : hash < 0.35 ? 'clinic' : 'house';
          else if (c[0] > cx + 30 && c[1] < cy - 10) kind = hash < 0.6 ? 'workshop' : 'house';
          if (i === -2 && k === -2 && a === 0 && b === 0) kind = 'school';
          if (i === 0 && k === 0 && a === 2 && b === 2) kind = 'church';
          buildings.push({ id: bid++, kind, poly: [[bx, by], [bx + 2.5, by], [bx + 2.5, by + 2.5], [bx, by + 2.5]], floors: kind === 'shop' ? 3 : kind === 'house' ? 2 : 1 });
        }
    }
  const count = (k: BuildingKind) => buildings.filter((b) => b.kind === k).length;
  return {
    width: W,
    height: H,
    center,
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
}

function problemsOf(town: Town): Problem[] {
  const out: Problem[] = [];
  for (const r of town.roads) {
    const a = town.nodes.find((n) => n.id === r.from), b = town.nodes.find((n) => n.id === r.to);
    if (!a || !b) continue;
    const junction = (n: typeof a) => n.legs >= 3;
    const len = polyLength(r.pts);
    const apart = r.kind === 'main' ? 4 : 3;
    if (junction(a) && junction(b) && len < apart) out.push({ level: 'error', text: `Junctions ${len.toFixed(1)} tiles apart along ${r.name} (under ${apart})`, at: r.pts[0] });
  }
  for (const n of town.nodes) {
    const x = Math.floor(n.at[0]), y = Math.floor(n.at[1]);
    if (wet(x, y)) out.push({ level: 'error', text: 'A junction in the lake', at: n.at });
  }
  for (const note of town.notes) out.push({ level: 'warning', text: note.text, at: note.at });
  return out;
}

function changesOf(base: Town, draft: Town): Changes {
  const houses = (t: Town) => new Set(t.buildings.filter((b) => b.kind === 'house').map((b) => `${b.poly[0][0]},${b.poly[0][1]}`));
  const [hb, hd] = [houses(base), houses(draft)];
  const lost = [...hb].filter((h) => !hd.has(h));
  const roadKey = (r: TownRoad) => r.pts.map((p) => p.map(Math.round).join(',')).join(';');
  const rb = new Set(base.roads.map(roadKey)), rd = new Set(draft.roads.map(roadKey));
  return {
    housesLost: lost.length,
    housesNew: [...hd].filter((h) => !hb.has(h)).length,
    lostHouses: lost.map((h) => h.split(',').map(Number) as Pt),
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
    const town = grow(skeleton);
    const base = grow(state.current);
    return { ok: true, seq, ms: Math.round(performance.now() - t0), town, problems: problemsOf(town), changes: changesOf(base, town) };
  },
  async live(): Promise<Town> {
    return grow(state.current);
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
    return { draft: dayOf(grow(skeleton)), live: dayOf(grow(state.current)) };
  },
  async commit(skeleton: Skeleton, message: string): Promise<CommitAnswer | { problems: Problem[] }> {
    await wait(400);
    const errors = problemsOf(grow(skeleton)).filter((p) => p.level === 'error');
    if (errors.length) return { problems: errors };
    const lost = changesOf(grow(state.current), grow(skeleton)).housesLost;
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
export const mockGrow = grow;

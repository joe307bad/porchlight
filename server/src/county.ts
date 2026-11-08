// The county, as the game lays it out (unity/.../Shared/County/CountyMap.cs):
// Porchlight in the middle, Lanternport, Ember Crossing and Juniper Falls east,
// south and west of it in a ring, and north of it a loop through each
// account's own towns. Every town sits in the middle of a 256-tile slot with a
// gate in the middle of each edge, so all of it follows from the towns' keys.
// The server uses it for one thing: which room's ground a position is (a
// town's own, each highway's near half, the countryside nearest it), so a room
// accepts its players out on its highways and in the air over its fields.
// The same numbers as the game's; test/county-vectors.json (written by
// `Checks county`) holds the two to the same answers.

export type Place = 'porchlight' | 'east' | 'south' | 'west' | 'mine1' | 'mine2' | 'mine3';
export type SpaceKind = 'town' | 'junction' | 'vacant' | 'highway';
type Side = 'north' | 'east' | 'south' | 'west';

export interface PlacedTown {
  place: Place;
  key: string;
}

interface Vec {
  x: number;
  y: number;
}

export interface Space {
  id: number;
  kind: SpaceKind;
  x0: number;
  y0: number;
  width: number;
  height: number;
  townKey: string | null;
  isPrivate: boolean;
  highway: number;
  s0: number;
  s1: number;
}

export interface Highway {
  a: number;
  b: number;
  points: Vec[];
  at: number[];
  length: number;
  segments: number[];
}

export const SLOT = 256;
export const GAP = 400;
const JUNCTION = 48;
const VACANT = 64;
const STRETCH = 180;
const VERGE = 14;
const CORRIDOR = 24;
/** How far a highway's end stretches reach into the spaces at its ends (CountyMap.INSET). */
const INSET = 3;
const MINE_ROW = -(SLOT + GAP + SLOT);
const JUNCTION_Y = Math.trunc((MINE_ROW + SLOT) / 2) - JUNCTION / 2;
export const PRIVATE_LINE = (JUNCTION_Y + JUNCTION) / 2;
export const NORTH = 'north';
const SAMPLE = 0.75;

const PLACES: Place[] = ['porchlight', 'east', 'south', 'west', 'mine1', 'mine2', 'mine3'];
export const isMine = (p: Place): boolean => p.startsWith('mine');
export const mineSlot = (slot: number): Place => PLACES[4 + slot];

function slotOrigin(p: Place): Vec {
  const step = SLOT + GAP;
  switch (p) {
    case 'porchlight': return { x: 0, y: 0 };
    case 'east': return { x: step, y: 0 };
    case 'south': return { x: 0, y: step };
    case 'west': return { x: -step, y: 0 };
    case 'mine1': return { x: -step, y: MINE_ROW };
    case 'mine2': return { x: 0, y: MINE_ROW };
    default: return { x: step, y: MINE_ROW };
  }
}

/** A map's width or height from a town key, as the game reads it (GenParams: default 128, 32 to 256 in steps of 16). */
export function keySize(key: string, which: 'width' | 'height'): number {
  const m = new RegExp(`(?:^|&)${which}=([0-9.eE+-]+)`).exec(key);
  const v = m ? Number(m[1]) : NaN;
  if (!Number.isFinite(v)) return 128;
  const stepped = Math.floor((v - 32) / 16 + 0.5) * 16 + 32;
  return Math.min(256, Math.max(32, stepped));
}

const gateTile = (side: Side, w: number, h: number): Vec => {
  switch (side) {
    case 'north': return { x: Math.trunc(w / 2), y: 0 };
    case 'east': return { x: w - 1, y: Math.trunc(h / 2) };
    case 'south': return { x: Math.trunc(w / 2), y: h - 1 };
    default: return { x: 0, y: Math.trunc(h / 2) };
  }
};

function gateEdge(side: Side, w: number, h: number): Vec {
  const t = gateTile(side, w, h);
  switch (side) {
    case 'north': return { x: t.x + 0.5, y: 0 };
    case 'east': return { x: w, y: t.y + 0.5 };
    case 'south': return { x: t.x + 0.5, y: h };
    default: return { x: 0, y: t.y + 0.5 };
  }
}

const outward = (side: Side): Vec => (side === 'north' ? { x: 0, y: -1 } : side === 'east' ? { x: 1, y: 0 } : side === 'south' ? { x: 0, y: 1 } : { x: -1, y: 0 });
const dist = (a: Vec, b: Vec): number => Math.hypot(a.x - b.x, a.y - b.y);

function sampleBezier(p0: Vec, p1: Vec, p2: Vec, p3: Vec, step: number): Vec[] {
  const FINE = 4096;
  const fine: Vec[] = [];
  const acc: number[] = [];
  for (let i = 0; i <= FINE; i++) {
    const t = i / FINE;
    const u = 1 - t;
    const a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
    fine.push({ x: a * p0.x + b * p1.x + c * p2.x + d * p3.x, y: a * p0.y + b * p1.y + c * p2.y + d * p3.y });
    acc.push(i === 0 ? 0 : acc[i - 1] + dist(fine[i - 1], fine[i]));
  }
  const total = acc[FINE];
  const n = Math.max(1, Math.ceil(total / step));
  const o: Vec[] = [p0];
  let j = 0;
  for (let k = 1; k < n; k++) {
    const want = (total * k) / n;
    while (j < FINE - 1 && acc[j + 1] < want) j++;
    const span = acc[j + 1] - acc[j];
    const f = span > 0 ? (want - acc[j]) / span : 0;
    o.push({ x: fine[j].x + (fine[j + 1].x - fine[j].x) * f, y: fine[j].y + (fine[j + 1].y - fine[j].y) * f });
  }
  o.push(p3);
  return o;
}

function pointAt(hw: Highway, s: number): Vec {
  if (s <= 0) return hw.points[0];
  if (s >= hw.length) return hw.points[hw.points.length - 1];
  let lo = 0;
  let hi = hw.at.length - 1;
  while (hi - lo > 1) {
    const m = Math.trunc((lo + hi) / 2);
    if (hw.at[m] <= s) lo = m;
    else hi = m;
  }
  const f = (s - hw.at[lo]) / Math.max(1e-9, hw.at[hi] - hw.at[lo]);
  return { x: hw.points[lo].x + (hw.points[hi].x - hw.points[lo].x) * f, y: hw.points[lo].y + (hw.points[hi].y - hw.points[lo].y) * f };
}

function nearest(hw: Highway, p: Vec): { s: number; d: number } {
  let best = Infinity;
  let bestS = 0;
  for (let i = 1; i < hw.points.length; i++) {
    const a = hw.points[i - 1];
    const b = hw.points[i];
    const dx = b.x - a.x, dy = b.y - a.y;
    const len2 = dx * dx + dy * dy;
    const t = len2 > 0 ? Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2)) : 0;
    const d = Math.hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t));
    if (d < best) {
      best = d;
      bestS = hw.at[i - 1] + (hw.at[i] - hw.at[i - 1]) * t;
    }
  }
  return { s: bestS, d: best };
}

const contains = (s: Space, x: number, y: number): boolean => x >= s.x0 && y >= s.y0 && x <= s.x0 + s.width && y <= s.y0 + s.height;

export class County {
  readonly spaces: Space[] = [];
  readonly highways: Highway[] = [];
  readonly northRoom: string;

  /** The county with these towns (Porchlight and the public ones, and whichever of an account's slots have one). */
  constructor(placed: PlacedTown[], account?: string) {
    this.northRoom = account ? `${NORTH}:${account}` : NORTH;
    const byPlace = new Map(placed.map((t) => [t.place, t]));
    if (!byPlace.has('porchlight')) throw new Error('the county needs Porchlight');
    const bySlot = new Map<Place, Space>();
    for (const p of PLACES) {
      const o = slotOrigin(p);
      const t = byPlace.get(p);
      if (t) {
        const w = Math.max(16, Math.min(SLOT, keySize(t.key, 'width')));
        const h = Math.max(16, Math.min(SLOT, keySize(t.key, 'height')));
        bySlot.set(p, this.add({ kind: 'town', x0: o.x + (SLOT - w) / 2, y0: o.y + (SLOT - h) / 2, width: w, height: h, townKey: t.key, isPrivate: isMine(p) }));
      } else if (isMine(p)) {
        bySlot.set(p, this.add({ kind: 'vacant', x0: o.x + (SLOT - VACANT) / 2, y0: o.y + (SLOT - VACANT) / 2, width: VACANT, height: VACANT, townKey: null, isPrivate: true }));
      }
    }
    const junction = this.add({ kind: 'junction', x0: (SLOT - JUNCTION) / 2, y0: JUNCTION_Y, width: JUNCTION, height: JUNCTION, townKey: null, isPrivate: true });
    const at = (p: Place) => bySlot.get(p);
    this.connect(at('porchlight'), 'east', at('east'), 'west', false);
    this.connect(at('porchlight'), 'south', at('south'), 'north', false);
    this.connect(at('porchlight'), 'west', at('west'), 'east', false);
    this.connect(at('east'), 'south', at('south'), 'east', false);
    this.connect(at('south'), 'west', at('west'), 'south', false);
    this.connect(at('porchlight'), 'north', junction, 'south', false);
    this.connect(junction, 'west', at('mine1'), 'south', true);
    this.connect(junction, 'north', at('mine2'), 'south', true);
    this.connect(junction, 'east', at('mine3'), 'south', true);
    this.connect(at('mine1'), 'east', at('mine2'), 'west', true);
    this.connect(at('mine2'), 'east', at('mine3'), 'west', true);
  }

  private add(s: Omit<Space, 'id' | 'highway' | 's0' | 's1'> & Partial<Space>): Space {
    const space: Space = { highway: -1, s0: 0, s1: 0, ...s, id: this.spaces.length };
    this.spaces.push(space);
    return space;
  }

  private connect(from: Space | undefined, fromSide: Side, to: Space | undefined, toSide: Side, isPrivate: boolean): void {
    if (!from || !to) return;
    const e0 = gateEdge(fromSide, from.width, from.height);
    const e3 = gateEdge(toSide, to.width, to.height);
    const p0 = { x: from.x0 + e0.x, y: from.y0 + e0.y };
    const p3 = { x: to.x0 + e3.x, y: to.y0 + e3.y };
    const d0 = outward(fromSide);
    const d3 = outward(toSide);
    const chord = dist(p0, p3);
    const straight = Math.abs(d0.x + d3.x) < 1e-9 && Math.abs(d0.y + d3.y) < 1e-9 && Math.abs((p3.x - p0.x) * d0.y - (p3.y - p0.y) * d0.x) < 1e-9;
    const k = straight ? chord / 3 : chord * 0.5523;
    const points = sampleBezier(p0, { x: p0.x + d0.x * k, y: p0.y + d0.y * k }, { x: p3.x + d3.x * k, y: p3.y + d3.y * k }, p3, SAMPLE);
    const atS = [0];
    for (let i = 1; i < points.length; i++) atS.push(atS[i - 1] + dist(points[i - 1], points[i]));
    const hw: Highway = { a: from.id, b: to.id, points, at: atS, length: atS[atS.length - 1], segments: [] };
    const id = this.highways.length;
    this.highways.push(hw);
    const n = Math.max(1, Math.ceil(hw.length / STRETCH));
    for (let k2 = 0; k2 < n; k2++) {
      const s0 = (hw.length * k2) / n;
      const s1 = (hw.length * (k2 + 1)) / n;
      let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
      const stretch = [pointAt(hw, s0), ...hw.points.filter((_, i) => hw.at[i] > s0 + 1e-6 && hw.at[i] < s1 - 1e-6), pointAt(hw, s1)];
      for (const q of stretch) {
        minX = Math.min(minX, q.x); maxX = Math.max(maxX, q.x);
        minY = Math.min(minY, q.y); maxY = Math.max(maxY, q.y);
      }
      let x0 = Math.floor(minX - VERGE), y0 = Math.floor(minY - VERGE);
      let x1 = Math.ceil(maxX + VERGE), y1 = Math.ceil(maxY + VERGE);
      const clip = (gate: Vec, side: Side) => {
        // C#'s Math.Round rounds halves to even, but a gate's edge coordinate is always whole.
        if (side === 'north') y1 = Math.min(y1, Math.round(gate.y) + INSET);
        else if (side === 'south') y0 = Math.max(y0, Math.round(gate.y) - INSET);
        else if (side === 'east') x0 = Math.max(x0, Math.round(gate.x) - INSET);
        else x1 = Math.min(x1, Math.round(gate.x) + INSET);
      };
      if (k2 === 0) clip(p0, fromSide);
      if (k2 === n - 1) clip(p3, toSide);
      const seg = this.add({
        kind: 'highway', x0, y0, width: x1 - x0, height: y1 - y0, townKey: null,
        isPrivate: isPrivate || pointAt(hw, (s0 + s1) / 2).y < PRIVATE_LINE, highway: id, s0, s1,
      });
      hw.segments.push(seg.id);
    }
  }

  roomOf(s: Space): string {
    return s.kind === 'town' ? s.townKey! : this.northRoom;
  }

  /** The room whose ground this world point is (see CountyMap.RoomAt). */
  roomAt(x: number, y: number): string {
    for (const s of this.spaces) if (s.kind !== 'highway' && contains(s, x, y)) return this.roomOf(s);
    const mine = y < PRIVATE_LINE;
    let near: Highway | null = null;
    let nearD = Infinity;
    let nearS = 0;
    for (const hw of this.highways) {
      const { s, d } = nearest(hw, { x, y });
      if (d < nearD) {
        near = hw;
        nearD = d;
        nearS = s;
      }
    }
    if (near && nearD <= CORRIDOR) return this.roomOf(this.spaces[nearS < near.length / 2 ? near.a : near.b]);
    let best: Space | null = null;
    let bestD = Infinity;
    for (const s of this.spaces) {
      if (s.kind === 'highway' || s.isPrivate !== mine) continue;
      const dx = Math.max(0, Math.max(s.x0 - x, x - (s.x0 + s.width)));
      const dy = Math.max(0, Math.max(s.y0 - y, y - (s.y0 + s.height)));
      const d = Math.hypot(dx, dy);
      if (d < bestD) {
        bestD = d;
        best = s;
      }
    }
    return best ? this.roomOf(best) : this.roomOf(this.spaces[0]);
  }

  /** Where a room's positions are measured from (world): its town's corner, or the county's for the north room. */
  roomOrigin(room: string): Vec {
    const s = this.spaces.find((t) => t.kind === 'town' && t.townKey === room);
    return s ? { x: s.x0, y: s.y0 } : { x: 0, y: 0 };
  }

  /** Every room this county has: its towns', and the north room. */
  rooms(): string[] {
    return [...this.spaces.filter((s) => s.kind === 'town').map((s) => s.townKey!), this.northRoom];
  }

  /**
   * Is this position (the room's own, measured from roomOrigin) the room's ground, or within
   * `slack` tiles of it? Slack covers the moment between crossing over and joining the next room.
   */
  owns(room: string, x: number, z: number, slack: number): boolean {
    const o = this.roomOrigin(room);
    const wx = o.x + x;
    const wy = o.y + z;
    if (this.roomAt(wx, wy) === room) return true;
    for (const [dx, dy] of [[slack, 0], [-slack, 0], [0, slack], [0, -slack], [slack, slack], [-slack, -slack], [slack, -slack], [-slack, slack]]) {
      if (this.roomAt(wx + dx, wy + dy) === room) return true;
    }
    return false;
  }

  /**
   * A quick check, fit for every move, that a position (the room's frame) is the room's ground
   * or within `slack` tiles of it: its own rectangle at once, elsewhere the room of each 8-tile
   * cell round it (each worked out once, then kept).
   */
  territory(room: string, slack = 16): (x: number, z: number) => boolean {
    const CELL = 8;
    const FAR = 4096;
    const o = this.roomOrigin(room);
    const home = this.home(room);
    const cache = new Map<string, string>();
    const cellRoom = (cx: number, cy: number): string => {
      const k = `${cx},${cy}`;
      let r = cache.get(k);
      if (r === undefined) cache.set(k, (r = this.roomAt((cx + 0.5) * CELL, (cy + 0.5) * CELL)));
      return r;
    };
    const reach = Math.ceil(slack / CELL);
    return (x, z) => {
      if (x >= home.x0 && x <= home.x1 && z >= home.z0 && z <= home.z1) return true;
      const wx = o.x + x;
      const wy = o.y + z;
      if (!(Math.abs(wx) < FAR && Math.abs(wy) < FAR)) return false;
      const cx = Math.floor(wx / CELL);
      const cy = Math.floor(wy / CELL);
      for (let dy = -reach; dy <= reach; dy++) for (let dx = -reach; dx <= reach; dx++) if (cellRoom(cx + dx, cy + dy) === room) return true;
      return false;
    };
  }

  /** The town's rectangle, room's frame, for clamping (the north room: its junction). */
  home(room: string): { x0: number; z0: number; x1: number; z1: number } {
    const s = this.spaces.find((t) => t.kind === 'town' && t.townKey === room) ?? this.spaces.find((t) => t.kind === 'junction')!;
    const o = this.roomOrigin(room);
    return { x0: s.x0 - o.x, z0: s.y0 - o.y, x1: s.x0 + s.width - o.x, z1: s.y0 + s.height - o.y };
  }
}

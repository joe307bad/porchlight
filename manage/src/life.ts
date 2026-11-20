// A town's life as the simulation has it (docs/town-life.md): its clock, who's
// indoors in each building, who's off the map at each gate, and the day's figures
// so far. The server keeps the latest (once a second) for each town it runs:
// GET /mp/sim/life; and who's in one building: GET /mp/sim/building.

/** GET /mp/sim/life (sim/src/SimTown.cs Life, TownLife.Summary). */
export interface TownLife {
  town: string;
  /** The town clock (unix ms), and as hh:mm (the county's time zone). */
  clock: number;
  time: string;
  /** [building (its place in the town's buildings), people inside]. */
  inside: [number, number][];
  /** People off the map at each gate. */
  offMap: number[];
  gates: [number, number][];
  stops: [number, number][];
  summary: LifeSummary;
}

export interface LifeSummary {
  clock: string;
  people: number;
  out: number;
  cars: number;
  trips: { started: number; finished: number; failed: number };
  /** Trips started each hour of the day: [walk, drive, bus]. */
  byHour: number[][];
  /** Mean trip (seconds): [walk, drive, bus]. */
  meanTrip: number[];
  /** Kerb spots taken, of all of them. */
  parking: [number, number];
  /** The roads most driven today: [road, cars]. */
  busiestRoads: [number, number][];
  /** Junctions where cars waited long: x, y (tiles), mean and longest wait (s). */
  waits: { x: number; y: number; mean: number; longest: number }[];
  problems: number;
  gridlockBroken: number;
  mainCrossings: number;
  hunts: number;
}

/** GET /mp/sim/building: who's inside, and when each goes out next. */
export interface BuildingLife {
  town: string;
  building: number;
  inside: { id: number; kind: string; next: string | null; to: string | null; purpose: string | null }[];
}

export const MODES = ['walk', 'drive', 'bus'] as const;

const cache = new WeakMap<TownLife, Map<number, number>>();

/** People inside each building, by its index. */
export function indoors(life: TownLife): Map<number, number> {
  let m = cache.get(life);
  if (!m) {
    m = new Map(life.inside);
    cache.set(life, m);
  }
  return m;
}

/** A disc for this many people indoors (tiles): its area as many as there are, never smaller than a dot. */
export function indoorRadius(n: number): number {
  return Math.max(0.35, Math.min(4, Math.sqrt(n) * 0.35));
}

export interface LifeFigures {
  people: number;
  indoors: number;
  offMap: number;
  out: number;
  /** Trips made of those started, and how many couldn't be made. */
  made: number;
  failed: number;
  /** Each mode's share of the trips started today (0-1), walk, drive, bus. */
  shares: number[];
  /** The hour most trips started in (0-23), or -1 before any. */
  busiestHour: number;
  /** Mean trip, minutes, by mode (null: none yet). */
  meanMinutes: (number | null)[];
  /** Share of kerb spots taken (0-1). */
  parked: number;
}

/** A town's life summed up for the panel. */
export function figures(life: TownLife): LifeFigures {
  const s = life.summary;
  const indoorsN = life.inside.reduce((n, [, k]) => n + k, 0);
  const offMap = life.offMap.reduce((n, k) => n + k, 0);
  const byMode = [0, 0, 0];
  let busiest = -1, most = 0;
  s.byHour.forEach((h, hour) => {
    const n = h[0] + h[1] + h[2];
    for (let m = 0; m < 3; m++) byMode[m] += h[m];
    if (n > most) {
      most = n;
      busiest = hour;
    }
  });
  const total = byMode[0] + byMode[1] + byMode[2];
  return {
    people: s.people,
    indoors: indoorsN,
    offMap,
    out: s.out,
    made: s.trips.finished,
    failed: s.trips.failed,
    shares: byMode.map((n) => (total ? n / total : 0)),
    busiestHour: busiest,
    meanMinutes: s.meanTrip.map((sec) => (sec > 0 ? sec / 60 : null)),
    parked: s.parking[1] ? s.parking[0] / s.parking[1] : 0,
  };
}

/** Who's in a building, said in a line each. */
export function describeInside(b: BuildingLife): string[] {
  return b.inside.map((p) => {
    const kind = p.kind === 'AtHome' ? 'at home' : p.kind === 'CommuterIn' ? 'commuter' : p.kind.toLowerCase();
    const next = p.next ? `out at ${p.next}${p.purpose ? ` (${p.purpose.toLowerCase()})` : ''}` : 'in for the day';
    return `#${p.id} · ${kind} · ${next}`;
  });
}

// --- Focus: one car or townsperson (GET /mp/sim/focus; sim's Life/Focus.cs) -------------------

/** One question in a decision tree: its answer, which way it went (a yes-or-no decision; none for a fact), and what it went by. */
export interface Why {
  q: string;
  a: string;
  yes?: boolean;
  note?: string;
  under?: Why[];
}

/** A step in a decision queue, and when it starts (unix ms; null: not known yet). */
export interface QueueItem {
  at: number | null;
  time: string | null;
  what: string;
  /** The step under way now. */
  now: boolean;
}

/** GET /mp/sim/focus: one car or person, followed by who it is. */
export interface EntityFocus {
  town: string;
  kind: 'car' | 'person';
  id: number;
  /** The town clock when asked (unix ms), and as hh:mm. */
  clock: number;
  time: string;
  /** Who they are ("Works, and lives in town"; "A car"). */
  who: string;
  /** What they're doing, in a line. */
  doing: string;
  /** A person: Inside, OnTrip or OffMap; their car (-1: none). */
  where?: string;
  car?: number;
  /** A car: whose (-1: the bus), which bus (-1: none), its state. */
  owner?: number;
  bus?: number;
  state?: string;
  /** What they're drawn as now (a person driving is drawn as their car), and its place in the feed's records. */
  drawn: 'car' | 'person' | 'none';
  slot: number;
  /** Where they are (town frame, tiles). */
  at: [number, number];
  tree: Why[];
  queue: QueueItem[];
  /** The way ahead (town frame), and where it ends. */
  path: [number, number][];
  dest: [number, number] | null;
  /** A person's last few trips. */
  recent?: { purpose: string; mode: string; to: string; started: string; ended: string | null; made: boolean; failed: string | null }[];
}

/** What's focused on: a town's car or person, by who it is. */
export interface FocusTarget {
  town: string;
  kind: 'car' | 'person';
  id: number;
}

/** What a click found that has no day to explain. */
export type FocusAnswer = EntityFocus | { wanderer: true } | { empty: true };

export const isFocus = (a: FocusAnswer | null): a is EntityFocus => !!a && 'id' in a;

/** A decision's mark: ✓ yes (passed), ✗ no, nothing for a fact. */
export function mark(w: Why): string {
  return w.yes === undefined ? '' : w.yes ? '✓' : '✗';
}

/** When a queued step starts, from the town clock `clock`: now, in so long, or at a time of day. */
export function untilText(item: QueueItem, clock: number): string {
  if (item.now) return 'now';
  if (item.at === null) return 'then';
  const s = Math.round((item.at - clock) / 1000);
  if (s <= 0) return 'now';
  if (s < 90) return `in ${s} s`;
  if (s < 60 * 60) return `in ${Math.round(s / 60)} min`;
  return `at ${item.time}`;
}

/** The records a focus can be drawn from: a room's cars and people (county frame), as the feed has them now. */
export interface FocusRoom {
  ox: number;
  oy: number;
  cars: { n: number; shown: Uint8Array; x: Float32Array; y: Float32Array };
  people: { n: number; shown: Uint8Array; x: Float32Array; y: Float32Array };
}

/**
 * Where a focus is now, on the county map: where it's drawn (its car's or its own record, moving smoothly), or
 * where the simulation last said (indoors, parked, or its town not in the feed).
 */
export function focusPoint(f: EntityFocus, room: FocusRoom | null | undefined): { x: number; y: number; live: boolean } | null {
  if (!room) return null;
  const layer = f.drawn === 'car' ? room.cars : f.drawn === 'person' ? room.people : null;
  if (layer && f.slot >= 0 && f.slot < layer.n && layer.shown[f.slot]) return { x: layer.x[f.slot], y: layer.y[f.slot], live: true };
  return { x: room.ox + f.at[0], y: room.oy + f.at[1], live: false };
}

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
  /** What each of the tick's car and person records is: a letter each (ENTITY_KINDS), '-' for an empty one. */
  kinds?: { cars: string; people: string };
}

/** The kinds of thing on the map: what each car and person record is (sim/src/SimTown.cs Kinds). */
export const ENTITY_KINDS: { id: string; of: 'car' | 'person'; letter: string; label: string }[] = [
  { id: 'car:r', of: 'car', letter: 'r', label: "Residents' cars" },
  { id: 'car:c', of: 'car', letter: 'c', label: "Commuters' cars" },
  { id: 'car:t', of: 'car', letter: 't', label: 'Through traffic' },
  { id: 'car:v', of: 'car', letter: 'v', label: 'Vans on their rounds' },
  { id: 'car:b', of: 'car', letter: 'b', label: 'Buses' },
  { id: 'car:g', of: 'car', letter: 'g', label: 'Bin lorries' },
  { id: 'car:e', of: 'car', letter: 'e', label: 'Ambulances and fire engines' },
  { id: 'car:s', of: 'car', letter: 's', label: 'Boats' },
  { id: 'car:p', of: 'car', letter: 'p', label: 'Parked cars' },
  { id: 'person:w', of: 'person', letter: 'w', label: 'Workers' },
  { id: 'person:s', of: 'person', letter: 's', label: 'Students' },
  { id: 'person:h', of: 'person', letter: 'h', label: 'Out on errands' },
  { id: 'person:c', of: 'person', letter: 'c', label: 'Commuters on foot' },
  { id: 'person:x', of: 'person', letter: 'x', label: 'Wanderers' },
];

/** Each of a kind out now, across the towns: [town key, its place in the records]. */
export function ofKind(lives: Map<string, TownLife>, kind: (typeof ENTITY_KINDS)[number]): [string, number][] {
  const o: [string, number][] = [];
  for (const [key, life] of lives) {
    const s = kind.of === 'car' ? life.kinds?.cars : life.kinds?.people;
    if (!s) continue;
    for (let i = 0; i < s.length; i++) if (s[i] === kind.letter) o.push([key, i]);
  }
  return o;
}

/** A town's population table (Analytics.Population): a row a kind of person, then where they are now. */
export const POPULATION_COLUMNS = ['People', 'Home', 'Work / school', 'Elsewhere', 'Walking', 'Driving', 'Bus', 'Off map', 'Own a car'] as const;
export const POPULATION_KINDS: Record<string, string> = { Worker: 'Workers', Student: 'Students', AtHome: 'At home in the day', CommuterIn: 'Commuters', Through: 'Through traffic' };

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
  /** [kind, people, home, work or school, elsewhere, walking, driving, on the bus, off the map, own a car]. */
  population?: [string, ...number[]][];
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
  /** A person's age and household in a line ("34, an adult, in a household of 4 (a family with two children)"). Older servers: none. */
  household?: string | null;
  /** A person's name (PersonNames: the same as over them in the game); a car's driver's. Older servers: none. */
  name?: string;
  ownerName?: string | null;
  /** What they're doing, in a line. */
  doing: string;
  /** A person: Inside, OnTrip or OffMap; their car (-1: none). */
  where?: string;
  car?: number;
  /** A car: whose (-1: a bus or van), which bus (-1: none), its state. */
  owner?: number;
  bus?: number;
  /** A van on its rounds: which one (-1: not a van). */
  van?: number;
  /** A service vehicle's kind (Bins, Ambulance, FireEngine), and whether its siren's going. Older servers: none. */
  service?: string | null;
  siren?: boolean;
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
  /** A person's day (a car's: its owner's): where they are in it, and what decides what's next. */
  day?: DayView;
  /** A person's last few trips. */
  recent?: { purpose: string; mode: string; to: string; started: string; ended: string | null; made: boolean; failed: string | null }[];
}

/** Where someone is in their day (sim's Focus.cs Day). */
export interface DayView {
  /** Who they are, and the rule their day was drawn up by. */
  who: string;
  rule: string;
  /** Today's trips: made or failed, the one under way, the next and the later ones (and tomorrow's, once planned). */
  entries: { at: number; time: string; tomorrow: boolean; what: string; mode: string | null; status: 'done' | 'failed' | 'now' | 'next' | 'later'; note: string | null }[];
  /** What happens next, and what decides it, in a sentence. */
  decides: string;
}

/** A day's entry's mark: ✓ made, ✗ couldn't be, ▶ under way, → next, · later. */
export function dayMark(status: DayView['entries'][number]['status']): string {
  return { done: '✓', failed: '✗', now: '▶', next: '→', later: '·' }[status];
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

/** A town's traffic lights (sim's TownLife.Signals): each signal's stage showing green and when it ends, its timing, and its lamps. */
export interface Signals {
  town: string;
  /** The town's clock when asked (ms). */
  now: number;
  /** A stage's green before the signals were retimed by the traffic (an older server's every stage). */
  stageMs: number;
  /** `greens`: each stage's green (ms, amber included), Webster's split of the traffic measured (absent from an older server). */
  lights: { node: number; stage: number; ends: number; greens?: [number, number]; lamps: [number, number, number][] }[];
  /** Its stop signs, when asked for (&stops=1): x, y, 1 if an all-way stop. */
  stops?: [number, number, number][];
}

/** The last few seconds of a green show amber. */
export const AMBER_MS = 3_000;

/**
 * A lamp's colour at town time `now`: green if its stage is the one showing (amber in its last seconds), red if not.
 * Before and past the stage it was asked in, the stages take turns on the light's own greens (each `stageMs` from an
 * older server), as the server's lights do until it says one changed early (StreetFurniture.Showing).
 */
export function lampColor(light: { stage: number; ends: number; greens?: [number, number] }, lampStage: number, now: number, stageMs: number): 'green' | 'amber' | 'red' {
  const [g0, g1] = light.greens ?? [stageMs, stageMs];
  const period = Math.max(1, g0 + g1);
  const anchor = light.stage === 0 ? g0 : period;
  const into = (((anchor - (light.ends - now)) % period) + period) % period;
  const showing = into < g0 ? 0 : 1;
  const left = (showing === 0 ? g0 : period) - into;
  if (lampStage !== showing) return 'red';
  return left <= AMBER_MS ? 'amber' : 'green';
}

/** GET /mp/sim/congestion (sim's SimTown.Congestion): the last day's hold-ups an hour at a time, and the streets that held cars up most in the hour asked about. */
export interface Congestion {
  town: string;
  /** Hours back from this one (0: this hour so far). */
  ago: number;
  /** The last 24 hours, oldest first: when each started (unix ms, and as hh:mm), cars that left a lane in it, and their hold-ups (s, in all). */
  hours: { from: number; time: string; cars: number; hold: number }[];
  /** The worst streets that hour, worst first: kind (0 main, 1 street), cars, hold-ups (s, in all and on average), centreline (town tiles: x, y, …). */
  streets: CongestedStreet[];
}

export interface CongestedStreet {
  road: number;
  /** Its street's name ("High Street"), and this stretch of it ("High Street, Elm Street to Oak Lane"); older servers: none. */
  name?: string;
  stretch?: string;
  kind: number;
  cars: number;
  hold: number;
  mean: number;
  points: number[];
}

/** A congested stretch as listed: its street and the streets at its ends, or (unnamed) what kind of road it is. */
export function streetText(s: CongestedStreet): string {
  return s.stretch || s.name || `${s.kind === 0 ? 'Main road' : 'Street'} ${s.road}`;
}

/** A congested stretch's street ("High Street"), as its signs in the game have it; unnamed, what kind of road it is. */
export function streetName(s: CongestedStreet): string {
  return s.name || (s.stretch ? s.stretch.split(', ')[0] : `${s.kind === 0 ? 'Main road' : 'Street'} ${s.road}`);
}

/** Where along it the stretch is, between the streets at its ends ("Elm Street to Oak Lane", "at Elm Street"), or "". */
export function streetBetween(s: CongestedStreet): string {
  const name = streetName(s);
  return s.stretch && s.stretch.startsWith(`${name}, `) ? s.stretch.slice(name.length + 2) : '';
}

/** A hold-up in a few characters: "40 s", "12 min", "3.4 h". */
export function heldText(seconds: number): string {
  if (seconds < 90) return `${Math.round(seconds)} s`;
  if (seconds < 90 * 60) return `${Math.round(seconds / 60)} min`;
  return `${(seconds / 3600).toFixed(1)} h`;
}

/** Each hour's bar on the slider, 0–1 of the worst hour's hold-ups. */
export function holdBars(hours: Congestion['hours']): number[] {
  const most = Math.max(0, ...hours.map((h) => h.hold));
  return hours.map((h) => (most > 0 ? h.hold / most : 0));
}

/** A street drawn as congested: as wide as its share of the worst one's hold-ups (1–6 px, before zoom). */
export function congestionWidth(hold: number, worst: number): number {
  return worst > 0 ? 1 + 5 * Math.min(1, hold / worst) : 1;
}

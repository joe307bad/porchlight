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

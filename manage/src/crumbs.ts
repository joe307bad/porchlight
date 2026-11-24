// Where the sidebar is, as breadcrumbs: the county (its towns), a town (its
// congestion, then everything out in it, a section a sort, one open at a time), then whatever was
// clicked in it (a car or person, followed; a building), and on from there (a
// driver's car, someone in a building). Going back to a crumb picks up as it
// was left: a town keeps its open section, its hour of congestion, how far down its
// list was shown and who was clicked from it (Memory). Pure: App.tsx moves the
// map to match (the whole county, the town, the one followed).

import { ENTITY_KINDS, type FocusTarget, type TownLife, ofKind } from './life.ts';

export type Crumb =
  | { at: 'county' }
  | { at: 'town'; town: string }
  | { at: 'entity'; town: string; target: FocusTarget }
  | { at: 'building'; town: string; building: number };

/** The town's sections after its congestion: each a sort of thing out in it (ENTITY_KINDS' ids), every kind in one of them. */
export const TABS: { id: string; label: string; kinds: string[] }[] = [
  { id: 'cars', label: 'Cars', kinds: ['car:r', 'car:c', 'car:t'] },
  { id: 'vans', label: 'Vans', kinds: ['car:v'] },
  { id: 'buses', label: 'Buses', kinds: ['car:b'] },
  { id: 'services', label: 'Services', kinds: ['car:g', 'car:e'] },
  { id: 'people', label: 'People', kinds: ['person:w', 'person:s', 'person:h', 'person:c', 'person:m', 'person:x'] },
  { id: 'parked', label: 'Parked', kinds: ['car:p'] },
  { id: 'boats', label: 'Boats', kinds: ['car:s'] },
];

/** The congestion's section (the first, and open the first time in). */
export const CONGESTION = 'congestion';

/** The calendar's section: today, the week ahead, and the market. */
export const CALENDAR = 'calendar';

/** How many of a section are listed at first, and how many more each "Show more" adds. */
export const PAGE = 40;

/** What a town's crumb was left showing. */
export interface TownMemory {
  /** The section open (CONGESTION or a TABS id), or none. */
  open: string | null;
  /** Hours back the congestion was looking (0: this hour). */
  ago: number;
  /** How many of the open section were listed. */
  shown: number;
  /** The one clicked from the list last ("car|12": its record), highlighted on coming back. */
  picked: string | null;
}

export class Memory {
  /** The town clicked from the county last, highlighted on coming back. */
  town: string | null = null;
  private readonly towns = new Map<string, TownMemory>();

  of(town: string): TownMemory {
    return this.towns.get(town) ?? { open: CONGESTION, ago: 0, shown: PAGE, picked: null };
  }

  set(town: string, patch: Partial<TownMemory>): TownMemory {
    const m = { ...this.of(town), ...patch };
    // A section opened anew starts at its first page.
    if (patch.open !== undefined && patch.open !== this.of(town).open && patch.shown === undefined) m.shown = PAGE;
    this.towns.set(town, m);
    return m;
  }
}

export const COUNTY: Crumb = { at: 'county' };

/** Into a town from the county. */
export function intoTown(town: string): Crumb[] {
  return [COUNTY, { at: 'town', town }];
}

/**
 * On to a car or person: from a town's list or a building, a crumb further on; from another one (their driver,
 * their car) in its place, so toggling between a car and its driver stays one crumb under the town (or building);
 * from the county, or the map (`fresh`), the county, their town, them.
 */
export function onTo(stack: Crumb[], target: FocusTarget, fresh = false): Crumb[] {
  const top = stack[stack.length - 1];
  const crumb: Crumb = { at: 'entity', town: target.town, target };
  if (fresh || top.at === 'county' || ('town' in top && top.town !== target.town)) return [...intoTown(target.town), crumb];
  if (top.at === 'entity') return sameTarget(top.target, target) ? stack : [...stack.slice(0, -1), crumb];
  return [...stack, crumb];
}

/** On to a building clicked on the map: the county, its town, it. */
export function onToBuilding(town: string, building: number): Crumb[] {
  return [...intoTown(town), { at: 'building', town, building }];
}

/** Back to crumb i (and everything after it gone). */
export function backTo(stack: Crumb[], i: number): Crumb[] {
  return stack.slice(0, Math.max(1, Math.min(stack.length, i + 1)));
}

export function sameTarget(a: FocusTarget, b: FocusTarget): boolean {
  return a.town === b.town && a.kind === b.kind && a.id === b.id;
}

/**
 * A crumb's word: the county, a town's name, "Car 12", a person's name once it's known ("Ada Brekke"; till then
 * "Person 40"), "Building 7" (its number, as the map's hover has it).
 */
export function crumbLabel(c: Crumb, townName: (key: string) => string, personName?: (town: string, id: number) => string | undefined): string {
  switch (c.at) {
    case 'county':
      return 'County';
    case 'town':
      return townName(c.town);
    case 'entity':
      if (c.target.kind === 'car') return `Car ${c.target.id}`;
      return personName?.(c.town, c.target.id) ?? `Person ${c.target.id}`;
    case 'building':
      return `Building ${c.building}`;
  }
}

/** Each of a section's sort out now in a town: [kind, its record], cars then people as ENTITY_KINDS has them. */
export function tabEntities(life: TownLife | undefined, tab: string): { of: 'car' | 'person'; slot: number; kind: string }[] {
  const t = TABS.find((x) => x.id === tab);
  if (!life || !t) return [];
  const here = new Map([[life.town, life]]);
  const o: { of: 'car' | 'person'; slot: number; kind: string }[] = [];
  for (const k of ENTITY_KINDS) if (t.kinds.includes(k.id)) for (const [, slot] of ofKind(here, k)) o.push({ of: k.of, slot, kind: k.label });
  return o;
}

/** A town's people, for its line in the county's list. */
export function population(life: TownLife | undefined): number | null {
  return life ? life.summary.people : null;
}

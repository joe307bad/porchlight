// Where the sidebar is, as breadcrumbs: the county (its towns), a town (its
// congestion and everything out in it, a tab a sort), then whatever was
// clicked in it (a car or person, followed; a building), and on from there (a
// driver's car, someone in a building). Going back to a crumb picks up as it
// was left: a town keeps its tab, its hour of congestion, how far down its
// list was shown and who was clicked from it (Memory). Pure: App.tsx moves the
// map to match (the whole county, the town, the one followed).

import { ENTITY_KINDS, type FocusTarget, type TownLife, ofKind } from './life.ts';

export type Crumb =
  | { at: 'county' }
  | { at: 'town'; town: string }
  | { at: 'entity'; town: string; target: FocusTarget }
  | { at: 'building'; town: string; building: number };

/** The town's tabs: each a sort of thing out in it (ENTITY_KINDS' ids), every kind in one of them. */
export const TABS: { id: string; label: string; kinds: string[] }[] = [
  { id: 'cars', label: 'Cars', kinds: ['car:r', 'car:c', 'car:t'] },
  { id: 'vans', label: 'Vans', kinds: ['car:v'] },
  { id: 'buses', label: 'Buses', kinds: ['car:b'] },
  { id: 'people', label: 'People', kinds: ['person:w', 'person:s', 'person:h', 'person:c', 'person:x'] },
  { id: 'parked', label: 'Parked', kinds: ['car:p'] },
  { id: 'boats', label: 'Boats', kinds: ['car:s'] },
];

/** How many of a tab are listed at first, and how many more each "Show more" adds. */
export const PAGE = 40;

/** What a town's crumb was left showing. */
export interface TownMemory {
  tab: string;
  /** Hours back the congestion was looking (0: this hour). */
  ago: number;
  /** How many of the tab were listed. */
  shown: number;
  /** The one clicked from the list last ("car|12": its record), highlighted on coming back. */
  picked: string | null;
}

export class Memory {
  /** The town clicked from the county last, highlighted on coming back. */
  town: string | null = null;
  private readonly towns = new Map<string, TownMemory>();

  of(town: string): TownMemory {
    return this.towns.get(town) ?? { tab: TABS[0].id, ago: 0, shown: PAGE, picked: null };
  }

  set(town: string, patch: Partial<TownMemory>): TownMemory {
    const m = { ...this.of(town), ...patch };
    // A tab picked anew starts at its first page.
    if (patch.tab !== undefined && patch.tab !== this.of(town).tab && patch.shown === undefined) m.shown = PAGE;
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
 * On to a car or person: from a town's list, a building or another one (their driver, their car), a crumb further on;
 * from the county, or the map (`fresh`), the county, their town, them.
 */
export function onTo(stack: Crumb[], target: FocusTarget, fresh = false): Crumb[] {
  const top = stack[stack.length - 1];
  const crumb: Crumb = { at: 'entity', town: target.town, target };
  if (fresh || top.at === 'county' || ('town' in top && top.town !== target.town)) return [...intoTown(target.town), crumb];
  if (top.at === 'entity' && sameTarget(top.target, target)) return stack;
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

/** A crumb's word: the county, a town's name, "Car 12", "Person 40", "Building". */
export function crumbLabel(c: Crumb, townName: (key: string) => string): string {
  switch (c.at) {
    case 'county':
      return 'County';
    case 'town':
      return townName(c.town);
    case 'entity':
      return `${c.target.kind === 'car' ? 'Car' : 'Person'} ${c.target.id}`;
    case 'building':
      return 'Building';
  }
}

/** Each of a tab's sort out now in a town: [kind, its record], cars then people as ENTITY_KINDS has them. */
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

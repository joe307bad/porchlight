// What a town's buildings are for (land use), as the game zones them when it
// makes the town from its seed (unity/.../Explore/Zoning.cs): homes, work, or
// both. Drawn on the map under the streets, and summed up in the panel.

import type { CountyMap } from './api.ts';

export type Town = CountyMap['towns'][number];

/** Zoning.Zone, in its order: a building's [0]. */
export const ZONES = [
  { label: 'Residential', color: '#d9b44a', business: false, homes: true },
  { label: 'Mixed use', color: '#e8833a', business: true, homes: true },
  { label: 'Retail', color: '#e2525c', business: true, homes: false },
  { label: 'Office', color: '#c85fb4', business: true, homes: false },
  { label: 'Industrial', color: '#8f78e0', business: true, homes: false },
  { label: 'Civic', color: '#4a9ee0', business: false, homes: false },
] as const;

/** A balanced town (Zoning.JOBS_PER_HOME): a job for every working resident, about 1.2 a household. */
export const JOBS_PER_HOME = 1.2;
/** The band planners count as balanced, jobs a household (US EPA EnviroAtlas). */
export const BALANCED = { low: 0.75, high: 1.5 };

export interface LandUse {
  buildings: number;
  /** Buildings by zone, in ZONES order. */
  byZone: number[];
  /** Buildings with homes only, with work only (shops, offices, industry), and with both. */
  residential: number;
  business: number;
  mixed: number;
  homes: number;
  jobs: number;
  residents: number;
  workers: number;
  jobsPerHome: number;
  balance: 'balanced' | 'too few jobs' | 'too many jobs';
}

/** A town's land use, or null if it isn't known yet (a player's town the simulation hasn't made). */
export function landUse(t: Pick<Town, 'zoning' | 'buildings'>): LandUse | null {
  if (!t.buildings || !t.zoning) return null;
  const byZone = ZONES.map(() => 0);
  for (const b of t.buildings) if (b[0] >= 0 && b[0] < byZone.length) byZone[b[0]]++;
  const mixed = byZone[1];
  const business = byZone[2] + byZone[3] + byZone[4];
  const { homes, jobs, residents, workers } = t.zoning;
  const jobsPerHome = homes ? jobs / homes : 0;
  return {
    buildings: t.buildings.length,
    byZone,
    residential: byZone[0],
    business,
    mixed,
    homes,
    jobs,
    residents,
    workers,
    jobsPerHome,
    balance: jobsPerHome < BALANCED.low ? 'too few jobs' : jobsPerHome > BALANCED.high ? 'too many jobs' : 'balanced',
  };
}

/** One building, said: what it's for and what it holds. */
export function describe(b: number[]): string {
  const zone = ZONES[b[0]]?.label ?? 'Building';
  const parts: string[] = [zone];
  if (b[5]) parts.push(`${b[5].toLocaleString()} ${b[5] === 1 ? 'home' : 'homes'}`);
  if (b[6]) parts.push(`${b[6].toLocaleString()} ${b[6] === 1 ? 'job' : 'jobs'}`);
  return parts.join(' · ');
}

/** The building of this town at (x, y), the town's frame; or null. */
export function buildingAt(t: Pick<Town, 'buildings'>, x: number, y: number): number[] | null {
  for (const b of t.buildings ?? []) if (x >= b[1] && x < b[3] && y >= b[2] && y < b[4]) return b;
  return null;
}

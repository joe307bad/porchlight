// The street editor's wire, as docs/street-editor-api.md has it: the skeleton an editor builds (a downtown and
// the regions to make again), and the town, problems, changes and day the draft worker answers with. Points are
// [x, y] in the town's own tiles.

export type Pt = [number, number];
export type RoadClass = 'arterial' | 'collector' | 'local';
export type Control = 'auto' | 'free' | 'stop' | 'allway' | 'signal' | 'roundabout';

export interface SkeletonRoad {
  pts: Pt[];
  kind: 'main' | 'street' | 'path';
  cls?: RoadClass;
}

export interface RoadOverride {
  a: Pt;
  b: Pt;
  cls?: RoadClass;
  lanes?: number;
  divided?: boolean;
  oneWay?: boolean;
  locked?: boolean;
}

export interface JunctionOverride {
  at: Pt;
  control: Control;
}

/** What an editor would like in a region they regenerate (the panel's checkboxes). */
export interface Want {
  roundabout: boolean;
  park: boolean;
  school: boolean;
  shops: boolean;
  houses: boolean;
}

/** Blocks to be made again: their polygons as they were when picked, a salt (another re-rolls it), and what's wanted. */
export interface Region {
  blocks: Pt[][];
  salt: number;
  want: Want;
}

export interface Skeleton {
  v: 1;
  /** The downtown bullseye (tall buildings, shops, the core), or absent for the generator's own. */
  center?: Pt;
  /** Applied in order, later ones over earlier. */
  regions?: Region[];
  // An older draft's road edits: kept as they are, never made by the edit screen now.
  mains?: SkeletonRoad[];
  streets?: SkeletonRoad[];
  removed?: [Pt, Pt][];
  roads?: RoadOverride[];
  junctions?: JunctionOverride[];
}

export interface Pocket {
  atTo: boolean;
  left: boolean;
  length: number;
}

export interface RoadDesign {
  lanes: [number, number];
  width: number;
  divided: boolean;
  pockets: Pocket[];
  flow: [number, number];
  vc: number;
  relief: boolean;
  locked: boolean;
}

export interface TownNode {
  id: number;
  at: Pt;
  legs: number;
  gate: boolean;
}

export interface TownRoad {
  id: number;
  from: number;
  to: number;
  kind: 'main' | 'street' | 'path';
  cls: RoadClass;
  pts: Pt[];
  name?: string;
  width: number;
  design: RoadDesign | null;
}

export interface TownJunction {
  node: number;
  at: Pt;
  form: 'plain' | 'roundabout';
  radius: number;
  control: 'free' | 'stop' | 'allway' | 'signal' | 'yield' | 'auto';
  protectedLeft: boolean;
  slips: Pt[][];
  entering: number;
}

export interface TownBlock {
  id: number;
  poly: Pt[];
  transect: number;
  era: 'core' | 'garden' | 'postwar';
  park: boolean;
}

export type BuildingKind = 'house' | 'shop' | 'workshop' | 'church' | 'hall' | 'school' | 'supermarket' | 'petrol' | 'fire' | 'clinic';

export interface TownBuilding {
  id: number;
  kind: BuildingKind;
  poly: Pt[];
  floors: number;
}

export interface Town {
  width: number;
  height: number;
  center: Pt;
  /** The downtown the plan used (the bullseye, or the generator's own). */
  downtown?: Pt;
  secondCentre: Pt | null;
  townRadius: number;
  gates: Pt[];
  /** One bit a tile, row by row, lowest bit first, base64. */
  water: string;
  nodes: TownNode[];
  roads: TownRoad[];
  junctions: TownJunction[];
  blocks: TownBlock[];
  buildings: TownBuilding[];
  notes: { at: Pt; text: string }[];
  stats: Record<string, number | Record<string, number>>;
}

/** Only warnings now: the worker solves what it can itself. (`error` is kept for an older server's answers.) */
export interface Problem {
  level: 'warning' | 'error';
  text: string;
  at: Pt | null;
}

export interface Changes {
  /** The people living in the town, before and after. */
  population: [number, number];
  shops: [number, number];
  roadsAdded: number;
  roadsRemoved: number;
  roundabouts: [number, number];
  signals: [number, number];
}

export interface PlanAnswer {
  ok: boolean;
  error?: string;
  ms?: number;
  seq?: number;
  town?: Town;
  problems?: Problem[];
  changes?: Changes;
  /** The skeleton as it was actually planned (a region's salt moved on, or a region dropped): the draft takes it. */
  skeleton?: Skeleton;
  /** Whether each region came out with what was asked. */
  regions?: RegionResult[];
}

export interface RegionResult {
  index: number;
  ok: boolean;
  text?: string;
}

export interface Day {
  trips: number;
  meanCarMin: number;
  p90CarMin: number;
  meanWalkMin: number;
  failed: number;
  gaveUp: number;
  worst: { at: Pt; delayS: number; control: string }[];
  pockets: { at: Pt; storage: number; worstQueue: number }[];
}

/** What the edit screen keeps with a draft (the server stores it as it is): the skeleton it started from, and undo. */
export interface Commands {
  base: Skeleton;
  undo: { label: string; skeleton: Skeleton }[];
  redo: { label: string; skeleton: Skeleton }[];
}

export interface Draft {
  town: string;
  baseRev: number;
  skeleton: Skeleton;
  commands: Commands | null;
  updatedAt: string;
  stale: boolean;
  current: { rev: number; skeleton: Skeleton | null };
}

export interface EditableTown {
  key: string;
  name: string;
  rev: number;
  mine: boolean;
  editable: boolean;
}

export interface CommitAnswer {
  key: string;
  rev: number;
  rehoused: number;
  players: number;
}

export const emptySkeleton = (): Skeleton => ({ v: 1 });

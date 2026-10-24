// TownModel: the single contract between generation and every renderer.
// Plain data only — entities refer to each other by integer id, never by reference.

import type { GenParams } from './params';

export type Vec2 = { x: number; y: number }; // world units; 1 unit = 1 tile

export const Terrain = {
  GRASS: 0,
  DIRT: 1,
  SAND: 2,
  WATER_SHALLOW: 3,
  WATER_DEEP: 4,
  ROCK: 5,
} as const;
export type Terrain = (typeof Terrain)[keyof typeof Terrain];

export const Overlay = {
  NONE: 0,
  ROAD_MAIN: 1,
  ROAD_STREET: 2,
  PATH: 3,
  BRIDGE: 4,
  PLAZA: 5,
} as const;
export type Overlay = (typeof Overlay)[keyof typeof Overlay];

/** Reverse lookup for enum objects, e.g. enumName(Terrain, 3) === 'WATER_SHALLOW'. */
export function enumName(e: Record<string, number>, value: number): string {
  for (const key in e) if (e[key] === value) return key;
  return `?${value}`;
}

export interface River {
  id: number;
  points: Vec2[];
  /** Width in tiles at each point, same length as points. */
  widthAt: number[];
}

export type RoadKind = 'main' | 'street' | 'path';

export interface RoadNode {
  id: number;
  at: Vec2;
}

export interface RoadEdge {
  id: number;
  from: number; // RoadNode id
  to: number; // RoadNode id
  kind: RoadKind;
  /** Polyline from `from` to `to`, endpoints included. */
  points: Vec2[];
}

export interface RoadGraph {
  nodes: RoadNode[];
  edges: RoadEdge[];
}

export interface Bridge {
  id: number;
  roadEdgeId: number;
  from: Vec2;
  to: Vec2;
}

export interface Block {
  id: number;
  polygon: Vec2[];
  /** Enclosed areas that aren't part of the block (ponds, rock). */
  holes: Vec2[][];
}

export interface Lot {
  id: number;
  blockId: number;
  polygon: Vec2[];
  frontageEdgeId: number;
}

export type Facing = 'N' | 'E' | 'S' | 'W';
export type BuildingKind = 'house' | 'shop' | 'workshop' | 'church' | 'hall';

export interface Building {
  id: number;
  lotId: number;
  footprint: Vec2[];
  /** Tiles covered by the footprint (iso hint). */
  tiles: Vec2[];
  kind: BuildingKind;
  floors: number;
  facing: Facing;
}

export type LandmarkKind = 'plaza' | 'well' | 'church' | 'market';

export interface Landmark {
  id: number;
  kind: LandmarkKind;
  at: Vec2;
  /** Lots the landmark occupies (plaza lots, the church lot); empty for point landmarks. */
  lotIds: number[];
}

/** Derived fields kept for the debug views. */
export interface DebugFields {
  /** Upstream tile count draining through each tile. */
  flow: Float32Array;
  /** A* cost of entering each tile for main roads; Infinity where blocked. */
  roadCost: Float32Array;
}

export interface TileGrid {
  width: number;
  height: number;
  terrain: Uint8Array; // Terrain
  overlay: Uint8Array; // Overlay
  buildingId: Int32Array; // -1 or index into buildings
  elevation: Uint8Array; // quantized height steps for iso stacking
  walkable: Uint8Array; // 0 / 1
}

export interface TownModel {
  seed: string;
  params: GenParams;
  width: number;
  height: number;

  heightmap: Float32Array; // 0..1, row-major
  waterLevel: number;

  center: Vec2; // town center, picked in stage 3
  townRadius: number; // tiles
  gates: Vec2[]; // where main roads leave the map

  rivers: River[];
  roads: RoadGraph;
  bridges: Bridge[];
  blocks: Block[];
  lots: Lot[];
  buildings: Building[];
  landmarks: Landmark[];

  tiles: TileGrid;
  spawn: Vec2;

  fields: DebugFields;
}

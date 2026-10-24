// Blueprint palette. Stroke widths in screen pixels; road widths come from GenParams.

import type { BuildingKind } from '../../model/types';

type RGB = readonly [number, number, number];

export const STYLE = {
  background: '#2b2a27',
  parchment: [239, 230, 207] as RGB,
  parchmentShadow: [196, 180, 146] as RGB,
  border: '#6b5d45',
  contourMajor: 'rgba(120, 98, 64, 0.55)',
  contourMinor: 'rgba(120, 98, 64, 0.2)',
  water: [156, 195, 217] as RGB,
  waterDeep: [112, 160, 196] as RGB,
  shore: '#5d88a8',
  river: '#8fbad6',
  riverEdge: '#5d88a8',
  block: 'rgba(214, 199, 160, 0.35)',
  blockEdge: 'rgba(140, 118, 80, 0.45)',
  lotEdge: 'rgba(120, 100, 70, 0.55)',
  road: { main: '#3d342b', street: '#6a5b4b', path: '#8d7556' },
  sidewalk: '#d9cfb4',
  bridgeDeck: '#c49a6c',
  bridgeRail: '#5b3a1e',
  building: {
    house: '#7a5c4a',
    shop: '#a0583c',
    workshop: '#6e6a5e',
    church: '#7d2e3e',
    hall: '#3e5a7d',
  } satisfies Record<BuildingKind, string>,
  buildingEdge: '#2e241d',
  door: '#f4e9cf',
  plaza: '#e8d9b0',
  plazaEdge: '#a48b5c',
  landmark: '#2e241d',
  gate: '#3d342b',
  grid: 'rgba(90, 76, 52, 0.22)',
  spawn: '#c2412d',
  spawnRing: '#fff8e8',
} as const;

/** Tile-view colours, indexed by the Terrain and Overlay enums. */
export const TILE_TERRAIN: readonly RGB[] = [
  [184, 204, 140], // GRASS
  [176, 146, 104], // DIRT
  [226, 210, 156], // SAND
  [126, 176, 214], // WATER_SHALLOW
  [72, 120, 178], // WATER_DEEP
  [142, 136, 126], // ROCK
];
export const TILE_OVERLAY: readonly (RGB | null)[] = [
  null, // NONE
  [70, 58, 46], // ROAD_MAIN
  [112, 96, 80], // ROAD_STREET
  [150, 122, 84], // PATH
  [164, 104, 52], // BRIDGE
  [222, 204, 160], // PLAZA
];
export const TILE_BUILDING: RGB = [128, 64, 52];

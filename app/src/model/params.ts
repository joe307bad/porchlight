// Every generator tunable lives here with a default. The UI builds its sliders
// from PARAM_SPECS, and the URL hash stores any value that differs from default.
// Heights are 0..1; slopes are height change per tile.

export interface GenParams {
  // map
  width: number;
  height: number;
  // terrain
  noiseScale: number; // tiles per largest noise feature
  noiseOctaves: number;
  edgeFalloff: number; // fraction of the map flattened toward the edges
  waterFraction: number; // share of tiles below the water level
  contourSteps: number; // major contour bands on the blueprint
  rockSlope: number; // slope at which ground turns to rock
  // rivers
  riverCount: number;
  riverMinLength: number; // tiles
  riverMaxWidth: number; // tiles
  // town
  townRadius: number; // fraction of the smaller map side
  gateCount: number;
  maxRoadSlope: number;
  bridgeCost: number; // extra A* cost per water tile crossed
  mainRoadWidth: number; // tiles, as drawn
  // streets
  roadDensity: number; // 0..1 chance streets branch
  streetSegment: number; // tiles per growth step
  streetSpacing: number; // tiles between side streets
  streetWidth: number; // tiles, as drawn
  // lots and buildings
  lotMinSize: number; // tiles
  lotMaxSize: number; // tiles
  buildingDensity: number; // 0..1 share of lots that get a building
  maxBuildingSize: number; // tiles per side
  frontSetback: number; // tiles of front yard between a building and the sidewalk in front of it, where the lot is deep enough
}

export const DEFAULT_PARAMS: GenParams = {
  width: 128,
  height: 128,
  noiseScale: 96,
  noiseOctaves: 4,
  edgeFalloff: 0.15,
  waterFraction: 0.08,
  contourSteps: 4,
  rockSlope: 0.055,
  riverCount: 2,
  riverMinLength: 24,
  riverMaxWidth: 4,
  townRadius: 0.36,
  gateCount: 3,
  maxRoadSlope: 0.04,
  bridgeCost: 12,
  mainRoadWidth: 2,
  roadDensity: 0.85,
  streetSegment: 5,
  streetSpacing: 9,
  streetWidth: 1,
  lotMinSize: 8,
  lotMaxSize: 30,
  buildingDensity: 0.85,
  maxBuildingSize: 7,
  frontSetback: 0,
};

export type ParamGroup = 'Map' | 'Terrain' | 'Rivers' | 'Town & roads' | 'Streets' | 'Lots & buildings';

export interface ParamSpec {
  key: keyof GenParams;
  label: string;
  group: ParamGroup;
  min: number;
  max: number;
  step: number;
}

export const PARAM_SPECS: readonly ParamSpec[] = [
  { key: 'width', label: 'Map width', group: 'Map', min: 32, max: 256, step: 16 },
  { key: 'height', label: 'Map height', group: 'Map', min: 32, max: 256, step: 16 },
  { key: 'noiseScale', label: 'Feature size', group: 'Terrain', min: 16, max: 160, step: 4 },
  { key: 'noiseOctaves', label: 'Octaves', group: 'Terrain', min: 4, max: 6, step: 1 },
  { key: 'edgeFalloff', label: 'Edge falloff', group: 'Terrain', min: 0, max: 0.4, step: 0.01 },
  { key: 'waterFraction', label: 'Water fraction', group: 'Terrain', min: 0, max: 0.3, step: 0.01 },
  { key: 'contourSteps', label: 'Contour steps', group: 'Terrain', min: 2, max: 8, step: 1 },
  { key: 'rockSlope', label: 'Rock slope', group: 'Terrain', min: 0.01, max: 0.1, step: 0.001 },
  { key: 'riverCount', label: 'Rivers', group: 'Rivers', min: 0, max: 3, step: 1 },
  { key: 'riverMinLength', label: 'Min length', group: 'Rivers', min: 8, max: 80, step: 1 },
  { key: 'riverMaxWidth', label: 'Max width', group: 'Rivers', min: 1, max: 8, step: 0.5 },
  { key: 'townRadius', label: 'Town radius', group: 'Town & roads', min: 0.15, max: 0.5, step: 0.01 },
  { key: 'gateCount', label: 'Gates', group: 'Town & roads', min: 2, max: 4, step: 1 },
  { key: 'maxRoadSlope', label: 'Max road slope', group: 'Town & roads', min: 0.02, max: 0.1, step: 0.001 },
  { key: 'bridgeCost', label: 'Bridge cost', group: 'Town & roads', min: 2, max: 60, step: 1 },
  { key: 'mainRoadWidth', label: 'Main road width', group: 'Town & roads', min: 1, max: 3, step: 0.25 },
  { key: 'roadDensity', label: 'Street density', group: 'Streets', min: 0, max: 1, step: 0.01 },
  { key: 'streetSegment', label: 'Segment length', group: 'Streets', min: 3, max: 10, step: 0.5 },
  { key: 'streetSpacing', label: 'Side street spacing', group: 'Streets', min: 5, max: 20, step: 1 },
  { key: 'streetWidth', label: 'Street width', group: 'Streets', min: 0.5, max: 2, step: 0.25 },
  { key: 'lotMinSize', label: 'Lot min size', group: 'Lots & buildings', min: 4, max: 40, step: 1 },
  { key: 'lotMaxSize', label: 'Lot max size', group: 'Lots & buildings', min: 12, max: 120, step: 1 },
  { key: 'buildingDensity', label: 'Building density', group: 'Lots & buildings', min: 0, max: 1, step: 0.01 },
  { key: 'maxBuildingSize', label: 'Max building size', group: 'Lots & buildings', min: 2, max: 12, step: 1 },
  { key: 'frontSetback', label: 'Front setback', group: 'Lots & buildings', min: 0, max: 3, step: 1 },
];

/** Fill in defaults, snap to each spec's step and clamp to its range. */
export function normalizeParams(partial: Partial<GenParams>): GenParams {
  const out = { ...DEFAULT_PARAMS };
  for (const spec of PARAM_SPECS) {
    const v = partial[spec.key];
    if (typeof v !== 'number' || !Number.isFinite(v)) continue;
    const stepped = Math.round((v - spec.min) / spec.step) * spec.step + spec.min;
    out[spec.key] = Math.min(spec.max, Math.max(spec.min, Number(stepped.toFixed(6))));
  }
  if (out.lotMaxSize < out.lotMinSize * 2) out.lotMaxSize = out.lotMinSize * 2;
  return out;
}

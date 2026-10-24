import type { GenParams } from './params';
import { Overlay, Terrain, type TileGrid, type TownModel } from './types';

export function createTileGrid(width: number, height: number): TileGrid {
  const n = width * height;
  return {
    width,
    height,
    terrain: new Uint8Array(n).fill(Terrain.GRASS),
    overlay: new Uint8Array(n).fill(Overlay.NONE),
    buildingId: new Int32Array(n).fill(-1),
    elevation: new Uint8Array(n),
    walkable: new Uint8Array(n),
  };
}

/** A model with every layer present but empty; pipeline stages fill it in. */
export function createEmptyModel(seed: string, params: GenParams): TownModel {
  const { width, height } = params;
  return {
    seed,
    params: { ...params },
    width,
    height,
    heightmap: new Float32Array(width * height),
    waterLevel: 0,
    center: { x: width / 2, y: height / 2 },
    townRadius: 0,
    gates: [],
    rivers: [],
    roads: { nodes: [], edges: [] },
    bridges: [],
    blocks: [],
    lots: [],
    buildings: [],
    landmarks: [],
    tiles: createTileGrid(width, height),
    spawn: { x: width / 2, y: height / 2 },
    fields: {
      flow: new Float32Array(width * height),
      roadCost: new Float32Array(width * height),
    },
  };
}

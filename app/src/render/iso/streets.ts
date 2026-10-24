// Tiles you can stand on in "streets only" walking: roads (with their
// sidewalks), bridges, paths and the plaza.

import { Overlay, type TileGrid } from '../../model/types';

const STREET = new Set<number>([Overlay.ROAD_MAIN, Overlay.ROAD_STREET, Overlay.BRIDGE, Overlay.PATH, Overlay.PLAZA]);

export function streetMask(tiles: TileGrid): Uint8Array {
  const out = new Uint8Array(tiles.width * tiles.height);
  for (let i = 0; i < out.length; i++) out[i] = STREET.has(tiles.overlay[i]) && tiles.buildingId[i] < 0 ? 1 : 0;
  return out;
}

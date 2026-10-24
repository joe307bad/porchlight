// Neighbour bitmasks for picking iso corner/edge sprites. Bit k is set when the
// neighbour in DIRS4[k] (or DIRS8[k]) matches; bit 0 is north, then clockwise.
// Off-map neighbours count as matching so map edges don't draw false borders.

import type { TileGrid } from '../model/types';
import { Overlay, Terrain } from '../model/types';
import { DIRS4, DIRS8 } from './grid';

export function autotileMask(
  tiles: TileGrid,
  x: number,
  y: number,
  match: (index: number) => boolean,
  neighbours: 4 | 8 = 4,
): number {
  const dirs = neighbours === 4 ? DIRS4 : DIRS8;
  let mask = 0;
  dirs.forEach(([dx, dy], bit) => {
    const nx = x + dx;
    const ny = y + dy;
    const off = nx < 0 || ny < 0 || nx >= tiles.width || ny >= tiles.height;
    if (off || match(ny * tiles.width + nx)) mask |= 1 << bit;
  });
  return mask;
}

const ROAD_OVERLAYS = new Set<number>([Overlay.ROAD_MAIN, Overlay.ROAD_STREET, Overlay.PATH, Overlay.BRIDGE]);

export const isRoad = (tiles: TileGrid) => (i: number) => ROAD_OVERLAYS.has(tiles.overlay[i]);

export const isWater = (tiles: TileGrid) => (i: number) =>
  tiles.terrain[i] === Terrain.WATER_SHALLOW || tiles.terrain[i] === Terrain.WATER_DEEP;

/** Matches neighbours at the same or higher elevation; unset bits mark a drop (cliff edge). */
export const notLowerThan = (tiles: TileGrid, level: number) => (i: number) => tiles.elevation[i] >= level;

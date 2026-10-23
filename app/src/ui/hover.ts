// Readout for the tile under the cursor.

import { index, inBounds } from '../core/grid';
import { Overlay, Terrain, enumName, type TownModel, type Vec2 } from '../model/types';

export function describeTile(model: TownModel, world: Vec2 | null): string {
  if (!world) return '';
  const x = Math.floor(world.x);
  const y = Math.floor(world.y);
  if (!inBounds(x, y, model.width, model.height)) return '';
  const i = index(x, y, model.width);
  const t = model.tiles;
  const id = t.buildingId[i];
  const building = id === -1 ? '—' : `${id} (${model.buildings[id]?.kind ?? '?'})`;
  return [
    `tile ${x}, ${y}`,
    `terrain ${enumName(Terrain, t.terrain[i])}`,
    `overlay ${enumName(Overlay, t.overlay[i])}`,
    `building ${building}`,
    `walkable ${t.walkable[i] ? 'yes' : 'no'}`,
  ].join('  ·  ');
}

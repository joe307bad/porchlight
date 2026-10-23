// Stage 6: blocks. Roads (at their full raster width), water, rock and the town
// radius cut the land into faces; each connected face of buildable tiles is a
// block. Because roads are cut at full width, blocks never overlap them.

import type { Block } from '../model/types';
import { components, ground, outline, tilesIn } from './parcels';
import type { Stage } from './stage';

export const blocks: Stage = {
  name: 'blocks',
  run(model) {
    const { width: w } = model;
    const { free } = ground(model);
    const tiles: number[] = [];
    for (let i = 0; i < free.length; i++) if (free[i]) tiles.push(i);
    const list: Block[] = [];
    for (const comp of components(tiles, w)) {
      if (comp.length < model.params.lotMinSize) continue;
      const [polygon, ...holes] = outline(comp, w);
      if (!polygon) continue;
      list.push({ id: list.length, polygon, holes });
    }
    return { ...model, blocks: list };
  },
  check(model) {
    const { width: w, height: h } = model;
    const { free } = ground(model);
    const owner = new Int32Array(w * h).fill(-1);
    for (const b of model.blocks) {
      for (const t of tilesIn(b.polygon, b.holes, w, h)) {
        if (!free[t]) return [`block ${b.id} covers road, water or rock at ${t % w},${Math.floor(t / w)}`];
        if (owner[t] >= 0) return [`blocks ${owner[t]} and ${b.id} overlap`];
        owner[t] = b.id;
      }
    }
    return [];
  },
};

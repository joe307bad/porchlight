// Kenney isometric sprites (CC0, www.kenney.nl) served from public/kenney:
// "Isometric Buildings" for the buildings (the free pack with the most building
// types: ~50 ground floors, 24 upper floors, 50+ roofs that stack into houses,
// shops and offices), terrain blocks from "Isometric Landscape", the fountain,
// lamp posts, benches and round trees from "Isometric City", fences, tilled
// soil, corn, hay and crates from "Isometric Miniature Farm", and conifers and
// more trees from "Isometric Roads". Cars are drawn in textures.ts.

import { Assets, Texture } from 'pixi.js';
import type { BuildingKind, Facing } from '../../model/types';

const BASE = `${import.meta.env.BASE_URL}kenney/`;
const pad = (n: number) => String(n).padStart(3, '0');
const building = (n: number) => `${BASE}buildings/buildingTiles_${pad(n)}.png`;

/**
 * Which side each ground floor's entrance (door, shopfront or awning) is on,
 * read off the sprites: S = lower-left face, E = lower-right face, W and N =
 * the two back faces (only the awning edge shows). Mirroring a sprite swaps
 * S with E and N with W.
 */
export const ENTRANCE: Record<number, Facing> = {
  2: 'S', 4: 'S', 10: 'S', 14: 'S', 18: 'S', 20: 'S', 22: 'S', 30: 'S', 34: 'S', 37: 'S', 101: 'S', 108: 'S', 109: 'S',
  113: 'S', 115: 'S', 117: 'S', 123: 'S', 125: 'S',
  1: 'E', 3: 'E', 9: 'E', 12: 'E', 17: 'E', 21: 'E', 26: 'E', 28: 'E', 29: 'E', 36: 'E', 41: 'E', 42: 'E', 93: 'E',
  99: 'E', 100: 'E', 107: 'E', 116: 'E', 124: 'E',
  11: 'W', 25: 'W', 27: 'W', 40: 'W',
  19: 'N', 33: 'N', 35: 'N', 46: 'N',
};

/** Ground floors (with their paved base), upper floors and roofs, grouped by facade colour. */
export const FAMILIES = {
  red: { homes: [30, 36], shops: [18, 26, 34, 41, 99, 113, 25, 33, 40, 46], uppers: [45, 49, 52, 54] },
  brown: { homes: [14, 21], shops: [14, 21], uppers: [32, 38, 43, 47] },
  yellow: { homes: [22, 29], shops: [22, 29], uppers: [39, 44, 48, 51] },
  beige: { homes: [37, 42, 117, 109], shops: [4, 12, 20, 28, 100, 108, 115, 101, 107, 11, 19, 27, 35], uppers: [50, 53, 55, 56] },
  glass: { homes: [3, 116, 2, 10], shops: [1, 2, 9, 10, 17, 93, 123, 124, 125], uppers: [0, 7, 8, 15, 16, 23, 24, 31] },
} as const;
export type Family = keyof typeof FAMILIES;
/** Ground floors with no entrance on any side (halls, workshops). */
export const PLAIN_GROUND = [85, 92, 106, 114, 122];
/**
 * Pitched roofs by ridge direction (measured from the sprites): 'y' roofs show
 * their gable end on the south (lower-left) face, so the ridge runs along the
 * world y axis; 'x' roofs show it on the east face. Mirroring swaps the two.
 */
export const ROOF_RIDGE: Record<number, 'x' | 'y'> = {
  58: 'y', 63: 'y', 70: 'y', 71: 'y', 72: 'y', 82: 'y', 83: 'y', 84: 'y', 96: 'y', 97: 'y', 98: 'y', 112: 'y',
  61: 'x', 65: 'x', 68: 'x', 74: 'x', 76: 'x', 77: 'x', 80: 'x', 88: 'x', 90: 'x', 91: 'x', 104: 'x', 105: 'x',
};
export const GABLE_ROOFS = [58, 61, 63, 65, 68, 70, 72, 74, 80, 82];
export const GREY_ROOFS = [88, 96, 97, 104, 105, 112];
export const ARCHED_ROOFS = [71, 76, 77, 83, 84, 90, 91, 98];
export const FLAT_ROOFS = [5, 6, 13, 79, 86, 87, 94, 95, 102, 103, 110, 111, 118, 119, 120, 121, 126, 127, 128];

/** Which families suit each kind of building. */
export const KIND_FAMILIES: Record<BuildingKind, Family[]> = {
  house: ['red', 'brown', 'yellow', 'beige', 'red', 'beige'],
  shop: ['red', 'beige', 'glass', 'red', 'beige'],
  workshop: ['brown', 'beige'],
  church: ['beige'],
  hall: ['red', 'beige'],
};

/**
 * Low fence sprites by the tile edge they stand on (the farm pack names them
 * by viewing direction, so its _N fence is on our south edge, and so on).
 */
const FENCE_FILE: Record<Facing, string> = { S: 'fenceLow_N', W: 'fenceLow_E', N: 'fenceLow_S', E: 'fenceLow_W' };
/** Farm sprites are 256px tiles; this brings them to our 130px tiles. */
export const FARM_SCALE = 130 / 256;
/** Their tile diamond's top vertex sits here in the 256x512 sprite. */
export const FARM_TOP = { x: 128, y: 374 };

export interface IsoAssets {
  building(n: number): Texture;
  /**
   * A ground-floor piece cut down to just its building box. Its paved base is
   * left to the paving block drawn in the ground layer, so buildings sit flush
   * with the tiles in front of them.
   */
  groundFloor(n: number): Texture;
  paving: Texture;
  grass: Texture;
  sand: Texture;
  dirt: Texture;
  water: Texture;
  fountain: Texture;
  lamp: Texture;
  tree: Texture;
  benches: Texture[];
  fence(side: Facing): Texture;
  farmland: Texture;
  crops: Texture[];
  hay: Texture;
  crates: Texture;
  /** Conifers and other trees from the roads pack (small sprites; scale them up). */
  foliage: Texture[];
}

let loaded: Promise<IsoAssets> | null = null;

export function loadIsoAssets(): Promise<IsoAssets> {
  loaded ??= (async () => {
    const ids = new Set<number>([
      ...Object.values(FAMILIES).flatMap((f) => [...f.homes, ...f.shops, ...f.uppers]),
      ...PLAIN_GROUND,
      ...GABLE_ROOFS,
      ...GREY_ROOFS,
      ...ARCHED_ROOFS,
      ...FLAT_ROOFS,
    ]);
    const other = {
      grass: `${BASE}landscape/landscapeTiles_067.png`,
      sand: `${BASE}landscape/landscapeTiles_059.png`,
      dirt: `${BASE}landscape/landscapeTiles_073.png`,
      water: `${BASE}landscape/landscapeTiles_066.png`,
      fountain: `${BASE}city/cityTiles_043.png`,
      paving: `${BASE}city/cityTiles_072.png`,
      lamp: `${BASE}details/cityDetails_005.png`,
      tree: `${BASE}details/cityDetails_010.png`,
      bench1: `${BASE}details/cityDetails_001.png`,
      bench2: `${BASE}details/cityDetails_002.png`,
      farmland: `${BASE}farm/dirtFarmland_N.png`,
      cornYoung: `${BASE}farm/cornYoung_N.png`,
      corn: `${BASE}farm/corn_N.png`,
      hay: `${BASE}farm/hayBales_N.png`,
      crates: `${BASE}farm/sacksCrate_N.png`,
    };
    const fenceUrl = (side: Facing) => `${BASE}farm/${FENCE_FILE[side]}.png`;
    const foliageUrls = ['coniferTall', 'coniferShort', 'treeAltTall', 'treeTall', 'treeShort', 'treeAltShort'].map((n) => `${BASE}foliage/${n}.png`);
    const urls = [
      ...[...ids].map(building),
      ...Object.values(other),
      ...(['N', 'E', 'S', 'W'] as Facing[]).map(fenceUrl),
      ...foliageUrls,
    ];
    const tex = (await Assets.load(urls)) as Record<string, Texture>;
    for (const t of Object.values(tex)) t.source.scaleMode = 'linear';
    const grounds = new Map<number, Texture>();
    const groundFloor = (n: number) => {
      let t = grounds.get(n);
      if (!t) {
        t = cutBase(tex[building(n)] ?? Texture.EMPTY);
        grounds.set(n, t);
      }
      return t;
    };
    return {
      building: (n: number) => tex[building(n)] ?? Texture.EMPTY,
      groundFloor,
      paving: tex[other.paving],
      grass: tex[other.grass],
      sand: tex[other.sand],
      dirt: tex[other.dirt],
      water: tex[other.water],
      fountain: tex[other.fountain],
      lamp: tex[other.lamp],
      tree: tex[other.tree],
      benches: [tex[other.bench1], tex[other.bench2]],
      fence: (side: Facing) => tex[fenceUrl(side)],
      farmland: tex[other.farmland],
      crops: [tex[other.cornYoung], tex[other.corn]],
      hay: tex[other.hay],
      crates: tex[other.crates],
      foliage: foliageUrls.map((u) => tex[u]),
    };
  })();
  return loaded;
}

/**
 * Keep only a ground floor's building box: erase its paved base (the ground
 * layer draws paving instead) by clearing everything outside the box's
 * columns and below its bottom edges. The box is 99px wide, centred, with its
 * bottom corners 67.5px (sides) and 42.75px (front) above the sprite's bottom.
 */
function cutBase(tex: Texture): Texture {
  const src = tex.source.resource as CanvasImageSource | undefined;
  if (!src || !tex.width) return tex;
  const w = tex.width;
  const h = tex.height;
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d')!;
  const left = w / 2 - 49.5;
  const right = w / 2 + 49.5;
  ctx.beginPath();
  ctx.moveTo(left, 0);
  ctx.lineTo(right, 0);
  ctx.lineTo(right, h - 67.5);
  ctx.lineTo(w / 2, h - 42.75);
  ctx.lineTo(left, h - 67.5);
  ctx.closePath();
  ctx.clip();
  ctx.drawImage(src, 0, 0);
  return Texture.from(canvas);
}

// Per-model render caches: rasters and contour paths are expensive to rebuild
// every frame, so each is built once per TownModel and reused while panning.

import type { TownModel } from '../../model/types';
import { STYLE, TILE_BUILDING, TILE_OVERLAY, TILE_TERRAIN } from './style';

/** Pixels per tile for the smooth terrain and water rasters. */
const DETAIL = 4;
const MINOR_PER_STEP = 3;

interface Cache {
  terrain?: HTMLCanvasElement;
  water?: HTMLCanvasElement;
  contours?: { major: Path2D; minor: Path2D; shore: Path2D };
  tiles?: HTMLCanvasElement;
  debug: Map<string, HTMLCanvasElement>;
}

const caches = new WeakMap<TownModel, Cache>();

function cacheFor(model: TownModel): Cache {
  let c = caches.get(model);
  if (!c) {
    c = { debug: new Map() };
    caches.set(model, c);
  }
  return c;
}

function makeCanvas(w: number, h: number): { canvas: HTMLCanvasElement; ctx: CanvasRenderingContext2D; img: ImageData } {
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d')!;
  return { canvas, ctx, img: ctx.createImageData(w, h) };
}

/** Bilinear height at world point (x, y); tile centres sit at +0.5. */
function sampleHeight(model: TownModel, x: number, y: number): number {
  const { width: w, height: h, heightmap: hm } = model;
  const fx = Math.min(w - 1, Math.max(0, x - 0.5));
  const fy = Math.min(h - 1, Math.max(0, y - 0.5));
  const x0 = Math.floor(fx);
  const y0 = Math.floor(fy);
  const x1 = Math.min(w - 1, x0 + 1);
  const y1 = Math.min(h - 1, y0 + 1);
  const tx = fx - x0;
  const ty = fy - y0;
  const a = hm[y0 * w + x0] * (1 - tx) + hm[y0 * w + x1] * tx;
  const b = hm[y1 * w + x0] * (1 - tx) + hm[y1 * w + x1] * tx;
  return a * (1 - ty) + b * ty;
}

/** Parchment with soft hillshading lit from the north-west. */
export function terrainImage(model: TownModel): HTMLCanvasElement {
  const cache = cacheFor(model);
  if (cache.terrain) return cache.terrain;
  const W = model.width * DETAIL;
  const H = model.height * DETAIL;
  const { canvas, ctx, img } = makeCanvas(W, H);
  const [lr, lg, lb] = STYLE.parchment;
  const [dr, dg, db] = STYLE.parchmentShadow;
  for (let py = 0; py < H; py++) {
    for (let px = 0; px < W; px++) {
      const x = (px + 0.5) / DETAIL;
      const y = (py + 0.5) / DETAIL;
      const gx = sampleHeight(model, x + 0.5, y) - sampleHeight(model, x - 0.5, y);
      const gy = sampleHeight(model, x, y + 0.5) - sampleHeight(model, x, y - 0.5);
      const light = Math.max(0, Math.min(1, 0.5 + (gx + gy) * 14));
      const height = sampleHeight(model, x, y);
      const t = Math.max(0, Math.min(1, light * 0.45 + (1 - height) * 0.12));
      const o = (py * W + px) * 4;
      img.data[o] = lr + (dr - lr) * t;
      img.data[o + 1] = lg + (dg - lg) * t;
      img.data[o + 2] = lb + (db - lb) * t;
      img.data[o + 3] = 255;
    }
  }
  ctx.putImageData(img, 0, 0);
  cache.terrain = canvas;
  return canvas;
}

/** Smooth lake shapes: the bilinear heightmap thresholded at the water level. */
export function waterImage(model: TownModel): HTMLCanvasElement {
  const cache = cacheFor(model);
  if (cache.water) return cache.water;
  const W = model.width * DETAIL;
  const H = model.height * DETAIL;
  const { canvas, ctx, img } = makeCanvas(W, H);
  for (let py = 0; py < H; py++) {
    for (let px = 0; px < W; px++) {
      const v = sampleHeight(model, (px + 0.5) / DETAIL, (py + 0.5) / DETAIL);
      if (v >= model.waterLevel) continue;
      const deep = Math.min(1, (model.waterLevel - v) / 0.06);
      const o = (py * W + px) * 4;
      for (let k = 0; k < 3; k++) img.data[o + k] = STYLE.water[k] + (STYLE.waterDeep[k] - STYLE.water[k]) * deep;
      img.data[o + 3] = 255;
    }
  }
  ctx.putImageData(img, 0, 0);
  cache.water = canvas;
  return canvas;
}

/**
 * Contour lines by marching squares over tile centres. Major lines sit on the
 * contour steps (params.contourSteps bands over the dry height range), minor lines
 * subdivide each step; the water level is drawn as the shoreline.
 */
export function contours(model: TownModel): { major: Path2D; minor: Path2D; shore: Path2D } {
  const cache = cacheFor(model);
  if (cache.contours) return cache.contours;
  const { width: w, height: h, heightmap: hm, waterLevel } = model;
  let top = waterLevel;
  for (const v of hm) top = Math.max(top, v);
  const steps = model.params.contourSteps;
  const span = (top - waterLevel) / steps;
  const major = new Path2D();
  const minor = new Path2D();
  const shore = new Path2D();
  const levels: { v: number; path: Path2D }[] = [{ v: waterLevel, path: shore }];
  for (let s = 0; s < steps; s++) {
    for (let m = 0; m < MINOR_PER_STEP; m++) {
      if (s === 0 && m === 0) continue;
      levels.push({ v: waterLevel + span * (s + m / MINOR_PER_STEP), path: m === 0 ? major : minor });
    }
  }
  for (const { v, path } of levels) {
    for (let y = 0; y + 1 < h; y++) {
      for (let x = 0; x + 1 < w; x++) {
        const a = hm[y * w + x];
        const b = hm[y * w + x + 1];
        const c = hm[(y + 1) * w + x + 1];
        const d = hm[(y + 1) * w + x];
        const code = (a >= v ? 8 : 0) | (b >= v ? 4 : 0) | (c >= v ? 2 : 0) | (d >= v ? 1 : 0);
        if (code === 0 || code === 15) continue;
        const cx = x + 0.5;
        const cy = y + 0.5;
        const t = (p: number, q: number) => (v - p) / (q - p);
        const top_ = { x: cx + t(a, b), y: cy };
        const right = { x: cx + 1, y: cy + t(b, c) };
        const bottom = { x: cx + t(d, c), y: cy + 1 };
        const left = { x: cx, y: cy + t(a, d) };
        const seg = (p: { x: number; y: number }, q: { x: number; y: number }) => {
          path.moveTo(p.x, p.y);
          path.lineTo(q.x, q.y);
        };
        switch (code) {
          case 1: case 14: seg(left, bottom); break;
          case 2: case 13: seg(bottom, right); break;
          case 3: case 12: seg(left, right); break;
          case 4: case 11: seg(top_, right); break;
          case 5: seg(left, top_); seg(bottom, right); break;
          case 6: case 9: seg(top_, bottom); break;
          case 7: case 8: seg(left, top_); break;
          case 10: seg(left, bottom); seg(top_, right); break;
        }
      }
    }
  }
  cache.contours = { major, minor, shore };
  return cache.contours;
}

/** The tile grid as one pixel per tile. */
export function tileImage(model: TownModel): HTMLCanvasElement {
  const cache = cacheFor(model);
  if (cache.tiles) return cache.tiles;
  const t = model.tiles;
  const { canvas, ctx, img } = makeCanvas(t.width, t.height);
  for (let i = 0; i < t.terrain.length; i++) {
    let rgb = TILE_TERRAIN[t.terrain[i]] ?? [255, 0, 255];
    const over = TILE_OVERLAY[t.overlay[i]];
    if (over) rgb = over;
    if (t.buildingId[i] >= 0) rgb = TILE_BUILDING;
    img.data[i * 4] = rgb[0];
    img.data[i * 4 + 1] = rgb[1];
    img.data[i * 4 + 2] = rgb[2];
    img.data[i * 4 + 3] = 255;
  }
  ctx.putImageData(img, 0, 0);
  cache.tiles = canvas;
  return canvas;
}

/** A per-tile debug raster, coloured by `color(i)` returning RGBA or null. */
export function debugImage(
  model: TownModel,
  key: string,
  color: (i: number) => readonly [number, number, number, number] | null,
): HTMLCanvasElement {
  const cache = cacheFor(model);
  const hit = cache.debug.get(key);
  if (hit) return hit;
  const { canvas, ctx, img } = makeCanvas(model.width, model.height);
  for (let i = 0; i < model.width * model.height; i++) {
    const c = color(i);
    if (!c) continue;
    img.data.set(c, i * 4);
  }
  ctx.putImageData(img, 0, 0);
  cache.debug.set(key, canvas);
  return canvas;
}

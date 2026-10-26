// Small procedural textures: the soft radial glow used for lamp halos and the
// pools of light they throw on the pavement at night, and the street sign atlas.

import { CanvasTexture, SRGBColorSpace } from 'three';

let glow: CanvasTexture | null = null;

/** A white radial falloff on transparent black, for additive glows. */
export function glowTexture(): CanvasTexture {
  if (glow) return glow;
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const ctx = c.getContext('2d')!;
  const g = ctx.createRadialGradient(32, 32, 0, 32, 32, 32);
  g.addColorStop(0, 'rgba(255,255,255,1)');
  g.addColorStop(0.35, 'rgba(255,255,255,0.45)');
  g.addColorStop(1, 'rgba(255,255,255,0)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, 64, 64);
  glow = new CanvasTexture(c);
  glow.colorSpace = SRGBColorSpace;
  return glow;
}

/** One street name cell of the sign atlas, in texture coordinates. */
export interface SignCell {
  u0: number;
  u1: number;
  v0: number;
  v1: number;
}

const CELL_W = 512;
const CELL_H = 100;
const ATLAS_COLS = 2;
const SIGN_GREEN = '#24603f';

/**
 * Every street name lettered on a green street-sign blade (white border,
 * white capitals), one cell per name in a two-column atlas.
 */
export function signAtlas(names: readonly string[]): { texture: CanvasTexture; cell(i: number): SignCell } {
  const rows = Math.max(1, Math.ceil(names.length / ATLAS_COLS));
  const c = document.createElement('canvas');
  c.width = CELL_W * ATLAS_COLS;
  c.height = CELL_H * rows;
  const ctx = c.getContext('2d')!;
  names.forEach((name, i) => {
    const x = (i % ATLAS_COLS) * CELL_W;
    const y = Math.floor(i / ATLAS_COLS) * CELL_H;
    ctx.fillStyle = SIGN_GREEN;
    ctx.fillRect(x, y, CELL_W, CELL_H);
    ctx.strokeStyle = '#f4f1ea';
    ctx.lineWidth = 6;
    ctx.strokeRect(x + 9, y + 9, CELL_W - 18, CELL_H - 18);
    const text = name.toUpperCase();
    let size = 54;
    ctx.font = `700 ${size}px "Helvetica Neue", Arial, sans-serif`;
    const room = CELL_W - 48;
    const wide = ctx.measureText(text).width;
    if (wide > room) size = Math.floor((size * room) / wide);
    ctx.font = `700 ${size}px "Helvetica Neue", Arial, sans-serif`;
    ctx.fillStyle = '#f4f1ea';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(text, x + CELL_W / 2, y + CELL_H / 2 + 3);
  });
  const texture = new CanvasTexture(c);
  texture.colorSpace = SRGBColorSpace;
  texture.anisotropy = 4;
  const inset = 0.5;
  return {
    texture,
    cell: (i) => {
      const col = i % ATLAS_COLS;
      const row = Math.floor(i / ATLAS_COLS);
      return {
        u0: (col * CELL_W + inset) / c.width,
        u1: ((col + 1) * CELL_W - inset) / c.width,
        // The canvas is flipped onto the texture: its top row is v = 1.
        v0: 1 - ((row + 1) * CELL_H - inset) / c.height,
        v1: 1 - (row * CELL_H + inset) / c.height,
      };
    },
  };
}

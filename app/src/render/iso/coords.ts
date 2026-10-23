// Iso projection shared by every part of the explorable view. World units are
// tiles (tile (x, y) spans x..x+1, y..y+1); screen units are texture pixels at
// zoom 1. Kenney's isometric tiles have a 130x64 top diamond and blocks 32px
// thick, so one elevation step is one block.

export const HALF_W = 65;
export const HALF_H = 32;
export const STEP = 32;

/** Screen position of a world point at elevation z (in steps). */
export function toScreen(x: number, y: number, z = 0): { x: number; y: number } {
  return { x: (x - y) * HALF_W, y: (x + y) * HALF_H - z * STEP };
}

/** World point on the ground plane (z = 0) under a screen position. */
export function toWorld(sx: number, sy: number): { x: number; y: number } {
  return { x: (sx / HALF_W + sy / HALF_H) / 2, y: (sy / HALF_H - sx / HALF_W) / 2 };
}

/** Painter's-order key: things further down-screen (larger x + y) draw later. */
export function depthOf(x: number, y: number, order = 0): number {
  return Math.round((x + y) * 100) * 16 + order;
}

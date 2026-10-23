// Row-major grid helpers shared by fields (heightmap) and the tile grid.

export const index = (x: number, y: number, width: number): number => y * width + x;

export const inBounds = (x: number, y: number, width: number, height: number): boolean =>
  x >= 0 && y >= 0 && x < width && y < height;

/** N, E, S, W — the order autotile bitmasks use (bit 0 = N). */
export const DIRS4: readonly (readonly [number, number])[] = [
  [0, -1], [1, 0], [0, 1], [-1, 0],
];

export const DIRS8: readonly (readonly [number, number])[] = [
  [0, -1], [1, -1], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1],
];

/**
 * Every cell a segment passes through (Amanatides & Woo grid traversal),
 * as [x, y] pairs in order from a to b.
 */
export function cellsOnSegment(ax: number, ay: number, bx: number, by: number): [number, number][] {
  let x = Math.floor(ax);
  let y = Math.floor(ay);
  const ex = Math.floor(bx);
  const ey = Math.floor(by);
  const dx = bx - ax;
  const dy = by - ay;
  const stepX = Math.sign(dx);
  const stepY = Math.sign(dy);
  const tDeltaX = stepX ? Math.abs(1 / dx) : Infinity;
  const tDeltaY = stepY ? Math.abs(1 / dy) : Infinity;
  let tMaxX = stepX > 0 ? (x + 1 - ax) * tDeltaX : stepX < 0 ? (ax - x) * tDeltaX : Infinity;
  let tMaxY = stepY > 0 ? (y + 1 - ay) * tDeltaY : stepY < 0 ? (ay - y) * tDeltaY : Infinity;
  const out: [number, number][] = [[x, y]];
  for (let guard = 0; (x !== ex || y !== ey) && guard < 100000; guard++) {
    if (tMaxX < tMaxY) {
      x += stepX;
      tMaxX += tDeltaX;
    } else {
      y += stepY;
      tMaxY += tDeltaY;
    }
    out.push([x, y]);
  }
  return out;
}

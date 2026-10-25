// Solid rectangles the player can't walk through (building walls, furniture),
// bucketed by tile so a step only tests what's nearby. Pure, unit-tested.

import type { Rect } from './interiors';

export class Solids {
  private readonly buckets = new Map<number, Rect[]>();

  constructor(
    rects: readonly Rect[],
    private readonly width: number,
  ) {
    for (const r of rects) this.add(r);
  }

  add(r: Rect): void {
    for (let y = Math.floor(r.y0); y <= Math.floor(r.y1); y++) {
      for (let x = Math.floor(r.x0); x <= Math.floor(r.x1); x++) {
        const k = y * this.width + x;
        const list = this.buckets.get(k);
        if (list) list.push(r);
        else this.buckets.set(k, [r]);
      }
    }
  }

  /** True if a circle of radius r at (x, y) overlaps any solid. */
  hits(x: number, y: number, r: number): boolean {
    for (let ty = Math.floor(y - r); ty <= Math.floor(y + r); ty++) {
      for (let tx = Math.floor(x - r); tx <= Math.floor(x + r); tx++) {
        const list = this.buckets.get(ty * this.width + tx);
        if (!list) continue;
        for (const b of list) {
          const dx = x - Math.max(b.x0, Math.min(x, b.x1));
          const dy = y - Math.max(b.y0, Math.min(y, b.y1));
          if (dx * dx + dy * dy < r * r) return true;
        }
      }
    }
    return false;
  }
}

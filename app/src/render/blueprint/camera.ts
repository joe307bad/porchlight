// Screen <-> world transform for the blueprint canvas. `scale` is CSS pixels per tile;
// (ox, oy) is where world (0, 0) lands on screen, in CSS pixels.

import type { Vec2 } from '../../model/types';

export class Camera {
  ox = 0;
  oy = 0;
  scale = 4;

  static readonly MIN_SCALE = 0.5;
  static readonly MAX_SCALE = 96;

  worldToScreen(p: Vec2): Vec2 {
    return { x: p.x * this.scale + this.ox, y: p.y * this.scale + this.oy };
  }

  screenToWorld(p: Vec2): Vec2 {
    return { x: (p.x - this.ox) / this.scale, y: (p.y - this.oy) / this.scale };
  }

  /** Fit a width x height world into the viewport with some margin. */
  fit(width: number, height: number, viewW: number, viewH: number, margin = 32): void {
    this.scale = Math.max(
      Camera.MIN_SCALE,
      Math.min((viewW - margin * 2) / width, (viewH - margin * 2) / height),
    );
    this.ox = (viewW - width * this.scale) / 2;
    this.oy = (viewH - height * this.scale) / 2;
  }

  pan(dx: number, dy: number): void {
    this.ox += dx;
    this.oy += dy;
  }

  /** Zoom by `factor`, keeping the world point under `screen` fixed. */
  zoomAt(screen: Vec2, factor: number): void {
    const before = this.screenToWorld(screen);
    this.scale = Math.min(Camera.MAX_SCALE, Math.max(Camera.MIN_SCALE, this.scale * factor));
    this.ox = screen.x - before.x * this.scale;
    this.oy = screen.y - before.y * this.scale;
  }
}

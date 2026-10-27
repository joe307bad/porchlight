// A small top-down map of the whole town, in the bottom-left corner of the 3D view,
// drawn the same way up as the blueprint. It marks the player (and auto-walk's
// destination), other players in blue, and outlines what the camera can see;
// clicking it moves the player there.

import type { TownModel, Vec2 } from '../../model/types';
import { TILE_BUILDING, TILE_OVERLAY, TILE_TERRAIN } from '../blueprint/style';

/** Longest side of the minimap, in CSS pixels. */
const SIZE = 264;

export class Minimap {
  readonly canvas = document.createElement('canvas');
  private readonly ctx: CanvasRenderingContext2D;
  private base: HTMLCanvasElement | null = null;
  private model: TownModel | null = null;
  private scale = 1;
  /** Other players in the room (map tiles). */
  private others: Vec2[] = [];

  constructor(host: HTMLElement, onPick: (at: Vec2) => void) {
    this.canvas.className = 'minimap';
    this.canvas.title = 'Click to jump there';
    this.ctx = this.canvas.getContext('2d')!;
    host.appendChild(this.canvas);
    this.canvas.addEventListener('pointerdown', (e) => {
      if (!this.model) return;
      const r = this.canvas.getBoundingClientRect();
      // Measured from the drawn size, which CSS may shrink to fit a narrow sidebar.
      const { width: w, height: h } = this.model;
      onPick({ x: ((e.clientX - r.left) / r.width) * w, y: ((e.clientY - r.top) / r.height) * h });
      e.stopPropagation();
    });
  }

  setTown(model: TownModel): void {
    this.model = model;
    const { width: w, height: h, tiles } = model;
    const base = document.createElement('canvas');
    base.width = w;
    base.height = h;
    const bctx = base.getContext('2d')!;
    const img = bctx.createImageData(w, h);
    for (let i = 0; i < w * h; i++) {
      let rgb = TILE_TERRAIN[tiles.terrain[i]] ?? [0, 0, 0];
      const over = TILE_OVERLAY[tiles.overlay[i]];
      if (over) rgb = over;
      if (tiles.buildingId[i] >= 0) rgb = TILE_BUILDING;
      img.data.set([rgb[0], rgb[1], rgb[2], 255], i * 4);
    }
    bctx.putImageData(img, 0, 0);
    this.base = base;

    this.scale = SIZE / Math.max(w, h);
    const dpr = window.devicePixelRatio || 1;
    this.canvas.style.width = `${w * this.scale}px`;
    this.canvas.style.height = `${h * this.scale}px`;
    this.canvas.width = Math.round(w * this.scale * dpr);
    this.canvas.height = Math.round(h * this.scale * dpr);
  }

  /** Where the other players are, drawn on every redraw. */
  setOthers(others: Vec2[]): void {
    this.others = others;
  }

  /** Redraw with the player at `player`, the camera's view as a world-space quad, and auto-walk's goal. */
  draw(player: Vec2 | null, view: Vec2[], goal: Vec2 | null = null, route: Vec2[] = []): void {
    if (!this.base) return;
    const { ctx } = this;
    const k = this.canvas.width / this.base.width;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(this.base, 0, 0, this.canvas.width, this.canvas.height);
    ctx.setTransform(k, 0, 0, k, 0, 0);

    ctx.beginPath();
    view.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
    ctx.closePath();
    ctx.fillStyle = 'rgba(255, 255, 255, 0.18)';
    ctx.fill();
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.9)';
    ctx.lineWidth = 1.5 / k;
    ctx.stroke();

    if (route.length > 1) {
      ctx.beginPath();
      route.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
      ctx.strokeStyle = '#ff6a00';
      ctx.lineWidth = 1.5 / k;
      ctx.stroke();
    }

    if (goal) {
      // A small flag where auto-walk is heading.
      ctx.strokeStyle = '#fff8e8';
      ctx.lineWidth = 1.5 / k;
      ctx.beginPath();
      ctx.moveTo(goal.x, goal.y);
      ctx.lineTo(goal.x, goal.y - 9 / k);
      ctx.stroke();
      ctx.fillStyle = '#ff6a00';
      ctx.beginPath();
      ctx.moveTo(goal.x, goal.y - 9 / k);
      ctx.lineTo(goal.x + 6 / k, goal.y - 7 / k);
      ctx.lineTo(goal.x, goal.y - 5 / k);
      ctx.closePath();
      ctx.fill();
    }

    for (const o of this.others) {
      ctx.beginPath();
      ctx.arc(o.x, o.y, 3 / k, 0, Math.PI * 2);
      ctx.fillStyle = '#3b82c4';
      ctx.fill();
      ctx.lineWidth = 1.5 / k;
      ctx.strokeStyle = '#fff8e8';
      ctx.stroke();
    }

    if (player) {
      const r = 4 / k;
      ctx.beginPath();
      ctx.arc(player.x, player.y, r, 0, Math.PI * 2);
      ctx.fillStyle = '#c2412d';
      ctx.fill();
      ctx.lineWidth = 2 / k;
      ctx.strokeStyle = '#fff8e8';
      ctx.stroke();
    }
  }
}

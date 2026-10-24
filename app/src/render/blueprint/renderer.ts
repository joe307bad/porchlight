import type { TownModel, Vec2 } from '../../model/types';
import { Camera } from './camera';
import { LAYERS, type DrawView } from './layers';
import { STYLE } from './style';

/** Draw the visible layers of a model into `ctx` (sized viewW x viewH CSS px at `dpr`). */
export function drawScene(
  ctx: CanvasRenderingContext2D,
  model: TownModel,
  camera: Camera,
  viewW: number,
  viewH: number,
  dpr: number,
  visible: ReadonlySet<string>,
  background = STYLE.background,
): void {
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.fillStyle = background;
  ctx.fillRect(0, 0, viewW * dpr, viewH * dpr);

  const s = camera.scale * dpr;
  ctx.setTransform(s, 0, 0, s, camera.ox * dpr, camera.oy * dpr);

  const tl = camera.screenToWorld({ x: 0, y: 0 });
  const br = camera.screenToWorld({ x: viewW, y: viewH });
  const view: DrawView = {
    scale: camera.scale,
    px: 1 / camera.scale,
    x0: Math.max(0, Math.floor(tl.x)),
    y0: Math.max(0, Math.floor(tl.y)),
    x1: Math.min(model.width, Math.ceil(br.x)),
    y1: Math.min(model.height, Math.ceil(br.y)),
  };

  for (const layer of LAYERS) {
    if (!visible.has(layer.id)) continue;
    ctx.save();
    layer.draw(ctx, model, view);
    ctx.restore();
  }
}

export class BlueprintRenderer {
  readonly camera = new Camera();
  /** Where the explore-mode character is, drawn as a "you are here" pin. */
  marker: Vec2 | null = null;
  /** Auto-walk's planned route and destination, drawn under the pin. */
  route: Vec2[] = [];
  goal: Vec2 | null = null;
  readonly visible = new Set(LAYERS.filter((l) => l.defaultOn).map((l) => l.id));
  private readonly ctx: CanvasRenderingContext2D;

  constructor(readonly canvas: HTMLCanvasElement) {
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('Canvas 2D is not available');
    this.ctx = ctx;
  }

  /** Match the backing store to the element size and device pixel ratio. */
  resize(): void {
    const dpr = window.devicePixelRatio || 1;
    const { clientWidth: w, clientHeight: h } = this.canvas;
    this.canvas.width = Math.round(w * dpr);
    this.canvas.height = Math.round(h * dpr);
  }

  fit(model: TownModel): void {
    this.camera.fit(model.width, model.height, this.canvas.clientWidth, this.canvas.clientHeight);
  }

  render(model: TownModel): void {
    const dpr = window.devicePixelRatio || 1;
    drawScene(this.ctx, model, this.camera, this.canvas.clientWidth, this.canvas.clientHeight, dpr, this.visible);
    if (this.route.length > 1) this.drawRoute(dpr);
    if (this.marker) this.drawMarker(this.marker, dpr);
  }

  /** True if a world point is on screen, at least `margin` (0..0.5 of the view) in from the edges. */
  isInView(at: Vec2, margin: number): boolean {
    const p = this.camera.worldToScreen(at);
    const w = this.canvas.clientWidth;
    const h = this.canvas.clientHeight;
    return p.x > w * margin && p.x < w * (1 - margin) && p.y > h * margin && p.y < h * (1 - margin);
  }

  /** The route as a dashed orange line, with a flag at the destination. */
  private drawRoute(dpr: number): void {
    const { ctx } = this;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.beginPath();
    this.route.forEach((w, i) => {
      const p = this.camera.worldToScreen(w);
      if (i) ctx.lineTo(p.x, p.y);
      else ctx.moveTo(p.x, p.y);
    });
    ctx.strokeStyle = '#ff6a00';
    ctx.lineWidth = 3;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.setLineDash([7, 6]);
    ctx.stroke();
    ctx.setLineDash([]);
    if (this.goal) {
      const g = this.camera.worldToScreen(this.goal);
      ctx.strokeStyle = '#2e241d';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(g.x, g.y);
      ctx.lineTo(g.x, g.y - 22);
      ctx.stroke();
      ctx.fillStyle = '#ff6a00';
      ctx.beginPath();
      ctx.moveTo(g.x, g.y - 22);
      ctx.lineTo(g.x + 14, g.y - 17);
      ctx.lineTo(g.x, g.y - 12);
      ctx.closePath();
      ctx.fill();
    }
  }

  /** Pan (keeping the zoom, but at least a readable one) so a world point sits mid-screen. */
  centerOn(at: Vec2): void {
    const minScale = 6;
    if (this.camera.scale < minScale) this.camera.scale = minScale;
    this.camera.ox = this.canvas.clientWidth / 2 - at.x * this.camera.scale;
    this.camera.oy = this.canvas.clientHeight / 2 - at.y * this.camera.scale;
  }

  /** A map pin in screen pixels, so it stays the same size at any zoom. */
  private drawMarker(at: Vec2, dpr: number): void {
    const { ctx } = this;
    const p = this.camera.worldToScreen(at);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    // Soft halo on the spot itself.
    ctx.beginPath();
    ctx.arc(p.x, p.y, 14, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(194, 65, 45, 0.22)';
    ctx.fill();
    // The pin: a teardrop with its point on the spot.
    const r = 9;
    const cy = p.y - 22;
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
    ctx.bezierCurveTo(p.x - 4, cy + r, p.x - r, cy + 4, p.x - r, cy);
    ctx.arc(p.x, cy, r, Math.PI, 0);
    ctx.bezierCurveTo(p.x + r, cy + 4, p.x + 4, cy + r, p.x, p.y);
    ctx.fillStyle = STYLE.spawn;
    ctx.fill();
    ctx.lineWidth = 2;
    ctx.strokeStyle = STYLE.spawnRing;
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(p.x, cy, 3.5, 0, Math.PI * 2);
    ctx.fillStyle = STYLE.spawnRing;
    ctx.fill();
    // Label.
    ctx.font = '600 12px ui-sans-serif, system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'bottom';
    const label = 'You are here';
    const w = ctx.measureText(label).width + 12;
    ctx.fillStyle = 'rgba(28, 27, 24, 0.85)';
    ctx.beginPath();
    ctx.roundRect(p.x - w / 2, cy - r - 24, w, 18, 4);
    ctx.fill();
    ctx.fillStyle = '#fff8e8';
    ctx.fillText(label, p.x, cy - r - 9);
  }
}

// The edit screen's map: one town, drawn from the draft worker's answer (its roads at their width with
// their lanes, medians, turn pockets, roundabouts, slips and controls; its blocks by zone and its buildings
// by what they're for), with the town as it is now ghosted underneath. Handles for every junction and bend
// when close enough in, ghost handles to add a bend, and the bullseye for the town's centre at any zoom.
// A drag bends the roads round the handle every frame (skeleton.ts's rubber band) and snaps; letting go,
// or pausing, hands the edit to the screen, which turns it into a skeleton and asks for a re-plan.
// Pan by dragging empty ground, zoom with the wheel or a pinch, double-click to zoom in.

import { closestOn, decodeWater, dist, dragNode, dragWarnings, pullBend, snapNode, withBend, type Snap } from './skeleton.ts';
import type { BuildingKind, Pt, Town, TownRoad } from './types.ts';

export type Tool = 'select' | 'draw' | 'delete' | 'split';

export type Selection = { kind: 'road'; id: number } | { kind: 'junction'; node: number } | null;

export type Edit =
  | { type: 'node'; node: number; to: Pt; merge?: number; join?: number }
  | { type: 'bend'; road: number; index: number; to: Pt }
  | { type: 'newBend'; road: number; seg: number; to: Pt }
  | { type: 'center'; to: Pt }
  | { type: 'draw'; pts: Pt[] }
  | { type: 'delete'; road: number }
  | { type: 'split'; road: number; at: Pt };

export interface EditLayers {
  blocks: boolean;
  buildings: boolean;
  heat: boolean;
  ghost: boolean;
  lanes: boolean;
  controls: boolean;
}

export const DEFAULT_EDIT_LAYERS: EditLayers = { blocks: true, buildings: true, heat: false, ghost: true, lanes: true, controls: true };

/** What each building's for, as drawn: houses quiet, everything people go to bright. */
export const BUILDING_COLORS: Record<BuildingKind, string> = {
  house: '#8d7a68',
  shop: '#f2a541',
  workshop: '#7f8fa6',
  church: '#c9b8e8',
  hall: '#9fd8cb',
  school: '#5fb3f2',
  supermarket: '#ff8c5a',
  petrol: '#e05a5a',
  fire: '#ff4d4d',
  clinic: '#7be08a',
};

const TRANSECT_COLORS: Record<number, string> = { 3: '#1d2a22', 4: '#232a2a', 5: '#2b2926', 6: '#33282a' };
const LAND = '#17201a';
const MIN_SCALE = 1, MAX_SCALE = 80;
/** Handles show once a junction is this big on screen (px). */
const HANDLE_PX = 12;
/** The drag pauses this long (ms) before the screen's asked to re-plan. */
const PAUSE_MS = 150;

type Grab =
  | { kind: 'pan'; last: { x: number; y: number } }
  | { kind: 'node'; node: number; at: Pt; snap: Snap | null }
  | { kind: 'bend'; road: number; index: number; at: Pt }
  | { kind: 'newBend'; road: number; seg: number; at: Pt }
  | { kind: 'center'; at: Pt };

export class EditRenderer {
  town: Town | null = null;
  live: Town | null = null;
  layers: EditLayers = DEFAULT_EDIT_LAYERS;
  selection: Selection = null;
  tool: Tool = 'select';
  /** A re-plan's on its way: what's drawn fades. */
  pending = false;
  /** Where the problems are (drawn as markers), and the houses the draft loses. */
  marks: { at: Pt; level: 'error' | 'warning' }[] = [];
  lost: Pt[] = [];
  /** The centre as the skeleton has it (shown before the answer catches up). */
  center: Pt | null = null;
  /** Classes the draw tool lays (for its colour). */
  drawClass: 'arterial' | 'collector' | 'local' = 'local';

  onSelect: (s: Selection) => void = () => {};
  /** Let go: the edit to make, and the town it was made against (whose ids it names). */
  onEdit: (e: Edit, town: Town) => void = () => {};
  /** Paused mid-drag: the edit as it stands, for a re-plan. */
  onPause: (e: Edit, town: Town) => void = () => {};
  onWarnings: (w: string[]) => void = () => {};

  private readonly canvas: HTMLCanvasElement;
  private readonly ctx: CanvasRenderingContext2D;
  private readonly resize: ResizeObserver;
  private cam = { cx: 80, cy: 80, scale: 4 };
  private width = 0;
  private height = 0;
  private dpr = 1;
  private raf = 0;
  private dirty = true;
  private grab: Grab | null = null;
  private pointers = new Map<number, { x: number; y: number }>();
  private pinch: { d: number; scale: number } | null = null;
  private downAt: { x: number; y: number } | null = null;
  private moved = false;
  private pauseTimer: ReturnType<typeof setTimeout> | null = null;
  /** The roads as bent by the drag in hand. */
  private preview = new Map<number, Pt[]>();
  private warnings: string[] = [];
  private draft: Pt[] = [];
  private cursor: Pt | null = null;
  private waterImage: HTMLCanvasElement | null = null;
  private water: Uint8Array = new Uint8Array(0);
  private fitted = false;
  /** The town a drag began on: its roads are drawn (bent) and its ids edited until the answer to the drag comes. */
  private frozen: Town | null = null;

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: false })!;
    this.resize = new ResizeObserver(() => this.fitCanvas());
    this.resize.observe(canvas);
    this.fitCanvas();
    canvas.addEventListener('wheel', this.wheel, { passive: false });
    canvas.addEventListener('pointerdown', this.down);
    canvas.addEventListener('pointermove', this.move);
    canvas.addEventListener('pointerup', this.up);
    canvas.addEventListener('pointercancel', this.up);
    canvas.addEventListener('dblclick', this.dbl);
    canvas.addEventListener('contextmenu', this.context);
    this.raf = requestAnimationFrame(this.frame);
  }

  dispose(): void {
    cancelAnimationFrame(this.raf);
    this.resize.disconnect();
    const c = this.canvas;
    c.removeEventListener('wheel', this.wheel);
    c.removeEventListener('pointerdown', this.down);
    c.removeEventListener('pointermove', this.move);
    c.removeEventListener('pointerup', this.up);
    c.removeEventListener('pointercancel', this.up);
    c.removeEventListener('dblclick', this.dbl);
    c.removeEventListener('contextmenu', this.context);
  }

  setTown(town: Town | null): void {
    const sameWater = this.town && town && this.town.water === town.water;
    this.town = town;
    if (town && !sameWater) {
      this.water = decodeWater(town);
      this.waterImage = this.makeWater(town);
    }
    if (town && !this.fitted) {
      this.fitted = true;
      this.fit();
    }
    // A fresh answer after letting go: the drag's bending is in it now.
    if (!this.grab) {
      this.preview.clear();
      this.frozen = null;
    }
    this.redraw();
  }

  redraw(): void {
    this.dirty = true;
  }

  /** The whole town in view. */
  fit(): void {
    if (!this.town || !this.width) return;
    this.cam.cx = this.town.width / 2;
    this.cam.cy = this.town.height / 2;
    this.cam.scale = Math.max(MIN_SCALE, Math.min(this.width / this.town.width, this.height / this.town.height) * 0.95);
    this.redraw();
  }

  /** Take the view to a spot, close enough in to work on it. */
  goTo(p: Pt, scale = 10): void {
    this.cam.cx = p[0];
    this.cam.cy = p[1];
    this.cam.scale = Math.max(this.cam.scale, scale);
    this.redraw();
  }

  /** Stop drawing a road (Escape). */
  cancel(): void {
    this.draft = [];
    this.grab = null;
    this.frozen = null;
    this.preview.clear();
    this.redraw();
  }

  /** Finish the road being drawn (Enter, or a double-click). */
  finishDraw(): void {
    if (this.draft.length >= 2 && this.town) this.onEdit({ type: 'draw', pts: this.draft }, this.town);
    this.draft = [];
    this.redraw();
  }

  private makeWater(town: Town): HTMLCanvasElement {
    const c = document.createElement('canvas');
    c.width = town.width;
    c.height = town.height;
    const g = c.getContext('2d')!;
    const img = g.createImageData(town.width, town.height);
    for (let i = 0; i < this.water.length; i++) {
      if (!this.water[i]) continue;
      img.data[i * 4] = 0x2c;
      img.data[i * 4 + 1] = 0x5a;
      img.data[i * 4 + 2] = 0x7a;
      img.data[i * 4 + 3] = 255;
    }
    g.putImageData(img, 0, 0);
    return c;
  }

  private fitCanvas(): void {
    const r = this.canvas.getBoundingClientRect();
    this.dpr = window.devicePixelRatio || 1;
    this.width = r.width;
    this.height = r.height;
    this.canvas.width = Math.max(1, Math.round(r.width * this.dpr));
    this.canvas.height = Math.max(1, Math.round(r.height * this.dpr));
    if (this.town && !this.fitted) {
      this.fitted = true;
      this.fit();
    }
    this.redraw();
  }

  // --- Frame ---------------------------------------------------------------------------------------

  private readonly frame = () => {
    this.raf = requestAnimationFrame(this.frame);
    if (!this.dirty) return;
    this.dirty = false;
    this.draw();
  };

  private toScreen(p: Pt): { x: number; y: number } {
    return { x: (p[0] - this.cam.cx) * this.cam.scale + this.width / 2, y: (p[1] - this.cam.cy) * this.cam.scale + this.height / 2 };
  }

  private toWorld(x: number, y: number): Pt {
    return [this.cam.cx + (x - this.width / 2) / this.cam.scale, this.cam.cy + (y - this.height / 2) / this.cam.scale];
  }

  /** Where a town point is on screen, in CSS pixels from the canvas's corner (for tests). */
  screenOf(p: Pt): { x: number; y: number } {
    return this.toScreen(p);
  }

  private handlesShown(): boolean {
    return this.cam.scale * 2 >= HANDLE_PX;
  }

  private roadPts(r: TownRoad): Pt[] {
    return this.preview.get(r.id) ?? r.pts;
  }

  private draw(): void {
    const g = this.ctx;
    g.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    g.fillStyle = '#0b0f14';
    g.fillRect(0, 0, this.width, this.height);
    const t = this.town;
    if (!t) return;
    const s = this.cam.scale;
    // World transform for the town's tiles.
    g.setTransform(this.dpr * s, 0, 0, this.dpr * s, this.dpr * (this.width / 2 - this.cam.cx * s), this.dpr * (this.height / 2 - this.cam.cy * s));
    g.fillStyle = LAND;
    g.fillRect(0, 0, t.width, t.height);
    if (this.waterImage) {
      g.imageSmoothingEnabled = false;
      g.drawImage(this.waterImage, 0, 0);
    }
    g.globalAlpha = this.pending ? 0.55 : 1;
    if (this.layers.blocks)
      for (const b of t.blocks) {
        g.fillStyle = b.park ? '#2f5a34' : (TRANSECT_COLORS[b.transect] ?? '#222');
        poly(g, b.poly);
        g.fill();
      }
    if (this.layers.buildings)
      for (const b of t.buildings) {
        g.fillStyle = BUILDING_COLORS[b.kind] ?? '#888';
        poly(g, b.poly);
        g.fill();
      }
    g.globalAlpha = 1;
    g.globalAlpha = this.pending ? 0.6 : 1;
    const roads = this.frozen ?? t;
    this.drawRoads(g, roads);
    g.globalAlpha = 1;
    this.drawJunctions(g, roads);
    this.drawGhost(g, s);
    // Back to screen pixels for the markers and handles.
    g.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    this.drawMarks(g, roads);
    this.drawHandles(g, roads);
    this.drawCentre(g, t);
    this.drawDraw(g);
    this.drawWarnings(g);
  }

  /** The town as it is now, ghosted over the draft. */
  private drawGhost(g: CanvasRenderingContext2D, s: number): void {
    if (this.layers.ghost && this.live) {
      // A thin dashed line down each road as it is now: where the draft's moved one away from it shows.
      g.strokeStyle = 'rgba(190,215,255,0.5)';
      g.lineCap = 'butt';
      g.lineJoin = 'round';
      g.lineWidth = Math.max(0.15, 1.5 / s);
      g.setLineDash([4 / s, 3 / s]);
      for (const r of this.live.roads) {
        line(g, r.pts);
        g.stroke();
      }
      g.setLineDash([]);
    }
  }

  private drawRoads(g: CanvasRenderingContext2D, t: Town): void {
    g.lineCap = 'round';
    g.lineJoin = 'round';
    const order = [...t.roads].sort((a, b) => (a.kind === 'main' ? 1 : 0) - (b.kind === 'main' ? 1 : 0));
    // Kerb and asphalt.
    for (const r of order) {
      const pts = this.roadPts(r);
      const sel = this.selection?.kind === 'road' && this.selection.id === r.id;
      g.strokeStyle = sel ? '#f6b73c' : '#0e1216';
      g.lineWidth = Math.max(r.width, 0.35) + (sel ? 0.9 : 0.5);
      line(g, pts);
      g.stroke();
    }
    for (const r of order) {
      const pts = this.roadPts(r);
      g.strokeStyle = r.kind === 'main' ? '#4a4f57' : r.kind === 'path' ? '#6a5e4a' : '#3a3f46';
      g.lineWidth = Math.max(r.width, 0.35);
      if (this.layers.heat && r.design) g.strokeStyle = heat(r.design.vc);
      line(g, pts);
      g.stroke();
    }
    if (!this.layers.lanes || this.cam.scale < 3) return;
    for (const r of order) {
      const d = r.design;
      if (!d || r.kind === 'path') continue;
      const pts = this.roadPts(r);
      const [fwd, back] = d.lanes;
      const lane = d.width / Math.max(1, fwd + back + (d.divided ? 1 : 0));
      g.lineWidth = 0.08;
      // The middle: a median, double yellow, or nothing on a one-way road.
      if (d.divided) {
        g.strokeStyle = '#3f7d46';
        g.lineWidth = lane * 0.8;
        line(g, pts);
        g.stroke();
      } else if (fwd > 0 && back > 0 && r.kind === 'main') {
        g.strokeStyle = '#e8c547';
        for (const o of [-0.05, 0.05]) {
          line(g, offset(pts, o));
          g.stroke();
        }
      }
      // Lanes the same way: dashed white.
      g.strokeStyle = 'rgba(255,255,255,0.7)';
      g.lineWidth = 0.06;
      g.setLineDash([0.5, 0.5]);
      const half = d.divided ? lane / 2 : 0;
      for (let k = 1; k < fwd; k++) {
        line(g, offset(pts, half + k * lane));
        g.stroke();
      }
      for (let k = 1; k < back; k++) {
        line(g, offset(pts, -(half + k * lane)));
        g.stroke();
      }
      g.setLineDash([]);
      // Turn pockets: an arrow in each.
      for (const p of d.pockets) this.drawPocket(g, pts, p.atTo, p.left, p.length, lane, half);
      // One way: an arrow along it.
      if (fwd === 0 || back === 0) {
        const way = fwd > 0 ? pts : [...pts].reverse();
        const mid = Math.floor(way.length / 2);
        const a = way[Math.max(0, mid - 1)], b = way[Math.min(way.length - 1, mid)];
        arrow(g, a, b, '#fff', 0.5);
      }
    }
  }

  private drawPocket(g: CanvasRenderingContext2D, pts: Pt[], atTo: boolean, left: boolean, length: number, lane: number, half: number): void {
    // Traffic towards the end it's at: the pocket's beside the middle (left) or the kerb (right) on its side.
    const way = atTo ? pts : [...pts].reverse();
    const total = way.reduce((acc, p, i) => (i ? acc + dist(way[i - 1], p) : 0), 0);
    if (total < 1) return;
    const o = left ? half + lane * 0.5 : half + lane * 1.5;
    const side = offset(way, o);
    const end = pointAlong(side, Math.max(0, total - 0.8));
    const start = pointAlong(side, Math.max(0, total - 0.8 - Math.min(length, total * 0.6)));
    g.strokeStyle = '#ffffff';
    g.lineWidth = 0.06;
    line(g, [start, end]);
    g.stroke();
    // The arrow's head turns the pocket's way.
    const dx = end[0] - start[0], dy = end[1] - start[1], l = Math.hypot(dx, dy) || 1;
    const ux = dx / l, uy = dy / l;
    // Screen right of travel is (-uy, ux); left is the other way.
    const sx = left ? uy : -uy, sy = left ? -ux : ux;
    g.fillStyle = '#ffffff';
    g.beginPath();
    g.moveTo(end[0] + sx * 0.35, end[1] + sy * 0.35);
    g.lineTo(end[0] - ux * 0.15, end[1] - uy * 0.15);
    g.lineTo(end[0] + ux * 0.25 + sx * 0.05, end[1] + uy * 0.25 + sy * 0.05);
    g.closePath();
    g.fill();
  }

  private drawJunctions(g: CanvasRenderingContext2D, t: Town): void {
    for (const j of t.junctions) {
      if (j.form === 'roundabout') {
        g.strokeStyle = '#4a4f57';
        g.lineWidth = 1.6;
        g.beginPath();
        g.arc(j.at[0], j.at[1], j.radius, 0, Math.PI * 2);
        g.stroke();
        g.fillStyle = '#3f7d46';
        g.beginPath();
        g.arc(j.at[0], j.at[1], Math.max(0.4, j.radius - 0.8), 0, Math.PI * 2);
        g.fill();
      }
      for (const slip of j.slips) {
        g.strokeStyle = '#3a3f46';
        g.lineWidth = 0.7;
        line(g, slip);
        g.stroke();
      }
    }
  }

  private drawMarks(g: CanvasRenderingContext2D, t: Town): void {
    const s = this.cam.scale;
    if (this.layers.controls && s >= 2.5)
      for (const j of t.junctions) {
        const p = this.toScreen(j.at);
        const r = Math.max(3, Math.min(7, s * 0.6));
        if (j.control === 'signal') {
          g.fillStyle = '#111';
          g.fillRect(p.x - r * 0.6, p.y - r * 1.4, r * 1.2, r * 2.8);
          ['#ff5b5b', '#f6b73c', '#3ddc84'].forEach((c, i) => {
            g.fillStyle = c;
            g.beginPath();
            g.arc(p.x, p.y - r * 0.9 + i * r * 0.9, r * 0.35, 0, Math.PI * 2);
            g.fill();
          });
          if (j.protectedLeft) {
            g.fillStyle = '#3ddc84';
            g.font = `${Math.round(r * 1.4)}px system-ui`;
            g.fillText('←', p.x + r, p.y);
          }
        } else if (j.control === 'stop' || j.control === 'allway') {
          octagon(g, p.x, p.y, r, '#d33');
          if (j.control === 'allway') {
            g.strokeStyle = '#fff';
            g.lineWidth = 1;
            g.beginPath();
            g.arc(p.x, p.y, r + 2, 0, Math.PI * 2);
            g.stroke();
          }
        } else if (j.control === 'yield') {
          g.fillStyle = '#fff';
          g.strokeStyle = '#d33';
          g.lineWidth = 1.5;
          g.beginPath();
          g.moveTo(p.x - r, p.y - r * 0.8);
          g.lineTo(p.x + r, p.y - r * 0.8);
          g.lineTo(p.x, p.y + r);
          g.closePath();
          g.fill();
          g.stroke();
        }
      }
    // Gates: where the highways come in.
    for (const q of t.gates) {
      const p = this.toScreen(q);
      g.fillStyle = '#8ab4ff';
      g.beginPath();
      g.arc(p.x, p.y, 5, 0, Math.PI * 2);
      g.fill();
    }
    if (t.secondCentre) {
      const p = this.toScreen(t.secondCentre);
      g.strokeStyle = '#f2a541';
      g.lineWidth = 2;
      g.beginPath();
      g.arc(p.x, p.y, 8, 0, Math.PI * 2);
      g.stroke();
    }
    // Houses the draft loses.
    g.strokeStyle = '#ff6b6b';
    g.lineWidth = 2;
    for (const h of this.lost) {
      const p = this.toScreen([h[0] + 1, h[1] + 1]);
      const r = Math.max(3, s * 0.8);
      g.beginPath();
      g.moveTo(p.x - r, p.y - r);
      g.lineTo(p.x + r, p.y + r);
      g.moveTo(p.x + r, p.y - r);
      g.lineTo(p.x - r, p.y + r);
      g.stroke();
    }
    for (const m of this.marks) {
      const p = this.toScreen(m.at);
      g.fillStyle = m.level === 'error' ? '#ff6b6b' : '#f6b73c';
      g.beginPath();
      g.arc(p.x, p.y, 8, 0, Math.PI * 2);
      g.fill();
      g.fillStyle = '#111';
      g.font = 'bold 11px system-ui';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText('!', p.x, p.y + 0.5);
      g.textAlign = 'start';
      g.textBaseline = 'alphabetic';
    }
  }

  private drawHandles(g: CanvasRenderingContext2D, t: Town): void {
    const grab = this.grab;
    if (grab?.kind === 'node' && grab.snap) {
      g.strokeStyle = 'rgba(138,180,255,0.8)';
      g.setLineDash([4, 4]);
      g.lineWidth = 1;
      for (const [a, b] of grab.snap.guides) {
        const p = this.toScreen(a), q = this.toScreen(b);
        g.beginPath();
        g.moveTo(p.x, p.y);
        g.lineTo(q.x, q.y);
        g.stroke();
      }
      g.setLineDash([]);
    }
    if (!this.handlesShown() || this.tool === 'draw') {
      if (grab?.kind === 'node') this.handle(g, grab.at, 6, '#f6b73c', this.warnings.length > 0);
      return;
    }
    const view = this.viewBox();
    for (const r of t.roads) {
      const pts = this.roadPts(r);
      if (!pts.some((p) => inBox(p, view))) continue;
      for (let i = 1; i < pts.length - 1; i++) this.handle(g, pts[i], 3.5, '#d7dde4', false);
      for (let i = 0; i + 1 < pts.length; i++) {
        const m: Pt = [(pts[i][0] + pts[i + 1][0]) / 2, (pts[i][1] + pts[i + 1][1]) / 2];
        if (dist(pts[i], pts[i + 1]) * this.cam.scale < 24) continue;
        const p = this.toScreen(m);
        g.strokeStyle = 'rgba(215,221,228,0.55)';
        g.lineWidth = 1;
        g.beginPath();
        g.arc(p.x, p.y, 3, 0, Math.PI * 2);
        g.stroke();
      }
    }
    for (const n of t.nodes) {
      if (n.legs === 2 || !inBox(n.at, view)) continue;
      const at = grab?.kind === 'node' && grab.node === n.id ? grab.at : n.at;
      const sel = this.selection?.kind === 'junction' && this.selection.node === n.id;
      this.handle(g, at, 6, n.gate ? '#8ab4ff' : sel ? '#f6b73c' : '#ffffff', grab?.kind === 'node' && grab.node === n.id && this.warnings.length > 0);
    }
  }

  private handle(g: CanvasRenderingContext2D, at: Pt, r: number, color: string, bad: boolean): void {
    const p = this.toScreen(at);
    g.fillStyle = bad ? '#ff6b6b' : color;
    g.strokeStyle = '#0b0f14';
    g.lineWidth = 1.5;
    g.beginPath();
    g.arc(p.x, p.y, r, 0, Math.PI * 2);
    g.fill();
    g.stroke();
  }

  private centreAt(t: Town): Pt {
    return this.grab?.kind === 'center' ? this.grab.at : (this.center ?? t.center);
  }

  private drawCentre(g: CanvasRenderingContext2D, t: Town): void {
    const p = this.toScreen(this.centreAt(t));
    ['#d33', '#fff', '#d33', '#fff'].forEach((c, i) => {
      g.fillStyle = c;
      g.beginPath();
      g.arc(p.x, p.y, 14 - i * 3.5, 0, Math.PI * 2);
      g.fill();
    });
    g.strokeStyle = '#0b0f14';
    g.lineWidth = 1.5;
    g.beginPath();
    g.arc(p.x, p.y, 14, 0, Math.PI * 2);
    g.stroke();
    // How far the centre's pull reaches: the town's radius.
    g.strokeStyle = 'rgba(221,51,51,0.35)';
    g.setLineDash([6, 6]);
    g.beginPath();
    g.arc(p.x, p.y, t.townRadius * this.cam.scale, 0, Math.PI * 2);
    g.stroke();
    g.setLineDash([]);
  }

  private drawDraw(g: CanvasRenderingContext2D): void {
    if (this.tool !== 'draw' || (this.draft.length === 0 && !this.cursor)) return;
    const pts = this.cursor ? [...this.draft, this.cursor] : this.draft;
    g.strokeStyle = this.drawClass === 'arterial' ? '#f6b73c' : this.drawClass === 'collector' ? '#9fd8cb' : '#d7dde4';
    g.lineWidth = Math.max(2, this.cam.scale * (this.drawClass === 'arterial' ? 2 : 1));
    g.globalAlpha = 0.7;
    g.beginPath();
    pts.forEach((q, i) => {
      const p = this.toScreen(q);
      if (i) g.lineTo(p.x, p.y);
      else g.moveTo(p.x, p.y);
    });
    g.stroke();
    g.globalAlpha = 1;
    for (const q of this.draft) this.handle(g, q, 4, '#f6b73c', false);
  }

  private drawWarnings(g: CanvasRenderingContext2D): void {
    if (!this.warnings.length || this.grab?.kind !== 'node') return;
    const p = this.toScreen(this.grab.at);
    g.font = '12px system-ui';
    const lines = this.warnings;
    const w = Math.max(...lines.map((l) => g.measureText(l).width)) + 12;
    const x = Math.min(this.width - w - 4, p.x + 14), y = p.y + 14;
    g.fillStyle = 'rgba(40,10,10,0.9)';
    g.fillRect(x, y, w, lines.length * 16 + 8);
    g.fillStyle = '#ff8b8b';
    lines.forEach((l, i) => g.fillText(l, x + 6, y + 16 + i * 16));
  }

  private viewBox(): [number, number, number, number] {
    const [x0, y0] = this.toWorld(0, 0);
    const [x1, y1] = this.toWorld(this.width, this.height);
    return [x0 - 2, y0 - 2, x1 + 2, y1 + 2];
  }

  // --- Picking -------------------------------------------------------------------------------------

  private pick(x: number, y: number): Grab | { kind: 'road'; road: number; at: Pt } | null {
    const t = this.town;
    if (!t) return null;
    const w = this.toWorld(x, y);
    const near = (p: Pt, px: number) => {
      const s = this.toScreen(p);
      return Math.hypot(s.x - x, s.y - y) <= px;
    };
    if (near(this.centreAt(t), 15)) return { kind: 'center', at: this.centreAt(t) };
    if (this.handlesShown()) {
      for (const n of t.nodes) if (n.legs !== 2 && !n.gate && near(n.at, 8)) return { kind: 'node', node: n.id, at: n.at, snap: null };
      for (const r of t.roads) {
        for (let i = 1; i < r.pts.length - 1; i++) if (near(r.pts[i], 6)) return { kind: 'bend', road: r.id, index: i, at: r.pts[i] };
        for (let i = 0; i + 1 < r.pts.length; i++) {
          if (dist(r.pts[i], r.pts[i + 1]) * this.cam.scale < 24) continue;
          const m: Pt = [(r.pts[i][0] + r.pts[i + 1][0]) / 2, (r.pts[i][1] + r.pts[i + 1][1]) / 2];
          if (near(m, 6)) return { kind: 'newBend', road: r.id, seg: i, at: m };
        }
      }
    }
    let best: { road: number; d: number; at: Pt } | null = null;
    for (const r of t.roads) {
      const c = closestOn(w, r.pts);
      const reach = r.width / 2 + 4 / this.cam.scale;
      if (c.d <= reach && (!best || c.d < best.d)) best = { road: r.id, d: c.d, at: c.point };
    }
    return best ? { kind: 'road', road: best.road, at: best.at } : null;
  }

  private junctionAt(x: number, y: number): number | null {
    const t = this.town;
    if (!t) return null;
    for (const n of t.nodes) {
      if (n.legs < 3) continue;
      const s = this.toScreen(n.at);
      if (Math.hypot(s.x - x, s.y - y) <= Math.max(8, this.cam.scale * 1.2)) return n.id;
    }
    return null;
  }

  // --- Input ---------------------------------------------------------------------------------------

  private local(e: { clientX: number; clientY: number }): { x: number; y: number } {
    const r = this.canvas.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  }

  private zoomAt(px: number, py: number, scale: number): void {
    const before = this.toWorld(px, py);
    this.cam.scale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, scale));
    const after = this.toWorld(px, py);
    this.cam.cx += before[0] - after[0];
    this.cam.cy += before[1] - after[1];
    this.redraw();
  }

  private readonly wheel = (e: WheelEvent) => {
    e.preventDefault();
    const p = this.local(e);
    const lines = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? 400 : 1;
    this.zoomAt(p.x, p.y, this.cam.scale * Math.exp(-e.deltaY * lines * 0.0015));
  };

  private readonly dbl = (e: MouseEvent) => {
    if (this.tool === 'draw') return this.finishDraw();
    const p = this.local(e);
    this.zoomAt(p.x, p.y, this.cam.scale * 2);
  };

  private readonly context = (e: MouseEvent) => {
    e.preventDefault();
    if (this.tool === 'draw') this.finishDraw();
  };

  private readonly down = (e: PointerEvent) => {
    if (e.button === 2) return;
    this.canvas.setPointerCapture(e.pointerId);
    const p = this.local(e);
    this.pointers.set(e.pointerId, p);
    if (this.pointers.size === 2) {
      const [a, b] = [...this.pointers.values()];
      this.pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), scale: this.cam.scale };
      this.grab = null;
      this.frozen = null;
      this.preview.clear();
      return;
    }
    this.downAt = p;
    this.moved = false;
    const hit = this.tool === 'select' ? this.pick(p.x, p.y) : null;
    this.grab = hit && hit.kind !== 'road' ? hit : { kind: 'pan', last: p };
    if (this.grab.kind !== 'pan') this.frozen = this.town;
  };

  private readonly move = (e: PointerEvent) => {
    const p = this.local(e);
    if (this.tool === 'draw') {
      const t = this.town;
      this.cursor = t ? snapNode(t, null, this.toWorld(p.x, p.y)).p : this.toWorld(p.x, p.y);
      this.redraw();
    }
    if (!this.pointers.has(e.pointerId)) return;
    this.pointers.set(e.pointerId, p);
    if (this.pointers.size === 2 && this.pinch) {
      const [a, b] = [...this.pointers.values()];
      this.zoomAt((a.x + b.x) / 2, (a.y + b.y) / 2, this.pinch.scale * (Math.hypot(a.x - b.x, a.y - b.y) / Math.max(1, this.pinch.d)));
      return;
    }
    if (this.downAt && !this.moved && Math.hypot(p.x - this.downAt.x, p.y - this.downAt.y) < 3) return;
    this.moved = true;
    const grab = this.grab;
    if (!grab) return;
    const w = this.toWorld(p.x, p.y);
    const t = this.frozen ?? this.town;
    switch (grab.kind) {
      case 'pan':
        this.cam.cx -= (p.x - grab.last.x) / this.cam.scale;
        this.cam.cy -= (p.y - grab.last.y) / this.cam.scale;
        grab.last = p;
        break;
      case 'center':
        grab.at = w;
        this.schedulePause();
        break;
      case 'node':
        if (!t) break;
        grab.snap = snapNode(t, grab.node, w);
        grab.at = grab.snap.p;
        this.preview = dragNode(t, grab.node, grab.at);
        this.warnings = dragWarnings(t, grab.node, grab.at, this.preview, this.water);
        this.onWarnings(this.warnings);
        this.schedulePause();
        break;
      case 'bend':
      case 'newBend': {
        if (!t) break;
        grab.at = w;
        const r = t.roads.find((x) => x.id === grab.road)!;
        const pts = grab.kind === 'bend' ? pullBend(r.pts, grab.index, w) : pullBend(withBend(r.pts, grab.seg, w), grab.seg + 1, w);
        this.preview = new Map([[r.id, pts]]);
        this.schedulePause();
        break;
      }
    }
    this.redraw();
  };

  private readonly up = (e: PointerEvent) => {
    if (e.button === 2) return;
    const p = this.local(e);
    this.pointers.delete(e.pointerId);
    if (this.pointers.size < 2) this.pinch = null;
    const grab = this.grab;
    this.grab = null;
    if (this.pauseTimer) clearTimeout(this.pauseTimer);
    this.pauseTimer = null;
    this.warnings = [];
    this.onWarnings([]);
    if (!this.moved) {
      this.frozen = null;
      this.click(p.x, p.y);
      this.preview.clear();
      this.redraw();
      return;
    }
    const edit = grab ? this.editOf(grab) : null;
    const t = this.frozen ?? this.town;
    if (edit && t) this.onEdit(edit, t);
    else this.frozen = null;
    // Kept bent until the answer comes (setTown clears it).
    this.redraw();
  };

  private editOf(grab: Grab): Edit | null {
    switch (grab.kind) {
      case 'center':
        return { type: 'center', to: grab.at };
      case 'node':
        return { type: 'node', node: grab.node, to: grab.at, merge: grab.snap?.merge, join: grab.snap?.join };
      case 'bend':
        return { type: 'bend', road: grab.road, index: grab.index, to: grab.at };
      case 'newBend':
        return { type: 'newBend', road: grab.road, seg: grab.seg, to: grab.at };
      default:
        return null;
    }
  }

  private schedulePause(): void {
    if (this.pauseTimer) clearTimeout(this.pauseTimer);
    this.pauseTimer = setTimeout(() => {
      this.pauseTimer = null;
      const edit = this.grab ? this.editOf(this.grab) : null;
      const t = this.frozen ?? this.town;
      if (edit && t) this.onPause(edit, t);
    }, PAUSE_MS);
  }

  private click(x: number, y: number): void {
    const t = this.town;
    if (!t) return;
    const w = this.toWorld(x, y);
    if (this.tool === 'draw') {
      this.draft.push(snapNode(t, null, w).p);
      return;
    }
    const hit = this.pick(x, y);
    const road = hit?.kind === 'road' ? hit.road : hit && 'road' in hit ? hit.road : null;
    if (this.tool === 'delete') {
      if (road !== null) this.onEdit({ type: 'delete', road }, t);
      return;
    }
    if (this.tool === 'split') {
      if (road !== null) this.onEdit({ type: 'split', road, at: closestOn(w, t.roads.find((r) => r.id === road)!.pts).point }, t);
      return;
    }
    const j = this.junctionAt(x, y);
    if (j !== null) this.onSelect({ kind: 'junction', node: j });
    else if (road !== null) this.onSelect({ kind: 'road', id: road });
    else this.onSelect(null);
  }
}

// --- Drawing helpers ---------------------------------------------------------------------------------

function poly(g: CanvasRenderingContext2D, pts: Pt[]): void {
  g.beginPath();
  pts.forEach((p, i) => (i ? g.lineTo(p[0], p[1]) : g.moveTo(p[0], p[1])));
  g.closePath();
}

function line(g: CanvasRenderingContext2D, pts: Pt[]): void {
  g.beginPath();
  pts.forEach((p, i) => (i ? g.lineTo(p[0], p[1]) : g.moveTo(p[0], p[1])));
}

/** A polyline moved `d` tiles to the right of its way (screen right, y down): the side its forward traffic drives on. */
export function offset(pts: Pt[], d: number): Pt[] {
  return pts.map((p, i) => {
    const a = pts[Math.max(0, i - 1)], b = pts[Math.min(pts.length - 1, i + 1)];
    const dx = b[0] - a[0], dy = b[1] - a[1], l = Math.hypot(dx, dy) || 1;
    return [p[0] - (dy / l) * d, p[1] + (dx / l) * d];
  });
}

function pointAlong(pts: Pt[], s: number): Pt {
  for (let i = 0; i + 1 < pts.length; i++) {
    const l = dist(pts[i], pts[i + 1]);
    if (s <= l) {
      const t = l ? s / l : 0;
      return [pts[i][0] + (pts[i + 1][0] - pts[i][0]) * t, pts[i][1] + (pts[i + 1][1] - pts[i][1]) * t];
    }
    s -= l;
  }
  return pts[pts.length - 1];
}

function arrow(g: CanvasRenderingContext2D, a: Pt, b: Pt, color: string, size: number): void {
  const dx = b[0] - a[0], dy = b[1] - a[1], l = Math.hypot(dx, dy) || 1;
  const ux = dx / l, uy = dy / l;
  const m: Pt = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
  g.fillStyle = color;
  g.beginPath();
  g.moveTo(m[0] + ux * size, m[1] + uy * size);
  g.lineTo(m[0] - ux * size * 0.5 - uy * size * 0.6, m[1] - uy * size * 0.5 + ux * size * 0.6);
  g.lineTo(m[0] - ux * size * 0.5 + uy * size * 0.6, m[1] - uy * size * 0.5 - ux * size * 0.6);
  g.closePath();
  g.fill();
}

function octagon(g: CanvasRenderingContext2D, x: number, y: number, r: number, color: string): void {
  g.fillStyle = color;
  g.beginPath();
  for (let i = 0; i < 8; i++) {
    const a = Math.PI / 8 + (i * Math.PI) / 4;
    if (i) g.lineTo(x + Math.cos(a) * r, y + Math.sin(a) * r);
    else g.moveTo(x + Math.cos(a) * r, y + Math.sin(a) * r);
  }
  g.closePath();
  g.fill();
}

/** A road's load as a colour: green when there's room, amber near full, red over. */
export function heat(vc: number): string {
  if (vc < 0.5) return '#2e8b57';
  if (vc < 0.75) return '#9acd32';
  if (vc < 0.9) return '#f6b73c';
  if (vc < 1) return '#ff8c42';
  return '#ff4d4d';
}

function inBox(p: Pt, b: [number, number, number, number]): boolean {
  return p[0] >= b[0] && p[0] <= b[2] && p[1] >= b[1] && p[1] <= b[3];
}

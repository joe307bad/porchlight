// The edit screen's map: one town, drawn from the draft worker's answer (its roads at their width with
// their lanes, medians, turn pockets, roundabouts, slips and controls; its blocks by zone and its buildings
// by what they're for), with the town as it is now ghosted underneath, the town's circle faint, and the
// downtown bullseye. Click a block to pick it (or put it back); shift-drag, or drag in box mode, to pick every
// block whose middle is inside the box. Drag the bullseye to move downtown: it stays within 0.7 of the town's
// radius of its centre. Pan by dragging, zoom with the wheel or a pinch, double-click to zoom in.

import { blocksInBox, clampDowntown, decodeWater, dist, inPoly } from './skeleton.ts';
import type { BuildingKind, Pt, Town } from './types.ts';

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
type Grab =
  | { kind: 'pan'; last: { x: number; y: number } }
  | { kind: 'box'; from: { x: number; y: number }; to: { x: number; y: number } }
  | { kind: 'downtown'; at: Pt };

export class EditRenderer {
  town: Town | null = null;
  live: Town | null = null;
  layers: EditLayers = DEFAULT_EDIT_LAYERS;
  /** A drag with one finger (or the mouse) draws a box instead of panning: for touch, where there's no shift. */
  boxMode = false;
  /** A re-plan's on its way: what's drawn fades. */
  pending = false;
  /** The houses the draft loses. */
  /** The bullseye as the skeleton has it (shown before the answer catches up); else the answer's downtown. */
  downtown: Pt | null = null;

  /** A block clicked: its polygon, to pick or put back. */
  onToggle: (poly: Pt[]) => void = () => {};
  /** A box drawn: the blocks whose middle is inside it. */
  onBox: (polys: Pt[][]) => void = () => {};
  /** The bullseye let go: where downtown goes (already kept within its reach). */
  onDowntown: (at: Pt) => void = () => {};

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
  private waterImage: HTMLCanvasElement | null = null;
  private water: Uint8Array = new Uint8Array(0);
  private fitted = false;
  /** The picked blocks, by their polygons written out (setSelection). */
  private picked = new Set<string>();

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
    this.redraw();
  }

  redraw(): void {
    this.dirty = true;
  }

  /** The picked blocks, by their polygons, drawn bright. */
  setSelection(sel: Pt[][]): void {
    this.picked = new Set(sel.map((p) => JSON.stringify(p)));
    this.redraw();
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

  /** Stop a box or a bullseye drag in hand (Escape). */
  cancel(): void {
    if (this.grab?.kind !== 'pan') this.grab = null;
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
    this.drawSelection(g, t, s);
    g.globalAlpha = this.pending ? 0.6 : 1;
    this.drawRoads(g, t);
    g.globalAlpha = 1;
    this.drawJunctions(g, t);
    this.drawGhost(g, s);
    this.drawCircle(g, t, s);
    // Back to screen pixels for the markers, the bullseye and the box.
    g.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    this.drawMarks(g, t);
    this.drawDowntown(g, t);
    this.drawBox(g);
  }

  /** The picked blocks: bright over their zone's colour, outlined. */
  private drawSelection(g: CanvasRenderingContext2D, t: Town, s: number): void {
    if (!this.picked.size) return;
    g.lineJoin = 'round';
    for (const b of t.blocks) {
      if (!this.picked.has(JSON.stringify(b.poly))) continue;
      poly(g, b.poly);
      g.fillStyle = 'rgba(246,183,60,0.35)';
      g.fill();
      g.strokeStyle = '#f6b73c';
      g.lineWidth = Math.max(0.2, 2 / s);
      g.stroke();
    }
  }

  /** The town's circle, faint: the whole town stays inside it. */
  private drawCircle(g: CanvasRenderingContext2D, t: Town, s: number): void {
    g.strokeStyle = 'rgba(190,215,255,0.22)';
    g.lineWidth = Math.max(0.15, 1.5 / s);
    g.setLineDash([6 / s, 6 / s]);
    g.beginPath();
    g.arc(t.center[0], t.center[1], t.townRadius, 0, Math.PI * 2);
    g.stroke();
    g.setLineDash([]);
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
      g.strokeStyle = '#0e1216';
      g.lineWidth = Math.max(r.width, 0.35) + 0.5;
      line(g, r.pts);
      g.stroke();
    }
    for (const r of order) {
      const pts = r.pts;
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
      const pts = r.pts;
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
  }

  /** Where the bullseye is drawn: in hand, as the skeleton has it, or where the plan put downtown. */
  private downtownAt(t: Town): Pt {
    if (this.grab?.kind === 'downtown') return this.grab.at;
    return this.downtown ?? t.downtown ?? t.center;
  }

  private drawDowntown(g: CanvasRenderingContext2D, t: Town): void {
    const p = this.toScreen(this.downtownAt(t));
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
    g.font = 'bold 11px system-ui';
    g.textAlign = 'center';
    g.lineWidth = 3;
    g.strokeStyle = 'rgba(11,15,20,0.85)';
    g.strokeText('Downtown', p.x, p.y + 27);
    g.fillStyle = '#fff';
    g.fillText('Downtown', p.x, p.y + 27);
    g.textAlign = 'start';
  }

  private drawBox(g: CanvasRenderingContext2D): void {
    if (this.grab?.kind !== 'box') return;
    const { from, to } = this.grab;
    const x = Math.min(from.x, to.x), y = Math.min(from.y, to.y), w = Math.abs(to.x - from.x), h = Math.abs(to.y - from.y);
    g.fillStyle = 'rgba(246,183,60,0.12)';
    g.fillRect(x, y, w, h);
    g.strokeStyle = '#f6b73c';
    g.lineWidth = 1;
    g.setLineDash([5, 4]);
    g.strokeRect(x + 0.5, y + 0.5, w, h);
    g.setLineDash([]);
  }

  // --- Picking -------------------------------------------------------------------------------------

  private nearDowntown(x: number, y: number): boolean {
    if (!this.town) return false;
    const s = this.toScreen(this.downtownAt(this.town));
    return Math.hypot(s.x - x, s.y - y) <= 15;
  }

  /** The block under a point on screen, if any. */
  private blockAt(x: number, y: number): Pt[] | null {
    const t = this.town;
    if (!t) return null;
    const w = this.toWorld(x, y);
    for (const b of t.blocks) if (inPoly(w, b.poly)) return b.poly;
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
    const p = this.local(e);
    this.zoomAt(p.x, p.y, this.cam.scale * 2);
  };

  private readonly context = (e: MouseEvent) => {
    e.preventDefault();
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
      this.redraw();
      return;
    }
    this.downAt = p;
    this.moved = false;
    if (this.town && this.nearDowntown(p.x, p.y)) this.grab = { kind: 'downtown', at: this.downtownAt(this.town) };
    else if (e.shiftKey || this.boxMode) this.grab = { kind: 'box', from: p, to: p };
    else this.grab = { kind: 'pan', last: p };
  };

  private readonly move = (e: PointerEvent) => {
    const p = this.local(e);
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
    switch (grab.kind) {
      case 'pan':
        this.cam.cx -= (p.x - grab.last.x) / this.cam.scale;
        this.cam.cy -= (p.y - grab.last.y) / this.cam.scale;
        grab.last = p;
        break;
      case 'box':
        grab.to = p;
        break;
      case 'downtown': {
        const w = this.toWorld(p.x, p.y);
        grab.at = this.town ? clampDowntown(w, this.town.center, this.town.townRadius) : w;
        break;
      }
    }
    this.redraw();
  };

  private readonly up = (e: PointerEvent) => {
    if (e.button === 2) return;
    const p = this.local(e);
    const wasPinch = this.pinch !== null;
    this.pointers.delete(e.pointerId);
    if (this.pointers.size < 2) this.pinch = null;
    const grab = this.grab;
    this.grab = null;
    this.redraw();
    if (wasPinch || !grab) return;
    if (!this.moved) {
      // A click: the block under it picked, or put back.
      const b = this.blockAt(p.x, p.y);
      if (b) this.onToggle(b);
      return;
    }
    if (grab.kind === 'box' && this.town) this.onBox(blocksInBox(this.town.blocks, this.toWorld(grab.from.x, grab.from.y), this.toWorld(grab.to.x, grab.to.y)));
    else if (grab.kind === 'downtown') this.onDowntown(grab.at);
  };
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

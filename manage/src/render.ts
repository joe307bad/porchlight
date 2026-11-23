// The map, drawn on a canvas. Its still parts (the towns' ground and streets,
// the highways) are built once into Path2Ds in the county's frame and drawn
// into a cached canvas only when the view moves; each animation frame draws
// that, then everything moving, batched by colour into one path a colour, and
// only what's in view. Pan by dragging, zoom with the wheel or a pinch.

import type { CountyMap } from './api.ts';
import { REACH, STOPPED, doing, type Layer, type PlayerView, type World } from './world.ts';
import { streetLabels, type StreetLabel } from './streets.ts';
import { ZONES, buildingIndexAt, describe } from './zoning.ts';
import { congestionWidth, focusPoint, indoorRadius, indoors, lampColor, type CongestedStreet, type EntityFocus, type Signals, type TownLife } from './life.ts';

export interface Layers {
  roads: boolean;
  /** What the buildings are for (land use). */
  zoning: boolean;
  cars: boolean;
  people: boolean;
  players: boolean;
  names: boolean;
  reach: boolean;
  trails: boolean;
  /** Traffic lights, a lamp at each stop line (close in). */
  signals: boolean;
  /** Stop signs, one on the kerb by each lane that stops (close in). */
  stops: boolean;
}

export const DEFAULT_LAYERS: Layers = { roads: true, zoning: true, cars: true, people: true, players: true, names: true, reach: true, trails: false, signals: true, stops: true };

/** Lamp colours. */
export const LAMPS = { green: '#3ddc84', amber: '#ffb020', red: '#ff4d4d' } as const;
/** Stop signs: stopping for a bigger road, and an all-way stop. */
export const STOP_SIGN = '#e5484d';
export const ALL_WAY = '#ff8fa3';
/** Closer in than this (pixels a tile), traffic lights are drawn. */
export const SIGNALS_FROM = 2;

/** Car colours by speed (tiles a second): stopped, crawling, moving, fast. */
export const SPEEDS = [
  { under: STOPPED, color: '#ff4d4f', label: 'Stopped' },
  { under: 1.5, color: '#ffb02e', label: 'Crawling' },
  { under: 4, color: '#3ddc84', label: 'Moving' },
  { under: Infinity, color: '#38bdf8', label: 'Fast' },
];
export const PERSON = '#e9e4ff';
/** The focus's ring and its way ahead. */
export const FOCUS = '#ffcf4a';
/** The congestion slider's streets. */
export const CONGESTED = '#ff4d4d';
/** People indoors, drawn on their buildings. */
export const INDOORS = '#f4f1e8';
const PERSON_PX = pixel(PERSON, 0.9);
const PERSON_DIM_PX = pixel(PERSON, 0.35);
const SPEED_PX = SPEEDS.map((s) => pixel(s.color, 1));
const SPEED_DIM_PX = SPEEDS.map((s) => pixel(s.color, 0.45));

/** Pixels a tile from which cars are drawn their true shape (further out, as dots). */
const SHAPED = 4;
/** From this close in (pixels a tile), streets are named on the map. */
const STREET_NAMES = 6;

/** A colour (#rrggbb) and opacity as one pixel of an ImageData, as a little-endian Uint32Array holds it. */
function pixel(hex: string, alpha: number): number {
  const n = parseInt(hex.slice(1), 16);
  return ((Math.round(alpha * 255) << 24) | ((n & 0xff) << 16) | (n & 0xff00) | ((n >> 16) & 0xff)) >>> 0;
}

/** Which of SPEEDS a car's speed is. */
function speedBand(s: number): number {
  for (let b = 0; b < SPEEDS.length - 1; b++) if (s < SPEEDS[b].under) return b;
  return SPEEDS.length - 1;
}

const MIN_SCALE = 0.15;
const MAX_SCALE = 40;
/** One tile is about 4 m: tiles a second to km/h. */
export const KMH = 14.4;

export interface Hover {
  /** The car or townsperson it's about (town|kind|slot), if it's the simulation's line on them. */
  key?: string;
  x: number;
  y: number;
  text: string;
}

interface Camera {
  cx: number;
  cy: number;
  /** Pixels (CSS) a tile. */
  scale: number;
}

interface StreetPaths {
  /** By kind (0 main, 1 street, 2 path), then by width. */
  streets: { kind: number; width: number; path: Path2D }[];
  /** The buildings, a path a zone (ZONES order). */
  zones: Path2D[];
  highways: Path2D;
  towns: CountyMap['towns'];
  spaces: Path2D;
  bounds: { x0: number; y0: number; x1: number; y1: number };
  /** Where each street's name is written (streets.ts), main roads first. */
  labels: StreetLabel[];
}

function buildPaths(map: CountyMap): StreetPaths {
  const byKey = new Map<string, { kind: number; width: number; path: Path2D }>();
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  const grow = (x: number, y: number) => {
    x0 = Math.min(x0, x); y0 = Math.min(y0, y);
    x1 = Math.max(x1, x); y1 = Math.max(y1, y);
  };
  const zones = ZONES.map(() => new Path2D());
  for (const t of map.towns) {
    for (const b of t.buildings ?? []) zones[b[0]]?.rect(t.x0 + b[1], t.y0 + b[2], b[3] - b[1], b[4] - b[2]);
    grow(t.x0, t.y0);
    grow(t.x0 + t.width, t.y0 + t.height);
    for (const r of t.roads ?? []) {
      const kind = r[0];
      const width = Math.max(0.5, Math.round(r[1] * 2) / 2);
      const k = `${kind}:${width}`;
      let s = byKey.get(k);
      if (!s) byKey.set(k, (s = { kind, width, path: new Path2D() }));
      s.path.moveTo(t.x0 + r[2], t.y0 + r[3]);
      for (let i = 4; i + 1 < r.length; i += 2) s.path.lineTo(t.x0 + r[i], t.y0 + r[i + 1]);
    }
  }
  const highways = new Path2D();
  for (const h of map.highways) {
    highways.moveTo(h[0], h[1]);
    grow(h[0], h[1]);
    for (let i = 2; i + 1 < h.length; i += 2) {
      highways.lineTo(h[i], h[i + 1]);
      grow(h[i], h[i + 1]);
    }
  }
  const spaces = new Path2D();
  for (const s of map.spaces) spaces.rect(s.x0, s.y0, s.width, s.height);
  // Paths under streets, streets under main roads.
  const streets = [...byKey.values()].sort((a, b) => b.kind - a.kind || a.width - b.width);
  return { streets, zones, highways, towns: map.towns, spaces, labels: streetLabels(map.towns), bounds: { x0, y0, x1, y1 } };
}

export class MapRenderer {
  private readonly ctx: CanvasRenderingContext2D;
  private readonly still = document.createElement('canvas');
  private readonly trail = document.createElement('canvas');
  private stillKey = '';
  private paths: StreetPaths;
  /** Each town's life, by key (the simulation's: who's indoors where), once known. */
  readonly life = new Map<string, TownLife>();
  private lifeVersion = 0;

  /** A town's life as the server last said: drawn on its buildings. */
  setLife(key: string, life: TownLife): void {
    this.life.set(key, life);
    this.lifeVersion++;
  }
  /** The kinds of car and person not drawn (ENTITY_KINDS' ids: 'car:v', 'person:x', …). */
  hidden = new Set<string>();
  /** Whether a town's ith car or person is of a kind that's hidden (by its town's latest life). */
  private off(key: string, kind: 'car' | 'person', i: number): boolean {
    if (this.hidden.size === 0) return false;
    const k = this.life.get(key)?.kinds;
    if (!k) return false;
    const ch = (kind === 'car' ? k.cars : k.people)[i];
    return ch !== undefined && this.hidden.has(`${kind}:${ch}`);
  }
  /** The building under the pointer, if its town's life is known: [town key, its index]. */
  hoverBuilding: { key: string; i: number } | null = null;
  private cam: Camera = { cx: 0, cy: 0, scale: 1 };
  private readonly map: CountyMap;
  private width = 0;
  private height = 0;
  private dpr = 1;
  private raf = 0;
  private following: string | null = null;
  private selected: string | null = null;
  private readonly pointers = new Map<number, { x: number; y: number }>();
  private dragged = false;
  private pinch: { d: number; scale: number } | null = null;
  private readonly resize: ResizeObserver;
  private lastHover = 0;
  layers: Layers = { ...DEFAULT_LAYERS };
  /** Drawn frames a second, for the panel. */
  fps = 0;
  private fpsCount = 0;
  private fpsAt = 0;
  onSelect: (sid: string | null) => void = () => {};
  onFollowEnd: () => void = () => {};
  /** A building clicked (its town's key and index), or null (somewhere else). */
  onBuilding: (b: { key: string; i: number } | null) => void = () => {};
  onHover: (h: Hover | null) => void = () => {};
  /** The car or townsperson under the pointer (null: none), for a line about them from the simulation. */
  onHoverEntity: (e: { key: string; kind: 'car' | 'person'; slot: number; x: number; y: number } | null) => void = () => {};
  /** A car or townsperson clicked: its town, and its place in the feed's records. */
  onEntity: (e: { key: string; kind: 'car' | 'person'; slot: number }) => void = () => {};
  /** A right-click on the map: where on the page (for the menu), and the spot in the county's tiles. */
  onContext: (c: { clientX: number; clientY: number; x: number; y: number }) => void = () => {};
  /** The camera's stopped following the focus (the map was dragged). */
  onFocusFollowEnd: () => void = () => {};
  /** The car or townsperson focused on (Manage's focus view), drawn with its way ahead; followed, if `followingFocus`. */
  private focused: EntityFocus | null = null;
  private followingFocus = false;

  private readonly canvas: HTMLCanvasElement;
  private readonly world: World;

  /** `loop` false: no animation loop of its own, frameAt() draws (the stress test). */
  constructor(canvas: HTMLCanvasElement, world: World, map: CountyMap, loop = true) {
    this.canvas = canvas;
    this.world = world;
    this.ctx = canvas.getContext('2d', { alpha: false })!;
    this.paths = buildPaths(map);
    this.map = map;
    this.resize = new ResizeObserver(() => this.fitCanvas());
    this.resize.observe(canvas);
    this.fitCanvas();
    this.fitAll();
    canvas.addEventListener('wheel', this.wheel, { passive: false });
    canvas.addEventListener('pointerdown', this.down);
    canvas.addEventListener('pointermove', this.move);
    canvas.addEventListener('pointerup', this.up);
    canvas.addEventListener('pointercancel', this.up);
    canvas.addEventListener('pointerleave', this.leave);
    canvas.addEventListener('dblclick', this.dbl);
    canvas.addEventListener('contextmenu', this.context);
    if (loop) this.raf = requestAnimationFrame(this.frame);
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
    c.removeEventListener('pointerleave', this.leave);
    c.removeEventListener('dblclick', this.dbl);
    c.removeEventListener('contextmenu', this.context);
  }

  setMap(map: CountyMap): void {
    this.paths = buildPaths(map);
    this.stillKey = '';
  }

  /** The whole county in view. */
  /** The towns on screen (their keys), if it's close enough in to see their traffic move; none further out. */
  townsInView(minScale = 1.5): string[] {
    const { cx, cy, scale } = this.cam;
    if (scale < minScale || !this.width) return [];
    const x0 = cx - this.width / 2 / scale, x1 = cx + this.width / 2 / scale;
    const y0 = cy - this.height / 2 / scale, y1 = cy + this.height / 2 / scale;
    return this.map.towns.filter((t) => t.x0 < x1 && t.x0 + t.width > x0 && t.y0 < y1 && t.y0 + t.height > y0).map((t) => t.key);
  }

  fitAll(): void {
    const b = this.paths.bounds;
    if (!Number.isFinite(b.x0)) return;
    this.fit(b.x0, b.y0, b.x1 - b.x0, b.y1 - b.y0, 24);
  }

  fit(x0: number, y0: number, w: number, h: number, pad = 32): void {
    if (!this.width || !this.height) return;
    const scale = Math.min((this.width - pad * 2) / Math.max(1, w), (this.height - pad * 2) / Math.max(1, h));
    this.cam = { cx: x0 + w / 2, cy: y0 + h / 2, scale: clamp(scale, MIN_SCALE, MAX_SCALE) };
  }

  follow(sid: string | null): void {
    this.following = sid;
    this.selected = sid;
    if (sid) this.cam.scale = Math.max(this.cam.scale, 6);
  }

  select(sid: string | null): void {
    this.selected = sid;
  }

  /** The car or townsperson focused on, as the simulation last said (null: none). */
  setFocus(f: EntityFocus | null): void {
    this.focused = f;
    if (!f) this.followingFocus = false;
  }

  /** Keep the focus in the middle of the map (and close enough in to see it), or stop. */
  followFocus(on: boolean): void {
    this.followingFocus = on;
    if (on) {
      this.following = null;
      this.cam.scale = Math.max(this.cam.scale, 8);
    }
  }

  /** Each town's traffic lights as last heard, and how far its clock was from ours then (ms). */
  private signals = new Map<string, { s: Signals; skew: number }>();

  /** Each town's stop signs (they don't change): x, y, 1 if all-way. */
  private stops = new Map<string, [number, number, number][]>();

  /** A town's traffic lights, just heard (null: forget them); its stop signs, if they came too. */
  setSignals(key: string, s: Signals | null): void {
    if (!s) this.signals.delete(key);
    else {
      this.signals.set(key, { s, skew: s.now - Date.now() });
      if (s.stops) this.stops.set(key, s.stops);
    }
  }

  /** The streets Manage's congestion slider picked out (one town's, at one hour), drawn red over the map. */
  private congested: { key: string; streets: CongestedStreet[] } | null = null;

  /** A town's most congested streets to draw (null: none). */
  setCongestion(key: string | null, streets: CongestedStreet[] | null): void {
    this.congested = key && streets && streets.length > 0 ? { key, streets } : null;
  }

  /** Fit the view round one of a town's streets (its centreline, town tiles: x, y, …). */
  fitStreet(key: string, points: number[]): void {
    const t = this.map.towns.find((x) => x.key === key);
    if (!t || points.length < 2) return;
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (let i = 0; i + 1 < points.length; i += 2) {
      x0 = Math.min(x0, points[i]);
      x1 = Math.max(x1, points[i]);
      y0 = Math.min(y0, points[i + 1]);
      y1 = Math.max(y1, points[i + 1]);
    }
    const pad = 6;
    this.fit(t.x0 + x0 - pad, t.y0 + y0 - pad, x1 - x0 + 2 * pad, y1 - y0 + 2 * pad, 64);
  }

  /** Whether a town's stop signs have been heard. */
  hasStops(key: string): boolean {
    return this.stops.has(key);
  }

  /** Whether the lights or stop signs are drawn at this zoom (and so worth asking for). */
  showsSignals(): boolean {
    return (this.layers.signals || this.layers.stops) && this.cam.scale >= SIGNALS_FROM;
  }

  /** Each stop sign: a small red octagon on the kerb by the line (paler for an all-way stop). */
  private drawStops(ctx: CanvasRenderingContext2D, view: { x0: number; y0: number; x1: number; y1: number }, px: number): void {
    if (!this.layers.stops || this.cam.scale < SIGNALS_FROM) return;
    const r = Math.max(0.3, 2.5 * px);
    for (const allWay of [0, 1]) {
      ctx.fillStyle = allWay ? ALL_WAY : STOP_SIGN;
      ctx.beginPath();
      for (const [key, stops] of this.stops) {
        const room = this.roomOf(key);
        if (!room) continue;
        for (const [sx, sy, a] of stops) {
          if (a !== allWay) continue;
          const x = room.meta.ox + sx, y = room.meta.oy + sy;
          if (x < view.x0 || x > view.x1 || y < view.y0 || y > view.y1) continue;
          for (let k = 0; k < 8; k++) {
            const t = ((k + 0.5) * Math.PI) / 4;
            if (k === 0) ctx.moveTo(x + r * Math.cos(t), y + r * Math.sin(t));
            else ctx.lineTo(x + r * Math.cos(t), y + r * Math.sin(t));
          }
          ctx.closePath();
        }
      }
      ctx.fill();
    }
  }

  /** Each traffic light, a small lamp in a dark housing on the kerb by each stop line: green, amber or red. */
  private drawSignals(ctx: CanvasRenderingContext2D, view: { x0: number; y0: number; x1: number; y1: number }, px: number): void {
    if (!this.layers.signals || !this.showsSignals()) return;
    const size = Math.max(0.45, 4 * px);
    const housing = size + Math.max(0.2, 2 * px);
    for (const [key, { s, skew }] of this.signals) {
      const room = this.roomOf(key);
      if (!room) continue;
      const now = Date.now() + skew;
      ctx.fillStyle = '#0b0d12';
      ctx.beginPath();
      for (const light of s.lights)
        for (const [lx, ly] of light.lamps) {
          const x = room.meta.ox + lx, y = room.meta.oy + ly;
          if (x < view.x0 || x > view.x1 || y < view.y0 || y > view.y1) continue;
          ctx.rect(x - housing / 2, y - housing / 2, housing, housing);
        }
      ctx.fill();
      for (const color of ['green', 'amber', 'red'] as const) {
        ctx.fillStyle = LAMPS[color];
        ctx.beginPath();
        for (const light of s.lights)
          for (const [lx, ly, stage] of light.lamps) {
            const x = room.meta.ox + lx, y = room.meta.oy + ly;
            if (x < view.x0 || x > view.x1 || y < view.y0 || y > view.y1) continue;
            if (lampColor(light, stage, now, s.stageMs) !== color) continue;
            ctx.moveTo(x + size / 2, y);
            ctx.arc(x, y, size / 2, 0, Math.PI * 2);
          }
        ctx.fill();
      }
    }
  }

  /** The room a town's in, in the feed (null: not open). */
  private roomOf(key: string) {
    for (const r of this.world.order) if (r.meta.key === key) return r;
    return null;
  }

  /** Where the focus is now (county frame), or null. */
  focusAt(): { x: number; y: number; live: boolean } | null {
    const f = this.focused;
    if (!f) return null;
    const r = this.roomOf(f.town);
    return focusPoint(f, r ? { ox: r.meta.ox, oy: r.meta.oy, cars: r.cars, people: r.people } : null);
  }

  private fitCanvas(): void {
    const r = this.canvas.getBoundingClientRect();
    this.dpr = Math.min(2, window.devicePixelRatio || 1);
    this.width = r.width;
    this.height = r.height;
    for (const c of [this.canvas, this.still, this.trail]) {
      c.width = Math.max(1, Math.round(r.width * this.dpr));
      c.height = Math.max(1, Math.round(r.height * this.dpr));
    }
    this.stillKey = '';
  }

  // --- Input -------------------------------------------------------------

  private toWorld(px: number, py: number): { x: number; y: number } {
    return { x: this.cam.cx + (px - this.width / 2) / this.cam.scale, y: this.cam.cy + (py - this.height / 2) / this.cam.scale };
  }

  private local(e: PointerEvent | WheelEvent | MouseEvent): { x: number; y: number } {
    const r = this.canvas.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  }

  private zoomAt(px: number, py: number, scale: number): void {
    const before = this.toWorld(px, py);
    this.cam.scale = clamp(scale, MIN_SCALE, MAX_SCALE);
    const after = this.toWorld(px, py);
    this.cam.cx += before.x - after.x;
    this.cam.cy += before.y - after.y;
  }

  private readonly wheel = (e: WheelEvent) => {
    e.preventDefault();
    const p = this.local(e);
    const lines = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? 400 : 1;
    this.zoomAt(p.x, p.y, this.cam.scale * Math.exp(-e.deltaY * lines * 0.0015));
  };

  /** Right-click: the menu for that spot (its place on screen, and in the county's tiles). */
  private readonly context = (e: MouseEvent) => {
    e.preventDefault();
    const p = this.local(e);
    this.onContext({ clientX: e.clientX, clientY: e.clientY, ...this.toWorld(p.x, p.y) });
  };

  private readonly dbl = (e: MouseEvent) => {
    const p = this.local(e);
    this.zoomAt(p.x, p.y, this.cam.scale * 2);
  };

  private readonly down = (e: PointerEvent) => {
    // The right button is the menu's (contextmenu), never a drag or a click.
    if (e.button === 2) return;
    this.canvas.setPointerCapture(e.pointerId);
    this.pointers.set(e.pointerId, this.local(e));
    this.dragged = false;
    if (this.pointers.size === 2) {
      const [a, b] = [...this.pointers.values()];
      this.pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), scale: this.cam.scale };
    }
  };

  private readonly move = (e: PointerEvent) => {
    const p = this.local(e);
    const was = this.pointers.get(e.pointerId);
    if (!was) {
      this.hover(p.x, p.y);
      return;
    }
    this.pointers.set(e.pointerId, p);
    if (this.pointers.size === 2 && this.pinch) {
      const [a, b] = [...this.pointers.values()];
      const d = Math.hypot(a.x - b.x, a.y - b.y);
      this.zoomAt((a.x + b.x) / 2, (a.y + b.y) / 2, this.pinch.scale * (d / Math.max(1, this.pinch.d)));
      this.dragged = true;
      return;
    }
    const dx = p.x - was.x;
    const dy = p.y - was.y;
    if (!this.dragged && Math.hypot(dx, dy) < 3) {
      this.pointers.set(e.pointerId, was);
      return;
    }
    this.dragged = true;
    if (this.following) {
      this.following = null;
      this.onFollowEnd();
    }
    if (this.followingFocus) {
      this.followingFocus = false;
      this.onFocusFollowEnd();
    }
    this.cam.cx -= dx / this.cam.scale;
    this.cam.cy -= dy / this.cam.scale;
  };

  private readonly up = (e: PointerEvent) => {
    if (e.button === 2) return;
    const p = this.local(e);
    this.pointers.delete(e.pointerId);
    if (this.pointers.size < 2) this.pinch = null;
    if (!this.dragged && e.type === 'pointerup') {
      const hit = this.playerAt(p.x, p.y, 14);
      this.selected = hit?.sid ?? null;
      this.onSelect(this.selected);
      if (hit) return;
      // No player there: a car or townsperson to focus on; or else a building with its town's life known, to see who's in it.
      const e = this.entityAt(p.x, p.y, 10);
      if (e) this.onEntity(e);
      else this.onBuilding(this.hoverBuilding);
    }
  };

  private readonly leave = () => {
    if (!this.pointers.size) this.onHover(null);
  };

  /** The car or townsperson drawn nearest the pointer, within `within` pixels (of the layers shown). */
  private entityAt(px: number, py: number, within: number): { key: string; kind: 'car' | 'person'; slot: number } | null {
    const w = this.toWorld(px, py);
    let best = (within / this.cam.scale) ** 2;
    let hit: { key: string; kind: 'car' | 'person'; slot: number } | null = null;
    for (const r of this.world.order) {
      for (const [kind, l, on] of [['car', r.cars, this.layers.cars], ['person', r.people, this.layers.people]] as const) {
        if (!on) continue;
        for (let i = 0; i < l.n; i++) {
          if (!l.shown[i] || this.off(r.meta.key, kind, i)) continue;
          const d = (l.x[i] - w.x) ** 2 + (l.y[i] - w.y) ** 2;
          if (d < best) {
            best = d;
            hit = { key: r.meta.key, kind, slot: i };
          }
        }
      }
    }
    return hit;
  }

  private playerAt(px: number, py: number, within: number): PlayerView | null {
    let best: PlayerView | null = null;
    let bestD = within;
    for (const p of this.world.players()) {
      if (!p.seen) continue;
      const s = this.toScreen(p.x, p.y);
      const d = Math.hypot(s.x - px, s.y - py);
      if (d < bestD) {
        bestD = d;
        best = p;
      }
    }
    return best;
  }

  private hover(px: number, py: number): void {
    const now = performance.now();
    if (now - this.lastHover < 50) return;
    this.lastHover = now;
    const p = this.playerAt(px, py, 12);
    if (p) {
      this.onHover({ x: px, y: py, text: `${p.name} · ${doing(p)} · ${p.town}${p.host ? ' · runs its traffic' : ''}` });
      return;
    }
    const e = this.entityAt(px, py, 8);
    this.onHoverEntity(e ? { ...e, x: px, y: py } : null);
    const w = this.toWorld(px, py);
    const within = 8 / this.cam.scale;
    let best = within * within;
    let text: string | null = null;
    for (const r of this.world.order) {
      if (this.layers.cars) {
        const c = r.cars;
        for (let i = 0; i < c.n; i++) {
          if (!c.shown[i] || this.off(r.meta.key, 'car', i)) continue;
          const d = (c.x[i] - w.x) ** 2 + (c.y[i] - w.y) ** 2;
          if (d < best) {
            best = d;
            text = `Car · ${c.ts[i] < STOPPED ? 'stopped' : `${Math.round(c.ts[i] * KMH)} km/h`} · ${r.meta.name}`;
          }
        }
      }
      if (this.layers.people) {
        const c = r.people;
        for (let i = 0; i < c.n; i++) {
          if (!c.shown[i] || this.off(r.meta.key, 'person', i)) continue;
          const d = (c.x[i] - w.x) ** 2 + (c.y[i] - w.y) ** 2;
          if (d < best) {
            best = d;
            text = `Townsperson · ${c.ts[i] < 0.1 ? 'standing' : 'walking'} · ${r.meta.name}`;
          }
        }
      }
    }
    if (!text) {
      for (const t of this.paths.towns) {
        if (w.x < t.x0 || w.y < t.y0 || w.x > t.x0 + t.width || w.y > t.y0 + t.height) continue;
        const i = this.layers.zoning ? buildingIndexAt(t, w.x - t.x0, w.y - t.y0) : -1;
        const b = i >= 0 ? t.buildings![i] : null;
        const life = this.life.get(t.key);
        const inside = life && i >= 0 ? (indoors(life).get(i) ?? 0) : null;
        text = b ? `Building ${i} · ${describe(b)}${inside === null ? '' : ` · ${inside.toLocaleString()} inside`} · ${t.name}` : t.name;
        this.hoverBuilding = b && life ? { key: t.key, i } : null;
      }
    }
    this.onHover(text ? { x: px, y: py, text } : null);
  }

  // --- Drawing -----------------------------------------------------------

  private toScreen(x: number, y: number): { x: number; y: number } {
    return { x: (x - this.cam.cx) * this.cam.scale + this.width / 2, y: (y - this.cam.cy) * this.cam.scale + this.height / 2 };
  }

  private readonly frame = (now: number) => {
    this.raf = requestAnimationFrame(this.frame);
    this.frameAt(now);
  };

  /** Everything moved on to `now` and drawn. */
  frameAt(now: number): void {
    this.world.step(now);
    if (this.following) {
      const p = this.world.find(this.following);
      if (p && p.seen) {
        this.cam.cx = p.x;
        this.cam.cy = p.y;
      }
    } else if (this.followingFocus) {
      const at = this.focusAt();
      if (at) {
        this.cam.cx = at.x;
        this.cam.cy = at.y;
      }
    }
    this.draw();
    this.fpsCount++;
    if (now - this.fpsAt >= 1000) {
      this.fps = Math.round((this.fpsCount * 1000) / (now - this.fpsAt));
      this.fpsCount = 0;
      this.fpsAt = now;
    }
  }

  /** The still parts, drawn again only when the view or what's shown changes. */
  private drawStill(): void {
    const key = `${this.cam.cx},${this.cam.cy},${this.cam.scale},${this.width},${this.height},${this.layers.roads},${this.layers.zoning},${this.layers.names},${this.lifeVersion}`;
    if (key === this.stillKey) return;
    this.stillKey = key;
    const ctx = this.still.getContext('2d', { alpha: false })!;
    const { scale } = this.cam;
    const px = 1 / scale;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.fillStyle = '#0b0f14';
    ctx.fillRect(0, 0, this.still.width, this.still.height);
    this.toCounty(ctx);
    // The towns' ground.
    for (const t of this.paths.towns) {
      ctx.fillStyle = t.private ? '#141b1f' : '#121a22';
      ctx.fillRect(t.x0, t.y0, t.width, t.height);
      ctx.strokeStyle = t.private ? '#2f4a3a' : '#22303d';
      ctx.lineWidth = px;
      ctx.strokeRect(t.x0, t.y0, t.width, t.height);
    }
    ctx.setLineDash([4 * px, 4 * px]);
    ctx.strokeStyle = '#26323c';
    ctx.stroke(this.paths.spaces);
    ctx.setLineDash([]);
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    // The highways.
    ctx.strokeStyle = '#3a4654';
    ctx.lineWidth = Math.max(2.4, 1.6 * px);
    ctx.stroke(this.paths.highways);
    // What the buildings are for, under the streets.
    if (this.layers.zoning) {
      ctx.globalAlpha = 0.5;
      this.paths.zones.forEach((path, z) => {
        ctx.fillStyle = ZONES[z].color;
        ctx.fill(path);
      });
      ctx.globalAlpha = 1;
      // Who's indoors: a disc on each building, its area as many as are inside.
      if (this.life.size) {
        ctx.fillStyle = INDOORS;
        ctx.beginPath();
        for (const t of this.paths.towns) {
          const life = this.life.get(t.key);
          if (!life || !t.buildings) continue;
          for (const [i, n] of life.inside) {
            const b = t.buildings[i];
            if (!b) continue;
            const r = indoorRadius(n);
            const cx = t.x0 + (b[1] + b[3]) / 2, cy = t.y0 + (b[2] + b[4]) / 2;
            ctx.moveTo(cx + r, cy);
            ctx.arc(cx, cy, r, 0, 2 * Math.PI);
          }
        }
        ctx.globalAlpha = 0.55;
        ctx.fill();
        ctx.globalAlpha = 1;
      }
    }
    // The streets.
    if (this.layers.roads) {
      for (const s of this.paths.streets) {
        ctx.strokeStyle = s.kind === 0 ? '#3a4654' : s.kind === 1 ? '#2a3440' : '#1f2832';
        ctx.lineWidth = Math.max(s.width, (s.kind === 2 ? 0.6 : 0.9) * px);
        ctx.stroke(s.path);
      }
    }
    // The streets' names, close in: along each street's longest stretch, where the name fits, never over another.
    if (this.layers.names && this.layers.roads && scale >= STREET_NAMES) this.drawStreetNames(ctx);
    // The towns' names.
    if (this.layers.names) {
      ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
      ctx.font = '600 12px system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'bottom';
      for (const t of this.paths.towns) {
        const s = this.toScreen(t.x0 + t.width / 2, t.y0);
        ctx.fillStyle = '#8193a6';
        ctx.fillText(t.name, s.x, s.y - 4);
      }
    }
  }

  /** Each street's name along its longest stretch, in screen pixels: main roads first, none over another. */
  private drawStreetNames(ctx: CanvasRenderingContext2D): void {
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.lineJoin = 'round';
    const taken: { x0: number; y0: number; x1: number; y1: number }[] = [];
    for (const l of this.paths.labels) {
      const s = this.toScreen(l.x, l.y);
      if (s.x < -100 || s.y < -20 || s.x > this.width + 100 || s.y > this.height + 20) continue;
      ctx.font = l.kind === 0 ? '600 12px system-ui, sans-serif' : '500 11px system-ui, sans-serif';
      const w = ctx.measureText(l.name).width;
      if (w + 8 > l.length * this.cam.scale) continue;
      // Its box on screen (axis-aligned, enough to keep names apart).
      const hw = (Math.abs(Math.cos(l.angle)) * w + Math.abs(Math.sin(l.angle)) * 12) / 2 + 2;
      const hh = (Math.abs(Math.sin(l.angle)) * w + Math.abs(Math.cos(l.angle)) * 12) / 2 + 2;
      const box = { x0: s.x - hw, y0: s.y - hh, x1: s.x + hw, y1: s.y + hh };
      if (taken.some((b) => b.x0 < box.x1 && box.x0 < b.x1 && b.y0 < box.y1 && box.y0 < b.y1)) continue;
      taken.push(box);
      ctx.save();
      ctx.translate(s.x, s.y);
      ctx.rotate(l.angle);
      ctx.lineWidth = 3;
      ctx.strokeStyle = '#0b0f14';
      ctx.strokeText(l.name, 0, 0);
      ctx.fillStyle = l.kind === 0 ? '#c9d4df' : '#9fb0c0';
      ctx.fillText(l.name, 0, 0);
      ctx.restore();
    }
  }

  /** The county's frame (tiles) on this canvas. */
  private toCounty(ctx: CanvasRenderingContext2D): void {
    const s = this.cam.scale * this.dpr;
    ctx.setTransform(s, 0, 0, s, (this.width / 2 - this.cam.cx * this.cam.scale) * this.dpr, (this.height / 2 - this.cam.cy * this.cam.scale) * this.dpr);
  }

  private draw(): void {
    this.drawStill();
    const ctx = this.ctx;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.drawImage(this.still, 0, 0);
    const { scale } = this.cam;
    const px = 1 / scale;
    // What's in view (tiles), with a margin.
    const m = 4 + 20 * px;
    const view = { x0: this.cam.cx - this.width / 2 / scale - m, y0: this.cam.cy - this.height / 2 / scale - m, x1: this.cam.cx + this.width / 2 / scale + m, y1: this.cam.cy + this.height / 2 / scale + m };

    const dim = this.layers.reach && this.world.players().length > 0;
    if (scale < SHAPED && (this.layers.cars || this.layers.people)) {
      // Further out, each car and person is a few pixels: set straight into a buffer, drawn in one go.
      const dots = this.plotDots(dim);
      if (this.layers.trails) this.trailDots(dots);
      else if (this.trailKey) this.clearTrails();
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.drawImage(dots, 0, 0);
      this.toCounty(ctx);
    } else {
      if (this.layers.trails) this.drawTrails(view);
      else if (this.trailKey) this.clearTrails();
      this.toCounty(ctx);
      if (this.layers.people) this.drawPeople(ctx, view, px, dim);
      if (this.layers.cars) this.drawCars(ctx, view, px, dim);
    }
    this.drawCongestion(ctx, px);
    this.drawStops(ctx, view, px);
    this.drawSignals(ctx, view, px);
    this.drawFocus(ctx, px);
    if (this.layers.players) this.drawPlayers(ctx, view, px);
  }

  /** The congestion slider's streets: red over the road, wider the more they held cars up; each numbered as listed. */
  private drawCongestion(ctx: CanvasRenderingContext2D, px: number): void {
    const c = this.congested;
    if (!c) return;
    const t = this.map.towns.find((x) => x.key === c.key);
    if (!t) return;
    const worst = c.streets[0].hold;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = CONGESTED;
    ctx.globalAlpha = 0.8;
    for (const s of c.streets) {
      if (s.points.length < 4) continue;
      ctx.lineWidth = Math.max(0.3, congestionWidth(s.hold, worst) * 2 * px);
      ctx.beginPath();
      ctx.moveTo(t.x0 + s.points[0], t.y0 + s.points[1]);
      for (let i = 2; i + 1 < s.points.length; i += 2) ctx.lineTo(t.x0 + s.points[i], t.y0 + s.points[i + 1]);
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
    const r = Math.max(0.6, 7 * px);
    ctx.font = `bold ${Math.max(0.8, 9 * px)}px system-ui, sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    c.streets.forEach((s, n) => {
      const m = Math.floor(s.points.length / 4) * 2;
      const x = t.x0 + s.points[m], y = t.y0 + s.points[m + 1];
      ctx.fillStyle = CONGESTED;
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = '#ffffff';
      ctx.fillText(String(n + 1), x, y);
    });
  }

  /** The focus: its way ahead, where it's going, and a ring round it where it is now. */
  private drawFocus(ctx: CanvasRenderingContext2D, px: number): void {
    const f = this.focused;
    const at = this.focusAt();
    if (!f || !at) return;
    const r = this.roomOf(f.town);
    const ox = r ? r.meta.ox : 0, oy = r ? r.meta.oy : 0;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    if (f.path.length > 1) {
      // From where it's drawn now along the way the simulation gave (skipping what's already behind it).
      let from = 0, best = Infinity;
      for (let i = 0; i < Math.min(f.path.length, 40); i++) {
        const d = (f.path[i][0] + ox - at.x) ** 2 + (f.path[i][1] + oy - at.y) ** 2;
        if (d < best) {
          best = d;
          from = i;
        }
      }
      ctx.strokeStyle = FOCUS;
      ctx.globalAlpha = 0.85;
      ctx.lineWidth = Math.max(0.25, 3 * px);
      ctx.setLineDash([6 * px, 4 * px]);
      ctx.beginPath();
      ctx.moveTo(at.x, at.y);
      for (let i = from + 1; i < f.path.length; i++) ctx.lineTo(f.path[i][0] + ox, f.path[i][1] + oy);
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.globalAlpha = 1;
    }
    if (f.dest) {
      const x = f.dest[0] + ox, y = f.dest[1] + oy, s = Math.max(0.5, 6 * px);
      ctx.strokeStyle = FOCUS;
      ctx.lineWidth = Math.max(0.2, 2.5 * px);
      ctx.beginPath();
      ctx.moveTo(x - s, y - s);
      ctx.lineTo(x + s, y + s);
      ctx.moveTo(x + s, y - s);
      ctx.lineTo(x - s, y + s);
      ctx.stroke();
    }
    const R = Math.max(1.2, 10 * px);
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 2.5 * px;
    ctx.setLineDash(at.live ? [] : [3 * px, 3 * px]);
    ctx.beginPath();
    ctx.arc(at.x, at.y, R, 0, Math.PI * 2);
    ctx.stroke();
    ctx.strokeStyle = FOCUS;
    ctx.lineWidth = 1.5 * px;
    ctx.beginPath();
    ctx.arc(at.x, at.y, R + 3 * px, 0, Math.PI * 2);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  private readonly dots = document.createElement('canvas');
  private dotsImage: ImageData | null = null;
  private dotsPx = new Uint32Array(0);
  /** The part of the dots buffer drawn on last frame (device pixels), to clear. */
  private dotsBox = { x0: 0, y0: 0, x1: 0, y1: 0 };

  /** Every car and person in view as a few pixels in a buffer: no path, one image. Those near a player brighter, if `dim`. */
  private plotDots(dim: boolean): HTMLCanvasElement {
    const W = this.still.width;
    const H = this.still.height;
    if (!this.dotsImage || this.dotsImage.width !== W || this.dotsImage.height !== H) {
      this.dotsImage = new ImageData(W, H);
      this.dotsPx = new Uint32Array(this.dotsImage.data.buffer);
      this.dots.width = W;
      this.dots.height = H;
      this.dotsBox = { x0: 0, y0: 0, x1: W, y1: H };
    }
    const px = this.dotsPx;
    const was = this.dotsBox;
    for (let y = was.y0; y < was.y1; y++) px.fill(0, y * W + was.x0, y * W + was.x1);
    const box = { x0: W, y0: H, x1: 0, y1: 0 };
    const k = this.cam.scale * this.dpr;
    const ox = W / 2 - this.cam.cx * k;
    const oy = H / 2 - this.cam.cy * k;
    const size = (tiles: number, css: number) => Math.max(1, Math.round(Math.max(tiles * this.cam.scale, css) * this.dpr));
    const plot = (key: string, kind: 'car' | 'person', l: Layer, n: number, s: number, near: number, color: (i: number) => number) => {
      const half = s >> 1;
      for (let i = 0; i < n; i++) {
        if (!l.shown[i] || (near >= 0 && l.near[i] !== near) || this.off(key, kind, i)) continue;
        const X = ((l.x[i] * k + ox) | 0) - half;
        const Y = ((l.y[i] * k + oy) | 0) - half;
        if (X < 0 || Y < 0 || X + s > W || Y + s > H) continue;
        const c = color(i);
        for (let dy = 0; dy < s; dy++) {
          const row = (Y + dy) * W + X;
          for (let dx = 0; dx < s; dx++) px[row + dx] = c;
        }
        if (X < box.x0) box.x0 = X;
        if (Y < box.y0) box.y0 = Y;
        if (X + s > box.x1) box.x1 = X + s;
        if (Y + s > box.y1) box.y1 = Y + s;
      }
    };
    const sp = size(0.45, 2);
    const sc = size(0.9, 3);
    for (const pass of dim ? [0, 1] : [-1]) {
      const bright = pass !== 0;
      for (const room of this.world.order) {
        if (this.layers.people) {
          const c = bright ? PERSON_PX : PERSON_DIM_PX;
          plot(room.meta.key, 'person', room.people, room.people.n, sp, pass, () => c);
        }
        if (this.layers.cars) {
          const ts = room.cars.ts;
          const colors = bright ? SPEED_PX : SPEED_DIM_PX;
          plot(room.meta.key, 'car', room.cars, room.cars.n, sc, pass, (i) => colors[speedBand(ts[i])]);
        }
      }
    }
    // What changed: last frame's dots and this one's.
    const x0 = Math.min(was.x0, box.x0), y0 = Math.min(was.y0, box.y0);
    const x1 = Math.max(was.x1, box.x1), y1 = Math.max(was.y1, box.y1);
    if (x1 > x0 && y1 > y0) this.dots.getContext('2d')!.putImageData(this.dotsImage, 0, 0, x0, y0, x1 - x0, y1 - y0);
    this.dotsBox = box.x1 > box.x0 ? box : { x0: 0, y0: 0, x1: 0, y1: 0 };
    return this.dots;
  }

  /** Trails, further out: the dots laid over what's faded of the last frames. */
  private trailDots(dots: HTMLCanvasElement): void {
    const key = `${this.cam.cx},${this.cam.cy},${this.cam.scale},${this.width},${this.height}`;
    if (key !== this.trailKey) this.clearTrails();
    this.trailKey = key;
    const t = this.trail.getContext('2d')!;
    t.setTransform(1, 0, 0, 1, 0, 0);
    t.globalCompositeOperation = 'destination-out';
    t.fillStyle = 'rgba(0,0,0,0.035)';
    t.fillRect(0, 0, this.trail.width, this.trail.height);
    t.globalCompositeOperation = 'source-over';
    t.globalAlpha = 0.4;
    t.drawImage(dots, 0, 0);
    t.globalAlpha = 1;
    this.ctx.setTransform(1, 0, 0, 1, 0, 0);
    this.ctx.drawImage(this.trail, 0, 0);
  }

  private trailKey = '';

  private clearTrails(): void {
    this.trailKey = '';
    const t = this.trail.getContext('2d')!;
    t.setTransform(1, 0, 0, 1, 0, 0);
    t.clearRect(0, 0, this.trail.width, this.trail.height);
  }

  /** Where things have been lately: each frame the old fades a little and the new is added. */
  private drawTrails(view: { x0: number; y0: number; x1: number; y1: number }): void {
    const key = `${this.cam.cx},${this.cam.cy},${this.cam.scale},${this.width},${this.height}`;
    if (key !== this.trailKey) this.clearTrails();
    this.trailKey = key;
    const t = this.trail.getContext('2d')!;
    t.setTransform(1, 0, 0, 1, 0, 0);
    t.globalCompositeOperation = 'destination-out';
    t.fillStyle = 'rgba(0,0,0,0.035)';
    t.fillRect(0, 0, this.trail.width, this.trail.height);
    t.globalCompositeOperation = 'source-over';
    this.toCounty(t);
    const r = Math.max(0.35, 1.2 / this.cam.scale);
    const dot = (key: string, kind: 'car' | 'person', l: Layer, color: string) => {
      t.fillStyle = color;
      t.beginPath();
      for (let i = 0; i < l.n; i++) {
        if (!l.shown[i] || this.off(key, kind, i)) continue;
        const x = l.x[i];
        const y = l.y[i];
        if (x < view.x0 || x > view.x1 || y < view.y0 || y > view.y1) continue;
        t.rect(x - r / 2, y - r / 2, r, r);
      }
      t.fill();
    };
    for (const room of this.world.order) {
      if (this.layers.cars) dot(room.meta.key, 'car', room.cars, 'rgba(56,189,248,0.5)');
      if (this.layers.people) dot(room.meta.key, 'person', room.people, 'rgba(233,228,255,0.35)');
    }
    this.ctx.setTransform(1, 0, 0, 1, 0, 0);
    this.ctx.drawImage(this.trail, 0, 0);
  }

  private drawPeople(ctx: CanvasRenderingContext2D, view: { x0: number; y0: number; x1: number; y1: number }, px: number, dim: boolean): void {
    const r = Math.max(0.45, 2 * px);
    for (const pass of dim ? [0, 1] : [1]) {
      ctx.fillStyle = PERSON;
      ctx.globalAlpha = pass === 0 ? 0.35 : 0.9;
      ctx.beginPath();
      for (const room of this.world.order) {
        const l = room.people;
        for (let i = 0; i < l.n; i++) {
          if (dim && (l.near[i] === 1) !== (pass === 1)) continue;
          if (this.off(room.meta.key, 'person', i)) continue;
          const x = l.x[i];
          const y = l.y[i];
          if (x < view.x0 || x > view.x1 || y < view.y0 || y > view.y1) continue;
          ctx.rect(x - r / 2, y - r / 2, r, r);
        }
      }
      ctx.fill();
    }
    ctx.globalAlpha = 1;
  }

  private drawCars(ctx: CanvasRenderingContext2D, view: { x0: number; y0: number; x1: number; y1: number }, px: number, dim: boolean): void {
    // Close in, each car its true shape and heading; further out, a dot.
    const shaped = this.cam.scale >= SHAPED;
    const L = 1.7, W = 0.85;
    const r = Math.max(0.9, 3 * px);
    for (const pass of dim ? [0, 1] : [1]) {
      ctx.globalAlpha = pass === 0 ? 0.45 : 1;
      for (let b = 0; b < SPEEDS.length; b++) {
        const lo = b === 0 ? -1 : SPEEDS[b - 1].under;
        const hi = SPEEDS[b].under;
        ctx.fillStyle = SPEEDS[b].color;
        ctx.beginPath();
        for (const room of this.world.order) {
          const l = room.cars;
          for (let i = 0; i < l.n; i++) {
            const s = l.ts[i];
            if (s < lo || s >= hi) continue;
            if (dim && (l.near[i] === 1) !== (pass === 1)) continue;
            if (this.off(room.meta.key, 'car', i)) continue;
            const x = l.x[i];
            const y = l.y[i];
            if (x < view.x0 || x > view.x1 || y < view.y0 || y > view.y1) continue;
            if (!shaped) {
              ctx.rect(x - r / 2, y - r / 2, r, r);
              continue;
            }
            const c = Math.cos(l.h[i]);
            const n = Math.sin(l.h[i]);
            const ax = (c * L) / 2, ay = (n * L) / 2;
            const bx = (-n * W) / 2, by = (c * W) / 2;
            ctx.moveTo(x + ax + bx, y + ay + by);
            ctx.lineTo(x + ax - bx, y + ay - by);
            ctx.lineTo(x - ax - bx, y - ay - by);
            ctx.lineTo(x - ax + bx, y - ay + by);
            ctx.closePath();
          }
        }
        ctx.fill();
      }
    }
    ctx.globalAlpha = 1;
  }

  private drawPlayers(ctx: CanvasRenderingContext2D, view: { x0: number; y0: number; x1: number; y1: number }, px: number): void {
    const players = this.world.players().filter((p) => p.seen && p.x >= view.x0 - REACH && p.x <= view.x1 + REACH && p.y >= view.y0 - REACH && p.y <= view.y1 + REACH);
    // Their reach, and who's within it of whom.
    if (this.layers.reach) {
      ctx.lineWidth = px;
      ctx.setLineDash([3 * px, 3 * px]);
      for (const p of players) {
        ctx.strokeStyle = p.color;
        ctx.globalAlpha = 0.45;
        ctx.beginPath();
        ctx.arc(p.x, p.y, REACH, 0, Math.PI * 2);
        ctx.stroke();
      }
      ctx.setLineDash([]);
      ctx.globalAlpha = 0.5;
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = 1.2 * px;
      ctx.beginPath();
      for (const p of players) {
        for (const q of p.nearPlayers) {
          if (q.sid < p.sid || q.ride === p || p.ride === q) continue;
          ctx.moveTo(p.x, p.y);
          ctx.lineTo(q.x, q.y);
        }
      }
      ctx.stroke();
      ctx.globalAlpha = 1;
    }
    // Riding with someone: tied to them.
    ctx.lineWidth = 2.5 * px;
    ctx.strokeStyle = '#ffd166';
    ctx.beginPath();
    for (const p of players) {
      if (!p.ride || !p.ride.seen) continue;
      ctx.moveTo(p.x, p.y);
      ctx.lineTo(p.ride.x, p.ride.y);
    }
    ctx.stroke();
    // Each player: a dot their colour (a square at the wheel), its heading, a ring if it runs its town's traffic.
    const R = 5.5 * px;
    for (const p of players) {
      const driving = p.car > 0;
      ctx.fillStyle = p.color;
      ctx.strokeStyle = '#05080b';
      ctx.lineWidth = 1.5 * px;
      ctx.beginPath();
      if (driving) ctx.rect(p.x - R, p.y - R, R * 2, R * 2);
      else ctx.arc(p.x, p.y, R, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
      ctx.strokeStyle = p.color;
      ctx.lineWidth = 2 * px;
      ctx.beginPath();
      ctx.moveTo(p.x, p.y);
      ctx.lineTo(p.x + Math.sin(p.yaw) * R * 2.2, p.y + Math.cos(p.yaw) * R * 2.2);
      ctx.stroke();
      if (p.host) {
        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = px;
        ctx.beginPath();
        ctx.arc(p.x, p.y, R + 3 * px, 0, Math.PI * 2);
        ctx.stroke();
      }
      if (p.sid === this.selected) {
        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = 2 * px;
        ctx.beginPath();
        ctx.arc(p.x, p.y, R + 7 * px, 0, Math.PI * 2);
        ctx.stroke();
      }
    }
    if (this.layers.names) {
      ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
      ctx.font = '600 12px system-ui, sans-serif';
      ctx.textAlign = 'left';
      ctx.textBaseline = 'middle';
      ctx.lineWidth = 3;
      ctx.strokeStyle = '#05080b';
      for (const p of players) {
        const s = this.toScreen(p.x, p.y);
        ctx.strokeText(p.name, s.x + 10, s.y);
        ctx.fillStyle = '#ffffff';
        ctx.fillText(p.name, s.x + 10, s.y);
      }
    }
  }
}

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

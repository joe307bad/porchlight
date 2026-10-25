// Ground-floor rooms: every building piece (a house, or one unit of a row) has
// one room behind its street front, with walls you can't walk through, a door
// in the middle of the front wall, and furniture picked for the kind of
// building: a kitchen, sofa and table in homes; a counter and shelves in
// shops; desks in offices; workbenches and boxes in workshops; benches in
// halls and the church. Pure (no Three, no DOM), so it's unit-tested; the
// world draws what it plans.
//
// Room-local frame: u runs along the street front (0..width), v runs from
// the front wall (0) to the back wall (depth). Furniture models face +Z.

import type { Rng } from '../../core/rng';
import type { Facing, Vec2 } from '../../model/types';
import type { HouseStyle } from './layout';

/** Wall thickness and height, the door gap, and how tall the door is (tiles). */
export const WALL = 0.07;
export const WALL_HEIGHT = 0.8;
export const DOOR_WIDTH = 0.46;
export const DOOR_HEIGHT = 0.62;
/** Furniture-kit models are drawn at this many tiles per model unit (a door is about 1 unit tall). */
export const FURNITURE_SCALE = 0.6;

export interface Room {
  id: number;
  /** The piece's outer rectangle on the map; walls run just inside it. */
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  /** The side the door is on (toward the street). */
  facing: Facing;
  style: HouseStyle;
}

/** An axis-aligned rectangle on the map. */
export interface Rect {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

/** A furniture model's size at scale, before turning: w across its front, d front to back, h tall. */
export interface FurnitureSize {
  w: number;
  d: number;
  h: number;
}

export interface Placement {
  name: string;
  /** Centre of its footprint on the map. */
  x: number;
  y: number;
  /** Height it stands at (above the floor), for things on counters and desks. */
  lift: number;
  /** Turn about the vertical: the model's front (+Z) faces (sin rot, cos rot) on the map. */
  rot: number;
  /** Its footprint on the map. */
  box: Rect;
  /** Rugs lie flat: they don't block walking or other furniture. */
  flat: boolean;
}

export interface RoomPlan {
  furniture: Placement[];
  /** Where someone minds the counter (shops and offices), facing the door. */
  keeper: { x: number; y: number; rot: number } | null;
}

const OUTWARD: Record<Facing, Vec2> = { N: { x: 0, y: -1 }, E: { x: 1, y: 0 }, S: { x: 0, y: 1 }, W: { x: -1, y: 0 } };

/** The room's frame: its width along the front, its depth, and local (u, v) to map. */
export function frame(room: Room) {
  const f = OUTWARD[room.facing];
  const alongX = room.facing === 'N' || room.facing === 'S';
  const width = alongX ? room.x1 - room.x0 : room.y1 - room.y0;
  const depth = alongX ? room.y1 - room.y0 : room.x1 - room.x0;
  // u runs along the front; the front wall's middle is u = width / 2, v = 0.
  const a = { x: -f.y, y: f.x };
  const frontMid = { x: (room.x0 + room.x1) / 2 + (f.x * (room.x1 - room.x0)) / 2, y: (room.y0 + room.y1) / 2 + (f.y * (room.y1 - room.y0)) / 2 };
  const toMap = (u: number, v: number): Vec2 => ({ x: frontMid.x + a.x * (u - width / 2) - f.x * v, y: frontMid.y + a.y * (u - width / 2) - f.y * v });
  /** A direction in the room's frame ((du, dv): +v is into the room) as a map rotation for a model facing +Z. */
  const rotFor = (du: number, dv: number) => {
    const d = { x: a.x * du - f.x * dv, y: a.y * du - f.y * dv };
    return Math.atan2(d.x, d.y);
  };
  return { width, depth, outward: f, along: a, frontMid, toMap, rotFor };
}

/** The door's centre on the front wall's outer face, and the way out. */
export function doorOf(room: Room): { at: Vec2; outward: Vec2; along: Vec2 } {
  const fr = frame(room);
  return { at: fr.frontMid, outward: fr.outward, along: fr.along };
}

/** The room's floor inside its walls (map rectangle). */
export function innerRect(room: Room): Rect {
  return { x0: room.x0 + WALL, y0: room.y0 + WALL, x1: room.x1 - WALL, y1: room.y1 - WALL };
}

export function inRoom(room: Room, p: Vec2): boolean {
  const r = innerRect(room);
  return p.x > r.x0 && p.x < r.x1 && p.y > r.y0 && p.y < r.y1;
}

/** The room's walls as solid rectangles, the front one split round the door. */
export function roomWalls(room: Room): Rect[] {
  const { x0, y0, x1, y1 } = room;
  const t = WALL;
  const walls: Record<Facing, Rect> = {
    N: { x0, y0, x1, y1: y0 + t },
    S: { x0, y0: y1 - t, x1, y1 },
    W: { x0, y0, x1: x0 + t, y1 },
    E: { x0: x1 - t, y0, x1, y1 },
  };
  const out: Rect[] = [];
  for (const side of ['N', 'E', 'S', 'W'] as const) {
    const w = walls[side];
    if (side !== room.facing) {
      out.push(w);
      continue;
    }
    const door = doorOf(room).at;
    const half = DOOR_WIDTH / 2;
    if (side === 'N' || side === 'S') {
      out.push({ ...w, x1: door.x - half }, { ...w, x0: door.x + half });
    } else {
      out.push({ ...w, y1: door.y - half }, { ...w, y0: door.y + half });
    }
  }
  return out;
}

/** A box in room-local (u, v) coordinates. */
interface LocalBox {
  u0: number;
  v0: number;
  u1: number;
  v1: number;
}

type Face = 'front' | 'back' | 'left' | 'right';

/** Room-local direction each face points (front = toward the door). */
const FACE_DIR: Record<Face, [number, number]> = { front: [0, -1], back: [0, 1], left: [-1, 0], right: [1, 0] };

/**
 * Furnish a room: things against the back and side walls, a few pieces in
 * the middle, all clear of the door and each other, and nothing outside the
 * walls. `size` gives each model's footprint at scale.
 */
export function furnishRoom(room: Room, size: (name: string) => FurnitureSize, rng: Rng): RoomPlan {
  const fr = frame(room);
  const W = fr.width;
  const D = fr.depth;
  const placed: { box: LocalBox; flat: boolean }[] = [];
  const out: Placement[] = [];
  const inner: LocalBox = { u0: WALL + 0.02, v0: WALL + 0.02, u1: W - WALL - 0.02, v1: D - WALL - 0.02 };
  // Keep a way in: nothing in front of the door.
  const doorway: LocalBox = { u0: W / 2 - 0.42, v0: 0, u1: W / 2 + 0.42, v1: Math.min(D * 0.45, 0.9) };
  const overlaps = (a: LocalBox, b: LocalBox) => a.u0 < b.u1 && b.u0 < a.u1 && a.v0 < b.v1 && b.v0 < a.v1;

  /** Place `name` with its centre at (u, v), facing `face`; false if it doesn't fit. */
  const put = (name: string, u: number, v: number, face: Face, opts: { lift?: number; flat?: boolean; on?: boolean } = {}): boolean => {
    const s = size(name);
    const sideways = face === 'left' || face === 'right';
    const hu = (sideways ? s.d : s.w) / 2;
    const hv = (sideways ? s.w : s.d) / 2;
    const box: LocalBox = { u0: u - hu, v0: v - hv, u1: u + hu, v1: v + hv };
    if (box.u0 < inner.u0 - 1e-6 || box.v0 < inner.v0 - 1e-6 || box.u1 > inner.u1 + 1e-6 || box.v1 > inner.v1 + 1e-6) return false;
    const flat = opts.flat ?? false;
    // Things that sit on top of something (a screen on a desk) skip the overlap checks.
    if (!flat && !opts.on) {
      if (overlaps(box, doorway)) return false;
      if (placed.some((p) => !p.flat && overlaps(p.box, box))) return false;
    }
    if (!opts.on) placed.push({ box, flat });
    const [du, dv] = FACE_DIR[face];
    const c = fr.toMap(u, v);
    const p0 = fr.toMap(box.u0, box.v0);
    const p1 = fr.toMap(box.u1, box.v1);
    out.push({
      name,
      x: c.x,
      y: c.y,
      lift: opts.lift ?? 0,
      rot: fr.rotFor(du, dv),
      box: { x0: Math.min(p0.x, p1.x), y0: Math.min(p0.y, p1.y), x1: Math.max(p0.x, p1.x), y1: Math.max(p0.y, p1.y) },
      flat,
    });
    return true;
  };

  /**
   * Line things up along a wall from one end, side by side, each with its back
   * to the wall: `wall` is which wall, `from` how far along it to start (0..1).
   */
  const alongWall = (wall: 'back' | 'left' | 'right', names: string[], from = 0, gap = 0.02): number => {
    let count = 0;
    const span = wall === 'back' ? inner.u1 - inner.u0 : inner.v1 - inner.v0;
    let at = (wall === 'back' ? inner.u0 : inner.v0) + span * from;
    for (const name of names) {
      const s = size(name);
      let ok: boolean;
      if (wall === 'back') {
        ok = put(name, at + s.w / 2, inner.v1 - s.d / 2, 'front');
      } else if (wall === 'left') {
        ok = put(name, inner.u0 + s.d / 2, at + s.w / 2, 'right');
      } else {
        ok = put(name, inner.u1 - s.d / 2, at + s.w / 2, 'left');
      }
      if (ok) count++;
      at += s.w + gap;
    }
    return count;
  };

  const pick = <T>(xs: readonly T[]): T => xs[Math.floor(rng.next() * xs.length)];
  const cu = W / 2;
  const cv = D / 2 + 0.15;
  let keeper: RoomPlan['keeper'] = null;

  switch (room.style) {
    case 'shop': {
      // A counter across the room, with someone behind it, shelves behind and along the sides.
      const counterV = Math.min(D - WALL - 0.55, Math.max(0.9, D * 0.55));
      const bar = size('kitchenBar');
      const n = Math.max(1, Math.min(5, Math.floor((W * 0.55) / bar.w)));
      const start = cu - (n * bar.w) / 2 + (rng.chance(0.5) ? 0.25 : -0.25);
      for (let k = 0; k < n; k++) put('kitchenBar', start + (k + 0.5) * bar.w, counterV, 'front');
      put('kitchenBarEnd', start - size('kitchenBarEnd').w / 2, counterV, 'front');
      put('kitchenBarEnd', start + n * bar.w + size('kitchenBarEnd').w / 2, counterV, 'front');
      put(pick(['radio', 'computerScreen']), start + bar.w / 2, counterV - 0.02, 'front', { lift: bar.h, on: true });
      keeper = { ...fr.toMap(start + (n * bar.w) / 2, counterV + bar.d / 2 + 0.16), rot: fr.rotFor(0, -1) };
      alongWall('back', Array.from({ length: 8 }, () => pick(['bookcaseOpen', 'bookcaseClosedWide', 'bookcaseOpen'])), 0.02);
      alongWall('left', [pick(['bookcaseOpen', 'bookcaseClosedWide']), 'cardboardBoxClosed', 'bookcaseOpen'], 0.05);
      alongWall('right', ['bookcaseOpen', pick(['cardboardBoxOpen', 'pottedPlant']), 'bookcaseOpen'], 0.05);
      put('pottedPlant', inner.u0 + 0.15, inner.v0 + 0.15, 'front');
      put('stoolBar', start + bar.w * 0.5, counterV - bar.d / 2 - 0.15, 'back');
      break;
    }
    case 'office': {
      // A reception counter near the door, then rows of desks with screens and chairs.
      keeper = null;
      const bar = size('kitchenBar');
      if (put('kitchenBar', cu - 0.6, 1.0, 'front') && put('kitchenBar', cu - 0.6 - bar.w, 1.0, 'front')) {
        keeper = { ...fr.toMap(cu - 0.6 - bar.w / 2, 1.0 + bar.d / 2 + 0.16), rot: fr.rotFor(0, -1) };
      }
      const desk = size('desk');
      for (let v = 1.6; v < D - WALL - desk.d; v += 0.85) {
        for (let u = inner.u0 + 0.1 + desk.w / 2; u < inner.u1 - desk.w / 2; u += desk.w + 0.35) {
          if (!put('desk', u, v, 'back')) continue;
          put('computerScreen', u, v + 0.04, 'back', { lift: desk.h, on: true });
          put('chairDesk', u, v - desk.d / 2 - 0.12, 'back');
        }
      }
      alongWall('left', ['bookcaseClosedWide', 'pottedPlant'], 0.4);
      alongWall('right', ['pottedPlant', 'bookcaseOpen'], 0.4);
      put('trashcan', inner.u1 - 0.1, inner.v0 + 0.5, 'left');
      break;
    }
    case 'workshop': {
      alongWall('back', Array.from({ length: 6 }, () => pick(['bookcaseOpen', 'cardboardBoxClosed', 'cardboardBoxOpen', 'kitchenCabinetDrawer'])), 0.02);
      alongWall('left', ['table', 'cardboardBoxClosed', 'cardboardBoxOpen'], 0.15);
      alongWall('right', [pick(['bookcaseClosedWide', 'kitchenCabinet']), 'trashcan'], 0.2);
      if (put('table', cu, cv, pick(['front', 'back']))) put('cardboardBoxOpen', cu, cv, 'front', { lift: size('table').h, on: true });
      put('stoolBar', cu + 0.5, cv, 'left');
      break;
    }
    case 'hall':
    case 'church': {
      // A table at the far end facing the door, rows of benches facing it.
      put(room.style === 'church' ? 'table' : 'tableRound', cu, inner.v1 - 0.4, 'front');
      put('pottedPlant', inner.u0 + 0.15, inner.v1 - 0.2, 'front');
      put('pottedPlant', inner.u1 - 0.15, inner.v1 - 0.2, 'front');
      const bench = size('bench');
      for (let v = inner.v1 - 1.1; v > 1.0; v -= bench.d + 0.32) {
        for (const side of [-1, 1]) {
          for (let k = 0; k < 3; k++) put('bench', cu + side * (0.45 + bench.w * (k + 0.5)), v, 'back');
        }
      }
      put('rugRectangle', cu, D * 0.55, 'back', { flat: true });
      break;
    }
    default: {
      // A home: a kitchen run on the back wall, a sofa facing a TV, a table and chairs, plants.
      const kitchen = ['kitchenFridge', 'kitchenStove', 'kitchenSink', 'kitchenCabinet', 'kitchenCabinetDrawer'];
      const left = rng.chance(0.5);
      alongWall('back', left ? kitchen : [...kitchen].reverse(), left ? 0 : 0.45);
      const sofaWall = left ? 'right' : 'left';
      if (!alongWall(sofaWall, ['loungeSofa'], 0.35)) alongWall(sofaWall, ['loungeChair'], 0.35);
      alongWall(sofaWall === 'left' ? 'right' : 'left', [pick(['bookcaseOpen', 'bookcaseClosedWide']), 'cabinetTelevision'], 0.3);
      put(pick(['rugRectangle', 'rugRound']), cu, cv, 'front', { flat: true });
      const table = pick(['table', 'tableRound']);
      if (put(table, cu, cv, 'front')) {
        const t = size(table);
        const chair = pick(['chair', 'chairCushion']);
        put(chair, cu - t.w / 2 - 0.12, cv, 'right');
        put(chair, cu + t.w / 2 + 0.12, cv, 'left');
        put(chair, cu, cv + t.d / 2 + 0.12, 'front');
      } else {
        put('tableCoffee', cu, cv, 'front');
      }
      put('pottedPlant', inner.u0 + 0.14, inner.v0 + 0.14, 'front');
      put('lampRoundFloor', inner.u1 - 0.12, inner.v0 + 0.12, 'front');
      put('coatRackStanding', cu + 0.62, inner.v0 + 0.16, 'front');
      put('plantSmall1', inner.u0 + 0.12, inner.v1 - 0.12, 'front');
    }
  }
  return { furniture: out, keeper };
}

/** Every model name a plan may use. */
export const FURNITURE = [
  'table', 'tableRound', 'tableCoffee', 'chair', 'chairCushion', 'chairDesk', 'desk', 'computerScreen',
  'kitchenBar', 'kitchenBarEnd', 'kitchenCabinet', 'kitchenCabinetDrawer', 'kitchenFridge', 'kitchenStove', 'kitchenSink',
  'loungeSofa', 'loungeChair', 'bookcaseOpen', 'bookcaseClosedWide', 'pottedPlant', 'plantSmall1', 'lampRoundFloor',
  'rugRectangle', 'rugRound', 'cardboardBoxClosed', 'cardboardBoxOpen', 'bench', 'stoolBar', 'trashcan', 'radio',
  'coatRackStanding', 'cabinetTelevision',
] as const;

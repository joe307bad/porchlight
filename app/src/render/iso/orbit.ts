// The explore camera's geometry, pure so it's unit-tested. The camera looks
// down on the player at a fixed iso-style pitch from one of 12 fixed headings
// around them, 30° apart. Stop 1 (45°) is the classic isometric view, with the
// camera off toward +x/+y; stops 4, 7 and 10 are the other three classic
// views, and the stops in between look straight down avenues at 15° off-axis.
//
// World space: x right, y down the map (the blueprint's axes); the camera's
// heading `yaw` is measured from +y toward +x.

import type { Vec2 } from '../../model/types';

export const CAMERA_STOPS = 12;
const STEP = (Math.PI * 2) / CAMERA_STOPS;
const OFFSET = Math.PI / 12; // 15°: the stops straddle the axes, so 45° is one of them
/** Camera pitch above the ground: 30° gives Kenney's 2:1 isometric look. */
export const PITCH = Math.PI / 6;
export const DEFAULT_STOP = 1;

/** Yaw of camera stop k (radians), any integer k. */
export function stopYaw(k: number): number {
  return OFFSET + mod(k, CAMERA_STOPS) * STEP;
}

/** The stop nearest a yaw. */
export function nearestStop(yaw: number): number {
  return mod(Math.round((yaw - OFFSET) / STEP), CAMERA_STOPS);
}

/** The signed turn (radians, in -π..π) that takes `from` to `to` the short way round. */
export function shortestTurn(from: number, to: number): number {
  const d = mod(to - from, Math.PI * 2);
  return d > Math.PI ? d - Math.PI * 2 : d;
}

/** Where the camera sits relative to what it looks at, as a unit vector (x, up, y). */
export function cameraOffset(yaw: number, pitch = PITCH): { x: number; up: number; y: number } {
  return { x: Math.sin(yaw) * Math.cos(pitch), up: Math.sin(pitch), y: Math.cos(yaw) * Math.cos(pitch) };
}

/** The ground directions that read as screen right and screen up from this heading. */
export function screenAxes(yaw: number): { right: Vec2; up: Vec2 } {
  return { right: { x: Math.cos(yaw), y: -Math.sin(yaw) }, up: { x: -Math.sin(yaw), y: -Math.cos(yaw) } };
}

/** A screen-space direction (right, down) as a unit world direction on the ground, or null for none. */
export function screenToWorld(input: Vec2, yaw: number): Vec2 | null {
  const len = Math.hypot(input.x, input.y);
  if (len < 1e-9) return null;
  const { right, up } = screenAxes(yaw);
  const ix = input.x / len;
  const iy = input.y / len;
  return { x: right.x * ix - up.x * iy, y: right.y * ix - up.y * iy };
}

/**
 * The stop whose view looks most nearly along a ground direction: the camera
 * sits behind the walker, so they look down the street they're walking.
 */
export function stopLookingAlong(dir: Vec2): number {
  // Looking along dir means the camera sits at -dir: yaw = atan2(-dir.x, -dir.y).
  return nearestStop(Math.atan2(-dir.x, -dir.y));
}

/**
 * The stop to be at to stay behind a walker heading in `dir`, with some give:
 * keep the current stop until the ideal heading is more than `margin` past
 * the halfway point to the next one, so the camera doesn't flick back and
 * forth on a heading that sits between two stops.
 */
export function stopBehind(current: number, dir: Vec2, margin = Math.PI / 36): number {
  if (Math.hypot(dir.x, dir.y) < 1e-6) return current;
  const ideal = Math.atan2(-dir.x, -dir.y);
  const off = Math.abs(shortestTurn(stopYaw(current), ideal));
  return off > STEP / 2 + margin ? nearestStop(ideal) : current;
}

/**
 * Stops to try, nearest first, when the current one is blocked: 1 and -1 away,
 * then 2 and -2, and so on round to the far side.
 */
export function stopsByDistance(from: number): number[] {
  const out: number[] = [];
  for (let d = 1; d <= CAMERA_STOPS / 2; d++) {
    out.push(mod(from + d, CAMERA_STOPS));
    if (d < CAMERA_STOPS / 2) out.push(mod(from - d, CAMERA_STOPS));
  }
  return out;
}

/** A box standing on the ground: x0..x1, y0..y1 on the map, rising to `top`. */
export interface Box {
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  top: number;
}

/**
 * True if the line from a point (at height h above ground point p) toward a
 * camera in direction `offset` passes through the box.
 */
export function rayHitsBox(p: Vec2, h: number, offset: { x: number; up: number; y: number }, b: Box): boolean {
  let near = 1e-4;
  let far = Infinity;
  const slab = (o: number, d: number, lo: number, hi: number) => {
    if (Math.abs(d) < 1e-12) return o >= lo && o <= hi;
    let t0 = (lo - o) / d;
    let t1 = (hi - o) / d;
    if (t0 > t1) [t0, t1] = [t1, t0];
    near = Math.max(near, t0);
    far = Math.min(far, t1);
    return near <= far;
  };
  return slab(p.x, offset.x, b.x0, b.x1) && slab(p.y, offset.y, b.y0, b.y1) && slab(h, offset.up, 0, b.top);
}

function mod(a: number, n: number): number {
  return ((a % n) + n) % n;
}

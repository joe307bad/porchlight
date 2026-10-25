import { describe, expect, it } from 'vitest';
import {
  CAMERA_STOPS,
  DEFAULT_STOP,
  cameraOffset,
  nearestStop,
  CHASE_TILTS,
  DEFAULT_CHASE_TILT,
  DEFAULT_ISO_TILT,
  ISO_TILTS,
  PITCH,
  chaseYaw,
  clampTilt,
  tiltPitch,
  rayBoxDistance,
  rayHitsBox,
  screenToWorld,
  shortestTurn,
  stopLookingAlong,
  stopBehind,
  stopYaw,
  stopsByDistance,
} from '../src/render/iso/orbit';

const deg = (r: number) => (r * 180) / Math.PI;

describe('camera orbit', () => {
  it('has 12 stops 30° apart, including the four classic iso views', () => {
    expect(CAMERA_STOPS).toBe(12);
    const yaws = Array.from({ length: 12 }, (_, k) => Math.round(deg(stopYaw(k))));
    expect(yaws).toEqual([15, 45, 75, 105, 135, 165, 195, 225, 255, 285, 315, 345]);
    expect(deg(stopYaw(DEFAULT_STOP))).toBeCloseTo(45);
    for (let k = -14; k < 26; k++) expect(nearestStop(stopYaw(k))).toBe(((k % 12) + 12) % 12);
  });

  it('turns the short way round', () => {
    expect(deg(shortestTurn(stopYaw(11), stopYaw(0)))).toBeCloseTo(30);
    expect(deg(shortestTurn(stopYaw(0), stopYaw(11)))).toBeCloseTo(-30);
    expect(Math.abs(deg(shortestTurn(0, Math.PI)))).toBeCloseTo(180);
  });

  it('maps screen directions to the ground like the old 2:1 iso view at the default stop', () => {
    const yaw = stopYaw(DEFAULT_STOP);
    // Screen down walks toward +x+y (toward the camera), screen right toward +x-y.
    const down = screenToWorld({ x: 0, y: 1 }, yaw)!;
    expect(down.x).toBeCloseTo(Math.SQRT1_2);
    expect(down.y).toBeCloseTo(Math.SQRT1_2);
    const right = screenToWorld({ x: 1, y: 0 }, yaw)!;
    expect(right.x).toBeCloseTo(Math.SQRT1_2);
    expect(right.y).toBeCloseTo(-Math.SQRT1_2);
    expect(screenToWorld({ x: 0, y: 0 }, yaw)).toBeNull();
    // The camera sits toward +x+y.
    const o = cameraOffset(yaw);
    expect(o.x).toBeGreaterThan(0);
    expect(o.y).toBeGreaterThan(0);
    expect(o.up).toBeGreaterThan(0);
  });

  it('screen up always walks away from the camera', () => {
    for (let k = 0; k < 12; k++) {
      const yaw = stopYaw(k);
      const up = screenToWorld({ x: 0, y: -1 }, yaw)!;
      const o = cameraOffset(yaw);
      expect(up.x * o.x + up.y * o.y).toBeLessThan(0);
    }
  });

  it('picks the stop behind a walker so they look down the street', () => {
    for (let k = 0; k < 12; k++) {
      const o = cameraOffset(stopYaw(k));
      expect(stopLookingAlong({ x: -o.x, y: -o.y })).toBe(k);
    }
  });

  it('tries nearby stops first, each once', () => {
    const order = stopsByDistance(3);
    expect(order.slice(0, 4)).toEqual([4, 2, 5, 1]);
    expect(new Set(order).size).toBe(11);
    expect(order).not.toContain(3);
  });

  it('finds buildings between the player and the camera', () => {
    const o = cameraOffset(stopYaw(DEFAULT_STOP)); // camera toward +x+y
    const p = { x: 10, y: 10 };
    const inFront = { x0: 11, y0: 11, x1: 13, y1: 13, top: 3 };
    const behind = { x0: 7, y0: 7, x1: 9, y1: 9, top: 3 };
    const low = { x0: 11, y0: 11, x1: 13, y1: 13, top: 0.2 };
    expect(rayHitsBox(p, 0.4, o, inFront)).toBe(true);
    expect(rayHitsBox(p, 0.4, o, behind)).toBe(false);
    expect(rayHitsBox(p, 0.4, o, low)).toBe(false);
    // From the opposite stop, the one behind is now in front.
    const back = cameraOffset(stopYaw(DEFAULT_STOP + 6));
    expect(rayHitsBox(p, 0.4, back, behind)).toBe(true);
    expect(rayHitsBox(p, 0.4, back, inFront)).toBe(false);
  });

  it('stays behind a walker, holding its stop until they have clearly turned', () => {
    const along = (yaw: number) => ({ x: -Math.sin(yaw), y: -Math.cos(yaw) }); // walking away from a camera at yaw
    const at = (k: number, deg: number) => along(stopYaw(k) + (deg * Math.PI) / 180);
    expect(stopBehind(3, at(3, 0))).toBe(3);
    expect(stopBehind(3, at(3, 18))).toBe(3); // just past halfway: hold
    expect(stopBehind(3, at(3, 25))).toBe(4);
    expect(stopBehind(3, at(3, -25))).toBe(2);
    expect(stopBehind(3, at(3, 180))).toBe(9); // turned round: the camera goes round too
    expect(stopBehind(3, { x: 0, y: 0 })).toBe(3);
  });

  it('measures how far a sight line runs before it enters a building', () => {
    const o = cameraOffset(stopYaw(DEFAULT_STOP));
    const b = { x0: 11, y0: 11, x1: 13, y1: 13, top: 3 };
    const d = rayBoxDistance({ x: 10, y: 10 }, 0.4, o, b);
    // It enters through the x = 11 / y = 11 faces, 1 tile away on each axis.
    expect(d).toBeCloseTo(1 / o.x);
    expect(rayBoxDistance({ x: 10, y: 10 }, 0.4, cameraOffset(stopYaw(DEFAULT_STOP + 6)), b)).toBe(Infinity);
    expect(rayBoxDistance({ x: 12, y: 12 }, 0.4, o, b)).toBe(0);
  });

  it('puts the close-up camera straight behind the walker', () => {
    for (const heading of [0, 1, -2, 3]) {
      const o = cameraOffset(chaseYaw(heading));
      const facing = { x: Math.sin(heading), y: Math.cos(heading) };
      expect((o.x * facing.x + o.y * facing.y) / Math.hypot(o.x, o.y)).toBeCloseTo(-1);
      // And screen up walks the way they face.
      const up = screenToWorld({ x: 0, y: -1 }, chaseYaw(heading))!;
      expect(up.x).toBeCloseTo(facing.x);
      expect(up.y).toBeCloseTo(facing.y);
    }
  });

  it('tilts in fixed steps, defaulting to the classic 30° view, and stops at the ends', () => {
    expect(tiltPitch(ISO_TILTS, DEFAULT_ISO_TILT)).toBeCloseTo(PITCH);
    expect(deg(tiltPitch(CHASE_TILTS, DEFAULT_CHASE_TILT))).toBeCloseTo(17);
    expect(clampTilt(ISO_TILTS, -3)).toBe(0);
    expect(clampTilt(ISO_TILTS, 99)).toBe(ISO_TILTS.length - 1);
    for (const tilts of [ISO_TILTS, CHASE_TILTS]) {
      for (let k = 1; k < tilts.length; k++) expect(tiltPitch(tilts, k)).toBeGreaterThan(tiltPitch(tilts, k - 1));
      expect(tiltPitch(tilts, tilts.length - 1)).toBeLessThan(Math.PI / 2);
      expect(tiltPitch(tilts, 0)).toBeGreaterThan(0);
    }
  });
});

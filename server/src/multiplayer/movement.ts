// Checking what clients say about themselves. Movement is client-authoritative
// (the server has no town geometry), so the server only makes sure a move is
// well-formed, in the town, in order, and no faster than a car can go. Pure,
// so it's unit-tested without a room.

import { ANIM_MAX, type MoveMessage } from './protocol.ts';

/**
 * Fastest anything goes, tiles per second: a boosted car (TOP_SPEED 5.2 ×
 * BOOST_SPEED 1.7 in the app's driving.ts). Sprinting on foot is 6.1.
 */
export const MAX_SPEED = 8.9;
/** Allowance over MAX_SPEED for jitter in when moves arrive. */
const SPEED_TOLERANCE = 1.25;
/** Plus a fixed allowance (tiles): stepping out of a car lands you beside it. */
const SLACK = 0.75;
/** Time counted between moves (seconds): at least one send interval, at most a second. */
const MIN_GAP = 0.05;
const MAX_GAP = 1;
/** How high off the ground anything gets (tiles); a jump is about 0.2. */
const MAX_HEIGHT = 3;

export interface Bounds {
  width: number;
  height: number;
}

/** The last accepted state of one player's movement. */
export interface Track {
  /** False until the first move after joining (or reconnecting), which is taken as given. */
  anchored: boolean;
  x: number;
  y: number;
  z: number;
  /** Server time it was accepted (ms). */
  t: number;
  /** Last accepted seq, or null before any. */
  seq: number | null;
  /** Bumped by every correction; moves carrying an older one are ignored. */
  epoch: number;
}

export const newTrack = (): Track => ({ anchored: false, x: 0, y: 0, z: 0, t: 0, seq: null, epoch: 0 });

const isNum = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const isInt = (v: unknown, max: number): v is number => Number.isInteger(v) && (v as number) >= 0 && (v as number) <= max;

/** A move message, or null if anything in it is missing, the wrong type, NaN or Infinity. */
export function parseMove(raw: unknown): MoveMessage | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const m = raw as Record<string, unknown>;
  if (!isInt(m.seq, 0xffff) || !isInt(m.epoch, 0xffff) || !isInt(m.anim, ANIM_MAX) || !isInt(m.car, 255)) return null;
  if (!isInt(m.t, 0xffffffff)) return null;
  if (!isNum(m.x) || !isNum(m.y) || !isNum(m.z) || !isNum(m.yaw)) return null;
  const ride = m.ride ?? '';
  if (typeof ride !== 'string' || ride.length > 32) return null;
  return { seq: m.seq, t: m.t, epoch: m.epoch, x: m.x, y: m.y, z: m.z, yaw: m.yaw, anim: m.anim, car: m.car, ride };
}

/** Is `a` after `b` in a 16-bit sequence that wraps? */
export function seqNewer(a: number, b: number): boolean {
  const d = (a - b) & 0xffff;
  return d !== 0 && d < 0x8000;
}

export const inBounds = (b: Bounds, x: number, y: number, z: number): boolean =>
  x >= 0 && x <= b.width && z >= 0 && z <= b.height && y >= -0.5 && y <= MAX_HEIGHT;

export function clampToBounds(b: Bounds, x: number, z: number): { x: number; z: number } {
  return { x: Math.min(b.width, Math.max(0, x)), z: Math.min(b.height, Math.max(0, z)) };
}

export type Verdict = 'ok' | 'stale' | 'out-of-bounds' | 'too-fast';

/**
 * Judge a move against the last accepted one, timed by when the server got it
 * (never the client's clock). Doesn't change `track`; the room does on 'ok'.
 */
export function judgeMove(track: Track, m: MoveMessage, now: number, bounds: Bounds): Verdict {
  if (m.epoch !== track.epoch) return 'stale';
  if (track.seq !== null && !seqNewer(m.seq, track.seq)) return 'stale';
  if (!inBounds(bounds, m.x, m.y, m.z)) return 'out-of-bounds';
  if (!track.anchored) return 'ok';
  const gap = Math.min(MAX_GAP, Math.max(MIN_GAP, (now - track.t) / 1000));
  const d = Math.hypot(m.x - track.x, m.z - track.z);
  return d <= MAX_SPEED * SPEED_TOLERANCE * gap + SLACK ? 'ok' : 'too-fast';
}

/**
 * Messages per second from one client: over `limit` in a second are dropped;
 * `strikes` seconds over it in a row, or four times it in one, is a kick.
 */
export class RateLimit {
  private windowStart = 0;
  private count = 0;
  private overs = 0;
  private readonly limit: number;
  private readonly strikes: number;

  constructor(limit = 30, strikes = 3) {
    this.limit = limit;
    this.strikes = strikes;
  }

  hit(now: number): 'ok' | 'drop' | 'kick' {
    if (now - this.windowStart >= 1000) {
      this.overs = this.count > this.limit ? this.overs + 1 : 0;
      if (now - this.windowStart >= 2000) this.overs = 0; // a quiet second in between
      this.windowStart = now;
      this.count = 0;
    }
    this.count++;
    if (this.overs >= this.strikes || this.count > this.limit * 4) return 'kick';
    return this.count > this.limit ? 'drop' : 'ok';
  }
}

/** Counts events over the last minute (rejected moves): too many is a kick. */
export class Strikes {
  private times: number[] = [];
  private readonly perMinute: number;

  constructor(perMinute = 10) {
    this.perMinute = perMinute;
  }

  /** Record one; true once there have been too many in the last minute. */
  add(now: number): boolean {
    this.times = this.times.filter((t) => now - t < 60_000);
    this.times.push(now);
    return this.times.length >= this.perMinute;
  }
}

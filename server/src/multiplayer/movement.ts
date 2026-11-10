// Checking what clients say about themselves. Movement is client-authoritative
// (the server has no town geometry), so the server only makes sure a move is
// well-formed, in the town, in order, and no faster than a car can go. Pure,
// so it's unit-tested without a room.

import { ANIM_MAX, type MoveMessage } from './protocol.ts';

/**
 * Fastest anything goes, tiles per second: a boosted car (TOP_SPEED 5.2 ×
 * CAR_BOOST 3.4 = 17.7 in the game's Driving.cs). A boosted plane does 11.1,
 * sprinting on foot is 6.1 and a boat goes no faster than a car.
 */
export const MAX_SPEED = 18;
/** Allowance over MAX_SPEED for jitter in when moves arrive. */
const SPEED_TOLERANCE = 1.25;
/** Plus a fixed allowance (tiles): stepping out of a car lands you beside it. */
const SLACK = 0.75;
/**
 * Distance is budgeted, not judged move by move: the budget fills at MAX_SPEED (with its tolerance) by
 * the server's clock, up to this many seconds' worth, and each move spends what it covers. Moves that
 * arrive in a bunch (a frame that stalled on the client, Wi-Fi catching up) spend what built up while
 * they were held back, so a fast car isn't put back for them; after a stall, the first move through can
 * be a couple of seconds' drive on, and that's fine, it's not a teleport. Over any stretch of time,
 * nobody gets further than MAX_SPEED allows (and a few seconds' head start).
 */
const MAX_GAP = 3;
/**
 * Driving in from the room next door, the client goes on driving all through joining (matchmaking,
 * connecting, the room loading the character), but the room only starts its clock once it's done:
 * this much driving (seconds, at the top speed) is let off the first move after.
 */
export const ARRIVE_GRACE_SECONDS = 4;
/** How high off the ground anything gets (tiles): a jump is about 0.2, a plane climbs to 20. */
const MAX_HEIGHT = 24;

export interface Bounds {
  /** The town's rectangle (from x0, z0: 0 but for the north room, measured from the county's corner). */
  width: number;
  height: number;
  x0?: number;
  z0?: number;
  /**
   * In the county (county.ts): the room's ground besides its rectangle, its half of each highway
   * and the countryside nearest it, for cars on the road and planes over the fields.
   */
  owns?: (x: number, z: number) => boolean;
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
  /** Extra distance (tiles) allowed on the next accepted move only (driving in from next door). */
  grace: number;
  /** Distance (tiles) still to spend as of `t`: see MAX_GAP. Down to -SLACK after a move that used its slack. */
  credit: number;
}

export const newTrack = (): Track => ({ anchored: false, x: 0, y: 0, z: 0, t: 0, seq: null, epoch: 0, grace: 0, credit: 0 });

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

const inRect = (b: Bounds, x: number, z: number): boolean => x >= (b.x0 ?? 0) && x <= (b.x0 ?? 0) + b.width && z >= (b.z0 ?? 0) && z <= (b.z0 ?? 0) + b.height;

export const inBounds = (b: Bounds, x: number, y: number, z: number): boolean =>
  (b.owns ? b.owns(x, z) : inRect(b, x, z)) && y >= -0.5 && y <= MAX_HEIGHT;

/** The spot, if it's the room's; else the nearest point of its rectangle. */
export function clampToBounds(b: Bounds, x: number, z: number): { x: number; z: number } {
  if (b.owns ? b.owns(x, z) : inRect(b, x, z)) return { x, z };
  const x0 = b.x0 ?? 0;
  const z0 = b.z0 ?? 0;
  return { x: Math.min(x0 + b.width, Math.max(x0, x)), z: Math.min(z0 + b.height, Math.max(z0, z)) };
}

export type Verdict = 'ok' | 'stale' | 'out-of-bounds' | 'too-fast';

/**
 * Judge a move against the last accepted one, timed by when the server got it
 * (never the client's clock). Doesn't change `track`; the room does on 'ok'.
 */
export function judgeMove(track: Track, m: MoveMessage, now: number, bounds: Bounds, opts: { riding?: boolean } = {}): Verdict {
  if (m.epoch !== track.epoch) return 'stale';
  if (track.seq !== null && !seqNewer(m.seq, track.seq)) return 'stale';
  if (!inBounds(bounds, m.x, m.y, m.z)) return 'out-of-bounds';
  // A passenger goes where the car goes (wherever they see it, which can jump as its driver's moves
  // come in): they're held to being beside its driver instead (the room's canRide).
  if (!track.anchored || opts.riding) return 'ok';
  const d = Math.hypot(m.x - track.x, m.z - track.z);
  return d <= creditAt(track, now) + SLACK + track.grace ? 'ok' : 'too-fast';
}

/** The distance budget (tiles) at `now`: what was left at the last accepted move, filled since, up to MAX_GAP's worth. */
export function creditAt(track: Track, now: number): number {
  const rate = MAX_SPEED * SPEED_TOLERANCE;
  return Math.min(rate * MAX_GAP, (track.credit ?? 0) + (rate * Math.max(0, now - track.t)) / 1000);
}

/** What's left of the budget once move `m` is accepted at `now` (its slack and any grace are spent first, never saved). */
export function creditAfter(track: Track, m: MoveMessage, now: number): number {
  const d = Math.hypot(m.x - track.x, m.z - track.z);
  const left = creditAt(track, now) + track.grace - d;
  return Math.max(-SLACK, Math.min(creditAt(track, now), left));
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

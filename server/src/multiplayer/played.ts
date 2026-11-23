// What players have really been seen to do, for what the game says it's earned:
// missions started (the game fetches a mission's story each time it starts one)
// and how long and how far an account has played since, and races run right along
// their course, timed here. In memory only. The game's own word isn't enough:
// anyone signed in can send the server whatever they like. See docs/cross-account.md.

import type { Mission } from '../missions.ts';

/** Fastest anyone goes on foot (tiles a second): the game's PLAYER_SPEED × FAST_SPRINT (Actors.cs). */
export const MAX_FOOT = 3.4 * 2.4;
/** A mission in hand must have been moved about in this much (tiles). */
export const LEAST_TILES = 10;
/** A mission without its own `# least:` takes at least this long (seconds). */
export const LEAST_SECONDS = 20;
/** A start is forgotten after this long (ms), and only so many are kept an account. */
const START_MS = 6 * 3600_000;
const MAX_STARTS = 8;
/** A race waits this long for its time to be sent (ms). */
const RACE_MS = 30 * 60_000;
/** The game's clock may be this much shorter than the server's (seconds) and still be the one kept. */
const CLOCK_SLACK = 3;

/** A town's race course, as the game lays it out (Course.cs; asked of the simulation). */
export interface CourseLine {
  /** [x, y, half the road's width] along it. */
  points: [number, number, number][];
}

interface Start {
  mission: string;
  at: number;
  /** The account's travel when it started. */
  tiles: number;
}

export class Played {
  /** The clock (ms), for tests to move on. */
  now: () => number = Date.now;
  private readonly starts = new Map<string, Start[]>();
  private readonly travel = new Map<string, number>();
  /** Each character's last race run to its end. */
  private readonly races = new Map<string, { town: string; seconds: number; at: number; claimed: boolean }>();

  start(account: string, mission: string): void {
    const list = this.live(account);
    list.push({ mission, at: this.now(), tiles: this.travel.get(account) ?? 0 });
    // The oldest go, of this mission first: fetching one over and over mustn't lose another that's being played.
    while (list.length > MAX_STARTS) {
      const same = list.findIndex((s) => s.mission === mission);
      list.splice(same, 1);
    }
    this.starts.set(account, list);
  }

  private live(account: string): Start[] {
    const list = (this.starts.get(account) ?? []).filter((s) => this.now() - s.at < START_MS);
    if (!list.length) this.starts.delete(account);
    return list;
  }

  moved(account: string, tiles: number): void {
    // Only while something's started: otherwise there's nothing to measure (and nothing to keep).
    if (this.starts.has(account)) this.travel.set(account, (this.travel.get(account) ?? 0) + tiles);
  }

  /** Has the account started this mission, long enough ago and with enough done since, to have finished it? `spend`: it's finished now, so that start is used up. */
  couldHaveFinished(account: string, mission: Mission, spend: boolean): boolean {
    const list = this.live(account);
    const tiles = this.travel.get(account) ?? 0;
    const i = list.findIndex((s) => s.mission === mission.id && this.now() - s.at >= mission.least * 1000 && tiles - s.tiles >= LEAST_TILES);
    if (i < 0) return false;
    if (spend) {
      list.splice(i, 1);
      if (list.length) this.starts.set(account, list);
      else {
        this.starts.delete(account);
        this.travel.delete(account);
      }
    }
    return true;
  }

  raced(character: string, town: string, seconds: number): void {
    for (const [id, r] of this.races) if (this.now() - r.at > RACE_MS) this.races.delete(id);
    this.races.set(character, { town, seconds, at: this.now(), claimed: false });
  }

  hasRaced(character: string): boolean {
    const r = this.races.get(character);
    return !!r && this.now() - r.at <= RACE_MS;
  }

  /**
   * The time to keep for a race this character ran in this town, once each; null if none was seen.
   * The game's own clock (it knows the line to the frame), unless it's well short of the server's.
   */
  claimRace(character: string, town: string, said: number): number | null {
    const r = this.races.get(character);
    if (!r || r.claimed || r.town !== town || this.now() - r.at > RACE_MS) return null;
    r.claimed = true;
    return said < r.seconds - CLOCK_SLACK ? Math.round(r.seconds * 100) / 100 : said;
  }
}

/** This process's. */
export const played = new Played();

/** A runner's way along a course, as the server sees their moves (the game's CourseRun, a little more forgiving). */
export class RaceWatch {
  /** How near the start counts as still on it, and how near the end as over it (tiles). */
  static readonly START = 1.5;
  static readonly END = 2;
  private static readonly AHEAD = 4;
  private static readonly BACK = 8;
  private static readonly EDGE = 2.2;
  private readonly points: CourseLine['points'];
  private readonly at: number[] = [0];
  private progress = -1;
  private startedAt = 0;
  private last: { x: number; y: number; t: number } | null = null;

  constructor(course: CourseLine) {
    this.points = course.points;
    for (let k = 1; k < this.points.length; k++) {
      const [a, b] = [this.points[k - 1], this.points[k]];
      this.at.push(this.at[k - 1] + Math.hypot(b[0] - a[0], b[1] - a[1]));
    }
  }

  get length(): number {
    return this.at[this.at.length - 1];
  }

  /** They're somewhere else all at once (a teleport): the way there doesn't count. */
  lost(): void {
    this.last = null;
  }

  /** They're here, at `t` (ms); `counts`: on foot, on the ground. The race's time (seconds) as they finish it, else null. */
  step(x: number, y: number, counts: boolean, t: number): number | null {
    if (!counts || this.points.length < 2) {
      this.last = null;
      return null;
    }
    const [sx, sy] = this.points[0];
    if (Math.hypot(x - sx, y - sy) <= RaceWatch.START) {
      this.progress = 0;
      this.startedAt = t;
    }
    const from = this.last;
    this.last = { x, y, t };
    if (this.progress < 0) return null;
    // Moves can be a way apart (a stalled frame): the ground between counts, if they could have run it.
    const d = from ? Math.hypot(x - from.x, y - from.y) : 0;
    const ran = from !== null && d <= MAX_FOOT * 1.5 * Math.max(0.1, (t - from.t) / 1000) + 2;
    const steps = ran ? Math.max(1, Math.ceil(d)) : 1;
    for (let i = 1; i <= steps; i++) this.along(from && ran ? from.x + ((x - from.x) * i) / steps : x, from && ran ? from.y + ((y - from.y) * i) / steps : y);
    if (this.progress < this.length - RaceWatch.END) return null;
    this.progress = -1;
    const seconds = (t - this.startedAt) / 1000;
    // Nobody runs that fast.
    return seconds * MAX_FOOT * 1.1 + 2 * RaceWatch.END >= this.length ? seconds : null;
  }

  private along(x: number, y: number): void {
    let bestD = Infinity, bestS = 0, bestHalf = 0;
    const lo = this.progress - RaceWatch.BACK, hi = this.progress + RaceWatch.AHEAD;
    for (let k = 1; k < this.points.length; k++) {
      if (this.at[k] < lo) continue;
      if (this.at[k - 1] > hi) break;
      const [ax, ay, ah] = this.points[k - 1];
      const [bx, by, bh] = this.points[k];
      const dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
      const f = l2 > 1e-12 ? Math.max(0, Math.min(1, ((x - ax) * dx + (y - ay) * dy) / l2)) : 0;
      const d = Math.hypot(x - ax - dx * f, y - ay - dy * f);
      if (d >= bestD) continue;
      bestD = d;
      bestS = this.at[k - 1] + (this.at[k] - this.at[k - 1]) * f;
      bestHalf = ah + (bh - ah) * f;
    }
    // No further on than the look-ahead, however long the stretch it's on is.
    if (bestD <= bestHalf + RaceWatch.EDGE && bestS > this.progress && bestS <= hi) this.progress = bestS;
  }
}

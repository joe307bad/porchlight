// Smooth motion for other players. Each sends its position about 30 times a
// second, stamped with when it took it on its own clock; we keep them by that
// stamp and draw each player a little in the past (INTERP_DELAY) on their own
// clock (PlaybackClock), between the two moves either side, so they glide
// rather than step, however unevenly the moves arrive. If moves stop coming
// they carry on a moment (EXTRAPOLATE), then hold; after a long gap they jump
// straight to the latest. Pure, so it's unit-tested.

/** How far behind their best-case arrival others are drawn (ms): room for about three moves' worth of network jitter. */
export const INTERP_DELAY = 120;
/** How long to carry on past the last patch before holding still (ms). */
export const EXTRAPOLATE = 200;
/** Snapshots this far behind the render time are history (ms): past a background tab's once-a-second sends. */
const KEEP = 1500;

export interface Snap {
  /** When it was taken, on the sender's clock (ms). */
  t: number;
  x: number;
  y: number;
  z: number;
  /** Heading, radians. */
  yaw: number;
}

const TAU = Math.PI * 2;
/** The short way round from a to b, radians in (-π, π]. */
export function turnBetween(a: number, b: number): number {
  const d = (((b - a) % TAU) + TAU) % TAU;
  return d > Math.PI ? d - TAU : d;
}

export class SnapshotBuffer {
  private snaps: Snap[] = [];

  get size(): number {
    return this.snaps.length;
  }

  push(s: Snap): void {
    if (!Number.isFinite(s.t)) return; // no server time yet
    const last = this.snaps[this.snaps.length - 1];
    if (last && s.t < last.t) return; // out of date
    if (last && s.t === last.t) this.snaps[this.snaps.length - 1] = s;
    else this.snaps.push(s);
    if (this.snaps.length > 64) this.snaps.splice(0, this.snaps.length - 64);
  }

  /** Where they are at server time `t`, or null before the first snapshot. */
  sample(t: number): Snap | null {
    const snaps = this.snaps;
    if (!snaps.length || !Number.isFinite(t)) return null;
    const last = snaps[snaps.length - 1];
    // Long silent, or a backlog after a background tab: jump to the latest.
    if (t - last.t > KEEP) {
      this.snaps = [last];
      return { ...last, t };
    }
    if (t <= snaps[0].t) return { ...snaps[0], t };
    if (t >= last.t) {
      const prev = snaps[snaps.length - 2];
      if (!prev) return { ...last, t };
      const ahead = Math.min(t - last.t, EXTRAPOLATE);
      const k = ahead / (last.t - prev.t || 1);
      return {
        t,
        x: last.x + (last.x - prev.x) * k,
        y: Math.max(0, last.y + (last.y - prev.y) * k),
        z: last.z + (last.z - prev.z) * k,
        yaw: last.yaw,
      };
    }
    let i = snaps.length - 2;
    while (i > 0 && snaps[i].t > t) i--;
    const a = snaps[i];
    const b = snaps[i + 1];
    // Drop what's no longer needed (keep the one just before `t`).
    if (i > 0) this.snaps = snaps.slice(i);
    const k = (t - a.t) / (b.t - a.t || 1);
    return {
      t,
      x: a.x + (b.x - a.x) * k,
      y: a.y + (b.y - a.y) * k,
      z: a.z + (b.z - a.z) * k,
      yaw: a.yaw + turnBetween(a.yaw, b.yaw) * k,
    };
  }
}

/** How far back the recent arrivals are kept, to tell the network's best case (ms). */
const WINDOW = 3000;
/** Off by more than this (ms), the playhead jumps rather than catching up. */
const JUMP = 400;
/** How hard the playhead leans toward where it should be, and its limit (a rate of 0.85 to 1.15). */
const CATCH_UP = 300;
const MAX_WARP = 0.15;

/**
 * Which moment of one player's own clock to show (their moves' `t`). A move
 * arrives some delay after it was taken; the smallest recent delay is the
 * network at its best, and the playhead runs INTERP_DELAY behind that, so
 * moves held up by a jittery network are still there in time. It advances
 * with the frames, a touch faster or slower to stay on course, never jumping
 * back and forth with each arrival: steady motion on screen.
 */
export class PlaybackClock {
  private arrivals: { at: number; delay: number }[] = [];
  private head: number | null = null;

  /** A move taken at `sent` (their clock) arrived at `now` (ours), both ms. */
  observe(sent: number, now: number): void {
    if (!Number.isFinite(sent) || !Number.isFinite(now)) return;
    this.arrivals.push({ at: now, delay: now - sent });
    while (this.arrivals.length > 1 && now - this.arrivals[0].at > WINDOW) this.arrivals.shift();
  }

  /** Move on by `dt` ms to local time `now`; returns the moment of their clock to show, or null before any move. */
  advance(now: number, dt: number): number | null {
    if (!this.arrivals.length) return null;
    let best = Infinity;
    for (const a of this.arrivals) best = Math.min(best, a.delay);
    const ideal = now - best - INTERP_DELAY;
    if (this.head === null || Math.abs(ideal - this.head) > JUMP) this.head = ideal;
    else this.head += dt * (1 + Math.max(-MAX_WARP, Math.min(MAX_WARP, (ideal - this.head) / CATCH_UP)));
    return this.head;
  }
}

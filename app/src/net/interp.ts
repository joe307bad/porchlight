// Smooth motion for other players. Their positions arrive in patches about 20
// times a second; each player's are kept with the server time they're for, and
// drawn a little in the past (INTERP_DELAY), between the two patches either
// side, so they glide rather than step. If patches stop coming they carry on a
// moment (EXTRAPOLATE), then hold; after a long gap they jump straight to the
// latest. Pure, so it's unit-tested.

/** How far behind the server's clock others are drawn (ms): two patches' worth. */
export const INTERP_DELAY = 100;
/** How long to carry on past the last patch before holding still (ms). */
export const EXTRAPOLATE = 200;
/** Snapshots this far behind the render time are history (ms). */
const KEEP = 1000;

export interface Snap {
  /** Server time (ms). */
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

/**
 * The server's clock, read off the patches (each carries the server time).
 * A patch arrives some latency after it was stamped, so each reading is the
 * true offset minus that latency; the best (highest) recent one is kept,
 * easing down slowly in case the network gets slower.
 */
export class ServerClock {
  private offset: number | null = null;

  /** A patch stamped `serverTime` arrived at local time `now` (ms). */
  observe(serverTime: number, now: number): void {
    const sample = serverTime - now;
    // The first patch can come before the room has stamped a time: one NaN here would stick for good.
    if (!Number.isFinite(sample)) return;
    this.offset = this.offset === null ? sample : Math.max(sample, this.offset - 2);
  }

  /** The server's time now, or null before the first patch. */
  now(local: number): number | null {
    return this.offset === null ? null : local + this.offset;
  }
}

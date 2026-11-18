// The live feed (GET /mp/traffic/feed), read as a stream and fed into the
// World as each frame completes; on any drop, back again after a pause that
// grows (to 10 s) while it keeps failing.

import { FRAME_POSITIONS, FrameReader } from './protocol.ts';
import type { World } from './world.ts';

export type FeedStatus =
  | { state: 'connecting' }
  | { state: 'live' }
  | { state: 'retrying'; why: string; inMs: number }
  | { state: 'signedOut' }
  | { state: 'refused'; why: string };

export class Feed {
  private ctl: AbortController | null = null;
  private stopped = false;
  private retry = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Bytes taken in, for the panel's figures. */
  bytes = 0;
  frames = 0;

  private readonly world: World;
  private readonly onStatus: (s: FeedStatus) => void;

  /** This stream's name on the server, once it's said. */
  private id: string | null = null;
  /** The towns looked at closely: what's wanted, and what the server was last told. */
  private focusWanted: string[] = [];
  private focusSent: string | null = null;

  /**
   * The towns being looked at closely (their keys): the server sends those ten times a second, the
   * rest's traffic once a second. Said when it changes (and again on a new stream).
   */
  focus(towns: string[]): void {
    this.focusWanted = [...towns].sort();
    void this.sendFocus();
  }

  private async sendFocus(): Promise<void> {
    const body = JSON.stringify({ feed: this.id, towns: this.focusWanted });
    if (!this.id || body === this.focusSent) return;
    this.focusSent = body;
    try {
      const res = await fetch('/mp/traffic/focus', { method: 'POST', headers: { 'content-type': 'application/json' }, body });
      if (!res.ok) this.focusSent = null;
    } catch {
      this.focusSent = null; // said again on the next change
    }
  }

  constructor(world: World, onStatus: (s: FeedStatus) => void) {
    this.world = world;
    this.onStatus = onStatus;
  }

  start(): void {
    this.stopped = false;
    void this.run();
  }

  stop(): void {
    this.stopped = true;
    if (this.timer) clearTimeout(this.timer);
    this.ctl?.abort();
  }

  private async run(): Promise<void> {
    this.onStatus({ state: 'connecting' });
    const ctl = (this.ctl = new AbortController());
    let why = 'The connection dropped.';
    try {
      const res = await fetch('/mp/traffic/feed', { signal: ctl.signal, cache: 'no-store', credentials: 'same-origin' });
      if (res.status === 401) {
        this.onStatus({ state: 'signedOut' });
        return;
      }
      if (res.status === 429 || res.status === 403) {
        const body = (await res.json().catch(() => null)) as { error?: string } | null;
        this.onStatus({ state: 'refused', why: body?.error ?? `The server said ${res.status}.` });
        return;
      }
      if (!res.ok || !res.body) throw new Error(`The server said ${res.status}.`);
      const reader = res.body.getReader();
      const frames = new FrameReader();
      let live = false;
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        this.bytes += value.byteLength;
        const now = performance.now();
        for (const f of frames.push(value)) {
          this.frames++;
          if (f.type === FRAME_POSITIONS) this.world.applyPositions(f, now);
          else {
            this.world.applyRooms(f.rooms);
            if (f.feed && f.feed !== this.id) {
              this.id = f.feed;
              this.focusSent = null; // a new stream: say again what's looked at
              void this.sendFocus();
            }
          }
        }
        if (!live) {
          live = true;
          this.retry = 0;
          this.onStatus({ state: 'live' });
        }
      }
    } catch (err) {
      if (this.stopped) return;
      why = (err as Error).message || why;
    }
    if (this.stopped) return;
    const inMs = Math.min(10_000, 500 * 2 ** this.retry++);
    this.onStatus({ state: 'retrying', why, inMs });
    this.timer = setTimeout(() => void this.run(), inMs);
  }
}

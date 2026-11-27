// What a session was played on, and how it ran: for tying a lag report or an
// error to a device without guessing from timestamps (#26).
//
// - ClientInfo comes with the join (JoinOptions.client) and the WebSocket's
//   User-Agent: platform, OS, build, GPU, memory, screen, and an anonymous
//   install id (a random id the game makes once and keeps; never derived from
//   the hardware or the account). Kept on the session row.
// - The game reports how it's running once a minute (MSG_PERF): frame rate,
//   slow frames, its worst frame and memory. The room measures the round trip
//   itself (MSG_PING / MSG_PONG). Each report is logged, and a summary is kept
//   on the session row when it ends.
//
// Nothing here is personal: no names, no addresses, nothing typed in. Logs
// name sessions and characters by the first 8 characters of their ids (the
// database maps them back for an investigation).

/** First 8 characters of an id, for log lines. */
export const short = (id: string | null | undefined): string => (id ? id.slice(0, 8) : '-');

export const PLATFORMS = ['web', 'windows', 'mac', 'linux', 'editor', 'other'] as const;
export type ClientPlatform = (typeof PLATFORMS)[number];

export interface ClientInfo {
  /** web, windows, mac, linux, editor (or other, for anything unknown). */
  platform: ClientPlatform;
  /** The OS as the game sees it ("Windows 11 (10.0.22631) 64bit", "Mac OS X 15.1"). */
  os?: string;
  /** BuildInfo.Short: "web.87 · a1b2c3d", "v0.0.72 · a1b2c3d", "dev". */
  build?: string;
  /** The graphics card or browser renderer. */
  gpu?: string;
  /** System memory (MB) and logical cores, as the game sees them. */
  memMb?: number;
  cores?: number;
  /** The window's size in pixels: "1920x1080". */
  screen?: string;
  /** The game's anonymous install id (random, kept by the game; one per browser or install). */
  device?: string;
  /** The WebSocket's User-Agent: the browser on the web (the native game sends none). */
  ua?: string;
}

const text = (v: unknown, max: number): string | undefined => {
  if (typeof v !== 'string') return undefined;
  // No control characters or line breaks: these end up in log lines.
  const s = v.replace(/[\x00-\x1f\x7f-\x9f\u2028\u2029]/g, '').trim().slice(0, max);
  return s || undefined;
};

const count = (v: unknown, max: number): number | undefined =>
  typeof v === 'number' && Number.isFinite(v) && v >= 0 ? Math.min(Math.round(v), max) : undefined;

/** What the join said it's played on, cleaned up (an older game says nothing: platform "other"). */
export function parseClient(raw: unknown, userAgent: string | null): ClientInfo {
  const c = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  const platform = PLATFORMS.includes(c.platform as ClientPlatform) ? (c.platform as ClientPlatform) : 'other';
  const info: ClientInfo = {
    platform,
    os: text(c.os, 80),
    build: text(c.build, 40),
    gpu: text(c.gpu, 80),
    memMb: count(c.memMb, 1 << 22),
    cores: count(c.cores, 1024),
    screen: text(c.screen, 16),
    device: text(c.device, 40)?.replace(/[^\w-]/g, ''),
    ua: text(userAgent, 200),
  };
  for (const k of Object.keys(info) as (keyof ClientInfo)[]) if (info[k] === undefined) delete info[k];
  return info;
}

/** One line for the logs: "web/Windows 11 64bit, web.87 · a1b2c3d, device 9f8e7d6c, Firefox/153.0". */
export function describeClient(c: ClientInfo): string {
  const parts = [c.os ? `${c.platform}/${c.os}` : c.platform];
  if (c.build) parts.push(c.build);
  if (c.device) parts.push(`device ${short(c.device)}`);
  if (c.ua) {
    // Just the browser and its version, not the whole string.
    const browser = /(Firefox|Edg|OPR|Chrome|Safari)\/[\d.]+/.exec(c.ua)?.[0];
    if (browser) parts.push(browser);
  }
  return parts.join(', ');
}

/** The game's report on the last minute (MSG_PERF). */
export interface PerfReport {
  /** Frames a second, over the minute. */
  fps: number;
  /** Frames over 50 ms. */
  slow: number;
  /** The longest frame (ms). */
  worstMs: number;
  /** Managed / total memory the game has (MB), where it can tell. */
  memMb?: number;
  /** Running the town for the room (the host). */
  host?: boolean;
}

export function parsePerf(raw: unknown): PerfReport | null {
  const m = (raw && typeof raw === 'object' ? raw : null) as Record<string, unknown> | null;
  if (!m) return null;
  const fps = typeof m.fps === 'number' && Number.isFinite(m.fps) && m.fps >= 0 && m.fps <= 1000 ? Math.round(m.fps * 10) / 10 : null;
  const slow = count(m.slow, 100_000);
  const worstMs = count(m.worstMs, 600_000);
  if (fps === null || slow === undefined || worstMs === undefined) return null;
  const report: PerfReport = { fps, slow, worstMs };
  const memMb = count(m.memMb, 1 << 22);
  if (memMb !== undefined) report.memMb = memMb;
  if (m.host === true) report.host = true;
  return report;
}

/** How a session ran, kept on its row (sessions.perf). */
export interface PerfSummary {
  /** Reports received (about one a minute). */
  reports: number;
  fpsAvg: number | null;
  fpsMin: number | null;
  /** Slow frames (over 50 ms) in all, and the longest frame (ms). */
  slow: number;
  worstMs: number;
  /** Round trips to the room (ms), measured by the room. */
  rttAvg: number | null;
  rttMax: number | null;
  rttSamples: number;
  /** The most memory the game reported (MB). */
  memMaxMb: number | null;
}

/** A session's reports and round trips, as they come. */
export class SessionPerf {
  private reports = 0;
  private fpsTotal = 0;
  private fpsMin = Infinity;
  private slow = 0;
  private worstMs = 0;
  private memMax = 0;
  private rttTotal = 0;
  private rttMax = 0;
  private rttCount = 0;
  /** Round trips since the last report, for the next log line. */
  private recent: number[] = [];

  report(r: PerfReport): void {
    this.reports++;
    this.fpsTotal += r.fps;
    this.fpsMin = Math.min(this.fpsMin, r.fps);
    this.slow += r.slow;
    this.worstMs = Math.max(this.worstMs, r.worstMs);
    if (r.memMb !== undefined) this.memMax = Math.max(this.memMax, r.memMb);
  }

  rtt(ms: number): void {
    if (!Number.isFinite(ms) || ms < 0 || ms > 120_000) return;
    this.rttCount++;
    this.rttTotal += ms;
    this.rttMax = Math.max(this.rttMax, ms);
    this.recent.push(ms);
  }

  /** The round trips since the last call ("rtt 45 ms (max 120)"), and forget them. */
  takeRecentRtt(): string {
    const r = this.recent;
    this.recent = [];
    if (!r.length) return 'rtt -';
    return `rtt ${Math.round(r.reduce((a, b) => a + b, 0) / r.length)} ms (max ${Math.round(Math.max(...r))})`;
  }

  summary(): PerfSummary {
    return {
      reports: this.reports,
      fpsAvg: this.reports ? Math.round((this.fpsTotal / this.reports) * 10) / 10 : null,
      fpsMin: this.reports ? this.fpsMin : null,
      slow: this.slow,
      worstMs: this.worstMs,
      rttAvg: this.rttCount ? Math.round(this.rttTotal / this.rttCount) : null,
      rttMax: this.rttCount ? Math.round(this.rttMax) : null,
      rttSamples: this.rttCount,
      memMaxMb: this.memMax || null,
    };
  }
}

/** How a session ended, for its row and the log. */
export interface SessionEnd {
  /** The kick's reason, or the close code. */
  detail?: string;
  perf?: PerfSummary;
  drops?: number;
}

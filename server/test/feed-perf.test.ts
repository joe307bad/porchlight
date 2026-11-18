// The traffic feed shares the game's event loop, so it must stay cheap: a
// tick at a heavy but real load (every town of a county full, at the most a
// world message can carry, and many people watching) within a budget, each
// town packed once and its bytes sent to every viewer as they are (not copied
// per viewer), and a viewer that can't keep up skipped rather than queued.
//
// The budgets are several times what a desk measures, so a slow shared CI
// runner passes and a real slowdown doesn't.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { FEED_MS, STUCK_MS, TrafficFeed, type FeedSink } from '../src/multiplayer/feed.ts';
import type { CityRoom, WatchedPlayer } from '../src/multiplayer/CityRoom.ts';
import { WORLD_MAX_BYTES } from '../src/multiplayer/protocol.ts';

/** The most cars and people a town's world message carries (the WebSocket's 4 KB cap, less its framing). */
const RECORDS = 580;

function town(i: number, players: number, records = RECORDS): CityRoom {
  const list: WatchedPlayer[] = Array.from({ length: players }, (_, j) => ({
    sessionId: `s${i}-${j}`, characterId: `c${i}-${j}`, name: `Player ${j}`, look: 'characters/x',
    x: j, y: 0, z: j, rotY: j * 100, anim: 1, car: j % 3 === 0 ? 2 : 0, ride: j % 7 === 1 ? `s${i}-${j - 1}` : '',
  }));
  const bytes = new Uint8Array(Math.min(WORLD_MAX_BYTES, records * 7));
  for (let k = 0; k < bytes.length; k++) bytes[k] = (k * 31) & 0xff;
  return {
    town: `town-${i}`,
    watchId: i + 1,
    world: { cars: bytes.subarray(0, (records >> 1) * 7), people: bytes.subarray((records >> 1) * 7), at: Date.now() },
    state: { host: list[0]?.sessionId ?? '' },
    watchPlayers: () => list[Symbol.iterator](),
  } as unknown as CityRoom;
}

/** A viewer's connection: what it was sent, and whether it's still busy. */
class Sink implements FeedSink {
  writableNeedDrain = false;
  chunks: Buffer[] = [];
  bytes = 0;
  keep = false;
  write(chunk: Buffer): boolean {
    this.bytes += chunk.length;
    if (this.keep) this.chunks.push(chunk);
    return true;
  }
  cork(): void {}
  uncork(): void {}
  end(): void {}
}

function county(towns: CityRoom[]) {
  return new Map(towns.map((t, i) => [t.town, { x: i * 400, y: 0 }]));
}

function timeTicks(feed: TrafficFeed, n: number): { avg: number; p95: number } {
  for (let i = 0; i < 10; i++) feed.tick(); // warm up
  const times: number[] = [];
  for (let i = 0; i < n; i++) {
    const t0 = performance.now();
    feed.tick();
    times.push(performance.now() - t0);
  }
  times.sort((a, b) => a - b);
  return { avg: times.reduce((a, b) => a + b, 0) / n, p95: times[Math.floor(n * 0.95)] };
}

test('a full county, watched by 50: each tick well within its share of the event loop', () => {
  // Every town of a county (8 rooms), 100 players in each, every one at the most a world message carries.
  const towns = Array.from({ length: 8 }, (_, i) => town(i, 100));
  const feed = new TrafficFeed({} as never, () => towns);
  feed.log = () => {};
  const sinks = Array.from({ length: 50 }, () => new Sink());
  const stops = sinks.map((s, i) => feed.watch(`a${i}`, s, county(towns)));
  const { avg, p95 } = timeTicks(feed, 100);
  for (const stop of stops) stop();
  // 10 ms is a tenth of the time between ticks; a desk does this in about 1.
  assert.ok(avg < 10, `a tick took ${avg.toFixed(2)} ms on average (budget 10 ms)`);
  assert.ok(p95 < 25, `95% of ticks within ${p95.toFixed(2)} ms (budget 25 ms)`);
  assert.ok(FEED_MS >= 100, 'about 10 ticks a second at most');
});

test("viewers who can't see a town cost it nothing, and one quiet county costs almost nothing", () => {
  const towns = Array.from({ length: 8 }, (_, i) => town(i, 0, 0));
  const feed = new TrafficFeed({} as never, () => towns);
  feed.log = () => {};
  const stops = Array.from({ length: 200 }, (_, i) => feed.watch(`a${i}`, new Sink(), county(towns)));
  const { avg } = timeTicks(feed, 50);
  for (const stop of stops) stop();
  assert.ok(avg < 5, `${avg.toFixed(2)} ms a tick for 200 viewers of empty towns (budget 5 ms)`);
});

test("each town's bytes are packed once a tick and sent to every viewer as they are", () => {
  const towns = [town(0, 3), town(1, 3)];
  const feed = new TrafficFeed({} as never, () => towns);
  const a = new Sink();
  const b = new Sink();
  a.keep = b.keep = true;
  const stops = [feed.watch('a', a, county(towns)), feed.watch('b', b, county(towns))];
  feed.tick();
  a.chunks = [];
  b.chunks = [];
  feed.tick();
  for (const stop of stops) stop();
  // Each viewer: the positions frame's head, then each town's packed bytes.
  assert.equal(a.chunks.length, 3);
  assert.equal(b.chunks.length, 3);
  assert.notEqual(a.chunks[0], b.chunks[0], 'its own head (its towns and its count)');
  assert.equal(a.chunks[1], b.chunks[1], 'the same buffer, not a copy');
  assert.equal(a.chunks[2], b.chunks[2]);
});

test("a viewer that can't keep up misses frames, rather than having them pile up; slow ticks are said", () => {
  const towns = [town(0, 10)];
  const feed = new TrafficFeed({} as never, () => towns);
  const lines: string[] = [];
  feed.log = (l) => lines.push(l);
  const busy = new Sink();
  busy.writableNeedDrain = true;
  const fine = new Sink();
  const stops = [feed.watch('busy', busy, county(towns)), feed.watch('fine', fine, county(towns))];
  for (let i = 0; i < 5; i++) feed.tick();
  assert.equal(busy.bytes, 0, 'nothing written to a connection still busy');
  assert.ok(fine.bytes > 0);
  const stats = feed.takeStats();
  assert.equal(stats.skipped, 5);
  assert.equal(stats.ticks, 5);
  // A town far too big for a tick to keep up: said straight away.
  const slow = towns[0] as unknown as { watchPlayers: () => Iterator<WatchedPlayer> };
  const was = slow.watchPlayers;
  slow.watchPlayers = () => {
    const end = performance.now() + 40;
    while (performance.now() < end);
    return was();
  };
  feed.tick();
  assert.ok(lines.some((l) => l.startsWith('traffic feed: slow tick')), lines.join('\n'));
  for (const stop of stops) stop();
  assert.ok(lines.some((l) => /traffic feed: 0 viewer\(s\) \(none now\), \d+ ticks, avg/.test(l)), 'a last report once nobody is watching');
});

test("a viewer that takes nothing for a long while is let go (a closed tab a proxy never said was closed)", () => {
  const towns = [town(0, 3)];
  let clock = Date.now();
  const feed = new TrafficFeed({} as never, () => towns, () => clock);
  const lines: string[] = [];
  feed.log = (l) => lines.push(l);
  const gone = new Sink();
  gone.writableNeedDrain = true;
  let destroyed = false;
  (gone as unknown as { destroy(): void }).destroy = () => (destroyed = true);
  const slow = new Sink();
  const stops = [feed.watch('a', gone, county(towns)), feed.watch('a', slow, county(towns))];
  feed.tick();
  // A slow reader that catches up within the time is kept.
  slow.writableNeedDrain = true;
  clock += STUCK_MS - 1000;
  feed.tick();
  slow.writableNeedDrain = false;
  feed.tick();
  assert.equal(feed.viewerCount, 2);
  clock += 2000;
  feed.tick();
  assert.equal(feed.viewerCount, 1, 'the one taking nothing all along is gone');
  assert.ok(destroyed, 'its connection closed');
  assert.ok(lines.some((l) => l.includes('let go of a viewer')), lines.join('\n'));
  for (const stop of stops) stop();
});

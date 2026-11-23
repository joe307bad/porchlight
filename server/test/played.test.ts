// What the server makes of what it sees players do (played.ts).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { Mission } from '../src/missions.ts';
import { MAX_FOOT, Played, RaceWatch, type CourseLine } from '../src/multiplayer/played.ts';

// 100 tiles east, then 100 south.
const course: CourseLine = { points: [[10, 10, 1], [110, 10, 1], [110, 110, 1]] };
/** Along the course at `speed`, a move every `gap` ms; the time it's given, if any. */
function run(w: RaceWatch, speed: number, gap = 33, counts: (s: number) => boolean = () => true, from = 0, t0 = 0): number | null {
  let got: number | null = null;
  for (let i = 0; ; i++) {
    const s = Math.min(200, from + (speed * i * gap) / 1000);
    const [x, y] = s <= 100 ? [10 + s, 10] : [110, s - 90];
    got = w.step(x, y, counts(s), t0 + i * gap) ?? got;
    if (s >= 200) return got;
  }
}

test('a race run along its course is timed', () => {
  const t = run(new RaceWatch(course), 6);
  assert.ok(t !== null && Math.abs(t - 200 / 6) < 1.5, String(t));
  assert.ok(run(new RaceWatch(course), 8.16) !== null, 'the fastest sprint');
  assert.ok(run(new RaceWatch(course), 6, 500) !== null, 'stalled frames');
});

test("driven, cut short, impossibly fast or not from the start: it doesn't count", () => {
  assert.equal(run(new RaceWatch(course), 17), null);
  assert.equal(run(new RaceWatch(course), 6, 33, (s) => s < 40 || s > 90), null);
  assert.equal(run(new RaceWatch(course), 6, 33, () => true, 50), null);
  const w = new RaceWatch(course);
  w.step(10, 10, true, 0);
  w.lost();
  assert.equal(w.step(110, 110, true, 60_000), null);
  // Straight across the corner.
  const cut = new RaceWatch(course);
  for (let i = 0; i <= 300; i++) assert.equal(cut.step(10 + i / 3, 10 + i / 3, true, i * 100), null);
});

test('starting over starts the clock over', () => {
  const w = new RaceWatch(course);
  w.step(10, 10, true, 0);
  w.step(12, 10, true, 1_000);
  const t = run(w, 6, 33, () => true, 0, 60_000);
  assert.ok(t !== null && t < 35, String(t));
});

test('a mission is finished once a start, after its least time and some ground; a time kept once a race', () => {
  const p = new Played();
  let now = 0;
  p.now = () => now;
  const m = { id: 'm', least: 60 } as Mission;
  p.start('a', 'm');
  p.moved('a', 100);
  assert.equal(p.couldHaveFinished('a', m, true), false);
  now = 61_000;
  assert.equal(p.couldHaveFinished('b', m, true), false);
  assert.equal(p.couldHaveFinished('a', m, false), true);
  assert.equal(p.couldHaveFinished('a', m, true), true);
  assert.equal(p.couldHaveFinished('a', m, true), false);
  // Started over and over: still only so many, and another mission's start isn't lost to it.
  p.start('a', 'other');
  for (let i = 0; i < 50; i++) p.start('a', 'm');
  p.moved('a', 100);
  now += 61_000;
  assert.equal(p.couldHaveFinished('a', { ...m, id: 'other' }, true), true);

  p.raced('c', 'town', 300);
  assert.equal(p.claimRace('c', 'elsewhere', 299), null);
  assert.equal(p.claimRace('c', 'town', 298.5), 298.5);
  assert.equal(p.claimRace('c', 'town', 298.5), null);
  assert.ok(MAX_FOOT > 8);
});

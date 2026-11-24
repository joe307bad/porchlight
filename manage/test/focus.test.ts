// Manage's focus on one car or townsperson (life.ts): where it's drawn on the map
// (its own record while it's out, else where the simulation last said), how its
// decision tree marks each answer, and when each step in its queue comes.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { AMBER_MS, dayMark, focusPoint, isFocus, lampColor, mark, untilText, type EntityFocus, type FocusRoom } from '../src/life.ts';

const focus = (o: Partial<EntityFocus>): EntityFocus => ({
  town: 'seed=x',
  kind: 'person',
  id: 7,
  clock: 1_000_000,
  time: '08:15',
  who: 'Works, and lives in town',
  doing: 'Going to work, by car',
  drawn: 'none',
  slot: -1,
  at: [10, 20],
  tree: [],
  queue: [],
  path: [],
  dest: null,
  ...o,
});

function room(): FocusRoom {
  const layer = (n: number) => ({ n, shown: new Uint8Array(n), x: new Float32Array(n), y: new Float32Array(n) });
  const r = { ox: 100, oy: 200, cars: layer(4), people: layer(4) };
  r.cars.shown[2] = 1;
  r.cars.x[2] = 105.5;
  r.cars.y[2] = 210.25;
  return r;
}

test('a focus out and drawn is where its record is, moving with it; a hidden one where the simulation last said', () => {
  const r = room();
  // Driving: drawn as their car, in car slot 2.
  assert.deepEqual(focusPoint(focus({ drawn: 'car', slot: 2 }), r), { x: 105.5, y: 210.25, live: true });
  // Indoors: at their door, in the town's frame moved onto the county's.
  assert.deepEqual(focusPoint(focus({ drawn: 'none', at: [10, 20] }), r), { x: 110, y: 220, live: false });
  // Their slot's gone empty since (they've parked): where the simulation last said.
  assert.deepEqual(focusPoint(focus({ drawn: 'car', slot: 1, at: [3, 4] }), r), { x: 103, y: 204, live: false });
  // A slot past the room's records (they've changed): the same.
  assert.equal(focusPoint(focus({ drawn: 'person', slot: 9 }), r)!.live, false);
  // The town isn't in the feed: nowhere to draw it.
  assert.equal(focusPoint(focus({}), null), null);
});

test('a decision tree marks yes and no, and leaves a fact unmarked', () => {
  assert.equal(mark({ q: 'Is its light green?', a: 'Yes, until 08:15:30', yes: true }), '✓');
  assert.equal(mark({ q: 'Is there room for it beyond?', a: 'No: car 12 filled it', yes: false }), '✗');
  assert.equal(mark({ q: 'Which way?', a: 'The quickest by the clear roads' }), '');
});

test("a queue's steps say when: now, in seconds or minutes, or later in the day at a time", () => {
  const clock = 1_000_000;
  assert.equal(untilText({ at: clock, time: '08:15:00', what: 'Driving', now: true }, clock), 'now');
  assert.equal(untilText({ at: clock + 40_000, time: '08:15:40', what: 'Junction 3', now: false }, clock), 'in 40 s');
  assert.equal(untilText({ at: clock + 5 * 60_000, time: '08:20:00', what: 'Park', now: false }, clock), 'in 5 min');
  assert.equal(untilText({ at: clock + 3 * 3_600_000, time: '11:15', what: 'Set off', now: false }, clock), 'at 11:15');
  assert.equal(untilText({ at: null, time: null, what: 'Walk from the car', now: false }, clock), 'then');
  // A step whose time has come (the answer's a second old): now.
  assert.equal(untilText({ at: clock - 500, time: '08:14:59', what: 'Junction 3', now: false }, clock), 'now');
});

test("a day's trips are marked made, failed, under way, next and later", () => {
  assert.deepEqual((['done', 'failed', 'now', 'next', 'later'] as const).map(dayMark), ['✓', '✗', '▶', '→', '·']);
});

test('a click that finds a wanderer or an empty slot is no focus', () => {
  assert.equal(isFocus({ wanderer: true }), false);
  assert.equal(isFocus({ empty: true }), false);
  assert.equal(isFocus(null), false);
  assert.equal(isFocus(focus({})), true);
});

test("a traffic light's lamps: green on its stage, amber at the end of it, red otherwise, taking turns after", () => {
  const light = { stage: 0, ends: 30_000 };
  assert.equal(lampColor(light, 0, 10_000, 30_000), 'green');
  assert.equal(lampColor(light, 1, 10_000, 30_000), 'red');
  assert.equal(lampColor(light, 0, 30_000 - AMBER_MS + 1, 30_000), 'amber');
  // Past when it said it ends: the other stage's turn, then back.
  assert.equal(lampColor(light, 1, 31_000, 30_000), 'green');
  assert.equal(lampColor(light, 0, 31_000, 30_000), 'red');
  assert.equal(lampColor(light, 0, 61_000, 30_000), 'green');
});

test("a retimed light's lamps take turns on its own greens: a long main-road green, a short side-street one", () => {
  const light = { stage: 0, ends: 40_000, greens: [40_000, 15_000] as [number, number] };
  assert.equal(lampColor(light, 0, 1_000, 30_000), 'green');
  assert.equal(lampColor(light, 1, 41_000, 30_000), 'green');
  assert.equal(lampColor(light, 1, 55_000 - AMBER_MS + 1, 30_000), 'amber');
  assert.equal(lampColor(light, 0, 56_000, 30_000), 'green');
  // Before the stage it was asked in: the side street's green.
  assert.equal(lampColor(light, 1, -5_000, 30_000), 'green');
});

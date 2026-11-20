// A town's life as Manage reads it (life.ts), from what the server sends
// (GET /mp/sim/life, /mp/sim/building), and the hidden slots in the feed.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { describeInside, figures, indoorRadius, indoors, type TownLife } from '../src/life.ts';
import { HIDDEN, RECORD, readRecord } from '../src/protocol.ts';
import { buildingIndexAt } from '../src/zoning.ts';

const hours = Array.from({ length: 24 }, () => [0, 0, 0]);
hours[8] = [30, 20, 2];
hours[17] = [10, 25, 1];

const life: TownLife = {
  town: 'seed=x',
  clock: 0,
  time: '08:15',
  inside: [
    [0, 4],
    [3, 9],
  ],
  offMap: [2, 1],
  gates: [],
  stops: [],
  summary: {
    clock: '08:15',
    people: 20,
    out: 4,
    cars: 3,
    trips: { started: 88, finished: 80, failed: 2 },
    byHour: hours,
    meanTrip: [240, 360, 0],
    parking: [30, 120],
    busiestRoads: [],
    waits: [],
    problems: 0,
    gridlockBroken: 0,
    mainCrossings: 0,
    hunts: 0,
  },
};

test("given a town's life, then everyone's counted once: indoors, out, or out of town", () => {
  const f = figures(life);
  assert.equal(f.indoors, 13);
  assert.equal(f.offMap, 3);
  assert.equal(f.indoors + f.offMap + f.out, f.people);
  assert.equal(f.busiestHour, 8);
  assert.deepEqual(f.shares.map((x) => Math.round(x * 88)), [40, 45, 3]);
  assert.deepEqual(f.meanMinutes, [4, 6, null]);
  assert.equal(f.parked, 0.25);
});

test('given who is indoors, then each building has its count, and a bigger crowd a bigger disc', () => {
  const m = indoors(life);
  assert.equal(m.get(3), 9);
  assert.equal(m.get(1), undefined);
  assert.ok(indoorRadius(9) > indoorRadius(4));
  assert.ok(indoorRadius(1) >= 0.35, 'one person still shows');
  assert.ok(indoorRadius(10_000) <= 4, 'a crowd never covers the town');
});

test("given a building clicked, then who's in it is said a line each", () => {
  const lines = describeInside({
    town: 'seed=x',
    building: 3,
    inside: [
      { id: 7, kind: 'Worker', next: '17:30', to: 'building 1', purpose: 'Home' },
      { id: 9, kind: 'AtHome', next: null, to: null, purpose: null },
    ],
  });
  assert.deepEqual(lines, ['#7 · worker · out at 17:30 (home)', '#9 · at home · in for the day']);
});

test('given the hidden sentinel in a record, then it reads as nobody; anything else is where it says', () => {
  const b = new DataView(new ArrayBuffer(RECORD * 2));
  b.setUint16(0, HIDDEN, false);
  b.setUint16(2, HIDDEN, false);
  b.setUint16(RECORD, 1234, false);
  b.setUint16(RECORD + 2, HIDDEN, false); // only one of x, y at the top: still a place
  assert.equal(readRecord(b, 0), null);
  const r = readRecord(b, RECORD)!;
  assert.equal(r.x, 12.34);
  assert.equal(r.y, 655.35);
});

test('given a point on the map, then the building there is found by its place in the list', () => {
  const t = { buildings: [[0, 0, 0, 2, 2, 1, 0], [3, 5, 5, 8, 8, 0, 4]] };
  assert.equal(buildingIndexAt(t, 6, 7), 1);
  assert.equal(buildingIndexAt(t, 4, 4), -1);
});

// A town's life as Manage reads it (life.ts), from what the server sends
// (GET /mp/sim/life, /mp/sim/building), and the hidden slots in the feed.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ENTITY_KINDS, POPULATION_COLUMNS, congestionWidth, describeInside, figures, heldText, holdBars, indoorRadius, indoors, marketText, ofKind, type TownLife } from '../src/life.ts';
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

test('given what each record is in two towns, then a kind lists each of its own across them, and nothing else', () => {
  const a: TownLife = { ...life, town: 'a', kinds: { cars: 'rv-vbs', people: 'xxwsh-c' } };
  const b: TownLife = { ...life, town: 'b', kinds: { cars: '--v', people: 'w' } };
  const lives = new Map([
    ['a', a],
    ['b', b],
  ]);
  const vans = ENTITY_KINDS.find((k) => k.id === 'car:v')!;
  assert.deepEqual(ofKind(lives, vans), [
    ['a', 1],
    ['a', 3],
    ['b', 2],
  ]);
  const workers = ENTITY_KINDS.find((k) => k.id === 'person:w')!;
  assert.deepEqual(ofKind(lives, workers), [
    ['a', 2],
    ['b', 0],
  ]);
  // A car's letter isn't a person's: a student is 's', and so is a boat, but each is only of its own.
  const boats = ENTITY_KINDS.find((k) => k.id === 'car:s')!;
  assert.deepEqual(ofKind(lives, boats), [['a', 5]]);
  // Every record counted once, empties ('-') in none.
  const all = ENTITY_KINDS.reduce((n, k) => n + ofKind(lives, k).length, 0);
  assert.equal(all, 'rv-vbs--v'.replace(/-/g, '').length + 'xxwsh-cw'.replace(/-/g, '').length);
  // A town the server hasn't said kinds for (an older one): none of anything.
  assert.deepEqual(ofKind(new Map([['c', life]]), vans), []);
});

test('the kinds Manage shows are the fifteen the simulation sends: nine of vehicle, six on foot', () => {
  assert.equal(ENTITY_KINDS.filter((k) => k.of === 'car').length, 9);
  assert.equal(ENTITY_KINDS.filter((k) => k.of === 'person').length, 6);
  assert.equal(new Set(ENTITY_KINDS.map((k) => k.id)).size, 15);
  assert.equal(ENTITY_KINDS.filter((k) => k.of === 'car').map((k) => k.letter).join(''), 'rctvbgesp');
  assert.equal(ENTITY_KINDS.filter((k) => k.of === 'person').map((k) => k.letter).join(''), 'wshcmx');
  assert.equal(POPULATION_COLUMNS.length, 9, "the town table's columns: Analytics.POPULATION_COLUMNS");
});

test('the congestion slider: hold-ups in a few characters, each hour a bar against the worst, streets as wide as theirs', () => {
  assert.equal(heldText(40), '40 s');
  assert.equal(heldText(12 * 60), '12 min');
  assert.equal(heldText(3.4 * 3600), '3.4 h');
  const hours = [0, 30, 120, 60].map((hold, i) => ({ from: i * 3_600_000, time: `0${i}:00`, cars: hold, hold }));
  assert.deepEqual(holdBars(hours), [0, 0.25, 1, 0.5]);
  assert.deepEqual(holdBars(hours.map((h) => ({ ...h, hold: 0 }))), [0, 0, 0, 0], 'a quiet day: no bars, no dividing by nothing');
  assert.equal(congestionWidth(120, 120), 6);
  assert.equal(congestionWidth(0, 120), 1);
  assert.equal(congestionWidth(60, 120), 3.5);
});

const week = (market: boolean[]): TownLife['calendar'] =>
  market.map((m, i) => ({ day: 20000 + i, name: `Day ${i}`, weekend: m, holiday: null, school: !m, market: m, events: m ? ['Market day, 9 AM to 12 PM'] : [] }));

test('given market day with the stalls up, then the market line says how many are there till when', () => {
  const t = marketText({ ...life, calendar: week([true, false]), market: { stalls: 6, up: 4, open: true, hours: '9 AM to 12 PM', people: 120, at: [1, 1] } });
  assert.equal(t, 'Open till 12 PM: 4 of 6 stalls, 120 there');
});

test('given market day before it opens, then it says today, and the next', () => {
  const t = marketText({ ...life, calendar: week([true, false, false, false, false, false, false, true]), market: { stalls: 6, up: 4, open: false, hours: '9 AM to 12 PM', people: 0, at: [1, 1] } });
  assert.equal(t, 'Today 9 AM to 12 PM; next on Day 7');
});

test('given a weekday, then the market line says when it is next on; and a town with no market has none', () => {
  assert.equal(marketText({ ...life, calendar: week([false, false, true]), market: { stalls: 6, up: 0, open: false, hours: null, people: 0, at: [1, 1] } }), 'Next on Day 2');
  assert.equal(marketText({ ...life, market: null }), null);
});

test('given someone at the market, then they are their own kind on the map', () => {
  const kind = ENTITY_KINDS.find((k) => k.id === 'person:m')!;
  assert.equal(kind.letter, 'm');
  assert.deepEqual(ofKind(new Map([['t', { ...life, kinds: { cars: '', people: 'wm-m' } }]]), kind), [['t', 1], ['t', 3]]);
});

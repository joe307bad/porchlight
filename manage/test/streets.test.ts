// Street names on Manage's map (streets.ts), and what the right-click menu calls a spot (SpotMenu.tsx).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { CountyMap } from '../src/api.ts';
import { streetBetween, streetName, streetText, type CongestedStreet } from '../src/life.ts';
import { nearestStreet, streetLabels, townAt } from '../src/streets.ts';

type Town = CountyMap['towns'][number];
const town: Town = {
  key: 'seed=x',
  name: 'Lanternport',
  x0: 100,
  y0: 50,
  width: 64,
  height: 64,
  private: false,
  // A main road across, a street down with a short and a long stretch, and a path.
  roads: [
    [0, 2, 0, 10, 60, 10],
    [1, 1, 20, 10, 20, 14, 21, 40],
    [2, 0.5, 30, 30, 40, 40],
  ],
  roadNames: ['High Street', 'Elm Street', 'Fern Walk'],
  zoning: null,
  buildings: null,
};

test('each named street is written once, along its longest stretch, never upside down; main roads first; no paths', () => {
  const labels = streetLabels([town]);
  assert.deepEqual(labels.map((l) => l.name), ['High Street', 'Elm Street']);
  const elm = labels[1];
  assert.ok(Math.abs(elm.length - Math.hypot(1, 26)) < 1e-9, 'the long stretch, not the short one');
  assert.equal(elm.x, 100 + 20.5);
  assert.equal(elm.y, 50 + 27);
  for (const l of labels) assert.ok(l.angle >= -Math.PI / 2 && l.angle <= Math.PI / 2, `${l.name} reads left to right`);
  // A town whose names the server hasn't sent: nothing written.
  assert.deepEqual(streetLabels([{ ...town, roadNames: null }]), []);
});

test("a right-click's spot: its town, in the town's own tiles, and the street it's by", () => {
  const at = townAt([town], 121, 70);
  assert.ok(at);
  assert.equal(at.town.key, 'seed=x');
  assert.deepEqual([at.x, at.y], [21, 20]);
  assert.equal(nearestStreet(town, at.x, at.y), 'Elm Street');
  assert.equal(nearestStreet(town, 50, 11), 'High Street');
  assert.equal(nearestStreet(town, 60, 60), null, 'nothing named near the corner');
  assert.equal(townAt([town], 10, 10), null, 'outside every town');
});

test('a congested stretch is listed by its street and the streets at its ends, or (unnamed) by its kind', () => {
  const s: CongestedStreet = { road: 7, kind: 0, cars: 3, hold: 60, mean: 20, points: [] };
  assert.equal(streetText({ ...s, name: 'High Street', stretch: 'High Street, Elm Street to Oak Lane' }), 'High Street, Elm Street to Oak Lane');
  assert.equal(streetText({ ...s, name: 'High Street' }), 'High Street');
  assert.equal(streetText(s), 'Main road 7', "an older server's");
  // The list's two lines: the street, then the streets at the stretch's ends.
  const high = { ...s, name: 'High Street', stretch: 'High Street, Elm Street to Oak Lane' };
  assert.equal(streetName(high), 'High Street');
  assert.equal(streetBetween(high), 'Elm Street to Oak Lane');
  assert.equal(streetBetween({ ...s, name: 'High Street', stretch: 'High Street, at Elm Street' }), 'at Elm Street');
  assert.equal(streetBetween({ ...s, name: 'High Street', stretch: 'High Street' }), '');
  assert.equal(streetName(s), 'Main road 7');
});

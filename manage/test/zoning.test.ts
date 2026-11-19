// A town's land use as the panel and the map read it (zoning.ts), from the
// buildings the server sends: [zone, x0, y0, x1, y1, homes, jobs].

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildingAt, describe, landUse, ZONES } from '../src/zoning.ts';

const town = {
  zoning: { homes: 10, residents: 25, workers: 12, jobs: 12 },
  buildings: [
    [0, 0, 0, 2, 2, 1, 0],
    [0, 3, 0, 5, 2, 8, 0],
    [1, 0, 3, 2, 5, 1, 4],
    [3, 3, 3, 6, 6, 0, 6],
    [4, 7, 0, 9, 3, 0, 1],
    [5, 7, 4, 9, 6, 0, 1],
  ],
};

test('counts buildings by zone, and homes against business', () => {
  const u = landUse(town)!;
  assert.equal(u.buildings, 6);
  assert.deepEqual(u.byZone, [2, 1, 0, 1, 1, 1]);
  assert.equal(u.residential, 2);
  assert.equal(u.business, 2);
  assert.equal(u.mixed, 1);
  assert.equal(u.jobsPerHome, 1.2);
  assert.equal(u.balance, 'balanced');
  assert.equal(ZONES.length, 6);
});

test('says when a town is out of balance', () => {
  assert.equal(landUse({ ...town, zoning: { ...town.zoning, jobs: 2 } })!.balance, 'too few jobs');
  assert.equal(landUse({ ...town, zoning: { ...town.zoning, jobs: 40 } })!.balance, 'too many jobs');
});

test('a town not yet zoned has no land use', () => {
  assert.equal(landUse({ zoning: null, buildings: null }), null);
});

test('finds and describes the building under a point', () => {
  assert.equal(describe(buildingAt(town, 4.5, 5.9)!), 'Office · 6 jobs');
  assert.equal(describe(buildingAt(town, 1, 4)!), 'Mixed use · 1 home · 4 jobs');
  assert.equal(describe(buildingAt(town, 4, 1)!), 'Residential · 8 homes');
  assert.equal(buildingAt(town, 2.5, 2.5), null);
});

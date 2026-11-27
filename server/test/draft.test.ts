// The street editor's draft worker (sim/src/Draft.cs) run for real through DraftWorker: a plan, a plan
// against a base with the bullseye moved, houses, a skeleton it can't read, a plan superseded while it
// waited, and a worker that stops started again; and the simulation (SimPool) growing an edited town from
// the skeleton sent with it.
// Skipped where there's no build of it (as sim.test.ts).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { findWorker, SimPool } from '../src/sim/pool.ts';
import { DraftWorker } from '../src/sim/draft.ts';

const worker = findWorker({ ...process.env, SIM: undefined });
const skip = worker ? false : 'no build of the simulation worker (sim/)';
const KEY = 'seed=draft-test&width=96&height=96';

test('the draft worker plans a town, and what changed when the bullseye moves', { skip, timeout: 120_000 }, async () => {
  const w = new DraftWorker(worker!);
  w.log = () => {};
  try {
    const plain = (await w.request({ op: 'plan', key: KEY, skeleton: null })) as any;
    assert.equal(plain.ok, true, plain.error);
    const t = plain.town;
    assert.equal(t.width, 96);
    assert.ok(t.roads.length > 0 && t.nodes.length > 0 && t.blocks.length > 0 && t.buildings.length > 0);
    assert.equal(typeof t.water, 'string');
    assert.equal(Buffer.from(t.water, 'base64').length, Math.ceil((96 * 96) / 8));
    assert.ok(t.roads.every((r: any) => Array.isArray(r.pts) && r.pts.length >= 2 && typeof r.name === 'string' && ('design' in r)));
    assert.ok(t.junctions.every((j: any) => typeof j.control === 'string'));
    assert.equal(plain.changes, null);
    assert.ok(Object.keys(t.stats.ms).includes('buildings'));

    const houses = (await w.request({ op: 'houses', key: KEY, skeleton: null })) as any;
    assert.equal(houses.ok, true);
    assert.equal(houses.houses.length, t.stats.houses);

    const moved = (await w.request({ op: 'plan', key: KEY, skeleton: { v: 1, center: [t.center[0] + 8, t.center[1]] }, base: { key: KEY, skeleton: null } })) as any;
    assert.equal(moved.ok, true, moved.error);
    assert.equal(moved.town.center[0], t.center[0] + 8);
    assert.equal(typeof moved.changes.housesLost, 'number');
    assert.equal(moved.changes.lostHouses.length, moved.changes.housesLost);
    assert.equal(moved.changes.shops[0], t.stats.shops);

    const bad = await w.request({ op: 'plan', key: KEY, skeleton: { junctions: [{ at: [1, 1], control: 'teleport' }] } });
    assert.equal(bad.ok, false);
    assert.match(String(bad.error), /control/);
  } finally {
    w.close();
  }
});

test('the simulation grows an edited town (a key with `rev`) with the skeleton sent beside it in TOWNS', { skip, timeout: 120_000 }, async () => {
  const pool = new SimPool(worker!, 1);
  pool.log = () => {};
  const edited = `${KEY}&rev=1`;
  pool.setTowns([KEY, edited], new Map([[edited, { v: 1, center: [60.5, 40.5] }]]));
  pool.start();
  try {
    const end = Date.now() + 60_000;
    while (!(pool.town(KEY)?.roads && pool.town(edited)?.roads) && Date.now() < end) await new Promise((r) => setTimeout(r, 100));
    assert.ok(pool.town(KEY)?.roads && pool.town(edited)?.roads, 'both towns made');
    assert.notDeepEqual(pool.town(edited)!.roads, pool.town(KEY)!.roads, 'the bullseye moved the streets');
  } finally {
    pool.close();
  }
});

test('a plan still waiting is superseded by the next for the same editor; a worker that stops is started again', { skip, timeout: 120_000 }, async () => {
  const w = new DraftWorker(worker!);
  w.log = () => {};
  try {
    const first = w.request({ op: 'plan', key: KEY, skeleton: null }, 'me');
    const second = w.request({ op: 'plan', key: KEY, skeleton: null }, 'me');
    const third = w.request({ op: 'plan', key: KEY, skeleton: null }, 'me');
    // The first was sent at once; the second waited, and the third took its place.
    assert.equal((await first).ok, true);
    assert.deepEqual(await second, { ok: false, error: 'superseded' });
    assert.equal((await third).ok, true);

    (w as any).proc.kill('SIGKILL');
    while ((w as any).proc) await new Promise((r) => setTimeout(r, 20));
    const after = await w.request({ op: 'houses', key: KEY, skeleton: null });
    assert.equal(after.ok, true, String(after.error));
  } finally {
    w.close();
  }
});

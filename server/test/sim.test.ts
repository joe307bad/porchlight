// The simulation worker (sim/, .NET) run for real: it's started, given a town, ticks
// ten times a second with every car and person, sends deltas that carry a client from
// one tick to the next, stops for a player in the road, and puts a driver out of a
// car taken. Skipped where there's no build of it (no dotnet: `dotnet build -c Release`
// in sim/, or SIM_COMMAND).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { findWorker, SimPool, type SimTown } from '../src/sim/pool.ts';
import { STARTER_TOWNS } from '../src/multiplayer/persistence.ts';

const worker = findWorker({ ...process.env, SIM: undefined });
const skip = worker ? false : 'no build of the simulation worker (sim/)';

const RECORD = 7;

/** Waits until `ok` holds after a tick, or fails after `ms`. */
function until(pool: SimPool, ok: (towns: SimTown[]) => boolean, ms = 20_000): Promise<SimTown[]> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`not within ${ms} ms`)), ms);
    pool.onTick = (towns) => {
      if (!ok(towns)) return;
      clearTimeout(timer);
      resolve(towns);
    };
  });
}

/** A delta (sim/src/Delta.cs) applied to records as a client has them. */
function applyDelta(cars: Uint8Array, people: Uint8Array, d: Uint8Array): { cars: number; people: number; changed: number } {
  const v = new DataView(d.buffer, d.byteOffset, d.byteLength);
  const nc = v.getUint16(0), np = v.getUint16(2);
  let o = 4, changed = 0;
  for (const into of [cars, people]) {
    const n = v.getUint16(o);
    o += 2;
    for (let k = 0; k < n; k++, o += 2 + RECORD) {
      into.set(d.subarray(o + 2, o + 2 + RECORD), v.getUint16(o) * RECORD);
      changed++;
    }
  }
  assert.equal(o, d.byteLength, 'the delta ends where it says');
  return { cars: nc, people: np, changed };
}

test('the worker runs a town: full records, deltas, players, taken cars', { skip, timeout: 60_000 }, async () => {
  const pool = new SimPool(worker!, 1);
  const lines: string[] = [];
  pool.log = (l) => lines.push(l);
  pool.start();
  const key = STARTER_TOWNS[0].key;
  try {
    pool.setTowns([key]);
    const [town] = await until(pool, (t) => t.length > 0);
    assert.equal(town.key, key);
    assert.ok(town.cars.byteLength / RECORD > 0, 'it has traffic');
    assert.ok(town.people.byteLength / RECORD > 0, 'it has townsfolk');
    assert.ok(Math.abs(Date.now() - town.at) < 2_000, 'the tick is of now');

    // A client with the full town, carried along by the deltas, has every record the worker has
    // or (dead-reckoned between) close to it; and a delta is a good deal less than the whole town.
    const cars = Uint8Array.from(town.cars), people = Uint8Array.from(town.people);
    let full = 0, sent = 0;
    for (let i = 0; i < 30; i++) {
      const [t] = await until(pool, (ts) => ts.length > 0);
      const r = applyDelta(cars, people, t.delta);
      assert.equal(r.cars, cars.byteLength / RECORD);
      assert.equal(r.people, people.byteLength / RECORD);
      full += t.cars.byteLength + t.people.byteLength;
      sent += t.delta.byteLength;
    }
    assert.ok(sent < full * 0.6, `deltas ${sent} B against ${full} B whole`);

    // A player standing in front of a car: it stops there.
    const v = new DataView(town.cars.buffer, town.cars.byteOffset, town.cars.byteLength);
    let moving = -1;
    for (let i = 0; i < town.cars.byteLength / RECORD; i++) if (v.getUint8(i * RECORD + 6) > 20) moving = i;
    if (moving >= 0) {
      const [t] = await until(pool, (ts) => ts.length > 0);
      const w = new DataView(t.cars.buffer, t.cars.byteOffset, t.cars.byteLength);
      const at = moving * RECORD;
      const x = w.getUint16(at) / 100, y = w.getUint16(at + 2) / 100, h = (w.getUint16(at + 4) / 65536) * 2 * Math.PI;
      const stand = { x: x + Math.cos(h) * 1.2, y: y + Math.sin(h) * 1.2 };
      let speed = Infinity;
      for (let i = 0; i < 20 && speed > 0.2; i++) {
        pool.setPlayers(new Map([[key, [stand]]]));
        const [u] = await until(pool, (ts) => ts.length > 0);
        speed = new DataView(u.cars.buffer, u.cars.byteOffset).getUint8(at + 6) / 40;
      }
      assert.ok(speed <= 0.2, `car ${moving} stopped for the player (still ${speed} tiles a second)`);
      pool.setPlayers(new Map());
    }

    // A car taken after the town's first word on it: out of the traffic, its driver onto the pavement.
    pool.setTaken(key, []);
    const before = town.people.byteLength / RECORD;
    pool.setTaken(key, ['t0']);
    const [after] = await until(pool, (ts) => ts.length > 0 && ts[0].fullDue);
    assert.equal(after.people.byteLength / RECORD, before + 1, 'the driver is out');
    assert.equal(after.looks.length, 1);

    // Everyone's gone: the car's back in the traffic, the driver gone.
    pool.setTaken(key, []);
    const [back] = await until(pool, (ts) => ts.length > 0 && ts[0].people.byteLength / RECORD === before);
    assert.equal(back.looks.length, 0);
  } finally {
    pool.close();
  }
  assert.ok(!lines.some((l) => /couldn't|Exception/.test(l)), lines.join('\n'));
});

test('a worker that stops is started again, and given its towns', { skip, timeout: 60_000 }, async () => {
  const pool = new SimPool(worker!, 1);
  pool.log = () => {};
  pool.start();
  try {
    pool.setTowns([STARTER_TOWNS[1].key]);
    await until(pool, (t) => t.length > 0);
    // @ts-expect-error: the worker, reached into
    pool.workers[0].proc.kill('SIGKILL');
    const [t] = await until(pool, (ts) => ts.length > 0, 30_000);
    assert.equal(t.key, STARTER_TOWNS[1].key);
  } finally {
    pool.close();
  }
});

test('towns split between workers are each run once', { skip, timeout: 60_000 }, async () => {
  const pool = new SimPool(worker!, 2);
  pool.log = () => {};
  pool.start();
  try {
    const keys = STARTER_TOWNS.map((t) => t.key);
    pool.setTowns(keys);
    const seen = new Set<string>();
    await until(pool, (ts) => {
      for (const t of ts) seen.add(t.key);
      return seen.size === keys.length;
    }, 40_000);
    assert.deepEqual([...seen].sort(), [...keys].sort());
  } finally {
    pool.close();
  }
});

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
/** An empty slot (WorldSync.HIDDEN: nobody out in it now). */
const hidden = (v: DataView, i: number) => v.getUint16(i * RECORD) === 0xffff && v.getUint16(i * RECORD + 2) === 0xffff;

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

    // A player stepping out in front of a car: it stops short of them, and never drives into them. The town
    // runs on the real clock, so which cars are moving differs run to run, and the player reaches the worker
    // a tick or few after the positions they were placed by (the car's moved on meanwhile): a car that's
    // already on the spot by then, turns off before reaching it, or whose slot goes to another car proves
    // nothing either way, and the next one's tried.
    const pose = (u: { cars: Uint8Array }, at: number) => {
      const w = new DataView(u.cars.buffer, u.cars.byteOffset, u.cars.byteLength);
      const rx = w.getUint16(at), ry = w.getUint16(at + 2);
      if (rx === 0xffff && ry === 0xffff) return null;
      return { x: rx / 100, y: ry / 100, h: (w.getUint16(at + 4) / 65536) * 2 * Math.PI, speed: w.getUint8(at + 6) / 40 };
    };
    let stopped = -1;
    const tried = new Set<number>();
    const judged: number[] = [];
    for (let attempt = 0; attempt < 8 && stopped < 0; attempt++) {
      const [t] = await until(pool, (ts) => ts.length > 0);
      let moving = -1;
      for (let i = 0; i < t.cars.byteLength / RECORD; i++) {
        const p = pose(t, i * RECORD);
        if (p && p.speed > 0.5 && !tried.has(i)) moving = i;
      }
      if (moving < 0) break;
      tried.add(moving);
      const at = moving * RECORD;
      const first = pose(t, at)!;
      const stand = { x: first.x + Math.cos(first.h) * 3, y: first.y + Math.sin(first.h) * 3 };
      // Somewhere clear: no other car on (or just short of) the spot.
      let clear = true;
      for (let i = 0; i < t.cars.byteLength / RECORD; i++) {
        const o = i !== moving ? pose(t, i * RECORD) : null;
        if (o && Math.hypot(o.x - stand.x, o.y - stand.y) < 1.5) clear = false;
      }
      if (!clear) continue;
      let last = first;
      let seen = false;
      let nearest = Infinity;
      for (let i = 0; i < 20; i++) {
        pool.setPlayers(new Map([[key, [stand]]]));
        const [u] = await until(pool, (ts) => ts.length > 0);
        const p = pose(u, at);
        if (!p || Math.hypot(p.x - last.x, p.y - last.y) > 3) break; // parked, or the slot's another car's now
        last = p;
        const d = Math.hypot(p.x - stand.x, p.y - stand.y);
        const along = (stand.x - p.x) * Math.cos(p.h) + (stand.y - p.y) * Math.sin(p.h);
        // Already at the spot when the player got there: too late to stop, not driving into them.
        if (!seen && along < 1.4) break;
        if (!seen) judged.push(moving);
        seen = true;
        assert.ok(d > 0.45 || along <= 0 || p.speed <= 0.2, `car ${moving} drove into the player (${d.toFixed(2)} tiles off, ${p.speed} tiles a second)`);
        if (d > nearest + 0.5) break; // it turned off another way
        nearest = Math.min(nearest, d);
        if (p.speed <= 0.2 && along < 2.5) {
          stopped = moving;
          break;
        }
      }
      pool.setPlayers(new Map());
    }
    if (judged.length > 0) assert.ok(stopped >= 0, `none of cars ${judged.join(', ')} stopped for a player in front of it`);

    // A car taken after the town's first word on it: out of the traffic, its driver onto the pavement.
    pool.setTaken(key, []);
    const [now] = await until(pool, (ts) => ts.length > 0);
    const before = now.people.byteLength / RECORD;
    const cv = new DataView(now.cars.buffer, now.cars.byteOffset, now.cars.byteLength);
    let shown = -1;
    for (let i = 0; i < now.cars.byteLength / RECORD && shown < 0; i++) if (!hidden(cv, i)) shown = i;
    assert.ok(shown >= 0, 'a car on the road to take');
    pool.setTaken(key, [`t${shown}`]);
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

test('the town has a life: a clock, people indoors, a day\'s figures, and the worker answers about one of them', { skip, timeout: 60_000 }, async () => {
  const pool = new SimPool(worker!, 1);
  pool.log = () => {};
  pool.start();
  const key = STARTER_TOWNS[0].key;
  try {
    pool.setTowns([key]);
    await until(pool, (t) => t.length > 0 && t[0].life !== null && t[0].zoning !== null);
    const town = pool.town(key)!;
    const life = town.life!;
    assert.ok(Math.abs(life.clock - Date.now()) < 5_000, 'its clock is the time now');
    assert.match(life.time, /^\d\d:\d\d$/);
    const indoors = life.inside.reduce((n, [, k]) => n + k, 0);
    const people = life.summary.people as number;
    assert.ok(people > 100, `a town of people (${people})`);
    // Everyone's somewhere: indoors, off the map, or out.
    const off = life.offMap.reduce((n, k) => n + k, 0);
    assert.equal(indoors + off + (life.summary.out as number), people, 'everyone indoors, off the map or out');
    for (const [b] of life.inside) assert.ok(b >= 0 && b < town.zoning!.buildings.length, `building ${b} is one of the town's`);
    // Who's in the fullest building, and when each next goes out.
    const [fullest, n] = [...life.inside].sort((a, b) => b[1] - a[1])[0];
    const answer = (await pool.ask(key, 'building', fullest)) as { inside: { id: number; kind: string }[] } | null;
    assert.ok(answer, 'the worker answers');
    assert.equal(answer!.inside.length, Math.min(50, n), 'everyone inside (up to 50) listed');
    // A car on the road: who's driving and what they're up to; an empty slot: nobody.
    const v = new DataView(town.cars.buffer, town.cars.byteOffset, town.cars.byteLength);
    for (let i = 0; i < town.cars.byteLength / RECORD; i++) {
      const about = (await pool.ask(key, 'car', i)) as { state: string } | null;
      if (hidden(v, i)) continue;
      if (about) {
        assert.ok(typeof about.state === 'string');
        break;
      }
    }
    assert.equal(await pool.ask('no-such-town', 'car', 0), null);
  } finally {
    pool.close();
  }
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

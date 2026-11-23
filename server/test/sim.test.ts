// The simulation worker (sim/, .NET) run for real: it's started, given a town, ticks
// ten times a second with every car and person, sends deltas that carry a client from
// one tick to the next, stops for a player in the road, and puts a driver out of a
// car taken. Skipped where there's no build of it (no dotnet: `dotnet build -c Release`
// in sim/, or SIM_COMMAND).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { findWorker, SimPool, type SimTown } from '../src/sim/pool.ts';
import { STARTER_TOWNS } from '../src/multiplayer/persistence.ts';

// The towns run at mid-morning (10:30 county time, UTC-5) whenever this runs: at night the streets are all but
// empty, and there'd be no car to stand in front of or take. The worker's clock is shifted forward to it.
const HOUR = 3_600_000;
const DAY = 24 * HOUR;
const SHIFT = (((10.5 * HOUR - ((Date.now() - 5 * HOUR) % DAY)) % DAY) + DAY) % DAY;
process.env.SIM_CLOCK_SHIFT_MS = String(Math.round(SHIFT));
/** The towns' clock now. */
const townNow = () => Date.now() + SHIFT;

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
function applyDelta(cars: Uint8Array, people: Uint8Array, d: Uint8Array): { cars: number; people: number; changed: number; lights: number } {
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
  // Then the town's traffic lights (StreetFurniture.EncodeLights): u8 n, n × (u8 green stage, u16 ms until it ends).
  const lights = d[o];
  for (let k = 0; k < lights; k++) {
    const r = o + 1 + 3 * k;
    assert.ok(d[r] === 0 || d[r] === 1, 'a light\'s green stage is 0 or 1');
    assert.ok(v.getUint16(r + 1) <= 30_000, 'and it ends within a stage');
  }
  o += 1 + 3 * lights;
  assert.equal(o, d.byteLength, 'the delta ends where it says');
  return { cars: nc, people: np, changed, lights };
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
    assert.ok(Math.abs(townNow() - town.at) < 2_000, 'the tick is of now (the towns\' clock)');

    // A client with the full town, carried along by the deltas, has every record the worker has
    // or (dead-reckoned between) close to it; and a delta is a good deal less than the whole town.
    const cars = Uint8Array.from(town.cars), people = Uint8Array.from(town.people);
    let full = 0, sent = 0;
    for (let i = 0; i < 30; i++) {
      const [t] = await until(pool, (ts) => ts.length > 0);
      const r = applyDelta(cars, people, t.delta);
      assert.equal(r.cars, cars.byteLength / RECORD);
      assert.equal(r.people, people.byteLength / RECORD);
      // Porchlight has traffic signals: their lights come with every delta, for a player's lamps to show as here.
      assert.ok(r.lights > 0, 'the town\'s traffic lights come with the delta');
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
        if (d > nearest + 0.5) {
          // It turned off another way: it was never going to reach them, so it isn't judged.
          judged.pop();
          break;
        }
        nearest = Math.min(nearest, d);
        // Stopped short of them, having come on toward them (they stood 3 tiles ahead of its middle; a bus,
        // 3.1 long, stops with its middle 1.2 + 1.55 back).
        if (p.speed <= 0.2 && along < 3) {
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
    assert.ok(Math.abs(life.clock - townNow()) < 5_000, 'its clock is the time now (the towns\' clock)');
    assert.match(life.time, /^(1[0-2]|[1-9]):[0-5]\d [AP]M$/, 'Eastern time, on a 12-hour clock');
    const indoors = life.inside.reduce((n, [, k]) => n + k, 0);
    const people = life.summary.people as number;
    assert.ok(people > 100, `a town of people (${people})`);
    // Everyone's somewhere: indoors, off the map, or out.
    const off = life.offMap.reduce((n, k) => n + k, 0);
    assert.equal(indoors + off + (life.summary.out as number), people, 'everyone indoors, off the map or out');
    for (const [b] of life.inside) assert.ok(b >= 0 && b < town.zoning!.buildings.length, `building ${b} is one of the town's`);
    // Manage's town table: a row a kind of person, each counted once, by where they are now.
    const table = life.summary.population as [string, ...number[]][];
    assert.ok(table.length > 0, 'a population table');
    assert.equal(table.reduce((n, r) => n + (r[1] as number), 0), people, 'its rows add up to the town');
    for (const r of table) assert.equal((r.slice(2, 9) as number[]).reduce((n, k) => n + k, 0), r[1], `${r[0]}: home, work, elsewhere, walking, driving, bus and off the map add up`);
    // What each record is, a letter each: as many as there are records, and a letter for every one drawn.
    await until(pool, (t) => t[0].life?.kinds !== undefined);
    const now = pool.town(key)!;
    const kinds = now.life!.kinds!;
    assert.equal(kinds.cars.length, now.cars.byteLength / RECORD, 'a letter a car record');
    assert.ok(kinds.people.length >= now.people.byteLength / RECORD - 2, 'a letter a person record');
    assert.match(kinds.cars, /^[-rctvbsp]*$/);
    assert.match(kinds.people, /^[-wshcx]*$/);
    assert.ok(/[wshc]/.test(kinds.people) || /x/.test(kinds.people), 'some townsfolk of a kind');
    // Who's in the fullest building, and when each next goes out.
    const [fullest, n] = [...life.inside].sort((a, b) => b[1] - a[1])[0];
    const answer = (await pool.ask(key, 'building', fullest)) as { inside: { id: number; kind: string }[] } | null;
    assert.ok(answer, 'the worker answers');
    assert.equal(answer!.inside.length, Math.min(50, n), 'everyone inside (up to 50) listed');
    // A car on the road: who's driving and what they're up to; an empty slot: nobody.
    const v = new DataView(town.cars.buffer, town.cars.byteOffset, town.cars.byteLength);
    // (Only slots drawn are asked about: at night nearly all are empty, and each ask waits on a tick.)
    for (let i = 0; i < town.cars.byteLength / RECORD; i++) {
      if (hidden(v, i)) continue;
      const about = (await pool.ask(key, 'car', i)) as { state: string } | null;
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

interface Why { q: string; a: string; yes: boolean; note?: string; under?: Why[] }
interface Focus {
  kind: string;
  id: number;
  where?: string;
  doing: string;
  drawn: string;
  slot: number;
  tree: Why[];
  queue: { at: number | null; time: string | null; what: string; now: boolean }[];
  path: number[][];
  empty?: boolean;
  wanderer?: boolean;
  day?: { decides: string; entries: { status: string }[] };
}

test('Manage focuses on a car or townsperson: found where it\'s drawn, followed by who it is, its tree and queue', { skip, timeout: 60_000 }, async () => {
  const pool = new SimPool(worker!, 1);
  pool.log = () => {};
  pool.start();
  const key = STARTER_TOWNS[0].key;
  try {
    pool.setTowns([key]);
    await until(pool, (t) => t.length > 0 && t[0].life !== null && t[0].zoning !== null);
    const town = pool.town(key)!;
    const focus = async (kind: 'car' | 'person', by: 'slot' | 'id', i: number) =>
      (await pool.ask(key, kind === 'car' ? 'focus-car' : 'focus-person', i, by)) as Focus | null;

    // A car on the road, clicked where it's drawn: who it is, why it's doing what it's doing, what's next.
    const cars = new DataView(town.cars.buffer, town.cars.byteOffset, town.cars.byteLength);
    let car: Focus | null = null;
    let askedEmpty = false;
    for (let i = 0; i < town.cars.byteLength / RECORD && !car; i++) {
      if (hidden(cars, i)) {
        // One empty slot asked about is enough (at night nearly all are, and each ask waits on a tick).
        if (askedEmpty) continue;
        askedEmpty = true;
        const none = await focus('car', 'slot', i);
        assert.equal(none?.empty, true, 'an empty slot has nobody to focus on');
        continue;
      }
      const f = await focus('car', 'slot', i);
      if (f && !f.empty) car = f;
    }
    if (car) {
      assert.equal(car.kind, 'car');
      assert.ok(car.tree.length > 0 && car.tree.every((w) => typeof w.q === 'string' && typeof w.a === 'string'), 'a decision tree of questions and answers');
      assert.ok(car.queue.length > 0 && car.queue[0].now, 'its queue starts with what it does now');
      const again = await focus('car', 'id', car.id);
      assert.equal(again?.id, car.id, 'followed by who it is');
    }

    // Someone indoors, by who they are (from the building they're in): where they are and when they go out next.
    const life = town.life!;
    const [fullest] = [...life.inside].sort((a, b) => b[1] - a[1])[0];
    const inside = (await pool.ask(key, 'building', fullest)) as { inside: { id: number }[] };
    const person = await focus('person', 'id', inside.inside[0].id);
    assert.ok(person, 'a person indoors');
    assert.equal(person!.doing, 'Indoors');
    assert.equal(person!.tree[0].q, 'Where are they?');
    assert.match(person!.tree[0].a, new RegExp(`building ${fullest}\\b`), 'the building as Manage knows it (zoning\'s order)');
    assert.ok(person!.queue.some((x) => x.what.startsWith('Set off')), 'their next trip is in their queue');
    // Their day: where they are in it, and what decides what's next.
    assert.ok(person!.day && typeof person!.day.decides === 'string' && person!.day.decides.length > 0, 'what decides what they do next');
    assert.ok(person!.day!.entries.some((e) => e.status === 'next'), 'their next trip marked in their day');

    // The first townsfolk records are the wanderers: they've no day to explain.
    const first = await focus('person', 'slot', 0);
    assert.ok(first?.wanderer || first?.kind === 'person' || first?.empty, 'a wanderer, or someone of the town\'s life');

    // Someone out and drawn: found by where, and back again by who.
    const people = new DataView(town.people.buffer, town.people.byteOffset, town.people.byteLength);
    for (let i = town.people.byteLength / RECORD - 1; i >= 0; i--) {
      if (hidden(people, i)) continue;
      const f = await focus('person', 'slot', i);
      if (!f || f.wanderer || f.empty) continue;
      assert.equal(f.where, 'OnTrip');
      assert.ok(f.tree.some((w) => w.q === 'How are they getting there?'), 'how they chose to get there');
      const again = await focus('person', 'id', f.id);
      assert.equal(again?.id, f.id);
      // The map's hover line on them: who, what, and how far through their day.
      const brief = (await pool.ask(key, 'brief-person', i, 'slot')) as { id: number; line: string } | null;
      assert.equal(brief?.id, f.id);
      assert.match(brief!.line, /^Person #\d+ · .+ · trip \d+ of \d+ today$/);
      break;
    }
    assert.equal(await pool.ask('no-such-town', 'focus-car', 0, 'slot'), null);

    // The traffic lights Manage draws: each signal's stage and when it ends, a lamp at each stop line on a stage.
    const signals = (await pool.ask(key, 'signals', 0)) as { now: number; stageMs: number; lights: { stage: number; ends: number; lamps: number[][] }[] };
    assert.ok(signals && Array.isArray(signals.lights), 'the lights');
    assert.equal(signals.stageMs, 30_000);
    for (const l of signals.lights) {
      assert.ok(l.stage === 0 || l.stage === 1);
      assert.ok(l.ends > signals.now && l.ends <= signals.now + signals.stageMs, 'its stage ends within a stage from now');
      assert.ok(l.lamps.length >= 3 && l.lamps.every((p) => p.length === 3 && (p[2] === 0 || p[2] === 1)), 'a lamp on each way in');
      assert.ok(new Set(l.lamps.map((p) => p[2])).size === 2, 'some ways go on each stage');
    }
    assert.equal((signals as { stops?: unknown }).stops, undefined, 'stop signs only when asked');
    // Manage's congestion slider: the last 24 hours, oldest first, this one last; this hour's worst streets worst
    // first, each with its centreline to draw.
    const jam = (await pool.ask(key, 'congestion', 0)) as {
      ago: number;
      hours: { from: number; time: string; cars: number; hold: number }[];
      streets: { road: number; name: string; stretch: string; kind: number; cars: number; hold: number; mean: number; points: number[] }[];
    };
    assert.equal(jam.ago, 0);
    assert.equal(jam.hours.length, 24);
    for (let i = 1; i < 24; i++) assert.equal(jam.hours[i].from - jam.hours[i - 1].from, 3_600_000, 'an hour apart');
    assert.match(jam.hours[23].time, /^(1[0-2]|[1-9]):00 [AP]M$/);
    assert.ok(jam.streets.length <= 12, 'at most a dozen streets');
    for (let i = 1; i < jam.streets.length; i++) assert.ok(jam.streets[i].hold <= jam.streets[i - 1].hold, 'worst first');
    for (const st of jam.streets) {
      assert.ok(st.cars > 0 && st.hold > 0 && st.points.length >= 4 && st.points.length % 2 === 0, 'a street that held cars up, and its line');
      assert.ok(st.kind === 0 || st.kind === 1, 'a road cars drive');
      // By the name on its street signs in the game (StreetNaming, as World.cs has it), never "Main road 12".
      assert.ok(st.name.length > 0 && st.stretch.startsWith(st.name), `named as signed: ${st.stretch}`);
    }
    assert.ok(jam.streets.reduce((n, st) => n + st.hold, 0) <= jam.hours[23].hold + 1, "no more than the hour's hold-ups");
    // The stop signs, asked for once: one on the kerb by each lane that stops, all-way or not.
    const withStops = (await pool.ask(key, 'signals', 1)) as { stops: number[][] };
    assert.ok(withStops.stops.length > 0, 'the town has stop signs');
    assert.ok(withStops.stops.every((s) => s.length === 3 && (s[2] === 0 || s[2] === 1)));
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

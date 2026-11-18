// The server running a town end to end: a real game server with the simulation
// worker (sim/), played by SDK clients. Nobody hosts; each player arriving gets the
// whole town, then deltas; a client's world is turned down; Manage's feed has the
// town's traffic with nobody in it, looked at closely or not; and where a car is,
// is a request away. Skipped where there's no build of the worker.

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { Client } from '@colyseus/sdk';
import { SESSION_COOKIE } from '../src/app.ts';
import { seal } from '../src/cookies.ts';
import { startGameServer, type GameServer } from '../src/multiplayer/index.ts';
import { MemoryPersistence, STARTER_TOWNS } from '../src/multiplayer/persistence.ts';
import { MSG_SPAWN, MSG_WORLD, MSG_WORLD_DELTA, MSG_WORLD_WANT, SERVER_HOST } from '../src/multiplayer/protocol.ts';
import { findWorker } from '../src/sim/pool.ts';

const worker = findWorker({ ...process.env, SIM: undefined });
const skip = worker ? false : 'no build of the simulation worker (sim/)';

const SECRET = 'x'.repeat(40);
const APP = 'http://localhost:5173';
const persistence = new MemoryPersistence();
let game: GameServer;
let base = '';
const KEY = STARTER_TOWNS[0].key;

const token = (sub: string) => seal(SECRET, { sub, name: `Person ${sub}`, email: null, idToken: 't' }, 3600);
const headers = (sub: string) => ({ cookie: `${SESSION_COOKIE}=${token(sub)}`, origin: APP });

async function until(what: string, check: () => boolean, ms = 20_000): Promise<void> {
  const end = Date.now() + ms;
  while (!check()) {
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 20));
  }
}

before(async () => {
  if (!worker) return;
  persistence.start();
  game = await startGameServer({
    port: 0,
    appUrl: APP,
    sessionSecret: SECRET,
    persistence,
    handleSignals: false,
    handler: async (_req, res) => {
      res.writeHead(404);
      res.end();
    },
    sim: worker,
  });
  base = `http://localhost:${game.port}`;
  game.sim!.pool.log = () => {};
  await until('the town to be run', () => !!game.sim!.running(KEY));
});

after(async () => {
  if (game) await game.server.gracefullyShutdown(false);
});

test('the server hosts its towns: the whole town on arrival, then deltas', { skip }, async () => {
  const c = new Client(`${base}/mp`);
  c.auth.token = token('walker');
  const id = (await (await fetch(`${base}/mp/characters`, { method: 'POST', headers: { ...headers('walker'), 'content-type': 'application/json' }, body: JSON.stringify({ name: 'Walker' }) })).json()).id;
  const worlds: { cars: Uint8Array; people: Uint8Array; looks: string[] }[] = [];
  const deltas: Uint8Array[] = [];
  const room = await c.joinOrCreate('city', { town: KEY, width: 256, height: 256, character: id });
  room.onMessage(MSG_SPAWN, () => {});
  room.onMessage(MSG_WORLD, (m) => worlds.push(m));
  room.onMessage(MSG_WORLD_DELTA, (m) => deltas.push(m.d));
  await until('the server to host', () => room.state.host === SERVER_HOST);
  await until('the whole town', () => worlds.length > 0);
  await until('deltas', () => deltas.length >= 5);
  const w = worlds[0];
  assert.ok(w.cars.byteLength > 0 && w.cars.byteLength % 7 === 0);
  // Each delta is of the town the player was sent (same counts), and smaller than it.
  for (const d of deltas) {
    const v = new DataView(d.buffer, d.byteOffset, d.byteLength);
    assert.equal(v.getUint16(0), w.cars.byteLength / 7);
    assert.equal(v.getUint16(2), w.people.byteLength / 7);
  }
  const avg = deltas.reduce((s, d) => s + d.byteLength, 0) / deltas.length;
  assert.ok(avg < (w.cars.byteLength + w.people.byteLength) * 0.6, `a delta averages ${avg} B`);

  // A player's world is turned down while the server hosts.
  const before = worlds.length;
  room.send(MSG_WORLD, { cars: new Uint8Array([1, 2, 3, 4, 5, 6, 7]), people: new Uint8Array(0), looks: [] });
  await new Promise((r) => setTimeout(r, 300));
  assert.ok(worlds.slice(before).every((x) => x.cars.byteLength !== 7));

  // A game whose town wasn't ready for the whole asks again: sent it (once a second at most).
  const had = worlds.length;
  room.send(MSG_WORLD_WANT, {});
  room.send(MSG_WORLD_WANT, {});
  await until('the whole town again', () => worlds.length > had);
  await new Promise((r) => setTimeout(r, 300));
  assert.equal(worlds.length, had + 1);

  // Where's car 0? As the simulation has it.
  const where = await (await fetch(`${base}/mp/sim/where?town=${encodeURIComponent(KEY)}&kind=car&i=0`, { headers: headers('walker') })).json();
  assert.equal(where.kind, 'car');
  assert.ok(where.x >= 0 && where.x <= 256 && where.y >= 0 && where.y <= 256);
  assert.equal((await fetch(`${base}/mp/sim/where?town=${encodeURIComponent(KEY)}&kind=car&i=99999`, { headers: headers('walker') })).status, 404);
  await room.leave();
});

test('the feed has every town\'s traffic, nobody in them; towns not looked at closely come once a second', { skip }, async () => {
  const ctl = new AbortController();
  const res = await fetch(`${base}/mp/traffic/feed`, { headers: headers('viewer'), signal: ctl.signal });
  assert.equal(res.status, 200);
  const reader = res.body!.getReader();
  let buf = new Uint8Array(0);
  let feedId = '';
  let ids = new Map<number, string>();
  const frames: { at: number; rooms: { key: string; age: number; cars: number }[] }[] = [];
  const read = async () => {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) return;
      const joined = new Uint8Array(buf.length + value.length);
      joined.set(buf);
      joined.set(value, buf.length);
      buf = joined;
      for (;;) {
        if (buf.length < 5) break;
        const v = new DataView(buf.buffer, buf.byteOffset);
        const len = v.getUint32(0, true);
        if (buf.length < 4 + len) break;
        const type = buf[4];
        const body = buf.subarray(5, 4 + len);
        buf = buf.subarray(4 + len);
        if (type === 1) {
          const j = JSON.parse(new TextDecoder().decode(body));
          feedId = j.feed;
          ids = new Map(j.rooms.map((r: { id: number; key: string }) => [r.id, r.key]));
        } else if (type === 2) {
          const b = new DataView(body.buffer, body.byteOffset, body.byteLength);
          const n = b.getUint16(8, true);
          let o = 10;
          const rooms = [];
          for (let i = 0; i < n; i++) {
            const id = b.getUint32(o, true), age = b.getUint16(o + 4, true), players = b.getUint16(o + 6, true);
            o += 8 + players * 18;
            let cars = 0;
            if (age !== 0xfffe) {
              cars = b.getUint16(o, true);
              o += 2 + cars * 7;
              const people = b.getUint16(o, true);
              o += 2 + people * 7;
            }
            rooms.push({ key: ids.get(id) ?? '?', age, cars });
          }
          assert.equal(o, body.byteLength);
          frames.push({ at: Date.now(), rooms });
        }
      }
    }
  };
  const reading = read().catch(() => {});
  await until('every starter town in the feed with its traffic', () => {
    const last = frames.at(-1);
    return !!last && STARTER_TOWNS.every((t) => last.rooms.some((r) => r.key === t.key && r.cars > 0));
  });
  // Looking closely at one town: it every tick, the others' traffic once a second.
  const focus = await fetch(`${base}/mp/traffic/focus`, { method: 'POST', headers: { ...headers('viewer'), 'content-type': 'application/json' }, body: JSON.stringify({ feed: feedId, towns: [KEY] }) });
  assert.equal(focus.status, 200);
  assert.equal((await fetch(`${base}/mp/traffic/focus`, { method: 'POST', headers: { ...headers('someone-else'), 'content-type': 'application/json' }, body: JSON.stringify({ feed: feedId, towns: [] }) })).status, 404);
  const from = frames.length;
  await until('two seconds of frames', () => frames.length - from >= 20);
  const later = frames.slice(from + 1);
  const other = STARTER_TOWNS[1].key;
  assert.ok(later.every((f) => f.rooms.find((r) => r.key === KEY)!.age !== 0xfffe), 'the focused town every tick');
  const full = later.filter((f) => f.rooms.find((r) => r.key === other)!.age !== 0xfffe).length;
  assert.ok(full >= 1 && full <= 4, `the other town in full ${full} times in ${later.length}`);
  ctl.abort();
  await reading;
});

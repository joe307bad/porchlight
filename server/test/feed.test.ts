// The traffic view's feed end to end: a real server, a player hosting a town,
// and the feed read as the manage app reads it.

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { Client, type Room } from '@colyseus/sdk';
import { SESSION_COOKIE } from '../src/app.ts';
import { seal } from '../src/cookies.ts';
import { FRAME_POSITIONS as SERVER_POSITIONS, FRAME_ROOMS as SERVER_ROOMS } from '../src/multiplayer/feed.ts';
import { FRAME_POSITIONS, FRAME_ROOMS, FrameReader, PLAYER_BYTES, RECORD, readPlayer, readRecord, type RoomMeta } from '../../manage/src/protocol.ts';
import { startGameServer, type GameServer } from '../src/multiplayer/index.ts';
import { MemoryPersistence, STARTER_TOWNS } from '../src/multiplayer/persistence.ts';
import { MSG_SPAWN, MSG_WORLD } from '../src/multiplayer/protocol.ts';

const SECRET = 'y'.repeat(40);
const APP = 'http://localhost:5173';
const persistence = new MemoryPersistence();
let game: GameServer;
let base = '';

const token = (sub: string) => seal(SECRET, { sub, name: `Person ${sub}`, email: null, idToken: 't' }, 3600);
const cookie = (sub: string) => ({ cookie: `${SESSION_COOKIE}=${token(sub)}` });

async function api(sub: string, method: string, path: string, body?: unknown): Promise<{ status: number; json: any }> {
  const res = await fetch(`${base}${path}`, { method, headers: { ...cookie(sub), ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  return { status: res.status, json: await res.json() };
}

async function until(what: string, check: () => boolean, ms = 3000): Promise<void> {
  const end = Date.now() + ms;
  while (!check()) {
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 20));
  }
}

interface Rooms {
  rooms: RoomMeta[];
}
interface Positions {
  time: number;
  rooms: { id: number; age: number; players: { x: number; z: number; car: number; ride: number }[]; cars: { x: number; y: number; speed: number }[]; people: number }[];
}

test("the app reads the server's frames", () => {
  assert.equal(FRAME_ROOMS, SERVER_ROOMS);
  assert.equal(FRAME_POSITIONS, SERVER_POSITIONS);
});

/** The feed, read frame by frame by the manage app's own reader (manage/src/protocol.ts). */
async function watch(sub: string) {
  const ctl = new AbortController();
  const res = await fetch(`${base}/mp/traffic/feed`, { headers: cookie(sub), signal: ctl.signal });
  const rooms: Rooms[] = [];
  const positions: Positions[] = [];
  const frames = new FrameReader();
  const reading = (async () => {
    const reader = res.body!.getReader();
    for (;;) {
      const { value, done } = await reader.read().catch(() => ({ value: undefined, done: true }));
      if (done || !value) return;
      for (const f of frames.push(value)) {
        if (f.type === FRAME_ROOMS) {
          rooms.push({ rooms: f.rooms });
          continue;
        }
        assert.equal(f.type, FRAME_POSITIONS);
        positions.push({
          time: f.time,
          rooms: f.rooms.map((r) => ({
            id: r.id,
            age: r.age,
            players: Array.from({ length: r.players }, (_, i) => readPlayer(f.bytes, r.playersAt + i * PLAYER_BYTES)),
            cars: Array.from({ length: r.cars }, (_, i) => {
              const c = readRecord(f.bytes, r.carsAt + i * RECORD);
              return { x: c.x, y: c.y, speed: c.speed };
            }),
            people: r.people,
          })),
        });
      }
    }
  })();
  return { res, rooms, positions, stop: async () => { ctl.abort(); await reading; } };
}

before(async () => {
  game = await startGameServer({ port: 0, appUrl: APP, sessionSecret: SECRET, persistence, handleSignals: false, handler: async (_req, res) => { res.writeHead(404); res.end(); } });
  base = `http://localhost:${game.port}`;
});

after(async () => {
  await game.server.gracefullyShutdown(false);
});

test('the county map: every town that comes with the game, the highways, and only your own towns', async () => {
  assert.equal((await fetch(`${base}/mp/traffic/county`)).status, 401);
  const made = await api('maker', 'POST', '/mp/towns', { name: 'Hollow' });
  assert.equal(made.status, 201);
  const map = (await api('maker', 'GET', '/mp/traffic/county')).json;
  const names = map.towns.map((t: { name: string }) => t.name);
  for (const t of STARTER_TOWNS) assert.ok(names.includes(t.name), `${t.name} on the map`);
  assert.ok(names.includes('Hollow'));
  assert.ok(map.highways.length >= 5 && map.highways.every((h: number[]) => h.length >= 4 && h.length % 2 === 0));
  const other = (await api('someone', 'GET', '/mp/traffic/county')).json;
  assert.ok(!other.towns.some((t: { name: string }) => t.name === 'Hollow'), "someone else's town isn't on your map");
});

test('the feed: a town\'s players, cars and people, where they are on the county map, about 10 times a second', async () => {
  assert.equal((await fetch(`${base}/mp/traffic/feed`)).status, 401);
  const porchlight = STARTER_TOWNS.find((t) => t.place === 'porchlight')!;
  const lantern = STARTER_TOWNS.find((t) => t.place === 'east')!;
  const character = (await api('host', 'POST', '/mp/characters', { name: 'Hosty' })).json;
  const c = new Client(`${base}/mp`);
  c.auth.token = token('host');
  const room: Room = await c.joinOrCreate('city', { town: lantern.key, width: 256, height: 256, character: character.id });
  room.onMessage(MSG_SPAWN, () => {});
  room.onMessage(MSG_WORLD, () => {});
  room.send('move', { seq: 1, t: Math.round(performance.now()), epoch: 0, x: 40, y: 0, z: 50, yaw: 0, anim: 1, car: 0, ride: '' });
  await until('to host the town', () => room.state.host === room.sessionId);

  // Two cars (WorldSync's records: big-endian x and y in hundredths, heading, speed in fortieths), one person.
  const car = (x: number, y: number, speed: number) => [(x * 100) >> 8, (x * 100) & 0xff, (y * 100) >> 8, (y * 100) & 0xff, 0, 0, speed * 40];
  room.send(MSG_WORLD, { cars: new Uint8Array([...car(10, 20, 2), ...car(30, 40, 0)]), people: new Uint8Array(7), looks: [] });

  const viewer = await watch('someone');
  assert.equal(viewer.res.headers.get('content-type'), 'application/octet-stream');
  await until('the town in the feed, with its cars', () =>
    viewer.positions.some((p) => p.rooms.some((r) => r.cars.length === 2 && r.players.length === 1 && r.players[0].x === 40)),
  );
  const meta = viewer.rooms.at(-1)!.rooms.find((r) => r.key === lantern.key)!;
  assert.equal(meta.name, lantern.name);
  assert.equal(meta.players[0].name, 'Hosty');
  assert.equal(meta.host, 0);
  const map = (await api('someone', 'GET', '/mp/traffic/county')).json;
  const placed = map.towns.find((t: { key: string }) => t.key === lantern.key);
  assert.deepEqual([meta.ox, meta.oy], [placed.x0, placed.y0], 'the room is placed where its town is on the map');
  assert.ok(!viewer.rooms.at(-1)!.rooms.some((r) => r.key === porchlight.key), 'only rooms that are open');
  const last = viewer.positions.at(-1)!.rooms.find((r) => r.id === meta.id)!;
  assert.deepEqual(last.cars, [{ x: 10, y: 20, speed: 2 }, { x: 30, y: 40, speed: 0 }]);
  assert.equal(last.people, 1);
  assert.ok(last.age < 2000);

  // About 10 frames a second, and a rooms frame only when who's there changes.
  const before = viewer.positions.length;
  const roomsBefore = viewer.rooms.length;
  await new Promise((r) => setTimeout(r, 1000));
  const got = viewer.positions.length - before;
  assert.ok(got >= 7 && got <= 12, `${got} frames in a second`);
  assert.equal(viewer.rooms.length, roomsBefore);

  // They leave: the rooms frame says so.
  await room.leave();
  await until('the room to go from the feed', () => viewer.rooms.at(-1)!.rooms.every((r) => r.players.length === 0));
  await viewer.stop();
  await until('the viewer to be let go', () => game.traffic.viewerCount === 0);
});

test("a town someone made is only in its maker's feed", async () => {
  const town = (await api('maker', 'GET', '/mp/towns')).json.towns.find((t: { name: string }) => t.name === 'Hollow');
  const character = (await api('maker', 'POST', '/mp/characters', { name: 'Maker' })).json;
  const c = new Client(`${base}/mp`);
  c.auth.token = token('maker');
  const room: Room = await c.joinOrCreate('city', { town: town.key, width: 128, height: 128, character: character.id });
  room.onMessage(MSG_SPAWN, () => {});
  room.send('move', { seq: 1, t: Math.round(performance.now()), epoch: 0, x: 20, y: 0, z: 20, yaw: 0, anim: 1, car: 0, ride: '' });
  const mine = await watch('maker');
  const theirs = await watch('someone');
  await until("the maker's town in their feed", () => mine.rooms.some((r) => r.rooms.some((x) => x.key === town.key && x.players.length === 1)));
  await new Promise((r) => setTimeout(r, 300));
  assert.ok(theirs.positions.length > 0);
  assert.ok(!theirs.rooms.some((r) => r.rooms.some((x) => x.key === town.key)), "not in anyone else's");
  await room.leave();
  await mine.stop();
  await theirs.stop();
});

test('an account may only have so many feeds open', async () => {
  const open = [];
  for (let i = 0; i < 4; i++) open.push(await watch('greedy'));
  assert.equal((await fetch(`${base}/mp/traffic/feed`, { headers: cookie('greedy') })).status, 429);
  for (const v of open) await v.stop();
});

// The game server end to end: a real server on a spare port, played by SDK
// clients the way the app plays it.

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { Client, type Room } from '@colyseus/sdk';
import { SESSION_COOKIE } from '../src/app.ts';
import { seal } from '../src/cookies.ts';
import { startGameServer, type GameServer } from '../src/multiplayer/index.ts';
import { MemoryPersistence } from '../src/multiplayer/persistence.ts';
import { CLOSE_SIGNED_IN_ELSEWHERE, MSG_CORRECT, MSG_SPAWN, type CorrectMessage, type SpawnMessage } from '../src/multiplayer/protocol.ts';

const SECRET = 'x'.repeat(40);
const APP = 'http://localhost:5173';
const persistence = new MemoryPersistence();
let game: GameServer;
let base = '';

const token = (sub: string) => seal(SECRET, { sub, name: `Person ${sub}`, email: null, idToken: 't' }, 3600);

async function api(sub: string, method: string, path: string, body?: unknown): Promise<{ status: number; json: any }> {
  const res = await fetch(`${base}${path}`, {
    method,
    headers: { cookie: `${SESSION_COOKIE}=${token(sub)}`, ...(body ? { 'content-type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  return { status: res.status, json: await res.json() };
}

function client(sub: string): Client {
  const c = new Client(`${base}/mp`);
  c.auth.token = token(sub);
  return c;
}

async function until(what: string, check: () => boolean, ms = 3000): Promise<void> {
  const end = Date.now() + ms;
  while (!check()) {
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 20));
  }
}

const town = { town: 'seed=test', width: 128, height: 128 };
let seq = 0;
const move = (room: Room, x: number, z: number, epoch = 0, extra: { anim?: number; car?: number; ride?: string } = {}) =>
  room.send('move', { seq: ++seq & 0xffff, epoch, x, y: 0, z, yaw: 0, anim: 1, car: 0, ride: '', ...extra });

before(async () => {
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
  });
  base = `http://localhost:${game.port}`;
});

after(async () => {
  await game.server.gracefullyShutdown(false);
});

test('characters: an account makes several, and only sees its own', async () => {
  assert.equal((await api('dad', 'POST', '/mp/characters', { name: '  Dad\u0007 ' })).status, 201);
  assert.equal((await api('dad', 'POST', '/mp/characters', { name: 'Kid', look: 'characters/character-female-b' })).status, 201);
  assert.equal((await api('dad', 'POST', '/mp/characters', { name: 'Kid' })).status, 409);
  assert.equal((await api('dad', 'POST', '/mp/characters', { name: '' })).status, 400);
  const mine = (await api('dad', 'GET', '/mp/characters')).json.characters;
  assert.deepEqual(mine.map((c: { name: string }) => c.name), ['Dad', 'Kid']);
  assert.equal(mine[1].look, 'characters/character-female-b');
  assert.deepEqual((await api('stranger', 'GET', '/mp/characters')).json.characters, []);
  assert.equal((await fetch(`${base}/mp/characters`)).status, 401);
});

test('two characters on one account play together, and see each other move', async () => {
  const [dad, kid] = (await api('dad', 'GET', '/mp/characters')).json.characters;
  const a = await client('dad').joinOrCreate('city', { ...town, character: dad.id });
  const spawnA = new Promise<SpawnMessage>((r) => a.onMessage(MSG_SPAWN, r));
  const b = await client('dad').joinOrCreate('city', { ...town, character: kid.id });
  b.onMessage(MSG_SPAWN, () => {});
  assert.equal(a.roomId, b.roomId);
  assert.equal((await spawnA).at, null); // never played here: the town's own spawn point

  move(a, 10, 10);
  move(b, 12, 10);
  await until('each to see the other', () => a.state.players?.get(b.sessionId)?.x === 12 && b.state.players?.get(a.sessionId)?.x === 10);
  assert.equal(b.state.players.get(a.sessionId).name, 'Dad');

  // A different town is a different room.
  const other = await client('dad').joinOrCreate('city', { town: 'seed=else', width: 128, height: 128, character: (await api('dad', 'POST', '/mp/characters', { name: 'Third' })).json.id });
  assert.notEqual(other.roomId, a.roomId);
  await other.leave();

  // Someone else's character is off limits.
  await assert.rejects(client('stranger').joinOrCreate('city', { ...town, character: dad.id }));

  // Too fast: corrected back, and later moves from before the correction are ignored.
  const corrected = new Promise<CorrectMessage>((r) => b.onMessage(MSG_CORRECT, r));
  move(b, 100, 100);
  const c = await corrected;
  assert.deepEqual([c.x, c.z, c.epoch], [12, 10, 1]);
  move(b, 12.2, 10, 0); // old epoch
  move(b, 12.4, 10, 1);
  await until('the corrected move', () => a.state.players.get(b.sessionId)?.x > 12.3);

  // The same character again elsewhere: newest wins, the old one is signed out.
  const left = new Promise<number>((r) => a.onLeave(r));
  const a2 = await client('dad').joinOrCreate('city', { ...town, character: dad.id });
  const spawn2 = await new Promise<SpawnMessage>((r) => a2.onMessage(MSG_SPAWN, r));
  assert.equal(await left, CLOSE_SIGNED_IN_ELSEWHERE);
  assert.deepEqual(spawn2.at && [spawn2.at.x, spawn2.at.z], [10, 10]); // saved by the old session on its way out

  await a2.leave();
  await b.leave();
  await until('the kid to be saved', () => persistence.states.get(kid.id)?.town === 'seed=test');
  assert.equal(persistence.states.get(kid.id)?.x, 12.4);
  assert.equal([...persistence.sessions.values()].filter((s) => !s.reason).length, 0);
});

test('passengers ride with a driver, up to three to a car', async () => {
  const names = ['Driver', 'P1', 'P2', 'P3', 'P4'];
  const ids: string[] = [];
  for (const name of names) ids.push((await api('car', 'POST', '/mp/characters', { name })).json.id);
  const rooms = await Promise.all(ids.map((character) => client('car').joinOrCreate('city', { ...town, character })));
  const corrected = rooms.map((r) => {
    let n = 0;
    r.onMessage(MSG_CORRECT, () => n++);
    r.onMessage(MSG_SPAWN, () => {});
    return () => n;
  });
  await new Promise((r) => setTimeout(r, 100));
  const [driver, ...riders] = rooms;
  for (const r of riders) move(r, 49.5, 50); // standing by the car (and so placed, and seeing each other)
  move(driver, 50, 50, 0, { anim: 4, car: 1 });
  await until('the driver to drive', () => riders[0].state.players?.get(driver.sessionId)?.anim === 4);
  // Three get in; the fourth is turned away.
  for (const r of riders.slice(0, 3)) move(r, 50, 50, 0, { anim: 5, ride: driver.sessionId });
  await until('three passengers', () => [...driver.state.players.values()].filter((p: { ride: string }) => p.ride === driver.sessionId).length === 3);
  move(riders[3], 50, 50, 0, { anim: 5, ride: driver.sessionId });
  await until('the fourth to be refused', () => corrected[4]() === 1);
  assert.equal(driver.state.players.get(riders[3].sessionId)?.ride ?? '', '');
  // Nobody rides with someone on foot.
  move(driver, 50.1, 50, 0, { anim: 1 });
  move(riders[3], 50.1, 50, 1, { anim: 5, ride: driver.sessionId });
  await until('a refusal for riding with a walker', () => corrected[4]() === 2);
  await Promise.all(rooms.map((r) => r.leave()));
});

test('joins are refused while the database is down', async () => {
  const [dad] = (await api('dad', 'GET', '/mp/characters')).json.characters;
  persistence.down = true;
  try {
    await assert.rejects(client('dad').joinOrCreate('city', { ...town, character: dad.id }));
  } finally {
    persistence.down = false;
  }
});

// The game server end to end: a real server on a spare port, played by SDK
// clients the way the app plays it.

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { Client, type Room } from '@colyseus/sdk';
import { SESSION_COOKIE } from '../src/app.ts';
import { seal } from '../src/cookies.ts';
import { startGameServer, type GameServer } from '../src/multiplayer/index.ts';
import { MemoryPersistence, STARTER_TOWNS } from '../src/multiplayer/persistence.ts';
import { CLOSE_SIGNED_IN_ELSEWHERE, HOST_SILENT_MS, IN_USE, MSG_CAR, MSG_CORRECT, MSG_DOTS, MSG_LOOK, MSG_MISSION_DONE, MSG_NIGHT, MSG_SPAWN, MSG_TAKE, MSG_UNLOCK, MSG_UNLOCKS, MSG_WORLD, type CorrectMessage, type SpawnMessage } from '../src/multiplayer/protocol.ts';

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
  room.send('move', { seq: ++seq & 0xffff, t: Math.round(performance.now()), epoch, x, y: 0, z, yaw: 0, anim: 1, car: 0, ride: '', ...extra });

before(async () => {
  game = await startGameServer({
    port: 0,
    appUrl: APP,
    sessionSecret: SECRET,
    persistence,
    handleSignals: false,
    handler: async (req, res) => {
      if (req.url === '/boom') throw new Error('boom');
      res.writeHead(404);
      res.end();
    },
  });
  base = `http://localhost:${game.port}`;
});

after(async () => {
  await game.server.gracefullyShutdown(false);
});

test('a request that throws gets a 500, and the server carries on', async () => {
  const quiet = console.error;
  console.error = () => {};
  try {
    assert.equal((await fetch(`${base}/boom`)).status, 500);
    assert.equal((await fetch(`${base}/elsewhere`)).status, 404);
  } finally {
    console.error = quiet;
  }
});

test('characters: an account makes several, and only sees its own', async () => {
  assert.equal((await api('dad', 'POST', '/mp/characters', { name: '  Dad\u0007 ' })).status, 201);
  assert.equal((await api('dad', 'POST', '/mp/characters', { name: 'Kid', look: 'characters/character-female-b' })).status, 201);
  assert.equal((await api('dad', 'POST', '/mp/characters', { name: 'Kid' })).status, 409);
  assert.equal((await api('dad', 'POST', '/mp/characters', { name: '' })).status, 400);
  const mine = (await api('dad', 'GET', '/mp/characters')).json.characters;
  assert.deepEqual(mine.map((c: { name: string }) => c.name), ['Dad', 'Kid']);
  assert.deepEqual(mine.map((c: { town: string | null }) => c.town), [null, null]);
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
  // Signed in somewhere else, the account sees who's playing where (to join them).
  const listed = (await api('dad', 'GET', '/mp/characters')).json.characters;
  assert.deepEqual(listed.map((c: { town: string | null }) => c.town), ['seed=test', 'seed=test']);
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

test('animals are for accounts that have unlocked them, and an unlock is kept', async () => {
  // Not even at the start: a new character can't be made an animal.
  const made = (await api('keeper', 'POST', '/mp/characters', { name: 'Keeper', look: 'animals/animal-penguin' })).json;
  assert.equal(made.look, null);
  const join = async () => {
    const room = await client('keeper').joinOrCreate('city', { ...town, character: made.id });
    const spawn = await new Promise<SpawnMessage>((r) => room.onMessage(MSG_SPAWN, r));
    move(room, 20, 20);
    await until('them to be seen', () => room.state.players?.get(room.sessionId)?.x === 20);
    return { room, spawn };
  };
  const first = await join();
  assert.deepEqual(first.spawn.unlocks, []);
  const me = () => first.room.state.players.get(first.room.sessionId);
  first.room.send(MSG_LOOK, { model: 'animals/animal-penguin' });
  first.room.send(MSG_LOOK, { model: 'people/character-b-1' });
  await until('the person look', () => me().appearance === 'people/character-b-1');
  // Unlocked (twice, kept once; something there's no such unlock for, not at all): now a penguin will do.
  first.room.send(MSG_UNLOCK, { what: 'animals' });
  first.room.send(MSG_UNLOCK, { what: 'animals' });
  first.room.send(MSG_UNLOCK, { what: 'flying' });
  first.room.send(MSG_LOOK, { model: 'animals/animal-penguin' });
  await until('the penguin look', () => me().appearance === 'animals/animal-penguin');
  await first.room.leave();
  await until('the look to be saved', () => persistence.states.get(made.id)?.look === 'animals/animal-penguin');
  assert.deepEqual(await persistence.unlocks((await persistence.account('keeper', '')).id), ['animals']);
  const second = await join();
  assert.deepEqual(second.spawn.unlocks, ['animals']);
  assert.equal(second.spawn.look, 'animals/animal-penguin');
  await second.room.leave();
});

test('what one character earns, all the account\'s characters have, at once and in any town', async () => {
  const one = (await api('family', 'POST', '/mp/characters', { name: 'One' })).json;
  const two = (await api('family', 'POST', '/mp/characters', { name: 'Two' })).json;
  const a = await client('family').joinOrCreate('city', { ...town, character: one.id });
  a.onMessage(MSG_SPAWN, () => {});
  const b = await client('family').joinOrCreate('city', { town: 'seed=family-other', width: 128, height: 128, character: two.id });
  const spawned = new Promise<SpawnMessage>((r) => b.onMessage(MSG_SPAWN, r));
  const told = new Promise<{ unlocks: string[] }>((r) => b.onMessage(MSG_UNLOCKS, r));
  assert.deepEqual((await spawned).unlocks, []);
  move(a, 20, 20);
  move(b, 20, 20);
  await until('them to be placed', () => b.state.players?.get(b.sessionId)?.x === 20 && a.state.players?.get(a.sessionId)?.x === 20);
  a.send(MSG_UNLOCK, { what: 'firefighter' });
  assert.deepEqual((await told).unlocks, ['firefighter']);
  // So the other one, in another town, can be the firefighter straight away.
  b.send(MSG_LOOK, { model: 'people/firefighter' });
  await until('the firefighter look', () => b.state.players.get(b.sessionId)?.appearance === 'people/firefighter');
  // A new character on the account has it from the start.
  assert.equal((await api('family', 'POST', '/mp/characters', { name: 'New', look: 'people/firefighter' })).json.look, 'people/firefighter');
  await a.leave();
  await b.leave();
  // Someone else's account doesn't.
  assert.equal((await api('stranger', 'POST', '/mp/characters', { name: 'New', look: 'people/firefighter' })).json.look, null);
});

test('each town keeps its own best times for a mission, one per account', async () => {
  const runner = (await api('runner', 'POST', '/mp/characters', { name: 'Runner' })).json;
  const key = encodeURIComponent('seed=marathon1');
  const empty = await api('runner', 'GET', `/mp/records?town=${key}&mission=marathon`);
  assert.equal(empty.status, 200);
  assert.deepEqual(empty.json, { best: [], mine: null });
  const kept = await api('runner', 'POST', '/mp/records', { town: 'seed=marathon1', mission: 'marathon', character: runner.id, seconds: 312.345 });
  assert.equal(kept.status, 200);
  assert.equal(kept.json.before, null);
  assert.equal(kept.json.mine, 312.35);
  assert.deepEqual(kept.json.best.map((r: { name: string; mine: boolean }) => [r.name, r.mine]), [['Runner', true]]);
  // Another town's board is its own; nonsense is refused.
  assert.deepEqual((await api('runner', 'GET', `/mp/records?town=${encodeURIComponent('seed=marathon2')}&mission=marathon`)).json.best, []);
  assert.equal((await api('runner', 'POST', '/mp/records', { town: 'seed=marathon1', mission: 'no-such', character: runner.id, seconds: 30 })).status, 404);
  assert.equal((await api('runner', 'POST', '/mp/records', { town: 'seed=marathon1', mission: 'marathon', character: runner.id, seconds: -1 })).status, 400);
  assert.equal((await api('runner', 'POST', '/mp/records', { town: 'nope', mission: 'marathon', character: runner.id, seconds: 30 })).status, 400);
  // Someone else sees the time, as not theirs.
  const seen = await api('stranger', 'GET', `/mp/records?town=${key}&mission=marathon`);
  assert.deepEqual([seen.json.best[0].name, seen.json.best[0].mine, seen.json.mine], ['Runner', false, null]);
});

test('missions finished are the account\'s, and can be played again', async () => {
  const one = (await api('finisher', 'POST', '/mp/characters', { name: 'One' })).json;
  const two = (await api('finisher', 'POST', '/mp/characters', { name: 'Two' })).json;
  const before = (await api('finisher', 'GET', '/mp/progress')).json;
  assert.deepEqual(before.missions.find((m: { id: string }) => m.id === 'chase').times, 0);
  assert.deepEqual(before.unlocks.map((u: { id: string; at: string | null }) => [u.id, u.at]), [['animals', null], ['firefighter', null], ['sprint', null]]);
  assert.equal(before.unlocks[0].mission, 'Penguin on the run');
  assert.equal(before.unlocks[2].mission, 'Marathon runner');
  for (const character of [one.id, two.id]) {
    const room = await client('finisher').joinOrCreate('city', { ...town, character });
    await new Promise<SpawnMessage>((r) => room.onMessage(MSG_SPAWN, r));
    room.send(MSG_MISSION_DONE, { id: 'chase' });
    room.send(MSG_MISSION_DONE, { id: 'no-such-mission' });
    room.send(MSG_UNLOCK, { what: 'animals' });
    await new Promise((r) => setTimeout(r, 100));
    await room.leave();
  }
  const after = (await api('finisher', 'GET', '/mp/progress')).json;
  const chase = after.missions.find((m: { id: string }) => m.id === 'chase');
  assert.deepEqual([chase.times, chase.firstCharacter, chase.unlocks], [2, 'One', 'animals']);
  assert.ok(after.unlocks[0].at);
  assert.equal(after.unlocks[1].at, null);
  assert.equal(after.missions.some((m: { id: string }) => m.id === 'no-such-mission'), false);
  assert.equal((persistence.completions.get((await persistence.account('finisher', '')).id) ?? []).length, 1);
});

test('towns: three each and delete one to make another, and houses for two', async () => {
  const listed = (await api('mayor', 'GET', '/mp/towns')).json;
  assert.deepEqual([listed.made, listed.max], [0, 3]);
  assert.ok(listed.towns.some((t: { name: string; mine: boolean }) => t.name === 'Porchlight' && !t.mine));
  const made: { id: string; key: string }[] = [];
  for (const name of ['One', 'Two', 'Three']) {
    const res = await api('mayor', 'POST', '/mp/towns', { name });
    assert.equal(res.status, 201);
    made.push(res.json);
  }
  assert.equal(new Set(made.map((t) => t.key)).size, 3);
  assert.equal((await api('mayor', 'POST', '/mp/towns', { name: 'Four' })).status, 409);
  assert.equal((await api('mayor', 'POST', '/mp/towns', { name: ' ' })).status, 400);

  const [a, b, c] = await Promise.all(['A', 'B', 'C'].map(async (name) => (await api('mayor', 'POST', '/mp/characters', { name })).json));
  const other = (await api('neighbour', 'POST', '/mp/characters', { name: 'Other' })).json;
  const home = (who: string, id: string, x: number, y: number) => api(who, 'PUT', `/mp/characters/${id}/home`, { x, y });
  const moveTo = (who: string, id: string, town: string) => api(who, 'PUT', `/mp/characters/${id}/town`, { town });
  assert.equal((await home('mayor', a.id, 5, 6)).status, 409); // no town yet
  assert.equal((await moveTo('neighbour', a.id, made[0].id)).status, 404); // not theirs
  assert.equal((await moveTo('mayor', a.id, crypto.randomUUID())).status, 404);
  for (const id of [a.id, b.id, c.id]) assert.equal((await moveTo('mayor', id, made[0].id)).status, 200);
  // A town someone made is theirs alone: no one else sees it, lives there or plays there. Porchlight is everyone's.
  assert.equal((await moveTo('neighbour', other.id, made[0].id)).status, 404);
  assert.ok(!(await api('neighbour', 'GET', '/mp/towns')).json.towns.some((t: { id: string }) => made.some((m) => m.id === t.id)));
  assert.ok((await api('neighbour', 'GET', '/mp/towns')).json.towns.some((t: { name: string }) => t.name === 'Porchlight'));
  assert.equal((await api('neighbour', 'GET', `/mp/towns/${made[0].id}/homes`)).status, 404);
  await assert.rejects(client('neighbour').joinOrCreate('city', { town: made[0].key, width: 128, height: 128, character: other.id }), /someone else/);

  const moved = await home('mayor', a.id, 5, 6);
  assert.deepEqual(moved.json.home, { townId: made[0].id, townName: 'One', townKey: made[0].key, house: { x: 5, y: 6 } });
  assert.equal((await home('mayor', b.id, 5, 6)).status, 200); // two to a house
  assert.equal((await home('mayor', c.id, 5, 6)).status, 409); // not three
  assert.equal((await home('neighbour', other.id, 5, 6)).status, 409); // not in that town
  assert.equal((await home('mayor', a.id, -1, 6)).status, 400);
  assert.deepEqual((await api('mayor', 'GET', `/mp/towns/${made[0].id}/homes`)).json.homes, [{ x: 5, y: 6, mine: true, residents: ['A', 'B'] }]);
  assert.equal((await api('mayor', 'GET', '/mp/towns')).json.towns.find((t: { id: string }) => t.id === made[0].id).residents, 3);
  // The character's list says where they live.
  const mine = (await api('mayor', 'GET', '/mp/characters')).json.characters;
  assert.equal(mine.find((x: { name: string }) => x.name === 'A').home.townName, 'One');
  // Moving town leaves the house behind.
  assert.equal((await moveTo('mayor', b.id, made[1].id)).json.home.house, null);

  // Not someone else's town, nor one anyone's playing in; a deleted town's people choose again.
  assert.equal((await api('neighbour', 'DELETE', `/mp/towns/${made[0].id}`)).status, 403);
  assert.equal((await api('mayor', 'DELETE', `/mp/towns/${listed.towns[0].id}`)).status, 403);
  const room = await client('mayor').joinOrCreate('city', { town: made[0].key, width: 128, height: 128, character: c.id });
  await new Promise<SpawnMessage>((r) => room.onMessage(MSG_SPAWN, r));
  assert.equal((await api('mayor', 'GET', '/mp/towns')).json.towns.find((t: { id: string }) => t.id === made[0].id).playing, 1);
  assert.equal((await api('mayor', 'DELETE', `/mp/towns/${made[0].id}`)).status, 409);
  await room.leave();
  await until('them to have left', () => persistence.sessions.size > 0 && ![...persistence.sessions.values()].some((s) => s.characterId === c.id && !s.reason));
  assert.equal((await api('mayor', 'DELETE', `/mp/towns/${made[0].id}`)).status, 200);
  assert.equal((await api('mayor', 'GET', '/mp/characters')).json.characters.find((x: { name: string }) => x.name === 'A').home, null);
  assert.equal((await api('mayor', 'POST', '/mp/towns', { name: 'Four' })).status, 201);
});

test('the town editor saves a town by name: its own key, once, and it counts towards the limit', async () => {
  const key = 'seed=my-town&width=96&roadDensity=0.5';
  const saved = await api('editor', 'POST', '/mp/towns', { name: 'My Town', key });
  assert.equal(saved.status, 201);
  assert.deepEqual([saved.json.name, saved.json.key, saved.json.mine], ['My Town', key, true]);
  const again = await api('someone', 'POST', '/mp/towns', { name: 'Copy', key });
  assert.equal(again.status, 409);
  assert.match(again.json.error, /My Town/);
  assert.equal((await api('someone', 'POST', '/mp/towns', { name: 'Maple', key: STARTER_TOWNS[0].key })).status, 409);
  for (const bad of ['nonsense', 'seed=', 'seed=a&b=<script>', 'x=1&seed=a', 5, `seed=${'a'.repeat(512)}`]) {
    assert.equal((await api('editor', 'POST', '/mp/towns', { name: 'Bad', key: bad })).status, 400, String(bad));
  }
  assert.equal((await api('editor', 'POST', '/mp/towns', { name: 'Two', key: 'seed=two' })).status, 201);
  assert.equal((await api('editor', 'POST', '/mp/towns', { name: 'Three', key: 'seed=three' })).status, 201);
  assert.equal((await api('editor', 'POST', '/mp/towns', { name: 'Four', key: 'seed=four' })).status, 409);
});

test('a character plays on one device at a time; the others see where, and can join them', async () => {
  const me = (await api('couple', 'POST', '/mp/characters', { name: 'Me' })).json;
  const list = async (tab: string) => (await api('couple', 'GET', `/mp/characters?tab=${tab}`)).json.characters[0];
  const here = { ...town, town: 'seed=couple', character: me.id };
  const phone = await client('couple').joinOrCreate('city', { ...here, tab: 'phone' });
  phone.onMessage(MSG_SPAWN, () => {});

  const seen = await list('laptop');
  assert.deepEqual([seen.inUse, seen.town, seen.session], [true, 'seed=couple', phone.sessionId]);
  assert.equal((await list('phone')).inUse, false);

  // Another device can't have them, and the phone carries on.
  await assert.rejects(client('couple').joinOrCreate('city', { ...here, tab: 'laptop' }), new RegExp(IN_USE));
  await assert.rejects(client('couple').joinOrCreate('city', { ...here, town: 'seed=elsewhere', tab: 'laptop' }), new RegExp(IN_USE));
  assert.equal((await list('laptop')).session, phone.sessionId);

  // The same tab can: a reload's old connection is still there.
  const left = new Promise<number>((r) => phone.onLeave(r));
  const reloaded = await client('couple').joinOrCreate('city', { ...here, tab: 'phone' });
  reloaded.onMessage(MSG_SPAWN, () => {});
  assert.equal(await left, CLOSE_SIGNED_IN_ELSEWHERE);

  // Lost connection: not in use, so they aren't stuck for as long as it might come back.
  reloaded.reconnection.enabled = false;
  reloaded.connection.close(4010);
  for (let tries = 0; (await list('laptop')).inUse; tries++) {
    assert.ok(tries < 100, 'the drop was never noticed');
    await new Promise((r) => setTimeout(r, 20));
  }
  const laptop = await client('couple').joinOrCreate('city', { ...here, tab: 'laptop' });
  laptop.onMessage(MSG_SPAWN, () => {});
  assert.equal((await list('phone')).inUse, true);
  await laptop.leave();
  assert.deepEqual([(await list('phone')).inUse, (await list('phone')).town], [false, null]);
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
  // Nobody rides with someone on foot. (Refusals are half a second apart at least.)
  move(driver, 50.1, 50, 0, { anim: 1 });
  await new Promise((r) => setTimeout(r, 600));
  move(riders[3], 50.1, 50, 1, { anim: 5, ride: driver.sessionId });
  await until('a refusal for riding with a walker', () => corrected[4]() === 2);
  // Asking over and over (each answered straight back, in the new epoch) isn't a flood of refusals, or a kick.
  for (let i = 0; i < 40; i++) move(riders[3], 50.1, 50, 2, { anim: 5, ride: driver.sessionId });
  await new Promise((r) => setTimeout(r, 300));
  assert.ok(corrected[4]() <= 3, `${corrected[4]()} refusals`);
  assert.ok(riders[3].connection.isOpen, 'still connected');
  await Promise.all(rooms.map((r) => r.leave()));
});

test('the town is shared: one host sends its traffic, and night and taken cars are the same for everyone', async () => {
  const ids: string[] = [];
  for (const name of ['Host', 'Guest']) ids.push((await api('town', 'POST', '/mp/characters', { name })).json.id);
  const shared = { town: 'seed=shared', width: 128, height: 128 };
  const a = await client('town').joinOrCreate('city', { ...shared, character: ids[0] });
  a.onMessage(MSG_SPAWN, () => {});
  move(a, 20, 20);
  await until('a host', () => a.state.host === a.sessionId);
  const b = await client('town').joinOrCreate('city', { ...shared, character: ids[1] });
  b.onMessage(MSG_SPAWN, () => {});
  move(b, 21, 20);
  const worlds: { looks: string[]; cars: Uint8Array }[] = [];
  b.onMessage(MSG_WORLD, (m) => worlds.push(m));
  a.onMessage(MSG_WORLD, () => assert.fail('the host is sent its own world'));
  await new Promise((r) => setTimeout(r, 100));
  b.send(MSG_WORLD, { cars: new Uint8Array([9]), people: new Uint8Array(0), looks: [] }); // not the host: dropped
  a.send(MSG_WORLD, { cars: new Uint8Array([1, 2, 3]), people: new Uint8Array([4]), looks: ['people/x'] });
  await until('the host\'s world to reach the guest', () => worlds.length === 1);
  assert.deepEqual([...worlds[0].cars], [1, 2, 3]);
  assert.deepEqual(worlds[0].looks, ['people/x']);

  b.send(MSG_NIGHT, { on: true });
  b.send(MSG_TAKE, { car: 'p3' });
  b.send(MSG_TAKE, { car: 'p3' });
  a.send(MSG_TAKE, { car: 't12' });
  a.send(MSG_TAKE, { car: 'nonsense' });
  b.send(MSG_CAR, { model: 2, x: 22, z: 20, yaw: 0 });
  await until('night and the taken cars', () => a.state.night === true && a.state.taken.split(',').length === 2);
  assert.deepEqual(a.state.taken.split(',').sort(), ['p3', 't12']);
  await until('the guest\'s parked car', () => a.state.cars.get(b.sessionId)?.model === 2);

  // The host leaves: the guest takes over.
  await a.leave();
  await until('the guest to host', () => b.state.host === b.sessionId);
  await b.leave();
});

test('a quiet host hands the town to someone still playing, never to another quiet tab', async () => {
  const ids: string[] = [];
  for (const name of ['First', 'Second']) ids.push((await api('town', 'POST', '/mp/characters', { name })).json.id);
  const shared = { town: 'seed=quiet-host', width: 128, height: 128 };
  const a = await client('town').joinOrCreate('city', { ...shared, character: ids[0] });
  a.onMessage(MSG_SPAWN, () => {});
  move(a, 20, 20);
  await until('a host', () => a.state.host === a.sessionId);
  const b = await client('town').joinOrCreate('city', { ...shared, character: ids[1] });
  b.onMessage(MSG_SPAWN, () => {});
  b.onMessage(MSG_WORLD, () => {});
  a.onMessage(MSG_WORLD, () => {});
  // a's tab goes quiet (no world, no moves); b keeps playing, so b runs the town.
  const playing = setInterval(() => move(b, 21, 20), 300);
  try {
    await until('the playing guest to host', () => b.state.host === b.sessionId, 6000);
  } finally {
    clearInterval(playing);
  }
  // Now b goes quiet too: a is no better, so b stays on.
  await new Promise((r) => setTimeout(r, HOST_SILENT_MS + 600));
  assert.equal(b.state.host, b.sessionId, 'the town went to another quiet tab');
  // a comes back: a takes over from the quiet b.
  move(a, 20.5, 20);
  await until('the returning player to host', () => a.state.host === a.sessionId, 2000);
  await a.leave();
  await b.leave();
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

test('the county: towns have places, yours fill slots, and a room takes you out along its highways', async () => {
  const listed = (await api('driver', 'GET', '/mp/towns')).json;
  const places = Object.fromEntries(listed.towns.filter((t: { mine: boolean }) => !t.mine).map((t: { name: string; place: string }) => [t.name, t.place]));
  assert.deepEqual(places, { Porchlight: 'porchlight', Lanternport: 'east', 'Ember Crossing': 'south', 'Juniper Falls': 'west' });
  assert.ok(typeof listed.account === 'string');

  // Your towns go in your three places north of Porchlight, in order, and a freed place is the next one used.
  const made: { id: string; slot: number }[] = [];
  for (const name of ['Up', 'Over', 'Across']) made.push((await api('driver', 'POST', '/mp/towns', { name })).json);
  assert.deepEqual(made.map((t) => t.slot), [0, 1, 2]);
  assert.equal((await api('driver', 'DELETE', `/mp/towns/${made[1].id}`)).status, 200);
  assert.equal((await api('driver', 'POST', '/mp/towns', { name: 'Again' })).json.slot, 1);

  // The north room (the junction and your empty plots) is yours alone.
  const me = (await api('driver', 'POST', '/mp/characters', { name: 'Driver' })).json;
  const north = await client('driver').joinOrCreate('city', { town: `north:${listed.account}`, width: 48, height: 48, character: me.id });
  north.onMessage(MSG_SPAWN, () => {});
  const stranger = (await api('hitcher', 'POST', '/mp/characters', { name: 'Hitcher' })).json;
  await assert.rejects(client('hitcher').joinOrCreate('city', { town: `north:${listed.account}`, width: 48, height: 48, character: stranger.id }), /someone else/);
  await north.leave();

  // Driving into Porchlight off the east highway: they're where they say, out on the road.
  const porchlight = STARTER_TOWNS[0];
  const room = await client('driver').joinOrCreate('city', { town: porchlight.key, width: 256, height: 256, character: me.id, arrive: { x: 300, y: 0, z: 128.5, yaw: Math.PI } });
  const spawn = await new Promise<SpawnMessage>((r) => room.onMessage(MSG_SPAWN, r));
  assert.deepEqual(spawn.at && [spawn.at.x, spawn.at.z], [300, 128.5]);
  // On along the highway, at speed, and a good way: they drove on all the while they were joining.
  move(room, 275, 128.5);
  await until('the move out on the road', () => room.state.players?.get(room.sessionId)?.x === 275);
  // That was let off once: teleporting a long way up it is too fast, from where they got to.
  const corrected = new Promise<CorrectMessage>((r) => room.onMessage(MSG_CORRECT, r));
  move(room, 380, 128.5);
  assert.deepEqual([(await corrected).x], [275]);
  await room.leave();

  // Arriving somewhere that isn't the room's ground (most of the way to Lanternport) doesn't count.
  const far = await client('driver').joinOrCreate('city', { town: porchlight.key, width: 256, height: 256, character: me.id, arrive: { x: 620, y: 0, z: 128.5, yaw: 0 } });
  const farSpawn = await new Promise<SpawnMessage>((r) => far.onMessage(MSG_SPAWN, r));
  assert.deepEqual(farSpawn.at && [farSpawn.at.x, farSpawn.at.z], [275, 128.5]); // where they were saved, not where they said
  await far.leave();
});

test('night falls all over the county at once', async () => {
  const a = (await api('owl', 'POST', '/mp/characters', { name: 'Owl' })).json;
  const b = (await api('owl', 'POST', '/mp/characters', { name: 'Lark' })).json;
  const here = await client('owl').joinOrCreate('city', { town: STARTER_TOWNS[0].key, width: 256, height: 256, character: a.id });
  here.onMessage(MSG_SPAWN, () => {});
  const there = await client('owl').joinOrCreate('city', { town: STARTER_TOWNS[1].key, width: 224, height: 192, character: b.id });
  there.onMessage(MSG_SPAWN, () => {});
  await until('both rooms', () => here.state.night !== undefined && there.state.night !== undefined);
  const was = here.state.night;
  here.send(MSG_NIGHT, { on: !was });
  await until('night in the other town too', () => there.state.night === !was);
  here.send(MSG_NIGHT, { on: was });
  await until('and day again', () => there.state.night === was);
  await here.leave();
  await there.leave();
});

test('following someone from town to town: the roll call names their character, and where they are now is a request away', async () => {
  const leader = (await api('leader', 'POST', '/mp/characters', { name: 'Leader' })).json;
  const tag = (await api('tagalong', 'POST', '/mp/characters', { name: 'Tag' })).json;
  const first = { town: 'seed=follow-a', width: 128, height: 128 };
  const a = await client('leader').joinOrCreate('city', { ...first, character: leader.id });
  a.onMessage(MSG_SPAWN, () => {});
  const b = await client('tagalong').joinOrCreate('city', { ...first, character: tag.id });
  b.onMessage(MSG_SPAWN, () => {});
  let dots: unknown[][] = [];
  b.onMessage(MSG_DOTS, (d: unknown[][]) => (dots = d));
  move(a, 20, 30);
  await until('the roll call to name the leader', () => dots.some((d) => d[0] === a.sessionId && d[4] === leader.id), 4000);

  const here = await api('tagalong', 'GET', `/mp/players/${leader.id}`);
  assert.equal(here.status, 200);
  assert.equal(here.json.town, 'seed=follow-a');
  assert.equal(here.json.session, a.sessionId);
  assert.equal(here.json.name, 'Leader');
  assert.equal(typeof here.json.x, 'number');

  // Off to another town: a new session there, and the same request finds them.
  await a.leave();
  await until('the leader to be gone', () => !dots.some((d) => d[0] === a.sessionId), 4000);
  assert.equal((await api('tagalong', 'GET', `/mp/players/${leader.id}`)).status, 404);
  const a2 = await client('leader').joinOrCreate('city', { town: 'seed=follow-b', width: 128, height: 128, character: leader.id });
  a2.onMessage(MSG_SPAWN, () => {});
  move(a2, 40, 50);
  let there = await api('tagalong', 'GET', `/mp/players/${leader.id}`);
  const end = Date.now() + 3000;
  while (there.status !== 200 && Date.now() < end) {
    await new Promise((r) => setTimeout(r, 50));
    there = await api('tagalong', 'GET', `/mp/players/${leader.id}`);
  }
  assert.equal(there.json.town, 'seed=follow-b');
  assert.equal(there.json.session, a2.sessionId);
  assert.equal((await api('tagalong', 'GET', '/mp/players/nobody')).status, 404);
  assert.equal((await fetch(`${base}/mp/players/${leader.id}`)).status, 401);
  await a2.leave();
  await b.leave();
});

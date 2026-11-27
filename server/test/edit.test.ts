// The street editor's routes end to end (edit.ts): a real server on a spare port, a stand-in for the draft
// worker, and SDK clients playing in the town being edited.

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { Client } from '@colyseus/sdk';
import { SESSION_COOKIE } from '../src/app.ts';
import { seal } from '../src/cookies.ts';
import { startGameServer, type GameServer } from '../src/multiplayer/index.ts';
import { MemoryPersistence, STARTER_TOWNS, rehouse, revOf, starterTowns, withRev } from '../src/multiplayer/persistence.ts';
import { CLOSE_TOWN_REBUILT, MSG_NOTICE, MSG_SPAWN, MSG_TOWN_REBUILDING } from '../src/multiplayer/protocol.ts';
import type { DraftAnswer, DraftClient } from '../src/sim/draft.ts';

const SECRET = 'x'.repeat(40);
const APP = 'http://localhost:5173';
const persistence = new MemoryPersistence();
let game: GameServer;
let base = '';

const PORCHLIGHT = STARTER_TOWNS[0];
const [A, B] = PORCHLIGHT.houses;
/** The new revision's houses: every one of the town's but A's. */
const NEW_HOUSES = PORCHLIGHT.houses.slice(1);

/** The draft worker, as the routes see it: what it's asked, and answers made up to suit. */
const asked: Record<string, unknown>[] = [];
let planProblems: { level: string; text: string; at: number[] | null }[] = [];
const fakeDraft: DraftClient = {
  async request(req): Promise<DraftAnswer> {
    asked.push(req);
    if (req.op === 'plan') return { ok: true, ms: 5, town: { roads: [] }, problems: planProblems, changes: req.base ? { housesLost: 1 } : null };
    if (req.op === 'houses') return { ok: true, houses: NEW_HOUSES };
    if (req.op === 'day') return { ok: true, ms: 9, day: { trips: req.skeleton ? 2 : 1 } };
    return { ok: false, error: 'no such op' };
  },
  close() {},
};

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

const q = (key: string) => encodeURIComponent(key);

before(async () => {
  const editor = await persistence.account('editor', 'Person editor');
  game = await startGameServer({
    port: 0,
    appUrl: APP,
    sessionSecret: SECRET,
    persistence,
    handleSignals: false,
    draft: fakeDraft,
    editors: editor.id,
    rebuildSeconds: 0.3,
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

test('someone not in EDITORS can see the towns but not edit the ones that come with the game; their own, they can', async () => {
  const theirs = (await api('stranger', 'GET', '/mp/edit/towns')).json;
  assert.equal(theirs.find((t: { key: string }) => t.key === PORCHLIGHT.key).editable, false);
  assert.equal((await api('editor', 'GET', '/mp/edit/towns')).json.find((t: { key: string }) => t.key === PORCHLIGHT.key).editable, true);

  assert.equal((await api('stranger', 'GET', `/mp/edit/draft?town=${q(PORCHLIGHT.key)}`)).status, 403);
  assert.equal((await api('stranger', 'PUT', '/mp/edit/draft', { town: PORCHLIGHT.key, baseRev: 0, skeleton: {}, commands: [] })).status, 403);
  assert.equal((await api('stranger', 'POST', '/mp/edit/plan', { town: PORCHLIGHT.key, skeleton: {}, seq: 1 })).status, 403);
  assert.equal((await api('stranger', 'POST', '/mp/edit/commit', { town: PORCHLIGHT.key, skeleton: {}, message: 'mine now' })).status, 403);
  assert.equal((await api('stranger', 'GET', `/mp/edit/history?town=${q(PORCHLIGHT.key)}`)).status, 403);
  assert.equal(asked.length, 0, 'the worker was never asked');
  assert.equal((await fetch(`${base}/mp/edit/towns`)).status, 401);

  // A town they made is theirs to edit, and nobody else's (not even an editor's: it's private).
  const made = await api('stranger', 'POST', '/mp/towns', { name: 'Elsewhere', key: 'seed=elsewhere' });
  assert.equal(made.status, 201);
  assert.equal((await api('stranger', 'GET', `/mp/edit/draft?town=${q('seed=elsewhere')}`)).status, 200);
  assert.equal((await api('editor', 'GET', `/mp/edit/draft?town=${q('seed=elsewhere')}`)).status, 404);
  // A revision of a town is that town: its key can't be made again.
  assert.equal((await api('editor', 'POST', '/mp/towns', { name: 'Copy', key: 'seed=elsewhere&rev=2' })).status, 409);
});

test('drafts: made from the town as it is, kept, and thrown away', async () => {
  const fresh = (await api('editor', 'GET', `/mp/edit/draft?town=${q(PORCHLIGHT.key)}`)).json;
  assert.equal(fresh.town, PORCHLIGHT.key);
  assert.equal(fresh.baseRev, 0);
  assert.deepEqual(fresh.skeleton, { v: 1 });
  assert.deepEqual(fresh.commands, []);
  assert.equal(fresh.stale, false);
  assert.deepEqual(fresh.current, { rev: 0, skeleton: null });

  const skeleton = { v: 1, center: [140.5, 120.5] };
  const put = await api('editor', 'PUT', '/mp/edit/draft', { town: PORCHLIGHT.key, baseRev: 0, skeleton, commands: [{ op: 'centre', to: [140.5, 120.5] }] });
  assert.equal(put.status, 200);
  assert.ok(Date.parse(put.json.updatedAt) > 0);
  const kept = (await api('editor', 'GET', `/mp/edit/draft?town=${q(PORCHLIGHT.key)}`)).json;
  assert.deepEqual(kept.skeleton, skeleton);
  assert.equal(kept.commands.length, 1);
  assert.equal((await api('editor', 'PUT', '/mp/edit/draft', { town: PORCHLIGHT.key, baseRev: -1, skeleton })).status, 400);
  assert.equal((await api('editor', 'PUT', '/mp/edit/draft', { town: PORCHLIGHT.key, baseRev: 0, skeleton: [1, 2] })).status, 400);

  assert.equal((await api('editor', 'DELETE', `/mp/edit/draft?town=${q(PORCHLIGHT.key)}`)).status, 200);
  assert.deepEqual((await api('editor', 'GET', `/mp/edit/draft?town=${q(PORCHLIGHT.key)}`)).json.skeleton, { v: 1 });

  // A plan is the worker's, against the town as it is, with the editor's seq back.
  const plan = await api('editor', 'POST', '/mp/edit/plan', { town: PORCHLIGHT.key, skeleton, seq: 7 });
  assert.equal(plan.status, 200);
  assert.equal(plan.json.seq, 7);
  assert.equal(plan.json.ok, true);
  assert.deepEqual(asked.at(-1), { op: 'plan', key: PORCHLIGHT.key, skeleton, base: { key: PORCHLIGHT.key, skeleton: null } });
  const day = await api('editor', 'POST', '/mp/edit/day', { town: PORCHLIGHT.key, skeleton });
  assert.deepEqual(day.json, { draft: { trips: 2 }, live: { trips: 1 } });
});

test('households are rehoused to the nearest free house, north to south', () => {
  const moves = rehouse(
    [
      { key: 'a', x: 0, y: 0 },
      { key: 'b', x: 10, y: 0 },
      { key: 'c', x: 0, y: 1 },
    ],
    [
      [10, 0],
      [1, 0],
      [5, 5],
    ],
  );
  assert.deepEqual([...moves], [
    ['a', [1, 0]],
    ['c', [5, 5]],
  ]);
  assert.deepEqual([...rehouse([{ key: 'x', x: 0, y: 0 }], [])], [['x', null]]);
});

test('commit: errors turn it down; otherwise a revision, the key bumped, households rehoused by tile, and the room told then sent over', async () => {
  // Two households in Porchlight: one in house A (gone in the new revision), one in B (still there).
  const town = (await api('resident', 'GET', '/mp/towns')).json.towns.find((t: { key: string }) => t.key === PORCHLIGHT.key);
  const one = (await api('resident', 'POST', '/mp/characters', { name: 'One' })).json;
  const two = (await api('neighbour', 'POST', '/mp/characters', { name: 'Two' })).json;
  for (const [who, c, [x, y]] of [['resident', one, A], ['neighbour', two, B]] as const) {
    assert.equal((await api(who, 'PUT', `/mp/characters/${c.id}/town`, { town: town.id })).status, 200);
    assert.equal((await api(who, 'PUT', `/mp/characters/${c.id}/home`, { x, y })).status, 200);
  }
  const room = await client('resident').joinOrCreate('city', { town: PORCHLIGHT.key, width: 256, height: 256, character: one.id });
  room.onMessage(MSG_SPAWN, () => {});
  const events: string[] = [];
  let rebuilding: any = null;
  room.onMessage(MSG_TOWN_REBUILDING, (m) => {
    rebuilding = m;
    events.push('rebuilding');
  });
  let leftWith: { code: number; reason?: string } | null = null;
  room.onLeave((code: number, reason?: string) => {
    leftWith = { code, reason };
    events.push('left');
  });
  await until('the room to have them', () => persistence.sessions.size > 0);
  // Somewhere in town, so there's a place of theirs to carry over.
  room.send('move', { seq: 1, t: Math.round(performance.now()), epoch: 0, x: 100, y: 0, z: 100, yaw: 0, anim: 1, car: 0, ride: '' });
  await until('them to be placed', () => room.state.players?.get(room.sessionId)?.x === 100);
  const skeleton = { v: 1, center: [140.5, 120.5] };

  planProblems = [{ level: 'error', text: 'two roads meet at 20°', at: [10, 10] }];
  const refused = await api('editor', 'POST', '/mp/edit/commit', { town: PORCHLIGHT.key, skeleton, message: 'too sharp' });
  assert.equal(refused.status, 409);
  assert.equal(refused.json.problems[0].text, 'two roads meet at 20°');
  assert.equal(revOf((await persistence.townOf(PORCHLIGHT.key))!.key), 0, 'nothing kept');

  planProblems = [{ level: 'warning', text: '1 house(s) lost', at: [A[0] + 0.5, A[1] + 0.5] }];
  const newKey = withRev(PORCHLIGHT.key, 1);
  const committed = await api('editor', 'POST', '/mp/edit/commit', { town: PORCHLIGHT.key, skeleton, message: 'The high street moves east' });
  assert.equal(committed.status, 200, JSON.stringify(committed.json));
  assert.deepEqual(committed.json, { key: newKey, rev: 1, rehoused: 1, players: 1 });
  assert.deepEqual(asked.slice(-2).map((r) => [r.op, r.full ?? false]), [['plan', true], ['houses', false]]);

  // The revision, its skeleton by its key, and the town's key now.
  assert.equal((await persistence.townOf(PORCHLIGHT.key))!.key, newKey);
  assert.equal(starterTowns()[0].key, newKey);
  const history = (await api('editor', 'GET', `/mp/edit/history?town=${q(newKey)}`)).json;
  assert.deepEqual(history.map((h: { rev: number; message: string; by: string }) => [h.rev, h.message, h.by]), [[1, 'The high street moves east', 'Person editor']]);
  assert.deepEqual((await api('resident', 'GET', `/mp/towns/skeleton?key=${q(newKey)}`)).json, { key: newKey, rev: 1, skeleton });
  assert.equal((await api('resident', 'GET', `/mp/towns/skeleton?key=${q(PORCHLIGHT.key)}`)).status, 404);
  // The editor's draft went with the commit; a new one is of revision 1.
  const draft = (await api('editor', 'GET', `/mp/edit/draft?town=${q(newKey)}`)).json;
  assert.deepEqual([draft.baseRev, draft.skeleton, draft.stale], [1, skeleton, false]);

  // House A is gone: its household is in the nearest free house; B's stays.
  const movedTo = persistence.characters.get(one.id)!.house!;
  const d2 = (h: readonly number[]) => (h[0] - A[0]) ** 2 + (h[1] - A[1]) ** 2;
  const nearest = Math.min(...NEW_HOUSES.filter((h) => h !== B).map(d2));
  assert.equal(d2([movedTo.x, movedTo.y]), nearest);
  assert.deepEqual(persistence.characters.get(two.id)!.house, { x: B[0], y: B[1] });
  // Claims are checked against the revision's houses now.
  assert.equal((await api('neighbour', 'PUT', `/mp/characters/${two.id}/home`, { x: A[0], y: A[1] })).status, 404);

  // The room: told, then (0.3 s on) saved and sent to the new key.
  await until('the room to say it is rebuilding, then close', () => leftWith !== null);
  assert.deepEqual(events, ['rebuilding', 'left']);
  assert.deepEqual(rebuilding, { name: PORCHLIGHT.name, by: 'Person editor', seconds: 0.3, key: newKey });
  assert.equal(leftWith!.code, CLOSE_TOWN_REBUILT);
  if (leftWith!.reason !== undefined) assert.equal(leftWith!.reason, "rev=1");
  await until('their place to carry over to the new key', () => persistence.states.get(one.id)?.town === newKey);

  // Joining the new key's room, they're told their house moved, once.
  const notice = new Promise<{ text: string }>((r) => {
    void client('resident')
      .joinOrCreate('city', { town: newKey, width: 256, height: 256, character: one.id })
      .then((again) => {
        again.onMessage(MSG_SPAWN, () => {});
        again.onMessage(MSG_NOTICE, r);
      });
  });
  assert.match((await notice).text, /house is gone/);
  assert.deepEqual(await persistence.takeNotices(one.id), []);

  // Committing against a key that's moved on is turned down.
  assert.equal((await api('editor', 'POST', '/mp/edit/commit', { town: newKey, skeleton, message: 'again' })).status, 200);
  const stale = await persistence.commit({ townId: town.id, key: newKey, skeleton, houses: NEW_HOUSES, message: 'late', by: 'x' }).catch((e: Error) => e);
  assert.ok(stale instanceof Error && /changed this town first/.test(stale.message));
});

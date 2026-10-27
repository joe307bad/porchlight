// PgPersistence against a real Postgres. Skipped unless TEST_DATABASE_URL is
// set (the dev database will do: `npm run test:db` here).

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { PgPersistence } from '../src/multiplayer/persistence.ts';

const url = process.env.TEST_DATABASE_URL;
const db = url ? new PgPersistence(url) : null;
const skip = !db && 'TEST_DATABASE_URL is not set';
const sub = `test-${crypto.randomUUID()}`;

before(async () => {
  if (!db) return;
  db.start();
  const end = Date.now() + 15_000;
  while (!db.ready && Date.now() < end) await new Promise((r) => setTimeout(r, 100));
  assert.ok(db.ready, 'database never became ready');
});

after(async () => {
  await db?.close();
});

test('characters, saves guarded by version, sessions', { skip }, async () => {
  const p = db!;
  const account = await p.account(sub, 'Tester');
  assert.equal((await p.account(sub, 'Tester Renamed')).id, account.id);
  const kid = await p.createCharacter(account.id, 'Kid', 'characters/character-female-b');
  const dad = await p.createCharacter(account.id, 'Dad', null);
  await assert.rejects(p.createCharacter(account.id, 'Kid', null), /already have/);
  assert.deepEqual((await p.listCharacters(account.id)).map((c) => [c.name, c.look]), [['Kid', 'characters/character-female-b'], ['Dad', null]]);
  assert.equal(await p.character(account.id, crypto.randomUUID()), null);
  assert.equal(await p.character(account.id, 'not-a-uuid'), null);

  const base = { town: 'seed=t', y: 0, rotY: 1, look: '' };
  await p.save([
    { ...base, characterId: kid.id, x: 5, z: 6, version: 2 },
    { ...base, characterId: dad.id, x: 7, z: 8, version: 1, look: 'characters/character-male-c' },
  ]);
  await p.save([{ ...base, characterId: kid.id, x: 99, z: 99, version: 1 }]); // stale: ignored
  const k = await p.loadState(kid.id);
  assert.deepEqual([k?.town, k?.x, k?.z, k?.look, k?.version], ['seed=t', 5, 6, 'characters/character-female-b', 2]);
  assert.equal((await p.loadState(dad.id))?.look, 'characters/character-male-c');

  const s = await p.openSession(kid.id, 'room1');
  await p.closeSession(s, kid.id, 'consented', 42);
  await p.closeSession(s, kid.id, 'timeout', 42); // already closed: no double count
  assert.equal(await p.deleteCharacter(account.id, dad.id), true);
  assert.equal(await p.deleteCharacter('00000000-0000-0000-0000-000000000000', kid.id), false);
});

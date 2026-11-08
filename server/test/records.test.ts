// A mission's best times in each town (Marathon Runner): one per account, its
// fastest, under the name of the character who ran it. Against the in-memory
// store always, and Postgres too when TEST_DATABASE_URL is set.

import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { cleanSeconds, MemoryPersistence, PgPersistence, type Persistence } from '../src/multiplayer/persistence.ts';

const url = process.env.TEST_DATABASE_URL;
const pg = url ? new PgPersistence(url) : null;

after(async () => {
  await pg?.close();
});

async function ready(p: Persistence) {
  p.start();
  const end = Date.now() + 15_000;
  while (!p.ready && Date.now() < end) await new Promise((r) => setTimeout(r, 100));
  assert.ok(p.ready, 'never became ready');
}

async function boards(p: Persistence) {
  await ready(p);
  const run = crypto.randomUUID().slice(0, 8);
  const town = `seed=records${run}`;
  const other = `seed=other${run}`;
  const ada = await p.account(`test-ada-${run}`, 'Ada');
  const bo = await p.account(`test-bo-${run}`, 'Bo');
  const adaKid = await p.createCharacter(ada.id, 'Kid', null);
  const adaMum = await p.createCharacter(ada.id, 'Mum', null);
  const boMe = await p.createCharacter(bo.id, 'Bo', null);

  assert.deepEqual(await p.records(ada.id, town, 'marathon'), { best: [], mine: null });
  let b = await p.keepRecord(ada.id, adaKid.id, town, 'marathon', 300);
  assert.equal(b.before, null);
  assert.equal(b.mine, 300);
  // Slower: kept as it was.
  b = await p.keepRecord(ada.id, adaMum.id, town, 'marathon', 320);
  assert.equal(b.before, 300);
  assert.equal(b.mine, 300);
  assert.deepEqual(b.best.map((r) => [r.name, r.seconds, r.mine]), [['Kid', 300, true]]);
  // Faster, by another of the account's characters: one row, theirs now.
  b = await p.keepRecord(ada.id, adaMum.id, town, 'marathon', 250.5);
  assert.equal(b.before, 300);
  assert.deepEqual(b.best.map((r) => [r.name, r.seconds]), [['Mum', 250.5]]);
  // Someone else, faster still: on top, and not Ada's.
  await p.keepRecord(bo.id, boMe.id, town, 'marathon', 200);
  const board = await p.records(ada.id, town, 'marathon');
  assert.deepEqual(board.best.map((r) => [r.name, r.seconds, r.mine]), [['Bo', 200, false], ['Mum', 250.5, true]]);
  assert.equal(board.mine, 250.5);
  // Each town (and mission) has its own board.
  assert.deepEqual(await p.records(ada.id, other, 'marathon'), { best: [], mine: null });
  assert.deepEqual(await p.records(ada.id, town, 'chase'), { best: [], mine: null });
  // Only your own characters run under your account.
  await assert.rejects(p.keepRecord(ada.id, boMe.id, town, 'marathon', 100), /No such character/);
}

test('best times, in memory', () => boards(new MemoryPersistence()));
test('best times, in Postgres', { skip: !pg && 'TEST_DATABASE_URL is not set' }, () => boards(pg!));

test('a time to keep is a real number of seconds', () => {
  assert.equal(cleanSeconds(83.456), 83.46);
  for (const bad of [0, -5, Number.NaN, Infinity, '60', null, 7 * 3600]) assert.equal(cleanSeconds(bad), null);
});

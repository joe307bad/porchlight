// PgPersistence against a real Postgres. Skipped unless TEST_DATABASE_URL is
// set (the dev database will do: `npm run test:db` here).

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { PgPersistence } from '../src/multiplayer/persistence.ts';
import { towns } from '../src/multiplayer/tables.ts';

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

  // Unlocks are the account's, kept once each; every character has them.
  assert.deepEqual(await p.unlocks(account.id), []);
  await p.unlock(account.id, 'animals');
  await p.unlock(account.id, 'animals');
  assert.deepEqual(await p.unlocks(account.id), ['animals']);

  // Missions can be played again; who finished first stays.
  await p.completeMission(account.id, kid.id, 'chase');
  await p.completeMission(account.id, dad.id, 'chase');
  const progress = await p.progress(account.id);
  assert.deepEqual(progress.completions.map((c) => [c.missionId, c.times, c.firstCharacter]), [['chase', 2, 'Kid']]);
  assert.deepEqual(progress.unlocks.map((u) => u.what), ['animals']);

  const s = await p.openSession(kid.id, 'room1');
  await p.closeSession(s, kid.id, 'consented', 42);
  await p.closeSession(s, kid.id, 'timeout', 42); // already closed: no double count
  assert.equal(await p.deleteCharacter(account.id, dad.id), true);
  assert.equal(await p.deleteCharacter('00000000-0000-0000-0000-000000000000', kid.id), false);
});

test('towns: three at most, and houses: one account, two people, free again when empty', { skip }, async () => {
  const p = db!;
  const me = await p.account(`${sub}-towns`, 'Mayor');
  const them = await p.account(`${sub}-other`, 'Other');
  const made: Awaited<ReturnType<typeof p.createTown>>[] = [];
  for (const name of ['One', 'Two', 'Three']) made.push(await p.createTown(me.id, name));
  await assert.rejects(p.createTown(me.id, 'Four'), /Delete one/);
  const listed = await p.listTowns(me.id);
  assert.ok(listed.some((t) => t.name === 'Porchlight' && !t.mine));
  assert.equal(listed.filter((t) => t.mine).length, 3);

  const [a, b, c] = await Promise.all(['A', 'B', 'C'].map((n) => p.createCharacter(me.id, `${n}-${made[0].id.slice(0, 6)}`, null)));
  const stranger = await p.createCharacter(them.id, 'Stranger', null);
  await assert.rejects(p.claimHome(me.id, a.id, 5, 6), /Choose a town/);
  for (const ch of [a, b, c]) await p.moveToTown(me.id, ch.id, made[0].id);
  await p.moveToTown(them.id, stranger.id, made[0].id);
  await assert.rejects(p.moveToTown(them.id, a.id, made[0].id), /No such character/);
  // Everyone who's moved there counts, whoever's account they're on (and only there).
  const counted = await p.listTowns(them.id);
  assert.equal(counted.find((t) => t.id === made[0].id)?.residents, 4);
  assert.equal(counted.find((t) => t.id === made[1].id)?.residents, 0);

  await p.claimHome(me.id, a.id, 5, 6);
  await p.claimHome(me.id, b.id, 5, 6);
  await p.claimHome(me.id, a.id, 5, 6); // already there
  await assert.rejects(p.claimHome(me.id, c.id, 5, 6), /already has 2/);
  await assert.rejects(p.claimHome(them.id, stranger.id, 5, 6), /Someone else/);
  assert.deepEqual((await p.character(me.id, a.id))?.home?.house, { x: 5, y: 6 });
  assert.equal((await p.character(me.id, a.id))?.home?.townName, 'One');
  const homes = await p.listHomes(them.id, made[0].id);
  assert.deepEqual(homes.map((h) => [h.x, h.y, h.mine, h.residents.length]), [[5, 6, false, 2]]);

  // Everyone out: the house is anyone's. (A moves house, B moves town.)
  await p.claimHome(me.id, a.id, 7, 7);
  await p.moveToTown(me.id, b.id, made[1].id);
  assert.equal((await p.character(me.id, b.id))?.home?.house, null);
  await p.claimHome(them.id, stranger.id, 5, 6);
  assert.deepEqual((await p.listHomes(me.id, made[0].id)).map((h) => [h.x, h.mine]).sort(), [[5, false], [7, true]]);

  // A town from the editor: its own key, and only one town to a key (the limit's full: someone else tries).
  const edited = await p.createTown(them.id, 'Edited', `seed=edited-${made[0].id}&width=96`);
  assert.equal(edited.key, `seed=edited-${made[0].id}&width=96`);
  await assert.rejects(p.createTown(them.id, 'Copy', edited.key), /called Edited/);

  // Deleting a town: only its maker; those who lived there choose again; a free slot for another.
  await assert.rejects(p.ownTownKey(them.id, made[0].id), /Only whoever/);
  await assert.rejects(p.deleteTown(them.id, made[0].id), /No such town/);
  await p.deleteTown(me.id, made[0].id);
  assert.equal((await p.character(them.id, stranger.id))?.home, null);
  assert.equal((await p.createTown(me.id, 'Four')).name, 'Four');

  // Deleting someone's last resident frees the house.
  await p.moveToTown(me.id, c.id, made[1].id);
  await p.claimHome(me.id, c.id, 1, 1);
  assert.equal(await p.deleteCharacter(me.id, c.id), true);
  assert.deepEqual(await p.listHomes(me.id, made[1].id), []);
});

test('a starter town that no longer comes with the game goes on the next start, and those who lived there choose again', { skip }, async () => {
  const p = db!;
  const me = await p.account(`${sub}-retired`, 'Retired');
  const key = `seed=retired-${crypto.randomUUID()}`;
  const [old] = await (p as unknown as { db: { insert: (t: typeof towns) => { values: (v: object) => { returning: () => Promise<{ id: string }[]> } } } }).db.insert(towns).values({ name: 'Old', key }).returning();
  const c = await p.createCharacter(me.id, 'Stays', null);
  await p.moveToTown(me.id, c.id, old.id);
  const next = new PgPersistence(url!);
  next.start();
  const end = Date.now() + 15_000;
  while (!next.ready && Date.now() < end) await new Promise((r) => setTimeout(r, 100));
  assert.ok(next.ready);
  assert.ok(!(await p.listTowns(me.id)).some((t) => t.key === key));
  assert.equal((await p.character(me.id, c.id))?.home, null);
  assert.equal((await p.listTowns(me.id)).filter((t) => t.name === 'Porchlight').length, 1);
  await next.close();
});

test('regenerating a town that comes with the game gives everyone a new house, households together', { skip }, async () => {
  const p = db!;
  const id = crypto.randomUUID();
  const town = { name: `Town ${id}`, key: `seed=first-${id}`, houses: [[1, 1], [2, 2], [3, 3]] as [number, number][] };
  await p.settleStarterTown(town);
  const me = await p.account(`${sub}-rehoused`, 'Me');
  const them = await p.account(`${sub}-neighbour`, 'Them');
  const placed = (await p.listTowns(me.id)).find((t) => t.key === town.key)!;
  const [a, b, c] = await Promise.all(['A', 'B', 'NoHouse'].map((n) => p.createCharacter(me.id, n, null)));
  const d = await p.createCharacter(them.id, 'D', null);
  for (const ch of [a, b, c]) await p.moveToTown(me.id, ch.id, placed.id);
  await p.moveToTown(them.id, d.id, placed.id);
  await p.claimHome(me.id, a.id, 1, 1);
  await p.claimHome(me.id, b.id, 1, 1);
  await p.claimHome(them.id, d.id, 2, 2);
  const base = { town: town.key, y: 0, rotY: 1, look: '', x: 5, z: 6, version: 1 };
  await p.save([{ ...base, characterId: a.id }]);

  // The same again: nothing moves.
  await p.settleStarterTown(town);
  assert.deepEqual((await p.character(me.id, a.id))?.home?.house, { x: 1, y: 1 });
  assert.equal((await p.loadState(a.id))?.town, town.key);

  // Regenerated, under a new key: same town, new houses, and nobody standing anywhere.
  const next = { ...town, key: `seed=second-${id}`, houses: [[7, 7], [8, 8]] as [number, number][] };
  await p.settleStarterTown(next);
  const [ha, hb, hc, hd] = await Promise.all([p.character(me.id, a.id), p.character(me.id, b.id), p.character(me.id, c.id), p.character(them.id, d.id)]);
  assert.deepEqual([ha?.home?.townId, ha?.home?.townKey], [placed.id, next.key]);
  assert.deepEqual(ha?.home?.house, hb?.home?.house);
  assert.notDeepEqual(ha?.home?.house, hd?.home?.house);
  for (const h of [ha, hd]) assert.ok(next.houses.some(([x, y]) => x === h?.home?.house?.x && y === h?.home?.house?.y));
  assert.equal(hc?.home?.house, null);
  assert.equal(hc?.home?.townId, placed.id);
  assert.equal((await p.loadState(a.id))?.town, '');
  assert.equal((await p.listHomes(me.id, placed.id)).length, 2);

  // More households than houses: whoever doesn't fit chooses again.
  await p.settleStarterTown({ ...next, houses: [[9, 9]] });
  const homed = [(await p.character(me.id, a.id))?.home?.house, (await p.character(them.id, d.id))?.home?.house];
  assert.equal(homed.filter(Boolean).length, 1);
});

// The sidebar's breadcrumbs (src/crumbs.ts): where each click takes it, going back,
// and each town picking up as it was left.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { COUNTY, Memory, PAGE, TABS, backTo, crumbLabel, intoTown, onTo, onToBuilding, population, tabEntities } from '../src/crumbs.ts';
import { ENTITY_KINDS, type TownLife } from '../src/life.ts';

const names = (k: string) => ({ a: 'Lanternport', b: 'Porchlight' })[k] ?? k;
const words = (c: ReturnType<typeof intoTown>) => c.map((x) => crumbLabel(x, names)).join(' › ');

test('a town, then someone in it from its list, then their car: a crumb each', () => {
  let c = intoTown('a');
  assert.equal(words(c), 'County › Lanternport');
  c = onTo(c, { town: 'a', kind: 'person', id: 40 });
  assert.equal(words(c), 'County › Lanternport › Person 40');
  c = onTo(c, { town: 'a', kind: 'car', id: 12 });
  assert.equal(words(c), 'County › Lanternport › Person 40 › Car 12');
  assert.equal(onTo(c, { town: 'a', kind: 'car', id: 12 }), c, 'the same one again: no new crumb');
});

test('clicked on the map, or in another town: the county, their town, them', () => {
  const deep = onTo(onTo(intoTown('a'), { town: 'a', kind: 'person', id: 40 }), { town: 'a', kind: 'car', id: 12 });
  assert.equal(words(onTo(deep, { town: 'a', kind: 'car', id: 3 }, true)), 'County › Lanternport › Car 3');
  assert.equal(words(onTo(intoTown('a'), { town: 'b', kind: 'car', id: 3 })), 'County › Porchlight › Car 3');
  assert.equal(words(onTo([COUNTY], { town: 'b', kind: 'person', id: 1 })), 'County › Porchlight › Person 1');
  assert.equal(words(onToBuilding('a', 7)), 'County › Lanternport › Building');
  assert.equal(words(onTo(onToBuilding('a', 7), { town: 'a', kind: 'person', id: 2 })), 'County › Lanternport › Building › Person 2');
});

test('going back drops the crumbs after it, never the county', () => {
  const deep = onTo(onTo(intoTown('a'), { town: 'a', kind: 'person', id: 40 }), { town: 'a', kind: 'car', id: 12 });
  assert.equal(words(backTo(deep, 2)), 'County › Lanternport › Person 40');
  assert.equal(words(backTo(deep, 1)), 'County › Lanternport');
  assert.equal(words(backTo(deep, 0)), 'County');
  assert.equal(words(backTo(deep, -3)), 'County');
});

test('a town picks up as it was left: its tab, hour, how far down and who was clicked', () => {
  const m = new Memory();
  assert.deepEqual(m.of('a'), { tab: TABS[0].id, ago: 0, shown: PAGE, picked: null }, 'first time: the first tab, this hour');
  m.set('a', { tab: 'vans' });
  m.set('a', { ago: 5, shown: PAGE * 2, picked: 'car|12' });
  assert.deepEqual(m.of('a'), { tab: 'vans', ago: 5, shown: PAGE * 2, picked: 'car|12' }, 'as left');
  assert.deepEqual(m.of('b'), { tab: TABS[0].id, ago: 0, shown: PAGE, picked: null }, 'each town its own');
  m.set('a', { tab: 'people' });
  assert.equal(m.of('a').shown, PAGE, 'another tab starts at its first page');
  assert.equal(m.of('a').ago, 5, 'and keeps the hour');
});

test('every kind of car and person is under one tab, and a tab lists its own', () => {
  const kinds = TABS.flatMap((t) => t.kinds).sort();
  assert.deepEqual(kinds, ENTITY_KINDS.map((k) => k.id).sort());
  const life = { town: 'a', summary: { people: 1234 }, kinds: { cars: 'rcvbp-r', people: 'wx-s' } } as unknown as TownLife;
  assert.deepEqual(tabEntities(life, 'cars').map((e) => `${e.of}|${e.slot}`), ['car|0', 'car|6', 'car|1'], "residents' cars, then commuters'");
  assert.deepEqual(tabEntities(life, 'people').map((e) => `${e.of}|${e.slot}`), ['person|0', 'person|3', 'person|1']);
  assert.equal(tabEntities(life, 'boats').length, 0);
  assert.equal(population(life), 1234);
  assert.equal(population(undefined), null);
});

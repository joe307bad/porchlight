// The county as the server lays it out (src/county.ts) is the game's
// (Shared/County/CountyMap.cs): the same spaces, highways and rooms, checked
// against the vectors `Checks county` writes.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { County, keySize, type PlacedTown } from '../src/county.ts';

interface Case {
  name: string;
  account: string;
  towns: PlacedTown[];
  spaces: [string, number, number, number, number][];
  highways: number[];
  rooms: [number, number, string][];
}

const vectors = JSON.parse(readFileSync(new URL('./county-vectors.json', import.meta.url), 'utf8')) as { cases: Case[] };

for (const c of vectors.cases) {
  test(`the county with ${c.name} of your own towns is laid out as the game lays it out`, () => {
    const county = new County(c.towns, c.account);
    assert.deepEqual(county.spaces.map((s) => [s.kind, s.x0, s.y0, s.width, s.height]), c.spaces);
    assert.equal(county.highways.length, c.highways.length);
    county.highways.forEach((h, i) => assert.ok(Math.abs(h.length - c.highways[i]) < 0.01, `highway ${i}: ${h.length} vs ${c.highways[i]}`));
    const wrong = c.rooms.filter(([x, y, room]) => county.roomAt(x, y) !== room);
    assert.deepEqual(wrong, []);
  });
}

test("a key's size reads as the game reads it", () => {
  assert.equal(keySize('seed=abc', 'width'), 128);
  assert.equal(keySize('seed=abc&width=224&height=192', 'width'), 224);
  assert.equal(keySize('seed=abc&width=224&height=192', 'height'), 192);
  assert.equal(keySize('seed=abc&width=999', 'width'), 256);
  assert.equal(keySize('seed=abc&width=40', 'width'), 48);
});

test('a room owns its town, the near half of its highways, and a little more', () => {
  const county = new County(vectors.cases[0].towns);
  const porchlight = vectors.cases[0].towns[0].key;
  assert.ok(county.owns(porchlight, 128, 128, 0));
  // Out of the east gate, a little way along the highway: still Porchlight's.
  assert.ok(county.owns(porchlight, 300, 128.5, 0));
  // Most of the way to Lanternport: not, even with slack.
  assert.ok(!county.owns(porchlight, 600, 128.5, 12));
  // North, past the middle of the road to the junction: the north room's.
  assert.equal(county.roomAt(128.5, -250), 'north');
});

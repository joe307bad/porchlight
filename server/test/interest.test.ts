import assert from 'node:assert/strict';
import { test } from 'node:test';
import { CELL, Interest } from '../src/multiplayer/interest.ts';

const sees = (g: Interest, a: string) => [...g.visibleTo(a)].sort();

test('players near each other see each other; far ones do not', () => {
  const g = new Interest();
  g.move('a', 1, 1);
  g.move('b', CELL + 1, 1); // the next cell over
  g.move('c', CELL * 5, CELL * 5); // far away
  g.drain();
  assert.deepEqual(sees(g, 'a'), ['b']);
  assert.deepEqual(sees(g, 'b'), ['a']);
  assert.deepEqual(sees(g, 'c'), []);
});

test('hysteresis: seen from one cell off, kept until more than two off', () => {
  const g = new Interest();
  g.move('a', 1, 1);
  g.move('b', 1, 1);
  g.drain();
  g.move('b', CELL * 2 + 1, 1); // two cells off: still seen
  assert.deepEqual(g.drain(), []);
  assert.deepEqual(sees(g, 'a'), ['b']);
  g.move('b', CELL * 3 + 1, 1); // three off: gone, both ways
  const ops = g.drain();
  assert.equal(ops.filter((o) => !o.add).length, 2);
  assert.deepEqual(sees(g, 'a'), []);
  assert.deepEqual(sees(g, 'b'), []);
  g.move('b', CELL * 2 + 1, 1); // back to two off: not re-added until one off
  assert.deepEqual(g.drain(), []);
  g.move('b', CELL + 1, 1);
  assert.equal(g.drain().filter((o) => o.add).length, 2);
});

test('moving within a cell does nothing', () => {
  const g = new Interest();
  g.move('a', 1, 1);
  g.move('b', 2, 2);
  g.drain();
  g.move('b', 3, 3);
  assert.deepEqual(g.drain(), []);
});

test('remove tells everyone who could see them', () => {
  const g = new Interest();
  g.move('a', 1, 1);
  g.move('b', 2, 2);
  g.move('c', 3, 3);
  g.drain();
  g.remove('b');
  const ops = g.drain();
  assert.deepEqual(ops.map((o) => `${o.viewer}-${o.target}-${o.add}`).sort(), ['a-b-false', 'c-b-false']);
  assert.deepEqual(sees(g, 'a'), ['c']);
});

test('adds are capped per tick and the rest follow', () => {
  const g = new Interest();
  for (let i = 0; i < 30; i++) g.move(`p${i}`, 1, 1);
  g.drain(); // they all see each other now
  g.move('newcomer', 2, 2);
  const first = g.drain(5).filter((o) => o.viewer === 'newcomer');
  assert.equal(first.length, 5);
  let total = first.length;
  for (let i = 0; i < 10; i++) total += g.drain(5).filter((o) => o.viewer === 'newcomer').length;
  assert.equal(total, 30);
});

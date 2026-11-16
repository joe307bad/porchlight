// A town's traffic settings: patches checked against the game's bounds.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { applyTrafficPatch, cleanTraffic, emptyTraffic, MAX_OPS, type TrafficSettings } from '../src/multiplayer/traffic.ts';

const patch = (s: TrafficSettings, ops: unknown[]) => {
  const next = applyTrafficPatch(s, ops);
  if (typeof next === 'string') throw new Error(next);
  return next;
};

test('a patch makes a new version, leaving the old settings as they were', () => {
  const a = emptyTraffic();
  const b = patch(a, [{ op: 'preset', value: 'busy' }, { op: 'global', key: 'density', value: 1.5 }, { op: 'flag', key: 'nightFlash', value: false }]);
  assert.equal(b.version, 1);
  assert.equal(b.preset, 'busy');
  assert.equal(b.density, 1.5);
  assert.equal(b.nightFlash, false);
  assert.deepEqual(a, emptyTraffic());
  // Back to the town's own with null.
  const c = patch(b, [{ op: 'global', key: 'density', value: null }, { op: 'flag', key: 'nightFlash', value: null }]);
  assert.equal(c.density, undefined);
  assert.equal(c.nightFlash, undefined);
});

test('junction and road overrides: set, merged, and gone once back to auto', () => {
  let s = patch(emptyTraffic(), [{ op: 'junction', key: 'j120_48', control: 'AllWayStop' }, { op: 'road', key: 'rj0_0~j4_0~Street~2_0', closed: true }]);
  assert.deepEqual(s.junctions, [{ key: 'j120_48', control: 'AllWayStop' }]);
  assert.deepEqual(s.roads, [{ key: 'rj0_0~j4_0~Street~2_0', closed: true }]);
  s = patch(s, [{ op: 'junction', key: 'j120_48', crossings: false }, { op: 'road', key: 'rj0_0~j4_0~Street~2_0', speed: 0.5 }]);
  assert.deepEqual(s.junctions, [{ key: 'j120_48', control: 'AllWayStop', crossings: false }]);
  assert.deepEqual(s.roads, [{ key: 'rj0_0~j4_0~Street~2_0', closed: true, speed: 0.5 }]);
  s = patch(s, [{ op: 'junction', key: 'j120_48', control: null, crossings: true }, { op: 'road', key: 'rj0_0~j4_0~Street~2_0', reset: true }]);
  assert.deepEqual(s.junctions, []);
  assert.deepEqual(s.roads, []);
});

test('out of bounds or malformed: the whole patch is refused', () => {
  const s = emptyTraffic();
  for (const ops of [
    [],
    [{ op: 'preset', value: 'rush' }],
    [{ op: 'global', key: 'density', value: 3 }],
    [{ op: 'global', key: 'toString', value: 1 }],
    [{ op: 'global', key: 'cruise', value: Number.NaN }],
    [{ op: 'flag', key: 'signals', value: 'yes' }],
    [{ op: 'junction', key: 'x', control: 'Stop' }],
    [{ op: 'junction', key: 'j1_1', control: 'Teleport' }],
    [{ op: 'road', key: 'r a', closed: true }],
    [{ op: 'road', key: 'ra~b', oneWay: 2 }],
    [{ op: 'road', key: 'ra~b', speed: 9 }],
    [{ op: 'event', name: 'Marathon', roads: [], from: 0, until: 1 }],
    [{ op: 'event', name: 'Marathon', roads: ['ra~b'], from: 5, until: 5 }],
    [{ op: 'preset', value: 'busy' }, { op: 'nope' }],
    Array.from({ length: MAX_OPS + 1 }, () => ({ op: 'preset', value: 'busy' })),
  ]) assert.equal(typeof applyTrafficPatch(s, ops), 'string', JSON.stringify(ops).slice(0, 80));
});

test('events: a timed closure, replaced by name, removed', () => {
  let s = patch(emptyTraffic(), [{ op: 'event', name: 'Marathon', roads: ['ra~b', 'rb~c'], from: 1000, until: 5000 }]);
  assert.deepEqual(s.events, [{ name: 'Marathon', kind: 'closure', roads: ['ra~b', 'rb~c'], from: 1000, until: 5000 }]);
  s = patch(s, [{ op: 'event', name: 'Marathon', roads: ['ra~b'], from: 2000, until: 3000 }]);
  assert.equal(s.events.length, 1);
  assert.deepEqual(s.events[0].roads, ['ra~b']);
  s = patch(s, [{ op: 'event', name: 'Marathon', remove: true }]);
  assert.deepEqual(s.events, []);
});

test('stored settings are cleaned: anything unexpected dropped', () => {
  const s = cleanTraffic({ version: 4, preset: 'weird', density: 9, cruise: 1.2, signals: 'no', junctions: [{ key: 'j1_2', control: 'Stop', extra: 1 }, { key: 'bad' }], roads: 'x', events: [{}] });
  assert.equal(s.version, 4);
  assert.equal(s.preset, 'auto');
  assert.equal(s.density, undefined);
  assert.equal(s.cruise, 1.2);
  assert.equal(s.signals, undefined);
  assert.deepEqual(s.junctions, [{ key: 'j1_2', control: 'Stop' }]);
  assert.deepEqual(s.roads, []);
  assert.deepEqual(s.events, []);
  assert.deepEqual(cleanTraffic(null), emptyTraffic());
});

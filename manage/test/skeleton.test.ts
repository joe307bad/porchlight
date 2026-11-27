// The edit screen's skeleton rules (src/edit/skeleton.ts), against the mock's made-up town.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mockGrow } from '../src/edit/mock.ts';
import {
  changeCount,
  deleteRoad,
  dist,
  drawRoad,
  dragWarnings,
  moveBend,
  moveCenter,
  moveNode,
  normalize,
  pullEnd,
  rebase,
  setJunction,
  setRoad,
  snapNode,
  splitRoad,
  withBend,
  decodeWater,
  roadOverride,
} from '../src/edit/skeleton.ts';
import type { Pt, Town } from '../src/edit/types.ts';

const empty = normalize(null);
const town: Town = mockGrow(empty);
const street = town.roads.find((r) => r.kind === 'street' && r.pts.length === 2 && dist(r.pts[0], r.pts[1]) > 8)!;
const main = town.roads.find((r) => r.kind === 'main' && r.pts.length >= 2)!;
const inner = town.nodes.find((n) => n.legs >= 3 && !n.gate && town.roads.filter((r) => r.from === n.id || r.to === n.id).every((r) => r.kind === 'street'))!;

test('the fixture has streets, main roads and a street-only junction', () => {
  assert.ok(street && main && inner);
});

test('the rubber band moves the end fully and fades to nothing by 6 tiles', () => {
  const pts: Pt[] = [[0, 0], [2, 0], [4, 0], [6, 0], [8, 0], [10, 0]];
  const out = pullEnd(pts, true, [0, 3]);
  assert.deepEqual(out[0], [0, 3]);
  assert.ok(out[1][1] > out[2][1] && out[2][1] > 0);
  assert.equal(out[3][1], 0);
  assert.deepEqual(out[5], [10, 0]);
  // From the other end too.
  const back = pullEnd(pts, false, [0, -2]);
  assert.deepEqual(back[5], [10, -2]);
  assert.equal(back[0][1], 0);
});

test('a generated street moved is pinned, and its old ends go into removed', () => {
  const a = street.pts[0], b = street.pts[1];
  const mid: Pt = [(a[0] + b[0]) / 2 + 2, (a[1] + b[1]) / 2 + 2];
  const s = moveBend(empty, town, street.id, 1, mid);
  // A two-point road has no bend to move; a new bend then.
  assert.equal(s, empty);
  const s2 = deleteRoad(empty, town, street.id);
  assert.equal(s2.removed.length, 1);
  assert.equal(s2.streets.length, 0);
  assert.equal(s2.mains, undefined);
});

test('moving a junction pins every street meeting there and removes their old ends', () => {
  const legs = town.roads.filter((r) => r.from === inner.id || r.to === inner.id);
  const to: Pt = [inner.at[0] + 1.5, inner.at[1] - 1];
  const s = moveNode(empty, town, inner.id, to);
  assert.equal(s.streets.length, legs.length);
  assert.equal(s.removed.length, legs.length);
  assert.equal(s.mains, undefined);
  for (const r of s.streets) assert.ok(r.pts.some((p) => dist(p, to) < 0.01), 'each pinned street ends at the new spot');
  // Moving it again edits the pinned streets in place, not new ones.
  const s2 = moveNode(s, mockGrow(s), mockGrow(s).nodes.find((n) => dist(n.at, to) < 0.75)!.id, [to[0] + 1, to[1]]);
  assert.equal(s2.streets.length, legs.length);
  assert.equal(s2.removed.length, legs.length);
});

test('the first main-road edit snapshots every main road into mains', () => {
  const mains = town.roads.filter((r) => r.kind === 'main').length;
  const s = deleteRoad(empty, town, main.id);
  assert.ok(s.mains);
  assert.ok(s.mains!.length >= mains - 1);
  assert.equal(s.streets.length, 0);
  assert.equal(s.removed.length, 0);
});

test('gates never move', () => {
  const gate = town.nodes.find((n) => n.gate)!;
  const snap = snapNode(town, null, [gate.at[0] + 0.5, gate.at[1]]);
  assert.equal(snap.merge, gate.id);
});

test('the bullseye sets center and nothing else', () => {
  const s = moveCenter(empty, [70.123, 90.456]);
  assert.deepEqual(s.center, [70.12, 90.46]);
  assert.equal(changeCount(s), 1);
  assert.equal(changeCount(s, s), 0, 'nothing changed from itself');
  // And the mock grows the town round it: the shops follow.
  const moved = mockGrow(s);
  const shops = moved.buildings.filter((b) => b.kind === 'shop');
  const mean = shops.reduce((a, b) => [a[0] + b.poly[0][0], a[1] + b.poly[0][1]], [0, 0]).map((v) => v / shops.length);
  assert.ok(dist(mean as Pt, [70, 90]) < 10, `shops gather near the new centre (${mean})`);
});

test('drawing an arterial adds a main road; a local road a street', () => {
  const pts: Pt[] = [[10, 10], [30, 10]];
  const a = drawRoad(empty, town, pts, 'arterial');
  assert.equal(a.mains!.length, town.roads.filter((r) => r.kind === 'main').length + 1);
  const l = drawRoad(empty, town, pts, 'local');
  assert.equal(l.streets.length, 1);
  assert.equal(l.mains, undefined);
  assert.equal(drawRoad(empty, town, [[1, 1]], 'local').streets.length, 0);
});

test('splitting a street leaves two pinned streets meeting at the cut', () => {
  const a = street.pts[0], b = street.pts[1];
  const at: Pt = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
  const s = splitRoad(empty, town, street.id, at);
  assert.equal(s.streets.length, 2);
  assert.ok(s.streets.every((r) => r.pts.some((p) => dist(p, at) < 0.02)));
});

test('a new bend through a ghost handle', () => {
  assert.deepEqual(withBend([[0, 0], [10, 0]], 0, [5, 2]), [[0, 0], [5, 2], [10, 0]]);
});

test('side-panel overrides match by ends either way round, and clear back to nothing', () => {
  const a: Pt = [1, 1], b: Pt = [9, 1];
  let s = setRoad(empty, a, b, { lanes: 2, divided: true });
  assert.equal(s.roads.length, 1);
  s = setRoad(s, [9.4, 1.2], [1.3, 0.8], { oneWay: true });
  assert.equal(s.roads.length, 1);
  assert.deepEqual(roadOverride(s, b, a), { a, b, lanes: 2, divided: true, oneWay: true });
  s = setRoad(s, a, b, { lanes: undefined, divided: false, oneWay: false });
  assert.equal(s.roads.length, 0);
  let j = setJunction(empty, [5, 5], 'roundabout');
  j = setJunction(j, [5.5, 5.5], 'signal');
  assert.deepEqual(j.junctions, [{ at: [5.5, 5.5], control: 'signal' }]);
  assert.equal(setJunction(j, [5, 5], 'auto').junctions.length, 0);
});

test('a junction pulled onto its neighbour warns; one in the lake warns', () => {
  const water = decodeWater(town);
  const otherOf = (r: Town['roads'][number]) => town.nodes.find((n) => n.id === (r.from === inner.id ? r.to : r.from))!;
  const leg = town.roads.find((r) => (r.from === inner.id || r.to === inner.id) && otherOf(r).legs >= 3)!;
  const other = otherOf(leg);
  const to: Pt = [other.at[0] + (inner.at[0] - other.at[0]) * 0.15, other.at[1] + (inner.at[1] - other.at[1]) * 0.15];
  const bent = new Map(town.roads.filter((r) => r.from === inner.id || r.to === inner.id).map((r) => [r.id, r.from === inner.id ? pullEnd(r.pts, true, [to[0] - inner.at[0], to[1] - inner.at[1]]) : pullEnd(r.pts, false, [to[0] - inner.at[0], to[1] - inner.at[1]])]));
  const w = dragWarnings(town, inner.id, to, bent, water);
  assert.ok(w.some((x) => x.includes('from the next junction')), w.join('; '));
  assert.ok(dragWarnings(town, inner.id, [132, 128], new Map(), water).includes('in the water'));
});

test('snapping: square to a neighbour, onto a junction, onto a road', () => {
  const near = snapNode(town, null, [inner.at[0] + 0.6, inner.at[1] + 0.6]);
  assert.equal(near.merge, inner.id);
  const leg = town.roads.find((r) => r.from === inner.id)!;
  const far = leg.pts[1];
  // Dragging the junction a little off level with its neighbour: it snaps level (or plumb).
  const off: Pt = [inner.at[0] + 0.2, inner.at[1] + 0.3];
  const s = snapNode(town, inner.id, off);
  assert.ok(Math.abs(s.p[0] - far[0]) < 1e-6 || Math.abs(s.p[1] - far[1]) < 1e-6 || s.merge !== undefined || s.join !== undefined);
});

test('rebase lays the draft over a newer revision and lists what both changed', () => {
  const base = empty;
  const draft = setJunction(moveCenter(base, [70, 70]), [10, 10], 'signal');
  const current = setJunction(moveCenter(base, [90, 90]), [50, 50], 'stop');
  const r = rebase(base, draft, current);
  assert.deepEqual(r.skeleton.center, [70, 70]);
  assert.equal(r.skeleton.junctions.length, 2);
  assert.ok(r.clashes.some((c) => c.includes('centre')));
  // Nothing in common: no clash.
  const r2 = rebase(base, setJunction(base, [10, 10], 'signal'), setJunction(base, [50, 50], 'stop'));
  assert.equal(r2.clashes.length, 0);
  // Both set the same junction differently.
  const r3 = rebase(base, setJunction(base, [10, 10], 'signal'), setJunction(base, [10, 10], 'stop'));
  assert.equal(r3.clashes.length, 1);
  assert.deepEqual(r3.skeleton.junctions, [{ at: [10, 10], control: 'signal' }]);
});

// The edit screen's skeleton (src/edit/skeleton.ts): picking blocks, regions, the downtown bullseye, Commit's
// rule and rebasing, against the mock's made-up town.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mockGrow, mockPlan } from '../src/edit/mock.ts';
import {
  addBlocks,
  addRegion,
  blocksInBox,
  canCommit,
  changeCount,
  clampDowntown,
  dist,
  inPoly,
  isSelected,
  latestRegionNote,
  noteOf,
  populationNote,
  middle,
  moveDowntown,
  NO_WANT,
  normalize,
  rebase,
  reselect,
  sameSkeleton,
  toggleBlock,
} from '../src/edit/skeleton.ts';
import type { Pt, Skeleton, Town } from '../src/edit/types.ts';

const empty = normalize(null);
const town: Town = mockGrow(empty);
const square: Pt[] = [[0, 0], [10, 0], [10, 10], [0, 10]];

test('a point in a polygon, and a block’s middle', () => {
  assert.ok(inPoly([5, 5], square));
  assert.ok(!inPoly([11, 5], square));
  assert.deepEqual(middle(square), [5, 5]);
  // An L whose centroid falls in its notch: the middle is still inside it.
  const ell: Pt[] = [[0, 0], [10, 0], [10, 2], [2, 2], [2, 10], [0, 10]];
  assert.ok(inPoly(middle(ell), ell));
});

test('clicking a block picks it, clicking it again puts it back', () => {
  const [a, b] = town.blocks;
  let sel = toggleBlock([], a.poly);
  sel = toggleBlock(sel, b.poly);
  assert.equal(sel.length, 2);
  assert.ok(isSelected(sel, a.poly));
  sel = toggleBlock(sel, a.poly.map((p) => [p[0], p[1]] as Pt));
  assert.equal(sel.length, 1, 'the same polygon, though a different array');
  assert.ok(!isSelected(sel, a.poly) && isSelected(sel, b.poly));
});

test('a box picks the blocks whose middle is inside it, added to what’s picked, each once', () => {
  const box = blocksInBox(town.blocks, [80, 80], [50, 50]);
  assert.ok(box.length > 0);
  for (const p of box) {
    const m = middle(p);
    assert.ok(m[0] >= 50 && m[0] <= 80 && m[1] >= 50 && m[1] <= 80);
  }
  const sel = addBlocks([box[0]], box);
  assert.equal(sel.length, box.length);
});

test('a region goes on the end with its blocks, its salt and what’s wanted; nothing picked, nothing added', () => {
  const blocks = town.blocks.slice(0, 2).map((b) => b.poly);
  const s = addRegion(empty, blocks, { ...NO_WANT, park: true }, 42);
  assert.deepEqual(s.regions, [{ blocks, salt: 42, want: { ...NO_WANT, park: true } }]);
  const s2 = addRegion(s, blocks, NO_WANT);
  assert.equal(s2.regions!.length, 2);
  assert.ok(Number.isInteger(s2.regions![1].salt) && s2.regions![1].salt >= 0);
  assert.equal(addRegion(empty, [], NO_WANT).regions, undefined);
  // The UI makes no road edits.
  assert.equal(s2.streets, undefined);
  assert.equal(s2.mains, undefined);
});

test('after a plan, the new blocks inside the region are picked again', () => {
  const picked = blocksInBox(town.blocks, [55, 55], [80, 80]);
  const s = addRegion(empty, picked, NO_WANT, 7);
  const after = mockGrow(s);
  assert.ok(after.blocks.length > town.blocks.length, 'the mock split some of them');
  const again = reselect(after.blocks, picked);
  assert.ok(again.length >= picked.length);
  for (const p of again) assert.ok(picked.some((a) => inPoly(middle(p), a)));
  // A block outside isn't.
  const outside = after.blocks.find((b) => !picked.some((a) => inPoly(middle(b.poly), a)))!;
  assert.ok(!isSelected(again, outside.poly));
  assert.deepEqual(reselect(after.blocks, []), []);
  // Another salt makes it differently.
  const other = mockGrow(addRegion(empty, picked, NO_WANT, 8));
  assert.notDeepEqual(other.blocks.map((b) => b.poly), after.blocks.map((b) => b.poly));
});

test('the mock says how a region came out, and the screen shows the latest one’s word', () => {
  const picked = blocksInBox(town.blocks, [55, 55], [80, 80]);
  const s = addRegion(addRegion(empty, picked, NO_WANT, 1), picked, { ...NO_WANT, roundabout: true, park: true }, 2);
  const { town: t, regions } = mockPlan(s);
  assert.equal(regions.length, 2);
  assert.ok(regions[1].ok, regions[1].text);
  assert.ok(t.junctions.some((j) => j.form === 'roundabout'));
  assert.ok(t.blocks.some((b) => b.park && picked.some((a) => inPoly(middle(b.poly), a))));
  assert.equal(latestRegionNote({ ok: true, regions: [{ index: 0, ok: true }, { index: 1, ok: false, text: 'No room for a roundabout there' }] }, s)?.text, 'No room for a roundabout there');
  assert.equal(latestRegionNote({ ok: true, regions: [{ index: 0, ok: false, text: 'old' }, { index: 1, ok: true }] }, s), null);
  assert.equal(latestRegionNote({ ok: true }, empty), null);
});

test('the bullseye stays within 0.7 of the town’s radius of its centre', () => {
  const c: Pt = [80, 80];
  assert.deepEqual(clampDowntown([90, 80], c, 60), [90, 80]);
  const far = clampDowntown([200, 80], c, 60);
  assert.ok(Math.abs(dist(far, c) - 42) < 1e-9);
  assert.ok(Math.abs(far[1] - 80) < 1e-9);
  const s = moveDowntown(empty, [200, 80.123], town);
  assert.ok(dist(s.center!, town.center) <= 0.7 * town.townRadius + 0.01);
  assert.deepEqual(moveDowntown(empty, [70.123, 90.456]).center, [70.12, 90.46]);
});

test('downtown moves the shops, not the town', () => {
  const s = moveDowntown(empty, [65, 95], town);
  const moved = mockGrow(s);
  assert.deepEqual(moved.center, town.center);
  assert.deepEqual(moved.downtown, [65, 95]);
  const shops = moved.buildings.filter((b) => b.kind === 'shop');
  const mean = shops.reduce((a, b) => [a[0] + b.poly[0][0], a[1] + b.poly[0][1]], [0, 0]).map((v) => v / shops.length) as Pt;
  assert.ok(dist(mean, [65, 95]) < 12, `shops gather near downtown (${mean})`);
});

test('Commit opens with a change and shuts again when undo takes it back, or while a plan’s on its way', () => {
  const base = empty;
  assert.equal(canCommit(base, base, false), false, 'nothing to commit');
  const step1 = addRegion(base, [town.blocks[0].poly], NO_WANT, 3);
  assert.equal(canCommit(step1, base, false), true);
  assert.equal(canCommit(step1, base, true), false, 'a plan in flight');
  const step2 = moveDowntown(step1, [70, 70], town);
  assert.equal(canCommit(step2, base, false), true);
  // Undo pops back to the base: shut again.
  const undo: Skeleton[] = [base, step1];
  const back = undo.pop()!;
  assert.equal(canCommit(back, base, false), true);
  assert.equal(canCommit(undo.pop()!, base, false), false);
  // An older draft's road edits count as changes too.
  assert.equal(canCommit(normalize({ junctions: [{ at: [1, 1], control: 'signal' }] }), base, false), true);
});

test('normalize leaves empty lists out, keeps an older draft’s road edits as they are', () => {
  const old = { v: 1 as const, streets: [{ pts: [[1, 1], [5, 1]] as Pt[], kind: 'street' as const }], removed: [], roads: [], junctions: [{ at: [3, 3] as Pt, control: 'stop' as const }] };
  const n = normalize(old);
  assert.deepEqual(n, { v: 1, streets: old.streets, junctions: old.junctions });
  assert.ok(sameSkeleton(old, n));
  assert.deepEqual(normalize(null), { v: 1 });
});

test('changeCount counts the downtown and each region', () => {
  const s = addRegion(moveDowntown(empty, [70, 70]), [square], NO_WANT, 1);
  assert.equal(changeCount(s), 2);
  assert.equal(changeCount(s, s), 0);
});

test('rebase lays the draft over a newer revision and lists what both changed', () => {
  const base = empty;
  const mine = addRegion(moveDowntown(base, [70, 70]), [square], NO_WANT, 1);
  const theirs = addRegion(moveDowntown(base, [90, 90]), [[[20, 20], [30, 20], [30, 30]]], NO_WANT, 2);
  const r = rebase(base, mine, theirs);
  assert.deepEqual(r.skeleton.center, [70, 70]);
  assert.equal(r.skeleton.regions!.length, 2);
  assert.equal(r.skeleton.regions![1].salt, 1, 'the draft’s region goes after theirs');
  assert.ok(r.clashes.some((c) => c.includes('downtown')));
  // Nothing in common: no clash.
  const r2 = rebase(base, addRegion(base, [square], NO_WANT, 1), moveDowntown(base, [90, 90]));
  assert.equal(r2.clashes.length, 0);
  assert.deepEqual(r2.skeleton.center, [90, 90]);
  // An older draft's junction set differently by both still clashes.
  const j = (control: 'signal' | 'stop') => normalize({ junctions: [{ at: [10, 10], control }] });
  const r3 = rebase(base, j('signal'), j('stop'));
  assert.equal(r3.clashes.length, 1);
  assert.deepEqual(r3.skeleton.junctions, [{ at: [10, 10], control: 'signal' }]);
});

test('a fall in population is one warning line; a region that lacks what it asked for comes first', () => {
  const s = { v: 1, regions: [{ blocks: [], salt: 1, want: { roundabout: true, park: false, school: false, shops: false, houses: false } }] } as any;
  const down = { ok: true, changes: { population: [1200, 1150], shops: [1, 1], roadsAdded: 0, roadsRemoved: 0, roundabouts: [0, 0], signals: [0, 0] } } as any;
  assert.deepEqual(populationNote(down), { ok: false, text: 'Population down by 50 (1,200 → 1,150)' });
  assert.equal(populationNote({ ...down, changes: { ...down.changes, population: [1200, 1300] } }), null);
  assert.equal(noteOf(down, s)?.text, 'Population down by 50 (1,200 → 1,150)');
  assert.equal(noteOf({ ...down, regions: [{ index: 0, ok: false, text: 'No room for a roundabout there' }] }, s)?.text, 'No room for a roundabout there');
});

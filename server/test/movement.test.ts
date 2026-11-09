import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ARRIVE_GRACE_SECONDS, MAX_SPEED, RateLimit, Strikes, judgeMove, newTrack, parseMove, seqNewer } from '../src/multiplayer/movement.ts';
import type { MoveMessage } from '../src/multiplayer/protocol.ts';

const bounds = { width: 128, height: 128 };
const move = (over: Partial<MoveMessage> = {}): MoveMessage => ({ seq: 1, t: 0, epoch: 0, x: 10, y: 0, z: 10, yaw: 0, anim: 1, car: 0, ride: '', ...over });

test('parseMove rejects junk', () => {
  assert.ok(parseMove(move()));
  for (const bad of [null, 'x', {}, move({ t: -1 }), move({ t: 1.5 }), move({ x: NaN }), move({ z: Infinity }), move({ anim: 9 }), move({ seq: 70000 }), { ...move(), y: '1' }]) {
    assert.equal(parseMove(bad), null, JSON.stringify(bad));
  }
});

test('seq wraps', () => {
  assert.ok(seqNewer(2, 1));
  assert.ok(seqNewer(0, 0xffff));
  assert.ok(!seqNewer(1, 1));
  assert.ok(!seqNewer(0xffff, 0));
});

test('the first move anchors; then speed is checked against server time', () => {
  const t = newTrack();
  assert.equal(judgeMove(t, move({ x: 100 }), 0, bounds), 'ok');
  Object.assign(t, { anchored: true, x: 10, z: 10, t: 1000, seq: 1 });
  // A tenth of a second at walking pace: fine.
  assert.equal(judgeMove(t, move({ seq: 2, x: 10.4 }), 1100, bounds), 'ok');
  // Ten tiles in a tenth of a second: no.
  assert.equal(judgeMove(t, move({ seq: 2, x: 20 }), 1100, bounds), 'too-fast');
  // A long pause doesn't buy a teleport: the gap counts as a few seconds at most.
  assert.equal(judgeMove(t, move({ seq: 2, x: 10 + MAX_SPEED * 5 }), 61_000, bounds), 'too-fast');
});

test('a boosted car through a two-second network stall is no cheat', () => {
  const t = { ...newTrack(), anchored: true, x: 10, z: 10, t: 1000, seq: 1 };
  // A boosted car does 8.9 tiles a second: the first move through after the stall is 18 tiles on.
  assert.equal(judgeMove(t, move({ seq: 2, x: 10 + 8.9 * 2 }), 3000, bounds), 'ok');
});

test('driving in from next door: the drive while joining is let off the first move', () => {
  const t = { ...newTrack(), anchored: true, x: 10, z: 10, t: 1000, seq: null };
  // A second's drive during the join that the room never timed, and a tenth after.
  const m = move({ seq: 1, x: 10 + 8.9 * 1.1 });
  assert.equal(judgeMove(t, m, 1100, bounds), 'too-fast');
  assert.equal(judgeMove({ ...t, grace: MAX_SPEED * ARRIVE_GRACE_SECONDS }, m, 1100, bounds), 'ok');
});

test('a passenger goes where the car goes', () => {
  const t = { ...newTrack(), anchored: true, x: 10, z: 10, t: 1000, seq: 1 };
  // The car they see caught up a way at once (its driver's moves came in a bunch): held to the driver, not the speed.
  const m = move({ seq: 2, x: 30, ride: 'driver' });
  assert.equal(judgeMove(t, m, 1100, bounds), 'too-fast');
  assert.equal(judgeMove(t, m, 1100, bounds, { riding: true }), 'ok');
  // Still in town, though.
  assert.equal(judgeMove(t, move({ seq: 2, x: -5, ride: 'driver' }), 1100, bounds, { riding: true }), 'out-of-bounds');
});

test('stale moves (old seq, old epoch) and out-of-town ones', () => {
  const t = { ...newTrack(), anchored: true, x: 10, z: 10, t: 0, seq: 5, epoch: 2 };
  assert.equal(judgeMove(t, move({ seq: 5, epoch: 2 }), 50, bounds), 'stale');
  assert.equal(judgeMove(t, move({ seq: 6, epoch: 1 }), 50, bounds), 'stale');
  assert.equal(judgeMove(t, move({ seq: 6, epoch: 2, x: -1 }), 50, bounds), 'out-of-bounds');
  assert.equal(judgeMove(t, move({ seq: 6, epoch: 2, y: 50 }), 50, bounds), 'out-of-bounds');
  // A plane at its ceiling is in the town.
  assert.equal(judgeMove(t, move({ seq: 6, epoch: 2, y: 20 }), 50, bounds), 'ok');
});

test('rate limit drops, then kicks when it keeps up', () => {
  const r = new RateLimit(30, 3);
  let drops = 0;
  for (let i = 0; i < 40; i++) if (r.hit(i) === 'drop') drops++;
  assert.equal(drops, 10);
  let kicked = false;
  for (let s = 1; s < 6 && !kicked; s++) for (let i = 0; i < 40; i++) if (r.hit(s * 1000 + i) === 'kick') kicked = true;
  assert.ok(kicked);
  const calm = new RateLimit(30, 3);
  for (let s = 0; s < 10; s++) for (let i = 0; i < 20; i++) assert.equal(calm.hit(s * 1000 + i * 10), 'ok');
});

test('strikes count over a minute', () => {
  const s = new Strikes(3);
  assert.ok(!s.add(0));
  assert.ok(!s.add(1000));
  assert.ok(!s.add(61_500)); // the first two have expired
  assert.ok(!s.add(62_000));
  assert.ok(s.add(62_500));
});

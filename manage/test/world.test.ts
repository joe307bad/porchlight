// The manage app's reading of the feed, and its World: frames split across
// the stream's chunks any which way, and what's drawn moving between frames.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { FRAME_POSITIONS, FRAME_ROOMS, FrameReader, type Frame, type RoomMeta } from '../src/protocol.ts';
import { World } from '../src/world.ts';

const frame = (type: number, body: Uint8Array): Uint8Array => {
  const out = new Uint8Array(5 + body.length);
  new DataView(out.buffer).setUint32(0, body.length + 1, true);
  out[4] = type;
  out.set(body, 5);
  return out;
};

const roomsFrame = (rooms: RoomMeta[]) => frame(FRAME_ROOMS, new TextEncoder().encode(JSON.stringify({ rooms })));

/** A positions frame of one room, as server/src/multiplayer/feed.ts packs it. */
function positionsFrame(id: number, players: { x: number; z: number; ride?: number }[], cars: { x: number; y: number; heading: number; speed: number }[], age = 0): Uint8Array {
  const size = 8 + 2 + 8 + players.length * 18 + 2 + cars.length * 7 + 2;
  const b = new Uint8Array(size);
  const v = new DataView(b.buffer);
  v.setFloat64(0, 1234, true);
  v.setUint16(8, 1, true);
  let o = 10;
  v.setUint32(o, id, true);
  v.setUint16(o + 4, age, true);
  v.setUint16(o + 6, players.length, true);
  o += 8;
  for (const p of players) {
    v.setFloat32(o, p.x, true);
    v.setFloat32(o + 4, p.z, true);
    v.setInt16(o + 16, p.ride ?? -1, true);
    o += 18;
  }
  v.setUint16(o, cars.length, true);
  o += 2;
  for (const c of cars) {
    v.setUint16(o, Math.round(c.x * 100), false);
    v.setUint16(o + 2, Math.round(c.y * 100), false);
    v.setUint16(o + 4, Math.round((c.heading / (2 * Math.PI)) * 65536) & 0xffff, false);
    v.setUint8(o + 6, Math.round(c.speed * 40));
    o += 7;
  }
  v.setUint16(o, 0, true);
  return frame(FRAME_POSITIONS, b);
}

const meta: RoomMeta = { id: 7, key: 'seed=x', name: 'Ex', ox: 100, oy: 200, host: 0, players: [{ sid: 'a', id: 'ca', name: 'Ann', look: '' }, { sid: 'b', id: 'cb', name: 'Bo', look: '' }] };

test('frames come out whole however the stream splits them', () => {
  const stream = new Uint8Array([...roomsFrame([meta]), ...positionsFrame(7, [{ x: 1, z: 2 }, { x: 3, z: 4, ride: 0 }], [{ x: 10, y: 20, heading: 0, speed: 2 }])]);
  for (const size of [1, 2, 3, 7, 50, stream.length]) {
    const r = new FrameReader();
    const got: Frame[] = [];
    for (let i = 0; i < stream.length; i += size) got.push(...r.push(stream.subarray(i, i + size)));
    assert.deepEqual(got.map((f) => f.type), [FRAME_ROOMS, FRAME_POSITIONS], `in chunks of ${size}`);
  }
  // Big frames grow the reader's buffer.
  const many = Array.from({ length: 20_000 }, (_, i) => ({ x: i % 600, y: 5, heading: 0, speed: 1 }));
  const r = new FrameReader();
  const big = positionsFrame(7, [], many);
  const f = [...r.push(big.subarray(0, 1000)), ...r.push(big.subarray(1000))];
  assert.equal(f.length, 1);
  assert.equal(f[0].type === FRAME_POSITIONS && f[0].rooms[0].cars, 20_000);
});

test('the world: placed on the county map, riders tied to drivers, cars carried on between frames', () => {
  const w = new World();
  const r = new FrameReader();
  for (const f of r.push(roomsFrame([meta]))) if (f.type === FRAME_ROOMS) w.applyRooms(f.rooms);
  for (const f of r.push(positionsFrame(7, [{ x: 1, z: 2 }, { x: 1.5, z: 2, ride: 0 }], [{ x: 5, y: 6, heading: 0, speed: 2 }, { x: 6, y: 6, heading: Math.PI / 2, speed: 0 }])))
    if (f.type === FRAME_POSITIONS) w.applyPositions(f, 1000);
  const [ann, bo] = w.players();
  assert.equal(bo.ride, ann);
  assert.deepEqual(ann.nearPlayers, [bo]);
  assert.equal(ann.nearCars, 2);
  const room = w.order[0];
  assert.equal(room.cars.tx[0], 105);
  assert.equal(room.cars.ty[0], 206);
  // First step: drawn where they are; then the moving car goes on east at 2 tiles a second, the stopped one stays.
  w.step(1000);
  w.step(1250);
  w.step(1500);
  assert.ok(Math.abs(room.cars.tx[0] - 106) < 1e-3, `${room.cars.tx[0]}`);
  assert.ok(room.cars.x[0] > 105 && room.cars.x[0] <= 106);
  assert.equal(room.cars.tx[1], 106);
  const t = w.totals();
  assert.deepEqual([t.players, t.cars, t.moving, t.stopped, t.towns], [2, 2, 1, 1, 1]);
  // A host gone quiet: everything stands where it was.
  for (const f of r.push(positionsFrame(7, [{ x: 1, z: 2 }], [{ x: 5, y: 6, heading: 0, speed: 2 }], 5000))) if (f.type === FRAME_POSITIONS) w.applyPositions(f, 2000);
  w.step(2500);
  w.step(3000);
  assert.equal(room.cars.tx[0], 105);
  // The room closed: gone from the world.
  w.applyRooms([]);
  assert.equal(w.players().length, 0);
});

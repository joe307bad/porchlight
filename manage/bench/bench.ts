// The map under stress (test-browser/stress.mjs opens this, never shipped): a
// county of 8 towns, each with 1,250 cars and 1,250 townsfolk (four times what
// a town's world message can carry) and 12 players, fed 10 times a second as
// the server packs it, and drawn frame by frame, each frame timed through to
// its pixels. The results go on window.__bench.

import type { CountyMap } from '../src/api.ts';
import { FRAME_POSITIONS, FRAME_ROOMS, FrameReader, type RoomMeta } from '../src/protocol.ts';
import { MapRenderer, DEFAULT_LAYERS, type Layers } from '../src/render.ts';
import { World } from '../src/world.ts';

const TOWNS = 8;
const CARS = 1250;
const PEOPLE = 1250;
const PLAYERS = 12;
const SIZE = 256;
const FRAMES = 180;

/** A town's streets: a grid, every 8 tiles, with main roads every 32. */
function streets(): number[][] {
  const roads: number[][] = [];
  for (let k = 4; k < SIZE; k += 8) {
    const main = k % 32 === 4;
    roads.push([main ? 0 : 1, main ? 3 : 2, k, 0, k, SIZE / 2, k, SIZE]);
    roads.push([main ? 0 : 1, main ? 3 : 2, 0, k, SIZE / 2, k, SIZE, k]);
  }
  return roads;
}

const origin = (i: number) => ({ x: (i % 4) * (SIZE + 120), y: Math.floor(i / 4) * (SIZE + 120) });
const map: CountyMap = {
  towns: Array.from({ length: TOWNS }, (_, i) => ({ key: `t${i}`, name: `Town ${i + 1}`, x0: origin(i).x, y0: origin(i).y, width: SIZE, height: SIZE, private: false, roads: streets(), zoning: null, buildings: null })),
  spaces: [],
  highways: Array.from({ length: TOWNS - 1 }, (_, i) => [origin(i).x + SIZE, origin(i).y + SIZE / 2, origin(i + 1).x, origin(i + 1).y + SIZE / 2]),
};
const metas: RoomMeta[] = map.towns.map((t, i) => ({
  id: i + 1, key: t.key, name: t.name, ox: t.x0, oy: t.y0, host: 0,
  players: Array.from({ length: PLAYERS }, (_, j) => ({ sid: `s${i}-${j}`, id: `c${i}-${j}`, name: `Player ${i}.${j}`, look: '' })),
}));

/** Things moving along the grid: each on one street, back and forth. */
const movers = (n: number, seed: number) =>
  Array.from({ length: n }, (_, k) => ({ line: 4 + 8 * ((k * 7 + seed) % 32), across: k % 2 === 0, s: (k * 37) % SIZE, v: ((k * 13) % 9) * 0.6, dir: 1 }));
const towns = metas.map((_, i) => ({ cars: movers(CARS, i), people: movers(PEOPLE, i + 3), t: 0 }));

function frame(type: number, body: Uint8Array): Uint8Array {
  const out = new Uint8Array(5 + body.length);
  new DataView(out.buffer).setUint32(0, body.length + 1, true);
  out[4] = type;
  out.set(body, 5);
  return out;
}

/** One positions frame of the whole county, a tenth of a second on, as server/src/multiplayer/feed.ts packs it. */
function positions(): Uint8Array {
  const per = 8 + PLAYERS * 18 + 2 + CARS * 7 + 2 + PEOPLE * 7;
  const b = new Uint8Array(10 + TOWNS * per);
  const v = new DataView(b.buffer);
  v.setFloat64(0, Date.now(), true);
  v.setUint16(8, TOWNS, true);
  let o = 10;
  towns.forEach((town, i) => {
    town.t += 0.1;
    v.setUint32(o, i + 1, true);
    v.setUint16(o + 4, 0, true);
    v.setUint16(o + 6, PLAYERS, true);
    o += 8;
    for (let j = 0; j < PLAYERS; j++, o += 18) {
      const a = town.t / 5 + j;
      v.setFloat32(o, SIZE / 2 + Math.cos(a) * (20 + j * 8), true);
      v.setFloat32(o + 4, SIZE / 2 + Math.sin(a) * (20 + j * 8), true);
      v.setUint16(o + 12, (j * 5000) & 0xffff, true);
      v.setUint8(o + 14, 1);
      v.setUint8(o + 15, j % 3 === 0 ? 2 : 0);
      v.setInt16(o + 16, j % 5 === 1 ? j - 1 : -1, true);
    }
    for (const list of [town.cars, town.people]) {
      v.setUint16(o, list.length, true);
      o += 2;
      for (const m of list) {
        m.s += m.v * m.dir * 0.1;
        if (m.s < 0 || m.s > SIZE - 1) m.dir *= -1;
        const x = m.across ? m.s : m.line;
        const y = m.across ? m.line : m.s;
        v.setUint16(o, Math.round(Math.max(0, x) * 100), false);
        v.setUint16(o + 2, Math.round(Math.max(0, y) * 100), false);
        v.setUint16(o + 4, m.across ? (m.dir > 0 ? 0 : 32768) : m.dir > 0 ? 16384 : 49152, false);
        v.setUint8(o + 6, Math.round(m.v * 40));
        o += 7;
      }
    }
  });
  return frame(FRAME_POSITIONS, b);
}

interface Result {
  frames: number;
  avg: number;
  p95: number;
  max: number;
  /** Time taking in each feed frame (decoding, and who's near whom). */
  feedAvg: number;
}

async function run(): Promise<Record<string, Result>> {
  const canvas = document.getElementById('map') as HTMLCanvasElement;
  const world = new World();
  const reader = new FrameReader();
  const renderer = new MapRenderer(canvas, world, map, false);
  for (const f of reader.push(frame(FRAME_ROOMS, new TextEncoder().encode(JSON.stringify({ rooms: metas })))))
    if (f.type === FRAME_ROOMS) world.applyRooms(f.rooms);
  const ctx = canvas.getContext('2d')!;
  let now = 1000;
  const feed: number[] = [];
  const feedFrame = () => {
    const bytes = positions();
    const t0 = performance.now();
    for (const f of reader.push(bytes)) if (f.type === FRAME_POSITIONS) world.applyPositions(f, now);
    feed.push(performance.now() - t0);
  };
  // Wait for the canvas to have its size.
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  const scenario = (setup: () => void, layers: Partial<Layers>): Result => {
    renderer.layers = { ...DEFAULT_LAYERS, ...layers };
    setup();
    feed.length = 0;
    const times: number[] = [];
    for (let i = 0; i < FRAMES + 10; i++) {
      // The feed's 10 a second, between 60 drawn.
      if (i % 6 === 0) feedFrame();
      now += 1000 / 60;
      const t0 = performance.now();
      renderer.frameAt(now);
      // Through to pixels: a canvas draws lazily, and reading one back waits for it.
      ctx.getImageData(0, 0, 1, 1);
      if (i >= 10) times.push(performance.now() - t0);
    }
    times.sort((a, b) => a - b);
    return {
      frames: times.length,
      avg: times.reduce((a, b) => a + b, 0) / times.length,
      p95: times[Math.floor(times.length * 0.95)],
      max: times[times.length - 1],
      feedAvg: feed.reduce((a, b) => a + b, 0) / Math.max(1, feed.length),
    };
  };
  return {
    town: scenario(() => renderer.fit(map.towns[0].x0, map.towns[0].y0, 120, 80), {}),
    trails: scenario(() => renderer.fitAll(), { trails: true }),
    county: scenario(() => renderer.fitAll(), {}),
  };
}

declare global {
  interface Window {
    __bench?: Record<string, Result> | { error: string };
  }
}

run().then(
  (r) => (window.__bench = r),
  (e: Error) => (window.__bench = { error: String(e?.stack ?? e) }),
);

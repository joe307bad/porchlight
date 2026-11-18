// The traffic view's data (manage/, the React app at /manage): the county as
// a map, and every car, townsperson and player in it, live.
//
//   GET /mp/traffic/county   the signed-in account's county: its towns (and the
//                            roads of the ones that come with the game), the
//                            highways and the spaces between (JSON)
//   GET /mp/traffic/feed     a stream of binary frames, about 10 a second, for
//                            as long as the request stays open
//
// One timer serves every viewer: each tick packs each open room once (its
// players, and its host's cars and townsfolk exactly as the game packed them,
// WorldSync.cs), and each viewer is sent the rooms of its own county, joined.
// A viewer whose connection can't keep up skips ticks rather than queueing
// them. A town someone made is only ever sent to them.
//
// Each frame: u32 length (of what follows), u8 type, then
//
//   type 1, rooms: UTF-8 JSON { rooms: [{ id, key, name, ox, oy, host, players: [{ sid, id, name, look }] }] }
//     sent first, and again whenever the rooms or who's in them change. ox, oy:
//     where the room's positions are measured from, on the county map.
//   type 2, positions:
//     f64 server time (ms), u16 room count, then per room (in the last rooms frame's order)
//       u32 id, u16 age of its world (ms; 65535: none, or older)
//       u16 players, then per player (in its rooms entry's order):
//         f32 x, f32 z, f32 y, u16 heading (0-65535), u8 anim, u8 car (model + 1), i16 ride (the driver's index, or -1)
//       u16 cars, then 7 bytes each as the game packs them
//       u16 people, then 7 bytes each likewise
//   Everything little-endian but the cars' and people's records (WorldSync.cs:
//   big-endian u16 x and y in hundredths of a tile, heading 0-65535, u8 speed in fortieths of a tile a second).

import { readFileSync, readdirSync } from 'node:fs';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { County, mineSlot, NORTH, type Space } from '../county.ts';
import { foreignOrigin, readSession, type GameDeps } from './accounts.ts';
import { openRooms, type CityRoom } from './CityRoom.ts';
import { STARTER_TOWNS } from './persistence.ts';

const COUNTY = '/mp/traffic/county';
const FEED = '/mp/traffic/feed';

/** How often viewers are sent where everything is (ms). */
export const FEED_MS = 100;
/** How long a viewer's county is trusted before it's looked up again (ms): a town made or deleted shows up by then. */
const COUNTY_REFRESH_MS = 15_000;
/** Most viewers at once, all told and per account. */
const MAX_VIEWERS = 200;
const MAX_PER_ACCOUNT = 4;
/** Bytes per player in a positions frame. */
const PLAYER_BYTES = 18;
export const FRAME_ROOMS = 1;
export const FRAME_POSITIONS = 2;
const NO_WORLD = 0xffff;

/** A county, as the traffic view draws it (all in tiles; the county's frame). */
export interface CountyMapJson {
  towns: {
    key: string;
    name: string;
    x0: number;
    y0: number;
    width: number;
    height: number;
    private: boolean;
    /** Its streets (town's own frame): [kind, width, x0, y0, x1, y1, ...], when known (the towns that come with the game). */
    roads: number[][] | null;
  }[];
  /** The junction north of Porchlight, and an account's empty places for towns. */
  spaces: { kind: string; x0: number; y0: number; width: number; height: number }[];
  /** Each highway's centreline: x0, y0, x1, y1, ... */
  highways: number[][];
}

/** The streets of the towns that come with the game, by key (towns/*.roads.json, written by `Checks roadmap`). */
let starterRoads: Map<string, number[][]> | null = null;
function roadsOf(key: string): number[][] | null {
  if (!starterRoads) {
    starterRoads = new Map();
    const dir = new URL('../../towns/', import.meta.url);
    try {
      for (const f of readdirSync(dir)) {
        if (!f.endsWith('.roads.json')) continue;
        try {
          const doc = JSON.parse(readFileSync(new URL(f, dir), 'utf8')) as { key: string; roads: number[][] };
          starterRoads.set(doc.key, doc.roads);
        } catch (err) {
          console.error(`traffic: couldn't read ${f}:`, (err as Error).message);
        }
      }
    } catch {
      // No towns folder (a test's): no streets.
    }
  }
  return starterRoads.get(key) ?? null;
}

const round1 = (v: number) => Math.round(v * 10) / 10;

/** An account's county: the towns that come with the game, and its own in their places; and what each is called. */
async function countyOf(deps: GameDeps, accountId: string): Promise<{ county: County; names: Map<string, string> }> {
  const towns = await deps.persistence.listTowns(accountId);
  const names = new Map<string, string>();
  for (const t of STARTER_TOWNS) names.set(t.key, t.name);
  const placed = STARTER_TOWNS.map((t) => ({ place: t.place, key: t.key }));
  for (const t of towns) {
    names.set(t.key, t.name);
    if (t.mine && t.slot !== null && t.slot >= 0 && t.slot < 3) placed.push({ place: mineSlot(t.slot), key: t.key });
  }
  const county = new County(placed, accountId);
  names.set(county.northRoom, 'Countryside');
  return { county, names };
}

export function countyMapJson(county: County, names: Map<string, string>): CountyMapJson {
  const box = (s: Space) => ({ x0: s.x0, y0: s.y0, width: s.width, height: s.height });
  return {
    towns: county.spaces
      .filter((s) => s.kind === 'town')
      .map((s) => ({ key: s.townKey!, name: names.get(s.townKey!) ?? 'A town', ...box(s), private: s.isPrivate, roads: roadsOf(s.townKey!) })),
    spaces: county.spaces.filter((s) => s.kind === 'junction' || s.kind === 'vacant').map((s) => ({ kind: s.kind, ...box(s) })),
    highways: county.highways.map((h) => h.points.flatMap((p) => [round1(p.x), round1(p.y)])),
  };
}

/** One room, packed once a tick for every viewer who can see it. */
interface Packed {
  room: CityRoom;
  /** Changes whenever who's in the room (or what they're called, or look like, or who hosts) does. */
  sig: string;
  players: { sid: string; id: string; name: string; look: string }[];
  host: number;
  /** Its part of a positions frame. */
  bytes: Buffer;
}

function pack(room: CityRoom, now: number): Packed {
  const list = [...room.watchPlayers()];
  const index = new Map(list.map((p, i) => [p.sessionId, i]));
  const world = room.world;
  const age = world ? Math.max(0, Math.min(NO_WORLD, now - world.at)) : NO_WORLD;
  const cars = world && age < NO_WORLD ? world.cars : null;
  const people = world && age < NO_WORLD ? world.people : null;
  const carBytes = cars ? cars.byteLength - (cars.byteLength % 7) : 0;
  const peopleBytes = people ? people.byteLength - (people.byteLength % 7) : 0;
  const b = Buffer.allocUnsafe(4 + 2 + 2 + list.length * PLAYER_BYTES + 2 + carBytes + 2 + peopleBytes);
  let o = 0;
  o = b.writeUInt32LE(room.watchId >>> 0, o);
  o = b.writeUInt16LE(age, o);
  o = b.writeUInt16LE(list.length, o);
  for (const p of list) {
    o = b.writeFloatLE(p.x, o);
    o = b.writeFloatLE(p.z, o);
    o = b.writeFloatLE(p.y, o);
    o = b.writeUInt16LE(p.rotY & 0xffff, o);
    o = b.writeUInt8(p.anim & 0xff, o);
    o = b.writeUInt8(p.car & 0xff, o);
    o = b.writeInt16LE(p.ride ? (index.get(p.ride) ?? -1) : -1, o);
  }
  o = b.writeUInt16LE(carBytes / 7, o);
  if (cars) o += Buffer.from(cars.buffer, cars.byteOffset, carBytes).copy(b, o);
  o = b.writeUInt16LE(peopleBytes / 7, o);
  if (people) o += Buffer.from(people.buffer, people.byteOffset, peopleBytes).copy(b, o);
  const players = list.map((p) => ({ sid: p.sessionId, id: p.characterId, name: p.name, look: p.look }));
  const host = index.get(room.state?.host ?? '') ?? -1;
  const sig = `${host}|${players.map((p) => `${p.sid}:${p.name}:${p.look}`).join(',')}`;
  return { room, sig, players, host, bytes: b };
}

/** Where a viewer's frames go: the feed's HTTP response (a test's stand-in has the same few members). */
export interface FeedSink {
  readonly writableNeedDrain: boolean;
  write(chunk: Buffer): boolean;
  cork(): void;
  uncork(): void;
  end(): void;
  /** Closes the connection now, whatever it still holds (an HTTP response has it). */
  destroy?(): void;
}

/** A viewer whose connection hasn't taken a frame for this long (ms) is let go: it's gone, whatever's in between didn't say. */
export const STUCK_MS = 30_000;

interface Viewer {
  account: string;
  res: FeedSink;
  /** The rooms of its county, and where each is on the map (null until known). */
  origins: Map<string, { x: number; y: number }> | null;
  names: Map<string, string>;
  countyAt: number;
  looking: boolean;
  /** What the last rooms frame it was sent said. */
  sentSig: string;
  /** Since when its connection hasn't taken a frame (0: it has). */
  stuckSince: number;
}

/** A frame's first bytes: its length (of what follows them) and its type. */
const frameHead = (type: number, length: number, extra = 0): Buffer => {
  const head = Buffer.allocUnsafe(5 + extra);
  head.writeUInt32LE(length + 1, 0);
  head.writeUInt8(type, 4);
  return head;
};

/** A tick slower than this (ms) is said at once (at most every SLOW_GAP_MS); the rest once a minute. */
const SLOW_MS = 25;
const SLOW_GAP_MS = 10_000;
const REPORT_MS = 60_000;

/** How the feed's doing since the last report: what the server log says once a minute. */
export interface FeedStats {
  ticks: number;
  /** Time spent in ticks (ms), all told and the longest. */
  totalMs: number;
  maxMs: number;
  /** Frames a viewer missed because its connection was still busy with the last. */
  skipped: number;
  bytes: number;
}

export class TrafficFeed {
  private readonly viewers = new Set<Viewer>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private readonly deps: GameDeps;
  private readonly rooms: () => Iterable<CityRoom>;
  private readonly now: () => number;
  private stats: FeedStats = { ticks: 0, totalMs: 0, maxMs: 0, skipped: 0, bytes: 0 };
  private reportedAt = 0;
  private slowSaidAt = -Infinity;
  /** Where reports go (the server log; a test listens). */
  log: (line: string) => void = (line) => console.log(line);

  constructor(deps: GameDeps, rooms: () => Iterable<CityRoom> = openRooms, now: () => number = Date.now) {
    this.deps = deps;
    this.rooms = rooms;
    this.now = now;
  }

  get viewerCount(): number {
    return this.viewers.size;
  }

  /** How it's gone since the last report (and start again). */
  takeStats(): FeedStats {
    const s = this.stats;
    this.stats = { ticks: 0, totalMs: 0, maxMs: 0, skipped: 0, bytes: 0 };
    return s;
  }

  /** Handles the traffic routes; false if the request isn't one of them. */
  routes = async (req: IncomingMessage, res: ServerResponse): Promise<boolean> => {
    const path = (req.url ?? '/').split('?')[0];
    if (path !== COUNTY && path !== FEED) return false;
    const send = (status: number, body: unknown): true => {
      res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' });
      res.end(JSON.stringify(body));
      return true;
    };
    if (req.method !== 'GET') return send(405, { error: 'Method not allowed.' });
    const session = readSession(this.deps.sessionSecret, req.headers.cookie);
    if (!session) return send(401, { error: 'Not signed in.' });
    if (foreignOrigin(this.deps.appUrl, req.headers.origin)) return send(403, { error: 'Wrong origin.' });
    if (!this.deps.persistence.ready) return send(503, { error: 'The game is starting up. Try again in a moment.' });
    const account = await this.deps.persistence.account(session.sub, session.name);
    if (account.banned) return send(403, { error: 'This account is banned.' });

    if (path === COUNTY) {
      const { county, names } = await countyOf(this.deps, account.id);
      return send(200, countyMapJson(county, names));
    }

    let mine = 0;
    for (const v of this.viewers) if (v.account === account.id) mine++;
    if (mine >= MAX_PER_ACCOUNT || this.viewers.size >= MAX_VIEWERS) return send(429, { error: 'Too many traffic views open. Close one and try again.' });
    res.writeHead(200, {
      'content-type': 'application/octet-stream',
      'cache-control': 'no-store, no-transform',
      'x-accel-buffering': 'no',
    });
    req.socket.setNoDelay(true);
    res.flushHeaders();
    const stop = this.watch(account.id, res);
    res.on('close', stop);
    return true;
  };

  /**
   * Send this viewer its county's rooms from now on: those in `origins` (room → where on the map), or,
   * if not given, its account's county, looked up. The function returned stops it.
   */
  watch(account: string, res: FeedSink, origins?: Map<string, { x: number; y: number }>): () => void {
    const viewer: Viewer = { account, res, origins: origins ?? null, names: new Map(), countyAt: origins ? Infinity : 0, looking: false, sentSig: '', stuckSince: 0 };
    this.viewers.add(viewer);
    if (!origins) this.lookUp(viewer);
    this.start();
    return () => {
      this.viewers.delete(viewer);
      if (this.viewers.size === 0) this.stop();
    };
  }

  /** Work out (again) which rooms are the viewer's, and where each is. */
  private lookUp(viewer: Viewer): void {
    if (viewer.looking) return;
    viewer.looking = true;
    countyOf(this.deps, viewer.account)
      .then(({ county, names }) => {
        const origins = new Map<string, { x: number; y: number }>();
        for (const r of county.rooms()) origins.set(r, county.roomOrigin(r));
        viewer.origins = origins;
        viewer.names = names;
        viewer.sentSig = '';
      })
      .catch((err: Error) => console.error(`traffic: couldn't lay out ${viewer.account}'s county:`, err.message))
      .finally(() => {
        viewer.countyAt = this.now();
        viewer.looking = false;
      });
  }

  private start(): void {
    if (this.timer) return;
    this.reportedAt = performance.now();
    this.timer = setInterval(() => this.tick(), FEED_MS);
    this.timer.unref?.();
  }

  private stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    this.report(true);
  }

  /** Close every viewer (shutting down). */
  close(): void {
    for (const v of this.viewers) v.res.end();
    this.viewers.clear();
    this.stop();
  }

  /** One tick, timed: the feed shares the game's event loop, so a slow one is said at once. */
  tick(): void {
    const start = performance.now();
    this.send();
    const ms = performance.now() - start;
    const s = this.stats;
    s.ticks++;
    s.totalMs += ms;
    s.maxMs = Math.max(s.maxMs, ms);
    if (ms > SLOW_MS && start - this.slowSaidAt > SLOW_GAP_MS) {
      this.slowSaidAt = start;
      this.log(`traffic feed: slow tick, ${ms.toFixed(1)} ms for ${this.viewers.size} viewer(s)`);
    }
    if (start - this.reportedAt >= REPORT_MS) this.report(false);
  }

  /** Once a minute while anyone's watching (and when the last stops): how the ticks went. */
  private report(last: boolean): void {
    this.reportedAt = performance.now();
    const s = this.takeStats();
    if (!s.ticks) return;
    this.log(
      `traffic feed: ${this.viewers.size} viewer(s)${last ? ' (none now)' : ''}, ${s.ticks} ticks, avg ${(s.totalMs / s.ticks).toFixed(2)} ms, max ${s.maxMs.toFixed(2)} ms, ` +
        `${s.skipped} frame(s) skipped, ${(s.bytes / 1024 / 1024).toFixed(1)} MB sent`,
    );
  }

  /** Every room packed once, and each viewer sent the rooms of its county: the packed bytes themselves, never copied per viewer. */
  private send(): void {
    const now = this.now();
    const byKey = new Map<string, Packed[]>();
    let packed = false;
    for (const viewer of this.viewers) {
      if (now - viewer.countyAt > COUNTY_REFRESH_MS) this.lookUp(viewer);
      if (!viewer.origins) continue;
      // Its socket hasn't taken the last frame yet: skip this one, rather than queue it.
      if (viewer.res.writableNeedDrain) {
        this.stats.skipped++;
        if (!viewer.stuckSince) viewer.stuckSince = now;
        else if (now - viewer.stuckSince > STUCK_MS) {
          // Nobody's reading: a closed tab whose proxy never said so. It mustn't hold a place.
          this.viewers.delete(viewer);
          this.log(`traffic feed: let go of a viewer that took nothing for ${Math.round((now - viewer.stuckSince) / 1000)} s`);
          if (viewer.res.destroy) viewer.res.destroy();
          else viewer.res.end();
          if (this.viewers.size === 0) this.stop();
        }
        continue;
      }
      viewer.stuckSince = 0;
      if (!packed) {
        packed = true;
        for (const room of this.rooms()) {
          const list = byKey.get(room.town) ?? [];
          list.push(pack(room, now));
          byKey.set(room.town, list);
        }
      }
      const mine: Packed[] = [];
      let size = 8 + 2;
      for (const key of viewer.origins.keys()) {
        for (const p of byKey.get(key) ?? []) {
          mine.push(p);
          size += p.bytes.length;
        }
      }
      const res = viewer.res;
      res.cork();
      const sig = mine.map((p) => `${p.room.watchId}#${p.sig}`).join(';') || '-';
      if (sig !== viewer.sentSig) {
        const rooms = mine.map((p) => {
          const o = viewer.origins!.get(p.room.town)!;
          return { id: p.room.watchId, key: p.room.town, name: viewer.names.get(p.room.town) ?? (p.room.town.startsWith(`${NORTH}:`) ? 'Countryside' : 'A town'), ox: o.x, oy: o.y, host: p.host, players: p.players };
        });
        const json = Buffer.from(JSON.stringify({ rooms }), 'utf8');
        res.write(frameHead(FRAME_ROOMS, json.length));
        res.write(json);
        this.stats.bytes += 5 + json.length;
        viewer.sentSig = sig;
      }
      const head = frameHead(FRAME_POSITIONS, size, 10);
      head.writeDoubleLE(now, 5);
      head.writeUInt16LE(mine.length, 13);
      res.write(head);
      for (const p of mine) res.write(p.bytes);
      res.uncork();
      this.stats.bytes += 5 + size;
    }
  }
}

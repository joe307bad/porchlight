// The traffic view's data (manage/, the React app at /manage): the county as
// a map, and every car, townsperson and player in it, live.
//
//   GET /mp/traffic/county   the signed-in account's county: its towns (and their
//                            roads: saved for the ones that come with the game,
//                            the simulation's for the rest), the
//                            highways and the spaces between, and `sim`: whether
//                            this server runs the towns' simulation (JSON)
//   GET /mp/traffic/feed     a stream of binary frames, about 10 a second, for
//                            as long as the request stays open
//   POST /mp/traffic/focus   { feed, towns: [key] }: the towns that viewer is
//                            looking at closely (the rest come once a second)
//   GET /mp/sim/where?town=<key>&kind=car|person&i=<n>
//                            where that car or townsperson is right now, and (the
//                            simulation's) who they are and what they're doing (JSON)
//   GET /mp/sim/life?town=<key>
//                            the town's life: its clock, who's indoors where, off the
//                            map at each gate, and the day's figures (JSON)
//   GET /mp/sim/building?town=<key>&i=<n>
//                            who's in that building (zoning's order) and when each next goes out
//   GET /mp/sim/focus?town=<key>&kind=car|person&slot=<n> (or &id=<n>)
//                            one car or townsperson, found by where it's drawn (its place in
//                            the positions frame) or who it is (the id an earlier answer gave):
//                            its decision tree, its queue of what's next, its day, and its way
//                            ahead (docs/town-life.md §11; JSON); &brief=1: just a line of who
//                            and what they're doing, for the map's hover
//   GET /mp/sim/signals?town=<key>[&stops=1]
//                            the town's traffic lights now: each signal's stage showing green
//                            and when it ends, and a lamp at each stop line with the stage it
//                            goes on; &stops=1: its stop signs too (docs/town-life.md §11; JSON)
//   GET /mp/sim/congestion?town=<key>&ago=<hours back, 0–23>
//                            the last day's hold-ups an hour at a time, and the streets that
//                            held cars up most in that hour, each with its centreline (JSON)
//
// One timer serves every viewer: each tick packs each town once (its players,
// and its cars and townsfolk exactly as the game packs them, WorldSync.cs: the
// server's simulation's, ../sim/, or where there's none its host's), and each
// viewer is sent the towns of its own county, joined. The towns it's looking at
// closely (it says which: /focus) come every tick; the rest every tick with
// their players, but their traffic only once a second. A viewer whose connection
// can't keep up skips ticks rather than queueing them. A town someone made is
// only ever sent to them.
//
// Each frame: u32 length (of what follows), u8 type, then
//
//   type 1, rooms: UTF-8 JSON { feed, rooms: [{ id, key, name, ox, oy, host, players: [{ sid, id, name, look }] }] }
//     sent first, and again whenever the towns or who's in them change. ox, oy:
//     where the town's positions are measured from, on the county map. feed:
//     this stream's name, for /focus.
//   type 2, positions:
//     f64 server time (ms), u16 room count, then per room (in the last rooms frame's order)
//       u32 id, u16 age of its world (ms; 65535: none, or older; 65534: as last sent,
//         and no cars or people follow)
//       u16 players, then per player (in its rooms entry's order):
//         f32 x, f32 z, f32 y, u16 heading (0-65535), u8 anim, u8 car (model + 1), i16 ride (the driver's index, or -1)
//       u16 cars, then 7 bytes each as the game packs them
//       u16 people, then 7 bytes each likewise
//   Everything little-endian but the cars' and people's records (WorldSync.cs:
//   big-endian u16 x and y in hundredths of a tile, heading 0-65535, u8 speed in fortieths of a tile a second).

import { randomBytes } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { County, mineSlot, NORTH, type Space } from '../county.ts';
import { foreignOrigin, readSession, type GameDeps } from './accounts.ts';
import { openRooms, type CityRoom } from './CityRoom.ts';
import { STARTER_TOWNS } from './persistence.ts';
import { SERVER_HOST } from './protocol.ts';
import type { SimLink } from '../sim/link.ts';
import type { SimTown, TownZoning } from '../sim/pool.ts';

const COUNTY = '/mp/traffic/county';
const FEED = '/mp/traffic/feed';
const FOCUS = '/mp/traffic/focus';
const WHERE = '/mp/sim/where';
const LIFE = '/mp/sim/life';
const BUILDING = '/mp/sim/building';
const SIM_FOCUS = '/mp/sim/focus';
const SIM_SIGNALS = '/mp/sim/signals';
const SIM_CONGESTION = '/mp/sim/congestion';
/** A town not looked at closely: its traffic once in this many ticks. */
export const QUIET_EVERY = 10;

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
/** Its cars and townsfolk as last sent: none in this frame. */
const KEPT = 0xfffe;

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
    /** Its streets (town's own frame): [kind, width, x0, y0, x1, y1, ...], when known (saved, or the simulation's once it's made the town). */
    roads: number[][] | null;
    /** What its buildings are for (Zoning.cs), as for roads: saved, or the simulation's. */
    zoning: TownZoning['figures'] | null;
    /** Each building: [zone, x0, y0, x1, y1 (tiles, exclusive), homes, jobs], the town's frame. */
    buildings: number[][] | null;
  }[];
  /** The junction north of Porchlight, and an account's empty places for towns. */
  spaces: { kind: string; x0: number; y0: number; width: number; height: number }[];
  /** Each highway's centreline: x0, y0, x1, y1, ... */
  highways: number[][];
}

/** A town's map as Manage draws it: its streets and, if known, what its buildings are for. */
export interface TownMap {
  roads: number[][];
  zoning: TownZoning | null;
}

/** The maps of the towns that come with the game, by key (towns/*.roads.json, written by `Checks roadmap`). */
let starterMaps: Map<string, TownMap> | null = null;
function mapOf(key: string): TownMap | null {
  if (!starterMaps) {
    starterMaps = new Map();
    const dir = new URL('../../towns/', import.meta.url);
    try {
      for (const f of readdirSync(dir)) {
        if (!f.endsWith('.roads.json')) continue;
        try {
          const doc = JSON.parse(readFileSync(new URL(f, dir), 'utf8')) as { key: string; roads: number[][]; zoning?: TownZoning['figures']; buildings?: number[][] };
          starterMaps.set(doc.key, { roads: doc.roads, zoning: doc.zoning && doc.buildings ? { figures: doc.zoning, buildings: doc.buildings } : null });
        } catch (err) {
          console.error(`traffic: couldn't read ${f}:`, (err as Error).message);
        }
      }
    } catch {
      // No towns folder (a test's): no streets.
    }
  }
  return starterMaps.get(key) ?? null;
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

/** A town's streets and zoning: saved for those that come with the game; for the rest, as the simulation made them. */
export function countyMapJson(county: County, names: Map<string, string>, made: (key: string) => TownMap | null = () => null): CountyMapJson {
  const box = (s: Space) => ({ x0: s.x0, y0: s.y0, width: s.width, height: s.height });
  return {
    towns: county.spaces
      .filter((s) => s.kind === 'town')
      .map((s) => {
        const key = s.townKey!;
        const saved = mapOf(key);
        const sim = made(key);
        const roads = saved?.roads ?? sim?.roads ?? null;
        const zoning = saved?.zoning ?? sim?.zoning ?? null;
        return { key, name: names.get(key) ?? 'A town', ...box(s), private: s.isPrivate, roads, zoning: zoning?.figures ?? null, buildings: zoning?.buildings ?? null };
      }),
    spaces: county.spaces.filter((s) => s.kind === 'junction' || s.kind === 'vacant').map((s) => ({ kind: s.kind, ...box(s) })),
    highways: county.highways.map((h) => h.points.flatMap((p) => [round1(p.x), round1(p.y)])),
  };
}

/** A town (or the north room): its rooms here (a busy town can have two), and the simulation's run of it. */
interface Source {
  key: string;
  id: number;
  rooms: CityRoom[];
  sim: SimTown | null;
  /** Packed this tick: in full, and with only its players. */
  full?: Packed;
  quiet?: Packed;
}

/** One town, packed once a tick for every viewer who can see it. */
interface Packed {
  /** Changes whenever who's in the room (or what they're called, or look like, or who hosts) does. */
  sig: string;
  players: { sid: string; id: string; name: string; look: string }[];
  host: number;
  /** Its part of a positions frame. */
  bytes: Buffer;
}

function pack(src: Source, now: number, withWorld: boolean): Packed {
  const list = src.rooms.flatMap((r) => [...r.watchPlayers()]);
  const index = new Map(list.map((p, i) => [p.sessionId, i]));
  // The server's simulation of it, else its host's.
  const world = src.sim ? { cars: src.sim.cars, people: src.sim.people, at: src.sim.at } : (src.rooms.find((r) => r.world)?.world ?? null);
  const fresh = world ? Math.max(0, Math.min(NO_WORLD, now - world.at)) : NO_WORLD;
  const age = withWorld ? fresh : KEPT;
  const cars = world && age < KEPT ? world.cars : null;
  const people = world && age < KEPT ? world.people : null;
  const carBytes = cars ? cars.byteLength - (cars.byteLength % 7) : 0;
  const peopleBytes = people ? people.byteLength - (people.byteLength % 7) : 0;
  const b = Buffer.allocUnsafe(4 + 2 + 2 + list.length * PLAYER_BYTES + (age === KEPT ? 0 : 2 + carBytes + 2 + peopleBytes));
  let o = 0;
  o = b.writeUInt32LE(src.id >>> 0, o);
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
  if (age !== KEPT) {
    o = b.writeUInt16LE(carBytes / 7, o);
    if (cars) o += Buffer.from(cars.buffer, cars.byteOffset, carBytes).copy(b, o);
    o = b.writeUInt16LE(peopleBytes / 7, o);
    if (people) o += Buffer.from(people.buffer, people.byteOffset, peopleBytes).copy(b, o);
  }
  const players = list.map((p) => ({ sid: p.sessionId, id: p.characterId, name: p.name, look: p.look }));
  let host = -1;
  for (const r of src.rooms) host = Math.max(host, index.get(r.state?.host ?? '') ?? -1);
  const sig = `${host}|${players.map((p) => `${p.sid}:${p.name}:${p.look}`).join(',')}`;
  return { sig, players, host, bytes: b };
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
  /** This stream's name (for /focus). */
  id: string;
  account: string;
  /** The towns it's looking at closely (null: it hasn't said; everything, every tick). */
  focus: Set<string> | null;
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
  private readonly sim: SimLink | null;
  /** Each town's number in the feed, never reused while this process runs. */
  private readonly ids = new Map<string, number>();
  private ticks = 0;
  private stats: FeedStats = { ticks: 0, totalMs: 0, maxMs: 0, skipped: 0, bytes: 0 };
  private reportedAt = 0;
  private slowSaidAt = -Infinity;
  /** Where reports go (the server log; a test listens). */
  log: (line: string) => void = (line) => console.log(line);

  constructor(deps: GameDeps, rooms: () => Iterable<CityRoom> = openRooms, now: () => number = Date.now, sim: SimLink | null = null) {
    this.deps = deps;
    this.rooms = rooms;
    this.now = now;
    this.sim = sim;
  }

  private idOf(key: string): number {
    let id = this.ids.get(key);
    if (id === undefined) this.ids.set(key, (id = this.ids.size + 1));
    return id;
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
    const [path, query = ''] = (req.url ?? '/').split('?');
    if (path !== COUNTY && path !== FEED && path !== FOCUS && path !== WHERE && path !== LIFE && path !== BUILDING && path !== SIM_FOCUS && path !== SIM_SIGNALS && path !== SIM_CONGESTION) return false;
    const send = (status: number, body: unknown): true => {
      res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' });
      res.end(JSON.stringify(body));
      return true;
    };
    if (req.method !== (path === FOCUS ? 'POST' : 'GET')) return send(405, { error: 'Method not allowed.' });
    const session = readSession(this.deps.sessionSecret, req.headers.cookie);
    if (!session) return send(401, { error: 'Not signed in.' });
    if (foreignOrigin(this.deps.appUrl, req.headers.origin)) return send(403, { error: 'Wrong origin.' });
    if (!this.deps.persistence.ready) return send(503, { error: 'The game is starting up. Try again in a moment.' });
    const account = await this.deps.persistence.account(session.sub, session.name);
    if (account.banned) return send(403, { error: 'This account is banned.' });

    if (path === COUNTY) {
      const { county, names } = await countyOf(this.deps, account.id);
      // `sim`: whether this server runs its towns' life at all (without it, Manage has no lights, stop signs,
      // people in buildings or focus to show, and says so).
      return send(200, {
        ...countyMapJson(county, names, (key) => {
          const t = this.sim?.pool.town(key);
          return t?.roads ? { roads: t.roads, zoning: t.zoning } : null;
        }),
        sim: this.sim !== null,
      });
    }

    if (path === FOCUS) {
      let body: { feed?: unknown; towns?: unknown };
      try {
        body = JSON.parse(await readBody(req, 64 * 1024));
      } catch {
        return send(400, { error: 'Bad focus.' });
      }
      const viewer = [...this.viewers].find((v) => v.id === body.feed && v.account === account.id);
      if (!viewer) return send(404, { error: 'No such feed.' });
      const towns = Array.isArray(body.towns) ? body.towns.filter((t): t is string => typeof t === 'string').slice(0, 64) : [];
      viewer.focus = new Set(towns);
      return send(200, { ok: true });
    }

    if (path === WHERE) {
      const q = new URLSearchParams(query);
      const key = q.get('town') ?? '';
      const kind = q.get('kind');
      const i = Number(q.get('i'));
      if (!(await this.deps.persistence.mayEnter(account.id, key))) return send(403, { error: "That town is someone else's." });
      // What the players see: the server's simulation, or the player running it while it doesn't.
      const src = this.sources(Date.now()).get(key);
      const town = src?.sim ?? src?.rooms.find((r) => r.world)?.world ?? null;
      if (!town) return send(404, { error: 'Nobody\'s running that town.' });
      const list = kind === 'car' ? town.cars : kind === 'person' ? town.people : null;
      if (!list || !Number.isInteger(i) || i < 0 || i >= list.byteLength / 7) return send(404, { error: 'No such car or townsperson.' });
      const v = new DataView(list.buffer, list.byteOffset + i * 7, 7);
      // An empty slot (nobody out in it now: WorldSync.HIDDEN).
      const hidden = v.getUint16(0) === 0xffff && v.getUint16(2) === 0xffff;
      // The simulation knows who it is, too.
      const about = src?.sim && this.sim ? await this.sim.pool.ask(key, kind as 'car' | 'person', i) : null;
      return send(200, {
        town: key,
        kind,
        i,
        hidden,
        x: hidden ? null : v.getUint16(0) / 100,
        y: hidden ? null : v.getUint16(2) / 100,
        heading: (v.getUint16(4) / 65536) * 2 * Math.PI,
        speed: v.getUint8(6) / 40,
        at: town.at,
        about,
      });
    }

    if (path === SIM_FOCUS) {
      const q = new URLSearchParams(query);
      const key = q.get('town') ?? '';
      const kind = q.get('kind');
      const by = q.has('id') ? 'id' : 'slot';
      const i = Number(q.get(by));
      if (!(await this.deps.persistence.mayEnter(account.id, key))) return send(403, { error: "That town is someone else's." });
      if ((kind !== 'car' && kind !== 'person') || !Number.isInteger(i) || i < 0) return send(400, { error: 'Say kind=car or person, and slot= or id=.' });
      const sim = this.sim?.running(key, Date.now()) ?? null;
      if (!sim) return send(404, { error: "The simulation isn't running that town." });
      const brief = q.get('brief') === '1';
      const answer = await this.sim!.pool.ask(key, `${brief ? 'brief' : 'focus'}-${kind}` as 'focus-car' | 'focus-person' | 'brief-car' | 'brief-person', i, by);
      if (!answer) return send(404, { error: kind === 'car' ? 'No such car.' : 'No such townsperson.' });
      return send(200, { town: key, ...(answer as object) });
    }

    if (path === SIM_SIGNALS) {
      const q = new URLSearchParams(query);
      const key = q.get('town') ?? '';
      if (!(await this.deps.persistence.mayEnter(account.id, key))) return send(403, { error: "That town is someone else's." });
      const sim = this.sim?.running(key, Date.now()) ?? null;
      if (!sim) return send(404, { error: "The simulation isn't running that town." });
      const answer = await this.sim!.pool.ask(key, 'signals', q.get('stops') === '1' ? 1 : 0);
      if (!answer) return send(404, { error: "The simulation isn't running that town." });
      return send(200, { town: key, ...(answer as object) });
    }

    if (path === SIM_CONGESTION) {
      const q = new URLSearchParams(query);
      const key = q.get('town') ?? '';
      const ago = Number(q.get('ago') ?? '0');
      if (!Number.isInteger(ago) || ago < 0 || ago > 23) return send(400, { error: 'Say ago= hours back, 0 to 23.' });
      if (!(await this.deps.persistence.mayEnter(account.id, key))) return send(403, { error: "That town is someone else's." });
      const sim = this.sim?.running(key, Date.now()) ?? null;
      if (!sim) return send(404, { error: "The simulation isn't running that town." });
      const answer = await this.sim!.pool.ask(key, 'congestion', ago);
      if (!answer) return send(404, { error: "The simulation isn't running that town." });
      return send(200, { town: key, ...(answer as object) });
    }

    if (path === LIFE || path === BUILDING) {
      const q = new URLSearchParams(query);
      const key = q.get('town') ?? '';
      if (!(await this.deps.persistence.mayEnter(account.id, key))) return send(403, { error: "That town is someone else's." });
      const sim = this.sim?.running(key, Date.now()) ?? null;
      if (!sim || !sim.life) return send(404, { error: "The simulation isn't running that town." });
      if (path === LIFE) return send(200, { town: key, ...sim.life });
      const i = Number(q.get('i'));
      if (!Number.isInteger(i) || i < 0) return send(404, { error: 'No such building.' });
      const answer = await this.sim!.pool.ask(key, 'building', i);
      if (!answer) return send(404, { error: 'No such building.' });
      return send(200, { town: key, ...(answer as object) });
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
    const viewer: Viewer = { id: randomBytes(9).toString('base64url'), focus: null, account, res, origins: origins ?? null, names: new Map(), countyAt: origins ? Infinity : 0, looking: false, sentSig: '', stuckSince: 0 };
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
    const tick = this.ticks++;
    let sources: Map<string, Source> | null = null;
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
      sources ??= this.sources(now);
      // Its towns: those it's looking at closely (or all, if it hasn't said) with their traffic every
      // tick; the rest with it once a second, and only their players in between.
      const mine: { src: Source; p: Packed }[] = [];
      let size = 8 + 2;
      const everyone = viewer.focus === null || tick % QUIET_EVERY === 0;
      for (const key of viewer.origins.keys()) {
        const src = sources.get(key);
        if (!src) continue;
        const close = everyone || viewer.focus!.has(key);
        const p = close ? (src.full ??= pack(src, now, true)) : (src.quiet ??= pack(src, now, false));
        mine.push({ src, p });
        size += p.bytes.length;
      }
      const res = viewer.res;
      res.cork();
      const sig = mine.map(({ src, p }) => `${src.id}#${p.sig}`).join(';') || '-';
      if (sig !== viewer.sentSig) {
        const rooms = mine.map(({ src, p }) => {
          const o = viewer.origins!.get(src.key)!;
          return { id: src.id, key: src.key, name: viewer.names.get(src.key) ?? (src.key.startsWith(`${NORTH}:`) ? 'Countryside' : 'A town'), ox: o.x, oy: o.y, host: p.host, players: p.players };
        });
        const json = Buffer.from(JSON.stringify({ feed: viewer.id, rooms }), 'utf8');
        res.write(frameHead(FRAME_ROOMS, json.length));
        res.write(json);
        this.stats.bytes += 5 + json.length;
        viewer.sentSig = sig;
      }
      const head = frameHead(FRAME_POSITIONS, size, 10);
      head.writeDoubleLE(now, 5);
      head.writeUInt16LE(mine.length, 13);
      res.write(head);
      for (const { p } of mine) res.write(p.bytes);
      res.uncork();
      this.stats.bytes += 5 + size;
    }
  }

  /** Every town there's something of this tick: the simulation's (with or without players), and any with a room open. */
  private sources(now: number): Map<string, Source> {
    const out = new Map<string, Source>();
    const get = (key: string) => {
      let src = out.get(key);
      if (!src) out.set(key, (src = { key, id: this.idOf(key), rooms: [], sim: this.sim?.running(key, now) ?? null }));
      return src;
    };
    if (this.sim) for (const t of this.sim.pool.all()) if (this.sim.running(t.key, now)) get(t.key);
    for (const room of this.rooms()) {
      const src = get(room.town);
      src.rooms.push(room);
      // A player runs this town's traffic (the server doesn't, for now): theirs is what the game shows.
      const host = room.state?.host ?? '';
      if (host && host !== SERVER_HOST) src.sim = null;
    }
    return out;
  }
}

/** A request's body, up to `max` bytes. */
function readBody(req: IncomingMessage, max: number): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let size = 0;
    req.on('data', (c: Buffer) => {
      size += c.length;
      if (size > max) {
        reject(new Error('too big'));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

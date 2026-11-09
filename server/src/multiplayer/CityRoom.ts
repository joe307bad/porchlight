// One town, shared. Everyone who opens the same town (seed and params) in
// explore mode lands in the same room (up to 100; past that a second copy of
// the town opens). Each client plays one of their account's characters and
// tells the room where it is about 30 times a second; the room checks each
// move as it arrives, and sends every client the players near them
// (interest.ts) as delta patches, also about 30 a second. Each position
// carries the time its sender took it, which is what other clients play it
// back by: the room's own ticks never decide its timing.
//
// The town itself is shared too: one client, the host, runs its traffic and
// townsfolk and sends them out about 10 times a second, which the room passes
// on to everyone else (if the host goes quiet or leaves, another client takes
// over from where things were). Night or day, the town's cars someone has got
// into and everyone's parked car are in the room's state.
//
// The tick never waits on the database. A character is read once when it
// joins, and written in batches every 30 s (if it moved), when it leaves, and
// on shutdown. If the database is down the game goes on and the writes wait.

import { CloseCode, Room, ServerError, type AuthContext, type Client } from '@colyseus/core';
import { StateView } from '@colyseus/schema';
import { foreignOrigin, readSession, type GameDeps } from './accounts.ts';
import { Interest } from './interest.ts';
import { ARRIVE_GRACE_SECONDS, MAX_SPEED, RateLimit, Strikes, clampToBounds, inBounds, judgeMove, newTrack, parseMove, type Bounds, type Track } from './movement.ts';
import { cleanLook, STARTER_TOWNS, type EndReason, type Snapshot } from './persistence.ts';
import { County, mineSlot } from '../county.ts';
import {
  ANIM_DRIVE,
  ANIM_IDLE,
  CAR_SEATS,
  CLOSE_IN_USE,
  IN_USE,
  CLOSE_KICKED,
  CLOSE_SIGNED_IN_ELSEWHERE,
  HOST_SILENT_MS,
  MSG_CAR,
  MSG_CORRECT,
  MSG_DOTS,
  MSG_LOOK,
  MSG_MISSION_DONE,
  MSG_MOVE,
  MSG_NIGHT,
  MSG_SPAWN,
  MSG_TAKE,
  MSG_TELEPORT,
  MSG_UNLOCK,
  MSG_UNLOCKS,
  MSG_WORLD,
  UNLOCKS,
  lookAllowed,
  SEND_MS,
  WORLD_MAX_BYTES,
  WORLD_PER_SECOND,
  quantizeYaw,
  yawFromWire,
  type CorrectMessage,
  type DotsMessage,
  type MoveMessage,
  type SpawnMessage,
} from './protocol.ts';
import { CityState, ParkedCar, Player } from './schema.ts';

/** The room's housekeeping tick (idle players, who sees whom). Moves don't wait for it. */
const TICK_MS = 50;
const FLUSH_MS = 30_000;
const DOTS_MS = 1_000;
/** Seconds a dropped client has to come back. */
const RECONNECT_SECONDS = 20;
/** How close (tiles) to a car you have to be to get in with its driver. */
const RIDE_REACH = 3;
/** No moves for this long: show them standing. */
const IDLE_AFTER_MS = 2_000;
/** A new look is saved this long after the last change. */
const LOOK_SAVE_DELAY_MS = 2_000;
/** Most minimap jumps a second. */
const TELEPORT_GAP_MS = 1_000;
/** Shutdown doesn't wait longer than this on the database. */
const SHUTDOWN_SAVE_MS = 8_000;
/** Most cars a town's `taken` list holds. */
const MAX_TAKEN = 4_000;

interface JoinAuth {
  accountId: string;
  characterId: string;
  name: string;
  look: string | null;
}

interface JoinOptions {
  town?: unknown;
  width?: unknown;
  height?: unknown;
  character?: unknown;
  /**
   * Driving (or flying) in from the next room's ground: where they are now, in this room's frame
   * ({ x, y, z, yaw }). They carry on from there, if it's this room's ground.
   */
  arrive?: unknown;
  /** Which tab or device this is (it makes this up, and keeps it for as long as the tab lasts). */
  tab?: unknown;
}

/** Everything about a player the clients don't get. */
interface PlayerRuntime {
  sessionId: string;
  accountId: string;
  characterId: string;
  player: Player;
  view: StateView;
  client: Client | null;
  sessionRow: string | null;
  joinedAt: number;
  track: Track;
  lastInputAt: number;
  lastTeleportAt: number;
  rate: RateLimit;
  /** World messages this second (while they're the host). */
  worldRate: RateLimit;
  strikes: Strikes;
  /** Bumped on every saveable change; `savedChange` is the last one written. */
  change: number;
  savedChange: number;
  version: number;
  lookTimer: ReturnType<typeof setTimeout> | null;
  /** What their account has unlocked (UNLOCKS). */
  unlocks: string[];
  /** Saved and signed off (left, timed out, kicked, shut down): nothing more to do. */
  finished: boolean;
}

/** Characters being played right now, across every room in this process. */
const online = new Map<string, { room: CityRoom; sessionId: string; tab: string }>();

export const isOnline = (characterId: string): boolean => online.has(characterId);

/** Is someone other than this tab playing this character, and still connected? */
const inUse = (o: { room: CityRoom; sessionId: string; tab: string } | undefined, tab: string): boolean => !!o && o.tab !== tab && o.room.connected(o.sessionId);

/** This character right now, as `tab` sees it: the town (its room's key) and session they're playing in, and whether someone else has them. */
export const presence = (characterId: string, tab: string): { town: string | null; session: string | null; inUse: boolean } => {
  const o = online.get(characterId);
  return { town: o?.room.town ?? null, session: o?.sessionId ?? null, inUse: inUse(o, tab) };
};

/** How many characters are playing in this town (its room's key) right now. */
export const playingIn = (town: string): number => [...online.values()].filter((o) => o.room.town === town).length;

/** Night or day in each town, kept while this process runs (a town's room closes when everyone's left). */
/** Night or day: the same all over the county, so driving to the next town doesn't change it. */
let night = false;
/** The rooms open in this process (to tell them all when night falls, or someone's county changes). */
const rooms = new Set<CityRoom>();
/** How far past its ground (tiles) a room still takes a move: the moment between crossing over and joining the next room. */
const TERRITORY_SLACK = 16;

/** An account made or deleted a town: the rooms in its part of the county work out their ground again. */
export function countyChanged(account: string): void {
  for (const r of rooms) if (r.countyAccount === account) void r.loadCounty();
}

/** Where someone driving in says they are (this room's frame), or null. */
function parseArrive(raw: unknown): { x: number; y: number; z: number; yaw: number } | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const a = raw as Record<string, unknown>;
  const ok = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
  return ok(a.x) && ok(a.z) && ok(a.yaw) ? { x: a.x, y: ok(a.y) ? a.y : 0, z: a.z, yaw: a.yaw } : null;
}

const byteLength = (v: unknown): number => (v instanceof Uint8Array ? v.byteLength : -1);

let deps: GameDeps | null = null;

/** Hand the room what it needs (before any room is created). */
export function configureCityRoom(d: GameDeps): void {
  deps = d;
}

const withTimeout = <T>(p: Promise<T>, ms: number): Promise<T> =>
  Promise.race([p, new Promise<T>((_, reject) => setTimeout(() => reject(new Error(`timed out after ${ms} ms`)), ms).unref())]);

export class CityRoom extends Room<{ state: CityState }> {
  maxClients = 100;
  patchRate = SEND_MS;
  autoDispose = true;
  /** Colyseus's own hard limit; RateLimit below drops and kicks well before it. */
  maxMessagesPerSecond = 200;

  town = '';
  bounds: Bounds = { width: 0, height: 0 };
  private readonly runtime = new Map<string, PlayerRuntime>();
  private readonly interest = new Interest();
  /** The `taken` list, as a set. */
  private readonly taken = new Set<string>();
  /** When the host was chosen, and last sent the world. */
  private hostSince = 0;
  private hostHeardAt = 0;
  private shuttingDown = false;
  private flushing = false;
  private flushFailures = 0;
  private flushSkip = 0;
  private tickTotal = 0;
  private tickMax = 0;
  private ticks = 0;

  private get deps(): GameDeps {
    if (!deps) throw new Error('configureCityRoom() was never called');
    return deps;
  }

  /** The account whose part of the county this room's in (null: the public part, or no county). */
  countyAccount: string | null = null;

  /**
   * The room's ground: its town, its half of each of its highways and the countryside nearest
   * it (county.ts), once the county's known; until then (or for a town no one's saved), the town.
   */
  async loadCounty(): Promise<void> {
    try {
      const found = await this.deps.persistence.countyFor(this.town);
      if (!found) return;
      const county = new County(
        [...STARTER_TOWNS.map((t) => ({ place: t.place, key: t.key })), ...found.mine.filter((m) => m.slot >= 0 && m.slot < 3).map((m) => ({ place: mineSlot(m.slot), key: m.key }))],
        found.account ?? undefined,
      );
      if (!county.rooms().includes(this.town)) return;
      const home = county.home(this.town);
      this.countyAccount = found.account;
      this.bounds = { x0: home.x0, z0: home.z0, width: home.x1 - home.x0, height: home.z1 - home.z0, owns: county.territory(this.town, TERRITORY_SLACK) };
    } catch (err) {
      console.error(`city: couldn't lay out ${this.town}'s county:`, (err as Error).message);
    }
  }

  async onCreate(options: JoinOptions): Promise<void> {
    const town = typeof options.town === 'string' ? options.town : '';
    const width = Number(options.width);
    const height = Number(options.height);
    if (!town || town.length > 512 || !(width >= 16 && width <= 1024) || !(height >= 16 && height <= 1024)) {
      throw new ServerError(400, 'bad town');
    }
    this.town = town;
    this.bounds = { width, height };
    rooms.add(this);
    await this.loadCounty();
    this.state = new CityState();
    this.state.night = night;
    this.state.taken = '';
    this.state.host = '';

    this.onMessage(MSG_MOVE, (client, raw: unknown) => {
      const rt = this.admit(client);
      const move = rt && parseMove(raw);
      if (!rt || !move) return;
      // Applied as it arrives (in order: the socket keeps it), not on the next tick.
      this.applyMove(rt, move, Date.now());
    });
    this.onMessage(MSG_TELEPORT, (client, raw: unknown) => {
      const rt = this.admit(client);
      const m = raw as { x?: unknown; z?: unknown } | null;
      if (!rt || typeof m?.x !== 'number' || typeof m.z !== 'number' || !Number.isFinite(m.x) || !Number.isFinite(m.z)) return;
      this.applyTeleport(rt, m.x, m.z, Date.now());
    });
    this.onMessage(MSG_LOOK, (client, raw: unknown) => {
      const rt = this.admit(client);
      const look = cleanLook((raw as { model?: unknown } | null)?.model);
      if (!rt || !look || look === rt.player.appearance || !lookAllowed(look, rt.unlocks)) return;
      rt.player.appearance = look;
      rt.change++;
      // Debounced: someone flicking through characters is saved once they settle.
      if (rt.lookTimer) clearTimeout(rt.lookTimer);
      rt.lookTimer = setTimeout(() => {
        rt.lookTimer = null;
        if (!rt.finished) void this.save([rt]);
      }, LOOK_SAVE_DELAY_MS);
    });
    this.onMessage(MSG_UNLOCK, (client, raw: unknown) => {
      const rt = this.admit(client);
      const what = (raw as { what?: unknown } | null)?.what;
      if (!rt || typeof what !== 'string' || !UNLOCKS.includes(what) || rt.unlocks.includes(what)) return;
      rt.unlocks.push(what);
      // The account's: its other characters playing right now have it too.
      for (const o of online.values()) o.room.granted(rt, o.sessionId, what);
      this.deps.persistence.unlock(rt.accountId, what).catch((err: Error) => console.error(`city: couldn't keep ${rt.accountId}'s unlock ${what}:`, err.message));
    });
    this.onMessage(MSG_MISSION_DONE, (client, raw: unknown) => {
      const rt = this.admit(client);
      const id = (raw as { id?: unknown } | null)?.id;
      if (!rt || typeof id !== 'string') return;
      // Only real missions: nothing's kept for any other name a client makes up.
      this.deps.missions
        .get(id)
        .then((mission) => (mission ? this.deps.persistence.completeMission(rt.accountId, rt.characterId, id) : undefined))
        .catch((err: Error) => console.error(`city: couldn't keep ${rt.accountId} finishing ${id}:`, err.message));
    });

    this.onMessage(MSG_NIGHT, (client, raw: unknown) => {
      const rt = this.admit(client);
      const on = (raw as { on?: unknown } | null)?.on;
      if (!rt || typeof on !== 'boolean') return;
      night = on;
      for (const r of rooms) r.state.night = on;
    });
    this.onMessage(MSG_TAKE, (client, raw: unknown) => {
      const rt = this.admit(client);
      const car = (raw as { car?: unknown } | null)?.car;
      if (!rt || typeof car !== 'string' || !/^[pt]\d{1,5}$/.test(car) || this.taken.has(car) || this.taken.size >= MAX_TAKEN) return;
      this.taken.add(car);
      this.state.taken = [...this.taken].join(',');
    });
    this.onMessage(MSG_CAR, (client, raw: unknown) => {
      const rt = this.admit(client);
      const m = raw as { model?: unknown; x?: unknown; z?: unknown; yaw?: unknown } | null;
      if (!rt || typeof m?.model !== 'number' || !Number.isInteger(m.model) || m.model < 0 || m.model > 255) return;
      if (m.model === 0) {
        this.state.cars.delete(client.sessionId);
        return;
      }
      if (typeof m.x !== 'number' || typeof m.z !== 'number' || typeof m.yaw !== 'number' || !Number.isFinite(m.yaw)) return;
      const c = clampToBounds(this.bounds, m.x, m.z);
      const car = this.state.cars.get(client.sessionId) ?? new ParkedCar();
      car.model = m.model;
      car.x = c.x;
      car.z = c.z;
      car.yaw = quantizeYaw(m.yaw);
      if (!this.state.cars.has(client.sessionId)) this.state.cars.set(client.sessionId, car);
    });
    this.onMessage(MSG_WORLD, (client, raw: unknown) => {
      const rt = this.admit(client);
      if (!rt || client.sessionId !== this.state.host) return;
      const m = raw as { cars?: unknown; people?: unknown; looks?: unknown } | null;
      const cars = byteLength(m?.cars);
      const people = byteLength(m?.people);
      if (cars < 0 || people < 0 || cars + people > WORLD_MAX_BYTES || !Array.isArray(m?.looks) || m.looks.length > 256) return;
      if (rt.worldRate.hit(Date.now()) !== 'ok') return;
      this.hostHeardAt = Date.now();
      this.broadcast(MSG_WORLD, { cars: m.cars, people: m.people, looks: m.looks.filter((l) => typeof l === 'string' && l.length < 64) }, { except: client });
    });

    this.setSimulationInterval(() => this.tick(), TICK_MS);
    this.clock.setInterval(() => void this.flush(), FLUSH_MS);
    this.clock.setInterval(() => this.sendDots(), DOTS_MS);
  }

  async onAuth(_client: Client, options: JoinOptions, context: AuthContext): Promise<JoinAuth> {
    const d = this.deps;
    const session = readSession(d.sessionSecret, context.headers.get('cookie'), context.token);
    if (!session) throw new ServerError(401, 'Not signed in.');
    if (foreignOrigin(d.appUrl, context.headers.get('origin'))) throw new ServerError(403, 'Wrong origin.');
    if (!d.persistence.ready) throw new ServerError(503, 'The game is starting up. Try again in a moment.');
    try {
      const account = await d.persistence.account(session.sub, session.name);
      if (account.banned) throw new ServerError(403, 'This account is banned.');
      const character = await d.persistence.character(account.id, String(options.character ?? ''));
      if (!character) throw new ServerError(404, 'No such character.');
      // A town someone made is theirs alone: only Porchlight (and the others that come with the game) is everyone's.
      if (!(await d.persistence.mayEnter(account.id, typeof options.town === 'string' ? options.town : ''))) throw new ServerError(403, "That town is someone else's.");
      return { accountId: account.id, characterId: character.id, name: character.name, look: character.look };
    } catch (err) {
      if (err instanceof ServerError) throw err;
      console.error('city: join refused, database unavailable:', (err as Error).message);
      throw new ServerError(503, 'Service unavailable. Try again in a moment.');
    }
  }

  async onJoin(client: Client, options: JoinOptions, auth: JoinAuth): Promise<void> {
    const d = this.deps;
    const tab = typeof options.tab === 'string' ? options.tab.slice(0, 64) : '';
    // One person to a character: someone connected on another tab or device keeps them.
    const prior = online.get(auth.characterId);
    if (inUse(prior, tab)) throw new ServerError(CLOSE_IN_USE, IN_USE);
    // This tab again (a reload, or another town), or someone who's lost their connection:
    // saved and signed out first, so what's loaded below is up to date.
    if (prior) {
      await prior.room.evict(prior.sessionId);
      const since = online.get(auth.characterId);
      if (since && since.sessionId !== prior.sessionId) throw new ServerError(CLOSE_IN_USE, IN_USE); // someone was quicker
    }
    online.set(auth.characterId, { room: this, sessionId: client.sessionId, tab });

    try {
      const saved = await d.persistence.loadState(auth.characterId);
      const earned = await d.persistence.unlocks(auth.accountId);
      const player = new Player();
      player.id = auth.characterId;
      player.name = auth.name;
      const unlocks = earned.filter((u) => UNLOCKS.includes(u));
      const look = saved?.look ?? auth.look ?? '';
      player.appearance = look && lookAllowed(look, unlocks) ? look : '';
      let at: SpawnMessage['at'] = null;
      const arrive = parseArrive(options.arrive);
      const arriving = arrive !== null && inBounds(this.bounds, arrive.x, arrive.y, arrive.z);
      if (arriving) at = { x: arrive.x, z: arrive.z, yaw: arrive.yaw };
      else if (saved && saved.town === this.town && inBounds(this.bounds, saved.x, 0, saved.z)) {
        at = { x: saved.x, z: saved.z, yaw: saved.rotY };
      }
      const sessionRow = await d.persistence.openSession(auth.characterId, this.roomId);

      this.state.players.set(client.sessionId, player);
      const view = new StateView();
      view.add(player); // always see yourself
      client.view = view;
      this.runtime.set(client.sessionId, {
        sessionId: client.sessionId,
        accountId: auth.accountId,
        characterId: auth.characterId,
        player,
        view,
        client,
        sessionRow,
        joinedAt: Date.now(),
        track: newTrack(),
        lastInputAt: Date.now(),
        lastTeleportAt: 0,
        // Moves come about 30 a second, and the host's world 10: room for those and the odd teleport, look or car.
        rate: new RateLimit(60),
        worldRate: new RateLimit(WORLD_PER_SECOND),
        strikes: new Strikes(),
        change: 0,
        savedChange: 0,
        version: saved?.version ?? 0,
        lookTimer: null,
        unlocks,
        finished: false,
      });
      // Driving in from next door: they're where they say (it's this room's ground), and their
      // first move is held to the speed limit from there, like any other.
      // They went on driving while joining, though: that much is let off their first move.
      if (arriving) Object.assign(this.runtime.get(client.sessionId)!.track, { anchored: true, x: arrive.x, y: arrive.y, z: arrive.z, t: Date.now(), grace: MAX_SPEED * ARRIVE_GRACE_SECONDS });
      // Nobody sees them (and they're on no one's minimap) until their first
      // move says where they are: the client checks the spot against the town.
      client.send(MSG_SPAWN, { at, look: player.appearance || null, unlocks } satisfies SpawnMessage);
    } catch (err) {
      if (online.get(auth.characterId)?.sessionId === client.sessionId) online.delete(auth.characterId);
      this.state.players.delete(client.sessionId);
      this.runtime.delete(client.sessionId);
      if (err instanceof ServerError) throw err;
      console.error('city: join failed:', (err as Error).message);
      throw new ServerError(503, 'Service unavailable. Try again in a moment.');
    }
  }

  /** Someone on `from`'s account earned this: if this session is another of the account's, it has it too. */
  granted(from: PlayerRuntime, sessionId: string, what: string): void {
    const rt = this.runtime.get(sessionId);
    if (!rt || rt === from || rt.accountId !== from.accountId || rt.unlocks.includes(what)) return;
    rt.unlocks.push(what);
    rt.client?.send(MSG_UNLOCKS, { unlocks: rt.unlocks });
  }

  /** Dropped without saying goodbye: hold their place for a while. */
  onDrop(client: Client): void {
    const rt = this.runtime.get(client.sessionId);
    if (!rt || rt.finished || this.shuttingDown) return; // onLeave follows straight away
    rt.client = null;
    rt.player.away = true;
    if (rt.player.anim !== ANIM_DRIVE) rt.player.anim = ANIM_IDLE;
    // Not awaited: Colyseus calls onLeave if the window runs out.
    this.allowReconnection(client, RECONNECT_SECONDS);
  }

  onReconnect(client: Client): void {
    const rt = this.runtime.get(client.sessionId);
    // Signed in elsewhere while they were away: this connection is stale.
    if (!rt || rt.finished) throw new ServerError(CLOSE_SIGNED_IN_ELSEWHERE, 'Signed in elsewhere.');
    rt.client = client;
    client.view = rt.view;
    rt.player.away = false;
    rt.lastInputAt = Date.now();
    // They kept playing offline: take their next move as given, like on joining.
    rt.track.anchored = false;
  }

  async onLeave(client: Client, code?: number): Promise<void> {
    const rt = this.runtime.get(client.sessionId);
    if (!rt) return;
    const reason: EndReason = code === CloseCode.CONSENTED ? 'consented' : this.shuttingDown ? 'shutdown' : 'timeout';
    await this.finish(rt, reason);
    this.forget(rt);
  }

  /** On deploy (SIGTERM): save everyone, then let Colyseus close the room. */
  onBeforeShutdown(): void {
    this.shuttingDown = true;
    const all = [...this.runtime.values()].filter((rt) => !rt.finished);
    void withTimeout(Promise.all(all.map((rt) => this.finish(rt, 'shutdown'))), SHUTDOWN_SAVE_MS)
      .catch((err) => console.error('city: shutdown save:', (err as Error).message))
      .finally(() => void this.disconnect(CloseCode.SERVER_SHUTDOWN).catch(() => {}));
  }

  async onDispose(): Promise<void> {
    rooms.delete(this);
    // Normally everyone's been saved on their way out; catch anyone who wasn't.
    const left = [...this.runtime.values()].filter((rt) => !rt.finished);
    await Promise.all(left.map((rt) => this.finish(rt, this.shuttingDown ? 'shutdown' : 'timeout')));
  }

  /** Is this session connected (or still joining), rather than dropped and waiting to come back? */
  connected(sessionId: string): boolean {
    const rt = this.runtime.get(sessionId);
    return !rt || rt.client !== null;
  }

  /** Someone else has taken this character: save, sign off and close this session. */
  async evict(sessionId: string): Promise<void> {
    const rt = this.runtime.get(sessionId);
    if (!rt) return;
    await this.finish(rt, 'kicked');
    rt.client?.leave(CLOSE_SIGNED_IN_ELSEWHERE, 'Signed in elsewhere.');
    if (!rt.client) this.forget(rt); // dropped and waiting to come back: gone now (onReconnect refuses them)
  }

  private kick(rt: PlayerRuntime, why: string): void {
    if (rt.finished) return;
    console.warn(`city: kicking ${rt.characterId}: ${why}`);
    void this.finish(rt, 'kicked'); // marks them finished at once, so no reconnection window
    rt.client?.leave(CLOSE_KICKED, why);
  }

  /** The runtime of a client allowed to send right now, or null (and maybe a kick). */
  private admit(client: Client): PlayerRuntime | null {
    const rt = this.runtime.get(client.sessionId);
    if (!rt || rt.finished || !rt.client) return null;
    const verdict = rt.rate.hit(Date.now());
    if (verdict === 'kick') this.kick(rt, 'too many messages');
    return verdict === 'ok' ? rt : null;
  }

  private tick(): void {
    const start = performance.now();
    const now = Date.now();
    for (const rt of this.runtime.values()) {
      if (rt.finished || !rt.client) continue;
      // Gone quiet (a background tab, a sleeping laptop): show them standing.
      if (now - rt.lastInputAt > IDLE_AFTER_MS && rt.player.anim !== ANIM_IDLE && rt.player.anim !== ANIM_DRIVE) {
        rt.player.anim = ANIM_IDLE;
      }
    }
    this.chooseHost(now);
    for (const op of this.interest.drain()) {
      const viewer = this.runtime.get(op.viewer);
      const target = this.state.players.get(op.target);
      if (!viewer || !target) continue;
      if (op.add) viewer.view.add(target);
      else viewer.view.remove(target);
    }
    this.measure(performance.now() - start);
  }

  /**
   * Someone to run the town: the host stays while they're here and sending; if
   * they leave, drop out or go quiet (a hidden tab), whoever's been here
   * longest among those still playing (moving, or at least saying they're
   * there) takes over. A quiet host stays on if nobody else is playing either,
   * rather than handing the town to another quiet tab.
   */
  private chooseHost(now: number): void {
    const ready = (rt: PlayerRuntime | undefined): rt is PlayerRuntime => !!rt && !rt.finished && !!rt.client && rt.track.anchored;
    const active = (rt: PlayerRuntime): boolean => now - rt.lastInputAt <= HOST_SILENT_MS;
    const host = this.runtime.get(this.state.host);
    const silent = ready(host) && now - Math.max(this.hostHeardAt, this.hostSince) > HOST_SILENT_MS;
    if (ready(host) && !silent) return;
    let next: PlayerRuntime | null = null;
    for (const rt of this.runtime.values()) {
      if (!ready(rt) || rt === host) continue;
      if (!next || (active(rt) && !active(next)) || (active(rt) === active(next) && rt.joinedAt < next.joinedAt)) next = rt;
    }
    if (next && ready(host) && !active(next)) next = null;
    if (!next) {
      if (!ready(host) && this.state.host !== '') this.state.host = '';
      return;
    }
    this.state.host = next.sessionId;
    this.hostSince = now;
    this.hostHeardAt = 0;
  }

  private applyMove(rt: PlayerRuntime, m: MoveMessage, at: number): void {
    // A passenger who can't ride with who they say is put back below, without a strike (a race, not a cheat).
    const riding = !!m.ride && this.canRide(rt, m);
    const verdict = judgeMove(rt.track, m, at, this.bounds, { riding });
    if (verdict === 'stale') return;
    if (verdict !== 'ok') {
      // Back to the last good spot (or, before there is one, the nearest spot in town).
      if (!rt.track.anchored) {
        const c = clampToBounds(this.bounds, m.x, m.z);
        Object.assign(rt.track, { anchored: true, x: c.x, y: 0, z: c.z, t: at, grace: 0 });
        this.place(rt, c.x, 0, c.z);
      }
      this.correct(rt);
      if (rt.strikes.add(at)) this.kick(rt, `too many bad moves (${verdict})`);
      return;
    }
    if (m.ride && !riding) {
      // The car's full, or its driver got out or left: put them back where they were (no strike: a race, not a cheat).
      this.correct(rt);
      return;
    }
    Object.assign(rt.track, { anchored: true, x: m.x, y: m.y, z: m.z, t: at, seq: m.seq, grace: 0 });
    rt.lastInputAt = at;
    const p = rt.player;
    const moved = p.x !== m.x || p.z !== m.z;
    this.place(rt, m.x, m.y, m.z);
    p.rotY = quantizeYaw(m.yaw);
    p.t = m.t;
    p.anim = m.anim;
    p.car = m.ride ? 0 : m.car;
    p.ride = m.ride;
    if (moved) rt.change++;
  }

  /**
   * May they ride with `m.ride`: someone else, driving, close by, with a seat free?
   * Getting in takes being beside the car; once in, a rider sits where they see the
   * car, which is a little behind where its driver last said it was (interpolation
   * and the network both lag), so at speed they're allowed a second's drive behind.
   */
  private canRide(rt: PlayerRuntime, m: MoveMessage): boolean {
    const driver = this.runtime.get(m.ride);
    if (!driver || driver === rt || driver.finished || driver.player.anim !== ANIM_DRIVE) return false;
    // Riding in from next door (still on their arrival grace) they were already in the car, in the other room.
    const reach = rt.player.ride === m.ride || rt.track.grace > 0 ? RIDE_REACH + MAX_SPEED : RIDE_REACH;
    if (Math.hypot(driver.player.x - m.x, driver.player.z - m.z) > reach) return false;
    let riders = 0;
    for (const other of this.runtime.values()) if (other !== rt && other.player.ride === m.ride) riders++;
    return riders < CAR_SEATS - 1;
  }

  private applyTeleport(rt: PlayerRuntime, x: number, z: number, at: number): void {
    if (at - rt.lastTeleportAt < TELEPORT_GAP_MS) return;
    rt.lastTeleportAt = at;
    const c = clampToBounds(this.bounds, x, z);
    Object.assign(rt.track, { anchored: true, x: c.x, y: 0, z: c.z, t: at, grace: 0 });
    rt.player.car = 0;
    rt.player.ride = '';
    rt.player.anim = ANIM_IDLE;
    this.place(rt, c.x, 0, c.z);
    rt.change++;
    this.correct(rt);
  }

  private place(rt: PlayerRuntime, x: number, y: number, z: number): void {
    rt.player.x = x;
    rt.player.y = y;
    rt.player.z = z;
    this.interest.move(rt.sessionId, x, z);
  }

  /** Tell the client where the server has them; moves it sent before this are ignored. */
  private correct(rt: PlayerRuntime): void {
    rt.track.epoch = (rt.track.epoch + 1) & 0xffff;
    rt.track.seq = null;
    rt.client?.send(MSG_CORRECT, { x: rt.track.x, y: rt.track.y, z: rt.track.z, epoch: rt.track.epoch } satisfies CorrectMessage);
  }

  /** Everyone's position, roughly, for the minimap (far players aren't in anyone's view). */
  private sendDots(): void {
    if (!this.clients.length) return;
    const dots: DotsMessage = [];
    for (const rt of this.runtime.values()) {
      if (!rt.track.anchored || rt.finished) continue;
      dots.push([rt.sessionId, Math.round(rt.player.x * 10) / 10, Math.round(rt.player.z * 10) / 10, rt.player.name]);
    }
    this.broadcast(MSG_DOTS, dots);
  }

  private snapshot(rt: PlayerRuntime): Snapshot {
    rt.version++;
    return {
      characterId: rt.characterId,
      town: this.town,
      x: rt.player.x,
      y: 0, // never saved mid-jump
      z: rt.player.z,
      rotY: yawFromWire(rt.player.rotY),
      look: rt.player.appearance,
      version: rt.version,
    };
  }

  /** Write these players now; true if it worked. Their dirty marks clear only on success. */
  private async save(rts: PlayerRuntime[]): Promise<boolean> {
    const live = rts.filter((rt) => rt.track.anchored);
    if (!live.length) return true;
    const marks = live.map((rt) => rt.change);
    try {
      await this.deps.persistence.save(live.map((rt) => this.snapshot(rt)));
      live.forEach((rt, i) => (rt.savedChange = Math.max(rt.savedChange, marks[i])));
      return true;
    } catch (err) {
      console.error(`city: save failed for ${live.length} player(s): ${(err as Error).message}`);
      return false;
    }
  }

  /** Every 30 s: everyone who's changed since they were last saved, in one write. */
  private async flush(): Promise<void> {
    if (this.flushing) return;
    // After failures, back off: skip 1, 2, 4, then 8 intervals.
    if (this.flushSkip > 0) {
      this.flushSkip--;
      return;
    }
    this.flushing = true;
    try {
      const dirty = [...this.runtime.values()].filter((rt) => !rt.finished && rt.change !== rt.savedChange);
      if (await this.save(dirty)) this.flushFailures = 0;
      else this.flushSkip = 2 ** Math.min(3, this.flushFailures++);
    } finally {
      this.flushing = false;
    }
  }

  /** Save one last time and close their session row. Runs once per player. */
  private async finish(rt: PlayerRuntime, reason: EndReason): Promise<void> {
    if (rt.finished) return;
    rt.finished = true;
    if (rt.lookTimer) clearTimeout(rt.lookTimer);
    if (online.get(rt.characterId)?.sessionId === rt.sessionId) online.delete(rt.characterId);
    await this.save([rt]);
    if (rt.sessionRow) {
      await this.deps.persistence
        .closeSession(rt.sessionRow, rt.characterId, reason, (Date.now() - rt.joinedAt) / 1000)
        .catch((err) => console.error(`city: closing session: ${(err as Error).message}`));
    }
  }

  /** Out of the room's state, everyone's views and the grid. Safe to call twice. */
  private forget(rt: PlayerRuntime): void {
    this.interest.remove(rt.sessionId);
    this.state.players.delete(rt.sessionId);
    this.state.cars.delete(rt.sessionId);
    this.runtime.delete(rt.sessionId);
  }

  /** Tick time, logged every minute while anyone's here. */
  private measure(ms: number): void {
    this.ticks++;
    this.tickTotal += ms;
    this.tickMax = Math.max(this.tickMax, ms);
    if (this.ticks < 60_000 / TICK_MS) return;
    if (this.runtime.size) {
      console.log(
        `city ${this.roomId}: ${this.runtime.size} player(s), tick avg ${(this.tickTotal / this.ticks).toFixed(2)} ms, max ${this.tickMax.toFixed(2)} ms`,
      );
    }
    this.ticks = 0;
    this.tickTotal = 0;
    this.tickMax = 0;
  }
}

// Playing the town with other people (the server's CityRoom, through
// Colyseus). Everyone exploring the same town (seed and params) shares a
// room. This client plays one character: it tells the room where that
// character is about 20 times a second (only when something changed), hands
// the room's patches about everyone nearby to the view, and does what the
// room says (start where you left off, snap back after a bad move).
//
// Movement is predicted: your character moves the moment you press a key, and
// the server only ever corrects it. If the server can't be reached the town
// still works, on your own, and the client keeps trying to rejoin.

import { Client, type Room } from '@colyseus/sdk';
import type { Character } from './characters';
import {
  ANIM_DRIVE,
  ANIM_IDLE,
  ANIM_JUMP,
  ANIM_RIDE,
  ANIM_RUN,
  ANIM_WALK,
  CITY_ROOM,
  CLOSE_KICKED,
  CLOSE_SIGNED_IN_ELSEWHERE,
  MSG_CORRECT,
  MSG_DOTS,
  MSG_LOOK,
  MSG_MOVE,
  MSG_SPAWN,
  MSG_TELEPORT,
  type CorrectMessage,
  type DotsMessage,
  type MoveMessage,
  type SpawnMessage,
} from './protocol';
import type { RemoteState } from '../render/iso/remotes';

/** The local player as the network sees them. Ground is (x, z); y is height. */
export interface LocalState {
  x: number;
  y: number;
  z: number;
  yaw: number;
  /** Ground speed, tiles per second. */
  speed: number;
  car: number;
  /** A passenger: the driver's session id; otherwise ''. */
  ride: string;
}

/** What the game view does for the network (IsoView). */
export interface NetHost {
  localState(): LocalState | null;
  /** Joined: start where they left off in this town (or at its spawn point), looking like this. */
  applySpawn(at: SpawnMessage['at'], look: string | null): void;
  /** The server says they're here (a correction, or a minimap jump it accepted). */
  placePlayer(x: number, z: number): void;
  syncRemotes(serverTime: number, players: RemoteState[], me: string): void;
  clearRemotes(): void;
  /** Everyone else in the room, for the minimap. */
  setDots(dots: { x: number; y: number }[]): void;
  /** A line about the connection (null clears it). */
  notify(text: string | null): void;
}

/** How this tab is doing online, for the sidebar. */
export type NetStatus = { state: 'joining' } | { state: 'online'; here: number } | { state: 'offline'; why: string };

export interface Town {
  /** Identifies the town: its seed and params. */
  key: string;
  width: number;
  height: number;
}

const SEND_MS = 50;
/** Rejoin delays after losing the room (ms), longest last and then repeated. */
const RETRY_MS = [2_000, 5_000, 10_000, 30_000];

/** Walk / run / jump, for the anim byte (others' clients mostly work it out from the motion). */
function animOf(s: LocalState): number {
  if (s.ride) return ANIM_RIDE;
  if (s.car) return ANIM_DRIVE;
  if (s.y > 0) return ANIM_JUMP;
  if (s.speed > 2.5) return ANIM_RUN;
  return s.speed > 0.05 ? ANIM_WALK : ANIM_IDLE;
}

export class Multiplayer {
  private readonly client = new Client(`${location.origin}/mp`);
  private room: Room | null = null;
  private town: Town | null = null;
  private seq = 0;
  private epoch = 0;
  /** Spawn applied: until then nothing is sent (the server takes the first move as where they are). */
  private spawned = false;
  private lastSent = '';
  private timer: ReturnType<typeof setInterval> | null = null;
  private retry: ReturnType<typeof setTimeout> | null = null;
  private retries = 0;
  /** Signed out by another session, or kicked: don't rejoin by ourselves. */
  private stopped = false;
  /** Bumped on every join and leave, so a slow join can tell it's been overtaken. */
  private generation = 0;
  private readonly host: NetHost;
  readonly character: Character;
  /** Told whenever the status (or how many are here) changes. */
  onStatus: ((status: NetStatus) => void) | null = null;
  private status: NetStatus = { state: 'joining' };
  private readonly onVisible = () => {
    if (document.visibilityState === 'visible') this.send(true);
  };

  constructor(host: NetHost, character: Character) {
    this.host = host;
    this.character = character;
    document.addEventListener('visibilitychange', this.onVisible);
  }

  /** Play this town (leaving any other). */
  enter(town: Town): void {
    if (this.town?.key === town.key && (this.room || this.retry)) return;
    this.town = town;
    // Signed out by another session: playing this character again here takes
    // a deliberate pick (switching character), not just a new town.
    if (this.stopped) return;
    this.retries = 0;
    void this.join();
  }

  /** Leave the room for good (switching character, or closing). */
  async close(): Promise<void> {
    document.removeEventListener('visibilitychange', this.onVisible);
    this.town = null;
    this.stopped = true;
    await this.disconnect();
  }

  /** What the client thinks is going on (the dev console's `porchlight.net()`). */
  debug(): object {
    const room = this.room;
    const players: object[] = [];
    (room?.state as { players?: Map<string, RemoteState> } | undefined)?.players?.forEach((p, key) =>
      players.push({ key, me: key === room!.sessionId, name: p.name, x: +p.x.toFixed(1), z: +p.z.toFixed(1), anim: p.anim }),
    );
    return { character: this.character.name, town: this.town?.key, room: room?.roomId ?? null, session: room?.sessionId ?? null, spawned: this.spawned, stopped: this.stopped, status: this.status, sentTo: players };
  }

  private setStatus(status: NetStatus): void {
    this.status = status;
    this.onStatus?.(status);
  }

  /** A minimap jump: ask the server, which answers with where to go. False when offline (just go). */
  teleport(x: number, z: number): boolean {
    if (!this.room || !this.spawned) return false;
    this.room.send(MSG_TELEPORT, { x, z });
    return true;
  }

  /** A new look for the character (it's saved with them). */
  lookChanged(model: string): void {
    this.room?.send(MSG_LOOK, { model });
  }

  private async disconnect(): Promise<void> {
    this.generation++;
    if (this.retry) clearTimeout(this.retry);
    this.retry = null;
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    const room = this.room;
    this.room = null;
    this.spawned = false;
    this.host.clearRemotes();
    this.host.setDots([]);
    if (room) await room.leave(true).catch(() => {});
  }

  private async join(): Promise<void> {
    await this.disconnect();
    const town = this.town;
    if (!town) return;
    const generation = this.generation;
    const options = { town: town.key, width: town.width, height: town.height, character: this.character.id };
    this.setStatus({ state: 'joining' });
    let room: Room;
    try {
      // The room can fill up between matchmaking and joining: try once more.
      room = await this.client.joinOrCreate(CITY_ROOM, options).catch(() => this.client.joinOrCreate(CITY_ROOM, options));
    } catch (err) {
      if (generation !== this.generation) return;
      console.warn('multiplayer: could not join:', err);
      const why = (err as Error).message || "the game server isn't answering";
      this.host.notify(`Playing alone: ${why}. Trying again…`);
      this.setStatus({ state: 'offline', why });
      this.scheduleRejoin();
      return;
    }
    if (generation !== this.generation) {
      void room.leave(true).catch(() => {});
      return;
    }
    this.room = room;
    this.seq = 0;
    this.epoch = 0;
    this.lastSent = '';
    this.retries = 0;
    this.host.notify(null);
    this.setStatus({ state: 'online', here: 1 });

    room.onMessage(MSG_SPAWN, (m: SpawnMessage) => {
      this.host.applySpawn(m.at, m.look);
      this.spawned = true;
      this.send(true);
    });
    room.onMessage(MSG_CORRECT, (c: CorrectMessage) => {
      this.epoch = c.epoch;
      this.host.placePlayer(c.x, c.z);
      this.send(true);
    });
    room.onMessage(MSG_DOTS, (dots: DotsMessage) => {
      const here = Math.max(1, dots.length);
      if (this.status.state !== 'online' || this.status.here !== here) this.setStatus({ state: 'online', here });
      this.host.setDots(dots.filter(([key]) => key !== room.sessionId).map(([, x, z]) => ({ x, y: z })));
    });
    room.onStateChange((state: { serverTime?: number; players?: Map<string, Omit<RemoteState, 'key'>> }) => {
      if (typeof state.serverTime !== 'number') return; // not stamped yet: wait for the next patch
      const players: RemoteState[] = [];
      state.players?.forEach((p, key) => {
        if (key !== room.sessionId) players.push({ ...pick(p), key });
      });
      this.host.syncRemotes(state.serverTime, players, room.sessionId);
    });
    room.onDrop(() => this.host.notify('Connection lost. Reconnecting…'));
    room.onReconnect(() => {
      this.host.notify(null);
      this.send(true);
    });
    room.onLeave((code: number) => {
      if (this.room !== room) return; // we left it ourselves
      this.room = null;
      this.spawned = false;
      if (this.timer) clearInterval(this.timer);
      this.timer = null;
      this.host.clearRemotes();
      this.host.setDots([]);
      if (code === CLOSE_SIGNED_IN_ELSEWHERE) {
        this.stopped = true;
        this.setStatus({ state: 'offline', why: 'playing somewhere else' });
        this.host.notify(`${this.character.name} is being played somewhere else now. Playing alone here.`);
      } else if (code === CLOSE_KICKED) {
        this.stopped = true;
        this.setStatus({ state: 'offline', why: 'sent out by the server' });
        this.host.notify('The server sent you out of the town. Playing alone here.');
      } else {
        this.host.notify('Lost the game server. Playing alone, trying to rejoin…');
        this.setStatus({ state: 'offline', why: 'lost the server' });
        this.scheduleRejoin();
      }
    });
    this.timer = setInterval(() => this.send(false), SEND_MS);
  }

  private scheduleRejoin(): void {
    if (this.stopped || !this.town || this.retry) return;
    const delay = RETRY_MS[Math.min(this.retries++, RETRY_MS.length - 1)];
    this.retry = setTimeout(() => {
      this.retry = null;
      void this.join();
    }, delay);
  }

  /** Send where the character is, if it's changed (or `force`). */
  private send(force: boolean): void {
    const room = this.room;
    if (!room || !this.spawned) return;
    const s = this.host.localState();
    if (!s) return;
    const anim = animOf(s);
    const key = `${s.x.toFixed(3)},${s.y.toFixed(3)},${s.z.toFixed(3)},${s.yaw.toFixed(2)},${anim},${s.car},${s.ride}`;
    if (!force && key === this.lastSent) return;
    this.lastSent = key;
    this.seq = (this.seq + 1) & 0xffff;
    const move: MoveMessage = { seq: this.seq, epoch: this.epoch, x: s.x, y: s.y, z: s.z, yaw: s.yaw, anim, car: s.car, ride: s.ride };
    room.send(MSG_MOVE, move);
  }
}

/** The synced fields, copied off the live schema object. */
function pick(p: Omit<RemoteState, 'key'>): Omit<RemoteState, 'key'> {
  return { name: p.name, x: p.x, y: p.y, z: p.z, rotY: p.rotY, anim: p.anim, car: p.car, ride: p.ride ?? '', appearance: p.appearance, away: p.away };
}

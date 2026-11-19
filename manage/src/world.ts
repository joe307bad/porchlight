// Everything on the map right now: each open room's players, cars and
// townsfolk, in the county's frame (tiles). Frames from the feed set where
// each thing is headed; step() moves what's drawn toward it every animation
// frame, carrying cars and people on at their speed between frames as the game
// does (WorldSync.cs), so 10 updates a second draw smoothly at 60. All in typed
// arrays, reused while a room's counts don't change: nothing per car is
// allocated per frame.
//
// Who's near whom (players and the cars and people round them, and each
// other) is worked out once per feed frame, not per drawn frame.

import { ANIM, KEPT, NO_WORLD, PLAYER_BYTES, RECORD, readPlayer, type PositionsFrame, type RoomMeta } from './protocol.ts';

/** How quickly the drawn position closes on the sent one (per second), and further off than this (tiles) it jumps. */
const EASE = 8;
const JUMP = 3;
const PLAYER_EASE = 12;
const PLAYER_JUMP = 24;
/** A world older than this (ms) has stopped: its cars and people stand where they were. */
/** Without word of a town's traffic for this long, it stops (towns not looked at closely come once a second). */
const STALE_MS = 2500;
/** Tiles round a player that count as near them. */
export const REACH = 12;
/** Tiles a second under which a car counts as stopped. */
export const STOPPED = 0.25;

/** A room's cars, or its townsfolk. */
export class Layer {
  n = 0;
  /** Where the host has them (county frame), heading (radians) and speed (tiles a second). */
  tx = new Float32Array(0);
  ty = new Float32Array(0);
  th = new Float32Array(0);
  ts = new Float32Array(0);
  /** Where they're drawn. */
  x = new Float32Array(0);
  y = new Float32Array(0);
  h = new Float32Array(0);
  /** Within REACH of a player. */
  near = new Uint8Array(0);
  /** Cars face (cos h, sin h); people (sin h, cos h). */
  readonly people: boolean;
  constructor(people: boolean) {
    this.people = people;
  }

  /** The host's latest, from the frame's bytes. */
  read(v: DataView, at: number, n: number, ox: number, oy: number): void {
    if (n !== this.n) {
      const had = this.n;
      const fresh = had === 0;
      this.n = n;
      const grow = (a: Float32Array) => {
        const b = new Float32Array(n);
        b.set(a.subarray(0, Math.min(a.length, n)));
        return b;
      };
      this.tx = new Float32Array(n);
      this.ty = new Float32Array(n);
      this.th = new Float32Array(n);
      this.ts = new Float32Array(n);
      this.x = fresh ? new Float32Array(n) : grow(this.x);
      this.y = fresh ? new Float32Array(n) : grow(this.y);
      this.h = fresh ? new Float32Array(n) : grow(this.h);
      this.near = new Uint8Array(n);
      // New ones start where they are.
      if (n > had) this.x.fill(NaN, had);
    }
    const TURN = (2 * Math.PI) / 65536;
    for (let i = 0, o = at; i < n; i++, o += RECORD) {
      this.tx[i] = ox + v.getUint16(o, false) / 100;
      this.ty[i] = oy + v.getUint16(o + 2, false) / 100;
      this.th[i] = v.getUint16(o + 4, false) * TURN;
      this.ts[i] = v.getUint8(o + 6) / 40;
    }
  }

  step(dt: number, going: boolean): void {
    const k = Math.min(1, 1 - Math.exp(-dt * EASE));
    const { tx, ty, th, ts, x, y, h } = this;
    for (let i = 0; i < this.n; i++) {
      if (going && ts[i] > 0) {
        const d = ts[i] * dt;
        if (this.people) {
          tx[i] += Math.sin(th[i]) * d;
          ty[i] += Math.cos(th[i]) * d;
        } else {
          tx[i] += Math.cos(th[i]) * d;
          ty[i] += Math.sin(th[i]) * d;
        }
      }
      const dx = tx[i] - x[i];
      const dy = ty[i] - y[i];
      if (!(dx * dx + dy * dy <= JUMP * JUMP)) {
        // Too far off, or new (NaN): straight there.
        x[i] = tx[i];
        y[i] = ty[i];
        h[i] = th[i];
        continue;
      }
      x[i] += dx * k;
      y[i] += dy * k;
      let turn = th[i] - h[i];
      turn -= Math.round(turn / (2 * Math.PI)) * 2 * Math.PI;
      h[i] += turn * k;
    }
  }

  clear(): void {
    this.n = 0;
  }
}

export interface PlayerView {
  sid: string;
  id: string;
  name: string;
  look: string;
  roomId: number;
  town: string;
  /** Where they're headed (county frame), and where they're drawn. */
  tx: number;
  ty: number;
  x: number;
  y: number;
  /** Height off the ground (a jump, a plane). */
  z: number;
  /** Heading, radians (0 faces +y, the map's south). */
  yaw: number;
  anim: number;
  car: number;
  /** A passenger: the driver; otherwise null. */
  ride: PlayerView | null;
  host: boolean;
  /** Cars and people within REACH, and other players. */
  nearCars: number;
  nearPeople: number;
  nearPlayers: PlayerView[];
  color: string;
  seen: boolean;
}

export interface RoomView {
  meta: RoomMeta;
  /** Its world's age at the last frame (ms; NO_WORLD: none), and when that frame came (performance.now()). */
  age: number;
  receivedAt: number;
  cars: Layer;
  people: Layer;
  players: PlayerView[];
}

/** A colour of its own for each character, the same every visit. */
export function colorOf(id: string): string {
  let h = 2166136261;
  for (let i = 0; i < id.length; i++) h = Math.imul(h ^ id.charCodeAt(i), 16777619);
  return `hsl(${(h >>> 0) % 360} 85% 62%)`;
}

export function doing(p: PlayerView): string {
  if (p.ride) return `Riding with ${p.ride.name}`;
  if (p.car > 0 || p.anim === ANIM.drive) return 'Driving';
  if (p.z > 3) return 'Flying';
  switch (p.anim) {
    case ANIM.walk: return 'Walking';
    case ANIM.run: return 'Running';
    case ANIM.jump: return 'Jumping';
    default: return 'Standing';
  }
}

export interface Totals {
  towns: number;
  players: number;
  cars: number;
  people: number;
  moving: number;
  stopped: number;
  /** Mean speed of the moving cars (tiles a second). */
  speed: number;
}

export class World {
  readonly rooms = new Map<number, RoomView>();
  /** The rooms in the order the server lists them. */
  order: RoomView[] = [];
  /** Bumped whenever who's where changes enough for the panels to show it again (each frame from the feed). */
  version = 0;
  serverTime = 0;
  private stepped = 0;

  applyRooms(metas: RoomMeta[]): void {
    const keep = new Set<number>();
    const order: RoomView[] = [];
    for (const meta of metas) {
      keep.add(meta.id);
      let r = this.rooms.get(meta.id);
      if (!r) {
        r = { meta, age: NO_WORLD, receivedAt: 0, cars: new Layer(false), people: new Layer(true), players: [] };
        this.rooms.set(meta.id, r);
      }
      r.meta = meta;
      const was = new Map(r.players.map((p) => [p.sid, p]));
      r.players = meta.players.map((m, i) => {
        const p = was.get(m.sid);
        const v: PlayerView = p ?? {
          sid: m.sid, id: m.id, name: m.name, look: m.look, roomId: meta.id, town: meta.name,
          tx: NaN, ty: NaN, x: NaN, y: NaN, z: 0, yaw: 0, anim: 0, car: 0, ride: null, host: false,
          nearCars: 0, nearPeople: 0, nearPlayers: [], color: colorOf(m.id), seen: false,
        };
        v.name = m.name;
        v.look = m.look;
        v.town = meta.name;
        v.host = i === meta.host;
        return v;
      });
      order.push(r);
    }
    for (const id of [...this.rooms.keys()]) if (!keep.has(id)) this.rooms.delete(id);
    this.order = order;
    this.version++;
  }

  applyPositions(f: PositionsFrame, now: number): void {
    const v = f.bytes;
    this.serverTime = f.time;
    for (const rp of f.rooms) {
      const r = this.rooms.get(rp.id);
      if (!r) continue;
      const { ox, oy } = r.meta;
      const kept = rp.age === KEPT;
      if (!kept) {
        r.age = rp.age;
        r.receivedAt = now;
      }
      for (let i = 0; i < rp.players && i < r.players.length; i++) {
        const p = r.players[i];
        const d = readPlayer(v, rp.playersAt + i * PLAYER_BYTES);
        p.tx = ox + d.x;
        p.ty = oy + d.z;
        p.z = d.y;
        p.yaw = d.yaw;
        p.anim = d.anim;
        p.car = d.car;
        p.ride = d.ride >= 0 && d.ride < r.players.length && d.ride !== i ? r.players[d.ride] : null;
        p.seen = true;
      }
      if (kept) {
        // Its traffic carries on as it was.
      } else if (rp.age === NO_WORLD) {
        r.cars.clear();
        r.people.clear();
      } else {
        r.cars.read(v, rp.carsAt, rp.cars, ox, oy);
        r.people.read(v, rp.peopleAt, rp.people, ox, oy);
      }
    }
    this.neighbours();
    this.version++;
  }

  /** Move everything drawn on to now. */
  step(now: number): void {
    const dt = this.stepped ? Math.min(0.25, (now - this.stepped) / 1000) : 0;
    this.stepped = now;
    if (dt <= 0) return;
    const kp = Math.min(1, 1 - Math.exp(-dt * PLAYER_EASE));
    for (const r of this.order) {
      const going = r.age !== NO_WORLD && r.age + (now - r.receivedAt) < STALE_MS;
      r.cars.step(dt, going);
      r.people.step(dt, going);
      for (const p of r.players) {
        if (!p.seen) continue;
        const dx = p.tx - p.x;
        const dy = p.ty - p.y;
        if (!(dx * dx + dy * dy <= PLAYER_JUMP * PLAYER_JUMP)) {
          p.x = p.tx;
          p.y = p.ty;
        } else {
          p.x += dx * kp;
          p.y += dy * kp;
        }
      }
    }
  }

  /** Who's near whom, as of the latest frame. */
  private neighbours(): void {
    const R2 = REACH * REACH;
    const all: PlayerView[] = [];
    for (const r of this.order) {
      r.cars.near.fill(0, 0, r.cars.n);
      r.people.near.fill(0, 0, r.people.n);
      for (const p of r.players) {
        if (!p.seen) continue;
        all.push(p);
        p.nearCars = count(r.cars, p, R2);
        p.nearPeople = count(r.people, p, R2);
      }
    }
    for (const p of all) p.nearPlayers = [];
    for (let i = 0; i < all.length; i++) {
      for (let j = i + 1; j < all.length; j++) {
        const a = all[i];
        const b = all[j];
        const dx = a.tx - b.tx;
        const dy = a.ty - b.ty;
        if (dx * dx + dy * dy <= R2) {
          a.nearPlayers.push(b);
          b.nearPlayers.push(a);
        }
      }
    }
  }

  totals(): Totals {
    const t: Totals = { towns: 0, players: 0, cars: 0, people: 0, moving: 0, stopped: 0, speed: 0 };
    let sum = 0;
    for (const r of this.order) {
      if (r.players.length || r.cars.n || r.people.n) t.towns++;
      t.players += r.players.length;
      t.cars += r.cars.n;
      t.people += r.people.n;
      for (let i = 0; i < r.cars.n; i++) {
        const s = r.cars.ts[i];
        if (s < STOPPED) t.stopped++;
        else {
          t.moving++;
          sum += s;
        }
      }
    }
    t.speed = t.moving ? sum / t.moving : 0;
    return t;
  }

  players(): PlayerView[] {
    const out: PlayerView[] = [];
    for (const r of this.order) for (const p of r.players) out.push(p);
    return out;
  }

  find(sid: string): PlayerView | null {
    for (const r of this.order) for (const p of r.players) if (p.sid === sid) return p;
    return null;
  }
}

/** How many of the layer are within reach of the player (and mark them so). */
function count(l: Layer, p: PlayerView, r2: number): number {
  let n = 0;
  const { tx, ty, near } = l;
  for (let i = 0; i < l.n; i++) {
    const dx = tx[i] - p.tx;
    const dy = ty[i] - p.ty;
    if (dx * dx + dy * dy <= r2) {
      near[i] = 1;
      n++;
    }
  }
  return n;
}

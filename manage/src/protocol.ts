// The server's traffic feed (server/src/multiplayer/feed.ts), read: frames
// split out of the stream as its chunks come, and each one decoded. Pure.

export const FRAME_ROOMS = 1;
export const FRAME_POSITIONS = 2;
/** A room's world age that means it has none (or none lately). */
export const NO_WORLD = 0xffff;
/** A room's cars and townsfolk as last sent: none in this frame. */
export const KEPT = 0xfffe;
/** Bytes per car or townsperson (WorldSync.cs), per player (feed.ts). */
export const RECORD = 7;
/** A record's x and y both this: an empty slot, nobody out in it now (WorldSync.HIDDEN). */
export const HIDDEN = 0xffff;
export const PLAYER_BYTES = 18;

export interface RoomMeta {
  id: number;
  key: string;
  name: string;
  /** Where the room's positions are measured from, on the county map (tiles). */
  ox: number;
  oy: number;
  /** The index of the player who runs the town's traffic, or -1. */
  host: number;
  players: { sid: string; id: string; name: string; look: string }[];
}

export interface RoomsFrame {
  type: typeof FRAME_ROOMS;
  /** This stream's name, for telling the server which towns are looked at closely (api.ts focus). */
  feed?: string;
  rooms: RoomMeta[];
}

/** One room's part of a positions frame: views into the frame's bytes, decoded as they're used. */
export interface RoomPositions {
  id: number;
  /** How old its world is (ms), or NO_WORLD. */
  age: number;
  players: number;
  /** Where its players start in `bytes`. */
  playersAt: number;
  cars: number;
  carsAt: number;
  people: number;
  peopleAt: number;
}

export interface PositionsFrame {
  type: typeof FRAME_POSITIONS;
  /** The server's clock (ms). */
  time: number;
  rooms: RoomPositions[];
  bytes: DataView;
}

export type Frame = RoomsFrame | PositionsFrame;

/** Splits the stream into frames as its chunks come (a frame can span chunks, and a chunk hold many). */
export class FrameReader {
  private buf = new Uint8Array(64 * 1024);
  private start = 0;
  private end = 0;
  private readonly text = new TextDecoder();

  /** Everything this chunk completes. */
  push(chunk: Uint8Array): Frame[] {
    this.append(chunk);
    const out: Frame[] = [];
    for (;;) {
      const have = this.end - this.start;
      if (have < 4) break;
      const b = this.buf;
      const s = this.start;
      const len = (b[s] | (b[s + 1] << 8) | (b[s + 2] << 16) | (b[s + 3] << 24)) >>> 0;
      if (len < 1 || len > 64 * 1024 * 1024) throw new Error(`a frame of ${len} bytes`);
      if (have < 4 + len) break;
      const type = b[s + 4];
      // Its own copy: the buffer is reused.
      const body = b.slice(s + 5, s + 4 + len);
      this.start += 4 + len;
      const f = this.decode(type, body);
      if (f) out.push(f);
    }
    return out;
  }

  private append(chunk: Uint8Array): void {
    if (this.start === this.end) this.start = this.end = 0;
    if (this.end + chunk.length > this.buf.length) {
      // Out of room at the end: what's left moves to the front, into a bigger buffer if need be.
      const have = this.end - this.start;
      if (have + chunk.length > this.buf.length) {
        const next = new Uint8Array((have + chunk.length) * 2);
        next.set(this.buf.subarray(this.start, this.end));
        this.buf = next;
      } else this.buf.copyWithin(0, this.start, this.end);
      this.start = 0;
      this.end = have;
    }
    this.buf.set(chunk, this.end);
    this.end += chunk.length;
  }

  private decode(type: number, body: Uint8Array): Frame | null {
    if (type === FRAME_ROOMS) {
      const json = JSON.parse(this.text.decode(body)) as { feed?: string; rooms: RoomMeta[] };
      return { type: FRAME_ROOMS, feed: json.feed, rooms: json.rooms };
    }
    if (type === FRAME_POSITIONS) return decodePositions(body);
    return null; // a kind this build doesn't know: skipped
  }
}

export function decodePositions(body: Uint8Array): PositionsFrame {
  const v = new DataView(body.buffer, body.byteOffset, body.byteLength);
  const time = v.getFloat64(0, true);
  const n = v.getUint16(8, true);
  let o = 10;
  const rooms: RoomPositions[] = [];
  for (let i = 0; i < n; i++) {
    const id = v.getUint32(o, true);
    const age = v.getUint16(o + 4, true);
    const players = v.getUint16(o + 6, true);
    o += 8;
    const playersAt = o;
    o += players * PLAYER_BYTES;
    if (age === KEPT) {
      // Its traffic as last sent (a town not looked at closely, between its once-a-second updates).
      rooms.push({ id, age, players, playersAt, cars: 0, carsAt: o, people: 0, peopleAt: o });
      continue;
    }
    const cars = v.getUint16(o, true);
    o += 2;
    const carsAt = o;
    o += cars * RECORD;
    const people = v.getUint16(o, true);
    o += 2;
    const peopleAt = o;
    o += people * RECORD;
    rooms.push({ id, age, players, playersAt, cars, carsAt, people, peopleAt });
  }
  if (o !== body.byteLength) throw new Error(`a positions frame of ${body.byteLength} bytes said ${o}`);
  return { type: FRAME_POSITIONS, time, rooms, bytes: v };
}

/** A car's or townsperson's record (WorldSync.cs): position (tiles, the room's frame), heading (radians) and speed (tiles a second). */
export function readRecord(v: DataView, at: number): { x: number; y: number; heading: number; speed: number } | null {
  if (v.getUint16(at, false) === HIDDEN && v.getUint16(at + 2, false) === HIDDEN) return null;
  return {
    x: v.getUint16(at, false) / 100,
    y: v.getUint16(at + 2, false) / 100,
    heading: (v.getUint16(at + 4, false) / 65536) * 2 * Math.PI,
    speed: v.getUint8(at + 6) / 40,
  };
}

export interface PlayerRecord {
  x: number;
  z: number;
  y: number;
  /** Heading, radians (0 faces +z). */
  yaw: number;
  anim: number;
  /** At the wheel: the car model + 1; on foot 0. */
  car: number;
  /** A passenger: the driver's index in the room's players; otherwise -1. */
  ride: number;
}

export function readPlayer(v: DataView, at: number): PlayerRecord {
  return {
    x: v.getFloat32(at, true),
    z: v.getFloat32(at + 4, true),
    y: v.getFloat32(at + 8, true),
    yaw: (v.getUint16(at + 12, true) / 65536) * 2 * Math.PI,
    anim: v.getUint8(at + 14),
    car: v.getUint8(at + 15),
    ride: v.getInt16(at + 16, true),
  };
}

/** Player.anim (server/src/multiplayer/protocol.ts). */
export const ANIM = { idle: 0, walk: 1, run: 2, jump: 3, drive: 4, ride: 5 } as const;

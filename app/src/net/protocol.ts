// What the game client and the city room say to each other: a copy of
// server/src/multiplayer/protocol.ts. Change both together.
//
// Units are the town's: 1 unit = 1 tile (about 4 m). The ground is x (east)
// and z (south: the map's y); y is height off the ground (mid-jump).

/** Room name for matchmaking. */
export const CITY_ROOM = 'city';

/** Client → server. */
export const MSG_MOVE = 'move'; // MoveMessage, about 30 a second while moving (see SEND_MS)
export const MSG_TELEPORT = 'teleport'; // { x, z }: a minimap jump; answered with `correct`
export const MSG_LOOK = 'look'; // { model }: a new character

/** Server → client. */
export const MSG_SPAWN = 'spawn'; // SpawnMessage, once after joining
export const MSG_CORRECT = 'correct'; // CorrectMessage: snap here
export const MSG_DOTS = 'dots'; // DotsMessage, once a second: everyone in the room, for the minimap

/** Close codes (4000 is a consented leave; 4001-4010 are Colyseus's own). */
export const CLOSE_SIGNED_IN_ELSEWHERE = 4100;
export const CLOSE_KICKED = 4101;

/** Player.anim. */
export const ANIM_IDLE = 0;
export const ANIM_WALK = 1;
export const ANIM_RUN = 2;
export const ANIM_JUMP = 3;
export const ANIM_DRIVE = 4;
/** A passenger in someone else's car. */
export const ANIM_RIDE = 5;
export const ANIM_MAX = 5;

/** Seats in a car: the driver and three passengers. */
export const CAR_SEATS = 4;

/** How often clients send moves, and the room sends patches (ms): about 30 a second. */
export const SEND_MS = 33;

export interface MoveMessage {
  /** Increments every send (wraps at 65536). */
  seq: number;
  /**
   * When the sender sampled this position, on its own clock (ms). Others play
   * each player back on that player's clock, so the spacing of their moves
   * stays even however the network and the room's ticks bunch them up.
   */
  t: number;
  /** The `epoch` of the last `correct` the client applied. */
  epoch: number;
  x: number;
  y: number;
  z: number;
  /** Heading, radians (0 faces +z, the way the town's characters face). */
  yaw: number;
  anim: number;
  /** At the wheel: the car model's index + 1; on foot 0. */
  car: number;
  /** A passenger: the driver's session id; otherwise ''. */
  ride: string;
}

export interface SpawnMessage {
  /** Where they were last time in this town, or null: use the town's spawn point. */
  at: { x: number; z: number; yaw: number } | null;
  /** Their saved character, if any. */
  look: string | null;
}

export interface CorrectMessage {
  x: number;
  y: number;
  z: number;
  /** Moves sent before this correction carry an older epoch and are ignored. */
  epoch: number;
}

/** [sessionId, x, z] per player, positions rounded to a tenth of a tile. */
export type DotsMessage = [string, number, number][];

/** Heading in radians ↔ the 0-255 byte on the wire (about 1.4° a step). */
export const quantizeYaw = (yaw: number): number => Math.round((yaw / (2 * Math.PI)) * 256) & 255;
export const yawFromByte = (b: number): number => (b / 256) * 2 * Math.PI;

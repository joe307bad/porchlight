// What the game client and the city room say to each other. The game keeps its
// own copy of these in unity/Assets/Porchlight/Runtime/Shared/Net/Protocol.cs: change both together.
//
// Units are the town's: 1 unit = 1 tile (about 4 m). The ground is x (east)
// and z (south: the map's y); y is height off the ground (mid-jump).

/** Room name for matchmaking. */
export const CITY_ROOM = 'city';

/** Client → server. */
export const MSG_MOVE = 'move'; // MoveMessage, about 30 a second while moving (see SEND_MS)
export const MSG_TELEPORT = 'teleport'; // { x, z }: a minimap jump; answered with `correct`
export const MSG_LOOK = 'look'; // { model }: a new character
export const MSG_NIGHT = 'night'; // { on }: switch the town to night or day, for everyone
export const MSG_TAKE = 'take'; // { car }: "p3" or "t12", a parked or traffic car got into
export const MSG_CAR = 'car'; // ParkedCarMessage: where their own car is parked (or that it's gone)
export const MSG_WORLD = 'world'; // the host's traffic and townsfolk (WorldMessage), about 10 a second

/** Server → client. */
export const MSG_SPAWN = 'spawn'; // SpawnMessage, once after joining
export const MSG_CORRECT = 'correct'; // CorrectMessage: snap here
export const MSG_DOTS = 'dots'; // DotsMessage, once a second: everyone in the room, for the minimap
// MSG_WORLD, passed on from the host to everyone else as it comes.

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

/** [sessionId, x, z, name] per player, positions rounded to a tenth of a tile. */
export type DotsMessage = [string, number, number, string][];

/** Their own car, left parked: the model + 1 (0: no car, or they're driving it), where, and its heading in radians. */
export interface ParkedCarMessage {
  model: number;
  x: number;
  z: number;
  yaw: number;
}

/**
 * The host's town, as the clients encode it: the traffic (`cars`) and the
 * townsfolk (`people`), packed into bytes, and the looks of townsfolk added
 * since the town was built. The room only checks its size and passes it on.
 */
export interface WorldMessage {
  cars: Uint8Array;
  people: Uint8Array;
  looks: string[];
}

/** Most bytes a world message may carry. */
export const WORLD_MAX_BYTES = 16_384;
/** The host's world messages, at most this many a second. */
export const WORLD_PER_SECOND = 12;
/** No world message from the host for this long: someone else takes over. */
export const HOST_SILENT_MS = 3_000;

/**
 * Heading in radians ↔ the 0-65535 on the wire. (A byte's 1.4° steps were
 * enough to see from a passenger's seat: the camera behind a car swung with
 * each step.)
 */
export const quantizeYaw = (yaw: number): number => Math.round((yaw / (2 * Math.PI)) * 65536) & 0xffff;
export const yawFromWire = (q: number): number => (q / 65536) * 2 * Math.PI;

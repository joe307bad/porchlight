// The synced state: what every client gets (in delta patches, about 30 a second).
// Only players in a client's StateView reach it (interest.ts decides who);
// anything server-only lives on the room's PlayerRuntime instead.
//
// The schema() form rather than decorators, since Node runs this file by
// stripping its types and decorators aren't erasable.

import { schema } from '@colyseus/schema';

export const Player = schema(
  {
    /** The character's id (not the session id, which keys the map). */
    id: 'string',
    /** The character's name. */
    name: 'string',
    x: 'float32',
    /** Height off the ground (mid-jump). */
    y: 'float32',
    z: 'float32',
    /** Heading, 0-65535 for a full turn (quantizeYaw). */
    rotY: 'uint16',
    /** ANIM_* in protocol.ts. */
    anim: 'uint8',
    /** At the wheel: the car model + 1; on foot 0. */
    car: 'uint8',
    /** A passenger: the driver's session id; otherwise ''. */
    ride: 'string',
    /** Which character model they look like. */
    appearance: 'string',
    /** Dropped and maybe coming back (the reconnection window). */
    away: 'boolean',
    /** When their client sampled this position, on its own clock (MoveMessage.t). */
    t: 'uint32',
  },
  'Player',
);
export type Player = InstanceType<typeof Player>;

/** A player's own car, parked where they left it (everyone sees every parked car, near or far). */
export const ParkedCar = schema(
  {
    /** The car model + 1. */
    model: 'uint8',
    x: 'float32',
    z: 'float32',
    /** Heading, 0-65535 for a full turn (quantizeYaw). */
    yaw: 'uint16',
  },
  'ParkedCar',
);
export type ParkedCar = InstanceType<typeof ParkedCar>;

export const CityState = schema(
  {
    players: { map: Player, view: true },
    /** Each player's parked car, by session id (absent while they drive it, or have none). */
    cars: { map: ParkedCar },
    /**
     * The session that runs the town's traffic and townsfolk and sends them to
     * everyone else (MSG_WORLD), so all see the same cars and people.
     */
    host: 'string',
    /** Night in this town: anyone can switch it, and it's the same for everyone. */
    night: 'boolean',
    /** The town's cars someone has taken: "p3" (parked car 3), "t12" (traffic car 12), comma separated. */
    taken: 'string',
  },
  'CityState',
);
export type CityState = InstanceType<typeof CityState>;

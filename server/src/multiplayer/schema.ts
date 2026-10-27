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

export const CityState = schema(
  {
    players: { map: Player, view: true },
  },
  'CityState',
);
export type CityState = InstanceType<typeof CityState>;

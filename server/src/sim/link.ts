// The simulation (pool.ts) and the game's rooms, joined up: which towns it runs
// (every town in a county: those that come with the game and each account's placed
// ones, looked at again whenever a county changes and every minute), where the
// players in each are (ten times a second, for the traffic to stop for), which cars
// they've taken, and each tick passed on to the rooms of its town (CityRoom.simTick).

import type { GameDeps } from '../multiplayer/accounts.ts';
import type { CityRoom } from '../multiplayer/CityRoom.ts';
import { STARTER_TOWNS } from '../multiplayer/persistence.ts';
import type { SimPlayer, SimPool, SimTown } from './pool.ts';

const PLAYERS_MS = 100;
const TOWNS_MS = 60_000;
/** A town the simulation hasn't moved on for this long isn't being run (its worker's down): its players run it. */
export const SIM_STALE_MS = 1_500;

export class SimLink {
  private timers: ReturnType<typeof setInterval>[] = [];
  private refreshing = false;

  readonly pool: SimPool;
  private readonly deps: GameDeps;
  private readonly rooms: () => Iterable<CityRoom>;

  constructor(pool: SimPool, deps: GameDeps, rooms: () => Iterable<CityRoom>) {
    this.pool = pool;
    this.deps = deps;
    this.rooms = rooms;
    pool.onTick = (towns) => this.ticked(towns);
  }

  start(): void {
    this.pool.start();
    // The towns that come with the game straight away; the accounts' once the database answers.
    this.pool.setTowns(STARTER_TOWNS.map((t) => t.key));
    void this.refreshTowns();
    this.timers.push(setInterval(() => void this.refreshTowns(), TOWNS_MS));
    this.timers.push(setInterval(() => this.sendPlayers(), PLAYERS_MS));
    for (const t of this.timers) t.unref();
  }

  close(): void {
    for (const t of this.timers) clearInterval(t);
    this.timers = [];
    this.pool.close();
  }

  /** The town with this key, if the simulation is running it right now. */
  running(key: string, now = Date.now()): SimTown | null {
    const town = this.pool.town(key);
    return town && town.ready && now - town.at < SIM_STALE_MS ? town : null;
  }

  /** Every town in a county, run. */
  async refreshTowns(): Promise<void> {
    if (this.refreshing || !this.deps.persistence.ready) return;
    this.refreshing = true;
    try {
      const keys = await this.deps.persistence.countyTownKeys();
      this.pool.setTowns([...new Set([...STARTER_TOWNS.map((t) => t.key), ...keys])]);
    } catch (err) {
      console.error('sim: couldn\'t list the towns:', (err as Error).message);
    } finally {
      this.refreshing = false;
    }
  }

  /** A room's cars someone's in changed: the simulation's told (every room of that town's, together). */
  takenChanged(key: string): void {
    const all = new Set<string>();
    for (const room of this.rooms()) if (room.town === key) for (const id of room.takenList()) all.add(id);
    this.pool.setTaken(key, [...all]);
  }

  private sendPlayers(): void {
    const by = new Map<string, SimPlayer[]>();
    for (const room of this.rooms()) {
      if (!this.pool.town(room.town)) continue;
      const list = by.get(room.town) ?? [];
      for (const p of room.watchPlayers()) list.push({ x: p.x, y: p.z });
      for (const c of room.parkedCars()) list.push(c);
      by.set(room.town, list);
    }
    this.pool.setPlayers(by);
  }

  private ticked(towns: SimTown[]): void {
    const moved = new Map(towns.map((t) => [t.key, t]));
    for (const room of this.rooms()) {
      const town = moved.get(room.town);
      if (!town) continue;
      // Townsfolk added or gone: the whole town (a delta of other counts would be turned down).
      if (town.fullDue) room.simFull(town);
      else room.simTick(town);
    }
    for (const t of towns) t.fullDue = false;
  }
}

/** The simulation, if this server runs one (multiplayer/index.ts sets it). */
let link: SimLink | null = null;

export const sim = (): SimLink | null => link;

export function setSim(l: SimLink | null): void {
  link = l;
}

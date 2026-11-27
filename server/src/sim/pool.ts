// The towns' traffic and townsfolk, run by the .NET worker (sim/, the game's own
// code): this starts it (or several, each with a share of the towns), tells it which
// towns there are, where the players in each are and which cars they've taken, and
// keeps each town's latest from its ticks (ten a second): the full records (for
// someone arriving, Manage, and anyone asking where something is) and the delta
// since the last (for the players already there). Its wire is sim/src/Program.cs's.
//
// It also keeps each town's life (LIFE, once a second: its clock, who's indoors
// where and off the map at each gate, the day's figures; ../../docs/town-life.md)
// and asks it about one car, person or building for the debug view (ASK/ANSWER).
//
// A worker that stops is started again (after a pause that grows if it keeps
// stopping), and told everything again. With no worker to start (SIM=off, or no
// build of it), there's no simulation and the towns' players run them as before.

import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const IN_TOWNS = 1, IN_PLAYERS = 2, IN_TAKEN = 3, IN_ASK = 4;
const OUT_READY = 1, OUT_TICK = 2, OUT_LOOKS = 3, OUT_STATS = 4, OUT_LIFE = 5, OUT_ANSWER = 6;
/** How long an ASK waits for its ANSWER (ms). */
const ASK_MS = 2_000;
const RECORD = 7;
const RESTART_MS = [1_000, 2_000, 5_000, 10_000, 30_000];

/** A town, as the simulation last had it. */
/** What a town's buildings are for (Zoning.cs, made with the town from its key). */
export interface TownZoning {
  figures: { homes: number; residents: number; workers: number; jobs: number };
  /** Each building: [zone (Zoning.Zone), x0, y0, x1, y1 (tiles, exclusive), homes, jobs], the town's frame. */
  buildings: number[][];
}

/** A park (Parks.cs): its middle [x, y], and its outline's rings, each flat [x0, y0, x1, y1, ...] (tiles, the town's frame). */
export interface TownPark {
  at: [number, number];
  rings: number[][];
}

export interface SimTown {
  readonly id: number;
  readonly key: string;
  /** Made and ticking. */
  ready: boolean;
  /** WorldSync's records: every car, then every person (7 bytes each). */
  cars: Uint8Array;
  people: Uint8Array;
  /** What changed since the tick before (sim/src/Delta.cs), for those who had that. */
  delta: Uint8Array;
  /** When (unix ms) the simulation had it so. */
  at: number;
  /** The looks of townsfolk past those the town came with. */
  looks: string[];
  /** Its streets once made (SimTown.Roads: [kind, width, x0, y0, x1, y1, ...], tiles, its own frame), for Manage's map. */
  roads: number[][] | null;
  /** Each of its streets' names, in roads' order ("Elm Street"; "" for a path). */
  roadNames: string[] | null;
  /** What its buildings are for once made (Zoning.cs): [zone, x0, y0, x1, y1, homes, jobs] each, and the town's figures. */
  zoning: TownZoning | null;
  /** Its parks once made (Parks.cs), for Manage's map. */
  parks: TownPark[] | null;
  /** Why it couldn't be made, if it couldn't. */
  error: string | null;
  /** Its townsfolk changed (LOOKS): this tick goes to its players whole, not as a delta (their counts differ). */
  fullDue: boolean;
  /** Its life (LIFE), once the worker's said. */
  life: TownLife | null;
  /** An edited town's skeleton (its key has a `rev`; docs/street-editor-api.md), sent with it in TOWNS. */
  skeleton: unknown;
}

/** A town's life as the worker last said (SimTown.Life in sim/src/SimTown.cs). */
export interface TownLife {
  /** The town clock (unix ms) and as hh:mm. */
  clock: number;
  time: string;
  /** [building (its place in zoning.buildings), people inside]. */
  inside: [number, number][];
  /** People off the map at each gate (gates' order). */
  offMap: number[];
  /** Each gate's and bus stop's place: [x, y] (tiles, the town's frame). */
  gates: [number, number][];
  stops: [number, number][];
  /** The day's figures (TownLife.Summary). */
  summary: Record<string, unknown>;
  /** What each of the latest tick's car and person records is: a letter each, '-' for an empty one (SimTown.Kinds). */
  kinds?: { cars: string; people: string };
  /** The market in the plaza (SimTown.Market), null if the town has none: `people` there now aren't in `inside`. */
  market?: { people: number } | null;
}

export interface SimPlayer {
  x: number;
  y: number;
}

/** How to start a worker: the command and its arguments. */
export interface SimCommand {
  command: string;
  args: string[];
}

/** The worker to start: SIM_COMMAND, the deployed build beside the server, or dev's (dotnet build in sim/). Null: none. */
export function findWorker(env: NodeJS.ProcessEnv = process.env): SimCommand | null {
  if (env.SIM === 'off') return null;
  if (env.SIM_COMMAND) {
    const [command, ...args] = env.SIM_COMMAND.split(' ').filter(Boolean);
    return { command, args };
  }
  const deployed = fileURLToPath(new URL('../../sim/porchlight-sim', import.meta.url));
  if (existsSync(deployed)) return { command: deployed, args: [] };
  for (const config of ['Release', 'Debug']) {
    const dll = fileURLToPath(new URL(`../../../sim/bin/${config}/net8.0/porchlight-sim.dll`, import.meta.url));
    if (existsSync(dll)) return { command: 'dotnet', args: [dll] };
  }
  return null;
}

function frame(type: number, body: Uint8Array): Buffer {
  const head = Buffer.alloc(5);
  head.writeUInt32LE(body.length + 1, 0);
  head[4] = type;
  return Buffer.concat([head, body]);
}

const json = (o: unknown): Buffer => Buffer.from(JSON.stringify(o));

/** One worker process, and the towns it has. */
class Worker {
  proc: ChildProcessWithoutNullStreams | null = null;
  private buf: Buffer = Buffer.alloc(0);
  private stops = 0;
  private startedAt = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private closed = false;

  private readonly index: number;
  private readonly cmd: SimCommand;
  private readonly pool: SimPool;

  constructor(index: number, cmd: SimCommand, pool: SimPool) {
    this.index = index;
    this.cmd = cmd;
    this.pool = pool;
  }

  start(): void {
    if (this.closed) return;
    this.startedAt = Date.now();
    const proc = spawn(this.cmd.command, this.cmd.args, { stdio: ['pipe', 'pipe', 'pipe'], env: process.env });
    this.proc = proc;
    this.buf = Buffer.alloc(0);
    proc.stdout.on('data', (chunk: Buffer) => this.read(chunk));
    proc.stderr.on('data', (chunk: Buffer) => {
      for (const line of chunk.toString().split('\n')) if (line.trim()) this.pool.log(`[sim ${this.index}] ${line.trim()}`);
    });
    proc.stdin.on('error', () => {}); // it went: 'exit' says so
    proc.on('error', (err) => this.pool.log(`sim ${this.index}: couldn't start ${this.cmd.command}: ${err.message}`));
    proc.on('exit', (code, signal) => {
      if (this.proc !== proc) return;
      this.proc = null;
      this.pool.workerStopped(this.index);
      if (this.closed) return;
      // Ran a good while: a fresh start. Kept stopping: wait longer each time.
      if (Date.now() - this.startedAt > 60_000) this.stops = 0;
      const wait = RESTART_MS[Math.min(this.stops++, RESTART_MS.length - 1)];
      this.pool.log(`sim ${this.index}: stopped (${signal ?? `code ${code}`}); starting again in ${wait / 1000} s`);
      this.timer = setTimeout(() => this.start(), wait);
    });
    this.pool.workerStarted(this.index);
  }

  send(type: number, body: Uint8Array): void {
    if (!this.proc?.stdin.writable) return;
    this.proc.stdin.write(frame(type, body));
  }

  close(): void {
    this.closed = true;
    if (this.timer) clearTimeout(this.timer);
    const proc = this.proc;
    this.proc = null;
    if (proc) {
      proc.stdin.end();
      setTimeout(() => proc.kill('SIGKILL'), 2_000).unref();
    }
  }

  private read(chunk: Buffer): void {
    this.buf = this.buf.length ? Buffer.concat([this.buf, chunk]) : chunk;
    for (;;) {
      if (this.buf.length < 5) return;
      const length = this.buf.readUInt32LE(0);
      if (this.buf.length < 4 + length) return;
      const type = this.buf[4];
      const body = this.buf.subarray(5, 4 + length);
      this.buf = this.buf.subarray(4 + length);
      try {
        this.pool.received(type, body);
      } catch (err) {
        this.pool.log(`sim ${this.index}: a message it sent (${type}): ${(err as Error).message}`);
      }
    }
  }
}

export class SimPool {
  private readonly workers: Worker[];
  private readonly towns = new Map<number, SimTown>();
  private readonly byKey = new Map<string, SimTown>();
  private readonly taken = new Map<number, string[]>();
  private nextId = 1;
  private nextAsk = 1;
  private readonly asks = new Map<number, (answer: unknown) => void>();
  /** Called after each tick with the towns it moved on. */
  onTick: (towns: SimTown[]) => void = () => {};
  log: (line: string) => void = (line) => console.log(line);

  constructor(cmd: SimCommand, workers = 1) {
    this.workers = Array.from({ length: Math.max(1, workers) }, (_, i) => new Worker(i, cmd, this));
  }

  start(): void {
    for (const w of this.workers) w.start();
  }

  close(): void {
    for (const w of this.workers) w.close();
  }

  /** The towns to run (their keys, and an edited one's skeleton): new ones are made, gone ones stop. */
  setTowns(keys: Iterable<string>, skeletons: ReadonlyMap<string, unknown> = new Map()): void {
    const want = new Set(keys);
    for (const key of want) {
      if (this.byKey.has(key)) continue;
      const town: SimTown = { id: this.nextId++, key, ready: false, cars: new Uint8Array(0), people: new Uint8Array(0), delta: new Uint8Array(0), at: 0, looks: [], roads: null, roadNames: null, zoning: null, parks: null, error: null, fullDue: false, life: null, skeleton: skeletons.get(key) ?? null };
      this.towns.set(town.id, town);
      this.byKey.set(key, town);
    }
    for (const [key, town] of this.byKey) {
      if (want.has(key)) continue;
      this.byKey.delete(key);
      this.towns.delete(town.id);
      this.taken.delete(town.id);
    }
    for (let i = 0; i < this.workers.length; i++) this.sendTowns(i);
  }

  /** The town with this key, if it's run (made or being made). */
  town(key: string): SimTown | undefined {
    return this.byKey.get(key);
  }

  /** Every town run. */
  all(): Iterable<SimTown> {
    return this.towns.values();
  }

  /** Where the players are, town by town (tiles, each town's frame): what the traffic stops for. */
  setPlayers(players: Map<string, SimPlayer[]>): void {
    const parts: Buffer[][] = this.workers.map(() => []);
    const counts = this.workers.map(() => 0);
    for (const [key, list] of players) {
      const town = this.byKey.get(key);
      if (!town || list.length === 0) continue;
      const n = Math.min(list.length, 1000);
      const b = Buffer.alloc(6 + n * 8);
      b.writeUInt32LE(town.id, 0);
      b.writeUInt16LE(n, 4);
      for (let i = 0; i < n; i++) {
        b.writeFloatLE(list[i].x, 6 + i * 8);
        b.writeFloatLE(list[i].y, 10 + i * 8);
      }
      const w = this.workerOf(town.id);
      parts[w].push(b);
      counts[w]++;
    }
    for (let i = 0; i < this.workers.length; i++) {
      const head = Buffer.alloc(2);
      head.writeUInt16LE(counts[i], 0);
      this.workers[i].send(IN_PLAYERS, Buffer.concat([head, ...parts[i]]));
    }
  }

  /** The cars someone's in, in this town ("t3", "p12"; the room's `taken`). */
  setTaken(key: string, list: string[]): void {
    const town = this.byKey.get(key);
    if (!town) return;
    this.taken.set(town.id, list);
    this.workers[this.workerOf(town.id)].send(IN_TAKEN, json({ id: town.id, taken: list }));
  }

  /**
   * About one car (traffic slot), person (townsfolk index) or building (zoning index) in a town now; or Manage's
   * focus on a car or person (its decision tree and queue; 'brief-…': just a line, for the map's hover), found by
   * where it's drawn (`by` 'slot') or who it is ('id'). Null if there's none or no answer.
   */
  ask(
    key: string,
    kind: 'car' | 'person' | 'building' | 'focus-car' | 'focus-person' | 'brief-car' | 'brief-person' | 'signals' | 'congestion' | 'ground',
    i: number,
    by?: 'slot' | 'id',
  ): Promise<unknown> {
    const town = this.byKey.get(key);
    if (!town || !town.ready) return Promise.resolve(null);
    const ask = this.nextAsk++;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.asks.delete(ask);
        resolve(null);
      }, ASK_MS);
      timer.unref?.();
      this.asks.set(ask, (answer) => {
        clearTimeout(timer);
        resolve(answer);
      });
      this.workers[this.workerOf(town.id)].send(IN_ASK, json({ ask, town: town.id, kind, i, by }));
    });
  }

  private workerOf(id: number): number {
    return id % this.workers.length;
  }

  private sendTowns(i: number): void {
    const mine = [...this.towns.values()].filter((t) => this.workerOf(t.id) === i).map((t) => (t.skeleton ? { id: t.id, key: t.key, skeleton: t.skeleton } : { id: t.id, key: t.key }));
    this.workers[i].send(IN_TOWNS, json(mine));
  }

  /** A worker (re)started: everything it has to know. */
  workerStarted(i: number): void {
    this.sendTowns(i);
    for (const [id, list] of this.taken) if (this.workerOf(id) === i) this.workers[i].send(IN_TAKEN, json({ id, taken: list }));
  }

  /** A worker stopped: its towns aren't ticking until it's back (they'll be made again). */
  workerStopped(i: number): void {
    for (const t of this.towns.values()) if (this.workerOf(t.id) === i) t.ready = false;
  }

  /** Something a worker sent. */
  received(type: number, body: Buffer): void {
    if (type === OUT_TICK) return this.tick(body);
    if (type === OUT_READY) {
      const m = JSON.parse(body.toString()) as { id: number; cars?: number; people?: number; roads?: number[][]; roadNames?: string[]; zoning?: TownZoning['figures']; buildings?: number[][]; parks?: TownPark[]; error?: string };
      const town = this.towns.get(m.id);
      if (!town) return;
      town.error = m.error ?? null;
      if (Array.isArray(m.roads)) town.roads = m.roads;
      if (Array.isArray(m.roadNames)) town.roadNames = m.roadNames;
      if (m.zoning && Array.isArray(m.buildings)) town.zoning = { figures: m.zoning, buildings: m.buildings };
      if (Array.isArray(m.parks)) town.parks = m.parks;
      if (m.error) this.log(`sim: ${town.key} couldn't be made: ${m.error}`);
      return;
    }
    if (type === OUT_LOOKS) {
      const m = JSON.parse(body.toString()) as { id: number; looks: string[] };
      const town = this.towns.get(m.id);
      if (!town) return;
      town.looks = m.looks.filter((l) => typeof l === 'string' && l.length < 64).slice(0, 256);
      town.fullDue = true; // the tick right after has the new count
      return;
    }
    if (type === OUT_LIFE) {
      const m = JSON.parse(body.toString()) as TownLife & { id: number };
      const town = this.towns.get(m.id);
      if (!town) return;
      const { id: _, ...life } = m;
      town.life = life;
      return;
    }
    if (type === OUT_ANSWER) {
      const m = JSON.parse(body.toString()) as { ask: number; answer: unknown };
      const done = this.asks.get(m.ask);
      if (!done) return;
      this.asks.delete(m.ask);
      done(m.answer ?? null);
      return;
    }
    if (type === OUT_STATS) {
      const m = JSON.parse(body.toString()) as { towns: number; ticks: number; avgMs: number; maxMs: number };
      this.log(`sim: ${m.towns} town(s), ${m.ticks} ticks, avg ${m.avgMs} ms, max ${m.maxMs} ms`);
    }
  }

  private tick(b: Buffer): void {
    const at = b.readDoubleLE(0);
    const n = b.readUInt16LE(8);
    let o = 10;
    const moved: SimTown[] = [];
    for (let i = 0; i < n; i++) {
      const id = b.readUInt32LE(o);
      const cars = b.readUInt16LE(o + 4);
      const people = b.readUInt16LE(o + 6);
      o += 8;
      const carBytes = b.subarray(o, o + cars * RECORD);
      o += cars * RECORD;
      const peopleBytes = b.subarray(o, o + people * RECORD);
      o += people * RECORD;
      const deltaLength = b.readUInt32LE(o);
      o += 4;
      const delta = b.subarray(o, o + deltaLength);
      o += deltaLength;
      const town = this.towns.get(id);
      if (!town) continue;
      // Views of the tick's one buffer: nothing copied, and the next tick's replaces them.
      town.cars = carBytes;
      town.people = peopleBytes;
      town.delta = delta;
      town.at = at;
      town.ready = true;
      moved.push(town);
    }
    this.onTick(moved);
  }
}

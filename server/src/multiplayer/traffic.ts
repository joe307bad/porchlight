// A town's traffic settings (the game's TrafficConfig, Explore/TrafficConfig.cs):
// sparse overrides on what the generator decided, kept per town and edited
// only by whoever made the town. An edit is a patch: the version it was made
// against and a list of operations, each checked against the same bounds the
// game's settings sheet uses. A patch made against an old version is refused
// (the editor gets the current settings back and tries again), so two
// edits never silently undo each other.

export const TRAFFIC_PRESETS = ['auto', 'sleepy', 'busy'] as const;
export type TrafficPreset = (typeof TRAFFIC_PRESETS)[number];

/** The town-wide numbers, with their bounds (TrafficConfig.Globals). */
export const TRAFFIC_GLOBALS: Record<string, { min: number; max: number }> = {
  density: { min: 0, max: 2 },
  maxCars: { min: 0, max: 120 },
  cruise: { min: 0.5, max: 1.5 },
  patience: { min: 1, max: 20 },
};

export const TRAFFIC_FLAGS = ['signals', 'nightFlash', 'wander'] as const;

/** JunctionControl, by name. */
export const JUNCTION_CONTROLS = ['None', 'DeadEnd', 'Gate', 'Priority', 'Stop', 'AllWayStop', 'RightBeforeLeft', 'Signal', 'Roundabout'] as const;

export const MAX_JUNCTIONS = 512;
export const MAX_ROADS = 512;
export const MAX_EVENTS = 16;
export const MAX_OPS = 64;
/** The whole settings, as JSON. */
export const MAX_TRAFFIC_BYTES = 64 * 1024;

export interface JunctionOverride {
  key: string;
  control?: string;
  crossings?: boolean;
  leading?: number;
}

export interface RoadOverride {
  key: string;
  closed?: boolean;
  oneWay?: number;
  speed?: number;
}

export interface TrafficEvent {
  name: string;
  kind: string;
  roads: string[];
  from: number;
  until: number;
}

export interface TrafficSettings {
  version: number;
  preset: TrafficPreset;
  density?: number;
  maxCars?: number;
  cruise?: number;
  patience?: number;
  signals?: boolean;
  nightFlash?: boolean;
  wander?: boolean;
  junctions: JunctionOverride[];
  roads: RoadOverride[];
  events: TrafficEvent[];
}

export type TrafficOp =
  | { op: 'preset'; value: TrafficPreset }
  | { op: 'global'; key: string; value: number | null }
  | { op: 'flag'; key: string; value: boolean | null }
  | { op: 'junction'; key: string; control?: string | null; crossings?: boolean; leading?: number | null; reset?: boolean }
  | { op: 'road'; key: string; closed?: boolean; oneWay?: number; speed?: number; reset?: boolean }
  | { op: 'event'; name: string; kind?: string; roads?: string[]; from?: number; until?: number; remove?: boolean }
  | { op: 'reset' };

export const emptyTraffic = (): TrafficSettings => ({ version: 0, preset: 'auto', junctions: [], roads: [], events: [] });

const JUNCTION_KEY = /^j-?\d{1,6}_-?\d{1,6}$/;
const ROAD_KEY = /^r[^\s]{1,80}$/;
const isNum = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

/** Settings as stored, made safe to use (anything unexpected dropped). */
export function cleanTraffic(raw: unknown): TrafficSettings {
  const out = emptyTraffic();
  if (!isObj(raw)) return out;
  if (isNum(raw.version) && Number.isInteger(raw.version) && raw.version >= 0) out.version = raw.version;
  if (TRAFFIC_PRESETS.includes(raw.preset as TrafficPreset)) out.preset = raw.preset as TrafficPreset;
  for (const [k, b] of Object.entries(TRAFFIC_GLOBALS)) {
    const v = raw[k];
    if (isNum(v) && v >= b.min && v <= b.max) (out as unknown as Record<string, number>)[k] = v;
  }
  for (const k of TRAFFIC_FLAGS) if (typeof raw[k] === 'boolean') out[k] = raw[k] as boolean;
  // Each override goes through the same checks as an edit.
  const ops: TrafficOp[] = [];
  if (Array.isArray(raw.junctions)) for (const j of raw.junctions.slice(0, MAX_JUNCTIONS)) if (isObj(j)) ops.push({ ...(j as object), op: 'junction' } as TrafficOp);
  if (Array.isArray(raw.roads)) for (const r of raw.roads.slice(0, MAX_ROADS)) if (isObj(r)) ops.push({ ...(r as object), op: 'road' } as TrafficOp);
  if (Array.isArray(raw.events)) for (const e of raw.events.slice(0, MAX_EVENTS)) if (isObj(e)) ops.push({ ...(e as object), op: 'event' } as TrafficOp);
  // One that doesn't pass is just dropped.
  for (const op of ops) applyOp(out, op);
  return out;
}

/**
 * The settings after `ops`, as a new object (the version one on), or why not: an
 * operation that's malformed or out of bounds refuses the whole patch.
 */
export function applyTrafficPatch(current: TrafficSettings, ops: unknown): TrafficSettings | string {
  if (!Array.isArray(ops) || ops.length === 0) return 'no changes';
  if (ops.length > MAX_OPS) return 'too many changes at once';
  const next: TrafficSettings = structuredClone(current);
  for (const op of ops) {
    const err = applyOp(next, op);
    if (err) return err;
  }
  next.version = current.version + 1;
  if (JSON.stringify(next).length > MAX_TRAFFIC_BYTES) return 'too many settings';
  return next;
}

function applyOp(s: TrafficSettings, raw: unknown): string | null {
  if (!isObj(raw) || typeof raw.op !== 'string') return 'bad change';
  switch (raw.op) {
    case 'reset': {
      const fresh = emptyTraffic();
      fresh.version = s.version;
      Object.keys(s).forEach((k) => delete (s as unknown as Record<string, unknown>)[k]);
      Object.assign(s, fresh);
      return null;
    }
    case 'preset':
      if (!TRAFFIC_PRESETS.includes(raw.value as TrafficPreset)) return 'no such preset';
      s.preset = raw.value as TrafficPreset;
      return null;
    case 'global': {
      const b = typeof raw.key === 'string' && Object.hasOwn(TRAFFIC_GLOBALS, raw.key) ? TRAFFIC_GLOBALS[raw.key] : undefined;
      if (!b) return 'no such setting';
      const rec = s as unknown as Record<string, number | undefined>;
      if (raw.value === null) delete rec[raw.key as string];
      else if (isNum(raw.value) && raw.value >= b.min && raw.value <= b.max) rec[raw.key as string] = raw.value;
      else return `${raw.key} out of bounds`;
      return null;
    }
    case 'flag': {
      if (!TRAFFIC_FLAGS.includes(raw.key as (typeof TRAFFIC_FLAGS)[number])) return 'no such setting';
      const rec = s as unknown as Record<string, boolean | undefined>;
      if (raw.value === null) delete rec[raw.key as string];
      else if (typeof raw.value === 'boolean') rec[raw.key as string] = raw.value;
      else return 'bad setting';
      return null;
    }
    case 'junction': {
      if (typeof raw.key !== 'string' || !JUNCTION_KEY.test(raw.key)) return 'bad junction';
      const at = s.junctions.findIndex((j) => j.key === raw.key);
      if (raw.reset === true) {
        if (at >= 0) s.junctions.splice(at, 1);
        return null;
      }
      const j: JunctionOverride = at >= 0 ? s.junctions[at] : { key: raw.key };
      if (raw.control !== undefined) {
        if (raw.control === null) delete j.control;
        else if (JUNCTION_CONTROLS.includes(raw.control as (typeof JUNCTION_CONTROLS)[number])) j.control = raw.control as string;
        else return 'no such control';
      }
      if (raw.crossings !== undefined) {
        if (typeof raw.crossings !== 'boolean') return 'bad crossings';
        if (raw.crossings) delete j.crossings;
        else j.crossings = false;
      }
      if (raw.leading !== undefined) {
        if (raw.leading === null) delete j.leading;
        else if (isNum(raw.leading) && raw.leading >= 0 && raw.leading <= 10) j.leading = raw.leading;
        else return 'leading out of bounds';
      }
      const empty = j.control === undefined && j.crossings === undefined && j.leading === undefined;
      if (at >= 0 && empty) s.junctions.splice(at, 1);
      else if (at < 0 && !empty) {
        if (s.junctions.length >= MAX_JUNCTIONS) return 'too many junctions changed';
        s.junctions.push(j);
      }
      return null;
    }
    case 'road': {
      if (typeof raw.key !== 'string' || !ROAD_KEY.test(raw.key)) return 'bad road';
      const at = s.roads.findIndex((r) => r.key === raw.key);
      if (raw.reset === true) {
        if (at >= 0) s.roads.splice(at, 1);
        return null;
      }
      const r: RoadOverride = at >= 0 ? s.roads[at] : { key: raw.key };
      if (raw.closed !== undefined) {
        if (typeof raw.closed !== 'boolean') return 'bad closed';
        if (raw.closed) r.closed = true;
        else delete r.closed;
      }
      if (raw.oneWay !== undefined) {
        if (raw.oneWay !== -1 && raw.oneWay !== 0 && raw.oneWay !== 1) return 'bad one way';
        if (raw.oneWay === 0) delete r.oneWay;
        else r.oneWay = raw.oneWay;
      }
      if (raw.speed !== undefined) {
        if (!isNum(raw.speed) || raw.speed < 0.25 || raw.speed > 2) return 'speed out of bounds';
        if (raw.speed === 1) delete r.speed;
        else r.speed = raw.speed;
      }
      const empty = r.closed === undefined && r.oneWay === undefined && r.speed === undefined;
      if (at >= 0 && empty) s.roads.splice(at, 1);
      else if (at < 0 && !empty) {
        if (s.roads.length >= MAX_ROADS) return 'too many roads changed';
        s.roads.push(r);
      }
      return null;
    }
    case 'event': {
      if (typeof raw.name !== 'string' || raw.name.length === 0 || raw.name.length > 48) return 'bad event name';
      const at = s.events.findIndex((e) => e.name === raw.name);
      if (raw.remove === true) {
        if (at >= 0) s.events.splice(at, 1);
        return null;
      }
      const kind = raw.kind ?? 'closure';
      if (kind !== 'closure') return 'no such event';
      if (!Array.isArray(raw.roads) || raw.roads.length === 0 || raw.roads.length > 128 || !raw.roads.every((k) => typeof k === 'string' && ROAD_KEY.test(k))) return 'bad event roads';
      if (!isNum(raw.from) || !isNum(raw.until) || raw.from < 0 || raw.until <= raw.from) return 'bad event times';
      const e: TrafficEvent = { name: raw.name, kind, roads: [...(raw.roads as string[])], from: raw.from, until: raw.until };
      if (at >= 0) s.events[at] = e;
      else {
        if (s.events.length >= MAX_EVENTS) return 'too many events';
        s.events.push(e);
      }
      return null;
    }
    default:
      return 'bad change';
  }
}

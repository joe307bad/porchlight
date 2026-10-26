// Turn-by-turn directions for auto walk: the planned route over the sidewalks
// (walking.ts) read out as steps on named streets (names.ts), the way a map
// app would: "Head north on Elm Street", "Turn left onto High Street",
// "Cross Mill Lane", "Arrive at your destination". Pure (no Three, no DOM).

import { arcPosition } from '../../core/geometry';
import type { Vec2 } from '../../model/types';
import type { SidewalkNetwork } from './layout';
import type { StreetNames } from './names';
import { pointAlong, type Leg, type Spot } from './walking';

export type StepKind = 'start' | 'join' | 'straight' | 'slight-left' | 'slight-right' | 'left' | 'right' | 'uturn' | 'cross' | 'arrive';

export interface Step {
  kind: StepKind;
  text: string;
  /** Street index (into StreetNames.streets) the step is on. */
  street: number;
  /** How far to walk after it, before the next step (tiles). */
  distance: number;
}

export interface Itinerary {
  start: Spot;
  legs: Leg[];
  goal: number;
  joining: boolean;
}

/** About how many metres one tile is (a townsperson is a little under half a tile tall). */
export const METRES_PER_TILE = 4;

/** A walked stretch of one sidewalk, and how we got onto it. */
interface Stretch {
  path: number;
  s0: number;
  s1: number;
  dir: 1 | -1;
  via: 'start' | 'link' | 'cross';
}

/** The route as steps, ending with the arrival. Consecutive stretches along the same street merge into one step. */
export function directions(net: SidewalkNetwork, names: StreetNames, it: Itinerary): Step[] {
  const stretches = walkStretches(net, it);
  const streetOf = (path: number) => names.ofRoad[net.paths[path].road];
  const name = (street: number) => names.streets[street]?.name ?? 'the street';
  const steps: Step[] = [];
  stretches.forEach((st, i) => {
    const street = streetOf(st.path);
    const len = Math.abs(st.s1 - st.s0);
    const last = steps[steps.length - 1];
    if (st.via === 'start') {
      const dir = headingAt(net, st, 'start');
      steps.push({
        kind: it.joining ? 'join' : 'start',
        text: it.joining ? `Make your way to ${name(street)}, then head ${compass(dir)}` : `Head ${compass(dir)} on ${name(street)}`,
        street,
        distance: len,
      });
      return;
    }
    if (st.via === 'cross') {
      steps.push({ kind: 'cross', text: `Cross ${name(street)}`, street, distance: len });
      return;
    }
    const before = headingAt(net, stretches[i - 1], 'end');
    const after = headingAt(net, st, 'start');
    const turn = turnKind(before, after);
    if (street === last.street && turn !== 'uturn') {
      last.distance += len; // carrying on along the same street through a junction
      return;
    }
    const text =
      turn === 'uturn'
        ? street === last.street
          ? `Turn round at the end of ${name(street)}`
          : `Turn round onto ${name(street)}`
        : turn === 'straight'
          ? `Continue onto ${name(street)}`
          : `${turn.startsWith('slight') ? 'Bear' : 'Turn'} ${turn.endsWith('left') ? 'left' : 'right'} onto ${name(street)}`;
    steps.push({ kind: turn, text, street, distance: len });
  });
  const goalStreet = streetOf(it.goal);
  steps.push({ kind: 'arrive', text: `Arrive at your destination on ${name(goalStreet)}`, street: goalStreet, distance: 0 });
  // A crossing straight after a turn onto the same street reads better as one step.
  for (let k = steps.length - 2; k >= 1; k--) {
    const a = steps[k - 1];
    const b = steps[k];
    if (b.kind === 'cross' && a.street === b.street && a.distance < 0.6 && a.kind !== 'start' && a.kind !== 'join') {
      a.text += ' and cross to the other side';
      a.distance += b.distance;
      steps.splice(k, 1);
    }
  }
  return steps;
}

/** The sidewalk stretches the route walks, in order. */
function walkStretches(net: SidewalkNetwork, it: Itinerary): Stretch[] {
  const len = (path: number) => {
    const at = net.paths[path].at;
    return at[at.length - 1];
  };
  const out: Stretch[] = [];
  let { path, s, dir } = it.start;
  let via: Stretch['via'] = 'start';
  for (const leg of it.legs) {
    if (leg.cross) {
      out.push({ path, s0: s, s1: s, dir, via });
      const here = pointAlong(net.paths[path], s);
      s = arcPosition(here, net.paths[leg.path].points);
      via = 'cross';
    } else {
      out.push({ path, s0: s, s1: dir > 0 ? len(path) : 0, dir, via });
      s = leg.dir > 0 ? 0 : len(leg.path);
      via = 'link';
    }
    path = leg.path;
    dir = leg.dir;
  }
  out.push({ path, s0: s, s1: path === it.goal ? len(path) / 2 : dir > 0 ? len(path) : 0, dir, via });
  return out;
}

/** Unit direction of travel at the start or end of a stretch. */
function headingAt(net: SidewalkNetwork, st: Stretch, where: 'start' | 'end'): Vec2 {
  const p = net.paths[st.path];
  const total = p.at[p.at.length - 1];
  // A zero-length stretch (crossing straight on) still has a direction: the way the leg walks.
  const sign = st.s1 === st.s0 ? st.dir : st.s1 > st.s0 ? 1 : -1;
  const s = where === 'start' ? st.s0 : st.s1;
  const a = pointAlong(p, Math.max(0, Math.min(total, s - 0.4 * sign)));
  const b = pointAlong(p, Math.max(0, Math.min(total, s + 0.4 * sign)));
  const l = Math.hypot(b.x - a.x, b.y - a.y) || 1;
  return { x: (b.x - a.x) / l, y: (b.y - a.y) / l };
}

/** What kind of turn takes you from heading `a` to heading `b` (map y runs south, so clockwise is right). */
export function turnKind(a: Vec2, b: Vec2): Exclude<StepKind, 'start' | 'join' | 'cross' | 'arrive'> {
  const angle = (Math.atan2(a.x * b.y - a.y * b.x, a.x * b.x + a.y * b.y) * 180) / Math.PI;
  const m = Math.abs(angle);
  if (m < 30) return 'straight';
  if (m > 150) return 'uturn';
  const side = angle > 0 ? 'right' : 'left';
  return m < 60 ? (`slight-${side}` as const) : side;
}

/** Compass point of a map direction (north is up the map, -y). */
export function compass(d: Vec2): string {
  const points = ['north', 'north-east', 'east', 'south-east', 'south', 'south-west', 'west', 'north-west'];
  const deg = (Math.atan2(d.x, -d.y) * 180) / Math.PI;
  return points[(Math.round(deg / 45) + 8) % 8];
}

/** A walking distance in tiles as shown to the player: metres, rounded like a map app would. */
export function formatDistance(tiles: number): string {
  const m = tiles * METRES_PER_TILE;
  if (m < 10) return '';
  if (m < 100) return `${Math.round(m / 5) * 5} m`;
  if (m < 1000) return `${Math.round(m / 10) * 10} m`;
  return `${(m / 1000).toFixed(1)} km`;
}

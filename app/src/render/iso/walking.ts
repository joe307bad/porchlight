// Walking the sidewalk network: follow a sidewalk, take a turning at its end,
// turn around, or cross the road to the sidewalk opposite. Used by the
// townsfolk and by the player's auto-walk. Pure (no Pixi), so it's unit-tested.

import { arcPosition } from '../../core/geometry';
import { MinHeap } from '../../core/heap';
import type { Rng } from '../../core/rng';
import type { Vec2 } from '../../model/types';
import type { SidewalkNetwork, SidewalkPath } from './layout';

interface Spot {
  path: number;
  s: number;
  dir: 1 | -1;
}

/** One step of a planned route: walk `path` in `dir`, reached by crossing the road if `cross`. */
interface Leg {
  path: number;
  dir: 1 | -1;
  cross: boolean;
}

/** Crossing the road costs about this many tiles of walking (so routes don't zig-zag across). */
const CROSSING_COST = 4;

export class SidewalkWalk {
  private path: number;
  private s: number;
  private dir: 1 | -1;
  /** A straight walk between two spots (crossing a junction or a road), then carry on from `then`. */
  private crossing: { from: Vec2; to: Vec2; t: number; then: Spot } | null = null;
  /** Remaining legs of the route to `goal` (empty when just wandering). */
  private route: Leg[] = [];
  private goal: number | null = null;
  /** When set, pick a new far-off goal every time one is reached. */
  private roaming = false;

  constructor(
    private readonly net: SidewalkNetwork,
    private readonly rng: Rng,
    start: Spot,
  ) {
    this.path = start.path;
    this.s = start.s;
    this.dir = start.dir;
  }

  /** Start somewhere random on the network. */
  static random(net: SidewalkNetwork, rng: Rng): SidewalkWalk {
    const path = rng.int(0, net.paths.length - 1);
    return new SidewalkWalk(net, rng, { path, s: rng.range(0, length(net.paths[path])), dir: rng.chance(0.5) ? 1 : -1 });
  }

  /** Start from an arbitrary point: walk straight to the nearest sidewalk, then follow it. */
  static join(net: SidewalkNetwork, rng: Rng, from: Vec2): SidewalkWalk {
    let best = { path: 0, s: 0, d: Infinity };
    net.paths.forEach((p, i) => {
      const s = arcPosition(from, p.points);
      const q = pointAlong(p, s);
      const d = Math.hypot(q.x - from.x, q.y - from.y);
      if (d < best.d) best = { path: i, s, d };
    });
    const walk = new SidewalkWalk(net, rng, { path: best.path, s: best.s, dir: rng.chance(0.5) ? 1 : -1 });
    walk.crossing = { from, to: pointAlong(net.paths[best.path], best.s), t: 0, then: { path: best.path, s: best.s, dir: walk.dir } };
    return walk;
  }

  /**
   * Keep heading somewhere far away: plan a route over sidewalks and street
   * crossings to a distant sidewalk, and on arrival pick another.
   */
  roam(): void {
    this.roaming = true;
    this.pickFarGoal();
  }

  /** Where the current goal is (the middle of its sidewalk), if any. */
  goalPoint(): Vec2 | null {
    if (this.goal === null) return null;
    const p = this.net.paths[this.goal];
    return pointAlong(p, length(p) / 2);
  }

  /**
   * The planned route as a polyline from where we are to the goal: along each
   * sidewalk, across junctions and across roads, ending mid-way along the goal
   * sidewalk. Empty when not heading anywhere.
   */
  routePoints(): Vec2[] {
    if (this.goal === null) return [];
    const pts: Vec2[] = [this.position()];
    let path = this.path;
    let s = this.s;
    let dir = this.dir;
    if (this.crossing) {
      pts.push(this.crossing.to);
      ({ path, s, dir } = this.crossing.then);
    }
    for (const leg of this.route) {
      if (leg.cross) {
        const other = this.net.paths[leg.path];
        s = arcPosition(pts[pts.length - 1], other.points);
        pts.push(pointAlong(other, s));
      } else {
        const p = this.net.paths[path];
        pts.push(...slice(p, s, dir > 0 ? length(p) : 0));
        const next = this.net.paths[leg.path];
        s = leg.dir > 0 ? 0 : length(next);
        pts.push(pointAlong(next, s));
      }
      path = leg.path;
      dir = leg.dir;
    }
    if (path === this.goal) {
      const p = this.net.paths[path];
      pts.push(...slice(p, s, length(p) / 2));
    }
    return pts;
  }

  private pickFarGoal(): void {
    const here = this.position();
    let best = -1;
    let bestD = -1;
    // The farthest of a handful of random sidewalks: far, but not always the same corner.
    for (let k = 0; k < 12; k++) {
      const i = this.rng.int(0, this.net.paths.length - 1);
      const p = this.net.paths[i];
      const m = pointAlong(p, length(p) / 2);
      const d = Math.hypot(m.x - here.x, m.y - here.y);
      if (d > bestD) {
        bestD = d;
        best = i;
      }
    }
    this.goal = best;
    this.plan();
  }

  /** Dijkstra over (sidewalk, direction) states from where we are to the goal sidewalk. */
  private plan(): void {
    this.route = [];
    if (this.goal === null) return;
    const start = this.crossing ? this.crossing.then : { path: this.path, dir: this.dir };
    const n = this.net.paths.length * 2;
    const key = (path: number, dir: 1 | -1) => path * 2 + (dir > 0 ? 1 : 0);
    const cost = new Float64Array(n).fill(Infinity);
    const prev = new Int32Array(n).fill(-1);
    const viaCross = new Uint8Array(n);
    const heap = new MinHeap();
    const s0 = key(start.path, start.dir);
    cost[s0] = 0;
    heap.push(s0, 0);
    let found = -1;
    while (heap.size) {
      const state = heap.pop();
      const path = state >> 1;
      const dir: 1 | -1 = state & 1 ? 1 : -1;
      if (path === this.goal) {
        found = state;
        break;
      }
      const relax = (next: number, c: number, cross: boolean) => {
        if (cost[state] + c < cost[next]) {
          cost[next] = cost[state] + c;
          prev[next] = state;
          viaCross[next] = cross ? 1 : 0;
          heap.push(next, cost[next]);
        }
      };
      // Walk to the end of this sidewalk and on to a connecting one.
      const end = dir > 0 ? 1 : 0;
      for (const link of this.net.links[path * 2 + end]) {
        const q = link >> 1;
        relax(key(q, (link & 1) === 0 ? 1 : -1), length(this.net.paths[path]), false);
      }
      // Cross the road to the sidewalk opposite.
      const sibling = this.siblingOf(path);
      if (sibling >= 0) relax(key(sibling, dir), CROSSING_COST, true);
    }
    if (found < 0) {
      // Unreachable from here: forget it, and (when roaming) pick another next step.
      this.goal = null;
      return;
    }
    const legs: Leg[] = [];
    for (let s = found; s !== s0 && s >= 0; s = prev[s]) {
      legs.push({ path: s >> 1, dir: s & 1 ? 1 : -1, cross: viaCross[s] === 1 });
    }
    this.route = legs.reverse();
  }

  private siblingOf(path: number): number {
    const p = this.net.paths[path];
    let out = -1;
    this.net.paths.forEach((q, i) => {
      if (i !== path && q.road === p.road && q.side !== p.side) out = i;
    });
    return out;
  }

  get isCrossing(): boolean {
    return this.crossing !== null;
  }

  position(): Vec2 {
    if (this.crossing) {
      const c = this.crossing;
      return { x: c.from.x + (c.to.x - c.from.x) * c.t, y: c.from.y + (c.to.y - c.from.y) * c.t };
    }
    return pointAlong(this.net.paths[this.path], this.s);
  }

  /** Unit vector of the way we're walking along the sidewalk. */
  heading(): Vec2 {
    const p = this.net.paths[this.path];
    const a = pointAlong(p, this.s);
    const b = pointAlong(p, Math.max(0, Math.min(length(p), this.s + 0.3 * this.dir)));
    const l = Math.hypot(b.x - a.x, b.y - a.y);
    if (l < 1e-6) return { x: 0, y: 0 };
    return { x: (b.x - a.x) / l, y: (b.y - a.y) / l };
  }

  /** Unit vector from this sidewalk towards its road. */
  towardRoad(): Vec2 {
    const p = this.net.paths[this.path];
    const a = pointAlong(p, Math.max(0, this.s - 0.15));
    const b = pointAlong(p, Math.min(length(p), this.s + 0.15));
    const tx = b.x - a.x;
    const ty = b.y - a.y;
    const l = Math.hypot(tx, ty) || 1;
    // The sidewalk was offset by (ty, -tx) * side from the road, so the road lies the other way.
    return { x: (-ty / l) * p.side, y: (tx / l) * p.side };
  }

  /** Walk `distance` tiles further and return the new position. */
  advance(distance: number): Vec2 {
    let left = distance;
    for (let guard = 0; left > 1e-9 && guard < 8; guard++) {
      if (this.crossing) {
        const c = this.crossing;
        const len = Math.max(0.05, Math.hypot(c.to.x - c.from.x, c.to.y - c.from.y));
        const need = (1 - c.t) * len;
        if (left < need) {
          c.t += left / len;
          left = 0;
        } else {
          left -= need;
          ({ path: this.path, s: this.s, dir: this.dir } = c.then);
          this.crossing = null;
        }
        continue;
      }
      if (this.route.length && this.route[0].cross) {
        const leg = this.route.shift()!;
        this.dir = leg.dir;
        if (this.crossStreet(false)) continue;
      }
      if (this.roaming && (this.goal === null || (this.path === this.goal && !this.route.length))) this.pickFarGoal();
      const p = this.net.paths[this.path];
      const total = length(p);
      const next = this.s + left * this.dir;
      if (next >= 0 && next <= total) {
        this.s = next;
        left = 0;
      } else {
        const end: 0 | 1 = next < 0 ? 0 : 1;
        left -= end === 0 ? this.s : total - this.s;
        this.s = end === 0 ? 0 : total;
        this.atEnd(end);
      }
    }
    return this.position();
  }

  turnAround(): void {
    if (this.crossing) {
      const c = this.crossing;
      const here = this.position();
      // Head back to the spot we set out from.
      this.crossing = { from: here, to: c.from, t: 0, then: { path: this.path, s: this.s, dir: (this.dir * -1) as 1 | -1 } };
    } else {
      this.dir = (this.dir * -1) as 1 | -1;
    }
    this.plan();
  }

  /** Cross the road to the sidewalk directly opposite. False if there's no sidewalk across. */
  crossStreet(replan = true): boolean {
    if (this.crossing) return false;
    const here = this.position();
    const p = this.net.paths[this.path];
    let target = -1;
    this.net.paths.forEach((q, i) => {
      if (i !== this.path && q.road === p.road && q.side !== p.side) target = i;
    });
    if (target < 0) return false;
    const other = this.net.paths[target];
    const s = arcPosition(here, other.points);
    // Both sidewalks follow their road's direction, so keep walking the same way.
    this.crossing = { from: here, to: pointAlong(other, s), t: 0, then: { path: target, s, dir: this.dir } };
    if (replan) this.plan();
    return true;
  }

  private needsPlan = false;

  /** At a sidewalk's end: follow the route if there is one, else wander on (or turn round at a dead end). */
  private atEnd(end: 0 | 1): void {
    if (this.needsPlan) {
      this.needsPlan = false;
      this.plan();
    }
    const options = this.net.links[this.path * 2 + end];
    const leg = this.route[0];
    const routed = leg && !leg.cross ? options.find((o) => o >> 1 === leg.path) : undefined;
    if (routed !== undefined) this.route.shift();
    else if (this.goal !== null) this.route = []; // lost the route: replan from the next sidewalk
    if (routed === undefined && this.goal !== null) this.needsPlan = true;
    if (!options.length || (routed === undefined && this.goal === null && this.rng.chance(0.08))) {
      this.dir = end === 0 ? 1 : -1;
      return;
    }
    const target = routed ?? this.rng.pick(options);
    const path = target >> 1;
    const fromStart = (target & 1) === 0;
    const p = this.net.paths[path];
    const s = fromStart ? 0 : length(p);
    this.crossing = { from: this.position(), to: pointAlong(p, s), t: 0, then: { path, s, dir: fromStart ? 1 : -1 } };
  }
}

/** Points along a sidewalk from arc position s0 to s1 (either direction), ends included. */
function slice(p: SidewalkPath, s0: number, s1: number): Vec2[] {
  const out: Vec2[] = [pointAlong(p, s0)];
  const lo = Math.min(s0, s1);
  const hi = Math.max(s0, s1);
  const inner = p.points.filter((_, k) => p.at[k] > lo && p.at[k] < hi);
  out.push(...(s1 >= s0 ? inner : inner.reverse()));
  out.push(pointAlong(p, s1));
  return out;
}

function length(p: SidewalkPath): number {
  return p.at[p.at.length - 1];
}

export function pointAlong(path: SidewalkPath, s: number): Vec2 {
  let k = 1;
  while (k < path.at.length - 1 && path.at[k] < s) k++;
  const a = path.points[k - 1];
  const b = path.points[k];
  const f = (s - path.at[k - 1]) / Math.max(1e-9, path.at[k] - path.at[k - 1]);
  return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f };
}

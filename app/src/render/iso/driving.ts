// Driving: the player can get into any car (parked, or one passing in the
// traffic) and drive it. W / S are the accelerator and the brake (held at a
// standstill, S reverses), A / D steer. Arcade handling: steering bites
// harder the faster you go, the car coasts to a stop when you let go, and it
// stops dead against buildings, sheds, water and the map's edge (bridges are
// fine). Auto walk at the wheel becomes auto drive (`AutoDrive`): the car
// drives itself round town at a brisk pace. Pure state (no Three), so it's
// unit-tested; the view draws the car.

import { arcPosition, dist, distToPolyline } from '../../core/geometry';
import { index } from '../../core/grid';
import { MinHeap } from '../../core/heap';
import type { Rng } from '../../core/rng';
import { Overlay, Terrain, type TownModel, type Vec2 } from '../../model/types';
import { Use, type IsoLayout, type RoadStroke } from './layout';

/** Top speed forward and in reverse (tiles per second). */
export const TOP_SPEED = 5.2;
const REVERSE_SPEED = 1.6;
/** Boost (the right trigger, or Shift): how much higher the top speed goes, and how much harder it pulls. */
const BOOST_SPEED = 1.7;
const BOOST_ACCEL = 1.6;
/** Acceleration, braking and coasting drag (tiles per second, per second). */
const ACCEL = 3.2;
const BRAKE = 7;
const DRAG = 1.4;
/** Fastest the car can turn (radians per second), reached from this speed up. */
const STEER_RATE = 2.1;
const FULL_STEER_SPEED = 1.6;
/** A car's footprint (tiles): length is CAR_LENGTH in world.ts, width a little under half that. */
export const CAR_HALF_LENGTH = 0.5;
export const CAR_HALF_WIDTH = 0.22;

export class Vehicle {
  /** Forward speed (tiles per second, negative reversing). */
  speed = 0;

  constructor(
    /** Index into the car model list. */
    readonly model: number,
    public x: number,
    public y: number,
    /** Direction the car points, radians in map space (atan2(dy, dx)), as for traffic. */
    public heading: number,
  ) {}

  /** Unit vector the car points along. */
  get forward(): Vec2 {
    return { x: Math.cos(this.heading), y: Math.sin(this.heading) };
  }

  /**
   * One step of driving. `throttle` is +1 (accelerate), -1 (brake, then
   * reverse) or 0 (coast); `steer` is -1 (left) to +1 (right) as seen from
   * the driver's seat. `open` says where the car may be.
   */
  drive(throttle: number, steer: number, dt: number, open: (x: number, y: number) => boolean, boost = false): void {
    // Part throttle (a controller trigger) pulls less hard, and only up to its share of top speed.
    // Boosting raises the top speed; let go and the car coasts back down to the normal one.
    const top = TOP_SPEED * (boost ? BOOST_SPEED : 1);
    if (throttle > 0 && this.speed <= top * throttle) {
      this.speed += (this.speed < 0 ? BRAKE : ACCEL * (boost ? BOOST_ACCEL : 1)) * throttle * dt;
      this.speed = Math.min(top, this.speed);
    } else if (throttle < 0) this.speed -= (this.speed > 0 ? BRAKE : ACCEL * 0.6) * -throttle * dt;
    else this.speed -= Math.sign(this.speed) * Math.min(Math.abs(this.speed), DRAG * dt);
    this.speed = Math.max(-REVERSE_SPEED, this.speed);
    // Map y runs south, so turning right is a growing heading. Reversing steers the back round.
    const bite = Math.min(1, Math.abs(this.speed) / FULL_STEER_SPEED) * Math.sign(this.speed);
    const heading = this.heading + steer * STEER_RATE * bite * dt;
    const step = this.speed * dt;
    const x = this.x + Math.cos(heading) * step;
    const y = this.y + Math.sin(heading) * step;
    if (fits(x, y, heading, open)) {
      this.x = x;
      this.y = y;
      this.heading = heading;
    } else if (fits(this.x, this.y, heading, open)) {
      this.heading = heading; // can still turn on the spot it's in
      this.speed = 0;
    } else {
      this.speed = 0; // stopped dead against it
    }
  }
}

/** True if a car at (x, y) pointing along `heading` stands wholly on open ground. */
export function fits(x: number, y: number, heading: number, open: (x: number, y: number) => boolean): boolean {
  const f = { x: Math.cos(heading), y: Math.sin(heading) };
  const r = { x: -f.y, y: f.x };
  for (const [a, b] of [[1, 1], [1, -1], [-1, 1], [-1, -1], [1, 0], [-1, 0], [0, 1], [0, -1]]) {
    const px = x + f.x * CAR_HALF_LENGTH * a + r.x * CAR_HALF_WIDTH * b;
    const py = y + f.y * CAR_HALF_LENGTH * a + r.y * CAR_HALF_WIDTH * b;
    if (!open(px, py)) return false;
  }
  return true;
}

/** Where a car may go: anywhere on the map but buildings, sheds and open water (bridges are fine). */
export function drivable(model: TownModel, layout: IsoLayout): (x: number, y: number) => boolean {
  const { width: w, height: h, tiles } = model;
  const ok = new Uint8Array(w * h);
  for (let i = 0; i < ok.length; i++) {
    const use = layout.use[i];
    const wet = tiles.terrain[i] === Terrain.WATER_SHALLOW || tiles.terrain[i] === Terrain.WATER_DEEP;
    const over = tiles.overlay[i];
    ok[i] = use === Use.HOUSE || use === Use.SHED || (wet && over === Overlay.NONE) ? 0 : 1;
  }
  return (x, y) => {
    const tx = Math.floor(x);
    const ty = Math.floor(y);
    return x > 0.05 && y > 0.05 && x < w - 0.05 && y < h - 0.05 && ok[index(tx, ty, w)] === 1;
  };
}

/** Where the driver steps out: beside the driver's door if there's room, else the other side, behind or in front. */
export function exitSpot(car: Vehicle, canStand: (x: number, y: number) => boolean): Vec2 | null {
  const f = car.forward;
  const left = { x: f.y, y: -f.x }; // map y runs south, so the left of travel is (fy, -fx)
  const side = CAR_HALF_WIDTH + 0.28;
  const end = CAR_HALF_LENGTH + 0.28;
  for (const [a, b] of [[0, side], [0, -side], [-end, 0], [end, 0], [0, side + 0.4], [0, -side - 0.4]]) {
    const p = { x: car.x + f.x * a + left.x * b, y: car.y + f.y * a + left.y * b };
    if (canStand(p.x, p.y)) return p;
  }
  return null;
}

/** Auto drive's cruising speed (tiles per second): brisk, not far off top speed. */
export const AUTO_DRIVE_SPEED = 4.4;
/** Slowest it takes a sharp corner, and how far before the junction it starts braking for one (tiles). */
const CORNER_SPEED = 2;
const CORNER_BRAKING = 2.4;
/** Keeps this far behind the car ahead (centre to centre, tiles), slowing from twice as far. */
const FOLLOW_GAP = 1.5;
const LANE_HALF = 0.42;

/**
 * Auto drive: the car drives itself in the right-hand lane of the roads,
 * planning a route over the road graph to a far-off junction and, on
 * arriving, picking another. It brakes for sharp corners and dead ends (where
 * it turns round) and keeps its distance from the car ahead. The car eases
 * onto the lane it's steered to, so it pulls in smoothly from wherever it was
 * and doesn't jump from one road's lane to the next's at a junction.
 */
export class AutoDrive {
  private road = 0;
  private s = 0;
  private dir: 1 | -1 = 1;
  private speed: number;
  /** Roads still to drive after this one, and the junction they lead to. */
  private route: number[] = [];
  private goal: number | null = null;
  private readonly at: number[][];
  private readonly lengths: number[];
  /** The drivable roads meeting at each graph node. */
  private readonly byNode = new Map<number, number[]>();

  constructor(
    private readonly roads: readonly RoadStroke[],
    private readonly rng: Rng,
    car: Vehicle,
  ) {
    this.at = roads.map((r) => {
      const at = [0];
      for (let k = 1; k < r.points.length; k++) at.push(at[k - 1] + dist(r.points[k], r.points[k - 1]));
      return at;
    });
    this.lengths = this.at.map((a) => a[a.length - 1]);
    roads.forEach((r, i) => {
      if (r.kind === 'path' || this.lengths[i] < 0.5) return;
      for (const n of [r.from, r.to]) this.byNode.set(n, [...(this.byNode.get(n) ?? []), i]);
    });
    // Pull onto the nearest road, carrying on the way the car points.
    let best = Infinity;
    roads.forEach((r, i) => {
      if (r.kind === 'path' || this.lengths[i] < 0.5) return;
      const d = distToPolyline(car, r.points);
      if (d < best) {
        best = d;
        this.road = i;
      }
    });
    this.s = arcPosition(car, roads[this.road].points);
    const t = this.tangent(this.road, this.s);
    this.dir = t.x * car.forward.x + t.y * car.forward.y >= 0 ? 1 : -1;
    this.speed = Math.max(0, car.speed);
    this.pickGoal();
  }

  /** Drive on for dt seconds and steer `car` to match; `obstacles` are other cars it mustn't run into. */
  step(car: Vehicle, dt: number, obstacles: readonly Vec2[] = []): void {
    let target = AUTO_DRIVE_SPEED;
    // Brake for a sharp turn (or turning round at a dead end) at the end of this road.
    const left = this.dir > 0 ? this.lengths[this.road] - this.s : this.s;
    if (left < CORNER_BRAKING) {
      const sharp = this.turnAhead();
      if (sharp > 0.25) target = Math.min(target, CORNER_SPEED + (AUTO_DRIVE_SPEED - CORNER_SPEED) * Math.max(0, left / CORNER_BRAKING) * (1 - sharp));
    }
    // Keep back from whatever's ahead in the lane.
    const f = car.forward;
    for (const o of obstacles) {
      const dx = o.x - car.x;
      const dy = o.y - car.y;
      const along = dx * f.x + dy * f.y;
      if (along <= 0 || along > FOLLOW_GAP * 2 || Math.abs(dx * f.y - dy * f.x) > LANE_HALF) continue;
      target = Math.min(target, along < FOLLOW_GAP ? 0 : (AUTO_DRIVE_SPEED * (along - FOLLOW_GAP)) / FOLLOW_GAP);
    }
    this.speed += Math.max(-7 * dt, Math.min(3.6 * dt, target - this.speed));
    this.s += this.dir * this.speed * dt;
    for (let guard = 0; guard < 4 && (this.s < 0 || this.s > this.lengths[this.road]); guard++) this.atJunction();

    // Steer the car onto the lane: a quarter of the road's width right of its centreline.
    const r = this.roads[this.road];
    const p = pointAt(r.points, this.at[this.road], this.s);
    const t = this.tangent(this.road, this.s);
    const tx = t.x * this.dir;
    const ty = t.y * this.dir;
    const lane = r.width / 4;
    const want = { x: p.x - ty * lane, y: p.y + tx * lane };
    const k = Math.min(1, dt * 6);
    car.x += (want.x - car.x) * k;
    car.y += (want.y - car.y) * k;
    let turn = Math.atan2(ty, tx) - car.heading;
    while (turn > Math.PI) turn -= Math.PI * 2;
    while (turn < -Math.PI) turn += Math.PI * 2;
    car.heading += turn * k;
    car.speed = this.speed;
  }

  /** Where it's heading (the junction at the end of the route), if anywhere. */
  goalPoint(): Vec2 | null {
    return this.goal === null ? null : this.nodeAt(this.goal);
  }

  /** The rest of the route as a polyline along the roads' centrelines. */
  routePoints(): Vec2[] {
    const r = this.roads[this.road];
    const pts: Vec2[] = [pointAt(r.points, this.at[this.road], this.s)];
    const ahead = r.points.filter((_, k) => (this.dir > 0 ? this.at[this.road][k] > this.s : this.at[this.road][k] < this.s));
    pts.push(...(this.dir > 0 ? ahead : ahead.reverse()));
    let node = this.dir > 0 ? r.to : r.from;
    for (const i of this.route) {
      const q = this.roads[i];
      pts.push(...(q.from === node ? q.points : [...q.points].reverse()));
      node = q.from === node ? q.to : q.from;
    }
    return pts;
  }

  /**
   * The rest of the route as stretches of road, for turn-by-turn directions:
   * each road still to drive, how far along it, and the way the car is going
   * at its start and end.
   */
  stretches(): { road: number; length: number; start: Vec2; end: Vec2 }[] {
    const out: { road: number; length: number; start: Vec2; end: Vec2 }[] = [];
    const along = (road: number, s: number, dir: 1 | -1) => {
      const t = this.tangent(road, s);
      return { x: t.x * dir, y: t.y * dir };
    };
    const len = this.lengths[this.road];
    out.push({
      road: this.road,
      length: this.dir > 0 ? len - this.s : this.s,
      start: along(this.road, this.s, this.dir),
      end: along(this.road, this.dir > 0 ? len - 0.3 : 0.3, this.dir),
    });
    let node = this.dir > 0 ? this.roads[this.road].to : this.roads[this.road].from;
    for (const i of this.route) {
      const q = this.roads[i];
      const dir: 1 | -1 = q.from === node ? 1 : -1;
      const l = this.lengths[i];
      out.push({ road: i, length: l, start: along(i, dir > 0 ? 0.3 : l - 0.3, dir), end: along(i, dir > 0 ? l - 0.3 : 0.3, dir) });
      node = dir > 0 ? q.to : q.from;
    }
    return out;
  }

  private atJunction(): void {
    const r = this.roads[this.road];
    const node = this.s > this.lengths[this.road] ? r.to : r.from;
    if (!this.route.length) this.pickGoal(node); // arrived (or lost the way): somewhere new
    let next = this.route.shift();
    if (next === undefined) {
      // No route from here: any other road, else turn round (a dead end).
      const options = (this.byNode.get(node) ?? []).filter((i) => i !== this.road);
      next = options.length ? this.rng.pick(options) : this.road;
    }
    const over = Math.max(0, this.s - this.lengths[this.road], -this.s);
    if (next === this.road && (this.byNode.get(node) ?? []).length <= 1) {
      this.dir = (this.dir * -1) as 1 | -1;
      this.s = this.dir > 0 ? over : this.lengths[this.road] - over;
      return;
    }
    const q = this.roads[next];
    this.road = next;
    this.dir = q.from === node ? 1 : -1;
    this.s = this.dir > 0 ? over : this.lengths[next] - over;
  }

  /** How sharp the turn at the end of this road is: 0 straight on, 1 turning right round. */
  private turnAhead(): number {
    const r = this.roads[this.road];
    const node = this.dir > 0 ? r.to : r.from;
    const next = this.route[0];
    if (next === undefined) return (this.byNode.get(node) ?? []).length <= 1 ? 1 : 0.5;
    const inT = this.tangent(this.road, this.dir > 0 ? this.lengths[this.road] - 0.3 : 0.3);
    const a = { x: inT.x * this.dir, y: inT.y * this.dir };
    const q = this.roads[next];
    const outDir = q.from === node ? 1 : -1;
    const outT = this.tangent(next, outDir > 0 ? 0.3 : this.lengths[next] - 0.3);
    const b = { x: outT.x * outDir, y: outT.y * outDir };
    return (1 - (a.x * b.x + a.y * b.y)) / 2;
  }

  /** Plan to a far-off junction: the farthest of a handful of random ones, by Dijkstra over the roads. */
  private pickGoal(from?: number): void {
    const r = this.roads[this.road];
    const start = from ?? (this.dir > 0 ? r.to : r.from);
    const nodes = [...this.byNode.keys()];
    const here = this.nodeAt(start);
    for (let attempt = 0; attempt < 3; attempt++) {
      let goal = start;
      let far = -1;
      for (let k = 0; k < 12; k++) {
        const n = this.rng.pick(nodes);
        const d = dist(this.nodeAt(n), here);
        if (d > far) {
          far = d;
          goal = n;
        }
      }
      const route = this.plan(start, goal);
      if (route && route.length) {
        this.goal = goal;
        this.route = route;
        return;
      }
    }
    this.goal = null;
    this.route = [];
  }

  private plan(start: number, goal: number): number[] | null {
    const cost = new Map<number, number>([[start, 0]]);
    const via = new Map<number, { road: number; from: number }>();
    const heap = new MinHeap();
    const ids = new Map<number, number>();
    const key = (n: number) => {
      if (!ids.has(n)) ids.set(n, ids.size);
      return ids.get(n)!;
    };
    const back: number[] = [];
    key(start);
    back[0] = start;
    heap.push(0, 0);
    while (heap.size) {
      const node = back[heap.pop()];
      if (node === goal) break;
      for (const i of this.byNode.get(node) ?? []) {
        const q = this.roads[i];
        const other = q.from === node ? q.to : q.from;
        const c = cost.get(node)! + this.lengths[i];
        if (c < (cost.get(other) ?? Infinity)) {
          cost.set(other, c);
          via.set(other, { road: i, from: node });
          const id = key(other);
          back[id] = other;
          heap.push(id, c);
        }
      }
    }
    if (!via.has(goal)) return null;
    const out: number[] = [];
    for (let n = goal; n !== start; n = via.get(n)!.from) out.push(via.get(n)!.road);
    return out.reverse();
  }

  private nodeAt(node: number): Vec2 {
    const i = this.byNode.get(node)?.[0] ?? 0;
    const r = this.roads[i];
    return r.from === node ? r.points[0] : r.points[r.points.length - 1];
  }

  private tangent(road: number, s: number): Vec2 {
    const r = this.roads[road];
    const len = this.lengths[road];
    const a = pointAt(r.points, this.at[road], Math.max(0, s - 0.15));
    const b = pointAt(r.points, this.at[road], Math.min(len, s + 0.15));
    const l = Math.hypot(b.x - a.x, b.y - a.y) || 1;
    return { x: (b.x - a.x) / l, y: (b.y - a.y) / l };
  }
}

function pointAt(points: readonly Vec2[], at: readonly number[], s: number): Vec2 {
  let k = 1;
  while (k < at.length - 1 && at[k] < s) k++;
  const a = points[k - 1];
  const b = points[k];
  const f = Math.max(0, Math.min(1, (s - at[k - 1]) / Math.max(1e-9, at[k] - at[k - 1])));
  return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f };
}

/** True if a car at p (pointing along `heading`) keeps off every road's asphalt, with its middle on a pavement or beyond. */
function clearOfRoads(p: Vec2, heading: number, roads: readonly RoadStroke[], sidewalk: number): boolean {
  const f = { x: Math.cos(heading), y: Math.sin(heading) };
  const ends = [p, { x: p.x + f.x * CAR_HALF_LENGTH, y: p.y + f.y * CAR_HALF_LENGTH }, { x: p.x - f.x * CAR_HALF_LENGTH, y: p.y - f.y * CAR_HALF_LENGTH }];
  return roads.every((r) => {
    if (r.kind === 'path') return true;
    const kerb = r.width / 2;
    return distToPolyline(p, r.points) > kerb + sidewalk / 2 - 0.03 && ends.every((e) => distToPolyline(e, r.points) > kerb + 0.1);
  });
}

/**
 * A spot to leave a car for the player near `near` (where they first stand):
 * up on the pavement beside the kerb of the nearest road, pointing along it,
 * clear of the traffic lane, of other parked cars, and of the spot itself.
 */
export function carSpotNear(
  near: Vec2,
  roads: readonly RoadStroke[],
  sidewalk: number,
  open: (x: number, y: number) => boolean,
  taken: readonly Vec2[],
): { x: number; y: number; heading: number } | null {
  let best: { x: number; y: number; heading: number } | null = null;
  let bestD = Infinity;
  for (const r of roads) {
    if (r.kind === 'path') continue;
    const off = r.width / 2 + sidewalk / 2;
    for (let k = 1; k < r.points.length; k++) {
      const a = r.points[k - 1];
      const b = r.points[k];
      const len = dist(a, b);
      if (len < 1e-6) continue;
      const tx = (b.x - a.x) / len;
      const ty = (b.y - a.y) / len;
      for (let t = 0; t <= len; t += 0.25) {
        for (const side of [1, -1]) {
          // Parked facing the way the traffic on its side of the road goes.
          const p = { x: a.x + tx * t - ty * off * side, y: a.y + ty * t + tx * off * side };
          const d = dist(p, near);
          if (d >= bestD || d < 0.75) continue;
          const heading = Math.atan2(ty * side, tx * side);
          if (!fits(p.x, p.y, heading, open) || taken.some((q) => dist(q, p) < 1.3)) continue;
          if (!clearOfRoads(p, heading, roads, sidewalk)) continue;
          bestD = d;
          best = { ...p, heading };
        }
      }
    }
  }
  return best;
}

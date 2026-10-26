// Moving traffic on every through road: cars keep to the right-hand lane,
// follow the road graph through junctions (U-turning at the map edge) and slow
// down behind the car ahead. They also stop for whatever's in their lane (you,
// or a car you've left there), and turn round if it doesn't move. A car can be
// taken out of traffic altogether when the player gets in. Dead-end side
// streets keep their parked cars. Pure state; the view draws each car's model
// at its pose.

import { dist } from '../../core/geometry';
import type { Rng } from '../../core/rng';
import type { Vec2 } from '../../model/types';
import type { CarPose } from './driving';
import { deadEnds, type IsoLayout, type RoadStroke } from './layout';
import { shortestTurn } from './orbit';

const CRUISE = 2.4; // tiles per second
const GAP = 1.6; // tiles kept behind the car ahead
const CARS_PER_TILE = 1 / 12;
const MAX_CARS = 70;
/** Stop this far (tiles, middle of the car to it) short of something in the lane. */
const OBSTACLE_GAP = 0.9;
/** How far either side of a car's line something counts as in the way (tiles). */
const LANE_HALF = 0.35;
/** Held up this long (seconds) by something that isn't traffic, turn round. */
const PATIENCE = 4;

export { type CarPose } from './driving';

interface Car extends CarPose {
  road: number;
  s: number;
  dir: 1 | -1;
  speed: number;
  /** Which side of the road it's on, easing from -1 to 1 after a U-turn (follows `dir`). */
  side: number;
  /** Seconds spent stopped for an obstacle. */
  wait: number;
  /** Taken out of traffic (the player got in): no longer moved or drawn. */
  gone: boolean;
}

export class Traffic {
  readonly cars: Car[] = [];
  private readonly roads: RoadStroke[];
  private readonly lengths: number[];
  private readonly at: number[][];
  /** Through roads meeting at each graph node. */
  private readonly byNode = new Map<number, number[]>();

  constructor(
    layout: IsoLayout,
    private readonly rng: Rng,
    pickModel: (rng: Rng) => number,
  ) {
    this.roads = layout.roads;
    this.at = this.roads.map((r) => {
      const at = [0];
      for (let k = 1; k < r.points.length; k++) at.push(at[k - 1] + dist(r.points[k], r.points[k - 1]));
      return at;
    });
    this.lengths = this.at.map((a) => a[a.length - 1]);
    // Every through road carries traffic; dead-end side streets are left for parked cars.
    const dead = deadEnds(this.roads);
    const mains: number[] = [];
    this.roads.forEach((r, i) => {
      if (r.kind === 'path' || this.lengths[i] < 1 || (r.kind === 'street' && dead.has(i))) return;
      mains.push(i);
      for (const n of [r.from, r.to]) this.byNode.set(n, [...(this.byNode.get(n) ?? []), i]);
    });
    const total = mains.reduce((s, i) => s + this.lengths[i], 0);
    const count = Math.min(MAX_CARS, Math.floor(total * CARS_PER_TILE));
    for (let k = 0; k < count && mains.length; k++) {
      const road = rng.pick(mains);
      const car: Car = { model: pickModel(rng), road, s: rng.range(0, this.lengths[road]), dir: rng.chance(0.5) ? 1 : -1, speed: CRUISE, heading: 0, x: 0, y: 0, side: 0, wait: 0, gone: false };
      car.side = car.dir;
      this.cars.push(car);
    }
    this.update(0);
  }

  /** Where each car is (world tiles), for dev tooling. */
  positions(): { x: number; y: number; heading: number }[] {
    return this.cars.filter((c) => !c.gone).map((c) => ({ x: c.x, y: c.y, heading: c.heading }));
  }

  /** Take a car out of traffic (the player is getting in) and return where it was. */
  take(i: number): CarPose {
    const c = this.cars[i];
    c.gone = true;
    return { model: c.model, x: c.x, y: c.y, heading: c.heading };
  }

  /**
   * Move every car on. `obstacles` are points cars must stop short of (the
   * player, and the body of any car out of traffic).
   */
  update(dt: number, obstacles: readonly Vec2[] = []): void {
    for (const car of this.cars) {
      if (car.gone) continue;
      // Ease towards cruising speed, or towards a stop behind the car ahead.
      const ahead = this.gapAhead(car);
      const blocked = this.obstacleAhead(car, obstacles);
      const target = Math.min(ease(ahead, GAP), ease(blocked, OBSTACLE_GAP));
      car.speed += (target - car.speed) * Math.min(1, dt * (target < car.speed ? 6 : 3));
      car.wait = blocked < OBSTACLE_GAP * 1.5 && car.speed < 0.2 ? car.wait + dt : 0;
      if (car.wait > PATIENCE) {
        car.dir = (car.dir * -1) as 1 | -1;
        car.wait = 0;
      }
      car.side += Math.sign(car.dir - car.side) * Math.min(Math.abs(car.dir - car.side), dt * 2);
      car.s += car.dir * car.speed * dt;
      if (car.s < 0 || car.s > this.lengths[car.road]) this.turnAtJunction(car);
      this.place(car, dt);
    }
  }

  /** How far ahead (tiles) the nearest obstacle in this car's lane is. */
  private obstacleAhead(car: Car, obstacles: readonly Vec2[]): number {
    const hx = Math.cos(car.heading);
    const hy = Math.sin(car.heading);
    let best = Infinity;
    for (const o of obstacles) {
      const dx = o.x - car.x;
      const dy = o.y - car.y;
      const along = dx * hx + dy * hy;
      if (along > 0 && along < best && Math.abs(dy * hx - dx * hy) < LANE_HALF) best = along;
    }
    return best;
  }

  private gapAhead(car: Car): number {
    let best = Infinity;
    for (const o of this.cars) {
      if (o === car || o.gone || o.road !== car.road || o.dir !== car.dir) continue;
      const d = (o.s - car.s) * car.dir;
      if (d > 0 && d < best) best = d;
    }
    return best;
  }

  private turnAtJunction(car: Car): void {
    const r = this.roads[car.road];
    const node = car.s > this.lengths[car.road] ? r.to : r.from;
    const options = (this.byNode.get(node) ?? []).filter((i) => i !== car.road);
    if (!options.length) {
      // Dead end (or the map edge): turn round.
      car.s = Math.min(this.lengths[car.road], Math.max(0, car.s));
      car.dir = (car.dir * -1) as 1 | -1;
      return;
    }
    const next = this.rng.pick(options);
    const nr = this.roads[next];
    car.road = next;
    car.dir = nr.from === node ? 1 : -1;
    car.side = car.dir;
    car.s = car.dir === 1 ? 0 : this.lengths[next];
  }

  private place(car: Car, dt = 0): void {
    const r = this.roads[car.road];
    const p = pointAt(r.points, this.at[car.road], car.s);
    const q = pointAt(r.points, this.at[car.road], Math.max(0, Math.min(this.lengths[car.road], car.s + 0.25 * car.dir)));
    let tx = q.x - p.x;
    let ty = q.y - p.y;
    const l = Math.hypot(tx, ty);
    if (l > 1e-6) {
      tx /= l;
      ty /= l;
      // Turn smoothly (a U-turn swings round rather than flipping on the spot).
      const want = Math.atan2(ty, tx);
      car.heading += dt ? shortestTurn(car.heading, want) * Math.min(1, dt * 4) : shortestTurn(car.heading, want);
    } else {
      tx = Math.cos(car.heading);
      ty = Math.sin(car.heading);
    }
    // Keep right: the lane centre is a quarter of the road's width to the right
    // of travel, i.e. `side` quarters to the right of the road's own direction.
    const lane = (r.width / 4) * car.side;
    const ux = tx * car.dir;
    const uy = ty * car.dir;
    car.x = p.x - uy * lane;
    car.y = p.y + ux * lane;
  }
}

/** Target speed with something `gap` tiles ahead: stop at `stop`, full speed from twice that. */
function ease(gap: number, stop: number): number {
  return gap < stop ? 0 : gap < stop * 2 ? CRUISE * ((gap - stop) / stop) : CRUISE;
}

function pointAt(points: readonly Vec2[], at: readonly number[], s: number): Vec2 {
  let k = 1;
  while (k < at.length - 1 && at[k] < s) k++;
  const a = points[k - 1];
  const b = points[k];
  const f = Math.max(0, Math.min(1, (s - at[k - 1]) / Math.max(1e-9, at[k] - at[k - 1])));
  return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f };
}

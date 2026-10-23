// Moving traffic on every through road: cars keep to the right-hand lane,
// follow the road graph through junctions (U-turning at the map edge) and slow
// down behind the car ahead. Dead-end side streets keep their parked cars.

import { Sprite } from 'pixi.js';
import { dist } from '../../core/geometry';
import type { Rng } from '../../core/rng';
import type { Vec2 } from '../../model/types';
import { depthOf, toScreen } from './coords';
import { deadEnds, type IsoLayout, type RoadStroke } from './layout';
import { CAR_COLOURS, CAR_TEX, carTexture } from './textures';

const CRUISE = 2.4; // tiles per second
const GAP = 1.3; // tiles kept behind the car ahead
const CARS_PER_TILE = 1 / 12;
const MAX_CARS = 70;

interface Car {
  sprite: Sprite;
  colour: string;
  road: number;
  s: number;
  dir: 1 | -1;
  speed: number;
  heading: number;
}

export class Traffic {
  private readonly cars: Car[] = [];
  private readonly roads: RoadStroke[];
  private readonly lengths: number[];
  private readonly at: number[][];
  /** Main roads meeting at each graph node. */
  private readonly byNode = new Map<number, number[]>();

  constructor(
    layout: IsoLayout,
    add: (s: Sprite) => void,
    private readonly rng: Rng,
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
      const colour = rng.pick(CAR_COLOURS);
      const sprite = new Sprite(carTexture(colour, 0));
      sprite.anchor.set(CAR_TEX.cx / CAR_TEX.w, CAR_TEX.cy / CAR_TEX.h);
      add(sprite);
      this.cars.push({ sprite, colour, road, s: rng.range(0, this.lengths[road]), dir: rng.chance(0.5) ? 1 : -1, speed: CRUISE, heading: 0 });
    }
    this.update(0);
  }

  /** Where each car is on screen (for dev tooling). */
  positions(): { x: number; y: number; heading: number }[] {
    return this.cars.map((c) => ({ x: c.sprite.x, y: c.sprite.y, heading: c.heading }));
  }

  update(dt: number): void {
    for (const car of this.cars) {
      // Ease towards cruising speed, or towards a stop behind the car ahead.
      const ahead = this.gapAhead(car);
      const target = ahead < GAP ? 0 : ahead < GAP * 2 ? CRUISE * ((ahead - GAP) / GAP) : CRUISE;
      car.speed += (target - car.speed) * Math.min(1, dt * 3);
      car.s += car.dir * car.speed * dt;
      if (car.s < 0 || car.s > this.lengths[car.road]) this.turnAtJunction(car);
      this.place(car);
    }
  }

  private gapAhead(car: Car): number {
    let best = Infinity;
    for (const o of this.cars) {
      if (o === car || o.road !== car.road || o.dir !== car.dir) continue;
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
    car.s = car.dir === 1 ? 0 : this.lengths[next];
  }

  private place(car: Car): void {
    const r = this.roads[car.road];
    const p = pointAt(r.points, this.at[car.road], car.s);
    const q = pointAt(r.points, this.at[car.road], Math.max(0, Math.min(this.lengths[car.road], car.s + 0.25 * car.dir)));
    let tx = q.x - p.x;
    let ty = q.y - p.y;
    const l = Math.hypot(tx, ty);
    if (l > 1e-6) {
      tx /= l;
      ty /= l;
      car.heading = Math.atan2(ty, tx);
    } else {
      tx = Math.cos(car.heading);
      ty = Math.sin(car.heading);
    }
    // Keep right: the lane centre is a quarter of the road's width to the right of travel.
    const lane = r.width / 4;
    const x = p.x - ty * lane;
    const y = p.y + tx * lane;
    const screen = toScreen(x, y);
    car.sprite.position.set(screen.x, screen.y);
    car.sprite.texture = carTexture(car.colour, car.heading);
    car.sprite.zIndex = depthOf(x, y, 6);
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

// Cars the player can get into and drive: W / S for throttle, brake and
// reverse, A / D to steer. Pure state (pose, speed, steering); the view checks
// where a car may go and draws its model at the pose. Unit-tested.

import type { Vec2 } from '../../model/types';

export interface CarPose {
  /** Index into the car model list. */
  model: number;
  x: number;
  y: number;
  /** Direction of travel, radians in world space (atan2(dy, dx)). */
  heading: number;
}

/** A car out of traffic: driven by the player, or left wherever they got out. */
export interface Motor extends CarPose {
  /** Tiles per second along the heading (negative when reversing). */
  speed: number;
  /** Front wheels, -1 (full left) to 1 (full right); eases toward the keys. */
  steer: number;
}

/** A car's body as three circles along its length (tiles), for collisions. */
export const CAR_RADIUS = 0.24;
const CIRCLE_SPACING = 0.3;

const TOP_SPEED = 5.5; // tiles per second
const REVERSE_SPEED = 2;
const ACCELERATION = 3.5;
const BRAKING = 9;
/** Slowing down with no pedal pressed (tiles per second per second). */
const COASTING = 1.6;
/** Front to rear axle (tiles), and how far the front wheels turn at a crawl (radians). */
const WHEELBASE = 0.65;
const MAX_LOCK = 0.6;
/** The lock shrinks with speed, so the car stays steerable at full tilt. */
const LOCK_FADE = 0.35;
const STEER_RATE = 6;
/** Bouncing off something keeps this much of the speed, backwards. */
const BOUNCE = 0.2;

/** Slow enough to get out (tiles per second). */
export const STOPPED = 0.8;

/** Where the car's three body circles are. */
export function carCircles(c: { x: number; y: number; heading: number }): Vec2[] {
  const dx = Math.cos(c.heading) * CIRCLE_SPACING;
  const dy = Math.sin(c.heading) * CIRCLE_SPACING;
  return [
    { x: c.x - dx, y: c.y - dy },
    { x: c.x, y: c.y },
    { x: c.x + dx, y: c.y + dy },
  ];
}

/** True if two cars' bodies overlap. */
export function carsTouch(a: { x: number; y: number; heading: number }, b: { x: number; y: number; heading: number }): boolean {
  if (Math.hypot(a.x - b.x, a.y - b.y) > CIRCLE_SPACING * 2 + CAR_RADIUS * 2) return false;
  const bs = carCircles(b);
  return carCircles(a).some((p) => bs.some((q) => Math.hypot(p.x - q.x, p.y - q.y) < CAR_RADIUS * 2));
}

/** The kerbside spot just out of the driver's door (left of travel), `out` tiles from the car's middle. */
export function besideCar(c: { x: number; y: number; heading: number }, side: 1 | -1, out: number): Vec2 {
  // Right of travel is (-sin, cos) on the map (y runs down), so left is the opposite.
  return { x: c.x + Math.sin(c.heading) * out * side, y: c.y - Math.cos(c.heading) * out * side };
}

/**
 * One step of driving. `throttle` is 1 (W), -1 (S) or 0; `steer` is -1 (A),
 * 1 (D) or 0. `clear` says whether the car's body fits at a pose; if the move
 * would hit something the car stays put and bounces back a little.
 */
export function drive(car: Motor, throttle: number, steer: number, dt: number, clear: (pose: { x: number; y: number; heading: number }) => boolean): void {
  if (throttle > 0) car.speed += (car.speed < 0 ? BRAKING : ACCELERATION) * throttle * dt;
  else if (throttle < 0) car.speed += (car.speed > 0 ? BRAKING : ACCELERATION) * throttle * dt;
  else car.speed -= Math.sign(car.speed) * Math.min(Math.abs(car.speed), COASTING * dt);
  car.speed = Math.min(TOP_SPEED, Math.max(-REVERSE_SPEED, car.speed));
  car.steer += (steer - car.steer) * Math.min(1, dt * STEER_RATE);

  const lock = (car.steer * MAX_LOCK) / (1 + Math.abs(car.speed) * LOCK_FADE);
  const heading = car.heading + (car.speed / WHEELBASE) * Math.tan(lock) * dt;
  const x = car.x + Math.cos(heading) * car.speed * dt;
  const y = car.y + Math.sin(heading) * car.speed * dt;
  // Stuck somewhere it shouldn't be (e.g. got into a car parked tight): let it drive out.
  const free = !clear(car);
  if (free || clear({ x, y, heading })) {
    car.x = x;
    car.y = y;
    car.heading = heading;
  } else if (clear({ x: car.x, y: car.y, heading })) {
    car.heading = heading;
    car.speed *= -BOUNCE;
  } else {
    car.speed *= -BOUNCE;
  }
}

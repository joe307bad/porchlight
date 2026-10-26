import { describe, expect, it } from 'vitest';
import { createRng } from '../src/core/rng';
import { generateTown } from '../src/gen';
import { STOPPED, besideCar, carCircles, carsTouch, drive, type Motor } from '../src/render/iso/driving';
import { buildLayout } from '../src/render/iso/layout';
import { Traffic } from '../src/render/iso/traffic';

const car = (over: Partial<Motor> = {}): Motor => ({ model: 0, x: 10, y: 10, heading: 0, speed: 0, steer: 0, ...over });
const open = (_pose: { x: number; y: number; heading: number }) => true;
const run = (c: Motor, throttle: number, steer: number, seconds: number, clear = open) => {
  for (let t = 0; t < seconds; t += 0.02) drive(c, throttle, steer, 0.02, clear);
};

describe('driving', () => {
  it('pulls away on the throttle and coasts to a stop', () => {
    const c = car();
    run(c, 1, 0, 2);
    expect(c.speed).toBeGreaterThan(3);
    expect(c.x).toBeGreaterThan(12);
    expect(Math.abs(c.y - 10)).toBeLessThan(1e-9);
    run(c, 0, 0, 10);
    expect(c.speed).toBe(0);
  });

  it('brakes, then reverses, on S', () => {
    const c = car({ speed: 4 });
    run(c, -1, 0, 0.3);
    expect(c.speed).toBeLessThan(2);
    run(c, -1, 0, 3);
    expect(c.speed).toBeLessThan(0);
    const x = c.x;
    run(c, -1, 0, 0.5);
    expect(c.x).toBeLessThan(x);
  });

  it('steers right on D (towards +y when heading +x, as the map runs down)', () => {
    const c = car({ speed: 3 });
    run(c, 1, 1, 0.6);
    expect(c.heading).toBeGreaterThan(0.2);
    expect(c.y).toBeGreaterThan(10);
  });

  it('does not turn standing still', () => {
    const c = car();
    run(c, 0, 1, 1);
    expect(c.heading).toBe(0);
  });

  it('stops dead at something in the way, bouncing back a little', () => {
    const c = car({ speed: 4 });
    const wall = (p: { x: number }) => p.x < 10.5;
    run(c, 1, 0, 1, wall);
    expect(c.x).toBeLessThan(10.5);
    expect(Math.abs(c.speed)).toBeLessThan(STOPPED);
  });

  it('lets a car stuck in something drive out of it', () => {
    const c = car();
    const stuckHere = (p: { x: number }) => p.x > 10.01;
    run(c, 1, 0, 1, stuckHere);
    expect(c.x).toBeGreaterThan(10.5);
  });

  it('gets out on the left of the way the car faces', () => {
    // Facing +x on a map whose y runs down, left is -y.
    const at = besideCar({ x: 0, y: 0, heading: 0 }, 1, 0.5);
    expect(at.x).toBeCloseTo(0);
    expect(at.y).toBeCloseTo(-0.5);
  });

  it('knows when two cars touch', () => {
    const a = { x: 0, y: 0, heading: 0 };
    expect(carsTouch(a, { x: 0.9, y: 0, heading: 0 })).toBe(true);
    expect(carsTouch(a, { x: 0, y: 0.6, heading: 0 })).toBe(false);
    expect(carsTouch(a, { x: 0, y: 0.6, heading: Math.PI / 2 })).toBe(true);
    expect(carCircles(a)).toHaveLength(3);
  });
});

describe('traffic and the player', () => {
  const layout = buildLayout(generateTown('driving'));
  const traffic = () => new Traffic(layout, createRng('t'), () => 0);

  it('takes a car out of traffic: it stops moving and is no longer listed', () => {
    const t = traffic();
    expect(t.cars.length).toBeGreaterThan(0);
    const n = t.positions().length;
    const pose = t.take(0);
    const { x, y } = t.cars[0];
    expect(pose.x).toBe(x);
    for (let k = 0; k < 50; k++) t.update(0.05);
    expect(t.cars[0].x).toBe(x);
    expect(t.cars[0].y).toBe(y);
    expect(t.positions()).toHaveLength(n - 1);
  });

  it('stops short of something standing in its lane, then turns round', () => {
    const t = traffic();
    // The car with the longest way to go before its next junction.
    const inner = t as unknown as { lengths: number[] };
    type Lane = { road: number; s: number; dir: number };
    const left = (q: Lane) => (q.dir > 0 ? inner.lengths[q.road] - q.s : q.s);
    const c = [...t.cars].sort((p, q) => left(q as unknown as Lane) - left(p as unknown as Lane))[0];
    // Something just ahead of it, in its lane; held there.
    const block = () => [{ x: c.x + Math.cos(c.heading) * 1.2, y: c.y + Math.sin(c.heading) * 1.2 }];
    const ahead = block();
    for (let k = 0; k < 40; k++) t.update(0.05, ahead);
    expect(c.speed).toBeLessThan(0.2);
    expect(Math.hypot(ahead[0].x - c.x, ahead[0].y - c.y)).toBeGreaterThan(0.7);
    const lane = c as unknown as { dir: number };
    const dir = lane.dir;
    let turned = false;
    for (let k = 0; k < 100 && !turned; k++) {
      t.update(0.05, ahead);
      turned = lane.dir === -dir;
    }
    expect(turned).toBe(true);
  });
});

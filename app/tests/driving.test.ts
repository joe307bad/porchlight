// Driving a car: handling, keeping off buildings and water, getting out, and
// the traffic giving up a car and stopping for the player.

import { describe, expect, it } from 'vitest';
import { index } from '../src/core/grid';
import { createRng } from '../src/core/rng';
import { generateTown } from '../src/gen';
import { Overlay, Terrain } from '../src/model/types';
import { AUTO_DRIVE_SPEED, AutoDrive, TOP_SPEED, Vehicle, carSpotNear, drivable, exitSpot, fits } from '../src/render/iso/driving';
import { distToPolyline } from '../src/core/geometry';
import { SIDEWALK, Use, buildLayout } from '../src/render/iso/layout';
import { testSeeds } from './seeds';
import { drivingDirections } from '../src/render/iso/directions';
import { nameStreets } from '../src/render/iso/names';
import { Traffic } from '../src/render/iso/traffic';

const open = () => true;

describe('driving', () => {
  it('accelerates up to top speed, coasts to a stop and reverses', () => {
    const car = new Vehicle(0, 50, 50, 0);
    for (let k = 0; k < 200; k++) car.drive(1, 0, 0.05, open);
    expect(car.speed).toBeCloseTo(TOP_SPEED);
    expect(car.x).toBeGreaterThan(70);
    expect(car.y).toBeCloseTo(50);
    for (let k = 0; k < 200; k++) car.drive(0, 0, 0.05, open);
    expect(car.speed).toBe(0);
    for (let k = 0; k < 40; k++) car.drive(-1, 0, 0.05, open);
    expect(car.speed).toBeLessThan(0);
  });

  it('steers right clockwise on the map (y runs south), harder once moving', () => {
    const still = new Vehicle(0, 50, 50, 0);
    still.drive(0, 1, 0.5, open);
    expect(still.heading).toBe(0); // no turning on the spot
    const car = new Vehicle(0, 50, 50, 0);
    for (let k = 0; k < 20; k++) car.drive(1, 1, 0.05, open);
    expect(car.heading).toBeGreaterThan(0.3);
    expect(car.y).toBeGreaterThan(50); // heading east, turning right swings it south
  });

  it('stops dead against what it can not drive through', () => {
    const wall = (x: number) => x < 55;
    const car = new Vehicle(0, 50, 50, 0);
    for (let k = 0; k < 200; k++) car.drive(1, 0, 0.05, (x) => wall(x));
    expect(car.x).toBeLessThan(55);
    expect(car.speed).toBe(0);
    expect(fits(car.x, car.y, car.heading, (x) => wall(x))).toBe(true);
  });

  it('keeps off buildings, sheds and open water, but drives the roads and bridges', () => {
    const m = generateTown('walking');
    const layout = buildLayout(m);
    const can = drivable(m, layout);
    let checked = 0;
    for (let y = 0; y < m.height; y++) {
      for (let x = 0; x < m.width; x++) {
        const i = index(x, y, m.width);
        const wet = m.tiles.terrain[i] === Terrain.WATER_DEEP || m.tiles.terrain[i] === Terrain.WATER_SHALLOW;
        const over = m.tiles.overlay[i];
        const ok = can(x + 0.5, y + 0.5);
        if (layout.use[i] === Use.HOUSE || layout.use[i] === Use.SHED) expect(ok).toBe(false);
        if (wet && over === Overlay.NONE) expect(ok).toBe(false);
        if (over === Overlay.ROAD_MAIN || over === Overlay.BRIDGE) {
          expect(ok).toBe(true);
          checked++;
        }
      }
    }
    expect(checked).toBeGreaterThan(50);
    expect(can(-1, 5)).toBe(false);
  });

  it('lets the driver out beside the car, on the driver\'s side when there is room', () => {
    const car = new Vehicle(0, 50, 50, 0); // facing east: the driver's (left) side is north
    const spot = exitSpot(car, open)!;
    expect(spot.y).toBeLessThan(50);
    const southOnly = exitSpot(car, (_, y) => y > 50)!;
    expect(southOnly.y).toBeGreaterThan(50);
    expect(exitSpot(car, () => false)).toBeNull();
  });
});

describe('traffic and the player', () => {
  const m = generateTown('walking');
  const layout = buildLayout(m);

  it('gives up a car the player gets into, and stops drawing or moving it', () => {
    const traffic = new Traffic(layout, createRng('t'), () => 0);
    const before = traffic.positions().length;
    const c = traffic.cars[0];
    const i = traffic.nearest({ x: c.x, y: c.y }, 0.5);
    expect(i).toBe(0);
    const pose = traffic.take(i);
    expect(pose.x).toBe(c.x);
    traffic.update(0.5);
    expect(traffic.cars[0].x).toBe(pose.x); // left where it was
    expect(traffic.positions()).toHaveLength(before - 1);
    expect(traffic.nearest({ x: c.x, y: c.y }, 0.5)).not.toBe(0);
  });

  it('stops for something in its lane ahead', () => {
    const traffic = new Traffic(layout, createRng('u'), () => 0);
    const c = traffic.cars[0];
    // Let it get going, then stand in the road just ahead of it.
    for (let k = 0; k < 20; k++) traffic.update(0.05);
    const ahead = () => ({ x: c.x + Math.cos(c.heading) * 1.2, y: c.y + Math.sin(c.heading) * 1.2 });
    for (let k = 0; k < 60; k++) traffic.update(0.05, [ahead()]);
    expect(c.speed).toBeLessThan(0.05);
  });
});

describe('auto drive', () => {
  const m = generateTown('walking');
  const layout = buildLayout(m);
  const roads = layout.roads.filter((r) => r.kind !== 'path');
  const onRoad = (p: { x: number; y: number }) => Math.min(...roads.map((r) => distToPolyline(p, r.points) - r.width / 2));
  const start = () => {
    const r = roads.find((q) => q.kind === 'main')!;
    const a = r.points[0];
    const b = r.points[1];
    return new Vehicle(0, a.x, a.y, Math.atan2(b.y - a.y, b.x - a.x));
  };

  it('drives itself round town at a brisk pace, keeping to the roads', () => {
    const car = start();
    const drive = new AutoDrive(layout.roads, createRng('drive'), car);
    let fast = 0;
    let travelled = 0;
    let off = 0;
    for (let k = 0; k < 1200; k++) {
      const was = { x: car.x, y: car.y };
      drive.step(car, 0.05);
      travelled += Math.hypot(car.x - was.x, car.y - was.y);
      if (car.speed > AUTO_DRIVE_SPEED * 0.9) fast++;
      if (k > 40 && onRoad(car) > 0.05) off++;
    }
    expect(travelled).toBeGreaterThan(60 * AUTO_DRIVE_SPEED * 0.6); // a minute of mostly brisk driving
    expect(fast).toBeGreaterThan(400);
    expect(off).toBe(0);
  });

  it('keeps to the right-hand lane', () => {
    const car = start();
    const drive = new AutoDrive(layout.roads, createRng('lane'), car);
    let right = 0;
    let checked = 0;
    for (let k = 0; k < 600; k++) {
      drive.step(car, 0.05);
      if (k < 40 || k % 10) continue;
      // The road's centreline should lie to the car's left.
      const f = car.forward;
      const left = { x: car.x + f.y * 0.3, y: car.y - f.x * 0.3 };
      const nearest = Math.min(...roads.map((r) => distToPolyline(car, r.points)));
      const leftNearest = Math.min(...roads.map((r) => distToPolyline(left, r.points)));
      checked++;
      if (leftNearest < nearest) right++;
    }
    expect(right / checked).toBeGreaterThan(0.8);
  });

  it('slows to a stop behind a car ahead in its lane', () => {
    const car = start();
    const drive = new AutoDrive(layout.roads, createRng('follow'), car);
    for (let k = 0; k < 30; k++) drive.step(car, 0.05);
    expect(car.speed).toBeGreaterThan(1);
    for (let k = 0; k < 40; k++) {
      const f = car.forward;
      drive.step(car, 0.05, [{ x: car.x + f.x * 1.2, y: car.y + f.y * 1.2 }]);
    }
    expect(car.speed).toBeLessThan(0.05);
  });

  it('shows where it is going: a route ahead to a far-off junction', () => {
    const car = start();
    const drive = new AutoDrive(layout.roads, createRng('route'), car);
    const route = drive.routePoints();
    const goal = drive.goalPoint()!;
    expect(route.length).toBeGreaterThan(2);
    const end = route[route.length - 1];
    expect(Math.hypot(end.x - goal.x, end.y - goal.y)).toBeLessThan(1e-6);
    expect(Math.hypot(goal.x - car.x, goal.y - car.y)).toBeGreaterThan(5);
  });
});

describe('a car by the spawn point', () => {
  it.each(testSeeds(12))('leaves one on the pavement right by where you start, clear of the road (%s)', (seed) => {
    const m = generateTown(seed);
    const layout = buildLayout(m);
    const can = drivable(m, layout);
    const parked = layout.cars;
    const spot = carSpotNear(m.spawn, layout.roads, SIDEWALK, can, parked);
    expect(spot).not.toBeNull();
    const d = Math.hypot(spot!.x - m.spawn.x, spot!.y - m.spawn.y);
    expect(d).toBeGreaterThanOrEqual(0.75);
    expect(d).toBeLessThan(2.5);
    expect(fits(spot!.x, spot!.y, spot!.heading, can)).toBe(true);
    for (const c of parked) expect(Math.hypot(c.x - spot!.x, c.y - spot!.y)).toBeGreaterThan(1.3);
    // Out of the traffic lanes: its middle is on the pavement, past the kerb.
    for (const r of layout.roads) if (r.kind !== 'path') expect(distToPolyline(spot!, r.points)).toBeGreaterThan(r.width / 2 + 0.1);
  });
});

describe('auto drive directions', () => {
  const m = generateTown('walking');
  const layout = buildLayout(m);
  const names = nameStreets(m, layout.roads);
  const main = layout.roads.find((q) => q.kind === 'main')!;
  const car = () => new Vehicle(0, main.points[0].x, main.points[0].y, Math.atan2(main.points[1].y - main.points[0].y, main.points[1].x - main.points[0].x));

  it('reads the route out street by street, from the start to the arrival', () => {
    for (const seed of ['a', 'b', 'c', 'd']) {
      const c = car();
      const drive = new AutoDrive(layout.roads, createRng(seed), c);
      for (let k = 0; k < 10; k++) drive.step(c, 0.05);
      const steps = drivingDirections(names, drive.stretches());
      expect(steps[0].kind).toBe('start');
      expect(steps[0].text).toMatch(/^Head [a-z-]+ on /);
      expect(steps[steps.length - 1].kind).toBe('arrive');
      expect(steps.length).toBeGreaterThan(2);
      for (const st of steps) expect(st.text).toContain(names.streets[st.street].name);
      // A new step only where the street changes (or you turn round).
      for (let k = 1; k < steps.length - 1; k++) {
        if (steps[k].kind !== 'uturn') expect(steps[k].street).not.toBe(steps[k - 1].street);
      }
    }
  });

  it('adds up to the route still to drive, and shrinks as you drive it', () => {
    const c = car();
    const drive = new AutoDrive(layout.roads, createRng('e'), c);
    drive.step(c, 0.05);
    const total = () => drivingDirections(names, drive.stretches()).reduce((sum, st) => sum + st.distance, 0);
    const pts = drive.routePoints();
    let route = 0;
    for (let k = 1; k < pts.length; k++) route += Math.hypot(pts[k].x - pts[k - 1].x, pts[k].y - pts[k - 1].y);
    expect(Math.abs(total() - route)).toBeLessThan(0.5);
    const before = total();
    for (let k = 0; k < 20; k++) drive.step(c, 0.05);
    expect(total()).toBeLessThan(before);
  });
});

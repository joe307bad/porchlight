// Runs the explore view's simulations (traffic, townsfolk, auto walk, auto
// drive, directions) for a few hundred fixed steps and writes where everyone
// ends up, for the C# port to compare against.
// Run: npx --prefix app vite-node "unity/Tools~/parity/sim.ts" -- out.json

import { writeFileSync } from 'node:fs';
import { generateTown } from '../../../app/src/gen';
import { normalizeParams } from '../../../app/src/model/params';
import { createRng } from '../../../app/src/core/rng';
import { buildLayout } from '../../../app/src/render/iso/layout';
import { nameStreets } from '../../../app/src/render/iso/names';
import { Traffic } from '../../../app/src/render/iso/traffic';
import { pickCarModel } from '../../../app/src/render/iso/fleet';
import { Pedestrian, Player } from '../../../app/src/render/iso/actors';
import { AutoDrive, Vehicle, carSpotNear, drivable } from '../../../app/src/render/iso/driving';
import { directions, drivingDirections } from '../../../app/src/render/iso/directions';
import { randomLook } from '../../../app/src/render/iso/characters';
import { streetMask } from '../../../app/src/render/iso/streets';

const out = process.argv.slice(2).filter((a) => a !== '--')[0] ?? 'sim.json';
const seeds = ['porchlight', 'k2x9q', 'tram42', 'mill'];
const results = seeds.map((seed) => {
  const m = generateTown(seed, normalizeParams({}));
  const layout = buildLayout(m);
  const names = nameStreets(m, layout.roads);
  const traffic = new Traffic(layout, createRng(`${seed}::traffic`), pickCarModel);
  const rng = createRng(`${seed}::townsfolk`);
  const people = Array.from({ length: 12 }, () => new Pedestrian(randomLook(rng), layout.sidewalks, rng));
  const autoRng = createRng('auto-walk');
  const player = new Player(randomLook(autoRng), m.spawn.x, m.spawn.y, 1.05);
  player.startAuto(layout.sidewalks, autoRng);
  const open = streetMask(m.tiles);
  const canDrive = drivable(m, layout);
  const spot = carSpotNear(m.spawn, layout.roads, 0.45, canDrive, []);
  const car = new Vehicle(1, spot!.x, spot!.y, spot!.heading);
  const driveRng = createRng('auto-drive');
  const auto = new AutoDrive(layout.roads, driveRng, car);
  const steps: unknown[] = [];
  for (let i = 0; i < 400; i++) {
    const dt = 0.05;
    traffic.update(dt, [{ x: player.x, y: player.y }]);
    for (const p of people) p.update(dt);
    player.update(null, dt, m, open);
    auto.step(car, dt, traffic.positions());
    if (i % 50 === 49) {
      const it = player.autoItinerary();
      steps.push({
        cars: traffic.positions(),
        people: people.map((p) => ({ x: p.x, y: p.y, heading: p.heading, speed: p.speed })),
        player: { x: player.x, y: player.y, heading: player.heading },
        route: player.autoRoute(),
        walk: it ? directions(layout.sidewalks, names, it).map((s) => [s.kind, s.text, s.distance]) : null,
        car: { x: car.x, y: car.y, heading: car.heading, speed: car.speed },
        drive: drivingDirections(names, auto.stretches()).map((s) => [s.kind, s.text, s.distance]),
        driveRoute: auto.routePoints(),
      });
    }
  }
  // Manual driving with a fixed input pattern.
  const manual = new Vehicle(0, spot!.x, spot!.y, spot!.heading);
  const manualPath: unknown[] = [];
  for (let i = 0; i < 300; i++) {
    manual.drive(i % 90 < 60 ? 1 : -1, Math.sin(i / 20), 0.05, canDrive, i % 120 < 40);
    if (i % 30 === 29) manualPath.push({ x: manual.x, y: manual.y, heading: manual.heading, speed: manual.speed });
  }
  return { seed, steps, manual: manualPath, spot };
});
writeFileSync(out, JSON.stringify(results));
console.log(`wrote ${results.length} simulations to ${out}`);

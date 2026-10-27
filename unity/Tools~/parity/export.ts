// Writes the TypeScript generator's towns for a list of seeds (and some
// non-default params) as JSON, for the C# port to compare against bit for bit.
// Run: npx --prefix app vite-node "unity/Tools~/parity/export.ts" -- out.json [count]

import { writeFileSync } from 'node:fs';
import { generateTown } from '../../../app/src/gen';
import { normalizeParams } from '../../../app/src/model/params';
import { createRng } from '../../../app/src/core/rng';
import { buildLayout } from '../../../app/src/render/iso/layout';
import { nameStreets } from '../../../app/src/render/iso/names';
import { planSigns } from '../../../app/src/render/iso/signs';

const args = process.argv.slice(2).filter((a) => a !== '--');
const out = args[0] ?? 'parity.json';
const count = Number(args[1] ?? 24);
const rng = createRng('unity-parity');
const cases: { seed: string; params: Record<string, number> }[] = [];
for (let i = 0; i < count; i++) {
  const seed = Math.floor(rng.next() * 2 ** 32).toString(36);
  const params: Record<string, number> = {};
  if (i % 4 === 1) params.width = params.height = 96;
  if (i % 4 === 2) { params.townRadius = 0.45; params.roadDensity = 1; params.riverCount = 3; }
  if (i % 4 === 3) { params.frontSetback = 2; params.waterFraction = 0.15; params.width = 160; }
  cases.push({ seed, params });
}
cases.push({ seed: 'porchlight', params: {} });

const f32 = (a: Float32Array) => Array.from(a);
const towns = cases.map(({ seed, params }) => {
  const m = generateTown(seed, normalizeParams(params));
  const layout = buildLayout(m);
  const names = nameStreets(m, layout.roads);
  const signs = planSigns(m, layout, names);
  return {
    seed,
    params: m.params,
    heightmap: f32(m.heightmap),
    waterLevel: m.waterLevel,
    center: m.center,
    townRadius: m.townRadius,
    gates: m.gates,
    rivers: m.rivers,
    roads: m.roads,
    bridges: m.bridges,
    blocks: m.blocks,
    lots: m.lots,
    buildings: m.buildings,
    landmarks: m.landmarks,
    terrain: Array.from(m.tiles.terrain),
    overlay: Array.from(m.tiles.overlay),
    buildingId: Array.from(m.tiles.buildingId),
    walkable: Array.from(m.tiles.walkable),
    spawn: m.spawn,
    flow: f32(m.fields.flow),
    roadCost: Array.from(m.fields.roadCost).map((v) => (Number.isFinite(v) ? v : -1)),
    layout: {
      houses: layout.houses,
      use: Array.from(layout.use),
      fences: layout.fences,
      cars: layout.cars,
      roads: layout.roads,
      bridges: layout.bridges,
      sidewalks: layout.sidewalks,
    },
    names,
    signs,
  };
});
writeFileSync(out, JSON.stringify(towns));
console.log(`wrote ${towns.length} towns to ${out}`);

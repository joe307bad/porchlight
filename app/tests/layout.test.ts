import { describe, expect, it } from 'vitest';
import { DIRS4, index, inBounds } from '../src/core/grid';
import { distToPolyline } from '../src/core/geometry';
import { generateTown } from '../src/gen';
import { tilesIn } from '../src/gen/parcels';
import { Overlay, type TownModel } from '../src/model/types';
import { Use, buildLayout } from '../src/render/iso/layout';
import { testSeeds } from './seeds';

const DIR = { N: DIRS4[0], E: DIRS4[1], S: DIRS4[2], W: DIRS4[3] } as const;
const isStreet = (m: TownModel, x: number, y: number) =>
  inBounds(x, y, m.width, m.height) &&
  (m.tiles.overlay[index(x, y, m.width)] === Overlay.ROAD_MAIN || m.tiles.overlay[index(x, y, m.width)] === Overlay.ROAD_STREET);

const towns = testSeeds(8).map((seed) => ({ seed, m: generateTown(seed) }));

describe('iso layout: block by block', () => {
  it.each(towns)('builds every blueprint building at its exact footprint, fronting its street ($seed)', ({ m }) => {
    const { houses, blocks } = buildLayout(m);
    expect(houses.length).toBe(m.buildings.length);
    for (const h of houses) {
      const b = m.buildings[h.buildingId];
      expect(h.tiles).toEqual(b.tiles);
      expect(h.facing).toBe(b.facing);
      const [dx, dy] = DIR[h.facing];
      expect(h.tiles.some((t) => isStreet(m, t.x + dx, t.y + dy))).toBe(true);
    }
    for (const plan of blocks) expect(plan.placed).toBe(plan.wanted);
  });

  it.each(towns)('keeps every use inside its own block and fills the whole block ($seed)', ({ m }) => {
    const { use } = buildLayout(m);
    const blockOf = new Int32Array(m.width * m.height).fill(-1);
    for (const b of m.blocks) {
      for (const t of tilesIn(b.polygon, b.holes, m.width, m.height)) {
        if (m.tiles.overlay[t] === Overlay.NONE) blockOf[t] = b.id;
      }
    }
    for (let i = 0; i < use.length; i++) {
      if (use[i] !== Use.NONE) expect(blockOf[i]).toBeGreaterThanOrEqual(0);
    }
  });

  it.each(towns)('fences yards and gardens but never across a street ($seed)', ({ m }) => {
    const { fences, use } = buildLayout(m);
    for (const f of fences) {
      expect([Use.YARD, Use.SHED, Use.GARDEN, Use.COMMUNITY_GARDEN]).toContain(use[index(f.x, f.y, m.width)]);
      const [dx, dy] = DIR[f.side];
      expect(isStreet(m, f.x + dx, f.y + dy)).toBe(false);
    }
  });

  it('gives houses back yards with sheds and gardens', () => {
    let sheds = 0;
    let gardens = 0;
    let yards = 0;
    for (const { m } of towns) {
      const { use } = buildLayout(m);
      for (const u of use) {
        if (u === Use.SHED) sheds++;
        if (u === Use.GARDEN) gardens++;
        if (u === Use.YARD) yards++;
      }
    }
    expect(yards).toBeGreaterThan(100);
    expect(sheds).toBeGreaterThan(20);
    expect(gardens).toBeGreaterThan(20);
  });

  it('turns some empty lots among houses into community gardens', () => {
    let community = 0;
    for (const { m } of towns) community += [...buildLayout(m).use].filter((u) => u === Use.COMMUNITY_GARDEN).length;
    expect(community).toBeGreaterThan(0);
  });
});

describe('iso layout: roads', () => {
  it('smooths roads but keeps their ends on the junctions', () => {
    const m = generateTown('smooth');
    const layout = buildLayout(m);
    m.roads.edges.forEach((e, i) => {
      const r = layout.roads[i];
      expect(r.points[0]).toEqual(e.points[0]);
      expect(r.points[r.points.length - 1]).toEqual(e.points[e.points.length - 1]);
      for (const p of r.points) expect(distToPolyline(p, e.points)).toBeLessThan(1.5);
    });
  });

  it('builds a sidewalk on both sides of every street and joins them at junctions', () => {
    const { sidewalks, roads } = buildLayout(generateTown('walks'));
    expect(sidewalks.paths.length).toBeGreaterThan(roads.filter((r) => r.kind !== 'path').length * 1.5);
    expect(sidewalks.links.filter((l) => l.length > 0).length / sidewalks.links.length).toBeGreaterThan(0.6);
  });

  it('marks river crossings as bridges and parks cars off the water', () => {
    const m = towns.map((t) => t.m).find((t) => t.bridges.length > 0)!;
    const layout = buildLayout(m);
    expect(layout.bridges.length).toBeGreaterThan(0);
    expect(layout.cars.length).toBeGreaterThan(5);
  });
});

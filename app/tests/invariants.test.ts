// The milestone "done when" checks, run over 50 seeds. Each pipeline stage
// owns its invariants (Stage.check); generating with { check: true } runs them
// right after the stage, and the tests below re-run them per milestone on the
// finished model so a failure names the milestone that broke.

import { beforeAll, describe, expect, it } from 'vitest';
import { autotileMask, isRoad } from '../src/core/autotile';
import { STAGES, generateTown } from '../src/gen';
import { DEFAULT_PARAMS } from '../src/model/params';
import { decodeModel, encodeModel } from '../src/model/serialize';
import type { TownModel } from '../src/model/types';
import { testSeeds } from './seeds';

const SEEDS = testSeeds(50);
const models = new Map<string, TownModel>();

beforeAll(() => {
  for (const seed of SEEDS) models.set(seed, generateTown(seed, {}, { check: true }));
});

const stage = (name: string) => STAGES.find((s) => s.name === name)!;
const violations = (name: string, model: TownModel) => stage(name).check?.(model) ?? [];

const MILESTONES: [string, string[]][] = [
  ['M1 terrain + water', ['terrain']],
  ['M2 rivers', ['rivers']],
  ['M3 center, gates, main roads, bridges', ['site', 'roads']],
  ['M4 streets', ['streets']],
  ['M5 blocks + lots', ['blocks', 'lots']],
  ['M6 buildings + landmarks', ['buildings']],
  ['M7 rasterize', ['rasterize']],
];

for (const [title, stages] of MILESTONES) {
  describe(title, () => {
    it.each(SEEDS)('holds for seed %s', (seed) => {
      const model = models.get(seed)!;
      // Terrain's water check measures the heightmap before rivers carve it.
      const m = stages.includes('terrain') ? generateTown(seed, {}, { until: 'terrain' }) : model;
      for (const s of stages) expect(violations(s, m), s).toEqual([]);
    });
  });
}

describe('M1 water fraction', () => {
  it('stays near the target across 20 seeds', () => {
    for (const seed of SEEDS.slice(0, 20)) {
      const m = generateTown(seed, {}, { until: 'terrain' });
      let water = 0;
      for (const v of m.heightmap) if (v < m.waterLevel) water++;
      expect(Math.abs(water / m.heightmap.length - DEFAULT_PARAMS.waterFraction)).toBeLessThan(0.02);
    }
  });
});

describe('town content', () => {
  it('builds a real town on most seeds', () => {
    const busy = SEEDS.filter((s) => models.get(s)!.buildings.length >= 30);
    expect(busy.length).toBeGreaterThanOrEqual(SEEDS.length * 0.9);
  });

  it('places a plaza and a well on every seed', () => {
    for (const seed of SEEDS) {
      const kinds = models.get(seed)!.landmarks.map((l) => l.kind);
      expect(kinds).toContain('plaza');
      expect(kinds).toContain('well');
    }
  });
});

describe('M7 iso handoff', () => {
  it('road autotile masks see their road neighbours', () => {
    const m = models.get(SEEDS[0])!;
    const road = isRoad(m.tiles);
    let checked = 0;
    for (let y = 1; y < m.height - 1 && checked < 200; y++) {
      for (let x = 1; x < m.width - 1 && checked < 200; x++) {
        const i = y * m.width + x;
        if (!road(i)) continue;
        const mask = autotileMask(m.tiles, x, y, road);
        expect(Boolean(mask & 1)).toBe(road(i - m.width));
        expect(Boolean(mask & 2)).toBe(road(i + 1));
        expect(Boolean(mask & 4)).toBe(road(i + m.width));
        expect(Boolean(mask & 8)).toBe(road(i - 1));
        checked++;
      }
    }
    expect(checked).toBeGreaterThan(0);
  });

  it('every building carries iso hints', () => {
    for (const b of models.get(SEEDS[1])!.buildings) {
      expect(b.tiles.length).toBeGreaterThan(0);
      expect(['N', 'E', 'S', 'W']).toContain(b.facing);
      expect(b.floors).toBeGreaterThanOrEqual(1);
    }
  });
});

describe('M8 export + performance', () => {
  it('JSON export round-trips exactly', () => {
    for (const seed of SEEDS.slice(0, 5)) {
      const model = models.get(seed)!;
      const json = encodeModel(model);
      const back = decodeModel(json);
      expect(encodeModel(back)).toBe(json);
      expect(back.tiles.walkable).toBeInstanceOf(Uint8Array);
      expect(back.fields.roadCost).toBeInstanceOf(Float32Array);
    }
  });

  it('generates a 128x128 town in under 500 ms', () => {
    generateTown('warmup');
    const times = SEEDS.slice(0, 5).map((seed) => {
      const t0 = performance.now();
      generateTown(seed, { width: 128, height: 128 });
      return performance.now() - t0;
    });
    const mean = times.reduce((a, b) => a + b, 0) / times.length;
    expect(mean).toBeLessThan(500);
  });
});

import { describe, expect, it } from 'vitest';
import { createRng, stageRng } from '../src/core/rng';
import { generateTown } from '../src/gen';
import { encodeModel } from '../src/model/serialize';
import { testSeeds } from './seeds';

describe('rng', () => {
  it('repeats the same sequence for the same seed', () => {
    const a = createRng('abc');
    const b = createRng('abc');
    for (let i = 0; i < 100; i++) expect(a.next()).toBe(b.next());
  });

  it('gives each stage its own stream', () => {
    expect(stageRng('abc', 'terrain').next()).not.toBe(stageRng('abc', 'rivers').next());
  });

  it('stays in [0, 1)', () => {
    const r = createRng('range');
    for (let i = 0; i < 10_000; i++) {
      const v = r.next();
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThan(1);
    }
  });
});

describe('generateTown', () => {
  it.each(testSeeds(10))('is identical for the same seed (%s)', (seed) => {
    expect(encodeModel(generateTown(seed))).toBe(encodeModel(generateTown(seed)));
  });

  it('changes with the seed', () => {
    expect(encodeModel(generateTown('one'))).not.toBe(encodeModel(generateTown('two')));
  });

  it('honours map size params', () => {
    const model = generateTown('size', { width: 64, height: 96 });
    expect(model.tiles.terrain.length).toBe(64 * 96);
  });

  it('works at the smallest and largest map sizes', () => {
    for (const size of [32, 256]) {
      const m = generateTown(`size-${size}`, { width: size, height: size }, { check: true });
      expect(m.width).toBe(size);
    }
  });
});

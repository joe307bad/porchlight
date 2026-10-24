// Stage 1: heightmap from seeded fractal simplex noise, flattened toward the
// map edges so hills aren't cut off, with the water level set as a percentile.

import { createNoise2D } from 'simplex-noise';
import type { Stage } from './stage';

const PERSISTENCE = 0.4;
/**
 * Height per unit of noise at REFERENCE_SCALE. Heights aren't stretched to 0..1
 * per map, so a small map isn't steeper than a big one, and relief shrinks with
 * the feature size, so smaller hills get lower rather than steeper.
 */
const RELIEF = 0.75;
const REFERENCE_SCALE = 96;

const smoothstep = (t: number) => t * t * (3 - 2 * t);

export const terrain: Stage = {
  name: 'terrain',
  run(model, rng) {
    const { width: w, height: h, params: p } = model;
    const noise = createNoise2D(() => rng.next());
    const hm = new Float32Array(w * h);
    const relief = RELIEF * Math.min(1, p.noiseScale / REFERENCE_SCALE);

    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        let amp = 1;
        let freq = 1 / p.noiseScale;
        let sum = 0;
        let norm = 0;
        for (let o = 0; o < p.noiseOctaves; o++) {
          sum += amp * noise(x * freq, y * freq);
          norm += amp;
          amp *= PERSISTENCE;
          freq *= 2;
        }
        // tanh keeps the middle linear (slope `relief`) and eases the extremes into 0..1 without clamping.
        hm[y * w + x] = 0.5 + 0.5 * Math.tanh((sum / norm) * relief * 2);
      }
    }

    // Ease the outer band toward the map's median height: no cliffs at the border.
    const band = p.edgeFalloff * Math.min(w, h);
    if (band > 0) {
      const edgeLevel = percentile(hm, 0.5);
      for (let y = 0; y < h; y++) {
        for (let x = 0; x < w; x++) {
          const d = Math.min(x, y, w - 1 - x, h - 1 - y);
          if (d >= band) continue;
          const t = smoothstep(d / band);
          const i = y * w + x;
          hm[i] = Math.min(1, Math.max(0, edgeLevel + (hm[i] - edgeLevel) * t));
        }
      }
    }

    const waterLevel = p.waterFraction > 0 ? percentile(hm, p.waterFraction) : -1;
    return { ...model, heightmap: hm, waterLevel };
  },
  check(model) {
    const errors: string[] = [];
    let water = 0;
    for (const v of model.heightmap) {
      if (!(v >= 0 && v <= 1)) {
        errors.push(`height ${v} outside 0..1`);
        break;
      }
      if (v < model.waterLevel) water++;
    }
    const fraction = water / model.heightmap.length;
    if (Math.abs(fraction - model.params.waterFraction) > 0.02) {
      errors.push(`water fraction ${fraction.toFixed(3)} far from target ${model.params.waterFraction}`);
    }
    return errors;
  },
};

/** Value below which `q` of the cells fall. */
export function percentile(values: Float32Array, q: number): number {
  const sorted = Float32Array.from(values).sort();
  return sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))];
}

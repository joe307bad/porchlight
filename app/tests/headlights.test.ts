// Where a car's headlights, tail lights and beam go, whichever way it points.

import { describe, expect, it } from 'vitest';
import { carLights } from '../src/render/iso/headlights';

const shape = { halfLength: 0.52, halfWidth: 0.22, lampHeight: 0.14 };

describe('car lights', () => {
  for (const heading of [0, Math.PI / 2, 2.4, -1]) {
    it(`puts headlights and the beam ahead, tail lights behind, a lamp either side (heading ${heading.toFixed(2)})`, () => {
      const l = carLights(10, 20, heading, shape);
      const f = { x: Math.cos(heading), y: Math.sin(heading) };
      const ahead = (p: { x: number; y: number }) => (p.x - 10) * f.x + (p.y - 20) * f.y;
      const across = (p: { x: number; y: number }) => -(p.x - 10) * f.y + (p.y - 20) * f.x;
      for (const p of l.front) {
        expect(ahead(p)).toBeCloseTo(0.508);
        expect(p.h).toBe(0.14);
      }
      for (const p of l.back) expect(ahead(p)).toBeCloseTo(-0.508);
      expect(across(l.front[0])).toBeGreaterThan(0.1);
      expect(across(l.front[1])).toBeLessThan(-0.1);
      expect(Math.abs(across(l.front[0]))).toBeLessThan(shape.halfWidth);
      expect(ahead(l.beam)).toBeGreaterThan(shape.halfLength);
      expect(Math.abs(across(l.beam))).toBeLessThan(1e-9);
      expect(l.forward.x).toBeCloseTo(f.x);
    });
  }
});

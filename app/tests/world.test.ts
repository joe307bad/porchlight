// The 3D town builder places every blueprint building inside its footprint,
// gives the camera's sight checks a box for each, and produces finite
// transforms for every instance. Kenney models are stood in for by boxes.

import { BoxGeometry, InstancedMesh, Matrix4, MeshBasicMaterial, Texture, Vector3 } from 'three';
import { describe, expect, it, vi } from 'vitest';
import { createRng } from '../src/core/rng';
import { generateTown } from '../src/gen';
import { DEFAULT_PARAMS } from '../src/model/params';
import type { Prefab, TownAssets } from '../src/render/iso/assets';
import { Traffic } from '../src/render/iso/traffic';
import { Fleet, pickCarModel } from '../src/render/iso/fleet';
import { buildWorld } from '../src/render/iso/world';
import { doorOf, inRoom } from '../src/render/iso/interiors';
import { testSeeds } from './seeds';

vi.mock('../src/render/iso/textures', () => ({
  glowTexture: () => new Texture(),
  signAtlas: () => ({ texture: new Texture(), cell: () => ({ u0: 0, u1: 1, v0: 0, v1: 1 }) }),
}));

function boxPrefab(name: string, w: number, h: number, d: number): Prefab {
  const geometry = new BoxGeometry(w, h, d).translate(0, h / 2, 0);
  return { name, parts: [{ geometry, material: new MeshBasicMaterial() }], min: new Vector3(-w / 2, 0, -d / 2), size: new Vector3(w, h, d) };
}

/** Stand-in assets: a box per model, with house-like proportions that vary by name. */
function fakeAssets(): TownAssets {
  const cache = new Map<string, Prefab>();
  const prefab = (name: string) => {
    let p = cache.get(name);
    if (!p) {
      const k = name.length % 5;
      p = name.startsWith('cars/') ? boxPrefab(name, 1.5, 1.3, 2.5) : boxPrefab(name, 1 + k * 0.2, 0.8 + k * 0.3, 1 + ((k * 3) % 5) * 0.1);
      cache.set(name, p);
    }
    return p;
  };
  return { prefab, variant: (name) => prefab(name), variationCount: () => 3, character: () => { throw new Error('no characters in this test'); } };
}

describe('3D town builder', () => {
  for (const seed of testSeeds(4)) {
    it(`fits every building inside its footprint (${seed})`, () => {
      const model = generateTown(seed, DEFAULT_PARAMS);
      const world = buildWorld(model, fakeAssets());
      const { houses } = world.layout;
      expect(world.boxes.length).toBeGreaterThanOrEqual(houses.length);
      // Every house is covered by its pieces, and every piece lies inside a house.
      let covered = 0;
      for (const h of houses) {
        const xs = h.tiles.map((t) => t.x);
        const ys = h.tiles.map((t) => t.y);
        const [x0, y0, x1, y1] = [Math.min(...xs), Math.min(...ys), Math.max(...xs) + 1, Math.max(...ys) + 1];
        const mine = world.boxes.filter((b) => b.x0 >= x0 - 1e-6 && b.x1 <= x1 + 1e-6 && b.y0 >= y0 - 1e-6 && b.y1 <= y1 + 1e-6);
        const area = mine.reduce((s, b) => s + (b.x1 - b.x0) * (b.y1 - b.y0), 0);
        expect(area).toBeCloseTo((x1 - x0) * (y1 - y0), 5);
        covered += mine.length;
        for (const b of mine) expect(b.top).toBeGreaterThan(0.5);
      }
      expect(covered).toBe(world.boxes.length);

      // Every instance transform is finite.
      const m = new Matrix4();
      let instances = 0;
      world.root.traverse((o) => {
        const mesh = o as InstancedMesh;
        if (!mesh.isInstancedMesh) return;
        for (let i = 0; i < mesh.count; i++) {
          mesh.getMatrixAt(i, m);
          for (const e of m.elements) expect(Number.isFinite(e)).toBe(true);
          instances++;
        }
      });
      expect(instances).toBeGreaterThan(houses.length);
      expect(world.lamps.length).toBeGreaterThan(0);

      // A furnished room behind every piece, each with a door you can walk in by.
      expect(world.rooms.length).toBe(world.boxes.length);
      expect(world.doors.length).toBe(world.rooms.length);
      let furnished = 0;
      world.rooms.forEach((room, i) => {
        const { at, outward } = doorOf(room);
        for (let s = 0.35; s >= -0.35; s -= 0.05) {
          const p = { x: at.x + outward.x * s, y: at.y + outward.y * s };
          expect(world.solids.hits(p.x, p.y, 0.13), `room ${i} (${room.style}, ${room.facing}) at ${s.toFixed(2)}`).toBe(false);
        }
        expect(inRoom(room, { x: at.x - outward.x * 0.35, y: at.y - outward.y * 0.35 })).toBe(true);
        if (world.plans[i].furniture.length >= 3) furnished++;
        // Its interior builds.
        expect(world.interior(i).children.length).toBeGreaterThan(0);
        world.setShellVisible(i, false);
        world.setShellVisible(i, true);
      });
      expect(furnished / world.rooms.length).toBeGreaterThan(0.8);

      const traffic = new Traffic(world.layout, createRng(`${seed}::traffic`), pickCarModel);
      const fleet = new Fleet(traffic, fakeAssets(), world.root);
      traffic.update(0.5);
      fleet.update();
      world.dispose();
      fleet.dispose();
    });
  }
});

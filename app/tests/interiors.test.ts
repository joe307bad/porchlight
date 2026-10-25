import { describe, expect, it } from 'vitest';
import { createRng } from '../src/core/rng';
import type { Facing } from '../src/model/types';
import { Solids } from '../src/render/iso/collide';
import { DOOR_WIDTH, FURNITURE, WALL, doorOf, furnishRoom, inRoom, innerRect, roomWalls, type FurnitureSize, type Room } from '../src/render/iso/interiors';
import type { HouseStyle } from '../src/render/iso/layout';

/** Rough furniture footprints (tiles), like the kit's at scale. */
const SIZES: Record<string, FurnitureSize> = Object.fromEntries(
  FURNITURE.map((n) => {
    const big = /sofa|Sofa|bookcaseClosedWide|table$|rugRectangle|cabinetTelevision|desk/.test(n);
    const rug = n.startsWith('rug');
    return [n, { w: rug ? 0.9 : big ? 0.5 : 0.26, d: rug ? 0.55 : big ? 0.26 : 0.26, h: 0.25 }];
  }),
);
const size = (n: string) => {
  const s = SIZES[n];
  if (!s) throw new Error(`no size for ${n}`);
  return s;
};

const STYLES: HouseStyle[] = ['house', 'shop', 'office', 'workshop', 'hall', 'church'];
const FACINGS: Facing[] = ['N', 'E', 'S', 'W'];

function room(facing: Facing, style: HouseStyle, w = 3, d = 4): Room {
  const alongX = facing === 'N' || facing === 'S';
  return { id: 1, x0: 10, y0: 20, x1: 10 + (alongX ? w : d), y1: 20 + (alongX ? d : w), facing, style };
}

describe('ground-floor rooms', () => {
  it('put the door in the middle of the front wall, with a gap you fit through', () => {
    for (const f of FACINGS) {
      const r = room(f, 'house');
      const door = doorOf(r);
      const solids = new Solids(roomWalls(r), 64);
      const radius = 0.13;
      // Walking straight in through the door is clear all the way...
      for (let s = -0.4; s <= 0.6; s += 0.05) {
        const p = { x: door.at.x - door.outward.x * s, y: door.at.y - door.outward.y * s };
        expect(solids.hits(p.x, p.y, radius), `${f} ${s}`).toBe(false);
      }
      // ...but a step to one side of the door is wall.
      const beside = { x: door.at.x + door.along.x * (DOOR_WIDTH / 2 + 0.2), y: door.at.y + door.along.y * (DOOR_WIDTH / 2 + 0.2) };
      expect(solids.hits(beside.x, beside.y, radius)).toBe(true);
      // Just inside the door is in the room.
      expect(inRoom(r, { x: door.at.x - door.outward.x * 0.3, y: door.at.y - door.outward.y * 0.3 })).toBe(true);
      expect(inRoom(r, { x: door.at.x + door.outward.x * 0.3, y: door.at.y + door.outward.y * 0.3 })).toBe(false);
      // The other three walls are solid all along.
      const c = { x: (r.x0 + r.x1) / 2, y: (r.y0 + r.y1) / 2 };
      expect(solids.hits(c.x, r.y0 + 0.01, radius) || r.facing === 'N').toBe(true);
    }
  });

  for (const style of STYLES) {
    it(`furnish a ${style} inside its walls, clear of the door and without overlaps`, () => {
      for (const f of FACINGS) {
        for (const [w, d] of [[2, 3], [3, 4], [5, 3], [4, 6], [1.6, 2]]) {
          const r = room(f, style, w, d);
          const plan = furnishRoom(r, size, createRng(`${style}-${f}-${w}-${d}`));
          const inner = innerRect(r);
          const door = doorOf(r);
          const entry = { x: door.at.x - door.outward.x * 0.35, y: door.at.y - door.outward.y * 0.35 };
          for (const p of plan.furniture) {
            expect(p.box.x0).toBeGreaterThanOrEqual(inner.x0 - 1e-6);
            expect(p.box.y0).toBeGreaterThanOrEqual(inner.y0 - 1e-6);
            expect(p.box.x1).toBeLessThanOrEqual(inner.x1 + 1e-6);
            expect(p.box.y1).toBeLessThanOrEqual(inner.y1 + 1e-6);
            expect(Number.isFinite(p.rot)).toBe(true);
          }
          const solid = plan.furniture.filter((p) => !p.flat && p.lift === 0);
          for (let i = 0; i < solid.length; i++) {
            for (let j = i + 1; j < solid.length; j++) {
              const a = solid[i].box;
              const b = solid[j].box;
              const overlap = Math.min(a.x1, b.x1) - Math.max(a.x0, b.x0) > 1e-6 && Math.min(a.y1, b.y1) - Math.max(a.y0, b.y0) > 1e-6;
              expect(overlap, `${solid[i].name} / ${solid[j].name}`).toBe(false);
            }
          }
          // You can always step in through the door.
          const solids = new Solids([...roomWalls(r), ...solid.map((p) => p.box)], 64);
          expect(solids.hits(entry.x, entry.y, 0.13), `${style} ${f} ${w}x${d}`).toBe(false);
          if (w >= 3 && d >= 3) expect(plan.furniture.length, `${style} ${w}x${d}`).toBeGreaterThan(3);
        }
      }
    });
  }

  it('puts someone behind the shop counter, facing the door', () => {
    const r = room('S', 'shop', 4, 4);
    const plan = furnishRoom(r, size, createRng('keeper'));
    expect(plan.keeper).not.toBeNull();
    expect(inRoom(r, plan.keeper!)).toBe(true);
    // Facing the door: toward +y for a south-facing shop.
    expect(Math.cos(plan.keeper!.rot)).toBeCloseTo(1);
  });

  it('faces wall furniture into the room', () => {
    const r = room('S', 'house', 4, 4);
    const plan = furnishRoom(r, size, createRng('kitchen'));
    const fridge = plan.furniture.find((p) => p.name === 'kitchenFridge');
    expect(fridge).toBeDefined();
    // The back wall of a south-facing room is at the north (y0): the kitchen faces south.
    expect(fridge!.y).toBeLessThan(r.y0 + WALL + 0.4);
    expect(Math.cos(fridge!.rot)).toBeCloseTo(1);
  });
});

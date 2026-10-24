import { describe, expect, it } from 'vitest';
import { generateTown } from '../src/gen';
import { Overlay } from '../src/model/types';
import { streetMask } from '../src/render/iso/streets';

describe('streets-only walking', () => {
  it('covers roads and the spawn but no buildings or water', () => {
    const m = generateTown('street-mask');
    const street = streetMask(m.tiles);
    expect(street[Math.floor(m.spawn.y) * m.width + Math.floor(m.spawn.x)]).toBe(1);
    for (let i = 0; i < street.length; i++) {
      if (!street[i]) continue;
      expect(m.tiles.buildingId[i]).toBe(-1);
      expect([Overlay.ROAD_MAIN, Overlay.ROAD_STREET, Overlay.BRIDGE, Overlay.PATH, Overlay.PLAZA]).toContain(m.tiles.overlay[i]);
    }
  });
});

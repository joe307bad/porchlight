// The player's footwork: momentum, sprinting, and the jump (held for height,
// tapped for a hop, buffered just before landing).

import { describe, expect, it } from 'vitest';
import { Player } from '../src/render/iso/actors';
import type { TownModel } from '../src/model/types';

const SIZE = 40;
const model = { width: SIZE, height: SIZE } as TownModel;
const open = new Uint8Array(SIZE * SIZE).fill(1);
const DT = 1 / 60;

function player(): Player {
  return new Player({ model: 'people/character-1-2-2' }, SIZE / 2, SIZE / 2);
}

/** Run `seconds` of updates walking `dir`, calling `each` before every step; returns the highest lift reached. */
function run(p: Player, seconds: number, dir: { x: number; y: number } | null, each?: (t: number) => void): number {
  let top = 0;
  for (let t = 0; t < seconds; t += DT) {
    each?.(t);
    p.update(dir, DT, model, open);
    top = Math.max(top, p.lift);
  }
  return top;
}

describe('player footwork', () => {
  it('gets up to speed over a moment, and stops over a shorter one', () => {
    const p = player();
    run(p, DT * 2, { x: 1, y: 0 });
    expect(p.speed).toBeLessThan(1);
    run(p, 0.5, { x: 1, y: 0 });
    expect(p.speed).toBeCloseTo(3.4, 1);
    run(p, DT * 2, null);
    expect(p.speed).toBeGreaterThan(1);
    run(p, 0.3, null);
    expect(p.speed).toBe(0);
  });

  it('sprints faster than it runs, and a part-pushed stick walks', () => {
    const p = player();
    p.sprinting = true;
    run(p, 1, { x: 0, y: 1 });
    expect(p.speed).toBeGreaterThan(5.5);
    const q = player();
    run(q, 1, { x: 0, y: 0.3 });
    expect(q.speed).toBeLessThan(1.2);
  });

  it('jumps higher held than tapped, and higher at a run', () => {
    const held = player();
    held.jump();
    const high = run(held, 1, null, () => (held.jumpHeld = true));
    const tapped = player();
    tapped.jump();
    const low = run(tapped, 1, null, () => (tapped.jumpHeld = false));
    expect(high).toBeGreaterThan(0.18);
    expect(high).toBeLessThan(0.25);
    expect(low).toBeLessThan(high * 0.75);
    expect(held.lift).toBe(0);
    const runner = player();
    run(runner, 0.5, { x: 1, y: 0 });
    runner.jump();
    expect(run(runner, 1, { x: 1, y: 0 }, () => (runner.jumpHeld = true))).toBeGreaterThan(high);
  });

  it('keeps its momentum in the air', () => {
    const p = player();
    run(p, 0.5, { x: 1, y: 0 });
    p.jump();
    run(p, 0.15, null, () => (p.jumpHeld = true));
    expect(p.lift).toBeGreaterThan(0);
    expect(p.speed).toBeGreaterThan(2.5);
  });

  it('jumps again on landing when jump is pressed just before', () => {
    const p = player();
    p.jump();
    let pressed = false;
    let landedAgain = false;
    for (let t = 0; t < 1.5; t += DT) {
      p.jumpHeld = false;
      if (!pressed && p.rise < 0 && p.lift < 0.03) {
        p.jump();
        pressed = true;
      }
      p.update(null, DT, model, open);
      if (pressed && p.lift > 0.02) landedAgain = true;
    }
    expect(pressed).toBe(true);
    expect(landedAgain).toBe(true);
  });
});

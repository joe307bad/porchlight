import { describe, expect, it } from 'vitest';
import { createRng } from '../src/core/rng';
import { distToPolyline } from '../src/core/geometry';
import { generateTown } from '../src/gen';
import { buildLayout } from '../src/render/iso/layout';
import { SidewalkWalk } from '../src/render/iso/walking';

const m = generateTown('walking');
const { sidewalks, roads } = buildLayout(m);
const onSomeSidewalk = (p: { x: number; y: number }) => Math.min(...sidewalks.paths.map((s) => distToPolyline(p, s.points)));

describe('sidewalk walking', () => {
  it('keeps to the sidewalks for a long walk (apart from short crossings at junctions)', () => {
    const walk = SidewalkWalk.random(sidewalks, createRng('a'));
    let off = 0;
    for (let k = 0; k < 2000; k++) {
      const p = walk.advance(0.3);
      if (!walk.isCrossing && onSomeSidewalk(p) > 0.05) off++;
    }
    expect(off).toBe(0);
  });

  it('turns around', () => {
    const walk = SidewalkWalk.random(sidewalks, createRng('b'));
    walk.advance(0.2);
    const h = walk.heading();
    walk.turnAround();
    const g = walk.heading();
    expect(h.x * g.x + h.y * g.y).toBeLessThan(-0.9);
  });

  it('crosses to the sidewalk opposite, across its own road', () => {
    const walk = SidewalkWalk.random(sidewalks, createRng('c'));
    walk.advance(0.5);
    const before = walk.position();
    const toward = walk.towardRoad();
    expect(walk.crossStreet()).toBe(true);
    // Finish the crossing.
    for (let k = 0; k < 40 && walk.isCrossing; k++) walk.advance(0.1);
    const after = walk.position();
    const moved = { x: after.x - before.x, y: after.y - before.y };
    const across = Math.hypot(moved.x, moved.y);
    expect(across).toBeGreaterThan(1);
    // It went towards the road it was beside, and ended up on a sidewalk.
    expect((moved.x * toward.x + moved.y * toward.y) / across).toBeGreaterThan(0.7);
    expect(onSomeSidewalk(after)).toBeLessThan(0.05);
    expect(roads.length).toBeGreaterThan(0);
  });

  it('joins the nearest sidewalk from anywhere', () => {
    const walk = SidewalkWalk.join(sidewalks, createRng('d'), { x: m.width / 2 + 0.3, y: m.height / 2 + 0.7 });
    for (let k = 0; k < 200 && walk.isCrossing; k++) walk.advance(0.2);
    expect(walk.isCrossing).toBe(false);
    expect(onSomeSidewalk(walk.position())).toBeLessThan(0.05);
  });
});

describe('roaming auto-walk', () => {
  it('travels to a distant part of town using only sidewalks and street crossings', () => {
    const walk = SidewalkWalk.random(sidewalks, createRng('roam'));
    const start = walk.position();
    walk.roam();
    const goal = walk.goalPoint()!;
    expect(Math.hypot(goal.x - start.x, goal.y - start.y)).toBeGreaterThan(15);
    let reached = false;
    let off = 0;
    for (let k = 0; k < 20000 && !reached; k++) {
      const p = walk.advance(0.2);
      if (!walk.isCrossing && onSomeSidewalk(p) > 0.05) off++;
      if (Math.hypot(p.x - goal.x, p.y - goal.y) < 6) reached = true;
    }
    expect(off).toBe(0);
    expect(reached).toBe(true);
  });
});

describe('route display', () => {
  it('draws the planned route from the walker to its goal', () => {
    const walk = SidewalkWalk.random(sidewalks, createRng('route'));
    walk.roam();
    const pts = walk.routePoints();
    const goal = walk.goalPoint()!;
    expect(pts.length).toBeGreaterThan(2);
    expect(Math.hypot(pts[0].x - walk.position().x, pts[0].y - walk.position().y)).toBeLessThan(1e-6);
    const end = pts[pts.length - 1];
    expect(Math.hypot(end.x - goal.x, end.y - goal.y)).toBeLessThan(0.5);
    // Every point is on a sidewalk (the straight bits between them are sidewalk runs or crossings).
    for (const p of pts) expect(onSomeSidewalk(p)).toBeLessThan(0.05);
  });
});

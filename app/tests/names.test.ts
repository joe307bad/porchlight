// Street names, the signs that carry them, and auto walk's turn-by-turn
// directions read off the planned route.

import { describe, expect, it } from 'vitest';
import { createRng } from '../src/core/rng';
import { distToPolyline } from '../src/core/geometry';
import { generateTown } from '../src/gen';
import { compass, directions, formatDistance, turnKind } from '../src/render/iso/directions';
import { buildLayout } from '../src/render/iso/layout';
import { nameStreets } from '../src/render/iso/names';
import { planSigns } from '../src/render/iso/signs';
import { SidewalkWalk } from '../src/render/iso/walking';
import { testSeeds } from './seeds';

describe('street names', () => {
  for (const seed of testSeeds(8)) {
    const m = generateTown(seed);
    const layout = buildLayout(m);
    const names = nameStreets(m, layout.roads);

    it(`names every road, each street once (${seed})`, () => {
      expect(names.ofRoad).toHaveLength(layout.roads.length);
      for (const s of names.ofRoad) expect(names.streets[s].name).toMatch(/^[A-Z][a-z]+( [A-Z][a-z]+)+$/);
      const all = names.streets.map((s) => s.name);
      expect(new Set(all).size).toBe(all.length);
      // Roads join into longer streets, not one name per stretch between junctions.
      expect(names.streets.length).toBeLessThan(layout.roads.length);
    });

    it(`is the same every time for the same town (${seed})`, () => {
      expect(nameStreets(m, layout.roads).streets.map((s) => s.name)).toEqual(names.streets.map((s) => s.name));
    });

    it(`puts signs on the pavement at junctions, off the road (${seed})`, () => {
      const signs = planSigns(m, layout, names);
      expect(signs.length).toBeGreaterThan(0);
      for (const s of signs) {
        expect(new Set(s.blades.map((b) => b.street)).size).toBe(s.blades.length);
        expect(s.blades.length).toBeGreaterThanOrEqual(2);
        for (const r of layout.roads) expect(distToPolyline(s.at, r.points)).toBeGreaterThan(r.width / 2);
      }
      // Most named streets have a sign somewhere.
      const signed = new Set(signs.flatMap((s) => s.blades.map((b) => b.street)));
      expect(signed.size).toBeGreaterThan(names.streets.length * 0.6);
    });
  }

  it('calls the main road through the centre the High Street', () => {
    const m = generateTown('walking');
    const names = nameStreets(m, buildLayout(m).roads);
    expect(names.streets.filter((s) => s.name === 'High Street')).toHaveLength(1);
    expect(names.streets.find((s) => s.name === 'High Street')!.kind).toBe('main');
  });
});

describe('auto walk directions', () => {
  const m = generateTown('walking');
  const layout = buildLayout(m);
  const names = nameStreets(m, layout.roads);
  const net = layout.sidewalks;

  it('reads the route out from the start to the arrival, on named streets', () => {
    for (const seed of ['a', 'b', 'c', 'd', 'e']) {
      const walk = SidewalkWalk.random(net, createRng(seed));
      walk.roam();
      const it = walk.itinerary();
      if (!it) continue;
      const steps = directions(net, names, it);
      expect(steps[0].kind).toBe('start');
      expect(steps[0].text).toMatch(/^Head (north|south|east|west|north-east|north-west|south-east|south-west) on /);
      expect(steps[steps.length - 1].kind).toBe('arrive');
      for (const s of steps) {
        expect(s.text).toContain(names.streets[s.street].name);
        expect(s.distance).toBeGreaterThanOrEqual(0);
      }
      // A step only starts where something changes: no two plain walks along the same street in a row.
      for (let k = 1; k < steps.length; k++) {
        if (steps[k].kind === 'straight') expect(steps[k].street).not.toBe(steps[k - 1].street);
      }
    }
  });

  it('adds up to the length of the route', () => {
    const walk = SidewalkWalk.random(net, createRng('f'));
    walk.roam();
    const it = walk.itinerary()!;
    const total = directions(net, names, it).reduce((s, st) => s + st.distance, 0);
    const pts = walk.routePoints();
    let route = 0;
    for (let k = 1; k < pts.length; k++) route += Math.hypot(pts[k].x - pts[k - 1].x, pts[k].y - pts[k - 1].y);
    // The route line also includes the short hops across junctions and roads.
    expect(total).toBeLessThanOrEqual(route + 1e-6);
    expect(total).toBeGreaterThan(route * 0.6);
  });

  it('shrinks as you walk it', () => {
    const walk = SidewalkWalk.random(net, createRng('g'));
    walk.roam();
    const first = directions(net, names, walk.itinerary()!);
    const before = first.reduce((s, st) => s + st.distance, 0);
    walk.advance(3);
    const after = directions(net, names, walk.itinerary()!).reduce((s, st) => s + st.distance, 0);
    expect(after).toBeLessThan(before);
  });

  it('starts by making your way to the sidewalk when joining from off it', () => {
    const walk = SidewalkWalk.join(net, createRng('h'), m.spawn);
    walk.roam();
    const it = walk.itinerary();
    expect(it).not.toBeNull();
    expect(directions(net, names, it!)[0].kind).toBe('join');
  });

  it('names turns and compass points the map way up (y runs south)', () => {
    expect(turnKind({ x: 1, y: 0 }, { x: 0, y: 1 })).toBe('right');
    expect(turnKind({ x: 1, y: 0 }, { x: 0, y: -1 })).toBe('left');
    expect(turnKind({ x: 1, y: 0 }, { x: 1, y: 0.1 })).toBe('straight');
    expect(turnKind({ x: 1, y: 0 }, { x: 1, y: 0.8 })).toBe('slight-right');
    expect(turnKind({ x: 1, y: 0 }, { x: -1, y: 0.05 })).toBe('uturn');
    expect(compass({ x: 0, y: -1 })).toBe('north');
    expect(compass({ x: 1, y: 0 })).toBe('east');
    expect(compass({ x: -1, y: 1 })).toBe('south-west');
  });

  it('rounds distances like a map app', () => {
    expect(formatDistance(1)).toBe('');
    expect(formatDistance(10)).toBe('40 m');
    expect(formatDistance(31)).toBe('120 m');
    expect(formatDistance(400)).toBe('1.6 km');
  });
});

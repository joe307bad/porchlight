import { describe, expect, it } from 'vitest';
import { EXTRAPOLATE, ServerClock, SnapshotBuffer, turnBetween } from '../src/net/interp';
import appProtocol from '../src/net/protocol.ts?raw';
import serverProtocol from '../../server/src/multiplayer/protocol.ts?raw';

const snap = (t: number, x: number, yaw = 0) => ({ t, x, y: 0, z: 0, yaw });

describe('SnapshotBuffer', () => {
  it('interpolates between the snapshots either side', () => {
    const b = new SnapshotBuffer();
    b.push(snap(0, 0));
    b.push(snap(50, 1));
    b.push(snap(100, 3));
    expect(b.sample(25)?.x).toBeCloseTo(0.5);
    expect(b.sample(75)?.x).toBeCloseTo(2);
  });

  it('turns the short way round', () => {
    const b = new SnapshotBuffer();
    b.push(snap(0, 0, 3));
    b.push(snap(100, 0, -3)); // 0.28 rad apart across ±π, not 6
    const yaw = b.sample(50)!.yaw;
    expect(Math.abs(turnBetween(yaw, Math.PI))).toBeLessThan(0.01);
  });

  it('carries on briefly past the last snapshot, then holds', () => {
    const b = new SnapshotBuffer();
    b.push(snap(0, 0));
    b.push(snap(100, 1));
    expect(b.sample(150)?.x).toBeCloseTo(1.5);
    expect(b.sample(100 + EXTRAPOLATE + 300)?.x).toBeCloseTo(1 + EXTRAPOLATE / 100);
  });

  it('shrugs off a missing server time', () => {
    const b = new SnapshotBuffer();
    b.push(snap(Number.NaN, 1));
    expect(b.size).toBe(0);
    b.push(snap(0, 1));
    expect(b.sample(Number.NaN)).toBeNull();
    expect(b.sample(10)?.x).toBe(1);
  });

  it('jumps to the latest after a long gap, and ignores stale snapshots', () => {
    const b = new SnapshotBuffer();
    b.push(snap(0, 0));
    b.push(snap(50, 5));
    b.push(snap(20, 99));
    expect(b.sample(5000)?.x).toBe(5);
    expect(b.size).toBe(1);
  });
});

describe('ServerClock', () => {
  it('ignores a reading with no server time, rather than going NaN for good', () => {
    const c = new ServerClock();
    c.observe(undefined as unknown as number, 100);
    c.observe(1000, 100);
    expect(c.now(0)).toBe(900);
  });

  it('keeps the least delayed reading, easing down slowly', () => {
    const c = new ServerClock();
    expect(c.now(0)).toBeNull();
    c.observe(1000, 100); // 900
    c.observe(1050, 200); // 850: a slow patch, barely moves it
    expect(c.now(0)).toBe(898);
    c.observe(1200, 250); // 950: a quick one, taken at once
    expect(c.now(0)).toBe(950);
  });
});

it("the app's protocol is the server's", () => {
  const body = (s: string) => s.slice(s.indexOf('// Units are'));
  expect(body(appProtocol)).toBe(body(serverProtocol));
});

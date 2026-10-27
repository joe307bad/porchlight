import { describe, expect, it } from 'vitest';
import { EXTRAPOLATE, INTERP_DELAY, PlaybackClock, SnapshotBuffer, turnBetween } from '../src/net/interp';
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

describe('PlaybackClock', () => {
  it('plays back steadily however unevenly moves arrive', () => {
    const c = new PlaybackClock();
    // Moves taken every 33 ms on their clock (which reads 5000 behind ours),
    // arriving 30 to 110 ms later, in an uneven pattern.
    const jitter = [30, 90, 45, 110, 30, 70, 30, 100, 50, 30];
    let next = 0;
    let head: number | null = null;
    let steps = 0;
    for (let now = 5000; now < 9000; now += 1000 / 60) {
      while (next * 33 + 5000 + jitter[next % jitter.length] <= now) {
        c.observe(next * 33, now);
        next++;
      }
      const h = c.advance(now, 1000 / 60);
      if (h === null) continue;
      if (head !== null) {
        const step = h - head;
        // Always forward, never more than 15% off real time.
        expect(step).toBeGreaterThan((1000 / 60) * 0.84);
        expect(step).toBeLessThan((1000 / 60) * 1.16);
        steps++;
      }
      head = h;
    }
    expect(steps).toBeGreaterThan(200);
    // And on course: INTERP_DELAY behind the quickest arrival (30 ms).
    expect(head!).toBeCloseTo(9000 - 5000 - 30 - INTERP_DELAY, -1);
  });

  it('jumps when it is far off (a backgrounded tab coming back)', () => {
    const c = new PlaybackClock();
    c.observe(0, 50);
    c.advance(50, 16);
    c.observe(10_000, 10_050);
    expect(c.advance(10_050, 16)).toBeCloseTo(10_000 - INTERP_DELAY);
  });

  it('ignores a missing time', () => {
    const c = new PlaybackClock();
    c.observe(Number.NaN, 100);
    expect(c.advance(100, 16)).toBeNull();
  });
});

it("the app's protocol is the server's", () => {
  const body = (s: string) => s.slice(s.indexOf('// Units are'));
  expect(body(appProtocol)).toBe(body(serverProtocol));
});

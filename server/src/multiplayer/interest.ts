// Interest management: who each player is sent. The town is cut into square
// cells; a player sees everyone in their own cell and the 8 around it, and
// keeps seeing them until they're more than two cells off, so someone pacing
// along a cell edge doesn't blink in and out. Work happens only when a player
// crosses into a new cell.
//
// Pure bookkeeping (no Colyseus): the room drains the resulting add / remove
// operations each tick and applies them to the clients' StateViews.

/** Cell size in tiles (about 64 m). The 3×3 window is about as far as the zoomed-out camera sees. */
export const CELL = 16;
/** Seen from this many cells off (Chebyshev)... */
const ADD_RANGE = 1;
/** ...and kept until further than this. */
const KEEP_RANGE = 2;
/** Adds per viewer per tick; the rest wait, so walking into a crowd isn't one huge patch. */
export const MAX_ADDS_PER_TICK = 20;

export interface ViewOp {
  viewer: string;
  target: string;
  add: boolean;
}

interface Cell {
  cx: number;
  cz: number;
}

const key = (cx: number, cz: number) => `${cx},${cz}`;

export class Interest {
  private readonly cells = new Map<string, Set<string>>();
  private readonly where = new Map<string, Cell>();
  /** Who each player is being sent. */
  private readonly sees = new Map<string, Set<string>>();
  /** Who is being sent each player. */
  private readonly seenBy = new Map<string, Set<string>>();
  /** Adds still to make, by viewer. */
  private readonly pending = new Map<string, Set<string>>();
  private removes: ViewOp[] = [];

  /** Put a player at (x, z) tiles, or move them there. Changes come out of drain(). */
  move(id: string, x: number, z: number): void {
    const cx = Math.floor(x / CELL);
    const cz = Math.floor(z / CELL);
    const was = this.where.get(id);
    if (was && was.cx === cx && was.cz === cz) return;
    if (was) this.cells.get(key(was.cx, was.cz))?.delete(id);
    let cell = this.cells.get(key(cx, cz));
    if (!cell) this.cells.set(key(cx, cz), (cell = new Set()));
    cell.add(id);
    this.where.set(id, { cx, cz });
    if (!this.sees.has(id)) {
      this.sees.set(id, new Set());
      this.seenBy.set(id, new Set());
    }

    // Everyone near the new cell and they see each other...
    for (let dz = -ADD_RANGE; dz <= ADD_RANGE; dz++) {
      for (let dx = -ADD_RANGE; dx <= ADD_RANGE; dx++) {
        for (const other of this.cells.get(key(cx + dx, cz + dz)) ?? []) {
          if (other === id) continue;
          this.want(id, other);
          this.want(other, id);
        }
      }
    }
    // ...and anyone now too far off stops.
    for (const other of this.sees.get(id)!) if (this.distance(id, other) > KEEP_RANGE) this.drop(id, other);
    for (const viewer of this.seenBy.get(id)!) if (this.distance(viewer, id) > KEEP_RANGE) this.drop(viewer, id);
  }

  /** Take a player out altogether: everyone who could see them stops. */
  remove(id: string): void {
    const was = this.where.get(id);
    if (!was) return;
    for (const viewer of [...this.seenBy.get(id)!]) this.drop(viewer, id);
    for (const other of this.sees.get(id)!) this.seenBy.get(other)?.delete(id);
    for (const set of this.pending.values()) set.delete(id);
    this.pending.delete(id);
    this.cells.get(key(was.cx, was.cz))?.delete(id);
    this.where.delete(id);
    this.sees.delete(id);
    this.seenBy.delete(id);
  }

  /** The view changes to make now: every remove, and up to `maxAdds` adds per viewer. */
  drain(maxAdds = MAX_ADDS_PER_TICK): ViewOp[] {
    const ops = this.removes;
    this.removes = [];
    for (const [viewer, targets] of this.pending) {
      let n = 0;
      for (const target of targets) {
        if (n >= maxAdds) break;
        targets.delete(target);
        // Still around and still near enough by the time there's room for them?
        if (!this.where.has(target) || this.distance(viewer, target) > KEEP_RANGE) continue;
        this.sees.get(viewer)!.add(target);
        this.seenBy.get(target)!.add(viewer);
        ops.push({ viewer, target, add: true });
        n++;
      }
      if (!targets.size) this.pending.delete(viewer);
    }
    return ops;
  }

  /** Who `viewer` is being sent (after the last drain). */
  visibleTo(viewer: string): ReadonlySet<string> {
    return this.sees.get(viewer) ?? new Set();
  }

  private want(viewer: string, target: string): void {
    if (this.sees.get(viewer)!.has(target)) return;
    let set = this.pending.get(viewer);
    if (!set) this.pending.set(viewer, (set = new Set()));
    set.add(target);
  }

  private drop(viewer: string, target: string): void {
    this.pending.get(viewer)?.delete(target);
    if (!this.sees.get(viewer)?.delete(target)) return;
    this.seenBy.get(target)?.delete(viewer);
    this.removes.push({ viewer, target, add: false });
  }

  private distance(a: string, b: string): number {
    const p = this.where.get(a);
    const q = this.where.get(b);
    if (!p || !q) return Infinity;
    return Math.max(Math.abs(p.cx - q.cx), Math.abs(p.cz - q.cz));
  }
}

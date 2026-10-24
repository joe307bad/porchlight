// People: the player (keyboard-driven or auto-walking) and townsfolk walking
// the sidewalk graph. Pure state (position, heading, speed); the view poses a
// rigged 3D character to match each one.

import { index } from '../../core/grid';
import type { Rng } from '../../core/rng';
import type { TownModel, Vec2 } from '../../model/types';
import type { Look } from './characters';
import type { SidewalkNetwork } from './layout';
import { SidewalkWalk } from './walking';

export class Walker {
  /** Direction faced, radians: the 3D model's rotation about the vertical (0 = facing +y). */
  heading = 0;
  /** How fast they moved over the last step (tiles per second; 0 = standing). */
  speed = 0;

  constructor(
    readonly look: Look,
    public x: number,
    public y: number,
    /** Size relative to an average townsperson. */
    readonly scale = 1,
  ) {}

  /** Record a move of (dx, dy) tiles over dt seconds: face the way walked. */
  moved(dx: number, dy: number, dt: number): void {
    const d = Math.hypot(dx, dy);
    if (d > 1e-6) this.heading = Math.atan2(dx, dy);
    this.speed = dt > 0 ? d / dt : 0;
  }
}

const PLAYER_SPEED = 3.4; // tiles per second
/** How close to the map edge the player may walk (tiles). */
const EDGE = 0.2;
const PLAYER_RADIUS = 0.18;

const AUTO_SPEED = 1.8; // tiles per second

export class Player extends Walker {
  /** Set while auto-walking the sidewalks. */
  private auto: SidewalkWalk | null = null;

  get isAuto(): boolean {
    return this.auto !== null;
  }

  /** Head for the nearest sidewalk, then keep walking to far-off parts of town. */
  startAuto(net: SidewalkNetwork, rng: Rng): void {
    if (!net.paths.length) return;
    this.auto = SidewalkWalk.join(net, rng, { x: this.x, y: this.y });
    this.auto.roam();
  }

  /** The route auto-walk plans to take, as a polyline (empty when not auto-walking). */
  autoRoute(): Vec2[] {
    return this.auto?.routePoints() ?? [];
  }

  /** Where auto-walk is heading, if it is. */
  autoGoal(): Vec2 | null {
    return this.auto?.goalPoint() ?? null;
  }

  /** The way auto-walk is going along its sidewalk (or null when not auto-walking). */
  autoHeading(): Vec2 | null {
    return this.auto?.heading() ?? null;
  }

  stopAuto(): void {
    this.auto = null;
  }

  /**
   * While auto-walking, a direction key (as a world direction) is a request:
   * away from the way you're walking turns you around, towards the road crosses it.
   */
  command(d: Vec2): void {
    if (!this.auto) return;
    const h = this.auto.heading();
    const road = this.auto.towardRoad();
    if (d.x * h.x + d.y * h.y < -0.5) this.auto.turnAround();
    else if (d.x * road.x + d.y * road.y > 0.5) this.auto.crossStreet();
  }

  /**
   * Walk in a world direction (unit vector or null to stand still), only onto
   * tiles `open` allows (1 = walkable), sliding along walls.
   */
  update(dir: Vec2 | null, dt: number, model: TownModel, open: Uint8Array): void {
    if (this.auto) {
      const next = this.auto.advance(AUTO_SPEED * dt);
      this.moved(next.x - this.x, next.y - this.y, dt);
      this.x = next.x;
      this.y = next.y;
      return;
    }
    let dx = 0;
    let dy = 0;
    if (dir) {
      const step = PLAYER_SPEED * dt;
      const clampX = (v: number) => Math.min(model.width - EDGE, Math.max(EDGE, v));
      const clampY = (v: number) => Math.min(model.height - EDGE, Math.max(EDGE, v));
      // Try the full move, then each axis alone, so the player slides along edges.
      for (const [mx, my] of [[dir.x, dir.y], [dir.x, 0], [0, dir.y]]) {
        const nx = clampX(this.x + mx * step);
        const ny = clampY(this.y + my * step);
        if (canStand(nx, ny, open, model)) {
          dx = nx - this.x;
          dy = ny - this.y;
          break;
        }
      }
      this.x += dx;
      this.y += dy;
    }
    this.moved(dx, dy, dt);
  }
}

function canStand(x: number, y: number, open: Uint8Array, model: TownModel): boolean {
  const r = PLAYER_RADIUS;
  for (const [ox, oy] of [[0, 0], [r, 0], [-r, 0], [0, r], [0, -r]]) {
    const tx = Math.floor(x + ox);
    const ty = Math.floor(y + oy);
    if (tx < 0 || ty < 0 || tx >= model.width || ty >= model.height || !open[index(tx, ty, model.width)]) return false;
  }
  return true;
}

/** A townsperson strolling the sidewalks (now and then crossing the road). */
export class Pedestrian extends Walker {
  private readonly walk: SidewalkWalk;
  private pause = 0;
  private readonly pace: number;

  constructor(
    look: Look,
    net: SidewalkNetwork,
    private readonly rng: Rng,
  ) {
    const walk = SidewalkWalk.random(net, rng);
    const at = walk.position();
    super(look, at.x, at.y, rng.range(0.85, 1.05));
    this.walk = walk;
    this.pace = rng.range(0.9, 1.5);
  }

  update(dt: number): void {
    if (this.pause > 0) {
      this.pause -= dt;
      this.moved(0, 0, dt);
      return;
    }
    if (!this.walk.isCrossing) {
      if (this.rng.chance(dt * 0.01)) this.pause = this.rng.range(1, 4); // stop for a chat now and then
      else if (this.rng.chance(dt * 0.02)) this.walk.crossStreet();
    }
    const next = this.walk.advance(this.pace * dt);
    this.moved(next.x - this.x, next.y - this.y, dt);
    this.x = next.x;
    this.y = next.y;
  }
}

// People: the player (keyboard-driven, kept on streets) and townsfolk walking
// the sidewalk graph. Both are a single sprite swapping walk-cycle frames.

import { Sprite, type Texture } from 'pixi.js';
import { index } from '../../core/grid';
import type { Rng } from '../../core/rng';
import type { TownModel, Vec2 } from '../../model/types';
import { CHAR_ANCHOR, CHAR_SCALE, WALK_FRAME_COUNT, walkFrames, type Look, type WalkFrames } from './characters';
import { depthOf, HALF_H, HALF_W, toScreen } from './coords';
import type { SidewalkNetwork } from './layout';
import { SidewalkWalk } from './walking';

/** Tiles walked per animation cycle. */
const STRIDE = 0.9;

export class Walker {
  readonly sprite: Sprite;
  readonly frames: WalkFrames;
  x: number;
  y: number;
  z: number;
  private travelled = 0;
  /** White silhouette of the current pose, for the see-through outline. */
  mask: Texture;
  private facingBack = false;
  private facingLeft = false;

  constructor(look: Look, at: Vec2, z: number, scale = 1) {
    this.frames = walkFrames(look);
    this.sprite = new Sprite(this.frames.front[0]);
    this.mask = this.frames.frontMask[0];
    this.sprite.anchor.set(CHAR_ANCHOR.x, CHAR_ANCHOR.y);
    this.sprite.scale.set(CHAR_SCALE * scale);
    this.x = at.x;
    this.y = at.y;
    this.z = z;
    this.place();
  }

  /** Update the pose for a move of (dx, dy) tiles this frame. */
  animate(dx: number, dy: number): void {
    const moved = Math.hypot(dx, dy);
    if (moved > 1e-6) {
      const screen = { x: (dx - dy) * HALF_W, y: (dx + dy) * HALF_H };
      if (Math.abs(screen.y) > 1e-6) this.facingBack = screen.y < 0;
      if (Math.abs(screen.x) > 1e-6) this.facingLeft = screen.x < 0;
      this.travelled += moved;
    }
    const set = this.facingBack ? this.frames.back : this.frames.front;
    const masks = this.facingBack ? this.frames.backMask : this.frames.frontMask;
    const frame = moved > 1e-6 ? 1 + (Math.floor((this.travelled / STRIDE) * WALK_FRAME_COUNT) % WALK_FRAME_COUNT) : 0;
    this.sprite.texture = set[frame];
    this.mask = masks[frame];
    this.sprite.scale.x = Math.abs(this.sprite.scale.x) * (this.facingLeft ? -1 : 1);
  }

  place(): void {
    const p = toScreen(this.x, this.y, this.z);
    this.sprite.position.set(p.x, p.y);
    this.sprite.zIndex = depthOf(this.x, this.y, 8);
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

  stopAuto(): void {
    this.auto = null;
  }

  /**
   * While auto-walking, a direction key (screen-relative) is a request: away
   * from the way you're walking turns you around, towards the road crosses it.
   */
  command(input: Vec2): void {
    if (!this.auto) return;
    const d = screenToWorldDir(input);
    if (!d) return;
    const h = this.auto.heading();
    const road = this.auto.towardRoad();
    if (d.x * h.x + d.y * h.y < -0.5) this.auto.turnAround();
    else if (d.x * road.x + d.y * road.y > 0.5) this.auto.crossStreet();
  }

  private updateAuto(dt: number, model: TownModel): void {
    stepTo(this, this.auto!.advance(AUTO_SPEED * dt), dt, model);
  }

  /**
   * Move by a screen-space input direction (right, down), only onto tiles
   * `open` allows (it's 1 for walkable tiles), sliding along walls.
   */
  update(input: Vec2, dt: number, model: TownModel, open: Uint8Array): void {
    if (this.auto) {
      this.updateAuto(dt, model);
      return;
    }
    let dx = 0;
    let dy = 0;
    const len = Math.hypot(input.x, input.y);
    if (len > 0) {
      // Screen direction to world direction (so "up" walks straight up the screen).
      const ix = input.x / len;
      const iy = input.y / len;
      let wx = (ix / HALF_W + iy / HALF_H) / 2;
      let wy = (iy / HALF_H - ix / HALF_W) / 2;
      const wl = Math.hypot(wx, wy);
      wx /= wl;
      wy /= wl;
      const step = PLAYER_SPEED * dt;
      const clampX = (v: number) => Math.min(model.width - EDGE, Math.max(EDGE, v));
      const clampY = (v: number) => Math.min(model.height - EDGE, Math.max(EDGE, v));
      const ok = (x: number, y: number) => onStreet(x, y, open, model);
      // Try the full move, then each axis alone, so the player slides along street edges.
      for (const [mx, my] of [[wx, wy], [wx, 0], [0, wy]]) {
        const nx = clampX(this.x + mx * step);
        const ny = clampY(this.y + my * step);
        if (ok(nx, ny)) {
          dx = nx - this.x;
          dy = ny - this.y;
          break;
        }
      }
      this.x += dx;
      this.y += dy;
    }
    const target = model.tiles.elevation[index(Math.floor(this.x), Math.floor(this.y), model.width)] ?? 0;
    this.z += (target - this.z) * Math.min(1, dt * 12);
    this.animate(dx, dy);
    this.place();
  }
}

/** A screen-space input direction (right, down) as a unit world direction, or null for none. */
function screenToWorldDir(input: Vec2): Vec2 | null {
  const len = Math.hypot(input.x, input.y);
  if (!len) return null;
  const ix = input.x / len;
  const iy = input.y / len;
  const wx = (ix / HALF_W + iy / HALF_H) / 2;
  const wy = (iy / HALF_H - ix / HALF_W) / 2;
  const wl = Math.hypot(wx, wy);
  return { x: wx / wl, y: wy / wl };
}

function onStreet(x: number, y: number, street: Uint8Array, model: TownModel): boolean {
  const r = PLAYER_RADIUS;
  for (const [ox, oy] of [[0, 0], [r, 0], [-r, 0], [0, r], [0, -r]]) {
    const tx = Math.floor(x + ox);
    const ty = Math.floor(y + oy);
    if (tx < 0 || ty < 0 || tx >= model.width || ty >= model.height || !street[index(tx, ty, model.width)]) return false;
  }
  return true;
}

/** A townsperson strolling the sidewalks (now and then crossing the road). */
export class Pedestrian extends Walker {
  private readonly walk: SidewalkWalk;
  private pause = 0;
  private readonly speed: number;

  constructor(
    look: Look,
    net: SidewalkNetwork,
    private readonly model: TownModel,
    private readonly rng: Rng,
  ) {
    const walk = SidewalkWalk.random(net, rng);
    super(look, walk.position(), 0, rng.range(0.85, 1.05));
    this.walk = walk;
    this.speed = rng.range(0.9, 1.5);
  }

  update(dt: number): void {
    if (this.pause > 0) {
      this.pause -= dt;
      this.animate(0, 0);
      return;
    }
    if (!this.walk.isCrossing) {
      if (this.rng.chance(dt * 0.01)) this.pause = this.rng.range(1, 4); // stop for a chat now and then
      else if (this.rng.chance(dt * 0.02)) this.walk.crossStreet();
    }
    const next = this.walk.advance(this.speed * dt);
    stepTo(this, next, dt, this.model);
  }
}

/** Move a walker to `next`, easing its height and animating the step. */
function stepTo(w: Walker, next: Vec2, dt: number, model: TownModel): void {
  const dx = next.x - w.x;
  const dy = next.y - w.y;
  w.x = next.x;
  w.y = next.y;
  const tile = index(Math.floor(w.x), Math.floor(w.y), model.width);
  w.z += ((model.tiles.elevation[tile] ?? 0) - w.z) * Math.min(1, dt * 10);
  w.animate(dx, dy);
  w.place();
}
